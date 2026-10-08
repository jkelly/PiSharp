import type { APIRoute } from 'astro';
import { getReleases } from '../../data/changelog';

const escape = (s: string) =>
	s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

export const GET: APIRoute = async ({ site }) => {
	const base = new URL('/changelog/', site);
	const items = (await getReleases())
		.filter((e) => e.data.status === 'released')
		.map((e) => {
			const link = new URL(`#v${e.data.version}`, base).href;
			const kind = e.data.kind === 'sync' ? `Pi sync, matches Pi ${e.data.baseline}` : `C# patch, baseline Pi ${e.data.baseline}`;
			return [
				'<item>',
				`<title>PiSharp ${escape(e.data.version)}</title>`,
				`<link>${escape(link)}</link>`,
				`<guid>${escape(link)}</guid>`,
				e.data.date ? `<pubDate>${e.data.date.toUTCString()}</pubDate>` : '',
				`<description>${escape(kind)}</description>`,
				'</item>',
			].join('');
		});

	const xml = `<?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel><title>PiSharp releases</title><link>${escape(base.href)}</link><description>PiSharp release notes</description>${items.join('')}</channel></rss>`;
	return new Response(xml, { headers: { 'Content-Type': 'application/rss+xml; charset=utf-8' } });
};
