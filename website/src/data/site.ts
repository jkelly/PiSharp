import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

export const nav = [
	{ label: 'Docs', href: '/docs/' },
	{ label: 'SDK', href: '/sdk/' },
	{ label: 'Extensions', href: '/extensions/' },
	{ label: 'Parity', href: '/parity/' },
	{ label: 'Changelog', href: '/changelog/' },
];

export const links = {
	github: 'https://github.com/jkelly/PiSharp',
	releases: 'https://github.com/jkelly/PiSharp/releases',
	nuget: 'https://www.nuget.org/profiles/PiSharpJK',
	issues: 'https://github.com/jkelly/PiSharp/issues',
	contributing: 'https://github.com/jkelly/PiSharp/blob/main/CONTRIBUTING.md',
	plans: 'https://github.com/jkelly/PiSharp/tree/main/docs/plans',
	compatibility: 'https://github.com/jkelly/PiSharp/tree/main/compatibility',
	samples: 'https://github.com/jkelly/PiSharp/tree/main/samples/extensions',
	pi: 'https://pi.dev',
	piChangelog: 'https://pi.dev/changelog',
	piBlogPost: 'https://mariozechner.at/posts/2025-11-30-pi-coding-agent/',
};

const readJson = (path: string) => JSON.parse(readFileSync(resolve(process.cwd(), path), 'utf8'));

/** The Pi baseline the port currently targets, read at build time from the compatibility lock. */
function readTarget() {
	try {
		const lock = readJson('../compatibility/target.lock.json');
		return {
			tag: String(lock.source.tag),
			commit: String(lock.source.commit),
			repository: String(lock.source.repository ?? 'https://github.com/earendil-works/pi'),
		};
	} catch {
		return { tag: 'v1.1.0', commit: '', repository: 'https://github.com/earendil-works/pi' };
	}
}

/** The newest published PiSharp release and the Pi baseline it tracks, from the release record. */
function readPublished() {
	try {
		const release = readJson('../compatibility/public-release.json');
		if (release.publication?.nugetPackagesPublished) return { version: String(release.version), tag: String(release.upstream.tag) };
		const previous = (release.previousReleases ?? []).filter((entry: { nugetPackagesPublished?: boolean }) => entry.nugetPackagesPublished).at(-1);
		if (previous) return { version: String(previous.version), tag: String(previous.upstreamTag) };
	} catch {}
	return { version: '0.99.1', tag: 'v0.99.1' };
}

const target = readTarget();
const published = readPublished();

export const versions = {
	/** PiSharp's version is the Pi version it matches; a fourth segment marks C#-only patches. */
	pisharp: published.version,
	/** The Pi release the published PiSharp version matches. */
	baseline: published.tag.replace(/^v/, ''),
	baselineCommit: target.tag === published.tag ? target.commit : '',
	baselineUrl: `${target.repository}/releases/tag/${published.tag}`,
	/** The Pi release the port is currently being synced to. */
	target: target.tag.replace(/^v/, ''),
	/** Update by hand when Pi ships a new release. */
	latestPi: '1.1.0',
};

export const inSync = versions.baseline === versions.latestPi;
