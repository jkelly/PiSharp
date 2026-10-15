// Prints Fixtures/tool-rounds.upstream.json: the committed records the pinned pi-durable (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs) writes
// for HarnessToolsDifferentialCases.cs's tool-round scenarios with pi-ai's faux provider on MemoryStorage, normalized as
// capture-chat-turns.mjs normalizes them (times, provider session ID; task startedAt/endedAt dropped).
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings --conditions=source --experimental-strip-types capture-tool-rounds.mjs <pi checkout> --commit <sha> > tool-rounds.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

import { pinnedArguments, toolResult } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-tool-rounds.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
/** A tool result in the pinned version's shape: `content` at v1.1.0, `output` from v1.2.0. */
const result = (content, rest) => toolResult(pin, content, rest);
const at = (path) => pathToFileURL(join(pi, path)).href;
const { createModels, fauxAssistantMessage, fauxProvider, fauxToolCall, Type } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
const { AgentDoc, createRegistry, defineExtension, defineTool, Harness, hook, InboxDoc, LiveDoc, ProviderDoc, ToolTask, UsageDoc } =
  await import(at('packages/durable/src/index.ts'));
const { MemoryStorage } = await import(at('packages/durable/src/storage/memory.ts'));

const context = {};
const T = 1000;
const TIMES = ['timestamp', 'at', 'until', 'pollAt', 'startedAt', 'endedAt', 'durationMs'];

function normalize(value, key) {
  if (Array.isArray(value)) return value.map((item) => normalize(item));
  if (value !== null && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = normalize(v, k);
    return out;
  }
  if (typeof value === 'number' && TIMES.includes(key ?? '')) return '<time>';
  if (typeof value === 'string' && key === 'sessionId') return '<session>';
  return value;
}

async function dump(harness, storage, ids) {
  const conversations = {};
  for (const id of ids) {
    const entries = [...(await storage.scanEntries({ conversationId: id, order: 'ascending' }, 1000, undefined, context)).items];
    const docs = {};
    for (const [name, doc] of [['pi.live', LiveDoc], ['pi.inbox', InboxDoc], ['pi.usage', UsageDoc], ['pi.agent', AgentDoc], ['pi.provider', ProviderDoc]])
      docs[name] = (await harness.snapshot(doc, id, context)) ?? null;
    conversations[id] = { entries, docs };
  }
  const tasks = [...(await storage.scanTasks({}, 1000, undefined, context)).items].map(({ startedAt: _s, endedAt: _e, ...rest }) => rest);
  const submissions = [...(await storage.scanSubmissions({}, 1000, undefined, context)).items];
  return normalize({ conversations, tasks, submissions });
}

async function open(setup, storage) {
  const harness = await Harness.open(storage, { models: setup.models, registry: setup.registry, settings: setup.settings ?? {} }, context);
  const root = await harness.root(context, { agent: { model: { provider: 'faux', modelId: 'faux-1' } } });
  return { harness, root };
}

function chat(settings) {
  const faux = fauxProvider({ api: 'faux' });
  const models = createModels();
  models.setProvider(faux.provider);
  return { faux, models, registry: createRegistry(), settings };
}

const calls = (...list) =>
  fauxAssistantMessage(list.map(([name, args, id]) => fauxToolCall(name, args, { id })), { stopReason: 'toolUse', timestamp: T });
const USAGE = { input: 1, output: 2, cacheRead: 0, cacheWrite: 0, totalTokens: 3, cost: { input: 0.5, output: 0.25, cacheRead: 0, cacheWrite: 0, total: 0.75 } };
const echo = defineTool({
  name: 'echo',
  description: 'Echoes its text',
  parameters: Type.Object({ text: Type.String() }),
  execute: async (args) => result([{ type: 'text', text: `echo ${args.text}` }], { usage: USAGE }),
});

const out = {};

{
  // A sequential round: explicit content with usage, then running output bounded to its first lines, an api diagnostic, running details,
  // a result diagnostic and an addTools control; then the answer.
  const setup = chat({ toolExecution: 'sequential' });
  const log = defineTool({
    name: 'log',
    description: 'Logs lines',
    parameters: Type.Object({}),
    outputLimits: { maxLines: 2 },
    execute: async (_args, api) => {
      api.output('line 1\n');
      api.output(new TextEncoder().encode('line 2\nline 3\n'));
      api.diagnostic({ severity: 'info', code: 'note', message: 'from api' });
      await api.details({ step: 1 }, context);
      return { diagnostics: [{ severity: 'warn', message: 'from result' }], control: { addTools: ['echo'] } };
    },
  });
  setup.registry.install(defineExtension({ name: 'tools', tools: [echo, log] }));
  setup.faux.setResponses([calls(['echo', { text: 'hi' }, 'c1'], ['log', {}, 'c2']), fauxAssistantMessage('done', { timestamp: T })]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  await root.configure({ tools: [log] }, context);
  await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
  out.round = await dump(harness, storage, [root.id]);
  await harness.close(context);
}

{
  // Invalid arguments with pi-ai's validation message, a beforeTool block, a throwing tool that ends its task failed with the result
  // entry, and a call whose usage enters pi.usage.tools; then the answer.
  const setup = chat({ toolExecution: 'sequential' });
  const thrower = defineTool({
    name: 'thrower',
    description: 'Throws',
    parameters: Type.Object({}),
    execute: async (_args, api) => {
      api.output('partial\n');
      throw new Error('boom');
    },
  });
  setup.registry.install(defineExtension({
    name: 'tools',
    tools: [echo, thrower],
    hooks: [hook(ToolTask, { beforeTool: (call) => (call.id === 'blocked' ? { block: 'not today' } : undefined) })],
  }));
  setup.faux.setResponses([
    calls(['echo', { text: { not: 'a string' } }, 'invalid'], ['echo', { text: 'x' }, 'blocked'], ['thrower', {}, 'c3'], ['echo', { text: 'ok' }, 'c4']),
    fauxAssistantMessage('done', { timestamp: T }),
  ]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
  out.rejections = await dump(harness, storage, [root.id]);
  await harness.close(context);
}

{
  // A tool aborted after it published output and details: an aborted result from its durable slot, then the answer.
  const setup = chat();
  let reached;
  const published = new Promise((resolve) => { reached = resolve; });
  const slow = defineTool({
    name: 'slow',
    description: 'Waits',
    parameters: Type.Object({}),
    execute: async (_args, api, callContext) => {
      api.output('partial\n');
      await api.details({ phase: 'running' }, callContext);
      reached();
      await new Promise((_, reject) => callContext.abortSignal.addEventListener('abort', () => reject(callContext.abortSignal.reason)));
      return {};
    },
  });
  setup.registry.install(defineExtension({ name: 'tools', tools: [slow] }));
  setup.faux.setResponses([calls(['slow', {}, 'c1']), fauxAssistantMessage('done', { timestamp: T })]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  const submission = await root.submit({ type: 'input', content: 'go' }, context);
  await published;
  const taskId = (await harness.snapshot(LiveDoc, root.id, context)).tools[0].taskId;
  const marked = await harness.abortTask(taskId, context);
  await submission.wait(context);
  out.aborted = { marked, done: await dump(harness, storage, [root.id]) };
  await harness.close(context);
}

console.log(JSON.stringify(out));
