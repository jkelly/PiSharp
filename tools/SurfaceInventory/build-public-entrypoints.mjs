import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, lstatSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

const modulePath = fileURLToPath(import.meta.url);
const repository = resolve(dirname(modulePath), '../..');
const COMMIT = 'd86654abb8862e201933517d6f1fce9f88dd117f';
const TREE = '200bd10bb146773516f862a02b8aaebeed163e00';
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const fail = message => { throw new Error(message); };
export const digest = bytes => createHash('sha256').update(bytes).digest('hex');

// JSON.parse alone loses duplicate object keys. Walk the original tokens first.
export function parseJson(bytes, label = 'JSON') {
  const text = Buffer.isBuffer(bytes) ? bytes.toString('utf8') : bytes;
  if (typeof text !== 'string' || (Buffer.isBuffer(bytes) && !Buffer.from(text).equals(bytes))) fail(`${label}: invalid UTF-8`);
  const value = JSON.parse(text);
  let offset = 0;
  const whitespace = () => { while (/\s/.test(text[offset] ?? '') && offset < text.length) offset++; };
  const string = () => {
    const start = offset++;
    while (offset < text.length) {
      const c = text[offset++];
      if (c === '\\') offset++;
      else if (c === '"') return JSON.parse(text.slice(start, offset));
    }
    fail(`${label}: unterminated string`);
  };
  const walk = depth => {
    if (depth > 256) fail(`${label}: excessive JSON depth`);
    whitespace();
    if (text[offset] === '{') {
      offset++; whitespace(); const keys = new Set();
      if (text[offset] === '}') { offset++; return; }
      while (true) {
        const key = string();
        if (keys.has(key)) fail(`${label}: duplicate JSON key ${key}`);
        keys.add(key); whitespace(); offset++; walk(depth + 1); whitespace();
        if (text[offset++] === '}') return;
        whitespace();
      }
    }
    if (text[offset] === '[') {
      offset++; whitespace();
      if (text[offset] === ']') { offset++; return; }
      while (true) { walk(depth + 1); whitespace(); if (text[offset++] === ']') return; }
    }
    if (text[offset] === '"') { string(); return; }
    const token = /^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/.exec(text.slice(offset));
    if (!token) fail(`${label}: invalid token`);
    offset += token[0].length;
  };
  walk(0); whitespace();
  if (offset !== text.length) fail(`${label}: unexpected trailing token`);
  return value;
}

export function confinedPath(path, label = 'path') {
  if (typeof path !== 'string' || !path || path.includes('\\') || /[:\u0000-\u001f\u007f]/.test(path)) fail(`${label}: noncanonical relative path`);
  const parts = path.split('/');
  if (parts.some(part => !part || part === '.' || part === '..')) fail(`${label}: path escapes or is noncanonical`);
  let decoded;
  try { decoded = decodeURIComponent(path); } catch { fail(`${label}: invalid path encoding`); }
  if (decoded !== path && (decoded.includes('\\') || decoded.split('/').some(part => !part || part === '.' || part === '..') || decoded.split('/').length !== parts.length)) fail(`${label}: encoded path escape`);
  return path;
}

function localFile(root, path) {
  confinedPath(path);
  const target = resolve(root, ...path.split('/'));
  const physical = realpathSync(target);
  const rel = relative(realpathSync(root), physical);
  if (isAbsolute(rel) || rel === '..' || rel.startsWith(`..${sep}`) || !lstatSync(target).isFile() || lstatSync(target).isSymbolicLink()) fail(`file not confined: ${path}`);
  return readFileSync(target);
}

export function pinnedBytes(bytes, expected, label) {
  if (!/^[a-f0-9]{64}$/.test(expected) || digest(bytes) !== expected) fail(`${label}: SHA-256 mismatch`);
  return bytes;
}

export function parseArgs(args) {
  if (args.length !== 2 || args[0] !== '--source' || !args[1] || args[1].startsWith('--')) fail('Usage: build-public-entrypoints.mjs --source <pinned public Git checkout>');
  return { source: resolve(args[1]) };
}

function targetPath(target, label) {
  if (typeof target !== 'string') fail(`${label}: target must be a string or null`);
  return confinedPath(target.startsWith('./') ? target.slice(2) : target, label);
}

