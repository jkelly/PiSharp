// Original Hello preparation/execution through the unchanged qualified full-duplex peer.
import assert from 'node:assert/strict';
import { startPeer, strictJson, json } from '../NodeBridge/wire.mjs';
import { createHelloLoader } from './module-loader.mjs';
const allowed = new Set(['--worker-generation', '--session-generation', '--mode', '--repo', '--oracle', '--jiti', '--reference', '--hello-reference']);
const args = new Map(), argv = process.argv.slice(2);
for (let index = 0; index < argv.length; index += 2) { assert(allowed.has(argv[index]) && !args.has(argv[index]) && index + 1 < argv.length); args.set(argv[index], argv[index + 1]); }
assert.equal(args.size, 8); assert.equal(args.get('--mode'), 'normal');
const identity = text => { assert(/^[1-9][0-9]*$/u.test(text ?? '')); const value = Number(text); assert(Number.isSafeInteger(value)); return value; };
const worker = identity(args.get('--worker-generation')), session = identity(args.get('--session-generation'));
const roots = { repo: args.get('--repo'), oracle: args.get('--oracle'), jiti: args.get('--jiti'), reference: args.get('--reference'), helloReference: args.get('--hello-reference') };
const operations = new Map(); let retainedBytes = 0, source, owner, generation, loaded = false, finalized = false;
const bounded = value => { assert(Buffer.byteLength(JSON.stringify(value)) <= 524288, 'Hello bridge response budget'); return json(value); };
const text = (value, maximum = 128) => { assert(typeof value === 'string' && value.length > 0 && value.length <= maximum); return value; };
const exact = (value, keys) => { assert(value && typeof value === 'object' && !Array.isArray(value)); assert.deepEqual(Object.keys(value).sort(), [...keys].sort()); };
const argumentsValue = raw => { text(raw, 262144); assert(Buffer.byteLength(raw) <= 262144, 'Hello arguments byte limit'); return strictJson(raw); };
function admit(value, kind) {
  assert(source && loaded && !finalized && value.ownerId === owner && value.ownerGeneration === generation && value.callbackId === 'hello-callback-1');
  const operationId = text(value.operationId); assert(!operations.has(operationId) && operations.size < 16, 'Hello operation admission');
  let done; const settled = new Promise(resolve => { done = resolve; });
  const operation = { kind, settled, done, status: 'running' }; operations.set(operationId, operation); return operation;
}
function remember(operation, observation) {
  const bytes = Buffer.byteLength(JSON.stringify(observation)); assert(bytes <= 524000 && retainedBytes + bytes <= 8388608, 'Hello retained observation budget');
  operation.observation = observation; operation.bytes = bytes; retainedBytes += bytes;
}
const peer = await startPeer(worker, session, async (message, signal, hostCall) => {
  if (message.method === 'probe.runtime') { assert.equal(message.value.presence, 'absent'); return json({ version: process.version, platform: process.platform, architecture: process.arch, pid: process.pid, parentPid: process.ppid, cwd: process.cwd(), environment: { ...process.env } }); }
  if (message.method === 'probe.ping') return json({ alive: true });
  assert.equal(message.value.presence, 'json'); const value = message.value.data;
  switch (message.method) {
    case 'hello.load': {
      exact(value, ['ownerId', 'ownerGeneration', 'cwd']); assert(!source && message.handle === undefined);
      owner = text(value.ownerId); generation = identity(String(value.ownerGeneration));
      try { source = await createHelloLoader(roots); const result = await source.load(text(value.cwd, 4096)); loaded = true; return bounded(result); }
      catch (error) { return bounded({ status: 'rejected', thrown: { name: error?.name, message: error?.message, stack: error?.stack, ...(source ? { observed: source.observe(error) } : {}) } }); }
    }
    case 'hello.prepare': {
      exact(value, ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', 'argumentsJson']); assert(message.handle === undefined, 'Preparation has no host callback handle');
      const input = argumentsValue(value.argumentsJson), operation = admit(value, 'prepare');
      try { signal.throwIfAborted(); const result = source.prepare(input); remember(operation, result); operation.status = result.status; signal.throwIfAborted(); return bounded(result); }
      finally { if (operation.status === 'running') operation.status = 'failed'; operation.done(); }
    }
    case 'hello.execute': {
      exact(value, ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', 'toolCallId', 'argumentsJson']);
      assert(message.handle && message.handle.ownerId === owner && message.handle.ownerGeneration === generation);
      const parameters = argumentsValue(value.argumentsJson), toolCallId = text(value.toolCallId, 65536), operation = admit(value, 'execute');
      let updates = 0, updateBytes = 0, updateFailure; let pending = Promise.resolve(); const updateObservations = [];
      const onUpdate = update => {
        signal.throwIfAborted(); assert(++updates <= 16, 'Hello progress count');
        const raw = JSON.stringify(update); assert(typeof raw === 'string' && (updateBytes += Buffer.byteLength(raw)) <= 262144, 'Hello progress byte budget');
        updateObservations.push(source.observe(update));
        // Original callback remains synchronous; exact native delivery receipts run in order and join settlement.
        pending = pending.then(() => hostCall('tool.update', json({ operationId: value.operationId, toolCallId, partialResultJson: raw }), message.handle, signal));
        pending.catch(error => { updateFailure ??= error; });
      };
      try {
        signal.throwIfAborted(); const result = await source.execute(toolCallId, parameters, signal, onUpdate);
        try { await pending; } catch (error) { updateFailure ??= error; }
        const observation = { ...result, updates: updateObservations, updateDeliveryJoined: true, ...(updateFailure ? { updateFailure: { name: updateFailure?.name, message: updateFailure?.message, stack: updateFailure?.stack } } : {}) };
        remember(operation, observation); operation.status = updateFailure ? 'publication-failed' : result.status;
        signal.throwIfAborted(); if (updateFailure) throw updateFailure; return bounded(observation);
      } finally { await pending.catch(() => {}); if (operation.status === 'running') operation.status = 'failed'; operation.done(); }
    }
    case 'hello.settle': {
      exact(value, ['operationId']); assert(message.handle === undefined); const operation = operations.get(text(value.operationId)); assert(operation, 'Unknown Hello operation');
      await operation.settled; const result = bounded({ settled: true, kind: operation.kind, status: operation.status, ...(operation.observation ? { observation: operation.observation } : {}) });
      operations.delete(value.operationId); retainedBytes -= operation.bytes ?? 0; return result;
    }
    case 'hello.finalize': { exact(value, []); assert(source && operations.size === 0 && message.handle === undefined); finalized = true; return bounded(await source.finalize()); }
    default: throw Error('Unsupported Hello bridge method');
  }
}, async () => { assert([...operations.values()].every(operation => operation.status !== 'running'), 'Hello shutdown has live source operations'); operations.clear(); retainedBytes = 0; if (source && !finalized) await source.finalize(); });
await peer.stopped;
