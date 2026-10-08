# Explicit offline Completions CLI and RPC composition

`session create`, `prompt`, `resume`, and `rpc` additionally accept `--offline-api openai-completions`. The existing default remains `openai-responses`; explicit Responses and Anthropic selections, scripts, policies, report/print/JSON output, and shutdown behavior retain their accepted paths. Read-only commands still reject provider-selection flags. These commands are native, with no Node product dependency.

The new profile uses the explicit authored model `pisharp-offline-completions-session`, API `openai-completions`, provider `openai`, and final endpoint `https://offline-session.invalid/v1/chat/completions`. Its distinct model ID matters: acknowledged session model selection stores provider/model ID, and the runtime registry keys those two fields rather than API. It prevents a Responses offline session from being silently rebound as Completions, or the reverse. Reopening through the wrong offline profile fails with `OfflineProviderMismatch` before any new durable input. It does not add a new durable API field or alter registry authority.

The supplied endpoint is consumed only by the profile's injected `HttpMessageHandler`. No DNS, network, environment API key, credential store, provider installation, authentication discovery, or fallback HTTP handler is used. The fixed inert Bearer value exists only to exercise actual request construction and admission; it is neither a credential nor persisted in session history. This is an authored offline configuration, not a live model/cost claim.

## Actual native path and request admission

The profile constructs the accepted `CompletionsKeyAuthRequestFactory` and `CompletionsHttpSseTransport`, which in turn use actual HTTP response/body cleanup, SDK-profile SSE framing, `OpenAICompletionsWireSource`, reducer/ChatRun, stateful Agent, runtime registry, and `PersistentAgentSession`. Each request is freshly owned by the transport; the profile owns the injected client and handler. Input/assistant/tool-result append and checkpoint barriers retain their existing ordering before tools or later provider turns. Session command success occurs after coordinator/storage disposal and physical length checks. RPC has one shared JSONL writer, disposition-aware asynchronous responses, and awaited EOF/cancellation cleanup.

Requests are POST with JSON content type `application/json` and the explicit inert Bearer header. The actual factory emits `model`, projected `messages`, `stream:true`, `store:false`, `stream_options.include_usage:true`, `max_completion_tokens:8192`, and current function declarations. This text/function profile is nonreasoning; it supplies no effort or discovered capabilities. The fake handler validates the selected endpoint/method/auth/body model and these payload flags, checks optional complete `expectedRequest` structurally (object-key order only; strings/arrays/null/missing/numeric tokens exact), and applies literal `requiredInputTexts` to projected request strings. Completions declaration observations read `tools[].function.name`, preserving the actual factory shape. Request/report metadata does not log the key or raw request body.

The same registered built-in read/write adapters and mandatory final-action per-file policy execute tools. Only explicitly allowed final local targets are authorized; existing reserved session/script paths and workspace validation remain intact. Opt-in Bash remains the accepted independent exact-command/action authorization profile; this candidate adds no automatic Bash grant or claim of new Bash-family qualification. Broader built-ins, extensions, reasoning/images, live credentials, general CLI configuration, TUI, and full RPC command parity remain required work.

## Literal script profile

The shared script schema remains `{ "schemaVersion":1, "turns":[...] }`, with each turn retaining `events`, optional `requiredInputTexts`, optional `expectedRequest`, and the existing inert RPC gate metadata. Here `events` contains literal parsed Completions chunk objects:

```json
{
  "schemaVersion": 1,
  "turns": [{
    "requiredInputTexts": ["hello"],
    "events": [
      {"id":"authored-chunk","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"offline answer"},"finish_reason":null}]},
      {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]},
      {"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12}}
    ]
  }]
}
```

No `type` is invented on chunk objects. Each must have an array `choices`; optional `object` must equal `chat.completion.chunk`. Typed Responses/Anthropic envelopes fail Completions preflight, and typeless Completions chunks fail the unchanged typed-family preflight. Strict owned JSON, unique decoded keys, scalar Unicode, and finite numeric values are required. Inner delta/finish/usage semantics remain the actual mapper's responsibility, so well-formed admitted scripts can intentionally exercise protocol failures after acknowledged user input. Complete/final tool arguments remain strict; partial fragments cannot authorize tools.

