import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { Template } from "aws-cdk-lib/assertions";
import { compileConfig } from "../../lib/config/compiler.ts";
import type {
  Ec2Provisioner,
  EcsClusterConfig,
  RunnerControllerRuntime,
  RunnerProfile,
} from "../../lib/config/types.ts";
import { synthesizeController } from "../../lib/infrastructure/controller-stack.ts";
import {
  ec2Profiles,
  fargateProfiles,
} from "../../lib/infrastructure/runner-iam.ts";

type Spec = RunnerControllerRuntime["spec"];
type ClusterKind = EcsClusterConfig["type"];
type RunnerPlacement = ClusterKind | "host";

interface Statement {
  readonly Effect: "Allow" | "Deny";
  readonly Action: string | string[];
  readonly Resource: unknown;
  readonly Condition?: Record<string, Record<string, unknown>>;
}

const fixture = compileConfig(
  readFileSync(
    new URL("../../../testdata/config/mixed-ec2-fargate.yaml", import.meta.url),
    "utf8",
  ),
);
const base = fixture.runtime.spec;
const { account, region } = base.aws;
const ecsArn = `arn:aws:ecs:${region}:${account}:`;
const [alias, pinned] = ec2Profiles(base.profiles);
const fargate = fargateProfiles(base.profiles);
const defaultAlias: RunnerProfile & { provisioner: Ec2Provisioner } = {
  ...alias,
  provisioner: {
    ...alias.provisioner,
    launchTemplateVersion: { type: "alias", name: "$Default" },
  },
};
const layouts = {
  "ec2-pinned": [pinned],
  "ec2-latest": [alias],
  "ec2-default": [defaultAlias],
  fargate,
  "fargate-single": [fargate[0]],
  "mixed-pinned-single": [pinned, fargate[0]],
  "mixed-latest-single": [alias, fargate[0]],
  "mixed-default-single": [defaultAlias, fargate[0]],
  "mixed-pinned": [pinned, ...fargate],
  "mixed-latest": [alias, ...fargate],
  "mixed-default": [defaultAlias, ...fargate],
  "mixed-two-of-each": base.profiles,
};
const modes = [
  { mode: "active", inspectAws: false },
  { mode: "dry-run", inspectAws: false },
  { mode: "dry-run", inspectAws: true },
] as const;
const clusterKinds = ["create", "import"] as const;
const runnerPlacements = ["host", ...clusterKinds] as const;
const disabledConnection = "matrix-disabled";
const disabledTemplate = "lt-0123456789abcdef9";
const disabledTask = `${ecsArn}task-definition/matrix-disabled:2`;
const disabledRole = `arn:aws:iam::${account}:role/matrix-disabled`;
const disabledSecret = `arn:aws:secretsmanager:${region}:${account}:secret:matrix-disabled-a1b2c3`;

function matrixCluster(alias: string, kind: ClusterKind): EcsClusterConfig {
  const name = `matrix-${alias}`;
  return kind === "create"
    ? { type: "create", name }
    : { type: "import", arn: `${ecsArn}cluster/${name}` };
}

function* placements(count: number): Generator<RunnerPlacement[]> {
  if (count === 0) {
    yield [];
    return;
  }
  for (const first of runnerPlacements) {
    for (const rest of placements(count - 1)) yield [first, ...rest];
  }
}

function compileRow(
  profiles: readonly RunnerProfile[],
  host: ClusterKind,
  runners: readonly RunnerPlacement[],
  table: Spec["tracking"]["table"],
  mode: (typeof modes)[number],
  includeDisabled: boolean,
) {
  const hostAlias = base.controller.hosting.cluster;
  const ecsClusters: Record<string, EcsClusterConfig> = {
    [hostAlias]: matrixCluster(hostAlias, host),
  };
  let fargateIndex = 0;
  const configured = profiles.map((profile): RunnerProfile => {
    const provisioner = profile.provisioner;
    if (provisioner.type === "ec2") return profile;
    const placement = runners[fargateIndex++];
    if (placement !== "host") {
      ecsClusters[provisioner.cluster] = matrixCluster(
        provisioner.cluster,
        placement,
      );
    }
    return {
      ...profile,
      provisioner: {
        ...provisioner,
        cluster: placement === "host" ? hostAlias : provisioner.cluster,
      },
    };
  });
  if (includeDisabled) {
    ecsClusters[disabledConnection] = matrixCluster(
      disabledConnection,
      "create",
    );
    for (const [source, provisioner] of [
      [pinned, { ...pinned.provisioner, launchTemplateId: disabledTemplate }],
      [
        fargate[0],
        {
          ...fargate[0].provisioner,
          cluster: disabledConnection,
          taskDefinitionArn: disabledTask,
          executionRoleArn: `${disabledRole}-execution`,
        },
      ],
    ] as const) {
      const id = `matrix-disabled-${provisioner.type}`;
      configured.push({
        ...source,
        id,
        state: "disabled",
        github: {
          connection: disabledConnection,
          runnerGroup: "MatrixDisabled",
          scaleSet: id,
          labels: [id],
          registration: "existing",
        },
        provisioner: {
          ...provisioner,
          bootstrapRoleArn: `${disabledRole}-${provisioner.type}`,
          ...(provisioner.type === "ec2" && {
            instanceProfileArn: `arn:aws:iam::${account}:instance-profile/matrix-disabled`,
          }),
        },
      });
    }
  }
  const spec: Spec = {
    ...base,
    mode: mode.mode,
    dryRun: { demand: { type: "none" }, inspectAws: mode.inspectAws },
    tracking: { table },
    ecsClusters,
    github: includeDisabled
      ? {
          ...base.github,
          [disabledConnection]: {
            scopeURL: "https://github.com/matrix-disabled",
            auth: { type: "github-app", secretArn: disabledSecret },
          },
        }
      : base.github,
    profiles: configured,
  };
  // JSON document syntax is also valid YAML and exercises the same public configuration compiler.
  return compileConfig(
    JSON.stringify({ ...fixture.runtime, kind: "RunnerController", spec }),
  );
}

