import test from 'node:test';
import assert from 'node:assert/strict';
import { createOriginalVirtualModuleInjection } from './original-virtual-module-injection.mjs';

// Synthetic reference suppliers; these do not qualify the upstream algorithms.
function supplied() {
  const Type = { Object() {}, String() {} };
  return { typebox: { Type, OtherTypeboxExport: Symbol('full namespace') },
    typeboxCompile: { Compile() {} }, typeboxValue: { Check() {} },
    originalAi: { Type, StringEnum() {} }, originalTypes: { defineTool: value => value },
    originalTruncation: { DEFAULT_MAX_BYTES: 51200, DEFAULT_MAX_LINES: 2000,
      formatSize() {}, truncateHead() {}, truncateTail() {}, truncateLine() {} } };
}

test('original-injection.preserves-all-existing-full-typebox-namespaces', () => {
  const input = supplied(), modules = createOriginalVirtualModuleInjection(input);
  for (const name of ['typebox', '@sinclair/typebox']) assert.strictEqual(modules[name], input.typebox);
  for (const name of ['typebox/compile', '@sinclair/typebox/compile']) assert.strictEqual(modules[name], input.typeboxCompile);
  for (const name of ['typebox/value', '@sinclair/typebox/value']) assert.strictEqual(modules[name], input.typeboxValue);
  assert.strictEqual(modules.typebox.OtherTypeboxExport, input.typebox.OtherTypeboxExport);
  assert.equal(Object.keys(modules).length, 10);
  assert(Object.isFrozen(modules));
  assert(!Object.isFrozen(input.typebox)); // Caller namespaces are not mutated.
});

test('original-injection.retains-shared-original-ai-and-tool-function-references', () => {
  const input = supplied(), modules = createOriginalVirtualModuleInjection(input);
  const ai = modules['@earendil-works/pi-ai'], tools = modules['@earendil-works/pi-coding-agent'];
  assert.strictEqual(ai, modules['@mariozechner/pi-ai']);
  assert.strictEqual(tools, modules['@mariozechner/pi-coding-agent']);
  assert.strictEqual(ai.StringEnum, input.originalAi.StringEnum);
  assert.strictEqual(tools.defineTool, input.originalTypes.defineTool);
  for (const name of ['formatSize', 'truncateHead', 'truncateTail', 'truncateLine'])
    assert.strictEqual(tools[name], input.originalTruncation[name]);
  assert(Object.isFrozen(ai)); assert(Object.isFrozen(tools));
  assert(!Object.hasOwn(modules, '@earendil-works/pi-tui'));
  assert(!Object.hasOwn(tools, 'truncateMiddle'));
});

test('original-injection.refuses-incomplete-or-conflicting-suppliers', () => {
  for (const name of ['typebox', 'typeboxCompile', 'typeboxValue', 'originalAi', 'originalTypes', 'originalTruncation']) {
    const input = supplied(); delete input[name];
    assert.throws(() => createOriginalVirtualModuleInjection(input), TypeError);
  }
  const mismatched = supplied(); mismatched.originalAi.Type = { Object() {}, String() {} };
  assert.throws(() => createOriginalVirtualModuleInjection(mismatched), TypeError);
  const inherited = supplied(); inherited.originalTypes = Object.create(inherited.originalTypes);
  assert.throws(() => createOriginalVirtualModuleInjection(inherited), TypeError);
});

test('original-injection.does-not-invoke-supplied-original-functions', () => {
  const input = supplied(), fault = new Error('exact original fault'); let calls = 0;
  input.originalAi.StringEnum = () => { calls++; throw fault; };
  const modules = createOriginalVirtualModuleInjection(input);
  assert.equal(calls, 0);
  assert.throws(() => modules['@earendil-works/pi-ai'].StringEnum(), error => error === fault);
  assert.equal(calls, 1);
});
