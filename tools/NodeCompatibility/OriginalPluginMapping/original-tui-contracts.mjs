import assert from 'node:assert/strict';
import { createOriginalVirtualModuleInjection } from './original-virtual-module-injection.mjs';

// The coordinator supplies reviewed genuine namespaces, invokes/joins each run
// once and records its outcome. No upstream imports or synthetic suppliers here.
export function originalTuiContractCases(admitted) {
  if (!admitted || admitted.originalTui === undefined)
    throw new TypeError('Reviewed genuine original TUI suppliers are required.');
  const modules = createOriginalVirtualModuleInjection(admitted);
  const namespace = modules['@earendil-works/pi-tui'];
  return Object.freeze([
    Object.freeze({ name: 'genuine-tui.alias-and-named-import-identity', run() {
      assert.strictEqual(namespace, modules['@mariozechner/pi-tui']);
      for (const name of ['Key', 'matchesKey', 'Text', 'Box', 'visibleWidth', 'truncateToWidth'])
        assert.strictEqual(namespace[name], admitted.originalTui[name]);
    } }),
    Object.freeze({ name: 'genuine-tui.bounded-namespace-and-no-default-import', run() {
      assert.deepEqual(Object.keys(namespace), ['Key', 'matchesKey', 'Text', 'Box', 'visibleWidth', 'truncateToWidth']);
      assert(Object.isFrozen(namespace));
      for (const name of ['default', 'TUI', 'ProcessTerminal', 'Markdown', 'dispatchMouseEvent'])
        assert(!Object.hasOwn(namespace, name));
    } }),
    Object.freeze({ name: 'genuine-tui.full-typebox-and-original-constructor-prototypes', run() {
      assert.strictEqual(modules.typebox, admitted.typebox);
      assert.strictEqual(modules['@sinclair/typebox/compile'], admitted.typeboxCompile);
      assert.strictEqual(modules['@sinclair/typebox/value'], admitted.typeboxValue);
      assert.strictEqual(namespace.Text.prototype, admitted.originalTui.Text.prototype);
      assert.strictEqual(namespace.Box.prototype, admitted.originalTui.Box.prototype);
    } }),
  ]);
}
