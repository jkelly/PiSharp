// highlight.js 10.7.3 (BSD-3-Clause) differential-test golden generator for PiSharp's native C# port
// (Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts).
//
// Usage: node generate-goldens.mjs <highlight.js package dir> <hljs-corpus.json> <out hljs-goldens.json>
//
// Replays Pi's lifecycle on the real library: hljs core + the 21 eager languages, the corpus cases of phase "eager",
// then require("lib/index.js") on the same instance (Pi's loadAllHighlightLanguages) and the cases of phase "all" —
// in corpus order, in one process, because hljs keeps some matcher state across calls. Records
// hljs.highlight(code, {language, ignoreIllegals}).value (or the thrown message), supportsLanguage() answers, and
// renderHighlightedHtml(value, TEST_THEME) for each case.
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";

const [libDir, corpusFile, outFile] = process.argv.slice(2);
if (!libDir || !corpusFile || !outFile) {
	console.error("usage: node generate-goldens.mjs <highlight.js dir> <hljs-corpus.json> <hljs-goldens.json>");
	process.exit(2);
}
const require = createRequire(import.meta.url);
const corpus = JSON.parse(fs.readFileSync(corpusFile, "utf8"));
const hljs = require(path.join(libDir, "lib", "core.js"));
if (hljs.versionString !== "10.7.3") throw new Error(`expected highlight.js 10.7.3, got ${hljs.versionString}`);

// ---- Pi utils/html.ts + utils/syntax-highlight.ts renderHighlightedHtml (transcribed verbatim, types removed) ----------
function decodeCodePoint(codePoint) {
	if (!Number.isInteger(codePoint) || codePoint < 0 || codePoint > 0x10ffff) return undefined;
	return String.fromCodePoint(codePoint);
}
function decodeHtmlEntity(entity) {
	switch (entity) {
		case "amp": return "&";
		case "lt": return "<";
		case "gt": return ">";
		case "quot": return '"';
		case "apos": return "'";
	}
	if (entity.startsWith("#x") || entity.startsWith("#X")) return decodeCodePoint(Number.parseInt(entity.slice(2), 16));
	if (entity.startsWith("#")) return decodeCodePoint(Number.parseInt(entity.slice(1), 10));
	return undefined;
}
function decodeHtmlEntityAt(html, index) {
	const semicolonIndex = html.indexOf(";", index + 1);
	if (semicolonIndex === -1 || semicolonIndex - index > 16) return undefined;
	const entity = html.slice(index + 1, semicolonIndex);
	const decoded = decodeHtmlEntity(entity);
	if (decoded === undefined) return undefined;
	return { text: decoded, length: semicolonIndex - index + 1 };
}
const SPAN_CLOSE = "</span>";
const HIGHLIGHT_CLASS_PREFIX = "hljs-";
function getScopeFromSpanTag(tag) {
	const match = /\sclass\s*=\s*(?:"([^"]*)"|'([^']*)')/.exec(tag);
	const classValue = match?.[1] ?? match?.[2];
	if (!classValue) return undefined;
	for (const className of classValue.split(/\s+/)) {
		if (className.startsWith(HIGHLIGHT_CLASS_PREFIX)) return className.slice(HIGHLIGHT_CLASS_PREFIX.length);
	}
	return undefined;
}
function getScopeFormatter(scope, theme) {
	const exact = theme[scope];
	if (exact) return exact;
	const dotIndex = scope.indexOf(".");
	if (dotIndex !== -1) {
		const prefixFormatter = theme[scope.slice(0, dotIndex)];
		if (prefixFormatter) return prefixFormatter;
	}
	const dashIndex = scope.indexOf("-");
	if (dashIndex !== -1) {
		const prefixFormatter = theme[scope.slice(0, dashIndex)];
		if (prefixFormatter) return prefixFormatter;
	}
	return undefined;
}
function getActiveFormatter(scopes, theme) {
	for (let i = scopes.length - 1; i >= 0; i--) {
		const scope = scopes[i];
		if (!scope) continue;
		const formatter = getScopeFormatter(scope, theme);
		if (formatter) return formatter;
	}
	return theme.default;
}
function isSpanOpenTagStart(html, index) {
	if (!html.startsWith("<span", index)) return false;
	const nextChar = html[index + "<span".length];
	return nextChar === ">" || nextChar === " " || nextChar === "\t" || nextChar === "\n" || nextChar === "\r";
}
function renderHighlightedHtml(html, theme = {}) {
	let output = "";
	let textBuffer = "";
	const scopes = [];
	const flushText = () => {
		if (!textBuffer) return;
		const formatter = getActiveFormatter(scopes, theme);
		output += formatter ? textBuffer.split("\n").map((line) => (line ? formatter(line) : line)).join("\n") : textBuffer;
		textBuffer = "";
	};
	let index = 0;
	while (index < html.length) {
		if (isSpanOpenTagStart(html, index)) {
			const tagEndIndex = html.indexOf(">", index + 5);
			if (tagEndIndex !== -1) {
				flushText();
				const tag = html.slice(index, tagEndIndex + 1);
				const scope = getScopeFromSpanTag(tag);
				scopes.push(scope);
				index = tagEndIndex + 1;
				continue;
			}
		}
		if (html.startsWith(SPAN_CLOSE, index)) {
			flushText();
			if (scopes.length > 0) scopes.pop();
			index += SPAN_CLOSE.length;
			continue;
		}
		if (html[index] === "&") {
			const decoded = decodeHtmlEntityAt(html, index);
			if (decoded) {
				textBuffer += decoded.text;
				index += decoded.length;
				continue;
			}
		}
		textBuffer += html[index];
		index++;
	}
	flushText();
	return output;
}

