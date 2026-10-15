// The scenarios capture.mjs runs only at Pi v1.2.0 (06a466935536f8c9bcacb2d5efc0fc021be1bdff), on top of scenarios.mjs: what pi-durable
// gained or changed in v1.2.0 (packages/durable/CHANGELOG.md), each as a session upstream writes on SQLite and JSONL with its records,
// live documents and agent events, and for resumable ones what a later Harness makes of it. Each names the work-list areas
// (compatibility/durable-sync-1.2.0-worklist.json) whose port it is evidence for; Fixtures/v1.2.0/index.json lists them.
// They follow upstream's own tests (packages/durable/test/<file>, named per scenario), made deterministic: one task can act at a time,
// gates wait for observable states, and nothing reads the wall clock that the records keep.
// PiSharp has no port of these yet; the area that adopts a scenario adds it to tests/PiSharp.Durable.Tests/Compat/CompatScenarios.cs.
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { crc32, deflateSync } from 'node:zlib';
import { aborted, answer, calls, deferred, eventually, open, result, T, upstream } from './scenarios.mjs';

/** An error as observed: its name, message, and cause's message, if any. */
const described = (error) => ({
  name: error?.name ?? null,
  message: error?.message ?? String(error),
  ...(error?.cause === undefined ? {} : { cause: error.cause?.message ?? String(error.cause) }),
});
/** Settles `promise` into { value } or { error }. */
const settle = (promise) => promise.then((value) => ({ value: value ?? null }), (error) => ({ error: described(error) }));
/** A nested call result as programs get it, without its task ID and duration, which the records keep. */
const nestedResult = ({ taskId: _taskId, durationMs: _durationMs, ...rest }) => rest;
/** A faux reply making one tool call. */
const call = (name, args = {}, id = 'c1') => calls([name, args, id]);

function textTool(name, description, execute, extra = {}) {
  const { ai, durable } = upstream();
  return durable.defineTool({
    name,
    description,
    parameters: ai.Type.Object({ text: ai.Type.Optional(ai.Type.String()), id: ai.Type.Optional(ai.Type.Number()) }),
    execute,
    ...extra,
  });
}
const install = (setup, name, tools, extra = {}) => setup.registry.install(upstream().durable.defineExtension({ name, tools, ...extra }));

// ─── compaction-failure: the faux model answers agent and summarization requests from separate queues ───
function compactingChat(createChat, contextWindow) {
  const { durable } = upstream();
  const setup = createChat({ models: [{ id: 'faux-1', contextWindow, maxTokens: 900 }] });
  setup.settings.stream = { cacheRetention: 'none' };
  setup.settings.retry = { enabled: true, maxRetries: 2, baseDelayMs: 1 };
  setup.settings.compaction = { enabled: false, reserveTokens: 1000, keepRecentTokens: 150, backgroundTokens: 0 };
  setup.registry.install(durable.defineExtension({ name: 'section:preamble', sections: [durable.section('preamble', () => 'You are helpful.', { tag: false })] }));
  const script = { agent: [], summaries: [] };
  setup.faux.setResponses(Array.from({ length: 100 }, () => async (transcript) => {
    const first = transcript.messages[0];
    const summary = first?.role === 'system' && typeof first.content === 'string' && first.content.startsWith('You are a context summarization assistant');
    const step = (summary ? script.summaries : script.agent).shift();
    if (step === undefined) throw new Error(`No scripted ${summary ? 'summary' : 'agent'} response`);
    return typeof step === 'function' ? step() : step;
  }));
  return { setup, script };
}
const tokens = (label, count) => `${label} ${'x'.repeat(Math.max(0, count * 4 - label.length - 1))}`;

// ─── wait-cycle and abandon-on-restart tasks ───
const completed = (value) => ({ status: 'terminal', outcome: { status: 'completed', result: value } });

