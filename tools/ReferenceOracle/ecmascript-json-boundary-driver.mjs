// Authored exact-rational input generation; expected strings are separately observed from Node builtins.
import assert from 'node:assert/strict';

const fractionMask = (1n << 52n) - 1n, maxFinite = 0x7fefffffffffffffn;
const negativeMask = 1n << 63n;
const powers5 = [1n];
function power5(exponent) { while (powers5.length <= exponent) powers5.push(powers5.at(-1) * 5n); return powers5[exponent]; }
function reduced(n, d) {
  if (n === 0n) return { n: 0n, d: 1n };
  while ((n & 1n) === 0n && (d & 1n) === 0n) { n >>= 1n; d >>= 1n; }
  return { n, d };
}
function rational(bits) {
  assert(bits >= 0n && bits <= maxFinite);
  const exponent = Number(bits >> 52n), fraction = bits & fractionMask;
  if (exponent === 0) return reduced(fraction, 1n << 1074n);
  const significand = (1n << 52n) | fraction, power = exponent - 1023 - 52;
  return power >= 0 ? { n: significand << BigInt(power), d: 1n } : reduced(significand, 1n << BigInt(-power));
}
function exactDecimal(n, d = 1n) {
  ({ n, d } = reduced(n, d)); let places = 0;
  for (let remaining = d; remaining > 1n; remaining >>= 1n) { assert((remaining & 1n) === 0n); places++; }
  let digits = (n * power5(places)).toString();
  if (places === 0) return digits;
  if (digits.length <= places) digits = '0'.repeat(places + 1 - digits.length) + digits;
  const at = digits.length - places;
  return (digits.slice(0, at) + '.' + digits.slice(at)).replace(/0+$/, '').replace(/\.$/, '');
}
function decimalOfBits(bits) { const { n, d } = rational(bits); return exactDecimal(n, d); }
function binaryPowerBits(exponent) {
  return exponent >= -1022 ? BigInt(exponent + 1023) << 52n : 1n << BigInt(exponent + 1074);
}
function compareRationalBits(n, d, bits) { const value = rational(bits), left = n * value.d, right = value.n * d; return left < right ? -1 : left > right ? 1 : 0; }
function nearestDecimalPowerBits(exponent) {
  const n = exponent >= 0 ? 10n ** BigInt(exponent) : 1n;
  const d = exponent < 0 ? 10n ** BigInt(-exponent) : 1n;
  let low = 0n, high = maxFinite;
  while (low < high) {
    const middle = (low + high + 1n) >> 1n;
    if (compareRationalBits(n, d, middle) >= 0) low = middle; else high = middle - 1n;
  }
  if (compareRationalBits(n, d, low) === 0) return low;
  assert(low < maxFinite);
  const before = rational(low), after = rational(low + 1n);
  const comparison = 2n * n * before.d * after.d - d * (before.n * after.d + after.n * before.d);
  return comparison < 0n ? low : comparison > 0n ? low + 1n : (low & 1n) === 0n ? low : low + 1n;
}
const exponentId = exponent => exponent < 0 ? 'neg' + -exponent : 'pos' + exponent;

