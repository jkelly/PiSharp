import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { assessAcceptance, applyAcceptance } from './accept-sync-1.5.2.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const scratch = resolve(repo, 'artifacts');
const hash = value => createHash('sha256').update(value).digest('hex');
const sourceCommit = 'b4afdddf44e059c67ccf5097e5317d913521b470';
const pendingDocs = {
  'README.md': 'It is unreleased and integrated validation is pending.',
  'IMPLEMENTATION_STATUS.md': 'Applicable candidate work is **implementation-pending-validation**; historical records follow.',
  'website/src/pages/index.astro': 'integrated validation is pending.',
  'website/src/pages/parity.astro': 'Applicable work is pending integrated validation; a source pin does not establish parity.',
  'website/src/content/docs/docs/index.md': 'Its integrated validation is pending;',
  'website/src/content/docs/docs/extensions/node-bridge.md': 'Integrated candidate validation is pending.',
  'website/src/content/docs/docs/guides/durable.md': 'Applicable work remains pending integrated validation.',
  'website/src/content/docs/docs/versioning.md': 'integrated validation remains pending',
  'docs/release-notes/1.5.2.md': '# Unreleased candidate\n\nCatalog: 43 providers and 1,773 entries. Existing compatibility switches use bool?; null inherits and explicit false wins.\n\n## Validation status\n\nPending.',
  'AI_DOCS/sync-1.5.2.md': '# Work plan\n\nHistorical planning remains here.\n\n## Validation evidence\n\nTool/provider/telemetry findings require fresh passing receipts.\n\n## Independent review corrections\n\nOriginal failed-run history is retained in the mutable ledger.',
};