Before acquiring a session writer, the inherited parser caps the UTF-8 script at 1,048,576 bytes, depth 32, 64 turns, 256 chunks per turn, and 4,096 chunks total. Completions also caps each retained raw chunk at 65,530 UTF-16 characters, reserving six characters for `data: ` within the inherited 65,536-character SSE line bound. HTTP/SSE retains 256 data events per response and 1,048,576 total data characters; mapper/reducer/session/queue/output bounds still apply independently. The native finite-value admission and bounds are explicit hardening, not source limit parity or a total heap promise.

The fake handler writes every literal chunk unchanged as `data: <JSON>\n\n`, in source order. It does not synthesize `[DONE]`, a normalized final assistant, usage, or a successful finish reason. The real mapper drains finish and usage chunks through body EOF and finalizes them under its accepted contract. Missing finish reason, malformed final arguments, request/history mismatch, or unsupported wire data produce the real sanitized provider failure and retain honest durable state. They cannot produce a tool effect from a partial or rejected stream. No retry or script-turn replay is introduced.

RPC's existing gate is held inside the actual injected HTTP send under its work token. A configured `get_state` response releases it only after a complete successful write/flush. Abort, active EOF, caller cancellation, and poisoned output release still join owned work and durable close; they cannot wake a failed gate into a successful scripted tool response. Queued values remain actual Agent queues, and abort does not silently rerun them. Normal clients keep stdin open until `agent_settled` before orderly EOF.

## Authored verification and remaining qualification

Four appended groups reuse the existing compiled CLI/RPC child, bounded output, gate, borrowed-stream, deadline, and process-tree cleanup helpers. CLI covers actual read/write/tool-next-request turns, complete first factory payload, fragmented argument/text chunks, usage/cache/provider IDs, exact NUL/Unicode file bytes and retained arguments, independent resume/selected sibling ancestry, source/script preservation, closed durable acknowledgement, cross-family/API preflight, bounded chunks/nonfinite metadata, failed request, strict arguments, and EOF. RPC covers actual tool updates and results, shared LF events, current model state, acknowledged raw entries, independent reopen/selected branch, policy denial, malformed-frame recovery, actual running-state/queue clearing, retained queues through abort, active EOF, write/flush poisoning, caller cancellation, and exclusive durable lease reacquisition.

Root's first immutable candidate `1d9a665b37212b2c3df887be9b31928d3cdbe1b7` compiled with zero warnings/errors. CodingAgent reported 82 passing groups and one failure in the first `CompletionsProcesses` child; both appended RPC groups and Completions admission passed. The preserved first log is `artifacts/offline-completions-cli-rpc-first-native.log`, with the failed receipt copied to `artifacts/offline-completions-cli-rpc-first-codingagent-results.json` before rerunning.

Source inspection found an authored complete-request fixture error: it copied Anthropic `input_schema`, which omits `additionalProperties`, into Completions parameters. The actual registered read/write declarations contain `additionalProperties:false`; the Completions converter preserves those nonstrict parameters. The corrected expected request independently spells the closed schema fields including `additionalProperties:false` for both functions. This follows pinned `packages/ai/src/api/openai-completions.ts` `convertTools` and the genuine Completions SDK golden's retained closed schemas. It changes neither production, structural comparison, registered schemas, nor upstream goldens. Full body, exact file bytes, NUL arguments, next-request history and branch/reopen assertions remain. This corrected fixture has not yet been rerun; root owns that serial gate.

Two root process-only probes used the unchanged first candidate's compiled CLI. The old complete-request expectation produced exit 1 with zero admitted requests/actions; changing only the two authored schema fields produced exit 0 with three requests, two actions and exact Unicode/NUL file bytes. Both complete process receipts and scripts are retained under `artifacts/completions-original-schema-probe-first/`; the aggregate repair gate is separate and still pending at this commit.

Root owns native registration, compilation, serial gates, immutable commits, and independent review. Existing test IDs/groups remain unchanged. The script and complete expected request fixtures are authored source-informed data; this integration does not create or rewrite a genuine upstream golden. The accepted Completions components carry their own pinned source/default-SDK qualification evidence. Combining them here does not claim full provider, wrapper/options, CLI, RPC, cross-platform, extension, or phase closure. Source signal/observer timing, mutable partial aliases, source error/abort traces, and unsupported capability combinations remain mandatory qualified comparison work.
