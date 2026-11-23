# PiSharp 1.6.0 publication

The project owner authorized package publication on 2026-11-22. [PiSharp 1.6.0](https://github.com/jkelly/PiSharp/releases/tag/v1.6.0) was published at **2026-11-23 00:43:16 UTC** by the successful [tagged release workflow 45087319788](https://github.com/jkelly/PiSharp/actions/runs/45087319788). The immutable tag resolves to `711aa190dc2f4e339cf9232db48f70d8102504ff`. Publication documentation does not move that tag or replace its packages.

## Published packages and verification

- **29 NuGet packages:** 28 libraries and `PiSharp.Cli`, all version **1.6.0**. The publishing log records 29 accepted uploads and no duplicate skips.
- All 29 canonical NuGet version indexes expose 1.6.0. Every package was downloaded from the official flat-container feed and compared with its GitHub release asset. Every ZIP payload entry matched byte-for-byte, excluding NuGet's added repository signature. Each nuspec names version 1.6.0 and the exact tagged source commit (`711aa190dc2f4e339cf9232db48f70d8102504ff`). The signed NuGet containers retain their own hashes, separate from the unsigned GitHub archives.
- A fresh consumer outside the checkout restored exclusively from the official NuGet feed, with an isolated package cache and no project references. All PiSharp dependencies resolved as packages at exactly 1.6.0. The five-library consumer exercised Chord, native hosting, Durable.Store, Env.Protocol and Env, including a byte-schema host call, SQLite adapter and exported public-signature visibility checks. Compilation without experimental opt-in failed with the expected diagnostics (`PISHARP_CHORD001`, `PISHARP_DURABLE001`, `PISHARP_ENV001`); the opted-in build and execution passed.
- A private installation of `PiSharp.Cli` from NuGet returned **1.6.0** and passed its help smoke test. No global tool installation was changed.

The [machine-readable package and consumer verification](release-1.6.0-package-verification.json) records package hashes, source identities and consumer results.

## Exact-tag qualification

All **36 active release jobs succeeded**; the two optional browser jobs were intentionally skipped by the tag workflow's `include_web: false` configuration. The required core, Durable, gap-regression and native-environment gates ran at the exact tagged source. The build job also ran all 22 serial offline release suites before packing and validating the 29 archives and the CLI's native SQLite payload.

The source implementation had already passed PR #63 (63 CI checks at `42aac5ada0d8071fb5d5311561ff9773fbb634d0`), the MCP HTTP close fix in PR #66 (64 checks), the release preparation pull request PR #65 (64/64 checks) and release dry run `45082383717` (36/36 active jobs). The tagged workflow independently qualified the merged release commit `711aa190dc2f4e339cf9232db48f70d8102504ff`.

This release's 29 GitHub assets are the managed packages. Native daemon checks passed, but no 1.6.0 standalone daemon ZIPs are claimed by this package publication. Previous 1.5.0.1 native archives retain their original source and qualification. Experimental APIs, explicit skips, live-provider accounts, real SSH hosts, physical macOS input and other untested environments retain their documented boundaries. No complete upstream, all-provider or all-platform parity is claimed.

## Documentation and preserved history

The publication update records the actual tag, release run and publication time; promotes the dated changelog and RSS entry; and updates current package installation examples and the website's published version. Earlier release records, source locks, validation receipts and historical parity assessments retain their original identities and scope. The website deployment is checked after the publication documentation is merged.
