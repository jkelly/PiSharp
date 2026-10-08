// Whole actual native async API; NEW profile guard never mutates the old oracle.
import assert from 'node:assert/strict';
import cp from 'node:child_process';
import net from 'node:net';
import http from 'node:http';
import https from 'node:https';
import tls from 'node:tls';
import http2 from 'node:http2';
import dgram from 'node:dgram';
import workers from 'node:worker_threads';
import { createHash } from 'node:crypto';
import { openSync, writeSync, closeSync } from 'node:fs';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { join, resolve, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { repo, hash, regular, readPlan, verifyOracle, contained, admitPaths, environment, noLinks } from './full-structural-openai-common.mjs';

export function nativeArguments(oracle) { return ['--api', '--async', '--cwd', join(oracle, 'upstream'), '--callbacks=readFile,fileExists,directoryExists,getAccessibleEntries,realpath']; }
export function assertNativeSpawn(path, args, options, context) { assert.equal(context.count, 0, 'Only one helper permitted'); assert.equal(resolve(path).toLowerCase(), resolve(context.exe).toLowerCase()); assert.deepEqual(args, nativeArguments(context.oracle)); assert.deepEqual(options?.stdio, ['pipe', 'pipe', 'inherit']); for (const field of ['shell', 'cwd', 'env']) assert.equal(options?.[field], undefined); assert.equal(resolve(context.cwd).toLowerCase(), resolve(context.scratch).toLowerCase()); assert.equal(context.hash, context.expectedHash); }
export function assertChildEnvironment(actual, scratch, plan) {
  const wanted = environment(scratch, plan), fold = name => process.platform === 'win32' ? name.toUpperCase() : name;
  const admitted = new Set([...Object.keys(wanted), 'PISHARP_SEMANTIC_OPENAI_STRUCTURAL_CHILD', 'PISHARP_SEMANTIC_OPENAI_STRUCTURAL_ORACLE', 'PISHARP_SEMANTIC_OPENAI_STRUCTURAL_SCRATCH', 'PISHARP_SEMANTIC_OPENAI_STRUCTURAL_OUTPUT'].map(fold)), entries = new Map();
  for (const name of Object.keys(actual)) { const key = fold(name); assert(!entries.has(key), 'Ambiguous environment key names: ' + JSON.stringify([entries.get(key)?.name, name])); entries.set(key, { name, value: actual[name] }); }
  const unexpected = [...entries.entries()].filter(([key]) => !admitted.has(key)).map(([, row]) => row.name);
  assert(unexpected.length === 0, 'Unexpected inherited environment key names: ' + JSON.stringify(unexpected));
  for (const [name, value] of Object.entries(wanted)) assert(entries.get(fold(name))?.value === value, 'Child environment differs at key: ' + name);
}
export function observeOwnedChildClose(child, label, timers = { setTimeout, clearTimeout }) {
  assert(child && typeof child.once === 'function' && typeof child.kill === 'function', 'Actual owned ChildProcess required');
  const observation = { label, spawned: false, exitObserved: false, closeObserved: false, events: [], errors: [], failures: [], watchdogs: [], terminationRequests: [], stdio: {} }; let firstFailure, terminationRequested = false;
  const activeTimers = new Set(); let resolveClose; const closed = new Promise(resolve => { resolveClose = resolve; });
  function event(kind, details = {}) { observation.events.push({ ordinal: observation.events.length, kind, ...details }); }
  function noteFailure(kind, error) { const value = { kind, name: error?.name ?? 'Error', message: error?.message ?? String(error), code: error?.code, syscall: error?.syscall, path: error?.path }; observation.failures.push(value); firstFailure ??= value; return value; }
  function requestTermination(reason) {
    const request = { reason, suppressed: terminationRequested || observation.closeObserved }; observation.terminationRequests.push(request);
    if (request.suppressed) return; terminationRequested = true;
    // The pinned native client documents a read loop that needs stdin EOF before termination.
    if (typeof child.stdin?.end === 'function') {
      request.stdinEndRequested = true;
      try { child.stdin.end(); request.stdinEndReturned = true; event('termination-stdin-end', { reason }); }
      catch (error) { request.stdinEndError = noteFailure('termination-stdin-end-error', error); event('termination-stdin-end-error', { reason, error: request.stdinEndError }); }
    }
    try { request.returned = child.kill(); event('termination-request', { reason, returned: request.returned }); }
    catch (error) { request.error = noteFailure('termination-request-error', error); event('termination-request-error', { reason, error: request.error }); }
  }
  child.once('spawn', () => { observation.spawned = true; observation.pid = child.pid; event('spawn', { pid: child.pid }); });
  child.on('error', error => { const value = noteFailure('child-error', error); observation.errors.push({ name: error.name, message: error.message, code: error.code, syscall: error.syscall, path: error.path }); event('error', { error: observation.errors.at(-1) }); requestTermination(value.kind); });
  child.once('exit', (code, signal) => { observation.exitObserved = true; observation.exit = { code, signal }; event('exit', { code, signal }); if (code !== 0 || signal !== null) noteFailure('child-exit', new Error(label + ' exit ' + code + '/' + signal)); });
  for (const name of ['stdin','stdout','stderr']) {
    const stream = child[name], row = observation.stdio[name] = { available: !!stream, endObserved: false, finishObserved: false, closeObserved: false, errors: [] };
    if (!stream) continue;
    for (const kind of ['end','finish','close']) stream.once(kind, () => { row[kind + 'Observed'] = true; event(name + '-' + kind); });
    stream.on('error', error => { row.errors.push({ name: error.name, message: error.message, code: error.code }); noteFailure(name + '-error', error); event(name + '-error', { error: row.errors.at(-1) }); requestTermination(name + '-error'); });
  }
  child.once('close', (code, signal) => {
    observation.closeObserved = true; observation.close = { code, signal }; observation.childStateAtClose = { exitCode: child.exitCode, signalCode: child.signalCode, killed: child.killed, pid: child.pid }; event('close', { code, signal });
    for (const timer of activeTimers) timers.clearTimeout(timer); activeTimers.clear();
    if (code !== 0 || signal !== null) noteFailure('child-close', new Error(label + ' close ' + code + '/' + signal));
    if (!observation.exitObserved && !firstFailure) noteFailure('missing-exit-witness', new Error(label + ' closed without an observed exit event'));
    if (observation.exitObserved && (code !== observation.exit.code || signal !== observation.exit.signal)) noteFailure('exit-close-disagreement', new Error(label + ' exit/close outcomes disagree'));
    resolveClose();
  });
  function armDeadline(kind, milliseconds) {
    assert(Number.isSafeInteger(milliseconds) && milliseconds > 0); const watchdog = { kind, milliseconds, armed: !observation.closeObserved, fired: false }; observation.watchdogs.push(watchdog);
    if (!watchdog.armed) return; const timer = timers.setTimeout(() => { activeTimers.delete(timer); watchdog.fired = true; event('watchdog', { kind, milliseconds }); noteFailure(kind, new Error(label + ' ' + kind + '; owned termination requested; physical close still awaited')); requestTermination(kind); }, milliseconds); activeTimers.add(timer);
  }
  return {
    child, observation, noteFailure, requestTermination, armDeadline,
    async wait() { await closed; return { observation, firstFailure }; },
    async requireSuccessfulClose() { await closed; if (firstFailure) { const error = new Error(firstFailure.message); error.name = firstFailure.name; error.ownedChildLifecycle = observation; error.firstFailure = firstFailure; throw error; } assert.equal(observation.closeObserved, true); assert.equal(observation.exitObserved, true); return { exitCode: observation.exit.code, signalCode: observation.exit.signal, exitAwaited: true, closeAwaited: true, physicalCloseObserved: true, observation }; },
    firstFailure() { return firstFailure; }
  };
}
export async function awaitExit(child, timeout, lifecycle) { assert(lifecycle, 'Native close observation must be installed at actual spawn'); assert.equal(lifecycle.child, child, 'Close observer must own the exact spawned helper'); lifecycle.armDeadline('native-close-deadline', timeout); return lifecycle.requireSuccessfulClose(); }
export function runtimeModuleRows(receipt, plan) { return receipt.files.filter(row => row.path.startsWith(plan.executionBoundary.runtimeModulePrefix)); }
export async function installGuard(oracle, scratch, output, plan = readPlan()) {
  admitPaths(oracle, scratch, plan); assert(contained(scratch, output)); assert.equal(resolve(process.cwd()).toLowerCase(), resolve(scratch).toLowerCase()); assertChildEnvironment(process.env, scratch, plan); const verified = await verifyOracle(oracle, plan), exe = join(oracle, plan.native.path), loaded = new Map();
  const permitted = new Map(runtimeModuleRows(verified.receipt, plan).map(row => [resolve(join(oracle, row.path)).toLowerCase(), row])); const harnessPaths = ['full-structural-openai-common.mjs', 'full-structural-openai-preload.mjs', 'full-structural-openai-driver.mjs'].map(name => join(repo, 'tools/SemanticReference', name)), harness = new Map(harnessPaths.map(path => [resolve(path).toLowerCase(), { path: relative(repo, path).split(sep).join('/'), bytes: regular(path).length, sha256: hash(regular(path)) }]));
  const protocolPath = output + '.protocol.jsonl'; noLinks(protocolPath); const descriptor = openSync(protocolPath, 'wx'), originalSpawn = cp.spawn, protocolHash = createHash('sha256'), state = { count: 0, child: undefined, nativeLifecycle: undefined, protocolBytes: 0, protocolBytesCaptured: 0, protocolBytesOmitted: 0, protocolFileBytes: 0, chunks: 0, nativeFailure: undefined, protocolOverflow: false, protocolWriteFailure: false, denied: [] }; let protocolClosed = false, protocolDigest;
  function record(direction, raw) {
    state.protocolBytes += raw.length; const available = state.protocolWriteFailure ? 0 : Math.max(0, plan.limits.protocolBytes - state.protocolBytesCaptured), prefix = raw.subarray(0, available); let captured = 0;
    if (prefix.length) { try { const row = Buffer.from(JSON.stringify({ direction, base64: prefix.toString('base64') }) + '\n'); let offset = 0; while (offset < row.length) { const written = writeSync(descriptor, row, offset, row.length - offset); assert(written > 0, 'Native protocol write made no progress'); protocolHash.update(row.subarray(offset, offset + written)); state.protocolFileBytes += written; offset += written; } captured = prefix.length; state.chunks++; }
      catch (error) { state.protocolWriteFailure = true; state.nativeFailure ??= 'Native protocol capture write failed: ' + error.message; state.nativeLifecycle.noteFailure('protocol-write-error', error); state.nativeLifecycle.requestTermination('protocol-write-error'); }
    }
    state.protocolBytesCaptured += captured; state.protocolBytesOmitted += raw.length - captured;
    if (prefix.length !== raw.length && !state.protocolWriteFailure && !state.protocolOverflow) { state.protocolOverflow = true; state.nativeFailure ??= 'Complete native protocol exceeds admitted bounds'; state.nativeLifecycle.noteFailure('protocol-overflow', new Error(state.nativeFailure)); state.nativeLifecycle.requestTermination('protocol-overflow'); }
  }
  function deny(name) { return function () { state.denied.push(name); throw new Error('Full-project oracle denied ' + name); }; }
  cp.spawn = function (path, args, options) { assertNativeSpawn(path, args, options, { count: state.count, exe, oracle, scratch, cwd: process.cwd(), hash: hash(regular(exe)), expectedHash: plan.native.sha256 }); assertChildEnvironment(process.env, scratch, plan); state.count++; const child = state.child = originalSpawn(path, args, options);
    state.nativeLifecycle = observeOwnedChildClose(child, 'native compiler helper'); state.nativeLifecycle.armDeadline('native-operation-deadline', plan.limits.driverTimeoutMs - plan.native.exitTimeoutMs);
    child.stdout.on('data', raw => record('native-to-client', raw)); const originalWrite = child.stdin.write.bind(child.stdin); child.stdin.write = function (bytes, ...rest) { const raw = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes, typeof rest[0] === 'string' ? rest[0] : 'utf8'); record('client-to-native', raw); return originalWrite(bytes, ...rest); }; return child;
  };
  for (const name of ['spawnSync', 'exec', 'execSync', 'execFile', 'execFileSync', 'fork']) cp[name] = deny('process.' + name);
  for (const name of ['connect', 'createConnection', 'createServer']) net[name] = deny('net.' + name); net.Socket.prototype.connect = deny('Socket.connect'); net.Server.prototype.listen = deny('Server.listen');
  for (const mod of [http, https]) for (const name of ['request', 'get', 'createServer']) mod[name] = deny('HTTP.' + name); for (const name of ['connect', 'createServer']) tls[name] = deny('TLS.' + name); for (const name of ['connect', 'createServer', 'createSecureServer']) http2[name] = deny('http2.' + name); dgram.createSocket = deny('dgram.createSocket'); for (const name of ['bind', 'connect', 'send']) dgram.Socket.prototype[name] = deny('UDP.' + name); workers.Worker = deny('Worker'); globalThis.fetch = deny('fetch'); globalThis.WebSocket = deny('WebSocket'); syncBuiltinESMExports();
  registerHooks({ load(url, context, nextLoad) { if (url.startsWith('file:')) { const path = fileURLToPath(url), key = resolve(path).toLowerCase(), row = permitted.get(key) ?? harness.get(key); assert(row, 'Unadmitted loaded module'); const bytes = regular(path); assert.equal(hash(bytes), row.sha256); loaded.set(key, row); } return nextLoad(url, context); } });
  function protocolReceipt() { assert(protocolClosed, 'Native protocol lifecycle not closed'); protocolDigest ??= protocolHash.digest('hex'); return { path: protocolPath, chunks: state.chunks, rawBytes: state.protocolBytes, capturedRawBytes: state.protocolBytesCaptured, omittedRawBytes: state.protocolBytesOmitted, fileBytes: state.protocolFileBytes, complete: !state.protocolWriteFailure && !state.protocolOverflow && state.protocolBytesOmitted === 0, writeFailure: state.protocolWriteFailure, overflow: state.protocolOverflow, sha256: protocolDigest }; }
  const guard = { verified, state, async finish() { try { assert.equal(state.count, 1); const exit = await awaitExit(state.child, plan.native.exitTimeoutMs, state.nativeLifecycle); assert(!state.nativeFailure, state.nativeFailure); return exit; } finally { if (state.nativeLifecycle) assert.equal(state.nativeLifecycle.observation.closeObserved, true, 'Native protocol cannot be closed without physical child-close witness'); if (!protocolClosed) { closeSync(descriptor); protocolClosed = true; } } }, lifecycleReceipt() { const observation = state.nativeLifecycle?.observation; return { nativeChildLifecycle: observation ? { label: observation.label, spawned: observation.spawned, pid: observation.pid, exitObserved: observation.exitObserved, closeObserved: observation.closeObserved, exit: observation.exit, close: observation.close, childStateAtClose: observation.childStateAtClose, eventCount: observation.events.length, failureCount: observation.failures.length, watchdogCount: observation.watchdogs.length, terminationRequestCount: observation.terminationRequests.length, stdio: Object.fromEntries(Object.entries(observation.stdio).map(([name,row]) => [name,{ available: row.available, endObserved: row.endObserved, finishObserved: row.finishObserved, closeObserved: row.closeObserved, errorCount: row.errors.length }])) } : undefined, ownedNativeHandles: state.count, nativeFirstFailure: state.nativeLifecycle?.firstFailure(), nativeFailure: state.nativeFailure, protocol: protocolReceipt() }; }, provenance() { return { executable: { path: exe, sha256: plan.native.sha256 }, arguments: nativeArguments(oracle), cwd: process.cwd(), environment: { ...process.env }, loadedModules: [...loaded.values()].sort((a, b) => a.path.localeCompare(b.path)), protocol: protocolReceipt(), nativeChildLifecycle: state.nativeLifecycle?.observation, nativeFirstFailure: state.nativeLifecycle?.firstFailure(), denied: state.denied, nativeFailure: state.nativeFailure, nativeOsSandboxEnforcedByNode: false }; } };
  globalThis.pisharpFullStructuralOpenaiGuard = guard; return guard;
}
if (process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_CHILD === '1') await installGuard(process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_ORACLE, process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_SCRATCH, process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_OUTPUT);
