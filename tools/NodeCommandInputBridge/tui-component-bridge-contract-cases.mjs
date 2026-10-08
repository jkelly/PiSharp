// Authored source-boundary scaffolding only. Caller supplies the frozen TUI
// owner's actual bridge, genuine environment and unchanged retained rg tool.
// No bridge, renderer, theme, native lease or wire implementation is recreated.
import assert from 'node:assert/strict';

export function rendererProtocolContractCases({ createTuiComponentBridge, bridgeOptions, originalDefinition, callFrame, resultFrames }) {
  if (typeof createTuiComponentBridge !== 'function' || !bridgeOptions || !callFrame ||
      originalDefinition?.name !== 'rg' || typeof originalDefinition.renderCall !== 'function' ||
      typeof originalDefinition.renderResult !== 'function')
    throw new TypeError('Actual reviewed bridge and unchanged original rg renderer suppliers are required.');
  for (const name of ['partial', 'no-matches', 'expanded']) {
    if (!resultFrames || !Object.hasOwn(resultFrames, name)) throw new TypeError('Reviewed renderer input frame required: ' + name);
  }
  const cases = [];
  const add = (name, action, maximumComponents) => cases.push(Object.freeze({ name, async run() {
    const bridge = createTuiComponentBridge({ ...bridgeOptions, ...(maximumComponents === undefined ? {} : { maximumComponents }) });
    const failures = []; let result; try { result = await action(bridge); } catch (error) { failures.push(error); } try { await bridge.close(); } catch (error) { failures.push(error); } if (failures.length) throw new AggregateError(failures, name); return result;
  } }));
  async function expectedRows(renderer, args, frame, result) {
    const env = bridgeOptions.environmentFor(bridgeOptions.identity);
    const component = bridgeOptions.invokeInOwner(null, () => result
      ? renderer(args, frame.options, env.theme, frame.context) : renderer(args, env.theme, frame.context));
    try { return await bridgeOptions.invokeInOwner(null, () => component.render(frame.width)); }
    finally {
      if (typeof component.dispose === 'function') await bridgeOptions.invokeInOwner(null, () => component.dispose());
    }
  }
  add('genuine-rg.render-call-matches-original-owner-realm-rows', async bridge => {
    const expected = await expectedRows(originalDefinition.renderCall, callFrame.args, callFrame, false);
    const id = await bridge.toolCall(originalDefinition.renderCall, callFrame.args, callFrame.context);
    const rows = await bridge.render(id, callFrame.width);
    assert.deepEqual(rows.rows, expected); assert.deepEqual(rows.cellWidths, expected.map(bridge.originalTui.visibleWidth));
    assert.strictEqual(bridge.originals.find(row => row.kind === 'render' && row.componentId === id).original, rows.rows);
    await bridge.dispose(id);
    return { componentId: id, rows };
  });
  for (const name of ['partial', 'no-matches', 'expanded']) add('genuine-rg.render-result-' + name, async bridge => {
    const frame = resultFrames[name];
    const expected = await expectedRows(originalDefinition.renderResult, frame.result, frame, true);
    const id = await bridge.toolResult(originalDefinition.renderResult, frame.result, frame.options, frame.context);
    const rows = await bridge.render(id, frame.width);
    assert.deepEqual(rows.rows, expected); assert.deepEqual(rows.cellWidths, expected.map(bridge.originalTui.visibleWidth)); await bridge.dispose(id);
    return { componentId: id, rows };
  });
  add('genuine-rg.same-dispose-original-and-stale-handle-refusal', async bridge => {
    const id = await bridge.toolCall(originalDefinition.renderCall, callFrame.args, callFrame.context);
    const originalClose = bridge.dispose(id);
    assert.strictEqual(bridge.dispose(id), originalClose); await originalClose;
    await assert.rejects(() => bridge.render(id, callFrame.width), /Retired component handle/);
    return { componentId: id, closeJoined: true };
  });
  // This is an intended lifecycle requirement, not a claim the observed draft
  // passes: its current closed tool slots still consume slots.size capacity.
  add('genuine-rg.disposed-slot-releases-live-component-capacity', async bridge => {
    const first = await bridge.toolCall(originalDefinition.renderCall, callFrame.args, callFrame.context);
    await bridge.dispose(first);
    const next = await bridge.toolCall(originalDefinition.renderCall, callFrame.args, callFrame.context);
    assert.notEqual(next, first); await bridge.render(next, callFrame.width); await bridge.dispose(next);
    return { first, next };
  }, 1);
  return Object.freeze(cases);
}

