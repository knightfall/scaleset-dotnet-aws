import assert from "node:assert/strict";
import { createECDH, createHash } from "node:crypto";
import { createRequire } from "node:module";
import { test } from "node:test";

type Item = Record<string, { S?: string; N?: string }>;
interface Store {
  readonly read: (request: {
    TableName: string;
    Key: Item;
    ConsistentRead: boolean;
  }) => Promise<{ Item?: Item }>;
  readonly put: (request: {
    TableName: string;
    Item: Item;
    ConditionExpression: string;
  }) => Promise<void>;
}
const { createHandler } = createRequire(import.meta.url)(
  "../lib/infrastructure/hello-ingress.cjs",
) as {
  createHandler: (
    options: Store & {
      readonly now: () => number;
      readonly tableName: string;
      readonly account: string;
      readonly partition: string;
      readonly roleName: string;
      readonly enabled: boolean;
    },
  ) => (input: unknown) => Promise<{ statusCode: number }>;
};

const account = "111122223333";
const roleName = "fixture-bootstrap";
const taskId = "00000000000000000000000000000001";
const secondId = "00000000000000000000000000000002";
const roleId = "AROAFIXTUREBOOTSTRAP1";
const tableName = "fixture-table";

function payload() {
  const key = createECDH("prime256v1");
  key.generateKeys();
  return {
    protocolVersion: 1,
    bootNonce: Array.from({ length: 32 }, () => 1),
    recipientPublicKey: Array.from(key.getPublicKey()),
  };
}

function event(body: string, id = taskId, role = roleName) {
  return {
    version: "2.0",
    rawPath: "/",
    rawQueryString: "",
    isBase64Encoded: false,
    body,
    requestContext: {
      authorizer: {
        iam: {
          accountId: account,
          userId: `${roleId}:${id}`,
          userArn: `arn:aws:sts::${account}:assumed-role/${role}/${id}`,
        },
      },
      http: { method: "POST" },
    },
  };
}

function fixture(enabled = true, lostResponses = new Set<string>()) {
  const rows = new Map<string, Item>();
  const calls = { reads: 0, putAttempts: 0, writes: 0, clock: 1_700_000_000 };
  const handle = createHandler({
    account,
    roleName,
    tableName,
    partition: "aws",
    enabled,
    now: () => calls.clock * 1000,
    read: async (request) => {
      assert.equal(request.TableName, tableName);
      assert.equal(request.ConsistentRead, true);
      assert.deepEqual(request.Key.SK, { S: "BOOTSTRAP" });
      calls.reads++;
      return { Item: rows.get(request.Key.PK.S!) };
    },
    put: async (request) => {
      assert.equal(request.TableName, tableName);
      assert.equal(request.ConditionExpression, "attribute_not_exists(PK)");
      const key = request.Item.PK.S!;
      calls.putAttempts++;
      if (rows.has(key)) {
        throw Object.assign(new Error("fixture conditional check"), {
          name: "ConditionalCheckFailedException",
        });
      }
      rows.set(key, request.Item);
      calls.writes++;
      if (lostResponses.delete(key)) {
        throw new Error("fixture lost the acknowledged write");
      }
    },
  });
  return { handle, rows, calls };
}

