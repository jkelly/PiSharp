// No source transformations, copied private compiler logic or ambient stubs.
import assert from 'node:assert/strict';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { repo, hash, regular, snapshot, readPlan, verifyOracle, flattenEntrypoints, bindingCandidates, resolveLock, assertDiagnosticFamilies, readBaseline, readCurrentBaseline, readPublicAstSchema, assertRetainedObservation, mandatoryLockInventory, classifyPackageTarget, assertRequiredPublishedBlockers, assertOriginalRoots, createEncodingBudget, writeStreamedEvidence, writeProvenanceBundle, failureIdentity, textIdentity, evidenceIo, retainCaptureEvidenceFailures, captureEvidenceFailures } from './full-structural-openai-common.mjs';

export const requiredCheckerOperations = ['getAliasedSymbol','getImmediateAliasedSymbol','isUnknownSymbol','getTypeOfSymbol','getDeclaredTypeOfSymbol','getTypeAtLocation','getTypeFromTypeNode','getPropertiesOfType','getSignaturesOfType','getReturnTypeOfSignature','getRestTypeOfSignature','getTypePredicateOfSignature','getIndexInfosOfType','getTypeArguments','getConstraintOfTypeParameter','getBaseConstraintOfType','getParameterType','typeToString'];
export const unavailablePublicFields = ['getDefaultFromTypeParameter','instantiated mapped key/remapping/readonly/optional metadata','conditional distributivity and inferred parameters','signature minimum argument count','tuple label metadata and instantiated generic variance'];
export class StructuralBudgetError extends Error { constructor(message) { super(message); this.name = 'StructuralBudgetError'; } }
export function isRetainedSymbolTypePanic(error) {
  return error?.code === -32603 && typeof error.message === 'string' &&
    /^panic: interface conversion: checker\.TypeData is \*checker\.TypeReference, not \*checker\.TupleType(?:\n|$)/.test(error.message);
}
export function createStructuralInventory(checker, project, enums, limits, options = {}) {
  const { SymbolFlags, SignatureKind, ObjectFlags, TypeFlags, astSchema } = enums; assert(astSchema, 'Pinned public AST declarations required for prototype getters');
  const output = { schemaVersion: 1, identitySemantics: 'references assigned in first-observed traversal order, memoized by actual native identity; raw native IDs retained in provenance', symbols: [], types: [], signatures: [], declarations: [], syntax: [], moduleRoots: [], entrypointRoots: [], unsupportedFields: [], operationCounts: [], unresolvedResults: [], reachableGraphTraversalCompleted: false, fullStructuralInventoryQualified: false, semanticPublicClosure: false };
  const identities = { symbols: new Map(), types: new Map(), signatures: new Map(), declarations: new Map() }, objects = { symbols: [], types: [], signatures: [], declarations: [] }, queue = [], rawIdentities = [], syntaxIds = new WeakMap(), syntaxObjects = [];
  const counts = new Map(), queryLog = [], failures = [], rawSyntaxIdentities = []; let cursor = 0, operations = 0, firstIsolatedFailure;
  function bound(name, size) { if (size > limits[name]) throw new StructuralBudgetError('Complete structural ' + name + ' budget exceeded: ' + size + '/' + limits[name] + '; no truncation permitted'); }
  function reference(kind, value) {
    if (value === undefined) return { state: 'undefined' };
    if (value === null) return { state: 'null' };
    const key = kind === 'declarations' ? JSON.stringify([value.path, value.index, value.kind]) : value.id;
    if (kind !== 'declarations') assert(Number.isSafeInteger(key), 'Real native identity required');
    const known = identities[kind].get(key); if (known !== undefined) return { state: 'observed', ref: known };
    const index = output[kind].length; bound(kind, index + 1); const ref = ({ symbols: 's', types: 't', signatures: 'g', declarations: 'd' })[kind] + index;
    identities[kind].set(key, ref); objects[kind].push(value); output[kind].push({ ref }); queue.push({ kind, index });
    rawIdentities.push({ kind, ref, nativeIdentity: key, producingProject: project.id }); return { state: 'observed', ref };
  }
  const symbolRef = value => reference('symbols', value), typeRef = value => reference('types', value), signatureRef = value => reference('signatures', value), declarationRef = value => reference('declarations', value);
  async function call(owner, method, args = [], context) {
    bound('operations', ++operations); const previous = counts.get(method) ?? { method, executed: 0, undefinedResults: 0, nullResults: 0, failures: 0 }; counts.set(method, previous);
    const event = { order: queryLog.length, method, ownerNativeId: owner?.id, context, state: 'requested' }; queryLog.push(event);
    try { assert.equal(typeof owner?.[method], 'function', 'Required public checker/object operation unavailable: ' + method); previous.executed++; const value = await owner[method](...args); event.state = value === undefined ? 'undefined' : value === null ? 'null' : 'returned'; if (value === undefined) previous.undefinedResults++; if (value === null) previous.nullResults++; return value; }
    catch (error) { previous.failures++; event.state = 'failed'; event.error = { name: error.name, message: error.message }; failures.push(event); throw error; }
  }
  function scalar(value) { return typeof value === 'bigint' ? { kind: 'actual-bigint', decimal: value.toString() } : value; }
  function publicNodeLocation(node) { return { kind: node.kind, pos: node.pos, end: node.end, flags: node.flags, sourceFileName: node.getSourceFile?.().fileName }; }
  function syntaxValue(value, depth = 0) {
    if (depth > limits.syntaxDepth) throw new StructuralBudgetError('Complete declaration syntax depth exceeded; no truncation permitted');
    if (value === undefined) return { state: 'undefined' }; if (value === null || typeof value !== 'object') { assert.notEqual(typeof value, 'function', 'Unexpected own AST function requires explicit support'); return scalar(value); }
    if (syntaxIds.has(value)) return { syntaxRef: syntaxIds.get(value) };
    const isArray = Array.isArray(value), isNode = !isArray && Number.isInteger(value.kind) && typeof value.getSourceFile === 'function';
    bound('syntaxNodes', output.syntax.length + 1); const ref = 'n' + output.syntax.length, row = { ref, kind: isArray ? 'ordered-public-array' : isNode ? 'actual-public-AST-node' : 'actual-public-data-object' }; syntaxIds.set(value, ref); output.syntax.push(row); syntaxObjects.push(value);
    let fieldNames;
    if (isNode) {
      fieldNames = astSchema.kindFields.get(value.kind) ?? (astSchema.tokenKinds.has(value.kind) ? astSchema.baseFields : undefined);
      if (!fieldNames) { const failure = { method: 'public-AST-syntax-schema', state: 'failed', ref, location: publicNodeLocation(value), error: { message: 'Actual syntax kind absent from pinned public declarations: ' + value.kind } }; failures.push(failure); throw new Error(failure.error.message + ' at ' + JSON.stringify(failure.location)); }
      rawSyntaxIdentities.push({ ref, publicNativeId: value.id, location: publicNodeLocation(value) });
    } else if (isArray) {
      // RemoteNodeList deliberately exposes initial entries as prototype getters.
      // Array.prototype.map skips these lazy entries; its public iterator does not.
      row.items = Array.from(value, item => syntaxValue(item, depth + 1));
      fieldNames = [...new Set([...astSchema.arrayFields, 'parent'])];
    } else if (value instanceof Map) { row.kind = 'actual-public-map'; row.entries = [...value].map(([key, item]) => [syntaxValue(key, depth + 1), syntaxValue(item, depth + 1)]); fieldNames = []; }
    else if (value instanceof Set) { row.kind = 'actual-public-set'; row.items = [...value].map(item => syntaxValue(item, depth + 1)); fieldNames = []; }
    else fieldNames = Object.keys(value);
    // Public Node/NodeArray fields come from hash-pinned declarations, including
    // inherited prototype getters. Private decoder storage is never a type field.
    // Parent is a context edge; preserve its actual coordinates and pinned source.
    const fields = [];
    for (const key of fieldNames) {
      const available = key in value, actual = value[key];
      if (isNode && !available) unsupported(ref, 'public AST field ' + key, 'Declared public syntax field absent on actual native node: ' + value.kind, true);
      if (key === 'parent' && actual && typeof actual === 'object') fields.push({ name: key, publicFieldAvailable: available, value: { relation: 'actual-declaration-context-parent', ...publicNodeLocation(actual), expandedAsChild: false } });
      else fields.push({ name: key, publicFieldAvailable: available, value: syntaxValue(actual, depth + 1) });
    }
    row.fields = fields; return { syntaxRef: ref };
  }
  function unsupported(ref, field, reason, supplementaryDeclarationEvidence = false) { output.unsupportedFields.push({ ref, field, reason, supplementaryDeclarationEvidence, permanentExemption: false, semanticQualificationBlocked: true }); }
  function kinds(type) { return Object.fromEntries(['isClassOrInterface','isUnionType','isIntersectionType','isObjectType','isIntrinsicType','isErrorType','isLiteralType','isStringLiteralType','isNumberLiteralType','isBigIntLiteralType','isBooleanLiteralType','isTypeReference','isTupleType','isIndexType','isIndexedAccessType','isConditionalType','isSubstitutionType','isTemplateLiteralType','isStringMappingType','isTypeParameter'].map(method => { assert.equal(typeof type[method], 'function', 'Missing actual type kind predicate: ' + method); return [method, type[method]()]; })); }
  async function processSymbol(symbol, row) {
    Object.assign(row, { name: symbol.name, escapedName: symbol.escapedName, flags: symbol.flags, checkFlags: symbol.checkFlags, declarations: symbol.declarations.map(declarationRef), valueDeclaration: declarationRef(symbol.valueDeclaration) });
    row.isUnknownSymbol = await call(checker, 'isUnknownSymbol', [symbol], row.ref);
    if (row.isUnknownSymbol) output.unresolvedResults.push({ ref: row.ref, kind: 'actual-checker-unknown-symbol', name: symbol.name });
    if (symbol.flags & SymbolFlags.Alias) { row.immediateAlias = symbolRef(await call(checker, 'getImmediateAliasedSymbol', [symbol], row.ref)); row.aliasedSymbol = symbolRef(await call(checker, 'getAliasedSymbol', [symbol], row.ref)); }
    // A symbol can have both value and type facets. Query each genuine facet;
    // do not call a type-only symbol a value or invent a function declared type.
    if (symbol.flags & SymbolFlags.Value) {
      let value, unresolved;
      try { value = await call(checker, 'getTypeOfSymbol', [symbol], row.ref); }
      catch (error) {
        if (options.isolateSymbolTypePanic !== true || !isRetainedSymbolTypePanic(error)) throw error;
        firstIsolatedFailure ??= error;
        const event = queryLog.at(-1);
        event.error = { name: error.name, message: error.message, stack: error.stack, code: error.code };
        unresolved = { ref: row.ref, kind: 'actual-native-symbol-value-type-query-failure', name: symbol.name,
          nativeSymbolId: symbol.id, method: event.method, queryOrder: event.order, error: event.error,
          semanticQualificationBlocked: true, permanentExemption: false };
        output.unresolvedResults.push(unresolved);
        row.valueType = { state: 'unresolved', reason: unresolved.kind, queryOrder: event.order };
      }
      if (!unresolved) row.valueType = typeRef(value);
    }
    if (symbol.flags & SymbolFlags.Type) row.declaredType = typeRef(await call(checker, 'getDeclaredTypeOfSymbol', [symbol], row.ref));
    row.parent = symbolRef(await call(symbol, 'getParent', [], row.ref)); row.exportSymbol = symbolRef(await call(symbol, 'getExportSymbol', [], row.ref));
    row.members = [...await call(symbol, 'getMembers', [], row.ref)].map(([name, member]) => ({ escapedName: name, symbol: symbolRef(member) }));
    row.exports = [...await call(symbol, 'getExports', [], row.ref)].map(([name, member]) => ({ escapedName: name, symbol: symbolRef(member) }));
  }
  async function processType(type, row) {
    const predicates = kinds(type); Object.assign(row, { flags: type.flags, kindPredicates: predicates });
    if (predicates.isObjectType) row.objectFlags = type.objectFlags;
    if (predicates.isLiteralType) row.literalValue = scalar(type.value);
    if (predicates.isIntrinsicType) row.intrinsicName = type.intrinsicName;
    if (predicates.isTypeParameter && type.isThisType !== undefined) row.isThisType = type.isThisType;
    if (predicates.isErrorType) output.unresolvedResults.push({ ref: row.ref, kind: 'actual-checker-error-type', intrinsicName: type.intrinsicName });
    row.symbol = symbolRef(await call(type, 'getSymbol', [], row.ref)); row.aliasSymbol = symbolRef(await call(type, 'getAliasSymbol', [], row.ref)); row.aliasTypeArguments = (await call(type, 'getAliasTypeArguments', [], row.ref)).map(typeRef);
    row.properties = (await call(checker, 'getPropertiesOfType', [type], row.ref)).map(symbolRef);
    row.callSignatures = (await call(checker, 'getSignaturesOfType', [type, SignatureKind.Call], row.ref)).map(signatureRef);
    row.constructSignatures = (await call(checker, 'getSignaturesOfType', [type, SignatureKind.Construct], row.ref)).map(signatureRef);
    row.indexInfos = (await call(checker, 'getIndexInfosOfType', [type], row.ref)).map(info => ({ keyType: typeRef(info.keyType), valueType: typeRef(info.valueType), isReadonly: info.isReadonly, declaration: declarationRef(info.declaration) }));
    row.baseConstraint = typeRef(await call(checker, 'getBaseConstraintOfType', [type], row.ref));
    row.display = { text: await call(checker, 'typeToString', [type], row.ref), interpretation: 'actual formatting only; never used as structural closure or a replacement type' };
    if (predicates.isTypeReference) { row.target = typeRef(await call(type, 'getTarget', [], row.ref)); row.typeArguments = (await call(checker, 'getTypeArguments', [type], row.ref)).map(typeRef); unsupported(row.ref, 'instantiated generic variance', 'No native public type/response variance operation or field', true); }
    if (type.flags & TypeFlags.Freshable) { row.freshType = typeRef(await call(type, 'getFreshType', [], row.ref)); row.regularType = typeRef(await call(type, 'getRegularType', [], row.ref)); }
    if (predicates.isClassOrInterface) row.baseTypes = (await call(type, 'getBaseTypes', [], row.ref)).map(typeRef);
    if (predicates.isClassOrInterface || predicates.isTupleType) { row.typeParameters = (await call(type, 'getTypeParameters', [], row.ref)).map(typeRef); row.outerTypeParameters = (await call(type, 'getOuterTypeParameters', [], row.ref)).map(typeRef); row.localTypeParameters = (await call(type, 'getLocalTypeParameters', [], row.ref)).map(typeRef); }
    if (predicates.isTupleType) {
      // Required TupleType metadata is optional in the pinned TypeResponse wire. Record an actual
      // absent/null response as mandatory unresolved data; do not invent empty/zero/false defaults.
      row.tupleMetadata = {};
      for (const field of ['elementFlags','fixedLength','readonly']) {
        const value = type[field];
        if (value === undefined || value === null) {
          const state = value === undefined ? 'undefined' : 'null'; row[field] = { state };
          row.tupleMetadata[field] = { state, semanticQualificationBlocked: true };
          output.unresolvedResults.push({ ref: row.ref, kind: 'actual-native-missing-tuple-metadata', field,
            state, semanticQualificationBlocked: true, permanentExemption: false });
        } else {
          assert(field === 'elementFlags' ? Array.isArray(value) && value.every(flag => Number.isSafeInteger(flag) && flag >= 0) :
            field === 'fixedLength' ? Number.isSafeInteger(value) && value >= 0 : typeof value === 'boolean',
            'Invalid actual public tuple metadata: ' + field + ' at ' + row.ref);
          row[field] = field === 'elementFlags' ? [...value] : value;
          row.tupleMetadata[field] = { state: 'observed' };
        }
      }
      unsupported(row.ref, 'tuple label metadata', 'No labels in native public TupleType/TypeResponse; exact NamedTupleMember syntax retained when declared', true);
    }
    if (predicates.isUnionType || predicates.isIntersectionType || predicates.isTemplateLiteralType) row.constituentTypes = (await call(type, 'getTypes', [], row.ref)).map(typeRef);
    if (predicates.isTemplateLiteralType) row.texts = [...type.texts];
    if (predicates.isIndexType || predicates.isStringMappingType) row.target = typeRef(await call(type, 'getTarget', [], row.ref));
    if (predicates.isIndexedAccessType) { row.objectType = typeRef(await call(type, 'getObjectType', [], row.ref)); row.indexType = typeRef(await call(type, 'getIndexType', [], row.ref)); }
    if (predicates.isConditionalType) { for (const [field, method] of [['checkType','getCheckType'],['extendsType','getExtendsType'],['trueType','getTrueType'],['falseType','getFalseType']]) row[field] = typeRef(await call(type, method, [], row.ref)); unsupported(row.ref, 'conditional distributivity and inferred parameters', 'Not fields or operations of the exposed native TypeResponse/ConditionalType', true); }
    if (predicates.isSubstitutionType) { row.baseType = typeRef(await call(type, 'getBaseType', [], row.ref)); row.constraint = typeRef(await call(type, 'getConstraint', [], row.ref)); }
    if (predicates.isTypeParameter) { row.constraint = typeRef(await call(checker, 'getConstraintOfTypeParameter', [type], row.ref)); unsupported(row.ref, 'getDefaultFromTypeParameter', 'No public native checker operation; declaration defaultType and getTypeFromTypeNode are recorded separately, without inventing an instantiated default', true); }
    if (predicates.isObjectType && (type.objectFlags & ObjectFlags.Mapped)) unsupported(row.ref, 'instantiated mapped key/remapping/readonly/optional metadata', 'Native TypeResponse/ObjectType omits mapped metadata; exact MappedTypeNode syntax retained for declarations', true);
  }
  async function processSignature(signature, row) {
    Object.assign(row, { declaration: declarationRef(signature.declaration), hasRestParameter: signature.hasRestParameter, isConstruct: signature.isConstruct, isAbstract: signature.isAbstract });
    row.target = signatureRef(await call(signature, 'getTarget', [], row.ref)); row.typeParameters = (await call(signature, 'getTypeParameters', [], row.ref)).map(typeRef);
    const parameters = await call(signature, 'getParameters', [], row.ref); row.parameters = [];
    for (let index = 0; index < parameters.length; index++) row.parameters.push({ position: index, symbol: symbolRef(parameters[index]), type: typeRef(await call(checker, 'getParameterType', [signature, index], row.ref)) });
    row.thisParameter = symbolRef(await call(signature, 'getThisParameter', [], row.ref)); row.returnType = typeRef(await call(checker, 'getReturnTypeOfSignature', [signature], row.ref)); row.restType = typeRef(await call(checker, 'getRestTypeOfSignature', [signature], row.ref));
    const predicate = await call(checker, 'getTypePredicateOfSignature', [signature], row.ref); row.typePredicate = predicate === undefined ? { state: 'undefined' } : { state: 'observed', kind: predicate.kind, parameterName: predicate.parameterName, parameterIndex: predicate.parameterIndex, type: typeRef(predicate.type) };
    unsupported(row.ref, 'signature minimum argument count', 'Not a public SignatureResponse/Signature field; ordered real parameters, optional symbol flags and complete declaration syntax are retained', true);
  }
  async function processDeclaration(handle, row) {
    Object.assign(row, { handle: { index: handle.index, kind: handle.kind, path: handle.path } });
    const node = await call(handle, 'resolve', [project], row.ref);
    if (!node) { row.resolution = 'actual-public-handle-resolution-returned-undefined'; output.unresolvedResults.push({ ref: row.ref, kind: row.resolution, handle: row.handle }); return; }
    row.resolution = 'actual-AST-node'; row.node = publicNodeLocation(node); row.fullText = node.getFullText(); row.text = node.getText();
    const before = syntaxObjects.length; row.syntax = syntaxValue(node); row.typeAtDeclaration = typeRef(await call(checker, 'getTypeAtLocation', [node], row.ref)); row.annotatedTypeNodes = [];
    // Query actual declaration type nodes, including the native AST's defaultType
    // field (not the classic compiler API spelling `default`). Preserve syntax too.
    const queriedTypeNodes = new Map();
    for (const item of syntaxObjects.slice(before)) if (item && !Array.isArray(item)) for (const field of ['type','constraint','defaultType','nameType']) {
      const child = item[field]; if (!child || typeof child !== 'object' || !Number.isInteger(child.kind)) continue;
      if (!queriedTypeNodes.has(child)) queriedTypeNodes.set(child, typeRef(await call(checker, 'getTypeFromTypeNode', [child], row.ref)));
      row.annotatedTypeNodes.push({ field, syntax: syntaxValue(child), location: publicNodeLocation(child), type: queriedTypeNodes.get(child) });
    }
  }
  return {
    seedModule(path, moduleSymbol, exports) { output.moduleRoots.push({ path, moduleSymbol: symbolRef(moduleSymbol), exports: exports.map((symbol, ordinal) => ({ ordinal, name: symbol.name, symbol: symbolRef(symbol) })) }); },
    seedEntrypoints(rows) { output.entrypointRoots = rows.map((row, ordinal) => ({ ordinal, manifestPath: row.manifestPath, field: row.field, subpath: row.subpath, conditions: row.conditions, target: row.target, canonicalTarget: row.canonicalTarget, matchingSourceTargets: row.matchingSourceTargets, matchingModuleRootOrdinals: output.moduleRoots.flatMap((module, index) => row.matchingSourceTargets?.includes(module.path) ? [index] : []), canonicalTargetPresent: row.canonicalTargetPresent, publishedTarget: row.publishedTarget, sourceOwnershipVerified: row.sourceOwnershipVerified, mandatoryMappingOrOwnershipBlocker: row.mandatoryMappingOrOwnershipBlocker, inferredDistToSourceMapping: false })) ; },
    async finish() { while (cursor < queue.length) { const { kind, index } = queue[cursor++]; const row = output[kind][index], value = objects[kind][index]; if (kind === 'symbols') await processSymbol(value, row); else if (kind === 'types') await processType(value, row); else if (kind === 'signatures') await processSignature(value, row); else await processDeclaration(value, row); } output.operationCounts = [...counts.values()]; output.reachableGraphTraversalCompleted = firstIsolatedFailure === undefined; output.queueRecords = queue.length; if (firstIsolatedFailure) output.existingWorkQueueDrainedWithUnresolvedQueries = true; return output; },
    isolatedFailure() { return firstIsolatedFailure; },
    partialObservation() { output.operationCounts = [...counts.values()]; output.queueRecords = queue.length; return output; },
    provenance() { return { projectId: project.id, nativeIdentityMap: rawIdentities, syntaxNativeIdentityMap: rawSyntaxIdentities, queryLog, failures, queueProcessed: cursor, queueRecords: queue.length, operations, noTruncation: true }; }
  };
}