export function declaredEntries(manifest, manifestPath) {
  const result = [];
  const add = (pointer, kind, subpath, conditions, target) => {
    if (target !== null) {
      if (kind === 'exports' && !target.startsWith('./')) fail(`${manifestPath}${pointer}: export target must start with ./`);
      targetPath(target, `${manifestPath}${pointer}`);
    }
    result.push({ id: `${manifestPath}#${pointer}`, kind, subpath, conditions, ordinal: result.length, target });
  };
  const escape = s => s.replaceAll('~', '~0').replaceAll('/', '~1');
  const walk = (value, pointer, subpath, conditions) => {
    if (value === null || typeof value === 'string') { add(pointer, 'exports', subpath, conditions, value); return; }
    if (Array.isArray(value)) { value.forEach((item, index) => walk(item, `${pointer}/${index}`, subpath, [...conditions, { fallbackIndex: index }])); return; }
    if (!value || typeof value !== 'object') fail(`${manifestPath}${pointer}: unsupported exports value`);
    const keys = Object.keys(value);
    const subpaths = keys.filter(key => key.startsWith('.'));
    if (subpaths.length && subpaths.length !== keys.length) fail(`${manifestPath}${pointer}: mixed subpath and condition keys`);
    for (const key of keys) {
      if (subpaths.length) {
        if (pointer !== '/exports' || (key !== '.' && !key.startsWith('./'))) fail(`${manifestPath}${pointer}: invalid subpath nesting`);
        if (key !== '.') targetPath(key, 'export subpath');
        walk(value[key], `${pointer}/${escape(key)}`, key, conditions);
      } else {
        if (!key || /^(?:0|[1-9]\d*)$/.test(key)) fail(`${manifestPath}${pointer}: invalid condition key`);
        walk(value[key], `${pointer}/${escape(key)}`, subpath, [...conditions, key]);
      }
    }
  };
  if (Object.hasOwn(manifest, 'exports')) walk(manifest.exports, '/exports', '.', []);
  for (const key of ['main', 'types']) if (Object.hasOwn(manifest, key)) add(`/${key}`, key, '.', [], manifest[key]);
  if (Object.hasOwn(manifest, 'bin')) {
    if (typeof manifest.bin === 'string') add('/bin', 'bin', manifest.name?.split('/').at(-1) ?? null, [], manifest.bin);
    else if (manifest.bin && !Array.isArray(manifest.bin) && typeof manifest.bin === 'object') {
      for (const [name, target] of Object.entries(manifest.bin)) add(`/bin/${escape(name)}`, 'bin', name, [], target);
    } else fail(`${manifestPath}: invalid bin declaration`);
  }
  if (new Set(result.map(entry => entry.id)).size !== result.length) fail('duplicate entry identity');
  return result;
}

// This expands declared file patterns, without choosing an active export condition.
export function matchTarget(pattern, paths) {
  const pieces = pattern.split('*');
  if (pieces.length > 2) fail('multiple target wildcards require owner/parser review');
  const escape = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const regex = new RegExp(`^${pieces.map(escape).join('(.*)')}$`);
  return paths.map(path => { const match = regex.exec(path); return match ? { path, substitution: match[1] ?? null } : null; }).filter(Boolean).sort((a, b) => compare(a.path, b.path));
}

export function parseTar(tar) {
  if (!Buffer.isBuffer(tar) || tar.length % 512 !== 0) fail('archive: invalid tar length');
  const files = new Map(); let offset = 0;
  const field = (header, start, length) => {
    const bytes = header.subarray(start, start + length); const zero = bytes.indexOf(0);
    const significant = bytes.subarray(0, zero < 0 ? bytes.length : zero); const text = significant.toString('utf8');
    if (!Buffer.from(text).equals(significant)) fail('archive: invalid UTF-8 header');
    return text;
  };
  const octal = (header, start, length) => {
    const value = header.subarray(start, start + length).toString('ascii').replaceAll('\0', '').trim();
    if (!/^[0-7]+$/.test(value)) fail('archive: invalid octal field');
    const number = Number.parseInt(value, 8);
    if (!Number.isSafeInteger(number)) fail('archive: unsafe size');
    return number;
  };
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) {
      if (tar.length - offset < 1024 || !tar.subarray(offset).every(byte => byte === 0)) fail('archive: invalid end blocks');
      return files;
    }
    let checksum = 0;
    for (let i = 0; i < 512; i++) checksum += i >= 148 && i < 156 ? 32 : header[i];
    if (checksum !== octal(header, 148, 8)) fail('archive: header checksum mismatch');
    const name = field(header, 0, 100), prefix = field(header, 345, 155);
    const full = prefix ? `${prefix}/${name}` : name;
    const type = field(header, 156, 1), size = octal(header, 124, 12);
    if (!full.startsWith('package/')) fail('archive: unexpected package root');
    if (type !== '' && type !== '0') fail('archive: unsupported member kind');
    const path = confinedPath(full.slice(8), 'archive member');
    if (files.has(path)) fail(`archive: duplicate member ${path}`);
    offset += 512;
    if (size > tar.length - offset) fail('archive: truncated member');
    const bytes = tar.subarray(offset, offset + size);
    files.set(path, { path, bytes: size, sha256: digest(bytes), content: bytes });
    const padded = Math.ceil(size / 512) * 512;
    if (padded > tar.length - offset || !tar.subarray(offset + size, offset + padded).every(byte => byte === 0)) fail('archive: invalid member padding');
    offset += padded;
  }
  fail('archive: missing end blocks');
}

