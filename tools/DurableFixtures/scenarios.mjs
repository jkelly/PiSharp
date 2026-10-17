// The scenarios capture.mjs runs on a pinned upstream pi-durable (pin.mjs), and the storage helpers capture.mjs and verify-reverse.mjs
// share. tests/PiSharp.Durable.Tests/Compat/CompatScenarios.cs runs the same scenarios on PiSharp; keep the two in step. Tool results take
// the pinned version's shape (`content` at v1.1.0, `output` from v1.2.0); scenarios-v1.2.0.mjs adds the scenarios only v1.2.0 has.
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { toolResult } from './pin.mjs';
/** The Harness clock (`now`), fixed so task times, user message timestamps and inbox times repeat. */
export const NOW = 1767225600000;
/** The timestamp of every scripted faux answer. */
export const T = 1000;

let ai;
let durable;
let sqlite;
let jsonl;
let pin;
/** Sinks for agent events: when set, open() streams the root conversation's events (watchEvents) into it until the Harness closes. */
let eventSink;

/** Loads pi-ai (the published build in node_modules) and pi-durable's sources from a Pi checkout at `pinned` (pin.mjs). */
export async function setPi(pi, pinned) {
  pin = pinned;
  const at = (path) => pathToFileURL(join(pi, path)).href;
  ai = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
  durable = { ...(await import(at('packages/durable/src/index.ts'))), configure: (await import(at('packages/durable/src/harness/agent.ts'))).configure };
  sqlite = await import(at('packages/durable/src/storage/sqlite/node.ts'));
  jsonl = await import(at('packages/durable/src/storage/jsonl/node.ts'));
}

/** The loaded modules, for scenarios-v1.2.0.mjs: pi-ai, pi-durable (with configure), and the pinned version. */
export const upstream = () => ({ ai, durable, pin, at: (path) => pathToFileURL(join(pin.pi, path)).href });

/** Streams the events of every root conversation open() opens into `sink` (an array), or stops when `sink` is undefined. */
export function recordEvents(sink) {
  eventSink = sink;
}

/** A tool result in the pinned version's shape. */
export const result = (content, rest) => toolResult(pin, content, rest);

/** Opens upstream storage: `session.sqlite` in the directory, or the directory as JSONL storage. */
export function openBackend(backend, directory, context) {
  if (backend === 'sqlite') return sqlite.openNodeSqliteStorage(join(directory, 'session.sqlite'));
  if (backend === 'jsonl') return jsonl.openNodeJsonlStorage(directory, context);
  throw new Error(`unknown backend ${backend}`);
}

async function all(scan) {
  const page = await scan();
  if (page.next !== undefined) throw new Error('more than one page');
  return [...page.items];
}

/**
 * Everything the storage reads back, in its own order and key order: every conversation; each conversation's visible entries, oldest
 * first; every task and submission; and every current document of the session scope and of each conversation and task scope. Closes it.
 */
export async function dumpStorage(storage, context) {
  try {
    const conversations = await all(() => storage.scanConversations({}, 1000, undefined, context));
    const entries = {};
    for (const conversation of conversations)
      entries[conversation.id] = await all(() => storage.scanEntries({ conversationId: conversation.id, order: 'ascending' }, 1000, undefined, context));
    const tasks = await all(() => storage.scanTasks({}, 1000, undefined, context));
    const submissions = await all(() => storage.scanSubmissions({}, 1000, undefined, context));
    const scopes = [{ kind: 'session' }, ...conversations.map((c) => ({ kind: 'conversation', conversationId: c.id })),
      ...tasks.map((t) => ({ kind: 'task', taskId: t.id }))];
    const documents = [];
    for (const scope of scopes) {
      for (const record of await all(() => storage.scanDocuments({ scope, at: 'current' }, 1000, undefined, context))) {
        const stored = await storage.document(record.id, 'current', context);
        documents.push({ record: stored.record, version: stored.version, value: stored.value });
      }
    }
    return { conversations, entries, tasks, submissions, documents };
  } finally {
    await storage.close(context);
  }
}

/** A faux model and a fresh registry; the Harness clock is fixed. */
export function createChat(options = {}) {
  const faux = ai.fauxProvider({ api: 'faux', ...options });
  const models = ai.createModels();
  models.setProvider(faux.provider);
  return { faux, models, registry: durable.createRegistry(), settings: {} };
}

