// Exact official archive acquisition and inert inspection only. Never imports
// acquired modules, extracts members, installs packages or invokes a process.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { Agent, get } from 'node:https';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync, gzipSync } from 'node:zlib';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const recipePath = join(repo, 'compatibility/ast-inventory-setup.json');
const recipeHash = 'c451615a82d7d813e35d86fabfa6550df7d31bfd03492c7e603dd031d9682e18';
const parserPath = join(repo, 'tools/CompatibilityReport/raw-json.mjs');
const parserHash = '58c378290d0be114e740dadda934d9a57e16a9321ae05eb8e34320e11d561ee9';
const admissionPath = join(repo, 'compatibility/ast-archive-admission.json');
const defaultRawRoot = join(repo, 'artifacts/ast-archive-inspection');
const runtimeHash = '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237';
const selectedPaths = [
  'node_modules/typescript', 'node_modules/@typescript/typescript-win32-x64',
  'node_modules/@babel/parser', 'node_modules/@babel/types',
  'node_modules/@babel/helper-string-parser', 'node_modules/@babel/helper-validator-identifier'
];
export const limits = Object.freeze({
  compressedBytes: 32 * 1024 * 1024, expandedBytes: 192 * 1024 * 1024,
  memberBytes: 160 * 1024 * 1024, members: 12000, depth: 32,
  pathUtf8Bytes: 1024, componentUtf8Bytes: 255, paxBytes: 64 * 1024,
  jsonBytes: 2 * 1024 * 1024, noticeBytes: 2 * 1024 * 1024,
  apiTextBytes: 8 * 1024 * 1024, totalCompressedBytes: 128 * 1024 * 1024,
  responseTimeoutMs: 30000
});
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
const bytesJson = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
const sortPath = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
const utf8 = bytes => new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
function regular(path) {
  let current = resolve(path);
  while (true) { if (existsSync(current)) assert(!lstatSync(current).isSymbolicLink(), 'Link/junction rejected: ' + current); const parent = dirname(current); if (parent === current) break; current = parent; }
  assert(lstatSync(path).isFile(), 'Expected regular file'); return readFileSync(path);
}
function confined(root, target) {
  const path = resolve(target), suffix = relative(resolve(root), path);
  assert(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Path escapes owned root'); return path;
}
function writeNew(path, bytes) {
  assert(!existsSync(path), 'Refuse existing evidence file: ' + path);
  let current = dirname(resolve(path));
  while (true) { if (existsSync(current)) assert(!lstatSync(current).isSymbolicLink(), 'Link/junction in write path'); const parent = dirname(current); if (parent === current) break; current = parent; }
  mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes, { flag: 'wx' });
}
function checkedJson(bytes, maximum = limits.jsonBytes) { assert(bytes.length <= maximum, 'JSON byte limit'); return parseJsonSupported(utf8(bytes)); }
function safeMember(name, directory, caps) {
  assert(!/[\\\0]/.test(name) && !name.startsWith('/') && !/^[A-Za-z]:/.test(name), 'Unsafe tar path');
  assert(Buffer.byteLength(name) <= caps.pathUtf8Bytes, 'Tar path byte limit');
  const value = directory && name.endsWith('/') ? name.slice(0, -1) : name;
  const parts = value.split('/'); assert(parts.length <= caps.depth, 'Tar path depth limit');
  for (const part of parts) {
    assert(part && part !== '.' && part !== '..' && !/[<>:"|?*\x00-\x1f\x7f]/.test(part) && !/[. ]$/.test(part), 'Unsafe tar component');
    assert(Buffer.byteLength(part) <= caps.componentUtf8Bytes, 'Tar component byte limit');
    assert(!/^(?:con|prn|aux|nul|com[1-9\u00b9\u00b2\u00b3]|lpt[1-9\u00b9\u00b2\u00b3])(?:\.|$)/i.test(part), 'Reserved Windows tar component');
  }
  assert(value === 'package' ? directory : value.startsWith('package/'), 'Tar outside package/');
  return value === 'package' ? '' : value.slice(8);
}
export function inspectArchive(compressed, caps = limits) {
  assert(compressed.length > 0 && compressed.length <= caps.compressedBytes, 'Compressed archive byte limit');
  const tar = gunzipSync(compressed, { maxOutputLength: caps.expandedBytes });
  assert(tar.length <= caps.expandedBytes && tar.length % 512 === 0, 'Expanded tar byte/alignment limit');
  const files = new Map(), seen = new Map(); let offset = 0, memberCount = 0, directories = 0, metadataHeaders = 0, localPax = null;
  const field = (header, start, count) => { const data = header.subarray(start, start + count), end = data.indexOf(0); if (end >= 0) { assert(data.subarray(end).every(b => b === 0), 'Tar field bytes after NUL'); return utf8(data.subarray(0, end)); } return utf8(data); };
  const octal = (header, start, count) => {
    const data = header.subarray(start, start + count); assert(data.every(b => b === 0 || b === 32 || (b >= 48 && b <= 55)), 'Unsupported tar numeric encoding');
    const raw = data.toString('ascii').replace(/^[\0 ]+|[\0 ]+$/g, ''); assert(/^[0-7]*$/.test(raw), 'Embedded tar numeric separator');
    const result = raw ? Number.parseInt(raw, 8) : 0; assert(Number.isSafeInteger(result), 'Unsafe tar number'); return result;
  };
  const pax = data => {
    assert(data.length <= caps.paxBytes, 'PAX byte limit'); const result = Object.create(null); let cursor = 0;
    while (cursor < data.length) {
      const space = data.indexOf(32, cursor); assert(space > cursor, 'Invalid PAX record');
      const sizeText = data.subarray(cursor, space).toString('ascii'); assert(/^[1-9][0-9]*$/.test(sizeText), 'Invalid PAX length');
      const end = cursor + Number(sizeText); assert(Number.isSafeInteger(end) && end > space + 1 && end <= data.length && data[end - 1] === 10, 'PAX bounds');
      const record = utf8(data.subarray(space + 1, end - 1)), equals = record.indexOf('='); assert(equals > 0, 'Invalid PAX attribute');
      const key = record.slice(0, equals); assert(['path', 'mtime', 'atime', 'ctime', 'uid', 'gid', 'uname', 'gname'].includes(key) && !Object.hasOwn(result, key), 'Unsupported/duplicate PAX attribute');
      result[key] = record.slice(equals + 1); cursor = end;
    }
    return result;
  };
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(b => b === 0)) {
      assert(tar.length - offset >= 1024 && tar.subarray(offset).every(b => b === 0), 'Invalid tar terminal blocks');
      assert(localPax === null && files.size > 0, 'Dangling PAX or empty tar');
      for (const [key] of seen) { const parts = key.split('/'); for (let i = 1; i < parts.length; i++) assert(seen.get(parts.slice(0, i).join('/')) !== 'file', 'File/directory parent conflict'); }
      return { files, memberCount, directories, metadataHeaders, expandedBytes: tar.length, regularBytes: [...files.values()].reduce((sum, data) => sum + data.length, 0) };
    }
    assert(++memberCount <= caps.members, 'Tar member count limit');
    const sum = header.reduce((total, byte, index) => total + (index >= 148 && index < 156 ? 32 : byte), 0); assert.equal(sum, octal(header, 148, 8), 'Tar checksum');
    const size = octal(header, 124, 12); assert(size <= caps.memberBytes, 'Tar member byte limit');
    const start = offset + 512, end = start + size, next = start + Math.ceil(size / 512) * 512;
    assert(end <= tar.length && next <= tar.length, 'Truncated tar member');
    assert(tar.subarray(end, next).every(b => b === 0), 'Nonzero tar member padding');
    const type = field(header, 156, 1) || '0'; offset = next;
    if (type === 'x') { assert(localPax === null, 'Consecutive PAX headers'); safeMember(field(header, 0, 100), false, caps); localPax = pax(tar.subarray(start, end)); metadataHeaders++; continue; }
    assert(type === '0' || type === '5', 'Tar link/device/global-PAX/extension rejected');
    assert(field(header, 157, 100) === '', 'Tar link target on regular member');
    const prefix = field(header, 345, 155), leaf = field(header, 0, 100);
    const originalName = prefix ? prefix + '/' + leaf : leaf; safeMember(originalName, type === '5', caps);
    const name = safeMember(localPax?.path ?? originalName, type === '5', caps); localPax = null;
    const key = name.toLowerCase(); assert(!seen.has(key), 'Duplicate or Windows case-colliding tar path'); seen.set(key, type === '0' ? 'file' : 'directory');
    if (type === '5') { assert.equal(size, 0, 'Directory member data'); directories++; }
    else { assert(name, 'Empty regular member path'); files.set(name, tar.subarray(start, end)); }
  }
  throw new Error('Missing tar terminal blocks');
}
function noticeFamily(text) { return /Apache License/.test(text) ? 'Apache-2.0-text' : /Permission is hereby granted, free of charge/.test(text) ? 'MIT-text' : /BSD.*License|Redistribution and use in source/.test(text) ? 'BSD-text' : 'unclassified-text'; }
function binaryKind(bytes) {
  if (bytes.length >= 2 && bytes[0] === 77 && bytes[1] === 90) return 'PE/DOS-MZ-header';
  if (bytes.subarray(0, 4).equals(Buffer.from([0x7f, 69, 76, 70]))) return 'ELF-header';
  if (bytes.subarray(0, 4).equals(Buffer.from([0, 97, 115, 109]))) return 'WASM-header';
  return null;
}
function packageSummary(row, archiveBytes, metadataBytes, archiveRecord, metadataRecord) {
  const integrity = 'sha512-' + hash(archiveBytes, 'sha512', 'base64'); assert.equal(integrity, row.lockEntry.integrity, 'Pinned archive SRI differs');
  const metadata = checkedJson(metadataBytes); assert.equal(metadata.name, row.name); assert.equal(metadata.version, row.lockEntry.version);
  assert.equal(metadata.dist.tarball, row.lockEntry.resolved); assert.equal(metadata.dist.integrity, integrity); assert.equal(metadata.dist.shasum, hash(archiveBytes, 'sha1'), 'Registry SHA-1 shasum differs');
  const archive = inspectArchive(archiveBytes), manifestBytes = archive.files.get('package.json'); assert(manifestBytes, 'Packaged manifest absent');
  const manifest = checkedJson(manifestBytes); assert.equal(manifest.name, row.name); assert.equal(manifest.version, row.lockEntry.version); assert.equal(manifest.license, row.lockEntry.license);
  for (const key of ['dependencies', 'optionalDependencies', 'peerDependencies', 'peerDependenciesMeta']) assert.deepEqual(manifest[key] ?? {}, row.lockEntry[key] ?? {}, 'Packaged lock declarations differ: ' + key);
  const fileRows = [...archive.files].map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes), binaryHeader: binaryKind(bytes) })).sort(sortPath);
  const notices = [...archive.files].filter(([path]) => /(^|\/)(?:license|licence|notice|copying)(?:\.[^/]*)?$|(?:third[-_ ]?party.*(?:notice|license|licence))/i.test(path)).map(([path, bytes]) => { assert(bytes.length <= limits.noticeBytes, 'Notice byte limit'); const text = utf8(bytes); return { path, bytes: bytes.length, sha256: hash(bytes), utf8Text: text, observedFamily: noticeFamily(text) }; }).sort(sortPath);
  const keys = ['main', 'module', 'types', 'typings', 'type', 'exports', 'bin', 'engines', 'os', 'cpu', 'scripts', 'dependencies', 'optionalDependencies', 'peerDependencies', 'peerDependenciesMeta'];
  const declarationFields = keys.map(key => ({ key, present: Object.hasOwn(manifest, key), ...(Object.hasOwn(manifest, key) ? { value: manifest[key] } : {}) }));
  const entryNames = new Set(); for (const key of ['main', 'module', 'types', 'typings']) if (typeof manifest[key] === 'string') entryNames.add(manifest[key].replace(/^\.\//, ''));
  for (const value of Object.values(typeof manifest.bin === 'string' ? { bin: manifest.bin } : manifest.bin ?? {})) if (typeof value === 'string') entryNames.add(value.replace(/^\.\//, ''));
  const visitExports = value => { if (typeof value === 'string') { if (!value.includes('*')) entryNames.add(value.replace(/^\.\//, '')); } else if (value && typeof value === 'object') for (const item of Object.values(value)) visitExports(item); }; visitExports(manifest.exports);
  const entryFiles = [...entryNames].sort().map(path => { const bytes = archive.files.get(path); return { path, present: !!bytes, ...(bytes ? { bytes: bytes.length, sha256: hash(bytes), binaryHeader: binaryKind(bytes), ...(/\.(?:[cm]?js|ts)$|(?:^|\/)tsc$/.test(path) && bytes.length <= limits.apiTextBytes ? { utf8Text: utf8(bytes).slice(0, 16384), textIsComplete: utf8(bytes).length <= 16384 } : {}) } : {}) }; });
  const markerNames = ['createSourceFile', 'createProgram', 'getTypeChecker', 'typescript.exe', 'spawn', 'parseExpression', 'exports.parse', 'export function parse', 'parse(', 'getBinaryPath'];
  const apiMarkers = [];
  for (const [path, bytes] of archive.files) {
    if (!/\.(?:[cm]?js|d\.ts|ts)$/.test(path) || bytes.length > limits.apiTextBytes) continue;
    const text = utf8(bytes); const observations = [];
    for (const marker of markerNames) { const index = text.indexOf(marker); if (index >= 0) observations.push({ marker, offsetUtf16: index, line: text.slice(0, index).split('\n').length, excerpt: text.slice(Math.max(0, index - 160), index + marker.length + 320) }); }
    if (observations.length) apiMarkers.push({ path, bytes: bytes.length, sha256: hash(bytes), observations });
  }
  apiMarkers.sort(sortPath);
  return {
    name: row.name, version: row.lockEntry.version, sourceLockPath: row.sourceLockPath, unchangedLockEntry: row.lockEntry,
    acquisition: { archive: archiveRecord, metadata: metadataRecord },
    registry: { name: metadata.name, version: metadata.version, license: metadata.license, repository: metadata.repository ?? null, gitHeadPresent: Object.hasOwn(metadata, 'gitHead'), ...(Object.hasOwn(metadata, 'gitHead') ? { gitHeadDeclaration: metadata.gitHead } : {}), dist: metadata.dist, bodyBytes: metadataBytes.length, bodySha256: hash(metadataBytes), rawUtf8: utf8(metadataBytes), signatureVerified: false, attestationVerified: false },
    archive: { bytes: archiveBytes.length, sha256: hash(archiveBytes), sha1: hash(archiveBytes, 'sha1'), integrity, expandedBytes: archive.expandedBytes, regularBytes: archive.regularBytes, regularFiles: archive.files.size, directories: archive.directories, metadataHeaders: archive.metadataHeaders, memberCount: archive.memberCount, filesFingerprint: hash(bytesJson(fileRows)), files: fileRows },
    manifest: { bytes: manifestBytes.length, sha256: hash(manifestBytes), rawUtf8: utf8(manifestBytes), declarationFields },
    lifecycle: { scriptsPresent: Object.hasOwn(manifest, 'scripts'), scripts: manifest.scripts ?? null, installationKeys: ['preinstall', 'install', 'postinstall', 'prepare'].filter(key => Object.hasOwn(manifest.scripts ?? {}, key)), executed: false },
    packagedNotices: notices, packagedRootNoticePresent: notices.some(item => !item.path.includes('/')),
    entries: entryFiles, inertApiTextMarkers: apiMarkers,
    interpretationBoundary: 'Manifest/file bytes and inert lexical markers only; no imported compiler/parser API, semantic resolution, binary execution, package install or legal approval'
  };
}
function preflight() {
  assert.equal(hash(regular(recipePath)), recipeHash, 'Frozen recipe changed'); assert.equal(hash(regular(parserPath)), parserHash, 'Raw JSON dependency changed');
  const recipe = checkedJson(regular(recipePath)); assert.equal(recipe.source.commit, 'd86654abb8862e201933517d6f1fce9f88dd117f');
  assert.equal(process.version, 'v24.19.0'); assert.equal(process.execArgv.length, 0, 'Runtime startup options not admitted'); assert.equal(hash(regular(process.execPath)), runtimeHash, 'Runtime executable changed');
  const rows = selectedPaths.map(path => Object.values(recipe.packages).flat().find(item => item.sourceLockPath === path)); assert(rows.every(Boolean));
  assert.equal(new Set(rows.map(row => row.name)).size, 6); for (const row of rows) { assert(new URL(row.lockEntry.resolved).origin === 'https://registry.npmjs.org'); assert(new URL(row.versionMetadataUrl).origin === 'https://registry.npmjs.org'); }
  return { recipe, rows };
}
function request(url, maximum, kind) {
  const target = new URL(url); assert(target.origin === 'https://registry.npmjs.org' && !target.username && !target.password && !target.search && !target.hash, 'Unapproved HTTPS endpoint');
  const startedAt = new Date().toISOString(), requestHeaders = { 'User-Agent': 'PiSharp-inert-AST-archive-inspection/1', Accept: kind === 'metadata' ? 'application/json' : 'application/octet-stream', 'Accept-Encoding': 'identity' };
  return new Promise((accept, reject) => {
    const agent = new Agent({ keepAlive: false, rejectUnauthorized: true });
    const req = get(target, { agent, headers: requestHeaders }, response => {
      if (response.statusCode !== 200) { response.resume(); agent.destroy(); reject(new Error('Anonymous HTTPS read failed: ' + url + ' status ' + response.statusCode)); return; }
      const contentLength = response.headers['content-length']; if (contentLength !== undefined && (!/^[0-9]+$/.test(contentLength) || Number(contentLength) > maximum)) { response.destroy(); agent.destroy(); reject(new Error('Response Content-Length limit: ' + url)); return; }
      const chunks = []; let size = 0;
      response.on('data', chunk => { size += chunk.length; if (size > maximum) response.destroy(new Error('Response byte limit: ' + url)); else chunks.push(chunk); });
      response.on('error', error => { agent.destroy(); reject(error); });
      response.on('end', () => { agent.destroy(); const bytes = Buffer.concat(chunks); if (contentLength !== undefined && bytes.length !== Number(contentLength)) { reject(new Error('Response length mismatch')); return; } accept({ bytes, record: { method: 'GET', url, status: 200, redirectsFollowed: 0, startedAt, completedAt: new Date().toISOString(), requestHeaders, responseRawHeaders: response.rawHeaders, bytes: bytes.length, sha256: hash(bytes), authorizationHeaderSent: false, inheritedProxyUsed: false } }); });
    });
    req.setTimeout(limits.responseTimeoutMs, () => req.destroy(new Error('Anonymous HTTPS timeout: ' + url)));
    req.on('error', error => {
      agent.destroy(); const details = { name: error.name, code: error.code ?? null, message: error.message, errors: error.errors?.map(item => ({ code: item.code ?? null, message: item.message })) ?? [] };
      reject(new Error('Anonymous HTTPS read failed: ' + url + ': ' + JSON.stringify(details), { cause: error }));
    });
  });
}
function artifactNames(row) { return { archive: 'archives/' + row.lockEntry.resolved.slice(row.lockEntry.resolved.lastIndexOf('/') + 1), metadata: 'metadata/' + row.name.replaceAll('/', '+').replace(/^@/, '') + '-' + row.lockEntry.version + '.json' }; }
function createAdmission(acquisition, rawRoot, rows) {
  const summaries = rows.map(row => {
    const names = artifactNames(row), recorded = acquisition.packages.find(item => item.sourceLockPath === row.sourceLockPath); assert(recorded);
    const archiveBytes = regular(confined(rawRoot, join(rawRoot, names.archive))), metadataBytes = regular(confined(rawRoot, join(rawRoot, names.metadata)));
    for (const [bytes, record] of [[archiveBytes, recorded.archive], [metadataBytes, recorded.metadata]]) { assert.equal(bytes.length, record.bytes); assert.equal(hash(bytes), record.sha256); }
    return packageSummary(row, archiveBytes, metadataBytes, recorded.archive, recorded.metadata);
  });
  return {
    schemaVersion: 1, kind: 'exact-official-AST-archives-inert-admission', sourceSha: 'd86654abb8862e201933517d6f1fce9f88dd117f',
    status: 'six SRI-verified official archives inspected inertly; no install/parser/compiler/native execution or semantic approval',
    owner: { model: 'gpt-6.1-sol', reasoningEffort: 'xhigh' },
    recipe: { path: 'compatibility/ast-inventory-setup.json', sha256: recipeHash },
    harness: { path: 'tools/SurfaceInventory/inspect-ast-archives.mjs', sha256: hash(regular(ownPath)), dependencies: [{ path: 'tools/CompatibilityReport/raw-json.mjs', sha256: parserHash }] },
    runtime: { version: process.version, sha256: runtimeHash, profile: { platform: process.platform, architecture: process.arch }, newCompilerOrPackageExecution: false },
    limits, rawEvidence: { defaultRelativeRoot: 'artifacts/ast-archive-inspection', acquisitionReceipt: { path: 'acquisition.json', bytes: regular(join(rawRoot, 'acquisition.json')).length, sha256: hash(regular(join(rawRoot, 'acquisition.json'))) } },
    packages: summaries,
    summary: { archives: summaries.length, metadataReads: summaries.length, archiveBytes: summaries.reduce((n, item) => n + item.archive.bytes, 0), regularFiles: summaries.reduce((n, item) => n + item.archive.regularFiles, 0), packagedNotices: summaries.reduce((n, item) => n + item.packagedNotices.length, 0), archiveIntegrityMatches: true, sourceLockManifestDependencyMatches: true, packageInstallation: false, archiveExtraction: false, compilerOrParserImport: false, nativeExecution: false, subprocesses: false, providerCalls: false },
    gates: { ASTSyntaxQualification: 'HOLD', semanticTransitiveClosure: 'HOLD', mandatoryProfilesUnchanged: true, redistributionLegalApproval: false, nativeSdkAbiApproval: false, allEightPhaseAcceptance: false }
  };
}
function checkRawTree(rawRoot, rows) {
  const expected = new Set(['acquisition.json', ...rows.flatMap(row => Object.values(artifactNames(row)))]); const actual = [];
  const visit = (directory, depth) => { assert(depth <= 3); assert(!lstatSync(directory).isSymbolicLink()); for (const name of readdirSync(directory).sort()) { const path = confined(rawRoot, join(directory, name)), info = lstatSync(path); assert(!info.isSymbolicLink()); if (info.isDirectory()) visit(path, depth + 1); else { assert(info.isFile()); actual.push(relative(rawRoot, path).split(sep).join('/')); } } };
  visit(rawRoot, 0); assert.deepEqual(actual.sort(), [...expected].sort(), 'Unexpected/missing raw evidence file');
}
async function acquire(rows) {
  assert(!existsSync(defaultRawRoot), 'Refuse existing raw acquisition root'); assert(!existsSync(admissionPath), 'Refuse existing admission record');
  // Inherited values never become request headers/proxy/client credentials.
  for (const key of Object.keys(process.env)) if (!['SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'TZ'].includes(key)) delete process.env[key];
  const payloads = []; let total = 0;
  for (const row of rows) {
    const metadata = await request(row.versionMetadataUrl, limits.jsonBytes, 'metadata');
    const parsed = checkedJson(metadata.bytes); assert.equal(parsed.name, row.name); assert.equal(parsed.version, row.lockEntry.version); assert.equal(parsed.dist.integrity, row.lockEntry.integrity); assert.equal(parsed.dist.tarball, row.lockEntry.resolved);
    const archive = await request(row.lockEntry.resolved, limits.compressedBytes, 'archive');
    assert.equal('sha512-' + hash(archive.bytes, 'sha512', 'base64'), row.lockEntry.integrity, 'Source-locked SRI failed');
    total += archive.bytes.length; assert(total <= limits.totalCompressedBytes, 'Total acquisition byte limit');
    payloads.push({ row, metadata, archive }); console.log(JSON.stringify({ acquired: row.name + '@' + row.lockEntry.version, archiveBytes: archive.bytes.length, sha256: hash(archive.bytes), integrityMatched: true }));
  }
  // All downloaded bytes are preserved before tar/API admission. An inspection
  // failure keeps raw forensic evidence; no existing bytes are ever rewritten.
  assert(!existsSync(defaultRawRoot)); mkdirSync(defaultRawRoot);
  const acquisition = { schemaVersion: 1, kind: 'six-exact-official-AST-artifact-acquisition', recipeSha256: recipeHash, harnessSha256: hash(regular(ownPath)), runtimeSha256: runtimeHash, packages: [] };
  for (const { row, metadata, archive } of payloads) { const names = artifactNames(row); writeNew(confined(defaultRawRoot, join(defaultRawRoot, names.archive)), archive.bytes); writeNew(confined(defaultRawRoot, join(defaultRawRoot, names.metadata)), metadata.bytes); acquisition.packages.push({ sourceLockPath: row.sourceLockPath, name: row.name, version: row.lockEntry.version, archive: { path: names.archive, ...archive.record }, metadata: { path: names.metadata, ...metadata.record } }); }
  writeNew(join(defaultRawRoot, 'acquisition.json'), bytesJson(acquisition)); checkRawTree(defaultRawRoot, rows);
  const admission = createAdmission(acquisition, defaultRawRoot, rows); writeNew(admissionPath, bytesJson(admission));
  return admission;
}
function selfTest() {
  const tarFor = entries => { const parts = []; for (const entry of entries) { const bytes = Buffer.from(entry.text ?? 'x'), h = Buffer.alloc(512); h.write(entry.path); h.write('0000644\0', 100); h.write('0000000\0', 108); h.write('0000000\0', 116); h.write(bytes.length.toString(8).padStart(11, '0') + '\0', 124); h.fill(32, 148, 156); h.write(entry.type ?? '0', 156); h.write('ustar\0', 257); h.write('00', 263); const sum = h.reduce((a, b) => a + b, 0); h.write(sum.toString(8).padStart(6, '0') + '\0 ', 148); parts.push(h, bytes, Buffer.alloc((512 - bytes.length % 512) % 512)); } return gzipSync(Buffer.concat([...parts, Buffer.alloc(1024)])); };
  const valid = tarFor([{ path: 'package/a.js', text: 'export const x=1;' }]); assert.equal(inspectArchive(valid).files.size, 1);
  const probes = [
    ['absolute', tarFor([{ path: '/package/a' }])], ['traversal', tarFor([{ path: 'package/../a' }])], ['backslash', tarFor([{ path: 'package\\a' }])],
    ['duplicate', tarFor([{ path: 'package/a' }, { path: 'package/a' }])], ['case collision', tarFor([{ path: 'package/A' }, { path: 'package/a' }])],
    ['file parent', tarFor([{ path: 'package/a' }, { path: 'package/a/b' }])], ['symlink', tarFor([{ path: 'package/a', type: '2' }])], ['hardlink', tarFor([{ path: 'package/a', type: '1' }])],
    ['device', tarFor([{ path: 'package/a', type: '3' }])], ['reserved Windows', tarFor([{ path: 'package/CON.txt' }])], ['trailing dot', tarFor([{ path: 'package/a.' }])],
    ['compressed limit', valid, { ...limits, compressedBytes: 1 }], ['expanded limit', valid, { ...limits, expandedBytes: 512 }], ['member limit', valid, { ...limits, memberBytes: 1 }],
    ['count limit', valid, { ...limits, members: 0 }], ['depth limit', valid, { ...limits, depth: 1 }], ['path byte limit', valid, { ...limits, pathUtf8Bytes: 1 }], ['component byte limit', valid, { ...limits, componentUtf8Bytes: 1 }]
  ];
  for (const [name, bytes, caps] of probes) assert.throws(() => inspectArchive(bytes, caps ?? limits), name);
  assert.throws(() => utf8(Buffer.from([0xc3, 0x28])), 'Invalid UTF-8'); assert.throws(() => checkedJson(Buffer.from('{"x":1,"x":2}')), 'Duplicate JSON property');
  console.log(JSON.stringify({ kind: 'inert-archive-parser-pure-tests', passed: probes.length + 3, mutationRejections: probes.length + 2, packageOrCompilerExecution: false }));
}
export async function main(args = process.argv.slice(2)) {
  if (args.length === 1 && args[0] === '--self-test') { selfTest(); return; }
  const acquireNew = args.length === 1 && args[0] === '--acquire';
  const checkOverride = args.length === 2 && args[0] === '--raw-root';
  assert(acquireNew || args.length === 0 || checkOverride, 'Only --acquire, --raw-root PATH or --self-test accepted');
  const { rows } = preflight();
  let admission;
  if (acquireNew) admission = await acquire(rows);
  else {
    const rawRoot = checkOverride ? resolve(args[1]) : defaultRawRoot; checkRawTree(rawRoot, rows);
    const acquisition = checkedJson(regular(join(rawRoot, 'acquisition.json'))); assert.equal(acquisition.recipeSha256, recipeHash); assert.equal(acquisition.harnessSha256, hash(regular(ownPath))); assert.equal(acquisition.runtimeSha256, runtimeHash);
    const expectedBytes = regular(admissionPath); admission = createAdmission(acquisition, rawRoot, rows); assert(bytesJson(admission).equals(expectedBytes), 'Frozen admission differs from inert reinspection; refusing rewrite');
  }
  console.log(JSON.stringify({ ...admission.summary, admissionSha256: hash(regular(admissionPath)), gates: admission.gates }, null, 2));
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === ownPath.toLowerCase()) main().catch(error => { console.error(error.message); process.exitCode = 1; });
