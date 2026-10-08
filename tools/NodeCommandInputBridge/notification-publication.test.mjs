// Authored offline controls; execution requires coordinator allocation.
import assert from 'node:assert/strict';
import test from 'node:test';
import { createNotificationPublication } from './tool-progress.mjs';
const gate = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

test('every falsy publication rejection is retained and suppresses later native work', async () => {
  for (const reason of [undefined, null, false, 0, '', NaN]) {
    const publication = createNotificationPublication(); let calls = 0;
    assert.equal(publication.enqueue(async () => { calls++; throw reason; }), undefined);
    publication.enqueue(async () => { calls++; assert.fail('publication retried after failure'); });
    const receipt = await publication.join();
    assert.equal(receipt.failed, true); assert(Object.hasOwn(receipt, 'failure'));
    assert(Object.is(receipt.failure, reason)); assert.equal(calls, 1);
    const again = await publication.join(); assert(Object.is(again.failure, reason));
  }
});

test('successful notifications return undefined synchronously and serialize native publication', async () => {
  const publication = createNotificationPublication(), hold = gate(), started = gate(), calls = [];
  let joined, maximum = 0, concurrent = 0;
  try {
    assert.equal(publication.enqueue(async () => { concurrent++; maximum = Math.max(maximum, concurrent);
      calls.push('first'); started.resolve(); await hold.promise; concurrent--; }), undefined);
    publication.enqueue(async () => { concurrent++; maximum = Math.max(maximum, concurrent); calls.push('second'); concurrent--; });
    await started.promise;
    let settled = false; joined = publication.join().then(receipt => { settled = true; return receipt; });
    await Promise.resolve(); assert.equal(settled, false); assert.deepEqual(calls, ['first']);
    hold.resolve(); assert.deepEqual(await joined, { failed: false });
    assert.equal(maximum, 1); assert.deepEqual(calls, ['first', 'second']);
  } finally { hold.resolve(); await (joined ?? publication.join()); }
});

test('dialog wait preserves the exact falsy rejection without losing settlement failure', async () => {
  const publication = createNotificationPublication(); publication.enqueue(async () => { throw undefined; });
  let caught = false;
  try { await publication.wait(); } catch (reason) { caught = true; assert.equal(reason, undefined); }
  assert.equal(caught, true); const receipt = await publication.join();
  assert.equal(receipt.failed, true); assert(Object.hasOwn(receipt, 'failure'));
});

test('truthy synchronous and asynchronous failures preserve the original object', async () => {
  for (const asynchronous of [false, true]) {
    const publication = createNotificationPublication(), reason = new Error('native receipt rejected');
    publication.enqueue(asynchronous ? async () => { throw reason; } : () => { throw reason; });
    const receipt = await publication.join(); assert.equal(receipt.failed, true); assert.equal(receipt.failure, reason);
  }
});

test('empty publication settlement succeeds without inventing a failure property', async () => {
  const publication = createNotificationPublication(); await publication.wait();
  assert.deepEqual(await publication.join(), { failed: false });
});
