// Independent NEW whole-config profile. Reuse reviewed immutable mechanics by hash.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const profileFiles = ['compatibility/semantic-full-project-openai.plan.json', 'tools/SemanticReference/full-project-openai-common.mjs', 'tools/SemanticReference/full-project-openai-preload.mjs', 'tools/SemanticReference/full-project-openai-driver.mjs', 'tools/SemanticReference/run-full-project-openai.mjs', 'tools/SemanticReference/full-project-openai.test.mjs', 'docs/compatibility/semantic-full-project-openai.md'];
export const planHash = '3401b676e50b7546a4b3179e76282a4f8e1309a107e8d6efad173924cf9db3eb';
const mechanicPin = { path: 'tools/SemanticInventory/full-project-catalog-common.mjs', bytes: 15169, sha256: '1c8fb8f5c386e4b7ff9f91a419afea3cb9086952d095b1bbad3380d234daa44c' };
const mechanicPath = join(repo, mechanicPin.path);
for (let path = resolve(mechanicPath);;) { assert(!lstatSync(path).isSymbolicLink(), 'Reviewed mechanic link rejected'); const parent = dirname(path); if (parent === path) break; path = parent; }
const mechanicBytes = readFileSync(mechanicPath); assert.equal(mechanicBytes.length, mechanicPin.bytes); assert.equal(createHash('sha256').update(mechanicBytes).digest('hex'), mechanicPin.sha256, 'Reviewed mechanic changed before import');
const shared = await import(pathToFileURL(mechanicPath).href);
export const { hash, jsonBytes, order, noLinks, contained, regular, writeNew, environment, snapshot, createReadOnlyFS, flattenEntrypoints, globRegex, bindingCandidates, resolveLock, assertDiagnosticFamilies, compareCounts, classifyPackageTarget, assertRequiredPublishedBlockers, assertOriginalRoots } = shared;
export const diagnosticMethods = shared.diagnosticMethods;
function checkPin(root, pin, maximum = 33554432) {
  const path = join(root, pin.path); assert(contained(root, path)); const bytes = regular(path, maximum);
  assert.equal(bytes.length, pin.bytes, 'Frozen input size changed: ' + pin.path); assert.equal(hash(bytes), pin.sha256, 'Frozen input hash changed: ' + pin.path); return bytes;
}
export function assertProfilePlan(plan) {
  assert.deepEqual(plan.diagnosticMethods, diagnosticMethods);
  assert.equal(plan.receipt.actualAdmissionPinPending, false); assert.equal(plan.receipt.path, '.pisharp-public-triage-openai-admitted.json');
  assert.equal(plan.receipt.sha256, '1e7f61d543738e4c7ad81df5cacdf98c8d75190633f881fd3741a127c6d8a577'); assert.equal(plan.receipt.bytes, 2376214);
  assert.equal(plan.receipt.payloadFiles, 8129); assert.equal(plan.receipt.sourceFiles, 2093); assert.equal(plan.receipt.compilerFiles, 529);
  assert.equal(plan.receipt.catalogFiles, 43); assert.equal(plan.receipt.catalogBytes, 902551);
  assert.equal(plan.receipt.previousNodePackageFiles, 114); assert.equal(plan.receipt.previousNodeDeclarations, 108);
  assert.equal(plan.receipt.typeboxPackageFiles, 1385); assert.equal(plan.receipt.typeboxDeclarations, 691);
  assert.equal(plan.receipt.leafPackageFiles, 416); assert.equal(plan.receipt.leafDeclarations, 129);
  assert.equal(plan.receipt.openaiPackageFiles, 3548); assert.equal(plan.receipt.openaiDeclarations, 784);
  assert.equal(plan.receipt.allAddedPackageFiles, 5463); assert.equal(plan.receipt.allAdmittedDeclarations, 1712);
  assert.equal(plan.executionBoundary.runtimeModulePrefix, 'node_modules/typescript/'); assert.equal(plan.executionBoundary.vfsPayloadFiles, 8129);
  assert.equal(plan.native.path, 'node_modules/@typescript/typescript-win32-x64/lib/tsc.exe');
  assert.equal(plan.baseline.sourceFiles, 2443); assert.equal(plan.baseline.rootFiles, 1661); assert.equal(plan.baseline.unresolvedRecords, 711);
  assert.equal(plan.baseline.mandatoryNearestLockInstances, 34); assert.equal(plan.baseline.originalMandatorySpecifierGroups, 43);
  assert.equal(plan.baseline.entrypointConditionRecords, 150); assert.equal(plan.requiredPublishedOwnershipBlockers.length, 3);
  assert.equal(plan.openaiSetup.pins.length, 3); assert.equal(plan.predecessorProfilePins.length, 7);
  assert.deepEqual(plan.sdkDeclarationResolution.knownDeclarationImports, ['ws', 'zod/v3', 'undici', 'stream/web', '#x509-transport-state']);
  assert.deepEqual(plan.sdkDeclarationResolution.missingCanonicalPeerInstances, ['node_modules/@types/ws', 'node_modules/@smithy/hash-node']);
  assert.equal(plan.sdkDeclarationResolution.optionalPeersAreNotExemptions, true); assert.equal(plan.sdkDeclarationResolution.actualConditionSelectionPending, true);
  assert.equal(plan.sdkDeclarationResolution.addedPackageRuntimeExecuted, false); assert.equal(plan.sdkDeclarationResolution.sourceOrConfigChanges, false);
  assert.equal(plan.sdkDeclarationResolution.diagnosticReductionPromised, false); assert.equal(plan.sdkDeclarationResolution.structuralTypeInventoryExhaustive, false);
  assert.equal(plan.fullStructuralTypeInventoryPending, true); assert.equal(plan.semanticPublicClosure, false); assert.deepEqual(plan.phaseGatesPassed, []);
}
export function readPlan() {
  const bytes = regular(join(repo, profileFiles[0])); assert.equal(hash(bytes), planHash, 'Frozen reviewed OpenAI profile plan changed');
  const plan = JSON.parse(bytes.toString('utf8')); assertProfilePlan(plan); return plan;
}
export function pins() { return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order); }
export function admitPaths(oracle, scratch, plan = readPlan()) {
  assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only exact admitted read-only OpenAI successor permitted');
  assert(typeof scratch === 'string' && isAbsolute(scratch), 'Explicit absolute caller-owned run-root required');
  for (const other of [oracle, ...plan.protectedOracleRoots, repo]) assert(!contained(other, scratch, true) && !contained(scratch, other, true), 'Run-root overlaps immutable input/repository');
  noLinks(oracle); noLinks(scratch);
}
export function parseArgs(args, plan = readPlan()) {
  let oracle = plan.oracle, scratch, mode = 'verify'; const seen = new Set();
  for (let i = 0; i < args.length; i++) {
    const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg);
    if (['--oracle', '--scratch', '--run-root'].includes(arg)) { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing value'); if (arg === '--oracle') oracle = args[++i]; else { assert(scratch === undefined, 'Conflicting output-root aliases'); scratch = args[++i]; } }
    else if (arg === '--check' || arg === '--capture-new') { assert.equal(mode, 'verify', 'Conflicting mode'); mode = arg; }
    else assert.fail('Unknown argument');
  }
  assert(scratch, 'Explicit caller-owned --run-root required'); admitPaths(oracle, scratch, plan);
  return { oracle: resolve(oracle), scratch: resolve(scratch), runRoot: resolve(scratch), mode };
}
export function readBaseline(plan = readPlan()) {
  for (const pin of plan.baseline.pins) checkPin(repo, pin, plan.limits.snapshotBytes);
  const bytes = regular(join(repo, plan.baseline.pins[0].path), plan.limits.snapshotBytes); assert.equal(hash(bytes), plan.baseline.snapshotSha256);
  const value = JSON.parse(bytes.toString('utf8')); assertDiagnosticFamilies(value.diagnostics, diagnosticMethods);
  assert.deepEqual(Object.fromEntries(diagnosticMethods.map(method => [method, value.diagnostics[method].length])), plan.baseline.diagnosticCounts);
  assert.equal(value.unresolved.length, 711); assert.equal(value.rootFiles.length, 1661); assert.equal(value.sourceFiles.length, 2443);
  assert.equal(value.entrypoints.length, 150); assert.equal(value.requiredPublishedOwnershipBlockers.length, 3); assert.equal(value.semanticPublicClosure, false);
  return value;
}
export function mandatoryLockInventory(baseline, plan) { return shared.mandatoryLockInventory(baseline, plan); }
export async function readPublishedArtifacts(plan = readPlan()) {
  for (const pin of plan.catalogSetup.pins) checkPin(repo, pin);
  return shared.readPublishedArtifacts(plan);
}
export async function verifyOracle(oracle, plan = readPlan()) {
  assert.equal(process.version, plan.runtime.version); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase());
  assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase()); noLinks(oracle);
  assertProfilePlan(plan);
  for (const pin of [...plan.openaiSetup.pins, ...plan.predecessorProfilePins, ...plan.catalogSetup.pins]) checkPin(repo, pin);
  const admission = await import(pathToFileURL(join(repo, plan.openaiSetup.helper)).href), setupPlan = admission.readPlan();
  assert.equal(resolve(setupPlan.oracle).toLowerCase(), resolve(oracle).toLowerCase()); assert.deepEqual(setupPlan.origin, plan.origin);
  const data = await admission.verifyOpenaiOracle(oracle, setupPlan), receiptBytes = regular(join(oracle, plan.receipt.path));
  assert.equal(receiptBytes.length, plan.receipt.bytes); assert.equal(hash(receiptBytes), plan.receipt.sha256); assert.equal(data.receiptSha256, plan.receipt.sha256);
  for (const pin of plan.metadataPins) checkPin(oracle, pin);
  assert.equal(data.receipt.files.length, 8129); assert.equal(data.receipt.sdkArtifact.archive.files.length, 3548); assert.equal(data.receipt.newDeclarationFiles, 784);
  assert.equal(data.receipt.canonicalFingerprint, plan.receipt.canonicalFingerprint);
  // This verified object retains the catalog successor, then its complete leaf predecessor.
  const catalogData = data.origin, leafData = catalogData.origin;
  const originalSourceRows = leafData.originalSourceRows;
  const catalogRows = catalogData.receipt.sourceArtifact.catalogFiles.map(row => ({ ...row, path: 'upstream/' + row.path }));
  const sdkArtifact = data.receipt.sdkArtifact, packages = [...leafData.packages, sdkArtifact];
  const addedFiles = [...leafData.addedFiles, ...sdkArtifact.archive.files.map(row => ({ ...row, path: sdkArtifact.target + '/' + row.path }))];
  assert.equal(originalSourceRows.length, 2093); assert.equal(catalogRows.length, 43); assert.equal(addedFiles.length, 5463); assert.equal(packages.length, 11);
  for (const pin of plan.sourcePins) assert.equal(hash(regular(join(oracle, pin.path))), pin.sha256);
  assert.equal(hash(regular(join(oracle, plan.native.path))), plan.native.sha256);
  assert.equal(hash(regular(join(repo, plan.publicEntrypointPlan.path))), plan.publicEntrypointPlan.sha256); readBaseline(plan);
  const publishedArtifacts = await readPublishedArtifacts(plan);
  const verificationRepoPins = [...new Map([...plan.sharedProfilePins, ...plan.predecessorProfilePins, ...plan.openaiSetup.pins, ...plan.catalogSetup.pins, ...setupPlan.dependencies, ...setupPlan.baseline.pins, ...plan.publicArtifactEvidence.pins, ...leafData.verificationRepoPins].map(row => [row.path, row])).values()].sort(order);
  return { receipt: data.receipt, receiptSha256: data.receiptSha256, originalSourceRows, catalogRows, packages, addedFiles, publishedArtifacts, verificationRepoPins, sourceArtifact: catalogData.receipt.sourceArtifact, sdkArtifact };
}
