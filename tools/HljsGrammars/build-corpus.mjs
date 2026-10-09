// highlight.js 10.7.3 (BSD-3-Clause) differential-test corpus builder for PiSharp's native C# port
// (Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts).
//
// Usage: node build-corpus.mjs <corpus-src dir> <highlight.js package dir> <out hljs-corpus.json>
//
// corpus-src/<registered language name>/<slug>.txt holds one snippet each. Every snippet becomes a case; derived
// variants add a truncated copy (unterminated constructs), CRLF line endings and ignoreIllegals=false runs. Cases of
// phase "eager" run before lib/index.js is loaded (only Pi's 21 eager languages exist then).
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";

const [srcDir, libDir, outFile] = process.argv.slice(2);
if (!srcDir || !libDir || !outFile) {
	console.error("usage: node build-corpus.mjs <corpus-src dir> <highlight.js dir> <hljs-corpus.json>");
	process.exit(2);
}
const require = createRequire(import.meta.url);
const hljs = require(path.join(libDir, "lib", "index.js"));
const EAGER = ["python", "java", "go", "javascript", "json", "cpp", "typescript", "php", "ruby", "c", "csharp", "nix", "bash",
	"rust", "scala", "kotlin", "swift", "dart", "groovy", "perl", "lua"];
const languages = hljs.listLanguages();

const snippets = [];
for (const lang of fs.readdirSync(srcDir).sort()) {
	if (lang.startsWith(".")) continue;
	if (!languages.includes(lang)) throw new Error(`unknown language folder ${lang}`);
	for (const file of fs.readdirSync(path.join(srcDir, lang)).sort()) {
		if (!file.endsWith(".txt")) continue;
		snippets.push({ lang, slug: file.slice(0, -4), code: fs.readFileSync(path.join(srcDir, lang, file), "utf8") });
	}
}
const missing = languages.filter((l) => !snippets.some((s) => s.lang === l));
if (missing.length) console.error(`WARNING: no snippet for ${missing.join(", ")}`);

// Cut near a fraction of the text without splitting a surrogate pair.
const cut = (s, f) => {
	let i = Math.floor(s.length * f);
	if (i > 0 && i < s.length && /[\udc00-\udfff]/.test(s[i])) i--;
	return s.slice(0, i);
};

const cases = [];
const add = (phase, id, language, code, ignoreIllegals = true) => cases.push({ id, phase, language, ignoreIllegals, code });

// Phase "eager": Pi's eagerly registered languages, aliases and not-yet-registered names.
for (const s of snippets.filter((s) => EAGER.includes(s.lang))) add("eager", `eager/${s.lang}/${s.slug}`, s.lang, s.code);
const byLang = (l) => snippets.find((s) => s.lang === l)?.code ?? "";
for (const [alias, lang] of [["js", "javascript"], ["TS", "typescript"], ["py", "python"], ["c++", "cpp"], ["cs", "csharp"],
	["rb", "ruby"], ["sh", "bash"], ["kt", "kotlin"], ["golang", "go"], ["rs", "rust"], ["jsx", "javascript"], ["H", "c"]]) {
	add("eager", `eager/alias/${alias}`, alias, byLang(lang));
}
add("eager", "eager/unknown/xml-before-load", "xml", byLang("xml"));
add("eager", "eager/unknown/nope", "nope", "x = 1");

