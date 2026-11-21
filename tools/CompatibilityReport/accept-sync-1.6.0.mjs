#!/usr/bin/env node
// Explicit final metadata transition for the v1.5.2..v1.6.0 sync. Without --apply it only prints (or writes with --out) a
// review plan; nothing in the repository changes.
//
//   node tools/CompatibilityReport/accept-sync-1.6.0.mjs --accepted-ledger AI_DOCS/sync-1.6.0-source-validation.json \
//     --ledger-sha256 <sha> --coverage AI_DOCS/sync-1.6.0-source-coverage.json --coverage-sha256 <sha> [--out <plan.json>] [--apply]
//
// Generalized from accept-sync-1.5.2.mjs, which stays as it is: one suite receipt is judged by its assessSuite, and the
// ledger and coverage formats are 1.5.2's (collect-sync-1.6.0-validation.mjs writes them). What differs:
//   - the sync's paths are in the SYNC block and the suites, catalog, packaging record and limitations in the policy file;
//   - a row's reviewed finding (sync-1.6.0-dispositions.mjs) can block acceptance: a package that has not landed
//     (finding pending-package, or pendingParts) and the policy's blockingFindings; regenerate the table once it lands;
//   - a row's reviewed deferral is a limitation of that row, so the row and its package are tested-with-limitations;
//   - it edits no documentation of its own. The policy's documentationEdits ([path, exact text, replacement]) are the only
//     documentation it changes, and each must match exactly or nothing is written.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, relative, dirname, isAbsolute } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { assessSuite } from './accept-sync-1.5.2.mjs';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const SYNC = {
  version: '1.6.0',
  policy: 'tools/CompatibilityReport/sync-1.6.0-acceptance-policy.json',
  table: 'compatibility/sync-1.6.0-applicability.json',
  overlay: 'compatibility/durable-sync-1.6.0-overlay.json',
  map: 'compatibility/durable-port-map.json',
  implementationStatus: 'compatibility/implementation-status.json', candidateKey: 'pi160Candidate',
  target: 'compatibility/target.lock.json',
  decision: 'docs/decisions/0015-pi-1.6.0-sync.md',
  record: 'compatibility/sync-1.6.0-acceptance.json',
};
const NO_OP = 'no-op', TESTED = 'tested', LIMITED = 'tested-with-limitations';

const digest = value => createHash('sha256').update(value).digest('hex');
const json = path => JSON.parse(readFileSync(path, 'utf8'));
const inside = (root, path) => {
  const full = resolve(root, path), rel = relative(root, full);
  if (!rel || rel === '..' || rel.startsWith('../') || rel.startsWith('..\\') || isAbsolute(rel)) throw new Error(`Path is outside the source repository: ${path}`);
  return full;
};
const portable = (root, path) => relative(root, inside(root, path)).replace(/\\/g, '/');
const unique = values => [...new Set(values)];
export const suiteName = project => {
  const filename = project.replace(/\\/g, '/').split('/').at(-1);
  return filename === 'PiSharp.Extensions.ContractTests.csproj' ? 'Extensions.ContractTests'
    : /^PiSharp\.(.+)\.Tests\.csproj$/.exec(filename)?.[1];
};
export const suiteProject = name => name === 'Extensions.ContractTests'
  ? 'tests/PiSharp.Extensions.ContractTests/PiSharp.Extensions.ContractTests.csproj'
  : `tests/PiSharp.${name}.Tests/PiSharp.${name}.Tests.csproj`;
export const readPolicy = (path = SYNC.policy) => json(resolve(repository, path));
// The work packages whose rows need runtime evidence, and the suites the policy maps them to.
export const requiredSuites = (table, policy) => unique(Object.entries(policy.workPackages)
  .filter(([id]) => table.rows.some(row => row.workPackage === id && row.status !== NO_OP)).flatMap(([, suites]) => suites));

