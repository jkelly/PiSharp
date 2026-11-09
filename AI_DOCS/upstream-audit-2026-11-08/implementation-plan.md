# Implementation plan — PiSharp gap closure

Status: **proposed, not executed**. Prepared 2026-11-08 from the [audit](gap-audit.md). Scope is current main `14dd34b`; do not build this on the older active branch. Preserve unrelated working-tree work. Existing release baseline remains Pi 1.5.0 until acceptance of a later full sync.

## Delivery sequence

1. **Establish reproducible gates, then fix shipped correctness.** Start W01; run W02/W03/W07/W09/W10/W11/W12 on isolated current-main branches with disjoint ownership. W07 and W12 cover data preservation and storage errors, not just upstream feature parity.
2. **Complete provider and session behavior.** W04/W05/W06/W08; W09 and W10 coordinate the MCP/auth shutdown contract. W15 makes core and exact-release verification enforceable.
3. **Add the useful Durable APIs.** W13 after settings and provider contracts. W14 bulk SQLite follows the corrected failure semantics and has measured performance gates.
4. **Close disclosed baseline gaps in separate milestones.** W16–W22 cover the 19 released deferrals and 37 later rows without claiming every JavaScript concept maps directly to C#.
5. **Consider optional unreleased architecture.** W23 Anthropic continuation/shared cache warm; W24 the remaining candidate/API disposition pass. W25 integrates, re-runs the right gates and records release scope.

No package should combine a schema/wire compatibility change, an unrelated UI feature and a performance refactor. A partial backport must list its selected commits and exceptions; it must not claim full parity with unreleased main.

## Work packages

Requested worker tier is Ultrafast; effective tier must be recorded from runtime or `unverified`. Models/efforts below are planning assignments. They do not claim this main chat changed model.

