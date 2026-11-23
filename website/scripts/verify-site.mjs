import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

const websiteDir = process.cwd();
const distDir = resolve(websiteDir, 'dist');
const catalogPath = resolve(websiteDir, '../samples/agents/catalog.json');

if (!existsSync(distDir)) {
	console.error(`Missing built site at ${distDir}. Run "npm run build" first.`);
	process.exit(1);
}

function walkHtml(dir) {
	const files = [];
	for (const entry of readdirSync(dir)) {
		const full = join(dir, entry);
		const stat = statSync(full);
		if (stat.isDirectory()) files.push(...walkHtml(full));
		else if (entry.endsWith('.html')) files.push(full);
	}
	return files;
}

const htmlFiles = walkHtml(distDir);
const brokenLinks = [];
const unreplacedTokens = [];

// Verify all root-relative internal href targets (and #anchors) resolve to built files in dist/.
const hrefPattern = /href="(\/[^"#?]*)(?:\?[^"#]*)?(?:#([^"]+))?"/g;
const htmlCache = new Map();
const readCached = (p) => {
	if (!htmlCache.has(p)) htmlCache.set(p, readFileSync(p, 'utf8'));
	return htmlCache.get(p);
};

for (const file of htmlFiles) {
	const content = readCached(file);
	if (content.includes('{{PISHARP_VERSION}}') && !file.replace(/\\/g, '/').endsWith('/docs/contribute/website/index.html')) {
		unreplacedTokens.push(file.replace(distDir, 'dist'));
	}
	let match;
	while ((match = hrefPattern.exec(content)) !== null) {
		const rawPath = match[1];
		const hash = match[2];
		if (rawPath.startsWith('//') || rawPath.startsWith('/_astro/') || rawPath.startsWith('/pagefind/')) continue;
		const clean = rawPath.replace(/^\/+/, '');
		const candidates = clean === ''
			? [join(distDir, 'index.html')]
			: [
					join(distDir, clean),
					join(distDir, clean, 'index.html'),
					join(distDir, `${clean}.html`),
			  ];
		const target = candidates.find((c) => existsSync(c));
		if (!target) {
			brokenLinks.push({ file: file.replace(distDir, 'dist'), href: hash ? `${rawPath}#${hash}` : rawPath });
			continue;
		}
		if (hash && target.endsWith('.html')) {
			const targetHtml = readCached(target);
			if (!targetHtml.includes(`id="${hash}"`)) {
				brokenLinks.push({ file: file.replace(distDir, 'dist'), href: `${rawPath}#${hash} (missing #${hash})` });
			}
		}
	}
}

// Verify every entry in samples/agents/catalog.json appears on /samples/.
const samplesPagePath = join(distDir, 'samples', 'index.html');
if (!existsSync(samplesPagePath)) {
	console.error('Missing built /samples/ page at dist/samples/index.html');
	process.exit(1);
}
const samplesHtml = readFileSync(samplesPagePath, 'utf8');
const payloadMatch = samplesHtml.match(/id="samples-payload"[^>]*>([\s\S]*?)<\/script>/);
if (!payloadMatch) {
	console.error('Missing samples-payload JSON block on /samples/.');
	process.exit(1);
}
const payloadEntries = JSON.parse(payloadMatch[1]);
const renderedCards = (samplesHtml.match(/data-sample-id="/g) ?? []).length;
if (payloadEntries.length !== renderedCards) {
	console.error(`Mismatch on /samples/: ${payloadEntries.length} payload entries vs ${renderedCards} rendered cards.`);
	process.exit(1);
}
const totalRunnable = payloadEntries.reduce((sum, item) => sum + (item.exampleCount ?? 1), 0);

const catalog = JSON.parse(readFileSync(catalogPath, 'utf8'));
const missingSamples = [];
for (const entry of catalog) {
	if (!samplesHtml.includes(`data-sample-id="${entry.folder}"`)) {
		missingSamples.push(`#${entry.number} (${entry.folder})`);
	}
}

// Verify llms.txt and llms-full.txt agent-readable documentation bundles.
for (const llmsFile of ['llms.txt', 'llms-full.txt']) {
	const llmsPath = join(distDir, llmsFile);
	if (!existsSync(llmsPath)) {
		console.error(`Missing built ${llmsFile} at ${llmsPath}`);
		process.exit(1);
	}
	const llmsText = readFileSync(llmsPath, 'utf8');
	if (llmsText.trim().length < 200) {
		console.error(`Built ${llmsFile} appears empty or truncated (${llmsText.length} bytes).`);
		process.exit(1);
	}
	if (!llmsText.includes('# PiSharp')) {
		console.error(`Built ${llmsFile} missing expected PiSharp header.`);
		process.exit(1);
	}
}

if (unreplacedTokens.length > 0) {
	console.error(`Found unreplaced {{PISHARP_VERSION}} token in ${unreplacedTokens.length} file(s): ${unreplacedTokens.join(', ')}`);
	process.exit(1);
}

if (brokenLinks.length > 0) {
	console.error(`Found ${brokenLinks.length} broken internal link(s):`);
	for (const item of brokenLinks.slice(0, 30)) {
		console.error(`  ${item.file} -> ${item.href}`);
	}
	process.exit(1);
}

if (missingSamples.length > 0) {
	console.error(`Missing ${missingSamples.length} catalog.json sample(s) on /samples/: ${missingSamples.join(', ')}`);
	process.exit(1);
}

console.log(
	`Verified ${htmlFiles.length} built HTML pages (0 broken internal links/anchors, 0 unreplaced version tokens), llms.txt + llms-full.txt, all ${catalog.length} catalog.json samples, and ${renderedCards} explorer cards (${totalRunnable} runnable samples) on /samples/.`,
);
