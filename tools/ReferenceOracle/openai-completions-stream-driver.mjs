// Observes the unchanged public exported stream and actual OpenAI SDK.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const packages = ['openai', 'partial-json', 'typebox'];
function inside(root, path) {
  const suffix = relative(resolve(root), resolve(path));
  return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep);
}
function undefinedPaths(value, path = '', result = []) {
  if (value && typeof value === 'object') for (const key of Object.keys(value)) {
    const child = path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1');
    if (value[key] === undefined) result.push(child); else undefinedPaths(value[key], child, result);
  }
  return result;
}
function snapshot(value) {
  return { value: structuredClone(value), ownUndefinedPaths: undefinedPaths(value) };
}

assert.equal(process.argv.length, 2, 'Driver accepts no arguments');
assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Locked full offline preload is required');
registerHooks({ resolve(specifier, context, nextResolve) {
  const result = nextResolve(specifier, context);
  const allowed = result.url.startsWith('node:') || (result.url.startsWith('file:') && (
    inside(join(oracle, 'upstream'), fileURLToPath(result.url)) ||
    packages.some(name => inside(join(oracle, 'node_modules', name), fileURLToPath(result.url)))));
  assert(allowed, 'Unqualified source/dependency resolution rejected: ' + result.url);
  return result;
} });

const fixture = JSON.parse(readFileSync(join(repo, 'fixtures/reference/openai-completions-stream/input.json'), 'utf8'));
assert.equal(fixture.schemaVersion, 1);
assert.equal(fixture.cases.length, 3);
const originalNow = Date.now;
Date.now = () => fixture.clock.unixMilliseconds;
try {
  const { stream } = await import(pathToFileURL(join(oracle, 'upstream/packages/ai/src/api/openai-completions.ts')).href);
  const cases = [];
  for (const test of fixture.cases) {
    assert(test.wireChunks.length <= 16, 'Authored wire bound');
    const model = { ...structuredClone(fixture.model), ...structuredClone(test.modelOverrides ?? {}) };
    const context = structuredClone(test.context);
    const sseText = fixture.response.prefixComment + test.wireChunks.map(chunk =>
      'data: ' + JSON.stringify(chunk) + fixture.response.lineEnding.repeat(2)).join('') + fixture.response.suffix;
    const bytes = Buffer.from(sseText, 'utf8');
    assert(bytes.length < 65536, 'Authored SSE byte bound');
    const payloadSnapshots = [], fetchRequests = [], responseHooks = [], providerEvents = [];
    const emissionSnapshots = [], drainedFrames = [], trace = [];
    const options = { ...structuredClone(fixture.commonOptions), ...structuredClone(test.options),
      async onPayload(payload, actualModel) {
        assert.equal(actualModel, model); trace.push('onPayload');
        payloadSnapshots.push(snapshot(payload)); return undefined;
      },
      async onResponse(response, actualModel) {
        assert.equal(actualModel, model); trace.push('onResponse'); responseHooks.push(snapshot(response));
      },
      async onProviderStreamEvent(chunk, actualModel) {
        assert.equal(actualModel, model); trace.push('provider:' + providerEvents.length); providerEvents.push(snapshot(chunk));
      },
      async fetch(input, init) {
        trace.push('fetch'); assert.equal(fetchRequests.length, 0, 'One actual SDK request per case');
        const request = new Request(input, init);
        assert.equal(request.url, fixture.model.baseUrl + '/chat/completions');
        assert.equal(request.method, 'POST'); assert.equal(typeof init?.body, 'string');
        fetchRequests.push({ url: request.url, method: request.method, headers: [...request.headers.entries()],
          body: init.body, bodyUtf8Sha256: hash(Buffer.from(init.body)), bodyJson: JSON.parse(init.body),
          initOwnKeys: Object.keys(init), signalAborted: request.signal.aborted });
        let offset = 0;
        const wire = new ReadableStream({ pull(controller) {
          if (offset === bytes.length) { controller.close(); return; }
          const end = Math.min(bytes.length, offset + fixture.response.chunkBytes);
          controller.enqueue(new Uint8Array(bytes.subarray(offset, end))); offset = end;
        } });
        return new Response(wire, { status: fixture.response.status, headers: fixture.response.headers });
      }
    };
    const resultStream = stream(model, context, options), originalPush = resultStream.push.bind(resultStream);
    // Instance observation only; capture before shared source partials mutate.
    resultStream.push = event => { trace.push('emit:' + event.type); emissionSnapshots.push(snapshot(event)); originalPush(event); };
    for await (const event of resultStream) drainedFrames.push(snapshot(event));
    const finalResult = snapshot(await resultStream.result());
    assert.equal(payloadSnapshots.length, 1); assert.equal(fetchRequests.length, 1); assert.equal(responseHooks.length, 1);
    assert.equal(providerEvents.length, test.wireChunks.length);
    assert.equal(finalResult.value.stopReason, test.expectedStopReason);
    assert.equal(emissionSnapshots[0].value.type, 'start'); assert.equal(emissionSnapshots.at(-1).value.type, 'done');
    assert.deepEqual(providerEvents.map(row => row.value), test.wireChunks, 'Complete actual SDK DTOs must match authored wire objects');
    cases.push({ caseId: test.caseId, payloadSnapshots, fetchRequests, responseHooks, providerEvents,
      emissionSnapshots, drainedFrames, finalResult, trace,
      responseWire: { sseText, utf8Sha256: hash(bytes), bytes: bytes.length, chunkBytes: fixture.response.chunkBytes } });
  }
  const observations = { cases, checks: { caseCount: cases.length,
    fakeFetchCalls: cases.reduce((sum, test) => sum + test.fetchRequests.length, 0),
    sourceSeam: 'Unchanged exported stream; private createClient/buildParams/chunk loop; real OpenAI SDK create/serialize/SSE decode; supported options.fetch',
    fakeClock: 'Date.now replaced and restored; no RNG replacement',
    noSourceTransformOrSdkShim: true, networkAndProcessesBlocked: true,
    noNativeImportOrDifferentialClaim: true } };
  console.log(JSON.stringify({ observations, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()]
    .sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }, null, 2));
} finally { Date.now = originalNow; }
