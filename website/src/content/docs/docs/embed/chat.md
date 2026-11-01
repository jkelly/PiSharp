---
title: Chat and providers
description: "Stream from any provider with PiSharp.AI: transports, models, auth and events."
---

`PiSharp.AI` is the chat layer. It sends a transcript to a model and gives you back a stream of events: text, thinking, tool calls and a final message with usage and cost. It ports Pi's [`packages/ai`](https://github.com/earendil-works/pi/tree/v1.2.0/packages/ai). It never runs tools and never looks up credentials on its own: you pass the model, the endpoint and the key. For the agent loop on top of it, see [Agent loop](/docs/embed/agent/).

## Install

```powershell
dotnet add package PiSharp.AI --version 1.2.0.1
```

`PiSharp.AI` brings `PiSharp.Contracts`, which holds the message and event types.

| Namespace | What is in it |
| --- | --- |
| `PiSharp.AI` | `ChatClient`, `ChatRun`, `ChatRequest`, `IChatTransport`, `IModelProvider`, `ThinkingLevels`, `StreamLimits` |
| `PiSharp.AI.Providers` | `NativeProviderFactory`, `NativeHttpModelProvider`, `ModelTransportRegistry` |
| `PiSharp.AI.Catalogs` | `FrozenModelCatalog`, `FrozenCatalogModel`, `BuiltinProviders`, `ProviderEnvironmentKeys` |
| `PiSharp.AI.Authentication` | `InjectedAuthenticationResolver`, `ProviderEnvironmentSnapshot` |
| `PiSharp.AI.Authentication.OAuth` | `IProviderOAuth`, the provider login classes, `StoredOAuthLifecycle` |
| `PiSharp.AI.Protocols.*` | Per-API option records, such as `PiSharp.AI.Protocols.OpenAICompletions.CompletionsKeyAuthRequestOptions` |
| `PiSharp.AI.ModelOperations` | Classifier and image APIs. See [Classifiers and images](/docs/embed/model-operations/). |
| `PiSharp.Contracts` | `ModelDescriptor`, `TranscriptEntry`, `AssistantMessage`, `StreamEvent` and its subtypes, `JsonData` |

## How it fits together

1. A `ModelDescriptor(Id, Api, Provider)` names a model, for example `("gpt-5", "openai-responses", "openai")`. The three parts together are the model's identity.
2. An `IChatTransport` streams that model. `NativeProviderFactory` builds one for each API Pi supports, bound to one model, one endpoint and one key.
3. A `ChatRequest` holds the model, the transcript and per-request options such as the thinking level.
4. `ChatClient.StartAsync` starts a `ChatRun`. You read its events with `ReadEventsAsync` and await `Completion` for the final `ChatResult`.

## A first stream

This program streams one answer from OpenRouter and prints the usage. It reads the key from `OPENROUTER_API_KEY`.

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
    ?? throw new InvalidOperationException("Set OPENROUTER_API_KEY.");
var model = new ModelDescriptor("anthropic/claude-sonnet-4.5", "openai-completions", "openrouter");
using var provider = NativeProviderFactory.CreateCompletions(model,
    new Uri("https://openrouter.ai/api/v1/chat/completions"), key);

var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
ImmutableArray<TranscriptEntry> messages =
[
    Entry("system", new { role = "system", content = "You answer in one short paragraph.", timestamp = now }),
    Entry("user", new { role = "user", content = "Why is the sky blue?", timestamp = now }),
];

var client = new ChatClient(provider);
await using var run = await client.StartAsync(new ChatRequest(model, messages, now));
await foreach (var observation in run.ReadEventsAsync())
{
    if (observation is TextDelta delta) Console.Write(delta.Delta);
}
Console.WriteLine();

var result = await run.Completion;
if (result.Failure is { } failure)
    Console.WriteLine($"Failed ({failure.Kind}): {failure.Message}");
var usage = result.Message.Usage;
Console.WriteLine($"{usage.Input} tokens in, {usage.Output} out, stop reason {result.Message.StopReason}");

