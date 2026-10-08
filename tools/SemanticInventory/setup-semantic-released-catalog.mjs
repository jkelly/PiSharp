// Stage D: strict inert release-source data admission. Import performs no IO.
// Parser lineage: node-declaration-archive.mjs baee47b8…633ffd; exact release root only.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const profileFiles = ['compatibility/semantic-released-catalog.plan.json', 'tools/SemanticInventory/setup-semantic-released-catalog.mjs', 'tools/SemanticInventory/semantic-released-catalog.test.mjs', 'docs/compatibility/semantic-released-catalog-setup.md'];
export const ownerName = '.pisharp-released-catalog-owner.json', preparedName = '.pisharp-released-catalog-prepared.json', startedName = '.pisharp-released-catalog-admission-started.json', receiptName = '.pisharp-released-catalog-admitted.json';
export const planHash = '9a14334508d418ed0bdc46f38fa31f368390a017f31c4ea49250965d58b6d3a0';
export const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
export const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
export const order = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
export function noLinks(target) { for (let current = resolve(target);;) { let stat; try { stat = lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; } assert(!stat?.isSymbolicLink(), 'Link/junction rejected'); const parent = dirname(current); if (parent === current) break; current = parent; } }
export function confined(root, target, allowRoot = false) { const suffix = relative(resolve(root), resolve(target)); assert((allowRoot || suffix !== '') && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Path escapes owned root'); return resolve(target); }
export function regular(path, maximum = 33554432) { noLinks(path); const stat = lstatSync(path); assert(stat.isFile() && stat.size <= maximum, 'Regular-file bounds'); return readFileSync(path); }
export function writeNew(root, path, bytes) { confined(root, path); noLinks(path); assert(!existsSync(path), 'Existing/uncertain evidence preserved'); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes, { flag: 'wx' }); }
export function assertPlan(plan) {
  assert.equal(plan.origin.payloadFiles, 4538); assert.equal(plan.origin.sourceFiles, 2093); assert.equal(plan.catalog.files.length, 43);
  assert.equal(plan.catalog.files.reduce((sum, row) => sum + row.bytes, 0), 902551); assert.equal(new Set(plan.catalog.files.map(row => row.path.toLowerCase())).size, 43);
  for (const row of plan.catalog.files) { assert(/^packages\/ai\/src\/providers\/data\/[^/]+\.json$/.test(row.path)); assert(Number.isSafeInteger(row.bytes) && row.bytes > 0); assert(/^[a-f0-9]{64}$/.test(row.sha256)); }
  assert.equal(plan.catalog.manifest.path, 'packages/ai/src/providers/data/.manifest.json'); assert.equal(plan.catalog.manifest.generatedAt, '2026-09-29T18:07:44.679Z');
  assert.equal(plan.release.root, 'pi-0.99.1'); assert.equal(plan.release.regularFiles, 2136); assert.equal(plan.release.directories, 249); assert.equal(plan.release.metadataHeaders, 0);
  assert.deepEqual(plan.release.attributeConversions, ['pi-test.bat', 'pi-test.ps1']); assert.equal(plan.policy.wholePayloadFiles, 4581); assert.equal(plan.policy.addedPayloadFiles, 43);
  assert.deepEqual(plan.release.recordedSourceIdentities.map(row => row.path), plan.release.attributeConversions); for (const row of plan.release.recordedSourceIdentities) { assert(/^[a-f0-9]{40}$/.test(row.gitBlob)); assert(/^[a-f0-9]{64}$/.test(row.sha256)); assert(/^[a-f0-9]{64}$/.test(row.publisherArchiveAndPriorCheckout.sha256)); assert(row.publisherArchiveAndPriorCheckout.bytes > row.bytes); }
  for (const key of ['network', 'npm', 'packageRuntimeImported', 'compilerImported', 'nativeExecuted', 'binLinks', 'uncertainRetryAllowed', 'diagnosticReductionPromised']) assert.equal(plan.policy[key], false);
  assert.equal(plan.baseline.mandatoryLockInstances, 34); assert.equal(plan.baseline.entrypointConditions, 150); assert.equal(plan.semanticPublicClosure, false); assert.deepEqual(plan.phaseGatesPassed, []);
}
export function readPlan() { const bytes = regular(join(repo, profileFiles[0])); assert.equal(hash(bytes), planHash, 'Frozen reviewed plan changed'); const plan = JSON.parse(utf8(bytes)); assertPlan(plan); return plan; }
export function pins() { return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order); }
export function parseArgs(args, plan = readPlan()) { let mode = '--check', root = plan.oracle; const seen = new Set(); for (let i = 0; i < args.length; i++) { const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg); if (['--check', '--prepare', '--admit'].includes(arg)) { assert(![...seen].some(other => other !== arg && ['--check', '--prepare', '--admit'].includes(other)), 'Conflicting modes'); mode = arg; } else if (arg === '--oracle') { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing oracle'); root = args[++i]; } else assert.fail('Unknown argument'); } assert(isAbsolute(root)); assert.equal(resolve(root).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only exact fresh catalog oracle admitted'); for (const other of [plan.origin.root, repo]) { const a = relative(resolve(other), resolve(root)), b = relative(resolve(root), resolve(other)); assert(a && b && (a === '..' || a.startsWith('..' + sep) || isAbsolute(a)) && (b === '..' || b.startsWith('..' + sep) || isAbsolute(b)), 'Owned oracle overlaps immutable input'); } noLinks(root); return { mode, root: resolve(root) }; }
export function inventory(root, caps) { const files = []; let total = 0; noLinks(root); const visit = (directory, depth) => { assert(depth <= caps.depth); for (const name of readdirSync(directory).sort()) { const path = confined(root, join(directory, name)), stat = lstatSync(path); assert(!stat.isSymbolicLink(), 'Payload link rejected'); if (stat.isDirectory()) visit(path, depth + 1); else { const bytes = regular(path, caps.originFileBytes); total += bytes.length; assert(total <= caps.originTotalBytes); files.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); assert(files.length <= caps.members); } } }; visit(root, 0); return files.sort(order); }
function payload(root, caps) { const packageBytes = regular(join(root, 'package.json')); return [...inventory(join(root, 'upstream'), caps).map(row => ({ ...row, path: 'upstream/' + row.path })), ...inventory(join(root, 'node_modules'), caps).map(row => ({ ...row, path: 'node_modules/' + row.path })), { path: 'package.json', bytes: packageBytes.length, sha256: hash(packageBytes) }].sort(order); }
export function verifyBaseline(value, plan) { assert.deepEqual(Object.fromEntries(Object.entries(value.diagnostics).map(([key, rows]) => [key, rows.length])), plan.baseline.diagnosticCounts); assert.equal(value.rootFiles.length, 1661); assert.equal(value.sourceFiles.length, 2400); assert.equal(value.unresolved.length, 711); assert.equal(value.mandatoryBaselineLockInstances.length, 34); assert.equal(value.mandatoryBaselineLockInstances.reduce((n, row) => n + row.parsedSpecifiers.length, 0), 43); assert.equal(value.entrypoints.length, 150); return value; }

