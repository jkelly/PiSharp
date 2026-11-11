import assert from 'node:assert/strict';
import { ExtensionRuntime, describeExtension } from './pi-host/runtime.mjs';
import { translateRegistrations } from '../NodeCommandInputBridge/admission.mjs';

const notifications = [];
const runtime = new ExtensionRuntime({ notify: (...args) => notifications.push(args) }, { cwd: process.cwd() });
function extension(name) {
  const ext = runtime.createExtension(name, name);
  runtime.extensions.push(ext);
  const load = runtime.createExtensionAPI(ext);
  load.commit();
  return { ext, api: load.api };
}
const surfaces = ['user', 'assistant', 'assistant-thinking', 'custom', 'summary', 'changelog'];
const calls = [];
const first = extension('first');
first.api.registerMarkdownTransformer((text, context) => { calls.push(context); return text + 'A'; });
assert.equal(describeExtension(first.ext).markdownTransformer, true, 'registration object reports presence');
for (const type of surfaces) {
  const context = { messageType: type, isStreaming: false, availableWidth: 38, messageId: 'persisted', timestamp: '2026-11-10T00:00:00Z' };
  assert.equal(runtime.transformMarkdown('x', context), surfaces.indexOf(type) < 3 ? 'xA' : 'x', 'legacy defaults: ' + type);
}
assert.equal(calls.length, 3);
assert.equal(calls[0].messageId, 'persisted');
assert.equal(calls[0].availableWidth, 38);
first.api.registerMarkdownTransformer(text => text + 'never', { messageTypes: [] });
for (const type of surfaces) assert.equal(runtime.transformMarkdown('x', { messageType: type }), 'x', 'explicit empty: ' + type);
first.api.registerMarkdownTransformer(text => text + 'A', { messageTypes: ['custom', 'summary', 'changelog'] });
const second = extension('second');
second.api.registerMarkdownTransformer(text => text + 'B', { messageTypes: surfaces });
const failed = extension('failed');
failed.api.registerMarkdownTransformer(() => { throw new Error('expected'); }, { messageTypes: surfaces });
const invalid = extension('invalid');
invalid.api.registerMarkdownTransformer(() => undefined, { messageTypes: surfaces });
const last = extension('last');
last.api.registerMarkdownTransformer(text => text + 'C', { messageTypes: surfaces });
for (const type of surfaces) assert.equal(runtime.transformMarkdown('x', { messageType: type }), surfaces.indexOf(type) < 3 ? 'xBC' : 'xABC', 'subset, order, errors: ' + type);
assert.equal(runtime.transformMarkdown('x', { messageType: 'Custom' }), 'x', 'matching is exact');
assert.equal(runtime.transformMarkdown('x', undefined), 'x', 'missing type is not admitted');
assert.equal(notifications.at(-1)[1].extension.markdownTransformer, true, 'late registration notification preserves presence');
assert.throws(() => translateRegistrations([first.ext], runtime), error => error.bridgeCode === 'UnsupportedRegistration' && error.surface === 'markdownTransformer', 'bounded command bridge explicitly rejects registration objects');
console.log('PASS Markdown transformer defaults, all six surfaces, subset/empty, replacement, load order, metadata, isolation, command admission');
