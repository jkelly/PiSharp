#!/usr/bin/env node
// Complete source inventory from local Git objects; never uses GitHub's truncated files API.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export function sourceDiff(upstream, from = 'v1.5.0', to = 'v1.5.2') {
  const git = (...args) => execFileSync('git', ['-C', resolve(upstream), ...args], { maxBuffer: 1 << 30 });
  const ref = tag => ({ tag, commit: git('rev-parse', `${tag}^{commit}`).toString().trim(), tree: git('rev-parse', `${tag}^{tree}`).toString().trim() });
  const before = ref(from), after = ref(to);
  const tree = sha => new Map(git('ls-tree', '-rz', sha).toString().split('\0').filter(Boolean).map(row => {
    const [meta, path] = row.split('\t'), [mode, type, oid] = meta.split(' ');
    return [path, { mode, type, oid }];
  }));
  const oldTree = tree(before.commit), newTree = tree(after.commit);
  const fields = git('diff', '--name-status', '-z', '-M', before.commit, after.commit).toString().split('\0').filter(Boolean);
  const changes = [];
  for (let i = 0; i < fields.length;) {
    const status = fields[i++], first = fields[i++];
    const renamed = status.startsWith('R') || status.startsWith('C');
    changes.push({ status, oldPath: status === 'A' ? null : first, newPath: status === 'D' ? null : renamed ? fields[i++] : first });
  }
  const ids = [...new Set(changes.flatMap(row => [row.oldPath && oldTree.get(row.oldPath), row.newPath && newTree.get(row.newPath)]).filter(Boolean).filter(x => x.type === 'blob').map(x => x.oid))];
  // cat-file needs stdin; use a single process for every unique blob, including binary files.
  const batch = ids.length ? execFileSync('git', ['-C', resolve(upstream), 'cat-file', '--batch'], { input: ids.join('\n') + '\n', maxBuffer: 1 << 30 }) : Buffer.alloc(0);
  const hashes = new Map();
  for (let offset = 0; offset < batch.length;) {
    const end = batch.indexOf(10, offset), [oid, type, sizeText] = batch.subarray(offset, end).toString().split(' '), size = Number(sizeText);
    if (type !== 'blob' || !Number.isSafeInteger(size)) throw new Error('Invalid Git batch record');
    const value = batch.subarray(end + 1, end + 1 + size);
    hashes.set(oid, { sha256: createHash('sha256').update(value).digest('hex'), bytes: size });
    offset = end + size + 2;
  }
  const endpoint = (path, source, files) => {
    if (path === null) return null;
    const object = files.get(path);
    if (!object) throw new Error(`Missing ${source.commit}:${path}`);
    return { path, mode: object.mode, type: object.type, gitBlob: object.oid, ...hashes.get(object.oid), url: `https://github.com/earendil-works/pi/blob/${source.commit}/${path}` };
  };
  const files = changes.map(row => ({ status: row.status, package: /^packages\/([^/]+)\//.exec(row.newPath ?? row.oldPath)?.[1] ?? '(root)', before: endpoint(row.oldPath, before, oldTree), after: endpoint(row.newPath, after, newTree) }));
  const count = key => files.reduce((result, row) => (result[key(row)] = (result[key(row)] ?? 0) + 1, result), {});
  return { schemaVersion: 1, generatedBy: 'tools/CompatibilityReport/source-diff.mjs', method: 'Complete local git diff --name-status -z -M and Git object bytes; no API pagination or file limit', from: before, to: after, summary: { files: files.length, byStatus: count(row => row.status[0]), byPackage: count(row => row.package) }, files };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const args = Object.fromEntries(process.argv.slice(2).reduce((out, arg, i, all) => arg.startsWith('--') ? [...out, [arg.slice(2), all[i + 1]]] : out, []));
  if (!args.upstream || !args.out) throw new Error('--upstream and --out are required');
  const inventory = sourceDiff(args.upstream, args.from, args.to);
  writeFileSync(resolve(args.out), JSON.stringify(inventory, null, 2) + '\n');
  console.log(JSON.stringify(inventory.summary, null, 2));
}
