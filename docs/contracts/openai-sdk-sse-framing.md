# Bounded OpenAI SDK 7.19.0 SSE framing

`CompletionsHttpSseTransport` now defaults to the bounded `OpenAISdk719` framing profile. A general `new SseDecoderOptions()` still selects `Standard`, with its existing replacement UTF-8, single stream-leading BOM removal, connection ID/retry state and discard-at-EOF behavior. Existing explicit strict Standard Completions framing remains available. This changes the provider's default admission of named records without data fields and malformed UTF-8 inside otherwise valid JSON strings.

The shared implementation remains `src/PiSharp.AI/Transports/SseDecoder.cs`; there is no separate `IncrementalSseDecoder` implementation. `SseDecoderOptions.Profile` and `SseEvent.EventName` are nonpositional init properties. The existing five-argument options constructor/deconstruction and four-argument event constructor/deconstruction remain source compatible. Standard events keep `EventName` null, preserving equality with legacy event values. `EventType` retains the existing `message` fallback when the actual event name is absent or empty.

```csharp
var framing = new SseDecoderOptions(
    RejectInvalidUtf8: false,
    EofBehavior: SseEofBehavior.DispatchPendingEvent)
{
    Profile = SseFramingProfile.OpenAISdk719
};
var decoder = new SseDecoder(framing);
// The Completions transport selects these settings when Framing is omitted.
```

SDK profile construction rejects strict UTF-8 or discard-at-EOF settings. Unknown profile values and invalid existing size/read-buffer limits reject before any HTTP send. An explicit Standard profile passed to Completions must retain strict UTF-8 and EOF dispatch, as before.

## Framing contract and source authority

The authority is the unchanged task-local OpenAI 7.19.0 SDK loaded by the frozen whole Completions reference `804ad5180657fc90b6b15e384dd51a07d1106aae`. `core/streaming.mjs` is 24,544 bytes, SHA-256 `1b8fa8338dc28913351993382187962082f0af42c5274a009060de3263579842`. Its `SSEDecoder.decode` at lines 595-645 dispatches a blank record if the event name is truthy or at least one data field exists. Its `flush` applies that same rule once at EOF. `Stream.fromSSEResponse` at lines 95-130 checks exact `[DONE]` before parsing JSON, named errors or thread envelopes. These source-derived expectations are not new executed source captures.

`EventName` carries the SDK's exact event field after removing at most one leading space: null means no event field, empty string means an explicitly empty field, and other values retain spaces, colons, tabs and inert NUL characters. No executable identifier/path policy is changed. Later event fields replace earlier names. Data fields join with LF; only the final field separator is removed. Comments and unrecognized fields do not create a public event.

| Wire record | SDK-profile result |
| --- | --- |
| Blank/comment-only records | No event |
| `event:` followed by a blank line | No event; empty name stays pending |
| Empty-name record, then `data: {}` | One event with `EventName == ""` and data `{}` |
| `data: {}` without an event field | One event with `EventName == null` |
| `event: error` without a data field | One event with empty data; Completions JSON admission fails |
| `data:` | One event with empty data; Completions JSON admission fails |
| `event: thread.run` without data | One event with empty data; JSON admission fails before envelope handling |
| Named `error` with data `[DONE]` | Exact sentinel ends acquisition before error handling |
| Data `[DONE]suffix` or an extra leading space before `[DONE]` | Ordinary malformed JSON, not a sentinel |
| Pending data or nonempty name at EOF | Dispatched once, including a final line without a terminator |

Actual dispatch resets name/data. A discarded blank record retains an explicit empty name. LF, CR and CRLF line endings and fragmented multibyte Unicode remain incremental. A BOM-only final line can itself complete a pending event; the subsequent EOF dispatch does not duplicate it.

