import assert from "node:assert/strict";
import {
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { Match, Template } from "aws-cdk-lib/assertions";
import { stringify } from "yaml";
import { compileConfig } from "../../lib/config/compiler.ts";
import type {
  ControllerConfig,
  RunnerProfile,
} from "../../lib/config/types.ts";
import { parseYaml } from "../../lib/config/yaml.ts";
import { synthesizeController } from "../../lib/infrastructure/controller-stack.ts";
import { deployController } from "../../lib/infrastructure/deploy-controller.ts";
import type { CommandResult } from "../../lib/infrastructure/deploy-controller.ts";
import {
  controllerStackName,
  deploymentIdentity,
  trackingTableName,
} from "../../lib/infrastructure/deployment-input.ts";

const source = parseYaml(
  readFileSync(
    new URL("../../../testdata/config/mixed-ec2-fargate.yaml", import.meta.url),
    "utf8",
  ),
) as ControllerConfig;
const [, ec2, shell, arm] = source.spec.profiles;
const drainingShell = { ...shell, state: "draining" } as const;
const { account, region } = source.spec.aws;
const deploymentId = `${account}:${region}:${source.metadata.name}`;
type Compiled = ReturnType<typeof compileConfig>;
type RetirementCheck = "verified" | "retirement-required";

function config(...profiles: RunnerProfile[]) {
  return compileConfig(
    stringify({ ...source, spec: { ...source.spec, profiles } }),
  );
}

function synthesize(compiled: Compiled) {
  const outdir = mkdtempSync(join(tmpdir(), "scaleset-transition-"));
  try {
    return Template.fromJSON(
      synthesizeController(compiled, "1", outdir).template,
    );
  } finally {
    rmSync(outdir, { recursive: true, force: true });
  }
}

function deployment(
  prior: Compiled,
  candidate: Compiled,
  ...checks: RetirementCheck[]
) {
  const scope = `arn:aws:cloudformation:${region}:${account}`;
  const stackName = controllerStackName(prior.runtime);
  const stackId = `${scope}:stack/${stackName}/fixture-stack`;
  const version = "prior-runtime-version";
  const steps: string[] = [];
  const pendingChecks = [...checks];
  let template: Template | undefined;
  let changeSetId: string | undefined;
  let changeSetName: string | undefined;
  let completed = false;
  const ok = (value: object = {}): CommandResult => ({
    status: 0,
    stdout: JSON.stringify(value),
    stderr: "",
  });
  const run = () => {
    const recovery = mkdtempSync(join(tmpdir(), "scaleset-transition-state-"));
    try {
      deployController(
        {
          compiled: candidate,
          generation: "2",
        },
        (program, args) => {
          const option = (name: string) => {
            const index = args.indexOf(name);
            assert.ok(index >= 0 && index + 1 < args.length, name);
            return args[index + 1];
          };
          if (program === "dotnet") {
            steps.push("retire");
            assert.deepEqual(
              args.slice(args.indexOf("--") + 1, args.indexOf("--") + 3),
              ["preflight", "retire"],
            );
            assert.equal(option("--current-generation"), "1");
            assert.equal(option("--generation"), "2");
            assert.equal(option("--current-sha256"), prior.sha256);
            assert.equal(option("--sha256"), candidate.sha256);
            assert.equal(
              readFileSync(option("--current-config"), "utf8"),
              prior.json,
            );
            assert.equal(
              readFileSync(option("--config"), "utf8"),
              candidate.json,
            );
            const check = pendingChecks.shift();
            assert.ok(check);
            return check === "verified"
              ? ok()
              : {
                  status: 1,
                  stdout: "",
                  stderr: "retirement-required",
                };
          }
          if (program === process.execPath) {
            steps.push("prepare");
            assert.equal(option("--method"), "prepare-change-set");
            changeSetName = option("--change-set-name");
            changeSetId = `${scope}:changeSet/${changeSetName}/fixture-set`;
            const outdir = option("--app");
            const asset = readdirSync(outdir).find((name) =>
              /^asset\..+\.json$/u.test(name),
            );
            assert.ok(asset);
            assert.equal(
              readFileSync(join(outdir, asset), "utf8"),
              candidate.json,
            );
            template = Template.fromJSON(
              JSON.parse(
                readFileSync(join(outdir, "Controller.template.json"), "utf8"),
              ),
            );
            template.hasOutput("RuntimeConfigSha256", {
              Value: candidate.sha256,
            });
            template.hasOutput("DeploymentGeneration", { Value: "2" });
            return ok();
          }

          assert.equal(program, "aws");
          assert.equal(option("--region"), region);
          const operation = args[6];
          steps.push(operation);
          switch (operation) {
            case "get-caller-identity":
              return ok({ Account: account });
            case "describe-stacks": {
              assert.equal(option("--stack-name"), stackName);
              const outputs = {
                DeploymentIdentity: deploymentIdentity(prior.runtime),
                TrackingTableName: trackingTableName(prior.runtime),
                RuntimeConfigUri: `s3://fixture-assets/runtime/${completed ? candidate.sha256 : prior.sha256}.json`,
                RuntimeConfigVersionId: version,
                RuntimeConfigSha256: completed
                  ? candidate.sha256
                  : prior.sha256,
                DeploymentGeneration: completed ? "2" : "1",
              };
              return ok({
                Stacks: [
                  {
                    StackName: stackName,
                    StackId: stackId,
                    StackStatus: "UPDATE_COMPLETE",
                    Outputs: Object.entries(outputs).map(
                      ([OutputKey, OutputValue]) => ({
                        OutputKey,
                        OutputValue,
                      }),
                    ),
                  },
                ],
              });
            }
            case "get-object":
              assert.equal(option("--version-id"), version);
              assert.equal(option("--expected-bucket-owner"), account);
              writeFileSync(args.at(-1)!, prior.json);
              return ok({ VersionId: version });
            case "describe-change-set":
              assert.equal(option("--stack-name"), stackId);
              assert.ok(
                [changeSetName, changeSetId].includes(
                  option("--change-set-name"),
                ),
              );
              return ok({
                Status: "CREATE_COMPLETE",
                ExecutionStatus: "AVAILABLE",
                StackId: stackId,
                StackName: stackName,
                ChangeSetId: changeSetId,
              });
            case "get-template":
              assert.ok(template);
              return ok({ TemplateBody: template.toJSON() });
            case "delete-change-set":
              assert.equal(option("--stack-name"), stackId);
              assert.equal(option("--change-set-name"), changeSetId);
              return ok();
            case "execute-change-set":
              assert.equal(option("--change-set-name"), changeSetId);
              return ok();
            case "wait":
              assert.ok(args.includes("stack-update-complete"));
              assert.equal(option("--stack-name"), stackId);
              completed = true;
              return ok();
            default:
              assert.fail(`Unexpected deployment call ${args.join(" ")}`);
          }
        },
        recovery,
      );
    } finally {
      rmSync(recovery, { recursive: true, force: true });
    }
  };
  return {
    run,
    steps,
    prepared() {
      assert.ok(template);
      assert.equal(pendingChecks.length, 0);
      return template;
    },
  };
}

const currentRead = ["get-caller-identity", "describe-stacks", "get-object"];
const approvedUpdate = [
  ...currentRead,
  "retire",
  "prepare",
  "describe-stacks",
  "describe-change-set",
  "get-template",
  "describe-stacks",
  "retire",
  "execute-change-set",
  "wait",
  "describe-stacks",
  "get-template",
];

interface Statement {
  readonly Action: string | string[];
  readonly Condition?: Record<string, Record<string, unknown>>;
  readonly Resource?: unknown;
}

function policy(template: Template) {
  const definition = Object.values(
    template.findResources("AWS::ECS::TaskDefinition"),
  );
  assert.equal(definition.length, 1);
  const [role] = definition[0].Properties.TaskRoleArn["Fn::GetAtt"] as string[];
  assert.match(role, /^ControllerTaskRole/u);
  return [
    ...Object.values(template.findResources("AWS::IAM::Policy")),
    ...Object.values(template.findResources("AWS::IAM::ManagedPolicy")),
  ]
    .filter((item) =>
      (item.Properties.Roles as { Ref?: string }[] | undefined)?.some(
        ({ Ref }) => Ref === role,
      ),
    )
    .flatMap((item) => item.Properties.PolicyDocument.Statement as Statement[]);
}

function grants(statements: readonly Statement[], actions: readonly string[]) {
  return statements.filter(({ Action }) =>
    (Array.isArray(Action) ? Action : [Action]).some((entry) =>
      actions.includes(entry),
    ),
  );
}

function resourceNames(template: Template, type: string) {
  return Object.keys(template.findResources(type));
}

function ecsTaskArn(clusterName: string) {
  return {
    "Fn::Join": [
      "",
      [
        "arn:",
        { Ref: "AWS::Partition" },
        `:ecs:${region}:${account}:task/${clusterName}/*`,
      ],
    ],
  };
}

function assertUnchangedEc2AndHost(before: Template, after: Template) {
  const ec2Actions = [
    "ec2:RunInstances",
    "ec2:CreateTags",
    "ec2:TerminateInstances",
    "iam:GetInstanceProfile",
  ];
  assert.ok(grants(policy(before), ec2Actions).length > 0);
  assert.deepEqual(
    grants(policy(after), ec2Actions),
    grants(policy(before), ec2Actions),
  );
  const hostCluster = resourceNames(before, "AWS::ECS::Cluster");
  assert.equal(hostCluster.length, 1);
  assert.deepEqual(resourceNames(after, "AWS::ECS::Cluster"), hostCluster);
  const hostFence = (template: Template) =>
    grants(policy(template), ["ecs:DescribeTasks"]).filter(
      (entry) =>
        typeof entry.Condition?.ArnEquals?.["ecs:cluster"] === "object",
    );
  assert.deepEqual(hostFence(before), [
    {
      Action: "ecs:DescribeTasks",
      Condition: {
        ArnEquals: { "ecs:cluster": { "Fn::GetAtt": [hostCluster[0], "Arn"] } },
      },
      Effect: "Allow",
      Resource: ecsTaskArn("engineering-dev-controller"),
    },
  ]);
  assert.deepEqual(hostFence(after), hostFence(before));
  assert.deepEqual(
    after.findResources("AWS::DynamoDB::Table"),
    before.findResources("AWS::DynamoDB::Table"),
  );
  for (const type of ["AWS::ECS::Service", "AWS::ECS::TaskDefinition"]) {
    const resources = resourceNames(before, type);
    assert.equal(resources.length, 1);
    assert.deepEqual(resourceNames(after, type), resources);
  }
  for (const type of [
    "AWS::EC2::VPC",
    "AWS::EC2::Subnet",
    "AWS::EC2::LaunchTemplate",
  ]) {
    after.resourceCountIs(type, 0);
  }
  after.hasResourceProperties("AWS::Events::Rule", {
    EventPattern: { source: ["aws.ec2"] },
  });
  after.hasResourceProperties("AWS::ECS::TaskDefinition", {
    ContainerDefinitions: [
      Match.objectLike({
        Command: Match.arrayWith(["--ec2-event-queue-url"]),
      }),
    ],
  });
}

function assertFargate(template: Template, ...profiles: RunnerProfile[]) {
  const statements = policy(template);
  const launch = grants(statements, ["ecs:RunTask"]);
  const terminate = grants(statements, ["ecs:StopTask"]);
  const tag = grants(statements, ["ecs:TagResource"]);
  assert.equal(launch.length, profiles.length);
  assert.equal(terminate.length, profiles.length);
  assert.equal(tag.length, profiles.length);
  template.resourceCountIs("AWS::Events::Rule", 1 + profiles.length);
  template.resourceCountIs("AWS::SQS::Queue", profiles.length ? 4 : 2);
  const commands = Object.values(
    template.findResources("AWS::ECS::TaskDefinition"),
  )[0].Properties.ContainerDefinitions[0].Command as unknown[];
  assert.equal(commands.includes("--ecs-event-queue-url"), profiles.length > 0);
  const bootstrapRoles = Object.entries(
    template.findResources("AWS::IAM::Policy"),
  )
    .filter(([id]) => id.startsWith("Bootstrap"))
    .flatMap(([, item]) =>
      (item.Properties.Roles as unknown[]).filter(
        (role) => typeof role === "string",
      ),
    )
    .sort();
  assert.deepEqual(
    bootstrapRoles,
    [ec2, ...profiles]
      .map(({ provisioner }) => provisioner.bootstrapRoleArn.split("/").at(-1))
      .sort(),
  );
  for (const profile of profiles) {
    assert.equal(profile.provisioner.type, "fargate");
    const provisioner = profile.provisioner;
    const cluster = source.spec.ecsClusters[provisioner.cluster];
    assert.equal(cluster.type, "import");
    const taskResource = ecsTaskArn(cluster.arn.split("/").at(-1)!);
    const ownership = (kind: "RequestTag" | "ResourceTag") => ({
      [`aws:${kind}/scaleset-aws:controller`]: "scaleset-dotnet-aws",
      [`aws:${kind}/scaleset-aws:deployment`]: deploymentId,
      [`aws:${kind}/scaleset-aws:profile`]: profile.id,
      [`aws:${kind}/scaleset-aws:bootstrap-protocol`]:
        provisioner.bootstrapProtocol,
    });
    assert.deepEqual(
      launch.find(({ Resource }) => Resource === provisioner.taskDefinitionArn),
      {
        Action: "ecs:RunTask",
        Effect: "Allow",
        Resource: provisioner.taskDefinitionArn,
        Condition: {
          ArnEquals: { "ecs:cluster": cluster.arn },
          StringEquals: ownership("RequestTag"),
          Bool: { "ecs:enable-execute-command": "false" },
          Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
        },
      },
    );
    const stop = terminate.find(
      ({ Condition }) => Condition?.ArnEquals?.["ecs:cluster"] === cluster.arn,
    );
    assert.deepEqual(stop, {
      Action: "ecs:StopTask",
      Effect: "Allow",
      Resource: taskResource,
      Condition: {
        ArnEquals: { "ecs:cluster": cluster.arn },
        StringEquals: ownership("ResourceTag"),
        Null: { "aws:ResourceTag/scaleset-aws:allocation": "false" },
      },
    });
    assert.deepEqual(
      tag.find(
        ({ Condition }) =>
          Condition?.StringEquals?.["aws:RequestTag/scaleset-aws:profile"] ===
          profile.id,
      ),
      {
        Action: "ecs:TagResource",
        Effect: "Allow",
        Resource: taskResource,
        Condition: {
          StringEquals: {
            ...ownership("RequestTag"),
            "ecs:CreateAction": "RunTask",
          },
          Null: { "aws:RequestTag/scaleset-aws:allocation": "false" },
        },
      },
    );
    template.hasResourceProperties("AWS::IAM::Policy", {
      Roles: [provisioner.bootstrapRoleArn.split("/").at(-1)],
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: "dynamodb:DeleteItem",
            Condition: {
              "ForAllValues:StringEquals": {
                "dynamodb:LeadingKeys": ["OFFER#${aws:userid}"],
              },
              Null: { "dynamodb:LeadingKeys": "false" },
            },
          }),
          Match.objectLike({
            Action: "lambda:InvokeFunctionUrl",
            Effect: "Allow",
            Condition: {
              StringEquals: {
                "lambda:FunctionUrlAuthType": "AWS_IAM",
              },
            },
          }),
        ]),
      },
    });
    template.hasResourceProperties("AWS::Events::Rule", {
      EventPattern: {
        source: ["aws.ecs"],
        detail: {
          clusterArn: [cluster.arn],
          taskDefinitionArn: [provisioner.taskDefinitionArn],
        },
      },
    });
  }
  const passedRoles = grants(statements, ["iam:PassRole"]).filter(
    ({ Condition }) =>
      Condition?.StringEquals?.["iam:PassedToService"] ===
      "ecs-tasks.amazonaws.com",
  );
  const expectedRoles = profiles
    .flatMap(({ provisioner }) => {
      assert.equal(provisioner.type, "fargate");
      return [provisioner.bootstrapRoleArn, provisioner.executionRoleArn];
    })
    .sort();
  assert.deepEqual(
    passedRoles.map(({ Resource }) => Resource),
    expectedRoles.length > 0 ? [expectedRoles] : [],
  );
}

