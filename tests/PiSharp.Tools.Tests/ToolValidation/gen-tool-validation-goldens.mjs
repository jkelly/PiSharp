#!/usr/bin/env node
// Generates differential-test fixtures for PiSharp's ToolArgumentValidation port by running the REAL
// upstream code: @earendil-works/pi-ai 1.1.0 validateToolArguments (dist/utils/validation.js) with
// typebox 1.3.27, and the real Pi built-in tool schemas from @earendil-works/pi-coding-agent.
//
// Usage:
//   node gen-tool-validation-goldens.mjs <node_modules> [out.json]          -> compact goldens (a few hundred cases)
//   node gen-tool-validation-goldens.mjs <node_modules> [out.json] --full   -> full differential corpus (thousands)
//
// <node_modules> must contain @earendil-works/pi-ai, @earendil-works/pi-coding-agent and typebox.
// Schemas built with TypeBox are serialized with their hidden (non-enumerable) TypeBox metadata keys
// ("~kind", "~optional", ...) made visible, because Value.Convert dispatches on "~kind".
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const argv = process.argv.slice(2);
const full = argv.includes("--full");
const positional = argv.filter((a) => !a.startsWith("--"));
if (positional.length < 1) {
	console.error("usage: node gen-tool-validation-goldens.mjs <node_modules> [out.json] [--full]");
	process.exit(2);
}
const nm = path.resolve(positional[0]);
const outFile = path.resolve(positional[1] ?? (full ? "tool-validation-corpus.json" : "tool-validation-goldens.json"));
const imp = (p) => import(pathToFileURL(path.join(nm, p)).href);

const { Type } = await imp("typebox/build/index.mjs");
const { validateToolArguments } = await imp("@earendil-works/pi-ai/dist/utils/validation.js");
const { StringEnum } = await imp("@earendil-works/pi-ai/dist/utils/typebox-helpers.js");
const tools = await imp("@earendil-works/pi-coding-agent/dist/core/tools/index.js");
const piAiVersion = JSON.parse(fs.readFileSync(path.join(nm, "@earendil-works/pi-ai/package.json"), "utf8")).version;
const typeboxVersion = JSON.parse(fs.readFileSync(path.join(nm, "typebox/package.json"), "utf8")).version;

// ---------------------------------------------------------------------------------------------
// Deterministic PRNG
// ---------------------------------------------------------------------------------------------
function mulberry32(seed) {
	return () => {
		seed |= 0;
		seed = (seed + 0x6d2b79f5) | 0;
		let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
		t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
		return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
	};
}
const seedArg = argv.find((a) => a.startsWith("--seed="));
let rnd = mulberry32(seedArg ? Number(seedArg.slice(7)) : 0x5eed1234);
const pick = (arr) => arr[Math.floor(rnd() * arr.length)];
const chance = (p) => rnd() < p;

// ---------------------------------------------------------------------------------------------
// Schema serialization (exposes TypeBox hidden keys)
// ---------------------------------------------------------------------------------------------
function serializeSchema(v) {
	if (Array.isArray(v)) return v.map(serializeSchema);
	if (v && typeof v === "object") {
		const o = {};
		for (const k of Object.getOwnPropertyNames(v)) {
			if (v[k] === undefined || typeof v[k] === "function") continue;
			o[k] = serializeSchema(v[k]);
		}
		return o;
	}
	return v;
}

// ---------------------------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------------------------
const schemas = []; // { name, origin: "typebox" | "json" | "legacy", schema (live object) }
function addTypeBox(name, schema) {
	schemas.push({ name, origin: "typebox", schema });
}
function addJson(name, schema) {
	schemas.push({ name, origin: "json", schema });
}
// MCP tools: coding-agent/src/extensions/mcp/tools.ts toParameters()
function mcpParameters(schema) {
	return { ...schema, type: schema.type ?? "object", ...(schema.properties === undefined ? { properties: {} } : {}) };
}

// Real Pi built-in tools.
const defs = tools.createAllToolDefinitions(process.cwd());
for (const [key, def] of Object.entries(defs)) addTypeBox(`builtin_${key}`, def.parameters);

