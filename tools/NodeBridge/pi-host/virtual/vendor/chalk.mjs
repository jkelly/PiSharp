// PiSharp native replacement for the parts of the `chalk` npm package used by Pi (modifiers, basic colors,
// hex/rgb/ansi256). Mirrors chalk's escape codes, nested-close re-opening and per-line wrapping.
// Styling is on by default (the PiSharp host renders ANSI); FORCE_COLOR=0 or NO_COLOR disables it.

const MODIFIERS = {
	reset: [0, 0],
	bold: [1, 22],
	dim: [2, 22],
	italic: [3, 23],
	underline: [4, 24],
	overline: [53, 55],
	inverse: [7, 27],
	hidden: [8, 28],
	strikethrough: [9, 29],
};
const FG = {
	black: 30, red: 31, green: 32, yellow: 33, blue: 34, magenta: 35, cyan: 36, white: 37,
	blackBright: 90, gray: 90, grey: 90, redBright: 91, greenBright: 92, yellowBright: 93, blueBright: 94,
	magentaBright: 95, cyanBright: 96, whiteBright: 97,
};
const BG = {
	bgBlack: 40, bgRed: 41, bgGreen: 42, bgYellow: 43, bgBlue: 44, bgMagenta: 45, bgCyan: 46, bgWhite: 47,
	bgBlackBright: 100, bgGray: 100, bgGrey: 100, bgRedBright: 101, bgGreenBright: 102, bgYellowBright: 103,
	bgBlueBright: 104, bgMagentaBright: 105, bgCyanBright: 106, bgWhiteBright: 107,
};

function detectLevel() {
	const env = typeof process !== "undefined" ? process.env : {};
	if (env.FORCE_COLOR !== undefined) {
		if (env.FORCE_COLOR === "false" || env.FORCE_COLOR === "0") return 0;
		const n = Number.parseInt(env.FORCE_COLOR, 10);
		return Number.isFinite(n) ? Math.min(3, Math.max(1, n)) : 1;
	}
	if (env.NO_COLOR !== undefined && env.NO_COLOR !== "") return 0;
	return 3;
}

function hexToRgb(hex) {
	const match = /[a-f\d]{6}|[a-f\d]{3}/i.exec(String(hex));
	if (!match) return [0, 0, 0];
	let s = match[0];
	if (s.length === 3) s = [...s].map((c) => c + c).join("");
	const n = Number.parseInt(s, 16);
	return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}

function rgbToAnsi256(r, g, b) {
	if (r === g && g === b) {
		if (r < 8) return 16;
		if (r > 248) return 231;
		return Math.round(((r - 8) / 247) * 24) + 232;
	}
	return 16 + 36 * Math.round((r / 255) * 5) + 6 * Math.round((g / 255) * 5) + Math.round((b / 255) * 5);
}

function ansi256ToAnsi(code) {
	if (code < 8) return 30 + code;
	if (code < 16) return 90 + (code - 8);
	let r;
	let g;
	let b;
	if (code >= 232) {
		r = g = b = ((code - 232) * 10 + 8) / 255;
	} else {
		code -= 16;
		const remainder = code % 36;
		r = Math.floor(code / 36) / 5;
		g = Math.floor(remainder / 6) / 5;
		b = (remainder % 6) / 5;
	}
	const value = Math.max(r, g, b) * 2;
	if (value === 0) return 30;
	let result = 30 + ((Math.round(b) << 2) | (Math.round(g) << 1) | Math.round(r));
	if (value === 2) result += 60;
	return result;
}

function colorOpen(level, isBg, r, g, b) {
	if (level >= 3) return `\x1b[${isBg ? 48 : 38};2;${r};${g};${b}m`;
	const code256 = rgbToAnsi256(r, g, b);
	if (level === 2) return `\x1b[${isBg ? 48 : 38};5;${code256}m`;
	return `\x1b[${ansi256ToAnsi(code256) + (isBg ? 10 : 0)}m`;
}

