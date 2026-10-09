// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-runtime.ts (provider composition,
// availability snapshot, getError, getProviderAuthStatus, getAuth, registerVirtualModel, resolveModel),
// packages/coding-agent/src/core/model-registry.ts (getApiKeyAndHeaders, getProviderDisplayName),
// packages/coding-agent/src/core/provider-composer.ts (composeApiKeyAuth check/resolve) and packages/ai/src/models.ts
// (getModels, getAvailable, checkProviderAuth, refresh), packages/ai/src/providers/{github-copilot,openai,radius}.ts (filters, radius catalog).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>The registry's effects: models.json path, environment reader, stored <c>auth.json</c> credentials, the
/// <c>!command</c> switch, the remote catalog store and client, and the file probe used for ambient credentials.</summary>
internal sealed record ModelRegistryOptions
{
    internal string? ModelsPath { get; init; }
    internal Func<string, string?> Environment { get; init; } = _ => null;
    internal IReadOnlyDictionary<string, ProviderStoredCredential> StoredCredentials { get; init; } = ImmutableDictionary<string, ProviderStoredCredential>.Empty;
    internal Func<string, string?>? RunCommand { get; init; }
    internal IModelsStore? ModelsStore { get; init; }
    internal Func<HttpClient>? CreateCatalogClient { get; init; }
    internal string? CatalogBaseUrl { get; init; }
    internal Func<string, bool>? FileExists { get; init; }
    internal string? Home { get; init; }
    internal Func<DateTimeOffset>? Now { get; init; }
}

/// <summary>Resolved request authentication of one model (getApiKeyAndHeaders).</summary>
internal sealed record ModelRequestAuth(string? ApiKey, IReadOnlyDictionary<string, string>? Headers, IReadOnlyDictionary<string, string>? Environment, string? Source)
{
    public override string ToString() => $"ModelRequestAuth ({Source}) [redacted]";
}

/// <summary>Built-in catalogs, the models.json layer, remote overlays and virtual models, with credential-blind listing and lazy,
/// read-only availability checks.</summary>
internal sealed class ModelRegistry
{
    private sealed record Composed(string Id, List<RegistryModel> Models, bool Overlaid, string? Error);
    private sealed record Registered(RegistryModel Model, VirtualModelDefinition Definition);

    private readonly ModelRegistryOptions options;
    private readonly Dictionary<string, RemoteCatalogOverlay> overlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Registered>> virtualModels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> authChecks = new(StringComparer.Ordinal);
    private ImmutableArray<RegistryModel>? radiusDynamic;
    private ModelsJsonConfig config;
    private List<Composed> providers = [];
    internal ConfigValueResolver Values { get; }

    private ModelRegistry(ModelRegistryOptions options)
    {
        this.options = options;
        Values = new(options.Environment, options.RunCommand);
        config = ModelsJsonConfig.Load(options.ModelsPath);
        var client = options.CreateCatalogClient ?? (() => new HttpClient());
        foreach (var provider in BuiltinProviders.All.Where(provider => provider.Id != "radius"))
            overlays[provider.Id] = new(provider.Id, client, options.CatalogBaseUrl, BuiltinModelCatalog.GeneratedAtUnixMilliseconds, options.Now);
        Rebuild();
    }

    /// <summary>A registry from built-ins and models.json, without restoring persisted catalogs.</summary>
    internal static ModelRegistry Create(ModelRegistryOptions? options = null) => new(options ?? new());

    /// <summary>Source ModelRuntime.create: compose, then restore persisted catalogs without network (refresh allowNetwork:false).</summary>
    internal static async Task<ModelRegistry> CreateAsync(ModelRegistryOptions options, CancellationToken cancellationToken)
    {
        var registry = new ModelRegistry(options);
        await registry.RefreshAsync(allowNetwork: false, force: null, providers: null, cancellationToken).ConfigureAwait(false);
        return registry;
    }