// TypeBox-built extension-style schemas.
addTypeBox("tb_union_num_str", Type.Object({ v: Type.Union([Type.Number(), Type.String()]), o: Type.Optional(Type.Union([Type.Boolean(), Type.Null()])) }));
addTypeBox("tb_literals", Type.Object({ mode: Type.Union([Type.Literal("fast"), Type.Literal("slow")]), n: Type.Literal(3), b: Type.Optional(Type.Literal(true)) }));
addTypeBox("tb_enum", Type.Object({ e: Type.Enum(["a", "b", 3]), s: StringEnum(["x", "y"]), k: Type.Optional(Type.KeyOf(Type.Object({ alpha: Type.String(), beta: Type.String() }))) }));
addTypeBox("tb_int", Type.Object({ i: Type.Integer({ minimum: 0, maximum: 10 }), m: Type.Optional(Type.Number({ exclusiveMinimum: 0, multipleOf: 0.5 })) }));
addTypeBox("tb_arrays", Type.Object({ nums: Type.Array(Type.Number()), bools: Type.Optional(Type.Array(Type.Boolean(), { minItems: 1, maxItems: 3 })), uniq: Type.Optional(Type.Array(Type.Any(), { uniqueItems: true })) }));
addTypeBox("tb_nested", Type.Object({ cfg: Type.Object({ depth: Type.Integer(), name: Type.Optional(Type.String()), flags: Type.Optional(Type.Object({ x: Type.Boolean() })) }), list: Type.Optional(Type.Array(Type.Object({ id: Type.Number(), tag: Type.Optional(Type.String()) }))) }));
addTypeBox("tb_tuple", Type.Object({ pair: Type.Tuple([Type.String(), Type.Number()]), opt: Type.Optional(Type.Tuple([Type.Boolean(), Type.Integer(), Type.Null()])) }));
addTypeBox("tb_record", Type.Object({ env: Type.Record(Type.String(), Type.String()), counts: Type.Optional(Type.Record(Type.String(), Type.Integer())) }));
addTypeBox("tb_strict", Type.Object({ a: Type.String(), b: Type.Optional(Type.Number()) }, { additionalProperties: false }));
addTypeBox("tb_strict_allreq", Type.Object({ a: Type.String(), b: Type.Number() }, { additionalProperties: false }));
addTypeBox("tb_addl_schema", Type.Object({ a: Type.String(), b: Type.Optional(Type.Boolean()) }, { additionalProperties: Type.Number() }));
addTypeBox("tb_intersect_nested", Type.Object({ both: Type.Intersect([Type.Object({ a: Type.String() }), Type.Object({ b: Type.Number() })]) }));
addTypeBox("tb_intersect_root", Type.Intersect([Type.Object({ a: Type.Number() }), Type.Object({ b: Type.Optional(Type.Boolean()) })]));
addTypeBox("tb_null", Type.Object({ n: Type.Null(), u: Type.Optional(Type.Union([Type.String(), Type.Null()])) }));
addTypeBox("tb_strings", Type.Object({ s: Type.String({ minLength: 2, maxLength: 5 }), p: Type.Optional(Type.String({ pattern: "^[a-z]+$" })), f: Type.Optional(Type.String({ format: "email" })), d: Type.Optional(Type.String({ format: "date-time" })), u: Type.Optional(Type.String({ format: "uuid" })) }));
addTypeBox("tb_any_unknown", Type.Object({ a: Type.Any(), u: Type.Optional(Type.Unknown()) }));
addTypeBox("tb_union_objects", Type.Object({ op: Type.Union([Type.Object({ kind: Type.Literal("add"), value: Type.Number() }), Type.Object({ kind: Type.Literal("remove"), id: Type.String() })]) }));
addTypeBox("tb_opt_array_union", Type.Object({ items: Type.Optional(Type.Union([Type.Array(Type.String()), Type.String()])) }));
addTypeBox("tb_keys", Type.Object({ 1: Type.Number(), 0: Type.String(), "a.b": Type.Optional(Type.Number()), "x+": Type.Optional(Type.Boolean()) }));
addTypeBox("tb_bool_num", Type.Object({ flag: Type.Boolean(), count: Type.Number() }));
addTypeBox("tb_root_union", Type.Union([Type.Object({ a: Type.String() }), Type.Object({ b: Type.Number() })]));
addTypeBox("tb_integer_array", Type.Object({ ids: Type.Array(Type.Integer()), grid: Type.Optional(Type.Array(Type.Array(Type.Number()))) }));
addTypeBox("tb_literal_num_union", Type.Object({ level: Type.Union([Type.Literal(1), Type.Literal(2), Type.Literal(3)]), on: Type.Optional(Type.Union([Type.Literal(true), Type.Literal("auto")])) }));
addTypeBox("tb_union_int_bool", Type.Object({ v: Type.Union([Type.Integer(), Type.Boolean()]), w: Type.Optional(Type.Union([Type.Null(), Type.Number()])) }));
addTypeBox("tb_unsafe_obj", Type.Object({ raw: Type.Unsafe({ type: "object", properties: { n: { type: "number" } }, required: ["n"] }), e: Type.Optional(StringEnum(["low", "high"])) }));
addTypeBox("tb_record_num_keys", Type.Object({ m: Type.Record(Type.Number(), Type.Boolean()) }));
addTypeBox("tb_array_of_unions", Type.Object({ vals: Type.Array(Type.Union([Type.Number(), Type.Boolean(), Type.Null()])) }));
addTypeBox("tb_optional_objects", Type.Object({ outer: Type.Optional(Type.Object({ inner: Type.Optional(Type.Object({ leaf: Type.Number() })), s: Type.String() })) }));
addTypeBox("tb_number_constraints", Type.Object({ pct: Type.Number({ minimum: 0, maximum: 100 }), step: Type.Optional(Type.Integer({ multipleOf: 5, exclusiveMaximum: 50 })) }));

