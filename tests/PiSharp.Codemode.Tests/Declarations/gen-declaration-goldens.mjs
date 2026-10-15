#!/usr/bin/env node
// Generates differential-test goldens for PiSharp's codemode declarations by running the REAL upstream code:
// @earendil-works/pi-codemode 1.2.0 (dist/declarations.js: schemaToType, renderToolSignature, renderToolSample) and
// @earendil-works/pi-coding-agent 1.2.0 (dist/extensions/mcp/tools.js createMcpResultSchema, dist/extensions/codemode/tool.js
// createCodemodeToolDefinition().prepareLoadout and reachesTools).
//
// Usage: node gen-declaration-goldens.mjs <node_modules> [out.json]
//
// <node_modules> must contain @earendil-works/pi-codemode and @earendil-works/pi-coding-agent at 1.2.0. The corpus is built here
// from a fixed seed (mulberry32), so a rerun with the same packages writes the same file.
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [modules, outArg] = process.argv.slice(2);
if (!modules) {
	console.error("usage: node gen-declaration-goldens.mjs <node_modules> [out.json]");
	process.exit(2);
}
const nm = path.resolve(modules);
const imp = (p) => import(pathToFileURL(path.join(nm, p)).href);
const version = (pkg) => JSON.parse(fs.readFileSync(path.join(nm, pkg, "package.json"), "utf8")).version;
for (const pkg of ["@earendil-works/pi-codemode", "@earendil-works/pi-coding-agent"]) {
	if (version(pkg) !== "1.2.0") throw new Error(`expected ${pkg} 1.2.0, got ${version(pkg)}`);
}
const declarations = await imp("@earendil-works/pi-codemode/dist/declarations.js");
const { createMcpResultSchema } = await imp("@earendil-works/pi-coding-agent/dist/extensions/mcp/tools.js");
const { createCodemodeToolDefinition } = await imp("@earendil-works/pi-coding-agent/dist/extensions/codemode/tool.js");
const { createToolSearchToolDefinition } = await imp("@earendil-works/pi-coding-agent/dist/extensions/tool-search/tool.js");

// ------------------------------------------------------------------ deterministic corpus
let seed = 0x5eed1200;
function random() {
	seed = (seed + 0x6d2b79f5) | 0;
	let t = seed;
	t = Math.imul(t ^ (t >>> 15), t | 1);
	t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
	return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
}
const pick = (items) => items[Math.floor(random() * items.length)];
const chance = (p) => random() < p;

const NUMBERS = [0, -0, 1, -1, 2, 5, 10, 100, 30000, 0.5, 0.1, 1.5e-7, 1e21, 123456789012, -2.5, 3.14159, 1e-7, 9007199254740993];
const STRINGS = ["", "^[a-z]+$", "date-time", "uri", "int32", "a\nb", "x y", "tab\there", "quote\"s", "π", "x".repeat(90), "\r\n", "back\\slash"];
const DEFAULTS = [null, 0, -0, 1, "", "text", " ", true, false, [], [1, 2], { a: 1 }, { 2: "b", 1: "a", z: 0 }, { text: "x".repeat(100) },
	"line\nbreak", 1e21, 0.1, [null], { nested: { deep: [true] } }];
const DESCRIPTIONS = [undefined, "", "Plain description.", "  Padded.  ", "Two\nlines", "CRLF\r\nlines", "Sep arated para",
	"Blank\n\n\nlines", "Comment */ end", "Ünïcode π"];
const TYPES = [undefined, "integer", "number", "string", "array", "object", "boolean", "null", ["integer", "null"], ["string", "number"], 7];

function constraints(type) {
	const schema = {};
	if (type !== undefined) schema.type = type;
	if (chance(0.35)) schema.minimum = pick(NUMBERS);
	if (chance(0.35)) schema.maximum = pick(NUMBERS);
	if (chance(0.25)) schema.exclusiveMinimum = chance(0.5) ? pick(NUMBERS) : pick([true, false]);
	if (chance(0.25)) schema.exclusiveMaximum = chance(0.5) ? pick(NUMBERS) : pick([true, false]);
	if (chance(0.15)) schema.multipleOf = chance(0.8) ? pick(NUMBERS) : pick(STRINGS);
	if (chance(0.2)) schema.minLength = pick(NUMBERS);
	if (chance(0.2)) schema.maxLength = chance(0.9) ? pick(NUMBERS) : "8";
	if (chance(0.2)) schema.pattern = chance(0.9) ? pick(STRINGS) : 5;
	if (chance(0.2)) schema.format = chance(0.9) ? pick(STRINGS) : 1;
	if (chance(0.15)) schema.minItems = pick(NUMBERS);
	if (chance(0.15)) schema.maxItems = pick(NUMBERS);
	if (chance(0.15)) schema.uniqueItems = pick([true, false, "yes"]);
	return schema;
}

