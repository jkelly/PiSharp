import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { ensureArchive, historicalArchive } from './ensure-historical-ai-archive.mjs';

const bytes = Buffer.from('opaque archive fixture; never executed');
const pin = { ...historicalArchive, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };

async function fixture(run) {
  const root = await mkdtemp(join(tmpdir(), 'pisharp-historical-'));
  try { await run(root, join(root, pin.name)); }
  finally { await rm(root, { recursive: true, force: true }); }
}

test('fixed official source and immutable historical pin', () => {
  assert.equal(historicalArchive.url, 'https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-0.99.1.tgz');
  assert.equal(historicalArchive.bytes, 760966);
  assert.equal(historicalArchive.sha256, 'f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3');
});

test('verified bytes publish atomically and reuse requires no request', async () => fixture(async (root, target) => {
  let calls = 0;
  await ensureArchive(target, { pin, request: async (url, options) => {
    calls++;
    assert.equal(url, pin.url); assert.equal(options.redirect, 'error');
    assert.deepEqual(await readdir(root), [(await readdir(root))[0]]);
    assert.match((await readdir(root))[0], /^\.historical-ai-/);
    return new Response(bytes);
  } });
  assert.deepEqual(await readFile(target), bytes);
  assert.deepEqual(await readdir(root), [pin.name]);
  await ensureArchive(target, { pin, request: () => { throw new Error('cache miss'); } });
  assert.equal(calls, 1);
}));

test('bad status, oversized, incomplete and hash mismatch never publish and remove staging', async () => {
  for (const response of [new Response(bytes, { status: 403 }), new Response(Buffer.concat([bytes, bytes])),
    new Response(bytes.subarray(1)), new Response(Buffer.alloc(bytes.length)), new Response(bytes, { headers: { 'content-length': '1' } })]) {
    await fixture(async (root, target) => {
      await assert.rejects(ensureArchive(target, { pin, request: async () => response }));
      assert.deepEqual(await readdir(root), []);
    });
  }
});

test('existing corrupt bytes fail closed without fetching or overwriting', async () => fixture(async (root, target) => {
  await writeFile(target, Buffer.alloc(bytes.length));
  await assert.rejects(ensureArchive(target, { pin, request: () => { throw new Error('must not fetch'); } }), /SHA256/);
  assert.deepEqual(await readFile(target), Buffer.alloc(bytes.length));
  assert.deepEqual(await readdir(root), [pin.name]);
}));
