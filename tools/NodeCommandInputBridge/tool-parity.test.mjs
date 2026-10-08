// Authored deterministic controls; coordinator owns execution.
import assert from 'node:assert/strict';
import test from 'node:test';
import { createToolProgress } from './tool-progress.mjs';
import { createOriginalToolContext, unsupportedToolOperation } from './tool-context.mjs';
const gate = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const observe = value => structuredClone(value);
const update = text => ({ content: [{ type: 'text', text }], details: {} });
test('progress preserves synchronous callback return, snapshots values, serializes delivery and joins', async () => {
  const hold = gate(), started = gate(), calls = []; let concurrent = 0, maximum = 0;
  const progress = createToolProgress(new AbortController().signal, async (sequence, raw) => {
    concurrent++; maximum = Math.max(maximum, concurrent); calls.push([sequence, JSON.parse(raw)]);
    if (sequence === 1) { started.resolve(); await hold.promise; } concurrent--; return { delivered: true, sequence };
  }, observe);
  let pending, failed = false;
  try {
    const first = update('first'); assert.equal(progress.onUpdate(first), undefined); first.content[0].text = 'mutated';
    progress.onUpdate(update('second')); await started.promise;
    let joined = false; pending = progress.join().then(value => { joined = true; return value; });
    await Promise.resolve(); assert.equal(joined, false); assert.equal(calls.length, 1);
    hold.resolve(); const receipt = await pending;
    assert.equal(maximum, 1); assert.equal(calls[0][1].content[0].text, 'first');
    assert.deepEqual(calls.map(row => row[0]), [1, 2]); assert.equal(receipt.deliveredUpdates, 2); assert.equal(receipt.updateDeliveryJoined, true);
  } catch (error) { failed = true; throw error; }
  finally {
    hold.resolve();
    try { await (pending ?? progress.join()); } catch (cleanup) { if (!failed) throw cleanup; }
  }
});
test('count overflow remains a failed publication even if source catches it', async () => {
  const progress = createToolProgress(new AbortController().signal, async sequence => ({ delivered: true, sequence }), observe);
  for (let i = 0; i < 16; i++) progress.onUpdate(update(String(i)));
  assert.throws(() => progress.onUpdate(update('overflow')), /backpressure/);
  assert.match((await progress.join()).updateFailure.message, /backpressure/);
});
test('frame byte budget rejects before native publication', async () => {
  let calls = 0; const progress = createToolProgress(new AbortController().signal, async () => { calls++; }, observe);
  assert.throws(() => progress.onUpdate(update('x'.repeat(65536))), /byte budget/);
  assert.equal((await progress.join()).updateCount, 0); assert.equal(calls, 0);
});
test('cumulative byte budget and non-JSON values fail without dropping the diagnostic', async () => {
  const progress = createToolProgress(new AbortController().signal, async sequence => ({ delivered: true, sequence }), observe);
  for (let i = 0; i < 4; i++) progress.onUpdate(update('x'.repeat(60000)));
  assert.throws(() => progress.onUpdate(update('x'.repeat(60000))), /byte budget/);
  assert.ok((await progress.join()).updateFailure);
  const invalid = createToolProgress(new AbortController().signal, async () => assert.fail('invalid update published'), observe);
  assert.throws(() => invalid.onUpdate(undefined), /Non-JSON/);
  assert.ok((await invalid.join()).updateFailure);
});
test('nested functions, symbols, arbitrary objects and pre-cancelled updates never publish', async () => {
  for (const value of [{ callback() {} }, { [Symbol('x')]: 1 }, { date: new Date() }]) {
    const progress = createToolProgress(new AbortController().signal, async () => assert.fail('non-JSON update published'), value => value);
    assert.throws(() => progress.onUpdate(value)); assert.ok((await progress.join()).updateFailure);
  }
  const controller = new AbortController(); controller.abort();
  const progress = createToolProgress(controller.signal, async () => assert.fail('cancelled update published'), observe);
  assert.throws(() => progress.onUpdate(update('cancelled')), { name: 'AbortError' }); assert.ok((await progress.join()).updateFailure);
});
test('failed or mismatched native receipt cannot become successful settlement', async () => {
  for (const receipt of [{ delivered: false, sequence: 1 }, { delivered: true, sequence: 2 }]) {
    const progress = createToolProgress(new AbortController().signal, async () => receipt, observe);
    progress.onUpdate(update('x')); assert.match((await progress.join()).updateFailure.message, /matching native/);
  }
});
test('falsy publication rejection is retained and suppresses queued native work', async () => {
  let calls = 0; const progress = createToolProgress(new AbortController().signal, async () => { calls++; throw undefined; }, observe);
  progress.onUpdate(update('a')); progress.onUpdate(update('b'));
  const result = await progress.join(); assert.equal(calls, 1); assert.equal(result.updateFailure.message, 'undefined');
});
test('cancellation joins an admitted publication and prevents queued publication', async () => {
  const controller = new AbortController(), hold = gate(), started = gate(); let calls = 0;
  const progress = createToolProgress(controller.signal, async sequence => { calls++; started.resolve(); await hold.promise; return { delivered: true, sequence }; }, observe);
  let pending, failed = false;
  try {
    progress.onUpdate(update('a')); progress.onUpdate(update('b')); await started.promise; controller.abort();
    let settled = false; pending = progress.join().then(value => { settled = true; return value; });
    await Promise.resolve(); assert.equal(settled, false); hold.resolve();
    assert.ok((await pending).updateFailure); assert.equal(calls, 1);
  } catch (error) { failed = true; throw error; }
  finally {
    hold.resolve();
    try { await (pending ?? progress.join()); } catch (cleanup) { if (!failed) throw cleanup; }
  }
});
test('retained source callback rejects after settlement without new effects', async () => {
  let calls = 0; const progress = createToolProgress(new AbortController().signal, async sequence => { calls++; return { delivered: true, sequence }; }, observe);
  await progress.join(); assert.throws(() => progress.onUpdate(update('late')), /admission closed/); assert.equal(calls, 0);
});
test('proper tool context receives parent identity and default cancellation signal', async () => {
  const signal = new AbortController().signal, captured = [], getTools = unsupportedToolOperation('ctx.tools'), execute = unsupportedToolOperation('ctx.executeTool');
  const runner = { createToolContext(id, currentSignal) { captured.push([id, currentSignal]);
    return Object.defineProperties({}, { tools: { get: getTools }, executeTool: { value: (name, args, options = {}) => execute(name, args, { ...options, parentToolCallId: id, signal: options.signal ?? currentSignal }) } }); } };
  const context = createOriginalToolContext(runner, 'actual-parent', signal);
  assert.deepEqual(captured, [['actual-parent', signal]]);
  assert.throws(() => context.tools, error => error.bridgeCode === 'UnsupportedHostOperation' && error.surface === 'ctx.tools');
  assert.throws(() => context.executeTool('read', {}), error => error.surface === 'ctx.executeTool');
  assert.throws(() => createOriginalToolContext({ createToolContext: () => ({}) }, 'actual-parent', signal), /guarded tools getter/);
});