static TranscriptEntry Entry(string role, object body) =>
    new(role, JsonData.Parse(JsonSerializer.Serialize(body)));
```

A run never throws for a provider failure. It always ends with a terminal event, and `Completion` holds the final message plus a `ChatFailure` when something went wrong. This sample passes no model metadata, so the cost is zero and the only thinking level is `off`. [The model catalog](#the-model-catalog) shows how to add both.

## Messages and stream events

### Transcript entries

A transcript is an `ImmutableArray<TranscriptEntry>`. Each `TranscriptEntry(string Role, JsonData WireBody)` holds one message as Pi's JSON, and the `role` in the body matches `Role`.

| Role | Wire body | Notes |
| --- | --- | --- |
| `system` | `{"role":"system","content":"...","timestamp":n}` | Add `toolsAdded` (an array of `{"name","description","parameters"}` declarations) to declare tools, and `toolsRemoved` to withdraw them. This is how tools reach the model. |
| `user` | `{"role":"user","content":"...","timestamp":n}` | `content` is a string or an array of `{"type":"text","text":"..."}` and image blocks. |
| `assistant` | The JSON form of an `AssistantMessage` | Write it with `PiWireJson.WriteMessage(message)` and read it with `PiWireJson.ReadMessage(element)`. |
| `toolResult` | A tool result message | The [agent loop](/docs/embed/agent/) writes these for you. |

`JsonData` is an immutable JSON value. `JsonData.Parse(string)` and `JsonData.FromElement(JsonElement)` create one, `Value` is the `JsonElement`, and `ToString()` returns the raw JSON.

### The assistant message

`AssistantMessage(Api, Provider, Model, Timestamp, Content, Usage, StopReason, ExtraProperties)` is the result of one request. `Content` is an array of blocks:

| Block | Members |
| --- | --- |
| `TextContent` | `Text` |
| `ThinkingContent` | `Thinking` |
| `ToolCallContent` | `Id`, `Name`, `Arguments` (a `JsonData` object) |

`StopReason` is one of:

| Value | Meaning |
| --- | --- |
| `Stop` | The model finished. |
| `Length` | The model hit its output token limit. |
| `ToolUse` | The model asked for tool calls. |
| `Error` | The provider failed. The error text is in `ExtraProperties["errorMessage"]`. |
| `Aborted` | The run was canceled. |
| `Pending`, `Deferred` | Not final; you see these only on partial messages. |

`DurationMs` is set when the `ChatClient` has a `TimeProvider` (see [Usage and cost](#usage-and-cost)).

### Stream events

`ReadEventsAsync` yields `StreamEvent` records in order. Every block has a start, deltas and an end, indexed by `ContentIndex`. The terminal event comes last.

| Event | Members | When |
| --- | --- | --- |
| `StreamStarted` | `Partial` | The stream opened. `Partial` is the empty assistant message. |
| `TextStarted`, `TextDelta`, `TextEnded` | `ContentIndex`, `Content` / `Delta` | A text block. |
| `ThinkingStarted`, `ThinkingDelta`, `ThinkingCheckpoint`, `ThinkingEnded` | `ContentIndex`, `Content` / `Delta` | A thinking block. |
| `ToolCallStarted`, `ToolCallProvisionalStarted`, `ToolCallHeaderUpdated`, `ToolCallDelta`, `ToolCallCheckpoint`, `ToolCallEnded` | `ContentIndex`, `ToolCall` / `Id`, `Name` / `Delta` / `Json` | A tool call. `ToolCallEnded.ToolCall` has the parsed arguments. |
| `ContentBlockFinalized` | `ContentIndex`, `Content` | A block's final value. |
| `StreamDone` | `Reason`, `Message` | The stream ended normally. |
| `StreamError` | `Reason`, `Message` | The stream ended with an error or an abort. |

`StreamDone` and `StreamError` both derive from `StreamTerminalEvent`, which also carries `NativeDiagnostic` and `NativeCleanupDiagnostic` for provider-specific failure details. `PiWireJson.WriteEvent` and `PiWireJson.ReadEvent` convert events to and from Pi's JSON event shape.

## Providers and transports

`NativeProviderFactory` creates a `NativeHttpModelProvider` for one model. The provider implements `IChatTransport`, `IModelProvider` and `IThinkingLevelTransport`. It owns its `HttpClient`, so dispose it when you are done. A `handler` you pass stays yours to dispose. The factory never discovers credentials or models.

| Method | Provider and API | Endpoint |
| --- | --- | --- |
| `CreateResponses(model, endpoint, key, projectionOptions, ...)` | `openai`, `openai-responses` | Full URL, such as `https://api.openai.com/v1/responses` |
| `CreateCompletions(model, endpoint, key, ...)` | `openai` or `openrouter`, `openai-completions` | Full URL, such as `https://openrouter.ai/api/v1/chat/completions` |
| `CreateAnthropic(model, endpoint, key, projectionOptions, ...)` | `anthropic`, `anthropic-messages` | Base URI, such as `https://api.anthropic.com` |
| `CreateMistral`, `CreateMistralSimple` | Mistral conversations | Base URI |
| `CreateAzureCompletions`, `CreateAzureResponses` | Azure OpenAI | From `AzureEndpointOptions` |
| `CreateBedrock(model, metadata, options, environment, ...)` | Amazon Bedrock | From the region in `BedrockConverseOptions` or the AWS environment |
| `CreateCodexResponses(model, metadata, options, accessToken, ...)` | OpenAI Codex (OAuth) | The row's `baseUrl` (default `https://chatgpt.com/backend-api`) plus `/codex/responses` |
| `CreateGoogleVertexRoute(model, metadata, auth, ...)` | Google Vertex | From the catalog row |
| `CreateProviderRoute(model, metadata, ProviderRouteOptions, ...)` | Any catalog provider, with an auth callback | From the catalog row |
| `CreateCatalogCompletions`, `CreateCatalogResponses`, `CreateCatalogAnthropic` | Any provider whose catalog row declares that API | `baseUrl` plus `/chat/completions`, `/responses` or the Anthropic path |
| `CreateCatalogMistral`, `CreateCatalogGoogle`, `CreateCatalogPiMessages` | Mistral, Google Generative AI, Pi messages | From the catalog row |

