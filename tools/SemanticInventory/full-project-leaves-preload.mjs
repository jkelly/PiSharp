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
import { repo, hash, regular, readPlan, verifyOracle, contained, admitPaths, environment, noLinks } from './full-project-leaves-common.mjs';

export function nativeArguments(oracle) { return ['--api', '--async', '--cwd', join(oracle, 'upstream'), '--callbacks=readFile,fileExists,directoryExists,getAccessibleEntries,realpath']; }
export function assertNativeSpawn(path, args, options, context) { assert.equal(context.count, 0, 'Only one helper permitted'); assert.equal(resolve(path).toLowerCase(), resolve(context.exe).toLowerCase()); assert.deepEqual(args, nativeArguments(context.oracle)); assert.deepEqual(options?.stdio, ['pipe', 'pipe', 'inherit']); for (const field of ['shell', 'cwd', 'env']) assert.equal(options?.[field], undefined); assert.equal(resolve(context.cwd).toLowerCase(), resolve(context.scratch).toLowerCase()); assert.equal(context.hash, context.expectedHash); }
export function assertChildEnvironment(actual, scratch, plan) {
  const wanted = environment(scratch, plan), fold = name => process.platform === 'win32' ? name.toUpperCase() : name;
  const admitted = new Set([...Object.keys(wanted), 'PISHARP_SEMANTIC_LEAVES_FULL_CHILD', 'PISHARP_SEMANTIC_LEAVES_FULL_ORACLE', 'PISHARP_SEMANTIC_LEAVES_FULL_SCRATCH', 'PISHARP_SEMANTIC_LEAVES_FULL_OUTPUT'].map(fold)), entries = new Map();
  for (const name of Object.keys(actual)) { const key = fold(name); assert(!entries.has(key), 'Ambiguous environment key names: ' + JSON.stringify([entries.get(key)?.name, name])); entries.set(key, { name, value: actual[name] }); }
  const unexpected = [...entries.entries()].filter(([key]) => !admitted.has(key)).map(([, row]) => row.name);
  assert(unexpected.length === 0, 'Unexpected inherited environment key names: ' + JSON.stringify(unexpected));
  for (const [name, value] of Object.entries(wanted)) assert(entries.get(fold(name))?.value === value, 'Child environment differs at key: ' + name);
}
export async function awaitExit(child, timeout) { if (child.exitCode === null && child.signalCode === null) await new Promise((resolveExit, rejectExit) => { const finish = () => { clearTimeout(timer); resolveExit(); }; const timer = setTimeout(() => { child.removeListener('exit', finish); child.kill(); rejectExit(new Error('Owned helper exit timeout; termination requested; run failed')); }, timeout); child.once('exit', finish); }); assert.equal(child.exitCode, 0); assert.equal(child.signalCode, null); return { exitCode: 0, signalCode: null, exitAwaited: true }; }
export function runtimeModuleRows(receipt, plan) { return receipt.files.filter(row => row.path.startsWith(plan.executionBoundary.runtimeModulePrefix)); }
export async function installGuard(oracle, scratch, output, plan = readPlan()) {
  admitPaths(oracle, scratch, plan); assert(contained(scratch, output)); assert.equal(resolve(process.cwd()).toLowerCase(), resolve(scratch).toLowerCase()); assertChildEnvironment(process.env, scratch, plan); const verified = await verifyOracle(oracle, plan), exe = join(oracle, plan.native.path), loaded = new Map();
  const permitted = new Map(runtimeModuleRows(verified.receipt, plan).map(row => [resolve(join(oracle, row.path)).toLowerCase(), row])); const harnessPaths = ['full-project-leaves-common.mjs', 'full-project-leaves-preload.mjs', 'full-project-leaves-driver.mjs'].map(name => join(repo, 'tools/SemanticInventory', name)), harness = new Map(harnessPaths.map(path => [resolve(path).toLowerCase(), { path: relative(repo, path).split(sep).join('/'), bytes: regular(path).length, sha256: hash(regular(path)) }]));
  const protocolPath = output + '.protocol.jsonl'; noLinks(protocolPath); const descriptor = openSync(protocolPath, 'wx'), originalSpawn = cp.spawn, protocolHash = createHash('sha256'), state = { count: 0, child: undefined, protocolBytes: 0, chunks: 0, nativeFailure: undefined, denied: [] }; let protocolClosed = false, protocolDigest;
  function record(direction, raw) { state.protocolBytes += raw.length; if (state.protocolBytes > plan.limits.protocolBytes) { state.nativeFailure = 'Complete native protocol exceeds admitted bounds'; state.child?.kill(); throw new Error(state.nativeFailure); } const row = Buffer.from(JSON.stringify({ direction, base64: raw.toString('base64') }) + '\n'); writeSync(descriptor, row); protocolHash.update(row); state.chunks++; }
  function deny(name) { return function () { state.denied.push(name); throw new Error('Full-project oracle denied ' + name); }; }
  cp.spawn = function (path, args, options) { assertNativeSpawn(path, args, options, { count: state.count, exe, oracle, scratch, cwd: process.cwd(), hash: hash(regular(exe)), expectedHash: plan.native.sha256 }); assertChildEnvironment(process.env, scratch, plan); state.count++; const child = state.child = originalSpawn(path, args, options);
    const watchdog = setTimeout(() => { state.nativeFailure = 'Native operation deadline exceeded'; child.kill(); }, plan.limits.driverTimeoutMs - plan.native.exitTimeoutMs); child.once('exit', () => clearTimeout(watchdog)); child.once('error', () => clearTimeout(watchdog));
    child.stdout.on('data', raw => record('native-to-client', raw)); const originalWrite = child.stdin.write.bind(child.stdin); child.stdin.write = function (bytes, ...rest) { const raw = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes, typeof rest[0] === 'string' ? rest[0] : 'utf8'); record('client-to-native', raw); return originalWrite(bytes, ...rest); }; return child;
  };
  for (const name of ['spawnSync', 'exec', 'execSync', 'execFile', 'execFileSync', 'fork']) cp[name] = deny('process.' + name);
  for (const name of ['connect', 'createConnection', 'createServer']) net[name] = deny('net.' + name); net.Socket.prototype.connect = deny('Socket.connect'); net.Server.prototype.listen = deny('Server.listen');
  for (const mod of [http, https]) for (const name of ['request', 'get', 'createServer']) mod[name] = deny('HTTP.' + name); for (const name of ['connect', 'createServer']) tls[name] = deny('TLS.' + name); for (const name of ['connect', 'createServer', 'createSecureServer']) http2[name] = deny('http2.' + name); dgram.createSocket = deny('dgram.createSocket'); for (const name of ['bind', 'connect', 'send']) dgram.Socket.prototype[name] = deny('UDP.' + name); workers.Worker = deny('Worker'); globalThis.fetch = deny('fetch'); globalThis.WebSocket = deny('WebSocket'); syncBuiltinESMExports();
  registerHooks({ load(url, context, nextLoad) { if (url.startsWith('file:')) { const path = fileURLToPath(url), key = resolve(path).toLowerCase(), row = permitted.get(key) ?? harness.get(key); assert(row, 'Unadmitted loaded module'); const bytes = regular(path); assert.equal(hash(bytes), row.sha256); loaded.set(key, row); } return nextLoad(url, context); } });
  const guard = { verified, state, async finish() { try { assert.equal(state.count, 1); const exit = await awaitExit(state.child, plan.native.exitTimeoutMs); assert(!state.nativeFailure, state.nativeFailure); return exit; } finally { if (!protocolClosed) { closeSync(descriptor); protocolClosed = true; } } }, provenance() { assert(protocolClosed, 'Native protocol lifecycle not closed'); protocolDigest ??= protocolHash.digest('hex'); return { executable: { path: exe, sha256: plan.native.sha256 }, arguments: nativeArguments(oracle), cwd: process.cwd(), environment: { ...process.env }, loadedModules: [...loaded.values()].sort((a, b) => a.path.localeCompare(b.path)), protocol: { path: protocolPath, chunks: state.chunks, rawBytes: state.protocolBytes, sha256: protocolDigest }, denied: state.denied, nativeFailure: state.nativeFailure, nativeOsSandboxEnforcedByNode: false }; } };
  globalThis.pisharpFullProjectLeavesGuard = guard; return guard;
}
if (process.env.PISHARP_SEMANTIC_LEAVES_FULL_CHILD === '1') await installGuard(process.env.PISHARP_SEMANTIC_LEAVES_FULL_ORACLE, process.env.PISHARP_SEMANTIC_LEAVES_FULL_SCRATCH, process.env.PISHARP_SEMANTIC_LEAVES_FULL_OUTPUT);

