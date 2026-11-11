# Pi and Pi Durable 1.5.2 update

Authorized 2026-11-10: check Pi and Pi Durable and make all updates to the new version.

## Frozen inputs

- Implementation: `E:/AI/PiSharp/update-1.5.2`, branch `sync/pi-1.5.2`, from PiSharp main `9d2b64f94dcb357b293f6b31452dff36388849fe`.
- Reference: `E:/AI/PiSharp/upstream-v152`, Pi v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`. Compare with v1.5.0 `f9a26365568127461c47a07a37dcb42c140a3ac5` in the same Git repository.
- Existing user checkout and other worktrees remain untouched. Preserve historical capture and release pins.
- Scope: all applicable package changes through v1.5.2, including v1.5.1. Runtime changes need implementation and regression evidence, not only new version strings. Browser-only/TypeScript-only behavior needs an explicit applicability explanation.
- No publication, deployment, live provider calls, or credentials. Do not silently alter model choices or broaden Anthropic subscription OAuth policy.

## Discovery and contracts

Discovery workers first enumerate their changed upstream source files and changelog bullets, map to native implementations, identify already-ported changes, and propose precise C# contracts. Write `AI_DOCS/agent-reports/sync-1.5.2-<area>-discovery.md` and report to root. Implementation depends on root reviewing contracts and enabling it. Do not edit shared version, status, target, generated inventories, solution, release documentation, or another worker's paths.

Root owns common metadata, source locks, package pins, integration, and final validation. Test source ownership follows each area; shared test Program.cs registration needs coordination. Workers can add a separate test file and report registration needed. Builds/runs are serialized by root after code integration; do not launch dotnet builds independently. Node-only isolated tests are allowed.

## Work plan

Requested worker service tier is ultrafast; the delegation API has no tier selector, so effective tier is unverified. Main model assignments are planning guidance, not a change to the active main model.

| id | agent_type | complexity | model | reasoning_effort | service_tier | complexity_reason | goal | depends_on | owned_paths | inputs | outputs | verification | status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| D-AI | explorer | hard | gpt-6.1-sol | high | ultrafast | Provider wire and model contracts | Discover AI/agent/catalog deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-DUR | explorer | hard | gpt-6.1-sol | high | ultrafast | Durable scheduler and compaction | Discover harness/session deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-STO | explorer | hard | gpt-6.1-sol | high | ultrafast | Crash consistency and batching | Discover JSONL/SQLite/store deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-CLI | explorer | hard | gpt-6.1-sol | high | ultrafast | Interactive/session lifecycle | Discover coding-agent deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-TUI | explorer | medium | gpt-6.1-sol | medium | ultrafast | Terminal state handling | Discover TUI deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-MCP | explorer | hard | gpt-6.1-sol | high | ultrafast | OAuth and transport lifetime | Discover MCP deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-SCH | explorer | hard | gpt-6.1-sol | high | ultrafast | Schema semantics and tool search | Discover schema/codemode/durable tools deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-ENV | explorer | hard | gpt-6.1-sol | high | ultrafast | Remote process protocol | Discover env deltas | none | report only | frozen refs | exact contracts and inventory | source comparison | complete |
| D-CHO | explorer | very hard | gpt-6.1-sol | xhigh | ultrafast | Broad Chord runtime migration | Decompose Chord changes and native gaps | none | report only | frozen refs | component work packages and contracts | source comparison | complete |
| META | main | hard | gpt-6.1-sol | high | unverified | Exact source and release metadata | Freeze source and integrate pins | none | compatibility, tools, docs, root metadata | frozen refs | new source target and complete applicability | integrity checks | tested-with-limitations |
| VERIFY | main | hard | gpt-6.1-sol | high | unverified | Cross-library regression coverage | Build and run relevant suites, resolve failures | implementation | integration fixes and reports | integrated tree | measured test results | serialized builds/runs | tested-with-limitations |

Implementation rows and contract decisions are added after discovery.

## Implementation and verification work

All discovery contracts are recorded under `AI_DOCS/agent-reports/sync-1.5.2-*-discovery.md`. Source-ready rows below stay running until their relevant validation passes. `/root/validation` owns the one serialized build/run slot; main retains final acceptance. Model effort/tier declarations are assignment requests, not runtime certification.

| id | agent_type | complexity | model | reasoning_effort | service_tier | complexity_reason | goal | depends_on | owned_paths | inputs | outputs | verification | status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| I-AI | worker | hard | gpt-6.1-sol | high | ultrafast | Request state lifetime | Shared strict fallback and cache options | D-AI | AI protocols/auth | AI report | Runtime changes | ProviderSync | tested-with-limitations |
| I-CAT | worker | hard | gpt-6.1-sol | high | ultrafast | Complete exact catalog and native capability binding | Exact 1.5.2 snapshot: 43 providers, 1,773 entries; 82 adds, 21 removes, 104 changes; native binding regression coverage | D-AI | Current catalog and historical layers | AI report and 1.5.2 package provenance | Exact snapshot plus native capability projection | Models, ModelRuntime, provider request tests | tested-with-limitations |
| I-REFRESH | worker | hard | gpt-6.1-sol | high | ultrafast | Auth versus public catalog | Configured-only refresh | D-AI | CLI model registry | AI report | Auth gate | ModelRuntime | tested-with-limitations |
| I-DUR | worker | hard | gpt-6.1-sol | high | ultrafast | Scheduling and storage reads | Durable residual fixes | D-DUR | Durable Harness | Durable report | Lazy failure reads | Durable | tested-with-limitations |
| I-JSONL | worker | hard | gpt-6.1-sol | high | ultrafast | Recovery integrity | Read-only JSONL | D-STO | Durable JSONL | Storage report | Read-only snapshots | Durable | tested-with-limitations |
| I-SQLITE | worker | hard | gpt-6.1-sol | high | ultrafast | Batched transaction semantics | Bounded SQLite statements | D-STO | Durable.Sqlite | Storage report | Batch checks | Durable | tested-with-limitations |
| I-MSSQL | worker | hard | gpt-6.1-sol | high | ultrafast | Stored corruption rollback | Preserve whole-call failure | D-STO | Durable.MSSQL | Storage report | Decode classification | Durable offline executor | tested-with-limitations |
| I-SCHEMA | worker | hard | gpt-6.1-sol | high | ultrafast | JS value model | Finite/bigint/undefined semantics | D-SCH | Contracts Schema | Schema report | Validators and boundary | Schema | complete |
| I-CODE | worker | hard | gpt-6.1-sol | high | ultrafast | Ref resolution and formatting | Strict refs and nonfinite output | D-SCH | Codemode | Schema report | New API/prelude | Codemode | tested-with-limitations |
| I-ENV | worker | hard | gpt-6.1-sol | high | ultrafast | Pipe backpressure/lifetime | Ordered asynchronous input | D-ENV | Env and daemon | Env report | FIFO admission | Env suites | tested-with-limitations |
| I-MCP-AUTH | worker | hard | gpt-6.1-sol | high | ultrafast | Shared sign-in state | Split sign-in/helpers | D-MCP | MCP OAuth/config | MCP report | APIs and CLI adapter | McpSync | tested-with-limitations |
| I-MCP-IO | worker | hard | gpt-6.1-sol | high | ultrafast | Transport lifetime | Pipe transport/error bounds | D-MCP | MCP transport | MCP report | Transport and tail | Extension contracts | tested-with-limitations |
| I-SESSION | worker | hard | gpt-6.1-sol | high | ultrafast | Cwd and replacement authority | Missing-folder fork/import | D-CLI | CLI/session owner | CLI report | Typed choices/rollback | Session/Interactive | tested-with-limitations |
| I-TOOLS | worker | hard | gpt-6.1-sol | high | ultrafast | Streaming and atomic edits | Read/edit/find/PowerShell fixes | D-CLI | Tools | CLI report | Bounded operations | Tools | tested-with-limitations |
| I-INPUT | worker | hard | gpt-6.1-sol | high | ultrafast | Delivery/replay identity | Images and idle warmer | D-CLI | Agent/cache | CLI report | Normalization/retention | CodingAgent/Session | tested-with-limitations |
| I-AUTH-PKG | worker | hard | gpt-6.1-sol | high | ultrafast | Shell auth/package state | CLI login/update results | D-CLI | Auth/packages | CLI report | Commands/diagnostics | CLI/Extension | tested-with-limitations |
| I-TUI | worker | hard | gpt-6.1-sol | high | ultrafast | Terminal rendering | Markers/images/comments | D-TUI | Tui | TUI report | Rendering corrections | Tui | tested-with-limitations |
| I-UI | worker | hard | gpt-6.1-sol | high | ultrafast | Interactive state | Selectors/keybindings/flow | D-TUI | CLI UI | TUI report | UI changes | Interactive | tested-with-limitations |
| I-MARKDOWN | worker | hard | gpt-6.1-sol | high | ultrafast | Extension surfaces | Typed filtered transforms | D-TUI | Markdown/bridge | TUI report | Six surfaces | Node/Interactive/Extension | tested-with-limitations |
| I-MERMAID | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | New parser/layout package | Lovely 0.3.3 native port | D-TUI | Lovely namespace | Exact npm source | Nine kinds/styles | Exact differential and UI | tested-with-limitations |
| C-K | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Reload/call authority | Kernel old-work/readiness | D-CHO | Chord Host | Chord report | Restart/gates/telemetry | Chord | tested-with-limitations |
| C-W | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Protocol ordering | Wire/remote state and tracing | D-CHO | Chord Link/Remote | Chord report | Codecs and calls | Exact codec/Chord | tested-with-limitations |
| C-HD | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Keyed state and adoption | Host data integration | D-CHO,D-STO | Contracts/Documents/HostData | Reports | Keyed APIs | Chord | tested-with-limitations |
| C-STORE | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Migration/transactions | Queries/host import/export | D-STO | Chord Store | Storage report | Schema3/query/archives | Chord/Durable | tested-with-limitations |
| C-HTTP | worker | hard | gpt-6.1-sol | high | ultrafast | Admission and connection trust | HTTP default deny/lifecycle | D-CHO | Hosting Http | Chord report | Public routes/tracing | Hosting | tested-with-limitations |
| C-PL | worker | hard | gpt-6.1-sol | high | ultrafast | Plugin dependency closure | Build/discover/watch/testing | D-CHO | Hosting Plugins | Chord report | Native plugin APIs | Hosting | tested-with-limitations |
| C-WK | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Child lifetime/base identity | Frozen worker deployment | D-CHO | Hosting Workers | Chord report | Snapshots/recovery | Real child Hosting | tested-with-limitations |
| C-A1 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Host startup/auth/persistence | App kit/session/auth | D-CHO | Hosting App bootstrap | Chord report | App runtime | Hosting | tested-with-limitations |
| C-A2 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | File metadata and uploads | File storage service | D-CHO | Hosting App Files | Chord report | Blob/version/upload APIs | Hosting | tested-with-limitations |
| C-A3 | worker | hard | gpt-6.1-sol | high | ultrafast | Archive atomicity | Worker archives | D-CHO | Hosting App Archive | Chord report + final format2 | Guarded import/export | Hosting | tested-with-limitations |
| C-SUR | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Reconnect/preferences | Portable surfaces | D-CHO | Chord Surfaces | Chord report | Clients/themes/languages | Chord | tested-with-limitations |
| C-PUB | worker | hard | gpt-6.1-sol | high | ultrafast | Immutable artifact lifecycle | Surface publication | D-CHO | Hosting Surfaces | Chord report | Hash cache/routes | Hosting | tested-with-limitations |
| C-TERM | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Terminal bridge authority | Native terminal/launcher | D-CHO | Hosting Terminal | Chord report | IO/actions/filter | Hosting | tested-with-limitations |
| C-SYNC | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Outbox reconciliation | Portable session synchronization | D-CHO | Chord Sync | Exact final source | Remote/state/client | Chord | tested-with-limitations |
| T01 | worker | hard | gpt-6.1-sol | high | ultrafast | Timing and trace metadata | Clock/render/schema/helpers | D-TEL | Telemetry and helper | Telemetry report | Clock/schema | Telemetry/Chord | tested-with-limitations |
| T2 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Ingest bounds and lifetime | Recorder/facet/sender | D-TEL | Chord TelemetryIngest | Telemetry report | Ingest APIs | Chord | tested-with-limitations |
| T3 | worker | hard | gpt-6.1-sol | high | ultrafast | Time storage migration | SQLite v2/read-only | D-TEL | TelemetrySqlite | Telemetry report | Migration/readers | Chord/Durable | tested-with-limitations |

## Validation evidence

Accepted current-source local evidence is frozen in [the source ledger](../AI_DOCS/sync-1.5.2-source-validation.json) and [expected-case coverage](../AI_DOCS/sync-1.5.2-source-coverage.json), with [scope and limitations](../compatibility/sync-1.5.2-acceptance.json). The mutable [run ledger](sync-1.5.2-validation.json) preserves superseded failures and reruns; exact raw receipts remain under ignored artifacts/gap-validation/. The [separate packaging record](../AI_DOCS/sync-1.5.2-packaging.json) records the later package stage; source acceptance does not assert package validation.

The optional secure execution of downloaded terminal JavaScript remains an explicit adapter boundary: trusted native DLL execution is not described as a sandbox. Browser DOM/React hooks are platform APIs, not fabricated C# implementations.

## Independent review corrections

The focused reviews in `sync-1.5.2-{data,cli,telemetry,kernel}-review.md` identified real integration defects. Implementation owners added corrections and regression cases; runtime acceptance is recorded separately in the validation ledger.

- File put/finish/copy now preserve installed content after an ambiguous metadata commit, and copy retains the source export hold through blob acquisition.
- Worker telemetry shutdown bounds its final send even when a previous periodic send has stalled.
- Retiring a session attachment cancels and joins that attachment's cache replay before disposing its transport and extensions; a later attachment is unaffected.
- Prompt image normalization forwards the configured JPEG quality.
- Loading rejects queued unsent method indexes while preserving calls already sent, preventing a stale index from reaching a rebound object.
- Old-work lineage survives settlement of its parent context without retaining the entire retired attempt.

These corrections are not evidence of full upstream parity. Original failure output and source-specific test receipts remain retained.

## Final local source validation

Status: **tested-with-limitations**, as recorded in [the acceptance record](../compatibility/sync-1.5.2-acceptance.json). This records only the accepted current-source suite scope and explicit limitations; the candidate remains unreleased.
