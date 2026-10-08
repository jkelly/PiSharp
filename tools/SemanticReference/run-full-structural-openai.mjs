// Capture/verify writes exclusively to explicit fresh caller-selected scratch.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { repo, hash, regular, jsonBytes, readPlan, parseArgs, pins, environment, verifyOracle, writeNew, readBaseline, compareCounts, verifyProvenanceBundle, verifyProtocolEvidence } from './full-structural-openai-common.mjs';
import { observeOwnedChildClose } from './full-structural-openai-preload.mjs';

export function childArgs(repository = repo) { return ['--import', pathToFileURL(join(repository, 'tools/SemanticReference/full-structural-openai-preload.mjs')).href, join(repository, 'tools/SemanticReference/full-structural-openai-driver.mjs')]; }
export function assertFrozen(manifest, expected, harness, receiptSha256) { assert.deepEqual(manifest.harness, harness, 'Profile changed'); assert.equal(manifest.receiptSha256, receiptSha256); assert.equal(manifest.expected.bytes, expected.length); assert.equal(manifest.expected.sha256, hash(expected), 'Golden changed'); }
export async function observeDriverChild(spawnImpl, executable, args, options, limits, timers) {
  assert(Number.isSafeInteger(limits.snapshotBytes) && limits.snapshotBytes > 0); assert(Number.isSafeInteger(limits.driverTimeoutMs) && limits.driverTimeoutMs > 0);
  let child;
  try { child = spawnImpl(executable, args, options); }
  catch (error) { return { stdout: Buffer.alloc(0), stderr: Buffer.alloc(0), failure: { kind: 'synchronous-spawn-error', name: error.name, message: error.message, code: error.code, syscall: error.syscall, path: error.path }, lifecycle: { label: 'Node structural driver', spawned: false, ownedChildHandleReturned: false, exitObserved: false, closeObserved: false, events: [], watchdogs: [], terminationRequests: [] }, streams: { stdout: { observedBytes: 0, capturedBytes: 0, omittedBytes: 0 }, stderr: { observedBytes: 0, capturedBytes: 0, omittedBytes: 0 } } }; }
  const lifecycle = observeOwnedChildClose(child, 'Node structural driver', timers), chunks = { stdout: [], stderr: [] }, streams = { stdout: { observedBytes: 0, capturedBytes: 0, omittedBytes: 0 }, stderr: { observedBytes: 0, capturedBytes: 0, omittedBytes: 0 } }; let totalCaptured = 0, overflow = false;
  lifecycle.observation.ownedChildHandleReturned = true;
  for (const name of ['stdout', 'stderr']) {
    if (!child[name] || typeof child[name].on !== 'function') { lifecycle.noteFailure('missing-' + name + '-pipe', new Error('Owned driver ' + name + ' pipe required')); lifecycle.requestTermination('missing-' + name + '-pipe'); continue; }
    child[name].on('data', value => {
      const raw = Buffer.isBuffer(value) ? value : Buffer.from(value), row = streams[name], available = Math.max(0, limits.snapshotBytes - totalCaptured), prefix = raw.subarray(0, available); row.observedBytes += raw.length; row.capturedBytes += prefix.length; row.omittedBytes += raw.length - prefix.length; totalCaptured += prefix.length;
      if (prefix.length) chunks[name].push(Buffer.from(prefix));
      if (prefix.length !== raw.length && !overflow) { overflow = true; lifecycle.noteFailure('driver-output-overflow', new Error('Combined driver stdout/stderr exceeds admitted byte bound; remaining bytes drained but run failed')); lifecycle.requestTermination('driver-output-overflow'); }
    });
  }
  lifecycle.armDeadline('driver-deadline', limits.driverTimeoutMs);
  const joined = await lifecycle.wait();
  return { stdout: Buffer.concat(chunks.stdout), stderr: Buffer.concat(chunks.stderr), streams, lifecycle: joined.observation, failure: joined.firstFailure };
}
export function assertPhysicalNativeClose(provenance) {
  assert.equal(provenance.lifecycle?.exitAwaited, true, 'Native exit witness required'); assert.equal(provenance.lifecycle?.closeAwaited, true, 'Native physical close join required'); assert.equal(provenance.lifecycle?.physicalCloseObserved, true);
  assert.equal(provenance.nativeChildLifecycle?.closeObserved, true); assert.equal(provenance.nativeChildLifecycle?.exitObserved, true); assert.deepEqual(provenance.nativeChildLifecycle.exit, { code: 0, signal: null }); assert.deepEqual(provenance.nativeChildLifecycle.close, { code: 0, signal: null });
  assert.equal(provenance.nativeFirstFailure, undefined); assert.equal(provenance.nativeFailure, undefined); assert.equal(provenance.failure, undefined); assert.equal(provenance.protocol?.complete, true); assert.equal(provenance.protocol?.omittedRawBytes, 0);
}
async function childRun(oracle, scratch, plan, name) {
  const output = join(scratch, name + '.json'), env = { ...environment(scratch, plan), PISHARP_SEMANTIC_OPENAI_STRUCTURAL_CHILD: '1', PISHARP_SEMANTIC_OPENAI_STRUCTURAL_ORACLE: oracle, PISHARP_SEMANTIC_OPENAI_STRUCTURAL_SCRATCH: scratch, PISHARP_SEMANTIC_OPENAI_STRUCTURAL_OUTPUT: output };
  const observed = await observeDriverChild(spawn, plan.runtime.path, childArgs(), { cwd: scratch, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }, plan.limits);
  const streams = {}; let failure = observed.failure, nativeReceipt, nativeReceiptPin, nativeReceiptReadError;
  try { const bytes = regular(output + '.native-receipt.json', plan.limits.fileBytes); nativeReceipt = JSON.parse(bytes.toString('utf8')); nativeReceiptPin = { path: output + '.native-receipt.json', bytes: bytes.length, sha256: hash(bytes) }; }
  catch (error) { nativeReceiptReadError = { name: error.name, message: error.message }; }
  try { for (const name of ['stdout', 'stderr']) { const path = output + '.' + name + '.bin'; writeNew(scratch, path, observed[name]); streams[name] = { ...observed.streams[name], path, sha256: hash(observed[name]) }; }
    writeNew(scratch, output + '.log.json', jsonBytes({ driverChildLifecycle: observed.lifecycle, streams, failure, nativeReceipt: nativeReceiptPin, nativeReceiptReadError, originalExceptionAtNativeJoin: nativeReceipt?.failure, nativeHelperCloseCertifiedByDriverLogAlone: false }));
  } catch (error) { failure ??= { kind: 'driver-evidence-write-error', name: error.name, message: error.message, code: error.code }; }
  if (failure) { const original = nativeReceipt?.failure; const error = new Error(original?.message?.inline ?? failure.message); error.name = original?.name?.inline ?? failure.name; error.firstFailure = original ?? failure; error.driverFirstFailure = failure; error.nativeJoinReceipt = nativeReceipt; error.nativeReceiptReadError = nativeReceiptReadError; error.ownedChildLifecycle = observed.lifecycle; throw error; }
  assert.equal(observed.lifecycle.exitObserved, true); assert.equal(observed.lifecycle.closeObserved, true);
  assert(nativeReceipt, 'Successful driver must produce early native receipt'); assertPhysicalNativeClose(nativeReceipt);
  const bundle = verifyProvenanceBundle(output, plan.limits); verifyProtocolEvidence(output, nativeReceipt.protocol, plan.limits);
  assert.equal(bundle.manifest.shape.representation, 'object'); assert(!bundle.manifest.shape.fields.some(row => ['failure','nativeFirstFailure','nativeFailure'].includes(row.name)), 'Complete provenance retains a failure');
  return { output, bytes: regular(output, plan.limits.snapshotBytes), provenanceSha256: bundle.provenanceSha256, provenanceFiles: [{ path: output + '.native-receipt.json', bytes: nativeReceiptPin.bytes, sha256: nativeReceiptPin.sha256 }, { path: output + '.provenance.json', bytes: regular(output + '.provenance.json').length, sha256: bundle.provenanceSha256 }, ...bundle.manifest.parts.map(row => ({ path: output + row.suffix, bytes: row.bytes, records: row.records, sha256: row.sha256 }))], protocol: nativeReceipt.protocol, aggregateProvenanceBytes: bundle.aggregateBytes, driverPhysicalCloseObserved: true, nativePhysicalCloseObserved: true };
}
async function childRunAndVerify(oracle, scratch, plan, name) {
  let value, failure, verification;
  try { value = await childRun(oracle, scratch, plan, name); } catch (error) { failure = error; }
  try { const verified = await verifyOracle(oracle, plan); verification = { passed: true, receiptSha256: verified.receiptSha256 }; }
  catch (error) { verification = { passed: false, error: { name: error.name, message: error.message, stack: error.stack } }; failure ??= error; }
  try { writeNew(scratch, join(scratch, name + '.after-verification.json'), jsonBytes({ verification, firstFailure: failure ? { name: failure.name, message: failure.message, firstFailure: failure.firstFailure } : undefined })); }
  catch (error) { failure ??= error; }
  if (failure) throw failure; return value;
}
export async function main(args = process.argv.slice(2)) {
  const plan = readPlan(), { oracle, scratch, mode } = parseArgs(args, plan); assert.equal(process.execArgv.length, 0); assert(!existsSync(scratch), 'Fresh scratch required; uncertain/existing outputs preserved'); const harness = pins(), verified = await verifyOracle(oracle, plan), baseline = readBaseline(plan);
  const expectedPath = join(repo, 'fixtures/semantic-full-structural-openai/expected.json'), manifestPath = join(repo, 'fixtures/semantic-full-structural-openai/manifest.json'); let expected, manifest;
  if (mode === '--capture-new') assert(!existsSync(expectedPath) && !existsSync(manifestPath), 'Existing/partial genuine golden preserved; capture-new cannot replace it');
  if (mode === 'verify') { assert(existsSync(expectedPath) && existsSync(manifestPath), 'No genuine full-project reference yet; root must capture-new'); expected = regular(expectedPath, plan.limits.snapshotBytes); manifest = JSON.parse(regular(manifestPath).toString()); assertFrozen(manifest, expected, harness, verified.receiptSha256); }
  if (mode === '--check') { console.log(JSON.stringify({ status: 'read-only full structural profile/oracle/path preflight passed; actual queries not executed', oracle, scratch, receiptSha256: verified.receiptSha256, payloadFiles: 8129, sourceFiles: 2093, compilerFiles: 529, admittedPackageFiles: 5463, admittedDeclarationFiles: 1712, catalogDataFiles: 43, catalogDataBytes: 902551, openaiPackageFiles: 3548, openaiDeclarationFiles: 784, nativeApiDeclarationPins: 6, moduleCandidates: 800, entrypointConditions: 150, publishedOwnershipBlockers: 3, nativeExecuted: false })); return; }
  mkdirSync(scratch); for (const name of ['home', 'temp', 'config', 'evidence']) mkdirSync(join(scratch, name)); writeNew(scratch, join(scratch, '.pisharp-full-structural-openai-owner.json'), jsonBytes({ owner: 'PiSharp-unchanged-config-openai-recursive-structural-profile-v1', oracle, harness, receiptSha256: verified.receiptSha256, nativeExecutionRequested: true }));
  const first = await childRunAndVerify(oracle, scratch, plan, 'first'); const second = await childRunAndVerify(oracle, scratch, plan, 'second'); assert.deepEqual(pins(), harness); assert(first.bytes.equals(second.bytes), 'Two genuine whole-configuration observations differ; raw artifacts preserved');
  if (mode === '--capture-new') { manifest = { schemaVersion: 1, kind: 'genuine-unchanged-upstream-config-openai-recursive-structural-reference', sourceSha: plan.sourceCommit, receiptSha256: verified.receiptSha256, baselineSnapshotSha256: plan.baseline.snapshotSha256, currentWholeSnapshotSha256: plan.currentWholeBaseline.sha256, harness, expected: { path: 'fixtures/semantic-full-structural-openai/expected.json', bytes: first.bytes.length, sha256: hash(first.bytes) }, rawProvenance: [{ path: first.output + '.provenance.json', sha256: first.provenanceSha256 }, { path: second.output + '.provenance.json', sha256: second.provenanceSha256 }], rawProvenanceFiles: [first, second].map(item => ({ output: item.output, aggregateProvenanceBytes: item.aggregateProvenanceBytes, files: item.provenanceFiles, protocol: item.protocol })), originalConfigOpenedUnchanged: true, admittedPackageFiles: 5463, admittedDeclarationFiles: 1712, catalogDataFiles: 43, catalogDataBytes: 902551, openaiPackageFiles: 3548, openaiDeclarationFiles: 784, publishedOwnershipBlockers: 3, diagnosticFiltering: false, pathNormalization: false, fullStructuralInventoryQualified: false, semanticPublicClosure: false, phaseGatesPassed: [] }; writeNew(scratch, join(scratch, 'evidence/expected.json'), first.bytes); writeNew(scratch, join(scratch, 'evidence/manifest.json'), jsonBytes(manifest)); }
  else assert(expected.equals(first.bytes), 'Actual full-project observation differs from frozen golden');
  const result = JSON.parse(first.bytes.toString()), graph = result.structuralTypeInventory; assert.equal(graph.reachableGraphTraversalCompleted, true); assert.equal(graph.moduleRoots.length, 800); assert.equal(graph.entrypointRoots.length, 150);
  const report = { status: 'matched-complete-observations', mode, freshChildren: 2, snapshotSha256: hash(first.bytes), ...compareCounts(result, baseline, plan), declarationsConsumedInProgram: result.declarationAdmission.declarationsConsumedInProgram.length, originalCanonicalSourceRows: result.originalCanonicalSourceRows, manifestRecords: result.manifestRecords.length, entrypointRecords: result.entrypoints.length, bindings: result.bindings.length, moduleRecords: result.modules.length, programDeclarationFiles: result.programDeclarationFiles.length, mandatoryBaselineLockInstances: result.mandatoryBaselineLockInstances.length, catalogFiles: result.sourceDataAdmission.catalogFiles.length, publishedOwnershipBlockers: result.requiredPublishedOwnershipBlockers.length, structural: { symbols: graph.symbols.length, types: graph.types.length, signatures: graph.signatures.length, declarations: graph.declarations.length, syntaxNodes: graph.syntax.length, unresolvedResults: graph.unresolvedResults.length, unsupportedFields: graph.unsupportedFields.length, operationCounts: graph.operationCounts, reachableGraphTraversalCompleted: graph.reachableGraphTraversalCompleted, fullStructuralInventoryQualified: false }, semanticPublicClosure: false, phaseGatesPassed: [] }; writeNew(scratch, join(scratch, 'report.json'), jsonBytes(report)); console.log(JSON.stringify(report));
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase()) main().catch(error => { console.error(error.stack); process.exitCode = 1; });
