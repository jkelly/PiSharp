// Independent Node zlib decoder for native protocol4 output. No package import, network or generated golden writes.
import assert from 'node:assert/strict';
import { inflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
const summary = (text) => `utf16=${text.length},utf8=${Buffer.byteLength(text)},sha256=${createHash('sha256').update(text,'utf8').digest('hex')}`;
const sameText = (actual, expected, message) => {
  if (actual !== expected) throw new Error(`${message}: actual(${summary(actual)}) expected(${summary(expected)})`);
};
const sameData = (actual, expected, message) => {
  if (!isDeepStrictEqual(actual, expected)) throw new Error(`${message}: actual(${summary(JSON.stringify(actual))}) expected(${summary(JSON.stringify(expected))})`);
};
let input = '';
process.stdin.setEncoding('utf8');
for await (const chunk of process.stdin) {
  input += chunk;
  assert(input.length <= 8 * 1024 * 1024, 'bounded fixture input');
}
const { fixtures } = JSON.parse(input);
let packed = 0;
for (const fixture of fixtures) {
  const { websocket, ipc, frame, inflated, id } = fixture;
  if (websocket.base64) {
    const bytes = Buffer.from(websocket.base64, 'base64');
    assert(bytes.length >= 4, `${id}: header`);
    const length = bytes.readUInt32BE(0);
    assert(length <= bytes.length - 4, `${id}: envelope length`);
    const { z: field, ...envelope } = JSON.parse(bytes.subarray(4, 4 + length).toString('utf8'));
    assert(field === 'args' || field === 'value', `${id}: packed field`);
    const payload = inflateRawSync(bytes.subarray(4 + length), { maxOutputLength: 2 * 1024 * 1024 }).toString('utf8');
    const header = JSON.stringify(envelope);
    const text = `${header.slice(0, -1)},${JSON.stringify(field)}:${payload}}`;
    sameText(text, inflated, `${id}: Node inflates native WebSocket bytes exactly`);
    sameData(JSON.parse(text), frame, `${id}: decoded native frame`);
    packed++;
  } else {
    sameData(JSON.parse(websocket.text), frame, `${id}: native text`);
  }
  const { z, ...envelope } = ipc;
  const restored = z === undefined ? ipc : {
    ...envelope,
    [z[0]]: JSON.parse(inflateRawSync(Buffer.from(z[1], 'base64'), { maxOutputLength: 2 * 1024 * 1024 }).toString('utf8')),
  };
  sameData(restored, frame, `${id}: Node decodes native IPC payload`);
}
assert.equal(fixtures.length, 8, 'complete frozen fixture set');
assert.equal(packed, 3, 'every threshold/large packed fixture decoded');
process.stdout.write(JSON.stringify({ fixtures: fixtures.length, packed, node: process.version, zlib: process.versions.zlib }));
