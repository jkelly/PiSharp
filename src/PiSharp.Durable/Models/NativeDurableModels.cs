// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/models.ts (createModels, setProvider, getModels/getModel,
// applyAuth, streamSimple, completeSimple, streamDeferred, fetchDeferred, cancelDeferred), packages/ai/src/api/lazy.ts (lazyStream: setup
// failures end the stream with an error message), packages/ai/src/auth/helpers.ts (envApiKeyAuth: a stored key, else the first set
// variable) and packages/ai/src/env-api-keys.ts. The provider set is pi-ai's builtinProviders() with their catalog shards; requests run
// over PiSharp.AI's native transports (see NativeModelRoutes).
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Providers;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Options of <see cref="NativeDurableModels"/>.</summary>
public sealed record NativeDurableModelsOptions
{
    /// <summary>The built-in providers to register (pi-ai <c>setProvider</c> for each); null registers every built-in provider with a
    /// catalog. Unknown ids are refused.</summary>
    public IReadOnlyCollection<string>? Providers { get; init; }

    /// <summary>Host catalogs (a models.json layer, a remote catalog); each registers its provider and replaces that provider's built-in
    /// catalog.</summary>
    public IReadOnlyList<FrozenModelCatalog>? Catalogs { get; init; }

    /// <summary>Reads one environment variable for the provider key lookup; an empty value is absent. Null: no environment keys. The
    /// portable core never reads the process environment itself (as PiSharp.AI's <see cref="ProviderEnvironmentKeys"/>), so a host that
    /// wants pi-ai's process.env behavior passes System.Environment's GetEnvironmentVariable.</summary>
    public Func<string, string?>? Environment { get; init; }

    /// <summary>
    /// The host's API key for a request (a stored credential, an OAuth access token): consulted first, as pi-ai's stored credential
    /// wins over the environment. Null or empty falls back to the provider's environment variables.
    /// </summary>
    public Func<DurableModel, CancellationToken, ValueTask<string?>>? ResolveApiKey { get; init; }

    /// <summary>The HTTP handler every request is sent through; caller-owned. Null: a handler owned by the instance (redirects off).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Clock of request timestamps, <c>durationMs</c> and the <c>timeoutMs</c> deadline; default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Production <see cref="IDurableModels"/> over PiSharp.AI (a port of pi-ai <c>createModels()</c> with the built-in providers registered).
/// Models come from the built-in catalog shards (or host catalogs); API keys come from <see cref="NativeDurableModelsOptions.ResolveApiKey"/>,
/// else the provider's environment variables read through <see cref="NativeDurableModelsOptions.Environment"/>. Each request binds the model's native protocol with that key and streams through
/// <see cref="ChatClient"/>, so a request failure always ends the stream with an <c>error</c> or <c>aborted</c> message.
/// </summary>
/// <remarks>
/// <para>Streams <c>openai-completions</c>, <c>openai-responses</c>, <c>anthropic-messages</c>, <c>google-generative-ai</c>,
/// <c>mistral-conversations</c> and <c>pi-messages</c> models. Providers whose auth resolves per request (github-copilot, openai-codex,
/// amazon-bedrock, google-vertex, cloudflare-workers-ai, cloudflare-ai-gateway, azure) and Anthropic OAuth or auth tokens need the
/// host's authentication (the CLI adapter, P12); their requests end with an error naming the gap.</para>
/// <para><see cref="DurableStreamOptions"/> mapping. Supported: <c>Reasoning</c> (clamped to the model's levels; null is <c>off</c>),
/// <c>SessionId</c>, <c>Headers</c>, <c>CacheRetention</c>, <c>MaxTokens</c> (default the model's <c>maxTokens</c>), <c>Metadata.user_id</c>
/// (anthropic-messages), and <c>TimeoutMs</c>, which this adapter enforces as an idle deadline (no event for that long ends the request
/// with <c>error</c> "Request timed out."). <c>MaxRetries</c> and <c>MaxRetryDelayMs</c> reach google-generative-ai only; the other
/// native transports make one attempt, so durable generation retries own retrying. Unsupported: <c>Transport</c> (only openai-codex
/// reads it upstream), other <c>Metadata</c> fields (no upstream provider reads them) and <c>Deferred</c> (no pi-ai 1.1.0 provider
/// implements deferred responses; the request runs in the foreground, as upstream providers without deferred support do). Not mapped:
/// pi-ai <c>clampMaxTokensToContext</c> (the cap is not reduced to the context room the transcript leaves).</para>
/// </remarks>
public sealed class NativeDurableModels : IDurableModels, IDisposable
{
    private readonly ImmutableDictionary<string, FrozenModelCatalog?> providers;
    private readonly Func<string, string?> environment;
    private readonly Func<DurableModel, CancellationToken, ValueTask<string?>>? resolveApiKey;
    private readonly HttpMessageHandler handler;
    private readonly bool ownsHandler;
    private readonly TimeProvider time;
    private readonly ModelCache models = new();
    private int disposed;

