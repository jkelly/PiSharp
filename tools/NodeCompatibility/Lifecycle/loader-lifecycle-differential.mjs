// Source-only genuine loader/Runner differential. Native lifecycle transport is
// qualified separately; this driver never claims native snapshot provenance.
import assert from 'node:assert/strict';
import { readSessionSnapshot } from '../../NodeCommandInputBridge/session-manager-facade.mjs';
export const loaderLifecycleCaseIds = Object.freeze([
  'loader-todo-start-latest', 'loader-todo-tree-selected-branch',
  'loader-todo-empty-branch', 'loader-todo-pure-transitions'
]);
export async function runLoaderLifecycleDifferential(caseId, { bridge, loadReceipt, reference, signal }) {
  assert(loaderLifecycleCaseIds.includes(caseId));
  assert.equal(loadReceipt.sourceCommit, 'd86654abb8862e201933517d6f1fce9f88dd117f');
  const sourcePath = '/packages/coding-agent/examples/extensions/todo.ts';
  const isTodo = row => row.sourcePath.replaceAll('\\', '/').endsWith(sourcePath);
  const todo = loadReceipt.tools.find(row => row.name === 'todo' && isTodo(row)); assert(todo);
  const sourceReceipts = [], sourceOriginals = [];
  const sourceSnapshot = () => readSessionSnapshot('json', JSON.stringify({
    sessionId: reference.session.getSessionId(), generation: 1, selectedLeafId: reference.session.getLeafId(),
    branchEntries: reference.session.getBranch(), persistence: 'VolatileMemory'
  }));
  const capabilities = { mode: 'print', connectionGeneration: 0, sessionGeneration: 0, features: [] };
  const noHost = () => { throw new Error('Source-only lifecycle differential grants no native host calls'); };
  const invoke = async (kind, callbackId, argument) => {
    const original = bridge.invoke(kind, callbackId, argument, undefined, undefined, capabilities, signal, noHost,
      kind === 'tool' ? 'source-only-todo-call' : undefined, undefined, sourceSnapshot());
    sourceOriginals.push(original); const receipt = await original; sourceReceipts.push(receipt);
    assert.equal(receipt.status, 'fulfilled', JSON.stringify(receipt));
    assert.equal(receipt.publicationJoined, true); assert.equal(receipt.publicationCount, 0); return receipt;
  };
  const emit = async event => {
    await reference.join(reference.runner.emit(event));
    const handler = loadReceipt.sessionHandlers.find(row => row.topic === event.type && isTodo(row)); assert(handler);
    const receipt = await invoke(event.type, handler.callbackId, event);
    assert.equal(receipt.resultPresence, 'undefined');
  };
  const execute = async params => {
    const expected = await reference.execute(params);
    const prepared = bridge.prepare(todo.callbackId, params, signal); assert.equal(prepared.status, 'fulfilled');
    const receipt = await invoke('tool', todo.callbackId, JSON.parse(prepared.preparedJson));
    assert.deepEqual(JSON.parse(receipt.resultJson), expected); return expected;
  };
  const first = [{ id: 7, text: 'active A', done: false }], second = [{ id: 9, text: 'future B', done: true }];
  let failure;
  try {
    if (caseId === 'loader-todo-pure-transitions') {
      await emit({ type: 'session_start', reason: 'new' });
      await execute({ action: 'add', text: 'pure' }); await execute({ action: 'toggle', id: 1 });
      await execute({ action: 'clear' }); await execute({ action: 'add', text: 'after clear' });
    } else {
      const a = reference.snapshot(first, 8), b = reference.snapshot(second, 10);
      await emit({ type: 'session_start', reason: 'resume' }); await execute({ action: 'list' });
      if (caseId === 'loader-todo-tree-selected-branch') {
        reference.session.branch(a); await emit({ type: 'session_tree', oldLeafId: b, newLeafId: a });
        assert.deepEqual((await execute({ action: 'list' })).details.todos, first);
        reference.session.branch(b); await emit({ type: 'session_tree', oldLeafId: a, newLeafId: b });
        assert.deepEqual((await execute({ action: 'list' })).details.todos, second);
      } else if (caseId === 'loader-todo-empty-branch') {
        reference.session.resetLeaf(); await emit({ type: 'session_tree', oldLeafId: b, newLeafId: null });
        assert.deepEqual((await execute({ action: 'list' })).details.todos, []);
      }
    }
    assert.equal(reference.session.getSessionFile(), undefined);
  } catch (error) { failure = error; }
  const faults = failure ? [failure] : [];
  for (const original of new Set(sourceOriginals)) try { await original; } catch (error) { if (!faults.includes(error)) faults.push(error); }
  if (faults.length) throw new AggregateError(faults, 'Loader differential originals failed');
  return { caseId, sourceReceipts, genuineOriginalReference: true, sourceOnlySnapshotProvider: true,
    nativeLifecycleParityQualified: false, rgExecuteInvoked: false, nativeHostCalls: 0 };
}