    // Built-in entries are immutable (every change goes through RegistryModel.With), so one projection per process is shared.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<RegistryModel>> Builtins = new(StringComparer.Ordinal);
    private static IReadOnlyList<RegistryModel> BuiltinModels(string provider) => !BuiltinModelCatalog.Has(provider) ? [] :
        Builtins.GetOrAdd(provider, id => [.. BuiltinModelCatalog.Get(id).Models.Select(RegistryModel.FromCatalog)]);

    private IEnumerable<string> ProviderIds() => BuiltinProviders.All.Select(provider => provider.Id)
        .Concat(config.ProviderIds).Concat(extensionProviders.Keys).Concat(virtualModels.Keys).Distinct(StringComparer.Ordinal);

    // model-runtime.ts registerProvider/unregisterProvider: an extension's provider layer over the built-in and models.json layers.
    private readonly Dictionary<string, JsonObject> extensionProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<RegistryModel, PiSharp.AI.IChatTransport>> customStreams = new(StringComparer.Ordinal);

    /// <summary>Source registerProvider(name, config): validated alone first (a broken registration throws without changing the stored
    /// one); a re-registration merges defined values over the previous one.</summary>
    internal void RegisterExtensionProvider(string providerId, JsonObject configuration)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new InvalidOperationException("Provider id must not be empty.");
        if (configuration["hasStreamSimple"]?.GetValue<bool>() == true && JsonTree.String(configuration, "api") is null)
            throw new InvalidOperationException($"Provider {providerId}: \"api\" is required when registering streamSimple.");
        var effective = extensionProviders.TryGetValue(providerId, out var previous) ? (JsonObject)previous.DeepClone() : new JsonObject();
        foreach (var (key, value) in configuration) if (value is not null) effective[key] = value.DeepClone();
        var baseModels = BuiltinModels(providerId);
        _ = ModelProviderComposer.ApplyExtension(providerId, ModelProviderComposer.ApplyModelsJson(providerId, baseModels, config.GetProvider(providerId)), effective);
        extensionProviders[providerId] = effective;
        Rebuild();
    }

    internal void UnregisterExtensionProvider(string providerId)
    {
        if (extensionProviders.Remove(providerId)) Rebuild();
    }

    /// <summary>provider-composer.ts composeOAuthAuth / getAllModels for extension providers with <c>oauth</c>: the stored OAuth
    /// credential resolves through the extension's refreshToken and getApiKey, and modifyModels projects the chat models.</summary>
    internal IExtensionOAuthLayer? ExtensionOAuth { get => extensionOAuth; set { extensionOAuth = value; Rebuild(); } }
    private IExtensionOAuthLayer? extensionOAuth;

    /// <summary>Source registerApiProvider(streamSimple): models of this API stream through the extension (keyed by API, as pi-ai's
    /// API provider registry is).</summary>
    internal void RegisterCustomStream(string api, Func<RegistryModel, PiSharp.AI.IChatTransport> create) => customStreams[api] = create;
    internal Func<RegistryModel, PiSharp.AI.IChatTransport>? CustomStream(string api) => customStreams.GetValueOrDefault(api);

    private void Rebuild()
    {
        var next = new List<Composed>();
        foreach (var id in ProviderIds())
        {
            IReadOnlyList<RegistryModel> baseModels = id == "radius" && radiusDynamic is { } dynamic ? dynamic :
                overlays.TryGetValue(id, out var overlay) ? overlay.Apply(BuiltinModels(id)) : BuiltinModels(id);
            var providerConfig = config.GetProvider(id);
            var extension = extensionProviders.GetValueOrDefault(id);
            if (providerConfig is null && extension is null) { next.Add(new(id, [.. baseModels], false, null)); continue; }
            try
            {
                var composed = providerConfig is null ? [.. baseModels] : ModelProviderComposer.Compose(id, baseModels, providerConfig);
                var models = ModelProviderComposer.ApplyExtension(id, composed, extension);
                // getAllModels: an extension's modifyModels projects the chat models with the stored OAuth credential.
                if (extension is not null && Stored(id)?.Type == "oauth" && extensionOAuth?.ModifyModels(id, models) is { } modified) models = [.. modified];
                next.Add(new(id, models, true, null));
            }
            catch (InvalidOperationException error) { next.Add(new(id, [.. baseModels], false, error.Message)); }
        }
        providers = next;
        authChecks.Clear();
    }

    private IReadOnlyList<RegistryModel> ProviderModels(Composed provider)
    {
        var registered = virtualModels.TryGetValue(provider.Id, out var entries) ? entries.Values.Select(entry => entry.Model).ToList() : [];
        return registered.Count == 0 ? provider.Models : VirtualModels.WithVirtualModels(provider.Models, registered);
    }

    /// <summary>Source refresh(): models.json is reloaded; persisted catalogs are restored; with <paramref name="allowNetwork"/> the remote
    /// catalogs are revalidated. Returns per-provider errors.</summary>
    internal async Task<IReadOnlyDictionary<string, Exception>> RefreshAsync(bool allowNetwork, bool? force, IReadOnlyCollection<string>? providers,
        CancellationToken cancellationToken)
    {
        config = ModelsJsonConfig.Load(options.ModelsPath);
        var errors = new Dictionary<string, Exception>(StringComparer.Ordinal);
        var store = options.ModelsStore;
        if (store is not null)
        {
            foreach (var (id, overlay) in overlays)
            {
                if (providers is not null && !providers.Contains(id)) continue;
                try
                {
                    var stored = await store.ReadAsync(id, cancellationToken).ConfigureAwait(false);
                    stored = stored is null ? null : stored with { Models = new([.. stored.Models.OfType<JsonObject>().Where(KnownType).Select(model => (JsonNode?)model.DeepClone())]) };
                    await overlay.RefreshAsync(new(stored, allowNetwork, allowNetwork ? force : null, async publication =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (publication.Delete) await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                        else if (publication.Persist is { } persist) await store.WriteAsync(id, persist, cancellationToken).ConfigureAwait(false);
                        publication.Update?.Invoke();
                        return true;
                    }, cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { errors[id] = error; }
            }
            if (providers is null || providers.Contains("radius"))
            {
                // radius.ts refreshModels: a stored gateway catalog replaces the shipped baseline. The gateway fetch belongs to the Radius
                // login (IMPL-A1) and is not performed here.
                try
                {
                    if (await store.ReadAsync("radius", cancellationToken).ConfigureAwait(false) is { } stored)
                        radiusDynamic = [.. stored.Models.OfType<JsonObject>().Where(KnownType).Where(model => JsonTree.String(model, "provider") == "radius")
                            .Select(RegistryModel.FromJson)];
                }
                catch (Exception error) when (error is not OperationCanceledException) { errors["radius"] = error; }
            }
        }
        Rebuild();
        return errors;
        static bool KnownType(JsonObject model) => JsonTree.String(model, "type") is null or "chat" or "image" or "classifier";
    }

    /// <summary>Source getError: models.json load errors, then every provider composition error.</summary>
    internal string? GetError()
    {
        var errors = new List<string>();
        if (config.Error is { } loadError) errors.Add(loadError);
        foreach (var provider in providers) if (provider.Error is { } error) errors.Add($"Provider \"{provider.Id}\": {error}");
        return errors.Count > 0 ? string.Join("\n\n", errors) : null;
    }

    /// <summary>Every chat model, in provider order (getAll).</summary>
    internal IReadOnlyList<RegistryModel> GetAll() =>
        [.. providers.SelectMany(provider => ProviderModels(provider)).Where(model => model.Type == CatalogModelType.Chat)];

    /// <summary>Every model of a type, optionally for one provider (getModelsOfType).</summary>
    internal IReadOnlyList<RegistryModel> GetAllOfType(CatalogModelType type, string? provider = null) =>
        [.. providers.Where(entry => provider is null || entry.Id == provider).SelectMany(ProviderModels).Where(model => model.Type == type)];

    internal RegistryModel? Find(string provider, string id) =>
        GetAllOfType(CatalogModelType.Chat, provider).FirstOrDefault(model => model.Id == id);

    /// <summary>A catalog chat model that is not virtual (getPhysicalModel).</summary>
    internal RegistryModel? FindPhysical(string provider, string id) => Find(provider, id) is { } model && !VirtualModels.IsVirtual(model) ? model : null;

    internal IReadOnlyList<string> GetProviderIds() => [.. providers.Select(provider => provider.Id)];

    /// <summary>Chat models whose provider has a usable credential (getAvailable), with the provider's credential filters.</summary>
    internal IReadOnlyList<RegistryModel> GetAvailable() =>
        [.. providers.Where(provider => HasConfiguredAuth(provider.Id)).SelectMany(provider => Filter(provider.Id, ProviderModels(provider)
            .Where(model => model.Type == CatalogModelType.Chat).ToList()))];

    private IEnumerable<RegistryModel> Filter(string provider, IReadOnlyList<RegistryModel> models)
    {
        // github-copilot filterModels: an OAuth credential's availableModelIds limits the chat models (virtual models pass through).
        if (provider == "github-copilot" && Stored(provider) is { Type: "oauth", AvailableModelIds: { } ids })
            return models.Where(model => VirtualModels.IsVirtual(model) || ids.Contains(model.Id, StringComparer.Ordinal));
        return models;
    }

    /// <summary>auth-storage.ts read: a stored api_key's key is resolved as a config value (templates with its env, and <c>!command</c>
    /// through the cached shell runner); an unresolved key reads as absent.</summary>
    private ProviderStoredCredential? Stored(string provider) =>
        !options.StoredCredentials.TryGetValue(provider, out var stored) ? null :
        stored is { Type: "api_key", Key: { } key } ? stored with { Key = Values.Resolve(key, stored.Environment) } : stored;

    /// <summary>Source hasConfiguredAuth: the provider's auth check passes. Checks are cached until the next rebuild.</summary>
    internal bool HasConfiguredAuth(string provider) => CheckAuth(provider) is not null;

    /// <summary>The auth check's source label (checkProviderAuth), or null.</summary>
    internal string? CheckAuth(string provider) => authChecks.GetOrAdd(provider, ComputeAuth);

    private string? ComputeAuth(string providerId)
    {
        var composed = providers.FirstOrDefault(provider => provider.Id == providerId);
        if (composed is null) return null;
        var stored = Stored(providerId);
        BuiltinProviders.TryGet(providerId, out var builtin);
        if (builtin is null && !composed.Overlaid)
            return virtualModels.ContainsKey(providerId) ? "virtual" : null; // A provider of only virtual models needs no credentials.
        string? Inherited(ProviderStoredCredential? credential) =>
            builtin is null ? null : BuiltinProviders.CheckAuth(builtin, credential, options.Environment, options.FileExists, options.Home);
        if (!composed.Overlaid) return Inherited(stored);
        var providerConfig = ProviderConfig(providerId);
        if (stored?.Type == "oauth") return builtin?.OAuthName is not null || extensionOAuth?.Has(providerId) == true ? "OAuth" : null;
        var rawKey = ModelProviderComposer.ApiKey(providerConfig);
        // composeApiKeyAuth: OAuth-only providers get no fabricated API-key method.
        if (builtin is { Auth: ProviderAuthKind.OAuthOnly } && rawKey is null) return null;
        if (stored?.Type == "api_key")
        {
            if (stored.Key is { Length: > 0 }) return "stored credential";
            return Inherited(stored);
        }
        if (rawKey is not null)
        {
            if (ConfigValueResolver.IsCommand(rawKey)) return "configured API key";
            return ConfigValueResolver.GetEnvVarNames(rawKey).All(name => ProviderEnvironmentKeys.Value(options.Environment, name) is not null)
                ? "configured API key" : null;
        }
        return Inherited(null);
    }

    /// <summary>Source getProviderAuthStatus.</summary>
    internal ProviderAuthStatus GetProviderAuthStatus(string provider)
    {
        if (options.StoredCredentials.ContainsKey(provider)) return new(true, "stored");
        if (ModelProviderComposer.ConfiguredRequestAuthStatus(ProviderConfig(provider), Values) is { } configured) return configured;
        return CheckAuth(provider) is { } source ? new(true, "environment", source) : new(false);
    }

    /// <summary>Source getProviderDisplayName.</summary>
    internal string GetProviderDisplayName(string provider) =>
        config.GetProvider(provider) is { } providerConfig && JsonTree.String(providerConfig, "name") is { } configured ? configured :
        BuiltinProviders.TryGet(provider, out var builtin) ? builtin.Name : provider;

    internal JsonObject? GetProviderConfig(string provider) => ProviderConfig(provider);

    /// <summary>The provider's configuration for request auth: models.json, with an extension's apiKey, headers and authHeader over it
    /// (provider-composer.ts configuredApiKey/configuredHeaders).</summary>
    private JsonObject? ProviderConfig(string provider)
    {
        var configured = config.GetProvider(provider);
        if (!extensionProviders.TryGetValue(provider, out var extension)) return configured;
        var merged = configured is null ? new JsonObject() : (JsonObject)configured.DeepClone();
        if (extension["apiKey"] is { } apiKey) merged["apiKey"] = apiKey.DeepClone();
        if (extension["authHeader"] is { } authHeader) merged["authHeader"] = authHeader.DeepClone();
        if (extension["headers"] is JsonObject headers)
        {
            var all = merged["headers"] as JsonObject ?? new JsonObject();
            foreach (var (name, value) in headers) all[name] = value?.DeepClone();
            merged["headers"] = all;
        }
        if (extension["baseUrl"] is { } baseUrl && merged["baseUrl"] is null) merged["baseUrl"] = baseUrl.DeepClone();
        return merged;
    }

    /// <summary>
    /// Source getApiKeyAndHeaders for a key-auth request: the stored api_key (its environment fallback when the key does not resolve),
    /// then models.json <c>apiKey</c> (env templates and <c>!command</c>), then the built-in environment variable; then provider and model headers and
    /// <c>authHeader</c>. Returns null with <paramref name="error"/> set when no usable key exists.
    /// </summary>
    internal ModelRequestAuth? ResolveRequestAuth(RegistryModel model, out string? error)
    {
        error = null;
        var providerId = model.Provider;
        var providerConfig = ProviderConfig(providerId);
        var stored = Stored(providerId);
        BuiltinProviders.TryGet(providerId, out var builtin);
        string? key = null; string? source = null; IReadOnlyDictionary<string, string>? env = null;
        try
        {
            if (stored?.Type == "oauth")
            {
                // composeOAuthAuth: an extension provider's OAuth credential becomes its API key (refreshed when it expires).
                if (extensionOAuth?.Has(providerId) != true || extensionOAuth.ApiKey(providerId) is not { Length: > 0 } oauthKey)
                { error = $"Stored OAuth credentials for \"{providerId}\" are not available on this route; use an API key."; return null; }
                key = oauthKey; source = "OAuth";
            }
            else if (stored is { Type: "api_key", Key: { Length: > 0 } storedKey }) { key = storedKey; env = stored.Environment; source = "stored credential"; }
            // composeApiKeyAuth: a stored credential (even one whose key does not resolve) skips models.json apiKey.
            else if (stored is not { Type: "api_key" } && ModelProviderComposer.ApiKey(providerConfig) is { } rawKey)
            { key = Values.ResolveOrThrow(rawKey, $"API key for provider \"{providerId}\"", stored?.Environment); source = "configured API key"; }
            if (key is null && builtin is not null)
            {
                // envApiKeyAuth: the first set variable. Ambient (AWS, Vertex ADC), Cloudflare and Anthropic auth have their own routes.
                foreach (var name in builtin.ApiKeyVariables)
                    if (ProviderEnvironmentKeys.Value(options.Environment, name) is { } value) { key = value; source = name; break; }
            }
            if (key is null)
            {
                // resolveCompatibilityRequestConfig: without a resolved key only static and configured headers apply.
                if (ModelProviderComposer.AuthHeader(providerConfig)) { error = $"No API key found for \"{providerId}\""; return null; }
                var configured = Values.ResolveHeadersOrThrow(JsonTreeMaps.Spread(ModelProviderComposer.ConfiguredHeaders(providerConfig),
                    ModelProviderComposer.RawModelHeaders(model, providerConfig)), $"model \"{model.Reference}\"");
                var statics = ModelProviderComposer.StringMap(model.Headers);
                return new(null, statics is null && configured is null ? null : JsonTreeMaps.Spread(statics, configured), null, null);
            }
            var headers = Values.ResolveHeadersOrThrow(ModelProviderComposer.ConfiguredHeaders(providerConfig), $"provider \"{providerId}\"", env);
            var auth = ModelProviderComposer.WithConfiguredAuth(key, null, headers, ModelProviderComposer.AuthHeader(providerConfig));
            var modelHeaders = Values.ResolveHeadersOrThrow(ModelProviderComposer.RawModelHeaders(model, providerConfig), $"model \"{model.Reference}\"", env);
            return new(key, ModelProviderComposer.MergeHeaders(auth, modelHeaders), env, source);
        }
        catch (InvalidOperationException failure)
        {
            error = failure.Message == "authHeader requires a resolved API key" ? $"No API key found for \"{providerId}\"" : failure.Message;
            return null;
        }
    }

    /// <summary>Source registerVirtualModel: re-registering replaces; a physical model with the same provider and id conflicts.</summary>
    internal void RegisterVirtualModel(VirtualModelDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Provider) || string.IsNullOrWhiteSpace(definition.Id))
            throw new InvalidOperationException("Virtual model provider and id must not be empty.");
        if (Find(definition.Provider, definition.Id) is { } existing && !VirtualModels.IsVirtual(existing))
            throw new InvalidOperationException($"Virtual model {definition.Provider}/{definition.Id} conflicts with a physical model.");
        if (!virtualModels.TryGetValue(definition.Provider, out var models)) virtualModels[definition.Provider] = models = new(StringComparer.Ordinal);
        models[definition.Id] = new(VirtualModels.Create(definition), definition);
        Rebuild();
    }

    internal void UnregisterVirtualModel(string provider, string id)
    {
        if (!virtualModels.TryGetValue(provider, out var models) || !models.Remove(id)) return;
        if (models.Count == 0) virtualModels.Remove(provider);
        Rebuild();
    }

    /// <summary>Source resolveModel: ask the router for one request's physical model; the target must be a physical catalog model with
    /// credentials, and the thinking level is clamped to it.</summary>
    internal async ValueTask<ModelRoute> ResolveVirtualAsync(RegistryModel model, IReadOnlyList<BranchEntry> branch, IReadOnlyList<JsonObject> messages,
        ModelRouteReason reason, string thinkingLevel, AssistantEntry? failed, JsonNode? state, CancellationToken cancellationToken)
    {
        var name = $"Virtual model {model.Provider}/{model.Id}";
        if (!virtualModels.TryGetValue(model.Provider, out var models) || !models.TryGetValue(model.Id, out var registered))
            throw new InvalidOperationException($"{name} is not registered.");
        var latest = VirtualModels.FindLatestResponse(branch);
        var previous = latest is null ? null : FindPhysical(latest.Provider, latest.Model);
        var failedModel = failed is null ? null : FindPhysical(failed.Provider, failed.Model);
        var route = await registered.Definition.Route(new(model, thinkingLevel, reason,
            previous is null ? null : (previous, latest!.ThinkingLevel),
            failedModel is null ? null : (failedModel, failed!.ThinkingLevel, new JsonObject { ["stopReason"] = failed.StopReason }),
            state, messages, cancellationToken)).ConfigureAwait(false);
        var target = FindPhysical(route.Model.Provider, route.Model.Id);
        var routed = $"{name} routed to {route.Model.Provider}/{route.Model.Id}";
        if (target is null) throw new InvalidOperationException($"{routed}, which is not a physical model.");
        if (!HasConfiguredAuth(target.Provider)) throw new InvalidOperationException($"{routed}, which has no credentials.");
        return new(target, VirtualModels.ClampThinkingLevel(target, route.ThinkingLevel), route.State);
    }
}