// The C# test builds the same theme: each listed scope wraps text as «scope|text».
export const TEST_THEME_SCOPES = ["keyword", "string", "comment", "title", "meta", "number", "built_in", "literal", "selector",
	"template", "attr", "tag", "name", "variable", "type", "subst", "section", "default"];
const theme = Object.fromEntries(TEST_THEME_SCOPES.map((s) => [s, (t) => `«${s}|${t}»`]));

// ---- run ---------------------------------------------------------------------------------------------------------------
const EAGER = ["python", "java", "go", "javascript", "json", "cpp", "typescript", "php", "ruby", "c", "csharp", "nix", "bash",
	"rust", "scala", "kotlin", "swift", "dart", "groovy", "perl", "lua"];
for (const name of EAGER) hljs.registerLanguage(name, require(path.join(libDir, "lib", "languages", `${name}.js`)));

const silence = console.error;
const results = [];
const supports = [];
const t0 = Date.now();
let loaded = false;
for (const c of corpus.cases) {
	if (c.phase === "all" && !loaded) {
		for (const name of corpus.supportsLanguage) supports.push({ phase: "eager", name, result: hljs.getLanguage(name) !== undefined });
		require(path.join(libDir, "lib", "index.js"));
		loaded = true;
	}
	const r = { id: c.id };
	try {
		console.error = () => {};
		if (c.auto) {
			const auto = hljs.highlightAuto(c.code, c.languageSubset ?? undefined);
			r.value = auto.value;
			r.language = auto.language ?? null;
			r.relevance = auto.relevance;
		} else {
			r.value = hljs.highlight(c.code, { language: c.language, ignoreIllegals: c.ignoreIllegals }).value;
		}
		r.rendered = renderHighlightedHtml(r.value, theme);
	} catch (e) {
		r.error = String(e && e.message);
	} finally {
		console.error = silence;
	}
	results.push(r);
}
if (!loaded) require(path.join(libDir, "lib", "index.js"));
for (const name of corpus.supportsLanguage) supports.push({ phase: "all", name, result: hljs.getLanguage(name) !== undefined });
// Rendered as UTF-16 code units: entity decoding can produce lone surrogates, which JSON readers reject.
const utf16 = (s) => Array.from({ length: s.length }, (_, i) => s.charCodeAt(i));
const renderCases = corpus.renderCases.map((html) => ({ html, rendered: utf16(renderHighlightedHtml(html, theme)) }));

fs.writeFileSync(outFile, JSON.stringify({ version: "10.7.3", node: process.version, themeScopes: TEST_THEME_SCOPES, cases: results, supports, renderCases }));
console.error(`${results.length} cases, ${supports.length} supportsLanguage checks, ${renderCases.length} render cases in ${Date.now() - t0} ms`);
