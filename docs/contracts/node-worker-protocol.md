# Extension worker protocol v1: native protocol slice

This implements an early [P7-03](../plans/07-node-bridge.md) slice in the optional `PiSharp.ExtensionHost` project. It supplies a strict wire codec, borrowed injected I/O and a bounded full-duplex coordinator. Its dependencies are `PiSharp.Contracts` and the BCL. Native core and CLI projects do not require this host or Node.

The fixtures are authored native protocol vectors, explicitly marked `sourceCapture: false`. They are not observations of public Pi extensions. The committed real extension corpus still needs execution against unchanged upstream source and a bridge prototype. P7-01 feasibility, P7-02 support decisions, worker supervision, TypeScript loading/facades and P7-G remain open. A successful wire request supplies no tool, filesystem, process or session authorization: composition must route admitted operations through the native broker and generation-checked reducers.

## Envelope and framing

[extension-worker-v1.schema.json](../../schemas/extension-worker-v1.schema.json) defines seven closed envelope variants: `hello`, `request`, `response`, `error`, `progress`, `cancel`, and `shutdown`. Every envelope carries `version: 1`, a positive `workerGeneration`, and a positive `sessionGeneration`. Request, response, error, progress and cancel carry a positive numeric `id`. Identity fields must fit the interoperable integer range 1 through 9,007,199,254,740,991; the codec admits integer tokens through `TryGetInt64`. Identifiers are 1–96 ASCII letters/digits or `_`, `-`, `.`, `:`. Unknown fields and unknown versions fail admission.

Each frame is one UTF-8 JSON object followed by LF. Exactly one CR immediately before LF is removed. The frame byte cap excludes LF and that removed CR. Empty lines, comments, trailing commas, BOM, malformed JSON, duplicate decoded property names, invalid UTF-8, unpaired literal or escaped UTF-16 surrogates and numbers that overflow finite binary64 are rejected. Underflowing finite numeric tokens are retained without rewriting. NUL in a JSON string, emoji, U+2028 and U+2029 remain string data; only physical LF frames messages. Any nonempty EOF tail fails `PartialFinalFrame`, even if it could parse as complete JSON.

The schema is structural. `WorkerFrameCodec` also enforces duplicate-property, Unicode, finite-number, depth, value-count and byte rules. These are native protocol admission rules, not an assertion about upstream JavaScript coercions. The codec removes whitespace outside strings when encoding, preserves opaque JSON numeric tokens and array/property order, validates its own complete envelope before writing and appends one LF. `JsonData` values own their elements after temporary parse documents are disposed.

Values use explicit tags:

```json
{"presence":"absent"}
{"presence":"undefined"}
{"presence":"json","data":null}
```

The JSON form requires `data`; the other two forbid it. This distinguishes a missing operation value, JavaScript undefined and JSON null. Interior JSON objects remain JSON: JavaScript functions, cycles, arbitrary instances and undefined object members need separately specified facade representations. This slice does not silently coerce them into JSON.

## Handshake and identity

Both peers send `hello` and negotiate the intersection of declared features. Default features are `requests`, `callbacks`, `progress`, `cancel`, and `tagged-values`; `requests`, `cancel`, and `tagged-values` are mandatory. Callback handles and progress require their negotiated features. `Ready` completes only after the local hello is actually flushed and a compatible remote hello is admitted. There is no pre-handshake operation queue, and a duplicate hello fails the connection.

One coordinator owns one worker/session epoch. Every incoming frame must match both generations. `InvalidateSessionAsync` fences the old epoch, cancels activity and returns its joined cleanup; a replacement requires a new coordinator and handshake. Continuous worker reattachment across native session replacement belongs to the supervisor integration. Old handles/results cannot be admitted into the replacement by this coordinator.

Each direction allocates its own increasing request IDs. The incoming request high watermark rejects duplicate or decreasing IDs, while replies/progress must correlate to a live request whose actual write attempt has begun. Duplicate, unknown and stale replies fail deterministically. Cancellation of a live incoming request signals its token. Cancellation at or below the incoming high watermark that has already settled is a harmless no-op, because cancellation can cross a flushed reply; an unknown future cancel ID fails correlation. No cancel revives a request or reuses an ID.

`RegisterCallback` creates opaque registration/callback IDs under an owner ID and owner generation. Exact handle equality is required for invocation. Owner revocation removes handles and cancels its active callbacks, retaining their slots until actual settlement. A revoked owner can renew only at a greater generation and receives new IDs. Bounded owner tombstones prevent old generations from being reintroduced; reaching that owner count requires a new connection epoch. A retained request context rejects progress after its callback settles. Registration/factory staging, descriptors, unsubscribe and rollback are later facade work.

## Full-duplex execution and bounds

The coordinator has independent reader and writer pumps. Incoming handlers run after an asynchronous yield in separately tracked, bounded tasks. The reader keeps admitting replies and nested callbacks while an original request awaits its result. State locks protect short transitions only; handlers, writes, progress and cleanup are awaited outside those locks. A handler may await a nested request on the same coordinator without monopolizing its reader. Admitted native operations still need broker recursion/cycle bounds at composition.

