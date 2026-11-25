import { createHash, randomUUID } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { compileConfig, validateRuntime } from "../config/compiler.ts";
import { synthesizeController } from "./controller-stack.ts";
import { resolveControllerTelemetry } from "./controller-telemetry.ts";
import {
  controllerStackName,
  deploymentIdentity,
  generationValue,
  trackingTableName,
} from "./deployment-input.ts";
import type { ControllerDeploymentEnvironment } from "./deployment-input.ts";
import { validateFargatePreflight } from "./fargate-preflight.ts";
import { resolveImportedEncryption } from "./imported-encryption.ts";
import {
  runtimeCopySource,
  verifyImportedEncryption,
} from "./imported-encryption-preflight.ts";

type Compiled = ReturnType<typeof compileConfig>;
type ObjectProperties = Record<string, unknown>;

export interface CommandResult {
  readonly status: number;
  readonly stdout: string;
  readonly stderr: string;
}

type RunCommand = (program: string, args: string[]) => CommandResult;

interface DeploymentTarget extends ControllerDeploymentEnvironment {
  readonly compiled: Compiled;
  readonly generation: string;
}

const cdkCli = fileURLToPath(
  new URL("../../node_modules/aws-cdk/bin/cdk", import.meta.url),
);
const controllerProject = fileURLToPath(
  new URL(
    "../../../src/Knightfall.ScaleSet.Aws.Controller/Knightfall.ScaleSet.Aws.Controller.csproj",
    import.meta.url,
  ),
);
const stableStackStatuses = new Set([
  "CREATE_COMPLETE",
  "UPDATE_COMPLETE",
  "UPDATE_ROLLBACK_COMPLETE",
  "IMPORT_COMPLETE",
  "IMPORT_ROLLBACK_COMPLETE",
]);

function object(value: unknown): ObjectProperties {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("Deployment received a malformed AWS response.");
  }
  return value as ObjectProperties;
}

function field(source: ObjectProperties, key: string): string {
  const value = source[key];
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`Deployment response is missing ${key}.`);
  }
  return value;
}

function rows(value: unknown): ObjectProperties[] {
  if (!Array.isArray(value))
    throw new Error("Deployment received a malformed AWS list.");
  return value.map(object);
}

function commandResult(result: CommandResult, description: string): string {
  if (result.status !== 0) {
    throw new Error(`${description} failed.`);
  }
  return result.stdout;
}

function stackScope(
  stackId: string,
  account: string,
  region: string,
  name: string,
) {
  const parts = stackId.split(":");
  return (
    parts.length === 6 &&
    parts[0] === "arn" &&
    /^aws(?:-[a-z0-9-]+)?$/u.test(parts[1]) &&
    parts[2] === "cloudformation" &&
    parts[3] === region &&
    parts[4] === account &&
    parts[5].startsWith(`stack/${name}/`) &&
    parts[5].length > name.length + 7
  );
}

