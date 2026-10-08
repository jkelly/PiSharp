# PiSharp website

The source for [pisharp.ai](https://pisharp.ai), built with [Astro](https://astro.build) and [Starlight](https://starlight.astro.build). It deploys to GitHub Pages through `.github/workflows/website.yml` on every push to `main` that touches `website/` or `compatibility/`.

## Run locally

Requires Node 22 or later.

```powershell
cd website
npm install
npm run dev      # http://localhost:4321
npm run build    # output in website/dist
```

## Where things live

| What | Where |
| --- | --- |
| Docs pages | `src/content/docs/docs/` (served under `/docs/`), sidebar in `astro.config.mjs` |
| Home, SDK, Extensions, Parity, Changelog | `src/pages/` |
| Changelog entries | `src/content/changelog/<version>.md` (`kind: sync` or `kind: patch`) |
| Parity statuses and roadmap | `src/data/parity.json` |
| NuGet package list | `src/data/packages.ts` |
| Latest Pi version, nav and links | `src/data/site.ts` |
| Theme colors and shared styles | `src/styles/custom.css` |

The Pi baseline shown on the site is read at build time from `../compatibility/target.lock.json`.

## Versioning

PiSharp's version is the Pi version it matches. C#-only patches add a fourth number (`0.99.1.1`). Add a changelog entry with `kind: patch` for those, and `kind: sync` when the baseline moves.

## Custom domain

The domain is set in the repository's **Settings → Pages → Custom domain** (`pisharp.ai`). DNS is managed at GoDaddy.
