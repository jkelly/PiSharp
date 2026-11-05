// PiSharp replacement for the `marked-cjk-friendly` 0.1.2 npm package as pi-tui's Markdown component uses it (Pi v1.5.0
// packages/tui/src/components/markdown.ts: `markdownParser.use({ tokenizer: { emStrong: markedCjkFriendly().tokenizer?.emStrong } })`).
// The emStrong below is the package's own, de-minified from its browser build (vendored by PiSharp at
// src/PiSharp.CodingAgent/Export/Assets/vendor/marked-cjk-friendly.min.js):
//   marked-cjk-friendly v0.1.2, Copyright (c) 2024 Tatsunori Uchino, and authors and contributors of original packages
//   (MIT License), https://github.com/tats-u/markdown-cjk-friendly/tree/main/packages/marked-cjk-friendly
// It reads marked's emphasis rules (`this.rules`) and masked inline source, which ./marked.mjs supplies as marked 18 does. A CJK
// character or CJK punctuation next to a delimiter run no longer stops it from opening or closing emphasis; next to other text
// marked's rules are unchanged. The package's `del` override is not included: pi-tui does not use it.

// East Asian Wide, Fullwidth and Halfwidth characters that are not emoji, and Hangul.
const CJK_RANGES =
	"\\u1100-\\u11ff\\u20a9\\u2329-\\u232a\\u2630-\\u2637\\u268a-\\u268f\\u2e80-\\u2e99\\u2e9b-\\u2ef3\\u2f00-\\u2fd5\\u2ff0-\\u303e\\u3041-\\u3096\\u3099-\\u30ff\\u3105-\\u312f\\u3131-\\u318e\\u3190-\\u31e5\\u31ef-\\u321e\\u3220-\\u3247\\u3250-\\ua48c\\ua490-\\ua4c6\\ua960-\\ua97c\\uac00-\\ud7a3\\ud7b0-\\ud7c6\\ud7cb-\\ud7fb\\uf900-\\ufaff\\ufe10-\\ufe19\\ufe30-\\ufe52\\ufe54-\\ufe66\\ufe68-\\ufe6b\\uff01-\\uffbe\\uffc2-\\uffc7\\uffca-\\uffcf\\uffd2-\\uffd7\\uffda-\\uffdc\\uffe0-\\uffe6\\uffe8-\\uffee\\u{16fe0}-\\u{16fe4}\\u{16ff0}-\\u{16ff6}\\u{17000}-\\u{18cda}\\u{18cff}-\\u{18d20}\\u{18d80}-\\u{18df2}\\u{18e00}-\\u{19191}\\u{191a0}-\\u{191d2}\\u{1aff0}-\\u{1aff3}\\u{1aff5}-\\u{1affb}\\u{1affd}-\\u{1affe}\\u{1b000}-\\u{1b128}\\u{1b132}\\u{1b150}-\\u{1b152}\\u{1b155}\\u{1b164}-\\u{1b168}\\u{1b170}-\\u{1b2fb}\\u{1d300}-\\u{1d356}\\u{1d360}-\\u{1d376}\\u{1f1ae}\\u{1f200}\\u{1f202}\\u{1f210}-\\u{1f219}\\u{1f21b}-\\u{1f22e}\\u{1f230}-\\u{1f231}\\u{1f237}\\u{1f23b}\\u{1f240}-\\u{1f248}\\u{1f260}-\\u{1f265}\\u{1f7da}\\u{20000}-\\u{3fffd}";
const CJK = new RegExp(`[${CJK_RANGES}]`, "u");
const PREVIOUS_PUNCTUATION_OR_CJK = new RegExp(`^((?![*_])[\\s\\p{P}\\p{S}${CJK_RANGES}])`, "u");

// marked's closing-delimiter rules with CJK characters counted as punctuation.
function asteriskRule(punct, punctSpace, notPunctSpace) {
	return new RegExp(
		`^[^_*]*?__[^_*]*?\\*[^_*]*?(?=__)|[^*]+(?=[^*])|(?!\\*)${punct}(\\*+)(?=[\\s]|$)|${notPunctSpace}(\\*+)(?!\\*)(?=${punctSpace}|$)|(?!\\*)${punctSpace}(\\*+)(?=${notPunctSpace})|[\\s](\\*+)(?!\\*)(?=${punct})|(?!\\*)${punct}(\\*+)(?!\\*)(?=${punct})|${notPunctSpace}(\\*+)(?=${notPunctSpace})`,
		"gu",
	);
}

function underscoreRule(punct, punctSpace, notPunctSpace) {
	return new RegExp(
		`^[^_*]*?\\*\\*[^_*]*?_[^_*]*?(?=\\*\\*)|[^_]+(?=[^_])|(?!_)${punct}(_+)(?=[\\s]|$)|${notPunctSpace}(_+)(?!_)(?=${punctSpace}|$)|(?!_)${punctSpace}(_+)(?=${notPunctSpace})|[\\s](_+)(?!_)(?=${punct})|(?!_)${punct}(_+)(?!_)(?=${punct})`,
		"gu",
	);
}

