// Authored Stage E1a: exact one-package inert admission. No compiler/SDK execution.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const planHash = '8feaf1cdd0e6bb8285f87cf4be7517e5e741abf9b3101b6ef6030e28913082d7';
export const profileFiles = ['compatibility/semantic-public-triage-openai.plan.json', 'tools/SemanticReference/setup-semantic-public-triage-openai.mjs', 'tools/SemanticReference/semantic-public-triage-openai.test.mjs'];
export const ownerName = '.pisharp-public-triage-openai-owner.json';
export const preparedName = '.pisharp-public-triage-openai-prepared.json';
export const startedName = '.pisharp-public-triage-openai-admission-started.json';
export const receiptName = '.pisharp-public-triage-openai-admitted.json';
export const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
export const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
export const order = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
const utf8 = bytes => new TextDecoder('utf8', { fatal: true, ignoreBOM: true }).decode(bytes);

export function noLinks(target) {
  for (let current = resolve(target);;) {
    let stat; try { stat = lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    assert(!stat?.isSymbolicLink(), 'Link/junction rejected');
    const parent = dirname(current); if (parent === current) break; current = parent;
  }
}
export function confined(root, target, allowRoot = false) {
  const suffix = relative(resolve(root), resolve(target));
  assert((allowRoot || suffix !== '') && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Path escapes owned root');
  return resolve(target);
}
export function regular(path, maximum = 33554432) {
  noLinks(path); const stat = lstatSync(path);
  assert(stat.isFile() && stat.size >= 0 && stat.size <= maximum, 'Regular-file bounds');
  return readFileSync(path);
}
export function writeNew(root, path, bytes) {
  confined(root, path); noLinks(path); assert(!existsSync(path), 'Existing/uncertain evidence preserved');
  mkdirSync(dirname(path), { recursive: true }); noLinks(path); writeFileSync(path, bytes, { flag: 'wx' });
}
function checkPin(root, pin) {
  const bytes = regular(confined(root, join(root, pin.path)));
  assert.equal(bytes.length, pin.bytes, 'Frozen input size changed: ' + pin.path);
  assert.equal(hash(bytes), pin.sha256, 'Frozen input hash changed: ' + pin.path); return bytes;
}
export function assertPlan(plan) {
  assert.equal(plan.kind, 'authored-offline-exclusive-openai-inert-successor-setup');
  assert.equal(plan.sourceCommit, 'd86654abb8862e201933517d6f1fce9f88dd117f');
  assert.equal(plan.origin.payloadFiles, 4581); assert.equal(plan.origin.originalCanonicalSourceRows, 2093);
  assert.equal(plan.package.name, 'openai'); assert.equal(plan.package.version, '7.19.0');
  assert.equal(plan.package.lockPath, 'packages/ai/node_modules/openai');
  assert.equal(plan.package.target, 'upstream/' + plan.package.lockPath); assert.equal(plan.package.archiveRoot, 'package');
  assert.equal(plan.package.lockEntry.version, plan.package.version);
  assert.equal(plan.package.lockEntry.resolved, 'https://registry.npmjs.org/openai/-/openai-7.19.0.tgz');
  assert.equal(plan.package.lockEntry.integrity, 'sha512-MX2s3u2L5racTO0CC/SWpCOasJQBCJrqLKXK+l82cAhdeF8mPMBEe/gxMm0ZFa2xpKpOFLRjxv5afYEZbBXmbQ==');
  assert.equal(plan.archiveSource.archive.bytes, 2781465);
  assert.equal(plan.archiveSource.archive.sha256, '597532dcb9051ee8aa5edadedb57b798b42add68cc5aec1d67e994d0cfad07f1');
  assert.equal(plan.package.regularFiles, 3548); assert.equal(plan.package.declarationFiles, 784); assert.equal(plan.package.regularBytes, 18477473);
  assert.equal(plan.package.members.length, 3548); assert.equal(plan.package.members.reduce((n, row) => n + row.bytes, 0), 18477473);
  assert.equal(plan.package.members.filter(row => /\.d\.[cm]?ts$/.test(row.path)).length, 784);
  assert.equal(new Set(plan.package.members.map(row => row.path.toLowerCase())).size, 3548);
  for (const row of plan.package.members) {
    assert(row.path && !row.path.startsWith('/') && !/[\\\0:]/.test(row.path));
    assert(row.path.split('/').every(part => part && part !== '.' && part !== '..'));
    assert(Number.isSafeInteger(row.bytes) && row.bytes >= 0 && row.bytes <= plan.limits.memberBytes);
    assert(/^[a-f0-9]{64}$/.test(row.sha256));
  }
  assert.equal(plan.package.notices.length, 4);
  assert.deepEqual(plan.package.notices.map(row => row.path), ['LICENSE', 'src/_vendor/partial-json-parser/LICENSE', 'src/_vendor/zod-to-json-schema/LICENSE', 'src/internal/qs/LICENSE.md']);
  assert.deepEqual(plan.package.knownDeclarationImports, ['ws', 'zod/v3', 'undici', 'stream/web', '#x509-transport-state']);
  assert.deepEqual(plan.package.unresolvedPeerInstanceDecisionsRetained, ['node_modules/@types/ws', 'node_modules/@smithy/hash-node']);
  assert.equal(plan.package.peerDeclarationsRetained, true); assert.equal(plan.package.optionalPeersAreNotSemanticExemptions, true);
  assert.equal(plan.policy.originalPayloadFiles, 4581); assert.equal(plan.policy.addedPayloadFiles, 3548);
  assert.equal(plan.policy.addedDeclarations, 784); assert.equal(plan.policy.wholePayloadFiles, 8129);
  for (const key of ['network', 'npm', 'packageRuntimeImported', 'compilerImported', 'nativeExecuted', 'binLinks', 'lifecycleScripts', 'sourceOrConfigChanges', 'diagnosticReductionPromised', 'semanticClosurePromised', 'uncertainRetryAllowed']) assert.equal(plan.policy[key], false);
  assert.equal(plan.baseline.mandatoryLockInstances, 34); assert.equal(plan.baseline.originalMandatorySpecifierGroups, 43);
  assert.equal(plan.baseline.currentLiteralUnresolvedSpecifiers, 31); assert.equal(plan.baseline.entrypointConditions, 150);
  assert.equal(plan.baseline.publishedOwnershipBlockers, 3); assert.equal(plan.semanticPublicClosure, false); assert.deepEqual(plan.phaseGatesPassed, []);
}
export function readPlan() {
  const bytes = regular(join(repo, profileFiles[0])); assert.equal(hash(bytes), planHash, 'Frozen reviewed setup plan changed');
  const plan = JSON.parse(utf8(bytes)); assertPlan(plan); return plan;
}
export function pins() {
  return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order);
}
function disjoint(a, b) {
  const x = relative(resolve(a), resolve(b)).toLowerCase(), y = relative(resolve(b), resolve(a)).toLowerCase();
  const outside = value => value && (isAbsolute(value) || value === '..' || value.startsWith('..' + sep));
  assert(outside(x) && outside(y), 'Owned successor overlaps immutable input');
}
export function parseArgs(args, plan = readPlan()) {
  let mode = '--check', root = plan.oracle; const seen = new Set(), modes = ['--check', '--prepare', '--admit'];
  for (let i = 0; i < args.length; i++) {
    const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg);
    if (modes.includes(arg)) { assert(![...seen].some(other => other !== arg && modes.includes(other)), 'Conflicting modes'); mode = arg; }
    else if (arg === '--oracle') { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing oracle'); root = args[++i]; }
    else assert.fail('Unknown option');
  }
  assert(isAbsolute(root), 'Absolute owned successor required');
  assert.equal(resolve(root).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only exact fresh OpenAI successor admitted');
  for (const other of [repo, plan.origin.root, plan.archiveSource.root, ...plan.protectedOracleRoots]) disjoint(root, other);
  noLinks(root); return { mode, root: resolve(root) };
}
export function inventory(root, caps) {
  noLinks(root); const rows = []; let total = 0;
  const visit = (directory, depth) => {
    assert(depth <= caps.depth, 'Tree depth limit');
    for (const name of readdirSync(directory).sort()) {
      const path = confined(root, join(directory, name)), stat = lstatSync(path); assert(!stat.isSymbolicLink(), 'Payload link rejected');
      if (stat.isDirectory()) visit(path, depth + 1);
      else { const bytes = regular(path, caps.originFileBytes); total += bytes.length; assert(total <= caps.originTotalBytes, 'Tree byte limit'); rows.push({ path: relative(root, path).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); assert(rows.length <= caps.members, 'Tree member limit'); }
    }
  };
  visit(root, 0); return rows.sort(order);
}
function payload(root, caps) {
  const bytes = regular(join(root, 'package.json'));
  return [...inventory(join(root, 'upstream'), caps).map(row => ({ ...row, path: 'upstream/' + row.path })), ...inventory(join(root, 'node_modules'), caps).map(row => ({ ...row, path: 'node_modules/' + row.path })), { path: 'package.json', bytes: bytes.length, sha256: hash(bytes) }].sort(order);
}
export function verifyBaseline(value, plan) {
  assert.deepEqual(Object.keys(value.diagnostics), plan.baseline.diagnosticMethods);
  assert.deepEqual(Object.fromEntries(Object.entries(value.diagnostics).map(([key, rows]) => { assert(Array.isArray(rows)); return [key, rows.length]; })), plan.baseline.diagnosticCounts);
  assert.equal(value.rootFiles.length, 1661); assert.equal(value.mandatoryOriginalRootFiles.length, 1661); assert.equal(value.sourceFiles.length, 2443);
  assert.equal(value.unresolved.length, 711); assert.equal(value.mandatoryBaselineLockInstances.length, 34);
  assert.equal(value.mandatoryBaselineLockInstances.reduce((n, row) => n + row.parsedSpecifiers.length, 0), 43);
  assert.equal(new Set(value.unresolved.map(row => row.parsedSpecifier)).size, 31); assert.equal(value.entrypoints.length, 150);
  assert.equal(value.requiredPublishedOwnershipBlockers.length, 3); assert(value.requiredPublishedOwnershipBlockers.every(row => row.mandatoryMappingOrOwnershipBlocker === true));
  const groups = {};
  for (const row of value.unresolved) {
    assert.equal(row.method, 'getSemanticDiagnostics'); assert.deepEqual(row.value, value.diagnostics.getSemanticDiagnostics[row.index]);
    if (row.nearestLockCandidate?.lockPath === plan.package.lockPath) { assert.deepEqual(row.nearestLockCandidate.lockEntry, plan.package.lockEntry); groups[row.parsedSpecifier] = (groups[row.parsedSpecifier] ?? 0) + 1; }
  }
  assert.deepEqual(groups, plan.baseline.openaiSpecifierCounts);
  assert.equal(value.wholeConfigurationOpenedUnchanged, true); assert.equal(value.diagnosticFiltering, false); assert.equal(value.pathNormalization, false);
  assert.equal(value.semanticPublicClosure, false); assert.deepEqual(value.phaseGatesPassed, []); return value;
}
export function inspectOpenai(compressed, plan, inspectArchive, parseJsonSupported) {
  assert.equal(compressed.length, plan.archiveSource.archive.bytes); assert.equal(hash(compressed), plan.archiveSource.archive.sha256);
  assert.equal('sha512-' + hash(compressed, 'sha512', 'base64'), plan.package.lockEntry.integrity, 'Exact upstream SRI mismatch');
  const parsed = inspectArchive(compressed, plan.limits, plan.package.archiveRoot);
  assert(parsed.expandedBytes <= compressed.length * plan.limits.compressionRatio);
  assert.equal(parsed.files.size, 3548); assert.equal(parsed.directories, 0); assert.equal(parsed.metadataHeaders, 0); assert.equal(parsed.regularBytes, 18477473);
  const files = [...parsed.files].map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes) })).sort(order);
  assert.deepEqual(files, plan.package.members, 'Every exact official member is required');
  const manifestBytes = parsed.files.get('package.json'); assert(manifestBytes); assert.equal(manifestBytes.length, plan.package.manifest.bytes); assert.equal(hash(manifestBytes), plan.package.manifest.sha256);
  const manifest = parseJsonSupported(utf8(manifestBytes)); assert.equal(manifest.name, 'openai'); assert.equal(manifest.version, '7.19.0'); assert.equal(manifest.license, 'Apache-2.0');
  assert.equal(manifest.exports['.'].require.types, './index.d.ts'); assert.equal(manifest.exports['.'].types, './index.d.mts');
  assert.equal(manifest.exports['./resources/*.js'].default, './resources/*.js');
  assert.equal(manifest.imports['#x509-transport-state'].types, './src/internal/auth/x509-transport-state.cts');
  assert.equal(files.filter(row => /\.d\.[cm]?ts$/.test(row.path)).length, 784);
  const noticePaths = files.filter(row => /(?:^|\/)(?:LICENSE(?:\.[^/]+)?|NOTICE(?:\.[^/]+)?)$/i.test(row.path)).map(row => row.path);
  assert.deepEqual(noticePaths, plan.package.notices.map(row => row.path));
  const notices = plan.package.notices.map(pin => { const bytes = parsed.files.get(pin.path); assert(bytes && bytes.length <= plan.limits.noticeBytes); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256); return { ...pin, utf8Text: utf8(bytes) }; });
  return { parsed, evidence: { name: manifest.name, version: manifest.version, lockPath: plan.package.lockPath, lockEntry: plan.package.lockEntry, target: plan.package.target, archive: { ...plan.archiveSource.archive, url: plan.package.lockEntry.resolved, integrity: plan.package.lockEntry.integrity, publisherRoot: parsed.publisherRoot, memberCount: parsed.memberCount, expandedBytes: parsed.expandedBytes, regularBytes: parsed.regularBytes, files }, manifest: { ...plan.package.manifest, rawUtf8: utf8(manifestBytes) }, notices, declarationFiles: 784, scriptDeclarations: manifest.scripts ?? {}, binDeclarations: manifest.bin ?? {}, peerDependencies: manifest.peerDependencies ?? {}, peerDependenciesMeta: manifest.peerDependenciesMeta ?? {}, knownDeclarationImports: plan.package.knownDeclarationImports, unresolvedPeerInstanceDecisionsRetained: plan.package.unresolvedPeerInstanceDecisionsRetained, allArchiveMembersRetained: true, checkerConditionSelectionPending: true, sdkRuntimeExecuted: false, licensingClosure: false, semanticPublicClosure: false } };
}
export async function context(plan = readPlan()) {
  // Verify every approved local module before importing it. No package module is imported.
  for (const pin of [...plan.dependencies, ...plan.baseline.pins]) checkPin(repo, pin);
  const raw = await import(pathToFileURL(join(repo, 'tools/CompatibilityReport/raw-json.mjs')).href);
  const verifier = await import(pathToFileURL(join(repo, plan.origin.verifier)).href);
  const parser = await import(pathToFileURL(join(repo, 'tools/SemanticInventory/node-declaration-archive.mjs')).href);
  const origin = await verifier.verifyCatalogOracle(plan.origin.root);
  assert.equal(origin.receiptSha256, plan.origin.receiptSha256); assert.equal(origin.receipt.files.length, 4581); assert.equal(origin.receipt.canonicalFingerprint, plan.origin.canonicalFingerprint);
  const retained = [];
  for (const pin of plan.origin.metadataPins) retained.push({ path: 'archives/predecessor-root/' + pin.path, bytes: checkPin(plan.origin.root, pin) });
  assert.equal(regular(join(plan.origin.root, plan.origin.receipt)).length, plan.origin.receiptBytes);
  assert.deepEqual(inventory(join(plan.origin.root, 'archives'), plan.limits), plan.origin.archivedEvidence.map(row => ({ ...row, path: row.path.slice('archives/'.length) })));
  for (const pin of plan.origin.archivedEvidence) retained.push({ path: 'archives/predecessor/' + pin.path.slice('archives/'.length), bytes: checkPin(plan.origin.root, pin) });
  const baseline = verifyBaseline(raw.parseJsonSupported(utf8(checkPin(repo, plan.baseline.pins[0]))), plan);
  const lock = raw.parseJsonSupported(utf8(regular(join(plan.origin.root, 'upstream/package-lock.json')))); assert.deepEqual(lock.packages[plan.package.lockPath], plan.package.lockEntry);
  const source = plan.archiveSource;
  const compressed = checkPin(source.root, source.archive), inspectionBytes = checkPin(source.root, source.inspection), restoredBytes = checkPin(source.root, source.restoredReceipt);
  const inspected = inspectOpenai(compressed, plan, parser.inspectArchive, raw.parseJsonSupported);
  assert.deepEqual(inventory(join(source.root, source.installedPath), plan.limits), inspected.evidence.archive.files, 'Every prior installed SDK member must equal official archive bytes');
  const inspection = raw.parseJsonSupported(utf8(inspectionBytes));
  assert.equal(inspection.name, 'openai'); assert.equal(inspection.version, '7.19.0'); assert.equal(inspection.url, plan.package.lockEntry.resolved);
  assert.equal(inspection.sha256, source.archive.sha256); assert.equal(inspection.integrity, plan.package.lockEntry.integrity);
  assert.equal(inspection.files, 3548); assert.equal(inspection.unpackedBytes, 18477473); assert.deepEqual(inspection.fileFingerprint, plan.package.existingFingerprint);
  assert.deepEqual(inspection.licenses, inspected.evidence.notices);
  const restored = raw.parseJsonSupported(utf8(restoredBytes)), accepted = restored.installed.filter(row => row.name === 'openai');
  assert.equal(accepted.length, 1); assert.equal(accepted[0].version, '7.19.0'); assert.equal(accepted[0].archiveSha256, source.archive.sha256);
  assert.equal(accepted[0].manifestSha256, plan.package.manifest.sha256); assert.deepEqual(accepted[0].files, plan.package.existingFingerprint); assert.deepEqual(accepted[0].licenses, inspected.evidence.notices);
  retained.push({ path: 'archives/openai-7.19.0.tgz', bytes: compressed }, { path: 'archives/openai-7.19.0.inspection.json', bytes: inspectionBytes }, { path: 'archives/source-responses-sdk-restored.json', bytes: restoredBytes });
  return { origin, inspected, retained, baseline };
}
export function admissionRows(originReceipt, members, target = 'upstream/packages/ai/node_modules/openai') {
  assert.equal(originReceipt.files.length, 4581); assert.equal(members.length, 3548); assert.equal(target, 'upstream/packages/ai/node_modules/openai');
  const files = [...originReceipt.files, ...members.map(row => ({ ...row, path: target + '/' + row.path }))].sort(order);
  assert.equal(files.length, 8129); assert.equal(new Set(files.map(row => row.path.toLowerCase())).size, 8129, 'Duplicate/case-colliding payload'); return files;
}
function ownerRecord(root, plan, harness) {
  return { schemaVersion: 1, owner: 'PiSharp-public-triage-openai-inert-stage-E1a-v1', oracle: root, sourceCommit: plan.sourceCommit, planSha256: planHash, originReceiptSha256: plan.origin.receiptSha256, harness };
}
function preparedRecord(plan, data, owner) {
  return { schemaVersion: 1, ownerSha256: hash(jsonBytes(owner)), originReceiptSha256: plan.origin.receiptSha256, sourceCommit: plan.sourceCommit, copiedFiles: data.origin.receipt.files, retainedEvidence: data.retained.map(row => ({ path: row.path, bytes: row.bytes.length, sha256: hash(row.bytes) })).sort(order), copiedPayloadFiles: 4581, packageOrCompilerExecuted: false };
}
function startedRecord(prepared, plan) {
  return { schemaVersion: 1, preparedSha256: hash(jsonBytes(prepared)), archiveSha256: plan.archiveSource.archive.sha256, target: plan.package.target, selectedMembers: plan.package.members, uncertainRetryAllowed: false };
}
export function admissionRecord(plan, data, prepared, files) {
  return { schemaVersion: 1, owner: 'PiSharp-public-triage-openai-inert-stage-E1a-v1', sourceCommit: plan.sourceCommit, canonicalFingerprint: plan.origin.canonicalFingerprint, planSha256: planHash, originReceiptSha256: plan.origin.receiptSha256, preparedSha256: hash(jsonBytes(prepared)), sdkArtifact: data.inspected.evidence, files, originalPayloadFiles: 4581, addedPayloadFiles: 3548, wholePayloadFiles: 8129, newDeclarationFiles: 784, actualCatalogBaselineSha256: plan.baseline.pins[0].sha256, mandatoryOriginalLockInstances: data.baseline.mandatoryBaselineLockInstances, originalEntrypointConditions: 150, requiredPublishedOwnershipBlockers: data.baseline.requiredPublishedOwnershipBlockers, network: false, npm: false, packageCodeImported: false, compilerImported: false, nativeExecuted: false, binLinks: false, lifecycleScripts: false, diagnosticFiltering: false, pathNormalization: false, diagnosticReductionPromised: false, semanticPublicClosure: false, phaseGatesPassed: [] };
}
export function verifyState(root, plan, data, harness, admitted) {
  assert.deepEqual(readdirSync(root).sort(), ['archives', 'node_modules', 'upstream', 'package.json', ownerName, preparedName, ...(admitted ? [startedName, receiptName] : [])].sort(), 'Unknown/uncertain successor state preserved');
  const owner = ownerRecord(root, plan, harness), prepared = preparedRecord(plan, data, owner);
  assert(regular(join(root, ownerName)).equals(jsonBytes(owner)), 'Owner record differs'); assert(regular(join(root, preparedName)).equals(jsonBytes(prepared)), 'Prepared record differs');
  assert.deepEqual(inventory(join(root, 'archives'), plan.limits), data.retained.map(row => ({ path: row.path.slice('archives/'.length), bytes: row.bytes.length, sha256: hash(row.bytes) })).sort(order), 'Every predecessor/SDK evidence byte required');
  const files = admitted ? admissionRows(data.origin.receipt, plan.package.members, plan.package.target) : data.origin.receipt.files;
  assert.deepEqual(payload(root, plan.limits), files, 'Complete source/compiler/catalog/SDK payload differs');
  if (!admitted) return { owner, prepared };
  assert(regular(join(root, startedName)).equals(jsonBytes(startedRecord(prepared, plan))), 'Started record differs');
  const receipt = admissionRecord(plan, data, prepared, files), bytes = regular(join(root, receiptName)); assert(bytes.equals(jsonBytes(receipt)), 'Complete inert SDK receipt differs');
  return { owner, prepared, receipt, receiptSha256: hash(bytes), wholePayloadFiles: 8129, addedPayloadFiles: 3548 };
}
function assertRuntime(plan) {
  assert.equal(process.version, plan.runtime.version); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase());
  assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); assert.equal(process.execArgv.length, 0, 'Startup/preload overrides rejected');
  assert(!process.env.NODE_OPTIONS && !process.env.NODE_PATH, 'Ambient runtime injection rejected');
}
export async function verifyOpenaiOracle(root, plan = readPlan()) {
  parseArgs(['--check', '--oracle', root], plan); const data = await context(plan);
  return { ...data, ...verifyState(resolve(root), plan, data, pins(), true) };
}
export async function main(args = process.argv.slice(2)) {
  const plan = readPlan(), { mode, root } = parseArgs(args, plan); assertRuntime(plan);
  const harness = pins(), data = await context(plan);
  if (mode === '--check') {
    const state = existsSync(root) ? verifyState(root, plan, data, harness, existsSync(join(root, receiptName))) : 'fresh successor absent; complete4581 and exact3548 official SDK members verified read-only';
    console.log(JSON.stringify({ state: typeof state === 'string' ? state : { receiptSha256: state.receiptSha256, wholePayloadFiles: state.wholePayloadFiles }, nativeExecuted: false, network: false, semanticPublicClosure: false })); return;
  }
  if (mode === '--prepare') {
    assert(!existsSync(root), 'Existing/uncertain successor preserved'); mkdirSync(root); mkdirSync(join(root, 'archives'));
    const owner = ownerRecord(root, plan, harness); writeNew(root, join(root, ownerName), jsonBytes(owner));
    let total = 0;
    for (const row of data.origin.receipt.files) { const bytes = regular(confined(plan.origin.root, join(plan.origin.root, row.path))); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); total += bytes.length; assert(total <= plan.limits.originTotalBytes); writeNew(root, join(root, row.path), bytes); }
    for (const row of data.retained) writeNew(root, join(root, row.path), row.bytes);
    assert.deepEqual(payload(root, plan.limits), data.origin.receipt.files); await context(plan); assert.deepEqual(pins(), harness);
    writeNew(root, join(root, preparedName), jsonBytes(preparedRecord(plan, data, owner))); verifyState(root, plan, data, harness, false);
    console.log(JSON.stringify({ status: 'all4581 predecessor payloads copied exclusively; exact predecessor/SDK evidence retained; no SDK extraction yet', nativeExecuted: false })); return;
  }
  assert(existsSync(root)); assert(!existsSync(join(root, startedName)) && !existsSync(join(root, receiptName)), 'Existing/uncertain admission preserved; no retry');
  const prepared = verifyState(root, plan, data, harness, false).prepared, files = admissionRows(data.origin.receipt, plan.package.members, plan.package.target);
  assert(!existsSync(join(root, plan.package.target)), 'SDK target already exists'); await context(plan); assert.deepEqual(pins(), harness);
  writeNew(root, join(root, startedName), jsonBytes(startedRecord(prepared, plan)));
  for (const [path, bytes] of data.inspected.parsed.files) writeNew(root, join(root, plan.package.target, path), bytes);
  assert.deepEqual(payload(root, plan.limits), files); await context(plan); assert.deepEqual(pins(), harness);
  writeNew(root, join(root, receiptName), jsonBytes(admissionRecord(plan, data, prepared, files)));
  const state = verifyState(root, plan, data, harness, true);
  console.log(JSON.stringify({ status: '3548 unchanged official SDK members admitted inert at exact nearest-lock path', receiptSha256: state.receiptSha256, wholePayloadFiles: 8129, nativeExecuted: false, semanticPublicClosure: false, phaseGatesPassed: [] }));
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase()) main().catch(error => { console.error(error.stack); process.exitCode = 1; });
