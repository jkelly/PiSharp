// NEW profile; reuse hash-pinned read-only mechanics without changing old profiles.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const profileFiles = ['compatibility/semantic-full-project-catalog.plan.json', 'tools/SemanticInventory/full-project-catalog-common.mjs', 'tools/SemanticInventory/full-project-catalog-preload.mjs', 'tools/SemanticInventory/full-project-catalog-driver.mjs', 'tools/SemanticInventory/run-full-project-catalog.mjs', 'tools/SemanticInventory/full-project-catalog.test.mjs', 'docs/compatibility/semantic-full-project-catalog.md'];
const sharedPin = { path: 'tools/SemanticInventory/full-project-typebox-common.mjs', bytes: 18430, sha256: 'df5b99d591731d56607d4d8eb505bfbdb25581d50a2652c2dbd90cc05dc00eba' };
const sharedPath = join(repo, sharedPin.path); for (let path = resolve(sharedPath);;) { assert(!lstatSync(path).isSymbolicLink(), 'Shared mechanic link rejected'); const parent = dirname(path); if (parent === path) break; path = parent; } const sharedBytes = readFileSync(sharedPath); assert.equal(sharedBytes.length, sharedPin.bytes); assert.equal(createHash('sha256').update(sharedBytes).digest('hex'), sharedPin.sha256, 'Reviewed shared source changed before import');
const shared = await import(pathToFileURL(sharedPath).href);
export const { hash, jsonBytes, order, noLinks, contained, regular, writeNew, environment, snapshot, createReadOnlyFS, flattenEntrypoints, globRegex, bindingCandidates, resolveLock, assertDiagnosticFamilies, compareCounts } = shared;
export const diagnosticMethods = shared.diagnosticMethods;
export function assertProfilePlan(plan) { assert.deepEqual(plan.diagnosticMethods, diagnosticMethods); assert.equal(plan.receipt.payloadFiles, 4581); assert.equal(plan.receipt.sourceFiles, 2093); assert.equal(plan.receipt.catalogFiles, 43); assert.equal(plan.receipt.catalogBytes, 902551); assert.equal(plan.receipt.compilerFiles, 529); assert.equal(plan.receipt.previousNodePackageFiles, 114); assert.equal(plan.receipt.previousNodeDeclarations, 108); assert.equal(plan.receipt.typeboxPackageFiles, 1385); assert.equal(plan.receipt.typeboxDeclarations, 691); assert.equal(plan.receipt.leafPackageFiles, 416); assert.equal(plan.receipt.leafDeclarations, 129); assert.equal(plan.receipt.allAddedPackageFiles, 1915); assert.equal(plan.receipt.allAdmittedDeclarations, 928); assert.equal(plan.executionBoundary.runtimeModulePrefix, 'node_modules/typescript/'); assert.equal(plan.native.path, 'node_modules/@typescript/typescript-win32-x64/lib/tsc.exe'); assert.equal(plan.semanticPublicClosure, false); assert.deepEqual(plan.phaseGatesPassed, []); assert.deepEqual(plan.sharedProfilePins, [sharedPin]); }
export function readPlan() { const plan = JSON.parse(regular(join(repo, 'compatibility/semantic-full-project-catalog.plan.json')).toString('utf8')); assertProfilePlan(plan); return plan; }
export function pins() { return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order); }
export function admitPaths(oracle, scratch, plan = readPlan()) { assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only pinned read-only oracle admitted'); assert(typeof scratch === 'string' && isAbsolute(scratch), 'Scratch must be explicitly absolute'); for (const other of [oracle, ...plan.protectedOracleRoots, repo]) assert(!contained(other, scratch, true) && !contained(scratch, other, true), 'Scratch overlaps immutable oracle/repository'); noLinks(oracle); noLinks(scratch); }
export function parseArgs(args, plan = readPlan()) { let oracle = plan.oracle, scratch, mode = 'verify'; const seen = new Set(); for (let i = 0; i < args.length; i++) { const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg); if (arg === '--oracle' || arg === '--scratch' || arg === '--run-root') { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing value'); if (arg === '--oracle') oracle = args[++i]; else { assert(scratch === undefined, 'Conflicting output-root aliases'); scratch = args[++i]; } } else if (arg === '--check' || arg === '--capture-new') { assert.equal(mode, 'verify', 'Conflicting mode'); mode = arg; } else assert.fail('Unknown argument'); } assert(scratch, 'Explicit caller-owned --run-root required'); admitPaths(oracle, scratch, plan); return { oracle: resolve(oracle), scratch: resolve(scratch), runRoot: resolve(scratch), mode }; }
export function readBaseline(plan = readPlan()) { for (const pin of plan.baseline.pins) { const bytes = regular(join(repo, pin.path), plan.limits.snapshotBytes); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Genuine leaf baseline changed'); } const bytes = regular(join(repo, plan.baseline.pins[0].path), plan.limits.snapshotBytes); assert.equal(hash(bytes), plan.baseline.snapshotSha256); return JSON.parse(bytes.toString('utf8')); }
export function mandatoryLockInventory(baseline, plan) { const rows = baseline.mandatoryBaselineLockInstances; assert(Array.isArray(rows)); assert.equal(rows.length, plan.baseline.mandatoryNearestLockInstances, 'All original34 mandatory instances retained'); assert.equal(new Set(rows.map(row => row.lockPath)).size, 34); for (const row of rows) { assert(typeof row.lockPath === 'string' && row.lockEntry && typeof row.lockEntry.version === 'string'); assert(Array.isArray(row.parsedSpecifiers)); } assert.equal(rows.reduce((sum, row) => sum + row.parsedSpecifiers.length, 0), plan.baseline.originalMandatorySpecifierGroups, 'All original43 specifier groups retained'); return snapshot(rows, plan.limits, 'mandatoryBaselineLockInstances'); }


export function classifyPackageTarget(row, sourcePaths, artifact) {
  const target = row.target, original = { field: row.field, target, subpath: row.subpath, conditions: row.conditions };
  const unresolved = reason => ({ ...original, targetSyntax: reason, canonicalTargetPresent: undefined, publishedTarget: { state: 'unresolved-target-syntax' }, sourceOwnershipVerified: false, mandatoryMappingOrOwnershipBlocker: true, missingPublishedTargetRemainsBlocker: true, inferredDistToSourceMapping: false });
  if (target === null) return unresolved(row.field === 'exports' ? 'explicit-null-export-denial' : 'unsupported-null-field-target');
  if (typeof target !== 'string' || !target) return unresolved('unsupported-non-string-or-empty-target');
  if (row.field === 'exports' && !target.startsWith('./')) return unresolved('invalid-exports-relative-target');
  if (!['main', 'bin', 'source', 'types', 'typings', 'exports'].includes(row.field)) return unresolved('unsupported-field-semantics');
  if (/[\\:\x00-\x1f\x7f?"<>|%]/.test(target) || target.startsWith('/')) return unresolved('unconfined-or-unsupported-path-target');
  const literal = target.startsWith('./') ? target.slice(2) : target, components = literal.split('/');
  if (!literal || components.some(part => !part || part === '.' || part === '..' || /[. ]$/.test(part) || /^(?:con|prn|aux|nul|com[1-9\u00b9\u00b2\u00b3]|lpt[1-9\u00b9\u00b2\u00b3])(?:\.|$)/i.test(part))) return unresolved('unconfined-or-platform-unsupported-component');
  if (literal.split('*').length > 2) return unresolved('unsupported-multiple-wildcard-target');
  const wildcard = literal.includes('*'), canonicalTarget = dirname(row.manifestPath).replaceAll('\\', '/') + '/' + literal;
  const matchingSourceTargets = wildcard ? sourcePaths.filter(path => globRegex(canonicalTarget).test(path)) : sourcePaths.includes(canonicalTarget) ? [canonicalTarget] : [];
  let publishedTarget;
  if (!artifact?.archive) publishedTarget = { state: artifact ? 'artifact-not-acquired' : 'metadata-not-acquired', metadata: artifact?.registry };
  else {
    const matching = artifact.files.filter(file => wildcard ? globRegex(literal).test(file.path) : file.path === literal);
    publishedTarget = { state: matching.length ? 'acquired-member-present' : 'acquired-member-absent', archive: artifact.archive, matchingMembers: matching };
  }
  const missingPublishedTargetRemainsBlocker = publishedTarget.state !== 'acquired-member-present';
  return { ...original, targetSyntax: 'confined-package-relative-' + row.field, canonicalTarget, literalPackageRelativeTarget: literal, wildcard, matchingSourceTargets, canonicalTargetPresent: matchingSourceTargets.length > 0, publishedTarget, missingPublishedTargetRemainsBlocker, sourceOwnershipVerified: false, mandatoryMappingOrOwnershipBlocker: true, inferredDistToSourceMapping: false };
}
export function assertRequiredPublishedBlockers(rows, plan) {
  for (const required of plan.requiredPublishedOwnershipBlockers) { const matching = rows.filter(row => row.manifestPath === required.manifestPath && row.field === required.field && row.subpath === required.subpath && row.target === required.target); assert.equal(matching.length, 1, 'All three literal public target rows required'); assert.equal(matching[0].mandatoryMappingOrOwnershipBlocker, true); assert.equal(matching[0].sourceOwnershipVerified, false); assert.equal(matching[0].inferredDistToSourceMapping, false); }
}
export function assertOriginalRoots(actualRoots, originalRoots, actualSourceRoot, originalSourceRoot) {
  const key = (root, path) => { const suffix = relative(root, path); assert(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Project root outside canonical source'); return suffix.split(sep).join('/'); };
  const actual = new Set(actualRoots.map(path => key(actualSourceRoot, path))); for (const path of originalRoots) assert(actual.has(key(originalSourceRoot, path)), 'Original root omitted from actual unchanged-config project');
  return { originalRoots: originalRoots.length, actualRoots: actualRoots.length, allOriginalRootsPresent: true, validationUsesSourceRelativeIdentityOnly: true, capturedAbsolutePathsUnchanged: true };
}
export async function readPublishedArtifacts(plan = readPlan()) {
  for (const pin of plan.publicArtifactEvidence.pins) { const bytes = regular(join(repo, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Published artifact evidence changed'); }
  const inspection = JSON.parse(regular(join(repo, 'artifacts/released-baseline/inspection.json')).toString('utf8'));
  const npm = JSON.parse(regular(join(repo, 'artifacts/released-npm-ai/inspection.json')).toString('utf8'));
  const lock = JSON.parse(regular(join(repo, 'artifacts/released-npm-ai/evidence.lock.json')).toString('utf8'));
  for (const row of lock.files) { const bytes = regular(join(repo, 'artifacts/released-npm-ai', row.path)); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); }
  const compressed = regular(join(repo, 'artifacts/released-npm-ai/pi-ai-0.99.1.tgz')); assert.equal(hash(compressed), npm.package.sha256); assert.equal('sha512-' + createHash('sha512').update(compressed).digest('base64'), npm.package.integrity); assert.equal(createHash('sha1').update(compressed).digest('hex'), npm.package.shasum);
  const parser = await import(pathToFileURL(join(repo, 'tools/SemanticInventory/node-declaration-archive.mjs')).href), setup = await import(pathToFileURL(join(repo, plan.catalogSetup.helper)).href);
  const parsed = parser.inspectArchive(compressed, setup.readPlan().limits, 'package');
  const actualFiles = [...parsed.files].map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes) })).sort(order);
  assert.deepEqual(actualFiles, npm.packagedFiles.slice().sort(order)); assert.equal(actualFiles.length, 813);
  return inspection.publicNpmVersionMetadata.map(registry => registry.name === npm.package.name ? { name: registry.name, version: registry.version, registry, archive: npm.package, files: actualFiles, packageRuntimeExecuted: false, signatureVerified: false, attestationVerified: false } : { name: registry.name, version: registry.version, registry, packageRuntimeExecuted: false, artifactNotAcquired: true });
}
export async function verifyOracle(oracle, plan = readPlan()) {
  assert.equal(process.version, plan.runtime.version); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase()); assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase()); noLinks(oracle);
  assert.equal(plan.receipt.actualAdmissionPinPending, false, 'Actual root catalog receipt must be bound before execution'); assert(/^[a-f0-9]{64}$/.test(plan.receipt.sha256)); assert(Number.isSafeInteger(plan.receipt.bytes));
  for (const pin of plan.catalogSetup.pins) { const bytes = regular(join(repo, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Frozen catalog setup changed'); }
  const admission = await import(pathToFileURL(join(repo, plan.catalogSetup.helper)).href), setupPlan = admission.readPlan(); assert.equal(resolve(setupPlan.oracle).toLowerCase(), resolve(oracle).toLowerCase()); assert.deepEqual(setupPlan.origin, plan.origin);
  const data = await admission.verifyCatalogOracle(oracle, setupPlan); assert.equal(data.receiptSha256, plan.receipt.sha256); assert.equal(regular(join(oracle, plan.receipt.path)).length, plan.receipt.bytes);
  for (const pin of plan.metadataPins) { const bytes = regular(join(oracle, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256); }
  assert.equal(data.receipt.files.length, 4581); assert.equal(data.receipt.sourceArtifact.catalogFiles.length, 43); assert.equal(data.receipt.sourceArtifact.selectedPayloadBytes, 902551); assert.equal(data.receipt.canonicalFingerprint, plan.receipt.canonicalFingerprint);
  const originalSourceRows = data.origin.originalSourceRows, catalogRows = setupPlan.catalog.files.map(row => ({ ...row, path: 'upstream/' + row.path })), packages = data.origin.packages, addedFiles = data.origin.addedFiles;
  assert.equal(originalSourceRows.length, 2093); assert.equal(addedFiles.length, 1915); assert.equal(catalogRows.length, 43);
  for (const pin of plan.sourcePins) assert.equal(hash(regular(join(oracle, pin.path))), pin.sha256); assert.equal(hash(regular(join(oracle, plan.native.path))), plan.native.sha256); assert.equal(hash(regular(join(repo, plan.publicEntrypointPlan.path))), plan.publicEntrypointPlan.sha256); readBaseline(plan);
  const publishedArtifacts = await readPublishedArtifacts(plan);
  const verificationRepoPins = [...new Map([...plan.sharedProfilePins, ...plan.catalogSetup.pins, ...setupPlan.dependencies, ...setupPlan.release.pins, ...plan.publicArtifactEvidence.pins, ...data.origin.verificationRepoPins].map(row => [row.path, row])).values()].sort(order);
  return { receipt: data.receipt, receiptSha256: data.receiptSha256, originalSourceRows, catalogRows, packages, addedFiles, publishedArtifacts, verificationRepoPins };
}