The fixed factories check the provider and API of the model you pass. The endpoint must be an absolute `http` or `https` URI with no user info or fragment. The key must be 1 to 4096 printable ASCII characters.

`CreateCompletions` fills in OpenRouter defaults when you pass no options: no developer role, `max_tokens` instead of `max_completion_tokens`, and no `store` field. `CompletionsKeyAuthRequestOptions` sets `MaxTokens`, `Temperature`, headers and the other request fields. The other APIs have their own option records in `PiSharp.AI.Protocols.*`.

### Several models behind one transport

`IModelProvider` pairs a provider id and its models with a transport. `ModelTransportRegistry` joins several providers into one `IChatTransport` and routes each request by the full model identity:

```csharp
using var openRouter = NativeProviderFactory.CreateCompletions(sonnet,
    new Uri("https://openrouter.ai/api/v1/chat/completions"), openRouterKey);
using var openAI = NativeProviderFactory.CreateResponses(gpt,
    new Uri("https://api.openai.com/v1/responses"), openAIKey,
    new ResponsesTranscriptProjectionOptions(Reasoning: true));

var registry = new ModelTransportRegistry([openRouter, openAI]);
var client = new ChatClient(registry);   // streams either model
```

The registry rejects a duplicate model and throws for a model it does not hold. `Resolve(model)` returns the transport for one model, and `Models` lists them all.

### Your own transport

Implement `IChatTransport.StreamAsync(ChatRequest, CancellationToken)` to plug in a provider PiSharp does not ship. Yield the block events and end with exactly one terminal event. A `StreamStarted` must carry an empty partial message whose stop reason is `Pending`. Implement `IThinkingLevelTransport` as well when your transport supports thinking levels.

`ChatClient` checks the protocol as it reduces your events into the final message. A second start, or any event after the terminal event, fails the run as `MalformedStream`. A stream that ends without a terminal event fails as `UnexpectedEof`.

