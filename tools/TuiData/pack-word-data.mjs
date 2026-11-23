#!/usr/bin/env node
// Verifies (or re-packs with --pack <raw-dir>) the 6 gzip-compressed ICU78.3 word dictionary
// embedded resources under src/PiSharp.Tui/Input/Assets/WordData/.
import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { gunzipSync, gzipSync } from 'node:zlib';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, '../..');
const assetDir = resolve(repoRoot, 'src/PiSharp.Tui/Input/Assets/WordData');

export const PINNED_WORD_DATA = [
  { name: 'cjk', file: 'cjk.gz', bytes: 2007296, sha256: '3850478c713b2004cbb96ef4b9f049caf16d6faf32a883d2e63c09659d0c59c7' },
  { name: 'thai', file: 'thai.gz', bytes: 126144, sha256: 'ea47ae521cea65077bf047a75d1e9d28c495ec2f8cf4d2734fcb65a888229be9' },
  { name: 'normalization', file: 'normalization.gz', bytes: 116723, sha256: 'c66eb5009782666af89b49022babeb4896be4b1112715d276095431d0c4e8309' },
  { name: 'lao', file: 'lao.gz', bytes: 162624, sha256: 'b044d4232c2fea926b88f6047c4efcd683e7cf0573cf9f60990aa7500c60ef0c' },
  { name: 'khmer', file: 'khmer.gz', bytes: 445552, sha256: '080582d61636c7f9039d65bc37452d100faa07f3ba7f5e21aa32aa1672441413' },
  { name: 'burmese', file: 'burmese.gz', bytes: 254448, sha256: '99f1b2afe6672bc495a872e85dd5ed2be5ffc4d9b8377deff2dbc18a2b4538e5' }
];

export function verifyAll(dir = assetDir) {
  let totalCompressed = 0;
  let totalRaw = 0;
  for (const spec of PINNED_WORD_DATA) {
    const gz = readFileSync(resolve(dir, spec.file));
    if (gz.length > 2_106_316) throw new Error(`${spec.file}: exceeds compressed bound`);
    const raw = gunzipSync(gz);
    if (raw.length !== spec.bytes) throw new Error(`${spec.file}: expected ${spec.bytes} bytes, got ${raw.length}`);
    const digest = createHash('sha256').update(raw).digest('hex');
    if (digest !== spec.sha256) throw new Error(`${spec.file}: sha256 mismatch (${digest} != ${spec.sha256})`);
    totalCompressed += gz.length;
    totalRaw += raw.length;
  }
  return { count: PINNED_WORD_DATA.length, totalCompressed, totalRaw };
}

export function packFromRaw(rawDir, outDir = assetDir) {
  for (const spec of PINNED_WORD_DATA) {
    const raw = readFileSync(resolve(rawDir, `${spec.name}.bin`));
    if (raw.length !== spec.bytes) throw new Error(`${spec.name}.bin: expected ${spec.bytes} bytes, got ${raw.length}`);
    const digest = createHash('sha256').update(raw).digest('hex');
    if (digest !== spec.sha256) throw new Error(`${spec.name}.bin: sha256 mismatch (${digest} != ${spec.sha256})`);
    const gz = gzipSync(raw, { level: 9 });
    writeFileSync(resolve(outDir, spec.file), gz);
  }
  return verifyAll(outDir);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const packIdx = process.argv.indexOf('--pack');
  const summary = packIdx >= 0 ? packFromRaw(resolve(process.argv[packIdx + 1])) : verifyAll();
  console.log(`WordData verified: ${summary.count} resources, ${summary.totalCompressed} compressed bytes -> ${summary.totalRaw} raw bytes`);
}