function hasAction(statement: Statement, action: string): boolean {
  return [statement.Action].flat().includes(action);
}

function canonical(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonical);
  if (typeof value !== "object" || value === null) return value;
  const record = value as Record<string, unknown>;
  if (record.Ref === "AWS::Partition") return "aws";
  if (record.Ref === "AWS::URLSuffix") return "amazonaws.com";
  if (Array.isArray(record["Fn::Join"])) {
    const [separator, values] = record["Fn::Join"] as [string, unknown[]];
    const rendered = values.map(canonical);
    if (rendered.every((part) => typeof part === "string"))
      return rendered.join(separator);
  }
  return Object.fromEntries(
    Object.keys(record)
      .sort()
      .map((key) => {
        const field = canonical(record[key]);
        return [
          key,
          (key === "ecs:cluster" || key === "ec2:Subnet") &&
          Array.isArray(field)
            ? field.sort(arnOrder)
            : field,
        ];
      }),
  );
}

function sortedJson(values: readonly unknown[]): string[] {
  return [
    ...new Set(values.map((value) => JSON.stringify(canonical(value)))),
  ].sort();
}

function policyResources(template: Template) {
  return Object.values({
    ...template.findResources("AWS::IAM::Policy"),
    ...template.findResources("AWS::IAM::ManagedPolicy"),
  });
}

function policyForRole(template: Template, roleId: string): Statement[] {
  const role = template.findResources("AWS::IAM::Role")[roleId];
  assert.ok(role);
  assert.equal(
    role.Properties.ManagedPolicyArns,
    undefined,
    "unexpected external managed role policy",
  );
  const attached = policyResources(template).filter((policy) =>
    ((policy.Properties.Roles ?? []) as unknown[]).some(
      (item) => JSON.stringify(item) === JSON.stringify({ Ref: roleId }),
    ),
  );
  return [
    ...attached.flatMap(
      (policy) => policy.Properties.PolicyDocument.Statement as Statement[],
    ),
    ...(
      (role.Properties.Policies ?? []) as {
        PolicyDocument: { Statement: Statement[] };
      }[]
    ).flatMap((policy) => policy.PolicyDocument.Statement),
  ];
}

interface Scope {
  readonly resource: unknown;
  readonly condition?: Statement["Condition"];
}

function verifyGrants(
  statements: Statement[],
  action: string,
  expected: Scope[],
) {
  const actual = statements
    .filter((statement) => hasAction(statement, action))
    .flatMap((statement) => {
      assert.equal(statement.Effect, "Allow");
      return [statement.Resource]
        .flat()
        .map((resource) => ({ resource, condition: statement.Condition }));
    });
  assert.deepEqual(
    sortedJson(actual),
    sortedJson(expected),
    `${action} has different IAM scope`,
  );
}

function scope(
  resources: readonly unknown[],
  condition?: Statement["Condition"],
): Scope[] {
  return resources.map((resource) => ({ resource, condition }));
}

function clusterName(cluster: EcsClusterConfig): string {
  return cluster.type === "create"
    ? cluster.name
    : cluster.arn.split("/").at(-1)!;
}

