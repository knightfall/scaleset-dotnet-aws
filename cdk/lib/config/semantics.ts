import { ConfigValidationError } from "./errors.ts";
import { foldGitHubIdentity } from "./github-case.ts";
import type {
  AwsEnvironment,
  RunnerControllerRuntime,
  RunnerProfile,
} from "./types.ts";

const maximumScaleSetId = 9223372036854775807n;
const hostedGitHub = /^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+ghe\.com$/;
const inlineCredentialKeys = new Set([
  "token",
  "personalAccessToken",
  "pat",
  "accessToken",
  "privateKey",
  "clientId",
  "installationId",
]);

export function rejectDeferredGitHubConfig(value: unknown): void {
  const config = record(value);
  const spec = record(config?.spec);
  rejectAuthFields(config, "");
  rejectAuthFields(spec, "/spec");

  for (const [name, value] of Object.entries(record(spec?.github) ?? {})) {
    const path = childPath("/spec/github", name);
    const connection = record(value);
    rejectAuthFields(connection, path, true);
    rejectAuthFields(record(connection?.auth), childPath(path, "auth"), true);
    if (typeof connection?.scopeURL === "string") {
      parseGitHubScope(connection.scopeURL, childPath(path, "scopeURL"));
    }
  }

  const profiles = spec?.profiles;
  if (Array.isArray(profiles)) {
    for (const [index, value] of profiles.entries()) {
      rejectAuthFields(
        record(record(value)?.github),
        `/spec/profiles/${index}/github`,
        true,
      );
    }
  }
}

export function validateSemantics(config: RunnerControllerRuntime): void {
  const spec = config.spec;
  const hosting = spec.controller.hosting;
  requireReference(
    spec.networks,
    hosting.network,
    "/spec/controller/hosting/network",
  );
  requireReference(
    spec.ecsClusters,
    hosting.cluster,
    "/spec/controller/hosting/cluster",
  );
  validateAwsResources(spec);

  const scopes = new Map<string, string>();
  for (const [name, connection] of Object.entries(spec.github)) {
    const path = childPath("/spec/github", name);
    const scope = parseGitHubScope(
      connection.scopeURL,
      childPath(path, "scopeURL"),
    );
    if (scope.enterprise !== (connection.auth.type === "enterprise-pat")) {
      fail(
        `${path}/auth/type`,
        "Use enterprise-pat for enterprise targets and github-app for organizations and repositories.",
      );
    }
    scopes.set(name, scope.identity);
  }

  if (!spec.profiles.some((profile) => profile.state !== "disabled")) {
    fail("/spec/profiles", "At least one profile must be active or draining.");
  }

  const profileIds = new Set<string>();
  const labelsByScope = new Map<string, Set<string>>();
  const copilotGroups = new Map<string, boolean>();
  const profileBindings = new Set<string>();
  const pinnedSets = new Set<string>();

  for (const [index, profile] of spec.profiles.entries()) {
    const path = `/spec/profiles/${index}`;
    requireUnique(
      profileIds,
      profile.id,
      `${path}/id`,
      "Profile IDs must be unique.",
    );
    validateProfile(profile, spec, path);

    const routing = profile.github;
    const scope = scopes.get(routing.connection);
    if (scope === undefined) {
      fail(
        `${path}/github/connection`,
        "The named GitHub connection does not exist.",
      );
    }
    const groupName = foldGitHubIdentity(routing.runnerGroup);
    const scaleSetName = foldGitHubIdentity(routing.scaleSet);
    const group = JSON.stringify([scope, groupName]);
    const isCopilotProfile = profile.purpose === "copilot-cloud-agent";
    const isCopilotGroup = copilotGroups.get(group);
    if (isCopilotGroup !== undefined && isCopilotGroup !== isCopilotProfile) {
      fail(
        `${path}/github/runnerGroup`,
        "Copilot cloud agent profiles require a dedicated runner group.",
      );
    }
    copilotGroups.set(group, isCopilotProfile);
    const labels = routing.labels.map(foldGitHubIdentity);
    if (!labels.includes(scaleSetName)) {
      fail(
        `${path}/github/labels`,
        "Include the scale-set name in its managed labels.",
      );
    }
    let usedLabels = labelsByScope.get(scope);
    if (usedLabels === undefined) {
      usedLabels = new Set<string>();
      labelsByScope.set(scope, usedLabels);
    }
    for (const label of labels) {
      requireUnique(
        usedLabels,
        label,
        `${path}/github/labels`,
        "Managed labels must be disjoint within a GitHub scope.",
      );
    }

    const scaleSetId = routing.scaleSetId;
    if (scaleSetId !== undefined) {
      if (BigInt(scaleSetId) > maximumScaleSetId) {
        fail(
          `${path}/github/scaleSetId`,
          "Scale-set IDs must fit in a signed 64-bit integer.",
        );
      }
      if (routing.registration === "create-if-missing") {
        fail(
          `${path}/github/registration`,
          "A pinned scale-set ID requires existing registration.",
        );
      }
    }

    if (profile.state !== "disabled") {
      const binding = JSON.stringify([scope, groupName, scaleSetName]);
      requireUnique(
        profileBindings,
        binding,
        `${path}/github/scaleSet`,
        "Active and draining profiles must have distinct scale-set bindings.",
      );
      if (scaleSetId !== undefined) {
        requireUnique(
          pinnedSets,
          JSON.stringify([scope, scaleSetId]),
          `${path}/github/scaleSetId`,
          "Active and draining profiles must have distinct pinned scale-set IDs within a scope.",
        );
      }
    }
  }
}

