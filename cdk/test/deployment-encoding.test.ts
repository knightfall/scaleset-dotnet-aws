import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  chmodSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { compileConfig } from "../lib/config/compiler.ts";
import { readControllerTelemetry } from "../lib/infrastructure/controller-telemetry.ts";
import { deployController } from "../lib/infrastructure/deploy-controller.ts";
import {
  controllerStackName,
  deploymentIdentity,
} from "../lib/infrastructure/deployment-input.ts";
import {
  readAttempt,
  readDecision,
} from "../lib/infrastructure/deployment-recovery.ts";

const config = fileURLToPath(
  new URL("../../testdata/config/mixed-ec2-fargate.yaml", import.meta.url),
);
const compiled = compileConfig(readFileSync(config));
const aws = compiled.runtime.spec.aws;
const malformedUtf8 = [
  Buffer.from([0xc0, 0xaf]),
  Buffer.from([0x80]),
  Buffer.from([0xe2, 0x82]),
];

function injectBytes(source: string, bytes: Buffer): Buffer {
  const [first, last] = source.split("FIXTURE_BYTES");
  return Buffer.concat([Buffer.from(first), bytes, Buffer.from(last)]);
}

test("telemetry readers reject malformed UTF-8 and retain a literal Unicode endpoint", (context) => {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-telemetry-encoding-"));
  context.after(() => rmSync(directory, { recursive: true, force: true }));
  const document = join(directory, "telemetry.json");
  const outdir = join(directory, "out");
  const run = (entry: string) =>
    spawnSync(
      process.execPath,
      [
        fileURLToPath(new URL(`../bin/${entry}.ts`, import.meta.url)),
        "--config",
        config,
        "--generation",
        "1",
        "--telemetry",
        document,
      ],
      { encoding: "utf8", env: { CDK_OUTDIR: outdir, PATH: "" } },
    );
  const source =
    '{"exporters":{"common":{"endpoint":"https://collector.invalid/FIXTURE_BYTES"}}}';
  for (const bytes of malformedUtf8) {
    writeFileSync(document, injectBytes(source, bytes));
    assert.throws(() => readControllerTelemetry(document, aws), {
      message: "Controller telemetry input must be valid UTF-8.",
    });
    for (const entry of ["controller", "deploy-controller"]) {
      const result = run(entry);
      assert.equal(result.status, 1, entry);
      assert.equal(
        result.stderr.trim(),
        "Controller telemetry input must be valid UTF-8.",
        entry,
      );
    }
  }

  const endpoint = "https://collector.invalid/café";
  writeFileSync(document, injectBytes(source, Buffer.from("café")));
  assert.equal(
    readControllerTelemetry(document, aws)?.exporters?.common?.endpoint,
    endpoint,
  );
  const result = run("controller");
  assert.equal(result.status, 0, result.stderr);
  assert.ok(
    readFileSync(join(outdir, "Controller.template.json"), "utf8").includes(
      endpoint,
    ),
  );
});

