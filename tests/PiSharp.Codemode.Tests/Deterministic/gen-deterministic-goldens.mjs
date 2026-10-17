#!/usr/bin/env node
// Generates differential-test goldens for PiSharp's deterministic codemode runs (CodemodeSandbox.Start) by running the REAL
// upstream code: @earendil-works/pi-codemode 1.2.0 CodemodeSandbox.start() (dist/index.js, a QuickJS VM in a worker thread).
//
// Usage: node gen-deterministic-goldens.mjs <node_modules> [out.json]
//
// <node_modules> must contain @earendil-works/pi-codemode at 1.2.0. Every scenario is answered by the fixed policy below, which
// DeterministicDifferentialCases.cs applies too, so a rerun with the same package writes the same file:
// - tool "echo" resolves with its argument, except the argument "fail", which rejects with message "no" and the properties
//   { code: 7, name: "Ignored" }; global "search" resolves with ["found", <its argument list>];
// - "immediate": each call is answered as soon as it is made, as result number 1, 2, ... in call order;
// - "batch": nothing is answered until the VM waits (idle); then every unanswered call is numbered from the newest to the oldest
//   and delivered from the oldest to the newest, so results arrive out of seq order and are applied newest first;
// - result number n carries the clock value startedAt + step * n.
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [modules, outArg] = process.argv.slice(2);
if (!modules) {
	console.error("usage: node gen-deterministic-goldens.mjs <node_modules> [out.json]");
	process.exit(2);
}
const nm = path.resolve(modules);
const imp = (p) => import(pathToFileURL(path.join(nm, p)).href);
const version = JSON.parse(fs.readFileSync(path.join(nm, "@earendil-works/pi-codemode/package.json"), "utf8")).version;
if (version !== "1.2.0") throw new Error(`expected @earendil-works/pi-codemode 1.2.0, got ${version}`);
const { CodemodeSandbox } = await imp("@earendil-works/pi-codemode/dist/index.js");
const { PRELUDE_SOURCE } = await imp("@earendil-works/pi-codemode/dist/runtime/prelude-source.js");

function fnv1a(text) {
	let hash = 0x811c9dc5;
	for (let index = 0; index < text.length; index++) {
		hash ^= text.charCodeAt(index);
		hash = Math.imul(hash, 0x01000193);
	}
	return (hash >>> 0).toString(16).padStart(8, "0");
}

const STARTED_AT = Date.UTC(2026, 0, 15, 10, 0, 0);
const TOOLS = [{ name: "echo" }, { name: "my-tool", jsName: "renamed", description: "Renamed." }];
const GLOBALS = [{ name: "search", spread: true }, { name: "ns.lookup" }];

const SCENARIOS = [
	{
		name: "clock-and-random",
		code: `const values = [Date.now(), performance.now(), Math.random(), Math.random(), new Date().toISOString()];
const first = await tools.echo(values);
text(JSON.stringify([Date.now(), performance.now(), Math.random()]));
return await tools.echo(first.concat(Math.random()));`,
	},
	{ name: "seed-zero", seed: 0, code: "return [Math.random(), Math.random(), Math.random()]" },
	{ name: "seed-max", seed: 4294967295, code: "return [Math.random(), Math.random(), Math.random()]" },
	{
		name: "random-after-allocation",
		seed: 77,
		code: `const map = new Map(); const set = new Set();
for (let i = 0; i < 200; i++) { map.set("k" + i, { i, s: Symbol(String(i)) }); set.add([i, /x+/g.exec("xx" + i)]); }
const sorted = [...map.keys()].sort((a, b) => (a < b ? 1 : a > b ? -1 : 0)).slice(0, 3);
JSON.parse(JSON.stringify({ sorted, size: set.size }));
return [sorted, Math.random(), Math.random()];`,
	},
	{
		name: "random-branching",
		seed: 12345,
		code: `let total = 0; const path = [];
for (let i = 0; i < 12; i++) {
  const r = Math.random();
  if (r < 0.4) { total += await tools.echo(i); path.push("one"); }
  else if (r < 0.8) { const both = await Promise.all([tools.echo(i), tools.echo(-i)]); total += both[0] + both[1]; path.push("two"); }
  else { path.push("none"); }
}
console.log("path", path.join(","), Date.now() - ${STARTED_AT});
return { total, path, at: performance.now() };`,
	},
	{
		name: "parallel-and-microtasks",
		code: `const order = [];
const a = tools.echo("a").then((v) => order.push(v));
Promise.resolve().then(() => order.push("micro"));
const xs = await Promise.all([1, 2, 3].map((n) => tools.echo(n).then((v) => { order.push(v); return v; })));
order.push("after all");
await a;
return [order, xs, await tools.echo(xs.reduce((x, y) => x + y))];`,
	},
	{
		name: "batch-order",
		policy: "batch",
		code: `const order = [];
await Promise.all(["a", "b", "c"].map((name) => tools.echo(name).then(() => order.push([name, Date.now() - ${STARTED_AT}]))));
const second = await Promise.all([tools.echo(1), tools.echo(2)]);
return [order, second, Date.now() - ${STARTED_AT}];`,
	},
	{
		name: "globals-and-rejections",
		code: `const found = await search("x", 2);
const looked = await ns.lookup({ id: 1 });
let caught;
try { await tools.echo("fail"); } catch (error) { caught = [error instanceof Error, error.message, error.name, error.code]; }
text(JSON.stringify(caught));
return [found, looked, await tools.renamed("r"), Object.keys(tools), ALL_TOOLS.map((tool) => [tool.name, tool.description])];`,
	},
	{
		name: "store-writes",
		store: { counter: 1, list: [1, 2] },
		code: `const before = load("counter");
const next = await tools.echo(before + 1);
store("counter", next);
store("list", undefined);
store("fresh", { at: Date.now() - ${STARTED_AT} });
return [before, load("counter"), load("list")];`,
	},
	{
		name: "throws-after-calls",
		code: `await tools.echo(1);
await Promise.all([tools.echo(2), tools.echo(3)]);
throw new Error("boom " + Math.random());`,
	},
	{ name: "exits-mid-run", code: `text("before"); await tools.echo(1); exit(); text("never");` },
	{ name: "returns-at-once", code: `return [Date.now(), performance.now()]` },
	{
		name: "stalls",
		code: `await tools.echo(1); await new Promise(() => {});`,
	},
	{
		name: "clock-never-backwards",
		step: -1000,
		code: `const before = Date.now(); await tools.echo(1); const after = Date.now(); await tools.echo(2);
return [before, after, Date.now(), performance.now()];`,
	},
	{
		name: "fractional-clock",
		startedAt: 1000.6,
		step: 0.7,
		code: `const seen = [[Date.now(), performance.now()]];
for (let i = 0; i < 4; i++) { await tools.echo(i); seen.push([Date.now(), performance.now()]); }
return seen;`,
	},
	{
		name: "utc-dates",
		code: `const d = new Date(Date.UTC(2026, 5, 1, 23, 30));
return [d.getHours(), d.getDate(), d.getTimezoneOffset(), new Date(2026, 0, 1).toISOString(), typeof WeakRef, typeof FinalizationRegistry,
  Date.parse("2026-01-15T10:00:00"), JSON.stringify(new Date(0))];`,
	},
];

