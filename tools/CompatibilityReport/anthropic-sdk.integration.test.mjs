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
const family = "fixtures/pi-v0.99.1/anthropic-sdk/manifest.json";
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`);

function inside(root, path) {
  const full = resolve(root, path), within = relative(realpathSync(root), full);
  assert.ok(within && !isAbsolute(within) && within !== ".." && !within.startsWith(`..${sep}`), "Refusing operation outside owned temporary copy");
  return full;
}

function isolated(action) {
  const temporaryRoot = realpathSync(tmpdir()), scratch = mkdtempSync(join(temporaryRoot, "pisharp-anthropic-integrity-"));
  try {
    for (const path of ["compatibility", "fixtures/pi-v0.99.1", "tools/CompatibilityReport", "tools/PiReferenceRunner"])
      cpSync(join(repo, path), inside(scratch, path), { recursive: true });
    return action(scratch);
  } finally {
    const physical = realpathSync(scratch), within = relative(temporaryRoot, physical);
    assert.ok(within && !isAbsolute(within) && within !== ".." && !within.startsWith(`..${sep}`) && within.startsWith("pisharp-anthropic-integrity-"), "Refusing cleanup outside owned temporary root");
    rmSync(physical, { recursive: true, force: true });
  }
}

function check(root) {
  return spawnSync(process.execPath, [inside(root, "tools/CompatibilityReport/check.mjs")], { cwd: root, windowsHide: true, encoding: "utf8", timeout: 30_000, maxBuffer: 2 * 1024 * 1024 });
}

function changeJson(root, path, change) {
  const full = inside(root, path), value = parseJsonSupported(readFileSync(full, "utf8"));
  change(value); writeFileSync(full, jsonBytes(value));
}

function manifest(root, change) { changeJson(root, family, change); }

// All mutations and digest refreshes are confined to the guarded scratch copy.
// Coordinated controls reach schema/cross-field checks beyond ordinary byte pins.
function lock(root, change) {
  manifest(root, value => {
    changeJson(root, value.lock.path, change);
    value.lock.sha256 = hash(readFileSync(inside(root, value.lock.path)));
  });
}

function golden(root, change) {
  manifest(root, value => {
    changeJson(root, value.expected.path, change);
    value.expected.sha256 = hash(readFileSync(inside(root, value.expected.path)));
    changeJson(root, value.lock.path, evidence => { evidence.captureHistory[0].goldenSha256 = value.expected.sha256; });
    value.lock.sha256 = hash(readFileSync(inside(root, value.lock.path)));
  });
}

function request(root, caseIndex, change) {
  golden(root, value => {
    const observed = value.observations.cases[caseIndex], captured = observed.fetchRequests[0];
    const body = parseJsonSupported(captured.rawBody); change(body);
    captured.rawBody = JSON.stringify(body);
    captured.rawBodyUtf8Bytes = Buffer.byteLength(captured.rawBody);
    captured.rawBodyUtf8Sha256 = hash(Buffer.from(captured.rawBody));
    const prior = observed.payloadSnapshots[0].params;
    observed.payloadSnapshots[0].params = { ...body, ...(Object.hasOwn(prior, "betas") ? { betas: prior.betas } : {}) };
  });
}

function append(root, path) { const full = inside(root, path); writeFileSync(full, Buffer.concat([readFileSync(full), Buffer.from("\n")])); }

test("Anthropic numeric seam preserves raw output lexemes and explicit authored rounding", () => {
  assert.notEqual(canonicalRawJson('{"n":1e+21}'), canonicalRawJson('{"n":1e21}'));
  assert.equal(compareRawJson('{"n":-0}', '{"n":0}'), false);
  assert.throws(() => parseJsonSupported('{"n":9007199254740993}'), /loses precision/);
  assert.throws(() => parseJsonSupported('{"n":-0}'), /Negative zero/);
  const input = parseJsonSupported(readFileSync(join(repo, "tools/PiReferenceRunner/anthropic-sdk-inputs.json"), "utf8"));
  const captured = parseJsonSupported(readFileSync(join(repo, "fixtures/pi-v0.99.1/anthropic-sdk/core.expected.json"), "utf8"));
  const fields = input.cases[1].numberConversions.fields, records = captured.observations.cases[1].numberConversions;
  assert.equal(fields.length, 8); assert.equal(records.length, fields.length);
  for (let index = 0; index < fields.length; index++) {
    const number = Number(fields[index].lexeme), record = records[index];
    assert.equal(record.authoredLexeme, fields[index].lexeme);
    assert.equal(record.jsonStringifyNumber, JSON.stringify(number));
    assert.equal(record.negativeZero, Object.is(number, -0));
  }
  assert.equal(records.find(row => row.field === "roundedInteger").authoredLexeme, "9007199254740993");
  assert.equal(records.find(row => row.field === "roundedInteger").jsonStringifyNumber, "9007199254740992");
});

test("default checker registers Anthropic alongside every existing family without parity credit", () => isolated(root => {
  const result = check(root); assert.equal(result.status, 0, result.stderr || result.error?.message);
  const report = parseJsonSupported(result.stdout);
  assert.equal(report.fixtureManifestCount, 17); assert.equal(report.fixtureCount, 23);
  assert.equal(report.anthropicOracleLockFilesReferenced, 1);
  assert.equal(report.evidenceKindCounts["captured-unchanged-anthropic-wrapper-sdk-oracle"], 1);
  assert.equal(report.fixtureManifests.filter(row => row.path === family).length, 1);
  assert.equal(report.requirementCount, 46); assert.equal(report.mandatoryDeferred, 45);
  assert.equal(report.fullNativeParity, "blocked"); assert.equal(report.phase1Gate, "open");
}));

const controls = [
  ["unknown standalone family", root => {
    mkdirSync(inside(root, "fixtures/pi-v0.99.1/unregistered-anthropic-copy"));
    cpSync(inside(root, family), inside(root, "fixtures/pi-v0.99.1/unregistered-anthropic-copy/manifest.json"));
  }, /Unregistered standalone fixture manifest/],
  ["manifest kind", root => manifest(root, value => value.expected.kind = "captured-upstream-responses-oracle"), /Supported fixture provenance kind/],
  ["normalizer substitution", root => manifest(root, value => value.normalizerVersion = "canonical-json-v1"), /Fixture manifest normalizer/],
  ["unreviewed manifest field", root => manifest(root, value => value.behavioralAcceptance = true), /Anthropic standalone manifest schema/],
  ["fixture input identity", root => manifest(root, value => value.input.sha256 = "0".repeat(64)), /Fixture input SHA256/],
  ["captured expected bytes", root => append(root, "fixtures/pi-v0.99.1/anthropic-sdk/core.expected.json"), /Fixture expected SHA256/],
  ["oracle lock bytes", root => append(root, "fixtures/pi-v0.99.1/anthropic-sdk/oracle.lock.json"), /Anthropic oracle lock.*SHA256/],
  ["reference plan bytes", root => append(root, "compatibility/anthropic-sdk-reference.plan.json"), /Anthropic frozen reference plan.*SHA256/],
  ["setup plan bytes", root => append(root, "tools/PiReferenceRunner/anthropic-sdk-install-plan.json"), /Anthropic canonical setup plan.*SHA256/],
  ["historical restored receipt bytes", root => append(root, "compatibility/anthropic-oracle-setup.json"), /Anthropic historical public setup receipt.*SHA256/],
  ["capture helper bytes", root => append(root, "tools/PiReferenceRunner/capture-anthropic-sdk.mjs"), /Anthropic repository harness.*SHA256/],
  ["setup helper bytes", root => append(root, "tools/PiReferenceRunner/setup-anthropic-sdk-oracle.mjs"), /Anthropic repository harness.*SHA256/],
  ["fabricated capture seam", root => manifest(root, value => value.provenance.sseDecoder = "SDK streaming decoder qualified"), /Anthropic recorded capture seams/],
  ["golden envelope kind", root => golden(root, value => value.kind = "authored-synthetic-contract"), /Anthropic captured expected envelope/],
  ["golden envelope extra field", root => golden(root, value => value.acceptedNative = true), /Anthropic captured expected envelope/],
  ["lock schema substitution", root => lock(root, value => value.dependencyLock = "fabricated"), /Anthropic oracle lock\/environment schema/],
  ["runtime digest", root => lock(root, value => value.environmentPins.runtime.sha256 = "0".repeat(64)), /Anthropic exact recorded runtime/],
  ["recorded Node component version", root => lock(root, value => value.environmentPins.nodeVersions.openssl = "different"), /Anthropic exact recorded runtime/],
  ["recorded OS header identity", root => lock(root, value => value.environmentPins.os.release = "different"), /Anthropic exact recorded runtime/],
  ["canonical full-source fingerprint", root => lock(root, value => value.environmentPins.sourceFingerprint.canonicalGit.sha256 = "0".repeat(64)), /Anthropic recorded 2091 raw/],
  ["checkout conversion attribution", root => lock(root, value => value.environmentPins.sourceFingerprint.declaredCheckoutConversions.pop()), /Anthropic recorded 2091 raw/],
  ["historical helper receipt digest", root => lock(root, value => value.environmentPins.setupReceipt.sha256 = "0".repeat(64)), /Anthropic historical receipt\/helper/],
  ["alternate recorded oracle root", root => lock(root, value => value.environmentPins.approvedOracle += "-alternate"), /Anthropic historical receipt\/helper/],
  ["projected manifest digest", root => lock(root, value => value.environmentPins.projectionManifestSha256 = "0".repeat(64)), /Anthropic projected manifest\/lock/],
  ["projected lock digest", root => lock(root, value => value.environmentPins.projectionLockSha256 = "0".repeat(64)), /Anthropic projected manifest\/lock/],
  ["missing harness", root => lock(root, value => value.harnessFiles.pop()), /Anthropic frozen harness/],
  ["duplicated harness identity", root => lock(root, value => value.harnessFiles[1] = value.harnessFiles[0]), /Anthropic frozen harness/],
  ["archive SRI", root => lock(root, value => value.environmentPins.installedPackages[0].integrity = "sha512-fabricated"), /Anthropic eight-package/],
  ["archive byte digest", root => lock(root, value => value.environmentPins.installedPackages[0].archiveSha256 = "0".repeat(64)), /Anthropic eight-package/],
  ["dependency version", root => lock(root, value => value.environmentPins.installedPackages[0].version = "0.52.0"), /Anthropic eight-package/],
  ["installed tree count", root => lock(root, value => value.environmentPins.installedPackages[0].files.files++), /Anthropic eight-package/],
  ["license text provenance", root => lock(root, value => value.environmentPins.installedPackages[0].licenses[0].utf8Text += "changed"), /Anthropic eight-package/],
  ["license redistribution closure", root => lock(root, value => value.environmentPins.licenseStatus.redistributionLicenseClosure = true), /Anthropic declared-MIT-only/],
  ["manifest license HOLD", root => manifest(root, value => value.licenseStatus.licenseReviewStatus = "Approved"), /Anthropic declared-MIT-only/],
  ["standardwebhooks fabricated packaged grant", root => lock(root, value => value.environmentPins.installedPackages.find(row => row.name === "standardwebhooks").packagedRootLicenseVerified = true), /Anthropic eight-package/],
  ["loaded module count", root => lock(root, value => value.loadedModules.pop()), /Anthropic admitted recorded/],
  ["duplicate loaded identity", root => lock(root, value => value.loadedModules[1] = value.loadedModules[0]), /Anthropic admitted recorded/],
  ["loaded external package", root => lock(root, value => value.loadedModules[0].path = "node_modules/openai/unreviewed.mjs"), /Anthropic admitted recorded/],
  ["loaded path traversal", root => lock(root, value => value.loadedModules[0].path = "node_modules/@anthropic-ai/sdk/../escape.mjs"), /Anthropic admitted recorded/],
  ["canonical executed-source digest", root => lock(root, value => value.sourceHashes[0].canonicalGitBlobSha256 = "0".repeat(64)), /Anthropic recorded canonical whole-source/],
  ["source closure missing wrapper", root => lock(root, value => {
    const source = value.sourceHashes.find(row => row.path.endsWith("/anthropic-messages.ts")), loaded = value.loadedModules.find(row => row.path === source.path);
    source.path = loaded.path = "upstream/packages/ai/src/api/fabricated-anthropic.ts";
  }), /Anthropic recorded canonical whole-source/],
  ["capture history source replacement", root => lock(root, value => value.captureHistory[0].sourceAndSdkBytesChanged = true), /Anthropic frozen capture history/],
  ["capture history golden identity", root => lock(root, value => value.captureHistory[0].goldenSha256 = "0".repeat(64)), /Anthropic frozen capture history/],
  ["case order", root => golden(root, value => value.observations.cases.reverse()), /Anthropic ordered case identity/],
  ["case count", root => golden(root, value => value.observations.cases.pop()), /Anthropic observations\/case schema/],
  ["request count", root => golden(root, value => value.observations.cases[0].fetchRequests = []), /Anthropic recorded request\/event counts/],
  ["raw body byte length", root => golden(root, value => value.observations.cases[0].fetchRequests[0].rawBodyUtf8Bytes++), /Anthropic raw body bytes/],
  ["raw body digest", root => golden(root, value => value.observations.cases[0].fetchRequests[0].rawBodyUtf8Sha256 = "0".repeat(64)), /Anthropic raw body bytes/],
  ["coordinated zero budget rewrite", root => request(root, 1, body => body.max_tokens = 1), /Anthropic retained default\/zero\/negative/],
  ["coordinated negative budget rewrite", root => request(root, 2, body => body.max_tokens = 0), /Anthropic retained default\/zero\/negative/],
  ["rounded integer projection rewrite", root => request(root, 1, body => body.messages[1].content[0].input.roundedInteger = 9007199254740994), /Anthropic retained authored numeric seam/],
  ["opaque numeric-looking string rewrite", root => request(root, 1, body => body.messages[1].content[0].input.opaque = "1"), /Anthropic authored numeric call\/opaque\/null/],
  ["retained null omission", root => request(root, 1, body => delete body.messages[1].content[0].input.retainedNull), /Anthropic authored numeric call\/opaque\/null/],
  ["negative-zero seam observation", root => golden(root, value => value.observations.cases[1].numberConversions[1].negativeZero = false), /Anthropic retained authored numeric seam/],
  ["authored wide integer lexeme", root => golden(root, value => value.observations.cases[1].numberConversions[6].authoredLexeme = "9007199254740992"), /Anthropic retained authored numeric seam/],
  ["raw exponent lexeme normalization", root => golden(root, value => {
    const row = value.observations.cases[1].fetchRequests[0]; row.rawBody = row.rawBody.replace('"largeExponent":1e+21', '"largeExponent":1e21');
    row.rawBodyUtf8Bytes = Buffer.byteLength(row.rawBody); row.rawBodyUtf8Sha256 = hash(Buffer.from(row.rawBody));
  }), /Anthropic raw body bytes/],
  ["SDK raw header order", root => golden(root, value => value.observations.cases[0].fetchRequests[0].headers.reverse()), /Anthropic retained raw headers/],
  ["null-removed header reinstated", root => golden(root, value => value.observations.cases[2].fetchRequests[0].headers.push(["x-remove", "remove-with-null"])), /Anthropic retained raw headers/],
  ["authored model header override", root => golden(root, value => value.observations.cases[2].fetchRequests[0].headers.find(row => row[0] === "x-model")[1] = "model-owned"), /Anthropic retained raw headers/],
  ["request URL", root => golden(root, value => value.observations.cases[0].fetchRequests[0].url = "https://different.invalid/v1/messages"), /Anthropic recorded SDK request boundary/],
  ["response callback status", root => golden(root, value => value.observations.cases[0].responseHooks[0].response.status = 201), /Anthropic retained response callback/],
  ["wire provider text", root => golden(root, value => value.observations.cases[0].providerEvents[2].delta.text = "changed"), /Anthropic retained response callback/],
  ["own-undefined inventory", root => golden(root, value => value.observations.cases[0].emissionOwnUndefined[0].paths.push("/partial/unknown")), /Anthropic retained emission\/drain/],
  ["emission snapshot mutation hidden by drained result", root => golden(root, value => value.observations.cases[0].emissionSnapshots[2].partial.content[0].text = "changed"), /Anthropic retained separate emission\/drain/],
  ["coordinated indexed snapshot rewrite", root => golden(root, value => {
    value.observations.cases[0].emissionSnapshots[1].partial.content[0].index = 0; value.observations.cases[0].drainedFrames[1].partial.content[0].index = 0;
  }), /Anthropic retained indexed partial/],
  ["coordinated terminal usage rewrite", root => golden(root, value => {
    const row = value.observations.cases[0]; row.finalResult.usage.input = 12; row.emissionSnapshots.at(-1).message.usage.input = 12; row.drainedFrames.at(-1).message.usage.input = 12;
  }), /Anthropic retained terminal\/result/],
  ["raw wire hash", root => golden(root, value => value.observations.responseWire.utf8Sha256 = "0".repeat(64)), /Anthropic raw response wire/],
  ["derived fake-fetch count", root => golden(root, value => value.observations.checks.fakeFetchCalls = 0), /Anthropic derived counts/],
  ["SDK decoder qualification overclaim", root => golden(root, value => value.observations.checks.sdkStreamingDecoderQualified = true), /Anthropic derived counts/],
  ["Date.now disclosure omission", root => golden(root, value => delete value.observations.checks.childDateNowOverride), /Anthropic derived counts/]
];

for (const [name, mutate, error] of controls) test(`Anthropic integrity rejects ${name}`, () => isolated(root => {
  mutate(root); const result = check(root);
  assert.equal(result.status, 1, `Mutation unexpectedly accepted: ${name}\n${result.stdout}\n${result.stderr}`);
  assert.match(result.stderr, error);
}));
