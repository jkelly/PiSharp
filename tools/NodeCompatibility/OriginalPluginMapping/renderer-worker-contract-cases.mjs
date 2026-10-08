// Coordinator supplies the actual held full-duplex worker peer and original rg
// definition/environment. No source loader, bridge, renderer or theme is replaced.
import assert from 'node:assert/strict';

export function rendererWorkerContractCases({ request, identity, callbackId, handle, originalDefinition,
  originalEnvironment, originalTui, invokeInOriginalRealm, callFrame, resultFrames }) {
  assert.equal(typeof request, 'function'); assert.equal(typeof invokeInOriginalRealm, 'function');
  assert.equal(originalDefinition?.name, 'rg'); assert.equal(typeof originalDefinition.renderCall, 'function');
  assert.equal(typeof originalDefinition.renderResult, 'function'); assert.equal(typeof originalTui?.visibleWidth, 'function');
  assert(originalEnvironment?.theme && identity && handle && callbackId && callFrame);
  assert.equal(typeof identity.scopeId, 'string'); assert(Number.isSafeInteger(identity.nativeSessionGeneration) && identity.nativeSessionGeneration > 0);
  for (const name of ['partial', 'no-matches', 'expanded']) assert(resultFrames?.[name], 'Genuine result frame required: ' + name);
  let sequence = 0;
  const value = fields => ({ ...identity, operationId: 'genuine-render-control-' + (++sequence), ...fields });
  async function joined(method, fields, capability) {
    const supplied = value(fields);
    let response, primary;
    try { response = await request(method, supplied, capability); } catch (error) { primary = error; }
    // Invalid admission has no operation; successfully admitted cases always settle the same ID.
    if (response) {
      const settled = await request('command-input.settle', { operationId: supplied.operationId });
      assert.equal(settled.settled, true); assert.equal(settled.status, response.status);
    }
    if (primary) throw primary; return response;
  }
  async function originalRows(frame, result) {
    const component = await invokeInOriginalRealm(() => result
      ? originalDefinition.renderResult(frame.result, frame.options, originalEnvironment.theme, frame.context)
      : originalDefinition.renderCall(frame.args, originalEnvironment.theme, frame.context));
    try {
      const rows = await invokeInOriginalRealm(() => component.render(frame.width));
      const cellWidths = await invokeInOriginalRealm(() => rows.map(row => originalTui.visibleWidth(row)));
      return { rows, cellWidths };
    } finally { if (typeof component.dispose === 'function') await invokeInOriginalRealm(() => component.dispose()); }
  }
  async function create(frame, result) {
    const response = await joined(result ? 'command-input.render-result' : 'command-input.render-call', {
      callbackId, renderContextJson: JSON.stringify(frame.transportContext), ...(result
        ? { resultJson: JSON.stringify(frame.result), optionsJson: JSON.stringify(frame.options) }
        : { argumentsJson: JSON.stringify(frame.args) }),
    }, handle);
    assert.equal(response.status, 'fulfilled'); assert.equal(typeof response.componentId, 'string'); return response.componentId;
  }
  const positive = (name, frame, result) => Object.freeze({ name, async run() {
    const expected = await originalRows(frame, result), componentId = await create(frame, result);
    try {
      const rendered = await joined('command-input.component-render', { componentId, width: frame.width });
      assert.equal(rendered.status, 'fulfilled'); assert.deepEqual(rendered.render, expected);
    } finally {
      const disposed = await joined('command-input.component-dispose', { componentId });
      assert.equal(disposed.status, 'fulfilled');
    }
  } });
  return Object.freeze([
    positive('worker.genuine-rg-call-rows-and-original-cell-widths', callFrame, false),
    ...['partial', 'no-matches', 'expanded'].map(name => positive('worker.genuine-rg-result-' + name,
      resultFrames[name], true)),
    Object.freeze({ name: 'worker.disposal-tombstone-and-stale-render-refusal', async run() {
      const componentId = await create(callFrame, false);
      assert.equal((await joined('command-input.component-dispose', { componentId })).status, 'fulfilled');
      // Loader disposal must return the same original close task, not re-invoke source disposal.
      assert.equal((await joined('command-input.component-dispose', { componentId })).status, 'fulfilled');
      const stale = await joined('command-input.component-render', { componentId, width: callFrame.width });
      assert.equal(stale.status, 'rejected'); assert.match(stale.thrown.message, /Retired source component/u);
    } }),
    Object.freeze({ name: 'worker.forged-scope-or-native-session-component-refused', async run() {
      const componentId = await create(callFrame, false), failures = [];
      try {
        const wrongScope = await joined('command-input.component-render', { componentId, width: callFrame.width, scopeId: identity.scopeId + '.forged' });
        const wrongSession = await joined('command-input.component-render', { componentId, width: callFrame.width,
          nativeSessionGeneration: identity.nativeSessionGeneration === 1 ? 2 : 1 });
        assert.equal(wrongScope.status, 'rejected'); assert.equal(wrongSession.status, 'rejected');
      } catch (error) { failures.push(error); }
      try { assert.equal((await joined('command-input.component-dispose', { componentId })).status, 'fulfilled'); }
      catch (error) { failures.push(error); }
      if (failures.length) throw new AggregateError(failures, 'Scope/session control and original disposal failures');
    } }),
    Object.freeze({ name: 'worker.rejected-renderer-context-releases-source-reservation', async run() {
      const baseline = await create(callFrame, false);
      assert.equal((await joined('command-input.component-dispose', { componentId: baseline })).status, 'fulfilled');
      // More than the real 16-component capacity: failed metadata admission must
      // not reserve source slots or invoke the genuine renderer factory.
      for (let index = 0; index < 17; index++) {
        const response = await joined('command-input.render-call', { callbackId,
          argumentsJson: JSON.stringify(callFrame.args),
          renderContextJson: JSON.stringify({ ...callFrame.transportContext, unsupportedContext: true }) }, handle);
        assert.equal(response.status, 'rejected');
        assert.match(response.thrown.message, /Unsupported original renderer context metadata/u);
      }
      const componentId = await create(callFrame, false), failures = [];
      try {
        const response = await joined('command-input.component-render', { componentId, width: callFrame.width });
        assert.equal(response.status, 'fulfilled'); assert.deepEqual(response.render, await originalRows(callFrame, false));
      } catch (error) { failures.push(error); }
      try { assert.equal((await joined('command-input.component-dispose', { componentId })).status, 'fulfilled'); }
      catch (error) { failures.push(error); }
      if (failures.length) throw new AggregateError(failures, 'Original reservation control and disposal failures');
    } }),
  ]);
}

