# Completions injected HTTP/SSE composition

Source-facing interrupted reader release follows [the source abort reader order](completions-abort-reader-order.md). Logical lease release precedes public Error/result while admitted read/cancel work and physical disposal remain retained and joined before canonical completion.

`CompletionsHttpSseTransport` composes the accepted explicit-key request factory, shared owned-response/SSE framing, strict JSON admission and `OpenAICompletionsWireSource`. It is a usable offline HTTP-to-Agent boundary. Explicit [bounded outer retries](completions-auth-and-retries.md) apply only to HTTP preparation. It does not provide credentials, live endpoints, catalog discovery, independent timeout configuration, OAuth, session persistence or full Completions qualification.

```csharp
var factory = new CompletionsKeyAuthRequestFactory(endpoint, model,
    projectionOptions, requestOptions);
var transport = new CompletionsHttpSseTransport(client,
    (request, token) => factory.Create(request, explicitKey, token),
    httpSseOptions, mapperOptions);
var chat = new ChatClient(transport);
```

Namespace: `PiSharp.AI.Protocols.OpenAICompletions`. The constructor takes an injected `HttpClient`, `Func<ChatRequest,CancellationToken,HttpRequestMessage>`, optional `CompletionsHttpSseOptions`, and optional `OpenAICompletionsWireOptions`. The client remains borrowed. Each enumeration requires a fresh independently owned request from the factory; it performs one send unless `Retry.MaxRetries` is explicitly positive. The exact enumeration work token reaches the factory and body acquisition. `HttpClient` may pass its own linked token to the injected handler. Pre-canceled enumeration performs no factory call or HTTP send.

The shared transport requests `ResponseHeadersRead`, rejects unsuccessful status without acquiring/serializing the response body, and owns both the response and acquired stream. The Completions wrapper scopes request ownership outside the shared iterator: awaited asynchronous body disposal precedes synchronous response/content disposal, which precedes request/content disposal. This order applies on success, failed admission, read/acquisition/status errors, cancellation and early iterator closure. A bounded `ChatRun` provides single-consumer backpressure and shared concurrent disposal settlement; disposal joins the actual producer and its transport cleanup.

## Framing and admission

Defaults select the bounded OpenAISdk719 framing profile: replacement UTF-8, a reset leading-BOM policy for each line, exact missing/empty event names, named records without data, and pending-event dispatch at EOF. General SSE decoder defaults remain unchanged. Explicit Standard framing requires strict UTF-8 and pending-event dispatch; invalid profile/UTF-8/EOF combinations fail construction. The wrapper parses every admitted non-sentinel data value as a strict object and owns the complete JSON DTO. Decoded duplicate keys, unpaired Unicode, malformed JSON and excess depth fail with fixed diagnostics, including in opaque fields. No provider payload, request URL, key, response body or provider error text appears in those diagnostics. Finite-number/tool-argument admission remains the mapper's responsibility; HTTP framing does not invent executable arguments. See [SDK framing](openai-sdk-sse-framing.md).

The exact SDK authority is the unchanged OpenAI SDK 7.19.0 module loaded by frozen reference `804ad5180657fc90b6b15e384dd51a07d1106aae`: `node_modules/openai/core/streaming.mjs`, 24,544 bytes, SHA256 `1b8fa8338dc28913351993382187962082f0af42c5274a009060de3263579842`. Its current iterator compares data with exact `[DONE]`, breaks, and closes the nested SSE iterator. It does not use `startsWith` or drain later data frames. The wrapper follows that exact sentinel rule for named and unnamed frames. It charges the sentinel, stops before parsing or requesting trailing bytes, and awaits owned cleanup. The sentinel is never a synthesized successful completion: the production mapper must independently validate the observed finish state and finalized arguments. EOF without a sentinel can also succeed after those checks. Whitespace or suffix text around `[DONE]` is parsed and rejected as invalid JSON.

The SDK parses JSON before interpreting named `error` frames, then throws for that name or a truthy `data.error` on ordinary events. The wrapper follows that order and uses a fixed provider-error diagnostic. Named `thread.*` values get the SDK's `{event,data}` envelope; they are not silently relabeled as completion chunks. Other named JSON data is ordinary chunk data. All delivered events, including sentinels and named frames, charge the wrapper's frame limits.

