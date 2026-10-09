// Regenerates the mechanically ported upstream Pi modules in this folder (tui/, ai/, agent/,
// coding-agent/ except the hand-written files, pi-tui.mjs, pi-ai.mjs, pi-agent-core.mjs, theme.mjs).
// TypeScript types are stripped with Node's built-in stripTypeScriptTypes (transform mode), import
// specifiers are rewritten to the virtual modules / vendor replacements, and the PiSharp adaptations
// below (redirects, patches, appends) are applied. Requires Node 22.13+.
//
//   node port-upstream.mjs <path-to-upstream-pi>/packages
//
// The upstream checkout must be Pi commit abe508e1b89912adde45528136c3221eb69acdd7 (MIT).
import fs from "node:fs";
import path from "node:path";
import { stripTypeScriptTypes } from "node:module";
import { fileURLToPath } from "node:url";

const COMMIT = "abe508e1b89912adde45528136c3221eb69acdd7";
const UP = path.resolve(process.argv[2] ?? process.env.PI_UPSTREAM_PACKAGES ?? "");
if (!process.argv[2] && !process.env.PI_UPSTREAM_PACKAGES) {
	console.error("usage: node port-upstream.mjs <upstream pi packages dir>");
	process.exit(2);
}
const OUT = path.resolve(process.env.OUT ?? path.dirname(fileURLToPath(import.meta.url)));

// upstream path (relative to packages/) -> output path (relative to virtual/)
const outMap = new Map();
// upstream relative path -> virtual relative target (hand-written replacement); not ported
const redirects = new Map();
const bare = {
	"get-east-asian-width": "vendor/east-asian-width.mjs",
	marked: "vendor/marked.mjs",
	"partial-json": "vendor/partial-json.mjs",
	chalk: "vendor/chalk.mjs",
	yaml: "vendor/yaml.mjs",
	"cross-spawn": "vendor/cross-spawn.mjs",
	ignore: "vendor/ignore.mjs",
	typebox: "typebox.mjs",
	"typebox/compile": "typebox-compile.mjs",
	"typebox/value": "typebox-value.mjs",
	"@earendil-works/pi-ai": "pi-ai.mjs",
	"@earendil-works/pi-ai/compat": "pi-ai.mjs",
	"@earendil-works/pi-tui": "pi-tui.mjs",
	"@earendil-works/pi-agent-core": "pi-agent-core.mjs",
};
const builtins = new Set(["fs", "os", "path", "url", "module", "events", "child_process", "crypto", "util", "stream", "readline", "process", "buffer", "tty", "worker_threads", "perf_hooks", "fs/promises", "assert", "string_decoder", "timers", "timers/promises"]);

const patches = new Map(); // upstream rel -> [[from, to], ...]
const appends = new Map(); // upstream rel -> string

function defaultOut(rel) {
	// tui/src/x.ts -> tui/x.mjs ; ai/src/a/b.ts -> ai/a/b.mjs
	const m = /^([^/]+)\/src\/(.*)\.ts$/.exec(rel);
	if (!m) throw new Error(`bad rel ${rel}`);
	return `${m[1]}/${m[2]}.mjs`;
}

const unresolved = [];

