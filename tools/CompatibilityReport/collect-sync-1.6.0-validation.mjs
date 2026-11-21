#!/usr/bin/env node
// Turns the raw suite receipts that tools/GapValidation/Run-Tests.ps1 writes (artifacts/gap-validation/<label>-<guid>/receipt.json)
// into the 1.6.0 validation ledger and expected-case coverage, then freezes the source-only snapshots that
// accept-sync-1.6.0.mjs reads. The formats are 1.5.2's (AI_DOCS/sync-1.5.2-source-validation.json and -source-coverage.json);
// this is the Node form of the 1.5.2 update-validation-report, Audit-Sync152Coverage and Freeze-Sync152SourceValidation scripts.
//
//   node tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs ledger [--receipts artifacts/gap-validation] [--verify-current-sources]
//        [--solution-receipt <receipt.json>] [--out AI_DOCS/sync-1.6.0-validation.json]
//   node tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs coverage --spec <coverage-spec.json> [--receipts ...]
//        [--out AI_DOCS/sync-1.6.0-coverage.json]
//   node tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs freeze [--ledger AI_DOCS/sync-1.6.0-validation.json]
//        [--coverage AI_DOCS/sync-1.6.0-coverage.json]
//
// The ledger and coverage files are mutable run records and may be rewritten. freeze re-verifies the current source of every
// accepted receipt and writes the policy's ledger and coverage snapshot paths once; it refuses to overwrite either.
// A coverage spec is { schemaVersion: 1, groups: [{ group, project, sources?: [case files], prefix?, expectedIds?: [ids], count }] }:
// a group's expected ids are the ids its case files declare (the 1.5.2 audit's pattern) that start with prefix, plus
// expectedIds for ids built at run time; count pins the static inventory, and a different count stops the audit.
import { readFileSync, writeFileSync, existsSync, readdirSync, mkdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, relative, dirname, isAbsolute } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { SYNC, suiteName, suiteProject, readPolicy, requiredSuites } from './accept-sync-1.6.0.mjs';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const digest = value => createHash('sha256').update(value).digest('hex');
const json = path => JSON.parse(readFileSync(path, 'utf8'));
const text = value => JSON.stringify(value, null, 2) + '\n';
const inside = (root, path) => {
  const full = resolve(root, path), rel = relative(root, full);
  if (!rel || rel === '..' || rel.startsWith('../') || rel.startsWith('..\\') || isAbsolute(rel)) throw new Error(`Path is outside the source repository: ${path}`);
  return full;
};
const portable = (root, path) => relative(root, inside(root, path)).replace(/\\/g, '/');
export const DEFAULTS = { receipts: 'artifacts/gap-validation', ledger: `AI_DOCS/sync-${SYNC.version}-validation.json`, coverage: `AI_DOCS/sync-${SYNC.version}-coverage.json` };

// Every receipt.json one level below the receipts directory, oldest first.
export function readReceipts(root, receiptsDir = DEFAULTS.receipts) {
  const directory = inside(root, receiptsDir);
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true }).filter(entry => entry.isDirectory())
    .map(entry => resolve(directory, entry.name, 'receipt.json')).filter(existsSync)
    .map(path => ({ path: portable(root, path), data: json(path) }))
    .sort((a, b) => String(a.data.startedUtc).localeCompare(String(b.data.startedUtc)));
}
// A full-suite selection: no --only/--filter, no *_FILTER variable, not build-only.
export const isFullSuite = receipt => !(receipt.selection?.testArgs ?? []).some(arg => arg === '--only' || arg === '--filter')
  && !Object.entries(receipt.selection?.filters ?? {}).some(([name, value]) => name.endsWith('FILTER') && value)
  && receipt.selection?.buildOnly !== true;

