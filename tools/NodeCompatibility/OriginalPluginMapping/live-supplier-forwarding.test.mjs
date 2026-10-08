import test from 'node:test';
import assert from 'node:assert/strict';
import { createOriginalPluginMapping } from './original-plugin-mapping.mjs';

test('original-live-supplier.optional-file-mutation-queue-retains-reference-without-invocation', () => {
  let calls = 0; const result = Promise.resolve('original result');
  const original = () => { calls++; return result; };
  const map = createOriginalPluginMapping({ Type: { Object() {}, String() {} }, defineTool: value => value, withFileMutationQueue: original });
  const current = map.resolve('@earendil-works/pi-coding-agent');
  assert.strictEqual(current, map.resolve('@mariozechner/pi-coding-agent'));
  assert.strictEqual(current.withFileMutationQueue, original); assert.equal(calls, 0);
  assert.strictEqual(current.withFileMutationQueue('unused', () => {}), result); assert.equal(calls, 1);
});

test('original-live-supplier.absent-or-invalid-queue-is-not-fabricated', () => {
  const supplied = { Type: { Object() {}, String() {} }, defineTool: value => value };
  assert.throws(() => createOriginalPluginMapping(supplied).named('@earendil-works/pi-coding-agent', 'withFileMutationQueue'), TypeError);
  for (const withFileMutationQueue of [null, {}, true])
    assert.throws(() => createOriginalPluginMapping({ ...supplied, withFileMutationQueue }), TypeError);
});
