import { Token } from "aws-cdk-lib";
import { parseDocument } from "yaml";
import type { RunnerControllerRuntime } from "../config/types.ts";

export const fargatePreflightEnvironmentName = "SCALESET_FARGATE_PREFLIGHT";
const maximumBytes = 16 * 1024;
const maximumCpuUnits = 9_223_372_036_854_775_807n;

export interface ControllerDeploymentEnvironment {
  readonly fargatePreflight?: string;
}

function invalid(): never {
  throw new Error(
    "SCALESET_FARGATE_PREFLIGHT is invalid for the configured deployment.",
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function containsOnly(
  record: Record<string, unknown>,
  allowed: readonly string[],
): boolean {
  return Object.keys(record).every((key) => allowed.includes(key));
}

function strings(value: unknown): value is string[] {
  return (
    Array.isArray(value) &&
    value.every(
      (entry: unknown) =>
        typeof entry === "string" &&
        entry.isWellFormed() &&
        [...entry].every(
          (character) => character >= " " && character !== "\u007f",
        ),
    )
  );
}

function validApproval(value: unknown): boolean {
  return (
    isRecord(value) &&
    containsOnly(value, [
      "image",
      "entryPoint",
      "command",
      "scratchMountPaths",
    ]) &&
    typeof value.image === "string" &&
    !/\s/u.test(value.image) &&
    /^[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[a-f0-9]{64}$/u.test(value.image) &&
    strings(value.entryPoint) &&
    strings(value.command) &&
    (value.scratchMountPaths === undefined || strings(value.scratchMountPaths))
  );
}

function utcTicks(value: unknown): bigint | undefined {
  if (typeof value !== "string") return undefined;
  // Use explicit offsets and the same 100ns precision as DateTimeOffset.
  const parts =
    /^(\d{4})-(\d\d)-(\d\d)T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.(\d{1,7}))?(Z|[+-](?:0\d|1[0-3]):[0-5]\d|[+-]14:00)$/u.exec(
      value,
    );
  if (!parts || parts[0] !== value) return undefined;
  const [, yearText, monthText, dayText, fraction = "", offset] = parts;
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const monthDays = [
    31,
    leap ? 29 : 28,
    31,
    30,
    31,
    30,
    31,
    31,
    30,
    31,
    30,
    31,
  ];
  if (
    year < 1 ||
    month < 1 ||
    month > 12 ||
    day < 1 ||
    day > monthDays[month - 1]
  )
    return undefined;
  const milliseconds = Date.parse(value.slice(0, 19) + offset);
  if (
    !Number.isFinite(milliseconds) ||
    milliseconds < -62_135_596_800_000 ||
    milliseconds > 253_402_300_799_999
  )
    return undefined;
  return BigInt(milliseconds) * 10_000n + BigInt(fraction.padEnd(7, "0"));
}

function validQuota(
  value: unknown,
  aws: RunnerControllerRuntime["spec"]["aws"],
): boolean {
  if (
    !isRecord(value) ||
    !containsOnly(value, [
      "account",
      "region",
      "observedAt",
      "expiresAt",
      "remainingCpuUnits",
      "reservedCpuUnits",
    ]) ||
    value.account !== aws.account ||
    value.region !== aws.region ||
    typeof value.remainingCpuUnits !== "bigint" ||
    typeof value.reservedCpuUnits !== "bigint"
  ) {
    return false;
  }

  const observed = utcTicks(value.observedAt);
  const expires = utcTicks(value.expiresAt);
  return (
    observed !== undefined &&
    observed !== -621_355_968_000_000_000n &&
    expires !== undefined &&
    expires > observed
  );
}

function readJson(input: string): unknown {
  // JSON supplies the syntax and exact source for integer tokens. The YAML
  // AST adds duplicate-member rejection, which JSON.parse omits.
  const value: unknown = JSON.parse(
    input,
    (key: string, entry: unknown, context?: { source?: string }): unknown => {
      if (key !== "remainingCpuUnits" && key !== "reservedCpuUnits")
        return entry;
      const source = context?.source;
      if (
        typeof entry !== "number" ||
        source === undefined ||
        !/^-?(?:0|[1-9][0-9]{0,18})$/u.test(source)
      ) {
        return invalid();
      }
      const units = BigInt(source);
      return units >= 0n && units <= maximumCpuUnits ? units : invalid();
    },
  );
  const document = parseDocument(input, {
    schema: "json",
    prettyErrors: false,
    strict: true,
    uniqueKeys: true,
  });
  if (document.errors.length > 0 || document.warnings.length > 0)
    return invalid();
  return value;
}

export function validateFargatePreflight(
  runtime: RunnerControllerRuntime,
  input?: string,
): string | undefined {
  if (input === undefined) return undefined;
  const { spec } = runtime;
  const enabled = new Set(
    spec.profiles
      .filter(
        (profile) =>
          profile.state !== "disabled" &&
          profile.provisioner.type === "fargate",
      )
      .map((profile) => profile.id),
  );
  if (
    enabled.size === 0 ||
    (spec.mode !== "active" && !spec.dryRun.inspectAws) ||
    Buffer.byteLength(input, "utf8") > maximumBytes ||
    !input.isWellFormed() ||
    Token.isUnresolved(input) ||
    input.includes("{{resolve:")
  )
    return invalid();

  let value: unknown;
  try {
    value = readJson(input);
  } catch {
    return invalid();
  }
  if (
    !isRecord(value) ||
    !containsOnly(value, ["profiles", "quota"]) ||
    !isRecord(value.profiles) ||
    Object.entries(value.profiles).some(
      ([profile, approval]) =>
        !enabled.has(profile) || !validApproval(approval),
    ) ||
    (value.quota != null && !validQuota(value.quota, spec.aws))
  )
    return invalid();
  return input;
}
