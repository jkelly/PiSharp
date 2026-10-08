// Coordinator selects ONE case in a fresh owned process with exact pinned
// roots. This invokes the actual source loader; it does not start a process,
// emulate Jiti/factories, or qualify the native host/renderer transport.
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { createCommandInputLoader } from '../../NodeCommandInputBridge/module-loader.mjs';
import { originalExportContractCases } from './original-export-contracts.mjs';
import { originalTuiContractCases } from './original-tui-contracts.mjs';

export const originalLivePluginCases = Object.freeze([
  'legacy-default-route', 'bounded-hello-and-genuine-imports',
  'unchanged-todo-lifecycle-diagnostic', 'unchanged-truncated-tool-renderer-diagnostic',
]);

export async function runOriginalLivePluginCase(caseId, { roots, cwd, signal, uiCapabilities }) {
  assert(originalLivePluginCases.includes(caseId), 'Unknown original live plugin case');
  assert(signal && !signal.aborted);
  const source = await createCommandInputLoader(roots);
  let result, finalization;
  try {
    if (caseId === 'legacy-default-route') {
      result = await source.load(cwd, 1, null);
      assert.equal(result.virtualModuleReplacement.replacement, join(roots.reference, 'tools/NodeExtensionReference/controlled-virtual-modules.mjs'));
      assert(!Object.hasOwn(result.virtualModuleReplacement, 'route'));
      assert.equal(result.commands[0].callbackId, 'commands-1');
      assert.equal(result.inputHandlers[0].callbackId, 'input-transform-1');
      assert.equal(result.successfulSourceFactoryInvocations, 2);
    } else if (caseId === 'bounded-hello-and-genuine-imports') {
      result = await source.load(cwd, 1, null, ['packages/coding-agent/examples/extensions/hello.ts']);
      assert.equal(result.virtualModuleReplacement.route, 'bounded-original-suppliers-1');
      assert.equal(result.successfulSourceFactoryInvocations, 1);
      assert.equal(result.tools.length, 1); assert.equal(result.tools[0].name, 'hello');
      const provider = await import('./live-original-virtual-modules.mjs');
      const admitted = provider.ORIGINAL_SUPPLIERS, controls = [
        ...originalExportContractCases({ Type: admitted.typebox.Type, defineTool: admitted.originalTypes.defineTool,
          StringEnum: admitted.originalAi.StringEnum, truncation: admitted.originalTruncation }),
        ...originalTuiContractCases(admitted),
      ];
      result.originalImportControls = [];
      for (const control of controls) {
        control.run(); result.originalImportControls.push({ name: control.name, status: 'fulfilled' });
      }
      const modules = provider.VIRTUAL_MODULES;
      assert.strictEqual(modules['@earendil-works/pi-coding-agent'].withFileMutationQueue, admitted.withFileMutationQueue);
      assert.strictEqual(modules['@mariozechner/pi-coding-agent'].withFileMutationQueue, admitted.withFileMutationQueue);
      const prepared = source.prepare(result.tools[0].callbackId, { name: 'Joe' }, signal);
      assert.equal(prepared.status, 'fulfilled');
      const invoked = await source.invoke('tool', result.tools[0].callbackId, JSON.parse(prepared.preparedJson),
        undefined, undefined, uiCapabilities, signal, async () => assert.fail('Original hello requires no host operation'), 'original-hello-call');
      assert.equal(invoked.status, 'fulfilled');
      assert.deepEqual(JSON.parse(invoked.resultJson), { content: [{ type: 'text', text: 'Hello, Joe!' }], details: { greeted: 'Joe' } });
      result.prepared = prepared; result.invoked = invoked;
    } else {
      const todo = caseId === 'unchanged-todo-lifecycle-diagnostic';
      const path = 'packages/coding-agent/examples/extensions/' + (todo ? 'todo.ts' : 'truncated-tool.ts');
      const surface = todo ? 'on.session_start' : 'tool.rg.renderCall';
      let originalFailure;
      await assert.rejects(() => source.load(cwd, 1, null, [path]), error => {
        originalFailure = error;
        return error.bridgeCode === 'UnsupportedRegistration' && error.surface === surface;
      });
      result = { sourcePath: path, expectedUnsupportedSurface: surface, status: 'rejected',
        originalFailure: source.observe(originalFailure) };
    }
  } finally {
    finalization = await source.finalize();
    assert.equal(finalization.immutableInputsVerified, true);
    assert.equal(finalization.invalidated, true); assert.equal(finalization.clockRestored, true);
  }
  return { caseId, result, finalization, nativeHostQualified: false, rendererQualified: false };
}
