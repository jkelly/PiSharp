// PiSharp-only (no upstream Pi source): model resolution, auth and dispatch for embeddings, image generation and video
// generation. Auth uses the model-operation resolver contract (ProviderAuthResolver), so a host passes the same credentials
// its chat and classifier requests use.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.AI.Catalogs;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>Options of <see cref="MediaRegistry"/>.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MediaRegistryOptions
{
    /// <summary>The listed models; null lists <see cref="MediaCatalog.Builtin"/>.</summary>
    public IEnumerable<MediaModel>? Models { get; init; }
    /// <summary>A provider's base URL when neither the model nor its auth names one (a host's catalog or models.json); null falls
    /// back to the built-in provider table.</summary>
    public Func<string, string?>? ProviderBaseUrl { get; init; }
    /// <summary>The HTTP transport for requests whose options name none; null uses the shared model-operation client.</summary>
    public HttpMessageInvoker? Http { get; init; }
    /// <summary>Clock for video polling and timestamps when the request options name none.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Embeddings, images, videos, speech, transcription, music, reranking and decisions across providers. Models are named by <c>provider/id</c> specs; a spec the catalog does not list
/// still resolves when its provider has a default API of that kind (<see cref="MediaCatalog.DefaultApi"/>). Every request resolves
/// its provider's auth first and never throws for request failures: they arrive as error or aborted results. Thread-safe.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class MediaRegistry
{
    private readonly ProviderAuthResolver auth;
    private readonly Func<string, string?>? providerBaseUrl;
    private readonly HttpMessageInvoker? http;
    private readonly TimeProvider time;
    private ImmutableArray<MediaModel> models;
    private readonly ConcurrentDictionary<string, IEmbeddingsApi> embeddings = new(StringComparer.Ordinal)
    { [OpenAIEmbeddings.ApiId] = OpenAIEmbeddings.Instance, [GoogleEmbeddings.ApiId] = GoogleEmbeddings.Instance };
    private readonly ConcurrentDictionary<string, IImagesApi> images = new(StringComparer.Ordinal)
    {
        [OpenAIImages.ApiId] = OpenAIImages.Instance, [GoogleImages.ApiId] = GoogleImages.Instance,
        [OpenRouterImages.ApiId] = OpenRouterImages.Instance, [OpenRouterImageApi.ApiId] = OpenRouterImageApi.Instance,
    };
    private readonly ConcurrentDictionary<string, IVideosApi> videos = new(StringComparer.Ordinal)
    { [GoogleVideos.ApiId] = GoogleVideos.Instance, [OpenRouterVideos.ApiId] = OpenRouterVideos.Instance };
    private readonly ConcurrentDictionary<string, ISpeechApi> speech = new(StringComparer.Ordinal) { [OpenAISpeech.ApiId] = OpenAISpeech.Instance };
    private readonly ConcurrentDictionary<string, ITranscriptionApi> transcription = new(StringComparer.Ordinal)
    { [OpenAITranscription.ApiId] = OpenAITranscription.Instance };
    private readonly ConcurrentDictionary<string, IMusicApi> music = new(StringComparer.Ordinal) { [OpenRouterChatAudio.ApiId] = OpenRouterChatAudio.Instance };
    private readonly ConcurrentDictionary<string, IRerankApi> rerank = new(StringComparer.Ordinal) { [OpenRouterRerank.ApiId] = OpenRouterRerank.Instance };
    private readonly ConcurrentDictionary<string, IDecisionsApi> decisions = new(StringComparer.Ordinal)
    { [OpenRouterDecisions.ApiId] = OpenRouterDecisions.Instance };
    private readonly ConcurrentDictionary<string, Task<ImmutableArray<DiscoveredModel>>> discovered = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public MediaRegistry(ProviderAuthResolver auth, MediaRegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        options ??= new();
        this.auth = auth;
        providerBaseUrl = options.ProviderBaseUrl;
        http = options.Http;
        time = options.TimeProvider ?? TimeProvider.System;
        models = [.. options.Models ?? MediaCatalog.Builtin];
    }

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(20);

    /// <summary>Lists more models; one with the kind and spec of a listed model replaces it in place.</summary>
    public void AddModels(IEnumerable<MediaModel> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        lock (gate)
        {
            var list = models.ToList();
            foreach (var model in added)
            {
                var index = list.FindIndex(existing => existing.Kind == model.Kind && existing.Spec == model.Spec);
                if (index >= 0) list[index] = model; else list.Add(model);
            }
            models = [.. list];
        }
    }

    private ImmutableArray<MediaModel> ReadModels() { lock (gate) return models; }

    public void RegisterEmbeddingsApi(IEmbeddingsApi api) { ArgumentNullException.ThrowIfNull(api); embeddings[api.Api] = api; }
    public void RegisterImagesApi(IImagesApi api) { ArgumentNullException.ThrowIfNull(api); images[api.Api] = api; }
    public void RegisterVideosApi(IVideosApi api) { ArgumentNullException.ThrowIfNull(api); videos[api.Api] = api; }
    public void RegisterSpeechApi(ISpeechApi api) { ArgumentNullException.ThrowIfNull(api); speech[api.Api] = api; }
    public void RegisterTranscriptionApi(ITranscriptionApi api) { ArgumentNullException.ThrowIfNull(api); transcription[api.Api] = api; }
    public void RegisterMusicApi(IMusicApi api) { ArgumentNullException.ThrowIfNull(api); music[api.Api] = api; }
    public void RegisterRerankApi(IRerankApi api) { ArgumentNullException.ThrowIfNull(api); rerank[api.Api] = api; }
    public void RegisterDecisionsApi(IDecisionsApi api) { ArgumentNullException.ThrowIfNull(api); decisions[api.Api] = api; }

    /// <summary>The listed models, of one kind and provider when given, in listing order.</summary>
    public ImmutableArray<MediaModel> GetModels(MediaKind? kind = null, string? provider = null) =>
        [.. ReadModels().Where(model => (kind is null || model.Kind == kind) && (provider is null || model.Provider == provider))];

    /// <summary>The listed models whose providers have auth configured.</summary>
    public async Task<ImmutableArray<MediaModel>> GetAvailableAsync(MediaKind? kind = null, CancellationToken cancellationToken = default)
    {
        var configured = new Dictionary<string, bool>(StringComparer.Ordinal);
        var available = ImmutableArray.CreateBuilder<MediaModel>();
        foreach (var model in GetModels(kind))
        {
            if (!configured.TryGetValue(model.Provider, out var ok))
            {
                ok = await auth(new ProviderAuthRequest(model.Provider) { Check = true }, cancellationToken).ConfigureAwait(false) is not null;
                configured[model.Provider] = ok;
            }
            if (ok) available.Add(model);
        }
        return available.ToImmutable();
    }

    /// <summary>
    /// The model a spec names: <c>provider/id</c> (the provider is the first segment, so <c>openrouter/openai/text-embedding-3-small</c>
    /// is OpenRouter's), or a bare id that one listed model of the kind has. A listed model wins; an unlisted <c>provider/id</c> uses the
    /// provider's default API. False with a reason otherwise.
    /// </summary>
    public bool TryResolve(MediaKind kind, string spec, out MediaModel model, out string error)
    {
        ArgumentNullException.ThrowIfNull(spec);
        model = null!; error = "";
        spec = spec.Trim();
        var listed = GetModels(kind);
        if (listed.FirstOrDefault(entry => entry.Spec == spec) is { } exact) { model = exact; return true; }
        var slash = spec.IndexOf('/');
        if (slash <= 0 || slash == spec.Length - 1)
        {
            var byId = listed.Where(entry => entry.Id == spec).ToList();
            if (byId.Count == 1) { model = byId[0]; return true; }
            error = byId.Count > 1
                ? $"Model \"{spec}\" is ambiguous; use one of: {string.Join(", ", byId.Select(entry => entry.Spec))}"
                : $"Unknown {Kind(kind)} model \"{spec}\"; name it as provider/model";
            return false;
        }
        var provider = spec[..slash];
        var id = spec[(slash + 1)..];
        if (MediaCatalog.DefaultApi(kind, provider) is not { } api)
        {
            error = $"Provider {provider} has no {Kind(kind)} API";
            return false;
        }
        model = new MediaModel(kind, provider, id, api)
        {
            Listed = false, AcceptsImages = kind is MediaKind.Image or MediaKind.Video or MediaKind.Music or MediaKind.Decision,
        };
        return true;
    }

    /// <summary>Embeds the request's texts with <paramref name="model"/>.</summary>
    public async Task<EmbeddingResult> EmbedAsync(MediaModel model, EmbeddingRequest request, MediaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(request);
        try
        {
            Require(model, MediaKind.Embedding);
            if (request.Input.IsDefaultOrEmpty) throw new ArgumentException("At least one text is required.");
            var api = embeddings.GetValueOrDefault(model.Api) ?? throw NoApi(model);
            var (endpoint, resolved) = await ResolveAsync(model, options, cancellationToken).ConfigureAwait(false);
            var result = await api.EmbedAsync(model, endpoint, request, resolved, cancellationToken).ConfigureAwait(false);
            return result.InputTokens is { } tokens && model.CostPerMillionTokens is { } rate ? result with { Cost = rate * tokens / 1_000_000 } : result;
        }
        catch (Exception error)
        {
            return new EmbeddingResult(model.Provider, model.Id, [], StopReason(cancellationToken)) { ErrorMessage = Message(error) };
        }
    }

    /// <summary>Generates images from the context's prompt and reference images; <paramref name="parameters"/> go to the API as
    /// <see cref="ImagesOptions.Metadata"/>.</summary>
    public async Task<AssistantImages> GenerateImagesAsync(MediaModel model, ImagesContext context, ImageParameters? parameters = null,
        MediaOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        try
        {
            Require(model, MediaKind.Image);
            if (!model.AcceptsImages && (context.Input.IsDefault ? [] : context.Input).OfType<ImageContent>().Any())
                throw new ArgumentException($"Model {model.Spec} does not accept reference images.");
            var api = images.GetValueOrDefault(model.Api) ?? throw NoApi(model);
            var (endpoint, resolved) = await ResolveAsync(model, options, cancellationToken).ConfigureAwait(false);
            var imageOptions = new ImagesOptions
            {
                ApiKey = endpoint.ApiKey, Headers = endpoint.Headers, Http = resolved.Http, TimeProvider = resolved.TimeProvider,
                TimeoutMs = resolved.TimeoutMs, MaxRetries = resolved.MaxRetries, MaxRetryDelayMs = resolved.MaxRetryDelayMs,
                Metadata = parameters?.ToJson(),
            };
            // The endpoint already carries the model headers.
            var imageModel = model.ToImageModel(endpoint.BaseUrl) with { Headers = [] };
            return await api.GenerateImagesAsync(imageModel, context, imageOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new AssistantImages(model.Api, model.Provider, model.Id, [], StopReason(cancellationToken), Now(options))
            { ErrorMessage = Message(error) };
        }
    }

    /// <summary>
    /// Generates one video: submits the job (or resumes <see cref="VideoJobHooks.ResumeJobId"/>), polls it every
    /// <see cref="MediaOptions.PollIntervalMs"/> until it completes, fails or exceeds <see cref="MediaOptions.MaxWaitMs"/>, then
    /// downloads it.
    /// </summary>
    public async Task<VideoResult> GenerateVideoAsync(MediaModel model, VideoRequest request, MediaOptions? options = null, VideoJobHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(request);
        string? jobId = hooks?.ResumeJobId;
        try
        {
            Require(model, MediaKind.Video);
            if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null && request.References.IsDefaultOrEmpty && request.PreviousJobId is null)
                throw new ArgumentException("A video prompt is required.");
            if (request.Image is not null && !model.AcceptsImages) throw new ArgumentException($"Model {model.Spec} does not accept a first-frame image.");
            var api = videos.GetValueOrDefault(model.Api) ?? throw NoApi(model);
            var (endpoint, resolved) = await ResolveAsync(model, options, cancellationToken).ConfigureAwait(false);
            if (jobId is null)
            {
                jobId = await api.SubmitAsync(model, endpoint, request, resolved, cancellationToken).ConfigureAwait(false);
                if (hooks?.OnSubmittedAsync is { } submitted) jobId = await submitted(jobId, cancellationToken).ConfigureAwait(false);
            }
            var clock = resolved.TimeProvider;
            var interval = resolved.PollIntervalMs is { } poll ? TimeSpan.FromMilliseconds(Math.Max(0, poll)) : DefaultPollInterval;
            var maxWait = resolved.MaxWaitMs is { } wait ? TimeSpan.FromMilliseconds(Math.Max(0, wait)) : DefaultMaxWait;
            var deadline = clock.GetUtcNow() + maxWait;
            VideoJobStatus status;
            for (;;)
            {
                status = await api.GetStatusAsync(model, endpoint, jobId, resolved, cancellationToken).ConfigureAwait(false);
                if (hooks?.OnStatusAsync is { } observe) await observe(status, cancellationToken).ConfigureAwait(false);
                if (status.State == VideoJobState.Completed) break;
                if (status.State == VideoJobState.Failed)
                    return new VideoResult(model.Provider, model.Id, ModelOperationStopReason.Error)
                    { JobId = jobId, Cost = status.Cost, ErrorMessage = status.ErrorMessage ?? "Video job failed" };
                if (clock.GetUtcNow() >= deadline)
                    throw new TimeoutException($"Video job {jobId} did not finish within {maxWait.TotalMinutes:0.#} minutes; it may still complete on the provider.");
                await Task.Delay(interval, clock, cancellationToken).ConfigureAwait(false);
            }
            var video = await api.DownloadAsync(model, endpoint, jobId, status, resolved, cancellationToken).ConfigureAwait(false);
            if (video.Data.Length == 0) throw new InvalidDataException($"{model.Provider} returned an empty video");
            return new VideoResult(model.Provider, model.Id, ModelOperationStopReason.Stop) { JobId = jobId, Video = video, Cost = status.Cost };
        }
        catch (Exception error)
        {
            return new VideoResult(model.Provider, model.Id, StopReason(cancellationToken)) { JobId = jobId, ErrorMessage = Message(error) };
        }
    }

    /// <summary>Speaks the request's text.</summary>
    public Task<MediaResult> SpeakAsync(MediaModel model, SpeechRequest request, MediaOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(model, MediaKind.Speech, speech, options, cancellationToken, (api, endpoint, resolved) =>
        {
            if (string.IsNullOrWhiteSpace(request.Input) && request.Turns.IsDefaultOrEmpty) throw new ArgumentException("Text to speak is required.");
            return api.SpeakAsync(model, endpoint, request, resolved, cancellationToken);
        });
    }

    /// <summary>Transcribes the request's audio.</summary>
    public Task<MediaResult> TranscribeAsync(MediaModel model, TranscriptionRequest request, MediaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(model, MediaKind.Transcription, transcription, options, cancellationToken, (api, endpoint, resolved) =>
        {
            if (request.Audio.Length == 0) throw new ArgumentException("Audio to transcribe is required.");
            return api.TranscribeAsync(model, endpoint, request, resolved, cancellationToken);
        });
    }

    /// <summary>Generates music or other audio from the request's prompt.</summary>
    public Task<MediaResult> ComposeAsync(MediaModel model, MusicRequest request, MediaOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(model, MediaKind.Music, music, options, cancellationToken, (api, endpoint, resolved) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("A music prompt is required.");
            return api.ComposeAsync(model, endpoint, request, resolved, cancellationToken);
        });
    }

    /// <summary>Orders the request's documents by relevance to its query.</summary>
    public Task<MediaResult> RerankAsync(MediaModel model, RerankRequest request, MediaOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(model, MediaKind.Rerank, rerank, options, cancellationToken, (api, endpoint, resolved) =>
        {
            if (request.Documents.IsDefaultOrEmpty) throw new ArgumentException("At least one document is required.");
            return api.RerankAsync(model, endpoint, request, resolved, cancellationToken);
        });
    }

    /// <summary>Answers the request's questions about its state.</summary>
    public Task<MediaResult> DecideAsync(MediaModel model, DecisionRequest request, MediaOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(model, MediaKind.Decision, decisions, options, cancellationToken, (api, endpoint, resolved) =>
        {
            if (request.Questions.Value.ValueKind != System.Text.Json.JsonValueKind.Object || !request.Questions.Value.EnumerateObject().Any())
                throw new ArgumentException("Questions must be an object with at least one named question.");
            return api.DecideAsync(model, endpoint, request, resolved, cancellationToken);
        });
    }

    private async Task<MediaResult> RunAsync<TApi>(MediaModel model, MediaKind kind, ConcurrentDictionary<string, TApi> apis, MediaOptions? options,
        CancellationToken cancellationToken, Func<TApi, MediaEndpoint, MediaOptions, Task<MediaResult>> call)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            Require(model, kind);
            var api = apis.GetValueOrDefault(model.Api) ?? throw NoApi(model);
            var (endpoint, resolved) = await ResolveAsync(model, options, cancellationToken).ConfigureAwait(false);
            return await call(api, endpoint, resolved).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new MediaResult(model.Provider, model.Id, StopReason(cancellationToken)) { ErrorMessage = Message(error) };
        }
    }

    /// <summary>
    /// Every model of a kind that a provider offers now, from the provider's model list: OpenRouter's <c>GET {baseUrl}/models</c>,
    /// filtered by output modality. Cached per provider for the registry's lifetime; an unconfigured or unsupported provider has none.
    /// </summary>
    public async Task<ImmutableArray<DiscoveredModel>> DiscoverAsync(MediaKind kind, string provider = "openrouter", CancellationToken cancellationToken = default)
    {
        if (provider != "openrouter") return [];
        var all = await discovered.GetOrAdd(provider, _ => LoadOpenRouterModelsAsync(provider)).ConfigureAwait(false);
        if (all.IsEmpty) discovered.TryRemove(provider, out _);
        cancellationToken.ThrowIfCancellationRequested();
        var modality = MediaCatalog.OpenRouterModality(kind);
        return [.. all.Where(model => model.OutputModalities.Contains(modality))];
    }

    private async Task<ImmutableArray<DiscoveredModel>> LoadOpenRouterModelsAsync(string provider)
    {
        try
        {
            var probe = new MediaModel(MediaKind.Image, provider, "models", OpenRouterImageApi.ApiId);
            var (endpoint, resolved) = await ResolveAsync(probe, null, CancellationToken.None).ConfigureAwait(false);
            var response = await MediaHttp.SendJsonAsync(provider, HttpMethod.Get, MediaHttp.Url(endpoint.BaseUrl, "models?output_modalities=all"),
                MediaHttp.BearerHeaders(endpoint), null, resolved, 2, CancellationToken.None).ConfigureAwait(false);
            if (MediaHttp.Field(response, "data") is not { ValueKind: System.Text.Json.JsonValueKind.Array } data) return [];
            var models = ImmutableArray.CreateBuilder<DiscoveredModel>();
            foreach (var item in data.EnumerateArray())
            {
                if (MediaHttp.String(item, "id") is not { Length: > 0 } id) continue;
                var architecture = MediaHttp.Field(item, "architecture");
                ImmutableArray<string> Modalities(string name) => architecture is { } value && MediaHttp.Field(value, name) is { ValueKind: System.Text.Json.JsonValueKind.Array } list
                    ? [.. list.EnumerateArray().Where(entry => entry.ValueKind == System.Text.Json.JsonValueKind.String).Select(entry => entry.GetString()!)] : [];
                models.Add(new DiscoveredModel(provider, id, MediaHttp.String(item, "name") ?? id, Modalities("input_modalities"), Modalities("output_modalities")));
            }
            return models.ToImmutable();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return []; }
    }

    /// <summary>The provider's auth and base URL for one request, and the request options with the registry's transport and clock.</summary>
    private async Task<(MediaEndpoint Endpoint, MediaOptions Options)> ResolveAsync(MediaModel model, MediaOptions? options, CancellationToken cancellationToken)
    {
        options ??= new MediaOptions { TimeProvider = time };
        if (options.Http is null && http is not null) options = options with { Http = http };
        var resolution = await auth(new ProviderAuthRequest(model.Provider, options.ApiKey, options.Env.IsEmpty ? null : options.Env), cancellationToken)
            .ConfigureAwait(false) ?? throw new ModelOperationsException(ModelOperationsErrorCode.Auth, NotConfigured(model.Provider));
        cancellationToken.ThrowIfCancellationRequested();
        var baseUrl = model.BaseUrl ?? resolution.BaseUrl ?? providerBaseUrl?.Invoke(model.Provider)
            ?? (BuiltinProviders.TryGet(model.Provider, out var builtin) ? builtin.BaseUrl : null)
            ?? throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"No base URL is known for provider {model.Provider}");
        var headers = ProviderRequest.MergeHeaders(resolution.Headers, ProviderRequest.Nullable(model.Headers), options.Headers);
        var endpoint = new MediaEndpoint(baseUrl, options.ApiKey ?? resolution.ApiKey)
        {
            Headers = [.. headers.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))],
        };
        return (endpoint, options);
    }

    private static string NotConfigured(string provider)
    {
        var variables = ProviderEnvironmentKeys.GetApiKeyVariables(provider);
        return $"Provider is not configured: {provider}" + (variables is { Count: > 0 } ? $" (set {string.Join(" or ", variables)}, or log in)" : "");
    }

    private static void Require(MediaModel model, MediaKind kind)
    {
        if (model.Kind != kind) throw new ArgumentException($"Model {model.Spec} is a {Kind(model.Kind)} model, not a {Kind(kind)} model.");
    }

    private static ModelOperationsException NoApi(MediaModel model) =>
        new(ModelOperationsErrorCode.Provider, $"No {Kind(model.Kind)} implementation is registered for api \"{model.Api}\"");

    private static string Kind(MediaKind kind) => MediaCatalog.Name(kind);

    private static ModelOperationStopReason StopReason(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error;

    private static string Message(Exception error) => error is OperationCanceledException ? "Request aborted" : error.Message;

    private long Now(MediaOptions? options) => (options?.TimeProvider ?? time).GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>Auth resolvers for media requests without a host credential store.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public static class MediaAuth
{
    /// <summary>An explicit request key, else the provider's first API-key environment variable that is set; null when none is.</summary>
    public static ProviderAuthResolver Environment(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        return (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(request.ApiKey)) return ValueTask.FromResult<ProviderAuthResult?>(new ProviderAuthResult(request.ApiKey));
            foreach (var name in ProviderEnvironmentKeys.GetApiKeyVariables(request.Provider) ?? [])
                if (readEnvironment(name) is { Length: > 0 } key) return ValueTask.FromResult<ProviderAuthResult?>(new ProviderAuthResult(key));
            return ValueTask.FromResult<ProviderAuthResult?>(null);
        };
    }
}

/// <summary>A model a provider lists: its id, name and input and output modalities.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record DiscoveredModel(string Provider, string Id, string Name, ImmutableArray<string> InputModalities, ImmutableArray<string> OutputModalities)
{
    public string Spec => Provider + "/" + Id;
}
