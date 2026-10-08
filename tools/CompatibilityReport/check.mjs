import { currentPublicHarnessPin, publicDerivativeVerificationScope } from '../PublicDerivativeIntegrity.mjs';
import { qualifiedReferenceFile } from '../PublicReferenceLayout.mjs';
import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, realpathSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { NORMALIZER_VERSION } from "./canonical-json.mjs";
import { canonicalRawJson, compareRawJson, parseJsonSupported } from "./raw-json.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const args = process.argv.slice(2);
function argument(name) { const index = args.indexOf(name); return index < 0 ? undefined : args[index + 1]; }
const upstream = argument("--upstream");
const output = argument("--out");
const checks = [];
const sdkManifestPath = "fixtures/pi-v0.99.1/responses-sdk/manifest.json";
const toolsSdkManifestPath = "fixtures/pi-v0.99.1/responses-tools-sdk/manifest.json";
const fileQueueManifestPath = "fixtures/pi-v0.99.1/file-mutation-queue/manifest.json";
const sessionManifestPath = "fixtures/pi-v0.99.1/session-context/manifest.json";
const rpcManifestPath = "fixtures/pi-v0.99.1/rpc-jsonl/manifest.json";
const editManifestPath = "fixtures/pi-v0.99.1/edit/manifest.json";
const anthropicManifestPath = "fixtures/pi-v0.99.1/anthropic-sdk/manifest.json";
const agentProgressManifestPath = "fixtures/pi-v0.99.1/agent-progress/manifest.json";
const sdkKind = "captured-unchanged-responses-wrapper-sdk-oracle";
const toolsSdkKind = "captured-unchanged-responses-tools-wrapper-sdk-oracle";
const fileQueueKind = "captured-whole-file-mutation-queue-oracle";
const sessionKind = "captured-unchanged-session-context-oracle";
const rpcKind = "captured-whole-rpc-jsonl-oracle";
const editKind = "captured-unchanged-edit-oracle";
const anthropicKind = "captured-unchanged-anthropic-wrapper-sdk-oracle";
const anthropicNormalizer = "raw-json-v1-lossless-numeric-lexemes-object-key-order-only";
const agentProgressKind = "captured-unchanged-whole-agent-progress-oracle";
const agentProgressNormalizer = "raw-json-v1-key-order-only-with-numeric-lexemes";
const sdkNormalizer = `${NORMALIZER_VERSION}; raw-body-and-headers-retained`;
const fileQueueNormalizer = `${NORMALIZER_VERSION}; callback-values-and-error-fields-retained`;
const sessionNormalizer = `${NORMALIZER_VERSION}; raw-json-scalars-arrays-and-opaque-fields-retained`;
const rpcNormalizer = `${NORMALIZER_VERSION}; callback-strings-wire-bytes-and-array-order-retained`;
const editNormalizer = "raw-json-object-key-order-v1-lossless-numeric-lexemes";
const standaloneProfiles = new Map([
  [sdkManifestPath, { fixtureId: "responses-sdk-core", kind: sdkKind, normalizer: sdkNormalizer }],
  [toolsSdkManifestPath, { fixtureId: "responses-tools-sdk-core", kind: toolsSdkKind, normalizer: sdkNormalizer }],
  [fileQueueManifestPath, { fixtureId: "file-mutation-queue-core", kind: fileQueueKind, normalizer: fileQueueNormalizer }],
  [sessionManifestPath, { fixtureId: "session-context-core", kind: sessionKind, normalizer: sessionNormalizer }],
  [rpcManifestPath, { fixtureId: "rpc-jsonl-core", kind: rpcKind, normalizer: rpcNormalizer }],
  [editManifestPath, { fixtureId: "edit-core", kind: editKind, normalizer: editNormalizer }],
  [anthropicManifestPath, { fixtureId: "anthropic-sdk-direct-stream-three-requests", kind: anthropicKind, normalizer: anthropicNormalizer }],
  [agentProgressManifestPath, { fixtureId: "whole-agent-progress-metadata-v1", kind: agentProgressKind, normalizer: agentProgressNormalizer }]
]);
// These are frozen fixture labels, not additions to the canonical requirement inventory.
const nonInventoryLabels = new Map([
  [fileQueueManifestPath, new Set(["P3-05", "tools.file-mutation-ordering"])],
  [toolsSdkManifestPath, new Set(["tools.declaration-replay"])],
  [sessionManifestPath, new Set(["P4-03", "P4-04", "P4-06"])],
  [editManifestPath, new Set(["P1-05", "P1-06", "P3-05", "P3-06"])],
  [agentProgressManifestPath, new Set(["P1-06", "P1-07", "P1-08", "P3-01", "P3-03"])]
]);
const physicalRepo = realpathSync(repo);
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
function requireCondition(condition, name) {
  checks.push({ name, passed: Boolean(condition) });
  if (!condition) throw new Error(name);
}
function localPath(path) {
  if (typeof path !== "string" || !path || isAbsolute(path)) throw new Error(`Evidence path must be repository relative: ${path}`);
  const full = resolve(repo, path);
  const within = relative(repo, full);
  if (!within || isAbsolute(within) || within === ".." || within.startsWith(`..${sep}`)) throw new Error(`Evidence path escapes repository: ${path}`);
  if (existsSync(full)) {
    const physicalWithin = relative(physicalRepo, realpathSync(full));
    if (!physicalWithin || isAbsolute(physicalWithin) || physicalWithin === ".." || physicalWithin.startsWith(`..${sep}`)) throw new Error(`Evidence path escapes repository through a link: ${path}`);
  }
  return full;
}
function read(path) { return parseJsonSupported(readFileSync(localPath(path), "utf8")); }

function discoverManifests(directory = localPath("fixtures/pi-v0.99.1")) {
  const paths = [];
  for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0)) {
    if (entry.isSymbolicLink()) throw new Error(`Symbolic link in fixture evidence tree: ${entry.name}`);
    const path = join(directory, entry.name);
    if (entry.isDirectory()) paths.push(...discoverManifests(path));
    else if (entry.isFile() && entry.name === "manifest.json") paths.push(relative(repo, path).split(sep).join("/"));
  }
  return paths.sort();
}

// Operates on already validated canonical JSON. Only selected root metadata is converted
// to runtime values; wide numeric corpus values remain raw lexemes throughout checking.
function rootMembers(canonical) {
  if (!canonical.startsWith("{")) throw new Error("Fixture evidence root must be a JSON object");
  const members = new Map();
  function stringEnd(start) {
    let end = start + 1;
    while (canonical[end] !== '"') end += canonical[end] === "\\" ? 2 : 1;
    return end + 1;
  }
  let offset = 1;
  while (canonical[offset] !== "}") {
    const keyEnd = stringEnd(offset);
    const key = parseJsonSupported(canonical.slice(offset, keyEnd));
    const start = keyEnd + 1;
    let end = start, depth = 0;
    for (; end < canonical.length; end++) {
      const character = canonical[end];
      if (character === '"') end = stringEnd(end) - 1;
      else if (character === "{" || character === "[") depth++;
      else if (character === "}" || character === "]") { if (depth === 0) break; depth--; }
      else if (character === "," && depth === 0) break;
    }
    members.set(key, canonical.slice(start, end));
    if (canonical[end] === "}") break;
    offset = end + 1;
  }
  return members;
}

function fixtureDocument(bytes) {
  const raw = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  const members = rootMembers(canonicalRawJson(raw));
  const selected = ["fixtureId", "sourceSha", "kind", "normalizerVersion", "provenance", "schemaVersion", "scope", "inputOrigin", "inputSha256"]
    .filter(key => members.has(key)).map(key => `${JSON.stringify(key)}:${members.get(key)}`);
  return { raw, members, metadata: parseJsonSupported(`{${selected.join(",")}}`) };
}

const captureKinds = new Map([
  ["captured-upstream-oracle", { inputKind: "upstream-event-stream-input", legacyExpected: true }],
  ["captured-upstream-agent-oracle", { inputKind: "authored-full-agent-input" }],
  ["captured-upstream-frame-oracle", { inputKind: "authored-frame-event-input" }],
  ["captured-upstream-json-preview-oracle", { inputKind: "authored-json-preview-input" }],
  ["captured-upstream-responses-oracle", { inputKind: "authored-parsed-responses-wire-input" }],
  ["captured-upstream-responses-replay-oracle", { inputKind: "authored-responses-transcript-input" }],
  ["captured-upstream-loop-continuation-oracle", { inputKind: "authored-loop-continuation-input" }],
  ["captured-upstream-finish-decisions-oracle", { inputKind: "authored-finish-decisions-input", provenanceInputKind: "authored-finish-decisions-input" }],
  ["captured-upstream-tool-truncation-oracle", { sourceOnly: true }]
]);

function sameMetadata(left, right) {
  return left !== undefined && right !== undefined && compareRawJson(JSON.stringify(left), JSON.stringify(right));
}

function pinnedBytes(reference, name, requireSize = false) {
  requireCondition(reference && typeof reference.sha256 === "string" && /^[a-f0-9]{64}$/.test(reference.sha256), `${name} digest declaration`);
  const bytes = readFileSync(localPath(reference.path));
  requireCondition(hash(bytes) === reference.sha256, `${name} SHA256`);
  if (requireSize || Object.hasOwn(reference, "bytes"))
    requireCondition(Number.isSafeInteger(reference.bytes) && reference.bytes >= 0 && reference.bytes === bytes.length, `${name} byte length`);
  return bytes;
}

function rawPair(fixture) {
  requireCondition(Array.isArray(fixture.rawCaptures) && fixture.rawCaptures.length === 2 && new Set(fixture.rawCaptures.map(row => row.path)).size === 2, `Two distinct retained raw captures ${fixture.fixtureId}`);
  const captures = fixture.rawCaptures.map((row, index) => {
    const bytes = pinnedBytes(row, `Retained raw capture ${index + 1} ${fixture.fixtureId}`, true);
    return { bytes, document: fixtureDocument(bytes) };
  });
  requireCondition(captures[0].bytes.equals(captures[1].bytes), `Retained raw captures byte-identical ${fixture.fixtureId}`);
  return captures;
}

function finishDecisionEvidence(fixture, expectedDocument, lock, baseline) {
  const name = fixture.fixtureId;
  requireCondition(lock.captureKind === fixture.provenance.kind && lock.environmentLockPath === fixture.provenance.environmentLock, `Finish-decision lock identity ${name}`);
  const environmentBytes = pinnedBytes({ path: lock.environmentLockPath, sha256: lock.environmentLockSha256 }, `Finish-decision environment lock ${name}`);
  const environment = parseJsonSupported(fixtureDocument(environmentBytes).raw);
  requireCondition(environment.schemaVersion === 1 && sameMetadata(lock.environmentPins, environment.environmentPins) && lock.environmentPins.sourceSha === baseline.source.commit && lock.environmentPins.sourceTree === baseline.source.tree && lock.environmentPins.runtime.version === baseline.referenceRuntime.version && lock.environmentPins.runtime.sha256 === baseline.referenceRuntime.sha256, `Finish-decision environment pins ${name}`);
  const harnessPaths = ["tools/PiReferenceRunner/capture-finish-decisions.mjs", "tools/PiReferenceRunner/full-preload.mjs", "tools/PiReferenceRunner/offline-guard.mjs", "tools/CompatibilityReport/raw-json.mjs"];
  requireCondition(Array.isArray(lock.harnessFiles) && lock.harnessFiles.length === harnessPaths.length && new Set(lock.harnessFiles.map(row => row.path)).size === harnessPaths.length && harnessPaths.every(path => lock.harnessFiles.some(row => row.path === path)), `Finish-decision harness inventory ${name}`);
  for (const file of lock.harnessFiles) {
    pinnedBytes(currentPublicHarnessPin(file), `Finish-decision harness ${file.path}`);
    if (file.path !== harnessPaths[0]) requireCondition(sameMetadata(file, environment.harnessFiles.find(row => row.path === file.path)), `Finish-decision reused harness pin ${file.path}`);
  }
  requireCondition(Array.isArray(lock.loadedModules) && lock.loadedModules.length > 0 && new Set(lock.loadedModules.map(row => row.path)).size === lock.loadedModules.length && lock.loadedModules.every(row => environment.loadedModules.some(qualified => sameMetadata(row, qualified))), `Finish-decision recorded module pins ${name}`);
  const captures = rawPair(fixture);
  for (const { document } of captures) {
    requireCondition(document.members.has("observations") && compareRawJson(document.members.get("observations"), expectedDocument.members.get("observations")), `Finish-decision retained observations ${name}`);
    requireCondition(document.members.has("loadedModules") && compareRawJson(document.members.get("loadedModules"), JSON.stringify(lock.loadedModules)), `Finish-decision retained module pins ${name}`);
  }
}

// Split only already validated canonical arrays. Corpus strings and numeric values
// stay raw; only case IDs, categories and derived integrity counts become runtime values.
function rawArrayItems(canonical) {
  if (typeof canonical !== "string" || !canonical.startsWith("[")) throw new Error("Fixture evidence member must be a JSON array");
  const items = [];
  let start = 1, depth = 0;
  for (let offset = 1; offset < canonical.length; offset++) {
    const character = canonical[offset];
    if (character === '"') {
      for (offset++; canonical[offset] !== '"'; offset++) if (canonical[offset] === "\\") offset++;
    } else if (character === "{" || character === "[") depth++;
    else if (character === "}" || character === "]") {
      if (depth === 0) { if (offset > start) items.push(canonical.slice(start, offset)); break; }
      depth--;
    } else if (character === "," && depth === 0) {
      items.push(canonical.slice(start, offset));
      start = offset + 1;
    }
  }
  return items;
}

function hasKeys(members, keys) {
  return members.size === keys.length && keys.every(key => members.has(key));
}

function truncationEvidence(fixture, inputDocument, expectedDocument, payloads, baseline) {
  const name = fixture.fixtureId, provenance = fixture.provenance;
  const scope = "pure-head-tail-tool-output-truncation", origin = "PiSharp-authored synthetic boundary corpus";
  requireCondition(provenance.inputKind === "authored-tool-truncation-input" && provenance.inputOrigin === origin && provenance.sourceSha === baseline.source.commit && provenance.source && provenance.captureCommand && provenance.capturedAt && !Object.hasOwn(provenance, "dependencyLock") && !Object.hasOwn(provenance, "environmentLock"), `Truncation source-only provenance ${name}`);
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "sourceSha", "inputOrigin", "cases"]) && inputDocument.metadata.schemaVersion === 1 && inputDocument.metadata.sourceSha === baseline.source.commit && inputDocument.metadata.inputOrigin === origin, `Truncation input schema ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "sourceSha", "scope", "inputSha256", "observations"]) && expectedDocument.metadata.schemaVersion === 1 && expectedDocument.metadata.sourceSha === baseline.source.commit && expectedDocument.metadata.scope === scope && expectedDocument.metadata.inputSha256 === fixture.input.sha256, `Truncation expected schema/input digest ${name}`);
  const recordBytes = pinnedBytes(provenance.record, `Truncation provenance record ${name}`);
  const record = parseJsonSupported(fixtureDocument(recordBytes).raw);
  requireCondition(record.schemaVersion === 1 && record.sourceRepository === baseline.source.repository && record.sourceSha === baseline.source.commit && record.scope === scope && record.inputOrigin === origin && sameMetadata(record.input, fixture.input), `Truncation provenance record identity/input ${name}`);
  requireCondition(provenance.runner?.path === "tools/PiReferenceRunner/capture-tool-truncation.mjs" && sameMetadata(record.runner, provenance.runner), `Truncation runner pin ${name}`);
  pinnedBytes(provenance.runner, `Truncation runner ${name}`);
  const runtime = record.runtime;
  requireCondition(sameMetadata(runtime, provenance.runtime) && runtime.version === baseline.referenceRuntime.version && runtime.sha256 === baseline.referenceRuntime.sha256 && runtime.platform === baseline.referenceRuntime.platform && runtime.architecture === baseline.referenceRuntime.architecture && sameMetadata(runtime.flags, ["--experimental-strip-types", "--disable-warning=ExperimentalWarning"]), `Truncation exact runtime pin ${name}`);
  requireCondition(typeof runtime.path === "string" && isAbsolute(runtime.path) && realpathSync(runtime.path).toLowerCase() === realpathSync(process.execPath).toLowerCase(), `Truncation runtime executable identity ${name}`);
  const modulePath = "packages/coding-agent/src/core/tools/truncate.ts";
  const source = baseline.artifacts.find(row => row.kind === "source-file" && row.path === modulePath);
  const closure = record.dependencyClosure;
  requireCondition(source && sameMetadata(provenance.sourceModules, [{ path: modulePath, sha256: source.sha256 }]) && sameMetadata(closure?.sourceModules, provenance.sourceModules) && sameMetadata(closure.sourceImports, []) && sameMetadata(closure.externalPackages, []) && sameMetadata(closure.builtinsUsedBySource, ["Buffer"]) && closure.copiedOrExtractedSource === false && sameMetadata(closure.sourcePatches, []) && sameMetadata(closure.resolutionShims, []) && sameMetadata(provenance.capturedMethods, ["truncateHead", "truncateTail"]), `Truncation import-free source module pin ${name}`);
  const sourceCheck = { revision: baseline.source.commit, status: "", sourceSha256: source.sha256 };
  requireCondition(sameMetadata(record.sourceChecks?.before, sourceCheck) && sameMetadata(record.sourceChecks?.after, sourceCheck), `Truncation recorded clean source checks ${name}`);
  requireCondition(record.independentCaptureProcesses === 2 && provenance.independentCaptureProcesses === 2 && record.capturesByteIdentical === true && provenance.capturesByteIdentical === true && sameMetadata(record.normalization, []) && sameMetadata(provenance.normalization, []) && provenance.providerWireTraffic === false && provenance.nativeDifferential === false && provenance.toolEffectsImplemented === false && provenance.phaseAcceptanceClaimed === false, `Truncation capture scope ${name}`);
  const captures = rawPair(fixture);
  requireCondition(Number.isSafeInteger(fixture.expected.bytes) && fixture.expected.bytes === payloads.expected.length && captures.every(row => row.bytes.equals(payloads.expected)), `Truncation expected equals retained raw captures ${name}`);
  const retained = [...fixture.rawCaptures, fixture.expected];
  requireCondition(Array.isArray(record.captures) && record.captures.length === retained.length && record.captures.every((row, index) => row.path === basename(retained[index].path) && dirname(retained[index].path) === dirname(provenance.record.path) && row.sha256 === retained[index].sha256 && row.bytes === retained[index].bytes), `Truncation recorded capture cross-hashes ${name}`);
  const inputs = rawArrayItems(inputDocument.members.get("cases"));
  const observations = rootMembers(expectedDocument.members.get("observations"));
  requireCondition(hasKeys(observations, ["cases", "checks"]) && inputs.length > 0, `Truncation observations schema ${name}`);
  const results = rawArrayItems(observations.get("cases")), identities = [], categories = [];
  requireCondition(results.length === inputs.length, `Truncation input/result case count ${name}`);
  const resultFields = ["content", "truncated", "truncatedBy", "totalLines", "totalBytes", "outputLines", "outputBytes", "lastLinePartial", "firstLineExceedsLimit", "maxLines", "maxBytes"];
  for (let index = 0; index < inputs.length; index++) {
    const input = rootMembers(inputs[index]), result = rootMembers(results[index]);
    const caseId = parseJsonSupported(input.get("caseId")), category = parseJsonSupported(input.get("category"));
    requireCondition(hasKeys(input, input.has("options") ? ["caseId", "category", "content", "options"] : ["caseId", "category", "content"]) && typeof caseId === "string" && caseId.length > 0 && typeof category === "string" && category.length > 0 && input.get("content")?.startsWith('"'), `Truncation authored case schema ${name} ${index}`);
    if (input.has("options")) {
      const options = rootMembers(input.get("options"));
      requireCondition([...options].every(([key, value]) => ["maxLines", "maxBytes"].includes(key) && (value === "null" || (/^(0|[1-9][0-9]*)$/.test(value) && Number(value) <= 2147483647))), `Truncation authored budget schema ${name} ${index}`);
    }
    requireCondition(hasKeys(result, ["caseId", "category", "head", "tail"]) && result.get("caseId") === input.get("caseId") && result.get("category") === input.get("category"), `Truncation ordered case identity ${name} ${index}`);
    for (const method of ["head", "tail"]) {
      const fields = rootMembers(result.get(method));
      requireCondition(hasKeys(fields, resultFields) && fields.get("content")?.startsWith('"') && ["truncated", "lastLinePartial", "firstLineExceedsLimit"].every(key => ["true", "false"].includes(fields.get(key))) && ["null", '"lines"', '"bytes"'].includes(fields.get("truncatedBy")) && ["totalLines", "totalBytes", "outputLines", "outputBytes", "maxLines", "maxBytes"].every(key => /^(0|[1-9][0-9]*)$/.test(fields.get(key))), `Truncation eleven-field result schema ${name} ${index} ${method}`);
    }
    identities.push(caseId); categories.push(category);
  }
  requireCondition(new Set(identities).size === identities.length, `Truncation unique case IDs ${name}`);
  const counts = parseJsonSupported(observations.get("checks"));
  requireCondition(counts.capturedCaseCount === inputs.length && counts.capturedResultCount === inputs.length * 2 && provenance.capturedCaseCount === inputs.length && provenance.observedResultCount === inputs.length * 2 && record.caseCount === inputs.length && sameMetadata(counts.categories, [...new Set(categories)].sort()) && counts.sourceUnchanged === true && counts.resultFieldsExact === true && sameMetadata(counts.normalization, []), `Truncation derived capture counts ${name}`);
}

function fileMutationQueueEvidence(fixture, inputDocument, expectedDocument, baseline) {
  const name = fixture.fixtureId, family = dirname(fileQueueManifestPath).split(sep).join("/"), provenance = fixture.provenance;
  const modulePath = "packages/coding-agent/src/core/tools/file-mutation-queue.ts";
  // This additional canonical module was frozen with the source-only capture. It
  // does not extend the baseline's separately qualified source-file inventory.
  const source = { path: modulePath, bytes: 1697, sha256: "33cb06ac9bcdf32c8b84d9d12e33be44c503a7670f668e003a3262cd34294d11" };
  requireCondition(name === "file-mutation-queue-core" && fixture.input.kind === "authored-filesystem-queue-probes" && fixture.expected.kind === fileQueueKind && fixture.input.path === `${family}/core.input.json` && fixture.expected.path === `${family}/core.expected.json` && fixture.lock?.path === `${family}/oracle.lock.json` && !Object.hasOwn(provenance, "kind") && !Object.hasOwn(provenance, "dependencyLock") && !Object.hasOwn(provenance, "environmentLock") && provenance.source === modulePath && provenance.sourceSha256 === source.sha256 && provenance.repeatRuns === 2 && provenance.byteIdentical === true && ["authoredExpectedOutput", "sourceModified", "privateFunctionExtraction", "dependenciesInstalled", "networkOrProviderCalls", "actualTemporaryPathsReturnedBySource"].every(key => provenance[key] === false) && provenance.mechanism && provenance.observationProjection && provenance.captureCommand && fixture.scope, `File queue source-only manifest identity/provenance ${name}`);
  const input = inputDocument.metadata, expected = expectedDocument.metadata;
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "requirementIds", "normalizerVersion", "scenario", "clock", "seed", "fileUtf8", "callbackFailure", "invalidResolverPath", "cases", "provenance"]) && input.schemaVersion === 1 && input.fixtureId === name && input.sourceSha === baseline.source.commit && input.kind === fixture.input.kind && input.normalizerVersion === fileQueueNormalizer && input.provenance?.kind === "authored-synthetic-input" && compareRawJson(inputDocument.members.get("requirementIds"), JSON.stringify(["P3-05", "tools.file-mutation-ordering"])) && sameMetadata(fixture.requirementIds, ["P3-05", "tools.file-mutation-ordering"]) && compareRawJson(inputDocument.members.get("clock"), JSON.stringify(fixture.clock)) && compareRawJson(inputDocument.members.get("seed"), JSON.stringify(fixture.seed)) && inputDocument.members.get("scenario")?.startsWith('"'), `File queue authored input identity ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expected.schemaVersion === 1 && expected.fixtureId === name && expected.sourceSha === baseline.source.commit && expected.kind === fileQueueKind, `File queue expected envelope schema ${name}`);
  const lock = parseJsonSupported(fixtureDocument(pinnedBytes(fixture.lock, `File queue oracle lock ${name}`)).raw), pins = lock.environmentPins;
  requireCondition(sameMetadata(Object.keys(lock).sort(), ["schemaVersion", "environmentPins", "harnessFiles", "loadedModules", "loadedBuiltins"].sort()) && lock.schemaVersion === 1 && pins?.sourceSha === baseline.source.commit && pins.sourceTree === baseline.source.tree && sameMetadata(pins.executedSource, { ...source, canonicalGitBlobSha256: source.sha256 }) && sameMetadata(pins.runtime, { version: baseline.referenceRuntime.version, sha256: baseline.referenceRuntime.sha256 }) && pins.platform === baseline.referenceRuntime.platform && pins.architecture === baseline.referenceRuntime.architecture && sameMetadata(pins.externalDependencies, []) && sameMetadata(Object.keys(pins).sort(), ["sourceSha", "sourceTree", "executedSource", "runtime", "platform", "architecture", "externalDependencies"].sort()), `File queue exact source/runtime/empty-dependency pins ${name}`);
  const harnessPaths = ["tools/PiReferenceRunner/capture-file-mutation-queue.mjs", "tools/PiReferenceRunner/offline-guard.mjs", "tools/CompatibilityReport/raw-json.mjs"];
  requireCondition(Array.isArray(lock.harnessFiles) && lock.harnessFiles.length === harnessPaths.length && new Set(lock.harnessFiles.map(row => row.path)).size === harnessPaths.length && harnessPaths.every(path => lock.harnessFiles.some(row => row.path === path)), `File queue harness inventory ${name}`);
  for (const file of lock.harnessFiles) pinnedBytes(currentPublicHarnessPin(file), `File queue repository harness ${file.path}`, true);
  requireCondition(sameMetadata(lock.loadedModules, [source]) && sameMetadata(lock.loadedBuiltins, ["node:fs/promises", "node:path"]), `File queue single-module/builtin closure ${name}`);
  const authoredCases = rawArrayItems(inputDocument.members.get("cases")), observations = rootMembers(expectedDocument.members.get("observations"));
  requireCondition(hasKeys(observations, ["cases", "filesystemScope", "clock", "checks"]) && observations.get("filesystemScope")?.startsWith('"') && observations.get("clock")?.startsWith('"') && authoredCases.length === 4, `File queue observations schema ${name}`);
  const cases = rawArrayItems(observations.get("cases")), caseIds = ["existing-fifo", "filesystem-aliases", "missing-fallback", "resolver-rejection"];
  requireCondition(cases.length === authoredCases.length && hasKeys(rootMembers(inputDocument.members.get("callbackFailure")), ["name", "message"]), `File queue case/error input schema ${name}`);
  const probesByCase = [["existing-fifo"], ["junction-existing", "hardlink-existing"], ["missing-normalized", "missing-junction-alias"]];
  let probeCount = 0;
  for (let index = 0; index < cases.length; index++) {
    const authored = rootMembers(authoredCases[index]), observed = rootMembers(cases[index]), returns = rootMembers(authored.get("returns"));
    requireCondition(hasKeys(authored, ["caseId", "probe", "returns"]) && authored.get("caseId") === JSON.stringify(caseIds[index]) && authored.get("probe") === authored.get("caseId") && observed.get("caseId") === authored.get("caseId") && hasKeys(returns, index === 3 ? ["afterFailure"] : ["first", "second", "unrelated", "subsequent"]) && [...returns.values()].every(value => value.startsWith('"')), `File queue ordered authored/captured case identity ${name} ${index}`);
    if (index === 3) {
      const outcomes = rootMembers(observed.get("outcomes")), rejected = rootMembers(outcomes.get("rejected")), error = rootMembers(rejected.get("error")), after = rootMembers(outcomes.get("afterFailure"));
      const invalidPath = parseJsonSupported(inputDocument.members.get("invalidResolverPath"));
      requireCondition(hasKeys(observed, ["caseId", "callbackInvoked", "outcomes"]) && observed.get("callbackInvoked") === "false" && hasKeys(outcomes, ["rejected", "afterFailure"]) && hasKeys(rejected, ["status", "error"]) && rejected.get("status") === '"rejected"' && hasKeys(error, ["name", "message", "code"]) && error.get("name") === '"TypeError"' && error.get("code") === '"ERR_INVALID_ARG_VALUE"' && typeof invalidPath === "string" && isAbsolute(invalidPath) && invalidPath.includes("\0") && parseJsonSupported(error.get("message")) === `The argument 'path' must be a string, Uint8Array, or URL without null bytes. Received '${invalidPath.replaceAll("\\", "\\\\").replaceAll("\0", "\\x00")}'` && hasKeys(after, ["status", "value"]) && after.get("status") === '"fulfilled"' && after.get("value") === returns.get("afterFailure"), `File queue retained resolver rejection/recovery fields ${name}`);
      continue;
    }
    requireCondition(hasKeys(observed, index === 1 ? ["caseId", "filesystemObservations", "probes"] : ["caseId", "probes"]), `File queue case observations schema ${name} ${index}`);
    if (index === 1) {
      const filesystem = rootMembers(observed.get("filesystemObservations")), fileText = parseJsonSupported(inputDocument.members.get("fileUtf8"));
      requireCondition(typeof fileText === "string" && hasKeys(filesystem, ["junctionResolvesToOwnedTarget", "existingJunctionFileRealpathsEqual", "hardlinkStatIdentityEqual", "hardlinkRealpathsEqual", "fileUtf8Sha256"]) && ["junctionResolvesToOwnedTarget", "existingJunctionFileRealpathsEqual", "hardlinkStatIdentityEqual"].every(key => filesystem.get(key) === "true") && filesystem.get("hardlinkRealpathsEqual") === "false" && parseJsonSupported(filesystem.get("fileUtf8Sha256")) === hash(Buffer.from(fileText, "utf8")), `File queue retained filesystem relation/file digest ${name}`);
    }
    const probes = rawArrayItems(observed.get("probes"));
    requireCondition(probes.length === probesByCase[index].length, `File queue recorded probe count ${name} ${index}`);
    for (let probeIndex = 0; probeIndex < probes.length; probeIndex++) {
      const probe = rootMembers(probes[probeIndex]), outcomes = rootMembers(probe.get("outcomes")), parallel = probeIndex === 1;
      requireCondition(hasKeys(probe, ["probeId", "checkpoints", "trace", "outcomes"]) && probe.get("probeId") === JSON.stringify(probesByCase[index][probeIndex]) && compareRawJson(probe.get("checkpoints"), JSON.stringify([{ at: "unrelated-completed-while-first-work-blocked", secondStarted: parallel }, { at: "first-cleanup-blocked", secondStarted: parallel }])) && compareRawJson(probe.get("trace"), JSON.stringify(parallel ? ["first:start", "second:start", "second:complete", "unrelated:start", "unrelated:complete", "first:work-complete", "first:cleanup-complete", "subsequent:start", "subsequent:complete"] : ["first:start", "unrelated:start", "unrelated:complete", "first:work-complete", "first:cleanup-complete", "second:start", index === 0 ? "second:throw" : "second:complete", "subsequent:start", "subsequent:complete"])) && hasKeys(outcomes, ["first", "second", "unrelated", "subsequent"]), `File queue retained probe/checkpoint/trace schema ${name} ${index} ${probeIndex}`);
      for (const label of outcomes.keys()) {
        const outcome = rootMembers(outcomes.get(label)), rejected = index === 0 && label === "second";
        requireCondition(hasKeys(outcome, rejected ? ["status", "error"] : ["status", "value"]) && outcome.get("status") === JSON.stringify(rejected ? "rejected" : "fulfilled") && (rejected ? outcome.get("error") === inputDocument.members.get("callbackFailure") : outcome.get("value") === returns.get(label)), `File queue raw callback value/error cross-fields ${name} ${index} ${probeIndex} ${label}`);
      }
      probeCount++;
    }
  }
  const counts = rootMembers(observations.get("checks"));
  requireCondition(hasKeys(counts, ["caseCount", "probeCount", "wholeUnchangedModule", "externalPackagesLoaded", "networkAndChildProcessesBlocked"]) && counts.get("caseCount") === String(cases.length) && counts.get("probeCount") === String(probeCount) && counts.get("wholeUnchangedModule") === "true" && counts.get("externalPackagesLoaded") === "0" && counts.get("networkAndChildProcessesBlocked") === "true", `File queue derived counts/source-only scope ${name}`);
}

