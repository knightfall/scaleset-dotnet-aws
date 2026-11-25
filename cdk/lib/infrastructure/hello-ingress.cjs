/* eslint-disable @typescript-eslint/no-require-imports -- CloudFormation packages the inline Node.js handler as CommonJS. */
const { createHash, ECDH } = require("node:crypto");

const maximumBodyBytes = 1024;
const lifetimeSeconds = 3600;
const principalPattern =
  /^AROA[A-Z0-9]{1,60}:(i-(?:[a-f0-9]{8}|[a-f0-9]{17})|[a-f0-9]{32})$/;
const helloIssuer = "iam-url-v1";

function canonicalHello(body) {
  if (typeof body !== "string" || Buffer.byteLength(body) > maximumBodyBytes) {
    return null;
  }
  try {
    const { protocolVersion, bootNonce, recipientPublicKey, ...rest } =
      JSON.parse(body);
    if (
      protocolVersion !== 1 ||
      Object.keys(rest).length ||
      !bytes(bootNonce, 32) ||
      !bootNonce.some((value) => value !== 0) ||
      !bytes(recipientPublicKey, 65)
    ) {
      return null;
    }
    const encoded = Buffer.from(recipientPublicKey);
    if (
      encoded[0] !== 4 ||
      !ECDH.convertKey(
        encoded,
        "prime256v1",
        undefined,
        undefined,
        "uncompressed",
      ).equals(encoded)
    ) {
      return null;
    }
    return JSON.stringify({ protocolVersion, bootNonce, recipientPublicKey });
  } catch {
    return null;
  }
}

function bytes(value, length) {
  return (
    Array.isArray(value) &&
    value.length === length &&
    value.every((byte) => Number.isInteger(byte) && byte >= 0 && byte <= 255)
  );
}

function createHandler({
  read,
  put,
  now = Date.now,
  tableName,
  account,
  partition,
  roleName,
  enabled,
}) {
  const callerPrefix = `arn:${partition}:sts::${account}:assumed-role/${roleName}/`;
  return async (event) => {
    const context = event?.requestContext;
    const caller = context?.authorizer?.iam;
    const principal = caller?.userId;
    const identity =
      typeof principal === "string" ? principal.match(principalPattern) : null;
    if (
      !enabled ||
      caller?.accountId !== account ||
      !identity ||
      caller.userArn !== callerPrefix + identity[1]
    ) {
      return { statusCode: 403 };
    }
    if (
      event.version !== "2.0" ||
      event.rawPath !== "/" ||
      event.rawQueryString ||
      context.http?.method !== "POST" ||
      event.isBase64Encoded !== false
    ) {
      return { statusCode: 400 };
    }
    const payload = canonicalHello(event.body);
    if (payload === null) {
      return { statusCode: 400 };
    }

    const key = {
      PK: { S: `HELLO#BROKER#${principal}` },
      SK: { S: "BOOTSTRAP" },
    };
    const digest = createHash("sha256").update(payload).digest("hex");
    const seconds = Math.floor(now() / 1000);
    try {
      const existing = await read({
        TableName: tableName,
        Key: key,
        ConsistentRead: true,
      });
      if (existing.Item) {
        const item = existing.Item;
        const exact =
          item.helloIssuer?.S === helloIssuer &&
          item.awsUserId?.S === principal &&
          item.protocolVersion?.N === "1" &&
          Number(item.expiresAt?.N) > seconds &&
          item.helloSha256?.S === digest &&
          item.payload?.S === payload;
        return { statusCode: exact ? 204 : 409 };
      }
      await put({
        TableName: tableName,
        Item: {
          ...key,
          awsUserId: { S: principal },
          protocolVersion: { N: "1" },
          helloSha256: { S: digest },
          payload: { S: payload },
          helloIssuer: { S: helloIssuer },
          expiresAt: { N: String(seconds + lifetimeSeconds) },
        },
        ConditionExpression: "attribute_not_exists(PK)",
      });
      return { statusCode: 204 };
    } catch {
      // An uncertain write can be retried using the same body. Keep SDK errors out of runner responses and logs.
      return { statusCode: 503 };
    }
  };
}

let handler;
exports.createHandler = createHandler;
exports.handler = (event) => {
  if (!handler) {
    const {
      DynamoDBClient,
      GetItemCommand,
      PutItemCommand,
    } = require("@aws-sdk/client-dynamodb");
    const client = new DynamoDBClient({ maxAttempts: 1 });
    handler = createHandler({
      read: (request) => client.send(new GetItemCommand(request)),
      put: (request) => client.send(new PutItemCommand(request)),
      tableName: process.env.TRACKING_TABLE,
      account: process.env.BOOTSTRAP_ACCOUNT,
      partition: process.env.BOOTSTRAP_PARTITION,
      roleName: process.env.BOOTSTRAP_ROLE_NAME,
      enabled: process.env.HELLO_ENABLED === "true",
    });
  }
  return handler(event);
};
