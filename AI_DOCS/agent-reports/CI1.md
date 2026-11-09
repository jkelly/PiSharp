# CI1 release preparation triage

Status: discovery complete for all nine gap jobs and Core validation; Durable remains in progress, with confirmed failures below. No product/test/CI source edits, builds, test execution, Git mutations or remote writes performed. All evidence is for `3024b1e09e1add83e8cfeeb73bc4235a026b517c`, not the unpublished 1.5.0.1 candidate.

Downloaded actual Actions artifacts using `gh run download` into:

- `artifacts/release-prep/gap-42544805935/`
- `artifacts/release-prep/core-42544805912/`
- `artifacts/release-prep/durable-42544805823/` (only completed lanes uploaded so far)

Machine-readable extracted matrix evidence, including every failing case, raw failure and failed stage: `artifacts/release-prep/gap-triage-summary.json`. Each gap artifact contains the original `GROUP/manifest.json` plus a run-ID directory with byte-hashed stdout/stderr for every build/test stage. Reports distinguish observed cases from summary-only suites and preserve policy skips.

## Gap matrix, run 42544805935

| Job ID | Matrix job | Actual failures | Classification / minimal correction |
|---|---|---|---|
| 128927362579 | core / Linux | CliSync 3 failures; leading diagnostic line also makes JSON invalid | CliSync fixture expectations/catalog enumeration, plus mixed stdout format |
| 128927362440 | core / Windows | CliSync 3 failures; leading diagnostic line also makes JSON invalid | Same as Linux |
| 128927362513 | core / macOS | CliSync 5 failures | Same 3 plus canonical temp-path fixture mismatch |
| 128927362479 | chord / Linux | Chord JSON rejects 12 `PASS_UPSTREAM_CAPTURE` statuses; Hosting 9 failed cases | Runner policy; missing dotnet fixture prerequisite (7); real Unix named-mutex WaitAny portability (1); loopback HTTP 403 (1) |
| 128927362615 | chord / Windows | Chord JSON rejects 12 captured-pass statuses; Hosting 7 failed cases | Runner policy; missing dotnet fixture prerequisite |
| 128927362571 | chord / macOS | Same 12 rejected pass statuses; 3 Chord pipe failures; Hosting 9; Legacy.Cluster 7 | Above plus named-pipe Unix socket paths exceed macOS 104-character bound |
| 128927362687 | env / Linux | Adapter emits 60 PASS observations but policy demands summary-only | Registration/parser mismatch; all actual cases passed |
| 128927362583 | env / Windows | Adapter emits 54 PASS observations but policy demands summary-only | Registration/parser mismatch; Unix FIFO is an explicit allowed skip; terminal ENOTSUP asserted |
| 128927362441 | env / macOS | Adapter same 60-case policy mismatch; Daemon exits 134 before first case; Terminal exits 134 after 8 cases | Daemon pipe-path fixture; native slow-consumer PTY failure owned by MAC1 |

Raw artifact folders are `gap-{core,chord,env}-{ubuntu-latest,windows-latest,macos-15}`. Under each folder the group directory's manifest is the complete case/stage index. All suites built successfully; none of the nine is a compile failure. Required production runtime installer job `128927362112` passed with uploaded provenance.

### Runner defects

1. `tools/ci/run_gap_suites.py:143` accepts `PASS`, `PASS_AUTHORED_NATIVE_ONLY`, `PASS_AUTHORED_NATIVE` but omits `PASS_UPSTREAM_CAPTURE` in JSON; the line parser already normalizes it. The passed-count expression at line155 has the same omission. Exactly **12**, not 13, Chord schema corpus cases carry that status. Preserve the original evidence qualification while counting these as successful captured cases. Windows/Linux Chord.Tests process exits 0 with all actual cases passing; macOS additionally has genuine pipe setup failures. Do not broadly accept arbitrary `PASS_*` statuses.
2. `tools/ci/gap-suites.json:368` declares Env.Adapter summary-only even though Program now prints named real-daemon cases after its original summary. Register observed lines and explicit platform case identities/completion evidence. Do not discard those cases or fabricate assertion counts from the summary.
3. CliSync Linux/Windows stdout starts `  long session: 10701 records, 1741 request messages, body 152447 bytes` before its JSON object. The strict JSON parser rejects it, then salvage correctly preserves failing cases. Move that diagnostic to stderr or write/read a dedicated report file; do not loosen completeness validation by blindly searching for a JSON suffix.