function toolsDeclarationEvidence(authored, body, index, name) {
  const context = rootMembers(authored.get("context"));
  requireCondition(hasKeys(context, ["messages"]) && body.get("max_output_tokens") === "64", `Tools SDK bounded transcript/budget profile ${name} ${index}`);
  const messages = rawArrayItems(context.get("messages")), definitions = new Map();
  const addedNames = [["read", "write"], ["edit"], ["read"], ["write"]];
  requireCondition(messages.length === [2, 4, 6, 8, 8][index], `Tools SDK authored transcript length ${name} ${index}`);
  for (let messageIndex = 0; messageIndex < messages.length; messageIndex++) {
    const message = rootMembers(messages[messageIndex]), system = messageIndex % 2 === 0, step = messageIndex / 2;
    requireCondition(hasKeys(message, system ? ["role", "content", "timestamp", "toolsAdded", ...(step === 1 ? ["toolsRemoved"] : [])] : ["role", "content", "timestamp"]) && message.get("role") === JSON.stringify(system ? "system" : "user") && message.get("content")?.startsWith('"') && message.get("timestamp") === String(1700000000000 + messageIndex), `Tools SDK authored standard-tool message schema ${name} ${index} ${messageIndex}`);
    if (!system) continue;
    if (step === 1) requireCondition(compareRawJson(message.get("toolsRemoved"), '[{"name":"read"}]'), `Tools SDK authored removal declaration ${name} ${index}`);
    const additions = rawArrayItems(message.get("toolsAdded"));
    requireCondition(additions.length === addedNames[step].length, `Tools SDK authored declaration count ${name} ${index} ${step}`);
    for (let addition = 0; addition < additions.length; addition++) {
      const declaration = rootMembers(additions[addition]), toolName = parseJsonSupported(declaration.get("name"));
      requireCondition(hasKeys(declaration, ["name", "description", "parameters"]) && toolName === addedNames[step][addition] && declaration.get("description")?.startsWith('"') && declaration.get("parameters")?.startsWith("{"), `Tools SDK authored declaration schema ${name} ${index} ${step} ${addition}`);
      definitions.set(toolName, declaration);
    }
  }
  // Frozen observation profiles supply order; this is not a second implementation
  // of the upstream replay resolver. Authored descriptions/schema stay raw.
  const orders = [["read", "write"], ["write", "edit"], ["write", "edit", "read"], ["write", "edit", "read"], ["write", "edit", "read"]], tools = rawArrayItems(body.get("tools"));
  requireCondition(tools.length === orders[index].length, `Tools SDK retained declaration count ${name} ${index}`);
  for (let toolIndex = 0; toolIndex < tools.length; toolIndex++) {
    const tool = rootMembers(tools[toolIndex]), toolName = orders[index][toolIndex], declaration = definitions.get(toolName);
    requireCondition(hasKeys(tool, ["type", "name", "description", "parameters", ...(index === 4 ? [] : ["strict"])]) && tool.get("type") === '"function"' && tool.get("name") === JSON.stringify(toolName) && (index === 4 || tool.get("strict") === "false"), `Tools SDK retained tool order/strict presence ${name} ${index} ${toolIndex}`);
    requireCondition(tool.get("description") === declaration.get("description") && tool.get("parameters") === declaration.get("parameters"), `Tools SDK raw declaration/schema preservation ${name} ${index} ${toolIndex}`);
  }
}

function responsesSdkEvidence(fixture, inputDocument, expectedDocument, baseline, toolsProfile = false) {
  const profile = standaloneProfiles.get(toolsProfile ? toolsSdkManifestPath : sdkManifestPath);
  const name = fixture.fixtureId, family = dirname(toolsProfile ? toolsSdkManifestPath : sdkManifestPath), provenance = fixture.provenance;
  const inputKind = toolsProfile ? "authored-offline-full-wrapper-tool-history-input" : "authored-offline-full-wrapper-input";
  requireCondition(name === profile.fixtureId && fixture.input.kind === inputKind && fixture.expected.kind === profile.kind && fixture.input.path === `${family}/core.input.json` && fixture.expected.path === `${family}/core.expected.json` && fixture.lock?.path === `${family}/oracle.lock.json` && !Object.hasOwn(provenance, "kind") && !Object.hasOwn(provenance, "dependencyLock") && provenance.repeatRuns === 2 && provenance.byteIdentical === true && provenance.source && provenance.sdk && provenance.sourceResolver && provenance.observations && provenance.wireInput && provenance.credentials && provenance.captureCommand && fixture.scope, `SDK standalone manifest identity/provenance ${name}`);
  const input = inputDocument.metadata, expected = expectedDocument.metadata;
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "kind", "normalizerVersion", "scenario", "clock", "seed", "model", "commonOptions", "cases", "provenance", ...(toolsProfile ? ["wireFixture"] : ["context", "response"])]) && input.schemaVersion === 1 && input.fixtureId === name && input.sourceSha === baseline.source.commit && input.kind === fixture.input.kind && input.normalizerVersion === sdkNormalizer && input.provenance?.kind === "authored-synthetic-input", `SDK authored input identity ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expected.schemaVersion === 1 && expected.fixtureId === name && expected.sourceSha === baseline.source.commit && expected.kind === profile.kind, `SDK expected envelope schema ${name}`);
  const lockBytes = pinnedBytes(fixture.lock, `SDK oracle lock ${name}`);
  const lock = parseJsonSupported(fixtureDocument(lockBytes).raw);
  requireCondition(lock.schemaVersion === 1 && !Object.hasOwn(lock, "sourceSha") && !Object.hasOwn(lock, "dependencyLock") && lock.environmentPins?.sourceSha === baseline.source.commit, `SDK oracle lock schema/source identity ${name}`);
  // These are reviewed repository inputs. Their external workspace/receipt/archive
  // paths are recorded metadata and are never resolved or read by this checker.
  const planPath = "tools/PiReferenceRunner/responses-sdk-install-plan.json";
  const planBytes = pinnedBytes({ path: planPath, sha256: "0d19249ca9e105602abf304de9c3710e67157bab7209ef67645fd70f77307f22" }, `SDK canonical install plan ${name}`);
  const plan = parseJsonSupported(fixtureDocument(planBytes).raw), pins = lock.environmentPins;
  requireCondition(plan.schemaVersion === 1 && plan.source.commit === baseline.source.commit && plan.source.repository === baseline.source.repository && ["lock", "aiManifest", "wrapper"].every(prefix => baseline.artifacts.some(row => row.kind === "source-file" && row.path === plan.source[`${prefix}Path`] && row.sha256 === plan.source[`${prefix}Sha256`])), `SDK plan baseline cross-pins ${name}`);
  requireCondition(sameMetadata(pins.runtime, plan.runtime) && pins.runtime.version === baseline.referenceRuntime.version && pins.runtime.sha256 === baseline.referenceRuntime.sha256 && pins.platform === baseline.referenceRuntime.platform && pins.architecture === baseline.referenceRuntime.architecture && pins.npmVersion === plan.packageManager.version, `SDK runtime/platform pins ${name}`);
  const digest = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
  requireCondition(["setupReceiptSha256", "preparedReceiptSha256", "projectionManifestSha256", "projectionLockSha256"].every(key => digest(pins[key])) && pins.sourceFingerprint?.canonicalGit?.files === 2093 && pins.sourceFingerprint.acquiredCheckout?.files === 2093 && digest(pins.sourceFingerprint.canonicalGit.sha256) && digest(pins.sourceFingerprint.acquiredCheckout.sha256), `SDK recorded setup/source fingerprint schema ${name}`);
  const harnessPaths = [toolsProfile ? "tools/PiReferenceRunner/capture-responses-tools-sdk.mjs" : "tools/PiReferenceRunner/capture-responses-sdk.mjs", "tools/PiReferenceRunner/full-preload.mjs", "tools/PiReferenceRunner/offline-guard.mjs", "tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs", planPath, "tools/CompatibilityReport/raw-json.mjs", ...(toolsProfile ? ["fixtures/pi-v0.99.1/responses-sdk/core.input.json"] : [])];
  requireCondition(Array.isArray(lock.harnessFiles) && lock.harnessFiles.length === harnessPaths.length && new Set(lock.harnessFiles.map(row => row.path)).size === harnessPaths.length && harnessPaths.every(path => lock.harnessFiles.some(row => row.path === path)), `SDK harness inventory ${name}`);
  for (const file of lock.harnessFiles) pinnedBytes(currentPublicHarnessPin(file), `SDK repository harness ${file.path}`, true);
  const harnessPin = path => lock.harnessFiles.find(row => row.path === path)?.sha256;
  requireCondition(harnessPin(planPath) === hash(planBytes) && harnessPin(harnessPaths[3]) === "1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513" && harnessPin(harnessPaths[1]) === plan.genuineCaptureBoundary.offlinePreload.observerSha256 && harnessPin(harnessPaths[2]) === plan.genuineCaptureBoundary.offlinePreload.guardSha256, `SDK canonical helper/preload pins ${name}`);
  const packageCounts = new Map([["openai", 3548], ["partial-json", 9], ["typebox", 1385]]);
  requireCondition(Array.isArray(pins.dependencies) && pins.dependencies.length === 3 && new Set(pins.dependencies.map(row => row.name)).size === 3 && pins.dependencies.every(row => {
    const planned = plan.packages.find(candidate => candidate.name === row.name);
    return planned && packageCounts.has(row.name) && row.version === planned.lockEntry.version && row.integrity === planned.lockEntry.integrity && digest(row.archiveSha256) && digest(row.manifestSha256) && row.files?.files === packageCounts.get(row.name) && digest(row.files.sha256) && Array.isArray(row.licenses) && row.licenses.length > 0 && row.licenses.every(file => typeof file.path === "string" && file.path.length > 0 && digest(file.sha256) && Number.isSafeInteger(file.bytes) && file.bytes > 0);
  }) && pins.dependencies.reduce((count, row) => count + row.files.files, 0) === 4942, `SDK three-package recorded archive/tree pins ${name}`);
  const admitted = path => typeof path === "string" && !isAbsolute(path) && !path.includes("\\") && path.split("/").every(part => part && part !== "." && part !== ".." && !part.includes(":")) && (path.startsWith("upstream/") || [...packageCounts.keys()].some(name => path.startsWith(`node_modules/${name}/`)));
  requireCondition(Array.isArray(lock.loadedModules) && lock.loadedModules.length === 231 && new Set(lock.loadedModules.map(row => row.path)).size === lock.loadedModules.length && lock.loadedModules.every(row => admitted(row.path) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0) && lock.loadedModules.filter(row => row.path.startsWith("upstream/")).length === 30 && lock.loadedModules.filter(row => row.path.startsWith("node_modules/openai/")).length === 199 && lock.loadedModules.filter(row => row.path.startsWith("node_modules/partial-json/")).length === 2, `SDK admitted recorded loaded module paths/counts ${name}`);
  requireCondition(Array.isArray(lock.sourceHashes) && lock.sourceHashes.length === 30 && new Set(lock.sourceHashes.map(row => row.path)).size === lock.sourceHashes.length && lock.sourceHashes.every(row => {
    const loaded = lock.loadedModules.find(file => file.path === row.path);
    const baselineSource = baseline.artifacts.find(file => file.kind === "source-file" && `upstream/${file.path}` === row.path);
    return row.path.startsWith("upstream/") && loaded && row.sha256 === loaded.sha256 && row.bytes === loaded.bytes && row.canonicalGitBlobSha256 === row.sha256 && (!baselineSource || baselineSource.sha256 === row.sha256);
  }) && ["wrapper", "resolver"].every(prefix => lock.sourceHashes.some(row => row.path === `upstream/${plan.source[`${prefix}Path`]}` && row.sha256 === plan.source[`${prefix}Sha256`])), `SDK recorded canonical source cross-pins ${name}`);
  requireCondition(Array.isArray(lock.captureHistory) && lock.captureHistory.length > 0 && lock.captureHistory.every(row => row.goldenSha256 === fixture.expected.sha256 && row.sourceAndSdkBytesChanged === false && row.goldenRewritten !== true) && (toolsProfile ? lock.captureHistory.length === 1 && lock.captureHistory[0].kind === "initial genuine capture" : lock.captureHistory.at(-1).harnessSha256 === harnessPin(harnessPaths[0])), `SDK frozen capture history ${name}`);
  if (toolsProfile) {
    const prior = read(sdkManifestPath);
    const priorLock = parseJsonSupported(fixtureDocument(pinnedBytes(prior.lock, `Tools SDK reused oracle lock ${name}`)).raw);
    requireCondition(sameMetadata(pins, priorLock.environmentPins) && sameMetadata(lock.loadedModules, priorLock.loadedModules) && sameMetadata(lock.sourceHashes, priorLock.sourceHashes), `Tools SDK reused historical environment/module pins ${name}`);
  } else {
    const correction = lock.captureHistory.at(-1), previous = lock.captureHistory.at(-2);
    requireCondition(correction.kind === "validation-only explicit-oracle preflight correction R1" && correction.previousHarnessSha256 === previous?.harnessSha256 && correction.setupHelperSha256 === harnessPin(harnessPaths[3]) && correction.previousSetupHelperSha256 === "7c4cfc8715344710100d99902defeaef5280169f098cc53cab29f96a90120029" && correction.historicalReceiptHelperSha256 === correction.previousSetupHelperSha256 && correction.setupReceiptsRewritten === false && correction.oraclePathAllowlistChanged === false, `SDK historical setup receipt pin ${name}`);
  }
  const member = key => parseJsonSupported(inputDocument.members.get(key));
  const model = member("model"), options = member("commonOptions");
  let response;
  if (toolsProfile) {
    const wire = member("wireFixture");
    requireCondition(hasKeys(rootMembers(inputDocument.members.get("wireFixture")), ["path", "sha256", "use"]) && wire.path === "fixtures/pi-v0.99.1/responses-sdk/core.input.json" && wire.sha256 === "3d4431739bcce4b2218d13a71dba4bf273fe47aab8937f36d3177eb984bf9633" && wire.use === "response" && harnessPin(wire.path) === wire.sha256 && sameMetadata(fixture.requirementIds, ["api.openai-responses", "tools.declaration-replay"]), `Tools SDK authored wire reference/labels ${name}`);
    requireCondition(options.maxTokens === 64 && ["supportsMidConvoSystemMessages", "supportsAdditionalTools", "supportsToolSearch", "supportsOpenAIGrammarTools"].every(key => model.compat?.[key] === false), `Tools SDK bounded standard-tool compatibility profile ${name}`);
    response = parseJsonSupported(fixtureDocument(pinnedBytes(wire, `Tools SDK authored wire input ${name}`)).members.get("response"));
  } else response = member("response");
  requireCondition(sameMetadata(member("clock"), fixture.clock) && member("seed") === fixture.seed && sameMetadata(member("requirementIds"), fixture.requirementIds) && typeof member("scenario") === "string" && model.reasoning === false && model.api === "openai-responses" && model.provider === "openai", `SDK bounded authored profile metadata ${name}`);
  const inputCases = rawArrayItems(inputDocument.members.get("cases")), observations = rootMembers(expectedDocument.members.get("observations"));
  requireCondition(hasKeys(observations, ["cases", "responseWire", "checks"]) && inputCases.length === (toolsProfile ? 5 : 3), `SDK observations schema ${name}`);
  const cases = rawArrayItems(observations.get("cases"));
  requireCondition(cases.length === inputCases.length, `SDK case count ${name}`);
  const caseIds = toolsProfile ? ["initial-tools-strict-capable", "remove-add-strict-capable", "readd-strict-capable", "replace-strict-capable", "replace-strict-incapable"] : ["max-tokens-positive-floor", "max-tokens-zero", "max-tokens-negative"], budgets = ["1", "0", "-1"];
  for (let index = 0; index < cases.length; index++) {
    const authored = rootMembers(inputCases[index]), observed = rootMembers(cases[index]);
    requireCondition(hasKeys(authored, toolsProfile ? ["caseId", "compat", "context", "options"] : ["caseId", "options"]) && parseJsonSupported(authored.get("caseId")) === caseIds[index] && (toolsProfile ? authored.get("options") === "{}" && hasKeys(rootMembers(authored.get("compat")), ["supportsStrictMode"]) && rootMembers(authored.get("compat")).get("supportsStrictMode") === (index === 4 ? "false" : "true") : hasKeys(rootMembers(authored.get("options")), ["maxTokens"]) && rootMembers(authored.get("options")).get("maxTokens") === budgets[index]) && hasKeys(observed, ["caseId", "payloadSnapshots", "fetchRequests", "responseHooks", "providerEvents", "emissionSnapshots", "drainedFrames", "finalResult", "trace"]) && observed.get("caseId") === authored.get("caseId"), `SDK ordered case schema/identity ${name} ${index}`);
    const payloads = rawArrayItems(observed.get("payloadSnapshots")), fetches = rawArrayItems(observed.get("fetchRequests"));
    requireCondition(payloads.length === 1 && fetches.length === 1 && rawArrayItems(observed.get("responseHooks")).length === 1 && rawArrayItems(observed.get("providerEvents")).length === 5 && rawArrayItems(observed.get("emissionSnapshots")).length === 5 && rawArrayItems(observed.get("drainedFrames")).length === 5 && observed.get("finalResult")?.startsWith("{"), `SDK recorded request/event counts ${name} ${index}`);
    const payload = rootMembers(payloads[0]), request = rootMembers(fetches[0]);
    requireCondition(hasKeys(payload, ["params", "ownUndefinedPaths"]) && sameMetadata(parseJsonSupported(payload.get("ownUndefinedPaths")), ["/prompt_cache_retention", "/prompt_cache_options"]) && hasKeys(request, ["url", "method", "headers", "body", "bodyUtf8Sha256", "bodyJson", "initOwnKeys", "signalAborted"]), `SDK payload/request schema ${name} ${index}`);
    const body = parseJsonSupported(request.get("body"));
    requireCondition(typeof body === "string" && parseJsonSupported(request.get("bodyUtf8Sha256")) === hash(Buffer.from(body, "utf8")) && compareRawJson(body, request.get("bodyJson")) && compareRawJson(request.get("bodyJson"), payload.get("params")), `SDK raw body digest/retained JSON consistency ${name} ${index}`);
    const bodyMembers = rootMembers(canonicalRawJson(body));
    if (toolsProfile) toolsDeclarationEvidence(authored, bodyMembers, index, name);
    else requireCondition(index === 1 ? !bodyMembers.has("max_output_tokens") : bodyMembers.get("max_output_tokens") === "16", `SDK retained zero/negative/positive budget observation ${name} ${index}`);
    requireCondition(parseJsonSupported(request.get("url")) === `${model.baseUrl}/responses` && parseJsonSupported(request.get("method")) === "POST" && request.get("signalAborted") === "false" && sameMetadata(parseJsonSupported(request.get("initOwnKeys")), ["signal", "method", "headers", "body"]), `SDK request boundary metadata ${name} ${index}`);
    const headers = new Map(Object.entries({ ...model.headers, ...options.headers }).map(([key, value]) => [key.toLowerCase(), value]));
    for (const [key, value] of headers) if (value === null) headers.delete(key);
    for (const [key, value] of Object.entries({ accept: "application/json", authorization: `Bearer ${options.apiKey}`, "content-type": "application/json", session_id: options.sessionId, "x-stainless-arch": pins.architecture, "x-stainless-lang": "js", "x-stainless-os": "Windows", "x-stainless-package-version": pins.dependencies.find(row => row.name === "openai").version, "x-stainless-retry-count": "0", "x-stainless-runtime": "node", "x-stainless-runtime-version": pins.runtime.version, "x-stainless-timeout": String(options.timeoutMs / 1000) })) headers.set(key, value);
    requireCondition(sameMetadata(parseJsonSupported(request.get("headers")), [...headers].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)), `SDK retained raw request headers ${name} ${index}`);
  }
  const wire = parseJsonSupported(observations.get("responseWire")), counts = parseJsonSupported(observations.get("checks"));
  requireCondition(wire.utf8Sha256 === hash(Buffer.from(response.sseText, "utf8")) && wire.bytes === Buffer.byteLength(response.sseText, "utf8") && wire.chunkBytes === response.chunkBytes && counts.caseCount === cases.length && counts.fakeFetchCalls === cases.length && counts.noSourceTransformOrSdkShim === true && counts.networkAndProcessesBlocked === true && typeof counts.sourceSeam === "string", `SDK recorded wire/derived count integrity ${name}`);
}
function sessionContextEvidence(fixture, inputDocument, expectedDocument, baseline) {
  const name = fixture.fixtureId, family = "fixtures/pi-v0.99.1/session-context", provenance = fixture.provenance;
  requireCondition(name === "session-context-core" && fixture.input.kind === "authored-v3-session-forest-and-selected-leaf-input" && fixture.expected.kind === sessionKind && fixture.input.path === `${family}/core.input.json` && fixture.expected.path === `${family}/core.expected.json` && fixture.lock?.path === `${family}/oracle.lock.json` && !Object.hasOwn(provenance, "kind") && !Object.hasOwn(provenance, "dependencyLock") && provenance.repeatRuns === 2 && provenance.byteIdentical === true && ["source", "dependencies", "sourceResolver", "observations", "input", "credentials", "clock", "captureCommand"].every(key => typeof provenance[key] === "string" && provenance[key]) && fixture.scope, `Session standalone manifest identity/provenance ${name}`);
  const input = inputDocument.metadata, expected = expectedDocument.metadata;
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "kind", "clock", "seed", "normalizerVersion", "entries", "cases", "provenance"]) && input.schemaVersion === 1 && input.fixtureId === name && input.sourceSha === baseline.source.commit && input.kind === fixture.input.kind && input.normalizerVersion === sessionNormalizer && sameMetadata(fixture.requirementIds, ["P4-03", "P4-04", "P4-06"]) && compareRawJson(inputDocument.members.get("requirementIds"), JSON.stringify(fixture.requirementIds)) && compareRawJson(inputDocument.members.get("clock"), JSON.stringify(fixture.clock)) && compareRawJson(inputDocument.members.get("seed"), JSON.stringify(fixture.seed)) && sameMetadata(Object.keys(input.provenance).sort(), ["entries", "dates", "toolAndBashRecords", "expected", "unknownInventory", "limits"].sort()) && Object.values(input.provenance).every(value => typeof value === "string" && value), `Session authored input identity/profile ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expected.schemaVersion === 1 && expected.fixtureId === name && expected.sourceSha === baseline.source.commit && expected.kind === sessionKind, `Session expected envelope schema ${name}`);
  const lockBytes = pinnedBytes(fixture.lock, `Session oracle lock ${name}`), lock = parseJsonSupported(fixtureDocument(lockBytes).raw), pins = lock.environmentPins;
  requireCondition(lock.schemaVersion === 1 && sameMetadata(Object.keys(lock).sort(), ["schemaVersion", "environmentPins", "harnessFiles", "loadedModules", "sourceHashes", "captureHistory"].sort()) && pins?.sourceSha === baseline.source.commit, `Session oracle lock schema/source identity ${name}`);
  const planPath = "compatibility/session-context-oracle-plan.json", planSha = "31efcbef3fa2031b880118ce8c26165946d30ca2bab589942c0a1e2e8e730427";
  const plan = parseJsonSupported(fixtureDocument(pinnedBytes({ path: planPath, sha256: planSha }, `Session canonical install plan ${name}`)).raw);
  requireCondition(plan.schemaVersion === 1 && plan.source.commit === baseline.source.commit && plan.source.repository === baseline.source.repository && plan.source.lockPath === "package-lock.json" && baseline.artifacts.some(row => row.kind === "source-file" && row.path === plan.source.lockPath && row.sha256 === plan.source.lockSha256) && plan.source.pins.length === 11 && new Set(plan.source.pins.map(row => row.path)).size === 11 && plan.source.pins.every(row => { const source = baseline.artifacts.find(file => file.kind === "source-file" && file.path === row.path); return !source || source.sha256 === row.sha256 && source.bytes === row.bytes; }), `Session plan canonical source cross-pins ${name}`);
  // Public historical receipt bytes are immutable. External oracle/archive paths
  // in this metadata are not opened, and its pending setup status is retained.
  const historicalHelperSha = "285cb9e70726e19e38dc318a40137708ae65e3ae33e893ea4775e6f169168bdc", inspectorSha = "1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513";
  const receiptSha = "3e43ae32f145a1cb62d277deda4659d91e78fd49bb0615f88b06fa9c8f2c7f61";
  const receipt = parseJsonSupported(fixtureDocument(pinnedBytes({ path: "compatibility/session-context-oracle-setup.json", sha256: receiptSha }, `Session historical setup receipt ${name}`)).raw);
  requireCondition(receipt.status === "eight exact dependencies installed; whole session module qualification pending" && receipt.owner.schemaVersion === 1 && receipt.owner.owner === "PiSharp-session-context-oracle-v1" && receipt.owner.oracle === plan.workspace.proposedRoot && receipt.owner.sourceCommit === baseline.source.commit && receipt.owner.planSha256 === planSha && receipt.owner.helperSha256 === historicalHelperSha && receipt.owner.archiveInspectorSha256 === inspectorSha && receipt.loadedModuleClosureQualified === false && ["lifecycleScripts", "binLinks", "nativeAddons", "apiCredentials", "providerCalls", "sourceTransformOrModuleReplacement"].every(key => receipt[key] === false) && sameMetadata(receipt.optionalPeers, []) && pins.setupReceiptSha256 === receiptSha && pins.preparedReceiptSha256 === "447e1290ef2e206417bd0370ddea4becb4f436685d580d1bc8660eaa80cd864f", `Session historical setup/helper receipt identity ${name}`);
  requireCondition(sameMetadata(pins.sourceFingerprint, receipt.sourceFingerprint) && sameMetadata(pins.sourceFingerprint.canonicalGit, plan.source.canonicalFingerprint) && pins.sourceFingerprint.canonicalGit.files === 2093 && pins.sourceFingerprint.acquiredCheckout.files === 2093 && sameMetadata(pins.sourceFingerprint.declaredCheckoutConversions.map(row => row.path), ["pi-test.bat", "pi-test.ps1"]) && pins.sourceFingerprint.declaredCheckoutConversions.every(row => row.eol === "crlf") && pins.sourceFingerprint.canonicalGit.files - pins.sourceFingerprint.declaredCheckoutConversions.length === 2091, `Session 2091 raw plus two attribute conversion accounting ${name}`);
  requireCondition(sameMetadata(pins.runtime, plan.runtime) && pins.runtime.version === baseline.referenceRuntime.version && pins.runtime.sha256 === baseline.referenceRuntime.sha256 && pins.platform === baseline.referenceRuntime.platform && pins.architecture === baseline.referenceRuntime.architecture && pins.npmVersion === plan.packageManager.version, `Session exact runtime/platform pins ${name}`);
  const packages = { "": { name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, dependencies: plan.projection.packageJson.dependencies } };
  for (const row of plan.packages) packages[row.projectedLockPath] = row.lockEntry;
  const projectedHash = value => hash(Buffer.from(`${JSON.stringify(value, null, 2)}\n`));
  requireCondition(pins.projectionManifestSha256 === projectedHash(plan.projection.packageJson) && pins.projectionLockSha256 === projectedHash({ name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, lockfileVersion: 3, requires: true, packages }), `Session projected manifest/lock cross-hashes ${name}`);
  const harnessPaths = ["tools/PiReferenceRunner/capture-session-context.mjs", "tools/PiReferenceRunner/full-preload.mjs", "tools/PiReferenceRunner/offline-guard.mjs", "tools/PiReferenceRunner/setup-session-context-oracle.mjs", "tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs", planPath, "tools/CompatibilityReport/raw-json.mjs"];
  requireCondition(Array.isArray(lock.harnessFiles) && lock.harnessFiles.length === harnessPaths.length && new Set(lock.harnessFiles.map(row => row.path)).size === harnessPaths.length && harnessPaths.every(path => lock.harnessFiles.some(row => row.path === path)), `Session harness inventory ${name}`);
  for (const file of lock.harnessFiles) pinnedBytes(currentPublicHarnessPin(file), `Session repository harness ${file.path}`, true);
  const harnessPin = path => lock.harnessFiles.find(row => row.path === path)?.sha256;
  requireCondition(harnessPin(harnessPaths[3]) === historicalHelperSha && harnessPin(harnessPaths[4]) === inspectorSha && plan.reusedArchiveInspector.sha256 === inspectorSha && harnessPin(planPath) === planSha && harnessPin(harnessPaths[1]) === plan.genuineCaptureBoundary.offlinePreload.observerSha256 && harnessPin(harnessPaths[2]) === plan.genuineCaptureBoundary.offlinePreload.guardSha256, `Session frozen helper/inspector/preload pins ${name}`);
  const packageCounts = new Map([["cross-spawn", 9], ["isexe", 8], ["partial-json", 9], ["path-key", 5], ["shebang-command", 4], ["shebang-regex", 5], ["typebox", 1385], ["which", 6]]);
  requireCondition(Array.isArray(pins.dependencies) && pins.dependencies.length === packageCounts.size && new Set(pins.dependencies.map(row => row.name)).size === packageCounts.size && pins.dependencies.every(row => {
    const planned = plan.packages.find(item => item.name === row.name), installed = receipt.installed.find(item => item.name === row.name);
    return planned && installed && packageCounts.has(row.name) && row.version === planned.lockEntry.version && row.integrity === planned.lockEntry.integrity && sameMetadata(row, { name: installed.name, version: installed.version, archiveSha256: installed.archiveSha256, integrity: planned.lockEntry.integrity, files: installed.files, manifestSha256: installed.manifestSha256, licenses: installed.licenses.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })) }) && row.files.files === packageCounts.get(row.name) && installed.licenses.every(file => Buffer.byteLength(file.utf8Text, "utf8") === file.bytes && hash(Buffer.from(file.utf8Text, "utf8")) === file.sha256);
  }) && pins.dependencies.reduce((sum, row) => sum + row.files.files, 0) === 1431, `Session eight-package historical archive/tree/license cross-pins ${name}`);
  const loadedCounts = new Map([["cross-spawn", 6], ["isexe", 2], ["partial-json", 2], ["path-key", 1], ["shebang-command", 1], ["shebang-regex", 1], ["typebox", 668], ["which", 1]]);
  const digest = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
  const admitted = path => typeof path === "string" && !isAbsolute(path) && !path.includes("\\") && path.split("/").every(part => part && part !== "." && part !== ".." && !part.includes(":")) && (path.startsWith("upstream/") || [...loadedCounts.keys()].some(pkg => path.startsWith(`node_modules/${pkg}/`)));
  requireCondition(Array.isArray(lock.loadedModules) && lock.loadedModules.length === 715 && new Set(lock.loadedModules.map(row => row.path)).size === 715 && lock.loadedModules.every(row => admitted(row.path) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0 && sameMetadata(Object.keys(row).sort(), ["path", "bytes", "sha256"].sort())) && lock.loadedModules.filter(row => row.path.startsWith("upstream/")).length === 33 && [...loadedCounts].every(([pkg, count]) => lock.loadedModules.filter(row => row.path.startsWith(`node_modules/${pkg}/`)).length === count), `Session admitted recorded 33-source/682-dependency module inventory ${name}`);
  requireCondition(Array.isArray(lock.sourceHashes) && lock.sourceHashes.length === 33 && new Set(lock.sourceHashes.map(row => row.path)).size === 33 && lock.sourceHashes.every(row => {
    const loaded = lock.loadedModules.find(file => file.path === row.path), planned = plan.source.pins.find(file => `upstream/${file.path}` === row.path), source = baseline.artifacts.find(file => file.kind === "source-file" && `upstream/${file.path}` === row.path);
    return row.path.startsWith("upstream/") && loaded && sameMetadata(row, { ...loaded, canonicalGitBlobSha256: loaded.sha256 }) && (!planned || planned.sha256 === row.sha256 && planned.bytes === row.bytes) && (!source || source.sha256 === row.sha256);
  }) && ["packages/coding-agent/src/core/session-manager.ts", "packages/coding-agent/src/core/messages.ts", "packages/coding-agent/src/experimental/source-resolver.ts"].every(path => lock.sourceHashes.some(row => row.path === `upstream/${path}`)), `Session recorded canonical loaded source cross-pins ${name}`);
  requireCondition(sameMetadata(lock.captureHistory, [{ kind: "initial genuine capture", goldenSha256: fixture.expected.sha256, sourceAndDependencyBytesChanged: false }]), `Session frozen capture history ${name}`);
  sessionContextObservations(inputDocument, expectedDocument, name);
}