export const scenarios = [
  {
    name: 'nested-tools',
    since: '1.2.0',
    areas: ['a'],
    events: true,
    description: 'A model-issued tool makes nested calls (harness-nested-tools.test.ts, harness-callers.test.ts): two parallel calls of a ' +
      'tools-only echo (one under an explicit key), a schema tool, a missing tool and invalid arguments; each nested call is its own ' +
      'pi.tool task owned by the caller with a pi.tool.nested document, and the caller returns what it received to the model.',
    async run(createChat, openStorage, context) {
      const { ai, durable } = upstream();
      const setup = createChat();
      setup.settings.toolExecution = 'sequential';
      const echo = textTool('echo', 'Echoes its text', async (args) => result([{ type: 'text', text: `echo ${args.text ?? ''}` }]), { callers: ['tools'] });
      const counter = durable.defineTool({
        name: 'counter',
        description: 'Counts things',
        parameters: ai.Type.Object({}),
        structuredOutputSchema: ai.Type.Object({ count: ai.Type.Number() }),
        execute: async () => result([{ type: 'text', text: '3 things' }], { structuredOutput: { count: 3 } }),
      });
      const received = [];
      const batch = durable.defineTool({
        name: 'batch',
        description: 'Calls other tools',
        parameters: ai.Type.Object({}),
        execute: async (_args, api, callContext) => {
          received.push(...(await Promise.all([
            api.executeTool('echo', { text: 'a' }, callContext),
            api.executeTool('echo', { text: 'b' }, callContext, { key: 'named' }),
          ])));
          received.push(await api.executeTool('counter', {}, callContext));
          received.push(await api.executeTool('missing', {}, callContext));
          received.push(await api.executeTool('echo', { text: 5 }, callContext));
          return result([{ type: 'text', text: JSON.stringify(received.map(nestedResult)) }]);
        },
      });
      install(setup, 'tools', [echo, counter, batch]);
      setup.faux.setResponses([call('batch'), answer('done')]);
      const { harness, root } = await open(setup, await openStorage(), context);
      const agent = await root.agent(context);
      await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
      await harness.waitForIdle(context);
      await harness.close(context);
      return {
        offered: agent.tools.map((tool) => tool.name),
        callable: agent.callable.map((tool) => tool.name),
        received,
      };
    },
  },
  {
    name: 'structured-output',
    since: '1.2.0',
    areas: ['a'],
    events: true,
    description: 'Structured tool output (harness-structured-output.test.ts): model-issued calls of a schema tool (its value stays out of ' +
      'the transcript) and of one whose value breaks its schema (the call stands, the host gets a report); then nested calls of an ' +
      'image tool, an error result without a schema, the broken schema tool, a schema tool\'s error result keeping its value, and a valid one.',
    async run(createChat, openStorage, context) {
      const { ai, durable } = upstream();
      const setup = createChat();
      setup.settings.toolExecution = 'sequential';
      const Count = ai.Type.Object({ count: ai.Type.Number() });
      const returning = (name, value, extra = {}) => durable.defineTool({ name, description: name, parameters: ai.Type.Object({}), execute: async () => value, ...extra });
      const received = [];
      install(setup, 'tools', [
        returning('counter', result([{ type: 'text', text: '3 things' }], { structuredOutput: { count: 3 } }), { structuredOutputSchema: Count }),
        returning('wrong', { structuredOutput: { count: 'three' } }, { structuredOutputSchema: Count }),
        returning('picture', result([{ type: 'image', data: 'AAAA', mimeType: 'image/png' }])),
        returning('bad', result([{ type: 'text', text: 'bad' }], { isError: true })),
        returning('failing', result([{ type: 'text', text: 'failed' }], { structuredOutput: { count: 0 }, isError: true }), { structuredOutputSchema: Count }),
        durable.defineTool({
          name: 'probe',
          description: 'Calls the others',
          parameters: ai.Type.Object({}),
          execute: async (_args, api, callContext) => {
            for (const name of ['picture', 'bad', 'wrong', 'failing', 'counter']) received.push(await api.executeTool(name, {}, callContext));
            return result([{ type: 'text', text: JSON.stringify(received.map(nestedResult)) }]);
          },
        }),
      ]);
      setup.faux.setResponses([calls(['counter', {}, 'c1'], ['wrong', {}, 'c2']), call('probe', {}, 'c3'), answer('done')]);
      const reports = [];
      const { harness, root } = await open(setup, await openStorage(), context, { onReport: (error) => reports.push(described(error)) });
      await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
      await harness.waitForIdle(context);
      await harness.close(context);
      return { received, reports };
    },
  },
  {
    name: 'deferred-tool-search',
    since: '1.2.0',
    areas: ['a', 'k'],
    events: true,
    description: 'Deferred tools and tool_search (tool-search.test.ts): two deferred tools stay out of the request until tool_search ' +
      'loads the best match, which pi.agent records in modelTools.add and a pi.system entry appends; the model then calls it.',
    async run(createChat, openStorage, context) {
      const { ai, durable, at } = upstream();
      const { createToolSearchExtension } = await import(at('packages/durable/src/tool-search/index.ts'));
      const setup = createChat();
      const tool = (name, description, extra = {}) => durable.defineTool({
        name,
        description,
        parameters: ai.Type.Object({ number: ai.Type.Optional(ai.Type.Number({ description: 'Issue number' })) }),
        execute: async () => result([{ type: 'text', text: `${name} ran` }]),
        ...extra,
      });
      for (const registration of [tool('alpha', 'Alpha tool'), tool('github_get_issue', 'Get a GitHub issue by number', { deferred: true }),
        tool('github_list_repos', 'List the repositories of a user', { deferred: true }), tool('omega', 'Omega tool')])
        install(setup, `tool:${registration.name}`, [registration]);
      setup.registry.install(createToolSearchExtension());
      setup.faux.setResponses([call('tool_search', { query: 'github issue', limit: 1 }), call('github_get_issue', { number: 42 }, 'c2'), answer('done')]);
      const { harness, root } = await open(setup, await openStorage(), context);
      const names = async () => {
        const agent = await root.agent(context);
        return { offered: agent.tools.map((t) => t.name), deferred: agent.deferred.map((t) => t.name), callable: agent.callable.map((t) => t.name) };
      };
      const before = await names();
      await (await root.submit({ type: 'input', content: 'go' }, context)).wait(context);
      const after = await names();
      await harness.close(context);
      return { before, after };
    },
  },
  {
    name: 'codemode',
    since: '1.2.0',
    areas: ['d'],
    events: true,
    description: 'Durable code mode (codemode.test.ts): a codemode script calls echo, then the replay-safe hold, then echo, each a nested ' +
      'call journaled before delivery; closed while hold runs. Resumed: the script replays from its journal without running the ' +
      'finished echo again, hold reruns, and the script finishes; the model answers.',
    async run(createChat, openStorage, context) {
      const tools = await codemodeTools(createChat, true);
      tools.setup.faux.setResponses([call('codemode', { code: CODEMODE_SCRIPT }, 's1')]);
      const { harness, root } = await open(tools.setup, await openStorage(), context);
      await root.submit({ type: 'input', content: 'go' }, context);
      await tools.holds.reached.promise;
      await harness.close(context);
      return { echo: Object.fromEntries(tools.echo), holdRuns: tools.holds.runs, vmStarts: tools.vm.starts };
    },
    async resume(createChat, openStorage, context) {
      const tools = await codemodeTools(createChat, false);
      tools.setup.faux.setResponses([answer('done')]);
      const { harness } = await open(tools.setup, await openStorage(), context);
      harness.resume();
      await harness.waitForIdle(context);
      await harness.close(context);
      return { echo: Object.fromEntries(tools.echo), holdRuns: tools.holds.runs, vmStarts: tools.vm.starts };
    },
  },
  {
    name: 'read-image',
    since: '1.2.0',
    areas: ['e'],
    events: true,
    description: 'read returns images (tools-images.test.ts), without an image processor: a 1x1 PNG within the limits comes back as one ' +
      'image block with an info diagnostic; a BMP, which needs converting, is an unsupported_image error result.',
    async run(createChat, openStorage, context) {
      const { durable, at } = upstream();
      const { NodeExecutionEnv } = await import(at('packages/durable/src/env/node.ts'));
      const { createReadTool } = await import(at('packages/durable/src/tools/index.ts'));
      const directory = await mkdtemp(join(tmpdir(), 'pisharp-capture-images-'));
      try {
        await writeFile(join(directory, 'pixel.png'), pixelPng());
        await writeFile(join(directory, 'pixel.bmp'), pixelBmp());
        const env = new NodeExecutionEnv({ cwd: directory });
        const setup = createChat();
        setup.settings.toolExecution = 'sequential';
        setup.registry.install(durable.defineExtension({ name: 'coding', tools: [createReadTool()] }));
        setup.faux.setResponses([calls(['read', { path: 'pixel.png' }, 'c1'], ['read', { path: 'pixel.bmp' }, 'c2']), answer('a red pixel')]);
        const { harness, root } = await open(setup, await openStorage(), context, { env: () => env });
        await (await root.submit({ type: 'input', content: 'look' }, context)).wait(context);
        await harness.waitForIdle(context);
        await harness.close(context);
      } finally {
        await rm(directory, { recursive: true, force: true });
      }
    },
  },
  {
    name: 'input-entries',
    since: '1.2.0',
    areas: ['i'],
    events: true,
    description: 'Input entries (harness-input-entries.test.ts): an idle input placed as a chat.mention entry carrying its data, with ' +
      'the content as its user message; then an input whose generation is in flight, with a steer (p2.input, no data) and a follow-up ' +
      '(chat.mention) queued in pi.inbox with their entries; closed. Resumed: the generation reruns, then the queued inputs are placed as ' +
      'their entries.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      const reached = deferred();
      setup.faux.setResponses([answer('hello'), async (_transcript, options) => {
        reached.resolve();
        await aborted(options.signal);
        return answer('never');
      }]);
      const { harness, root } = await open(setup, await openStorage(), context);
      await (await root.submit({ type: 'input', content: 'ann: hi', entry: mention('hi'), requestId: 'r1' }, context)).wait(context);
      await root.submit({ type: 'input', content: 'a' }, context);
      await reached.promise;
      await root.submit({ type: 'input', content: 's', entry: { kind: 'p2.input' }, whenBusy: 'steer' }, context);
      await root.submit({ type: 'input', content: 'f', entry: mention('f'), whenBusy: 'followUp' }, context);
      const rejected = await settle(root.submit({ type: 'input', content: 'x', entry: { kind: 'pi.custom' } }, context));
      await harness.close(context);
      return { rejected };
    },
    async resume(createChat, openStorage, context) {
      const setup = createChat();
      setup.faux.setResponses([answer('first again'), answer('for s'), answer('for f'), answer('spare')]);
      const { harness } = await open(setup, await openStorage(), context);
      harness.resume();
      await harness.waitForIdle(context);
      await harness.close(context);
    },
  },
  {
    name: 'display-only-write',
    since: '1.2.0',
    areas: ['i'],
    events: true,
    description: 'Display-only writes while busy (harness-input-entries.test.ts, #10407): with a generation in flight and a follow-up ' +
      'queued, a write of only kind and data is appended and settled done at once, while writes with a model message or edits queue; ' +
      'once a reset is queued, a display-only write waits behind it. The answer, the reset, and the follow-up then run in order.',
    async run(createChat, openStorage, context) {
      const { durable } = upstream();
      const setup = createChat();
      const reached = deferred();
      const held = deferred();
      setup.faux.setResponses([async () => { reached.resolve(); await held.promise; return answer('first'); }, answer('second')]);
      const { harness, root } = await open(setup, await openStorage(), context);
      const input = await root.submit({ type: 'input', content: 'a' }, context);
      await reached.promise;
      const followUp = await root.submit({ type: 'input', content: 'f' }, context);
      const model = await root.submit({ type: 'write', entry: { kind: 'note', model: [{ role: 'user', content: 'm', timestamp: T }] } }, context);
      const user = (await root.entries({ order: 'ascending' }, 10, undefined, context)).items.find((entry) => entry.kind === 'pi.user');
      const edit = await root.submit({ type: 'write', entry: { kind: 'redact', edits: [{ target: user.id, action: 'omit' }] } }, context);
      const note = await root.submit({ type: 'write', entry: { kind: 'note', data: 'n' } }, context);
      const noteAtOnce = await note.status(context);
      await root.reset('handoff', context);
      const chat = await root.submit({ type: 'write', entry: { kind: 'chat.message', data: 'hi' } }, context);
      const chatBehindReset = await chat.status(context);
      const inbox = (await harness.snapshot(durable.InboxDoc, root.id, context)).items.map((item) => [item.id, item.mode]);
      held.resolve();
      for (const submission of [input, followUp, model, edit, chat]) await submission.wait(context);
      await harness.waitForIdle(context);
      await harness.close(context);
      return { noteAtOnce, chatBehindReset, inbox };
    },
  },
  {
    name: 'compaction-failure',
    since: '1.2.0',
    areas: ['j'],
    events: true,
    description: 'Compaction failure (harness-compaction.test.ts): three turns, then a turn whose background threshold compaction fails ' +
      '(pi.live.compactionFailure, compaction_end with error); then a turn over the blocking threshold whose compaction fails too, so ' +
      'the run fails with model_error "Compaction failed: ..." without sending its request.',
    async run(createChat, openStorage, context) {
      const { setup, script } = compactingChat(createChat, 2000);
      const failure = (message) => upstream().ai.fauxAssistantMessage('', { stopReason: 'error', errorMessage: message, timestamp: T });
      const { harness, root } = await open(setup, await openStorage(), context);
      const turn = async (user, reply) => {
        if (reply !== undefined) script.agent.push(answer(reply));
        return (await root.submit({ type: 'input', content: user }, context)).wait(context);
      };
      for (const n of [1, 2, 3]) await turn(tokens(`u${n}`, 100), tokens(`a${n}`, 100));
      setup.settings.compaction = { enabled: true, reserveTokens: 500, keepRecentTokens: 150, backgroundTokens: 1000 };
      // As capture-compaction.mjs orders a background compaction: the answer waits until the summarization request is sent, and the
      // summarization fails only once the turn has ended.
      const reached = deferred();
      const held = deferred();
      script.summaries.push(async () => {
        reached.resolve();
        await held.promise;
        return failure('bad request');
      });
      script.agent.push(async () => {
        await reached.promise;
        return answer(tokens('a4', 100));
      });
      const background = await turn(tokens('u4', 100));
      held.resolve();
      await eventually(async () => (await harness.snapshot(upstream().durable.LiveDoc, root.id, context))?.compactionFailure !== undefined);
      await harness.waitForIdle(context);
      setup.settings.compaction = { enabled: true, reserveTokens: 500, keepRecentTokens: 150, backgroundTokens: 0 };
      script.summaries.push(failure('still bad'));
      const blocking = await turn(tokens('u5', 900));
      await harness.waitForIdle(context);
      await harness.close(context);
      return { background, blocking, unusedAgentReplies: script.agent.length, unusedSummaries: script.summaries.length };
    },
  },
  {
    name: 'session-failed',
    since: '1.2.0',
    areas: ['b'],
    events: true,
    description: 'Storage failure closes the Session (harness-storage-failure.test.ts): one input answered, then the storage fails the ' +
      'next commit; the call that hit it rejects with the storage error, later calls with SessionFailed (cause: that error), the event ' +
      'stream ends with session_failed and Harness.closed settles { reason: "failed" }. The files hold what was committed before.',
    async run(createChat, openStorage, context) {
      const setup = createChat();
      setup.faux.setResponses([answer('one answered'), answer('never asked')]);
      const storage = await openStorage();
      const commit = storage.commit.bind(storage);
      let failNext;
      storage.commit = async (...args) => {
        if (failNext !== undefined) {
          const error = failNext;
          failNext = undefined;
          throw error;
        }
        return commit(...args);
      };
      const reports = [];
      const { harness, root } = await open(setup, storage, context, { onReport: (error) => reports.push(described(error)) });
      await (await root.submit({ type: 'input', content: 'one' }, context)).wait(context);
      failNext = new Error('disk gone');
      const failed = await settle(root.submit({ type: 'input', content: 'two' }, context));
      const later = await settle(root.submit({ type: 'input', content: 'three' }, context));
      const idle = await settle(harness.waitForIdle(context));
      const closed = await harness.closed;
      await harness.close(context);
      return { failed, later, idle, closed: { reason: closed.reason, ...(closed.error === undefined ? {} : { error: described(closed.error) }) }, reports };
    },
  },
  {
    name: 'nested-tools-restart',
    since: '1.2.0',
    areas: ['a', 'c'],
    events: true,
    description: 'Nested calls across a restart (harness-nested-tools-restart.test.ts): a replay-unsafe caller makes two nested calls, ' +
      'a replay-safe one that publishes output and details and runs, and one a beforeTool hook holds before intent; closed. Resumed: both ' +
      'are abandoned (abandonOnRestart, abortReason "restart") before they run again, ending interrupted and abandoned, and the caller ' +
      'settles with an error result; the model answers.',
    async run(createChat, openStorage, context) {
      const tools = restartTools(createChat, true);
      tools.setup.faux.setResponses([call('batch')]);
      const { harness, root } = await open(tools.setup, await openStorage(), context);
      await root.submit({ type: 'input', content: 'go' }, context);
      await Promise.all([tools.running.promise, tools.gated.promise]);
      await harness.close(context);
      return { runs: tools.runs };
    },
    async resume(createChat, openStorage, context) {
      const tools = restartTools(createChat, false);
      tools.setup.faux.setResponses([answer('done')]);
      const { harness } = await open(tools.setup, await openStorage(), context);
      harness.resume();
      await harness.waitForIdle(context);
      await harness.close(context);
      return { runs: tools.runs };
    },
  },
  {
    name: 'abandon-on-restart',
    since: '1.2.0',
    areas: ['c'],
    events: true,
    description: 'TaskOptions.abandonOnRestart (harness-nested-tools-restart.test.ts): a replay-unsafe tool creates a child task it owns ' +
      'with api.createTask() and awaits it in memory; closed while the child runs. Resumed: the child is marked with abortReason ' +
      '"restart" and its abort handler ends it aborted before any phase runs again; the tool call ends interrupted and the model answers.',
    async run(createChat, openStorage, context) {
      const worker = abandonTools(createChat);
      worker.setup.faux.setResponses([call('work')]);
      const { harness, root } = await open(worker.setup, await openStorage(), context);
      await root.submit({ type: 'input', content: 'go' }, context);
      await worker.started.promise;
      await harness.close(context);
      return { phases: worker.phases };
    },
    async resume(createChat, openStorage, context) {
      const worker = abandonTools(createChat);
      worker.setup.faux.setResponses([answer('done')]);
      const { harness } = await open(worker.setup, await openStorage(), context);
      harness.resume();
      await harness.waitForIdle(context);
      await harness.close(context);
      return { phases: worker.phases };
    },
  },
  {
    name: 'wait-cycle',
    since: '1.2.0',
    areas: ['c'],
    events: false,
    description: 'Wait-cycle rejection (harness-structured.test.ts, #10411): conversation-owned task a waits on b; then b commits a wait ' +
      'on a, which closes the cycle a -> b -> a: runtime.commit() rejects it naming the cycle, b ends faulted, and a resumes and ' +
      'completes.',
    async run(createChat, openStorage, context) {
      const { durable } = upstream();
      const setup = createChat();
      const ids = {};
      const bMayWait = deferred();
      const waitOn = (on, checkpoint) => ({ status: 'waiting', checkpoint, on, policy: 'allSettled' });
      const A = durable.defineTask({
        name: 'compat.cycle-a',
        version: 1,
        initial: () => ({ phase: 'wait' }),
        phases: {
          wait: async (_task, runtime, ctx) => { await runtime.commit(() => waitOn([ids.b], { phase: 'join' }), ctx); },
          join: async (_task, runtime, ctx) => { await runtime.commit(() => completed({ a: 'joined' }), ctx); },
        },
        abort: async () => {},
      });
      const B = durable.defineTask({
        name: 'compat.cycle-b',
        version: 1,
        initial: () => ({ phase: 'wait' }),
        phases: {
          wait: async (_task, runtime, ctx) => {
            await bMayWait.promise;
            await runtime.commit(() => waitOn([ids.a], { phase: 'join' }), ctx);
          },
          join: async (_task, runtime, ctx) => { await runtime.commit(() => completed({ b: 'joined' }), ctx); },
        },
        abort: async () => {},
      });
      setup.registry.install(durable.defineExtension({ name: 'tasks', tasks: [A, B] }));
      const { harness, root } = await open(setup, await openStorage(), context);
      await root.commit(async (tx) => {
        ids.b = await tx.createTask(B, { name: 'b' }, { ownership: { kind: 'conversation' } });
        ids.a = await tx.createTask(A, { name: 'a' }, { ownership: { kind: 'conversation' } });
      }, context);
      harness.resume();
      await eventually(async () => (await harness.getTask(ids.a, context))?.state.status === 'waiting');
      bMayWait.resolve();
      const b = await harness.waitForTask(ids.b, context);
      const a = await harness.waitForTask(ids.a, context);
      await harness.waitForIdle(context);
      await harness.close(context);
      return { a: a.state.outcome, b: b.state.outcome };
    },
  },
];

