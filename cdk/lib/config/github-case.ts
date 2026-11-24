import { readFileSync } from "node:fs";

const data = new URL(
  "../../config/unicode/CaseFolding-17.0.0.txt",
  import.meta.url,
);
const mappings = new Map<string, string>();

for (const line of readFileSync(data, "utf8").split("\n")) {
  const [source, status, target] = line.split(";");
  if (status?.trim() === "C" || status?.trim() === "S") {
    mappings.set(
      String.fromCodePoint(Number.parseInt(source, 16)),
      String.fromCodePoint(Number.parseInt(target, 16)),
    );
  }
}

export function foldGitHubIdentity(value: string): string {
  let folded = "";
  for (const character of value) {
    folded += mappings.get(character) ?? character;
  }
  return folded;
}