Default wrapper limits are 4,096 data events, 65,536 UTF-16 characters per data event, 1,048,576 cumulative data characters and JSON depth 32. Shared decoder line/event/read-buffer limits and mapper chunk/content/tool limits apply independently. A thread envelope also must fit the per-value character/depth limits. Each data frame is checked before parsing; the accumulated counter retains no unbounded queue. Pulling the next provider frame is required to advance HTTP/SSE acquisition; available mapper frames do not trigger additional body reads. Request-factory payload admission occurs before HTTP effects.

These are logical framed-data bounds. Comments and discarded empty records do not charge the wrapper's data counter; delivered named records do charge it even without data. No cumulative raw-body byte cap is supplied by the shared transport. Response-header admission remains the injected client's configuration, and the wrapper does not mutate its borrowed client. Cumulative raw-byte/header admission is a separate shared-transport hardening prerequisite, rather than an implied whole-response heap bound.

## Remaining mandatory source boundaries

The following are open corrections rather than approved permanent exclusions:

- Default Completions now dispatches named records without a `data` field and rejects their empty JSON, matching the measured source rule. Standard SSE still omits them by its own unchanged profile. Missing/empty name, line BOM and replacement-byte rules have native regressions; genuine whole-provider capture qualification remains required for broader claims.
- This wrapper deliberately awaits and propagates body cleanup failure, including after `[DONE]`. The pinned SDK suppresses cleanup errors once it has observed the sentinel. The new native fault test preserves actual cleanup ownership and a failed terminal. Compatibility disposition still requires a qualified source observation and integration decision; it is not waived.
- The current mapper emits `StreamStarted` before deferred HTTP acquisition. Source emits its start after SDK acquisition. Async acquisition/pre-start failure ordering belongs to the separate stream-boundary correction.
- Mutable/late/blank tool identity, sparse indexes, structured reasoning details, JS numeric usage/cost arithmetic, repaired partial/final JSON, emission-time observations and source final/error/EOF behavior remain the separate mapper/core parity work. The wrapper adds no flags to hide these cases. Strict native duplicate/Unicode/object admission also differs from the broader SDK JSON parser and needs explicit qualification.
- Wrapper parsing/framing/resource, factory, HTTP status, acquisition, read and cleanup exceptions enter the current mapper as acquired-source failures, with typed `NativeDiagnostic.Code: SourceFailed` outside message data. Thus a wrapper limit does not currently become a top-level `ResourceLimit` classification. Mapper-internal limits retain their existing classification. Separately rejected owned cleanup carries `NativeCleanupDiagnostic.Code: CleanupFailed`. The complete standardized provider-error taxonomy remains mandatory work.

## Test handoff

`CompletionsHttpSseTransportTests.Cases()` exposes six groups. It checks all three frozen recorded SDK response wires through actual injected HTTP, complete SDK request bodies at the handler boundary, and every production mapper frame against direct admission of every recorded SDK DTO. This is a complete composition comparison, not a source-stream parity claim: no captured case or event is filtered, and the current mapper's differences remain visible to its separate differential suite. Fixture input/expected/lock hashes are checked before parsing; no fixture or source/SDK file is changed.

Other groups cover bytewise UTF-8/JSON/control fragmentation, exact finalized tool arguments, terminal cleanup gates, EOF/sentinel/named/strict admission, exact and lower budgets, request rejection before send, cooperative cancellation and concurrent `ChatRun` disposal, held post-sentinel reads, pull pacing, all owned acquisition/fault stages and fresh requests across repeated enumeration. A real cleanup fault after `[DONE]` is a required negative native control.

`CompletionsHttpTurnTests.Cases()` exposes two Agent groups. A real request factory/handler/body/mapper/ChatRun/ToolInvoker/scheduler/AgentLoop pipeline checks cleanup before assistant commit, assistant acknowledgement before policy/effect, tool-result acknowledgement before the next HTTP send, source-defined tool replay and retention of details/usage raw values. The authored adapter completely validates its declared two-property schema and the policy/executor receive the identical final action. Malformed, truncated, HTTP-rejected and schema-invalid cases preserve zero effects. This is an in-memory effect fixture; it does not claim durable session or live-provider behavior.

These new groups were authored without native execution in this delegated slice. Root owns registration, project wiring, isolated compilation/tests, immutable commits and independent acceptance. No existing mapper, stream core, shared HTTP/SSE implementation, golden or registration was edited.