export function qualifyArchive(bytes, inspection, acquisition, receipt, expected, sourceSha) {
  if (bytes.length !== expected.bytes || `sha512-${createHash('sha512').update(bytes).digest('base64')}` !== expected.integrity || createHash('sha1').update(bytes).digest('hex') !== expected.shasum) fail('archive: size/SRI/SHA-1 mismatch');
  const p = inspection.package;
  if (inspection.kind !== 'official-pi-ai-release-package-inspection' || p.name !== expected.name || p.version !== expected.version || p.gitHeadDeclaredByRegistry !== sourceSha || inspection.releaseSourceAuthority.sourceSha !== sourceSha || p.bytes !== bytes.length || p.sha256 !== digest(bytes) || p.integrity !== expected.integrity || p.shasum !== expected.shasum || p.sha512AndSha1Verified !== true) fail('archive: inspection identity mismatch');
  if (acquisition.bytes !== bytes.length || acquisition.sha256 !== p.sha256 || acquisition.integrity !== expected.integrity || acquisition.shasum !== expected.shasum || acquisition.url !== p.url || acquisition.metadataSha256 !== p.registryMetadataSha256 || acquisition.installed !== false || acquisition.extracted !== false || acquisition.executed !== false) fail('archive: acquisition identity mismatch');
  const record = receipt.files?.filter(file => file.path === 'pi-ai-0.99.1.tgz');
  if (receipt.sourceSha !== sourceSha || record?.length !== 1 || record[0].sha256 !== p.sha256 || record[0].bytes !== bytes.length) fail('archive: receipt identity mismatch');
  const files = parseTar(gunzipSync(bytes, { maxOutputLength: 16 * 1024 * 1024 }));
  if (files.size !== expected.regularFileCount || p.observedRegularFiles !== files.size || p.registryReportedFiles !== files.size || inspection.packagedFiles.length !== files.size) fail('archive: regular file count mismatch');
  const seen = new Set();
  for (const record of inspection.packagedFiles) {
    confinedPath(record.path);
    if (seen.has(record.path)) fail('archive: duplicate inspection member');
    seen.add(record.path); const actual = files.get(record.path);
    if (!actual || actual.bytes !== record.bytes || actual.sha256 !== record.sha256) fail(`archive: file mismatch ${record.path}`);
  }
  const packageBytes = files.get('package.json')?.content;
  if (!packageBytes || digest(packageBytes) !== p.packageJsonSha256 || packageBytes.length !== p.packageJsonBytes) fail('archive: package manifest mismatch');
  return files;
}

function sourceCandidates(entry, packageRoot, canonical) {
  if (entry.target === null) return { basis: 'BlockedDeclaration', state: 'NotApplicable', candidates: [] };
  const target = targetPath(entry.target, entry.id);
  const explicit = entry.kind === 'exports' && entry.conditions.includes('source');
  const direct = `${packageRoot ? packageRoot + '/' : ''}${target}`;
  let pattern;
  if (explicit) pattern = `${packageRoot ? packageRoot + '/' : ''}${target}`;
  else if (canonical.has(direct)) pattern = direct;
  else if (target.startsWith('dist/') && /\.(?:d\.ts|js)$/.test(target)) pattern = `${packageRoot ? packageRoot + '/' : ''}src/${target.slice(5).replace(/\.(?:d\.ts|js)$/, '.ts')}`;
  const matches = pattern ? matchTarget(pattern, [...canonical.keys()]) : [];
  const exact = explicit || canonical.has(direct);
  return { basis: explicit ? 'ExplicitSourceCondition' : canonical.has(direct) ? 'DirectCanonicalTarget' : 'UnverifiedDistToSrcCandidate', state: exact && matches.length ? 'DeclaredCanonicalSourcePresent' : 'Unverified', pattern: pattern ?? null, candidates: matches.map(({ path, substitution }) => ({ path, substitution, sha256: canonical.get(path).sha256, gitBlobId: canonical.get(path).gitBlobId })) };
}

