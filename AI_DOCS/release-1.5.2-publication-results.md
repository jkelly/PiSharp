# PiSharp 1.5.2 publication

The project owner authorized package publication on 2026-11-12. [PiSharp 1.5.2](https://github.com/jkelly/PiSharp/releases/tag/v1.5.2) was published at **2026-11-12 07:25:31 UTC** by the successful [tagged release workflow 43171815825](https://github.com/jkelly/PiSharp/actions/runs/43171815825). The immutable tag resolves to `f786a55dc32d1767ac220ed1ad4c44748bccba97`. Publication documentation does not move that tag or replace its packages.

## Published packages and verification

- **29 NuGet packages:** 28 libraries and `PiSharp.Cli`, all version **1.5.2**. The publishing log records 29 accepted uploads and no duplicate skips.
- All 29 canonical NuGet version indexes expose 1.5.2. Every package was downloaded from the official flat-container feed and compared with its GitHub release asset. Every ZIP payload entry matched byte-for-byte, excluding NuGet's added repository signature. Each nuspec names version 1.5.2 and the exact tagged source commit. The signed NuGet containers retain their own hashes, separate from the unsigned GitHub archives.
- A fresh consumer outside the checkout restored exclusively from the official NuGet feed, with an isolated package cache and no project references. All PiSharp dependencies resolved as packages at exactly 1.5.2. The five-library consumer exercised Chord, native hosting, Durable.Store, Env.Protocol and Env, including a byte-schema host call and SQLite adapter. Compilation without experimental opt-in failed with the expected diagnostics; the opted-in build and execution passed.
- A private installation of `PiSharp.Cli` from NuGet returned **1.5.2** and passed its help smoke test. No global tool installation was changed.

The [machine-readable package and consumer verification](release-1.5.2-package-verification.json) records package hashes, source identities and consumer results. Full local receipts remain under `artifacts/release152/` in the publication worktree.

## Exact-tag qualification

All **36 active release jobs succeeded**; the two optional browser jobs were intentionally skipped by the tag workflow's `include_web: false` configuration. The required core, Durable, gap-regression and native-environment gates ran at the exact tagged source. The build job also ran all 22 serial offline release suites before packing and validating the 29 archives and the CLI's native SQLite payload.

The source implementation had already passed PR #60 and all eight post-merge workflows at `8223a2f77ce7cf80157c30614e7d52472603cf38`. Release preparation changed documentation, the release ledger and its source-lock regression, without changing runtime source. The tagged workflow independently qualified the release commit.

This release's 29 GitHub assets are the managed packages. Native daemon checks passed, but no 1.5.2 standalone daemon ZIPs are claimed by this package publication. Previous 1.5.0.1 native archives retain their original source and qualification. Experimental APIs, explicit skips, live-provider accounts, real SSH hosts, physical macOS input and other untested environments retain their documented boundaries. No complete upstream, all-provider or all-platform parity is claimed.

## Documentation and preserved history

The publication update records the actual tag, release run and publication time; promotes the dated changelog and RSS entry; and updates current package installation examples and the website's published version. Earlier release records, source locks, validation receipts and historical parity assessments retain their original identities and scope. The website deployment is checked after the publication documentation is merged.
