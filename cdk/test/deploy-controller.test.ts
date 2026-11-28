import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import { compileConfig } from "../lib/config/compiler.ts";
import { deployController as deploy } from "../lib/infrastructure/deploy-controller.ts";
import type { CommandResult } from "../lib/infrastructure/deploy-controller.ts";
import {
  controllerStackName,
  deploymentIdentity,
  trackingTableName,
} from "../lib/infrastructure/deployment-input.ts";
import {
  readAttempt,
  readDecision,
} from "../lib/infrastructure/deployment-recovery.ts";

const prior = compileConfig(
  readFileSync(
    new URL("../../testdata/config/mixed-ec2-fargate.yaml", import.meta.url),
    "utf8",
  ),
);
const stackName = controllerStackName(prior.runtime);
const environment = deploymentIdentity(prior.runtime);
const table = trackingTableName(prior.runtime);
const { account, region } = prior.runtime.spec.aws;
const scope = `arn:aws:cloudformation:${region}:${account}`;
const stackId = `${scope}:stack/${stackName}/fixture-stack`;
const version = "exact-prior-version";
const candidate = compileConfig(
  JSON.stringify({
    ...prior.runtime,
    kind: "RunnerController",
    spec: {
      ...prior.runtime.spec,
      profiles: prior.runtime.spec.profiles.slice(1),
    },
  }),
);
const testState = mkdtempSync(join(tmpdir(), "scaleset-deploy-test-"));
after(() => rmSync(testState, { recursive: true, force: true }));

function deployController(
  target: Parameters<typeof deploy>[0],
  run: Parameters<typeof deploy>[1],
  directory = testState,
) {
  return deploy(target, run, directory);
}

function ok(value: object = {}): CommandResult {
  return { status: 0, stdout: JSON.stringify(value), stderr: "" };
}

function failure(stderr: string): CommandResult {
  return { status: 1, stdout: "", stderr };
}
function serviceFailure(stderr: string): CommandResult {
  return { status: 254, stdout: "", stderr };
}

function option(args: string[], name: string): string {
  const index = args.indexOf(name);
  assert.ok(index >= 0 && index + 1 < args.length, name);
  return args[index + 1];
}

