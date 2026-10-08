// Admission metadata for unchanged Pi v0.99.1 d86654abb8862e201933517d6f1fce9f88dd117f.
// This extends the existing trusted worker; it is not an arbitrary-code sandbox.
import assert from 'node:assert/strict';
import { readNamespace } from './tool-loadout.mjs';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { join } from 'node:path';
export const sourceCommit = 'd86654abb8862e201933517d6f1fce9f88dd117f';
export const sourcePins = Object.freeze([
  { path: 'packages/coding-agent/examples/extensions/commands.ts', bytes: 2594, sha256: '36716b53da169936c7e1360a4fde1e2fc0c3356a6505f177235f09c5538c6e4f' },
  { path: 'packages/coding-agent/examples/extensions/input-transform.ts', bytes: 1444, sha256: 'cf0f65d610631ca75d18aae1c8f1408139702f674cb2835f8c009b943776474b' },
  { path: 'packages/coding-agent/examples/extensions/pirate.ts', bytes: 1461, sha256: 'dd6ce684bbe7630e4a749fe133ef8b8b875adbc7e31284b17fe0b8157d51e013' },
  { path: 'packages/coding-agent/examples/extensions/hello.ts', bytes: 640, sha256: '0aa4e9800c2526914d4c1edb00b2cfa9bd9dd6da5218289994cccd5f5bfa4934' },
  // Exact unchanged consumers. Original rg renderer callbacks are retained;
  // unsupported lifecycle and custom presentation fields diagnose explicitly.
  { path: 'packages/coding-agent/examples/extensions/todo.ts', bytes: 8848, sha256: 'e46824d00217e25242c186d41837cc84ca81b23f978500323448502a9a424ee2' },
  { path: 'packages/coding-agent/examples/extensions/truncated-tool.ts', bytes: 6490, sha256: '4ed5fbeb6da53eab8d1e04721d77961b8d1b3bf5e11d1512a17fc5a3f58f9955' }
].map(Object.freeze));
export function selectSources(root, requested, read = fs.readFileSync) {
  assert(Array.isArray(requested) && requested.length > 0 && requested.length <= 8, 'Source admission count');
  return requested.map(path => {
    const pin = sourcePins.find(row => row.path === path);
    assert(pin, 'Unadmitted extension source: ' + String(path));
    const absolute = join(root, 'upstream', path), bytes = read(absolute);
    assert.equal(bytes.length, pin.bytes, 'Source byte pin: ' + path);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), pin.sha256, 'Source hash pin: ' + path);
    return { ...pin, absolute };
  });
}
export function unsupported(surface) {
  const error = new Error('Unsupported Node bridge registration: ' + surface);
  error.bridgeCode = 'UnsupportedRegistration'; error.surface = surface; throw error;
}
export function translateRegistrations(extensions, runtime) {
  const commands = [], inputs = [], beforeAgentStart = [], sessionHandlers = [], tools = [], ids = new Set(), names = new Set();
  let count = 0;
  const add = (list, row) => {
    assert(++count <= 64, 'Registration admission count');
    assert(!ids.has(row.callbackId), 'Duplicate callback identity'); ids.add(row.callbackId); list.push(row);
  };
  const name = (kind, value) => {
    assert(typeof value === 'string' && value.length > 0 && value.length <= 128, 'Registration name');
    const key = kind + ':' + value; assert(!names.has(key), 'Duplicate registration name: ' + key); names.add(key);
  };
  for (const [index, extension] of extensions.entries()) {
    for (const field of ['flags', 'shortcuts', 'messageRenderers', 'entryRenderers']) if (extension[field].size) unsupported(field);
    if (extension.markdownTransformer !== undefined) unsupported('markdownTransformer');
    for (const [event, callbacks] of extension.handlers) {
      if (!['input', 'before_agent_start', 'session_start', 'session_tree'].includes(event)) unsupported('on.' + event);
      for (const [ordinal, callback] of callbacks.entries()) {
        assert(typeof callback === 'function');
        add(event === 'input' ? inputs : event === 'before_agent_start' ? beforeAgentStart : sessionHandlers, { callbackId: event + '-' + (index + 1) + '-' + (ordinal + 1), sourcePath: extension.path, callback, ...(['session_start', 'session_tree'].includes(event) ? { topic: event, extensionIndex: index } : {}) });
      }
    }
    for (const command of extension.commands.values()) {
      name('command', command.name); assert(typeof command.handler === 'function');
      if (command.getArgumentCompletions !== undefined && typeof command.getArgumentCompletions !== 'function') unsupported('command.' + command.name + '.getArgumentCompletions');
      assert(typeof command.description === 'string' && command.description.length <= 65536);
      add(commands, { callbackId: 'command-' + (index + 1) + '-' + command.name, name: command.name, description: command.description,
        sourcePath: extension.path, handler: command.handler, completion: command.getArgumentCompletions });
    }
    for (const registered of extension.tools.values()) {
      const tool = registered.definition;
      // Native descriptors do not carry these semantics. Reject instead of silently flattening them.
      for (const field of ['prepareArguments', 'promptSnippet', 'promptGuidelines',
        'constrainedSampling', 'renderShell', 'outputSchema', 'exposure', 'annotations', 'defaultActive', 'executionMode'])
        if (tool[field] !== undefined) unsupported('tool.' + tool.name + '.' + field);
      for (const field of ['renderCall', 'renderResult'])
        if (tool[field] !== undefined && typeof tool[field] !== 'function') unsupported('tool.' + tool.name + '.' + field);
      if (tool.prepareLoadout !== undefined && typeof tool.prepareLoadout !== 'function') unsupported('tool.' + tool.name + '.prepareLoadout');
      const namespace = tool.namespace === undefined ? undefined : readNamespace(tool.namespace);
      name('tool', tool.name); assert(typeof tool.execute === 'function');
      assert(typeof tool.description === 'string' && tool.description.length <= 65536);
      const parametersJson = JSON.stringify(tool.parameters); assert(Buffer.byteLength(parametersJson) <= 65536, 'Tool schema budget');
      add(tools, { callbackId: 'tool-' + (index + 1) + '-' + tool.name, name: tool.name, description: tool.description,
        sourcePath: extension.path, parametersJson, namespace, hasLoadoutPreparation: typeof tool.prepareLoadout === 'function',
        hasRenderCall: typeof tool.renderCall === 'function', hasRenderResult: typeof tool.renderResult === 'function', definition: tool, execute: tool.execute });
    }
  }
  for (const field of ['pendingProviderRegistrations', 'pendingNativeProviderRegistrations', 'pendingVirtualModelRegistrations']) if (runtime[field].length) unsupported(field);
  if (runtime.mcpServers.list().length) unsupported('mcpServers');
  return { commands, inputs, beforeAgentStart, sessionHandlers, tools };
}