// Phase "all".
snippets.forEach((s, i) => {
	const id = `${s.lang}/${s.slug}`;
	add("all", id, s.lang, s.code);
	if (s.code.length > 20) add("all", `${id}#cut45`, s.lang, cut(s.code, 0.45));
	if (i % 3 === 0 && s.code.includes("\n")) add("all", `${id}#crlf`, s.lang, s.code.replace(/\r?\n/g, "\r\n"));
	if (i % 4 === 1) add("all", `${id}#strict`, s.lang, s.code, false);
	if (i % 7 === 2 && s.code.length > 40) add("all", `${id}#cut80`, s.lang, cut(s.code, 0.8));
});
for (const [alias, lang] of [["html", "xml"], ["HTML", "xml"], ["md", "markdown"], ["yml", "yaml"], ["ps1", "powershell"],
	["docker", "dockerfile"], ["mk", "makefile"], ["toml", "ini"], ["console", "shell"], ["text", "plaintext"], ["svg", "xml"],
	["postgres", "pgsql"], ["patch", "diff"], ["c-like", "cpp"], ["h", "c"], ["hpp", "cpp"]]) {
	add("all", `alias/${alias}`, alias, byLang(lang));
}
// highlightAuto (Pi's highlight() without a language; also what subLanguage arrays use): exercises relevance and the
// result sort (V8 TimSort with the supersetOf tie-break) over every registered language.
const addAuto = (phase, id, code, languageSubset = null) => cases.push({ id, phase, auto: true, languageSubset, language: null, ignoreIllegals: false, code });
addAuto("eager", "eager/auto/python", byLang("python"));
addAuto("eager", "eager/auto/rust", byLang("rust"));
addAuto("eager", "eager/auto/plain", "just some words, nothing else");
snippets.forEach((s, i) => { if (i % 23 === 5) addAuto("all", `auto/${s.lang}/${s.slug}`, s.code); });
addAuto("all", "auto/subset/cpp-arduino", byLang("cpp"), ["arduino", "cpp", "c"]);
addAuto("all", "auto/subset/arduino-first", "void setup() { pinMode(13, OUTPUT); }\nint main() { return 0; }\n", ["arduino", "cpp"]);
addAuto("all", "auto/subset/js-xml", byLang("xml"), ["javascript", "xml", "handlebars"]);
addAuto("all", "auto/empty", "");
add("all", "unknown/nope", "nope", "x = 1");
add("all", "unknown/empty-name", "", "x = 1");

// Deterministic fuzz: every snippet with a few random edits from a hostile alphabet (quotes, brackets, escapes, line
// breaks, markup, non-ASCII) to drive grammars through unterminated, illegal and resumed-scan paths.
let seed = 0x2545f491;
const rnd = (n) => {
	seed ^= seed << 13; seed >>>= 0;
	seed ^= seed >>> 17;
	seed ^= seed << 5; seed >>>= 0;
	return seed % n;
};
const NASTY = ["\"", "'", "`", "\\", "/", "*", "<", ">", "{", "}", "(", ")", "[", "]", "#", "$", "@", "%", "\n", "\r\n", "\t", " ",
	"<!--", "-->", "*/", "/*", "${", "#{", "<<EOF\n", "?>", "<?php", "</script>", "é", "日", "😀", " ", "İ", ".class", "=>", "::"];
const FUZZ_ROUNDS = Number(process.env.HLJS_FUZZ_ROUNDS ?? 1); // stress runs: e.g. HLJS_FUZZ_ROUNDS=20
for (let round = 0; round < FUZZ_ROUNDS; round++) snippets.forEach((s, i) => {
	if ((i + round) % 2 !== 0 || s.code.length < 10) return;
	let code = s.code;
	const edits = 1 + rnd(4);
	for (let e = 0; e < edits; e++) {
		let at = rnd(code.length + 1);
		if (at > 0 && at < code.length && /[\udc00-\udfff]/.test(code[at])) at--;
		const op = rnd(3);
		if (op === 0) code = code.slice(0, at) + NASTY[rnd(NASTY.length)] + code.slice(at);
		else if (op === 1) {
			let end = Math.min(code.length, at + 1 + rnd(8));
			if (end < code.length && /[\udc00-\udfff]/.test(code[end])) end++;
			code = code.slice(0, at) + code.slice(end);
		} else code = code.slice(0, at) + NASTY[rnd(NASTY.length)] + NASTY[rnd(NASTY.length)] + code.slice(at);
	}
	add("all", `fuzz/${s.lang}/${s.slug}${round ? "#" + round : ""}`, s.lang, code, rnd(3) !== 0);
});

