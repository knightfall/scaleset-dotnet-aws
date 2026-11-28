import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { compileConfig } from "../lib/config/compiler.ts";
import { readControllerTelemetry } from "../lib/infrastructure/controller-telemetry.ts";
import { deployController } from "../lib/infrastructure/deploy-controller.ts";
import { deploymentArguments } from "../lib/infrastructure/deployment-input.ts";
import { fargatePreflightEnvironmentName } from "../lib/infrastructure/fargate-preflight.ts";
import { readImportedEncryption } from "../lib/infrastructure/imported-encryption.ts";

try {
  const { config, generation, telemetry, encryption, resume, inspectReview } =
    deploymentArguments(process.argv.slice(2), true);
  const compiled = compileConfig(readFileSync(config));
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
  const reviewStack = deployController(
    {
      compiled,
      generation,
      resume,
      inspectReview,
      fargatePreflight: process.env[fargatePreflightEnvironmentName],
      telemetry: readControllerTelemetry(telemetry, compiled.runtime.spec.aws),
      encryption: readImportedEncryption(encryption),
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
  if (reviewStack !== undefined) process.stdout.write(`${reviewStack}\n`);
} catch (error) {
  process.stderr.write(
    `${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exitCode = 1;
}
