// Root-owned initial capture; default replays immutable whole-Agent observations.
// Exposes no install/download/source mutation action. --check is read-only.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';
import { captureAgentProgress } from './capture-agent-progress.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const planPath = join(repo, 'compatibility/agent-progress-reference.plan.json');
const planHash = 'd1884bde8e7bf264cbc1e030b78d6d333252d690ecc4f7d12a67f1b268008d27';
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
function loadPlan() { const bytes = readRegular(planPath); assert.equal(hash(bytes), planHash, 'Frozen reference plan changed'); return parseJsonSupported(bytes.toString('utf8')); }
function tree(root) {
  noLinks(root); assert(lstatSync(root).isDirectory()); const files = [];
  const visit = directory => {
    for (const name of readdirSync(directory).sort()) {
      const path = join(directory, name), stat = lstatSync(path); assert(!stat.isSymbolicLink());
      if (stat.isDirectory()) visit(path); else { assert(stat.isFile()); const bytes = readFileSync(path); files.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); }
    }
  }; visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function environment(oracle, scratch) {
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', PATH: dirname(process.execPath), USERPROFILE: join(scratch, 'home'), HOME: join(scratch, 'home'), APPDATA: join(scratch, 'home'), LOCALAPPDATA: join(scratch, 'home'), TMP: scratch, TEMP: scratch, TZ: 'UTC', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(oracle, 'config/gitconfig'), GIT_OPTIONAL_LOCKS: '0', PISHARP_REFERENCE_ORACLE: oracle };
}
export function parseCaptureArguments(args) {
  let first = false, check = false, oracle;
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first && !check) first = true;
    else if (args[index] === '--check' && !check && !first) check = true;
    else if (args[index] === '--oracle' && oracle === undefined && args[index + 1] && !args[index + 1].startsWith('--')) oracle = resolve(args[++index]);
    else throw new Error('Usage: node run-agent-progress.mjs [--capture-new | --check] [--oracle APPROVED_MINIMAL_ORACLE]');
  }
  return { first, check, oracle };
}
export function selectCaptureOracle(oracle, plan) {
  const selected = resolve(oracle ?? plan.approvedOracle); assert.equal(normalized(selected), normalized(plan.approvedOracle), 'Only the exact approved minimal oracle is admitted'); noLinks(selected); return selected;
}
export function validateGoldenAdmission(first, presence) {
  assert.equal(presence.length, 3);
  if (first) assert(!presence.some(Boolean), 'First-new capture refuses existing golden/lock/manifest'); else assert(presence.every(Boolean), 'Immutable captured golden/lock/manifest required');
}
function git(upstream, args, env) {
  const result = spawnSync('C:\\Program Files\\Git\\cmd\\git.exe', ['-c', 'safe.directory=' + upstream, '-C', upstream, ...args], { env, windowsHide: true, timeout: 30000, maxBuffer: 32 * 1024 * 1024 });
  assert.equal(result.status, 0, 'Read-only Git source verification failed: ' + (result.error?.message ?? result.stderr)); return result.stdout;
}
function sourceFingerprint(oracle, plan, env) {
  const upstream = join(oracle, 'upstream'); noLinks(upstream);
  const read = args => git(upstream, args, env).toString('utf8').trim();
  assert.equal(read(['rev-parse', 'HEAD']), plan.sourceSha); assert.equal(read(['rev-parse', 'HEAD^{tree}']), plan.source.tree);
  assert.equal(read(['status', '--porcelain=v1', '--untracked-files=all']), '', 'Source has modified/generated files');
  for (const pin of plan.source.pins) verifyPin(upstream, pin);
  const files = [], checkoutFiles = [], conversions = [];
  for (const record of git(upstream, ['ls-tree', '-r', '-z', 'HEAD'], env).toString('utf8').split('\0').filter(Boolean)) {
    const match = /^([0-7]+) blob ([0-9a-f]{40})\t(.+)$/.exec(record); assert(match && ['100644', '100755'].includes(match[1]), 'Unexpected source tree object');
    const path = resolve(upstream, match[3]); assert(inside(upstream, path)); const bytes = readRegular(path); let canonicalBytes = bytes;
    if (hash(Buffer.concat([Buffer.from('blob ' + bytes.length + '\0'), bytes]), 'sha1') !== match[2]) {
      const attr = git(upstream, ['check-attr', '-z', 'eol', '--', match[3]], env).toString('utf8').split('\0');
      assert.deepEqual(attr.slice(0, 3), [match[3], 'eol', 'crlf']); canonicalBytes = git(upstream, ['cat-file', 'blob', match[2]], env);
      const utf8 = canonicalBytes.toString('utf8'); assert(Buffer.from(utf8).equals(canonicalBytes) && !canonicalBytes.includes(Buffer.from('\r\n')) && Buffer.from(utf8.replaceAll('\n', '\r\n')).equals(bytes));
      assert.equal(hash(Buffer.concat([Buffer.from('blob ' + canonicalBytes.length + '\0'), canonicalBytes]), 'sha1'), match[2]);
      conversions.push({ path: match[3], eol: 'crlf', canonicalSha256: hash(canonicalBytes), checkoutSha256: hash(bytes) });
    }
    files.push({ path: match[3], mode: match[1], gitBlob: match[2], bytes: canonicalBytes.length, sha256: hash(canonicalBytes) }); checkoutFiles.push({ path: match[3], bytes: bytes.length, sha256: hash(bytes) });
  }
  const fingerprint = rows => ({ files: rows.length, sha256: hash(Buffer.from(canonical(rows))) });
  const result = { canonicalGit: fingerprint(files), acquiredCheckout: fingerprint(checkoutFiles), declaredCheckoutConversions: conversions };
  assert(same(result.canonicalGit, plan.source.canonicalFingerprint)); assert(same(result.acquiredCheckout, plan.source.acquiredFingerprint)); assert(same(conversions.map(row => row.path).sort(), plan.source.declaredConversionPaths)); return result;
}
export async function checkCaptureEnvironment(plan, oracle) {
  assert.equal(process.platform, 'win32'); assert.equal(process.version, plan.runtime.version); assert.equal(normalized(process.execPath), normalized(plan.runtime.absoluteExecutable)); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  const original = parseJsonSupported(verifyPin(repo, plan.originalSetupPlan).toString('utf8'));
  for (const pin of [plan.archiveInspector, plan.offlineObserver, plan.offlineGuard, plan.rawComparator, plan.input, plan.projectionManifest, plan.projectionLock]) verifyPin(repo, pin);
  const receipt = parseJsonSupported(verifyPin(oracle, plan.setupReceipt).toString('utf8'));
  assert.equal(receipt.owner, 'PiSharp-reference-setup-v1'); assert.equal(receipt.sourceCommit, plan.sourceSha); assert.equal(receipt.status, 'dependencies-installed; runtime oracle qualification pending'); assert.equal(receipt.upstreamSourceModified, false);
  assert.equal(receipt.npmVersion, plan.packageManagerEvidence.version); assert.equal(receipt.npmArchiveSha256, plan.packageManagerEvidence.archive.sha256);
  assert.equal(normalized(original.workspace), normalized(oracle)); assert.equal(original.source.commit, plan.sourceSha); assert(same(original.dependencies, plan.packages.map(({ name, version, resolved, integrity, license }) => ({ name, version, resolved, integrity, license }))));
  for (const pin of plan.oracleProjection) verifyPin(oracle, pin); verifyPin(oracle, plan.gitConfig);
  assert(verifyPin(oracle, plan.oracleProjection[0]).equals(verifyPin(repo, plan.projectionManifest))); assert(verifyPin(oracle, plan.oracleProjection[1]).equals(verifyPin(repo, plan.projectionLock)));
  const npmBytes = verifyPin(oracle, plan.packageManagerEvidence.archive); assert.equal('sha512-' + hash(npmBytes, 'sha512', 'base64'), plan.packageManagerEvidence.integrity);
  const env = environment(oracle, join(oracle, 'tmp')), source = sourceFingerprint(oracle, plan, env);
  const upstreamLock = readJson(join(oracle, 'upstream/package-lock.json'));
  assert(same(readdirSync(join(oracle, 'node_modules')).filter(name => name !== '.package-lock.json').sort(), plan.packages.map(row => row.name).sort()), 'Extra installed packages rejected');
  const { inspectPackageArchive } = await import(pathToFileURL(resolve(repo, plan.archiveInspector.path)).href);
  for (const row of plan.packages) {
    const locked = upstreamLock.packages['node_modules/' + row.name]; assert.equal(locked.version, row.version); assert.equal(locked.resolved, row.resolved); assert.equal(locked.integrity, row.integrity); assert.equal(locked.license, row.license);
    const compressed = verifyPin(oracle, row.archive); assert.equal('sha512-' + hash(compressed, 'sha512', 'base64'), row.integrity);
    const archive = inspectPackageArchive(compressed), expected = [...archive.files].map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    const actual = tree(join(oracle, 'node_modules', row.name)); assert(same(actual, expected), 'Installed bytes differ from SRI-verified official archive'); assert.equal(actual.length, row.files.files); assert.equal(hash(Buffer.from(canonical(actual))), row.files.sha256);
    const manifestBytes = archive.files.get('package.json'), manifest = parseJsonSupported(manifestBytes.toString('utf8')); assert.equal(hash(manifestBytes), row.manifestSha256); assert.equal(manifest.name, row.name); assert.equal(manifest.version, row.version); assert.equal(manifest.license, row.license);
    for (const [field, value] of [['dependencies', row.dependencyDeclarations], ['optionalDependencies', row.optionalDependencies], ['peerDependencies', row.peerDependencies]]) assert(same(manifest[field] ?? {}, value));
    assert(same(Object.fromEntries(Object.entries(manifest.scripts ?? {}).filter(([key]) => ['preinstall', 'install', 'postinstall', 'prepare'].includes(key))), row.lifecycleScripts)); assert(same(row.lifecycleScripts, {}));
    assert.equal(receipt.installed.find(item => item.name === row.name)?.manifestSha256, row.manifestSha256);
    for (const license of row.licenses) { const bytes = archive.files.get(license.path); assert.equal(bytes.length, license.bytes); assert.equal(hash(bytes), license.sha256); assert.equal(bytes.toString('utf8'), license.utf8Text); }
  }
  return { sourceFingerprint: source, installedPackages: plan.packages, setupReceipt: plan.setupReceipt, projectionManifestSha256: plan.oracleProjection[0].sha256, projectionLockSha256: plan.oracleProjection[1].sha256, approvedOracle: oracle, runtime: plan.runtime, licenseStatus: plan.licenseStatus };
}
function outputPath(plan, key) { const path = resolve(repo, plan.outputs[key]); assert(inside(repo, path)); noLinks(path); return path; }
function output(plan, key, bytes, exclusive = false) { const path = outputPath(plan, key); mkdirSync(dirname(path), { recursive: true }); noLinks(path); writeFileSync(path, bytes, exclusive ? { flag: 'wx' } : undefined); }
async function parentCapture(args) {
  const settings = parseCaptureArguments(args), plan = loadPlan(), oracle = selectCaptureOracle(settings.oracle, plan);
  const goldenPaths = ['expected', 'lock', 'manifest'].map(key => outputPath(plan, key));
  if (!settings.check) validateGoldenAdmission(settings.first, goldenPaths.map(existsSync));
  const pins = await checkCaptureEnvironment(plan, oracle), fixture = parseJsonSupported(verifyPin(repo, plan.input).toString('utf8'));
  assert.equal(fixture.fixtureId, plan.fixtureId); assert.equal(fixture.sourceSha, plan.sourceSha); assert.equal(fixture.cases.length, 3);
  if (settings.check) { const report = { status: 'read-only exact source/archive/installed/runtime/input checks passed; no Agent/source child execution', oracle, sourceFingerprint: pins.sourceFingerprint, installedPackages: pins.installedPackages.map(({ name, version, files }) => ({ name, version, files })), goldenPresent: goldenPaths.map(existsSync), mutations: false }; console.log(rawJson(report)); return report; }
  const harnessFiles = plan.harnessFiles.map(path => { const bytes = readRegular(resolve(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; });
  let locked;
  if (!settings.first) {
    const manifest = readJson(goldenPaths[2]); assert.equal(manifest.fixtureId, plan.fixtureId); assert.equal(manifest.sourceSha, plan.sourceSha); assert(same(manifest.input, plan.input));
    assert.equal(manifest.expected.path, plan.outputs.expected); assert.equal(manifest.lock.path, plan.outputs.lock); assert.equal(fileHash(goldenPaths[0]), manifest.expected.sha256, 'Golden changed before execution'); assert.equal(fileHash(goldenPaths[1]), manifest.lock.sha256, 'Lock changed before execution');
    locked = readJson(goldenPaths[1]); assert(same(locked.environmentPins, pins)); assert(same(locked.harnessFiles, harnessFiles), 'Frozen harness changed before child execution');
    for (const file of locked.loadedModules) { assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || plan.packages.some(row => file.path.startsWith('node_modules/' + row.name + '/')))); assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Locked loaded source changed before execution'); }
  }
  const scratchRoot = outputPath(plan, 'scratch'); mkdirSync(scratchRoot, { recursive: true }); const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-')), marker = Buffer.from('PiSharp-owned-agent-progress-capture-v1\n');
    try {
      writeFileSync(join(scratch, '.owner'), marker, { flag: 'wx' }); mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(resolve(repo, plan.offlineObserver.path)).href, ownPath, '--child'], { cwd: join(scratch, 'workspace'), env: environment(oracle, scratch), windowsHide: true, encoding: 'utf8', timeout: 30000, maxBuffer: 32 * 1024 * 1024 });
      assert.equal(child.status, 0, 'Whole unchanged Agent child failed; report missing prerequisites without substitutes: ' + (child.error?.message ?? child.stderr)); captures.push(parseJsonSupported(child.stdout));
    } finally {
      const target = resolve(scratch); assert(inside(scratchRoot, target), 'Recursive cleanup outside owned scratch root rejected'); noLinks(target); assert.equal(normalized(realpathSync(target)), normalized(target)); assert(readRegular(join(target, '.owner')).equals(marker), 'Scratch ownership changed'); rmSync(target, { recursive: true, force: true });
    }
  }
  assert.equal(rawJson(captures[0]), rawJson(captures[1]), 'Fresh captures differ; no event/opaque/scheduler normalization is allowed');
  const loadedModules = captures[0].loadedModules, sourceHashes = [], env = environment(oracle, join(oracle, 'tmp'));
  assert(loadedModules.some(file => file.path === 'upstream/packages/agent/src/agent.ts')); assert(loadedModules.some(file => file.path === 'upstream/packages/agent/src/agent-loop.ts'));
  for (const file of loadedModules) {
    assert.equal(fileHash(join(oracle, file.path)), file.sha256);
    if (file.path.startsWith('upstream/')) { const blob = git(join(oracle, 'upstream'), ['show', plan.sourceSha + ':' + file.path.slice(9)], env); assert.equal(hash(blob), file.sha256, 'Loaded source differs from canonical Git bytes'); sourceHashes.push({ ...file, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length }); }
    else assert(plan.packages.some(row => file.path.startsWith('node_modules/' + row.name + '/')), 'Loaded package outside exact profile');
  }
  const after = await checkCaptureEnvironment(plan, oracle); assert(same(after, pins), 'Source/dependency/runtime evidence changed during capture');
  assert(same(harnessFiles, plan.harnessFiles.map(path => { const bytes = readRegular(resolve(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; })), 'Harness changed during capture');
  const golden = { schemaVersion: 1, fixtureId: plan.fixtureId, sourceSha: plan.sourceSha, kind: 'captured-unchanged-whole-agent-progress-oracle', observations: captures[0].observations }, actualRaw = rawJson(golden);
  if (settings.first) {
    assert(!goldenPaths.some(existsSync), 'Preserving concurrently created immutable evidence');
    output(plan, 'expected', actualRaw, true);
    output(plan, 'lock', rawJson({ schemaVersion: 1, environmentPins: pins, harnessFiles, loadedModules, sourceHashes, captureHistory: [{ kind: 'initial genuine whole-Agent capture', goldenSha256: hash(Buffer.from(actualRaw)), sourceBytesChanged: false, childDateNowOverride: fixture.clock }] }), true);
    output(plan, 'manifest', rawJson({ schemaVersion: 1, fixtureId: plan.fixtureId, sourceSha: plan.sourceSha, requirementIds: fixture.requirementIds, input: plan.input, expected: { path: plan.outputs.expected, sha256: fileHash(goldenPaths[0]), kind: golden.kind }, lock: { path: plan.outputs.lock, sha256: fileHash(goldenPaths[1]) }, clock: fixture.clock, seed: fixture.seed, normalizerVersion: fixture.normalizerVersion, licenseStatus: plan.licenseStatus, provenance: { source: 'Whole unchanged packages/agent/src/agent.ts and agent-loop.ts public lifecycle', input: fixture.inputKind, callbacks: 'Authored execute/update and beforeToolCall/afterToolCall/finishTurn; two actual Agent subscribers', barrierControl: 'Authored promise gates; no sleeps or source scheduler replacement', emissionSnapshot: 'First source subscriber snapshots each complete event/state at entry before awaiting its barrier', jsonUnsupportedValues: 'JSON.stringify omission and Set {} retained; own undefined/function/Set inventory is separate', credentials: 'None inherited or required; no API key', repeatRuns: 2, byteIdentical: true }, scope: plan.scope }), true);
  } else assert(same(locked.loadedModules, loadedModules) && same(locked.sourceHashes, sourceHashes), 'Observed source/dependency closure differs from immutable lock');
  const matched = compareRawJson(readRegular(goldenPaths[0]).toString('utf8'), actualRaw); output(plan, 'actual', actualRaw);
  const report = { schemaVersion: 1, fixtureId: plan.fixtureId, sourceSha: plan.sourceSha, capturedInitialGolden: settings.first, repeatRuns: 2, byteIdentical: true, matched, sourceCleanAfter: true, installedPackages: plan.packages.length, installedFiles: plan.packages.reduce((sum, row) => sum + row.files.files, 0), loadedUpstreamFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length, cases: golden.observations.cases.map(item => ({ caseId: item.caseId, eventCount: item.events.length, terminal: item.events.at(-1).event.json.type, ...item.metrics })), scope: plan.scope };
  output(plan, 'report', rawJson(report)); console.log(rawJson(report)); assert(matched, 'Whole-Agent observations differ from immutable genuine golden'); return report;
}
export async function main(args = process.argv.slice(2)) {
  if (args[0] === '--child') {
    assert.deepEqual(args, ['--child']); const plan = loadPlan(), oracle = selectCaptureOracle(process.env.PISHARP_REFERENCE_ORACLE, plan), fixture = parseJsonSupported(verifyPin(repo, plan.input).toString('utf8'));
    console.log(JSON.stringify(await captureAgentProgress(oracle, fixture, plan.packages.map(row => row.name))));
  } else return parentCapture(args);
}
if (process.argv[1] && normalized(process.argv[1]) === normalized(ownPath)) await main();