test("resume rejects malformed attempt and decision bytes before deployment work", (context) => {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-recovery-encoding-"));
  context.after(() => rmSync(directory, { recursive: true, force: true }));
  const journal = join(directory, "attempt.json");
  const name = "scaleset-00000000-0000-0000-0000-000000000000";
  const decision = `${journal}.${name}.decision`;
  const attempt = {
    identity: deploymentIdentity(compiled.runtime),
    generation: "1",
    runtimeSha256: compiled.sha256,
    templateSha256: "fixture",
    base: null,
    name,
    stackId: null,
    changeSetId: null,
  };
  function verifyResumeHolds() {
    const calls: string[] = [];
    assert.throws(
      () =>
        deployController(
          { compiled, generation: "1", resume: journal },
          (program, args) => {
            assert.equal(program, "aws");
            if (args.includes("get-caller-identity")) {
              calls.push("identity");
              return {
                status: 0,
                stdout: JSON.stringify({ Account: aws.account }),
                stderr: "",
              };
            }
            assert.ok(args.includes("describe-stacks"));
            calls.push("stack");
            const stack = controllerStackName(compiled.runtime);
            return {
              status: 254,
              stdout: "",
              stderr: `An error occurred (ValidationError) when calling the DescribeStacks operation: Stack with id ${stack} does not exist`,
            };
          },
        ),
      /Deployment recovery evidence must be valid UTF-8/u,
    );
    assert.deepEqual(calls, ["identity", "stack"]);
  }
  const invalid = {
    message: "Deployment recovery evidence must be valid UTF-8.",
  };
  for (const bytes of malformedUtf8) {
    writeFileSync(
      journal,
      injectBytes(
        JSON.stringify({ ...attempt, identity: "FIXTURE_BYTES" }),
        bytes,
      ),
      { mode: 0o600 },
    );
    assert.throws(() => readAttempt(journal), invalid);
    verifyResumeHolds();

    writeFileSync(journal, JSON.stringify(attempt));
    writeFileSync(decision, injectBytes('"discardFIXTURE_BYTES"', bytes), {
      mode: 0o600,
    });
    assert.throws(() => readDecision(journal, name), invalid);
    verifyResumeHolds();
  }

  writeFileSync(journal, JSON.stringify({ ...attempt, identity: "équipe" }));
  writeFileSync(decision, '"discard"');
  assert.equal(readAttempt(journal).identity, "équipe");
  assert.equal(readDecision(journal, name), "discard");

  const link = join(directory, "link.json");
  symlinkSync(journal, link);
  assert.throws(
    () => readAttempt(link),
    /private file owned by the current user/u,
  );
  assert.throws(
    () => readAttempt(directory),
    /private file owned by the current user/u,
  );
  chmodSync(decision, 0o644);
  assert.throws(
    () => readDecision(journal, name),
    /private file owned by the current user/u,
  );
});

test("local deployment documents accept 64 KiB and reject larger inputs", (context) => {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-bounded-documents-"));
  context.after(() => rmSync(directory, { recursive: true, force: true }));
  const journal = join(directory, "attempt.json");
  const name = "scaleset-00000000-0000-0000-0000-000000000000";
  const decision = `${journal}.${name}.decision`;
  const telemetry = join(directory, "telemetry.json");
  const limit = 64 * 1024;

  writeFileSync(decision, '"discard"'.padEnd(limit), { mode: 0o600 });
  assert.equal(readDecision(journal, name), "discard");
  writeFileSync(decision, '"discard"'.padEnd(limit + 1));
  assert.throws(() => readDecision(journal, name), {
    message: "Deployment recovery evidence exceeds 64 KiB.",
  });

  writeFileSync(telemetry, "{}".padEnd(limit));
  assert.deepEqual(readControllerTelemetry(telemetry, aws), {});
  writeFileSync(telemetry, "{}".padEnd(limit + 1));
  assert.throws(() => readControllerTelemetry(telemetry, aws), {
    message: "Controller telemetry input exceeds 64 KiB.",
  });
});

test("deployment journal rejects duplicate JSON names even when escaped", (context) => {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-duplicate-journal-"));
  context.after(() => rmSync(directory, { recursive: true, force: true }));
  const journal = join(directory, "attempt.json");
  const attempt = {
    identity: deploymentIdentity(compiled.runtime),
    generation: "1",
    runtimeSha256: compiled.sha256,
    templateSha256: "fixture",
    base: null,
    name: "scaleset-00000000-0000-0000-0000-000000000000",
    stackId: null,
    changeSetId: null,
  };
  const json = JSON.stringify(attempt);
  writeFileSync(journal, json, { mode: 0o600 });
  assert.deepEqual(readAttempt(journal), attempt);

  for (const key of ["generation", String.raw`gener\u0061tion`]) {
    writeFileSync(journal, json.slice(0, -1) + `,"${key}":"1"}`);
    assert.throws(() => readAttempt(journal), {
      message: "Deployment recovery evidence is malformed.",
    });
  }
});
