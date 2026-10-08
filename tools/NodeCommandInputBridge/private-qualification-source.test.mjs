// Source-only controls: no worker, original loader, process or native UI is started.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPrivateQualificationSource, validatePrivateQualificationLease, validatePrivateQualificationRegistrations } from './module-loader.mjs';
import { translateRegistrations } from './admission.mjs';
const sourcePath = fileURLToPath(new URL('./qualification-test-fixtures/private-original-ui-qualification-plugin.mjs', import.meta.url));
const sourceRoot = dirname(sourcePath), bytes = fs.readFileSync(sourcePath);
const lease = () => ({ leaseId: 'ui-qualification-' + 'a'.repeat(32), ownerId: 'actual-owner', ownerGeneration: 7, sourceRoot, sourcePath, bytes: 2960, sha256: '8e27790e1b5aa480c0e44595e98026b3d130fcb8a789b3b3aed2b5d335d336c8', sourceCommit: 'd86654abb8862e201933517d6f1fce9f88dd117f' });
const acquire = (overrides = {}) => createPrivateQualificationSource(lease(), 'actual-owner', 7, { read: () => bytes, lstat: () => ({ isSymbolicLink: () => false }), realpath: path => resolve(path), ...overrides });
test('actual owner, generation, fixed pin and exact metadata fail before physical reads', () => {
  let reads = 0;
  for (const change of [{ ownerId: 'borrowed' }, { ownerGeneration: 8 }, { leaseId: 'ui-qualification-' + 'A'.repeat(32) }, { bytes: 2959 }, { sha256: '0'.repeat(64) }, { sourceCommit: '0'.repeat(40) }, { sourcePaths: [sourcePath] }]) {
    assert.throws(() => createPrivateQualificationSource({ ...lease(), ...change }, 'actual-owner', 7, { read: () => { reads++; return bytes; } }));
  }
  assert.equal(reads, 0);
});
test('lease snapshot immutable; admission contains exactly one file', () => {
  const metadata = lease(), snapshot = validatePrivateQualificationLease(metadata, 'actual-owner', 7);
  metadata.ownerGeneration = 8; assert.equal(snapshot.ownerGeneration, 7); assert(Object.isFrozen(snapshot));
  const owned = acquire(); assert(owned.matches(sourcePath)); assert(!owned.matches(join(sourceRoot, 'neighbor.mjs')));
  assert.throws(() => validatePrivateQualificationLease({ ...lease(), sourcePath: join(sourceRoot, '..', 'private-original-ui-qualification-plugin.mjs') }, 'actual-owner', 7));
});
test('every verification checks same-length physical mutation and supplied load bytes', () => {
  let current = bytes, reads = 0; const owned = acquire({ read: () => { reads++; return current; } });
  owned.verify(); assert.equal(reads, 2); current = Buffer.from(bytes); current[0] ^= 1;
  assert.throws(() => owned.verify()); assert.throws(() => owned.verify(current)); owned.verify(bytes);
});
test('root, ancestor and canonical aliases cannot acquire', () => {
  assert.throws(() => acquire({ lstat: () => ({ isSymbolicLink: () => true }) }));
  assert.throws(() => acquire({ lstat: path => ({ isSymbolicLink: () => path === sourcePath }) }));
  assert.throws(() => acquire({ realpath: path => path === sourceRoot ? join(sourceRoot, 'alias') : resolve(path) }));
});
test('retirement fences admission while exact final verification remains owned', () => {
  const owned = acquire(); owned.beginClose(); assert(owned.ownsPath(sourcePath)); assert(!owned.matches(sourcePath));
  assert.throws(() => owned.assertActive()); owned.verify(); owned.close(); owned.close(); assert.throws(() => owned.verify());
});
test('repeated close preserves original physical failure identity', () => {
  const original = Error('held-source original'); let fail = false, reads = 0;
  const owned = acquire({ read: () => { reads++; if (fail) throw original; return bytes; } }); fail = true;
  assert.throws(() => owned.close(), error => error === original); assert.throws(() => owned.close(), error => error === original); assert.equal(reads, 2);
});
test('actual translation retains exact callbacks; private catalog rejects extra semantics', () => {
  const names = ['input', 'editor', 'timeout', 'preabort', 'child', 'abort', 'parent', 'session'].map(name => 'qualification-' + name);
  const commands = new Map(names.map(name => [name, { name, description: name, handler: async () => {} }]));
  const extension = { path: sourcePath, commands, handlers: new Map(), tools: new Map(), flags: new Map(), shortcuts: new Map(), messageRenderers: new Map(), entryRenderers: new Map() };
  const runtime = { pendingProviderRegistrations: [], pendingNativeProviderRegistrations: [], pendingVirtualModelRegistrations: [], mcpServers: { list: () => [] } };
  const catalog = translateRegistrations([extension], runtime); validatePrivateQualificationRegistrations(catalog, sourcePath);
  assert.equal(catalog.commands[0].handler, commands.get(names[0]).handler);
  assert.throws(() => validatePrivateQualificationRegistrations({ ...catalog, inputs: [{}] }, sourcePath));
  assert.throws(() => validatePrivateQualificationRegistrations({ ...catalog, commands: catalog.commands.slice(1) }, sourcePath));
  assert.throws(() => validatePrivateQualificationRegistrations({ ...catalog, commands: catalog.commands.map((row, index) => index ? row : { ...row, completion: () => [] }) }, sourcePath));
});