// Sub-language continuations: hljs resumes a string subLanguage where its previous segment in the same document ended.
add("all", "continuation/xml-script-comment", "xml", "<script>let a = 1; /* open\n</script><p>mid &amp; more</p><script>still comment */ let b = `t${a}`;</script>\n<style>a { color: red; /* x */</style><style>} b { margin: 0 }</style>");
add("all", "continuation/php-template", "php-template", "<ul>\n<?php foreach ($items as $i) { /* open ?>\n<li><?= $i ?> still */ echo \"<b>$i</b>\"; } ?>\n</ul>\n<?php $s = <<<EOT\nheredoc <?php\nEOT;\n?>");
add("all", "continuation/erb", "erb", "<% if @x %>\n<p><%= @x.name # c %></p>\n<% else %>\n<%# comment %>\n<% end %>");
add("all", "continuation/markdown-html", "markdown", "<div>\n<span>a</span>\n</div>\n\ntext <b>bold</b> *e*\n\n```html\n<p>x</p>\n```\n");

// Synthetic stress: a long TypeScript file (also the perf benchmark input).
const tsUnit = byLang("typescript");
add("all", "stress/typescript-500", "typescript", Array.from({ length: Math.ceil(500 / Math.max(1, tsUnit.split("\n").length)) }, () => tsUnit).join("\n").split("\n").slice(0, 500).join("\n"));
add("all", "stress/long-line-js", "javascript", "const a = [" + Array.from({ length: 600 }, (_, i) => `"s${i}", ${i}, /r${i}/g`).join(", ") + "];\n");

const aliasNames = new Set();
for (const l of languages) for (const a of hljs.getLanguage(l).aliases ?? []) aliasNames.add(a);
const supportsLanguage = [...new Set([...languages, ...aliasNames, "Python", "JavaScript", "C++", "CSharp", "HTML", "XML", "YAML",
	"nope", "", " ", "python ", "constructor", "__proto__", "toString", "hasOwnProperty", "undefined", "null", "İ", "c-like"])];

const renderCases = [
	"plain &amp; &lt;tag&gt; &quot;q&quot; &#x27;a&#x27; &apos;",
	"&#65;&#x42;&#X43;&#x1F600;&#x110000;&#xD800;&#-1;&#x-41;&#0x41;&#x0x41;&# 66;&#;&#x;&#12345678901234;&bogus;&amp",
	"&amp;&amp;&lt;&#x41;&#x41&#x41;",
	"&abcdefghijklmnopq;&abcdefghijklmno;",
	'<span class="hljs-keyword">if</span> <span class="hljs-meta-keyword">x</span>\n<span class="hljs-meta">a\n\nb</span>',
	'<span class="hljs-selector-tag hljs-x">s</span><span class=\'hljs-title.class_\'>t</span><span\tclass = "x hljs-number">1</span>',
	'<span class="">e</span><span>n</span><span class="xml"><span class="hljs-tag">t</span>u</span>v</span></span>w',
	"<spanx>not a tag</spanx><span class=\"hljs-string\">unterminated <span",
	"line1\nline2\r\n\n<span class=\"hljs-comment\">// c\n</span>",
	// Pi's theme is a plain object: unknown scopes named like Object.prototype members resolve to those members.
	'<span class="hljs-keyword"><span class="hljs-constructor">C</span><span class="hljs-toString">a</span><span class="hljs-isPrototypeOf">b</span><span class="hljs-constructor-x">c</span>\n</span>',
];

fs.writeFileSync(outFile, JSON.stringify({ version: "10.7.3", cases, supportsLanguage, renderCases }));
console.error(`${snippets.length} snippets, ${cases.length} cases (${cases.filter((c) => c.phase === "eager").length} eager), ${supportsLanguage.length} supportsLanguage names`);