function propertySchema(depth) {
	const roll = random();
	let schema;
	if (roll < 0.55) {
		schema = constraints(pick(TYPES));
		if (schema.type === "array" && chance(0.7)) schema.items = chance(0.8) ? constraints(pick(TYPES)) : { type: "string" };
		if (schema.type === "object" && depth < 2 && chance(0.6)) Object.assign(schema, objectSchema(depth + 1));
	} else if (roll < 0.8) {
		// Nullable unions (Pydantic's `int | None`), sometimes with constraints on both levels.
		const branches = [constraints(pick(TYPES)), { type: "null" }];
		if (chance(0.15)) branches.push(constraints("string"));
		if (chance(0.1)) branches.unshift({ type: "null" });
		if (chance(0.1)) branches.push(true);
		schema = { [chance(0.7) ? "anyOf" : "oneOf"]: chance(0.5) ? branches : branches.reverse() };
		if (chance(0.4)) Object.assign(schema, constraints(undefined));
		if (chance(0.1)) schema.anyOf = "not an array";
	} else if (roll < 0.9) {
		schema = { enum: pick([["a", "b"], [1, 2, null], ["x"]]) };
		if (chance(0.5)) Object.assign(schema, constraints(undefined));
	} else {
		schema = pick([true, false, { $ref: "#/$defs/Item" }, { const: "fixed", maximum: 3 }, { allOf: [{ type: "integer", minimum: 1 }] }]);
	}
	if (typeof schema === "object") {
		if (chance(0.4)) schema.default = pick(DEFAULTS);
		if (chance(0.15)) schema.deprecated = pick([true, false, "true"]);
		const description = pick(DESCRIPTIONS);
		if (description !== undefined && chance(0.5)) schema.description = description;
	}
	return schema;
}

function objectSchema(depth = 0) {
	const properties = {};
	const count = 1 + Math.floor(random() * 4);
	for (let index = 0; index < count; index++) properties[pick(["id", "name", "limit", "timeoutMs", "tags", "n", "m", "sep", "a-b", "2", "config"]) + (index ? index : "")] = propertySchema(depth);
	const schema = { type: "object", properties };
	const names = Object.keys(properties);
	if (chance(0.5)) schema.required = names.filter(() => chance(0.5));
	if (chance(0.2)) schema.additionalProperties = pick([false, true, { type: "string", minLength: 1 }]);
	return schema;
}

const UPSTREAM_SCHEMAS = [
	{ type: "object", properties: { timeoutMs: { type: "integer", minimum: 0, maximum: 30000, default: 0, description: "Wait in milliseconds." },
		id: { type: "string", pattern: "^[a-z]+$", maxLength: 8 }, tags: { type: "array", items: { type: "string" }, maxItems: 5, uniqueItems: true, deprecated: true },
		name: { type: "string" } }, required: ["id"] },
	{ type: "object", properties: { limit: { anyOf: [{ type: "integer", minimum: 1, maximum: 100 }, { type: "null" }], default: null, title: "Limit" } } },
	{ type: "object", properties: { n: { anyOf: [{ type: "integer", minimum: 10, maximum: 50, format: "int32" }, { type: "null" }], minimum: 0, maximum: 20, format: "port" } } },
	{ type: "object", properties: { n: { type: "integer", minimum: 0, exclusiveMinimum: true, maximum: 10, exclusiveMaximum: false },
		m: { anyOf: [{ type: "number", maximum: 5, exclusiveMaximum: true }, { type: "null" }], maximum: 8 } } },
	{ type: "object", properties: { config: { type: "object", default: { text: "x".repeat(100) } } } },
	{ type: "object", properties: { sep: { type: "string", pattern: "^[\r\n  ]$", default: " ", description: "a b" } } },
	{ type: "object", properties: { n: { type: "integer", minimum: 1 } } },
	{ type: "object", properties: { outer: { type: "object", description: "Outer", properties: { inner: { type: "string", description: "Inner", minLength: 2 } } } } },
	{ type: "object", $defs: { Item: { type: "object", properties: { id: { type: "integer", minimum: 0 } } } },
		properties: { item: { $ref: "#/$defs/Item", description: "An item." }, list: { type: "array", items: { $ref: "#/$defs/Item" }, minItems: 1 } } },
];

