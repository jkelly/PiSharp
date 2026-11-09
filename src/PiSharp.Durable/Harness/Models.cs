// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/models.ts (the Models interface) and packages/ai/src/types.ts (Model,
// SimpleStreamOptions, DeferredHandle), reduced to what packages/durable uses: getModel, streamSimple, completeSimple, streamDeferred,
// fetchDeferred and cancelDeferred. Catalog refresh, auth and login, images and classifiers stay with the host.
//
// Mapping: a pi-ai Model is a DurableModel carrying the catalog entry's wire JSON plus the fields durable reads; the request context is
// the transcript (pi-ai wire messages; system prompt and tools ride as positional system messages, so no other context field is
// needed); stream events and the final message are PiSharp.Contracts types (PiWireJson converts both ways). P8 adds the production
// adapter over PiSharp.AI transports; until then hosts and tests supply their own implementation.
// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/providers/model-schema.ts (ModelInputLimits, ModelImageInputLimits,
// ModelImageResizeOptions) and types.ts (Model.inputLimits), which pi-durable 1.2.0's read tool consults for its image limits (work list
// area e).
// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/models.ts (getModelsOfType, getAvailableOfType, getModelOfType, classify,
// generateImages) and packages/ai/src/utils/model-operations.ts (classifierErrorResult, imageErrorResult), which pi-durable 1.2.0's code mode
// calls for scripts' models.* (work list area d). Models of every type are pi-ai model JSON (AnyModel); classifier and image contexts and
// results are pi-ai wire JSON (ClassifierContext, ClassifierResult, ImagesContext, AssistantImages). The members have defaults, so a host
// that offers no classifier or image models implements nothing: no catalog entries, and every call an error result.
// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/types.ts (StreamOptions identity and onPayload) and
// packages/ai/src/utils/identity.ts (withClientIdentity), which pi-durable 1.5.0's Harness identity and beforeProviderRequest hook use
// (area model-requests). IDurableModels' deferred, classifier and image calls take no identity, so withClientIdentity reaches its chat
// requests only.
// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/types.ts (StreamOptions.telemetry) and packages/ai/src/models.ts
// (ModelsDeferredFetchOptions, ModelsDeferredCancelOptions, ModelsClassifierOptions, ModelsImagesOptions: their telemetry), which
// pi-durable 1.5.0 passes a phase's span through (area telemetry, 2ec55d808). DurableStreamOptions.Telemetry rides with a chat request;
// the deferred, classifier and image calls take a DurableRequestOptions in overloads whose defaults drop it, so a host's IDurableModels
// written before them keeps working and records nothing. pi-ai's Models records pi.ai.request and pi.ai.poll spans under it.
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>A pi-ai chat model catalog entry (upstream pi-ai <c>Model</c>).</summary>
public sealed record DurableModel
{
    /// <summary>Model ID.</summary>
    public required string Id { get; init; }
    /// <summary>Display name.</summary>
    public required string Name { get; init; }
    /// <summary>pi-ai API identifier.</summary>
    public required string Api { get; init; }
    /// <summary>Provider ID.</summary>
    public required string Provider { get; init; }
    /// <summary>Base URL.</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>Whether the model reasons.</summary>
    public bool Reasoning { get; init; }
    /// <summary>Accepted input kinds (<c>text</c>, <c>image</c>).</summary>
    public ImmutableArray<string> Input { get; init; } = ["text"];
    /// <summary>Context window in tokens.</summary>
    public double ContextWindow { get; init; }
    /// <summary>Maximum output tokens.</summary>
    public double MaxTokens { get; init; }
    /// <summary>Provider input limits (pi-ai <c>inputLimits</c>); null when the catalog entry declares none.</summary>
    public DurableModelInputLimits? InputLimits { get; init; }
    /// <summary>The complete catalog entry as pi-ai wire JSON, when the host has it (costs, compat, thinking maps).</summary>
    public JsonObjectValue? Wire { get; init; }

    /// <summary>The model identity PiSharp.AI transports take.</summary>
    public ModelDescriptor Descriptor => new(Id, Api, Provider);
}

/// <summary>Provider input limits of a model (pi-ai <c>ModelInputLimits</c>).</summary>
public sealed record DurableModelInputLimits
{
    /// <summary>Maximum serialized provider request size in bytes.</summary>
    public long? MaxRequestBytes { get; init; }
    /// <summary>Image input limits.</summary>
    public DurableModelImageInputLimits? Images { get; init; }