function fixture({
  deniedBefore = false,
  deniedAfter = false,
  stackMissing = false,
  unknownStack = false,
  wrongTable = false,
  wrongBody = false,
  changedVersion = false,
  expectedFargatePreflight = undefined as string | undefined,
  deployed = prior,
  proposed = candidate,
  prepareLost = false,
  executeLost = false,
  templateDrift = false,
  importedState = 0,
  wrongAbsenceCode = false,
  tableAppearsAfterPrepare = false,
  keepRollback = false,
  replacedBeforeCleanup = false,
  afterPrepare = () => {},
} = {}) {
  const steps: string[] = [];
  let count = 0;
  let prepared: { name: string; id: string; template: unknown } | undefined;
  let reviewExists = false;
  let executed = false;
  let complete = false;
  let executionToken: string | undefined;
  let prepareAttempts = 0;
  let tableChecks = 0;
  let currentStackId = stackId;
  let generation = "2";
  const changeSetNames: string[] = [];
  const run = (program: string, args: string[]): CommandResult => {
    if (program === "dotnet") {
      steps.push("preflight");
      assert.equal(args[0], "run");
      assert.match(
        option(args, "--project"),
        /Knightfall.ScaleSet.Aws.Controller\.csproj$/u,
      );
      assert.deepEqual(
        args.slice(args.indexOf("--") + 1, args.indexOf("--") + 3),
        ["preflight", "retire"],
      );
      assert.equal(
        readFileSync(option(args, "--current-config"), "utf8"),
        deployed.json,
      );
      assert.equal(
        readFileSync(option(args, "--config"), "utf8"),
        proposed.json,
      );
      assert.equal(option(args, "--current-sha256"), deployed.sha256);
      assert.equal(option(args, "--sha256"), proposed.sha256);
      assert.equal(option(args, "--current-generation"), "1");
      assert.equal(option(args, "--generation"), generation);
      count++;
      return (count === 1 && deniedBefore) || (count === 2 && deniedAfter)
        ? failure("deployment preflight refused the binding")
        : ok();
    }
    if (program === process.execPath) {
      steps.push("prepare");
      assert.ok(args[0].endsWith("/aws-cdk/bin/cdk"));
      assert.equal(args[1], "deploy");
      assert.equal(option(args, "--method"), "prepare-change-set");
      const assembly = option(args, "--app");
      const manifest = JSON.parse(
        readFileSync(`${assembly}/manifest.json`, "utf8"),
      );
      assert.equal(
        manifest.artifacts[args.at(-1)!].properties.stackName,
        stackName,
      );
      const compiled = JSON.parse(
        readFileSync(`${assembly}/Controller.template.json`, "utf8"),
      );
      assert.equal(compiled.Outputs.RuntimeConfigSha256.Value, proposed.sha256);
      assert.equal(compiled.Outputs.DeploymentGeneration.Value, generation);
      const task = Object.values(compiled.Resources).find(
        (resource) =>
          (resource as { Type: string }).Type === "AWS::ECS::TaskDefinition",
      ) as {
        Properties: { ContainerDefinitions: [{ Environment?: unknown }] };
      };
      assert.deepEqual(task.Properties.ContainerDefinitions[0].Environment, [
        { Name: "SCALESET_RUNTIME_BUCKET_OWNER", Value: account },
        ...(expectedFargatePreflight === undefined
          ? []
          : [
              {
                Name: "SCALESET_FARGATE_PREFLIGHT",
                Value: expectedFargatePreflight,
              },
            ]),
      ]);
      const name = option(args, "--change-set-name");
      assert.notEqual(name, prepared?.name);
      changeSetNames.push(name);
      prepared = {
        name,
        id: `${scope}:changeSet/${name}/exact-id`,
        template: compiled,
      };
      reviewExists = true;
      prepareAttempts++;
      afterPrepare();
      return prepareLost && prepareAttempts === 1
        ? failure("lost prepare reply")
        : ok();
    }

    assert.equal(program, "aws");
    assert.equal(option(args, "--region"), region);
    if (args.includes("get-caller-identity")) {
      steps.push("identity");
      return ok({ Account: account });
    }
    if (args.includes("describe-stacks")) {
      steps.push("stack");
      assert.ok(
        [stackName, currentStackId].includes(option(args, "--stack-name")),
      );
      if (unknownStack) return failure("AccessDenied");
      if (stackMissing && !reviewExists) {
        const message = `An error occurred (ValidationError) when calling the DescribeStacks operation: Stack with id ${stackName} does not exist`;
        return wrongAbsenceCode ? failure(message) : serviceFailure(message);
      }
      if (stackMissing && !executed) {
        return ok({
          Stacks: [
            {
              StackName: stackName,
              StackId: currentStackId,
              StackStatus: "REVIEW_IN_PROGRESS",
            },
          ],
        });
      }
      const deployedResult = complete && !keepRollback ? proposed : deployed;
      const outputs = {
        DeploymentIdentity: environment,
        TrackingTableName: wrongTable ? "some-other-table" : table,
        RuntimeConfigUri: `s3://prior-assets/runtime/${deployedResult.sha256}.json`,
        RuntimeConfigVersionId: version,
        RuntimeConfigSha256: deployedResult.sha256,
        DeploymentGeneration: complete && !keepRollback ? generation : "1",
      };
      return ok({
        Stacks: [
          {
            StackName: stackName,
            StackId:
              replacedBeforeCleanup && count >= 2
                ? `${scope}:stack/${stackName}/other-stack`
                : currentStackId,
            StackStatus:
              complete && !keepRollback
                ? stackMissing
                  ? "CREATE_COMPLETE"
                  : "UPDATE_COMPLETE"
                : keepRollback && complete
                  ? "UPDATE_ROLLBACK_COMPLETE"
                  : executed
                    ? stackMissing
                      ? "CREATE_IN_PROGRESS"
                      : "UPDATE_IN_PROGRESS"
                    : "CREATE_COMPLETE",
            Outputs: Object.entries(outputs).map(
              ([OutputKey, OutputValue]) => ({ OutputKey, OutputValue }),
            ),
          },
        ],
      });
    }
    if (
      args.includes("list-stack-resources") ||
      args.includes("list-change-sets")
    ) {
      assert.equal(option(args, "--stack-name"), currentStackId);
      assert.equal(args.includes("--no-paginate"), false);
      assert.equal(args.includes("--max-items"), false);
      assert.equal(reviewExists && !executed, true);
      if (args.includes("list-stack-resources")) {
        steps.push("resources");
        return ok({ StackResourceSummaries: [], NextToken: null });
      }
      steps.push("change-sets");
      return ok({ Summaries: prepared ? [{ ChangeSetId: prepared.id }] : [] });
    }
    if (args.includes("describe-table")) {
      steps.push("table");
      tableChecks++;
      assert.equal(option(args, "--table-name"), table);
      if (tableAppearsAfterPrepare && tableChecks === 2) {
        return ok({ Table: { TableName: table } });
      }
      if (proposed.runtime.spec.tracking.table.type === "import") {
        return ok({
          Table: {
            TableArn: `arn:aws:dynamodb:${region}:${account}:table/${table}`,
            TableName: table,
            TableStatus: "ACTIVE",
          },
        });
      }
      return serviceFailure(
        "An error occurred (ResourceNotFoundException) when calling the DescribeTable operation: Not found",
      );
    }
    if (args.includes("query")) {
      steps.push("catalog");
      assert.equal(option(args, "--table-name"), table);
      assert.ok(args.includes("--consistent-read"));
      return ok({ Count: importedState });
    }
    if (args.includes("get-object")) {
      steps.push("source");
      assert.equal(option(args, "--version-id"), version);
      assert.equal(option(args, "--expected-bucket-owner"), account);
      writeFileSync(args.at(-1)!, wrongBody ? proposed.json : deployed.json);
      return ok({ VersionId: changedVersion ? "another-version" : version });
    }
    if (args.includes("describe-change-set")) {
      steps.push("change-set");
      assert.equal(option(args, "--stack-name"), currentStackId);
      const requested = option(args, "--change-set-name");
      if (
        !prepared ||
        (requested !== prepared.name && requested !== prepared.id)
      ) {
        return serviceFailure(
          "An error occurred (ChangeSetNotFound) when calling the DescribeChangeSet operation: Not found",
        );
      }
      return ok({
        Status: "CREATE_COMPLETE",
        ExecutionStatus: executed ? "EXECUTE_IN_PROGRESS" : "AVAILABLE",
        StackId: currentStackId,
        StackName: stackName,
        ChangeSetId: prepared.id,
      });
    }
    if (args.includes("get-template")) {
      steps.push("template");
      assert.equal(option(args, "--stack-name"), currentStackId);
      assert.equal(option(args, "--template-stage"), "Original");
      assert.ok(prepared);
      if (args.includes("--change-set-name")) {
        assert.equal(option(args, "--change-set-name"), prepared.id);
      }
      return ok({
        TemplateBody: templateDrift
          ? { template: "another" }
          : prepared.template,
      });
    }
    if (args.includes("delete-change-set")) {
      steps.push("delete-exact-change-set");
      assert.ok(prepared);
      assert.equal(option(args, "--stack-name"), currentStackId);
      assert.equal(option(args, "--change-set-name"), prepared.id);
      assert.equal(executed, false);
      prepared = undefined;
      return ok();
    }
    if (args.includes("execute-change-set")) {
      steps.push("execute");
      assert.match(
        option(args, "--change-set-name"),
        /:changeSet\/scaleset-[a-f0-9-]+\/exact-id$/u,
      );
      assert.equal(option(args, "--stack-name"), currentStackId);
      const token = option(args, "--client-request-token");
      assert.equal(token, prepared?.name);
      executionToken = token;
      executed = true;
      return executeLost ? failure("lost execute reply") : ok();
    }
    if (args.includes("wait")) {
      steps.push("wait");
      assert.ok(
        args.includes(
          stackMissing ? "stack-create-complete" : "stack-update-complete",
        ),
      );
      assert.equal(option(args, "--stack-name"), currentStackId);
      assert.ok(executionToken);
      complete = true;
      return ok();
    }
    throw new Error(`Unexpected operation ${args.join(" ")}`);
  };
  function operatorRemovesEmptyReview(id: string, correctedGeneration: string) {
    assert.equal(id, currentStackId);
    assert.ok(reviewExists && !executed && prepared === undefined);
    reviewExists = false;
    currentStackId = `${scope}:stack/${stackName}/replacement-stack`;
    generation = correctedGeneration;
  }
  return { run, steps, changeSetNames, operatorRemovesEmptyReview };
}

