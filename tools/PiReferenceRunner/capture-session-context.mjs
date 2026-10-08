// Genuine unchanged whole session-manager module and separate public LLM conversion.
// Default verifies frozen output; --capture-new refuses any existing golden/lock.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const family = 'fixtures/pi-v0.99.1/session-context';
const inputPath = join(repo, family, 'core.input.json'), expectedPath = join(repo, family, 'core.expected.json');
const manifestPath = join(repo, family, 'manifest.json'), lockPath = join(repo, family, 'oracle.lock.json');
const planPath = join(repo, 'compatibility/session-context-oracle-plan.json');
const planSha256 = '31efcbef3fa2031b880118ce8c26165946d30ca2bab589942c0a1e2e8e730427';
const setupPath = join(repo, 'tools/PiReferenceRunner/setup-session-context-oracle.mjs');
const setupSha256 = '285cb9e70726e19e38dc318a40137708ae65e3ae33e893ea4775e6f169168bdc';
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
const fileHash = path => hash(readFileSync(path));
const readJson = path => parseJsonSupported(readFileSync(path, 'utf8'));
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
const rawJson = value => JSON.stringify(value, null, 2) + '\n';
const packages = ['cross-spawn', 'isexe', 'partial-json', 'path-key', 'shebang-command', 'shebang-regex', 'typebox', 'which'];
const harnessPaths = ['tools/PiReferenceRunner/capture-session-context.mjs', 'tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/PiReferenceRunner/setup-session-context-oracle.mjs', 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs', 'compatibility/session-context-oracle-plan.json', 'tools/CompatibilityReport/raw-json.mjs'];
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
async function childCapture(oracle) {
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Child requires the existing locked offline preload');
  const allowed = url => {
    if (!url.startsWith('file:')) return url.startsWith('node:');
    const path = fileURLToPath(url);
    return inside(join(oracle, 'upstream'), path) || packages.some(name => inside(join(oracle, 'node_modules', name), path));
  };
  // Forward resolution, allowing unchanged source and the exact eight packages.
  registerHooks({ resolve(specifier, context, nextResolve) { const result = nextResolve(specifier, context); assert(allowed(result.url), 'Unreviewed module fallback rejected: ' + result.url); return result; } });
  const originalDate = Date, originalNow = Date.now;
  const session = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/session-manager.ts')).href);
  const { convertToLlm } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/messages.ts')).href);
  assert.equal(typeof session.buildSessionProjection, 'function'); assert.equal(typeof session.buildSessionContext, 'function'); assert.equal(typeof convertToLlm, 'function');
  const fixture = readJson(inputPath), cases = [];
  for (const test of fixture.cases) {
    const entries = structuredClone(fixture.entries), originalInput = rawJson(entries);
    const projection = session.buildSessionProjection(entries, test.leafId);
    const projectionSnapshot = structuredClone(projection), projectionUndefined = undefinedPaths(projection);
    const context = session.buildSessionContext(entries, test.leafId);
    const contextSnapshot = structuredClone(context), contextUndefined = undefinedPaths(context);
    const llmMessages = convertToLlm(context.messages);
    const llmSnapshot = structuredClone(llmMessages), llmUndefined = undefinedPaths(llmMessages);
    assert.equal(rawJson(entries), originalInput, 'Public pure exports changed authored source entries');
    assert.equal(Date, originalDate); assert.equal(Date.now, originalNow, 'No global clock override is admitted');
    assert(same(projectionSnapshot.messages, contextSnapshot.messages), 'Canonical context differs from public projection messages');
    cases.push({ caseId: test.caseId, leafId: test.leafId, projection: projectionSnapshot, context: contextSnapshot, llmMessages: llmSnapshot, ownUndefinedPaths: { projection: projectionUndefined, context: contextUndefined, llmMessages: llmUndefined } });
  }
  return { observations: { cases, checks: { caseCount: cases.length, authoredInputEntries: fixture.entries.length, wholeSessionModuleLoaded: true, publicExports: ['buildSessionProjection', 'buildSessionContext', 'convertToLlm'], sourceEntriesUnchanged: true, noClockOrRngOverride: true, noSessionConstructorOrFilesystemOperation: true, networkAndProcessesBlocked: true } }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) };
}
export function parseCaptureArguments(args) {
  let first = false, oracle;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--oracle' && !oracle && args[index + 1] && !args[index + 1].startsWith('--')) oracle = resolve(args[++index]);
    else throw new Error('Usage: node capture-session-context.mjs [--capture-new] [--oracle APPROVED_SESSION_ORACLE]');
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
  assert.equal(fileHash(planPath), planSha256, 'Reviewed session oracle plan changed');
  assert.equal(fileHash(setupPath), setupSha256, 'Accepted setup helper changed');
  const plan = readJson(planPath); oracle ??= resolve(plan.workspace.proposedRoot);
  assert.equal(oracle.toLowerCase(), resolve(plan.workspace.proposedRoot).toLowerCase(), 'Only the fresh approved session oracle is admitted');
  assert.equal(process.version, plan.runtime.version); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  const restoredPath = join(oracle, '.pisharp-session-context-restored.json');
  assert(existsSync(restoredPath), 'Root-confirmed successful restore record is required before session execution');
  const restored = readJson(restoredPath);
  assert.equal(restored.status, 'eight exact dependencies installed; whole session module qualification pending');
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
  const fixture = readJson(inputPath); assert.equal(fixture.sourceSha, plan.source.commit);
  assert.equal(fixture.fixtureId, 'session-context-core');
  assert.equal(fixture.schemaVersion, 1); assert.equal(fixture.cases.length, 4);
  assert(fixture.entries.length > 0 && fixture.cases.every(test => Object.hasOwn(test, 'leafId')), 'Explicit authored selection is required');
  const harnessFiles = harnessPaths.map(path => ({ path, sha256: fileHash(join(repo, path)), bytes: readFileSync(join(repo, path)).length }));
  const environmentPins = { sourceSha: plan.source.commit, sourceFingerprint: preflight.sourceFingerprint, runtime: plan.runtime, platform: process.platform, architecture: process.arch, npmVersion: plan.packageManager.version, setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-session-context-prepared.json')), projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')), dependencies };
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
    assert.equal(expected.schemaVersion, 1); assert.equal(expected.fixtureId, fixture.fixtureId); assert.equal(expected.sourceSha, fixture.sourceSha); assert.equal(expected.kind, 'captured-unchanged-session-context-oracle');
    for (const file of lock.loadedModules) {
      assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || packages.some(name => file.path.startsWith(`node_modules/${name}/`))), 'Unreviewed locked module path rejected');
      assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Loaded module bytes changed before execution');
    }
  }
  const outputRoot = join(repo, 'artifacts/session-context-reference'), scratchRoot = join(outputRoot, 'scratch'); mkdirSync(scratchRoot, { recursive: true });
  const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-'));
    try {
      mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(repo, 'tools/PiReferenceRunner/full-preload.mjs')).href, ownPath, '--child'], { cwd: join(scratch, 'workspace'), env: cleanEnvironment(oracle, scratch), windowsHide: true, encoding: 'utf8', timeout: 20000, maxBuffer: 16 * 1024 * 1024 });
      assert.equal(child.status, 0, 'Genuine whole session capture failed; do not substitute a module: ' + (child.error?.message ?? child.stderr));
      captures.push(parseJsonSupported(child.stdout));
    } finally {
      const target = resolve(scratch); assert(inside(scratchRoot, target), 'Scratch cleanup confinement failed'); rmSync(target, { recursive: true, force: true });
    }
  }
  assert.equal(rawJson(captures[0]), rawJson(captures[1]), 'Two captures differ; no random/opaque field may be normalized away');
  const loadedModules = captures[0].loadedModules;
  assert(loadedModules.some(file => file.path === 'upstream/packages/coding-agent/src/core/session-manager.ts') && loadedModules.some(file => file.path === 'upstream/packages/coding-agent/src/core/messages.ts') && loadedModules.some(file => file.path.startsWith('node_modules/cross-spawn/')), 'Whole session module and real eager dependency load proof absent');
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
  const golden = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: 'captured-unchanged-session-context-oracle', observations: captures[0].observations }, actualRaw = rawJson(golden);
  if (first) {
    writeFileSync(expectedPath, actualRaw, { flag: 'wx' });
    writeFileSync(lockPath, rawJson({ schemaVersion: 1, environmentPins, harnessFiles, loadedModules, sourceHashes, captureHistory: [{ kind: 'initial genuine capture', goldenSha256: hash(Buffer.from(actualRaw)), sourceAndDependencyBytesChanged: false }] }), { flag: 'wx' });
    writeFileSync(manifestPath, rawJson({ schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, requirementIds: fixture.requirementIds, clock: fixture.clock, seed: fixture.seed, normalizerVersion: fixture.normalizerVersion, input: { path: family + '/core.input.json', sha256: fileHash(inputPath), kind: fixture.kind }, expected: { path: family + '/core.expected.json', sha256: fileHash(expectedPath), kind: golden.kind }, lock: { path: family + '/oracle.lock.json', sha256: fileHash(lockPath) }, provenance: { source: 'Unchanged whole packages/coding-agent/src/core/session-manager.ts and messages.ts public exports', dependencies: 'Exact eight upstream-lock archives and fully verified installed files', sourceResolver: 'Unchanged canonical experimental/source-resolver.ts', observations: 'Synchronous public projection/context and separate LLM conversion snapshots; own undefined fields separately recorded', input: 'authored v3 entries and leaf IDs, no user session/tool output', credentials: 'explicit credential-free offline child environment', clock: 'authored dates only; no Date/Date.now or RNG override', repeatRuns: 2, byteIdentical: true, captureCommand: 'node tools/PiReferenceRunner/capture-session-context.mjs --capture-new --oracle APPROVED_SESSION_ORACLE' }, scope: 'Four selected context cases including one stored compaction/edit/summary/bash path; genuine source only, no native parity/session persistence/generation/full phase closure' }), { flag: 'wx' });
  } else {
    const locked = readJson(lockPath); assert(same(loadedModules, locked.loadedModules) && same(sourceHashes, locked.sourceHashes), 'Loaded closure changed');
    assert.equal(fileHash(lockPath), readJson(manifestPath).lock.sha256, 'Immutable lock changed');
  }
  const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
  writeFileSync(join(outputRoot, 'core.actual.json'), actualRaw);
  const report = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, capturedInitialGolden: first, repeatRuns: 2, byteIdentical: true, matched, sourceCleanAfter: true, sourceEntriesUnchanged: true, wholeSessionModuleLoaded: true, installedPackageFiles: dependencies.reduce((sum, row) => sum + row.files.files, 0), loadedUpstreamFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length, loadedDependencyCounts: Object.fromEntries(packages.map(name => [name, loadedModules.filter(file => file.path.startsWith('node_modules/' + name + '/')).length])), cases: golden.observations.cases.map(test => ({ caseId: test.caseId, leafId: test.leafId, projectedEntryIds: test.projection.entries.map(entry => entry.sourceEntry.id), runtimeRoles: test.context.messages.map(message => message.role), llmRoles: test.llmMessages.map(message => message.role), thinkingLevel: test.context.thinkingLevel, model: test.context.model, ownUndefinedCounts: Object.fromEntries(Object.entries(test.ownUndefinedPaths).map(([key, paths]) => [key, paths.length])) })), scope: 'Genuine unchanged whole-session pure context and separate LLM conversion; no native differential/persistence/generation/full parity gate claim' };
  writeFileSync(join(outputRoot, 'report.json'), rawJson(report)); console.log(rawJson(report)); assert(matched, 'Actual output differs from immutable golden');
}
export async function main(args = process.argv.slice(2)) {
  if (args[0] === '--child') {
    assert.equal(args.length, 1);
    console.log(JSON.stringify(await childCapture(resolve(process.env.PISHARP_REFERENCE_ORACLE))));
  } else await parentCapture(args);
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === resolve(ownPath).toLowerCase()) await main();
