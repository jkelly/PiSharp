# Decision 0016: Internal Transport Retry Classification Alongside Sanitized Error Messages

- **Date:** 2026-11-23
- **Status:** Accepted
- **Scope:** `PiSharp.Contracts`, `PiSharp.AI`, `PiSharp.CodingAgent`, `PiSharp.Durable`

## Context

In PiSharp 1.6.0, four native HTTP/SSE transports (`OpenAICompletions`, `AnthropicMessages`, `GoogleGenerativeAI` / `GoogleVertex`, and `MistralConversations`) replace raw transport/network exceptions with fixed sanitized `errorMessage` strings (`"Completions stream did not complete."`, `"Anthropic stream did not complete."`, `"Google provider operation failed."`, `"Mistral source operation failed."` / `"Mistral operation failed."`). That sanitization policy prevents raw exception messages, stack traces, or credentials from leaking into transcripts, logs, or UI surfaces.

However, because `AgentRetryPolicy.IsRetryableError` and `PiAiRetry.IsRetryableAssistantError` classify retryability from `errorMessage` text alone, transient connection drops (`SocketException`, `HttpRequestException`, `HttpIOException`, premature stream endings, timeouts) on those four transports were not retried by `PersistentAgentSession` or `PiSharp.Durable`.

## Decision

1. **Preserve sanitized user-facing error text unchanged.** On terminal failure (including exhausted retries), all four native transports continue emitting the exact approved sanitized `errorMessage` strings with zero raw exception text or extra wire JSON properties.
2. **Carry an in-memory, non-serialized retryability signal.** `StreamTerminalEvent` and `ChatResult` expose `[JsonIgnore] public bool IsRetryableTransportFailure { get; init; }` (default `false`).
3. **Classify transient connection failures internally at the transport boundary.** `TransportRetryClassifier.IsTransientConnectionFailure(Exception?)` inspects the raw exception chain inside `PiSharp.AI` before sanitization, marking socket resets, aborted/refused/timed-out connections, `HttpRequestException` (without a non-retryable HTTP 4xx status), `HttpIOException`, network-related `IOException`, `TimeoutException`, and premature stream EOF as retryable while rejecting caller cancellation, malformed JSON/protocol errors, resource limits, and permanent HTTP 4xx errors.
4. **Honor the signal in higher-level retry coordinators.** `SessionRetryCoordinator`, `PersistentAgentSession`, and `PiSharp.Durable`'s `GenerationPhases` treat `IsRetryableTransportFailure` as retryable (subject to existing retry policy, attempt budget, cancellation, and context-overflow precedence) without modifying persisted wire message shapes.

## Consequences

- Transient connection drops on OpenAI Completions, Anthropic Messages, Google Generative AI / Vertex, and Mistral Conversations are retried uniformly alongside OpenAI Responses and Codex.
- User-facing transcripts, JSONL session files, and sanitized `errorMessage` strings remain byte-for-byte unchanged.
