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
import * as iam from "aws-cdk-lib/aws-iam";
import * as kms from "aws-cdk-lib/aws-kms";
import * as sqs from "aws-cdk-lib/aws-sqs";
import type { Construct } from "constructs";
import type { compileConfig } from "../config/compiler.ts";
import { awsPartition } from "../config/semantics.ts";
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
import { helloIngress } from "./hello-ingress.ts";
import {
  resolveImportedEncryption,
  s3ObjectDecrypt,
  trackingTableDecrypt,
} from "./imported-encryption.ts";
import {
  bootstrapPolicy,
  ec2Profiles,
  ec2SupervisorStatusPolicy,
  fargateProfiles,
  grantEc2,
  grantFargate,
  grantGitHubCredentials,
  grantHostFence,
  grantTableAccess,
} from "./runner-iam.ts";
import type { RunnerCluster } from "./runner-iam.ts";
import { RuntimeArtifact } from "./runtime-artifact.ts";

const maximumHelloConcurrencyPerRole = 16;

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
    const encryption = resolveImportedEncryption(
      props.encryption,
      runtime,
      Boolean(fargatePreflight?.quotaSource),
    );
    const telemetry = resolveControllerTelemetry(props.telemetry, spec.aws);

    const active = spec.mode === "active";
    const inspect =
      !active && spec.dryRun.inspectAws && spec.dryRun.demand.type === "none";
    const enabled = spec.profiles
      .filter(({ state }) => state !== "disabled")
      .toSorted((a, b) => (a.id < b.id ? -1 : Number(a.id > b.id)));
    const supportsHelloIngress = awsPartition(spec.aws.region) === "aws";
    if (active && enabled.length > 0 && !supportsHelloIngress) {
      throw new Error(
        "Active runner HELLO requires Lambda Function URLs, which are unavailable in this AWS partition.",
      );
    }
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
      encryption.bootstrapAssetKeyArn,
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
        conditions: {
          StringEquals: {
            "s3:VersionId": artifact.versionId,
            "s3:ResourceAccount": this.account,
          },
        },
      }),
    );
    if (fargatePreflight?.quotaSource) {
      const { bucket, key } = fargatePreflight.quotaSource;
      const objectArn = this.formatArn({
        service: "s3",
        account: "",
        region: "",
        resource: bucket,
        resourceName: key,
        arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
      });
      this.controllerRole.addToPolicy(
        new iam.PolicyStatement({
          actions: ["s3:GetObject"],
          resources: [objectArn],
          conditions: { StringEquals: { "s3:ResourceAccount": this.account } },
        }),
      );
      if (encryption.fargateQuotaKeyArn) {
        this.controllerRole.addToPolicy(
          s3ObjectDecrypt(this, encryption.fargateQuotaKeyArn, objectArn),
        );
      }
    }
    if (active) {
      grantTableAccess(this.controllerRole, this.trackingTable);
      if (encryption.trackingTableKeyArn) {
        this.controllerRole.addToPolicy(
          trackingTableDecrypt(
            this,
            encryption.trackingTableKeyArn,
            this.trackingTable.tableName,
          ),
        );
      }
    } else {
      this.grantSnapshot(runtime, encryption.snapshotKeyArn);
    }
    if (encryption.trackingTableKeyArn) {
      new CfnOutput(this, "TrackingTableKmsKeyArn", {
        value: encryption.trackingTableKeyArn,
      });
    }
    grantEc2(this.controllerRole, runtime, ec2, active);
    grantFargate(this.controllerRole, runtime, fargate, clusters, active);
    const runnerPolicyDependencies = supportsHelloIngress
      ? this.configureBootstrapRoles(
          enabled,
          active,
          encryption.trackingTableKeyArn,
        )
      : [];

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
      if (spec.controller.predecessorCapture) {
        this.controllerRole.addToPolicy(
          new iam.PolicyStatement({
            actions: ["logs:FilterLogEvents"],
            resources: [spec.controller.predecessorCapture.logGroupArn],
          }),
        );
      }
      grantGitHubCredentials(this.controllerRole, runtime, enabled);
      const statusRoles = new Set(
        ec2
          .filter(
            ({ provisioner }) => provisioner.statusQualification !== undefined,
          )
          .map(({ provisioner }) => provisioner.bootstrapRoleArn),
      );
      for (const arn of statusRoles) {
        const id = stableId("Ec2SupervisorStatus", arn);
        const role = iam.Role.fromRoleArn(this, id, arn);
        const policy = new iam.Policy(this, `${id}Policy`, {
          roles: [role],
          statements: [ec2SupervisorStatusPolicy(this.trackingTable)],
        });
        runnerPolicyDependencies.push(policy);
      }
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
    const service = createControllerService(this, {
      spec,
      clusterArn: this.hostingCluster.arn,
      taskRole: this.controllerRole,
      command: this.runtimeArguments,
      fargatePreflight: fargatePreflight?.environment,
      telemetry,
    });
    service.node.addDependency(...runnerPolicyDependencies);
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

  private configureBootstrapRoles(
    profiles: readonly RunnerProfile[],
    active: boolean,
    tableKeyArn?: string,
  ) {
    const dependencies: Construct[] = [];
    const concurrencyByRole = new Map<string, number>();
    for (const { capacity, provisioner } of profiles) {
      const arn = provisioner.bootstrapRoleArn;
      const configured =
        (concurrencyByRole.get(arn) ?? 0) + capacity.maxRunners;
      concurrencyByRole.set(
        arn,
        active ? Math.min(configured, maximumHelloConcurrencyPerRole) : 0,
      );
    }
    for (const arn of [...concurrencyByRole.keys()].sort()) {
      const id = stableId("Bootstrap", arn);
      const role = iam.Role.fromRoleArn(this, id, arn);
      const ingress = helloIngress(
        this,
        id,
        this.trackingTable,
        role,
        concurrencyByRole.get(arn)!,
        tableKeyArn,
      );
      if (active) {
        const policy = new iam.Policy(this, `${id}Policy`, {
          roles: [role],
          statements: [
            ...bootstrapPolicy(this.trackingTable),
            ...(tableKeyArn
              ? [
                  trackingTableDecrypt(
                    this,
                    tableKeyArn,
                    this.trackingTable.tableName,
                  ),
                ]
              : []),
            ...ingress.statements,
          ],
        });
        dependencies.push(...ingress.dependencies, policy);
      }
    }
    return dependencies;
  }

  private grantSnapshot(runtime: RunnerControllerRuntime, keyArn?: string) {
    const demand = runtime.spec.dryRun.demand;
    if (demand.type !== "snapshot" || !("s3Uri" in demand)) return;
    // The shared runtime schema admits only literal snapshot addresses. IAM and the SDK use the same key.
    const path = demand.s3Uri.slice("s3://".length);
    const objectArn = this.formatArn({
      service: "s3",
      account: "",
      region: "",
      resource: path,
    });
    this.controllerRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["s3:GetObjectVersion"],
        resources: [objectArn],
        conditions: {
          StringEquals: {
            "s3:VersionId": demand.versionId,
            "s3:ResourceAccount": runtime.spec.aws.account,
          },
        },
      }),
    );
    if (keyArn)
      this.controllerRole.addToPolicy(s3ObjectDecrypt(this, keyArn, objectArn));
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
        conditions: { StringEquals: { "aws:SourceAccount": this.account } },
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
    for (const destination of [queue, dlq]) {
      destination.addToResourcePolicy(
        new iam.PolicyStatement({
          principals: [new iam.ServicePrincipal("events.amazonaws.com")],
          actions: ["sqs:SendMessage"],
          resources: [destination.queueArn],
          conditions: { ArnEquals: { "aws:SourceArn": rule.ruleArn } },
        }),
      );
    }
    // The standard CDK SQS target adds broad queue and KMS grants for encrypted targets.
    rule.addTarget({
      bind: () => ({
        arn: queue.queueArn,
        deadLetterConfig: { arn: dlq.queueArn },
      }),
    });
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
      encryption: environment.encryption,
    });
    return app.synth().getStackArtifact(stack.artifactId);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}
