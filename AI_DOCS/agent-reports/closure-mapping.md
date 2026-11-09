# Closure mapping audit

This is a historical mid-implementation ownership snapshot. The assignments and source gaps below were subsequently resolved; use [the current mapping](../../compatibility/gap-status-2026-11-08.json), [execution plan](../gap-execution-plan.md), and [validation ledger](../validation-status.json) for current implementation and acceptance evidence.

Read-only reconciliation of current implementation, 2026-11-08. Only this report is edited. Root: `E:/AI/PiSharp/gap-implementation`; original baseline `14dd34b369fba6d236684da6dfc8a39a03263321`. References: `gap-execution-plan.md`, original audit/register, original applicability inventory, current worker reports, current source, and live worker inventory. Source is concurrently changing; this is an ownership/acceptance snapshot, not an immutable runtime acceptance.

Requested assignment: gpt-6.1-sol/high, Ultrafast. Effective tier unverified. No builds, restores, live providers, migrations, commits, or child agents. All runtime validation belongs to I13/root and the serialized helper.

## Findings requiring explicit ownership

1. **Unassigned: `chord.delta-tracker-open-change`.** D14 calls for a separate Delta worker at `D14.md:128`; design_chord confirms it has not assigned/implemented it. Current `src/PiSharp.Durable/Chord/Delta/Tracker.cs:19,107,232` still uses a HashSet of weak references and allocates a weak registration for every change. Implement primary-open plus weak competitors with preserved stale/change semantics and allocation/lifetime controls. This is an original phase-6 row, not an in-flight H1 defect.
2. **Unassigned: `chord.telemetry-sqlite`.** D14 calls for a separate telemetry worker at `D14.md:129`; none is active/named, and no corresponding current source was found. Required scope includes recorder/store, bounded spans, aggregate/flush/retention, permanent write-failure stop and query APIs. Do not confuse this with existing SQLite counters or I12's LRU.
3. **F09 provider/catalogue portion lacks a named implementation worker.** I01 owns and implements schema APIs, explicitly excludes catalogue/provider work (`I01.md:28,45`). I06 explicitly implements only the 18 C19 corrections and adds no models/prices (`I06.md:6`). The frozen candidate adds `packages/ai/src/providers/qwen-token-plan-individual-cn.ts`, with a new provider ID, CN endpoint and `QWEN_TOKEN_PLAN_CN_API_KEY`; F09 also names Copilot/model additions/defaults. Root retains general responsibility but should name the exact reviewed add/no-op decisions and source changes before closing F09. Preserve frozen shards; use the reviewed overlay/catalogue pipeline and credential precedence tests.
4. **F04 is planned, not currently active.** I18 appears as `pending` in execution plan; no live `impl_bulk_sql` worker/report was present. C03/C23 I12 work does not itself prove bulk prefetch/group optimization. Either activate I18 after I12 settles or record an explicit scoped disposition. Required tests: ordered prefix/fault semantics, stored malformed shape, IDs/create/update/retire/copy, surrogate data, SQL-count and measured latency. No performance improvement can be inferred from an LRU.
5. **Schema corpus/package admission owner is only implicit.** X1 is assigned Durable.Store/real-host examples, J1 owns job fingerprints, H1 real-host behavior; no dedicated owner was confirmed for the full schema-switch tables/jobs/owner corpus and experimental schema API needed for package admission. `tests/PiSharp.Chord.Tests/Schema/SchemaSwitchCases.cs:11-12,66-72` still compares contracts/documents only. `PiSharp.Chord.csproj:8` and `PiSharp.Durable.Store.csproj:8` remain nonpackable. These may remain intentionally repository-only, but root must decide and record that; implementing Host does not automatically make the assemblies distributable.
6. **New regression projects need ongoing gate registration.** I13 fixes the original ten-core/release22/Durable lanes. New dedicated AnthropicThreads, OpenAICompatibilityGap, SimpleOutputBudget, ResponsesTranscriptTools, ClassifierConfig, Env.*, Evals and additional Chord projects are not all covered by those old suite lists. Local final runs are necessary but do not supply future regression coverage. Root/I13 should register required new suites in a single maintained manifest/workflow, with platform-specific Env/PTY/AOT and real process gates.

