import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { z } from 'astro/zod';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

export const collections = {
	docs: defineCollection({ loader: docsLoader(), schema: docsSchema() }),
	// One Markdown file per release in src/content/changelog/.
	// `kind: sync` moves the Pi baseline; `kind: patch` is a C#-only fix (fourth version segment).
	changelog: defineCollection({
		loader: glob({ pattern: '**/*.md', base: './src/content/changelog' }),
		schema: z.object({
			version: z.string(),
			kind: z.enum(['sync', 'patch']),
			baseline: z.string(),
			date: z.coerce.date().optional(),
			status: z.enum(['released', 'in-progress']).default('released'),
			labels: z.array(z.string()).default([]),
		}),
	}),
};
