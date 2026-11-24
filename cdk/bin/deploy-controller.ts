import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { compileConfig } from "../lib/config/compiler.ts";
import { readControllerTelemetry } from "../lib/infrastructure/controller-telemetry.ts";
import { deployController } from "../lib/infrastructure/deploy-controller.ts";
import { deploymentArguments } from "../lib/infrastructure/deployment-input.ts";
import { fargatePreflightEnvironmentName } from "../lib/infrastructure/fargate-preflight.ts";

try {
  const { config, generation, telemetry } = deploymentArguments(
    process.argv.slice(2),
  );
  const compiled = compileConfig(readFileSync(config, "utf8"));
  const env = Object.fromEntries(
    Object.entries(process.env).filter(
      ([name]) =>
        !name.startsWith("AWS_ENDPOINT_URL") &&
        !name.startsWith("OTEL_") &&
        name !== fargatePreflightEnvironmentName,
    ),
  );
  env.AWS_IGNORE_CONFIGURED_ENDPOINT_URLS = "true";
  env.AWS_CLI_AUTO_PROMPT = "off";
  deployController(
    {
      compiled,
      generation,
      fargatePreflight: process.env[fargatePreflightEnvironmentName],
      telemetry: readControllerTelemetry(telemetry, compiled.runtime.spec.aws),
    },
    (program, args) => {
      const result = spawnSync(program, args, {
        encoding: "utf8",
        env,
        maxBuffer: 4 * 1024 * 1024,
      });
      if (result.error) throw result.error;
      return {
        status: result.status ?? 1,
        stdout: result.stdout,
        stderr: result.stderr,
      };
    },
  );
} catch (error) {
  process.stderr.write(
    `${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exitCode = 1;
}
