// The v1.5.2..v1.6.0 sync's applicability table, its Durable overlay and its Node fixture.
// Work packages report on their own branches, so a pending or no-op row may name a report that is not merged yet; a row
// root has accepted must have its report. The meta report, which owns this table, is always required.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';
import { defineApplicabilityChecks } from './sync-applicability-checks.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const tablePath = 'compatibility/sync-1.6.0-applicability.json';

defineApplicabilityChecks({
  tablePath,
  overlayPath: 'compatibility/durable-sync-1.6.0-overlay.json',
  preservedMapPath: 'compatibility/durable-port-map.v1.5.0.json',
  preservedMapSha256: '45fb24e89fb9d0c63ccc2fd8c05dd6e66f3b63fad0d7500f2964303380690ee2',
  fixtureRoot: 'tools/NodeBridge/runtime-fixture-160',
  reportsMayBePending: true,
  requiredReports: ['AI_DOCS/agent-reports/sync-1.6.0-meta.md'],
});

test('the 1.6.0 table names only the work plan\'s packages and the plan\'s no-op categories', () => {
  const table = JSON.parse(readFileSync(resolve(repo, tablePath), 'utf8'));
  const plan = readFileSync(resolve(repo, 'AI_DOCS/sync-1.6.0.md'), 'utf8');
  // packages/chord is one package, or the chord-* packages chord-discovery sets; its manifest is a version pin, which meta owns.
  const chord = /^chord(?:-[a-z0-9-]+)?$/;
  for (const id of Object.keys(table.workPackages)) assert(plan.includes(`| ${id} |`) || chord.test(id), id);
  for (const row of table.rows) {
    if (row.upstream[0].startsWith('apps/')) assert.deepEqual([row.status, row.category], ['no-op', 'upstream-app'], row.id);
    if (row.upstream[0].startsWith('packages/chord/')) assert(chord.test(row.workPackage) || row.upstream[0] === 'packages/chord/package.json' && row.workPackage === 'meta', row.id);
  }
});

// While nothing is accepted, the committed table, overlay and candidate pointer are exactly what the generator writes.
test('the pending 1.6.0 snapshot reproduces from its generator', () => {
  const table = JSON.parse(readFileSync(resolve(repo, tablePath), 'utf8'));
  if (table.acceptance || table.rows.some(row => !['implementation-pending-validation', 'no-op'].includes(row.status))) return;
  execFileSync(process.execPath, [resolve(repo, 'tools/CompatibilityReport/sync-1.6.0-applicability.mjs'), '--check'], { cwd: repo, stdio: 'pipe' });
});