export async function finishNativeSession(view, api, guard) {
  const steps = []; let failure, lifecycle;
  for (const [name, action] of [['view.dispose', async () => { if (view) await view.dispose(); }], ['API.close', async () => { if (api) await api.close(); }], ['guard.finish', async () => { lifecycle = await guard.finish(); }]]) {
    try { await action(); steps.push({ name, completed: true }); }
    catch (error) { failure ??= error; steps.push({ name, completed: false, error: { name: error.name, message: error.message, stack: error.stack } }); }
  }
  return { failure, lifecycle, steps };
}
export function retainStructuralFailure(structural, error) {
  const first = structural?.isolatedFailure() ?? error;
  if (first !== error) retainCaptureEvidenceFailures(first, [{ stage: 'continued-structural-traversal', error }]);
  return first;
}
export async function persistCaptureArtifacts(oracle, scratch, output, plan, guard, cleanup, firstException, largeProvenance, options = {}) {
  const io = options.io ?? evidenceIo, budget = createEncodingBudget(plan.limits), secondaryFailures = [...captureEvidenceFailures(firstException)]; let failure = firstException, bundle;
  function failed(stage, error) { if (failure) secondaryFailures.push({ stage, error }); else failure = error; }
  let summary;
  try { summary = guard.lifecycleReceipt(); } catch (error) { failed('native-receipt-state', error); summary = { nativeJoinReceiptStateUnavailable: true, nativeJoinReceiptStateError: failureIdentity(error) }; }
  const lifecycle = cleanup.lifecycle ? Object.fromEntries(['exitCode','signalCode','exitAwaited','closeAwaited','physicalCloseObserved'].map(key => [key,cleanup.lifecycle[key]])) : undefined;
  const receipt = { schemaVersion: 1, kind: 'native-join-before-provenance-encoding', receiptSha256: plan.receipt.sha256, lifecycle, ...summary, nativeFirstFailure: failureIdentity(summary.nativeFirstFailure), nativeFailure: textIdentity(summary.nativeFailure), failure: failureIdentity(failure), nativeCleanup: cleanup.steps.map(row => ({ name: row.name, completed: row.completed, error: failureIdentity(row.error) })), fullCaptureQualified: false, provenanceEncodingStarted: false, laterVerificationAndEncodingRequired: true };
  let receiptPin;
  try { receiptPin = { suffix: '.native-receipt.json', ...writeStreamedEvidence(scratch, output + '.native-receipt.json', [receipt], budget, io) }; delete receiptPin.records; }
  catch (error) { failed('early-native-receipt-write', error); }
  try { await (options.verify ?? verifyOracle)(oracle, plan); }
  catch (error) { failed('after-native-join-verification', error); }
  let partialObservationPin;
  try {
    const partial = options.partialObservation?.();
    if (partial !== undefined) {
      assert(failure && receiptPin, 'Partial observation requires a nonpassing cause and early native receipt');
      partialObservationPin = { suffix: '.partial.json', ...writeStreamedEvidence(scratch, output + '.partial.json',
        [partial], createEncodingBudget(plan.limits), io) };
    }
  } catch (error) { failed('partial-observation-encoding', error); }
  try {
    assert(receiptPin, 'Early native receipt must exist before any large provenance encoding');
    const provenance = { ...largeProvenance(), lifecycle: cleanup.lifecycle, nativeCleanup: cleanup.steps, failure: failure ? { name: failure.name, message: failure.message, stack: failure.stack, code: failure.code, syscall: failure.syscall, path: failure.path, kind: failure.kind } : undefined, secondaryFailures: secondaryFailures.map(row => ({ stage: row.stage, error: { name: row.error.name, message: row.error.message, stack: row.error.stack, code: row.error.code, syscall: row.error.syscall, path: row.error.path, kind: row.error.kind } })), ...(partialObservationPin ? { partialObservationPin } : {}) };
    bundle = writeProvenanceBundle(scratch, output, provenance, budget, receiptPin, io);
  } catch (error) { failed('full-provenance-encoding', error); }
  if (failure) {
    const additional = secondaryFailures.filter(row => !captureEvidenceFailures(failure).includes(row));
    if (additional.length) retainCaptureEvidenceFailures(failure, additional);
    throw failure;
  }
  assert(bundle, 'Complete provenance manifest required'); return bundle;
}
export async function capture(oracle, scratch, output) {
  const plan = readPlan(), guard = globalThis.pisharpFullStructuralOpenaiGuard; assert(guard, 'Reviewed NEW preload required'); const sourceRoot = join(oracle, 'upstream'), sourceRows = guard.verified.originalSourceRows, sourcePaths = [...sourceRows, ...guard.verified.catalogRows].map(row => row.path.slice('upstream/'.length));
  const sourceConfig = JSON.parse(regular(join(sourceRoot, 'tsconfig.json')).toString()), baseConfig = JSON.parse(regular(join(sourceRoot, 'tsconfig.base.json')).toString()), lock = JSON.parse(regular(join(sourceRoot, 'package-lock.json')).toString());
  const files = new Map(guard.verified.receipt.files.map(row => [join(oracle, row.path), regular(join(oracle, row.path))])); const { createReadOnlyFS } = await import('./full-structural-openai-common.mjs'); const vfs = createReadOnlyFS(files, oracle);
  const { API, NodeHandle, SymbolFlags, SignatureKind, ObjectFlags, TypeFlags } = await import(pathToFileURL(join(oracle, 'node_modules/typescript/dist/api/async/api.js')).href);
  const { SyntaxKind } = await import(pathToFileURL(join(oracle, 'node_modules/typescript/dist/ast/index.js')).href);
  let api, view, result, failure, nativeCleanup, structural; const rawHandles = [];
  const snap = (value, label) => snapshot(value, plan.limits, label);
  const handleFields = handle => { if (handle === undefined) return undefined; assert(handle instanceof NodeHandle); return { index: handle.index, kind: handle.kind, path: handle.path, semantics: 'actual public NodeHandle; full wire retained separately' }; };
  const symbolFields = symbol => { const declarations = symbol.declarations.map(handleFields), valueDeclaration = handleFields(symbol.valueDeclaration); rawHandles.push({ id: symbol.id, name: symbol.name, flags: symbol.flags, declarations, valueDeclaration }); return { name: symbol.name, escapedName: symbol.escapedName, flags: symbol.flags, checkFlags: symbol.checkFlags, declarations, valueDeclaration }; };
  try {
    api = new API({ tsserverPath: join(oracle, plan.native.path), cwd: sourceRoot, fs: vfs.fs, collectTiming: false });
    const configPath = join(sourceRoot, 'tsconfig.json'), parsedConfig = await api.parseConfigFile(configPath); view = await api.updateSnapshot({ openProjects: [configPath] }); const project = view.getProject(configPath); assert(project, 'Unchanged upstream project not created'); const { program, checker } = project, diagnostics = {};
    for (const method of plan.diagnosticMethods) { assert.equal(typeof program[method], 'function'); diagnostics[method] = snap(await program[method](), 'diagnostics.' + method); }
    assertDiagnosticFamilies(diagnostics, plan.diagnosticMethods);
    const sourceFiles = await program.getSourceFileNames(), programKeys = new Set(sourceFiles.map(path => resolve(path).toLowerCase()));
    for (const method of requiredCheckerOperations) assert.equal(typeof checker[method], 'function', 'Required real structural operation unavailable: ' + method);
    const astSchema = readPublicAstSchema(oracle, SyntaxKind, plan);
    structural = createStructuralInventory(checker, project, { SymbolFlags, SignatureKind, ObjectFlags, TypeFlags, astSchema }, plan.structuralInventory.limits,
      { isolateSymbolTypePanic: plan.symbolQueryFailureIsolation.enabled });
    const manifestRecords = sourceRows.filter(row => row.path.endsWith('/package.json')).map(row => { const manifest = JSON.parse(files.get(join(oracle, row.path)).toString()); return { path: row.path.slice('upstream/'.length), sha256: row.sha256, manifest }; });
    const baseline = readBaseline(plan), entrypointKey = row => JSON.stringify([row.manifestPath, row.field, row.subpath, row.conditions, row.target]), previousEntrypoints = new Map(baseline.entrypoints.map(row => [entrypointKey(row), row]));
    const originalRootValidation = assertOriginalRoots(project.rootFiles, baseline.rootFiles, sourceRoot, join(plan.origin.root, 'upstream'));
    const rawEntrypoints = manifestRecords.flatMap(({ path, manifest }) => flattenEntrypoints(manifest, path)); assert.equal(rawEntrypoints.length, 150); assert.equal(previousEntrypoints.size, 150);
    const entrypoints = rawEntrypoints.map(row => { const previous = previousEntrypoints.get(entrypointKey(row)); assert(previous, 'All original condition/target rows retained exactly'); const artifact = guard.verified.publishedArtifacts.find(item => item.name === row.package), classified = classifyPackageTarget(row, sourcePaths, artifact); return { ...row, ...classified, frozenPreviousProfileClassification: { canonicalTarget: previous.canonicalTarget, canonicalTargetPresent: previous.canonicalTargetPresent, missingPublishedTargetRemainsBlocker: previous.missingPublishedTargetRemainsBlocker, inferredDistToSourceMapping: previous.inferredDistToSourceMapping } }; });
    assertRequiredPublishedBlockers(entrypoints, plan);
    const requiredPublishedOwnershipBlockers = plan.requiredPublishedOwnershipBlockers.map(required => entrypoints.find(row => entrypointKey(row) === entrypointKey({ ...required, conditions: [] })));
    const buildConfigurations = plan.buildConfigurationPaths.map(path => { const bytes = regular(join(sourceRoot, path)); return { path, bytes: bytes.length, sha256: hash(bytes), rawConfiguration: JSON.parse(bytes.toString('utf8')), configurationExecuted: false, sourceToPublishedOwnershipQualified: false }; });
    const bindings = bindingCandidates(sourceConfig.compilerOptions.paths, sourcePaths), candidates = [...new Set(bindings.flatMap(row => row.patterns.flatMap(pattern => pattern.matching)))].sort(), modules = [];
    for (const path of candidates) { const absolute = join(sourceRoot, path), row = { path, sha256: sourceRows.find(item => item.path === 'upstream/' + path)?.sha256, inProgram: programKeys.has(resolve(absolute).toLowerCase()) };
      if (!row.inProgram) { row.status = 'canonical path match is not in actual program; retained unresolved'; modules.push(row); structural.seedModule(path, undefined, []); continue; }
      const file = await program.getSourceFile(absolute); if (!file) { row.status = 'actual program source unavailable; unresolved'; modules.push(row); structural.seedModule(path, undefined, []); continue; }
      const moduleSymbol = await checker.getSymbolAtLocation(file); row.exportSyntax = [...file.statements].filter(statement => statement.kind === SyntaxKind.ExportDeclaration || statement.kind === SyntaxKind.ExportAssignment).map(statement => ({ kind: statement.kind, pos: statement.pos, end: statement.end, isTypeOnly: statement.isTypeOnly, moduleSpecifierKind: statement.moduleSpecifier?.kind, moduleSpecifier: statement.moduleSpecifier?.text, hasNamedExportClause: Boolean(statement.exportClause), isExportEquals: statement.isExportEquals }));
      if (!moduleSymbol) { row.status = 'source has no actual external-module symbol'; modules.push(row); structural.seedModule(path, undefined, []); continue; } const exports = await checker.getExportsOfModule(moduleSymbol); row.moduleSymbol = symbolFields(moduleSymbol); row.exports = exports.map(symbolFields); row.status = 'actual checker export-symbol table observed; exhaustive structural types/declaration closure still pending'; modules.push(row); structural.seedModule(path, moduleSymbol, exports);
    }
    const dependencyDeclarations = manifestRecords.flatMap(({ path, manifest }) => ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies'].flatMap(group => Object.entries(manifest[group] ?? {}).map(([name, range]) => ({ manifestPath: path, package: manifest.name, group, name, range, optionalPeer: manifest.peerDependenciesMeta?.[name]?.optional, nearestLockCandidate: resolveLock(lock, path, name) }))));
    const unresolved = Object.entries(diagnostics).flatMap(([method, values]) => values.map((value, index) => ({ method, index, value })).filter(row => [2307, 2688, 7016, 2792].includes(row.value.code)).map(row => { const text = typeof row.value.text === 'string' ? row.value.text : typeof row.value.messageText === 'string' ? row.value.messageText : undefined, match = text?.match(/(?:module|definition file for) ['"]([^'"]+)['"]/); const filename = row.value.fileName ?? row.value.file; let from; if (typeof filename === 'string') from = relative(sourceRoot, filename).split(sep).join('/'); return { ...row, parsedSpecifier: match?.[1], nearestLockCandidate: match ? resolveLock(lock, from ?? 'package.json', match[1]) : undefined, classificationComplete: false }; }));
    const admittedMemberKeys = new Set(guard.verified.addedFiles.map(row => resolve(join(oracle, row.path)).toLowerCase()));
    const sdkMemberKeys = new Set(guard.verified.sdkArtifact.archive.files.map(row => resolve(join(oracle, guard.verified.sdkArtifact.target, row.path)).toLowerCase()));
    const admittedMembersConsumedInProgram = sourceFiles.filter(path => admittedMemberKeys.has(resolve(path).toLowerCase()));
    const sourceDataAdmission = { receiptSha256: plan.origin.receiptSha256, ...guard.verified.sourceArtifact, actualProgramCatalogMembers: sourceFiles.filter(path => guard.verified.catalogRows.some(row => resolve(join(oracle, row.path)).toLowerCase() === resolve(path).toLowerCase())), dataImportedAsRuntimeCode: false, diagnosticReductionPromised: false }; const declarationAdmission = { receiptSha256: plan.receipt.sha256, payloadFiles: guard.verified.receipt.files.length, addedFiles: guard.verified.addedFiles, packages: guard.verified.packages.map(row => ({ name: row.name, version: row.version, lockPath: row.lockPath, target: row.target, archive: { path: row.archive.path, url: row.archive.url, bytes: row.archive.bytes, sha256: row.archive.sha256, integrity: row.archive.integrity }, manifest: row.manifest, notices: row.notices, declarationFiles: row.declarationFiles ?? row.declarations.length })), admittedMembersConsumedInProgram, declarationsConsumedInProgram: admittedMembersConsumedInProgram.filter(path => /\.d\.[cm]?ts$/.test(path)), packageRuntimeExecuted: false, referenceCensusSemanticClosure: false };
    const sdkDeclarationResolution = { receiptSha256: plan.receipt.sha256, artifact: guard.verified.sdkArtifact, actualProgramSdkMembers: sourceFiles.filter(path => sdkMemberKeys.has(resolve(path).toLowerCase())), originalOptionalPeersRetained: true, optionalPeersAreNotExemptions: true, missingCanonicalPeerInstances: plan.sdkDeclarationResolution.missingCanonicalPeerInstances, packageImportTypesConditionsRetained: true, sourceOrConfigChanges: false, packageRuntimeExecuted: false, resolutionGraphClosureQualified: false, publicOwnershipQualificationPending: true };
    assert.equal(modules.length, 800); structural.seedEntrypoints(entrypoints);
    const structuralTypeInventory = await structural.finish();
    structuralTypeInventory.publicCheckerOperations = requiredCheckerOperations.map(method => ({ method, methodAvailableOnActualChecker: typeof checker[method] === 'function', executions: structuralTypeInventory.operationCounts.find(row => row.method === method)?.executed ?? 0 }));
    structuralTypeInventory.capabilityGaps = plan.structuralInventory.unsupportedCheckerFields.map(field => ({ field, basis: 'hash-pinned native public declarations; type-specific observations carry actual affected graph refs', permanentExemption: false }));
    structuralTypeInventory.sourceAndPublishedOwnershipQualificationPending = true;
    result = { schemaVersion: 1, kind: 'genuine-unchanged-upstream-config-openai-recursive-structural-observation', sourceSha: plan.sourceCommit, sourceConfig: snap(sourceConfig, 'sourceConfig'), baseConfig: snap(baseConfig, 'baseConfig'), originalConfigExclusionsAreNotCompatibilityExclusions: true, parsedConfig: snap(parsedConfig, 'parsedConfig'), compilerOptions: snap(program.getCompilerOptions(), 'compilerOptions'), rootFiles: snap(project.rootFiles, 'rootFiles'), originalRootValidation, mandatoryOriginalRootFiles: snap(baseline.rootFiles, 'mandatoryOriginalRootFiles'), sourceFiles: snap(sourceFiles, 'sourceFiles'), programDeclarationFiles: sourceFiles.filter(path => /\.d\.[cm]?ts$/.test(path)), diagnostics, declarationAdmission, sourceDataAdmission, sdkDeclarationResolution, structuralTypeInventory, publishedArtifacts: guard.verified.publishedArtifacts, requiredPublishedOwnershipBlockers, buildConfigurations, mandatoryBaselineLockInstances: mandatoryLockInventory(readBaseline(plan), plan), originalCanonicalSourceRows: sourceRows.length, manifestRecords: manifestRecords.map(({ path, sha256, manifest }) => ({ path, sha256, name: manifest.name, version: manifest.version, private: manifest.private, exports: manifest.exports, main: manifest.main, types: manifest.types, typings: manifest.typings, source: manifest.source, bin: manifest.bin })), entrypoints, bindings, modules, dependencyDeclarations, unresolved, wholeConfigurationOpenedUnchanged: true, upstreamModulesExecuted: false, diagnosticFiltering: false, pathNormalization: false, fullStructuralTypeInventoryPending: true, semanticPublicClosure: false, phaseGatesPassed: [] };
    assertRetainedObservation(result, readCurrentBaseline(plan));
    failure = structural.isolatedFailure();
  } catch (error) { failure = retainStructuralFailure(structural, error); }
  finally { nativeCleanup = await finishNativeSession(view, api, guard); failure ??= nativeCleanup.failure; }
  await persistCaptureArtifacts(oracle, scratch, output, plan, guard, nativeCleanup, failure, () => ({ schemaVersion: 1, profile: 'unchanged-upstream-config-openai-recursive-structural', receiptSha256: plan.receipt.sha256, verificationRepoPins: guard.verified.verificationRepoPins, nativeApiDeclarationPins: guard.verified.nativeApiDeclarationPins, ...guard.provenance(), rawHandles, structuralGraph: structural?.provenance(), fsRequests: vfs.requests, deniedFsRequests: vfs.denied }),
    { partialObservation: () => failure && structural ? { schemaVersion: 1, kind: 'incomplete-recursive-structural-observation',
      fullStructuralInventoryQualified: false, semanticPublicClosure: false, fullP1Verdict: 'HOLD', phaseGatesPassed: [],
      observation: result ?? { structuralTypeInventory: structural.partialObservation() } } : undefined });
  writeStreamedEvidence(scratch, output, [result], createEncodingBudget(plan.limits));
}
export async function captureMinimalPanic(oracle, scratch, output) {
  const plan = readPlan(), guard = globalThis.pisharpFullStructuralOpenaiGuard;
  assert(guard, 'Existing bounded read-only preload required');
  const sourceRoot = join(oracle, 'upstream'), sourcePath = join(sourceRoot, 'packages/coding-agent/src/core/tools/find.ts');
  assert.equal(hash(regular(sourcePath)), 'b06bcae6821a0e9fda1b63be613a7ce28eb0e66e1d01d16564e59e83f9ce10ea');
  const files = new Map(guard.verified.receipt.files.map(row => [join(oracle, row.path), regular(join(oracle, row.path))]));
  const { createReadOnlyFS } = await import('./full-structural-openai-common.mjs'), vfs = createReadOnlyFS(files, oracle);
  const { API } = await import(pathToFileURL(join(oracle, 'node_modules/typescript/dist/api/async/api.js')).href);
  const { SyntaxKind } = await import(pathToFileURL(join(oracle, 'node_modules/typescript/dist/ast/index.js')).href);
  const observation = { kind: 'bounded-same-pinned-native-api-panic-preflight', sourceSha: plan.sourceCommit,
    sourcePath, sourceSha256: hash(regular(sourcePath)), queries: [], nativeIdsResolvedFresh: true,
    fullStructuralInventoryQualified: false, semanticPublicClosure: false, fullP1Verdict: 'HOLD' };
  let api, view, failure, cleanup;
  async function query(method, context, action) {
    assert(observation.queries.length < 16, 'Minimal API query bound');
    const row = { ordinal: observation.queries.length, method, context, state: 'requested' }; observation.queries.push(row);
    try { const value = await action(); row.state = value === undefined ? 'undefined' : value === null ? 'null' : 'returned'; return value; }
    catch (error) { row.state = 'failed'; row.failure = { name: error.name, message: error.message, code: error.code }; throw error; }
  }
  try {
    api = new API({ tsserverPath: join(oracle, plan.native.path), cwd: sourceRoot, fs: vfs.fs, collectTiming: false });
    const configPath = join(sourceRoot, 'tsconfig.json');
    await query('parseConfigFile', { configPath }, () => api.parseConfigFile(configPath));
    view = await query('updateSnapshot', { configPath }, () => api.updateSnapshot({ openProjects: [configPath] }));
    const project = view.getProject(configPath); assert(project, 'Actual unchanged project required');
    const file = await query('getSourceFile', { sourcePath }, () => project.program.getSourceFile(sourcePath));
    assert(file, 'Actual public source AST required');
    const declarations = Array.from(file.statements).filter(node => node.kind === SyntaxKind.VariableStatement)
      .flatMap(node => Array.from(node.declarationList.declarations)).filter(node => node.name?.text === 'findToolSystemPromptContribution');
    assert.equal(declarations.length, 1, 'Unique actual contribution declaration required');
    const declaration = declarations[0], assertion = declaration.initializer;
    assert.equal(assertion.kind, SyntaxKind.AsExpression); assert.equal(assertion.type.kind, SyntaxKind.TypeReference);
    assert.equal(assertion.type.typeName.text, 'const');
    observation.actualTypeNode = { id: assertion.type.id, kind: assertion.type.kind, pos: assertion.type.pos, end: assertion.type.end,
      sourceFileName: assertion.type.getSourceFile().fileName, declarationName: declaration.name.text };
    const type = await query('getTypeFromTypeNode', observation.actualTypeNode, () => project.checker.getTypeFromTypeNode(assertion.type));
    assert(type, 'Actual assertion type required'); observation.actualOwnerType = { id: type.id, flags: type.flags, objectFlags: type.objectFlags };
    const properties = await query('getPropertiesOfType', { nativeTypeId: type.id }, () => project.checker.getPropertiesOfType(type));
    observation.actualProperties = properties.map(symbol => ({ id: symbol.id, name: symbol.name, flags: symbol.flags,
      declarations: symbol.declarations.map(handle => ({ index: handle.index, kind: handle.kind, path: handle.path })) }));
    const symbols = properties.filter(symbol => symbol.name === 'guidelines'); assert.equal(symbols.length, 1, 'Unique fresh guidelines symbol required');
    observation.actualTargetSymbolId = symbols[0].id;
    const result = await query('getTypeOfSymbol', { nativeSymbolId: symbols[0].id, name: symbols[0].name }, () => project.checker.getTypeOfSymbol(symbols[0]));
    observation.actualTargetType = result ? { id: result.id, flags: result.flags, objectFlags: result.objectFlags } : null;
  } catch (error) { failure = error; }
  finally { cleanup = await finishNativeSession(view, api, guard); failure ??= cleanup.failure; }
  await persistCaptureArtifacts(oracle, scratch, output, plan, guard, cleanup, failure, () => ({
    schemaVersion: 1, profile: 'bounded-same-pinned-native-api-panic-preflight', receiptSha256: plan.receipt.sha256,
    verificationRepoPins: guard.verified.verificationRepoPins, nativeApiDeclarationPins: guard.verified.nativeApiDeclarationPins,
    ...guard.provenance(), minimalQueryObservation: observation, fsRequests: vfs.requests, deniedFsRequests: vfs.denied,
    fullStructuralInventoryQualified: false, semanticPublicClosure: false, fullP1Verdict: 'HOLD' }));
  writeStreamedEvidence(scratch, output, [observation], createEncodingBudget(plan.limits));
}
if (process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_CHILD === '1') {
  const operation = process.argv.slice(2).includes('--minimal-panic') ? captureMinimalPanic : capture;
  operation(resolve(process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_ORACLE), resolve(process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_SCRATCH), resolve(process.env.PISHARP_SEMANTIC_OPENAI_STRUCTURAL_OUTPUT)).catch(error => { console.error(error?.stack ?? String(error)); for (const row of captureEvidenceFailures(error)) console.error('Secondary capture failure at ' + row.stage + ':\n' + (row.error?.stack ?? String(row.error))); process.exitCode = 1; });
}
