import {
  isAlias,
  isCollection,
  isPair,
  isScalar,
  LineCounter,
  parseAllDocuments,
  visit,
} from "yaml";
import { ConfigValidationError } from "./errors.ts";

const coreTags = new Set(
  ["bool", "float", "int", "map", "null", "seq", "str"].map(
    (tag) => "tag:yaml.org,2002:" + tag,
  ),
);

function isIntegralDecimal(source: string): boolean {
  const match = /^[-+]?([0-9]*)(?:\.([0-9]*))?(?:[eE]([-+]?[0-9]+))?$/.exec(
    source,
  );
  if (!match) {
    return false;
  }

  const [, whole, fraction = "", exponent = "0"] = match;
  const digits = whole + fraction;
  if (digits.length === 0) {
    return false;
  }

  let end = digits.length;
  while (digits[end - 1] === "0") {
    end -= 1;
  }
  if (end === 0) {
    return true;
  }

  // Trailing zeros may remain to the right of the shifted decimal point.
  const trailingZeros = digits.length - end;
  return Number(exponent) >= fraction.length - trailingZeros;
}

export function parseYaml(source: string): unknown {
  const lineCounter = new LineCounter();
  const documents = parseAllDocuments(source, {
    lineCounter,
    merge: false,
    prettyErrors: false,
    schema: "core",
    strict: true,
    stringKeys: false,
    uniqueKeys: true,
    version: "1.2",
  });
  if (documents.length !== 1) {
    throw new ConfigValidationError(
      "yaml",
      "",
      "Expected exactly one YAML document.",
    );
  }

  const [document] = documents;
  const diagnostic = document.errors[0] ?? document.warnings[0];
  if (diagnostic) {
    const { line, col } = lineCounter.linePos(diagnostic.pos[0]);
    // The YAML parser's rendered message can include source text.
    throw new ConfigValidationError(
      "yaml",
      "",
      `YAML ${diagnostic.code} at line ${line}, column ${col}.`,
    );
  }

  function rejectYaml(message: string, offset?: number): never {
    if (offset === undefined) {
      throw new ConfigValidationError("yaml", "", message);
    }

    const { line, col } = lineCounter.linePos(offset);
    throw new ConfigValidationError(
      "yaml",
      "",
      `${message} Line ${line}, column ${col}.`,
    );
  }

  visit(document, (_, node) => {
    if (isAlias(node)) {
      rejectYaml("YAML aliases are unsupported.", node.range?.[0]);
    }
    if (isScalar(node) || isCollection(node)) {
      if (node.anchor !== undefined) {
        rejectYaml("YAML anchors are unsupported.", node.range?.[0]);
      }
      if (node.tag !== undefined && !coreTags.has(node.tag)) {
        rejectYaml("Custom YAML tags are unsupported.", node.range?.[0]);
      }
    }
    if (isPair(node)) {
      const key = node.key;
      if (!isScalar(key) || typeof key.value !== "string") {
        rejectYaml("YAML mapping keys must be strings.");
      }
      if (key.value === "<<") {
        rejectYaml("YAML merge keys are unsupported.", key.range?.[0]);
      }
    }
    if (isScalar(node) && typeof node.value === "number") {
      const number = node.value;
      if (
        !Number.isFinite(number) ||
        (Number.isInteger(number) && !Number.isSafeInteger(number))
      ) {
        rejectYaml(
          "YAML numbers must be finite and integers must be exactly representable.",
          node.range?.[0],
        );
      }
      if (
        Number.isSafeInteger(number) &&
        node.format !== "HEX" &&
        node.format !== "OCT" &&
        !isIntegralDecimal(node.source ?? "")
      ) {
        rejectYaml(
          "YAML numeric value loses precision when parsed as an integer.",
          node.range?.[0],
        );
      }
    }
  });

  return document.toJS({ maxAliasCount: 0 });
}
