# Completions bounded body read results

This implements a provider-local boundary within original P2-03 streaming/projection and P2-04 transport ownership. Related P2-08/09/10 requirements remain mandatory. It does not close those packages or a phase gate. The original clause is unchanged at `docs/plans/02-provider-layer.md:17`; all 79 original scope records and eight phase gates remain.

## Actual result operation and production use

`CompletionsBodyReader` wraps an invocation-owned `ICompletionsResponseBodyReader` executor. Its public `ReadAsync` returns a real `CompletionsBodyReadResult` from that executor's admitted operation. Each nonterminal result owns an immutable byte array, with `Done:false`. A closed reader has `Done:true` and no value in its source view. `Snapshot` retains own-undefined presence at `/value`; data results represent actual byte indices as numeric object keys, matching the pinned SDK's JSON view of a Uint8Array. No JavaScript object, argument receipt, enqueue count or external operation is invented.

The default Completions HTTP/SSE path constructs this same reader. `CompletionsReaderStream` adapts its returned bytes to the existing decoder's count operation. It neither calls a second executor nor creates a second read or byte queue. Optional nonpositional `CompletionsHttpSseOptions.OnBodyRead` receives the exact result supplied to that decoder, after the executor operation has settled and outside the reader lock. The callback receives owned data, no stream/lease. A synchronous observer fault follows existing producer failure and physical cleanup; private exception text does not become a canonical diagnostic. A caller retaining callback values owns that retention; the library retains no unbounded result ledger.

Existing factory delegates, positional options/deconstruction, `ICompletionsResponseBodyReader`, provider hooks and shared Contracts/Agent/session APIs are unchanged. The default count executor still performs real cancellation and lease release over the borrowed physical body. The HTTP response remains the separate physical body owner.

## Bounds and authority

The configured read maximum remains inclusive 1 through 65,536 bytes, matching the existing decoder limit. A private reusable buffer admits one executor read; validated counts are copied into an immutable result before admission. Negative and over-destination counts fail before observer/decoder publication. Zero from an actual executor read records physical EOF. Repeated reads of that closed reader return the absent-value result without new executor work. A released reader refuses further read/cancel/release authority.

Byte snapshots have at most 65,536 numeric members and are below 1 MiB. They are a dedicated byte-only JSON representation, not an invocation of the general floating-point/string/object projection with larger limits. Byte values are exactly 0 through 255; no arbitrary numeric or source message projection bypasses its existing kernel. The configured decoder, line/event, JSON-depth, cumulative data, source value, source publication and diagnostic budgets are unchanged.

One active read is admitted. Cancel calls coalesce onto one retained settlement. Cancellation joins the real admitted read even when the executor throws during cancellation or ignores cancellation. Release requires the admitted read and ordinary cancellation to have settled. The existing internal interrupted-release path retains its distinct executor release and eventual cancellation/physical join. An executor cannot join its own cancellation; no lock is held during executor work, callbacks or awaited settlement.

## Cancellation closure and physical EOF

A completed read interrupted by its supplied token or an actual admitted reader cancellation returns a closed, absent-value DTO. An unrelated OperationCanceledException remains a failed operation. This source result does **not** mark physical EOF. `ReachedEof` is true only after an actual executor count of zero. The transport therefore still starts and joins its real cancellation operation after cancellation closure; it cannot skip cancel by treating that DTO as a natural EOF.

Pre-canceled read tokens still reject admission. After cancellation has closed the reader, another uncanceled read can observe its closed DTO without extra input. A noncooperative executor remains awaited; its actual returned bytes are retained rather than rewritten or abandoned. Source result closure grants no successful canonical/provider/tool/durable authority. The decoder's existing token checks and the run's existing canonical cleanup barrier remain active.

## Meaningful validation and complete remaining boundary

Eight focused groups cover immutable actual bytes, absent/repeated EOF, the complete genuine every-byte EOF result sequence including numeric sidecars, inclusive bounds and invalid counts, active/late admission and self-join, cooperative/noncooperative/faulted cancellation, cancellation versus physical EOF and unrelated failure, actual default HTTP/SSE DONE/EOF/ignored-tail/held cleanup, and observer/count/physical failure sanitization. Existing transport pacing, cancellation, abort, source-result, reader effect and physical owner groups remain unchanged.

The complete 14-case diagnostic records actual read results from **both independent HTTP invocations** and compares every result value, presence path and numeric sidecar to all corresponding source reader results. These are additional assertions. Original full request/event/result/error/lifecycle comparisons, complete reader-ledger assertions, all fourteen input cases, source expected/manifest/lock pins, diagnostic/operation bounds and deadlines remain. The old reader-ledger diagnostic now explicitly says the full acquire/read/cancel/release/enqueue ABI remains open while bounded DTO results are compared separately.

Development first exposed two added read-result count differences in the aborted-read case: native threw cancellation where the source returned closed. That version and its source/evidence are retained. The closure refinement removes those new DTO differences while preserving real cancellation, physical EOF distinction and joined canonical ownership. Development compile and test receipts are separate from immutable candidate qualification and independent acceptance.

Still mandatory: the complete WHATWG acquire/cancel/release arguments/operation ABI and enqueue/prefetch behavior; exact source live-alias/async consumer scheduling; native immutable Start and joined canonical settlement differences; full failure/return shapes; raw signed-zero versus serialized hook values; truthful SDK/native runtime headers and actual wire evidence; transcript/signature/tool-argument/auth/retry/mandatory provider/platform matrices. The strict lifecycle gate remains failing, even when every new bounded read-result comparison matches. Combined native, genuine-terminal and independent review gates belong to the integration lead/parent reviewers.

Source authority: Pi `d86654abb8862e201933517d6f1fce9f88dd117f`, SDK 7.19.0, unchanged `fixtures/reference/openai-completions-sdk-lifecycle/expected.json`, SHA256 `829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`. This branch consumes that historical capture; it did not execute or repair the pinned reference.