function fixture(action, { skip = false, suite = 'Unit' } = {}) {
  mkdirSync(scratch, { recursive: true });
  const root = mkdtempSync(resolve(scratch, 'acceptance-controls-'));
  const write = (path, value) => {
    const full = resolve(root, path); mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, typeof value === 'string' ? value : JSON.stringify(value, null, 2) + '\n');
  };
  const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
  try {
    for (const [path, text] of Object.entries(pendingDocs)) write(path, text);
    const project = suite === 'Extensions.ContractTests' ? 'tests/PiSharp.Extensions.ContractTests/PiSharp.Extensions.ContractTests.csproj'
      : `tests/PiSharp.${suite}.Tests/PiSharp.${suite}.Tests.csproj`;
    const sourceFile = `${dirname(project)}/Unit.cs`;
    write(project, '<Project />'); write(sourceFile, '// owned source\n');
    const files = [project, sourceFile].map(path => ({ path, bytes: readFileSync(resolve(root, path)).length, sha256: hash(readFileSync(resolve(root, path))) }));
    const fingerprint = hash(JSON.stringify(files));
    write('artifacts/run/source-before.json', { sha256: fingerprint, files });
    const cases = [{ id: 'unit.case', status: 'PASS_AUTHORED_NATIVE_ONLY' }, ...(skip ? [{ id: 'platform-only', status: 'SKIP', reason: 'requires Unix' }] : [])];
    write('artifacts/run/receipt.json', { project, result: 'passed', stages: [{ name: 'build', exitCode: 0 }, { name: 'tests', exitCode: 0 }],
      source: { changedDuringRun: false, beforeSha256: fingerprint, afterSha256: fingerprint, manifest: 'artifacts/run/source-before.json' },
      platform: { os: 'test', architecture: 'test' }, caseEvidence: { kind: 'console-json', cases, consoleSkipObservations: [] } });
    const row = { project, accepted: true, fullSuite: true, stableDuringRun: true, currentSourceMatches: true,
      result: skip ? 'passed-with-console-skips' : 'passed', selectedCount: cases.length, failures: [],
      skippedCases: cases.filter(item => item.status === 'SKIP'), consoleSkips: [], receipt: 'artifacts/run/receipt.json' };
    write('AI_DOCS/ledger.json', { status: 'completed', sourceValidationOnly: true, packageValidation: 'pending-not-asserted', pendingSuites: [], additionalPending: [], latest: [row] });
    write('AI_DOCS/coverage.json', { complete: true, groups: [{ group: 'unit', project, complete: true, expectedCount: 1, expectedIds: ['unit.case'],
      selectedExpected: 1, missingIds: [], failedOrSkipped: [], receipt: 'artifacts/run/receipt.json' }] });
    const noOp = { id: 'no-op', status: 'no-op', workPackage: 'TEST', reason: 'source-only' };
    write('compatibility/sync-1.5.2-applicability.json', { to: { commit: sourceCommit }, rows: [{ id: 'runtime', status: 'implementation-pending-validation', workPackage: 'TEST' }, noOp],
      workPackages: { TEST: { status: 'implementation-pending-validation' } }, statusMeaning: { 'no-op': 'source-only', 'implementation-pending-validation': 'pending' }, summary: {} });
    write('compatibility/durable-sync-1.5.2-overlay.json', { status: 'implementation-pending-validation', rows: [{ id: 'runtime', status: 'implementation-pending-validation' }] });
    write('compatibility/durable-port-map.json', { files: [{ id: 'historical', status: 'ported' }], currentImplementation: { candidate: { status: 'implementation-pending-validation' } } });
    write('compatibility/implementation-status.json', { pi152Candidate: { status: 'implementation-pending-validation' }, historical: { status: 'unchanged' } });
    write('website/src/data/parity.json', { sourceCandidate: { status: 'implementation-pending-validation' }, baseline: 'Pi v1.5.0' });
    write('compatibility/target.lock.json', { status: 'source frozen' });
    const assess = () => assessAcceptance({ root, ledgerPath: 'AI_DOCS/ledger.json', ledgerSha256: hash(readFileSync(resolve(root, 'AI_DOCS/ledger.json'))),
      coveragePath: 'AI_DOCS/coverage.json', coverageSha256: hash(readFileSync(resolve(root, 'AI_DOCS/coverage.json'))),
      policy: { sourceCommit, scope: 'synthetic local test', workPackages: { TEST: [suite] } } });
    action({ root, write, read, assess, noOp });
  } finally {
    assert(relative(scratch, root).startsWith('acceptance-controls-'));
    rmSync(root, { recursive: true, force: true });
  }
}

test('a complete accepted ledger produces a review plan without editing metadata', () => fixture(({ root, assess }) => {
  const before = readFileSync(resolve(root, 'compatibility/sync-1.5.2-applicability.json'));
  const plan = assess();
  assert.equal(plan.eligible, true); assert.equal(plan.status, 'tested');
  assert.deepEqual(readFileSync(resolve(root, 'compatibility/sync-1.5.2-applicability.json')), before);
}));

test('the final policy requires the authorized extra suites and keeps SDKBeta auxiliary', () => {
  const policy = JSON.parse(readFileSync(resolve(repo, 'tools/CompatibilityReport/sync-1.5.2-acceptance-policy.json'), 'utf8'));
  const required = Object.values(policy.workPackages).flat();
  assert(required.includes('OpenAICompatibilityGap'), 'the verified compatibility suite belongs to AI acceptance');
  assert.equal(new Set(required).size, 47, 'every required runtime suite is represented exactly once after deduplication');
  for (const name of ['AnthropicSimple', 'AnthropicProfile', 'AnthropicAuthSync', 'AzureResponses', 'AzureSimple',
    'NativeModelCatalog', 'ClassifierConfig', 'Classifiers', 'Agent', 'Rpc', 'Durable.Hosting', 'Durable.Server', 'Tui.Editor',
    'McpParity', 'McpTuiOriginalQualification', 'DurableMessages'])
    assert(required.includes(name), name);
  assert(!required.includes('SDKBeta'));
});

