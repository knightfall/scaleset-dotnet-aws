export interface AwsEnvironment {
  readonly account: string;
  readonly region: string;
}

export interface NetworkConfig {
  readonly vpcId: string;
  readonly subnets: readonly { readonly id: string; readonly az: string }[];
  readonly securityGroupIds: readonly string[];
}

export type EcsClusterConfig =
  | { readonly type: "create"; readonly name: string }
  | { readonly type: "import"; readonly arn: string };

export interface GitHubConnectionConfig {
  readonly scopeURL: string;
  readonly auth: {
    readonly type: "github-app" | "enterprise-pat";
    readonly secretArn: string;
  };
}

export type DryRunDemand =
  | { readonly type: "none" }
  | { readonly type: "snapshot" | "replay"; readonly path: string }
  | {
      readonly type: "snapshot";
      readonly s3Uri: string;
      readonly versionId: string;
      readonly sha256: string;
    };

export interface Ec2Provisioner {
  readonly type: "ec2";
  readonly launchTemplateId: string;
  readonly launchTemplateVersion:
    | { readonly type: "pinned"; readonly version: string }
    | { readonly type: "alias"; readonly name: "$Latest" | "$Default" };
  readonly network: string;
  readonly instanceType?: string;
  readonly instanceProfileArn: string;
  readonly bootstrapRoleArn: string;
  readonly bootstrapProtocol: "v1";
  readonly tenancy?: "default" | "dedicated";
  readonly subnetStrategy?: "round-robin" | "failure-weighted";
}

export interface FargateProvisioner {
  readonly type: "fargate";
  readonly cluster: string;
  readonly network: string;
  readonly taskDefinitionArn: string;
  readonly runnerContainer: string;
  readonly bootstrapRoleArn: string;
  readonly executionRoleArn: string;
  readonly bootstrapProtocol: "v1";
}

export interface RunnerProfile {
  readonly id: string;
  readonly state: "active" | "draining" | "disabled";
  readonly purpose:
    "copilot-cloud-agent" | "ec2-general" | "fargate-compatible";
  readonly platform: {
    readonly os: "linux" | "windows";
    readonly arch: "x64" | "arm64";
  };
  readonly capacity: {
    readonly idleReserve: number;
    readonly maxRunners: number;
    readonly startupTimeoutSeconds: number;
  };
  readonly github: {
    readonly connection: string;
    readonly runnerGroup: string;
    readonly scaleSet: string;
    readonly scaleSetId?: string;
    readonly labels: readonly string[];
    readonly registration: "existing" | "create-if-missing";
  };
  readonly provisioner: Ec2Provisioner | FargateProvisioner;
}

interface DryRunConfig {
  readonly demand?: DryRunDemand;
  readonly inspectAws?: boolean;
}

interface ControllerSpec {
  readonly aws: AwsEnvironment;
  readonly mode?: "active" | "dry-run";
  readonly dryRun?: DryRunConfig;
  readonly controller: {
    readonly image: string;
    readonly hosting: {
      readonly type: "ecs-fargate";
      readonly network: string;
      readonly cluster: string;
      readonly cpuArchitecture: "x64" | "arm64";
      readonly cpu: number;
      readonly memoryMiB: number;
    };
  };
  readonly tracking: {
    readonly table:
      | { readonly type: "create" }
      | { readonly type: "import"; readonly name: string };
  };
  readonly networks: Readonly<Record<string, NetworkConfig>>;
  readonly ecsClusters: Readonly<Record<string, EcsClusterConfig>>;
  readonly github: Readonly<Record<string, GitHubConnectionConfig>>;
  readonly profiles: readonly RunnerProfile[];
}

export interface ControllerConfig {
  readonly apiVersion: "scaleset-aws/v1alpha1";
  readonly kind: "RunnerController";
  readonly metadata: { readonly name: string };
  readonly spec: ControllerSpec;
}

export interface RunnerControllerRuntime extends Omit<
  ControllerConfig,
  "kind" | "spec"
> {
  readonly kind: "RunnerControllerRuntime";
  readonly spec: ControllerSpec & {
    readonly mode: NonNullable<ControllerSpec["mode"]>;
    readonly dryRun: Required<DryRunConfig>;
  };
}