function profileTags(
  profile: RunnerProfile,
  kind: "RequestTag" | "ResourceTag",
) {
  return {
    [`aws:${kind}/scaleset-aws:controller`]: "scaleset-dotnet-aws",
    [`aws:${kind}/scaleset-aws:deployment`]: `${account}:${region}:${fixture.runtime.metadata.name}`,
    [`aws:${kind}/scaleset-aws:profile`]: profile.id,
    [`aws:${kind}/scaleset-aws:bootstrap-protocol`]:
      profile.provisioner.bootstrapProtocol,
  };
}

const requireAllocation = (kind: "RequestTag" | "ResourceTag") => ({
  [`aws:${kind}/scaleset-aws:allocation`]: "false",
});

function clusterArn(template: Template, cluster: EcsClusterConfig) {
  if (cluster.type === "import") return cluster.arn;
  const id = template.getResourceId("AWS::ECS::Cluster", {
    Properties: { ClusterName: cluster.name },
  });
  return { "Fn::GetAtt": [id, "Arn"] };
}

function verifyTemplate(
  compiled: ReturnType<typeof compileConfig>,
  template: Template,
) {
  const { spec } = compiled.runtime;
  const active = spec.mode === "active";
  const selected =
    active || (spec.dryRun.inspectAws && spec.dryRun.demand.type === "none")
      ? spec.profiles.filter((profile) => profile.state !== "disabled")
      : [];
  const ec2 = ec2Profiles(selected);
  const fgt = fargateProfiles(selected);
  const task = template.getResourceId("AWS::ECS::TaskDefinition");
  const [taskResource] = Object.values(
    template.findResources("AWS::ECS::TaskDefinition"),
  );
  const roleArn = taskResource.Properties.TaskRoleArn as {
    "Fn::GetAtt": [string, string];
  };
  const taskRole = roleArn["Fn::GetAtt"][0];
  assert.match(taskRole, /^ControllerTaskRole/u);
  const executionRoleArn = taskResource.Properties
    .ExecutionRoleArn as typeof roleArn;
  const executionRole = executionRoleArn["Fn::GetAtt"][0];
  assert.match(executionRole, /^ControllerExecutionRole/u);
  assert.notEqual(taskRole, executionRole);
  verifyExecutionRole(policyForRole(template, executionRole), spec, template);
  assert.equal(taskResource.Properties.ContainerDefinitions.length, 1);
  const [container] = taskResource.Properties.ContainerDefinitions as {
    Command: unknown[];
    ReadonlyRootFilesystem: boolean;
    StopTimeout: number;
  }[];
  assert.equal(container.ReadonlyRootFilesystem, true);
  assert.equal(container.StopTimeout, 90);

  const hostAlias = spec.controller.hosting.cluster;
  const hostArn = clusterArn(template, spec.ecsClusters[hostAlias]);
  template.resourceCountIs("AWS::ECS::Service", 1);
  template.hasResourceProperties("AWS::ECS::Service", {
    Cluster: hostArn,
    TaskDefinition: { Ref: task },
    DesiredCount: 1,
    EnableExecuteCommand: false,
    LaunchType: "FARGATE",
    DeploymentConfiguration: {
      MinimumHealthyPercent: 0,
      MaximumPercent: 100,
      DeploymentCircuitBreaker: { Enable: true, Rollback: true },
    },
    NetworkConfiguration: {
      AwsvpcConfiguration: {
        AssignPublicIp: "DISABLED",
        Subnets: spec.networks[spec.controller.hosting.network].subnets
          .map(({ id }) => id)
          .toSorted(),
        SecurityGroups:
          spec.networks[
            spec.controller.hosting.network
          ].securityGroupIds.toSorted(),
      },
    },
  });
  const used = new Set([
    hostAlias,
    ...fgt.map((profile) => profile.provisioner.cluster),
  ]);
  const clusterNames = [...used].flatMap((alias) => {
    const cluster = spec.ecsClusters[alias];
    return cluster.type === "create" ? [cluster.name] : [];
  });
  assert.deepEqual(
    Object.values(template.findResources("AWS::ECS::Cluster"))
      .map((cluster) => String(cluster.Properties.ClusterName))
      .sort(),
    clusterNames.sort(),
  );
  for (const kind of [
    "VPC",
    "Subnet",
    "SecurityGroup",
    "SecurityGroupIngress",
    "SecurityGroupEgress",
    "LaunchTemplate",
  ]) {
    template.resourceCountIs(`AWS::EC2::${kind}`, 0);
  }
  if (spec.tracking.table.type === "create") {
    template.resourceCountIs("AWS::DynamoDB::Table", 1);
    template.hasResource("AWS::DynamoDB::Table", {
      DeletionPolicy: "Retain",
      UpdateReplacePolicy: "Retain",
      Properties: {
        SSESpecification: { SSEEnabled: true },
        PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
        TimeToLiveSpecification: { AttributeName: "expiresAt", Enabled: true },
      },
    });
  } else {
    template.resourceCountIs("AWS::DynamoDB::Table", 0);
  }

  const outputs = template.findOutputs("*");
  assert.equal(outputs.RuntimeConfigSha256.Value, compiled.sha256);
  const resourceTypes = Object.values(
    (template.toJSON() as { Resources: Record<string, { Type: string }> })
      .Resources,
  ).map((entry) => entry.Type);
  const custom = resourceTypes.filter(
    (type) =>
      type.startsWith("Custom::") ||
      type === "AWS::CloudFormation::CustomResource",
  );
  assert.deepEqual(custom, ["Custom::ScaleSetRuntimeArtifact"]);
  template.hasResourceProperties("Custom::ScaleSetRuntimeArtifact", {
    Account: account,
    DestinationKey: `runtime/${compiled.sha256}.json`,
  });
  const queues = verifyEventRoutes(template, spec, active, ec2.length > 0, fgt);
  assert.deepEqual(container.Command, [
    "run",
    "--config-s3-uri",
    outputs.RuntimeConfigUri.Value,
    "--config-version-id",
    outputs.RuntimeConfigVersionId.Value,
    "--sha256",
    compiled.sha256,
    "--generation",
    "7",
    "--health-addr",
    "127.0.0.1:8080",
    ...queues.flags,
  ]);

  verifyTaskRole(
    policyForRole(template, taskRole),
    spec,
    template,
    ec2,
    fgt,
    queues.arns,
  );
  verifyBootstrapPolicies(
    policyResources(template),
    tableArn(template, spec),
    active ? selected : [],
  );

  const cloudFormation = JSON.stringify(template.toJSON());
  for (const disabled of [
    disabledTemplate,
    disabledTask,
    disabledRole,
    disabledSecret,
    "matrix-matrix-disabled",
  ]) {
    assert.ok(
      !cloudFormation.includes(disabled),
      `disabled resource ${disabled}`,
    );
  }
}