function answer(call) {
	if (call.target === "global" && call.name === "search") return { ok: true, value: ["found", call.args] };
	if (call.args === "fail") return { ok: false, message: "no", properties: { code: 7, name: "Ignored" } };
	return { ok: true, value: call.args };
}

async function run(sandbox, scenario) {
	const startedAt = scenario.startedAt ?? STARTED_AT;
	const step = scenario.step ?? 1000;
	const events = [];
	const calls = [];
	let fingerprint;
	let seq = 0;
	const unanswered = [];
	let handle;
	const deliver = (n, call) => handle.deliver(n, startedAt + step * n, call.id, answer(call));
	handle = sandbox.start(scenario.code, {
		startedAt,
		seed: scenario.seed ?? 1,
		tools: TOOLS,
		globals: GLOBALS,
		store: scenario.store,
		timeoutMs: 20_000,
		onStarted: (value) => {
			fingerprint = value;
			events.push("started");
		},
		onCall: (call) => {
			calls.push(call);
			events.push(`call ${call.id} ${call.target} ${call.name}`);
			if ((scenario.policy ?? "immediate") === "immediate") {
				const n = ++seq;
				queueMicrotask(() => deliver(n, call));
			} else unanswered.push(call);
		},
		onIdle: (n, issued) => {
			events.push(`idle ${n} ${issued}`);
			if (scenario.policy === "batch" && unanswered.length > 0) {
				const batch = unanswered.splice(0).sort((a, b) => a.id - b.id);
				const numbers = new Map([...batch].reverse().map((call) => [call.id, ++seq]));
				for (const call of batch) deliver(numbers.get(call.id), call);
			}
		},
	});
	const result = await handle.result;
	const outcome = {
		ok: result.ok,
		...(result.ok ? { value: result.value, storeWrites: result.storeWrites } : { error: { kind: result.error.kind, name: result.error.name, message: result.error.message } }),
		output: result.output,
		calls: result.calls.map(({ name, status }) => ({ name, status })),
		...(result.during ? { during: result.during } : {}),
	};
	return { fingerprint, events, calls, result: outcome };
}

const sandbox = new CodemodeSandbox();
const scenarios = [];
let fingerprint;
for (const scenario of SCENARIOS) {
	const recorded = await run(sandbox, scenario);
	// Replayed twice upstream too: a scenario whose recording is not stable is not a golden.
	const again = await run(sandbox, scenario);
	if (JSON.stringify(again) !== JSON.stringify(recorded)) throw new Error(`scenario ${scenario.name} is not deterministic upstream`);
	fingerprint = recorded.fingerprint;
	scenarios.push({
		...scenario,
		startedAt: scenario.startedAt ?? STARTED_AT,
		seed: scenario.seed ?? 1,
		step: scenario.step ?? 1000,
		policy: scenario.policy ?? "immediate",
		store: scenario.store ?? {},
		expected: { events: recorded.events, calls: recorded.calls, result: recorded.result },
	});
}
await sandbox.close();

const golden = {
	description:
		"Differential goldens for PiSharp's deterministic codemode runs (CodemodeSandbox.Start), recorded from the real upstream @earendil-works/pi-codemode 1.2.0 CodemodeSandbox.start(). Regenerate: node gen-deterministic-goldens.mjs <node_modules> [out.json]",
	piCodemode: version,
	upstreamFingerprint: fingerprint,
	preludeHash: fnv1a(PRELUDE_SOURCE),
	tools: TOOLS,
	globals: GLOBALS,
	scenarios,
};
const text = `${JSON.stringify(golden, null, "\t")}\n`;
if (outArg) fs.writeFileSync(outArg, text);
else process.stdout.write(text);
