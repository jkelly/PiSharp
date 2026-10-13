// Prints Fixtures/env-line-scan.upstream.json: what the pinned durable env (Pi abe508e1b89912adde45528136c3221eb69acdd7,
// packages/durable/src/env/decode.ts and line-scan.ts) and Node's WHATWG TextDecoder produce for the 5000 seeded random files of
// test/env-line-scan.test.ts, which EnvLineScanCases.cs replays with the same generator and chunking. Per seed, a digest of
// "seed|file bytes (hex)|UTF-8 of new TextDecoder().decode(file) (hex)" for the decoding case, and of
// "seed|startLine|endLine|newlines,start,end,firstLineEnd,lastLineStart,selectedBytes,firstLineBytes" from upstream's LineScanner fed in
// the test's chunks for the scanning case (each the first 16 hex digits of its SHA-256).
// Usage, from a Pi checkout at that commit (with its node_modules installed):
//   node --no-warnings --conditions=source --experimental-strip-types capture-env-line-scan.mjs <pi checkout> > env-line-scan.upstream.json
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const pi = process.argv[2];
if (!pi) throw new Error('usage: capture-env-line-scan.mjs <pi checkout>');
const at = (path) => pathToFileURL(join(pi, 'packages/durable/src/env', path)).href;
const { StreamDecoder } = await import(at('decode.ts'));
const { LineScanner } = await import(at('line-scan.ts'));

// test/env-line-scan.test.ts, verbatim.
function random(seed) {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const PIECES = [[0x0a], [0x0a], [0x61], [0x62, 0x63], [0xef, 0xbb, 0xbf], [0xc3, 0xa9], [0xe2, 0x82, 0xac], [0xf0, 0x9f, 0x98, 0x80],
  [0xe2, 0x82], [0xff], [0x80], [0xf0, 0x9f], [0x0d, 0x0a]];
function randomFile(next) {
  const bytes = [];
  const pieces = Math.floor(next() * 60);
  for (let index = 0; index < pieces; index++) bytes.push(...PIECES[Math.floor(next() * PIECES.length)]);
  return Uint8Array.from(bytes);
}

const hex = (bytes) => Buffer.from(bytes).toString('hex');
const digest = (text) => createHash('sha256').update(text).digest('hex').slice(0, 16);
const decode = [];
const scan = [];
for (let seed = 1; seed <= 5000; seed++) {
  // "decodes any chunking like decoding the whole stream": the streamed text must equal the whole decode, captured here.
  const next = random(seed);
  const bytes = randomFile(next);
  const decoder = new StreamDecoder();
  let text = '';
  for (let offset = 0; offset < bytes.length; ) {
    const size = 1 + Math.floor(next() * 7);
    text += decoder.decode(bytes.subarray(offset, offset + size));
    offset += size;
  }
  text += decoder.decode();
  const whole = new TextDecoder().decode(bytes);
  if (text !== whole) throw new Error(`upstream StreamDecoder disagrees with TextDecoder at seed ${seed}`);
  decode.push(digest(`${seed}|${hex(bytes)}|${hex(new TextEncoder().encode(whole))}`));
}
for (let seed = 1; seed <= 5000; seed++) {
  // "agrees with decoding and splitting the whole file".
  const next = random(seed);
  const file = randomFile(next);
  const lines = new TextDecoder().decode(file).split('\n');
  const startLine = Math.floor(next() * (lines.length + 2));
  const endLine = next() < 0.3 ? undefined : startLine + 1 + Math.floor(next() * (lines.length + 1));
  const scanner = new LineScanner(startLine, endLine);
  for (let offset = 0; offset < file.length; ) {
    const size = 1 + Math.floor(next() * 7);
    scanner.push(file.subarray(offset, offset + size));
    offset += size;
  }
  const s = scanner.finish();
  scan.push(digest(`${seed}|${startLine}|${endLine ?? ''}|${s.newlines},${s.start},${s.end},${s.firstLineEnd},${s.lastLineStart},${s.selectedBytes},${s.firstLineBytes}`));
}
// U+FEFF after an invalid sequence, decoded byte by byte ("keeps a U+FEFF that does not start the stream").
const bom = Uint8Array.from([0xe2, 0x82, 0xef, 0xbb, 0xbf, 0x61]);
const bomDecoder = new StreamDecoder();
const bomText = [...bom].map((byte) => bomDecoder.decode(Uint8Array.of(byte))).join('') + bomDecoder.decode();
process.stdout.write(JSON.stringify({
  source: 'Pi abe508e1b89912adde45528136c3221eb69acdd7 packages/durable/src/env/{decode,line-scan}.ts under Node ' + process.version,
  keepsLaterBom: [...bomText].map((character) => character.codePointAt(0)),
  decode,
  scan,
}) + '\n');