## The model catalog

Pi ships a catalog of models per provider: id, API, base URL, context window, whether the model reasons, its input types and its token prices. `FrozenModelCatalog.ReadProviderJson(provider, utf8Json, options)` reads one provider's catalog file. `PiSharp.AI` has no public loader for a built-in copy: the CLI embeds the files from [`src/PiSharp.Cli/Models`](https://github.com/jkelly/PiSharp/tree/main/src/PiSharp.Cli/Models) for its own use. Copy the file you need, or read your own.

Pass the catalog row as the model's metadata. The transport then computes the cost of each request and knows which thinking levels the model supports:

```csharp
var catalog = FrozenModelCatalog.ReadProviderJson("openrouter", await File.ReadAllBytesAsync("openrouter.json"));
if (!catalog.TryGetModel(CatalogModelType.Chat, "anthropic/claude-sonnet-4.5", out var row))
    throw new InvalidOperationException("Model not in catalog.");

var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
using var provider = NativeProviderFactory.CreateCatalogCompletions(model, row.BaseUrl, key,
    new CompletionsTranscriptProjectionOptions(), new CompletionsKeyAuthRequestOptions(), row.Raw);

Console.WriteLine(string.Join(", ", ThinkingLevels.GetSupported(provider, model)));
```

`FrozenModelCatalog` exposes `Provider`, `DeclaredApis`, `Models`, `Raw` and `ProviderJsonSha256`. Each `FrozenCatalogModel` has:

| Member | Meaning |
| --- | --- |
| `Type` | `CatalogModelType.Chat`, `Image` or `Classifier` |
| `Id`, `Name`, `Provider` | The model's identity and display name |
| `DeclaredApi` | The API the row declares, such as `openai-completions` |
| `BaseUrl` | The provider base URL for that model |
| `Cost` | The price table, in dollars per million tokens |
| `DeclaresImageInput` | Whether `input` includes `image` |
| `Raw` | The whole row as `JsonData` |

`FrozenModelCatalogReadOptions` bounds the read: `MaximumUtf8Bytes` (2 MiB), `MaximumDepth` (32) and `MaximumModels` (4096). A bad file throws `CatalogReadException` with a `CatalogReadFailure`.

`BuiltinProviders.All` lists Pi's built-in providers. Each `BuiltinProviderDefinition` has the `Id`, display `Name`, default `BaseUrl`, the `ApiKeyVariables` it reads, its `ProviderAuthKind` and its `Apis`.

## Custom base URLs and providers

To point an OpenAI-compatible API at another host, pass your own endpoint to `CreateCompletions`. The model's provider must still be `openai` or `openrouter`.

For any other provider id, such as a local llama.cpp or vLLM server, write a catalog row yourself and use a catalog factory:

```csharp
var model = new ModelDescriptor("qwen3-coder", "openai-completions", "local");
var row = JsonData.Parse("""
    {
      "id": "qwen3-coder", "name": "Qwen3 Coder (local)", "api": "openai-completions", "provider": "local",
      "baseUrl": "http://localhost:8080/v1", "reasoning": false, "input": ["text"],
      "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 },
      "contextWindow": 32768, "maxTokens": 8192,
      "compat": { "supportsDeveloperRole": false, "supportsStore": false }
    }
    """);
using var provider = NativeProviderFactory.CreateCatalogCompletions(model, "http://localhost:8080/v1", "none",
    new CompletionsTranscriptProjectionOptions(), new CompletionsKeyAuthRequestOptions(), row);
```

The row's `id`, `api` and `provider` must match the model, and `baseUrl` must match the base URL you pass. For `openai-completions` the row also needs `reasoning`, `input` and `cost`. `compat` holds the same compatibility flags as Pi's `models.json`. [Configuration](/docs/reference/configuration/) lists them, and [llama.cpp](/docs/guides/llama-cpp/) shows a local server end to end.

## Authentication

The library never reads `auth.json` and never logs in for you. The CLI's credential store is internal to `PiSharp.Cli`. In your app you resolve the key and pass it to the factory.

### Environment keys

