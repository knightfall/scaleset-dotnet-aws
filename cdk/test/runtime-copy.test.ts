import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { runInNewContext } from "node:vm";

const source = readFileSync(
  new URL("../lib/infrastructure/copy-runtime.cjs", import.meta.url),
  "utf8",
);

const create = {
  RequestType: "Create",
  ResourceProperties: {
    Account: "111122223333",
    SourceBucket: "fixture-assets",
    SourceKey: "dir/sub +runtime.json",
    DestinationBucket: "fixture-runtime",
    DestinationKey: "runtime/fixture.json",
  },
};

function copy(versionId?: string, errorName?: string) {
  const calls: unknown[] = [];
  const exports: {
    begin?: (event: object) => Promise<unknown>;
    copy?: (event: object) => Promise<unknown>;
  } = {};
  runInNewContext(source, {
    exports,
    require: (module: string) => {
      assert.equal(module, "@aws-sdk/client-s3");
      return {
        S3Client: class {
          async send(command: unknown) {
            calls.push(command);
            if (errorName) {
              throw Object.assign(new Error("private-service-error-fixture"), {
                name: errorName,
              });
            }
            return {
              VersionId: versionId,
              CopySourceVersionId: "source-version",
            };
          }
        },
        CopyObjectCommand: function (input: unknown) {
          return input;
        },
      };
    },
  });
  return { begin: exports.begin!, copy: exports.copy!, calls };
}

test("copies exact runtime bytes by key and returns only the destination version", async () => {
  const fixture = copy("destination-version");
  const initial = await fixture.begin(create);
  const response = await fixture.copy(create);
  assert.deepEqual(JSON.parse(JSON.stringify(initial)), {
    PhysicalResourceId: "fixture-runtime/runtime/fixture.json",
  });
  assert.deepEqual(JSON.parse(JSON.stringify(response)), {
    IsComplete: true,
    Data: { VersionId: "destination-version" },
  });
  assert.deepEqual(JSON.parse(JSON.stringify(fixture.calls)), [
    {
      Bucket: "fixture-runtime",
      CopySource: "fixture-assets/dir/sub%20%2Bruntime.json",
      Key: "runtime/fixture.json",
      ExpectedBucketOwner: "111122223333",
      ExpectedSourceBucketOwner: "111122223333",
    },
  ]);
});

test("bootstrap copy access denial identifies the exact operator action without echoing a service message", async () => {
  const fixture = copy(undefined, "AccessDenied");
  await assert.rejects(
    () => fixture.copy(create),
    (error: unknown) => {
      assert.ok(
        typeof error === "object" &&
          error !== null &&
          "message" in error &&
          typeof error.message === "string",
      );
      assert.match(error.message, /configure bootstrapAssetKeyArn/u);
      assert.doesNotMatch(error.message, /private-service-error-fixture/u);
      return true;
    },
  );
});

test("missing or null versions leave the waiter incomplete and removal retains the object", async () => {
  for (const version of [undefined, "", "null"]) {
    const fixture = copy(version);
    const pending = await fixture.copy(create);
    assert.deepEqual(JSON.parse(JSON.stringify(pending)), {
      IsComplete: false,
    });
  }
  const fixture = copy();
  const request = {
    ...create,
    RequestType: "Delete",
    PhysicalResourceId: "retained",
  };
  const deleted = await fixture.begin(request);
  assert.deepEqual(JSON.parse(JSON.stringify(deleted)), {
    PhysicalResourceId: "retained",
  });
  assert.deepEqual(JSON.parse(JSON.stringify(await fixture.copy(request))), {
    IsComplete: true,
  });
  assert.deepEqual(fixture.calls, []);
});
