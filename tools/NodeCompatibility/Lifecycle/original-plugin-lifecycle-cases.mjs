// Reference and real-adapter lifecycle controls. Run only through coordinator
// qualification in a separate pinned process. No rg.execute or persisted session.
import assert from 'node:assert/strict';
export const originalPluginLifecycleCaseIds = Object.freeze([
  'todo-start-restores-latest-snapshot', 'todo-tree-uses-active-branch',
  'todo-empty-branch-clears-state', 'todo-pure-state-transitions',
  'todo-mode-native-ui-contract', 'truncated-tool-has-no-lifecycle-callbacks'
]);

// Modules must be the actual qualified original loader, runner, SessionManager
// and genuine suppliers. The coordinator owns source resolution/guards/pins.
export async function createOriginalLifecycleFixture({ modules, cwd, signal }) {
  const { loader, events, runner: originalRunner, sessions, suppliers } = modules;
  const runtime = loader.createExtensionRuntime(), bus = events.createEventBus(), failures = [], originals = [];
  let runner, unsubscribe;
  const paths = ['todo.ts', 'truncated-tool.ts'].map(name => modules.originalPath('packages/coding-agent/examples/extensions/' + name));
  try {
    signal.throwIfAborted();
    const factory = loader.loadExtensions(paths, cwd, bus, runtime); originals.push(factory);
    const loaded = await factory; assert.deepEqual(loaded.errors, []); assert.equal(loaded.extensions.length, 2);
    const todo = loaded.extensions.find(ext => ext.tools.has('todo')), rg = loaded.extensions.find(ext => ext.tools.has('rg'));
    assert(todo && rg); assert.deepEqual([...todo.handlers.keys()].sort(), ['session_start', 'session_tree']);
    const session = sessions.SessionManager.inMemory(cwd);
    assert(session instanceof sessions.SessionManager); assert.equal(session.getSessionFile(), undefined);
    // Model services are unused by these original callbacks; no model facade or
    // credentials are supplied. Session context is the entire original instance.
    runner = new originalRunner.ExtensionRunner(loaded.extensions, runtime, cwd, session, undefined);
    unsubscribe = runner.onError(error => failures.push(error));
    const definition = todo.tools.get('todo').definition, command = todo.commands.get('todos');
    async function join(original) { originals.push(original); const result = await original; signal.throwIfAborted(); assert.deepEqual(failures, []); return result; }
    const execute = async params => {
      const validated = suppliers.ORIGINAL_SUPPLIERS.originalAi.validateToolArguments(definition,
        { id: 'original-lifecycle-todo', name: 'todo', type: 'toolCall', arguments: params });
      return await join(definition.execute('original-lifecycle-todo', validated, signal, undefined, runner.createContext()));
    };
    const snapshot = (todos, nextId) => session.appendMessage({ role: 'toolResult', toolCallId: 'saved-todo', toolName: 'todo',
      content: [{ type: 'text', text: 'Saved original todo state' }], details: { action: 'list', todos: structuredClone(todos), nextId }, isError: false, timestamp: Date.now() });
    return { runner, session, todo, rg, command, definition, suppliers, execute, snapshot, join,
      async close() {
        const faults = [...failures];
        for (const original of new Set(originals)) try { await original; } catch (error) { faults.push(error); }
        for (const cleanup of [() => unsubscribe?.(), () => runtime.invalidate(), () => bus.clear(), () => loader.clearExtensionCache()])
          try { cleanup(); } catch (error) { faults.push(error); }
        if (faults.length) throw new AggregateError(faults, 'Original lifecycle callbacks and cleanup failed');
      } };
  } catch (error) {
    const faults = [error];
    for (const original of new Set(originals)) try { await original; } catch (fault) { if (!faults.includes(fault)) faults.push(fault); }
    for (const cleanup of [() => unsubscribe?.(), () => runtime.invalidate(), () => bus.clear(), () => loader.clearExtensionCache()])
      try { cleanup(); } catch (fault) { faults.push(fault); }
    throw new AggregateError(faults, 'Original lifecycle fixture acquisition failed');
  }
}

