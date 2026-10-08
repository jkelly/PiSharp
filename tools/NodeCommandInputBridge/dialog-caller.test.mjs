// Source-only controls. Not executed by the authoring lane.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createOriginalDialogCaller, createDialogWorkerGate } from './module-loader.mjs';

const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); promise.catch(() => {}); return { promise, resolve, reject }; };
const cancelled = { outcome: 'cancelled', presence: 'undefined' };
const value = item => typeof item === 'boolean' ? { outcome: 'value', presence: 'json', value: item }
  : { outcome: 'value', presence: 'json', valueJson: JSON.stringify(item).replace(/[^\x00-\x7f]/g, unit => '\\u' + unit.charCodeAt(0).toString(16).padStart(4, '0')) };
const identity = { dialogId: 'dialog-1', scopeId: 'actual-scope', nativeSessionGeneration: 7 };
const reserved = { reserved: true, ...identity };
const retired = { retired: true, dialogId: identity.dialogId };
const cancelAck = { cancelRequested: true, dialogId: identity.dialogId };
const owner = () => ({ settled: false });
const caller = (active, native, beforeOpen) => createOriginalDialogCaller({ current: () => active, trace: () => {}, native, beforeOpen });
const inspectPending = original => { const status = { settled: false }; original.then(() => { status.settled = true; }, () => { status.settled = true; }); return status; };

test('genuine pre-aborted select/input/confirm have no native admission; signal impostors reject', async () => {
  const controller = new AbortController(); controller.abort(); let effects = 0;
  for (const method of ['ui.select', 'ui.input', 'ui.confirm']) {
    const active = owner(), api = caller(active, () => { effects++; throw Error('Unexpected admission'); });
    assert.equal(await api.call(method, ['title', method === 'ui.select' ? ['a'] : 'text', { signal: controller.signal }]), method === 'ui.confirm' ? false : undefined);
    await api.join(active); assert.equal(active.dialogCalls, undefined);
  }
  const active = owner(), api = caller(active, () => { effects++; });
  assert.throws(() => api.call('ui.input', ['title', '', { signal: { aborted: true } }]));
  assert.equal(effects, 0);
});

test('abort while reserve is held waits for exact scope then joins open, cancel and retirement independently', async () => {
  const reservation = deferred(), reserving = deferred(), opened = deferred(), open = deferred(), cancellation = deferred(), retirement = deferred(), retiring = deferred();
  const active = owner(), controller = new AbortController(), stages = [];
  const api = caller(active, (method, payload) => {
    stages.push([method, payload]);
    if (method === 'ui.dialog.reserve') { reserving.resolve(); return reservation.promise; }
    if (method === 'ui.input') { opened.resolve(); return open.promise; }
    if (method === 'ui.dialog.cancel') return cancellation.promise;
    assert.equal(method, 'ui.dialog.retire'); retiring.resolve(); return retirement.promise;
  });
  const original = api.call('ui.input', ['title', '', { signal: controller.signal }]), status = inspectPending(original);
  const joined = api.join(active);
  try {
    await reserving.promise; controller.abort(); assert.equal(stages.some(([method]) => method === 'ui.dialog.cancel'), false);
    reservation.resolve(reserved); await opened.promise;
    assert.deepEqual(stages.map(([method]) => method), ['ui.dialog.reserve', 'ui.dialog.cancel', 'ui.input']);
    open.resolve(cancelled); await Promise.resolve(); assert.equal(status.settled, false);
    cancellation.resolve(cancelAck); await retiring.promise; assert.equal(status.settled, false);
    retirement.resolve(retired); assert.equal(await original, undefined); await joined;
    assert.equal(active.dialogCalls[0].original, original);
    assert.deepEqual(stages[1][1], identity);
  } finally {
    reservation.resolve(reserved); open.resolve(cancelled); cancellation.resolve(cancelAck); retirement.resolve(retired);
    await Promise.allSettled([original, joined]);
  }
});

test('complete dropped dialog original is admitted before publication wait and joined before owner settlement', async () => {
  const publication = deferred(), opened = deferred(), open = deferred(), retirement = deferred(), retiring = deferred();
  const active = owner(), api = caller(active, method => {
    if (method === 'ui.dialog.reserve') return Promise.resolve(reserved);
    if (method === 'ui.input') { opened.resolve(); return open.promise; }
    assert.equal(method, 'ui.dialog.retire'); retiring.resolve(); return retirement.promise;
  }, () => publication.promise);
  const original = api.call('ui.input', ['title']), joined = api.join(active), status = inspectPending(joined);
  try {
    assert.equal(api.join(active), joined);
    assert.equal(active.dialogCalls[0].original, original);
    assert.throws(() => api.call('ui.input', ['late'])); assert.equal(status.settled, false);
    publication.resolve(); await opened.promise; assert.equal(status.settled, false);
    open.resolve(value('')); await retiring.promise; assert.equal(status.settled, false);
    retirement.resolve(retired); await joined; assert.equal(await original, '');
  } finally { publication.resolve(); open.resolve(value('')); retirement.resolve(retired); await Promise.allSettled([original, joined]); }
});