const PUNCT = `[\\p{P}\\p{S}${CJK_RANGES}]`;
const PUNCT_SPACE = `[\\s\\p{P}\\p{S}${CJK_RANGES}]`;
const NOT_PUNCT_SPACE = `[^\\s\\p{P}\\p{S}${CJK_RANGES}]`;
const GFM_PUNCT = `(?!~)[\\p{P}\\p{S}${CJK_RANGES}]`;
const GFM_PUNCT_SPACE = `(?!~)[\\s\\p{P}\\p{S}${CJK_RANGES}]`;
const GFM_NOT_PUNCT_SPACE = `(?:[^\\s\\p{P}\\p{S}${CJK_RANGES}]|~)`;
const R_DELIM_AST = asteriskRule(PUNCT, PUNCT_SPACE, NOT_PUNCT_SPACE);
const R_DELIM_AST_GFM = asteriskRule(GFM_PUNCT, GFM_PUNCT_SPACE, GFM_NOT_PUNCT_SPACE);
const R_DELIM_UND = underscoreRule(PUNCT, PUNCT_SPACE, NOT_PUNCT_SPACE);

/** Whether the character before `src` is whitespace, punctuation or CJK (an ideographic variation selector counts as CJK). */
function previousIsPunctuationOrCjk(prevChar, maskedSrc, src) {
	let result = PREVIOUS_PUNCTUATION_OR_CJK.test(prevChar);
	if (!result && prevChar) {
		const start = maskedSrc.length - src.length;
		if (start >= 1) {
			let index = start - 1;
			let code = maskedSrc.charCodeAt(index);
			// A standardized variation selector (U+FE00-U+FE0E) takes the class of the character it follows.
			if (code >= 0xfe00 && code <= 0xfe0e && index >= 1) {
				index--;
				code = maskedSrc.charCodeAt(index);
			}
			if ((code & 0xfc00) === 0xdc00 && index >= 1) {
				const codePoint = maskedSrc.codePointAt(index - 1);
				if (codePoint !== undefined && codePoint > 0xffff) {
					result = codePoint >= 0xe0100 && codePoint <= 0xe01ef ? true : CJK.test(String.fromCodePoint(codePoint));
				}
			} else {
				result = CJK.test(String.fromCharCode(code));
			}
		}
	}
	return result;
}

/** Whether a left- or right-only delimiter match (groups 1 to 4) has a CJK character on either side, making it both. */
function touchesCjk(match, maskedSrc) {
	if (!(match[1] || match[2] || match[3] || match[4])) return false;
	const before = String.fromCodePoint(match[0].codePointAt(0));
	const end = match.index + match[0].length;
	const after = end < maskedSrc.length ? String.fromCodePoint(maskedSrc.codePointAt(end)) : "";
	return CJK.test(before) || CJK.test(after);
}

// marked 16 and later have five groups in emStrongLDelim (the run may be followed by nothing); earlier versions have four.
const hasOptionalNext = (match) => match.length >= 5;
const hasNext = (match) => !!(match[1] || match[2] || match[3] || match[4]);
const underscoreBeforeText = (match) => (hasOptionalNext(match) ? !!match[4] : !!match[3]);
const punctuationNext = (match) => (hasOptionalNext(match) ? match[1] || match[3] || "" : match[1] || match[2] || "");

function emStrong(src, maskedSrc, prevChar = "") {
	const { rules } = this;
	const match = rules.inline.emStrongLDelim.exec(src);
	if (!match) return false;
	const optionalNext = hasOptionalNext(match);
	if (optionalNext && !hasNext(match)) return undefined;
	// "_" cannot open between two alphanumerics.
	if (underscoreBeforeText(match) && prevChar.match(rules.other.unicodeAlphaNumeric)) return false;
	const nextChar = punctuationNext(match);
	const previousCjk = previousIsPunctuationOrCjk(prevChar, maskedSrc, src);
	if (!nextChar || !prevChar || rules.inline.punctuation.exec(prevChar) || previousCjk || CJK.test(nextChar)) {
		const lLength = [...match[0]].length - 1;
		let rDelim;
		let rLength;
		let delimTotal = lLength;
		let midDelimTotal = 0;
		const afterSameDelimiter = optionalNext && prevChar === match[0][0];
		const gfm = rules.inline.emStrongRDelimAst.source.includes("(?!~)");
		const endReg = match[0][0] === "*" ? (gfm ? R_DELIM_AST_GFM : R_DELIM_AST) : R_DELIM_UND;
		endReg.lastIndex = 0;
		const masked = maskedSrc.slice(-1 * src.length + lLength);
		let close;
		while ((close = endReg.exec(masked)) != null) {
			rDelim = close[1] || close[2] || close[3] || close[4] || close[5] || close[6];
			if (!rDelim) continue;
			rLength = [...rDelim].length;
			const cjk = touchesCjk(close, masked);
			const leftOnly = !!(close[3] || close[4]);
			const either = !!(close[5] || close[6]);
			if (leftOnly && !cjk) {
				delimTotal += rLength;
				continue;
			}
			if (either || cjk) {
				// CommonMark emphasis rules 9 and 10.
				if (lLength % 3 && !((lLength + rLength) % 3)) {
					midDelimTotal += rLength;
					continue;
				}
				if (afterSameDelimiter && either) break;
			}
			delimTotal -= rLength;
			if (delimTotal > 0) continue;
			rLength = Math.min(rLength, rLength + delimTotal + midDelimTotal);
			const lastCharLength = close[0].codePointAt(0) > 0xffff ? 2 : 1;
			const raw = src.slice(0, lLength + close.index + lastCharLength + rLength);
			if (Math.min(lLength, rLength) % 2) {
				const text = raw.slice(1, -1);
				return { type: "em", raw, text, tokens: this.lexer.inlineTokens(text) };
			}
			const text = raw.slice(2, -2);
			return { type: "strong", raw, text, tokens: this.lexer.inlineTokens(text) };
		}
	}
	return undefined;
}

/** The package's default export: a marked extension whose tokenizer overrides emStrong. */
export default function markedCjkFriendly() {
	return { tokenizer: { emStrong } };
}