function validateProfile(
  profile: RunnerProfile,
  spec: RunnerControllerRuntime["spec"],
  path: string,
): void {
  const { capacity, platform, provisioner, purpose } = profile;
  if (capacity.idleReserve > capacity.maxRunners) {
    fail(
      `${path}/capacity/idleReserve`,
      "Idle reserve cannot exceed maximum capacity.",
    );
  }

  if ((purpose === "fargate-compatible") !== (provisioner.type === "fargate")) {
    fail(
      `${path}/purpose`,
      "Choose a purpose compatible with the runner provisioner.",
    );
  }
  if (
    (provisioner.type === "fargate" && platform.os !== "linux") ||
    (platform.os === "windows" && platform.arch !== "x64") ||
    (purpose === "copilot-cloud-agent" &&
      (platform.os !== "linux" || platform.arch !== "x64"))
  ) {
    fail(
      `${path}/platform`,
      "This runner purpose and provisioner require a supported OS and architecture.",
    );
  }

  const prefix = `${path}/provisioner`;
  requireReference(spec.networks, provisioner.network, `${prefix}/network`);
  requireArn(
    provisioner.bootstrapRoleArn,
    "iam",
    spec.aws,
    `${prefix}/bootstrapRoleArn`,
  );
  if (provisioner.type === "ec2") {
    requireArn(
      provisioner.instanceProfileArn,
      "iam",
      spec.aws,
      `${prefix}/instanceProfileArn`,
    );
    if (provisioner.statusQualification !== undefined) {
      if (
        platform.os !== "linux" ||
        provisioner.launchTemplateVersion.type !== "pinned" ||
        BigInt(provisioner.launchTemplateVersion.version) > maximumScaleSetId
      ) {
        fail(
          `${prefix}/statusQualification`,
          "Supervisor STATUS requires a Linux profile and a numeric pinned Launch Template version.",
        );
      }
      if (
        spec.profiles.some(
          (other) =>
            other !== profile &&
            (other.provisioner.bootstrapRoleArn ===
              provisioner.bootstrapRoleArn ||
              (other.provisioner.type === "fargate" &&
                other.provisioner.executionRoleArn ===
                  provisioner.bootstrapRoleArn)),
        )
      ) {
        fail(
          `${prefix}/bootstrapRoleArn`,
          "Supervisor STATUS requires a dedicated EC2 bootstrap role.",
        );
      }
    }
  } else {
    requireReference(
      spec.ecsClusters,
      provisioner.cluster,
      `${prefix}/cluster`,
    );
    requireArn(
      provisioner.taskDefinitionArn,
      "ecs",
      spec.aws,
      `${prefix}/taskDefinitionArn`,
    );
    requireArn(
      provisioner.executionRoleArn,
      "iam",
      spec.aws,
      `${prefix}/executionRoleArn`,
    );
  }
}

function validateAwsResources(spec: RunnerControllerRuntime["spec"]): void {
  const { account, region } = spec.aws;
  const registry = spec.controller.image.split("/", 1)[0];
  const domain = region.startsWith("cn-")
    ? "amazonaws.com.cn"
    : "amazonaws.com";
  if (registry !== `${account}.dkr.ecr.${region}.${domain}`) {
    fail(
      "/spec/controller/image",
      "The ECR registry must match the deployment account and Region.",
    );
  }

  for (const [name, cluster] of Object.entries(spec.ecsClusters)) {
    if (cluster.type === "import") {
      requireArn(
        cluster.arn,
        "ecs",
        spec.aws,
        `${childPath("/spec/ecsClusters", name)}/arn`,
      );
    }
  }
  for (const [name, connection] of Object.entries(spec.github)) {
    requireArn(
      connection.auth.secretArn,
      "secretsmanager",
      spec.aws,
      `${childPath("/spec/github", name)}/auth/secretArn`,
    );
  }
  for (const [name, network] of Object.entries(spec.networks)) {
    const networkPath = childPath("/spec/networks", name);
    const subnetIds = new Set<string>();
    for (const [index, subnet] of network.subnets.entries()) {
      const path = `${networkPath}/subnets/${index}`;
      requireUnique(
        subnetIds,
        subnet.id,
        `${path}/id`,
        "Subnet IDs must be unique within a network.",
      );
      const zoneSuffix = subnet.az.slice(region.length);
      if (
        !subnet.az.startsWith(region) ||
        !/^(?:[a-z]|-[a-z0-9-]+[0-9][a-z])$/.test(zoneSuffix)
      ) {
        fail(
          `${path}/az`,
          "The subnet Availability Zone must belong to the deployment Region.",
        );
      }
    }
  }
}

