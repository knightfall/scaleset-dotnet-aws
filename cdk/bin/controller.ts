import { readFileSync } from "node:fs";
import { compileConfig } from "../lib/config/compiler.ts";
import { synthesizeController } from "../lib/infrastructure/controller-stack.ts";
import { deploymentArguments } from "../lib/infrastructure/deployment-input.ts";
import { fargatePreflightEnvironmentName } from "../lib/infrastructure/fargate-preflight.ts";

try {
  const { config, generation } = deploymentArguments(process.argv.slice(2));
  synthesizeController(
    compileConfig(readFileSync(config, "utf8")),
    generation,
    undefined,
    {
      fargatePreflight: process.env[fargatePreflightEnvironmentName],
    },
  );
} catch (error) {
  process.stderr.write(
    `${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exitCode = 1;
}