// The receipt's source manifest still describes the tree: its digest, each file's bytes, and no file it did not record under
// its projects' directories, in the repository build policy or in the validation owner (accept-sync-1.5.2.mjs's checks).
export function sourceCurrent(root, receipt) {
  const problems = [];
  try {
    const manifest = json(inside(root, receipt.source.manifest));
    if (digest(JSON.stringify(manifest.files)) !== manifest.sha256) problems.push('source manifest canonical digest mismatch');
    if (manifest.sha256 !== receipt.source.afterSha256) problems.push('receipt does not end on this source manifest');
    const recorded = new Set(manifest.files.map(file => file.path.replace(/\\/g, '/')));
    const walk = directory => {
      for (const entry of readdirSync(directory, { withFileTypes: true })) {
        if (entry.isDirectory() && !['bin', 'obj', 'node_modules'].includes(entry.name)) walk(resolve(directory, entry.name));
        else if (entry.isFile() && !recorded.has(portable(root, resolve(directory, entry.name)))) problems.push(`new source file: ${portable(root, resolve(directory, entry.name))}`);
      }
    };
    for (const directory of new Set(manifest.files.filter(file => file.path.endsWith('.csproj')).map(file => dirname(inside(root, file.path))))) walk(directory);
    for (const name of ['Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config'])
      if (existsSync(resolve(root, name)) && !recorded.has(name)) problems.push(`new build policy: ${name}`);
    if (existsSync(resolve(root, 'tools/GapValidation'))) for (const entry of readdirSync(resolve(root, 'tools/GapValidation'), { withFileTypes: true }))
      if (entry.isFile() && !recorded.has(`tools/GapValidation/${entry.name}`)) problems.push(`new validation-owner file: ${entry.name}`);
    for (const file of manifest.files) {
      const path = inside(root, file.path);
      if (!existsSync(path) || digest(readFileSync(path)) !== file.sha256) problems.push(`current source differs: ${file.path}`);
    }
  } catch (error) { problems.push(error.message); }
  return problems;
}

const cases = receipt => receipt.caseEvidence?.cases ?? [];
function ledgerRow(root, { path, data }, verify) {
  const failures = cases(data).filter(item => /FAIL|ERROR/.test(item.status ?? ''));
  const fullSuite = isFullSuite(data), stableDuringRun = data.source?.changedDuringRun === false;
  const sourceProblems = verify && data.source?.afterSha256 ? sourceCurrent(root, data) : null;
  const currentSourceMatches = sourceProblems ? sourceProblems.length === 0 : null;
  return {
    project: portable(root, data.project), receipt: path, result: data.result, selectedCount: data.caseEvidence?.selectedCount ?? null,
    fullSuite, stableDuringRun, currentSourceMatches,
    accepted: failures.length === 0 && ['passed', 'passed-with-console-skips'].includes(data.result) && stableDuringRun && fullSuite && (!verify || currentSourceMatches === true),
    failures, skippedCases: cases(data).filter(item => /^SKIP|^TODO/.test(item.status ?? '')), consoleSkips: data.caseEvidence?.consoleSkipObservations ?? [],
    ...(sourceProblems?.length ? { sourceProblems } : {}),
  };
}

// The mutable run ledger: every run, the latest run per project, and the suites the policy still needs.
export function collectLedger({ root = repository, receiptsDir = DEFAULTS.receipts, policy = readPolicy(), verifyCurrentSources = false, solutionReceipt } = {}) {
  root = resolve(root);
  const receipts = readReceipts(root, receiptsDir);
  const byProject = new Map();
  for (const receipt of receipts) byProject.set(portable(root, receipt.data.project), receipt);
  const latest = [...byProject.values()].map(receipt => ledgerRow(root, receipt, verifyCurrentSources)).sort((a, b) => a.project.localeCompare(b.project));
  const table = json(inside(root, SYNC.table));
  const required = requiredSuites(table, policy);
  const pendingSuites = required.filter(name => !latest.some(row => row.project === suiteProject(name) && row.accepted));
  let fullSolution = null;
  if (solutionReceipt) {
    const receipt = json(inside(root, solutionReceipt));
    const passed = receipt.result === 'passed' && (receipt.stages ?? []).every(stage => stage.exitCode === 0) && receipt.source?.changedDuringRun === false;
    const problems = passed ? sourceCurrent(root, receipt) : ['the solution receipt did not pass on unchanged source'];
    fullSolution = { receipt: portable(root, solutionReceipt), sha256: digest(readFileSync(inside(root, solutionReceipt))), currentSourceMatches: problems.length === 0, ...(problems.length ? { problems } : {}) };
  }
  const additionalPending = [
    ...(verifyCurrentSources ? [] : ['current-final-source fingerprint verification']),
    ...(fullSolution?.currentSourceMatches ? [] : ['full solution build']),
  ];
  return {
    schemaVersion: 1, target: `Pi/Pi Durable ${SYNC.version}`, generatedUtc: new Date().toISOString(),
    status: pendingSuites.length || additionalPending.length ? 'validation-in-progress' : 'validation-complete-not-frozen',
    policy: 'Only a passing full-suite receipt with an unchanged project-reference source fingerprint is accepted. Passing with console skips retains explicit limitations. No live provider or SQL Server service tests are implied.',
    sourceCommit: policy.sourceCommit, receipts: portable(root, receiptsDir),
    requiredSuites: required, pendingSuites, additionalPending, fullSolution, latest,
    runs: receipts.map(({ path, data }) => ({ project: portable(root, data.project), label: data.label, receipt: path, result: data.result,
      startedUtc: data.startedUtc, finishedUtc: data.finishedUtc, fullSuite: isFullSuite(data), source: data.source, selectedCount: data.caseEvidence?.selectedCount ?? null })),
  };
}