function verifyEventRoutes(
  template: Template,
  spec: Spec,
  active: boolean,
  ec2: boolean,
  fgt: ReturnType<typeof fargateProfiles>,
) {
  const queues = template.findResources("AWS::SQS::Queue");
  const queuePolicies = template.findResources("AWS::SQS::QueuePolicy");
  const keys = template.findResources("AWS::KMS::Key");
  const rules = template.findResources("AWS::Events::Rule");
  const flags: unknown[] = [];
  const arns: unknown[] = [];
  const expected: unknown[] = [];
  for (const [prefix, source, detailType, flag, details] of [
    [
      "Ec2",
      "aws.ec2",
      "EC2 Instance State-change Notification",
      "--ec2-event-queue-url",
      active && ec2 ? [{ "instance-id": [{ prefix: "i-" }] }] : [],
    ],
    [
      "Fargate",
      "aws.ecs",
      "ECS Task State Change",
      "--ecs-event-queue-url",
      active
        ? fgt.map(({ provisioner }) => ({
            clusterArn: [
              clusterArn(template, spec.ecsClusters[provisioner.cluster]),
            ],
            taskDefinitionArn: [provisioner.taskDefinitionArn],
          }))
        : [],
    ],
  ] as const) {
    if (details.length === 0) continue;
    const id = Object.keys(queues).find((queue) =>
      queue.startsWith(`${prefix}EventQueue`),
    );
    const dlq = Object.keys(queues).find((queue) =>
      queue.startsWith(`${prefix}EventDeadLetters`),
    );
    const key = Object.keys(keys).find((resource) =>
      resource.startsWith(`${prefix}EventKey`),
    );
    assert.ok(id && dlq && key);
    const keyArn = { "Fn::GetAtt": [key, "Arn"] };
    assert.deepEqual(queues[id].Properties.KmsMasterKeyId, keyArn);
    assert.deepEqual(queues[dlq].Properties.KmsMasterKeyId, keyArn);
    assert.equal(queues[dlq].Properties.MessageRetentionPeriod, 1_209_600);
    assert.equal(keys[key].Properties.EnableKeyRotation, true);
    verifyObservationKeyPolicy(keys[key].Properties.KeyPolicy.Statement);
    const ruleArns = Object.keys(rules)
      .filter((rule) => rule.startsWith(`${prefix}StateChange`))
      .map((rule) => ({ "Fn::GetAtt": [rule, "Arn"] }));
    assert.equal(ruleArns.length, details.length);
    for (const destination of [id, dlq]) {
      const policies = Object.values(queuePolicies).filter((policy) =>
        (policy.Properties.Queues as { Ref: string }[]).some(
          ({ Ref: queue }) => queue === destination,
        ),
      );
      assert.equal(policies.length, 1);
      assert.deepEqual(policies[0].Properties.Queues, [{ Ref: destination }]);
      verifyObservationQueuePolicy(
        policies[0].Properties.PolicyDocument.Statement,
        { "Fn::GetAtt": [destination, "Arn"] },
        ruleArns,
      );
    }
    const queueArn = { "Fn::GetAtt": [id, "Arn"] };
    const dlqArn = { "Fn::GetAtt": [dlq, "Arn"] };
    assert.deepEqual(queues[id].Properties.RedrivePolicy, {
      deadLetterTargetArn: dlqArn,
      maxReceiveCount: 5,
    });
    flags.push(flag, { Ref: id });
    arns.push(queueArn);
    for (const detail of details) {
      expected.push({
        EventPattern: {
          source: [source],
          "detail-type": [detailType],
          detail,
          account: [account],
          region: [region],
        },
        State: "ENABLED",
        Targets: [
          { Arn: queueArn, DeadLetterConfig: { Arn: dlqArn }, Id: "Target0" },
        ],
      });
    }
  }
  assert.equal(Object.keys(queues).length, arns.length * 2);
  assert.equal(Object.keys(queuePolicies).length, arns.length * 2);
  assert.equal(Object.keys(keys).length, arns.length);
  const actual = Object.values(rules).map((rule) => rule.Properties);
  assert.equal(actual.length, expected.length);
  assert.deepEqual(sortedJson(actual), sortedJson(expected));
  return { flags, arns };
}

