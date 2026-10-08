import test from 'node:test';
import assert from 'node:assert/strict';
import { createOriginalPluginMapping, activateDefaultPlugin } from './original-plugin-mapping.mjs';

function supplied() {
  const Type = Object.freeze({
    Object: properties => ({ type: 'object', properties }),
    String: options => ({ type: 'string', ...options }),
  });
  const defineTool = value => value;
  return { Type, defineTool };
}

test('original-plugin-mapping.alias-pairs-share-namespace-and-supplied-export-identities', () => {
  const input = supplied(), map = createOriginalPluginMapping(input);
  assert.strictEqual(map.resolve('typebox'), map.resolve('@sinclair/typebox'));
  assert.strictEqual(map.resolve('@earendil-works/pi-ai'), map.resolve('@mariozechner/pi-ai'));
  assert.strictEqual(map.resolve('@earendil-works/pi-coding-agent'), map.resolve('@mariozechner/pi-coding-agent'));
  assert.strictEqual(map.named('@mariozechner/pi-ai', 'Type'), input.Type);
  assert.strictEqual(map.named('@mariozechner/pi-coding-agent', 'defineTool'), input.defineTool);
  const probe = Object.freeze({ identity: true });
  assert.strictEqual(map.named('@mariozechner/pi-coding-agent', 'defineTool')(probe), probe);
});

test('original-plugin-mapping.synthetic-hello-consumer-registers-and-executes-through-legacy-map', async () => {
  const map = createOriginalPluginMapping(supplied());
  const Type = map.named('@mariozechner/pi-ai', 'Type');
  const defineTool = map.named('@mariozechner/pi-coding-agent', 'defineTool');
  // Synthetic consumer of the pinned hello.ts runtime imports/default shape.
  // It does not claim that the unchanged original TypeScript was loaded or executed.
  const tool = defineTool({ name: 'hello', parameters: Type.Object({ name: Type.String({ description: 'Name to greet' }) }),
    async execute(_id, params) { return { content: [{ type: 'text', text: `Hello, ${params.name}!` }], details: { greeted: params.name } }; } });
  const registered = [];
  activateDefaultPlugin({ default: pi => pi.registerTool(tool) }, { registerTool: value => registered.push(value) });
  assert.strictEqual(registered[0], tool);
  assert.deepEqual(await registered[0].execute('call', { name: 'Joe' }),
    { content: [{ type: 'text', text: 'Hello, Joe!' }], details: { greeted: 'Joe' } });
});

test('original-plugin-mapping.refuses-neighbor-modules-and-exports-before-consumer-effects', () => {
  const map = createOriginalPluginMapping(supplied());
  for (const module of ['@earendil-works/pi-tui', '@mariozechner/pi-ai/oauth', 'typebox/compile', '../typebox', '__proto__'])
    assert.throws(() => map.resolve(module), TypeError);
  for (const name of ['getModel', 'default', 'constructor', '__proto__'])
    assert.throws(() => map.named('@mariozechner/pi-ai', name), TypeError);
  assert(Object.isFrozen(map.modules));
  assert(Object.isFrozen(map.resolve('@mariozechner/pi-ai')));
});

test('original-plugin-mapping.default-only-selection-and-single-acquisition', () => {
  let reads = 0, calls = 0;
  const api = Object.freeze({ admitted: true });
  const value = Object.freeze({ marker: true });
  const module = { get default() { reads++; return received => { calls++; assert.strictEqual(received, api); return value; }; } };
  assert.strictEqual(activateDefaultPlugin(module, api), value);
  assert.equal(reads, 1); assert.equal(calls, 1);
  assert.throws(() => activateDefaultPlugin({ activate() { calls++; } }, api), TypeError);
  assert.throws(() => activateDefaultPlugin(Object.create({ default() { calls++; } }), api), TypeError);
  assert.equal(calls, 1);
});

test('original-plugin-mapping.preserves-synchronous-fault-and-original-promise-reference', async () => {
  const error = new Error('synthetic original');
  assert.throws(() => activateDefaultPlugin({ default() { throw error; } }, {}), value => value === error);
  let settle;
  const original = new Promise(resolve => { settle = resolve; });
  let called = 0;
  const returned = activateDefaultPlugin({ default() { called++; return original; } }, {});
  assert.strictEqual(returned, original); assert.equal(called, 1);
  settle('joined'); assert.equal(await returned, 'joined');
  const rejection = Promise.reject(error);
  assert.strictEqual(activateDefaultPlugin({ default: () => rejection }, {}), rejection);
  await assert.rejects(rejection, value => value === error);
});

