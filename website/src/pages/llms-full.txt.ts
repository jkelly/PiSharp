import type { APIRoute } from 'astro';
import { getCollection } from 'astro:content';
import { versions, links } from '../data/site';
import { packages } from '../data/packages';
import { allSamples, sampleStats } from '../data/samples';

export const GET: APIRoute = async ({ site }) => {
	const base = (site?.toString() ?? 'https://pisharp.ai').replace(/\/$/, '');
	const docs = (await getCollection('docs')).sort((a, b) => a.id.localeCompare(b.id));

	const parts: string[] = [
		`# PiSharp — Complete Documentation (${versions.pisharp})`,
		``,
		`> Independent native .NET 10 / C# port of the Pi coding agent (Pi v${versions.baseline} baseline).`,
		`> Source: ${links.github} | Docs: ${base}/docs/ | Samples: ${base}/samples/`,
		``,
		`## Published Packages (${packages.length} at ${versions.pisharp})`,
		``,
		...packages.map((p) => `- \`${p.id}\`: ${p.description}`),
		``,
		`## Sample Catalog Summary (${sampleStats.totalRunnableSamples} runnable samples)`,
		``,
		...allSamples.map(
			(s) =>
				`- \`${s.id}\` (${s.runtime} / ${s.category}): ${s.title} — \`${s.demoCommand}\` (${links.github}/tree/main/${s.repoPath})`,
		),
		``,
	];

	for (const doc of docs) {
		const slug = doc.id === 'docs' ? '/docs/' : `/${doc.id.replace(/\/index$/, '')}/`;
		const body = (doc.body ?? '').replaceAll('{{PISHARP_VERSION}}', versions.pisharp).trim();
		parts.push(
			`---`,
			``,
			`# ${doc.data.title ?? doc.id}`,
			`URL: ${base}${slug}`,
			doc.data.description ? `Description: ${doc.data.description}` : '',
			``,
			body,
			``,
		);
	}

	return new Response(parts.filter((line) => line !== undefined).join('\n') + '\n', {
		headers: { 'Content-Type': 'text/plain; charset=utf-8' },
	});
};
