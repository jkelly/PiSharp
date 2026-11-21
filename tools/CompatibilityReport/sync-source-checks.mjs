// Shared source-lock and file-inventory checks for a sync (v1.5.0..v1.5.2, v1.5.2..v1.6.0, ...).
// Each sync's sync-<version>-source.test.mjs names its own lock, preserved previous lock and inventory, so a later
// target never changes what an earlier sync's test reads.
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

// lockPath: the lock that pins this sync's target (compatibility/target.lock.json while it is the current target, its
//   preserved copy afterwards). previousLockPath/previousLockSha256: the preserved lock it moved from, byte-for-byte.
// inventoryLockPath: the preserved lock surfaces.json and parity.json were generated for.
// preservedRelease: a release published before this sync, which must stay resolvable and unchanged.
// version: the candidate version; once the release record names it, the record must resolve to this sync's lock.
export function defineSourceChecks({ lockPath, previousLockPath, previousLockSha256, inventoryPath, inventoryLockPath,
  tag, commit, files, version, preservedRelease, upstreamEnv }) {
  const target = read(lockPath), previous = read(previousLockPath), inventory = read(inventoryPath);

  test('the exact prior source lock and historical evidence remain preserved', () => {
    assert.equal(createHash('sha256').update(readFileSync(resolve(repo, previousLockPath))).digest('hex'), previousLockSha256);
    assert.equal(target.previousTarget.lock, previousLockPath);
    assert.equal(target.previousTarget.commit, previous.source.commit);
    assert.deepEqual(target.previousBaseline, previous.previousBaseline);
    const inventoryLock = read(inventoryLockPath);
    for (const path of ['compatibility/surfaces.json', 'compatibility/parity.json'])
      assert.equal(resolveSourceLock(read, target, read(path).sourceSha).source.commit, inventoryLock.source.commit);
    const release = read('compatibility/public-release.json');
    const priorRelease = release.version === preservedRelease.version ? release : release.previousReleases.find(entry => entry.version === preservedRelease.version);
    assert(priorRelease, `The ${preservedRelease.version} release record must remain available`);
    assert.equal(resolveSourceLock(read, target, priorRelease.upstream.commit).source.commit, read(preservedRelease.lockPath).source.commit);
    assert.equal(priorRelease.publication.tagCommit, preservedRelease.tagCommit);
    assert.equal(priorRelease.publication.nugetPackagesPublished, true);
    // The release record may be newer than this sync's lock, so it resolves through the current target's chain.
    const releaseLock = resolveSourceLock(read, read('compatibility/target.lock.json'), release.upstream.commit);
    assert.equal(releaseLock.source.tag, release.upstream.tag);
    if (release.version === version) assert.equal(releaseLock.source.commit, target.source.commit);
    assert.throws(() => resolveSourceLock(read, target, 'missing'), /No preserved source lock/);
  });

  test('the candidate lock covers every surviving changed path and package manifest', () => {
    assert.equal(target.source.tag, tag);
    assert.equal(target.source.commit, commit);
    assert.deepEqual(inventory.from, { tag: previous.source.tag, commit: previous.source.commit, tree: previous.source.tree });
    assert.deepEqual(inventory.to, { tag: target.source.tag, commit: target.source.commit, tree: target.source.tree });
    const rows = new Map(target.artifacts.filter(row => row.kind === 'source-file').map(row => [row.path, row]));
    assert.equal(inventory.summary.files, inventory.files.length);
    assert.equal(inventory.files.length, files);
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

  const upstream = process.env[upstreamEnv];
  if (process.env.PI_REQUIRE_UPSTREAM === '1' && !upstream) throw new Error(`PI_REQUIRE_UPSTREAM requires ${upstreamEnv}; refusing a skipped source check`);
  test('the complete inventory reproduces from local upstream Git objects', { skip: upstream ? false : `set ${upstreamEnv} to a Pi checkout that has ${inventory.from.tag} and ${inventory.to.tag}` }, () => {
    assert.deepEqual(sourceDiff(upstream, inventory.from.tag, inventory.to.tag), inventory);
  });
}
