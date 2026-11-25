import { ArnFormat, Stack } from "aws-cdk-lib";
import * as iam from "aws-cdk-lib/aws-iam";
import type { ITable } from "aws-cdk-lib/aws-dynamodb";
import type {
  Ec2Provisioner,
  FargateProvisioner,
  RunnerControllerRuntime,
  RunnerProfile,
} from "../config/types.ts";
import { deploymentIdentity } from "./deployment-input.ts";

export interface RunnerCluster {
  readonly arn: string;
  readonly name: string;
}

type ProvisionerProfile<T> = RunnerProfile & { readonly provisioner: T };

export function ec2Profiles(profiles: readonly RunnerProfile[]) {
  return profiles.filter(
    (profile): profile is ProvisionerProfile<Ec2Provisioner> =>
      profile.provisioner.type === "ec2",
  );
}

export function fargateProfiles(profiles: readonly RunnerProfile[]) {
  return profiles.filter(
    (profile): profile is ProvisionerProfile<FargateProvisioner> =>
      profile.provisioner.type === "fargate",
  );
}

export function grantTableAccess(role: iam.Role, table: ITable) {
  allow(role, ["dynamodb:GetItem", "dynamodb:Query"], [table.tableArn]);
  allow(role, ["dynamodb:Query"], [`${table.tableArn}/index/due`]);
  allow(
    role,
    [
      "dynamodb:ConditionCheckItem",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:DeleteItem",
    ],
    [table.tableArn],
    { StringEquals: { "dynamodb:EnclosingOperation": "TransactWriteItems" } },
  );
}

export function grantHostFence(role: iam.Role, cluster: RunnerCluster) {
  allow(role, ["ecs:DescribeTasks"], [taskArn(role, cluster)], {
    ArnEquals: { "ecs:cluster": cluster.arn },
  });
}

export function grantGitHubCredentials(
  role: iam.Role,
  runtime: RunnerControllerRuntime,
  profiles: readonly RunnerProfile[],
) {
  const secretArns = [
    ...new Set(
      profiles.map(
        (profile) =>
          runtime.spec.github[profile.github.connection].auth.secretArn,
      ),
    ),
  ].sort();
  if (secretArns.length === 0) return;
  allow(role, ["secretsmanager:GetSecretValue"], secretArns, {
    StringEquals: { "secretsmanager:VersionStage": "AWSCURRENT" },
  });
  const stack = Stack.of(role);
  allow(
    role,
    ["kms:Decrypt"],
    [
      stack.formatArn({
        service: "kms",
        resource: "key",
        resourceName: "*",
        arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
      }),
    ],
    {
      StringEquals: {
        "kms:ViaService": `secretsmanager.${stack.region}.${stack.urlSuffix}`,
        "kms:EncryptionContext:SecretARN": secretArns,
      },
    },
  );
}