const mention = (text) => ({ kind: 'chat.mention', data: { author: 'ann', text } });

/** A 1x1 red PNG (RGB, deflated scanline, real CRCs). */
function pixelPng() {
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(1, 0);
  header.writeUInt32BE(1, 4);
  header.set([8, 2, 0, 0, 0], 8);
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header),
    chunk('IDAT', deflateSync(Buffer.from([0, 255, 0, 0]))), chunk('IEND', Buffer.alloc(0))]);
}
/** A 1x1 24-bit BMP: a red pixel, its row padded to four bytes. */
function pixelBmp() {
  const bytes = Buffer.alloc(58);
  bytes.write('BM', 0, 'latin1');
  bytes.writeUInt32LE(58, 2);
  bytes.writeUInt32LE(54, 10);
  bytes.writeUInt32LE(40, 14);
  bytes.writeInt32LE(1, 18);
  bytes.writeInt32LE(1, 22);
  bytes.writeUInt16LE(1, 26);
  bytes.writeUInt16LE(24, 28);
  bytes.writeUInt32LE(4, 34);
  bytes.set([0, 0, 255, 0], 54);
  return bytes;
}

const CODEMODE_SCRIPT = 'const a = await tools.echo({ text: "a" });\nconst held = await tools.hold({ id: 1 });\n' +
  'const b = await tools.echo({ text: "b" });\nreturn [a, held, b];';