// Plain JSON schemas (MCP / extension style).
addJson("json_mcp_basic", mcpParameters({ type: "object", properties: { query: { type: "string", description: "q" }, limit: { type: "integer", minimum: 1, maximum: 100 }, verbose: { type: "boolean" } }, required: ["query"] }));
addJson("json_mcp_no_type", mcpParameters({ properties: { x: { type: "number" }, y: { type: "string" } }, required: ["x"] }));
addJson("json_mcp_empty", mcpParameters({}));
addJson("json_type_arrays", { type: "object", properties: { a: { type: ["string", "null"] }, b: { type: ["number", "string"] }, c: { type: ["integer", "boolean"] }, d: { type: ["null", "number"] }, e: { type: ["array", "string"], items: { type: "number" } }, f: { type: ["boolean"] } } });
addJson("json_nullable_anyof", { type: "object", properties: { a: { anyOf: [{ type: "number" }, { type: "null" }] }, b: { oneOf: [{ type: "integer" }, { type: "string", enum: ["auto"] }] }, c: { anyOf: [{ type: "boolean" }, { type: "array", items: { type: "integer" } }] } }, required: ["a"] });
addJson("json_allof_root", { allOf: [{ type: "object", properties: { a: { type: "number" } }, required: ["a"] }, { type: "object", properties: { b: { type: "boolean" } } }] });
addJson("json_allof_typed", { type: "object", allOf: [{ properties: { a: { type: "integer" } }, required: ["a"] }, { properties: { a: { minimum: 2 }, s: { type: "string", minLength: 1 } } }] });
addJson("json_not", { type: "object", properties: { a: { not: { type: "string" } }, b: { type: "number", not: { const: 0 } }, c: { not: {} } } });
addJson("json_enum_const", { type: "object", properties: { color: { enum: ["red", "green", 1, null] }, ver: { const: 2 }, obj: { const: { x: [1, 2] } }, arr: { enum: [[1, 2], { a: 1 }] }, s: { type: "string", enum: ["on", "off"] } } });
addJson("json_numbers", { type: "object", properties: { n: { type: "number", minimum: 0, maximum: 1 }, x: { type: "number", exclusiveMinimum: 0, exclusiveMaximum: 10 }, m: { type: "number", multipleOf: 0.1 }, i: { type: "integer", multipleOf: 3 }, big: { type: "number", maximum: 1e21 }, tiny: { minimum: 1e-7 } } });
addJson("json_strings", { type: "object", properties: { s: { type: "string", minLength: 2, maxLength: 4 }, digits: { type: "string", pattern: "^\\d+$" }, word: { type: "string", pattern: "\\w+\\s\\w+" }, end: { type: "string", pattern: "end$" }, dot: { type: "string", pattern: "^.$" }, uni: { type: "string", pattern: "^\\p{Lu}" }, unanch: { pattern: "b" } } });
addJson("json_formats", { type: "object", properties: { date: { type: "string", format: "date" }, time: { type: "string", format: "time" }, dt: { type: "string", format: "date-time" }, ip4: { type: "string", format: "ipv4" }, ip6: { type: "string", format: "ipv6" }, uri: { type: "string", format: "uri" }, uriref: { type: "string", format: "uri-reference" }, email: { type: "string", format: "email" }, uuid: { type: "string", format: "uuid" }, jp: { type: "string", format: "json-pointer" }, rjp: { type: "string", format: "relative-json-pointer" }, dur: { type: "string", format: "duration" }, tpl: { type: "string", format: "uri-template" }, custom: { type: "string", format: "custom-thing" }, frag: { type: "string", format: "json-pointer-uri-fragment" } } });
addJson("json_arrays", { type: "object", properties: { list: { type: "array", items: { type: "number" }, minItems: 1, maxItems: 3 }, uniq: { type: "array", uniqueItems: true }, notuniq: { type: "array", uniqueItems: false }, has: { type: "array", contains: { type: "string" } }, few: { type: "array", contains: { type: "number" }, minContains: 2, maxContains: 3 }, zero: { type: "array", contains: { type: "number" }, minContains: 0 } } });
addJson("json_tuples", { type: "object", properties: { pre: { type: "array", prefixItems: [{ type: "string" }, { type: "number" }], items: { type: "boolean" } }, old: { type: "array", items: [{ type: "number" }, { type: "boolean" }], additionalItems: false }, oldext: { type: "array", items: [{ type: "string" }], additionalItems: { type: "integer" } }, none: { type: "array", items: false } } });
addJson("json_objects", { type: "object", properties: { a: { type: "string" }, b: { type: "number" }, c: {} }, additionalProperties: { type: "number" }, patternProperties: { "^x-": { type: "string" }, "^n_": { type: "integer" } }, propertyNames: { pattern: "^[a-z_-]+$", maxLength: 8 }, minProperties: 1, maxProperties: 4 });
addJson("json_dependencies", { type: "object", properties: { a: {}, b: {}, c: {}, x: {}, y: {}, z: {}, w: { type: "number" } }, dependentRequired: { a: ["b", "c"] }, dependencies: { x: ["y"], z: { required: ["w"] } } });
addJson("json_ref", { type: "object", $defs: { pos: { type: "integer", minimum: 0 }, node: { type: "object", properties: { name: { type: "string" }, child: { $ref: "#/$defs/node" } }, required: ["name"] } }, definitions: { flag: { type: "boolean" } }, properties: { p: { $ref: "#/$defs/pos" }, tree: { $ref: "#/$defs/node" }, opt: { $ref: "#/$defs/pos" }, f: { $ref: "#/definitions/flag" }, bad: { $ref: "#/$defs/missing" }, self: { type: "object", properties: { again: { $ref: "#" } } } }, required: ["p"] });
addJson("json_if", { type: "object", properties: { kind: { type: "string" }, n: {} }, if: { properties: { kind: { const: "num" } } }, then: { properties: { n: { type: "number" } }, required: ["n"] }, else: { properties: { n: { type: "string" } } } });
addJson("json_unevaluated", { type: "object", properties: { a: { type: "string" } }, allOf: [{ properties: { b: { type: "number" } } }], unevaluatedProperties: false });
addJson("json_unevaluated_items", { type: "object", properties: { t: { type: "array", prefixItems: [{ type: "string" }], unevaluatedItems: { type: "number" } } } });
addJson("json_bool_schemas", { type: "object", properties: { t: true, f: false, n: { type: "number" } }, additionalProperties: true });
addJson("json_required_only", { type: "object", required: ["a", "b"] });
addJson("json_proto_names", { type: "object", properties: { toString: { type: "string" }, constructor: {}, hasOwnProperty: { type: "number" }, normal: { type: "boolean" } }, required: ["valueOf"] });
addJson("json_nested_deep", { type: "object", properties: { level1: { type: "object", properties: { level2: { type: "array", items: { type: "object", properties: { id: { type: "integer" }, tags: { type: "array", items: { type: "string" } }, meta: { type: "object", additionalProperties: false, properties: { ok: { type: "boolean" } } } }, required: ["id"] } } }, required: ["level2"] } }, required: ["level1"] });
addJson("json_many_errors", { type: "object", properties: Object.fromEntries("abcdefghijkl".split("").map((k) => [k, { type: "integer" }])), required: "abcdefghijkl".split("") });
addJson("json_keys_order", { type: "object", properties: { 10: { type: "number" }, 2: { type: "string" }, b: { type: "boolean" }, a: { type: "integer" }, 1: { type: "null" } }, required: ["b", "10", "a"] });
addJson("json_weird_keys", { type: "object", properties: { "a/b": { type: "number" }, "": { type: "string" }, "a.b": { type: "boolean" }, "x y": { type: "integer" }, "~0": { type: "number" } }, required: ["a/b"] });
addJson("json_items_tuple_coerce", { type: "object", properties: { list: { type: "array", items: [{ type: "number" }, { type: "boolean" }, { type: "null" }] } } });
addJson("json_root_anyof", { anyOf: [{ type: "object", properties: { a: { type: "number" } }, required: ["a"] }, { type: "object", properties: { b: { type: "string" } }, required: ["b"] }] });
addJson("json_root_oneof", { oneOf: [{ type: "object", properties: { kind: { const: "a" }, n: { type: "integer" } }, required: ["kind", "n"] }, { type: "object", properties: { kind: { const: "b" }, s: { type: "string" } }, required: ["kind"] }] });
addJson("json_root_string", { type: "string" });
addJson("json_root_number", { type: "number", minimum: 3 });
addJson("json_root_nullable_obj", { type: ["object", "null"], properties: { a: { type: "number" } }, required: ["a"] });
addJson("json_multi_type", { type: "object", properties: { v: { type: ["number", "boolean"] }, w: { type: ["boolean", "number"] }, s: { type: ["string", "number"] }, n: { type: ["null", "string"] }, i: { type: ["integer", "string"] }, o: { type: ["object", "string"], properties: { q: { type: "number" } } } } });
addJson("json_oneof_objects", { type: "object", properties: { shape: { oneOf: [{ type: "object", properties: { r: { type: "number" } }, required: ["r"], additionalProperties: false }, { type: "object", properties: { w: { type: "number" }, h: { type: "number" } }, required: ["w", "h"], additionalProperties: false }] } }, required: ["shape"] });
addJson("json_nullable_keyword", { type: "object", properties: { a: { type: "string", nullable: true }, b: { type: "integer", nullable: true } }, required: ["a"] });
addJson("json_additional_false_nested", { type: "object", properties: { opts: { type: "object", properties: { x: { type: "number" }, y: { type: "number" } }, required: ["x", "y"], additionalProperties: false }, more: { type: "object", properties: { x: { type: "number" } }, required: ["y"], additionalProperties: false } } });
addJson("json_additional_false_root", { type: "object", properties: { cmd: { type: "string" }, n: { type: "number" } }, required: ["cmd"], additionalProperties: false });
addJson("json_anyof_coerce", { type: "object", properties: { v: { anyOf: [{ type: "integer" }, { type: "boolean" }] }, w: { oneOf: [{ type: "string", maxLength: 3 }, { type: "number" }] }, x: { anyOf: [{ type: "array", items: { type: "number" } }, { type: "string" }] } } });
addJson("json_null_type", { type: "object", properties: { z: { type: "null" }, zz: { type: ["null"] } } });
addJson("json_additional_coerce", { type: "object", properties: { fixed: { type: "string" } }, additionalProperties: { type: "integer" } });
addJson("json_string_lengths_unicode", { type: "object", properties: { g: { type: "string", maxLength: 2 }, h: { type: "string", minLength: 3 } } });
addJson("json_nested_union_in_array", { type: "object", properties: { rows: { type: "array", items: { type: "object", properties: { v: { type: ["number", "null"] }, k: { anyOf: [{ type: "string" }, { type: "number" }] } }, required: ["k"] } } } });
addJson("json_root_anyof_array", { anyOf: [{ type: "array", items: { type: "string" } }, { type: "object", properties: { n: { type: "number" } } }] });
addJson("json_self_ref", { $ref: "#" });
addJson("json_anchor_ref", { type: "object", $defs: { pos: { $anchor: "pos", type: "integer", minimum: 0 } }, properties: { p: { $ref: "#pos" }, q: { $ref: "other.json#/$defs/pos" }, r: { $ref: "other.json" }, s: { $ref: "x#" } } });
addJson("json_codemode_like", { type: "object", properties: { code: { type: "string", description: "JS" }, timeoutMs: { type: "integer", minimum: 1 }, dryRun: { type: "boolean", default: false } }, required: ["code"], additionalProperties: false });

