---
title: Classifiers and images
description: Call classifier and image models through the model-operations registry.
---

Some catalog models don't chat. Classifier models answer typed questions about a JSON state with probabilities, and image models turn a prompt into images. Pi calls these model operations and runs them through `Models.classify` and `Models.generateImages`. PiSharp ports both to `PiSharp.AI`, in the `PiSharp.AI.ModelOperations` namespace, with their request and result types in `PiSharp.Contracts.ModelOperations`.

The coding agent uses the same registry. Extensions reach it as `ctx.modelRegistry`, and code mode scripts as the `models` global. Chat models stay with the chat transports on [Chat and providers](/docs/embed/chat/).

:::note[The media library]
Embeddings, image editing, video, speech, transcription, music and reranking live in the `PiSharp.AI.Media` namespace (experimental, unreleased), on the [Media library](/docs/embed/media/) page. It is in `PiSharp.AI` on `main` but not in any release, and its API is marked `[Experimental("PISHARP_MEDIA001")]`. Its OpenAI and Google image APIs implement the same `IImagesApi` interface this page describes.
:::

## Install

```powershell
dotnet add package PiSharp.AI --version 1.2.0.1
```

`PiSharp.AI` brings `PiSharp.Contracts`. Add these namespaces:

```csharp
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
```

## Models

A catalog entry's `type` says what it is for: `"classifier"`, `"image"`, or `"chat"` when the field is absent. The registry reads entries of every type into records derived from `OperationModel`:

| Record | `Type` | Extra fields |
| --- | --- | --- |
| `ClassifierModel` | `ModelType.Classifier` | `ContextWindow` |
| `ImageModel` | `ModelType.Image` | `Output`: always holds `"image"`; `"text"` too when the model can also answer in text |
| `ChatModel` | `ModelType.Chat` | none. Listed by the typed reads only; chat requests go to the chat transports. |

Every `OperationModel` has `Id`, `Name`, `Api`, `Provider`, `BaseUrl`, `Input` (`"text"` and `"image"`), `Cost` and `Headers`. `AcceptsImages` is true when `Input` contains `"image"`. `Metadata` keeps the whole catalog object. `ToJson()` returns that object with the current `BaseUrl`, and `ToPublicJson()` drops `headers`, which can carry credentials.

`OperationModel.FromJson(JsonData)` reads one catalog object and `OperationModel.FromCatalog(FrozenCatalogModel)` one entry of a released catalog shard. Both throw `FormatException` for a malformed entry.

`ModelCost` holds the rates per million tokens (`Input`, `Output`, `CacheRead`, `CacheWrite`) and optional `Tiers`. When a request's input, cache reads and cache writes together exceed a tier's `InputTokensAbove`, the highest such tier prices the whole request. `ModelCost.Free` is all zeros.

## Built-in APIs

`model.Api` picks the implementation. PiSharp ports Pi's five built-in APIs:

| API id | Class | Request | Images in | Temperature |
| --- | --- | --- | --- | --- |
| `openai-decisions` | `OpenAIDecisionsClassifier` | `POST {baseUrl}/decisions` | Up to 128 (`MaxImages`) | No |
| `typesafe-system-one` | `TypeSafeSystemOneClassifier` | `POST {baseUrl}/systemone` | No | No |
| `cloudflare-workers-ai-system-one` | `CloudflareWorkersAISystemOneClassifier` | `POST {baseUrl}/run` | No | No |
| `llama-cpp-classify` | `LlamaCppClassifier` | `/tokenize`, `/apply-template` and `/completion` on the llama-server root | No | Yes, default 1 |
| `openrouter-images` | `OpenRouterImages` | `POST {baseUrl}/chat/completions`, not streamed | Reference images | Not used |

Each class has a static `Instance`, an `ApiId` constant and an `Api` property.

