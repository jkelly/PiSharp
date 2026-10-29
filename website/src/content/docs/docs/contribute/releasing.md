---
title: Releasing
description: How a PiSharp release is versioned, prepared, built and published.
---

A PiSharp release is a Git tag. Pushing a `v*` tag runs [`release.yml`](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/release.yml), which tests, packs and publishes everything. Most of the work is the preparation before the tag: the version, the notes, and the records the website and the release scripts read. This page describes the process as the repository's files and past release commits show it.

:::note
No tag, release or package is published without the project owner's explicit go-ahead. [Decision 0006](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0006-pi-1.2.0-sync.md) states the rule.
:::

## Versioning

PiSharp's version is the Pi version it matches, and a fourth number marks a C#-only patch on the same baseline: 1.2.0 is the Pi v1.2.0 sync, and 1.2.0.1 adds C# work on top of it. See [Versioning](/docs/versioning/).

The release scripts enforce this:

- The version must match `^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.-]+)?$`.
- Its first three numbers must equal the tag in `compatibility/target.lock.json` (`v1.2.0` today). A sync to a new Pi release therefore comes before its release. See [How the port tracks Pi](/docs/contribute/porting/).
- A version with a `-` suffix becomes a GitHub prerelease.

Every assembly gets the release version, except `PiSharp.ExtensionHost`, which keeps assembly version `1.0.0.0`.

## Prepare the release

Releases are prepared on a `release/<version>` branch, such as `release/1.2.0.1`, and merged into `main` with a pull request. The preparation commit touches these files:

