import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { compileConfig, validateRuntime } from "../lib/config/compiler.ts";
import { ConfigValidationError } from "../lib/config/errors.ts";
import { parseYaml } from "../lib/config/yaml.ts";

const fixtures = new URL("../../testdata/config/", import.meta.url);
const positiveFixtures = [
  "ec2-only-latest",
  "ec2-only-pinned",
  "fargate-only",
  "mixed-ec2-fargate",
];

type Edit = { path: string; value?: unknown };

type Rejection = {
  name: string;
  code: string;
  errorPath?: string;
  redacted?: string[];
} & (
  | { fault: "yaml syntax"; yaml: string }
  | { fault: "configuration"; base?: string; edits: Edit[] }
);

function fixture(relativePath: string): string {
  return readFileSync(new URL(relativePath, fixtures), "utf8");
}

function edited(value: unknown, edits: readonly Edit[]): unknown {
  const document: unknown = structuredClone(value);
  for (const edit of edits) {
    assert.ok(edit.path.startsWith("/"));
    const parts = edit.path
      .slice(1)
      .split("/")
      .map((part) => part.replaceAll("~1", "/").replaceAll("~0", "~"));
    const key = parts.pop();
    assert.ok(key !== undefined);
    let parent = document;
    for (const part of parts) {
      assert.ok(parent && typeof parent === "object");
      assert.ok(
        Object.hasOwn(parent, part),
        `Unresolved fixture path: ${edit.path}`,
      );
      parent = (parent as Record<string, unknown>)[part];
    }
    assert.ok(parent && typeof parent === "object");
    const record = parent as Record<string, unknown>;
    if ("value" in edit) {
      record[key] = edit.value;
    } else {
      assert.ok(
        Object.hasOwn(record, key),
        `Missing fixture field: ${edit.path}`,
      );
      delete record[key];
    }
  }
  return document;
}