export function grantEc2(
  role: iam.Role,
  runtime: RunnerControllerRuntime,
  profiles: readonly ProvisionerProfile<Ec2Provisioner>[],
  active: boolean,
) {
  if (profiles.length === 0) return;
  regionalReads(role, [
    "ec2:DescribeInstances",
    "ec2:DescribeLaunchTemplateVersions",
    "ec2:DescribeImages",
    "ec2:DescribeInstanceTypes",
    "ec2:DescribeSubnets",
    "ec2:DescribeSecurityGroups",
    "ec2:DescribeRouteTables",
  ]);
  allow(
    role,
    ["iam:GetInstanceProfile"],
    unique(profiles.map(({ provisioner }) => provisioner.instanceProfileArn)),
  );
  allow(
    role,
    ["iam:GetRole"],
    unique(profiles.map(({ provisioner }) => provisioner.bootstrapRoleArn)),
  );
  if (!active) return;

  for (const profile of profiles) {
    const provisioner = profile.provisioner;
    const network = runtime.spec.networks[provisioner.network];
    const template = ec2Arn(
      role,
      "launch-template",
      provisioner.launchTemplateId,
    );
    const instance = ec2Arn(role, "instance", "*");
    const volume = ec2Arn(role, "volume", "*");
    const subnets = unique(
      network.subnets.map(({ id }) => ec2Arn(role, "subnet", id)),
    );
    const groups = unique(
      network.securityGroupIds.map((id) => ec2Arn(role, "security-group", id)),
    );
    const request = ownership(runtime, profile, "RequestTag");
    const owned = ownership(runtime, profile, "ResourceTag");
    const launchTemplate = { "ec2:LaunchTemplate": template };

    allow(role, ["ec2:RunInstances"], [template]);
    allow(role, ["ec2:RunInstances"], [...subnets, ...groups], {
      ArnEquals: launchTemplate,
    });
    allow(
      role,
      ["ec2:RunInstances"],
      [
        ec2Arn(role, "image", "ami-*", ""),
        ec2Arn(role, "snapshot", "snap-*", ""),
        ec2Arn(role, "key-pair", "*"),
      ],
      {
        ArnEquals: launchTemplate,
        Bool: { "ec2:IsLaunchTemplateResource": "true" },
      },
    );
    allow(
      role,
      ["ec2:RunInstances"],
      [ec2Arn(role, "network-interface", "*")],
      {
        ArnEquals: { ...launchTemplate, "ec2:Subnet": subnets },
        BoolIfExists: { "ec2:AssociatePublicIpAddress": "false" },
      },
    );
    allow(role, ["ec2:RunInstances"], [instance], {
      ArnEquals: {
        ...launchTemplate,
        "ec2:InstanceProfile": provisioner.instanceProfileArn,
      },
      StringEquals: request,
      Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
    });
    allow(role, ["ec2:RunInstances"], [volume], {
      ArnEquals: launchTemplate,
      StringEquals: request,
      Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
    });
    allow(role, ["ec2:CreateTags"], [instance, volume], {
      StringEquals: { ...request, "ec2:CreateAction": "RunInstances" },
      Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
    });
    allow(role, ["ec2:TerminateInstances"], [instance], {
      StringEquals: owned,
      Null: { "aws:ResourceTag/scaleset-aws:allocation": "false" },
    });
  }
  allow(
    role,
    ["iam:PassRole"],
    unique(profiles.map(({ provisioner }) => provisioner.bootstrapRoleArn)),
    {
      StringEquals: { "iam:PassedToService": "ec2.amazonaws.com" },
      ArnLike: {
        "iam:AssociatedResourceArn": Stack.of(role).formatArn({
          service: "ec2",
          region: "*",
          resource: "instance",
          resourceName: "*",
          arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
        }),
      },
    },
  );
}

export function grantFargate(
  role: iam.Role,
  runtime: RunnerControllerRuntime,
  profiles: readonly ProvisionerProfile<FargateProvisioner>[],
  clusters: ReadonlyMap<string, RunnerCluster>,
  active: boolean,
) {
  if (profiles.length === 0) return;
  regionalReads(role, [
    "ecs:DescribeTaskDefinition",
    "ecs:ListAccountSettings",
    "ec2:DescribeSubnets",
    "ec2:DescribeSecurityGroups",
  ]);
  const clusterArns = unique(
    profiles.map(({ provisioner }) => clusters.get(provisioner.cluster)!.arn),
  );
  allow(role, ["ecs:DescribeClusters"], clusterArns);
  allow(role, ["ecs:ListTasks"], ["*"], {
    ArnEquals: { "ecs:cluster": clusterArns },
  });
  allow(
    role,
    ["iam:GetRole"],
    unique(
      profiles.flatMap(({ provisioner }) => [
        provisioner.bootstrapRoleArn,
        provisioner.executionRoleArn,
      ]),
    ),
  );

  for (const profile of profiles) {
    const provisioner = profile.provisioner;
    const cluster = clusters.get(provisioner.cluster)!;
    const task = taskArn(role, cluster);
    const scoped = { "ecs:cluster": cluster.arn };
    allow(role, ["ecs:DescribeTasks"], [task], { ArnEquals: scoped });
    if (!active) continue;
    const request = ownership(runtime, profile, "RequestTag");
    const owned = ownership(runtime, profile, "ResourceTag");
    allow(role, ["ecs:RunTask"], [provisioner.taskDefinitionArn], {
      ArnEquals: scoped,
      StringEquals: request,
      Bool: { "ecs:enable-execute-command": "false" },
      Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
    });
    allow(role, ["ecs:TagResource"], [task], {
      StringEquals: { ...request, "ecs:CreateAction": "RunTask" },
      Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
    });
    allow(role, ["ecs:StopTask"], [task], {
      ArnEquals: scoped,
      StringEquals: owned,
      Null: { "aws:ResourceTag/scaleset-aws:allocation": "false" },
    });
  }
  if (active) {
    allow(
      role,
      ["iam:PassRole"],
      unique(
        profiles.flatMap(({ provisioner }) => [
          provisioner.bootstrapRoleArn,
          provisioner.executionRoleArn,
        ]),
      ),
      {
        StringEquals: { "iam:PassedToService": "ecs-tasks.amazonaws.com" },
      },
    );
  }
}

