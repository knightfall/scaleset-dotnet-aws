import { createHash } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  App,
  ArnFormat,
  CfnOutput,
  Duration,
  RemovalPolicy,
  Stack,
} from "aws-cdk-lib";
import * as dynamodb from "aws-cdk-lib/aws-dynamodb";
import * as ecs from "aws-cdk-lib/aws-ecs";
import * as events from "aws-cdk-lib/aws-events";
import * as targets from "aws-cdk-lib/aws-events-targets";
import * as iam from "aws-cdk-lib/aws-iam";
import * as kms from "aws-cdk-lib/aws-kms";
import * as sqs from "aws-cdk-lib/aws-sqs";
import type { Construct } from "constructs";
import type { compileConfig } from "../config/compiler.ts";
import type {
  RunnerControllerRuntime,
  RunnerProfile,
} from "../config/types.ts";
import {
  controllerHealthAddress,
  createControllerService,
} from "./controller-service.ts";
import { resolveControllerTelemetry } from "./controller-telemetry.ts";
import {
  controllerStackName,
  deploymentIdentity,
  generationValue,
  trackingTableName,
} from "./deployment-input.ts";
import type { ControllerDeploymentEnvironment } from "./deployment-input.ts";
import { validateFargatePreflight } from "./fargate-preflight.ts";
import {
  bootstrapPolicy,
  ec2Profiles,
  fargateProfiles,
  grantEc2,
  grantFargate,
  grantGitHubCredentials,
  grantHostFence,
  grantTableAccess,
} from "./runner-iam.ts";
import type { RunnerCluster } from "./runner-iam.ts";
import { RuntimeArtifact } from "./runtime-artifact.ts";

export interface ControllerInfrastructureProps extends ControllerDeploymentEnvironment {
  readonly compiled: ReturnType<typeof compileConfig>;
  readonly generation: string;
  readonly artifactPath: string;
}

export class ControllerInfrastructure extends Stack {
  readonly controllerRole: iam.Role;
  readonly trackingTable: dynamodb.ITable;
  readonly hostingCluster: RunnerCluster;
  readonly runtimeArguments: readonly string[];