These items were sent to root while work continued. No active worker's unfinished code is classified as a failure.

## State legend

- **Source / validation pending:** meaningful source/tests or a worker report exist; final integrated execution is not asserted.
- **Active:** named implementation worker owns it; no completion asserted.
- **Retained:** intentional product/runtime difference; requires explicit current disposition and controls, not parity language.
- **Gap:** owner/acceptance omission listed above.

## C01-C23 correctness register: 23/23 assigned

| ID | Owner | Concrete implementation / acceptance anchor |
|---|---|---|
| C01 | I01 | SchemaResolver/Compiler/Coercer/Explainer and Schema.Tests GapCases; relative/nested bases, dynamic/unevaluated refs, actual tool validation. Source, final rerun pending. |
| C02 | I02 | Anthropic root definitions in shared converter; dedicated Threads schema matrix and strict fallback controls. Preliminary dedicated pass, integrated rerun pending. |
| C03 | I12 | Durable.Sqlite stored-shape decoding separated from semantic refusal. Whole-call rollback/no receipts on corrupt shape; prefix commits on true refusal. Source review coherent; final validation pending. |
| C04 | I11 | SessionHarnessSettings and TeamSettings dynamic forwarding; dedicated host/team tests. Source, validation pending. |
| C05 | I07 | EditPlan original byte/newline spans; GapFileTools mixed-ending/BOM controls. Source, validation pending. |
| C06 | I07 | rg-produced bounded context replacing full-file reads; UTF-16/CR/cancellation controls. Source, validation pending. |
| C07 | I10 | Owned catalogue refresh drained on exit; fake credential-rotation/persistence gates and late-signal exit-code fix. Active/final validation pending. |
| C08 | I08 | PromptTemplateExpander quote parser; apostrophe/unmatched/escaped/empty cases. Source, validation pending. |
| C09 | I03 | Contracts/Json/LocalSchemaReferences plus both rankers and codemode output; cycles/malformed/unused/shared refs. Minor reviewer inline-plus-reference term-frequency question requires disposition. |
| C10 | I09 | MCP CR/LF/CRLF framing; split delimiter/open-stream tests. Focused rerun pending; earlier 30/32 run is not acceptance. |
| C11 | I09 | Refresh before occupied callback-port re-registration; fake endpoint tests. Source, validation pending. |
| C12 | I10 | RPC idle shutdown signal/admission/response ordering; repeated shutdown/cleanup cases. Active/final validation pending. |
| C13 | I08 | PersistentAgentSession.ThinkingPersistence and navigation-to-prompt commit; reopen/failure cases. Source, validation pending. |
| C14 | I08 + I07 | UnicodeText prefix/suffix at summary/codemode/grep consumers; non-BMP boundary tests. This does not cover word segmentation. |
| C15 | I07 | grep cwd/searchPath-relative glob handling and ./ cases. Source, validation pending. |
| C16 | I07 | find/ls/grep normalized limits and lookahead; exact versus excess cases. Source, validation pending. |
| C17 | I04 | Completions RequiresToolsWithToolHistory with metadata/caller defaults; actual request matrix. Preliminary matrix pass; integrated rerun pending. |
| C18 | I05 | SimpleOutputBudget and all seven consumers; 131 authored cross-provider cases. Source, validation pending. |
| C19 | I06 | Maintained overlay's 18 scoped rows plus binding/temperature propagation; no frozen shard rewrites. Source, validation pending. |
| C20 | I04 | Responses session-id only, caller header precedence and Completions control. Preliminary pass; final rerun pending. |
| C21 | I04 | Both retry classifiers exact cut-short phrase/permanent exclusions. Preliminary pass; final rerun pending. |
| C22 | I19 | Typed package update results, unchanged/offline/warning/version/commit outputs. Source, validation pending. |
| C23 | I12 | 256-entry LRU, eviction disposal/reprepare, bounded instrumentation. Source reviewed; final validation pending. |

## F01-F09 new capability register: split ownership required

