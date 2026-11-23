# PiSharp 1.6.0.1 publication

The project owner authorized package publication on 2026-11-23. [PiSharp 1.6.0.1](https://github.com/jkelly/PiSharp/releases/tag/v1.6.0.1) was published at **2026-11-23 15:22:25 UTC** by the successful [tagged release workflow 45198502548](https://github.com/jkelly/PiSharp/actions/runs/45198502548). The immutable annotated tag resolves to `1a74a207b30613444d5f8ef884957e7580860539`. Publication documentation does not move that tag or replace its packages.

## Published packages and verification

- **31 NuGet packages:** 29 libraries (adding `PiSharp.Extensions.AI`), `PiSharp.Cli`, and `PiSharp.Templates`, all version **1.6.0.1**. The publishing log records 31 accepted uploads and 0 duplicate skips.
- [Post-publish verification workflow 45207181360](https://github.com/jkelly/PiSharp/actions/runs/45207181360) verified all **31/31** canonical NuGet version indexes expose 1.6.0.1. Every package was downloaded from the official flat-container feed (`api.nuget.org`) and compared with its GitHub release asset. Every ZIP payload entry matched byte-for-byte, excluding NuGet's added repository signature. Each nuspec names version 1.6.0.1 and the exact tagged source commit (`1a74a207b30613444d5f8ef884957e7580860539`). The signed NuGet containers retain their own hashes, separate from the unsigned GitHub archives.
- The isolated official-feed consumer restored exclusively from `api.nuget.org` with an empty package cache and zero project references, resolving 13 PiSharp packages at exactly `1.6.0.1` and exercising Chord, native hosting, `Durable.Store`, `Env.Protocol`, and `Env`. Compilation without experimental opt-in failed with the expected Tier-2 diagnostics (`PISHARP_CHORD001`, `PISHARP_DURABLE001`, `PISHARP_ENV001`), while the opted-in build and execution passed.
- A private installation of `PiSharp.Cli` from NuGet returned **1.6.0.1** and passed its help smoke test. The compressed `PiSharp.Cli.1.6.0.1.nupkg` archive measures **61,636,654 bytes (~58.8 MiB)** under the enforced 80 MiB budget, with zero native `.pdb` debug symbols.
- Additional isolated official-feed smoke tests installed `PiSharp.Templates@1.6.0.1` into a clean custom hive, scaffolded and ran `pisharp-agent` against NuGet.org packages, and restored, compiled (with `TreatWarningsAsErrors=true` and zero `<NoWarn>`), and executed `PiSharp.Extensions.AI` `1.6.0.1` side-by-side with `Microsoft.Extensions.AI` `10.11.1`.

The [machine-readable package and consumer verification](release-1.6.0.1-package-verification.json) records package hashes, source identities, and consumer results.

## Exact-tag qualification

All **38 active release jobs succeeded** in run `45198502548`; the optional browser jobs (`include_web: false`) and manual-only standalone job were intentionally skipped by the tag workflow configuration. The required `core-validation`, `durable-validation`, `gap-validation`, and `native-env-validation` gates ran at the exact tagged commit `1a74a207b30613444d5f8ef884957e7580860539`. The build job also ran all 23 serial offline release suites, packed and validated all 31 NuGet archives, smoke-tested all three `PiSharp.Templates` templates, and verified the CLI's native SQLite payload and 80 MiB size budget.

Prior to tagging, PRs #68–#73 (Waves 1–6), release preparation PR #74 (67/67 CI checks, clean Codex and CodeRabbit reviews), and manual `release.yml` dry run `45187372513` (37/37 active jobs including self-contained standalone archives for `win-x64`, `linux-x64`, `osx-arm64` and Homebrew/Scoop/Winget manifests) all passed.

This release's 31 GitHub assets are the managed NuGet packages. Native daemon checks passed, but no 1.6.0.1 standalone daemon ZIPs are claimed by this package publication; earlier 1.5.0.1 native daemon archives retain their original source and qualification. Experimental APIs, explicit skips, live-provider accounts, real SSH hosts, physical macOS input, Windows PTY creation (`ENOTSUP`), and other untested environments retain their documented boundaries. No complete upstream, all-provider, or all-platform parity is claimed.

## Documentation and preserved history

This publication update records the actual tag, release run, verification run, and publication timestamp; promotes the dated changelog and RSS entry; marks `PiSharp.Extensions.AI` and `PiSharp.Templates` as published on `/sdk/`; and advances `pisharp.ai`'s published version to `1.6.0.1` while keeping PiSharp `1.6.0`, `1.5.2`, `1.5.0.1`, and earlier release records, source locks, and historical receipts intact.