// Legacy (TypeBox 0.x style): a plain schema carrying Symbol.for("TypeBox.Kind") -> coerceWithJsonSchema skipped.
function addLegacy(name, schema) {
	const s = structuredClone(schema);
	Object.defineProperty(s, Symbol.for("TypeBox.Kind"), { value: "Object", enumerable: false });
	schemas.push({ name, origin: "legacy", schema: s });
}
addLegacy("legacy_basic", { type: "object", properties: { n: { type: "number" }, b: { type: "boolean" }, s: { type: "string" } }, required: ["n"] });
addLegacy("legacy_nested", { type: "object", properties: { o: { type: "object", properties: { i: { type: "integer" } } }, a: { type: "array", items: { type: "number" } } } });

// ---------------------------------------------------------------------------------------------
// Value pools
// ---------------------------------------------------------------------------------------------
const POOL = [
	null, true, false, 0, 1, -1, 2, 3, 1.5, -2.5, 100, 1e21, 1e-7, 123456789.123, 9007199254740993, 0.1, 5e-324, 1.7976931348623157e308,
	"", " ", "0", "1", "-1", "1.5", " 42 ", "\t7\n", "1e3", "0x1F", "0b101", "0o17", "-0x10", "Infinity", "-Infinity", "NaN",
	"abc", "true", "false", "TRUE", "False", "null", "NULL", "undefined", "123n", "-45n", "12.50", ".5", "5.", "+5", "1_000", "\uFF11\uFF12",
	"\u00A0 3 \u2028", "x".repeat(70), "e\u0301", "\uD83D\uDC4D\uD83C\uDFFD", "\uD83C\uDDFA\uD83C\uDDF8\uD83C\uDDEC\uD83C\uDDE7", "a\u200Db", "\ud800", "lo\udc00ne",
	'line\nbreak "quoted" \\ back \u0001 \u001f \u007f \u2028 \u2029 </script>', "add", "remove", "fast", "slow", "auto", "red", "x", "a", "3", "-0", "0.0", "1e400",
	"2024-01-02", "2024-02-30", "2024-01-02T03:04:05Z", "2024-01-02t03:04:05.123+05:30", "23:59:60Z", "12:00:00", "192.168.0.1", "256.1.1.1", "::1", "fe80::1%eth0",
	"https://example.com/a?b=c#d", "not a uri", "/relative/path", "mailto:x@y.z", "user@example.com", "bad@", "123e4567-e89b-12d3-a456-426614174000", "/a~1b/~0c", "0#", "1/a",
	"P1Y2M3DT4H5M6S", "PT", "http://x/{id}", "#/a/b", "abc def", "ABC", "a_b-c", "toolong-name", "9", "src/index.ts", "*.ts",
	[], [1], [1, 2], ["a", "b"], [1, "2", true, null], [[1]], [1, 1], [{ a: 1 }, { a: 1 }], [{ a: 1, b: 2 }, { b: 2, a: 1 }], ["1", "2"], ["true", "0"], [0, -1],
	{}, { a: 1 }, { oldText: "x", newText: "y" }, { 0: "z", b: 2, 1: "y" }, { kind: "add", value: "5" }, { kind: "remove", id: 7 }, { r: "2" }, { w: 1, h: 2 },
	{ name: "n", child: { name: 5 } }, { x: "1", y: true }, { depth: "3" }, { nested: { deep: [1, { e: null }] } },
];
const ROOT_ARGS = [null, "string", 42, true, [], [1, 2], {}, { extra: 1 }, [{ a: 1 }], "null", 0, false];

