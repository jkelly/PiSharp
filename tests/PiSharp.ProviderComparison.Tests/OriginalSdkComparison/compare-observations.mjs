// Separate builtin-only comparator. No input is promoted to an observed SDK capture.
// Future root supplies successful stdout JSON from BOTH independently admitted executions.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { isAbsolute } from 'node:path';
const [nativePath, originalPath] = process.argv.slice(2);
assert.equal(process.argv.length, 4); assert([nativePath, originalPath].every(isAbsolute));
function read(path) { const bytes = readFileSync(path); assert(bytes.length <= 262144); return JSON.parse(bytes); }
const native = read(nativePath), original = read(originalPath);
assert.equal(native.implementation, 'native'); assert.equal(original.implementation, 'original');
assert.equal(native.schemaVersion, 1); assert.equal(original.schemaVersion, 1);
assert(!native.failed && !original.failed); assert.equal(native.inputSha256, original.inputSha256);
assert.equal(native.originalCommit, 'd86654abb8862e201933517d6f1fce9f88dd117f'); assert.equal(original.originalCommit, native.originalCommit);
const names = ['pi-messages.direct-settled-tool-alias','pi-messages.simple-settled-tool-alias'];
assert.deepEqual(native.cases.map(c => c.name), names); assert.deepEqual(original.cases.map(c => c.name), names);
assert(native.originals.length >= 6 && native.originals.every(r => r.Joined && r.status === 'RanToCompletion'));
assert(original.originals.length === 6 && original.originals.every(r => r.joined));
const rows = names.map((name, index) => {
  const n = native.cases[index], o = original.cases[index];
  assert.deepEqual(n.request, o.request, name + ': genuine request');
  assert.deepEqual(n.finalCall, o.finalCall, name + ': authoritative final value');
  assert.deepEqual(o.finalCall, { type: 'toolCall', id: 'final-call', name: 'other', arguments: { value: 7 } });
  assert.equal(o.sameStartEnd, true); assert.equal(n.sameStartEnd, false);
  assert.deepEqual(o.settledStart, o.finalCall);
  assert.deepEqual(n.settledStart, { type: 'toolCall', id: 'provisional-call', name: 'inspect', arguments: {} });
  assert.deepEqual(o.deltaArgumentsAfterEnd, { value: 1 });
  assert.deepEqual(o.emissionStart, n.settledStart); assert.equal(n.responseDisposed, true);
  return { name, requestAndFinalValueMatched: true, settledEarlierReference: 'EXPLICIT DIFFERENCE: JS mutable alias versus native immutable value', originalPartialArgumentsDetachedOnEnd: true };
});
console.log(JSON.stringify({ schemaVersion: 1, originalCommit: original.originalCommit, inputSha256: native.inputSha256,
  actualOriginalObservationCompared: true, cases: rows, mandatorySixtyMapping: null, fullParity: false }));
