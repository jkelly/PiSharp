// PiSharp Node extension host: the I/O thread. It owns the process's standard input and output, which carry the host protocol (one
// JSON object per line). Requests from the PiSharp host are passed to the main thread, where the extensions run. Calls the main
// thread makes synchronously (getters such as ctx.sessionManager.getEntries()) are answered here while the main thread waits on
// Atomics, so the host can answer them even though the extensions' event loop is blocked.
import fs from 'node:fs';
import { parentPort, workerData, receiveMessageOnPort } from 'node:worker_threads';

const { flag, syncPort, mainPort } = workerData;
const signal = new Int32Array(flag);
const maximumLine = 256 * 1024 * 1024;
let pendingSync = null; // { id }
let nextId = 0;
const ownRequests = new Map(); // host-protocol id -> { kind: 'sync'|'async', token }

function write(message) {
  const text = JSON.stringify(message) + '\n';
  const bytes = Buffer.from(text, 'utf8');
  let offset = 0;
  while (offset < bytes.length) {
    try { offset += fs.writeSync(1, bytes, offset, bytes.length - offset); }
    catch (error) { if (error.code === 'EAGAIN') continue; throw error; }
  }
}

function deliverSync(payload) {
  syncPort.postMessage(payload);
  Atomics.store(signal, 0, 1);
  Atomics.notify(signal, 0);
}

mainPort.on('message', (message) => {
  switch (message.kind) {
    case 'out': write(message.frame); break;
    case 'sync': {
      const id = ++nextId;
      ownRequests.set(id, { kind: 'sync' });
      pendingSync = id;
      write({ type: 'request', id, method: message.method, params: message.params, sync: true });
      break;
    }
    case 'async': {
      const id = ++nextId;
      ownRequests.set(id, { kind: 'async', token: message.token });
      write({ type: 'request', id, method: message.method, params: message.params });
      break;
    }
    case 'stop': process.exit(0); break;
    case 'cancelOwn': {
      for (const [id, own] of ownRequests) if (own.kind === 'async' && own.token === message.token) { write({ type: 'cancel', id }); break; }
      break;
    }
  }
});

function receive(line) {
  let message;
  try { message = JSON.parse(line); } catch { process.stderr.write('pisharp-node-host: malformed frame\n'); process.exit(30); }
  if (message.type === 'response') {
    const own = ownRequests.get(message.id);
    if (!own) return;
    ownRequests.delete(message.id);
    if (own.kind === 'sync') { pendingSync = null; deliverSync(message); }
    else mainPort.postMessage({ kind: 'response', token: own.token, message });
    return;
  }
  if (message.type === 'progress') {
    const own = ownRequests.get(message.id);
    if (own?.kind === 'async') mainPort.postMessage({ kind: 'progress', token: own.token, value: message.value });
    return;
  }
  // Requests, notifications and cancellations go to the main thread.
  mainPort.postMessage({ kind: 'incoming', message });
}

let buffered = Buffer.alloc(0);
const chunk = Buffer.alloc(1 << 16);
function pump() {
  fs.read(0, chunk, 0, chunk.length, null, (error, count) => {
    if (error) {
      if (error.code === 'EAGAIN') { setTimeout(pump, 5); return; }
      if (error.code === 'EOF') count = 0; else { mainPort.postMessage({ kind: 'eof' }); return; }
    }
    if (count === 0) {
      if (pendingSync !== null) deliverSync({ type: 'response', id: pendingSync, error: { message: 'PiSharp host closed the connection' } });
      mainPort.postMessage({ kind: 'eof' });
      return;
    }
    buffered = Buffer.concat([buffered, chunk.subarray(0, count)]);
    for (;;) {
      const end = buffered.indexOf(10);
      if (end < 0) break;
      let line = buffered.subarray(0, end);
      buffered = buffered.subarray(end + 1);
      if (line.length && line[line.length - 1] === 13) line = line.subarray(0, line.length - 1);
      if (line.length) receive(line.toString('utf8'));
    }
    if (buffered.length > maximumLine) { process.stderr.write('pisharp-node-host: frame limit\n'); process.exit(31); }
    pump();
  });
}
void receiveMessageOnPort; void parentPort;
pump();