- **OpenAI Decisions** sends the state as JSON text. With images, the input becomes one user message with the state and the images as data URLs. A `bool` question becomes a Decisions `predicate`, with the meanings of true and false added to its instructions. A 504 from the gateway is not retried. Only OpenAI API keys work on this route: Sign in with ChatGPT tokens are rejected.
- **System One** is TypeSafe's protocol. OpenRouter, OpenCode Zen, Vercel AI Gateway and llama.cpp decision models serve it at their own base URLs. Cloudflare Workers AI wraps it in its `run` envelope and fills `{CLOUDFLARE_ACCOUNT_ID}` and `{CLOUDFLARE_GATEWAY_ID}` in the base URL from the provider environment (`CloudflareWorkersAISystemOneClassifier.ResolveModel`). A `bool` question travels as System One's `noul` type.
- **llama.cpp** classifies with an ordinary chat model. Each question becomes one prompt that lists the answers under single-token labels: letters for a choice (2 to 62 options), digits for a score (2 to 10 levels), `Yes` and `No` for a bool. The server returns the log-probabilities of the next token, and the answer is the softmax over the label tokens. The model never generates text. `Temperature` divides the log-probabilities before the softmax.
- **OpenRouter images** asks for `modalities: ["image"]`, or `["image", "text"]` when the model's `Output` includes `"text"`. The reply's text becomes an `ImagesTextBlock` and each `data:` image URL an `ImageContent`. Other URLs are skipped.

The System One APIs and llama.cpp return an error result for a context with images, also for models whose catalog entry lists image input.

## Built-in providers

`BuiltinModelOperationProviders.Create(providerId, models)` builds Pi's provider definition for a provider id. `BuiltinModelOperationProviders.ProviderIds` lists the six ids that serve classifier or image models:

| Provider | Classifier API | Image API | Key from the environment |
| --- | --- | --- | --- |
| `openai` | `openai-decisions` | | `OPENAI_API_KEY` |
| `openrouter` | `typesafe-system-one` | `openrouter-images` | `OPENROUTER_API_KEY` |
| `typesafe` | `typesafe-system-one` | | `TYPESAFE_API_KEY` |
| `cloudflare-workers-ai` | `cloudflare-workers-ai-system-one` | | `CLOUDFLARE_API_KEY` and `CLOUDFLARE_ACCOUNT_ID` |
| `opencode` | `typesafe-system-one` | | `OPENCODE_API_KEY` |
| `vercel-ai-gateway` | `typesafe-system-one` | | `AI_GATEWAY_API_KEY` |

Another id gives a provider that lists its models and has no APIs. When `openai` has an OAuth credential, `GetAvailableOfTypeAsync` lists none of its classifier models (its `FilterAllModels`), because Decisions rejects those tokens.

`BuiltinModelOperationProviders.LlamaCpp(models, source)` is the provider of the built-in llama.cpp extension, id `llama.cpp`. It serves `llama-cpp-classify` and `typesafe-system-one`. You discover the models yourself. Pass `source` when the list changes at runtime: the registry then reads it on every lookup. See [llama.cpp](/docs/guides/llama-cpp/) for how the CLI finds them.

## Create a registry

`ModelOperationsRegistry` holds providers and resolves auth on every call. It is thread-safe, and you can add, replace or remove providers at any time.

`CreateBuiltin` takes released catalog shards, one provider each, and builds the built-in provider definitions over them. The shards are the JSON files of Pi's model catalog (`providers/data/<provider>.json` in `@earendil-works/pi-ai` 1.2.0). The CLI embeds the same files in [`src/PiSharp.Cli/Models`](https://github.com/jkelly/PiSharp/tree/main/src/PiSharp.Cli/Models). [Chat and providers](/docs/embed/chat/) covers `FrozenModelCatalog`.

```csharp
using PiSharp.AI.Catalogs;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

var catalogs = new[] { "openai", "openrouter", "typesafe" }.Select(provider =>
    FrozenModelCatalog.ReadProviderJson(provider, File.ReadAllBytes(Path.Combine("catalog", provider + ".json"))));

var registry = ModelOperationsRegistry.CreateBuiltin(catalogs, ModelOperationsAuth.Standard(Environment.GetEnvironmentVariable));

foreach (var model in await registry.GetAvailableOfTypeAsync(ModelType.Classifier))
    Console.WriteLine($"{model.Provider}/{model.Id}");
```

`GetAvailableOfTypeAsync` lists only the models whose provider has a credential. `GetModelsOfType` lists them all.

