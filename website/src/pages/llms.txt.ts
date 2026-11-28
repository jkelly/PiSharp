import type { APIRoute } from 'astro';
import { getCollection } from 'astro:content';
import { versions, links, hasSourceCandidate } from '../data/site';
import { packages } from '../data/packages';
import { allSamples, sampleStats } from '../data/samples';

export const GET: APIRoute = async ({ site }) => {
	const base = (site?.toString() ?? 'https://pisharp.ai').replace(/\/$/, '');
	const docs = (await getCollection('docs')).sort((a, b) => a.id.localeCompare(b.id));

	const lines: string[] = [
		`# PiSharp`,
		``,
		`> Independent native .NET 10 / C# port of the Pi coding agent. Current published release: PiSharp ${versions.pisharp} (Pi v${versions.baseline} baseline).`,
		...(hasSourceCandidate ? [`> Unreleased source preview for Pi and Pi Durable ${versions.target}. Preview changes: ${base}/changelog/#v${versions.target}. Build instructions: ${base}/docs/quickstart/#build-from-source.`] : []),
		``,
		`- Website: ${base}/`,
		`- Full documentation bundle: ${base}/llms-full.txt`,
		`- GitHub: ${links.github}`,
		`- NuGet: ${links.nuget}`,
		`- Install CLI: \`dotnet tool install -g PiSharp.Cli --version ${versions.pisharp}\``,
		``,
		`## Documentation`,
		``,
	];

	for (const doc of docs) {
		const slug = doc.id === 'docs' ? '/docs/' : `/${doc.id.replace(/\/index$/, '')}/`;
		const title = doc.data.title ?? doc.id;
		const desc = doc.data.description ? `: ${doc.data.description}` : '';
		lines.push(`- [${title}](${base}${slug})${desc}`);
	}

	lines.push(
		``,
		`## Published NuGet Packages (${packages.length} packages at version ${versions.pisharp})`,
		``,
	);
	for (const pkg of packages) {
		const tags: string[] = [];
		if (pkg.tool) tags.push('CLI tool');
		if (pkg.experimental) tags.push('experimental');
		const suffix = tags.length > 0 ? ` (${tags.join(', ')})` : '';
		lines.push(`- \`${pkg.id}\`${suffix}: ${pkg.description}`);
	}

	lines.push(
		``,
		`## Runnable Samples (${sampleStats.totalRunnableSamples} samples across ${allSamples.length} cards)`,
		``,
		`Browse interactively at ${base}/samples/.`,
		``,
	);
	for (const sample of allSamples) {
		lines.push(
			`- **${sample.title}** (\`${sample.id}\`, ${sample.runtime}, ${sample.category}): ${sample.summary} — Run: \`${sample.demoCommand}\``,
		);
	}

	return new Response(lines.join('\n') + '\n', {
		headers: { 'Content-Type': 'text/plain; charset=utf-8' },
	});
};
