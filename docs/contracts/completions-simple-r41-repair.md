# Simple adapter repair after R41

This is an uncompiled, unexecuted source successor to R41 `91db94ac12318dd7ad05f066f4e819154b29b897`. Native execution is paused during restoration of the temporary testing window. R41's actual failures, build products, receipts and immutable reports remain preserved; they are not results for this successor.

The implementation follows Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f` and the existing OpenAI SDK 7.19.0 lock. Independent R41 static triage identified 60 public-message byte-order failures, one payload byte-order failure, five option/header admission failures and one cleanup-classification failure. An earlier comparison failure can hide later failures, so all original 69 cases and 71 entry-point calls still need to run in full.

## Public object order

Completions public snapshots construct fields in Source order: `role`, `content`, `api`, `provider`, `model`, `usage`, `stopReason`, `timestamp`. The producer retains the actual first-assignment order of later response identifiers, response model and finish fields. Assigning an absent response id still preserves own-undefined presence; a later real id occupies its original insertion position.

Usage follows `input`, `output`, `cacheRead`, `cacheWrite`, optional `reasoning`, `totalTokens`, `cost`. Content follows each Source block's construction order. Tool-result requests put `content` before `tool_call_id`, with optional `name` afterwards. Error terminal snapshots retain actual parsed previews and strip parsing buffers as Source does. This is a provider-specific projection; the shared native wire serializer and generic ECMAScript projection remain unchanged.

## Options and headers

Simple retains bounded opaque telemetry and metadata. The Completions SSE provider does not add those values, transport preference or WebSocket timeout to its request body. Null, SSE, auto and WebSocket preferences enter the same Completions SSE implementation, matching this protocol's inert preference behavior. This does not implement WebSocket transport for other protocols.

Scoped environment data is admitted as a bounded object of strings/nulls. The existing native direct options contain an explicit resolved cache-retention enum; it takes precedence over `PI_CACHE_RETENTION`. The borrowed client is not reconfigured, and this repair adds no ambient environment lookup, credential discovery or proxy setup. Source's raw omitted-cache environment fallback remains outside this configured direct-options seam.

Simple's request timeout is forwarded into the real HTTP transport. Each attempt owns a linked deadline, awaits the original send, and disposes the deadline when response headers arrive. The returned SSE body continues under the run's cancellation token. This follows the pinned SDK `fetchWithTimeout` timer, which clears after injected fetch returns. The established outer retry policy still decides whether to retry, after rejected response/request ownership has closed.

`cf-aig-authorization` is admitted and forwarded with ordinary header validation. Explicit keys and options-header precedence follow the Source client construction. For a CF-only header, Source uses `unused` as the SDK authorization placeholder; the frozen fixture records `Authorization: Bearer unused` alongside the authored CF header. It is not a discovered credential. Missing or empty auth still rejects synchronously before any effect. Host, content length, transfer encoding and connection remain prohibited header overrides.

## Semantic failure and physical cleanup

Joining a faulted decoder/provider task retains its semantic failure, rather than counting it a second time as failed disposal. Reader cancellation/release faults after exact `[DONE]` are nonfatal when actual physical disposal succeeds, matching the pinned SDK completion-sentinel catch. Reader faults before exact DONE or at natural EOF remain fatal. Input, body/response and request disposal failures remain fatal even after exact DONE. All original decoder/read/cancel/release tasks are still joined before physical disposal and canonical completion. The original admitted public callback error is preserved independently of the physical cleanup result. No original task or owned resource is abandoned. This corrects the fatal-after-DONE classification introduced in `99aad5b7682756c20c7ecea02c9436daa53a3388`; source, cleanup and canonical held-barrier assertions remain intact.

## Regression coverage and pending gates

The original fixture and all comparisons remain intact. The consumer additionally compares the actual request text and UTF-8 SHA-256 to the captured Source body. Ten separately reported authored controls cover late property assignment, CF/explicit/options-header precedence, forbidden framing headers, opaque bounds and explicit cache priority, send timeout, post-header body ownership, callback failure with successful cleanup, combined callback/release failure, and cancellation/release failure after exact DONE.

The controls have not compiled or run. A pure JavaScript retrospective ordering diagnostic applied the proposed field order to immutable R41 observations; all 676 captured snapshots and all 61 observed request bodies then matched their captured Source strings. That diagnostic is not an execution of the new C# implementation, a new Source capture, or a parity gate.

Required next validation is a newly authorized fresh offline build/publish boundary, new candidate/product receipt and loaded assembly admission, all original Simple cases plus all authored controls, thinking/direct/provider lifecycle regressions, and the assigned full milestone gates followed by independent Astra Medium review. All 378 strict findings, 38 metadata omissions, 79 original scopes and eight open phase gates remain retained. No package acceptance or completion percentage follows from this source draft.
