import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { canonicalRawJson, compareRawJson, parseJsonSupported } from "./raw-json.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const family = "fixtures/pi-v0.99.1/agent-progress/manifest.json";
const goldenPath = "fixtures/pi-v0.99.1/agent-progress/core.expected.json";
const lockPath = "fixtures/pi-v0.99.1/agent-progress/oracle.lock.json";
const inputPath = "tools/PiReferenceRunner/agent-progress-inputs.json";
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`);

function inside(root, path) {
  const full = resolve(root, path), within = relative(realpathSync(root), full);
  assert.ok(within && !isAbsolute(within) && within !== ".." && !within.startsWith(`..${sep}`), "Refusing operation outside owned temporary copy");
  return full;
}

function isolated(action) {
  const temporaryRoot = realpathSync(tmpdir()), scratch = mkdtempSync(join(temporaryRoot, "pisharp-agent-progress-integrity-"));
  try {
    for (const path of ["compatibility", "fixtures/pi-v0.99.1", "tools/CompatibilityReport", "tools/PiReferenceRunner"])
      cpSync(join(repo, path), inside(scratch, path), { recursive: true });
    return action(scratch);
  } finally {
    const physical = realpathSync(scratch), within = relative(temporaryRoot, physical);
    assert.ok(within && !isAbsolute(within) && within !== ".." && !within.startsWith(`..${sep}`) && within.startsWith("pisharp-agent-progress-integrity-"), "Refusing cleanup outside owned temporary root");
    rmSync(physical, { recursive: true, force: true });
  }
}

function check(root) {
  // Repository metadata only. No oracle, capture, source import, restore or build.
  return spawnSync(process.execPath, [inside(root, "tools/CompatibilityReport/check.mjs")], { cwd: root, windowsHide: true, encoding: "utf8", timeout: 30_000, maxBuffer: 2 * 1024 * 1024 });
}

function changeJson(root, path, change) {
  const full = inside(root, path), value = parseJsonSupported(readFileSync(full, "utf8"));
  change(value); writeFileSync(full, jsonBytes(value));
}
function manifest(root, change) { changeJson(root, family, change); }
function lock(root, change) {
  manifest(root, value => { changeJson(root, value.lock.path, change); value.lock.sha256 = hash(readFileSync(inside(root, value.lock.path))); });
}
function refreshGolden(root) {
  manifest(root, value => {
    value.expected.sha256 = hash(readFileSync(inside(root, value.expected.path)));
    changeJson(root, value.lock.path, evidence => { evidence.captureHistory[0].goldenSha256 = value.expected.sha256; });
    value.lock.sha256 = hash(readFileSync(inside(root, value.lock.path)));
  });
}
function golden(root, change) { changeJson(root, goldenPath, change); refreshGolden(root); }
function rawGolden(root, change) {
  const full = inside(root, goldenPath), before = readFileSync(full, "utf8"), after = change(before);
  assert.notEqual(after, before, "Raw mutation must change actual bytes"); writeFileSync(full, after); refreshGolden(root);
}
function append(root, path) { const full = inside(root, path); writeFileSync(full, Buffer.concat([readFileSync(full), Buffer.from("\n")])); }
const caseAt = (value, index = 0) => value.observations.cases[index];
const eventAt = (value, type, index = 0) => caseAt(value, index).events.find(row => row.event.json.type === type);

test("Agent progress raw numeric/null/missing and JS unsupported-value boundaries remain explicit", () => {
  assert.equal(compareRawJson('{"n":1.25}', '{"n":1.250}'), false);
  assert.equal(compareRawJson('{"n":0}', '{"n":-0}'), false);
  assert.equal(compareRawJson('{"details":null}', '{}'), false);
  assert.throws(() => parseJsonSupported('{"n":9007199254740993}'), /loses precision/);
  assert.throws(() => parseJsonSupported('{"n":-0}'), /Negative zero/);
  const authored = parseJsonSupported(readFileSync(join(repo, inputPath), "utf8"));
  const captured = parseJsonSupported(readFileSync(join(repo, goldenPath), "utf8"));
  assert.equal(authored.cases.length, 3); assert.equal(captured.observations.cases.length, 3);
  assert.deepEqual(captured.observations.cases.map(row => row.events.length), [25, 24, 18]);
  assert.deepEqual(captured.observations.cases.map(row => row.metrics.maxActiveFirstListeners), [2, 1, 2]);
  const wrapper = captured.observations.cases[1].events.find(row => row.event.json.type === "message_end" && row.event.json.message.toolCallId === "call-absent").event;
  assert.ok(!Object.hasOwn(wrapper.json.message, "details")); assert.ok(wrapper.ownUndefinedPaths.includes("/message/details"));
  assert.deepEqual(captured.observations.cases[0].finalState.json.pendingToolCalls, {});
  assert.deepEqual(captured.observations.cases[0].finalState.setValues, [{ path: "/pendingToolCalls", values: [] }]);
});

test("default checker registers one Agent progress group alongside every previous family without parity credit", () => isolated(root => {
  const result = check(root); assert.equal(result.status, 0, result.stderr || result.error?.message);
  const report = parseJsonSupported(result.stdout);
  assert.equal(report.fixtureManifestCount, 17); assert.equal(report.fixtureCount, 23);
  assert.equal(report.agentProgressOracleLockFilesReferenced, 1);
  assert.equal(report.evidenceKindCounts["captured-unchanged-whole-agent-progress-oracle"], 1);
  assert.deepEqual(report.fixtureManifests.filter(row => row.path === family), [{ path: family, fixtureGroups: 1 }]);
  for (const label of ["P1-06", "P1-07", "P1-08", "P3-01", "P3-03"]) assert.ok(report.nonInventoryFixtureLabels.includes(label));
  assert.equal(report.requirementCount, 46); assert.equal(report.mandatoryDeferred, 45);
  assert.equal(report.fullNativeParity, "blocked"); assert.equal(report.phase1Gate, "open");
}));

// Coordinated controls refresh only scratch-copy manifest/history hashes. These
// must still fail the explicit schema, cross-field facts or immutable raw pins.
const controls = [
  ["unknown standalone family", root => { mkdirSync(inside(root, "fixtures/pi-v0.99.1/unregistered-progress")); cpSync(inside(root, family), inside(root, "fixtures/pi-v0.99.1/unregistered-progress/manifest.json")); }, /Unregistered standalone fixture manifest/],
  ["manifest provenance kind", root => manifest(root, value => value.expected.kind = "captured-upstream-agent-oracle"), /Supported fixture provenance kind/],
  ["normalizer substitution", root => manifest(root, value => value.normalizerVersion = "canonical-json-v1"), /Fixture manifest normalizer/],
  ["unreviewed manifest field", root => manifest(root, value => value.acceptedNative = true), /Agent progress standalone manifest schema/],
  ["noninventory label substitution", root => manifest(root, value => value.requirementIds[0] = "unknown.progress"), /Fixture requirement/],
  ["duplicate fixture label", root => manifest(root, value => value.requirementIds.push(value.requirementIds[0])), /Fixture requirement IDs/],
  ["input bytes", root => append(root, inputPath), /Fixture input SHA256/],
  ["input size pin", root => manifest(root, value => value.input.bytes++), /Agent progress frozen scope/],
  ["coordinated authored input rewrite", root => { changeJson(root, inputPath, value => value.cases[0].tools[0].result.usage.cost.total = 1); manifest(root, value => { const bytes = readFileSync(inside(root, inputPath)); value.input.sha256 = hash(bytes); value.input.bytes = bytes.length; }); }, /Agent progress frozen scope/],
  ["captured expected bytes", root => append(root, goldenPath), /Fixture expected SHA256/],
  ["oracle lock bytes", root => append(root, lockPath), /Agent progress oracle lock.*SHA256/],
  ["reference plan bytes", root => append(root, "compatibility/agent-progress-reference.plan.json"), /Agent progress frozen reference plan.*SHA256/],
  ["historical setup plan bytes", root => append(root, "compatibility/reference-oracle-install-plan.json"), /Agent progress historical setup plan.*SHA256/],
  ["projection manifest bytes", root => append(root, "compatibility/reference-oracle-dependencies/package.json"), /Agent progress repository projection manifest.*SHA256/],
  ["projection lock bytes", root => append(root, "compatibility/reference-oracle-dependencies/package-lock.json"), /Agent progress repository projection lock.*SHA256/],
  ["capture helper bytes", root => append(root, "tools/PiReferenceRunner/capture-agent-progress.mjs"), /Agent progress repository harness.*SHA256/],
  ["runner bytes", root => append(root, "tools/PiReferenceRunner/run-agent-progress.mjs"), /Agent progress repository harness.*SHA256/],
  ["observer bytes", root => append(root, "tools/PiReferenceRunner/full-preload.mjs"), /SHA256/],
  ["offline guard bytes", root => append(root, "tools/PiReferenceRunner/offline-guard.mjs"), /SHA256/],
  ["raw comparator bytes", root => append(root, "tools/CompatibilityReport/raw-json.mjs"), /SHA256/],
  ["event-filtering provenance", root => manifest(root, value => value.provenance.emissionSnapshot = "Only selected matching events"), /Agent progress unchanged-source capture seams/],
  ["repeat-run overclaim", root => manifest(root, value => value.provenance.repeatRuns = 1), /Agent progress unchanged-source capture seams/],
  ["native differences omission", root => manifest(root, value => value.scope.nativeDifferencesOpen = []), /Agent progress frozen scope/],
  ["license closure overclaim", root => manifest(root, value => value.licenseStatus.fullP102Closure = true), /Agent progress frozen scope/],
  ["golden envelope kind", root => golden(root, value => value.kind = "authored-synthetic-contract"), /Agent progress captured expected envelope/],
  ["golden envelope extra field", root => golden(root, value => value.nativeParity = true), /Agent progress captured expected envelope/],
  ["lock schema substitution", root => lock(root, value => value.dependencyLock = "fabricated"), /Agent progress oracle lock\/environment schema/],
  ["environment missing field", root => lock(root, value => delete value.environmentPins.runtime), /Agent progress oracle lock\/environment schema/],
  ["runtime digest", root => lock(root, value => value.environmentPins.runtime.sha256 = "0".repeat(64)), /Agent progress exact runtime/],
  ["runtime executable", root => lock(root, value => value.environmentPins.runtime.absoluteExecutable = "C:/unapproved/node.exe"), /Agent progress exact runtime/],
  ["oracle root metadata", root => lock(root, value => value.environmentPins.approvedOracle += "-alternate"), /Agent progress exact runtime/],
  ["recorded installed license closure", root => lock(root, value => value.environmentPins.licenseStatus.fullP102Closure = true), /Agent progress exact runtime/],
  ["canonical source fingerprint", root => lock(root, value => value.environmentPins.sourceFingerprint.canonicalGit.sha256 = "0".repeat(64)), /Agent progress recorded 2091 raw/],
  ["acquired source fingerprint", root => lock(root, value => value.environmentPins.sourceFingerprint.acquiredCheckout.files--), /Agent progress recorded 2091 raw/],
  ["undeclared source conversion", root => lock(root, value => value.environmentPins.sourceFingerprint.declaredCheckoutConversions[0].path = "other.ps1"), /Agent progress recorded 2091 raw/],
  ["historical receipt digest", root => lock(root, value => value.environmentPins.setupReceipt.sha256 = "0".repeat(64)), /Agent progress recorded historical receipt/],
  ["historical receipt byte count", root => lock(root, value => value.environmentPins.setupReceipt.bytes++), /Agent progress recorded historical receipt/],
  ["projection digest", root => lock(root, value => value.environmentPins.projectionLockSha256 = "0".repeat(64)), /Agent progress projection\/setup dependency/],
  ["package version", root => lock(root, value => value.environmentPins.installedPackages[0].version = "0.1.8"), /Agent progress two-package archive/],
  ["archive SRI", root => lock(root, value => value.environmentPins.installedPackages[0].integrity = "sha512-fabricated"), /Agent progress two-package archive/],
  ["archive digest", root => lock(root, value => value.environmentPins.installedPackages[0].archive.sha256 = "0".repeat(64)), /Agent progress two-package archive/],
  ["installed tree fingerprint", root => lock(root, value => value.environmentPins.installedPackages[1].files.sha256 = "0".repeat(64)), /Agent progress two-package archive/],
  ["installed file census", root => lock(root, value => value.environmentPins.installedPackages[1].files.files--), /Agent progress two-package archive/],
  ["package manifest digest", root => lock(root, value => value.environmentPins.installedPackages[0].manifestSha256 = "0".repeat(64)), /Agent progress two-package archive/],
  ["unreviewed dependency declaration", root => lock(root, value => value.environmentPins.installedPackages[0].dependencyDeclarations.fake = "1.0.0"), /Agent progress two-package archive/],
  ["lifecycle declaration", root => lock(root, value => value.environmentPins.installedPackages[0].lifecycleScripts.install = "unreviewed"), /Agent progress two-package archive/],
  ["packaged license text", root => lock(root, value => value.environmentPins.installedPackages[0].licenses[0].utf8Text += "changed"), /Agent progress two-package archive/],
  ["coordinated license text pins", root => lock(root, value => { const notice = value.environmentPins.installedPackages[0].licenses[0]; notice.utf8Text += "changed"; notice.bytes = Buffer.byteLength(notice.utf8Text); notice.sha256 = hash(Buffer.from(notice.utf8Text)); }), /Agent progress two-package archive/],
  ["frozen capture helper pin", root => lock(root, value => value.harnessFiles[0].sha256 = "0".repeat(64)), /Agent progress frozen harness/],
  ["missing harness file", root => lock(root, value => value.harnessFiles.pop()), /Agent progress frozen harness/],
  ["duplicate loaded module", root => lock(root, value => value.loadedModules[0] = value.loadedModules[1]), /Agent progress admitted recorded/],
  ["missing loaded module", root => lock(root, value => value.loadedModules.pop()), /Agent progress admitted recorded/],
  ["unadmitted dependency root", root => lock(root, value => value.loadedModules[0].path = "node_modules/other/index.js"), /Agent progress admitted recorded/],
  ["relative loaded path escape", root => lock(root, value => value.loadedModules[0].path = "node_modules/typebox/../other.mjs"), /Agent progress admitted recorded/],
  ["loaded path percent escape", root => lock(root, value => value.loadedModules[0].path = "node_modules/typebox/%2e%2e/other.mjs"), /Agent progress admitted recorded/],
  ["extra loaded-module field", root => lock(root, value => value.loadedModules[0].accepted = true), /Agent progress admitted recorded/],
  ["loaded dependency byte identity", root => lock(root, value => value.loadedModules[0].sha256 = "0".repeat(64)), /Agent progress immutable recorded module/],
  ["source canonical digest", root => lock(root, value => value.sourceHashes[0].canonicalGitBlobSha256 = "0".repeat(64)), /Agent progress canonical whole-source/],
  ["source canonical byte length", root => lock(root, value => value.sourceHashes[0].canonicalGitBlobBytes++), /Agent progress canonical whole-source/],
  ["source/loaded coordinated identity", root => lock(root, value => { const source = value.sourceHashes.find(row => row.path.endsWith("/auth/helpers.ts")); const loaded = value.loadedModules.find(row => row.path === source.path); source.sha256 = source.canonicalGitBlobSha256 = loaded.sha256 = "0".repeat(64); }), /Agent progress immutable recorded module|Agent progress canonical whole-source/],
  ["source closure missing row", root => lock(root, value => value.sourceHashes.pop()), /Agent progress canonical whole-source/],
  ["capture history source change", root => lock(root, value => value.captureHistory[0].sourceBytesChanged = true), /Agent progress frozen capture history/],
  ["capture history golden cross-pin", root => lock(root, value => value.captureHistory[0].goldenSha256 = "0".repeat(64)), /Agent progress frozen capture history/],
  ["capture history clock rewrite", root => lock(root, value => value.captureHistory[0].childDateNowOverride.unixMilliseconds++), /Agent progress frozen capture history/],
  ["observation envelope field", root => golden(root, value => value.observations.selectedEvents = true), /Agent progress observation\/case envelope/],
  ["disclosed seam omitted", root => golden(root, value => delete value.observations.seams.jsonObservation), /Agent progress complete disclosed observation seams/],
  ["case omitted", root => golden(root, value => value.observations.cases.pop()), /Agent progress observation\/case envelope/],
  ["case order", root => golden(root, value => value.observations.cases.reverse()), /Agent progress ordered case identity/],
  ["mode substitution", root => golden(root, value => caseAt(value).mode = "serialized-native"), /Agent progress ordered case identity/],
  ["filtered source event", root => golden(root, value => caseAt(value).events.splice(14, 1)), /Agent progress emission entry index|Agent progress all retained source event/],
  ["event envelope field", root => golden(root, value => caseAt(value).events[0].acceptedNative = true), /Agent progress emission entry index/],
  ["event shape field", root => golden(root, value => caseAt(value).events[0].event.json.extra = true), /Agent progress complete event schema/],
  ["event index", root => golden(root, value => caseAt(value).events[0].index = 1), /Agent progress emission entry index/],
  ["own undefined null collapse", root => golden(root, value => { const observation = caseAt(value).promptReturn; observation.json = null; }), /Agent progress public settled|Agent progress own-undefined\/function omission/],
  ["duplicate own-undefined pointer", root => golden(root, value => caseAt(value).initialState.ownUndefinedPaths.push("/streamingMessage")), /Agent progress own-undefined\/function omission/],
  ["invented undefined pointer on present property", root => golden(root, value => caseAt(value).initialState.ownUndefinedPaths.push("/model")), /Agent progress own-undefined\/function omission/],
  ["function inventory removed", root => golden(root, value => caseAt(value).initialState.callablePaths = []), /Agent progress complete public state/],
  ["Set JSON replacement", root => golden(root, value => caseAt(value).initialState.json.pendingToolCalls = []), /Agent progress original Set serialization/],
  ["Set iteration duplicate", root => golden(root, value => caseAt(value).observations[0].state.setValues[0].values.push("call-alpha")), /Agent progress original Set serialization/],
  ["fake request duplicated", root => golden(root, value => caseAt(value).requests.push(caseAt(value).requests[0])), /Agent progress one authored fake-provider request/],
  ["credential introduced", root => golden(root, value => { caseAt(value).requests[0].options.json.apiKey = "introduced"; caseAt(value).requests[0].options.ownUndefinedPaths = caseAt(value).requests[0].options.ownUndefinedPaths.filter(path => path !== "/apiKey"); }), /Agent progress absent credential/],
  ["provider frame discarded", root => golden(root, value => caseAt(value).providerFrames.splice(1, 1)), /Agent progress retained authored provider frame order/],
  ["raw provider arguments", root => golden(root, value => caseAt(value).providerFrames[2].json.delta = '{"value":2}'), /Agent progress raw provider argument/],
  ["listener trace omitted", root => golden(root, value => caseAt(value).listenerTrace.pop()), /Agent progress complete two-listener/],
  ["listener event identity", root => golden(root, value => caseAt(value).listenerTrace[0].type = "agent_end"), /Agent progress listener trace schema/],
  ["listener overlap erased", root => golden(root, value => { const row = caseAt(value).listenerTrace.find(row => row.activeListeners === 2); row.activeListeners = 1; }), /Agent progress listener overlap accounting/],
  ["void callback return changed", root => golden(root, value => { const row = caseAt(value).toolTrace.find(row => row.kind === "update_callback_return"); row.returned = { json: null, ownUndefinedPaths: [], callablePaths: [], setValues: [] }; }), /Agent progress actual void callback return/],
  ["tool argument rewrite", root => golden(root, value => caseAt(value).toolTrace[0].args.json.value++), /Agent progress execute raw argument identity/],
  ["full tool return opaque value", root => golden(root, value => caseAt(value).toolTrace.find(row => row.kind === "execute_return").result.json.opaqueResult.numeric++), /Agent progress complete authored raw tool return/],
  ["partial update null removal", root => golden(root, value => delete caseAt(value).toolTrace.find(row => row.kind === "invoke_update").partial.json.details.retainedNull), /Agent progress accepted callback\/partial/],
  ["hook context filtered", root => golden(root, value => delete caseAt(value).hooks[0].context.json.context), /Agent progress own-undefined\/function omission|Agent progress complete hook context/],
  ["hook callable inventory filtered", root => golden(root, value => caseAt(value).hooks[0].context.callablePaths = []), /Agent progress unfiltered hook tool functions/],
  ["hook null return collapsed", root => golden(root, value => delete caseAt(value).hooks[3].returned.json.details), /Agent progress raw hook call/],
  ["hook result replacement rewrite", root => golden(root, value => eventAt(value, "tool_execution_end").event.json.result.structuredContent = null), /Agent progress observed raw hook\/result replacement/],
  ["stored structured result invented", root => golden(root, value => { const row = caseAt(value).events.find(row => row.event.json.type === "message_end" && row.event.json.message.role === "toolResult"); row.event.json.message.structuredContent = {}; }), /Agent progress provider-to-event\/final-message/],
  ["late callback creates event", root => golden(root, value => caseAt(value).controls.find(row => row.kind === "late_callback_after_tool_end").eventCountAfter++), /Agent progress actual ignored late callback/],
  ["late callback rejected profile substituted", root => golden(root, value => caseAt(value).controls.find(row => row.kind === "late_callback_after_idle").returned = { json: false, ownUndefinedPaths: [], callablePaths: [], setValues: [] }), /Agent progress actual ignored late callback/],
  ["idle resolved before barrier release", root => golden(root, value => caseAt(value).observations[0].idleResolved = true), /Agent progress barrier\/idle/],
  ["probe accepted-update count", root => golden(root, value => caseAt(value).observations[0].acceptedUpdateCount--), /Agent progress barrier\/idle/],
  ["checkpoint tool order", root => golden(root, value => caseAt(value).observations[1].toolEndIds = []), /Agent progress checkpoint tool\/message order/],
  ["derived overlap metric", root => golden(root, value => caseAt(value).metrics.maxActiveFirstListeners = 1), /Agent progress derived callback\/event\/overlap/],
  ["abort propagation removed", root => golden(root, value => caseAt(value, 2).toolTrace.find(row => row.kind === "tool_signal_abort").signalAborted = false), /Agent progress actual abort propagation/],
  ["after-abort accepted callback signal removed", root => golden(root, value => { const rows = caseAt(value, 2).toolTrace.filter(row => row.kind === "invoke_update"); rows.at(-1).signalAborted = false; }), /Agent progress actual abort propagation/],
  ["opaque snapshot string rewrite", root => golden(root, value => caseAt(value).events[0].stateBeforeListener.json.tools[0].description = "changed"), /Agent progress immutable raw observation cases/],
  ["coordinated numeric lexeme spelling", root => rawGolden(root, raw => raw.replaceAll('"answer": 1.25', '"answer": 1.250')), /Agent progress complete authored raw tool return|Agent progress immutable raw observation cases/],
  ["wide raw numeric rewrite", root => rawGolden(root, raw => raw.replaceAll('"numeric": 7', '"numeric": 9007199254740993')), /Agent progress complete authored raw tool return|Agent progress immutable raw observation cases/],
  ["negative-zero raw rewrite", root => rawGolden(root, raw => raw.replace('"isStreaming": false', '"isStreaming": -0')), /Agent progress public settled/],
  ["duplicate JSON property", root => rawGolden(root, raw => raw.replace('"schemaVersion": 1,', '"schemaVersion": 1, "schemaVersion": 1,')), /Duplicate JSON property/],
  ["coordinated whitespace byte rewrite", root => rawGolden(root, raw => raw + "\n"), /Agent progress immutable captured golden bytes/]
];

for (const [name, mutate, error] of controls) test(`Agent progress integrity rejects ${name}`, () => isolated(root => {
  mutate(root); const result = check(root);
  assert.equal(result.status, 1, `Mutation unexpectedly accepted: ${name}\n${result.stdout}\n${result.stderr}`);
  assert.match(result.stderr, error);
}));