    /// <summary>The limits of a catalog entry's <c>inputLimits</c> JSON; null when it is not an object. Fields that are not positive integers
    /// are left unset.</summary>
    public static DurableModelInputLimits? FromJson(JsonValue? json)
    {
        if (json is not JsonObjectValue limits) return null;
        return new DurableModelInputLimits
        {
            MaxRequestBytes = Integer(limits, "maxRequestBytes"),
            Images = limits.TryGetValue("images", out var images) && images is JsonObjectValue imageLimits
                ? new DurableModelImageInputLimits
                {
                    Resize = imageLimits.TryGetValue("resize", out var resize) && resize is JsonObjectValue options
                        ? new DurableModelImageResizeOptions
                        {
                            MaxWidth = Int(options, "maxWidth"), MaxHeight = Int(options, "maxHeight"), MaxBytes = Int(options, "maxBytes"),
                            JpegQuality = Int(options, "jpegQuality"),
                        }
                        : null,
                    MaxPerMessage = Int(imageLimits, "maxPerMessage"),
                    MaxPerRequest = Int(imageLimits, "maxPerRequest"),
                }
                : null,
        };
    }

    private static long? Integer(JsonObjectValue json, string key) =>
        json.TryGetValue(key, out var value) && value is JsonNumberValue number && number.Value >= 1 && number.Value <= Ids.MaxSafeInteger &&
        Math.Truncate(number.Value) == number.Value ? (long)number.Value : null;

    private static int? Int(JsonObjectValue json, string key) => Integer(json, key) is { } value && value <= int.MaxValue ? (int)value : null;
}

/// <summary>Image input limits of a model (pi-ai <c>ModelImageInputLimits</c>).</summary>
public sealed record DurableModelImageInputLimits
{
    /// <summary>The cache-safe resize profile applied before a new image enters conversation history.</summary>
    public DurableModelImageResizeOptions? Resize { get; init; }
    /// <summary>Maximum images accepted in one provider message.</summary>
    public int? MaxPerMessage { get; init; }
    /// <summary>Maximum images accepted across one provider request.</summary>
    public int? MaxPerRequest { get; init; }
}

/// <summary>An image resize profile (pi-ai <c>ModelImageResizeOptions</c>); unset fields take the consumer's defaults.</summary>
public sealed record DurableModelImageResizeOptions
{
    /// <summary>Pixels.</summary>
    public int? MaxWidth { get; init; }
    /// <summary>Pixels.</summary>
    public int? MaxHeight { get; init; }
    /// <summary>Maximum base64-encoded payload size in bytes.</summary>
    public int? MaxBytes { get; init; }
    /// <summary>JPEG quality, 1 to 100.</summary>
    public int? JpegQuality { get; init; }
}

/// <summary>Options of one simple request (pi-ai <c>ModelsSimpleStreamOptions</c>, the fields durable sets).</summary>
public sealed record DurableStreamOptions
{
    /// <summary>Preferred transport.</summary>
    public string? Transport { get; init; }
    /// <summary>HTTP request timeout.</summary>
    public double? TimeoutMs { get; init; }
    /// <summary>WebSocket connection/open handshake timeout in milliseconds.</summary>
    public double? WebsocketConnectTimeoutMs { get; init; }
    /// <summary>Custom token budgets by thinking level, for token-based providers.</summary>
    public JsonObjectValue? ThinkingBudgets { get; init; }
    /// <summary>Provider/SDK retries.</summary>
    public int? MaxRetries { get; init; }
    /// <summary>Longest server-requested retry delay to wait for.</summary>
    public double? MaxRetryDelayMs { get; init; }
    /// <summary>Extra headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>Request metadata.</summary>
    public JsonObjectValue? Metadata { get; init; }
    /// <summary>Prompt cache retention.</summary>
    public string? CacheRetention { get; init; }
    /// <summary>Session identifier for session-based caching (the conversation's <c>pi.provider</c> session ID).</summary>
    public string? SessionId { get; init; }
    /// <summary>The thinking level; null for <c>off</c>.</summary>
    public string? Reasoning { get; init; }
    /// <summary>Ask a capable provider for a durable handle: <c>true</c> or <c>{window}</c>.</summary>
    public JsonValue? Deferred { get; init; }
    /// <summary>Maximum output tokens.</summary>
    public double? MaxTokens { get; init; }
    /// <summary>PiSharp-only (decision 0007): the conversation's service tier (<c>ChatRequest.ServiceTier</c>).</summary>
    public string? ServiceTier { get; init; }
    /// <summary>How the request identifies the app to the provider (pi-ai <c>identity</c>, Pi v1.5.0; <c>ChatRequest.Identity</c>): merged
    /// field by field over the identity the model access was created with. A Harness sets it through
    /// <see cref="DurableModelIdentity.WithClientIdentity"/>. Null: the model access's own identity.</summary>
    public ClientIdentity? Identity { get; init; }
    /// <summary>
    /// Inspects or replaces each provider request body before it is sent (pi-ai <c>onPayload</c>, Pi v1.5.0): called with the body in the
    /// provider API's own format and the model; a returned value replaces the body, null keeps it. It runs again whenever the body is
    /// rebuilt, such as a retry within one attempt. <see cref="NativeDurableModels"/> applies it to JSON request bodies at the HTTP boundary
    /// of its routes, its host routes included when the host's handler is wrapped for it; other model access may ignore it.
    /// </summary>
    public Func<JsonValue, DurableModel, CancellationToken, Task<JsonValue?>>? OnPayload { get; init; }
    /// <summary>The parent span of the request's <c>pi.ai.request</c> span (pi-ai <c>telemetry</c>, Pi v1.5.0); a Harness passes the phase's
    /// span. Null: nothing is recorded.</summary>
    public ITelemetrySpan? Telemetry { get; init; }
}

