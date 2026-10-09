// Self-test for the PiSharp Node bridge virtual Pi modules.
//
//   node selftest.mjs                      run all checks
//   node selftest.mjs --upstream <dir>     also parse upstream Pi `packages/` sources (default: $PI_UPSTREAM_PACKAGES)
//   node selftest.mjs --write-snapshot     regenerate upstream-exports.json from the upstream sources
//
// (a) every runtime export name of the upstream entry points exists on the matching shim module
//     (parsed from upstream when available, otherwise from the upstream-exports.json snapshot);
// (b) TUI components render within width 40 with sensible content and react to input;
// (c) typebox builders produce TypeBox 1.x JSON Schema shapes and the validator accepts/rejects samples;
// (d) theme.fg/bg emit ANSI escapes in truecolor and 256-color modes;
// plus host-hook routing checks for pi-ai streams and built-in tools.
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argValue = (flag) => {
	const i = args.indexOf(flag);
	return i >= 0 ? args[i + 1] : undefined;
};
const upstreamDir = argValue("--upstream") ?? process.env.PI_UPSTREAM_PACKAGES;
const snapshotPath = path.join(here, "upstream-exports.json");

let failures = 0;
let passes = 0;
async function test(name, fn) {
	try {
		await fn();
		passes++;
		console.log(`ok   ${name}`);
	} catch (error) {
		failures++;
		console.log(`FAIL ${name}\n     ${error?.stack?.split("\n").slice(0, 4).join("\n     ") ?? error}`);
	}
}

// ---------------------------------------------------------------------------------------------
// Upstream export parsing (export { ... } from / export function|class|const / export * from)
// ---------------------------------------------------------------------------------------------

function runtimeExports(file, seen = new Set()) {
	const out = new Set();
	if (seen.has(file)) return out;
	seen.add(file);
	let src;
	try {
		src = fs.readFileSync(file, "utf8");
	} catch {
		return out;
	}
	src = src.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
	const resolveSpec = (spec) => {
		if (!spec.startsWith(".")) return null;
		const p = path.resolve(path.dirname(file), spec);
		if (fs.existsSync(p)) return p;
		for (const ext of [".ts", "/index.ts"]) if (fs.existsSync(p.replace(/\.js$/, "") + ext)) return p.replace(/\.js$/, "") + ext;
		return p;
	};
	for (const m of src.matchAll(/export\s+\*\s+from\s+["']([^"']+)["']/g)) {
		const p = resolveSpec(m[1]);
		if (p) for (const n of runtimeExports(p, seen)) out.add(n);
	}
	for (const m of src.matchAll(/export\s+\*\s+as\s+(\w+)\s+from/g)) out.add(m[1]);
	for (const m of src.matchAll(/export\s+(type\s+)?\{([^}]*)\}/g)) {
		if (m[1]) continue;
		for (let part of m[2].split(",")) {
			part = part.trim();
			if (!part || part.startsWith("type ")) continue;
			const as = part.split(/\s+as\s+/);
			out.add((as[1] ?? as[0]).trim());
		}
	}
	for (const m of src.matchAll(/^export\s+(?:declare\s+)?(?:default\s+)?(?:abstract\s+)?(?:async\s+)?(function\*?|class|const|let|var|enum)\s+(\w+)/gm)) {
		if (m[1] === "const" && /^export\s+const\s+enum/.test(m[0])) continue;
		out.add(m[2]);
	}
	return out;
}

const ENTRIES = {
	"pi-tui.mjs": ["tui/src/index.ts"],
	"pi-ai.mjs": ["ai/src/compat.ts", "ai/src/index.ts"],
	"pi-ai-oauth.mjs": ["ai/src/oauth.ts"],
	"pi-ai-providers.mjs": ["ai/src/providers/all.ts"],
	"pi-agent-core.mjs": ["agent/src/index.ts"],
	"pi-coding-agent.mjs": ["coding-agent/src/index.ts"],
};

function expectedExports() {
	if (upstreamDir && fs.existsSync(upstreamDir)) {
		const result = {};
		for (const [mod, files] of Object.entries(ENTRIES)) {
			const names = new Set();
			for (const f of files) for (const n of runtimeExports(path.join(upstreamDir, f))) names.add(n);
			result[mod] = [...names].sort();
		}
		return { source: `upstream ${upstreamDir}`, exports: result };
	}
	return { source: "snapshot upstream-exports.json", exports: JSON.parse(fs.readFileSync(snapshotPath, "utf8")).exports };
}

const STUB = Symbol.for("pisharp.bridge.stub");
const mods = {};
for (const file of [
	"typebox.mjs",
	"typebox-compile.mjs",
	"typebox-value.mjs",
	"pi-tui.mjs",
	"pi-ai.mjs",
	"pi-ai-oauth.mjs",
	"pi-ai-providers.mjs",
	"pi-agent-core.mjs",
	"theme.mjs",
	"pi-coding-agent.mjs",
]) {
	await test(`import ${file}`, async () => {
		mods[file] = await import(pathToFileURL(path.join(here, file)).href);
	});
}

