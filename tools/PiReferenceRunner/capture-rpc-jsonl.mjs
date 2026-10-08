import { currentPublicHarnessPins, publicDerivativeVerificationScope } from '../PublicDerivativeIntegrity.mjs';
import { physicalReferencePath, qualifiedReferenceFile, validatedReferenceRoot } from '../PublicReferenceLayout.mjs';
// Capture only unchanged public JSONL exports. No line splitting or JSON admission
// is implemented here; actual Node streams deliver the authored chunks.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { Readable } from 'node:stream';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const family = 'fixtures/pi-v0.99.1/rpc-jsonl', fixtureId = 'rpc-jsonl-core';
const sourceSha = 'd86654abb8862e201933517d6f1fce9f88dd117f', sourceTree = '200bd10bb146773516f862a02b8aaebeed163e00';
const approvedSource = 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1/upstream';
const modulePath = 'packages/coding-agent/src/modes/rpc/jsonl.ts';
const modulePin = { path: modulePath, bytes: 1503, sha256: '95723d349fcebad1f1da7ce103d02ba7d5e2c876b7d178d41d8b56beedbd93e0' };
const runtimePath = 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe';
const runtimeSha = '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237';
const inputSha = '8a5388aa9bb953beed7410044370a500e87dfd20644904d1ef6a93cddbeb5bb7';
const normalizer = 'object-key-order-v1; callback-strings-wire-bytes-and-array-order-retained';
const kind = 'captured-whole-rpc-jsonl-oracle';
const inputPath = join(repo, family, 'core.input.json'), expectedPath = join(repo, family, 'core.expected.json');
const lockPath = join(repo, family, 'oracle.lock.json'), manifestPath = join(repo, family, 'manifest.json');
const guardPath = join(repo, 'tools/PiReferenceRunner/offline-guard.mjs');
const guardSha = 'ae3741bdce496451bd04afcf8628df6ddc5bc5cfad8af6ee5e52cbc7b065a094';
const rawPath = join(repo, 'tools/CompatibilityReport/raw-json.mjs');
const rawSha = '58c378290d0be114e740dadda934d9a57e16a9321ae05eb8e34320e11d561ee9';
const gitExe = 'C:/Program Files/Git/cmd/git.exe';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`, 'utf8');
const readJson = path => parseJsonSupported(readFileSync(path, 'utf8'));
const same = (left, right) => compareRawJson(JSON.stringify(left), JSON.stringify(right));
function inside(root, path) { const suffix = relative(resolve(root), resolve(path)); return suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); }
function keys(value, names) { assert(value && typeof value === 'object' && !Array.isArray(value)); assert.deepEqual(Object.keys(value).sort(), [...names].sort()); }
function bytesFromHex(hex) { assert(typeof hex === 'string' && /^(?:[a-f0-9]{2})*$/.test(hex) && hex.length <= 32768, 'Bounded exact lower-case byte hex required'); return Buffer.from(hex, 'hex'); }
function runtimeCheck() {
  assert.equal(process.version, 'v24.19.0'); assert.equal(process.platform, 'win32'); assert.equal(process.arch, 'x64');
  assert.equal(realpathSync(process.execPath).toLowerCase(), realpathSync(qualifiedReferenceFile(runtimePath, runtimeSha)).toLowerCase(), 'Use the absolute qualified runtime');
  assert.equal(fileHash(process.execPath), runtimeSha, 'Pinned runtime bytes differ');
  assert.equal(fileHash(guardPath), guardSha, 'Accepted offline guard changed'); assert.equal(fileHash(rawPath), rawSha, 'Accepted raw comparator changed');
}
function sourceSelection(value) {
  const root = resolve(value ?? physicalReferencePath(approvedSource));
  assert.equal(root.toLowerCase(), resolve(physicalReferencePath(approvedSource)).toLowerCase(), 'Only the approved unchanged source checkout is admitted');
  assert.equal(realpathSync(root).toLowerCase(), root.toLowerCase(), 'Source root resolves through an unexpected path');
  const path = join(root, modulePath); assert(!lstatSync(path).isSymbolicLink(), 'Source module link rejected');
  const bytes = readFileSync(path); assert.equal(bytes.length, modulePin.bytes); assert.equal(hash(bytes), modulePin.sha256);
  return root;
}
function sourceCheck(root) {
  const git = (...args) => {
    const result = spawnSync(gitExe, ['-c', `safe.directory=${root}`, '-C', root, ...args], { cwd: root, env: { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: 'NUL', GIT_OPTIONAL_LOCKS: '0' }, windowsHide: true, timeout: 20000, maxBuffer: 8 * 1024 * 1024 });
    assert.equal(result.status, 0, result.error?.message ?? result.stderr.toString()); return result.stdout;
  };
  const revision = git('rev-parse', 'HEAD').toString().trim(), tree = git('rev-parse', 'HEAD^{tree}').toString().trim();
  const status = git('status', '--porcelain=v1', '--untracked-files=all').toString();
  assert.equal(revision, sourceSha); assert.equal(tree, sourceTree); assert.equal(status, '', 'Approved upstream source checkout must remain entirely clean');
  const canonical = git('show', `${sourceSha}:${modulePath}`); assert.equal(hash(canonical), modulePin.sha256); assert.equal(canonical.length, modulePin.bytes);
  const bytes = readFileSync(join(root, modulePath)); assert(bytes.equals(canonical), 'Executed module differs from canonical Git bytes');
  return { revision, tree, status, sourceSha256: hash(bytes), canonicalGitBlobSha256: hash(canonical) };
}
function inputCheck() {
  assert.equal(fileHash(inputPath), inputSha, 'Frozen authored input bytes changed');
  const input = readJson(inputPath);
  keys(input, ['schemaVersion', 'fixtureId', 'sourceSha', 'requirementIds', 'kind', 'normalizerVersion', 'clock', 'seed', 'readerCases', 'serializerCases', 'provenance']);
  assert.equal(input.schemaVersion, 1); assert.equal(input.fixtureId, fixtureId); assert.equal(input.sourceSha, sourceSha);
  assert.deepEqual(input.requirementIds, ['rpc.framing']); assert.equal(input.kind, 'authored-rpc-jsonl-chunks-and-serialization-values'); assert.equal(input.normalizerVersion, normalizer);
  assert.equal(input.provenance.kind, 'authored-synthetic-input'); assert(input.clock && input.seed);
  assert(Array.isArray(input.readerCases) && input.readerCases.length > 0 && input.readerCases.length <= 64);
  assert(Array.isArray(input.serializerCases) && input.serializerCases.length > 0 && input.serializerCases.length <= 32);
  const identities = [...input.readerCases, ...input.serializerCases].map(row => row.caseId);
  assert(identities.every(id => typeof id === 'string' && id)); assert.equal(new Set(identities).size, identities.length);
  return input;
}
function readerProbes(test) {
  const base = ['caseId', 'purpose', ...(test.chunks ? ['chunks'] : ['wire', 'fragments']), ...(Object.hasOwn(test, 'detachAfterChunk') ? ['detachAfterChunk'] : [])]; keys(test, base);
  assert.equal(typeof test.purpose, 'string');
  let probes;
  if (test.chunks) {
    assert(Array.isArray(test.chunks) && test.chunks.length > 0 && test.chunks.length <= 16384);
    probes = [{ probeId: 'explicit-chunks', chunks: test.chunks.map(chunk => {
      keys(chunk, chunk.kind === 'buffer' ? ['kind', 'hex'] : ['kind', 'text']);
      if (chunk.kind === 'buffer') return bytesFromHex(chunk.hex);
      assert.equal(chunk.kind, 'string'); assert.equal(typeof chunk.text, 'string'); assert(Buffer.byteLength(chunk.text, 'utf8') <= 16384); return chunk.text;
    }) }];
  } else {
    keys(test.wire, test.wire.kind === 'utf8' ? ['kind', 'text'] : ['kind', 'hex']); keys(test.fragments, ['mode']);
    const bytes = test.wire.kind === 'utf8' ? (assert.equal(typeof test.wire.text, 'string'), Buffer.from(test.wire.text, 'utf8')) : (assert.equal(test.wire.kind, 'hex'), bytesFromHex(test.wire.hex));
    assert(bytes.length <= 16384);
    if (test.fragments.mode === 'whole-buffer') probes = [{ probeId: 'whole-buffer', chunks: [bytes] }];
    else if (test.fragments.mode === 'every-byte') probes = [{ probeId: 'every-byte', chunks: [...bytes].map(byte => Buffer.from([byte])) }];
    else {
      assert.equal(test.fragments.mode, 'all-two-chunk-splits'); assert(bytes.length <= 512);
      probes = Array.from({ length: bytes.length + 1 }, (_, offset) => ({ probeId: `split-${offset}`, chunks: [bytes.subarray(0, offset), bytes.subarray(offset)] }));
    }
  }
  if (Object.hasOwn(test, 'detachAfterChunk')) assert(Number.isSafeInteger(test.detachAfterChunk) && test.detachAfterChunk >= 0 && probes.every(probe => test.detachAfterChunk < probe.chunks.length));
  return probes;
}
async function observeReader(reference, test, probe) {
  const stream = Readable.from(probe.chunks, { objectMode: true });
  const callbacks = [], chunkSnapshots = [], detachSnapshots = []; let chunkIndex = -1, phase = 'before-data';
  stream.on('data', () => { chunkIndex++; phase = 'data'; });
  stream.once('end', () => { phase = 'end'; });
  const counts = () => ({ data: stream.listenerCount('data'), end: stream.listenerCount('end') });
  const beforeAttach = counts();
  const detach = reference.attachJsonlLineReader(stream, line => {
    assert.equal(typeof line, 'string');
    callbacks.push({ index: callbacks.length, phase, chunkIndex: phase === 'data' ? chunkIndex : null, line, lineUtf8Sha256: hash(Buffer.from(line, 'utf8')) });
  });
  assert.equal(typeof detach, 'function'); const afterAttach = counts();
  stream.on('data', chunk => {
    chunkSnapshots.push({ chunkIndex, deliveredKind: typeof chunk === 'string' ? 'string' : 'buffer', callbackCount: callbacks.length });
    if (chunkIndex === test.detachAfterChunk) {
      const before = counts(); detach(); const after = counts(); detach();
      detachSnapshots.push({ at: 'authored-data-boundary', chunkIndex, before, after, afterSecondCall: counts() });
    }
  });
  await new Promise((resolveEnd, reject) => { stream.once('end', resolveEnd); stream.once('error', reject); });
  const beforeCleanup = counts(); detach(); const afterCleanup = counts();
  return { probeId: probe.probeId, authoredChunks: probe.chunks.map(chunk => typeof chunk === 'string' ? { kind: 'string', text: chunk } : { kind: 'buffer', hex: chunk.toString('hex') }), callbacks, chunkSnapshots, listenerObservations: { beforeAttach, afterAttach, detachSnapshots, beforeCleanup, afterCleanup } };
}
async function childCapture(root) {
  assert(process.execArgv.includes('--experimental-strip-types'), 'Explicit type stripping required');
  // The pre-imported, hash-pinned guard blocks network and child-process APIs.
  assert.equal(globalThis.fetch.name, 'denied', 'Child requires the accepted offline guard');
  const moduleUrl = pathToFileURL(join(root, modulePath)).href, loadedModules = [], builtins = new Set();
  registerHooks({
    resolve(specifier, context, nextResolve) {
      const result = nextResolve(specifier, context);
      assert(result.url.startsWith('node:') || result.url === moduleUrl, `Unexpected source module fallback: ${result.url}`);
      if (context.parentURL === moduleUrl && result.url.startsWith('node:')) builtins.add(result.url);
      return result;
    },
    load(url, context, nextLoad) {
      const result = nextLoad(url, context);
      if (url === moduleUrl) { const bytes = readFileSync(join(root, modulePath)); assert.equal(hash(bytes), modulePin.sha256); loadedModules.push({ ...modulePin }); }
      return result;
    }
  });
  const reference = await import(moduleUrl);
  assert.deepEqual(Object.keys(reference).sort(), ['attachJsonlLineReader', 'serializeJsonLine']);
  const input = inputCheck(), readerCases = []; let probeCount = 0, callbackCount = 0;
  for (const test of input.readerCases) {
    const probes = [];
    for (const probe of readerProbes(test)) { const observed = await observeReader(reference, test, probe); probes.push(observed); probeCount++; callbackCount += observed.callbacks.length; }
    readerCases.push({ caseId: test.caseId, probes });
  }
  const serializerCases = input.serializerCases.map(test => {
    keys(test, ['caseId', 'value']); const serialized = reference.serializeJsonLine(test.value);
    assert.equal(typeof serialized, 'string'); const bytes = Buffer.from(serialized, 'utf8');
    return { caseId: test.caseId, serialized, utf8Hex: bytes.toString('hex'), bytes: bytes.length, utf8Sha256: hash(bytes) };
  });
  assert.deepEqual(loadedModules, [modulePin]); assert.deepEqual([...builtins].sort(), ['node:string_decoder']);
  assert.equal(fileHash(join(root, modulePath)), modulePin.sha256); assert.equal(fileHash(inputPath), inputSha);
  return { observations: { readerCases, serializerCases, checks: { readerCaseCount: readerCases.length, readerProbeCount: probeCount, callbackCount, serializerCaseCount: serializerCases.length, unchangedWholeModule: true, publicExports: ['attachJsonlLineReader', 'serializeJsonLine'], externalPackagesLoaded: 0, networkAndChildProcessesBlocked: true, jsonAdmissionInvoked: false, clockOrRngOverride: false } }, loadedModules, sourceRuntimeBuiltins: [...builtins].sort() };
}
export function parseCaptureArguments(args) {
  let first = false, root;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--upstream' && root === undefined && args[index + 1] && !args[index + 1].startsWith('--')) root = args[++index];
    else throw new Error('Usage: qualified-node capture-rpc-jsonl.mjs [--capture-new] [--upstream APPROVED_CLEAN_SOURCE]');
  }
  return { first, root };
}
function childEnvironment(root, scratch) {
  const home = join(scratch, 'home');
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: scratch, TEMP: scratch, TZ: 'UTC', PISHARP_PUBLIC_REFERENCE_ROOT: validatedReferenceRoot(), PISHARP_RPC_JSONL_SOURCE: root };
}
async function parentCapture(args) {
  const settings = parseCaptureArguments(args), root = sourceSelection(settings.root), input = inputCheck();
  const before = sourceCheck(root), harnessPaths = ['tools/PiReferenceRunner/capture-rpc-jsonl.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs'];
  const harnessFiles = () => harnessPaths.map(path => ({ path, bytes: readFileSync(join(repo, path)).length, sha256: fileHash(join(repo, path)) }));
  const harness = harnessFiles(), rawNames = ['capture-1.raw.json', 'capture-2.raw.json'];
  if (settings.first) assert(![expectedPath, lockPath, manifestPath, ...rawNames.map(name => join(repo, family, name))].some(existsSync), 'First capture refuses any existing golden, lock, manifest or raw capture');
  else {
    const manifest = readJson(manifestPath); assert.equal(manifest.fixtureId, fixtureId); assert.equal(manifest.sourceSha, sourceSha); assert.equal(manifest.normalizerVersion, normalizer);
    assert.equal(manifest.input.path, `${family}/core.input.json`); assert.equal(manifest.input.sha256, inputSha); assert.equal(manifest.expected.path, `${family}/core.expected.json`); assert.equal(manifest.lock.path, `${family}/oracle.lock.json`);
    assert.equal(fileHash(expectedPath), manifest.expected.sha256); assert.equal(fileHash(lockPath), manifest.lock.sha256);
    const lock = readJson(lockPath); assert(same(currentPublicHarnessPins(lock.harnessFiles), harness), 'Frozen harness differs'); assert(same(lock.sourceChecks.before, before) && same(lock.sourceChecks.after, before), 'Recorded source state differs');
    assert(same(lock.environmentPins, { sourceSha, sourceTree, executedSource: { ...modulePin, canonicalGitBlobSha256: modulePin.sha256 }, runtime: { version: process.version, path: runtimePath, sha256: runtimeSha, flags: ['--experimental-strip-types', '--disable-warning=ExperimentalWarning'] }, platform: process.platform, architecture: process.arch, externalDependencies: [] }), 'Frozen source/runtime pins differ');
    assert(Array.isArray(manifest.rawCaptures) && manifest.rawCaptures.length === rawNames.length);
    for (const [index, row] of manifest.rawCaptures.entries()) {
      keys(row, ['path', 'bytes', 'sha256']); assert.equal(row.path, `${family}/${rawNames[index]}`);
      assert(Number.isSafeInteger(row.bytes) && row.bytes > 0); assert(/^[a-f0-9]{64}$/.test(row.sha256));
      assert.equal(fileHash(join(repo, row.path)), row.sha256); assert.equal(readFileSync(join(repo, row.path)).length, row.bytes);
    }
    assert(same(lock.captureHistory, [{ kind: 'initial genuine capture', goldenSha256: manifest.expected.sha256, sourceAndDependencyBytesChanged: false }]), 'Initial capture history changed');
  }
  const outputRoot = join(repo, 'artifacts/rpc-jsonl-reference'), scratchRoot = join(outputRoot, 'scratch'); mkdirSync(scratchRoot, { recursive: true });
  const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-'));
    try {
      mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--disable-warning=ExperimentalWarning', '--import', pathToFileURL(guardPath).href, ownPath, '--child'], { cwd: join(scratch, 'workspace'), env: childEnvironment(root, scratch), windowsHide: true, timeout: 30000, maxBuffer: 16 * 1024 * 1024 });
      assert.equal(child.status, 0, child.error?.message ?? child.stderr.toString()); assert(Buffer.isBuffer(child.stdout)); captures.push(child.stdout);
    } finally {
      const target = realpathSync(scratch); assert(inside(realpathSync(scratchRoot), target), 'Scratch cleanup confinement failed'); rmSync(target, { recursive: true, force: true });
    }
  }
  assert(captures[0].equals(captures[1]), 'Two fresh whole-export captures differ');
  const captured = parseJsonSupported(captures[0].toString('utf8')), after = sourceCheck(root);
  assert(same(before, after)); assert(same(harness, harnessFiles())); assert.equal(fileHash(inputPath), inputSha);
  assert.deepEqual(captured.loadedModules, [modulePin]); assert.deepEqual(captured.sourceRuntimeBuiltins, ['node:string_decoder']);
  const golden = { schemaVersion: 1, fixtureId, sourceSha, kind, observations: captured.observations }, actual = jsonBytes(golden);
  const rawCaptures = rawNames.map((name, index) => ({ path: `${family}/${name}`, bytes: captures[index].length, sha256: hash(captures[index]) }));
  if (settings.first) {
    const lock = { schemaVersion: 1, environmentPins: { sourceSha, sourceTree, executedSource: { ...modulePin, canonicalGitBlobSha256: modulePin.sha256 }, runtime: { version: process.version, path: runtimePath, sha256: runtimeSha, flags: ['--experimental-strip-types', '--disable-warning=ExperimentalWarning'] }, platform: process.platform, architecture: process.arch, externalDependencies: [] }, harnessFiles: harness, loadedModules: captured.loadedModules, sourceRuntimeBuiltins: captured.sourceRuntimeBuiltins, sourceChecks: { before, after }, rawCaptures, captureHistory: [{ kind: 'initial genuine capture', goldenSha256: hash(actual), sourceAndDependencyBytesChanged: false }] };
    writeFileSync(expectedPath, actual, { flag: 'wx' });
    for (let index = 0; index < rawNames.length; index++) writeFileSync(join(repo, family, rawNames[index]), captures[index], { flag: 'wx' });
    writeFileSync(lockPath, jsonBytes(lock), { flag: 'wx' });
    writeFileSync(manifestPath, jsonBytes({ schemaVersion: 1, fixtureId, sourceSha, requirementIds: input.requirementIds, clock: input.clock, seed: input.seed, normalizerVersion: normalizer, input: { path: `${family}/core.input.json`, sha256: inputSha, kind: input.kind }, expected: { path: `${family}/core.expected.json`, sha256: hash(actual), kind }, lock: { path: `${family}/oracle.lock.json`, sha256: fileHash(lockPath) }, rawCaptures, provenance: { source: modulePath, sourceSha256: modulePin.sha256, exports: ['attachJsonlLineReader', 'serializeJsonLine'], mechanism: 'Whole unchanged public exports with actual authored Node Readable buffer/string events; no copied splitting or JSON parsing', repeatRuns: 2, byteIdentical: true, dependenciesInstalled: false, sourceModified: false, privateFunctionExtraction: false, networkOrProviderCalls: false, captureCommand: 'node tools/PiReferenceRunner/capture-rpc-jsonl.mjs --capture-new --upstream APPROVED_CLEAN_SOURCE' }, scope: 'Source-only public line callbacks, cleanup and serializer bytes; no native JSON admission/dispatcher/process IO/backpressure/full RPC qualification' }), { flag: 'wx' });
  } else {
    const manifest = readJson(manifestPath), lock = readJson(lockPath);
    assert(same(lock.loadedModules, captured.loadedModules) && same(lock.sourceRuntimeBuiltins, captured.sourceRuntimeBuiltins)); assert(same(lock.rawCaptures, rawCaptures) && same(manifest.rawCaptures, rawCaptures), 'Retained raw capture pins differ from fresh output');
    for (let index = 0; index < rawNames.length; index++) assert(readFileSync(join(repo, family, rawNames[index])).equals(captures[index]), 'Fresh raw capture differs from retained bytes');
    assert.equal(fileHash(lockPath), manifest.lock.sha256);
  }
  const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actual.toString('utf8')); writeFileSync(join(outputRoot, 'core.actual.json'), actual);
  const report = { publicDerivativeVerification: publicDerivativeVerificationScope, schemaVersion: 1, fixtureId, sourceSha, capturedInitialGolden: settings.first, repeatRuns: 2, byteIdentical: true, matched, goldenSha256: fileHash(expectedPath), lockSha256: fileHash(lockPath), inputSha256: inputSha, harnessSha256: fileHash(ownPath), sourceCleanAfter: true, loadedSourceModules: captured.loadedModules.length, sourceRuntimeBuiltins: captured.sourceRuntimeBuiltins, ...captured.observations.checks, scope: 'Genuine whole-export framing observations only; JSON admission and native RPC acceptance remain separate' };
  writeFileSync(join(outputRoot, 'report.json'), jsonBytes(report)); console.log(JSON.stringify(report, null, 2)); assert(matched, 'Fresh callbacks/serialization differ from immutable golden');
}
export async function main(args = process.argv.slice(2)) {
  runtimeCheck();
  if (args[0] === '--child') { assert.deepEqual(args, ['--child']); console.log(JSON.stringify(await childCapture(sourceSelection(process.env.PISHARP_RPC_JSONL_SOURCE)))); }
  else await parentCapture(args);
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === resolve(ownPath).toLowerCase()) await main();