test("adding a Fargate profile in YAML scopes its grants without replacing the EC2 controller", () => {
  const prior = config(ec2);
  const next = config(ec2, shell);
  const update = deployment(prior, next, "verified", "verified");
  update.run();
  assert.deepEqual(update.steps, approvedUpdate);
  const before = synthesize(prior);
  const after = update.prepared();
  assertFargate(before);
  assertFargate(after, shell);
  assertUnchangedEc2AndHost(before, after);
});

test("draining a profile in YAML updates its runtime state and preserves cleanup grants", () => {
  const prior = config(ec2, shell, arm);
  const next = config(ec2, drainingShell, arm);
  const update = deployment(prior, next, "verified", "verified");
  update.run();
  assert.deepEqual(update.steps, approvedUpdate);
  assert.equal(
    next.runtime.spec.profiles.find(({ id }) => id === shell.id)?.state,
    "draining",
  );
  const before = synthesize(prior);
  const after = update.prepared();
  assert.notEqual(prior.sha256, next.sha256);
  assertFargate(after, shell, arm);
  assertUnchangedEc2AndHost(before, after);
  assert.deepEqual(
    after.findResources("AWS::Events::Rule"),
    before.findResources("AWS::Events::Rule"),
  );
  assert.deepEqual(
    policy(after).filter(({ Action }) => Action !== "s3:GetObjectVersion"),
    policy(before).filter(({ Action }) => Action !== "s3:GetObjectVersion"),
  );
});