test('open, cancellation write and retirement faults retain each original reference after all joins', async () => {
  const opened = deferred(), open = deferred(), cancellation = deferred(), retirement = deferred(), retiring = deferred();
  const openFault = Error('open original'), cancelFault = Error('cancel write original'), retireFault = Error('retire original');
  const active = owner(), controller = new AbortController(), api = caller(active, method => {
    if (method === 'ui.dialog.reserve') return Promise.resolve(reserved);
    if (method === 'ui.input') { opened.resolve(); return open.promise; }
    if (method === 'ui.dialog.cancel') return cancellation.promise;
    assert.equal(method, 'ui.dialog.retire'); retiring.resolve(); return retirement.promise;
  });
  const original = api.call('ui.input', ['title', '', { signal: controller.signal }]);
  try {
    await opened.promise; controller.abort(); cancellation.reject(cancelFault); open.reject(openFault);
    await retiring.promise; retirement.reject(retireFault);
    await assert.rejects(original, error => { assert.deepEqual(error.errors, [openFault, cancelFault, retireFault]); return true; });
    await assert.rejects(api.join(active), error => error === undefined ? false : error.errors[0] === openFault && error.errors[1] === cancelFault && error.errors[2] === retireFault);
  } finally { open.resolve(cancelled); cancellation.resolve(cancelAck); retirement.resolve(retired); await Promise.allSettled([original]); }
});

test('signal is stripped, timeout and original values retained; completed dialogs detach listener; editor stays direct', async () => {
  const active = owner(), controller = new AbortController(), stages = [];
  const api = caller(active, (method, payload) => {
    stages.push([method, payload]);
    if (method === 'ui.dialog.reserve') return Promise.resolve(reserved);
    if (method === 'ui.dialog.retire') return Promise.resolve(retired);
    assert(['ui.input', 'ui.editor'].includes(method)); return Promise.resolve(value(method === 'ui.input' ? '' : 'editor\n💬'));
  });
  assert.equal(await api.call('ui.input', ['title 💬', '', { signal: controller.signal, timeout: 0 }]), '');
  controller.abort(); assert.equal(stages.some(([method]) => method === 'ui.dialog.cancel'), false);
  const payload = stages.find(([method]) => method === 'ui.input')[1];
  assert.deepEqual(JSON.parse(payload.suppliedArgumentsJson), ['title 💬', '', { timeout: 0 }]); assert.equal(payload.optionsPresent, true);
  assert.equal(await api.call('ui.editor', ['editor', 'prefill\n💬']), 'editor\n💬');
  assert.deepEqual(stages.at(-1), ['ui.editor', { suppliedArgumentsJson: '["editor","prefill\\n💬"]', optionsPresent: false }]);
  await api.join(active);
});

test('worker binds actual operation and scope, joins cached cancel/retire originals after parent cancellation', async () => {
  const calls = [], cancel = deferred(), retire = deferred(); let admitting = true;
  const gate = createDialogWorkerGate({ operationId: 'actual-op', nativeSessionGeneration: 7,
    assertAdmission: () => assert(admitting), call: (method, payload, bindSignal) => {
      calls.push([method, payload, bindSignal]);
      if (method === 'ui.dialog.reserve') return Promise.resolve(reserved);
      if (method === 'ui.input') return Promise.resolve(cancelled);
      return method === 'ui.dialog.cancel' ? cancel.promise : retire.promise;
    } });
  await gate('ui.dialog.reserve', { method: 'ui.input' });
  assert.throws(() => gate('ui.input', { ...identity, operationId: 'forged', suppliedArgumentsJson: '["title"]', optionsPresent: false }));
  assert.throws(() => gate('ui.input', { ...identity, scopeId: 'borrowed', suppliedArgumentsJson: '["title"]', optionsPresent: false }));
  await gate('ui.input', { ...identity, suppliedArgumentsJson: '["title"]', optionsPresent: false }); admitting = false;
  assert.throws(() => gate('ui.dialog.reserve', { method: 'ui.input' }));
  const cancelledOriginal = gate('ui.dialog.cancel', identity), retiredOriginal = gate('ui.dialog.retire', identity);
  try {
    assert.equal(gate('ui.dialog.cancel', identity), cancelledOriginal); assert.equal(gate('ui.dialog.retire', identity), retiredOriginal);
    assert.deepEqual(calls.map(([method, payload, bindSignal]) => [method, payload.operationId, bindSignal]), [
      ['ui.dialog.reserve', 'actual-op', true], ['ui.input', 'actual-op', true], ['ui.dialog.cancel', 'actual-op', false], ['ui.dialog.retire', 'actual-op', false]]);
    cancel.resolve(cancelAck); retire.resolve(retired); await cancelledOriginal; await retiredOriginal;
    assert.throws(() => gate('ui.input', { ...identity, suppliedArgumentsJson: '["title"]', optionsPresent: false }));
  } finally { cancel.resolve(cancelAck); retire.resolve(retired); await Promise.allSettled([cancelledOriginal, retiredOriginal]); }
});