function verifyObservationKeyPolicy(statements: readonly unknown[]) {
  const expected = [
    {
      Action: "kms:*",
      Effect: "Allow",
      Principal: { AWS: `arn:aws:iam::${account}:root` },
      Resource: "*",
    },
    {
      Action: ["kms:Decrypt", "kms:GenerateDataKey"],
      Condition: { StringEquals: { "aws:SourceAccount": account } },
      Effect: "Allow",
      Principal: { Service: "events.amazonaws.com" },
      Resource: "*",
    },
  ];
  assert.equal(statements.length, expected.length);
  assert.deepEqual(sortedJson(statements), sortedJson(expected));
}

function verifyObservationQueuePolicy(
  statements: readonly unknown[],
  queueArn: unknown,
  ruleArns: readonly unknown[],
) {
  const expected = [
    {
      Action: "sqs:*",
      Condition: { Bool: { "aws:SecureTransport": "false" } },
      Effect: "Deny",
      Principal: { AWS: "*" },
      Resource: queueArn,
    },
    ...ruleArns.map((ruleArn) => ({
      Action: "sqs:SendMessage",
      Condition: { ArnEquals: { "aws:SourceArn": ruleArn } },
      Effect: "Allow",
      Principal: { Service: "events.amazonaws.com" },
      Resource: queueArn,
    })),
  ];
  assert.equal(statements.length, expected.length);
  assert.deepEqual(sortedJson(statements), sortedJson(expected));
}

function tableArn(template: Template, spec: Spec) {
  return spec.tracking.table.type === "import"
    ? `arn:aws:dynamodb:${region}:${account}:table/${spec.tracking.table.name}`
    : { "Fn::GetAtt": [template.getResourceId("AWS::DynamoDB::Table"), "Arn"] };
}