You can also write the model yourself and add its provider:

```csharp
var jev = new ClassifierModel("jev-latest", "Jev", TypeSafeSystemOneClassifier.ApiId, "typesafe",
    "https://api.typesafe.ai/v1/", ["text"], ModelCost.Free, ContextWindow: 64000);

var registry = new ModelOperationsRegistry(ModelOperationsAuth.Standard(Environment.GetEnvironmentVariable));
registry.SetProvider(BuiltinModelOperationProviders.Create("typesafe", [jev]));
```

## Classify

A `ClassifierContext` holds the state, a JSON object, and the questions by id. Ids must be unique, and their order is kept.

```csharp
var context = new ClassifierContext(
    JsonData.Parse("""{"title":"Build fails on Linux","body":"Since yesterday dotnet build exits with code 1."}"""),
    [
        new("kind", new ClassifierChoiceQuestion("What kind of report is this?",
            [new("bug", "Something that worked is broken"), new("question", "Asks how to do something"), new("feature", "Asks for new behavior")])),
        new("severity", new ClassifierScoreQuestion("How severe is the problem?", ["cosmetic", "annoying", "blocking"])),
        new("actionable", new ClassifierBoolQuestion("Can a maintainer act on it without asking for more details?",
            "It says what fails and how", "Details are missing")),
    ]);

var result = await registry.ClassifyAsync(jev, context);
if (result.StopReason != ModelOperationStopReason.Stop)
{
    Console.WriteLine($"{result.StopReason}: {result.ErrorMessage}");
    return;
}

if (result.GetAnswer("kind") is ClassifierChoiceAnswer kind)
    Console.WriteLine($"kind: {kind.Choice}, confidence {kind.Confidence:F2}");
if (result.GetAnswer("severity") is ClassifierScoreAnswer severity)
    Console.WriteLine($"severity: {severity.Score:F2} on a scale from 0 to 2");
if (result.GetAnswer("actionable") is ClassifierBoolAnswer actionable)
    Console.WriteLine($"actionable: {actionable.Probability:P0}");
if (result.Usage is { } usage)
    Console.WriteLine($"cost: ${usage.Cost.Total}");
```

| Question | Criteria | Answer |
| --- | --- | --- |
| `ClassifierChoiceQuestion(Instructions, Criteria)` | Each label with its meaning, in order. A meaning may be empty. | `ClassifierChoiceAnswer(Choice, Probabilities, Confidence)`: the chosen label, the probability of every label, and the confidence |
| `ClassifierScoreQuestion(Instructions, Criteria)` | The levels, lowest first | `ClassifierScoreAnswer(Score, Confidence)`: the expected level index |
| `ClassifierBoolQuestion(Instructions, True, False)` | The meanings of true and false | `ClassifierBoolAnswer(Probability)`: the probability of true |

Pass images in the context's third parameter, `Images`, as `ImageContent(Data, MimeType)` with base64 data. The registry returns an error result when the model doesn't accept images.

`ClassifierResult` carries `Api`, `Provider`, `Model`, `Answers`, `StopReason`, `Timestamp` (Unix milliseconds), `Usage` and `ErrorMessage`. `GetAnswer(id)` returns one answer or null.

## Generate images

An `ImagesContext` is a list of `ImagesTextBlock` prompts and optional `ImageContent` references, in order.

```csharp
var model = registry.GetModelOfType(ModelType.Image, "openrouter", "google/gemini-2.5-flash-image") as ImageModel
    ?? throw new InvalidOperationException("The image model is not in the catalog.");

var result = await registry.GenerateImagesAsync(model, new ImagesContext([new ImagesTextBlock("A lighthouse at dusk, in watercolor")]));

var count = 0;
foreach (var block in result.Output)
{
    if (block is ImagesTextBlock text) Console.WriteLine(text.Text);
    else if (block is ImageContent image)
        await File.WriteAllBytesAsync($"image-{++count}.{image.MimeType.Split('/')[1]}", Convert.FromBase64String(image.Data));
}
if (result.StopReason != ModelOperationStopReason.Stop) Console.WriteLine(result.ErrorMessage);
```

