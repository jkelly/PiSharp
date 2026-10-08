// NEW profile: frozen predecessor mechanics are checked before their first import.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, openSync, writeSync, readSync, closeSync, fstatSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const profileFiles = ['compatibility/semantic-full-structural-openai.plan.json', 'tools/SemanticReference/full-structural-openai-common.mjs', 'tools/SemanticReference/full-structural-openai-preload.mjs', 'tools/SemanticReference/full-structural-openai-driver.mjs', 'tools/SemanticReference/run-full-structural-openai.mjs', 'tools/SemanticReference/full-structural-openai.test.mjs', 'docs/compatibility/semantic-full-structural-openai.md'];
export const planHash = 'ed7d486f01f1f5248c39ea1a7dc717bb10ae7f330622452b3ce8e0a0b50db736';
const primitivePin = { path: 'tools/SemanticReference/full-project-openai-common.mjs', bytes: 11415, sha256: '94b555a1686a1f2076f9a9473ad2b3e505263c401ae6197c6a52fe98134dbcdd' };
const primitivePath = join(repo, primitivePin.path);
for (let path = resolve(primitivePath);;) { assert(!lstatSync(path).isSymbolicLink()); const parent = dirname(path); if (parent === path) break; path = parent; }
const primitiveBytes = readFileSync(primitivePath); assert.equal(primitiveBytes.length, primitivePin.bytes); assert.equal(createHash('sha256').update(primitiveBytes).digest('hex'), primitivePin.sha256, 'Frozen predecessor common changed before import');
const previous = await import(pathToFileURL(primitivePath).href);
export const { hash, jsonBytes, order, noLinks, contained, regular, writeNew, environment, snapshot, createReadOnlyFS, flattenEntrypoints, globRegex, bindingCandidates, resolveLock, assertDiagnosticFamilies, compareCounts, classifyPackageTarget, assertRequiredPublishedBlockers, assertOriginalRoots, diagnosticMethods, mandatoryLockInventory, readPublishedArtifacts, readBaseline, parseArgs, admitPaths } = previous;
function checkPin(root, pin, maximum = 33554432) { const path = join(root, pin.path); assert(contained(root, path)); const bytes = regular(path, maximum); assert.equal(bytes.length, pin.bytes, 'Frozen input bytes: ' + pin.path); assert.equal(hash(bytes), pin.sha256, 'Frozen input hash: ' + pin.path); return bytes; }
export function assertProfilePlan(plan) {
  previous.assertProfilePlan(plan); const p = plan.structuralInventory;
  assert.equal(plan.kind, 'unchanged-whole-upstream-config-openai-actual-recursive-structural-profile');
  assert.equal(plan.previousWholeProfilePins.length, 9); assert.equal(plan.nativeApiDeclarationPins.length, 6);
  assert.equal(plan.currentWholeBaseline.sha256, 'b317776cc214fa757abf19769de3fec25323e4489226d705559441100876c107'); assert.equal(plan.currentWholeBaseline.bytes, 15195616);
  assert.equal(p.allModuleCandidates, 800); assert.equal(p.allEntrypointRows, 150); assert.equal(p.allCanonicalSourceRows, 2093);
  for (const field of ['nativeIdentityMemoization','nativeIdsInRawProvenance','traversalReferenceArraysOrdered','graphTraversalCompletenessOnly']) assert.equal(p[field], true);
  for (const field of ['methodPresenceIsExecution','formatStringsAreStructuralTypes','unsupportedArePermanentExemptions','semanticPublicClosure']) assert.equal(p[field], false);
  assert.equal(p.budgetOverflow, 'hard-fail-preserve-raw-artifacts-no-truncation'); assert.equal(p.queryFailure, 'hard-fail-with-operation-and-native-identity');
  assert.equal(p.unsupportedCheckerFields.length, 5); for (const [name, value] of Object.entries(p.limits)) assert(Number.isSafeInteger(value) && value > 0, 'Invalid structural budget ' + name);
  assert.deepEqual(p.syntaxSchema.sources, ['node_modules/typescript/dist/ast/ast.d.ts','node_modules/typescript/dist/ast/ast.generated.d.ts']); assert.equal(p.syntaxSchema.publicPrototypeGetters, true); assert.equal(p.syntaxSchema.nativeNodeArrayIteration, true); assert.equal(p.syntaxSchema.decoderStorageIsPublicSyntax, false); assert.equal(p.syntaxSchema.unknownSyntaxKind, 'hard-fail-no-generic-empty-AST');
  assert.equal(plan.limits.protocolBytes, 2147483648); assert.equal(plan.limits.snapshotBytes, 536870912); assert.equal(plan.limits.driverTimeoutMs, 1800000);
  assert.deepEqual(plan.provenanceFormat, { schemaVersion: 2, orderedSidecars: true, firstReceiptBeforeEncoding: true, receiptAndManifestShareAggregateBudget: true, snapshotAggregateBytes: 536870912, receiptInlineUtf8Bytes: 4096, streamingChunkCodeUnits: 4096, originalFailureRemainsAuthoritative: true, consumerVerifiesEverySidecar: true, oldFailedCaptureQualified: false });
  assert.deepEqual(plan.symbolQueryFailureIsolation, { enabled: true, method: 'getTypeOfSymbol', errorCode: -32603,
    panicPrefix: 'panic: interface conversion: checker.TypeData is *checker.TypeReference, not *checker.TupleType',
    scope: 'individual-symbol-value-type', continueExistingQueue: true, substituteSemanticFacts: false,
    fullCaptureMustFail: true, partialObservationSuffix: '.partial.json', referenceExecutionQualified: false });
}
export function readPlan() { const bytes = regular(join(repo, profileFiles[0])); assert.equal(hash(bytes), planHash, 'Frozen structural plan changed'); const plan = JSON.parse(bytes.toString('utf8')); assertProfilePlan(plan); return plan; }
export function pins() { return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order); }
export function currentBaselineCensus(value) {
  assertDiagnosticFamilies(value.diagnostics, diagnosticMethods);
  const tables = value.modules.filter(row => row.moduleSymbol !== undefined), exports = value.modules.flatMap(row => row.exports ?? []), sdk = value.sdkDeclarationResolution.actualProgramSdkMembers;
  return {
    moduleCandidates: value.modules.length, moduleSymbolTables: tables.length, moduleCandidatesWithoutSymbol: value.modules.filter(row => row.inProgram && row.moduleSymbol === undefined).length, moduleCandidatesOutsideProgram: value.modules.filter(row => !row.inProgram).length, emptyModuleExportTables: tables.filter(row => row.exports.length === 0).length,
    exportOccurrences: exports.length, aliasOccurrences: exports.filter(symbol => (symbol.flags & 2097152) !== 0).length, declarationHandleOccurrences: exports.reduce((n, symbol) => n + symbol.declarations.length, 0), moduleDeclarationHandleOccurrences: tables.reduce((n, row) => n + row.moduleSymbol.declarations.length, 0), exportValueDeclarationHandleOccurrences: exports.filter(symbol => symbol.valueDeclaration !== undefined).length, moduleValueDeclarationHandleOccurrences: tables.filter(row => row.moduleSymbol.valueDeclaration !== undefined).length,
    rootFiles: value.rootFiles.length, originalCanonicalSourceRows: value.originalCanonicalSourceRows, sourceFiles: value.sourceFiles.length, declarationFiles: value.programDeclarationFiles.length, manifestRecords: value.manifestRecords.length, bindingRecords: value.bindings.length, entrypointRecords: value.entrypoints.length, mandatoryLockInstances: value.mandatoryBaselineLockInstances.length, mandatoryLockSpecifierGroups: value.mandatoryBaselineLockInstances.reduce((n, row) => n + row.parsedSpecifiers.length, 0), publishedOwnershipBlockers: value.requiredPublishedOwnershipBlockers.length,
    canonicalTargetPresentConditions: value.entrypoints.filter(row => row.canonicalTargetPresent).length, publishedMemberPresentWithoutCanonicalConditions: value.entrypoints.filter(row => !row.canonicalTargetPresent && row.publishedTarget.state === 'acquired-member-present').length, unacquiredWithoutCanonicalConditions: value.entrypoints.filter(row => !row.canonicalTargetPresent && row.publishedTarget.state === 'artifact-not-acquired').length,
    admittedProgramMembers: value.declarationAdmission.admittedMembersConsumedInProgram.length, admittedProgramDeclarations: value.declarationAdmission.declarationsConsumedInProgram.length, semanticDiagnostics: value.diagnostics.getSemanticDiagnostics.length, unresolvedRecords: value.unresolved.length, diagnosticCounts: Object.fromEntries(diagnosticMethods.map(method => [method, value.diagnostics[method].length])),
    sdkProgramMembers: sdk.length, sdkProgramDtsMembers: sdk.filter(path => path.endsWith('.d.ts')).length, sdkProgramDmtsMembers: sdk.filter(path => path.endsWith('.d.mts')).length, sdkProgramCtsMembers: sdk.filter(path => path.endsWith('.cts')).length, structuralAvailableCheckerOperations: value.structuralTypeInventory.publicCheckerOperations.filter(row => row.methodAvailableOnActualChecker === true).length, structuralExecutedCheckerOperations: value.structuralTypeInventory.publicCheckerOperations.filter(row => row.operationExecutedForStructuralInventory === true).length
  };
}
export function assertCurrentBaselineCensus(value, expected) { const { path, bytes, sha256, ...counts } = expected; const observed = currentBaselineCensus(value); assert.deepEqual(observed, counts, 'Current census differs from the exact pinned whole observation'); return observed; }
export function readCurrentBaseline(plan = readPlan()) {
  const bytes = checkPin(repo, plan.currentWholeBaseline, plan.limits.snapshotBytes), value = JSON.parse(bytes.toString('utf8'));
  assertCurrentBaselineCensus(value, plan.currentWholeBaseline); return value;
}
export function assertRetainedObservation(value, current) {
  // Compare untouched data directly. No root substitutions or diagnostic normalization.
  for (const [key, expected] of Object.entries(current)) if (!['kind','structuralTypeInventory'].includes(key)) assert.deepEqual(value[key], expected, 'Whole predecessor observation changed: ' + key);
}
export function parsePublicAstSchema(texts, syntaxKind) {
  // Read public declarations as data. This contains no copied decoder/checker logic.
  const interfaces = new Map(), tokens = new Set();
  for (const text of texts) {
    for (const match of text.matchAll(/^export interface (\w+)(?:<[^{}]*?>)?(?: extends ([^{]+))? \{([\s\S]*?)^\}/gm)) {
      const [, name, heritage = '', body] = match, fields = [...body.matchAll(/^    (?:readonly )?(\w+)\??\s*:/gm)].map(row => row[1]).filter(field => !field.startsWith('_'));
      const kindDeclaration = body.match(/^    readonly kind:\s*([^;]+);/m)?.[1] ?? '', kinds = [...kindDeclaration.matchAll(/SyntaxKind\.(\w+)/g)].map(row => row[1]);
      const bases = heritage.split(',').map(base => base.trim().replace(/<.*$/, '')).filter(Boolean); interfaces.set(name, { name, fields, bases, kinds });
    }
    const tokenDeclaration = text.match(/^export type TokenSyntaxKind = ([\s\S]*?);/m)?.[1] ?? '';
    for (const match of tokenDeclaration.matchAll(/SyntaxKind\.(\w+)/g)) { assert(Number.isInteger(syntaxKind[match[1]]), 'Public token kind unavailable: ' + match[1]); tokens.add(syntaxKind[match[1]]); }
  }
  const cache = new Map();
  function fieldsFor(name, active = new Set()) {
    if (cache.has(name)) return cache.get(name); const schema = interfaces.get(name);
    if (!schema) { assert(['ReadonlyArray','Array'].includes(name), 'Unknown public AST base interface: ' + name); return []; }
    assert(!active.has(name), 'Public AST declaration inheritance cycle'); const next = new Set(active).add(name), values = [...new Set([...schema.bases.flatMap(base => fieldsFor(base, next)), ...schema.fields])]; cache.set(name, values); return values;
  }
  const kindFields = new Map();
  for (const [name, schema] of interfaces) for (const kindName of schema.kinds) { const kind = syntaxKind[kindName]; assert(Number.isInteger(kind), 'Public AST syntax kind unavailable: ' + kindName); kindFields.set(kind, [...new Set([...(kindFields.get(kind) ?? []), ...fieldsFor(name)])]); }
  const baseFields = fieldsFor('Node'), arrayFields = fieldsFor('NodeArray');
  assert(baseFields.includes('parent') && baseFields.includes('flags')); assert(arrayFields.includes('pos') && arrayFields.includes('end')); assert(interfaces.get('TypeParameterDeclaration').fields.includes('defaultType'));
  return { kindFields, tokenKinds: tokens, baseFields, arrayFields, interfaces: [...interfaces.values()].map(row => ({ name: row.name, fields: fieldsFor(row.name), kinds: row.kinds })), declaredInterfaceCount: interfaces.size };
}
export function readPublicAstSchema(oracle, syntaxKind, plan = readPlan()) {
  const texts = plan.structuralInventory.syntaxSchema.sources.map(path => { const pin = plan.nativeApiDeclarationPins.find(row => row.path === path); assert(pin); return checkPin(oracle, pin).toString('utf8'); });
  return parsePublicAstSchema(texts, syntaxKind);
}
export async function verifyOracle(oracle, plan = readPlan()) {
  assertProfilePlan(plan); for (const pin of plan.previousWholeProfilePins) checkPin(repo, pin, plan.limits.snapshotBytes);
  for (const pin of plan.nativeApiDeclarationPins) checkPin(oracle, pin);
  const verified = await previous.verifyOracle(oracle, plan); readCurrentBaseline(plan);
  return { ...verified, verificationRepoPins: [...verified.verificationRepoPins, ...plan.previousWholeProfilePins].sort(order), nativeApiDeclarationPins: plan.nativeApiDeclarationPins };
}

// Admission is over actual encoded bytes, including the early receipt, sidecars
// and manifest. A sidecar is a representation, never a new byte allowance.
export function createEncodingBudget(limits) {
  let bytes = 0, nodes = 0;
  return { limits, claimBytes(size) { assert(Number.isSafeInteger(size) && size >= 0); assert(bytes + size <= limits.snapshotBytes, 'Aggregate encoded evidence byte budget exceeded; no truncation'); bytes += size; }, visit(depth) { assert(++nodes <= limits.jsonNodes && depth <= limits.jsonDepth, 'Aggregate encoded evidence node/depth budget exceeded; no truncation'); }, counts() { return { bytes, nodes }; } };
}
const secondaryEvidenceFailures = new Map();
export function retainCaptureEvidenceFailures(error, records) { const combined = [...(secondaryEvidenceFailures.get(error) ?? []), ...records]; secondaryEvidenceFailures.set(error, combined); try { error.captureEvidenceFailures = combined; } catch {} return combined; }
export function captureEvidenceFailures(error) { return secondaryEvidenceFailures.get(error) ?? error?.captureEvidenceFailures ?? []; }
export function* streamingJsonChunks(value, budget, chunkUnits = 4096, initialDepth = 0) {
  assert(Number.isSafeInteger(chunkUnits) && chunkUnits > 1); assert(Number.isSafeInteger(initialDepth) && initialDepth >= 0); const active = new Set();
  function* string(text) {
    yield Buffer.from('"');
    for (let start = 0; start < text.length;) { let end = Math.min(text.length, start + chunkUnits); if (end < text.length && text.charCodeAt(end - 1) >= 0xd800 && text.charCodeAt(end - 1) <= 0xdbff && text.charCodeAt(end) >= 0xdc00 && text.charCodeAt(end) <= 0xdfff) end--; yield Buffer.from(JSON.stringify(text.slice(start, end)).slice(1, -1)); start = end; }
    yield Buffer.from('"');
  }
  function* visit(item, depth, inArray = false) {
    budget.visit(depth);
    if (item === undefined) { assert(inArray, 'Undefined root cannot be encoded'); yield Buffer.from('null'); return; }
    if (typeof item === 'string') { yield* string(item); return; }
    if (item === null || typeof item === 'boolean') { yield Buffer.from(String(item)); return; }
    if (typeof item === 'number') { assert(Number.isFinite(item), 'Nonfinite evidence number'); yield Buffer.from(JSON.stringify(item)); return; }
    assert(typeof item === 'object' && !active.has(item), 'Unsupported/cyclic evidence value'); active.add(item);
    try {
      if (Array.isArray(item)) { yield Buffer.from('['); for (let index = 0; index < item.length; index++) { if (index) yield Buffer.from(','); yield* visit(item[index], depth + 1, true); } yield Buffer.from(']'); }
      else { yield Buffer.from('{'); let first = true; for (const key of Object.keys(item)) { const child = item[key]; if (child === undefined) continue; if (!first) yield Buffer.from(','); first = false; yield* string(key); yield Buffer.from(':'); yield* visit(child, depth + 1); } yield Buffer.from('}'); }
    } finally { active.delete(item); }
  }
  yield* visit(value, initialDepth);
}
export const evidenceIo = { open(path) { noLinks(path); return openSync(path, 'wx'); }, write: writeSync, read: readSync, close: closeSync, openRead(path) { noLinks(path); return openSync(path, 'r'); }, stat: fstatSync, small: regular };
export function writeStreamedEvidence(scratch, path, records, budget, io = evidenceIo, semanticDepthOffset = 0) {
  assert(contained(scratch, path)); const descriptor = io.open(path), digest = createHash('sha256'), buffer = Buffer.alloc(65536); let bytes = 0, count = 0, buffered = 0, ioFailed = false, failure;
  function flush() { let offset = 0; try { while (offset < buffered) { const written = io.write(descriptor, buffer, offset, buffered - offset); assert(Number.isSafeInteger(written) && written > 0 && written <= buffered - offset, 'Evidence write made no progress'); digest.update(buffer.subarray(offset, offset + written)); bytes += written; offset += written; } buffered = 0; } catch (error) { ioFailed = true; throw error; } }
  function write(raw) { budget.claimBytes(raw.length); for (let offset = 0; offset < raw.length;) { const size = Math.min(buffer.length - buffered, raw.length - offset); raw.copy(buffer, buffered, offset, offset + size); buffered += size; offset += size; if (buffered === buffer.length) flush(); } }
  function failed(error) { if (failure) retainCaptureEvidenceFailures(failure, [{ stage: 'stream-finalization', error }]); else failure = error; }
  try { for (const value of records) { for (const raw of streamingJsonChunks(value, budget, 4096, semanticDepthOffset)) write(raw); write(Buffer.from('\n')); count++; } }
  catch (error) { failed(error); }
  finally { try { if (!ioFailed) flush(); } catch (error) { failed(error); } try { io.close(descriptor); } catch (error) { failed(error); } }
  if (failure) throw failure;
  return { bytes, records: count, sha256: digest.digest('hex') };
}
export function textIdentity(value, inlineBytes = 4096) {
  if (value === undefined) return undefined; const text = String(value), digest = createHash('sha256'); let bytes = 0;
  for (let start = 0; start < text.length;) { let end = Math.min(text.length, start + 4096); if (end < text.length && text.charCodeAt(end - 1) >= 0xd800 && text.charCodeAt(end - 1) <= 0xdbff && text.charCodeAt(end) >= 0xdc00 && text.charCodeAt(end) <= 0xdfff) end--; const raw = Buffer.from(text.slice(start, end)); digest.update(raw); bytes += raw.length; start = end; }
  return { utf8Bytes: bytes, sha256: digest.digest('hex'), inline: bytes <= inlineBytes ? text : undefined, completeTextInFullProvenanceRequired: bytes > inlineBytes };
}
export function failureIdentity(error, inlineBytes = 4096) { return error ? { name: textIdentity(error.name, inlineBytes), message: textIdentity(error.message, inlineBytes), stack: textIdentity(error.stack, inlineBytes), code: textIdentity(error.code, inlineBytes), syscall: textIdentity(error.syscall, inlineBytes), path: textIdentity(error.path, inlineBytes), kind: textIdentity(error.kind, inlineBytes) } : undefined; }
export function writeProvenanceBundle(scratch, output, value, budget, receipt, io = evidenceIo) {
  const parts = []; const active = new Set();
  function shape(item, path, depth = 0) {
    assert(depth <= budget.limits.jsonDepth, 'Provenance shape depth exceeded');
    if (Array.isArray(item) || typeof item === 'string' && Buffer.byteLength(item) > 4096) {
      const ordinal = parts.length, suffix = '.provenance.' + ordinal + '.jsonl', array = Array.isArray(item); parts.push(undefined);
      function* records() { if (array) { for (let index = 0; index < item.length; index++) yield { ordinal: index, value: item[index] === undefined ? null : item[index] }; } else yield { ordinal: 0, value: item }; }
      const pin = writeStreamedEvidence(scratch, output + suffix, records(), budget, io, array ? depth : Math.max(0, depth - 1)); parts[ordinal] = { ordinal, fieldPath: path, valueKind: array ? 'ordered-array' : 'exact-string', suffix, ...pin }; return { representation: 'ordered-sidecar', ordinal };
    }
    if (item && typeof item === 'object') { assert(!active.has(item), 'Cyclic provenance shape'); active.add(item); try { return { representation: 'object', fields: Object.keys(item).filter(key => item[key] !== undefined).map(key => ({ name: key, value: shape(item[key], [...path, key], depth + 1) })) }; } finally { active.delete(item); } }
    return { representation: 'inline', value: item };
  }
  const manifest = { schemaVersion: 2, kind: 'complete-ordered-provenance-sidecars', receipt, parts, shape: shape(value, []), complete: true, aggregateEncodedByteLimit: budget.limits.snapshotBytes, nodeAndDepthLimitsUnchanged: true, noTruncation: true };
  const pin = writeStreamedEvidence(scratch, output + '.provenance.json', [manifest], budget, io); return { manifest, pin, aggregate: budget.counts() };
}
export function verifyStreamedFile(path, pin, limits, io = evidenceIo, orderedRecords = false) {
  assert(Number.isSafeInteger(pin.bytes) && pin.bytes >= 0 && pin.bytes <= limits.snapshotBytes); const descriptor = io.openRead(path), digest = createHash('sha256'), buffer = Buffer.alloc(65536); let bytes = 0, records = 0, prefix = '', lastByte, lineStarted = false;
  try { assert(io.stat(descriptor).isFile() && io.stat(descriptor).size === pin.bytes, 'Sidecar physical byte count differs'); for (;;) { const size = io.read(descriptor, buffer, 0, buffer.length, null); if (!size) break; bytes += size; assert(bytes <= pin.bytes); const raw = buffer.subarray(0, size); digest.update(raw);
      if (orderedRecords) for (const byte of raw) { if (byte === 10) { assert(lineStarted && prefix.startsWith('{"ordinal":' + records + ',"value":') && lastByte === 125, 'Sidecar record order/framing differs'); records++; prefix = ''; lineStarted = false; } else { lineStarted = true; if (prefix.length < 96) prefix += String.fromCharCode(byte); lastByte = byte; } }
    } }
  finally { io.close(descriptor); }
  assert.equal(bytes, pin.bytes); assert.equal(digest.digest('hex'), pin.sha256, 'Sidecar hash differs'); if (orderedRecords) { assert(!lineStarted, 'Incomplete final sidecar record'); assert.equal(records, pin.records, 'Sidecar record count differs'); } return { bytes, records };
}
export function verifyProvenanceBundle(output, limits, io = evidenceIo) {
  const receiptPath = output + '.native-receipt.json', receiptBytes = io.small(receiptPath, limits.fileBytes), receipt = JSON.parse(receiptBytes.toString('utf8'));
  const manifestPath = output + '.provenance.json', manifestBytes = io.small(manifestPath, limits.fileBytes), manifest = JSON.parse(manifestBytes.toString('utf8'));
  assert.equal(manifest.schemaVersion, 2); assert.equal(manifest.kind, 'complete-ordered-provenance-sidecars'); assert.equal(manifest.complete, true); assert.equal(manifest.noTruncation, true); assert.equal(manifest.aggregateEncodedByteLimit, limits.snapshotBytes);
  assert.deepEqual(manifest.receipt, { suffix: '.native-receipt.json', bytes: receiptBytes.length, sha256: hash(receiptBytes) });
  let aggregateBytes = receiptBytes.length + manifestBytes.length; const refs = [], fields = new Set();
  function visit(node) { if (node.representation === 'ordered-sidecar') { assert(Number.isSafeInteger(node.ordinal)); refs.push(node.ordinal); } else if (node.representation === 'object') { const names = new Set(); for (const row of node.fields) { assert(typeof row.name === 'string' && !names.has(row.name)); names.add(row.name); visit(row.value); } } else assert.equal(node.representation, 'inline'); }
  visit(manifest.shape); assert.deepEqual([...refs].sort((a,b) => a-b), manifest.parts.map((_,index) => index), 'Every sidecar must have exactly one shape reference');
  for (const [index, part] of manifest.parts.entries()) { assert.equal(part.ordinal, index); assert.equal(part.suffix, '.provenance.' + index + '.jsonl'); assert(['ordered-array','exact-string'].includes(part.valueKind)); assert(Array.isArray(part.fieldPath) && part.fieldPath.every(key => typeof key === 'string')); const field = JSON.stringify(part.fieldPath); assert(!fields.has(field)); fields.add(field); assert(Number.isSafeInteger(part.bytes) && part.bytes >= 0); assert(Number.isSafeInteger(part.records) && part.records >= 0 && part.records <= limits.jsonNodes); assert(/^[a-f0-9]{64}$/.test(part.sha256)); if (part.valueKind === 'exact-string') assert.equal(part.records, 1); aggregateBytes += part.bytes; assert(aggregateBytes <= limits.snapshotBytes, 'Aggregate provenance budget exceeded'); }
  assert.deepEqual(manifest.parts.map(row => row.fieldPath), collectSidecarPaths(manifest.shape), 'Shape paths do not match ordered sidecars');
  for (const part of manifest.parts) verifyStreamedFile(output + part.suffix, part, limits, io, true);
  return { receipt, manifest, aggregateBytes, provenanceSha256: hash(manifestBytes), receiptSha256: hash(receiptBytes) };
}
function collectSidecarPaths(root) { const paths = []; function visit(node, path) { if (node.representation === 'ordered-sidecar') paths[node.ordinal] = path; else if (node.representation === 'object') for (const row of node.fields) visit(row.value, [...path,row.name]); } visit(root, []); return paths; }
export function verifyProtocolEvidence(output, protocol, limits, io = evidenceIo) {
  assert.equal(protocol.path, output + '.protocol.jsonl');
  for (const key of ['rawBytes','capturedRawBytes','omittedRawBytes','fileBytes','chunks']) assert(Number.isSafeInteger(protocol[key]) && protocol[key] >= 0);
  assert.equal(protocol.rawBytes, protocol.capturedRawBytes + protocol.omittedRawBytes); assert(protocol.capturedRawBytes <= limits.protocolBytes); assert(protocol.chunks <= protocol.capturedRawBytes); assert.equal(protocol.complete, true); assert.equal(protocol.omittedRawBytes, 0); assert.equal(protocol.writeFailure, false); assert.equal(protocol.overflow, false);
  // Base64 expansion and the unchanged per-chunk JSON wrapper consume physical
  // storage, but never enlarge the original 2GiB admitted raw-traffic bound.
  const maximumEncodedBytes = Math.ceil(protocol.capturedRawBytes * 4 / 3) + protocol.chunks * 128; assert(protocol.fileBytes <= maximumEncodedBytes);
  return verifyStreamedFile(protocol.path, { bytes: protocol.fileBytes, sha256: protocol.sha256 }, { ...limits, snapshotBytes: maximumEncodedBytes }, io);
}
