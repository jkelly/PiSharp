# Explicit offline durable RPC CLI host

`session rpc` runs the native RPC dispatcher over actual stdin/stdout and one registry-backed durable session. This is a native headless integration prerequisite, not full Pi CLI/RPC or Phase 5 acceptance. It reuses the authored offline provider and per-file policy from [session commands](session-cli-commands.md); genuine provider services, credential discovery and network transport are not invoked.

## Invocation and ownership

```text
session rpc --session <existing absolute JSONL> --workspace <existing absolute directory> --offline-script <absolute JSON>
  [--leaf <entry ID>|--root] [--allow-read <absolute file>] [--allow-write <absolute file>]
```

Create the session first with `session create`. The session and script must be explicit fully qualified local paths; the workspace must exist and match the canonical stored working directory. Omitted leaf selection uses the physical latest entry. An explicit older leaf appends a sibling branch without rewriting the existing log. Root selection supplies empty ancestry. The exact selected model/tool declarations are restored through the registry and mandatory final-action policy, before request construction. There is no implicit prompt, session search, default home, source overwrite, import or repair.

The public host seam is `RpcSessionCommand.RunAsync(args, stdin, stdout, stderr, cancellationToken)`. Input/output streams and diagnostic writer are borrowed. The host owns the offline client/handler and coordinator; the dispatcher owns its shared JsonlWriter wrapper and session shutdown. Native frames, callbacks, actual response/body/request cleanup and durable append checkpoints retain their accepted ownership barriers.

No session header, startup announcement, handshake or success summary is written to stdout. All responses and events use one shared awaited bounded writer. Startup/terminal host failures emit one fixed JSONL diagnostic to stderr, excluding raw exceptions, rejected payloads and credentials. The inert offline authorization value never becomes persisted session data.

RPC diagnostics include `cleanupFailureCount`. Terminal startup validation uses the same diagnostic fields and order for shared RPC admission rejections, with `cleanupFailureCount: 0` because no resources have been acquired. Console admission and later terminal failure diagnostics retain their existing shape.

## Protocol and settlement

Input uses the accepted strict byte-oriented JsonlReader, including its disclosed recoverable syntax/non-object admissions and terminal hardening failures. The host does not parse commands a second time or infer authority from observed raw bytes. Malformed complete frames can receive a sanitized parse response and allow subsequent frames; duplicate properties, invalid Unicode/UTF-8 and configured hard limits retain the reader's terminal behavior.

The dispatcher supplies correlated responses, asynchronous prompt acceptance, canonical message/tool events, actual queue state/clearing and selected session queries. A successful `prompt` response denotes admission, not completion. Keep reading stdout, and keep stdin open until `agent_settled` when completed work is required. Actual file read/write turns run through the restored declarations, strict final arguments and per-file prepared-action policy. Historical tool calls never authorize rerunning effects.

`get_entries` returns acknowledged physical entry bodies without a header, with the selected context leaf; nested numeric message timestamps and ISO entry timestamps remain distinct. `get_messages` supplies the selected branch model/runtime profile defined by the dispatcher. Source-order records, opaque fields and number tokens are not rewritten by the host.

Abort awaits actual canceled work and cleanup before acknowledgment. Pending queues remain independently inspectable/clearable after abort; they are not silently dropped. The pinned implementation's abort-requested run path suppresses automatic post-abort continuation; a later explicit operation can deliver retained inputs. This source behavior is narrower than prose suggesting immediate continuation of queued work. Clearing first explicitly removes and returns the full queued text.

Closing stdin requests shutdown. Active EOF cancels actual work, awaits the admitted prompt's aborted assistant/message/turn/run settlement, durable append, protocol writes/flushes and provider/body cleanup, then closes the durable writer. It does not release a held request into successful fake completion or start a queued generation. A settled EOF is likewise orderly. Host success is checked against the closed coordinator's acknowledged byte count and actual file length. No usable prefix is relabeled as resumable after corruption, uncertain append or output failure.

Exit 0 denotes orderly host lifecycle shutdown; provider failures and aborts after prompt admission remain authoritative protocol/message outcomes. It does not assert that every prompt succeeded or every authored script turn was used. Exit 1 denotes terminal host/cancellation/cleanup failure; exit 2 denotes startup admission/configuration rejection. Diagnostics conservatively disclose that modifying effects may have completed. Persistence and protocol delivery are not a transaction; broken output can lose a record after disk acknowledgment. Inspect actual durable state before retrying uncertain work.

Actual caller-token cancellation is checked again after dispatcher cleanup and durable closure, producing exit 1 and the fixed `Canceled` diagnostic. Protocol `abort` and ordinary stdin EOF use internal run/input shutdown and can return exit 0 when healthy; they do not cancel the caller token. The direct-host regression observes an admitted held streaming generation, cancels the actual caller, and checks authoritative/durable aborted output, preserved prefix, borrowed streams and independent writer reacquisition before accepting host return. This is not a delivered console Ctrl+C claim.

## Optional authored work gate

The existing bounded script format can place exactly this optional object on its first turn:

```json
{"rpcGate":{"releaseOnGetStateId":"release"},"events":[{"type":"response.completed","response":{"status":"completed","output":[]}}]}
```

The example shows only the metadata placement; a runnable successful response still needs the valid wire DTO sequence appropriate to its content. Gate metadata on later turns, extra gate properties, invalid Unicode/control characters or an empty/over-128-character ID are rejected before writer acquisition. Other script validation and limits remain the shared loader's responsibility.

