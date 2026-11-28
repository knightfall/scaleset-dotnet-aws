import { Stack } from "aws-cdk-lib";
import { PolicyStatement } from "aws-cdk-lib/aws-iam";
import type { Construct } from "constructs";
import { awsPartition } from "../config/semantics.ts";
import type {
  AwsEnvironment,
  RunnerControllerRuntime,
} from "../config/types.ts";
import { parseYaml } from "../config/yaml.ts";
import { readRegularDeploymentDocument } from "./deployment-document.ts";

const bindings = [
  "bootstrapAssetKeyArn",
  "trackingTableKeyArn",
  "snapshotKeyArn",
  "fargateQuotaKeyArn",
] as const;
export type ImportedEncryption = Partial<
  Record<(typeof bindings)[number], string>
>;

export function exactKeyArn(
  value: unknown,
  { account, region }: AwsEnvironment,
): value is string {
  const prefix = `arn:${awsPartition(region)}:kms:${region}:${account}:key/`;
  return (
    typeof value === "string" &&
    value.startsWith(prefix) &&
    /^(?:[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}|mrk-[a-f0-9]{32})(?![\s\S])/u.test(
      value.slice(prefix.length),
    )
  );
}

export function resolveImportedEncryption(
  value: unknown,
  runtime: RunnerControllerRuntime,
  hasQuotaObject: boolean,
): ImportedEncryption {
  if (value === undefined) return {};
  if (
    typeof value !== "object" ||
    value === null ||
    Array.isArray(value) ||
    Object.keys(value).some(
      (key) => !bindings.includes(key as (typeof bindings)[number]),
    )
  ) {
    throw new Error(
      "Invalid imported encryption document. Use only supported, exact KMS key ARN fields.",
    );
  }
  const result: ImportedEncryption = {};
  for (const key of bindings) {
    const arn = (value as Record<string, unknown>)[key];
    if (arn === undefined) continue;
    if (!exactKeyArn(arn, runtime.spec.aws)) {
      throw new Error(
        `Invalid imported encryption setting ${key}. Use a literal key ARN in the deployment account and Region.`,
      );
    }
    result[key] = arn;
  }
  const { dryRun, mode, tracking } = runtime.spec;
  if (result.trackingTableKeyArn && tracking.table.type !== "import") {
    throw new Error(
      "trackingTableKeyArn requires an imported lifecycle table.",
    );
  }
  if (
    result.snapshotKeyArn &&
    (mode !== "dry-run" ||
      dryRun.demand.type !== "snapshot" ||
      !("s3Uri" in dryRun.demand))
  ) {
    throw new Error("snapshotKeyArn requires a deployed dry-run S3 snapshot.");
  }
  if (result.fargateQuotaKeyArn && !hasQuotaObject) {
    throw new Error(
      "fargateQuotaKeyArn requires a configured Fargate quota object.",
    );
  }
  return result;
}

export function readImportedEncryption(path: string | undefined): unknown {
  if (path === undefined) return undefined;
  try {
    const bytes = readRegularDeploymentDocument(path);
    if (bytes === undefined) {
      throw new Error("Imported encryption input exceeds 64 KiB.");
    }
    const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    parseYaml(text); // Reject duplicate JSON members.
    return JSON.parse(text) as unknown;
  } catch {
    throw new Error("Invalid imported encryption JSON document.");
  }
}

export function s3ObjectDecrypt(
  scope: Construct,
  keyArn: string,
  objectArn: string,
): PolicyStatement {
  const { region } = Stack.of(scope);
  return new PolicyStatement({
    actions: ["kms:Decrypt"],
    resources: [keyArn],
    conditions: {
      StringEquals: {
        // KMS service context names use this suffix in every partition.
        "kms:ViaService": `s3.${region}.amazonaws.com`,
        "kms:EncryptionContext:aws:s3:arn": objectArn,
      },
    },
  });
}

export function trackingTableDecrypt(
  scope: Construct,
  keyArn: string,
  tableName: string,
): PolicyStatement {
  const { account, region } = Stack.of(scope);
  return new PolicyStatement({
    actions: ["kms:Decrypt"],
    resources: [keyArn],
    conditions: {
      StringEquals: {
        "kms:ViaService": `dynamodb.${region}.amazonaws.com`,
        "kms:EncryptionContext:aws:dynamodb:tableName": tableName,
        "kms:EncryptionContext:aws:dynamodb:subscriberId": account,
      },
    },
  });
}