// ---------------------------------------------------------------------------------------------
// Sampler: produce plausible values for a (JSON-ish) schema
// ---------------------------------------------------------------------------------------------
function resolveRef(root, ref) {
	if (!ref.startsWith("#")) return undefined;
	if (ref === "#") return root;
	let cur = root;
	for (const part of ref.slice(2).split("/")) {
		if (cur == null || typeof cur !== "object") return undefined;
		cur = cur[part.replace(/~1/g, "/").replace(/~0/g, "~")];
	}
	return cur;
}
function sampleString(schema) {
	if (schema.format === "date-time") return pick(["2024-01-02T03:04:05Z", "2024-13-01T00:00:00Z"]);
	if (schema.format === "email") return pick(["a@b.co", "nope"]);
	if (schema.format === "uuid") return "123e4567-e89b-12d3-a456-426614174000";
	if (schema.format) return pick(POOL.filter((v) => typeof v === "string"));
	if (schema.pattern) return pick(["abc", "123", "x y", "trend", "ok", "Q"]);
	const len = schema.minLength ?? 0;
	return pick(["hello", "src/main.ts", "ls -la", "x".repeat(Math.max(len, 1)), "README.md", "foo bar"]);
}
function sample(schema, root, depth = 0) {
	if (schema === true || schema === undefined) return pick(POOL);
	if (schema === false) return pick(POOL);
	if (depth > 4) return null;
	if (typeof schema.$ref === "string") {
		const t = resolveRef(root, schema.$ref);
		return t === undefined ? pick(POOL) : sample(t, root, depth + 1);
	}
	if ("const" in schema) return structuredClone(schema.const);
	if (Array.isArray(schema.enum)) return structuredClone(pick(schema.enum));
	if (Array.isArray(schema.anyOf)) return sample(pick(schema.anyOf), root, depth + 1);
	if (Array.isArray(schema.oneOf)) return sample(pick(schema.oneOf), root, depth + 1);
	if (Array.isArray(schema.allOf)) {
		const out = {};
		for (const s of schema.allOf) {
			const v = sample(s, root, depth + 1);
			if (v && typeof v === "object" && !Array.isArray(v)) Object.assign(out, v);
		}
		if (schema.properties) Object.assign(out, sample({ ...schema, allOf: undefined, type: "object" }, root, depth + 1));
		return out;
	}
	let type = schema.type;
	if (Array.isArray(type)) type = pick(type);
	if (type === undefined) {
		if (schema.properties || schema.patternProperties) type = "object";
		else if (schema.items || schema.prefixItems) type = "array";
		else return pick(POOL);
	}
	switch (type) {
		case "object": {
			const o = {};
			const props = schema.properties ?? {};
			const req = new Set(Array.isArray(schema.required) ? schema.required : []);
			for (const [k, s] of Object.entries(props)) {
				if (req.has(k) || chance(0.6)) o[k] = sample(s, root, depth + 1);
			}
			if (schema.patternProperties) {
				for (const p of Object.keys(schema.patternProperties)) {
					if (p === "^x-" && chance(0.5)) o["x-trace"] = pick(["id", 5]);
					if (p === "^n_" && chance(0.5)) o.n_count = pick([1, "2", 2.5]);
					if (p === "^.*$" && chance(0.8)) o[pick(["HOME", "PATH", "k1"])] = pick(["v", 1, true, null, "2"]);
					if (p.startsWith("^-?") && chance(0.8)) o[pick(["1", "2.5", "-3", "x"])] = pick([true, "true", 0, "no"]);
				}
			}
			return o;
		}
		case "array": {
			if (Array.isArray(schema.prefixItems)) return schema.prefixItems.map((s) => sample(s, root, depth + 1));
			if (Array.isArray(schema.items)) return schema.items.map((s) => sample(s, root, depth + 1));
			const n = Math.floor(rnd() * 4);
			const out = [];
			for (let i = 0; i < n; i++) out.push(schema.items !== undefined ? sample(schema.items, root, depth + 1) : pick(POOL));
			return out;
		}
		case "string":
			return sampleString(schema);
		case "integer":
			return pick([0, 1, 2, 5, 7, 10, 42, -3]);
		case "number":
			return pick([0, 0.5, 1, 2.25, 10, 99.9, -1]);
		case "boolean":
			return chance(0.5);
		case "null":
			return null;
		default:
			return pick(POOL);
	}
}

// ---------------------------------------------------------------------------------------------
// Case generation
// ---------------------------------------------------------------------------------------------
function runCase(entry, args) {
	const argsJson = JSON.stringify(args);
	if (argsJson === undefined) return undefined;
	const parsed = JSON.parse(argsJson);
	const tool = { name: entry.name, description: "", parameters: entry.schema };
	try {
		const result = validateToolArguments(tool, { type: "toolCall", id: "call_1", name: entry.name, arguments: parsed });
		const resultJson = JSON.stringify(result);
		return { argsJson, expected: { ok: true, resultJson: resultJson === undefined ? null : resultJson } };
	} catch (e) {
		return { argsJson, expected: { ok: false, error: String(e?.message ?? e), errorType: e?.constructor?.name ?? "unknown" } };
	}
}

const cases = [];
const seen = new Set();
function emit(entry, args, tag) {
	const r = runCase(entry, args);
	if (!r) return;
	const key = `${entry.name}\u0000${r.argsJson}`;
	if (seen.has(key)) return;
	seen.add(key);
	cases.push({ schema: entry.name, tag, argsJson: r.argsJson, expected: r.expected });
}

function propertiesOf(schema) {
	if (schema && typeof schema === "object" && schema.properties && typeof schema.properties === "object") return Object.entries(schema.properties);
	if (schema && Array.isArray(schema.allOf)) return schema.allOf.flatMap((s) => propertiesOf(s));
	if (schema && Array.isArray(schema.anyOf)) return schema.anyOf.flatMap((s) => propertiesOf(s));
	if (schema && Array.isArray(schema.oneOf)) return schema.oneOf.flatMap((s) => propertiesOf(s));
	return [];
}

