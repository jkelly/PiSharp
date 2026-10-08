# Detached chat event-reader lifetime

`ChatClient.StartWithDetachedReaderAsync(request, cancellationToken = default)` starts an explicit native run whose event consumer may return while its provider continues. The returned `ChatRun` retains lifetime ownership. Disposing the run or explicitly canceling it still cancels and joins the actual provider, transport cleanup and owned drain. Existing `StartAsync`, `StartWithAbortSettlementAsync`, constructors and default reader-return behavior stay available with their existing policies.

The operation is grounded in pinned Pi `d86654abb8862e201933517d6f1fce9f88dd117f`: `utils/event-stream.ts` supplies an event consumer iterator separately from the asynchronous producer in `api/openai-completions.ts`. Returning that outer event iterator does not cancel the provider producer. Returning a direct OpenAI SDK response-backed Stream has different request-abort behavior. This named native operation targets the outer Pi event-consumer lifetime; it does not claim those two source layers are interchangeable.

## Ownership and admission

One retained task waits for a caller-reader handoff. Once `ReadEventsAsync` has exited its inner channel enumeration, ordinary reader return activates that task as the sole replacement channel reader. It discards already admitted progress and subsequent progress using the existing bounded channel, releases a writer blocked by backpressure, and keeps the real reducer/provider running. There is no additional event queue, second caller reader, task per frame or external drain callback.

If the provider finishes while the caller remains attached, the dormant task settles without consuming the caller's buffered progress. Normal progress and the authoritative terminal remain available to that original reader. A second caller enumeration is still rejected even after detachment.

The producer closes its channel before joining an activated drain, then publishes the authoritative terminal and `Completion`. Shared run disposal joins both producer and retained drain before releasing the cancellation source. No drain is left running after either settlement/disposal. The default operation uses no replacement reader task and preserves its prior cancellation on early reader return.

Caller cancellation and `Cancel()` retain authority after detachment. An explicitly canceled `ReadEventsAsync` token cancels the producer; in this named mode the reader's canceled operation joins producer cleanup before settling. A read token's registration belongs to its live reader enumeration and ends when that reader returns. Cancel the caller/run token or dispose the run to cancel a producer after its reader has detached. Existing sanitized cancellation-callback diagnostics remain in `CleanupFailure`; an external callback exception cannot bypass owned cleanup. Cleanup that ignores cancellation remains awaited until its actual completion.

The caller still owns an unfinished run after detaching. If a provider never finishes, explicit run disposal is the deterministic cancellation/join operation. Detachment does not invent a timeout, force-close arbitrary resources or change a provider's terminal authority.

## Validation surfaces

`DetachedChatReaderTests.Cases()` supplies eight meaningful groups:

1. Existing default reader return cancels the provider and settles Aborted.
2. Actual HTTP/SSE Completions continues after reader return, preserves complete raw DTOs/Unicode output, and joins held body cleanup before terminal/Completion.
3. A capacity-one full channel and blocked write hand off to the owned drain, produce all 1,024 actual deltas, and retain the complete canonical result.
4. Explicit reader-token cancellation joins held cleanup before its pending read and shared disposal settle.
5. Caller cancellation after detachment joins concurrent disposal and performs physical cleanup once.
6. A throwing cancellation callback preserves the existing sanitized diagnostic while noncooperative cleanup remains held and joined.
7. Detachment rejects a second caller reader; an already completed run retains its buffered Start/terminal for its original reader.
8. Actual awaited provider callback failure and post-DONE body cleanup failure retain sanitized Error and terminal/Completion consistency after detachment.

The fresh branch's complete 14-case lifecycle diagnostic selects this real product operation solely for the source fixture whose consumer mode is `return-after-start`. Each receipt names `nativeReaderPolicy`; all original case IDs/order, source fixtures/pins, complete comparisons and remaining diagnostics stay present. Other cases continue through `StartAsync`. Original `9f4649161d37c09456baca3fe1a20445c4d78814` receipts remain immutable evidence of the default policy, including its 39 consumer-return mismatch observations. Root registers the eight groups and runs normal and strict commands serially.

**Author execution: NOT RUN at freeze.** No new mismatch count or whole-provider closure is claimed. The independently accepted hook candidate previously passed 522 normal groups; its all-14 strict comparison still failed with 388 records. Actual independent Reader Cancel/Release semantics, source enqueue/prefetch and release ordering, post-DONE nonfatal cleanup policy, early source abort settlement, source mutable aliases/error contracts, runtime header identity and full provider ABI remain mandatory open work. This slice preserves joined cleanup and does not suppress faults or publish early canonical terminals to match those rows.