test('worker retains synchronous cancel/retire initiation faults without retry or replacement', async () => {
  const cancelFault = Error('synchronous cancel'), retireFault = Error('synchronous retire'); let cancellations = 0, retirements = 0;
  const gate = createDialogWorkerGate({ operationId: 'actual-op', nativeSessionGeneration: 7, call: method => {
    if (method === 'ui.dialog.reserve') return Promise.resolve(reserved);
    if (method === 'ui.dialog.cancel') { cancellations++; throw cancelFault; }
    if (method === 'ui.dialog.retire') { retirements++; throw retireFault; }
    throw Error('Unexpected stage');
  } });
  await gate('ui.dialog.reserve', { method: 'ui.input' });
  const cancel = gate('ui.dialog.cancel', identity), retire = gate('ui.dialog.retire', identity);
  assert.equal(gate('ui.dialog.cancel', identity), cancel); assert.equal(gate('ui.dialog.retire', identity), retire);
  await assert.rejects(cancel, error => error === cancelFault); await assert.rejects(retire, error => error === retireFault);
  assert.equal(cancellations, 1); assert.equal(retirements, 1);
});

test('worker enforces generation and monotonic sixteen-child tombstone budget across retirement', async () => {
  let effects = 0;
  const rejected = createDialogWorkerGate({ operationId: 'actual-op', nativeSessionGeneration: 8, call: () => { effects++; return Promise.resolve(reserved); } });
  await assert.rejects(rejected('ui.dialog.reserve', { method: 'ui.input' }));
  const gate = createDialogWorkerGate({ operationId: 'actual-op', nativeSessionGeneration: 7, call: (method, payload) => {
    effects++; return Promise.resolve(method === 'ui.dialog.reserve' ? { reserved: true, dialogId: payload.dialogId, scopeId: 'scope-' + payload.dialogId, nativeSessionGeneration: 7 } : { retired: true, dialogId: payload.dialogId });
  } });
  for (let index = 1; index <= 16; index++) {
    const admission = await gate('ui.dialog.reserve', { method: 'ui.input' }); assert.equal(admission.dialogId, 'dialog-' + index);
    await gate('ui.dialog.retire', { dialogId: admission.dialogId, scopeId: admission.scopeId, nativeSessionGeneration: admission.nativeSessionGeneration });
  }
  const before = effects; assert.throws(() => gate('ui.dialog.reserve', { method: 'ui.input' })); assert.equal(effects, before);
});

test('native timeout receipts preserve original false/undefined; parent lifetime failure remains rejection', async () => {
  for (const method of ['ui.input', 'ui.select', 'ui.confirm']) {
    const active = owner(), stages = [], api = caller(active, (stage, payload) => {
      stages.push([stage, payload]);
      if (stage === 'ui.dialog.reserve') return Promise.resolve(reserved);
      if (stage === 'ui.dialog.retire') return Promise.resolve(retired);
      assert.equal(stage, method); return Promise.resolve({ outcome: 'timedOut', presence: 'undefined' });
    });
    assert.equal(await api.call(method, ['title', method === 'ui.select' ? ['one'] : '', { timeout: 25 }]), method === 'ui.confirm' ? false : undefined);
    assert.deepEqual(JSON.parse(stages[1][1].suppliedArgumentsJson).at(-1), { timeout: 25 }); await api.join(active);
  }
  const parentFault = Error('Generic enclosing cancellation'), active = owner(), stages = [];
  const api = caller(active, method => { stages.push(method); return Promise.reject(parentFault); });
  const original = api.call('ui.input', ['title']);
  await assert.rejects(original, error => error === parentFault); await assert.rejects(api.join(active), error => error === parentFault);
  assert.deepEqual(stages, ['ui.dialog.reserve']);
});