| ID | Owner / state | Remaining acceptance |
|---|---|---|
| F01 | I17 source | Whole-object clear/replace/inheritance, all precedence, disabled versus manual, overflow/background and checkpoint reopen. |
| F02 | I17 source | Public ITx committed lookup, post-write/settled rejection and third-party default-interface behavior; release notes. |
| F03 | I17 + I21 source | Actual CLI/main and Web account routes, reasoning capability preserved, pinned request reopen, per-request disposal, budgets and socket timeout. Reviewer RUNTIME-1/2/4 fixes require final inspection/run. |
| F04 | I18 pending / gap | Explicit active optimization owner and measured SQL-count/latency controls after C03. |
| F05 | I04 source | Non-null assistant independent of synthetic insertion; cache-key auto/enabled/disabled across direct/simple/Azure/model override. Preliminary authored pass. |
| F06 | I07 source | Slice of >64MiB file, bounded memory and fallback adapters, EOF/encoding/cancellation. |
| F07 | I19 source | API-key/browser/device/no-browser login/logout; persisted credential lifetime; provider-auth MCP guidance. |
| F08 | I02 + I10 source | Threads and shared cache warm; OAuth effective projection review fix, session retirement, changed prefix/options, stale/concurrent response, disabled continuation, fallback failures. Preliminary authored pass; final consumer/regression run pending. |
| F09 | I01 schema source; root provider/catalogue gap | Schema Validate/Equal/Recursive/Unsafe/type representation decisions exist. Exact new providers/models/defaults need reviewed disposition and tests; I06 is only C19. |

## Original 19 ported-with-deferral rows: 19/19 traced

| Exact row ID | Current owner / disposition | Required gate |
|---|---|---|
| ai.client-identity | I21 + I17 | Default NativeDurableModels identity plus explicit request merge; retain no attribution. Broader Models semantics must match actual public surface. |
| ai.transcript-tool-support | R6/design_remaining | Actual Responses/Codex/Azure none/additions/changes payloads, anchored batches/search/removal/redefinition and cache prefix. Source report R6.md. |
| ai.system-one-compat | I20 | Real configured classifier HTTP calls and reload, credential precedence; chat-only overrides do not create classifiers. |
| ai.tool-call-args-incremental | R5 Mistral/PiMessages; R6 Completions assessment; I02 Anthropic assessment requested | Immutable observed snapshots, interleaving/failure, measured scaling. R6 reports no repeated failure serialization to fix. Anthropic residual needs explicit source disposition. |
| core.exec-max-buffer | R4 | Combined >1MiB stdout/stderr, exact/over limit, UTF-8, kill/drain ownership. |
| core.session-write-failure-rollback | Retained under W24/I08/root | Native fail-stop after uncertain write; later mutations rejected, no state-ahead-of-disk, incomplete-tail reopen. Do not silently adopt optimistic rollback. |
| cli.custom-overlay-own-handle | R8 | Real Node onHandle, dynamic visibility, resize/focus/stack, stale/late/dispose. Arbitrary object unfocus target remains explicitly unsupported per R8 contract. |
| tui.native-shift-enter-esc-return | R7 | CoreGraphics source and synthetic controls; physical macOS host acceptance outstanding. |
| tools.tool-search-unicode-words | D16 follow-up/W24 | Current UnicodeProfileCases child processes compare normal/invariant/ICU/fallback and disclose .NET sigma/NFC differences. Intentional differences remain; no exact V8 segmentation parity claim. |
| bridge.pi-ai-extension-view | R8 | Approved installed Pi1.5 runtime policy/no default attribution, real package/Jiti/TypeBox imports and Agent loop, version-policy cache isolation and rollback. |
| durable.store-storage | X1 + H1/H2/J1 | Actual host-owned batches, followed views/deferred commit order, conformance/reopen, examples35/36. Existing hand-built passing cases alone insufficient. |
| durable.harness-identity | I21 + I17 | Chat/classify/images/fetch/cancel/options overloads/hooks/tools; supported native fake HTTP and honest unsupported deferred behavior. |
| durable.before-provider-request | I21 | Scoped account routes, concurrent different hooks/no leakage, actual serialized body and owned/default handler disposal. |
| durable.registry-reader | X1 + H1/H2 | Actual contribution/load/retry/follower failure state, example37, close and next Harness. |
| chord.next-entry-point | H1/H2/J1/L1/R1/N1 | Verify exported usable layers, not only source classes; packaging disposition separately owned by root. |
| chord.next-contracts-data | J1 + X1/H1; corpus owner gap | Add table/job recorded fingerprints and real-host stored schema fixture; preserve historical blobs. |
| chord.next-documents-state | J1 + H1/H2 | Actual job-run transaction slots and host drain/closeTimeout. Retain documented CLR draft/microtask alternatives, rather than impossible JS proxy semantics. |
| chord.next-store | X1 + H1 | Real-host owners/refusal and publication; Cloudflare stays intentional exclusion. |
| chord.next-registries | H1/H2 + X1 | One-per-host/setup-only staging, provider load failure, followed-ended observer, parked/reachability/publication states and actual Durable integration. |

