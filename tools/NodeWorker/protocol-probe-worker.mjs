// Authored process/protocol probe, not an upstream extension host. No external packages.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const argv = process.argv.slice(2);
const allowed = new Set(['--worker-generation', '--session-generation', '--mode']);
const args = new Map();
for (let i = 0; i < argv.length; i += 2) {
  if (!allowed.has(argv[i]) || args.has(argv[i]) || i + 1 >= argv.length) throw new Error('Invalid probe arguments');
  args.set(argv[i], argv[i + 1]);
}
if (args.size !== 3) throw new Error('Missing probe arguments');
const identity = (s) => {
  if (!/^[1-9][0-9]*$/.test(s ?? '')) throw new Error('Invalid identity');
  const n = Number(s); if (!Number.isSafeInteger(n)) throw new Error('Invalid identity'); return n;
};
const worker = identity(args.get('--worker-generation'));
const session = identity(args.get('--session-generation'));
const mode = args.get('--mode');
if (!['normal', 'no-hello', 'wrong-generation', 'exit-before-hello', 'ignore-shutdown', 'owned-child'].includes(mode)) throw new Error('Invalid mode');
const root = process.cwd();
if (mode === 'owned-child') {
  const receipt = path.join(root, 'owned-child.pending');
  fs.writeFileSync(receipt, JSON.stringify({ pid: process.pid, parentPid: process.ppid, worker, session }) + '\n', { flag: 'wx' });
  fs.renameSync(receipt, path.join(root, 'owned-child.json'));
  setInterval(() => {}, 1000);
} else {
  await main();
}

