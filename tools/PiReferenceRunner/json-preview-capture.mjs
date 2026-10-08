import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const fixture = parseJsonSupported(readFileSync(process.argv[2], 'utf8'));
const { parseStreamingJson } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/json-parse.ts')).href);
assert.equal(new Set(fixture.cases.map(test => test.caseId)).size, fixture.cases.length, 'Case IDs must be unique');
const cases = fixture.cases.map(test => {
  assert.ok(test.input.kind === 'string' || test.input.kind === 'undefined');
  if (test.input.kind === 'string') assert.equal(typeof test.input.text, 'string');
  const input = test.input.kind === 'undefined' ? undefined : test.input.text;
  let strictRuntimeInputCheck;
  if (input === undefined) strictRuntimeInputCheck = { status: 'undefined-input' };
  else {
    try {
      parseJsonSupported(input);
      strictRuntimeInputCheck = { status: 'accepted' };
    } catch (error) {
      if (error instanceof RangeError) strictRuntimeInputCheck = { status: 'unsupported-runtime-precision', reason: error.message };
      else if (error instanceof SyntaxError) strictRuntimeInputCheck = { status: 'incomplete-or-invalid-json' };
      else throw error;
    }
  }
  const result = parseStreamingJson(input);
  const resultKind = result === null ? 'null' : Array.isArray(result) ? 'array' : typeof result;
  assert.ok(['null', 'array', 'object', 'string', 'number', 'boolean'].includes(resultKind), 'Unexpected preview result type');
  const observation = { caseId: test.caseId, category: test.category, input: structuredClone(test.input), strictRuntimeInputCheck, resultKind };
  // JSON.stringify would silently turn Infinity into null and -0 into 0.
  // Record these observed runtime-only scalars explicitly instead of coercing.
  if (typeof result === 'number' && (!Number.isFinite(result) || Object.is(result, -0))) {
    observation.runtimeOnlyNumber = { representation: Object.is(result, -0) ? '-0' : String(result), jsonResultAvailable: false };
  } else {
    const serialized = JSON.stringify(result);
    assert.notEqual(serialized, undefined);
    observation.result = structuredClone(parseJsonSupported(serialized));
  }
  return observation;
});
console.log(JSON.stringify({ observations: { cases, effects: [], checks: {
  capturedCaseCount: cases.length,
  categories: [...new Set(cases.map(test => test.category))].sort(),
  unsupportedStrictPrecisionCases: cases.filter(test => test.strictRuntimeInputCheck.status === 'unsupported-runtime-precision').map(test => test.caseId),
  runtimeOnlyNumberCases: cases.filter(test => test.runtimeOnlyNumber !== undefined).map(test => test.caseId),
  resultKinds: [...new Set(cases.map(test => test.resultKind))].sort(),
  numericParityQualified: false,
} }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }));