// Tags of exactly 80 characters are kept and of 81 left out (MAX_TAG_CHARS), counted before line terminators are escaped.
for (const length of [70, 71, 72]) {
	UPSTREAM_SCHEMAS.push({ type: "object", properties: {
		p: { type: "string", pattern: "p".repeat(length) },
		d: { type: "string", default: "d".repeat(length - 2) },
		f: { type: "string", format: `${"f".repeat(length - 1)}\n` },
		e: { type: "integer", minimum: Number(`1${"0".repeat(Math.min(length - 1, 20))}`) },
	} });
}

const schemaCases = [];
const addSchema = (schema, maxChars) => {
	const expected = declarations.schemaToType(schema, maxChars === undefined ? {} : { maxChars });
	schemaCases.push(maxChars === undefined ? { schema, expected } : { schema, maxChars, expected });
};
for (const schema of UPSTREAM_SCHEMAS) {
	addSchema(schema);
	for (const maxChars of [10, 20, 60, 120]) addSchema(schema, maxChars);
}
for (let index = 0; index < 1500; index++) {
	const schema = objectSchema();
	if (chance(0.2)) schema.$defs = { Item: objectSchema(1) };
	addSchema(schema);
	if (chance(0.25)) {
		const full = declarations.schemaToType(schema).length;
		addSchema(schema, Math.max(1, Math.floor(full * pick([0.5, 0.8, 0.95, 1]))));
	}
}

// ------------------------------------------------------------------ MCP tool declarations
const OUTPUT_SCHEMAS = [
	undefined, null, false, true, 0, "", "schema", [], {},
	{ type: "object", properties: { results: { type: "array", items: { $ref: "#/definitions/Result~1item~0v1" } } }, required: ["results"], additionalProperties: false,
		definitions: { "Result/item~v1": { type: "object", properties: { id: { type: "string" }, score: { type: "number", minimum: 0, maximum: 1 } }, required: ["id", "score"], additionalProperties: false } } },
	{ type: "object", properties: { node: { $ref: "#" }, label: { type: "string", description: "Label of the node." } } },
	{ $defs: { Item: { type: "object", properties: { n: { type: "integer" } } } }, type: "array", items: { $ref: "#/$defs/Item" }, maxItems: 10 },
	{ type: "object", properties: { $ref: { type: "string" }, default: { const: { $ref: "#/x" } }, data: { enum: [{ $ref: "#" }] } }, default: { $ref: "#" },
		examples: [{ $ref: "#/a" }], patternProperties: { "^x": { $ref: "#/properties/data" } }, dependentSchemas: { a: { $ref: "#" } } },
	{ type: "object", properties: { 2: { type: "string" }, 1: { type: "integer", minimum: 1 }, b: { type: "object", properties: { z: { type: "null" } }, required: ["z"] } }, required: ["1"] },
	{ type: "object", properties: { remote: { $ref: "https://example.test/schema.json" }, items: { type: "array", items: { $ref: "#/definitions/Missing" } } } },
	{ anyOf: [{ type: "object", properties: { ok: { const: true } } }, { type: "null" }] },
];
const mcpCases = [];
const inputSchemas = [{ type: "object", properties: {} }, { type: "object", properties: { query: { type: "string", description: "What to find.", minLength: 1 } }, required: ["query"] }];
OUTPUT_SCHEMAS.forEach((outputSchema, index) => {
	const resultSchema = createMcpResultSchema(outputSchema);
	const tool = { name: `mcp__docs__tool_${index}`, description: `Tool ${index}.`, inputSchema: inputSchemas[index % 2], outputSchema: resultSchema };
	mcpCases.push({
		outputSchema: outputSchema === undefined ? { undefined: true } : { value: outputSchema },
		resultSchema,
		signature: declarations.renderToolSignature(tool),
		sample: declarations.renderToolSample(tool),
	});
});