function specifiersOf(code) {
	const out = [];
	const re = /(\bfrom\s*|\bimport\s*\(\s*|\bimport\s+)(["'])([^"']+)\2/g;
	let m;
	while ((m = re.exec(code))) out.push(m[3]);
	return out;
}

function relFromUpstream(fromRel, spec) {
	const abs = path.resolve(path.dirname(path.join(UP, fromRel)), spec);
	return path.relative(UP, abs).replace(/\\/g, "/");
}

const queue = [];
function enqueue(rel, out) {
	if (redirects.has(rel)) return;
	if (outMap.has(rel)) return;
	outMap.set(rel, out ?? defaultOut(rel));
	queue.push(rel);
}

function relImport(fromOut, toOut) {
	let r = path.relative(path.dirname(path.join(OUT, fromOut)), path.join(OUT, toOut)).replace(/\\/g, "/");
	if (!r.startsWith(".")) r = `./${r}`;
	return r;
}

function run(entries, options = {}) {
	for (const [rel, out] of entries) enqueue(rel, out);
	for (const [rel, list] of Object.entries(options.patches ?? {})) patches.set(rel, list);
	for (const [rel, text] of Object.entries(options.appends ?? {})) appends.set(rel, text);
	for (const [rel, target] of Object.entries(options.redirects ?? {})) redirects.set(rel, target);
	const stop = new Set(options.stop ?? []);
	const written = [];
	while (queue.length) {
		const rel = queue.shift();
		const out = outMap.get(rel);
		let src = fs.readFileSync(path.join(UP, rel), "utf8");
		for (const [from, to] of patches.get(rel) ?? []) {
			if (!src.includes(from)) throw new Error(`patch not found in ${rel}: ${from.slice(0, 80)}`);
			src = src.split(from).join(to);
		}
		let code = stripTypeScriptTypes(src, { mode: "transform", sourceMap: false });
		code = code.replace(/(^(?:import|export)\b[^;'"]*?\bfrom\s*|^import\s*|\bimport\(\s*)(["'])([^"'\n]+)\2/gm, (whole, pre, q, spec) => {
			let target;
			if (spec.startsWith(".") && spec.endsWith(".mjs")) {
				return whole;
			} else if (spec.startsWith(".")) {
				const depRel = relFromUpstream(rel, spec);
				if (redirects.has(depRel)) target = redirects.get(depRel);
				else if (stop.has(depRel)) {
					unresolved.push(`${rel} -> ${spec} (stopped)`);
					return whole;
				} else {
					if (!fs.existsSync(path.join(UP, depRel))) {
						unresolved.push(`${rel} -> ${spec} (missing)`);
						return whole;
					}
					enqueue(depRel);
					target = outMap.get(depRel);
				}
			} else if (bare[spec]) {
				target = bare[spec];
			} else if (spec.startsWith("node:") || builtins.has(spec)) {
				return whole;
			} else {
				unresolved.push(`${rel} -> ${spec} (bare)`);
				return whole;
			}
			return `${pre}${q}${relImport(out, target)}${q}`;
		});
		if (appends.has(rel)) code += `\n${appends.get(rel)}\n`;
		const header = `// Pi ${COMMIT} (MIT): packages/${rel}.\n// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged${patches.has(rel) || appends.has(rel) ? " except for the marked PiSharp adaptations" : ""}).\n`;
		const dest = path.join(OUT, out);
		fs.mkdirSync(path.dirname(dest), { recursive: true });
		fs.writeFileSync(dest, header + code);
		written.push(out);
	}
	return { written, unresolved };
}

// ---------------------------------------------------------------------------------------------
// Port configuration
// ---------------------------------------------------------------------------------------------

const themeDir = path.join(UP, "coding-agent/src/modes/interactive/theme");
const dark = JSON.parse(fs.readFileSync(path.join(themeDir, "dark.json"), "utf8"));
const light = JSON.parse(fs.readFileSync(path.join(themeDir, "light.json"), "utf8"));

const apiImpl = [
	"anthropic-messages",
	"azure-openai-responses",
	"bedrock-converse-stream",
	"google-generative-ai",
	"google-vertex",
	"mistral-conversations",
	"openai-codex-responses",
	"openai-completions",
	"openai-responses",
	"pi-messages",
	"openrouter-images",
];

const redirectConfig = {
	"ai/src/providers/all.ts": "pi-ai-providers.mjs",
	"ai/src/image-models.ts": "ai-host/image-models.mjs",
	"coding-agent/src/utils/syntax-highlight.ts": "coding-agent/syntax-highlight.mjs",
};
for (const api of apiImpl) redirectConfig[`ai/src/api/${api}.ts`] = "ai-host/api.mjs";

const patchConfig = {
	"ai/src/compat.ts": [
		["const compatModels = builtinModels();", "// PiSharp: the builtin model catalog lives in the host; builtin APIs are host-backed (ai-host/api.mjs)."],
		[
			"function getBuiltinProviderForModel(model: Model<Api>) {\n\tif (getApiProvider(model.api) !== builtinApiProviderInstances.get(model.api)) return undefined;\n\tconst provider = compatModels.getProvider(model.provider);\n\treturn provider?.getModels().some((candidate) => candidate.api === model.api) ? provider : undefined;\n}",
			"function getBuiltinProviderForModel(_model: Model<Api>): undefined {\n\t// PiSharp: builtin providers are reached through the registered host-backed API implementations.\n\treturn undefined;\n}",
		],
		["export const getModel = getBuiltinModel;", 'export const getModel = __pisharpHostFunction("getModel");'],
		["export const getModels = getBuiltinModels;", 'export const getModels = __pisharpHostFunction("getModels");'],
		["export const getProviders = getBuiltinProviders;", 'export const getProviders = __pisharpHostFunction("getProviders");'],
		[
			"\tconst provider = resolveApiProvider(model.api);\n\treturn provider.stream(model, transcript, withEnvApiKey(model, options) as StreamOptions);",
			'\tconst provider = resolveApiProvider(model.api);\n\t__pisharpRequireHost("stream", model.api);\n\treturn provider.stream(model, transcript, withEnvApiKey(model, options) as StreamOptions);',
		],
		[
			"\tconst provider = resolveApiProvider(model.api);\n\treturn provider.streamSimple(model, transcript, withEnvApiKey(model, options));",
			'\tconst provider = resolveApiProvider(model.api);\n\t__pisharpRequireHost("streamSimple", model.api);\n\treturn provider.streamSimple(model, transcript, withEnvApiKey(model, options));',
		],
		["\tconst s = stream(model, context, options);", '\t__pisharpRequireHost("complete", model.api);\n\tconst s = stream(model, context, options);'],
		["\tconst s = streamSimple(model, context, options);", '\t__pisharpRequireHost("completeSimple", model.api);\n\tconst s = streamSimple(model, context, options);'],
		["): void {\n\tapiProviderRegistry.set(provider.api, {", '): void {\n\t__pisharpNotifyHost("registerApiProvider", provider, sourceId);\n\tapiProviderRegistry.set(provider.api, {'],
		["export function unregisterApiProviders(sourceId: string): void {", 'export function unregisterApiProviders(sourceId: string): void {\n\t__pisharpNotifyHost("unregisterApiProviders", sourceId);'],
	],
	"ai/src/api/bedrock-converse-stream.lazy.ts": [
		['(await importNodeOnlyApi("./bedrock-converse-stream.ts"))', '(await import("./bedrock-converse-stream.ts"))'],
	],
	"coding-agent/src/config.ts": [
		["let pkg: PackageJson = {};", 'let pkg: PackageJson = { name: "@earendil-works/pi-coding-agent", version: "1.1.0" };'],
		[
			'try {\n\tconst packageJsonPath = getPackageJsonPath();\n\tpkg = JSON.parse(stripBom(readFileSync(packageJsonPath, "utf-8"))) as PackageJson;\n\tstartupPackageJsonPath = packageJsonPath;\n} catch (e: unknown) {\n\tconst err = e as NodeJS.ErrnoException;\n\tif (err.code !== "ENOENT") throw e;\n}',
			"// PiSharp: the Node bridge ships no Pi package.json; it identifies as the ported Pi 1.1.0.\nstartupPackageJsonPath = undefined;",
		],
	],
	"coding-agent/src/modes/interactive/theme/theme.ts": [
		[
			'\t\tconst themesDir = getThemesDir();\n\t\tconst darkPath = path.join(themesDir, "dark.json");\n\t\tconst lightPath = path.join(themesDir, "light.json");\n\t\tBUILTIN_THEMES = {\n\t\t\tdark: JSON.parse(stripBom(fs.readFileSync(darkPath, "utf-8"))) as ThemeJson,\n\t\t\tlight: JSON.parse(stripBom(fs.readFileSync(lightPath, "utf-8"))) as ThemeJson,\n\t\t};',
			"\t\t// PiSharp: built-in theme JSON is embedded (copied from upstream dark.json/light.json).\n\t\tBUILTIN_THEMES = {\n\t\t\tdark: structuredClone(PISHARP_BUILTIN_DARK_THEME) as ThemeJson,\n\t\t\tlight: structuredClone(PISHARP_BUILTIN_LIGHT_THEME) as ThemeJson,\n\t\t};",
		],
		["createTheme(", "createThemeFromJson("],
		[
			"export function getMarkdownTheme(): MarkdownTheme {\n\treturn {",
			"export function getMarkdownTheme(themeOverride?: Theme): MarkdownTheme {\n\tconst theme = themeOverride ?? globalThemeProxy;\n\treturn {",
		],
		[
			"export function getSelectListTheme(): SelectListTheme {\n\treturn {",
			"export function getSelectListTheme(themeOverride?: Theme): SelectListTheme {\n\tconst theme = themeOverride ?? globalThemeProxy;\n\treturn {",
		],
		[
			"export function getEditorTheme(): EditorTheme {\n\treturn {\n\t\tborderColor: (text: string) => theme.fg(\"borderMuted\", text),\n\t\tselectList: getSelectListTheme(),",
			"export function getEditorTheme(themeOverride?: Theme): EditorTheme {\n\tconst theme = themeOverride ?? globalThemeProxy;\n\treturn {\n\t\tborderColor: (text: string) => theme.fg(\"borderMuted\", text),\n\t\tselectList: getSelectListTheme(themeOverride),",
		],
		[
			"export function getSettingsListTheme(): SettingsListTheme {\n\treturn {",
			"export function getSettingsListTheme(themeOverride?: Theme): SettingsListTheme {\n\tconst theme = themeOverride ?? globalThemeProxy;\n\treturn {",
		],
	],
};

const appendConfig = {
	"ai/src/compat.ts": `// ---------------------------------------------------------------------------------------------
// PiSharp adaptations: builtin API providers are host-backed. Calling stream/complete for them without
// the host bridge hook throws "<name> is not available in the PiSharp Node bridge".
import { getBridge as __pisharpGetBridge, hostFunction as __pisharpHostFunction, unavailable as __pisharpUnavailable } from "./bridge.mjs";
function __pisharpRequireHost(name, api) {
	const provider = getApiProvider(api);
	if (provider && provider === builtinApiProviderInstances.get(api) && !__pisharpGetBridge()) throw __pisharpUnavailable(name);
}
// Upstream's provider composer falls back to this registry for the agent's own model streams, so
// extension registrations are mirrored to the PiSharp host (the builtin host-backed APIs are not).
function __pisharpNotifyHost(name, ...args) {
	const bridge = __pisharpGetBridge();
	if (!bridge) return;
	if (name === "registerApiProvider" && BUILTIN_APIS.some(([, streams]) => streams.stream === args[0].stream)) return;
	bridge.call(name, ...args);
}`,
	"coding-agent/src/modes/interactive/theme/theme.ts": `// ---------------------------------------------------------------------------------------------
// PiSharp adaptations.
const globalThemeProxy = theme;

/** PiSharp: create a Theme from a theme name (built-in/custom/registered) or a theme JSON document. */
export function createTheme(nameOrThemeJson, colorMode) {
	if (typeof nameOrThemeJson === "string") return loadTheme(nameOrThemeJson, colorMode);
	return createThemeFromJson(parseThemeJson("<inline>", nameOrThemeJson), colorMode);
}

export { createThemeFromJson, loadTheme };

const PISHARP_BUILTIN_DARK_THEME = ${JSON.stringify(dark, null, "\t")};

const PISHARP_BUILTIN_LIGHT_THEME = ${JSON.stringify(light, null, "\t")};

// PiSharp: interactive Pi initializes the theme at startup; the bridge does it on first load so
// extension render code (theme.fg, getMarkdownTheme(), ...) works. PISHARP_THEME selects a theme.
if (!globalThis[THEME_KEY]) initTheme(process.env.PISHARP_THEME || undefined);`,
};

const result = run(
	[
		["tui/src/index.ts", "pi-tui.mjs"],
		["ai/src/index.ts", "ai/index.mjs"],
		["ai/src/compat.ts", "pi-ai.mjs"],
		["agent/src/index.ts", "pi-agent-core.mjs"],
		["coding-agent/src/modes/interactive/theme/theme.ts", "theme.mjs"],
		["coding-agent/src/config.ts"],
		["coding-agent/src/core/extensions/types.ts"],
		["coding-agent/src/core/extensions/wrapper.ts"],
		["coding-agent/src/core/messages.ts"],
		["coding-agent/src/core/compaction/index.ts"],
		["coding-agent/src/core/event-bus.ts"],
		["coding-agent/src/core/source-info.ts"],
		["coding-agent/src/core/virtual-models.ts"],
		["coding-agent/src/core/keybindings.ts"],
		["coding-agent/src/core/skills.ts"],
		["coding-agent/src/core/tools/truncate.ts"],
		["coding-agent/src/core/tools/file-mutation-queue.ts"],
		["coding-agent/src/core/tools/tool-definition-wrapper.ts"],
		["coding-agent/src/utils/frontmatter.ts"],
		["coding-agent/src/utils/mime.ts"],
		["coding-agent/src/utils/shell.ts"],
		["coding-agent/src/modes/interactive/components/dynamic-border.ts"],
		["coding-agent/src/modes/interactive/components/bordered-loader.ts"],
		["coding-agent/src/modes/interactive/components/custom-editor.ts"],
		["coding-agent/src/modes/interactive/components/keybinding-hints.ts"],
		["coding-agent/src/modes/interactive/components/visual-truncate.ts"],
	],
	{ redirects: redirectConfig, patches: patchConfig, appends: appendConfig },
);
console.log(`written ${result.written.length}`);
console.log(result.written.join("\n"));
console.log("UNRESOLVED:\n" + result.unresolved.join("\n"));
