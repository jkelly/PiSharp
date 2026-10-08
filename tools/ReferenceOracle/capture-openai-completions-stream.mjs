// Genuine source/SDK capture. Default verifies; creation refuses existing evidence.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const family = 'fixtures/reference/openai-completions-stream';
const inputPath = join(repo, family, 'input.json'), expectedPath = join(repo, family, 'expected.json');
const manifestPath = join(repo, family, 'manifest.json');
const lockPath = join(repo, 'tools/ReferenceOracle/openai-completions-stream.lock.json');
const driverPath = join(repo, 'tools/ReferenceOracle/openai-completions-stream-driver.mjs');
const setupPath = join(repo, 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs');
const planPath = join(repo, 'tools/PiReferenceRunner/responses-sdk-install-plan.json');
const setupSha256 = '1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513';
const planSha256 = '0d19249ca9e105602abf304de9c3710e67157bab7209ef67645fd70f77307f22';
const historicalSetupSha256 = '7c4cfc8715344710100d99902defeaef5280169f098cc53cab29f96a90120029';
const qualifiedSdkLockPath = join(repo, 'fixtures/pi-v0.99.1/responses-sdk/oracle.lock.json');
const qualifiedSdkLockSha256 = '8abace9383475c96ce57801353c9febbdde9b164c37106c6db202f8d0bb55f70';
const wrapperPath = 'upstream/packages/ai/src/api/openai-completions.ts';
const wrapperSha256 = '7c1bc51ddf740ce69b371e1ae391fcdcecde26852428451e7b17eeabdcfb4eb4';
const packages = ['openai', 'partial-json', 'typebox'];
const harnessPaths = ['tools/ReferenceOracle/capture-openai-completions-stream.mjs',
  'tools/ReferenceOracle/openai-completions-stream-driver.mjs', 'tools/PiReferenceRunner/full-preload.mjs',
  'tools/PiReferenceRunner/offline-guard.mjs', 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs',
  'tools/PiReferenceRunner/responses-sdk-install-plan.json', 'tools/CompatibilityReport/raw-json.mjs'];
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
function bytes(path, cap = 32 * 1024 * 1024) {
  const stat = lstatSync(path); assert(stat.isFile() && !stat.isSymbolicLink(), 'Regular non-linked file required');
  assert(stat.size <= cap, 'File exceeds evidence byte bound'); return readFileSync(path);
}
const fileHash = path => hash(bytes(path));
const readJson = path => parseJsonSupported(bytes(path).toString('utf8'));
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']'
  : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
const rawJson = value => JSON.stringify(value, null, 2) + '\n';
function inside(root, path) {
  const suffix = relative(resolve(root), resolve(path));
  return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep);
}
function noLinks(path) {
  for (let current = resolve(path); ; current = dirname(current)) {
    assert(!lstatSync(current).isSymbolicLink(), 'Links/junctions are not admitted');
    if (dirname(current) === current) break;
  }
}
function cleanEnvironment(oracle, scratch) {
  const home = join(scratch, 'home');
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', USERPROFILE: home, HOME: home,
    APPDATA: home, LOCALAPPDATA: home, TMP: scratch, TEMP: scratch, TMPDIR: scratch, TZ: 'UTC',
    GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(oracle, 'config', 'gitconfig'), GIT_OPTIONAL_LOCKS: '0',
    PISHARP_REFERENCE_ORACLE: oracle };
}
function tree(root) {
  noLinks(root); const files = [];
  const visit = directory => {
    for (const name of readdirSync(directory).sort()) {
      const path = join(directory, name), stat = lstatSync(path);
      assert(!stat.isSymbolicLink(), 'Dependency links/junctions rejected');
      if (stat.isDirectory()) visit(path);
      else {
        assert(stat.isFile(), 'Dependency special files rejected');
        files.push({ path: relative(root, path).replaceAll('\\', '/'), bytes: stat.size, sha256: fileHash(path) });
      }
    }
  };
  visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
export function parseCaptureArguments(args) {
  let first = false, oracle;
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--capture-new' && !first) first = true;
    else if (args[i] === '--oracle' && !oracle && args[i + 1] && !args[i + 1].startsWith('--')) oracle = resolve(args[++i]);
    else throw new Error('Usage: node capture-openai-completions-stream.mjs [--capture-new] [--oracle APPROVED_SDK_ORACLE]');
  }
  return { first, oracle };
}
function setupCheck(oracle, environment) {
  // R1 correction retained: both calls explicitly forward the reviewed absolute oracle.
  const result = spawnSync(process.execPath, [setupPath, '--check', '--oracle', oracle], {
    cwd: repo, env: environment, windowsHide: true, encoding: 'utf8', timeout: 20000, maxBuffer: 2 * 1024 * 1024 });
  assert.equal(result.status, 0, 'Read-only full source/setup check failed: ' + (result.error?.message ?? result.stderr));
  return parseJsonSupported(result.stdout);
}
async function main(args) {
  const settings = parseCaptureArguments(args);
  assert.equal(fileHash(planPath), planSha256); assert.equal(fileHash(setupPath), setupSha256);
  assert.equal(fileHash(qualifiedSdkLockPath), qualifiedSdkLockSha256, 'Existing qualified SDK receipt changed');
  const plan = readJson(planPath), qualified = readJson(qualifiedSdkLockPath);
  const oracle = settings.oracle ?? resolve(plan.workspace.proposedRoot);
  assert.equal(oracle.toLowerCase(), resolve(plan.workspace.proposedRoot).toLowerCase(), 'Only the approved oracle is admitted');
  noLinks(oracle); noLinks(repo);
  assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.absoluteExecutable).toLowerCase());
  assert.equal(process.version, plan.runtime.version); assert.equal(hash(bytes(process.execPath, 128 * 1024 * 1024)), plan.runtime.sha256);
  assert.equal(process.platform, 'win32'); assert.equal(process.arch, 'x64');
  assert.equal(fileHash(join(oracle, wrapperPath)), wrapperSha256);
  assert.equal(fileHash(join(repo, 'tools/PiReferenceRunner/full-preload.mjs')), 'e1f62a2048bc484a162085430a5afb6f88547d78049b6f29b2a0e4f65fcd79e2');
  assert.equal(fileHash(join(repo, 'tools/PiReferenceRunner/offline-guard.mjs')), 'ae3741bdce496451bd04afcf8628df6ddc5bc5cfad8af6ee5e52cbc7b065a094');
  const inputBytes = bytes(inputPath, 256 * 1024), fixture = parseJsonSupported(inputBytes.toString('utf8'));
  assert.equal(fixture.sourceSha, plan.source.commit); assert.equal(fixture.fixtureId, 'openai-completions-stream-three-cases');
  assert.equal(fixture.cases.length, 3);
  if (settings.first) assert(![expectedPath, manifestPath, lockPath].some(existsSync), 'Creation refuses existing golden/manifest/lock');
  const restoredPath = join(oracle, '.pisharp-responses-sdk-restored.json'), restored = readJson(restoredPath);
  assert.equal(restored.owner.planSha256, planSha256); assert.equal(restored.owner.helperSha256, historicalSetupSha256);
  assert.equal(restored.status, 'three dependencies installed; wrapper qualification pending');
  assert.equal(fileHash(restoredPath), qualified.environmentPins.setupReceiptSha256);
  assert.equal(fileHash(join(oracle, '.pisharp-responses-sdk-prepared.json')), qualified.environmentPins.preparedReceiptSha256);
  assert.equal(fileHash(join(oracle, 'package.json')), qualified.environmentPins.projectionManifestSha256);
  assert.equal(fileHash(join(oracle, 'package-lock.json')), qualified.environmentPins.projectionLockSha256);
  assert(same(readdirSync(join(oracle, 'node_modules')).filter(name => name !== '.package-lock.json').sort(), packages));
  const outputRoot = join(repo, 'artifacts/openai-completions-stream-reference'), scratchRoot = join(outputRoot, 'scratch');
  mkdirSync(scratchRoot, { recursive: true }); noLinks(scratchRoot);
  const parentScratch = mkdtempSync(join(scratchRoot, 'parent-'));
  try {
    mkdirSync(join(parentScratch, 'home'));
    const environment = cleanEnvironment(oracle, parentScratch), preflight = setupCheck(oracle, environment);
    assert(same(preflight.sourceFingerprint, restored.sourceFingerprint));
    const { inspectPackageArchive } = await import(pathToFileURL(setupPath).href);
    const dependencies = plan.packages.map(row => {
      const archiveBytes = bytes(join(oracle, 'archives', row.name + '-' + row.lockEntry.version + '.tgz'));
      assert.equal('sha512-' + hash(archiveBytes, 'sha512', 'base64'), row.lockEntry.integrity);
      const archive = inspectPackageArchive(archiveBytes), files = tree(join(oracle, 'node_modules', row.name));
      const archiveFiles = [...archive.files].map(([path, data]) => ({ path, bytes: data.length, sha256: hash(data) }))
        .sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
      assert(same(files, archiveFiles), 'Installed tree differs from exact archive: ' + row.name);
      const receipt = restored.installed.find(item => item.name === row.name);
      assert.equal(hash(Buffer.from(canonical(files))), receipt.files.sha256);
      assert.equal(files.length, receipt.files.files); assert.equal(hash(archiveBytes), receipt.archiveSha256);
      for (const license of receipt.licenses) assert.equal(hash(archive.files.get(license.path)), license.sha256);
      return { name: row.name, version: row.lockEntry.version, archiveSha256: hash(archiveBytes), integrity: row.lockEntry.integrity,
        files: receipt.files, manifestSha256: receipt.manifestSha256, licenses: receipt.licenses };
    });
    const harnessFiles = harnessPaths.map(path => ({ path, bytes: bytes(join(repo, path)).length, sha256: fileHash(join(repo, path)) }));
    const environmentPins = { sourceSha: plan.source.commit, sourceFingerprint: preflight.sourceFingerprint,
      runtime: plan.runtime, platform: process.platform, architecture: process.arch,
      qualifiedSdkLock: { path: 'fixtures/pi-v0.99.1/responses-sdk/oracle.lock.json', sha256: qualifiedSdkLockSha256 },
      setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-responses-sdk-prepared.json')),
      projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')), dependencies };
    let locked;
    if (!settings.first) {
      const manifest = readJson(manifestPath); assert.equal(manifest.fixtureId, fixture.fixtureId); assert.equal(manifest.sourceSha, fixture.sourceSha);
      assert.equal(manifest.input.path, family + '/input.json'); assert.equal(manifest.expected.path, family + '/expected.json');
      assert.equal(manifest.lock.path, 'tools/ReferenceOracle/openai-completions-stream.lock.json');
      assert.equal(fileHash(inputPath), manifest.input.sha256); assert.equal(fileHash(expectedPath), manifest.expected.sha256);
      assert.equal(fileHash(lockPath), manifest.lock.sha256); locked = readJson(lockPath);
      assert(same(environmentPins, locked.environmentPins) && same(harnessFiles, locked.harnessFiles), 'Environment/harness changed before execution');
      for (const file of locked.loadedModules) {
        assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || packages.some(name => file.path.startsWith('node_modules/' + name + '/'))));
        assert.equal(fileHash(join(oracle, file.path)), file.sha256);
      }
    }
    const captures = [];
    for (let repeat = 0; repeat < 2; repeat++) {
      const scratch = mkdtempSync(join(scratchRoot, 'child-'));
      try {
        mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
        const child = spawnSync(process.execPath, ['--max-old-space-size=256', '--experimental-strip-types', '--import',
          pathToFileURL(join(repo, 'tools/PiReferenceRunner/full-preload.mjs')).href, driverPath], {
          cwd: join(scratch, 'workspace'), env: cleanEnvironment(oracle, scratch), windowsHide: true,
          encoding: 'utf8', timeout: 20000, maxBuffer: 16 * 1024 * 1024 });
        assert.equal(child.status, 0, 'Genuine wrapper/SDK capture failed: ' + (child.error?.message ?? child.stderr));
        const capture = parseJsonSupported(child.stdout);
        writeFileSync(join(outputRoot, (settings.first ? 'creation' : 'verification') + '-' + repeat + '.json'), rawJson(capture));
        captures.push(capture);
      } finally { assert(inside(scratchRoot, scratch)); rmSync(scratch, { recursive: true, force: true }); }
    }
    assert.equal(rawJson(captures[0]), rawJson(captures[1]), 'Fresh captures differ; no opaque/dynamic field normalization allowed');
    const loadedModules = captures[0].loadedModules;
    assert(loadedModules.some(file => file.path === wrapperPath) && loadedModules.some(file => file.path.startsWith('node_modules/openai/')));
    if (locked) assert(same(loadedModules, locked.loadedModules), 'Actual loaded closure differs from lock');
    const sourceHashes = [];
    for (const file of loadedModules) {
      assert.equal(fileHash(join(oracle, file.path)), file.sha256);
      if (file.path.startsWith('upstream/')) {
        const git = spawnSync('C:\\Program Files\\Git\\cmd\\git.exe', ['-c', 'safe.directory=' + join(oracle, 'upstream'),
          '-C', join(oracle, 'upstream'), 'show', plan.source.commit + ':' + file.path.slice('upstream/'.length)], {
          cwd: repo, env: environment, windowsHide: true, timeout: 20000, maxBuffer: 16 * 1024 * 1024 });
        assert.equal(git.status, 0); assert.equal(hash(git.stdout), file.sha256, 'Executed source differs from canonical blob');
        sourceHashes.push({ ...file, canonicalGitBlobSha256: hash(git.stdout) });
      } else assert(packages.some(name => file.path.startsWith('node_modules/' + name + '/')));
    }
    const after = setupCheck(oracle, environment); assert(same(after.sourceFingerprint, preflight.sourceFingerprint));
    for (const row of dependencies) assert.equal(hash(Buffer.from(canonical(tree(join(oracle, 'node_modules', row.name))))), row.files.sha256);
    assert(same(harnessFiles, harnessPaths.map(path => ({ path, bytes: bytes(join(repo, path)).length, sha256: fileHash(join(repo, path)) }))));
    assert.equal(fileHash(inputPath), hash(inputBytes));
    const golden = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha,
      kind: 'captured-unchanged-openai-completions-wrapper-sdk', observations: captures[0].observations };
    const actualRaw = rawJson(golden);
    if (settings.first) {
      writeFileSync(expectedPath, actualRaw, { flag: 'wx' });
      writeFileSync(lockPath, rawJson({ schemaVersion: 1, environmentPins, harnessFiles, loadedModules, sourceHashes,
        capture: { freshRuns: 2, noSourceSdkChanges: true, clockSeam: fixture.clock, goldenSha256: hash(Buffer.from(actualRaw)) } }), { flag: 'wx' });
      writeFileSync(manifestPath, rawJson({ schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha,
        requirementIds: fixture.requirementIds, input: { path: family + '/input.json', sha256: fileHash(inputPath), kind: fixture.kind },
        expected: { path: family + '/expected.json', sha256: fileHash(expectedPath), kind: golden.kind },
        lock: { path: 'tools/ReferenceOracle/openai-completions-stream.lock.json', sha256: fileHash(lockPath) },
        nativeComparison: 'not performed', independentAcceptance: 'pending' }), { flag: 'wx' });
    } else assert.equal(actualRaw, bytes(expectedPath).toString('utf8'), 'Complete exact observation bytes differ from unchanged golden');
    console.log(rawJson({ status: settings.first ? 'new genuine golden captured twice' : 'immutable genuine golden verified twice',
      cases: golden.observations.cases.length, loadedSourceFiles: sourceHashes.length,
      loadedDependencyFiles: loadedModules.length - sourceHashes.length, goldenSha256: fileHash(expectedPath),
      nativeComparison: false, noNetworkInstallSourceSdkMutation: true }));
  } finally { assert(inside(scratchRoot, parentScratch)); rmSync(parentScratch, { recursive: true, force: true }); }
}
if (process.argv[1] && resolve(process.argv[1]) === ownPath) await main(process.argv.slice(2));
