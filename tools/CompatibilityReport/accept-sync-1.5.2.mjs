#!/usr/bin/env node
// Explicit final metadata transition. Without --apply this only writes/prints a review plan.
import { readFileSync, writeFileSync, existsSync, readdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, relative, dirname, isAbsolute } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const digest = value => createHash('sha256').update(value).digest('hex');
const json = path => JSON.parse(readFileSync(path, 'utf8'));
const inside = (root, path) => {
  const full = resolve(root, path), rel = relative(root, full);
  if (!rel || rel === '..' || rel.startsWith('../') || rel.startsWith('..\\') || isAbsolute(rel)) throw new Error(`Path is outside the source repository: ${path}`);
  return full;
};
const suiteName = project => {
  const filename = project.replace(/\\/g, '/').split('/').at(-1);
  return filename === 'PiSharp.Extensions.ContractTests.csproj' ? 'Extensions.ContractTests'
    : /^PiSharp\.(.+)\.Tests\.csproj$/.exec(filename)?.[1];
};
const suiteProject = name => name === 'Extensions.ContractTests'
  ? 'tests/PiSharp.Extensions.ContractTests/PiSharp.Extensions.ContractTests.csproj'
  : `tests/PiSharp.${name}.Tests/PiSharp.${name}.Tests.csproj`;
const portable = (root, path) => relative(root, inside(root, path)).replace(/\\/g, '/');
const unique = values => [...new Set(values)];

export function assessSuite(root, entry) {
  const problems = [], require = (condition, message) => { if (!condition) problems.push(message); };
  require(entry.accepted === true, 'not accepted by the validation owner');
  require(entry.fullSuite === true, 'not a full-suite selection');
  require(entry.stableDuringRun === true, 'source changed during the run');
  require(entry.currentSourceMatches === true, 'final current-source qualification is missing or false');
  require(/^passed(?:-with-console-skips|-parser-recovered)?$/.test(entry.result), `result is ${entry.result}`);
  require(Number.isInteger(entry.selectedCount) && entry.selectedCount > 0, 'no positive observed case count');
  require((entry.failures ?? []).length === 0, 'failed cases remain');
  let receipt, receiptPath, manifest;
  try {
    receiptPath = inside(root, entry.receipt);
    receipt = json(receiptPath);
    const tests = receipt.stages?.find(stage => stage.name === 'tests');
    require(tests?.exitCode === 0, 'raw test process did not exit successfully');
    require((receipt.stages ?? []).every(stage => stage.exitCode === 0), 'a raw stage did not exit successfully');
    require(receipt.source?.changedDuringRun === false, 'raw receipt has changed source');
    require(receipt.source?.beforeSha256 === receipt.source?.afterSha256, 'before/after source fingerprints differ');
    require(portable(root, receipt.project) === portable(root, entry.project), 'receipt names another project path');
    require(portable(root, entry.project) === suiteProject(suiteName(entry.project)), 'ledger project is not the canonical suite path');
    const manifestPath = inside(root, receipt.source.manifest);
    manifest = json(manifestPath);
    require(digest(JSON.stringify(manifest.files)) === manifest.sha256, 'source manifest canonical digest mismatch');
    require(manifest.sha256 === receipt.source.beforeSha256, 'receipt does not name this source manifest');
    const recorded = new Set(manifest.files.map(file => file.path.replace(/\\/g, '/')));
    const walk = directory => {
      for (const entry of readdirSync(directory, { withFileTypes: true })) {
        if (entry.isDirectory() && !['bin', 'obj', 'node_modules'].includes(entry.name)) walk(resolve(directory, entry.name));
        else if (entry.isFile()) {
          const path = portable(root, resolve(directory, entry.name));
          require(recorded.has(path), `new source file absent from accepted manifest: ${path}`);
        }
      }
    };
    for (const directory of unique(manifest.files.filter(file => file.path.endsWith('.csproj')).map(file => dirname(inside(root, file.path))))) walk(directory);
    for (const name of ['Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config'])
      if (existsSync(resolve(root, name))) require(recorded.has(name), `new build policy absent from accepted manifest: ${name}`);
    if (existsSync(resolve(root, 'tools/GapValidation'))) for (const entry of readdirSync(resolve(root, 'tools/GapValidation'), { withFileTypes: true }))
      if (entry.isFile()) require(recorded.has(`tools/GapValidation/${entry.name}`), `new validation-owner file absent from accepted manifest: ${entry.name}`);
    for (const file of manifest.files) {
      const path = inside(root, file.path);
      require(existsSync(path) && digest(readFileSync(path)) === file.sha256, `current source differs: ${file.path}`);
    }
  } catch (error) { problems.push(error.message); }
  const skips = [...(entry.skippedCases ?? []), ...(receipt?.caseEvidence?.cases ?? []).filter(item => /^SKIP/.test(item.status ?? ''))];
  const limitations = unique([...skips.map(item => `${item.id ?? '(unnamed)'}: ${item.reason ?? item.status ?? 'skipped'}`),
    ...(entry.consoleSkips ?? []), ...(receipt?.caseEvidence?.consoleSkipObservations ?? [])]);
  return {
    suite: suiteName(entry.project), eligible: problems.length === 0, problems,
    status: limitations.length ? 'tested-with-limitations' : 'tested', limitations,
    ...(receiptPath ? { receipt: portable(root, receiptPath), receiptSha256: digest(readFileSync(receiptPath)) } : {}),
    sourceFingerprint: manifest?.sha256, platform: receipt?.platform, selectedCount: entry.selectedCount,
    evidenceKind: receipt?.caseEvidence?.kind, recordedResult: receipt?.result,
    caseStatusCounts: (receipt?.caseEvidence?.cases ?? []).reduce((counts, item) => (counts[item.status] = (counts[item.status] ?? 0) + 1, counts), {}),
    passedIds: (receipt?.caseEvidence?.cases ?? []).filter(item => /^(?:PASS(?:_|$)|passed$)/.test(item.status ?? '')).map(item => item.id),
  };
}