`AssistantImages` carries `Api`, `Provider`, `Model`, `Output`, `StopReason`, `Timestamp`, `ResponseId`, `Usage` and `ErrorMessage`.

## Request options

`ClassifierOptions` and `ImagesOptions` derive from `ModelRequestOptions`. Every option is optional.

| Option | Meaning |
| --- | --- |
| `ApiKey` | Sent as `Authorization: Bearer`. On a registry call it replaces the stored or environment credential. |
| `Headers` | Merged over the auth, model and API default headers, ignoring case. A null value removes a header of that name. |
| `Env` | Provider-scoped values, for example `CLOUDFLARE_ACCOUNT_ID`. They overlay the resolved provider environment. |
| `OnPayload` | Receives a copy of the request body before it is sent. Return a replacement, or null to keep it. |
| `OnResponse` | Observes the status and lower-cased headers of an accepted response. |
| `TimeoutMs` | Timeout of each attempt, in milliseconds. |
| `MaxRetries` | Client retries. Classifiers default to 2, image generation to 0. |
| `MaxRetryDelayMs` | Longest `retry-after` the client waits; a longer one fails at once. Default 60000; 0 removes the cap. |
| `Http` | The `HttpMessageInvoker` to send with. Default: a shared client that does not follow redirects. |
| `TimeProvider` | Clock for retry delays, timeouts and result timestamps. Default `TimeProvider.System`. |
| `TransformHeaders` | Registry calls only. Rewrites the merged headers last, before the API adds its own defaults. |
| `ClassifierOptions.Temperature` | Divides the answer logits before they become probabilities. Must be positive. Only `llama-cpp-classify` applies it. |
| `ImagesOptions.Metadata` | Request metadata. `openrouter-images` sends none of it. |

Retries cover timed-out attempts and the statuses 408, 409, 429 and 500 and above. An `x-should-retry` response header of `true` or `false` overrides that. The client waits as long as `retry-after-ms` or `retry-after` asks, up to `MaxRetryDelayMs`, and otherwise backs off from about 0.5 to 8 seconds. Cancel the call with its `CancellationToken`.

```csharp
var result = await registry.ClassifyAsync(jev, context, new ClassifierOptions
{
    TimeoutMs = 30_000,
    MaxRetries = 1,
    Headers = [new("x-request-source", "triage-bot")],
});
```

## Auth

The registry's constructor takes a `ProviderAuthResolver`: a delegate that turns a `ProviderAuthRequest` into a `ProviderAuthResult`, or null when the provider is not configured.

| `ProviderAuthRequest` | Meaning |
| --- | --- |
| `Provider` | The provider id. |
| `ApiKey` | An explicit request key. It takes the place of a stored credential. |
| `Env` | Request environment values over the provider environment. |
| `Check` | An availability check from `GetAvailableOfTypeAsync`. Only a non-null result and `IsOAuth` matter, so an OAuth credential need not be refreshed. |

| `ProviderAuthResult` | Meaning |
| --- | --- |
| `ApiKey` | The key or token to send. |
| `Headers` | Auth headers, merged under the model and request headers. |
| `Env` | The provider environment the API sees, for example `CLOUDFLARE_ACCOUNT_ID`. |
| `BaseUrl` | Replaces the model's base URL, for example a resolved account endpoint. |
| `IsOAuth` | True for an OAuth token. Providers use it to filter models. |

`ToString()` of a result never shows the secrets.

`ModelOperationsAuth.Standard(readEnvironment, storedCredential)` is the resolver Pi uses. It resolves an explicit request key first, then the stored API key credential that `storedCredential` returns, then the provider's environment variable. Two providers differ:

- `cloudflare-workers-ai` needs `CLOUDFLARE_ACCOUNT_ID` as well, from the credential's environment or the process environment, and passes it on in `Env`.
- `llama.cpp` is configured by its server URL, `LLAMA_BASE_URL`. The key is the credential's, then `LLAMA_API_KEY`, then `local`. The result's `BaseUrl` is the server URL with `/v1`.

The CLI also resolves credentials from `auth.json`, including OAuth refreshes, before it falls back to `Standard`. To do the same, write your own resolver and call `Standard` from it for the providers you don't handle.