Default limits are configurable through `WorkerProtocolOptions`:

| Resource | Default |
| --- | ---: |
| Frame UTF-8 bytes, excluding delimiter | 1,048,576 |
| JSON nesting depth, including envelope | 32 |
| JSON values, including envelope values | 65,536 |
| Pending outbound calls | 32 |
| Active inbound callbacks/handlers | 16 |
| Pending writes, including executing/encoding writes | 32 |
| Registered handles / known owners | 128 / 128 |
| Unread progress values per pending call | 16 |
| Coordinator retained wire bytes | 8,388,608 |
| Reader chunk size | 4,096 |

Options themselves have fixed upper bounds. Admission fails immediately when a call, callback, handle, owner or write count is full; it does not add an unbounded waiting queue. A request's pending slot lasts through the actual request write/flush and actual reply, or through skipping a cancelled unsent request. The callback slot lasts through handler completion and its actual final response write. Write count and encoded frame bytes remain reserved until the executing writer settles or queued work is skipped/drained.

`MaximumBufferedBytes` accounts for encoded queued/executing output frames, owned active inbound request frames, retained replies and unread progress frames while attached to the coordinator. It is a logical UTF-8 wire budget, not a measurement of total CLR heap use. Metadata is separately count/string bounded. The transport has its own bounded per-frame accumulator and read chunk; temporary parse/encode buffers and caller-owned input values are not included in the coordinator byte total. CR/LF bookkeeping and owned JSON representation can differ from physical allocation size. This separation is intentional and must remain explicit in memory reviews.

Progress has one reader per `WorkerCall`. Consuming an attached value releases its coordinator wire reservation. On actual settlement, any remaining bounded owned progress values transfer to the caller's `WorkerCall`; the caller then owns their lifetime. A cancelled caller does not release the pending slot or its retained wire state early. Progress overflow and retained-byte overflow fail closed; no earlier observations are silently filtered to make a case fit.

## Cancellation, faults and cleanup

Caller cancellation reports `Cancelled` with `NotSent` if the request is still queued and is subsequently skipped, or `Unknown` once a write attempt has begun. `Unknown` includes failed/partial writes and replies lost after possible effects. A late local cancel may therefore report uncertainty even when a remote reply was already on its way. No mutation is automatically retried. An incoming stale handle or capacity denial returns a bounded `error` with `notSent`; handler failure returns `CallbackFailed` with `unknown`. Only cancellation of that handler's actual token becomes `Cancelled`; an unrelated `OperationCanceledException` is a callback failure. Error codes are bounded identifiers; exception text is not emitted on the wire.

EOF, protocol faults, IO faults, local disposal and shutdown enter one shared stop/cleanup path. `Completion` is observable before termination and completes only after the reader, writer, encoding reservations and tracked callbacks join and cancellation resources are disposed. Repeated `DisposeAsync` calls return the same task. A transport/protocol fault remains observable through cleanup instead of being swallowed. Cancellation-registration failures are recorded while cleanup continues joining activity. Graceful local shutdown awaits the shutdown write/flush before stopping; remote shutdown stops admission and joins activity.

Transport readers, writers and streams are borrowed and remain open. The coordinator cancels asynchronous IO and handlers cooperatively. Injected IO/handlers that ignore cancellation, or synchronous cancellation callbacks that block indefinitely, cannot be forcibly joined by this in-process slice; process supervision and bounded worker termination remain P7-04/P7-09 work. There is no timeout that releases still-running write/callback admission and claims cleanup succeeded.

The host never writes diagnostics to its protocol transport or `Console`. A future worker supervisor must bind this transport to stdout/stdin and continuously drain separate stderr diagnostics, with redaction and process ownership. No supervisor, executable discovery, runtime acquisition, package installation or Node process is supplied here.

## Authored evidence and gate handoff

[frames.json](../../fixtures/native/node-worker-protocol/frames.json) contains eight authored envelope vectors; [rejections.json](../../fixtures/native/node-worker-protocol/rejections.json) contains nine authored malformed/unsupported vectors. `WorkerProtocolTests.Cases()` (or `Cases(repositoryRoot)`) defines 17 executable test groups using injected bounded channels, text IO and fragmented byte streams. The groups cover complete envelope roundtrips; strict Unicode/numeric/parser limits; LF/CRLF and every byte chunk size; handshake; nested request -> callback -> request progress; actual flush settlement; queued/sent/crossing cancellation; slow writers; owner/handle/count fences; callback error and foreign cancellation; duplicate/stale replies; progress/byte bounds; EOF/fault joining; and shared borrowed cleanup. Tests require no Node, source execution or network.

Authoring and static inspection are complete; no .NET compilation or native test execution was performed by the author. The root-owned optional project/test registration must run the native gate and retain its actual results. Passing these groups would qualify this native protocol slice only. It does not qualify the unchanged TypeScript corpus, supported API matrix, worker lifecycle, facade semantics, broker composition or platform release gates.