export function assessAcceptance({ root = repository, ledgerPath, ledgerSha256, coveragePath, coverageSha256, policy }) {
  root = resolve(root);
  policy ??= json(resolve(repository, 'tools/CompatibilityReport/sync-1.5.2-acceptance-policy.json'));
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
  const table = json(inside(root, 'compatibility/sync-1.5.2-applicability.json'));
  require(table.to.commit === policy.sourceCommit, 'applicability target differs from policy');
  require(Array.isArray(ledger.value.pendingSuites) && ledger.value.pendingSuites.length === 0, 'validation ledger still has pending suites');
  require(Array.isArray(ledger.value.additionalPending) && ledger.value.additionalPending.length === 0, 'validation ledger still has additional pending work');
  require(!/progress|pending/i.test(ledger.value.status ?? ''), 'validation ledger is not finalized');
  require(coverage.value.complete === true, 'expected-case coverage is incomplete');
  const needed = unique(Object.entries(policy.workPackages).filter(([id]) => table.rows.some(row => row.workPackage === id && row.status !== 'no-op')).flatMap(([, suites]) => suites));
  const suites = {};
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
    if (!table.rows.some(row => row.workPackage === id && row.status !== 'no-op')) { workPackages[id] = { status: 'no-op', suites: [] }; continue; }
    const required = policy.workPackages[id];
    if (!required?.length) { problems.push(`${id}: no reviewed suite mapping`); continue; }
    const evidence = required.map(name => suites[name]).filter(Boolean);
    workPackages[id] = { status: evidence.some(item => item.status === 'tested-with-limitations') ? 'tested-with-limitations' : 'tested', suites: required,
      limitations: evidence.flatMap(item => item.limitations.map(detail => `${item.suite}: ${detail}`)) };
  }
  let catalog;
  if (policy.catalog) try {
    const provenancePath = inside(root, policy.catalog.provenance), provenance = json(provenancePath);
    const manifestPath = inside(root, `${policy.catalog.directory}/.manifest.json`), manifest = json(manifestPath);
    require(provenance.version === '1.5.2' && provenance.sourceCommit === policy.sourceCommit, 'catalog provenance target mismatch');
    require(digest(readFileSync(manifestPath)) === provenance.manifestSha256, 'catalog manifest digest mismatch');
    require(Object.keys(manifest.files).length === provenance.providerCount, 'catalog provider count mismatch');
    for (const [name, hash] of Object.entries(manifest.files)) require(digest(readFileSync(inside(root, `${policy.catalog.directory}/${name}`))) === hash, `catalog shard differs: ${name}`);
    require(existsSync(inside(root, policy.catalog.report)), 'catalog report missing');
    catalog = { provenance: policy.catalog.provenance, provenanceSha256: digest(readFileSync(provenancePath)), report: policy.catalog.report,
      reportSha256: digest(readFileSync(inside(root, policy.catalog.report))), tarball: provenance.tarball, providerCount: provenance.providerCount, entries: provenance.entries };
  } catch (error) { problems.push(`Catalog: ${error.message}`); }
  return { schemaVersion: 1, eligible: problems.length === 0, scope: policy.scope, source: table.to,
    status: policy.limitations?.length || Object.values(workPackages).some(item => item.status === 'tested-with-limitations') ? 'tested-with-limitations' : 'tested',
    limitations: policy.limitations ?? [],
    problems: unique(problems), ledger: { path: ledger.path, sha256: ledger.sha256 }, coverage: { path: coverage.path, sha256: coverage.sha256 },
    packageValidation: { status: 'pending-not-asserted', record: 'AI_DOCS/sync-1.5.2-packaging.json', note: 'Packaging is a later, separately recorded stage. This source acceptance does not claim package or install validation.' },
    applicabilityInput: { path: 'compatibility/sync-1.5.2-applicability.json', sha256: digest(readFileSync(inside(root, 'compatibility/sync-1.5.2-applicability.json'))) },
    policySha256: digest(JSON.stringify(policy)), catalog, workPackages,
    suites: Object.fromEntries(Object.entries(suites).map(([name, { passedIds, problems, ...value }]) => [name, value])) };
}