### Chord.Hosting prerequisite and runtime defects

Seven failed case IDs on every OS, all `InvalidOperationException: Run with the pinned dotnet host or set PISHARP_TEST_DOTNET`, originate in `tests/PiSharp.Chord.Hosting.Tests/Program.cs:77`:

- `plugins.rollout-continues-failures-and-stops-explicitly`
- `plugins.new-failed-plugin-retains-dirty-mark`
- `plugins.builder-natural-exit-joins-descendant-pipes`
- `plugins.build-load-reload-failure-and-collectible-context`
- `workers.real-process-route-stop-restart`
- `workers.startup-shutdown-closes-connected-host`
- `processes.natural-and-crashed-leader-kill-descendants`

`dotnet run` launches the test apphost, so `Environment.ProcessPath` is not dotnet. Resolve the configured dotnet executable and propagate `PISHARP_TEST_DOTNET` to tests and descendants, or execute the built DLL using that host. This is missing fixture setup, not evidence the plugins themselves fail.

Unix-only case `workers.address-lock-waits-and-cancels` throws `PlatformNotSupportedException: Wait operations on multiple wait handles including a named synchronization primitive are not supported on this platform.` Runtime source: `src/PiSharp.Chord.Hosting/Workers/ProcessAddressLock.cs:25`, `WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle])`. Replace with a cancellable bounded wait on the mutex on the same dedicated ownership thread; preserve abandoned-lock acquisition, cancellation and joining before disposal.

Unix-only `http.real-loopback-listener-and-websocket` receives 403 at `tests/PiSharp.Chord.Hosting.Tests/Program.cs:423`. `ChordHttp.AdmitAsync`, `src/PiSharp.Chord.Hosting/Http/ChordHttp.cs:205`, uses `Uri.TryCreate(rawTarget, UriKind.Absolute, ...)` as a proxy-form guard; `/` may be recognized as an absolute file URI on Unix. This is a concrete source hypothesis, requiring focused verification; do not assume proxy environment or weaken host/origin checks globally. Require explicit HTTP(S) absolute request-target syntax when rejecting proxy-form requests.

### macOS socket paths

Raw failures explicitly show `/var/folders/2s/.../T/CoreFxPipe_pisharp-...` exceeding the 104-character Unix-domain socket path limit. Confirmed paths/cases:

- `tests/PiSharp.Chord.Tests/Legacy/SessionCases.cs:93,104`: `legacy.link.native-pipe-transfer`, `legacy.link.native-websocket-transfer`.
- `tests/PiSharp.Chord.Tests/Link/LinkCases.cs:192`: `link.transports.framed-pipe-duplex`.
- `src/PiSharp.Chord/Legacy/Manager/LegacyProcessDriver.cs:32`: seven Legacy.Cluster cases: `manager.admission-cleans-natural-exit-before-ready`, `manager.concurrent-start-stop-replace-real-child`, `manager.kernel-lock-and-replace`, `manager.does-not-inherit-parent-lock`, `manager.fatal-and-readiness-timeout-cleanup`, `manager.busy-to-idle-clock-and-policy`, `manager.grace-kills-descendants`.
- `tests/PiSharp.Env.Daemon.Tests/Program.cs:264` (`Wire.Open`): Daemon fails before its first `FileOperations` case.
- `src/PiSharp.Chord.Hosting/Workers/ProcessWorkers.cs:140` generates similarly long data/control pipe names; currently masked by the missing dotnet prerequisite and Unix lock error.

Use a short unique temp directory for hosted macOS before child processes start, and/or shorten generated opaque pipe names sufficiently for actual user TMPDIR paths. A CI-only TMPDIR workaround does not prove production worker portability with long TMPDIR. Env Terminal slow-consumer is separately owned by MAC1; preserve that failure.

