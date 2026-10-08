// Genuine whole unchanged Anthropic stream + real SDK request handling.
// Authored fake fetch/SSE and child Date.now are explicit environment seams.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { registerHooks } from 'node:module';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const inside = (root, path) => { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); };
export function ownUndefinedPaths(value, path = '', result = []) {
  if (value && typeof value === 'object') for (const key of Object.keys(value)) {
    const child = path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1');
    if (value[key] === undefined) result.push(child); else ownUndefinedPaths(value[key], child, result);
  }
  return result;
}
export function applyAuthoredNumberConversions(context, specification) {
  if (!specification) return [];
  const call = context.messages[specification.messageIndex]?.content?.[specification.contentIndex];
  assert(call?.type === 'toolCall' && call.arguments && typeof call.arguments === 'object' && !Array.isArray(call.arguments), 'Numeric seam must target an authored tool arguments object');
  const seen = new Set();
  return specification.fields.map(({ field, lexeme }) => {
    assert(typeof field === 'string' && !seen.has(field) && !['__proto__', 'prototype', 'constructor'].includes(field)); seen.add(field);
    assert(typeof lexeme === 'string' && /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(lexeme), 'Numeric seam requires a declared JSON-number lexeme string');
    const number = Number(lexeme); assert(Number.isFinite(number), 'Initial corpus admits only finite authored JS numbers');
    Object.defineProperty(call.arguments, field, { value: number, enumerable: true, configurable: true, writable: true });
    return { field, authoredLexeme: lexeme, mechanism: 'Number(authoredLexeme) before unchanged public stream', numberToString: String(number), jsonStringifyNumber: JSON.stringify(number), negativeZero: Object.is(number, -0), finite: Number.isFinite(number) };
  });
}
export async function captureAnthropicSdk(oracle, fixture, admittedPackages) {
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Existing locked observer/offline preload required');
  const allowed = url => {
    if (!url.startsWith('file:')) return url.startsWith('node:');
    const path = fileURLToPath(url);
    return inside(resolve(oracle, 'upstream'), path) || admittedPackages.some(name => inside(resolve(oracle, 'node_modules', name), path));
  };
  // Forward the original resolution result; reject ancestor/global/module fallback.
  registerHooks({ resolve(specifier, context, nextResolve) { const result = nextResolve(specifier, context); assert(allowed(result.url), 'Unreviewed source/dependency resolution rejected: ' + result.url); return result; } });
  const originalNow = Date.now;
  Date.now = () => fixture.clock.unixMilliseconds;
  try {
    const { stream } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/api/anthropic-messages.ts')).href);
    assert.equal(typeof stream, 'function', 'Whole unchanged public stream export missing');
    const cases = [];
    for (const test of fixture.cases) {
      const model = { ...structuredClone(fixture.model), ...structuredClone(test.modelOverrides ?? {}) };
      const context = structuredClone(test.context), numberConversions = applyAuthoredNumberConversions(context, test.numberConversions);
      const payloadSnapshots = [], fetchRequests = [], responseHooks = [], providerEvents = [], emissionSnapshots = [], emissionOwnUndefined = [], drainedFrames = [], trace = [];
      const options = { ...structuredClone(fixture.commonOptions), ...structuredClone(test.options),
        async onPayload(payload, actualModel) { assert.equal(actualModel, model); trace.push('onPayload'); payloadSnapshots.push({ params: structuredClone(payload), ownUndefinedPaths: ownUndefinedPaths(payload) }); return undefined; },
        async onResponse(response, actualModel) { assert.equal(actualModel, model); trace.push('onResponse'); responseHooks.push({ response: structuredClone(response), ownUndefinedPaths: ownUndefinedPaths(response) }); },
        async onProviderStreamEvent(event, actualModel) { assert.equal(actualModel, model); trace.push('provider:' + event.type); providerEvents.push(structuredClone(event)); },
        async fetch(input, init) {
          trace.push('fetch'); assert.equal(fetchRequests.length, 0, 'Retries or multiple requests outside first profile');
          const request = new Request(input, init);
          assert.equal(request.url, model.baseUrl + '/v1/messages?beta=true', 'Only authored fake endpoint is admitted');
          assert.equal(request.method, 'POST'); assert.equal(typeof init?.body, 'string', 'Actual SDK raw JSON body expected');
          const body = init.body;
          fetchRequests.push({ url: request.url, method: request.method, headers: [...request.headers.entries()], rawBody: body, rawBodyUtf8Bytes: Buffer.byteLength(body), rawBodyUtf8Sha256: hash(Buffer.from(body)), fetchInputType: typeof input, initOwnKeys: Object.keys(init), initHeadersKind: init.headers?.constructor?.name ?? null, signalAborted: request.signal.aborted });
          const bytes = Buffer.from(fixture.response.sseText, 'utf8'); let offset = 0;
          const wire = new ReadableStream({ pull(controller) { if (offset === bytes.length) { controller.close(); return; } const end = Math.min(bytes.length, offset + fixture.response.chunkBytes); controller.enqueue(new Uint8Array(bytes.subarray(offset, end))); offset = end; } });
          return new Response(wire, { status: fixture.response.status, headers: fixture.response.headers });
        }
      };
      assert.equal(options.maxRetries, 0); assert.equal(options.apiKey, 'pisharp-authored-inert-key-noncredential'); assert(!Object.hasOwn(options, 'client'));
      const resultStream = stream(model, context, options), push = resultStream.push.bind(resultStream);
      // Observation tap on this returned instance: capture before shared output mutates.
      // No source bytes, provider implementation, client or SDK module are replaced.
      resultStream.push = event => { trace.push('emit:' + event.type); emissionOwnUndefined.push({ emissionIndex: emissionSnapshots.length, paths: ownUndefinedPaths(event) }); emissionSnapshots.push(structuredClone(event)); push(event); };
      for await (const event of resultStream) drainedFrames.push(structuredClone(event));
      const result = await resultStream.result(), finalResult = structuredClone(result), finalOwnUndefined = ownUndefinedPaths(result);
      assert.equal(payloadSnapshots.length, 1); assert(fetchRequests.length <= 1);
      const terminal = emissionSnapshots.at(-1); assert(terminal && ['done', 'error'].includes(terminal.type), 'Source stream must settle');
      if (test.caseId === 'defaults-model-max') {
        assert.equal(fetchRequests.length, 1, 'First case must prove actual SDK request'); assert.equal(responseHooks.length, 1); assert.equal(terminal.type, 'done');
      }
      cases.push({ caseId: test.caseId, numberConversions, payloadSnapshots, fetchRequests, responseHooks, providerEvents, emissionSnapshots, emissionOwnUndefined, drainedFrames, finalResult, finalOwnUndefined, trace });
    }
    return { observations: { cases, responseWire: { utf8Sha256: hash(Buffer.from(fixture.response.sseText)), bytes: Buffer.byteLength(fixture.response.sseText), chunkBytes: fixture.response.chunkBytes }, checks: { caseCount: cases.length, fakeFetchCalls: cases.reduce((count, item) => count + item.fetchRequests.length, 0), sourceSeam: 'Unchanged exported stream; real SDK beta.messages.create/asResponse; unchanged Pi SSE parser; supported fake fetch/callbacks', sdkStreamingDecoderQualified: false, noSourceTransformOrSdkShim: true, childDateNowOverride: fixture.clock, actualHeadersRetained: true, networkAndProcessesBlocked: true } }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) };
  } finally { Date.now = originalNow; }
}
