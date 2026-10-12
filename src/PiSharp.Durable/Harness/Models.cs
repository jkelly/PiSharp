// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/models.ts (the Models interface) and packages/ai/src/types.ts (Model,
// SimpleStreamOptions, DeferredHandle), reduced to what packages/durable uses: getModel, streamSimple, completeSimple, streamDeferred,
// fetchDeferred and cancelDeferred. Catalog refresh, auth and login, images and classifiers stay with the host.
//
// Mapping: a pi-ai Model is a DurableModel carrying the catalog entry's wire JSON plus the fields durable reads; the request context is
// the transcript (pi-ai wire messages; system prompt and tools ride as positional system messages, so no other context field is
// needed); stream events and the final message are PiSharp.Contracts types (PiWireJson converts both ways). P8 adds the production
// adapter over PiSharp.AI transports; until then hosts and tests supply their own implementation.
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

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
    /// <summary>The complete catalog entry as pi-ai wire JSON, when the host has it (costs, compat, thinking maps).</summary>
    public JsonObjectValue? Wire { get; init; }

    /// <summary>The model identity PiSharp.AI transports take.</summary>
    public ModelDescriptor Descriptor => new(Id, Api, Provider);
}

/// <summary>Options of one simple request (pi-ai <c>ModelsSimpleStreamOptions</c>, the fields durable sets).</summary>
public sealed record DurableStreamOptions
{
    /// <summary>Preferred transport.</summary>
    public string? Transport { get; init; }
    /// <summary>HTTP request timeout.</summary>
    public double? TimeoutMs { get; init; }
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
