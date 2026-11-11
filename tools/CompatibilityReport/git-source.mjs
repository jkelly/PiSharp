import { execFileSync } from 'node:child_process';

// Batch Git reads keep full-repository source freezes practical on Windows too.
export function readSourceFiles(upstream, commit, paths) {
  const run = (args, input) => execFileSync('git', ['-C', upstream, ...args], { input, maxBuffer: 1 << 30 });
  const objects = new Map(run(['ls-tree', '-rz', commit]).toString().split('\0').filter(Boolean).map(row => {
    const tab = row.indexOf('\t'), [mode, type, oid] = row.slice(0, tab).split(' ');
    return [row.slice(tab + 1), { mode, type, oid }];
  }));
  const present = paths.filter(path => objects.has(path));
  const ids = [...new Set(present.map(path => objects.get(path)).filter(object => object.type === 'blob').map(object => object.oid))];
  const batch = ids.length ? run(['cat-file', '--batch'], ids.join('\n') + '\n') : Buffer.alloc(0);
  const contents = new Map();
  for (let offset = 0; offset < batch.length;) {
    const end = batch.indexOf(10, offset), [oid, type, length] = batch.subarray(offset, end).toString().split(' '), size = Number(length);
    if (type !== 'blob' || !Number.isSafeInteger(size)) throw new Error('Invalid source Git batch record');
    contents.set(oid, batch.subarray(end + 1, end + size + 1));
    offset = end + size + 2;
  }
  return new Map(present.map(path => {
    const object = objects.get(path), bytes = contents.get(object.oid);
    if (!bytes) throw new Error(`Source path is not a blob: ${path}`);
    return [path, { gitBlob: object.oid, bytes }];
  }));
}
