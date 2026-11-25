import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { Template } from "aws-cdk-lib/assertions";
import { compileConfig } from "../lib/config/compiler.ts";
import { synthesizeController } from "../lib/infrastructure/controller-stack.ts";
import {
  runtimeCopySource,
  verifyImportedEncryption,
} from "../lib/infrastructure/imported-encryption-preflight.ts";
import { resolveImportedEncryption } from "../lib/infrastructure/imported-encryption.ts";

const fixture = compileConfig(
  readFileSync(
    new URL("../../testdata/config/mixed-ec2-fargate.yaml", import.meta.url),
    "utf8",
  ),
);
const { spec } = fixture.runtime;
const { account, region } = spec.aws;
const prefix = `arn:aws:kms:${region}:${account}:key/`;
const bootstrapAssetKeyArn = `${prefix}00000000-0000-0000-0000-000000000001`;
const trackingTableKeyArn = `${prefix}00000000-0000-0000-0000-000000000002`;
const snapshotKeyArn = `${prefix}00000000-0000-0000-0000-000000000003`;
const fargateQuotaKeyArn = `${prefix}00000000-0000-0000-0000-000000000004`;
const table = "existing-lifecycle";
const quota = { bucket: "quota-fixture", key: "quota/observation.json" };
const quotaConfig = JSON.stringify({ profiles: {}, quotaSource: quota });
const version = "snapshot+version=1";

function compile(changes: Partial<typeof spec>) {
  return compileConfig(
    JSON.stringify({
      ...fixture.runtime,
      kind: "RunnerController",
      spec: { ...spec, ...changes },
    }),
  );
}

const imported = compile({
  tracking: { table: { type: "import", name: table } },
});
const snapshot = compile({
  mode: "dry-run",
  dryRun: {
    inspectAws: false,
    demand: {
      type: "snapshot",
      s3Uri: "s3://snapshot-fixture/path/sample.json",
      versionId: version,
      sha256: "b".repeat(64),
    },
  },
});

function synthesize(
  config: typeof fixture,
  encryption?: unknown,
  fargatePreflight?: string,
) {
  return Template.fromJSON(
    synthesizeController(config, "7", undefined, {
      encryption,
      fargatePreflight,
    }).template,
  );
}

