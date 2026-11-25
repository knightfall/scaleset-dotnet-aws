import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { isDeepStrictEqual } from "node:util";
import {
  compileConfig,
  decodeConfiguration,
  validateRuntime,
} from "../config/compiler.ts";
import { awsPartition } from "../config/semantics.ts";
import { synthesizeController } from "./controller-stack.ts";
import { resolveControllerTelemetry } from "./controller-telemetry.ts";
import {
  controllerStackName,
  deploymentIdentity,
  generationValue,
  trackingTableName,
} from "./deployment-input.ts";
import type { ControllerDeploymentEnvironment } from "./deployment-input.ts";
import {
  decide,
  finishAttempt,
  newChangeSetName,
  readAttempt,
  readDecision,
  recoveryFile,
  saveAttempt,
} from "./deployment-recovery.ts";
import type { DeploymentAttempt } from "./deployment-recovery.ts";
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
  readonly resume?: string;
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
const runtimeObjectUri =
  /^s3:\/\/([a-z0-9.-]+)\/(runtime\/[0-9a-f]{64}\.json)$/u;

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
    parts[1] === awsPartition(region) &&
    parts[2] === "cloudformation" &&
    parts[3] === region &&
    parts[4] === account &&
    parts[5].startsWith(`stack/${name}/`) &&
    /^[a-zA-Z0-9-]+$/u.test(parts[5].slice(name.length + 7))
  );
}

function serviceError(result: CommandResult, code: string, operation: string) {
  const message = result.stderr.trim();
  const prefix = `An error occurred (${code}) when calling the ${operation} operation: `;
  return result.status === 254 &&
    result.stdout.trim() === "" &&
    !/[\r\n]/u.test(message) &&
    message.startsWith(prefix) &&
    message.length > prefix.length
    ? message.slice(prefix.length)
    : undefined;
}

function stackOutputs(stack: ObjectProperties): Map<string, string> {
  const outputs = new Map<string, string>();
  for (const output of rows(stack.Outputs)) {
    const key = field(output, "OutputKey");
    if (outputs.has(key)) {
      throw new Error("The current stack has duplicate output keys.");
    }
    outputs.set(key, field(output, "OutputValue"));
  }
  return outputs;
}

function stableStackEvidence(stack: ObjectProperties): string {
  return createHash("sha256")
    .update(
      JSON.stringify([
        stack.StackId,
        stack.StackStatus,
        stack.LastUpdatedTime,
        [...stackOutputs(stack)].sort(),
      ]),
    )
    .digest("hex");
}

function reviewPlaceholder(stack: ObjectProperties): boolean {
  return (
    stack.StackStatus === "REVIEW_IN_PROGRESS" &&
    (stack.Outputs === undefined ||
      (Array.isArray(stack.Outputs) && stack.Outputs.length === 0))
  );
}