function sessionContextObservations(inputDocument, expectedDocument, name) {
  const authoredEntries = rawArrayItems(inputDocument.members.get("entries")), entries = new Map();
  requireCondition(authoredEntries.length === 28, `Session authored forest entry count ${name}`);
  for (const raw of authoredEntries) {
    const entry = rootMembers(raw), id = parseJsonSupported(entry.get("id")), parent = parseJsonSupported(entry.get("parentId"));
    requireCondition(typeof id === "string" && id && !entries.has(id) && (parent === null || entries.has(parent)) && entry.get("type")?.startsWith('"') && /^"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z"$/.test(entry.get("timestamp")), `Session unique ordered acyclic authored entry identity ${name} ${id}`);
    entries.set(id, { raw, members: entry });
  }
  const inputCases = rawArrayItems(inputDocument.members.get("cases")), observations = rootMembers(expectedDocument.members.get("observations"));
  requireCondition(inputCases.length === 4 && hasKeys(observations, ["cases", "checks"]), `Session observations schema ${name}`);
  const cases = rawArrayItems(observations.get("cases")), identities = ["selected-left", "selected-right", "explicit-null-leaf", "compacted-left-with-edits"], leaves = ['"left-leaf"', '"right-leaf"', "null", '"left-advanced-leaf"'];
  const entryIds = [
    ["root-system", "root-model", "root-thinking", "root-user", "left-system", "left-user", "left-assistant", "left-result", "left-custom-message", "left-custom-state", "left-usage", "left-label", "left-info", "left-leaf"],
    ["root-system", "root-model", "root-thinking", "root-user", "right-model", "right-thinking", "right-user", "right-assistant", "right-custom-message", "right-leaf"], [],
    ["left-compaction", "left-user", "left-assistant", "left-result", "left-custom-message", "left-custom-state", "left-usage", "left-label", "left-info", "left-leaf", "left-edit-before", "left-branch-summary", "left-bash", "left-bash-hidden", "left-edit-after", "left-edit-tool", "left-advanced-leaf"]
  ];
  const roles = [["system", "user", "system", "user", "assistant", "toolResult", "custom"], ["system", "user", "user", "assistant", "custom", "futureRuntime"], [], ["system", "compactionSummary", "user", "assistant", "toolResult", "branchSummary", "bashExecution", "bashExecution"]];
  const llmRoles = [["system", "user", "system", "user", "assistant", "toolResult", "user"], ["system", "user", "user", "assistant", "user"], [], ["system", "user", "user", "assistant", "toolResult", "user", "user"]];
  const rawObject = members => `{${[...members].map(([key, value]) => `${JSON.stringify(key)}:${value}`).join(",")}}`;
  const roleNames = messages => messages.map(raw => parseJsonSupported(rootMembers(raw).get("role")));
  requireCondition(cases.length === inputCases.length, `Session recorded case count ${name}`);
  for (let index = 0; index < cases.length; index++) {
    const authored = rootMembers(inputCases[index]), observed = rootMembers(cases[index]);
    requireCondition(hasKeys(authored, ["caseId", "leafId"]) && authored.get("caseId") === JSON.stringify(identities[index]) && authored.get("leafId") === leaves[index] && hasKeys(observed, ["caseId", "leafId", "projection", "context", "llmMessages", "ownUndefinedPaths"]) && observed.get("caseId") === authored.get("caseId") && observed.get("leafId") === authored.get("leafId"), `Session ordered case/explicit leaf identity ${name} ${index}`);
    const projection = rootMembers(observed.get("projection")), context = rootMembers(observed.get("context"));
    requireCondition(hasKeys(projection, ["entries", "messages", "thinkingLevel", "model"]) && hasKeys(context, ["messages", "thinkingLevel", "model"]) && projection.get("messages") === context.get("messages") && projection.get("thinkingLevel") === context.get("thinkingLevel") && projection.get("model") === context.get("model"), `Session separate projection/context raw cross-fields ${name} ${index}`);
    const projectedEntries = rawArrayItems(projection.get("entries")), contextMessages = rawArrayItems(context.get("messages")), llmMessages = rawArrayItems(observed.get("llmMessages")), flattened = [];
    requireCondition(projectedEntries.length === entryIds[index].length && sameMetadata(roleNames(contextMessages), roles[index]) && sameMetadata(roleNames(llmMessages), llmRoles[index]), `Session retained projection/role profiles ${name} ${index}`);
    for (let entryIndex = 0; entryIndex < projectedEntries.length; entryIndex++) {
      const projected = rootMembers(projectedEntries[entryIndex]), source = entries.get(entryIds[index][entryIndex]);
      requireCondition(hasKeys(projected, ["sourceEntry", "messages"]) && source && projected.get("sourceEntry") === source.raw, `Session raw retained source-entry identity ${name} ${index} ${entryIndex}`);
      const contributions = rawArrayItems(projected.get("messages")); flattened.push(...contributions);
      const entry = source.members, type = parseJsonSupported(entry.get("type"));
      if (type === "message") {
        const original = rootMembers(entry.get("message"));
        if (index === 3 && ["left-user", "left-result"].includes(entryIds[index][entryIndex])) {
          const edit = entries.get(entryIds[index][entryIndex] === "left-user" ? "left-edit-after" : "left-edit-tool").members, replacement = rootMembers(edit.get("replacement")).get("content"), content = entryIds[index][entryIndex] === "left-user" ? replacement : `[{"type":"text","text":${replacement}}]`;
          const edited = new Map(original); edited.set("content", content);
          requireCondition(contributions.length === 1 && compareRawJson(contributions[0], rawObject(edited)), `Session raw latest-edit content and retained metadata ${name} ${index} ${entryIndex}`);
        } else requireCondition(contributions.length === 1 && contributions[0] === entry.get("message"), `Session raw ordinary runtime message preservation ${name} ${index} ${entryIndex}`);
      } else if (type === "custom_message") {
        if (index === 3) requireCondition(contributions.length === 0, `Session null-edit omitted custom contribution ${name}`);
        else {
          const custom = new Map([["role", '"custom"'], ...["customType", "content", "display", "details"].filter(key => entry.has(key)).map(key => [key, entry.get(key)]), ["timestamp", index === 0 ? "1700000008000" : "1700000018000"]]);
          requireCondition(contributions.length === 1 && compareRawJson(contributions[0], rawObject(custom)), `Session custom raw null/missing fields ${name} ${index}`);
        }
      } else if (type === "compaction") {
        const summary = contributions.length === 2 && rootMembers(contributions[1]);
        requireCondition(contributions.length === 2 && contributions[0] === entry.get("systemMessage") && hasKeys(summary, ["role", "summary", "tokensBefore", "timestamp"]) && summary.get("role") === '"compactionSummary"' && summary.get("summary") === entry.get("summary") && summary.get("tokensBefore") === entry.get("tokensBefore") && summary.get("timestamp") === rootMembers(entry.get("systemMessage")).get("timestamp"), `Session compaction recorded raw checkpoint/summary fields ${name}`);
      } else if (type === "branch_summary") {
        requireCondition(contributions.length === 1 && compareRawJson(contributions[0], `{"role":"branchSummary","summary":${entry.get("summary")},"fromId":${entry.get("fromId")},"timestamp":1700000022000}`), `Session branch-summary recorded raw fields ${name}`);
      } else requireCondition(contributions.length === 0, `Session state-only/unknown entry contribution profile ${name} ${index} ${entryIndex}`);
    }
    requireCondition(compareRawJson(`[${flattened.join(",")}]`, context.get("messages")), `Session raw flattened projection/context message equality ${name} ${index}`);
    const model = index === 2 ? "null" : `{"provider":${rootMembers(entries.get(index === 1 ? "right-assistant" : "left-assistant").members.get("message")).get("provider")},"modelId":${rootMembers(entries.get(index === 1 ? "right-assistant" : "left-assistant").members.get("message")).get("model")}}`;
    requireCondition(context.get("thinkingLevel") === JSON.stringify(["low", "high", "off", "low"][index]) && compareRawJson(context.get("model"), model), `Session recorded settings/source assistant cross-fields ${name} ${index}`);
    const unchanged = index === 0 ? [[0, 0], [1, 1], [2, 2], [3, 3], [4, 4], [5, 5]] : index === 1 ? [[0, 0], [1, 1], [2, 2], [3, 3]] : index === 3 ? [[0, 0], [2, 2], [3, 3], [4, 4]] : [];
    for (const [runtimeIndex, llmIndex] of unchanged) requireCondition(contextMessages[runtimeIndex] === llmMessages[llmIndex], `Session raw separate LLM ordinary message preservation ${name} ${index} ${llmIndex}`);
    if (index < 2) {
      const runtime = rootMembers(contextMessages[index === 0 ? 6 : 4]), content = runtime.get("content");
      requireCondition(compareRawJson(llmMessages.at(-1), `{"role":"user","content":${content.startsWith('"') ? `[{"type":"text","text":${content}}]` : content},"timestamp":${runtime.get("timestamp")}}`), `Session separate LLM custom conversion retained fields ${name} ${index}`);
    }
    if (index === 3) {
      const summaryText = ["The conversation history before this point was compacted into the following summary:\n\n<summary>\n", "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n"];
      for (const [slot, runtimeSlot, prefix] of [[1, 1, 0], [5, 5, 1]]) {
        const runtime = rootMembers(contextMessages[runtimeSlot]), text = summaryText[prefix] + parseJsonSupported(runtime.get("summary")) + (prefix === 0 ? "\n</summary>" : "</summary>");
        requireCondition(compareRawJson(llmMessages[slot], `{"role":"user","content":[{"type":"text","text":${JSON.stringify(text)}}],"timestamp":${runtime.get("timestamp")}}`), `Session separate LLM recorded summary text/metadata ${name} ${slot}`);
      }
      const bash = rootMembers(contextMessages[6]), text = `Ran \`${parseJsonSupported(bash.get("command"))}\`\n\`\`\`\n${parseJsonSupported(bash.get("output"))}\n\`\`\`\n\nCommand exited with code 1\n\n[Output truncated. Full output: ${parseJsonSupported(bash.get("fullOutputPath"))}]`;
      requireCondition(bash.get("exitCode") === "1" && bash.get("truncated") === "true" && bash.get("excludeFromContext") === "false" && rootMembers(contextMessages[7]).get("excludeFromContext") === "true" && compareRawJson(llmMessages[6], `{"role":"user","content":[{"type":"text","text":${JSON.stringify(text)}}],"timestamp":${bash.get("timestamp")}}`), `Session stored bash data/separate LLM exclusion profile ${name}`);
    }
    const undefinedPaths = rootMembers(observed.get("ownUndefinedPaths"));
    requireCondition(hasKeys(undefinedPaths, ["projection", "context", "llmMessages"]) && compareRawJson(undefinedPaths.get("projection"), JSON.stringify(index === 1 ? ["/entries/8/messages/0/details", "/messages/4/details"] : [])) && compareRawJson(undefinedPaths.get("context"), JSON.stringify(index === 1 ? ["/messages/4/details"] : [])) && undefinedPaths.get("llmMessages") === "[]", `Session own-undefined paths distinct from null/missing ${name} ${index}`);
  }
  const counts = rootMembers(observations.get("checks"));
  requireCondition(hasKeys(counts, ["caseCount", "authoredInputEntries", "wholeSessionModuleLoaded", "publicExports", "sourceEntriesUnchanged", "noClockOrRngOverride", "noSessionConstructorOrFilesystemOperation", "networkAndProcessesBlocked"]) && counts.get("caseCount") === String(cases.length) && counts.get("authoredInputEntries") === String(authoredEntries.length) && compareRawJson(counts.get("publicExports"), '["buildSessionProjection","buildSessionContext","convertToLlm"]') && ["wholeSessionModuleLoaded", "sourceEntriesUnchanged", "noClockOrRngOverride", "noSessionConstructorOrFilesystemOperation", "networkAndProcessesBlocked"].every(key => counts.get(key) === "true"), `Session derived recorded counts/source-only observation scope ${name}`);
}

