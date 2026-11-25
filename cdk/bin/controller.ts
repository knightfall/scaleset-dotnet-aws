import { readFileSync } from "node:fs";
import { compileConfig } from "../lib/config/compiler.ts";
import { synthesizeController } from "../lib/infrastructure/controller-stack.ts";
import { readControllerTelemetry } from "../lib/infrastructure/controller-telemetry.ts";
import { deploymentArguments } from "../lib/infrastructure/deployment-input.ts";
import { fargatePreflightEnvironmentName } from "../lib/infrastructure/fargate-preflight.ts";
import { readImportedEncryption } from "../lib/infrastructure/imported-encryption.ts";

try {
  const { config, generation, telemetry, encryption } = deploymentArguments(
    process.argv.slice(2),
  );
  const compiled = compileConfig(readFileSync(config, "utf8"));
  synthesizeController(compiled, generation, undefined, {
    fargatePreflight: process.env[fargatePreflightEnvironmentName],
    telemetry: readControllerTelemetry(telemetry, compiled.runtime.spec.aws),
    encryption: readImportedEncryption(encryption),
  });
} catch (error) {
  process.stderr.write(
    `${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exitCode = 1;
}