export async function open(setup, storage, context, options = {}) {
  const harness = await durable.Harness.open(storage, { models: setup.models, registry: setup.registry, settings: setup.settings, now: () => NOW, ...options }, context);
  const root = await harness.root(context, { agent: { model: { provider: 'faux', modelId: 'faux-1' } } });
  if (eventSink !== undefined) {
    // Every event delivered before the Harness closes, in order; batch boundaries are left out, since they depend on timing.
    const sink = eventSink;
    const stream = await durable.watchEvents(harness, root.id, context);
    // As examples/19-json.ts prints them: the snapshot first, as a `snapshot` event.
    sink.push({ type: 'snapshot', ...stream.snapshot });
    stream.start(async (events) => { sink.push(...events); });
    const close = harness.close.bind(harness);
    harness.close = async (closeContext) => {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await stream.stop();
      return close(closeContext);
    };
  }
  return { harness, root };
}

/** Opens a Harness over the session and closes it without resuming it or touching a conversation. */
export async function openOnly(openStorage, context) {
  const setup = createChat();
  const harness = await durable.Harness.open(await openStorage(), { models: setup.models, registry: setup.registry, settings: setup.settings, now: () => NOW }, context);
  await harness.close(context);
}

export const deferred = () => {
  let resolve;
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
};
export const aborted = (signal) => new Promise((resolve) => {
  if (signal.aborted) resolve();
  else signal.addEventListener('abort', () => resolve(), { once: true });
});
export const answer = (text) => ai.fauxAssistantMessage(text, { timestamp: T });
export const calls = (...list) => ai.fauxAssistantMessage(list.map(([name, args, id]) => ai.fauxToolCall(name, args, { id })), { stopReason: 'toolUse', timestamp: T });
export const eventually = async (check) => {
  for (let attempt = 0; attempt < 4000; attempt++) {
    if (await check()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error('Condition was not reached');
};

// ─── tool-interrupted ───────────────────────────────────────────────────────

/** An unsafe tool that writes output and details, then blocks until its invocation is aborted. */
function workTool() {
  const started = deferred();
  const state = { runs: 0 };
  const registration = durable.defineTool({
    name: 'work',
    description: 'Works until interrupted',
    parameters: ai.Type.Object({}),
    execute: async (_args, api, callContext) => {
      state.runs++;
      api.output(`run ${state.runs}\n`);
      await api.details({ run: state.runs }, callContext);
      if (state.runs === 1) {
        started.resolve();
        await aborted(callContext.abortSignal);
      }
      return result([{ type: 'text', text: `finished run ${state.runs}` }]);
    },
  });
  return { registration, started, state };
}

const workExtension = (tool) => durable.defineExtension({ name: 'work', tools: [tool.registration] });

// ─── follow-up ──────────────────────────────────────────────────────────────

export const scenarios = [
  {
    name: 'chat',
    description: 'One input answered: tagged and untagged prompt sections, an offered tool, a request ID.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      setup.registry.install(durable.defineExtension({
        name: 'base',
        sections: [durable.section('preamble', () => 'You are terse.', { tag: false }), durable.section('cwd', () => '/repo')],
        tools: [durable.defineTool({ name: 'noop', description: 'Does nothing', parameters: ai.Type.Object({}), execute: async () => result([]) })],
      }));
      setup.faux.setResponses([answer('Hello there')]);
      const { harness, root } = await open(setup, await openStorage(), context);
      await (await root.submit({ type: 'input', content: 'hi', requestId: 'r1' }, context)).wait(context);
      await harness.close(context);
    },
  },
  {
    name: 'tool-interrupted',
    description: 'A call to an unsafe (not replay-safe) tool, closed while the tool runs after writing output and details: the file holds the ' +
      'execute checkpoint. Resumed: the call settles as interrupted with its durable partial output, then the model answers.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      const tool = workTool();
      setup.registry.install(workExtension(tool));
      setup.faux.setResponses([calls(['work', {}, 'call-1'])]);
      const { harness, root } = await open(setup, await openStorage(), context);
      await root.submit({ type: 'input', content: 'go' }, context);
      await tool.started.promise;
      await eventually(async () => (await harness.snapshot(durable.LiveDoc, root.id, context))?.tools?.[0]?.taskId !== undefined);
      await harness.close(context);
    },
    async resume(createChat, openStorage, context) {
      const setup = createChat();
      const tool = workTool();
      setup.registry.install(workExtension(tool));
      setup.faux.setResponses([answer('done')]);
      const { harness } = await open(setup, await openStorage(), context);
      harness.resume();
      await harness.waitForIdle(context);
      if (tool.state.runs !== 0) throw new Error('the unsafe tool ran again');
      await harness.close(context);
    },
  },
  {
    name: 'follow-up',
    description: 'An input whose generation is in flight and a follow-up queued behind it, closed while the model is still answering. ' +
      'Resumed: the generation reruns and answers, then the queued follow-up runs.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      const reached = deferred();
      setup.faux.setResponses([async (_transcript, options) => {
        reached.resolve();
        await aborted(options.signal);
        return answer('never');
      }]);
      const { harness, root } = await open(setup, await openStorage(), context);
      await root.submit({ type: 'input', content: 'one' }, context);
      await reached.promise;
      await root.submit({ type: 'input', content: 'two', whenBusy: 'followUp' }, context);
      await harness.close(context);
    },
    async resume(createChat, openStorage, context) {
      const setup = createChat();
      setup.faux.setResponses([answer('first'), answer('second')]);
      const { harness } = await open(setup, await openStorage(), context);
      harness.resume();
      await harness.waitForIdle(context);
      await harness.close(context);
    },
  },
  {
    name: 'compaction',
    description: 'Three turns, a manual compaction placed at once while idle, then one more turn over the compacted context.',
    async run(createChat, openStorage, context) {
      const setup = createChat({ models: [{ id: 'faux-1', contextWindow: 100_000, maxTokens: 900 }] });
      setup.settings.stream = { cacheRetention: 'none' };
      setup.settings.compaction = { enabled: false, reserveTokens: 1000, keepRecentTokens: 150, backgroundTokens: 0 };
      setup.registry.install(durable.defineExtension({ name: 'section:preamble', sections: [durable.section('preamble', () => 'You are helpful.', { tag: false })] }));
      const text = (label, tokens) => `${label} ${'x'.repeat(Math.max(0, tokens * 4 - label.length - 1))}`;
      setup.faux.setResponses([answer(text('a1', 100)), answer(text('a2', 100)), answer(text('a3', 100)), answer('SUMMARY'), answer('done')]);
      const { harness, root } = await open(setup, await openStorage(), context);
      for (const user of [text('u1', 100), text('u2', 100), text('u3', 100)])
        await (await root.submit({ type: 'input', content: user }, context)).wait(context);
      const id = await root.compact('focus on files', context);
      const record = await harness.waitForTask(id, context);
      await (await harness.submission(record.state.outcome.result.submissionId, context)).wait(context);
      await (await root.submit({ type: 'input', content: 'next' }, context)).wait(context);
      await harness.waitForIdle(context);
      await harness.close(context);
    },
  },
  {
    name: 'subagent',
    description: 'A foreground subagent tool (upstream example 22): the call creates a child conversation its task owns, runs one input ' +
      'there, and returns the child\'s answer to the parent, which answers.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      const Subagent = durable.defineExtension({
        name: 'subagent',
        tools: [durable.defineTool({
          name: 'subagent',
          description: 'Delegate a self-contained task to a subagent and get its answer back.',
          parameters: ai.Type.Object({ task: ai.Type.String({ description: 'What the subagent should do' }) }),
          replay: 'safe',
          execute: async (args, api, callContext) => {
            const child = await api.commit(async (tx) => {
              const existing = (await tx.scanConversations({ ownerTaskId: api.taskId }, 1)).items[0];
              if (existing !== undefined) return existing.id;
              const created = await tx.createConversation({ ownership: { kind: 'task', taskId: api.taskId } });
              await durable.configure(tx, created.id, { extensions: { remove: [Subagent] } });
              return created.id;
            }, callContext);
            await api.details({ conversationId: child }, callContext);
            const handle = await api.conversation(child, callContext);
            const settled = await (await handle.submit({ type: 'input', content: args.task, requestId: `subagent:${api.taskId}` }, callContext)).wait(callContext);
            if (settled.status !== 'done' || settled.type !== 'input') throw new Error(`Subagent failed: ${settled.status}`);
            const entry = await api.commit((tx) => tx.entry(durable.AssistantEntry, settled.answer), callContext);
            const text = entry.model[0].content.flatMap((c) => (c.type === 'text' ? [c.text] : [])).join('');
            return result([{ type: 'text', text }], { details: { conversationId: child } });
          },
        })],
      });
      setup.registry.install(Subagent);
      setup.faux.setResponses([
        calls(['subagent', { task: 'Name three prime numbers.' }, 'call-1']),
        answer('2, 3, and 5.'),
        answer('The subagent says: 2, 3, and 5.'),
      ]);
      const { harness, root } = await open(setup, await openStorage(), context);
      await (await root.submit({ type: 'input', content: 'Use the subagent tool to find three prime numbers.' }, context)).wait(context);
      await harness.waitForIdle(context);
      await harness.close(context);
    },
  },
  {
    name: 'fork',
    description: 'Two turns, an ownerless fork at the first answer, and a turn in the fork.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      setup.faux.setResponses([answer('first'), answer('second'), answer('third')]);
      const { harness, root } = await open(setup, await openStorage(), context);
      const one = await (await root.submit({ type: 'input', content: 'one' }, context)).wait(context);
      await (await root.submit({ type: 'input', content: 'two' }, context)).wait(context);
      const fork = await root.fork(one.answer, { ownership: { kind: 'ownerless' } }, context);
      await (await fork.submit({ type: 'input', content: 'three' }, context)).wait(context);
      await harness.waitForIdle(context);
      await harness.close(context);
    },
  },
  {
    name: 'task-graph',
    description: 'A conversation-owned task that creates a conversation it owns and a child task it owns; the child creates a grandchild ' +
      'task it owns and waits for it; each waits (allSettled) and completes once its owned work is done.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      const completed = (result) => ({ status: 'terminal', outcome: { status: 'completed', result } });
      const Leaf = durable.defineTask({
        name: 'compat.leaf',
        version: 1,
        initial: () => ({ phase: 'work' }),
        phases: { work: async (task, runtime, ctx) => { await runtime.commit(() => completed({ leaf: task.input.label }), ctx); } },
        abort: async () => {},
      });
      const Middle = durable.defineTask({
        name: 'compat.middle',
        version: 1,
        initial: () => ({ phase: 'spawn' }),
        phases: {
          spawn: async (task, runtime, ctx) => {
            await runtime.commit(async (tx) => {
              const leaf = await tx.createTask(Leaf, { label: 'grandchild' }, { ownership: { kind: 'task', taskId: task.id } });
              return { status: 'waiting', checkpoint: { phase: 'join', leaf }, on: [leaf], policy: 'allSettled' };
            }, ctx);
          },
          join: async (task, runtime, ctx) => { await runtime.commit(() => completed({ middle: true, leaf: task.state.checkpoint.leaf }), ctx); },
        },
        abort: async () => {},
      });
      const Root = durable.defineTask({
        name: 'compat.root',
        version: 1,
        initial: () => ({ phase: 'spawn' }),
        phases: {
          spawn: async (task, runtime, ctx) => {
            const ownership = { kind: 'task', taskId: task.id };
            await runtime.commit(async (tx) => {
              const owned = await tx.createConversation({ ownership });
              const middle = await tx.createTask(Middle, { depth: 1 }, { ownership });
              return { status: 'waiting', checkpoint: { phase: 'join', owned: owned.id, middle }, on: [middle], policy: 'allSettled' };
            }, ctx);
          },
          join: async (task, runtime, ctx) => { await runtime.commit(() => completed({ root: true, middle: task.state.checkpoint.middle }), ctx); },
        },
        abort: async () => {},
      });
      setup.registry.install(durable.defineExtension({ name: 'tasks', tasks: [Root, Middle, Leaf] }));
      const { harness, root } = await open(setup, await openStorage(), context);
      const id = await root.commit((tx) => tx.createTask(Root, { name: 'graph' }, { ownership: { kind: 'conversation' } }), context);
      harness.resume();
      await harness.waitForTask(id, context);
      await harness.waitForIdle(context);
      await harness.close(context);
    },
  },
];
