// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/theme/theme.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged except for the marked PiSharp adaptations).
import * as fs from "node:fs";
import * as path from "node:path";
import { backgroundAnsi, colorToHex, colorToOklch, foregroundAnsi, getTerminalColorMode, indexedColor, mixColors, parseColor, rgbColor, styleTextWithAnsi } from "./pi-tui.mjs";
import chalk from "./vendor/chalk.mjs";
import { getCustomThemesDir, getThemesDir } from "./coding-agent/config.mjs";
import { closeWatcher, watchWithErrorHandler } from "./coding-agent/utils/fs-watch.mjs";
import { highlight, supportsLanguage } from "./coding-agent/syntax-highlight.mjs";
import { stripBom } from "./coding-agent/utils/text.mjs";
import { generateSystemThemeColors, SYSTEM_THEME_NAME, terminalAppearance } from "./coding-agent/modes/interactive/theme/system-theme.mjs";
export { SYSTEM_THEME_NAME } from "./coding-agent/modes/interactive/theme/system-theme.mjs";
let themeJsonValidator;
export function setThemeJsonValidator(validator) {
    themeJsonValidator = validator;
}
function resolveVarRefs(value, vars, visited = new Set()) {
    if (typeof value === "number" || value === "" || value.startsWith("#") || /^ok(lch|hsl)\(/i.test(value)) {
        return value;
    }
    if (visited.has(value)) {
        throw new Error(`Circular variable reference detected: ${value}`);
    }
    if (!(value in vars)) {
        throw new Error(`Variable reference not found: ${value}`);
    }
    visited.add(value);
    return resolveVarRefs(vars[value], vars, visited);
}
function resolveThemeColors(colors, vars = {}) {
    const resolved = {};
    for (const [key, value] of Object.entries(colors)){
        resolved[key] = resolveVarRefs(value, vars);
    }
    return resolved;
}
function withThemeColorFallbacks(colors) {
    return {
        ...colors,
        scrollbarTrack: colors.scrollbarTrack ?? colors.muted,
        scrollbarThumb: colors.scrollbarThumb ?? colors.text,
        thinkingMax: colors.thinkingMax ?? colors.thinkingXhigh,
        searchMatchBg: colors.searchMatchBg ?? colors.selectedBg,
        searchMatchText: colors.searchMatchText ?? colors.text
    };
}
let terminalColors = {};
let terminalColorsPending = false;
let terminalColorScheme;
export function setTerminalColors(colors) {
    terminalColors = {
        ...colors
    };
    terminalColorsPending = false;
}
export function setTerminalColorScheme(scheme) {
    terminalColorScheme = scheme;
}
export function markTerminalColorsPending() {
    terminalColorsPending = true;
}
const GUESSED_DEFAULT_COLORS = {
    dark: {
        foreground: parseColor("#e5e5e7"),
        background: parseColor("#000000")
    },
    light: {
        foreground: parseColor("#000000"),
        background: parseColor("#ffffff")
    }
};
function averageLightness(colors) {
    const fixed = colors.filter((color)=>color.kind !== "indexed" || color.index >= 16);
    if (fixed.length === 0) return undefined;
    return fixed.reduce((sum, color)=>sum + colorToOklch(color).l, 0) / fixed.length;
}
function detectAppearance(foregrounds, backgrounds) {
    const fg = averageLightness(foregrounds);
    const bg = averageLightness(backgrounds);
    if (fg !== undefined && bg !== undefined) return bg < fg ? "dark" : "light";
    if (bg !== undefined) return bg < 0.5 ? "dark" : "light";
    if (fg !== undefined) return fg > 0.5 ? "dark" : "light";
    return undefined;
}
export class Theme {
    name;
    sourcePath;
    sourceInfo;
    mode;
    fgAnsi = new Map();
    bgAnsi = new Map();
    concreteColors = {};
    defaultForegroundTokens = [];
    defaultBackgroundTokens = [];
    dimTokens;
    ownAppearance;
    resolvedColors;
    constructor(fgColors, bgColors, mode, options = {}){
        this.name = options.name;
        this.sourcePath = options.sourcePath;
        this.sourceInfo = options.sourceInfo;
        this.mode = mode;
        this.dimTokens = new Set(options.dim);
        const foregrounds = {
            ...fgColors,
            scrollbarTrack: fgColors.scrollbarTrack ?? fgColors.muted,
            scrollbarThumb: fgColors.scrollbarThumb ?? fgColors.text,
            thinkingMax: fgColors.thinkingMax ?? fgColors.thinkingXhigh,
            searchMatchText: fgColors.searchMatchText ?? fgColors.text
        };
        const backgrounds = {
            ...bgColors,
            searchMatchBg: bgColors.searchMatchBg ?? bgColors.selectedBg
        };
        const concreteForegrounds = [];
        const concreteBackgrounds = [];
        const addToken = (token, value, isBackground)=>{
            if (value === "") {
                (isBackground ? this.defaultBackgroundTokens : this.defaultForegroundTokens).push(token);
                return isBackground ? "\x1b[49m" : "\x1b[39m";
            }
            const color = parseColor(value);
            this.concreteColors[token] = color;
            (isBackground ? concreteBackgrounds : concreteForegrounds).push(color);
            return isBackground ? backgroundAnsi(color, mode) : foregroundAnsi(color, mode);
        };
        for (const [token, value] of Object.entries(foregrounds)){
            this.fgAnsi.set(token, addToken(token, value, false));
        }
        for (const [token, value] of Object.entries(backgrounds)){
            this.bgAnsi.set(token, addToken(token, value, true));
        }
        this.ownAppearance = options.appearance ?? detectAppearance(concreteForegrounds, concreteBackgrounds);
    }
    get appearance() {
        return this.ownAppearance ?? getTerminalTheme();
    }
    get colors() {
        const terminal = terminalColors;
        if (this.resolvedColors?.terminal !== terminal) {
            const guess = GUESSED_DEFAULT_COLORS[this.appearance];
            const toColor = (rgb, fallback)=>rgb ? rgbColor(rgb.r, rgb.g, rgb.b) : fallback;
            const foreground = toColor(terminal.foreground, guess.foreground);
            const background = toColor(terminal.background, guess.background);
            const colors = {
                ...this.concreteColors
            };
            for (const token of this.defaultForegroundTokens)colors[token] = foreground;
            for (const token of this.defaultBackgroundTokens)colors[token] = background;
            for (const token of this.dimTokens){
                const color = colors[token];
                if (color) colors[token] = mixColors(color, background, 0.4);
            }
            this.resolvedColors = {
                terminal,
                colors: Object.freeze(colors)
            };
        }
        return this.resolvedColors.colors;
    }
    style(text, options) {
        const { fg, bg } = options;
        if (typeof fg === "string" && this.dimTokens.has(fg)) options = {
            ...options,
            dim: true
        };
        return styleTextWithAnsi(text, fg === undefined ? undefined : typeof fg === "string" ? this.tokenAnsi(this.fgAnsi, fg) : foregroundAnsi(fg, this.mode), bg === undefined ? undefined : typeof bg === "string" ? this.tokenAnsi(this.bgAnsi, bg) : backgroundAnsi(bg, this.mode), options);
    }
    fg(color, text) {
        const ansi = this.tokenAnsi(this.fgAnsi, color);
        if (this.dimTokens.has(color)) return `${ansi}\x1b[2m${text}\x1b[22;39m`;
        return `${ansi}${text}\x1b[39m`;
    }
    bg(color, text) {
        const ansi = this.tokenAnsi(this.bgAnsi, color);
        return `${ansi}${text}\x1b[49m`;
    }
    tokenAnsi(ansi, token) {
        const value = ansi.get(token);
        if (value === undefined) throw new Error(`Unknown theme color: ${token}`);
        return value;
    }
    bold(text) {
        return chalk.bold(text);
    }
    italic(text) {
        return chalk.italic(text);
    }
    underline(text) {
        return chalk.underline(text);
    }
    inverse(text) {
        return chalk.inverse(text);
    }
    strikethrough(text) {
        return chalk.strikethrough(text);
    }
    getFgAnsi(color) {
        const ansi = this.tokenAnsi(this.fgAnsi, color);
        return this.dimTokens.has(color) ? `${ansi}\x1b[2m` : ansi;
    }
    getBgAnsi(color) {
        return this.tokenAnsi(this.bgAnsi, color);
    }
    getColorMode() {
        return this.mode;
    }
    getThinkingBorderColor(level) {
        switch(level){
            case "off":
                return (str)=>this.fg("thinkingOff", str);
            case "minimal":
                return (str)=>this.fg("thinkingMinimal", str);
            case "low":
                return (str)=>this.fg("thinkingLow", str);
            case "medium":
                return (str)=>this.fg("thinkingMedium", str);
            case "high":
                return (str)=>this.fg("thinkingHigh", str);
            case "xhigh":
                return (str)=>this.fg("thinkingXhigh", str);
            case "max":
                return (str)=>this.fg("thinkingMax", str);
            default:
                return (str)=>this.fg("thinkingOff", str);
        }
    }
    getBashModeBorderColor() {
        return (str)=>this.fg("bashMode", str);
    }
}
let BUILTIN_THEMES;
function getBuiltinThemes() {
    if (!BUILTIN_THEMES) {
        BUILTIN_THEMES = {
            dark: structuredClone(PISHARP_BUILTIN_DARK_THEME),
            light: structuredClone(PISHARP_BUILTIN_LIGHT_THEME)
        };
    }
    return BUILTIN_THEMES;
}
export function getAvailableThemes() {
    return getAvailableThemesWithPaths().map(({ name })=>name);
}
export function getAvailableThemesWithPaths() {
    const themesDir = getThemesDir();
    const result = [];
    const seen = new Set();
    const addTheme = (themeInfo)=>{
        if (seen.has(themeInfo.name)) {
            return;
        }
        seen.add(themeInfo.name);
        result.push(themeInfo);
    };
    addTheme({
        name: SYSTEM_THEME_NAME,
        path: undefined
    });
    for (const name of Object.keys(getBuiltinThemes())){
        addTheme({
            name,
            path: path.join(themesDir, `${name}.json`)
        });
    }
    for (const themeInfo of getCustomThemeInfos()){
        addTheme(themeInfo);
    }
    for (const [name, theme] of registeredThemes.entries()){
        addTheme({
            name,
            path: theme.sourcePath
        });
    }
    return result.sort((a, b)=>a.name === SYSTEM_THEME_NAME ? -1 : b.name === SYSTEM_THEME_NAME ? 1 : a.name.localeCompare(b.name));
}
function getCustomThemeInfos() {
    const customThemesDir = getCustomThemesDir();
    const result = [];
    if (!fs.existsSync(customThemesDir)) {
        return result;
    }
    for (const file of fs.readdirSync(customThemesDir)){
        if (!file.endsWith(".json")) {
            continue;
        }
        const themePath = path.join(customThemesDir, file);
        try {
            const customTheme = loadThemeFromPath(themePath);
            if (customTheme.name) {
                result.push({
                    name: customTheme.name,
                    path: themePath
                });
            }
        } catch  {}
    }
    return result;
}
function assertThemeNameIsValid(name) {
    if (name.includes("/")) {
        throw new Error(`Invalid theme name "${name}": theme names cannot contain "/" because it is reserved for automatic light/dark theme settings.`);
    }
}
function parseThemeJson(label, json) {
    if (themeJsonValidator) return themeJsonValidator(label, json);
    if (typeof json !== "object" || json === null || !("colors" in json)) {
        throw new Error(`Invalid theme "${label}": expected an object with a "colors" map.`);
    }
    return json;
}
function parseThemeJsonContent(label, content) {
    let json;
    try {
        json = JSON.parse(stripBom(content));
    } catch (error) {
        throw new Error(`Failed to parse theme ${label}: ${error}`);
    }
    return parseThemeJson(label, json);
}
function loadThemeJson(name) {
    const builtinThemes = getBuiltinThemes();
    if (name in builtinThemes) {
        return builtinThemes[name];
    }
    const registeredTheme = registeredThemes.get(name);
    if (registeredTheme?.sourcePath) {
        const content = fs.readFileSync(registeredTheme.sourcePath, "utf-8");
        return parseThemeJsonContent(registeredTheme.sourcePath, content);
    }
    if (registeredTheme) {
        throw new Error(`Theme "${name}" does not have a source path for export`);
    }
    const customThemesDir = getCustomThemesDir();
    const themePath = path.join(customThemesDir, `${name}.json`);
    if (!fs.existsSync(themePath)) {
        throw new Error(`Theme not found: ${name}`);
    }
    const content = fs.readFileSync(themePath, "utf-8");
    return parseThemeJsonContent(name, content);
}
const BACKGROUND_TOKENS = new Set([
    "selectedBg",
    "searchMatchBg",
    "userMessageBg",
    "customMessageBg",
    "toolPendingBg",
    "toolSuccessBg",
    "toolErrorBg"
]);
function splitThemeColors(colors) {
    const fgColors = {};
    const bgColors = {};
    for (const [key, value] of Object.entries(colors)){
        if (BACKGROUND_TOKENS.has(key)) {
            bgColors[key] = value;
        } else {
            fgColors[key] = value;
        }
    }
    return {
        fgColors,
        bgColors
    };
}
function createThemeFromJson(themeJson, mode, sourcePath) {
    const colorMode = mode ?? getTerminalColorMode();
    const resolvedColors = resolveThemeColors(withThemeColorFallbacks(themeJson.colors), themeJson.vars);
    const { fgColors, bgColors } = splitThemeColors(resolvedColors);
    return new Theme(fgColors, bgColors, colorMode, {
        name: themeJson.name,
        sourcePath,
        appearance: themeJson.appearance
    });
}
function createSystemTheme(mode) {
    const generated = generateSystemThemeColors({
        ...terminalColors,
        saturation: terminalColorsPending ? 0 : 1,
        appearanceHint: getTerminalTheme()
    });
    const { fgColors, bgColors } = splitThemeColors(generated.colors);
    return new Theme(fgColors, bgColors, mode ?? getTerminalColorMode(), {
        name: SYSTEM_THEME_NAME,
        appearance: generated.appearance,
        dim: generated.dim
    });
}
export function loadThemeFromPath(themePath, mode) {
    const content = fs.readFileSync(themePath, "utf-8");
    const themeJson = parseThemeJsonContent(themePath, content);
    return createThemeFromJson(themeJson, mode, themePath);
}
function loadTheme(name, mode) {
    if (name === SYSTEM_THEME_NAME) return createSystemTheme(mode);
    const registeredTheme = registeredThemes.get(name);
    if (registeredTheme) {
        return registeredTheme;
    }
    const themeJson = loadThemeJson(name);
    return createThemeFromJson(themeJson, mode);
}
export function getThemeByName(name) {
    try {
        return loadTheme(name);
    } catch  {
        return undefined;
    }
}
export function parseAutoThemeSetting(themeSetting) {
    if (!themeSetting) return undefined;
    const slashIndex = themeSetting.indexOf("/");
    if (slashIndex === -1 || themeSetting.indexOf("/", slashIndex + 1) !== -1) {
        return undefined;
    }
    const lightTheme = themeSetting.slice(0, slashIndex).trim();
    const darkTheme = themeSetting.slice(slashIndex + 1).trim();
    if (!lightTheme || !darkTheme) {
        return undefined;
    }
    return {
        lightTheme,
        darkTheme
    };
}
export function resolveThemeSetting(themeSetting, terminalTheme) {
    const autoTheme = parseAutoThemeSetting(themeSetting);
    if (autoTheme) {
        return terminalTheme === "light" ? autoTheme.lightTheme : autoTheme.darkTheme;
    }
    if (themeSetting?.includes("/")) return undefined;
    if (typeof themeSetting === "string") return themeSetting;
    return undefined;
}
export function detectColorFgBgTheme(env = process.env) {
    const bg = env.COLORFGBG?.split(";").at(-1)?.trim();
    if (!bg || !/^\d{1,2}$/.test(bg)) return undefined;
    const index = Number(bg);
    if (index > 15) return undefined;
    return index <= 6 || index === 8 ? "dark" : "light";
}
export function detectTerminalTheme(colors = {}, reportedScheme, env = process.env) {
    const { background, foreground } = colors;
    if (background) return terminalAppearance(background, foreground);
    return reportedScheme ?? detectColorFgBgTheme(env) ?? "dark";
}
export function getTerminalTheme() {
    return detectTerminalTheme(terminalColors, terminalColorScheme);
}
const THEME_KEY = Symbol.for("@earendil-works/pi-coding-agent:theme");
const THEME_KEY_OLD = Symbol.for("@mariozechner/pi-coding-agent:theme");
export const theme = new Proxy({}, {
    get (_target, prop) {
        const t = globalThis[THEME_KEY];
        if (!t) throw new Error("Theme not initialized. Call initTheme() first.");
        return t[prop];
    }
});
function setGlobalTheme(t) {
    globalThis[THEME_KEY] = t;
    globalThis[THEME_KEY_OLD] = t;
}
let currentThemeName;
let themeWatcher;
let themeReloadTimer;
let onThemeChangeCallback;
const registeredThemes = new Map();
export function setRegisteredThemes(themes) {
    registeredThemes.clear();
    for (const theme of themes){
        if (theme.name) {
            assertThemeNameIsValid(theme.name);
            registeredThemes.set(theme.name, theme);
        }
    }
}
export function initTheme(themeName, enableWatcher = false) {
    const name = themeName ?? SYSTEM_THEME_NAME;
    currentThemeName = name;
    try {
        setGlobalTheme(loadTheme(name));
        if (enableWatcher) {
            startThemeWatcher();
        }
    } catch (_error) {
        currentThemeName = SYSTEM_THEME_NAME;
        setGlobalTheme(loadTheme(SYSTEM_THEME_NAME));
    }
}
export function setTheme(name, enableWatcher = false) {
    currentThemeName = name;
    try {
        setGlobalTheme(loadTheme(name));
        if (enableWatcher) {
            startThemeWatcher();
        }
        if (onThemeChangeCallback) {
            onThemeChangeCallback();
        }
        return {
            success: true
        };
    } catch (error) {
        currentThemeName = SYSTEM_THEME_NAME;
        setGlobalTheme(loadTheme(SYSTEM_THEME_NAME));
        return {
            success: false,
            error: error instanceof Error ? error.message : String(error)
        };
    }
}
export function setThemeInstance(themeInstance) {
    setGlobalTheme(themeInstance);
    currentThemeName = "<in-memory>";
    stopThemeWatcher();
    if (onThemeChangeCallback) {
        onThemeChangeCallback();
    }
}
export function onThemeChange(callback) {
    onThemeChangeCallback = callback;
}
function startThemeWatcher() {
    stopThemeWatcher();
    if (!currentThemeName || currentThemeName === "dark" || currentThemeName === "light" || currentThemeName === SYSTEM_THEME_NAME) {
        return;
    }
    const customThemesDir = getCustomThemesDir();
    const watchedThemeName = currentThemeName;
    const watchedFileName = `${watchedThemeName}.json`;
    const themeFile = path.join(customThemesDir, watchedFileName);
    if (!fs.existsSync(themeFile)) {
        return;
    }
    const scheduleReload = ()=>{
        if (themeReloadTimer) {
            clearTimeout(themeReloadTimer);
        }
        themeReloadTimer = setTimeout(()=>{
            themeReloadTimer = undefined;
            if (currentThemeName !== watchedThemeName) {
                return;
            }
            if (!fs.existsSync(themeFile)) {
                return;
            }
            try {
                const reloadedTheme = loadThemeFromPath(themeFile);
                registeredThemes.set(watchedThemeName, reloadedTheme);
                setGlobalTheme(reloadedTheme);
                if (onThemeChangeCallback) {
                    onThemeChangeCallback();
                }
            } catch (_error) {}
        }, 100);
    };
    themeWatcher = watchWithErrorHandler(customThemesDir, (_eventType, filename)=>{
        if (currentThemeName !== watchedThemeName) {
            return;
        }
        if (!filename) {
            scheduleReload();
            return;
        }
        if (filename !== watchedFileName) {
            return;
        }
        scheduleReload();
    }, ()=>{
        closeWatcher(themeWatcher);
        themeWatcher = undefined;
    }) ?? undefined;
}
export function stopThemeWatcher() {
    if (themeReloadTimer) {
        clearTimeout(themeReloadTimer);
        themeReloadTimer = undefined;
    }
    closeWatcher(themeWatcher);
    themeWatcher = undefined;
}
export function getResolvedThemeColors(themeName) {
    const colors = loadTheme(themeName ?? currentThemeName ?? SYSTEM_THEME_NAME).colors;
    return Object.fromEntries(Object.entries(colors).map(([token, color])=>[
            token,
            colorToHex(color)
        ]));
}
export function isLightTheme(themeName) {
    return loadTheme(themeName ?? currentThemeName ?? SYSTEM_THEME_NAME).appearance === "light";
}
export function getThemeExportColors(themeName) {
    const name = themeName ?? currentThemeName ?? SYSTEM_THEME_NAME;
    if (name === SYSTEM_THEME_NAME) return {};
    try {
        const themeJson = loadThemeJson(name);
        const exportSection = themeJson.export;
        if (!exportSection) return {};
        const vars = themeJson.vars ?? {};
        const resolve = (value)=>{
            if (value === undefined) return undefined;
            const resolved = resolveVarRefs(value, vars);
            if (typeof resolved === "number") return colorToHex(indexedColor(resolved));
            if (resolved === "") return undefined;
            if (/^okhsl\(/i.test(resolved)) return colorToHex(parseColor(resolved));
            return resolved;
        };
        return {
            pageBg: resolve(exportSection.pageBg),
            cardBg: resolve(exportSection.cardBg),
            infoBg: resolve(exportSection.infoBg)
        };
    } catch  {
        return {};
    }
}
let cachedHighlightThemeFor;
let cachedCliHighlightTheme;
function buildCliHighlightTheme(t) {
    return {
        keyword: (s)=>t.fg("syntaxKeyword", s),
        built_in: (s)=>t.fg("syntaxType", s),
        literal: (s)=>t.fg("syntaxNumber", s),
        number: (s)=>t.fg("syntaxNumber", s),
        regexp: (s)=>t.fg("syntaxString", s),
        string: (s)=>t.fg("syntaxString", s),
        subst: (s)=>t.fg("text", s),
        comment: (s)=>t.fg("syntaxComment", s),
        doctag: (s)=>t.fg("syntaxComment", s),
        meta: (s)=>t.fg("muted", s),
        function: (s)=>t.fg("syntaxFunction", s),
        title: (s)=>t.fg("syntaxFunction", s),
        class: (s)=>t.fg("syntaxType", s),
        type: (s)=>t.fg("syntaxType", s),
        tag: (s)=>t.fg("syntaxPunctuation", s),
        name: (s)=>t.fg("syntaxKeyword", s),
        attr: (s)=>t.fg("syntaxVariable", s),
        variable: (s)=>t.fg("syntaxVariable", s),
        params: (s)=>t.fg("syntaxVariable", s),
        operator: (s)=>t.fg("syntaxOperator", s),
        punctuation: (s)=>t.fg("syntaxPunctuation", s),
        emphasis: (s)=>t.italic(s),
        strong: (s)=>t.bold(s),
        link: (s)=>t.underline(s),
        addition: (s)=>t.fg("toolDiffAdded", s),
        deletion: (s)=>t.fg("toolDiffRemoved", s)
    };
}
function getCliHighlightTheme(t) {
    if (cachedHighlightThemeFor !== t || !cachedCliHighlightTheme) {
        cachedHighlightThemeFor = t;
        cachedCliHighlightTheme = buildCliHighlightTheme(t);
    }
    return cachedCliHighlightTheme;
}
export function highlightCode(code, lang) {
    const validLang = lang && supportsLanguage(lang) ? lang : undefined;
    if (!validLang) {
        return code.split("\n").map((line)=>theme.fg("mdCodeBlock", line));
    }
    const opts = {
        language: validLang,
        ignoreIllegals: true,
        theme: getCliHighlightTheme(theme)
    };
    try {
        return highlight(code, opts).split("\n");
    } catch  {
        return code.split("\n");
    }
}
export function getLanguageFromPath(filePath) {
    const ext = filePath.split(".").pop()?.toLowerCase();
    if (!ext) return undefined;
    const extToLang = {
        ts: "typescript",
        tsx: "typescript",
        js: "javascript",
        jsx: "javascript",
        mjs: "javascript",
        cjs: "javascript",
        py: "python",
        rb: "ruby",
        rs: "rust",
        go: "go",
        java: "java",
        kt: "kotlin",
        swift: "swift",
        c: "c",
        h: "c",
        cpp: "cpp",
        cc: "cpp",
        cxx: "cpp",
        hpp: "cpp",
        cs: "csharp",
        php: "php",
        sh: "bash",
        bash: "bash",
        zsh: "bash",
        fish: "fish",
        ps1: "powershell",
        sql: "sql",
        html: "html",
        htm: "html",
        css: "css",
        scss: "scss",
        sass: "sass",
        less: "less",
        json: "json",
        yaml: "yaml",
        yml: "yaml",
        toml: "toml",
        xml: "xml",
        md: "markdown",
        markdown: "markdown",
        dockerfile: "dockerfile",
        makefile: "makefile",
        cmake: "cmake",
        lua: "lua",
        perl: "perl",
        r: "r",
        scala: "scala",
        clj: "clojure",
        ex: "elixir",
        exs: "elixir",
        erl: "erlang",
        hs: "haskell",
        ml: "ocaml",
        vim: "vim",
        graphql: "graphql",
        proto: "protobuf",
        tf: "hcl",
        hcl: "hcl"
    };
    return extToLang[ext];
}
export function getMarkdownTheme(themeOverride) {
    const theme = themeOverride ?? globalThemeProxy;
    return {
        heading: (text)=>theme.fg("mdHeading", text),
        link: (text)=>theme.fg("mdLink", text),
        linkUrl: (text)=>theme.fg("mdLinkUrl", text),
        code: (text)=>theme.fg("mdCode", text),
        codeBlock: (text)=>theme.fg("mdCodeBlock", text),
        codeBlockBorder: (text)=>theme.fg("mdCodeBlockBorder", text),
        quote: (text)=>theme.fg("mdQuote", text),
        quoteBorder: (text)=>theme.fg("mdQuoteBorder", text),
        hr: (text)=>theme.fg("mdHr", text),
        listBullet: (text)=>theme.fg("mdListBullet", text),
        bold: (text)=>theme.bold(text),
        italic: (text)=>theme.italic(text),
        underline: (text)=>theme.underline(text),
        strikethrough: (text)=>theme.strikethrough(text),
        highlightCode: (code, lang)=>{
            const validLang = lang && supportsLanguage(lang) ? lang : undefined;
            if (!validLang) {
                return code.split("\n").map((line)=>theme.fg("mdCodeBlock", line));
            }
            const opts = {
                language: validLang,
                ignoreIllegals: true,
                theme: getCliHighlightTheme(theme)
            };
            try {
                return highlight(code, opts).split("\n");
            } catch  {
                return code.split("\n").map((line)=>theme.fg("mdCodeBlock", line));
            }
        }
    };
}
export function getSelectListTheme(themeOverride) {
    const theme = themeOverride ?? globalThemeProxy;
    return {
        selectedPrefix: (text)=>theme.fg("accent", text),
        selectedText: (text)=>theme.fg("accent", text),
        description: (text)=>theme.fg("muted", text),
        scrollInfo: (text)=>theme.fg("muted", text),
        noMatch: (text)=>theme.fg("muted", text)
    };
}
export function getEditorTheme(themeOverride) {
    const theme = themeOverride ?? globalThemeProxy;
    return {
        borderColor: (text)=>theme.fg("borderMuted", text),
        selectList: getSelectListTheme(themeOverride)
    };
}
export function getSettingsListTheme(themeOverride) {
    const theme = themeOverride ?? globalThemeProxy;
    return {
        label: (text, selected)=>selected ? theme.fg("accent", text) : text,
        value: (text, selected)=>selected ? theme.fg("accent", text) : theme.fg("muted", text),
        description: (text)=>theme.fg("dim", text),
        cursor: theme.fg("accent", "→ "),
        hint: (text)=>theme.fg("dim", text)
    };
}

// ---------------------------------------------------------------------------------------------
// PiSharp adaptations.
const globalThemeProxy = theme;

/** PiSharp: create a Theme from a theme name (built-in/custom/registered) or a theme JSON document. */
export function createTheme(nameOrThemeJson, colorMode) {
	if (typeof nameOrThemeJson === "string") return loadTheme(nameOrThemeJson, colorMode);
	return createThemeFromJson(parseThemeJson("<inline>", nameOrThemeJson), colorMode);
}

export { createThemeFromJson, loadTheme };

const PISHARP_BUILTIN_DARK_THEME = {
	"$schema": "https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/src/modes/interactive/theme/theme-schema.json",
	"name": "dark",
	"appearance": "dark",
	"vars": {
		"text": "okhsl(234 3% 89%)",
		"muted": "okhsl(229 6% 67%)",
		"violet": "okhsl(295 50% 67%)",
		"blue": "okhsl(232 54% 67%)",
		"green": "okhsl(159 59% 67%)",
		"red": "okhsl(20 72% 67%)",
		"yellow": "okhsl(83 88% 67%)",
		"blueBg": "okhsl(233 41% 24%)"
	},
	"colors": {
		"accent": "violet",
		"border": "okhsl(231 57% 65%)",
		"borderAccent": "okhsl(295 53% 64%)",
		"borderMuted": "okhsl(229 8% 53%)",
		"success": "green",
		"error": "red",
		"warning": "yellow",
		"muted": "muted",
		"dim": "okhsl(229 8% 56%)",
		"text": "text",
		"thinkingText": "okhsl(226 7% 65%)",
		"selectedBg": "blueBg",
		"scrollbarTrack": "okhsl(237 7% 33%)",
		"scrollbarThumb": "okhsl(232 7% 65%)",
		"searchMatchBg": "okhsl(53 51% 24%)",
		"searchMatchText": "muted",
		"userMessageBg": "blueBg",
		"userMessageText": "text",
		"customMessageBg": "okhsl(295 42% 24%)",
		"customMessageText": "muted",
		"customMessageLabel": "violet",
		"toolPendingBg": "okhsl(229 5% 24%)",
		"toolSuccessBg": "okhsl(158 46% 25%)",
		"toolErrorBg": "okhsl(19 54% 25%)",
		"toolTitle": "text",
		"toolOutput": "muted",
		"mdHeading": "yellow",
		"mdLink": "blue",
		"mdLinkUrl": "muted",
		"mdCode": "violet",
		"mdCodeBlock": "green",
		"mdCodeBlockBorder": "muted",
		"mdQuote": "muted",
		"mdQuoteBorder": "muted",
		"mdHr": "muted",
		"mdListBullet": "violet",
		"toolDiffAdded": "green",
		"toolDiffRemoved": "red",
		"toolDiffContext": "muted",
		"syntaxComment": "muted",
		"syntaxKeyword": "blue",
		"syntaxFunction": "yellow",
		"syntaxVariable": "okhsl(202 58% 67%)",
		"syntaxString": "okhsl(52 67% 67%)",
		"syntaxNumber": "green",
		"syntaxType": "violet",
		"syntaxOperator": "muted",
		"syntaxPunctuation": "muted",
		"thinkingOff": "okhsl(229 8% 49%)",
		"thinkingMinimal": "okhsl(232 20% 52%)",
		"thinkingLow": "okhsl(232 45% 54%)",
		"thinkingMedium": "okhsl(263 59% 56%)",
		"thinkingHigh": "okhsl(295 73% 59%)",
		"thinkingXhigh": "okhsl(337 81% 61%)",
		"thinkingMax": "okhsl(20 99% 63%)",
		"bashMode": "okhsl(159 64% 65%)"
	},
	"export": {
		"pageBg": "okhsl(262 14% 16%)",
		"cardBg": "okhsl(264 13% 19%)",
		"infoBg": "okhsl(53 51% 24%)"
	}
};

const PISHARP_BUILTIN_LIGHT_THEME = {
	"$schema": "https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/src/modes/interactive/theme/theme-schema.json",
	"name": "light",
	"appearance": "light",
	"vars": {
		"text": "okhsl(225 5% 27%)",
		"muted": "okhsl(229 8% 47%)",
		"violet": "okhsl(295 60% 46%)",
		"blue": "okhsl(231 68% 47%)",
		"green": "okhsl(159 75% 46%)",
		"red": "okhsl(20 91% 47%)",
		"yellow": "okhsl(83 99% 47%)",
		"blueBg": "okhsl(235 19% 91%)"
	},
	"colors": {
		"accent": "violet",
		"border": "okhsl(231 67% 55%)",
		"borderAccent": "okhsl(295 59% 55%)",
		"borderMuted": "okhsl(235 7% 66%)",
		"success": "green",
		"error": "red",
		"warning": "yellow",
		"muted": "muted",
		"dim": "okhsl(229 7% 59%)",
		"text": "text",
		"thinkingText": "okhsl(234 8% 55%)",
		"selectedBg": "blueBg",
		"scrollbarTrack": "okhsl(248 3% 90%)",
		"scrollbarThumb": "okhsl(226 7% 65%)",
		"searchMatchBg": "okhsl(56 22% 91%)",
		"searchMatchText": "muted",
		"userMessageBg": "blueBg",
		"userMessageText": "text",
		"customMessageBg": "okhsl(295 25% 91%)",
		"customMessageText": "muted",
		"customMessageLabel": "violet",
		"toolPendingBg": "okhsl(248 3% 91%)",
		"toolSuccessBg": "okhsl(156 21% 91%)",
		"toolErrorBg": "okhsl(24 23% 91%)",
		"toolTitle": "text",
		"toolOutput": "muted",
		"mdHeading": "yellow",
		"mdLink": "blue",
		"mdLinkUrl": "muted",
		"mdCode": "violet",
		"mdCodeBlock": "green",
		"mdCodeBlockBorder": "muted",
		"mdQuote": "muted",
		"mdQuoteBorder": "muted",
		"mdHr": "muted",
		"mdListBullet": "violet",
		"toolDiffAdded": "green",
		"toolDiffRemoved": "red",
		"toolDiffContext": "muted",
		"syntaxComment": "muted",
		"syntaxKeyword": "blue",
		"syntaxFunction": "yellow",
		"syntaxVariable": "okhsl(203 73% 46%)",
		"syntaxString": "okhsl(52 84% 46%)",
		"syntaxNumber": "green",
		"syntaxType": "violet",
		"syntaxOperator": "muted",
		"syntaxPunctuation": "muted",
		"thinkingOff": "okhsl(223 5% 80%)",
		"thinkingMinimal": "okhsl(229 14% 78%)",
		"thinkingLow": "okhsl(232 33% 76%)",
		"thinkingMedium": "okhsl(264 48% 74%)",
		"thinkingHigh": "okhsl(295 62% 72%)",
		"thinkingXhigh": "okhsl(337 74% 70%)",
		"thinkingMax": "okhsl(20 98% 68%)",
		"bashMode": "okhsl(159 74% 55%)"
	},
	"export": {
		"pageBg": "okhsl(17 3% 94%)",
		"cardBg": "okhsl(17 5% 97%)",
		"infoBg": "okhsl(56 22% 91%)"
	}
};

// PiSharp: interactive Pi initializes the theme at startup; the bridge does it on first load so
// extension render code (theme.fg, getMarkdownTheme(), ...) works. PISHARP_THEME selects a theme.
if (!globalThis[THEME_KEY]) initTheme(process.env.PISHARP_THEME || undefined);
