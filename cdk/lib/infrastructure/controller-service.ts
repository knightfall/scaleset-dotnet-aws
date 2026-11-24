import { Duration, RemovalPolicy, Stack } from "aws-cdk-lib";
import * as ecr from "aws-cdk-lib/aws-ecr";
import * as ecs from "aws-cdk-lib/aws-ecs";
import * as iam from "aws-cdk-lib/aws-iam";
import * as logs from "aws-cdk-lib/aws-logs";
import type { Construct } from "constructs";
import type { RunnerControllerRuntime } from "../config/types.ts";
import type { resolveControllerTelemetry } from "./controller-telemetry.ts";
import { fargatePreflightEnvironmentName } from "./fargate-preflight.ts";

export const controllerHealthAddress = "127.0.0.1:8080";

interface ControllerServiceProps {
  readonly spec: RunnerControllerRuntime["spec"];
  readonly clusterArn: string;
  readonly taskRole: iam.IRole;
  readonly command: readonly string[];
  readonly fargatePreflight?: string;
  readonly telemetry: ReturnType<typeof resolveControllerTelemetry>;
}

const memoryGiBRanges = new Map<number, readonly [number, number, number]>([
  [512, [1, 4, 1]],
  [1024, [2, 8, 1]],
  [2048, [4, 16, 1]],
  [4096, [8, 30, 1]],
  [8192, [16, 60, 4]],
  [16384, [32, 120, 8]],
]);

function supportsFargateSize(cpu: number, memoryMiB: number): boolean {
  if (cpu === 256) return [512, 1024, 2048].includes(memoryMiB);
  const memoryGiB = memoryMiB / 1024;
  if (cpu === 32768) return [60, 120, 244].includes(memoryGiB);
  const range = memoryGiBRanges.get(cpu);
  if (!range) return false;
  const [minimum, maximum, increment] = range;
  return (
    memoryGiB >= minimum &&
    memoryGiB <= maximum &&
    (memoryGiB - minimum) % increment === 0
  );
}

export function createControllerService(
  scope: Construct,
  props: ControllerServiceProps,
) {
  const { image, hosting } = props.spec.controller;
  const network = props.spec.networks[hosting.network];
  if (!supportsFargateSize(hosting.cpu, hosting.memoryMiB)) {
    throw new Error(
      "The controller CPU and memory must be a valid Fargate size.",
    );
  }
  if (network.subnets.length > 16 || network.securityGroupIds.length > 5) {
    throw new Error("The controller network exceeds the ECS awsvpc limits.");
  }

  const stack = Stack.of(scope);
  const executionRole = new iam.Role(scope, "ControllerExecutionRole", {
    assumedBy: new iam.ServicePrincipal("ecs-tasks.amazonaws.com", {
      conditions: {
        StringEquals: { "aws:SourceAccount": stack.account },
        ArnLike: {
          "aws:SourceArn": stack.formatArn({ service: "ecs", resource: "*" }),
        },
      },
    }),
  });
  const digestSeparator = image.lastIndexOf("@");
  const repositoryName = image.slice(image.indexOf("/") + 1, digestSeparator);
  const repository = ecr.Repository.fromRepositoryName(
    scope,
    "ControllerImageRepository",
    repositoryName,
  );
  const logGroup = new logs.LogGroup(scope, "ControllerLogs", {
    logGroupName: `/ecs/${stack.stackName}`,
    retention: logs.RetentionDays.ONE_MONTH,
    removalPolicy: RemovalPolicy.RETAIN,
  });
  const task = new ecs.FargateTaskDefinition(scope, "ControllerTask", {
    taskRole: props.taskRole,
    executionRole,
    cpu: hosting.cpu,
    memoryLimitMiB: hosting.memoryMiB,
    runtimePlatform: {
      operatingSystemFamily: ecs.OperatingSystemFamily.LINUX,
      cpuArchitecture:
        hosting.cpuArchitecture === "arm64"
          ? ecs.CpuArchitecture.ARM64
          : ecs.CpuArchitecture.X86_64,
    },
  });
  const environment = { ...props.telemetry.environment };
  if (props.fargatePreflight !== undefined) {
    environment[fargatePreflightEnvironmentName] = props.fargatePreflight;
  }
  task.addContainer("Controller", {
    image: ecs.ContainerImage.fromEcrRepository(
      repository,
      image.slice(digestSeparator + 1),
    ),
    command: [...props.command],
    environment: Object.keys(environment).length > 0 ? environment : undefined,
    essential: true,
    readonlyRootFilesystem: true,
    stopTimeout: Duration.seconds(90),
    logging: ecs.LogDrivers.awsLogs({
      logGroup,
      streamPrefix: "controller",
      mode: ecs.AwsLogDriverMode.NON_BLOCKING,
    }),
    healthCheck: {
      command: [
        "CMD",
        "dotnet",
        "/app/Knightfall.ScaleSet.Aws.Controller.dll",
        "healthcheck",
      ],
      startPeriod: Duration.seconds(30),
    },
  });
  const { headers } = props.telemetry;
  if (headers.length > 0) {
    const taskDefinition = task.node.defaultChild as ecs.CfnTaskDefinition;
    // ECS retrieves these references using the execution role at task startup.
    taskDefinition.addPropertyOverride(
      "ContainerDefinitions.0.Secrets",
      headers.map(({ name, secretArn }) => ({
        Name: name,
        ValueFrom: secretArn,
      })),
    );
    executionRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["secretsmanager:GetSecretValue"],
        resources: [
          ...new Set(headers.map(({ secretArn }) => secretArn)),
        ].sort(),
      }),
    );
    for (const { secretArn, kmsKeyArn } of headers) {
      if (kmsKeyArn === undefined) continue;
      executionRole.addToPolicy(
        new iam.PolicyStatement({
          actions: ["kms:Decrypt"],
          resources: [kmsKeyArn],
          conditions: {
            StringEquals: {
              "kms:ViaService": `secretsmanager.${stack.region}.${stack.urlSuffix}`,
              "kms:EncryptionContext:SecretARN": secretArn,
            },
          },
        }),
      );
    }
  }
  // Exact configured IDs avoid a VPC lookup, default security group, or inferred subnet selection.
  const service = new ecs.CfnService(scope, "ControllerService", {
    cluster: props.clusterArn,
    taskDefinition: task.taskDefinitionArn,
    launchType: "FARGATE",
    platformVersion: "1.4.0",
    schedulingStrategy: "REPLICA",
    desiredCount: 1,
    enableExecuteCommand: false,
    availabilityZoneRebalancing: "DISABLED",
    deploymentController: { type: "ECS" },
    deploymentConfiguration: {
      minimumHealthyPercent: 0,
      maximumPercent: 100,
      deploymentCircuitBreaker: { enable: true, rollback: true },
    },
    networkConfiguration: {
      awsvpcConfiguration: {
        assignPublicIp: "DISABLED",
        subnets: network.subnets.map(({ id }) => id).toSorted(),
        securityGroups: network.securityGroupIds.toSorted(),
      },
    },
  });
  service.node.addDependency(props.taskRole, executionRole);
  return service;
}