test("authenticated role HELLO is canonical, principal-pinned, bounded and expiring", async () => {
  const { handle, rows, calls } = fixture();
  const hello = payload();
  const canonical = JSON.stringify(hello);
  const reordered = JSON.stringify({
    recipientPublicKey: hello.recipientPublicKey,
    bootNonce: hello.bootNonce,
    protocolVersion: hello.protocolVersion,
  });
  const [first, concurrent] = await Promise.all([
    handle(event(reordered)),
    handle(event(canonical)),
  ]);
  assert.equal(first.statusCode, 204);
  assert.equal(concurrent.statusCode, 503);
  const key = `HELLO#BROKER#${roleId}:${taskId}`;
  assert.deepEqual(rows.get(key), {
    PK: { S: key },
    SK: { S: "BOOTSTRAP" },
    awsUserId: { S: `${roleId}:${taskId}` },
    payload: { S: canonical },
    protocolVersion: { N: "1" },
    helloIssuer: { S: "iam-url-v1" },
    helloSha256: { S: createHash("sha256").update(canonical).digest("hex") },
    expiresAt: { N: String(calls.clock + 3600) },
  });
  calls.clock += 200;
  assert.equal((await handle(event(canonical))).statusCode, 204);
  assert.equal(
    (await handle(event(JSON.stringify(payload())))).statusCode,
    409,
  );
  assert.equal(calls.putAttempts, 2);
  assert.equal(calls.writes, 1);
  assert.equal(rows.get(key)?.expiresAt.N, String(calls.clock + 3400));

  assert.equal((await handle(event(canonical, secondId))).statusCode, 204);
  assert.equal(rows.size, 2);
  assert.equal(calls.writes, 2);
  calls.clock += 3400;
  assert.equal((await handle(event(canonical))).statusCode, 409);
  assert.equal(calls.putAttempts, 3);
  rows.delete(key); // Model the separate DynamoDB TTL removal after logical expiry.
  assert.equal((await handle(event(canonical))).statusCode, 204);
  assert.equal(rows.get(key)?.expiresAt.N, String(calls.clock + 3600));
});

test("invalid or disabled requests perform no write and disclose no payload", async () => {
  const { handle, calls } = fixture();
  const hello = payload();
  const valid = JSON.stringify(hello);
  const trusted = event(valid);
  const forbidden = [
    event(valid, taskId, "other-role"),
    {
      ...trusted,
      requestContext: {
        ...trusted.requestContext,
        authorizer: {
          iam: {
            ...trusted.requestContext.authorizer.iam,
            accountId: "999900001111",
          },
        },
      },
    },
    {
      ...trusted,
      requestContext: { ...trusted.requestContext, authorizer: {} },
    },
  ];
  for (const input of forbidden) {
    assert.deepEqual(await handle(input), { statusCode: 403 });
  }
  const malformed = [
    event(JSON.stringify({ ...hello, awsUserId: "ignored-input" })),
    event(JSON.stringify({ ...hello, bootNonce: Array(32).fill(0) })),
    event(JSON.stringify({ ...hello, recipientPublicKey: Array(65).fill(4) })),
    event(valid + " ".repeat(1024)),
    { ...trusted, isBase64Encoded: true },
  ];
  for (const input of malformed) {
    assert.deepEqual(await handle(input), { statusCode: 400 });
  }
  assert.deepEqual(await fixture(false).handle(trusted), { statusCode: 403 });
  assert.deepEqual(calls, {
    reads: 0,
    putAttempts: 0,
    writes: 0,
    clock: 1_700_000_000,
  });
});

test("EC2 and Fargate sharing a role replay lost responses without rewriting another principal’s HELLO or expiry", async () => {
  const burst = Array.from({ length: 8 }, (_, index) => {
    const suffix = (index + 1).toString(16);
    const id =
      index < 4 ? `i-${suffix.padStart(17, "0")}` : suffix.padStart(32, "0");
    return {
      id,
      body: JSON.stringify(payload()),
      key: `HELLO#BROKER#${roleId}:${id}`,
    };
  });
  const { handle, rows, calls } = fixture(
    true,
    new Set([burst[0]!.key, burst[4]!.key]),
  );
  const statuses = await Promise.all(
    burst.map(({ body, id }) => handle(event(body, id))),
  );
  assert.deepEqual(
    statuses.map(({ statusCode }) => statusCode),
    [503, 204, 204, 204, 503, 204, 204, 204],
  );
  assert.equal(rows.size, burst.length);
  for (const { key, body } of burst) {
    assert.equal(rows.get(key)?.payload.S, body);
    assert.equal(rows.get(key)?.expiresAt.N, String(calls.clock + 3_600));
  }
  const receipts = structuredClone(rows);
  calls.clock += 10;
  const retries = await Promise.all(
    burst.map(({ body, id }) => handle(event(body, id))),
  );
  assert.ok(retries.every(({ statusCode }) => statusCode === 204));
  const crossed = await handle(event(burst[0]!.body, burst[4]!.id));
  assert.equal(crossed.statusCode, 409);
  assert.deepEqual(rows, receipts);
  assert.equal(calls.putAttempts, burst.length);
  assert.equal(calls.writes, burst.length);
});
