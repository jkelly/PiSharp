# Completions source-run residual census

This is a read-only analysis of the exact root-executed candidate `211275673f3a72c10d953a89ebcf978a8238c1f1`. Root reported a clean locked native build, zero warnings/errors, Node absent and 649/649 native groups passing across the eight reports: 104/122/110/58/54/99/33/69. The separate full lifecycle comparison ran all 14 immutable cases: zero passed, 14 failed, **433 comparison observations**. These are comparison rows, not 433 independently established defects.

The authority remains public Pi `d86654abb8862e201933517d6f1fce9f88dd117f`, the official locked OpenAI SDK 7.19.0, and the unchanged lifecycle expected SHA-256 `829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`. No runtime, capture, Git action, production edit, assertion change, golden change or comparison filter was performed for this analysis.

| Receipt | Bytes | SHA-256 |
|---|---:|---|
| `artifacts/source-run-full-strict.log` | 1,556,859 | `3c48ba5a97ea1e6e0fca7bbfed80c73b4c931046054a1313d77f86762fdbae29` |
| `artifacts/transports/source-run-full-strict-results.json` | 4,851 | `28f17978ef3745e0be76127c80e244838a5168649f54d5e3928cf468a3196509` |
| Prior `../PiSharp-completions-reader-lifecycle-work/artifacts/completions-reader-first-strict.log` | 947,376 | `4e175d681336c38ffebd77768a4c448054a2c1df6b74e29f49e5640ec6115c1c` |

Each current case has two explicitly independent actual HTTP invocations. The top-level `comparison` belongs to the retained canonical execution. `publicSourceExecution.comparison`, whose paths begin `/publicSourceExecution`, belongs to the separately owned public-source execution. Its source task, reader, cleanup and canonical completion all belong to that second invocation. The census does not merge their ownership or discard either execution.

## Complete count reconciliation

| Exact case ID | Canonical | Public source | Total |
|---|---:|---:|---:|
| missing-empty-named-and-thread-events | 12 | 27 | 39 |
| event-only-nonempty-name | 18 | 14 | 32 |
| empty-data-event | 18 | 14 | 32 |
| named-error-with-falsy-property | 18 | 14 | 32 |
| exact-done-joins-cancel-and-ignores-tail | 12 | 16 | 28 |
| trailing-space-done-is-not-exact | 18 | 21 | 39 |
| eof-later-bom-replacement-utf8-every-byte | 12 | 16 | 28 |
| falsy-error-false-cancel-rejects-after-done | 12 | 16 | 28 |
| falsy-error-zero-release-fault-after-done | 12 | 16 | 28 |
| falsy-error-negative-zero-consumer-returns | 14 | 13 | 27 |
| falsy-error-empty-string | 12 | 16 | 28 |
| abort-pending-read-cancel-gate-rejects | 19 | 12 | 31 |
| reader-acquisition-fault | 20 | 12 | 32 |
| on-response-fault-before-reader | 17 | 12 | 29 |
| **All 14** | **214** | **219** | **433** |

The independent Kind totals also reconcile to 433: `count` 42; `different-consumption-seam` 28; `kind` 3; `missing-source-snapshot` 1; `native-only-field` 36; `ordered-sequence` 27; `source-only-field` 1; `unsupported-reader-dto` 28; `unsupported-return-dto` 1; `value` 266. No binary64 mismatch is reported in this run.

The following disjoint path families account for every row. “Canonical” means no `/publicSourceExecution` prefix; the public family prefixes are literal.

