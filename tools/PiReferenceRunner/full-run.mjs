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
if (oracleIndex >= 0 && (!args[oracleIndex + 1] || args[oracleIndex + 1].startsWith('--'))) throw new Error('--oracle requires the approved task-local oracle path');
const oracle = oracleIndex >= 0 ? resolve(args[oracleIndex + 1]) : resolve(repo, '../Pi-reference-oracle-v0.99.1');
const upstream = join(oracle, 'upstream');
const fixturePath = join(repo, 'fixtures/pi-v0.99.1/agent/awaited-parallel.input.json');
const expectedPath = join(repo, 'fixtures/pi-v0.99.1/agent/awaited-parallel.expected.json');
const manifestPath = join(repo, 'fixtures/pi-v0.99.1/agent/manifest.json');
const lockPath = join(toolRoot, 'full-lock.json');
const outputRoot = join(repo, 'artifacts/full-reference');
const scratchRoot = join(outputRoot, 'scratch');
const firstCapture = process.argv.includes('--capture-new');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const read = path => parseJsonSupported(readFileSync(path, 'utf8'));
const plan = read(join(repo, 'compatibility/reference-oracle-install-plan.json'));
const setup = read(join(oracle, '.pisharp-oracle-setup.json'));
const fixture = read(fixturePath);
function git(...args) {
  const result = spawnSync('C:/Program Files/Git/cmd/git.exe', ['-c', `safe.directory=${upstream}`, '-C', upstream, ...args], { windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(result.stderr.toString());
  return result.stdout;
}
function cleanSource() {
  if (git('rev-parse', 'HEAD').toString().trim() !== plan.source.commit || git('status', '--porcelain', '--untracked-files=all').toString().trim()) throw new Error('Oracle source must remain clean at the pinned SHA');
  if (fileHash(join(upstream, plan.source.lockPath)) !== plan.source.lockSha256) throw new Error('Oracle upstream lock differs from setup pin');
}
function dependencyTree(dependency) {
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
  const installed = setup.installed.find(row => row.name === dependency.name);
  if (manifest.name !== dependency.name || manifest.version !== dependency.version || fileHash(join(root, 'package.json')) !== installed?.manifestSha256) throw new Error('Installed package manifest differs from approved setup');
  return { name: dependency.name, version: dependency.version, integrity: dependency.integrity, manifestSha256: installed.manifestSha256, treeSha256: hash(JSON.stringify(files)), fileCount: files.length, bytes: files.reduce((sum, file) => sum + file.bytes, 0) };
}
if (setup.owner !== 'PiSharp-reference-setup-v1' || setup.sourceCommit !== plan.source.commit || setup.npmVersion !== plan.packageManager.version || !setup.status.startsWith('dependencies-installed')) throw new Error('Approved minimal oracle setup is not ready');
if (process.version !== plan.runtime.version || fileHash(process.execPath) !== plan.runtime.sha256) throw new Error('Reference Node executable differs from pin');
if (fixture.sourceSha !== plan.source.commit) throw new Error('Agent fixture source SHA differs from pin');
if (fileHash(join(oracle, 'package.json')) !== fileHash(join(repo, plan.projection.manifestPath)) || fileHash(join(oracle, 'package-lock.json')) !== fileHash(join(repo, plan.projection.lockPath))) throw new Error('Oracle dependency projection differs from reviewed setup inputs');
if (JSON.stringify(readdirSync(join(oracle, 'node_modules')).filter(name => !name.startsWith('.')).sort()) !== JSON.stringify(plan.dependencies.map(dependency => dependency.name).sort())) throw new Error('Unexpected installed dependency package');
cleanSource();
const dependencies = plan.dependencies.map(dependencyTree);
const environmentPins = { sourceSha: plan.source.commit, sourceTree: git('rev-parse', 'HEAD^{tree}').toString().trim(), runtime: plan.runtime, npmVersion: setup.npmVersion, npmArchiveSha256: setup.npmArchiveSha256, projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')), dependencies };
if (!firstCapture) {
  const locked = read(lockPath);
  if (!compareRawJson(JSON.stringify(locked.environmentPins), JSON.stringify(environmentPins))) throw new Error('Installed source/dependency/runtime environment differs from captured lock');
  for (const file of locked.harnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Harness bytes changed before execution: ${file.path}`);
  for (const file of locked.loadedModules) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Loaded module bytes changed before execution: ${file.path}`);
  const manifest = read(manifestPath).fixtures[0];
  if (manifest.fixtureId !== fixture.fixtureId || manifest.sourceSha !== fixture.sourceSha || fileHash(fixturePath) !== manifest.input.sha256 || fileHash(expectedPath) !== manifest.expected.sha256) throw new Error('Agent fixture identity/checksum mismatch before execution');
} else if ([lockPath, expectedPath, manifestPath].some(existsSync)) throw new Error('Initial capture refuses to overwrite a golden, lock or manifest');
mkdirSync(scratchRoot, { recursive: true });
const captures = [];
for (let repeat = 0; repeat < 2; repeat++) {
  const isolated = mkdtempSync(join(scratchRoot, 'capture-'));
  try {
    const home = join(isolated, 'home'); const workspace = join(isolated, 'workspace');
    mkdirSync(home); mkdirSync(workspace);
    const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(toolRoot, 'full-preload.mjs')).href, join(toolRoot, 'full-capture.mjs'), fixturePath], {
      cwd: workspace, env: { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: isolated, TEMP: isolated, TZ: 'UTC', PISHARP_REFERENCE_ORACLE: oracle },
      encoding: 'utf8', timeout: 20000, maxBuffer: 8 * 1024 * 1024, windowsHide: true,
    });
    if (child.status !== 0) throw new Error(`Full agent capture failed: ${child.error?.message ?? child.stderr}`);
    captures.push(readFromString(child.stdout));
  } finally {
    const cleanupTarget = resolve(isolated); const within = relative(resolve(scratchRoot), cleanupTarget);
    if (!within || isAbsolute(within) || within.startsWith('..')) throw new Error('Refusing cleanup outside reference scratch root');
    rmSync(cleanupTarget, { recursive: true, force: true });
  }
}
function readFromString(raw) { return parseJsonSupported(raw); }
if (!compareRawJson(JSON.stringify(captures[0]), JSON.stringify(captures[1]))) throw new Error('Repeated full agent capture differs');
const loadedModules = captures[0].loadedModules;
const sourceHashes = [];
for (const module of loadedModules) {
  if (module.path.startsWith('upstream/')) {
    const path = module.path.slice('upstream/'.length);
    const blob = git('show', `${plan.source.commit}:${path}`);
    sourceHashes.push({ ...module, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length });
  } else if (!plan.dependencies.some(dependency => module.path.startsWith(`node_modules/${dependency.name}/`))) throw new Error(`Unexpected loaded oracle module ${module.path}`);
}
cleanSource();
if (!compareRawJson(JSON.stringify(dependencies), JSON.stringify(plan.dependencies.map(dependencyTree)))) throw new Error('Dependency bytes changed during capture');
const actual = { fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: 'captured-upstream-agent-oracle', normalizerVersion: fixture.normalizerVersion, observations: captures[0].observations };
const actualRaw = `${JSON.stringify(actual, null, 2)}\n`;
const actualPath = join(outputRoot, 'awaited-parallel.actual.json');
writeFileSync(actualPath, actualRaw);
const harnessFiles = ['tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/full-capture.mjs', 'tools/PiReferenceRunner/full-run.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs'];
if (firstCapture) {
  writeFileSync(lockPath, `${JSON.stringify({ schemaVersion: 1, environmentPins, loadedModules, sourceHashes, harnessFiles: harnessFiles.map(path => ({ path, sha256: fileHash(join(repo, path)) })) }, null, 2)}\n`);
  writeFileSync(expectedPath, actualRaw);
  writeFileSync(manifestPath, `${JSON.stringify({ schemaVersion: 1, sourceSha: fixture.sourceSha, normalizerVersion: fixture.normalizerVersion, scope: 'One genuine awaited upstream runAgentLoop scenario with fake provider/tools; no production provider, native agent differential or phase closure', fixtures: [{ fixtureId: fixture.fixtureId, requirementIds: fixture.requirementIds, scenario: fixture.scenario, sourceSha: fixture.sourceSha, clock: fixture.clock, seed: fixture.seed, environment: { platform: process.platform, architecture: process.arch, credentials: 'not inherited', network: 'disabled during capture', workspace: 'fresh isolated temporary home/workspace' }, input: { path: relative(repo, fixturePath).replaceAll('\\', '/'), sha256: fileHash(fixturePath) }, expected: { path: relative(repo, expectedPath).replaceAll('\\', '/'), sha256: fileHash(expectedPath) }, provenance: { kind: 'captured-upstream-agent-oracle', inputKind: 'authored-synthetic-input', source: 'Unmodified pinned packages/agent/src/agent-loop.ts runAgentLoop', resolver: 'Unmodified pinned packages/coding-agent/src/experimental/source-resolver.ts', dependencyLock: 'tools/PiReferenceRunner/full-lock.json', captureCommand: 'node tools/PiReferenceRunner/full-run.mjs --capture-new', capturedAt: '2026-09-30', rawProviderWireFrames: false } }] }, null, 2)}\n`);
} else {
  const locked = read(lockPath);
  if (!compareRawJson(JSON.stringify(locked.loadedModules), JSON.stringify(loadedModules)) || !compareRawJson(JSON.stringify(locked.sourceHashes), JSON.stringify(sourceHashes))) throw new Error('Runtime loaded-source/dependency closure differs from locked capture');
  for (const file of locked.harnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Harness bytes changed: ${file.path}`);
  const manifest = read(manifestPath).fixtures[0];
  if (fileHash(fixturePath) !== manifest.input.sha256 || fileHash(expectedPath) !== manifest.expected.sha256) throw new Error('Agent fixture checksum mismatch');
}
const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
const report = { schemaVersion: 1, sourceSha: plan.source.commit, fixtureId: fixture.fixtureId, capturedInitialGolden: firstCapture, repeatRuns: 2, deterministic: true, matched, sourceCleanAfter: true, loadedUpstreamSourceFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length, environmentPins, checks: actual.observations.checks, scope: 'Full awaited upstream loop at one synthetic fake-stream/tool seam; no native differential/provider wire/full parity gate claim' };
writeFileSync(join(outputRoot, 'full-reference-report.json'), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report, null, 2));
if (!matched) process.exitCode = 1;
