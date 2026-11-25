import { readFileSync } from "node:fs";
import { isIPv4 } from "node:net";
import { awsPartition } from "../config/semantics.ts";
import type { RunnerControllerRuntime } from "../config/types.ts";
import { parseYaml } from "../config/yaml.ts";

export interface ControllerTelemetrySettings {
  readonly sdkDisabled?: boolean;
  readonly serviceName?: string;
  readonly deploymentEnvironment?: string;
  readonly metricExportIntervalMs?: number;
  readonly sampler?:
    | { readonly type: "parentbased_always_on" }
    | { readonly type: "parentbased_traceidratio"; readonly ratio?: number };
  readonly exporters?: Partial<Record<ExportSignal, TelemetryExporterSettings>>;
}

type AwsScope = RunnerControllerRuntime["spec"]["aws"];
const exportSignals = ["common", "traces", "metrics", "logs"] as const;
type ExportSignal = (typeof exportSignals)[number];

interface TelemetryExporterSettings {
  readonly endpoint?: string;
  readonly protocol?: "grpc" | "http/protobuf";
  readonly timeoutMs?: number;
  readonly headersSecret?: TelemetryHeaderSecret;
}

interface TelemetryHeaderSecret {
  readonly secretArn: string;
  readonly kmsKeyArn?: string;
}

const ratioFormat = new Intl.NumberFormat("en-US", {
  useGrouping: false,
  maximumSignificantDigits: 17,
});

function invalid(field: string): never {
  throw new Error(`Invalid controller telemetry setting ${field}.`);
}

function object(
  value: unknown,
  allowed: readonly string[],
  field: string,
): Record<string, unknown> {
  if (
    typeof value !== "object" ||
    value === null ||
    Array.isArray(value) ||
    Object.keys(value).some((key) => !allowed.includes(key))
  ) {
    return invalid(field);
  }
  return value as Record<string, unknown>;
}

function identity(value: unknown, maximum: number): value is string {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= maximum &&
    !/[^A-Za-z0-9._-]/u.test(value)
  );
}

function integerInRange(
  value: unknown,
  minimum: number,
  maximum: number,
): value is number {
  return (
    typeof value === "number" &&
    Number.isInteger(value) &&
    value >= minimum &&
    value <= maximum
  );
}