await test("index.json maps every upstream virtual specifier to an existing shim", () => {
	const index = JSON.parse(fs.readFileSync(path.join(here, "index.json"), "utf8"));
	const required = [
		"typebox",
		"typebox/compile",
		"typebox/value",
		"@sinclair/typebox",
		"@sinclair/typebox/compile",
		"@sinclair/typebox/value",
	];
	for (const scope of ["@earendil-works", "@mariozechner"]) {
		for (const name of ["pi-agent-core", "pi-tui", "pi-ai", "pi-ai/compat", "pi-ai/oauth", "pi-ai/providers/all", "pi-coding-agent"]) {
			required.push(`${scope}/${name}`);
		}
	}
	for (const spec of required) {
		assert.ok(index[spec], `missing ${spec}`);
		assert.ok(fs.existsSync(path.join(here, index[spec])), `${spec} -> ${index[spec]} missing`);
	}
	assert.equal(Object.keys(index).length, required.length);
});

const coverage = [];
const expected = expectedExports();
if (args.includes("--write-snapshot")) {
	assert.ok(expected.source.startsWith("upstream"), "--write-snapshot needs --upstream <dir>");
	fs.writeFileSync(snapshotPath, `${JSON.stringify({ commit: "abe508e1b89912adde45528136c3221eb69acdd7", exports: expected.exports }, null, "\t")}\n`);
	console.log(`wrote ${snapshotPath}`);
}
for (const [mod, names] of Object.entries(expected.exports)) {
	await test(`(a) ${mod} exports all ${names.length} upstream runtime names (${expected.source})`, () => {
		const m = mods[mod];
		assert.ok(m, `${mod} not imported`);
		const missing = names.filter((n) => !(n in m));
		assert.deepEqual(missing, [], `missing exports: ${missing.join(", ")}`);
		const stubs = { host: [], unavailable: [] };
		for (const n of names) {
			const v = m[n];
			const kind = (typeof v === "function" || (typeof v === "object" && v !== null)) && v[STUB];
			if (kind) stubs[kind].push(n);
		}
		coverage.push({ mod, total: names.length, ...stubs });
	});
}

// ---------------------------------------------------------------------------------------------
// (b) TUI rendering
// ---------------------------------------------------------------------------------------------

