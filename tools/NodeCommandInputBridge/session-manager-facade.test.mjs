// Authored controls; execution belongs to the separately qualified coordinator.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createSessionManagerFacade, readSessionSnapshot, validateSessionEvent } from './session-manager-facade.mjs';
const snapshot = (entries = []) => ({ sessionId: 'actual-session', generation: 3,
  selectedLeafId: entries.at(-1)?.id ?? null, branchEntries: entries, persistence: 'VolatileMemory' });
const entry = (id, parentId = null) => ({ id, parentId, type: 'message', timestamp: '2026-10-06T00:00:00Z',
  message: { role: 'toolResult', toolName: 'todo', details: { todos: [{ id: 7, text: 'retained', done: false }], nextId: 8 } } });
const decode = value => readSessionSnapshot('json', JSON.stringify(value));
test('actual branch is detached, selected ancestry only, with unknown fields retained', () => {
  const value = snapshot([entry('a'), { ...entry('b', 'a'), unknownFutureField: { x: 1 } }]);
  const current = { sessionSnapshot: decode(value), signal: new AbortController().signal, settled: false };
  const facade = createSessionManagerFacade(current, () => current);
  assert.equal(facade.getSessionId(), 'actual-session'); assert.equal(facade.getLeafId(), 'b');
  const first = facade.getBranch(); assert.deepEqual(first, value.branchEntries);
  first[0].message.details.todos[0].text = 'mutated';
  current.sessionSnapshot.value.branchEntries.length = 0;
  assert.equal(facade.getBranch()[0].message.details.todos[0].text, 'retained');
  assert.equal(facade.getBranch().length, 2);
  assert.throws(() => facade.getBranch('a'), /nonCurrentLeaf/);
  for (const method of ['getTree', 'getEntries', 'getSessionFile', 'appendMessage', 'branch'])
    assert.throws(() => facade[method], error => error.bridgeCode === 'UnsupportedSessionManager');
});
test('unavailable and absent snapshots never fabricate an empty session', () => {
  for (const presence of ['none', 'unavailable']) {
    const current = { sessionSnapshot: readSessionSnapshot(presence), signal: new AbortController().signal };
    const facade = createSessionManagerFacade(current, () => current);
    assert.throws(() => facade.getBranch(), /snapshot/);
    assert.throws(() => readSessionSnapshot(presence, '{}'), /Absent/);
  }
  assert.throws(() => readSessionSnapshot('unknown'), /presence/);
});
test('empty admitted branch preserves actual null leaf', () => {
  const current = { sessionSnapshot: decode(snapshot()), signal: new AbortController().signal };
  const facade = createSessionManagerFacade(current, () => current);
  assert.deepEqual(facade.getBranch(), []); assert.equal(facade.getLeafId(), null);
});
test('retained facade checks invocation ownership, cancellation and settlement on every call', () => {
  const abort = new AbortController(), current = { sessionSnapshot: decode(snapshot()), signal: abort.signal };
  let active = current; const facade = createSessionManagerFacade(current, () => active), captured = facade.getBranch;
  active = {}; assert.throws(captured, /another invocation/); active = current;
  current.settled = true; assert.throws(captured, /settled/); current.settled = false;
  abort.abort(new Error('cancel-original')); assert.throws(captured, /cancel-original/);
});
test('malformed, duplicated, disconnected and excessive ancestry rejects without truncation', () => {
  for (const value of [snapshot([entry('a'), entry('b')]), snapshot([entry('a'), entry('a', 'a')]),
    { ...snapshot([entry('a')]), selectedLeafId: 'b' }, { ...snapshot(), generation: 0 },
    { ...snapshot(), persistence: 'unknown' }, { ...snapshot(), extra: true },
    snapshot([{ ...entry('a'), padding: 'x'.repeat(131072) }])]) assert.throws(() => decode(value));
  assert.throws(() => readSessionSnapshot('json', '{"sessionId":"a","sessionId":"b"}'), /DuplicateProperty/);
});
test('tree lifecycle requires post-navigation actual selected leaf; start reasons are bounded', () => {
  const value = decode(snapshot([entry('a')]));
  assert.deepEqual(validateSessionEvent({ type: 'session_tree', newLeafId: 'a', oldLeafId: null }, value),
    { type: 'session_tree', newLeafId: 'a', oldLeafId: null });
  assert.throws(() => validateSessionEvent({ type: 'session_tree', newLeafId: 'b', oldLeafId: 'a' }, value), /predates navigation/);
  assert.throws(() => validateSessionEvent({ type: 'session_tree', newLeafId: null, oldLeafId: 'a' }, { presence: 'none' }), /post-navigation/);
  for (const reason of ['startup', 'reload', 'new', 'resume', 'fork']) validateSessionEvent({ type: 'session_start', reason }, value);
  assert.throws(() => validateSessionEvent({ type: 'session_start', reason: 'invented' }, value), /reason/);
  assert.throws(() => validateSessionEvent({ type: 'session_before_tree' }, value), /Unsupported/);
});