### CliSync exact failures

All OS share:

| Case ID | Error | Source / correction direction |
|---|---|---|
| `live.image-pi-budget-every-family` | `BuiltinCatalogException: The pinned live model catalog is unavailable.` | `LiveImageCases.cs:28` iterates every BuiltinProviders entry and calls `BuiltinModelCatalog.Get`; `BuiltinModelCatalog.cs:75` throws when provider absent from pin map. `qwen-token-plan-individual-cn` exists in provider definitions but not the pin map. Check `Has` before requiring a shard, or reconcile the intended catalog/provider contract; do not silently invent a shard. |
| `provider.anthropic.live-route-managed-effort-levels-markers-and-recorded-effort` | Expected beta list without `message-threads-2026-08-12`, actual includes it | `MidEffortLiveCases.cs:64`; update exact expected beta set for newly implemented thread behavior while retaining effort assertions. |
| `mcp.session.global-config-direct-background-failure-report-and-no-mcp` | `direct MCP tool declared:` with empty tool list | `McpSessionCases.cs:103` asserts tools in every request's top-level list. Anthropic continuation can omit unchanged tools in subsequent requests; inspect first full request plus reconstructed continuation state. Root cause still requires focused verification; do not assume MCP failed to register from empty continuation payload alone. |

macOS additionally:

- `prompt.mcp-servers-section-first-prompt-append-and-removal`: expected `<cwd>\n/var/folders/...`, actual uses `/private/var/folders/...`.
- `live.long-session-2000-messages-10000-records-opens-and-sends-whole-transcript`: RPC exit2 `WorkspaceMismatch: Session working directory does not match the explicit workspace.`

Canonicalize fixture working directory and session metadata consistently before launch, preserving actual different-workspace rejection. Detailed source stacks and expected/actual values are in the macOS CliSync test stdout and `gap-triage-summary.json`.

## Core validation, run 42544805912

| Job ID | Suite | Confirmed failures / impact |
|---|---|---|
| 128927362760 | Compatibility | `artifact.catalog.released-npm-42-provider-shards`: missing `artifacts/released-npm-ai/pi-ai-0.99.1.tgz` |
| 128927362925 | Agent | `pi-messages-agent.released-radius-all-28-row-routing`: same missing tgz prerequisite |
| 128927363032 | Tools | `gap files rg context globs UTF16 and normalized limits`: `Exact find limit reported truncation.` at `GapFileToolsTests.cs:44` |
| 128927363128 | Extensions.ContractTests | Ten failed cases listed below |

Compatibility fixture reads the byte-pinned released npm package in `FrozenModelCatalogTests.cs`; Agent reads it in `PiMessagesAgentIntegrationTests.cs:230`. `tools/ci/run-core-suite.ps1` provisions native extension builds but never provisions this historical tarball. Fetch the exact expected artifact with integrity verification (reusing existing artifact-fetch tooling if present) before these suites; do not remove source/capture verification or call absence a pass.

Tools source: `src/PiSharp.Tools/Files/PiFindTool.cs` already fetches `limit+1` to prove excess. Inspect which fd executable the Windows host resolves; `core.yml` installs only ripgrep while this test requires fd. Existing runner-image fd may have different behavior. Failure needs focused reproduction before changing source or the exact-limit assertion.

Extensions failures (raw console contains full stacks; JSON report contains individual results):

