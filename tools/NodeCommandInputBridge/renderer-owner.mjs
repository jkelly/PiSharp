// Actual source owner for the frozen R874 component protocol. Original TUI
// instances, renderer state, Theme/Keybindings and host writers stay in this realm.
import assert from 'node:assert/strict';
import { createTuiComponentBridge } from './tui-component-bridge.mjs';

export function createOriginalRendererOwner({ supplier, currentInvocation, enterInvocation, workspace, sourceInvalid }) {
  const scopes = new Map(), components = new Map(), customOriginals = new Map();
  let sequence = 0, reservations = 0, retainedOriginals = 0, retired = false, closing;
  const ordinaryLimit = 960, cleanupLimit = 1024;
  const charge = cleanup => assert(++retainedOriginals <= (cleanup ? cleanupLimit : ordinaryLimit), 'Owner renderer original budget');
  const identityKeys = ['ownerId', 'ownerGeneration', 'scopeId', 'sessionGeneration', 'nativeSessionGeneration'];
  function identity(ticket) {
    assert(ticket && typeof ticket.ownerId === 'string' && ticket.ownerId.length > 0 && ticket.ownerId.length <= 128);
    assert(typeof ticket.scopeId === 'string' && ticket.scopeId.length > 0 && ticket.scopeId.length <= 128);
    for (const name of ['ownerGeneration', 'sessionGeneration', 'nativeSessionGeneration']) assert(Number.isSafeInteger(ticket[name]) && ticket[name] > 0);
    assert(typeof ticket.operationId === 'string' && ticket.operationId.length > 0 && ticket.operationId.length <= 128);
    assert(ticket.signal && typeof ticket.signal.throwIfAborted === 'function');
    return Object.freeze(Object.fromEntries(identityKeys.map(key => [key, ticket[key]])));
  }
  const equal = (left, right) => identityKeys.every(key => left[key] === right[key]);
  const activeCount = () => [...components.values()].filter(row => row.scope.bridge.hasComponent(row.internalId)).length;
  function requireLive() { assert(!retired && !sourceInvalid(), 'Original renderer owner retired'); }
  function scopeFor(ticket) {
    requireLive(); const actual = identity(ticket), key = actual.scopeId;
    if (scopes.has(key)) { const scope = scopes.get(key); assert(equal(scope.identity, actual), 'Renderer scope/native session identity differs'); return scope; }
    assert(scopes.size < 16 && typeof ticket.hostCall === 'function' && ticket.handle && ticket.handle.ownerId === actual.ownerId && ticket.handle.ownerGeneration === actual.ownerGeneration,
      'Actual persistent renderer scope and parent host writer required');
    const supplied = supplier(); assert(supplied?.ORIGINAL_SUPPLIERS && typeof supplied.createOriginalRendererEnvironment === 'function', 'Genuine renderer suppliers unavailable');
    const genuine = supplied.ORIGINAL_SUPPLIERS, environment = supplied.createOriginalRendererEnvironment();
    const scope = { identity: actual, prefix: 'source-scope-' + (++sequence), writer: ticket.hostCall, parentTicket: ticket,
      environment, rows: new Map(), bridge: undefined };
    const publicId = internalId => scope.prefix + '.' + internalId;
    function publish(internalId) {
      const id = publicId(internalId);
      if (!components.has(id)) {
        assert(components.size < ordinaryLimit && activeCount() < 16, 'Source component admission budget');
        const active = currentInvocation();
        assert(active?.rendererCreation?.scope === scope && typeof active.ticket.hostCall === 'function', 'Original component parent writer required');
        components.set(id, { scope, internalId, pendingInvalidations: 0, parentTicket: active.ticket,
          parentInvocation: active.rendererCreation.parentInvocation });
        if (active?.rendererCreation?.scope === scope) {
          components.get(id).pendingInvalidations = active.rendererCreation.pendingInvalidations;
          if (active.rendererCreation.reserved) { reservations--; active.rendererCreation.reserved = false; }
        }
      }
      return id;
    }
    const hostWriter = (method, payload, cleanup = false) => {
      assert(equal(payload, scope.identity), 'Source signal scope identity differs'); charge(cleanup);
      const id = publish(payload.componentId);
      // Parent native participant/writer stays authoritative after create/input calls settle.
      return components.get(id).parentTicket.hostCall(method, { ...scope.identity, componentId: id });
    };
    scope.environment.tui = Object.freeze({ requestRender() {
      const active = currentInvocation(); assert(active?.rendererScope === scope, 'Actual source component invocation required');
      if (active.rendererInternalId !== undefined) scope.bridge.invalidate(active.rendererInternalId);
      else { assert(active.rendererCreation && ++active.rendererCreation.pendingInvalidations <= 16, 'Unpublished source invalidation budget'); }
    } });
    scope.bridge = createTuiComponentBridge({ originalTui: genuine.originalTui,
      genuineThemeConstructor: genuine.originalTheme.Theme, genuineKeybindingsConstructor: genuine.originalKeybindings.KeybindingsManager,
      identity: scope.identity, environmentFor: suppliedIdentity => { assert(equal(suppliedIdentity, scope.identity)); return scope.environment; },
      invokeInOwner: (internalId, callback) => {
        const active = currentInvocation(); assert(active?.rendererScope === scope, 'Actual AsyncLocal source owner required');
        const cleanup = active.rendererRequestKind === 'component-dispose' || internalId !== null && !scope.bridge.hasComponent(internalId) ||
          internalId === null && active.rendererCreation?.factoryInvoked;
        charge(cleanup);
        const ancestors = [...(active.rendererAncestors ?? []), ...(active.rendererInternalId === undefined ? [] : [active.rendererInternalId])];
        const nested = { ...active, rendererAncestors: ancestors, ...(internalId === null ? {} : { rendererInternalId: internalId }) };
        const result = enterInvocation(nested, callback);
        if (internalId === null && active.rendererCreation) {
          active.rendererCreation.factoryInvoked = true;
          if (active.rendererRow && result && typeof result.render === 'function') active.rendererRow.lastComponent = result;
        }
        return result;
      },
      isExecutingComponent: internalId => {
        const active = currentInvocation(); return active?.rendererScope === scope &&
          (active.rendererInternalId === internalId || active.rendererAncestors?.includes(internalId));
      }, hostCall: (method, payload) => hostWriter(method, payload),
      retireNativeOpen: payload => hostWriter('ui.custom.done', payload, true) });
    scope.publish = publish; scopes.set(key, scope); return scope;
  }
  function owned(id, ticket, closed = false) {
    requireLive(); const row = components.get(id); assert(row && equal(row.scope.identity, identity(ticket)), 'Source component scope/native session identity differs');
    if (!closed) assert(row.scope.bridge.hasComponent(row.internalId), 'Retired source component'); return row;
  }
  function current(scope, ticket, additions = {}) {
    const parent = additions.parentInvocation ?? {};
    return { ...parent, kind: parent.kind ?? ticket.kind, signal: ticket.signal, ticket, hostCall: ticket.hostCall ?? parent.hostCall,
      rendererRequestKind: ticket.kind, rendererScope: scope,
      rendererAncestors: [], ...additions };
  }
  async function publicationJoin(scope, internalId) {
    const failures = [];
    for (const record of scope.bridge.originals.filter(row => row.componentId === internalId && row.kind.endsWith('-signal'))) {
      try { if (record.status === 'faulted') throw record.error; await record.original; } catch (error) { failures.push(error); }
    }
    if (failures.length) throw new AggregateError(failures, 'Original source component publications failed');
  }
  function rendererRow(scope, callbackId, context, args, options) {
    assert(context && typeof context === 'object' && !Array.isArray(context));
    assert(Object.keys(context).every(key => key === 'toolCallId'), 'Unsupported original renderer context metadata');
    const callId = context.toolCallId === undefined ? scope.identity.scopeId : context.toolCallId;
    assert(typeof callId === 'string' && callId.length > 0 && callId.length <= 128);
    const key = callbackId + ':' + callId;
    if (!scope.rows.has(key)) { assert(scope.rows.size < 64); scope.rows.set(key, { args: args ?? {}, state: {}, lastComponent: undefined }); }
    const row = scope.rows.get(key); if (args !== undefined) row.args = args;
    return { row, context: { args: row.args, toolCallId: callId, state: row.state, lastComponent: row.lastComponent,
      cwd: workspace(), executionStarted: options !== undefined, argsComplete: true,
      isPartial: options?.isPartial ?? false, expanded: options?.expanded ?? false, showImages: false, isError: false,
      invalidate: () => scope.environment.tui.requestRender() } };
  }
  async function createRenderer(renderer, callbackId, supplied, context, ticket, options) {
    assert(retainedOriginals < ordinaryLimit, 'Source renderer original admission budget');
    const scope = scopeFor(ticket); ticket.signal.throwIfAborted(); assert(activeCount() + reservations < 16, 'Source component capacity'); reservations++;
    const creation = { scope, reserved: true, factoryInvoked: false, pendingInvalidations: 0, parentInvocation: currentInvocation() };
    try {
      const retained = rendererRow(scope, callbackId, context, options === undefined ? supplied : undefined, options);
      return await enterInvocation(current(scope, ticket, { rendererCreation: creation, rendererRow: retained.row }), async () => {
        const internalId = options === undefined ? await scope.bridge.toolCall(renderer, supplied, retained.context)
          : await scope.bridge.toolResult(renderer, supplied, options, retained.context);
        return scope.publish(internalId);
      });
    } finally { if (creation.reserved) reservations--; }
  }
  function custom(factory, options, ticket) {
    assert(retainedOriginals < ordinaryLimit, 'Source custom original admission budget');
    const scope = scopeFor(ticket); ticket.signal.throwIfAborted(); assert(activeCount() + reservations < 16, 'Source custom capacity'); reservations++;
    const creation = { scope, reserved: true, factoryInvoked: false, pendingInvalidations: 0, parentInvocation: currentInvocation() };
    let original;
    try { original = enterInvocation(current(scope, ticket, { rendererCreation: creation, parentInvocation: creation.parentInvocation }), () => scope.bridge.custom(factory, options)); }
    catch (error) { if (creation.reserved) reservations--; throw error; }
    if (!customOriginals.has(ticket.operationId)) customOriginals.set(ticket.operationId, []);
    const record = { original, status: 'running' }; customOriginals.get(ticket.operationId).push(record);
    // Observe immediately; callers and joinOperation still await this same actual custom original.
    const release = () => { if (creation.reserved) { reservations--; creation.reserved = false; } };
    void original.then(() => { record.status = 'fulfilled'; release(); }, error => { record.status = 'faulted'; record.error = error; release(); });
    return original;
  }
  async function joinOperation(operationId) {
    const failures = [];
    for (const record of customOriginals.get(operationId) ?? []) try { await record.original; } catch (error) { failures.push(error); }
    if (failures.length) throw new AggregateError(failures, 'Original custom operation failures');
  }
  async function render(id, width, ticket) {
    assert(retainedOriginals < ordinaryLimit, 'Source render original admission budget');
    const row = owned(id, ticket); assert(Number.isSafeInteger(width) && width >= 1 && width <= 256);
    return await enterInvocation(current(row.scope, ticket, { parentInvocation: row.parentInvocation }), async () => {
      const failures = []; let result;
      try { result = await row.scope.bridge.render(row.internalId, width); } catch (error) { failures.push(error); }
      // Publishing is deferred until an actual native component callback proves attachment.
      try { for (let count = row.pendingInvalidations; count > 0; count--) row.scope.bridge.invalidate(row.internalId); row.pendingInvalidations = 0; }
      catch (error) { failures.push(error); }
      try { await publicationJoin(row.scope, row.internalId); } catch (error) { failures.push(error); }
      if (failures.length) throw new AggregateError(failures, 'Original render/publication failures'); return result;
    });
  }
  async function input(id, data, ticket) {
    assert(retainedOriginals < ordinaryLimit, 'Source input original admission budget');
    const row = owned(id, ticket); return await enterInvocation(current(row.scope, ticket, { parentInvocation: row.parentInvocation }), () => row.scope.bridge.input(row.internalId, data));
  }
  async function dispose(id, ticket) {
    const row = owned(id, ticket, true), active = currentInvocation();
    // Inspect the actual caller before replacing its frame with the retained
    // creator/disposal ticket. That replacement must not erase active ancestry.
    assert(active?.rendererScope !== row.scope ||
      active.rendererInternalId !== row.internalId && !active.rendererAncestors?.includes(row.internalId),
      'Source component close cannot join its own or ancestor original');
    return await enterInvocation(current(row.scope, ticket, { parentInvocation: row.parentInvocation }), () => row.scope.bridge.dispose(row.internalId));
  }
  function hasComponent(id, ticket) {
    const row = components.get(id); if (!row || retired || sourceInvalid()) return false;
    assert(equal(row.scope.identity, identity(ticket)), 'Source component ownership differs'); return row.scope.bridge.hasComponent(row.internalId);
  }
  function close() {
    if (closing) return closing;
    ensureClosable();
    // Every bridge refuses a pending native open before mutating retirement state.
    const originals = [], synchronousFailures = [];
    // Dispose in each component's acquired parent realm, including its actual
    // writer, before the scope bridge joins the same disposal tombstones.
    for (const row of components.values()) if (row.scope.bridge.hasComponent(row.internalId)) try {
      originals.push(enterInvocation(current(row.scope, row.parentTicket, {
        parentInvocation: row.parentInvocation, rendererRequestKind: 'component-dispose'
      }), () => row.scope.bridge.dispose(row.internalId)));
    } catch (error) { synchronousFailures.push(error); }
    for (const scope of scopes.values()) try { originals.push(enterInvocation(current(scope, scope.parentTicket), () => scope.bridge.close())); }
    catch (error) { synchronousFailures.push(error); }
    retired = true;
    closing = (async () => {
      const failures = [...synchronousFailures];
      for (const original of originals) try { await original; } catch (error) { failures.push(error); }
      for (const list of customOriginals.values()) for (const record of list) try { await record.original; } catch (error) { failures.push(error); }
      if (failures.length) throw new AggregateError(failures, 'Original renderer owner cleanup failures');
    })(); return closing;
  }
  function ensureClosable() {
    assert(!currentInvocation()?.rendererInternalId, 'Source owner close cannot join its own component callback');
    assert(reservations === 0 && [...customOriginals.values()].every(list => list.every(record => record.status !== 'running')),
      'Native owner must retire and join pending factory/custom originals before source finalization');
  }
  return Object.freeze({ custom, joinOperation, render, input, dispose, hasComponent, close, ensureClosable,
    toolCall: (renderer, callbackId, args, context, ticket) => createRenderer(renderer, callbackId, args, context, ticket),
    toolResult: (renderer, callbackId, result, options, context, ticket) => createRenderer(renderer, callbackId, result, context, ticket, options) });
}
