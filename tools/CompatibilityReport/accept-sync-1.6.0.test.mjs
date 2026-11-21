// The 1.6.0 acceptance tool and its collector, on synthetic fixtures only. No ledger, coverage or acceptance record of the
// real repository is read or written; compatibility/sync-1.6.0-acceptance.json stays absent until root applies one.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { assessAcceptance, applyAcceptance, SYNC, suiteProject } from './accept-sync-1.6.0.mjs';
import { collectLedger, collectCoverage, freezeValidation } from './collect-sync-1.6.0-validation.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const scratch = resolve(repo, 'artifacts');
const hash = value => createHash('sha256').update(value).digest('hex');
const repoJson = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const sourceCommit = 'faac585884ae2c013a6b1400f3e9e77f10b34bac';
const PENDING = 'implementation-pending-validation';

// A repository with one suite, one receipt, a frozen ledger and coverage, and the 1.6.0 metadata files in miniature.
function fixture(action, { skip = false, suite = 'Unit', rows = [], documentationEdits = [] } = {}) {
  mkdirSync(scratch, { recursive: true });
  const root = mkdtempSync(resolve(scratch, 'acceptance-160-controls-'));
  const write = (path, value) => {
    const full = resolve(root, path); mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, typeof value === 'string' ? value : JSON.stringify(value, null, 2) + '\n');
  };
  const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
  const bytes = path => readFileSync(resolve(root, path));
  try {
    write('README.md', 'Integrated validation of the 1.6.0 candidate is pending.\n');
    const project = suiteProject(suite), sourceFile = `${dirname(project)}/Unit.cs`;
    write(project, '<Project />'); write(sourceFile, '// owned source\n');
    const files = [project, sourceFile].map(path => ({ path, bytes: bytes(path).length, sha256: hash(bytes(path)) }));
    const fingerprint = hash(JSON.stringify(files));
    write('artifacts/gap-validation/unit-1/source-before.json', { sha256: fingerprint, files });
    const cases = [{ id: 'sync160.unit.case', status: 'PASS' }, ...(skip ? [{ id: 'platform-only', status: 'SKIP', reason: 'requires Unix' }] : [])];
    write('artifacts/gap-validation/unit-1/receipt.json', { project: resolve(root, project), label: 'unit', result: 'passed', startedUtc: '2026-11-20T00:00:00.0000000Z',
      stages: [{ name: 'build', exitCode: 0 }, { name: 'tests', exitCode: 0 }], selection: { testArgs: [], filters: {}, buildOnly: false },
      source: { changedDuringRun: false, beforeSha256: fingerprint, afterSha256: fingerprint, manifest: resolve(root, 'artifacts/gap-validation/unit-1/source-before.json') },
      platform: { os: 'test', architecture: 'test' }, caseEvidence: { kind: 'observed-case-lines', selectedCount: cases.length, cases, consoleSkipObservations: [] } });
    // A build of the whole solution on the same source, outside the suite receipts directory.
    write('artifacts/solution/receipt.json', { project: resolve(root, 'PiSharp.slnx'), label: 'solution', result: 'passed', stages: [{ name: 'build', exitCode: 0 }],
      source: { changedDuringRun: false, beforeSha256: fingerprint, afterSha256: fingerprint, manifest: 'artifacts/gap-validation/unit-1/source-before.json' } });
    write('tests/cases.cs', '    Case("sync160.unit.case", () => { }),\n    Case("other.case", () => { }),\n');
    const tableRows = [{ id: 'sync160.file.0001', upstream: ['packages/a/src/a.ts'], status: PENDING, workPackage: 'TEST', review: { finding: 'ported' } },
      { id: 'sync160.file.0002', upstream: ['packages/a/README.md'], status: 'no-op', workPackage: 'TEST', category: 'upstream-documentation', reason: 'docs' }, ...rows];
    write(SYNC.table, { to: { tag: 'v1.6.0', commit: sourceCommit }, rows: tableRows, workPackages: { TEST: { status: PENDING }, IDLE: { status: PENDING } },
      statusMeaning: { 'no-op': 'source-only', [PENDING]: 'pending' }, summary: {} });
    write(SYNC.overlay, { status: PENDING, rows: [{ id: 'sync160.file.0001', status: PENDING }] });
    write(SYNC.map, { files: [{ id: 'historical', status: 'ported' }], currentImplementation: { candidate: { source: { commit: sourceCommit }, status: PENDING } } });
    write(SYNC.implementationStatus, { pi160Candidate: { status: PENDING, sourceCommit }, pi152Candidate: { status: 'tested-with-limitations' } });
    write(SYNC.target, { status: 'Source-level re-freeze per docs/decisions/0015-pi-1.6.0-sync.md; candidate implementation pending validation; unreleased' });
    const policy = { version: '1.6.0', sourceCommit, ledger: 'AI_DOCS/source-validation.json', coverage: 'AI_DOCS/source-coverage.json', scope: 'synthetic local test',
      blockingFindings: ['pending-package', 'not-in-report'], packageValidationRecord: 'AI_DOCS/packaging.json', workPackages: { TEST: [suite] }, documentationEdits };
    let solutionReceipt = 'artifacts/solution/receipt.json';
    const unsetSolution = () => { solutionReceipt = undefined; };
    const collect = () => {
      write('AI_DOCS/validation.json', collectLedger({ root, policy, verifyCurrentSources: true, solutionReceipt }));
      write('AI_DOCS/coverage.json', collectCoverage({ root, spec: { schemaVersion: 1, groups: [{ group: 'unit', project, sources: ['tests/cases.cs'], prefix: 'sync160.', count: 1 }] } }));
    };
    const freeze = () => { collect(); return freezeValidation({ root, ledgerPath: 'AI_DOCS/validation.json', coveragePath: 'AI_DOCS/coverage.json', policy }); };
    const assess = () => {
      if (!existsSync(resolve(root, policy.ledger))) freeze();
      return assessAcceptance({ root, ledgerPath: policy.ledger, ledgerSha256: hash(bytes(policy.ledger)), coveragePath: policy.coverage, coverageSha256: hash(bytes(policy.coverage)), policy });
    };
    action({ root, write, read, bytes, assess, collect, freeze, policy, project, unsetSolution });
  } finally {
    assert(relative(scratch, root).startsWith('acceptance-160-controls-'));
    rmSync(root, { recursive: true, force: true });
  }
}

