// Prints Fixtures/views.upstream.json: what the pinned pi-durable (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs) delivers to a conversation
// watch (conversation.watch: the acquisition view, then each frame's operations) and to an agent event stream (watchEvents: the snapshot,
// then each batch) attached before the run, for HarnessViewsDifferentialCases.cs's scenarios with pi-ai's faux provider on MemoryStorage:
// a tool turn (one tool call that publishes running details, then the answer) and an aborted sequential round (the first tool blocks after
// publishing details, the generation is aborted, and the unstarted calls are answered). Times and the provider session ID are normalized as
// capture-chat-turns.mjs normalizes them.
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings --conditions=source --experimental-strip-types capture-views.mjs <pi checkout> --commit <sha> > views.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

import { pinnedArguments, toolResult } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-views.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
/** A tool result in the pinned version's shape: `content` at v1.1.0, `output` from v1.2.0. */
const result = (content, rest) => toolResult(pin, content, rest);
const at = (path) => pathToFileURL(join(pi, path)).href;
const { createModels, fauxAssistantMessage, fauxProvider, fauxToolCall, Type } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
const { createRegistry, defineExtension, defineTool, Harness, LiveDoc, watchEvents } = await import(at('packages/durable/src/index.ts'));
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

async function open(registry, faux, settings = {}) {
  const models = createModels();
  models.setProvider(faux.provider);
  const harness = await Harness.open(new MemoryStorage(), { models, registry, settings }, context);
  const root = await harness.root(context, { agent: { model: { provider: 'faux', modelId: 'faux-1' } } });
  const watch = await root.watch(context);
  const frames = [];
  watch.start(async (_value, ops) => { frames.push(ops); });
  const stream = await watchEvents(harness, root.id, context);
  const batches = [];
  stream.start(async (events) => { batches.push(events); });
  const initial = watch.value;
  const snapshot = stream.snapshot;
  const finish = async () => {
    await harness.waitForIdle(context);
    await new Promise((resolve) => setTimeout(resolve, 0));
    const last = watch.value;
    await watch.stop();
    await stream.stop();
    await harness.close(context);
    return normalize({ initial, frames, final: last, snapshot, batches });
  };
  return { harness, root, finish };
}

const calls = (...list) =>
  fauxAssistantMessage(list.map(([name, args, id]) => fauxToolCall(name, args, { id })), { stopReason: 'toolUse', timestamp: T });

const out = {};

{
  // A tool turn: one call whose tool publishes running details, then the answer.
  const faux = fauxProvider({ api: 'faux' });
  const registry = createRegistry();
  registry.install(defineExtension({
    name: 'tools',
    tools: [defineTool({
      name: 'echo',
      description: 'Echoes its text',
      parameters: Type.Object({ text: Type.String() }),
      execute: async (args, api, callContext) => {
        await api.details({ step: 1 }, callContext);
        return result([{ type: 'text', text: `echo ${args.text}` }]);
      },
    })],
  }));
  faux.setResponses([calls(['echo', { text: 'hi' }, 'c1']), fauxAssistantMessage('done', { timestamp: T })]);
  const { root, finish } = await open(registry, faux);
  await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
  out.toolTurn = await finish();
}

{
  // An aborted sequential round: the first tool blocks after publishing details; aborting the generation answers the unstarted calls.
  const faux = fauxProvider({ api: 'faux' });
  const registry = createRegistry();
  let reached;
  const published = new Promise((resolve) => { reached = resolve; });
  const blocking = (name) => defineTool({
    name,
    description: `The ${name} tool`,
    parameters: Type.Object({}),
    executionMode: 'sequential',
    execute: async (_args, api, callContext) => {
      await api.details({ phase: 'running' }, callContext);
      reached();
      await new Promise((_, reject) => callContext.abortSignal.addEventListener('abort', () => reject(callContext.abortSignal.reason)));
      return {};
    },
  });
  registry.install(defineExtension({ name: 'tools', tools: [blocking('one'), blocking('two')] }));
  faux.setResponses([calls(['one', {}, 'c1'], ['two', {}, 'c2'], ['two', {}, 'c3'])]);
  const { harness, root, finish } = await open(registry, faux);
  const submission = await root.submit({ type: 'input', content: 'go' }, context);
  await published;
  await harness.abortTask((await harness.snapshot(LiveDoc, root.id, context)).run.taskId, context);
  await submission.wait(context);
  out.abortedRound = await finish();
}

console.log(JSON.stringify(out));
