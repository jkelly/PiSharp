// Authored protocol peer. No source implementation is copied or transformed here.
import { TextDecoder } from 'node:util';
const cap = 1_048_576, retainedCap = 8_388_608;
const scalar = s => { for (let i = 0; i < s.length; i++) { const n = s.charCodeAt(i); if (n >= 0xd800 && n <= 0xdbff) { const m = s.charCodeAt(++i); if (!(m >= 0xdc00 && m <= 0xdfff)) throw Error('InvalidUnicode'); } else if (n >= 0xdc00 && n <= 0xdfff) throw Error('InvalidUnicode'); } };
// Tokenize before JSON.parse: duplicate decoded keys, nonfinite numbers and unpaired Unicode fail closed.
export function strictJson(text) {
  let p = 0, nodes = 0;
  const ws = () => { while (' \t\r\n'.includes(text[p] ?? '\0')) p++; };
  const string = () => { const start = p++; for (;;) { const c = text[p++]; if (c === undefined || c.charCodeAt(0) < 32) throw Error('MalformedFrame'); if (c === '"') break; if (c === '\\') { const e = text[p++]; if (e === 'u') { if (!/^[0-9a-f]{4}$/iu.test(text.slice(p, p + 4))) throw Error('MalformedFrame'); p += 4; } else if (!'"\\/bfnrt'.includes(e ?? '\0')) throw Error('MalformedFrame'); } } const value = JSON.parse(text.slice(start, p)); scalar(value); return value; };
  const value = depth => { if (++nodes > 65536 || depth > 32) throw Error('ValueLimit'); ws(); const c = text[p];
    if (c === '"') { string(); return; }
    if (c === '{') { p++; ws(); const names = new Set(); if (text[p] === '}') { p++; return; } for (;;) { ws(); if (text[p] !== '"') throw Error('MalformedFrame'); const key = string(); if (names.has(key)) throw Error('DuplicateProperty'); names.add(key); ws(); if (text[p++] !== ':') throw Error('MalformedFrame'); value(depth + 1); ws(); const e = text[p++]; if (e === '}') return; if (e !== ',') throw Error('MalformedFrame'); } }
    if (c === '[') { p++; ws(); if (text[p] === ']') { p++; return; } for (;;) { value(depth + 1); ws(); const e = text[p++]; if (e === ']') return; if (e !== ',') throw Error('MalformedFrame'); } }
    for (const literal of ['true', 'false', 'null']) if (text.startsWith(literal, p)) { p += literal.length; return; }
    const match = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/u.exec(text.slice(p)); if (!match || match.index !== 0 || !Number.isFinite(Number(match[0]))) throw Error('MalformedFrame'); p += match[0].length;
  }; value(0); ws(); if (p !== text.length) throw Error('MalformedFrame'); return JSON.parse(text);
}
export const json = data => ({ presence: 'json', data });
export const absent = Object.freeze({ presence: 'absent' });
const identifier = value => typeof value === 'string' && /^[a-zA-Z0-9._-]{1,128}$/u.test(value);
const id = value => Number.isSafeInteger(value) && value > 0;
const fields = (value, expected) => { if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(k => !expected.includes(k))) throw Error('InvalidEnvelope'); };
function tagged(value) { fields(value, ['presence', 'data']); if (!['absent', 'undefined', 'json'].includes(value.presence) || (value.presence === 'json') !== Object.hasOwn(value, 'data')) throw Error('InvalidValue'); }
export async function startPeer(worker, session, handler, shutdown) {
  const features = ['requests', 'callbacks', 'cancel', 'tagged-values'];
  const pending = new Map(), active = new Map(), writes = new Set();
  let nextId = 0, lastIncoming = 0, negotiated = false, closing = false, buffered = Buffer.alloc(0), held = 0;
  let stoppedResolve; const stopped = new Promise(r => { stoppedResolve = r; });
  const base = kind => ({ version: 1, kind, workerGeneration: worker, sessionGeneration: session });
  const send = message => { const bytes = Buffer.from(JSON.stringify(message) + '\n'); if (bytes.length - 1 > cap || writes.size >= 32 || held + bytes.length > retainedCap) throw Error('WriteLimit'); held += bytes.length;
    const task = new Promise((resolve, reject) => process.stdout.write(bytes, e => e ? reject(e) : resolve())); writes.add(task);
    void task.then(() => { held -= bytes.length; writes.delete(task); }, () => { held -= bytes.length; writes.delete(task); }); return task; };
  const hostCall = (method, value, handle, signal) => {
    if (closing || !identifier(method) || !handle || pending.size >= 32 || nextId === Number.MAX_SAFE_INTEGER) throw Error('CallbackLimit');
    const requestId = ++nextId; let resolve, reject; const result = new Promise((a, b) => { resolve = a; reject = b; });
    const cancel = () => { void send({ ...base('cancel'), id: requestId }).catch(fail); };
    pending.set(requestId, { resolve, reject, cancel, signal });
    void send({ ...base('request'), id: requestId, method, value, handle }).catch(fail);
    if (signal) { signal.addEventListener('abort', cancel, { once: true }); if (signal.aborted) cancel(); }
    return result;
  };
  const finish = async () => {
    closing = true; process.stdin.removeListener('data', data); process.stdin.removeListener('end', eof);
    for (const work of active.values()) work.controller.abort();
    for (const work of pending.values()) { work.signal?.removeEventListener('abort', work.cancel); work.reject(Error('ClosedUnknown')); } pending.clear();
    await Promise.allSettled([...active.values()].map(w => w.task));
    await shutdown(); await Promise.all([...writes]); await send(base('shutdown')); await Promise.all([...writes]);
    process.stdin.destroy(); await new Promise((resolve, reject) => process.stdout.end(e => e ? reject(e) : resolve()));
    process.stdin.unref?.(); process.stdout.unref?.(); process.stderr.unref?.(); stoppedResolve();
  };
  const fail = error => { if (closing) return; closing = true; for (const w of active.values()) w.controller.abort(); for (const w of pending.values()) w.reject(Error('TransportUnknown'));
    void process.stderr.write('bridge-wire-fault\n', () => { process.exitCode = 31; process.stdin.destroy(); process.stdout.destroy(); }); void error; };
  const receive = (message, byteCount) => {
    fields(message, ['version', 'kind', 'workerGeneration', 'sessionGeneration', 'features', 'id', 'method', 'value', 'handle', 'error']);
    if (message.version !== 1 || message.workerGeneration !== worker || message.sessionGeneration !== session) throw Error('StaleGeneration');
    if (message.kind === 'hello') { if (negotiated || Object.keys(message).length !== 5 || !Array.isArray(message.features) || !features.every(f => message.features.includes(f))) throw Error('Handshake'); negotiated = true; return; }
    if (!negotiated) throw Error('Handshake');
    if (message.kind === 'shutdown') { if (Object.keys(message).length !== 4) throw Error('InvalidEnvelope'); void finish().catch(error => { process.stderr.write('bridge-cleanup-fault\n'); process.exitCode = 32; process.stdin.destroy(); process.stdout.destroy(); void error; }); return; }
    if (!id(message.id)) throw Error('Correlation');
    if (message.kind === 'cancel') { fields(message, ['version', 'kind', 'workerGeneration', 'sessionGeneration', 'id']); if (message.id > lastIncoming) throw Error('Correlation'); active.get(message.id)?.controller.abort(); return; }
    if (message.kind === 'response' || message.kind === 'error') { const work = pending.get(message.id); if (!work) throw Error('Correlation');
      pending.delete(message.id); work.signal?.removeEventListener('abort', work.cancel);
      if (message.kind === 'response') { fields(message, ['version', 'kind', 'workerGeneration', 'sessionGeneration', 'id', 'value']); tagged(message.value); work.resolve(message.value); }
      else { fields(message, ['version', 'kind', 'workerGeneration', 'sessionGeneration', 'id', 'error']); fields(message.error, ['code', 'outcome']); if (!identifier(message.error.code) || !['unknown', 'notSent'].includes(message.error.outcome)) throw Error('InvalidEnvelope'); work.reject(Error('HostCallbackFailed')); } return; }
    if (message.kind !== 'request' || !identifier(message.method) || message.id <= lastIncoming || active.size >= 16 || held + byteCount > retainedCap) throw Error('RequestLimit');
    fields(message, ['version', 'kind', 'workerGeneration', 'sessionGeneration', 'id', 'method', 'value', 'handle']); tagged(message.value);
    if (message.handle !== undefined) { fields(message.handle, ['ownerId', 'ownerGeneration', 'registrationId', 'callbackId']); if (!identifier(message.handle.ownerId) || !id(message.handle.ownerGeneration) || !identifier(message.handle.registrationId) || !identifier(message.handle.callbackId)) throw Error('InvalidHandle'); }
    lastIncoming = message.id; const controller = new AbortController(); held += byteCount;
    const task = Promise.resolve().then(() => handler(message, controller.signal, hostCall)).then(
      value => { tagged(value); if (!closing) return send({ ...base('response'), id: message.id, value }); },
      () => { if (!closing) return send({ ...base('error'), id: message.id, error: { code: 'CallbackFailed', outcome: 'unknown' } }); }
    ).finally(() => { held -= byteCount; active.delete(message.id); }); active.set(message.id, { controller, task }); void task.catch(fail);
  };
  const data = chunk => { if (closing) return; try { if (chunk.length + buffered.length > cap + 4096) throw Error('ReadLimit'); buffered = Buffer.concat([buffered, chunk]);
    for (;;) { const end = buffered.indexOf(10); if (end < 0) break; let line = buffered.subarray(0, end); buffered = buffered.subarray(end + 1); if (line.at(-1) === 13) line = line.subarray(0, -1); if (line.length > cap) throw Error('FrameLimit'); receive(strictJson(new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(line)), end + 1); } if (buffered.length > cap) throw Error('FrameLimit'); } catch (error) { fail(error); } };
  const eof = () => { if (!closing) fail(Error(buffered.length ? 'PartialFinalFrame' : 'EndOfInput')); };
  process.stdin.on('data', data); process.stdin.on('end', eof); process.stdin.on('error', fail); process.stdout.on('error', fail);
  await send({ ...base('hello'), features }); return { stopped, hostCall };
}
