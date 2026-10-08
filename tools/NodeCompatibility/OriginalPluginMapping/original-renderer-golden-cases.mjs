// Independent source golden renderer controls. Coordinator selects one case in
// a fresh original process. Whole original extension factories and constructors
// run; rg.execute, native transport and fake host/renderer suppliers do not run.
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createCommandInputLoader } from '../../NodeCommandInputBridge/module-loader.mjs';
export const originalRendererGoldenCases = Object.freeze(['rg-call', 'rg-result-partial', 'rg-result-no-matches', 'rg-result-expanded']);
export const originalRendererFrames = Object.freeze({
  call: { args: { pattern: 'needle', path: 'src', glob: '*.ts' }, width: 80 },
  partial: { result: { content: [], details: { matchCount: 0 } }, options: { expanded: false, isPartial: true }, width: 80 },
  noMatches: { result: { content: [{ type: 'text', text: 'No matches found' }], details: { matchCount: 0 } }, options: { expanded: false, isPartial: false }, width: 80 },
  expanded: { result: { content: [{ type: 'text', text: 'src/界.ts:1:needle\nsrc/emoji.ts:2:needle 😀' }], details: { matchCount: 2 } },
    options: { expanded: true, isPartial: false }, width: 80 },
});
export async function runOriginalRendererGoldenCase(caseId, { roots, cwd, signal }) {
  assert(originalRendererGoldenCases.includes(caseId)); signal.throwIfAborted();
  const source = await createCommandInputLoader(roots), originals = [], failures = [];
  let runtime, bus, referenceLoader, report, finalization;
  try {
    const load = await source.load(cwd, 1, null, ['packages/coding-agent/examples/extensions/truncated-tool.ts']);
    assert.equal(load.tools.length, 1); assert.equal(load.tools[0].name, 'rg');
    assert.equal(load.tools[0].hasRenderCall, true); assert.equal(load.tools[0].hasRenderResult, true);
    // Obtain baseline callbacks from a second complete original factory/runtime,
    // not from a recreated registerTool API or a substitute renderer.
    const original = path => import(pathToFileURL(join(roots.oracle, 'upstream', path)).href);
    referenceLoader = await original('packages/coding-agent/src/core/extensions/loader.ts');
    const events = await original('packages/coding-agent/src/core/event-bus.ts');
    runtime = referenceLoader.createExtensionRuntime(); bus = events.createEventBus();
    const factory = referenceLoader.loadExtensions([join(roots.oracle, 'upstream/packages/coding-agent/examples/extensions/truncated-tool.ts')], cwd, bus, runtime);
    originals.push(factory); const loaded = await factory; assert.deepEqual(loaded.errors, []); assert.equal(loaded.extensions.length, 1);
    const definition = loaded.extensions[0].tools.get('rg').definition;
    const supplied = await import('./live-original-virtual-modules.mjs');
    const environment = supplied.createOriginalRendererEnvironment(), genuine = supplied.ORIGINAL_SUPPLIERS;
    assert(environment.theme instanceof genuine.originalTheme.Theme);
    assert(environment.keybindings instanceof genuine.originalKeybindings.KeybindingsManager);
    assert.strictEqual(supplied.VIRTUAL_MODULES['@earendil-works/pi-tui'].Text, genuine.originalTui.Text);
    const frame = caseId === 'rg-call' ? originalRendererFrames.call : caseId === 'rg-result-partial' ? originalRendererFrames.partial
      : caseId === 'rg-result-no-matches' ? originalRendererFrames.noMatches : originalRendererFrames.expanded;
    let component;
    try {
      const context = { args: originalRendererFrames.call.args, toolCallId: 'original-golden-rg', state: {}, lastComponent: undefined,
        cwd, executionStarted: caseId !== 'rg-call', argsComplete: true, isPartial: frame.options?.isPartial ?? false,
        expanded: frame.options?.expanded ?? false, showImages: false, isError: false,
        invalidate: () => assert.fail('Unchanged original rg renderers require no invalidation effect') };
      component = caseId === 'rg-call' ? definition.renderCall(frame.args, environment.theme, context)
        : definition.renderResult(frame.result, frame.options, environment.theme, context);
      assert(component instanceof genuine.originalTui.Text, 'Exact original Text instance required');
      const render = component.render(frame.width); originals.push(render); const rows = await render;
      assert(Array.isArray(rows) && rows.every(row => typeof row === 'string'));
      const cellWidths = rows.map(row => genuine.originalTui.visibleWidth(row));
      assert(cellWidths.every(value => Number.isSafeInteger(value) && value >= 0 && value <= frame.width));
      report = { caseId, frame, rows, cellWidths, originalTextInstance: true, originalThemeInstance: true,
        originalKeybindingsInstance: true, completeOriginalFactories: 2, toolCallbackId: load.tools[0].callbackId,
        toolCallId: 'original-golden-rg', rendererFlagsRetained: true, executeInvoked: false,
        nativeHostQualified: false, rendererTransportQualified: false };
    } catch (error) { failures.push(error); }
    if (typeof component?.dispose === 'function') try { const dispose = component.dispose(); originals.push(dispose); await dispose; } catch (error) { failures.push(error); }
  } catch (error) { failures.push(error); }
  // Keep every actual source original and cleanup failure, including failed factory/render.
  for (const original of new Set(originals)) try { await original; } catch (error) { if (!failures.includes(error)) failures.push(error); }
  try { runtime?.invalidate(); bus?.clear(); referenceLoader?.clearExtensionCache(); } catch (error) { failures.push(error); }
  try { finalization = await source.finalize(); } catch (error) { failures.push(error); }
  if (failures.length) throw new AggregateError(failures, 'Original renderer golden originals failed');
  signal.throwIfAborted(); assert.equal(finalization.immutableInputsVerified, true);
  return { ...report, finalization, originalsJoined: true, freshProcessRequired: true };
}
