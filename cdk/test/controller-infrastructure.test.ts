import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { Match, Template } from "aws-cdk-lib/assertions";
import { compileConfig } from "../lib/config/compiler.ts";
import type { RunnerControllerRuntime } from "../lib/config/types.ts";
import { synthesizeController } from "../lib/infrastructure/controller-stack.ts";

function fixture(name: string) {
  return compileConfig(
    readFileSync(
      new URL(`../../testdata/config/${name}.yaml`, import.meta.url),
      "utf8",
    ),
  );
}

function rewrite(
  compiled: ReturnType<typeof fixture>,
  spec: RunnerControllerRuntime["spec"],
) {
  return compileConfig(
    JSON.stringify({ ...compiled.runtime, kind: "RunnerController", spec }),
  );
}

function synthesize(compiled: ReturnType<typeof fixture>) {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-template-"));
  try {
    const stack = synthesizeController(compiled, "9007199254740993", directory);
    const asset = readdirSync(directory).find((file) =>
      /^asset\..+\.json$/u.test(file),
    );
    assert.ok(asset);
    assert.equal(readFileSync(join(directory, asset), "utf8"), compiled.json);
    return Template.fromJSON(stack.template);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

interface Statement {
  readonly Action: string | string[];
  readonly Condition?: Record<string, unknown>;
  readonly Resource?: unknown;
}

function rolePolicy(
  template: Template,
  role = "ControllerTaskRole",
): Statement[] {
  const policies = Object.entries({
    ...template.findResources("AWS::IAM::Policy"),
    ...template.findResources("AWS::IAM::ManagedPolicy"),
  });
  const matching = policies.filter(([key]) => key.startsWith(role));
  assert.ok(matching.length > 0);
  return matching.flatMap(
    ([, policy]) => policy.Properties.PolicyDocument.Statement as Statement[],
  );
}

function actions(template: Template) {
  return new Set(rolePolicy(template).flatMap(({ Action }) => Action));
}

test("the versioned artifact and retained tracking table match the runtime contract", () => {
  const config = fixture("mixed-ec2-fargate");
  const template = synthesize(config);
  template.hasResource("AWS::DynamoDB::Table", {
    DeletionPolicy: "Retain",
    UpdateReplacePolicy: "Retain",
    Properties: {
      TableName: "engineering-dev-runner-lifecycle",
      SSESpecification: { SSEEnabled: true },
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
      TimeToLiveSpecification: { AttributeName: "expiresAt", Enabled: true },
      KeySchema: [
        { AttributeName: "PK", KeyType: "HASH" },
        { AttributeName: "SK", KeyType: "RANGE" },
      ],
      GlobalSecondaryIndexes: [
        {
          IndexName: "due",
          Projection: { ProjectionType: "KEYS_ONLY" },
          KeySchema: [
            { AttributeName: "dueShard", KeyType: "HASH" },
            { AttributeName: "dueAtMillis", KeyType: "RANGE" },
          ],
        },
      ],
    },
  });
  template.hasResource("AWS::S3::Bucket", {
    DeletionPolicy: "Retain",
    Properties: {
      VersioningConfiguration: { Status: "Enabled" },
      BucketEncryption: {
        ServerSideEncryptionConfiguration: [
          { ServerSideEncryptionByDefault: { SSEAlgorithm: "AES256" } },
        ],
      },
    },
  });
  template.resourceCountIs("AWS::StepFunctions::StateMachine", 1);
  template.hasResourceProperties("Custom::ScaleSetRuntimeArtifact", {
    DestinationKey: `runtime/${config.sha256}.json`,
    SourceBucket: Match.anyValue(),
    SourceKey: Match.anyValue(),
  });
  template.hasOutput("RuntimeConfigSha256", { Value: config.sha256 });
  template.hasOutput("DeploymentGeneration", { Value: "9007199254740993" });
  const version = template.findOutputs("RuntimeConfigVersionId")
    .RuntimeConfigVersionId.Value as { "Fn::GetAtt": string[] };
  const [resource, property] = version["Fn::GetAtt"];
  assert.equal(property, "VersionId");
  assert.ok(
    template.findResources("Custom::ScaleSetRuntimeArtifact")[resource],
  );
  const read = rolePolicy(template).find(
    ({ Action }) => Action === "s3:GetObjectVersion",
  );
  assert.deepEqual(read?.Condition, {
    StringEquals: { "s3:VersionId": version },
  });
  const command = JSON.stringify(
    template.findOutputs("ControllerTaskArguments"),
  );
  for (const arg of [
    "run",
    "--config-s3-uri",
    "--config-version-id",
    "--sha256",
    "--generation",
    "--health-addr",
    "--ec2-event-queue-url",
    "--ecs-event-queue-url",
  ]) {
    assert.match(command, new RegExp(arg, "u"));
  }
});

test("the controller runs one shell-free Fargate task on the exact private network", () => {
  const config = fixture("mixed-ec2-fargate");
  const { spec } = config.runtime;
  const { image, hosting } = spec.controller;
  const network = spec.networks[hosting.network];
  const template = synthesize(config);
  template.resourceCountIs("AWS::ECS::Service", 1);
  template.resourceCountIs("AWS::ECS::TaskDefinition", 1);
  template.resourceCountIs("AWS::EC2::VPC", 0);
  template.resourceCountIs("AWS::EC2::Subnet", 0);
  template.resourceCountIs("AWS::EC2::SecurityGroup", 0);
  template.resourceCountIs("AWS::ECR::Repository", 0);
  const cluster = template.getResourceId("AWS::ECS::Cluster", {
    Properties: { ClusterName: "engineering-dev-controller" },
  });
  const task = template.getResourceId("AWS::ECS::TaskDefinition");
  template.hasResourceProperties("AWS::ECS::Service", {
    Cluster: { "Fn::GetAtt": [cluster, "Arn"] },
    TaskDefinition: { Ref: task },
    LaunchType: "FARGATE",
    PlatformVersion: "1.4.0",
    SchedulingStrategy: "REPLICA",
    DesiredCount: 1,
    EnableExecuteCommand: false,
    AvailabilityZoneRebalancing: "DISABLED",
    DeploymentController: { Type: "ECS" },
    DeploymentConfiguration: {
      MinimumHealthyPercent: 0,
      MaximumPercent: 100,
      DeploymentCircuitBreaker: { Enable: true, Rollback: true },
    },
    NetworkConfiguration: {
      AwsvpcConfiguration: {
        AssignPublicIp: "DISABLED",
        Subnets: network.subnets.map(({ id }) => id),
        SecurityGroups: network.securityGroupIds,
      },
    },
    LoadBalancers: Match.absent(),
  });
  const roles = Object.keys(template.findResources("AWS::IAM::Role"));
  const taskRole = roles.find((role) => role.startsWith("ControllerTaskRole"));
  const executionRole = roles.find((role) =>
    role.startsWith("ControllerExecutionRole"),
  );
  assert.ok(taskRole);
  assert.ok(executionRole);
  const [service] = Object.values(template.findResources("AWS::ECS::Service"));
  for (const role of ["ControllerTaskRole", "ControllerExecutionRole"]) {
    const dependencies = service.DependsOn as string[];
    assert.ok(dependencies.some((id) => id.startsWith(`${role}DefaultPolicy`)));
  }
  const logGroup = template.getResourceId("AWS::Logs::LogGroup", {
    Properties: {
      LogGroupName: "/ecs/engineering-dev-runner-controller",
      RetentionInDays: 30,
    },
  });
  const queues = Object.keys(template.findResources("AWS::SQS::Queue"));
  const ec2Queue = queues.find((queue) => queue.startsWith("Ec2EventQueue"));
  const ecsQueue = queues.find((queue) =>
    queue.startsWith("FargateEventQueue"),
  );
  assert.ok(ec2Queue);
  assert.ok(ecsQueue);
  const outputs = template.findOutputs("*");
  const [registryPrefix, repositoryDigest] = image.split("amazonaws.com");
  template.hasResourceProperties("AWS::ECS::TaskDefinition", {
    TaskRoleArn: { "Fn::GetAtt": [taskRole, "Arn"] },
    ExecutionRoleArn: { "Fn::GetAtt": [executionRole, "Arn"] },
    RequiresCompatibilities: ["FARGATE"],
    NetworkMode: "awsvpc",
    Cpu: "512",
    Memory: "1024",
    RuntimePlatform: {
      CpuArchitecture: "ARM64",
      OperatingSystemFamily: "LINUX",
    },
    ContainerDefinitions: [
      Match.objectLike({
        Image: {
          "Fn::Join": [
            "",
            [registryPrefix, { Ref: "AWS::URLSuffix" }, repositoryDigest],
          ],
        },
        Command: [
          "run",
          "--config-s3-uri",
          outputs.RuntimeConfigUri.Value,
          "--config-version-id",
          outputs.RuntimeConfigVersionId.Value,
          "--sha256",
          config.sha256,
          "--generation",
          "9007199254740993",
          "--health-addr",
          "127.0.0.1:8080",
          "--ec2-event-queue-url",
          { Ref: ec2Queue },
          "--ecs-event-queue-url",
          { Ref: ecsQueue },
        ],
        Essential: true,
        ReadonlyRootFilesystem: true,
        StopTimeout: 90,
        HealthCheck: {
          Command: [
            "CMD",
            "dotnet",
            "/app/Knightfall.ScaleSet.Aws.Controller.dll",
            "healthcheck",
          ],
        },
        LogConfiguration: {
          LogDriver: "awslogs",
          Options: {
            "awslogs-group": { Ref: logGroup },
            "awslogs-region": "ap-southeast-2",
            "awslogs-stream-prefix": "controller",
            mode: "non-blocking",
          },
        },
        PortMappings: Match.absent(),
        Secrets: Match.absent(),
      }),
    ],
  });
  const permissions = rolePolicy(template, "ControllerExecutionRole");
  assert.deepEqual(
    new Set(permissions.flatMap(({ Action }) => Action)),
    new Set([
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:GetDownloadUrlForLayer",
      "ecr:GetAuthorizationToken",
      "logs:CreateLogStream",
      "logs:PutLogEvents",
    ]),
  );
  for (const statement of permissions) {
    const actions = [statement.Action].flat();
    if (actions.includes("ecr:GetAuthorizationToken")) {
      assert.deepEqual(actions, ["ecr:GetAuthorizationToken"]);
      assert.equal(statement.Resource, "*");
    } else if (actions.includes("ecr:BatchGetImage")) {
      assert.match(
        JSON.stringify(statement.Resource),
        /:ecr:ap-southeast-2:111122223333:repository\/scaleset-dotnet-controller/u,
      );
    } else {
      assert.match(
        JSON.stringify(statement.Resource),
        new RegExp(logGroup, "u"),
      );
    }
  }
  assert.ok(
    [...actions(template)].every((action) => !/^(ecr|logs):/u.test(action)),
  );
});

test("offline synth rejects unsupported Fargate task sizes and awsvpc limits", () => {
  const config = fixture("fargate-only");
  const { spec } = config.runtime;
  const { controller } = spec;
  const badSize = rewrite(config, {
    ...spec,
    controller: {
      ...controller,
      hosting: { ...controller.hosting, cpu: 512, memoryMiB: 512 },
    },
  });
  assert.throws(() => synthesize(badSize), /valid Fargate size/u);
  const key = controller.hosting.network;
  const badNetwork = rewrite(config, {
    ...spec,
    networks: {
      ...spec.networks,
      [key]: {
        ...spec.networks[key],
        securityGroupIds: Array.from(
          { length: 6 },
          (_, index) => `sg-0000000${index}`,
        ),
      },
    },
  });
  assert.throws(() => synthesize(badNetwork), /ECS awsvpc limits/u);
});

test("active profiles alone supply provider permissions, event rules and role-scoped bootstrap actions", () => {
  for (const [name, ec2, ecs] of [
    ["ec2-only-pinned", true, false],
    ["ec2-only-latest", true, false],
    ["fargate-only", false, true],
    ["mixed-ec2-fargate", true, true],
  ] as const) {
    const compiled = fixture(name);
    const template = synthesize(
      rewrite(compiled, { ...compiled.runtime.spec, mode: "active" }),
    );
    const names = actions(template);
    for (const action of [
      "ec2:RunInstances",
      "ec2:TerminateInstances",
      "ec2:CreateTags",
    ]) {
      assert.equal(names.has(action), ec2, name);
    }
    for (const action of ["ecs:RunTask", "ecs:StopTask", "ecs:TagResource"]) {
      assert.equal(names.has(action), ecs, name);
    }
    for (const action of [
      "ecs:DescribeTasks",
      "secretsmanager:GetSecretValue",
      "sqs:ReceiveMessage",
      "sqs:DeleteMessage",
      "dynamodb:ConditionCheckItem",
      "dynamodb:UpdateItem",
    ]) {
      assert.ok(names.has(action), `${name} grants ${action}`);
    }
    if (!ecs) {
      const cluster = template.getResourceId("AWS::ECS::Cluster");
      const describe = rolePolicy(template).filter(({ Action }) =>
        [Action].flat().includes("ecs:DescribeTasks"),
      );
      assert.equal(describe.length, 1);
      assert.deepEqual(describe[0].Condition, {
        ArnEquals: { "ecs:cluster": { "Fn::GetAtt": [cluster, "Arn"] } },
      });
      assert.match(
        JSON.stringify(describe[0].Resource),
        /:task\/engineering-dev-controller\/\*/u,
      );
    }
    assert.ok(!names.has("dynamodb:TransactWriteItems"));
    template.resourceCountIs(
      "AWS::SQS::Queue",
      2 * (Number(ec2) + Number(ecs)),
    );
    const events = Object.values(template.findResources("AWS::Events::Rule"));
    const details = events.map((rule) => rule.Properties.EventPattern);
    assert.equal(
      details.some((pattern) => pattern.source[0] === "aws.ec2"),
      ec2,
    );
    assert.equal(
      details.some((pattern) => pattern.source[0] === "aws.ecs"),
      ecs,
    );
    for (const pattern of details) {
      assert.deepEqual(pattern.account, ["111122223333"]);
      assert.deepEqual(pattern.region, ["ap-southeast-2"]);
      assert.deepEqual(
        Object.keys(pattern.detail),
        pattern.source[0] === "aws.ec2"
          ? ["instance-id"]
          : ["clusterArn", "taskDefinitionArn"],
      );
    }
    const policy = JSON.stringify(template.findResources("AWS::IAM::Policy"));
    assert.ok(policy.includes("HELLO#${aws:userid}"));
    assert.ok(policy.includes("OFFER#${aws:userid}"));
    assert.ok(!policy.includes("STATUS#"));
  }
});

test("dry run uses neither lifecycle mutations nor event consumers", () => {
  const config = fixture("mixed-ec2-fargate");
  for (const inspectAws of [false, true]) {
    const template = synthesize(
      rewrite(config, {
        ...config.runtime.spec,
        mode: "dry-run",
        dryRun: {
          demand: {
            type: "snapshot",
            s3Uri: "s3://snapshot-fixture/engineer/data.json",
            versionId: "fixture-version",
            sha256: "a".repeat(64),
          },
          inspectAws,
        },
      }),
    );
    const names = actions(template);
    template.resourceCountIs("AWS::ECS::Service", 1);
    template.resourceCountIs("AWS::SQS::Queue", 0);
    template.resourceCountIs("AWS::Events::Rule", 0);
    const [task] = Object.values(
      template.findResources("AWS::ECS::TaskDefinition"),
    );
    const command = task.Properties.ContainerDefinitions[0].Command as string[];
    assert.equal(command[0], "run");
    assert.ok(!command.includes("--ec2-event-queue-url"));
    assert.ok(!command.includes("--ecs-event-queue-url"));
    for (const action of [
      "secretsmanager:GetSecretValue",
      "iam:PassRole",
      "ec2:RunInstances",
      "ec2:TerminateInstances",
      "ec2:CreateTags",
      "ecs:RunTask",
      "ecs:StopTask",
      "ecs:TagResource",
      "sqs:ReceiveMessage",
      "sqs:DeleteMessage",
      "dynamodb:ConditionCheckItem",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:DeleteItem",
    ]) {
      assert.ok(!names.has(action), action);
    }
    assert.equal(names.has("ec2:DescribeInstances"), inspectAws);
    assert.equal(names.has("ecs:DescribeTaskDefinition"), inspectAws);
    assert.equal(names.has("dynamodb:GetItem"), inspectAws);
    const reads = rolePolicy(template).filter(
      ({ Action }) => Action === "s3:GetObjectVersion",
    );
    assert.equal(reads.length, 2);
    assert.ok(
      reads.some(
        (statement) =>
          JSON.stringify(statement).includes(
            "snapshot-fixture/engineer/data.json",
          ) && statement.Condition?.StringEquals !== undefined,
      ),
    );
  }
});

test("imports never create replacement resources and profile ordering never changes logical IDs", () => {
  const original = fixture("mixed-ec2-fargate");
  const { spec } = original.runtime;
  const reversed = rewrite(original, {
    ...spec,
    profiles: [...spec.profiles].reverse(),
  });
  assert.deepEqual(
    Object.keys(synthesize(original).toJSON().Resources).sort(),
    Object.keys(synthesize(reversed).toJSON().Resources).sort(),
  );

  const importConfig = rewrite(original, {
    ...spec,
    controller: {
      ...spec.controller,
      hosting: { ...spec.controller.hosting, cpuArchitecture: "x64" },
    },
    tracking: { table: { type: "import", name: "existing-tracking" } },
    ecsClusters: {
      ...spec.ecsClusters,
      [spec.controller.hosting.cluster]: {
        type: "import",
        arn: "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-dev-controller",
      },
    },
  });
  const imported = synthesize(importConfig);
  imported.resourceCountIs("AWS::DynamoDB::Table", 0);
  imported.resourceCountIs("AWS::ECS::Cluster", 0);
  imported.resourceCountIs("AWS::EC2::VPC", 0);
  imported.resourceCountIs("AWS::ECS::TaskDefinition", 1);
  imported.hasResourceProperties("AWS::ECS::TaskDefinition", {
    RuntimePlatform: {
      CpuArchitecture: "X86_64",
      OperatingSystemFamily: "LINUX",
    },
  });
  imported.hasResourceProperties("AWS::ECS::Service", {
    Cluster:
      "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-dev-controller",
  });
  imported.hasOutput("TrackingTableName", { Value: "existing-tracking" });
});

test("shared cluster aliases deduplicate creation and disabled backends contribute no capability", () => {
  const mixed = fixture("mixed-ec2-fargate");
  const { spec } = mixed.runtime;
  const hostAlias = spec.controller.hosting.cluster;
  const sameCluster = rewrite(mixed, {
    ...spec,
    ecsClusters: { ...spec.ecsClusters, shared: spec.ecsClusters[hostAlias] },
    profiles: spec.profiles.map((profile) =>
      profile.provisioner.type === "fargate"
        ? {
            ...profile,
            provisioner: { ...profile.provisioner, cluster: "shared" },
          }
        : profile,
    ),
  });
  const shared = synthesize(sameCluster);
  shared.resourceCountIs("AWS::ECS::Cluster", 1);
  shared.hasResourceProperties("AWS::ECS::Service", {
    Cluster: {
      "Fn::GetAtt": [shared.getResourceId("AWS::ECS::Cluster"), "Arn"],
    },
  });
  const disabled = rewrite(mixed, {
    ...spec,
    profiles: spec.profiles.map((profile) =>
      profile.provisioner.type === "ec2"
        ? { ...profile, state: "disabled" }
        : { ...profile, state: "draining" },
    ),
  });
  const template = synthesize(disabled);
  assert.ok(actions(template).has("ecs:RunTask"));
  assert.ok(!actions(template).has("ec2:RunInstances"));
  template.resourceCountIs("AWS::SQS::Queue", 2);
});
