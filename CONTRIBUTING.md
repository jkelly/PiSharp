# Contributing to PiSharp

This checkout targets Pi and Pi Durable 1.7.0. PiSharp 1.6.0.2, the published package, tracks their 1.6.0 release. Use the source lock when checking a port and the public release record when documenting a NuGet install. [Source lock](compatibility/target.lock.json) · [Release record](compatibility/public-release.json) · [1.7.0 compatibility notes](docs/decisions/0018-pi-1.7.0-sync.md)

The contributor guides cover [building](https://pisharp.ai/docs/contribute/build/), [testing](https://pisharp.ai/docs/contribute/testing/) and [porting](https://pisharp.ai/docs/contribute/porting/).

1. Open an issue describing the proposed change and its rationale before substantial work.
2. Keep pull requests focused. Distinguish planned behavior from verified observations and implemented functionality.
3. Cite public upstream sources with immutable commit links when describing baseline behavior. Each ported C# file starts with a comment naming the upstream commit, license and source files; a file with no upstream counterpart starts with `// PiSharp-only (no upstream Pi source): ...`. Keep these headers accurate: the re-pin tool and the sync work lists read them.
4. Submit only material you have the right to share publicly. Exclude credentials, personal data, confidential material, and private repository content.
5. Preserve applicable copyright and license notices. Record any new third-party code or package in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), with its license text under `third-party/`. Contributions are provided under this repository's MIT license.

## Build and test

Build with the SDK pinned in `global.json` (10.0.401, no roll-forward). The committed `NuGet.Config` clears every package feed, so name nuget.org while your NuGet cache is empty:

```powershell
dotnet build src/PiSharp.Cli/PiSharp.Cli.csproj -c Release -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false
```

The suites under `tests/` are console programs. Each runs offline with no provider keys, prints a JSON report, and exits with 1 when a case fails. Run the suite for the area you change with `dotnet run --project tests/<suite> -c Release`.

The macOS matrix runs 19 release suites. The release workflow runs 23 suites and calls the Core, Durable, gap regression and native Env workflows at the release commit. When adding a suite, include it in the solution and the workflow that exercises its platform and prerequisites. [Testing guide](https://pisharp.ai/docs/contribute/testing/)

## Records to keep current

- `PUBLIC-DERIVATIVE-INTEGRITY.json` pins the current bytes of files that historical capture records hashed, including this file, `README.md`, `THIRD-PARTY-NOTICES.md` and `PiSharp.slnx`. After you change a pinned file, run `node tools/refresh-derivative-integrity.mjs --check`, then `node tools/refresh-derivative-integrity.mjs`, and commit the table and `tools/PublicDerivativeIntegrity.mjs` with the change. Save pinned files with LF line endings first (`git ls-files --eol <file>` shows `w/lf`). No workflow runs the check.
- Read the decision record in `docs/decisions/` before you change an area's scope.
- Keep [implementation status](IMPLEMENTATION_STATUS.md), the machine-readable task state and the website's parity data (`website/src/data/parity.json`) current when a port lands.

Authored fixture expectations, captured upstream results, developer checks and independent acceptance are distinct evidence kinds. Never regenerate goldens automatically or silently retarget the pinned upstream commit. Do not claim tests passed or parity was achieved without corresponding implementation and evidence.