export function deployController(
  target: DeploymentTarget,
  run: RunCommand,
  recoveryDirectory?: string,
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
  function describeStack(): ObjectProperties | undefined {
    const result = aws(
      "cloudformation",
      "describe-stacks",
      "--stack-name",
      name,
    );
    if (result.status !== 0) {
      if (
        serviceError(result, "ValidationError", "DescribeStacks") ===
        `Stack with id ${name} does not exist`
      ) {
        return undefined;
      }
      throw new Error(
        "Could not establish whether the deployment stack already exists.",
      );
    }
    const stacks = rows(object(JSON.parse(result.stdout)).Stacks);
    if (
      stacks.length !== 1 ||
      stacks[0].StackName !== name ||
      !stackScope(field(stacks[0], "StackId"), account, region, name)
    ) {
      throw new Error(
        "The current stack identity differs from the compiled deployment.",
      );
    }
    return stacks[0];
  }

  function describeChangeSet(
    stack: ObjectProperties,
    attempt: DeploymentAttempt,
  ): ObjectProperties | undefined {
    const stackId = field(stack, "StackId");
    if (attempt.stackId !== null && attempt.stackId !== stackId) {
      throw new Error(
        "The recovery record belongs to a different stack identity.",
      );
    }
    const result = aws(
      "cloudformation",
      "describe-change-set",
      "--stack-name",
      stackId,
      "--change-set-name",
      attempt.changeSetId ?? attempt.name,
    );
    if (
      serviceError(result, "ChangeSetNotFound", "DescribeChangeSet") !==
      undefined
    ) {
      return undefined;
    }
    const change = object(
      JSON.parse(commandResult(result, "Prepared change set verification")),
    );
    const changeSetId = field(change, "ChangeSetId");
    const prefix = `arn:${awsPartition(region)}:cloudformation:${region}:${account}:changeSet/${attempt.name}/`;
    if (
      change.StackId !== stackId ||
      change.StackName !== name ||
      !changeSetId.startsWith(prefix) ||
      !/^[a-zA-Z0-9-]+$/u.test(changeSetId.slice(prefix.length)) ||
      (attempt.changeSetId !== null && attempt.changeSetId !== changeSetId)
    ) {
      throw new Error(
        "The prepared CloudFormation change set targets another stack or attempt.",
      );
    }
    return change;
  }

  function isApplied(
    stack: ObjectProperties | undefined,
    attempt: DeploymentAttempt,
  ): boolean {
    if (
      stack === undefined ||
      attempt.stackId === null ||
      stack.StackId !== attempt.stackId ||
      stack.StackStatus !==
        (attempt.base === null ? "CREATE_COMPLETE" : "UPDATE_COMPLETE")
    ) {
      return false;
    }
    const outputs = stackOutputs(stack);
    const version = outputs.get("RuntimeConfigVersionId");
    const runtimeKey = outputs
      .get("RuntimeConfigUri")
      ?.match(runtimeObjectUri)?.[2];
    return (
      outputs.get("DeploymentIdentity") === identity &&
      outputs.get("TrackingTableName") === table &&
      outputs.get("RuntimeConfigSha256") === compiled.sha256 &&
      outputs.get("DeploymentGeneration") === generation &&
      version !== undefined &&
      version !== "null" &&
      runtimeKey === `runtime/${compiled.sha256}.json`
    );
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
  let journalPath = target.resume ? resolve(target.resume) : undefined;
  try {
    const candidateFile = join(workspace, "candidate.json");
    writeFileSync(candidateFile, compiled.json, {
      encoding: "utf8",
      mode: 0o600,
    });
    const prior = describeStack();
    let attempt = journalPath ? readAttempt(journalPath) : undefined;
    let decision =
      attempt && journalPath
        ? readDecision(journalPath, attempt.name)
        : undefined;
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
    function templateMatches(stackId: string, changeSetId?: string): boolean {
      const template = awsObject(
        "CloudFormation template verification",
        "cloudformation",
        "get-template",
        "--stack-name",
        stackId,
        "--template-stage",
        "Original",
        ...(changeSetId ? ["--change-set-name", changeSetId] : []),
      ).TemplateBody;
      return isDeepStrictEqual(
        typeof template === "string" ? JSON.parse(template) : template,
        artifact.template,
      );
    }
    const templateSha256 = createHash("sha256")
      .update(readFileSync(artifact.templateFullPath))
      .digest("hex");
    if (
      attempt &&
      (attempt.identity !== identity ||
        attempt.generation !== generation ||
        attempt.runtimeSha256 !== compiled.sha256 ||
        attempt.templateSha256 !== templateSha256)
    ) {
      throw new Error(
        "The recovery record does not match the exact reviewed deployment inputs.",
      );
    }

    function verifyCompleted(recovery: DeploymentAttempt) {
      const preparedFor = recovery.stackId;
      if (!preparedFor || !journalPath)
        throw new Error("The execution receipt is incomplete.");
      commandResult(
        aws(
          "cloudformation",
          "wait",
          recovery.base === null
            ? "stack-create-complete"
            : "stack-update-complete",
          "--stack-name",
          preparedFor,
        ),
        "CloudFormation deployment",
      );
      if (
        !isApplied(describeStack(), recovery) ||
        !templateMatches(preparedFor)
      ) {
        throw new Error(
          "The deployed stack does not confirm the exact candidate generation and digest.",
        );
      }
      finishAttempt(journalPath, recovery.name);
    }

    if (decision === "execute" && attempt && journalPath) {
      if (
        isApplied(prior, attempt) &&
        attempt.stackId &&
        templateMatches(attempt.stackId)
      ) {
        finishAttempt(journalPath, attempt.name);
        return;
      }
      if (
        prior &&
        prior.StackId === attempt.stackId &&
        (prior.StackStatus === "CREATE_IN_PROGRESS" ||
          prior.StackStatus === "UPDATE_IN_PROGRESS" ||
          prior.StackStatus === "UPDATE_COMPLETE_CLEANUP_IN_PROGRESS")
      ) {
        const receipt = describeChangeSet(prior, attempt);
        if (
          receipt?.ExecutionStatus === "EXECUTE_IN_PROGRESS" ||
          receipt?.ExecutionStatus === "EXECUTE_COMPLETE"
        ) {
          verifyCompleted(attempt);
          return;
        }
        throw new Error(
          "The in-progress stack cannot be attributed to the recorded change set.",
        );
      }
    }

    let checkRetirement: (() => void) | undefined;
    const base =
      prior && stableStackStatuses.has(field(prior, "StackStatus"))
        ? stableStackEvidence(prior)
        : null;
    const discardedPlaceholder =
      decision === "discard" && attempt?.base === null && prior === undefined;
    if (
      attempt &&
      (attempt.base !== base ||
        (attempt.stackId &&
          prior?.StackId !== attempt.stackId &&
          !discardedPlaceholder))
    ) {
      throw new Error(
        "The current stack has changed since the recorded deployment attempt.",
      );
    }
    if (prior && base === null && (!attempt || !reviewPlaceholder(prior))) {
      throw new Error(
        "The current stack is unstable or belongs to another deployment attempt.",
      );
    }

    if (prior && base !== null) {
      const outputs = stackOutputs(prior);
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
      const source = oldUri?.match(runtimeObjectUri);
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
        JSON.parse(decodeConfiguration(currentBytes)),
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
    }

    function checkFreshTracking() {
      const described = aws(
        "dynamodb",
        "describe-table",
        "--table-name",
        table,
      );
      if (runtime.spec.tracking.table.type === "create") {
        if (
          serviceError(
            described,
            "ResourceNotFoundException",
            "DescribeTable",
          ) === undefined
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
          arn !==
            `arn:${awsPartition(region)}:dynamodb:${region}:${account}:table/${table}`
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

    function discardPrepared(recovery: DeploymentAttempt) {
      const current = describeStack();
      if (
        !current ||
        current.StackId !== recovery.stackId ||
        (!stableStackStatuses.has(field(current, "StackStatus")) &&
          !reviewPlaceholder(current))
      ) {
        throw new Error(
          "The original stack could not be verified for change set cleanup.",
        );
      }
      const pending = describeChangeSet(current, recovery);
      if (!pending) return;
      if (
        !(
          pending.Status === "CREATE_COMPLETE" &&
          pending.ExecutionStatus === "AVAILABLE"
        ) &&
        !(
          pending.Status === "FAILED" &&
          pending.ExecutionStatus === "UNAVAILABLE"
        )
      ) {
        throw new Error("The owned change set is no longer safe to discard.");
      }
      commandResult(
        aws(
          "cloudformation",
          "delete-change-set",
          "--stack-name",
          field(current, "StackId"),
          "--change-set-name",
          field(pending, "ChangeSetId"),
        ),
        "Owned CloudFormation change set cleanup",
      );
    }

    let changeSet =
      prior && attempt ? describeChangeSet(prior, attempt) : undefined;
    if (attempt && decision === "discard") {
      if (!attempt.changeSetId || !attempt.stackId) {
        throw new Error(
          "The rejected change set has no verified cleanup receipt.",
        );
      }
      if (changeSet) discardPrepared(attempt);
      changeSet = undefined;
    }
    if (base === null) checkFreshTracking();
    checkRetirement?.();
    checkEncryption(false);
    if (!changeSet) {
      if (
        decision === "execute" ||
        (prior && base === null && attempt?.stackId !== prior.StackId)
      ) {
        throw new Error(
          "The exact change set or fresh review stack could not be recovered.",
        );
      }
      const creating = attempt === undefined;
      const retainedStack = prior ? field(prior, "StackId") : null;
      journalPath ??= recoveryFile(recoveryDirectory);
      attempt = {
        identity,
        generation,
        runtimeSha256: compiled.sha256,
        templateSha256,
        base,
        name: newChangeSetName(),
        stackId: retainedStack,
        changeSetId: null,
      };
      saveAttempt(journalPath, attempt, creating);
      decision = undefined;

      commandResult(
        run(process.execPath, [
          cdkCli,
          "deploy",
          "--app",
          assembly,
          "--method",
          "prepare-change-set",
          "--change-set-name",
          attempt.name,
          "--require-approval",
          "never",
          "--no-lookups",
          artifact.id,
        ]),
        "CDK change set preparation",
      );
      const prepared = describeStack();
      if (
        !prepared ||
        (base === null
          ? !reviewPlaceholder(prepared)
          : prepared.StackId !== attempt.stackId ||
            stableStackEvidence(prepared) !== base)
      ) {
        throw new Error(
          "The authoritative stack changed when preparing the change set.",
        );
      }
      changeSet = describeChangeSet(prepared, attempt);
    }

    if (!changeSet || !attempt || !journalPath) {
      throw new Error("The exact prepared change set was not confirmed.");
    }
    const changeSetId = field(changeSet, "ChangeSetId");
    const preparedFor = field(changeSet, "StackId");
    if (
      decision === undefined &&
      changeSet.Status === "FAILED" &&
      changeSet.ExecutionStatus === "UNAVAILABLE"
    ) {
      attempt = { ...attempt, stackId: preparedFor, changeSetId };
      saveAttempt(journalPath, attempt);
      decide(journalPath, attempt.name, "discard");
      discardPrepared(attempt);
      throw new Error(
        "CloudFormation rejected the owned change set during preparation.",
      );
    }
    if (
      changeSet.Status !== "CREATE_COMPLETE" ||
      changeSet.ExecutionStatus !== "AVAILABLE"
    ) {
      throw new Error("The owned change set is not ready for execution.");
    }
    if (!templateMatches(preparedFor, changeSetId)) {
      throw new Error(
        "The prepared CloudFormation template differs from the reviewed deployment.",
      );
    }
    attempt = { ...attempt, stackId: preparedFor, changeSetId };
    saveAttempt(journalPath, attempt);

    try {
      const current = describeStack();
      if (
        !current ||
        current.StackId !== preparedFor ||
        (base === null
          ? !reviewPlaceholder(current)
          : !stableStackStatuses.has(field(current, "StackStatus")) ||
            stableStackEvidence(current) !== base)
      ) {
        throw new Error(
          "The authoritative stack changed before change set execution.",
        );
      }
      if (base === null) checkFreshTracking();
      checkRetirement?.();
      checkEncryption(true);
    } catch (error) {
      if (decision === undefined) {
        decide(journalPath, attempt.name, "discard");
        discardPrepared(attempt);
      }
      throw error;
    }

    decide(journalPath, attempt.name, "execute");
    commandResult(
      aws(
        "cloudformation",
        "execute-change-set",
        "--stack-name",
        preparedFor,
        "--change-set-name",
        changeSetId,
        "--client-request-token",
        attempt.name,
      ),
      "CloudFormation change set execution",
    );
    verifyCompleted(attempt);
  } catch (error) {
    if (!journalPath) throw error;
    const message = error instanceof Error ? error.message : String(error);
    throw new Error(`${message} Recovery record: ${journalPath}`, {
      cause: error,
    });
  } finally {
    rmSync(workspace, { force: true, recursive: true });
  }
}