function rejected(operation: () => unknown, rejection: Rejection): void {
  assert.throws(operation, (error: unknown) => {
    assert.ok(error instanceof ConfigValidationError);
    assert.equal(error.code, rejection.code);
    if (rejection.errorPath !== undefined) {
      assert.equal(error.path, rejection.errorPath);
    }
    for (const sensitive of rejection.redacted ?? []) {
      assert.ok(
        !error.message.includes(sensitive),
        "Validation echoed a credential.",
      );
      assert.ok(
        !error.path.includes(sensitive),
        "Validation exposed a credential in its path.",
      );
    }
    if (error.code === "ghes-unsupported") {
      assert.match(error.message, /scaleset\.NET #20/);
    }
    if (error.code === "custom-ca-unsupported") {
      assert.match(error.message, /scaleset\.NET #21/);
    }
    return true;
  });
}

for (const name of positiveFixtures) {
  test(`${name} compiles to the committed runtime bytes`, () => {
    const result = compileConfig(fixture(`${name}.yaml`));
    const golden = readFileSync(new URL(`runtime/${name}.json`, fixtures));
    assert.deepEqual(Buffer.from(result.json, "utf8"), golden);
    assert.equal(
      result.sha256,
      createHash("sha256").update(golden).digest("hex"),
    );
    assert.deepEqual(
      validateRuntime(JSON.parse(golden.toString("utf8"))),
      result.runtime,
    );
    assert.equal(result.runtime.kind, "RunnerControllerRuntime");
    assert.equal(result.runtime.apiVersion, "scaleset-aws/v1alpha1");
  });
}

test("authored defaults and the signed 64-bit scale-set ID survive compilation", () => {
  const { runtime, json } = compileConfig(fixture("ec2-only-pinned.yaml"));
  assert.equal(runtime.spec.mode, "dry-run");
  assert.deepEqual(runtime.spec.dryRun, {
    demand: { type: "none" },
    inspectAws: false,
  });
  assert.equal(
    runtime.spec.profiles[0].github.scaleSetId,
    "9223372036854775807",
  );
  assert.match(json, /"scaleSetId": "9223372036854775807"/);
});

test("mixed input preserves independent EC2 and Fargate bindings and capacity", () => {
  const { spec } = compileConfig(fixture("mixed-ec2-fargate.yaml")).runtime;
  assert.equal(spec.mode, "active");
  assert.deepEqual(spec.aws, {
    account: "111122223333",
    region: "ap-southeast-2",
  });
  assert.equal(spec.controller.hosting.cluster, "control-plane");
  assert.equal(
    spec.github.engineering.scopeURL,
    "https://github.com/example-engineering",
  );
  assert.deepEqual(spec.ecsClusters["runner-shell"], {
    type: "import",
    arn: "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell",
  });
  assert.deepEqual(spec.ecsClusters["runner-arm"], {
    type: "import",
    arn: "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-arm",
  });
  assert.deepEqual(
    spec.profiles.map(({ id, state, capacity, github, provisioner }) => ({
      id,
      state,
      capacity,
      connection: github.connection,
      runnerGroup: github.runnerGroup,
      scaleSet: github.scaleSet,
      labels: github.labels,
      backend:
        provisioner.type === "ec2"
          ? {
              type: "ec2",
              launchTemplate: provisioner.launchTemplateId,
              version: provisioner.launchTemplateVersion,
            }
          : {
              type: "fargate",
              cluster: provisioner.cluster,
              taskDefinition: provisioner.taskDefinitionArn,
            },
    })),
    [
      {
        id: "linux-ec2-docker",
        state: "active",
        capacity: {
          idleReserve: 0,
          maxRunners: 12,
          startupTimeoutSeconds: 600,
        },
        connection: "engineering",
        runnerGroup: "Linux-EC2",
        scaleSet: "linux-ec2-docker",
        labels: ["linux-ec2-docker"],
        backend: {
          type: "ec2",
          launchTemplate: "lt-0123456789abcdef0",
          version: { type: "alias", name: "$Latest" },
        },
      },
      {
        id: "linux-ec2-build",
        state: "active",
        capacity: {
          idleReserve: 2,
          maxRunners: 16,
          startupTimeoutSeconds: 480,
        },
        connection: "engineering",
        runnerGroup: "Linux-EC2-Arm",
        scaleSet: "linux-ec2-build",
        labels: ["linux-ec2-build", "arm64-compiled"],
        backend: {
          type: "ec2",
          launchTemplate: "lt-0123456789abcdef1",
          version: { type: "pinned", version: "3" },
        },
      },
      {
        id: "linux-shell",
        state: "active",
        capacity: {
          idleReserve: 1,
          maxRunners: 24,
          startupTimeoutSeconds: 420,
        },
        connection: "engineering",
        runnerGroup: "No-Docker",
        scaleSet: "linux-shell",
        labels: ["linux-shell", "shell-x64"],
        backend: {
          type: "fargate",
          cluster: "runner-shell",
          taskDefinition:
            "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/linux-shell-runner:7",
        },
      },
      {
        id: "linux-ci-arm",
        state: "active",
        capacity: {
          idleReserve: 3,
          maxRunners: 31,
          startupTimeoutSeconds: 540,
        },
        connection: "engineering",
        runnerGroup: "Fargate-Arm-Batches",
        scaleSet: "linux-ci-arm",
        labels: ["linux-ci-arm", "slim-arm"],
        backend: {
          type: "fargate",
          cluster: "runner-arm",
          taskDefinition:
            "arn:aws:ecs:ap-southeast-2:111122223333:task-definition/runner-slim-arm:19",
        },
      },
    ],
  );
});

function reverseMappingOrder(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(reverseMappingOrder);
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value)
        .reverse()
        .map(([key, content]) => [key, reverseMappingOrder(content)]),
    );
  }
  return value;
}

test("comments and recursive YAML mapping reordering preserve exact bytes and digest", () => {
  const source = fixture("mixed-ec2-fargate.yaml");
  const rewritten =
    "# reordered YAML mappings\n" +
    JSON.stringify(reverseMappingOrder(parseYaml(source)), null, 2);
  assert.notEqual(source, rewritten);
  const original = compileConfig(source);
  const reordered = compileConfig(rewritten);
  assert.equal(reordered.json, original.json);
  assert.equal(reordered.sha256, original.sha256);
});

