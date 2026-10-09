// highlight.js 10.7.3 (BSD-3-Clause): lib/languages/*.js, lib/index.js — grammar extractor for PiSharp's native C# port
// (Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts).
//
// Usage: node extract-grammars.mjs <highlight.js package dir> <out.json.gz> [debug-out.json]
//
// Registers languages on the real hljs core exactly the way Pi does (21 eager languages, then lib/index.js on the same
// instance), captures every language definition object as returned by its LanguageFn (before any compilation), and
// serializes the raw mode graph:
//   - every plain object (mode, keyword table, ...) gets an id per language; references are {"o":id} so shared and cyclic
//     objects keep their identity (hljs compilation mutates objects in place, so identity matters);
//   - RegExp -> {"r":source} (hljs only ever uses `.source`; flags are dropped by langRe), undefined -> {"u":1};
//   - functions -> {"c":"<callback id>"}; unknown functions abort the extraction;
//   - frozen objects (hljs MODES) carry "#f":1 (hljs clones frozen modes on every use).
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import zlib from "node:zlib";

const [libDir, outFile, debugFile] = process.argv.slice(2);
if (!libDir || !outFile) {
	console.error("usage: node extract-grammars.mjs <highlight.js dir> <out.json.gz> [debug.json]");
	process.exit(2);
}
const require = createRequire(import.meta.url);
const pkg = JSON.parse(fs.readFileSync(path.join(libDir, "package.json"), "utf8"));
if (pkg.version !== "10.7.3") throw new Error(`expected highlight.js 10.7.3, got ${pkg.version}`);

const hljs = require(path.join(libDir, "lib", "core.js"));
const EAGER = ["python", "java", "go", "javascript", "json", "cpp", "typescript", "php", "ruby", "c", "csharp", "nix", "bash",
	"rust", "scala", "kotlin", "swift", "dart", "groovy", "perl", "lua"];

// ---- callback identification -------------------------------------------------------------------------------------
const norm = (f) => f.toString().replace(/\s+/g, " ").trim();
const CALLBACKS = new Map([
	[norm(hljs.SHEBANG()["on:begin"]), "shebang"],
	[norm(hljs.END_SAME_AS_BEGIN({})["on:begin"]), "endSameAsBegin.begin"],
	[norm(hljs.END_SAME_AS_BEGIN({})["on:end"]), "endSameAsBegin.end"],
	[norm(`(match, response) => {
      const afterMatchIndex = match[0].length + match.index;
      const nextChar = match.input[afterMatchIndex];
      // nested type?
      // HTML should not include another raw \`<\` inside a tag
      // But a type might: \`<Array<Array<number>>\`, etc.
      if (nextChar === "<") {
        response.ignoreMatch();
        return;
      }
      // <something>
      // This is now either a tag or a type.
      if (nextChar === ">") {
        // if we cannot find a matching closing tag, then we
        // will ignore it
        if (!hasClosingTag(match, { after: afterMatchIndex })) {
          response.ignoreMatch();
        }
      }
    }`), "javascript.isTrulyOpeningTag"],
	[norm(`(match, response) => {
          if (!SYSTEM_SYMBOLS_SET.has(match[0])) response.ignoreMatch();
        }`), "mathematica.systemSymbol"],
	[norm(`(mode, parent) => {
        if (!mode.beforeMatch) return;
        // starts conflicts with endsParent which we need to make sure the child
        // rule is not matched multiple times
        if (mode.starts) throw new Error("beforeMatch cannot be used with starts");

        const originalMode = Object.assign({}, mode);
        Object.keys(mode).forEach((key) => { delete mode[key]; });

        mode.begin = concat(originalMode.beforeMatch, lookahead(originalMode.begin));
        mode.starts = {
          relevance: 0,
          contains: [
            Object.assign(originalMode, { endsParent: true })
          ]
        };
        mode.relevance = 0;

        delete originalMode.beforeMatch;
      }`), "r.beforeMatch"],
]);
// The ported callbacks close over helpers; make sure those helpers are what the port assumes.
{
	const js = fs.readFileSync(path.join(libDir, "lib", "languages", "javascript.js"), "utf8");
	const ts = fs.readFileSync(path.join(libDir, "lib", "languages", "typescript.js"), "utf8");
	const hct = `const hasClosingTag = (match, { after }) => {
    const tag = "</" + match[0].slice(1);
    const pos = match.input.indexOf(tag, after);
    return pos !== -1;
  };`;
	if (!js.includes(hct) || !ts.includes(hct)) throw new Error("javascript/typescript hasClosingTag changed");
	const r = fs.readFileSync(path.join(libDir, "lib", "languages", "r.js"), "utf8");
	if (!r.includes("function lookahead(re) {\n  return concat('(?=', re, ')');\n}")) throw new Error("r.js lookahead changed");
	if (!r.includes("function concat(...args) {\n  const joined = args.map((x) => source(x)).join(\"\");\n  return joined;\n}"))
		throw new Error("r.js concat changed");
}
const usedCallbacks = new Set();
function callbackId(fn, where) {
	const id = CALLBACKS.get(norm(fn));
	if (!id) throw new Error(`unrecognised function at ${where}:\n${fn.toString()}`);
	usedCallbacks.add(id);
	return id;
}

