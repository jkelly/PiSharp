# PUB2 — local publication wording

Status: **complete and source frozen**, 2026-11-09 UTC. Assignment: hard/high, gpt-6.1-sol, requested Ultrafast tier unverified. Main owns publication, official-feed verification, remote operations, metadata and deployment. No .NET or Git operations performed. The final checks below supersede the preliminary mixed-state build.

## Prepared changes

- Promote `website/src/content/changelog/1.5.0.1.md` to `status: released` with confirmed UTC date **2026-11-09**, GitHub release link and exact-version CLI update command. Regenerate root `CHANGELOG.md` from all 13 entries.
- Mark all five added experimental libraries as published NuGet packages in `website/src/data/packages.ts`. `sdk.astro` now links all 29 packages at the metadata-driven published version and gives exact-version install commands. The inventory is 28 libraries plus the CLI.
- Update homepage, SDK, parity/roadmap, overview, versioning, current feature guides, release-process docs and architecture to released 1.5.0.1 wording. Preserve historical 1.5.0 changelog entries, parity assessment, source/capture pins and experimental/platform limits.
- Update current SDK XML references and versioned shell install examples to 1.5.0.1 throughout website docs. The five new libraries retain their diagnostic IDs, exact sibling dependencies and framework requirements.
- Update `README.md` and `docs/release-notes/1.5.0.1.md` with release links, package count, native-asset availability and qualified source evidence. Native downloads link to the release assets without cluttering guides with all three URLs.
- Link main's intended permanent proof `AI_DOCS/release-1.5.0.1-publication-results.md`. It must exist before deployment. State source qualification at immutable `8144e3eaa138a5faf56f5cbb0de477a56fd48f8f`: Core 11, Durable 11, gap 10, native 3, macOS 20; WebAgent 543 on all three platforms; all 29 archives and a fresh external consumer of the five new libraries. Do not imply live-provider, real SSH-host, physical Apple Terminal or other-RID qualification.

`compatibility/public-release.json`, `VersionPrefix`, runtime and tests were not edited. Published wording remains **local only** under main's instruction. Main waits for complete official-feed verification before push/deployment.

## Preliminary checks

- `$env:ASTRO_TELEMETRY_DISABLED='1'; npm run build`: passed, 60 static pages plus Pagefind and sitemap. Same optional i18n/docs-404 warnings as earlier builds.
- All root-relative generated links/fragments pass. Receipt: `artifacts/release-prep/website-publication-preliminary-verification.json` (60 pages, **4,383 links**, zero failures; 29 SDK NuGet links; promoted changelog present in RSS).
- Static inventory: exactly 28 `nuget: true` libraries plus CLI, no `unpublished: true` rows. Changelog is released with the provisional date. Historical release entries were not edited.
- `site.ts` still correctly derives the header/package link version from root-owned metadata. During this preliminary build publication flags are false, so header/SDK version remains 1.5.0. This mixed local preparation output is **not deployment-ready** and is expected until main records verified publication.

## Final publication checks — passed

Main confirmed public GitHub release **438734301**, workflow **42558097795** success and actual `publishedAt` **2026-11-09T04:11:05Z**. The provisional date was correct. Main updated publication flags to true and created the permanent publication evidence file. Main separately owns the remaining official-feed download check for the large CLI package before deployment.

- Clarified every ambiguous owned release-qualification phrase: validation covers **all 29 archives and a fresh external consumer of the five new libraries**. Named the 543-case three-platform suite **WebAgent** to match the actual evidence.
- Rebuilt after the metadata checkpoint and final wording edits using `$env:ASTRO_TELEMETRY_DISABLED='1'; npm run build`: **passed, 60 pages**, Pagefind and sitemap. Optional i18n/docs-404 warnings remain nonfatal.
- Parsed all generated HTML: **4,383 root-relative links and fragments, zero failures**.
- Header exactly names **PiSharp 1.5.0.1 / Pi v1.5.0**.
- All **29 SDK NuGet URLs** match the exact declared package set and version **1.5.0.1**. All **29 install commands** match those IDs and the same version; CLI uses `dotnet tool install`, the 28 libraries use `dotnet add package`.
- RSS has exactly one 1.5.0.1 release entry with UTC date **2026-11-09** and the correct changelog fragment URL.
- All three root-owned publication flags are true, the exact actual timestamp matches, and `AI_DOCS/release-1.5.0.1-publication-results.md` exists.
- Recomputed the C# changelog generator semantics independently and compared exact bytes: **13 entries match**, no BOM or CRLF.
- Full final receipt: `artifacts/release-prep/website-publication-final-verification.json`.

No browser repeat was needed for wording-only changes. No .NET, Git, remote, deployment or publication actions were performed by PUB2. Historical 1.5.0 release entries, baseline/evidence pins and qualification limits remain intact. No website blocker; main owns final publication/feed completion, push and live deployment verification.
