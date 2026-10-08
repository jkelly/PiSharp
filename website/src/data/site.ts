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
	issues: 'https://github.com/jkelly/PiSharp/issues',
	contributing: 'https://github.com/jkelly/PiSharp/blob/main/CONTRIBUTING.md',
	plans: 'https://github.com/jkelly/PiSharp/tree/main/docs/plans',
	compatibility: 'https://github.com/jkelly/PiSharp/tree/main/compatibility',
	samples: 'https://github.com/jkelly/PiSharp/tree/main/samples/extensions',
	pi: 'https://pi.dev',
	piChangelog: 'https://pi.dev/changelog',
	piBlogPost: 'https://mariozechner.at/posts/2025-11-30-pi-coding-agent/',
};

/** The Pi baseline, read at build time from the port's pinned compatibility lock file. */
function readBaseline() {
	try {
		const lock = JSON.parse(
			readFileSync(resolve(process.cwd(), '../compatibility/baseline.lock.json'), 'utf8'),
		);
		return {
			tag: String(lock.source?.tag ?? 'v0.99.1'),
			commit: String(lock.source?.commit ?? ''),
			repository: String(lock.source?.repository ?? 'https://github.com/earendil-works/pi'),
		};
	} catch {
		return { tag: 'v0.99.1', commit: '', repository: 'https://github.com/earendil-works/pi' };
	}
}

const baseline = readBaseline();

export const versions = {
	/** PiSharp's version is the Pi version it matches; a fourth segment marks C#-only patches. */
	pisharp: baseline.tag.replace(/^v/, ''),
	baseline: baseline.tag.replace(/^v/, ''),
	baselineCommit: baseline.commit,
	baselineUrl: `${baseline.repository}/releases/tag/${baseline.tag}`,
	/** Update by hand when Pi ships a new release. */
	latestPi: '1.1.0',
};

export const inSync = versions.baseline === versions.latestPi;
