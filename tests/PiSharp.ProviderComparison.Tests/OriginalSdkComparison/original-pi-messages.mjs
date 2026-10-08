// AUTHORED ONLY. Future admitted Node 22.19+ supports the actual original TS exports.
// Args: absolute input, absolute pin manifest, absolute upstream root, absolute partial-json root.
import assert from 'node:assert/strict';
import { readFileSync, realpathSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { resolve, isAbsolute } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import http from 'node:http'; import https from 'node:https'; import net from 'node:net';
import dns from 'node:dns'; import dgram from 'node:dgram'; import cp from 'node:child_process';
const [inputPath, manifestPath, upstream, partialJson] = process.argv.slice(2);
assert.equal(process.argv.length, 6);
assert([inputPath, manifestPath, upstream, partialJson].every(isAbsolute));
const denied = () => { throw new Error('No network or child process is admitted by this oracle'); };
globalThis.fetch = denied;
for (const m of [http, https]) { m.request = denied; m.get = denied; }
net.connect = denied; net.createConnection = denied; net.Socket.prototype.connect = denied;
dns.lookup = denied; dns.resolve = denied; dgram.createSocket = denied;
for (const name of ['exec','execFile','spawn','fork','execSync','execFileSync','spawnSync']) cp[name] = denied;
syncBuiltinESMExports();
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const bytes = readFileSync(inputPath); assert(bytes.length <= 65536);
const input = JSON.parse(bytes), manifest = JSON.parse(readFileSync(manifestPath));
assert.equal(input.originalCommit, 'd86654abb8862e201933517d6f1fce9f88dd117f');
assert.equal(manifest.originalCommit, input.originalCommit);
const allowed = new Map();
for (const pin of manifest.files) {
  const path = resolve(pin.root === 'upstream' ? upstream : partialJson, pin.path);
  assert.equal(realpathSync(path).toLowerCase(), path.toLowerCase(), 'no alternate physical closure');
  const source = readFileSync(path); assert.equal(source.length, pin.bytes); assert.equal(hash(source), pin.sha256);
  allowed.set(path.toLowerCase(), pin);
}
const loaded = [];
registerHooks({
  resolve(specifier, context, next) {
    const result = specifier === 'partial-json' ? { url: pathToFileURL(resolve(partialJson, 'dist/index.js')).href, shortCircuit: true } : next(specifier, context);
    assert(result.url.startsWith('node:') || (result.url.startsWith('file:') && allowed.has(fileURLToPath(result.url).toLowerCase())), 'module outside exact closure: ' + result.url);
    return result;
  },
  load(url, context, next) {
    if (url.startsWith('file:')) {
      const pin = allowed.get(fileURLToPath(url).toLowerCase()); assert(pin);
      assert.equal(hash(readFileSync(fileURLToPath(url))), pin.sha256); loaded.push(pin.path);
    }
    // Actual Node loader runs original unchanged TypeScript; no handwritten provider adapter.
    return next(url, context);
  }
});
const pi = await import(pathToFileURL(resolve(upstream, 'packages/ai/src/api/pi-messages.ts')).href);
const records = [], faults = [], cases = [];
function retain(name, original) { const row = { name, original, joined: false, direct: undefined }; records.push(row); return row; }
async function join(row) { row.joined = true; try { return await row.original; } catch (error) { row.direct = error; faults.push(error); throw error; } }
const copy = value => JSON.parse(JSON.stringify(value));
try {
  assert.deepEqual(input.cases, ['pi-messages.direct-settled-tool-alias','pi-messages.simple-settled-tool-alias']);
  for (const name of input.cases) {
    const requests = [], emissions = []; let start, end, deltaArguments;
    const fetch = (url, options) => {
      const original = (async () => {
        assert.equal(String(url), 'https://pi-messages.invalid/base/messages'); requests.push(JSON.parse(options.body));
        assert.equal(requests.length, 1);
        return new Response(input.events.map(e => 'data: ' + JSON.stringify(e) + '\n\n').join(''), { status: 200, headers: { 'content-type': 'text/event-stream' } });
      })(); retain(name + '.fetch', original); return original;
    };
    const actual = (name.includes('.simple-') ? pi.streamSimple : pi.stream)(input.model, input.context,
      { apiKey: 'inert-key', sessionId: 'inert-session', env: {}, fetch });
    const push = actual.push.bind(actual);
    actual.push = event => {
      emissions.push(copy(event));
      if (event.type === 'toolcall_start') start = event.partial.content[event.contentIndex];
      if (event.type === 'toolcall_delta') deltaArguments = event.partial.content[event.contentIndex].arguments;
      if (event.type === 'toolcall_end') end = event.toolCall;
      return push(event);
    };
    const result = retain(name + '.actual-result', actual.result());
    const drain = retain(name + '.drain', (async () => {
      let count = 0; for await (const event of actual) { assert(++count <= 16); if (event.type === 'error') throw new Error(event.error.errorMessage); }
    })());
    await join(drain); const terminal = await join(result);
    assert.equal(terminal.stopReason, 'toolUse'); assert.equal(start, end);
    assert.deepEqual(deltaArguments, { value: 1 }); assert.notEqual(deltaArguments, end.arguments);
    assert.deepEqual(end, { type: 'toolCall', id: 'final-call', name: 'other', arguments: { value: 7 } });
    assert.deepEqual(emissions.find(e => e.type === 'toolcall_start').partial.content[0].arguments, {});
    cases.push({ name, request: requests[0], finalCall: copy(end), settledStart: copy(start), sameStartEnd: start === end,
      deltaArgumentsAfterEnd: copy(deltaArguments), emissionStart: emissions.find(e => e.type === 'toolcall_start').partial.content[0] });
  }
} catch (error) { faults.push(error); }
finally {
  for (const row of records.filter(r => !r.joined)) { try { await join(row); } catch { /* raw direct reference retained */ } }
}
function graph(roots) {
  const ids = new Map(), queue = [], nodes = []; let edges = 0;
  function add(e) { if (!ids.has(e)) { assert(ids.size < 1024, 'raw faults retained on graph bound'); ids.set(e, ids.size); queue.push(e); } return ids.get(e); }
  for (const e of roots) add(e);
  while (queue.length) { const e = queue.shift(); const children = e instanceof AggregateError ? [...e.errors] : e?.cause !== undefined ? [e.cause] : [];
    edges += children.length; assert(edges <= 4096); nodes.push({ id: ids.get(e), type: e?.constructor?.name, message: String(e?.message ?? e), stack: e?.stack, children: children.map(add) }); }
  return nodes;
}
try {
  if (faults.length) throw new AggregateError(faults, 'Actual original stream failure');
  assert(records.every(r => r.joined));
  console.log(JSON.stringify({ schemaVersion: 1, implementation: 'original', originalCommit: input.originalCommit,
    inputSha256: hash(bytes), cases, loadedModules: [...new Set(loaded)], originals: records.map(r => ({ name: r.name, joined: r.joined })), faultGraph: graph(faults) }));
} catch (reportError) {
  // Preserve every original Promise and direct error if graph/serialize/console fails.
  const failure = new AggregateError([...new Set([...faults, reportError])], 'Original oracle/report failure');
  failure.originals = records; throw failure;
}
