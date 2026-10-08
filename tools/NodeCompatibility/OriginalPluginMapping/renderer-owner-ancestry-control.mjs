// Callable-owner regression, not an installed-plugin reachability claim.
// Coordinator supplies genuine namespaces/unchanged rg definition and tickets
// from its actual native participant. No fake host leases or replacement TUI.
import assert from 'node:assert/strict';
import { AsyncLocalStorage } from 'node:async_hooks';
import { createOriginalRendererOwner } from '../../NodeCommandInputBridge/renderer-owner.mjs';
export const rendererOwnerAncestryControlName = 'source-owner.genuine-rg-self-ancestor-and-unrelated-disposal';
export async function runRendererOwnerAncestryControl({ supplier, originalDefinition, workspace, acquireTicket }) {
  assert.equal(originalDefinition?.name, 'rg'); assert.equal(typeof originalDefinition.renderCall, 'function');
  assert.equal(typeof acquireTicket, 'function');
  const realm = new AsyncLocalStorage(), genuine = supplier.ORIGINAL_SUPPLIERS, failures = [], originals = [];
  const owner = createOriginalRendererOwner({ supplier: () => supplier, currentInvocation: () => realm.getStore(),
    enterInvocation: (frame, callback) => realm.run(frame, callback), workspace: () => workspace, sourceInvalid: () => false });
  const ticket = kind => acquireTicket(kind); // Actual participant, generations, scope and host handle.
  let outer, child, unrelated, selfRejected = false, ancestorRejected = false, unrelatedClosed = false;
  const args = { pattern: 'needle', path: 'src', glob: '*.ts' }, ids = [];
  function renderer(onRender) {
    return (parameters, theme, context) => {
      const component = originalDefinition.renderCall(parameters, theme, context);
      assert(component instanceof genuine.originalTui.Text);
      const originalRender = component.render;
      // Interpose only to exercise the callable owner while its actual admission
      // is held. Same original Text instance and original render remain owned.
      if (onRender) component.render = async width => {
        assert(realm.getStore()?.rendererInternalId, 'Actual owner callback frame required');
        await onRender();
        const original = originalRender.call(component, width); originals.push(original); return await original;
      };
      return component;
    };
  }
  const create = async (callback, callId) => {
    const original = owner.toolCall(callback, 'tool-1-rg', args, { toolCallId: callId }, ticket('render_call'));
    originals.push(original); const id = await original; ids.push(id); return id;
  };
  try {
    unrelated = await create(renderer(), 'ancestry-unrelated');
    outer = await create(renderer(async () => {
      await assert.rejects(() => owner.dispose(outer, ticket('component-dispose')), /own or ancestor original/u);
      selfRejected = true; assert(owner.hasComponent(outer, ticket('component-render')));
      // Create the real child under the still-active outer callback; its retained
      // creator is that actual frame. Rendering it enters the real child frame
      // with the actual outer original in its ancestry.
      child = await create(renderer(async () => {
        await assert.rejects(() => owner.dispose(outer, ticket('component-dispose')), /own or ancestor original/u);
        ancestorRejected = true; assert(owner.hasComponent(outer, ticket('component-render')));
        const close = owner.dispose(unrelated, ticket('component-dispose')); originals.push(close); await close;
        unrelatedClosed = true; assert.equal(owner.hasComponent(unrelated, ticket('component-render')), false);
      }), 'ancestry-child');
      const childRender = owner.render(child, 80, ticket('component-render')); originals.push(childRender); await childRender;
    }), 'ancestry-outer');
    const outerRender = owner.render(outer, 80, ticket('component-render')); originals.push(outerRender);
    const rows = await outerRender;
    assert(selfRejected && ancestorRejected && unrelatedClosed);
    assert(rows.rows.length === rows.cellWidths.length && rows.cellWidths.every(width => width <= 80));
    // External disposal tickets are not themselves active component callbacks.
    for (const id of [child, outer, unrelated]) {
      const close = owner.dispose(id, ticket('component-dispose')); originals.push(close); await close;
      const repeat = owner.dispose(id, ticket('component-dispose')); originals.push(repeat); await repeat;
      assert.equal(owner.hasComponent(id, ticket('component-render')), false);
    }
  } catch (error) { failures.push(error); }
  for (const original of new Set(originals)) try { await original; } catch (error) { if (!failures.includes(error)) failures.push(error); }
  for (const id of ids) try { await owner.dispose(id, ticket('component-dispose')); } catch (error) { failures.push(error); }
  try { await owner.close(); } catch (error) { failures.push(error); }
  if (failures.length) throw new AggregateError(failures, 'Actual owner ancestry regression and original cleanup failures');
  return { caseId: rendererOwnerAncestryControlName, selfRejected, ancestorRejected, unrelatedClosed,
    externalAndRepeatedDisposalJoined: true, originalsJoined: true, installedPluginReachabilityEstablished: false };
}
