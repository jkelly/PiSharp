# Independent runtime correctness review

Review date: 2026-11-08. Assignment: read-only review of the current gap implementation in `E:/AI/PiSharp/gap-implementation`, with this report as the only authored file. No source edits, commits, builds, or tests were performed by this reviewer. Central validation remains with I13. Model/tier described by the assignment: gpt-6.1-sol, xhigh, Ultrafast requested; effective tier not verified.

## Findings

### RUNTIME-1 — Per-request CLI transport disabled reasoning [P1, source fix inspected]

**Location:** `src/PiSharp.Cli/Durable/DurableModels.cs:155`, through `HostTransportAsync` at lines 132–143 and `src/PiSharp.Durable/Models/NativeDurableModels.cs:416`.

**Trigger and effect:** A hosted main request supplies `ThinkingBudgets` or `WebsocketConnectTimeoutMs` and requests a non-off reasoning level. The newly allocated `OwnedRequestTransport` originally implemented only `IChatTransport`. Native preparation detects thinking capabilities through `IThinkingLevelTransport`; when absent it substitutes `["off"]` and clamps the requested reasoning to off. Thus adding either new control silently removed reasoning from main-route requests. The existing summary route can impose its own fixed reasoning, which explains why a summary-only test would miss this.

**Concrete fix:** Forward `IThinkingLevelTransport.GetSupportedThinkingLevels` from the wrapped transport/owner. I17 implemented this during review; current lines 155–158 contain the forwarding implementation and were inspected. I17 reports new main-route and timeout-only coverage. No passing execution is claimed here.

### RUNTIME-2 — Web account routes dropped both new stream controls [P2, source fix inspected]

**Location:** `src/PiSharp.WebAgent/Routing/AccountRouter.cs:117` (`generation.Route(row.Entry, model)`), with the conditional at lines 119–124.

**Trigger and effect:** Call `WebDurableModels` with `ThinkingBudgets` or `WebsocketConnectTimeoutMs`. After stripping private account metadata, the router selects a cached generation route and only applies `MaxTokens`/`CacheRetention`. Neither new field reaches the generation's `LiveRouteRuntime`. Custom thinking budgets therefore use provider defaults, and the requested WebSocket handshake deadline is ignored. This is a complete production host path, not an unimplemented declaration. I17 confirmed the Web host is part of F03's scope.

**Concrete fix:** I21 now constructs an isolated per-request `LiveRouteRuntime`/`LazyModelTransport` when either field is supplied, forwarding both fields and retaining the account's credential source, error review, thinking capability, and stream-lifetime disposal. Requests without overrides retain shared cached routes. Inspected the updated `RouteAsync`, `Generation.Route`, and `OwnedRequestTransport`; authored tests cover main/summary budgets, no next-request leakage, timeout-only reasoning, and an actual fake stalled WebSocket handshake. Execution remains with I13.

### RUNTIME-3 — A first signal during catalogue draining returned the stale exit code [P2, source fix inspected]

**Location:** `src/PiSharp.Cli/Pi/PiCommand.cs:578–597`.

**Trigger and effect:** RPC input reaches EOF while background OAuth rotation is waiting for persistence; the RPC body computes its successful return code; the first SIGTERM then arrives during the new `finally { await refresh; }`. A return expression is evaluated before its finally block. The original change consequently captured zero before the signal, drained the write, and returned zero instead of 143. This violates the stated first-signal-during-cleanup behavior, even though persistence itself is correctly joined.

**Concrete fix:** Store the raw RPC result, join the refresh, then evaluate `signals.Exit(code)`. I10 implemented this during review; current lines 579, 587 and 595–597 perform that sequence. I10 reports an additional EOF → input disposed → SIGTERM → release-persistence regression. Execution remains owned by I13.

### RUNTIME-4 — Web payload wrappers orphaned the default HTTP handler [P2, source fix inspected]