const tui = mods["pi-tui.mjs"];
const themeMod = mods["theme.mjs"];
const WIDTH = 40;
const strip = (s) => s.replace(/\x1b\[[0-9;]*[A-Za-z]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b_[^\x07\x1b]*(?:\x07|\x1b\\)/g, "");
function assertWidth(lines, width = WIDTH) {
	assert.ok(Array.isArray(lines), "render must return an array");
	for (const line of lines) {
		assert.equal(typeof line, "string");
		assert.ok(tui.visibleWidth(line) <= width, `line too wide (${tui.visibleWidth(line)} > ${width}): ${JSON.stringify(strip(line))}`);
	}
}
const fakeTui = { terminal: { rows: 24, columns: WIDTH }, requestRender() {} };

await test("(b) visibleWidth / truncateToWidth / wrapTextWithAnsi", () => {
	assert.equal(tui.visibleWidth("hello"), 5);
	assert.equal(tui.visibleWidth("\x1b[31mred\x1b[39m"), 3);
	assert.equal(tui.visibleWidth("日本語"), 6);
	assert.equal(tui.visibleWidth("😀"), 2);
	assert.equal(tui.visibleWidth("a\tb"), 5);
	const t = tui.truncateToWidth("abcdefghijklmnop", 10);
	assert.equal(strip(t), "abcdefg...");
	assert.equal(tui.visibleWidth(t), 10);
	const wrapped = tui.wrapTextWithAnsi("\x1b[1mbold words that wrap across several lines\x1b[22m", 12);
	assert.ok(wrapped.length >= 3);
	assertWidth(wrapped, 12);
	assert.ok(wrapped[1].startsWith("\x1b[1m"), "style re-opened on wrapped line");
	assert.equal(tui.sliceByColumn("abcdef", 2, 3), "cde");
});

await test("(b) keys: matchesKey / parseKey / Key / isKeyRelease", () => {
	assert.ok(tui.matchesKey("\x1b[A", "up"));
	assert.ok(tui.matchesKey("\x03", tui.Key.ctrl("c")));
	assert.ok(tui.matchesKey("\r", tui.Key.enter));
	assert.ok(tui.matchesKey("\x1b", "escape"));
	assert.equal(tui.parseKey("\x1b[B"), "down");
	assert.equal(tui.parseKey("\x01"), "ctrl+a");
	assert.equal(tui.parseKey("\x1b[97;5u"), "ctrl+a");
	assert.ok(tui.isKeyRelease("\x1b[97;1:3u"));
	assert.ok(!tui.isKeyRelease("a"));
	assert.ok(tui.isKeyRepeat("\x1b[97;1:2u"));
});

await test("(b) Text renders wrapped and padded to width", () => {
	const text = new tui.Text("Hello world, this is a long line of text that must wrap at forty columns.", 1, 1);
	const lines = text.render(WIDTH);
	assertWidth(lines);
	assert.ok(lines.length >= 4, "padding + wrapped lines");
	assert.equal(lines[0].trim(), "");
	assert.ok(strip(lines.join("\n")).includes("Hello world"));
	assert.ok(lines.every((l) => tui.visibleWidth(l) === WIDTH));
});

await test("(b) Box + Container + Spacer + TruncatedText", () => {
	const container = new tui.Container();
	const box = new tui.Box(2, 1, (s) => `\x1b[44m${s}\x1b[49m`);
	box.addChild(new tui.Text("Boxed content", 0, 0));
	container.addChild(box);
	container.addChild(new tui.Spacer(2));
	container.addChild(new tui.TruncatedText("A very long single line that is certainly wider than forty columns"));
	const lines = container.render(WIDTH);
	assertWidth(lines);
	assert.equal(lines.length, 3 + 2 + 1);
	assert.ok(lines[1].includes("\x1b[44m"), "box background applied");
	assert.equal(strip(lines[1]).slice(0, 15), "  Boxed content");
	assert.ok(strip(lines[5]).endsWith("..."));
});

await test("(b) Markdown renders headings, emphasis, lists, code, tables and quotes", () => {
	const md = new tui.Markdown(
		[
			"# Title",
			"",
			"Some **bold** and *italic* text with `code` and a [link](https://example.com).",
			"",
			"- item one",
			"- item two",
			"  - nested item",
			"",
			"1. first",
			"2. second",
			"",
			"```js",
			"const x = 1;",
			"```",
			"",
			"| a | b |",
			"|---|---|",
			"| 1 | 2 |",
			"",
			"> quoted text",
			"",
			"---",
		].join("\n"),
		1,
		0,
		themeMod.getMarkdownTheme(),
	);
	const lines = md.render(WIDTH);
	assertWidth(lines);
	const plain = lines.map(strip);
	const joined = plain.join("\n");
	assert.ok(plain.some((l) => l.includes("Title")), "heading");
	assert.ok(joined.includes("bold") && joined.includes("italic") && joined.includes("code"), "inline");
	assert.ok(joined.includes("link") && joined.includes("https://example.com"), "link text + url");
	assert.ok(plain.some((l) => l.includes("- item one")), "bullet");
	assert.ok(plain.some((l) => l.includes("    - nested item")), "nested bullet");
	assert.ok(plain.some((l) => l.includes("2. second")), "ordered");
	assert.ok(plain.some((l) => l.includes("```js")) && plain.some((l) => l.includes("const x = 1;")), "code block");
	assert.ok(plain.some((l) => l.includes("┌")) && plain.some((l) => l.includes("│ 1")), "table");
	assert.ok(plain.some((l) => l.includes("│ quoted text")), "blockquote");
	assert.ok(plain.some((l) => l.includes("─────")), "hr");
	assert.ok(lines.join("").includes("\x1b["), "styled with ANSI");
});

await test("(b) SelectList navigates with handleInput and selects", () => {
	const items = [
		{ value: "a", label: "Alpha", description: "first" },
		{ value: "b", label: "Beta", description: "second" },
		{ value: "c", label: "Gamma", description: "third" },
	];
	const list = new tui.SelectList(items, 5, themeMod.getSelectListTheme());
	let selected;
	let cancelled = false;
	list.onSelect = (item) => {
		selected = item.value;
	};
	list.onCancel = () => {
		cancelled = true;
	};
	let lines = list.render(WIDTH);
	assertWidth(lines);
	assert.ok(strip(lines[0]).includes("Alpha"));
	assert.ok(strip(lines[0]).startsWith("→"), "first item selected");
	list.handleInput("\x1b[B");
	lines = list.render(WIDTH);
	assert.ok(strip(lines[1]).startsWith("→") && strip(lines[1]).includes("Beta"), "cursor moved to Beta");
	assert.equal(list.getSelectedItem().value, "b");
	list.handleInput("\r");
	assert.equal(selected, "b");
	list.handleInput("\x1b");
	assert.ok(cancelled);
});

await test("(b) SettingsList renders and cycles values", () => {
	let changed;
	const settings = new tui.SettingsList(
		[{ id: "mode", label: "Mode", currentValue: "fast", values: ["fast", "slow"], description: "Speed" }],
		5,
		themeMod.getSettingsListTheme(),
		(id, value) => {
			changed = `${id}=${value}`;
		},
		() => {},
	);
	const lines = settings.render(WIDTH);
	assertWidth(lines);
	assert.ok(lines.map(strip).join("\n").includes("Mode"));
	settings.handleInput("\r");
	assert.equal(changed, "mode=slow");
});

await test("(b) Input accepts typing and submit", () => {
	const input = new tui.Input();
	let submitted;
	input.onSubmit = (v) => {
		submitted = v;
	};
	for (const ch of "hi there") input.handleInput(ch);
	input.handleInput("\x7f");
	assert.equal(input.getValue(), "hi ther");
	const lines = input.render(WIDTH);
	assertWidth(lines);
	input.handleInput("\r");
	assert.equal(submitted, "hi ther");
});

await test("(b) Editor supports multi-line typing, cursor movement and submit", () => {
	const editor = new tui.Editor(fakeTui, themeMod.getEditorTheme());
	let submitted;
	editor.onSubmit = (text) => {
		submitted = text;
	};
	for (const ch of "first line") editor.handleInput(ch);
	editor.handleInput("\n"); // ctrl+j: new line
	for (const ch of "second") editor.handleInput(ch);
	assert.equal(editor.getText(), "first line\nsecond");
	editor.handleInput("\x1b[D"); // left
	editor.handleInput("\x7f"); // backspace deletes 'n'
	assert.equal(editor.getText(), "first line\nsecod");
	editor.handleInput("\x1b[A"); // up to first line
	editor.handleInput("X");
	assert.equal(editor.getText(), "firsXt line\nsecod", "cursor keeps its column when moving up");
	editor.focused = true;
	const lines = editor.render(WIDTH);
	assertWidth(lines);
	assert.ok(lines.length >= 4, "border + 2 lines + border");
	const plain = lines.map(strip).join("\n");
	assert.ok(plain.includes("secod"));
	editor.setText("long text ".repeat(10).trim());
	assertWidth(editor.render(WIDTH));
	editor.handleInput("\r");
	assert.equal(submitted, "long text ".repeat(10).trim());
});

await test("(b) Loader / CancellableLoader / Image fallback", () => {
	const loader = new tui.CancellableLoader(fakeTui, (s) => s, (s) => s, "Working...");
	const lines = loader.render(WIDTH);
	assertWidth(lines);
	assert.ok(strip(lines.join("")).includes("Working..."));
	let aborted = false;
	loader.onAbort = () => {
		aborted = true;
	};
	loader.handleInput("\x1b");
	assert.ok(aborted && loader.signal.aborted);
	loader.dispose();
	tui.setCapabilities({ images: null, trueColor: true, hyperlinks: false });
	const image = new tui.Image("iVBORw0KGgo=", "image/png", { fallbackColor: (s) => s }, { filename: "pic.png" }, { widthPx: 10, heightPx: 10 });
	const imageLines = image.render(WIDTH);
	assertWidth(imageLines);
	assert.ok(strip(imageLines.join("")).includes("pic.png") || strip(imageLines.join("")).includes("image"));
});

await test("(b) fuzzyFilter / fuzzyMatch / CURSOR_MARKER", () => {
	const result = tui.fuzzyFilter(["apple", "banana", "grape"], "ap", (s) => s);
	assert.ok(result.includes("apple") && result.includes("grape") && !result.includes("banana"));
	assert.ok(tui.fuzzyMatch("ap", "apple").matches);
	assert.equal(typeof tui.CURSOR_MARKER, "string");
});

await test("(b) pi-coding-agent DynamicBorder / BorderedLoader / keyHint / truncateToVisualLines", () => {
	const ca = mods["pi-coding-agent.mjs"];
	const border = new ca.DynamicBorder();
	const lines = border.render(WIDTH);
	assert.equal(tui.visibleWidth(lines[0]), WIDTH);
	const loader = new ca.BorderedLoader(fakeTui, themeMod.theme, "Loading data");
	assertWidth(loader.render(WIDTH));
	assert.ok(strip(loader.render(WIDTH).join("\n")).includes("Loading data"));
	loader.dispose?.();
	const hint = ca.keyHint("tui.select.cancel", "to cancel");
	assert.ok(strip(hint).includes("escape") && strip(hint).includes("to cancel"), strip(hint));
	assert.ok(strip(ca.keyHint("app.interrupt", "interrupt")).length > "interrupt".length, "app keybindings installed");
	assert.ok(strip(ca.rawKeyHint("ctrl+x", "do it")).includes("ctrl+x"));
	const visual = ca.truncateToVisualLines("a\nb\nc\nd\ne", 2, WIDTH);
	assert.equal(visual.visualLines.length, 2);
});

// ---------------------------------------------------------------------------------------------
// (c) TypeBox
// ---------------------------------------------------------------------------------------------

const tb = mods["typebox.mjs"];
const tbc = mods["typebox-compile.mjs"];
const tbv = mods["typebox-value.mjs"];

await test("(c) Type.Object with Optional produces the TypeBox JSON shape", () => {
	const { Type } = tb;
	const schema = Type.Object({
		path: Type.String({ description: "File path" }),
		limit: Type.Optional(Type.Integer({ minimum: 1 })),
		mode: Type.Union([Type.Literal("a"), Type.Literal("b")]),
		tags: Type.Optional(Type.Array(Type.String())),
	});
	assert.deepEqual(JSON.parse(JSON.stringify(schema)), {
		type: "object",
		required: ["path", "mode"],
		properties: {
			path: { type: "string", description: "File path" },
			limit: { type: "integer", minimum: 1 },
			mode: { anyOf: [{ type: "string", const: "a" }, { type: "string", const: "b" }] },
			tags: { type: "array", items: { type: "string" } },
		},
	});
	assert.equal(schema["~kind"], "Object");
	assert.equal(schema.properties.limit["~optional"], true);
	assert.ok(!Object.keys(schema.properties.limit).includes("~optional"), "marker is non-enumerable");
	assert.ok(tb.Kind && tb.Static === undefined && tb.default === Type);
	assert.deepEqual(JSON.parse(JSON.stringify(Type.Partial(schema))).required, undefined);
	assert.deepEqual(JSON.parse(JSON.stringify(Type.Pick(schema, ["path"]))), {
		type: "object",
		required: ["path"],
		properties: { path: { type: "string", description: "File path" } },
	});
	assert.deepEqual(JSON.parse(JSON.stringify(Type.Tuple([Type.String(), Type.Number()]))), {
		type: "array",
		additionalItems: false,
		items: [{ type: "string" }, { type: "number" }],
		minItems: 2,
	});
	assert.deepEqual(JSON.parse(JSON.stringify(Type.Record(Type.String(), Type.Number()))), {
		type: "object",
		patternProperties: { "^.*$": { type: "number" } },
	});
	assert.deepEqual(JSON.parse(JSON.stringify(Type.Enum({ A: "a", B: "b" }))), { enum: ["a", "b"] });
	assert.deepEqual(JSON.parse(JSON.stringify(mods["pi-ai.mjs"].StringEnum(["x", "y"], { description: "d" }))), {
		type: "string",
		enum: ["x", "y"],
		description: "d",
	});
});

await test("(c) validator accepts/rejects samples with TypeBox-style errors", () => {
	const { Type } = tb;
	const schema = Type.Object(
		{ path: Type.String({ minLength: 1 }), limit: Type.Optional(Type.Integer({ minimum: 1 })) },
		{ additionalProperties: false },
	);
	const v = tbc.Compile(schema);
	assert.ok(v.Check({ path: "a.txt" }));
	assert.ok(v.Check({ path: "a.txt", limit: 3 }));
	assert.ok(v.Check({ path: "a.txt", limit: undefined }), "optional may be undefined");
	assert.ok(!v.Check({ limit: 3 }));
	assert.ok(!v.Check({ path: "x", limit: 0 }));
	assert.ok(!v.Check({ path: "x", extra: 1 }));
	assert.ok(!v.Check([]));
	assert.deepEqual(v.Errors({ limit: 1.5, extra: true }), [
		{ keyword: "required", schemaPath: "#", instancePath: "", params: { requiredProperties: ["path"] }, message: "must have required properties path" },
		{ keyword: "boolean", schemaPath: "#/additionalProperties", instancePath: "/extra", params: {}, message: "schema is false" },
		{ keyword: "additionalProperties", schemaPath: "#", instancePath: "", params: { additionalProperties: ["extra"] }, message: "must not have additional properties" },
		{ keyword: "type", schemaPath: "#/properties/limit", instancePath: "/limit", params: { type: "integer" }, message: "must be integer" },
	]);
	assert.ok(tbc.TypeCompiler.Compile(schema).Check({ path: "p" }));
	assert.ok(tbv.Value.Check(schema, { path: "p" }));
	assert.equal(tbv.Value.Errors(schema, {}).length, 1);
	const converted = tbv.Value.Convert(Type.Object({ n: Type.Number(), b: Type.Boolean(), s: Type.String() }), { n: "42", b: "true", s: 7 });
	assert.deepEqual(converted, { n: 42, b: true, s: "7" });
	assert.deepEqual(tbv.Value.Default(Type.Object({ a: Type.String({ default: "x" }) }), {}), { a: "x" });
	assert.deepEqual(tbv.Value.Clean(Type.Object({ a: Type.String() }), { a: "x", z: 1 }), { a: "x" });
	assert.throws(() => tbv.Value.Parse(Type.String(), 5), (e) => e.name !== undefined && e.message === "Parse");
	assert.equal(tbv.Value.Parse(Type.String(), "ok"), "ok");
});

await test("(c) pi-ai validateToolArguments coerces and reports like upstream", () => {
	const { Type } = tb;
	const ai = mods["pi-ai.mjs"];
	const tool = { name: "t", description: "", parameters: Type.Object({ count: Type.Number(), flag: Type.Optional(Type.Boolean()) }) };
	assert.deepEqual(ai.validateToolArguments(tool, { id: "1", name: "t", arguments: { count: "3", flag: "false" } }), { count: 3, flag: false });
	assert.throws(() => ai.validateToolArguments(tool, { id: "1", name: "t", arguments: {} }), /Validation failed for tool "t":\n {2}- count: must have required properties count/);
});

// ---------------------------------------------------------------------------------------------
// (d) Theme
// ---------------------------------------------------------------------------------------------

await test("(d) theme.fg/bg produce ANSI escapes (truecolor and 256)", () => {
	const dark = themeMod.createTheme("dark", "truecolor");
	const fg = dark.fg("accent", "hi");
	assert.match(fg, /^\x1b\[38;2;\d+;\d+;\d+mhi\x1b\[39m$/);
	assert.match(dark.bg("selectedBg", "x"), /^\x1b\[48;2;\d+;\d+;\d+mx\x1b\[49m$/);
	assert.equal(dark.bold("b"), "\x1b[1mb\x1b[22m");
	assert.equal(dark.italic("i"), "\x1b[3mi\x1b[23m");
	assert.equal(dark.underline("u"), "\x1b[4mu\x1b[24m");
	assert.equal(dark.strikethrough("s"), "\x1b[9ms\x1b[29m");
	assert.equal(dark.inverse("v"), "\x1b[7mv\x1b[27m");
	assert.match(dark.getFgAnsi("error"), /^\x1b\[38;2;/);
	assert.match(dark.getBgAnsi("toolErrorBg"), /^\x1b\[48;2;/);
	const light256 = themeMod.createTheme("light", "256color");
	assert.match(light256.fg("accent", "hi"), /^\x1b\[38;5;\d+mhi\x1b\[39m$/);
	assert.equal(light256.getColorMode(), "256color");
	assert.equal(dark.appearance, "dark");
	assert.equal(light256.appearance, "light");
	const grey = Object.fromEntries(Object.keys(dark.colors).map((token) => [token, "#808080"]));
	const fromJson = themeMod.createTheme({ name: "grey", colors: grey }, "truecolor");
	assert.ok(fromJson instanceof themeMod.Theme);
	assert.equal(fromJson.fg("accent", "g"), "\x1b[38;2;128;128;128mg\x1b[39m");
	const md = themeMod.getMarkdownTheme(dark);
	assert.match(md.heading("h"), /\x1b\[38;2;/);
	assert.deepEqual(themeMod.getMarkdownTheme(dark).highlightCode("a\nb", "js").length, 2);
	assert.ok(typeof themeMod.theme.fg("muted", "x") === "string", "global theme initialized");
	assert.equal(themeMod.getEditorTheme(dark).selectList.selectedText("x"), dark.fg("accent", "x"));
});

// ---------------------------------------------------------------------------------------------
// pi-ai / pi-agent-core / pi-coding-agent behaviour and host routing
// ---------------------------------------------------------------------------------------------

await test("pi-ai pure helpers (uuidv7, calculateCost, EventStream, system prompt helpers)", async () => {
	const ai = mods["pi-ai.mjs"];
	assert.match(ai.uuidv7(), /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
	const usage = { input: 1_000_000, output: 1_000_000, cacheRead: 0, cacheWrite: 0, totalTokens: 2_000_000, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
	ai.calculateCost({ cost: { input: 3, output: 15, cacheRead: 0.3, cacheWrite: 3.75 } }, usage);
	assert.equal(usage.cost.total, 18);
	const stream = ai.createAssistantMessageEventStream();
	const message = { role: "assistant", content: [], api: "x", provider: "p", model: "m", usage, stopReason: "stop", timestamp: 0 };
	stream.push({ type: "start", partial: message });
	stream.push({ type: "done", reason: "stop", message });
	const events = [];
	for await (const e of stream) events.push(e.type);
	assert.deepEqual(events, ["start", "done"]);
	assert.equal((await stream.result()).stopReason, "stop");
	assert.deepEqual(ai.parseStreamingJson('{"a": [1, 2'), { a: [1, 2] });
	assert.equal(typeof ai.getCurrentSystemPrompt, "function");
	assert.equal(typeof ai.collapseSystemMessages, "function");
});

await test("host hook: pi-ai stream functions throw without the bridge and route through it", async () => {
	const ai = mods["pi-ai.mjs"];
	const model = { id: "m", name: "m", api: "anthropic-messages", provider: "anthropic", baseUrl: "", reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 1000, maxTokens: 100 };
	const context = { messages: [{ role: "user", content: "hi", timestamp: 0 }] };
	delete globalThis.__pisharpBridge;
	assert.throws(() => ai.streamSimple(model, context), /streamSimple is not available in the PiSharp Node bridge/);
	await assert.rejects(ai.completeSimple(model, context), /completeSimple is not available in the PiSharp Node bridge/);
	assert.throws(() => ai.getModel("anthropic", "x"), /getModel is not available in the PiSharp Node bridge/);
	const calls = [];
	globalThis.__pisharpBridge = {
		call(name, ...callArgs) {
			calls.push(name);
			if (name === "getModel") return { ...model, id: callArgs[1] };
			const msg = { role: "assistant", content: [{ type: "text", text: "pong" }], api: model.api, provider: model.provider, model: model.id, usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }, stopReason: "stop", timestamp: 0 };
			return Promise.resolve(msg);
		},
	};
	try {
		assert.equal(ai.getModel("anthropic", "claude").id, "claude");
		const result = await ai.completeSimple(model, context);
		assert.equal(result.content[0].text, "pong");
		assert.ok(calls.includes("streamSimple"));
		// Extension-registered API providers are used directly (custom-provider examples).
		ai.registerApiProvider({ api: "my-api", stream: () => ai.createAssistantMessageEventStream(), streamSimple: () => ai.createAssistantMessageEventStream() }, "ext");
		assert.ok(ai.getApiProvider("my-api"));
		ai.unregisterApiProviders("ext");
		assert.equal(ai.getApiProvider("my-api"), undefined);
	} finally {
		delete globalThis.__pisharpBridge;
	}
});

await test("pi-agent-core Agent / agentLoop are ported and use the provided stream function", async () => {
	const core = mods["pi-agent-core.mjs"];
	const ai = mods["pi-ai.mjs"];
	const model = { id: "m", name: "m", api: "faux", provider: "faux", baseUrl: "", reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 1000, maxTokens: 100 };
	const streamFn = () => {
		const s = ai.createAssistantMessageEventStream();
		const msg = { role: "assistant", content: [{ type: "text", text: "hello from agent" }], api: "faux", provider: "faux", model: "m", usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }, stopReason: "stop", timestamp: Date.now() };
		queueMicrotask(() => {
			s.push({ type: "start", partial: msg });
			s.push({ type: "done", reason: "stop", message: msg });
		});
		return s;
	};
	const agent = new core.Agent({ initialState: { model, systemPrompt: "", tools: [] }, streamFn });
	await agent.prompt("hi");
	const last = agent.state.messages.at(-1);
	assert.equal(last.role, "assistant");
	assert.equal(last.content[0].text, "hello from agent");
});

await test("pi-coding-agent: defineTool, guards, truncation, frontmatter, config, convertToLlm", async () => {
	const ca = mods["pi-coding-agent.mjs"];
	const tool = { name: "x", label: "x", description: "d", parameters: tb.Type.Object({}), execute: async () => ({ content: [] }) };
	assert.equal(ca.defineTool(tool), tool);
	assert.ok(ca.isToolCallEventType("bash", { toolName: "bash", input: {} }));
	assert.ok(ca.isBashToolResult({ toolName: "bash" }));
	assert.ok(!ca.isReadToolResult({ toolName: "bash" }));
	assert.equal(ca.DEFAULT_MAX_LINES, 2000);
	assert.equal(ca.DEFAULT_MAX_BYTES, 50 * 1024);
	assert.equal(ca.formatSize(2048), "2.0KB");
	const head = ca.truncateHead("a\nb\nc", { maxLines: 2 });
	assert.equal(head.content, "a\nb");
	assert.ok(head.truncated);
	assert.equal(ca.truncateTail("a\nb\nc", { maxLines: 2 }).content, "b\nc");
	const fm = ca.parseFrontmatter("---\nname: demo\ntags: [a, b]\nnested:\n  key: 1\n  list:\n    - x\n    - y\ndescription: |\n  multi\n  line\n---\nBody text");
	assert.deepEqual(fm.frontmatter, { name: "demo", tags: ["a", "b"], nested: { key: 1, list: ["x", "y"] }, description: "multi\nline\n" });
	assert.equal(fm.body, "Body text");
	assert.equal(ca.stripFrontmatter("---\na: 1\n---\nrest"), "rest");
	assert.equal(ca.CONFIG_DIR_NAME, ".pi");
	assert.equal(ca.VERSION, "1.1.0");
	const previous = process.env.PI_CODING_AGENT_DIR;
	process.env.PI_CODING_AGENT_DIR = path.join(here, "agent-dir-test");
	assert.equal(ca.getAgentDir(), path.join(here, "agent-dir-test"));
	if (previous === undefined) delete process.env.PI_CODING_AGENT_DIR;
	else process.env.PI_CODING_AGENT_DIR = previous;
	assert.ok(ca.getAgentDir().endsWith(path.join(".pi", "agent")) || process.env.PI_CODING_AGENT_DIR);
	const llm = ca.convertToLlm([{ role: "user", content: "hi", timestamp: 0 }]);
	assert.equal(llm[0].role, "user");
	assert.ok(ca.serializeConversation(llm).includes("hi"));
	let order = [];
	await Promise.all([
		ca.withFileMutationQueue(path.join(here, "q.txt"), async () => {
			await new Promise((r) => setTimeout(r, 10));
			order.push(1);
		}),
		ca.withFileMutationQueue(path.join(here, "q.txt"), async () => {
			order.push(2);
		}),
	]);
	assert.deepEqual(order, [1, 2]);
	order = undefined;
	assert.equal(ca.parseSkillBlock('<skill name="s" location="/l">\nbody\n</skill>\n\nquestion').userMessage, "question");
	assert.throws(() => new ca.SessionManager(), /SessionManager is not available in the PiSharp Node bridge/);
	assert.throws(() => ca.SessionManager.create("x"), /SessionManager.create is not available in the PiSharp Node bridge/);
	assert.throws(() => ca.copyToClipboard("x"), /copyToClipboard is not available in the PiSharp Node bridge/);
});

await test("pi-coding-agent built-in tools: upstream metadata and host-backed execute", async () => {
	const ca = mods["pi-coding-agent.mjs"];
	const read = ca.createReadToolDefinition("/work");
	assert.equal(read.name, "read");
	assert.ok(read.description.startsWith("Read the contents of a file."));
	assert.deepEqual(JSON.parse(JSON.stringify(read.parameters)).required, ["path"]);
	const bash = ca.createBashTool("/work");
	assert.equal(bash.name, "bash");
	assert.equal(bash.promptSnippet, "Execute bash commands (ls, grep, find, etc.)");
	const edit = ca.createEditToolDefinition("/work");
	assert.deepEqual(edit.prepareArguments({ path: "f", oldText: "a", newText: "b" }), { path: "f", edits: [{ oldText: "a", newText: "b" }] });
	for (const create of [ca.createGrepTool, ca.createFindTool, ca.createLsTool, ca.createWriteTool, ca.createPowerShellTool]) {
		const t = create("/work");
		assert.ok(t.name && t.description && t.parameters && typeof t.execute === "function");
	}
	assert.equal(ca.createCodingTools("/w").map((t) => t.name).join(","), "read,bash,edit,write");
	assert.equal(ca.createReadOnlyTools("/w").map((t) => t.name).join(","), "read,grep,find,ls");
	await assert.rejects(read.execute("call-1", { path: "a.txt" }), /builtinTool.execute is not available in the PiSharp Node bridge/);
	let request;
	globalThis.__pisharpBridge = {
		async call(name, req) {
			assert.equal(name, "builtinTool.execute");
			request = req;
			return { content: [{ type: "text", text: "file body" }], details: undefined };
		},
	};
	try {
		const result = await bash.execute("call-2", { command: "ls" }, undefined, undefined, { cwd: "/elsewhere" });
		assert.equal(result.content[0].text, "file body");
		assert.deepEqual(request, { name: "bash", toolCallId: "call-2", params: { command: "ls" }, cwd: "/elsewhere" });
		const ops = { readFile: async () => Buffer.from("x"), access: async () => {} };
		await ca.createReadTool("/w", { operations: ops }).execute("call-3", { path: "p" });
		assert.equal(request.operations, ops);
		assert.equal(request.cwd, "/w");
	} finally {
		delete globalThis.__pisharpBridge;
	}
});

// ---------------------------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------------------------

console.log("\nexport coverage (upstream runtime names):");
for (const row of coverage) {
	const stubbed = row.host.length + row.unavailable.length;
	console.log(`  ${row.mod.padEnd(22)} ${String(row.total).padStart(4)} names, ${row.total - stubbed} implemented, ${row.host.length} host-hook, ${row.unavailable.length} unavailable`);
	if (args.includes("--verbose")) {
		if (row.host.length) console.log(`      host-hook: ${row.host.join(", ")}`);
		if (row.unavailable.length) console.log(`      unavailable: ${row.unavailable.join(", ")}`);
	}
}
console.log(`\n${passes} passed, ${failures} failed`);
process.exitCode = failures > 0 ? 1 : 0;