export function authoredInput() {
  const cases = [], familyCounts = Object.create(null), ids = new Set();
  const add = (family, caseId, json) => {
    assert(!ids.has(caseId)); ids.add(caseId); assert(json.length <= 4096 && cases.length < 20000);
    cases.push({ caseId, json }); familyCounts[family] = (familyCounts[family] ?? 0) + 1;
  };
  const signed = (family, name, raw) => {
    add(family, name + '-positive', raw); add(family, name + '-negative', raw.startsWith('-') ? raw.slice(1) : '-' + raw);
  };
  // Every representable power of two, including all subnormal powers, and the immediate binary64 neighbors.
  for (let exponent = -1074; exponent <= 1023; exponent++) {
    const bits = binaryPowerBits(exponent);
    for (const [name, neighbor] of [['previous', bits - 1n], ['center', bits], ['next', bits + 1n]])
      signed('binary-power-neighbors', 'binary-' + exponentId(exponent) + '-' + name, decimalOfBits(neighbor));
  }
  // Decimal powers cover the underflow boundary through the largest finite power of ten.
  // The nearest center is authored by exact integer nearest/even arithmetic, never by Number/JSON.parse.
  for (let exponent = -324; exponent <= 308; exponent++) {
    const bits = nearestDecimalPowerBits(exponent), name = 'decimal-' + exponentId(exponent);
    signed('decimal-power-neighbors', name + '-literal-power', '1e' + exponent);
    signed('decimal-power-neighbors', name + '-previous', bits === 0n ? '-' + decimalOfBits(1n) : decimalOfBits(bits - 1n));
    signed('decimal-power-neighbors', name + '-center', decimalOfBits(bits));
    signed('decimal-power-neighbors', name + '-next', decimalOfBits(bits + 1n));
  }
  const midpoint = (name, first, second) => {
    const a = rational(first), b = rational(second), mid = reduced(a.n * b.d + b.n * a.d, 2n * a.d * b.d);
    for (const [side, delta] of [['below', -1n], ['exact', 0n], ['above', 1n]])
      signed('midpoint-nearest-even', name + '-' + side, exactDecimal(mid.n * 4n + delta, mid.d * 4n));
  };
  for (const exponent of [-1074, -1073, -1023, -1022, -53, -52, -1, 0, 1, 20, 52, 53, 54, 63, 64, 100, 1023]) {
    const bits = binaryPowerBits(exponent);
    midpoint('midpoint-binary-' + exponentId(exponent) + '-left', bits - 1n, bits);
    midpoint('midpoint-binary-' + exponentId(exponent) + '-right', bits, bits + 1n);
  }
  midpoint('midpoint-one-odd-lower-even-upper', 0x3ff0000000000001n, 0x3ff0000000000002n);
  const overflow = (1n << 1024n) - (1n << 970n);
  for (const [side, delta] of [['below', -1n], ['exact', 0n], ['above', 1n]])
    signed('overflow-midpoint', 'overflow-halfway-' + side, (overflow + delta).toString());
  for (const [name, bits] of [['maximum-finite-previous', maxFinite - 1n], ['maximum-finite', maxFinite],
    ['maximum-subnormal', 0x000fffffffffffffn], ['minimum-normal', 0x0010000000000000n], ['minimum-subnormal', 1n]])
    signed('explicit-binary-extrema', name, decimalOfBits(bits));
  const numbers = [
    '0', '-0', '0.0', '-0.000', '-0e10000', '1e400', '-1e400', '1e309', '-1e309', '1e10000', '-1e10000',
    '1e-10000', '-1e-10000', '0e1000000000', '-0e-1000000000', '1e1000000000', '1e-1000000000',
    '9007199254740991', '9007199254740992', '9007199254740993', '9007199254740994', '-9007199254740993',
    '18446744073709551615', '18446744073709551616', '18446744073709551617', '-18446744073709551617',
    '1000000000000000128', '-1000000000000000128', '1.234567890123456789', '-1.234567890123456789',
    '1E+00', '1.2300e+2', '0.000001', '0.0000001', '100000000000000000000', '1000000000000000000000',
    '2.4703282292062327e-324', '2.4703282292062328e-324', '4.9406564584124654e-324',
    '1.7976931348623157e308', '1.7976931348623158e308', '1.7976931348623159e308'
  ];
  numbers.forEach((raw, index) => add('explicit-decimal-lexemes', 'decimal-lexeme-' + index.toString().padStart(2, '0'), raw));
  for (let control = 0; control < 32; control++)
    add('unicode-control-strings', 'control-' + control.toString(16).padStart(2, '0'), '"\\u' + control.toString(16).padStart(4, '0') + '"');
  const strings = [
    String.raw`"\ud800"`, String.raw`"\udfff"`, String.raw`"\ud800\ud800"`, String.raw`"\udfff\udfff"`,
    String.raw`"\ud800x"`, String.raw`"x\udfff"`, String.raw`"\udfff\ud800"`, String.raw`"\ud83d\ude42"`,
    '"\u03c0\u{1f642}e\u0301"', '"\u2028\u2029"', String.raw`"\u2028\u2029"`,
    String.raw`"\b\t\n\f\r\u0000\u0001\u001f"`, String.raw`"\"\\\/"`, '"\u007f\u0085\u00a0\ufeff"',
    String.raw`{"\ud800":"\udfff","\u0000":"NUL","\u2028":"separator"}`,
    String.raw`["\ud800",{"pair":"\ud83d\ude42","nul":"\u0000"}]`
  ];
  strings.forEach((json, index) => add('unicode-strings-and-keys', 'unicode-' + index.toString().padStart(2, '0'), json));
  const objects = [
    '{"z":0,"a":1,"z":2,"0":"zero","a":null}',
    '{"4294967295":"not-index","4294967294":"last-index","1":"one","0":"zero","01":"leading","-0":"negative"}',
    '{"10":"ten","2":"two","1":"one","a":"letter","00":"doublezero","0001":"zeros","1.0":"decimal","1e0":"exponent"}',
    '{"b":1,"a":2,"b":3,"c":4,"a":5}', '{"2":1,"a":2,"1":3,"2":4,"a":5}',
    '{"__proto__":{"inert":true},"constructor":1,"toString":2,"__proto__":null}',
    '{"x":{"b":1,"a":2,"b":3},"y":[{"2":1,"1":2,"a":3},-0,1e400]}',
    '{"a":0,"\\u0061":1,"z":2}', '{"0":0,"\\u0030":1,"-0":2,"4294967294":3,"4294967295":4}',
    '[9007199254740993,1.2300e+2,1e400,-0,{"nil":null,"unknown":{"$type":"Fake.Type, Native.Assembly"}}]',
    'null', 'true', 'false', '[]', '{}', ' \t\r\n{"b":1,"a":2}\n'
  ];
  objects.forEach((json, index) => add('property-order-and-nesting', 'property-' + index.toString().padStart(2, '0'), json));
  return { schemaVersion: 1, fixtureId: 'ecmascript-json-boundary', kind: 'authored-exact-rational-json-text-inputs',
    generation: { algorithm: 'Positive binary64 bit-pattern rationals; exact decimal expansions; independent BigInt nearest/even decimal powers; signed adjacent values and selected midpoint neighborhoods',
      binaryExponents: { minimum: -1074, maximum: 1023, powers: 2098 }, decimalExponents: { minimum: -324, maximum: 308, powers: 633 },
      familyCounts, caseCount: cases.length, maximumCaseUtf16Units: Math.max(...cases.map(test => test.json.length)),
      limits: { maximumCases: 20000, maximumCaseUtf16Units: 4096 }, randomGeneration: false,
      generatedInputsAreNotObservedOutputs: true, noNumberParseStringifyInNumericAuthoring: true }, cases };
}