export function buildModel(canonical, registryRecords, archiveFiles, expected, productRoot) {
  for (const [key, file] of canonical) {
    if (key !== confinedPath(file.path) || !Buffer.isBuffer(file.content) || digest(file.content) !== file.sha256) fail(`canonical model content identity mismatch: ${key}`);
  }
  const metadata = new Map();
  for (const record of registryRecords) {
    if (metadata.has(record.name)) fail(`duplicate registry identity: ${record.name}`);
    if (record.version !== '0.99.1' || record.gitHead !== COMMIT) fail('registry baseline identity mismatch');
    metadata.set(record.name, record);
  }
  const packages = [], names = new Map(), ids = new Set();
  for (const file of [...canonical.values()].filter(file => /(?:^|\/)package\.json$/.test(file.path)).sort((a, b) => compare(a.path, b.path))) {
    const manifest = parseJson(file.content, file.path);
    if (typeof manifest.name !== 'string' || !manifest.name) fail(`missing package name: ${file.path}`);
    if (names.has(manifest.name)) fail(`duplicate package identity: ${manifest.name}`);
    names.set(manifest.name, file.path);
    const published = manifest.private !== true;
    const registry = metadata.get(manifest.name);
    if (published && (!registry || manifest.version !== registry.version)) fail(`published package metadata missing: ${manifest.name}`);
    const packageRoot = file.path === 'package.json' ? '' : file.path.slice(0, -13);
    const acquired = manifest.name === expected.acquiredPackage;
    if (acquired && (!archiveFiles || !archiveFiles.get('package.json')?.content.equals(file.content))) fail('released/canonical package manifest differs');
    const entries = declaredEntries(manifest, file.path).map(entry => {
      if (ids.has(entry.id)) fail(`duplicate entry identity: ${entry.id}`);
      ids.add(entry.id);
      const target = entry.target === null ? null : targetPath(entry.target, entry.id);
      const matches = acquired && target !== null ? matchTarget(target, [...archiveFiles.keys()]) : [];
      return { ...entry, sourceMapping: sourceCandidates(entry, packageRoot, canonical), releasedArtifact: {
        state: entry.target === null ? 'BlockedDeclaration' : acquired ? (matches.length ? 'PresentInQualifiedArchive' : 'AbsentInQualifiedArchive') : 'Unverified',
        archiveAcquired: acquired,
        targets: matches.map(({ path, substitution }) => ({ path, substitution, publicSubpath: entry.subpath?.includes('*') && substitution !== null ? entry.subpath.replaceAll('*', substitution) : entry.subpath, bytes: archiveFiles.get(path).bytes, sha256: archiveFiles.get(path).sha256 })),
        runtimeExecuted: false
      }, implementationAcceptance: 'Deferred', implementationEvidenceState: 'Unverified' };
    });
    packages.push({ manifest: { path: file.path, sha256: file.sha256, gitBlobId: file.gitBlobId }, metadata: manifest, publication: {
      declaredPrivate: manifest.private === true,
      registryMetadata: registry ?? null,
      archiveState: acquired ? 'QualifiedAcquiredArchive' : published ? 'Unacquired' : 'UnverifiedPrivatePackagePublication',
      scopeDecision: 'Unverified; runtime tracing and owner decision required'
    }, entries });
  }
  const published = packages.filter(p => !p.publication.declaredPrivate);
  if (packages.length !== expected.packageManifestCount || published.length !== expected.publishedPackageCount || metadata.size !== published.length || published.some(p => !metadata.has(p.metadata.name))) fail('package/registry census mismatch');
  const byName = new Map(packages.map(p => [p.metadata.name, p]));
  if (!byName.has(productRoot)) fail('product dependency root missing');
  const closure = new Set();
  const visit = name => { if (closure.has(name)) return; closure.add(name); const p = byName.get(name); for (const dep of Object.keys(p.metadata.dependencies ?? {}).sort(compare)) if (byName.has(dep)) visit(dep); };
  visit(productRoot);
  return { packages, declaredProductWorkspaceDependencyClosure: [...closure].sort(compare), entryDeclarationCount: ids.size, publishedPackageCount: published.length, unacquiredPublishedPackageCount: published.filter(p => p.publication.archiveState === 'Unacquired').length };
}

