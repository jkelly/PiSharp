import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

function readPublishedVersion() {
	try {
		const releasePath = resolve(process.cwd(), '../compatibility/public-release.json');
		const release = JSON.parse(readFileSync(releasePath, 'utf8'));
		if (release.publication?.nugetPackagesPublished) return String(release.version);
		const previous = (release.previousReleases ?? [])
			.filter((entry) => entry.nugetPackagesPublished)
			.at(-1);
		if (previous) return String(previous.version);
	} catch {}
	return '0.99.1';
}

const TOKEN = '{{PISHARP_VERSION}}';
const ESCAPED_TOKEN = '\\{{PISHARP_VERSION}}';
const PLACEHOLDER = '\u0000PISHARP_VERSION_LITERAL\u0000';

function replaceTokens(str, version) {
	if (!str.includes(TOKEN)) return str;
	return str
		.replaceAll(ESCAPED_TOKEN, PLACEHOLDER)
		.replaceAll(TOKEN, version)
		.replaceAll(PLACEHOLDER, TOKEN);
}

/**
 * Sätteri mdast plugin that replaces `{{PISHARP_VERSION}}` in Markdown prose, inline code,
 * links and fenced code blocks with the current published NuGet version from
 * `compatibility/public-release.json`. Escape literally with `\{{PISHARP_VERSION}}`.
 */
export default function createPiSharpVersionPlugin() {
	const version = readPublishedVersion();
	const patchValue = (node, ctx) => {
		if (typeof node.value === 'string' && node.value.includes(TOKEN)) {
			ctx.setProperty(node, 'value', replaceTokens(node.value, version));
		}
	};
	return {
		name: 'pisharp-version',
		text: patchValue,
		inlineCode: patchValue,
		code(node, ctx) {
			patchValue(node, ctx);
			if (typeof node.meta === 'string' && node.meta.includes(TOKEN)) {
				ctx.setProperty(node, 'meta', replaceTokens(node.meta, version));
			}
		},
		html: patchValue,
		link(node, ctx) {
			if (typeof node.url === 'string' && node.url.includes(TOKEN)) {
				ctx.setProperty(node, 'url', replaceTokens(node.url, version));
			}
		},
	};
}