function numberBits(number) {
  const buffer = new ArrayBuffer(8), view = new DataView(buffer); view.setFloat64(0, number, false);
  return view.getBigUint64(0, false).toString(16).padStart(16, '0');
}
export function observeBuiltins(input) {
  assert.equal(input.fixtureId, 'ecmascript-json-boundary'); assert(input.cases.length <= 20000);
  const cases = [], numericSemantics = [];
  for (const test of input.cases) {
    assert(typeof test.json === 'string' && test.json.length <= 4096);
    const parsed = JSON.parse(test.json), serialized = JSON.stringify(parsed);
    assert.equal(typeof serialized, 'string'); cases.push({ caseId: test.caseId, json: test.json, serialized });
    if (typeof parsed === 'number') numericSemantics.push({ caseId: test.caseId, binary64Hex: numberBits(parsed),
      negativeZero: Object.is(parsed, -0), nonFinite: Number.isFinite(parsed) ? null : String(parsed) });
  }
  // A JSON.parse '__proto__' key is own data, never prototype mutation.
  const prototypeCase = JSON.parse(input.cases.find(test => test.caseId === 'property-05').json);
  assert.equal(Object.getPrototypeOf(prototypeCase), Object.prototype); assert(Object.hasOwn(prototypeCase, '__proto__'));
  assert.equal(prototypeCase.__proto__, null); assert.equal(Object.prototype.inert, undefined);
  const zeroHex = numberBits(-0); assert.equal(zeroHex, negativeMask.toString(16));
  return { schemaVersion: 1, fixtureId: input.fixtureId, kind: 'genuine-node-builtin-json-parse-stringify-observations',
    cases, numericSemantics, checks: { exactSerializedStringsRetained: true, inputJsonTextRetained: true,
      runtimeNumbersObservedByDataView: true, prototypeKeyIsOwnInertData: true, noUpstreamImports: true,
      noNativeProjectionExecutionOrParityClaim: true } };
}