function endpoint(value: unknown): value is string {
  if (
    typeof value !== "string" ||
    !value.isWellFormed() ||
    value.length > 2048 ||
    /[\s\p{Cc}\\?#<>"{}|^`]/u.test(value) ||
    /%(?![0-9a-f]{2})/iu.test(value)
  ) {
    return false;
  }
  const authority = /^https?:\/\/([^/]+)/iu.exec(value)?.[1];
  if (!authority || /[@%]/u.test(authority)) return false;
  const path = value.slice(value.indexOf("://") + 3 + authority.length);
  if (
    /[\[\]]/u.test(path) ||
    path.split("/").some((part) => part === "." || part === "..")
  ) {
    return false;
  }
  for (const escape of path.matchAll(/%([0-9a-f]{2})/giu)) {
    const octet = Number.parseInt(escape[1], 16);
    if (
      octet < 0x20 ||
      octet === 0x7f ||
      (octet >= 0x80 && /[a-f]/u.test(escape[1])) ||
      /[A-Za-z0-9._~\\-]/u.test(String.fromCharCode(octet))
    )
      return false;
  }
  const url = URL.parse(value);
  return url !== null && url.hostname.length > 0 && url.port !== "0";
}

function credentialTransportAllowed(endpoint: string): boolean {
  const { protocol, hostname } = new URL(endpoint);
  return (
    protocol === "https:" ||
    hostname === "localhost" ||
    hostname === "[::1]" ||
    (isIPv4(hostname) && hostname.startsWith("127."))
  );
}

function scopedArn(
  value: unknown,
  service: "secretsmanager" | "kms",
  resourcePattern: RegExp,
  aws: AwsScope,
): value is string {
  if (typeof value !== "string" || /\s/u.test(value)) return false;
  const prefix = `arn:${awsPartition(aws.region)}:${service}:${aws.region}:${aws.account}:`;
  return (
    value.startsWith(prefix) && resourcePattern.test(value.slice(prefix.length))
  );
}

export function resolveControllerTelemetry(
  value: unknown,
  aws: AwsScope,
): {
  readonly environment: Readonly<Record<string, string>>;
  readonly headers: readonly (TelemetryHeaderSecret & {
    readonly name: string;
  })[];
} {
  const environment: Record<string, string> = {};
  const headers: (TelemetryHeaderSecret & { name: string })[] = [];
  if (value === undefined) return { environment, headers };

  const {
    sdkDisabled,
    serviceName,
    deploymentEnvironment,
    metricExportIntervalMs,
    sampler,
    exporters,
  } = object(
    value,
    [
      "sdkDisabled",
      "serviceName",
      "deploymentEnvironment",
      "metricExportIntervalMs",
      "sampler",
      "exporters",
    ],
    "telemetry",
  );
  if (sdkDisabled !== undefined) {
    if (typeof sdkDisabled !== "boolean") invalid("sdkDisabled");
    environment.OTEL_SDK_DISABLED = String(sdkDisabled);
  }
  if (serviceName !== undefined) {
    if (!identity(serviceName, 128)) invalid("serviceName");
    environment.OTEL_SERVICE_NAME = serviceName;
  }
  if (deploymentEnvironment !== undefined) {
    if (!identity(deploymentEnvironment, 64)) invalid("deploymentEnvironment");
    environment.OTEL_RESOURCE_ATTRIBUTES = `deployment.environment.name=${deploymentEnvironment}`;
  }
  if (metricExportIntervalMs !== undefined) {
    if (!integerInRange(metricExportIntervalMs, 1000, 3600000))
      invalid("metricExportIntervalMs");
    environment.OTEL_METRIC_EXPORT_INTERVAL = String(metricExportIntervalMs);
  }
  if (sampler !== undefined) {
    const { type, ratio } = object(sampler, ["type", "ratio"], "sampler");
    if (type !== "parentbased_traceidratio" && type !== "parentbased_always_on")
      invalid("sampler.type");
    environment.OTEL_TRACES_SAMPLER = type;
    if (ratio !== undefined) {
      if (
        type !== "parentbased_traceidratio" ||
        typeof ratio !== "number" ||
        !(ratio >= 0 && ratio <= 1)
      )
        invalid("sampler.ratio");
      environment.OTEL_TRACES_SAMPLER_ARG = ratioFormat.format(
        ratio === 0 ? 0 : ratio,
      );
    }
  }
  if (exporters === undefined) return { environment, headers };

  const configured = object(exporters, exportSignals, "exporters");
  for (const signal of exportSignals) {
    if (configured[signal] === undefined) continue;
    const field = `exporters.${signal}`;
    const prefix =
      signal === "common"
        ? "OTEL_EXPORTER_OTLP_"
        : `OTEL_EXPORTER_OTLP_${signal.toUpperCase()}_`;
    const {
      endpoint: url,
      protocol,
      timeoutMs,
      headersSecret,
    } = object(
      configured[signal],
      ["endpoint", "protocol", "timeoutMs", "headersSecret"],
      field,
    );
    if (url !== undefined) {
      if (!endpoint(url)) invalid(`${field}.endpoint`);
      environment[prefix + "ENDPOINT"] = url;
    }
    if (protocol !== undefined) {
      if (protocol !== "grpc" && protocol !== "http/protobuf")
        invalid(`${field}.protocol`);
      environment[prefix + "PROTOCOL"] = protocol;
    }
    if (timeoutMs !== undefined) {
      if (!integerInRange(timeoutMs, 1, 5000)) invalid(`${field}.timeoutMs`);
      environment[prefix + "TIMEOUT"] = String(timeoutMs);
    }
    if (headersSecret !== undefined) {
      const { secretArn, kmsKeyArn } = object(
        headersSecret,
        ["secretArn", "kmsKeyArn"],
        `${field}.headersSecret`,
      );
      if (
        !scopedArn(
          secretArn,
          "secretsmanager",
          /^secret:[A-Za-z0-9/_+=.@-]+-[A-Za-z0-9]{6}$/u,
          aws,
        )
      ) {
        invalid(`${field}.headersSecret.secretArn`);
      }
      if (
        kmsKeyArn !== undefined &&
        !scopedArn(
          kmsKeyArn,
          "kms",
          /^key\/(?:[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}|mrk-[a-f0-9]{32})$/u,
          aws,
        )
      ) {
        invalid(`${field}.headersSecret.kmsKeyArn`);
      }
      headers.push({ name: prefix + "HEADERS", secretArn, kmsKeyArn });
    }
  }
  const commonEndpoint = environment.OTEL_EXPORTER_OTLP_ENDPOINT;
  const commonHasHeaders = headers.some(
    (header) => header.name === "OTEL_EXPORTER_OTLP_HEADERS",
  );
  for (const signal of exportSignals.slice(1)) {
    const prefix = `OTEL_EXPORTER_OTLP_${signal.toUpperCase()}_`;
    const signalEndpoint = environment[prefix + "ENDPOINT"];
    const effectiveEndpoint = signalEndpoint ?? commonEndpoint;
    const hasHeaders =
      commonHasHeaders ||
      headers.some((header) => header.name === prefix + "HEADERS");
    if (
      effectiveEndpoint &&
      hasHeaders &&
      !credentialTransportAllowed(effectiveEndpoint)
    ) {
      invalid(
        `exporters.${signalEndpoint === undefined ? "common" : signal}.endpoint`,
      );
    }
  }
  return { environment, headers };
}

export function readControllerTelemetry(
  path: string | undefined,
  aws: AwsScope,
): ControllerTelemetrySettings | undefined {
  if (path === undefined) return undefined;
  let value: unknown;
  try {
    const source = readFileSync(path, "utf8");
    value = JSON.parse(source);
    parseYaml(source); // Also reject duplicate JSON members.
  } catch {
    return invalid("document");
  }
  resolveControllerTelemetry(value, aws);
  return value as ControllerTelemetrySettings;
}