/// <summary>Options of a deferred, classifier or image call (pi-ai <c>ModelsDeferredFetchOptions</c>, <c>ModelsDeferredCancelOptions</c>,
/// <c>ModelsClassifierOptions</c> and <c>ModelsImagesOptions</c>, the field durable sets; the signal is the call's cancellation token).</summary>
public sealed record DurableRequestOptions
{
    /// <summary>App attribution for this operation, merged over the models access default.</summary>
    public ClientIdentity? Identity { get; init; }
    /// <summary>The parent span of the call's telemetry (pi-ai <c>telemetry</c>, Pi v1.5.0): a deferred poll records a <c>pi.ai.poll</c> span
    /// under it. Null: nothing is recorded.</summary>
    public ITelemetrySpan? Telemetry { get; init; }
}

/// <summary>A provider-side deferred response (pi-ai <c>DeferredHandle</c>), kept as its wire JSON <c>{provider, modelId, api, id,
/// expiresAt?, pollAfterMs?, data?}</c> so it round-trips through checkpoints unchanged.</summary>
public sealed record DurableDeferredHandle(JsonObjectValue Wire)
{
    /// <summary>The provider token.</summary>
    public string Id => Wire.TryGetValue("id", out var id) && id is JsonStringValue text ? text.Value : "";
}

/// <summary>
/// Model access used by generation and compaction (a port of pi-ai <c>Models</c>, reduced to what pi-durable calls). Requests take the
/// transcript as pi-ai wire messages. A stream never throws for a request failure once it started: it ends with a
/// <see cref="StreamTerminalEvent"/> whose message has stop reason <c>error</c> or <c>aborted</c>.
/// </summary>
public interface IDurableModels
{
    /// <summary>Synchronous chat model lookup against the last-known catalog (pi-ai <c>getModel</c>); null when unknown.</summary>
    DurableModel? GetModel(string provider, string modelId);

