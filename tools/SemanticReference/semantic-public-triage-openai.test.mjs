// Authored controls only. Root owns execution. No SDK/compiler/package runtime imports.
import test from 'node:test';
import assert from 'node:assert/strict';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { repo, readPlan, assertPlan, parseArgs, regular, hash, inspectOpenai, verifyBaseline, admissionRows, admissionRecord, context, confined, profileFiles } from './setup-semantic-public-triage-openai.mjs';

const plan = readPlan();
const copy = value => JSON.parse(JSON.stringify(value));
for (const pin of plan.dependencies) {
  const bytes = regular(join(repo, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256);
}
const { inspectArchive } = await import(pathToFileURL(join(repo, 'tools/SemanticInventory/node-declaration-archive.mjs')).href);
const { parseJsonSupported } = await import(pathToFileURL(join(repo, 'tools/CompatibilityReport/raw-json.mjs')).href);
const compressed = regular(join(plan.archiveSource.root, plan.archiveSource.archive.path));
const observed = inspectOpenai(compressed, plan, inspectArchive, parseJsonSupported);
const baseline = parseJsonSupported(regular(join(repo, plan.baseline.pins[0].path)).toString('utf8'));

test('one exact official package, 3548 members, 784 declarations and four original notices remain inert', () => {
  assertPlan(plan); assert.equal(observed.parsed.files.size, 3548); assert.equal(observed.evidence.archive.regularBytes, 18477473);
  assert.equal(observed.evidence.declarationFiles, 784); assert.equal(observed.evidence.notices.length, 4);
  assert.equal(observed.evidence.sdkRuntimeExecuted, false); assert.equal(observed.evidence.checkerConditionSelectionPending, true);
  assert.equal(observed.evidence.licensingClosure, false); assert.equal(observed.evidence.semanticPublicClosure, false);
});

for (const [label, mutate] of [
  ['another package version', p => { p.package.version = '7.19.1'; }],
  ['root placement loses canonical nested SDK ownership', p => { p.package.target = 'upstream/node_modules/openai'; }],
  ['another official lock URL', p => { p.package.lockEntry.resolved = 'https://example.invalid/openai.tgz'; }],
  ['another upstream SRI', p => { p.package.lockEntry.integrity = 'sha512-invalid'; }],
  ['partial SDK payload', p => { p.package.members.pop(); }],
  ['SDK declaration-only filtering', p => { p.package.members = p.package.members.filter(row => /\.d\.[cm]?ts$/.test(row.path)); }],
  ['case-colliding SDK member', p => { p.package.members[1].path = p.package.members[0].path.toUpperCase(); }],
  ['package traversal member', p => { p.package.members[0].path = '../outside'; }],
  ['missing vendored notice', p => { p.package.notices.pop(); }],
  ['optional peers treated as semantic exemptions', p => { p.package.optionalPeersAreNotSemanticExemptions = false; }],
  ['missing peer instance decision erased', p => { p.package.unresolvedPeerInstanceDecisionsRetained.pop(); }],
  ['predicted diagnostic reduction', p => { p.policy.diagnosticReductionPromised = true; }],
  ['SDK runtime execution', p => { p.policy.packageRuntimeImported = true; }],
  ['lifecycle execution', p => { p.policy.lifecycleScripts = true; }],
  ['config changes', p => { p.policy.sourceOrConfigChanges = true; }],
  ['network access', p => { p.policy.network = true; }],
  ['retry of uncertain admission', p => { p.policy.uncertainRetryAllowed = true; }],
  ['permanent mandatory lock scope reduction', p => { p.baseline.mandatoryLockInstances = 26; }],
  ['published ownership blockers omitted', p => { p.baseline.publishedOwnershipBlockers = 0; }],
  ['census misrepresented as public closure', p => { p.semanticPublicClosure = true; }],
  ['phase closure without evidence', p => { p.phaseGatesPassed = ['G0']; }],
]) test('plan rejects ' + label, () => { const altered = copy(plan); mutate(altered); assert.throws(() => assertPlan(altered)); });

test('default is read-only check of the exact fresh successor', () => {
  assert.deepEqual(parseArgs([], plan), { mode: '--check', root: resolve(plan.oracle) });
  assert.equal(parseArgs(['--prepare'], plan).mode, '--prepare'); assert.equal(parseArgs(['--admit'], plan).mode, '--admit');
});
for (const args of [
  ['--check', '--check'], ['--check', '--prepare'], ['--prepare', '--admit'], ['--oracle'], ['--oracle', '--check'],
  ['--unknown'], ['--oracle', '.'], ['--oracle', plan.origin.root], ['--oracle', plan.archiveSource.root], ['--oracle', repo],
]) test('argument denial ' + JSON.stringify(args), () => assert.throws(() => parseArgs(args, plan)));
test('successor overlap with any immutable root is denied even in a modified plan', () => {
  for (const root of [repo, plan.origin.root, plan.archiveSource.root]) { const altered = copy(plan); altered.oracle = root; assert.throws(() => parseArgs([], altered)); }
});
test('write path confinement rejects traversal and the root itself', () => {
  assert.throws(() => confined(plan.oracle, join(plan.oracle, '..', 'outside'))); assert.throws(() => confined(plan.oracle, plan.oracle));
  assert.equal(confined(plan.oracle, join(plan.oracle, 'upstream', 'safe')), resolve(plan.oracle, 'upstream', 'safe'));
});

test('the exact archive root is package and all compressed bytes authenticate before member parsing', () => {
  assert.equal(observed.parsed.publisherRoot, 'package');
  let invoked = false; const altered = Buffer.from(compressed); altered[altered.length - 1] ^= 1;
  assert.throws(() => inspectOpenai(altered, plan, () => { invoked = true; throw new Error('parser should not execute'); }, parseJsonSupported)); assert.equal(invoked, false);
});
test('an authenticated archive cannot lose one packaged member through a parser adapter', () => {
  const files = new Map(observed.parsed.files); files.delete('README.md');
  assert.throws(() => inspectOpenai(compressed, plan, () => ({ ...observed.parsed, files }), parseJsonSupported));
});
test('every member hash is checked, including inert JS and original license text', () => {
  for (const path of ['index.js', 'LICENSE']) {
    const files = new Map(observed.parsed.files), bytes = Buffer.from(files.get(path)); bytes[0] ^= 1; files.set(path, bytes);
    assert.throws(() => inspectOpenai(compressed, plan, () => ({ ...observed.parsed, files }), parseJsonSupported));
  }
});
test('all exact .d.ts/.d.mts partners and #x509 source condition remain present', () => {
  for (const path of ['index.d.ts', 'index.d.mts', 'resources/responses/responses.d.ts', 'resources/responses/responses.d.mts', 'resources/chat/completions.d.ts', 'resources/chat/completions.d.mts', 'src/internal/auth/x509-transport-state.cts']) assert(observed.parsed.files.has(path));
  const manifest = parseJsonSupported(observed.evidence.manifest.rawUtf8);
  assert.equal(manifest.exports['.'].require.types, './index.d.ts'); assert.equal(manifest.exports['.'].types, './index.d.mts');
  assert.equal(manifest.imports['#x509-transport-state'].types, './src/internal/auth/x509-transport-state.cts');
  assert.equal(manifest.peerDependenciesMeta.ws.optional, true); assert.equal(manifest.peerDependenciesMeta.undici.optional, true); assert.equal(manifest.peerDependenciesMeta.zod.optional, true);
  assert(observed.parsed.files.get('resources/responses/ws.d.ts').toString('utf8').includes("from 'ws'"));
  assert(observed.parsed.files.get('_vendor/zod-to-json-schema/Options.d.ts').toString('utf8').includes("from 'zod/v3'"));
  assert(observed.parsed.files.get('internal/auth/x509-transport-capability.d.ts').toString('utf8').includes("from 'undici'"));
});

test('complete genuine baseline retains all eight diagnostic families, indices, locks, roots and blockers', () => {
  assert.equal(verifyBaseline(baseline, plan), baseline); assert.equal(baseline.diagnostics.getSemanticDiagnostics.length, 1246);
  assert.equal(baseline.diagnostics.getSuggestionDiagnostics.length, 832); assert.equal(baseline.unresolved.length, 711);
});
for (const [label, mutate] of [
  ['an entire diagnostic family', b => { delete b.diagnostics.getSuggestionDiagnostics; }],
  ['one actual semantic diagnostic', b => { b.diagnostics.getSemanticDiagnostics.pop(); }],
  ['one original unresolved diagnostic', b => { b.unresolved.pop(); }],
  ['an unresolved original index', b => { b.unresolved[0].index = 1; }],
  ['one original root', b => { b.rootFiles.pop(); }],
  ['an already-resolved original lock instance', b => { b.mandatoryBaselineLockInstances.splice(b.mandatoryBaselineLockInstances.findIndex(row => row.name === 'chalk'), 1); }],
  ['one public condition row', b => { b.entrypoints.pop(); }],
  ['one published ownership blocker', b => { b.requiredPublishedOwnershipBlockers.pop(); }],
  ['the pi-ai ownership blocker despite member presence', b => { b.requiredPublishedOwnershipBlockers[0].mandatoryMappingOrOwnershipBlocker = false; }],
  ['the diagnostic-filtering boundary', b => { b.diagnosticFiltering = true; }],
]) test('baseline rejects omission/change of ' + label, () => { const altered = copy(baseline); mutate(altered); assert.throws(() => verifyBaseline(altered, plan)); });

test('complete admission union contains original4581 plus exact3548 without rewriting or relocating', () => {
  const original = { files: Array.from({ length: 4581 }, (_, i) => ({ path: 'upstream/unchanged/' + String(i).padStart(5, '0'), bytes: i, sha256: '0'.repeat(64) })) };
  const files = admissionRows(original, plan.package.members); assert.equal(files.length, 8129);
  for (const row of original.files) assert.equal(files.find(item => item.path === row.path), row);
  assert.equal(files.filter(row => row.path.startsWith('upstream/packages/ai/node_modules/openai/')).length, 3548);
  assert.throws(() => admissionRows({ files: original.files.slice(1) }, plan.package.members));
  assert.throws(() => admissionRows(original, plan.package.members.slice(1)));
  assert.throws(() => admissionRows(original, plan.package.members, 'upstream/node_modules/openai'));
  const duplicate = { files: original.files.slice() }; duplicate.files[0] = { ...duplicate.files[0], path: 'upstream/packages/ai/node_modules/openai/' + plan.package.members[0].path.toUpperCase() };
  assert.throws(() => admissionRows(duplicate, plan.package.members));
});
test('all prior source/compiler/catalog, archive/install and receipt pins verify read-only', async () => {
  const data = await context(plan); assert.equal(data.origin.receipt.files.length, 4581); assert.equal(data.inspected.evidence.archive.files.length, 3548);
  const files = admissionRows(data.origin.receipt, plan.package.members), receipt = admissionRecord(plan, data, {}, files);
  assert.equal(receipt.files.length, 8129); assert.equal(receipt.mandatoryOriginalLockInstances.length, 34);
  assert.equal(receipt.requiredPublishedOwnershipBlockers.length, 3); assert.equal(receipt.newDeclarationFiles, 784);
  for (const key of ['network', 'npm', 'packageCodeImported', 'compilerImported', 'nativeExecuted', 'binLinks', 'lifecycleScripts', 'diagnosticFiltering', 'pathNormalization', 'diagnosticReductionPromised', 'semanticPublicClosure']) assert.equal(receipt[key], false);
  assert.deepEqual(receipt.phaseGatesPassed, []); assert.equal(profileFiles.length, 3);
});
