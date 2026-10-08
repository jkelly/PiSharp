# Remaining Completions lifecycle differences and next boundary

This is a read-only design against the actual `9f4649161d37c09456baca3fe1a20445c4d78814` candidate. Root ran 522/522 normal groups with zero warnings/errors and Node absent; parent reports independent acceptance of that bounded hook slice plus 32 independent checks. All 14 native capture cases completed, but all 14 strict compatibility comparisons still failed. Their counts are `12,18,18,18,12,18,14,78,78,51,12,19,21,19`, totaling **388 observations**, down from the immutable earlier 545. These are comparison records, not a count of distinct defects or a whole-provider acceptance claim.

No production, reference fixture, source capture, existing diagnostic, or earlier receipt is changed by this document. No new native execution was performed by its author.

## Evidence authority

The complete bounded receipts are the 14 JSON records in `artifacts/completions-lifecycle-hooks-strict-first.log`, SHA-256 `aa5b2ca27c461d275f438d06015e0f2cb86f26fb135c5d6e2efa599ad35667b4`. The summary at `artifacts/transports/completions-lifecycle-hooks-strict-first.json` has SHA-256 `93b26e127e01d3973a7b0711cc592e8e6b7705909eaf6311469f74d6d807f15c`; the normal gate log has SHA-256 `e9ece10de8c3fba40815712834362d8d920f158f288dab8299edf327b10c6cec`.

The unchanged whole-Pi reference is source `d86654abb8862e201933517d6f1fce9f88dd117f`, expected SHA-256 `829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`. Pinned OpenAI SDK 7.19.0 `core/streaming.mjs` has SHA-256 `1b8fa8338dc28913351993382187962082f0af42c5274a009060de3263579842` in the task-local oracle. Its actual `createAbortableSSESource` and `fromSSEResponse` implementation, plus Pi `utils/event-stream.ts`, govern the distinctions below. The shim's alternate iterable fallback is not a substitute for the exercised `body.getReader()` route.

## Exhaustive classification

Every one of the 388 records belongs to exactly one primary group. Classify request paths first, then the three explicitly unsupported Reader/consumption kinds; classify the two post-DONE fault cases and the consumer-return case as complete causal cascades; then distinguish direct error fields, abort checkpoint/snapshot rows, and the remaining exact lifecycle/alias paths. This leaves zero unclassified records.

| Primary group | Records | Actual interpretation |
| --- | ---: | --- |
| Request header observation and runtime identity | 126 | Nine header-array records in each case; payload/body/hash are otherwise equal. |
| Reader DTO, lock and consumption seams | 42 | Fourteen each of unsupported Reader DTO, unsupported reader lock, and nonidentical enqueue/read consumption. |
| Direct source error/native diagnostic contract | 39 | Six rows each in six ordinary fault cases plus three abort error rows. |
| Post-DONE cancellation/release-fault cascades | 132 | Sixty-six records in each of the two real cleanup-fault cases after removing headers/Reader seams. |
| Consumer-return producer ownership | 39 | Actual detached Pi producer versus canceled native producer, including return DTO and downstream shape differences. |
| Abort held-settlement and source snapshot | 4 | Three held-gate completion flags and one missing authoritative drained source snapshot. |
| EOF cleanup operation | 2 | Native asynchronous body disposal where source EOF requires no reader cancellation. |
| Acquisition mutable alias and fixture-owner cleanup | 3 | Two drained Start alias fields and one sequence record containing fixture-owned orphan cleanup. |
| Pre-body response-hook fault cleanup ordering | 1 | Native closes its owned response before terminal; source driver's unacquired-body cleanup occurs after source settlement. |
| **Total** | **388** | **No rows omitted or permanently excluded.** |

The kind totals independently agree: binary64 115; count 23; value 145; native-only-field 34; source-only-field 16; ordered-sequence 7; kind 4; missing-source-snapshot 1; Reader DTO/lock/consumption 14 each; unsupported return DTO 1.

All **115 binary64** rows are displaced member paths at five unmatched frame positions: emission/drain in both cleanup-fault cases and emission in consumer return. Each contributes 23 missing/extra numeric paths. They do not identify a surviving arithmetic or number-serialization kernel mismatch. Presence-path differences in those displaced frames likewise do not reopen the independently accepted missing/null/empty response-ID correction. This interpretation retains every original numeric/presence assertion.