test("checks exact current and candidate bytes before preparation and again before exact execution", () => {
  const cloud = fixture();
  deployController({ compiled: candidate, generation: "2" }, cloud.run);
  assert.deepEqual(cloud.steps, [
    "identity",
    "stack",
    "source",
    "preflight",
    "prepare",
    "stack",
    "change-set",
    "template",
    "stack",
    "preflight",
    "execute",
    "wait",
    "stack",
    "template",
  ]);
});

test("the protected deploy rechecks the published bootstrap object's exact key before execution", () => {
  const key = `arn:aws:kms:${region}:${account}:key/00000000-0000-0000-0000-000000000001`;
  for (const matches of [true, false]) {
    const cloud = fixture();
    const steps: string[] = [];
    const run = (program: string, args: string[]) => {
      if (args.includes("get-bucket-encryption")) {
        steps.push("bucket");
        assert.ok(!cloud.steps.includes("prepare"));
        assert.equal(option(args, "--expected-bucket-owner"), account);
        return ok({
          ServerSideEncryptionConfiguration: {
            Rules: [
              {
                ApplyServerSideEncryptionByDefault: {
                  SSEAlgorithm: "aws:kms",
                  KMSMasterKeyID: key,
                },
              },
            ],
          },
        });
      }
      if (args.includes("describe-key")) {
        steps.push("key");
        assert.equal(option(args, "--key-id"), key);
        return ok({
          KeyMetadata: {
            Arn: key,
            Enabled: true,
            KeyState: "Enabled",
            KeySpec: "SYMMETRIC_DEFAULT",
            KeyUsage: "ENCRYPT_DECRYPT",
            KeyManager: "CUSTOMER",
          },
        });
      }
      if (args.includes("head-object")) {
        steps.push("object");
        assert.ok(cloud.steps.includes("prepare"));
        assert.ok(!cloud.steps.includes("execute"));
        assert.equal(option(args, "--expected-bucket-owner"), account);
        assert.match(option(args, "--key"), /^[a-f0-9]{64}\.json$/u);
        return ok({
          ServerSideEncryption: "aws:kms",
          SSEKMSKeyId: matches ? key : key.replace(/1$/u, "2"),
        });
      }
      return cloud.run(program, args);
    };
    const deploy = () =>
      deployController(
        {
          compiled: candidate,
          generation: "2",
          encryption: { bootstrapAssetKeyArn: key },
        },
        run,
      );
    if (matches) deploy();
    else
      assert.throws(deploy, /CDK bootstrap source asset must use the pinned/u);
    assert.deepEqual(
      steps,
      matches
        ? ["bucket", "key", "object", "key"]
        : ["bucket", "key", "object"],
    );
    assert.equal(cloud.steps.includes("execute"), matches);
  }
});

