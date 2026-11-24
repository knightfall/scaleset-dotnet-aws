import { parseArgs } from "node:util";
import type { RunnerControllerRuntime } from "../config/types.ts";

const maximumGeneration = 9223372036854775807n;

export function generationValue(value: string): bigint {
  if (!/^[1-9][0-9]{0,18}$/u.test(value)) {
    throw new Error("Deployment generation must be a positive Int64 decimal.");
  }
  const generation = BigInt(value);
  if (generation > maximumGeneration) {
    throw new Error("Deployment generation must be a positive Int64 decimal.");
  }
  return generation;
}

export function deploymentArguments(argv: string[]) {
  const { values } = parseArgs({
    args: argv,
    options: {
      config: { type: "string" },
      generation: { type: "string" },
    },
  });
  if (!values.config || !values.generation) {
    throw new Error(
      "Required arguments are --config <controller.yaml> --generation <positive integer>.",
    );
  }
  generationValue(values.generation);
  return { config: values.config, generation: values.generation };
}

export function trackingTableName(runtime: RunnerControllerRuntime): string {
  const table = runtime.spec.tracking.table;
  return table.type === "import"
    ? table.name
    : `${runtime.metadata.name}-runner-lifecycle`;
}

export function controllerStackName(runtime: RunnerControllerRuntime): string {
  return `${runtime.metadata.name}-runner-controller`;
}

export function deploymentIdentity(runtime: RunnerControllerRuntime): string {
  return `${runtime.spec.aws.account}:${runtime.spec.aws.region}:${runtime.metadata.name}`;
}