`ProviderEnvironmentKeys` knows which variables each built-in provider reads, in Pi's order. You pass the lookup:

```csharp
var key = ProviderEnvironmentKeys.GetEnvironmentApiKey("openrouter", Environment.GetEnvironmentVariable)
    ?? throw new InvalidOperationException("No OpenRouter key in the environment.");
```

| Member | Returns |
| --- | --- |
| `GetApiKeyVariables(provider)` | The variable names, such as `OPENROUTER_API_KEY`, or null for an unknown provider. |
| `FindEnvironmentKeys(provider, environment)` | The names that are set, or null. |
| `GetEnvironmentApiKey(provider, environment, fileExists, home)` | The first key that is set. For Anthropic it skips `ANTHROPIC_AUTH_TOKEN`, which is a bearer token rather than an API key. For Google Vertex and Amazon Bedrock it returns `ProviderEnvironmentKeys.AuthenticatedMarker` when ambient credentials exist instead of a key. |

[Environment variables](/docs/reference/environment/) lists every variable.

`InjectedAuthenticationResolver` resolves the same keys from a `ProviderEnvironmentSnapshot` and reports where each key came from. `ResolveAnthropicApiKeyAsync` follows Anthropic's order: a stored key, then `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_OAUTH_TOKEN`, `ANTHROPIC_API_KEY`, and last workload identity federation.

### OAuth logins

`PiSharp.AI.Authentication.OAuth` holds the subscription logins that `/login` uses in the CLI. Each implements `IProviderOAuth`:

| Member | Purpose |
| --- | --- |
| `LoginAsync(interaction, options, cancellationToken)` | Runs the login and returns an `OAuthCredentialSnapshot` (access token, refresh token, expiry). |
| `ToAuth(credential)` | Turns a credential into a `ProviderModelAuth(ApiKey, Headers, BaseUrl)` for requests. |
| `RefreshAsync(provider, current, cancellationToken)` | Exchanges the refresh token for a new credential. |
| `Name`, `IsSubscription`, `LoginLabel` | Display data. |

The implementations are `GitHubCopilotOAuth`, `OpenAICodexOAuth`, `OpenAIChatGPTOAuth`, `OpenRouterOAuth`, `KimiCodingOAuth`, `MetaOAuth`, `XaiOAuth`, `RadiusOAuth` and `AnthropicProviderOAuth`. Your app implements `IProviderAuthInteraction`: `PromptAsync(AuthPrompt, cancellationToken)` asks the user for text, and `Notify(AuthEvent)` shows a URL, a device code or progress.

To keep a token fresh, implement `IAdmittedOAuthCredentialSource` over your own storage and wrap it in `StoredOAuthLifecycle(credentials, refresh)`. `ResolveAsync(provider, ...)` returns the stored credential and refreshes it when it expires within five minutes (`DefaultMinimumValidityMilliseconds`).

## Thinking levels

Pi's levels, in order, are `off`, `minimal`, `low`, `medium`, `high`, `xhigh` and `max` (`ThinkingLevels.Ordered`). Each transport reports which ones a model supports:

| Member | Purpose |
| --- | --- |
| `ThinkingLevels.GetSupported(transport, model)` | The supported levels, in order. A transport that is not an `IThinkingLevelTransport` supports only `off`. |
| `ThinkingLevels.Clamp(available, level)` | The level if supported, else the nearest supported level above it, else the nearest below. |
| `ThinkingLevels.Validate(transport, model, level)` | Throws `ArgumentException` for an unsupported level. |

Set the level per request with `ChatRequest.ThinkingLevel`. Null keeps the transport's default. A native provider rejects an unsupported level, so clamp first:

```csharp
var level = ThinkingLevels.Clamp(ThinkingLevels.GetSupported(provider, model), "high");
var request = new ChatRequest(model, messages, now) { ThinkingLevel = level };
```

A provider built without catalog metadata supports only `off`. Pass the catalog row to get the model's real levels.

`ChatRequest.SessionId` names the session a request belongs to. Providers use it for prompt-cache keys and session affinity headers.

## Usage and cost

