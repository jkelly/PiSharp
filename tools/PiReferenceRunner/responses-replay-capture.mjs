import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const fixture = parseJsonSupported(readFileSync(process.argv[2], 'utf8'));
const { convertResponsesMessages } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/api/openai-responses-shared.ts')).href);
assert.equal(new Set(fixture.cases.map(test => test.caseId)).size, fixture.cases.length, 'Case IDs must be unique');
const cases = [];
for (const test of fixture.cases) {
  assert.ok(Object.hasOwn(fixture.contexts, test.contextRef), 'Unknown context reference');
  const context = structuredClone(fixture.contexts[test.contextRef]);
  const model = { ...structuredClone(fixture.baseModel), ...structuredClone(test.targetModel) };
  const options = structuredClone(test.options);
  const before = structuredClone({ context, model, options });
  let requestInput;
  let completion;
  const undefinedProjectionProperties = [];
  try {
    const projected = convertResponsesMessages(model, context, new Set(test.allowedToolCallProviders), options);
    // Preserve own undefined fields separately: JSON wire serialization omits
    // these properties, which differ from explicit null in both observations.
    function observeUndefined(value, path) {
      if (value === null || typeof value !== 'object') return;
      for (const [key, child] of Object.entries(value)) {
        const childPath = `${path}/${key.replaceAll('~', '~0').replaceAll('/', '~1')}`;
        if (child === undefined) undefinedProjectionProperties.push(childPath);
        else observeUndefined(child, childPath);
      }
    }
    observeUndefined(projected, '');
    requestInput = structuredClone(parseJsonSupported(JSON.stringify(projected)));
    completion = { status: 'returned' };
  } catch (error) {
    completion = { status: 'threw', error: { name: error.name, message: error.message } };
  }
  assert.deepEqual({ context, model, options }, before, 'Request projection must leave caller history/configuration unchanged');
  cases.push({ caseId: test.caseId, contextRef: test.contextRef, ...(requestInput === undefined ? {} : { requestInput }), undefinedProjectionProperties, inputUnchanged: true, completion });
}
console.log(JSON.stringify({ observations: { cases, effects: [], checks: {
  capturedCaseCount: cases.length,
  authoredContextCount: Object.keys(fixture.contexts).length,
  returnedCases: cases.filter(test => test.completion.status === 'returned').map(test => test.caseId),
  thrownCases: cases.filter(test => test.completion.status === 'threw').map(test => test.caseId),
  requestItemCounts: cases.filter(test => test.requestInput !== undefined).map(test => ({ caseId: test.caseId, count: test.requestInput.length })),
  allInputsUnchanged: cases.every(test => test.inputUnchanged),
  sourceSeam: 'Unchanged convertResponsesMessages with explicit model/transcript/provider set/options',
  fullPayloadQualified: false,
  orphanSynthesisQualified: false,
} }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }));
