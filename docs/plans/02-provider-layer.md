# Phase 2 Provider layer

## Goal and boundary

Deliver a terminal-independent .NET 10 provider library with replayable requests, ordered streaming and reliable cancellation. P3 owns tool execution. Separate provider identity/auth/catalog from wire protocol and HTTP/WebSocket transport. The baseline declares ten chat API IDs and transcript-level system/tool changes. Streams allow pre-start failure; mutable partials need an explicit immutable-event mapping. [B4–B7]

## Entry prerequisites

P1’s baseline lock, parity rows, normalizer and reference harness; agreed message/event schemas; .NET 10 test scaffolding; fake credential store and loopback transport. Real credentials, OAuth grants and paid requests require separate authorization.

## Work packages

1. **P2-01 Implement data and boundary contracts.** Owner: contracts lead; paths: `src/PiSharp.Contracts/{Messages,Models,Streaming}/`, `src/PiSharp.AI/Abstractions/`. Define typed text/thinking/image/tool blocks, usage, diagnostic/error taxonomy and provider metadata. Add `IModelProvider`, `IChatTransport`, credential/catalog interfaces and versioned wire DTOs. Preserve optional/unknown JSON fields where required. Output: serialization fixtures and compile-time consumer examples.

2. **P2-02 Build transcript preparation.** Owner: AI core; path: `PiSharp.AI/Transcripts/`. Implement normalization, ordered system-section updates, tool additions/removals and provider-specific folding. Keep canonical history unchanged; build a request projection. Implement same-model and cross-model reasoning/signature rules, ID remapping and orphan-tool-result handling from the pinned reference. Output: request-projection golden tests. [B6]

3. **P2-03 Implement streaming state and reduction.** Owner: streaming lead; path: `PiSharp.AI/Streaming/`. Use immutable indexed events and one reducer; model pre-start failure, start, interleaved blocks and terminal settlement explicitly. Preserve authoritative block ends and final result without cloning the transcript per token. Add bounded backpressure, single-consumer enforcement and async disposal. Output: transition/property tests and reference-compatible event projection.

4. **P2-04 Build injectable transports.** Owner: transport lead; path: `PiSharp.AI/Transports/`. Add HttpClient-based request/response abstraction, incremental SSE decoding, required WebSocket mode hooks, headers/proxy/endpoint policy, timeouts and resource cleanup. Separate raw wire frames from normalized provider events; bound frame, JSON-depth and accumulated-response sizes. Output: loopback fragmentation/fault harness and recorded response replay, with no network dependency in normal CI.

5. **P2-05 Add authentication and catalogs.** Owner: provider infrastructure; paths: `PiSharp.AI/{Authentication,Catalogs,Registry}/`. Port explicit-key, scoped environment and stored-credential precedence per provider; handle refresh locking, expiry, abort and failure without logging secrets. Import hashed model metadata/costs and distinguish capability declarations from proven support. Support registration/replacement with explicit lifetime rules. Output: fake OAuth/refresh-race tests and catalog compatibility report; persistent storage integrates later with P4/P5.

6. **P2-06 Deliver contrasting initial adapters.** Owner: adapter developers; paths: `PiSharp.AI/Protocols/{OpenAIResponses,AnthropicMessages}/`. Recommended first slice: OpenAI Responses and Anthropic Messages, each with key-auth offline fixtures. Implement payload conversion, tool schemas/choice, reasoning effort/signatures, images, usage/cache accounting, finish reasons and errors. Output: reusable adapter contract suite and complete fixture-backed traces. This staging recommendation does not reduce final scope.

7. **P2-07 Implement tool JSON and replay fidelity.** Owner: AI core; paths: `PiSharp.AI/{ToolArguments,Replay}/`. Separate partial display parsing from executable final arguments; retain raw fragments and match documented repair behavior without inventing values. Preserve namespaces, call/result links and opaque reasoning metadata only in valid replay contexts. Output: cross-provider round trips, malformed/final argument cases and no-execution-before-validation tests shared with P3. [B6, B8]

8. **P2-08 Complete remaining mandatory adapters.** Owner: adapter developers; paths: `PiSharp.AI/Protocols/`, `PiSharp.AI/Providers/`. Inventory-driven waves add OpenAI Completions-compatible endpoints; Google Generative AI/Vertex and Bedrock; Azure Responses, Codex Responses, Mistral Conversations and Pi Messages. Track each provider’s auth, model families and quirks separately from protocol coverage. Include mandated WebSocket/session-cache and deferred-fetch/cancel behavior. Output: complete mandatory matrix; image-generation/classifier APIs are included only where P1 establishes product dependencies. [B4]