const CASE_ID = /^\s*(?:yield\s+return\s+)?(?:(?:Case|JsonlFixtureCase)\(\s*|\(\s*)"([^"\r\n]+)"\s*,/gm;
export function expectedIds(root, group) {
  const declared = (group.sources ?? []).flatMap(file => [...readFileSync(inside(root, file), 'utf8').matchAll(CASE_ID)].map(match => match[1]))
    .filter(id => !group.prefix || id.startsWith(group.prefix));
  return [...new Set([...declared, ...(group.expectedIds ?? [])])].sort();
}

// Explicit new case identities compared with the latest full-suite receipt of their project.
export function collectCoverage({ root = repository, receiptsDir = DEFAULTS.receipts, spec }) {
  root = resolve(root);
  const receipts = readReceipts(root, receiptsDir).filter(receipt => isFullSuite(receipt.data));
  const groups = spec.groups.map(group => {
    const expected = expectedIds(root, group);
    if (expected.length !== group.count) throw new Error(`${group.group} static inventory mismatch: expected ${group.count}, extracted ${expected.length}`);
    const run = receipts.filter(receipt => portable(root, receipt.data.project) === group.project).at(-1);
    const observed = run ? cases(run.data) : [];
    const missingIds = expected.filter(id => !observed.some(item => item.id === id));
    const failedOrSkipped = observed.filter(item => expected.includes(item.id) && /FAIL|ERROR|SKIP/.test(item.status ?? ''));
    return { group: group.group, project: group.project, expectedCount: group.count, expectedIds: expected, receipt: run?.path ?? null,
      selectedExpected: observed.filter(item => expected.includes(item.id)).length, missingIds, failedOrSkipped, complete: Boolean(run) && missingIds.length === 0 && failedOrSkipped.length === 0 };
  });
  return { schemaVersion: 1, generatedUtc: new Date().toISOString(), scope: 'Explicit new case identities compared with observed full-suite receipts; raw and authored captures remain separately labeled.',
    groups, complete: groups.every(group => group.complete) };
}