function verifyTaskRole(
  statements: Statement[],
  spec: Spec,
  template: Template,
  ec2: ReturnType<typeof ec2Profiles>,
  fgt: ReturnType<typeof fargateProfiles>,
  queueArns: unknown[],
) {
  const active = spec.mode === "active";
  const expected = new Set(["s3:GetObjectVersion"]);
  const add = (...actions: string[]) =>
    actions.forEach((action) => expected.add(action));
  if (active) add("dynamodb:GetItem", "dynamodb:Query");
  if (ec2.length > 0)
    add(
      "ec2:DescribeInstances",
      "ec2:DescribeLaunchTemplateVersions",
      "ec2:DescribeImages",
      "ec2:DescribeInstanceTypes",
      "ec2:DescribeSubnets",
      "ec2:DescribeSecurityGroups",
      "ec2:DescribeRouteTables",
      "iam:GetInstanceProfile",
      "iam:GetRole",
    );
  if (fgt.length > 0)
    add(
      "ecs:DescribeClusters",
      "ecs:DescribeTasks",
      "ecs:DescribeTaskDefinition",
      "ecs:ListAccountSettings",
      "ecs:ListTasks",
      "ec2:DescribeSubnets",
      "ec2:DescribeSecurityGroups",
      "iam:GetRole",
    );
  if (active) {
    add(
      "dynamodb:ConditionCheckItem",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:DeleteItem",
      "ecs:DescribeTasks",
      "iam:PassRole",
      "secretsmanager:GetSecretValue",
      "kms:Decrypt",
      "sqs:ReceiveMessage",
      "sqs:DeleteMessage",
    );
    if (ec2.length > 0)
      add("ec2:RunInstances", "ec2:TerminateInstances", "ec2:CreateTags");
    if (fgt.length > 0) add("ecs:RunTask", "ecs:StopTask", "ecs:TagResource");
  }
  assert.deepEqual(
    new Set(statements.flatMap(({ Action }) => Action)),
    expected,
  );
  for (const action of ["sqs:ReceiveMessage", "sqs:DeleteMessage"]) {
    verifyGrants(statements, action, scope(queueArns));
  }

  const host = spec.controller.hosting.cluster;
  const runnerClusters = [
    ...new Set(fgt.map(({ provisioner }) => provisioner.cluster)),
  ];
  const runnerArns = runnerClusters.map((alias) =>
    clusterArn(template, spec.ecsClusters[alias]),
  );
  verifyGrants(statements, "ecs:DescribeClusters", scope(runnerArns));
  verifyGrants(
    statements,
    "ecs:ListTasks",
    runnerArns.length === 0
      ? []
      : scope(["*"], {
          ArnEquals: { "ecs:cluster": runnerArns },
        }),
  );
  verifyGrants(
    statements,
    "ecs:DescribeTasks",
    [...new Set(active ? [host, ...runnerClusters] : runnerClusters)].flatMap(
      (alias) => {
        const cluster = spec.ecsClusters[alias];
        return scope([`${ecsArn}task/${clusterName(cluster)}/*`], {
          ArnEquals: { "ecs:cluster": clusterArn(template, cluster) },
        });
      },
    ),
  );
  verifyGrants(
    statements,
    "iam:GetInstanceProfile",
    scope(ec2.map(({ provisioner }) => provisioner.instanceProfileArn)),
  );
  const ec2Roles = ec2.map(({ provisioner }) => provisioner.bootstrapRoleArn);
  const fargateRoles = fgt.flatMap(({ provisioner }) => [
    provisioner.bootstrapRoleArn,
    provisioner.executionRoleArn,
  ]);
  verifyGrants(
    statements,
    "iam:GetRole",
    scope([...ec2Roles, ...fargateRoles]),
  );
  const table = tableArn(template, spec);
  const due = { "Fn::Join": ["", [table, "/index/due"]] };
  verifyGrants(statements, "dynamodb:GetItem", active ? scope([table]) : []);
  verifyGrants(statements, "dynamodb:Query", active ? scope([table, due]) : []);
  for (const action of expected) {
    if (
      action.startsWith("ec2:Describe") ||
      action === "ecs:DescribeTaskDefinition" ||
      action === "ecs:ListAccountSettings"
    ) {
      verifyGrants(
        statements,
        action,
        scope(["*"], { StringEquals: { "aws:RequestedRegion": region } }),
      );
    }
  }
  if (!active) return;

  verifyGrants(statements, "iam:PassRole", [
    ...scope(ec2Roles, {
      StringEquals: { "iam:PassedToService": "ec2.amazonaws.com" },
      ArnLike: {
        "iam:AssociatedResourceArn": `arn:aws:ec2:*:${account}:instance/*`,
      },
    }),
    ...scope(fargateRoles, {
      StringEquals: { "iam:PassedToService": "ecs-tasks.amazonaws.com" },
    }),
  ]);
  const secretArns = [...ec2, ...fgt].map(
    (profile) => spec.github[profile.github.connection].auth.secretArn,
  );
  verifyGrants(
    statements,
    "secretsmanager:GetSecretValue",
    scope(secretArns, {
      StringEquals: { "secretsmanager:VersionStage": "AWSCURRENT" },
    }),
  );
  for (const action of [
    "dynamodb:ConditionCheckItem",
    "dynamodb:PutItem",
    "dynamodb:UpdateItem",
    "dynamodb:DeleteItem",
  ]) {
    verifyGrants(
      statements,
      action,
      scope([table], {
        StringEquals: { "dynamodb:EnclosingOperation": "TransactWriteItems" },
      }),
    );
  }
  verifyEc2Writes(statements, spec, ec2);
  verifyFargateWrites(statements, spec, template, fgt);
}

function arnOrder(left: unknown, right: unknown): number {
  return JSON.stringify(canonical(left)).localeCompare(
    JSON.stringify(canonical(right)),
  );
}