test("capacity values that round to integers fail at the YAML boundary", () => {
  const source = fixture("ec2-only-latest.yaml");
  for (const [original, rounded] of [
    ["maxRunners: 12", "maxRunners: 12.000000000000000001"],
    ["idleReserve: 0", "idleReserve: -1e-999"],
  ]) {
    const variant = source.replace(original, rounded);
    assert.notEqual(variant, source);
    assert.throws(() => compileConfig(variant), {
      name: "ConfigValidationError",
      code: "yaml",
    });
  }
});

test("exact integral YAML decimal, exponent, octal, and hexadecimal values compile canonically", () => {
  const source = fixture("ec2-only-latest.yaml");
  const expected = compileConfig(source);
  for (const literal of ["12.0", "1.2e1", "120e-1", "0o14", "0xC"]) {
    const variant = source.replace("maxRunners: 12", `maxRunners: ${literal}`);
    assert.notEqual(variant, source);
    const result = compileConfig(variant);
    assert.equal(result.runtime.spec.profiles[0].capacity.maxRunners, 12);
    assert.equal(result.json, expected.json);
  }
  const zero = source.replace("idleReserve: 0", "idleReserve: 0e-999");
  assert.notEqual(zero, source);
  assert.equal(compileConfig(zero).json, expected.json);
});

test("a draining profile can use the Default alias and the maximum idle reserve", () => {
  const source = edited(parseYaml(fixture("ec2-only-latest.yaml")), [
    { path: "/spec/profiles/0/state", value: "draining" },
    { path: "/spec/profiles/0/capacity/idleReserve", value: 12 },
    {
      path: "/spec/profiles/0/provisioner/launchTemplateVersion/name",
      value: "$Default",
    },
  ]);
  const { runtime } = compileConfig(JSON.stringify(source));
  const [profile] = runtime.spec.profiles;
  assert.equal(profile.state, "draining");
  assert.equal(profile.capacity.idleReserve, profile.capacity.maxRunners);
  assert.deepEqual(
    profile.provisioner.type === "ec2" &&
      profile.provisioner.launchTemplateVersion,
    {
      type: "alias",
      name: "$Default",
    },
  );
});

test("enterprise PATs and labels reused across separate GitHub scopes remain valid", () => {
  const source = edited(parseYaml(fixture("mixed-ec2-fargate.yaml")), [
    {
      path: "/spec/github/corporate",
      value: {
        scopeURL: "https://github.com/enterprises/example-corporate",
        auth: {
          type: "enterprise-pat",
          secretArn:
            "arn:aws:secretsmanager:ap-southeast-2:111122223333:secret:github-pat-corporate-a1b2c3",
        },
      },
    },
    { path: "/spec/profiles/1/github/connection", value: "corporate" },
    {
      path: "/spec/profiles/1/github/labels",
      value: ["linux-ec2-build", "LINUX-EC2-DOCKER"],
    },
  ]);
  const { spec } = compileConfig(JSON.stringify(source)).runtime;
  assert.equal(spec.github.corporate.auth.type, "enterprise-pat");
  assert.equal(spec.profiles[1].github.connection, "corporate");
  assert.deepEqual(spec.profiles[1].github.labels, [
    "linux-ec2-build",
    "LINUX-EC2-DOCKER",
  ]);
});