// Hand-picked cases that must always be present (goldens + corpus).
const byName = Object.fromEntries(schemas.map((s) => [s.name, s]));
const handPicked = [
	["builtin_read", { path: "a.txt", offset: "10", limit: "20" }],
	["builtin_read", { path: "a.txt", offset: null, limit: null }],
	["builtin_read", { path: 123 }],
	["builtin_read", {}],
	["builtin_bash", { command: "ls", timeout: "30" }],
	["builtin_bash", { command: ["ls"], timeout: true }],
	["builtin_edit", { path: "f", edits: [{ oldText: "a", newText: "b" }] }],
	["builtin_edit", { path: "f", edits: { oldText: "a", newText: "b" } }],
	["builtin_edit", { path: "f", edits: [{ oldText: 1, newText: null }, { oldText: "a" }] }],
	["builtin_edit", { path: "f", edits: "[{\"oldText\":\"a\",\"newText\":\"b\"}]" }],
	["builtin_grep", { pattern: "x", ignoreCase: "true", literal: "false", context: "2", limit: "1e2" }],
	["builtin_grep", { pattern: "x", ignoreCase: "yes", literal: 1, context: "two" }],
	["builtin_ls", { path: null, limit: null }],
	["builtin_ls", { limit: "Infinity" }],
	["builtin_write", { path: "p", content: { text: "x" } }],
	["builtin_find", { pattern: "*.ts", limit: 1e21 }],
	["json_many_errors", {}],
	["json_many_errors", Object.fromEntries("abcdefghijkl".split("").map((k) => [k, "x" + k]))],
	["json_root_string", null],
	["json_root_string", 5],
	["json_root_anyof", { a: "5" }],
	["json_root_anyof", { b: 5 }],
	["json_proto_names", {}],
	["json_proto_names", { toString: 5, normal: "true" }],
	["json_keys_order", { a: "1", b: "false", 10: "7", 2: 3, 1: "" }],
	["json_weird_keys", { "a/b": "x", "": 1, "a.b": "true", "x y": "2.5", "~0": "1" }],
	["json_formats", { date: "2024-02-29", time: "23:59:60Z", dt: "2023-02-29T00:00:00Z", ip4: "1.2.3", ip6: "1::2::3", uri: "http://a b", uriref: "//host/path", email: "a@b", uuid: "xyz", jp: "a/b", rjp: "1#", dur: "P1W", tpl: "{x", custom: 5, frag: "#/x%2" }],
	["json_strings", { s: "\uD83D\uDC4D\uD83C\uDFFD\uD83D\uDC4D", digits: "\u0661\u0662", word: "hello world", end: "the end\n", dot: "\uD83D\uDE00", uni: "\u00C9t\u00E9" }],
	["json_unevaluated", { a: "x", b: 1, c: 2 }],
	["json_unevaluated", { a: 1, b: "x" }],
	["json_dependencies", { a: 1, x: 1, z: 1 }],
	["json_objects", { a: "1", b: "2", "x-id": 5, n_count: "3", UPPER: 1, toolongpropertyname: 2 }],
	["json_ref", { p: "5", tree: { name: "a", child: { name: 1 } }, opt: null, f: "true", bad: 1, self: { again: {} } }],
	["json_additional_false_nested", { opts: { x: 1, y: 2 }, more: { y: 1 } }],
	["json_nested_deep", { level1: { level2: [{ id: "1", tags: [1, null], meta: { ok: "true", extra: 1 } }, { tags: "x" }] } }],
	["tb_root_union", { a: 5 }],
	["tb_intersect_root", { a: "5", b: "true" }],
	["tb_tuple", { pair: [1, "2"], opt: ["true", "3.7", "null"] }],
	["tb_arrays", { nums: "5", bools: "true", uniq: [1, 1] }],
	["tb_record", { env: { HOME: 1, PATH: null }, counts: { a: "2.9", b: "x" } }],
	["tb_keys", { 0: 5, 1: "7", "a.b": "3", axb: "4", "x+": "true", xx: "1" }],
	["tb_enum", { e: "3", s: 1, k: "alpha" }],
	["tb_literals", { mode: "FAST", n: "3", b: "true" }],
	["tb_union_objects", { op: { kind: "add", value: "5" } }],
	["json_root_anyof_array", [1, true]],
	["json_root_anyof_array", ["a"]],
	["json_root_anyof_array", { n: "4" }],
	["json_self_ref", {}],
	["json_anchor_ref", { p: "3", q: -1, r: "x", s: { p: 1 } }],
	["json_anchor_ref", { p: -2, q: "1" }],
	["legacy_basic", { n: "5", b: "true", s: 1 }],
	["legacy_nested", { o: { i: "2" }, a: ["1"] }],
];
for (const [name, args] of handPicked) emit(byName[name], args, "hand");

// ---------------------------------------------------------------------------------------------
// Fuzzed schemas (full corpus only)
// ---------------------------------------------------------------------------------------------
const SAFE_PATTERNS = ["^[a-z]+$", "^\\d{2,3}$", "\\s", "^(foo|bar)", "[A-Z]", "^\\w+@\\w+\\.com$", "^.{2}$", "^\\p{L}+$", "x*y", "\\bword\\b", "^[^0-9]*$", "(?<year>\\d{4})", "^a(?=b)", "(?<!x)y", "^\\$\\d", "[\\-+]", "^[\\s\\S]{0,3}$", "^\\S+$", "\\.ts$", "^$"];
const FORMATS = ["email", "uuid", "date", "date-time", "time", "ipv4", "ipv6", "uri", "uri-reference", "json-pointer", "duration", "unknown-format", "regex", "relative-json-pointer", "uri-template"];
const NUMS = [0, 1, 2.5, -1, 10, 0.1, 1e21, 3, 0.5, 100];
const KEYS = ["a", "b", "name", "count", "flag", "10", "2", "x-y", "path", "opt", "c", "list", "a", "b", "name", "count", "toString", "constructor", "valueOf", "a.b", "0"];
const TYPES = ["string", "number", "integer", "boolean", "null", "object", "array"];