function ansi256Open(level, isBg, code) {
	if (level >= 2) return `\x1b[${isBg ? 48 : 38};5;${code}m`;
	return `\x1b[${ansi256ToAnsi(code) + (isBg ? 10 : 0)}m`;
}

function replaceAll(string, substring, replacer) {
	let index = string.indexOf(substring);
	if (index === -1) return string;
	const substringLength = substring.length;
	let endIndex = 0;
	let returnValue = "";
	do {
		returnValue += string.slice(endIndex, index) + substring + replacer;
		endIndex = index + substringLength;
		index = string.indexOf(substring, endIndex);
	} while (index !== -1);
	returnValue += string.slice(endIndex);
	return returnValue;
}

function encaseCRLF(string, prefix, postfix, index) {
	let endIndex = 0;
	let returnValue = "";
	do {
		const gotCR = string[index - 1] === "\r";
		returnValue += string.slice(endIndex, gotCR ? index - 1 : index) + prefix + (gotCR ? "\r\n" : "\n") + postfix;
		endIndex = index + 1;
		index = string.indexOf("\n", endIndex);
	} while (index !== -1);
	returnValue += string.slice(endIndex);
	return returnValue;
}

function createBuilder(state, styles) {
	const builder = (...args) => apply(builder, args.length === 1 ? `${args[0]}` : args.join(" "));
	builder._styles = styles;
	builder._state = state;
	return new Proxy(builder, handler);
}

function apply(builder, string) {
	const state = builder._state;
	if (state.level <= 0 || !string) return string;
	let openAll = "";
	let closeAll = "";
	for (const style of builder._styles) {
		openAll += style.open;
		closeAll = style.close + closeAll;
	}
	if (string.includes("\x1b")) {
		for (let i = builder._styles.length - 1; i >= 0; i--) {
			const style = builder._styles[i];
			string = replaceAll(string, style.close, style.open);
		}
	}
	const lfIndex = string.indexOf("\n");
	if (lfIndex !== -1) string = encaseCRLF(string, closeAll, openAll, lfIndex);
	return openAll + string + closeAll;
}

const handler = {
	get(target, prop) {
		if (prop === "level") return target._state.level;
		if (prop === "_styles" || prop === "_state") return target[prop];
		if (typeof prop !== "string") return target[prop];
		const state = target._state;
		const styles = target._styles;
		const add = (open, close) => createBuilder(state, [...styles, { open, close }]);
		if (prop in MODIFIERS) {
			const [o, c] = MODIFIERS[prop];
			return add(`\x1b[${o}m`, `\x1b[${c}m`);
		}
		if (prop in FG) return add(`\x1b[${FG[prop]}m`, "\x1b[39m");
		if (prop in BG) return add(`\x1b[${BG[prop]}m`, "\x1b[49m");
		if (prop === "hex") return (hex) => add(colorOpen(state.level, false, ...hexToRgb(hex)), "\x1b[39m");
		if (prop === "bgHex") return (hex) => add(colorOpen(state.level, true, ...hexToRgb(hex)), "\x1b[49m");
		if (prop === "rgb") return (r, g, b) => add(colorOpen(state.level, false, r, g, b), "\x1b[39m");
		if (prop === "bgRgb") return (r, g, b) => add(colorOpen(state.level, true, r, g, b), "\x1b[49m");
		if (prop === "ansi256") return (code) => add(ansi256Open(state.level, false, code), "\x1b[39m");
		if (prop === "bgAnsi256") return (code) => add(ansi256Open(state.level, true, code), "\x1b[49m");
		if (prop === "visible") return add("", "");
		return target[prop];
	},
	set(target, prop, value) {
		if (prop === "level") {
			target._state.level = value;
			return true;
		}
		target[prop] = value;
		return true;
	},
};

export class Chalk {
	constructor(options = {}) {
		return createBuilder({ level: options.level ?? detectLevel() }, []);
	}
}

const chalk = createBuilder({ level: detectLevel() }, []);
export const supportsColor = { level: detectLevel() };
export const chalkStderr = chalk;
export default chalk;