export function deployController(
  target: DeploymentTarget,
  run: RunCommand,
): void {
  const { compiled, generation } = target;
  const runtime = compiled.runtime;
  const { account, region } = runtime.spec.aws;
  const name = controllerStackName(runtime);
  const identity = deploymentIdentity(runtime);
  const table = trackingTableName(runtime);
  generationValue(generation);
  const fargatePreflight = validateFargatePreflight(
    runtime,
    target.fargatePreflight,
  );
  const encryption = resolveImportedEncryption(
    target.encryption,
    runtime,
    Boolean(fargatePreflight?.quotaSource),
  );
  resolveControllerTelemetry(target.telemetry, runtime.spec.aws);
  const aws = (...args: string[]) =>
    run("aws", [
      "--no-cli-pager",
      "--region",
      region,
      "--output",
      "json",
      ...args,
    ]);
  function awsObject(description: string, ...args: string[]) {
    return object(JSON.parse(commandResult(aws(...args), description)));
  }

  if (
    awsObject("AWS caller verification", "sts", "get-caller-identity")
      .Account !== account
  ) {
    throw new Error(
      "The AWS caller account differs from the compiled deployment.",
    );
  }

  const workspace = mkdtempSync(join(tmpdir(), "scaleset-deployment-"));
  try {
    const candidateFile = join(workspace, "candidate.json");
    writeFileSync(candidateFile, compiled.json, {
      encoding: "utf8",
      mode: 0o600,
    });
    const prior = aws(
      "cloudformation",
      "describe-stacks",
      "--stack-name",
      name,
    );
    let currentStackId: string | undefined;
    let checkRetirement: (() => void) | undefined;
    if (prior.status === 0) {
      const stacks = rows(object(JSON.parse(prior.stdout)).Stacks);
      if (
        stacks.length !== 1 ||
        stacks[0].StackName !== name ||
        !stableStackStatuses.has(field(stacks[0], "StackStatus"))
      ) {
        throw new Error("The current stack is absent, unstable, or ambiguous.");
      }
      currentStackId = field(stacks[0], "StackId");
      if (!stackScope(currentStackId, account, region, name)) {
        throw new Error(
          "The current stack identity differs from the compiled deployment.",
        );
      }
      const outputs = new Map<string, string>();
      for (const output of rows(stacks[0].Outputs)) {
        const key = field(output, "OutputKey");
        if (outputs.has(key))
          throw new Error("The current stack has duplicate output keys.");
        outputs.set(key, field(output, "OutputValue"));
      }
      const oldVersion = outputs.get("RuntimeConfigVersionId");
      const oldSha = outputs.get("RuntimeConfigSha256");
      const oldUri = outputs.get("RuntimeConfigUri");
      const oldGeneration = outputs.get("DeploymentGeneration");
      if (
        outputs.has("TrackingTableKmsKeyArn") &&
        outputs.get("TrackingTableKmsKeyArn") !== encryption.trackingTableKeyArn
      ) {
        throw new Error(
          "The existing tracking table key binding changed. Keep the prior key ARN and use a reviewed key migration.",
        );
      }
      const source = oldUri?.match(
        /^s3:\/\/([a-z0-9.-]+)\/(runtime\/[0-9a-f]{64}\.json)$/u,
      );
      if (
        outputs.get("DeploymentIdentity") !== identity ||
        !oldVersion ||
        oldVersion === "null" ||
        !oldSha ||
        !source ||
        source[2] !== `runtime/${oldSha}.json` ||
        !oldGeneration ||
        generationValue(generation) <= generationValue(oldGeneration)
      ) {
        throw new Error(
          "The current configuration identity or deployment generation could not be verified.",
        );
      }
      const currentFile = join(workspace, "current.json");
      const copy = awsObject(
        "Current runtime read",
        "s3api",
        "get-object",
        "--bucket",
        source[1],
        "--key",
        source[2],
        "--version-id",
        oldVersion,
        "--expected-bucket-owner",
        account,
        currentFile,
      );
      if (copy.VersionId !== oldVersion) {
        throw new Error("S3 returned a different runtime object version.");
      }
      const currentBytes = readFileSync(currentFile);
      if (createHash("sha256").update(currentBytes).digest("hex") !== oldSha) {
        throw new Error(
          "The current runtime artifact digest differs from its stack output.",
        );
      }
      const current = validateRuntime(
        JSON.parse(currentBytes.toString("utf8")),
      );
      if (
        deploymentIdentity(current) !== identity ||
        trackingTableName(current) !== table ||
        outputs.get("TrackingTableName") !== table ||
        current.spec.tracking.table.type !== runtime.spec.tracking.table.type
      ) {
        throw new Error(
          "The current and candidate deployment tracking tables differ.",
        );
      }
      checkRetirement = () => {
        commandResult(
          run("dotnet", [
            "run",
            "--project",
            controllerProject,
            "--configuration",
            "Release",
            "--no-build",
            "--no-restore",
            "--no-launch-profile",
            "--",
            "preflight",
            "retire",
            "--current-config",
            currentFile,
            "--current-sha256",
            oldSha,
            "--current-generation",
            oldGeneration,
            "--config",
            candidateFile,
            "--sha256",
            compiled.sha256,
            "--generation",
            generation,
          ]),
          "Runtime retirement preflight",
        );
      };
      checkRetirement();
    } else {
      const stackAbsent = `(ValidationError) when calling the DescribeStacks operation: Stack with id ${name} does not exist`;
      if (!prior.stderr.trim().endsWith(stackAbsent)) {
        throw new Error(
          "Could not establish whether the deployment stack already exists.",
        );
      }
      const described = aws(
        "dynamodb",
        "describe-table",
        "--table-name",
        table,
      );
      if (runtime.spec.tracking.table.type === "create") {
        if (
          described.status === 0 ||
          !described.stderr.includes(
            "(ResourceNotFoundException) when calling the DescribeTable operation:",
          )
        ) {
          throw new Error(
            "The new stack's tracking table is present or could not be verified absent.",
          );
        }
      } else {
        const existing = object(
          object(
            JSON.parse(
              commandResult(described, "Imported tracking table verification"),
            ),
          ).Table,
        );
        const arn = field(existing, "TableArn");
        if (
          existing.TableName !== table ||
          existing.TableStatus !== "ACTIVE" ||
          !/^arn:aws(?:-[a-z0-9-]+)?:dynamodb:/u.test(arn) ||
          !arn.endsWith(`:${region}:${account}:table/${table}`)
        ) {
          throw new Error(
            "The imported tracking table does not belong to the target deployment scope.",
          );
        }
        const query = awsObject(
          "Imported tracking table catalog read",
          "dynamodb",
          "query",
          "--table-name",
          table,
          "--consistent-read",
          "--limit",
          "1",
          "--no-paginate",
          "--key-condition-expression",
          "PK = :deployment",
          "--expression-attribute-values",
          JSON.stringify({ ":deployment": { S: `ENV#${identity}` } }),
        );
        if (query.Count !== 0 || query.LastEvaluatedKey !== undefined) {
          throw new Error(
            "The imported table already holds state for this deployment.",
          );
        }
      }
    }

    const assembly = join(workspace, "assembly");
    const artifact = synthesizeController(compiled, generation, assembly, {
      fargatePreflight: fargatePreflight?.environment,
      telemetry: target.telemetry,
      encryption,
    });
    const bootstrap = encryption.bootstrapAssetKeyArn
      ? runtimeCopySource(artifact.template)
      : undefined;
    const checkEncryption = (assetPublished: boolean) =>
      verifyImportedEncryption(
        runtime,
        encryption,
        fargatePreflight?.quotaSource,
        bootstrap,
        assetPublished,
        awsObject,
      );
    checkEncryption(false);
    const changeSetName = `scaleset-${randomUUID()}`;
    commandResult(
      run(process.execPath, [
        cdkCli,
        "deploy",
        "--app",
        assembly,
        "--method",
        "prepare-change-set",
        "--change-set-name",
        changeSetName,
        "--require-approval",
        "never",
        "--no-lookups",
        artifact.id,
      ]),
      "CDK change set preparation",
    );
    const changeSet = awsObject(
      "Prepared change set verification",
      "cloudformation",
      "describe-change-set",
      "--stack-name",
      name,
      "--change-set-name",
      changeSetName,
    );
    const changeSetId = field(changeSet, "ChangeSetId");
    const preparedFor = field(changeSet, "StackId");
    if (
      !stackScope(preparedFor, account, region, name) ||
      (currentStackId && currentStackId !== preparedFor) ||
      changeSet.Status !== "CREATE_COMPLETE" ||
      changeSet.ExecutionStatus !== "AVAILABLE" ||
      !changeSetId.startsWith(
        `${preparedFor.split(":").slice(0, 5).join(":")}:changeSet/${changeSetName}/`,
      )
    ) {
      throw new Error(
        "The prepared CloudFormation change set is incomplete or targets another stack.",
      );
    }
    checkRetirement?.();
    checkEncryption(true);
    commandResult(
      aws(
        "cloudformation",
        "execute-change-set",
        "--change-set-name",
        changeSetId,
      ),
      "CloudFormation change set execution",
    );
    commandResult(
      aws(
        "cloudformation",
        "wait",
        currentStackId ? "stack-update-complete" : "stack-create-complete",
        "--stack-name",
        preparedFor,
      ),
      "CloudFormation deployment",
    );
  } finally {
    rmSync(workspace, { force: true, recursive: true });
  }
}