function editEvidence(fixture, inputDocument, expectedDocument, baseline) {
  const name = fixture.fixtureId, family = "fixtures/pi-v0.99.1/edit", provenance = fixture.provenance;
  const keys = (value, fields) => value && sameMetadata(Object.keys(value).sort(), [...fields].sort());
  requireCondition(keys(fixture, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "clock", "seed", "normalizerVersion", "input", "expected", "lock", "provenance", "scope"]) && name === "edit-core" && keys(fixture.input, ["path", "sha256", "kind"]) && keys(fixture.expected, ["path", "sha256", "kind"]) && keys(fixture.lock, ["path", "sha256"]) && fixture.input.path === `${family}/core.input.json` && fixture.input.sha256 === "fd07bb0eb503fdcd692822dbe2d6a51d0deae47b16c7a0e56ae7ea80788812f2" && fixture.input.kind === "authored-edit-input-and-owned-filesystem-layout" && fixture.expected.path === `${family}/core.expected.json` && fixture.expected.kind === editKind && fixture.lock.path === `${family}/oracle.lock.json` && sameMetadata(fixture.requirementIds, ["P1-05", "P1-06", "P3-05", "P3-06"]), `Edit standalone manifest identity/schema/input pin ${name}`);
  requireCondition(keys(provenance, ["source", "dependencies", "sourceResolver", "observations", "input", "credentials", "clock", "repeatRuns", "byteIdentical", "captureCommand"]) && provenance.source === "Unchanged whole packages/coding-agent/src/core/tools/edit.ts and edit-diff.ts public exports" && provenance.dependencies === "Exact thirteen upstream-lock archives and fully verified installed files" && provenance.sourceResolver === "Unchanged canonical experimental/source-resolver.ts" && provenance.repeatRuns === 2 && provenance.byteIdentical === true && ["observations", "input", "credentials", "clock", "captureCommand"].every(key => typeof provenance[key] === "string" && provenance[key]) && fixture.scope === "Twelve small edit cases; whole unchanged source and actual default filesystem effects; no renderer/native invocation, native matcher/formatter/cancellation/conflict parity or phase closure", `Edit bounded source-observation provenance ${name}`);
  const input = inputDocument.metadata, expected = expectedDocument.metadata;
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "requirementIds", "clock", "seed", "normalizerVersion", "cases"]) && input.schemaVersion === 1 && input.fixtureId === name && input.sourceSha === baseline.source.commit && input.kind === fixture.input.kind && input.normalizerVersion === editNormalizer && ["requirementIds", "clock", "seed"].every(key => compareRawJson(inputDocument.members.get(key), JSON.stringify(fixture[key]))), `Edit authored input schema/identity ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expected.schemaVersion === 1 && expected.fixtureId === name && expected.sourceSha === baseline.source.commit && expected.kind === editKind, `Edit expected envelope schema ${name}`);
  const lock = parseJsonSupported(fixtureDocument(pinnedBytes(fixture.lock, `Edit oracle lock ${name}`)).raw), pins = lock.environmentPins;
  requireCondition(lock.schemaVersion === 1 && keys(lock, ["schemaVersion", "environmentPins", "harnessFiles", "loadedModules", "sourceHashes", "captureHistory"]) && keys(pins, ["sourceSha", "sourceFingerprint", "runtime", "platform", "architecture", "npmVersion", "setupReceiptSha256", "preparedReceiptSha256", "projectionManifestSha256", "projectionLockSha256", "dependencies"]) && pins.sourceSha === baseline.source.commit, `Edit oracle lock schema/source identity ${name}`);
  // All external oracle/archive/runtime paths remain recorded metadata. This
  // adapter reads repository evidence only; full archive qualification is separate.
  const planPath = "compatibility/edit-oracle-plan.json", planSha = "b9127920b5db8b1b0cdf2ed24babfa943fe7bc79d3e842a2f28bbde6d91dffb3";
  const helperSha = "4337663e7938a100937e3ea442ffe334bec3646b98b50266ee116401069c827e", inspectorSha = "1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513";
  const receiptSha = "2c1a92e8cc89f344aa397a12cd2708e2909792ad81ec402a3570b7863e110bea";
  const plan = parseJsonSupported(fixtureDocument(pinnedBytes({ path: planPath, sha256: planSha }, `Edit canonical install plan ${name}`)).raw);
  const receipt = parseJsonSupported(fixtureDocument(pinnedBytes({ path: "compatibility/edit-oracle-setup.json", sha256: receiptSha }, `Edit historical setup receipt ${name}`)).raw);
  requireCondition(plan.schemaVersion === 1 && plan.source.commit === baseline.source.commit && plan.source.repository === baseline.source.repository && plan.source.lockPath === "package-lock.json" && baseline.artifacts.some(row => row.kind === "source-file" && row.path === plan.source.lockPath && row.sha256 === plan.source.lockSha256) && plan.source.pins.length === 20 && new Set(plan.source.pins.map(row => row.path)).size === 20 && plan.source.pins.every(row => { const source = baseline.artifacts.find(file => file.kind === "source-file" && file.path === row.path); return !source || source.sha256 === row.sha256 && source.bytes === row.bytes; }) && sameMetadata(plan.genuineCaptureBoundary.entryModules, ["upstream/packages/coding-agent/src/core/tools/edit-diff.ts", "upstream/packages/coding-agent/src/core/tools/edit.ts"]), `Edit canonical public-module/plan cross-pins ${name}`);
  requireCondition(receipt.status === "thirteen exact dependencies installed; whole edit module qualification pending" && sameMetadata(receipt.owner, { schemaVersion: 1, owner: "PiSharp-edit-oracle-v1", oracle: plan.workspace.proposedRoot, sourceCommit: baseline.source.commit, planSha256: planSha, helperSha256: helperSha, archiveInspectorSha256: inspectorSha }) && receipt.loadedModuleClosureQualified === false && ["lifecycleScripts", "binLinks", "nativeAddons", "apiCredentials", "providerCalls", "sourceTransformOrModuleReplacement"].every(key => receipt[key] === false) && sameMetadata(receipt.optionalPeers, []) && pins.setupReceiptSha256 === receiptSha && pins.preparedReceiptSha256 === "6bdc1dcb9b4279984193b190e037d5585155e942d0acfb929cb6ffc55b7d606d", `Edit historical setup/helper receipt identity ${name}`);
  requireCondition(sameMetadata(pins.sourceFingerprint, receipt.sourceFingerprint) && sameMetadata(pins.sourceFingerprint.canonicalGit, plan.source.canonicalFingerprint) && pins.sourceFingerprint.canonicalGit.files === 2093 && pins.sourceFingerprint.acquiredCheckout.files === 2093 && sameMetadata(pins.sourceFingerprint.declaredCheckoutConversions.map(row => row.path), ["pi-test.bat", "pi-test.ps1"]) && pins.sourceFingerprint.declaredCheckoutConversions.every(row => row.eol === "crlf") && pins.sourceFingerprint.canonicalGit.files - pins.sourceFingerprint.declaredCheckoutConversions.length === 2091, `Edit 2091 raw plus two attribute conversion accounting ${name}`);
  requireCondition(sameMetadata(pins.runtime, plan.runtime) && pins.runtime.version === baseline.referenceRuntime.version && pins.runtime.sha256 === baseline.referenceRuntime.sha256 && resolve(pins.runtime.absoluteExecutable).toLowerCase() === resolve(process.execPath).toLowerCase() && pins.platform === baseline.referenceRuntime.platform && pins.architecture === baseline.referenceRuntime.architecture && pins.npmVersion === plan.packageManager.version, `Edit exact runtime/path/platform pins ${name}`);
  const packages = { "": { name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, dependencies: plan.projection.packageJson.dependencies } };
  for (const row of plan.packages) packages[row.projectedLockPath] = row.lockEntry;
  const projectedHash = value => hash(Buffer.from(`${JSON.stringify(value, null, 2)}\n`));
  requireCondition(pins.projectionManifestSha256 === projectedHash(plan.projection.packageJson) && pins.projectionLockSha256 === projectedHash({ name: plan.projection.packageJson.name, version: plan.projection.packageJson.version, lockfileVersion: 3, requires: true, packages }), `Edit projected manifest/lock cross-hashes ${name}`);
  const harnessFiles = [
    { path: "tools/PiReferenceRunner/capture-edit.mjs", bytes: 24977, sha256: "0b957ef8c2ec1c7a5322b68167886fca3adb115e0991ff8630bbe308328b7e75" },
    { path: "tools/PiReferenceRunner/full-preload.mjs", bytes: 1145, sha256: "e1f62a2048bc484a162085430a5afb6f88547d78049b6f29b2a0e4f65fcd79e2" },
    { path: "tools/PiReferenceRunner/offline-guard.mjs", bytes: 784, sha256: "ae3741bdce496451bd04afcf8628df6ddc5bc5cfad8af6ee5e52cbc7b065a094" },
    { path: "tools/PiReferenceRunner/setup-edit-oracle.mjs", bytes: 33175, sha256: helperSha },
    { path: "tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs", bytes: 34806, sha256: inspectorSha },
    { path: planPath, bytes: 30815, sha256: planSha },
    { path: "tools/CompatibilityReport/raw-json.mjs", bytes: 3665, sha256: "58c378290d0be114e740dadda934d9a57e16a9321ae05eb8e34320e11d561ee9" }
  ];
  requireCondition(sameMetadata(lock.harnessFiles, harnessFiles) && plan.reusedArchiveInspector.sha256 === inspectorSha && harnessFiles[1].sha256 === plan.genuineCaptureBoundary.offlinePreload.observerSha256 && harnessFiles[2].sha256 === plan.genuineCaptureBoundary.offlinePreload.guardSha256, `Edit frozen harness/helper/preload inventory ${name}`);
  for (const file of lock.harnessFiles) pinnedBytes(currentPublicHarnessPin(file), `Edit repository harness ${file.path}`, true);
  const packageCounts = new Map([["chalk", 12], ["cross-spawn", 9], ["diff", 136], ["get-east-asian-width", 8], ["highlight.js", 398], ["isexe", 8], ["marked", 12], ["partial-json", 9], ["path-key", 5], ["shebang-command", 4], ["shebang-regex", 5], ["typebox", 1385], ["which", 6]]);
  const admitted = path => typeof path === "string" && !isAbsolute(path) && !/[\\\u0000-\u001f\u007f]/.test(path) && path.split("/").every(part => part && part !== "." && part !== ".." && !part.includes(":"));
  requireCondition(Array.isArray(pins.dependencies) && pins.dependencies.length === packageCounts.size && new Set(pins.dependencies.map(row => row.name)).size === packageCounts.size && pins.dependencies.every(row => {
    const planned = plan.packages.find(item => item.name === row.name), installed = receipt.installed.find(item => item.name === row.name);
    return planned && installed && packageCounts.has(row.name) && row.version === planned.lockEntry.version && row.integrity === planned.lockEntry.integrity && sameMetadata(row, { name: installed.name, version: installed.version, archiveSha256: installed.archiveSha256, integrity: planned.lockEntry.integrity, files: installed.files, manifestSha256: installed.manifestSha256, licenses: installed.licenses.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })) }) && row.files.files === packageCounts.get(row.name) && sameMetadata(installed.dependencyDeclarations, planned.lockEntry.dependencies ?? {}) && sameMetadata(installed.binDeclarations, planned.lockEntry.bin ?? {}) && installed.licenses.length > 0 && installed.licenses.every(file => admitted(file.path) && Buffer.byteLength(file.utf8Text, "utf8") === file.bytes && hash(Buffer.from(file.utf8Text, "utf8")) === file.sha256);
  }) && pins.dependencies.reduce((sum, row) => sum + row.files.files, 0) === 1997, `Edit thirteen-package historical archive/tree/license cross-pins ${name}`);
  const loadedCounts = new Map([["chalk", 4], ["cross-spawn", 6], ["diff", 19], ["get-east-asian-width", 4], ["highlight.js", 22], ["isexe", 2], ["marked", 1], ["partial-json", 0], ["path-key", 1], ["shebang-command", 1], ["shebang-regex", 1], ["typebox", 370], ["which", 1]]);
  const digest = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
  requireCondition(Array.isArray(lock.loadedModules) && lock.loadedModules.length === 496 && new Set(lock.loadedModules.map(row => row.path)).size === 496 && lock.loadedModules.every(row => keys(row, ["path", "bytes", "sha256"]) && admitted(row.path) && (row.path.startsWith("upstream/") || [...loadedCounts.keys()].some(pkg => row.path.startsWith(`node_modules/${pkg}/`))) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0) && lock.loadedModules.filter(row => row.path.startsWith("upstream/")).length === 64 && [...loadedCounts].every(([pkg, count]) => lock.loadedModules.filter(row => row.path.startsWith(`node_modules/${pkg}/`)).length === count), `Edit admitted recorded 64-source/432-dependency module inventory ${name}`);
  requireCondition(Array.isArray(lock.sourceHashes) && lock.sourceHashes.length === 64 && new Set(lock.sourceHashes.map(row => row.path)).size === 64 && lock.sourceHashes.every(row => {
    const loaded = lock.loadedModules.find(file => file.path === row.path), planned = plan.source.pins.find(file => `upstream/${file.path}` === row.path), source = baseline.artifacts.find(file => file.kind === "source-file" && `upstream/${file.path}` === row.path);
    return row.path.startsWith("upstream/") && loaded && sameMetadata(row, { ...loaded, canonicalGitBlobSha256: loaded.sha256 }) && (!planned || planned.sha256 === row.sha256 && planned.bytes === row.bytes) && (!source || source.sha256 === row.sha256 && source.bytes === row.bytes);
  }) && [...plan.genuineCaptureBoundary.entryModules, "upstream/packages/coding-agent/src/core/tools/renderers/edit.ts", "upstream/packages/coding-agent/src/utils/text.ts", "upstream/packages/tui/src/index.ts", "upstream/packages/coding-agent/src/experimental/source-resolver.ts"].every(path => lock.sourceHashes.some(row => row.path === path)), `Edit recorded canonical whole-module source cross-pins ${name}`);
  // Frozen digests cover the complete recorded inventories, including modules
  // outside baseline99. They do not re-open or qualify external installed bytes.
  requireCondition(hash(Buffer.from(canonicalRawJson(JSON.stringify(lock.loadedModules)))) === "554db5d9f6540797fbd97531c08841cfd48ab97de4305b3489842e152a076682" && hash(Buffer.from(canonicalRawJson(JSON.stringify(lock.sourceHashes)))) === "75b73608e8ea548ba8e0d5cc71fba90f3c657de2ffa5de66cf149908744406d9", `Edit immutable complete recorded module metadata ${name}`);
  requireCondition(sameMetadata(lock.captureHistory, [{ kind: "initial genuine capture", goldenSha256: fixture.expected.sha256, sourceAndDependencyBytesChanged: false }]), `Edit frozen capture history ${name}`);
  editObservations(inputDocument, expectedDocument, name);
}

function editObservations(inputDocument, expectedDocument, name) {
  const inputs = rawArrayItems(inputDocument.members.get("cases")), observations = rootMembers(expectedDocument.members.get("observations"));
  requireCondition(inputs.length === 12 && hasKeys(observations, ["cases", "checks", "filesystemScope", "failureContract"]), `Edit observations schema/input case count ${name}`);
  const cases = rawArrayItems(observations.get("cases")), identities = ["reverse-disjoint", "introduced-later-match", "normalized-ambiguity", "nested-overlap", "no-op", "empty-edits", "empty-old-text", "unicode-fuzzy-overlay", "bom-mixed-endings", "single-object-preparation", "legacy-appended", "missing-file-access"];
  const succeeded = new Set([0, 7, 8, 9, 10]), firstLines = new Map([[0, 1], [7, 3], [8, 2], [9, 1], [10, 1]]);
  const rawObject = members => `{${[...members].map(([key, value]) => `${JSON.stringify(key)}:${value}`).join(",")}}`;
  const string = value => typeof value === "string" && value.startsWith('"');
  const uint = value => typeof value === "string" && /^(0|[1-9][0-9]*)$/.test(value) && Number.isSafeInteger(Number(value));
  function outcome(raw, status, context) {
    const row = rootMembers(raw);
    requireCondition(hasKeys(row, status === "fulfilled" ? ["status", "value", "ownUndefinedPaths"] : ["status", "error"]) && row.get("status") === JSON.stringify(status) && (status !== "fulfilled" || row.get("ownUndefinedPaths") === "[]"), `Edit public outcome/own-undefined schema ${name} ${context}`);
    if (status === "fulfilled") return row.get("value");
    const error = rootMembers(row.get("error"));
    requireCondition(hasKeys(error, ["name", "message", "ownProperties"]) && error.get("name") === '"Error"' && string(error.get("message")) && parseJsonSupported(error.get("message")).length > 0 && error.get("ownProperties") === "{}", `Edit raw failure contract without stack ${name} ${context}`);
    return row.get("error");
  }
  function snapshot(raw, context) {
    const row = rootMembers(raw);
    requireCondition(hasKeys(row, ["bytes", "sha256", "base64", "utf8"]) && uint(row.get("bytes")) && string(row.get("sha256")) && string(row.get("base64")) && string(row.get("utf8")), `Edit byte snapshot schema ${name} ${context}`);
    const base64 = parseJsonSupported(row.get("base64")), bytes = Buffer.from(base64, "base64");
    requireCondition(bytes.toString("base64") === base64 && row.get("bytes") === String(bytes.length) && row.get("sha256") === JSON.stringify(hash(bytes)) && row.get("utf8") === JSON.stringify(new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes)), `Edit byte snapshot base64/length/SHA256/raw UTF8 ${name} ${context}`);
    return bytes;
  }
  function display(raw, index, context) {
    const row = rootMembers(raw);
    requireCondition(hasKeys(row, ["diff", "firstChangedLine"]) && string(row.get("diff")) && row.get("firstChangedLine") === String(firstLines.get(index)), `Edit recorded display/first-line schema ${name} ${context}`);
    return row;
  }
  requireCondition(cases.length === inputs.length, `Edit recorded case count ${name}`);
  for (let index = 0; index < cases.length; index++) {
    const authored = rootMembers(inputs[index]), observed = rootMembers(cases[index]), missing = index === 11, success = succeeded.has(index), path = parseJsonSupported(authored.get("path"));
    requireCondition(hasKeys(authored, ["caseId", "path", missing ? "missing" : "utf8", "arguments"]) && authored.get("caseId") === JSON.stringify(identities[index]) && (!missing || authored.get("missing") === "true") && typeof path === "string" && path.startsWith("files/") && !/[\\:\u0000-\u001f\u007f]/.test(path) && path.split("/").every(part => part && part !== "." && part !== "..") && (missing || string(authored.get("utf8"))) && hasKeys(observed, ["caseId", "path", "preparation", "helperObservations", "preview", "result", "filesystem"]) && observed.get("caseId") === authored.get("caseId") && observed.get("path") === authored.get("path"), `Edit ordered case/admitted path/input schema ${name} ${index}`);
    const argumentsMap = rootMembers(authored.get("arguments")), supplied = new Map([["path", authored.get("path")], ...argumentsMap]), preparation = rootMembers(observed.get("preparation"));
    let edits = argumentsMap.get("edits"), after = rawObject(supplied);
    if (index === 7) { edits = canonicalRawJson(parseJsonSupported(edits)); after = rawObject(new Map([["path", authored.get("path")], ["edits", edits]])); }
    else if (index === 9) { edits = `[${edits}]`; after = rawObject(new Map([["path", authored.get("path")], ["edits", edits]])); }
    else if (index === 8) edits = rawObject(new Map([["oldText", argumentsMap.get("oldText")], ["newText", argumentsMap.get("newText")]]));
    if (index === 8) edits = `[${edits}]`;
    else if (index === 10) edits = `[${[...rawArrayItems(edits), rawObject(new Map([["oldText", argumentsMap.get("oldText")], ["newText", argumentsMap.get("newText")]]))].join(",")}]`;
    const prepared = rawObject(new Map([["path", authored.get("path")], ["edits", edits]])), replacements = rawArrayItems(edits);
    requireCondition(hasKeys(preparation, ["suppliedBefore", "suppliedAfter", "prepared", "ownUndefinedPaths"]) && compareRawJson(preparation.get("suppliedBefore"), rawObject(supplied)) && compareRawJson(preparation.get("suppliedAfter"), after) && compareRawJson(preparation.get("prepared"), prepared) && preparation.get("ownUndefinedPaths") === "[]" && replacements.every(raw => { const edit = rootMembers(raw); return hasKeys(edit, ["oldText", "newText"]) && string(edit.get("oldText")) && string(edit.get("newText")); }), `Edit public preparation raw supplied/after/prepared cross-fields ${name} ${index}`);
    const filesystem = rootMembers(observed.get("filesystem"));
    requireCondition(hasKeys(filesystem, ["before", "after", "byteIdentical"]) && filesystem.get("byteIdentical") === String(!success), `Edit recorded filesystem equality/shape ${name} ${index}`);
    let before, afterBytes;
    if (missing) requireCondition(filesystem.get("before") === "null" && filesystem.get("after") === "null" && observed.get("helperObservations") === "null", `Edit missing-file null snapshots/helper absence ${name}`);
    else {
      before = snapshot(filesystem.get("before"), `${index} before`); afterBytes = snapshot(filesystem.get("after"), `${index} after`);
      requireCondition(before.equals(Buffer.from(parseJsonSupported(authored.get("utf8")), "utf8")) && before.equals(afterBytes) === !success, `Edit authored original/recorded saved byte equality ${name} ${index}`);
    }
    const preview = outcome(observed.get("preview"), "fulfilled", `${index} preview`), result = outcome(observed.get("result"), success ? "fulfilled" : "rejected", `${index} tool`);
    if (missing) {
      const previewValue = rootMembers(preview), error = rootMembers(result);
      requireCondition(hasKeys(previewValue, ["error"]) && previewValue.get("error") === error.get("message") && error.get("message") === JSON.stringify(`Could not edit file: ${path}. Error code: ENOENT.`), `Edit fulfilled failed-preview/absent-file raw failure cross-fields ${name}`);
      continue;
    }
    const helpers = rootMembers(observed.get("helperObservations")), normalization = rootMembers(helpers.get("normalization")), split = rootMembers(normalization.get("splitBom"));
    requireCondition(hasKeys(helpers, ["normalization", "matching", "fuzzyFind", "generated"]) && hasKeys(normalization, ["splitBom", "detectedLineEnding", "normalized", "fuzzyView"]) && hasKeys(split, ["bom", "text"]) && [split.get("bom"), split.get("text"), normalization.get("normalized"), normalization.get("fuzzyView")].every(string), `Edit public helper/normalization schema ${name} ${index}`);
    const bom = parseJsonSupported(split.get("bom")), text = parseJsonSupported(split.get("text")), normalized = parseJsonSupported(normalization.get("normalized"));
    const fuzzyView = index === 2 ? 'value="same"\nvalue="same"\n' : index === 7 ? 'untouched: "keep"\nuntouched: "keep"\nvalue=Foo-caf\u00e9 \ud83d\ude00\ntail\n' : normalized;
    requireCondition(bom === (index === 8 ? "\ufeff" : "") && bom + text === parseJsonSupported(authored.get("utf8")) && normalization.get("detectedLineEnding") === JSON.stringify(index === 8 ? "\r\n" : "\n") && normalized === text.replace(/\r\n|\r/g, "\n") && normalization.get("fuzzyView") === JSON.stringify(fuzzyView), `Edit retained BOM/endings/normalization profile ${name} ${index}`);
    const matching = outcome(helpers.get("matching"), success ? "fulfilled" : "rejected", `${index} matching`);
    if (index === 5) requireCondition(helpers.get("fuzzyFind") === "null", `Edit empty-plan absent first-text fuzzy call ${name}`);
    else {
      const fuzzy = rootMembers(outcome(helpers.get("fuzzyFind"), "fulfilled", `${index} fuzzy`));
      requireCondition(hasKeys(fuzzy, ["found", "index", "matchLength", "usedFuzzyMatch", "contentForReplacement"]) && fuzzy.get("found") === "true" && fuzzy.get("index") === String([13, 0, 0, 7, 0, null, 0, 36, 6, 0, 6][index]) && fuzzy.get("matchLength") === String([5, 5, 12, 6, 4, null, 0, 17, 12, 3, 4][index]) && fuzzy.get("usedFuzzyMatch") === String(index === 7) && fuzzy.get("contentForReplacement") === JSON.stringify(index === 7 ? fuzzyView : normalized), `Edit recorded fuzzy-find full result profile ${name} ${index}`);
    }
    if (success) {
      const match = rootMembers(matching), generated = rootMembers(helpers.get("generated"));
      requireCondition(hasKeys(match, ["baseContent", "newContent"]) && match.get("baseContent") === normalization.get("normalized") && string(match.get("newContent")) && hasKeys(generated, ["display", "patch", "restoredLineEndings"]) && string(generated.get("restoredLineEndings")), `Edit successful matching/generated schema ${name} ${index}`);
      const diff = outcome(generated.get("display"), "fulfilled", `${index} display`), patch = outcome(generated.get("patch"), "fulfilled", `${index} patch`), diffFields = display(diff, index, `${index} generated`), returned = rootMembers(result), details = rootMembers(returned.get("details"));
      display(preview, index, `${index} preview`);
      requireCondition(preview === diff && string(patch) && parseJsonSupported(patch).startsWith(`--- ${path}\n+++ ${path}\n`) && hasKeys(returned, ["content", "details"]) && hasKeys(details, ["diff", "patch", "firstChangedLine"]) && details.get("diff") === diffFields.get("diff") && details.get("firstChangedLine") === diffFields.get("firstChangedLine") && details.get("patch") === patch && compareRawJson(returned.get("content"), JSON.stringify([{ type: "text", text: `Successfully replaced ${replacements.length} block(s) in ${path}.` }])), `Edit complete preview/display/patch/tool raw cross-fields ${name} ${index}`);
      const newContent = parseJsonSupported(match.get("newContent")), restored = parseJsonSupported(generated.get("restoredLineEndings"));
      requireCondition(restored === (index === 8 ? newContent.replaceAll("\n", "\r\n") : newContent) && afterBytes.equals(Buffer.from(bom + restored, "utf8")), `Edit successful helper/actual saved bytes cross-fields ${name} ${index}`);
    } else {
      const previewValue = rootMembers(preview), matchError = rootMembers(matching), toolError = rootMembers(result);
      requireCondition(helpers.get("generated") === "null" && hasKeys(previewValue, ["error"]) && previewValue.get("error") === matchError.get("message") && (index === 5 ? toolError.get("message") === '"Edit tool input is invalid. edits must contain at least one replacement."' && matchError.get("message") === JSON.stringify(`No changes made to ${path}. The replacements produced identical content.`) : matching === result), `Edit rejected helper/fulfilled failed-preview/tool failure cross-fields ${name} ${index}`);
    }
  }
  const checks = rootMembers(observations.get("checks"));
  requireCondition(hasKeys(checks, ["caseCount", "wholeUnchangedEditAndDiffModules", "factory", "defaultRealFilesystemOperations", "rendererOrNativeHelperInvoked", "noClockOrRngOverride", "networkAndProcessesBlocked"]) && checks.get("caseCount") === String(cases.length) && checks.get("factory") === '"createEditToolDefinition"' && ["wholeUnchangedEditAndDiffModules", "defaultRealFilesystemOperations", "noClockOrRngOverride", "networkAndProcessesBlocked"].every(key => checks.get(key) === "true") && checks.get("rendererOrNativeHelperInvoked") === "false" && observations.get("filesystemScope") === JSON.stringify("Only this child verified-confined owned workspace; authored relative paths are actual source inputs") && observations.get("failureContract") === JSON.stringify("Raw error name/message and all additional own properties; stack excluded from serializable contract observation, never normalized"), `Edit derived counts/bounded source-only observation scope ${name}`);
}

function rpcJsonlEvidence(fixture, inputDocument, expectedDocument, baseline) {
  const name = fixture.fixtureId, family = "fixtures/pi-v0.99.1/rpc-jsonl", provenance = fixture.provenance;
  // The canonical public module is outside baseline.artifacts. Its explicit pin
  // qualifies this recorded family without expanding the baseline99 inventory.
  const source = { path: "packages/coding-agent/src/modes/rpc/jsonl.ts", bytes: 1503, sha256: "95723d349fcebad1f1da7ce103d02ba7d5e2c876b7d178d41d8b56beedbd93e0" };
  const exports = ["attachJsonlLineReader", "serializeJsonLine"];
  requireCondition(name === "rpc-jsonl-core" && fixture.input.path === `${family}/core.input.json` && fixture.input.sha256 === "8a5388aa9bb953beed7410044370a500e87dfd20644904d1ef6a93cddbeb5bb7" && fixture.input.kind === "authored-rpc-jsonl-chunks-and-serialization-values" && fixture.expected.path === `${family}/core.expected.json` && fixture.expected.kind === rpcKind && fixture.lock?.path === `${family}/oracle.lock.json` && !Object.hasOwn(provenance, "kind") && !Object.hasOwn(provenance, "dependencyLock") && !Object.hasOwn(provenance, "environmentLock") && provenance.source === source.path && provenance.sourceSha256 === source.sha256 && sameMetadata(provenance.exports, exports) && provenance.repeatRuns === 2 && provenance.byteIdentical === true && ["dependenciesInstalled", "sourceModified", "privateFunctionExtraction", "networkOrProviderCalls"].every(key => provenance[key] === false) && provenance.mechanism && provenance.captureCommand && fixture.scope && sameMetadata(fixture.requirementIds, ["rpc.framing"]), `RPC source-only manifest identity/provenance/input pin ${name}`);
  const input = inputDocument.metadata, expected = expectedDocument.metadata;
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "kind", "normalizerVersion", "clock", "seed", "readerCases", "serializerCases", "provenance"]) && input.schemaVersion === 1 && input.fixtureId === name && input.sourceSha === baseline.source.commit && input.kind === fixture.input.kind && input.normalizerVersion === rpcNormalizer && input.provenance?.kind === "authored-synthetic-input" && compareRawJson(inputDocument.members.get("requirementIds"), JSON.stringify(fixture.requirementIds)) && compareRawJson(inputDocument.members.get("clock"), JSON.stringify(fixture.clock)) && compareRawJson(inputDocument.members.get("seed"), JSON.stringify(fixture.seed)), `RPC authored input schema/identity ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expected.schemaVersion === 1 && expected.fixtureId === name && expected.sourceSha === baseline.source.commit && expected.kind === rpcKind, `RPC expected envelope schema ${name}`);
  const lock = parseJsonSupported(fixtureDocument(pinnedBytes(fixture.lock, `RPC oracle lock ${name}`)).raw), pins = lock.environmentPins;
  requireCondition(lock.schemaVersion === 1 && sameMetadata(Object.keys(lock).sort(), ["schemaVersion", "environmentPins", "harnessFiles", "loadedModules", "sourceRuntimeBuiltins", "sourceChecks", "rawCaptures", "captureHistory"].sort()) && sameMetadata(Object.keys(pins).sort(), ["sourceSha", "sourceTree", "executedSource", "runtime", "platform", "architecture", "externalDependencies"].sort()) && pins.sourceSha === baseline.source.commit && pins.sourceTree === baseline.source.tree && sameMetadata(pins.executedSource, { ...source, canonicalGitBlobSha256: source.sha256 }) && sameMetadata(pins.externalDependencies, []), `RPC canonical public module/empty-dependency lock schema ${name}`);
  const runtimePath = "P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe";
  requireCondition(sameMetadata(pins.runtime, { version: baseline.referenceRuntime.version, path: runtimePath, sha256: baseline.referenceRuntime.sha256, flags: ["--experimental-strip-types", "--disable-warning=ExperimentalWarning"] }) && pins.platform === baseline.referenceRuntime.platform && pins.architecture === baseline.referenceRuntime.architecture && resolve(qualifiedReferenceFile(runtimePath, baseline.referenceRuntime.sha256)).toLowerCase() === resolve(process.execPath).toLowerCase(), `RPC exact runtime/path/flags/platform pins ${name}`);
  const harnessPaths = ["tools/PiReferenceRunner/capture-rpc-jsonl.mjs", "tools/PiReferenceRunner/offline-guard.mjs", "tools/CompatibilityReport/raw-json.mjs"];
  requireCondition(Array.isArray(lock.harnessFiles) && lock.harnessFiles.length === harnessPaths.length && new Set(lock.harnessFiles.map(row => row.path)).size === harnessPaths.length && harnessPaths.every(path => lock.harnessFiles.some(row => row.path === path)), `RPC harness inventory ${name}`);
  for (const file of lock.harnessFiles) pinnedBytes(currentPublicHarnessPin(file), `RPC repository harness ${file.path}`, true);
  requireCondition(sameMetadata(lock.harnessFiles, [{ path: harnessPaths[0], bytes: 22495, sha256: "8e0cf55e2aae89bb711330ba02eedd033b37a8d9d2d6c02a8e518ae4b160065e" }, { path: harnessPaths[1], bytes: 784, sha256: "ae3741bdce496451bd04afcf8628df6ddc5bc5cfad8af6ee5e52cbc7b065a094" }, { path: harnessPaths[2], bytes: 3665, sha256: "58c378290d0be114e740dadda934d9a57e16a9321ae05eb8e34320e11d561ee9" }]) && sameMetadata(lock.loadedModules, [source]) && sameMetadata(lock.sourceRuntimeBuiltins, ["node:string_decoder"]), `RPC frozen harness/single-module/builtin pins ${name}`);
  const cleanSource = { revision: baseline.source.commit, tree: baseline.source.tree, status: "", sourceSha256: source.sha256, canonicalGitBlobSha256: source.sha256 };
  requireCondition(sameMetadata(lock.sourceChecks, { before: cleanSource, after: cleanSource }), `RPC recorded clean canonical source checks ${name}`);
  requireCondition(Array.isArray(fixture.rawCaptures) && fixture.rawCaptures.every((row, index) => row.path === `${family}/capture-${index + 1}.raw.json`) && sameMetadata(lock.rawCaptures, fixture.rawCaptures), `RPC retained raw path/cross-pin inventory ${name}`);
  const captures = rawPair(fixture);
  for (const { document } of captures) requireCondition(hasKeys(document.members, ["observations", "loadedModules", "sourceRuntimeBuiltins"]) && document.members.get("observations") === expectedDocument.members.get("observations") && compareRawJson(document.members.get("loadedModules"), JSON.stringify(lock.loadedModules)) && compareRawJson(document.members.get("sourceRuntimeBuiltins"), JSON.stringify(lock.sourceRuntimeBuiltins)), `RPC retained raw observations/module/builtin cross-fields ${name}`);
  requireCondition(sameMetadata(lock.captureHistory, [{ kind: "initial genuine capture", goldenSha256: fixture.expected.sha256, sourceAndDependencyBytesChanged: false }]), `RPC frozen capture history ${name}`);
  rpcJsonlObservations(inputDocument, expectedDocument, name);
}

