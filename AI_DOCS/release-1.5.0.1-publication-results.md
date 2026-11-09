# PiSharp 1.5.0.1 publication

The user authorized publication. [PiSharp 1.5.0.1](https://github.com/jkelly/PiSharp/releases/tag/v1.5.0.1) was published on **2026-11-09 at 04:11:05 UTC** by the successful [tagged release workflow 42558097795](https://github.com/jkelly/PiSharp/actions/runs/42558097795). Its immutable tag resolves to `8144e3eaa138a5faf56f5cbb0de477a56fd48f8f`. Subsequent publication documentation and helper corrections do not move that tag or change its packages.

## Published products

- **29 NuGet packages:** 28 libraries and `PiSharp.Cli`, all version **1.5.0.1**. The publish job recorded 29 accepted uploads and no duplicate skips. All 29 packages, including the CLI, were downloaded successfully from the official NuGet feed. Every ZIP payload entry matched its GitHub release archive byte-for-byte, excluding NuGet's added repository signature. Each nuspec identifies version 1.5.0.1 and the exact tagged source commit. NuGet's signed archive container hashes differ from the unsigned GitHub archives as expected; each set retains its own recorded digests. All 29 canonical NuGet version indexes also expose 1.5.0.1 for normal restore. The CLI's processing delay resolved during verification.
- **Three native daemon archives:** `linux-x64`, `osx-arm64` and `win-x64`, with their original notice files and provenance. These binaries came from the exact-source native gate; Windows PTY remains unsupported.
- **34 GitHub release assets:** the 29 managed archives, three native archives, `candidate-manifest.json` and `SHA256SUMS`. The manifest retains its preparation filename while explicitly recording the resulting published state. Checksums use the actual released archives, including the workflow-built managed packages.

The five added experimental libraries are `PiSharp.Chord`, `PiSharp.Chord.Hosting`, `PiSharp.Durable.Store`, `PiSharp.Env.Protocol` and `PiSharp.Env`. Opt-in diagnostics, exact sibling dependencies and the hosting framework requirement remain documented. The upstream baseline remains Pi v1.5.0, `f9a26365568127461c47a07a37dcb42c140a3ac5`; reviewed changes through `7d073328e44337d3292a7db9b0f0308f679c5e97` do not constitute a new baseline or a claim of complete parity.

## Source qualification

These independent preparation runs qualified the exact tagged revision:

| Gate | Result | Evidence |
| --- | --- | --- |
| Core | All 11 jobs succeeded | [42554073528](https://github.com/jkelly/PiSharp/actions/runs/42554073528) |
| Durable and Web | All 11 jobs succeeded; WebAgent 543 cases passed on each of Linux, macOS and Windows | [42554073656](https://github.com/jkelly/PiSharp/actions/runs/42554073656) |
| Gap implementation | All 10 jobs succeeded | [42554073590](https://github.com/jkelly/PiSharp/actions/runs/42554073590) |
| Native daemon | All three platform jobs succeeded; exact 1.5.0.1 handshake; 14 Unix PTY groups on Linux and macOS | [42554073540](https://github.com/jkelly/PiSharp/actions/runs/42554073540) |
| Additional macOS checks | All 20 jobs succeeded | [42554073603](https://github.com/jkelly/PiSharp/actions/runs/42554073603) |

The tagged release workflow repeated the four required gate groups and all 22 serial offline release suites, then passed pack, archive and SQLite checks before publishing. Local preparation separately verified all 29 normal Release archives and a fresh external consumer of the five new libraries, with unchanged source fingerprints during that gate. These are distinct from earlier implementation receipts and the original 1.5.0 artifacts.

Native manifests retain `repositoryDirty=true`. Hosted evidence records initial `trackedChanges=[]` at the exact tagged SHA. The manifest's dirty-flag cause was not independently audited by the byte verifier; the artifacts are not relabeled as clean builds. Explicit skips remain visible. Live-provider accounts, real SSH hosts, physical Apple Terminal input, Android and other RIDs retain separate qualification boundaries. Cloudflare/browser-only adapters remain exclusions. No complete upstream, all-provider or all-platform parity is claimed.

## Publication recovery and future helper correction

GitHub's authenticated by-tag endpoint returned 404 for the prepared draft while authenticated release listing exposed it. The original helper's offline fixture had modeled different behavior. Before the workflow created the public release, the prepared draft was preserved under `preparation-archive-1.5.0.1-8144`; its body and asset metadata were backed up locally. The running immutable workflow then created the public release successfully. The three native archives and refreshed integrity sidecars were transferred to the public release, and the released notes replaced the frozen candidate wording. All 34 public asset sizes and SHA-256 digests were verified; the three native archives and two integrity sidecars were also downloaded publicly and compared byte-for-byte before removing only the temporary archived draft (ID 438703276). The original body and asset metadata backup remains in the local publication receipts.

The helper on main now uses bounded authenticated release pagination, refuses ambiguous or inconsistent identities, and rechecks the exact draft by release ID before promotion. **22 offline controls passed**, including the observed 404/listed-draft behavior. This correction applies to subsequent runs; no release tag was moved and no published NuGet version was replaced.

## Website and durable evidence

Published-state source updates include the website, release notes, generated changelog, README and `compatibility/public-release.json`. The complete previous-release records, upstream pin and historical execution evidence remain intact. The final website build passed with 60 pages and 4,383 internal links/fragments, zero failures, all 29 exact-version SDK package URLs and installation commands, the 1.5.0.1 / Pi 1.5.0 header and one correctly dated RSS entry. The generated changelog matches all 13 entries byte-for-byte. [Website workflow 42561854694](https://github.com/jkelly/PiSharp/actions/runs/42561854694) built and deployed publication-source commit `79fd5bb1aa641cfdb46be28fd9e387c8336e6ba6` successfully. Live HTTP checks passed for the homepage, SDK, updates guide, changelog and RSS: all returned 200, with the correct 1.5.0.1 banner, exact 29 SDK links and one correctly dated release entry. The deployment receipt is retained with the publication artifacts.

Local publication receipts are retained under `artifacts/release-publication/1.5.0.1/`: the accepted-upload log, public release API response, official NuGet download receipts, package payload comparison, prepared-draft backup, released package archives, published release body and integrity sidecars. Preparation receipts remain under `artifacts/release-prep/`.
