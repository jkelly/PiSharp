import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { parseJsonSupported } from "./raw-json.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const scratchRoot = join(repo, "artifacts", "fixture-integrity-temp");
mkdirSync(scratchRoot, { recursive: true });
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");
const family = "fixtures/pi-v0.99.1/agent/manifest.json";
const finishFamily = "fixtures/pi-v0.99.1/finish-decisions/manifest.json";
const truncationFamily = "fixtures/pi-v0.99.1/tool-truncation/manifest.json";
const sdkFamily = "fixtures/pi-v0.99.1/responses-sdk/manifest.json";
const toolsSdkFamily = "fixtures/pi-v0.99.1/responses-tools-sdk/manifest.json";
const fileQueueFamily = "fixtures/pi-v0.99.1/file-mutation-queue/manifest.json";
const sessionFamily = "fixtures/pi-v0.99.1/session-context/manifest.json";
const rpcFamily = "fixtures/pi-v0.99.1/rpc-jsonl/manifest.json";
const editFamily = "fixtures/pi-v0.99.1/edit/manifest.json";

function inside(root, path) {
  const full = resolve(root, path);
  const within = relative(realpathSync(root), full);
  assert.ok(within && !isAbsolute(within) && within !== ".." && !within.startsWith(`..${sep}`), "Refusing operation outside task temporary copy");
  return full;
}

function isolated(action) {
  const scratch = mkdtempSync(join(scratchRoot, "check-"));
  try {
    for (const path of ["compatibility", "fixtures/pi-v0.99.1", "tools/CompatibilityReport", "tools/PiReferenceRunner"])
      cpSync(join(repo, path), inside(scratch, path), { recursive: true });
    return action(scratch);
  } finally {
    const physical = realpathSync(scratch);
    const within = relative(realpathSync(scratchRoot), physical);
    assert.ok(within && !isAbsolute(within) && within !== ".." && !within.startsWith(`..${sep}`), "Refusing cleanup outside verified task temporary root");
    rmSync(physical, { recursive: true, force: true });
  }
}

function check(scratch) {
  return spawnSync(process.execPath, [inside(scratch, "tools/CompatibilityReport/check.mjs")], {
    cwd: scratch, encoding: "utf8", windowsHide: true, timeout: 20_000, maxBuffer: 1024 * 1024
  });
}

function manifest(scratch, path, change) {
  const full = inside(scratch, path);
  const value = parseJsonSupported(readFileSync(full, "utf8"));
  change(value);
  writeFileSync(full, `${JSON.stringify(value, null, 2)}\n`);
}

function expected(scratch, manifestPath, change) {
  manifest(scratch, manifestPath, value => {
    const row = value.fixtures[0];
    const full = inside(scratch, row.expected.path);
    const raw = change(readFileSync(full, "utf8"));
    writeFileSync(full, raw);
    row.expected.sha256 = sha256(Buffer.from(raw));
  });
}

function truncationRecord(scratch, change) {
  manifest(scratch, truncationFamily, value => {
    const reference = value.fixtures[0].provenance.record;
    manifest(scratch, reference.path, change);
    reference.sha256 = sha256(readFileSync(inside(scratch, reference.path)));
  });
}

// Refresh every declared digest to exercise schema/cross-field checks independently
// of the simpler byte-tamper controls. All writes remain in the guarded test copy.
function truncationExpected(scratch, change) {
  manifest(scratch, truncationFamily, value => {
    const row = value.fixtures[0];
    const raw = change(readFileSync(inside(scratch, row.expected.path), "utf8"));
    const bytes = Buffer.from(raw), digest = sha256(bytes);
    for (const reference of [row.expected, ...row.rawCaptures]) {
      writeFileSync(inside(scratch, reference.path), bytes);
      reference.sha256 = digest; reference.bytes = bytes.length;
    }
    manifest(scratch, row.provenance.record.path, record => {
      for (const reference of record.captures) { reference.sha256 = digest; reference.bytes = bytes.length; }
    });
    row.provenance.record.sha256 = sha256(readFileSync(inside(scratch, row.provenance.record.path)));
  });
}

function sdkLock(scratch, change, manifestPath = sdkFamily) {
  manifest(scratch, manifestPath, value => {
    manifest(scratch, value.lock.path, change);
    value.lock.sha256 = sha256(readFileSync(inside(scratch, value.lock.path)));
  });
}

function sdkExpected(scratch, change, refreshHistory = false, manifestPath = sdkFamily) {
  manifest(scratch, manifestPath, value => {
    const raw = change(readFileSync(inside(scratch, value.expected.path), "utf8"));
    writeFileSync(inside(scratch, value.expected.path), raw);
    value.expected.sha256 = sha256(Buffer.from(raw));
    if (refreshHistory) {
      manifest(scratch, value.lock.path, lock => {
        for (const entry of lock.captureHistory) entry.goldenSha256 = value.expected.sha256;
      });
      value.lock.sha256 = sha256(readFileSync(inside(scratch, value.lock.path)));
    }
  });
}

function standaloneInput(scratch, manifestPath, change) {
  manifest(scratch, manifestPath, value => {
    manifest(scratch, value.input.path, change);
    value.input.sha256 = sha256(readFileSync(inside(scratch, value.input.path)));
  });
}

function standaloneExpected(scratch, manifestPath, change) {
  sdkExpected(scratch, raw => {
    const value = parseJsonSupported(raw); change(value);
    return `${JSON.stringify(value, null, 2)}\n`;
  }, manifestPath === toolsSdkFamily || manifestPath === sessionFamily || manifestPath === editFamily, manifestPath);
}

// Coordinate every stored request representation so controls reach declaration
// checks rather than failing only on the surrounding body/golden digests.
function toolsRequest(scratch, caseIndex, change) {
  standaloneExpected(scratch, toolsSdkFamily, value => {
    const observed = value.observations.cases[caseIndex], request = observed.fetchRequests[0];
    change(request.bodyJson);
    observed.payloadSnapshots[0].params = request.bodyJson;
    request.body = JSON.stringify(request.bodyJson);
    request.bodyUtf8Sha256 = sha256(Buffer.from(request.body));
  });
}

function sessionCopiedMessages(scratch, caseIndex, change) {
  standaloneExpected(scratch, sessionFamily, value => {
    const observed = value.observations.cases[caseIndex];
    for (const messages of [observed.projection.messages, observed.context.messages, observed.llmMessages, ...observed.projection.entries.map(entry => entry.messages)])
      for (const message of messages) change(message);
  });
}

// Coordinate golden, both retained observations, lock cross-pins and history to
// test RPC schema independently of the simpler immutable-byte tamper controls.
function rpcExpected(scratch, change) {
  manifest(scratch, rpcFamily, value => {
    const path = inside(scratch, value.expected.path), expected = parseJsonSupported(readFileSync(path, "utf8"));
    change(expected); writeFileSync(path, `${JSON.stringify(expected, null, 2)}\n`);
    value.expected.sha256 = sha256(readFileSync(path));
    for (const row of value.rawCaptures) {
      manifest(scratch, row.path, raw => raw.observations = expected.observations);
      const bytes = readFileSync(inside(scratch, row.path)); row.sha256 = sha256(bytes); row.bytes = bytes.length;
    }
    manifest(scratch, value.lock.path, lock => { lock.rawCaptures = value.rawCaptures; lock.captureHistory[0].goldenSha256 = value.expected.sha256; });
    value.lock.sha256 = sha256(readFileSync(inside(scratch, value.lock.path)));
  });
}

function rpcRaw(scratch, change) {
  manifest(scratch, rpcFamily, value => {
    for (const row of value.rawCaptures) {
      manifest(scratch, row.path, change);
      const bytes = readFileSync(inside(scratch, row.path)); row.sha256 = sha256(bytes); row.bytes = bytes.length;
    }
    manifest(scratch, value.lock.path, lock => lock.rawCaptures = value.rawCaptures);
    value.lock.sha256 = sha256(readFileSync(inside(scratch, value.lock.path)));
  });
}

function rpcSerializerBytes(result) {
  const bytes = Buffer.from(result.serialized, "utf8");
  result.utf8Hex = bytes.toString("hex"); result.bytes = bytes.length; result.utf8Sha256 = sha256(bytes);
}

function editSnapshotBytes(snapshot, utf8) {
  const bytes = Buffer.from(utf8, "utf8");
  snapshot.bytes = bytes.length; snapshot.sha256 = sha256(bytes); snapshot.base64 = bytes.toString("base64"); snapshot.utf8 = utf8;
}