function rpcJsonlObservations(inputDocument, expectedDocument, name) {
  const observations = rootMembers(expectedDocument.members.get("observations"));
  requireCondition(hasKeys(observations, ["readerCases", "serializerCases", "checks"]), `RPC observations schema ${name}`);
  const inputs = rawArrayItems(inputDocument.members.get("readerCases")), readers = rawArrayItems(observations.get("readerCases"));
  requireCondition(inputs.length === 24 && readers.length === inputs.length, `RPC reader input/captured case count ${name}`);
  const uint = value => typeof value === "string" && /^(0|[1-9][0-9]*)$/.test(value) && Number.isSafeInteger(Number(value));
  const hex = value => typeof value === "string" && /^(?:[a-f0-9]{2})*$/.test(value);
  let probeCount = 0, callbackCount = 0;
  for (let index = 0; index < readers.length; index++) {
    const input = rootMembers(inputs[index]), reader = rootMembers(readers[index]), explicit = input.has("chunks"), detached = input.has("detachAfterChunk");
    requireCondition(hasKeys(input, ["caseId", "purpose", ...(explicit ? ["chunks"] : ["wire", "fragments"]), ...(detached ? ["detachAfterChunk"] : [])]) && input.get("purpose")?.startsWith('"') && hasKeys(reader, ["caseId", "probes"]) && reader.get("caseId") === input.get("caseId"), `RPC ordered reader case/input schema ${name} ${index}`);
    const probes = rawArrayItems(reader.get("probes")); let mode = "explicit-chunks", wireBytes;
    if (!explicit) {
      const wire = rootMembers(input.get("wire")), fragments = rootMembers(input.get("fragments"));
      mode = parseJsonSupported(fragments.get("mode")); const wireKind = parseJsonSupported(wire.get("kind"));
      requireCondition(hasKeys(fragments, ["mode"]) && ["whole-buffer", "every-byte", "all-two-chunk-splits"].includes(mode) && hasKeys(wire, wireKind === "utf8" ? ["kind", "text"] : ["kind", "hex"]) && ["utf8", "hex"].includes(wireKind), `RPC authored wire/fragment plan schema ${name} ${index}`);
      const value = parseJsonSupported(wire.get(wireKind === "utf8" ? "text" : "hex"));
      requireCondition(typeof value === "string" && (wireKind === "utf8" || hex(value)), `RPC authored wire string/hex ${name} ${index}`);
      wireBytes = Buffer.from(value, wireKind === "utf8" ? "utf8" : "hex");
    }
    requireCondition(probes.length === (mode === "all-two-chunk-splits" ? wireBytes.length + 1 : 1), `RPC derived authored fragment probe count ${name} ${index}`);
    for (let probeIndex = 0; probeIndex < probes.length; probeIndex++) {
      const probe = rootMembers(probes[probeIndex]), chunks = rawArrayItems(probe.get("authoredChunks"));
      requireCondition(hasKeys(probe, ["probeId", "authoredChunks", "callbacks", "chunkSnapshots", "listenerObservations"]) && probe.get("probeId") === JSON.stringify(mode === "all-two-chunk-splits" ? `split-${probeIndex}` : mode), `RPC ordered probe schema/identity ${name} ${index} ${probeIndex}`);
      const kinds = [];
      for (const raw of chunks) {
        const chunk = rootMembers(raw), kind = parseJsonSupported(chunk.get("kind")); kinds.push(kind);
        requireCondition(hasKeys(chunk, kind === "buffer" ? ["kind", "hex"] : ["kind", "text"]) && ["buffer", "string"].includes(kind) && (kind === "buffer" ? hex(parseJsonSupported(chunk.get("hex"))) : chunk.get("text")?.startsWith('"')), `RPC authored delivered chunk schema ${name} ${index} ${probeIndex}`);
      }
      const planned = explicit ? input.get("chunks") : JSON.stringify(mode === "whole-buffer" ? [{ kind: "buffer", hex: wireBytes.toString("hex") }] : mode === "every-byte" ? [...wireBytes].map(byte => ({ kind: "buffer", hex: Buffer.from([byte]).toString("hex") })) : [{ kind: "buffer", hex: wireBytes.subarray(0, probeIndex).toString("hex") }, { kind: "buffer", hex: wireBytes.subarray(probeIndex).toString("hex") }]);
      requireCondition(compareRawJson(probe.get("authoredChunks"), planned), `RPC raw authored chunk/fragment cross-fields ${name} ${index} ${probeIndex}`);
      const callbacks = rawArrayItems(probe.get("callbacks")), dataIndices = []; let endSeen = false;
      for (let callbackIndex = 0; callbackIndex < callbacks.length; callbackIndex++) {
        const callback = rootMembers(callbacks[callbackIndex]), phase = parseJsonSupported(callback.get("phase")), line = parseJsonSupported(callback.get("line"));
        requireCondition(hasKeys(callback, ["index", "phase", "chunkIndex", "line", "lineUtf8Sha256"]) && callback.get("index") === String(callbackIndex) && typeof line === "string" && parseJsonSupported(callback.get("lineUtf8Sha256")) === hash(Buffer.from(line, "utf8")), `RPC callback raw string/index/UTF8 digest schema ${name} ${index} ${probeIndex} ${callbackIndex}`);
        requireCondition(phase === "data" ? !endSeen && uint(callback.get("chunkIndex")) && Number(callback.get("chunkIndex")) < chunks.length && (!dataIndices.length || Number(callback.get("chunkIndex")) >= dataIndices.at(-1)) && (!detached || Number(callback.get("chunkIndex")) <= Number(input.get("detachAfterChunk"))) : phase === "end" && !endSeen && !detached && callback.get("chunkIndex") === "null", `RPC callback chunk/EOF delivery metadata ${name} ${index} ${probeIndex} ${callbackIndex}`);
        if (phase === "data") dataIndices.push(Number(callback.get("chunkIndex"))); else endSeen = true;
      }
      const snapshots = rawArrayItems(probe.get("chunkSnapshots"));
      requireCondition(snapshots.length === chunks.length && snapshots.every((raw, chunkIndex) => { const snapshot = rootMembers(raw); return hasKeys(snapshot, ["chunkIndex", "deliveredKind", "callbackCount"]) && snapshot.get("chunkIndex") === String(chunkIndex) && snapshot.get("deliveredKind") === JSON.stringify(kinds[chunkIndex]) && snapshot.get("callbackCount") === String(dataIndices.filter(value => value <= chunkIndex).length); }), `RPC derived per-chunk callback snapshots ${name} ${index} ${probeIndex}`);
      const listeners = rootMembers(probe.get("listenerObservations"));
      const detachSnapshots = detached ? [{ at: "authored-data-boundary", chunkIndex: Number(input.get("detachAfterChunk")), before: { data: 3, end: 3 }, after: { data: 2, end: 2 }, afterSecondCall: { data: 2, end: 2 } }] : [];
      requireCondition(hasKeys(listeners, ["beforeAttach", "afterAttach", "detachSnapshots", "beforeCleanup", "afterCleanup"]) && compareRawJson(listeners.get("beforeAttach"), '{"data":1,"end":1}') && compareRawJson(listeners.get("afterAttach"), '{"data":2,"end":2}') && compareRawJson(listeners.get("detachSnapshots"), JSON.stringify(detachSnapshots)) && compareRawJson(listeners.get("beforeCleanup"), detached ? '{"data":2,"end":0}' : '{"data":3,"end":1}') && compareRawJson(listeners.get("afterCleanup"), '{"data":2,"end":0}'), `RPC recorded attach/detach/cleanup listener profile ${name} ${index} ${probeIndex}`);
      probeCount++; callbackCount += callbacks.length;
    }
  }
  const serializerInputs = rawArrayItems(inputDocument.members.get("serializerCases")), serializers = rawArrayItems(observations.get("serializerCases"));
  requireCondition(serializerInputs.length === 6 && serializers.length === serializerInputs.length, `RPC serializer input/captured case count ${name}`);
  for (let index = 0; index < serializers.length; index++) {
    const input = rootMembers(serializerInputs[index]), result = rootMembers(serializers[index]), serialized = parseJsonSupported(result.get("serialized"));
    requireCondition(hasKeys(input, ["caseId", "value"]) && hasKeys(result, ["caseId", "serialized", "utf8Hex", "bytes", "utf8Sha256"]) && result.get("caseId") === input.get("caseId") && typeof serialized === "string" && serialized.endsWith("\n") && !/[\r\n]/.test(serialized.slice(0, -1)) && compareRawJson(serialized, input.get("value")), `RPC serializer ordered identity/raw authored value ${name} ${index}`);
    const bytes = Buffer.from(serialized, "utf8");
    requireCondition(parseJsonSupported(result.get("utf8Hex")) === bytes.toString("hex") && result.get("bytes") === String(bytes.length) && parseJsonSupported(result.get("utf8Sha256")) === hash(bytes), `RPC serializer retained UTF8 bytes/length/digest ${name} ${index}`);
  }
  const counts = rootMembers(observations.get("checks"));
  requireCondition(hasKeys(counts, ["readerCaseCount", "readerProbeCount", "callbackCount", "serializerCaseCount", "unchangedWholeModule", "publicExports", "externalPackagesLoaded", "networkAndChildProcessesBlocked", "jsonAdmissionInvoked", "clockOrRngOverride"]) && counts.get("readerCaseCount") === String(readers.length) && counts.get("readerProbeCount") === String(probeCount) && counts.get("callbackCount") === String(callbackCount) && counts.get("serializerCaseCount") === String(serializers.length) && counts.get("unchangedWholeModule") === "true" && compareRawJson(counts.get("publicExports"), '["attachJsonlLineReader","serializeJsonLine"]') && counts.get("externalPackagesLoaded") === "0" && counts.get("networkAndChildProcessesBlocked") === "true" && counts.get("jsonAdmissionInvoked") === "false" && counts.get("clockOrRngOverride") === "false", `RPC derived counts/source-only framing scope ${name}`);
}

