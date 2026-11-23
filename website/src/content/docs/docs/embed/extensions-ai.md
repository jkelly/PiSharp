---
title: Microsoft.Extensions.AI bridge
description: Use PiSharp transports through Microsoft.Extensions.AI IChatClient and IEmbeddingGenerator, or run PiSharp.Agent over any IChatClient.
---

`PiSharp.Extensions.AI` provides a bidirectional bridge between PiSharp's streaming LLM layer (`PiSharp.AI`) and the standard .NET `Microsoft.Extensions.AI` abstractions:

- **`PiChatClient`** exposes any PiSharp `IChatTransport` (OpenAI Responses, Anthropic Messages, Google Gemini, Bedrock Converse, Mistral, OpenRouter, Azure OpenAI, Codex, or custom transports) as a `Microsoft.Extensions.AI.IChatClient`.
- **`ChatClientTransportAdapter`** exposes any `Microsoft.Extensions.AI.IChatClient` (Azure OpenAI SDK, OllamaSharp, GitHub Models, Semantic Kernel, or custom middleware stacks) as a PiSharp `IChatTransport`, so `PiSharp.Agent`, `PiSharp.CodingAgent`, `PiSharp.Durable`, and `PiSharp.CustomAgents` can run directly over it.
- **`PiEmbeddingGenerator`** exposes `PiSharp.AI.Media.IEmbeddingsApi` as `IEmbeddingGenerator<string, Embedding<float>>` (marked `[Experimental("PISHARP_MEDIA001")]` to match the media contract).
- **`ServiceCollectionExtensions`** registers clients, keyed multi-model clients, adapted transports, and embedding generators in ASP.NET Core and Generic Host DI containers.

The package depends only on `Microsoft.Extensions.AI.Abstractions` (10.11.1) and `Microsoft.Extensions.DependencyInjection.Abstractions` (10.0.0), with zero additional third-party dependencies on `net10.0`.

## Install

```powershell
dotnet add package PiSharp.Extensions.AI --version {{PISHARP_VERSION}}
```

## Use a PiSharp transport as `IChatClient`

Wrap any PiSharp `IChatTransport` and `ModelDescriptor` in `PiChatClient` to plug PiSharp's 40+ catalog providers, service tiers, and reasoning controls into `Microsoft.Extensions.AI` middleware (caching, logging, OpenTelemetry, or function invocation):

```csharp
using Microsoft.Extensions.AI;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Extensions.AI;

IChatTransport transport = /* e.g. OpenAIResponsesTransport or AnthropicMessagesTransport */;
var model = new ModelDescriptor("gpt-5.4", "openai-responses", "openai");

using IChatClient client = new PiChatClient(transport, model, new PiChatClientOptions
{
    ServiceTier = ServiceTiers.Priority,
    ThinkingLevel = "medium"
});

// Non-streaming completion
var response = await client.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Summarize the benefits of append-only sessions.")],
    new ChatOptions
    {
        Instructions = "Reply in two sentences.",
        ConversationId = "session-001",
        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High }
    });

Console.WriteLine(response.Text);

// Streaming completion
await foreach (var update in client.GetStreamingResponseAsync(
    [new ChatMessage(ChatRole.User, "Stream three bullet points.")]))
{
    Console.Write(update.Text);
}
```

### Mapping between `ChatOptions` and `ChatRequest`

| `Microsoft.Extensions.AI` | `PiSharp.AI.ChatRequest` |
| --- | --- |
| `ChatOptions.ModelId` | Overrides `ModelDescriptor.Id` |
| `ChatOptions.Instructions` | Leading `system` transcript entry |
| `ChatOptions.Tools` (`AIFunctionDeclaration` / `AIFunction`) | `toolsAdded` JSON Schema entries |
| `ChatOptions.ConversationId` | `ChatRequest.SessionId` |
| `ChatOptions.Reasoning.Effort` | `ChatRequest.ThinkingLevel` (`None` -> `off`, `Low` -> `low`, `Medium` -> `medium`, `High` -> `high`, `ExtraHigh` -> `xhigh`) |
| `AdditionalProperties["pisharp.thinkingLevel"]` | Explicit PiSharp thinking level (overrides `Reasoning.Effort`) |
| `AdditionalProperties["pisharp.serviceTier"]` | `ChatRequest.ServiceTier` (`ServiceTiers.*`) |
| `AdditionalProperties["pisharp.sessionId"]` | `ChatRequest.SessionId` |
| `AdditionalProperties["pisharp.disableContinuation"]` | `ChatRequest.DisableContinuation` |
| `AdditionalProperties["pisharp.identity"]` | `ChatRequest.Identity` (`ClientIdentity`) |

`DataContent` with an `image/*` media type maps to PiSharp `image` content blocks, `TextReasoningContent` maps to PiSharp `thinking` blocks (preserving `ProtectedData` <-> `thinkingSignature`), and `FunctionCallContent` / `FunctionResultContent` map cleanly to `toolCall` / `toolResult` entries (automatically resolving `toolName` from the prior assistant call).

## Run `PiSharp.Agent` over any `IChatClient`

Use `ChatClientTransportAdapter` to adapt any `Microsoft.Extensions.AI.IChatClient` into a PiSharp `IChatTransport`. The adapter projects PiSharp's incremental system prompt sections and `toolsAdded` / `toolsRemoved` deltas into `ChatOptions.Tools`, emits `AssistantStreamReducer`-compliant `StreamEvent` frames, and converts errors or cancellations into `StreamError` terminals:

```csharp
using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions.AI;

IChatClient externalClient = /* OllamaSharp, Azure OpenAI SDK, GitHub Models, etc. */;
using var transport = new ChatClientTransportAdapter(externalClient);

var model = new ModelDescriptor("llama3.2", "meai", "ollama");
await using var agent = new Agent(
    new AgentConfiguration(model, transport, tools),
    () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    eventSink);
```

## Dependency injection (`IServiceCollection`)

Register clients, keyed multi-model clients, or reverse-adapted transports directly in `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Extensions.AI;

var services = new ServiceCollection();
var primary = new ModelDescriptor("gpt-5.4", "openai-responses", "openai");
var fast = new ModelDescriptor("gpt-5.4-mini", "openai-responses", "openai");

services.AddPiSharpChatClient(sp => CreateOpenAiTransport(sp), primary);
services.AddKeyedPiSharpChatClient("fast", sp => CreateOpenAiTransport(sp), fast, new PiChatClientOptions
{
    ServiceTier = ServiceTiers.Fast
});

// Or expose an existing IChatClient as a PiSharp IChatTransport:
// services.AddPiSharpChatClientTransport();
```

## Embeddings (`PiEmbeddingGenerator`)

`PiEmbeddingGenerator` and `AddPiSharpEmbeddingGenerator` wrap `PiSharp.AI.Media.IEmbeddingsApi` as `IEmbeddingGenerator<string, Embedding<float>>`. Because the underlying media APIs are experimental, suppress `PISHARP_MEDIA001` where you construct or register them:

```csharp
#pragma warning disable PISHARP_MEDIA001
using Microsoft.Extensions.AI;
using PiSharp.AI.Media;
using PiSharp.Extensions.AI;

var model = new MediaModel(MediaKind.Embedding, "openai", "text-embedding-3-small", "openai-embeddings") { Dimensions = 1536 };
var endpoint = new MediaEndpoint("https://api.openai.com/v1", Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

using IEmbeddingGenerator<string, Embedding<float>> generator = new PiEmbeddingGenerator(embeddingsApi, model, endpoint);
var result = await generator.GenerateAsync(["Durable agents commit every step."]);
```