`GetAuthAsync(provider, apiKey, env)` resolves auth for a listed provider and returns null for an unknown one. `ResolveAuthAsync(request)` calls the resolver for any provider id, listed or not.

## Results, not exceptions

`ClassifyAsync` and `GenerateImagesAsync` never throw for a failed request. An unknown provider, a missing credential, an HTTP error, a malformed response or a cancelled token all come back as a result with `StopReason` set to `Error` or `Aborted`, an `ErrorMessage` and no answers or output. `Usage` is set whenever the service reported token counts, also for a failed request that was billed. It is priced at the model's catalog rates.

Before it dispatches, the registry resolves the auth of `model.Provider` and applies it: explicit option values win per field, a resolved base URL replaces the model's, and the provider's model rewrite runs last. The model itself is used as given. A model with another base URL therefore receives that provider's credentials, as in Pi.

## Registry reference

| Member | Description |
| --- | --- |
| `new ModelOperationsRegistry(auth)` | An empty registry with an auth resolver. |
| `CreateBuiltin(catalogs, auth)` | A registry of the built-in provider definitions over released catalog shards. |
| `SetProvider(provider)` | Adds or replaces a provider. |
| `RemoveProvider(id)` | Removes a provider. Returns false when there was none. |
| `GetProvider(id)`, `GetProviders()` | One provider, or all in registration order. |
| `GetAllModels(provider)` | Models of every type, of one provider or of all. |
| `GetModelsOfType(type, provider)` | Models of one type. |
| `GetModelOfType(type, provider, id)` | One model, or null. |
| `GetAvailableOfTypeAsync(type, provider, token)` | Models of one type whose provider has auth, after its credential filter. |
| `GetAuthAsync(provider, apiKey, env, token)` | Request auth for a listed provider. |
| `ResolveAuthAsync(request, token)` | Request auth for any provider id. |
| `ClassifyAsync(model, context, options, token)` | Classifies. Never throws for request failures. |
| `GenerateImagesAsync(model, context, options, token)` | Generates images. Never throws for request failures. |

## Add your own API

Implement `IClassifierApi` or `IImagesApi`. An implementation reports failures as error results instead of throwing.

```csharp
using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

public sealed class KeywordClassifier : IClassifierApi
{
    public const string ApiId = "keyword-classify";
    public string Api => ApiId;

    public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var state = context.State.ToString();
        var answers = ImmutableArray.CreateBuilder<KeyValuePair<string, ClassifierAnswer>>();
        foreach (var (id, question) in context.Questions)
            if (question is ClassifierBoolQuestion)
                answers.Add(new(id, new ClassifierBoolAnswer(state.Contains("error", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.1)));
        var timestamp = (options?.TimeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        return Task.FromResult(new ClassifierResult(model.Api, model.Provider, model.Id, answers.ToImmutable(),
            ModelOperationStopReason.Stop, timestamp));
    }
}
```

Register it on a provider of your own. The registry resolves auth for every call, so the resolver must return a result for that provider:

```csharp
var standard = ModelOperationsAuth.Standard(Environment.GetEnvironmentVariable);
var registry = new ModelOperationsRegistry((request, token) => request.Provider == "local"
    ? ValueTask.FromResult<ProviderAuthResult?>(new ProviderAuthResult(ApiKey: null))
    : standard(request, token));

var keywords = new ClassifierModel("keywords-1", "Keywords", KeywordClassifier.ApiId, "local", "", ["text"], ModelCost.Free, 0);
registry.SetProvider(new ModelOperationsProvider("local", "Local rules")
{
    Models = [keywords],
    Classifiers = ImmutableDictionary<string, IClassifierApi>.Empty.Add(KeywordClassifier.ApiId, new KeywordClassifier()),
});
```

| `ModelOperationsProvider` | Meaning |
| --- | --- |
| `Id`, `Name` | The provider id that models name in `Provider`, and its display name. |
| `Models` | Its models of every type. |
| `ModelSource` | Read on every lookup instead of `Models`, for a catalog that changes at runtime. |
| `Classifiers`, `Images` | Implementations by API id. |
| `ResolveModel` | Rewrites the model with the resolved provider environment before dispatch. |
| `FilterAllModels` | The models usable with the resolved credential. |

