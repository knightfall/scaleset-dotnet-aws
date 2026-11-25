import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { Ajv2020 } from "ajv/dist/2020.js";
import type { AnySchema, ErrorObject, ValidateFunction } from "ajv";
import { ConfigValidationError } from "./errors.ts";
import { rejectDeferredGitHubConfig, validateSemantics } from "./semantics.ts";
import type { ControllerConfig, RunnerControllerRuntime } from "./types.ts";
import { parseYaml } from "./yaml.ts";

const ajv = new Ajv2020({ strict: true, ownProperties: true });

function readSchema(filename: string): AnySchema {
  const path = new URL("../../config/schema/" + filename, import.meta.url);
  return JSON.parse(readFileSync(path, "utf8")) as AnySchema;
}

ajv.addSchema(readSchema("controller-common-v1alpha1.json"));

const validateConfigShape = ajv.compile<ControllerConfig>(
  readSchema("controller-config-v1alpha1.json"),
);

const validateRuntimeShape = ajv.compile<RunnerControllerRuntime>(
  readSchema("controller-runtime-v1alpha1.json"),
);

function errorPath(issue: ErrorObject): string {
  const property =
    issue.keyword === "required"
      ? issue.params.missingProperty
      : issue.params.additionalProperty;
  if (typeof property !== "string") {
    return issue.instancePath;
  }
  return (
    issue.instancePath +
    "/" +
    property.replaceAll("~", "~0").replaceAll("/", "~1")
  );
}

function requireShape<T>(input: unknown, validate: ValidateFunction<T>): T {
  if (validate(input)) {
    return input;
  }

  const issue = validate.errors?.[0];
  if (!issue) {
    throw new ConfigValidationError("schema", "", "Invalid configuration.");
  }
  throw new ConfigValidationError(
    "schema",
    errorPath(issue),
    issue.message ?? "Invalid configuration.",
  );
}

export function validateRuntime(value: unknown): RunnerControllerRuntime {
  rejectDeferredGitHubConfig(value);
  const runtime = requireShape(value, validateRuntimeShape);
  requireUnicodeText(runtime);
  validateSemantics(runtime);
  return runtime;
}

function requireUnicodeText(value: unknown): void {
  if (typeof value === "string" && !value.isWellFormed()) {
    throw new ConfigValidationError(
      "schema",
      "",
      "Configuration strings must contain valid Unicode text.",
    );
  }
  if (value && typeof value === "object") {
    for (const [key, item] of Object.entries(value)) {
      requireUnicodeText(key);
      requireUnicodeText(item);
    }
  }
}

function formatJson(value: unknown, indent: string): string {
  const nested = indent + "  ";
  if (Array.isArray(value)) {
    if (value.length === 0) {
      return "[]";
    }
    const items = value.map(
      (item: unknown) => nested + formatJson(item, nested),
    );
    return "[\n" + items.join(",\n") + "\n" + indent + "]";
  }
  if (value && typeof value === "object") {
    const record = value as Record<string, unknown>;
    const keys = Object.keys(record).sort();
    if (keys.length === 0) {
      return "{}";
    }
    const items = keys.map(
      (key) =>
        nested + JSON.stringify(key) + ": " + formatJson(record[key], nested),
    );
    return "{\n" + items.join(",\n") + "\n" + indent + "}";
  }

  const json = JSON.stringify(value);
  if (json === undefined) {
    throw new TypeError("Runtime configuration contains a non-JSON value.");
  }
  return json;
}

export function decodeConfiguration(bytes: Uint8Array): string {
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    throw new ConfigValidationError(
      "yaml",
      "",
      "Configuration input must be valid UTF-8.",
    );
  }
}

export function compileConfig(source: string | Uint8Array): {
  runtime: RunnerControllerRuntime;
  json: string;
  sha256: string;
} {
  const parsed = parseYaml(
    typeof source === "string" ? source : decodeConfiguration(source),
  );
  rejectDeferredGitHubConfig(parsed);
  const config = requireShape(parsed, validateConfigShape);
  const runtime = requireShape(
    {
      ...config,
      kind: "RunnerControllerRuntime",
      spec: {
        ...config.spec,
        mode: config.spec.mode ?? "dry-run",
        dryRun: {
          demand: config.spec.dryRun?.demand ?? { type: "none" },
          inspectAws: config.spec.dryRun?.inspectAws ?? false,
        },
      },
    },
    validateRuntimeShape,
  );
  validateSemantics(runtime);

  const json = formatJson(runtime, "") + "\n";
  const sha256 = createHash("sha256").update(json, "utf8").digest("hex");
  return { runtime, json, sha256 };
}
