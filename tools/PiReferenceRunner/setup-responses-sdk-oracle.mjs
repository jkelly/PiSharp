// Local preparation and registry restore are separate explicit actions.
// Default --check is read-only. Never modifies the existing qualified oracle.
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import { Agent, get } from 'node:https';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

const ownPath = fileURLToPath(import.meta.url);
const repo = resolve(dirname(ownPath), '../..');
const taskRoot = resolve(repo, '..');
const planPath = join(repo, 'tools/PiReferenceRunner/responses-sdk-install-plan.json');
const planHash = '0d19249ca9e105602abf304de9c3710e67157bab7209ef67645fd70f77307f22';
// Existing ownership/prepared/restore receipts belong to these original setup bytes.
// A read-only validator correction must not rewrite their historical identity.
const historicalSetupHelperSha256 = '7c4cfc8715344710100d99902defeaef5280169f098cc53cab29f96a90120029';
const ownerName = 'PiSharp-responses-sdk-oracle-v1';
const gitExe = 'C:\\Program Files\\Git\\cmd\\git.exe';
const packageNames = ['openai', 'partial-json', 'typebox'];
const digest = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
const requireThat = (condition, message) => { if (!condition) throw new Error(message); };
const normalizedPath = value => resolve(value).toLowerCase();

function confined(root, target) {
  const suffix = relative(resolve(root), resolve(target));
  requireThat(suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Target path escapes its owned root');
  return resolve(target);
}
function noLinkPath(target) {
  let current = resolve(target);
  while (true) {
    if (existsSync(current)) requireThat(!lstatSync(current).isSymbolicLink(), 'Filesystem link or junction rejected');
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
}
function safeRegularFile(path) {
  noLinkPath(path);
  requireThat(lstatSync(path).isFile(), 'Expected a regular file');
  return readFileSync(path);
}
function writeNew(path, bytes) {
  noLinkPath(path);
  requireThat(!existsSync(path), 'Preserving an existing setup file');
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, bytes, { flag: 'wx' });
}
function checkExact(path, bytes) {
  requireThat(safeRegularFile(path).equals(bytes), 'Prepared input changed; preserving it');
}

// Inert package parsing: reject every link/device/sparse/extension member before
// extraction. npm's already acquired bundle has only regular file members.
export function inspectPackageArchive(compressed, { maxOutputBytes = 64 * 1024 * 1024 } = {}) {
  const bytes = gunzipSync(compressed, { maxOutputLength: maxOutputBytes });
  const files = new Map(), seen = new Set(), seenCase = new Set(), fileNamesCase = new Set();
  let offset = 0, localPax = null, globalPax = {}, directories = 0, metadataHeaders = 0;
  const text = (header, start, length) => header.subarray(start, start + length).toString('utf8').replace(/\0.*$/s, '');
  const octal = (header, start, length) => {
    const value = text(header, start, length).trim();
    requireThat(/^[0-7]*$/.test(value), 'Unsupported tar number encoding');
    const number = value ? Number.parseInt(value, 8) : 0;
    requireThat(Number.isSafeInteger(number), 'Unsafe tar number');
    return number;
  };
  const safeName = (name, directory) => {
    requireThat(!name.includes('\\') && !name.includes('\0') && !name.startsWith('/') && !/^[A-Za-z]:/.test(name), 'Unsafe archive path');
    const trimmed = directory ? name.replace(/\/$/, '') : name;
    const components = trimmed.split('/');
    requireThat(components.every(part => part && part !== '.' && part !== '..' && !/[<>:"|?*\x00-\x1f]/.test(part) && !/[. ]$/.test(part) && !/^(?:con|prn|aux|nul|com[1-9\u00b9\u00b2\u00b3]|lpt[1-9\u00b9\u00b2\u00b3])(?:\.|$)/i.test(part)), 'Unsafe archive path component');
    requireThat(trimmed === 'package' ? directory : trimmed.startsWith('package/'), 'Archive path outside package root');
    return trimmed === 'package' ? '' : trimmed.slice('package/'.length);
  };
  const pax = data => {
    const result = {}; let index = 0;
    while (index < data.length) {
      const space = data.indexOf(32, index);
      requireThat(space > index, 'Invalid PAX record');
      const lengthText = data.subarray(index, space).toString('ascii');
      requireThat(/^[1-9][0-9]*$/.test(lengthText), 'Invalid PAX length');
      const end = index + Number(lengthText);
      requireThat(Number.isSafeInteger(end) && end > space + 1 && end <= data.length && data[end - 1] === 10, 'Invalid PAX bounds');
      const record = data.subarray(space + 1, end - 1).toString('utf8'), equals = record.indexOf('=');
      requireThat(equals > 0, 'Invalid PAX attribute');
      const key = record.slice(0, equals);
      requireThat(!Object.hasOwn(result, key) && key !== 'size' && key !== 'linkpath' && !key.startsWith('GNU.sparse'), 'Unsupported or duplicate PAX attribute');
      result[key] = record.slice(equals + 1); index = end;
    }
    return result;
  };
  while (offset + 512 <= bytes.length) {
    const header = bytes.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) {
      requireThat(bytes.length - offset >= 1024 && bytes.subarray(offset).every(byte => byte === 0), 'Invalid tar end marker');
      requireThat(localPax === null, 'Dangling PAX attributes');
      requireThat(files.size > 0, 'Empty package archive');
      const names = [...seen].sort();
      for (const name of names) {
        const parts = name.split('/');
        for (let i = 1; i < parts.length; i++) requireThat(!fileNamesCase.has(parts.slice(0, i).join('/').toLowerCase()), 'Archive file conflicts with a parent directory');
      }
      return { files, directories, metadataHeaders, unpackedBytes: [...files.values()].reduce((sum, data) => sum + data.length, 0) };
    }
    const checksum = [...header].reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 32 : byte), 0);
    requireThat(checksum === octal(header, 148, 8), 'Tar checksum mismatch');
    const size = octal(header, 124, 12), start = offset + 512, end = start + size;
    requireThat(end <= bytes.length && start + Math.ceil(size / 512) * 512 <= bytes.length, 'Truncated tar member');
    const type = text(header, 156, 1) || '0', data = bytes.subarray(start, end);
    offset = start + Math.ceil(size / 512) * 512;
    if (type === 'x' || type === 'g') {
      const attributes = pax(data);
      if (type === 'g') { requireThat(!Object.hasOwn(attributes, 'path'), 'Global PAX path override rejected'); globalPax = { ...globalPax, ...attributes }; }
      else { requireThat(localPax === null, 'Consecutive local PAX headers rejected'); localPax = attributes; }
      metadataHeaders++; continue;
    }
    requireThat(type === '0' || type === '5', 'Archive links and special members rejected');
    const attributes = { ...globalPax, ...(localPax ?? {}) }; localPax = null;
    const name = safeName(attributes.path ?? [text(header, 345, 155), text(header, 0, 100)].filter(Boolean).join('/'), type === '5');
    const caseKey = name.toLowerCase();
    requireThat(!seenCase.has(caseKey) && seen.size < 10000, 'Duplicate/case-colliding archive member or member limit exceeded');
    seen.add(name); seenCase.add(caseKey);
    if (type === '5') { requireThat(size === 0, 'Archive directory contains data'); directories++; }
    else { requireThat(name !== '' && !/\.node$/i.test(name), 'Native addon or invalid regular member rejected'); files.set(name, data); fileNamesCase.add(caseKey); }
  }
  throw new Error('Tar end marker missing');
}

function envFor(root) {
  return {
    SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', PATH: dirname(process.execPath),
    USERPROFILE: join(root, 'home'), HOME: join(root, 'home'), APPDATA: join(root, 'home'), LOCALAPPDATA: join(root, 'home'),
    TMP: join(root, 'tmp'), TEMP: join(root, 'tmp'), TZ: 'UTC',
    GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(root, 'config', 'gitconfig'), GIT_OPTIONAL_LOCKS: '0',
    NO_UPDATE_NOTIFIER: '1', npm_config_update_notifier: 'false', npm_config_progress: 'false',
    npm_config_fetch_retries: '0', npm_config_fetch_timeout: '60000', npm_config_strict_ssl: 'true',
    npm_config_ignore_scripts: 'true', npm_config_audit: 'false', npm_config_fund: 'false'
  };
}
function run(executable, args, cwd, environment, { timeout = 120000 } = {}) {
  const result = spawnSync(executable, args, { cwd, env: environment, encoding: 'utf8', windowsHide: true, timeout, maxBuffer: 32 * 1024 * 1024 });
  requireThat(result.status === 0, `${dirname(executable) === dirname(process.execPath) ? 'Node/npm' : 'Git'} command failed (${result.status ?? result.error?.code ?? 'unknown'}); preserving partial setup`);
  return result.stdout.trim();
}
function gitRead(root, args, environment) {
  return run(gitExe, ['-c', `safe.directory=${root}`, '-c', 'core.autocrlf=false', '-c', `core.hooksPath=${join(environment.HOME, '..', 'config', 'hooks')}`, '-C', root, ...args], root, environment);
}
function gitBlob(root, object, environment) {
  const result = spawnSync(gitExe, ['-c', `safe.directory=${root}`, '-C', root, 'cat-file', 'blob', object], { cwd: root, env: environment, encoding: null, windowsHide: true, timeout: 30000, maxBuffer: 32 * 1024 * 1024 });
  requireThat(result.status === 0 && Buffer.isBuffer(result.stdout), 'Canonical Git blob read failed');
  return result.stdout;
}
function fingerprint(files) { return { files: files.length, sha256: digest(Buffer.from(canonical(files))) }; }
function archiveFiles(archive, prefix = '') {
  return [...archive.files].map(([path, bytes]) => ({ path: prefix + path, bytes: bytes.length, sha256: digest(bytes) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function inventory(root) {
  noLinkPath(root); requireThat(lstatSync(root).isDirectory(), 'Expected an owned directory');
  const files = [];
  const visit = directory => {
    for (const name of readdirSync(directory).sort()) {
      const path = confined(root, join(directory, name));
      const stat = lstatSync(path); requireThat(!stat.isSymbolicLink(), 'Directory tree contains a link or junction');
      if (stat.isDirectory()) visit(path);
      else { requireThat(stat.isFile(), 'Directory tree contains a special file'); const bytes = readFileSync(path); files.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length, sha256: digest(bytes) }); }
    }
  };
  visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function validateSource(root, plan, environment) {
  noLinkPath(root);
  requireThat(gitRead(root, ['rev-parse', 'HEAD'], environment) === plan.source.commit, 'Source commit differs from pin');
  requireThat(gitRead(root, ['status', '--porcelain=v1', '--untracked-files=all'], environment) === '', 'Source has changes or generated files');
  for (const [path, expected] of [[plan.source.lockPath, plan.source.lockSha256], [plan.source.aiManifestPath, plan.source.aiManifestSha256], [plan.source.wrapperPath, plan.source.wrapperSha256], [plan.source.resolverPath, plan.source.resolverSha256]]) requireThat(digest(safeRegularFile(confined(root, join(root, path)))) === expected, 'Pinned source input bytes differ');
  const entries = gitRead(root, ['ls-tree', '-r', '-z', 'HEAD'], environment).split('\0').filter(Boolean), files = [], checkoutFiles = [], checkoutConversions = [];
  for (const entry of entries) {
    const match = /^([0-7]+) blob ([0-9a-f]{40})\t(.+)$/.exec(entry);
    requireThat(match && ['100644', '100755'].includes(match[1]), 'Unexpected source tree object');
    const bytes = safeRegularFile(confined(root, join(root, match[3])));
    let canonicalBytes = bytes;
    if (digest(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`), bytes]), 'sha1') !== match[2]) {
      // The pinned upstream attributes deliberately check out two Windows scripts
      // as CRLF. Verify the precise declared conversion and retain both hashes.
      const attribute = gitRead(root, ['check-attr', '-z', 'eol', '--', match[3]], environment).split('\0');
      requireThat(attribute[0] === match[3] && attribute[1] === 'eol' && attribute[2] === 'crlf', 'Unexpected checkout-to-Git byte difference');
      canonicalBytes = gitBlob(root, match[2], environment);
      const utf8 = canonicalBytes.toString('utf8');
      requireThat(Buffer.from(utf8).equals(canonicalBytes) && !canonicalBytes.includes(Buffer.from('\r\n')) && Buffer.from(utf8.replaceAll('\n', '\r\n')).equals(bytes), 'Checkout differs from exact declared CRLF conversion');
      requireThat(digest(Buffer.concat([Buffer.from(`blob ${canonicalBytes.length}\0`), canonicalBytes]), 'sha1') === match[2], 'Canonical source blob identity differs');
      checkoutConversions.push({ path: match[3], eol: 'crlf', canonicalSha256: digest(canonicalBytes), checkoutSha256: digest(bytes) });
    }
    files.push({ path: match[3], mode: match[1], gitBlob: match[2], bytes: canonicalBytes.length, sha256: digest(canonicalBytes) });
    checkoutFiles.push({ path: match[3], bytes: bytes.length, sha256: digest(bytes) });
  }
  return { canonicalGit: fingerprint(files), acquiredCheckout: fingerprint(checkoutFiles), declaredCheckoutConversions: checkoutConversions };
}
function verifyIntegrity(bytes, row) {
  requireThat('sha512-' + digest(bytes, 'sha512', 'base64') === row.integrity, 'Package archive SHA-512 integrity differs');
}
function packageArchiveReport(bytes, row, plan) {
  verifyIntegrity(bytes, row.lockEntry);
  const archive = inspectPackageArchive(bytes);
  const manifestBytes = archive.files.get('package.json');
  requireThat(manifestBytes, 'Package manifest absent');
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  requireThat(manifest.name === row.name && manifest.version === row.lockEntry.version && manifest.license === row.lockEntry.license, 'Package identity or declared license differs');
  requireThat(Object.keys(manifest.dependencies ?? {}).length === 0 && Object.keys(manifest.optionalDependencies ?? {}).length === 0, 'Unexpected package dependency closure');
  requireThat(!manifest.gypfile && !['preinstall', 'install', 'postinstall', 'prepare'].some(key => manifest.scripts?.[key]), 'Unexpected install lifecycle or native build declaration');
  requireThat(!manifest.bundledDependencies?.length && !manifest.bundleDependencies?.length, 'Unexpected bundled dependency');
  if (row.name === 'openai') {
    requireThat(same(manifest.peerDependencies ?? {}, row.lockEntry.peerDependencies) && same(manifest.peerDependenciesMeta ?? {}, row.lockEntry.peerDependenciesMeta), 'SDK optional peer declarations differ');
    requireThat(archive.files.size === row.registryMetadata.reportedFileCount && archive.unpackedBytes === row.registryMetadata.reportedUnpackedBytes, 'SDK packaged counts differ from recorded metadata');
    requireThat(digest(bytes, 'sha1') === row.registryMetadata.shasum, 'SDK archive SHA-1 differs');
  } else requireThat(Object.keys(manifest.peerDependencies ?? {}).length === 0, 'Unexpected non-SDK peer declaration');
  const licenses = [...archive.files].filter(([name]) => /(^|\/)(?:license|licence|notice|copying)(?:\.[^/]*)?$/i.test(name)).map(([path, data]) => ({ path, bytes: data.length, sha256: digest(data), utf8Text: data.toString('utf8') }));
  if (row.name === 'openai') requireThat(licenses.some(file => /^LICENSE(?:\.[^/]*)?$/i.test(file.path) && /Apache License/.test(file.utf8Text) && /Version 2\.0/.test(file.utf8Text)), 'SDK packaged Apache-2.0 license text not found');
  const files = [...archive.files].map(([path, data]) => ({ path, bytes: data.length, sha256: digest(data) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { archive, report: { name: row.name, version: manifest.version, url: row.lockEntry.resolved, bytes: bytes.length, sha256: digest(bytes), integrity: row.lockEntry.integrity, manifestSha256: digest(manifestBytes), files: files.length, unpackedBytes: archive.unpackedBytes, fileFingerprint: fingerprint(files), memberKinds: { regular: archive.files.size, directory: archive.directories, pax: archive.metadataHeaders }, licenses, installed: false, sourceModified: false } };
}
function cacheArchive(existing, row) {
  const hex = Buffer.from(row.lockEntry.integrity.slice('sha512-'.length), 'base64').toString('hex');
  return safeRegularFile(join(existing, 'cache', '_cacache', 'content-v2', 'sha512', hex.slice(0, 2), hex.slice(2, 4), hex.slice(4)));
}
function projection(plan) {
  const packages = { '': { name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, dependencies: plan.projection.packageJson.dependencies } };
  for (const row of plan.packages) packages[row.projectedLockPath] = row.lockEntry;
  return { manifest: jsonBytes(plan.projection.packageJson), lock: jsonBytes({ name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, lockfileVersion: 3, requires: true, packages }) };
}
export function parseSetupArguments(args) {
  const usage = 'Usage: node setup-responses-sdk-oracle.mjs [--check [--oracle APPROVED_SDK_ORACLE]|--prepare|--restore]';
  let mode, oracle;
  for (let index = 0; index < args.length; index++) {
    if (['--check', '--prepare', '--restore'].includes(args[index]) && mode === undefined) mode = args[index];
    else if (args[index] === '--oracle' && oracle === undefined && typeof args[index + 1] === 'string' && args[index + 1] && !args[index + 1].startsWith('--')) oracle = args[++index];
    else throw new Error(usage);
  }
  requireThat(oracle === undefined || mode === '--check', 'An explicit oracle is permitted only with --check');
  return { mode: mode ?? '--check', oracle };
}
export function selectSetupOracle(settings, plan, repositoryTaskRoot = taskRoot) {
  requireThat(settings.oracle === undefined || settings.mode === '--check', 'An explicit oracle is permitted only with --check');
  const oracle = settings.oracle === undefined ? confined(repositoryTaskRoot, join(repositoryTaskRoot, 'Pi-responses-sdk-oracle-v0.99.1')) : resolve(settings.oracle);
  requireThat(normalizedPath(oracle) === normalizedPath(plan.workspace.proposedRoot), 'Workspace differs from reviewed path');
  return oracle;
}
function context(settings) {
  const bytes = safeRegularFile(planPath); requireThat(digest(bytes) === planHash, 'Reviewed SDK setup plan changed');
  const plan = JSON.parse(bytes.toString('utf8'));
  requireThat(process.platform === 'win32' && process.version === plan.runtime.version && normalizedPath(process.execPath) === normalizedPath(plan.runtime.absoluteExecutable) && digest(safeRegularFile(process.execPath)) === plan.runtime.sha256, 'Pinned Windows Node executable differs');
  const oracle = selectSetupOracle(settings, plan);
  const oracleTaskRoot = settings.oracle === undefined ? taskRoot : dirname(oracle);
  noLinkPath(oracleTaskRoot); noLinkPath(oracle);
  requireThat(normalizedPath(realpathSync(oracleTaskRoot)) === normalizedPath(oracleTaskRoot), 'Task root resolves through an unexpected path');
  const source = join(plan.workspace.existingQualifiedOracle, 'upstream'), environment = envFor(oracle);
  requireThat(same(Object.keys(plan.projection.packageJson.dependencies).sort(), packageNames) && same(plan.packages.map(row => row.name).sort(), packageNames), 'Unexpected package profile');
  const sourceFingerprint = validateSource(source, plan, environment);
  const upstreamLock = JSON.parse(safeRegularFile(join(source, plan.source.lockPath)).toString('utf8'));
  for (const row of plan.packages) {
    requireThat(same(row.lockEntry, upstreamLock.packages[row.sourceLockPath]) && plan.projection.packageJson.dependencies[row.name] === row.lockEntry.version, 'Package projection differs from original lock entry');
    requireThat(row.lockEntry.resolved === `https://registry.npmjs.org/${row.name}/-/${row.name}-${row.lockEntry.version}.tgz` && !row.lockEntry.hasInstallScript && Object.keys(row.lockEntry.dependencies ?? {}).length === 0, 'Package lies outside the scoped registry profile');
  }
  for (const [path, expected] of [[plan.genuineCaptureBoundary.offlinePreload.existingObserver, plan.genuineCaptureBoundary.offlinePreload.observerSha256], [plan.genuineCaptureBoundary.offlinePreload.existingGuard, plan.genuineCaptureBoundary.offlinePreload.guardSha256]]) requireThat(digest(safeRegularFile(join(repo, path))) === expected, 'Existing observer or offline guard changed');
  const npmBytes = safeRegularFile(plan.packageManager.existingVerifiedArchive);
  requireThat(npmBytes.length === plan.packageManager.archiveBytes && digest(npmBytes) === plan.packageManager.sha256, 'Existing npm archive differs');
  verifyIntegrity(npmBytes, plan.packageManager);
  const npmArchive = inspectPackageArchive(npmBytes);
  const npmManifest = JSON.parse(npmArchive.files.get('package.json').toString('utf8'));
  requireThat(npmManifest.name === 'npm' && npmManifest.version === plan.packageManager.version, 'npm bootstrap identity differs');
  const seeds = plan.packages.filter(row => row.name !== 'openai').map(row => ({ row, bytes: cacheArchive(plan.workspace.existingQualifiedOracle, row) }));
  for (const seed of seeds) packageArchiveReport(seed.bytes, seed.row, plan);
  const currentHelperSha256 = digest(safeRegularFile(ownPath));
  return { plan, oracle, source, environment, sourceFingerprint, npmBytes, npmArchive, seeds, projection: projection(plan), helperSha256: settings.mode === '--check' ? historicalSetupHelperSha256 : currentHelperSha256 };
}
function preparedInputs(ctx) {
  return [
    ['package.json', ctx.projection.manifest], ['package-lock.json', ctx.projection.lock],
    ['config/user.npmrc', Buffer.alloc(0)], ['config/global.npmrc', Buffer.alloc(0)], ['config/gitconfig', Buffer.alloc(0)],
    ['archives/npm-11.6.2.tgz', ctx.npmBytes], ...ctx.seeds.map(seed => [`archives/${seed.row.name}-${seed.row.lockEntry.version}.tgz`, seed.bytes])
  ];
}
function expectedOwner(ctx) { return { schemaVersion: 1, owner: ownerName, oracle: ctx.oracle, sourceCommit: ctx.plan.source.commit, planSha256: planHash, helperSha256: ctx.helperSha256 }; }
function validatePrepared(ctx) {
  const owner = JSON.parse(safeRegularFile(join(ctx.oracle, '.pisharp-responses-sdk-owner.json')).toString('utf8'));
  requireThat(same(owner, expectedOwner(ctx)), 'Setup ownership/input identity differs');
  const record = JSON.parse(safeRegularFile(join(ctx.oracle, '.pisharp-responses-sdk-prepared.json')).toString('utf8'));
  requireThat(record.status === 'prepared; SDK restore not performed' && same(record.owner, owner), 'Prepared record identity differs');
  for (const [path, bytes] of preparedInputs(ctx)) checkExact(confined(ctx.oracle, join(ctx.oracle, path)), bytes);
  requireThat(same(validateSource(join(ctx.oracle, 'upstream'), ctx.plan, ctx.environment), ctx.sourceFingerprint) && same(record.sourceFingerprint, ctx.sourceFingerprint), 'Prepared canonical source differs');
  const npmFiles = fingerprint(archiveFiles(ctx.npmArchive, 'package/'));
  requireThat(same(fingerprint(inventory(join(ctx.oracle, 'tools', 'npm-11.6.2'))), npmFiles) && same(record.npmFiles, npmFiles), 'Prepared npm files differ from verified archive');
  return record;
}
function unpackRegular(archive, root) {
  noLinkPath(root); mkdirSync(root, { recursive: true });
  for (const [name, bytes] of archive.files) writeNew(confined(root, join(root, 'package', name)), bytes);
}
function prepare(ctx) {
  if (existsSync(join(ctx.oracle, '.pisharp-responses-sdk-prepared.json'))) { validatePrepared(ctx); return { status: 'existing prepared layout verified; no writes', oracle: ctx.oracle }; }
  if (existsSync(ctx.oracle)) requireThat(lstatSync(ctx.oracle).isDirectory() && readdirSync(ctx.oracle).length === 0, 'Preserving nonempty or incomplete oracle directory');
  mkdirSync(ctx.oracle, { recursive: true });
  writeNew(join(ctx.oracle, '.pisharp-responses-sdk-owner.json'), jsonBytes(expectedOwner(ctx)));
  for (const folder of ['home', 'tmp', 'cache', 'config', 'config/hooks', 'config/git-template', 'archives', 'tools', 'captures']) mkdirSync(confined(ctx.oracle, join(ctx.oracle, folder)), { recursive: true });
  for (const [path, bytes] of preparedInputs(ctx)) writeNew(confined(ctx.oracle, join(ctx.oracle, path)), bytes);
  const cacheRoot = join(ctx.plan.workspace.existingQualifiedOracle, 'cache', '_cacache'), cacheFiles = inventory(cacheRoot);
  for (const file of cacheFiles) {
    const bytes = safeRegularFile(confined(cacheRoot, join(cacheRoot, file.path)));
    requireThat(digest(bytes) === file.sha256, 'Existing cache changed during copy');
    if (file.path.startsWith('content-v2/sha512/')) requireThat(file.path.slice('content-v2/sha512/'.length).replaceAll('/', '') === digest(bytes, 'sha512'), 'Content-addressed cache object differs from its filename');
    writeNew(confined(ctx.oracle, join(ctx.oracle, 'cache', '_cacache', file.path)), bytes);
  }
  requireThat(same(cacheFiles, inventory(cacheRoot)), 'Existing cache changed; preserving incomplete preparation');
  unpackRegular(ctx.npmArchive, join(ctx.oracle, 'tools', 'npm-11.6.2'));
  const snapshot = join(ctx.oracle, 'upstream');
  run(gitExe, ['-c', 'core.autocrlf=false', '-c', `core.hooksPath=${join(ctx.oracle, 'config', 'hooks')}`, 'clone', '--local', '--no-hardlinks', '--quiet', `--template=${join(ctx.oracle, 'config', 'git-template')}`, ctx.source, snapshot], ctx.oracle, ctx.environment);
  requireThat(same(validateSource(snapshot, ctx.plan, ctx.environment), ctx.sourceFingerprint), 'Fresh clone differs from canonical source');
  requireThat(run(process.execPath, [join(ctx.oracle, 'tools/npm-11.6.2/package/bin/npm-cli.js'), '--version'], ctx.oracle, ctx.environment) === ctx.plan.packageManager.version, 'npm CLI version differs');
  requireThat(same(validateSource(ctx.source, ctx.plan, ctx.environment), ctx.sourceFingerprint), 'Original source changed during preparation');
  const record = { status: 'prepared; SDK restore not performed', owner: expectedOwner(ctx), sourceFingerprint: ctx.sourceFingerprint, npmFiles: fingerprint(inventory(join(ctx.oracle, 'tools', 'npm-11.6.2'))), copiedCacheFiles: fingerprint(cacheFiles), copiedCache: 'Owned copy of _cacache only; no accepted cache logs or mutable shared cache path', inputHashes: preparedInputs(ctx).map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: digest(bytes) })), networkUsed: false, dependenciesInstalled: false };
  writeNew(join(ctx.oracle, '.pisharp-responses-sdk-prepared.json'), jsonBytes(record));
  validatePrepared(ctx);
  return { status: record.status, oracle: ctx.oracle, sourceFingerprint: ctx.sourceFingerprint, npmFiles: record.npmFiles, copiedCacheFiles: record.copiedCacheFiles, dependenciesInstalled: false };
}
function downloadExact(url) {
  requireThat(url === 'https://registry.npmjs.org/openai/-/openai-7.19.0.tgz', 'Download URL outside reviewed scope');
  return new Promise((resolveDownload, reject) => {
    const agent = new Agent({ keepAlive: false, proxyEnv: {} });
    const request = get(url, { agent, headers: { 'User-Agent': 'PiSharp-offline-Responses-oracle-setup', Accept: 'application/octet-stream', 'Accept-Encoding': 'identity' } }, response => {
      if (response.statusCode !== 200) { response.resume(); reject(new Error('Official SDK archive response not HTTP 200; redirects not followed')); return; }
      const chunks = []; let length = 0;
      response.on('data', chunk => { length += chunk.length; if (length > 32 * 1024 * 1024) request.destroy(new Error('SDK archive exceeds download bound')); else chunks.push(chunk); });
      response.on('error', reject); response.on('end', () => resolveDownload(Buffer.concat(chunks)));
    });
    request.on('close', () => agent.destroy()); request.on('error', reject); request.setTimeout(30000, () => request.destroy(new Error('SDK archive read timeout')));
  });
}
function validateInstalled(ctx, reports) {
  const root = join(ctx.oracle, 'node_modules'); noLinkPath(root);
  requireThat(same(readdirSync(root).filter(name => name !== '.package-lock.json').sort(), packageNames), 'Installed package closure is larger or different than reviewed');
  const installed = [];
  for (const item of reports) {
    const packageRoot = join(root, item.row.name), files = inventory(packageRoot);
    const expected = archiveFiles(item.archive);
    requireThat(same(files, expected), 'Installed dependency bytes differ from inspected archive');
    requireThat(!files.some(file => /\.node$/i.test(file.path)), 'Installed native addon rejected');
    installed.push({ name: item.row.name, version: item.row.lockEntry.version, files: fingerprint(files), archiveSha256: item.report.sha256, manifestSha256: item.report.manifestSha256, licenses: item.report.licenses });
  }
  if (existsSync(join(root, '.package-lock.json'))) {
    const hidden = JSON.parse(safeRegularFile(join(root, '.package-lock.json')).toString('utf8'));
    requireThat(same(Object.keys(hidden.packages).sort(), ctx.plan.packages.map(row => row.projectedLockPath).sort()), 'Hidden npm lock contains unreviewed packages');
  }
  return installed;
}
async function restore(ctx) {
  validatePrepared(ctx);
  const restoredPath = join(ctx.oracle, '.pisharp-responses-sdk-restored.json');
  if (existsSync(restoredPath)) {
    const record = JSON.parse(safeRegularFile(restoredPath).toString('utf8'));
    requireThat(same(record.owner, expectedOwner(ctx)) && record.status === 'three dependencies installed; wrapper qualification pending', 'Restored record differs');
    const reports = ctx.plan.packages.map(row => ({ row, ...packageArchiveReport(safeRegularFile(join(ctx.oracle, 'archives', `${row.name}-${row.lockEntry.version}.tgz`)), row, ctx.plan) }));
    requireThat(same(validateInstalled(ctx, reports), record.installed), 'Restored dependency evidence changed');
    return { status: 'existing restore verified; no install or network', oracle: ctx.oracle, installed: record.installed.map(({ name, version, files }) => ({ name, version, files })) };
  }
  requireThat(!existsSync(join(ctx.oracle, 'node_modules')) && !existsSync(join(ctx.oracle, '.pisharp-responses-sdk-restore-started.json')), 'Preserving an incomplete or uncertain previous restore');
  writeNew(join(ctx.oracle, '.pisharp-responses-sdk-restore-started.json'), jsonBytes({ owner: expectedOwner(ctx), action: 'Explicit --restore: exact official registry three-package ci, scripts/optional peers disabled', credentialsUsed: false, providerCalls: false }));
  const sdk = ctx.plan.packages.find(row => row.name === 'openai'), sdkPath = join(ctx.oracle, 'archives', `openai-${sdk.lockEntry.version}.tgz`);
  const sdkBytes = existsSync(sdkPath) ? safeRegularFile(sdkPath) : await downloadExact(sdk.lockEntry.resolved);
  const inspectedSdk = packageArchiveReport(sdkBytes, sdk, ctx.plan);
  if (!existsSync(sdkPath)) writeNew(sdkPath, sdkBytes);
  writeNew(join(ctx.oracle, 'archives', 'openai-7.19.0.inspection.json'), jsonBytes(inspectedSdk.report));
  const reports = ctx.plan.packages.map(row => ({ row, ...(row.name === 'openai' ? inspectedSdk : packageArchiveReport(safeRegularFile(join(ctx.oracle, 'archives', `${row.name}-${row.lockEntry.version}.tgz`)), row, ctx.plan)) }));
  const cacheRoot = join(ctx.oracle, 'cache', '_cacache');
  const prepared = JSON.parse(safeRegularFile(join(ctx.oracle, '.pisharp-responses-sdk-prepared.json')).toString('utf8'));
  requireThat(same(fingerprint(inventory(cacheRoot)), prepared.copiedCacheFiles) && same(fingerprint(inventory(join(ctx.plan.workspace.existingQualifiedOracle, 'cache', '_cacache'))), prepared.copiedCacheFiles), 'Prepared/original cache differs before restore; preserving it');
  run(process.execPath, [join(ctx.oracle, 'tools/npm-11.6.2/package/bin/npm-cli.js'), ...ctx.plan.setupProposal.restoreArguments, '--prefix', ctx.oracle, '--cache', join(ctx.oracle, 'cache'), '--userconfig', join(ctx.oracle, 'config/user.npmrc'), '--globalconfig', join(ctx.oracle, 'config/global.npmrc')], ctx.oracle, ctx.environment, { timeout: 240000 });
  validatePrepared(ctx);
  const installed = validateInstalled(ctx, reports);
  requireThat(same(validateSource(ctx.source, ctx.plan, ctx.environment), ctx.sourceFingerprint), 'Original source changed during restore');
  const record = { owner: expectedOwner(ctx), status: 'three dependencies installed; wrapper qualification pending', installed, sourceFingerprint: ctx.sourceFingerprint, networkScope: 'Exact reviewed official registry package URLs; no provider endpoints', lifecycleScripts: false, optionalPeers: [], nativeAddons: false, apiCredentials: false, providerCalls: false, sdkShimOrSourceTransform: false, loadedModuleClosureQualified: false };
  writeNew(restoredPath, jsonBytes(record));
  return { status: record.status, oracle: ctx.oracle, installed: installed.map(({ name, version, files }) => ({ name, version, files })), sourceFingerprint: ctx.sourceFingerprint, wrapperQualified: false };
}
export async function main(args = process.argv.slice(2)) {
  const settings = parseSetupArguments(args), mode = settings.mode, ctx = context(settings);
  let result;
  if (mode === '--prepare') result = prepare(ctx);
  else if (mode === '--restore') result = await restore(ctx);
  else {
    if (existsSync(join(ctx.oracle, '.pisharp-responses-sdk-prepared.json'))) validatePrepared(ctx);
    result = { status: 'read-only preflight passed; no preparation/install/download executed', mode, oracle: ctx.oracle, existingWorkspace: existsSync(ctx.oracle), sourceFingerprint: ctx.sourceFingerprint, packageProfile: ctx.plan.projection.packageJson.dependencies, npmBundleFiles: ctx.npmArchive.files.size, defaultMutations: false, requiredRestoreAction: 'Root invokes --restore through applicable executor official-registry network approval after reviewing prepared layout' };
  }
  console.log(JSON.stringify(result, null, 2));
  return result;
}
if (process.argv[1] && normalizedPath(process.argv[1]) === normalizedPath(ownPath)) await main();