// mathematica's SYSTEM_SYMBOLS (closed over by its on:begin callback): evaluate the module source in a sandbox.
function mathematicaSymbols() {
	const file = path.join(libDir, "lib", "languages", "mathematica.js");
	const src = fs.readFileSync(file, "utf8") + "\nmodule.exports.__SYSTEM_SYMBOLS = SYSTEM_SYMBOLS;\n";
	const sandbox = { module: { exports: {} }, exports: {}, require: () => { throw new Error("require"); } };
	vm.runInNewContext(src, sandbox, { filename: file });
	const list = Array.from(sandbox.module.exports.__SYSTEM_SYMBOLS);
	if (!Array.isArray(list) || list.length < 1000) throw new Error("mathematica SYSTEM_SYMBOLS not found");
	return list;
}

// ---- capture registrations ---------------------------------------------------------------------------------------
const captured = []; // { phase, name, lang }
let phase = "eager";
const origRegister = hljs.registerLanguage;
hljs.registerLanguage = function (name, def) {
	let lang;
	origRegister.call(this, name, (h) => (lang = def(h)));
	if (!lang) throw new Error(`language ${name} failed to register`);
	captured.push({ phase, name, lang });
};
for (const name of EAGER) hljs.registerLanguage(name, require(path.join(libDir, "lib", "languages", `${name}.js`)));
phase = "index";
require(path.join(libDir, "lib", "index.js"));
hljs.registerLanguage = origRegister;

// ---- serialization -----------------------------------------------------------------------------------------------
const SKIP_KEYS = new Set(["rawDefinition"]);
const owner = new Map(); // non-frozen object -> registration name (detect sharing across registrations)
const sharedAcrossRegistrations = new Set();
const keyStats = new Map();
function serialize(lang, regIndex, langName) {
	const ids = new Map();
	const objs = [];
	const enc = (v, where) => {
		if (v === undefined) return { u: 1 };
		if (v === null || typeof v === "string" || typeof v === "boolean") return v;
		if (typeof v === "number") {
			if (!Number.isFinite(v)) throw new Error(`non-finite number at ${where}`);
			return v;
		}
		if (v instanceof RegExp) return { r: v.source };
		if (typeof v === "function") return { c: callbackId(v, where) };
		if (Array.isArray(v)) return v.map((x, i) => enc(x, `${where}[${i}]`));
		if (typeof v === "object") return { o: idOf(v, where) };
		throw new Error(`unsupported value ${typeof v} at ${where}`);
	};
	const idOf = (o, where) => {
		if (ids.has(o)) return ids.get(o);
		if (o instanceof Map || o instanceof Set) throw new Error(`Map/Set at ${where}`);
		const proto = Object.getPrototypeOf(o);
		if (proto !== null && proto !== Object.prototype) throw new Error(`exotic object at ${where}`);
		const id = objs.length;
		ids.set(o, id);
		objs.push(null);
		const frozen = Object.isFrozen(o);
		if (!frozen) {
			const prev = owner.get(o);
			// Module-level mode objects (java/kotlin NUMERIC) are shared by the eager and the index.js registration of the
			// same language. That is benign (identical language settings, no begin rules); anything else is not.
			if (prev !== undefined && prev !== langName) throw new Error(`object shared between languages at ${where}`);
			if (prev !== undefined) sharedAcrossRegistrations.add(where);
			owner.set(o, langName);
		}
		const rec = {};
		if (frozen) rec["#f"] = 1;
		for (const k in o) {
			if (SKIP_KEYS.has(k)) continue;
			keyStats.set(k, (keyStats.get(k) || 0) + 1);
			rec[k] = enc(o[k], `${where}.${k}`);
		}
		objs[id] = rec;
		return id;
	};
	idOf(lang, langName);
	return objs;
}

