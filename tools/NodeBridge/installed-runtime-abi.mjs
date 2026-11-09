// Runs only against an explicitly supplied, actually installed package directory. No requests leave this process.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { pathToFileURL } from 'node:url';
import { loadPiModules } from './pi-host/pi-modules.mjs';

const nodeModules = path.resolve(process.argv[2]);
delete process.env.PI_TELEMETRY;
const pi = await loadPiModules(nodeModules, 'dark');
assert.equal(pi.version, '1.5.0');
assert.equal(process.env.PI_TELEMETRY, '0');
const { getClientIdentity } = await import(pathToFileURL(path.join(nodeModules, '@earendil-works/pi-coding-agent/dist/core/client-identity.js')).href);
assert.equal(getClientIdentity({ getEnableInstallTelemetry: () => true }).attribution, null);
process.env.PI_TELEMETRY = '1';
await loadPiModules(nodeModules, 'dark');
assert.equal(process.env.PI_TELEMETRY, '1', 'explicit telemetry environment choice is preserved');
process.env.PI_TELEMETRY = '0';
const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'pisharp-installed-abi-'));
try {
  const filename = path.join(directory, 'extension.ts');
  await fs.writeFile(filename, `
    import Core from '@earendil-works/pi-agent-core';
    import * as core from '@earendil-works/pi-agent-core';
    import { Agent, DEFAULT_AGENT_IDENTITY } from '@mariozechner/pi-agent-core';
    import { VERSION } from '@earendil-works/pi-coding-agent';
    import { Text } from '@earendil-works/pi-tui';
    import { Text as LegacyText } from '@mariozechner/pi-tui';
    import { Type } from 'typebox';
    import { Type as LegacyType } from '@sinclair/typebox';
    import { Type as AiType, createAssistantMessageEventStream } from '@earendil-works/pi-ai';
    import { Type as CompatType } from '@earendil-works/pi-ai/compat';
    import { clampThinkingLevel } from '@earendil-works/pi-ai/models';
    import { clampThinkingLevel as legacyClamp } from '@mariozechner/pi-ai/models';
    import { Value } from 'typebox/value';
    import { Value as LegacyValue } from '@sinclair/typebox/value';
    import { Compile } from 'typebox/compile';
    import { Compile as LegacyCompile } from '@sinclair/typebox/compile';
    export default async () => {
      const identities = [];
      const model = { id: 'm', name: 'm', api: 'faux', provider: 'faux', baseUrl: '', reasoning: false, input: ['text'],
        cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 1000, maxTokens: 100 };
      const streamFn = (_model, _context, options) => {
        identities.push(options.identity);
        const stream = createAssistantMessageEventStream();
        queueMicrotask(() => stream.push({ type: 'done', reason: 'stop', message: { role: 'assistant', content: [{ type: 'text', text: 'ok' }],
          api: 'faux', provider: 'faux', model: 'm', usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }, stopReason: 'stop', timestamp: 0 } }));
        return stream;
      };
      await new Agent({ initialState: { model }, streamFn }).prompt('one');
      await new Core.Agent({ initialState: { model }, streamFn, identity: { userAgent: 'caller/1', attribution: { name: 'caller' } } }).prompt('two');
      const loop = core.agentLoop([{ role: 'user', content: 'three', timestamp: 0 }], { messages: [], tools: [] },
        { model, convertToLlm: messages => messages }, undefined, streamFn);
      await loop.result();
      return { codingAgentVersion: VERSION, sameTui: Text === LegacyText, sameCompile: Compile === LegacyCompile,
        sameAgent: Core.Agent === Agent && Agent === core.Agent,
        sameDefault: Core.DEFAULT_AGENT_IDENTITY === DEFAULT_AGENT_IDENTITY && DEFAULT_AGENT_IDENTITY === core.DEFAULT_AGENT_IDENTITY,
        sameType: Type === LegacyType && Type === AiType && Type === CompatType, sameValue: Value === LegacyValue,
        sameModels: clampThinkingLevel === legacyClamp, defaultIdentity: DEFAULT_AGENT_IDENTITY, identities };
    };`);
  const extension = await pi.importExtension(filename);
  const result = await extension();
  assert.equal(result.codingAgentVersion, '1.5.0');
  assert.equal(result.sameTui, true);
  assert.equal(result.sameCompile, true);
  assert.equal(result.sameAgent, true);
  assert.equal(result.sameDefault, true);
  assert.equal(result.sameType, true);
  assert.equal(result.sameValue, true);
  assert.equal(result.sameModels, true);
  assert.deepEqual(result.defaultIdentity, { attribution: null });
  assert.deepEqual(result.identities, [{ attribution: null }, { attribution: { name: 'caller' }, userAgent: 'caller/1' }, { attribution: null }]);
  // Native ESM and jiti must reach the same singleton. Reload uses a fresh extension module, not fresh dependency identities.
  const native = await import(pathToFileURL(pi.alias['@earendil-works/pi-agent-core']).href);
  assert.equal(native.DEFAULT_AGENT_IDENTITY.attribution, null);
  await fs.writeFile(filename, 'export default () => "reloaded";');
  assert.equal((await pi.importExtension(filename))(), 'reloaded');
  console.log(JSON.stringify({ version: pi.version, ...result, reloaded: true }));
} finally {
  await fs.rm(directory, { recursive: true, force: true });
}
