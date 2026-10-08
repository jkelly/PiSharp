# Completions response-reader ownership and source lifecycle

This candidate starts from accepted `e891` and implements a reader lease in the actual default Completions HTTP path. It is not an unused reader utility or a new opt-in framing profile. Existing SDK 7.19 framing, awaited payload/response/provider hooks, source-ID presence and detached-consumer operation remain connected to the same transport. Root will compose this default transport with its current accepted `1b` CLI, which already supports all three provider APIs; the older branch's CLI files do not describe current product integration.

## Actual evidence and unresolved authority

Root's accepted detached-reader candidate passed 530 native groups and 35 independent checks. Its complete 14 strict case counts are `12,18,18,18,12,18,14,78,78,14,12,19,21,19`: 351 observations, all 14 cases still failing. The actual return-after-start producer now continues to Stop; that case retains the nine header records, three Reader/consumption records, a raw callback negative-zero versus serialized-zero value, and a missing return DTO. The 92 binary64 records all follow the two displaced cleanup-fault emission/drain frames. No new reader result is claimed before execution.

Source authority remains Pi `d86654abb8862e201933517d6f1fce9f88dd117f`, expected `829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`, and locked SDK 7.19.0 `core/streaming.mjs` `1b8fa8338dc28913351993382187962082f0af42c5274a009060de3263579842`. `createAbortableSSESource` actually acquires a reader once, invokes read, starts cancellation at most once, and releases its reader. Natural EOF sets `closed` and omits cancellation. Exact DONE returns the nested iterator, joining cancellation before normal finalization. Aborted/error cleanup can release before its cancellation promise settles. `fromSSEResponse` suppresses cleanup errors after the exact sentinel. Pi's outer event consumer can return without canceling its independent provider producer. Each layer is a distinct lifetime.

The source fixture's ReadableStream enqueues/prefetches through its own body queue; the SDK reader itself does not invent a parallel prefetch loop. A new native queue is not justified by the 113 enqueued versus 102 consumed bytes in the DONE case. Native input reads must continue to report actual bytes and stop consuming ignored tail. Reader operations and actual physical Stream disposal will no longer be conflated in the new diagnostic.

## Approved small default-path slice

The public executor seam is typed and invocation-owned:

```csharp
public interface ICompletionsResponseBodyReader
{
    ValueTask<int> ReadAsync(Memory<byte> destination,
        CancellationToken cancellationToken = default);
    ValueTask CancelAsync();
    void Release();
}

public delegate ValueTask<ICompletionsResponseBodyReader>
    CompletionsResponseBodyReaderFactory(Stream body,
        CancellationToken cancellationToken);
```

`CompletionsResponseBodyReader.FromStream(body)` supplies the production default executor. It borrows the physical stream, admits one active read, owns its private read-cancellation source and read/cancellation settlement, coalesces cancellation, and releases its lease only after admitted reads/cancellation settle. Cancellation actually cancels that reader's private read token, so a cooperating underlying HTTP stream interrupts its pending read. It does not merely publish a cancel observation or relabel body disposal. A stream that ignores cancellation remains owned and awaited; there is no fabricated EOF, forced return or resource abandonment. Release closes lease authority and its cancellation resource without disposing the separately owned physical body.

`CompletionsHttpSseOptions.BodyReaderFactory` is a nonpositional optional executor substitution for genuine injected body implementations. The default factory is always used when this is absent. Existing constructor delegates, target-typed calls, options deconstruction and framing limits remain intact. The factory receives the response-owned physical stream after the real awaited header callback and Start. The transport checks each executor result against the decoder's admitted destination length and enforces single read/release authority before handing bytes to the existing bounded decoder. Raw UTF-8 decoding, line/event/data/depth/Unicode/JSON limits remain the production decoder/mapper's responsibility; no second byte queue or JSON simulation is introduced.

The shared HTTP transport changes only through an internal `AcquireBodyAsync` method reused by its existing general read path and Completions. It keeps exactly one physical acquisition and owns asynchronous body disposal followed by response disposal. The general transport's external contract is preserved. The Completions invocation owns the reader executor: on early termination it joins actual cancellation, then releases; at observed physical EOF it releases without invoking cancellation. It still joins reader cleanup and physical body/response/request cleanup before native terminal/Completion. A reader fault does not skip another owned cleanup operation or expose arbitrary exception strings. Physical ownership failures retain the existing fatal policy; the separately approved exact-DONE reader-operation rule below is implemented.