function anthropicSdkEvidence(fixture, inputDocument, expectedDocument, baseline) {
  const name = fixture.fixtureId, family = "fixtures/pi-v0.99.1/anthropic-sdk";
  const keys = (object, expected) => object && sameMetadata(Object.keys(object).sort(), [...expected].sort());
  const digest = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
  const relativePath = path => typeof path === "string" && path.length > 0 && !isAbsolute(path) && !/[\\:%\u0000-\u001f\u007f]/.test(path) && path.split("/").every(part => part && part !== "." && part !== "..");
  requireCondition(keys(fixture, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "input", "expected", "lock", "clock", "seed", "normalizerVersion", "licenseStatus", "provenance", "scope"]) && fixture.input.path === "tools/PiReferenceRunner/anthropic-sdk-inputs.json" && fixture.expected.path === `${family}/core.expected.json` && fixture.expected.kind === anthropicKind && fixture.lock?.path === `${family}/oracle.lock.json` && sameMetadata(fixture.requirementIds, ["api.anthropic-messages", "stream.indexed-events", "stream.terminal-settlement"]), `Anthropic standalone manifest schema/paths/requirements ${name}`);
  const provenance = fixture.provenance;
  requireCondition(keys(provenance, ["source", "sdk", "sseDecoder", "input", "callbacks", "emissionObservation", "credentials", "repeatRuns", "byteIdentical"]) && provenance.source === "Whole unchanged packages/ai/src/api/anthropic-messages.ts public stream" && provenance.sdk === "Actual upstream-locked @anthropic-ai/sdk0.124.0 beta.messages.create/asResponse request boundary" && provenance.sseDecoder === "Unchanged Pi iterateAnthropicEvents; no SDK decoder claim" && ["input", "callbacks", "emissionObservation", "credentials"].every(key => typeof provenance[key] === "string" && provenance[key]) && provenance.repeatRuns === 2 && provenance.byteIdentical === true, `Anthropic recorded capture seams/provenance ${name}`);
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "requirementIds", "clock", "seed", "normalizerVersion", "model", "commonOptions", "response", "cases"]) && inputDocument.metadata.schemaVersion === 1 && inputDocument.metadata.fixtureId === name && inputDocument.metadata.sourceSha === baseline.source.commit && inputDocument.metadata.kind === "authored-transcript-options-number-lexemes-and-in-memory-anthropic-wire" && inputDocument.metadata.normalizerVersion === anthropicNormalizer, `Anthropic authored input envelope ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expectedDocument.metadata.schemaVersion === 1 && expectedDocument.metadata.fixtureId === name && expectedDocument.metadata.sourceSha === baseline.source.commit && expectedDocument.metadata.kind === anthropicKind, `Anthropic captured expected envelope ${name}`);
  const referencePath = "compatibility/anthropic-sdk-reference.plan.json", referenceSha = "a8f0d82ad2a6b5def3e6687198b103c4bc90bb1b9629ae4f009dd1d8f68dadcc";
  const plan = parseJsonSupported(fixtureDocument(pinnedBytes({ path: referencePath, sha256: referenceSha, bytes: 25475 }, `Anthropic frozen reference plan ${name}`, true)).raw);
  requireCondition(plan.schemaVersion === 1 && plan.kind === "planned-genuine-unchanged-anthropic-wrapper-sdk-capture" && plan.sourceSha === baseline.source.commit && plan.fixtureId === name && sameMetadata(fixture.input, plan.input) && fixture.scope === plan.scope && plan.outputs.expected === fixture.expected.path && plan.outputs.lock === fixture.lock.path && plan.outputs.manifest === anthropicManifestPath && plan.repeatRuns === 2, `Anthropic frozen plan/manifest/input identity ${name}`);
  const setup = parseJsonSupported(fixtureDocument(pinnedBytes(plan.setupPlan, `Anthropic canonical setup plan ${name}`, true)).raw);
  requireCondition(setup.schemaVersion === 1 && setup.source.commit === baseline.source.commit && setup.source.repository === baseline.source.repository && setup.source.lockPath === "package-lock.json" && setup.source.pins.length === 8 && new Set(setup.source.pins.map(row => row.path)).size === 8 && setup.source.pins.every(row => {
    const source = baseline.artifacts.find(file => file.kind === "source-file" && file.path === row.path);
    return relativePath(row.path) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0 && (!source || source.sha256 === row.sha256 && source.bytes === row.bytes);
  }) && baseline.artifacts.some(file => file.kind === "source-file" && file.path === setup.source.lockPath && file.sha256 === setup.source.lockSha256), `Anthropic canonical setup/source baseline cross-pins ${name}`);
  const lock = parseJsonSupported(fixtureDocument(pinnedBytes(fixture.lock, `Anthropic oracle lock ${name}`)).raw), pins = lock.environmentPins;
  requireCondition(keys(lock, ["schemaVersion", "environmentPins", "harnessFiles", "loadedModules", "sourceHashes", "captureHistory"]) && lock.schemaVersion === 1 && keys(pins, ["sourceSha", "sourceFingerprint", "approvedOracle", "runtime", "nodeVersions", "os", "setupReceipt", "publicSetupReceipt", "preparedReceipt", "projectionManifestSha256", "projectionLockSha256", "installedPackages", "licenseStatus"]) && pins.sourceSha === baseline.source.commit, `Anthropic oracle lock/environment schema ${name}`);
  requireCondition(sameMetadata(pins.runtime, plan.runtime) && sameMetadata(plan.runtime, setup.runtime) && pins.runtime.version === baseline.referenceRuntime.version && pins.runtime.sha256 === baseline.referenceRuntime.sha256 && sameMetadata(pins.nodeVersions, Object.fromEntries(["node", "openssl", "v8", "uv", "unicode", "icu"].map(key => [key, process.versions[key]]))) && keys(pins.os, ["platform", "release", "architecture", "version"]) && pins.os.platform === baseline.referenceRuntime.platform && pins.os.architecture === baseline.referenceRuntime.architecture && pins.os.release === "10.0.26200" && pins.os.version === "Windows 11 Pro", `Anthropic exact recorded runtime/OS/version pins ${name}`);
  const fingerprint = { canonicalGit: setup.source.canonicalFingerprint, acquiredCheckout: setup.source.acquiredFingerprint, declaredCheckoutConversions: setup.source.declaredCheckoutConversions };
  requireCondition(sameMetadata(pins.sourceFingerprint, fingerprint) && sameMetadata(plan.sourceFingerprint, fingerprint) && fingerprint.canonicalGit.files === 2093 && fingerprint.acquiredCheckout.files === 2093 && sameMetadata(fingerprint.declaredCheckoutConversions.map(row => row.path), ["pi-test.bat", "pi-test.ps1"]) && fingerprint.declaredCheckoutConversions.every(row => row.eol === "crlf" && digest(row.canonicalSha256) && digest(row.checkoutSha256)) && fingerprint.canonicalGit.files - fingerprint.declaredCheckoutConversions.length === 2091, `Anthropic recorded 2091 raw plus two attribute source accounting ${name}`);
  const receipt = parseJsonSupported(fixtureDocument(pinnedBytes(plan.publicRestoredReceipt, `Anthropic historical public setup receipt ${name}`, true)).raw);
  requireCondition(sameMetadata(pins.setupReceipt, plan.restoredReceipt) && sameMetadata(pins.publicSetupReceipt, plan.publicRestoredReceipt) && sameMetadata(pins.preparedReceipt, plan.preparedReceipt) && plan.publicRestoredReceipt.sha256 === plan.restoredReceipt.sha256 && receipt.status === "eight exact dependencies installed; whole Anthropic wrapper qualification pending" && sameMetadata(receipt.owner, { schemaVersion: 1, owner: "PiSharp-anthropic-sdk-oracle-v1", oracle: plan.approvedOracle, sourceCommit: baseline.source.commit, planSha256: plan.setupPlan.sha256, helperSha256: plan.setupHelper.sha256, archiveInspectorSha256: plan.archiveInspector.sha256 }) && pins.approvedOracle === plan.approvedOracle && plan.approvedOracle === setup.workspace.proposedRoot && sameMetadata(receipt.sourceFingerprint, fingerprint), `Anthropic historical receipt/helper/oracle identity ${name}`);
  requireCondition(["lifecycleScripts", "lifecycleScriptsExecuted", "binLinks", "nativeAddons", "apiCredentials", "providerCalls", "sourceTransformOrModuleReplacement", "loadedModuleClosureQualified"].every(key => receipt[key] === false) && sameMetadata(receipt.optionalPeers, []) && sameMetadata(receipt.disabledLifecycleAdmission, setup.disabledLifecycleAdmission) && receipt.disabledLifecycleAdmission.scriptsExecuted === false && receipt.disabledLifecycleAdmission.disabledBy === "--ignore-scripts" && sameMetadata(receipt.developmentOnlyLicenseAdmission, setup.developmentOnlyLicenseAdmission) && receipt.licenseReviewStatus === "P1-02 HOLD" && receipt.redistributionLicenseClosure === false, `Anthropic preserved setup admission/effects/license HOLD ${name}`);
  const projectionPackages = { "": { name: setup.projection.packageJson.name, version: setup.projection.packageJson.version, dependencies: setup.projection.packageJson.dependencies } };
  for (const row of setup.packages) projectionPackages[row.projectedLockPath] = row.lockEntry;
  const projectedHash = value => hash(Buffer.from(`${JSON.stringify(value, null, 2)}\n`));
  requireCondition(pins.projectionManifestSha256 === projectedHash(setup.projection.packageJson) && pins.projectionLockSha256 === projectedHash({ name: setup.projection.packageJson.name, version: setup.projection.packageJson.version, lockfileVersion: 3, requires: true, packages: projectionPackages }), `Anthropic projected manifest/lock cross-hashes ${name}`);
  const harness = [
    { path: "tools/PiReferenceRunner/capture-anthropic-sdk.mjs", bytes: 8410, sha256: "cb921b0ac5a68f3b3cf9a36040c8467c3d80208ed6c2e444cc84ee20d37efcdd" },
    { path: "tools/PiReferenceRunner/run-anthropic-sdk.mjs", bytes: 19358, sha256: "0a74f1946bb02fc7d5faec09a64cc703d9c3ca39c03333be2794d256acd23348" },
    plan.input, { path: referencePath, bytes: 25475, sha256: referenceSha }, plan.setupPlan, plan.setupHelper, plan.archiveInspector, plan.offlineObserver, plan.offlineGuard, plan.rawComparator
  ];
  requireCondition(sameMetadata(lock.harnessFiles, harness) && sameMetadata(plan.harnessFiles, harness.map(row => row.path)) && new Set(harness.map(row => row.path)).size === 10, `Anthropic frozen harness/helper/comparator inventory ${name}`);
  for (const file of harness) pinnedBytes(currentPublicHarnessPin(file), `Anthropic repository harness ${file.path}`, true);
  const packageCounts = new Map([["@anthropic-ai/sdk", 1735], ["@babel/runtime", 249], ["@stablelib/base64", 15], ["fast-sha256", 6], ["json-schema-to-ts", 267], ["partial-json", 9], ["standardwebhooks", 8], ["ts-algebra", 104]]);
  requireCondition(Array.isArray(pins.installedPackages) && pins.installedPackages.length === 8 && sameMetadata(pins.installedPackages, plan.packages) && receipt.installed.length === 8 && setup.packages.length === 8 && new Set(plan.packages.map(row => row.name)).size === 8 && plan.packages.every(row => {
    const planned = setup.packages.find(item => item.name === row.name), installed = receipt.installed.find(item => item.name === row.name);
    const { integrity, archiveFile, ...original } = row;
    return planned && installed && packageCounts.has(row.name) && sameMetadata(original, installed) && row.version === planned.lockEntry.version && row.integrity === planned.lockEntry.integrity && row.archiveFile === planned.archiveFile && row.files.files === packageCounts.get(row.name) && digest(row.files.sha256) && digest(row.archiveSha256) && digest(row.manifestSha256) && sameMetadata(row.dependencyDeclarations, planned.lockEntry.dependencies ?? {}) && sameMetadata(row.peerDependencies, planned.lockEntry.peerDependencies ?? {}) && sameMetadata(row.peerDependenciesMeta, planned.lockEntry.peerDependenciesMeta ?? {}) && sameMetadata(row.binDeclarations, planned.lockEntry.bin ?? {}) && row.licenseReviewStatus === "P1-02 HOLD" && row.redistributionLicenseClosure === false && new Set(row.licenses.map(file => file.path)).size === row.licenses.length && row.licenses.every(file => relativePath(file.path) && typeof file.utf8Text === "string" && Buffer.byteLength(file.utf8Text, "utf8") === file.bytes && hash(Buffer.from(file.utf8Text, "utf8")) === file.sha256);
  }) && plan.packages.reduce((sum, row) => sum + row.files.files, 0) === 2393, `Anthropic eight-package archive/SRI/tree/manifest/dependency/license cross-pins ${name}`);
  const webhook = plan.packages.find(row => row.name === "standardwebhooks");
  requireCondition(sameMetadata(fixture.licenseStatus, plan.licenseStatus) && sameMetadata(pins.licenseStatus, plan.licenseStatus) && plan.licenseStatus.licenseReviewStatus === "P1-02 HOLD" && plan.licenseStatus.redistributionLicenseClosure === false && sameMetadata(plan.licenseStatus.developmentOnlyLicenseAdmission, setup.developmentOnlyLicenseAdmission) && webhook.licenseEvidence === "declared-MIT-only" && webhook.packagedRootLicenseVerified === false && sameMetadata(webhook.licenses, []) && sameMetadata(webhook.developmentOnlyLicenseAdmission, setup.developmentOnlyLicenseAdmission) && plan.packages.filter(row => row !== webhook).every(row => row.licenseEvidence === "packaged-root-license-text" && row.packagedRootLicenseVerified === true && row.licenses.length > 0 && row.developmentOnlyLicenseAdmission === null), `Anthropic declared-MIT-only development admission/no redistribution closure ${name}`);
  const loadedCounts = new Map([["@anthropic-ai/sdk", 121], ["@babel/runtime", 0], ["@stablelib/base64", 1], ["fast-sha256", 1], ["json-schema-to-ts", 0], ["partial-json", 2], ["standardwebhooks", 2], ["ts-algebra", 0]]);
  requireCondition(Array.isArray(lock.loadedModules) && lock.loadedModules.length === 153 && new Set(lock.loadedModules.map(row => row.path)).size === 153 && lock.loadedModules.every(row => keys(row, ["path", "bytes", "sha256"]) && relativePath(row.path) && (row.path.startsWith("upstream/") || [...loadedCounts.keys()].some(pkg => row.path.startsWith(`node_modules/${pkg}/`))) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0) && lock.loadedModules.filter(row => row.path.startsWith("upstream/")).length === 26 && [...loadedCounts].every(([pkg, count]) => lock.loadedModules.filter(row => row.path.startsWith(`node_modules/${pkg}/`)).length === count), `Anthropic admitted recorded 26-source/127-dependency loaded closure ${name}`);
  requireCondition(Array.isArray(lock.sourceHashes) && lock.sourceHashes.length === 26 && new Set(lock.sourceHashes.map(row => row.path)).size === 26 && lock.sourceHashes.every(row => {
    const loaded = lock.loadedModules.find(file => file.path === row.path), planned = setup.source.pins.find(file => `upstream/${file.path}` === row.path), source = baseline.artifacts.find(file => file.kind === "source-file" && `upstream/${file.path}` === row.path);
    return row.path.startsWith("upstream/") && loaded && sameMetadata(row, { ...loaded, canonicalGitBlobSha256: loaded.sha256 }) && (!planned || planned.sha256 === row.sha256 && planned.bytes === row.bytes) && (!source || source.sha256 === row.sha256 && source.bytes === row.bytes);
  }) && ["packages/ai/src/api/anthropic-messages.ts", "packages/coding-agent/src/experimental/source-resolver.ts"].every(path => lock.sourceHashes.some(row => row.path === `upstream/${path}`)), `Anthropic recorded canonical whole-source cross-pins ${name}`);
  const clock = parseJsonSupported(inputDocument.members.get("clock"));
  requireCondition(sameMetadata(fixture.clock, clock) && clock.unixMilliseconds === 1700000000000 && clock.mechanism === "Capture child explicitly replaces Date.now for output timestamps; source/SDK unchanged; actual OS/runtime/headers and crypto are not replaced." && JSON.stringify(fixture.seed) === inputDocument.members.get("seed") && compareRawJson(inputDocument.members.get("requirementIds"), JSON.stringify(fixture.requirementIds)) && sameMetadata(lock.captureHistory, [{ kind: "initial genuine whole-module capture", goldenSha256: fixture.expected.sha256, sourceAndSdkBytesChanged: false, childDateNowOverride: clock }]), `Anthropic frozen capture history/authored clock/seed ${name}`);
  anthropicSdkObservations(inputDocument, expectedDocument, pins, name);
}

function anthropicSdkObservations(inputDocument, expectedDocument, pins, name) {
  const observations = rootMembers(expectedDocument.members.get("observations")), cases = rawArrayItems(observations.get("cases")), inputs = rawArrayItems(inputDocument.members.get("cases"));
  requireCondition(hasKeys(observations, ["cases", "responseWire", "checks"]) && cases.length === 3 && inputs.length === 3, `Anthropic observations/case schema ${name}`);
  const model = parseJsonSupported(inputDocument.members.get("model")), common = parseJsonSupported(inputDocument.members.get("commonOptions")), response = parseJsonSupported(inputDocument.members.get("response"));
  requireCondition(model.api === "anthropic-messages" && model.provider === "anthropic" && model.reasoning === false && model.maxTokens === 64 && common.apiKey === "pisharp-authored-inert-key-noncredential" && common.maxRetries === 0 && sameMetadata(common.env, {}), `Anthropic authored direct-stream model/inert-key profile ${name}`);
  const ids = ["defaults-model-max", "zero-numeric-arguments", "negative-headers-long-cache-session"], budgets = ["64", "0", "-1"];
  const objectRaw = members => `{${[...members].map(([key, value]) => `${JSON.stringify(key)}:${value}`).join(",")}}`;
  const providerTypes = ["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"], emissionTypes = ["start", "text_start", "text_delta", "text_delta", "text_end", "done"];
  const trace = ["onPayload", "fetch", "onResponse", "emit:start", "provider:message_start", "provider:content_block_start", "emit:text_start", "provider:content_block_delta", "emit:text_delta", "provider:content_block_delta", "emit:text_delta", "provider:content_block_stop", "emit:text_end", "provider:message_delta", "provider:message_stop", "emit:done"];
  const wireEvents = response.sseText.split(/\r?\n/).filter(line => line.startsWith("data: ")).map(line => canonicalRawJson(line.slice(6)));
  requireCondition(sameMetadata(wireEvents.map(raw => parseJsonSupported(rootMembers(raw).get("type"))), providerTypes), `Anthropic authored wire event identities ${name}`);
  for (let index = 0; index < cases.length; index++) {
    const authored = rootMembers(inputs[index]), observed = rootMembers(cases[index]), options = { ...common, ...parseJsonSupported(authored.get("options")) }, actualModel = { ...model, ...(authored.has("modelOverrides") ? parseJsonSupported(authored.get("modelOverrides")) : {}) };
    requireCondition(hasKeys(authored, index === 1 ? ["caseId", "context", "options", "numberConversions"] : index === 2 ? ["caseId", "context", "modelOverrides", "options"] : ["caseId", "context", "options"]) && authored.get("caseId") === JSON.stringify(ids[index]) && hasKeys(observed, ["caseId", "numberConversions", "payloadSnapshots", "fetchRequests", "responseHooks", "providerEvents", "emissionSnapshots", "emissionOwnUndefined", "drainedFrames", "finalResult", "finalOwnUndefined", "trace"]) && observed.get("caseId") === authored.get("caseId"), `Anthropic ordered case identity/schema ${name} ${index}`);
    const payloads = rawArrayItems(observed.get("payloadSnapshots")), requests = rawArrayItems(observed.get("fetchRequests")), hooks = rawArrayItems(observed.get("responseHooks")), emissions = rawArrayItems(observed.get("emissionSnapshots")), drained = rawArrayItems(observed.get("drainedFrames")), provider = rawArrayItems(observed.get("providerEvents"));
    requireCondition(payloads.length === 1 && requests.length === 1 && hooks.length === 1 && emissions.length === 6 && drained.length === 6 && provider.length === 7, `Anthropic recorded request/event counts ${name} ${index}`);
    const payload = rootMembers(payloads[0]), params = rootMembers(payload.get("params")), request = rootMembers(requests[0]), body = parseJsonSupported(request.get("rawBody"));
    requireCondition(hasKeys(payload, ["params", "ownUndefinedPaths"]) && payload.get("ownUndefinedPaths") === "[]" && hasKeys(request, ["url", "method", "headers", "rawBody", "rawBodyUtf8Bytes", "rawBodyUtf8Sha256", "fetchInputType", "initOwnKeys", "initHeadersKind", "signalAborted"]), `Anthropic payload/raw request schema ${name} ${index}`);
    const bodyMembers = rootMembers(canonicalRawJson(body)), transported = new Map(params); transported.delete("betas");
    requireCondition(typeof body === "string" && request.get("rawBodyUtf8Bytes") === String(Buffer.byteLength(body, "utf8")) && parseJsonSupported(request.get("rawBodyUtf8Sha256")) === hash(Buffer.from(body, "utf8")) && compareRawJson(body, objectRaw(transported)), `Anthropic raw body bytes/digest/payload consistency ${name} ${index}`);
    requireCondition(bodyMembers.get("max_tokens") === budgets[index] && bodyMembers.get("stream") === "true" && bodyMembers.get("model") === JSON.stringify(model.id) && !bodyMembers.has("betas") && hasKeys(params, index === 1 ? ["model", "messages", "max_tokens", "stream", "system", "temperature", "tools"] : index === 2 ? ["model", "messages", "max_tokens", "stream", "betas", "system"] : ["model", "messages", "max_tokens", "stream", "system"]), `Anthropic retained default/zero/negative request observations ${name} ${index}`);
    requireCondition(request.get("url") === JSON.stringify(`${model.baseUrl}/v1/messages?beta=true`) && request.get("method") === '"POST"' && request.get("fetchInputType") === '"string"' && request.get("initHeadersKind") === '"Headers"' && request.get("signalAborted") === "false" && compareRawJson(request.get("initOwnKeys"), '["signal","method","headers","body"]'), `Anthropic recorded SDK request boundary ${name} ${index}`);
    const headers = new Map(Object.entries({ ...actualModel.headers, ...options.headers }).map(([key, value]) => [key.toLowerCase(), value]));
    for (const [key, value] of headers) if (value === null) headers.delete(key);
    const betas = index === 2 ? [...new Set(options.headers["anthropic-beta"].split(",").map(value => value.trim()).filter(Boolean))] : [];
    if (betas.length) headers.set("anthropic-beta", betas.join(","));
    if (index === 2) headers.set("x-session-affinity", options.sessionId);
    for (const [key, value] of Object.entries({ accept: "application/json", "anthropic-dangerous-direct-browser-access": "true", "anthropic-version": "2023-06-01", "content-type": "application/json", "user-agent": `pi (${pins.os.platform} ${pins.os.release}; ${pins.os.architecture})`, "x-api-key": common.apiKey, "x-stainless-arch": pins.os.architecture, "x-stainless-lang": "js", "x-stainless-os": "Windows", "x-stainless-package-version": pins.installedPackages.find(row => row.name === "@anthropic-ai/sdk").version, "x-stainless-retry-count": "0", "x-stainless-runtime": "node", "x-stainless-runtime-version": pins.runtime.version, "x-stainless-timeout": index === 0 ? "600" : String(options.timeoutMs / 1000) })) headers.set(key, value);
    requireCondition(compareRawJson(request.get("headers"), JSON.stringify([...headers].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0))) && (index === 2 ? compareRawJson(params.get("betas"), JSON.stringify(betas)) && actualModel.compat.sendSessionAffinityHeaders === true && actualModel.compat.supportsLongCacheRetention === true : !params.has("betas")), `Anthropic retained raw headers/null-removal/beta/session facts ${name} ${index}`);
    const conversions = rawArrayItems(observed.get("numberConversions"));
    requireCondition(conversions.length === (index === 1 ? 8 : 0), `Anthropic numeric seam observation count ${name} ${index}`);
    if (index === 1) {
      const specification = parseJsonSupported(authored.get("numberConversions")), authoredMessages = rawArrayItems(rootMembers(authored.get("context")).get("messages")), originalCall = rootMembers(rawArrayItems(rootMembers(authoredMessages[specification.messageIndex]).get("content"))[specification.contentIndex]);
      const messages = rawArrayItems(bodyMembers.get("messages")), call = rootMembers(rawArrayItems(rootMembers(messages[1]).get("content"))[0]), argumentsMembers = rootMembers(call.get("input"));
      requireCondition(specification.messageIndex === 2 && specification.contentIndex === 0 && specification.fields.length === 8 && new Set(specification.fields.map(row => row.field)).size === 8 && originalCall.get("type") === '"toolCall"' && originalCall.get("id") === call.get("id") && originalCall.get("name") === call.get("name") && call.get("type") === '"tool_use"' && argumentsMembers.get("opaque") === '"001"' && argumentsMembers.get("retainedNull") === "null" && argumentsMembers.size === 10 && bodyMembers.get("temperature") === "0", `Anthropic authored numeric call/opaque/null identity ${name}`);
      for (let fieldIndex = 0; fieldIndex < conversions.length; fieldIndex++) {
        const field = specification.fields[fieldIndex], number = Number(field.lexeme), record = rootMembers(conversions[fieldIndex]);
        // This is the disclosed authored Number(lexeme) seam, not a conversion of raw corpus numbers.
        requireCondition(hasKeys(record, ["field", "authoredLexeme", "mechanism", "numberToString", "jsonStringifyNumber", "negativeZero", "finite"]) && /^[a-zA-Z][a-zA-Z0-9]*$/.test(field.field) && /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(field.lexeme) && Number.isFinite(number) && record.get("field") === JSON.stringify(field.field) && record.get("authoredLexeme") === JSON.stringify(field.lexeme) && record.get("mechanism") === '"Number(authoredLexeme) before unchanged public stream"' && record.get("numberToString") === JSON.stringify(String(number)) && record.get("jsonStringifyNumber") === JSON.stringify(JSON.stringify(number)) && record.get("negativeZero") === String(Object.is(number, -0)) && record.get("finite") === "true" && argumentsMembers.get(field.field) === JSON.stringify(number), `Anthropic retained authored numeric seam/serialized lexeme ${name} ${field.field}`);
      }
    }
    const responseHook = rootMembers(hooks[0]);
    requireCondition(hasKeys(responseHook, ["response", "ownUndefinedPaths"]) && responseHook.get("ownUndefinedPaths") === "[]" && compareRawJson(responseHook.get("response"), JSON.stringify({ status: response.status, headers: response.headers })) && provider.every((raw, eventIndex) => raw === wireEvents[eventIndex]), `Anthropic retained response callback/authored wire DTOs ${name} ${index}`);
    const typeNames = rows => rows.map(raw => parseJsonSupported(rootMembers(raw).get("type")));
    requireCondition(sameMetadata(typeNames(emissions), emissionTypes) && sameMetadata(typeNames(drained), emissionTypes) && compareRawJson(observed.get("trace"), JSON.stringify(trace)) && compareRawJson(observed.get("emissionOwnUndefined"), JSON.stringify(emissions.map((_, emissionIndex) => ({ emissionIndex, paths: [] })))) && observed.get("finalOwnUndefined") === "[]", `Anthropic retained emission/drain/undefined/trace identities ${name} ${index}`);
    const deltaTexts = [2, 3].map(eventIndex => parseJsonSupported(rootMembers(rootMembers(wireEvents[eventIndex]).get("delta")).get("text"))), completeText = deltaTexts.join("");
    const zeroUsage = '{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}';
    const intermediateUsage = '{"input":11,"output":0,"cacheRead":2,"cacheWrite":3,"totalTokens":16,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0},"cacheWrite1h":0}';
    const finalUsage = '{"input":11,"output":2,"cacheRead":2,"cacheWrite":3,"totalTokens":18,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0},"cacheWrite1h":0}';
    requireCondition(emissions.every((raw, emissionIndex) => raw === drained[emissionIndex]), `Anthropic retained separate emission/drain raw snapshots ${name} ${index}`);
    for (let emissionIndex = 0; emissionIndex < 5; emissionIndex++) {
      const event = rootMembers(emissions[emissionIndex]), partial = rootMembers(event.get("partial")), partialContent = rawArrayItems(partial.get("content"));
      const text = emissionIndex < 2 ? "" : emissionIndex === 2 ? deltaTexts[0] : completeText;
      requireCondition(hasKeys(event, emissionIndex === 0 ? ["type", "partial"] : emissionIndex === 1 ? ["type", "contentIndex", "partial"] : emissionIndex === 4 ? ["type", "contentIndex", "content", "partial"] : ["type", "contentIndex", "delta", "partial"]) && hasKeys(partial, ["role", "content", "api", "provider", "model", "usage", "stopReason", "timestamp", ...(emissionIndex ? ["responseId"] : [])]) && partial.get("role") === '"assistant"' && partial.get("api") === '"anthropic-messages"' && partial.get("provider") === '"anthropic"' && partial.get("model") === JSON.stringify(model.id) && partial.get("stopReason") === '"pending"' && partial.get("timestamp") === "1700000000000" && compareRawJson(partial.get("usage"), emissionIndex ? intermediateUsage : zeroUsage) && (emissionIndex === 0 ? partialContent.length === 0 : partial.get("responseId") === '"msg_authored_wire"' && event.get("contentIndex") === "0" && partialContent.length === 1 && compareRawJson(partialContent[0], JSON.stringify({ type: "text", text, ...(emissionIndex < 4 ? { index: 4 } : {}) }))) && (emissionIndex === 2 || emissionIndex === 3 ? event.get("delta") === JSON.stringify(deltaTexts[emissionIndex - 2]) : emissionIndex !== 4 || event.get("content") === JSON.stringify(completeText)), `Anthropic retained indexed partial/text/usage snapshot ${name} ${index} ${emissionIndex}`);
    }
    const terminal = rootMembers(emissions.at(-1)), drainedTerminal = rootMembers(drained.at(-1)), final = rootMembers(observed.get("finalResult")), content = rawArrayItems(final.get("content"));
    requireCondition(hasKeys(terminal, ["type", "reason", "message"]) && hasKeys(final, ["role", "content", "api", "provider", "model", "usage", "stopReason", "timestamp", "responseId", "rawStopReason"]) && terminal.get("message") === observed.get("finalResult") && drainedTerminal.get("message") === observed.get("finalResult") && terminal.get("reason") === '"stop"' && final.get("role") === '"assistant"' && final.get("api") === '"anthropic-messages"' && final.get("provider") === '"anthropic"' && final.get("model") === JSON.stringify(model.id) && final.get("timestamp") === "1700000000000" && final.get("responseId") === '"msg_authored_wire"' && final.get("rawStopReason") === '"end_turn"' && final.get("stopReason") === '"stop"' && compareRawJson(final.get("usage"), finalUsage) && content.length === 1 && compareRawJson(content[0], JSON.stringify({ type: "text", text: completeText })), `Anthropic retained terminal/result/raw text/usage identity ${name} ${index}`);
  }
  const wire = rootMembers(observations.get("responseWire")), checks = rootMembers(observations.get("checks"));
  requireCondition(hasKeys(wire, ["utf8Sha256", "bytes", "chunkBytes"]) && wire.get("utf8Sha256") === JSON.stringify(hash(Buffer.from(response.sseText, "utf8"))) && wire.get("bytes") === String(Buffer.byteLength(response.sseText, "utf8")) && wire.get("chunkBytes") === String(response.chunkBytes), `Anthropic raw response wire byte/hash/chunk pins ${name}`);
  requireCondition(hasKeys(checks, ["caseCount", "fakeFetchCalls", "sourceSeam", "sdkStreamingDecoderQualified", "noSourceTransformOrSdkShim", "childDateNowOverride", "actualHeadersRetained", "networkAndProcessesBlocked"]) && checks.get("caseCount") === String(cases.length) && checks.get("fakeFetchCalls") === String(cases.length) && checks.get("sourceSeam") === '"Unchanged exported stream; real SDK beta.messages.create/asResponse; unchanged Pi SSE parser; supported fake fetch/callbacks"' && checks.get("sdkStreamingDecoderQualified") === "false" && checks.get("noSourceTransformOrSdkShim") === "true" && checks.get("childDateNowOverride") === inputDocument.members.get("clock") && checks.get("actualHeadersRetained") === "true" && checks.get("networkAndProcessesBlocked") === "true", `Anthropic derived counts/disclosed seams/no SDK decoder qualification ${name}`);
}

function agentProgressEvidence(fixture, inputDocument, expectedDocument, baseline) {
  const name = fixture.fixtureId, family = "fixtures/pi-v0.99.1/agent-progress";
  const keys = (object, expected) => object && sameMetadata(Object.keys(object).sort(), [...expected].sort());
  const digest = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
  const relativePath = path => typeof path === "string" && path.length > 0 && !isAbsolute(path) && !/[\\:%\u0000-\u001f\u007f]/.test(path) && path.split("/").every(part => part && part !== "." && part !== "..");
  const labels = ["P1-06", "P1-07", "P1-08", "P3-01", "P3-03"];
  requireCondition(keys(fixture, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "input", "expected", "lock", "clock", "seed", "normalizerVersion", "licenseStatus", "provenance", "scope"]) && keys(fixture.input, ["path", "bytes", "sha256"]) && keys(fixture.expected, ["path", "sha256", "kind"]) && keys(fixture.lock, ["path", "sha256"]) && fixture.input.path === "tools/PiReferenceRunner/agent-progress-inputs.json" && fixture.expected.path === `${family}/core.expected.json` && fixture.expected.kind === agentProgressKind && fixture.lock.path === `${family}/oracle.lock.json` && sameMetadata(fixture.requirementIds, labels), `Agent progress standalone manifest schema/paths/labels ${name}`);
  const planPath = "compatibility/agent-progress-reference.plan.json", planSha = "d1884bde8e7bf264cbc1e030b78d6d333252d690ecc4f7d12a67f1b268008d27";
  const plan = parseJsonSupported(fixtureDocument(pinnedBytes({ path: planPath, bytes: 12487, sha256: planSha }, `Agent progress frozen reference plan ${name}`, true)).raw);
  requireCondition(plan.schemaVersion === 1 && plan.fixtureId === name && plan.sourceSha === baseline.source.commit && sameMetadata(fixture.input, plan.input) && sameMetadata(fixture.scope, plan.scope) && sameMetadata(fixture.licenseStatus, plan.licenseStatus) && fixture.licenseStatus.fullP102Closure === false && fixture.licenseStatus.noNewPackageAcquisition === true && sameMetadata(fixture.scope.nativeDifferencesOpen, ["Native awaited/serialized progress versus source void callback and overlapping listeners", "Native late-callback rejection versus source ignored late callbacks"]) && plan.outputs.expected === fixture.expected.path && plan.outputs.lock === fixture.lock.path && plan.outputs.manifest === agentProgressManifestPath, `Agent progress frozen scope/license/open native differences ${name}`);
  requireCondition(sameMetadata(fixture.provenance, { source: "Whole unchanged packages/agent/src/agent.ts and agent-loop.ts public lifecycle", input: "authored fake provider frames, tool implementations/results/hooks and gate choreography; no authored expected output", callbacks: "Authored execute/update and beforeToolCall/afterToolCall/finishTurn; two actual Agent subscribers", barrierControl: "Authored promise gates; no sleeps or source scheduler replacement", emissionSnapshot: "First source subscriber snapshots each complete event/state at entry before awaiting its barrier", jsonUnsupportedValues: "JSON.stringify omission and Set {} retained; own undefined/function/Set inventory is separate", credentials: "None inherited or required; no API key", repeatRuns: 2, byteIdentical: true }), `Agent progress unchanged-source capture seams/provenance ${name}`);
  requireCondition(hasKeys(inputDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "requirementIds", "inputKind", "seed", "normalizerVersion", "clock", "model", "systemPrompt", "prompt", "assistantMessage", "toolParameters", "cases", "contractsToObserve", "unsupportedClaims"]) && inputDocument.metadata.schemaVersion === 1 && inputDocument.metadata.fixtureId === name && inputDocument.metadata.sourceSha === baseline.source.commit && inputDocument.metadata.normalizerVersion === agentProgressNormalizer && inputDocument.members.get("inputKind") === JSON.stringify(fixture.provenance.input) && compareRawJson(inputDocument.members.get("requirementIds"), JSON.stringify(labels)), `Agent progress authored input envelope ${name}`);
  requireCondition(hasKeys(expectedDocument.members, ["schemaVersion", "fixtureId", "sourceSha", "kind", "observations"]) && expectedDocument.metadata.schemaVersion === 1 && expectedDocument.metadata.fixtureId === name && expectedDocument.metadata.sourceSha === baseline.source.commit && expectedDocument.metadata.kind === agentProgressKind, `Agent progress captured expected envelope ${name}`);
  const clock = { unixMilliseconds: 1700000000000, mechanism: "Child-only Date.now replacement; source bytes unchanged; original restored after capture" };
  requireCondition(sameMetadata(fixture.clock, clock) && compareRawJson(inputDocument.members.get("clock"), JSON.stringify(clock)) && fixture.seed === "none; authored identifiers and values" && inputDocument.members.get("seed") === JSON.stringify(fixture.seed), `Agent progress authored clock/seed ${name}`);
  const setup = parseJsonSupported(fixtureDocument(pinnedBytes(plan.originalSetupPlan, `Agent progress historical setup plan ${name}`, true)).raw);
  requireCondition(sameMetadata(setup.source, { repository: baseline.source.repository, commit: baseline.source.commit, lockPath: "package-lock.json", lockSha256: plan.source.lockSha256 }) && plan.source.commit === baseline.source.commit && plan.source.repository === baseline.source.repository && plan.source.tree === baseline.source.tree && plan.source.lockPath === setup.source.lockPath && baseline.artifacts.some(row => row.kind === "source-file" && row.path === setup.source.lockPath && row.sha256 === setup.source.lockSha256) && plan.source.pins.length === 8 && new Set(plan.source.pins.map(row => row.path)).size === 8 && plan.source.pins.every(row => { const canonical = baseline.artifacts.find(file => file.kind === "source-file" && file.path === row.path); return relativePath(row.path) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0 && (!canonical || row.sha256 === canonical.sha256 && row.bytes === canonical.bytes); }), `Agent progress historical/canonical source plan cross-pins ${name}`);
  const lock = parseJsonSupported(fixtureDocument(pinnedBytes(fixture.lock, `Agent progress oracle lock ${name}`)).raw), pins = lock.environmentPins;
  requireCondition(keys(lock, ["schemaVersion", "environmentPins", "harnessFiles", "loadedModules", "sourceHashes", "captureHistory"]) && lock.schemaVersion === 1 && keys(pins, ["sourceFingerprint", "installedPackages", "setupReceipt", "projectionManifestSha256", "projectionLockSha256", "approvedOracle", "runtime", "licenseStatus"]), `Agent progress oracle lock/environment schema ${name}`);
  requireCondition(sameMetadata(pins.runtime, plan.runtime) && sameMetadata(setup.runtime, { version: plan.runtime.version, sha256: plan.runtime.sha256, reuseExisting: true }) && pins.runtime.version === baseline.referenceRuntime.version && pins.runtime.sha256 === baseline.referenceRuntime.sha256 && resolve(pins.runtime.absoluteExecutable).toLowerCase() === resolve(process.execPath).toLowerCase() && resolve(pins.approvedOracle).toLowerCase() === resolve(plan.approvedOracle).toLowerCase() && resolve(setup.workspace).toLowerCase() === resolve(plan.approvedOracle).toLowerCase() && sameMetadata(pins.licenseStatus, plan.licenseStatus), `Agent progress exact runtime/approved oracle metadata ${name}`);
  const conversions = [
    { path: "pi-test.bat", eol: "crlf", canonicalSha256: "d454a5a0e203d9009283b887720472b022b0853380758b11419c8547a32dbb88", checkoutSha256: "c9d3430a614cabfaae5a9e97259643ab9041629bc8253f58f22579a5b6d9c448" },
    { path: "pi-test.ps1", eol: "crlf", canonicalSha256: "9f76d37f75b1d6f03be14d8bce3ca05331c4b09b8003a53329d8e4fe942792ef", checkoutSha256: "9b97fa3be6582e935f43865599d21bbb216e5ffe2cb1eb198c77e7298490b895" }
  ];
  requireCondition(sameMetadata(pins.sourceFingerprint, { canonicalGit: plan.source.canonicalFingerprint, acquiredCheckout: plan.source.acquiredFingerprint, declaredCheckoutConversions: conversions }) && plan.source.canonicalFingerprint.files === 2093 && plan.source.acquiredFingerprint.files === 2093 && sameMetadata(plan.source.declaredConversionPaths, conversions.map(row => row.path)) && plan.source.canonicalFingerprint.files - conversions.length === 2091, `Agent progress recorded 2091 raw plus two attribute source accounting ${name}`);
  // External receipt/archive/installed paths are recorded evidence only. This
  // checker never opens them or upgrades historical qualification status.
  requireCondition(sameMetadata(pins.setupReceipt, plan.setupReceipt) && plan.setupReceipt.path === ".pisharp-oracle-setup.json" && plan.setupReceipt.bytes === 670 && plan.setupReceipt.sha256 === "19f38d04439ee9f6b9c417410cddc70b399ae2b576db04f196fb40c7f1cca600" && setup.acquisition.record.endsWith("/Pi-reference-oracle-v0.99.1/.pisharp-oracle-setup.json") && setup.acquisition.sourceModified === false && setup.scope.qualificationPending === true && setup.packageManager.version === plan.packageManagerEvidence.version && setup.packageManager.tarball === plan.packageManagerEvidence.tarball && setup.packageManager.integrity === plan.packageManagerEvidence.integrity && setup.acquisition.npmArchiveSha256 === plan.packageManagerEvidence.archive.sha256 && plan.packageManagerEvidence.invokedDuringCapture === false, `Agent progress recorded historical receipt/npm evidence ${name}`);
  const projection = parseJsonSupported(fixtureDocument(pinnedBytes(plan.projectionManifest, `Agent progress repository projection manifest ${name}`, true)).raw), projectionLock = parseJsonSupported(fixtureDocument(pinnedBytes(plan.projectionLock, `Agent progress repository projection lock ${name}`, true)).raw);
  requireCondition(pins.projectionManifestSha256 === plan.projectionManifest.sha256 && pins.projectionLockSha256 === plan.projectionLock.sha256 && plan.oracleProjection[0].sha256 === pins.projectionManifestSha256 && plan.oracleProjection[1].sha256 === pins.projectionLockSha256 && sameMetadata(setup.dependencies, plan.packages.map(({ name, version, resolved, integrity, license }) => ({ name, version, resolved, integrity, license }))) && sameMetadata(projection.dependencies, Object.fromEntries(plan.packages.map(row => [row.name, row.version]))), `Agent progress projection/setup dependency cross-pins ${name}`);
  requireCondition(sameMetadata(pins.installedPackages, plan.packages) && plan.packages.length === 2 && plan.packages.reduce((sum, row) => sum + row.files.files, 0) === 1394 && plan.packages.every(row => { const projected = projectionLock.packages[`node_modules/${row.name}`], acquisition = setup.acquisition.installedManifests.find(item => item.name === row.name); return keys(row, ["name", "version", "resolved", "integrity", "license", "archive", "files", "manifestSha256", "dependencyDeclarations", "optionalDependencies", "peerDependencies", "lifecycleScripts", "licenses"]) && projected && acquisition && projected.version === row.version && projected.resolved === row.resolved && projected.integrity === row.integrity && projected.license === row.license && acquisition.version === row.version && acquisition.sha256 === row.manifestSha256 && relativePath(row.archive.path) && digest(row.archive.sha256) && Number.isSafeInteger(row.archive.bytes) && row.archive.bytes > 0 && digest(row.files.sha256) && row.files.files === (row.name === "partial-json" ? 9 : row.name === "typebox" ? 1385 : -1) && ["dependencyDeclarations", "optionalDependencies", "peerDependencies", "lifecycleScripts"].every(field => sameMetadata(row[field], {})) && row.license === "MIT" && row.licenses.length === 1 && row.licenses.every(file => relativePath(file.path) && Buffer.byteLength(file.utf8Text, "utf8") === file.bytes && hash(Buffer.from(file.utf8Text, "utf8")) === file.sha256); }), `Agent progress two-package archive/SRI/tree/manifest/dependency/license cross-pins ${name}`);
  const harness = [
    { path: "tools/PiReferenceRunner/capture-agent-progress.mjs", bytes: 16247, sha256: "e5729f7cc984b5f5097a756fe55d4640c48944933eb4c63fe624492566196cb2" },
    { path: "tools/PiReferenceRunner/run-agent-progress.mjs", bytes: 21488, sha256: "d1bfb2c7f4685a4421a31de1cf394d517fca6150fad6f5d17a0f02b226802dd5" },
    plan.input, { path: planPath, bytes: 12487, sha256: planSha }, plan.offlineObserver, plan.offlineGuard, plan.archiveInspector, plan.rawComparator
  ];
  requireCondition(sameMetadata(lock.harnessFiles, harness) && sameMetadata(plan.harnessFiles, harness.map(row => row.path)) && new Set(harness.map(row => row.path)).size === 8, `Agent progress frozen harness/helper/comparator inventory ${name}`);
  for (const row of harness) pinnedBytes(currentPublicHarnessPin(row), `Agent progress repository harness ${row.path}`, true);
  requireCondition(Array.isArray(lock.loadedModules) && lock.loadedModules.length === 700 && new Set(lock.loadedModules.map(row => row.path)).size === 700 && lock.loadedModules.every(row => keys(row, ["path", "bytes", "sha256"]) && relativePath(row.path) && digest(row.sha256) && Number.isSafeInteger(row.bytes) && row.bytes > 0 && (row.path.startsWith("upstream/") || plan.packages.some(pkg => row.path.startsWith(`node_modules/${pkg.name}/`)))) && lock.loadedModules.filter(row => row.path.startsWith("upstream/")).length === 30 && lock.loadedModules.filter(row => row.path.startsWith("node_modules/partial-json/")).length === 2 && lock.loadedModules.filter(row => row.path.startsWith("node_modules/typebox/")).length === 668, `Agent progress admitted recorded 30-source/670-dependency loaded closure ${name}`);
  requireCondition(Array.isArray(lock.sourceHashes) && lock.sourceHashes.length === 30 && new Set(lock.sourceHashes.map(row => row.path)).size === 30 && lock.sourceHashes.every(row => { const loaded = lock.loadedModules.find(item => item.path === row.path), planned = plan.source.pins.find(item => `upstream/${item.path}` === row.path), canonical = baseline.artifacts.find(item => item.kind === "source-file" && `upstream/${item.path}` === row.path); return row.path.startsWith("upstream/") && loaded && sameMetadata(row, { ...loaded, canonicalGitBlobSha256: loaded.sha256, canonicalGitBlobBytes: loaded.bytes }) && (!planned || row.sha256 === planned.sha256 && row.bytes === planned.bytes) && (!canonical || row.sha256 === canonical.sha256 && row.bytes === canonical.bytes); }) && ["packages/agent/src/agent.ts", "packages/agent/src/agent-loop.ts", "packages/agent/src/stream-fn.ts", "packages/ai/src/utils/event-stream.ts", "packages/coding-agent/src/experimental/source-resolver.ts"].every(path => lock.sourceHashes.some(row => row.path === `upstream/${path}`)), `Agent progress canonical whole-source cross-pins ${name}`);
  requireCondition(hash(Buffer.from(canonicalRawJson(JSON.stringify(lock.loadedModules)))) === "e3c617c029299c252096ce1ad8cffa5eb285371c99f44a0b1e8d8da11e0383ba" && hash(Buffer.from(canonicalRawJson(JSON.stringify(lock.sourceHashes)))) === "db5af91f22dba65abea70e862a7f444ed6f0f7fd80b3fe6cc1f4fd38538855d0", `Agent progress immutable recorded module/source closure ${name}`);
  requireCondition(sameMetadata(lock.captureHistory, [{ kind: "initial genuine whole-Agent capture", goldenSha256: fixture.expected.sha256, sourceBytesChanged: false, childDateNowOverride: clock }]), `Agent progress frozen capture history ${name}`);
  agentProgressObservations(inputDocument, expectedDocument, name);
  requireCondition(fixture.expected.sha256 === "9646c803429bd3a56b1a1e8fc3f07c01060052f9f32e6b4021265f7249dc8d3e", `Agent progress immutable captured golden bytes ${name}`);
}

function agentProgressObservations(inputDocument, expectedDocument, name) {
  // Corpus numbers are never parsed into Number. Only string labels and bounded
  // diagnostic indexes are decoded; raw subtrees carry every captured value.
  const object = rootMembers, array = rawArrayItems, string = raw => parseJsonSupported(raw);
  const record = map => canonicalRawJson(`{${[...map].map(([key, value]) => `${JSON.stringify(key)}:${value}`).join(",")}}`);
  const absent = canonicalRawJson('{"ownUndefinedPaths":[""],"callablePaths":[],"setValues":[]}');
  const at = (raw, pointer) => {
    if (raw === undefined || pointer === "") return raw;
    for (const token of pointer.slice(1).split("/")) {
      const key = token.replaceAll("~1", "/").replaceAll("~0", "~");
      if (raw?.startsWith("{")) raw = object(raw).get(key);
      else if (raw?.startsWith("[") && /^(0|[1-9][0-9]*)$/.test(key)) raw = array(raw)[Number(key)];
      else return undefined;
    }
    return raw;
  };
  function wrapper(raw, label) {
    const value = object(raw), pointer = path => typeof path === "string" && (path === "" || path.startsWith("/") && !/~(?![01])/.test(path));
    requireCondition(hasKeys(value, ["ownUndefinedPaths", "callablePaths", "setValues", ...(value.has("json") ? ["json"] : [])]), `Agent progress JSON observation schema ${label}`);
    const undefinedPaths = array(value.get("ownUndefinedPaths")).map(string), callablePaths = array(value.get("callablePaths")).map(string), sets = array(value.get("setValues")).map(object);
    requireCondition([undefinedPaths, callablePaths].every(paths => new Set(paths).size === paths.length && paths.every(pointer)) && new Set([...undefinedPaths, ...callablePaths]).size === undefinedPaths.length + callablePaths.length && undefinedPaths.every(path => at(value.get("json"), path) === undefined) && callablePaths.every(path => at(value.get("json"), path) === undefined) && (value.has("json") ? !undefinedPaths.includes("") : raw === absent), `Agent progress own-undefined/function omission identity ${label}`);
    requireCondition(new Set(sets.map(row => row.get("path"))).size === sets.length && sets.every(row => hasKeys(row, ["path", "values"]) && pointer(string(row.get("path"))) && at(value.get("json"), string(row.get("path"))) === "{}" && array(row.get("values")).length === new Set(array(row.get("values"))).size), `Agent progress original Set serialization/inventory ${label}`);
    return value.get("json");
  }
  const observations = object(expectedDocument.members.get("observations")), cases = array(observations.get("cases")), inputs = array(inputDocument.members.get("cases"));
  requireCondition(hasKeys(observations, ["cases", "seams"]) && cases.length === 3 && inputs.length === 3, `Agent progress observation/case envelope ${name}`);
  requireCondition(compareRawJson(observations.get("seams"), JSON.stringify({ source: "Whole unchanged Agent public constructor/prompt/subscribe/abort/waitForIdle; unchanged agent-loop execution/finalization", stream: "Authored AssistantMessageEventStream frames, one fake request per case", tools: "Authored execute callbacks/results/errors, beforeToolCall/afterToolCall hook returns, finishTurn action end", barriers: "Explicit promise gates; no sleeps or source scheduler replacement", childDateNowOverride: parseJsonSupported(inputDocument.members.get("clock")), jsonObservation: "JSON.stringify value plus own-undefined/function/Set inventories; no field filtering", networkAndProcessesBlocked: true })), `Agent progress complete disclosed observation seams ${name}`);
  const ids = ["parallel-progress-hooks-and-structured-content", "sequential-absent-null-fields", "abort-during-awaited-update-and-late-callback"], modes = ["parallel-blocked-update", "sequential-field-shapes", "abort-blocked-update"];
  const expectedTypes = [
    ["agent_start", "turn_start", "message_start", "message_end", "message_start", ...Array(6).fill("message_update"), "message_end", "tool_execution_start", "tool_execution_start", "tool_execution_update", "tool_execution_update", "tool_execution_update", "tool_execution_end", "tool_execution_end", "message_start", "message_end", "message_start", "message_end", "turn_end", "agent_end"],
    ["agent_start", "turn_start", "message_start", "message_end", "message_start", ...Array(6).fill("message_update"), "message_end", "tool_execution_start", "tool_execution_update", "tool_execution_end", "message_start", "message_end", "tool_execution_start", "tool_execution_update", "tool_execution_end", "message_start", "message_end", "turn_end", "agent_end"],
    ["agent_start", "turn_start", "message_start", "message_end", "message_start", ...Array(3).fill("message_update"), "message_end", "tool_execution_start", "tool_execution_update", "tool_execution_update", "tool_execution_update", "tool_execution_end", "message_start", "message_end", "turn_end", "agent_end"]
  ];
  const eventFields = new Map([["agent_start", []], ["turn_start", []], ["message_start", ["message"]], ["message_end", ["message"]], ["message_update", ["message", "assistantMessageEvent"]], ["tool_execution_start", ["toolCallId", "toolName", "args"]], ["tool_execution_update", ["toolCallId", "toolName", "args", "partialResult"]], ["tool_execution_end", ["toolCallId", "toolName", "result", "isError"]], ["turn_end", ["message", "toolResults"]], ["agent_end", ["messages"]]]);
  for (let index = 0; index < cases.length; index++) {
    const authored = object(inputs[index]), observed = object(cases[index]), label = `${name} ${index}`, tools = array(authored.get("tools")).map(object), calls = tools.map(tool => object(tool.get("call"))), toolIds = calls.map(call => call.get("id"));
    requireCondition(hasKeys(observed, ["caseId", "mode", "initialState", "requests", "providerFrames", "events", "listenerTrace", "toolTrace", "hooks", "controls", "observations", "finalState", "promptReturn", "idleReturn", "metrics"]) && observed.get("caseId") === JSON.stringify(ids[index]) && observed.get("caseId") === authored.get("caseId") && observed.get("mode") === JSON.stringify(modes[index]) && observed.get("mode") === authored.get("mode") && new Set(toolIds).size === tools.length, `Agent progress ordered case identity/schema ${label}`);
    const state = (raw, location) => {
      const json = object(wrapper(raw, location)), own = object(raw), functions = tools.map((_, position) => `/tools/${position}/execute`);
      requireCondition(json.get("model") === inputDocument.members.get("model") && json.get("systemPrompt") === inputDocument.members.get("systemPrompt") && json.get("thinkingLevel") === '"off"' && compareRawJson(own.get("callablePaths"), JSON.stringify(functions)) && array(json.get("tools")).length === tools.length && json.get("pendingToolCalls") === "{}" && array(own.get("setValues")).length === 1 && object(array(own.get("setValues"))[0]).get("path") === '"/pendingToolCalls"', `Agent progress complete public state/function/Set identity ${location}`);
      return json;
    };
    const initial = state(observed.get("initialState"), `${label} initial`), final = state(observed.get("finalState"), `${label} final`);
    requireCondition(initial.get("isStreaming") === "false" && final.get("isStreaming") === "false" && object(array(object(observed.get("finalState")).get("setValues"))[0]).get("values") === "[]" && observed.get("promptReturn") === absent && observed.get("idleReturn") === absent, `Agent progress public settled/undefined return facts ${label}`);
    const requests = array(observed.get("requests")).map(object);
    requireCondition(requests.length === 1 && hasKeys(requests[0], ["model", "context", "options", "signalAborted"]) && wrapper(requests[0].get("model"), `${label} request model`) === inputDocument.members.get("model") && requests[0].get("signalAborted") === "false", `Agent progress one authored fake-provider request ${label}`);
    wrapper(requests[0].get("context"), `${label} request context`);
    const options = object(wrapper(requests[0].get("options"), `${label} request options`));
    requireCondition(hasKeys(options, ["model", "transport", "toolExecution", "signal"]) && options.get("model") === inputDocument.members.get("model") && options.get("toolExecution") === authored.get("toolExecution") && options.get("signal") === "{}" && array(object(requests[0].get("options")).get("ownUndefinedPaths")).includes('"/apiKey"'), `Agent progress absent credential/actual signal/options inventories ${label}`);
    const frames = array(observed.get("providerFrames")).map((raw, position) => object(wrapper(raw, `${label} provider ${position}`)));
    requireCondition(compareRawJson(JSON.stringify(frames.map(frame => string(frame.get("type")))), JSON.stringify(["start", ...tools.flatMap(() => ["toolcall_start", "toolcall_delta", "toolcall_end"]), "done"])), `Agent progress retained authored provider frame order ${label}`);
    for (let position = 0; position < tools.length; position++) {
      const delta = frames[2 + position * 3], end = frames[3 + position * 3];
      requireCondition(delta.get("contentIndex") === String(position) && canonicalRawJson(string(delta.get("delta"))) === calls[position].get("arguments") && end.get("toolCall") === record(new Map([["type", '"toolCall"'], ...calls[position]])), `Agent progress raw provider argument/call identity ${label} ${position}`);
    }
    const events = array(observed.get("events")).map(object), eventJson = events.map((event, position) => {
      requireCondition(hasKeys(event, ["index", "event", "stateBeforeListener", "signalAborted"]) && event.get("index") === String(position) && ["true", "false"].includes(event.get("signalAborted")), `Agent progress emission entry index/schema ${label} ${position}`);
      state(event.get("stateBeforeListener"), `${label} emission ${position}`);
      const json = object(wrapper(event.get("event"), `${label} event ${position}`)), type = string(json.get("type"));
      requireCondition(eventFields.has(type) && hasKeys(json, ["type", ...eventFields.get(type)]), `Agent progress complete event schema ${label} ${position}`);
      return json;
    });
    requireCondition(sameMetadata(eventJson.map(event => string(event.get("type"))), expectedTypes[index]), `Agent progress all retained source event identities/order ${label}`);
    const updates = eventJson.filter(event => event.get("type") === '"tool_execution_update"'), ends = eventJson.filter(event => event.get("type") === '"tool_execution_end"'), messageUpdates = eventJson.filter(event => event.get("type") === '"message_update"'), messages = eventJson.filter(event => event.get("type") === '"message_end"').map(event => object(event.get("message"))), toolMessages = messages.filter(message => message.get("role") === '"toolResult"');
    requireCondition(messageUpdates.every((event, position) => event.get("assistantMessageEvent") === record(frames[position + 1])) && toolMessages.length === tools.length && sameMetadata(toolMessages.map(message => string(message.get("toolCallId"))), toolIds.map(string)) && eventJson.at(-1).get("messages") === canonicalRawJson(`[${array(final.get("messages")).slice(1).join(",")}]`) && array(final.get("messages")).slice(1).every((raw, position) => raw === record(messages[position])), `Agent progress provider-to-event/final-message raw cross-pins ${label}`);
    const trace = array(observed.get("listenerTrace")).map(object); let active = 0, maximum = 0;
    for (let position = 0; position < trace.length; position++) {
      const row = trace[position], kind = string(row.get("kind")), number = row.get("index"), first = kind.startsWith("first_");
      requireCondition(["first_enter", "first_exit", "second_enter", "second_exit"].includes(kind) && /^(0|[1-9][0-9]*)$/.test(number) && Number(number) < events.length && hasKeys(row, ["kind", "index", "type", "signalAborted", ...(first ? ["activeListeners"] : [])]) && row.get("type") === eventJson[Number(number)].get("type") && ["true", "false"].includes(row.get("signalAborted")), `Agent progress listener trace schema/event identity ${label} ${position}`);
      if (kind === "first_enter") { active++; maximum = Math.max(maximum, active); }
      if (first) requireCondition(row.get("activeListeners") === String(active) && active > 0, `Agent progress listener overlap accounting ${label} ${position}`);
      if (kind === "first_exit") active--;
    }
    requireCondition(active === 0 && trace.length === events.length * 4 && events.every((_, position) => sameMetadata(trace.filter(row => row.get("index") === String(position)).map(row => string(row.get("kind"))), ["first_enter", "first_exit", "second_enter", "second_exit"])) && maximum === (index === 1 ? 1 : 2), `Agent progress complete two-listener/overlap facts ${label}`);
    const toolTrace = array(observed.get("toolTrace")).map(object), invoked = toolTrace.filter(row => row.get("kind") === '"invoke_update"'), callbackReturns = toolTrace.filter(row => row.get("kind") === '"update_callback_return"');
    const toolFields = new Map([["execute_enter", "args"], ["invoke_update", "partial"], ["update_callback_return", "returned"], ["execute_return", "result"], ["execute_throw", "errorMessage"], ["tool_signal_abort", undefined]]);
    for (const row of toolTrace) {
      const kind = string(row.get("kind")), field = toolFields.get(kind), toolIndex = toolIds.indexOf(row.get("id"));
      requireCondition(toolFields.has(kind) && toolIndex >= 0 && hasKeys(row, ["kind", "id", "signalAborted", ...(field ? [field] : [])]) && ["true", "false"].includes(row.get("signalAborted")), `Agent progress full tool trace schema/identity ${label}`);
      if (field && field !== "errorMessage") wrapper(row.get(field), `${label} tool ${kind}`);
      if (kind === "execute_enter") requireCondition(object(row.get("args")).get("json") === calls[toolIndex].get("arguments"), `Agent progress execute raw argument identity ${label}`);
      if (kind === "execute_return") requireCondition(object(row.get("result")).get("json") === tools[toolIndex].get("result"), `Agent progress complete authored raw tool return ${label}`);
      if (kind === "update_callback_return") requireCondition(row.get("returned") === absent, `Agent progress actual void callback return ${label}`);
    }
    requireCondition(invoked.length === updates.length && callbackReturns.length === invoked.length && invoked.every((row, position) => row.get("id") === updates[position].get("toolCallId") && object(row.get("partial")).get("json") === updates[position].get("partialResult")), `Agent progress accepted callback/partial event raw identity ${label}`);
    for (let position = 0; position < tools.length; position++) {
      const actual = invoked.filter(row => row.get("id") === toolIds[position]).map(row => object(row.get("partial")).get("json")), authoredUpdates = array(tools[position].get("updates"));
      requireCondition(sameMetadata(actual, [...authoredUpdates, ...(index === 2 ? [tools[position].get("updateAfterAbort")] : [])]), `Agent progress all authored update values/numeric/null identity ${label} ${position}`);
    }
    const hooks = array(observed.get("hooks")).map(object);
    requireCondition(hooks.length === tools.length * 2 + 1 && hooks.at(-1).get("kind") === '"finishTurn"', `Agent progress complete hook census ${label}`);
    for (const hook of hooks) {
      const kind = string(hook.get("kind")), context = object(wrapper(hook.get("context"), `${label} hook context`)); wrapper(hook.get("returned"), `${label} hook return`);
      requireCondition(hasKeys(hook, ["kind", "context", "signalAborted", "returned"]) && ["true", "false"].includes(hook.get("signalAborted")) && hasKeys(context, kind === "finishTurn" ? ["message", "toolResults", "context", "newMessages"] : kind === "beforeToolCall" ? ["assistantMessage", "toolCall", "args", "context"] : ["assistantMessage", "toolCall", "args", "result", "isError", "context"]), `Agent progress complete hook context/return schema ${label}`);
      requireCondition(compareRawJson(object(hook.get("context")).get("callablePaths"), JSON.stringify(tools.map((_, position) => `/context/tools/${position}/execute`))), `Agent progress unfiltered hook tool functions ${label}`);
      if (kind === "finishTurn") requireCondition(object(hook.get("returned")).get("json") === '{"action":"end"}' && context.get("toolResults") === canonicalRawJson(`[${toolMessages.map(record).join(",")}]`), `Agent progress authored finishTurn/all tool messages ${label}`);
      else {
        const toolIndex = toolIds.indexOf(object(context.get("toolCall")).get("id")), field = kind === "beforeToolCall" ? "beforeHook" : "afterHook";
        requireCondition(["beforeToolCall", "afterToolCall"].includes(kind) && toolIndex >= 0 && context.get("toolCall") === record(new Map([["type", '"toolCall"'], ...calls[toolIndex]])) && context.get("args") === calls[toolIndex].get("arguments") && (tools[toolIndex].has(field) ? object(hook.get("returned")).get("json") === tools[toolIndex].get(field) : hook.get("returned") === absent), `Agent progress raw hook call/arguments/authored return ${label}`);
      }
    }
    for (let position = 0; position < tools.length; position++) {
      const tool = tools[position], id = toolIds[position], end = ends.find(row => row.get("toolCallId") === id), message = toolMessages.find(row => row.get("toolCallId") === id), after = hooks.find(row => row.get("kind") === '"afterToolCall"' && object(object(row.get("context")).get("json")).get("toolCall") === record(new Map([["type", '"toolCall"'], ...calls[position]])));
      requireCondition(end && message && after && end.get("toolName") === calls[position].get("name"), `Agent progress tool end/hook/message identity ${label} ${position}`);
      const beforeResult = object(object(after.get("context")).get("json")).get("result"), result = new Map(object(beforeResult)), replacement = tool.has("afterHook") ? object(tool.get("afterHook")) : new Map();
      for (const field of ["content", "details", "usage", "terminate"]) if (replacement.has(field) && replacement.get(field) !== "null") result.set(field, replacement.get(field));
      if (replacement.has("structuredContent") && replacement.get("structuredContent") !== "null") result.set("structuredContent", replacement.get("structuredContent"));
      else if (replacement.has("content") && !["null", "false", "0", '""'].includes(replacement.get("content"))) result.delete("structuredContent");
      requireCondition(end.get("result") === record(result) && end.get("isError") === (replacement.get("isError") ?? object(object(after.get("context")).get("json")).get("isError")), `Agent progress observed raw hook/result replacement facts ${label} ${position}`);
      const projection = new Map([["role", '"toolResult"'], ["toolCallId", id], ["toolName", calls[position].get("name")], ["content", result.has("content") && result.get("content") !== "null" ? result.get("content") : "[]"], ["isError", end.get("isError")], ["timestamp", "1700000000000"]]);
      for (const field of ["details", "usage"]) if (result.has(field)) projection.set(field, result.get(field));
      requireCondition(record(message) === record(projection) && !message.has("structuredContent") && !message.has("terminate"), `Agent progress raw result versus stored message/null-missing projection ${label} ${position}`);
    }
    const controls = array(observed.get("controls")).map(object), late = controls.filter(row => ['"late_callback_after_tool_end"', '"late_callback_after_idle"'].includes(row.get("kind")));
    for (const row of late) requireCondition(hasKeys(row, ["kind", "id", "eventCountBefore", "eventCountAfter", "returned"]) && toolIds.includes(row.get("id")) && row.get("eventCountBefore") === String(events.length) && row.get("eventCountAfter") === row.get("eventCountBefore") && row.get("returned") === absent, `Agent progress actual ignored late callback/no-event facts ${label}`);
    requireCondition(late.length === tools.length + 1 && late.at(-1).get("kind") === '"late_callback_after_idle"', `Agent progress complete late callback census ${label}`);
    const probes = array(observed.get("observations")).map(object);
    for (let position = 0; position < probes.length; position++) {
      const probe = probes[position], count = probe.get("eventCount"), settled = probe.get("kind") === '"public_prompt_and_idle_settled"', probeState = state(probe.get("state"), `${label} probe ${position}`), signal = wrapper(probe.get("activeSignal"), `${label} probe signal ${position}`);
      requireCondition(hasKeys(probe, ["kind", "eventCount", "callbackCount", "acceptedUpdateCount", "afterHookIds", "toolEndIds", "toolMessageIds", "activeListeners", "idleResolved", "promptResolved", "state", "activeSignal", "signalAborted"]) && /^(0|[1-9][0-9]*)$/.test(count) && Number(count) <= events.length && probe.get("acceptedUpdateCount") === String(eventJson.slice(0, Number(count)).filter(row => row.get("type") === '"tool_execution_update"').length) && probe.get("callbackCount") === probe.get("acceptedUpdateCount") && probe.get("idleResolved") === String(settled) && probe.get("promptResolved") === String(settled) && probe.get("activeListeners") === (settled ? "0" : "1") && probeState.get("isStreaming") === String(!settled) && (settled ? signal === undefined && probe.get("signalAborted") === "null" : signal === "{}" && ["true", "false"].includes(probe.get("signalAborted"))), `Agent progress barrier/idle/signal/event-count facts ${label} ${position}`);
      requireCondition(probe.get("toolEndIds") === canonicalRawJson(JSON.stringify(eventJson.slice(0, Number(count)).filter(row => row.get("type") === '"tool_execution_end"').map(row => string(row.get("toolCallId"))))) && probe.get("toolMessageIds") === canonicalRawJson(JSON.stringify(eventJson.slice(0, Number(count)).filter(row => row.get("type") === '"message_end"' && object(row.get("message")).get("role") === '"toolResult"').map(row => string(object(row.get("message")).get("toolCallId"))))), `Agent progress checkpoint tool/message order facts ${label} ${position}`);
    }
    requireCondition(compareRawJson(observed.get("metrics"), JSON.stringify({ callbackCount: invoked.length, lateCallbackCount: late.length, acceptedUpdateCount: updates.length, maxActiveFirstListeners: maximum })), `Agent progress derived callback/event/overlap metrics ${label}`);
    if (index === 0) requireCondition(probes[0].get("afterHookIds") === "[]" && probes[1].get("afterHookIds") === '["call-beta"]' && probes[1].get("toolEndIds") === '["call-beta"]' && ends[0].get("toolCallId") === '"call-beta"' && ends[1].get("toolCallId") === '"call-alpha"', `Agent progress parallel unrelated finalization/blocked update facts ${label}`);
    if (index === 2) requireCondition(toolTrace.some(row => row.get("kind") === '"tool_signal_abort"' && row.get("signalAborted") === "true") && invoked.at(-1).get("signalAborted") === "true" && events.find(event => object(object(event.get("event")).get("json")).get("partialResult") === tools[0].get("updateAfterAbort"))?.get("signalAborted") === "true" && toolTrace.at(-1).get("kind") === '"execute_throw"' && toolTrace.at(-1).get("errorMessage") === tools[0].get("abortError") && ends[0].get("isError") === "true", `Agent progress actual abort propagation/accepted third update/error facts ${label}`);
  }
  // Immutable canonical raw content seals every field, including opaque values
  // and diagnostic facts beyond the explicit cross-checks above. It grants no
  // native parity, re-execution, external installed-byte or license closure.
  requireCondition(hash(Buffer.from(record(observations))) === "6b5d749897ef8b49274d51e313e5ae6fddf9573f884c28aca78793293ce28064", `Agent progress immutable raw observation cases ${name}`);
}

function git(...gitArgs) {
  const child = spawnSync("git", ["-c", `safe.directory=${resolve(upstream)}`, "-C", upstream, ...gitArgs], { maxBuffer: 64 * 1024 * 1024, windowsHide: true });
  if (child.status !== 0) throw new Error(child.stderr.toString());
  return child.stdout;
}

try {
  const baseline = read("compatibility/baseline.lock.json");
  const surfaces = read("compatibility/surfaces.json");
  const parity = read("compatibility/parity.json");
  const provenance = read("compatibility/provenance.json");
  const manifest = read("fixtures/pi-v0.99.1/manifest.json");
  const manifestPaths = discoverManifests();
  const manifests = manifestPaths.map(path => ({ path, document: read(path) }));
  const sha = baseline.source.commit;
  requireCondition(sha === "d86654abb8862e201933517d6f1fce9f88dd117f", "Pinned upstream SHA");
  for (const document of [surfaces, parity, provenance, manifest]) requireCondition(document.sourceSha === sha, "Manifest source SHA matches baseline");
  const requirementIds = new Set(surfaces.requirements.map(row => row.id));
  requireCondition(requirementIds.size === surfaces.requirements.length, "Unique requirement IDs");
  const parityIds = new Set(parity.rows.map(row => row.requirementId));
  requireCondition(parityIds.size === parity.rows.length && [...requirementIds].every(id => parityIds.has(id)) && parity.rows.length === surfaces.requirements.length, "Parity rows exactly match inventory IDs");
  const sourceArtifacts = baseline.artifacts.filter(artifact => artifact.kind === "source-file");
  const sourcePaths = new Set(sourceArtifacts.map(artifact => artifact.path));
  requireCondition(sourcePaths.size === sourceArtifacts.length, "Unique locked source files");
  requireCondition(surfaces.requirements.filter(row => row.family === "chat-api").length === 10, "All ten chat API IDs are seeded");
  for (const family of surfaces.requiredFamilies) requireCondition(surfaces.requirements.some(row => row.family === family), `Surface family ${family} is seeded`);
  const fixtures = [];
  for (const family of manifests) {
    const profile = standaloneProfiles.get(family.path);
    requireCondition(profile || Object.hasOwn(family.document, "fixtures"), `Unregistered standalone fixture manifest ${family.path}`);
    requireCondition(family.document.schemaVersion === 1 && (profile ? !Object.hasOwn(family.document, "fixtures") && family.document.fixtureId === profile.fixtureId : Array.isArray(family.document.fixtures)), `Fixture manifest schema ${family.path}`);
    requireCondition(family.document.sourceSha === sha, `Fixture manifest baseline ${family.path}`);
    const normalizer = Object.hasOwn(family.document, "normalizerVersion") ? family.document.normalizerVersion : manifest.normalizerVersion;
    requireCondition(normalizer === (profile?.normalizer ?? NORMALIZER_VERSION), `Fixture manifest normalizer ${family.path}`);
    family.fixtureGroups = profile ? 1 : family.document.fixtures.length;
    for (const fixture of (profile ? [family.document] : family.document.fixtures)) fixtures.push({ fixture, normalizer, manifestPath: family.path });
  }
  const fixtureIds = new Set(fixtures.map(row => row.fixture.fixtureId));
  requireCondition(fixtureIds.size === fixtures.length, "Unique fixture IDs across all manifests");
  for (const row of surfaces.requirements) {
    requireCondition(row.source.sha === sha && row.source.url === `https://github.com/earendil-works/pi/blob/${sha}/${row.source.path}`, `Pinned source link ${row.id}`);
    requireCondition(sourcePaths.has(row.source.path), `Source hash exists for ${row.id}`);
    requireCondition(row.owner && row.testId && Number.isInteger(row.targetPhase) && row.targetPhase >= 1 && row.targetPhase <= 8, `Owner/test/phase metadata for ${row.id}`);
    const parityRow = parity.rows.find(candidate => candidate.requirementId === row.id);
    requireCondition(parityRow.status === row.status && parityRow.mandatory === row.mandatory, `Parity classification for ${row.id}`);
    for (const id of row.fixtureIds) requireCondition(fixtureIds.has(id), `Linked fixture exists ${id}`);
  }
  const kindCounts = new Map();
  const dependencyLocks = new Set();
  const sdkOracleLocks = new Set();
  const fileQueueOracleLocks = new Set();
  const sessionOracleLocks = new Set();
  const rpcOracleLocks = new Set();
  const editOracleLocks = new Set();
  const anthropicOracleLocks = new Set();
  const agentProgressOracleLocks = new Set();
  const recordedNonInventoryLabels = new Set();
  for (const { fixture, normalizer, manifestPath } of fixtures) {
    const profile = standaloneProfiles.get(manifestPath), sdk = manifestPath === sdkManifestPath || manifestPath === toolsSdkManifestPath;
    requireCondition(typeof fixture.fixtureId === "string" && fixture.fixtureId.trim().length > 0, "Fixture ID is a nonempty string");
    const fixtureNormalizer = Object.hasOwn(fixture, "normalizerVersion") ? fixture.normalizerVersion : normalizer;
    requireCondition(fixture.sourceSha === sha && fixtureNormalizer === (profile?.normalizer ?? NORMALIZER_VERSION), `Fixture baseline/normalizer ${fixture.fixtureId}`);
    requireCondition(fixture.clock && fixture.seed && fixture.provenance && (profile || fixture.environment && fixture.scenario), `Fixture provenance/clock/environment ${fixture.fixtureId}`);
    requireCondition(Array.isArray(fixture.requirementIds) && fixture.requirementIds.length > 0 && new Set(fixture.requirementIds).size === fixture.requirementIds.length, `Fixture requirement IDs ${fixture.fixtureId}`);
    for (const id of fixture.requirementIds) {
      requireCondition(requirementIds.has(id) || nonInventoryLabels.get(manifestPath)?.has(id), `Fixture requirement ${id}`);
      if (!requirementIds.has(id)) recordedNonInventoryLabels.add(id);
    }
    const payloads = {};
    for (const field of ["input", "expected"]) {
      const file = localPath(fixture[field].path);
      const bytes = existsSync(file) ? readFileSync(file) : null;
      requireCondition(bytes && hash(bytes) === fixture[field].sha256, `Fixture ${field} SHA256 ${fixture.fixtureId}`);
      payloads[field] = bytes;
    }
    const inputDocument = fixtureDocument(payloads.input);
    const expectedDocument = fixtureDocument(payloads.expected);
    const input = inputDocument.metadata;
    const expected = expectedDocument.metadata;
    const kind = profile ? fixture.expected.kind : fixture.provenance.kind;
    requireCondition(profile ? kind === profile.kind : kind === "authored-synthetic-contract" || captureKinds.has(kind), `Supported fixture provenance kind ${fixture.fixtureId}`);
    if (Object.hasOwn(fixture.provenance, "sourceSha")) requireCondition(fixture.provenance.sourceSha === sha, `Provenance baseline ${fixture.fixtureId}`);
    if (sdk) {
      responsesSdkEvidence(fixture, inputDocument, expectedDocument, baseline, manifestPath === toolsSdkManifestPath);
      sdkOracleLocks.add(fixture.lock.path);
    } else if (manifestPath === fileQueueManifestPath) {
      fileMutationQueueEvidence(fixture, inputDocument, expectedDocument, baseline);
      fileQueueOracleLocks.add(fixture.lock.path);
    } else if (manifestPath === sessionManifestPath) {
      sessionContextEvidence(fixture, inputDocument, expectedDocument, baseline);
      sessionOracleLocks.add(fixture.lock.path);
    } else if (manifestPath === rpcManifestPath) {
      rpcJsonlEvidence(fixture, inputDocument, expectedDocument, baseline);
      rpcOracleLocks.add(fixture.lock.path);
    } else if (manifestPath === editManifestPath) {
      editEvidence(fixture, inputDocument, expectedDocument, baseline);
      editOracleLocks.add(fixture.lock.path);
    } else if (manifestPath === anthropicManifestPath) {
      anthropicSdkEvidence(fixture, inputDocument, expectedDocument, baseline);
      anthropicOracleLocks.add(fixture.lock.path);
    } else if (manifestPath === agentProgressManifestPath) {
      agentProgressEvidence(fixture, inputDocument, expectedDocument, baseline);
      agentProgressOracleLocks.add(fixture.lock.path);
    } else if (captureKinds.get(kind)?.sourceOnly) {
      truncationEvidence(fixture, inputDocument, expectedDocument, payloads, baseline);
    } else {
      requireCondition(input.fixtureId === fixture.fixtureId && input.sourceSha === sha, `Input identity ${fixture.fixtureId}`);
      requireCondition(input.normalizerVersion === fixtureNormalizer, `Input normalizer ${fixture.fixtureId}`);
      if (kind === "authored-synthetic-contract") {
        requireCondition(input.kind === kind && input.provenance?.kind === kind && fixture.provenance.upstreamOutput === false && input.provenance.upstreamOutput === false, `Authored provenance ${fixture.fixtureId}`);
        requireCondition(inputDocument.members.has("expected") && compareRawJson(inputDocument.members.get("expected"), expectedDocument.raw), `Authored expectation consistency ${fixture.fixtureId}`);
      } else {
        const capture = captureKinds.get(kind);
        requireCondition(input.kind === capture.inputKind && input.provenance?.kind === "authored-synthetic-input", `Oracle input provenance ${fixture.fixtureId}`);
        if (capture.legacyExpected) {
          requireCondition(fixture.provenance.sourceModule && sourcePaths.has(fixture.provenance.sourceModule) && existsSync(localPath(fixture.provenance.captureTool)), `Queue capture provenance ${fixture.fixtureId}`);
        } else {
          requireCondition(fixture.provenance.inputKind === (capture.provenanceInputKind ?? "authored-synthetic-input") && fixture.provenance.source && fixture.provenance.captureCommand && fixture.provenance.capturedAt, `Capture provenance ${fixture.fixtureId}`);
          requireCondition(expected.fixtureId === fixture.fixtureId && expected.sourceSha === sha && expected.kind === kind && expected.normalizerVersion === fixtureNormalizer, `Oracle expected identity ${fixture.fixtureId}`);
          const lockPath = fixture.provenance.dependencyLock;
          const lock = read(lockPath);
          requireCondition(lock.schemaVersion === 1 && (lock.sourceSha ?? lock.environmentPins?.sourceSha) === sha, `Dependency lock baseline ${fixture.fixtureId}`);
          dependencyLocks.add(lockPath);
          if (kind === "captured-upstream-finish-decisions-oracle") finishDecisionEvidence(fixture, expectedDocument, lock, baseline);
        }
      }
    }
    for (const [field, value] of [["fixtureId", fixture.fixtureId], ["sourceSha", sha], ["normalizerVersion", fixtureNormalizer]])
      if (Object.hasOwn(expected, field)) requireCondition(expected[field] === value, `Expected ${field} ${fixture.fixtureId}`);
    kindCounts.set(kind, (kindCounts.get(kind) ?? 0) + 1);
  }
  requireCondition(hash(readFileSync(process.execPath)) === baseline.referenceRuntime.sha256 && process.version === baseline.referenceRuntime.version, "Exact development Node executable");
  let verifiedSourceFiles = 0;
  let sourceArchiveVerified = false;
  if (upstream) {
    requireCondition(git("rev-parse", "HEAD").toString().trim() === sha && git("status", "--porcelain", "--untracked-files=all").toString().trim() === "", "Unmodified upstream checkout at pinned SHA");
    requireCondition(git("rev-parse", `${sha}^{tree}`).toString().trim() === baseline.source.tree && git("rev-parse", "v0.99.1^{commit}").toString().trim() === sha, "Tree and tag resolve to locked source");
    for (const artifact of sourceArtifacts) {
      const blob = git("show", `${sha}:${artifact.path}`);
      const checkout = readFileSync(resolve(upstream, artifact.path));
      requireCondition(hash(blob) === artifact.sha256 && blob.length === artifact.bytes && hash(checkout) === artifact.checkoutSha256 && checkout.length === artifact.checkoutBytes, `Canonical blob and checkout bytes ${artifact.path}`);
      verifiedSourceFiles++;
    }
    const archive = git("archive", "--format=tar", sha);
    const lockedArchive = baseline.artifacts.find(artifact => artifact.kind === "source-archive");
    sourceArchiveVerified = hash(archive) === lockedArchive.sha256 && archive.length === lockedArchive.bytes;
    requireCondition(sourceArchiveVerified, "Reproduced canonical source tar SHA256");
  }
  const mandatoryDeferred = parity.rows.filter(row => row.mandatory && row.status === "Deferred").length;
  const report = { publicDerivativeVerification: publicDerivativeVerificationScope, schemaVersion: 1, sourceSha: sha, scope: "Fixture-group byte/metadata integrity only; no capture re-execution or behavioral/native parity qualification", checksPassed: checks.length, requirementCount: requirementIds.size, mandatoryDeferred, fixtureCount: fixtureIds.size, fixtureManifestCount: manifests.length, fixtureManifests: manifests.map(family => ({ path: family.path, fixtureGroups: family.fixtureGroups })), authoredFixtures: kindCounts.get("authored-synthetic-contract") ?? 0, upstreamQueueOracles: kindCounts.get("captured-upstream-oracle") ?? 0, evidenceKindCounts: Object.fromEntries([...kindCounts].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)), dependencyLockFilesReferenced: dependencyLocks.size, sdkOracleLockFilesReferenced: sdkOracleLocks.size, fileQueueOracleLockFilesReferenced: fileQueueOracleLocks.size, sessionOracleLockFilesReferenced: sessionOracleLocks.size, rpcOracleLockFilesReferenced: rpcOracleLocks.size, editOracleLockFilesReferenced: editOracleLocks.size, nonInventoryFixtureLabels: [...recordedNonInventoryLabels].sort(), verifiedSourceFiles, sourceArchiveVerified, fullNativeParity: "blocked", phase1Gate: "open", baselineGapIds: baseline.gaps.map(gap => gap.id) };
  report.anthropicOracleLockFilesReferenced = anthropicOracleLocks.size;
  report.agentProgressOracleLockFilesReferenced = agentProgressOracleLocks.size;
  if (output) writeFileSync(resolve(output), `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify(report, null, 2));
} catch (error) {
  console.error(JSON.stringify({ checksPassed: checks.filter(check => check.passed).length, error: error.message }));
  process.exitCode = 1;
}
