// PiSharp replacement for packages/coding-agent/src/utils/syntax-highlight.ts, which wraps the
// highlight.js npm package. highlight.js is not vendored, so no language is reported as supported:
// upstream callers (theme.ts highlightCode / getMarkdownTheme().highlightCode) then fall back to their
// plain `mdCodeBlock` coloring, exactly as upstream does for unknown languages.

export function loadAllHighlightLanguages() {
	return Promise.resolve();
}

export function renderHighlightedHtml(html, _theme = {}) {
	return String(html)
		.replace(/<[^>]*>/g, "")
		.replace(/&lt;/g, "<")
		.replace(/&gt;/g, ">")
		.replace(/&quot;/g, '"')
		.replace(/&#x27;|&#39;/g, "'")
		.replace(/&amp;/g, "&");
}

export function highlight(code, _options = {}) {
	return code;
}

export function supportsLanguage(_name) {
	return false;
}
