import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const read = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const table = read('compatibility/sync-1.5.2-applicability.json');
const inventory = read(table.inventory);
const count = key => table.rows.reduce((out, row) => (out[row[key]] = (out[row[key]] ?? 0) + 1, out), {});

test('current applicability covers every frozen changed file exactly once', () => {
  assert.deepEqual(table.from, inventory.from);
  assert.deepEqual(table.to, inventory.to);
  assert.equal(table.rows.length, inventory.files.length);
  assert.equal(new Set(table.rows.map(row => row.id)).size, table.rows.length);
  assert.deepEqual(table.summary.byStatus, count('status'));
  assert.deepEqual(table.summary.byWorkPackage, count('workPackage'));
  assert.equal(table.summary.files, table.rows.length);
  for (let index = 0; index < table.rows.length; index++) {
    const row = table.rows[index], source = inventory.files[index];
    assert.deepEqual(row.source, { before: source.before, after: source.after }, row.id);
    assert.deepEqual(row.upstream, [(source.after ?? source.before).path], row.id);
    assert.equal(row.change, source.status, row.id);
    assert(row.status in table.statusMeaning, row.id);
    assert(row.reason?.length > 0, row.id);
    assert(table.workPackages[row.workPackage], row.id);
    assert(row.reports.length > 0, row.id);
    for (const path of row.reports) assert(existsSync(resolve(repo, path)), path);
    if (row.status === 'no-op') assert(row.category?.length > 0, row.id);
  }
});

test('Durable current overlay preserves the exact historical map and its rows', () => {
  const path = 'compatibility/durable-port-map.v1.5.0.json';
  const prior = read(path), current = read('compatibility/durable-port-map.json');
  const candidate = current.currentImplementation.candidate;
  const overlay = read(candidate.overlay);
  assert.equal(overlay.preservedMap, path);
  assert.equal(createHash('sha256').update(readFileSync(resolve(repo, path))).digest('hex'), overlay.preservedMapSha256);
  assert.equal(overlay.preservedMapSha256, '45fb24e89fb9d0c63ccc2fd8c05dd6e66f3b63fad0d7500f2964303380690ee2');
  assert.deepEqual(current.files, prior.files);
  assert.equal(current.upstreamCommit, prior.upstreamCommit);
  assert.deepEqual(overlay.source, table.to);
  assert.deepEqual(candidate.source, table.to);
  for (const row of overlay.rows) {
    const full = table.rows.find(value => value.id === row.id);
    assert(full, row.id);
    for (const key of ['source', 'status', 'workPackage', 'reports']) assert.deepEqual(row[key], full[key], `${row.id}.${key}`);
  }
});

test('the current Node fixture pins the candidate and remains test-only', () => {
  const root = 'tools/NodeBridge/runtime-fixture-152';
  const manifest = read(`${root}/package.json`), lock = read(`${root}/package-lock.json`), provenance = read(`${root}/fixture-provenance.json`);
  assert.equal(provenance.runtimeVersion, table.to.tag.slice(1));
  assert.equal(provenance.validation.productionCacheAdmission, false);
  for (const section of ['dependencies', 'overrides']) for (const [name, version] of Object.entries(manifest[section]))
    if (name.startsWith('@earendil-works/')) assert.equal(version, '1.5.2', name);
  assert.deepEqual(lock.packages[''].dependencies, manifest.dependencies);
  for (const [name, field] of [['package.json', 'manifestSha256'], ['package-lock.json', 'lockSha256']])
    assert.equal(createHash('sha256').update(readFileSync(resolve(repo, `${root}/${name}`))).digest('hex'), provenance[field]);
});
