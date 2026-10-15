// Prints Fixtures/task-graph.upstream.json: what the pinned pi-durable (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs) delivers to a task
// graph watch (harness.watchTaskGraph: the acquisition graph, each frame's operations, and the last value) and what a fresh
// harness.taskGraph() state reads at each pause, for HarnessTaskGraphDifferentialCases.cs's scenario on MemoryStorage. Only one task can
// run at each step, so the frame sequence does not depend on scheduling order:
//   a conversation-owned parent creates two conversations it owns, then a child task it waits for (allSettled); the child runs and
//   completes once released; the parent wakes, creates a late child, and completes once that child's phase started, so its outcome is
//   held as `completing`; the late child completes once released, and the parent becomes terminal.
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings --conditions=source --experimental-strip-types capture-task-graph.mjs <pi checkout> --commit <sha> > task-graph.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

import { pinnedArguments } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-task-graph.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
const at = (path) => pathToFileURL(join(pi, path)).href;
const { createModels } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));
const { createRegistry, defineExtension, defineTask, Harness } = await import(at('packages/durable/src/index.ts'));
const { MemoryStorage } = await import(at('packages/durable/src/storage/memory.ts'));

const context = {};
const deferred = () => {
  let resolve;
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
};
const completed = (result) => ({ status: 'terminal', outcome: { status: 'completed', result } });
const eventually = async (check) => {
  for (let attempt = 0; attempt < 2000; attempt++) {
    if (await check()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error('Condition was not reached');
};

const gates = { child: deferred(), late: deferred() };
const lateStarted = deferred();
const Child = defineTask({
  name: 'test.graph-child',
  version: 1,
  initial: () => ({ phase: 'work' }),
  phases: {
    work: async (task, runtime, ctx) => {
      if (task.input.late) lateStarted.resolve();
      await (task.input.late ? gates.late.promise : gates.child.promise);
      await runtime.commit(() => completed(null), ctx);
    },
  },
  abort: async () => {},
});
const Parent = defineTask({
  name: 'test.graph-parent',
  version: 1,
  initial: () => ({ phase: 'spawn' }),
  phases: {
    spawn: async (task, runtime, ctx) => {
      const ownership = { kind: 'task', taskId: task.id };
      await runtime.commit(async (tx) => {
        await tx.createConversation({ ownership });
        await tx.createConversation({ ownership });
        return undefined;
      }, ctx);
      await runtime.commit(async (tx) => {
        const child = await tx.createTask(Child, { late: false }, { ownership });
        return { status: 'waiting', checkpoint: { phase: 'join', child }, on: [child], policy: 'allSettled' };
      }, ctx);
    },
    join: async (task, runtime, ctx) => {
      await runtime.commit(async (tx) => {
        await tx.createTask(Child, { late: true }, { ownership: { kind: 'task', taskId: task.id } });
        return { status: 'running', checkpoint: { phase: 'finish' } };
      }, ctx);
    },
    finish: async (_task, runtime, ctx) => {
      // Completes once the late child's phase started, so its reservation commit comes first.
      await lateStarted.promise;
      await runtime.commit(() => completed(null), ctx);
    },
  },
  abort: async () => {},
});

const registry = createRegistry();
registry.install(defineExtension({ name: 'tasks', tasks: [Parent, Child] }));
const harness = await Harness.open(new MemoryStorage(), { models: createModels(), registry }, context);
const root = await harness.root(context);
const watch = await harness.watchTaskGraph(context);
const initial = watch.value;
const frames = [];
watch.start(async (_value, ops) => { frames.push(ops); });
const fresh = async () => {
  const state = await harness.taskGraph(context);
  const value = state.value;
  state.dispose();
  return value;
};
const snapshots = {};

const parent = await root.commit((tx) => tx.createTask(Parent, null, { ownership: { kind: 'conversation' } }), context);
snapshots.created = await fresh();
harness.resume();
await eventually(() => watch.value.tasks[String(parent)]?.state.status === 'waiting' &&
  Object.values(watch.value.tasks).some((node) => node.state.status === 'running'));
snapshots.waiting = await fresh();
gates.child.resolve();
await eventually(() => watch.value.tasks[String(parent)]?.state.status === 'completing' &&
  Object.values(watch.value.tasks).some((node) => node.state.status === 'running'));
snapshots.completing = await fresh();
gates.late.resolve();
await harness.waitForTask(parent, context);
await new Promise((resolve) => setTimeout(resolve, 0));
const last = watch.value;
await watch.stop();
snapshots.drained = await fresh();
await harness.close(context);
console.log(JSON.stringify({ parent, initial, frames, final: last, snapshots }));
