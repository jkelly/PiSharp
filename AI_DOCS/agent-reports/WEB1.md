# WEB1 — website and candidate changelog

Status: **complete**, 2026-11-08. Root: `C:/Users/jksof/.codex/worktrees/close-upstream-gaps/PiSharp`. Requested model/reasoning: gpt-6.1-sol/high; requested ultrafast tier has no exposed control and is unverified.

## Outcome

The existing Astro/Starlight site now documents the November 8 gap implementation and the unpublished 1.5.0.1 candidate. Published labels remain 1.5.0/Pi 1.5.0. The five added libraries have source and documentation links instead of NuGet links or install commands. Inventory is 28 libraries plus the CLI, 29 packages. The candidate entry is `kind: patch`, `baseline: 1.5.0`, `status: in-progress`, with no release date or release link; RSS excludes it.

New source-backed guides cover Chord v2/native hosting and separate protocol-4 legacy runtime, Durable.Store and real-host examples 35–37, Env protocol/client/adapters/SSH/native daemon, Anthropic continuation state/cleanup/prepared payloads, and evaluation tooling. Existing guides cover new Durable request/compaction/submission APIs, provider/tool/lifecycle corrections, auth login/logout and accurate update outcomes. Releasing now describes all four reusable validation gates and candidate/package/publication boundaries.

Published parity rows, historical notes and pins remain intact. The parity page explicitly labels that historical assessment and adds current candidate source coverage; the roadmap appends a candidate phase. No all-platform, live-provider, hosted-job or complete-upstream qualification is claimed.

## Changed files

- `website/astro.config.mjs` — sidebar discovery for new guides.
- `website/package-lock.json` — missing optional `@emnapi/core` and `@emnapi/runtime` records repaired so npm 11 can clean-install the existing dependency graph.
- `website/src/data/packages.ts` — five unpublished candidate packages, experimental IDs, source-guide metadata.
- `website/src/data/parity.json` — appended candidate roadmap phase; historical assessment retained.
- `website/src/pages/index.astro`, `sdk.astro`, `parity.astro` — candidate discovery, package publication state and qualification boundaries.
- `website/src/content/changelog/1.5.0.1.md` and generated root `CHANGELOG.md`.
- New docs: `guides/gap-updates.md`; `embed/chord.md`; `embed/durable-store.md`; `embed/env.md`; `embed/anthropic-threads.md`; `contribute/evaluations.md` under `website/src/content/docs/docs/`.
- Updated docs under that same root: `index.md`, `versioning.md`, `embed/sdk.md`, `embed/durable.md`, `embed/telemetry.md`, `guides/durable.md`, `guides/providers.md`, `extensions/node-bridge.md`, `reference/architecture.md`, `reference/cli.md`, `reference/settings.md`, `reference/tools.md`, `contribute/releasing.md`, `contribute/website.md`.

`website/src/data/site.ts` required no code change: the REL1 metadata preserves the published flat fallback record. Its actual generated output was verified after candidate metadata changed.

## Verification

1. `npm ci --cache ../artifacts/website-npm-cache --no-audit --no-fund` initially hit sandbox network denial, then the network-enabled run identified two missing optional lock records. `npm install --package-lock-only --cache ../artifacts/website-npm-cache --no-audit --no-fund --fetch-retries=0` repaired them. The following clean `npm ci` succeeded (273 packages). Cache stayed inside workspace artifacts; no global npm config changed.
2. `$env:ASTRO_TELEMETRY_DISABLED='1'; npm run build` succeeded after all edits: 60 static pages, Pagefind index and sitemap. Disabling telemetry avoids Astro writing outside the workspace. Nonfatal Starlight warnings mention absent optional `i18n` collection/docs 404 entry; the build generated `404.html` successfully.
3. Python HTML parsing checked **4,383 root-relative links across all 60 pages**, including fragments: **zero missing targets/fragments**. Receipt: `artifacts/release-prep/website-verification.json`. Also checked 27 repository source links in the six new guides against actual checkout paths: zero missing.
4. The final receipt verifies published header `PiSharp 1.5.0 is out, tracking Pi v1.5.0.`, candidate present on changelog, candidate absent from RSS, published 1.5.0 present in RSS, exactly 29 package inventory rows and five unpublished rows.
5. Preview: `$env:ASTRO_TELEMETRY_DISABLED='1'; npm run preview -- --host 127.0.0.1 --port 4321`. Playwright inspected home, SDK, parity, candidate overview, Chord, Store, Env, threads, Durable, evals, releasing and changelog. All rendered expected titles/content with no desktop horizontal overflow. Home/SDK/candidate overview/Env/parity also had no overflow at 390px width.
6. Browser interactions passed: C# patch filter retains candidate, parity tab switching works, candidate has exactly one In progress badge and zero GitHub release links. SDK has five Unpublished source-guide links. Screenshots inspected visually: `artifacts/release-prep/website-candidate-desktop.png` and `website-parity-mobile.png`.
7. Generated `CHANGELOG.md` reproduces `ChangelogSyncCases.Generate`: numeric descending versions, all 13 website entries, optional dates, trimmed bodies, absolute site links, UTF-8/no BOM/LF. No .NET build/test was run because the main agent owns .NET execution.

## Blockers and follow-ups

No website blocker. Main owns final diff/integrity review, .NET changelog assertion, candidate package/consumer gate, exact-SHA hosted qualification, commit/push and deployment. Candidate publication flags must stay false until actual publication is verified. No Git, remote, tag, deployment or .NET operations were performed by WEB1. The preview process was stopped after verification.
