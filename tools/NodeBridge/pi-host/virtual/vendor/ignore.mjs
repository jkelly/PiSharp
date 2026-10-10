// PiSharp native replacement for the `ignore` npm package (gitignore-style matching) as used by Pi's
// skill discovery: `ignore().add(patterns).ignores(relativePath)`. Supports comments, negation (!),
// anchored (/x) and directory-only (x/) patterns, `*`, `?`, `**` and character classes.

function escapeRegex(text) {
	return text.replace(/[.+^${}()|\\]/g, "\\$&");
}

function globToRegex(glob) {
	let out = "";
	for (let i = 0; i < glob.length; i++) {
		const c = glob[i];
		if (c === "*") {
			if (glob[i + 1] === "*") {
				const atStart = i === 0 || glob[i - 1] === "/";
				const atEnd = i + 2 === glob.length || glob[i + 2] === "/";
				if (atStart && atEnd) {
					out += i + 2 === glob.length ? ".*" : "(?:.*/)?";
					i += glob[i + 2] === "/" ? 2 : 1;
					continue;
				}
				out += ".*";
				i++;
				continue;
			}
			out += "[^/]*";
		} else if (c === "?") {
			out += "[^/]";
		} else if (c === "[") {
			const end = glob.indexOf("]", i + 1);
			if (end === -1) out += "\\[";
			else {
				let cls = glob.slice(i + 1, end);
				if (cls.startsWith("!")) cls = `^${cls.slice(1)}`;
				out += `[${cls.replace(/\\/g, "\\\\")}]`;
				i = end;
			}
		} else if (c === "\\" && i + 1 < glob.length) {
			out += escapeRegex(glob[i + 1]);
			i++;
		} else {
			out += escapeRegex(c);
		}
	}
	return out;
}

function compile(line) {
	let pattern = line.replace(/(?<!\\)\s+$/, "");
	if (!pattern || pattern.startsWith("#")) return null;
	let negative = false;
	if (pattern.startsWith("!")) {
		negative = true;
		pattern = pattern.slice(1);
	}
	if (pattern.startsWith("\\!") || pattern.startsWith("\\#")) pattern = pattern.slice(1);
	let directoryOnly = false;
	if (pattern.endsWith("/")) {
		directoryOnly = true;
		pattern = pattern.slice(0, -1);
	}
	const anchored = pattern.startsWith("/") || pattern.slice(0, -1).includes("/");
	if (pattern.startsWith("/")) pattern = pattern.slice(1);
	const body = globToRegex(pattern);
	const prefix = anchored ? "^" : "^(?:.*/)?";
	// A matching directory also ignores everything beneath it.
	const regex = new RegExp(`${prefix}${body}(?:/.*)?$`);
	const exact = new RegExp(`${prefix}${body}$`);
	return { negative, directoryOnly, regex, exact };
}

class Ignore {
	constructor() {
		this.rules = [];
	}

	add(patterns) {
		if (patterns instanceof Ignore) {
			this.rules.push(...patterns.rules);
			return this;
		}
		const list = Array.isArray(patterns) ? patterns : String(patterns ?? "").split(/\r?\n/);
		for (const item of list) {
			if (item instanceof Ignore) {
				this.rules.push(...item.rules);
				continue;
			}
			for (const line of String(item).split(/\r?\n/)) {
				const rule = compile(line);
				if (rule) this.rules.push(rule);
			}
		}
		return this;
	}

	test(path) {
		const normalized = String(path).replace(/\\/g, "/").replace(/^\.\//, "");
		const isDirPath = normalized.endsWith("/");
		const clean = normalized.replace(/\/+$/, "");
		let ignored = false;
		let unignored = false;
		for (const rule of this.rules) {
			if (rule.negative) {
				if (ignored && rule.exact.test(clean)) {
					ignored = false;
					unignored = true;
				}
				continue;
			}
			let matched = false;
			if (rule.directoryOnly) {
				// x/ matches the directory itself (when the path says so) or anything inside it.
				const inside = rule.regex.test(clean) && !rule.exact.test(clean);
				matched = inside || (isDirPath && rule.exact.test(clean));
			} else {
				matched = rule.regex.test(clean);
			}
			if (matched) {
				ignored = true;
				unignored = false;
			}
		}
		return { ignored, unignored };
	}

	ignores(path) {
		return this.test(path).ignored;
	}

	filter(paths) {
		return paths.filter((path) => !this.ignores(path));
	}

	createFilter() {
		return (path) => !this.ignores(path);
	}
}

export default function ignore(options) {
	const instance = new Ignore(options);
	return instance;
}

export function isPathValid(path) {
	return typeof path === "string" && path.length > 0 && !/^\.{0,2}\/|^[a-zA-Z]:/.test(path);
}