test("only actual callers receive exact imported KMS decrypt rights", () => {
  const active = synthesize(
    imported,
    { bootstrapAssetKeyArn, trackingTableKeyArn, fargateQuotaKeyArn },
    quotaConfig,
  );
  const dry = synthesize(snapshot, { bootstrapAssetKeyArn, snapshotKeyArn });
  for (const [output, controllers] of [
    [active, [trackingTableKeyArn, fargateQuotaKeyArn]],
    [dry, [snapshotKeyArn]],
  ] as const) {
    const policies = output.findResources("AWS::IAM::Policy");
    const copy = Object.entries(policies).filter(([name]) =>
      name.startsWith("RuntimeConfigCopy"),
    );
    assert.equal(copy.length, 1);
    const copyStatements = copy[0][1].Properties.PolicyDocument.Statement as {
      Action: string;
      Resource: unknown;
      Condition?: unknown;
    }[];
    const source = copyStatements.find(
      ({ Action }) => Action === "s3:GetObject",
    )?.Resource;
    assert.deepEqual(
      copyStatements.find(({ Resource }) => Resource === bootstrapAssetKeyArn),
      {
        Effect: "Allow",
        Action: "kms:Decrypt",
        Resource: bootstrapAssetKeyArn,
        Condition: {
          StringEquals: {
            "kms:ViaService": `s3.${region}.amazonaws.com`,
            "kms:EncryptionContext:aws:s3:arn": source,
          },
        },
      },
    );
    const controller = Object.entries(policies).filter(([name]) =>
      name.startsWith("ControllerTaskRole"),
    );
    assert.equal(controller.length, 1);
    const role = controller[0][1].Properties.PolicyDocument.Statement as {
      Action: string;
      Resource: unknown;
      Condition?: unknown;
    }[];
    for (const arn of controllers)
      assert.ok(
        role.some(
          (statement) =>
            statement.Action === "kms:Decrypt" && statement.Resource === arn,
        ),
      );
    const tableRead = role.find(
      ({ Resource }) => Resource === trackingTableKeyArn,
    );
    if (output === active) {
      assert.deepEqual(tableRead?.Condition, {
        StringEquals: {
          "kms:ViaService": `dynamodb.${region}.amazonaws.com`,
          "kms:EncryptionContext:aws:dynamodb:tableName": table,
          "kms:EncryptionContext:aws:dynamodb:subscriberId": account,
        },
      });
      const tableRoles = Object.entries(policies).filter(([, policy]) =>
        JSON.stringify(policy).includes(trackingTableKeyArn),
      );
      assert.ok(
        tableRoles.some(([name]) => /^Bootstrap.*HelloBroker/u.test(name)),
      );
      assert.ok(
        tableRoles.some(([name]) => /^Bootstrap(?!.*HelloBroker)/u.test(name)),
      );
      assert.ok(
        tableRoles.every(([name]) =>
          /^(?:Bootstrap|ControllerTaskRole)/u.test(name),
        ),
      );
    } else {
      assert.equal(tableRead, undefined);
    }
    const externalKey = output === active ? fargateQuotaKeyArn : snapshotKeyArn;
    const externalObject =
      output === active
        ? "quota-fixture/quota/observation.json"
        : "snapshot-fixture/path/sample.json";
    const condition = role.find(
      ({ Resource }) => Resource === externalKey,
    )?.Condition;
    assert.deepEqual(condition, {
      StringEquals: {
        "kms:ViaService": `s3.${region}.amazonaws.com`,
        "kms:EncryptionContext:aws:s3:arn": {
          "Fn::Join": [
            "",
            ["arn:", { Ref: "AWS::Partition" }, `:s3:::${externalObject}`],
          ],
        },
      },
    });
  }
  const defaults = synthesize(fixture).findResources("AWS::IAM::Policy");
  assert.ok(
    Object.entries(defaults)
      .filter(([name]) => name.startsWith("RuntimeConfigCopy"))
      .every(([, policy]) => !JSON.stringify(policy).includes("kms:")),
  );
  assert.ok(
    !JSON.stringify(defaults).includes(
      "kms:EncryptionContext:aws:dynamodb:tableName",
    ),
  );
  assert.ok(!JSON.stringify(active.toJSON()).includes("kms:CreateGrant"));
});

test("encryption options reject aliases, wildcards, out-of-scope and unused bindings", () => {
  for (const value of [
    "alias/key",
    `${prefix}*`,
    bootstrapAssetKeyArn.replace(account, "444455556666"),
    `${bootstrapAssetKeyArn}\n`,
  ]) {
    assert.throws(
      () =>
        resolveImportedEncryption(
          { bootstrapAssetKeyArn: value },
          fixture.runtime,
          false,
        ),
      /literal key ARN/u,
    );
  }
  for (const value of [
    { trackingTableKeyArn },
    { snapshotKeyArn },
    { fargateQuotaKeyArn },
    { ebsKeyArn: bootstrapAssetKeyArn },
  ]) {
    assert.throws(
      () => resolveImportedEncryption(value, fixture.runtime, false),
      /(?:requires|Invalid imported encryption)/u,
    );
  }
});

function keyMetadata(arn: string, manager = "CUSTOMER") {
  return {
    KeyMetadata: {
      Arn: arn,
      Enabled: true,
      KeyState: "Enabled",
      KeySpec: "SYMMETRIC_DEFAULT",
      KeyUsage: "ENCRYPT_DECRYPT",
      KeyManager: manager,
    },
  };
}

