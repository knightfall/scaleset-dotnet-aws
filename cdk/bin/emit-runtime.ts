import { mkdtemp, readFile, rename, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { compileConfig } from "../lib/config/compiler.ts";
import { ConfigValidationError } from "../lib/config/errors.ts";

async function emitRuntime(args: string[]): Promise<void> {
  if (args.length !== 2) {
    throw new Error("Usage: config:emit <source.yaml> <runtime.json>");
  }

  const [source, destination] = args;
  const { json, sha256 } = compileConfig(await readFile(source));
  const output = resolve(destination);
  const temporary = await mkdtemp(join(dirname(output), ".runtime-"));
  try {
    const staged = join(temporary, "runtime.json");
    await writeFile(staged, json, "utf8");
    await rename(staged, output);
  } finally {
    await rm(temporary, { recursive: true, force: true });
  }
  console.log(sha256);
}

try {
  await emitRuntime(process.argv.slice(2));
} catch (error) {
  if (error instanceof ConfigValidationError) {
    console.error(error.code, error.path || "/", error.message);
  } else {
    console.error(error instanceof Error ? error.message : String(error));
  }
  process.exitCode = 1;
}
