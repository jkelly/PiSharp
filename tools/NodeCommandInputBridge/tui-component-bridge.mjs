// Additive owner-realm component protocol. The loader supplies admitted original namespace objects;
// this module imports no replacement TUI constructors and owns no terminal/process/environment.
export function createTuiComponentBridge({ originalTui, genuineThemeConstructor, genuineKeybindingsConstructor,
  environmentFor, invokeInOwner, isExecutingComponent, hostCall, retireNativeOpen, identity, maximumComponents = 16 }) {
  const require = (condition, message) => { if (!condition) throw new Error(message); };
  require(originalTui && typeof originalTui.Text === 'function' && typeof originalTui.Box === 'function' &&
    typeof originalTui.matchesKey === 'function' && typeof originalTui.visibleWidth === 'function', 'Admitted original TUI namespace required');
  require(typeof genuineThemeConstructor === 'function' &&
    typeof environmentFor === 'function' && typeof invokeInOwner === 'function' && typeof isExecutingComponent === 'function' &&
    typeof hostCall === 'function', 'Actual owner-realm invocation and environment admission required');
  require(identity && typeof identity.ownerId === 'string' && identity.ownerId.length > 0 &&
    Number.isSafeInteger(identity.ownerGeneration) && identity.ownerGeneration > 0 &&
    Number.isSafeInteger(identity.sessionGeneration) && identity.sessionGeneration > 0, 'Component generation identity required');
  require(Number.isSafeInteger(maximumComponents) && maximumComponents > 0 && maximumComponents <= 16, 'Component capacity');
  const slots = new Map(), closed = new Map(); let sequence = 0, retired = false, reservations = 0, closing;
  const pendingCustom = new Set();
  const originals = [], failures = [];
  function environment(custom = false) {
    const value = environmentFor(identity);
    require(value && value.theme instanceof genuineThemeConstructor, 'Genuine owner-realm theme required');
    if (custom) require(typeof genuineKeybindingsConstructor === 'function' && value.keybindings instanceof genuineKeybindingsConstructor &&
      value.tui && typeof value.tui.requestRender === 'function' && typeof retireNativeOpen === 'function',
      'Genuine keybindings, admitted native TUI adapter and actual open retirement writer required');
    return value;
  }
  function begin(kind, id, callback, cleanup = false) {
    // Record the attempted original before invoking any source callback or host effect.
    require(originals.length < (cleanup ? 1024 : 960), 'Component retained-original budget');
    const record = { kind, componentId: id, original: undefined, error: undefined, status: 'running' }; originals.push(record);
    try { record.original = callback(); return record; }
    catch (error) { record.error = error; record.status = 'faulted'; failures.push(record); throw error; }
  }
  async function join(record) {
    if (record.status === 'faulted') throw record.error;
    try { const value = await record.original; record.status = 'fulfilled'; return value; }
    catch (error) { record.error = error; record.status = 'faulted'; if (!failures.includes(record)) failures.push(record); throw error; }
  }
  function slotFor(id) {
    const slot = slots.get(id); require(slot && !retired && slot.phase === 'open', 'Retired component handle'); return slot;
  }
  function signal(slot, method, kind) {
    const record = begin(kind, slot.id, () => hostCall(method, { ...identity, componentId: slot.id }));
    slot.publications.push(record);
    // A source requestRender/done returns void. Observe its actual host original immediately;
    // input/disposal still joins that same original and propagates every retained failure.
    record.observation = Promise.resolve(record.original).then(() => { record.status = 'fulfilled'; }, error => {
      record.error = error; record.status = 'faulted'; if (!failures.includes(record)) failures.push(record);
      // A failed asynchronous done write must stop the same acquired native open as well.
      // This only starts the owning signal original; custom() directly joins it in finally.
      if (slot.onSignalFailure) slot.onSignalFailure();
    });
    return record;
  }
  function allocate(component, env, reserved = false) {
    require(!retired && (reserved || slots.size + reservations < maximumComponents) && sequence < Number.MAX_SAFE_INTEGER, 'Component admission capacity');
    require(component && typeof component.render === 'function', 'Original factory must return a renderable component');
    const id = 'component-' + (++sequence);
    const slot = { id, component, environment: env, phase: 'open', active: new Set(), publications: [], failures: [], done: false, doneValue: undefined, close: undefined };
    slots.set(id, slot); return slot;
  }
  async function invoke(slot, kind, callback) {
    require(slot.phase === 'open', 'Component callback admission fenced');
    let settle;
    const admission = new Promise(resolve => { settle = resolve; }); slot.active.add(admission);
    try {
      return await join(begin(kind, slot.id, () => invokeInOwner(slot.id, callback)));
    } catch (error) { slot.failures.push(error); throw error;
    } finally { slot.active.delete(admission); settle(); }
  }
  async function render(id, width) {
    require(Number.isSafeInteger(width) && width >= 1 && width <= 256, 'Native render width');
    const slot = slotFor(id), rows = await invoke(slot, 'render', () => slot.component.render(width));
    require(Array.isArray(rows) && rows.length <= 256 && rows.every(row => typeof row === 'string'), 'Original render rows');
    require(rows.reduce((sum, row) => sum + row.length, 0) <= 65536, 'Native render row budget');
    // Rows are the only component projection. The original instance and constructors stay in the owner realm.
    const cellWidths = await invoke(slot, 'visible-width', () => rows.map(row => originalTui.visibleWidth(row)));
    require(cellWidths.every(value => Number.isSafeInteger(value) && value >= 0 && value <= width), 'Original cell-width projection');
    return { rows, cellWidths };
  }
  async function input(id, data) {
    require(typeof data === 'string' && data.length <= 65536, 'Native component input budget');
    const slot = slotFor(id);
    const faults = [];
    if (typeof slot.component.handleInput === 'function')
      try { await invoke(slot, 'input', () => slot.component.handleInput(data)); } catch (error) { faults.push(error); }
    // done/requestRender are acknowledged signals, never full close joins from inside the current input callback.
    for (const record of slot.publications) try { await join(record); } catch (error) { faults.push(error); }
    if (faults.length) throw new AggregateError(faults, 'Component input original failures');
  }
  function dispose(id) {
    require(!isExecutingComponent(id), 'Component close cannot join its own or ancestor original');
    if (closed.has(id)) return closed.get(id);
    const slot = slots.get(id); require(slot, 'Unknown component disposal');
    if (slot.close) return slot.close;
    slot.phase = 'retiring';
    const original = (async () => {
      const faults = [];
      for (const admitted of [...slot.active]) { try { await admitted; } catch (error) { faults.push(error); } }
      faults.push(...slot.failures);
      for (const record of slot.publications) { try { await join(record); } catch (error) { faults.push(error); } }
      if (typeof slot.component.dispose === 'function') {
        try { await join(begin('dispose', id, () => invokeInOwner(id, () => slot.component.dispose()), true)); }
        catch (error) { faults.push(error); }
      }
      slot.phase = 'closed';
      slots.delete(id);
      if (faults.length) throw new AggregateError(faults, 'Component disposal original failures');
    })();
    slot.close = original; closed.set(id, original); return original;
  }
  async function createSlot(kind, factory, customEnvironment = false) {
    require(!retired && slots.size + reservations < maximumComponents && sequence + reservations < 960,
      'Component factory admission capacity');
    const env = environment(customEnvironment); reservations++;
    let component;
    try {
      component = await join(begin(kind, null, () => invokeInOwner(null, () => factory(env))));
      return allocate(component, env, true);
    } catch (error) {
      const faults = [error];
      // Validation/retirement can fail after a real factory has returned an owned component.
      if (component && typeof component.dispose === 'function')
        try { await join(begin('unpublished-dispose', null, () => invokeInOwner(null, () => component.dispose()), true)); }
        catch (cleanupError) { faults.push(cleanupError); }
      if (faults.length > 1) throw new AggregateError(faults, 'Unpublished component original failures');
      throw error;
    } finally { reservations--; }
  }
  async function custom(factory, options) {
    require(typeof factory === 'function', 'Original custom component factory required');
    require(options === undefined || options && Object.keys(options).length === 1 && options.overlay === false,
      'Overlay fields require a separately admitted native overlay owner');
    let slot, earlyDone = false, earlyValue;
    const marker = {}; pendingCustom.add(marker);
    const done = value => {
      if (!slot) { if (!earlyDone) { earlyDone = true; earlyValue = value; } return; }
      if (slot.done || slot.phase !== 'open') return;
      slot.done = true; slot.doneValue = value;
      // Native ui.custom.done must only signal retirement. The pending open task owns full cleanup.
      signal(slot, 'ui.custom.done', 'done-signal');
    };
    try { slot = await createSlot('factory', env => factory(env.tui, env.theme, env.keybindings, done), true); }
    catch (error) { pendingCustom.delete(marker); throw error; }
    const faults = []; let openRecord, openJoined = false, retireRecord, retireObservation;
    const retireOpen = () => {
      if (retireRecord || !openRecord) return;
      try {
        retireRecord = begin('native-open-retire', slot.id,
          () => retireNativeOpen({ ...identity, componentId: slot.id }), true);
        // Observe this exact original immediately; retain rather than suppress its error.
        retireObservation = join(retireRecord).then(() => {}, error => { faults.push(error); });
      } catch (error) { faults.push(error); }
    };
    slot.onSignalFailure = retireOpen;
    try {
      openRecord = begin('native-open', slot.id, () => hostCall('ui.custom.open', { ...identity, componentId: slot.id }));
      if (earlyDone) done(earlyValue);
      openJoined = true; await join(openRecord);
    }
    catch (error) { faults.push(error); }
    finally {
      if (openRecord && !openJoined) {
        // A synchronous early done signal failure does not abandon the already acquired open.
        // This adapter writes the actual owning cancel/retire signal; it must not join this custom
        // callback. The helper then joins the same original open and retains its full failure.
        retireOpen();
        try { await join(openRecord); } catch (error) { faults.push(error); }
      }
      if (retireObservation) await retireObservation;
      slot.onSignalFailure = undefined;
      try { await dispose(slot.id); } catch (error) { faults.push(error); }
      pendingCustom.delete(marker);
    }
    if (faults.length) throw new AggregateError(faults, 'Custom component original failures');
    return slot.doneValue; // Exact source done object identity; no transport round trip or clone.
  }
  async function toolCall(renderer, args, context) {
    require(typeof renderer === 'function', 'Original renderCall callback required');
    return (await createSlot('render-call', env => renderer(args, env.theme, context))).id;
  }
  async function toolResult(renderer, result, options, context) {
    require(typeof renderer === 'function', 'Original renderResult callback required');
    return (await createSlot('render-result', env => renderer(result, options, env.theme, context))).id;
  }
  function close() {
    require(![...slots.keys()].some(isExecutingComponent), 'Owner component close cannot join its own original');
    // The native scope owner retires and joins custom-open originals before worker finalization.
    // Refuse before mutation rather than joining an open whose host retirement has not started.
    require(pendingCustom.size === 0 && reservations === 0, 'Native owner must retire and join pending factories/custom opens before component finalization');
    if (closing) return closing;
    retired = true;
    closing = (async () => {
      const tasks = new Set(closed.values()), faults = [];
      for (const id of [...slots.keys()]) { try { tasks.add(dispose(id)); } catch (error) { faults.push(error); } }
      for (const task of tasks) { try { await task; } catch (error) { faults.push(error); } }
      if (faults.length) throw new AggregateError(faults, 'Owner component original cleanup failures');
    })();
    return closing;
  }
  return Object.freeze({ originalTui, custom, render, input, dispose, toolCall, toolResult, close,
    invalidate(id) { signal(slotFor(id), 'ui.component.invalidate', 'invalidate-signal'); },
    hasComponent(id) { const slot = slots.get(id); return !!slot && !retired && slot.phase === 'open'; },
    get originals() { return [...originals]; }, get failures() { return [...failures]; } });
}
