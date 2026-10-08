// Authored controls only. Execution must be assigned by the coordinator.
import assert from 'node:assert/strict';
import test from 'node:test';
import { createToolLoadoutSnapshot, createToolLoadoutCache, invokeLoadoutPreparation } from './tool-loadout.mjs';
const row = (name, exposure = 'direct', namespace) => ({ declaration: { name, description: 'original ' + name, parameters: { type: 'object' } }, exposure,
  ...(namespace === undefined ? {} : { namespace }) });
const snapshot = () => ({ snapshotId: 'loadout-1', declared: ['outer', 'direct'], callable: ['direct', 'code', 'deferred'],
  registered: [row('direct'), row('outer', 'model-only'), row('code', 'codemode', { name: 'g' }), row('deferred', 'deferred', { name: '', description: '' }), row('hidden', 'hidden', { name: 'g', description: 'read only' })] });
test('lookup includes hidden and inactive metadata, exact tool name and source absence semantics', () => {
  const value = createToolLoadoutSnapshot(snapshot());
  assert.equal(value.getNamespace('missing'), undefined); assert.equal(value.getNamespace('direct'), undefined);
  assert.equal(value.getNamespace('g'), undefined); assert.equal(value.getNamespace('CODE'), undefined);
  assert.deepEqual(value.getNamespace('code'), { name: 'g' }); assert(!Object.hasOwn(value.getNamespace('code'), 'description'));
  assert.deepEqual(value.getNamespace('deferred'), { name: '', description: '' });
  assert.equal(value.getNamespace('hidden').description, 'read only'); assert.equal(value.getExposure('missing'), 'direct');
});
test('array order, shared declaration identity, namespace identity and deep immutable ownership', () => {
  const supplied = snapshot(), value = createToolLoadoutSnapshot(supplied);
  assert.deepEqual(value.declared.map(x => x.name), ['outer', 'direct']);
  assert.deepEqual(value.callable.map(x => x.name), ['direct', 'code', 'deferred']);
  assert.deepEqual(value.registered.map(x => x.name), ['direct', 'outer', 'code', 'deferred', 'hidden']);
  assert.equal(value.declared[1], value.registered[0]); assert.equal(value.getNamespace('code'), value.getNamespace('code'));
  supplied.registered[2].namespace.name = 'later'; assert.equal(value.getNamespace('code').name, 'g');
  assert.throws(() => value.registered.reverse()); assert.throws(() => value.registered[0].parameters.type = 'later');
  assert.throws(() => value.getNamespace('code').name = 'later');
  assert.equal(value.executeTool, undefined); assert.equal(value.registered[0].execute, undefined);
});
test('same native snapshot is shared across callbacks while new snapshots preserve earlier captures', async () => {
  const cache = createToolLoadoutCache(), supplied = snapshot(), first = cache(supplied), captures = [];
  const prepare = value => { captures.push(value); return { descriptions: { outer: value.getNamespace('code').name } }; };
  await invokeLoadoutPreparation(prepare, cache(supplied)); await invokeLoadoutPreparation(prepare, cache(supplied));
  assert.equal(captures[0], first); assert.equal(captures[1], first);
  const later = snapshot(); later.snapshotId = 'loadout-2'; later.registered[2].namespace.name = 'later';
  assert.equal(cache(later).getNamespace('code').name, 'later'); assert.equal(first.getNamespace('code').name, 'g');
  supplied.registered[2].namespace.name = 'changed'; assert.throws(() => cache(supplied), /identity changed/);
});
test('presentation changes are bounded and cannot return execution or mutation capability', async () => {
  const value = createToolLoadoutSnapshot(snapshot());
  assert.equal(await invokeLoadoutPreparation(() => undefined, value), undefined);
  assert.equal(await invokeLoadoutPreparation(() => null, value), null);
  assert.deepEqual(await invokeLoadoutPreparation(loadout => ({ descriptions: { outer: loadout.getNamespace('hidden').description }, hiddenDeclarations: ['outer'] }), value),
    { descriptions: { outer: 'read only' }, hiddenDeclarations: ['outer'] });
  for (const result of [{ executeTool: () => {} }, { descriptions: { x: 1 } }, { hiddenDeclarations: Array(257).fill('x') }])
    await assert.rejects(invokeLoadoutPreparation(() => result, value));
  assert.deepEqual(value.declared.map(x => x.description), ['original outer', 'original direct']);
});
test('unsupported asynchronous return is physically joined before rejection', async () => {
  let release; const gate = new Promise(resolve => { release = resolve; }); let settled = false, rejected = false;
  const pending = invokeLoadoutPreparation(() => gate.then(() => { settled = true; return {}; }), createToolLoadoutSnapshot(snapshot()))
    .catch(error => { rejected = true; assert.match(error.message, /asynchronous/); });
  try { await Promise.resolve(); assert.equal(settled, false); assert.equal(rejected, false); }
  finally { release(); await pending; }
  assert.equal(settled, true); assert.equal(rejected, true);
});
test('malformed, duplicate, unknown, oversized and execution-bearing snapshots reject', () => {
  for (const mutate of [
    value => value.registered.push(value.registered[0]),
    value => value.declared.push('missing'),
    value => value.registered[0].declaration.execute = true,
    value => value.registered[0].namespace = { name: 'g', execute: true },
    value => value.registered[0].namespace = { name: 'g', description: null },
    value => value.registered[0].exposure = 'granted',
    value => value.registered[0].declaration.description = 'x'.repeat(262144)
  ]) { const supplied = snapshot(); mutate(supplied); assert.throws(() => createToolLoadoutSnapshot(supplied)); }
});
test('snapshot cache fails explicitly at its retention boundary', () => {
  const cache = createToolLoadoutCache(); for (let i = 0; i < 128; i++) cache({ ...snapshot(), snapshotId: 'loadout-' + i });
  assert.throws(() => cache({ ...snapshot(), snapshotId: 'loadout-129' }), /retained snapshot budget/);
});
