// eslint-disable-next-line @typescript-eslint/no-require-imports -- CloudFormation loads inline Lambda code as a CommonJS index.js.
const { CopyObjectCommand, S3Client } = require("@aws-sdk/client-s3");

exports.begin = async (event) => {
  const { DestinationBucket, DestinationKey } = event.ResourceProperties;
  const physicalId = `${DestinationBucket}/${DestinationKey}`;
  return {
    PhysicalResourceId:
      event.RequestType === "Delete"
        ? (event.PhysicalResourceId ?? physicalId)
        : physicalId,
  };
};

exports.copy = async (event) => {
  if (event.RequestType === "Delete") {
    return { IsComplete: true };
  }

  const {
    Account,
    SourceBucket,
    SourceKey,
    DestinationBucket,
    DestinationKey,
  } = event.ResourceProperties;
  const client = new S3Client({});
  const copySource = [SourceBucket, ...SourceKey.split("/")]
    .map(encodeURIComponent)
    .join("/");
  const result = await client.send(
    new CopyObjectCommand({
      Bucket: DestinationBucket,
      CopySource: copySource,
      Key: DestinationKey,
      ExpectedBucketOwner: Account,
      ExpectedSourceBucketOwner: Account,
    }),
  );
  const versionId = result.VersionId;
  if (
    typeof versionId !== "string" ||
    versionId.length === 0 ||
    versionId === "null"
  ) {
    return { IsComplete: false };
  }
  return {
    IsComplete: true,
    Data: { VersionId: versionId },
  };
};
