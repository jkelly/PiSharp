# CI2 runner and prerequisite repairs

Status: ready for main integration and hosted verification. No .NET builds/test execution, workspace Git mutations, pushes or other remote writes performed. Source ownership stayed inside `tools/ci/**` and the assigned core/gap/release/macOS workflows.

## Changes

- The gap JSON parser accepts the exact existing `PASS_UPSTREAM_CAPTURE` status and counts it as passed while retaining the raw status/evidence qualification. Arbitrary unknown statuses still fail. Summary-only reports still cannot invent cases, and all incomplete/skip controls remain strict.
- Env.Adapter is registered as named lines: exactly 54 Windows cases and 60 Linux/macOS cases from the uploaded actual run. Both conformance modes and final seeded random/Durable-tool traces are required. Windows deliberately excludes six symlink identities; it does not receive fabricated skips/passes for them. Unknown, omitted, duplicate or unexpected skip observations still fail.
- Gap runner resolves the configured SDK executable to an absolute path, records it as `dotnetHost` and passes that same `PISHARP_TEST_DOTNET` through explicit child environments to builds, apphost test processes and their descendants. `run-gap-suites.ps1 -Dotnet` and the environment setting select the host. Core similarly respects/configures the absolute selected host. Release/macOS shell test invocations export their selected host.
- Added `ensure-historical-ai-archive.mjs`: only the official registry `@earendil-works/pi-ai@0.99.1` archive is fetched. It requires 760966 bytes and SHA256 `f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3`; redirects, wrong HTTP status/length/hash, corrupt existing cache and nonregular cache targets fail. Opaque bytes are staged privately, flushed, then atomically renamed. The helper never extracts or executes archive content. Correct existing bytes are verified and reused without a request.
- Core Agent/Compatibility provision that historical archive before build/run. Release and macOS offline suites provision it before testing. Core explicitly installs fd with ripgrep; its Tools stage prints each resolved executable path and version before executing the suite, addressing missing prerequisite evidence without guessing a pin.
- Added offline archive/parser/SDK controls and wired archive controls into Core applicability and the gap offline runner check.

## Verification

- `python -m unittest discover -s tools/ci -p test_gap_runner.py -v`: 32 tests passed. The final suite includes selected SDK propagation through every build/test and an actual child descendant. A prior run exposed an existing 300ms fixture-startup race; the hang test now waits up to 2 seconds for the startup marker before measuring the same 300ms hang deadline. Output-preservation and total bounded-duration assertions remain.
- `node --test tools/ci/historical-ai-archive.test.mjs tools/ci/validation-manifest.test.mjs`: 7 passed. Controls cover fixed official pin, atomic publication/reuse, HTTP/size/hash rejection, cleanup and refusal to overwrite corrupt cached bytes.
- Invoked the real helper once with approved network access: downloaded official 760966-byte archive, exact SHA256 verified, no extraction/execution. Artifact is available locally at `artifacts/released-npm-ai/pi-ai-0.99.1.tgz` for main's .NET tests.
- Reparsed actual run 42544805935 stdout using the repaired strict policies: Env.Adapter Windows 54/Linux 60/macOS 60 passed; Chord Windows/Linux 875 passed; macOS 872 passed and its 3 real pipe failures remain failed. This is artifact replay, not fresh runtime execution.
- PowerShell AST parsing passed for both changed runner scripts. All four changed workflow YAML files parse. Scoped `git diff --check` passed (line-ending normalization notice only).

## Remaining integration gates

Main owns .NET and exact-candidate hosted reruns. HOST1/CLI1/EXT1/FILE1/MAC1 own their respective source/fixture failures. CI2 does not call the matrix or release ready from corrected parser results alone.

Late Linux upload is now available in `artifacts/release-prep/durable-late-42544805823/`: ModelRuntime fails `catalog.every-embedded-shard-loads` on the same reviewed-only qwen provider; WebAgent additionally fails `runs.engine.codex-takes-its-service-tier-and-the-patched-options` because its strict `get_weather` tool is absent from the Codex request seen by RunCaptureHandler. Both were sent to main with exact source/report paths; see updated CI1. Durable normal/retention0 each have only the known M2 failure plus two explicit skips. Windows/macOS were still running. These source/fixture issues remain outside CI2's implementation scope.

Files: `.github/workflows/{core,gap-regressions,release,macos}.yml`; `tools/ci/{run_gap_suites.py,owned_process.py,run-gap-suites.ps1,run-core-suite.ps1,gap-suites.json,test_gap_runner.py,ensure-historical-ai-archive.mjs,historical-ai-archive.test.mjs}`; this report.