function verifyEc2Writes(
  statements: Statement[],
  spec: Spec,
  profiles: ReturnType<typeof ec2Profiles>,
) {
  const plans: Scope[] = [];
  const retire: Scope[] = [];
  const tag: Scope[] = [];
  const baseArn = `arn:aws:ec2:${region}:${account}:`;
  const instance = `${baseArn}instance/*`;
  const volume = `${baseArn}volume/*`;
  for (const profile of profiles) {
    const { provisioner } = profile;
    const network = spec.networks[provisioner.network];
    const template = `${baseArn}launch-template/${provisioner.launchTemplateId}`;
    const subnets = network.subnets
      .map(({ id }) => `${baseArn}subnet/${id}`)
      .sort();
    const groups = network.securityGroupIds
      .map((id) => `${baseArn}security-group/${id}`)
      .sort();
    const exact = { "ec2:LaunchTemplate": template };
    const request = profileTags(profile, "RequestTag");
    const exit = profileTags(profile, "ResourceTag");
    plans.push(...scope([template]));
    plans.push(...scope([...subnets, ...groups], { ArnEquals: exact }));
    plans.push(
      ...scope(
        [
          `arn:aws:ec2:${region}::image/ami-*`,
          `arn:aws:ec2:${region}::snapshot/snap-*`,
          `${baseArn}key-pair/*`,
        ],
        { ArnEquals: exact, Bool: { "ec2:IsLaunchTemplateResource": "true" } },
      ),
    );
    plans.push(
      ...scope([`${baseArn}network-interface/*`], {
        ArnEquals: { ...exact, "ec2:Subnet": subnets },
        BoolIfExists: { "ec2:AssociatePublicIpAddress": "false" },
      }),
    );
    plans.push(
      ...scope([instance], {
        ArnEquals: {
          ...exact,
          "ec2:InstanceProfile": provisioner.instanceProfileArn,
        },
        StringEquals: request,
        Null: requireAllocation("RequestTag"),
      }),
    );
    plans.push(
      ...scope([volume], {
        ArnEquals: exact,
        StringEquals: request,
        Null: requireAllocation("RequestTag"),
      }),
    );
    tag.push(
      ...scope([instance, volume], {
        StringEquals: { ...request, "ec2:CreateAction": "RunInstances" },
        Null: requireAllocation("RequestTag"),
      }),
    );
    retire.push(
      ...scope([instance], {
        StringEquals: exit,
        Null: requireAllocation("ResourceTag"),
      }),
    );
  }
  verifyGrants(statements, "ec2:RunInstances", plans);
  verifyGrants(statements, "ec2:CreateTags", tag);
  verifyGrants(statements, "ec2:TerminateInstances", retire);
}

function verifyFargateWrites(
  statements: Statement[],
  spec: Spec,
  template: Template,
  profiles: ReturnType<typeof fargateProfiles>,
) {
  const plans: Scope[] = [];
  const retire: Scope[] = [];
  const tag: Scope[] = [];
  for (const profile of profiles) {
    const { provisioner } = profile;
    const cluster = spec.ecsClusters[provisioner.cluster];
    const task = `${ecsArn}task/${clusterName(cluster)}/*`;
    const clusterScope = { "ecs:cluster": clusterArn(template, cluster) };
    const request = profileTags(profile, "RequestTag");
    const exit = profileTags(profile, "ResourceTag");
    plans.push(
      ...scope([provisioner.taskDefinitionArn], {
        ArnEquals: clusterScope,
        StringEquals: request,
        Bool: { "ecs:enable-execute-command": "false" },
        Null: requireAllocation("RequestTag"),
      }),
    );
    tag.push(
      ...scope([task], {
        StringEquals: { ...request, "ecs:CreateAction": "RunTask" },
        Null: requireAllocation("RequestTag"),
      }),
    );
    retire.push(
      ...scope([task], {
        ArnEquals: clusterScope,
        StringEquals: exit,
        Null: requireAllocation("ResourceTag"),
      }),
    );
  }
  verifyGrants(statements, "ecs:RunTask", plans);
  verifyGrants(statements, "ecs:TagResource", tag);
  verifyGrants(statements, "ecs:StopTask", retire);
}

function verifyExecutionRole(
  statements: Statement[],
  spec: Spec,
  template: Template,
) {
  const pull = [
    "ecr:BatchCheckLayerAvailability",
    "ecr:BatchGetImage",
    "ecr:GetDownloadUrlForLayer",
  ];
  const logging = ["logs:CreateLogStream", "logs:PutLogEvents"];
  assert.deepEqual(
    new Set(statements.flatMap(({ Action }) => Action)),
    new Set(["ecr:GetAuthorizationToken", ...pull, ...logging]),
  );
  verifyGrants(statements, "ecr:GetAuthorizationToken", scope(["*"]));
  const location = spec.controller.image.split("@", 1)[0];
  const repository = location.slice(location.indexOf("/") + 1);
  for (const action of pull) {
    verifyGrants(
      statements,
      action,
      scope([`arn:aws:ecr:${region}:${account}:repository/${repository}`]),
    );
  }
  const group = template.getResourceId("AWS::Logs::LogGroup");
  for (const action of logging) {
    verifyGrants(statements, action, scope([{ "Fn::GetAtt": [group, "Arn"] }]));
  }
}