This data selects a fixed native pacing implementation; it cannot supply code, an expression, executable callback, clock rewrite or authorization. The host provides a trusted internal `beforeSendAsync` delegate to the existing fake handler, preserving that parameter's shape. Responses and Anthropic retain the historical gate on the first HTTP send after request/history validation and the durable input barrier, before response headers. Completions gates the first nonempty actual asynchronous response-body read instead: its correct source-compatible ordering awaits response headers and `OnResponse` before publishing Start, then acquires/reads the body. A client waiting for assistant message-start can therefore send its release command without a circular wait on headers. No Start, chunk, sentinel or final event is fabricated to resolve the test. The actual reader token reaches the trusted callback; cancellation releases its wait and is checked again before any byte is returned.

The Completions response owns one immutable literal SSE byte array derived from the admitted script (1 MiB source plus bounded fixed framing for at most 256 events), with explicit byte length and event-stream content type. Headers return normally; its request receipt records accepted/validated headers, not evidence that response bytes were consumed. Zero-length reads do not consume the gate. Subsequent reads use the same bytes without another gate, prefetch loop or output queue. The owned body installs each actual read task before it becomes visible, permits one admitted read, and cancellation/disposal joins that task and its callback before closing. Synchronous content disposal shares that same settlement after the transport's awaited body cleanup; it cannot return early while a callback still runs. The default Completions reader's cancel/release and response/request ownership remain unchanged.

A complete successful `get_state` response with the configured ID releases the gate only after stdout WriteAsync and FlushAsync finish. Consequently clients can inspect streaming state, queue multiple inputs and clear them while real work is held. Valid raw abort and EOF never open the gate early: the authoritative dispatcher cancels actual work, and that cancellation releases/joins the callback. External cancellation and read/shutdown failures likewise settle the actual work token.

An observed output write or flush failure marks the gate failed before waking it. The trusted callback checks that failure before admission and again after its awaited release/cancellation check, so a failure wake cannot acquire valid response data. For Completions, headers may already be accepted but no held body byte gains authority. Shutdown's fallback release leaves the failure flag intact and still joins owned cleanup. This is native failure handling for held offline work; it cannot roll back tools or response bytes already acquired before a later output failure.

The gate is authored offline test behavior, not upstream event timing parity. Without metadata the callback returns immediately and the historical one-shot commands are unchanged. The appended internal profile parameter defaults to null; caller-provided serialized data never constructs a delegate.

## Bounds, metadata and qualifications

Existing shared script limits are 1 MiB, depth 32, 64 turns, 256 events per turn and 4096 events total. CLI argument/path/permission bounds and 64 KiB text file effects remain the general session command profile. RPC frames are capped at 1 MiB and depth 32, with at most 32 pending writer calls and the dispatcher's separate configured command/query/run limits. These are logical admission limits, not a total heap or lifetime session-file guarantee.

The full model JSON returned by state is explicitly authored for `pisharp-offline-session/openai-responses/openai`, including name, fake base URL, non-reasoning text input, zero synthetic costs, contextWindow and maxTokens metadata. Its `provenance:"authored-offline-profile"` and `liveModelCapabilityClaimed:false` remain visible. These metadata values are not observed live-provider capacities or tokenizer-based admission guarantees. Runtime safety uses the explicit byte/character/message/tool caps. Native storage IDs and clock values are fresh; no upstream clock/source private field is rewritten.

The mandatory policy authorizes only explicit canonical read/write targets below the workspace and excludes the session/script. Native paths remain trusted local controls with disclosed hard-link/replacement/TOCTOU limitations; no hostile filesystem sandbox is claimed. Durable acknowledgment retains the existing LocalFileFlush qualification rather than a universal filesystem/power-loss guarantee.

## Supplied separate-process evidence

`RpcSessionCommandTests.Cases(dotnetHost, cliDll)` supplies six authored separate-process groups:

- Held actual HTTP work with asynchronous get_state, multiple independent queues, full clear, flushed release and Unicode-separator prompt preservation.
- Real permitted read/write tool turns, final-target denial, canonical acknowledged entries/timestamps and LF-only shared protocol output.
- Actual canceled held work, authoritative aborted assistant, retained steering/follow-up values and await-idle abort acknowledgment.
- Active stdin EOF, final aborted protocol/durable commit, no queued continuation and independent writer reacquisition after child exit.
- Independent process reopen and explicit older-leaf branching, selected request history and unchanged prior source bytes.
- Malformed frame recovery, unsupported command failure, terminal Unicode hardening and bounded startup/script/gate admission without durable mutation.

One additional direct-host group gates a borrowed output stream separately at WriteAsync and FlushAsync. It observes actual running state while the scripted response remains held, then fails that delivery. The controls check fixed diagnostics, borrowed stream survival, no scripted tool authority or file effect, unchanged acknowledged log prefix, and independent exclusive writer acquisition after host return. Their channel input and output barriers control ordering; cleanup deadlines do not supply expected timing.

Child tests use explicit executable/DLL paths, shell-free arguments, concurrent bounded output collection, continuously parsed binary JSONL, actual temporary files, protocol/work gates and cleanup deadlines. No sleep controls, real provider/network calls or actual console Ctrl+C test are claimed. Writing these files does not assert a passing build or independent acceptance; registration and immutable isolated validation are parent-owned.

Pinned research uses `d86654abb8862e201933517d6f1fce9f88dd117f` [RPC types](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-types.ts), [RPC implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-mode.ts), [RPC contract](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/rpc.md), [CLI parsing](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/cli/args.ts), and [session-level run lifecycle](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts). The native host grammar, fake provider model, pacing metadata, limits and durable receipts are authored integration evidence.

The full mandatory RPC command inventory, extension/UI/preflight interfaces, recovery/compaction/retry, live authentication/provider setup, terminal delivery, unchanged reference-client interoperability and supported platform/filesystem matrix remain unfinished requirements.