test("explicit Fargate evidence reaches the deployment artifact after early validation", () => {
  const evidence = JSON.stringify({
    profiles: {
      "linux-shell": {
        image: `runner@sha256:${"a".repeat(64)}`,
        entryPoint: ["/opt/scaleset/RunnerBootstrap"],
        command: ["fargate", "/opt/scaleset/fargate-bootstrap.json"],
      },
    },
  });
  const cloud = fixture({ expectedFargatePreflight: evidence });
  deployController(
    { compiled: candidate, generation: "2", fargatePreflight: evidence },
    cloud.run,
  );
  assert.ok(cloud.steps.includes("prepare"));

  const invalid = fixture();
  assert.throws(
    () =>
      deployController(
        {
          compiled: candidate,
          generation: "2",
          fargatePreflight: '{"profiles":{"unrelated":{}}}',
        },
        invalid.run,
      ),
    /SCALESET_FARGATE_PREFLIGHT is invalid/u,
  );
  assert.deepEqual(invalid.steps, []);
});

test("a denied preflight before or after preparing never executes the change set", () => {
  for (const [options, permittedPreparation] of [
    [{ deniedBefore: true }, false],
    [{ deniedAfter: true }, true],
  ] as const) {
    const cloud = fixture(options);
    assert.throws(
      () =>
        deployController({ compiled: candidate, generation: "2" }, cloud.run),
      /retirement preflight failed/u,
    );
    assert.equal(cloud.steps.includes("prepare"), permittedPreparation);
    assert.ok(!cloud.steps.includes("execute"));
    assert.equal(
      cloud.steps.includes("delete-exact-change-set"),
      permittedPreparation,
    );
  }
});

test("the protected deploy holds an alpha.1 hosting move or STATUS grant removal with a retained table", () => {
  const { spec } = prior.runtime;
  const ec2 = spec.profiles[1];
  assert.ok(ec2.provisioner.type === "ec2");
  const qualified = {
    ...ec2,
    provisioner: {
      ...ec2.provisioner,
      statusQualification: {
        imageId: "ami-0123456789abcdef0",
        roleId: "AROAFIXTUREBOOTSTRAP01",
        evidenceSha256: "b".repeat(64),
      },
    },
  };
  const retained = {
    ...spec,
    tracking: { table: { type: "import", name: table } },
    profiles: [qualified],
  };
  const compile = (configuration: object) =>
    compileConfig(
      JSON.stringify({
        ...prior.runtime,
        kind: "RunnerController",
        spec: configuration,
      }),
    );
  const deployed = compile(retained);
  for (const changed of [
    {
      ...retained,
      controller: {
        ...spec.controller,
        hosting: { ...spec.controller.hosting, cluster: "runner-shell" },
      },
    },
    { ...retained, profiles: [ec2] },
  ]) {
    const proposed = compile(changed);
    const cloud = fixture({ deployed, proposed, deniedBefore: true });
    assert.throws(
      () =>
        deployController({ compiled: proposed, generation: "2" }, cloud.run),
      /retirement preflight failed/u,
    );
    assert.deepEqual(cloud.steps, ["identity", "stack", "source", "preflight"]);
  }
});