    /// <summary>Streams one request (pi-ai <c>streamSimple</c>).</summary>
    IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Runs one request to its final message (pi-ai <c>completeSimple</c>).</summary>
    Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Streams a deferred response (pi-ai <c>streamDeferred</c>).</summary>
    IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken);

    /// <summary>Fetches a deferred response's final message (pi-ai <c>fetchDeferred</c>); a still-pending response ends with stop reason
    /// <c>deferred</c>.</summary>
    Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken);

    /// <summary>Best-effort cancellation of a deferred response (pi-ai <c>cancelDeferred</c>).</summary>
    Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken);

    /// <summary><see cref="StreamDeferred(DurableModel, DurableDeferredHandle, CancellationToken)"/> with request options (pi-ai
    /// <c>streamDeferred(model, handle, options)</c>): a poll records <c>pi.ai.poll</c> under <see cref="DurableRequestOptions.Telemetry"/>.
    /// Default: the options are dropped.</summary>
    IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
        CancellationToken cancellationToken) => StreamDeferred(model, handle, cancellationToken);

    /// <summary><see cref="FetchDeferredAsync(DurableModel, DurableDeferredHandle, CancellationToken)"/> with request options (pi-ai
    /// <c>fetchDeferred(model, handle, options)</c>). Default: the options are dropped.</summary>
    Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
        CancellationToken cancellationToken) => FetchDeferredAsync(model, handle, cancellationToken);

    /// <summary><see cref="CancelDeferredAsync(DurableModel, DurableDeferredHandle, CancellationToken)"/> with request options (pi-ai
    /// <c>cancelDeferred(model, handle, options)</c>, which records no span). Default: the options are dropped.</summary>
    Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
        CancellationToken cancellationToken) => CancelDeferredAsync(model, handle, cancellationToken);

    /// <summary>pi-ai <c>getModelsOfType(type, provider?)</c>: the catalog's models of one type (<c>"chat"</c>, <c>"image"</c> or
    /// <c>"classifier"</c>) as pi-ai model JSON, of <paramref name="provider"/> or of every provider. Default: none.</summary>
    ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) => [];

    /// <summary>pi-ai <c>getAvailableOfType(type, provider?)</c>: the models of one type whose providers have complete auth. Default:
    /// none.</summary>
    Task<ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
        Task.FromResult(ImmutableArray<JsonObjectValue>.Empty);

    /// <summary>pi-ai <c>getModelOfType(type, provider, id)</c>; null when unknown. Default: the first of <see cref="GetModelsOfType"/>
    /// with that id.</summary>
    JsonObjectValue? GetModelOfType(string type, string provider, string id) =>
        GetModelsOfType(type, provider).FirstOrDefault(model => model.TryGetValue("id", out var value) && value is JsonStringValue text && text.Value == id);

    /// <summary>pi-ai <c>classify(model, context, { signal })</c>: a <c>ClassifierResult</c>. Never throws for a request failure: it
    /// returns an error result (<see cref="DurableModelOperations.ClassifierError"/>), as pi-ai does. Default: the error of a provider
    /// without classification.</summary>
    Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
        Task.FromResult(DurableModelOperations.ClassifierError(model,
            $"Provider {DurableModelOperations.Field(model, "provider")} does not support classification", cancellationToken.IsCancellationRequested));

    /// <summary><see cref="ClassifyAsync(JsonObjectValue, JsonObjectValue, CancellationToken)"/> with request options (pi-ai
    /// <c>classify(model, context, { signal, telemetry })</c>). Default: the options are dropped.</summary>
    Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, DurableRequestOptions? options,
        CancellationToken cancellationToken) => ClassifyAsync(model, context, cancellationToken);

    /// <summary>pi-ai <c>generateImages(model, context, { signal })</c>: an <c>AssistantImages</c>. Never throws for a request failure: it
    /// returns an error result (<see cref="DurableModelOperations.ImagesError"/>), as pi-ai does. Default: the error of a provider without
    /// image generation.</summary>
    Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
        Task.FromResult(DurableModelOperations.ImagesError(model,
            $"Provider {DurableModelOperations.Field(model, "provider")} does not support image generation", cancellationToken.IsCancellationRequested));

    /// <summary><see cref="GenerateImagesAsync(JsonObjectValue, JsonObjectValue, CancellationToken)"/> with request options (pi-ai
    /// <c>generateImages(model, context, { signal, telemetry })</c>). Default: the options are dropped.</summary>
    Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, DurableRequestOptions? options,
        CancellationToken cancellationToken) => GenerateImagesAsync(model, context, cancellationToken);
}

/// <summary>pi-ai's model operation results (<c>utils/model-operations.ts</c>) as wire JSON.</summary>
public static class DurableModelOperations
{
    /// <summary>A string field of a model, or <c>"undefined"</c> as a template literal prints a missing one.</summary>
    internal static string Field(JsonObjectValue model, string name) =>
        model.TryGetValue(name, out var value) && value is JsonStringValue text ? text.Value : "undefined";

