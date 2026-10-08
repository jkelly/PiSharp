import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const toolRoot = dirname(fileURLToPath(import.meta.url));
const repo = resolve(toolRoot, '../..');
const args = process.argv.slice(2);
const oracleIndex = args.indexOf('--oracle');
if (oracleIndex >= 0 && (!args[oracleIndex + 1] || args[oracleIndex + 1].startsWith('--'))) throw new Error('--oracle requires the approved task-local oracle directory');
const acceptedArgs = new Set(['--capture-new', '--oracle']);
for (let index = 0; index < args.length; index++) {
  if (!acceptedArgs.has(args[index])) throw new Error(`Unknown argument: ${args[index]}`);
  if (args[index] === '--oracle') index++;
}
const oracle = oracleIndex < 0 ? resolve(repo, '../Pi-reference-oracle-v0.99.1') : resolve(args[oracleIndex + 1]);
const upstream = join(oracle, 'upstream');
const inputPath = join(repo, 'fixtures/pi-v0.99.1/loop-continuation/core.input.json');
const expectedPath = join(repo, 'fixtures/pi-v0.99.1/loop-continuation/core.expected.json');
const manifestPath = join(repo, 'fixtures/pi-v0.99.1/loop-continuation/manifest.json');
const lockPath = join(toolRoot, 'loop-continuation-lock.json');
const environmentLockPath = join(toolRoot, 'full-lock.json');
const outputRoot = join(repo, 'artifacts/loop-continuation-reference');
const scratchRoot = join(outputRoot, 'scratch');
const firstCapture = args.includes('--capture-new');
const captureKind = 'captured-upstream-loop-continuation-oracle';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const read = path => parseJsonSupported(readFileSync(path, 'utf8'));
if (firstCapture && [expectedPath, manifestPath, lockPath].some(existsSync)) throw new Error('New loop continuation capture refuses to overwrite a golden, manifest or lock');
const environmentLock = read(environmentLockPath);
const pins = environmentLock.environmentPins;
const fixture = read(inputPath);
if (fixture.kind !== 'authored-loop-continuation-input' || fixture.normalizerVersion !== 'object-key-order-v1') throw new Error('Unexpected loop continuation input provenance/normalizer');
const harnessPaths = ['tools/PiReferenceRunner/loop-continuation-run.mjs', 'tools/PiReferenceRunner/loop-continuation-capture.mjs', 'tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs'];
function git(...gitArgs) {
  const child = spawnSync('C:/Program Files/Git/cmd/git.exe', ['-c', `safe.directory=${upstream}`, '-C', upstream, ...gitArgs], { windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
  if (child.status !== 0) throw new Error(child.error?.message ?? child.stderr.toString());
  return child.stdout;
}
const consultedSourceHashes = ['packages/agent/src/types.ts'].map(path => {
  const bytes = readFileSync(join(upstream, path));
  const blob = git('show', `${pins.sourceSha}:${path}`);
  if (!bytes.equals(blob)) throw new Error(`Consulted source differs from pinned Git bytes: ${path}`);
  return { path: `upstream/${path}`, sha256: hash(bytes), bytes: bytes.length, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length, use: 'Read-only public callback contract; type-only module is not runtime-loaded' };
});
function verifyEnvironment() {
  if (fixture.sourceSha !== pins.sourceSha || git('rev-parse', 'HEAD').toString().trim() !== pins.sourceSha || git('rev-parse', 'HEAD^{tree}').toString().trim() !== pins.sourceTree || git('status', '--porcelain', '--untracked-files=all').toString().trim()) throw new Error('Loop continuation oracle source must be clean at the locked SHA/tree');
  if (process.version !== pins.runtime.version || fileHash(process.execPath) !== pins.runtime.sha256) throw new Error('Loop continuation Node executable differs from runtime pin');
  if (fileHash(join(oracle, 'package.json')) !== pins.projectionManifestSha256 || fileHash(join(oracle, 'package-lock.json')) !== pins.projectionLockSha256) throw new Error('Oracle dependency projection differs from qualified environment');
  if (JSON.stringify(readdirSync(join(oracle, 'node_modules')).filter(name => !name.startsWith('.')).sort()) !== JSON.stringify(pins.dependencies.map(dependency => dependency.name).sort())) throw new Error('Unexpected installed oracle dependency package');
  for (const dependency of pins.dependencies) {
    const root = join(oracle, 'node_modules', dependency.name);
    const files = [];
    function visit(directory) {
      for (const name of readdirSync(directory).sort()) {
        const path = join(directory, name);
        const stat = lstatSync(path);
        if (stat.isSymbolicLink()) throw new Error('Unexpected link in installed dependency tree');
        if (stat.isDirectory()) visit(path);
        else if (stat.isFile()) files.push({ path: relative(root, path).replaceAll('\\', '/'), sha256: fileHash(path), bytes: stat.size });
        else throw new Error('Unexpected installed dependency file type');
      }
    }
    visit(root);
    const manifest = read(join(root, 'package.json'));
    if (manifest.name !== dependency.name || manifest.version !== dependency.version || fileHash(join(root, 'package.json')) !== dependency.manifestSha256 || hash(JSON.stringify(files)) !== dependency.treeSha256 || files.length !== dependency.fileCount || files.reduce((sum, file) => sum + file.bytes, 0) !== dependency.bytes) throw new Error(`Installed package bytes differ from qualified pin: ${dependency.name}`);
  }
  for (const path of ['tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs']) {
    const qualified = environmentLock.harnessFiles.find(file => file.path === path);
    if (!qualified || fileHash(join(repo, path)) !== qualified.sha256) throw new Error(`Reused loader/guard/parser differs from qualified harness: ${path}`);
  }
  for (const file of [...environmentLock.sourceHashes, ...consultedSourceHashes]) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Qualified upstream source bytes changed: ${file.path}`);
}
function verifyLockAndFixture() {
  const lock = read(lockPath);
  if (lock.captureKind !== captureKind || lock.sourceSha !== fixture.sourceSha || fileHash(environmentLockPath) !== lock.environmentLockSha256 || !compareRawJson(JSON.stringify(lock.environmentPins), JSON.stringify(pins))) throw new Error('Loop continuation source/environment lock changed');
  if (!compareRawJson(JSON.stringify(lock.consultedSourceHashes), JSON.stringify(consultedSourceHashes))) throw new Error('Consulted public source pins changed');
  for (const file of lock.harnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Loop continuation harness bytes changed: ${file.path}`);
  for (const file of lock.loadedModules) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Loaded oracle module bytes changed: ${file.path}`);
  const manifest = read(manifestPath);
  const row = manifest.fixtures[0];
  if (manifest.sourceSha !== fixture.sourceSha || manifest.normalizerVersion !== fixture.normalizerVersion || row.fixtureId !== fixture.fixtureId || row.sourceSha !== fixture.sourceSha || row.provenance.kind !== captureKind || !compareRawJson(JSON.stringify(row.clock), JSON.stringify(fixture.clock)) || fileHash(inputPath) !== row.input.sha256 || fileHash(expectedPath) !== row.expected.sha256) throw new Error('Loop continuation fixture identity/checksum mismatch');
  return lock;
}
verifyEnvironment();
const priorLock = firstCapture ? undefined : verifyLockAndFixture();
const initialHarnessFiles = harnessPaths.map(path => ({ path, sha256: fileHash(join(repo, path)) }));
const initialInputSha256 = fileHash(inputPath);
const initialEnvironmentLockSha256 = fileHash(environmentLockPath);
mkdirSync(scratchRoot, { recursive: true });
const captures = [];
const rawCaptures = [];
for (let repeat = 0; repeat < 2; repeat++) {
  const isolated = mkdtempSync(join(scratchRoot, 'capture-'));
  try {
    const home = join(isolated, 'home');
    const workspace = join(isolated, 'workspace');
    mkdirSync(home);
    mkdirSync(workspace);
    const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(toolRoot, 'full-preload.mjs')).href, join(toolRoot, 'loop-continuation-capture.mjs'), inputPath], {
      cwd: workspace,
      env: { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: isolated, TEMP: isolated, TZ: 'UTC', PISHARP_REFERENCE_ORACLE: oracle },
      encoding: 'utf8', windowsHide: true, timeout: 20000, maxBuffer: 16 * 1024 * 1024,
    });
    if (child.status !== 0) throw new Error(`Loop continuation capture failed: ${child.error?.message ?? child.stderr}`);
    rawCaptures.push(child.stdout);
    captures.push(parseJsonSupported(child.stdout));
  } finally {
    const target = resolve(isolated);
    const within = relative(resolve(scratchRoot), target);
    if (!within || isAbsolute(within) || within.startsWith('..')) throw new Error('Refusing cleanup outside loop continuation scratch root');
    rmSync(target, { recursive: true, force: true });
  }
}
if (rawCaptures[0] !== rawCaptures[1]) throw new Error('Loop continuation repeated captures are not byte-identical');
verifyEnvironment();
if (fileHash(inputPath) !== initialInputSha256 || fileHash(environmentLockPath) !== initialEnvironmentLockSha256) throw new Error('Input/environment lock changed during capture');
for (const file of initialHarnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Harness changed during capture: ${file.path}`);
const loadedModules = captures[0].loadedModules;
const sourceHashes = [];
for (const module of loadedModules) {
  const qualified = environmentLock.loadedModules.find(file => file.path === module.path);
  if (!qualified || qualified.sha256 !== module.sha256 || qualified.bytes !== module.bytes) throw new Error(`Runtime loop closure escaped qualified source/dependencies: ${module.path}`);
  if (module.path.startsWith('upstream/')) {
    const blob = git('show', `${pins.sourceSha}:${module.path.slice('upstream/'.length)}`);
    if (hash(blob) !== module.sha256 || blob.length !== module.bytes) throw new Error(`Runtime source differs from canonical Git blob: ${module.path}`);
    sourceHashes.push({ ...module, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length });
  }
}
if (priorLock) {
  verifyLockAndFixture();
  if (!compareRawJson(JSON.stringify(priorLock.loadedModules), JSON.stringify(loadedModules)) || !compareRawJson(JSON.stringify(priorLock.sourceHashes), JSON.stringify(sourceHashes))) throw new Error('Runtime loop continuation closure differs from locked capture');
}
const actual = { fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: captureKind, normalizerVersion: fixture.normalizerVersion, observations: captures[0].observations };
const actualRaw = `${JSON.stringify(actual, null, 2)}\n`;
writeFileSync(join(outputRoot, 'core.actual.json'), actualRaw);
for (let repeat = 0; repeat < rawCaptures.length; repeat++) writeFileSync(join(outputRoot, `capture-${repeat + 1}.raw.json`), rawCaptures[repeat]);
if (firstCapture) {
  // Exclusive creation is an additional guard against overwriting an intervening file.
  writeFileSync(lockPath, `${JSON.stringify({ schemaVersion: 1, captureKind, sourceSha: pins.sourceSha, environmentLockPath: 'tools/PiReferenceRunner/full-lock.json', environmentLockSha256: initialEnvironmentLockSha256, environmentPins: pins, consultedSourceHashes, loadedModules, sourceHashes, harnessFiles: initialHarnessFiles }, null, 2)}\n`, { flag: 'wx' });
  writeFileSync(expectedPath, actualRaw, { flag: 'wx' });
  writeFileSync(manifestPath, `${JSON.stringify({ schemaVersion: 1, sourceSha: pins.sourceSha, normalizerVersion: fixture.normalizerVersion, scope: 'Two genuine runAgentLoop observations at authored fake provider/tool/callback seams: automatic tool continuation and steering-before-follow-up; high-level Agent queue modes, production providers and native parity excluded', fixtures: [{ fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, requirementIds: fixture.requirementIds, scenario: fixture.scenario, scenarioIds: fixture.scenarios.map(scenario => scenario.scenarioId), clock: fixture.clock, seed: fixture.seed, environment: { platform: process.platform, architecture: process.arch, credentials: 'not inherited', network: 'disabled by existing offline guard', workspace: 'fresh isolated temporary home/workspace' }, input: { path: 'fixtures/pi-v0.99.1/loop-continuation/core.input.json', sha256: fileHash(inputPath) }, expected: { path: 'fixtures/pi-v0.99.1/loop-continuation/core.expected.json', sha256: fileHash(expectedPath) }, provenance: { kind: captureKind, inputKind: 'authored-synthetic-input', source: 'Unchanged pinned runAgentLoop, explicit fake streamFn/tools and public steering/follow-up/turn/request callbacks; disclosed harness Date.now override', resolver: 'Unchanged pinned packages/coding-agent/src/experimental/source-resolver.ts', sourceSha: fixture.sourceSha, dependencyLock: 'tools/PiReferenceRunner/loop-continuation-lock.json', environmentLock: 'tools/PiReferenceRunner/full-lock.json', capturedAt: '2026-09-30', captureCommand: 'node tools/PiReferenceRunner/loop-continuation-run.mjs --capture-new', providerWireTraffic: false, nativeDifferential: false, highLevelAgentQueueModes: false } }] }, null, 2)}\n`, { flag: 'wx' });
}
const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
const report = {
  schemaVersion: 1,
  fixtureId: fixture.fixtureId,
  sourceSha: pins.sourceSha,
  captureKind,
  capturedInitialGolden: firstCapture,
  repeatRuns: 2,
  deterministic: true,
  byteIdenticalRepeats: true,
  rawCaptureSha256: hash(rawCaptures[0]),
  inputSha256: fileHash(inputPath),
  expectedSha256: fileHash(expectedPath),
  lockSha256: fileHash(lockPath),
  matched,
  sourceCleanBefore: true,
  sourceCleanAfter: true,
  loadedUpstreamSourceFiles: sourceHashes.length,
  consultedTypeOnlySourceFiles: consultedSourceHashes.length,
  loadedDependencyFiles: loadedModules.length - sourceHashes.length,
  environmentLockSha256: initialEnvironmentLockSha256,
  checks: actual.observations.scenarios.map(scenario => ({ scenarioId: scenario.scenarioId, ...scenario.checks })),
  scope: 'Unchanged public low-level loop under disclosed fake clock/provider/tool/callback environment; no high-level Agent queue-mode, native differential, provider-wire or full-phase qualification',
};
writeFileSync(join(outputRoot, 'loop-continuation-reference-report.json'), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report, null, 2));
if (!matched) process.exitCode = 1;