function verifyBootstrapPolicies(
  policies: ReturnType<typeof policyResources>,
  table: unknown,
  selected: readonly RunnerProfile[],
) {
  const roles = [
    ...new Set(
      selected.map(({ provisioner }) =>
        provisioner.bootstrapRoleArn.split("/").at(-1),
      ),
    ),
  ].sort();
  const bootstrap = policies.filter(
    (policy) =>
      ((policy.Properties.Roles ?? []) as unknown[]).length > 0 &&
      (policy.Properties.Roles as unknown[]).every(
        (role) => typeof role === "string",
      ),
  );
  assert.deepEqual(
    bootstrap.flatMap((policy) => policy.Properties.Roles as string[]).sort(),
    roles,
  );
  for (const policy of bootstrap) {
    const statements = policy.Properties.PolicyDocument
      .Statement as Statement[];
    const allowed = statements.filter(({ Effect }) => Effect === "Allow");
    assert.deepEqual(
      new Set(allowed.flatMap(({ Action }) => Action)),
      new Set([
        "dynamodb:DeleteItem",
        "lambda:InvokeFunctionUrl",
        "lambda:InvokeFunction",
      ]),
    );
    verifyGrants(
      allowed,
      "dynamodb:DeleteItem",
      scope([table], {
        "ForAllValues:StringEquals": {
          "dynamodb:LeadingKeys": ["OFFER#${aws:userid}"],
        },
        Null: { "dynamodb:LeadingKeys": "false" },
      }),
    );
    const deny = statements.find(
      ({ Effect, Action }) =>
        Effect === "Deny" && Action.includes("dynamodb:PutItem"),
    );
    assert.ok(deny);
    assert.deepEqual(deny.Condition, {
      "ForAnyValue:StringLike": { "dynamodb:LeadingKeys": ["HELLO#*"] },
    });
  }
}

test("offline deployment synthesis matrix preserves capability and import boundaries", (context) => {
  const counts: Record<string, number> = {};
  for (const [name, profiles] of Object.entries(layouts)) {
    counts[name] = 0;
    for (const host of clusterKinds) {
      for (const runners of placements(fargateProfiles(profiles).length)) {
        for (const table of [
          { type: "create" },
          { type: "import", name: "matrix-preexisting-table" },
        ] as const) {
          for (const mode of modes) {
            for (const disabled of [false, true]) {
              const row = `${name}/${host}/${runners.join("+")}/${table.type}/${mode.mode}/${mode.inspectAws}/${disabled}`;
              const directory = mkdtempSync(join(tmpdir(), "scaleset-matrix-"));
              try {
                const compiled = compileRow(
                  profiles,
                  host,
                  runners,
                  table,
                  mode,
                  disabled,
                );
                const synthesized = synthesizeController(
                  compiled,
                  "7",
                  directory,
                );
                const asset = readdirSync(directory).find((file) =>
                  /^asset\..+\.json$/u.test(file),
                );
                assert.ok(asset);
                assert.equal(
                  readFileSync(join(directory, asset), "utf8"),
                  compiled.json,
                );
                verifyTemplate(
                  compiled,
                  Template.fromJSON(synthesized.template),
                );
              } catch (cause) {
                throw new Error(`Synthesis matrix row failed ${row}`, {
                  cause,
                });
              } finally {
                rmSync(directory, { recursive: true, force: true });
              }
              counts[name]++;
            }
          }
        }
      }
    }
  }
  assert.deepEqual(counts, {
    "ec2-pinned": 24,
    "ec2-latest": 24,
    "ec2-default": 24,
    fargate: 216,
    "fargate-single": 72,
    "mixed-pinned-single": 72,
    "mixed-latest-single": 72,
    "mixed-default-single": 72,
    "mixed-pinned": 216,
    "mixed-latest": 216,
    "mixed-default": 216,
    "mixed-two-of-each": 216,
  });
  const total = Object.values(counts).reduce((sum, count) => sum + count, 0);
  context.diagnostic(
    `Verified ${total} synthesized templates - ${Object.entries(counts)
      .map(([name, count]) => `${name} ${count}`)
      .join(", ")}`,
  );
  context.diagnostic(
    "Coverage includes created and imported tables and clusters, shared and separate placement, three controller modes, disabled profiles, exact runner and host IAM, event rules, and read-only imports.",
  );
});