for (const retained of [[arm], []]) {
  test(
    retained.length
      ? "verified retirement prunes only the removed profile's grants"
      : "verified retirement of the final Fargate profile removes its queue and runner grants",
    () => {
      const prior = config(ec2, drainingShell, ...retained);
      const next = config(ec2, ...retained);
      const update = deployment(prior, next, "verified", "verified");
      update.run();
      assert.deepEqual(update.steps, approvedUpdate);
      const before = synthesize(prior);
      const after = update.prepared();
      assertFargate(before, shell, ...retained);
      assertFargate(after, ...retained);
      assertUnchangedEc2AndHost(before, after);
      const removed = JSON.stringify(after.toJSON().Resources);
      assert.doesNotMatch(
        removed,
        /engineering-shell|linux-shell-runner|fargate-shell-(?:bootstrap|execution)/u,
      );
      assert.equal(
        grants(policy(after), ["ecs:DescribeTasks"]).length,
        1 + retained.length,
      );
    },
  );
}

test("the exact retirement preflight blocks premature removal before preparation and before execution", () => {
  const prior = config(ec2, drainingShell, arm);
  const next = config(ec2, arm);
  const missing = deployment(prior, next, "retirement-required");
  assert.throws(missing.run, /Runtime retirement preflight failed/u);
  assert.deepEqual(missing.steps, [...currentRead, "retire"]);
  const recheck = deployment(prior, next, "verified", "retirement-required");
  assert.throws(recheck.run, /Runtime retirement preflight failed/u);
  assert.deepEqual(recheck.steps, [
    ...currentRead,
    "retire",
    "prepare",
    "describe-stacks",
    "describe-change-set",
    "get-template",
    "describe-stacks",
    "retire",
    "describe-stacks",
    "describe-change-set",
    "delete-change-set",
  ]);
});

test("the compiler rejects removing or disabling the last enabled profile", () => {
  assert.throws(() => config(), {
    name: "ConfigValidationError",
    path: "/spec/profiles",
  });
  assert.throws(() => config({ ...ec2, state: "disabled" }), {
    name: "ConfigValidationError",
    path: "/spec/profiles",
    message: "At least one profile must be active or draining.",
  });
});