**Location:** `src/PiSharp.WebAgent/Routing/AccountRouter.cs:236`; allocation in `src/PiSharp.Durable/Models/NativeDurableModels.cs:634`; ownership decisions in `src/PiSharp.AI/Providers/NativeProviderFactory.cs:138–140` and `src/PiSharp.ModelRuntime/Commands/LiveSessionSelection.cs:782`.

**Trigger and effect:** Use the default Web `RoutingOptions.ProviderHandler == null`. The new runtime factory calls `WrapProviderHandler(null)`, which creates a `SocketsHttpHandler` inside a non-null wrapper. Provider factories treat any non-null supplied handler as caller-owned (`disposeHandler: false`), and route disposal deliberately leaves it alone. Neither the Web runtime nor its generation retains/disposes the newly created wrapper. Closing routes/router therefore no longer deterministically closes their default HTTP pools; per-request routes for RUNTIME-2 would increase the number of unowned pools. The analogous CLI wrapper predates this diff, so this finding specifically covers the newly changed Web path.

**Concrete fix:** I21 added a generation-owned lazy `ProviderHandlerOwner`; its wrapper is disposed after active streams and routes settle, while a non-owning forwarding handler protects externally supplied `ProviderHandler`. Inspected the updated lease, capability forwarding, active-stream drain and route disposal.

A follow-up review found that the router's generation `GetOrAdd`/`Lazy.Value` admission was not atomic with its disposal snapshot, and a concurrent second dispose returned before the first close. I21 added a router lifecycle gate, an atomic start/snapshot boundary, and one retained close task for all callers. These corrections were also inspected. Authored checks cover owned/borrowed disposal, shared/isolated routes, active-stream drain, concurrent close, and an admitted pending generation; no passing test claim is made by this reviewer.

## Paths reviewed without another actionable finding

- SQLite transaction refusal versus rejection: traced watched synchronous/faulted calls, persisted record/revision decoding, copy materialization, prefix commit, commit failure, metadata and receipt order. The new decode wrapper marks genuine stored-data errors before the existing refusal filter; writes still occur outside that filter and reject the full SQL transaction. Unknown sources and incompatible requested copy policies remain semantic refusals. LRU hit promotion, 256-entry eviction, command disposal and shared transaction cache use are coherent with the serialized executor.
- `SessionHarnessSettings` and `AgentTeam.TeamSettings`: compared all current virtual getters with their inner settings, preserving their deliberate extension/compaction policy while forwarding `AfterFailedRun` and `RestartLimit` dynamically.
- Stored compaction/options/identity: checked whole-object replacement and clear, token validation, document inheritance, resolution, automatic/manual compaction call sites, checkpoint read/write, identity merge precedence, legacy/options overloads, request-scoped payload callbacks and compressed-body replacement. Compaction precedence matches frozen upstream `packages/durable/src/harness/compaction.ts:269–295`.
- MCP: traced CR/LF/CRLF parser state across reads and immediate CR dispatch; reviewed stored-client refresh before free-port browser fallback, cancellation, scope, host disposal and credential preservation. No additional confirmed regression in these changes.
- Lifecycle: reviewed idle admission, startup/command/run/post-origin gates, outside-context idle waiter, retained shutdown task, reader cancellation and join, retained catalogue refresh and repeated-signal behavior. RUNTIME-3 is the concrete late-signal defect found.
- File tools: reviewed normalized-to-original edit spans including CRLF and fuzzy grapheme mapping; bounded two-pass large-text reads and existing slice/continuation semantics; rg JSON context/limit handling, Unicode truncation, fd lookahead and ls usable-entry lookahead; native separated-output combined limits and joined process cleanup. No additional confirmed regression found in those changes.

## Review boundary

This is a static correctness review of an actively edited worktree, not test acceptance. Read the relevant I07/I09/I10/I10-contract/I11/I12/I17/I21 reports and actual source/tests/callers; used the frozen local upstream checkout for applicable semantics. Chord and Env architecture was explicitly excluded. No transient missing declarations or in-flight compile failures are reported as design findings. Validation receipts and remaining owner fixes must be checked before acceptance.
