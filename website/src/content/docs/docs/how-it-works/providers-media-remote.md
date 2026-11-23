---
title: Providers, media & remote execution
description: How PiSharp handles multi-provider streaming, maintained model catalogs, Anthropic threads, service tiers, media models, telemetry and remote environments.
---

Below the agent loops sits PiSharp's infrastructure layer: native LLM transports, model catalogs, multimodal media APIs, opt-in telemetry, and remote execution daemons. Every piece is usable on its own without bringing in the CLI.

## Native providers & model catalogs (`PiSharp.AI`)

`PiSharp.AI` implements streaming transports directly over .NET's `HttpClient` for every API Pi supports — Anthropic Messages, OpenAI Responses and Chat Completions, OpenAI Codex, Azure OpenAI, Amazon Bedrock Converse, Google Gemini and Vertex, Mistral Conversations, GitHub Copilot, OpenRouter, xAI, Qwen, local [llama.cpp](/docs/guides/llama-cpp/) routers, and Pi Messages.

- **Catalog composition order** (lowest to highest precedence):
  1. Pinned upstream catalog (1,853 entries across 43 providers in PiSharp 1.6.0: 1,698 chat, 74 image, 81 classifier).
  2. Remote catalog (if enabled).
  3. Maintained overlay (`catalog-maintained/models.overlay.json`, optionally pointed elsewhere with `PISHARP_MAINTAINED_CATALOG`) — refreshed by sample [31-ModelCatalogMaintainer](/samples/?q=31-ModelCatalogMaintainer) without touching pinned hashes.
  4. User `models.json` configuration.
  5. Extension-registered models.
- **Explicit credentials:** embedded libraries never read environment variables implicitly. You supply an explicit key or environment reader (`Environment.GetEnvironmentVariable`) to `NativeProviderFactory` or `NativeDurableModels`.
- **Service tiers (`PISHARP_TIERS001`):** attach per-request or per-conversation latency/cost tiers (`auto`, `normal`, `economy`, `priority`, `fast`, `ultrafast`) via `ChatRequest.ServiceTier`, `AgentConfiguration.ServiceTier`, or `/tier` in `pisharp durable`.
- **Anthropic message threads:** bounded process-local continuation cache (`AnthropicMessageThreadStore`, default 10,000 sessions) that hashes normalized transcript prefixes so repeated turns send only the new suffix plus the prior reply ID, automatically falling back to full requests if the endpoint rejects continuation. See [Anthropic message threads](/docs/embed/anthropic-threads/).

:::note[New in 1.6.0: transport improvements]
In PiSharp 1.6.0: retries cover socket error codes, closed connections, and the 60-minute Codex limit; strict tool schemas are enabled only where models support them cleanly; the 1,000,000 output-token ceiling is lifted; and default `openai`/`azure`/`openai-codex` model selection updates to `gpt-6.2-sol`.
:::

## Media models & multimodal inputs (`PiSharp.AI.Media`)

Beyond chat completions, `PiSharp.AI` includes two model-operation surfaces:
1. **Standard Pi model operations (`PiSharp.AI.ModelOperations`):** classifier models and image generation APIs (demonstrated in [27-ProductContentStudio](/docs/samples/multimodal-receipt-and-studio/)).
2. **Experimental Media Library (`PiSharp.AI.Media`, `PISHARP_MEDIA001`):** unified APIs for **images, video, speech (TTS), transcription (STT), music, embeddings, rerank, and decisions**, also surfaced as `generate_image`, `generate_video`, `embed`, and deferred audio/rerank tools inside `PiSharp.Durable.Tools`. See [Media library](/docs/embed/media/) and [Media tools](/docs/guides/media/).

For **multimodal chat input**, pass PNG, JPEG, or WebP attachments directly in user messages (demonstrated in [16-ReceiptToExpense](/docs/samples/multimodal-receipt-and-studio/)) or use the `read` tool with `PiSharp.Tools.Skia`.

## Opt-in telemetry (`PiSharp.Telemetry`)

`PiSharp.Telemetry` (experimental, `PISHARP_TELEMETRY001`) ports Pi's telemetry contract — spans, links, context propagation carriers, metrics, and typed schemas — with no-op and in-memory adapters and **zero built-in network exporter**. Nothing is recorded unless your host creates and passes a span, and nothing leaves your process unless you wire your own exporter. See [Telemetry](/docs/embed/telemetry/).

## Remote execution environments (`PiSharp.Env`)

`PiSharp.Env.Protocol` and `PiSharp.Env` (experimental, `PISHARP_ENV001`) let a durable host run file, watch, process, and Unix PTY operations on another machine or container via `RemoteExecutionEnv` (which implements Durable's `IExecutionEnv`):

- **Framing & reconnects:** `Connection` speaks bounded framed RPC to the NativeAOT `PiSharp.Env.Daemon` over stdio or SSH. Requests lost on disconnect are **never silently replayed** because a remote mutation's outcome may be unknown.
- **Explicit trust:** `SshConnectOptions` requires a `known_hosts` file, host-key alias, and a `DaemonCatalog` whose artifact hash is verified on every reconnect.
- **Platform note:** remote filesystem and normal process execution work on Linux, macOS, and Windows; remote PTY terminals use Unix shims and return `ENOTSUP` on Windows. See [Remote environments and daemon](/docs/embed/env/).

## Related guides

- [Providers and models (CLI)](/docs/guides/providers/)
- [Chat and providers (SDK)](/docs/embed/chat/)
- [Streaming UIs, watches & telemetry](/docs/patterns/streaming-and-observability/)
- [Walkthrough: Vision receipts & media studio](/docs/samples/multimodal-receipt-and-studio/)
