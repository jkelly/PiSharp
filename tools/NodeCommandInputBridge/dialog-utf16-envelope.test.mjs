// Source controls only. The genuine caller/gate and strict outer parser are imported; no worker or native UI starts.
import test from 'node:test';
import assert from 'node:assert/strict';
import { decodeOriginalDialogOutcome, createOriginalDialogCaller, createDialogWorkerGate } from './module-loader.mjs';
import { strictJson } from '../NodeBridge/wire.mjs';
const encode = value => JSON.stringify(value).replace(/[^\x00-\x7f]/g, unit => '\\u' + unit.charCodeAt(0).toString(16).padStart(4, '0'));
const envelope = value => ({ outcome: 'value', presence: 'json', valueJson: encode(value) });
const frame = value => strictJson(JSON.stringify(value));
const deferred = () => { let resolve, reject; const original = new Promise((yes, no) => { resolve = yes; reject = no; }); original.catch(() => {}); return { original, resolve, reject }; };
test('dialog nested ASCII preserves lone units, pairs, emoji, quotes, controls and literal backslash-u exactly once', () => {
  for (const value of ['', '\ud800', '\udfff', 'a\ud800b\udfff', '\ud83d\ude00', 'emoji😀', '"quote"', '\\ud800', '\\uD800', '\0\b\t\n\f\r', '\\"\ud800']) {
    for (const method of ['ui.select', 'ui.input', 'ui.editor']) {
      const receipt = envelope(value); assert(/^[\x00-\x7f]*$/u.test(receipt.valueJson));
      assert.equal(decodeOriginalDialogOutcome(method, frame(receipt)), value);
    }
  }
  assert.throws(() => strictJson('{"value":"\\ud800"}')); // Outer codec remains strict.
});
test('dialog outcome rejects ambiguous fields, borrowed methods, wrong kinds and non-string literals', () => {
  for (const value of [null, [], { ...envelope('x'), value: 'x' }, { outcome: 'value', presence: 'json' },
    { ...envelope('x'), extra: true }, { ...envelope('x'), presence: 'undefined' }, { ...envelope('x'), outcome: 'unknown' },
    { outcome: 'value', presence: 'json', value: 'x' }, { ...envelope('x'), valueJson: 1 },
    ...['null', 'true', '1', '[]', '{}'].map(valueJson => ({ ...envelope('x'), valueJson }))]) {
    assert.throws(() => decodeOriginalDialogOutcome('ui.editor', value));
  }
  assert.throws(() => decodeOriginalDialogOutcome('ui.notify', envelope('x')));
  assert.throws(() => decodeOriginalDialogOutcome('ui.confirm', envelope('true')));
  assert.throws(() => decodeOriginalDialogOutcome('ui.confirm', { outcome: 'value', presence: 'json', value: 'true' }));
});
test('malformed, trailing, non-ASCII and oversized nested text reject without scalar normalization', () => {
  for (const valueJson of ['"\\uZZZZ"', '"\\x41"', '"unterminated', ' "x"', '"x" ', '"x""y"', '"raw\nline"', '"😀"', '"\ud800"', '"' + 'a'.repeat(393217) + '"']) {
    assert.throws(() => decodeOriginalDialogOutcome('ui.input', { outcome: 'value', presence: 'json', valueJson }));
  }
  assert.throws(() => decodeOriginalDialogOutcome('ui.input', envelope('a'.repeat(65537))));
  const maximum = '\ud800'.repeat(65536), receipt = envelope(maximum); assert.equal(receipt.valueJson.length, 393218);
  assert(Buffer.byteLength(JSON.stringify(receipt)) < 1048576); assert.equal(decodeOriginalDialogOutcome('ui.editor', frame(receipt)), maximum);
});
test('confirm booleans and undefined outcomes retain original semantics with exact shapes', () => {
  for (const value of [true, false]) assert.equal(decodeOriginalDialogOutcome('ui.confirm', frame({ outcome: 'value', presence: 'json', value })), value);
  for (const outcome of ['cancelled', 'timedOut', 'unavailable']) for (const method of ['ui.confirm', 'ui.input', 'ui.select', 'ui.editor']) {
    const receipt = { outcome, presence: 'undefined' }; assert.equal(decodeOriginalDialogOutcome(method, receipt), method === 'ui.confirm' ? false : undefined);
    assert.throws(() => decodeOriginalDialogOutcome(method, { ...receipt, valueJson: '"x"' }));
  }
  assert.equal(decodeOriginalDialogOutcome('ui.editor', { outcome: 'unavailable', presence: 'undefined', reason: 'NoUi' }), undefined);
  assert.throws(() => decodeOriginalDialogOutcome('ui.editor', { outcome: 'cancelled', presence: 'undefined', reason: 'NoUi' }));
});
test('actual caller/gate keeps the original pending until child retirement before decoding UTF16', async () => {
  const active = { settled: false }, retirement = deferred(), retiring = deferred(), stages = [];
  const identity = { dialogId: 'dialog-1', scopeId: 'actual-scope', nativeSessionGeneration: 7 };
  const gate = createDialogWorkerGate({ operationId: 'actual-operation', nativeSessionGeneration: 7, call: (method, payload) => {
    stages.push([method, payload]);
    if (method === 'ui.dialog.reserve') return Promise.resolve({ reserved: true, ...identity });
    if (method === 'ui.input') return Promise.resolve(frame(envelope('exact\ud800\n😀')));
    assert.equal(method, 'ui.dialog.retire'); retiring.resolve(); return retirement.original;
  } });
  const api = createOriginalDialogCaller({ current: () => active, trace: () => {}, native: gate });
  const original = api.call('ui.input', ['title']), joined = api.join(active); let settled = false;
  original.then(() => { settled = true; }, () => { settled = true; });
  try { await retiring.original; assert(!settled); retirement.resolve({ retired: true, dialogId: identity.dialogId });
    assert.equal(await original, 'exact\ud800\n😀'); await joined; assert.equal(stages.length, 3);
  } finally { retirement.resolve({ retired: true, dialogId: identity.dialogId }); await Promise.allSettled([original, joined]); }
});
test('malformed result and original retirement fault still join the held public original', async () => {
  const active = { settled: false }, retirement = deferred(), retiring = deferred(), fault = Error('actual retirement original');
  const identity = { dialogId: 'dialog-1', scopeId: 'actual-scope', nativeSessionGeneration: 7 };
  const api = createOriginalDialogCaller({ current: () => active, trace: () => {}, native: method => {
    if (method === 'ui.dialog.reserve') return Promise.resolve({ reserved: true, ...identity });
    if (method === 'ui.input') return Promise.resolve({ ...envelope('x'), value: 'ambiguous' });
    retiring.resolve(); return retirement.original;
  } });
  const original = api.call('ui.input', ['title']), joined = api.join(active); original.catch(() => {}); joined.catch(() => {});
  try { await retiring.original; retirement.reject(fault); await assert.rejects(original, error => error === fault); await assert.rejects(joined, error => error === fault); }
  finally { retirement.resolve(); await Promise.allSettled([original, joined]); }
});