`AssistantMessage.Usage` is a `TokenUsage`:

| Member | Meaning |
| --- | --- |
| `Input`, `Output` | Uncached input tokens and output tokens |
| `CacheRead`, `CacheWrite` | Tokens read from and written to the prompt cache |
| `TotalTokens` | The provider's total |
| `Cost` | A `UsageCost` with `Input`, `Output`, `CacheRead`, `CacheWrite` and `Total`, in dollars |

The transport prices tokens with the `cost` rates in the model's catalog row, including prompt-length tiers. Without metadata the rates are zero.

To time each request, give the client a clock. `AssistantMessage.DurationMs` is then set on the final message:

```csharp
var client = new ChatClient(provider) { TimeProvider = TimeProvider.System };
```

## Aborting

A canceled run ends with a `StreamError` whose reason is `Aborted`, and `Completion` settles with that message and a `ChatFailure` of kind `Cancelled`. You can cancel a run in several ways:

- Cancel the token you passed to `StartAsync`, or call `run.Cancel()`. The reader keeps going and receives the terminal event.
- Cancel the token you passed to `ReadEventsAsync`. This cancels the run too, and the `await foreach` throws `OperationCanceledException`.
- Leave the `await foreach` early. This cancels the producer.
- Dispose the run (`await using`). Disposing cancels and waits for the producer.

`ReadEventsAsync` allows one reader. A second call throws `InvalidOperationException`.

Two other start methods change abort handling:

| Method | Behavior |
| --- | --- |
| `StartWithAbortSettlementAsync(request, cancellationToken, fallbackTimestamp)` | Keeps the provider's own aborted terminal message after cancellation. Drain it with a reader token that is not canceled. The agent loop uses this. |
| `StartWithDetachedReaderAsync(request, cancellationToken)` | Leaving the reader does not cancel. The run drains the rest itself until you dispose it. |

`CompleteAsync(request, cancellationToken)` starts a run, drains it and returns the `ChatResult` when you do not need the events.

## Errors and limits

`ChatResult.Failure` is null on success. Otherwise its `Kind` is:

| `ChatFailureKind` | Cause |
| --- | --- |
| `Provider` | The provider returned an error, or the transport threw. `Message` carries the provider's error text when there is one. |
| `Cancelled` | The run was aborted. |
| `MalformedStream` | The provider sent invalid JSON or broke the event protocol. |
| `UnexpectedEof` | The stream ended without a terminal event. |
| `ResourceLimit` | The stream went over its `StreamLimits`. |
| `Configuration` | The request or transport configuration is invalid. |

`ChatRun.CleanupFailure` reports a failure while cleaning up after the terminal event.

`ChatClient(transport, capacity, limits)` takes a buffer `capacity` (32 events by default) and `StreamLimits(MaximumBlocks, MaximumCharacters)`. By default the blocks are unbounded and the character limit is the longest string Node can hold (536,870,888), as in Pi.

## Service tiers

:::caution[Experimental and unreleased]
Service tiers are on PiSharp's `main` branch but not in any release or NuGet package yet. To use them, build PiSharp from source and reference the projects directly (see [Build from source](/docs/contribute/build/)). The API is marked `[Experimental("PISHARP_TIERS001")]`, so suppress `PISHARP_TIERS001` in your project to compile it, and it may change or be removed without notice.
:::

`ChatRequest.ServiceTier` picks a provider's processing tier for one request: `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast` (`ServiceTiers.All`). Each protocol maps the tier to its own field, and protocols without tiers ignore it. Null keeps the transport's own setting. [Service tiers](/docs/guides/service-tiers/) explains what each provider supports.

## Compared with Pi

- Pi's `stream()` and `complete()` functions look up the model and its key for you. In PiSharp you build the transport explicitly from a model, an endpoint and a key.
- Pi's built-in model list is a module export. `PiSharp.AI` reads the same catalog files, but you supply them.
- Events and messages have the same shape as Pi's. `PiWireJson` converts them to and from Pi's JSON.
- Service tiers are a PiSharp addition and are not in Pi v1.2.0.
