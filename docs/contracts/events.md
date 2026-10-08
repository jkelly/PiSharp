# Native streaming contract, first executable slice

StartWithAbortSettlementAsync is an explicit opt-in addition described in [aborted-work settlement](abort-settlement.md). It preserves a bounded provider-authoritative Aborted terminal already returned after work cancellation, while the default StartAsync/CompleteAsync contracts below stay unchanged. Callers must drain with an uncanceled reader and await owned cleanup; successful terminals/progress do not gain admission after cancellation.

The implemented native transport consumes normalized immutable progress frames, followed by one terminal event. `RecordedChatTransport` replays authored frame records offline. HTTP, SSE, WebSocket, credentials, provider request translation and production protocol adapters are deferred. Pi v0.99.1 source is pinned to `d86654abb8862e201933517d6f1fce9f88dd117f`.

`ChatClient.StartAsync` returns a `ChatRun`. A run owns one ordered event reader, a `Task<ChatResult>` completion and asynchronous disposal. Its progress channel has a configured positive capacity and applies backpressure. The terminal event is stored separately and emitted after queued progress drains, so cancellation and disposal can settle a full channel without dropping a terminal from an active reader. `CompleteAsync` drains the event pipeline before returning its result. Consumers must read the run or dispose it.

`AssistantStreamReducer` has the following state transitions:

| State | Accepted events | Result |
| --- | --- | --- |
| Before start | `start`, or `error` | Start an empty pending assistant; an error can settle without start |
| Started | Typed block start, delta, checkpoint and end | Keep contiguous stable indices and each block's kind |
| Started, all blocks ended | `done` | Require consistent successful stop reason, requested model identity and authoritative content |
| Started, including partial blocks | `error` | Settle an error or aborted result |
| Terminal | No further reducer events | Reject direct reducer reuse |

Indices start at zero, are contiguous, and can start once. Blocks may interleave. A delta or end must address an active block of the correct kind; events after a block end fail. Text and thinking ends replace the accumulated display text with authoritative end content. Signature and redaction fields are replaced using presence semantics, including explicit null; omission removes the earlier corresponding signature field. Tool ends replace preview arguments with a complete authoritative object while retaining the call identity.

The native frame wire discriminators are `start`, `text_start`, `text_delta`, `text_end`, `thinking_start`, `thinking_delta`, `thinking_end`, `toolcall_start`, `toolcall_checkpoint`, `toolcall_delta`, `toolcall_end`, `done` and `error`. Compact progress fields follow the pinned upstream frame shape. They are separate from upstream `AssistantMessageEvent` objects that carry a shared mutable `partial` on every update. Native events do not acquire later mutations. `Snapshot()` materializes immutable content on demand; per-delta accumulation uses block builders rather than cloning the whole transcript.

Examples of compact progress and settlement:

```json
{"type":"text_delta","contentIndex":0,"delta":"hello"}
{"type":"text_end","contentIndex":0,"content":"hello"}
```

`done` carries `reason` and the finalized `message`; `error` carries `reason` and the finalized `error` assistant message. This first slice enforces balanced successful streams more strictly than the upstream partial-frame replay helper. That behavior and strict JSON parsing require scoped compatibility decisions before a complete parity claim.

Each run settles completion once. The first accepted transport terminal wins and ends consumption; later source events are not requested. A cleanup exception after that terminal does not rewrite its outcome. Direct reducer calls after terminal still fail. EOF without terminal becomes `UnexpectedEof`; malformed transitions or JSON become `MalformedStream`; bounds become `ResourceLimit`; cooperative cancellation becomes `Cancelled` with `aborted`; transport failures become sanitized `Provider` errors. A supplied pre-start error is retained. Invalid run configuration can throw before a run is returned.

Cancellation before start, during a delta or behind backpressure stops production and disposes the transport iterator. Abandoning the reader cancels its producer. Request cancellation leaves the event reader able to drain progress and observe the terminal. Cancelling the reader's own enumeration token can interrupt that reader with `OperationCanceledException`; completion still settles separately. Cancellation and disposal require transports to honor their cancellation token. A trusted uncooperative transport cannot be forcibly terminated by this library.

