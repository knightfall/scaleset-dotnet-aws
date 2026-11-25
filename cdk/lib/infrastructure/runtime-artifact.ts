import { readFileSync } from "node:fs";
import { CustomResource, Duration, RemovalPolicy, Stack } from "aws-cdk-lib";
import * as iam from "aws-cdk-lib/aws-iam";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as s3 from "aws-cdk-lib/aws-s3";
import * as staging from "aws-cdk-lib/aws-s3-assets";
import * as cr from "aws-cdk-lib/custom-resources";
import { Construct } from "constructs";
import { s3ObjectDecrypt } from "./imported-encryption.ts";

export class RuntimeArtifact extends Construct {
  readonly uri: string;
  readonly versionId: string;
  readonly objectArn: string;

  constructor(
    scope: Construct,
    id: string,
    inputPath: string,
    sha256: string,
    sourceKeyArn?: string,
  ) {
    super(scope, id);
    const asset = new staging.Asset(this, "Source", { path: inputPath });
    const bucket = new s3.Bucket(this, "Bucket", {
      versioned: true,
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const key = `runtime/${sha256}.json`;
    this.objectArn = bucket.arnForObjects(key);
    this.uri = bucket.s3UrlForObject(key);

    const code = readFileSync(
      new URL("./copy-runtime.cjs", import.meta.url),
      "utf8",
    );
    const begin = new lambda.Function(this, "Begin", {
      runtime: lambda.Runtime.NODEJS_24_X,
      handler: "index.begin",
      code: lambda.Code.fromInline(code),
    });
    const copy = new lambda.Function(this, "Copy", {
      runtime: lambda.Runtime.NODEJS_24_X,
      handler: "index.copy",
      timeout: Duration.seconds(60),
      code: lambda.Code.fromInline(code),
    });
    const sourceArn = asset.bucket.arnForObjects(asset.s3ObjectKey);
    copy.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["s3:GetObject"],
        resources: [sourceArn],
      }),
    );
    if (sourceKeyArn)
      copy.addToRolePolicy(s3ObjectDecrypt(this, sourceKeyArn, sourceArn));
    copy.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["s3:PutObject"],
        resources: [this.objectArn],
      }),
    );
    const provider = new cr.Provider(this, "Provider", {
      onEventHandler: begin,
      isCompleteHandler: copy,
      queryInterval: Duration.seconds(20),
      totalTimeout: Duration.minutes(16),
    });
    const result = new CustomResource(this, "Version", {
      resourceType: "Custom::ScaleSetRuntimeArtifact",
      serviceToken: provider.serviceToken,
      properties: {
        Account: Stack.of(this).account,
        SourceBucket: asset.s3BucketName,
        SourceKey: asset.s3ObjectKey,
        DestinationBucket: bucket.bucketName,
        DestinationKey: key,
      },
    });
    this.versionId = result.getAttString("VersionId");
  }
}