test("unverified current stack, table, object version or digest cannot reach preparation", () => {
  for (const options of [
    { wrongTable: true },
    { wrongBody: true },
    { changedVersion: true },
    { unknownStack: true },
    { wrongAbsenceCode: true, stackMissing: true },
  ]) {
    const cloud = fixture(options);
    assert.throws(
      () =>
        deployController({ compiled: candidate, generation: "2" }, cloud.run),
      /could not establish|could not be verified|differ|different|already exists/iu,
    );
    assert.ok(!cloud.steps.includes("preflight"));
    assert.ok(!cloud.steps.includes("prepare"));
  }
});

test("a fresh deployment needs confirmed stack and tracking-table absence", () => {
  const cloud = fixture({ stackMissing: true });
  deployController({ compiled: candidate, generation: "2" }, cloud.run);
  assert.deepEqual(cloud.steps, [
    "identity",
    "stack",
    "table",
    "prepare",
    "stack",
    "change-set",
    "template",
    "stack",
    "table",
    "execute",
    "wait",
    "stack",
    "template",
  ]);
});

function newRecoveryDirectory() {
  return mkdtempSync(join(testState, "attempt-"));
}

function recoveryRecord(directory: string) {
  const names = readdirSync(directory).filter((name) => name.endsWith(".json"));
  assert.equal(names.length, 1);
  return join(directory, names[0]);
}

test("resumes an interrupted fresh prepare using the exact owned review stack and template", () => {
  const cloud = fixture({ stackMissing: true, prepareLost: true });
  const recovery = newRecoveryDirectory();
  const target = { compiled: candidate, generation: "2" };
  assert.throws(
    () => deployController(target, cloud.run, recovery),
    /preparation failed/u,
  );
  const resume = recoveryRecord(recovery);
  const before = cloud.steps.length;
  assert.throws(
    () =>
      deployController(
        { ...target, generation: "3", resume },
        cloud.run,
        recovery,
      ),
    /does not match the exact reviewed deployment inputs/u,
  );
  assert.deepEqual(cloud.steps.slice(before), ["identity", "stack"]);
  deployController({ ...target, resume }, cloud.run, recovery);
  assert.equal(cloud.steps.filter((step) => step === "prepare").length, 1);
  assert.equal(cloud.steps.filter((step) => step === "execute").length, 1);
  assert.ok(!cloud.steps.includes("delete-exact-change-set"));
  assert.deepEqual(readdirSync(recovery), []);
});

test("recovers a lost execute reply without re-preparing or interpreting rollback as success", () => {
  const target = { compiled: candidate, generation: "2" };
  for (const keepRollback of [false, true]) {
    const cloud = fixture({ executeLost: true, keepRollback });
    const recovery = newRecoveryDirectory();
    assert.throws(
      () => deployController(target, cloud.run, recovery),
      /execution failed/u,
    );
    const resume = recoveryRecord(recovery);
    const previous = cloud.steps.length;
    if (keepRollback) {
      assert.throws(
        () => deployController({ ...target, resume }, cloud.run, recovery),
        /does not confirm the exact candidate/u,
      );
      assert.equal(recoveryRecord(recovery), resume);
    } else {
      deployController({ ...target, resume }, cloud.run, recovery);
      assert.deepEqual(readdirSync(recovery), []);
    }
    assert.equal(cloud.steps.filter((step) => step === "prepare").length, 1);
    assert.equal(cloud.steps.filter((step) => step === "execute").length, 1);
    assert.deepEqual(cloud.steps.slice(previous, previous + 4), [
      "identity",
      "stack",
      "change-set",
      "wait",
    ]);
    assert.ok(!cloud.steps.includes("delete-exact-change-set"));
  }
});

test("concurrent first applies preserve the first deployment's review stack", () => {
  const target = { compiled: candidate, generation: "2" };
  const other = newRecoveryDirectory();
  const first = fixture({
    stackMissing: true,
    afterPrepare: () => {
      assert.throws(
        () => deployController(target, first.run, other),
        /belongs to another deployment attempt/u,
      );
      assert.deepEqual(readdirSync(other), []);
    },
  });
  deployController(target, first.run, newRecoveryDirectory());
  assert.equal(first.steps.filter((step) => step === "prepare").length, 1);
  assert.equal(first.steps.filter((step) => step === "execute").length, 1);
  assert.ok(!first.steps.includes("delete-exact-change-set"));
});