export function bootstrapPolicy(table: ITable) {
  return [
    bootstrapStatement(table, "dynamodb:DeleteItem", "OFFER#${aws:userid}"),
    new iam.PolicyStatement({
      effect: iam.Effect.DENY,
      actions: [
        "dynamodb:PutItem",
        "dynamodb:UpdateItem",
        "dynamodb:DeleteItem",
        "dynamodb:BatchWriteItem",
        "dynamodb:PartiQLInsert",
        "dynamodb:PartiQLUpdate",
        "dynamodb:PartiQLDelete",
      ],
      resources: [table.tableArn],
      conditions: {
        "ForAnyValue:StringLike": { "dynamodb:LeadingKeys": ["HELLO#*"] },
      },
    }),
  ];
}

export function ec2SupervisorStatusPolicy(table: ITable) {
  const stack = Stack.of(table);
  return new iam.PolicyStatement({
    actions: ["dynamodb:PutItem"],
    resources: [table.tableArn],
    conditions: {
      "ForAllValues:StringEquals": {
        "dynamodb:LeadingKeys": ["STATUS#${aws:userid}"],
        "dynamodb:Attributes": [
          "PK",
          "SK",
          "awsUserId",
          "resourceArn",
          "helloSha256",
          "offerId",
          "runnerId",
          "sequence",
          "observedAt",
          "exited",
          "payload",
          "qualificationSha256",
        ],
      },
      StringEqualsIfExists: {
        "dynamodb:ReturnValues": ["NONE", "ALL_OLD"],
      },
      ArnLike: {
        "ec2:SourceInstanceARN": stack.formatArn({
          service: "ec2",
          resource: "instance",
          resourceName: "*",
          arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
        }),
      },
      NumericEquals: { "ec2:RoleDelivery": "2.0" },
      Null: {
        "dynamodb:LeadingKeys": "false",
        "dynamodb:Attributes": "false",
        "dynamodb:EnclosingOperation": "true",
      },
    },
  });
}

function bootstrapStatement(table: ITable, action: string, leadingKey: string) {
  return new iam.PolicyStatement({
    actions: [action],
    resources: [table.tableArn],
    conditions: {
      "ForAllValues:StringEquals": { "dynamodb:LeadingKeys": [leadingKey] },
      Null: { "dynamodb:LeadingKeys": "false" },
    },
  });
}

function ownership(
  runtime: RunnerControllerRuntime,
  profile: RunnerProfile,
  kind: "RequestTag" | "ResourceTag",
) {
  return {
    [`aws:${kind}/scaleset-aws:controller`]: "scaleset-dotnet-aws",
    [`aws:${kind}/scaleset-aws:deployment`]: deploymentIdentity(runtime),
    [`aws:${kind}/scaleset-aws:profile`]: profile.id,
    [`aws:${kind}/scaleset-aws:bootstrap-protocol`]:
      profile.provisioner.bootstrapProtocol,
  };
}

function regionalReads(role: iam.Role, actions: string[]) {
  allow(role, actions, ["*"], {
    StringEquals: { "aws:RequestedRegion": Stack.of(role).region },
  });
}

function ec2Arn(
  role: iam.Role,
  resource: string,
  resourceName: string,
  account = Stack.of(role).account,
) {
  return Stack.of(role).formatArn({
    service: "ec2",
    account,
    resource,
    resourceName,
    arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
  });
}

function taskArn(role: iam.Role, cluster: RunnerCluster) {
  return Stack.of(role).formatArn({
    service: "ecs",
    resource: "task",
    resourceName: `${cluster.name}/*`,
    arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
  });
}

function allow(
  role: iam.Role,
  actions: string[],
  resources: string[],
  conditions?: iam.PolicyStatementProps["conditions"],
) {
  role.addToPolicy(new iam.PolicyStatement({ actions, resources, conditions }));
}

function unique(values: readonly string[]) {
  return [...new Set(values)].sort();
}
