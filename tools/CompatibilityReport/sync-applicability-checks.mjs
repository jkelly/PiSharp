// Shared applicability-table, Durable-overlay and Node-fixture checks for a sync. Each sync's
// sync-<version>-applicability.test.mjs names its own table, overlay and fixture, so a later sync never changes what an
// earlier sync's test reads.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const read = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const sha256 = path => createHash('sha256').update(readFileSync(resolve(repo, path))).digest('hex');

// tablePath: the sync's applicability table. overlayPath: its Durable overlay; preservedMapPath/preservedMapSha256: the
// historical row-level Durable map both stay beside. fixtureRoot: the sync's test-only Node runtime fixture.
// reportsMayBePending: a row whose work package has not reported yet may name a report that is not written; a row with an
// accepted status never may. The 1.5.2 table is fully accepted, so it leaves this off.
export function defineApplicabilityChecks({ tablePath, overlayPath, preservedMapPath, preservedMapSha256, fixtureRoot, reportsMayBePending = false, requiredReports = [] }) {
  const table = read(tablePath), inventory = read(table.inventory), version = table.to.tag.slice(1);
  const count = key => table.rows.reduce((out, row) => (out[row[key]] = (out[row[key]] ?? 0) + 1, out), {});

  test(`${version} applicability covers every frozen changed file exactly once`, () => {
    assert.deepEqual(table.from, inventory.from);
    assert.deepEqual(table.to, inventory.to);
    assert.equal(table.rows.length, inventory.files.length);
    assert.equal(new Set(table.rows.map(row => row.id)).size, table.rows.length);
    assert.deepEqual(table.summary.byStatus, count('status'));
    assert.deepEqual(table.summary.byWorkPackage, count('workPackage'));
    assert.equal(table.summary.files, table.rows.length);
    assert(existsSync(resolve(repo, table.decision)), table.decision);
    for (const path of requiredReports) assert(existsSync(resolve(repo, path)), path);
    for (let index = 0; index < table.rows.length; index++) {
      const row = table.rows[index], source = inventory.files[index];
      assert.deepEqual(row.source, { before: source.before, after: source.after }, row.id);
      assert.deepEqual(row.upstream, [(source.after ?? source.before).path], row.id);
      assert.equal(row.change, source.status, row.id);
      assert(row.status in table.statusMeaning, row.id);
      assert(row.reason?.length > 0, row.id);
      assert(table.workPackages[row.workPackage], row.id);
      assert(row.reports.length > 0, row.id);
      const pending = reportsMayBePending && ['implementation-pending-validation', 'no-op'].includes(row.status);
      for (const path of row.reports) {
        assert.match(path, /^AI_DOCS\/agent-reports\/sync-[0-9.]+-[a-z0-9-]+\.md$/, row.id);
        if (!pending) assert(existsSync(resolve(repo, path)), `${row.id}: ${path}`);
      }
      if (row.status === 'no-op') assert(row.category?.length > 0, row.id);
    }
  });

  test(`the ${version} Durable overlay preserves the exact historical map and its rows`, () => {
    const prior = read(preservedMapPath), current = read('compatibility/durable-port-map.json');
    const implementation = current.currentImplementation;
    const pointer = [implementation.candidate, ...(implementation.previousCandidates ?? [])].find(entry => entry.overlay === overlayPath);
    assert(pointer, `the Durable map links ${overlayPath}`);
    const overlay = read(overlayPath);
    assert.equal(overlay.preservedMap, preservedMapPath);
    assert.equal(sha256(preservedMapPath), overlay.preservedMapSha256);
    assert.equal(overlay.preservedMapSha256, preservedMapSha256);
    assert.deepEqual(current.files, prior.files);
    assert.equal(current.upstreamCommit, prior.upstreamCommit);
    assert.deepEqual(overlay.source, table.to);
    assert.deepEqual(pointer.source, table.to);
    for (const row of overlay.rows) {
      const full = table.rows.find(value => value.id === row.id);
      assert(full, row.id);
      for (const key of ['source', 'status', 'workPackage', 'reports']) assert.deepEqual(row[key], full[key], `${row.id}.${key}`);
    }
  });

  test(`the Pi ${version} Node fixture pins the sync's target and remains test-only`, () => {
    const manifest = read(`${fixtureRoot}/package.json`), lock = read(`${fixtureRoot}/package-lock.json`), provenance = read(`${fixtureRoot}/fixture-provenance.json`);
    assert.equal(provenance.runtimeVersion, version);
    assert.equal(provenance.validation.productionCacheAdmission, false);
    for (const section of ['dependencies', 'overrides']) for (const [name, value] of Object.entries(manifest[section]))
      if (name.startsWith('@earendil-works/')) assert.equal(value, version, name);
    assert.deepEqual(lock.packages[''].dependencies, manifest.dependencies);
    for (const [name, field] of [['package.json', 'manifestSha256'], ['package-lock.json', 'lockSha256']])
      assert.equal(sha256(`${fixtureRoot}/${name}`), provenance[field]);
  });
}