test("a denied fresh apply discards only its change set and reuses its exact review stack", () => {
  const cloud = fixture({ stackMissing: true, tableAppearsAfterPrepare: true });
  const recovery = newRecoveryDirectory();
  const target = { compiled: candidate, generation: "2" };
  assert.throws(
    () => deployController(target, cloud.run, recovery),
    /tracking table is present or could not be verified absent/u,
  );
  const resume = recoveryRecord(recovery);
  assert.deepEqual(cloud.steps.slice(-3), [
    "stack",
    "change-set",
    "delete-exact-change-set",
  ]);
  assert.ok(!cloud.steps.includes("execute"));
  deployController({ ...target, resume }, cloud.run, recovery);
  assert.equal(cloud.changeSetNames.length, 2);
  assert.notEqual(cloud.changeSetNames[0], cloud.changeSetNames[1]);
  assert.equal(cloud.steps.filter((step) => step === "execute").length, 1);
  assert.equal(
    readdirSync(recovery).filter((name) => name.endsWith(".json")).length,
    0,
  );
});

test("inspect-review attributes an empty placeholder without mutation and permits a corrected first deploy after operator removal", () => {
  const cloud = fixture({ stackMissing: true, tableAppearsAfterPrepare: true });
  const recovery = newRecoveryDirectory();
  const original = { compiled: candidate, generation: "2" };
  assert.throws(
    () => deployController(original, cloud.run, recovery),
    /tracking table is present/u,
  );
  const journal = recoveryRecord(recovery);
  const corrected = { ...original, generation: "3" };
  assert.throws(
    () => deployController(corrected, cloud.run, recovery),
    /unstable or belongs to another deployment attempt/u,
  );
  assert.throws(
    () =>
      deployController({ ...corrected, resume: journal }, cloud.run, recovery),
    /does not match the exact reviewed deployment inputs/u,
  );
  const before = cloud.steps.length;
  const verified = deployController(
    { ...original, inspectReview: journal },
    cloud.run,
    recovery,
  );
  assert.equal(verified, stackId);
  assert.deepEqual(cloud.steps.slice(before), [
    "identity",
    "stack",
    "resources",
    "change-sets",
    "stack",
  ]);

  cloud.operatorRemovesEmptyReview(verified, corrected.generation);
  deployController(corrected, cloud.run, recovery);
  assert.equal(cloud.changeSetNames.length, 2);
  assert.equal(cloud.steps.filter((step) => step === "execute").length, 1);
  assert.equal(recoveryRecord(recovery), journal);
  assert.equal(readDecision(journal, readAttempt(journal).name), "discard");
});