## Original 37 port rows: exact row-to-worker mapping

| Exact row ID | Owner / state | Row-specific acceptance beyond existence of code |
|---|---|---|
| env.remote-watcher-stop | E04 | Client stop/abort acknowledgement and watch resources end once. |
| env.daemon-helper-thread-spawn-error | E02/E05 | Failed helper/process setup returns protocol error and releases all request resources. |
| env.daemon-exit-signals | E02/E05/E06 | Real daemon/child signal exit and pipe/group cleanup; Unix platform gate. |
| env.connection-startup-stderr | E01/E07 | Bounded sanitized startup stderr attached to real failure; process joins. |
| env.daemon-watch-frame-overflow | E04 | Bounded queue, one overflow signal, recovery/session close. |
| env.remote-execution-env | E01/E03/E05 | Real remote adapter conformance, session isolation, reconnect/cancel and shell/process semantics. |
| env.ssh-bootstrap | E07 | Verified catalog/atomic upload/host-key refusal; fake subprocess source tests plus target artifact gates. |
| env.terminal | E06 | Real Unix PTY raw bytes/foreground/resize/exit; Windows ENOTSUP retained as platform disposition, not success. |
| env.daemon | E02/E05/E06/E07 | Native six-target publish/run/shim and explicitly verified Android backend; hashes alone are not execution. |
| env.conformance-tests | E03 plus E02/E04/E05/E06/E07 | Existing EnvConformance applied to the remote adapter, operations and disconnections; not only independent per-operation fakes. |
| chord.delta-tracker-open-change | GAP, no worker confirmed | Primary open + competitors, stale reads, collection/allocation. |
| chord.sqlite-statement-cache | I12 source | Same C23 LRU/dispose/reprepare gate. |
| chord.telemetry-sqlite | GAP, no worker confirmed | Recorder/store/query/flush/retention/failure contract. |
| chord.contracts | LG2 | Legacy schema/fingerprint/placement, not new v2 Contracts alone. |
| chord.contract-schemas | LG2 | Legacy schema validation/default stripping and exact fingerprints. |
| chord.objects-declarations | LG2 | Lifetime/serve/variant runtime refusals and native declarations. |
| chord.hosts-facets-principals | LG2 | Actual legacy host/context/principal propagation and lifetime. |
| chord.references | LG1/LG2 | Variant/remote reference binding/retention and broken-link behavior. |
| chord.wire-protocol | LG1 | Exact protocol4 golden frames distinct from L1 next protocol. |
| chord.links-transports | LG1/LG3 | Actual WebSocket/child pipe transport ownership and explicit Node interoperability contract. |
| chord.links-flow-control | LG1 | Byte-counted ACK window, pending cap, result-versus-state ordering. |
| chord.links-compression | LG1 | Exact threshold/encoding/untrusted packed refusal and bounded inflation. |
| chord.routes-relays | LG1/LG3 | Dead route healing, worker parent lifetime routes and no timer leak. |
| chord.reload | LG2/LG3 | Legacy generation/lifetime reload behavior with actual retained objects. |
| chord.cluster-reload | LG3 | prepare/ready/commit/retire and late/early child cases. |
| chord.kernel-retention | LG2/LG3 | Actual roots/delegate/ALC/resource collection; plain-object GC insufficient. |
| chord.host-manager | LG3 + N1 shared native substrate | Legacy discovery/process locking/lifecycle, distinct protocol adapter. |
| chord.registries-v1 | LG2 | Legacy registry contribution/view semantics, not v2 RegistryRecord alone. |
| chord.tests-docs | All Chord workers + root | Full row-to-case disposition, examples/docs/API/export status and final matrix. |
| chord.next-host-objects | H1 | Real local host/load/publication/lifetime/park/follow/native/state/store. |
| chord.next-reload | H2 | Transactional staged compatible in-place replacement plus failure retention. |
| chord.next-jobs | J1 | Persisted intent/run/finish, same-tx dedupe/concurrency, close/reopen/reload and fingerprints. |
| chord.next-links-remote | L1/R1 | Exact next wire and real remote route/call/state/revocation/reconnect. |
| chord.next-plugins | N1 | Actual native loader/build/shared identity/private imports/failure preserving prior generation. |
| chord.next-http | N1 | Actual handler/wrappers/routes/static/stream/WebSocket/auth/allowed hosts; loopback defaults. |
| chord.next-workers | N1/R1 | Real child processes, late attach/restart, bounded stop/kill and parent links. |
| evals.harness | R9 | Deterministic offline paired protocol, blocking invalid observations, staged fake executable/container recorder; live remains explicitly opted in. |

