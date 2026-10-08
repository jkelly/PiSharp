import test from 'node:test';
import assert from 'node:assert/strict';
import { createOriginalPluginMapping, activateDefaultPlugin } from './original-plugin-mapping.mjs';
import { createOriginalVirtualModuleInjection } from './original-virtual-module-injection.mjs';

// Synthetic supplied namespaces only; no upstream module loading or rendering.
function supplied() {
  const Type = { Object() {}, String() {} };
  return { Type, defineTool: value => value, tui: { Key: { enter: 'enter' },
    matchesKey() {}, Text: class Text {}, Box: class Box {}, visibleWidth() {}, truncateToWidth() {},
    NeighborExport: Symbol('unavailable') } };
}

test('original-tui.alias-and-namespace-imports-retain-each-supplied-object', () => {
  const input = supplied(), map = createOriginalPluginMapping(input);
  const namespace = map.resolve('@earendil-works/pi-tui');
  assert.strictEqual(namespace, map.resolve('@mariozechner/pi-tui'));
  assert.deepEqual(Object.keys(namespace), ['Key', 'matchesKey', 'Text', 'Box', 'visibleWidth', 'truncateToWidth']);
  for (const name of Object.keys(namespace)) {
    assert.strictEqual(namespace[name], input.tui[name]);
    assert.strictEqual(map.named('@mariozechner/pi-tui', name), input.tui[name]);
  }
  assert(Object.isFrozen(namespace));
  assert(!Object.isFrozen(input.tui.Key));
  assert.throws(() => map.named('@earendil-works/pi-tui', 'NeighborExport'), TypeError);
  assert.equal(Object.keys(map.modules).length, 8);
});

test('original-tui.no-default-export-is-fabricated-and-plugin-default-remains-exact', () => {
  const input = supplied(), map = createOriginalPluginMapping(input);
  const namespace = map.resolve('@earendil-works/pi-tui');
  assert(!Object.hasOwn(namespace, 'default'));
  assert.throws(() => map.named('@earendil-works/pi-tui', 'default'), TypeError);
  assert.throws(() => activateDefaultPlugin(namespace, {}), TypeError);
  const api = {}, result = {}, calls = [];
  const factory = actualApi => { calls.push(actualApi); return result; };
  assert.strictEqual(activateDefaultPlugin({ default: factory }, api), result);
  assert.deepEqual(calls, [api]);
});

test('original-tui.optional-absence-and-incomplete-or-inherited-exports-fail-closed', () => {
  const absent = supplied(); delete absent.tui;
  assert.throws(() => createOriginalPluginMapping(absent).resolve('@earendil-works/pi-tui'), TypeError);
  for (const name of ['Key', 'matchesKey', 'Text', 'Box', 'visibleWidth', 'truncateToWidth']) {
    const missing = supplied(); delete missing.tui[name];
    assert.throws(() => createOriginalPluginMapping(missing), TypeError);
    const wrong = supplied(); wrong.tui[name] = null;
    assert.throws(() => createOriginalPluginMapping(wrong), TypeError);
    const inherited = supplied(); const original = inherited.tui[name]; delete inherited.tui[name];
    Object.setPrototypeOf(inherited.tui, { [name]: original });
    assert.throws(() => createOriginalPluginMapping(inherited), TypeError);
  }
  for (const tui of [null, [], () => {}]) assert.throws(() => createOriginalPluginMapping({ ...supplied(), tui }), TypeError);
});

test('original-tui.captures-once-without-construction-or-function-invocation', () => {
  const input = supplied(), fault = new Error('original fault'); let reads = 0, calls = 0;
  const original = () => { calls++; throw fault; };
  Object.defineProperty(input.tui, 'matchesKey', { get() { reads++; return original; } });
  input.tui.Text = class Text { constructor() { calls++; throw fault; } };
  const map = createOriginalPluginMapping(input), namespace = map.resolve('@earendil-works/pi-tui');
  assert.equal(reads, 1); assert.equal(calls, 0);
  assert.strictEqual(namespace.matchesKey, original);
  assert.throws(() => namespace.matchesKey('data', 'enter'), error => error === fault);
  assert.throws(() => new namespace.Text(), error => error === fault);
  assert.equal(calls, 2); assert.equal(reads, 1);
});

test('original-tui.injection-preserves-original-tui-and-full-typebox-aliases', () => {
  const input = supplied();
  const admitted = { typebox: { Type: input.Type, Extra: Symbol('retained') }, typeboxCompile: {}, typeboxValue: {},
    originalAi: { Type: input.Type, StringEnum() {} }, originalTypes: { defineTool: input.defineTool },
    originalTruncation: { DEFAULT_MAX_BYTES: 51200, DEFAULT_MAX_LINES: 2000,
      formatSize() {}, truncateHead() {}, truncateTail() {}, truncateLine() {} }, originalTui: input.tui };
  const modules = createOriginalVirtualModuleInjection(admitted);
  assert.equal(Object.keys(modules).length, 12);
  assert.strictEqual(modules.typebox, admitted.typebox);
  assert.strictEqual(modules['@sinclair/typebox/compile'], admitted.typeboxCompile);
  assert.strictEqual(modules['@sinclair/typebox/value'], admitted.typeboxValue);
  assert.strictEqual(modules['@earendil-works/pi-tui'], modules['@mariozechner/pi-tui']);
  for (const name of ['Key', 'matchesKey', 'Text', 'Box', 'visibleWidth', 'truncateToWidth'])
    assert.strictEqual(modules['@earendil-works/pi-tui'][name], input.tui[name]);
});