async function main() {
  if (mode === 'exit-before-hello') { process.exitCode = 19; return; }
  const features = ['requests', 'callbacks', 'progress', 'cancel', 'tagged-values'];
  const frameCap = 1_048_576, retainedCap = 8_388_608;
  const pending = new Map(), active = new Map();
  let nextId = 0, lastIncoming = 0, writes = 0, writeBytes = 0, activeBytes = 0;
  let bytes = Buffer.alloc(0), ready = false, paused = false, closing = false, failed = false, child = null, shutdownTail = false;
  const writeTasks = new Set(), activeTasks = new Set();
  const keepAlive = setInterval(() => {}, 1000);
  const base = (kind) => ({ version: 1, kind, workerGeneration: worker, sessionGeneration: session });
  const json = (data) => ({ presence: 'json', data });
  const absent = { presence: 'absent' };
  const stderr = (s) => new Promise((resolve, reject) => process.stderr.write(s, (e) => e ? reject(e) : resolve()));
  const send = (message, fixedTail) => {
    const encoded = Buffer.from(JSON.stringify(message) + '\n', 'utf8');
    const frame = fixedTail ? Buffer.concat([encoded, fixedTail]) : encoded;
    if (frame.length - 1 > frameCap || writes >= 32 || frame.length > retainedCap - writeBytes) throw new Error('Probe write limit');
    writes++; writeBytes += frame.length;
    const task = new Promise((resolve, reject) => process.stdout.write(frame, (e) => e ? reject(e) : resolve()));
    writeTasks.add(task);
    void task.then(() => { writes--; writeBytes -= frame.length; writeTasks.delete(task); }, () => { writes--; writeBytes -= frame.length; writeTasks.delete(task); });
    return task;
  };
  const fail = (error) => {
    if (failed) return;
    failed = true; closing = true;
    // Fixed diagnostic only; neither arbitrary exception text nor protocol frames go to stderr.
    void stderr('probe-fault\n').finally(() => process.exit(31));
    void error;
  };
  process.stdout.on('error', fail); process.stdin.on('error', fail); process.stderr.on('error', () => process.exit(32));
  await stderr('probe-start\n');
  if (mode !== 'no-hello') await send({ ...base('hello'), workerGeneration: mode === 'wrong-generation' ? worker + 1 : worker, features });

  const hostCall = async (method, value, handle, progress) => {
    if (pending.size >= 32 || nextId === Number.MAX_SAFE_INTEGER) throw new Error('Probe pending limit');
    const id = ++nextId;
    let resolve, reject;
    const result = new Promise((a, b) => { resolve = a; reject = b; });
    pending.set(id, { resolve, reject, progress });
    try { await send({ ...base('request'), id, method, value, ...(handle ? { handle } : {}) }); return await result; }
    finally { pending.delete(id); }
  };
  const replyError = (id, code, outcome = 'unknown') => send({ ...base('error'), id, error: { code, outcome } });
  const cleanupObservations = [];
  const observeCleanup = (stage) => {
    const resources = process.getActiveResourcesInfo();
    const stream = (s) => ({ destroyed: s.destroyed, closed: s.closed, readableEnded: s.readableEnded,
      writableEnded: s.writableEnded, writableFinished: s.writableFinished, supportsUnref: typeof s.unref === 'function' });
    cleanupObservations.push({ stage, worker, session, pid: process.pid, activeHandlers: active.size,
      activeTasks: activeTasks.size, pendingCallbacks: pending.size, writeTasks: writeTasks.size, writes, writeBytes,
      activeBytes, bufferedInputBytes: bytes.length, inputEnded, timerHasRef: keepAlive.hasRef(),
      stdin: stream(process.stdin), stdout: stream(process.stdout), stderr: stream(process.stderr),
      activeResourceCount: resources.length, activeResources: resources.slice(0, 64), resourcesTruncated: resources.length > 64 });
    // Five fixed stage samples at most. This diagnostic is owned output, never a protocol record.
    fs.writeFileSync(path.join(root, 'peer-cleanup.receipt.json'), JSON.stringify({ observations: cleanupObservations }) + '\n');
  };
  const run = async (message, count) => {
    const control = { cancelled: false, release: null };
    active.set(message.id, control); activeBytes += count;
    try {
      let value = message.value;
      switch (message.method) {
        case 'probe.ping': value = json({ alive: true }); break;
        case 'probe.echo': break;
        case 'probe.runtime':
          value = json({ version: process.version, platform: process.platform, architecture: process.arch,
            pid: process.pid, parentPid: process.ppid, cwd: root, environment: Object.fromEntries(Object.entries(process.env).sort(([a], [b]) => a.localeCompare(b))) }); break;
        case 'probe.hold': {
          // Publish admission only after the actual release waiter exists: native release/cancel
          // can cross the stdout write callback for this progress frame.
          const released = new Promise((resolve) => { control.release = resolve; });
          await send({ ...base('progress'), id: message.id, value: json({ stage: 'held' }) });
          await released; value = { presence: 'undefined' }; break;
        }
        case 'probe.release':
          for (const item of active.values()) item.release?.(); value = json({ released: true }); break;
        case 'probe.pause-input': paused = true; process.stdin.pause(); value = json({ paused: true }); break;
        case 'probe.leaf':
          await send({ ...base('progress'), id: message.id, value: json({ stage: 'leaf', nil: null }) });
          value = json({ text: '\u0000a\u2028b\u2029\uD83D\uDC4B', n: 1 }); break;
        case 'probe.nested':
          value = await hostCall('probe.callback', { presence: 'undefined' }, message.value.data.handle,
            (progress) => send({ ...base('progress'), id: message.id, value: progress }));
          await send({ ...base('progress'), id: message.id, value: json({ stage: 'after-callback' }) }); break;
        case 'probe.effect-exit':
          fs.writeFileSync(path.join(root, 'effect.receipt.json'), JSON.stringify({ worker, session, id: message.id, effects: 1 }) + '\n', { flag: 'wx' });
          await stderr('probe-effect-before-exit\n'); process.exit(23); break;
        case 'probe.stdout-contamination': process.stdout.write('authored contamination\n'); return;
        case 'probe.stderr-flood': await stderr(Buffer.alloc(131_072, 0x78)); value = absent; break;
        case 'probe.shutdown-tail': shutdownTail = true; value = absent; break;
        case 'probe.child':
          if (child) throw new Error('Child already exists');
          child = spawn(process.execPath, [fileURLToPath(import.meta.url), '--worker-generation', String(worker), '--session-generation', String(session), '--mode', 'owned-child'],
            { cwd: root, env: process.env, stdio: 'ignore', windowsHide: true });
          await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
          value = json({ childPid: child.pid }); break;
        case 'probe.stale-generation': await send({ ...base('progress'), sessionGeneration: session - 1, id: message.id, value: absent }); return;
        default: await replyError(message.id, 'UnsupportedMethod', 'notSent'); return;
      }
      if (control.cancelled) await replyError(message.id, 'Cancelled');
      else await send({ ...base('response'), id: message.id, value });
    } catch { if (!closing) await replyError(message.id, 'CallbackFailed'); }
    finally { active.delete(message.id); activeBytes -= count; }
  };
  const receive = async (message, count) => {
    if (!message || message.version !== 1 || message.workerGeneration !== worker || message.sessionGeneration !== session) throw new Error('Probe envelope');
    if (message.kind === 'hello') {
      if (ready || !Array.isArray(message.features) || !['requests', 'cancel', 'tagged-values'].every((f) => message.features.includes(f))) throw new Error('Probe handshake');
      ready = true; return;
    }
    if (!ready) throw new Error('Probe pre-handshake');
    if (message.kind === 'shutdown') {
      if (mode === 'ignore-shutdown') return;
      closing = true;
      for (const item of active.values()) { item.cancelled = true; item.release?.(); }
      for (const item of pending.values()) item.reject(new Error('Probe closing'));
      pending.clear();
      // The fixed owned-child test uses ignore-shutdown, and is killed by its native process owner.
      await Promise.allSettled([...activeTasks]); await Promise.allSettled([...writeTasks]);
      observeCleanup('handlers-and-writes-settled');
      if (shutdownTail) {
        // Fixed negative only: the tail follows the complete acknowledgement line in one write.
        // The receipt records the attempted authored size, not a claim of successful delivery.
        fs.writeFileSync(path.join(root, 'shutdown-tail.receipt.json'), JSON.stringify({ worker, session, attemptedBytes: 131_072 }) + '\n', { flag: 'wx' });
      }
      await send(base('shutdown'), shutdownTail ? Buffer.alloc(131_072, 0x78) : undefined); await stderr('probe-stop\n');
      observeCleanup('ack-and-stderr-settled');
      clearInterval(keepAlive); process.stdin.destroy();
      observeCleanup('stdin-destroy-dispatched');
      await new Promise((resolve, reject) => process.stdout.end((error) => error ? reject(error) : resolve()));
      observeCleanup('stdout-end-settled');
      // Every real output callback and handler above has settled. Release the Windows stdio
      // references so their asynchronous handle teardown does not keep this fixed peer alive.
      for (const stream of [process.stdin, process.stdout, process.stderr]) {
        if (typeof stream.unref !== 'function') throw new Error('Probe stdio ownership profile');
        stream.unref();
      }
      observeCleanup('stdio-unref-dispatched'); return;
    }
    const id = identity(String(message.id));
    if (message.kind === 'cancel') {
      const item = active.get(id);
      if (item) { item.cancelled = true; await stderr('probe-cancel:' + id + '\n'); }
      else if (id > lastIncoming) throw new Error('Probe unknown cancel');
      return;
    }
    if (message.kind === 'progress') {
      const item = pending.get(id); if (!item) throw new Error('Probe correlation');
      await item.progress?.(message.value); return;
    }
    if (message.kind === 'response' || message.kind === 'error') {
      const item = pending.get(id); if (!item) throw new Error('Probe correlation');
      pending.delete(id);
      if (message.kind === 'error') item.reject(new Error('Native callback failed')); else item.resolve(message.value);
      return;
    }
    if (message.kind !== 'request' || id <= lastIncoming) throw new Error('Probe correlation');
    lastIncoming = id;
    if (active.size >= 16 || count > retainedCap - activeBytes) { await replyError(id, 'CallbackLimit', 'notSent'); return; }
    const task = run(message, count); activeTasks.add(task);
    void task.then(() => activeTasks.delete(task), (error) => { activeTasks.delete(task); fail(error); });
    // Bounded active set; continue admitting nested requests/replies.
  };
  let reading = false, inputEnded = false;
  const consume = async () => {
    if (reading) return; reading = true;
    try {
      while (!paused && !closing) {
        const lf = bytes.indexOf(0x0a);
        if (lf < 0) { if (bytes.length > frameCap + 1) throw new Error('Probe frame limit'); break; }
        let frame = bytes.subarray(0, lf); bytes = bytes.subarray(lf + 1);
        if (frame.at(-1) === 0x0d) frame = frame.subarray(0, frame.length - 1);
        if (frame.length > frameCap) throw new Error('Probe frame limit');
        const text = new TextDecoder('utf-8', { fatal: true }).decode(frame);
        await receive(JSON.parse(text), frame.length + 1);
      }
    } finally {
      reading = false;
      // EOF may be delivered while an asynchronous receive is still consuming the final
      // already-delivered shutdown frame. Judge the tail only after that work has settled.
      // The declared uncooperative fixture intentionally survives both shutdown and pipe EOF;
      // its keep-alive is released only by the native owner's actual process-tree termination.
      if (inputEnded && !closing && mode !== 'ignore-shutdown')
        fail(new Error(bytes.length ? 'Partial probe EOF' : 'Unexpected probe EOF'));
    }
  };
  process.stdin.on('data', (chunk) => {
    // Native sender is already strictly admitted; this peer is not a general hostile JS JSON parser.
    if (chunk.length > 65_536 || bytes.length + chunk.length > frameCap + 65_537) { fail(new Error('Probe read limit')); return; }
    bytes = Buffer.concat([bytes, chunk]); void consume().catch(fail);
  });
  process.stdin.on('end', () => { inputEnded = true; void consume().catch(fail); });
}
