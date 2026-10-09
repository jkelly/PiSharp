// PiSharp native replacement for the parts of the `yaml` npm package used by Pi (frontmatter parsing):
// `parse(text)` for the YAML 1.2 core-schema subset found in Markdown frontmatter (block mappings and
// sequences, flow collections, plain/quoted/block scalars, comments), and a simple `stringify(value)`.

class YAMLParseError extends Error {
	constructor(message) {
		super(message);
		this.name = "YAMLParseError";
	}
}

function stripComment(line) {
	let inSingle = false;
	let inDouble = false;
	for (let i = 0; i < line.length; i++) {
		const c = line[i];
		if (c === "'" && !inDouble) inSingle = !inSingle;
		else if (c === '"' && !inSingle && line[i - 1] !== "\\") inDouble = !inDouble;
		else if (c === "#" && !inSingle && !inDouble && (i === 0 || /\s/.test(line[i - 1]))) return line.slice(0, i).trimEnd();
	}
	return line.trimEnd();
}

function indentOf(line) {
	return line.length - line.trimStart().length;
}

function parsePlainScalar(text) {
	const value = text.trim();
	if (value === "" || value === "~" || /^(?:null|Null|NULL)$/.test(value)) return null;
	if (/^(?:true|True|TRUE)$/.test(value)) return true;
	if (/^(?:false|False|FALSE)$/.test(value)) return false;
	if (/^[-+]?(?:0|[1-9][0-9_]*)$/.test(value)) return Number(value.replace(/_/g, ""));
	if (/^0x[0-9a-fA-F]+$/.test(value)) return Number.parseInt(value.slice(2), 16);
	if (/^0o[0-7]+$/.test(value)) return Number.parseInt(value.slice(2), 8);
	if (/^[-+]?(?:\.[0-9]+|[0-9][0-9_]*(?:\.[0-9_]*)?)(?:[eE][-+]?[0-9]+)?$/.test(value)) return Number(value.replace(/_/g, ""));
	if (/^[-+]?\.(?:inf|Inf|INF)$/.test(value)) return value.startsWith("-") ? Number.NEGATIVE_INFINITY : Number.POSITIVE_INFINITY;
	if (/^\.(?:nan|NaN|NAN)$/.test(value)) return Number.NaN;
	return value;
}

function parseDoubleQuoted(body) {
	return body.replace(/\\(x[0-9a-fA-F]{2}|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|.)/g, (_m, esc) => {
		switch (esc[0]) {
			case "n":
				return "\n";
			case "t":
				return "\t";
			case "r":
				return "\r";
			case "0":
				return "\0";
			case "b":
				return "\b";
			case "f":
				return "\f";
			case "e":
				return "\x1b";
			case " ":
				return " ";
			case "/":
				return "/";
			case "x":
			case "u":
			case "U":
				return String.fromCodePoint(Number.parseInt(esc.slice(1), 16));
			default:
				return esc;
		}
	});
}

