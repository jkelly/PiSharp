// Seal the tested-with-limitations source decision for the 1.7.0 port. Package smoke,
// NuGet publication and interoperability outside decision 0018 remain separate.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const inventoryPath = 'compatibility/sync-1.7.0-files.json';
const ledgerPath = 'AI_DOCS/sync-1.7.0-source-validation.json';
const coveragePath = 'AI_DOCS/sync-1.7.0-coverage.json';
const recordPath = 'compatibility/sync-1.7.0-acceptance.json';
const tablePath = 'compatibility/sync-1.7.0-applicability.json';
const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const pin = path => ({ path, sha256: createHash('sha256').update(readFileSync(resolve(root, path))).digest('hex') });
const [mode, ledgerDigest, coverageDigest] = process.argv.slice(2);
assert.ok(mode === '--check' || mode === '--apply', 'Use --check, or --apply <reviewed-ledger-sha256> <reviewed-coverage-sha256>.');
const ledger = read(ledgerPath);
const coverage = read(coveragePath);
const table = read(tablePath);
const inventory = read(inventoryPath);
const source = inventory.to;
assert.equal(source.commit, 'fb0fe823539f6d04850f6e34748c01f01f212e1f');
assert.equal(source.tag, 'v1.7.0');
assert.deepEqual(table.to, source);
assert.deepEqual(coverage.source, source);
assert.deepEqual(coverage.inventory, pin(inventoryPath));
assert.deepEqual(table.acceptance, { record: recordPath, ledger: pin(ledgerPath), coverage: pin(coveragePath) });
assert.equal(ledger.upstreamCommit, source.commit);
assert.equal(ledger.target, source.tag);
assert.equal(ledger.sourceValidationOnly, true);
assert.equal(ledger.packageValidation.status, 'pending');
assert.deepEqual(ledger.pendingSuites, []);
assert.deepEqual(ledger.additionalPending, []);
assert.ok(ledger.limitations.length > 0);
assert.ok(coverage.complete && coverage.sourceValidationOnly);
assert.equal(table.rows.length, inventory.summary.files);
assert.equal(table.rows.length, 1683);

const suites = new Set();
for (const report of ledger.reports) {
  assert.match(report.reportSha256, /^[0-9a-f]{64}$/);
  assert.ok(!suites.has(report.suite), 'Duplicate suite: ' + report.suite);
  suites.add(report.suite);
  assert.ok(report.passedIds.length > 0, 'No passes: ' + report.suite);
  assert.ok(report.result === 'passed' || report.result === 'passed-with-skips');
  assert.equal(report.cases, report.passedIds.length + report.skipped.length);
  assert.equal(new Set([...report.passedIds,...report.skipped]).size, report.cases, 'Nonunique case IDs: ' + report.suite);
}
assert.deepEqual(ledger.requiredSuites, [...suites].sort());
const groups = new Map(coverage.groups.map(g => [g.id, g]));
const counts = new Map();
for (const [i, row] of table.rows.entries()) {
  const file = inventory.files[i];
  assert.deepEqual(row.upstream, [...new Set([file.before?.path, file.after?.path].filter(Boolean))]);
  assert.equal(row.change, file.status);
  if (row.status === 'no-op') {
    assert.ok(row.category && row.reason, 'Undispositioned row: ' + row.id);
  } else {
    assert.equal(row.status, 'tested-with-limitations');
    assert.ok(groups.has(row.workPackage), 'Unknown evidence group for ' + row.id);
    counts.set(row.workPackage, (counts.get(row.workPackage) ?? 0) + 1);
  }
}
for (const [id, group] of groups) {
  assert.equal(counts.get(id), group.rowCount);
  assert.ok(group.limitation && group.suites.length && group.nativePaths.length, 'Incomplete evidence group: ' + id);
  for (const suite of group.suites) assert.ok(suites.has(suite), 'Missing suite: ' + suite);
}
// Also reproduce the independently authored case-to-group coverage and row classification.
execFileSync(process.execPath, ['tools/CompatibilityReport/sync-1.7.0-applicability.mjs', '--check'], { cwd: root });

const existed = existsSync(resolve(root, recordPath));
if (mode === '--apply') {
  assert.equal(ledgerDigest, pin(ledgerPath).sha256, 'Explicit ledger digest differs');
  assert.equal(coverageDigest, pin(coveragePath).sha256, 'Explicit coverage digest differs');
  assert.ok(!existed, 'A source acceptance is already frozen; review it with --check.');
  assert.equal(execFileSync('git', ['rev-parse','HEAD:src'], {cwd:root,encoding:'utf8'}).trim(), ledger.localBuild.nativeSourceTree,
    'Native source tree has changed since execution');
}
const appliedAt = mode === '--apply' ? new Date().toISOString() : read(recordPath).appliedAt;
assert.equal(new Date(appliedAt).toISOString(), appliedAt);
const record = {
  schemaVersion: 1, sync: '1.7.0', source, appliedAt,
  eligible: true, status: 'tested-with-limitations', problems: [],
  scope: coverage.scope, decision: table.decision,
  ledger: pin(ledgerPath), coverage: pin(coveragePath), applicabilityInput: pin(tablePath),
  suiteCount: suites.size, coverageGroups: groups.size, fileDispositions: coverage.summary,
  limitations: ledger.limitations, packageValidation: ledger.packageValidation
};
if (mode === '--apply') writeFileSync(resolve(root, recordPath), JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
else assert.deepEqual(record, read(recordPath));
console.log('Verified 1.7.0 source evidence for ' + groups.size + ' native groups and ' + table.rows.length + ' upstream file dispositions.');
