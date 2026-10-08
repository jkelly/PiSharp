// Whole original Commands/Input callbacks over the unchanged qualified full-duplex peer.
import assert from 'node:assert/strict';
import { startPeer, strictJson, json } from '../NodeBridge/wire.mjs';
import { createCommandInputLoader, createDialogWorkerGate, validatePrivateQualificationLease } from './module-loader.mjs';
import { readSessionSnapshot, validateSessionEvent } from './session-manager-facade.mjs';
const allowed = new Set(['--worker-generation', '--session-generation', '--mode', '--repo', '--oracle', '--jiti', '--reference', '--command-input-reference']);
const args = new Map(), argv = process.argv.slice(2);
for (let index = 0; index < argv.length; index += 2) { assert(allowed.has(argv[index]) && !args.has(argv[index]) && index + 1 < argv.length); args.set(argv[index], argv[index + 1]); }
assert.equal(args.size, 8); assert.equal(args.get('--mode'), 'normal');
const identity = text => { assert(/^[1-9][0-9]*$/u.test(text ?? '')); const value = Number(text); assert(Number.isSafeInteger(value)); return value; };
const worker = identity(args.get('--worker-generation')), session = identity(args.get('--session-generation'));
const roots = { repo: args.get('--repo'), oracle: args.get('--oracle'), jiti: args.get('--jiti'), reference: args.get('--reference'), commandInputReference: args.get('--command-input-reference') };
const text = (value, maximum = 128) => { assert(typeof value === 'string' && value.length > 0 && value.length <= maximum); return value; };
const exact = (value, keys) => { assert(value && typeof value === 'object' && !Array.isArray(value)); assert.deepEqual(Object.keys(value).sort(), [...keys].sort()); };
const bounded = value => { assert(Buffer.byteLength(JSON.stringify(value)) <= 524288, 'Commands/Input response budget'); return json(value); };
const rawJson = value => { text(value, 262144); assert(Buffer.byteLength(value) <= 262144, 'Commands/Input JSON request byte budget'); return strictJson(value); };
const operations = new Map(); let retainedBytes = 0, source, owner, generation, loadReserved = false, loaded = false, finalized = false, callbackIds, finalization;
function finalizeSource() {
  if (finalization) { if (finalization.failed) throw finalization.error; return finalization.original; }
  finalized = true; finalization = { failed: false };
  try { finalization.original = source.finalize(); return finalization.original; }
  catch (error) { finalization.failed = true; finalization.error = error; throw error; }
}
function admit(value, kind, component = false) {
  assert(source && loaded && !finalized && value.ownerId === owner && value.ownerGeneration === generation);
  if (component) assert.equal(value.sessionGeneration, session, 'Stale component session');
  else assert(callbackIds[kind].includes(value.callbackId), 'Callback kind/identity mismatch');
  const id = text(value.operationId); assert(!operations.has(id) && operations.size < 16, 'Commands/Input operation admission');
  let done; const settled = new Promise(resolve => { done = resolve; });
  const operation = { kind, settled, done, status: 'running' }; operations.set(id, operation); return operation;
}
function rendererExport(name) {
  if (typeof source[name] !== 'function') {
    const error = new Error('Unsupported original renderer transport: source.' + name);
    error.bridgeCode = 'UnsupportedRendererTransport'; error.surface = 'source.' + name; throw error;
  }
  return source[name].bind(source);
}
function componentIdentity(value) {
  assert.equal(value.ownerId, owner); assert.equal(value.ownerGeneration, generation);
  assert.equal(value.sessionGeneration, session); text(value.scopeId); identity(String(value.nativeSessionGeneration)); text(value.componentId);
}
function renderRows(value, width) {
  exact(value, ['rows', 'cellWidths']);
  assert(Array.isArray(value.rows) && value.rows.length <= 256 && Array.isArray(value.cellWidths) && value.rows.length === value.cellWidths.length);
  let characters = 0;
  for (const [index, row] of value.rows.entries()) {
    assert(typeof row === 'string'); characters += row.length; assert(characters <= 65536, 'Original row character budget');
    // The original visibleWidth projection is supplied by the source owner, never remeasured here.
    assert(Number.isSafeInteger(value.cellWidths[index]) && value.cellWidths[index] >= 0 && value.cellWidths[index] <= width);
    const visible = row.replace(/\u001b\[[0-9;]{0,126}m/gu, '');
    assert(!/[\u0000-\u001f\u007f-\u009f]/u.test(visible), 'Unsupported original row terminal effect');
  }
  return value;
}
function remember(operation, observation) {
  const bytes = Buffer.byteLength(JSON.stringify(observation)); assert(bytes <= 524000 && retainedBytes + bytes <= 8388608, 'Commands/Input retained observation budget');
  operation.observation = observation; operation.bytes = bytes; retainedBytes += bytes;
}
const uiCapabilities = value => {
  exact(value, ['mode', 'connectionGeneration', 'sessionGeneration', 'features']);
  assert(['print', 'json', 'rpc', 'tui'].includes(value.mode) && Number.isSafeInteger(value.connectionGeneration) && Number.isSafeInteger(value.sessionGeneration));
  assert(value.connectionGeneration >= 0 && value.sessionGeneration >= 0 && Array.isArray(value.features) && value.features.length <= 16 && new Set(value.features).size === value.features.length);
  assert(value.features.every(feature => typeof feature === 'string' && feature.length <= 64)); return value;
};
const snapshotFields = value => Object.hasOwn(value, 'sessionSnapshotPresence')
  ? ['sessionSnapshotPresence', ...(value.sessionSnapshotPresence === 'json' ? ['sessionSnapshotJson'] : [])] : [];
const callbackSnapshot = value => readSessionSnapshot(value.sessionSnapshotPresence ?? 'unavailable', value.sessionSnapshotJson);
const peer = await startPeer(worker, session, async (message, signal, hostCall) => {
  if (message.method === 'probe.runtime') { assert.equal(message.value.presence, 'absent'); return json({ version: process.version, platform: process.platform, architecture: process.arch, pid: process.pid, parentPid: process.ppid, cwd: process.cwd(), environment: { ...process.env } }); }
  if (message.method === 'probe.ping') return json({ alive: true });
  assert.equal(message.value.presence, 'json'); const value = message.value.data;
  switch (message.method) {
    case 'command-input.load':
    case 'command-input.load-qualification': {
      const qualification = message.method === 'command-input.load-qualification';
      exact(value, ['ownerId', 'ownerGeneration', 'cwd', 'inputInstances', 'controlledClock', ...(qualification ? ['qualificationLease'] : Object.hasOwn(value, 'sourcePaths') ? ['sourcePaths'] : [])]); assert(!source && !loadReserved && message.handle === undefined);
      loadReserved = true;
      owner = text(value.ownerId); generation = identity(String(value.ownerGeneration)); assert([1, 2].includes(value.inputInstances));
      assert(value.controlledClock === null || value.controlledClock === '2026-10-01T12:00:00.000Z');
      let qualificationLease;
      if (qualification) { assert(value.inputInstances === 1 && value.controlledClock === null); qualificationLease = validatePrivateQualificationLease(value.qualificationLease, owner, generation); signal.throwIfAborted(); }
      try {
        source = await createCommandInputLoader(roots); const result = qualification
          ? await source.loadQualification(text(value.cwd, 4096), qualificationLease, owner, generation)
          : await source.load(text(value.cwd, 4096), value.inputInstances, value.controlledClock, value.sourcePaths ?? null);
        if (qualification) signal.throwIfAborted();
        callbackIds = { input: result.inputHandlers.map(row => row.callbackId), command: result.commands.map(row => row.callbackId),
          completion: result.commands.filter(row => row.hasCompletion).map(row => row.callbackId), before_agent_start: result.beforeAgentStartHandlers.map(row => row.callbackId),
          session_start: result.sessionHandlers.filter(row => row.topic === 'session_start').map(row => row.callbackId),
          session_tree: result.sessionHandlers.filter(row => row.topic === 'session_tree').map(row => row.callbackId),
          tool: result.tools.map(row => row.callbackId), prepare: result.tools.map(row => row.callbackId),
          render_call: result.tools.filter(row => row.hasRenderCall === true).map(row => row.callbackId),
          render_result: result.tools.filter(row => row.hasRenderResult === true).map(row => row.callbackId) }; loaded = true; return bounded(result);
      } catch (error) { return bounded({ status: 'rejected', thrown: { name: error?.name, message: error?.message, stack: error?.stack, code: error?.bridgeCode, surface: error?.surface, ...(source ? { observed: source.observe(error) } : {}) } }); }
    }
    case 'command-input.completion': {
      exact(value, ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', 'prefix']); assert(message.handle === undefined, 'Completion grants no host capability');
      assert(typeof value.prefix === 'string' && value.prefix.length <= 65536); const operation = admit(value, 'completion');
      try { signal.throwIfAborted(); const result = source.complete(value.prefix, signal, value.callbackId); remember(operation, result); operation.status = result.status; signal.throwIfAborted(); return bounded(result); }
      finally { if (operation.status === 'running') operation.status = 'failed'; operation.done(); }
    }
    case 'command-input.prepare': {
      const loadout = value.preparationKind === 'loadout';
      exact(value, ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', ...(loadout ? ['preparationKind', 'loadoutJson'] : ['argumentsJson'])]);
      assert(message.handle === undefined, 'Preparation grants no host capability');
      const supplied = rawJson(loadout ? value.loadoutJson : value.argumentsJson), operation = admit(value, 'prepare');
      try { signal.throwIfAborted(); const result = loadout ? await source.prepareLoadout(value.callbackId, supplied, signal) : source.prepare(value.callbackId, supplied, signal);
        remember(operation, result); operation.status = result.status; signal.throwIfAborted(); return bounded(result); }
      finally { if (operation.status === 'running') operation.status = 'failed'; operation.done(); }
    }
    case 'command-input.command':
    case 'command-input.input':
    case 'command-input.before-agent-start':
    case 'command-input.session-event':
    case 'command-input.tool': {
      const command = message.method === 'command-input.command', tool = message.method === 'command-input.tool';
      const lifecycle = message.method === 'command-input.session-event';
      const supplied = rawJson(command ? value.argumentsJson : value.eventJson);
      const kind = lifecycle ? supplied.type : command ? 'command' : tool ? 'tool' : message.method === 'command-input.input' ? 'input' : 'before_agent_start';
      if (lifecycle) assert(['session_start', 'session_tree'].includes(kind), 'Unsupported lifecycle topic');
      exact(value, command
        ? ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', 'argumentsJson', 'catalogJson', 'catalogRevision', 'uiCapabilities', ...snapshotFields(value),
          ...(Object.hasOwn(value, 'scopeId') || Object.hasOwn(value, 'nativeSessionGeneration') ? ['scopeId', 'sessionGeneration', 'nativeSessionGeneration'] : [])]
        : ['ownerId', 'ownerGeneration', 'callbackId', 'operationId', 'eventJson', 'uiCapabilities', ...snapshotFields(value), ...(tool ? ['toolCallId'] : []),
          ...(Object.hasOwn(value, 'scopeId') || Object.hasOwn(value, 'nativeSessionGeneration') ? ['scopeId', 'sessionGeneration', 'nativeSessionGeneration'] : [])]);
      assert(message.handle && message.handle.ownerId === owner && message.handle.ownerGeneration === generation);
      const capabilities = uiCapabilities(value.uiCapabilities);
      if (Object.hasOwn(value, 'scopeId')) { text(value.scopeId); assert.equal(value.sessionGeneration, session); identity(String(value.nativeSessionGeneration)); assert.equal(value.nativeSessionGeneration, capabilities.sessionGeneration, 'Actual native UI attachment generation differs'); }
      const sessionSnapshot = callbackSnapshot(value);
      const catalog = command ? rawJson(value.catalogJson) : undefined;
      if (command) { assert(typeof supplied === 'string' && Array.isArray(catalog) && catalog.length <= 256); assert(Number.isSafeInteger(value.catalogRevision) && value.catalogRevision >= 0); }
      else if (lifecycle) { validateSessionEvent(supplied, sessionSnapshot); }
      else if (kind === 'input') { assert(supplied && typeof supplied === 'object' && typeof supplied.text === 'string' && ['interactive', 'rpc', 'extension'].includes(supplied.source)); }
      else if (tool) { text(value.toolCallId); assert(supplied && typeof supplied === 'object' && !Array.isArray(supplied)); }
      else { assert(supplied && supplied.type === 'before_agent_start' && typeof supplied.prompt === 'string' && typeof supplied.systemPrompt === 'string'); }
      const operation = admit(value, kind);
      const dialogGate = createDialogWorkerGate({ operationId: value.operationId, nativeSessionGeneration: capabilities.sessionGeneration,
        assertAdmission: () => { assert(operation.status === 'running'); signal.throwIfAborted(); },
        call: (method, payload, bindSignal) => hostCall(method, json(payload), message.handle, bindSignal ? signal : undefined).then(result => {
          assert(result.presence === 'json'); return result.data;
        }) });
      const native = (method, payload) => {
        const componentMethod = ['ui.custom.open', 'ui.custom.done', 'ui.component.invalidate'].includes(method);
        const childDialog = ['ui.dialog.reserve', 'ui.dialog.cancel', 'ui.dialog.retire'].includes(method) ||
          ['ui.select', 'ui.confirm', 'ui.input'].includes(method);
        assert(childDialog || ['ui.editor', 'ui.notify'].includes(method) || tool && method === 'tool.update' || command && componentMethod);
        if (childDialog) return dialogGate(method, payload);
        if (componentMethod) {
          assert(capabilities.mode === 'tui' && capabilities.features.includes('customterminalcomponent'), 'Native custom component capability required');
          exact(payload, ['ownerId', 'ownerGeneration', 'scopeId', 'sessionGeneration', 'nativeSessionGeneration', 'componentId']); componentIdentity(payload);
          assert.equal(payload.scopeId, value.scopeId); assert.equal(payload.nativeSessionGeneration, value.nativeSessionGeneration);
        }
        assert(!Object.hasOwn(payload, 'operationId'), 'Source cannot replace actual command operation identity');
        // Done/invalidate await their admitted signal acknowledgements. Only open awaits full native retirement.
        // The original command handle/signal remain bound even when a component input publishes a signal.
        const original = hostCall(method, json({ ...payload, operationId: value.operationId }), message.handle,
          method === 'ui.custom.done' ? undefined : signal);
        return original.then(result => { assert(result.presence === 'json'); return result.data; });
      };
      try {
        const ticket = Object.freeze({ ownerId: owner, ownerGeneration: generation, sessionGeneration: session,
          scopeId: value.scopeId, nativeSessionGeneration: value.nativeSessionGeneration,
          operationId: value.operationId, kind, signal, handle: message.handle, hostCall: native });
        signal.throwIfAborted(); const result = await source.invoke(kind, value.callbackId, supplied, catalog, value.catalogRevision, capabilities, signal, native, value.toolCallId, ticket, sessionSnapshot);
        remember(operation, result); operation.status = result.status; signal.throwIfAborted(); return bounded(result);
      } finally { if (operation.status === 'running') operation.status = 'failed'; operation.done(); }
    }
    case 'command-input.render-call':
    case 'command-input.render-result':
    case 'command-input.component-render':
    case 'command-input.component-input':
    case 'command-input.component-dispose': {
      const call = message.method === 'command-input.render-call', result = message.method === 'command-input.render-result';
      const component = !call && !result;
      const kind = call ? 'render_call' : result ? 'render_result' : message.method.slice('command-input.'.length);
      const identityFields = ['ownerId', 'ownerGeneration', 'scopeId', 'sessionGeneration', 'nativeSessionGeneration', 'operationId'];
      exact(value, component ? [...identityFields, ...snapshotFields(value), 'componentId', ...(kind === 'component-render' ? ['width'] : kind === 'component-input' ? ['data'] : [])]
        : [...identityFields, ...snapshotFields(value), 'callbackId', 'renderContextJson', ...(call ? ['argumentsJson'] : ['resultJson', 'optionsJson'])]);
      // Native InvokeAsync adds this read-only metadata to every contextual request.
      // Validate it without granting a SessionManager or changing renderer authority.
      callbackSnapshot(value);
      assert.equal(value.sessionGeneration, session, 'Stale renderer session');
      text(value.scopeId); identity(String(value.nativeSessionGeneration));
      if (component) { assert(message.handle === undefined, 'Component callbacks grant no fresh host capability'); componentIdentity(value); }
      // This is the native registered host capability (rN/cN), not the source tool callback ID.
      // Native registration authentication occurs when the actual peer dispatches a host call;
      // admit() separately authenticates the source renderer ID against its original kind table.
      else assert(message.handle && message.handle.ownerId === owner && message.handle.ownerGeneration === generation,
        'Owned native renderer host callback handle required');
      const operation = admit(value, kind, component);
      // Actual source loader must enter its own AsyncLocal owner realm using this request ticket.
      // Component originals retain their admitted parent command host writer in that realm.
      const native = component ? undefined : (method, payload) => {
        assert.equal(method, 'ui.component.invalidate', 'Tool renderers grant only an invalidation signal');
        exact(payload, ['ownerId', 'ownerGeneration', 'scopeId', 'sessionGeneration', 'nativeSessionGeneration', 'componentId']); componentIdentity(payload);
        assert.equal(payload.scopeId, value.scopeId); assert.equal(payload.nativeSessionGeneration, value.nativeSessionGeneration);
        const original = hostCall(method, json({ ...payload, operationId: value.operationId }), message.handle, signal);
        return original.then(response => { assert.equal(response.presence, 'json'); return response.data; });
      };
      const ticket = Object.freeze({ ownerId: owner, ownerGeneration: generation, sessionGeneration: session,
        scopeId: value.scopeId, nativeSessionGeneration: value.nativeSessionGeneration,
        operationId: value.operationId, kind, componentId: component ? value.componentId : undefined,
        signal, handle: message.handle, hostCall: native });
      let observation, acquiredComponent;
      try {
        if (kind !== 'component-dispose') signal.throwIfAborted(); let original;
        if (component) {
          const hasComponent = rendererExport('hasComponent');
          // Disposal must permit a tombstone lookup so repeat calls join the same original disposal.
          if (kind !== 'component-dispose') assert(hasComponent(value.componentId, ticket), 'Retired source component');
          if (kind === 'component-render') {
            assert(Number.isSafeInteger(value.width) && value.width >= 1 && value.width <= 256, 'Native original renderer width');
            original = renderRows(await rendererExport('renderComponent')(value.componentId, value.width, ticket), value.width);
          } else if (kind === 'component-input') {
            assert(typeof value.data === 'string' && value.data.length <= 65536, 'Original component input budget');
            original = await rendererExport('inputComponent')(value.componentId, value.data, ticket);
          } else original = await rendererExport('disposeComponent')(value.componentId, ticket);
          observation = { status: 'fulfilled', componentId: value.componentId,
            ...(kind === 'component-render' ? { render: original } : { returned: source.observe(original) }) };
        } else {
          const context = rawJson(value.renderContextJson); assert(context && typeof context === 'object' && !Array.isArray(context));
          if (call) {
            const supplied = rawJson(value.argumentsJson); assert(supplied && typeof supplied === 'object' && !Array.isArray(supplied));
            original = await rendererExport('renderToolCall')(value.callbackId, supplied, context, ticket);
          } else {
            const supplied = rawJson(value.resultJson), options = rawJson(value.optionsJson);
            assert(supplied && typeof supplied === 'object' && !Array.isArray(supplied));
            exact(options, ['expanded', 'isPartial']); assert(typeof options.expanded === 'boolean' && typeof options.isPartial === 'boolean');
            original = await rendererExport('renderToolResult')(value.callbackId, supplied, options, context, ticket);
          }
          acquiredComponent = text(original); assert(rendererExport('hasComponent')(original, ticket), 'Actual owned renderer component required');
          observation = { status: 'fulfilled', componentId: original };
        }
        if (kind !== 'component-dispose') signal.throwIfAborted();
        remember(operation, observation); operation.status = observation.status; return bounded(observation);
      } catch (error) {
        // A create may acquire a real component before cancellation/validation
        // rejects its response. Join that same disposal instead of losing its ID.
        if (acquiredComponent) try { await rendererExport('disposeComponent')(acquiredComponent, ticket); }
        catch (cleanupError) { error = new AggregateError([error, cleanupError], 'Original renderer creation and disposal failures'); }
        observation = { status: 'rejected', thrown: { name: error?.name, message: error?.message, stack: error?.stack,
          code: error?.bridgeCode, surface: error?.surface, observed: source.observe(error) } };
        remember(operation, observation); operation.status = 'rejected'; return bounded(observation);
      } finally { if (operation.status === 'running') operation.status = 'failed'; operation.done(); }
    }
    case 'command-input.settle': {
      exact(value, ['operationId']); assert(message.handle === undefined); const operation = operations.get(text(value.operationId)); assert(operation, 'Unknown Commands/Input operation');
      await operation.settled; const result = bounded({ settled: true, kind: operation.kind, status: operation.status, ...(operation.observation ? { observation: operation.observation } : {}) });
      operations.delete(value.operationId); retainedBytes -= operation.bytes ?? 0; return result;
    }
    case 'command-input.finalize': { exact(value, []); assert(source && operations.size === 0 && message.handle === undefined); return bounded(await finalizeSource()); }
    default: throw new Error('Unsupported Commands/Input bridge method');
  }
}, async () => {
  assert([...operations.values()].every(operation => operation.status !== 'running'), 'Commands/Input shutdown has live source callbacks');
  operations.clear(); retainedBytes = 0; if (source) await finalizeSource();
});
await peer.stopped;
