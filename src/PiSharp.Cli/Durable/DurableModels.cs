// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-runtime.ts (ModelRuntime as the durable agent uses
// it: getModel, getAvailableSnapshot, hasConfiguredAuth, and the Models streams the Harness calls) and packages/ai/src/models.ts
// (getSupportedThinkingLevels, clampThinkingLevel). Decision 0005's CLI model adapter: pi's model registry (built-in catalogs, models.json,
// persisted catalogs) and auth (auth.json with OAuth refresh, models.json keys and headers, the environment) behind IDurableModels.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.Cli.Durable;

/// <summary>
/// pi's model runtime for the durable Harness (upstream <c>ModelRuntime</c>). Models come from PiSharp.Cli's <see cref="ModelRegistry"/>
/// (built-in catalogs, models.json custom providers and models, persisted catalogs). Requests stream through
/// <see cref="NativeDurableModels"/>, whose <see cref="NativeDurableModelsOptions.HostTransport"/> is the CLI's live route of the model
/// (<see cref="LazyModelTransport"/>): every provider PiSharp.Cli authenticates, with auth.json credentials and their OAuth refresh,
/// models.json keys and headers, GitHub Copilot token exchange, OpenAI Codex OAuth, Amazon Bedrock and Google Vertex ambient credentials,
/// Cloudflare account ids and Azure endpoint configuration. A model without a live route falls back to NativeDurableModels' own routes with
/// the registry's key (<see cref="NativeDurableModelsOptions.ResolveApiKey"/>).
/// </summary>
internal sealed class DurableModels : IDurableModels, IAsyncDisposable
{
    private readonly LiveSessionRuntime runtime;
    private readonly NativeDurableModels native;
    private readonly IDurableModels? streams;
    private readonly HttpMessageHandler? handler;
    private readonly ConcurrentDictionary<(string Provider, string Id), LazyModelTransport> routes = new();
    private int disposed;

    private DurableModels(LiveSessionRuntime runtime, ModelRegistry registry, IDurableModels? streams)
    {
        this.runtime = runtime;
        Registry = registry;
        this.streams = streams;
        handler = runtime.CreateHttpHandler();
        native = new NativeDurableModels(new NativeDurableModelsOptions
        {
            Environment = runtime.ReadEnvironment,
            HttpHandler = handler,
            TimeProvider = runtime.Time,
            HostTransport = HostTransportAsync,
            ResolveApiKey = (model, cancellationToken) => ValueTask.FromResult(Registry.Find(model.Provider, model.Id) is { } entry
                ? Registry.ResolveRequestAuth(entry, out _)?.ApiKey : null),
        });
    }

    /// <summary>pi's model runtime over <paramref name="runtime"/>'s registry (model-runtime.ts <c>ModelRuntime.create</c>).</summary>
    /// <param name="streams">Replaces the provider streams (tests: a scripted model); the catalog still comes from the registry.</param>
    internal static async Task<DurableModels> CreateAsync(LiveSessionRuntime runtime, IDurableModels? streams = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var registry = await runtime.CreateModelRegistryAsync(cancellationToken).ConfigureAwait(false);
        return new DurableModels(runtime, registry, streams);
    }

    /// <summary>The registry the models and their auth come from.</summary>
    internal ModelRegistry Registry { get; }

    /// <summary>Whether <paramref name="entry"/> is a chat model the CLI can stream (a live route, or an extension stream).</summary>
    private bool Routable(RegistryModel entry) => entry.Type == CatalogModelType.Chat && !VirtualModels.IsVirtual(entry) &&
        (LiveSessionSelection.SupportedApi(entry.Provider, entry.Api) || Registry.CustomStream(entry.Api) is not null);

    /// <summary>A registry entry as a pi-ai model carrying its JSON.</summary>
    internal static DurableModel ToModel(RegistryModel entry) => new()
    {
        Id = entry.Id, Name = entry.Name ?? entry.Id, Api = entry.Api, Provider = entry.Provider, BaseUrl = entry.BaseUrl,
        Reasoning = entry.Reasoning, Input = entry.Input.IsDefaultOrEmpty ? ["text"] : entry.Input,
        ContextWindow = entry.ContextWindow, MaxTokens = entry.MaxTokens,
        Wire = JsonValue.Parse(entry.ToJsonString()) as JsonObjectValue,
    };

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(modelId);
        return Registry.Find(provider, modelId) is { Type: CatalogModelType.Chat } entry && !VirtualModels.IsVirtual(entry) ? ToModel(entry) : null;
    }

    /// <summary>model-runtime.ts <c>getAvailableSnapshot</c>: the chat models with configured auth, in the registry's order, that the CLI
    /// can stream.</summary>
    internal IReadOnlyList<DurableModel> GetAvailableSnapshot() => [.. Registry.GetAvailable().Where(Routable).Select(ToModel)];

    /// <summary>model-runtime.ts <c>hasConfiguredAuth</c>.</summary>
    internal bool HasConfiguredAuth(string provider) => Registry.HasConfiguredAuth(provider);

    /// <summary>pi-ai <c>getSupportedThinkingLevels</c>: from the model's catalog JSON (its <c>thinkingLevelMap</c>); a model without one
    /// supports off through high when it reasons, else only off.</summary>
    internal static ImmutableArray<string> SupportedThinkingLevels(DurableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Wire is { } wire) return ProviderTranscriptAccess.SupportedThinkingLevels(wire.ToData());
        return model.Reasoning ? ["off", "minimal", "low", "medium", "high"] : ["off"];
    }

    /// <summary>pi-ai <c>clampThinkingLevel</c>.</summary>
    internal static string ClampThinkingLevel(DurableModel model, string level) => ThinkingLevels.Clamp(SupportedThinkingLevels(model), level);

    /// <summary>The CLI's live route of <paramref name="model"/>: its main route, or its summary route (pi's compaction summaries) when the
    /// request asks for another output cap or no caching. Null without a live route.</summary>
    private ValueTask<IChatTransport?> HostTransportAsync(DurableModel model, DurableStreamOptions? options, CancellationToken cancellationToken)
    {
        if (Registry.Find(model.Provider, model.Id) is not { } entry || !Routable(entry)) return ValueTask.FromResult<IChatTransport?>(null);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var route = routes.GetOrAdd((model.Provider, model.Id), _ => new LazyModelTransport(entry, model.Descriptor, () => Registry, runtime));
        if (options?.MaxTokens is not null || options?.CacheRetention == "none")
        {
            var maximum = options?.MaxTokens is { } cap && double.IsFinite(cap) && cap >= 1 ? (int)Math.Min(cap, int.MaxValue)
                : (int)Math.Clamp(model.MaxTokens, 1, int.MaxValue);
            return ValueTask.FromResult<IChatTransport?>(route.Summary(maximum, options?.Reasoning));
        }
        return ValueTask.FromResult<IChatTransport?>(route);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => (streams ?? native).StreamSimple(model, messages, options, cancellationToken);

    /// <inheritdoc/>
    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => (streams ?? native).CompleteSimpleAsync(model, messages, options, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        (streams ?? native).StreamDeferred(model, handle, cancellationToken);

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        (streams ?? native).FetchDeferredAsync(model, handle, cancellationToken);

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        (streams ?? native).CancelDeferredAsync(model, handle, cancellationToken);

    /// <summary>Closes the live connections.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var failures = new List<Exception>();
        foreach (var route in routes.Values)
            try { await route.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        routes.Clear();
        // The runtime's handlers stay caller-owned, as LiveSessionConnection leaves them.
        native.Dispose();
        if (failures.Count > 0) throw new AggregateException("Durable model connections failed to close.", failures);
    }
}
