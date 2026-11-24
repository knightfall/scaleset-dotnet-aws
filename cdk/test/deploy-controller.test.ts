import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { test } from "node:test";
import { compileConfig } from "../lib/config/compiler.ts";
import { deployController } from "../lib/infrastructure/deploy-controller.ts";
import type { CommandResult } from "../lib/infrastructure/deploy-controller.ts";
import {
  controllerStackName,
  deploymentIdentity,
  trackingTableName,
} from "../lib/infrastructure/deployment-input.ts";

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

function ok(value: object = {}): CommandResult {
  return { status: 0, stdout: JSON.stringify(value), stderr: "" };
}

function failure(stderr: string): CommandResult {
  return { status: 1, stdout: "", stderr };
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
} = {}) {
  const steps: string[] = [];
  let count = 0;
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
        prior.json,
      );
      assert.equal(
        readFileSync(option(args, "--config"), "utf8"),
        candidate.json,
      );
      assert.equal(option(args, "--current-sha256"), prior.sha256);
      assert.equal(option(args, "--sha256"), candidate.sha256);
      assert.equal(option(args, "--current-generation"), "1");
      assert.equal(option(args, "--generation"), "2");
      count++;
      return (count === 1 && deniedBefore) || (count === 2 && deniedAfter)
        ? failure("tombstone missing")
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
      assert.equal(
        compiled.Outputs.RuntimeConfigSha256.Value,
        candidate.sha256,
      );
      assert.equal(compiled.Outputs.DeploymentGeneration.Value, "2");
      return ok();
    }

    assert.equal(program, "aws");
    assert.equal(option(args, "--region"), region);
    if (args.includes("get-caller-identity")) {
      steps.push("identity");
      return ok({ Account: account });
    }
    if (args.includes("describe-stacks")) {
      steps.push("stack");
      if (unknownStack) return failure("AccessDenied");
      if (stackMissing) {
        return failure(
          `An error occurred (ValidationError) when calling the DescribeStacks operation: Stack with id ${stackName} does not exist`,
        );
      }
      const outputs = {
        DeploymentIdentity: environment,
        TrackingTableName: wrongTable ? "some-other-table" : table,
        RuntimeConfigUri: `s3://prior-assets/runtime/${prior.sha256}.json`,
        RuntimeConfigVersionId: version,
        RuntimeConfigSha256: prior.sha256,
        DeploymentGeneration: "1",
      };
      return ok({
        Stacks: [
          {
            StackName: stackName,
            StackId: stackId,
            StackStatus: "CREATE_COMPLETE",
            Outputs: Object.entries(outputs).map(
              ([OutputKey, OutputValue]) => ({ OutputKey, OutputValue }),
            ),
          },
        ],
      });
    }
    if (args.includes("describe-table")) {
      steps.push("table");
      assert.equal(option(args, "--table-name"), table);
      return failure(
        "An error occurred (ResourceNotFoundException) when calling the DescribeTable operation: Not found",
      );
    }
    if (args.includes("get-object")) {
      steps.push("source");
      assert.equal(option(args, "--version-id"), version);
      assert.equal(option(args, "--expected-bucket-owner"), account);
      writeFileSync(args.at(-1)!, wrongBody ? candidate.json : prior.json);
      return ok({ VersionId: changedVersion ? "another-version" : version });
    }
    if (args.includes("describe-change-set")) {
      steps.push("change-set");
      return ok({
        Status: "CREATE_COMPLETE",
        ExecutionStatus: "AVAILABLE",
        StackId: stackId,
        ChangeSetId: `${scope}:changeSet/${option(args, "--change-set-name")}/exact-id`,
      });
    }
    if (args.includes("execute-change-set")) {
      steps.push("execute");
      assert.match(
        option(args, "--change-set-name"),
        /:changeSet\/scaleset-[a-f0-9-]+\/exact-id$/u,
      );
      return ok();
    }
    if (args.includes("wait")) {
      steps.push("wait");
      assert.ok(
        args.includes(
          stackMissing ? "stack-create-complete" : "stack-update-complete",
        ),
      );
      assert.equal(option(args, "--stack-name"), stackId);
      return ok();
    }
    throw new Error(`Unexpected operation ${args.join(" ")}`);
  };
  return { run, steps };
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
    "change-set",
    "preflight",
    "execute",
    "wait",
  ]);
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
  }
});

test("unverified current stack, table, object version or digest cannot reach preparation", () => {
  for (const options of [
    { wrongTable: true },
    { wrongBody: true },
    { changedVersion: true },
    { unknownStack: true },
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
    "change-set",
    "execute",
    "wait",
  ]);
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