| File | Change |
| --- | --- |
| [`Directory.Build.props`](https://github.com/jkelly/PiSharp/blob/main/Directory.Build.props) | Set `VersionPrefix` to the new version. |
| `docs/release-notes/<version>.md` | The full release notes. The GitHub release uses this file as its body. |
| `website/src/content/changelog/<version>.md` | The short changelog entry for the [changelog page](/changelog/). See [Changelog entries](/docs/contribute/website/#changelog-entries). |
| [`CHANGELOG.md`](https://github.com/jkelly/PiSharp/blob/main/CHANGELOG.md) | Regenerate it from the website entries. See [below](#regenerate-changelogmd). |
| [`compatibility/public-release.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/public-release.json) | Set `version`, `proposedReleaseTag` and `releaseCandidate` (branch, decision, experimental parts, notes and changelog paths), reset `publication` to false, and move the previous release into `previousReleases`. |
| [`docs/plans/full-parity-progress.json`](https://github.com/jkelly/PiSharp/blob/main/docs/plans/full-parity-progress.json) | Mark the previous release `published` with its tag and date, and add an entry for the new one with status `release-candidate`. |
| `README.md`, `THIRD-PARTY-NOTICES.md` | Update the version and any new third-party notices. |
| Decision records and `compatibility/*` | Record what ships, for example the decision's status and the sync rows that name the release. |
| `website/src/data/packages.ts`, `website/src/data/parity.json`, `website/src/pages/*.astro` | Update the package list, the parity page and the pages that name the version. |
| Docs pages with version pins | Update every `PackageReference` and version mention. Find them with `git grep -n "<previous version>" -- website/src`. |

A separate commit then re-seals the derivative integrity table, because `Directory.Build.props`, `README.md`, `THIRD-PARTY-NOTICES.md` and other files above are pinned there. See [Derivative integrity pins](/docs/contribute/porting/#derivative-integrity-pins).

### Regenerate CHANGELOG.md

`CHANGELOG.md` ships next to the CLI, where What's New and `/changelog` read it. It is generated from `website/src/content/changelog/*.md`, newest version first, as `## [version] - date` sections in Pi's format, with site-relative links made absolute. The `InteractiveParity` case `changelog.sync.generated-from-website` fails when the file is out of date. With `PISHARP_WRITE_CHANGELOG=1`, it rewrites the file instead:

```powershell
$env:PISHARP_WRITE_CHANGELOG = '1'
$env:INTERACTIVEPARITY_FILTER = 'changelog.'
dotnet run --project tests/PiSharp.InteractiveParity.Tests -c Release
Remove-Item Env:PISHARP_WRITE_CHANGELOG, Env:INTERACTIVEPARITY_FILTER
```

The file is written as UTF-8 without a byte order mark, with LF line endings.

### When you add a package

The package list is written out in several places. A new library needs all of them:

| File | Change |
| --- | --- |
| [`tools/release/build-release-packages.ps1`](https://github.com/jkelly/PiSharp/blob/main/tools/release/build-release-packages.ps1) | Add it to `$libraries`, in dependency order. The script packs exactly this list plus the CLI. |
| [`tools/release/PiSharp.NativeSdk.targets`](https://github.com/jkelly/PiSharp/blob/main/tools/release/PiSharp.NativeSdk.targets) | Add it to the `PiSharpNativeSdkPackage` condition, and give it a `Description`. |
| [`tools/release/native-sdk-package.ps1`](https://github.com/jkelly/PiSharp/blob/main/tools/release/native-sdk-package.ps1) | Add it to the allowed package ids, its PiSharp dependencies to `$siblings`, its third-party pins to `$external`, and any license files it must carry. |
| [`website/src/data/packages.ts`](https://github.com/jkelly/PiSharp/blob/main/website/src/data/packages.ts) | List it on the [SDK page](/sdk/). |
| `release.yml` | If the CLI tool carries the new assemblies, add them to the tool payload check. |

`native-sdk-package.ps1` also expects the provenance SDK version `10.0.401`. Update it with `global.json` when the SDK changes.

## Dry run

Run the release workflow by hand from the Actions tab to build and check a version without publishing anything. It takes two inputs:

| Input | Default | Effect |
| --- | --- | --- |
| `version` | `1.2.0.1` | The version to build and validate. |
| `skip_tests` | `false` | Skip the offline test suites. Only a dry run can skip them. |

A dry run runs only the build job. The publish and GitHub release jobs run only for a pushed tag.

You can also build the packages locally. See [Install your build as a tool](/docs/contribute/build/#install-your-build-as-a-tool).

## Tag and publish

Once the release branch is merged, tag the merge commit and push the tag:

```sh
git tag v1.2.0.1 <merge commit>
git push origin v1.2.0.1
```

## The release workflow

`release.yml` has three jobs.

### Build

The build job runs on Ubuntu with the SDK from `global.json` and Node.js 22. It takes the version from the tag, without the `v`, and then:

1. Runs the 19 offline release suites with `dotnet run`: the 17 suites the macOS workflow runs, plus `Durable` and `DurableCli`. A tag push always runs them. See [Tests](/docs/contribute/testing/#release-suites).
2. Runs `build-release-packages.ps1` with the version and the commit. See [Build the packages](#build-the-packages).
3. Checks the CLI tool's durable payload: the durable and SQLite assemblies and their license files must be in the package, and the `e_sqlite3` native library for `win-x64`, `linux-x64`, `linux-musl-x64` and `osx-arm64` must match the SQLitePCLRaw package byte for byte.
4. Uploads the packages and the validation reports as the `packages` artifact.

### Publish

The publish job runs in the `nuget` environment and uses NuGet trusted publishing. `NuGet/login` exchanges the run's OIDC token for a short-lived API key for the account in the `NUGET_USER` repository variable, so no long-lived key is stored. It then pushes every package:

```sh
dotnet nuget push "artifacts/packages/*.nupkg" --api-key <short-lived key> --source https://api.nuget.org/v3/index.json --skip-duplicate
```

`--skip-duplicate` lets a rerun skip the packages that already went up.

### GitHub release

After the publish job succeeds, the release job creates the GitHub release for the tag, titled `PiSharp <version>`, with `--verify-tag`. Its body is `docs/release-notes/<version>.md`, or generated notes if that file doesn't exist. The packages are attached.

## Build the packages

[`tools/release/build-release-packages.ps1`](https://github.com/jkelly/PiSharp/blob/main/tools/release/build-release-packages.ps1) needs PowerShell 7.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `-Version` | (required) | The release version. |
| `-SourceCommit` | (required) | The full 40-character commit the packages come from. |
| `-OutputDirectory` | `artifacts/packages` | Where the packages go. |
| `-PackageSource` | `https://api.nuget.org/v3/index.json` | The feed restore uses, since `NuGet.Config` clears every feed. |

It writes provenance files to `artifacts/release-inputs/`, recording the commit, version, Pi baseline and SDK. Then it packs:

- The 19 libraries, with `tools/release/PiSharp.NativeSdk.targets`. The targets file sets the package id, version, description, license, readme and notices, embeds the provenance, and fails without a version, a commit, the provenance file or `net10.0`. Each package is checked by `Assert-PiSharpNativeSdkPackage`: its members, its dependencies (PiSharp siblings at the release version, third-party packages at their pinned versions) and its assembly.
- The CLI, as the `PiSharp.Cli` tool with the command `pisharp`, with `tools/packaging/PiSharp.Distribution.targets`, checked by `tools/packaging/validate-distribution.ps1`.

The reports go to `artifacts/validation/`. The script fails unless it ends with exactly 20 packages.

## After the release

Record the publication in `compatibility/public-release.json`: set `tagCreated`, `binaryReleasePublished` and `nugetPackagesPublished` to true, and add `publishedOn`, `tagCommit` and `releaseRun`. The website reads this file to show the current release. Until `nugetPackagesPublished` is true, it shows the last release in `previousReleases` that has it.

## Legacy release documents

[`docs/release/`](https://github.com/jkelly/PiSharp/tree/main/docs/release) holds design records from before the current pipeline. No workflow uses the scripts they describe; `tests/release/` still tests those scripts.

| Document | Describes |
| --- | --- |
| `native-sdk-private-packages.md` | The first proposal for private native SDK packages. |
| `r296-artifact-metadata-leaf.md` | `ReleaseArtifactManifest.ps1`, a manifest validator for four payload profiles. |
| `r297-artifact-byte-admission.md` | `ReleaseArtifactFiles.ps1`, hash and length checks of staged payload files. |
| `standalone-release.md` | `standalone-release.ps1`, helpers for self-contained archives for `win-x64`, `linux-x64` and `osx-arm64`. |
| `windows-native-tool.md` | A framework-dependent Windows tool profile. |
