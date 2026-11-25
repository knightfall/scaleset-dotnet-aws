import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, matchesGlob } from "node:path";
import { test } from "node:test";
import { parseYaml } from "../lib/config/yaml.ts";

type Job = {
  if: string;
  permissions: Record<string, string>;
  env: Record<string, string>;
  steps: {
    name?: string;
    uses?: string;
    with?: Record<string, unknown>;
    run?: string;
  }[];
};
const workflow = parseYaml(
  readFileSync(
    new URL("../../.github/workflows/release-candidate.yml", import.meta.url),
    "utf8",
  ),
) as {
  on: Record<string, { branches?: string[]; paths?: string[] } | null>;
  permissions: Record<string, string>;
  jobs: { build: Job; candidate: Job };
};

test("native validation covers runtime changes and preserves the exact main sealing boundary", () => {
  assert.deepEqual(Object.keys(workflow.on).sort(), [
    "pull_request",
    "push",
    "workflow_dispatch",
  ]);
  assert.deepEqual(workflow.on.push, { branches: ["main"] });
  assert.deepEqual(workflow.on.pull_request?.branches, ["main"]);
  const matches = (path: string) =>
    workflow.on.pull_request?.paths?.some((pattern) =>
      matchesGlob(path, pattern),
    );
  for (const path of [
    "src/Knightfall.ScaleSet.Aws.Providers.Fargate/FargateTaskEventConsumer.cs",
    "src/Knightfall.ScaleSet.Aws.Controller/packages.lock.json",
    "cdk/config/schema/controller-common-v1alpha1.json",
    "testdata/config/runtime/ec2-only-pinned.json",
    "tests/Knightfall.ScaleSet.Aws.Core.Tests/ControllerHealthTests.cs",
    "NuGet.Config",
    ".github/release/build-controller.sh",
    "tests/oci/verify_controller_image.py",
  ])
    assert.equal(matches(path), true, path);
  assert.equal(matches("docs/operator-runbook.md"), false);
  assert.deepEqual(workflow.permissions, {});
  assert.deepEqual(workflow.jobs.build.permissions, { contents: "read" });
  assert.equal(
    workflow.jobs.candidate.if,
    "github.repository == 'knightfall/scaleset-dotnet-aws' && github.ref == 'refs/heads/main' && github.event_name != 'pull_request'",
  );
});

test("the runbook supplies only arguments to the packaged clock preflight", () => {
  const docs = readFileSync(
    new URL("../../docs/operator-runbook.md", import.meta.url),
    "utf8",
  );
  const command = /```json\n(\["preflight", "clock"[^\n]+\])\n```/u.exec(
    docs,
  )?.[1];
  assert.ok(command);
  assert.deepEqual(JSON.parse(command), [
    "preflight",
    "clock",
    "--config",
    "/operator/runtime.json",
    "--sha256",
    "<verified-runtime-sha256>",
  ]);
});

test("fork checkout selects the event's exact source and detects an absent or different head", () => {
  const build = workflow.jobs.build;
  assert.equal(
    build.env.BUILD_SHA,
    "${{ github.event.pull_request.head.sha || github.sha }}",
  );
  assert.equal(
    build.env.BUILD_REPOSITORY,
    "${{ github.event.pull_request.head.repo.full_name || github.repository }}",
  );
  const checkout = build.steps.find((step) =>
    step.uses?.startsWith("actions/checkout@"),
  );
  assert.deepEqual(checkout?.with, {
    repository: "${{ env.BUILD_REPOSITORY }}",
    ref: "${{ env.BUILD_SHA }}",
    "persist-credentials": false,
  });
  const select = build.steps.find(
    (step) => step.name === "Require an exact source for pull requests",
  )?.run;
  const verify = build.steps.find(
    (step) => step.name === "Verify the checked-out source commit",
  )?.run;
  assert.ok(select && verify);

  const workspace = mkdtempSync(join(tmpdir(), "scaleset-fork-checkout-"));
  const origin = join(workspace, "base");
  const fork = join(workspace, "fork");
  const checked = join(workspace, "checked");
  function git(...args: string[]) {
    return execFileSync(
      "git",
      [
        "-c",
        "user.name=Fixture",
        "-c",
        "user.email=fixture@example.invalid",
        "-c",
        "commit.gpgsign=false",
        ...args,
      ],
      { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
    ).trim();
  }
  try {
    git("init", "--initial-branch=main", origin);
    git("-C", origin, "commit", "--allow-empty", "-m", "base");
    git("clone", origin, fork);
    git("-C", fork, "commit", "--allow-empty", "-m", "fork head");
    const sha = git("-C", fork, "rev-parse", "HEAD");
    git("clone", origin, checked);
    const buildEnv = {
      ...process.env,
      BUILD_SHA: sha,
      PR_HEAD_SHA: sha,
      BUILD_REPOSITORY: "fixture/fork",
      PR_HEAD_REPOSITORY: "fixture/fork",
    };
    assert.equal(
      spawnSync("bash", ["-ec", select], { cwd: checked, env: buildEnv })
        .status,
      0,
    );
    assert.equal(
      spawnSync("bash", ["-ec", select], {
        cwd: checked,
        env: { ...buildEnv, PR_HEAD_REPOSITORY: "" },
      }).status,
      1,
    );
    assert.equal(
      spawnSync("bash", ["-ec", verify], { cwd: checked, env: buildEnv })
        .status,
      1,
    );
    git("-C", checked, "fetch", "--depth=1", `file://${fork}`, sha);
    git("-C", checked, "checkout", "--detach", "FETCH_HEAD");
    assert.equal(
      spawnSync("bash", ["-ec", verify], { cwd: checked, env: buildEnv })
        .status,
      0,
    );
  } finally {
    rmSync(workspace, { recursive: true, force: true });
  }
});