function readCanonical(source, plan) {
  const git = args => execFileSync('git', ['-c', `safe.directory=${source}`, '-c', 'core.fsmonitor=false', '-C', source, ...args], { encoding: null, maxBuffer: 256 * 1024 * 1024, timeout: 60000 });
  if (git(['rev-parse', `${COMMIT}^{tree}`]).toString('utf8').trim() !== TREE) fail('canonical tree mismatch');
  const records = git(['ls-tree', '-rz', '--full-tree', COMMIT]).toString('utf8').split('\0').filter(Boolean).map(record => {
    const match = /^(100644|100755) blob ([a-f0-9]{40})\t(.+)$/.exec(record);
    if (!match) fail('unexpected canonical tree entry');
    return { mode: match[1], gitBlobId: match[2], path: confinedPath(match[3]) };
  }).sort((a, b) => compare(a.path, b.path));
  if (records.length !== plan.source.fileCount || new Set(records.map(r => r.path)).size !== records.length) fail('canonical file census mismatch');
  const batch = execFileSync('git', ['-c', `safe.directory=${source}`, '-C', source, 'cat-file', '--batch'], { input: records.map(r => r.gitBlobId).join('\n') + '\n', maxBuffer: 256 * 1024 * 1024, timeout: 60000 });
  const conversions = new Map(plan.source.allowedCheckoutConversions.map(item => [item.path, item]));
  if (conversions.size !== plan.source.allowedCheckoutConversions.length) fail('duplicate conversion identity');
  let offset = 0; const files = new Map();
  for (const record of records) {
    const end = batch.indexOf(10, offset); if (end < 0) fail('truncated Git batch header');
    const header = /^([a-f0-9]{40}) blob (\d+)$/.exec(batch.subarray(offset, end).toString('ascii'));
    if (!header || header[1] !== record.gitBlobId) fail('Git batch identity mismatch');
    const size = Number(header[2]); offset = end + 1;
    if (!Number.isSafeInteger(size) || offset + size >= batch.length || batch[offset + size] !== 10) fail('invalid Git batch body');
    const content = batch.subarray(offset, offset + size); offset += size + 1;
    if (createHash('sha1').update(Buffer.from(`blob ${size}\0`)).update(content).digest('hex') !== record.gitBlobId) fail('Git blob content mismatch');
    const sha256 = digest(content), checkout = localFile(source, record.path), conversion = conversions.get(record.path);
    if (conversion && (conversion.sha256 !== sha256 || conversion.conversion !== 'LF-to-CRLF')) fail('conversion source pin mismatch');
    let checkoutMatch = 'RawByteIdentical';
    if (!content.equals(checkout)) {
      if (!conversion || content.includes(13) || !Buffer.from(content.toString('utf8')).equals(content) || !Buffer.from(content.toString('utf8').replaceAll('\n', '\r\n')).equals(checkout)) fail(`checkout differs from canonical source: ${record.path}`);
      checkoutMatch = 'DeclaredLFToCRLFConversion';
    }
    files.set(record.path, { ...record, bytes: size, sha256, checkout: { bytes: checkout.length, sha256: digest(checkout), match: checkoutMatch }, content });
  }
  if (offset !== batch.length || [...conversions.keys()].some(path => !files.has(path))) fail('canonical/conversion completeness mismatch');
  pinnedBytes(files.get(plan.source.attributes.path)?.content ?? Buffer.alloc(0), plan.source.attributes.sha256, 'source attributes');
  return files;
}