test('original-plugin-mapping.StringEnum-shares-original-function-and-schema-reference', () => {
  const input = supplied(), values = Object.freeze(['list', 'add', 'toggle', 'clear']), options = Object.freeze({ description: 'Action' });
  const schema = Object.freeze({ identity: true }); let calls = 0;
  const StringEnum = (received, opts) => { calls++; assert.strictEqual(received, values); assert.strictEqual(opts, options); return schema; };
  const map = createOriginalPluginMapping({ ...input, StringEnum });
  for (const specifier of ['@earendil-works/pi-ai', '@mariozechner/pi-ai']) {
    assert.strictEqual(map.named(specifier, 'StringEnum'), StringEnum);
    assert.strictEqual(map.named(specifier, 'StringEnum')(values, options), schema);
  }
  assert.equal(calls, 2); assert.strictEqual(map.resolve('@earendil-works/pi-ai'), map.resolve('@mariozechner/pi-ai'));
});
test('original-plugin-mapping.absent-or-invalid-StringEnum-is-never-fabricated', () => {
  assert.throws(() => createOriginalPluginMapping(supplied()).named('@earendil-works/pi-ai', 'StringEnum'), TypeError);
  for (const StringEnum of [null, {}, 'function', false]) assert.throws(() => createOriginalPluginMapping({ ...supplied(), StringEnum }), TypeError);
});
test('original-plugin-mapping.StringEnum-fault-identity-and-call-time-behavior-survive', () => {
  const error = new Error('supplied schema failure'); let calls = 0;
  const StringEnum = () => { calls++; throw error; }, map = createOriginalPluginMapping({ ...supplied(), StringEnum });
  assert.equal(calls, 0); assert.throws(() => map.named('@mariozechner/pi-ai', 'StringEnum')([]), value => value === error); assert.equal(calls, 1);
});
function truncationSpy() {
  const value = Object.freeze({ sameResult: true });
  const exports = { DEFAULT_MAX_BYTES: 51200, DEFAULT_MAX_LINES: 2000 };
  for (const name of ['formatSize', 'truncateHead', 'truncateTail', 'truncateLine']) exports[name] = (...args) => { assert.equal(args[0], 'same-input'); return value; };
  return { exports, value };
}
test('original-plugin-mapping.truncation-public-exports-share-alias-and-original-result-identities', () => {
  const { exports, value } = truncationSpy(), map = createOriginalPluginMapping({ ...supplied(), truncation: exports });
  for (const specifier of ['@earendil-works/pi-coding-agent', '@mariozechner/pi-coding-agent']) {
    for (const name of ['formatSize', 'truncateHead', 'truncateTail', 'truncateLine']) {
      assert.strictEqual(map.named(specifier, name), exports[name]); assert.strictEqual(map.named(specifier, name)('same-input'), value);
    }
    assert.equal(map.named(specifier, 'DEFAULT_MAX_BYTES'), 51200); assert.equal(map.named(specifier, 'DEFAULT_MAX_LINES'), 2000);
    for (const name of ['truncateMiddle', 'GREP_MAX_LINE_LENGTH']) assert.throws(() => map.named(specifier, name), TypeError);
  }
});
test('original-plugin-mapping.truncation-captures-export-references-and-rejects-missing-or-inherited-fields', () => {
  const { exports } = truncationSpy(), original = exports.truncateHead;
  const map = createOriginalPluginMapping({ ...supplied(), truncation: exports }); exports.truncateHead = () => assert.fail('replacement');
  assert.strictEqual(map.named('@earendil-works/pi-coding-agent', 'truncateHead'), original);
  assert.throws(() => createOriginalPluginMapping({ ...supplied(), truncation: Object.create(exports) }), TypeError);
  for (const name of Object.keys(exports)) {
    const missing = { ...exports }; delete missing[name]; assert.throws(() => createOriginalPluginMapping({ ...supplied(), truncation: missing }), TypeError);
  }
  for (const truncation of [null, false, { ...exports, DEFAULT_MAX_BYTES: 1 }]) assert.throws(() => createOriginalPluginMapping({ ...supplied(), truncation }), TypeError);
});
