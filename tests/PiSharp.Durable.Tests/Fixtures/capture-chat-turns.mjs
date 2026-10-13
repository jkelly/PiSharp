// Prints Fixtures/chat-turns.upstream.json: the committed records the pinned pi-durable (Pi abe508e1b89912adde45528136c3221eb69acdd7) writes
// for HarnessChatDifferentialCases.cs's scenarios with pi-ai's faux provider on MemoryStorage, normalized (times, provider session ID).
// Usage, from a Pi checkout at that commit with node_modules installed (pi-ai is the published 1.1.0 build):
//   node --no-warnings --conditions=source --experimental-strip-types capture-chat-turns.mjs <pi checkout> > chat-turns.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

const pi = process.argv[2];
if (!pi) throw new Error('usage: capture-chat-turns.mjs <pi checkout>');
const at = (path) => pathToFileURL(join(pi, path)).href;
const { createModels, fauxAssistantMessage, fauxProvider, fauxToolCall } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
const { AgentDoc, createRegistry, defineExtension, defineTool, Harness, InboxDoc, LiveDoc, ProviderDoc, UsageDoc } =
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

async function open(setup, storage, model = true) {
  const harness = await Harness.open(storage, { models: setup.models, registry: setup.registry, settings: setup.settings ?? {} }, context);
  const root = await harness.root(context, model ? { agent: { model: { provider: 'faux', modelId: 'faux-1' } } } : undefined);
  return { harness, root };
}

function chat() {
  const faux = fauxProvider({ api: 'faux' });
  const models = createModels();
  models.setProvider(faux.provider);
  return { faux, models, registry: createRegistry() };
}

const out = {};

{
  // A plain answer with tagged and untagged sections and an offered tool.
  const setup = chat();
  setup.registry.install(defineExtension({
    name: 'base',
    sections: [{ key: 'preamble', render: () => 'You are terse.', tag: false }, { key: 'cwd', render: () => '/repo' }],
    tools: [defineTool({ name: 'noop', description: 'Does nothing', parameters: { type: 'object', properties: {} }, execute: async () => ({ content: [] }) })],
  }));
  setup.faux.setResponses([fauxAssistantMessage('Hello there', { timestamp: T })]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  await (await root.submit({ type: 'input', content: 'hi', requestId: 'r1' }, context)).wait(context);
  out.answer = await dump(harness, storage, [root.id]);
  await harness.close(context);
}

{
  // A retryable error, then the answer.
  const setup = { ...chat(), settings: { retry: { baseDelayMs: 1 } } };
  setup.faux.setResponses([
    fauxAssistantMessage([], { stopReason: 'error', errorMessage: '503 Service Unavailable', timestamp: T }),
    fauxAssistantMessage('recovered', { timestamp: T }),
  ]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  await (await root.submit({ type: 'input', content: 'hi' }, context)).wait(context);
  out.retry = await dump(harness, storage, [root.id]);
  await harness.close(context);
}

{
  // A call to a tool the request did not offer: a tool_unavailable result, then the next generation answers.
  const setup = chat();
  setup.faux.setResponses([
    fauxAssistantMessage([fauxToolCall('missing', { x: 1 }, { id: 'call-1' })], { stopReason: 'toolUse', timestamp: T }),
    fauxAssistantMessage('done', { timestamp: T }),
  ]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
  out.unavailable = await dump(harness, storage, [root.id]);
  await harness.close(context);
}

{
  // A follow-up queued while busy runs after the answer; a queued write is placed at the same boundary.
  const setup = chat();
  let release;
  const released = new Promise((resolve) => { release = resolve; });
  setup.faux.setResponses([
    async () => { await released; return fauxAssistantMessage('first', { timestamp: T }); },
    fauxAssistantMessage('second', { timestamp: T }),
  ]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  const one = await root.submit({ type: 'input', content: 'one' }, context);
  const two = await root.submit({ type: 'input', content: 'two', whenBusy: 'followUp' }, context);
  const note = await root.submit({ type: 'write', entry: { kind: 'note', data: { text: 'x' } } }, context);
  // While busy: the inbox and the submissions (the run's own progress is timing-dependent).
  const queued = normalize({
    inbox: await harness.snapshot(InboxDoc, root.id, context),
    submissions: [...(await storage.scanSubmissions({}, 1000, undefined, context)).items],
  });
  release();
  await one.wait(context);
  await two.wait(context);
  await note.wait(context);
  out.followUp = { queued, done: await dump(harness, storage, [root.id]) };
  await harness.close(context);
}

{
  // No model: the run fails with no_model.
  const setup = chat();
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage, false);
  await (await root.submit({ type: 'input', content: 'hi' }, context)).wait(context);
  out.noModel = await dump(harness, storage, [root.id]);
  await harness.close(context);
}

console.log(JSON.stringify(out));