export function generate(source) {
  const planPath = 'compatibility/public-entrypoints.plan.json';
  const planBytes = localFile(repository, planPath), plan = parseJson(planBytes, planPath);
  if (plan.schemaVersion !== 1 || plan.source.commit !== COMMIT || plan.source.tree !== TREE || plan.source.fileCount !== 2093 || plan.source.packageManifestCount !== 23 || plan.source.publishedPackageCount !== 13 || plan.releasedArchive.name !== '@earendil-works/pi-ai' || plan.releasedArchive.regularFileCount !== 813) fail('unsupported inventory plan');
  if (process.version !== plan.runtime.version || digest(readFileSync(process.execPath)) !== plan.runtime.sha256) fail('reference runtime pin mismatch');
  const inputs = new Map(), inputEvidence = [];
  for (const input of plan.inputs) {
    if (inputs.has(input.role)) fail('duplicate plan input role');
    const bytes = pinnedBytes(localFile(repository, input.path), input.sha256, input.path);
    inputs.set(input.role, bytes); inputEvidence.push({ ...input, bytes: bytes.length });
  }
  const required = ['existingInventory', 'registryInspection', 'aiInspection', 'aiReceipt', 'aiAcquisition', 'aiArchive'];
  if (inputs.size !== required.length || required.some(role => !inputs.has(role))) fail('incomplete inventory inputs');
  const inventory = parseJson(inputs.get('existingInventory'));
  if (inventory.sourceSha !== COMMIT || inventory.sourceTree !== TREE || inventory.rows.length !== 712 || inventory.rows.some(row => row.implementationAcceptance !== 'Deferred' || row.implementationEvidenceState !== 'Unverified' || row.observed.executed !== false || row.observed.behavioralCapture !== false)) fail('existing inventory identity/acceptance mismatch');
  const registry = parseJson(inputs.get('registryInspection'));
  if (registry.sourceSha !== COMMIT || registry.kind !== 'official-released-source-and-registry-metadata-inspection') fail('registry inspection identity mismatch');
  const archiveBytes = inputs.get('aiArchive'), inspection = parseJson(inputs.get('aiInspection'));
  const archiveFiles = qualifyArchive(archiveBytes, inspection, parseJson(inputs.get('aiAcquisition')), parseJson(inputs.get('aiReceipt')), plan.releasedArchive, COMMIT);
  const canonical = readCanonical(realpathSync(source), plan);
  const model = buildModel(canonical, registry.publicNpmVersionMetadata, archiveFiles, { ...plan.source, acquiredPackage: plan.releasedArchive.name }, plan.productDependencyRoot);
  const sourceFiles = [...canonical.values()].map(({ content, ...file }) => ({ ...file, contractReviewState: 'Unverified', url: `https://github.com/earendil-works/pi/blob/${COMMIT}/${file.path}` }));
  const converted = sourceFiles.filter(file => file.checkout.match === 'DeclaredLFToCRLFConversion');
  return {
    schemaVersion: 1, inventoryId: plan.inventoryId, kind: 'static-canonical-source-and-released-entrypoint-ledger',
    acceptance: { implementationAcceptance: 'Deferred', implementationEvidenceState: 'Unverified', behavioralCaptures: 0, nativeParityAssertions: 0, phaseGatesClosed: 0 },
    provenance: { sourceSha: COMMIT, sourceTree: TREE, generatorSha256: digest(readFileSync(modulePath)), plan: { path: planPath, sha256: digest(planBytes) }, runtime: plan.runtime, inputs: inputEvidence, sourceExecution: false, network: false, sourceWrites: false, filesystemOutput: 'stdout only' },
    existingInventory: { path: plan.inputs.find(input => input.role === 'existingInventory').path, sha256: digest(inputs.get('existingInventory')), rowCount: inventory.rows.length, importedAs: 'immutable reference; rows and acceptance unchanged' },
    census: { canonicalSourceFiles: sourceFiles.length, rawCheckoutMatches: sourceFiles.length - converted.length, declaredCheckoutConversions: converted.length, packageManifests: model.packages.length, publishedPackages: model.publishedPackageCount, unacquiredPublishedArchives: model.unacquiredPublishedPackageCount, entryDeclarations: model.entryDeclarationCount },
    acquiredSourceConversions: { attributes: plan.source.attributes, allowlist: plan.source.allowedCheckoutConversions, observed: converted.map(file => ({ path: file.path, canonicalSha256: file.sha256, checkout: file.checkout })) },
    releasedArchive: { ...plan.releasedArchive, sha256: digest(archiveBytes), inspectionSha256: digest(inputs.get('aiInspection')), runtimeExecuted: false, regularFiles: [...archiveFiles.values()].map(({ content, ...file }) => file).sort((a, b) => compare(a.path, b.path)) },
    sourceFiles, packages: model.packages,
    declaredProductWorkspaceDependencyClosure: { names: model.declaredProductWorkspaceDependencyClosure, meaning: 'manifest dependencies only; runtime reachability and feature scope remain Unverified' },
    unresolvedObligations: plan.reviewObligations
  };
}

if (process.argv[1] && resolve(process.argv[1]) === modulePath) {
  try { const { source } = parseArgs(process.argv.slice(2)); process.stdout.write(JSON.stringify(generate(source), null, 2) + '\n'); }
  catch (error) { process.stderr.write(`public-entrypoints: ${error.message}\n`); process.exitCode = 1; }
}
