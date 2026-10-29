---
title: Media library
description: Embeddings, images, video, speech, transcription, music, rerank and decisions through PiSharp.AI.Media. Experimental and unreleased.
sidebar:
  badge:
    text: Unreleased
    variant: caution
---

:::caution[Experimental and unreleased]
The media library is on PiSharp's `main` branch but not in any release or NuGet package yet. To use it, build PiSharp from source and reference the projects directly (see [Build from source](/docs/contribute/build/)). Its API is marked `[Experimental("PISHARP_MEDIA001")]`, so suppress `PISHARP_MEDIA001` in your project to compile it, and it may change or be removed without notice.
:::

The `PiSharp.AI.Media` namespace, in the `PiSharp.AI` project, gives you one registry for eight kinds of model: embeddings, images, video, speech, transcription, music, rerank and decisions. You name a model as `provider/model`, the registry finds its API, credentials and base URL, and you get a result back. It builds on [model operations](/docs/embed/model-operations/): the same auth resolver, request options and image contract. The [media tools](/docs/guides/media/) of `pisharp durable` are built on it. Pi has no media library; this is a PiSharp addition ([decision 0007](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0007-media-generation.md)).

## Install

Reference `PiSharp.AI` from your clone and suppress the diagnostic (see [Reference the projects from your app](/docs/contribute/build/#reference-the-projects-from-your-app)):

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <NoWarn>$(NoWarn);PISHARP_MEDIA001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="../PiSharp/src/PiSharp.AI/PiSharp.AI.csproj" />
</ItemGroup>
```

The request and result types for images (`ImagesContext`, `ImagesTextBlock`, `ImageContent`, `AssistantImages`) and `ModelOperationStopReason` live in `PiSharp.Contracts.ModelOperations`. `ProviderAuthResolver` and `ModelRequestOptions` live in `PiSharp.AI.ModelOperations`.

## Create a registry

```csharp
using PiSharp.AI.Media;

var media = new MediaRegistry(MediaAuth.Environment(Environment.GetEnvironmentVariable));

MediaModel Resolve(MediaKind kind, string spec) =>
    media.TryResolve(kind, spec, out var model, out var error) ? model : throw new InvalidOperationException(error);
```

`MediaAuth.Environment` resolves a provider's key from the first of its API key variables that is set, such as `OPENAI_API_KEY`, `GEMINI_API_KEY`, `OPENROUTER_API_KEY` or `MISTRAL_API_KEY`. A key in the request options wins. Pass your own `ProviderAuthResolver` for stored credentials or OAuth; see [Auth and base URLs](#auth-and-base-urls).

| `MediaRegistryOptions` | Meaning |
| --- | --- |
| `Models` | The listed models. Null lists `MediaCatalog.Builtin`. |
| `ProviderBaseUrl` | `Func<string, string?>`: a provider's base URL when neither the model nor its auth names one. |
| `Http` | The HTTP transport for requests whose options name none. Null uses the shared model-operation client. |
| `TimeProvider` | Clock for video polling and timestamps. |

## Find a model

| Member | What it does |
| --- | --- |
| `GetModels(kind, provider)` | The listed models, filtered by kind and provider when given, in listing order. |
| `GetAvailableAsync(kind, cancellationToken)` | The listed models whose providers have auth. Each provider is checked once per call. |
| `TryResolve(kind, spec, out model, out error)` | The model a spec names. See below. |
| `AddModels(models)` | Lists more models. One with the kind and spec of a listed model replaces it in place. |
| `DiscoverAsync(kind, provider, cancellationToken)` | Every model of a kind OpenRouter offers now, from its model list filtered by output modality, as `DiscoveredModel` items (`Spec`, `Name`, input and output modalities). The list is fetched once per registry; a failed or empty fetch returns none and is tried again next time. Other providers return none. |

`TryResolve` takes `provider/id`, where the provider is the first segment: `openrouter/openai/text-embedding-3-small` is OpenRouter's. A listed model with that spec wins. An unlisted `provider/id` resolves too, to a model on the provider's default API for the kind, so you can name any model a provider serves. A bare id works when exactly one listed model of the kind has it. Errors read `Model "x" is ambiguous; use one of: ...`, `Unknown image model "x"; name it as provider/model` or `Provider p has no video API`.

```csharp
foreach (var model in await media.GetAvailableAsync(MediaKind.Image))
    Console.WriteLine($"{model.Spec} {model.DisplayName}{(model.AcceptsImages ? " [reference images]" : "")}");
```

## Results, not exceptions

Every request method returns a result. A failed request, a missing credential, a model of the wrong kind or a cancelled call comes back with a stop reason of `Error` (or `Aborted` when your token was cancelled) and an `ErrorMessage`, not as an exception. Only null arguments throw. A provider without auth fails with `Provider is not configured: openai (set OPENAI_API_KEY, or log in)`.

## Embeddings

```csharp
using PiSharp.AI.Media;
using PiSharp.Contracts.ModelOperations;

var model = Resolve(MediaKind.Embedding, "openai/text-embedding-3-small");
var result = await media.EmbedAsync(model, new EmbeddingRequest(["The cat sat on the mat.", "A dog barked at the mail carrier."])
{
    Dimensions = 256,
});
if (result.StopReason != ModelOperationStopReason.Stop) throw new InvalidOperationException(result.ErrorMessage);
Console.WriteLine($"{result.Vectors.Length} vectors of {result.Vectors[0].Length} floats, {result.InputTokens} tokens, ${result.Cost}");
```

| `EmbeddingRequest` | Meaning |
| --- | --- |
| `Input` | The texts. At least one. |
| `Dimensions` | Shorter vectors, for models that support it. |
| `TaskType` | Gemini task type, such as `RETRIEVAL_QUERY`. |
| `Options` | Extra request fields, as `JsonData`. |

`EmbeddingResult` has `Vectors` (one `float[]` per input, in input order), `InputTokens` when the provider counts them, and `Cost` in US dollars when the model lists `CostPerMillionTokens`. The OpenAI-compatible API sends at most 2048 texts per request and Gemini 100; longer lists go in several requests.

## Images

```csharp
using PiSharp.AI.Media;
using PiSharp.Contracts.ModelOperations;

var model = Resolve(MediaKind.Image, "openai/gpt-image-1.5");
var images = await media.GenerateImagesAsync(model,
    new ImagesContext([new ImagesTextBlock("A watercolor fox in the snow")]),
    new ImageParameters { Size = "1024x1024", Quality = "medium", OutputFormat = "png" });
if (images.StopReason != ModelOperationStopReason.Stop) throw new InvalidOperationException(images.ErrorMessage);

var image = images.Output.OfType<ImageContent>().First();
await File.WriteAllBytesAsync("fox.png", Convert.FromBase64String(image.Data));
```

To edit an image or keep a subject consistent, add `ImageContent` blocks (base64 data and a MIME type) to the context after the prompt. A model whose `AcceptsImages` is false refuses them. The result is an `AssistantImages`: `Output` holds `ImageContent` and, for models that talk, `ImagesTextBlock` items, and `Usage` the token usage and cost when the provider reports them.

| `ImageParameters` | Meaning |
| --- | --- |
| `Count` | Number of images (`n`). |
| `Size` | `auto`, `WIDTHxHEIGHT` (OpenAI), or a tier such as `2K` (OpenRouter). |
| `Quality` | `auto`, `low`, `medium`, `high` (and `xhigh` or `max` on some OpenAI models). |
| `Background` | `auto`, `transparent` or `opaque`. Transparency needs PNG or WebP. |
| `OutputFormat` | `png`, `jpeg`, `webp`, or `svg` for OpenRouter vectorization models. |
| `OutputCompression` | 0 to 100, for JPEG and WebP. |
| `AspectRatio` | For example `1:1`, `16:9` or `auto` (Google, OpenRouter). |
| `Resolution` | `512`, `1K`, `2K` or `4K` (OpenRouter; Gemini's `imageSize`). |
| `Moderation` | OpenAI: `auto` or `low`. |
| `InputFidelity` | OpenAI edits: `low` or `high`. |
| `Seed` | Seed, where the model supports one (OpenRouter). |
| `User` | A stable, hashed end-user id for abuse monitoring (OpenAI `safety_identifier`, OpenRouter `user`). |
| `Mask` | OpenAI edits: an `ImageContent` PNG whose fully transparent pixels mark where the first reference image is changed. |
| `Options` | Extra request fields, added as they are, such as OpenRouter's `provider` routing. |

Each API sends the parameters its provider documents and leaves the rest out. The provider rejects values a model doesn't support.

## Video

A video is a job: the registry submits it, polls it, then downloads the clip. Polling defaults to every 5 seconds for at most 20 minutes (`MediaRegistry.DefaultPollInterval` and `DefaultMaxWait`). `VideoJobHooks` make a job resumable: save the job id when it is submitted, and pass it back after a restart to keep polling instead of paying for a second job.

```csharp
using PiSharp.AI.Media;

var model = Resolve(MediaKind.Video, "openrouter/google/veo-3.1-fast");
var video = await media.GenerateVideoAsync(model,
    new VideoRequest("A paper boat drifting down a rainy street") { DurationSeconds = 8, AspectRatio = "16:9" },
    new MediaOptions { PollIntervalMs = 10_000, MaxWaitMs = 30 * 60_000 },
    new VideoJobHooks
    {
        ResumeJobId = savedJobId,
        OnSubmittedAsync = async (jobId, cancellationToken) =>
        {
            await saveJobId(jobId, cancellationToken);
            return jobId;
        },
        OnStatusAsync = (status, _) =>
        {
            Console.WriteLine($"{status.State} {status.Progress}");
            return ValueTask.CompletedTask;
        },
    });
if (video.Video is { } clip) await File.WriteAllBytesAsync("boat.mp4", clip.Data);
else Console.WriteLine($"{video.ErrorMessage} (job {video.JobId})");
```

`savedJobId` is the id you stored last time, or null, and `saveJobId` stores one. `OnSubmittedAsync` returns the job id to poll, normally the one it got.

| `VideoRequest` | Meaning |
| --- | --- |
| `Prompt` | The shot. May be empty for models that animate an image alone. |
| `Image`, `LastFrame` | First and last frames, as `ImageContent`. `LastFrame` is OpenRouter only. |
| `References` | OpenRouter: `MediaReference` items, each an `Image`, `Audio` or `Video` kind and an `https:` or `data:` URL. `MediaReference.FromBytes(kind, data, mimeType)` makes a `data:` URL. |
| `DurationSeconds`, `AspectRatio`, `Resolution` | Shape of the clip, such as `8`, `16:9` and `1080p`. |
| `Size` | OpenRouter: exact pixels, `WIDTHxHEIGHT`. |
| `NegativePrompt` | Google: what to avoid. |
| `GenerateAudio` | OpenRouter: generate a soundtrack, where the model can. |
| `Seed` | OpenRouter: a seed. |
| `PreviousJobId` | OpenRouter: a finished job to edit or extend. |
| `Options` | Extra request fields. |

`VideoResult` has `JobId`, `Video` (`GeneratedVideo`: bytes and MIME type) and `Cost`. A job still running at the deadline fails with "Video job *id* did not finish within *n* minutes; it may still complete on the provider", and keeps its `JobId`, so you can resume it.

## Speech

```csharp
using PiSharp.AI.Media;

var model = Resolve(MediaKind.Speech, "openai/gpt-4o-mini-tts");
var speech = await media.SpeakAsync(model, new SpeechRequest("Welcome to PiSharp.")
{
    Voice = "alloy",
    Instructions = "Warm and calm.",
    Format = "mp3",
});
if (speech.Audio is not { } audio) throw new InvalidOperationException(speech.ErrorMessage);
await File.WriteAllBytesAsync("welcome.mp3", audio.Data);
```

| `SpeechRequest` | Meaning |
| --- | --- |
| `Input` | The text. Empty when `Turns` is set. |
| `Turns` | Multi-speaker models: `SpeechTurn(text, voice, instructions)` items. |
| `Voice` | The provider's voice id. OpenAI defaults to `alloy`. |
| `Instructions` | Tone, pacing or emotion. |
| `Format` | `mp3` (the default on OpenRouter) or `pcm`; OpenAI also `opus`, `aac`, `flac` and `wav`. |
| `Speed` | Playback speed, where supported. |
| `References` | OpenRouter voice cloning: audio samples, or an image for voice design. |
| `Options` | Extra request fields. |

## Transcription, music, rerank and decisions

```csharp
using PiSharp.AI.Media;
using PiSharp.Contracts;

var stt = Resolve(MediaKind.Transcription, "openai/gpt-4o-mini-transcribe");
var transcript = await media.TranscribeAsync(stt,
    new TranscriptionRequest(await File.ReadAllBytesAsync("meeting.m4a"), "meeting.m4a") { Language = "en" });
Console.WriteLine(transcript.Text ?? transcript.ErrorMessage);

var composer = Resolve(MediaKind.Music, "openrouter/google/lyria-3-clip-preview");
var song = await media.ComposeAsync(composer, new MusicRequest("Calm lo-fi piano, 80 bpm"));
if (song.Audio is { } music) await File.WriteAllBytesAsync("calm.mp3", music.Data);

var reranker = Resolve(MediaKind.Rerank, "openrouter/cohere/rerank-4-fast");
var ranked = await media.RerankAsync(reranker, new RerankRequest("How do I resume a durable session?",
    ["Run pisharp durable --continue.", "Themes live in the themes folder.", "Each durable session is one SQLite file."]) { TopN = 2 });
foreach (var hit in ranked.Ranking) Console.WriteLine($"{hit.Score:0.000} document {hit.Index}");

var judge = Resolve(MediaKind.Decision, "openrouter/typesafe/jev-1.14");
var decision = await media.DecideAsync(judge, new DecisionRequest(
    JsonData.Parse("\"Thanks, that fixed it!\""),
    JsonData.Parse("""{"resolved":{"type":"noul","instructions":"Is the user's problem solved?"}}""")));
Console.WriteLine(decision.Data);
```

These four return a `MediaResult`, like speech. It has `Audio` (`GeneratedAudio`: bytes and MIME type) for speech and music; `Text` for a transcript, or any text a music model returns with its audio; `Ranking` (`RerankHit(Index, Score)` items, most relevant first) for rerank; `Data` for the decision answers, or the rest of a transcription response (language, duration, segments and words); and `Cost` when the provider reports one.

| Request | Fields |
| --- | --- |
| `TranscriptionRequest(audio, fileName)` | `Language` (auto-detected when null), `Prompt` (OpenAI), `Temperature`, `Timestamps` (segment and word timestamps), `Diarize` and `KeyTerms` (OpenRouter), `Options` (form parts for OpenAI, body fields for OpenRouter) |
| `MusicRequest(prompt)` | `Images` (sent with the prompt), `Seed`, `Options` |
| `RerankRequest(query, documents)` | `TopN`, `Options` |
| `DecisionRequest(state, questions)` | `Options`. `questions` is a JSON object of named questions of type `noul`, `choice`, `score` or `box`; see [`decide`](/docs/guides/media/#decide). |

## Request options

Every call takes an optional `MediaOptions`, a `ModelRequestOptions` with two more fields:

| Option | Meaning |
| --- | --- |
| `ApiKey` | A key for this request, ahead of the resolver's. |
| `Headers` | Headers merged over the auth and model headers; a null value removes one. |
| `Env` | Values passed to the auth resolver. |
| `TimeoutMs` | Timeout of each attempt. |
| `MaxRetries`, `MaxRetryDelayMs` | Retries of 408, 409, 429 and 5xx responses and connection errors, honoring `retry-after`. `MaxRetries` defaults to 2 for embeddings, rerank and decisions, 3 for video status checks and downloads, and 0 for the other generation requests, so a paid request is not sent twice. |
| `Http`, `TimeProvider` | Transport and clock for this request. |
| `PollIntervalMs` | Video: time between status checks. Default 5000. |
| `MaxWaitMs` | Video: longest wait for a job. Default 20 minutes. |

The media APIs don't call `OnPayload`, `OnResponse` or `TransformHeaders`. Each request type also has an `Options` field (`JsonData`). Its members are merged into the request body as they are, for fields this library doesn't model, such as OpenRouter's `provider` routing or a model's own parameters.

## Auth and base URLs

The registry calls its `ProviderAuthResolver` with a `ProviderAuthRequest(provider, apiKey, env)` for each request, and with `Check = true` when it only asks whether a provider is configured (`GetAvailableAsync`). Return null for a provider you can't serve, or a `ProviderAuthResult(apiKey)` with optional `Headers` and `BaseUrl`.

The base URL of a request is the first of: the model's `BaseUrl`, the auth result's `BaseUrl`, `MediaRegistryOptions.ProviderBaseUrl`, and the built-in provider table. This serves a local OpenAI-compatible embedding server:

```csharp
using PiSharp.AI.Media;
using PiSharp.AI.ModelOperations;

var environment = MediaAuth.Environment(Environment.GetEnvironmentVariable);
ProviderAuthResolver auth = (request, cancellationToken) => request.Provider == "local"
    ? ValueTask.FromResult<ProviderAuthResult?>(new ProviderAuthResult("unused"))
    : environment(request, cancellationToken);

var media = new MediaRegistry(auth);
media.AddModels([new MediaModel(MediaKind.Embedding, "local", "nomic-embed-text", OpenAIEmbeddings.ApiId)
{
    BaseUrl = "http://localhost:8080/v1",
    Dimensions = 768,
}]);
var result = await media.EmbedAsync(media.GetModels(MediaKind.Embedding, "local")[0], new EmbeddingRequest(["hello"]));
```

In `pisharp durable`, the resolver uses the credentials, headers and base URL of the provider's chat models, then the environment. It also lists OpenRouter's image models from the model catalog.

## Models and the catalog

`MediaModel(Kind, Provider, Id, Api)` describes one model:

| Property | Meaning |
| --- | --- |
| `Name`, `DisplayName` | A display name; `DisplayName` falls back to the id. |
| `Spec` | `provider/id`. |
| `BaseUrl`, `Headers` | Overrides for this model. |
| `Dimensions` | Embeddings: the vector size the model returns by default. |
| `AcceptsImages` | Images: takes reference images. Video: takes a first frame. The registry refuses those inputs otherwise. |
| `OutputsText` | Images: the model can also answer in text. |
| `CostPerMillionTokens` | Embedding price, used for `EmbeddingResult.Cost`. |
| `Listed` | False for a model `TryResolve` made from an unlisted spec. |

`MediaCatalog` holds the built-in data:

| Member | What it is |
| --- | --- |
| `Builtin` | The built-in models. The [media tools page](/docs/guides/media/#choosing-a-model) lists them. |
| `DefaultApi(kind, provider)` | The API an unlisted model uses, or null. Embeddings default to `openai-embeddings` (except `google`, and none for `anthropic`, `amazon-bedrock`, `google-vertex`, `openai-codex` and `github-copilot`); images go to `openai`, `google` or `openrouter`; video to `google` or `openrouter`; speech and transcription to `openai` or `openrouter`; music, rerank and decisions to `openrouter`. |
| `Name(kind)`, `Parse(name)` | The lower-case kind names (`image`, `embedding`, ...) and back. `Parse` also takes plurals and synonyms such as `tts`, `stt`, `audio` and `classify`. |
| `OpenRouterModality(kind)` | The OpenRouter output modality of a kind. |
| `FromImageModel(model)` | A model-operations `ImageModel` as a media model. OpenRouter's catalog entries are switched to its Image API. |

## APIs

| API id | Class | Kind | Providers | Endpoint |
| --- | --- | --- | --- | --- |
| `openai-embeddings` | `OpenAIEmbeddings` | Embedding | OpenAI, OpenRouter, Mistral, compatible servers | `POST {base}/embeddings`; `/v1` added to a base URL without a version |
| `google-embeddings` | `GoogleEmbeddings` | Embedding | Google | `models/{id}:batchEmbedContents` |
| `openai-images` | `OpenAIImages` | Image | OpenAI | `images/generations`, or `images/edits` when reference images are given (up to 16, plus an optional mask) |
| `openrouter-image-api` | `OpenRouterImageApi` | Image | OpenRouter | `POST {base}/images` |
| `openrouter-images` | `OpenRouterImages` (in `PiSharp.AI.ModelOperations`) | Image | OpenRouter | The chat-completions image route, which takes no image parameters |
| `google-images` | `GoogleImages` | Image | Google | Gemini `:generateContent`; Imagen `:predict` |
| `google-videos` | `GoogleVideos` | Video | Google | Veo `:predictLongRunning`, then the operation |
| `openrouter-videos` | `OpenRouterVideos` | Video | OpenRouter | `POST {base}/videos`, `GET videos/{id}`, `videos/{id}/content` |
| `openai-speech` | `OpenAISpeech` | Speech | OpenAI, OpenRouter | `audio/speech` |
| `openai-transcription` | `OpenAITranscription` | Transcription | OpenAI, OpenRouter | `audio/transcriptions` |
| `openrouter-chat-audio` | `OpenRouterChatAudio` | Music | OpenRouter | `chat/completions` with audio output |
| `openrouter-rerank` | `OpenRouterRerank` | Rerank | OpenRouter | `POST {base}/rerank` |
| `openrouter-decisions` | `OpenRouterDecisions` | Decision | OpenRouter | `POST {origin}/api/alpha/decisions` |

OpenAI has no video API. To add your own, implement `IEmbeddingsApi`, `IImagesApi`, `IVideosApi` (`SubmitAsync`, `GetStatusAsync`, `DownloadAsync`), `ISpeechApi`, `ITranscriptionApi`, `IMusicApi`, `IRerankApi` or `IDecisionsApi`, and register it with the matching `Register...Api` method. An implementation may throw; the registry turns the exception into an error result. Each receives a `MediaEndpoint(BaseUrl, ApiKey)` with the merged `Headers`.

## Media tools in your own harness

`PiSharp.Durable.Tools` turns the registry into the ten durable tools of `pisharp durable` ([Media tools](/docs/guides/media/)). The media tools are on `main` only, so reference `src/PiSharp.Durable.Tools/PiSharp.Durable.Tools.csproj` from your clone too, and suppress `PISHARP_DURABLE001` as well (see [Durable library](/docs/embed/durable/)):

```csharp
using PiSharp.AI.Media;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

var media = new MediaRegistry(MediaAuth.Environment(Environment.GetEnvironmentVariable));

var registry = Registry.Create();
registry.Install(MediaTools.CreateExtension(new MediaToolsOptions
{
    Media = media,
    OutputDirectory = "generated",
    Defaults = new MediaDefaults(new Dictionary<MediaKind, MediaDefault>
    {
        [MediaKind.Image] = new("openai/gpt-image-1.5", (JsonObjectValue)JsonValue.Parse("""{"quality":"high"}""")),
    }),
}));
registry.Install(ToolSearch.CreateExtension());

var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
{
    Models = models,
    Registry = registry,
    Env = (target, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(target.Cwd ?? Directory.GetCurrentDirectory())),
}, DurableContext.Background);
```

The tools read and write files through `HarnessOptions.Env`, so give the harness one. `ToolSearch` lets the model find the deferred tools.

| `MediaToolsOptions` | Default | Meaning |
| --- | --- | --- |
| `Media` | required | The registry the tools call. |
| `Defaults` | empty `MediaDefaults` | Per-kind default models and arguments (`IMediaDefaults`). |
| `OutputDirectory` | `.pi/media` | Where files go without `output_path`, relative to the call's working directory. |
| `ShowImages` | `true` | `generate_image` also shows the images to a model that accepts image input. |
| `DeferSpecializedTools` | `true` | Defer speech, transcription, music, rerank and decide. |
| `VideoPollIntervalMs`, `VideoMaxWaitMs` | 5 s, 20 min | Video polling. |

A `MediaDefault(model, options)` names the model a call without `model` uses, and default arguments under the tool's own argument names (`quality`, `output_format`, ...), which a call's arguments override. `MediaDefaults(initial)` keeps defaults in memory. Set its `Persist` callback, `(kind, value, scope, cancellationToken) => Task`, to save what `set_media_defaults` changes; `scope` is `global` or `project`, and a null `value` clears the kind. If `Persist` throws, the defaults stay as they were. `MediaTools.CreateImageTool(options)` and the other `Create...Tool` methods build single tools, and constants such as `MediaTools.EmbedToolName` name them.

The tools also declare structured output, so your own tools and workflows can call them and read the result:

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;

var pipeline = Define.Tool(new ToolRegistration
{
    Name = "pipeline",
    Description = "Embeds a fixed text through the embed tool.",
    Execute = async (_, api, context) =>
    {
        var nested = await api.ExecuteToolAsync(MediaTools.EmbedToolName,
            (JsonObjectValue)JsonValue.Parse("""{"input":["hello"],"model":"openai/text-embedding-3-small"}"""), context);
        if (nested.IsError) throw new InvalidOperationException("embed failed");
        var dimensions = ((JsonNumberValue)((JsonObjectValue)nested.StructuredOutput!)["dimensions"]).Value;
        return new ToolExecutionResult { Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", $"dimensions {dimensions}")])]) };
    },
});
```

The structures are on the [media tools page](/docs/guides/media/#structured-output), and so is how each tool behaves after a crash.