| id | agent_type | complexity | model | reasoning_effort | service_tier | complexity_reason | goal | depends_on | owned_paths | inputs | outputs | verification | status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| W01 | main | hard | gpt-6.1-sol | high | ultrafast | Freeze evidence and cross-layer contracts | Current-main branch, repro fixtures, per-SHA manifest | none | AI_DOCS audit; verification tooling | C/F/B/V register | Contract and acceptance baseline | Source identity; reproduce controls | pending |
| W02 | worker | hard | gpt-6.1-sol | high | ultrafast | Schema base/index/coercion/explanation consistency | Fix C01 | W01 | Contracts/Schema; schema tests | Relative-ID repro | One consistent base-URI model | Relative/nested IDs and full conformance | pending |
| W03 | worker | hard | gpt-6.1-sol | high | ultrafast | Provider schema and shared reference graph | Fix C02/C09 | W01 | Anthropic projector; common schema-ref helper; two ToolSearch paths; codemode summaries | Definition/ref fixtures | Preserved definitions; safe traversal | Strict modes; cycles; unused refs; ranking | pending |
| W04 | worker | hard | gpt-6.1-sol | high | ultrafast | Public/model/wire compatibility | Fix C17/C20/C21; add F05 | W01 | AI Completions/Responses/Azure compatibility; both retry classifiers; schema/docs/tests | Proxy request matrix | Options and metadata propagation | Default/override payloads; header precedence; retry parity | pending |
| W05 | worker | hard | gpt-6.1-sol | high | ultrafast | One formula across providers | Fix C18 | W01 | AI simple-provider budget helper and consumers | Frozen upstream algorithm | Shared output-budget calculation | Window/input/explicit-limit boundary matrix | pending |
| W06 | worker | medium | gpt-6.1-sol | medium | ultrafast | Metadata and pinned provenance | Correct C19 | W01 | Model catalogue pipeline/shards or approved overlay; fixtures | Specific upstream metadata diffs | Deliberately scoped catalogue correction | Exact models; hashes; runtime selection | pending |
| W07 | worker | hard | gpt-6.1-sol | high | ultrafast | File byte preservation and bounded memory | Fix C05/C06/C15/C16; add F06 | W01 | Tools/Files; file adapters; tests | Byte, huge-file, glob and limit fixtures | Preserving edit; streaming read/grep | Byte equality; memory; UTF-16; symlinks; limits | pending |
| W08 | worker | hard | gpt-6.1-sol | high | ultrafast | Input text and branch persistence | Fix C08/C13/C14 | W01 | Prompt parser; scalar truncation consumers; PersistentAgentSession | Quote/Unicode/tree scenarios | Literal input; valid truncation; durable thinking selection | Restart and rejected-write tests | pending |
| W09 | worker | hard | gpt-6.1-sol | high | ultrafast | Transport framing and auth state | Fix C10/C11; correct MCP login guidance | W01 | MCP HTTP/SSE; McpSignIn; CLI hints | Fake streams/OAuth client | CR framing; preserve refresh client | Every split delimiter; occupied port; cancellation | pending |
| W10 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Exit ownership and rotated credentials | Validate/fix C07; fix C12 | W01 | PiCommand lifecycle; session profile; RPC shutdown; fake auth tests | Blocked persistence and idle shutdown harness | Owned background work; idle wake | EOF/signal/command ordering; no live keys | pending |
| W11 | worker | medium | gpt-6.1-sol | medium | ultrafast | Settings forwarding through two hosts | Fix C04 | W01 | Durable.Hosting SessionHarnessSettings; CustomAgents TeamSettings; tests | HarnessSettings matrix | Dynamic pass-through of every non-overridden field | Both policies; live getter; queue outcome | pending |
| W12 | worker | hard | gpt-6.1-sol | high | ultrafast | Storage failures versus semantic refusals | Fix C03/C23 | W01 | Durable.Sqlite Storage/ConnectionDatabase; tests | Corrupt-shape probe; cache instrumentation | Correct rollback; bounded statement LRU | Fault matrix; receipt order; 256-entry eviction | pending |
| W13 | worker | hard | gpt-6.1-sol | high | ultrafast | Persisted agent and request contracts | Add F01/F02/F03 | W04,W11 | Durable types/agent/compaction/generation/models/transaction; host mappings | Exact upstream contracts | Conversation policy; tx lookup; stream controls | Reopen; precedence; clear; read-after-write | pending |
| W14 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Ordered group checks and bulk SQL semantics | Add F04 after correctness | W12 | Durable.Sqlite group-commit internals; benchmarks/tests | Working failure contract | Bulk read/check/write implementation | Seeded differential; faults; bounded SQL count | pending |
| W15 | worker | hard | gpt-6.1-sol | high | ultrafast | Console suites and release provenance | Close V01/V02/V03/V05 | W01 | CI workflows; native runner; test manifest/docs | Existing matrices/receipts | Required per-SHA test and inventory gates | Real suite execution; fail on missing prerequisite | pending |
| W16 | worker | hard | gpt-6.1-sol | high | ultrafast | Released provider/core deferrals | Close B01 | W04,W05 | Classifiers; transcript additions; incremental args; exec buffers | Exact 1.5.0 applicability rows | Separate row-level fixes | Public consumer and provider-specific negatives | pending |
| W17 | worker | hard | gpt-6.1-sol | high | ultrafast | Cross-route metadata/hooks | Close B02 | W04,W13 | Models identity; Durable non-chat; scoped account router | Identity and hook contracts | Consistent explicit identity and hooks | Classify/image/deferred/cancel routing | pending |
| W18 | main | very hard | gpt-6.1-sol | xhigh | ultrafast | Node ABI/platform behavioral differences | Resolve B03 into implementation decisions | W01,W03 | Node bridge/extension UI/TUI plans; fixture harness | Existing exceptions; runtime 1.2 pin | Separate ABI, Unicode, physical-key milestones | Real package fixtures and OS-specific tests | pending |
| W19 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Host lifetime and publication ordering | Chord host and real Durable adapters | W11,W12 | Chord host; registries/store; Durable.Store and examples | B04 and existing phase-6 plan | Real-host scenarios and examples 35-37 | Load/refuse/reload/close/follow; fingerprints | pending |
| W20 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Jobs/links/remote wire dependencies | Complete remaining Chord B05 | W19 | Chord jobs/links/wire/reload/worker plans | 26 deferred rows | Versioned protocol and host operations | Transactions; remote failures; job fingerprints | pending |
| W21 | main | very hard | gpt-6.1-sol | xhigh | ultrafast | Remote process/daemon architecture | Decide and implement pi-env B05 in subpackages | W19 | Execution env; remote client/daemon; platform adapters | 10 env rows; binary/native decision | Bounded staged remote environment | Cancel/startup/stderr/watch/Windows; opt-in SSH | pending |
| W22 | worker | hard | gpt-6.1-sol | high | ultrafast | Eval harness depends on stable execution | Complete pi-evals B06 | W16,W19 | Repository eval tooling only | 1 eval row | Offline evaluator and opt-in live path | Synthetic fixtures; no paid CI | pending |
| W23 | worker | very hard | gpt-6.1-sol | xhigh | ultrafast | Stateful server continuation and cache replay | Optional F08 | W04,W10,W13 | Anthropic session state; cache-warm policy; lifecycle | Frozen API and fallback rules | Opt-in/restricted defaults with cleanup | Mutation/concurrency/expiry/cancel/warm isolation | pending |
| W24 | explorer | hard | gpt-6.1-sol | high | ultrafast | Remaining candidates require evidence | Resolve F09 and unconfirmed candidate list | W01 | Bounded relevant paths and upstream commits | Full inventory and excluded list | Port/no-op/defer with proof | Caller/tests, never name-only inference | pending |
| W26 | worker | hard | gpt-6.1-sol | high | ultrafast | CLI results and login lifetime | Fix C22; add F07 | W10 | PiAuthCommand; PiPackageManager/Commands; related tests | Existing login and update flows | Typed update results; shell auth commands | Offline/no-change/warning/version cases; fake login/logout | pending |
| W25 | main | very hard | gpt-6.1-sol | xhigh | ultrafast | Cross-area regression and release scope | Integrate selected milestone and close V04 | Selected milestone packages,W15 | Release docs; acceptance/capture manifests | Tested changes | Exact release scope and evidence | Required suites + new differential corpus | pending |