function maybe(p, obj) {
	return chance(p) ? obj : {};
}
function fuzzLeaf() {
	switch (Math.floor(rnd() * 9)) {
		case 0:
			return { type: "string", ...maybe(0.3, { minLength: pick([0, 1, 2, 3]) }), ...maybe(0.3, { maxLength: pick([1, 2, 4, 10]) }), ...maybe(0.25, { pattern: pick(SAFE_PATTERNS) }), ...maybe(0.2, { format: pick(FORMATS) }) };
		case 1:
		case 2:
			return { type: pick(["number", "integer"]), ...maybe(0.3, { minimum: pick(NUMS) }), ...maybe(0.3, { maximum: pick(NUMS) }), ...maybe(0.15, { exclusiveMinimum: pick(NUMS) }), ...maybe(0.15, { exclusiveMaximum: pick(NUMS) }), ...maybe(0.2, { multipleOf: pick([0.5, 2, 3, 0.1, 0.01]) }) };
		case 3:
			return { type: "boolean" };
		case 4:
			return { type: "null" };
		case 5: {
			const a = pick(TYPES);
			let b = pick(TYPES);
			if (b === a) b = "null";
			return { type: chance(0.2) ? [a] : [a, b] };
		}
		case 6:
			return { enum: [pick(["x", "y", 1, 2, true, null, [1], { k: 1 }]), pick(["z", 3, false, "1"])] };
		case 7:
			return { const: pick(["c", 0, 1, false, null, { a: [1] }, [1, 2]]) };
		default:
			return { type: "string", enum: [pick(["on", "off", "1"]), pick(["auto", "true", ""])] };
	}
}
function fuzzObject(depth) {
	const o = {};
	if (chance(0.8)) o.type = "object";
	const props = {};
	const n = 1 + Math.floor(rnd() * 4);
	for (let i = 0; i < n; i++) props[pick(KEYS)] = fuzzJson(depth + 1);
	o.properties = props;
	const keys = Object.keys(props);
	if (chance(0.7)) o.required = keys.filter(() => chance(0.5));
	if (chance(0.1)) (o.required ??= []).push(pick(["zz", "toString"]));
	if (chance(0.25)) o.additionalProperties = pick([false, true, fuzzLeaf()]);
	if (chance(0.12)) o.patternProperties = { [pick(["^x-", "^[0-9]+$", "_id$"])]: fuzzLeaf() };
	if (chance(0.08)) o.propertyNames = pick([{ maxLength: 4 }, { pattern: "^[a-z0-9]+$" }]);
	if (chance(0.08)) o.minProperties = pick([1, 2]);
	if (chance(0.08)) o.maxProperties = pick([1, 2, 3]);
	if (chance(0.06) && keys.length > 1) o.dependentRequired = { [keys[0]]: [keys[1]] };
	return o;
}
function fuzzArray(depth) {
	const o = { type: "array" };
	const r = rnd();
	if (r < 0.6) o.items = fuzzJson(depth + 1);
	else if (r < 0.75) o.prefixItems = [fuzzLeaf(), fuzzLeaf()];
	else if (r < 0.9) {
		o.items = [fuzzLeaf(), fuzzLeaf()];
		if (chance(0.6)) o.additionalItems = pick([false, fuzzLeaf()]);
	}
	if (chance(0.2)) o.minItems = pick([1, 2]);
	if (chance(0.2)) o.maxItems = pick([1, 2, 3]);
	if (chance(0.15)) o.uniqueItems = pick([true, false]);
	if (chance(0.1)) {
		o.contains = fuzzLeaf();
		if (chance(0.5)) o.minContains = pick([0, 1, 2]);
		if (chance(0.5)) o.maxContains = pick([1, 2]);
	}
	return o;
}
function fuzzJson(depth) {
	const r = rnd();
	if (depth > 2 || r < 0.35) return fuzzLeaf();
	if (r < 0.55) return fuzzObject(depth + 1);
	if (r < 0.68) return fuzzArray(depth + 1);
	if (r < 0.8) {
		const subs = [fuzzJson(depth + 1), fuzzJson(depth + 1)];
		if (chance(0.3)) subs.push(fuzzJson(depth + 1));
		return { [pick(["anyOf", "oneOf", "allOf"])]: subs };
	}
	if (r < 0.85) return { not: fuzzLeaf() };
	if (r < 0.9) return { if: { type: pick(["string", "number"]) }, then: fuzzLeaf(), ...maybe(0.6, { else: fuzzLeaf() }) };
	if (r < 0.94) return { $ref: pick(["#/$defs/a", "#/$defs/b", "#/definitions/c", "#/$defs/missing"]) };
	if (r < 0.97) return pick([true, false, {}]);
	return { ...fuzzLeaf(), ...maybe(0.5, { nullable: true }) };
}
function fuzzRootJson() {
	const root = fuzzObject(0);
	root.$defs = { a: fuzzLeaf(), b: fuzzObject(2) };
	root.definitions = { c: fuzzLeaf() };
	if (chance(0.15)) root.unevaluatedProperties = pick([false, fuzzLeaf()]);
	return chance(0.85) ? root : mcpParameters({ properties: root.properties, required: root.required });
}

function tbLeaf() {
	switch (Math.floor(rnd() * 12)) {
		case 0:
			return Type.String(pick([{}, { minLength: 1 }, { maxLength: 3 }, { pattern: "^[a-z]+$" }, { format: "email" }]));
		case 1:
			return Type.Number(pick([{}, { minimum: 0 }, { maximum: 10 }, { multipleOf: 0.5 }]));
		case 2:
			return Type.Integer(pick([{}, { maximum: 5 }, { minimum: 1 }]));
		case 3:
			return Type.Boolean();
		case 4:
			return Type.Null();
		case 5:
			return Type.Literal(pick(["a", 1, true, "x y", 0, "1"]));
		case 6:
			return StringEnum(["p", "q"]);
		case 7:
			return Type.Enum(["e1", 2]);
		case 8:
			return pick([Type.Any(), Type.Unknown()]);
		case 9:
			return Type.Union([tbLeafSimple(), tbLeafSimple()]);
		case 10:
			return Type.Array(tbLeafSimple(), pick([{}, { minItems: 1 }, { maxItems: 2 }, { uniqueItems: true }]));
		default:
			return Type.Union([tbLeafSimple(), Type.Null()]);
	}
}
function tbLeafSimple() {
	return pick([Type.String(), Type.Number(), Type.Integer(), Type.Boolean(), Type.Null(), Type.Literal("k"), Type.Literal(2)]);
}
function tbObject(depth) {
	const props = {};
	const n = 1 + Math.floor(rnd() * 4);
	for (let i = 0; i < n; i++) {
		const t = tbType(depth + 1);
		props[pick(KEYS)] = chance(0.4) ? Type.Optional(t) : t;
	}
	return Type.Object(props, chance(0.15) ? { additionalProperties: false } : chance(0.1) ? { additionalProperties: tbLeafSimple() } : {});
}
function tbType(depth) {
	const r = rnd();
	if (depth > 2 || r < 0.5) return tbLeaf();
	if (r < 0.65) return tbObject(depth + 1);
	if (r < 0.75) return Type.Array(tbType(depth + 1));
	if (r < 0.82) return Type.Tuple([tbLeafSimple(), tbLeafSimple()]);
	if (r < 0.88) return Type.Record(Type.String(), tbLeafSimple());
	if (r < 0.94) return Type.Union([tbType(depth + 1), tbType(depth + 1)]);
	return Type.Intersect([tbObject(depth + 1), tbObject(depth + 1)]);
}