| Case, in unchanged reference order | Strict records | Source/native emitted | Primary non-header/non-Reader cause |
| --- | ---: | --- | --- |
| missing-empty-named-and-thread-events | 12 | 7/7 | None in the compared provider values. |
| event-only-nonempty-name | 18 | 2/2 | Six error contract rows. |
| empty-data-event | 18 | 2/2 | Six error contract rows. |
| named-error-with-falsy-property | 18 | 2/2 | Six error contract rows. |
| exact-done-joins-cancel-and-ignores-tail | 12 | 5/5 | None in the compared provider values. |
| trailing-space-done-is-not-exact | 18 | 4/4 | Six error contract rows. |
| eof-later-bom-replacement-utf8-every-byte | 14 | 5/5 | Two EOF cleanup records. |
| falsy-error-false-cancel-rejects-after-done | 78 | 5/4 | Sixty-six post-DONE cleanup-fault records. |
| falsy-error-zero-release-fault-after-done | 78 | 5/4 | Sixty-six post-DONE cleanup-fault records. |
| falsy-error-negative-zero-consumer-returns | 51 | 5/2 | Thirty-nine reader-return/producer-ownership records; drain is 1/1. |
| falsy-error-empty-string | 12 | 5/5 | None in the compared provider values. |
| abort-pending-read-cancel-gate-rejects | 19 | 2/2 | Three error plus four abort authority records. |
| reader-acquisition-fault | 21 | 2/2 | Six error plus three alias/fixture cleanup records. |
| on-response-fault-before-reader | 19 | 1/1 | Six error plus one owned cleanup sequence record. |

### Representation and identity versus missing behavior

The source request has 12 sorted headers; the native observation has six. The source contains actual SDK telemetry identifying JavaScript, Node `v24.19.0`, SDK `7.19.0`, Windows/x64 and retry zero. Native code must not invent these identities. Native `ByteArrayContent` adds actual `content-length: 249`. The fixture's generic `string.Join(", ", header.Value)` observes the typed native User-Agent components as `PiSharp, Completions, offline, lifecycle, oracle`, whereas the source fetch observation has one space-separated value. That receipt is not a capture of an actual serialized native HTTP wire request. A header-aware product serialization boundary and genuine wire evidence are needed before calling this an actual wire-format defect. All 126 rows remain strict mismatches; runtime identity requires an explicit compatibility-boundary decision, not copying Node constants or silently ignoring headers.

The 39 direct error rows compare the source's SDK/provider error text with sanitized native text and the native `openAICompletionsFailure` field. For example, malformed SSE source errors say `Error reading response: malformed server-sent event JSON.` while native uses `Completions stream did not complete.` Named error data produces source `(no status code or body)`; acquire/header callback faults carry authored exception messages. Arbitrary callback or read exception text must not be copied into native canonical output to clear these assertions. A later admitted typed failure boundary must distinguish known public framing/provider diagnostics from private external exceptions and retain bounded metadata. This is unresolved API behavior, not a timing seam.

The 42 Reader records explicitly identify missing semantic operations: an acquired reader lock, typed read results, independent cancellation, release, and actual ReadableStream enqueue/prefetch. Native `Stream.DisposeAsync` followed by `HttpContent.Dispose` currently serves as a mapped test seam; it does not establish those SDK effects. For example, the exact-DONE source enqueues 113 bytes while native reads 102; the post-DONE fault sources enqueue 117 while native reads 104. Neither replacing native counts with source counts nor reading the ignored tail to match prefetch is justified.

### Cleanup and ownership facts

The SDK reader source starts cancellation at most once. On normal exact DONE it joins cancellation and releases the reader before Pi finalization. At natural EOF its `closed` state prevents cancellation and it releases the reader. On abort/error its source cleanup may release before the cancellation promise settles; actual source held-gate flags are already terminal/result/producer true in the abort case. `fromSSEResponse` suppresses cleanup failure after the exact sentinel. Native joins its actual owned cleanup, does not suppress the mapped cancellation/release faults, and emits Error instead of source Stop. These are genuine remaining lifecycle requirements whose policy has not been approved; the current candidate cannot claim them fixed by matching selected snapshots.

Acquisition failure leaves the injected fixture stream untransferred. The native receipt honestly records its owner closing it after settlement; it must not be confused with a transport-owned body cleanup. Pi queued frames share mutable message aliases, so the acquisition-fault source's drained Start contains later error fields while native's immutable Start remains pending. A representation-only rewrite would not implement this queue lifetime.

## Recommended next small production slice: explicit detached event reader