test("inspect-review holds an unattributed, competed, nonempty, or executing stack", () => {
  const cloud = fixture({ stackMissing: true, tableAppearsAfterPrepare: true });
  const recovery = newRecoveryDirectory();
  const original = { compiled: candidate, generation: "2" };
  assert.throws(
    () => deployController(original, cloud.run, recovery),
    /tracking table is present/u,
  );
  const journal = recoveryRecord(recovery);
  const attempt = readAttempt(journal);
  const inspect = { ...original, inspectReview: journal };
  const overrideStack = (changes: Record<string, unknown>) =>
    ok({
      Stacks: [
        {
          StackName: stackName,
          StackId: stackId,
          StackStatus: "REVIEW_IN_PROGRESS",
          ...changes,
        },
      ],
    });
  for (const [operation, reply, expected] of [
    [
      "describe-stacks",
      overrideStack({ StackId: `${scope}:stack/${stackName}/other` }),
      /journal does not identify/u,
    ],
    [
      "describe-stacks",
      overrideStack({
        Outputs: [{ OutputKey: "fixture", OutputValue: "present" }],
      }),
      /journal does not identify/u,
    ],
    [
      "describe-stacks",
      overrideStack({ StackStatus: "CREATE_IN_PROGRESS" }),
      /journal does not identify/u,
    ],
    [
      "describe-stacks",
      overrideStack({ ParentId: "nested" }),
      /journal does not identify/u,
    ],
    [
      "list-stack-resources",
      ok({ StackResourceSummaries: [{ LogicalResourceId: "resource" }] }),
      /present or unverified/u,
    ],
    [
      "list-change-sets",
      ok({ Summaries: [{ ChangeSetId: "competing" }] }),
      /present or unverified/u,
    ],
    [
      "list-change-sets",
      ok({ Summaries: [], NextToken: "next" }),
      /present or unverified/u,
    ],
    [
      "list-change-sets",
      { ...ok(), stdout: "private fixture response" },
      /^Error: Deployment received a malformed AWS response\. Recovery record:/u,
    ],
  ] as const) {
    const before = cloud.steps.length;
    assert.throws(
      () =>
        deployController(
          inspect,
          (program, args) => {
            assert.equal(program, "aws");
            if (args.includes(operation)) return reply;
            return cloud.run(program, args);
          },
          recovery,
        ),
      expected,
    );
    assert.ok(
      cloud.steps
        .slice(before)
        .every((step) =>
          ["identity", "stack", "resources", "change-sets"].includes(step),
        ),
    );
  }
  const before = cloud.steps.length;
  assert.throws(
    () =>
      deployController(
        inspect,
        (program, args) =>
          args.includes("describe-stacks") &&
          option(args, "--stack-name") === stackId
            ? overrideStack({ StackStatus: "CREATE_IN_PROGRESS" })
            : cloud.run(program, args),
        recovery,
      ),
    /changed during inspection/u,
  );
  assert.deepEqual(cloud.steps.slice(before), [
    "identity",
    "stack",
    "resources",
    "change-sets",
  ]);

  const decision = `${journal}.${attempt.name}.decision`;
  writeFileSync(decision, '"execute"');
  assert.throws(
    () => deployController(inspect, cloud.run, recovery),
    /journal does not identify/u,
  );
  rmSync(decision);
  assert.throws(
    () => deployController(inspect, cloud.run, recovery),
    /journal does not identify/u,
  );
  writeFileSync(decision, '"discard"', { mode: 0o600 });
  assert.throws(
    () =>
      deployController(
        inspect,
        (program, args) => {
          const response = cloud.run(program, args);
          if (args.includes("list-change-sets")) rmSync(decision);
          return response;
        },
        recovery,
      ),
    /recovery evidence changed during inspection/u,
  );

  writeFileSync(decision, '"discard"', { mode: 0o600 });
  writeFileSync(
    journal,
    JSON.stringify({ ...attempt, stackId: null, changeSetId: null }),
  );
  assert.throws(
    () => deployController(inspect, cloud.run, recovery),
    /journal does not identify/u,
  );
});

test("cleanup holds a prepared change set if the authoritative stack identity is replaced", () => {
  const cloud = fixture({ deniedAfter: true, replacedBeforeCleanup: true });
  const recovery = newRecoveryDirectory();
  assert.throws(
    () =>
      deployController(
        { compiled: candidate, generation: "2" },
        cloud.run,
        recovery,
      ),
    /original stack could not be verified for change set cleanup/u,
  );
  assert.ok(recoveryRecord(recovery));
  assert.ok(!cloud.steps.includes("delete-exact-change-set"));
  assert.ok(!cloud.steps.includes("execute"));
});

test("a fresh imported table is checked before preparation and execution without overwriting existing state", () => {
  const imported = compileConfig(
    JSON.stringify({
      ...candidate.runtime,
      kind: "RunnerController",
      spec: {
        ...candidate.runtime.spec,
        tracking: { table: { type: "import", name: table } },
      },
    }),
  );
  const empty = fixture({ stackMissing: true, proposed: imported });
  deployController(
    { compiled: imported, generation: "2" },
    empty.run,
    newRecoveryDirectory(),
  );
  assert.equal(empty.steps.filter((step) => step === "catalog").length, 2);
  assert.ok(empty.steps.includes("execute"));
  const occupied = fixture({
    stackMissing: true,
    proposed: imported,
    importedState: 1,
  });
  assert.throws(
    () =>
      deployController(
        { compiled: imported, generation: "2" },
        occupied.run,
        newRecoveryDirectory(),
      ),
    /already holds state/u,
  );
  assert.deepEqual(occupied.steps, ["identity", "stack", "table", "catalog"]);
});

