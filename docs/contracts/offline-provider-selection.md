# Explicit offline provider selection

`session create`, `session prompt`, `session resume`, and `session rpc` accept optional
`--offline-api openai-responses|anthropic-messages`. Omission preserves the existing
`openai-responses`/`openai` binding and model ID `pisharp-offline-session`.
`anthropic-messages` selects the same authored model ID with provider `anthropic`.
Unknown, duplicate, and authentication options reject during argument admission.
`inspect` and `tree` remain read-only provider-independent commands and reject this
execution selector.

For example, use the same explicit selector when creating and reopening:

```text
session create --session <absolute JSONL> --workspace <existing directory> --offline-api anthropic-messages
session prompt --session <JSONL> --workspace <directory> --offline-api anthropic-messages --offline-script <absolute JSON> --message <text>
session rpc --session <JSONL> --workspace <directory> --offline-api anthropic-messages --offline-script <absolute JSON>
```

Each profile registers only its selected model family plus the existing `read` and
`write` adapters and mandatory final-file-action policy. The source-shaped durable
`model_change` stores `provider` and `modelId`; it has no canonical API field. The
authored initial system message additionally retains `offlineApi`. Completed
assistant bodies retain the actual API/provider/model identity, the one-shot report
includes `offlineApi`, and RPC `get_state.model` includes the complete authored
descriptor. A latest or explicitly selected ancestry whose model lacks this binding
returns sanitized `OfflineProviderMismatch` (exit 2), after closing the acquired
store and before prompt append, provider send, or file execution. It never rewrites
the log. Existing `--root` semantics select empty ancestry and explicitly use the
chosen fallback model; unrelated branches do not dictate its binding.

## Actual offline HTTP and SSE

The Anthropic profile composes `AnthropicMessagesKeyAuthRequestFactory`, an actual
`HttpClient` with a closed injected `HttpMessageHandler`, shared HTTP/SSE framing,
`AnthropicMessagesHttpSseTransport`, the accepted DTO mapper, Agent loop, and durable
coordinator. It never routes through an executable network handler or injects typed
provider DTOs into the Agent. The fixed base is `https://offline-session.invalid`;
the accepted factory appends `/v1/messages?beta=true`. Only the fixed authored inert
key is supplied. No environment, credential store, SDK, Node, live endpoint, retry,
or paid provider operation is consulted.

The handler admits the actual POST URI, JSON content type, selected model,
`stream:true`, provider input field, and authentication headers. Anthropic requires
`x-api-key`, version `2023-06-01`, no Authorization, `messages`, `max_tokens:8192`,
and no body `betas` or Responses `input`. Responses retains its existing route,
Bearer authentication, `input`, and `application/json; charset=utf-8`. Request
reports include only header identity and successful inert-key validation, never key
values. The default Responses static descriptor and trusted `beforeSendAsync` hook
remain available internally.

Scripts retain the bounded `schemaVersion:1`/`turns[].events[]` grammar from
[session commands](session-cli-commands.md). Literal event objects become SSE data;
Anthropic additionally emits their bounded `type` as the SSE `event` field.
Recognized events from the other family reject as `InvalidScript` before opening a
session. Unknown named events remain subject to the accepted provider bridge's
filtering and resource bounds. No completed event or missing final frame is invented.
`requiredInputTexts` checks the actual provider input (`messages` for Anthropic,
`input` for Responses). A turn may supply `expectedRequest`, an exact authored full
JSON body assertion: object key order is ignored, array order, field presence,
null, strings, and numeric lexical tokens are preserved. Mismatch fails the acquired
source before obtaining a scripted response. The already acknowledged user input
and failed assistant may be durable; executable tool authority is never granted.
Request/script/stream/turn/transcript/file limits remain bounded as in the existing
profiles, with a 1 MiB request and script limit and at most 64 turns.

RPC retains its actual HTTP work-token gate, release only after successful shared
stdout flush, poisoned output-fault authority, full queue receipt and clear, abort
settlement, and EOF shutdown. Durable message-end acknowledgement precedes public
message-end/get-messages/get-entries observations. External caller cancellation is
checked again **after** dispatcher cleanup and durable close, preserving `Canceled`
exit 1; ordinary protocol abort and EOF use their existing internal cancellation.
The host still borrows stdio and closes owned provider/coordinator resources before
return. No Bash authorization or execution policy is changed.

## Evidence and remaining work

The added CLI cases are authored native child-process controls, not genuine upstream
goldens. They check complete first Anthropic request bodies, inert-auth/header/route
admission, actual file tool calls and results in subsequent HTTP requests, durable
byte prefixes/length acknowledgement, independent reopen, older selected ancestry,
API identity, authorization denial, wrong-family rejection, and request mismatch.
RPC reuses the real gate, abort, EOF, write/flush failure, and caller cancellation
controls for both providers; its Anthropic cases also compare returned entries with
actual stored raw entries. The live checkpoint control owns an explicit
`FileShare.ReadWrite` read stream while the child owns its durable writer; the
reader borrows that stream, and the control awaits its close before checking complete
source, exact entry count, and full acknowledged byte length. `ReadFileAsync` keeps
its existing `FileShare.Read` contract for ordinary closed-writer observations.
These checks require parent execution of the compiled
native suite; source authoring alone makes no passing claim.

RPC model `contextWindow`, input capability, zero costs, and `maxTokens` describe an
authored offline profile. Anthropic's `8192` is also the actual request token setting;
Responses does not gain a request token limit from this metadata. Neither descriptor
is a discovered live model capability or runtime catalogue claim. The accepted
[Anthropic bridge contract](anthropic-messages-http-sse.md) separately records genuine
SDK fixture evidence, native default runtime-header differences, explicit captured
metadata comparison, and source start-timing differences; this CLI profile does not
claim SDK metadata or actual network-wire parity. Full provider families, live
authentication/OAuth, automatic model catalogue, complete numeric/streamSimple
profiles, retries, and remaining RPC/CLI parity are mandatory unfinished work.