Select actual consumer/producer ownership before adding a complete SDK reader facade. Pi's returned `AssistantMessageEventStream` consumer iterator can return without canceling the separate asynchronous provider producer. This differs from breaking a direct SDK response-backed `Stream`, which aborts its request. The native `ChatRun.ReadEventsAsync` currently cancels in `finally` on early return to unblock its bounded channel. In the real consumer-return receipt this yields zero bytes/provider callbacks, two emitted frames, and Aborted; source continues through one actual callback to five frames and Stop. This is a concrete behavior difference with 39 cascading records.

Proposed named API, subject to root's profile decision:

```csharp
public ValueTask<ChatRun> StartWithDetachedReaderAsync(
    ChatRequest request, CancellationToken cancellationToken = default);
```

Keep existing `StartAsync`, `ReadEventsAsync`, run disposal, constructors and positional APIs unchanged. The named path chooses a private reader-abandonment policy when constructing the run. Early **reader** return establishes exactly one owned bounded discard drain and detaches that consumer; the real provider continues. Explicit caller cancellation, `Cancel()` and **run** disposal continue to cancel and join the actual producer. Terminal and Completion remain authoritative and consistent only after the existing owned transport cleanup completes. No cleanup suppression or early terminal is introduced.

Use the existing bounded channel. Detachment must hand off after the claimed consumer exits, drain already admitted buffered frames, release an already blocked write, and then discard subsequent progress without an unbounded queue or task per event. A single owned draining task is enough; its completion is joined by run disposal/settlement ownership. Do not hold a lock while awaiting provider callbacks, channel writes, reader exit or disposal. Resolve reader-return versus cancellation/run-disposal races once, with no second caller reader allowed. The run itself remains the lifetime owner when no consumer remains; caller disposal still provides deterministic cleanup for a provider that never finishes.

This named policy is an explicit domain boundary for the Pi event-stream lifetime. It is not a default change to the general native run and does not imply source Stop for current `StartAsync` abandonment. Root must select the public compatibility entry point before implementation; an unused option is not whole-provider closure. The complete 14 default receipts remain available, and the new product path must be exercised by actual HTTP/SSE fixtures rather than modifying source expectations or adding test-only final projections. Do not predict all 39 records disappearing: once the actual provider callback runs, raw negative-zero versus serialized callback-view representation can become newly observable.

Suggested fresh ownership: existing `src/PiSharp.AI/Streaming/ChatRun.cs`, one new focused transport test file, the existing lifecycle diagnostic only if root approves an explicit product-policy invocation mode, and a new contract document. Root owns registration, immutable commits and builds. Six meaningful groups cover default cancellation controls; actual HTTP reader return followed by continued callback/Stop; bounded full-channel handoff; caller cancellation while detached with held owned cleanup; concurrent reader return/run disposal; and callback or cleanup faults retaining actual Error plus joined terminal/Completion consistency. Every genuine strict case remains mandatory.

## Mandatory subsequent reader seam

A real independent reader lease is still required; consumer detachment does not supply it. A minimal proposed native interface is an acquired single-reader lease with `ValueTask<int> ReadAsync(Memory<byte>, CancellationToken)`, `ValueTask CancelAsync()` and `void Release()`, owned by the response scope. Its adapter must enforce one acquisition, one active read, valid inclusive returned counts, existing byte/line/event/cumulative limits, and owned copies of admitted read observations. Cancel is coalesced once; caller cancellation begins real cancellation rather than waiting for synchronous response disposal. Owned pending reads and cancellation are joined before final release/body disposal under the native joined policy. Cancel failure must not skip release or response cleanup; concurrent cleanup shares actual settlement. No diagnostic listener may fabricate Reader DTOs or lock state.

Ordinary cooperative Stream cancellation can use a private linked read token, but this is not a universal equivalent of WHATWG reader cancellation. An injected reader implementation must execute actual cancellation/release effects; unsupported hard interruption cannot be claimed from a Stream that ignores its token. The SDK's release-before-cancellation-settlement and post-DONE nonfatal policy remain explicit design decisions after this primitive, not hidden behind it. Prefetch/chunk queue behavior also requires real bounded implementation and fresh whole-provider evidence. A physical body disposal remains independently required even when a lease only releases its reader lock at EOF.

No current evidence approves early canonical settlement, unchecked cleanup suppression, fabricated runtime identity, source mutable-alias simulation, or permanent exclusions. This document freezes a next design and the exhaustive remaining observations; implementation awaits a fresh assigned branch and scope.