| Case | Error / first source |
|---|---|
| `invocation progress rejects malformed data, configured bounds, overlapping delivery and exhausted source quotas` | Expected ExtensionRegistrationException; `ExtensionToolInvocationTests.cs:208` |
| `published initialization failure/cancellation and loader close join rollback` | Expected InitializationFailed; actual LoadFailed; `PublishedLoaderTests.cs:285` |
| `registered hooks cannot bypass full schema, final policy or first block` | Registered tool assertion failed; `RegisteredExtensionToolTests.cs` |
| `native tool activation durable ordered activation reaches real requests and survives reopen` | Tool order/set differs; `ToolActivationTests.cs` |
| `native tool activation cancelled activation has no durable or live publication` | Expected OperationCanceledException; `ToolActivationTests.cs` |
| `native tool activation navigation validates epoch restores pending state and returns usable post-epoch revision` | Tool order/set differs; `ToolActivationTests.cs` |
| `session-snapshot. identities JSON and inclusive view budgets reject without truncation` | Expected ExtensionRegistrationException; `ExtensionSessionSnapshotTests.cs:403` |
| `session-creation-preflight.same-eventual-count-size-depth-identity-number-policy` | Invalid staged snapshot admitted: count; `SessionCreationPreflightRegistryTests.cs:37,92` |
| `session-creation-preflight.postcommit-context-error-has-receipt-and-joined-cleanup` | Postcommit invalid context was admitted; `SessionCreationPreflightRegistryTests.cs:37,92` |
| `mcp-resource-runtime-bridge.actual-list-template-read-bridge-and-native-identity` | MCP callback aggregate/control failed; `McpResourceRuntimeBridgeTests.cs:110` |

These are unresolved behavior/fixture contract mismatches, not attributable to a common parsing error. Several concern limits intentionally changed in gap implementation, but classification requires checking explicit configured bounds separately from default-unbounded behavior. An owner should inspect before updating assertions.

Other Core suites and frozen-source applicability passed at this exact SHA; no claim about candidate readiness follows from that.

## Durable, run 42544805823 (still running)

Job IDs: Windows `128927362765`, macOS `128927362868`, Linux `128927363063`. Connector raw job logs confirm first Durable run fails on every OS at the same case:

`m2.sqlite.tool-turn-reads-edits-runs-and-answers-then-survives-reopen`, `tests/PiSharp.Durable.Tests/M2Cases.cs:104`: raw JSON substring checks literal `<cwd>` but the current JSON serializer emits `\u003Ccwd\u003E`. Actual decoded system string contains the correct cwd; use parsed system content rather than serializer-specific raw escaping. Linux retention0 run repeats exactly this failure. This is a fixture assertion mismatch, not evidence read/edit execution fails.

Linux completed at the late evidence refresh. Full uploaded reports are now in `artifacts/release-prep/durable-late-42544805823/`. Durable normal/retention0 each have 4,665 cases, one M2 failure and two explicit skips. ModelRuntime has 14 cases and one failure: `catalog.every-embedded-shard-loads`, `CatalogCases.cs:32`, `qwen-token-plan-individual-cn has no shard`. Its final loop incorrectly assumes every current provider belongs to the frozen source catalog; preserve exact frozen resource/hash checks and separately validate reviewed additional providers.

WebAgent has 539 cases and one additional failure: `runs.engine.codex-takes-its-service-tier-and-the-patched-options`, `tests/PiSharp.WebAgent.Tests/RunEngineCases.cs:586`. `src/PiSharp.WebAgent/Routing/RunCaptureHandler.cs:267` rejects the Codex request because strict `get_weather` is absent from its request tools; run status becomes error and notes say options request was not found. Raw failure and stack are in `webagent-report-ubuntu-latest/webagent-ubuntu-latest.json`. This remains a runtime/fixture contract mismatch requiring focused diagnosis; it is not covered by CI2 runner fixes.

The same Linux manifest records actual passing cases: Chord 875, DurableCli 119, Durable.Hosting 129, Durable.Server 31 and Durable.Web 17. Windows/macOS jobs were still running at this refresh, so do not infer their final results.

Native daemon run `42544805898`: Windows/Linux pass; macOS slow-consumer PTY failure belongs to MAC1 and is not duplicated here.

## Candidate impact / next gates

The existing website can be updated with qualified feature documentation, and unpublished candidate artifacts can be prepared, but hosted release readiness is blocked. Fix parser/fixture prerequisites first, independently fix Unix lock/socket portability and MAC1 PTY drain, resolve CliSync/Core behavior mismatches, then rerun required workflows on the exact candidate SHA. Preserve allowed Windows FIFO/Unix platform-specific skips and authored-vs-captured evidence labels. All source hypotheses above are explicitly identified; no tests were rerun by CI1.