## V01-V05 and ancillary disposition

| ID | Owner / state | Final check |
|---|---|---|
| V01 | I13 source | Core ten console runners execute, fixtures present, skips rejected; new project registration gap above. |
| V02 | I13 source | Release depends on reusable core/Durable jobs at exact github.sha; three-OS/zero-retention/MSSQL/SQLite assets remain visible. Hosted run not yet performed. |
| V03 | I13 source + structural checks passed | Offline and actual-object applicability/worklist/port map; final inventory must reflect new row dispositions without relabeling history. |
| V04 | capture_v150 active | Separate genuine v1.5 corpus and native reader comparisons, repeat/hash/normalization manifest; generated files alone are not comparison success. |
| V05 | I13/root | Final integrated source fingerprint/commit/platform/suite/prerequisite/result/skip ledger. Preliminary concurrently-edited receipts cannot close it. |

D16 follow-up explicitly owns all seven W24 candidates: queued images, overlay/blocked state, model cycling, prerelease changelog, tool-result disposal, reload/navigation concurrency, background-process Bash timeout. Current new `GapCandidateCases.cs`, `GapToolDisposalCases.cs`, and Unicode profile cases support this ownership; W24 report is pending. D16 reports three found source issues (prerelease repeated range, renderer disposal, Bash prior-exit timeout metadata) with fixes under validation, while other candidates are being dispositioned from actual existing tests. They should not disappear because they lack C IDs.

Retained boundaries require a release-scope statement: no-attribution default; uncertain session-write fail-stop; CLR draft/microtask alternatives; system ICU behavior; Cloudflare/browser backends; no paid/live CI; version remains 1.5.0 unless root makes a deliberate release decision. R8's approved installed-runtime change is a separate scoped change, not an automatic historical-pin rewrite.

## Final integration checklist for root

- Name owners for confirmed gaps 1-5, or explicitly record accepted scope/disposition.
- Give each of 23 C, 9 F, 19 partial rows, 37 deferred rows and 5 V entries an actual final source/test/status reference. Do not change original historical counts into new completion claims before the evidence exists.
- Resolve review-core OAuth thread projection and term-frequency question; review-runtime request reasoning/Web control/default handler ownership and late-signal fixes require inspected final source and tests.
- Include new project gates, real child/network-loopback integration, target-specific native PTY/AOT, and no unsupported-platform silent pass.
- Final build/test execution must occur after shared source settles through I13; record failures/skips rather than carrying forward old or pre-correction passes.