test('the collector builds the 1.5.2 ledger and coverage formats from raw receipts and freezes them once', () => fixture(({ read, freeze, policy, project }) => {
  const outputs = freeze();
  assert.deepEqual(outputs.map(output => output.path), [policy.ledger, policy.coverage]);
  const ledger = read(policy.ledger);
  assert.equal(ledger.status, 'source-validation-complete'); assert.equal(ledger.sourceValidationOnly, true);
  assert.deepEqual(ledger.packageValidation, { status: 'pending', record: 'AI_DOCS/packaging.json' });
  assert.deepEqual(ledger.requiredSuites, ['Unit']);
  assert.deepEqual(ledger.latest.map(row => [row.project, row.accepted, row.fullSuite, row.currentSourceMatches]), [[project, true, true, true]]);
  const coverage = read(policy.coverage);
  assert.deepEqual(coverage.groups[0].expectedIds, ['sync160.unit.case']); assert.equal(coverage.complete, true);
  assert.throws(freeze, /already exists/);
}));

test('the collector keeps a filtered, failed or later-changed run out of the accepted set', () => fixture(({ root, read, write, collect, freeze }) => {
  const receipt = read('artifacts/gap-validation/unit-1/receipt.json');
  write('artifacts/gap-validation/unit-2/receipt.json', { ...receipt, startedUtc: '2026-11-21T00:00:00.0000000Z', selection: { testArgs: ['--only', 'sync160.'], filters: {}, buildOnly: false } });
  collect();
  const ledger = read('AI_DOCS/validation.json');
  assert.deepEqual([ledger.latest[0].receipt, ledger.latest[0].fullSuite, ledger.latest[0].accepted], ['artifacts/gap-validation/unit-2/receipt.json', false, false]);
  assert.deepEqual(ledger.pendingSuites, ['Unit']);
  assert.equal(read('AI_DOCS/coverage.json').groups[0].receipt, 'artifacts/gap-validation/unit-1/receipt.json', 'coverage reads the latest full-suite run');
  assert.throws(freeze, /remain pending: Unit/);
  rmSync(resolve(root, 'artifacts/gap-validation/unit-2'), { recursive: true });
  write('tests/PiSharp.Unit.Tests/Added.cs', '// not observed by the run\n');
  collect();
  assert.equal(read('AI_DOCS/validation.json').latest[0].currentSourceMatches, false);
  assert(read('AI_DOCS/validation.json').latest[0].sourceProblems.some(problem => problem.includes('new source file')));
}));

