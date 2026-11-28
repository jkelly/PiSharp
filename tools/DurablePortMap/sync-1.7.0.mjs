// Generate or check the scoped Durable overlay from immutable upstream Git objects.
// Usage: node tools/DurablePortMap/sync-1.7.0.mjs <upstream-git-directory> [--check]
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
const upstream = process.argv[2];
if (!upstream) throw new Error('Supply an upstream Pi Git checkout with tags v1.6.0 and v1.7.0');
const git = (...args) => execFileSync('git', ['-C', upstream, ...args], { encoding: 'utf8' }).trim();
const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const immutable = tag => ({tag, commit: git('rev-parse', tag + '^{commit}'), tree: git('rev-parse', tag + '^{tree}')});
const previous = immutable('v1.6.0');
const source = immutable('v1.7.0');
if (previous.commit !== 'faac585884ae2c013a6b1400f3e9e77f10b34bac' || source.commit !== 'fb0fe823539f6d04850f6e34748c01f01f212e1f')
  throw new Error('Upstream tags do not match the frozen source commits');
const preservedMap = 'compatibility/durable-port-map.v1.5.0.json';
const previousOverlay = 'compatibility/durable-sync-1.6.0-overlay.json';
const mappings = new Map(read(preservedMap).files.map(row => [row.upstream, row.pisharp]));
for (const row of read(previousOverlay).rows) {
  const mapping = row.nativeMapping ?? row.historicalMapping;
  if (mapping) mappings.set(row.upstream[0], mapping.pisharp ?? mapping.tests ?? null);
}
mappings.set('packages/chord/test/context.test.ts', 'tests/PiSharp.Durable.Tests/Chord/ContextCases.cs');
mappings.set('packages/chord/test/delta-diff.test.ts', 'tests/PiSharp.Durable.Tests/Chord/DiffCases.cs');
mappings.set('packages/durable/test/sqlite-commit.test.ts', 'tests/PiSharp.Durable.Tests/Storage/SqliteDetailedBulkCases.cs');
const rows = [];
for (const line of git('diff', '--no-renames', '--name-status', previous.commit, source.commit, '--', 'packages/durable', 'packages/chord').split('\n')) {
  const [change, path] = line.split('\t');
  if (!path.startsWith('packages/durable/') && !mappings.has(path)) continue;
  rows.push({ upstream: path, change, beforeBlob: change === 'A' ? null : git('rev-parse', previous.commit + ':' + path),
    afterBlob: change === 'D' ? null : git('rev-parse', source.commit + ':' + path), nativeLocation: mappings.get(path) ?? null });
}
const overlay = { schemaVersion: 1, source, previous, targetLock: 'compatibility/target.lock.json', preservedMap,
  preservedMapSha256: createHash('sha256').update(readFileSync(resolve(root, preservedMap))).digest('hex'), previousOverlay,
  scope: 'Changed Durable files and mapped Chord files used by Durable. Locations identify native counterparts; the overlay records source coverage, not execution evidence.', rows };
const mapPath = 'compatibility/durable-port-map.json';
const overlayPath = 'compatibility/durable-sync-1.7.0-overlay.json';
const map = read(mapPath);
const implementation = map.currentImplementation;
if (implementation.candidate.overlay !== overlayPath) implementation.previousCandidates.unshift(implementation.candidate);
implementation.candidate = { source, status: 'source-mapped', overlay: overlayPath, preservedMap,
  note: 'The overlay identifies immutable upstream deltas. Row-level historical evidence remains pinned to its original release.' };
for (const [path, data] of [[mapPath, map], [overlayPath, overlay]]) {
  const rendered = JSON.stringify(data, null, 2) + '\n';
  if (process.argv.includes('--check')) {
    if (readFileSync(resolve(root, path), 'utf8') !== rendered) throw new Error('Stale ' + path);
  } else writeFileSync(resolve(root, path), rendered);
}
console.log('Verified ' + rows.length + ' changed Durable and mapped Chord paths.');