    private static JsonObjectValue Error(JsonObjectValue model, string empty, JsonValue emptyValue, string message, bool aborted) => JsonValue.Object(
    [
        KeyValuePair.Create("api", model.TryGetValue("api", out var api) ? api : JsonValue.Null),
        KeyValuePair.Create("provider", model.TryGetValue("provider", out var provider) ? provider : JsonValue.Null),
        KeyValuePair.Create("model", model.TryGetValue("id", out var id) ? id : JsonValue.Null),
        KeyValuePair.Create(empty, emptyValue),
        KeyValuePair.Create("stopReason", (JsonValue)(aborted ? "aborted" : "error")),
        KeyValuePair.Create("errorMessage", (JsonValue)message),
        KeyValuePair.Create("timestamp", (JsonValue)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
    ]);

    /// <summary>pi-ai <c>classifierErrorResult</c>: <c>{api, provider, model, answers: {}, stopReason, errorMessage, timestamp}</c>.</summary>
    public static JsonObjectValue ClassifierError(JsonObjectValue model, string message, bool aborted = false) =>
        Error(model, "answers", JsonObjectValue.Empty, message, aborted);

    /// <summary>pi-ai <c>imageErrorResult</c>: <c>{api, provider, model, output: [], stopReason, errorMessage, timestamp}</c>.</summary>
    public static JsonObjectValue ImagesError(JsonObjectValue model, string message, bool aborted = false) =>
        Error(model, "output", JsonArrayValue.Empty, message, aborted);
}

/// <summary>Model access without providers (pi-ai <c>createModels()</c> with nothing registered): every lookup misses and every request
/// fails. Enough for a Harness that runs only application tasks.</summary>
public sealed class EmptyDurableModels : IDurableModels
{
    /// <summary>The shared instance.</summary>
    public static EmptyDurableModels Instance { get; } = new();

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => null;

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => throw Unknown(model);

    /// <inheritdoc/>
    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => Task.FromException<AssistantMessage>(Unknown(model));

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        throw Unknown(model);

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(Unknown(model));

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException(Unknown(model));

    private static InvalidOperationException Unknown(DurableModel model) => new($"Unknown provider: {model.Provider}");
}

/// <summary>pi-ai <c>withClientIdentity</c> over <see cref="IDurableModels"/> (Pi v1.5.0).</summary>
public static class DurableModelIdentity
{
    /// <summary>
    /// <paramref name="models"/> with <paramref name="identity"/> as the default identity of its requests (pi-ai <c>withClientIdentity</c>):
    /// a request's <see cref="DurableStreamOptions.Identity"/> overrides it field by field, and it overrides the identity
    /// <paramref name="models"/> was created with. Everything else is delegated unchanged. Deferred, classifier and image calls carry
    /// identity through the same field-by-field merge.
    /// </summary>
    public static IDurableModels WithClientIdentity(IDurableModels models, ClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(identity);
        return new IdentityModels(models, identity);
    }

    private sealed class IdentityModels(IDurableModels inner, ClientIdentity identity) : IDurableModels
    {
        private DurableStreamOptions WithIdentity(DurableStreamOptions? options) =>
            (options ?? new DurableStreamOptions()) with { Identity = ClientIdentities.Merge(identity, options?.Identity) };

        private DurableRequestOptions WithIdentity(DurableRequestOptions? options) =>
            (options ?? new DurableRequestOptions()) with { Identity = ClientIdentities.Merge(identity, options?.Identity) };

        public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => inner.StreamSimple(model, messages, WithIdentity(options), cancellationToken);

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, WithIdentity(options), cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, WithIdentity((DurableRequestOptions?)null), cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, WithIdentity((DurableRequestOptions?)null), cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, WithIdentity((DurableRequestOptions?)null), cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
            CancellationToken cancellationToken) => inner.StreamDeferred(model, handle, WithIdentity(options), cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
            CancellationToken cancellationToken) => inner.FetchDeferredAsync(model, handle, WithIdentity(options), cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
            CancellationToken cancellationToken) => inner.CancelDeferredAsync(model, handle, WithIdentity(options), cancellationToken);

        public Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, DurableRequestOptions? options,
            CancellationToken cancellationToken) => inner.ClassifyAsync(model, context, WithIdentity(options), cancellationToken);

        public Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, DurableRequestOptions? options,
            CancellationToken cancellationToken) => inner.GenerateImagesAsync(model, context, WithIdentity(options), cancellationToken);

        public ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) => inner.GetModelsOfType(type, provider);

        public Task<ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
            inner.GetAvailableOfTypeAsync(type, provider, cancellationToken);

        public JsonObjectValue? GetModelOfType(string type, string provider, string id) => inner.GetModelOfType(type, provider, id);

        public Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
            inner.ClassifyAsync(model, context, WithIdentity((DurableRequestOptions?)null), cancellationToken);

        public Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
            inner.GenerateImagesAsync(model, context, WithIdentity((DurableRequestOptions?)null), cancellationToken);
    }
}