Every `DisposeAsync` caller awaits one shared disposal-completion task, including concurrent callers arriving while transport cleanup is blocked. Cancellation callback exceptions are retained as the sanitized, separate `ChatRun.CleanupFailure` diagnostic; they do not escape cancellation, skip producer/CTS cleanup or rewrite the terminal result. Provider iterator cleanup exceptions after an accepted terminal likewise do not rewrite settlement. Run completion and completed disposal remain separate boundaries.

Default limits are 256 content blocks and 1,048,576 accumulated characters. The character budget counts content strings, seeded/final tool argument JSON, raw tool fragments, identities and retained message/block/usage metadata. It is a normalized-state budget, not an HTTP frame/UTF-8 byte budget; raw transport decoding and frame size controls are deferred. Error messages synthesized by the runtime add bounded diagnostic text after failure. The reducer remains owned by one producer; it is not a concurrent mutable session store.

Terminal-only error messages pass the same block-count and retained-character admission checks before being accepted. Rejected oversized terminal content is excluded from the synthesized failure result. A successful terminal is also checked before its content is serialized for consistency comparison.

Evidence comes from executable native transition, cancellation, cleanup, backpressure and fixture tests in `tests/PiSharp.Compatibility.Tests/Program.cs`. All tests use task gates and cancellation tokens; no paid calls or provider credentials are used. The four fixture projections are synthetic contract tests, not independent full-provider goldens. P3 can consume finalized `ChatResult` values and typed tool calls without a terminal UI or Node runtime.

Source shapes: [pinned compact frame reducer](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/assistant-message-frame.ts), [pinned stream queue](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/event-stream.ts).

## Native thinking checkpoint successor (source-only)

ThinkingCheckpoint(contentIndex, content, properties) is an additive native event, encoded by PiWireJson as thinking_checkpoint with contentIndex/content plus extra properties. It is not a pinned Pi source event. The reducer requires an active thinking block, replaces the accumulated text and the thinkingSignature/redacted metadata using the same accounting as ThinkingEnded, and keeps that block open. Existing unrelated block metadata remains; absent checkpoint signature/redacted fields remove those prior values. A later ThinkingEnded supplies the final text/signature and closes the block. Neither checkpoints nor subsequent backfill mutate earlier immutable snapshots.

Checkpoints do not permit successful settlement of an unfinished block, alter tool-call authority, bypass configured text/signature bounds, or admit events after cancellation. Responses emits one checkpoint when a valid reasoning output_item.done lacks nonempty encryption. This makes its authoritative done-state visible to ordinary cancellation/error snapshots while terminal backfill remains possible. ChatRun, IChatTransport, reader detachment, abort settlement and disposal/task joining are unchanged. No Agent, SDK, session or terminal behavior is modified.

The Completions, Anthropic, Google and PiMessages producers retain their existing event sets and paths. Completions source projection and PiMessages source parsing do not acquire a new source event. RPC suppresses ThinkingCheckpoint alongside native tool checkpoints and identity fills; it never exposes thinking_checkpoint as an invented pinned-source event. Normal thinking/text/tool ends and acknowledged message_end bodies retain their existing projection. Native codec roundtrip, snapshot immutability, wrong-kind/index/order rejection, text/signature bounds, finality, cancellation admission and held original cleanup have authored regressions. All successor tests remain UNCOMPILED_UNEXECUTED pending runtime authorization.
The narrow RpcAgentEventProjector companion is implemented with two authored test families: actual Responses mapper/ChatClient output through public projection for completed, incomplete, EOF and cancellation; and controls for old native bookkeeping suppression, public end preservation and unknown-event rejection. The projector tests use constructed session snapshots, not a live dispatcher or durable session. No dispatcher/frontend/Agent/SDK implementation changed. Tests remain uncompiled and unexecuted.
