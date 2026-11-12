import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { sourceDiff } from './source-diff.mjs';
import { resolveSourceLock } from './source-lock.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const read = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const oldPath = 'compatibility/target.v1.5.0.lock.json';
const target = read('compatibility/target.lock.json');
const previous = read(oldPath);
const inventory = read('compatibility/sync-1.5.2-files.json');

test('the exact prior source lock and historical evidence remain preserved', () => {
  assert.equal(createHash('sha256').update(readFileSync(resolve(repo, oldPath))).digest('hex'), '429dad2debcf296a980cf3d813e7d1609855bc0d1e71f84c29bb003e758c3e1e');
  assert.equal(target.previousTarget.lock, oldPath);
  assert.equal(target.previousTarget.commit, previous.source.commit);
  assert.deepEqual(target.previousBaseline, previous.previousBaseline);
  for (const path of ['compatibility/surfaces.json', 'compatibility/parity.json'])
    assert.equal(resolveSourceLock(read, target, read(path).sourceSha).source.commit, previous.source.commit);
  const release = read('compatibility/public-release.json');
  const priorRelease = release.version === '1.5.0.1' ? release : release.previousReleases.find(entry => entry.version === '1.5.0.1');
  assert(priorRelease, 'The 1.5.0.1 release record must remain available');
  assert.equal(resolveSourceLock(read, target, priorRelease.upstream.commit).source.commit, previous.source.commit);
  assert.equal(priorRelease.publication.tagCommit, '8144e3eaa138a5faf56f5cbb0de477a56fd48f8f');
  assert.equal(priorRelease.publication.nugetPackagesPublished, true);
  const releaseLock = resolveSourceLock(read, target, release.upstream.commit);
  assert.equal(releaseLock.source.tag, release.upstream.tag);
  if (release.version === '1.5.2') assert.equal(releaseLock.source.commit, target.source.commit);
  assert.throws(() => resolveSourceLock(read, target, 'missing'), /No preserved source lock/);
});

test('the candidate lock covers every surviving changed path and package manifest', () => {
  assert.equal(target.source.tag, 'v1.5.2');
  assert.equal(target.source.commit, 'b4afdddf44e059c67ccf5097e5317d913521b470');
  assert.deepEqual(inventory.from, { tag: previous.source.tag, commit: previous.source.commit, tree: previous.source.tree });
  assert.deepEqual(inventory.to, { tag: target.source.tag, commit: target.source.commit, tree: target.source.tree });
  const rows = new Map(target.artifacts.filter(row => row.kind === 'source-file').map(row => [row.path, row]));
  assert.equal(inventory.summary.files, inventory.files.length);
  assert.equal(inventory.files.length, 2531);
  for (const file of inventory.files) {
    assert.match(file.status, /^(?:[ADMT]|[RC]\d+)$/);
    assert(file.before || file.after);
    for (const endpoint of [file.before, file.after].filter(Boolean)) {
      assert.match(endpoint.gitBlob, /^[0-9a-f]{40}$/);
      assert.match(endpoint.sha256, /^[0-9a-f]{64}$/);
      assert(Number.isInteger(endpoint.bytes));
    }
    if (file.after) {
      const locked = rows.get(file.after.path);
      assert(locked, file.after.path);
      assert.equal(locked.gitBlob, file.after.gitBlob);
      assert.equal(locked.sha256, file.after.sha256);
      assert.equal(locked.bytes, file.after.bytes);
    }
  }
  for (const name of Object.keys(inventory.summary.byPackage).filter(name => name !== '(root)'))
    assert(rows.has(`packages/${name}/package.json`), name);
});

const upstream = process.env.PI_UPSTREAM_V1_5_2;
if (process.env.PI_REQUIRE_UPSTREAM === '1' && !upstream) throw new Error('PI_REQUIRE_UPSTREAM requires PI_UPSTREAM_V1_5_2; refusing a skipped source check');
test('the complete inventory reproduces from local upstream Git objects', { skip: upstream ? false : 'set PI_UPSTREAM_V1_5_2 to the pinned Pi checkout' }, () => {
  assert.deepEqual(sourceDiff(upstream), inventory);
});