test("deployment redacts malformed AWS response text and holds execution", () => {
  const imported = compileConfig(
    JSON.stringify({
      ...candidate.runtime,
      kind: "RunnerController",
      spec: {
        ...candidate.runtime.spec,
        tracking: { table: { type: "import", name: table } },
      },
    }),
  );
  const privateText = "private fixture response";
  const invalid = { ...ok(), stdout: privateText };
  for (const [operation, reply, proposed] of [
    ["describe-change-set", invalid, candidate],
    ["get-template", ok({ TemplateBody: privateText }), candidate],
    ["describe-table", invalid, imported],
  ] as const) {
    const cloud = fixture({ stackMissing: true, proposed });
    let reached = false;
    assert.throws(
      () =>
        deployController(
          { compiled: proposed, generation: "2" },
          (program, args) => {
            const result = cloud.run(program, args);
            if (!args.includes(operation)) return result;
            reached = true;
            return reply;
          },
          newRecoveryDirectory(),
        ),
      (error: Error) => {
        assert.match(
          error.message,
          /^Deployment received a malformed AWS response\./u,
        );
        assert.ok(!error.message.includes(privateText));
        return true;
      },
    );
    assert.ok(reached, operation);
    assert.ok(!cloud.steps.includes("execute"));
    assert.ok(!cloud.steps.includes("delete-exact-change-set"));
  }
});

test("deployment redacts malformed prior runtime JSON even when its digest matches", () => {
  const priorJson = "private fixture runtime";
  const deployed = {
    ...prior,
    json: priorJson,
    sha256: createHash("sha256").update(priorJson).digest("hex"),
  };
  const cloud = fixture({ deployed });
  assert.throws(
    () =>
      deployController(
        { compiled: candidate, generation: "2" },
        cloud.run,
        newRecoveryDirectory(),
      ),
    { message: "Deployment received a malformed AWS response." },
  );
  assert.deepEqual(cloud.steps, ["identity", "stack", "source"]);
});

test("an authoritative change set with different template bytes is held for recovery", () => {
  const cloud = fixture({ templateDrift: true });
  const recovery = newRecoveryDirectory();
  assert.throws(
    () =>
      deployController(
        { compiled: candidate, generation: "2" },
        cloud.run,
        recovery,
      ),
    /template differs from the reviewed deployment/u,
  );
  assert.ok(recoveryRecord(recovery));
  assert.ok(!cloud.steps.includes("delete-exact-change-set"));
  assert.ok(!cloud.steps.includes("execute"));
});

test("deploy, synth and emission reject the same malformed UTF-8 and deployment names", () => {
  const source = new URL(
    "../../testdata/config/mixed-ec2-fargate.yaml",
    import.meta.url,
  );
  const valid = readFileSync(source);
  const work = newRecoveryDirectory();
  const file = join(work, "config.yaml");
  const output = join(work, "runtime.json");
  const invalid = [
    {
      bytes: Buffer.concat([
        valid,
        Buffer.from([0x23, 0x20, 0xc0, 0xaf, 0x0a]),
      ]),
      reason: /valid UTF-8/u,
    },
    ...["0name", "has.dot", "has_underscore", "a".repeat(111)].map((name) => ({
      bytes: Buffer.from(
        valid.toString().replace("name: engineering-dev", `name: ${name}`),
      ),
      reason: /must match pattern/u,
    })),
    {
      bytes: Buffer.from(
        valid
          .toString()
          .replace(
            "/runner-linux-bootstrap\n        bootstrapProtocol",
            "/runner-linux-bootstrap/\n        bootstrapProtocol",
          ),
      ),
      reason: /must match pattern/u,
    },
  ];
  for (const { bytes, reason } of invalid) {
    writeFileSync(file, bytes);
    for (const entry of ["controller", "deploy-controller", "emit-runtime"]) {
      const args =
        entry === "emit-runtime"
          ? [file, output]
          : ["--config", file, "--generation", "2"];
      const result = spawnSync(
        process.execPath,
        [new URL(`../bin/${entry}.ts`, import.meta.url).pathname, ...args],
        { cwd: work, encoding: "utf8" },
      );
      assert.equal(result.status, 1, entry);
      assert.match(result.stderr, reason, entry);
    }
  }
  assert.throws(() => readFileSync(output), { code: "ENOENT" });
  assert.equal(
    compileConfig(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), valid]))
      .sha256,
    prior.sha256,
  );
  const maxName = compileConfig(
    valid
      .toString()
      .replace("name: engineering-dev", `name: ${"a".repeat(110)}`),
  );
  assert.equal(controllerStackName(maxName.runtime).length, 128);
});

test("the executable command rejects a verifier or proof override before starting any process", () => {
  for (const flag of [
    "--controller",
    "--retirement-proof",
    "--skip-retirement",
  ]) {
    assert.throws(
      () =>
        execFileSync(
          process.execPath,
          [
            new URL("../bin/deploy-controller.ts", import.meta.url).pathname,
            "--config",
            "unused.yaml",
            "--generation",
            "2",
            flag,
            "fixture",
          ],
          { stdio: "pipe" },
        ),
      /Unknown option/u,
    );
  }
});