const utf8 = bytes => new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
function safeMember(name, directory, caps, root) {
  assert(!/[\\\0]/.test(name) && !name.startsWith('/') && !/^[A-Za-z]:/.test(name), 'Unsafe tar path');
  assert(Buffer.byteLength(name) <= caps.pathUtf8Bytes, 'Tar path byte limit');
  const value = directory && name.endsWith('/') ? name.slice(0, -1) : name;
  const parts = value.split('/'); assert(parts.length <= caps.depth, 'Tar path depth limit');
  for (const part of parts) {
    assert(part && part !== '.' && part !== '..' && !/[<>:"|?*\x00-\x1f\x7f]/.test(part) && !/[. ]$/.test(part), 'Unsafe tar component');
    assert(Buffer.byteLength(part) <= caps.componentUtf8Bytes, 'Tar component byte limit');
    assert(!/^(?:con|prn|aux|nul|com[1-9\u00b9\u00b2\u00b3]|lpt[1-9\u00b9\u00b2\u00b3])(?:\.|$)/i.test(part), 'Reserved Windows tar component');
  }
  assert(value === root ? directory : value.startsWith(root + '/'), 'Tar outside exact publisher root');
  return value === root ? '' : value.slice(root.length + 1);
}
export function inspectReleaseArchive(compressed, caps, root = 'pi-0.99.1') {
  assert(root === 'pi-0.99.1', 'Unadmitted publisher root');
  assert(compressed.length > 0 && compressed.length <= caps.compressedBytes, 'Compressed archive byte limit');
  const tar = gunzipSync(compressed, { maxOutputLength: caps.expandedBytes });
  assert(tar.length <= caps.expandedBytes && tar.length % 512 === 0, 'Expanded tar byte/alignment limit');
  const files = new Map(), seen = new Map(); let offset = 0, memberCount = 0, directories = 0, metadataHeaders = 0, localPax = null, publisherTimeHeaders = 0;
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
      return { files, publisherRoot: root, memberCount, directories, metadataHeaders, publisherTimeHeaders, expandedBytes: tar.length, regularBytes: [...files.values()].reduce((sum, data) => sum + data.length, 0) };
    }
    assert(++memberCount <= caps.members, 'Tar member count limit');
    const sum = header.reduce((total, byte, index) => total + (index >= 148 && index < 156 ? 32 : byte), 0); assert.equal(sum, octal(header, 148, 8), 'Tar checksum');
    const size = octal(header, 124, 12); assert(size <= caps.memberBytes, 'Tar member byte limit');
    const start = offset + 512, end = start + size, next = start + Math.ceil(size / 512) * 512;
    assert(end <= tar.length && next <= tar.length, 'Truncated tar member');
    assert(tar.subarray(end, next).every(b => b === 0), 'Nonzero tar member padding');
    const type = field(header, 156, 1) || '0'; offset = next;
    if (type === 'x') { assert(localPax === null, 'Consecutive PAX headers'); safeMember(field(header, 0, 100), false, caps, root); localPax = pax(tar.subarray(start, end)); metadataHeaders++; continue; }
    assert(type === '0' || type === '5', 'Tar link/device/global-PAX/extension rejected');
    assert(field(header, 157, 100) === '', 'Tar link target on regular member');
    const prefix = field(header, 345, 155);
    const leaf = field(header, 0, 100);
    const originalName = prefix ? prefix + '/' + leaf : leaf; safeMember(originalName, type === '5', caps, root);
    const name = safeMember(localPax?.path ?? originalName, type === '5', caps, root); localPax = null;
    const key = name.toLowerCase(); assert(!seen.has(key), 'Duplicate or Windows case-colliding tar path'); seen.set(key, type === '0' ? 'file' : 'directory');
    if (type === '5') { assert.equal(size, 0, 'Directory member data'); directories++; }
    else { assert(name, 'Empty regular member path'); files.set(name, tar.subarray(start, end)); }
  }
  throw new Error('Missing tar terminal blocks');
}