| Causal family and exact path coverage | Canonical | Public source | Total |
|---|---:|---:|---:|
| Request header array shape/order: `/fetchRequests/0/headers` and indices `/2/0`, `/2/1`, `/3/0`, `/3/1`, `/4/0`, `/4/1`, `/5/0`, `/5/1` | 126 | 126 | 252 |
| Canonical known/public error text and native failure fields: error-terminal `errorMessage` / `openAICompletionsFailure` in emission/drain/final snapshots | 39 | 0 | 39 |
| Real native reader operation versus source WHATWG DTO ABI: `/readerLedger` | 14 | 14 | 28 |
| Source enqueue provenance versus native actual bytes read: `/cleanup/sourceBoundary/bytesEnqueued` | 14 | 14 | 28 |
| Canonical held-abort checkpoint: `/lifecycle/cancel-gate-checkpoint/0/{terminalPushed,resultSettled,producerEnded}` | 3 | 0 | 3 |
| Canonical queued acquisition-fault Start alias: `/drainedFrames/0/value/partial/{stopReason,errorMessage}` | 2 | 0 | 2 |
| Canonical missing abort terminal source sidecar: `/drainedFrames/1` | 1 | 0 | 1 |
| Canonical outer return ABI: `/consumer/returnResult` | 1 | 0 | 1 |
| Retained hook JSON spelling: `/providerEvents/0/value/error` negative zero | 1 | 1 | 2 |
| **New canonical actual publication/delivery ordering**: `/lifecycle/milestones` | **13** | 0 | **13** |
| Public actual live-alias drain timing: `/publicSourceExecution/drainedFrames/...` (fully expanded below) | 0 | 50 | 50 |
| Public missing timestamped producer-publication seam: `/publicSourceExecution/lifecycle/milestones` | 0 | 14 | 14 |
| **Total** | **214** | **219** | **433** |

## What actually regressed from the 201-row canonical baseline

Matching every prior canonical row by `(caseId, Kind, Path)` shows **201 retained rows, zero removed rows, and 13 added rows**. All added rows are `ordered-sequence` at `/lifecycle/milestones`, one in each case except `on-response-fault-before-reader`. Thus 433 is not comparable with the earlier 201 as a single uniform denominator: 219 rows are newly measured public-source scope, while 13 are genuine new observable ordering differences in the old canonical scope.

Every retained row has the same expected value. Only 14 retained actual-value strings changed: the request header index `/fetchRequests/0/headers/4/1` now uses actual typed `Headers.UserAgent.ToString()` instead of comma-joining parsed product tokens. This corrects the native diagnostic value, but the array position still differs because the header arrays have different shapes. Its retained mismatch count does not imply that the previous incorrect comma spelling should be restored.

The ordering regression is concrete. In `missing-empty-named-and-thread-events`, source order begins `hook:onResponse,emit:start,acquire,acquired`; the current canonical receipt begins `hook:onResponse,acquire,acquired` and observes all five provider callbacks and reader cleanup before delivering `emit:start`. In the exact-DONE case, actual reader cancellation begins before canonical Start/text-start/text-delta delivery. In acquisition-fault and abort cases, canonical Start is delivered after acquisition begins. Header-hook failure remains the exception: it acquires no reader and publishes no Start, so it adds no new ordering row.

`CompletionsRun.cs:58` starts the async producer during construction. Its bounded write at line 119 can complete synchronously while the queue has capacity; `CompletionsHttpSseTransport.cs:136` and line 257 can also complete their decoder advances synchronously. The canonical consumer at `CompletionsRun.cs:84` is attached after construction. The original observer at `CompletionsLifecycleDifferentialTests.cs:427` therefore records actual canonical delivery after the producer has advanced. This is not evidence that the producer internally publishes Start after body acquisition; its source ledger captures Start before advancing. It is nevertheless an actual default transport observation regression from the old pull boundary and must be corrected or explicitly resolved at the real public boundary, not relabeled into a pass.

The pinned Pi code awaits onPayload at `openai-completions.ts:361`, request/response at lines 370–378, then pushes Start at line 379 before the `for await` SDK loop at line 553 and its awaited provider callback at line 554. SDK `streaming.mjs:344` acquires the real reader; its iterator and Pi's `event-stream.ts` are async generators with actual promise boundaries. Completed .NET awaits do not automatically provide the same scheduling boundary. An arbitrary sleep, a fabricated event label, or an unqualified claim that `Task.Yield` is exactly a JavaScript microtask would not establish compatibility.

## The added 219 public-source observations

Public-source comparisons report no differences in the complete emission values, complete final values, numeric-bit controls, source own-undefined final paths, held-abort source state, source cleanup cancellation/release disposition or the actual return DTO in these 14 cases. This is narrowly supported by the actual recorded comparisons. It does not establish global lifecycle parity or erase the retained canonical differences.