W03 spans several modules because the shared local-reference contract should have one owner. W07 owns all file-tool edits. W09/W10 must agree on shutdown/auth ownership before merging; W13/W17 must agree on option serialization. Other independent packages can proceed concurrently after W01.

## Required acceptance details

### W02/W03: schema correctness
- Relative root ID, absolute root ID, nested resource, JSON Pointer escaping, self/shared/cyclic reference.
- `check`, coercion, explain, reject-unresolved, dynamic references and unevaluated properties/items all agree.
- Anthropic preserves both definition dictionaries only where upstream does; do not accidentally enable unsupported root composition keywords.
- Strict require stays strict; prefer fallback retains valid references; inline additions use the same path.
- Tool search does not index unused definitions or double-count a shared target; missing/remote/malformed refs cannot crash lookup. Existing malformed-URI codemode fallback remains.

### W04–W06: provider contracts
- Empty history versus populated history, zero/nonzero available tools, proxy escape flag false/default/true.
- Non-null assistant content independent of synthetic assistant-message insertion.
- Cache-key mode across Completions, Responses and Azure; retention none/short/long; public option/metadata/caller precedence.
- Only Responses changes its affinity header to `session-id`.
- Add the exact new cut-short-after-reply retry phrase to both classifiers; quota exclusions win and generic deterministic errors do not retry.
- Budget helper follows the upstream two-stage margin: proportional 15% input margin with a 4,096 floor; near the boundary apply the 32,768 fallback cap while preserving 4,096 spare. Handle explicit limits, tiny/zero windows and nonnegative output consistently.
- Catalogue correction lists exact affected models and provenance. Keep historical pinned shards/fixtures coherent; use the approved overlay or explicitly update target/hash evidence, not hand-edited undisclosed baseline drift.