The SDK's `internal/utils/bytes.mjs` uses a default, nonfatal `TextDecoder` for each completed line. Its hash is `99aac602879f5110091ed3725701a0b919aeb4dd4014fc4d8e3586e9961ff76f`; `internal/decoders/line.mjs` has hash `cc64f56402b80488c3478a9586d6239e3c42d289b496ea97ee79ff3f4abffa7f`. The SDK profile uses incremental replacement UTF-8 and removes one leading BOM from each decoded line. An ASCII line terminator completes any interrupted UTF-8 prefix, so decoding retains no unresolved multibyte prefix across a line ending. A BOM inside a data value stays present; a second leading BOM stays present after removing the first. The authored tests exercise every two-chunk split, read buffers one through eight, an isolated invalid byte, interrupted/truncated multibyte prefixes and per-line BOM names.

## Resource and ownership bounds

Existing read-buffer, line and pending-event limits still apply. The read buffer stays between 1 and 65,536 bytes. SDK line bounds count decoded UTF-16 characters before removing that line's BOM. Pending-event accounting includes the current event name and each retained data field plus its separator. Replaced names release their previous logical charge; dispatched events reset their charge. Comments and ignored fields are subject to line bounds.

The SDK ignores `id` and `retry`. SDK-profile records and connection state therefore retain an empty ID and null retry, without parsing large retry integers or accumulating identifier state. Standard still charges pending and distinct committed IDs plus retry characters using the existing accounting. No private raw-chunk list, event queue or detached delivery task is introduced. The SDK's private raw list can retain comments across discarded blank records; public Completions delivery does not expose it, and this bounded implementation does not reproduce that unbounded internal retention.

The Completions wrapper charges each delivered frame, including a named empty-data frame and the sentinel, against its existing frame/data limits. Names remain subject to the shared event limit and thread-envelope admission. Strict native JSON object/duplicate/depth/Unicode admission is unchanged. Replacement decoding permits an invalid wire byte inside a JSON string to become U+FFFD; it does not make malformed JSON or invalid executable tool arguments valid.

Framing retains single enumeration, awaited read/disposal, cooperative cancellation and borrowed-stream behavior. HTTP retains request ownership, asynchronous body cleanup before synchronous response cleanup, and borrowed client ownership. Exact DONE stops before requesting a later read; successful or failed terminal publication still waits for owned cleanup.

## Tests and remaining qualification

Root registration is `tests.AddRange(OpenAISdkSseFramingTests.Cases())` in the Transport console consumer. The new file exposes six behavior groups: legacy/Standard consumers; SDK null/empty/name dispatch/reset/EOF; fragmented UTF-8/BOM; inclusive resource limits and ignored controls; actual default Completions HTTP composition; and held early-return/sentinel/cancellation cleanup. It uses existing fake HTTP/stream helpers, with no network or SDK dependency in the native runtime.

The new default proves the formerly dropped named/no-data frame through real `CompletionsHttpSseTransport` and `ChatClient`. An explicit Standard transport remains a comparison control. Existing frozen three-case whole-SDK wires remain unmodified and are replayed by the existing composition suite. The old direct `SseDecoder` named/no-data witness still describes Standard framing, rather than the corrected Completions default.

Implementation and tests were authored without .NET build/test/product execution, Git changes or new source captures in this delegated slice. Root owns registration, compilation, execution, immutable commits and independent acceptance. No mapper, scheduler, Program, source fixture, golden, SDK or source-oracle file was edited.

Mandatory source parity remains open: the pinned SDK suppresses cleanup faults after DONE and can settle abort paths without joining pending cancellation; native cleanup still joins and propagates those faults. The held cleanup-fault test preserves that current native failure and sanitized diagnostics. Source emits start after SDK acquisition and `onResponse`; the current mapper emits start before deferred HTTP acquisition. Neither lifecycle behavior is hidden or treated as a permanent exclusion.

Broader JSON.parse admission, numeric/own-property semantics, provider error taxonomy/diagnostics, mapper behavior, request acquisition/retry policy and full whole-provider lifecycle traces also require qualified genuine captures and integration. This framing slice does not assert full provider or Phase 2 parity. Fresh source observations must compare the complete public provider hook/emission/drain/final result and cleanup trace, alongside all existing captured cases, without filtering mismatches.