test('raw evidence format and captured status identities remain distinct', () => fixture(({ read, write, assess }) => {
  const receipt = read('artifacts/run/receipt.json');
  receipt.caseEvidence.kind = 'console-pass-lines'; receipt.caseEvidence.cases[0].status = 'PASS_UPSTREAM_CAPTURE';
  write('artifacts/run/receipt.json', receipt);
  const plan = assess(); assert.equal(plan.eligible, true);
  assert.equal(plan.suites.Unit.evidenceKind, 'console-pass-lines');
  assert.deepEqual(plan.suites.Unit.caseStatusCounts, { PASS_UPSTREAM_CAPTURE: 1 });
}));

test('exact native lowercase passed is accepted without accepting unknown prefixes or failures', () => fixture(({ read, write, assess }) => {
  const receipt = read('artifacts/run/receipt.json');
  receipt.caseEvidence.kind = 'native-lowercase-results';
  receipt.caseEvidence.cases[0].status = 'passed';
  write('artifacts/run/receipt.json', receipt);
  const plan = assess(); assert.equal(plan.eligible, true);
  assert.equal(plan.suites.Unit.evidenceKind, 'native-lowercase-results');
  assert.deepEqual(plan.suites.Unit.caseStatusCounts, { passed: 1 });
  for (const status of ['passed-extra', 'passed_with_warning', 'passing', 'failed', 'FAIL']) {
    receipt.caseEvidence.cases[0].status = status; write('artifacts/run/receipt.json', receipt);
    assert.equal(assess().eligible, false, status);
  }
}));

test('the exact Extensions.ContractTests project is recognized without relaxing project identity', () => fixture(({ root, read, write, assess }) => {
  assert.equal(assess().eligible, true);
  const receipt = read('artifacts/run/receipt.json');
  receipt.project = 'other/PiSharp.Extensions.ContractTests.csproj'; write('artifacts/run/receipt.json', receipt);
  const rejected = assess(); assert.equal(rejected.eligible, false);
  assert(rejected.problems.some(problem => problem.includes('another project path')));
}, { suite: 'Extensions.ContractTests' }));

test('custom assembly names do not replace canonical qualification project identity', () => fixture(({ read, write, assess }) => {
  const receipt = read('artifacts/run/receipt.json');
  receipt.stages[1].arguments = ['tests/PiSharp.McpTuiOriginalQualification.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll'];
  write('artifacts/run/receipt.json', receipt);
  const plan = assess(); assert.equal(plan.eligible, true);
  assert.equal(plan.suites.McpTuiOriginalQualification.suite, 'McpTuiOriginalQualification');
  receipt.project = 'tests/PiSharp.CodingAgent.Tests/PiSharp.CodingAgent.Tests.csproj';
  write('artifacts/run/receipt.json', receipt);
  assert(assess().problems.some(problem => problem.includes('another project path')));
}, { suite: 'McpTuiOriginalQualification' }));

test('explicit application retains no-op/history and exposes skipped-case limitations', () => fixture(({ root, assess, read, noOp }) => {
  const plan = assess(); assert.equal(plan.eligible, true); assert.equal(plan.status, 'tested-with-limitations');
  assert(plan.workPackages.TEST.limitations.some(value => value.includes('requires Unix')));
  applyAcceptance(root, plan);
  const table = read('compatibility/sync-1.5.2-applicability.json');
  assert.equal(table.rows[0].status, 'tested-with-limitations'); assert.deepEqual(table.rows[1], noOp);
  assert(table.rows.every(row => row.status in table.statusMeaning));
  assert(table.rows.every(row => row.status !== 'implementation-pending-validation'));
  assert.equal(table.workPackages.TEST.status, 'tested-with-limitations');
  assert.deepEqual(read('compatibility/durable-port-map.json').files, [{ id: 'historical', status: 'ported' }]);
  assert.equal(read('website/src/data/parity.json').baseline, 'Pi v1.5.0');
  assert.equal(read('compatibility/implementation-status.json').historical.status, 'unchanged');
  const workplan = readFileSync(resolve(root, 'AI_DOCS/sync-1.5.2.md'), 'utf8');
  assert(!workplan.includes('require fresh passing receipts'));
  assert(workplan.includes('[the source ledger](../AI_DOCS/ledger.json)'));
  assert(workplan.includes('Original failed-run history is retained in the mutable ledger.'));
  const notes = readFileSync(resolve(root, 'docs/release-notes/1.5.2.md'), 'utf8');
  assert(notes.includes('43 providers and 1,773 entries'));
  assert(notes.includes('bool?; null inherits and explicit false wins'));
}, { skip: true }));

