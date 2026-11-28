import { randomUUID } from "node:crypto";
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  mkdirSync,
  openSync,
  renameSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import type { Stats } from "node:fs";
import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { parseYaml } from "../config/yaml.ts";
import { readDeploymentDocument } from "./deployment-document.ts";

export interface DeploymentAttempt {
  readonly identity: string;
  readonly generation: string;
  readonly runtimeSha256: string;
  readonly templateSha256: string;
  readonly base: string | null;
  readonly name: string;
  readonly stackId: string | null;
  readonly changeSetId: string | null;
}

const changeSetName = /^scaleset-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u;

export function newChangeSetName(): string {
  return `scaleset-${randomUUID()}`;
}

export function recoveryFile(directory?: string): string {
  const state =
    directory ??
    join(homedir(), ".local", "state", "scaleset-dotnet-aws", "deployments");
  mkdirSync(state, { recursive: true, mode: 0o700 });
  return resolve(state, `${randomUUID()}.json`);
}

function requirePrivateState(state: Stats): void {
  if (
    !state.isFile() ||
    (state.mode & 0o077) !== 0 ||
    (process.getuid !== undefined && state.uid !== process.getuid())
  ) {
    throw new Error(
      "Deployment recovery evidence must be a private file owned by the current user.",
    );
  }
}

function readState(path: string): unknown {
  const state = lstatSync(path);
  requirePrivateState(state);
  const fd = openSync(
    path,
    constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
  );
  let bytes: Buffer | undefined;
  try {
    const opened = fstatSync(fd);
    requirePrivateState(opened);
    if (opened.dev !== state.dev || opened.ino !== state.ino) {
      throw new Error("Deployment recovery evidence changed while opening it.");
    }
    bytes = readDeploymentDocument(fd);
  } finally {
    closeSync(fd);
  }
  if (bytes === undefined) {
    throw new Error("Deployment recovery evidence exceeds 64 KiB.");
  }
  let source: string;
  try {
    source = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(
      bytes,
    );
  } catch {
    throw new Error("Deployment recovery evidence must be valid UTF-8.");
  }
  try {
    const value: unknown = JSON.parse(source);
    parseYaml(source); // Also reject duplicate JSON members.
    return value;
  } catch {
    throw new Error("Deployment recovery evidence is malformed.");
  }
}

export function readAttempt(path: string): DeploymentAttempt {
  const data = readState(path) as Partial<DeploymentAttempt> | null;
  if (
    data === null ||
    typeof data !== "object" ||
    typeof data.identity !== "string" ||
    typeof data.generation !== "string" ||
    typeof data.runtimeSha256 !== "string" ||
    typeof data.templateSha256 !== "string" ||
    (data.base !== null && typeof data.base !== "string") ||
    typeof data.name !== "string" ||
    !changeSetName.test(data.name) ||
    (data.stackId !== null && typeof data.stackId !== "string") ||
    (data.changeSetId !== null && typeof data.changeSetId !== "string") ||
    (data.changeSetId !== null && data.stackId === null)
  ) {
    throw new Error("Deployment recovery evidence is malformed.");
  }
  return data as DeploymentAttempt;
}

function writeState(path: string, state: unknown): void {
  writeFileSync(path, JSON.stringify(state) + "\n", {
    encoding: "utf8",
    flag: "wx",
    mode: 0o600,
    flush: true,
  });
}

export function saveAttempt(
  path: string,
  attempt: DeploymentAttempt,
  creating = false,
): void {
  if (creating) {
    writeState(path, attempt);
    return;
  }
  const staged = `${path}.${randomUUID()}.tmp`;
  try {
    writeState(staged, attempt);
    renameSync(staged, path);
  } finally {
    rmSync(staged, { force: true });
  }
}

type Decision = "execute" | "discard";

export function readDecision(path: string, name: string): Decision | undefined {
  try {
    const decision = readState(`${path}.${name}.decision`);
    if (decision !== "execute" && decision !== "discard") {
      throw new Error("Deployment recovery decision is malformed.");
    }
    return decision;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

export function decide(path: string, name: string, decision: Decision): void {
  try {
    writeState(`${path}.${name}.decision`, decision);
  } catch (error) {
    if (
      (error as NodeJS.ErrnoException).code !== "EEXIST" ||
      readDecision(path, name) !== decision
    ) {
      throw new Error(
        "The change set already has a different or unverified recovery decision.",
      );
    }
  }
}

export function finishAttempt(path: string, name: string): void {
  rmSync(path);
  rmSync(`${path}.${name}.decision`, { force: true });
}
