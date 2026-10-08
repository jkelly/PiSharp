// Preparation is local; only explicit --restore performs official-registry reads.
// Default --check is read-only and never changes either qualified older oracle.
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import { Agent, get } from 'node:https';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..'), taskRoot = dirname(repo);
const planPath = join(repo, 'tools/PiReferenceRunner/anthropic-sdk-install-plan.json');
const planHash = '8e3a8908257832f2b736680f676160f60f52c77c9c10f790160ffbb431684682';
const inspectorPath = join(repo, 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs');
const inspectorHash = '1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513';
const ownerName = 'PiSharp-anthropic-sdk-oracle-v1';
const ownerFile = '.pisharp-anthropic-sdk-owner.json', preparedFile = '.pisharp-anthropic-sdk-prepared.json';
const startedFile = '.pisharp-anthropic-sdk-restore-started.json', restoredFile = '.pisharp-anthropic-sdk-restored.json';
const preparedStatus = 'prepared; Anthropic SDK dependency restore not performed';
const restoredStatus = 'eight exact dependencies installed; whole Anthropic wrapper qualification pending';
const gitExe = 'C:\\Program Files\\Git\\cmd\\git.exe';
const packageNames = ["@anthropic-ai/sdk","@babel/runtime","@stablelib/base64","fast-sha256","json-schema-to-ts","partial-json","standardwebhooks","ts-algebra"];
const reviewedDisabledLifecycleAdmission = {
  name: 'standardwebhooks', version: '1.1.1', archiveFile: 'standardwebhooks-1.1.1.tgz', archiveBytes: 4109,
  archiveSha256: 'd0bc8fec280c9e30ea3cbc876f44fe71d56f443aeca2bd2bdf745718308d4aed',
  manifestSha256: '686397f0f0202e0b693cfd3f6241d7a8da3df326f9642867a484fbbd295690b6',
  scriptDeclarations: { build: 'tsc', prepare: 'npm run build', test: 'tsc && node --test dist/*.test.js', prepublishOnly: 'biome check', check: 'biome check', 'check:fix': 'biome check --write', fmt: 'biome format --write' },
  disabledScripts: { prepare: 'npm run build', build: 'tsc' }, disabledBy: '--ignore-scripts', scriptsExecuted: false,
  packagedFiles: ['README.md', 'dist/index.d.ts', 'dist/index.js', 'dist/index.js.map', 'dist/timing_safe_equal.d.ts', 'dist/timing_safe_equal.js', 'dist/timing_safe_equal.js.map', 'package.json']
};
const reviewedDevelopmentOnlyLicenseAdmission = {
  name: 'standardwebhooks', version: '1.1.1', archiveFile: 'standardwebhooks-1.1.1.tgz',
  archiveSha256: 'd0bc8fec280c9e30ea3cbc876f44fe71d56f443aeca2bd2bdf745718308d4aed',
  manifestSha256: '686397f0f0202e0b693cfd3f6241d7a8da3df326f9642867a484fbbd295690b6',
  scope: 'task-local-development-only', declaredLicense: 'MIT', licenseEvidence: 'declared-MIT-only', licenses: [],
  packagedRootLicenseVerified: false, redistributionLicenseClosure: false, licenseReviewStatus: 'P1-02 HOLD'
};
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
const requireThat = (condition, message) => { if (!condition) throw new Error(message); };
const normalized = path => resolve(path).toLowerCase();

function confined(root, path) {
  const suffix = relative(resolve(root), resolve(path));
  requireThat(suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Path escapes its owned root');
  return resolve(path);
}
export function inspectionReceiptPath(oracle, archiveFile) {
  // The reviewed archive names are single leaves with exact numeric versions.
  requireThat(typeof archiveFile === 'string' && /^[a-z0-9][a-z0-9-]*-[0-9]+\.[0-9]+\.[0-9]+\.tgz$/.exec(archiveFile)?.[0] === archiveFile, 'Invalid inspection archive filename');
  const archives = join(oracle, 'archives');
  return confined(archives, join(archives, archiveFile + '.inspection.json'));
}
function noLinks(path) {
  let current = resolve(path);
  while (true) {
    // lstat also sees dangling links; existsSync would hide those from admission.
    let stat;
    try { stat = lstatSync(current); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (stat) requireThat(!stat.isSymbolicLink(), 'Filesystem link or junction rejected');
    const parent = dirname(current); if (parent === current) break; current = parent;
  }
}
function readRegular(path) { noLinks(path); requireThat(lstatSync(path).isFile(), 'Expected a regular file'); return readFileSync(path); }
function readJson(path) { return JSON.parse(readRegular(path).toString('utf8')); }
function writeNew(root, path, bytes) {
  const target = confined(root, path); noLinks(target); requireThat(!existsSync(target), 'Preserving an existing setup file');
  mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes, { flag: 'wx' });
}
function checkExact(path, bytes) { requireThat(readRegular(path).equals(bytes), 'Prepared input changed; preserving it'); }

// Hash the accepted inert inspector before importing its export. Its setup main
// executes only for a direct CLI invocation; no SDK setup action is called here.
requireThat(hash(readRegular(inspectorPath)) === inspectorHash, 'Accepted archive inspector changed');
export const { inspectPackageArchive } = await import(pathToFileURL(inspectorPath).href);

function environment(root) {
  return {
    SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', PATH: dirname(process.execPath),
    USERPROFILE: join(root, 'home'), HOME: join(root, 'home'), APPDATA: join(root, 'home'), LOCALAPPDATA: join(root, 'home'),
    TMP: join(root, 'tmp'), TEMP: join(root, 'tmp'), TZ: 'UTC',
    GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(root, 'config/gitconfig'), GIT_OPTIONAL_LOCKS: '0',
    NO_UPDATE_NOTIFIER: '1', npm_config_update_notifier: 'false', npm_config_progress: 'false',
    npm_config_fetch_retries: '0', npm_config_fetch_timeout: '60000', npm_config_strict_ssl: 'true',
    npm_config_ignore_scripts: 'true', npm_config_bin_links: 'false', npm_config_audit: 'false', npm_config_fund: 'false'
  };
}
function run(executable, args, cwd, env, timeout = 120000) {
  const result = spawnSync(executable, args, { cwd, env, windowsHide: true, encoding: 'utf8', timeout, maxBuffer: 32 * 1024 * 1024 });
  requireThat(result.status === 0, `Setup subprocess failed (${result.status ?? result.error?.code ?? 'unknown'}); preserving partial state`);
  return result.stdout.trim();
}
function gitRead(root, args, env) {
  return run(gitExe, ['-c', `safe.directory=${root}`, '-c', 'core.autocrlf=false', '-c', `core.hooksPath=${join(env.HOME, '..', 'config/hooks')}`, '-C', root, ...args], root, env);
}
function gitBlob(root, object, env) {
  const result = spawnSync(gitExe, ['-c', `safe.directory=${root}`, '-C', root, 'cat-file', 'blob', object], { cwd: root, env, windowsHide: true, encoding: null, timeout: 30000, maxBuffer: 32 * 1024 * 1024 });
  requireThat(result.status === 0 && Buffer.isBuffer(result.stdout), 'Canonical Git blob read failed'); return result.stdout;
}
const fingerprint = files => ({ files: files.length, sha256: hash(Buffer.from(canonical(files))) });
function inventory(root) {
  noLinks(root); requireThat(lstatSync(root).isDirectory(), 'Expected a directory'); const files = [];
  const visit = directory => {
    for (const name of readdirSync(directory).sort()) {
      const path = confined(root, join(directory, name)), stat = lstatSync(path);
      requireThat(!stat.isSymbolicLink(), 'Directory tree contains a link or junction');
      if (stat.isDirectory()) visit(path);
      else { requireThat(stat.isFile(), 'Directory tree contains a special file'); const bytes = readFileSync(path); files.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); }
    }
  };
  visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function validateSource(root, plan, env) {
  noLinks(root);
  requireThat(gitRead(root, ['rev-parse', 'HEAD'], env) === plan.source.commit, 'Source commit differs');
  requireThat(gitRead(root, ['status', '--porcelain=v1', '--untracked-files=all'], env) === '', 'Source has changed or generated files');
  for (const pin of plan.source.pins) { const bytes = readRegular(confined(root, join(root, pin.path))); requireThat(bytes.length === pin.bytes && hash(bytes) === pin.sha256, 'Pinned source input changed: ' + pin.path); }
  const records = gitRead(root, ['ls-tree', '-r', '-z', 'HEAD'], env).split('\0').filter(Boolean), files = [], checkoutFiles = [], conversions = [];
  for (const record of records) {
    const match = /^([0-7]+) blob ([0-9a-f]{40})\t(.+)$/.exec(record);
    requireThat(match && ['100644', '100755'].includes(match[1]), 'Unexpected source tree object');
    const bytes = readRegular(confined(root, join(root, match[3]))); let canonicalBytes = bytes;
    if (hash(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`), bytes]), 'sha1') !== match[2]) {
      const attr = gitRead(root, ['check-attr', '-z', 'eol', '--', match[3]], env).split('\0');
      requireThat(attr[0] === match[3] && attr[1] === 'eol' && attr[2] === 'crlf', 'Undeclared checkout-to-Git byte change');
      canonicalBytes = gitBlob(root, match[2], env); const utf8 = canonicalBytes.toString('utf8');
      requireThat(Buffer.from(utf8).equals(canonicalBytes) && !canonicalBytes.includes(Buffer.from('\r\n')) && Buffer.from(utf8.replaceAll('\n', '\r\n')).equals(bytes), 'Checkout differs from precise declared CRLF conversion');
      requireThat(hash(Buffer.concat([Buffer.from(`blob ${canonicalBytes.length}\0`), canonicalBytes]), 'sha1') === match[2], 'Canonical source blob identity differs');
      conversions.push({ path: match[3], eol: 'crlf', canonicalSha256: hash(canonicalBytes), checkoutSha256: hash(bytes) });
    }
    files.push({ path: match[3], mode: match[1], gitBlob: match[2], bytes: canonicalBytes.length, sha256: hash(canonicalBytes) });
    checkoutFiles.push({ path: match[3], bytes: bytes.length, sha256: hash(bytes) });
  }
  const canonicalGit = fingerprint(files); requireThat(same(canonicalGit, plan.source.canonicalFingerprint), 'Full 2093-file canonical fingerprint differs');
  const acquiredCheckout = fingerprint(checkoutFiles);
  requireThat(same(acquiredCheckout, plan.source.acquiredFingerprint) && same(conversions, plan.source.declaredCheckoutConversions), 'Acquired checkout or declared two attribute conversions differ');
  return { canonicalGit, acquiredCheckout, declaredCheckoutConversions: conversions };
}
function verifyIntegrity(bytes, pin) { requireThat('sha512-' + hash(bytes, 'sha512', 'base64') === pin.integrity, 'Archive SHA-512 integrity differs'); }
function archiveFiles(archive, prefix = '') { return [...archive.files].map(([path, bytes]) => ({ path: prefix + path, bytes: bytes.length, sha256: hash(bytes) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0); }
function normalizedBins(value, name) {
  const bins = typeof value === 'string' ? { [name]: value } : value ?? {};
  return Object.fromEntries(Object.entries(bins).map(([key, path]) => [key, path.replace(/^\.\//, '')]));
}
function peerProfile(name) {
  return name === '@anthropic-ai/sdk' ? { peerDependencies: { zod: '^3.25.0 || ^4.0.0' }, peerDependenciesMeta: { zod: { optional: true } } } : { peerDependencies: {}, peerDependenciesMeta: {} };
}
export function admitLifecycleDeclarations(manifest, row, evidence) {
  requireThat(!manifest.gypfile && !evidence.memberPaths.some(path => /(?:^|\/)binding\.gyp$|\.node$/i.test(path)), 'Unexpected native build declaration');
  const scripts = manifest.scripts ?? {}, lifecycleKeys = ['preinstall', 'install', 'postinstall', 'prepublish', 'preprepare', 'prepare', 'postprepare'];
  const lifecycleScripts = Object.fromEntries(lifecycleKeys.filter(key => Object.hasOwn(scripts, key)).map(key => [key, scripts[key]]));
  if (Object.keys(lifecycleScripts).length === 0 && row.name !== reviewedDisabledLifecycleAdmission.name) return { lifecycleScripts, lifecycleScriptsExecuted: false, disabledLifecycleAdmission: null };
  const pin = reviewedDisabledLifecycleAdmission;
  requireThat(row.name === pin.name && manifest.name === pin.name && row.lockEntry.version === pin.version && manifest.version === pin.version && row.archiveFile === pin.archiveFile && evidence.archiveBytes === pin.archiveBytes && evidence.archiveSha256 === pin.archiveSha256 && evidence.manifestSha256 === pin.manifestSha256 && same(scripts, pin.scriptDeclarations) && same(lifecycleScripts, { prepare: pin.disabledScripts.prepare }) && same([...evidence.memberPaths].sort(), pin.packagedFiles), 'Unexpected lifecycle declaration; only exact reviewed scripts-disabled standardwebhooks archive is admitted');
  return { lifecycleScripts, lifecycleScriptsExecuted: false, disabledLifecycleAdmission: pin };
}
export function admitPackagedLicense(manifest, row, evidence, licenses) {
  requireThat(Array.isArray(licenses), 'Expected packaged license evidence array');
  if (row.name === reviewedDevelopmentOnlyLicenseAdmission.name) {
    requireThat(licenses.length === 0, 'Known development-only archive requires exactly empty packaged notices');
    admitLifecycleDeclarations(manifest, row, evidence);
    requireThat(manifest.license === 'MIT' && row.lockEntry.license === 'MIT' && same(manifest.dependencies ?? {}, row.lockEntry.dependencies ?? {}), 'Development-only declared license/dependencies differ');
    return { licenseEvidence: 'declared-MIT-only', packagedRootLicenseVerified: false, redistributionLicenseClosure: false, licenseReviewStatus: 'P1-02 HOLD', developmentOnlyLicenseAdmission: reviewedDevelopmentOnlyLicenseAdmission };
  }
  const admittedLicense = text => {
    const flat = text.replace(/\s+/g, ' ');
    if (row.lockEntry.license === 'MIT') return /Permission is hereby granted, free of charge/.test(flat) && /THE SOFTWARE IS PROVIDED "AS IS"/.test(flat);
    return row.lockEntry.license === 'Unlicense' && /This is free and unencumbered software released into the public domain\./.test(flat) && /Anyone is free to copy, modify, publish, use, compile, sell, or distribute/.test(flat) && /THE SOFTWARE IS PROVIDED "AS IS"/.test(flat);
  };
  const rootLicenses = licenses.filter(file => !file.path.includes('/'));
  requireThat(rootLicenses.length > 0 && rootLicenses.some(file => admittedLicense(file.utf8Text)), 'Packaged root license text does not match exact MIT/Unlicense admission');
  return { licenseEvidence: 'packaged-root-license-text', packagedRootLicenseVerified: true, redistributionLicenseClosure: false, licenseReviewStatus: 'P1-02 HOLD', developmentOnlyLicenseAdmission: null };
}
function inspectDependency(bytes, row) {
  verifyIntegrity(bytes, row.lockEntry); const archive = inspectPackageArchive(bytes), manifestBytes = archive.files.get('package.json');
  requireThat(manifestBytes, 'Package manifest missing'); const manifest = JSON.parse(manifestBytes.toString('utf8'));
  requireThat(manifest.name === row.name && manifest.version === row.lockEntry.version && manifest.license === row.lockEntry.license, 'Package identity/license differs');
  requireThat(same(manifest.dependencies ?? {}, row.lockEntry.dependencies ?? {}), 'Package dependency declarations differ from exact lock');
  const peers = peerProfile(row.name);
  requireThat(Object.keys(manifest.optionalDependencies ?? {}).length === 0 && same(manifest.peerDependencies ?? {}, peers.peerDependencies) && same(manifest.peerDependenciesMeta ?? {}, peers.peerDependenciesMeta), 'Unexpected optional/peer declaration; only exact SDK optional zod is admitted');
  const evidence = { archiveBytes: bytes.length, archiveSha256: hash(bytes), manifestSha256: hash(manifestBytes), memberPaths: [...archive.files.keys()] };
  const lifecycle = admitLifecycleDeclarations(manifest, row, evidence);
  requireThat(!manifest.bundledDependencies?.length && !manifest.bundleDependencies?.length, 'Unexpected bundled dependency');
  requireThat(same(normalizedBins(manifest.bin, row.name), normalizedBins(row.lockEntry.bin, row.name)), 'Package bin declarations differ');
  const licenses = [...archive.files].filter(([path]) => /(^|\/)(?:license|licence|notice|copying)(?:\.[^/]*)?$/i.test(path)).map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes), utf8Text: bytes.toString('utf8') }));
  const licenseAdmission = admitPackagedLicense(manifest, row, evidence, licenses);
  return { archive, report: { name: row.name, version: row.lockEntry.version, url: row.lockEntry.resolved, bytes: bytes.length, sha256: hash(bytes), integrity: row.lockEntry.integrity, manifestSha256: hash(manifestBytes), dependencyDeclarations: manifest.dependencies ?? {}, peerDependencies: manifest.peerDependencies ?? {}, peerDependenciesMeta: manifest.peerDependenciesMeta ?? {}, optionalPeersInstalled: [], binDeclarations: normalizedBins(manifest.bin, row.name), ...lifecycle, ...licenseAdmission, scriptDeclarations: manifest.scripts ?? {}, files: fingerprint(archiveFiles(archive)), unpackedBytes: archive.unpackedBytes, memberKinds: { regular: archive.files.size, directories: archive.directories, pax: archive.metadataHeaders }, licenses, installed: false } };
}
function projection(plan) {
  const packages = { '': { name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, dependencies: plan.projection.packageJson.dependencies } };
  for (const row of plan.packages) packages[row.projectedLockPath] = row.lockEntry;
  return { manifest: jsonBytes(plan.projection.packageJson), lock: jsonBytes({ name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, lockfileVersion: 3, requires: true, packages }) };
}
export function parseSetupArguments(args) {
  const usage = 'Usage: node setup-anthropic-sdk-oracle.mjs [--check [--oracle APPROVED_ANTHROPIC_ORACLE]|--prepare|--restore]';
  let mode, oracle;
  for (let index = 0; index < args.length; index++) {
    if (['--check', '--prepare', '--restore'].includes(args[index]) && mode === undefined) mode = args[index];
    else if (args[index] === '--oracle' && oracle === undefined && args[index + 1] && !args[index + 1].startsWith('--')) oracle = args[++index];
    else throw new Error(usage);
  }
  requireThat(oracle === undefined || mode === '--check', 'Explicit oracle permitted only with explicit read-only --check');
  return { mode: mode ?? '--check', oracle };
}
export function selectSetupOracle(settings, plan, repositoryTaskRoot = taskRoot) {
  requireThat(settings.oracle === undefined || settings.mode === '--check', 'Mutation mode cannot override the workspace');
  const oracle = settings.oracle === undefined ? confined(repositoryTaskRoot, join(repositoryTaskRoot, 'Pi-anthropic-sdk-oracle-v0.99.1-reviewed')) : resolve(settings.oracle);
  requireThat(normalized(oracle) === normalized(plan.workspace.proposedRoot), 'Workspace differs from reviewed plan'); return oracle;
}
function context(settings) {
  const bytes = readRegular(planPath); requireThat(hash(bytes) === planHash, 'Reviewed Anthropic setup plan changed'); const plan = JSON.parse(bytes.toString('utf8'));
  requireThat(process.platform === 'win32' && process.version === plan.runtime.version && normalized(process.execPath) === normalized(plan.runtime.absoluteExecutable) && hash(readRegular(process.execPath)) === plan.runtime.sha256, 'Pinned Windows Node executable differs');
  const oracle = selectSetupOracle(settings, plan), oracleTaskRoot = dirname(oracle);
  noLinks(oracleTaskRoot); noLinks(oracle); requireThat(normalized(realpathSync(oracleTaskRoot)) === normalized(oracleTaskRoot), 'Task root resolves through an unexpected path');
  const source = join(plan.workspace.existingQualifiedOracle, 'upstream'), env = environment(oracle), sourceFingerprint = validateSource(source, plan, env);
  requireThat(same(Object.keys(plan.projection.packageJson.dependencies).sort(), packageNames) && same(plan.packages.map(row => row.name).sort(), packageNames), 'Unexpected package profile');
  const upstreamLock = readJson(join(source, plan.source.lockPath));
  requireThat(readJson(join(source, 'packages/ai/package.json')).dependencies['@anthropic-ai/sdk'] === '0.124.0' && readJson(join(source, 'packages/ai/package.json')).dependencies['partial-json'] === '0.1.7', 'AI direct manifest declarations differ');
  for (const pin of plan.source.directDeclarations) requireThat(upstreamLock.packages[pin.packageLockPath]?.dependencies?.[pin.name] === pin.version, 'Direct source prerequisite declaration differs');
  for (const row of plan.packages) {
    requireThat(row.sourceLockPath === 'node_modules/' + row.name && row.projectedLockPath === row.sourceLockPath && same(row.lockEntry, upstreamLock.packages[row.sourceLockPath]) && plan.projection.packageJson.dependencies[row.name] === row.lockEntry.version, 'Projection differs from exact upstream lock');
    const leafName = row.name.split('/').at(-1), expectedFile = row.name.replace(/^@/, '').replaceAll('/', '-') + '-' + row.lockEntry.version + '.tgz';
    const peers = peerProfile(row.name);
    requireThat(row.archiveFile === expectedFile && row.lockEntry.resolved === `https://registry.npmjs.org/${row.name}/-/${leafName}-${row.lockEntry.version}.tgz` && !row.lockEntry.hasInstallScript && !row.lockEntry.dev && !row.lockEntry.optional && Object.keys(row.lockEntry.optionalDependencies ?? {}).length === 0 && same(row.lockEntry.peerDependencies ?? {}, peers.peerDependencies) && same(row.lockEntry.peerDependenciesMeta ?? {}, peers.peerDependenciesMeta), 'Package outside exact mandatory official-registry profile');
    requireThat(Object.keys(row.lockEntry.dependencies ?? {}).every(name => packageNames.includes(name)), 'Unreviewed transitive dependency');
  }
  requireThat(same(plan.knownOptionalPeerAdmission, { package: '@anthropic-ai/sdk', version: '0.124.0', ...peerProfile('@anthropic-ai/sdk'), installed: false, rule: 'Allow exactly these canonical SDK declarations; reject other peer or optional dependency declarations in any selected package. zod is not included in projection or installed.' }), 'Optional peer admission changed');
  requireThat(same(plan.disabledLifecycleAdmission, reviewedDisabledLifecycleAdmission) && plan.setupProposal.restoreArguments.includes('--ignore-scripts'), 'Reviewed scripts-disabled lifecycle admission changed');
  requireThat(same(plan.developmentOnlyLicenseAdmission, reviewedDevelopmentOnlyLicenseAdmission) && plan.setupProposal.redistributionLicenseClosure === false && plan.setupProposal.licenseReviewStatus === 'P1-02 HOLD', 'Reviewed development-only license admission changed');
  requireThat(same(plan.packages.filter(row => row.reuseVerifiedCacheArchive).map(row => row.name), ['partial-json']), 'Reused archive selection differs');
  requireThat(plan.reusedArchiveInspector.path === relative(repo, inspectorPath).split(sep).join('/') && plan.reusedArchiveInspector.sha256 === inspectorHash, 'Archive inspector plan pin differs');
  for (const [path, expected] of [[plan.genuineCaptureBoundary.offlinePreload.existingObserver, plan.genuineCaptureBoundary.offlinePreload.observerSha256], [plan.genuineCaptureBoundary.offlinePreload.existingGuard, plan.genuineCaptureBoundary.offlinePreload.guardSha256]]) requireThat(hash(readRegular(join(repo, path))) === expected, 'Existing offline tooling changed');
  const npmBytes = readRegular(plan.packageManager.existingVerifiedArchive);
  requireThat(npmBytes.length === plan.packageManager.archiveBytes && hash(npmBytes) === plan.packageManager.sha256, 'Existing npm archive differs'); verifyIntegrity(npmBytes, plan.packageManager);
  const npmArchive = inspectPackageArchive(npmBytes), npmManifest = JSON.parse(npmArchive.files.get('package.json').toString('utf8'));
  requireThat(npmManifest.name === 'npm' && npmManifest.version === plan.packageManager.version, 'npm bootstrap identity differs');
  const originalCacheFingerprint = fingerprint(inventory(join(plan.workspace.existingQualifiedOracle, 'cache/_cacache')));
  requireThat(same(originalCacheFingerprint, plan.reusedCacheFingerprint), 'Original accepted cache fingerprint differs');
  const seeds = plan.packages.filter(row => row.reuseVerifiedCacheArchive).map(row => {
    const hex = Buffer.from(row.lockEntry.integrity.slice('sha512-'.length), 'base64').toString('hex');
    const archivePath = join(plan.workspace.existingQualifiedOracle, 'cache/_cacache/content-v2/sha512', hex.slice(0, 2), hex.slice(2, 4), hex.slice(4));
    return { row, archivePath, bytes: readRegular(archivePath) };
  });
  for (const seed of seeds) {
    const inspected = inspectDependency(seed.bytes, seed.row), prior = plan.reusedSeed;
    requireThat(prior.name === seed.row.name && prior.version === seed.row.lockEntry.version && hash(seed.bytes) === prior.archiveSha256 && inspected.report.manifestSha256 === prior.manifestSha256 && same(inspected.report.files, prior.files), 'Accepted seed archive changed');
    const installed = inventory(join(plan.workspace.existingQualifiedOracle, 'node_modules', seed.row.name));
    requireThat(same(installed, archiveFiles(inspected.archive)) && same(fingerprint(installed), prior.files), 'Accepted original installed tree changed');
  }
  return { plan, oracle, source, env, sourceFingerprint, npmBytes, npmArchive, seeds, projection: projection(plan), helperSha256: hash(readRegular(ownPath)) };
}
function recheckOriginalSeeds(ctx) {
  for (const seed of ctx.seeds) {
    const bytes = readRegular(seed.archivePath); requireThat(bytes.equals(seed.bytes), 'Original accepted cache archive changed');
    const inspected = inspectDependency(bytes, seed.row);
    requireThat(hash(bytes) === ctx.plan.reusedSeed.archiveSha256 && same(inspected.report.files, ctx.plan.reusedSeed.files) && same(inventory(join(ctx.plan.workspace.existingQualifiedOracle, 'node_modules', seed.row.name)), archiveFiles(inspected.archive)), 'Original accepted installed tree changed');
  }
}
function owner(ctx) { return { schemaVersion: 1, owner: ownerName, oracle: ctx.oracle, sourceCommit: ctx.plan.source.commit, planSha256: planHash, helperSha256: ctx.helperSha256, archiveInspectorSha256: inspectorHash }; }
function preparedInputs(ctx) {
  return [['package.json', ctx.projection.manifest], ['package-lock.json', ctx.projection.lock], ['config/user.npmrc', Buffer.alloc(0)], ['config/global.npmrc', Buffer.alloc(0)], ['config/gitconfig', Buffer.alloc(0)], ['archives/npm-11.6.2.tgz', ctx.npmBytes], ...ctx.seeds.map(seed => [`archives/${seed.row.archiveFile}`, seed.bytes])];
}
function validatePrepared(ctx) {
  const identity = readJson(join(ctx.oracle, ownerFile)); requireThat(same(identity, owner(ctx)), 'Workspace ownership differs from current plan/helper');
  const record = readJson(join(ctx.oracle, preparedFile)); requireThat(record.status === preparedStatus && same(record.owner, identity), 'Prepared receipt identity differs');
  for (const [path, bytes] of preparedInputs(ctx)) checkExact(confined(ctx.oracle, join(ctx.oracle, path)), bytes);
  requireThat(same(validateSource(join(ctx.oracle, 'upstream'), ctx.plan, ctx.env), ctx.sourceFingerprint) && same(record.sourceFingerprint, ctx.sourceFingerprint), 'Prepared source differs');
  const npmFiles = fingerprint(archiveFiles(ctx.npmArchive, 'package/'));
  requireThat(same(fingerprint(inventory(join(ctx.oracle, 'tools/npm-11.6.2'))), npmFiles) && same(record.npmFiles, npmFiles), 'Prepared npm files differ from inspected bundle');
  return record;
}
function prepare(ctx) {
  if (existsSync(join(ctx.oracle, preparedFile))) { validatePrepared(ctx); return { status: 'existing owned preparation verified; no writes', oracle: ctx.oracle }; }
  if (existsSync(ctx.oracle)) requireThat(lstatSync(ctx.oracle).isDirectory() && readdirSync(ctx.oracle).length === 0, 'Preserving nonempty or incomplete target directory');
  mkdirSync(ctx.oracle, { recursive: true }); writeNew(ctx.oracle, join(ctx.oracle, ownerFile), jsonBytes(owner(ctx)));
  for (const folder of ['home', 'tmp', 'cache', 'config', 'config/hooks', 'config/git-template', 'archives', 'tools', 'captures']) mkdirSync(confined(ctx.oracle, join(ctx.oracle, folder)), { recursive: true });
  for (const [path, bytes] of preparedInputs(ctx)) writeNew(ctx.oracle, join(ctx.oracle, path), bytes);
  const cacheRoot = join(ctx.plan.workspace.existingQualifiedOracle, 'cache/_cacache'), cacheFiles = inventory(cacheRoot);
  for (const file of cacheFiles) {
    const bytes = readRegular(confined(cacheRoot, join(cacheRoot, file.path))); requireThat(hash(bytes) === file.sha256, 'Original cache changed during copy');
    if (file.path.startsWith('content-v2/sha512/')) requireThat(file.path.slice('content-v2/sha512/'.length).replaceAll('/', '') === hash(bytes, 'sha512'), 'Content-addressed cache name differs');
    writeNew(ctx.oracle, join(ctx.oracle, 'cache/_cacache', file.path), bytes);
  }
  requireThat(same(cacheFiles, inventory(cacheRoot)), 'Original cache changed; preserving partial preparation');
  const npmRoot = join(ctx.oracle, 'tools/npm-11.6.2'); mkdirSync(npmRoot, { recursive: true });
  for (const [name, bytes] of ctx.npmArchive.files) writeNew(ctx.oracle, confined(npmRoot, join(npmRoot, 'package', name)), bytes);
  const snapshot = join(ctx.oracle, 'upstream');
  run(gitExe, ['-c', 'core.autocrlf=false', '-c', `core.hooksPath=${join(ctx.oracle, 'config/hooks')}`, 'clone', '--local', '--no-hardlinks', '--quiet', `--template=${join(ctx.oracle, 'config/git-template')}`, ctx.source, snapshot], ctx.oracle, ctx.env);
  requireThat(same(validateSource(snapshot, ctx.plan, ctx.env), ctx.sourceFingerprint), 'Fresh clone differs from unchanged source');
  requireThat(run(process.execPath, [join(npmRoot, 'package/bin/npm-cli.js'), '--version'], ctx.oracle, ctx.env) === ctx.plan.packageManager.version, 'Prepared npm CLI version differs');
  requireThat(same(validateSource(ctx.source, ctx.plan, ctx.env), ctx.sourceFingerprint), 'Original source changed during preparation');
  recheckOriginalSeeds(ctx);
  const record = { status: preparedStatus, owner: owner(ctx), sourceFingerprint: ctx.sourceFingerprint, npmFiles: fingerprint(inventory(npmRoot)), copiedCacheFiles: fingerprint(cacheFiles), inputHashes: preparedInputs(ctx).map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes) })), networkUsed: false, dependenciesInstalled: false };
  writeNew(ctx.oracle, join(ctx.oracle, preparedFile), jsonBytes(record)); validatePrepared(ctx);
  return { status: record.status, oracle: ctx.oracle, sourceFingerprint: ctx.sourceFingerprint, npmFiles: record.npmFiles, copiedCacheFiles: record.copiedCacheFiles, dependenciesInstalled: false };
}
function downloadExact(url, plan) {
  requireThat(plan.setupProposal.newRequiredArchives.includes(url) && plan.packages.some(row => !row.reuseVerifiedCacheArchive && row.lockEntry.resolved === url), 'Download URL outside exact seven-package scope');
  return new Promise((resolveDownload, reject) => {
    const agent = new Agent({ keepAlive: false, proxyEnv: {}, rejectUnauthorized: true });
    const request = get(url, { agent, headers: { 'User-Agent': 'PiSharp-anthropic-sdk-oracle-setup', Accept: 'application/octet-stream', 'Accept-Encoding': 'identity' } }, response => {
      if (response.statusCode !== 200) { response.resume(); reject(new Error('Official archive response not HTTP 200; redirects refused')); return; }
      const chunks = []; let length = 0;
      response.on('data', chunk => { length += chunk.length; if (length > 32 * 1024 * 1024) request.destroy(new Error('Archive exceeds download limit')); else chunks.push(chunk); });
      response.on('error', reject); response.on('end', () => resolveDownload(Buffer.concat(chunks)));
    });
    request.on('close', () => agent.destroy()); request.on('error', reject); request.setTimeout(30000, () => request.destroy(new Error('Archive read timeout')));
  });
}
function readReports(ctx) { return ctx.plan.packages.map(row => ({ row, ...inspectDependency(readRegular(join(ctx.oracle, 'archives', row.archiveFile)), row) })); }
function validateInstalled(ctx, reports) {
  const root = join(ctx.oracle, 'node_modules'); noLinks(root);
  const expected = new Map();
  for (const name of packageNames) {
    const [top, child] = name.split('/');
    if (!expected.has(top)) expected.set(top, []);
    if (child) expected.get(top).push(child);
  }
  requireThat(same(readdirSync(root).filter(name => name !== '.package-lock.json').sort(), [...expected.keys()].sort()), 'Installed closure differs from exact eight package roots');
  for (const [top, children] of expected) {
    const directory = join(root, top); noLinks(directory); requireThat(lstatSync(directory).isDirectory(), 'Expected installed package/scope directory');
    if (children.length) requireThat(same(readdirSync(directory).sort(), children.sort()), 'Installed scope contains an unexpected package');
  }
  const installed = reports.map(item => {
    const files = inventory(join(root, item.row.name)); requireThat(same(files, archiveFiles(item.archive)), 'Installed bytes differ from inspected archive: ' + item.row.name);
    requireThat(!files.some(file => /\.node$/i.test(file.path)), 'Installed native addon rejected');
    return { name: item.row.name, version: item.row.lockEntry.version, files: fingerprint(files), archiveSha256: item.report.sha256, manifestSha256: item.report.manifestSha256, dependencyDeclarations: item.report.dependencyDeclarations, peerDependencies: item.report.peerDependencies, peerDependenciesMeta: item.report.peerDependenciesMeta, binDeclarations: item.report.binDeclarations, licenses: item.report.licenses, licenseEvidence: item.report.licenseEvidence, packagedRootLicenseVerified: item.report.packagedRootLicenseVerified, redistributionLicenseClosure: false, licenseReviewStatus: item.report.licenseReviewStatus, developmentOnlyLicenseAdmission: item.report.developmentOnlyLicenseAdmission };
  });
  requireThat(existsSync(join(root, '.package-lock.json')), 'Hidden npm lock missing');
  {
    const hidden = readJson(join(root, '.package-lock.json')); requireThat(same(Object.keys(hidden.packages).sort(), ctx.plan.packages.map(row => row.projectedLockPath).sort()), 'Hidden npm lock has unreviewed packages');
    for (const row of ctx.plan.packages) requireThat(hidden.packages[row.projectedLockPath]?.version === row.lockEntry.version && hidden.packages[row.projectedLockPath]?.integrity === row.lockEntry.integrity, 'Hidden lock version/integrity differs');
  }
  return installed;
}
function verifyRestored(ctx) {
  recheckOriginalSeeds(ctx); validatePrepared(ctx); const record = readJson(join(ctx.oracle, restoredFile));
  requireThat(record.status === restoredStatus && same(record.owner, owner(ctx)) && same(record.sourceFingerprint, ctx.sourceFingerprint), 'Restored receipt differs');
  requireThat(record.redistributionLicenseClosure === false && record.licenseReviewStatus === 'P1-02 HOLD' && same(record.developmentOnlyLicenseAdmission, ctx.plan.developmentOnlyLicenseAdmission), 'Development-only restore license receipt differs');
  const reports = readReports(ctx);
  for (const item of reports) checkExact(inspectionReceiptPath(ctx.oracle, item.row.archiveFile), jsonBytes(item.report));
  const installed = validateInstalled(ctx, reports); requireThat(same(installed, record.installed), 'Installed evidence changed');
  const prepared = readJson(join(ctx.oracle, preparedFile)); requireThat(same(fingerprint(inventory(join(ctx.plan.workspace.existingQualifiedOracle, 'cache/_cacache'))), prepared.copiedCacheFiles), 'Original accepted cache changed');
  return { status: 'existing exact restore verified; no installation or network', oracle: ctx.oracle, installed: installed.map(({ name, version, files }) => ({ name, version, files })), sourceFingerprint: ctx.sourceFingerprint, developmentOnlyLicenseAdmission: ctx.plan.developmentOnlyLicenseAdmission, redistributionLicenseClosure: false, licenseReviewStatus: 'P1-02 HOLD', wholeAnthropicWrapperQualified: false };
}
async function restore(ctx) {
  validatePrepared(ctx); if (existsSync(join(ctx.oracle, restoredFile))) return verifyRestored(ctx);
  requireThat(!existsSync(join(ctx.oracle, 'node_modules')) && !existsSync(join(ctx.oracle, startedFile)), 'Preserving incomplete or uncertain previous restore');
  const prepared = readJson(join(ctx.oracle, preparedFile)), cacheRoot = join(ctx.oracle, 'cache/_cacache'), originalCache = join(ctx.plan.workspace.existingQualifiedOracle, 'cache/_cacache');
  requireThat(same(fingerprint(inventory(cacheRoot)), prepared.copiedCacheFiles) && same(fingerprint(inventory(originalCache)), prepared.copiedCacheFiles), 'Prepared/original cache changed before restore');
  writeNew(ctx.oracle, join(ctx.oracle, startedFile), jsonBytes({ owner: owner(ctx), action: 'Explicit --restore: seven anonymous exact official archive reads and eight-package npm ci; scripts/binlinks/optional peers disabled', credentialsUsed: false, providerCalls: false }));
  for (const row of ctx.plan.packages) {
    const archivePath = join(ctx.oracle, 'archives', row.archiveFile);
    const bytes = existsSync(archivePath) ? readRegular(archivePath) : await downloadExact(row.lockEntry.resolved, ctx.plan);
    const item = inspectDependency(bytes, row);
    if (!existsSync(archivePath)) writeNew(ctx.oracle, archivePath, bytes);
    writeNew(ctx.oracle, inspectionReceiptPath(ctx.oracle, row.archiveFile), jsonBytes(item.report));
  }
  const reports = readReports(ctx);
  run(process.execPath, [join(ctx.oracle, 'tools/npm-11.6.2/package/bin/npm-cli.js'), ...ctx.plan.setupProposal.restoreArguments, '--prefix', ctx.oracle, '--cache', join(ctx.oracle, 'cache'), '--userconfig', join(ctx.oracle, 'config/user.npmrc'), '--globalconfig', join(ctx.oracle, 'config/global.npmrc')], ctx.oracle, ctx.env, 240000);
  validatePrepared(ctx); const installed = validateInstalled(ctx, reports);
  requireThat(same(validateSource(ctx.source, ctx.plan, ctx.env), ctx.sourceFingerprint), 'Original source changed during restore');
  requireThat(same(fingerprint(inventory(originalCache)), prepared.copiedCacheFiles), 'Original accepted cache changed during restore');
  const record = { status: restoredStatus, owner: owner(ctx), installed, sourceFingerprint: ctx.sourceFingerprint, networkScope: 'Exact eight official registry lock URLs only; seven newly required archive URLs; no provider endpoints', lifecycleScripts: false, lifecycleScriptsExecuted: false, disabledLifecycleAdmission: ctx.plan.disabledLifecycleAdmission, developmentOnlyLicenseAdmission: ctx.plan.developmentOnlyLicenseAdmission, redistributionLicenseClosure: false, licenseReviewStatus: 'P1-02 HOLD', optionalPeers: [], binLinks: false, nativeAddons: false, apiCredentials: false, providerCalls: false, sourceTransformOrModuleReplacement: false, loadedModuleClosureQualified: false };
  writeNew(ctx.oracle, join(ctx.oracle, restoredFile), jsonBytes(record)); return verifyRestored(ctx);
}
export async function main(args = process.argv.slice(2)) {
  const settings = parseSetupArguments(args), ctx = context(settings); let result;
  if (settings.mode === '--prepare') result = prepare(ctx);
  else if (settings.mode === '--restore') result = await restore(ctx);
  else {
    if (existsSync(join(ctx.oracle, restoredFile))) result = verifyRestored(ctx);
    else if (existsSync(join(ctx.oracle, preparedFile))) { validatePrepared(ctx); result = { status: 'owned prepared layout verified read-only; restore still pending', oracle: ctx.oracle }; }
    else { requireThat(!existsSync(ctx.oracle) || (lstatSync(ctx.oracle).isDirectory() && readdirSync(ctx.oracle).length === 0), 'Preserving nonempty or incomplete target'); result = { status: 'read-only preflight passed; no preparation, download or installation executed', oracle: ctx.oracle, sourceFingerprint: ctx.sourceFingerprint, packageProfile: ctx.plan.projection.packageJson.dependencies, originalCacheFingerprint: ctx.plan.reusedCacheFingerprint, npmBundleFiles: ctx.npmArchive.files.size, existingWorkspace: existsSync(ctx.oracle) }; }
    result.mode = '--check'; result.defaultMutations = false;
  }
  console.log(JSON.stringify(result, null, 2)); return result;
}
if (process.argv[1] && normalized(process.argv[1]) === normalized(ownPath)) await main();