test('stale or newly added source files block acceptance', () => fixture(({ write, assess }) => {
  write('tests/PiSharp.Unit.Tests/Unit.cs', '// changed after test\n');
  write('tests/PiSharp.Unit.Tests/Added.cs', '// not observed by the run\n');
  const plan = assess(); assert.equal(plan.eligible, false);
  assert(plan.problems.some(value => value.includes('current source differs')));
  assert(plan.problems.some(value => value.includes('new source file absent')));
}));

test('source changes between a successful plan and application are refused', () => fixture(({ root, write, assess }) => {
  const plan = assess(); assert.equal(plan.eligible, true);
  write('tests/PiSharp.Unit.Tests/Later.cs', '// arrived after the plan\n');
  assert.throws(() => applyAcceptance(root, plan), /source changed after review/);
}));

test('metadata application cannot invalidate an accepted linked source input', () => fixture(({ root, read, write, assess }) => {
  const manifest = read('artifacts/run/source-before.json');
  const bytes = readFileSync(resolve(root, 'README.md'));
  manifest.files.push({ path: 'README.md', bytes: bytes.length, sha256: hash(bytes) });
  manifest.sha256 = hash(JSON.stringify(manifest.files)); write('artifacts/run/source-before.json', manifest);
  const receipt = read('artifacts/run/receipt.json'); receipt.source.beforeSha256 = receipt.source.afterSha256 = manifest.sha256;
  write('artifacts/run/receipt.json', receipt);
  const plan = assess(); assert.equal(plan.eligible, true);
  assert.throws(() => applyAcceptance(root, plan), /invalidate an accepted runtime input: README.md/);
  assert.equal(read('compatibility/sync-1.5.2-applicability.json').rows[0].status, 'implementation-pending-validation');
}));

test('missing final source qualification and missing expected cases block every write', () => fixture(({ root, read, write, assess }) => {
  const ledger = read('AI_DOCS/ledger.json'); ledger.latest[0].currentSourceMatches = null; write('AI_DOCS/ledger.json', ledger);
  const coverage = read('AI_DOCS/coverage.json'); coverage.groups[0].expectedIds = ['not-run']; write('AI_DOCS/coverage.json', coverage);
  const before = readFileSync(resolve(root, 'compatibility/sync-1.5.2-applicability.json'));
  const plan = assess(); assert.equal(plan.eligible, false);
  assert(plan.problems.some(value => value.includes('current-source qualification')));
  assert(plan.problems.some(value => value.includes('not a passed case')));
  assert.throws(() => applyAcceptance(root, plan), /blocked/);
  assert.deepEqual(readFileSync(resolve(root, 'compatibility/sync-1.5.2-applicability.json')), before);
}));

test('receipt coverage and ledger hashes are immutable inputs', () => fixture(({ root, read, write, assess }) => {
  const coverage = read('AI_DOCS/coverage.json'); coverage.groups[0].receipt = 'artifacts/other/receipt.json'; write('AI_DOCS/coverage.json', coverage);
  assert(assess().problems.some(value => value.includes('superseded receipt')));
  assert.throws(() => assessAcceptance({ root, ledgerPath: 'AI_DOCS/ledger.json', ledgerSha256: '0'.repeat(64),
    coveragePath: 'AI_DOCS/coverage.json', coverageSha256: hash(readFileSync(resolve(root, 'AI_DOCS/coverage.json'))), policy: {} }), /SHA-256/);
}));
