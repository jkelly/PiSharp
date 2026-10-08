// This normalizer deliberately changes object key order only.
export const NORMALIZER_VERSION = "object-key-order-v1";

export function canonicalJson(value) {
  if (value === null || typeof value !== "object") return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(",")}}`;
}

export function compareJson(expected, actual) {
  return canonicalJson(expected) === canonicalJson(actual);
}
