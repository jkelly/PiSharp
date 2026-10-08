// Narrow read-only seam over the actual native callback's admitted active ancestry.
// This is not an original SessionManager or a full-tree/session mutation capability.
import assert from 'node:assert/strict';
import { strictJson } from '../NodeBridge/wire.mjs';
export const maximumSessionSnapshotBytes = 131072;
const exact = (value, keys) => {
  assert(value && typeof value === 'object' && !Array.isArray(value), 'Session snapshot object');
  assert.deepEqual(Object.keys(value).sort(), [...keys].sort(), 'Session snapshot shape');
};
const identity = value => assert(typeof value === 'string' && value.length > 0 && value.length <= 128, 'Session identity');
export function readSessionSnapshot(presence, encoded) {
  assert(['unavailable', 'none', 'json'].includes(presence), 'Session snapshot presence');
  if (presence !== 'json') { assert.equal(encoded, undefined, 'Absent session snapshot has data'); return { presence }; }
  assert(typeof encoded === 'string' && encoded.length <= maximumSessionSnapshotBytes && Buffer.byteLength(encoded) <= maximumSessionSnapshotBytes, 'Session snapshot budget');
  const value = strictJson(encoded);
  exact(value, ['sessionId', 'generation', 'selectedLeafId', 'branchEntries', 'persistence']);
  identity(value.sessionId); assert(Number.isSafeInteger(value.generation) && value.generation > 0, 'Session snapshot generation');
  assert(['DurableLocalFile', 'VolatileMemory', 'DeferredLocalFile'].includes(value.persistence), 'Session persistence');
  assert(Array.isArray(value.branchEntries) && value.branchEntries.length <= 4096, 'Session branch budget');
  const ids = new Set(); let parent = null;
  for (const entry of value.branchEntries) {
    assert(entry && typeof entry === 'object' && !Array.isArray(entry), 'Session branch entry');
    identity(entry.id); identity(entry.type); assert(!ids.has(entry.id), 'Duplicate session entry'); ids.add(entry.id);
    assert.equal(entry.parentId, parent, 'Session branch ancestry'); parent = entry.id;
    assert(typeof entry.timestamp === 'string', 'Session entry timestamp');
  }
  assert.equal(value.selectedLeafId, parent, 'Selected leaf must terminate actual ancestry');
  return { presence, value };
}
function unsupported(surface) {
  const error = new Error('Unsupported Node sessionManager operation: ' + surface);
  error.bridgeCode = 'UnsupportedSessionManager'; error.surface = 'sessionManager.' + surface; throw error;
}
export function createSessionManagerFacade(current, requireCurrent) {
  const snapshot = structuredClone(current.sessionSnapshot);
  const active = () => {
    assert.equal(requireCurrent(), current, 'Session facade belongs to another invocation');
    assert(!current.settled, 'Session facade invocation settled'); current.signal.throwIfAborted();
    if (snapshot?.presence !== 'json') unsupported('snapshot.' + (snapshot?.presence ?? 'unavailable'));
    return snapshot.value;
  };
  const methods = Object.freeze({
    getBranch: (leafId = undefined) => {
      const value = active();
      if (leafId !== undefined && leafId !== value.selectedLeafId) unsupported('getBranch.nonCurrentLeaf');
      return structuredClone(value.branchEntries);
    },
    getLeafId: () => active().selectedLeafId,
    getSessionId: () => active().sessionId
  });
  return new Proxy(methods, { get(target, key) {
    // Inspection does not create authority; all supported calls validate the actual ALS owner.
    if (Object.hasOwn(target, key)) return target[key];
    unsupported(String(key));
  } });
}
export function validateSessionEvent(event, snapshot) {
  assert(event && typeof event === 'object' && !Array.isArray(event), 'Session event object');
  if (event.type === 'session_start') {
    const allowed = ['type', 'reason', 'previousSessionFile'];
    assert(Object.keys(event).every(key => allowed.includes(key)), 'Session start fields');
    assert(['startup', 'reload', 'new', 'resume', 'fork'].includes(event.reason), 'Session start reason');
    if (Object.hasOwn(event, 'previousSessionFile')) assert(typeof event.previousSessionFile === 'string', 'Previous session file');
  } else {
    assert.equal(event.type, 'session_tree', 'Unsupported session event');
    assert(Object.keys(event).every(key => ['type', 'newLeafId', 'oldLeafId', 'summaryEntry', 'fromExtension'].includes(key)), 'Session tree fields');
    for (const field of ['newLeafId', 'oldLeafId']) if (event[field] !== null) identity(event[field]);
    if (Object.hasOwn(event, 'fromExtension')) assert(typeof event.fromExtension === 'boolean', 'Session tree origin');
    if (Object.hasOwn(event, 'summaryEntry')) assert(event.summaryEntry && typeof event.summaryEntry === 'object' && !Array.isArray(event.summaryEntry), 'Session tree summary');
    assert(snapshot?.presence === 'json', 'Session tree requires actual post-navigation snapshot');
    assert.equal(event.newLeafId, snapshot.value.selectedLeafId, 'Session tree snapshot predates navigation');
  }
  return event;
}
