// Genuine unchanged whole edit/edit-diff public helpers and factory default real FS.
// Default verifies frozen output; --capture-new refuses any existing golden/lock.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const family = 'fixtures/pi-v0.99.1/edit';
const inputPath = join(repo, family, 'core.input.json'), expectedPath = join(repo, family, 'core.expected.json');
const manifestPath = join(repo, family, 'manifest.json'), lockPath = join(repo, family, 'oracle.lock.json');
const planPath = join(repo, 'compatibility/edit-oracle-plan.json');
const planSha256 = 'b9127920b5db8b1b0cdf2ed24babfa943fe7bc79d3e842a2f28bbde6d91dffb3';
const setupPath = join(repo, 'tools/PiReferenceRunner/setup-edit-oracle.mjs');
const setupSha256 = '4337663e7938a100937e3ea442ffe334bec3646b98b50266ee116401069c827e';
const restoredReceiptSha256 = '2c1a92e8cc89f344aa397a12cd2708e2909792ad81ec402a3570b7863e110bea';
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
const fileHash = path => hash(readFileSync(path));
const readJson = path => parseJsonSupported(readFileSync(path, 'utf8'));
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
const rawJson = value => JSON.stringify(value, null, 2) + '\n';
const packages = ['chalk', 'cross-spawn', 'diff', 'get-east-asian-width', 'highlight.js', 'isexe', 'marked', 'partial-json', 'path-key', 'shebang-command', 'shebang-regex', 'typebox', 'which'];
const harnessPaths = ['tools/PiReferenceRunner/capture-edit.mjs', 'tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/PiReferenceRunner/setup-edit-oracle.mjs', 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs', 'compatibility/edit-oracle-plan.json', 'tools/CompatibilityReport/raw-json.mjs'];
function inside(root, path) { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); }
function cleanEnvironment(oracle, scratch) {
  const home = join(scratch, 'home');
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: scratch, TEMP: scratch, TZ: 'UTC', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(oracle, 'config', 'gitconfig'), GIT_OPTIONAL_LOCKS: '0', PISHARP_REFERENCE_ORACLE: oracle };
}
function tree(root) {
  const files = [];
  const visit = directory => {
    assert(!lstatSync(directory).isSymbolicLink(), 'Dependency directory links rejected');
    for (const name of readdirSync(directory).sort()) {
      const path = join(directory, name), stat = lstatSync(path);
      assert(!stat.isSymbolicLink(), 'Dependency links rejected');
      if (stat.isDirectory()) visit(path);
      else { assert(stat.isFile(), 'Dependency special files rejected'); files.push({ path: relative(root, path).replaceAll('\\', '/'), bytes: stat.size, sha256: fileHash(path) }); }
    }
  };
  visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function undefinedPaths(value, path = '', result = []) {
  if (value && typeof value === 'object') for (const key of Object.keys(value)) {
    const child = path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1');
    if (value[key] === undefined) result.push(child); else undefinedPaths(value[key], child, result);
  }
  return result;
}
function noLinks(target) {
  let current = resolve(target);
  while (true) {
    try { assert(!lstatSync(current).isSymbolicLink(), 'Links or junctions rejected'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    const parent = dirname(current); if (parent === current) break; current = parent;
  }
}
function byteSnapshot(bytes) { return { bytes: bytes.length, sha256: hash(bytes), base64: bytes.toString('base64'), utf8: bytes.toString('utf8') }; }
function failureSnapshot(error) {
  const ownProperties = {};
  for (const key of Object.getOwnPropertyNames(error)) if (!['stack', 'name', 'message'].includes(key)) ownProperties[key] = structuredClone(error[key]);
  return { name: error.name, message: error.message, ownProperties };
}
async function observe(operation) {
  try { const value = await operation(); return { status: 'fulfilled', value: structuredClone(value), ownUndefinedPaths: undefinedPaths(value) }; }
  catch (error) { return { status: 'rejected', error: failureSnapshot(error) }; }
}
async function childCapture(oracle) {
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Child requires locked unchanged offline preload');
  const allowed = url => {
    if (!url.startsWith('file:')) return url.startsWith('node:');
    const file = fileURLToPath(url);
    return inside(join(oracle, 'upstream'), file) || packages.some(name => inside(join(oracle, 'node_modules', name), file));
  };
  registerHooks({ resolve(specifier, context, nextResolve) { const result = nextResolve(specifier, context); assert(allowed(result.url), 'Unreviewed module fallback rejected: ' + result.url); return result; } });
  const originalDate = Date, originalNow = Date.now, originalRandom = Math.random;
  const helpers = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/tools/edit-diff.ts')).href);
  const edit = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/tools/edit.ts')).href);
  const { splitBom } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/utils/text.ts')).href);
  assert.equal(typeof helpers.applyEditsToNormalizedContent, 'function'); assert.equal(typeof edit.createEditToolDefinition, 'function');
  const workspace = resolve(process.cwd()); assert.equal(workspace, resolve(process.env.TMP, 'workspace')); noLinks(workspace);
  const fixture = readJson(inputPath), definition = edit.createEditToolDefinition(workspace), cases = [];
  for (const test of fixture.cases) {
    assert(typeof test.path === 'string' && test.path.startsWith('files/') && !test.path.includes('..') && !test.path.includes('\\') && !test.path.includes(':') && !test.path.includes('\0'), 'Only authored confined relative file paths admitted');
    const file = resolve(workspace, test.path); assert(inside(workspace, file)); noLinks(file); assert(!existsSync(file));
    if (!test.missing) { assert.equal(typeof test.utf8, 'string'); mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, Buffer.from(test.utf8, 'utf8'), { flag: 'wx' }); }
    const before = existsSync(file) ? byteSnapshot(readFileSync(file)) : null;
    const supplied = { path: test.path, ...structuredClone(test.arguments) }, suppliedBefore = structuredClone(supplied);
    const prepared = definition.prepareArguments(supplied);
    assert.equal(prepared.path, test.path, 'Prepared tool path differs from admitted owned path');
    const preparation = { suppliedBefore, suppliedAfter: structuredClone(supplied), prepared: structuredClone(prepared), ownUndefinedPaths: undefinedPaths(prepared) };
    let helperObservations = null;
    if (!test.missing && Array.isArray(prepared.edits)) {
      const split = splitBom(test.utf8), ending = helpers.detectLineEnding(split.text), normalized = helpers.normalizeToLF(split.text);
      const normalization = { splitBom: structuredClone(split), detectedLineEnding: ending, normalized, fuzzyView: helpers.normalizeForFuzzyMatch(normalized) };
      const matching = await observe(() => helpers.applyEditsToNormalizedContent(normalized, structuredClone(prepared.edits), test.path));
      const firstText = prepared.edits[0]?.oldText;
      const fuzzyFind = typeof firstText === 'string' ? await observe(() => helpers.fuzzyFindText(normalized, helpers.normalizeToLF(firstText))) : null;
      const generated = matching.status === 'fulfilled' ? {
        display: await observe(() => helpers.generateDiffString(matching.value.baseContent, matching.value.newContent)),
        patch: await observe(() => helpers.generateUnifiedPatch(test.path, matching.value.baseContent, matching.value.newContent)),
        restoredLineEndings: helpers.restoreLineEndings(matching.value.newContent, ending)
      } : null;
      helperObservations = { normalization, matching, fuzzyFind, generated };
    }
    const preview = await observe(() => helpers.computeEditsDiff(test.path, structuredClone(prepared.edits), workspace));
    const result = await observe(() => definition.execute(test.caseId, prepared));
    const after = existsSync(file) ? byteSnapshot(readFileSync(file)) : null;
    cases.push({ caseId: test.caseId, path: test.path, preparation, helperObservations, preview, result, filesystem: { before, after, byteIdentical: before === null ? after === null : after !== null && before.base64 === after.base64 } });
  }
  assert.equal(Date, originalDate); assert.equal(Date.now, originalNow); assert.equal(Math.random, originalRandom);
  return {
    observations: {
      cases,
      checks: { caseCount: cases.length, wholeUnchangedEditAndDiffModules: true, factory: 'createEditToolDefinition', defaultRealFilesystemOperations: true, rendererOrNativeHelperInvoked: false, noClockOrRngOverride: true, networkAndProcessesBlocked: true },
      filesystemScope: 'Only this child verified-confined owned workspace; authored relative paths are actual source inputs',
      failureContract: 'Raw error name/message and all additional own properties; stack excluded from serializable contract observation, never normalized'
    },
    loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0)
  };
}
export function parseCaptureArguments(args) {
  let first = false, oracle;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--oracle' && !oracle && args[index + 1] && !args[index + 1].startsWith('--')) oracle = resolve(args[++index]);
    else throw new Error('Usage: node capture-edit.mjs [--capture-new] [--oracle APPROVED_EDIT_ORACLE]');
  }
  return { first, oracle };
}
export function readOnlySetupCheck(oracle, environment) {
  const result = spawnSync(process.execPath, [setupPath, '--check', '--oracle', oracle], { cwd: repo, env: environment, windowsHide: true, encoding: 'utf8', timeout: 20000, maxBuffer: 2 * 1024 * 1024 });
  assert.equal(result.status, 0, 'Read-only full canonical-source/npm/input preflight failed: ' + (result.error?.message ?? result.stderr));
  return parseJsonSupported(result.stdout);
}
async function parentCapture(args) {
  let { first, oracle } = parseCaptureArguments(args);
  assert.equal(fileHash(planPath), planSha256, 'Reviewed edit oracle plan changed');
  assert.equal(fileHash(setupPath), setupSha256, 'Accepted setup helper changed');
  const plan = readJson(planPath); oracle ??= resolve(plan.workspace.proposedRoot);
  assert.equal(oracle.toLowerCase(), resolve(plan.workspace.proposedRoot).toLowerCase(), 'Only the fresh approved edit oracle is admitted');
  assert.equal(process.version, plan.runtime.version); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  const restoredPath = join(oracle, '.pisharp-edit-restored.json');
  assert(existsSync(restoredPath), 'Root-confirmed successful restore record is required before edit execution');
  assert.equal(fileHash(restoredPath), restoredReceiptSha256, 'Root-confirmed immutable restore receipt changed');
  const restored = readJson(restoredPath);
  assert.equal(restored.status, 'thirteen exact dependencies installed; whole edit module qualification pending');
  assert.equal(restored.owner.planSha256, planSha256); assert.equal(restored.owner.helperSha256, setupSha256);
  const preflight = readOnlySetupCheck(oracle, cleanEnvironment(oracle, join(oracle, 'tmp')));
  assert(same(preflight.sourceFingerprint, restored.sourceFingerprint), 'Canonical/source checkout fingerprint differs from successful restore');
  assert(same(readdirSync(join(oracle, 'node_modules')).filter(name => name !== '.package-lock.json').sort(), packages), 'Unexpected dependency package');
  const { inspectPackageArchive } = await import(pathToFileURL(setupPath).href);
  const dependencies = plan.packages.map(row => {
    const archivePath = join(oracle, 'archives', `${row.name}-${row.lockEntry.version}.tgz`), bytes = readFileSync(archivePath);
    assert.equal('sha512-' + hash(bytes, 'sha512', 'base64'), row.lockEntry.integrity);
    const archive = inspectPackageArchive(bytes), files = tree(join(oracle, 'node_modules', row.name));
    const archiveFiles = [...archive.files].map(([path, data]) => ({ path, bytes: data.length, sha256: hash(data) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    assert(same(files, archiveFiles), 'Installed files differ from exact inspected archive: ' + row.name);
    const receipt = restored.installed.find(item => item.name === row.name);
    assert.equal(hash(Buffer.from(canonical(files))), receipt.files.sha256); assert.equal(files.length, receipt.files.files); assert.equal(hash(bytes), receipt.archiveSha256);
    for (const license of receipt.licenses) assert.equal(hash(archive.files.get(license.path)), license.sha256);
    return { name: row.name, version: row.lockEntry.version, archiveSha256: hash(bytes), integrity: row.lockEntry.integrity, files: receipt.files, manifestSha256: receipt.manifestSha256, licenses: receipt.licenses.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })) };
  });
  const inputShaBefore = fileHash(inputPath), fixture = readJson(inputPath); assert.equal(fixture.sourceSha, plan.source.commit);
  assert.equal(fixture.fixtureId, 'edit-core');
  assert.equal(fixture.schemaVersion, 1); assert.equal(fixture.cases.length, 12); assert.equal(new Set(fixture.cases.map(test => test.caseId)).size, fixture.cases.length);
  const harnessFiles = harnessPaths.map(path => ({ path, sha256: fileHash(join(repo, path)), bytes: readFileSync(join(repo, path)).length }));
  const environmentPins = { sourceSha: plan.source.commit, sourceFingerprint: preflight.sourceFingerprint, runtime: plan.runtime, platform: process.platform, architecture: process.arch, npmVersion: plan.packageManager.version, setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-edit-prepared.json')), projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')), dependencies };
  if (first) assert(![expectedPath, manifestPath, lockPath].some(existsSync), 'New capture refuses existing golden/manifest/lock');
  else {
    const manifest = readJson(manifestPath);
    assert.equal(manifest.fixtureId, fixture.fixtureId); assert.equal(manifest.sourceSha, fixture.sourceSha);
    assert.equal(manifest.normalizerVersion, fixture.normalizerVersion);
    assert.equal(manifest.input.path, family + '/core.input.json'); assert.equal(manifest.expected.path, family + '/core.expected.json'); assert.equal(manifest.lock.path, family + '/oracle.lock.json');
    assert.equal(fileHash(lockPath), manifest.lock.sha256, 'Immutable lock changed before execution');
    const lock = readJson(lockPath);
    assert(same(environmentPins, lock.environmentPins) && same(harnessFiles, lock.harnessFiles), 'Runtime/setup/harness bytes changed before execution');
    assert.equal(fileHash(inputPath), manifest.input.sha256); assert.equal(fileHash(expectedPath), manifest.expected.sha256);
    const expected = readJson(expectedPath);
    assert.equal(expected.schemaVersion, 1); assert.equal(expected.fixtureId, fixture.fixtureId); assert.equal(expected.sourceSha, fixture.sourceSha); assert.equal(expected.kind, 'captured-unchanged-edit-oracle');
    for (const file of lock.loadedModules) {
      assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || packages.some(name => file.path.startsWith(`node_modules/${name}/`))), 'Unreviewed locked module path rejected');
      assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Loaded module bytes changed before execution');
    }
  }
  const outputRoot = join(repo, 'artifacts/edit-reference'), scratchRoot = join(outputRoot, 'scratch'); noLinks(scratchRoot); mkdirSync(scratchRoot, { recursive: true }); noLinks(scratchRoot);
  const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-'));
    try {
      mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(repo, 'tools/PiReferenceRunner/full-preload.mjs')).href, ownPath, '--child'], { cwd: join(scratch, 'workspace'), env: cleanEnvironment(oracle, scratch), windowsHide: true, encoding: 'utf8', timeout: 20000, maxBuffer: 16 * 1024 * 1024 });
      assert.equal(child.status, 0, 'Genuine whole edit capture failed; do not substitute a module: ' + (child.error?.message ?? child.stderr));
      captures.push(parseJsonSupported(child.stdout));
    } finally {
      const target = resolve(scratch); noLinks(target); assert(inside(resolve(scratchRoot), target) && inside(realpathSync(scratchRoot), realpathSync(target)), 'Scratch cleanup confinement failed'); rmSync(target, { recursive: true, force: true });
    }
  }
  assert.equal(rawJson(captures[0]), rawJson(captures[1]), 'Two captures differ; no random/opaque field may be normalized away');
  const loadedModules = captures[0].loadedModules;
  for (const required of ['upstream/packages/coding-agent/src/core/tools/edit.ts', 'upstream/packages/coding-agent/src/core/tools/edit-diff.ts', 'upstream/packages/coding-agent/src/core/tools/renderers/edit.ts', 'upstream/packages/tui/src/index.ts']) assert(loadedModules.some(file => file.path === required), 'Whole unchanged module load absent: ' + required);
  for (const name of ['diff', 'cross-spawn', 'marked', 'get-east-asian-width', 'chalk', 'highlight.js', 'typebox']) assert(loadedModules.some(file => file.path.startsWith('node_modules/' + name + '/')), 'Actual dependency load absent: ' + name);
  const sourceHashes = [], gitEnvironment = cleanEnvironment(oracle, join(oracle, 'tmp'));
  for (const file of loadedModules) {
    assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Loaded module changed after execution');
    if (file.path.startsWith('upstream/')) {
      const path = file.path.slice('upstream/'.length), git = spawnSync('C:\\Program Files\\Git\\cmd\\git.exe', ['-c', `safe.directory=${join(oracle, 'upstream')}`, '-C', join(oracle, 'upstream'), 'show', plan.source.commit + ':' + path], { env: gitEnvironment, windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
      assert.equal(git.status, 0); assert.equal(hash(git.stdout), file.sha256, 'Executed source bytes differ from canonical Git blob'); sourceHashes.push({ ...file, canonicalGitBlobSha256: hash(git.stdout) });
    } else assert(packages.some(name => file.path.startsWith(`node_modules/${name}/`)), 'Unexpected loaded package');
  }
  const after = readOnlySetupCheck(oracle, gitEnvironment);
  assert(same(after.sourceFingerprint, preflight.sourceFingerprint), 'Source changed during capture');
  for (const row of dependencies) assert.equal(hash(Buffer.from(canonical(tree(join(oracle, 'node_modules', row.name))))), row.files.sha256, 'Installed dependency changed during capture');
  assert(same(harnessFiles, harnessPaths.map(path => ({ path, sha256: fileHash(join(repo, path)), bytes: readFileSync(join(repo, path)).length }))), 'Harness changed during capture');
  assert.equal(fileHash(inputPath), inputShaBefore, 'Authored input changed during capture');
  const golden = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: 'captured-unchanged-edit-oracle', observations: captures[0].observations }, actualRaw = rawJson(golden);
  if (first) {
    writeFileSync(expectedPath, actualRaw, { flag: 'wx' });
    writeFileSync(lockPath, rawJson({ schemaVersion: 1, environmentPins, harnessFiles, loadedModules, sourceHashes, captureHistory: [{ kind: 'initial genuine capture', goldenSha256: hash(Buffer.from(actualRaw)), sourceAndDependencyBytesChanged: false }] }), { flag: 'wx' });
    writeFileSync(manifestPath, rawJson({ schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, requirementIds: fixture.requirementIds, clock: fixture.clock, seed: fixture.seed, normalizerVersion: fixture.normalizerVersion, input: { path: family + '/core.input.json', sha256: fileHash(inputPath), kind: fixture.kind }, expected: { path: family + '/core.expected.json', sha256: fileHash(expectedPath), kind: golden.kind }, lock: { path: family + '/oracle.lock.json', sha256: fileHash(lockPath) }, provenance: { source: 'Unchanged whole packages/coding-agent/src/core/tools/edit.ts and edit-diff.ts public exports', dependencies: 'Exact thirteen upstream-lock archives and fully verified installed files', sourceResolver: 'Unchanged canonical experimental/source-resolver.ts', observations: 'Public preparation/matching/normalization/preview/diff calls and whole factory default real filesystem execute; complete returned results, byte snapshots and contract failures; own undefined fields recorded', input: 'authored edit inputs and confined temporary files, no user files or authored expected output', credentials: 'explicit credential-free offline child environment', clock: 'no clock/RNG override, sleeps or generated timestamp inputs', repeatRuns: 2, byteIdentical: true, captureCommand: 'node tools/PiReferenceRunner/capture-edit.mjs --capture-new --oracle APPROVED_EDIT_ORACLE' }, scope: 'Twelve small edit cases; whole unchanged source and actual default filesystem effects; no renderer/native invocation, native matcher/formatter/cancellation/conflict parity or phase closure' }), { flag: 'wx' });
  } else {
    const locked = readJson(lockPath); assert(same(loadedModules, locked.loadedModules) && same(sourceHashes, locked.sourceHashes), 'Loaded closure changed');
    assert.equal(fileHash(lockPath), readJson(manifestPath).lock.sha256, 'Immutable lock changed');
  }
  const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
  writeFileSync(join(outputRoot, 'core.actual.json'), actualRaw);
  const report = {
    schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha,
    capturedInitialGolden: first, repeatRuns: 2, byteIdentical: true, matched,
    sourceCleanAfter: true, authoredInputUnchanged: true, wholeEditAndDiffModulesLoaded: true,
    installedPackageFiles: dependencies.reduce((sum, row) => sum + row.files.files, 0),
    loadedUpstreamFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length,
    loadedDependencyCounts: Object.fromEntries(packages.map(name => [name, loadedModules.filter(file => file.path.startsWith('node_modules/' + name + '/')).length])),
    cases: golden.observations.cases.map(test => ({ caseId: test.caseId, helperStatus: test.helperObservations?.matching.status ?? null, previewStatus: test.preview.status, toolStatus: test.result.status, failure: test.result.error ?? null, inputBytes: test.filesystem.before?.bytes ?? null, outputBytes: test.filesystem.after?.bytes ?? null, originalBytesPreserved: test.filesystem.byteIdentical })),
    scope: 'Genuine unchanged whole edit source and public factory default owned-filesystem effects; no renderer/native invocation or native matcher/formatter/conflict/cancellation/full phase parity claim'
  };
  writeFileSync(join(outputRoot, 'report.json'), rawJson(report)); console.log(rawJson(report)); assert(matched, 'Actual output differs from immutable golden');
}
export async function main(args = process.argv.slice(2)) {
  if (args[0] === '--child') {
    assert.equal(args.length, 1);
    console.log(JSON.stringify(await childCapture(resolve(process.env.PISHARP_REFERENCE_ORACLE))));
  } else await parentCapture(args);
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === resolve(ownPath).toLowerCase()) await main();