Actual differential fixtures substitute a real reader executor with independently gated/faulting cancel and release operations while using the actual byte-reading body. They record real operations, real admitted bytes, actual lock/lease state and real cancellation settlements. The final lock comparison now uses actual lease authority. Reader cancellation receipts replace the old physical-disposal mapping; complete native traces retain physical cleanup under separate owner operation names. A native `Memory<byte>` read and `ValueTask` cancellation are not WHATWG read/cancel DTOs; that unsupported ABI row remains explicit alongside the actual native reader ledger. Do not supply empty JavaScript argument arrays, invented typed-array/undefined results, source enqueue counts or Node telemetry to clear them.

Owned paths are the new `CompletionsResponseBodyReader.cs`, actual default `CompletionsHttpSseTransport.cs`, the internal physical-acquisition reuse in `HttpSseTransport.cs`, a new focused `CompletionsResponseBodyReaderTests.cs`, the existing complete lifecycle diagnostic, and this design document. Root owns registration, builds, immutable commits and current CLI composition. `CompletionsResponseBodyReaderTests.Cases()` supplies eight groups covering actual default cancellation/physical ownership, EOF versus DONE and ignored tail, single active read/release authority and late calls, cooperative cancellation/shared settlement, throwing cancellation callbacks/noncooperative reads, returned-count bounds, exact-DONE real cancel/release faults with held physical cleanup, and pre-DONE/EOF/physical/combined fatal controls. Existing normal cases plus the complete all-14 strict provider command remain required.

## Approved default policy: narrowly admitted post-DONE reader faults

Root explicitly approved this narrow default policy before implementation. After the exact DONE sentinel, the SDK treats reader cancel/release errors as nonfatal. This candidate retains those **reader-operation** failures in a bounded internal flags diagnostic while still independently joining admitted reads, cancellation, release and physical body/response disposal. If that physical cleanup succeeds, the mapper finalizes its actual admitted finish reason and terminal/Completion together. If physical cleanup fails, native Error remains authoritative. Missing finish reason, parser/provider/hook faults, pre-DONE cancellation, EOF release errors and unrelated ownership faults receive no successful-output permission from this rule.

This does not suppress Stream.DisposeAsync or HttpContent.Dispose failures: those are physical ownership failures. Moving the test's cancel/release faults to actual separate executor operations makes the distinction reviewable. No reader fault text or diagnostic metadata is copied into a canonical message to make a source assertion pass. Concurrent invocation disposal still shares actual settlement, and combined reader/physical faults are tested as fatal. The policy is wired into the default Completions transport that root will compose with current session/RPC/CLI integration. Existing direct native physical-cleanup controls must keep passing.

## Source-public abort and joined canonical settlement

The held source abort case exposes terminal/result/producer true while reader cancellation is still unsettled. Native canonical `ChatRun.Completion` currently awaits owned cleanup and remains false at that point. Both cannot represent the same promise without changing one contract. The next boundary must be explicit: a source-compatible public producer result can denote completion of provider semantics while an owned cleanup-completion task denotes completed resource lifetime. Its public dispose still joins cleanup; session persistence, model/tool continuation and CLI durable acknowledgment must use the joined canonical result. An early source-visible result must not grant permission to execute tools or write a successful durable transcript.

No such additional public producer-result boundary or early-abort policy is implemented here. Before implementing it, root must approve the actual public API and integration, ensure source events/result refer to the same authoritative source-facing value, retain separate canonical terminal/Completion consistency, and qualify the whole 14-case capture with both actual lifetimes recorded. A late cleanup fault cannot be silently lost, rewritten into an already delivered canonical success, or hidden by comparing only a selected source snapshot. Source mutable queue aliases, private exception normalization, reader DTO ABI and runtime identity likewise need admitted production boundaries and genuine whole-provider evidence, not test-only projections or permanent exclusions.

**Author execution: NOT RUN.** All 14 strict requirements and unchanged source fixtures remain mandatory. Root will execute and report actual reductions separately from capture/ownership success.
