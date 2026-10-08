// Strict JSON comparison: object keys sorted; numeric lexemes kept verbatim.
// Duplicate property names are invalid evidence, even when their values agree.
export function canonicalRawJson(raw, onNumber = () => {}) {
  let offset = 0;
  const whitespace = () => { while (/^[\x20\x09\x0a\x0d]$/.test(raw[offset] ?? "")) offset++; };
  function fail(message) { throw new SyntaxError(`${message} at JSON offset ${offset}`); }
  function quoted() {
    const start = offset++;
    while (offset < raw.length) {
      const character = raw[offset++];
      if (character === "\\") { offset++; continue; }
      if (character === '"') return JSON.parse(raw.slice(start, offset));
    }
    fail("Unterminated JSON string");
  }
  function value(depth) {
    if (depth > 128) fail("JSON exceeds evidence depth limit");
    whitespace();
    if (raw[offset] === '"') return JSON.stringify(quoted());
    if (raw[offset] === "{") {
      offset++; whitespace();
      const entries = new Map();
      if (raw[offset] === "}") { offset++; return "{}"; }
      while (true) {
        if (raw[offset] !== '"') fail("Expected JSON property name");
        const key = quoted();
        if (entries.has(key)) fail(`Duplicate JSON property ${JSON.stringify(key)}`);
        whitespace();
        if (raw[offset++] !== ":") fail("Expected colon");
        entries.set(key, value(depth + 1)); whitespace();
        if (raw[offset] === "}") { offset++; break; }
        if (raw[offset++] !== ",") fail("Expected object comma");
        whitespace();
      }
      return `{${[...entries.keys()].sort().map(key => `${JSON.stringify(key)}:${entries.get(key)}`).join(",")}}`;
    }
    if (raw[offset] === "[") {
      offset++; whitespace();
      const elements = [];
      if (raw[offset] === "]") { offset++; return "[]"; }
      while (true) {
        elements.push(value(depth + 1)); whitespace();
        if (raw[offset] === "]") { offset++; break; }
        if (raw[offset++] !== ",") fail("Expected array comma");
      }
      return `[${elements.join(",")}]`;
    }
    const number = raw.slice(offset).match(/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/);
    if (number) { offset += number[0].length; onNumber(number[0]); return number[0]; }
    for (const literal of ["true", "false", "null"]) {
      if (raw.startsWith(literal, offset)) { offset += literal.length; return literal; }
    }
    fail("Invalid JSON value");
  }
  const result = value(0);
  whitespace();
  if (offset !== raw.length) fail("Trailing JSON data");
  return result;
}

function decimalIdentity(lexeme) {
  const match = lexeme.match(/^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/);
  const fraction = match[3] ?? "";
  let digits = `${match[2]}${fraction}`.replace(/^0+/, "");
  if (!digits) return "0";
  let exponent = BigInt(match[4] ?? "0") - BigInt(fraction.length);
  while (digits.endsWith("0")) { digits = digits.slice(0, -1); exponent++; }
  return `${match[1]}${digits}e${exponent}`;
}

// Runtime inputs must survive JSON parse/stringify without decimal loss.
// The raw comparator has no such precision limit.
export function parseJsonSupported(raw) {
  canonicalRawJson(raw, lexeme => {
    const number = Number(lexeme);
    if (!Number.isFinite(number) || decimalIdentity(lexeme) !== decimalIdentity(JSON.stringify(number))) throw new RangeError(`Number loses precision in the JS runtime: ${lexeme}`);
    if (number === 0 && lexeme.startsWith("-")) throw new RangeError("Negative zero is unsupported in runtime evidence");
  });
  return JSON.parse(raw);
}

export function compareRawJson(expected, actual) {
  return canonicalRawJson(expected) === canonicalRawJson(actual);
}
