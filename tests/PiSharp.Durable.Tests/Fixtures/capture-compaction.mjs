// Prints Fixtures/compaction.upstream.json: the committed records the pinned pi-durable (Pi abe508e1b89912adde45528136c3221eb69acdd7) writes for
// HarnessCompactionDifferentialCases.cs's compaction scenarios with pi-ai's faux provider on MemoryStorage, normalized as capture-chat-turns.mjs
// normalizes them (times, provider session ID; task startedAt/endedAt dropped): a manual compaction placed at once, a blocking threshold
// compaction during a turn, a background threshold compaction placed once its turn ended, and an overflow compaction whose request is
// retried. Each scenario records every record once the conversation is idle, the Session usage total, and the summarization requests.
// Usage, from a Pi checkout at that commit with node_modules installed (pi-ai is the published 1.1.0 build):
//   node --no-warnings --conditions=source --experimental-strip-types capture-compaction.mjs <pi checkout> > compaction.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

const pi = process.argv[2];
if (!pi) throw new Error('usage: capture-compaction.mjs <pi checkout>');
const at = (path) => pathToFileURL(join(pi, path)).href;
const { createModels, fauxAssistantMessage, fauxProvider } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
const { AgentDoc, createRegistry, defineExtension, Harness, InboxDoc, LiveDoc, ProviderDoc, section, UsageDoc } =
  await import(at('packages/durable/src/index.ts'));
const { MemoryStorage } = await import(at('packages/durable/src/storage/memory.ts'));

const context = {};
const T = 1000;
const TIMES = ['timestamp', 'at', 'until', 'pollAt', 'startedAt', 'endedAt', 'durationMs'];
const OVERFLOW = 'prompt is too long: 250000 tokens > 200000 maximum';
const MANUAL = { enabled: false, reserveTokens: 1000, keepRecentTokens: 150, backgroundTokens: 0 };

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

async function dump(harness, storage, id, summaries) {
  const entries = [...(await storage.scanEntries({ conversationId: id, order: 'ascending' }, 1000, undefined, context)).items];
  const docs = {};
  for (const [name, doc] of [['pi.live', LiveDoc], ['pi.inbox', InboxDoc], ['pi.usage', UsageDoc], ['pi.agent', AgentDoc], ['pi.provider', ProviderDoc]])
    docs[name] = (await harness.snapshot(doc, id, context)) ?? null;
  const tasks = [...(await storage.scanTasks({}, 1000, undefined, context)).items].map(({ startedAt: _s, endedAt: _e, ...rest }) => rest);
  const submissions = [...(await storage.scanSubmissions({}, 1000, undefined, context)).items];
  // The summarization requests: their messages and the options the provider received, without the signal.
  const requests = summaries.map(({ messages, options }) => {
    const { signal: _signal, ...rest } = options ?? {};
    return { messages, options: rest };
  });
  return normalize({ conversations: { [id]: { entries, docs } }, tasks, submissions, usage: await harness.usage(context), requests });
}

const text = (label, tokens) => `${label} ${'x'.repeat(Math.max(0, tokens * 4 - label.length - 1))}`;
const answer = (content) => fauxAssistantMessage(content, { timestamp: T });
const failure = (errorMessage) => fauxAssistantMessage('', { stopReason: 'error', errorMessage, timestamp: T });

function gate() {
  let release;
  const promise = new Promise((resolve) => { release = resolve; });
  return { promise, release };
}

