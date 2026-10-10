// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	site: 'https://pisharp.ai',
	integrations: [
		starlight({
			title: 'PiSharp',
			description: 'An independent native .NET port of the Pi coding agent.',
			logo: { src: './src/assets/mark.svg', alt: 'PiSharp' },
			favicon: '/favicon.svg',
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/jkelly/PiSharp' }],
			editLink: { baseUrl: 'https://github.com/jkelly/PiSharp/edit/main/website/' },
			customCss: [
				'@fontsource/ibm-plex-sans/400.css',
				'@fontsource/ibm-plex-sans/500.css',
				'@fontsource/ibm-plex-sans/600.css',
				'@fontsource/ibm-plex-sans/700.css',
				'@fontsource/instrument-serif/400.css',
				'@fontsource/instrument-serif/400-italic.css',
				'@fontsource/jetbrains-mono/400.css',
				'@fontsource/jetbrains-mono/700.css',
				'./src/styles/custom.css',
			],
			components: {
				SiteTitle: './src/components/overrides/SiteTitle.astro',
				PageTitle: './src/components/overrides/PageTitle.astro',
				Footer: './src/components/overrides/Footer.astro',
			},
			sidebar: [
				{
					label: 'Get started',
					items: ['docs', 'docs/quickstart', 'docs/coming-from-pi', 'docs/versioning'],
				},
				{
					label: 'Use PiSharp',
					items: ['docs/guides/providers', 'docs/guides/interactive', 'docs/guides/mcp'],
				},
				{
					label: 'Extend',
					items: ['docs/extensions/node-bridge', 'docs/extensions/build-an-extension', 'docs/extensions/packages'],
				},
				{
					label: 'Embed',
					items: ['docs/embed/sdk'],
				},
				{
					label: 'Reference',
					items: [
						'docs/reference/cli',
						'docs/reference/configuration',
						'docs/reference/architecture',
						{ label: 'Implementation plans', link: 'https://github.com/jkelly/PiSharp/tree/main/docs/plans' },
					],
				},
				{
					label: 'Site',
					collapsed: true,
					items: [
						{ label: 'Home', link: '/' },
						{ label: 'SDK', link: '/sdk/' },
						{ label: 'Extensions', link: '/extensions/' },
						{ label: 'Parity', link: '/parity/' },
						{ label: 'Changelog', link: '/changelog/' },
					],
				},
			],
		}),
	],
});