const fuzzArg = argv.find((a) => a.startsWith("--fuzz="));
const fuzzN = fuzzArg ? Number(fuzzArg.slice(7)) : 300;
const fuzzSchemaCount = full ? { json: fuzzN, tb: Math.ceil(fuzzN / 2) } : { json: 0, tb: 0 };
for (let i = 0; i < fuzzSchemaCount.json; i++) addJson(`fuzz_json_${i}`, fuzzRootJson());
for (let i = 0; i < fuzzSchemaCount.tb; i++) addTypeBox(`fuzz_tb_${i}`, tbObject(0));

// ---------------------------------------------------------------------------------------------
// Systematic + random cases per schema
// ---------------------------------------------------------------------------------------------
for (const entry of schemas) {
	const fuzz = entry.name.startsWith("fuzz_");
	const poolStride = full ? (fuzz ? 6 : 1) : 9;
	const randomPerSchema = full ? (fuzz ? 25 : 60) : 3;
	const root = entry.schema;
	// 1. root-level oddities
	for (const v of ROOT_ARGS) if ((full && !fuzz) || chance(0.3)) emit(entry, v, "root");
	// 2. valid-ish samples
	for (let i = 0; i < (full ? 5 : 2); i++) emit(entry, sample(root, root), "sample");
	const props = propertiesOf(root);
	const base = sample(root, root);
	const baseObj = base && typeof base === "object" && !Array.isArray(base) ? base : {};
	// 3. systematic: each property set to (a stride of) each pool value
	for (const [key] of props) {
		for (let i = Math.floor(rnd() * poolStride); i < POOL.length; i += poolStride) {
			emit(entry, { ...structuredClone(baseObj), [key]: structuredClone(POOL[i]) }, "prop-pool");
		}
		emit(entry, { ...structuredClone(baseObj), [key]: null }, "prop-null");
		const without = structuredClone(baseObj);
		delete without[key];
		emit(entry, without, "prop-missing");
	}
	// 4. extra properties (incl. integer-like keys for key ordering)
	emit(entry, { ...structuredClone(baseObj), extra: "x", 10: 1, 2: "two" }, "extra");
	emit(entry, { 5: null, ...structuredClone(baseObj), zeta: [1, "2"] }, "extra");
	// 5. random combinations
	for (let i = 0; i < randomPerSchema; i++) {
		const args = structuredClone(sample(root, root));
		if (args && typeof args === "object" && !Array.isArray(args)) {
			for (const [key, sub] of props) {
				const r = rnd();
				if (r < 0.25) args[key] = structuredClone(pick(POOL));
				else if (r < 0.35) delete args[key];
				else if (r < 0.45) args[key] = null;
				else if (r < 0.6) args[key] = sample(sub, root);
			}
			if (chance(0.2)) args[pick(["junk", "9", "Extra", "a.b", "toString", "constructor", "hasOwnProperty"])] = structuredClone(pick(POOL));
		}
		emit(entry, args, "random");
	}
}

// ---------------------------------------------------------------------------------------------
// Golden selection: hand-picked cases + a few behaviourally distinct cases per schema
// ---------------------------------------------------------------------------------------------
function signature(c) {
	if (c.expected.ok) return `ok:${c.expected.resultJson === c.argsJson ? "same" : "changed"}`;
	const kinds = c.expected.error
		.split("\n")
		.filter((l) => l.startsWith("  - "))
		.map((l) => l.replace(/^ {2}- [^:]*: /, "").replace(/[0-9]+(\.[0-9]+)?/g, "N").replace(/properties .*/, "properties"));
	return `err:${[...new Set(kinds)].sort().join("|")}`;
}
let selected = cases;
if (!full) {
	const perSchema = new Map();
	const sigs = new Set();
	selected = [];
	for (const c of cases) {
		if (c.tag === "hand") {
			selected.push(c);
			continue;
		}
		const sig = `${c.schema}\u0000${signature(c)}`;
		const count = perSchema.get(c.schema) ?? 0;
		if (sigs.has(sig) || count >= 4) continue;
		sigs.add(sig);
		perSchema.set(c.schema, count + 1);
		selected.push(c);
	}
}

// ---------------------------------------------------------------------------------------------
// Output
// ---------------------------------------------------------------------------------------------
const usedSchemas = new Set(selected.map((c) => c.schema));
const out = {
	description:
		"Differential fixtures for PiSharp ToolArgumentValidation, recorded from the real upstream validateToolArguments " +
		"(@earendil-works/pi-ai dist/utils/validation.js). schemas[name].typeBox: built with TypeBox 1.x (hidden '~kind' " +
		"metadata serialized as visible keys); origin 'legacy': schema carried Symbol.for('TypeBox.Kind'). " +
		"cases[].argsJson: JSON.stringify(arguments); expected.resultJson: JSON.stringify(result) or expected.error: Error.message. " +
		"Regenerate: node gen-tool-validation-goldens.mjs <node_modules> [out.json] [--full]",
	piAi: piAiVersion,
	typebox: typeboxVersion,
	schemas: Object.fromEntries(
		schemas
			.filter((s) => usedSchemas.has(s.name))
			.map((s) => [s.name, { origin: s.origin, typeBox: s.origin === "typebox", schema: serializeSchema(s.schema) }]),
	),
	cases: selected.map((c) => ({ schema: c.schema, tag: c.tag, argsJson: c.argsJson, expected: c.expected })),
};
fs.writeFileSync(outFile, `${JSON.stringify(out, null, full ? undefined : 1)}\n`);
const failures = selected.filter((c) => !c.expected.ok).length;
console.log(`wrote ${outFile}: ${selected.length} cases (${failures} validation failures), ${Object.keys(out.schemas).length} schemas`);