/** Parse a flow value (scalar, quoted string, [..] or {..}) starting at `pos`. Returns { value, end }. */
function parseFlow(text, pos = 0, inFlow = false) {
	while (pos < text.length && /\s/.test(text[pos])) pos++;
	const c = text[pos];
	if (c === "[") {
		const result = [];
		pos++;
		for (;;) {
			while (pos < text.length && /\s/.test(text[pos])) pos++;
			if (text[pos] === "]") return { value: result, end: pos + 1 };
			if (pos >= text.length) throw new YAMLParseError("Unterminated flow sequence");
			const item = parseFlow(text, pos, true);
			result.push(item.value);
			pos = item.end;
			while (pos < text.length && /\s/.test(text[pos])) pos++;
			if (text[pos] === ",") pos++;
		}
	}
	if (c === "{") {
		const result = {};
		pos++;
		for (;;) {
			while (pos < text.length && /\s/.test(text[pos])) pos++;
			if (text[pos] === "}") return { value: result, end: pos + 1 };
			if (pos >= text.length) throw new YAMLParseError("Unterminated flow mapping");
			const key = parseFlow(text, pos, true);
			pos = key.end;
			while (pos < text.length && /\s/.test(text[pos])) pos++;
			let value = null;
			if (text[pos] === ":") {
				const item = parseFlow(text, pos + 1, true);
				value = item.value;
				pos = item.end;
			}
			result[String(key.value)] = value;
			while (pos < text.length && /\s/.test(text[pos])) pos++;
			if (text[pos] === ",") pos++;
		}
	}
	if (c === '"') {
		let end = pos + 1;
		while (end < text.length && !(text[end] === '"' && text[end - 1] !== "\\")) {
			if (text[end] === "\\") end++;
			end++;
		}
		return { value: parseDoubleQuoted(text.slice(pos + 1, end)), end: end + 1 };
	}
	if (c === "'") {
		let end = pos + 1;
		let out = "";
		while (end < text.length) {
			if (text[end] === "'") {
				if (text[end + 1] === "'") {
					out += "'";
					end += 2;
					continue;
				}
				break;
			}
			out += text[end];
			end++;
		}
		return { value: out, end: end + 1 };
	}
	let end = pos;
	if (inFlow) {
		while (end < text.length && !",]}".includes(text[end]) && !(text[end] === ":" && /[\s,\]}]/.test(text[end + 1] ?? " "))) end++;
	} else {
		end = text.length;
	}
	return { value: parsePlainScalar(text.slice(pos, end)), end };
}

function parseScalarValue(text) {
	const trimmed = text.trim();
	if (trimmed.startsWith("[") || trimmed.startsWith("{") || trimmed.startsWith('"') || trimmed.startsWith("'")) {
		return parseFlow(trimmed, 0).value;
	}
	if (trimmed.startsWith("&") || trimmed.startsWith("*") || trimmed.startsWith("!")) {
		// Anchors, aliases and tags are not supported: keep the text after the indicator token.
		const rest = trimmed.replace(/^[&*!][^\s]*\s*/, "");
		return rest === "" ? null : parseScalarValue(rest);
	}
	return parsePlainScalar(trimmed);
}