// ------------------------------------------------------------------ codemode loadout presentation
const textOutput = undefined;
const bashOutput = { type: "object", properties: { output: { type: "string" }, exit_code: { type: "number" }, full_output_path: { type: "string" } }, required: ["output", "exit_code"] };
function tools(namespaceName) {
	const docs = { name: namespaceName, aliases: ["docs"], description: "Documentation search." };
	const list = [
		{ name: "read", description: "Read a file.", parameters: { type: "object", properties: { path: { type: "string" } }, required: ["path"] }, exposure: "direct" },
		{ name: "bash", description: "Run a command.", parameters: { type: "object", properties: { command: { type: "string" }, timeout: { type: "number", minimum: 0 } }, required: ["command"] }, outputSchema: bashOutput, exposure: "direct" },
		{ name: "my-tool", description: "Hyphenated.", parameters: { type: "object", properties: {} }, exposure: "codemode", guidelines: ["Use sparingly.", "  "] },
	];
	OUTPUT_SCHEMAS.slice(8, 13).forEach((outputSchema, index) => list.push({
		name: `${namespaceName}__t${index}`, description: `Docs tool ${index}.`, parameters: inputSchemas[index % 2],
		outputSchema: createMcpResultSchema(outputSchema), exposure: index === 3 ? "deferred" : index === 4 ? "direct" : "codemode", namespace: docs,
	}));
	list.push({ name: "mcp__other__x", description: "Other server.", parameters: { type: "object", properties: {} }, outputSchema: createMcpResultSchema(undefined),
		exposure: "codemode", namespace: { name: "mcp__other" } });
	return list;
}
const loadoutCases = [];
for (const mode of ["on", "only"]) {
	for (const budget of [undefined, 0, 200, 3000]) {
		const registered = tools("mcp__docs");
		const agentTools = registered.map((tool) => ({ name: tool.name, label: tool.name, description: tool.description, parameters: tool.parameters, outputSchema: tool.outputSchema, execute: async () => ({ content: [] }) }));
		const declared = agentTools.filter((tool) => registered.find((row) => row.name === tool.name).exposure === "direct");
		const loadout = {
			declared, callable: agentTools, registered: agentTools,
			getExposure: (name) => registered.find((row) => row.name === name)?.exposure ?? "direct",
			getNamespace: (name) => registered.find((row) => row.name === name)?.namespace,
			getPromptGuidelines: (name) => registered.find((row) => row.name === name)?.guidelines ?? [],
		};
		const definition = createCodemodeToolDefinition({ getMode: () => mode, getInlineBudget: () => budget });
		loadoutCases.push({ mode, ...(budget === undefined ? {} : { inlineBudget: budget }), tools: registered, changes: definition.prepareLoadout(loadout) });
	}
}

// ------------------------------------------------------------------ reachesTools
const codemode = createCodemodeToolDefinition({});
const toolSearch = createToolSearchToolDefinition({ getTools: () => [], setActiveTools: () => {} });
const reachCases = [
	"return 1", "await tools.mcp__docs__search({ query: 'x' })", "const n = 'search'; await tools['mcp__docs__' + n]({})",
	"await searchTools('docs')", "ALL_TOOLS.length", "await describeNamespace('docs')", "await describeTool('x')", "mySearchTools()", "searchToolsX()",
	"éALL_TOOLS", "x.ALL_TOOLS", "tools.$dollar_1 + ü_word 42", "", "// @options: {\"timeout_ms\": 1}\ntext(1)",
].map((code) => ({ input: { code }, reach: codemode.reachesTools({ code }) }));
reachCases.push({ input: { code: 42 }, reach: codemode.reachesTools({ code: 42 }) }, { input: {}, reach: codemode.reachesTools({}) });
const toolSearchReach = toolSearch.reachesTools({ query: "docs" });

const out = path.resolve(outArg ?? "declaration-goldens.json");
fs.writeFileSync(out, `${JSON.stringify({
	description: "Differential goldens for PiSharp's codemode declarations, recorded from the real upstream code: @earendil-works/pi-codemode 1.2.0 schemaToType/renderToolSignature/renderToolSample, @earendil-works/pi-coding-agent 1.2.0 createMcpResultSchema, createCodemodeToolDefinition().prepareLoadout and reachesTools, and tool_search reachesTools. Regenerate: node gen-declaration-goldens.mjs <node_modules> [out.json]",
	piCodemode: version("@earendil-works/pi-codemode"),
	piCodingAgent: version("@earendil-works/pi-coding-agent"),
	schemaCases, mcpCases, loadoutCases, reachCases, toolSearchReach,
})}\n`);
console.log(`wrote ${out}: ${schemaCases.length} schemas, ${mcpCases.length} MCP tools, ${loadoutCases.length} loadouts, ${reachCases.length} reaches`);
