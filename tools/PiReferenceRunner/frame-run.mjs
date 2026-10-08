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
if (oracleIndex >= 0 && (!args[oracleIndex + 1] || args[oracleIndex + 1].startsWith('--'))) throw new Error('--oracle requires the approved oracle directory');
const oracle = oracleIndex < 0 ? resolve(repo, '../Pi-reference-oracle-v0.99.1') : resolve(args[oracleIndex + 1]);
const upstream = join(oracle, 'upstream');
const inputPath = join(repo, 'fixtures/pi-v0.99.1/frame/interleaved-signed.input.json');
const expectedPath = join(repo, 'fixtures/pi-v0.99.1/frame/interleaved-signed.expected.json');
const manifestPath = join(repo, 'fixtures/pi-v0.99.1/frame/manifest.json');
const lockPath = join(toolRoot, 'frame-lock.json');
const environmentLockPath = join(toolRoot, 'full-lock.json');
const outputRoot = join(repo, 'artifacts/frame-reference');
const scratchRoot = join(outputRoot, 'scratch');
const firstCapture = args.includes('--capture-new');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const read = path => parseJsonSupported(readFileSync(path, 'utf8'));
const environmentLock = read(environmentLockPath);
const pins = environmentLock.environmentPins;
const fixture = read(inputPath);
const harnessPaths = ['tools/PiReferenceRunner/frame-run.mjs', 'tools/PiReferenceRunner/frame-capture.mjs', 'tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs'];
function git(...gitArgs) {
  const child = spawnSync('C:/Program Files/Git/cmd/git.exe', ['-c', `safe.directory=${upstream}`, '-C', upstream, ...gitArgs], { windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
  if (child.status !== 0) throw new Error(child.stderr.toString());
  return child.stdout;
}
function verifyEnvironment() {
  if (fixture.sourceSha !== pins.sourceSha || git('rev-parse', 'HEAD').toString().trim() !== pins.sourceSha || git('rev-parse', 'HEAD^{tree}').toString().trim() !== pins.sourceTree || git('status', '--porcelain', '--untracked-files=all').toString().trim()) throw new Error('Frame oracle source must be unchanged at the locked SHA/tree');
  if (process.version !== pins.runtime.version || fileHash(process.execPath) !== pins.runtime.sha256) throw new Error('Frame reference Node differs from runtime pin');
  if (fileHash(join(oracle, 'package.json')) !== pins.projectionManifestSha256 || fileHash(join(oracle, 'package-lock.json')) !== pins.projectionLockSha256) throw new Error('Installed dependency projection differs from qualified environment');
  if (JSON.stringify(readdirSync(join(oracle, 'node_modules')).filter(name => !name.startsWith('.')).sort()) !== JSON.stringify(pins.dependencies.map(dependency => dependency.name).sort())) throw new Error('Unexpected installed package in frame oracle');
  for (const dependency of pins.dependencies) {
    const root = join(oracle, 'node_modules', dependency.name);
    const files = [];
    function visit(directory) {
      for (const name of readdirSync(directory).sort()) {
        const path = join(directory, name); const stat = lstatSync(path);
        if (stat.isSymbolicLink()) throw new Error('Unexpected dependency symlink');
        if (stat.isDirectory()) visit(path);
        else if (stat.isFile()) files.push({ path: relative(root, path).replaceAll('\\', '/'), sha256: fileHash(path), bytes: stat.size });
        else throw new Error('Unexpected dependency file type');
      }
    }
    visit(root);
    const manifest = read(join(root, 'package.json'));
    if (manifest.name !== dependency.name || manifest.version !== dependency.version || fileHash(join(root, 'package.json')) !== dependency.manifestSha256 || hash(JSON.stringify(files)) !== dependency.treeSha256 || files.length !== dependency.fileCount) throw new Error(`Installed package bytes differ from qualified pin: ${dependency.name}`);
  }
  for (const path of ['tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs']) {
    const qualified = environmentLock.harnessFiles.find(file => file.path === path);
    if (!qualified || fileHash(join(repo, path)) !== qualified.sha256) throw new Error(`Reused loader/guard/parser differs from qualified harness: ${path}`);
  }
  for (const file of environmentLock.sourceHashes) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Qualified upstream source bytes changed: ${file.path}`);
}
verifyEnvironment();
if (firstCapture) {
  if ([expectedPath, manifestPath, lockPath].some(existsSync)) throw new Error('New frame capture refuses to overwrite a golden, manifest or lock');
} else {
  const lock = read(lockPath);
  if (fileHash(environmentLockPath) !== lock.environmentLockSha256) throw new Error('Qualified environment lock changed');
  for (const file of lock.harnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Frame harness changed before execution: ${file.path}`);
  for (const file of lock.loadedModules) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Loaded frame module changed before execution: ${file.path}`);
  const manifest = read(manifestPath).fixtures[0];
  if (manifest.fixtureId !== fixture.fixtureId || manifest.sourceSha !== fixture.sourceSha || fileHash(inputPath) !== manifest.input.sha256 || fileHash(expectedPath) !== manifest.expected.sha256) throw new Error('Frame fixture identity/checksum mismatch before capture');
}
mkdirSync(scratchRoot, { recursive: true });
const captures = [];
for (let repeat = 0; repeat < 2; repeat++) {
  const isolated = mkdtempSync(join(scratchRoot, 'capture-'));
  try {
    const home = join(isolated, 'home'); const workspace = join(isolated, 'workspace');
    mkdirSync(home); mkdirSync(workspace);
    const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(toolRoot, 'full-preload.mjs')).href, join(toolRoot, 'frame-capture.mjs'), inputPath], {
      cwd: workspace, env: { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: isolated, TEMP: isolated, TZ: 'UTC', PISHARP_REFERENCE_ORACLE: oracle },
      encoding: 'utf8', windowsHide: true, timeout: 10000, maxBuffer: 4 * 1024 * 1024,
    });
    if (child.status !== 0) throw new Error(`Frame capture failed: ${child.error?.message ?? child.stderr}`);
    captures.push(parseJsonSupported(child.stdout));
  } finally {
    const target = resolve(isolated); const within = relative(resolve(scratchRoot), target);
    if (!within || isAbsolute(within) || within.startsWith('..')) throw new Error('Refusing cleanup outside frame scratch root');
    rmSync(target, { recursive: true, force: true });
  }
}
if (!compareRawJson(JSON.stringify(captures[0]), JSON.stringify(captures[1]))) throw new Error('Frame oracle repeated capture differs');
verifyEnvironment();
const loadedModules = captures[0].loadedModules;
const sourceHashes = [];
for (const module of loadedModules) {
  const qualified = environmentLock.loadedModules.find(file => file.path === module.path);
  if (!qualified || qualified.sha256 !== module.sha256 || qualified.bytes !== module.bytes) throw new Error(`Runtime frame closure escaped qualified source/dependencies: ${module.path}`);
  if (module.path.startsWith('upstream/')) {
    const blob = git('show', `${pins.sourceSha}:${module.path.slice('upstream/'.length)}`);
    sourceHashes.push({ ...module, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length });
  }
}
const actual = { fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: 'captured-upstream-frame-oracle', normalizerVersion: fixture.normalizerVersion, observations: captures[0].observations };
const actualRaw = `${JSON.stringify(actual, null, 2)}\n`;
writeFileSync(join(outputRoot, 'interleaved-signed.actual.json'), actualRaw);
if (firstCapture) {
  writeFileSync(lockPath, `${JSON.stringify({ schemaVersion: 1, sourceSha: pins.sourceSha, environmentLockPath: 'tools/PiReferenceRunner/full-lock.json', environmentLockSha256: fileHash(environmentLockPath), loadedModules, sourceHashes, harnessFiles: harnessPaths.map(path => ({ path, sha256: fileHash(join(repo, path)) })) }, null, 2)}\n`);
  writeFileSync(expectedPath, actualRaw);
  writeFileSync(manifestPath, `${JSON.stringify({ schemaVersion: 1, sourceSha: pins.sourceSha, normalizerVersion: fixture.normalizerVersion, scope: 'One genuine unchanged upstream frame encoder/reducer scenario; no native projection or provider wire qualification', fixtures: [{ fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, requirementIds: fixture.requirementIds, scenario: fixture.scenario, clock: fixture.clock, seed: fixture.seed, environment: { platform: process.platform, architecture: process.arch, credentials: 'not inherited', network: 'disabled', workspace: 'isolated home/workspace' }, input: { path: 'fixtures/pi-v0.99.1/frame/interleaved-signed.input.json', sha256: fileHash(inputPath) }, expected: { path: 'fixtures/pi-v0.99.1/frame/interleaved-signed.expected.json', sha256: fileHash(expectedPath) }, provenance: { kind: 'captured-upstream-frame-oracle', inputKind: 'authored-synthetic-input', source: 'Unmodified pinned AssistantMessageFrameEncoder, reduceAssistantMessageFrames and parseStreamingJson', sourceSha: fixture.sourceSha, dependencyLock: 'tools/PiReferenceRunner/frame-lock.json', capturedAt: '2026-09-30', captureCommand: 'node tools/PiReferenceRunner/frame-run.mjs --capture-new', nativeProjection: false, providerWireTraffic: false } }] }, null, 2)}\n`);
} else {
  const lock = read(lockPath);
  if (!compareRawJson(JSON.stringify(lock.loadedModules), JSON.stringify(loadedModules)) || !compareRawJson(JSON.stringify(lock.sourceHashes), JSON.stringify(sourceHashes))) throw new Error('Loaded frame closure differs from captured source/dependency lock');
}
const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
const report = { schemaVersion: 1, fixtureId: fixture.fixtureId, sourceSha: pins.sourceSha, capturedInitialGolden: firstCapture, repeatRuns: 2, deterministic: true, matched, sourceCleanAfter: true, loadedUpstreamSourceFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length, environmentLockSha256: fileHash(environmentLockPath), checks: actual.observations.checks, scope: 'Genuine frame encoder/reducer at one synthetic input seam; no native/provider/full phase parity claim' };
writeFileSync(join(outputRoot, 'frame-reference-report.json'), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report, null, 2));
if (!matched) process.exitCode = 1;