test('freezing needs a current full-solution build and verified current sources', () => fixture(({ read, freeze, unsetSolution }) => {
  unsetSolution();
  assert.throws(freeze, /Additional work remains pending: full solution build/);
  assert.equal(read('AI_DOCS/validation.json').status, 'validation-in-progress');
}));

test('a waived full-solution build is kept in the ledger and becomes a limitation of the acceptance', () => fixture(({ root, write, read, policy, bytes }) => {
  write('AI_DOCS/validation.json', collectLedger({ root, policy, verifyCurrentSources: true, waiveSolutionBuild: 'every required suite built its own closure' }));
  write('AI_DOCS/coverage.json', collectCoverage({ root, spec: { groups: [{ group: 'unit', project: suiteProject('Unit'), sources: ['tests/cases.cs'], prefix: 'sync160.', count: 1 }] } }));
  freezeValidation({ root, ledgerPath: 'AI_DOCS/validation.json', coveragePath: 'AI_DOCS/coverage.json', policy });
  assert.deepEqual(read(policy.ledger).fullSolution, { waived: 'every required suite built its own closure' });
  const plan = assessAcceptance({ root, ledgerPath: policy.ledger, ledgerSha256: hash(bytes(policy.ledger)), coveragePath: policy.coverage, coverageSha256: hash(bytes(policy.coverage)), policy });
  assert.equal(plan.eligible, true, plan.problems.join('\n'));
  assert.deepEqual([plan.status, plan.limitations], ['tested-with-limitations', ['No full-solution build receipt was recorded: every required suite built its own closure']]);
}));

test('the coverage audit stops when the static inventory differs from the pinned count', () => fixture(({ root, project }) => {
  assert.throws(() => collectCoverage({ root, spec: { groups: [{ group: 'unit', project, sources: ['tests/cases.cs'], count: 1 }] } }), /static inventory mismatch: expected 1, extracted 2/);
  const coverage = collectCoverage({ root, spec: { groups: [{ group: 'unit', project, sources: ['tests/cases.cs'], prefix: 'sync160.', expectedIds: ['not-run'], count: 2 }] } });
  assert.deepEqual([coverage.complete, coverage.groups[0].missingIds], [false, ['not-run']]);
}));

test('a complete frozen ledger produces a review plan without editing metadata', () => fixture(({ root, assess, bytes }) => {
  const before = bytes(SYNC.table);
  const plan = assess();
  assert.equal(plan.eligible, true, plan.problems.join('\n')); assert.equal(plan.status, 'tested');
  assert.deepEqual(plan.workPackages.IDLE, { status: 'no-op', suites: [] });
  assert.deepEqual(bytes(SYNC.table), before);
  assert(!existsSync(resolve(root, SYNC.record)));
}));

test('a package that has not landed and an unreported change block acceptance', () => fixture(({ assess }) => {
  const plan = assess(); assert.equal(plan.eligible, false);
  assert(plan.problems.some(problem => problem.startsWith('sync160.file.0003') && problem.includes('pending in chord-admission')));
  assert(plan.problems.some(problem => problem.startsWith('sync160.file.0004') && problem.includes('pending-package')));
  assert(plan.problems.some(problem => problem.startsWith('sync160.file.0005') && problem.includes('not-in-report')));
}, { rows: [
  { id: 'sync160.file.0003', upstream: ['packages/chord/src/host/route.ts'], status: PENDING, workPackage: 'TEST', review: { finding: 'ported', pendingParts: ['chord-admission'] } },
  { id: 'sync160.file.0004', upstream: ['packages/chord/src/host/admission.ts'], status: PENDING, workPackage: 'TEST', review: { finding: 'pending-package' } },
  { id: 'sync160.file.0005', upstream: ['packages/ai/test/cache-retention.test.ts'], status: PENDING, workPackage: 'TEST', review: { finding: 'not-in-report' } },
] }));