export async function runOriginalPluginLifecycleCase(caseId, fixture, { actualUi, inspectNativeUi } = {}) {
  assert(originalPluginLifecycleCaseIds.includes(caseId));
  const { session, runner, execute, snapshot, join } = fixture;
  const first = [{ id: 7, text: 'branch A', done: false }], second = [{ id: 9, text: 'branch B', done: true }];
  const state = async (todos, nextId) => {
    const result = await execute({ action: 'list' }); assert.deepEqual(result.details.todos, todos); assert.equal(result.details.nextId, nextId); return result;
  };
  if (caseId === 'todo-start-restores-latest-snapshot') {
    snapshot(first, 8); snapshot(second, 10);
    session.appendMessage({ role: 'toolResult', toolName: 'unrelated', toolCallId: 'other', content: [],
      details: { todos: [{ id: 99, text: 'ignore', done: false }], nextId: 100 }, isError: false, timestamp: Date.now() });
    await join(runner.emit({ type: 'session_start', reason: 'resume' })); await state(second, 10);
  } else if (caseId === 'todo-tree-uses-active-branch') {
    const a = snapshot(first, 8), b = snapshot(second, 10);
    await join(runner.emit({ type: 'session_start', reason: 'startup' })); await state(second, 10);
    session.branch(a); await join(runner.emit({ type: 'session_tree', oldLeafId: b, newLeafId: a })); await state(first, 8);
    assert.equal(session.getBranch().some(entry => entry.id === b), false);
    session.branch(b); await join(runner.emit({ type: 'session_tree', oldLeafId: a, newLeafId: b })); await state(second, 10);
  } else if (caseId === 'todo-empty-branch-clears-state') {
    const leaf = snapshot(first, 8); await join(runner.emit({ type: 'session_start', reason: 'startup' })); await state(first, 8);
    session.resetLeaf(); await join(runner.emit({ type: 'session_tree', oldLeafId: leaf, newLeafId: null })); await state([], 1);
  } else if (caseId === 'todo-pure-state-transitions') {
    await join(runner.emit({ type: 'session_start', reason: 'new' }));
    assert.equal((await execute({ action: 'add', text: 'pure in-memory' })).details.nextId, 2);
    assert.equal((await execute({ action: 'toggle', id: 1 })).details.todos[0].done, true);
    assert.equal((await execute({ action: 'clear' })).details.nextId, 1);
    const added = await execute({ action: 'add', text: 'after clear' }); assert.equal(added.details.todos[0].id, 1);
  } else if (caseId === 'todo-mode-native-ui-contract') {
    assert(actualUi && typeof actualUi.invokeOwnedCommand === 'function' && typeof inspectNativeUi === 'function',
      'Actual native UI adapter/receipt collector required; no fake custom host');
    for (const mode of ['print', 'rpc', 'tui']) {
      runner.setUIContext(actualUi.forMode(mode), mode); assert.equal(runner.createContext().mode, mode);
      const before = await inspectNativeUi();
      // Adapter enters the genuine native command participant. TUI execution
      // drives an original Escape input and joins the acquired custom open.
      await join(actualUi.invokeOwnedCommand(mode, () => join(fixture.command.handler('', runner.createCommandContext())),
        { originalEscapeInput: mode === 'tui' }));
      const after = await inspectNativeUi();
      if (mode === 'tui') assert.equal(after.originalCustomOpens - before.originalCustomOpens, 1);
      else { assert.equal(after.originalCustomOpens, before.originalCustomOpens); assert.equal(after.originalNotifyCalls - before.originalNotifyCalls, 1); }
    }
  } else {
    assert.equal(fixture.rg.handlers.size, 0);
    assert.equal(typeof fixture.rg.tools.get('rg').definition.execute, 'function');
    // Its process/file-write execute path is deliberately never invoked.
    await join(runner.emit({ type: 'session_start', reason: 'startup' }));
  }
  assert.equal(session.getSessionFile(), undefined);
  return { caseId, originalRunner: true, originalInMemorySessionManager: true, originalFactories: 2,
    rgExecuteInvoked: false, persistedSessionWrites: false, nativeLifecycleParityQualified: false };
}