### Call an API directly

`ModelOperationApis` dispatches on `model.Api` without a registry or auth lookup, as Pi's `generateImages()` does. Pass the key in the options. It holds the five built-in APIs, and `RegisterClassifierApi` and `RegisterImagesApi` add yours. Unlike the registry, `ClassifyAsync` and `GenerateImagesAsync` here throw `InvalidOperationException` when no API is registered for the model's API.

```csharp
var result = await ModelOperationApis.ClassifyAsync(jev, context,
    new ClassifierOptions { ApiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") });
```

## Providers from extensions

A native extension adds classifier and image providers with `IExtensionModelOperationProviderRegistry.RegisterModelOperationProvider`, as `pi.registerProvider` with `classifiers` and `images` does in Pi. The models join `ctx.modelRegistry` and code mode's `models`, and leave when the extension unloads. Implementations receive the model as its catalog JSON object.

```csharp
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

public sealed class KeywordRulesExtension : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        if (registry is IExtensionModelOperationProviderRegistry providers)
            providers.RegisterModelOperationProvider(new ExtensionModelOperationProvider("keyword-rules")
            {
                DisplayName = "Keyword rules",
                ApiKey = "none",
                Models = [JsonData.Parse("""{"type":"classifier","id":"rules-1"}""")],
                Classifiers = ImmutableDictionary<string, ExtensionClassifierImplementation>.Empty.Add("keyword-rules", ClassifyAsync),
            });
        return ValueTask.CompletedTask;
    }

    private static Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context,
        ExtensionModelRequestOptions options, CancellationToken cancellationToken)
    {
        var state = context.State.ToString();
        ImmutableArray<KeyValuePair<string, ClassifierAnswer>> answers = [.. context.Questions
            .Where(question => question.Value is ClassifierBoolQuestion)
            .Select(question => KeyValuePair.Create(question.Key,
                (ClassifierAnswer)new ClassifierBoolAnswer(state.Contains("error", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.1)))];
        return Task.FromResult(new ClassifierResult("keyword-rules", "keyword-rules", model.Value.GetProperty("id").GetString()!, answers,
            ModelOperationStopReason.Stop, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

A model config needs `type` and `id`. `provider` is the registration's name, and the rest defaults: `api` to the first implementation of its type, `baseUrl` to the provider's `BaseUrl`, `name` to the id, `input` to `["text"]` and `cost` to zero. `ApiKey` is an environment variable name or a literal key. A registration with a built-in provider's id adds its implementations to that provider's, and its models replace the built-in ones when it lists any. See [C# extensions](/docs/extensions/build-an-extension/#classifier-and-image-providers) and the [C# extension API](/docs/extensions/api/).

## JSON shapes

`ModelOperationJson` reads and writes Pi's JSON shapes of contexts and results, for bridges that pass them as JSON: code mode, Node extensions and RPC.

| Method | Shape |
| --- | --- |
| `ParseClassifierContext`, `WriteClassifierContext` | `{ state, images?, questions }` |
| `ParseClassifierResult`, `WriteClassifierResult` | `{ api, provider, model, answers, stopReason, timestamp, usage?, errorMessage? }` |
| `ParseImagesContext`, `WriteImagesContext` | `{ input: [text or image blocks] }` |
| `ParseAssistantImages`, `WriteAssistantImages` | `{ api, provider, model, output, stopReason, timestamp, responseId?, usage?, errorMessage? }` |
| `WriteUsage` | `{ input, output, cacheRead, cacheWrite, totalTokens, cost }` |
| `StopReasonName` | `stop`, `error` or `aborted` |

The parse methods throw `FormatException` for another shape. Object keys follow JavaScript property order, so a round trip keeps the order a script wrote.

## Where else the registry appears

- Code mode's `models` global calls an `ICodemodeModelRuntime`. [Code mode library](/docs/embed/codemode/#models-in-scripts) shows an adapter over this registry.
- `NativeDurableModelsOptions.ModelOperations` gives a durable session your registry for code mode's `models.*`. See [Durable library](/docs/embed/durable/).
