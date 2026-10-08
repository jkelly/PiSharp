import { readFileSync } from "node:fs";
import { NORMALIZER_VERSION } from "./canonical-json.mjs";
import { canonicalRawJson, compareRawJson } from "./raw-json.mjs";

const [expectedPath, actualPath] = process.argv.slice(2);
if (!expectedPath || !actualPath || process.argv.length !== 4) {
  console.error("Usage: node tools/CompatibilityReport/compare.mjs EXPECTED.json ACTUAL.json");
  process.exitCode = 2;
} else {
  try {
    const expected = readFileSync(expectedPath, "utf8");
    const actual = readFileSync(actualPath, "utf8");
    const equal = compareRawJson(expected, actual);
    console.log(JSON.stringify({ equal, normalizerVersion: NORMALIZER_VERSION }));
    if (!equal) {
      console.error(`expected: ${canonicalRawJson(expected)}`);
      console.error(`actual:   ${canonicalRawJson(actual)}`);
      process.exitCode = 1;
    }
  } catch (error) {
    console.error(error.message);
    process.exitCode = 2;
  }
}