export function assessAcceptance({ root = repository, ledgerPath, ledgerSha256, coveragePath, coverageSha256, policy }) {
  root = resolve(root);
  policy ??= readPolicy();
  const problems = [], require = (condition, message) => { if (!condition) problems.push(message); };
  const checked = (path, expected, label) => {
    const full = inside(root, path), bytes = readFileSync(full);
    if (!/^[a-f0-9]{64}$/.test(expected ?? '') || digest(bytes) !== expected) throw new Error(`${label} SHA-256 does not match the explicitly supplied value`);
    return { path: portable(root, full), sha256: expected, value: JSON.parse(bytes.toString()) };
  };
  const ledger = checked(ledgerPath, ledgerSha256, 'Validation ledger');
  const coverage = checked(coveragePath, coverageSha256, 'Coverage');
  if (policy.ledger) require(ledger.path === policy.ledger, 'use the immutable source-only ledger snapshot');
  if (policy.coverage) require(coverage.path === policy.coverage, 'use the immutable source-only coverage snapshot');
  require(ledger.value.sourceValidationOnly === true, 'ledger must explicitly declare source-only validation');
  require(ledger.value.packageValidation === 'pending-not-asserted' || ledger.value.packageValidation?.status === 'pending', 'source snapshot must identify packaging as a separate pending/not-asserted stage');
  require(coverage.value.sourceValidationOnly === true || !policy.coverage, 'coverage must be the frozen source-only snapshot');
  const table = json(inside(root, SYNC.table));
  require(table.to?.commit === policy.sourceCommit, 'applicability target differs from policy');
  require(!table.acceptance, 'the applicability table already carries an acceptance record');
  require(!existsSync(resolve(root, SYNC.record)), `${SYNC.record} already exists; acceptance is recorded once`);
  require(Array.isArray(ledger.value.pendingSuites) && ledger.value.pendingSuites.length === 0, 'validation ledger still has pending suites');
  require(Array.isArray(ledger.value.additionalPending) && ledger.value.additionalPending.length === 0, 'validation ledger still has additional pending work');
  require(!/progress|pending/i.test(ledger.value.status ?? ''), 'validation ledger is not finalized');
  require(coverage.value.complete === true, 'expected-case coverage is incomplete');
  for (const id of Object.keys(policy.workPackages)) require(id in table.workPackages, `${id}: the policy names a work package the table does not have`);

  // What the reports state about each applicable row decides whether it can be accepted at all, and what it is limited by.
  const blocking = new Set(policy.blockingFindings ?? []), rowLimitations = {};
  for (const row of table.rows.filter(row => row.status !== NO_OP)) {
    const review = row.review ?? {}, path = row.upstream[0];
    if (review.pendingParts?.length) problems.push(`${row.id} ${path}: parts are pending in ${review.pendingParts.join(', ')}; record them in sync-1.6.0-dispositions.mjs and regenerate the table once they land`);
    if (blocking.has(review.finding)) problems.push(`${row.id} ${path}: reviewed finding ${review.finding} cannot be accepted; record the landed port (or a reviewed no-op) in sync-1.6.0-dispositions.mjs and regenerate the table`);
    if (review.deferred) rowLimitations[row.id] = [`${path}: ${review.deferred}`];
  }

  const needed = requiredSuites(table, policy), suites = {};
  for (const name of needed) {
    const matches = (ledger.value.latest ?? []).filter(entry => suiteName(entry.project) === name);
    if (matches.length !== 1) { problems.push(`${name}: expected exactly one accepted latest full-suite record, found ${matches.length}`); continue; }
    suites[name] = assessSuite(root, matches[0]);
    problems.push(...suites[name].problems.map(problem => `${name}: ${problem}`));
  }
  for (const group of coverage.value.groups ?? []) {
    const name = suiteName(group.project), suite = suites[name];
    if (!suite) { problems.push(`${group.group}: coverage has no required accepted suite`); continue; }
    require(group.complete === true && group.missingIds?.length === 0 && group.failedOrSkipped?.length === 0, `${group.group}: incomplete expected cases`);
    require(group.expectedCount === group.expectedIds?.length && group.selectedExpected === group.expectedCount, `${group.group}: expected-case counts differ`);
    if (group.receipt) require(portable(root, group.receipt) === suite.receipt, `${group.group}: coverage names a superseded receipt`);
    for (const id of group.expectedIds ?? []) require(suite.passedIds.includes(id), `${group.group}: ${id} is not a passed case in the accepted receipt`);
  }
  const workPackages = {};
  for (const id of Object.keys(table.workPackages)) {
    const rows = table.rows.filter(row => row.workPackage === id && row.status !== NO_OP);
    if (!rows.length) { workPackages[id] = { status: NO_OP, suites: [] }; continue; }
    const required = policy.workPackages[id];
    if (!required?.length) { problems.push(`${id}: no reviewed suite mapping`); continue; }
    const evidence = required.map(name => suites[name]).filter(Boolean);
    const limitations = [...evidence.flatMap(item => item.limitations.map(detail => `${item.suite}: ${detail}`)),
      ...rows.flatMap(row => rowLimitations[row.id] ?? [])];
    workPackages[id] = { status: limitations.length ? LIMITED : TESTED, suites: required, limitations };
  }
  let catalog;
  if (policy.catalog) try {
    const provenancePath = inside(root, policy.catalog.provenance), provenance = json(provenancePath);
    const manifestPath = inside(root, `${policy.catalog.directory}/.manifest.json`), manifest = json(manifestPath);
    require(provenance.version === (policy.version ?? SYNC.version) && provenance.sourceCommit === policy.sourceCommit, 'catalog provenance target mismatch');
    require(digest(readFileSync(manifestPath)) === provenance.manifestSha256, 'catalog manifest digest mismatch');
    require(Object.keys(manifest.files).length === provenance.providerCount, 'catalog provider count mismatch');
    for (const [name, hash] of Object.entries(manifest.files)) require(digest(readFileSync(inside(root, `${policy.catalog.directory}/${name}`))) === hash, `catalog shard differs: ${name}`);
    require(existsSync(inside(root, policy.catalog.report)), 'catalog report missing');
    catalog = { provenance: policy.catalog.provenance, provenanceSha256: digest(readFileSync(provenancePath)), report: policy.catalog.report,
      reportSha256: digest(readFileSync(inside(root, policy.catalog.report))), tarball: provenance.tarball, providerCount: provenance.providerCount, entries: provenance.entries };
  } catch (error) { problems.push(`Catalog: ${error.message}`); }
  for (const [path, from] of policy.documentationEdits ?? []) {
    const full = resolve(root, path);
    require(existsSync(full) && readFileSync(full, 'utf8').includes(from), `${path}: a documentation edit's exact text is not there`);
  }
  return { schemaVersion: 1, eligible: problems.length === 0, sync: SYNC.version, scope: policy.scope, source: table.to,
    status: policy.limitations?.length || Object.values(workPackages).some(item => item.status === LIMITED) ? LIMITED : TESTED,
    limitations: policy.limitations ?? [],
    problems: unique(problems), ledger: { path: ledger.path, sha256: ledger.sha256 }, coverage: { path: coverage.path, sha256: coverage.sha256 },
    packageValidation: { status: 'pending-not-asserted', record: policy.packageValidationRecord ?? `AI_DOCS/sync-${SYNC.version}-packaging.json`, note: 'Packaging is a later, separately recorded stage. This source acceptance does not claim package or install validation.' },
    applicabilityInput: { path: SYNC.table, sha256: digest(readFileSync(inside(root, SYNC.table))) },
    policySha256: digest(JSON.stringify(policy)), catalog, documentationEdits: policy.documentationEdits ?? [], workPackages, rowLimitations,
    suites: Object.fromEntries(Object.entries(suites).map(([name, { passedIds, problems, ...value }]) => [name, value])) };
}