// The immutable source-only snapshots accept-sync-1.6.0.mjs reads. Every accepted receipt's source is re-verified now.
export function freezeValidation({ root = repository, ledgerPath = DEFAULTS.ledger, coveragePath = DEFAULTS.coverage, policy = readPolicy() } = {}) {
  root = resolve(root);
  const ledger = json(inside(root, ledgerPath)), coverage = json(inside(root, coveragePath));
  if (ledger.pendingSuites?.length) throw new Error(`Source suites remain pending: ${ledger.pendingSuites.join(', ')}`);
  if (ledger.additionalPending?.length) throw new Error(`Additional work remains pending: ${ledger.additionalPending.join(', ')}`);
  if (!coverage.complete || coverage.groups.some(group => !group.complete)) throw new Error('Explicit new-case coverage remains incomplete.');
  const requiredRows = ledger.latest.filter(row => ledger.requiredSuites.includes(suiteName(row.project)));
  if (requiredRows.length !== ledger.requiredSuites.length) throw new Error('Required-suite summary does not match the accepted suite set.');
  for (const row of requiredRows) {
    if (!row.accepted || row.currentSourceMatches !== true) throw new Error(`${row.project}: not an accepted current-source receipt`);
    const problems = sourceCurrent(root, json(inside(root, row.receipt)));
    if (problems.length) throw new Error(`${row.project}: source changed since the ledger: ${problems.join('; ')}`);
  }
  if (ledger.fullSolution) {
    const problems = sourceCurrent(root, json(inside(root, ledger.fullSolution.receipt)));
    if (problems.length) throw new Error(`Full-solution source changed: ${problems.join('; ')}`);
  }
  for (const path of [policy.ledger, policy.coverage]) if (existsSync(inside(root, path))) throw new Error(`A frozen source snapshot already exists; do not overwrite it: ${path}`);
  const selected = requiredRows.reduce((sum, row) => sum + row.selectedCount, 0), skipped = requiredRows.reduce((sum, row) => sum + row.skippedCases.length, 0);
  const frozenUtc = new Date().toISOString();
  const frozen = { ...ledger, status: 'source-validation-complete', sourceValidationOnly: true, pendingSuites: [], additionalPending: [],
    packageValidation: { status: 'pending', record: policy.packageValidationRecord },
    summary: { requiredSuites: requiredRows.length, selectedCaseExecutions: selected, passedCaseExecutions: selected - skipped, documentedSkippedCaseExecutions: skipped,
      distinctAssertionCountClaimed: false, coverageGroups: coverage.groups.length, explicitlyTrackedNewCaseIds: coverage.groups.reduce((sum, group) => sum + group.expectedCount, 0) },
    frozenUtc };
  const outputs = [[policy.ledger, frozen], [policy.coverage, { ...coverage, sourceValidationOnly: true, frozenUtc }]];
  for (const [path, value] of outputs) { mkdirSync(dirname(inside(root, path)), { recursive: true }); writeFileSync(inside(root, path), text(value)); }
  return outputs.map(([path]) => ({ path, sha256: digest(readFileSync(inside(root, path))) }));
}

export function runCollectorCli(argv = process.argv.slice(2)) {
  const [command, ...rest] = argv, args = {};
  for (let index = 0; index < rest.length; index++) {
    const name = rest[index];
    if (name === '--verify-current-sources') args.verify = true;
    else if (name.startsWith('--') && rest[index + 1] && !rest[index + 1].startsWith('--')) args[name.slice(2)] = rest[++index];
    else throw new Error(`Invalid argument ${name}`);
  }
  const receiptsDir = args.receipts ?? DEFAULTS.receipts;
  if (command === 'ledger') {
    const ledger = collectLedger({ receiptsDir, verifyCurrentSources: args.verify, solutionReceipt: args['solution-receipt'] });
    writeFileSync(inside(repository, args.out ?? DEFAULTS.ledger), text(ledger));
    console.log(`Validation ledger: ${ledger.latest.length} projects, ${ledger.latest.filter(row => row.accepted).length} accepted, ${ledger.pendingSuites.length} of ${ledger.requiredSuites.length} required suites pending, additional pending: ${ledger.additionalPending.join(', ') || 'none'}.`);
  } else if (command === 'coverage') {
    if (!args.spec) throw new Error('coverage needs --spec <coverage-spec.json>');
    const coverage = collectCoverage({ receiptsDir, spec: json(inside(repository, args.spec)) });
    writeFileSync(inside(repository, args.out ?? DEFAULTS.coverage), text(coverage));
    for (const group of coverage.groups) console.log(`${group.group}: ${group.selectedExpected}/${group.expectedCount}, missing ${group.missingIds.length}, failed/skipped ${group.failedOrSkipped.length}`);
  } else if (command === 'freeze') {
    for (const output of freezeValidation({ ledgerPath: args.ledger, coveragePath: args.coverage })) console.log(JSON.stringify(output));
  } else throw new Error('Usage: collect-sync-1.6.0-validation.mjs ledger|coverage|freeze [options]');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) runCollectorCli();
