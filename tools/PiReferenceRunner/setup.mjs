import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Default is an offline preflight. Package acquisition requires explicit --execute.
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const plan = JSON.parse(readFileSync(join(repo, 'compatibility/reference-oracle-install-plan.json'), 'utf8'));
const oracle = resolve(repo, '..', 'Pi-reference-oracle-v0.99.1');
const source = resolve(repo, '..', 'Pi-upstream-v0.99.1');
const snapshot = join(oracle, 'upstream');
const npmRoot = join(oracle, 'tools', 'npm-11.6.2');
const markerPath = join(oracle, '.pisharp-oracle-setup.json');
const config = join(oracle, 'config');
const git = 'C:\\Program Files\\Git\\cmd\\git.exe';
const tar = join(process.env.SystemRoot ?? 'C:\\WINDOWS', 'system32', 'tar.exe');
const manifestPath = join(repo, plan.projection.manifestPath);
const lockPath = join(repo, plan.projection.lockPath);
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const lock = JSON.parse(readFileSync(lockPath, 'utf8'));
const hash = (path, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(readFileSync(path)).digest(encoding);
const childEnvironment = {
  SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR,
  PATH: dirname(process.execPath), USERPROFILE: join(oracle, 'home'), HOME: join(oracle, 'home'),
  APPDATA: join(oracle, 'home'), LOCALAPPDATA: join(oracle, 'home'),
  TMP: join(oracle, 'tmp'), TEMP: join(oracle, 'tmp'), TZ: 'UTC',
  GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(config, 'gitconfig'),
  NO_UPDATE_NOTIFIER: '1', npm_config_update_notifier: 'false'
};
function run(executable, args, cwd = oracle) {
  const result = spawnSync(executable, args, { cwd, env: childEnvironment, encoding: 'utf8', windowsHide: true,
    timeout: 120000, maxBuffer: 16 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`${executable} exited ${result.status}: ${result.error?.message ?? result.stderr}`);
  return result.stdout.trim();
}
function gitRead(root, ...args) { return run(git, ['-c', `safe.directory=${root}`, '-C', root, ...args], root); }
function validateSource(root) {
  if (gitRead(root, 'rev-parse', 'HEAD') !== plan.source.commit) throw new Error('Upstream commit does not match pin');
  if (gitRead(root, 'status', '--porcelain', '--untracked-files=all')) throw new Error('Upstream source has user or generated changes');
  if (hash(join(root, plan.source.lockPath)) !== plan.source.lockSha256) throw new Error('Upstream lock bytes do not match pin');
}
if (process.version !== plan.runtime.version || hash(process.execPath) !== plan.runtime.sha256)
  throw new Error('Node executable does not match locked runtime');
validateSource(source);
if (JSON.stringify(Object.keys(manifest.dependencies).sort()) !== JSON.stringify(['partial-json', 'typebox']))
  throw new Error('Unexpected dependency projection');
const upstreamLock = JSON.parse(readFileSync(join(source, 'package-lock.json'), 'utf8'));
for (const [name, version] of Object.entries(manifest.dependencies)) {
  const row = lock.packages[`node_modules/${name}`];
  if (version !== row.version || JSON.stringify(row) !== JSON.stringify(upstreamLock.packages[`node_modules/${name}`]))
    throw new Error(`Lock projection differs from upstream: ${name}`);
  if (row.hasInstallScript || Object.keys(row.dependencies ?? {}).length) throw new Error('Unexpected dependency scripts or edges');
}
if (!process.argv.includes('--execute')) {
  console.log(JSON.stringify({ status: 'offline-preflight-passed; this run performed no install', workspace: oracle,
    existingSetupStatus: existsSync(markerPath) ? JSON.parse(readFileSync(markerPath, 'utf8')).status : 'not-installed',
    sourceCommit: plan.source.commit, packageManager: plan.packageManager.version,
    dependencies: manifest.dependencies, scripts: 'disabled', nativeBuilds: 'none',
    requiredApproval: 'Scoped executor network/download approval for explicit --execute',
    manifestSha256: hash(manifestPath), lockProjectionSha256: hash(lockPath) }, null, 2));
  process.exit(0);
}
if (existsSync(oracle) && readdirSync(oracle).length && !existsSync(markerPath))
  throw new Error('Refusing to modify an existing non-owned oracle directory');
if (existsSync(markerPath)) {
  const existing = JSON.parse(readFileSync(markerPath, 'utf8'));
  if (existing.owner !== 'PiSharp-reference-setup-v1' || existing.sourceCommit !== plan.source.commit)
    throw new Error('Oracle ownership marker does not match');
  for (const [name, input] of [['package.json', manifestPath], ['package-lock.json', lockPath]])
    if (existsSync(join(oracle, name)) && hash(join(oracle, name)) !== hash(input))
      throw new Error(`Preserving changed oracle input: ${name}`);
}
if (existsSync(snapshot)) validateSource(snapshot);
mkdirSync(oracle, { recursive: true });
const marker = { owner: 'PiSharp-reference-setup-v1', sourceCommit: plan.source.commit, status: 'setup-started' };
writeFileSync(markerPath, `${JSON.stringify(marker, null, 2)}\n`);
try {
  for (const folder of ['home', 'tmp', 'cache', 'config', 'archives', 'tools/npm-11.6.2'])
    mkdirSync(join(oracle, folder), { recursive: true });
  for (const file of ['user.npmrc', 'global.npmrc', 'gitconfig']) {
    const target = join(config, file);
    if (existsSync(target) && readFileSync(target, 'utf8') !== '') throw new Error(`Preserving changed setup config: ${file}`);
    writeFileSync(target, '');
  }
  for (const [name, input] of [['package.json', manifestPath], ['package-lock.json', lockPath]])
    writeFileSync(join(oracle, name), readFileSync(input));
  if (!existsSync(snapshot)) run(git, ['clone', '--local', '--no-hardlinks', '--quiet', source, snapshot]);
  validateSource(snapshot);
  const archive = join(oracle, 'archives', 'npm-11.6.2.tgz');
  const expected = plan.packageManager.integrity;
  if (!existsSync(archive)) {
    const response = await fetch(plan.packageManager.tarball, { redirect: 'error', signal: AbortSignal.timeout(60000) });
    if (!response.ok) throw new Error(`npm archive HTTP ${response.status}`);
    const bytes = Buffer.from(await response.arrayBuffer());
    if (bytes.length > 20 * 1024 * 1024) throw new Error('npm archive exceeds expected bootstrap budget');
    if (`sha512-${createHash('sha512').update(bytes).digest('base64')}` !== expected)
      throw new Error('npm bootstrap archive integrity mismatch');
    writeFileSync(archive, bytes);
  }
  if (`sha512-${hash(archive, 'sha512', 'base64')}` !== expected) throw new Error('Stored npm archive integrity mismatch');
  const entries = run(tar, ['-tf', archive]).split(/\r?\n/);
  if (entries.some(entry => !entry.startsWith('package/') || entry.split('/').includes('..') || entry.includes('\\')))
    throw new Error('Unexpected archive paths');
  run(tar, ['-xf', archive, '-C', npmRoot]);
  const npmCli = join(npmRoot, 'package', 'bin', 'npm-cli.js');
  if (run(process.execPath, [npmCli, '--version']) !== plan.packageManager.version) throw new Error('npm version differs from pin');
  run(process.execPath, [npmCli, ...plan.setup.restoreArguments, '--prefix', oracle, '--cache', join(oracle, 'cache'),
    '--userconfig', join(config, 'user.npmrc'), '--globalconfig', join(config, 'global.npmrc')]);
  if (hash(join(oracle, 'package-lock.json')) !== hash(lockPath)) throw new Error('npm changed the projection lock');
  const installed = [];
  for (const [name, version] of Object.entries(manifest.dependencies)) {
    const packagePath = join(oracle, 'node_modules', name, 'package.json');
    const actual = JSON.parse(readFileSync(packagePath, 'utf8'));
    if (actual.name !== name || actual.version !== version || Object.keys(actual.dependencies ?? {}).length)
      throw new Error(`Installed dependency differs from reviewed scope: ${name}`);
    if (['preinstall', 'install', 'postinstall', 'prepare'].some(key => actual.scripts?.[key]))
      throw new Error(`Installed package declares unexpected lifecycle scripts: ${name}`);
    installed.push({ name, version, manifestSha256: hash(packagePath) });
  }
  validateSource(snapshot);
  marker.status = 'dependencies-installed; runtime oracle qualification pending';
  marker.npmVersion = plan.packageManager.version;
  marker.npmArchiveSha256 = hash(archive);
  marker.installed = installed;
  marker.upstreamSourceModified = false;
  console.log(JSON.stringify(marker, null, 2));
} catch (error) { marker.status = 'setup-failed'; marker.failure = error.message; throw error; }
finally { writeFileSync(markerPath, `${JSON.stringify(marker, null, 2)}\n`); }