### W07/W08: file and session behavior
- Untouched byte ranges preserve mixed line endings and BOM. Compute diff before writes, retain cancellation/atomicity behavior, update old tests expecting normalization.
- Streaming read can return a small slice of a file larger than 64 MiB without loading it whole. Existing custom adapters have a bounded compatibility path.
- Grep obtains context from rg; bound retained data independent of input size. Assert UTF-16, CR-only and cancellation behavior.
- Path-relative and `./` globs; symlinks; zero/negative limits normalize to at least one; exact-limit vs one-extra-result.
- Apostrophes, unmatched and escaped matching quotes, empty arguments, and positional substitution.
- Scalar-safe head/tail snippets at non-BMP boundaries.
- High → prompt → low → prompt → branch navigation → prompt → reopen preserves effective level. Respect model clamping, no redundant entry, no state-ahead-of-disk on failure.

### W09/W10: MCP and exit lifecycle
- Split every CR/LF/CRLF boundary across chunks; CR-only response dispatches before stream EOF.
- Keep existing JSON/event size limits and cancellation safeguards.
- Occupied prior callback port does not destroy a refreshable registered client. Fake token endpoint records refresh versus unnecessary registration.
- Fake refresh rotates credentials, pauses before persistence, then EOF/first termination signal arrives. Exit must join the owned operation or a documented safe cancellation/persistence protocol. No real credentials in tests.
- Idle timer shutdown wakes RPC without client input; command-triggered shutdown writes the command response first; repeated shutdown is idempotent with documented forced-exit policy.

### W11–W14: Durable and SQLite
- Reflect/parameterize every non-overridden HarnessSettings getter across both wrappers. Prove live getter changes and queued-input/restart outcomes.
- Corrupt stored *shape* can be valid JSON: `documents.record='{}'` passes SQL JSON syntax checks. First/later-batch corruption rejects whole call with no receipts or earlier entry; real semantic refusal still commits exactly the accepted prefix. Driver/commit/copy/base/delta failures covered separately.
- 256-entry prepared-statement LRU, hit promotion, eviction disposal and reprepare; no instrumentation dictionary that grows forever.
- Per-conversation compaction inheritance and full precedence; explicit false; clear/replace/unchanged; validation; manual compaction; overflow and pre-task background gating.
- ITx lookup uses committed read state, honors after-write prohibition and transaction lifetime. Existing third-party ITx implementors need release notes/default-interface compatibility decision.
- Thinking budgets/handshake timeouts flow through prepared request checkpoints and all host/native adapters; reopening keeps in-flight pinned options.
- Group optimization preserves order, IDs, document create/update/retire/recreate/copy, prefixes, lone-surrogate encoding and faults. Measure SQL statement count and delayed-executor latency; do not claim a speedup from code shape alone.

### W15/W25: what completion means
- Required suites execute, rather than only compile; failures and prerequisite skips are visible.
- Run existing provider/schema/tool/session/RPC/MCP/Durable/hosting tests relevant to selected fixes.
- Durable close/reopen and zero-retention modes, Windows/Ubuntu/macOS, and storage provider matrix remain separately visible.
- SQL Server requires its existing isolated service job, never the user's local database.
- Preserve historical captures. Add newly approved 1.5.0/candidate fixtures to a separate corpus with byte/source/hash provenance and normalization policy.
- Release validation refers to exact tagged SHA. A green unrelated main build or historical receipt is insufficient.
- Each audit ID is fixed with evidence, intentionally retained with rationale, or deferred to a named milestone. No silent omissions.

## Baseline scope decisions and migration notes

- W16 decomposes per applicability row before implementation. The terminal, Node ABI and Chord work are not prerequisites for small provider/tool correctness backports.
- Chord v2 host/jobs/links and pi-env are distinct large projects. Decompose W19–W22 before assigning implementation. Real-host tests replace hand-built substitutes only when foundation is ready.
- Optional new APIs stay experimental as appropriate. Per-conversation JSON fields and stream checkpoint fields can be additive without a SQL schema bump, but old runtimes ignore new semantics; document supported reopen/downgrade behavior.
- Existing 1.5.0 SQLite schema v2 migration is not repeated. W12/W14 should not need a storage version bump.
- Keep no-attribution defaults, retained device event contract, native fail-stop session-write policy, unsupported host backends and the existing Node 1.2.0 pin unless their explicit separate milestone changes them.
- No immediate upgrade to an unreleased version number. A released upstream target later requires a fresh frozen comparison and acceptance inventory.
