// PiSharp native replacement for the parts of the `marked` npm package used by pi-tui's Markdown component:
// `Marked` (setOptions/use/lexer), `Lexer`, `Tokenizer` (overridable methods with `this.lexer`), and
// block/inline tokenizer extensions with `start()` hints. It produces marked-shaped tokens (GFM flavour:
// space, code, heading, hr, blockquote, list/list_item, html, table, paragraph, text; escape, html, link,
// image, strong, em, codespan, br, del, text). It is a compact CommonMark/GFM approximation, not a full
// spec implementation; rendering is done by the ported upstream Markdown component.

const PUNCT_ESCAPE = /^\\([!"#$%&'()*+,\-./:;<=>?@[\]\\^_`{|}~])/;
const BLOCK_TAGS =
	"address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul|pre|script|style|textarea";
const RE = {
	newline: /^(?:[ \t]*(?:\n|$))+/,
	indentedCode: /^((?: {4}| {0,3}\t)[^\n]+(?:\n(?:[ \t]*(?:\n|$))*)?)+/,
	fences: /^ {0,3}(`{3,}(?=[^`\n]*(?:\n|$))|~{3,})([^\n]*)(?:\n|$)(?:|([\s\S]*?)(?:\n|$))(?: {0,3}\1[~`]* *(?=\n|$)|$)/,
	heading: /^ {0,3}(#{1,6})(?=\s|$)(.*)(?:\n+|$)/,
	hr: /^ {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)/,
	blockquoteStart: /^ {0,3}>/,
	listItemStart: /^( {0,3})([*+-]|\d{1,9}[.)])([ \t][^\n]*|[ \t]*)(?:\n|$)/,
	htmlBlock: new RegExp(
		`^ {0,3}(?:<(?:${BLOCK_TAGS})(?=[\\s/>]|$)|</(?:${BLOCK_TAGS})(?=[\\s>])|<!--|<\\?|<![A-Z]|<!\\[CDATA\\[|(?:<[a-zA-Z][\\w-]*(?:\\s+[a-zA-Z_:][\\w:.-]*(?:\\s*=\\s*(?:"[^"]*"|'[^']*'|[^\\s"'=<>\`]+))?)*\\s*/?>|</[a-zA-Z][\\w-]*\\s*>)[ \\t]*(?:\\n|$))`,
		"i",
	),
	def: /^ {0,3}\[((?!\s*\])(?:\\.|[^[\]\\])+)\]:[ \t]*(?:\n[ \t]*)?([^<\s][^\s]*|<.*?>)(?:(?:[ \t]+(?:\n[ \t]*)?|\n[ \t]*)("(?:\\"|[^"]|"[^"\n]*")*"|'[^'\n]*(?:\n[^'\n]+)*\n?'|\([^()]*\)))?[ \t]*(?:\n+|$)/,
	tableDelimiter: /^ {0,3}\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$/,
	lheading: /^ {0,3}(=+|-+)[ \t]*$/,
	codespan: /^(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)/,
	br: /^( {2,}|\\)\n(?!\s*$)/,
	del: /^(~~?)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)/,
	autolink: /^<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s\x00-\x1f<>]*|[a-zA-Z0-9.!#$%&'*+/=?_`{|}~-]+(@)[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+(?![-_]))>/,
	url: /^((?:ftp|https?):\/\/|www\.)(?:[a-zA-Z0-9-]+\.?)+[^\s<]*|^[A-Za-z0-9._+-]+(@)[a-zA-Z0-9-_]+(?:\.[a-zA-Z0-9-_]*[a-zA-Z0-9])+(?![-_])/,
	tag: /^(?:<!--(?:-?>|[\s\S]*?(?:-->|$))|<\/?[a-zA-Z][\w-]*(?:\s+[a-zA-Z_:][\w:.-]*(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*\s*\/?>|<\?[\s\S]*?\?>|<![a-zA-Z]+\s[\s\S]*?>|<!\[CDATA\[[\s\S]*?\]\]>)/,
	text: /^([`~]+|[^`~])(?:(?= {2,}\n)|(?=[a-zA-Z0-9.!#$%&'*+/=?_`{|}~-]+@)|[\s\S]*?(?:(?=[\\<![`*_~]|\b_|https?:\/\/|ftp:\/\/|www\.|$)|[^ ](?= {2,}\n)|[^a-zA-Z0-9.!#$%&'*+/=?_`{|}~-](?=[a-zA-Z0-9.!#$%&'*+/=?_`{|}~-]+@)))/,
};

function leadingIndent(line) {
	let width = 0;
	for (const ch of line) {
		if (ch === " ") width++;
		else if (ch === "\t") width += 4 - (width % 4);
		else break;
	}
	return width;
}

function removeIndent(line, amount) {
	let i = 0;
	let width = 0;
	while (i < line.length && width < amount && (line[i] === " " || line[i] === "\t")) {
		width += line[i] === "\t" ? 4 - (width % 4) : 1;
		i++;
	}
	return line.slice(i);
}

function isBlank(line) {
	return /^[ \t]*$/.test(line);
}

function splitCells(row, count) {
	let text = row.trim();
	if (text.startsWith("|")) text = text.slice(1);
	if (text.endsWith("|") && !text.endsWith("\\|")) text = text.slice(0, -1);
	const cells = [];
	let current = "";
	let inCode = 0;
	for (let i = 0; i < text.length; i++) {
		const ch = text[i];
		if (ch === "\\" && text[i + 1] === "|") {
			current += "|";
			i++;
			continue;
		}
		if (ch === "`") inCode = inCode ? 0 : 1;
		if (ch === "|" && !inCode) {
			cells.push(current.trim());
			current = "";
			continue;
		}
		current += ch;
	}
	cells.push(current.trim());
	if (count !== undefined) {
		if (cells.length > count) cells.splice(count);
		while (cells.length < count) cells.push("");
	}
	return cells;
}

/** Does this line start a block that interrupts a paragraph? */
function interruptsParagraph(line) {
	if (RE.heading.test(line) || RE.hr.test(line) || RE.blockquoteStart.test(line)) return true;
	if (/^ {0,3}(?:`{3,}|~{3,})/.test(line)) return true;
	if (RE.htmlBlock.test(line) && !/^ {0,3}<[a-zA-Z]/.test(line.replace(new RegExp(`^ {0,3}<(?:${BLOCK_TAGS})`, "i"), ""))) {
		if (new RegExp(`^ {0,3}(?:</?(?:${BLOCK_TAGS})(?=[\\s/>]|$)|<!--)`, "i").test(line)) return true;
	}
	const list = /^ {0,3}(?:[*+-]|1[.)])[ \t]+\S/.exec(line);
	return !!list;
}

export class Tokenizer {
	constructor(options) {
		this.options = options || {};
		this.lexer = undefined;
	}

	space(src) {
		const cap = RE.newline.exec(src);
		if (cap && cap[0].length > 0) return { type: "space", raw: cap[0] };
		return undefined;
	}

	code(src) {
		const cap = RE.indentedCode.exec(src);
		if (!cap) return undefined;
		const text = cap[0]
			.split("\n")
			.map((line) => removeIndent(line, 4))
			.join("\n")
			.replace(/\n+$/, "");
		return { type: "code", raw: cap[0], codeBlockStyle: "indented", text };
	}

	fences(src) {
		const cap = RE.fences.exec(src);
		if (!cap) return undefined;
		const raw = cap[0];
		const indent = /^(\s+)(?:```)/.exec(raw)?.[1]?.length ?? leadingIndent(raw);
		const text = (cap[3] ?? "")
			.split("\n")
			.map((line) => (indent > 0 ? removeIndent(line, Math.min(indent, leadingIndent(line))) : line))
			.join("\n");
		const info = cap[2] ? cap[2].trim().replace(/\\([!"#$%&'()*+,\-./:;<=>?@[\]\\^_`{|}~])/g, "$1") : cap[2];
		return { type: "code", raw, lang: info, text };
	}

	heading(src) {
		const cap = RE.heading.exec(src);
		if (!cap) return undefined;
		let text = cap[2].trim();
		if (/#$/.test(text)) {
			const trimmed = text.replace(/#+$/, "");
			if (!trimmed || / $/.test(trimmed)) text = trimmed.trim();
		}
		return { type: "heading", raw: cap[0], depth: cap[1].length, text, tokens: this.lexer.inline(text) };
	}

	hr(src) {
		const cap = RE.hr.exec(src);
		if (!cap) return undefined;
		return { type: "hr", raw: cap[0].replace(/\n+$/, "") === cap[0] ? cap[0] : cap[0] };
	}

	blockquote(src) {
		if (!RE.blockquoteStart.test(src)) return undefined;
		const lines = src.split("\n");
		const taken = [];
		let lastWasContent = false;
		for (let i = 0; i < lines.length; i++) {
			const line = lines[i];
			if (RE.blockquoteStart.test(line)) {
				taken.push(line);
				lastWasContent = !isBlank(line.replace(/^ {0,3}> ?/, ""));
				continue;
			}
			if (lastWasContent && !isBlank(line) && !interruptsParagraph(line)) {
				taken.push(line);
				continue;
			}
			break;
		}
		const raw = taken.join("\n") + (taken.length < lines.length ? "\n" : "");
		const text = taken.map((line) => (RE.blockquoteStart.test(line) ? line.replace(/^ {0,3}> ?/, "") : line)).join("\n");
		const top = this.lexer.state.top;
		this.lexer.state.top = true;
		const tokens = this.lexer.blockTokens(text, []);
		this.lexer.state.top = top;
		return { type: "blockquote", raw, tokens, text };
	}

	list(src) {
		const first = RE.listItemStart.exec(src);
		if (!first) return undefined;
		const bull = first[2];
		const ordered = bull.length > 1;
		const marker = ordered ? bull.slice(-1) : bull;
		const lines = src.split("\n");
		const items = [];
		let index = 0;
		let loose = false;
		const baseIndent = first[1].length;

		while (index < lines.length) {
			const line = lines[index];
			const m = RE.listItemStart.exec(`${line}\n`);
			if (!m || m[1].length > baseIndent + 3 || leadingIndent(line) >= baseIndent + 4) break;
			const thisBull = m[2];
			const thisOrdered = thisBull.length > 1;
			if (thisOrdered !== ordered || (thisOrdered ? thisBull.slice(-1) : thisBull) !== marker) break;
			if (RE.hr.test(line) && items.length > 0) break;
			const afterMarker = m[3];
			let spaces = afterMarker.length - afterMarker.trimStart().length;
			const emptyFirst = afterMarker.trim() === "";
			if (emptyFirst) spaces = 1;
			if (spaces > 4) spaces = 1;
			const contentIndent = m[1].length + thisBull.length + spaces;
			const itemLines = [emptyFirst ? "" : afterMarker.slice(spaces)];
			const rawLines = [line];
			index++;
			let sawBlank = false;
			let lastBlankRun = 0;
			while (index < lines.length) {
				const next = lines[index];
				if (isBlank(next)) {
					itemLines.push("");
					rawLines.push(next);
					sawBlank = true;
					lastBlankRun++;
					index++;
					continue;
				}
				if (leadingIndent(next) >= contentIndent) {
					itemLines.push(removeIndent(next, contentIndent));
					rawLines.push(next);
					if (lastBlankRun > 0) sawBlank = "inner";
					lastBlankRun = 0;
					index++;
					continue;
				}
				if (lastBlankRun === 0 && !RE.listItemStart.test(`${next}\n`) && !interruptsParagraph(next)) {
					// lazy continuation line
					itemLines.push(next.trimStart());
					rawLines.push(next);
					index++;
					continue;
				}
				break;
			}
			// trailing blank lines belong between items (or after the list)
			let trailing = 0;
			while (itemLines.length > 1 && itemLines[itemLines.length - 1] === "") {
				itemLines.pop();
				rawLines.pop();
				trailing++;
			}
			if (sawBlank === "inner") loose = true;
			let itemText = itemLines.join("\n");
			let task = false;
			let checked;
			const taskMatch = /^\[[ xX]\] +/.exec(itemText);
			if (taskMatch) {
				task = true;
				checked = taskMatch[0][1] !== " ";
				itemText = itemText.slice(taskMatch[0].length);
			}
			items.push({ raw: rawLines.join("\n"), task, checked, loose: false, text: itemText, trailing });
			if (trailing > 0) {
				const nextLine = lines[index];
				const nextItem = nextLine !== undefined ? RE.listItemStart.exec(`${nextLine}\n`) : null;
				if (nextItem && nextItem[1].length <= baseIndent + 3) {
					const nb = nextItem[2];
					if ((nb.length > 1) === ordered && (nb.length > 1 ? nb.slice(-1) : nb) === marker) {
						loose = true;
						// marked keeps a run of 2+ blank lines inside the item (it lexes to a space token).
						if (trailing >= 2) items[items.length - 1].text += "\n".repeat(trailing);
						continue;
					}
				}
				// Put the trailing blank lines back so they become a space token.
				index -= trailing;
				break;
			}
		}

		if (items.length === 0) return undefined;
		const top = this.lexer.state.top;
		const listItems = items.map((item) => {
			// blockTokens() resets state.top, so mark every item's content as non-top again.
			this.lexer.state.top = false;
			return {
				type: "list_item",
				raw: item.raw,
				task: item.task,
				checked: item.checked,
				loose,
				text: item.text,
				tokens: this.lexer.blockTokens(item.text, []),
			};
		});
		this.lexer.state.top = top;
		const consumed = lines.slice(0, index).join("\n");
		const raw = consumed.replace(/\s+$/, "");
		return {
			type: "list",
			raw,
			ordered,
			start: ordered ? Number(bull.slice(0, -1)) : "",
			loose,
			items: listItems,
		};
	}

	html(src) {
		if (!RE.htmlBlock.test(src)) return undefined;
		const lines = src.split("\n");
		const taken = [];
		for (const line of lines) {
			if (taken.length > 0 && isBlank(line)) break;
			taken.push(line);
		}
		// Like marked, the block ends before the blank line, which then lexes to a space token.
		const raw = taken.join("\n") + (taken.length < lines.length && !isBlank(lines[taken.length] ?? "") ? "\n" : "");
		return { type: "html", block: true, raw, pre: /^ {0,3}<(?:pre|script|style)/i.test(raw), text: raw };
	}

	def(src) {
		const cap = RE.def.exec(src);
		if (!cap) return undefined;
		const tag = cap[1].toLowerCase().replace(/\s+/g, " ");
		const href = cap[2] ? cap[2].replace(/^<(.*)>$/, "$1") : "";
		const title = cap[3] ? cap[3].substring(1, cap[3].length - 1) : cap[3];
		return { type: "def", tag, raw: cap[0], href, title };
	}

	table(src) {
		const lines = src.split("\n");
		if (lines.length < 2 || !lines[0].includes("|") || !RE.tableDelimiter.test(lines[1])) return undefined;
		const headers = splitCells(lines[0]);
		const aligns = splitCells(lines[1]).map((cell) => {
			if (/^:-+:$/.test(cell)) return "center";
			if (/^-+:$/.test(cell)) return "right";
			if (/^:-+$/.test(cell)) return "left";
			return null;
		});
		if (headers.length !== aligns.length) return undefined;
		let index = 2;
		const rowLines = [];
		while (index < lines.length && !isBlank(lines[index]) && !interruptsParagraph(lines[index])) {
			rowLines.push(lines[index]);
			index++;
		}
		let rawEnd = index;
		while (rawEnd < lines.length && isBlank(lines[rawEnd]) && rawEnd < lines.length - 1) rawEnd++;
		const raw = lines.slice(0, index).join("\n") + (index < lines.length ? "\n" : "");
		const cell = (text, header, i) => ({ text, tokens: this.lexer.inline(text), header, align: aligns[i] ?? null });
		return {
			type: "table",
			raw,
			align: aligns,
			header: headers.map((text, i) => cell(text, true, i)),
			rows: rowLines.map((row) => splitCells(row, headers.length).map((text, i) => cell(text, false, i))),
		};
	}

	lheading(src) {
		const lines = src.split("\n");
		const taken = [];
		for (let i = 0; i < lines.length; i++) {
			const line = lines[i];
			if (isBlank(line)) return undefined;
			if (taken.length > 0 && RE.lheading.test(line)) {
				const level = line.trim()[0] === "=" ? 1 : 2;
				const text = taken.map((l) => l.trim()).join("\n");
				let rawLen = taken.join("\n").length + 1 + line.length;
				let rest = src.slice(rawLen);
				const extra = /^\n+/.exec(rest);
				if (extra) rawLen += extra[0].length;
				else if (rest.length === 0) rest = "";
				return { type: "heading", raw: src.slice(0, rawLen), depth: level, text, tokens: this.lexer.inline(text) };
			}
			if (taken.length > 0 && interruptsParagraph(line)) return undefined;
			if (taken.length === 0 && interruptsParagraph(line)) return undefined;
			taken.push(line);
		}
		return undefined;
	}

	paragraph(src) {
		const lines = src.split("\n");
		const taken = [];
		for (const line of lines) {
			if (isBlank(line)) break;
			if (taken.length > 0 && interruptsParagraph(line)) break;
			taken.push(line);
		}
		if (taken.length === 0) return undefined;
		const raw = taken.join("\n");
		const text = raw;
		return { type: "paragraph", raw, text, tokens: this.lexer.inline(text) };
	}

	text(src) {
		const lines = src.split("\n");
		const taken = [];
		for (const line of lines) {
			if (isBlank(line)) break;
			if (taken.length > 0 && interruptsParagraph(line)) break;
			taken.push(line);
		}
		if (taken.length === 0) return undefined;
		const raw = taken.join("\n");
		return { type: "text", raw, text: raw, tokens: this.lexer.inline(raw) };
	}

	escape(src) {
		const cap = PUNCT_ESCAPE.exec(src);
		if (cap) return { type: "escape", raw: cap[0], text: cap[1] };
		return undefined;
	}

	tag(src) {
		const cap = RE.tag.exec(src);
		if (!cap) return undefined;
		if (!this.lexer.state.inLink && /^<a /i.test(cap[0])) this.lexer.state.inLink = true;
		else if (this.lexer.state.inLink && /^<\/a>/i.test(cap[0])) this.lexer.state.inLink = false;
		return { type: "html", raw: cap[0], inLink: this.lexer.state.inLink, inRawBlock: false, block: false, text: cap[0] };
	}

	link(src) {
		const isImage = src[0] === "!";
		const start = isImage ? 1 : 0;
		if (src[start] !== "[") return undefined;
		const labelEnd = findClosingBracket(src, start);
		if (labelEnd < 0 || src[labelEnd + 1] !== "(") return undefined;
		const dest = parseDestination(src, labelEnd + 2);
		if (!dest) return undefined;
		const raw = src.slice(0, dest.end);
		const text = src.slice(start + 1, labelEnd);
		return this.outputLink(raw, text, dest.href, dest.title, isImage);
	}

	reflink(src, links) {
		const isImage = src[0] === "!";
		const start = isImage ? 1 : 0;
		if (src[start] !== "[") return undefined;
		const labelEnd = findClosingBracket(src, start);
		if (labelEnd < 0) return undefined;
		const text = src.slice(start + 1, labelEnd);
		let raw = src.slice(0, labelEnd + 1);
		let ref = text;
		const full = /^\[((?:\\.|[^[\]\\])*)\]/.exec(src.slice(labelEnd + 1));
		if (full) {
			raw += full[0];
			if (full[1]) ref = full[1];
		}
		const link = links[ref.toLowerCase().replace(/\s+/g, " ")];
		if (!link || !link.href) return undefined;
		return this.outputLink(raw, text, link.href, link.title, isImage);
	}

	outputLink(raw, text, href, title, isImage) {
		const unescape = (value) => (value ? value.replace(/\\([!"#$%&'()*+,\-./:;<=>?@[\]\\^_`{|}~])/g, "$1") : value);
		if (isImage) return { type: "image", raw, href: unescape(href), title: unescape(title) ?? null, text };
		this.lexer.state.inLink = true;
		const token = { type: "link", raw, href: unescape(href), title: unescape(title) ?? null, text, tokens: this.lexer.inlineTokens(text) };
		this.lexer.state.inLink = false;
		return token;
	}

	emStrong(src, maskedSrc, prevChar = "") {
		const run = /^(\*+|_+)/.exec(src);
		if (!run) return undefined;
		const ch = run[1][0];
		const next = src[run[1].length] ?? "";
		if (!next || /\s/.test(next)) return undefined;
		if (ch === "_" && /[\p{L}\p{N}]/u.test(prevChar)) return undefined;
		const tryDelim = (count) => {
			if (run[1].length < count) return undefined;
			const delim = ch.repeat(count);
			let i = count;
			while (i < src.length) {
				const c = src[i];
				if (c === "\\") {
					i += 2;
					continue;
				}
				if (c === "`") {
					const code = RE.codespan.exec(src.slice(i));
					if (code) {
						i += code[0].length;
						continue;
					}
				}
				if (c === ch) {
					let len = 0;
					while (src[i + len] === ch) len++;
					const before = src[i - 1];
					const after = src[i + len] ?? "";
					const opener = after && !/\s/.test(after) && (!before || /[\s\p{P}\p{S}]/u.test(before));
					if (opener) {
						// A nested emphasis opens here: skip over it when it closes.
						const nested = this.emStrong(src.slice(i), src.slice(i), before);
						if (nested) {
							i += nested.raw.length;
							continue;
						}
					}
					const rightFlanking = before && !/\s/.test(before);
					const okAfter = ch !== "_" || !/[\p{L}\p{N}]/u.test(after);
					if (rightFlanking && okAfter && len >= count && i > count) {
						const inner = src.slice(count, i);
						return { raw: src.slice(0, i + count), inner };
					}
					i += len;
					continue;
				}
				i++;
			}
			return undefined;
		};
		if (run[1].length >= 3) {
			const both = tryDelim(3);
			if (both && both.raw.endsWith(ch.repeat(3))) {
				const strong = { type: "strong", raw: both.raw.slice(1, -1), text: both.inner, tokens: this.lexer.inlineTokens(both.inner) };
				return { type: "em", raw: both.raw, text: `${ch.repeat(2)}${both.inner}${ch.repeat(2)}`, tokens: [strong] };
			}
		}
		const strong = tryDelim(2);
		if (strong) return { type: "strong", raw: strong.raw, text: strong.inner, tokens: this.lexer.inlineTokens(strong.inner) };
		const em = tryDelim(1);
		if (em) return { type: "em", raw: em.raw, text: em.inner, tokens: this.lexer.inlineTokens(em.inner) };
		return undefined;
	}

	codespan(src) {
		const cap = RE.codespan.exec(src);
		if (!cap) return undefined;
		let text = cap[2].replace(/\n/g, " ");
		const hasNonSpace = /[^ ]/.test(text);
		if (hasNonSpace && text.startsWith(" ") && text.endsWith(" ")) text = text.substring(1, text.length - 1);
		return { type: "codespan", raw: cap[0], text };
	}

	br(src) {
		const cap = RE.br.exec(src);
		if (cap) return { type: "br", raw: cap[0] };
		return undefined;
	}

	del(src) {
		const cap = RE.del.exec(src);
		if (cap) return { type: "del", raw: cap[0], text: cap[2], tokens: this.lexer.inlineTokens(cap[2]) };
		return undefined;
	}

	autolink(src) {
		const cap = RE.autolink.exec(src);
		if (!cap) return undefined;
		const text = cap[1];
		const href = cap[2] === "@" ? `mailto:${text}` : text;
		return { type: "link", raw: cap[0], text, href, tokens: [{ type: "text", raw: text, text }] };
	}

	url(src) {
		if (this.lexer.state.inLink) return undefined;
		let cap = RE.url.exec(src);
		if (!cap) return undefined;
		let text;
		let href;
		if (cap[2] === "@") {
			text = cap[0];
			href = `mailto:${text}`;
		} else {
			let value = cap[0];
			let previous;
			do {
				previous = value;
				value = value.replace(/[?!.,:*_~]+$/, "");
				const open = (value.match(/\(/g) || []).length;
				const close = (value.match(/\)/g) || []).length;
				if (close > open && value.endsWith(")")) value = value.slice(0, -1);
			} while (value !== previous);
			text = value;
			href = cap[1] === "www." ? `http://${text}` : text;
			cap = [value];
		}
		return { type: "link", raw: cap[0], text, href, tokens: [{ type: "text", raw: text, text }] };
	}

	inlineText(src) {
		const cap = RE.text.exec(src);
		if (cap && cap[0].length > 0) return { type: "text", raw: cap[0], text: cap[0], escaped: false };
		return undefined;
	}
}

function findClosingBracket(src, openIndex) {
	let depth = 0;
	for (let i = openIndex; i < src.length; i++) {
		const c = src[i];
		if (c === "\\") {
			i++;
			continue;
		}
		if (c === "`") {
			const code = RE.codespan.exec(src.slice(i));
			if (code) {
				i += code[0].length - 1;
				continue;
			}
		}
		if (c === "[") depth++;
		else if (c === "]") {
			depth--;
			if (depth === 0) return i;
		} else if (c === "\n" && src[i + 1] === "\n") return -1;
	}
	return -1;
}

function parseDestination(src, start) {
	let i = start;
	while (src[i] === " " || src[i] === "\t" || src[i] === "\n") i++;
	let href = "";
	if (src[i] === "<") {
		const end = src.indexOf(">", i);
		if (end < 0) return undefined;
		href = src.slice(i + 1, end);
		i = end + 1;
	} else {
		let depth = 0;
		const begin = i;
		while (i < src.length) {
			const c = src[i];
			if (c === "\\") {
				i += 2;
				continue;
			}
			if (/\s/.test(c)) break;
			if (c === "(") depth++;
			if (c === ")") {
				if (depth === 0) break;
				depth--;
			}
			i++;
		}
		href = src.slice(begin, i);
	}
	while (src[i] === " " || src[i] === "\t" || src[i] === "\n") i++;
	let title = null;
	if (src[i] === '"' || src[i] === "'" || src[i] === "(") {
		const close = src[i] === "(" ? ")" : src[i];
		const end = src.indexOf(close, i + 1);
		if (end < 0) return undefined;
		title = src.slice(i + 1, end);
		i = end + 1;
		while (src[i] === " " || src[i] === "\t" || src[i] === "\n") i++;
	}
	if (src[i] !== ")") return undefined;
	return { href, title, end: i + 1 };
}

export class Lexer {
	constructor(options) {
		this.tokens = [];
		this.tokens.links = Object.create(null);
		this.options = options || {};
		this.tokenizer = this.options.tokenizer || new Tokenizer(this.options);
		this.tokenizer.options = this.options;
		this.tokenizer.lexer = this;
		this.inlineQueue = [];
		this.state = { inLink: false, inRawBlock: false, top: true };
	}

	static lex(src, options) {
		return new Lexer(options).lex(src);
	}

	static lexInline(src, options) {
		return new Lexer(options).inlineTokens(src);
	}

	lex(src) {
		src = String(src).replace(/\r\n|\r/g, "\n");
		this.blockTokens(src, this.tokens);
		for (let i = 0; i < this.inlineQueue.length; i++) {
			const next = this.inlineQueue[i];
			const tokens = this.inlineTokens(next.src);
			next.tokens.length = 0;
			next.tokens.push(...tokens);
		}
		this.inlineQueue = [];
		return this.tokens;
	}

	/** Queue inline lexing until all link reference definitions are known. */
	inline(src, tokens = []) {
		this.inlineQueue.push({ src, tokens });
		return tokens;
	}

	blockTokens(src, tokens = [], lastParagraphClipped = false) {
		const extensions = this.options.extensions;
		while (src) {
			let token;
			if (
				extensions?.block?.some((extTokenizer) => {
					token = extTokenizer.call({ lexer: this }, src, tokens);
					if (token) {
						src = src.substring(token.raw.length);
						tokens.push(token);
						return true;
					}
					return false;
				})
			) {
				continue;
			}

			if ((token = this.tokenizer.space(src))) {
				src = src.substring(token.raw.length);
				const lastToken = tokens.at(-1);
				if (token.raw.length === 1 && lastToken !== undefined) lastToken.raw += "\n";
				else tokens.push(token);
				continue;
			}

			if ((token = this.tokenizer.code(src))) {
				src = src.substring(token.raw.length);
				const lastToken = tokens.at(-1);
				if (lastToken?.type === "paragraph" || lastToken?.type === "text") {
					lastToken.raw += (lastToken.raw.endsWith("\n") ? "" : "\n") + token.raw;
					lastToken.text += `\n${token.text}`;
					this.inlineQueue.at(-1).src = lastToken.text;
				} else {
					tokens.push(token);
				}
				continue;
			}

			for (const name of ["fences", "heading", "hr", "blockquote", "list", "html"]) {
				if ((token = this.tokenizer[name](src))) break;
			}
			if (token) {
				src = src.substring(token.raw.length);
				tokens.push(token);
				continue;
			}

			if ((token = this.tokenizer.def(src))) {
				src = src.substring(token.raw.length);
				const lastToken = tokens.at(-1);
				if (lastToken?.type === "paragraph" || lastToken?.type === "text") {
					lastToken.raw += `\n${token.raw}`;
					lastToken.text += `\n${token.raw}`;
					this.inlineQueue.at(-1).src = lastToken.text;
				} else if (!this.tokens.links[token.tag]) {
					this.tokens.links[token.tag] = { href: token.href, title: token.title };
				}
				continue;
			}

			if ((token = this.tokenizer.table(src)) || (token = this.tokenizer.lheading(src))) {
				src = src.substring(token.raw.length);
				tokens.push(token);
				continue;
			}

			let cutSrc = src;
			if (extensions?.startBlock) {
				let startIndex = Number.POSITIVE_INFINITY;
				const tempSrc = src.slice(1);
				for (const getStartIndex of extensions.startBlock) {
					const tempStart = getStartIndex.call({ lexer: this }, tempSrc);
					if (typeof tempStart === "number" && tempStart >= 0) startIndex = Math.min(startIndex, tempStart);
				}
				if (startIndex < Number.POSITIVE_INFINITY && startIndex >= 0) cutSrc = src.substring(0, startIndex + 1);
			}

			if (this.state.top && (token = this.tokenizer.paragraph(cutSrc))) {
				const lastToken = tokens.at(-1);
				if (lastParagraphClipped && lastToken?.type === "paragraph") {
					lastToken.raw += (lastToken.raw.endsWith("\n") ? "" : "\n") + token.raw;
					lastToken.text += `\n${token.text}`;
					this.inlineQueue.pop();
					this.inlineQueue.at(-1).src = lastToken.text;
				} else {
					tokens.push(token);
				}
				lastParagraphClipped = cutSrc.length !== src.length;
				src = src.substring(token.raw.length);
				continue;
			}

			if ((token = this.tokenizer.text(src))) {
				src = src.substring(token.raw.length);
				const lastToken = tokens.at(-1);
				if (lastToken?.type === "text") {
					lastToken.raw += (lastToken.raw.endsWith("\n") ? "" : "\n") + token.raw;
					lastToken.text += `\n${token.text}`;
					this.inlineQueue.pop();
					this.inlineQueue.at(-1).src = lastToken.text;
				} else {
					tokens.push(token);
				}
				continue;
			}

			// Should not happen; consume one line to guarantee progress.
			const newline = src.indexOf("\n");
			const raw = newline === -1 ? src : src.slice(0, newline + 1);
			tokens.push({ type: "text", raw, text: raw.replace(/\n$/, ""), tokens: this.inline(raw.replace(/\n$/, "")) });
			src = src.substring(raw.length);
		}
		this.state.top = true;
		return tokens;
	}

	inlineTokens(src, tokens = []) {
		const extensions = this.options.extensions;
		let prevChar = "";
		while (src) {
			let token;
			if (
				extensions?.inline?.some((extTokenizer) => {
					token = extTokenizer.call({ lexer: this }, src, tokens);
					if (token) {
						src = src.substring(token.raw.length);
						tokens.push(token);
						return true;
					}
					return false;
				})
			) {
				prevChar = token.raw.slice(-1);
				continue;
			}

			token =
				this.tokenizer.escape(src) ||
				this.tokenizer.tag(src) ||
				this.tokenizer.link(src) ||
				this.tokenizer.reflink(src, this.tokens.links) ||
				this.tokenizer.emStrong(src, src, prevChar) ||
				this.tokenizer.codespan(src) ||
				this.tokenizer.br(src) ||
				this.tokenizer.del(src) ||
				this.tokenizer.autolink(src) ||
				(!this.state.inLink ? this.tokenizer.url(src) : undefined);
			if (token) {
				src = src.substring(token.raw.length);
				const lastToken = tokens.at(-1);
				if (token.type === "text" && lastToken?.type === "text") {
					lastToken.raw += token.raw;
					lastToken.text += token.text;
				} else {
					tokens.push(token);
				}
				prevChar = token.raw.slice(-1);
				continue;
			}

			let cutSrc = src;
			if (extensions?.startInline) {
				let startIndex = Number.POSITIVE_INFINITY;
				const tempSrc = src.slice(1);
				for (const getStartIndex of extensions.startInline) {
					const tempStart = getStartIndex.call({ lexer: this }, tempSrc);
					if (typeof tempStart === "number" && tempStart >= 0) startIndex = Math.min(startIndex, tempStart);
				}
				if (startIndex < Number.POSITIVE_INFINITY && startIndex >= 0) cutSrc = src.substring(0, startIndex + 1);
			}
			token = this.tokenizer.inlineText(cutSrc) ?? { type: "text", raw: src[0], text: src[0], escaped: false };
			src = src.substring(token.raw.length);
			prevChar = token.raw.slice(-1);
			const lastToken = tokens.at(-1);
			if (lastToken?.type === "text") {
				lastToken.raw += token.raw;
				lastToken.text += token.text;
			} else {
				tokens.push(token);
			}
		}
		return tokens;
	}
}

export class Marked {
	constructor(...args) {
		this.defaults = { async: false, breaks: false, extensions: null, gfm: true, pedantic: false, silent: false, tokenizer: null };
		this.Tokenizer = Tokenizer;
		this.Lexer = Lexer;
		for (const arg of args) this.use(arg);
	}

	setOptions(options) {
		this.defaults = { ...this.defaults, ...options };
		return this;
	}

	use(...args) {
		const extensions = this.defaults.extensions || { block: [], inline: [], startBlock: [], startInline: [], renderers: {} };
		for (const pack of args) {
			if (!pack) continue;
			for (const ext of pack.extensions ?? []) {
				if (!ext.name) throw new Error("extension name required");
				if (ext.tokenizer) {
					if (ext.level === "block") {
						extensions.block.push(ext.tokenizer);
						if (ext.start) extensions.startBlock.push(ext.start);
					} else if (ext.level === "inline") {
						extensions.inline.push(ext.tokenizer);
						if (ext.start) extensions.startInline.push(ext.start);
					} else {
						throw new Error("extension level must be 'block' or 'inline'");
					}
				}
				if (ext.renderer) extensions.renderers[ext.name] = ext.renderer;
			}
			const { extensions: _ignored, ...rest } = pack;
			this.defaults = { ...this.defaults, ...rest };
		}
		this.defaults.extensions = extensions;
		return this;
	}

	lexer(src, options) {
		const opts = { ...this.defaults, ...options };
		if (opts.tokenizer) {
			// The tokenizer instance is shared; bind it to the new lexer for this run.
			opts.tokenizer.options = opts;
		}
		return new Lexer(opts).lex(src);
	}

	parse() {
		throw new Error("Marked.parse (HTML rendering) is not available in the PiSharp Node bridge");
	}

	parseInline() {
		throw new Error("Marked.parseInline (HTML rendering) is not available in the PiSharp Node bridge");
	}
}

const defaultMarked = new Marked();
export const marked = (src, options) => defaultMarked.parse(src, options);
marked.lexer = (src, options) => defaultMarked.lexer(src, options);
marked.use = (...args) => {
	defaultMarked.use(...args);
	return marked;
};
marked.setOptions = (options) => {
	defaultMarked.setOptions(options);
	return marked;
};
export const lexer = marked.lexer;
export default marked;
