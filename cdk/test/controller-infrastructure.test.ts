import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { isDeepStrictEqual } from "node:util";
import { Match, Template } from "aws-cdk-lib/assertions";
import { compileConfig } from "../lib/config/compiler.ts";
import { ConfigValidationError } from "../lib/config/errors.ts";
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

function synthesize(
  compiled: ReturnType<typeof fixture>,
  fargatePreflight?: string,
) {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-template-"));
  try {
    const stack = synthesizeController(
      compiled,
      "9007199254740993",
      directory,
      { fargatePreflight },
    );
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

test("qualifying predecessor capture grants only an exact active log-group read", () => {
  const baseline = fixture("ec2-only-pinned");
  const { spec } = baseline.runtime;
  const { account, region } = spec.aws;
  const capture = {
    logGroupArn: `arn:aws:logs:${region}:${account}:log-group:/aws/events/controller-fence`,
    eventRuleArn: `arn:aws:events:${region}:${account}:rule/controller-fence`,
    qualifiedFrom: "2026-11-25T00:00:00Z",
    qualifiedUntil: "2026-12-02T00:00:00Z",
  };
  const controller = { ...spec.controller, predecessorCapture: capture };
  const active = rewrite(baseline, { ...spec, mode: "active", controller });
  assert.deepEqual(active.runtime.spec.controller.predecessorCapture, capture);
  const captured = synthesize(active);
  assert.deepEqual(
    rolePolicy(captured).filter(({ Action }) =>
      [Action].flat().some((action) => action.startsWith("logs:")),
    ),
    [
      {
        Action: "logs:FilterLogEvents",
        Effect: "Allow",
        Resource: capture.logGroupArn,
      },
    ],
  );
  const withoutCapture = synthesize(
    rewrite(baseline, { ...spec, mode: "active" }),
  );
  assert.deepEqual(
    captured.findResources("AWS::Logs::LogGroup"),
    withoutCapture.findResources("AWS::Logs::LogGroup"),
  );
  for (const config of [
    rewrite(baseline, { ...spec, mode: "dry-run", controller }),
    rewrite(baseline, { ...spec, mode: "active" }),
  ]) {
    assert.ok(!actions(synthesize(config)).has("logs:FilterLogEvents"));
  }
  for (const invalid of [
    { logGroupArn: capture.logGroupArn + ":*" },
    {
      logGroupArn: `arn:aws:logs:${region}:${account}:log-group:/ecs/${baseline.runtime.metadata.name}-runner-controller`,
    },
    { eventRuleArn: capture.eventRuleArn.replace(account, "000000000000") },
    { qualifiedFrom: "2026-11-31T00:00:00Z" },
    { qualifiedUntil: capture.qualifiedFrom },
  ]) {
    assert.throws(
      () =>
        rewrite(baseline, {
          ...spec,
          controller: {
            ...controller,
            predecessorCapture: { ...capture, ...invalid },
          },
        }),
      (error: unknown) =>
        error instanceof ConfigValidationError &&
        error.path.startsWith("/spec/controller/predecessorCapture"),
    );
  }
});

function actions(template: Template) {
  return new Set(rolePolicy(template).flatMap(({ Action }) => Action));
}

const shellApproval = {
  image: `111122223333.dkr.ecr.ap-southeast-2.amazonaws.com/runner@sha256:${"a".repeat(64)}`,
  entryPoint: ["/opt/scaleset/RunnerBootstrap"],
  command: ["fargate", "/opt/scaleset/fargate-bootstrap.json"],
  scratchMountPaths: ["/work"],
};

const quota = {
  account: "111122223333",
  region: "ap-southeast-2",
  observedAt: "2026-11-23T10:00:00.0000001Z",
  expiresAt: "2026-11-23T10:15:00+00:00",
  remainingCpuUnits: 8_192,
  reservedCpuUnits: 1_024,
};

test("partitions without Function URLs preserve dry-run synthesis and hold active deployment", () => {
  const yaml = readFileSync(
    new URL("../../testdata/config/ec2-only-pinned.yaml", import.meta.url),
    "utf8",
  );
  for (const [region, partition, dnsSuffix] of [
    ["cn-north-1", "aws-cn", "amazonaws.com.cn"],
    ["us-gov-west-1", "aws-us-gov", "amazonaws.com"],
  ]) {
    const config = compileConfig(
      yaml
        .replaceAll("ap-southeast-2", region)
        .replaceAll("arn:aws:", `arn:${partition}:`)
        .replaceAll("amazonaws.com", dnsSuffix),
    );
    synthesize(config).resourceCountIs("AWS::Lambda::Url", 0);
    const active = rewrite(config, { ...config.runtime.spec, mode: "active" });
    assert.throws(
      () => synthesize(active),
      /Active runner HELLO requires Lambda Function URLs/u,
    );
  }
});

test("active service waits for its HELLO brokers and runner invocation policies", () => {
  const template = synthesize(fixture("mixed-ec2-fargate"));
  const resources = template.toJSON().Resources as Record<
    string,
    { Type: string }
  >;
  const brokerResources = Object.entries(resources)
    .filter(
      ([id, resource]) =>
        id.includes("HelloBroker") &&
        ["AWS::Lambda::Function", "AWS::Lambda::Url"].includes(resource.Type),
    )
    .map(([id]) => id);
  const runnerPolicies = Object.entries(resources)
    .filter(
      ([id, resource]) =>
        /^Bootstrap[a-f0-9]{16}Policy/u.test(id) &&
        resource.Type === "AWS::IAM::Policy",
    )
    .map(([id]) => id);
  assert.equal(brokerResources.length, 8);
  assert.equal(runnerPolicies.length, 4);
  const [service] = Object.values(template.findResources("AWS::ECS::Service"));
  const dependencies = service.DependsOn as string[];
  for (const id of [...brokerResources, ...runnerPolicies]) {
    assert.ok(dependencies.includes(id), id);
  }
});

test("HELLO reservations add enabled capacity by role, cap it, and stay disabled in dry-run", () => {
  const config = fixture("mixed-ec2-fargate");
  const spec = config.runtime.spec;
  const profiles = spec.profiles.map((profile) =>
    profile.provisioner.type === "fargate"
      ? {
          ...profile,
          state:
            profile.id === "linux-ci-arm"
              ? ("draining" as const)
              : profile.state,
          capacity: { ...profile.capacity, maxRunners: 6 },
          provisioner: {
            ...profile.provisioner,
            bootstrapRoleArn:
              "arn:aws:iam::111122223333:role/fargate-shell-bootstrap",
          },
        }
      : profile,
  );

  function brokers(mode: "active" | "dry-run") {
    const resources = synthesize(
      rewrite(config, { ...spec, profiles, mode }),
    ).findResources("AWS::Lambda::Function");
    return Object.entries(resources).filter(([id]) =>
      id.includes("HelloBroker"),
    );
  }
  const active = brokers("active");
  const dryRun = brokers("dry-run");
  assert.deepEqual(
    active.map(([id]) => id),
    dryRun.map(([id]) => id),
  );
  assert.deepEqual(
    Object.fromEntries(
      active.map(([, { Properties }]) => [
        Properties.Environment.Variables.BOOTSTRAP_ROLE_NAME,
        Properties.ReservedConcurrentExecutions,
      ]),
    ),
    {
      "runner-linux-bootstrap": 12,
      "runner-arm64-bootstrap": 16,
      "fargate-shell-bootstrap": 12,
    },
  );
  for (const [, { Properties }] of dryRun) {
    assert.equal(Properties.ReservedConcurrentExecutions, 0);
    assert.equal(Properties.Environment.Variables.HELLO_ENABLED, "false");
  }
  synthesize(config).hasResourceProperties("AWS::Lambda::Function", {
    ReservedConcurrentExecutions: 16,
    Environment: {
      Variables: Match.objectLike({
        BOOTSTRAP_ROLE_NAME: "fargate-shell-bootstrap",
        HELLO_ENABLED: "true",
      }),
    },
  });
});

test("explicit Fargate preflight changes only the controller task definition environment", () => {
  const config = fixture("mixed-ec2-fargate");
  const raw = JSON.stringify(
    { profiles: { "linux-shell": shellApproval }, quota },
    null,
    2,
  ).replace("8192", "9223372036854775807");
  const defaultTemplate = synthesize(config);
  const configuredTemplate = synthesize(config, raw);
  configuredTemplate.resourceCountIs("AWS::ECS::TaskDefinition", 1);
  configuredTemplate.hasResourceProperties("AWS::ECS::TaskDefinition", {
    ContainerDefinitions: Match.arrayWith([
      Match.objectLike({
        Name: "Controller",
        Environment: [{ Name: "SCALESET_FARGATE_PREFLIGHT", Value: raw }],
      }),
    ]),
  });
  defaultTemplate.hasResourceProperties("AWS::ECS::TaskDefinition", {
    ContainerDefinitions: Match.arrayWith([
      Match.objectLike({
        Environment: Match.absent(),
      }),
    ]),
  });
  configuredTemplate.hasOutput("RuntimeConfigSha256", { Value: config.sha256 });
  const wholeTemplate = configuredTemplate.toJSON();
  const task = Object.values(wholeTemplate.Resources).find(
    (entry) => (entry as { Type: string }).Type === "AWS::ECS::TaskDefinition",
  ) as {
    Properties: { ContainerDefinitions: [{ Environment?: unknown }] };
  };
  delete task.Properties.ContainerDefinitions[0].Environment;
  assert.deepEqual(wholeTemplate, defaultTemplate.toJSON());
});

test("refreshable Fargate quota grants the controller only one same-account S3 object", () => {
  const config = fixture("mixed-ec2-fargate");
  const quotaSource = {
    bucket: "approved-quota-samples",
    key: "usage/on-demand.json",
  };
  const raw = JSON.stringify({
    profiles: { "linux-shell": shellApproval },
    quotaSource,
  });
  const template = synthesize(config, raw);
  template.hasResourceProperties("AWS::ECS::TaskDefinition", {
    ContainerDefinitions: Match.arrayWith([
      Match.objectLike({
        Name: "Controller",
        Environment: [{ Name: "SCALESET_FARGATE_PREFLIGHT", Value: raw }],
      }),
    ]),
  });
  assert.deepEqual(
    rolePolicy(template).filter(({ Action }) =>
      [Action].flat().includes("s3:GetObject"),
    ),
    [
      {
        Action: "s3:GetObject",
        Condition: { StringEquals: { "s3:ResourceAccount": quota.account } },
        Effect: "Allow",
        Resource: {
          "Fn::Join": [
            "",
            [
              "arn:",
              { Ref: "AWS::Partition" },
              ":s3:::approved-quota-samples/usage/on-demand.json",
            ],
          ],
        },
      },
    ],
  );
  assert.ok(!actions(template).has("s3:PutObject"));
  assert.ok(!actions(template).has("s3:ListBucket"));
  for (const source of [
    { ...quotaSource, key: "usage/*" },
    { ...quotaSource, key: "usage/../on-demand.json" },
    { ...quotaSource, key: "usage/on-demand.json\n" },
    { ...quotaSource, bucket: "Approved-Quota" },
    { ...quotaSource, account: quota.account },
  ]) {
    assert.throws(
      () =>
        synthesize(
          config,
          JSON.stringify({ profiles: {}, quotaSource: source }),
        ),
      {
        message:
          "SCALESET_FARGATE_PREFLIGHT is invalid for the configured deployment.",
      },
    );
  }
});

test("Fargate preflight is available in inspection mode and refuses unrelated profiles or modes", () => {
  const mixed = fixture("mixed-ec2-fargate");
  const inspection = rewrite(mixed, {
    ...mixed.runtime.spec,
    mode: "dry-run",
    dryRun: { demand: { type: "none" }, inspectAws: true },
  });
  const evidence = JSON.stringify({
    profiles: { "linux-shell": shellApproval },
  });
  synthesize(inspection, evidence).hasResourceProperties(
    "AWS::ECS::TaskDefinition",
    {
      ContainerDefinitions: Match.arrayWith([
        Match.objectLike({
          Environment: [
            { Name: "SCALESET_FARGATE_PREFLIGHT", Value: evidence },
          ],
        }),
      ]),
    },
  );
  const offline = rewrite(inspection, {
    ...inspection.runtime.spec,
    dryRun: { demand: { type: "none" }, inspectAws: false },
  });
  const disabled = rewrite(mixed, {
    ...mixed.runtime.spec,
    profiles: mixed.runtime.spec.profiles.map((profile) =>
      profile.id === "linux-shell"
        ? { ...profile, state: "disabled" }
        : profile,
    ),
  });
  const snapshot = rewrite(inspection, {
    ...inspection.runtime.spec,
    dryRun: {
      demand: { type: "snapshot", path: "demand.json" },
      inspectAws: true,
    },
  });
  for (const config of [
    offline,
    snapshot,
    disabled,
    fixture("ec2-only-pinned"),
  ]) {
    assert.throws(
      () => synthesize(config, evidence),
      /SCALESET_FARGATE_PREFLIGHT is invalid/u,
    );
  }
  assert.throws(
    () =>
      synthesize(
        mixed,
        JSON.stringify({ profiles: { "linux-ec2-build": shellApproval } }),
      ),
    /SCALESET_FARGATE_PREFLIGHT is invalid/u,
  );
});

test("Fargate preflight rejects invalid or oversized JSON without reflecting input", () => {
  const config = fixture("mixed-ec2-fargate");
  const approved = { profiles: { "linux-shell": shellApproval } };
  const sentinel = "unexpected-value-must-stay-private";
  const invalid = [
    JSON.stringify({ ...approved, secrets: sentinel }),
    JSON.stringify({
      profiles: {
        "linux-shell": {
          ...shellApproval,
          environment: { password: sentinel },
        },
      },
    }),
    JSON.stringify({
      ...approved,
      quota: { ...quota, account: "000000000000" },
    }),
    JSON.stringify({
      ...approved,
      quota: { ...quota, expiresAt: quota.observedAt },
    }),
    JSON.stringify({
      ...approved,
      quota: { ...quota, observedAt: "2026-02-30T01:00:00Z" },
    }),
    JSON.stringify({
      ...approved,
      quota: { ...quota, remainingCpuUnits: "8192" },
    }),
    JSON.stringify({ ...approved, quota }).replace(
      "8192",
      "9223372036854775808",
    ),
    JSON.stringify({ ...approved, quota }).replace("8192", "8.192e3"),
    `{"profiles":{},"profiles":${JSON.stringify(approved.profiles)}}`,
    JSON.stringify({
      profiles: {
        "linux-shell": {
          ...shellApproval,
          command: [`{{resolve:secretsmanager:${sentinel}}}`],
        },
      },
    }),
    JSON.stringify({
      profiles: {
        "linux-shell": { ...shellApproval, command: ["😀".repeat(4_096)] },
      },
    }),
  ];
  for (const input of invalid) {
    assert.throws(() => synthesize(config, input), {
      message:
        "SCALESET_FARGATE_PREFLIGHT is invalid for the configured deployment.",
    });
  }
});

test("observation queues redrive and controller-stack logs have bounded retention", () => {
  const config = fixture("mixed-ec2-fargate");
  const template = synthesize(config);
  template.resourceCountIs("AWS::CloudWatch::Alarm", 0);
  const groups = template.findResources("AWS::Logs::LogGroup");
  const functionResources = template.findResources("AWS::Lambda::Function");
  const usedByLambda = new Set<string>();
  for (const resource of Object.values(functionResources)) {
    const id = resource.Properties.LoggingConfig?.LogGroup?.Ref as
      string | undefined;
    assert.ok(
      id && groups[id],
      "Each controller-managed Lambda uses a managed log group.",
    );
    usedByLambda.add(id);
    assert.equal(groups[id].Properties.RetentionInDays, 30);
    assert.equal(groups[id].DeletionPolicy, "Retain");
  }
  assert.ok([...usedByLambda].some((id) => id.startsWith("RuntimeConfigLogs")));
  assert.ok([...usedByLambda].some((id) => /^Bootstrap.*HelloLogs/u.test(id)));
  const queues = Object.entries(template.findResources("AWS::SQS::Queue"));
  for (const id of ["Ec2", "Fargate"]) {
    const source = queues.find(([name]) => name.startsWith(`${id}EventQueue`));
    const dlq = queues.find(([name]) =>
      name.startsWith(`${id}EventDeadLetters`),
    );
    assert.ok(source && dlq);
    assert.deepEqual(source[1].Properties.RedrivePolicy, {
      maxReceiveCount: 5,
      deadLetterTargetArn: { "Fn::GetAtt": [dlq[0], "Arn"] },
    });
    assert.equal(dlq[1].Properties.MessageRetentionPeriod, 14 * 24 * 60 * 60);
  }
});

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

test("mixed launch policies bind each EC2 template to its network and each Fargate definition to its cluster", () => {
  const config = fixture("mixed-ec2-fargate");
  const { spec } = config.runtime;
  const [firstEc2, secondEc2, firstFargate, secondFargate] = spec.profiles;
  assert.equal(firstEc2.provisioner.type, "ec2");
  assert.equal(secondEc2.provisioner.type, "ec2");
  assert.equal(firstFargate.provisioner.type, "fargate");
  assert.equal(secondFargate.provisioner.type, "fargate");
  const firstNetwork = spec.networks[firstEc2.provisioner.network];
  const secondNetwork = {
    ...firstNetwork,
    subnets: [
      { id: "subnet-0123456789abcdef4", az: "ap-southeast-2a" },
      { id: "subnet-0123456789abcdef5", az: "ap-southeast-2b" },
    ],
    securityGroupIds: ["sg-0123456789abcdef2"],
  };
  const template = synthesize(
    rewrite(config, {
      ...spec,
      networks: { ...spec.networks, "runner-alt": secondNetwork },
      profiles: [
        firstEc2,
        {
          ...secondEc2,
          provisioner: { ...secondEc2.provisioner, network: "runner-alt" },
        },
        firstFargate,
        secondFargate,
      ],
    }),
  );
  const statements = rolePolicy(template);
  const ec2Arn = (resource: string, region = spec.aws.region) => ({
    "Fn::Join": [
      "",
      [
        "arn:",
        { Ref: "AWS::Partition" },
        `:ec2:${region}:111122223333:${resource}`,
      ],
    ],
  });
  const ec2Grants = statements.filter(({ Action }) =>
    [Action].flat().includes("ec2:RunInstances"),
  );
  const networkGrants = ec2Grants.filter(({ Resource }) =>
    JSON.stringify(Resource).match(/:(?:subnet|security-group)\//u),
  );
  assert.equal(networkGrants.length, 2);
  for (const [provisioner, network] of [
    [firstEc2.provisioner, firstNetwork],
    [secondEc2.provisioner, secondNetwork],
  ] as const) {
    const launchTemplate = ec2Arn(
      `launch-template/${provisioner.launchTemplateId}`,
    );
    const resources = [
      ...network.subnets.map(({ id }) => ec2Arn(`subnet/${id}`)),
      ...network.securityGroupIds.map((id) => ec2Arn(`security-group/${id}`)),
    ];
    const grant = networkGrants.find(({ Condition }) =>
      isDeepStrictEqual(Condition, {
        ArnEquals: { "ec2:LaunchTemplate": launchTemplate },
      }),
    );
    assert.ok(grant);
    assert.deepEqual(grant.Resource, resources);
    assert.ok(
      ec2Grants.some(
        ({ Resource, Condition }) =>
          isDeepStrictEqual(Resource, launchTemplate) && !Condition,
      ),
    );
    const instance = ec2Grants.find(
      ({ Resource, Condition }) =>
        isDeepStrictEqual(Resource, ec2Arn("instance/*")) &&
        isDeepStrictEqual(
          (Condition?.ArnEquals as Record<string, unknown>)?.[
            "ec2:LaunchTemplate"
          ],
          launchTemplate,
        ),
    );
    assert.deepEqual(instance?.Condition?.ArnEquals, {
      "ec2:LaunchTemplate": launchTemplate,
      "ec2:InstanceProfile": provisioner.instanceProfileArn,
    });
  }
  const runTask = statements.filter(({ Action }) =>
    [Action].flat().includes("ecs:RunTask"),
  );
  assert.equal(runTask.length, 2);
  for (const { id, provisioner } of [firstFargate, secondFargate]) {
    assert.equal(provisioner.type, "fargate");
    const cluster = spec.ecsClusters[provisioner.cluster];
    assert.equal(cluster.type, "import");
    const grant = runTask.find(
      ({ Resource }) => Resource === provisioner.taskDefinitionArn,
    );
    assert.deepEqual(grant?.Condition, {
      ArnEquals: { "ecs:cluster": cluster.arn },
      StringEquals: {
        "aws:RequestTag/scaleset-aws:controller": "scaleset-dotnet-aws",
        "aws:RequestTag/scaleset-aws:deployment":
          "111122223333:ap-southeast-2:engineering-dev",
        "aws:RequestTag/scaleset-aws:profile": id,
        "aws:RequestTag/scaleset-aws:bootstrap-protocol": "v1",
      },
      Bool: { "ecs:enable-execute-command": "false" },
      Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
    });
  }
  const passRole = statements.filter(({ Action }) =>
    [Action].flat().includes("iam:PassRole"),
  );
  assert.deepEqual(
    new Set(passRole),
    new Set([
      {
        Action: "iam:PassRole",
        Resource: [
          firstEc2.provisioner.bootstrapRoleArn,
          secondEc2.provisioner.bootstrapRoleArn,
        ].sort(),
        Effect: "Allow",
        Condition: {
          StringEquals: { "iam:PassedToService": "ec2.amazonaws.com" },
          ArnLike: {
            "iam:AssociatedResourceArn": ec2Arn("instance/*", "*"),
          },
        },
      },
      {
        Action: "iam:PassRole",
        Resource: [
          firstFargate.provisioner.bootstrapRoleArn,
          firstFargate.provisioner.executionRoleArn,
          secondFargate.provisioner.bootstrapRoleArn,
          secondFargate.provisioner.executionRoleArn,
        ].sort(),
        Effect: "Allow",
        Condition: {
          StringEquals: { "iam:PassedToService": "ecs-tasks.amazonaws.com" },
        },
      },
    ]),
  );
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
    assert.ok(!policy.includes("HELLO#${aws:userid}"));
    assert.ok(policy.includes("OFFER#${aws:userid}"));
    assert.ok(!policy.includes("STATUS#"));
    assert.ok(policy.includes("ForAnyValue:StringLike"));
    assert.ok(policy.includes("lambda:InvokedViaFunctionUrl"));
    template.hasResourceProperties("AWS::Lambda::Url", {
      AuthType: "AWS_IAM",
    });
  }
});

test("only an active qualified EC2 role receives the supervisor STATUS policy", () => {
  const config = fixture("mixed-ec2-fargate");
  const { spec } = config.runtime;
  const ec2 = spec.profiles.find(
    (profile) => profile.provisioner.type === "ec2",
  );
  assert.ok(ec2?.provisioner.type === "ec2");
  const fargate = spec.profiles.find(
    (profile) => profile.provisioner.type === "fargate",
  );
  assert.ok(fargate?.provisioner.type === "fargate");
  const qualified = {
    ...ec2,
    provisioner: {
      ...ec2.provisioner,
      launchTemplateVersion: { type: "pinned", version: "12" } as const,
      statusQualification: {
        imageId: "ami-0123456789abcdef0",
        roleId: "AROAFIXTUREBOOTSTRAP01",
        evidenceSha256: "b".repeat(64),
      },
    },
  };
  const compiled = rewrite(config, {
    ...spec,
    mode: "active",
    profiles: [qualified, fargate],
  });
  const template = synthesize(compiled);
  const policies = Object.values(template.findResources("AWS::IAM::Policy"));
  const status = policies.filter((policy) =>
    JSON.stringify(policy).includes("STATUS#${aws:userid}"),
  );
  assert.equal(status.length, 1);
  assert.deepEqual(status[0].Properties.Roles, [
    ec2.provisioner.bootstrapRoleArn.split("/").at(-1),
  ]);
  assert.equal(status[0].Properties.PolicyDocument.Statement.length, 1);
  const [statement] = status[0].Properties.PolicyDocument.Statement;
  assert.equal(statement.Action, "dynamodb:PutItem");
  assert.deepEqual(statement.Condition["ForAllValues:StringEquals"], {
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
  });
  assert.deepEqual(statement.Condition.StringEqualsIfExists, {
    "dynamodb:ReturnValues": ["NONE", "ALL_OLD"],
  });
  assert.deepEqual(statement.Condition.NumericEquals, {
    "ec2:RoleDelivery": "2.0",
  });
  assert.match(
    JSON.stringify(statement.Condition.ArnLike["ec2:SourceInstanceARN"]),
    /:ec2:ap-southeast-2:111122223333:instance\/\*/u,
  );
  assert.deepEqual(statement.Condition.Null, {
    "dynamodb:LeadingKeys": "false",
    "dynamodb:Attributes": "false",
    "dynamodb:EnclosingOperation": "true",
  });
  assert.doesNotMatch(
    JSON.stringify(
      synthesize(
        rewrite(compiled, { ...compiled.runtime.spec, mode: "dry-run" }),
      ).toJSON(),
    ),
    /STATUS#/u,
  );

  assert.throws(
    () =>
      rewrite(config, {
        ...spec,
        profiles: [
          {
            ...qualified,
            provisioner: {
              ...qualified.provisioner,
              launchTemplateVersion: { type: "alias", name: "$Latest" },
            },
          },
          fargate,
        ],
      }),
    /numeric pinned Launch Template/u,
  );
  assert.throws(
    () =>
      rewrite(config, {
        ...spec,
        profiles: [
          qualified,
          {
            ...fargate,
            provisioner: {
              ...fargate.provisioner,
              bootstrapRoleArn: ec2.provisioner.bootstrapRoleArn,
            },
          },
        ],
      }),
    /dedicated EC2 bootstrap role/u,
  );
});

test("S3 snapshot dry run grants only the nominated object, version, and account in addition to runtime input", () => {
  const config = fixture("mixed-ec2-fargate");
  for (const inspectAws of [false, true]) {
    const template = synthesize(
      rewrite(config, {
        ...config.runtime.spec,
        mode: "dry-run",
        dryRun: {
          demand: {
            type: "snapshot",
            s3Uri: "s3://snapshot-fixture/profile-blue/v1/Runner_demand.json",
            versionId: "specific+version/=",
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
    template.hasResourceProperties("AWS::Lambda::Function", {
      ReservedConcurrentExecutions: 0,
      Environment: { Variables: Match.objectLike({ HELLO_ENABLED: "false" }) },
    });
    const [task] = Object.values(
      template.findResources("AWS::ECS::TaskDefinition"),
    );
    const command = task.Properties.ContainerDefinitions[0].Command as string[];
    assert.equal(command[0], "run");
    assert.ok(!command.includes("--ec2-event-queue-url"));
    assert.ok(!command.includes("--ecs-event-queue-url"));
    assert.deepEqual([...names], ["s3:GetObjectVersion"]);
    const reads = rolePolicy(template);
    assert.equal(reads.length, 2);
    assert.deepEqual(
      reads.find((statement) =>
        JSON.stringify(statement.Resource).includes("snapshot-fixture"),
      ),
      {
        Action: "s3:GetObjectVersion",
        Condition: {
          StringEquals: {
            "s3:VersionId": "specific+version/=",
            "s3:ResourceAccount": config.runtime.spec.aws.account,
          },
        },
        Effect: "Allow",
        Resource: {
          "Fn::Join": [
            "",
            [
              "arn:",
              { Ref: "AWS::Partition" },
              ":s3:::snapshot-fixture/profile-blue/v1/Runner_demand.json",
            ],
          ],
        },
      },
    );
  }
});

test("dry-run inventory reads follow inspectAws and enabled mixed provisioners without lifecycle access", () => {
  const config = fixture("mixed-ec2-fargate");
  for (const inspectAws of [false, true]) {
    for (const disabled of [undefined, "ec2", "fargate"] as const) {
      const template = synthesize(
        rewrite(config, {
          ...config.runtime.spec,
          mode: "dry-run",
          dryRun: { demand: { type: "none" }, inspectAws },
          profiles: config.runtime.spec.profiles.map((profile) =>
            profile.provisioner.type === disabled
              ? { ...profile, state: "disabled" }
              : profile,
          ),
        }),
      );
      const names = actions(template);
      assert.equal(
        names.has("ec2:DescribeInstances"),
        inspectAws && disabled !== "ec2",
      );
      assert.equal(
        names.has("ecs:ListTasks"),
        inspectAws && disabled !== "fargate",
      );
      assert.equal(
        names.has("iam:GetInstanceProfile"),
        inspectAws && disabled !== "ec2",
      );
      assert.ok([...names].every((action) => !action.startsWith("dynamodb:")));
      assert.equal(
        rolePolicy(template).filter(
          ({ Action }) => Action === "s3:GetObjectVersion",
        ).length,
        1,
      );
    }
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
