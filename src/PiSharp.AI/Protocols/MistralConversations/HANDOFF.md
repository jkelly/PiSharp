# Bounded Mistral text offline handoff

Status: authored implementation and 23 fake-HTTP controls; not compiled or executed. Whole-provider, catalog, source differential and durable session parity are unqualified.

Base: a54840b36360b70cbfae16e36f831ddc642f3b1c. Source: original Pi v0.99.1, d86654abb8862e201933517d6f1fce9f88dd117f. source-inventory.json records 21 original files and 12 current native contract/ownership inputs with SHA-256 and byte counts. This is source evidence, not an executable transitive-module admission. The generated packages/ai/src/providers/data/mistral.json is absent from both pinned Git tree and reference oracle; no catalog was fabricated or downloaded.

## Implemented slice

MistralTextHttpSseTransport borrows one injected HttpClient and binds exactly one ordinal mistral/mistral-conversations ModelDescriptor. MistralTextOptions requires an explicit base URL, asserted text capability, four finite nonnegative rates and an injected user agent. The explicit API key is never resolved from environment or credentials. The authored test metadata is synthetic and grants no catalog authority.

Initial optional system and subsequent user messages accept strings or text-object arrays. Temperature/maxTokens must be finite. Empty system strings and empty user arrays follow the source omission path. Payload callbacks see camel-case maxTokens; admitted wire payload uses max_tokens. Supported payload replacement preserves the bound model, true streaming, text-only messages and finite options. Unknown fields are diagnosed rather than silently forwarded.

The decoder supports the source's eight CR/LF boundaries, UTF-8 replacement decoding, initial BOM removal, EOF tail dispatch, data-line joining and [DONE]. Output strings and string/text-object arrays produce one text block, with empty sanitized chunks suppressed. It retains the first nonempty response ID, source usage/cache aliases, explicit binary64 cost data, and trailing usage after stop/length/model_length. Finish reasons do not end the original iterator. Missing finish and provider errors receive existing failure codes.

All original callbacks, SendAsync, MoveNextAsync and asynchronous disposal are awaited. TextEnded precedes the terminal; terminal emission waits for cleanup. Caller cancellation is Aborted/Cancelled; transport timeout is Error/SourceFailed. Partial text survives failures. Cleanup failure prevents success and records a secondary diagnostic. Early consumer disposal also awaits owned cleanup. Limits cover configuration, payload bytes, JSON depth, frame/cumulative decoded characters, content, error body and individual/cumulative headers.

The coordinated shared changes append NativeChatAdapter.MistralConversations=4 while preserving 1/2/3, and route ChatRun-generated cancellation, malformed/resource and cleanup diagnostics to this adapter. NativeSessionDiagnosticView is unchanged; durable session qualification remains open.

## Unsupported scope

Tools and tool authority, reasoning, media, assistant/tool replay, mid-transcript system updates, header overrides, deferred APIs, sampling/reasoning helpers, cache-affinity headers, tiered model costs and unknown payload replacement fields are unsupported and explicitly diagnosed when presented through this slice. This text transport has no custom rendering admission. Nonintegral/negative/unsafe usage numbers and unsupported content shapes are outside the bounded numeric/text contract. System.Text.Json duplicate-property and escaped-surrogate admission can differ from JavaScript; source differential must establish parity boundaries before a parity claim.

## Authored verification and integrating owner

The isolated companion project tests identity, request/hook ordering, finish reasons/text chunks, eight SSE boundaries/EOF, trailing usage/response ID, missing finish/provider error, HTTP error truncation, unsupported input/output, resource limits, held callback/send/read joins, caller cancellation versus timeout, explicit capability/cost/options, cleanup failure, ChatRun diagnostics and replacement/depth/header/cumulative-content bounds. Gate tests release in finally and await the original task; they do not infer completion from a cancellation race. Reports use a fresh --report path and fixed secret-free failure text.

No dotnet, Node, provider HTTP, original source runtime or test process was run in this lane. Only source inspection, PowerShell file authoring/hash checks and Git metadata operations were performed. Native build/test belongs exclusively to the coordinator after independent review and allocation.

integration.proposed.json is a reviewable registration proposal against this exact base: 29 to 30 companions, 50 to 51 product roots. Solution, registry, native runner and prepare scripts remain untouched. The integrating owner must regenerate candidate/tree, independent review, full manifest, allocation, actual build receipt, all product roots/settled logs, prepared admission and launch/report joins. Earlier receipts do not cover this change.

Later qualification requires the missing original catalog admission, an independently admitted fake-fetch original capture/source differential, actual native build/tests, and a separate durable diagnostic review. None blocks freezing this authored text slice; all block broader parity claims.

## Review successor

This successor addresses the two P2 findings against a8e7b1810251a11b6784e613ed3ec6a80b156084. Conversion is atomic per unpublished chunk: text length/open state, usage, first ID, raw stop, stop reason and provider error are restored on any admission failure. Earlier published chunks remain intact. Cleanup never emits a text end for a rolled-back first chunk.

JSON/shape/overflow classification lives in owned payload, chunk parsing and state conversion admission. Foreign payload/response/provider callbacks, HTTP send/body acquisition and original reads are awaited with SourceFailed classification and fixed text; exception classes from those operations cannot impersonate malformed admitted data. Existing cancellation and cleanup authority remains separate.

Seven additional grouped controls bring the authored total to 23: raw and ChatClient multi-part failure before first text, preservation of earlier published text, rejecting payload/response/provider callbacks plus send/read failures across InvalidOperationException/KeyNotFoundException/JsonException with secondary cleanup checks, early disposal, held asynchronous body disposal, held payload/provider callbacks under cancellation, and one-byte reads splitting BOM and multibyte UTF-8. No controls were compiled or run. The original pinned source inventory, explicit metadata, trailing usage and terminal-after-cleanup contract remain unchanged.
