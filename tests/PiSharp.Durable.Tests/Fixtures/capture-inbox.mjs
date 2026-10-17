// Prints Fixtures/inbox.upstream.json: the committed records the pinned pi-durable (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs) writes for
// HarnessInboxDifferentialCases.cs's inbox scenarios with pi-ai's faux provider on MemoryStorage, normalized as capture-chat-turns.mjs
// normalizes them (times, provider session ID; task startedAt/endedAt dropped). Each scenario records the inbox and submissions while the
// conversation is busy, then every record once it is idle, and the Session usage total.
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings --conditions=source --experimental-strip-types capture-inbox.mjs <pi checkout> --commit <sha> > inbox.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

import { pinnedArguments, toolResult } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-inbox.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
/** A tool result in the pinned version's shape: `content` at v1.1.0, `output` from v1.2.0. */
const result = (content, rest) => toolResult(pin, content, rest);
const at = (path) => pathToFileURL(join(pi, path)).href;
const { createModels, fauxAssistantMessage, fauxProvider, fauxToolCall, Type } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
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
  return normalize({ conversations, tasks, submissions, usage: await harness.usage(context) });
}

async function queued(harness, storage, id) {
  return normalize({
    inbox: await harness.snapshot(InboxDoc, id, context),
    submissions: [...(await storage.scanSubmissions({}, 1000, undefined, context)).items],
  });
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

function gate() {
  let release;
  const promise = new Promise((resolve) => { release = resolve; });
  return { promise, release };
}

/** A faux answer held until `release`; `reached` resolves when the request is sent. */
function gated(text) {
  const reached = gate();
  const held = gate();
  const step = async () => {
    reached.release();
    await held.promise;
    return fauxAssistantMessage(text, { timestamp: T });
  };
  return { step, reached: reached.promise, release: held.release };
}

async function waitFor(check) {
  for (let attempt = 0; attempt < 1000; attempt++) {
    if (await check()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error('Condition was not reached');
}

const answer = (text) => fauxAssistantMessage(text, { timestamp: T });
const out = {};

{
  // A steer, a follow-up and a write queued while a tool runs: the steer joins the run after tools, the write and the follow-up wait for
  // the final boundary.
  const setup = chat();
  const held = gate();
  setup.registry.install(defineExtension({
    name: 'tools',
    tools: [defineTool({
      name: 'hold',
      description: 'Waits for the test',
      parameters: Type.Object({}),
      execute: async () => {
        await held.promise;
        return result([{ type: 'text', text: 'held' }]);
      },
    })],
  }));
  setup.faux.setResponses([
    fauxAssistantMessage([fauxToolCall('hold', {}, { id: 'c1' })], { stopReason: 'toolUse', timestamp: T }),
    answer('after tools'),
    answer('follow-up'),
  ]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  const input = await root.submit({ type: 'input', content: 'a' }, context);
  await waitFor(async () => (await harness.snapshot(LiveDoc, root.id, context))?.tools?.[0]?.status === 'running');
  await root.submit({ type: 'input', content: 's', whenBusy: 'steer' }, context);
  const followUp = await root.submit({ type: 'input', content: 'f' }, context);
  const note = await root.submit({ type: 'write', entry: { kind: 'note', data: { text: 'x' } } }, context);
  const busy = await queued(harness, storage, root.id);
  held.release();
  await input.wait(context);
  await followUp.wait(context);
  await note.wait(context);
  out.steer = { queued: busy, done: await dump(harness, storage, [root.id]) };
  await harness.close(context);
}

{
  // A reset with handoff text queued behind a follow-up: placed first at the final boundary, so the follow-up runs in the new context. Then
  // an idle reset without handoff, and an idle write whose head reaches before the active range.
  const setup = chat();
  const first = gated('first');
  setup.faux.setResponses([first.step, answer('second')]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  const input = await root.submit({ type: 'input', content: 'a' }, context);
  await first.reached;
  const followUp = await root.submit({ type: 'input', content: 'f' }, context);
  await root.reset('handoff', context);
  const busy = await queued(harness, storage, root.id);
  first.release();
  await input.wait(context);
  await followUp.wait(context);
  await harness.waitForIdle(context);
  await root.reset(undefined, context);
  const target = [...(await root.entries({ order: 'ascending' }, 1, undefined, context)).items][0];
  await (await root.submit({ type: 'write', entry: { kind: 'summary', head: target.id } }, context)).wait(context);
  out.reset = { queued: busy, done: await dump(harness, storage, [root.id]) };
  await harness.close(context);
}

{
  // Queue modes all: every steer and follow-up queued behind the answer starts one successor run, in ID order.
  const setup = chat({ steeringMode: 'all', followUpMode: 'all' });
  const first = gated('first');
  setup.faux.setResponses([first.step, answer('all of them')]);
  const storage = new MemoryStorage();
  const { harness, root } = await open(setup, storage);
  const input = await root.submit({ type: 'input', content: 'a' }, context);
  await first.reached;
  const later = [];
  for (const [content, whenBusy] of [['f1', 'followUp'], ['s1', 'steer'], ['f2', 'followUp'], ['s2', 'steer']])
    later.push(await root.submit({ type: 'input', content, whenBusy }, context));
  const busy = await queued(harness, storage, root.id);
  first.release();
  await input.wait(context);
  for (const submission of later) await submission.wait(context);
  out.modes = { queued: busy, done: await dump(harness, storage, [root.id]) };
  await harness.close(context);
}

console.log(JSON.stringify(out));