function requireArn(
  arn: string,
  service: "ecs" | "iam" | "secretsmanager",
  aws: AwsEnvironment,
  path: string,
): void {
  const [, partition, actualService, region, account] = arn.split(":");
  if (
    partition !== awsPartition(aws.region) ||
    actualService !== service ||
    account !== aws.account ||
    region !== (service === "iam" ? "" : aws.region)
  ) {
    fail(
      path,
      "The ARN must match the deployment partition, account, and applicable Region.",
    );
  }
}

export function awsPartition(region: string): string {
  if (region.startsWith("cn-")) return "aws-cn";
  if (region.startsWith("us-gov-")) return "aws-us-gov";
  if (region.startsWith("us-isob-")) return "aws-iso-b";
  if (region.startsWith("us-isof-")) return "aws-iso-f";
  if (region.startsWith("us-iso-")) return "aws-iso";
  if (region.startsWith("eu-isoe-")) return "aws-iso-e";
  if (region.startsWith("eusc-")) return "aws-eusc";
  return "aws";
}

function parseGitHubScope(
  value: string,
  path: string,
): { identity: string; enterprise: boolean } {
  const raw = /^https:\/\/([^/]+)(\/.*)?$/i.exec(value);
  if (
    raw === null ||
    !/^[a-z0-9.-]+(?::[0-9]+)?$/i.test(raw[1]) ||
    /[\s\\?#]/.test(value)
  ) {
    fail(
      path,
      "Use an HTTPS GitHub scope URL without credentials, query, or fragment.",
    );
  }
  let target: URL;
  try {
    target = new URL(value);
  } catch {
    fail(path, "Use a valid absolute GitHub scope URL.");
  }
  if (target.username || target.password || target.port) {
    fail(path, "Use the standard HTTPS port with no URL credentials.");
  }
  const host =
    target.hostname === "www.github.com" ? "github.com" : target.hostname;
  if (host !== "github.com" && !hostedGitHub.test(host)) {
    throw new ConfigValidationError(
      "ghes-unsupported",
      path,
      "Custom GitHub Enterprise Server hosts are deferred in scaleset.NET #20.",
    );
  }
  const rawSegments = (raw[2] ?? "").replace(/^\/+|\/+$/g, "").split("/");
  if (rawSegments.length > 2) {
    fail(path, "Identify a GitHub organization, repository, or enterprise.");
  }
  const segments = rawSegments.map((part) =>
    foldGitHubIdentity(decodeScopeSegment(part, path)),
  );
  const enterprise = segments.length === 2 && segments[0] === "enterprises";
  return { identity: JSON.stringify([host, ...segments]), enterprise };
}

function decodeScopeSegment(value: string, path: string): string {
  let segment: string;
  try {
    segment = decodeURIComponent(value);
  } catch {
    fail(path, "Use valid URL encoding in GitHub scope path segments.");
  }
  if (
    segment.length === 0 ||
    segment === "." ||
    segment === ".." ||
    segment.includes("/") ||
    segment.includes("\\") ||
    [...segment].some((character) => {
      const code = character.codePointAt(0)!;
      return code < 32 || (code >= 127 && code <= 159);
    })
  ) {
    fail(
      path,
      "GitHub scope path segments must be non-empty and contain no separators or control characters.",
    );
  }
  return segment;
}

function rejectAuthFields(
  value: Record<string, unknown> | undefined,
  path: string,
  rejectInline = false,
): void {
  for (const key of Object.keys(value ?? {})) {
    if (
      key === "caBundlePath" ||
      key === "clientCertificatePath" ||
      key === "clientCertificate"
    ) {
      throw new ConfigValidationError(
        "custom-ca-unsupported",
        childPath(path, key),
        "Custom root CA and mutual TLS settings are deferred in scaleset.NET #21.",
      );
    }
    if (rejectInline && inlineCredentialKeys.has(key)) {
      throw new ConfigValidationError(
        "inline-credentials-unsupported",
        childPath(path, key),
        "Reference a Secrets Manager ARN for GitHub authentication.",
      );
    }
  }
}

function requireReference(
  values: Readonly<Record<string, unknown>>,
  name: string,
  path: string,
): void {
  if (!Object.hasOwn(values, name)) {
    fail(path, "The named configuration entry does not exist.");
  }
}

function requireUnique(
  values: Set<string>,
  value: string,
  path: string,
  message: string,
): void {
  if (values.has(value)) {
    fail(path, message);
  }
  values.add(value);
}

function record(value: unknown): Record<string, unknown> | undefined {
  if (typeof value === "object" && value !== null && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return undefined;
}

function childPath(path: string, name: string): string {
  return `${path}/${name.replaceAll("~", "~0").replaceAll("/", "~1")}`;
}

function fail(path: string, message: string): never {
  throw new ConfigValidationError("semantic", path, message);
}