/** codemode.test.ts's chat: the code mode extension, echo (unsafe, counted) and hold (replay-safe; blocks until aborted when `block`). */
async function codemodeTools(createChat, block) {
  const { at } = upstream();
  const { CodemodeSandbox } = await import(at('packages/codemode/src/index.ts'));
  const { createCodemodeExtension } = await import(at('packages/durable/src/codemode/index.ts'));
  const setup = createChat();
  const tools = { setup, echo: new Map(), holds: { block, reached: deferred(), runs: 0 }, vm: { starts: 0 } };
  const sandbox = new CodemodeSandbox({ memoryLimitBytes: 64 * 1024 * 1024 });
  const start = sandbox.start.bind(sandbox);
  sandbox.start = (code, options) => {
    tools.vm.starts++;
    return start(code, options);
  };
  setup.registry.install(createCodemodeExtension({ sandbox }));
  install(setup, 'tools', [
    textTool('echo', 'echo', async (args) => {
      tools.echo.set(args.text ?? '', (tools.echo.get(args.text ?? '') ?? 0) + 1);
      return result([{ type: 'text', text: `echo ${args.text ?? ''}` }]);
    }),
    textTool('hold', 'hold', async (args, _api, callContext) => {
      tools.holds.runs++;
      if (tools.holds.block) {
        tools.holds.reached.resolve();
        await aborted(callContext.abortSignal);
      }
      return result([{ type: 'text', text: `held ${args.id ?? ''}` }]);
    }, { replay: 'safe' }),
  ]);
  return tools;
}

