// Actual native bridge peer for ONE unchanged pinned example. Not an upstream Agent engine.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { startPeer, json, absent } from './wire.mjs';
import { createLoader } from './module-loader.mjs';
const allowed = new Set(['--worker-generation', '--session-generation', '--mode', '--repo', '--oracle', '--jiti', '--reference']);
const args = new Map(), argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i += 2) { assert(allowed.has(argv[i]) && !args.has(argv[i]) && i + 1 < argv.length); args.set(argv[i], argv[i + 1]); }
assert.equal(args.size, 7); assert.equal(args.get('--mode'), 'normal');
const identity = s => { assert(/^[1-9][0-9]*$/u.test(s ?? '')); const n = Number(s); assert(Number.isSafeInteger(n)); return n; };
const worker = identity(args.get('--worker-generation')), session = identity(args.get('--session-generation'));
const roots = Object.fromEntries(['repo', 'oracle', 'jiti', 'reference'].map(n => [n, args.get('--' + n)]));
const operations = new Map(); let retainedFailureBytes = 0, source, owner, generation, loaded = false, finalized = false;
const boundedJson = value => { assert(Buffer.byteLength(JSON.stringify(value)) <= 524288, 'Bridge response byte budget'); return json(value); };
const errorText = value => { const text = String(value), bytes = Buffer.from(text); let prefix = text.slice(0, 8192);
  if (prefix.length && prefix.charCodeAt(prefix.length - 1) >= 0xd800 && prefix.charCodeAt(prefix.length - 1) <= 0xdbff) prefix = prefix.slice(0, -1);
  return { utf8Bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex'), prefix, complete: prefix === text }; };
const text = (v, max = 128) => { assert(typeof v === 'string' && v.length > 0 && v.length <= max); return v; };
const object = v => { assert(v && typeof v === 'object' && !Array.isArray(v)); return v; };
const exact = (v, keys) => { object(v); assert.deepEqual(Object.keys(v).sort(), [...keys].sort()); };
const peer = await startPeer(worker, session, async (message, signal, hostCall) => {
  if (message.method === 'probe.runtime') { assert.equal(message.value.presence, 'absent'); return json({ version: process.version, platform: process.platform, architecture: process.arch, pid: process.pid, parentPid: process.ppid, cwd: process.cwd(), environment: { ...process.env } }); }
  if (message.method === 'probe.ping') return json({ alive: true });
  assert.equal(message.value.presence, 'json'); const value = message.value.data;
  switch (message.method) {
    case 'extension.load': {
      exact(value, ['ownerId', 'ownerGeneration', 'mode']); assert(!source); owner = text(value.ownerId); generation = identity(String(value.ownerGeneration));
      assert(['actual', 'async-failure', 'unsupported'].includes(value.mode)); source = await createLoader(roots);
      try { const result = await source.load(value.mode); loaded = !result.pending; return boundedJson(result); }
      catch (error) { return json({ status: 'rejected', code: error.bridgeCode ?? 'FactoryLoadFailed', message: String(error?.message ?? error),
        thrown: source.observe(error), name: error?.name, stack: error?.stack }); }
    }
    case 'extension.release-factory': assert(source && !loaded); return json(await source.release());
    case 'extension.invoke': {
      exact(value, ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', 'event', 'hasUI']);
      assert(Buffer.byteLength(JSON.stringify(value)) <= 262144, 'Bridge invocation byte budget');
      assert(source && loaded && !finalized && value.ownerId === owner && value.ownerGeneration === generation && value.callbackId === 'protected-paths-1');
      const operationId = text(value.operationId); assert(!operations.has(operationId) && operations.size < 16 && typeof value.hasUI === 'boolean');
      assert(message.handle && message.handle.ownerId === owner && message.handle.ownerGeneration === generation);
      const event = structuredClone(object(value.event)), before = source.observe(event);
      let done; const settled = new Promise(r => { done = r; }); const operation = { settled, status: 'running' }; operations.set(operationId, operation);
      const pending = [], notifications = []; let observedReturn;
      const unavailable = name => () => { throw Error('Unsupported host operation: ' + String(name)); };
      const ui = new Proxy(Object.freeze({ notify(messageText, kind) { assert(value.hasUI && typeof messageText === 'string' && messageText.length <= 65536 && ['info', 'warning', 'error'].includes(kind));
        assert(pending.length < 16); notifications.push([messageText, kind]); const task = hostCall('ui.notify', json({ operationId, message: messageText, kind }), message.handle, signal);
        task.catch(() => {}); pending.push(task); // Source notify remains synchronous. Native publication is joined below.
      } }), { get(target, key) { return Object.hasOwn(target, key) ? target[key] : unavailable(key); } });
      const context = new Proxy(Object.freeze({ hasUI: value.hasUI, ui }), { get(target, key) { if (Object.hasOwn(target, key)) return target[key]; throw Error('Unsupported context getter: ' + String(key)); } });
      try {
        signal.throwIfAborted(); const result = await source.invoke(event, context); const returned = source.observe(result); observedReturn = returned;
        const joins = await Promise.allSettled(pending); if (joins.some(r => r.status === 'rejected')) throw Error('NativePublicationFailed'); signal.throwIfAborted();
        operation.status = 'fulfilled';
        return boundedJson({ resultPresence: result === undefined ? 'undefined' : 'json', ...(result === undefined ? {} : { result }),
          inputBefore: before, inputAfter: source.observe(event), returned, notifications, publicationJoined: true });
      } catch (error) { const failure = { thrown: source.observe(error), name: error?.name, message: error?.message, stack: error?.stack,
          inputBefore: before, inputAfter: source.observe(event), notifications, ...(observedReturn ? { returned: observedReturn } : {}) };
        const bytes = Buffer.byteLength(JSON.stringify(failure)); assert(bytes <= 523264 && retainedFailureBytes + bytes <= 8388608, 'Bridge retained failure byte budget');
        operation.failure = failure; operation.failureBytes = bytes; retainedFailureBytes += bytes; throw error;
      } finally { await Promise.allSettled(pending); if (operation.status === 'running') operation.status = 'failed'; done(); }
    }
    case 'extension.settle': { exact(value, ['operationId']); const operation = operations.get(text(value.operationId)); assert(operation); await operation.settled;
      const result = boundedJson({ settled: true, status: operation.status, ...(operation.failure ? { failure: operation.failure } : {}) });
      operations.delete(value.operationId); retainedFailureBytes -= operation.failureBytes ?? 0; return result; }
    case 'extension.finalize': {
      assert(source && operations.size === 0); finalized = true; let responseUtf8Bytes, responseSha256, stage = 'source-finalize';
      try { const result = await source.finalize(); stage = 'encode-finalization-response'; const serialized = JSON.stringify(result);
        responseUtf8Bytes = Buffer.byteLength(serialized); responseSha256 = createHash('sha256').update(serialized).digest('hex'); return boundedJson(result); }
      catch (error) { let observedError, observationFailure; try { const report = source.observe(error), serialized = JSON.stringify(report);
          if (Buffer.byteLength(serialized) <= 131072) observedError = report;
          else observationFailure = { reason: 'Diagnostic observation byte budget', utf8Bytes: Buffer.byteLength(serialized), sha256: createHash('sha256').update(serialized).digest('hex') };
        } catch (failure) { observationFailure = errorText(failure?.message ?? failure); }
        return boundedJson({ status: 'failed', stage, diagnostics: source.finalizationDiagnostics(), ...(responseUtf8Bytes === undefined ? {} : { responseUtf8Bytes, responseSha256 }),
          error: { name: errorText(error?.name ?? typeof error), message: errorText(error?.message ?? error), stack: errorText(error?.stack ?? ''),
            ...(observedError ? { observed: observedError } : {}), ...(observationFailure ? { observationFailure } : {}) } }); }
    }
    default: throw Error('Unsupported bridge method');
  }
}, async () => { assert(operations.size === 0 || [...operations.values()].every(o => o.status !== 'running')); operations.clear(); retainedFailureBytes = 0;
  if (source && !finalized) await source.finalize(); });
await peer.stopped;