test('application keeps no-op and history, limits a deferred row and writes no documentation of its own', () => fixture(({ root, assess, read }) => {
  const plan = assess(); assert.equal(plan.eligible, true, plan.problems.join('\n')); assert.equal(plan.status, 'tested-with-limitations');
  assert(plan.workPackages.TEST.limitations.some(value => value.includes('requires Unix')));
  assert(plan.workPackages.TEST.limitations.some(value => value.includes('SIGTERM grace')));
  const changed = applyAcceptance(root, plan);
  assert.deepEqual(changed.sort(), [SYNC.map, SYNC.implementationStatus, SYNC.record, SYNC.table, SYNC.target, SYNC.overlay].sort());
  const table = read(SYNC.table);
  assert.equal(table.rows[0].status, 'tested-with-limitations');
  assert.equal(table.rows[1].status, 'no-op'); assert.equal(table.rows[1].validation, undefined);
  assert.deepEqual(table.rows[2].validation.limitations, ['packages/coding-agent/src/core/exec.ts: SIGTERM grace is not run on POSIX']);
  assert(table.rows.every(row => row.status in table.statusMeaning && row.status !== PENDING));
  assert.deepEqual(table.summary.byStatus, { 'tested-with-limitations': 2, 'no-op': 1 });
  assert.equal(table.acceptance.record, SYNC.record);
  assert.deepEqual(read(SYNC.map).files, [{ id: 'historical', status: 'ported' }]);
  assert.equal(read(SYNC.map).currentImplementation.candidate.acceptance, SYNC.record);
  assert.equal(read(SYNC.implementationStatus).pi160Candidate.status, 'tested-with-limitations');
  assert.equal(read(SYNC.implementationStatus).pi152Candidate.status, 'tested-with-limitations');
  assert.equal(read(SYNC.overlay).rows[0].status, 'tested-with-limitations');
  assert.match(read(SYNC.target).status, /tested-with-limitations local source evidence in compatibility\/sync-1.6.0-acceptance.json; unreleased$/);
  assert.equal(readFileSync(resolve(root, 'README.md'), 'utf8'), 'Integrated validation of the 1.6.0 candidate is pending.\n');
  assert.equal(read(SYNC.record).appliedAt.length > 0, true);
  assert.equal(assess().eligible, false, 'a second acceptance is refused');
}, { skip: true, rows: [{ id: 'sync160.file.0003', upstream: ['packages/coding-agent/src/core/exec.ts'], status: PENDING, workPackage: 'TEST',
  review: { finding: 'ported', deferred: 'SIGTERM grace is not run on POSIX' } }] }));

test('the policy\'s documentation edits must match exactly, and they are the only documentation written', () => {
  fixture(({ root, assess }) => {
    const changed = applyAcceptance(root, assess());
    assert(changed.includes('README.md'));
    assert.equal(readFileSync(resolve(root, 'README.md'), 'utf8'), 'Local source validation of the 1.6.0 candidate is recorded with its limitations.\n');
  }, { documentationEdits: [['README.md', 'Integrated validation of the 1.6.0 candidate is pending.', 'Local source validation of the 1.6.0 candidate is recorded with its limitations.']] });
  fixture(({ assess }) => {
    const plan = assess(); assert.equal(plan.eligible, false);
    assert(plan.problems.some(problem => problem.includes('README.md: a documentation edit')));
  }, { documentationEdits: [['README.md', 'text closeout-docs replaced', 'anything']] });
});

test('stale or newly added source files block acceptance', () => fixture(({ write, assess, freeze }) => {
  freeze();
  write('tests/PiSharp.Unit.Tests/Unit.cs', '// changed after test\n');
  write('tests/PiSharp.Unit.Tests/Added.cs', '// not observed by the run\n');
  const plan = assess(); assert.equal(plan.eligible, false);
  assert(plan.problems.some(value => value.includes('current source differs')));
  assert(plan.problems.some(value => value.includes('new source file absent')));
}));

test('source changes between a successful plan and application are refused', () => fixture(({ root, write, assess, bytes }) => {
  const plan = assess(); assert.equal(plan.eligible, true);
  write('tests/PiSharp.Unit.Tests/Later.cs', '// arrived after the plan\n');
  const before = bytes(SYNC.table);
  assert.throws(() => applyAcceptance(root, plan), /source changed after review/);
  assert.deepEqual(bytes(SYNC.table), before);
}));

test('metadata application cannot invalidate an accepted linked source input', () => fixture(({ root, read, write, assess }) => {
  const manifestPath = 'artifacts/gap-validation/unit-1/source-before.json', manifest = read(manifestPath);
  const target = readFileSync(resolve(root, SYNC.target));
  manifest.files.push({ path: SYNC.target, bytes: target.length, sha256: hash(target) });
  manifest.sha256 = hash(JSON.stringify(manifest.files)); write(manifestPath, manifest);
  const receipt = read('artifacts/gap-validation/unit-1/receipt.json'); receipt.source.beforeSha256 = receipt.source.afterSha256 = manifest.sha256;
  write('artifacts/gap-validation/unit-1/receipt.json', receipt);
  const solution = read('artifacts/solution/receipt.json'); solution.source = receipt.source; write('artifacts/solution/receipt.json', solution);
  const plan = assess(); assert.equal(plan.eligible, true, plan.problems.join('\n'));
  assert.throws(() => applyAcceptance(root, plan), /invalidate an accepted runtime input: compatibility\/target.lock.json/);
  assert.equal(read(SYNC.table).rows[0].status, PENDING);
  assert(!existsSync(resolve(root, SYNC.record)));
}));

