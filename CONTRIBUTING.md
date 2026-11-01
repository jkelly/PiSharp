# Contributing to PiSharp

PiSharp is a .NET 10 port of a fixed Pi release, today Pi v1.2.0 (`compatibility/target.lock.json`). The contributor guides at [pisharp.ai/docs/contribute](https://pisharp.ai/docs/contribute/build/) cover building, [tests](https://pisharp.ai/docs/contribute/testing/), [how the port tracks Pi](https://pisharp.ai/docs/contribute/porting/), releasing and the website. Useful contributions include ported behavior with authored tests, source-linked compatibility fixtures and fixes to recorded differences from Pi.

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

CI runs the release suites other than the two durable ones on macOS (`macos.yml`), the durable suites (`durable.yml`) and the custom agents suite (`custom-agents.yml`) on Windows, Linux and macOS, and all 19 release suites before a release (`release.yml`). No workflow runs the core suites, such as `PiSharp.Agent.Tests`, `PiSharp.Sessions.Tests` and `PiSharp.Tools.Tests`, or the companion suites, so run the one for a library you change yourself. A new suite needs an entry in `PiSharp.slnx`, and to run in CI, in the list in `release.yml` and the matrix in `macos.yml`.

## Records to keep current

- `PUBLIC-DERIVATIVE-INTEGRITY.json` pins the current bytes of files that historical capture records hashed, including this file, `README.md`, `THIRD-PARTY-NOTICES.md` and `PiSharp.slnx`. After you change a pinned file, run `node tools/refresh-derivative-integrity.mjs --check`, then `node tools/refresh-derivative-integrity.mjs`, and commit the table and `tools/PublicDerivativeIntegrity.mjs` with the change. Save pinned files with LF line endings first (`git ls-files --eol <file>` shows `w/lf`). No workflow runs the check.
- Read the decision record in `docs/decisions/` before you change an area's scope.
- Keep [implementation status](IMPLEMENTATION_STATUS.md), the machine-readable task state and the website's parity data (`website/src/data/parity.json`) current when a port lands.

Authored fixture expectations, captured upstream results, developer checks and independent acceptance are distinct evidence kinds. Never regenerate goldens automatically or silently retarget the pinned upstream commit. Do not claim tests passed or parity was achieved without corresponding implementation and evidence.
