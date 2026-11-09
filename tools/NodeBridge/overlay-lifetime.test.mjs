import assert from 'node:assert/strict';
import { ExtensionRuntime } from './pi-host/runtime.mjs';

const calls = [], notifications = [];
const bridge = {
  sync: (method) => { calls.push(method); return { hidden: false, focused: true, row: 2, col: 3, width: 10, height: 1 }; },
  notify: (method, params) => notifications.push({ method, params }),
  call: (method, params) => { calls.push({ method, params }); return new Promise(() => {}); },
};
const runtime = new ExtensionRuntime(bridge, { generation: 7, cwd: process.cwd(), hasUI: true, mode: 'interactive' });
const ui = runtime.createUIContext({});
let disposed = 0;
assert.equal(await ui.custom((_t, _h, _k, done) => { done('sync'); done('twice'); return { dispose: () => disposed++ }; }), 'sync');
assert.equal(disposed, 1);
assert.equal(runtime.components.size, 0);
let finish;
const late = ui.custom((_t, _h, _k, done) => { done('early'); return new Promise(resolve => finish = resolve); });
assert.equal(await late, 'early');
let disposedLate;
const lateDisposal = new Promise(resolve => disposedLate = resolve);
finish({ dispose: () => { disposed++; disposedLate(); } });
await lateDisposal;
assert.equal(disposed, 2);
let handle, optionsCalls = 0;
const open = ui.custom(() => ({ width: 10, render: () => ['x'], dispose: () => disposed++ }), {
  overlay: true, overlayOptions: () => { optionsCalls++; return { visible: (columns, rows) => columns >= 80 && rows >= 20 }; },
  onHandle: value => handle = value,
});
const id = [...runtime.components.keys()][0];
assert.match(id, /^c7-/);
runtime.readyOverlay(id);
runtime.readyOverlay(id);
assert.equal(optionsCalls, 1);
assert.equal(runtime.overlayVisible(id, 80, 24), true);
assert.equal(runtime.overlayVisible(id, 79, 24), false);
handle.setHidden(true);
assert.equal(handle.isHidden(), true); // own pending command precedes the next physical UI turn
handle.unfocus();
assert.equal(handle.isFocused(), false);
runtime.invalidate();
await open;
const before = calls.length + notifications.length;
handle.hide(); handle.focus(); handle.setHidden(false); handle.unfocus();
assert.equal(handle.isHidden(), true);
assert.equal(handle.isFocused(), false);
assert.equal(handle.getBounds(), undefined);
assert.equal(runtime.overlayVisible(id, 120, 40), false);
assert.equal(calls.length + notifications.length, before, 'stale handles never reach the bridge');
assert.equal(disposed, 3);
const replacement = new ExtensionRuntime(bridge, { generation: 8, cwd: process.cwd(), hasUI: true, mode: 'interactive' });
let resolveFactory;
const stillCreating = replacement.createUIContext({}).custom(() => new Promise(resolve => resolveFactory = resolve));
replacement.invalidate();
await stillCreating;
let disposedInvalidated;
const invalidatedDisposal = new Promise(resolve => disposedInvalidated = resolve);
resolveFactory({ dispose: () => { disposed++; disposedInvalidated(); } });
await invalidatedDisposal;
assert.equal(disposed, 4, 'invalidated pending factory settles and disposes its eventual component');
console.log('PASS overlay lifetime: sync/late done, predicate, invalidation, stale handles');