/** A chat whose faux model answers agent and summarization requests from separate queues, as harness-compaction.test.ts scripts it. */
async function chat(contextWindow) {
  const faux = fauxProvider({ api: 'faux', models: [{ id: 'faux-1', contextWindow, maxTokens: 900 }] });
  const models = createModels();
  models.setProvider(faux.provider);
  const script = { agent: [], summaries: [], summaryRequests: [] };
  faux.setResponses(Array.from({ length: 100 }, () => async (transcript, options) => {
    const first = transcript.messages[0];
    const summary = first?.role === 'system' && typeof first.content === 'string' &&
      first.content.startsWith('You are a context summarization assistant');
    if (summary) script.summaryRequests.push({ messages: [...transcript.messages], options });
    const step = (summary ? script.summaries : script.agent).shift();
    if (step === undefined) throw new Error(`No scripted ${summary ? 'summary' : 'agent'} response`);
    return typeof step === 'function' ? step() : step;
  }));
  const registry = createRegistry();
  registry.install(defineExtension({ name: 'section:preamble', sections: [section('preamble', () => 'You are helpful.', { tag: false })] }));
  const settings = {
    stream: { cacheRetention: 'none' },
    compaction: MANUAL,
    retry: { enabled: true, maxRetries: 2, baseDelayMs: 1 },
  };
  const storage = new MemoryStorage();
  const harness = await Harness.open(storage, { models, registry, settings }, context);
  const root = await harness.root(context, { agent: { model: { provider: 'faux', modelId: 'faux-1' } } });
  return { harness, root, storage, settings, script };
}

async function turn(c, user, reply) {
  c.script.agent.push(typeof reply === 'string' ? answer(reply) : reply);
  const submission = await c.root.submit({ type: 'input', content: user }, context);
  const settled = await submission.wait(context);
  if (settled.status !== 'done') throw new Error(`turn ${user.slice(0, 4)}: ${settled.status}`);
}

async function history(c) {
  await turn(c, text('u1', 100), text('a1', 100));
  await turn(c, text('u2', 100), text('a2', 100));
  await turn(c, text('u3', 100), text('a3', 100));
}

const out = {};

{
  // Manual: placed at once while idle, then one more turn over the compacted context.
  const c = await chat(100_000);
  await history(c);
  c.script.summaries.push(answer('SUMMARY'));
  const id = await c.root.compact('focus on files', context);
  const record = await c.harness.waitForTask(id, context);
  await (await c.harness.submission(record.state.outcome.result.submissionId, context)).wait(context);
  await turn(c, 'next', 'done');
  await c.harness.waitForIdle(context);
  out.manual = await dump(c.harness, c.storage, c.root.id, c.script.summaryRequests);
  await c.harness.close(context);
}

{
  // Blocking threshold: preparation compacts first, and the summary is appended before the request.
  const c = await chat(1000);
  await history(c);
  c.settings.compaction = { enabled: true, reserveTokens: 300, keepRecentTokens: 150, backgroundTokens: 0 };
  c.script.summaries.push(answer('BLOCKING'));
  await turn(c, text('u4', 200), 'a4');
  await c.harness.waitForIdle(context);
  out.blocking = await dump(c.harness, c.storage, c.root.id, c.script.summaryRequests);
  await c.harness.close(context);
}

{
  // Background threshold: the compaction summarizes while the turn ends, and its summary lands at once on the idle conversation.
  const c = await chat(2000);
  await history(c);
  c.settings.compaction = { enabled: true, reserveTokens: 500, keepRecentTokens: 150, backgroundTokens: 1000 };
  const held = gate();
  const reached = gate();
  c.script.summaries.push(async () => {
    reached.release();
    await held.promise;
    return answer('BACKGROUND');
  });
  // The answer waits until the summarization request is sent, so the compaction selected its range before the answer was appended.
  await turn(c, text('u4', 100), async () => {
    await reached.promise;
    return answer(text('a4', 100));
  });
  const [status] = (await c.harness.snapshot(LiveDoc, c.root.id, context)).compactions;
  held.release();
  const record = await c.harness.waitForTask(status.taskId, context);
  await (await c.harness.submission(record.state.outcome.result.submissionId, context)).wait(context);
  out.background = await dump(c.harness, c.storage, c.root.id, c.script.summaryRequests);
  await c.harness.close(context);
}

{
  // Overflow: the provider rejects the request as too long; generation compacts and retries the same attempt.
  const c = await chat(100_000);
  await history(c);
  c.settings.compaction = { ...MANUAL, enabled: true };
  c.script.summaries.push(answer('OVERFLOW SUMMARY'));
  c.script.agent.push(failure(OVERFLOW));
  await turn(c, text('u4', 100), 'fits');
  await c.harness.waitForIdle(context);
  out.overflow = await dump(c.harness, c.storage, c.root.id, c.script.summaryRequests);
  await c.harness.close(context);
}

console.log(JSON.stringify(out));
