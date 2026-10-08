import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const fixture = parseJsonSupported(readFileSync(process.argv[2], 'utf8'));
const { processResponsesStream } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/api/openai-responses-shared.ts')).href);
const { AssistantMessageEventStream } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/event-stream.ts')).href);
assert.equal(new Set(fixture.cases.map(test => test.caseId)).size, fixture.cases.length, 'Case IDs must be unique');
const cases = [];
for (const test of fixture.cases) {
  const output = structuredClone(fixture.initialOutput);
  const model = structuredClone(fixture.model);
  const stream = new AssistantMessageEventStream();
  const providerEvents = [];
  const emissionSnapshots = [];
  const push = stream.push.bind(stream);
  // Observe only this explicitly supplied stream instance; forward every event.
  stream.push = event => {
    emissionSnapshots.push(structuredClone(event));
    push(event);
  };
  async function* wire() {
    for (const event of test.events) yield structuredClone(event);
  }
  let completion;
  try {
    await processResponsesStream(wire(), output, stream, model, {
      onProviderStreamEvent: async (event, eventModel) => {
        assert.equal(eventModel, model);
        providerEvents.push(structuredClone(event));
      },
    });
    completion = { status: 'returned' };
  } catch (error) {
    completion = { status: 'threw', error: { name: error.name, message: error.message } };
  } finally {
    // Harness cleanup only: this helper emits no wrapper terminal event.
    stream.end();
  }
  assert.equal(providerEvents.length, test.events.length, 'Every parsed wire event must reach the genuine observation hook');
  cases.push({ caseId: test.caseId, providerEvents, emissionSnapshots, finalOutput: structuredClone(output), completion });
}
console.log(JSON.stringify({ observations: { cases, effects: [], checks: {
  capturedCaseCount: cases.length,
  parsedWireEventCount: cases.reduce((sum, test) => sum + test.providerEvents.length, 0),
  normalizedEmissionCount: cases.reduce((sum, test) => sum + test.emissionSnapshots.length, 0),
  returnedCases: cases.filter(test => test.completion.status === 'returned').map(test => test.caseId),
  thrownCases: cases.filter(test => test.completion.status === 'threw').map(test => test.caseId),
  emittedStartCount: cases.reduce((sum, test) => sum + test.emissionSnapshots.filter(event => event.type === 'start').length, 0),
  emittedTerminalCount: cases.reduce((sum, test) => sum + test.emissionSnapshots.filter(event => event.type === 'done' || event.type === 'error').length, 0),
  sourceSeam: 'Unchanged processResponsesStream with explicit parsed events/output/stream/model/options',
} }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }));