export function inspectCatalog(compressed, plan, origin, parseJsonSupported, validateRawJson) {
  assert.equal(typeof validateRawJson, 'function', 'Lossless raw JSON validator required');
  const archivePin = plan.release.pins.find(row => row.path.endsWith('/pi-0.99.1-source.tar.gz')); assert.equal(compressed.length, archivePin.bytes); assert.equal(hash(compressed), archivePin.sha256);
  const parsed = inspectReleaseArchive(compressed, plan.limits, plan.release.root); assert(parsed.expandedBytes <= compressed.length * plan.limits.compressionRatio); assert.equal(parsed.files.size, 2136); assert.equal(parsed.directories, 249); assert.equal(parsed.metadataHeaders, 0);
  const original = origin.originalSourceRows, trackedNames = new Set(original.map(row => row.path.slice('upstream/'.length))); assert.equal(trackedNames.size, 2093);
  const additions = [...parsed.files.keys()].filter(path => !trackedNames.has(path)).sort(); assert.deepEqual(additions, plan.catalog.files.map(row => row.path).sort(), 'Every release addition must be the exact selected catalog data');
  const comparisons = []; for (const row of original) {
    const path = row.path.slice('upstream/'.length), archived = parsed.files.get(path); assert(archived, 'Released original source member absent');
    assert.equal(archived.length, row.bytes, 'Raw publisher/prior checkout byte count differs'); assert.equal(hash(archived), row.sha256, 'Raw publisher/prior checkout hash differs');
    const identity = plan.release.recordedSourceIdentities.find(item => item.path === path); let canonicalGit;
    if (identity) {
      assert.deepEqual({ bytes: archived.length, sha256: hash(archived) }, identity.publisherArchiveAndPriorCheckout, 'Exact two published CRLF byte pins required');
      const temporaryGitComparison = Buffer.from(utf8(archived).replaceAll('\r\n', '\n'));
      assert(Buffer.from(utf8(temporaryGitComparison).replaceAll('\n', '\r\n')).equals(archived), 'Two recorded CRLF identities differ');
      assert.equal(temporaryGitComparison.length, identity.bytes); assert.equal(hash(temporaryGitComparison), identity.sha256);
      assert.equal(hash(Buffer.concat([Buffer.from('blob ' + temporaryGitComparison.length + '\0'), temporaryGitComparison]), 'sha1'), identity.gitBlob);
      canonicalGit = { bytes: identity.bytes, sha256: identity.sha256, gitBlob: identity.gitBlob, mode: identity.mode };
    }
    comparisons.push({ path, archiveBytes: archived.length, archiveSha256: hash(archived), checkoutBytes: row.bytes, checkoutSha256: row.sha256, canonicalGit, recordedGitToCheckoutConversion: Boolean(identity), rawPublisherEqualsPriorCheckout: true, oldPayloadRewritten: false });
  }
  const selected = new Map(); for (const row of plan.catalog.files) { const bytes = parsed.files.get(row.path); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); validateRawJson(utf8(bytes)); selected.set(row.path, bytes); }
  const manifest = parseJsonSupported(utf8(selected.get(plan.catalog.manifest.path))); assert.equal(manifest.schemaVersion, plan.catalog.manifest.schemaVersion); assert.equal(manifest.generatedAt, plan.catalog.manifest.generatedAt); assert.equal(manifest.structureHash, plan.catalog.manifest.structureHash); const providers = plan.catalog.files.filter(row => row.path !== plan.catalog.manifest.path); assert.equal(providers.length, 42); assert.deepEqual(Object.keys(manifest.files).sort(), providers.map(row => row.path.split('/').at(-1)).sort()); for (const row of providers) assert.equal(manifest.files[row.path.split('/').at(-1)], row.sha256);
  const license = parsed.files.get(plan.release.sourceLicense.path); assert.equal(license.length, plan.release.sourceLicense.bytes); assert.equal(hash(license), plan.release.sourceLicense.sha256);
  return { selected, evidence: { sourceCommit: plan.sourceCommit, archive: { url: plan.release.archiveUrl, bytes: compressed.length, sha256: hash(compressed), expandedBytes: parsed.expandedBytes, memberCount: parsed.memberCount, regularFiles: parsed.files.size, directories: parsed.directories, metadataHeaders: parsed.metadataHeaders }, originalSourceComparisons: comparisons, sourceLicense: { ...plan.release.sourceLicense, utf8Text: utf8(license) }, catalogFiles: plan.catalog.files, publisherManifest: { ...plan.catalog.manifest, rawUtf8: utf8(selected.get(plan.catalog.manifest.path)) }, selectedPayloadBytes: 902551, selectedMembers: 43, allOtherArchiveMembersNotExtracted: true, sourcePublisherManifestRetained: true, npmManifestSubstituted: false, reconstructed: false, licensingClosure: false } };
}
export async function context(plan = readPlan()) {
  for (const pin of [...plan.dependencies, ...plan.release.pins, ...plan.baseline.pins]) { const bytes = regular(join(repo, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Read-only qualified evidence changed'); }
  const verifier = await import(pathToFileURL(join(repo, plan.origin.verifier)).href), raw = await import(pathToFileURL(join(repo, 'tools/CompatibilityReport/raw-json.mjs')).href);
  const origin = await verifier.verifyOracle(plan.origin.root); assert.equal(origin.receiptSha256, plan.origin.receiptSha256); assert.equal(origin.receipt.files.length, 4538); const originReceipt = regular(join(plan.origin.root, plan.origin.receipt)); assert.equal(originReceipt.length, plan.origin.receiptBytes); assert.equal(hash(originReceipt), plan.origin.receiptSha256);
  for (const pin of plan.origin.metadataPins) { const bytes = regular(join(plan.origin.root, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256); }
  const gitEvidence = plan.release.canonicalGitEvidence, gitReceiptBytes = regular(join(gitEvidence.root, gitEvidence.receipt)); assert.equal(gitReceiptBytes.length, gitEvidence.bytes); assert.equal(hash(gitReceiptBytes), gitEvidence.sha256);
  const gitReceipt = raw.parseJsonSupported(utf8(gitReceiptBytes)); assert.equal(gitReceipt.sourceCommit, plan.sourceCommit); assert.equal(gitReceipt.canonicalFingerprint, plan.origin.canonicalFingerprint);
  assert.deepEqual(gitReceipt.canonicalRows.filter(row => plan.release.attributeConversions.includes(row.path)), plan.release.recordedSourceIdentities.map(({ publisherArchiveAndPriorCheckout, relationship, ...row }) => row));
  const baseline = raw.parseJsonSupported(utf8(regular(join(repo, plan.baseline.pins[0].path)))); verifyBaseline(baseline, plan);
  const lock = raw.parseJsonSupported(utf8(regular(join(repo, 'artifacts/released-baseline/evidence.lock.json')))); assert.equal(lock.sourceSha, plan.sourceCommit); for (const row of lock.files) { const pin = plan.release.pins.find(pin => pin.path === 'artifacts/released-baseline/' + row.path); assert.deepEqual(pin && { path: row.path, bytes: pin.bytes, sha256: pin.sha256 }, row, 'Every acquired release lock member retained'); }
  const inspection = raw.parseJsonSupported(utf8(regular(join(repo, 'artifacts/released-baseline/inspection.json')))); assert.equal(inspection.sourceSha, plan.sourceCommit); assert.deepEqual(inspection.sourceComparison.addedRegularFiles, plan.catalog.files); const sums = utf8(regular(join(repo, 'artifacts/released-baseline/SHA256SUMS'))); assert(sums.split(/\r?\n/).includes(plan.release.archiveSha256 + '  pi-0.99.1-source.tar.gz'));
  const compressed = regular(join(repo, 'artifacts/released-baseline/pi-0.99.1-source.tar.gz')); const inspected = inspectCatalog(compressed, plan, origin, raw.parseJsonSupported, raw.canonicalRawJson);
  const retained = [{ path: 'archives/source-leaves-admitted.json', bytes: originReceipt }, { path: 'archives/source-original-semantic-receipt.json', bytes: gitReceiptBytes }, ...plan.release.pins.map(pin => ({ path: 'archives/released-baseline/' + pin.path.split('/').at(-1), bytes: regular(join(repo, pin.path)) }))];
  return { origin, inspected, retained, baseline, parseJsonSupported: raw.parseJsonSupported };
}
export function admissionRows(originReceipt, catalogFiles) { assert.equal(originReceipt.files.length, 4538); const files = [...originReceipt.files, ...catalogFiles.map(row => ({ ...row, path: 'upstream/' + row.path }))].sort(order); assert.equal(files.length, 4581); assert.equal(new Set(files.map(row => row.path.toLowerCase())).size, 4581, 'Duplicate/case-colliding payload'); return files; }
function ownerRecord(root, plan, harness) { return { schemaVersion: 1, owner: 'PiSharp-released-catalog-inert-stage-D-v1', oracle: root, planSha256: planHash, originReceiptSha256: plan.origin.receiptSha256, harness }; }
function preparedRecord(plan, data, owner) { return { schemaVersion: 1, ownerSha256: hash(jsonBytes(owner)), originReceiptSha256: plan.origin.receiptSha256, sourceCommit: plan.sourceCommit, copiedFiles: data.origin.receipt.files, retainedEvidence: data.retained.map(row => ({ path: row.path, bytes: row.bytes.length, sha256: hash(row.bytes) })), copiedPayloadFiles: 4538, packageOrCompilerExecuted: false }; }
function startedRecord(prepared, plan) { return { schemaVersion: 1, preparedSha256: hash(jsonBytes(prepared)), sourceArchiveSha256: plan.release.archiveSha256, selectedMembers: plan.catalog.files, uncertainRetryAllowed: false }; }
export function admissionRecord(plan, data, prepared, files) { return { schemaVersion: 1, owner: 'PiSharp-released-catalog-inert-stage-D-v1', sourceCommit: plan.sourceCommit, canonicalFingerprint: plan.origin.canonicalFingerprint, planSha256: planHash, originReceiptSha256: plan.origin.receiptSha256, preparedSha256: hash(jsonBytes(prepared)), sourceArtifact: data.inspected.evidence, files, originalPayloadFiles: 4538, addedPayloadFiles: 43, wholePayloadFiles: 4581, newDeclarationFiles: 0, actualLeafBaselineSha256: plan.baseline.snapshotSha256, network: false, npm: false, packageCodeImported: false, compilerImported: false, nativeExecuted: false, binLinks: false, semanticPublicClosure: false, phaseGatesPassed: [] }; }
export function verifyState(root, plan, data, harness, admitted) {
  assert.deepEqual(readdirSync(root).sort(), ['archives', 'node_modules', 'upstream', 'package.json', ownerName, preparedName, ...(admitted ? [startedName, receiptName] : [])].sort(), 'Unknown/uncertain oracle state preserved');
  const owner = ownerRecord(root, plan, harness), prepared = preparedRecord(plan, data, owner); assert(regular(join(root, ownerName)).equals(jsonBytes(owner))); assert(regular(join(root, preparedName)).equals(jsonBytes(prepared)));
  assert.deepEqual(inventory(join(root, 'archives'), plan.limits), data.retained.map(row => ({ path: row.path.slice('archives/'.length), bytes: row.bytes.length, sha256: hash(row.bytes) })).sort(order), 'Every retained release/source receipt byte required');
  const files = admitted ? admissionRows(data.origin.receipt, plan.catalog.files) : data.origin.receipt.files; assert.deepEqual(payload(root, plan.limits), files, 'Old/new source/compiler/data payload differs');
  if (!admitted) return { owner, prepared };
  assert(regular(join(root, startedName)).equals(jsonBytes(startedRecord(prepared, plan)))); const expected = admissionRecord(plan, data, prepared, files), bytes = regular(join(root, receiptName)); assert(bytes.equals(jsonBytes(expected)), 'Complete source-data admission receipt differs');
  return { receipt: expected, receiptSha256: hash(bytes), wholePayloadFiles: 4581, addedPayloadFiles: 43 };
}
function assertRuntime(plan) { assert.equal(process.version, plan.runtime.version); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase()); assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); assert.equal(process.execArgv.length, 0, 'Startup/preload overrides rejected'); }
export async function verifyCatalogOracle(root, plan = readPlan()) { const data = await context(plan); parseArgs(['--check', '--oracle', root], plan); const state = verifyState(resolve(root), plan, data, pins(), true); return { ...data, ...state }; }
export async function main(args = process.argv.slice(2)) {
  const plan = readPlan(), { mode, root } = parseArgs(args, plan); assertRuntime(plan); const harness = pins(), data = await context(plan);
  if (mode === '--check') { const state = existsSync(root) ? verifyState(root, plan, data, harness, existsSync(join(root, receiptName))) : 'fresh root absent; original4538 plus exact43 source data verified read-only'; console.log(JSON.stringify({ state: typeof state === 'string' ? state : { receiptSha256: state.receiptSha256, wholePayloadFiles: state.wholePayloadFiles }, nativeExecuted: false, network: false })); return; }
  if (mode === '--prepare') { assert(!existsSync(root), 'Existing/uncertain oracle preserved'); mkdirSync(root); mkdirSync(join(root, 'archives')); const owner = ownerRecord(root, plan, harness); writeNew(root, join(root, ownerName), jsonBytes(owner)); let total = 0; for (const row of data.origin.receipt.files) { const bytes = regular(confined(plan.origin.root, join(plan.origin.root, row.path))); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); total += bytes.length; assert(total <= plan.limits.originTotalBytes); writeNew(root, join(root, row.path), bytes); } for (const row of data.retained) writeNew(root, join(root, row.path), row.bytes); assert.deepEqual(payload(root, plan.limits), data.origin.receipt.files); await context(plan); assert.deepEqual(pins(), harness); writeNew(root, join(root, preparedName), jsonBytes(preparedRecord(plan, data, owner))); verifyState(root, plan, data, harness, false); console.log(JSON.stringify({ status: 'original4538 copied exclusively; exact release evidence retained; no catalog extraction yet', nativeExecuted: false })); return; }
  assert(existsSync(root)); assert(!existsSync(join(root, startedName)) && !existsSync(join(root, receiptName)), 'Existing/uncertain admission preserved; no retry'); const prepared = verifyState(root, plan, data, harness, false).prepared, files = admissionRows(data.origin.receipt, plan.catalog.files);
  for (const row of plan.catalog.files) assert(!existsSync(join(root, 'upstream', row.path)), 'Selected source data target exists');
  await context(plan); assert.deepEqual(pins(), harness); writeNew(root, join(root, startedName), jsonBytes(startedRecord(prepared, plan)));
  for (const [path, bytes] of data.inspected.selected) writeNew(root, join(root, 'upstream', path), bytes);
  assert.deepEqual(payload(root, plan.limits), files); await context(plan); assert.deepEqual(pins(), harness); writeNew(root, join(root, receiptName), jsonBytes(admissionRecord(plan, data, prepared, files)));
  const state = verifyState(root, plan, data, harness, true); console.log(JSON.stringify({ status: '43 unchanged publisher catalog files admitted inert', receiptSha256: state.receiptSha256, wholePayloadFiles: 4581, nativeExecuted: false, semanticPublicClosure: false }));
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase()) main().catch(error => { console.error(error.stack); process.exitCode = 1; });
