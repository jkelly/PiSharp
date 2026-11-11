// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/model-registry.ts (classify,
// generateImages, getModelsOfType, findOfType, getAvailableOfType), core/model-runtime.ts (prepareRequest),
// core/auth-storage.ts (stored credentials), packages/ai/src/auth/resolve.ts (resolveProviderAuth, resolveStoredOAuth) and
// packages/ai/src/models.ts (checkProviderAuth) and packages/coding-agent/src/extensions/llama/provider.ts (the llama.cpp classifiers).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.AI.ModelOperations;
using PiSharp.AI.Providers;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>
/// The extension view of a <see cref="ModelOperationsRegistry"/> (upstream <c>ctx.modelRegistry</c>): models as their catalog
/// JSON objects, and classification and image generation for the model object the extension passes, used as given (its
/// api, provider, id, baseUrl, headers and cost). As in Pi, the auth of <c>model.provider</c> is resolved and applied to
/// it, so a trusted extension may send a provider's credentials to its own base URL (owner decision 0004).
/// </summary>
public sealed class NativeExtensionModelOperations(ModelOperationsRegistry registry)
{
    public ModelOperationsRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));

    public ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider) =>
        [.. Registry.GetModelsOfType(type, provider).Select(model => model.ToJson())];

    public JsonData? GetModelOfType(ModelType type, string provider, string id) => Registry.GetModelOfType(type, provider, id)?.ToJson();

    public async Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider, CancellationToken cancellationToken) =>
        [.. (await Registry.GetAvailableOfTypeAsync(type, provider, cancellationToken).ConfigureAwait(false)).Select(model => model.ToJson())];

    public async Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, ExtensionModelRequestOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        var (parsed, error) = Parse(model, ModelType.Classifier);
        if (parsed is not ClassifierModel classifier)
            return new ClassifierResult(Text(model, "api"), Text(model, "provider"), Text(model, "id"), [], ModelOperationStopReason.Error,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { ErrorMessage = error };
        return await Registry.ClassifyAsync(classifier, context, new ClassifierOptions
        {
            ApiKey = options?.ApiKey, Headers = options?.Headers ?? [], TimeoutMs = options?.TimeoutMs, MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs, Temperature = options?.Temperature
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext context, ExtensionModelRequestOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        var (parsed, error) = Parse(model, ModelType.Image);
        if (parsed is not ImageModel image)
            return new AssistantImages(Text(model, "api"), Text(model, "provider"), Text(model, "id"), [], ModelOperationStopReason.Error,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { ErrorMessage = error };
        return await Registry.GenerateImagesAsync(image, context, new ImagesOptions
        {
            ApiKey = options?.ApiKey, Headers = options?.Headers ?? [], TimeoutMs = options?.TimeoutMs, MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The supplied model object as a typed model, with utils/model-operations.ts <c>assertClassifierModel</c>/
    /// <c>assertImageModel</c> messages (a model without <c>type</c> is a chat model).</summary>
    private static (OperationModel? Model, string Error) Parse(JsonData model, ModelType type)
    {
        OperationModel parsed;
        try { parsed = OperationModel.FromJson(model); }
        catch (FormatException error) { return (null, error.Message); }
        if (parsed.Type == type) return (parsed, "");
        return (null, $"Model {parsed.Provider}/{parsed.Id} is not {(type == ModelType.Image ? "an image" : "a classifier")} model");
    }

    private static string Text(JsonData model, string name) =>
        model.Value.ValueKind == JsonValueKind.Object && model.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    /// <summary>
    /// The CLI's registry: every embedded, hash-verified provider catalog shard as the built-in providers, with auth resolved
    /// per request as upstream resolveProviderAuth does. A stored <c>auth.json</c> credential owns the provider: an api_key
    /// credential supplies its key and env; an OAuth credential is refreshed through <see cref="StoredOAuthLifecycle"/> when
    /// it expires within five minutes (the rotation is persisted) and its access token travels in the apiKey channel, for the
    /// providers with an OAuth refresh (<see cref="DefaultOAuthRefreshes"/>; <paramref name="oauthRefreshes"/> replaces the set). An
    /// OAuth credential of a provider without one, or of another type, leaves the provider unconfigured, with no environment
    /// fallback. Without a stored credential the configured models.json key applies, then the provider's environment variable.
    /// <paramref name="modelsPath"/> loads one models.json snapshot; the run-bound overload shares the run's refreshed snapshot.
    /// </summary>
    public static ModelOperationsRegistry CreateDefaultRegistry(Func<string, string?> readEnvironment, string? authPath,
        Func<HttpMessageInvoker>? createAuthHttp = null, TimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, IAdmittedOAuthRefresh>? oauthRefreshes = null, Func<bool>? llamaProvider = null, string? modelsPath = null)
    {
        var config = ModelsJsonConfig.Load(modelsPath);
        return CreateDefaultRegistry(readEnvironment, authPath, createAuthHttp, timeProvider, oauthRefreshes, llamaProvider,
            config.GetProvider);
    }

    /// <summary>The run shares its loaded configuration with chat. A refresh changes later lookups and auth resolution;
    /// model objects already handed to extensions remain immutable request inputs, as in model-runtime.ts.</summary>
    internal static ModelOperationsRegistry CreateDefaultRegistry(Func<string, string?> readEnvironment, string? authPath,
        Func<HttpMessageInvoker>? createAuthHttp, TimeProvider? timeProvider, Func<bool>? llamaProvider, ModelRegistry models) =>
        CreateDefaultRegistry(readEnvironment, authPath, createAuthHttp, timeProvider, null, llamaProvider, models.GetModelsJsonProviderConfig);

    private static ModelOperationsRegistry CreateDefaultRegistry(Func<string, string?> readEnvironment, string? authPath,
        Func<HttpMessageInvoker>? createAuthHttp, TimeProvider? timeProvider,
        IReadOnlyDictionary<string, IAdmittedOAuthRefresh>? oauthRefreshes, Func<bool>? llamaProvider,
        Func<string, JsonObject?> providerConfig)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        var catalogs = new List<FrozenModelCatalog>();
        foreach (var provider in CurrentModelCatalog.Providers)
        {
            try
            {
                if (ReviewedModelCatalog.Apply(provider, BuiltinModelCatalog.Has(provider) ? BuiltinModelCatalog.Get(provider) : null) is { } catalog)
                    catalogs.Add(catalog);
            }
            catch (Exception error) when (error is BuiltinCatalogException or CatalogReadException) { }
        }
        var store = authPath is null ? null : new AuthJsonCredentialStore(authPath, timeProvider);
        createAuthHttp ??= () => new HttpClient();
        var refreshes = new ProviderOAuthRefreshes(oauthRefreshes ?? DefaultOAuthRefreshes(readEnvironment, createAuthHttp, timeProvider));
        var lifecycle = store is null ? null : new StoredOAuthLifecycle(store, refreshes, timeProvider);
        var values = new ConfigValueResolver(readEnvironment);
        var standard = ModelOperationsAuth.Standard(readEnvironment, store is null ? null : async (provider, token) =>
        {
            var entry = await store.ReadEntryAsync(provider, token).ConfigureAwait(false);
            if (entry is not { Type: "api_key" }) return null;
            return new StoredApiKeyCredential(entry.Key is null ? null : ConfigValueTemplate.Resolve(entry.Key, entry.Environment, readEnvironment),
                entry.Environment is null ? null : new ProviderEnvironmentSnapshot(scoped: entry.Environment.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))));
        });
        async ValueTask<ProviderAuthResult?> ResolveAuth(ProviderAuthRequest request, CancellationToken token)
        {
            // auth/resolve.ts: an explicit key bypasses the store; otherwise a stored credential owns the provider.
            if (store is not null && request.ApiKey is null && await store.ReadEntryAsync(request.Provider, token).ConfigureAwait(false) is { } entry)
            {
                if (entry.Type == "oauth")
                {
                    if (!refreshes.Supports(request.Provider)) return null;
                    // checkProviderAuth: a stored OAuth credential is configured without being refreshed.
                    if (request.Check) return new ProviderAuthResult(null)
                    {
                        IsOAuth = true,
                        AvailableModelIds = request.Provider == "github-copilot"
                            ? GitHubCopilotOAuth.AvailableModels(await store.ReadAsync(request.Provider, token).ConfigureAwait(false))?.ToImmutableHashSet(StringComparer.Ordinal)
                            : null
                    };
                    OAuthCredentialSnapshot? credential;
                    try { credential = await lifecycle!.ResolveAsync(request.Provider, cancellationToken: token).ConfigureAwait(false); }
                    catch (OAuthLifecycleException error)
                    {
                        throw new ModelOperationsException(ModelOperationsErrorCode.Auth, error.Failure switch
                        {
                            OAuthLifecycleFailure.Refresh => WithCause($"OAuth refresh failed for {request.Provider}", error.OriginalException),
                            OAuthLifecycleFailure.Read => $"Credential store read failed for {request.Provider}",
                            _ => $"Credential store modify failed for {request.Provider}"
                        });
                    }
                    // Logged out meanwhile: no silent environment fallback.
                    if (credential is null) return null;
                    if (request.Provider == "github-copilot")
                    {
                        var enterprise = credential.ProviderData.TryGetValue("enterpriseUrl", out var domain) ? GitHubCopilotOAuth.NormalizeDomain(domain) : null;
                        return new ProviderAuthResult(credential.Access)
                        {
                            IsOAuth = true, BaseUrl = ProviderHeaderPolicies.CopilotBaseUrl(credential.Access, enterprise),
                            AvailableModelIds = GitHubCopilotOAuth.AvailableModels(credential)?.ToImmutableHashSet(StringComparer.Ordinal)
                        };
                    }
                    return new ProviderAuthResult(credential.Access) { IsOAuth = true };
                }
                if (entry.Type != "api_key") return null;
            }
            // composeApiKeyAuth: a configured key is used only without an explicit or stored credential.
            if (request.ApiKey is null && (store is null || await store.ReadEntryAsync(request.Provider, token).ConfigureAwait(false) is null) &&
                ModelProviderComposer.ApiKey(providerConfig(request.Provider)) is { } configuredKey)
            {
                if (request.Check && !values.IsConfigured(configuredKey)) return null;
                var key = request.Check ? "configured" : values.ResolveOrThrow(configuredKey, $"API key for provider \"{request.Provider}\"");
                request = request with { ApiKey = key };
            }
            return await standard(request, token).ConfigureAwait(false);
        }
        var registry = ModelOperationsRegistry.CreateBuiltin(catalogs, async (request, token) =>
        {
            var resolved = await ResolveAuth(request, token).ConfigureAwait(false);
            if (resolved is null || request.Check) return resolved;
            var config = providerConfig(request.Provider);
            var headerEnvironment = resolved.Env;
            if (request.ApiKey is null && store is not null &&
                await store.ReadEntryAsync(request.Provider, token).ConfigureAwait(false) is { Environment: { } storedEnvironment })
                headerEnvironment = storedEnvironment.ToImmutableDictionary(StringComparer.Ordinal).SetItems(headerEnvironment);
            if (request.Env is { } requestEnvironment) headerEnvironment = headerEnvironment.SetItems(requestEnvironment);
            var headers = values.ResolveHeadersOrThrow(ModelProviderComposer.ConfiguredHeaders(config), $"provider \"{request.Provider}\"", headerEnvironment);
            var merged = ModelProviderComposer.WithConfiguredAuth(resolved.ApiKey,
                resolved.Headers.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase),
                headers, ModelProviderComposer.AuthHeader(config));
            return resolved with { Headers = merged is null ? [] : [.. merged.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))] };
        });
        foreach (var provider in registry.GetProviders()) registry.SetProvider(Configure(provider));
        // The built-in llama.cpp extension's provider (extensions/llama/provider.ts getAllModels): the classifier models of the catalog
        // the models store next to auth.json holds, as the latest refresh or /llama left them. Not loaded, it registers nothing.
        if (llamaProvider?.Invoke() == false) return registry;
        var llama = PiSharp.Cli.Llama.LlamaCatalog.For(authPath is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(authPath))!, "models-store.json"));
        registry.SetProvider(Configure(BuiltinModelOperationProviders.LlamaCpp([], () =>
        {
            var models = ImmutableArray.CreateBuilder<OperationModel>();
            foreach (var model in llama.LoadedClassifierJson())
                try { models.Add(OperationModel.FromJson(JsonData.Parse(model.ToJsonString()))); } catch (FormatException) { }
            return models.ToImmutable();
        })));
        return registry;

        ModelOperationsProvider Configure(ModelOperationsProvider provider) => provider with
        {
            ModelSource = () =>
            {
                var originals = provider.CurrentModels;
                var config = providerConfig(provider.Id);
                if (config is null) return originals;
                try
                {
                    // Reuse the chat registry's exact layer: provider baseUrl applies to every type, classifier compat
                    // merges shallowly, and models/modelOverrides only create or alter chat models.
                    var source = originals.Select(model => RegistryModel.FromJson((JsonObject)JsonNode.Parse(model.ToJson().ToString())!)).ToArray();
                    return [.. ModelProviderComposer.Compose(provider.Id, source, config)
                        .Select(model => OperationModel.FromJson(JsonData.Parse(model.ToJsonString())))];
                }
                catch (InvalidOperationException) { return originals; } // Same invalid-provider fallback as ModelRegistry.Rebuild.
            }
        };
    }

    /// <summary>models-error.ts <c>withCauseDetail</c>: the underlying reason joins the message unless it is already in it.</summary>
    private static string WithCause(string message, Exception? cause) =>
        cause?.Message.Trim() is { Length: > 0 } detail && !message.Contains(detail, StringComparison.Ordinal) ? $"{message}: {detail}" : message;

    /// <summary>A refresh over a fresh HTTP client per refresh, disposed afterwards.</summary>
    /// <summary>
    /// The stored-OAuth refresh of every upstream provider with an OAuth method (auth/oauth/load.ts): anthropic, openai
    /// (ChatGPT) and openrouter as above, and openai-codex, github-copilot, kimi-coding, meta, radius and xai through their
    /// <see cref="ProviderAuthCatalog"/> flows (the same refresh /login and the live route use).
    /// </summary>
    internal static Dictionary<string, IAdmittedOAuthRefresh> DefaultOAuthRefreshes(Func<string, string?> readEnvironment,
        Func<HttpMessageInvoker> createAuthHttp, TimeProvider? timeProvider)
    {
        var refreshes = new Dictionary<string, IAdmittedOAuthRefresh>(StringComparer.Ordinal)
        {
            ["anthropic"] = new FreshClientRefresh(http => new AnthropicOAuth(http, timeProvider), createAuthHttp),
            ["openai"] = new FreshClientRefresh(http => new OpenAIChatGPTOAuthRefresh(http, timeProvider), createAuthHttp),
            ["openrouter"] = OpenRouterOAuthRefresh.Instance
        };
        foreach (var entry in ProviderAuthCatalog.Entries())
            if (entry.OAuth is { } oauth && !refreshes.ContainsKey(entry.Id))
                refreshes[entry.Id] = new FreshClientRefresh(http => oauth.Create(new OAuthFlowContext(http, readEnvironment, timeProvider, null, 0, null)), createAuthHttp);
        return refreshes;
    }

    private sealed class FreshClientRefresh(Func<HttpMessageInvoker, IAdmittedOAuthRefresh> create, Func<HttpMessageInvoker> createHttp) : IAdmittedOAuthRefresh
    {
        public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
        {
            using var http = createHttp();
            return await create(http).RefreshAsync(provider, current, cancellationToken).ConfigureAwait(false);
        }
    }
}
