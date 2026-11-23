---
title: The website
description: How pisharp.ai is built, where its content lives, and how changes publish.
---

This site, [pisharp.ai](https://pisharp.ai), is an [Astro](https://astro.build) site with the [Starlight](https://starlight.astro.build) documentation theme. Its source is in [`website/`](https://github.com/jkelly/PiSharp/tree/main/website) in the PiSharp repository, and every push to `main` that touches it publishes at once.

## Layout

| Path | Contents |
| --- | --- |
| `astro.config.mjs` | The site and Starlight configuration, including the sidebar. |
| `src/content.config.ts` | The content collections and the changelog schema. |
| `src/content/docs/docs/` | The documentation pages, served under `/docs/`. |
| `src/content/changelog/` | One Markdown file per release, for the [changelog](/changelog/). |
| `src/pages/` | The custom pages: home (`index.astro`), [Samples](/samples/) (`samples.astro`), [SDK](/sdk/), [Extensions](/extensions/), [Parity](/parity/), and the changelog with its RSS feed. |
| `src/components/` | `SitePage.astro`, the layout of the custom pages, and Starlight overrides for the site title, page title and footer. |
| `src/data/` | The data the pages read. See [Data files](#data-files). |
| `src/styles/custom.css` | Theme colors and shared styles. |
| `src/assets/`, `public/` | The logo and the favicon. |

The site uses Astro 7 and Starlight 0.42, with system fonts and `sharp` for images. Starlight adds search, the sidebar, dark mode and an "Edit page" link to each docs page, which points at the file on GitHub.

## Docs pages

Each docs page is a plain Markdown file. The file's path under `src/content/docs/` is its slug: `src/content/docs/docs/guides/durable.md` is `docs/guides/durable`, served at `/docs/guides/durable/`. The frontmatter holds the page's `title` and `description`, and optionally a sidebar badge:

```yaml
---
title: Media tools
description: Generate images, video, speech and music, embed and rerank text, and ask decision models from pisharp durable. Experimental since PiSharp 1.2.0.2.
sidebar:
  badge:
    text: Experimental
    variant: caution
---
```

Starlight renders the title, so a page starts with an intro paragraph and uses `##` headings, not `#`. Asides are written as `:::note`, `:::tip` or `:::caution[Title]` blocks. By convention the pages are plain `.md`, without MDX, imports or components.

No link checker runs when the site builds. Check new links yourself, and write internal links root-relative with a trailing slash, such as `/docs/reference/settings/`.

`src/data/site.ts` reads the published version from `compatibility/public-release.json`, including `previousReleases` while a future candidate's publication flags are false. Markdown pages under `src/content/docs/` can use `\{{PISHARP_VERSION}}` in prose or fenced code blocks; `src/plugins/satteri-pisharp-version.mjs` replaces it at build time with that published version so installation snippets stay current automatically. Promote a changelog to `status: released` and add its actual UTC date only after publication is verified; released entries enter RSS and gain their GitHub release link. Future candidate changelogs use `status: in-progress` and stay out of released RSS. Mark any future unpublished SDK library with `unpublished: true` and provide a source guide instead of a NuGet install command.

At build time, `src/pages/llms.txt.ts` and `src/pages/llms-full.txt.ts` also generate `/llms.txt` and `/llms-full.txt` so coding agents can consume the documentation and sample catalog cleanly.

## The sidebar

The sidebar in `astro.config.mjs` is listed by hand, as groups of slugs:

```js
{
  label: 'Contribute',
  items: [
    'docs/contribute/build',
    'docs/contribute/testing',
    // ...
  ],
},
```

A new page doesn't appear in the sidebar until you add its slug. Every slug the sidebar lists must exist, or the build fails. Entries with a `label` and `link` point outside the docs, like the "Implementation plans" link and the collapsed "Site" group.

## Content collections

`src/content.config.ts` defines two collections:

| Collection | Source | Schema |
| --- | --- | --- |
| `docs` | Starlight's docs loader | Starlight's docs schema |
| `changelog` | `src/content/changelog/**/*.md` | `version`, `kind`, `baseline`, `date`, `status`, `labels` |

## Changelog entries

Each release has one file, `src/content/changelog/<version>.md`:

```yaml
---
version: 1.2.0.1
kind: patch
baseline: 1.2.0
date: 2026-10-16
labels: [Experimental]
---
```

| Field | Type | Meaning |
| --- | --- | --- |
| `version` | string | The PiSharp version. |
| `kind` | `sync` or `patch` | `sync` moves the Pi baseline; `patch` is a C#-only release with a fourth number. |
| `baseline` | string | The Pi version it is based on, without the `v`. |
| `date` | date, optional | The release date. |
| `status` | `released` or `in-progress` | Defaults to `released`. An in-progress entry gets an "In progress" chip. |
| `labels` | list of strings | Extra chips, such as `Experimental`. Defaults to none. |

The changelog page sorts entries by version, numerically, and links each released one to its GitHub release. A `sync` entry also links to Pi's release notes.

The body is the entry's text. The repository's `CHANGELOG.md`, which ships with the CLI, is generated from these files, and a test fails when the two differ. After you add or change an entry, regenerate it as [Releasing](/docs/contribute/releasing/#regenerate-changelogmd) shows.

## Data files

| File | Holds | Used by |
| --- | --- | --- |
| `src/data/site.ts` | The navigation, the external links, and `versions`: the current PiSharp release, its Pi baseline and the target baseline, read at build time from `../compatibility/public-release.json` and `../compatibility/target.lock.json`. `latestPi` is set by hand when Pi ships a release. | Home, parity and the other custom pages. |
| `src/data/packages.ts` | The NuGet packages, each with an id, a description, a group (`Core`, `Frontends` or `Extensions`) and the flags `nuget`, `tool`, `optional` and `experimental`. | Home and SDK pages. |
| `src/data/parity.json` | The parity assessment: per-area status (`ported`, `partial`, `planned`, `wont-port`, `unknown`) with notes and evidence, the deviations from Pi, and the phases. | [Parity](/parity/) page. |
| `src/data/samples.ts` | Reads `../samples/agents/catalog.json` at build time, enforces 1:1 metadata coverage for all 62 practical agents, and exports the full catalog (Durable, Chord, CustomAgents, and extension samples included). | [Samples](/samples/) explorer. |
| `src/data/changelog.ts` | `getReleases()`, which reads the changelog collection in version order. | Changelog page and feed. |

Because `site.ts` reads `compatibility/`, the site reflects the release record. The current release is the one `public-release.json` marks as published on NuGet. See [After the release](/docs/contribute/releasing/#after-the-release). `src/data/parity.json` is not `compatibility/parity.json`; see [Two parity files](/docs/contribute/porting/#two-parity-files).

## Local development

You need Node.js 22 or later.

```sh
cd website
npm install
npm run dev
```

`npm run dev` serves the site at `http://localhost:4321` and reloads as you edit. `npm run build` writes the static site to `website/dist/`, `npm run check` verifies internal links and confirms all 62 entries from `samples/agents/catalog.json` are rendered on `/samples/`, and `npm run preview` serves that build. Run the commands from `website/`: `site.ts` and `samples.ts` find `compatibility/` and `samples/` relative to the current directory.

## Deployment

[`website.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/website.yml) builds and verifies the site on pull requests and pushes to `main` that touch `website/**`, `compatibility/**`, `samples/agents/catalog.json` or the workflow itself, and deploys to GitHub Pages on `main` pushes and manual runs. It installs the packages with Node.js 22, runs `npm run build` and `npm run check`, and deploys `website/dist`.

The workflow runs `npm install`, not `npm ci`, because `package-lock.json` is generated on Windows and leaves out Linux-only optional dependencies that `npm ci` would reject.

:::note[PR checks & live deploys]
Pull requests run the full Astro build and `npm run check` (internal links, `#anchor` targets, `\{{PISHARP_VERSION}}` token substitution, `/llms.txt` + `/llms-full.txt`, and all 62 `catalog.json` samples) without deploying. Merging to `main` publishes `website/dist` to GitHub Pages immediately.
:::

The custom domain is set in the repository's Pages settings, and its DNS is managed outside GitHub.

## Unreleased features

Because the site publishes from `main`, it documents features that are merged but in no release yet. Mark them so a reader can't mistake them for something on NuGet:

- Give the page the sidebar badge `Unreleased` with the `caution` variant, and end its description with "Experimental and unreleased."
- Start the page body with a `:::caution[Experimental and unreleased]` aside. It says the feature is on `main` but in no release or NuGet package, links [Build from source](/docs/contribute/build/), and names the `[Experimental]` id to suppress.
- When another page mentions the feature in passing, mark it inline "(experimental, unreleased)" and link its page.
- For a CLI-only feature, say that it is in no release yet and needs a CLI built from source.
- Don't add it to `packages.ts` until it ships as a package.

`RegistryReader` now ships in `PiSharp.Durable.Store` from 1.5.0.1; [its section](/docs/embed/durable/#registries-from-chord) names that release. A released experimental feature, such as [Pi Durable](/docs/guides/durable/) or [custom agents](/docs/custom-agents/overview/), uses an `Experimental` badge with the `caution` variant instead, and its aside names the release it shipped in. When a feature ships, remove its unreleased markers.