    /// <summary>Creates the collection.</summary>
    public NativeDurableModels(NativeDurableModelsOptions? options = null)
    {
        options ??= new();
        var builder = ImmutableDictionary.CreateBuilder<string, FrozenModelCatalog?>(StringComparer.Ordinal);
        foreach (var id in options.Providers ?? DurableBuiltinCatalog.Providers)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (!DurableBuiltinCatalog.ShardHashes.ContainsKey(id)) throw new ArgumentException($"Unknown built-in provider: {id}", nameof(options));
            builder[id] = null; // Loaded on first use.
        }
        foreach (var catalog in options.Catalogs ?? [])
        {
            ArgumentNullException.ThrowIfNull(catalog);
            builder[catalog.Provider] = catalog;
        }
        providers = builder.ToImmutable();
        environment = options.Environment ?? (_ => null);
        resolveApiKey = options.ResolveApiKey;
        handler = options.HttpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false };
        ownsHandler = options.HttpHandler is null;
        time = options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>The registered provider ids, in ordinal order (pi-ai <c>getProviders</c>).</summary>
    public IReadOnlyList<string> Providers => [.. providers.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The environment variables that may hold <paramref name="provider"/>'s API key, in lookup order; empty for a provider without
    /// one (pi-ai <c>getApiKeyEnvVars</c>; for anthropic only <c>ANTHROPIC_API_KEY</c> is used here).</summary>
    public static IReadOnlyList<string> ApiKeyVariables(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider == "anthropic") return [ProviderEnvironmentKeys.AnthropicApiKey];
        return ProviderEnvironmentKeys.GetApiKeyVariables(provider) ?? [];
    }

    /// <summary>Whether this adapter can stream <paramref name="model"/> (its API has a native route and its provider needs no host auth).</summary>
    public static bool Supports(DurableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return NativeModelRoutes.Supports(model.Provider, model.Api);
    }