test("Copilot and ordinary profiles need distinct groups within a canonical GitHub scope", () => {
  const source = edited(parseYaml(fixture("mixed-ec2-fargate.yaml")), [
    {
      path: "/spec/github/alias",
      value: {
        scopeURL: "https://www.github.com/EXAMPLE-ENGINEERING/",
        auth: {
          type: "github-app",
          secretArn:
            "arn:aws:secretsmanager:ap-southeast-2:111122223333:secret:github-app-alias-a1b2c3",
        },
      },
    },
    { path: "/spec/profiles/0/purpose", value: "copilot-cloud-agent" },
    { path: "/spec/profiles/0/github/runnerGroup", value: "Copilot-Only" },
    { path: "/spec/profiles/1/github/connection", value: "alias" },
  ]);
  const { runtime } = compileConfig(JSON.stringify(source));
  const [copilot, ordinary, ...fargate] = runtime.spec.profiles;
  assert.equal(copilot.purpose, "copilot-cloud-agent");
  assert.deepEqual(copilot.platform, { os: "linux", arch: "x64" });
  assert.equal(copilot.github.runnerGroup, "Copilot-Only");
  assert.equal(ordinary.github.runnerGroup, "Linux-EC2-Arm");
  assert.notEqual(copilot.github.scaleSet, ordinary.github.scaleSet);
  assert.notEqual(copilot.github.connection, ordinary.github.connection);
  assert.doesNotThrow(() => validateRuntime(runtime));

  const conflict = {
    ...copilot,
    github: {
      ...copilot.github,
      runnerGroup: ordinary.github.runnerGroup.toLowerCase(),
    },
  };
  for (const profiles of [
    [conflict, ordinary, ...fargate],
    [ordinary, conflict, ...fargate],
  ]) {
    const edits = [{ path: "/spec/profiles", value: profiles }];
    const error = {
      name: "ConfigValidationError",
      code: "semantic",
      path: "/spec/profiles/1/github/runnerGroup",
      message: /dedicated runner group/i,
    };
    assert.throws(
      () => compileConfig(JSON.stringify(edited(source, edits))),
      error,
    );
    assert.throws(() => validateRuntime(edited(runtime, edits)), error);
  }
});

test("local replay and versioned S3 snapshots retain their exact demand options", () => {
  const base = parseYaml(fixture("ec2-only-latest.yaml"));
  const demands = [
    { type: "replay", path: "/input/replay.json" },
    {
      type: "snapshot",
      s3Uri: "s3://config-fixtures/demand.json",
      versionId: "recorded-version",
      sha256: "a".repeat(64),
    },
  ];
  for (const demand of demands) {
    const source = edited(base, [
      { path: "/spec/dryRun/demand", value: demand },
    ]);
    assert.deepEqual(
      compileConfig(JSON.stringify(source)).runtime.spec.dryRun.demand,
      demand,
    );
  }
});

const rejections = JSON.parse(fixture("rejections.json")) as Rejection[];
for (const rejection of rejections) {
  test(`rejects ${rejection.name}`, () => {
    if (rejection.fault === "yaml syntax") {
      rejected(() => compileConfig(rejection.yaml), rejection);
      return;
    }
    const base = rejection.base ?? "ec2-only-latest";
    assert.ok(rejection.edits && rejection.edits.length > 0);
    const authored = edited(
      parseYaml(fixture(`${base}.yaml`)),
      rejection.edits,
    );
    const runtime = edited(
      JSON.parse(fixture(`runtime/${base}.json`)),
      rejection.edits,
    );
    rejected(() => compileConfig(JSON.stringify(authored)), rejection);
    rejected(() => validateRuntime(runtime), rejection);
  });
}

test("the emit CLI prints the digest of the exact committed runtime output", () => {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-config-test-"));
  try {
    const cli = fileURLToPath(
      new URL("../bin/emit-runtime.ts", import.meta.url),
    );
    const source = fileURLToPath(new URL("ec2-only-latest.yaml", fixtures));
    const golden = readFileSync(
      new URL("runtime/ec2-only-latest.json", fixtures),
    );
    const expectedDigest =
      createHash("sha256").update(golden).digest("hex") + "\n";
    const output = join(directory, "runtime.json");
    const invalid = join(directory, "invalid.yaml");

    for (let invocation = 0; invocation < 2; invocation++) {
      const digest = execFileSync(process.execPath, [cli, source, output], {
        encoding: "utf8",
      });
      assert.equal(digest, expectedDigest);
      assert.deepEqual(readFileSync(output), golden);
    }

    writeFileSync(invalid, "key: &anchor value\n");
    const failed = spawnSync(process.execPath, [cli, invalid, output], {
      encoding: "utf8",
    });
    assert.equal(failed.status, 1);
    assert.equal(failed.stdout, "");
    assert.match(failed.stderr, /yaml/i);
    assert.deepEqual(readFileSync(output), golden);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
