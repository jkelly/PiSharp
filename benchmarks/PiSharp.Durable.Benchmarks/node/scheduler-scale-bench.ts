/**
 * PiSharp-authored twin of benchmarks/PiSharp.Durable.Benchmarks/SchedulerScaleBench.cs, for the pinned upstream (Pi v1.2.0,
 * 06a466935536f8c9bcacb2d5efc0fc021be1bdff). Not an upstream file. Copy it into packages/durable/test of an upstream checkout and run, from
 * packages/durable:
 *
 *   node --conditions=source --experimental-strip-types test/scheduler-scale-bench.ts [fanout N,...] [chain N,...]
 *
 * - fanout: one model-issued tool creates N child tasks it owns with api.createTask(), each in its own commit and all at once, and waits for
 *   all of them; each child completes in its first phase. In-memory storage. Upstream's own fan-out benchmark,
 *   test/scheduler-fanout-bench.ts, makes nested tool calls instead.
 * - chain: one commit creates N tasks, each owning the next; every one completes in its first phase and holds while the task below it lives.
 *   The time runs from the creating commit to the top task's terminal record.
 */
import { BACKGROUND_CONTEXT as context } from "@earendil-works/chord/context";
import { createModels } from "@earendil-works/pi-ai/models";
import { fauxAssistantMessage, fauxProvider, fauxText, fauxToolCall } from "@earendil-works/pi-ai/providers/faux";
import { Type } from "typebox";
import { createRegistry, defineExtension, defineTask, defineTool, Harness, ToolResultEntry } from "../src/index.ts";
import { MemoryStorage } from "../src/storage/memory.ts";

const Leaf = defineTask<null, { phase: "run" }, null>({
	name: "bench.leaf",
	version: 1,
	initial: () => ({ phase: "run" }),
	phases: {
		run: async (_task, runtime, ctx) =>
			runtime.commit(() => ({ status: "terminal", outcome: { status: "completed", result: null } }), ctx),
	},
	abort: async (_task, runtime, ctx) =>
		runtime.commit(() => ({ status: "terminal", outcome: { status: "aborted" } }), ctx),
});

function counted(storage: MemoryStorage): { commits: number } {
	const counter = { commits: 0 };
	const commit = storage.commit.bind(storage);
	storage.commit = (writes, commitContext) => {
		counter.commits++;
		return commit(writes, commitContext);
	};
	return counter;
}

async function fanout(count: number): Promise<{ ms: number; commits: number }> {
	const faux = fauxProvider();
	const models = createModels();
	models.setProvider(faux.provider);
	const registry = createRegistry();
	const fan = defineTool({
		name: "fan",
		description: "Create child tasks in parallel",
		parameters: Type.Object({}),
		execute: async (_args, api, callContext) => {
			const ids = await Promise.all(
				Array.from({ length: count }, () =>
					api.createTask(Leaf, null, { ownership: { kind: "task", taskId: api.taskId } }, callContext),
				),
			);
			const settled = await Promise.all(ids.map((id) => api.waitForTask(id, callContext)));
			const failed = settled.filter((record) => record.state.outcome.status !== "completed").length;
			return { output: [{ type: "text", text: `${failed} failed` }], isError: failed > 0 };
		},
	});
	registry.install(defineExtension({ name: "bench", tools: [fan], tasks: [Leaf] }));
	const storage = new MemoryStorage();
	const counter = counted(storage);
	const harness = await Harness.open(storage, { models, registry }, context);
	const root = await harness.root(context, { agent: { model: { provider: "faux", modelId: "faux-1" } } });
	faux.setResponses([
		fauxAssistantMessage([fauxToolCall("fan", {}, { id: "fan" })], { stopReason: "toolUse" }),
		fauxAssistantMessage([fauxText("done")]),
	]);
	const started = performance.now();
	const settled = await (await root.submit({ type: "input", content: "go" }, context)).wait(context);
	if (settled.status !== "done") throw new Error(`run: ${settled.status}`);
	await harness.waitForIdle(context);
	const ms = performance.now() - started;
	const page = await root.entries({}, 10, undefined, context);
	const result = page.items.find((entry) => ToolResultEntry.is(entry))?.model?.[0];
	if (result?.role !== "toolResult" || result.isError) throw new Error("run: child tasks failed");
	await harness.close(context);
	return { ms, commits: counter.commits };
}

async function chain(depth: number): Promise<{ ms: number; commits: number }> {
	const registry = createRegistry();
	registry.install(defineExtension({ name: "bench", tasks: [Leaf] }));
	const storage = new MemoryStorage();
	const counter = counted(storage);
	const harness = await Harness.open(storage, { models: createModels(), registry }, context);
	const root = await harness.root(context);
	harness.resume();
	const started = performance.now();
	const top = await root.commit(async (tx) => {
		const top = await tx.createTask(Leaf, null, { ownership: { kind: "conversation" } });
		let owner = top;
		for (let n = 1; n < depth; n++) owner = await tx.createTask(Leaf, null, { ownership: { kind: "task", taskId: owner } });
		return top;
	}, context);
	const settled = await harness.waitForTask(top, context);
	const ms = performance.now() - started;
	if (settled.state.outcome.status !== "completed") throw new Error("chain: top failed");
	await harness.close(context);
	return { ms, commits: counter.commits };
}

const args = process.argv.slice(2);
const sizes = (name: string, defaults: number[]) => {
	const at = args.indexOf(name);
	return at < 0 ? (args.length === 0 ? defaults : []) : args[at + 1]!.split(",").map(Number);
};
console.log("fanout | ms     | commits | ms per task");
for (const n of sizes("fanout", [250, 500, 1000, 2000, 4000])) {
	const { ms, commits } = await fanout(n);
	console.log(`${String(n).padEnd(6)} | ${String(Math.round(ms)).padEnd(6)} | ${String(commits).padEnd(7)} | ${(ms / n).toFixed(2)}`);
}
console.log("chain  | ms     | commits | ms per task");
for (const n of sizes("chain", [500, 2000, 20000])) {
	const { ms, commits } = await chain(n);
	console.log(`${String(n).padEnd(6)} | ${String(Math.round(ms)).padEnd(6)} | ${String(commits).padEnd(7)} | ${(ms / n).toFixed(3)}`);
}