The 50 actual drain/alias differences are new measurements, not 50 changes to the old native immutable snapshot contract. The live source handle is real and shared with `SourceResult`; its consumer can observe a later producer revision than the qualified source consumer did. In the first case, native source Start already sees `ABC` and Stop, whereas the source captured Start has empty content/Pending and early text frames have `A` or `AB`. Malformed/named-error Start sees the actual later Error. Seven Start rows see a later response-id own-undefined path. These observations reveal a scheduling/publication issue; making the handle immutable or copying the final message into the source oracle would hide it rather than fix it.

Every one of the 50 paths is covered by this multiplicity table. Paths are relative to `/publicSourceExecution/drainedFrames`.

| Exact suffix | Count |
|---|---:|
| `/0/ownUndefinedPaths` | 7 |
| `/0/value/partial/content` | 7 |
| `/0/value/partial/errorMessage` | 4 |
| `/0/value/partial/rawStopReason` | 7 |
| `/0/value/partial/stopReason` | 10 |
| `/1/value/partial/content/0/text` | 1 |
| `/1/value/partial/errorMessage` | 1 |
| `/1/value/partial/rawStopReason` | 1 |
| `/1/value/partial/stopReason` | 2 |
| `/2/value/partial/content/0/text` | 1 |
| `/2/value/partial/errorMessage` | 1 |
| `/2/value/partial/rawStopReason` | 1 |
| `/2/value/partial/stopReason` | 2 |
| `/3/value/partial/content/0/text` | 1 |
| `/3/value/partial/rawStopReason` | 1 |
| `/3/value/partial/stopReason` | 1 |
| `/4/value/partial/rawStopReason` | 1 |
| `/4/value/partial/stopReason` | 1 |
| **Total** | **50** |

Per case these are 15/2/2/2/4/9/4/4/4/0/4/0/0/0 in the immutable case order. Their 50 rows plus 126 headers, 14 timeline, 14 reader-ABI, 14 enqueue-provenance and one hook spelling row are exactly 219.

The 14 public timeline differences include an absent real producer-publication callback seam. The product retains actual `SourceEmissions`, but the diagnostic observes `source:read:<type>` at line 395 and does not fabricate these into `emit:<type>`. `Milestones` at line 465 consequently includes actual hooks/reader/return/result observations and no invented source emit timestamp. Adding a real bounded observer at the publication operation is dependency-ready; copying or retrospectively inserting labels from the ledger is not a behavior correction. The actual Start/await scheduling must still be repaired separately and retested.

## Runtime representation and unresolved reader input semantics

The 252 header rows are two sets of 14 × 9 array comparisons. The source sends seven SDK-generated runtime headers: x-stainless-arch, lang, os, package-version, retry-count, runtime and runtime-version. The actual native request has its computed Content-Length instead. The five common header values are not a credential or provider-request body failure. Header insertion shifts create eight value comparisons and one array count per case. This is a real host/SDK representation gap in the exact corpus, not 252 distinct request defects. No Node/js/OS/package identity may be fabricated and no actual Content-Length removed to erase it. A supported explicit source-facing metadata contract and fresh genuine qualification are still needed; these rows remain mandatory.

The 28 reader-ABI rows are explicitly retained missing seams: source `read()` returns a WHATWG `{value,done}` DTO, while the actual native executor owns a Memory/count operation. Real Cancel, Release and physical joins now execute, but a source read DTO would need to be returned by a real bounded operation over that same executor, with actual owned bytes, EOF/absence, cancellation and single-reader authority. Renaming a count receipt into a WHATWG object would not implement that API.

The 28 enqueue rows also distinguish quantities. Native read counts happen to equal source bytes-enqueued in eight cases and differ in six; the unsupported provenance marker is deliberately still reported in all 14 for each execution. The actual unequal controls are:

