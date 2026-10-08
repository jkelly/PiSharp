import { getCollection, type CollectionEntry } from 'astro:content';

/** Compare dotted versions numerically, so 0.99.1.2 sorts after 0.99.1 and before 1.1.0. */
function compareVersions(a: string, b: string) {
	const pa = a.split('.').map(Number);
	const pb = b.split('.').map(Number);
	for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
		const d = (pa[i] ?? 0) - (pb[i] ?? 0);
		if (d) return d;
	}
	return 0;
}

/** All releases, newest version first. */
export async function getReleases(): Promise<CollectionEntry<'changelog'>[]> {
	const entries = await getCollection('changelog');
	return entries.sort((a, b) => compareVersions(b.data.version, a.data.version));
}