test("table encryption preflight allows AWS-owned and verified AWS-managed defaults and holds unbound customer keys", () => {
  const observed = {
    TableArn: `arn:aws:dynamodb:${region}:${account}:table/${table}`,
    TableStatus: "ACTIVE",
  };
  const steps: string[] = [];
  function prove(
    arn: string | undefined,
    manager: string,
    expected: string | undefined,
  ) {
    verifyImportedEncryption(
      imported.runtime,
      { trackingTableKeyArn: expected },
      undefined,
      undefined,
      false,
      (_, service, method, parameter, value) => {
        steps.push(`${service}/${method}`);
        if (service === "dynamodb") {
          assert.deepEqual(
            [method, parameter, value],
            ["describe-table", "--table-name", table],
          );
          return {
            Table: {
              ...observed,
              ...(arn
                ? {
                    SSEDescription: {
                      Status: "ENABLED",
                      SSEType: "KMS",
                      KMSMasterKeyArn: arn,
                    },
                  }
                : {}),
            },
          };
        }
        assert.deepEqual(
          [service, method, parameter, value],
          ["kms", "describe-key", "--key-id", trackingTableKeyArn],
        );
        return keyMetadata(trackingTableKeyArn, manager);
      },
    );
  }
  prove(undefined, "unused", undefined);
  assert.deepEqual(steps, ["dynamodb/describe-table"]);
  prove(trackingTableKeyArn, "AWS", undefined);
  prove(trackingTableKeyArn, "CUSTOMER", trackingTableKeyArn);
  assert.throws(
    () => prove(trackingTableKeyArn, "CUSTOMER", undefined),
    /requires trackingTableKeyArn/u,
  );
  assert.throws(
    () => prove(trackingTableKeyArn, "CUSTOMER", snapshotKeyArn),
    /differs from the candidate/u,
  );
});

test("bootstrap metadata and published object must agree, and S3 reads verify owner, version and context", () => {
  const artifact = synthesizeController(snapshot, "7");
  const bootstrap = runtimeCopySource(artifact.template);
  const calls: string[][] = [];
  function prove(
    assetPublished: boolean,
    bucketKey = false,
    actualVersion = version,
  ) {
    verifyImportedEncryption(
      snapshot.runtime,
      { bootstrapAssetKeyArn, snapshotKeyArn },
      undefined,
      bootstrap,
      assetPublished,
      (_, ...args) => {
        calls.push(args);
        if (args[0] === "kms") return keyMetadata(args[3]);
        assert.equal(
          args[args.indexOf("--expected-bucket-owner") + 1],
          account,
        );
        if (args[1] === "get-bucket-encryption") {
          assert.equal(args[3], bootstrap.bucket);
          return {
            ServerSideEncryptionConfiguration: {
              Rules: [
                {
                  ApplyServerSideEncryptionByDefault: {
                    SSEAlgorithm: "aws:kms",
                    KMSMasterKeyID: bootstrapAssetKeyArn,
                  },
                },
              ],
            },
          };
        }
        if (args[3] === bootstrap.bucket) {
          assert.equal(args[5], bootstrap.key);
          return {
            ServerSideEncryption: "aws:kms",
            SSEKMSKeyId: bootstrapAssetKeyArn,
            BucketKeyEnabled: bucketKey,
          };
        }
        assert.deepEqual(
          [args[3], args[5], args.at(-1)],
          ["snapshot-fixture", "path/sample.json", version],
        );
        return {
          ServerSideEncryption: "aws:kms",
          SSEKMSKeyId: snapshotKeyArn,
          VersionId: actualVersion,
        };
      },
    );
  }
  prove(false);
  prove(true);
  assert.ok(
    calls.some(
      (args) => args[1] === "head-object" && args[3] === bootstrap.bucket,
    ),
  );
  assert.throws(() => prove(true, true), /Bucket Keys disabled/u);
  assert.throws(
    () => prove(true, false, "changed-version"),
    /object and version/u,
  );
});