test("recursive integrity covers fifteen manifests and twenty-one separately classified fixture groups", () => isolated(scratch => {
  const child = check(scratch);
  assert.equal(child.status, 0, child.stderr);
  const report = parseJsonSupported(child.stdout);
  assert.equal(report.fixtureManifestCount, 17);
  assert.equal(report.fixtureCount, 23);
  assert.equal(report.authoredFixtures, 4);
  assert.equal(report.upstreamQueueOracles, 3);
  assert.equal(report.dependencyLockFilesReferenced, 7);
  assert.equal(report.sdkOracleLockFilesReferenced, 2);
  assert.equal(report.fileQueueOracleLockFilesReferenced, 1);
  assert.equal(report.sessionOracleLockFilesReferenced, 1);
  assert.equal(report.rpcOracleLockFilesReferenced, 1);
  assert.equal(report.editOracleLockFilesReferenced, 1);
  assert.deepEqual(report.nonInventoryFixtureLabels, ["P1-05", "P1-06", "P1-07", "P1-08", "P3-01", "P3-03", "P3-05", "P3-06", "P4-03", "P4-04", "P4-06", "tools.declaration-replay", "tools.file-mutation-ordering"]);
  assert.equal(report.evidenceKindCounts["captured-upstream-responses-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-upstream-responses-replay-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-upstream-loop-continuation-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-upstream-finish-decisions-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-upstream-tool-truncation-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-unchanged-responses-wrapper-sdk-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-whole-file-mutation-queue-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-unchanged-responses-tools-wrapper-sdk-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-unchanged-session-context-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-whole-rpc-jsonl-oracle"], 1);
  assert.equal(report.evidenceKindCounts["captured-unchanged-edit-oracle"], 1);
  assert.equal(report.requirementCount, 46);
  assert.equal(report.mandatoryDeferred, 45);
  assert.equal(report.fullNativeParity, "blocked");
  assert.equal(report.phase1Gate, "open");
  assert.equal(report.verifiedSourceFiles, 0); // This mutation suite does not re-run source qualification.
  assert.equal(report.sourceArchiveVerified, false);
  assert.match(report.scope, /integrity only/);
}));

test("legacy root-only seven-group evidence remains compatible", () => isolated(scratch => {
  // Remove only nested manifests from the guarded temporary copy. New families
  // must not accidentally remain in this deliberately root-only control.
  const fixtureRoot = "fixtures/pi-v0.99.1";
  function removeNestedManifests(directory) {
    for (const entry of readdirSync(inside(scratch, directory), { withFileTypes: true })) {
      const path = `${directory}/${entry.name}`;
      if (entry.isDirectory()) removeNestedManifests(path);
      else if (entry.name === "manifest.json" && directory !== fixtureRoot)
        rmSync(inside(scratch, path), { force: true });
    }
  }
  removeNestedManifests(fixtureRoot);
  const child = check(scratch);
  assert.equal(child.status, 0, child.stderr);
  const report = parseJsonSupported(child.stdout);
  assert.equal(report.fixtureManifestCount, 1);
  assert.equal(report.fixtureCount, 7);
  assert.equal(report.authoredFixtures, 4);
  assert.equal(report.upstreamQueueOracles, 3);
}));

