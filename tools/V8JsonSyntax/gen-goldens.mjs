// Generates tests/PiSharp.CliParity.Tests/JsonSyntax/v8-json-parse-goldens.json: JSON.parse's SyntaxError message for a
// deterministic corpus of near-JSON texts, as Node >= 22.19 (V8 12.4, the runtime Pi 1.1.0 requires) words it.
// Usage: node tools/V8JsonSyntax/gen-goldens.mjs <out.json> [seed] [count]
import { writeFileSync } from "node:fs";

const frags = ["{", "}", "[", "]", ":", ",", '"', "\\", "u", "0", "1", "9", "-", "+", ".", "e", "E", "t", "r", "u", "e", "f", "a", "l", "s", "n", " ",
	"\n", "\r", "\t", "x", '"a"', '"b":', "12", "0.5", "1e5", "true", "false", "null", "\\u00e9", "\\n", "\u0001", "中", "é", "<html>", "NaN",
	"undefined", "Infinity", "[object Object]", '{"k":', "[1,2,", "abcdefghijklmnop", "\r\n", "\\u12", "\\uD83D\\uDE00", "\u007f", " ",
	"1234567890123"];
const valid = ['{"a":[1,2,{"b":null}],"c":"x\\ny"}', "[true,false,null,-0.5e-3]", '"str"', "  42  ", '{"nested":{"deep":[[[]]]}}',
	'{\n  "models": {\n    "x": 1\n  }\n}\n', "[1e10, -0, 0.25, 123456789012]"];
const fixed = ["{", "[", "[1", '{"a" 1}', '{"a":1 "b"}', "01", "-", "-a", "1.", "1e", "1e+", "tru", "tru1", "nul", '"abc', '"a\\x"',
	'"a\\u12g4"', '"a\u0001"', "undefined", "NaN", "Infinity", "[object Object]", "1 2", '{"a":1,}', "[1,]", "{,}", "", " ", "-0", "-01", "0-",
	"1e5x", "[-]", '"\\', '"\\中"', '"\\é"', "<html><body>502 Bad Gateway</body></html>", "{ not json", "\r\n\r\n x", "[1,2,3,4,5,6,7,8,9,x]"];

const out = process.argv[2];
let seed = Number(process.argv[3] ?? 4242);
const count = Number(process.argv[4] ?? 3000);
const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
const pick = (list) => list[Math.floor(rnd() * list.length)];
const texts = [...fixed];
for (let i = 0; i < count; i++) {
	let s;
	if (i % 3 === 0) {
		const v = valid[i % valid.length];
		const p = Math.floor(rnd() * (v.length + 1));
		const mode = Math.floor(rnd() * 3);
		const f = pick(frags);
		s = mode === 0 ? v.slice(0, p) : mode === 1 ? v.slice(0, p) + f + v.slice(p) : v.slice(0, p) + f + v.slice(p + 1);
	} else {
		s = "";
		for (let j = 1 + Math.floor(rnd() * 12); j > 0; j--) s += pick(frags);
	}
	if (rnd() < 0.1) s += " ".repeat(Math.floor(rnd() * 5)) + "padding text that is long enough";
	if (rnd() < 0.05) s = "leading long text " + s;
	if (rnd() < 0.05) s = "\n\r\n  \r" + s;
	texts.push(s);
}
const cases = texts.map((text) => {
	try {
		JSON.parse(text);
		return { text, message: null };
	} catch (error) {
		return { text, message: error.message };
	}
});
writeFileSync(out, `${JSON.stringify({ node: process.version, cases }, null, 1)}\n`);
