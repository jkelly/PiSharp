// Root invokes --capture-new once; default verifies immutable genuine observations.
// No install/download/SDK or source replacement action is exposed by this runner.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { arch, platform, release, version as osVersion } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';
import { captureAnthropicSdk } from './capture-anthropic-sdk.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const referencePlanPath = join(repo, 'compatibility/anthropic-sdk-reference.plan.json');
const referencePlanHash = 'a8f0d82ad2a6b5def3e6687198b103c4bc90bb1b9629ae4f009dd1d8f68dadcc';
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
const rawJson = value => JSON.stringify(value, null, 2) + '\n';
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
const normalized = path => resolve(path).toLowerCase();
const inside = (root, path) => { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); };
function noLinks(path) {
  let current = resolve(path);
  while (true) {
    let stat; try { stat = lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (stat) assert(!stat.isSymbolicLink(), 'Filesystem link/junction rejected');
    const parent = dirname(current); if (parent === current) break; current = parent;
  }
}
function readRegular(path) { noLinks(path); assert(lstatSync(path).isFile()); return readFileSync(path); }
const fileHash = path => hash(readRegular(path));
const readJson = path => parseJsonSupported(readRegular(path).toString('utf8'));
function verifyPin(root, pin) { const path = resolve(root, pin.path); assert(inside(root, path)); const bytes = readRegular(path); assert.equal(bytes.length, pin.bytes, 'Pinned byte count changed: ' + pin.path); assert.equal(hash(bytes), pin.sha256, 'Pinned bytes changed: ' + pin.path); return bytes; }
function tree(root) {
  noLinks(root); assert(lstatSync(root).isDirectory()); const files = [];
  const visit = directory => {
    for (const name of readdirSync(directory).sort()) {
      const path = join(directory, name), stat = lstatSync(path); assert(!stat.isSymbolicLink());
      if (stat.isDirectory()) visit(path); else { assert(stat.isFile()); const bytes = readFileSync(path); files.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); }
    }
  }; visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function cleanEnvironment(oracle, scratch) {
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', PATH: dirname(process.execPath), USERPROFILE: join(scratch, 'home'), HOME: join(scratch, 'home'), APPDATA: join(scratch, 'home'), LOCALAPPDATA: join(scratch, 'home'), TMP: scratch, TEMP: scratch, TZ: 'UTC', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(oracle, 'config/gitconfig'), GIT_OPTIONAL_LOCKS: '0', PISHARP_REFERENCE_ORACLE: oracle };
}
export function parseCaptureArguments(args) {
  let first = false, oracle;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--oracle' && oracle === undefined && args[index + 1] && !args[index + 1].startsWith('--')) oracle = resolve(args[++index]);
    else throw new Error('Usage: node run-anthropic-sdk.mjs [--capture-new] [--oracle APPROVED_REVIEWED_ORACLE]');
  }
  return { first, oracle };
}
export function selectCaptureOracle(oracle, plan) {
  const selected = resolve(oracle ?? plan.approvedOracle); assert.equal(normalized(selected), normalized(plan.approvedOracle), 'Only reviewed restored oracle admitted'); noLinks(selected); return selected;
}
export function readOnlySetupCheck(oracle, environment, setupPath) {
  const result = spawnSync(process.execPath, [setupPath, '--check', '--oracle', oracle], { cwd: repo, env: environment, windowsHide: true, encoding: 'utf8', timeout: 30000, maxBuffer: 4 * 1024 * 1024 });
  assert.equal(result.status, 0, 'Read-only canonical-source/npm/dependency setup check failed: ' + (result.error?.message ?? result.stderr)); return parseJsonSupported(result.stdout);
}
function loadReferencePlan() { const bytes = readRegular(referencePlanPath); assert.equal(hash(bytes), referencePlanHash, 'Frozen reference plan changed'); return parseJsonSupported(bytes.toString('utf8')); }
function validateOutputPath(plan, key) { const path = resolve(repo, plan.outputs[key]); assert(inside(repo, path)); noLinks(path); return path; }
function writeOutput(plan, key, bytes, exclusive = false) { const path = validateOutputPath(plan, key); mkdirSync(dirname(path), { recursive: true }); noLinks(path); writeFileSync(path, bytes, exclusive ? { flag: 'wx' } : undefined); }
async function parentCapture(args) {
  const settings = parseCaptureArguments(args), plan = loadReferencePlan(), oracle = selectCaptureOracle(settings.oracle, plan);
  assert.equal(process.platform, 'win32'); assert.equal(process.version, plan.runtime.version); assert.equal(normalized(process.execPath), normalized(plan.runtime.absoluteExecutable)); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  const goldenPaths = ['expected', 'lock', 'manifest'].map(key => validateOutputPath(plan, key));
  // Admission is checked before any child execution, filesystem output or SDK import.
  if (settings.first) assert(!goldenPaths.some(existsSync), 'First-new capture refuses existing golden/lock/manifest'); else assert(goldenPaths.every(existsSync), 'Immutable captured golden/lock/manifest required');
  const setupPlan = parseJsonSupported(verifyPin(repo, plan.setupPlan).toString('utf8'));
  for (const pin of [plan.setupHelper, plan.archiveInspector, plan.offlineObserver, plan.offlineGuard, plan.rawComparator, plan.input, plan.publicRestoredReceipt]) verifyPin(repo, pin);
  const restored = parseJsonSupported(verifyPin(oracle, plan.restoredReceipt).toString('utf8')); verifyPin(oracle, plan.preparedReceipt);
  assert.equal(restored.status, 'eight exact dependencies installed; whole Anthropic wrapper qualification pending');
  assert.equal(restored.owner.planSha256, plan.setupPlan.sha256); assert.equal(restored.owner.helperSha256, plan.setupHelper.sha256); assert(same(restored.sourceFingerprint, plan.sourceFingerprint));
  assert.equal(restored.licenseReviewStatus, 'P1-02 HOLD'); assert.equal(restored.redistributionLicenseClosure, false);
  const webhook = restored.installed.find(item => item.name === 'standardwebhooks'); assert.equal(webhook.licenseEvidence, 'declared-MIT-only'); assert.equal(webhook.packagedRootLicenseVerified, false); assert.deepEqual(webhook.licenses, []);
  const setupPath = resolve(repo, plan.setupHelper.path), env = cleanEnvironment(oracle, join(oracle, 'tmp'));
  const preflight = readOnlySetupCheck(oracle, env, setupPath); assert(same(preflight.sourceFingerprint, plan.sourceFingerprint));
  assert.equal(normalized(preflight.oracle), normalized(oracle));
  assert(same(preflight.installed, plan.packages.map(({ name, version, files }) => ({ name, version, files }))), 'Read-only setup installed fingerprints differ');
  assert.equal(preflight.licenseReviewStatus, 'P1-02 HOLD'); assert.equal(preflight.redistributionLicenseClosure, false);
  const { inspectPackageArchive, inspectionReceiptPath } = await import(pathToFileURL(setupPath).href);
  const packageNames = plan.packages.map(row => row.name);
  assert(same(packageNames, setupPlan.packages.map(row => row.name)), 'Exact package profile differs');
  const dependencies = plan.packages.map(pin => {
    const row = setupPlan.packages.find(item => item.name === pin.name), bytes = readRegular(join(oracle, 'archives', row.archiveFile));
    assert.equal(hash(bytes), pin.archiveSha256); assert.equal('sha512-' + hash(bytes, 'sha512', 'base64'), row.lockEntry.integrity);
    const archive = inspectPackageArchive(bytes), files = tree(join(oracle, 'node_modules', row.name));
    const expectedFiles = [...archive.files].map(([path, data]) => ({ path, bytes: data.length, sha256: hash(data) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    assert(same(files, expectedFiles)); assert.equal(files.length, pin.files.files); assert.equal(hash(Buffer.from(canonical(files))), pin.files.sha256);
    assert.equal(hash(archive.files.get('package.json')), pin.manifestSha256);
    const recorded = restored.installed.find(item => item.name === pin.name); const inspection = readJson(inspectionReceiptPath(oracle, row.archiveFile));
    assert.equal(inspection.sha256, pin.archiveSha256); assert.equal(inspection.licenseEvidence, pin.licenseEvidence); assert.equal(inspection.packagedRootLicenseVerified, pin.packagedRootLicenseVerified);
    for (const license of pin.licenses) { assert.equal(hash(archive.files.get(license.path)), license.sha256); assert.equal(archive.files.get(license.path).toString('utf8'), license.utf8Text); }
    const { integrity, archiveFile, ...originalPin } = pin; assert(same(recorded, originalPin), 'Actual installed receipt facts differ');
    return pin;
  });
  const fixture = parseJsonSupported(verifyPin(repo, plan.input).toString('utf8')); assert.equal(fixture.sourceSha, plan.sourceSha); assert.equal(fixture.fixtureId, plan.fixtureId); assert.equal(fixture.cases.length, 3);
  const harnessFiles = plan.harnessFiles.map(path => { const bytes = readRegular(resolve(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; });
  const environmentPins = { sourceSha: plan.sourceSha, sourceFingerprint: preflight.sourceFingerprint, approvedOracle: oracle, runtime: plan.runtime, nodeVersions: { node: process.versions.node, openssl: process.versions.openssl, v8: process.versions.v8, uv: process.versions.uv, unicode: process.versions.unicode, icu: process.versions.icu }, os: { platform: platform(), release: release(), architecture: arch(), version: osVersion() }, setupReceipt: plan.restoredReceipt, publicSetupReceipt: plan.publicRestoredReceipt, preparedReceipt: plan.preparedReceipt, projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')), installedPackages: dependencies, licenseStatus: plan.licenseStatus };
  let locked;
  if (!settings.first) {
    const manifest = readJson(goldenPaths[2]); assert.equal(manifest.fixtureId, fixture.fixtureId); assert.equal(manifest.sourceSha, fixture.sourceSha);
    assert.equal(manifest.input.path, plan.input.path); assert.equal(manifest.input.sha256, plan.input.sha256); assert.equal(manifest.expected.path, plan.outputs.expected); assert.equal(manifest.lock.path, plan.outputs.lock);
    assert.equal(fileHash(goldenPaths[0]), manifest.expected.sha256, 'Golden changed before execution'); assert.equal(fileHash(goldenPaths[1]), manifest.lock.sha256, 'Lock changed before execution');
    locked = readJson(goldenPaths[1]); assert(same(locked.environmentPins, environmentPins)); assert(same(locked.harnessFiles, harnessFiles));
    for (const file of locked.loadedModules) { assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || packageNames.some(name => file.path.startsWith('node_modules/' + name + '/')))); assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Locked loaded source/dependency changed before execution'); }
  }
  const scratchRoot = validateOutputPath(plan, 'scratch'); mkdirSync(scratchRoot, { recursive: true }); const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-')), marker = Buffer.from('PiSharp-owned-anthropic-capture-v1\n');
    try {
      writeFileSync(join(scratch, '.owner'), marker, { flag: 'wx' }); mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(resolve(repo, plan.offlineObserver.path)).href, ownPath, '--child'], { cwd: join(scratch, 'workspace'), env: cleanEnvironment(oracle, scratch), windowsHide: true, encoding: 'utf8', timeout: 30000, maxBuffer: 32 * 1024 * 1024 });
      assert.equal(child.status, 0, 'Genuine whole-wrapper child failed; report prerequisites rather than substitute modules: ' + (child.error?.message ?? child.stderr)); captures.push(parseJsonSupported(child.stdout));
    } finally {
      const target = resolve(scratch); assert(inside(scratchRoot, target), 'Recursive cleanup path is outside owned scratch root'); noLinks(target); assert.equal(normalized(realpathSync(target)), normalized(target)); assert(readRegular(join(target, '.owner')).equals(marker), 'Scratch ownership changed'); rmSync(target, { recursive: true, force: true });
    }
  }
  assert.equal(rawJson(captures[0]), rawJson(captures[1]), 'Fresh captures differ; raw/opaque/random fields must not be normalized away');
  const loadedModules = captures[0].loadedModules; assert(loadedModules.some(file => file.path === 'upstream/packages/ai/src/api/anthropic-messages.ts')); assert(loadedModules.some(file => file.path.startsWith('node_modules/@anthropic-ai/sdk/')));
  const sourceHashes = [];
  for (const file of loadedModules) {
    assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Loaded bytes changed after execution');
    if (file.path.startsWith('upstream/')) {
      const path = file.path.slice('upstream/'.length), git = spawnSync('C:\\Program Files\\Git\\cmd\\git.exe', ['-c', 'safe.directory=' + join(oracle, 'upstream'), '-C', join(oracle, 'upstream'), 'show', plan.sourceSha + ':' + path], { env, windowsHide: true, timeout: 30000, maxBuffer: 32 * 1024 * 1024 });
      assert.equal(git.status, 0); assert.equal(hash(git.stdout), file.sha256, 'Executed source differs from canonical Git blob'); sourceHashes.push({ ...file, canonicalGitBlobSha256: hash(git.stdout) });
    } else assert(packageNames.some(name => file.path.startsWith('node_modules/' + name + '/')), 'Loaded package outside exact profile');
  }
  const after = readOnlySetupCheck(oracle, env, setupPath); assert(same(after.sourceFingerprint, preflight.sourceFingerprint)); assert(same(after.installed, preflight.installed)); assert.equal(after.licenseReviewStatus, 'P1-02 HOLD'); assert.equal(after.redistributionLicenseClosure, false);
  for (const row of dependencies) assert.equal(hash(Buffer.from(canonical(tree(join(oracle, 'node_modules', row.name))))), row.files.sha256);
  for (const pin of [plan.restoredReceipt, plan.preparedReceipt]) verifyPin(oracle, pin); verifyPin(repo, plan.publicRestoredReceipt); verifyPin(repo, plan.input);
  assert(same(harnessFiles, plan.harnessFiles.map(path => { const bytes = readRegular(resolve(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; })), 'Harness changed during capture');
  const golden = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: 'captured-unchanged-anthropic-wrapper-sdk-oracle', observations: captures[0].observations }, actualRaw = rawJson(golden);
  if (settings.first) {
    assert(!goldenPaths.some(existsSync), 'Preserving concurrently created immutable artifacts');
    writeOutput(plan, 'expected', actualRaw, true);
    writeOutput(plan, 'lock', rawJson({ schemaVersion: 1, environmentPins, harnessFiles, loadedModules, sourceHashes, captureHistory: [{ kind: 'initial genuine whole-module capture', goldenSha256: hash(Buffer.from(actualRaw)), sourceAndSdkBytesChanged: false, childDateNowOverride: fixture.clock }] }), true);
    writeOutput(plan, 'manifest', rawJson({ schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, requirementIds: fixture.requirementIds, input: plan.input, expected: { path: plan.outputs.expected, sha256: fileHash(goldenPaths[0]), kind: golden.kind }, lock: { path: plan.outputs.lock, sha256: fileHash(goldenPaths[1]) }, clock: fixture.clock, seed: fixture.seed, normalizerVersion: fixture.normalizerVersion, licenseStatus: plan.licenseStatus, provenance: { source: 'Whole unchanged packages/ai/src/api/anthropic-messages.ts public stream', sdk: 'Actual upstream-locked @anthropic-ai/sdk0.124.0 beta.messages.create/asResponse request boundary', sseDecoder: 'Unchanged Pi iterateAnthropicEvents; no SDK decoder claim', input: 'Authored transcripts/options/number-lexeme-to-Number seam and in-memory SSE', callbacks: 'Supported fake fetch/onPayload returning undefined/onResponse/onProviderStreamEvent', emissionObservation: 'Returned stream instance push tap snapshots before mutation; source/SDK implementations unchanged', credentials: 'Inert authored marker; explicit credential-free child env', repeatRuns: 2, byteIdentical: true }, scope: plan.scope }), true);
  } else assert(same(loadedModules, locked.loadedModules) && same(sourceHashes, locked.sourceHashes), 'Observed closure differs from frozen closure');
  const matched = compareRawJson(readRegular(goldenPaths[0]).toString('utf8'), actualRaw); writeOutput(plan, 'actual', actualRaw);
  const report = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, capturedInitialGolden: settings.first, repeatRuns: 2, byteIdentical: true, matched, sourceCleanAfter: true, installedPackages: dependencies.length, installedFiles: dependencies.reduce((sum, row) => sum + row.files.files, 0), loadedUpstreamFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length, loadedSdkFiles: loadedModules.filter(file => file.path.startsWith('node_modules/@anthropic-ai/sdk/')).length, cases: golden.observations.cases.map(item => ({ caseId: item.caseId, fetchCount: item.fetchRequests.length, terminal: item.emissionSnapshots.at(-1).type, numberConversionCount: item.numberConversions.length })), licenseStatus: plan.licenseStatus, scope: plan.scope };
  writeOutput(plan, 'report', rawJson(report)); console.log(rawJson(report)); assert(matched, 'Genuine observation differs from immutable golden'); return report;
}
export async function main(args = process.argv.slice(2)) {
  if (args[0] === '--child') {
    assert.deepEqual(args, ['--child']); const plan = loadReferencePlan(), oracle = selectCaptureOracle(process.env.PISHARP_REFERENCE_ORACLE, plan);
    const fixture = parseJsonSupported(verifyPin(repo, plan.input).toString('utf8'));
    console.log(JSON.stringify(await captureAnthropicSdk(oracle, fixture, plan.packages.map(row => row.name))));
  } else return parentCapture(args);
}
if (process.argv[1] && normalized(process.argv[1]) === normalized(ownPath)) await main();