    /// <summary>The chat models of one registered provider, or of every registered provider (pi-ai <c>getModels</c>); a catalog that fails to
    /// load yields no models.</summary>
    public IReadOnlyList<DurableModel> GetModels(string? provider = null)
    {
        if (provider is not null) return Catalog(provider) is { } catalog ? models.Of(catalog) : [];
        return [.. providers.Keys.Order(StringComparer.Ordinal).SelectMany(id => Catalog(id) is { } catalog ? models.Of(catalog) : [])];
    }

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(modelId);
        return GetModels(provider).FirstOrDefault(model => model.Id == modelId);
    }

    /// <summary>A catalog row as a <see cref="DurableModel"/> carrying its wire JSON.</summary>
    internal static DurableModel FromCatalog(FrozenCatalogModel model)
    {
        var raw = model.Raw.Value;
        double Number(string name) => raw.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;
        return new DurableModel
        {
            Id = model.Id, Name = model.Name, Api = model.DeclaredApi, Provider = model.Provider, BaseUrl = model.BaseUrl,
            Reasoning = raw.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == System.Text.Json.JsonValueKind.True,
            Input = [.. raw.GetProperty("input").EnumerateArray().Select(value => value.GetString()!)],
            ContextWindow = Number("contextWindow"), MaxTokens = Number("maxTokens"),
            Wire = (JsonObjectValue)Chord.JsonValue.FromData(model.Raw),
        };
    }

    private FrozenModelCatalog? Catalog(string provider)
    {
        if (!providers.TryGetValue(provider, out var catalog)) return null;
        if (catalog is not null) return catalog;
        try { return DurableBuiltinCatalog.Get(provider); }
        catch (Exception error) when (error is InvalidOperationException or CatalogReadException) { return null; }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<Chord.JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return StreamAsync(model, messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<StreamEvent> StreamAsync(DurableModel model, ImmutableArray<Chord.JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeMilliseconds();
        Prepared? prepared = null;
        string? failure = null;
        try { prepared = await PrepareAsync(model, messages, options, timestamp, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { failure = "Request was aborted"; }
        catch (Exception error) { failure = error.Message; }
        if (prepared is null)
        {
            // lazyStream: a setup failure (unknown provider, missing auth, unroutable API, invalid transcript) is the stream's error event.
            var reason = cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
            yield return new StreamError(reason, ErrorMessage(model, timestamp, reason, failure ?? "Request failed"));
            yield break;
        }
        using var provider = prepared.Provider;
        using var deadline = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var idle = options?.TimeoutMs is { } milliseconds && double.IsFinite(milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue)) : (TimeSpan?)null;
        var client = new ChatClient(provider.Transport) { TimeProvider = time };
        await using var run = await client.StartWithAbortSettlementAsync(prepared.Request, linked.Token, timestamp).ConfigureAwait(false);
        await using var events = run.ReadEventsAsync().GetAsyncEnumerator(CancellationToken.None);
        while (true)
        {
            // The deadline runs only while waiting for the next event, never while the caller handles one.
            if (idle is { } span) deadline.CancelAfter(span);
            if (!await events.MoveNextAsync().ConfigureAwait(false)) yield break;
            if (idle is not null && !deadline.IsCancellationRequested) deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            var value = events.Current;
            if (value is StreamTerminalEvent terminal)
            {
                if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && terminal.Reason == StopReason.Aborted)
                    value = TimedOut(terminal);
                yield return value;
                yield break;
            }
            yield return value;
        }
    }

    /// <summary>An idle deadline that cancelled the request: an <c>error</c> (retryable) instead of a caller abort, as an SDK timeout is.</summary>
    private static StreamError TimedOut(StreamTerminalEvent terminal)
    {
        var properties = (terminal.Message.ExtraProperties ?? JsonFields.Empty).Set("errorMessage", JsonData.Parse("\"Request timed out.\""));
        return new StreamError(StopReason.Error, terminal.Message with { StopReason = StopReason.Error, ExtraProperties = properties });
    }

    private sealed record Prepared(NativeHttpModelProvider Provider, ChatRequest Request);

    /// <summary>pi-ai <c>applyAuth</c> and the provider's <c>streamSimple</c> setup: provider, catalog row, key, binding, level.</summary>
    private async Task<Prepared> PrepareAsync(DurableModel model, ImmutableArray<Chord.JsonValue> messages, DurableStreamOptions? options,
        long timestamp, CancellationToken cancellationToken)
    {
        if (!providers.ContainsKey(model.Provider)) throw new InvalidOperationException($"Unknown provider: {model.Provider}");
        var metadata = Metadata(model);
        var descriptor = model.Descriptor;
        if (!NativeModelRoutes.Supports(model.Provider, model.Api))
            throw new NotSupportedException(NativeModelRoutes.Apis.Contains(model.Api)
                ? $"Provider {model.Provider} needs host authentication that NativeDurableModels does not provide"
                : $"The {model.Api} API of provider {model.Provider} has no durable route in PiSharp yet");
        var transcript = Transcript(messages);
        var key = await ApiKeyAsync(model, cancellationToken).ConfigureAwait(false);
        var raw = metadata.Value;
        var maximum = options?.MaxTokens is { } requested && double.IsFinite(requested) && requested >= 1
            ? (int)Math.Min(Math.Floor(requested), int.MaxValue)
            : raw.TryGetProperty("maxTokens", out var cap) && cap.TryGetDouble(out var modelCap) && modelCap >= 1 ? (int)Math.Min(modelCap, int.MaxValue) : 4096;
        var settings = Settings(options, maximum);
        var provider = NativeModelRoutes.Bind(descriptor, metadata, key, settings, handler);
        try
        {
            var level = ThinkingLevels.Clamp(provider.GetSupportedThinkingLevels(descriptor), options?.Reasoning ?? "off");
            var thinkingCap = NativeModelRoutes.ThinkingCap(raw, model.Api, level, maximum);
            if (thinkingCap != maximum)
            {
                provider.Dispose();
                provider = NativeModelRoutes.Bind(descriptor, metadata, key, settings with { MaxTokens = thinkingCap }, handler);
            }
            var request = new ChatRequest(descriptor, transcript, timestamp) { ThinkingLevel = level, SessionId = options?.SessionId };
            return new(provider, request);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>The request settings of <paramref name="options"/> at output cap <paramref name="maximum"/>.</summary>
    internal static NativeRouteSettings Settings(DurableStreamOptions? options, int maximum) => new()
    {
        MaxTokens = maximum,
        CacheRetention = options?.CacheRetention is "none" or "long" ? options.CacheRetention : "short",
        Headers = options?.Headers,
        UserId = options?.Metadata is { } metadata && metadata.TryGetValue("user_id", out var user) && user is JsonStringValue text ? text.Value : null,
        MaxRetries = options?.MaxRetries,
        MaxRetryDelayMs = options?.MaxRetryDelayMs,
    };

    /// <summary>The model's catalog row: its wire JSON, else the registered catalog's row, with the model's own <c>baseUrl</c> (pi-ai
    /// streams at <c>model.baseUrl</c>).</summary>
    private JsonData Metadata(DurableModel model)
    {
        var wire = model.Wire;
        if (wire is null)
        {
            var catalog = Catalog(model.Provider);
            if (catalog is null || !catalog.TryGetModel(CatalogModelType.Chat, model.Id, out var row))
                throw new InvalidOperationException($"Unknown model: {model.Provider}/{model.Id}");
            wire = (JsonObjectValue)Chord.JsonValue.FromData(row.Raw);
        }
        if (model.BaseUrl.Length > 0 && !(wire.TryGetValue("baseUrl", out var url) && url is JsonStringValue text && text.Value == model.BaseUrl))
            wire = wire.SetItem("baseUrl", model.BaseUrl);
        return wire.ToData();
    }

    /// <summary>pi-ai wire messages as transcript entries (the system prompt and tools ride in system messages).</summary>
    internal static ImmutableArray<TranscriptEntry> Transcript(ImmutableArray<Chord.JsonValue> messages)
    {
        if (messages.IsDefault) throw new ArgumentException("The transcript is missing.");
        return [.. messages.Select((message, index) => message is JsonObjectValue body && body.TryGetValue("role", out var role) &&
            role is JsonStringValue name && name.Value.Length > 0
                ? new TranscriptEntry(name.Value, body.ToData())
                : throw new ArgumentException($"Transcript message {index} has no role."))];
    }

    /// <summary>envApiKeyAuth: the host's key, else the first set variable of the provider.</summary>
    private async ValueTask<string> ApiKeyAsync(DurableModel model, CancellationToken cancellationToken)
    {
        if (resolveApiKey is not null && await resolveApiKey(model, cancellationToken).ConfigureAwait(false) is { Length: > 0 } stored) return stored;
        foreach (var name in ApiKeyVariables(model.Provider))
            if (ProviderEnvironmentKeys.Value(environment, name) is { } value) return value;
        if (model.Provider == "anthropic" && (ProviderEnvironmentKeys.Value(environment, ProviderEnvironmentKeys.AnthropicOAuthToken) is not null ||
            ProviderEnvironmentKeys.Value(environment, ProviderEnvironmentKeys.AnthropicAuthToken) is not null))
            throw new InvalidOperationException("Provider is not configured: anthropic (ANTHROPIC_OAUTH_TOKEN and ANTHROPIC_AUTH_TOKEN need the " +
                "host's Anthropic authentication; set ANTHROPIC_API_KEY)");
        throw new InvalidOperationException($"Provider is not configured: {model.Provider}");
    }

    /// <summary>lazy.ts createSetupErrorMessage.</summary>
    internal static AssistantMessage ErrorMessage(DurableModel model, long timestamp, StopReason reason, string text) =>
        new(model.Api, model.Provider, model.Id, timestamp, [], TokenUsage.Zero, reason,
            JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(text))));

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<Chord.JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        AssistantMessage? final = null;
        await foreach (var value in StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false))
            if (value is StreamTerminalEvent terminal) final = terminal.Message;
        return final ?? throw new InvalidOperationException("The stream ended without a terminal event.");
    }

    /// <summary>pi-ai <c>streamDeferred</c>: no PiSharp.AI transport implements deferred responses (nor does any pi-ai 1.1.0 provider), so the
    /// stream ends with an error message naming the gap, as pi-ai's does for a provider without <c>fetchDeferred</c>.</summary>
    public async IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new StreamError(StopReason.Error, ErrorMessage(model, time.GetUtcNow().ToUnixTimeMilliseconds(), StopReason.Error, DeferredGap(model)));
    }

    /// <summary>pi-ai <c>fetchDeferred</c>: the error message of <see cref="StreamDeferred"/>.</summary>
    public async Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        AssistantMessage? final = null;
        await foreach (var value in StreamDeferred(model, handle, cancellationToken).ConfigureAwait(false))
            if (value is StreamTerminalEvent terminal) final = terminal.Message;
        return final!;
    }

    /// <summary>pi-ai <c>cancelDeferred</c>: throws <see cref="NotSupportedException"/>, as pi-ai throws for a provider without
    /// <c>cancelDeferred</c>.</summary>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        return Task.FromException(new NotSupportedException(DeferredGap(model)));
    }

    private string DeferredGap(DurableModel model) => providers.ContainsKey(model.Provider)
        ? $"Provider {model.Provider} does not support deferred responses (PiSharp.AI has no deferred fetch or cancel)"
        : $"Unknown provider: {model.Provider}";

    /// <summary>Releases the owned HTTP handler; a caller-supplied handler stays open.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (ownsHandler) handler.Dispose();
    }

    /// <summary>The <see cref="DurableModel"/>s of each loaded catalog, built once.</summary>
    private sealed class ModelCache
    {
        private readonly ConditionalWeakTable<FrozenModelCatalog, IReadOnlyList<DurableModel>> cache = new();

        public IReadOnlyList<DurableModel> Of(FrozenModelCatalog catalog) => cache.GetValue(catalog, owned =>
            [.. owned.Models.Where(model => model.Type == CatalogModelType.Chat).Select(FromCatalog)]);
    }
}