test('ledger and coverage hashes are explicit immutable inputs', () => fixture(({ root, assess, policy, bytes }) => {
  assess();
  assert.throws(() => assessAcceptance({ root, ledgerPath: policy.ledger, ledgerSha256: '0'.repeat(64), coveragePath: policy.coverage, coverageSha256: hash(bytes(policy.coverage)), policy }), /SHA-256/);
  const mutable = assessAcceptance({ root, ledgerPath: 'AI_DOCS/validation.json', ledgerSha256: hash(bytes('AI_DOCS/validation.json')),
    coveragePath: policy.coverage, coverageSha256: hash(bytes(policy.coverage)), policy });
  assert.equal(mutable.eligible, false);
  assert(mutable.problems.includes('use the immutable source-only ledger snapshot'));
  assert(mutable.problems.includes('ledger must explicitly declare source-only validation'));
}));

test('a catalog whose provenance names another release blocks acceptance', () => fixture(({ write, assess, policy }) => {
  write('catalog/1.6.0/a.json', '{}');
  write('catalog/1.6.0/.manifest.json', { files: { 'a.json': hash('{}') } });
  write('catalog/1.6.0.provenance.json', { version: '1.5.2', sourceCommit, manifestSha256: '0'.repeat(64), providerCount: 1 });
  write('catalog/report.md', '# catalog\n');
  policy.catalog = { provenance: 'catalog/1.6.0.provenance.json', directory: 'catalog/1.6.0', report: 'catalog/report.md' };
  const plan = assess(); assert.equal(plan.eligible, false);
  assert(plan.problems.includes('catalog provenance target mismatch'));
  assert(plan.problems.includes('catalog manifest digest mismatch'));
}));

test('the committed policy maps every 1.6.0 work package to existing suites and blocks what has not landed', () => {
  const policy = repoJson(SYNC.policy), table = repoJson(SYNC.table);
  assert.equal(policy.sourceCommit, table.to.commit);
  assert.deepEqual(Object.keys(policy.workPackages).sort(), Object.keys(table.workPackages).sort());
  for (const [id, suites] of Object.entries(policy.workPackages)) {
    assert(suites.length, id);
    for (const suite of suites) assert(existsSync(resolve(repo, suiteProject(suite))), `${id}: ${suite}`);
  }
  assert.deepEqual(policy.blockingFindings, ['pending-package', 'not-in-report']);
  assert.deepEqual(policy.documentationEdits, [], 'documentation wording belongs to closeout-docs; root adds exact edits before applying');
  const provenance = repoJson(policy.catalog.provenance);
  assert.deepEqual([provenance.version, provenance.sourceCommit], [policy.version, policy.sourceCommit]);
});

// Until root applies an acceptance, the candidate is an unreleased source candidate whose integrated validation is pending.
test('the 1.6.0 candidate records match the acceptance state', () => {
  const policy = repoJson(SYNC.policy), table = repoJson(SYNC.table), candidate = repoJson(SYNC.implementationStatus)[SYNC.candidateKey];
  const pointer = repoJson(SYNC.map).currentImplementation.candidate;
  assert.deepEqual([candidate.sourceVersion, candidate.sourceCommit, candidate.released], [policy.version, policy.sourceCommit, false]);
  assert.deepEqual([candidate.applicability, candidate.durableOverlay, candidate.decision], [SYNC.table, SYNC.overlay, SYNC.decision]);
  if (!existsSync(resolve(repo, SYNC.record))) {
    assert.deepEqual([candidate.status, candidate.acceptance, table.acceptance, pointer.status], [PENDING, null, undefined, PENDING]);
    assert(table.rows.every(row => [PENDING, 'no-op'].includes(row.status)), 'no row is tested before acceptance');
  } else {
    assert.deepEqual([candidate.acceptance, table.acceptance.record, pointer.acceptance], [SYNC.record, SYNC.record, SYNC.record]);
  }
});