export function applyAcceptance(root, plan) {
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
  const recordPath = 'compatibility/sync-1.5.2-acceptance.json';
  const read = path => json(inside(root, path));
  const changes = new Map();
  const writeJson = (path, value) => changes.set(path, JSON.stringify(value, null, 2) + '\n');
  const table = read('compatibility/sync-1.5.2-applicability.json');
  table.acceptance = { record: recordPath, ledger: plan.ledger, coverage: plan.coverage, catalog: plan.catalog, scope: plan.scope };
  table.statusMeaning.tested = 'Current-source local full-suite and required-case evidence accepted; no broader parity or publication claim.';
  table.statusMeaning['tested-with-limitations'] = 'Current-source local full-suite evidence accepted with the recorded skips/platform limitations; not an unqualified pass.';
  for (const row of table.rows) if (row.status !== 'no-op') { row.status = plan.workPackages[row.workPackage].status; row.validation = { record: recordPath, workPackage: row.workPackage }; }
  for (const [id, value] of Object.entries(table.workPackages)) Object.assign(value, { status: plan.workPackages[id].status, validation: { record: recordPath, workPackage: id } });
  table.summary.byStatus = table.rows.reduce((out, row) => (out[row.status] = (out[row.status] ?? 0) + 1, out), {});
  writeJson('compatibility/sync-1.5.2-applicability.json', table);
  const overlay = read('compatibility/durable-sync-1.5.2-overlay.json'), byId = new Map(table.rows.map(row => [row.id, row]));
  overlay.status = plan.status; overlay.acceptance = recordPath;
  for (const row of overlay.rows) { const current = byId.get(row.id); row.status = current.status; if (current.validation) row.validation = current.validation; }
  writeJson('compatibility/durable-sync-1.5.2-overlay.json', overlay);
  const map = read('compatibility/durable-port-map.json'); map.currentImplementation.candidate.status = plan.status; map.currentImplementation.candidate.acceptance = recordPath;
  writeJson('compatibility/durable-port-map.json', map);
  const state = read('compatibility/implementation-status.json'); Object.assign(state.pi152Candidate, { status: plan.status, acceptance: recordPath, runtimeAcceptance: plan.scope });
  writeJson('compatibility/implementation-status.json', state);
  const parity = read('website/src/data/parity.json'); Object.assign(parity.sourceCandidate, { status: plan.status, acceptance: `https://github.com/jkelly/PiSharp/blob/main/${recordPath}` });
  writeJson('website/src/data/parity.json', parity);
  const target = read('compatibility/target.lock.json'); target.status = `Source-level re-freeze per docs/decisions/0014-pi-1.5.2-sync.md; ${plan.status} local source evidence in ${recordPath}; unreleased`;
  writeJson('compatibility/target.lock.json', target);
  const link = '[accepted source validation](compatibility/sync-1.5.2-acceptance.json)';
  const edits = [
    ['README.md', 'It is unreleased and integrated validation is pending.', `It is unreleased; ${link} records the local results and their limitations.`],
    ['IMPLEMENTATION_STATUS.md', 'Applicable candidate work is **implementation-pending-validation**;', `Applicable candidate work is **${plan.status}** in local source validation;`],
    ['website/src/pages/index.astro', 'integrated validation is pending.', 'local source validation is recorded with explicit limitations.'],
    ['website/src/pages/parity.astro', 'Applicable work is pending integrated validation; a source pin does not establish parity.', 'Local source validation is recorded with explicit suite and platform limitations; it does not establish complete parity.'],
    ['website/src/content/docs/docs/index.md', 'Its integrated validation is pending;', 'Its local source validation is recorded with explicit limitations;'],
    ['website/src/content/docs/docs/extensions/node-bridge.md', 'Integrated candidate validation is pending.', 'Local candidate source validation is recorded with explicit suite and platform limitations.'],
    ['website/src/content/docs/docs/guides/durable.md', 'Applicable work remains pending integrated validation.', 'Local source validation is recorded with explicit suite and platform limitations.'],
    ['website/src/content/docs/docs/versioning.md', 'integrated validation remains pending', 'local source validation is recorded with explicit limitations'],
  ];
  for (const [path, from, to] of edits) {
    const text = readFileSync(inside(root, path), 'utf8');
    if (!text.includes(from)) throw new Error(`Review changed documentation before applying acceptance: ${path}`);
    changes.set(path, text.replace(from, to));
  }
  const notesPath = 'docs/release-notes/1.5.2.md';
  let notes = readFileSync(inside(root, notesPath), 'utf8');
  notes = notes.replace('These areas contain new and previously implemented behavior; each applicable file remains marked `implementation-pending-validation` until integrated evidence supports a final decision. Area reports identify authored regressions and any remaining integration work.',
    'These areas contain new and previously implemented behavior. The applicability map records accepted local source tests as `tested` or `tested-with-limitations`; no-op dispositions and historical evidence remain separate.');
  if (!notes.includes('## Validation status')) throw new Error('Release notes validation section is missing');
  notes = notes.slice(0, notes.indexOf('## Validation status')) + `## Validation status\n\n**${plan.status}** for the local source scope in [the acceptance record](../../${recordPath}). Exact suite receipts, current-source fingerprints, required cases, skips and platform limitations are recorded there. No hosted, cross-platform, live-provider or publication acceptance is implied. The candidate remains unreleased.\n`;
  changes.set(notesPath, notes);
  const workplanPath = 'AI_DOCS/sync-1.5.2.md', workplan = readFileSync(inside(root, workplanPath), 'utf8');
  const evidenceParagraph = /(## Validation evidence\r?\n\r?\n)[^\r\n]+/;
  if (!evidenceParagraph.test(workplan)) throw new Error('Work plan current validation-evidence paragraph is missing');
  const currentEvidence = `Accepted current-source local evidence is frozen in [the source ledger](../${plan.ledger.path}) and [expected-case coverage](../${plan.coverage.path}), with [scope and limitations](../${recordPath}). The mutable [run ledger](sync-1.5.2-validation.json) preserves superseded failures and reruns; exact raw receipts remain under ignored artifacts/gap-validation/. The [separate packaging record](../${plan.packageValidation.record}) records the later package stage; source acceptance does not assert package validation.`;
  const updatedWorkplan = workplan.replace(evidenceParagraph, (_, heading) => heading + currentEvidence);
  const finalizedWorkplan = updatedWorkplan.split('\n').map(line => /^\| (?!D-)/.test(line)
    ? line.replace(/\|\s*(?:running|pending)\s*\|\s*$/, `| ${plan.status} |`) : line).join('\n');
  changes.set(workplanPath, `${finalizedWorkplan.trimEnd()}\n\n## Final local source validation\n\nStatus: **${plan.status}**, as recorded in [the acceptance record](../${recordPath}). This records only the accepted current-source suite scope and explicit limitations; the candidate remains unreleased.\n`);
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
  const plan = assessAcceptance({ ledgerPath: args['accepted-ledger'] ?? args.ledger, ledgerSha256: args['ledger-sha256'], coveragePath: args.coverage, coverageSha256: args['coverage-sha256'] });
  if (args.out) writeFileSync(inside(repository, args.out), JSON.stringify(plan, null, 2) + '\n');
  const changed = args.apply ? applyAcceptance(repository, plan) : [];
  console.log(JSON.stringify({ eligible: plan.eligible, mode: args.apply ? 'applied' : 'review-only', proposedStatus: plan.status, problems: plan.problems, changed }, null, 2));
  if (!plan.eligible) process.exitCode = 1;
  return plan;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) runAcceptanceCli();