const languages = {};
const order = [];
const eagerDump = {};
captured.forEach(({ phase: ph, name, lang }, i) => {
	const objs = serialize(lang, i, name);
	if (ph === "eager") {
		eagerDump[name] = JSON.stringify(objs);
	} else {
		order.push(name);
		languages[name] = objs;
	}
});
for (const name of EAGER) {
	if (eagerDump[name] !== JSON.stringify(languages[name])) throw new Error(`eager registration of ${name} differs from index.js`);
}

// ECMAScript non-unicode case-insensitive Canonicalize (ES2020 21.2.2.8.2) as implemented by this Node/V8: flat
// [ch, canonical, ...] for every UTF-16 unit whose canonical form differs from itself. The C# regex translator expands
// /i patterns with this table instead of relying on .NET's (different) case equivalences.
function canonicalizeTable() {
	const hex = (c) => "\\u" + c.toString(16).padStart(4, "0");
	const canon = new Array(0x10000);
	const flat = [];
	for (let c = 0; c < 0x10000; c++) {
		const u = String.fromCharCode(c).toUpperCase();
		let cu = u.length === 1 ? u.charCodeAt(0) : c;
		if (c >= 128 && cu < 128) cu = c;
		canon[c] = cu;
		if (cu !== c) flat.push(c, cu);
	}
	// Verify against the regex engine itself.
	const groups = new Map();
	for (let c = 0; c < 0x10000; c++) (groups.get(canon[c]) ?? groups.set(canon[c], []).get(canon[c])).push(c);
	for (let c = 0; c < 0x10000; c++) {
		const re = new RegExp(`[${hex(c)}]`, "i");
		const group = groups.get(canon[c]);
		for (const d of group) if (!re.test(String.fromCharCode(d))) throw new Error(`canonicalize mismatch ${c} ~ ${d}`);
		for (const s of [String.fromCharCode(c).toLowerCase(), String.fromCharCode(c).toUpperCase()]) {
			if (s.length === 1 && !group.includes(s.charCodeAt(0)) && re.test(s)) throw new Error(`canonicalize mismatch ${c} !~ ${s.charCodeAt(0)}`);
		}
	}
	return flat;
}

const out = {
	version: "10.7.3",
	eager: EAGER,
	order, // lib/index.js registration order
	data: {
		"mathematica.SYSTEM_SYMBOLS": usedCallbacks.has("mathematica.systemSymbol") ? mathematicaSymbols() : [],
		canonicalize: canonicalizeTable(),
	},
	languages,
};
const json = JSON.stringify(out);
fs.writeFileSync(outFile, zlib.gzipSync(Buffer.from(json, "utf8"), { level: 9 }));
if (debugFile) fs.writeFileSync(debugFile, JSON.stringify(out, null, 1));
const unused = [...CALLBACKS.values()].filter((c) => !usedCallbacks.has(c));
console.error(`languages: ${order.length}, json: ${json.length} bytes, callbacks used: ${[...usedCallbacks].join(", ")}` +
	(unused.length ? `, UNUSED: ${unused.join(", ")}` : ""));
if (sharedAcrossRegistrations.size) console.error("shared across eager/index registrations: " + [...sharedAcrossRegistrations].join(", "));
console.error("keys: " + [...keyStats.entries()].sort((a, b) => b[1] - a[1]).map(([k, n]) => `${k}=${n}`).join(" "));
