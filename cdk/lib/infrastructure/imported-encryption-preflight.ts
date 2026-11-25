import type {
  AwsEnvironment,
  RunnerControllerRuntime,
} from "../config/types.ts";
import { awsPartition } from "../config/semantics.ts";
import { trackingTableName } from "./deployment-input.ts";
import { exactKeyArn } from "./imported-encryption.ts";
import type { ImportedEncryption } from "./imported-encryption.ts";

type Properties = Record<string, unknown>;
type Lookup = (description: string, ...args: string[]) => Properties;
type S3Object = {
  readonly bucket: string;
  readonly key: string;
  readonly version?: string;
};

function object(value: unknown): Properties | undefined {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Properties)
    : undefined;
}

export function runtimeCopySource(template: unknown): S3Object {
  const resources = object(object(template)?.Resources);
  const copies = Object.values(resources ?? {})
    .map(object)
    .filter((resource) => resource?.Type === "Custom::ScaleSetRuntimeArtifact");
  const props = object(copies[0]?.Properties);
  if (
    copies.length !== 1 ||
    !props ||
    typeof props.SourceBucket !== "string" ||
    !/^[a-z0-9.-]{3,63}$/u.test(props.SourceBucket) ||
    typeof props.SourceKey !== "string" ||
    !/^[a-f0-9]{64}\.json$/u.test(props.SourceKey)
  ) {
    throw new Error(
      "Could not identify the exact synthesized runtime bootstrap asset. Use the default CDK file asset destination.",
    );
  }
  return { bucket: props.SourceBucket, key: props.SourceKey };
}

function readKey(lookup: Lookup, arn: string, label: string) {
  const metadata = object(
    lookup(`${label} KMS DescribeKey`, "kms", "describe-key", "--key-id", arn)
      .KeyMetadata,
  );
  if (
    metadata?.Arn !== arn ||
    metadata.Enabled !== true ||
    metadata.KeyState !== "Enabled" ||
    metadata.KeySpec !== "SYMMETRIC_DEFAULT" ||
    metadata.KeyUsage !== "ENCRYPT_DECRYPT"
  ) {
    throw new Error(`${label} requires the exact enabled symmetric KMS key.`);
  }
  return metadata.KeyManager;
}

function verifyTable(
  runtime: RunnerControllerRuntime,
  encryption: ImportedEncryption,
  lookup: Lookup,
) {
  if (runtime.spec.tracking.table.type !== "import") return;
  const table = trackingTableName(runtime);
  const { account, region } = runtime.spec.aws;
  const observed = object(
    lookup(
      "Imported lifecycle table encryption read",
      "dynamodb",
      "describe-table",
      "--table-name",
      table,
    ).Table,
  );
  if (
    observed?.TableArn !==
      `arn:${awsPartition(region)}:dynamodb:${region}:${account}:table/${table}` ||
    observed.TableStatus !== "ACTIVE"
  ) {
    throw new Error(
      "Could not verify the imported lifecycle table identity and active state.",
    );
  }
  const expected = encryption.trackingTableKeyArn;
  if (observed.SSEDescription === undefined && expected === undefined) return; // AWS-owned table key.
  const sse = object(observed.SSEDescription);
  const arn = sse?.KMSMasterKeyArn;
  if (
    sse?.Status !== "ENABLED" ||
    sse.SSEType !== "KMS" ||
    sse.InaccessibleEncryptionDateTime !== undefined ||
    !exactKeyArn(arn, runtime.spec.aws) ||
    (expected !== undefined && arn !== expected)
  ) {
    throw new Error(
      "Imported lifecycle table encryption differs from the candidate or is unavailable. Verify DescribeTable and the pinned key ARN.",
    );
  }
  const manager = readKey(lookup, arn, "Imported lifecycle table");
  if (expected === undefined ? manager !== "AWS" : manager !== "CUSTOMER") {
    throw new Error(
      "Imported lifecycle table customer-managed encryption requires trackingTableKeyArn and reviewed DynamoDB key access.",
    );
  }
}