const mutations = [
  ["tampered expected bytes", scratch => {
    const value = parseJsonSupported(readFileSync(inside(scratch, family), "utf8"));
    const full = inside(scratch, value.fixtures[0].expected.path);
    writeFileSync(full, `${readFileSync(full, "utf8")} `);
  }, /Fixture expected SHA256/],
  ["wrong manifest expected checksum", scratch => manifest(scratch, family, value => value.fixtures[0].expected.sha256 = "0".repeat(64)), /Fixture expected SHA256/],
  ["escaping expected path", scratch => manifest(scratch, family, value => value.fixtures[0].expected.path = "../outside.json"), /Evidence path escapes repository/],
  ["escaping dependency lock path", scratch => manifest(scratch, family, value => value.fixtures[0].provenance.dependencyLock = "../outside-lock.json"), /Evidence path escapes repository/],
  ["fixture ID duplicated across families", scratch => manifest(scratch, family, value => value.fixtures[0].fixtureId = "text-authoritative-end"), /Unique fixture IDs across all manifests/],
  ["wrong family baseline", scratch => manifest(scratch, family, value => value.sourceSha = "0".repeat(40)), /Fixture manifest baseline/],
  ["wrong family normalizer", scratch => manifest(scratch, family, value => value.normalizerVersion = "unknown-normalizer"), /Fixture manifest normalizer/],
  ["unknown requirement ID", scratch => manifest(scratch, family, value => value.fixtures[0].requirementIds = ["missing.requirement"]), /Fixture requirement missing.requirement/],
  ["unsupported claimed capture kind", scratch => manifest(scratch, family, value => value.fixtures[0].provenance.kind = "captured-upstream-unqualified-oracle"), /Supported fixture provenance kind/],
  ["wrong expected identity with refreshed checksum", scratch => expected(scratch, family, raw => raw.replace('"fixtureId": "awaited-parallel"', '"fixtureId": "another-fixture"')), /Oracle expected identity/],
  ["invalid raw JSON with refreshed checksum", scratch => expected(scratch, family, raw => raw.replace(/^\s*\{/, '{"duplicateProbe":1,"duplicateProbe":2,')), /Duplicate JSON property/],
  ["authored numeric lexeme change with refreshed checksum", scratch => expected(scratch, "fixtures/pi-v0.99.1/manifest.json", raw => {
    const changed = raw.replace(/("timestamp"\s*:\s*)1700000000000/, (_, prefix) => `${prefix}1700000000000.0`);
    assert.notEqual(changed, raw, "Lexeme mutation must affect the fixture");
    return changed;
  }), /Authored expectation consistency/],
  ["finish-decision manifest input kind inconsistent with its authored input", scratch => manifest(scratch, finishFamily, value => value.fixtures[0].provenance.inputKind = "authored-synthetic-input"), /Capture provenance finish-decisions-core/],
  ["finish-decision lock capture kind", scratch => manifest(scratch, "fixtures/pi-v0.99.1/finish-decisions/reference.lock.json", value => value.captureKind = "captured-upstream-agent-oracle"), /Finish-decision lock identity/],
  ["finish-decision environment lock digest", scratch => manifest(scratch, "fixtures/pi-v0.99.1/finish-decisions/reference.lock.json", value => value.environmentLockSha256 = "0".repeat(64)), /Finish-decision environment lock.*SHA256/],
  ["changed finish-decision harness bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-finish-decisions.mjs");
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /Finish-decision harness.*SHA256/],
  ["tampered finish-decision retained raw bytes", scratch => {
    const path = inside(scratch, "fixtures/pi-v0.99.1/finish-decisions/capture-1.raw.json");
    writeFileSync(path, `${readFileSync(path, "utf8")} `);
  }, /Retained raw capture 1 finish-decisions-core SHA256/],
  ["finish-decision expected observations inconsistent with retained captures", scratch => expected(scratch, finishFamily, raw => raw.replace('"observations": {', '"observations": {"unexpectedProbe":true,')), /Finish-decision retained observations/],
  ["truncation manifest input kind", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.inputKind = "authored-synthetic-input"), /Truncation source-only provenance/],
  ["truncation claiming a dependency lock instead of its direct-source closure", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.dependencyLock = "tools/PiReferenceRunner/full-lock.json"), /Truncation source-only provenance/],
  ["truncation input schema with refreshed checksum", scratch => manifest(scratch, truncationFamily, value => {
    const row = value.fixtures[0];
    manifest(scratch, row.input.path, input => input.schemaVersion = 2);
    row.input.sha256 = sha256(readFileSync(inside(scratch, row.input.path)));
  }), /Truncation input schema/],
  ["truncation expected input digest with refreshed checksum", scratch => expected(scratch, truncationFamily, raw => raw.replace(/("inputSha256"\s*:\s*")[a-f0-9]{64}/, (_, prefix) => `${prefix}${"0".repeat(64)}`)), /Truncation expected schema\/input digest/],
  ["escaping truncation provenance record path", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.record.path = "../outside.json"), /Evidence path escapes repository/],
  ["wrong truncation provenance record digest", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.record.sha256 = "0".repeat(64)), /Truncation provenance record.*SHA256/],
  ["truncation provenance record schema with refreshed checksum", scratch => truncationRecord(scratch, value => value.schemaVersion = 2), /Truncation provenance record identity\/input/],
  ["truncation provenance input cross-hash with refreshed checksum", scratch => truncationRecord(scratch, value => value.input.sha256 = "0".repeat(64)), /Truncation provenance record identity\/input/],
  ["changed truncation runner bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-tool-truncation.mjs");
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /Truncation runner.*SHA256/],
  ["truncation runtime pin coordinated in manifest and provenance", scratch => {
    truncationRecord(scratch, value => value.runtime.sha256 = "0".repeat(64));
    manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.runtime.sha256 = "0".repeat(64));
  }, /Truncation exact runtime pin/],
  ["truncation runtime flags coordinated in manifest and provenance", scratch => {
    truncationRecord(scratch, value => value.runtime.flags = []);
    manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.runtime.flags = []);
  }, /Truncation exact runtime pin/],
  ["truncation source module pin coordinated in manifest and provenance", scratch => {
    truncationRecord(scratch, value => value.dependencyClosure.sourceModules[0].sha256 = "0".repeat(64));
    manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.sourceModules[0].sha256 = "0".repeat(64));
  }, /Truncation import-free source module pin/],
  ["truncation external dependency claim with refreshed provenance checksum", scratch => truncationRecord(scratch, value => value.dependencyClosure.externalPackages = ["typebox"]), /Truncation import-free source module pin/],
  ["truncation dirty-source claim with refreshed provenance checksum", scratch => truncationRecord(scratch, value => value.sourceChecks.after.status = " M truncate.ts"), /Truncation recorded clean source checks/],
  ["truncation promoting native parity", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].provenance.nativeDifferential = true), /Truncation capture scope/],
  ["truncation duplicate retained raw path", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].rawCaptures[1].path = value.fixtures[0].rawCaptures[0].path), /Two distinct retained raw captures/],
  ["escaping truncation retained raw path", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].rawCaptures[0].path = "../outside.json"), /Evidence path escapes repository/],
  ["truncation retained raw length declaration", scratch => manifest(scratch, truncationFamily, value => value.fixtures[0].rawCaptures[0].bytes++), /Retained raw capture 1 tool-truncation-core byte length/],
  ["truncation retained raw pair mismatch with refreshed checksum", scratch => manifest(scratch, truncationFamily, value => {
    const reference = value.fixtures[0].rawCaptures[1], path = inside(scratch, reference.path);
    const bytes = Buffer.from(`${readFileSync(path, "utf8")} `);
    writeFileSync(path, bytes); reference.sha256 = sha256(bytes); reference.bytes = bytes.length;
  }), /Retained raw captures byte-identical/],
  ["truncation provenance capture cross-hash with refreshed checksum", scratch => truncationRecord(scratch, value => value.captures[0].sha256 = "0".repeat(64)), /Truncation recorded capture cross-hashes/],
  ["truncation case identity with every observation checksum refreshed", scratch => truncationExpected(scratch, raw => raw.replace('"caseId": "empty-default"', '"caseId": "different-case"')), /Truncation ordered case identity/],
  ["truncation missing result field with every observation checksum refreshed", scratch => truncationExpected(scratch, raw => raw.replace(/\s*"outputBytes": 0,/, "")), /Truncation eleven-field result schema/],
  ["truncation result field type with every observation checksum refreshed", scratch => truncationExpected(scratch, raw => raw.replace('"truncated": false', '"truncated": "false"')), /Truncation eleven-field result schema/],
  ["truncation derived count with every observation checksum refreshed", scratch => truncationExpected(scratch, raw => raw.replace('"capturedCaseCount": 124', '"capturedCaseCount": 125')), /Truncation derived capture counts/],
  ["SDK standalone fixture ID", scratch => manifest(scratch, sdkFamily, value => value.fixtureId = "responses-sdk-other"), /Fixture manifest schema/],
  ["SDK weakening its distinct raw-retention normalizer", scratch => manifest(scratch, sdkFamily, value => value.normalizerVersion = "object-key-order-v1"), /Fixture manifest normalizer/],
  ["SDK expected claimed capture kind", scratch => manifest(scratch, sdkFamily, value => value.expected.kind = "captured-upstream-responses-oracle"), /Supported fixture provenance kind/],
  ["SDK lock checksum", scratch => manifest(scratch, sdkFamily, value => value.lock.sha256 = "0".repeat(64)), /SDK oracle lock.*SHA256/],
  ["escaping SDK lock path", scratch => manifest(scratch, sdkFamily, value => value.lock.path = "../outside-lock.json"), /SDK standalone manifest identity\/provenance/],
  ["SDK lock schema with refreshed checksum", scratch => sdkLock(scratch, value => value.schemaVersion = 2), /SDK oracle lock schema\/source identity/],
  ["SDK source identity with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.sourceSha = "0".repeat(40)), /SDK oracle lock schema\/source identity/],
  ["SDK runtime version with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.runtime.version = "v0.0.0"), /SDK runtime\/platform pins/],
  ["SDK wrong installed OpenAI version with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].version = "6.40.0"), /SDK three-package recorded archive\/tree pins/],
  ["SDK archive SRI with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].integrity = "sha512-unreviewed"), /SDK three-package recorded archive\/tree pins/],
  ["SDK installed file count with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].files.files--), /SDK three-package recorded archive\/tree pins/],
  ["SDK duplicate loaded module path with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules[1].path = value.loadedModules[0].path), /SDK admitted recorded loaded module paths\/counts/],
  ["SDK escaping loaded module path with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules[0].path = "node_modules/openai/../../outside.mjs"), /SDK admitted recorded loaded module paths\/counts/],
  ["SDK canonical loaded source cross-pin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.sourceHashes[0].canonicalGitBlobSha256 = "0".repeat(64)), /SDK recorded canonical source cross-pins/],
  ["changed SDK capture harness bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-responses-sdk.mjs");
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /SDK repository harness.*SHA256/],
  ["SDK expected envelope identity with refreshed checksum", scratch => sdkExpected(scratch, raw => raw.replace('"fixtureId": "responses-sdk-core"', '"fixtureId": "another-sdk-fixture"')), /SDK expected envelope schema/],
  ["SDK changed golden contrary to retained capture history", scratch => sdkExpected(scratch, raw => `${raw} `), /SDK frozen capture history/],
  ["SDK raw body with refreshed declared golden/history checksums", scratch => sdkExpected(scratch, raw => {
    const changed = raw.replace('"body": "{', '"body": " {');
    assert.notEqual(changed, raw, "Raw body mutation must affect the fixture");
    return changed;
  }, true), /SDK raw body digest\/retained JSON consistency/],
  ["SDK raw headers with refreshed declared golden/history checksums", scratch => sdkExpected(scratch, raw => raw.replace('"model-owned"', '"changed-model-header"'), true), /SDK retained raw request headers/],
  ["SDK zero-budget observation promoted to null with refreshed declared checksums", scratch => sdkExpected(scratch, raw => {
    const value = parseJsonSupported(raw), observed = value.observations.cases[1];
    observed.payloadSnapshots[0].params.max_output_tokens = null;
    const request = observed.fetchRequests[0];
    request.bodyJson.max_output_tokens = null; request.body = JSON.stringify(request.bodyJson);
    request.bodyUtf8Sha256 = sha256(Buffer.from(request.body));
    return `${JSON.stringify(value, null, 2)}\n`;
  }, true), /SDK retained zero\/negative\/positive budget observation/],
  ["SDK negative-budget floor changed with refreshed declared checksums", scratch => sdkExpected(scratch, raw => {
    const value = parseJsonSupported(raw), observed = value.observations.cases[2];
    observed.payloadSnapshots[0].params.max_output_tokens = -1;
    const request = observed.fetchRequests[0];
    request.bodyJson.max_output_tokens = -1; request.body = JSON.stringify(request.bodyJson);
    request.bodyUtf8Sha256 = sha256(Buffer.from(request.body));
    return `${JSON.stringify(value, null, 2)}\n`;
  }, true), /SDK retained zero\/negative\/positive budget observation/],
  ["SDK response wire digest with refreshed declared checksums", scratch => sdkExpected(scratch, raw => {
    const value = parseJsonSupported(raw); value.observations.responseWire.utf8Sha256 = "0".repeat(64);
    return `${JSON.stringify(value, null, 2)}\n`;
  }, true), /SDK recorded wire\/derived count integrity/],
  ["SDK rewritten historical receipt identity with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory.at(-1).historicalReceiptHelperSha256 = "0".repeat(64)), /SDK historical setup receipt pin/],
  ["SDK current validation helper relabeled as historical setup with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory.at(-1).setupHelperSha256 = value.captureHistory.at(-1).historicalReceiptHelperSha256), /SDK historical setup receipt pin/],
  ["SDK correction claiming rewritten restore receipts with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory.at(-1).setupReceiptsRewritten = true), /SDK historical setup receipt pin/],
  ["new fixture label admitted outside its exact manifest", scratch => manifest(scratch, family, value => value.fixtures[0].requirementIds = ["tools.file-mutation-ordering"]), /Fixture requirement tools.file-mutation-ordering/],
  ["file queue unknown requirement label", scratch => manifest(scratch, fileQueueFamily, value => value.requirementIds.push("tools.unqualified")), /Fixture requirement tools.unqualified/],
  ["file queue falsely claiming a full dependency lock", scratch => manifest(scratch, fileQueueFamily, value => value.provenance.dependencyLock = "tools/PiReferenceRunner/full-lock.json"), /File queue source-only manifest identity\/provenance/],
  ["file queue source modification disclosure", scratch => manifest(scratch, fileQueueFamily, value => value.provenance.sourceModified = true), /File queue source-only manifest identity\/provenance/],
  ["file queue oracle lock checksum", scratch => manifest(scratch, fileQueueFamily, value => value.lock.sha256 = "0".repeat(64)), /File queue oracle lock.*SHA256/],
  ["file queue extra external dependency with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.externalDependencies = ["typebox"], fileQueueFamily), /File queue exact source\/runtime\/empty-dependency pins/],
  ["file queue canonical source pin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.executedSource.canonicalGitBlobSha256 = "0".repeat(64), fileQueueFamily), /File queue exact source\/runtime\/empty-dependency pins/],
  ["file queue runtime version with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.runtime.version = "v0.0.0", fileQueueFamily), /File queue exact source\/runtime\/empty-dependency pins/],
  ["file queue duplicate loaded module with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules.push(value.loadedModules[0]), fileQueueFamily), /File queue single-module\/builtin closure/],
  ["file queue undeclared builtin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedBuiltins.push("node:child_process"), fileQueueFamily), /File queue single-module\/builtin closure/],
  ["changed file queue capture harness bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-file-mutation-queue.mjs");
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /File queue repository harness.*SHA256/],
  ["file queue authored labels mismatched to manifest with refreshed input checksum", scratch => standaloneInput(scratch, fileQueueFamily, value => value.requirementIds.reverse()), /File queue authored input identity/],
  ["file queue reordered case with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases.reverse()), /File queue ordered authored\/captured case identity/],
  ["file queue callback result changed with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases[0].probes[0].outcomes.first.value = "invented-first"), /File queue raw callback value\/error cross-fields/],
  ["file queue callback error changed with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases[0].probes[0].outcomes.second.error.message = "invented-error"), /File queue raw callback value\/error cross-fields/],
  ["file queue cleanup checkpoint changed with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases[0].probes[0].checkpoints[1].secondStarted = true), /File queue retained probe\/checkpoint\/trace schema/],
  ["file queue filesystem relation changed with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases[1].filesystemObservations.hardlinkRealpathsEqual = true), /File queue retained filesystem relation\/file digest/],
  ["file queue resolver error code changed with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases[3].outcomes.rejected.error.code = "ENOENT"), /File queue retained resolver rejection\/recovery fields/],
  ["file queue resolver raw error message changed with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.cases[3].outcomes.rejected.error.message += " normalized"), /File queue retained resolver rejection\/recovery fields/],
  ["file queue invented capture count with refreshed expected checksum", scratch => standaloneExpected(scratch, fileQueueFamily, value => value.observations.checks.probeCount++), /File queue derived counts\/source-only scope/],
  ["tools SDK substituted capture kind", scratch => manifest(scratch, toolsSdkFamily, value => value.expected.kind = "captured-unchanged-responses-wrapper-sdk-oracle"), /Supported fixture provenance kind/],
  ["tools SDK escaping authored wire path with refreshed input checksum", scratch => standaloneInput(scratch, toolsSdkFamily, value => value.wireFixture.path = "../outside.json"), /Tools SDK authored wire reference\/labels/],
  ["tools SDK earlier golden used as authored wire with refreshed input checksum", scratch => standaloneInput(scratch, toolsSdkFamily, value => value.wireFixture.path = "fixtures/pi-v0.99.1/responses-sdk/core.expected.json"), /Tools SDK authored wire reference\/labels/],
  ["tools SDK broadening tool-search scope with refreshed input checksum", scratch => standaloneInput(scratch, toolsSdkFamily, value => value.model.compat.supportsToolSearch = true), /Tools SDK bounded standard-tool compatibility profile/],
  ["tools SDK original-only capture history with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory[0].kind = "validation-only explicit-oracle preflight correction R1", toolsSdkFamily), /SDK frozen capture history/],
  ["tools SDK historical receipt digest mismatched to reused environment with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.setupReceiptSha256 = "0".repeat(64), toolsSdkFamily), /Tools SDK reused historical environment\/module pins/],
  ["tools SDK loaded package digest mismatched to reused environment with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules.find(row => row.path.startsWith("node_modules/openai/")).sha256 = "0".repeat(64), toolsSdkFamily), /Tools SDK reused historical environment\/module pins/],
  ["changed tools SDK capture harness bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-responses-tools-sdk.mjs");
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /SDK repository harness.*SHA256/],
  ["tools SDK legacy direct context tools with refreshed input checksum", scratch => standaloneInput(scratch, toolsSdkFamily, value => value.cases[0].context.tools = []), /Tools SDK bounded transcript\/budget profile/],
  ["tools SDK removal declaration changed with refreshed input checksum", scratch => standaloneInput(scratch, toolsSdkFamily, value => value.cases[1].context.messages[2].toolsRemoved[0].name = "write"), /Tools SDK authored removal declaration/],
  ["tools SDK reordered declarations with all request and golden checksums refreshed", scratch => toolsRequest(scratch, 2, body => body.tools.reverse()), /Tools SDK retained tool order\/strict presence/],
  ["tools SDK replacement definition changed with all request and golden checksums refreshed", scratch => toolsRequest(scratch, 3, body => body.tools[0].description = "Write original text."), /Tools SDK raw declaration\/schema preservation/],
  ["tools SDK opaque schema array order changed with all request and golden checksums refreshed", scratch => toolsRequest(scratch, 0, body => body.tools[0].parameters["x-fixture-opaque"].ordered.reverse()), /Tools SDK raw declaration\/schema preservation/],
  ["tools SDK strict false omitted for capable case with all checksums refreshed", scratch => toolsRequest(scratch, 0, body => delete body.tools[0].strict), /Tools SDK retained tool order\/strict presence/],
  ["tools SDK strict false added for incapable case with all checksums refreshed", scratch => toolsRequest(scratch, 4, body => body.tools[0].strict = false), /Tools SDK retained tool order\/strict presence/],
  ["tools SDK raw body altered with refreshed golden and history checksums", scratch => sdkExpected(scratch, raw => {
    const value = parseJsonSupported(raw); value.observations.cases[0].fetchRequests[0].body += " ";
    return `${JSON.stringify(value, null, 2)}\n`;
  }, true, toolsSdkFamily), /SDK raw body digest\/retained JSON consistency/],
  ["tools SDK raw header altered with refreshed golden and history checksums", scratch => standaloneExpected(scratch, toolsSdkFamily, value => value.observations.cases[0].fetchRequests[0].headers[0][1] = "changed-header"), /SDK retained raw request headers/],
  ["tools SDK false request count with refreshed golden and history checksums", scratch => standaloneExpected(scratch, toolsSdkFamily, value => value.observations.checks.fakeFetchCalls++), /SDK recorded wire\/derived count integrity/],
  ["session label admitted outside its exact manifest", scratch => manifest(scratch, family, value => value.fixtures[0].requirementIds = ["P4-03"]), /Fixture requirement P4-03/],
  ["session unknown label", scratch => manifest(scratch, sessionFamily, value => value.requirementIds.push("P4-99")), /Fixture requirement P4-99/],
  ["session broadening its raw-retention normalizer", scratch => manifest(scratch, sessionFamily, value => value.normalizerVersion = "object-key-order-v1"), /Fixture manifest normalizer/],
  ["session fake generic dependency-lock provenance", scratch => manifest(scratch, sessionFamily, value => value.provenance.dependencyLock = "tools/PiReferenceRunner/full-lock.json"), /Session standalone manifest identity\/provenance/],
  ["session input kind with refreshed authored checksum", scratch => standaloneInput(scratch, sessionFamily, value => value.kind = "authored-synthetic-input"), /Session authored input identity\/profile/],
  ["session expected envelope with refreshed golden and history checksums", scratch => standaloneExpected(scratch, sessionFamily, value => value.kind = "authored-synthetic-contract"), /Session expected envelope schema/],
  ["session oracle lock digest", scratch => manifest(scratch, sessionFamily, value => value.lock.sha256 = "0".repeat(64)), /Session oracle lock.*SHA256/],
  ["session oracle lock schema with refreshed checksum", scratch => sdkLock(scratch, value => value.schemaVersion = 2, sessionFamily), /Session oracle lock schema\/source identity/],
  ["session setup receipt repinned to changed metadata with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.setupReceiptSha256 = "0".repeat(64), sessionFamily), /Session historical setup\/helper receipt identity/],
  ["session historical pending receipt relabeled as current capture qualification", scratch => manifest(scratch, "compatibility/session-context-oracle-setup.json", value => value.loadedModuleClosureQualified = true), /Session historical setup receipt.*SHA256/],
  ["session historical receipt helper replaced by inspector ownership", scratch => manifest(scratch, "compatibility/session-context-oracle-setup.json", value => value.owner.helperSha256 = value.owner.archiveInspectorSha256), /Session historical setup receipt.*SHA256/],
  ["session canonical plan mutation", scratch => manifest(scratch, "compatibility/session-context-oracle-plan.json", value => value.source.canonicalFingerprint.files--), /Session canonical install plan.*SHA256/],
  ["session treating all2093 checkout files as raw Git matches with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.sourceFingerprint.declaredCheckoutConversions = [], sessionFamily), /Session 2091 raw plus two attribute conversion accounting/],
  ["session acquired checkout fingerprint with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.sourceFingerprint.acquiredCheckout.sha256 = "0".repeat(64), sessionFamily), /Session 2091 raw plus two attribute conversion accounting/],
  ["session runtime version with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.runtime.version = "v0.0.0", sessionFamily), /Session exact runtime\/platform pins/],
  ["session projected lock pin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.projectionLockSha256 = "0".repeat(64), sessionFamily), /Session projected manifest\/lock cross-hashes/],
  ["changed session setup helper bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/setup-session-context-oracle.mjs");
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /Session repository harness.*SHA256/],
  ["session incorrect cross-spawn version with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].version = "6.0.6", sessionFamily), /Session eight-package historical archive\/tree\/license cross-pins/],
  ["session archive identity with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].archiveSha256 = "0".repeat(64), sessionFamily), /Session eight-package historical archive\/tree\/license cross-pins/],
  ["session installed file count with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].files.files++, sessionFamily), /Session eight-package historical archive\/tree\/license cross-pins/],
  ["session license identity with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].licenses[0].sha256 = "0".repeat(64), sessionFamily), /Session eight-package historical archive\/tree\/license cross-pins/],
  ["session duplicate loaded module with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules[1].path = value.loadedModules[0].path, sessionFamily), /Session admitted recorded 33-source\/682-dependency module inventory/],
  ["session escaping loaded module with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules[0].path = "node_modules/cross-spawn/../../outside.mjs", sessionFamily), /Session admitted recorded 33-source\/682-dependency module inventory/],
  ["session canonical source pin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.sourceHashes[0].canonicalGitBlobSha256 = "0".repeat(64), sessionFamily), /Session recorded canonical loaded source cross-pins/],
  ["session golden changed contrary to frozen capture history", scratch => sdkExpected(scratch, raw => `${raw} `, false, sessionFamily), /Session frozen capture history/],
  ["session source-change claim with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory[0].sourceAndDependencyBytesChanged = true, sessionFamily), /Session frozen capture history/],
  ["session duplicate authored entry with refreshed input checksum", scratch => standaloneInput(scratch, sessionFamily, value => value.entries[1].id = value.entries[0].id), /Session unique ordered acyclic authored entry identity/],
  ["session forward/cyclic parent with refreshed input checksum", scratch => standaloneInput(scratch, sessionFamily, value => value.entries[0].parentId = "left-leaf"), /Session unique ordered acyclic authored entry identity/],
  ["session null leaf changed to a missing field with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => delete value.observations.cases[2].leafId), /Session ordered case\/explicit leaf identity/],
  ["session projected source opaque data changed with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[0].projection.entries[0].sourceEntry.message.opaque.ordered.reverse()), /Session raw retained source-entry identity/],
  ["session context detached from separate projection with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[0].context.messages[0].opaque.keep = "changed"), /Session separate projection\/context raw cross-fields/],
  ["session coordinated runtime signature change with all declared checksums refreshed", scratch => sessionCopiedMessages(scratch, 0, message => {
    if (message.role === "assistant") message.content[1].toolCallSignature.keep = "changed";
  }), /Session raw ordinary runtime message preservation/],
  ["session latest edit changed across all copies with declared checksums refreshed", scratch => sessionCopiedMessages(scratch, 3, message => {
    if (message.role === "user" && typeof message.content === "string") message.content = "Invented last replacement.";
  }), /Session raw latest-edit content and retained metadata/],
  ["session decimal token lexeme changed across projection/context copies with declared checksums refreshed", scratch => sdkExpected(scratch, raw => {
    const changed = raw.replace(/("role": "compactionSummary"[\s\S]*?"tokensBefore": )128/g, (_, prefix) => `${prefix}128.0`);
    assert.notEqual(changed, raw); return changed;
  }, true, sessionFamily), /Session compaction recorded raw checkpoint\/summary fields/],
  ["session coordinated selected settings change with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => {
    value.observations.cases[0].projection.model.modelId = "invented-model";
    value.observations.cases[0].context.model.modelId = "invented-model";
  }), /Session recorded settings\/source assistant cross-fields/],
  ["session right custom undefined field promoted to null across runtime copies", scratch => sessionCopiedMessages(scratch, 1, message => {
    if (message.role === "custom") message.details = null;
  }), /Session custom raw null\/missing fields/],
  ["session own-undefined receipt removed with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[1].ownUndefinedPaths.projection = []), /Session own-undefined paths distinct from null\/missing/],
  ["session duplicate own-undefined receipt with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[1].ownUndefinedPaths.context.push("/messages/4/details")), /Session own-undefined paths distinct from null\/missing/],
  ["session runtime future role reinterpreted as a LLM user with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[1].llmMessages.push({ role: "user", content: "Unknown role stays inert." })), /Session retained projection\/role profiles/],
  ["session LLM opaque signature changed with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[0].llmMessages[4].content[1].toolCallSignature.keep = "lost-null"), /Session raw separate LLM ordinary message preservation/],
  ["session custom display/details leaked into separate LLM view with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[0].llmMessages[6].details = null), /Session separate LLM custom conversion retained fields/],
  ["session stored compaction summary prefix altered with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[3].llmMessages[1].content[0].text = "Authored compacted history only."), /Session separate LLM recorded summary text\/metadata/],
  ["session recorded bash output altered only in LLM view with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[3].llmMessages[6].content[0].text = "executed a real command"), /Session stored bash data\/separate LLM exclusion profile/],
  ["session hidden stored bash record sent to LLM with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.cases[3].llmMessages.push({ role: "user", content: "Hidden recorded output" })), /Session retained projection\/role profiles/],
  ["session invented public projection count with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.checks.caseCount++), /Session derived recorded counts\/source-only observation scope/],
  ["session clock override scope claim with refreshed expected checksum", scratch => standaloneExpected(scratch, sessionFamily, value => value.observations.checks.noClockOrRngOverride = false), /Session derived recorded counts\/source-only observation scope/],
  ["unregistered standalone family without silently excluding its manifest", scratch => {
    const directory = inside(scratch, "fixtures/pi-v0.99.1/unregistered-source-family"); mkdirSync(directory);
    cpSync(inside(scratch, rpcFamily), join(directory, "manifest.json"));
  }, /Unregistered standalone fixture manifest fixtures\/pi-v0\.99\.1\/unregistered-source-family\/manifest\.json/],
  ["RPC broadening requirement scope to dispatcher commands", scratch => manifest(scratch, rpcFamily, value => value.requirementIds = ["rpc.commands"]), /RPC source-only manifest identity\/provenance\/input pin/],
  ["RPC canonical public module replaced by baseline-listed dispatcher", scratch => manifest(scratch, rpcFamily, value => value.provenance.source = "packages/coding-agent/src/modes/rpc/rpc-mode.ts"), /RPC source-only manifest identity\/provenance\/input pin/],
  ["RPC falsely claiming a full dependency lock", scratch => manifest(scratch, rpcFamily, value => value.provenance.dependencyLock = "tools/PiReferenceRunner/full-lock.json"), /RPC source-only manifest identity\/provenance\/input pin/],
  ["RPC authored input pin coordinated after mutation", scratch => standaloneInput(scratch, rpcFamily, value => value.readerCases[0].purpose = "Changed authored input"), /RPC source-only manifest identity\/provenance\/input pin/],
  ["RPC oracle lock digest", scratch => manifest(scratch, rpcFamily, value => value.lock.sha256 = "0".repeat(64)), /RPC oracle lock.*SHA256/],
  ["RPC oracle lock schema with refreshed checksum", scratch => sdkLock(scratch, value => value.schemaVersion = 2, rpcFamily), /RPC canonical public module\/empty-dependency lock schema/],
  ["RPC canonical module pin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.executedSource.canonicalGitBlobSha256 = "0".repeat(64), rpcFamily), /RPC canonical public module\/empty-dependency lock schema/],
  ["RPC external package claim with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.externalDependencies = ["openai"], rpcFamily), /RPC canonical public module\/empty-dependency lock schema/],
  ["RPC runtime flags with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.runtime.flags = [], rpcFamily), /RPC exact runtime\/path\/flags\/platform pins/],
  ["RPC alternate runtime path with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.runtime.path = "C:/another-node.exe", rpcFamily), /RPC exact runtime\/path\/flags\/platform pins/],
  ["RPC runtime version with refreshed lock checksum", scratch => sdkLock(scratch, value => value.environmentPins.runtime.version = "v0.0.0", rpcFamily), /RPC exact runtime\/path\/flags\/platform pins/],
  ["changed RPC capture harness bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-rpc-jsonl.mjs"); writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /RPC repository harness.*SHA256/],
  ["RPC duplicate loaded source module with refreshed lock checksum", scratch => sdkLock(scratch, value => value.loadedModules.push(value.loadedModules[0]), rpcFamily), /RPC frozen harness\/single-module\/builtin pins/],
  ["RPC undeclared source builtin with refreshed lock checksum", scratch => sdkLock(scratch, value => value.sourceRuntimeBuiltins.push("node:child_process"), rpcFamily), /RPC frozen harness\/single-module\/builtin pins/],
  ["RPC dirty post-capture source claim with refreshed lock checksum", scratch => sdkLock(scratch, value => value.sourceChecks.after.status = " M jsonl.ts", rpcFamily), /RPC recorded clean canonical source checks/],
  ["RPC retained raw path escaped with refreshed lock cross-pin", scratch => {
    manifest(scratch, rpcFamily, value => value.rawCaptures[0].path = "../outside.json");
    sdkLock(scratch, value => value.rawCaptures[0].path = "../outside.json", rpcFamily);
  }, /RPC retained raw path\/cross-pin inventory/],
  ["RPC duplicate retained raw paths with refreshed lock cross-pin", scratch => {
    manifest(scratch, rpcFamily, value => value.rawCaptures[1].path = value.rawCaptures[0].path);
    sdkLock(scratch, value => value.rawCaptures[1].path = value.rawCaptures[0].path, rpcFamily);
  }, /RPC retained raw path\/cross-pin inventory/],
  ["RPC tampered raw capture bytes", scratch => {
    const path = inside(scratch, "fixtures/pi-v0.99.1/rpc-jsonl/capture-1.raw.json"); writeFileSync(path, `${readFileSync(path, "utf8")} `);
  }, /Retained raw capture 1 rpc-jsonl-core SHA256/],
  ["RPC retained raw length with coordinated declared cross-pins", scratch => {
    manifest(scratch, rpcFamily, value => value.rawCaptures[0].bytes++);
    sdkLock(scratch, value => value.rawCaptures[0].bytes++, rpcFamily);
  }, /Retained raw capture 1 rpc-jsonl-core byte length/],
  ["RPC raw pair mismatch with coordinated declared checksums", scratch => manifest(scratch, rpcFamily, value => {
    const row = value.rawCaptures[1], path = inside(scratch, row.path), bytes = Buffer.from(`${readFileSync(path, "utf8")} `);
    writeFileSync(path, bytes); row.sha256 = sha256(bytes); row.bytes = bytes.length;
    manifest(scratch, value.lock.path, lock => lock.rawCaptures = value.rawCaptures);
    value.lock.sha256 = sha256(readFileSync(inside(scratch, value.lock.path)));
  }), /Retained raw captures byte-identical/],
  ["RPC retained raw module identity changed in both declared captures", scratch => rpcRaw(scratch, value => value.loadedModules[0].sha256 = "0".repeat(64)), /RPC retained raw observations\/module\/builtin cross-fields/],
  ["RPC retained raw schema extra JSON admission object", scratch => rpcRaw(scratch, value => value.parsedJson = {}), /RPC retained raw observations\/module\/builtin cross-fields/],
  ["RPC expected observations detached from retained raw pair", scratch => sdkExpected(scratch, raw => {
    const value = parseJsonSupported(raw); value.observations.checks.callbackCount++;
    return `${JSON.stringify(value, null, 2)}\n`;
  }, true, rpcFamily), /RPC retained raw observations\/module\/builtin cross-fields/],
  ["RPC historical golden digest with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory[0].goldenSha256 = "0".repeat(64), rpcFamily), /RPC frozen capture history/],
  ["RPC source-change claim with refreshed lock checksum", scratch => sdkLock(scratch, value => value.captureHistory[0].sourceAndDependencyBytesChanged = true, rpcFamily), /RPC frozen capture history/],
  ["RPC envelope changed with every declared capture/history checksum refreshed", scratch => rpcExpected(scratch, value => value.fixtureId = "different-rpc-family"), /RPC expected envelope schema/],
  ["RPC reader identity changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[0].caseId = "another-case"), /RPC ordered reader case\/input schema/],
  ["RPC all-splits probe removed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[2].probes.pop()), /RPC derived authored fragment probe count/],
  ["RPC probe identity changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[0].probes[0].probeId = "invented-probe"), /RPC ordered probe schema\/identity/],
  ["RPC authored buffer bytes changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[0].probes[0].authoredChunks[0].hex = "ff"), /RPC raw authored chunk\/fragment cross-fields/],
  ["RPC callback text altered without its UTF8 digest after all outer checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[7].probes[0].callbacks[0].line = "Removed Unicode separators"), /RPC callback raw string\/index\/UTF8 digest schema/],
  ["RPC callback object substituted for raw line string with outer checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[1].probes[0].callbacks[0].line = { parsed: true }), /RPC callback raw string\/index\/UTF8 digest schema/],
  ["RPC callback delivery phase changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[1].probes[0].callbacks[0].phase = "dispatcher"), /RPC callback chunk\/EOF delivery metadata/],
  ["RPC callback emitted after authored detach with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[22].probes[0].callbacks[0].chunkIndex = 1), /RPC callback chunk\/EOF delivery metadata/],
  ["RPC per-chunk count changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[1].probes[0].chunkSnapshots[0].callbackCount = 0), /RPC derived per-chunk callback snapshots/],
  ["RPC cleanup listener counts changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[0].probes[0].listenerObservations.afterCleanup.end = 1), /RPC recorded attach\/detach\/cleanup listener profile/],
  ["RPC second detach changed listener count with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.readerCases[22].probes[0].listenerObservations.detachSnapshots[0].afterSecondCall.data = 3), /RPC recorded attach\/detach\/cleanup listener profile/],
  ["RPC serializer value identity changed with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.serializerCases[0].caseId = "different-serializer"), /RPC serializer ordered identity\/raw authored value/],
  ["RPC serializer numeric lexeme coerced with all byte and capture checksums refreshed", scratch => rpcExpected(scratch, value => {
    const row = value.observations.serializerCases[2]; row.serialized = row.serialized.replace("1.25", "1.250"); rpcSerializerBytes(row);
  }), /RPC serializer ordered identity\/raw authored value/],
  ["RPC serializer extra LF with all byte and capture checksums refreshed", scratch => rpcExpected(scratch, value => {
    const row = value.observations.serializerCases[0]; row.serialized += "\n"; rpcSerializerBytes(row);
  }), /RPC serializer ordered identity\/raw authored value/],
  ["RPC serializer UTF8 hex changed with all outer capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.serializerCases[0].utf8Hex = "00"), /RPC serializer retained UTF8 bytes\/length\/digest/],
  ["RPC serializer byte length changed with all outer capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.serializerCases[0].bytes++), /RPC serializer retained UTF8 bytes\/length\/digest/],
  ["RPC invented callback total with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.checks.callbackCount++), /RPC derived counts\/source-only framing scope/],
  ["RPC framing observations promoted to JSON admission with all capture checksums refreshed", scratch => rpcExpected(scratch, value => value.observations.checks.jsonAdmissionInvoked = true), /RPC derived counts\/source-only framing scope/],
  ["edit requirement label admitted outside its exact manifest", scratch => manifest(scratch, family, value => value.fixtures[0].requirementIds = ["P1-05"]), /Fixture requirement P1-05/],
  ["edit unknown requirement label", scratch => manifest(scratch, editFamily, value => value.requirementIds.push("tools.edit.unqualified")), /Fixture requirement tools.edit.unqualified/],
  ["edit manifest silently relabeled as native tool parity", scratch => manifest(scratch, editFamily, value => value.scope = "Native edit acceptance and phase closure"), /Edit bounded source-observation provenance/],
  ["edit fabricated raw capture fields", scratch => manifest(scratch, editFamily, value => value.rawCaptures = []), /Edit standalone manifest identity\/schema\/input pin/],
  ["edit substituted generic full dependency lock provenance", scratch => manifest(scratch, editFamily, value => value.provenance.dependencyLock = value.lock.path), /Edit bounded source-observation provenance/],
  ["edit expected provenance promoted to an agent capture", scratch => manifest(scratch, editFamily, value => value.expected.kind = "captured-upstream-agent-oracle"), /Supported fixture provenance kind edit-core/],
  ["edit changed authored corpus with coordinated declared digest", scratch => standaloneInput(scratch, editFamily, value => value.cases[0].utf8 += "injected"), /Edit standalone manifest identity\/schema\/input pin/],
  ["edit changed lock bytes", scratch => {
    const path = inside(scratch, "fixtures/pi-v0.99.1/edit/oracle.lock.json"); writeFileSync(path, `${readFileSync(path, "utf8")} `);
  }, /Edit oracle lock.*SHA256/],
  ["edit unexpected lock field with refreshed checksum", scratch => sdkLock(scratch, value => value.dependencyLock = "invented", editFamily), /Edit oracle lock schema\/source identity/],
  ["edit unexpected environment field with refreshed checksum", scratch => sdkLock(scratch, value => value.environmentPins.sourceTree = "invented", editFamily), /Edit oracle lock schema\/source identity/],
  ["edit rewritten historical restore receipt pin", scratch => sdkLock(scratch, value => value.environmentPins.setupReceiptSha256 = "0".repeat(64), editFamily), /Edit historical setup\/helper receipt identity/],
  ["edit rewritten historical prepared receipt pin", scratch => sdkLock(scratch, value => value.environmentPins.preparedReceiptSha256 = "0".repeat(64), editFamily), /Edit historical setup\/helper receipt identity/],
  ["edit lost attribute conversion accounting", scratch => sdkLock(scratch, value => value.environmentPins.sourceFingerprint.declaredCheckoutConversions.pop(), editFamily), /Edit 2091 raw plus two attribute conversion accounting/],
  ["edit alternate runtime path", scratch => sdkLock(scratch, value => value.environmentPins.runtime.absoluteExecutable = "C:/another-node.exe", editFamily), /Edit exact runtime\/path\/platform pins/],
  ["edit changed npm runtime pin", scratch => sdkLock(scratch, value => value.environmentPins.npmVersion = "0.0.0", editFamily), /Edit exact runtime\/path\/platform pins/],
  ["edit projected lock hash detached from canonical plan", scratch => sdkLock(scratch, value => value.environmentPins.projectionLockSha256 = "0".repeat(64), editFamily), /Edit projected manifest\/lock cross-hashes/],
  ["edit harness inventory omitted whole capture", scratch => sdkLock(scratch, value => value.harnessFiles.shift(), editFamily), /Edit frozen harness\/helper\/preload inventory/],
  ["edit historical helper repinned to session helper", scratch => sdkLock(scratch, value => value.harnessFiles[3].sha256 = "285cb9e70726e19e38dc318a40137708ae65e3ae33e893ea4775e6f169168bdc", editFamily), /Edit frozen harness\/helper\/preload inventory/],
  ["changed edit capture harness bytes", scratch => {
    const path = inside(scratch, "tools/PiReferenceRunner/capture-edit.mjs"); writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
  }, /Edit repository harness.*SHA256/],
  ["edit package version detached from reviewed source lock", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].version = "0.0.0", editFamily), /Edit thirteen-package historical archive\/tree\/license cross-pins/],
  ["edit archive digest detached from historical receipt", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].archiveSha256 = "0".repeat(64), editFamily), /Edit thirteen-package historical archive\/tree\/license cross-pins/],
  ["edit archive SRI detached from canonical source plan", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].integrity = "sha512-invented", editFamily), /Edit thirteen-package historical archive\/tree\/license cross-pins/],
  ["edit installed file count promoted beyond inspected 1997", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].files.files++, editFamily), /Edit thirteen-package historical archive\/tree\/license cross-pins/],
  ["edit packaged license digest detached from actual receipt", scratch => sdkLock(scratch, value => value.environmentPins.dependencies[0].licenses[0].sha256 = "0".repeat(64), editFamily), /Edit thirteen-package historical archive\/tree\/license cross-pins/],
  ["edit duplicate loaded module path", scratch => sdkLock(scratch, value => value.loadedModules[0].path = value.loadedModules[1].path, editFamily), /Edit admitted recorded 64-source\/432-dependency module inventory/],
  ["edit escaped external loaded path", scratch => sdkLock(scratch, value => value.loadedModules[0].path = "node_modules/chalk/../outside.js", editFamily), /Edit admitted recorded 64-source\/432-dependency module inventory/],
  ["edit partial-json falsely counted as loaded", scratch => sdkLock(scratch, value => value.loadedModules[0].path = "node_modules/partial-json/not-observed.js", editFamily), /Edit admitted recorded 64-source\/432-dependency module inventory/],
  ["edit canonical source blob digest changed", scratch => sdkLock(scratch, value => value.sourceHashes[0].canonicalGitBlobSha256 = "0".repeat(64), editFamily), /Edit recorded canonical whole-module source cross-pins/],
  ["edit required whole public module replaced coherently", scratch => sdkLock(scratch, value => {
    for (const rows of [value.loadedModules, value.sourceHashes]) rows.find(row => row.path === "upstream/packages/coding-agent/src/core/tools/edit.ts").path = "upstream/extracted-edit.ts";
  }, editFamily), /Edit recorded canonical whole-module source cross-pins/],
  ["edit unanchored dependency module hash changed coherently", scratch => sdkLock(scratch, value => value.loadedModules[0].sha256 = "0".repeat(64), editFamily), /Edit immutable complete recorded module metadata/],
  ["edit unanchored source metadata changed coherently", scratch => sdkLock(scratch, value => {
    const row = value.sourceHashes.find(item => item.path === "upstream/packages/tui/src/components/box.ts"); assert.ok(row);
    row.sha256 = row.canonicalGitBlobSha256 = "0".repeat(64); value.loadedModules.find(item => item.path === row.path).sha256 = row.sha256;
  }, editFamily), /Edit immutable complete recorded module metadata/],
  ["edit golden history changed with refreshed checksum", scratch => sdkLock(scratch, value => value.captureHistory[0].goldenSha256 = "0".repeat(64), editFamily), /Edit frozen capture history/],
  ["edit source mutation falsely admitted in capture history", scratch => sdkLock(scratch, value => value.captureHistory[0].sourceAndDependencyBytesChanged = true, editFamily), /Edit frozen capture history/],
  ["edit envelope identity changed with golden/history checksums refreshed", scratch => standaloneExpected(scratch, editFamily, value => value.fixtureId = "another-edit"), /Edit expected envelope schema/],
  ["edit invented observations field", scratch => standaloneExpected(scratch, editFamily, value => value.observations.nativeAccepted = true), /Edit observations schema\/input case count/],
  ["edit reordered case identity", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases.reverse()), /Edit ordered case\/admitted path\/input schema/],
  ["edit source logical path replaced with an external path", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].path = "../external.txt"), /Edit ordered case\/admitted path\/input schema/],
  ["edit prepared replacement detached from authored original", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].preparation.prepared.edits[0].oldText = "introduced"), /Edit public preparation raw supplied\/after\/prepared cross-fields/],
  ["edit supplied-after string preparation incorrectly retained as string", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[7].preparation.suppliedAfter.edits = value.observations.cases[7].preparation.suppliedBefore.edits), /Edit public preparation raw supplied\/after\/prepared cross-fields/],
  ["edit own-undefined inventory confused with null", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].preparation.ownUndefinedPaths = null), /Edit public preparation raw supplied\/after\/prepared cross-fields/],
  ["edit fulfilled failed-preview promoted to rejection", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[1].preview = value.observations.cases[1].result), /Edit public outcome\/own-undefined schema/],
  ["edit failure stack silently normalized into serializable contract", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[1].result.error.ownProperties.stack = "normalized"), /Edit raw failure contract without stack/],
  ["edit failed preview error detached from helper", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[1].preview.value.error = "different failure"), /Edit rejected helper\/fulfilled failed-preview\/tool failure cross-fields/],
  ["edit empty-plan distinct tool rejection erased", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[5].result = value.observations.cases[5].helperObservations.matching), /Edit rejected helper\/fulfilled failed-preview\/tool failure cross-fields/],
  ["edit missing-file actual error message erased coherently", scratch => standaloneExpected(scratch, editFamily, value => {
    const row = value.observations.cases[11]; row.preview.value.error = row.result.error.message = "missing file";
  }), /Edit fulfilled failed-preview\/absent-file raw failure cross-fields/],
  ["edit file byte length changed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].filesystem.before.bytes++), /Edit byte snapshot base64\/length\/SHA256\/raw UTF8/],
  ["edit base64 bytes changed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].filesystem.before.base64 = "AA=="), /Edit byte snapshot base64\/length\/SHA256\/raw UTF8/],
  ["edit raw UTF8 decoded string changed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[7].filesystem.before.utf8 = "normalized Unicode"), /Edit byte snapshot base64\/length\/SHA256\/raw UTF8/],
  ["edit original bytes fabricated with every snapshot field coordinated", scratch => standaloneExpected(scratch, editFamily, value => editSnapshotBytes(value.observations.cases[0].filesystem.before, "fabricated original\n")), /Edit authored original\/recorded saved byte equality/],
  ["edit rejected write falsely records changed bytes", scratch => standaloneExpected(scratch, editFamily, value => editSnapshotBytes(value.observations.cases[1].filesystem.after, "changed despite rejection\n")), /Edit authored original\/recorded saved byte equality/],
  ["edit byte identity flag detached from actual recorded status", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].filesystem.byteIdentical = true), /Edit recorded filesystem equality\/shape/],
  ["edit missing-file absent bytes promoted to an existing file", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[11].filesystem.after = value.observations.cases[0].filesystem.after), /Edit missing-file null snapshots\/helper absence/],
  ["edit BOM stripped from public normalization", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[8].helperObservations.normalization.splitBom.bom = ""), /Edit retained BOM\/endings\/normalization profile/],
  ["edit detected original CRLF profile changed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[8].helperObservations.normalization.detectedLineEnding = "\n"), /Edit retained BOM\/endings\/normalization profile/],
  ["edit fuzzy normalization erases captured Unicode", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[7].helperObservations.normalization.fuzzyView = "normalized"), /Edit retained BOM\/endings\/normalization profile/],
  ["edit fuzzy-find full result index changed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[7].helperObservations.fuzzyFind.value.index++), /Edit recorded fuzzy-find full result profile/],
  ["edit empty-plan omitted fuzzy call invented", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[5].helperObservations.fuzzyFind = value.observations.cases[0].helperObservations.fuzzyFind), /Edit empty-plan absent first-text fuzzy call/],
  ["edit generated matching base detached from normalization", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].helperObservations.matching.value.baseContent = "new base"), /Edit successful matching\/generated schema/],
  ["edit returned first changed line detached from captured display", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].result.value.details.firstChangedLine++), /Edit complete preview\/display\/patch\/tool raw cross-fields/],
  ["edit preview display detached from generated display", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].preview.value.diff += "normalized"), /Edit complete preview\/display\/patch\/tool raw cross-fields/],
  ["edit raw unified patch changed only in returned tool result", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[8].result.value.details.patch += "normalized"), /Edit complete preview\/display\/patch\/tool raw cross-fields/],
  ["edit complete returned content changed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.cases[0].result.value.content[0].text = "Accepted"), /Edit complete preview\/display\/patch\/tool raw cross-fields/],
  ["edit successful output bytes fabricated with coordinated snapshot fields", scratch => standaloneExpected(scratch, editFamily, value => editSnapshotBytes(value.observations.cases[0].filesystem.after, "fabricated saved bytes\n")), /Edit successful helper\/actual saved bytes cross-fields/],
  ["edit numeric byte-count spelling coerced with golden/history checksums refreshed", scratch => sdkExpected(scratch, raw => {
    const changed = raw.replace(/("bytes"\s*:\s*)19/, (_, prefix) => `${prefix}19.0`); assert.notEqual(changed, raw); return changed;
  }, true, editFamily), /Edit byte snapshot schema/],
  ["edit derived case count invented", scratch => standaloneExpected(scratch, editFamily, value => value.observations.checks.caseCount++), /Edit derived counts\/bounded source-only observation scope/],
  ["edit renderer execution falsely claimed", scratch => standaloneExpected(scratch, editFamily, value => value.observations.checks.rendererOrNativeHelperInvoked = true), /Edit derived counts\/bounded source-only observation scope/]
];

for (const [name, mutate, error] of mutations)
  test(`integrity rejects ${name}`, () => isolated(scratch => {
    mutate(scratch);
    const child = check(scratch);
    assert.equal(child.status, 1, `${child.stdout}\n${child.stderr}`);
    assert.match(child.stderr, error);
  }));

test("raw corpus values retain wide numeric lexemes and escaped lone surrogates", () => isolated(scratch => {
  let preserved;
  expected(scratch, "fixtures/pi-v0.99.1/json-preview/manifest.json", raw => {
    preserved = raw.replace(/^\s*\{/, '{"wideNumberProbe":9007199254740993,"loneSurrogateProbe":"\\ud800",');
    return preserved;
  });
  const child = check(scratch);
  assert.equal(child.status, 0, child.stderr);
  assert.equal(readFileSync(inside(scratch, "fixtures/pi-v0.99.1/json-preview/core-preview.expected.json"), "utf8"), preserved);
  assert.equal(parseJsonSupported(child.stdout).fullNativeParity, "blocked");
}));

test("session integrity preserves raw opaque numbers and surrogates through every stored copy", () => isolated(scratch => {
  // Test-copy metadata is coordinated deliberately. This tests the raw integrity
  // adapter and does not claim the modified corpus was executed by the oracle.
  const addOpaque = raw => {
    const changed = raw.replace(/("opaque"\s*:\s*\{\s*"keep"\s*:\s*null,\s*)("ordered":)/g, (_, prefix, key) => `${prefix}"rawNumberProbe":9007199254740993,"rawSurrogateProbe":"\\ud800",${key}`);
    assert.notEqual(changed, raw); return changed;
  };
  let preserved;
  manifest(scratch, sessionFamily, value => {
    const path = inside(scratch, value.input.path), raw = addOpaque(readFileSync(path, "utf8"));
    writeFileSync(path, raw); value.input.sha256 = sha256(Buffer.from(raw));
  });
  sdkExpected(scratch, raw => { preserved = addOpaque(raw); return preserved; }, true, sessionFamily);
  const child = check(scratch);
  assert.equal(child.status, 0, child.stderr);
  assert.equal(readFileSync(inside(scratch, "fixtures/pi-v0.99.1/session-context/core.expected.json"), "utf8"), preserved);
  assert.equal(parseJsonSupported(child.stdout).requirementCount, 46);
  assert.equal(parseJsonSupported(child.stdout).fullNativeParity, "blocked");
}));

test("RPC integrity retains malformed, empty and raw numeric callback text without JSON admission", () => isolated(scratch => {
  const child = check(scratch); assert.equal(child.status, 0, child.stderr);
  const evidence = parseJsonSupported(readFileSync(inside(scratch, "fixtures/pi-v0.99.1/rpc-jsonl/core.expected.json"), "utf8"));
  const first = id => evidence.observations.readerCases.find(row => row.caseId === id).probes[0].callbacks.map(row => row.line);
  assert.deepEqual(first("empty-lines"), ["", "", ""]);
  assert.deepEqual(first("final-incomplete"), ['{"incomplete":']);
  assert.deepEqual(first("duplicate-keys"), ['{"a":1,"a":2}']);
  assert.match(first("raw-scalars-and-whitespace")[0], /9007199254740993.*1\.0.*-0/);
  assert.ok(first("unicode-every-split")[0].includes("\u2028") && first("unicode-every-split")[0].includes("\u2029"));
  assert.ok(first("invalid-utf8")[0].includes("\ufffd"));
  assert.equal(evidence.observations.checks.readerProbeCount, 85);
  assert.equal(evidence.observations.checks.callbackCount, 126);
  assert.equal(evidence.observations.checks.jsonAdmissionInvoked, false);
  const report = parseJsonSupported(child.stdout);
  assert.equal(report.requirementCount, 46); assert.equal(report.mandatoryDeferred, 45); assert.equal(report.fullNativeParity, "blocked");
}));

test("edit integrity retains genuine preparation, preview failures, Unicode/BOM bytes and complete public results", () => isolated(scratch => {
  const child = check(scratch); assert.equal(child.status, 0, child.stderr);
  const evidence = parseJsonSupported(readFileSync(inside(scratch, "fixtures/pi-v0.99.1/edit/core.expected.json"), "utf8")), cases = evidence.observations.cases;
  assert.equal(cases.filter(row => row.result.status === "fulfilled").length, 5);
  assert.equal(cases.filter(row => row.result.status === "rejected").length, 7);
  assert.ok(cases.every(row => row.preview.status === "fulfilled"));
  assert.equal(cases[1].filesystem.byteIdentical, true);
  assert.match(cases[1].result.error.message, /Could not find edits\[1\]/);
  assert.match(cases[2].result.error.message, /Found 2 occurrences/);
  assert.notEqual(cases[5].preview.value.error, cases[5].result.error.message);
  assert.equal(typeof cases[7].preparation.suppliedBefore.edits, "string");
  assert.ok(Array.isArray(cases[7].preparation.suppliedAfter.edits));
  assert.ok(cases[7].filesystem.after.utf8.startsWith('untouched: \u201ckeep\u201d   \nuntouched: "keep"\n'));
  assert.ok(cases[7].filesystem.after.utf8.includes("\ud83d\ude00"));
  assert.equal(cases[8].filesystem.after.utf8, "\ufefffirst\r\nSECOND\r\nTHIRD\r\nEND");
  assert.equal(cases[8].result.value.details.patch, cases[8].helperObservations.generated.patch.value);
  assert.ok(cases.every(row => row.preparation.ownUndefinedPaths.length === 0));
  assert.equal(cases[11].filesystem.before, null); assert.equal(cases[11].filesystem.after, null);
  const lock = parseJsonSupported(readFileSync(inside(scratch, "fixtures/pi-v0.99.1/edit/oracle.lock.json"), "utf8"));
  assert.equal(lock.loadedModules.length, 496); assert.equal(lock.sourceHashes.length, 64);
  assert.equal(lock.environmentPins.dependencies.reduce((sum, row) => sum + row.files.files, 0), 1997);
  assert.ok(!lock.loadedModules.some(row => row.path.startsWith("node_modules/partial-json/")));
  const receipt = parseJsonSupported(readFileSync(inside(scratch, "compatibility/edit-oracle-setup.json"), "utf8"));
  assert.equal(receipt.loadedModuleClosureQualified, false); assert.match(receipt.status, /qualification pending/);
  const report = parseJsonSupported(child.stdout);
  assert.equal(report.editOracleLockFilesReferenced, 1); assert.equal(report.requirementCount, 46); assert.equal(report.mandatoryDeferred, 45); assert.equal(report.fullNativeParity, "blocked");
}));