  constructor(
    scope: Construct,
    id: string,
    props: ControllerInfrastructureProps,
  ) {
    const { runtime, sha256 } = props.compiled;
    const { spec } = runtime;
    super(scope, id, {
      stackName: controllerStackName(runtime),
      env: spec.aws,
    });
    generationValue(props.generation);
    const fargatePreflight = validateFargatePreflight(
      runtime,
      props.fargatePreflight,
    );
    const telemetry = resolveControllerTelemetry(props.telemetry, spec.aws);

    const active = spec.mode === "active";
    const inspect = !active && spec.dryRun.inspectAws;
    const enabled = spec.profiles
      .filter(({ state }) => state !== "disabled")
      .toSorted((a, b) => (a.id < b.id ? -1 : Number(a.id > b.id)));
    const selected = active || inspect ? enabled : [];
    const ec2 = ec2Profiles(selected);
    const fargate = fargateProfiles(selected);
    const clusterAliases = new Set([
      spec.controller.hosting.cluster,
      ...fargate.map(({ provisioner }) => provisioner.cluster),
    ]);
    const clusters = this.clusters(runtime, clusterAliases);
    this.hostingCluster = clusters.get(spec.controller.hosting.cluster)!;
    this.trackingTable = this.configureTable(runtime);

    const artifact = new RuntimeArtifact(
      this,
      "RuntimeConfig",
      props.artifactPath,
      sha256,
    );
    this.controllerRole = new iam.Role(this, "ControllerTaskRole", {
      assumedBy: new iam.ServicePrincipal("ecs-tasks.amazonaws.com", {
        conditions: {
          StringEquals: { "aws:SourceAccount": this.account },
          ArnLike: {
            "aws:SourceArn": this.formatArn({ service: "ecs", resource: "*" }),
          },
        },
      }),
    });
    this.controllerRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["s3:GetObjectVersion"],
        resources: [artifact.objectArn],
        conditions: { StringEquals: { "s3:VersionId": artifact.versionId } },
      }),
    );
    if (fargatePreflight?.quotaSource) {
      const { bucket, key } = fargatePreflight.quotaSource;
      this.controllerRole.addToPolicy(
        new iam.PolicyStatement({
          actions: ["s3:GetObject"],
          resources: [
            this.formatArn({
              service: "s3",
              account: "",
              region: "",
              resource: bucket,
              resourceName: key,
              arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
            }),
          ],
          conditions: { StringEquals: { "s3:ResourceAccount": this.account } },
        }),
      );
    }
    if (!active) {
      this.grantSnapshot(runtime);
    }
    grantTableAccess(this.controllerRole, this.trackingTable, active, inspect);
    grantEc2(this.controllerRole, runtime, ec2, active);
    grantFargate(this.controllerRole, runtime, fargate, clusters, active);

    const args = [
      "run",
      "--config-s3-uri",
      artifact.uri,
      "--config-version-id",
      artifact.versionId,
      "--sha256",
      sha256,
      "--generation",
      props.generation,
      "--health-addr",
      controllerHealthAddress,
    ];
    if (active) {
      grantHostFence(this.controllerRole, this.hostingCluster);
      grantGitHubCredentials(this.controllerRole, runtime, enabled);
      this.attachBootstrapPolicies(enabled);
      if (ec2.length > 0) {
        const { queue, dlq } = this.observationQueues("Ec2");
        this.addEventRule(
          "Ec2StateChange",
          {
            source: ["aws.ec2"],
            detailType: ["EC2 Instance State-change Notification"],
            detail: { "instance-id": [{ prefix: "i-" }] },
          },
          queue,
          dlq,
        );
        args.push("--ec2-event-queue-url", queue.queueUrl);
      }
      if (fargate.length > 0) {
        const { queue, dlq } = this.observationQueues("Fargate");
        const observedPairs = new Set<string>();
        for (const { provisioner } of fargate) {
          const cluster = clusters.get(provisioner.cluster)!;
          const pair = `${cluster.name}/${provisioner.taskDefinitionArn}`;
          if (observedPairs.has(pair)) continue;
          observedPairs.add(pair);
          this.addEventRule(
            stableId("FargateStateChange", pair),
            {
              source: ["aws.ecs"],
              detailType: ["ECS Task State Change"],
              detail: {
                clusterArn: [cluster.arn],
                taskDefinitionArn: [provisioner.taskDefinitionArn],
              },
            },
            queue,
            dlq,
          );
        }
        args.push("--ecs-event-queue-url", queue.queueUrl);
      }
    }

    this.runtimeArguments = args;
    createControllerService(this, {
      spec,
      clusterArn: this.hostingCluster.arn,
      taskRole: this.controllerRole,
      command: this.runtimeArguments,
      fargatePreflight: fargatePreflight?.environment,
      telemetry,
    });
    new CfnOutput(this, "DeploymentIdentity", {
      value: deploymentIdentity(runtime),
    });
    new CfnOutput(this, "TrackingTableName", {
      value: this.trackingTable.tableName,
    });
    new CfnOutput(this, "RuntimeConfigUri", { value: artifact.uri });
    new CfnOutput(this, "RuntimeConfigVersionId", {
      value: artifact.versionId,
    });
    new CfnOutput(this, "RuntimeConfigSha256", { value: sha256 });
    new CfnOutput(this, "DeploymentGeneration", { value: props.generation });
    new CfnOutput(this, "ControllerTaskArguments", {
      value: this.toJsonString(args),
    });
  }

  private configureTable(runtime: RunnerControllerRuntime): dynamodb.ITable {
    const tableName = trackingTableName(runtime);
    if (runtime.spec.tracking.table.type === "import") {
      return dynamodb.Table.fromTableName(this, "Tracking", tableName);
    }
    const table = new dynamodb.Table(this, "Tracking", {
      tableName,
      partitionKey: { name: "PK", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "SK", type: dynamodb.AttributeType.STRING },
      encryption: dynamodb.TableEncryption.AWS_MANAGED,
      timeToLiveAttribute: "expiresAt",
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    table.addGlobalSecondaryIndex({
      indexName: "due",
      partitionKey: { name: "dueShard", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "dueAtMillis", type: dynamodb.AttributeType.NUMBER },
      projectionType: dynamodb.ProjectionType.KEYS_ONLY,
    });
    return table;
  }

  private clusters(
    runtime: RunnerControllerRuntime,
    needed: ReadonlySet<string>,
  ) {
    const aliases = new Map<string, RunnerCluster>();
    const created = new Map<string, RunnerCluster>();
    for (const alias of [...needed].sort()) {
      const cluster = runtime.spec.ecsClusters[alias];
      if (cluster.type === "import") {
        aliases.set(alias, {
          name: cluster.arn.slice(cluster.arn.lastIndexOf("/") + 1),
          arn: cluster.arn,
        });
        continue;
      }
      let resolved = created.get(cluster.name);
      if (!resolved) {
        const resource = new ecs.CfnCluster(
          this,
          stableId("Cluster", cluster.name),
          { clusterName: cluster.name },
        );
        resolved = { name: cluster.name, arn: resource.attrArn };
        created.set(cluster.name, resolved);
      }
      aliases.set(alias, resolved);
    }
    return aliases;
  }

  private attachBootstrapPolicies(profiles: readonly RunnerProfile[]) {
    const roleArns = [
      ...new Set(
        profiles.map(({ provisioner }) => provisioner.bootstrapRoleArn),
      ),
    ].sort();
    for (const arn of roleArns) {
      const id = stableId("Bootstrap", arn);
      const role = iam.Role.fromRoleArn(this, id, arn);
      new iam.Policy(this, `${id}Policy`, {
        roles: [role],
        statements: bootstrapPolicy(this.trackingTable),
      });
    }
  }

  private grantSnapshot(runtime: RunnerControllerRuntime) {
    const demand = runtime.spec.dryRun.demand;
    if (demand.type !== "snapshot" || !("s3Uri" in demand)) return;
    const path = demand.s3Uri.slice("s3://".length);
    this.controllerRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["s3:GetObjectVersion"],
        resources: [
          this.formatArn({
            service: "s3",
            account: "",
            region: "",
            resource: path,
          }),
        ],
        conditions: { StringEquals: { "s3:VersionId": demand.versionId } },
      }),
    );
  }

  private observationQueues(id: string) {
    const encryptionMasterKey = new kms.Key(this, `${id}EventKey`, {
      enableKeyRotation: true,
    });
    encryptionMasterKey.addToResourcePolicy(
      new iam.PolicyStatement({
        principals: [new iam.ServicePrincipal("events.amazonaws.com")],
        actions: ["kms:Decrypt", "kms:GenerateDataKey"],
        resources: ["*"],
      }),
    );
    const security = {
      encryption: sqs.QueueEncryption.KMS,
      encryptionMasterKey,
      enforceSSL: true,
    };
    const dlq = new sqs.Queue(this, `${id}EventDeadLetters`, {
      ...security,
      retentionPeriod: Duration.days(14),
    });
    const queue = new sqs.Queue(this, `${id}EventQueue`, {
      ...security,
      deadLetterQueue: { queue: dlq, maxReceiveCount: 5 },
    });
    this.controllerRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["sqs:ReceiveMessage", "sqs:DeleteMessage"],
        resources: [queue.queueArn],
      }),
    );
    encryptionMasterKey.grantDecrypt(this.controllerRole);
    return { queue, dlq };
  }

  private addEventRule(
    id: string,
    pattern: events.EventPattern,
    queue: sqs.Queue,
    dlq: sqs.Queue,
  ) {
    const rule = new events.Rule(this, id, {
      eventPattern: {
        ...pattern,
        account: [this.account],
        region: [this.region],
      },
    });
    rule.addTarget(new targets.SqsQueue(queue, { deadLetterQueue: dlq }));
  }
}

function stableId(prefix: string, value: string) {
  return prefix + createHash("sha256").update(value).digest("hex").slice(0, 16);
}

export function synthesizeController(
  compiled: ReturnType<typeof compileConfig>,
  generation: string,
  outdir: string | undefined = undefined,
  environment: ControllerDeploymentEnvironment = {},
) {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-runtime-"));
  try {
    const artifactPath = join(directory, "runtime.json");
    writeFileSync(artifactPath, compiled.json, {
      encoding: "utf8",
      mode: 0o600,
    });
    const app = new App({ outdir });
    const stack = new ControllerInfrastructure(app, "Controller", {
      compiled,
      generation,
      artifactPath,
      fargatePreflight: environment.fargatePreflight,
      telemetry: environment.telemetry,
    });
    return app.synth().getStackArtifact(stack.artifactId);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}