function splitKey(line) {
	// Returns [key, rest] when the line is "key: value" (key may be quoted), else null.
	const text = line.trimStart();
	let keyEnd;
	let key;
	if (text[0] === '"' || text[0] === "'") {
		const parsed = parseFlow(text, 0, true);
		keyEnd = parsed.end;
		key = String(parsed.value);
		if (text[keyEnd] !== ":") return null;
	} else {
		const match = /^([^#:]*?|[^#]*?[^\s]):(?=\s|$)/.exec(text);
		if (!match || match[1].startsWith("- ") || match[1] === "-") return null;
		key = match[1].trim();
		keyEnd = match[1].length;
	}
	return [key, text.slice(keyEnd + 1)];
}

class Parser {
	constructor(text) {
		this.lines = text.replace(/\r\n?/g, "\n").split("\n");
		this.pos = 0;
	}

	skipBlank() {
		while (this.pos < this.lines.length) {
			const stripped = stripComment(this.lines[this.pos]);
			if (stripped.trim() === "" || /^---\s*$/.test(stripped) || /^\.\.\.\s*$/.test(stripped)) this.pos++;
			else break;
		}
	}

	peekIndent() {
		this.skipBlank();
		if (this.pos >= this.lines.length) return -1;
		return indentOf(this.lines[this.pos]);
	}

	parseBlock(indent) {
		const current = this.peekIndent();
		if (current < 0 || current < indent) return null;
		const line = stripComment(this.lines[this.pos]).trimStart();
		if (line.startsWith("- ") || line === "-") return this.parseSequence(current);
		if (splitKey(line)) return this.parseMapping(current);
		return this.parseMultilineScalar(current);
	}

	parseMultilineScalar(indent) {
		const parts = [];
		while (this.pos < this.lines.length) {
			const raw = this.lines[this.pos];
			if (raw.trim() === "") {
				parts.push("\n");
				this.pos++;
				continue;
			}
			if (indentOf(raw) < indent) break;
			parts.push(stripComment(raw).trim());
			this.pos++;
		}
		while (parts.length && parts[parts.length - 1] === "\n") parts.pop();
		const text = parts.reduce((acc, p) => (p === "\n" ? `${acc}\n` : acc === "" || acc.endsWith("\n") ? acc + p : `${acc} ${p}`), "");
		return parseScalarValue(text);
	}

	parseBlockScalar(header, parentIndent) {
		const style = header[0];
		const chomp = header.includes("-") ? "strip" : header.includes("+") ? "keep" : "clip";
		const explicit = /[1-9]/.exec(header);
		const lines = [];
		let blockIndent = explicit ? parentIndent + Number(explicit[0]) : -1;
		while (this.pos < this.lines.length) {
			const raw = this.lines[this.pos];
			if (raw.trim() === "") {
				lines.push("");
				this.pos++;
				continue;
			}
			const ind = indentOf(raw);
			if (blockIndent < 0) {
				if (ind <= parentIndent) break;
				blockIndent = ind;
			}
			if (ind < blockIndent) break;
			lines.push(raw.slice(blockIndent));
			this.pos++;
		}
		let trailing = 0;
		while (lines.length && lines[lines.length - 1] === "") {
			lines.pop();
			trailing++;
		}
		let text;
		if (style === "|") {
			text = lines.join("\n");
		} else {
			// Folding: a single line break between two text lines becomes a space; each empty line
			// becomes a newline; more-indented lines keep their line breaks.
			text = "";
			for (let i = 0; i < lines.length; i++) {
				const line = lines[i];
				const prev = lines[i - 1];
				if (i === 0) text = line;
				else if (line === "") text += "\n";
				else if (prev === "") text += /^\s/.test(line) ? `\n${line}` : line;
				else if (/^\s/.test(line) || /^\s/.test(prev)) text += `\n${line}`;
				else text += ` ${line}`;
			}
		}
		if (lines.length === 0) return "";
		if (chomp === "clip") return `${text}\n`;
		if (chomp === "keep") return `${text}\n${"\n".repeat(trailing)}`;
		return text;
	}

	parseValueAfterIndicator(rest, indent) {
		const value = stripComment(rest).trim();
		if (/^[|>][-+1-9]*$/.test(value)) {
			this.pos++;
			return this.parseBlockScalar(value, indent);
		}
		if (value === "" || /^[&!][^\s]*$/.test(value)) {
			this.pos++;
			const next = this.peekIndent();
			if (next > indent || (next === indent && this.pos < this.lines.length && /^-(\s|$)/.test(this.lines[this.pos].trimStart()))) {
				return this.parseBlock(next);
			}
			return null;
		}
		// Plain scalars may continue on more-indented lines.
		if (!/^["'[{]/.test(value)) {
			if (/:(\s|$)/.test(value)) {
				throw new YAMLParseError(`Nested mappings are not allowed in compact mappings at line ${this.pos + 1}`);
			}
			const parts = [value];
			this.pos++;
			while (this.pos < this.lines.length) {
				const raw = this.lines[this.pos];
				if (raw.trim() === "" || indentOf(raw) <= indent) break;
				const stripped = stripComment(raw).trim();
				if (splitKey(stripped) || stripped.startsWith("- ")) break;
				parts.push(stripped);
				this.pos++;
			}
			return parseScalarValue(parts.join(" "));
		}
		// Quoted or flow values may span lines: gather until balanced.
		let text = value;
		this.pos++;
		while (this.pos < this.lines.length && !isBalanced(text)) {
			text += `\n${this.lines[this.pos].trim()}`;
			this.pos++;
		}
		return parseFlow(text.replace(/\n/g, text.startsWith('"') || text.startsWith("'") ? " " : "\n"), 0).value;
	}

	parseMapping(indent) {
		const result = {};
		while (this.peekIndent() === indent) {
			const raw = stripComment(this.lines[this.pos]);
			const kv = splitKey(raw);
			if (!kv) break;
			const [key, rest] = kv;
			if (key === "<<") {
				this.parseValueAfterIndicator(rest, indent);
				continue;
			}
			result[key] = this.parseValueAfterIndicator(rest, indent);
		}
		return result;
	}

	parseSequence(indent) {
		const result = [];
		while (this.peekIndent() === indent) {
			const raw = stripComment(this.lines[this.pos]);
			const trimmed = raw.trimStart();
			if (!(trimmed.startsWith("- ") || trimmed === "-")) break;
			const rest = trimmed === "-" ? "" : trimmed.slice(2);
			const itemIndent = indent + 2 + (rest.length - rest.trimStart().length);
			if (rest.trim() !== "" && splitKey(rest.trim()) && !/^["'[{]/.test(rest.trim())) {
				// "- key: value" starts a mapping indented at the item content column.
				this.lines[this.pos] = " ".repeat(itemIndent) + rest.trimStart();
				result.push(this.parseMapping(itemIndent));
				continue;
			}
			result.push(this.parseValueAfterIndicator(rest, indent));
		}
		return result;
	}
}

function isBalanced(text) {
	if (text.startsWith('"')) return /[^\\]"\s*$/.test(text.slice(1)) || text.length > 1 && text.endsWith('"') && text[text.length - 2] !== "\\";
	if (text.startsWith("'")) return text.length > 1 && text.endsWith("'");
	let depth = 0;
	let quote = null;
	for (let i = 0; i < text.length; i++) {
		const c = text[i];
		if (quote) {
			if (c === quote && text[i - 1] !== "\\") quote = null;
			continue;
		}
		if (c === '"' || c === "'") quote = c;
		else if (c === "[" || c === "{") depth++;
		else if (c === "]" || c === "}") depth--;
	}
	return depth <= 0;
}

export function parse(text, _reviver, _options) {
	if (typeof text !== "string") throw new TypeError("yaml.parse expects a string");
	const parser = new Parser(text);
	const indent = parser.peekIndent();
	if (indent < 0) return null;
	const value = parser.parseBlock(indent);
	parser.skipBlank();
	if (parser.pos < parser.lines.length) {
		throw new YAMLParseError(`Unexpected content at line ${parser.pos + 1}: ${parser.lines[parser.pos]}`);
	}
	return value;
}

function needsQuotes(text) {
	return (
		text === "" ||
		/^[\s]|[\s]$/.test(text) ||
		/[:#\n"'{}[\],&*!|>%@`]/.test(text) ||
		/^[-?]/.test(text) ||
		typeof parsePlainScalar(text) !== "string"
	);
}

function stringifyValue(value, indent) {
	const pad = " ".repeat(indent);
	if (value === null || value === undefined) return "null";
	if (typeof value === "boolean" || typeof value === "number") return String(value);
	if (typeof value === "string") return needsQuotes(value) ? JSON.stringify(value) : value;
	if (Array.isArray(value)) {
		if (value.length === 0) return "[]";
		return `\n${value.map((item) => `${pad}- ${stringifyValue(item, indent + 2).replace(/^\n\s*/, "")}`).join("\n")}`;
	}
	const entries = Object.entries(value).filter(([, v]) => v !== undefined);
	if (entries.length === 0) return "{}";
	return `\n${entries.map(([k, v]) => `${pad}${needsQuotes(k) ? JSON.stringify(k) : k}: ${stringifyValue(v, indent + 2)}`.replace(/: \n/, ":\n")).join("\n")}`;
}

export function stringify(value) {
	const out = stringifyValue(value, 0);
	return `${out.replace(/^\n/, "")}\n`;
}

export { YAMLParseError };
export default { parse, stringify };
