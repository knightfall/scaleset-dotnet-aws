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

function runtimePolicy(template: Template): Statement[] {
  const policies = Object.entries({
    ...template.findResources("AWS::IAM::Policy"),
    ...template.findResources("AWS::IAM::ManagedPolicy"),
  });
  const matching = policies.filter(([key]) =>
    key.startsWith("ControllerTaskRole"),
  );
  assert.ok(matching.length > 0);
  return matching.flatMap(
    ([, policy]) => policy.Properties.PolicyDocument.Statement as Statement[],
  );
}

function actions(template: Template) {
  return new Set(runtimePolicy(template).flatMap(({ Action }) => Action));
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
  const read = runtimePolicy(template).find(
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
    "--ec2-event-queue-url",
    "--ecs-event-queue-url",
  ]) {
    assert.match(command, new RegExp(arg, "u"));
  }
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
    template.resourceCountIs("AWS::SQS::Queue", 0);
    template.resourceCountIs("AWS::Events::Rule", 0);
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
    const reads = runtimePolicy(template).filter(
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
  imported.resourceCountIs("AWS::ECS::TaskDefinition", 0);
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
  synthesize(sameCluster).resourceCountIs("AWS::ECS::Cluster", 1);
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