export function applyAcceptance(root, plan) {
  root = resolve(root);
  if (!plan.eligible) throw new Error('Acceptance is blocked; no metadata will be changed');
  for (const input of [plan.ledger, plan.coverage, plan.applicabilityInput])
    if (digest(readFileSync(inside(root, input.path))) !== input.sha256) throw new Error(`Acceptance input changed after review: ${input.path}`);
  for (const suite of Object.values(plan.suites)) {
    const receiptPath = inside(root, suite.receipt);
    if (digest(readFileSync(receiptPath)) !== suite.receiptSha256) throw new Error(`Accepted receipt changed after review: ${suite.receipt}`);
    const receipt = json(receiptPath);
    const checked = assessSuite(root, { project: receipt.project, receipt: suite.receipt, accepted: true, fullSuite: true,
      stableDuringRun: true, currentSourceMatches: true, result: 'passed', selectedCount: suite.selectedCount, failures: [] });
    if (!checked.eligible) throw new Error(`Accepted source changed after review: ${suite.suite}: ${checked.problems.join('; ')}`);
  }
  if (plan.catalog) {
    if (digest(readFileSync(inside(root, plan.catalog.provenance))) !== plan.catalog.provenanceSha256 ||
        digest(readFileSync(inside(root, plan.catalog.report))) !== plan.catalog.reportSha256) throw new Error('Catalog provenance/report changed after review');
    const provenance = json(inside(root, plan.catalog.provenance));
    const directory = plan.catalog.provenance.replace(/\.provenance\.json$/, '');
    const manifestPath = inside(root, `${directory}/.manifest.json`), manifest = json(manifestPath);
    if (digest(readFileSync(manifestPath)) !== provenance.manifestSha256) throw new Error('Catalog manifest changed after review');
    for (const [name, hash] of Object.entries(manifest.files)) if (digest(readFileSync(inside(root, `${directory}/${name}`))) !== hash) throw new Error(`Catalog shard changed after review: ${name}`);
  }
  const recordPath = SYNC.record;
  if (existsSync(resolve(root, recordPath))) throw new Error(`${recordPath} already exists; acceptance is recorded once`);
  const read = path => json(inside(root, path));
  const changes = new Map();
  const writeJson = (path, value) => changes.set(path, JSON.stringify(value, null, 2) + '\n');

  const table = read(SYNC.table);
  table.acceptance = { record: recordPath, ledger: plan.ledger, coverage: plan.coverage, catalog: plan.catalog, scope: plan.scope };
  table.statusMeaning[TESTED] = 'Current-source local full-suite and required-case evidence accepted; no broader parity or publication claim.';
  table.statusMeaning[LIMITED] = 'Current-source local full-suite evidence accepted with the recorded skips, deferrals or platform limitations; not an unqualified pass.';
  for (const row of table.rows) if (row.status !== NO_OP) {
    const own = plan.rowLimitations?.[row.id];
    row.status = own ? LIMITED : plan.workPackages[row.workPackage].status;
    row.validation = { record: recordPath, workPackage: row.workPackage, ...(own ? { limitations: own } : {}) };
  }
  for (const [id, value] of Object.entries(table.workPackages)) Object.assign(value, { status: plan.workPackages[id].status, validation: { record: recordPath, workPackage: id } });
  table.summary.byStatus = table.rows.reduce((out, row) => (out[row.status] = (out[row.status] ?? 0) + 1, out), {});
  writeJson(SYNC.table, table);

  const overlay = read(SYNC.overlay), byId = new Map(table.rows.map(row => [row.id, row]));
  overlay.status = plan.status; overlay.acceptance = recordPath;
  for (const row of overlay.rows) { const current = byId.get(row.id); if (!current) throw new Error(`${SYNC.overlay}: ${row.id} is not a table row`); row.status = current.status; if (current.validation) row.validation = current.validation; }
  writeJson(SYNC.overlay, overlay);

  // Only the candidate pointer moves; the map's rows stay the preserved v1.5.0 evidence.
  const map = read(SYNC.map);
  if (map.currentImplementation?.candidate?.source?.commit !== plan.source.commit) throw new Error(`${SYNC.map}: the candidate pointer names another source`);
  Object.assign(map.currentImplementation.candidate, { status: plan.status, acceptance: recordPath });
  writeJson(SYNC.map, map);

  const state = read(SYNC.implementationStatus), candidate = state[SYNC.candidateKey];
  if (!candidate || candidate.sourceCommit !== plan.source.commit) throw new Error(`${SYNC.implementationStatus}: ${SYNC.candidateKey} is missing or names another source`);
  Object.assign(candidate, { status: plan.status, acceptance: recordPath, runtimeAcceptance: plan.scope });
  writeJson(SYNC.implementationStatus, state);

  const target = read(SYNC.target);
  target.status = `Source-level re-freeze per ${SYNC.decision}; ${plan.status} local source evidence in ${recordPath}; unreleased`;
  writeJson(SYNC.target, target);

  for (const [path, from, to] of plan.documentationEdits ?? []) {
    const text = changes.get(path) ?? readFileSync(inside(root, path), 'utf8');
    if (!text.includes(from)) throw new Error(`Review changed documentation before applying acceptance: ${path}`);
    changes.set(path, text.replace(from, to));
  }
  writeJson(recordPath, { ...plan, appliedAt: new Date().toISOString() });
  // All evidence and documentation preconditions are checked before the first write.
  for (const suite of Object.values(plan.suites)) {
    const receipt = read(suite.receipt), manifest = read(receipt.source.manifest);
    for (const file of manifest.files) {
      const path = portable(root, file.path);
      if (changes.has(path) && digest(Buffer.from(changes.get(path))) !== file.sha256)
        throw new Error(`Metadata transition would invalidate an accepted runtime input: ${path}. Freeze it before source validation.`);
    }
  }
  for (const [path, value] of changes) writeFileSync(inside(root, path), value);
  return [...changes.keys()];
}

export function runAcceptanceCli(argv = process.argv.slice(2)) {
  const args = {};
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === '--apply') args.apply = true;
    else if (name.startsWith('--') && argv[index + 1] && !argv[index + 1].startsWith('--')) args[name.slice(2)] = argv[++index];
    else throw new Error(`Invalid argument ${name}`);
  }
  const plan = assessAcceptance({ ledgerPath: args['accepted-ledger'] ?? args.ledger, ledgerSha256: args['ledger-sha256'], coveragePath: args.coverage,
    coverageSha256: args['coverage-sha256'], policy: args.policy ? json(inside(repository, args.policy)) : undefined });
  if (args.out) writeFileSync(inside(repository, args.out), JSON.stringify(plan, null, 2) + '\n');
  const changed = args.apply ? applyAcceptance(repository, plan) : [];
  console.log(JSON.stringify({ eligible: plan.eligible, mode: args.apply ? 'applied' : 'review-only', proposedStatus: plan.status, problems: plan.problems, changed }, null, 2));
  if (!plan.eligible) process.exitCode = 1;
  return plan;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) runAcceptanceCli();