function verifyObject(
  source: S3Object,
  arn: string,
  label: string,
  aws: AwsEnvironment,
  lookup: Lookup,
) {
  const metadata = lookup(
    `${label} S3 HeadObject`,
    "s3api",
    "head-object",
    "--bucket",
    source.bucket,
    "--key",
    source.key,
    "--expected-bucket-owner",
    aws.account,
    ...(source.version ? ["--version-id", source.version] : []),
  );
  if (
    metadata.ServerSideEncryption !== "aws:kms" ||
    metadata.SSEKMSKeyId !== arn ||
    metadata.BucketKeyEnabled === true ||
    (source.version !== undefined && metadata.VersionId !== source.version)
  ) {
    throw new Error(
      `${label} must use the pinned SSE-KMS object key with S3 Bucket Keys disabled. Check the exact object and version.`,
    );
  }
}

export function verifyImportedEncryption(
  runtime: RunnerControllerRuntime,
  encryption: ImportedEncryption,
  quota: S3Object | undefined,
  bootstrap: S3Object | undefined,
  assetPublished: boolean,
  lookup: Lookup,
) {
  verifyTable(runtime, encryption, lookup);
  const { dryRun, aws } = runtime.spec;
  const keys = new Map<string, string>();
  if (encryption.bootstrapAssetKeyArn && bootstrap) {
    const arn = encryption.bootstrapAssetKeyArn;
    keys.set(arn, "CDK bootstrap asset");
    if (assetPublished) {
      verifyObject(bootstrap, arn, "CDK bootstrap source asset", aws, lookup);
    } else {
      const metadata = lookup(
        "CDK bootstrap bucket encryption read",
        "s3api",
        "get-bucket-encryption",
        "--bucket",
        bootstrap.bucket,
        "--expected-bucket-owner",
        aws.account,
      );
      const rules = object(metadata.ServerSideEncryptionConfiguration)?.Rules;
      const rule =
        Array.isArray(rules) && rules.length === 1
          ? object(rules[0])
          : undefined;
      const options = object(rule?.ApplyServerSideEncryptionByDefault);
      if (
        options?.SSEAlgorithm !== "aws:kms" ||
        (options.KMSMasterKeyID !== arn &&
          options.KMSMasterKeyID !== arn.slice(arn.lastIndexOf("/") + 1)) ||
        rule?.BucketKeyEnabled === true
      ) {
        throw new Error(
          "CDK bootstrap bucket must use the pinned SSE-KMS key with S3 Bucket Keys disabled. Check its default encryption.",
        );
      }
    }
  }
  if (
    encryption.snapshotKeyArn &&
    dryRun.demand.type === "snapshot" &&
    "s3Uri" in dryRun.demand
  ) {
    const path = dryRun.demand.s3Uri.slice("s3://".length);
    const delimiter = path.indexOf("/");
    keys.set(encryption.snapshotKeyArn, "S3 demand snapshot");
    verifyObject(
      {
        bucket: path.slice(0, delimiter),
        key: path.slice(delimiter + 1),
        version: dryRun.demand.versionId,
      },
      encryption.snapshotKeyArn,
      "S3 demand snapshot",
      aws,
      lookup,
    );
  }
  if (encryption.fargateQuotaKeyArn && quota) {
    keys.set(encryption.fargateQuotaKeyArn, "Fargate quota source");
    verifyObject(
      quota,
      encryption.fargateQuotaKeyArn,
      "Fargate quota source",
      aws,
      lookup,
    );
  }
  for (const [arn, label] of keys) {
    if (readKey(lookup, arn, label) !== "CUSTOMER") {
      throw new Error(
        `${label} requires a customer-managed KMS key for this opt-in binding.`,
      );
    }
  }
}
