// Capture the whole unchanged exported queue with authored callbacks/gates and
// an owned real filesystem. Default verifies; first-new creation never updates.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, statSync, symlinkSync, linkSync, writeFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const family = 'fixtures/pi-v0.99.1/file-mutation-queue';
const inputPath = join(repo, family, 'core.input.json'), expectedPath = join(repo, family, 'core.expected.json');
const manifestPath = join(repo, family, 'manifest.json'), lockPath = join(repo, family, 'oracle.lock.json');
const sourceSha = 'd86654abb8862e201933517d6f1fce9f88dd117f', sourceTree = '200bd10bb146773516f862a02b8aaebeed163e00';
const sourceRelative = 'packages/coding-agent/src/core/tools/file-mutation-queue.ts';
const sourceHash = '33cb06ac9bcdf32c8b84d9d12e33be44c503a7670f668e003a3262cd34294d11';
const nodeHash = '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237';
const guardRelative = 'tools/PiReferenceRunner/offline-guard.mjs', guardHash = 'ae3741bdce496451bd04afcf8628df6ddc5bc5cfad8af6ee5e52cbc7b065a094';
const rawRelative = 'tools/CompatibilityReport/raw-json.mjs', rawHash = '58c378290d0be114e740dadda934d9a57e16a9321ae05eb8e34320e11d561ee9';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const readJson = path => parseJsonSupported(readFileSync(path, 'utf8'));
const encode = value => JSON.stringify(value, null, 2) + '\n';
const same = (left, right) => compareRawJson(JSON.stringify(left), JSON.stringify(right));
function inside(root, path) { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); }
function noLinks(path) {
  let current = resolve(path);
  while (true) { if (existsSync(current)) assert(!lstatSync(current).isSymbolicLink(), 'Unexpected filesystem link/junction'); const parent = dirname(current); if (parent === current) return; current = parent; }
}
function gate() { let release; const promise = new Promise(done => { release = done; }); return { promise, release }; }
function errorFields(error) { const result = { name: error.name, message: error.message }; if ('code' in error) result.code = error.code; return result; }
async function outcome(promise) { try { return { status: 'fulfilled', value: await promise }; } catch (error) { return { status: 'rejected', error: errorFields(error) }; } }
function cleanEnvironment(upstream, scratch) {
  const home = join(scratch, 'home');
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', HOME: home, USERPROFILE: home, APPDATA: home, LOCALAPPDATA: home, TMP: scratch, TEMP: scratch, TZ: 'UTC', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(home, 'gitconfig'), GIT_OPTIONAL_LOCKS: '0', PISHARP_QUEUE_UPSTREAM: upstream, PISHARP_QUEUE_WORKSPACE: join(scratch, 'workspace') };
}
async function captureChild() {
  const upstream = resolve(process.env.PISHARP_QUEUE_UPSTREAM), workspace = resolve(process.env.PISHARP_QUEUE_WORKSPACE);
  assert.equal(resolve(process.cwd()), workspace);
  noLinks(workspace); assert(lstatSync(workspace).isDirectory());
  const sourcePath = join(upstream, sourceRelative), loaded = new Map(), builtins = new Set();
  registerHooks({
    resolve(specifier, context, nextResolve) {
      const result = nextResolve(specifier, context);
      if (result.url.startsWith('node:')) builtins.add(result.url);
      else assert.equal(result.url, pathToFileURL(sourcePath).href, 'Only unchanged whole queue source may resolve');
      return result;
    },
    load(url, context, nextLoad) {
      const result = nextLoad(url, context);
      if (url.startsWith('file:')) { assert.equal(url, pathToFileURL(sourcePath).href); const bytes = readFileSync(fileURLToPath(url)); loaded.set(sourceRelative, { path: sourceRelative, bytes: bytes.length, sha256: hash(bytes) }); }
      return result;
    }
  });
  const fixture = readJson(inputPath), { withFileMutationQueue } = await import(pathToFileURL(sourcePath).href);
  const cases = [];
  const makeFile = path => { assert(inside(workspace, path)); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, fixture.fileUtf8, { flag: 'wx' }); };
  async function probe(label, firstPath, secondPath, returns, failSecond = false) {
    const trace = [], firstStarted = gate(), workRelease = gate(), cleanupStarted = gate(), cleanupRelease = gate();
    const unrelatedPath = join(workspace, label, 'unrelated.txt'); makeFile(unrelatedPath);
    let secondStarted = false;
    const first = outcome(withFileMutationQueue(firstPath, async () => {
      trace.push('first:start'); firstStarted.release(); await workRelease.promise;
      trace.push('first:work-complete'); cleanupStarted.release(); await cleanupRelease.promise;
      trace.push('first:cleanup-complete'); return structuredClone(returns.first);
    }));
    await firstStarted.promise;
    const second = outcome(withFileMutationQueue(secondPath, async () => {
      secondStarted = true; trace.push('second:start');
      if (failSecond) { trace.push('second:throw'); const failure = new Error(fixture.callbackFailure.message); failure.name = fixture.callbackFailure.name; throw failure; }
      trace.push('second:complete'); return structuredClone(returns.second);
    }));
    // C is registered after B. Its start/completion proves the source's serial
    // registration barrier passed B while A remains held by explicit gates.
    const unrelated = outcome(withFileMutationQueue(unrelatedPath, async () => { trace.push('unrelated:start'); trace.push('unrelated:complete'); return structuredClone(returns.unrelated); }));
    const unrelatedResult = await unrelated;
    const checkpoints = [{ at: 'unrelated-completed-while-first-work-blocked', secondStarted }];
    workRelease.release(); await cleanupStarted.promise;
    checkpoints.push({ at: 'first-cleanup-blocked', secondStarted });
    cleanupRelease.release();
    const [firstResult, secondResult] = await Promise.all([first, second]);
    const subsequent = await outcome(withFileMutationQueue(firstPath, async () => { trace.push('subsequent:start'); trace.push('subsequent:complete'); return structuredClone(returns.subsequent); }));
    return { probeId: label, checkpoints, trace, outcomes: { first: firstResult, second: secondResult, unrelated: unrelatedResult, subsequent } };
  }
  let junctionTarget, junctionLink, original, hardlink;
  for (const test of fixture.cases) {
    if (test.probe === 'existing-fifo') {
      const file = join(workspace, 'existing', 'target.txt'); makeFile(file);
      cases.push({ caseId: test.caseId, probes: [await probe('existing-fifo', file, file, test.returns, true)] });
    } else if (test.probe === 'filesystem-aliases') {
      assert.equal(process.platform, 'win32', 'First alias profile requires Windows directory junctions');
      junctionTarget = join(workspace, 'junction-target'); junctionLink = join(workspace, 'junction-alias');
      original = join(junctionTarget, 'existing.txt'); hardlink = join(workspace, 'hardlink.txt'); makeFile(original);
      assert(inside(workspace, junctionTarget) && inside(workspace, junctionLink));
      // Ordinary user junction creation only; any failure blocks capture and is
      // reported with its original error. No admin/symlink policy workaround.
      symlinkSync(junctionTarget, junctionLink, 'junction');
      assert.equal(realpathSync(junctionLink), realpathSync(junctionTarget));
      assert(inside(realpathSync(workspace), realpathSync(junctionLink)), 'Junction target escapes owned layout');
      linkSync(original, hardlink);
      const originalStat = statSync(original), hardlinkStat = statSync(hardlink);
      cases.push({ caseId: test.caseId, filesystemObservations: { junctionResolvesToOwnedTarget: true, existingJunctionFileRealpathsEqual: realpathSync(original) === realpathSync(join(junctionLink, 'existing.txt')), hardlinkStatIdentityEqual: originalStat.dev === hardlinkStat.dev && originalStat.ino === hardlinkStat.ino, hardlinkRealpathsEqual: realpathSync(original) === realpathSync(hardlink), fileUtf8Sha256: hash(Buffer.from(fixture.fileUtf8)) }, probes: [await probe('junction-existing', original, join(junctionLink, 'existing.txt'), test.returns), await probe('hardlink-existing', original, hardlink, test.returns)] });
    } else if (test.probe === 'missing-fallback') {
      assert(junctionTarget && junctionLink, 'Authored missing-alias probe requires prior owned junction');
      const missing = join(workspace, 'missing.txt'), lexical = join(workspace, 'existing') + sep + '..' + sep + 'missing.txt';
      assert(!existsSync(missing) && !existsSync(join(junctionTarget, 'missing.txt')));
      cases.push({ caseId: test.caseId, probes: [await probe('missing-normalized', missing, lexical, test.returns), await probe('missing-junction-alias', join(junctionTarget, 'missing.txt'), join(junctionLink, 'missing.txt'), test.returns)] });
    } else if (test.probe === 'resolver-rejection') {
      assert(isAbsolute(fixture.invalidResolverPath) && fixture.invalidResolverPath.includes('\0'), 'Resolver failure input must be absolute and invalid before filesystem access');
      let callbackInvoked = false;
      const rejected = await outcome(withFileMutationQueue(fixture.invalidResolverPath, async () => { callbackInvoked = true; return 'not-executed'; }));
      const afterFailure = await outcome(withFileMutationQueue(original, async () => structuredClone(test.returns.afterFailure)));
      cases.push({ caseId: test.caseId, callbackInvoked, outcomes: { rejected, afterFailure } });
    } else throw new Error('Unknown authored probe');
  }
  assert.equal(cases.length, 4); assert.equal(loaded.size, 1);
  return { observations: { cases, filesystemScope: 'Only this child owned layout; callback/return labels are authored actual function inputs', clock: 'No clock replacement, sleeps or race-based deadlines', checks: { caseCount: cases.length, probeCount: cases.reduce((sum, test) => sum + (test.probes?.length ?? 0), 0), wholeUnchangedModule: true, externalPackagesLoaded: 0, networkAndChildProcessesBlocked: true } }, loadedModules: [...loaded.values()], loadedBuiltins: [...builtins].sort() };
}
function verifySource(upstream, environment) {
  noLinks(upstream);
  const git = (...args) => {
    const result = spawnSync('C:\\Program Files\\Git\\cmd\\git.exe', ['-c', `safe.directory=${upstream}`, '-C', upstream, ...args], { env: environment, windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
    assert.equal(result.status, 0, 'Pinned public source inspection failed'); return result.stdout;
  };
  assert.equal(git('rev-parse', 'HEAD').toString().trim(), sourceSha);
  assert.equal(git('rev-parse', 'HEAD^{tree}').toString().trim(), sourceTree);
  assert.equal(git('status', '--porcelain=v1', '--untracked-files=all').toString().trim(), '');
  assert.equal(fileHash(join(upstream, sourceRelative)), sourceHash);
  const blob = git('show', sourceSha + ':' + sourceRelative); assert.equal(hash(blob), sourceHash);
  return { sourceSha, sourceTree, executedSource: { path: sourceRelative, bytes: blob.length, sha256: sourceHash, canonicalGitBlobSha256: hash(blob) } };
}
async function captureParent(args) {
  let first = false, upstream;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--upstream' && !upstream && args[index + 1] && !args[index + 1].startsWith('--')) upstream = resolve(args[++index]);
    else throw new Error('Usage: node capture-file-mutation-queue.mjs [--capture-new] [--upstream PINNED_PUBLIC_CHECKOUT]');
  }
  upstream ??= resolve(repo, '../Pi-reference-oracle-v0.99.1/upstream');
  assert.equal(process.platform, 'win32'); assert.equal(process.version, 'v24.19.0'); assert.equal(fileHash(process.execPath), nodeHash);
  assert.equal(fileHash(join(repo, guardRelative)), guardHash); assert.equal(fileHash(join(repo, rawRelative)), rawHash);
  const fixture = readJson(inputPath); assert.equal(fixture.fixtureId, 'file-mutation-queue-core'); assert.equal(fixture.sourceSha, sourceSha);
  const scratchRoot = join(repo, 'artifacts/file-mutation-queue-reference/scratch'), outputRoot = dirname(scratchRoot);
  noLinks(scratchRoot);
  const sourceBefore = verifySource(upstream, cleanEnvironment(upstream, scratchRoot));
  const harnessFiles = ['tools/PiReferenceRunner/capture-file-mutation-queue.mjs', guardRelative, rawRelative].map(path => ({ path, sha256: fileHash(join(repo, path)), bytes: readFileSync(join(repo, path)).length }));
  const environmentPins = { ...sourceBefore, runtime: { version: process.version, sha256: nodeHash }, platform: process.platform, architecture: process.arch, externalDependencies: [] };
  if (first) assert(![expectedPath, manifestPath, lockPath].some(existsSync), 'Initial capture refuses existing golden/manifest/lock');
  else {
    const manifest = readJson(manifestPath); assert.equal(manifest.fixtureId, fixture.fixtureId); assert.equal(manifest.sourceSha, sourceSha); assert.equal(manifest.normalizerVersion, fixture.normalizerVersion);
    assert.equal(manifest.input.path, family + '/core.input.json'); assert.equal(manifest.expected.path, family + '/core.expected.json'); assert.equal(manifest.lock.path, family + '/oracle.lock.json');
    assert.equal(fileHash(inputPath), manifest.input.sha256); assert.equal(fileHash(expectedPath), manifest.expected.sha256); assert.equal(fileHash(lockPath), manifest.lock.sha256);
    const locked = readJson(lockPath); assert(same(locked.environmentPins, environmentPins)); assert(same(locked.harnessFiles, harnessFiles));
    assert(same(locked.loadedModules, [{ path: sourceRelative, bytes: sourceBefore.executedSource.bytes, sha256: sourceHash }]));
  }
  mkdirSync(scratchRoot, { recursive: true }); noLinks(scratchRoot);
  const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-'));
    try {
      mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(repo, guardRelative)).href, ownPath, '--child'], { cwd: join(scratch, 'workspace'), env: cleanEnvironment(upstream, scratch), windowsHide: true, encoding: 'utf8', timeout: 20000, maxBuffer: 4 * 1024 * 1024 });
      assert.equal(child.status, 0, 'Whole queue capture failed or filesystem prerequisite blocked: ' + (child.error?.message ?? child.stderr));
      captures.push(parseJsonSupported(child.stdout));
    } finally {
      // Resolve and verify the absolute cleanup root before recursive removal.
      const target = resolve(scratch); assert(inside(scratchRoot, target)); noLinks(target);
      assert(inside(realpathSync(scratchRoot), realpathSync(target)), 'Resolved cleanup target escapes owned scratch root');
      rmSync(target, { recursive: true, force: true });
    }
  }
  assert.equal(encode(captures[0]), encode(captures[1]), 'Two genuine observations differ; no real output is normalized away');
  assert(same(verifySource(upstream, cleanEnvironment(upstream, scratchRoot)), sourceBefore), 'Pinned source changed during capture');
  assert(same(harnessFiles, ['tools/PiReferenceRunner/capture-file-mutation-queue.mjs', guardRelative, rawRelative].map(path => ({ path, sha256: fileHash(join(repo, path)), bytes: readFileSync(join(repo, path)).length }))), 'Harness changed during capture');
  const actual = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha, kind: 'captured-whole-file-mutation-queue-oracle', observations: captures[0].observations }, actualRaw = encode(actual);
  if (first) {
    writeFileSync(expectedPath, actualRaw, { flag: 'wx' });
    writeFileSync(lockPath, encode({ schemaVersion: 1, environmentPins, harnessFiles, loadedModules: captures[0].loadedModules, loadedBuiltins: captures[0].loadedBuiltins }), { flag: 'wx' });
    writeFileSync(manifestPath, encode({ schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha, requirementIds: fixture.requirementIds, clock: fixture.clock, seed: fixture.seed, normalizerVersion: fixture.normalizerVersion, input: { path: family + '/core.input.json', sha256: fileHash(inputPath), kind: fixture.kind }, expected: { path: family + '/core.expected.json', sha256: fileHash(expectedPath), kind: actual.kind }, lock: { path: family + '/oracle.lock.json', sha256: fileHash(lockPath) }, provenance: { source: sourceRelative, sourceSha256: sourceHash, mechanism: 'Whole unchanged exported withFileMutationQueue; authored callbacks/gates and actual owned Windows filesystem', repeatRuns: 2, byteIdentical: true, authoredExpectedOutput: false, sourceModified: false, privateFunctionExtraction: false, dependenciesInstalled: false, networkOrProviderCalls: false, actualTemporaryPathsReturnedBySource: false, observationProjection: 'Source callback order and returned values/errors; authored logical labels; selected filesystem relation booleans, no inode/path normalization', captureCommand: 'node tools/PiReferenceRunner/capture-file-mutation-queue.mjs --capture-new' }, scope: 'Four bounded source-only filesystem queue cases; no native differential/tool-write/durability/cancellation/cross-process/phase claim' }), { flag: 'wx' });
  } else {
    const locked = readJson(lockPath); assert(same(captures[0].loadedModules, locked.loadedModules)); assert(same(captures[0].loadedBuiltins, locked.loadedBuiltins));
  }
  const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
  writeFileSync(join(outputRoot, 'core.actual.json'), actualRaw);
  const report = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha, capturedInitialGolden: first, repeatRuns: 2, byteIdentical: true, matched, sourceCleanAfter: true, loadedUpstreamFiles: captures[0].loadedModules.length, externalPackages: 0, checks: actual.observations.checks, cases: actual.observations.cases.map(test => ({ caseId: test.caseId, checkpoints: test.probes?.map(probe => ({ probeId: probe.probeId, checkpoints: probe.checkpoints })), resolverOutcomes: test.probe ? undefined : test.outcomes })), scope: 'Whole unchanged upstream queue on owned filesystem; no native differential or full phase acceptance' };
  writeFileSync(join(outputRoot, 'report.json'), encode(report)); console.log(encode(report)); assert(matched, 'Observation differs from frozen golden');
}
if (process.argv[2] === '--child') { assert.equal(process.argv.length, 3); console.log(JSON.stringify(await captureChild())); }
else await captureParent(process.argv.slice(2));