/** harness-nested-tools-restart.test.ts's first case: `batch` calls `started` (replay-safe, runs) and `gated` (held by a hook). */
function restartTools(createChat, block) {
  const { durable, ai } = upstream();
  const setup = createChat();
  const runs = { started: 0, gated: 0 };
  const running = deferred();
  const gated = deferred();
  install(setup, 'tools', [
    textTool('started', 'started', async (_args, api, callContext) => {
      runs.started++;
      api.output('partial\n');
      await api.details({ step: 1 }, callContext);
      running.resolve();
      if (block) await aborted(callContext.abortSignal);
      return result([]);
    }, { replay: 'safe' }),
    textTool('gated', 'gated', async () => {
      runs.gated++;
      return result([]);
    }),
    durable.defineTool({
      name: 'batch',
      description: 'batch',
      parameters: ai.Type.Object({}),
      execute: async (_args, api, callContext) => {
        await Promise.all([api.executeTool('started', {}, callContext), api.executeTool('gated', {}, callContext)]);
        return result([]);
      },
    }),
  ], {
    hooks: [durable.hook(durable.ToolTask, {
      beforeTool: async (hookCall, _api, hookContext) => {
        if (hookCall.name !== 'gated' || !block) return undefined;
        gated.resolve();
        return aborted(hookContext.abortSignal);
      },
    })],
  });
  return { setup, runs, running, gated };
}

/** harness-nested-tools-restart.test.ts's "abandons the child tasks a replay-unsafe tool created and awaited in memory". */
function abandonTools(createChat) {
  const { durable } = upstream();
  const setup = createChat();
  const phases = [];
  const started = deferred();
  const Worker = durable.defineTask({
    name: 'compat.direct-worker',
    version: 1,
    initial: () => ({ phase: 'work' }),
    phases: {
      work: async (_task, runtime) => {
        phases.push('work');
        started.resolve();
        await aborted(runtime.signal);
      },
    },
    abort: async (task, runtime, abortContext) => {
      phases.push(`abort ${task.abortReason}`);
      await runtime.commit(() => ({ status: 'terminal', outcome: { status: 'aborted' } }), abortContext);
    },
  });
  setup.registry.install(durable.defineExtension({ name: 'direct-worker', tasks: [Worker] }));
  install(setup, 'tools', [textTool('work', 'work', async (_args, api, callContext) => {
    const id = await api.createTask(Worker, null, { ownership: { kind: 'task', taskId: api.taskId } }, callContext);
    await api.waitForTask(id, callContext);
    return result([]);
  })]);
  return { setup, phases, started };
}