9. **P2-09 Settle failures, cancellation and retries.** Owner: resilience lead; paths: `PiSharp.AI/{Resilience,Diagnostics}/`. Distinguish configuration/auth, rejection/rate-limit, timeout, overflow, malformed wire data, disconnect and user abort. Bound retry delays and attempts; record sanitized history. Never retry a partially emitted request invisibly or re-execute tools. Preserve baseline-supported recovery only with explicit attempt boundaries and tests. Output: fault-matrix results and leak/cancellation tests.

10. **P2-10 Qualify and hand off.** Owner: test lead; paths: `tests/PiSharp.AI.Tests/`, `tests/PiSharp.Compatibility.Tests/Providers/`. Run every mandatory capability against offline fixtures, then differential requests/events/results. Optional live smoke tests require separate approval of provider, model, transmitted test data and budget; use synthetic prompts and a kill limit. Output: versioned compatibility report, known gaps and P3 integration example. Live model wording is never a deterministic golden oracle.

## Public and behavioral contracts

`StartAsync` yields a run with one ordered event reader and completion result; distinguish pre-start validation from post-start failure. `CompleteAsync` drains that pipeline instead of waiting on an undrained bounded channel. Consumers must read or dispose runs. One terminal outcome settles completion once. Cancel/dispose unblocks producers, readers and transports; stale events cannot enter replacement runs. Preserve Pi wire behavior through compatibility adapters.

Keep requested/actual model, response ID, raw/normalized stop reason, indexed content, deferred handle and opaque signatures. Reasoning token counts are a subset of output when reported; missing breakdown differs from zero. Cache read/write, one-hour cache writes and costs follow the frozen catalog. No generic middleware invokes tools.

## Required tests and exit gate

Test UTF-8/SSE splits at every boundary; comments/empty frames; interleaved blocks/tools; redacted or empty signed thinking; tool JSON split escapes, invalid final JSON and repair; missing auth before start; EOF without terminal; duplicate terminal; cancellation before headers/mid-delta/during backpressure; 429/Retry-After/timeouts; missing versus zero usage; refresh races; tool-ID collisions; same-model versus cross-model signatures; orphan calls; system changes between calls/results; unsupported images and deferred polling/cancel. Check request bytes or parsed semantic bodies as appropriate and verify no secrets in traces. [B7–B9]

Exit only when every mandatory provider-capability row passes its fixture tests, no unapproved semantic differences remain, final tasks always settle, resources close, and P3 can consume a fake and real adapter identically. An early two-provider preview may unblock P3; it does not close full Phase 2 scope.

## Dependencies risks and effort

P2-01 is prerequisite to P2-02/03/05; transport and transcript work can proceed in parallel. P2-06/08 parallelize after core contracts; P2-07/09 are cross-adapter gates. P3 may start against fake providers after P2-03. Decide SDK versus direct transport per adapter by fidelity tests; keep Microsoft.Extensions.AI optional. Main risks are silent reasoning loss, protocol/provider conflation, incomplete catalogs and transport-specific retry differences. Indicative human-equivalent effort: 15–25 engineer-days for the initial two-provider slice, plus 25–55 for remaining mandatory providers/auth/transports and qualification, subject to P1 inventory; not a delivery commitment.

## Sources

All references below are pinned to the compatibility commit. Facts are sourced; project/file names, task splits, gates and estimates are proposals.

- **B1** [Coding-agent package version and dependencies](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/package.json) and [root package metadata](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/package.json)
- **B2** [MIT license](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE)
- **B3** [AI build and catalog scripts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/package.json)
- **B4** [AI types and event protocol](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/types.ts)
- **B5** [Provider and models runtime](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/models.ts)
- **B6** [Cross-provider message transformation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/transform-messages.ts) and [transcript normalization](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/transcript.ts)
- **B7** [Assistant message frames and reduction](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/assistant-message-frame.ts) and [pre-generation authentication tests](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/test/pre-generation-error.test.ts)
- **B8** [Streaming JSON parsing and repair](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/json-parse.ts)
- **B9** [Responses terminal-event tests](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/test/openai-responses-terminal-event.test.ts) and [provider test inventory](https://github.com/earendil-works/pi/tree/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/test)
