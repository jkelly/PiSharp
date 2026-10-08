// Authored offline controls. Run only when assigned by the execution coordinator.
import assert from 'node:assert/strict';
import test from 'node:test';
import { selectSources, translateRegistrations } from './admission.mjs';
const runtime = () => ({ pendingProviderRegistrations: [], pendingNativeProviderRegistrations: [], pendingVirtualModelRegistrations: [], mcpServers: { list: () => [] } });
const extension = () => ({ path: '/pinned/source.ts', tools: new Map(), commands: new Map(), handlers: new Map(), flags: new Map(), shortcuts: new Map(), messageRenderers: new Map(), entryRenderers: new Map() });
test('unadmitted paths and excessive selections are rejected before reading', () => {
  const read = () => assert.fail('unadmitted source was read');
  for (const path of ['../pirate.ts', '/pirate.ts', 'packages/coding-agent/examples/extensions/tools.ts']) assert.throws(() => selectSources('/oracle', [path], read), /Unadmitted/);
  assert.throws(() => selectSources('/oracle', [], read), /count/);
  assert.throws(() => selectSources('/oracle', Array(9).fill('x'), read), /count/);
});
test('changed pinned source bytes are rejected', () => {
  assert.throws(() => selectSources('/oracle', ['packages/coding-agent/examples/extensions/pirate.ts'], () => Buffer.alloc(1461)), /Source hash pin/);
});
test('callback functions remain in their realm and descriptor identity is stable', () => {
  const source = extension(), command = async () => {}, hook = async () => {};
  source.commands.set('pirate', { name: 'pirate', description: 'toggle', handler: command }); source.handlers.set('before_agent_start', [hook]);
  const rows = translateRegistrations([source], runtime());
  assert.equal(rows.commands[0].handler, command); assert.equal(rows.beforeAgentStart[0].callback, hook);
  assert.equal(rows.commands[0].callbackId, 'command-1-pirate'); assert.equal(rows.beforeAgentStart[0].callbackId, 'before_agent_start-1-1');
});
test('flags, shortcuts, custom renderers and unsupported hooks diagnose their surface', () => {
  for (const field of ['flags', 'shortcuts', 'messageRenderers', 'entryRenderers']) {
    const source = extension(); source[field].set('x', {});
    assert.throws(() => translateRegistrations([source], runtime()), error => error.bridgeCode === 'UnsupportedRegistration' && error.surface === field);
  }
  const source = extension(); source.handlers.set('session_before_tree', [() => {}]);
  assert.throws(() => translateRegistrations([source], runtime()), /on.session_before_tree/);
});
test('tool rendering and unsupported argument preparation cannot be silently dropped', () => {
  for (const field of ['prepareArguments', 'exposure']) {
    const source = extension(); source.tools.set('hello', { definition: { name: 'hello', description: 'hello', parameters: {}, execute: async () => {}, [field]: () => {} } });
    assert.throws(() => translateRegistrations([source], runtime()), error => error.surface === 'tool.hello.' + field);
  }
});
test('duplicate commands and registration budget fail the complete admission transaction', () => {
  const a = extension(), b = extension();
  for (const source of [a, b]) source.commands.set('x', { name: 'x', description: '', handler: async () => {} });
  assert.throws(() => translateRegistrations([a, b], runtime()), /Duplicate registration name/);
  const many = extension(); many.handlers.set('input', Array(65).fill(() => {}));
  assert.throws(() => translateRegistrations([many], runtime()), /Registration admission count/);
});
test('pending provider registrations are rejected even when extension maps are empty', () => {
  const value = runtime(); value.pendingProviderRegistrations.push({});
  assert.throws(() => translateRegistrations([extension()], value), /pendingProviderRegistrations/);
});

test('bounded namespace and synchronous loadout callback are retained without exposure authority', () => {
  const source = extension(), callback = () => null;
  const namespace = { name: '', description: '' };
  source.tools.set('hello', { definition: { name: 'hello', description: '', parameters: {}, execute: () => {}, namespace, prepareLoadout: callback } });
  const row = translateRegistrations([source], runtime()).tools[0];
  assert.deepEqual(row.namespace, namespace); assert(Object.isFrozen(row.namespace)); assert.notEqual(row.namespace, namespace);
  assert.equal(row.hasLoadoutPreparation, true); assert.equal(row.definition.prepareLoadout, callback);
  namespace.name = 'later'; assert.equal(row.namespace.name, '');
});
test('unsupported namespace fields and malformed loadout callback reject registration', () => {
  for (const extra of [{ namespace: null }, { namespace: { name: 'g', execute: () => {} } }, { namespace: { name: 'g', description: null } }, { prepareLoadout: {} }]) {
    const source = extension(); source.tools.set('hello', { definition: { name: 'hello', description: '', parameters: {}, execute: () => {}, ...extra } });
    assert.throws(() => translateRegistrations([source], runtime()));
  }
});
test('only supported session observation handlers retain exact original function and topic', () => {
  const source = extension(), start = () => {}, tree = () => {};
  source.handlers.set('session_start', [start]); source.handlers.set('session_tree', [tree]);
  const rows = translateRegistrations([source], runtime());
  assert.deepEqual(rows.sessionHandlers.map(row => [row.callbackId, row.topic, row.extensionIndex]),
    [['session_start-1-1', 'session_start', 0], ['session_tree-1-1', 'session_tree', 0]]);
  assert.equal(rows.sessionHandlers[0].callback, start); assert.equal(rows.sessionHandlers[1].callback, tree);
  for (const topic of ['session_before_tree', 'session_shutdown', 'session_switch', 'session_before_start']) {
    const other = extension(); other.handlers.set(topic, [start]);
    assert.throws(() => translateRegistrations([other], runtime()), error => error.surface === 'on.' + topic);
  }
});
test('session observers share the complete registration transaction budget', () => {
  const source = extension(); source.handlers.set('session_start', Array(64).fill(() => {}));
  source.handlers.set('session_tree', [() => {}]);
  assert.throws(() => translateRegistrations([source], runtime()), /Registration admission count/);
});
test('original renderer functions remain admitted; malformed custom rendering diagnoses', () => {
  for (const field of ['renderCall', 'renderResult']) {
    const source = extension(), original = () => {};
    const definition = { name: 'todo', description: '', parameters: {}, execute: () => {}, [field]: original };
    source.tools.set('todo', { definition });
    assert.equal(translateRegistrations([source], runtime()).tools[0].definition[field], original);
    definition[field] = {};
    assert.throws(() => translateRegistrations([source], runtime()), error => error.surface === 'tool.todo.' + field);
  }
});