// Additional real-peer identity controls. The native probe must dispatch the
// supplied handle through the actual native registered callback table. It must
// not validate a copied table or a stub predicate. No new worker method is used.
export function rendererWorkerIdentityContractCases({ request, identity, callbackId, handle, callFrame,
  invokeRegisteredNativeHandle }) {
  assert.equal(typeof request, 'function'); assert.equal(typeof invokeRegisteredNativeHandle, 'function');
  assert(identity && handle && callFrame); assert.equal(typeof callbackId, 'string');
  assert.equal(typeof identity.scopeId, 'string'); assert(Number.isSafeInteger(identity.nativeSessionGeneration) && identity.nativeSessionGeneration > 0);
  assert.notEqual(handle.callbackId, callbackId, 'Actual native and source callback IDs must be distinct');
  assert.equal(handle.ownerId, identity.ownerId); assert.equal(handle.ownerGeneration, identity.ownerGeneration);
  let sequence = 0;
  const value = fields => ({ ...identity, operationId: 'genuine-render-identity-' + (++sequence), ...fields });
  const renderValue = sourceId => value({ callbackId: sourceId, argumentsJson: JSON.stringify(callFrame.args),
    renderContextJson: JSON.stringify(callFrame.transportContext) });
  async function settled(method, supplied, capability) {
    const response = await request(method, supplied, capability);
    const fence = await request('command-input.settle', { operationId: supplied.operationId });
    assert.equal(fence.settled, true); assert.equal(fence.status, response.status); return response;
  }
  async function genuineBaseline() {
    // Every negative case first proves this fresh peer, native registry and actual renderer work.
    await invokeRegisteredNativeHandle(handle);
    const response = await settled('command-input.render-call', renderValue(callbackId), handle);
    assert.equal(response.status, 'fulfilled'); assert.equal(typeof response.componentId, 'string');
    const disposed = await settled('command-input.component-dispose', value({ componentId: response.componentId }));
    assert.equal(disposed.status, 'fulfilled');
  }
  return Object.freeze([
    Object.freeze({ name: 'worker.distinct-native-and-source-callback-ids-accepted', async run() {
      // Authenticate the real native capability separately; then admit the genuine source renderer.
      await genuineBaseline();
    } }),
    Object.freeze({ name: 'worker.forged-source-renderer-id-refused', async run() {
      // Valid native capability cannot authorize a callback absent from the source renderer table.
      await genuineBaseline();
      await assert.rejects(() => request('command-input.render-call', renderValue(callbackId + '.forged'), handle));
    } }),
    Object.freeze({ name: 'worker.forged-native-owner-or-generation-refused', async run() {
      await genuineBaseline();
      const forgedOwner = { ...handle, ownerId: handle.ownerId === 'forged-owner' ? 'other-forged-owner' : 'forged-owner' };
      const forgedGeneration = { ...handle, ownerGeneration: handle.ownerGeneration === 1 ? 2 : 1 };
      for (const forged of [forgedOwner, forgedGeneration])
        await assert.rejects(() => request('command-input.render-call', renderValue(callbackId), forged));
    } }),
    Object.freeze({ name: 'native.forged-registered-handle-ids-refused', async run() {
      // Same owner/generation does not confer a native registry capability. Actual dispatch
      // authenticates callback AND registration ID before any native callback body executes.
      await genuineBaseline();
      await assert.rejects(() => invokeRegisteredNativeHandle({ ...handle, callbackId: handle.callbackId + '.forged' }));
      await assert.rejects(() => invokeRegisteredNativeHandle({ ...handle, registrationId: handle.registrationId + '.forged' }));
    } }),
  ]);
}
