// Decision 0011 (run W2, WU6): the Web Agent's IDurableModels, mirroring PiSharp.Cli's Durable/DurableModels.cs: NativeDurableModels with
// the HostTransport set to AccountRouter.RouteAsync, so every chat request goes through its chat's account, and GetModel answering exactly
// the RuntimeModelCatalog's chat models (a model listed on the pages always resolves, a model not listed never does). Unlike the CLI there
// is no fallback to NativeDurableModels' own routes: no environment reader and no ResolveApiKey are given, and the router never returns null
// (it throws, which ends the stream with the router's error). Code mode's model operations (models of other types, classification, image
// generation) are not routed through accounts yet, so they keep IDurableModels' defaults (none).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.WebAgent.Routing;

/// <summary>The chat models of <see cref="RuntimeModelCatalog"/>, streamed through each chat's account (<see cref="AccountRouter"/>).</summary>
public sealed class WebDurableModels : IDurableModels, IAsyncDisposable
{
    private readonly RuntimeModelCatalog catalog;
    private readonly NativeDurableModels native;
    private int disposed;

    /// <summary>The models of <paramref name="catalog"/> through <paramref name="router"/>.</summary>
    public WebDurableModels(RuntimeModelCatalog catalog, AccountRouter router)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(router);
        this.catalog = catalog;
        native = new NativeDurableModels(new NativeDurableModelsOptions
        {
            Environment = null,
            ResolveApiKey = null,
            HttpHandler = router.ProviderHandler,
            TimeProvider = router.Time,
            HostTransport = router.RouteAsync,
        });
    }

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(modelId);
        return catalog.Find(provider, modelId)?.Durable;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => native.StreamSimple(model, messages, options, cancellationToken);

    /// <inheritdoc/>
    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => native.CompleteSimpleAsync(model, messages, options, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        native.StreamDeferred(model, handle, cancellationToken);

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        native.FetchDeferredAsync(model, handle, cancellationToken);

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        native.CancelDeferredAsync(model, handle, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
        CancellationToken cancellationToken) => native.StreamDeferred(model, handle, options, cancellationToken);

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
        CancellationToken cancellationToken) => native.FetchDeferredAsync(model, handle, options, cancellationToken);

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options,
        CancellationToken cancellationToken) => native.CancelDeferredAsync(model, handle, options, cancellationToken);

    /// <summary>Releases the native adapter (the router and its routes are closed by their own owner).</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) native.Dispose();
        return ValueTask.CompletedTask;
    }
}
