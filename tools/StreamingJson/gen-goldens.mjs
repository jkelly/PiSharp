// Generates tests/PiSharp.ProviderSync.Tests/StreamingJson/parse-streaming-json-goldens.json: what Pi 1.1.0's parseStreamingJson
// (packages/ai/src/utils/json-parse.ts, over partial-json 0.1.7) returns for a deterministic corpus of tool-call argument texts, as
// JSON.stringify of the result, plus repairJson's output. Every provider finalizes tool-call arguments with this function.
// Usage: node tools/StreamingJson/gen-goldens.mjs <node_modules dir holding @earendil-works/pi-ai 1.1.0> <out.json> [seed] [count]
import { writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { join } from "node:path";

const modules = process.argv[2];
const out = process.argv[3];
let seed = Number(process.argv[4] ?? 1234);
const count = Number(process.argv[5] ?? 4000);
const { parseStreamingJson, repairJson } = await import(pathToFileURL(join(modules, "@earendil-works/pi-ai/dist/utils/json-parse.js")).href);

const deep = (n, open, close, inner = "1") => open.repeat(n) + inner + close.repeat(n);
const fixed = [
	// empty and whitespace
	"", " ", "\n\t", "\u00a0", "\ufeff{}", "\u00a0{\"a\":1}\u2028", "\u000b5",
	// complete values of every kind
	'{"path":"a.txt","limit":10}', "[1,2,3]", '"just a string"', "42", "-0", "1e400", "-1e400", "true", "false", "null", "{}", "[]",
	'{"a":1,"a":2}', '{"b":1,"a":2,"b":3}', '{"b":1,"2":2,"1":3,"a":4,"4294967294":5,"4294967295":6,"01":7}', '{"__proto__":{"x":1}}',
	'{"x":1.0,"y":1.50,"z":12345678901234567890,"w":1e-7,"v":123e-20,"u":1e21,"t":0.000001,"s":-0.0}',
	"[5e-324,2.5e-324,1.7976931348623157e308,1.7976931348623158e308,0.1,0.3333333333333333,123456789012345680000,1e21,9.999999999999999e20]",
	"[1e-6,1e-7,9007199254740993,100000000000000000000000,0.000001234,-1.5e-10,4.35,1.005,2e-7,123456789.123456789,1E+2,1e-0]",
	"[0.1e1,10e-1,1.25e+3,-0e5,5E-1,18014398509481985,9.5367431640625e-7,4.940656458412465e-324,2.2250738585072014e-308]",
	'{"lone":"\\ud800","pair":"\\ud83d\\ude00","low":"\\udc00x","rev":"\\ude00\\ud83d"}', '"\\u00e9\\u4e2d"',
	'{"\\ud800":1}', '{"a":{"\\udc00x":1}}', '["\\ud800"]',
	// truncated
	"{", "[", '{"', '{"a', '{"a"', '{"a":', '{"a": ', '{"a":1', '{"a":1,', '{"a":1,"b', '{"a":"x', '{"a":"x\\', '{"a":"x\\u12',
	'{"a":tr', '{"a":nu', '{"a":fal', '{"a":-', '{"a":1.', '{"a":1e', '{"a":1e+', '{"a":[1,2', '{"a":[1,{"b":', '{"a":{"b":{"c":',
	"[1,", "[1, 2", '["a", "b', "tru", "nul", "fals", "-", "-I", "-Inf", "Inf", "Infinity", "NaN", "Na", "N", "-Infinity", "1e", "1e5e",
	'{"path":"src/main.ts","content":"line1\\nline2', '{"command":"ls -la","timeout":', '{"edits":[{"oldText":"a","newText":"b"},{"old',
	// control characters and bad escapes
	'{"a":"line1\nline2"}', '{"a":"tab\there"}', '{"a":"bell\u0007"}', '{"a":"nul\u0000"}', '{"path":"C:\\Users\\me"}',
	'{"a":"\\x41"}', '{"a":"\\u12g4"}', '{"a":"\\', '{"a":"\\"}', '{"a":"ok\\', '"\\q"', '"a\nb"', '{"re":"\\d+\\s*"}',
	'{"a":"\\u00e9\\é"}', '{"a":"\r\n"}', '{"a":"x\u001fy"}',
	// malformed
	"{a:1}", "{'a':1}", '{"a":1,}', "[1,]", "{,}", "[,1]", '{"a" 1}', '{"a":1 "b":2}', '{"a":01}', '{"a":.5}', '{"a":+1}',
	'{"a":undefined}', '{"a":NaN}', '{"a":Infinity,"b":-Infinity}', '{"a":1}}', '{"a":1} trailing', "[1] 2", '{"a":[}', "{]", "[}",
	"<html><body>502</body></html>", "not json at all", "{ not json", "undefined", "[object Object]", "1 2", "--1", "0x10", "1_000",
	'{"a":1}{"b":2}', '{"a":{"b":1},"a":{"c":2}', '{"__proto__":null,"__proto__":{"x":1}', '{"__proto__":[1],"y":2', '{"__proto__":5,"q":1',
	'{"__proto__":{"__proto__":null},"z":1', '{"1":1,"0":0,"b":2', '{"a":"\\ud800', '{"a":"\\ud800"', '[1e400', '[-0', '[1.50',
	'{"a":[1 , 2 ] , "b" : 3', '{"a":[ ]', '[ ]x', '{"a":"b"x}', '{"a":truex}', '[truee]', '[nulx]', '["a" "b"]', '{"a":1 , }',
	'{1:2}', '{"a"', '{"a"x', '{"a":"b","c"', '{"k":[[["deep"', '[{"a":1},{"b"', '{"e":1e5x}', '[1e', '[-]', '[-a]', '{"a":-}',
	// depth
	deep(63, "[", "]"), deep(64, "[", "]"), deep(65, "[", "]"), deep(64, "[", ""), deep(65, "[", ""), deep(200, "[", ""),
	deep(64, '{"a":', "}"), deep(65, '{"a":', "}"), deep(70, '{"a":', ""), "[".repeat(65) + "x",
];

const frags = ["{", "}", "[", "]", ":", ",", '"', "\\", "u", "0", "1", "9", "-", "+", ".", "e", "E", "t", "r", "u", "e", "f", "a", "l",
	"s", "n", " ", "\n", "\r", "\t", "x", '"a"', '"b":', "12", "0.5", "1e5", "true", "false", "null", "\\u00e9", "\\n", "\u0001", "中",
	"é", "NaN", "Infinity", "-Infinity", '{"k":', "[1,2,", "\\u12", "\\uD83D\\uDE00", "\\ud800", "\u007f", "\u00a0", "\\x", "\\q",
	"__proto__", '"__proto__":', '"0":', '"10":', "1234567890123456789", "1e400", "-0", "\u2028", "😀"];
const valid = ['{"path":"src/a.ts","offset":1,"limit":20}', '{"command":"echo \\"hi\\"\\n","timeout":30}',
	'{"edits":[{"oldText":"a\\tb","newText":"c"}],"path":"x"}', '[true,false,null,-0.5e-3,"s"]', '{"nested":{"deep":[[[{"k":"v"}]]]}}',
	'{"a":"\\u00e9\\\\","b":[1e10,-0,0.25,123456789012]}', '{"q":"C:\\\\dir\\\\file","n":null}'];
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const pick = (list) => list[Math.floor(rnd() * list.length)];
const texts = [...fixed];
for (const v of valid) for (let p = 0; p <= v.length; p++) texts.push(v.slice(0, p));
for (let i = 0; i < count; i++) {
	let s;
	if (i % 2 === 0) {
		const v = valid[i % valid.length];
		const p = Math.floor(rnd() * (v.length + 1));
		const mode = Math.floor(rnd() * 4);
		const f = pick(frags);
		s = mode === 0 ? v.slice(0, p) + f : mode === 1 ? v.slice(0, p) + f + v.slice(p) : mode === 2 ? v.slice(0, p) + f + v.slice(p + 1) : v.slice(p);
	} else {
		s = "";
		for (let j = 1 + Math.floor(rnd() * 14); j > 0; j--) s += pick(frags);
	}
	texts.push(s);
}

const depthOf = (value) => {
	if (value === null || typeof value !== "object") return 0;
	let max = 0;
	for (const child of Object.values(value)) max = Math.max(max, depthOf(child));
	return max + 1;
};
// PiSharp owns tool arguments as System.Text.Json data, which cannot carry a lone surrogate: it keeps toWellFormed() of every name and
// string (colliding names keep the first position and the last value).
const toWellFormed = (value) => {
	if (typeof value === "string") return value.toWellFormed();
	if (value === null || typeof value !== "object") return value;
	if (Array.isArray(value)) return value.map(toWellFormed);
	const result = Object.create(null); // an own "__proto__" stays an own property
	for (const [key, child] of Object.entries(value)) result[key.toWellFormed()] = toWellFormed(child);
	return result;
};
const wellFormed = (text) => !/[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/.test(text);
const units = (text) => Array.from({ length: text.length }, (_, i) => text.charCodeAt(i).toString(16).padStart(4, "0")).join("");
const seen = new Set();
const cases = [];
for (const text of texts) {
	if (seen.has(text)) continue;
	seen.add(text);
	const result = parseStreamingJson(text);
	const entry = wellFormed(text) ? { text } : { units: units(text) };
	entry.json = JSON.stringify(result);
	entry.depth = depthOf(result);
	const native = JSON.stringify(toWellFormed(result));
	if (native !== entry.json) entry.native = native;
	entry.repaired = repairJson(text);
	if (!wellFormed(entry.repaired)) { delete entry.repaired; entry.repairedUnits = units(repairJson(text)); }
	cases.push(entry);
}
writeFileSync(out, JSON.stringify({ node: process.version, piAi: "1.1.0", partialJson: "0.1.7", cases }, null, 1) + "\n");
console.log(`${cases.length} cases`);