| Case | Source bytes enqueued | Canonical bytes read | Public bytes read |
|---|---:|---:|---:|
| empty-data-event | 9 | 8 | 8 |
| exact-done-joins-cancel-and-ignores-tail | 113 | 102 | 102 |
| falsy-error-false-cancel-rejects-after-done | 117 | 104 | 104 |
| falsy-error-zero-release-fault-after-done | 117 | 104 | 104 |
| reader-acquisition-fault | 13 | 0 | 0 |
| on-response-fault-before-reader | 13 | 0 | 0 |

The source fixture's ReadableStream can enqueue before reader acquisition, while the native injected physical Stream reads on demand. A future prefetched executor must actually own bytes and bounded queue work if justified by source semantics. It must preserve no native body acquisition after failed header observation and independently join every admitted operation. Equal byte integers in eight cases do not prove input-seam equivalence, and changing counts or permanently excluding the six unequal cases is not admissible.

The two hook spelling rows compare source JSON-serialized `error:0` with retained native `error:-0`. Binary64 sidecars match; this is not a binary64 arithmetic regression. The accepted production serialization kernel can supply an actual separately named hook DTO JSON view, with original raw numbers and numeric sidecars preserved alongside it. Applying normalization only inside the comparator would weaken the boundary and is not a fix.

The old canonical error/abort/return families remain visible because the default native API deliberately grants joined canonical authority and native failure metadata, while the new source API separately exposes semantic result, public failure data and the outer return operation. Both APIs must retain their actual contracts. An additional source API match cannot excuse the canonical 39 error projections, three held-abort checkpoint rows, two alias rows, missing terminal sidecar or return ABI. Their source-compatible boundary remains mandatory, with no successful native terminal preceding physical cleanup.

All public-source receipts show request/response disposed at their own joined canonical settlement. Typed `Cleanup.Succeeded` is false in malformed/named/trailing-JSON, acquisition-fault and aborted-cancel-fault cases, and true in the other eight cases. The current implementation retains failed decoder operations inside its cleanup outcome; this is native typed failure disposition, not an assertion that each corresponding source physical cleanup failed. The strict ledger does not compare this boolean with a source equivalent, so it adds no row to this census.

## Next dependency-ready corrections

The first production priority is the **real startup publication boundary**, addressing the new 13 canonical regressions before further ABI growth. A narrow proposal is an awaited publication sink for the first actual StreamStarted frame, selected by the actual consumer/view and invoked by the same producer before requesting the first body chunk. Canonical delivery needs a real acknowledged handoff before acquisition; a public source sink may acknowledge its actual bounded enqueue. This is consumer backpressure rather than a sourceView branch that changes abort/DONE/EOF or reader Cancel/Release policy. The shared owner must retain and cancel/join the handoff, and startup must never depend on an unowned event task.

Proposed first scope: existing `CompletionsRun.cs` and `CompletionsHttpSseTransport.cs`, plus one new startup-boundary test file and contract update. Keep constructors and positional options source-compatible. Do not change State, parsed transport, goldens, comparison order or canonical cleanup policy. Required actual controls are synchronous factory/handler/body with Start delivered before first acquire; held awaited header hook then Start/acquire; failed hook with no Start/acquire; actual acquisition fault after Start; exact DONE and EOF; early abort and held cleanup; canonical early return with no orphan handoff; public return and result-only source consumption without a new startup hang. The result-only control is essential: a universal external-reader ACK that blocks a healthy source producer merely because no event iterator was opened would introduce an unrelated source regression. Root must approve the precise sink ownership before edits, then execute all seven source groups, the entire native gate and both unfiltered 14-case ledgers. No reduction is predicted.

After that startup correction, add actual producer-publication observation to expose the 14 public timelines, and qualify the exact reader/await/consumer scheduling that causes the 50 live-alias drain rows. Source-compatible async-turn policy needs actual controlled whole-provider captures; a sleep or a generic scheduling hint cannot substitute for proof. Next come genuine same-executor read DTO/byte queue semantics and actual serialized hook views. Runtime-header policy needs its own truthful source-facing contract. These remain required closure work; none is a permanent parity exclusion.

Native safety/ownership acceptance and complete source lifecycle acceptance are separate outcomes. The exact candidate passed the native gate, but all 14 strict source comparisons still fail. This census accounts for every observation and proposes the next correction; it does not accept the remaining lifecycle surface.
