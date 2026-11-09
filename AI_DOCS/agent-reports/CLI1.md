# CLI1 release blocker corrections

Status: implementation complete; .NET validation is delegated to the root agent, which owns the build/test lease. No .NET execution, Git commands, remote writes or historical catalog changes were performed by CLI1. Requested worker: hard, sol/high, ultrafast; effective service tier is unverified.

## Evidence and dispositions

Original evidence is the actual `artifacts/release-prep/gap-42544805935/gap-core-{windows-latest,ubuntu-latest,macos-15}/core/*/PiSharp.CliSync.Tests.tests.stdout.log`, indexed by `gap-triage-summary.json` and CI1's report. Windows/Linux contain three failed CliSync cases, macOS five. These corrections do not imply that the candidate or any hosted rerun passed.

- `live.image-pi-budget-every-family`: `BuiltinModelCatalog` in `src/PiSharp.ModelRuntime/Models/BuiltinModelCatalog.cs` deliberately represents only the 42 verified pi-ai 1.5.0 package shards. `BuiltinProviders` additionally registers reviewed `qwen-token-plan-individual-cn`; `ReviewedModelCatalog.Apply(provider, baseline)` admits its ten reviewed rows without pretending there was a historical shard. The fixture now uses the same `Has ? Get : null` plus reviewed-overlay path as the production registry and Models live-route tests. A registered provider with neither baseline nor reviewed rows still fails explicitly. No pins, shard bytes, hashes, manifest rows or runtime catalog behavior were changed.
- `provider.anthropic.live-route-managed-effort-levels-markers-and-recorded-effort`: the exact beta list includes `message-threads-2026-08-12`. `AnthropicMessageThreadStore.Prepare` removes inherited tools/system and sends only the new message suffix after a known response. The managed case therefore checks create/continue plus the precise authored parent ID, the exact initial low marker and continuation max marker, adaptive thinking, fixed top-level low effort, absence of temperature, and recorded low/max response levels. Summary controls explicitly set `DisableContinuation` with a nonempty session ID and retain the legacy exact payload/fallback and managed/unmanaged thinking checks.
- `mcp.session.global-config-direct-background-failure-report-and-no-mcp`: the fake endpoint now issues distinct recorded response IDs. Effective declarations inherit only through a continuation referencing the preceding completed response; a full request resets declarations. Initial declarations must be present, both prompts must reach the provider, and direct MCP/codemode tools remain required throughout. Broken/disabled servers, diagnostics, connection counts, joined channel close, and `--no-mcp` exclusions remain checked. Additional controls reject unknown/no prior parents and prevent new threads from inheriting stale declarations.
- macOS prompt/long-session failures: `WithLiveRoot` and the separate MCP prompt fixture canonicalize an existing directory through the same `LocalFileOperations.CanonicalizeAsync` operation used by `OfflineSessionProfile`. Session headers, explicit workspace arguments and expected cwd now receive one consistent identity. This resolves actual `/var` ancestor symlinks without textual path replacements. The long-session fixture additionally opens its actual session against a different existing workspace and requires exit 2, `WorkspaceMismatch`, and zero provider requests before proceeding with the full successful open/prompt checks.
- Report parsing: both image route diagnostics (previously masked by the first image failure) and the long-session diagnostic move to stderr. `Program` continues emitting the complete JSON report and optional fresh report file; no case is filtered or dropped and no parser is loosened.

## Changed files

- `tests/PiSharp.CliSync.Tests/LiveImageCases.cs`
- `tests/PiSharp.CliSync.Tests/LiveRouteCases.cs`
- `tests/PiSharp.CliSync.Tests/LongSessionCases.cs`
- `tests/PiSharp.CliSync.Tests/McpPromptCases.cs`
- `tests/PiSharp.CliSync.Tests/McpSessionCases.cs`
- `tests/PiSharp.CliSync.Tests/MidEffortLiveCases.cs`
- `tests/PiSharp.Models.Tests/CatalogExpansionCases.cs`
- `tests/PiSharp.ModelRuntime.Tests/CatalogCases.cs` (late authorized extension)
- This report.

The focused Models regression retains the ten-row exact allowlist, row identity projection and China/international credential isolation, and adds explicit controls that the new provider is registered, has no historical shard, rejects frozen `Get` with `LiveCatalogUnavailable`, and admits its reviewed catalog. This preserves the intended frozen/live distinction.

## Validation handoff

Source review and raw hosted failure inspection completed. Root should run the full `PiSharp.CliSync.Tests` and `PiSharp.Models.Tests` executable suites under its existing lease. Particularly inspect the managed second request suffix, all six image families, both MCP discovery modes and `--no-mcp`, the long-session mismatch negative control plus every transcript marker, and clean strict JSON stdout with stderr diagnostics. macOS hosted rerun is required to prove the symlink correction there. No passing tests are claimed by this worker.

## Late Durable ModelRuntime case

The actual late Linux report `artifacts/release-prep/durable-late-42544805823/model-runtime-report-ubuntu-latest/model-runtime-ubuntu-latest.json` contains 14 cases and one failure: `catalog.every-embedded-shard-loads` rejects `qwen-token-plan-individual-cn` at `tests/PiSharp.ModelRuntime.Tests/CatalogCases.cs:32`. Root explicitly extended CLI1 ownership to that test after the read-only diagnosis.

The exact embedded resource/hash set comparison and each frozen shard's hash verification/loading remain intact. The final provider assertion now requires that the complete set of registered providers without shards exactly equals `ReviewedModelCatalog.AdditionalProviders`. Each such provider must have no invented historical resource, retain `LiveCatalogUnavailable` from frozen `Get`, and admit a nonempty reviewed catalog. The registry fixture resolves its fallback using the production reviewed-over-frozen path and explicitly requires every reviewed-only model to compose without a shard. Product catalogs remain unchanged. Root owns the requested full 14-case ModelRuntime run; CLI1 performed no .NET execution and made no CliSync/Models edits while root's compilation queue was active.
