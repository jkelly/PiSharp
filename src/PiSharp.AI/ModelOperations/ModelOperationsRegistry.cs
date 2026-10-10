// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/models.ts (Models.classify, Models.generateImages,
// applyAuth, typed model reads, getAvailableOfType, createProvider classifier/image dispatch), utils/model-operations.ts,
// images.ts, images-api-registry.ts, providers/images/register-builtins.ts, providers/openai.ts, providers/openrouter.ts,
// providers/typesafe.ts, providers/cloudflare-workers-ai.ts, providers/opencode.ts, providers/vercel-ai-gateway.ts,
// auth/helpers.ts (envApiKeyAuth), auth/resolve.ts (apiKey overrides), providers/cloudflare-auth.ts, and
// packages/coding-agent/src/core/model-registry.ts (classify, generateImages, findOfType, getModelsOfType).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.AI.Authentication;
using PiSharp.AI.Catalogs;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>utils/models-error.ts <c>ModelsError</c> codes used by the registry.</summary>
public enum ModelOperationsErrorCode { Provider, Auth }

/// <summary>utils/models-error.ts <c>ModelsError</c>. The registry reports it inside error results, never by throwing.</summary>
public sealed class ModelOperationsException(ModelOperationsErrorCode code, string message) : Exception(message)
{
    public ModelOperationsErrorCode Code { get; } = code;
}

/// <summary>An auth lookup for one provider. <see cref="ApiKey"/> is an explicit request key that takes the place of a
/// stored api_key credential (auth/resolve.ts overrides); <see cref="Env"/> overlays the provider environment.</summary>
public sealed record ProviderAuthRequest(string Provider, string? ApiKey = null, ImmutableDictionary<string, string>? Env = null)
{
    /// <summary>An availability check (models.ts <c>checkProviderAuth</c>): a stored OAuth credential counts as configured
    /// without being refreshed, and only <see cref="ProviderAuthResult.IsOAuth"/> and non-null matter.</summary>
    public bool Check { get; init; }
}

/// <summary>auth/types.ts <c>AuthResult</c>: request auth for one provider. Secrets never appear in ToString.</summary>
public sealed record ProviderAuthResult(string? ApiKey)
{
    public ImmutableArray<KeyValuePair<string, string?>> Headers { get; init; } = [];
    public ImmutableDictionary<string, string> Env { get; init; } = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    /// <summary>A base URL that replaces the model's (e.g. a resolved account endpoint).</summary>
    public string? BaseUrl { get; init; }
    /// <summary>True when the credential is an OAuth token: OpenAI then lists no Decisions models.</summary>
    public bool IsOAuth { get; init; }
    public override string ToString() => "ProviderAuthResult [redacted]";
}

/// <summary>Resolves request auth for a provider; null means the provider is not configured.</summary>
public delegate ValueTask<ProviderAuthResult?> ProviderAuthResolver(ProviderAuthRequest request, CancellationToken cancellationToken);

/// <summary>models.ts <c>Provider</c> as far as classification and image generation need it: its models of every type,
/// its classifier and image API implementations keyed by <c>model.api</c>, an optional per-request model rewrite
/// (Cloudflare endpoint placeholders) and the credential-dependent model filter.</summary>
public sealed record ModelOperationsProvider(string Id, string Name)
{
    public ImmutableArray<OperationModel> Models { get; init; } = [];
    /// <summary>models.ts <c>Provider.getAllModels()</c> for a provider whose catalog changes at runtime (a discovered or refreshed
    /// catalog): read on every lookup instead of <see cref="Models"/>.</summary>
    public Func<ImmutableArray<OperationModel>>? ModelSource { get; init; }
    /// <summary>The provider's models now: <see cref="ModelSource"/>, else <see cref="Models"/>.</summary>
    public ImmutableArray<OperationModel> CurrentModels => ModelSource?.Invoke() ?? Models;
    public ImmutableDictionary<string, IClassifierApi> Classifiers { get; init; } = ImmutableDictionary<string, IClassifierApi>.Empty;
    public ImmutableDictionary<string, IImagesApi> Images { get; init; } = ImmutableDictionary<string, IImagesApi>.Empty;
    /// <summary>Rewrites the model with the request's resolved env before dispatch.</summary>
    public Func<OperationModel, IReadOnlyDictionary<string, string>, OperationModel>? ResolveModel { get; init; }
    /// <summary>models.ts <c>filterAllModels</c>: the models usable with the resolved credential.</summary>
    public Func<ImmutableArray<OperationModel>, ProviderAuthResult, ImmutableArray<OperationModel>>? FilterAllModels { get; init; }
}

/// <summary>images-api-registry.ts and its classifier counterpart: API implementations by API id, for direct calls
/// without provider auth (<c>generateImages()</c> in images.ts). The built-in APIs are registered.</summary>
public static class ModelOperationApis
{
    private static readonly ConcurrentDictionary<string, IImagesApi> ImagesApis = new(StringComparer.Ordinal)
    { [OpenRouterImages.ApiId] = OpenRouterImages.Instance };
    private static readonly ConcurrentDictionary<string, IClassifierApi> ClassifierApis = new(StringComparer.Ordinal)
    {
        [OpenAIDecisionsClassifier.ApiId] = OpenAIDecisionsClassifier.Instance,
        [TypeSafeSystemOneClassifier.ApiId] = TypeSafeSystemOneClassifier.Instance,
        [CloudflareWorkersAISystemOneClassifier.ApiId] = CloudflareWorkersAISystemOneClassifier.Instance,
        [LlamaCppClassifier.ApiId] = LlamaCppClassifier.Instance
    };

    public static void RegisterImagesApi(IImagesApi api) { ArgumentNullException.ThrowIfNull(api); ImagesApis[api.Api] = api; }
    public static void RegisterClassifierApi(IClassifierApi api) { ArgumentNullException.ThrowIfNull(api); ClassifierApis[api.Api] = api; }
    public static IImagesApi? GetImagesApi(string api) => ImagesApis.GetValueOrDefault(api);
    public static IClassifierApi? GetClassifierApi(string api) => ClassifierApis.GetValueOrDefault(api);

    /// <summary>images.ts <c>generateImages</c>: dispatch on <c>model.api</c>; auth must be passed in the options. Throws
    /// when no API is registered for the model's API.</summary>
    public static Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var api = GetImagesApi(model.Api) ?? throw new InvalidOperationException($"No API provider registered for api: {model.Api}");
        if (api.Api != model.Api) throw new InvalidOperationException($"Mismatched api: {model.Api} expected {api.Api}");
        return api.GenerateImagesAsync(model, context, options, cancellationToken);
    }

    /// <summary>The classifier counterpart of <see cref="GenerateImagesAsync"/>.</summary>
    public static Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var api = GetClassifierApi(model.Api) ?? throw new InvalidOperationException($"No API provider registered for api: {model.Api}");
        return api.ClassifyAsync(model, context, options, cancellationToken);
    }
}

/// <summary>The built-in provider definitions of Pi v1.1.0 that serve classifier or image models, keyed by provider id.</summary>
public static class BuiltinModelOperationProviders
{
    public static ImmutableArray<string> ProviderIds { get; } =
        ["openai", "openrouter", "typesafe", "cloudflare-workers-ai", "opencode", "vercel-ai-gateway"];

    /// <summary>The provider definition with <paramref name="models"/>. A provider without classifier or image APIs lists
    /// its models only.</summary>
    public static ModelOperationsProvider Create(string providerId, IEnumerable<OperationModel> models)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        var list = models.ToImmutableArray();
        static ImmutableDictionary<string, IClassifierApi> One(IClassifierApi api) =>
            ImmutableDictionary<string, IClassifierApi>.Empty.WithComparers(StringComparer.Ordinal).Add(api.Api, api);
        return providerId switch
        {
            "openai" => new ModelOperationsProvider("openai", "OpenAI")
            {
                Models = list, Classifiers = One(OpenAIDecisionsClassifier.Instance),
                // Sign in with ChatGPT tokens only reach the Responses API; the Decisions API rejects them.
                FilterAllModels = (all, auth) => auth.IsOAuth ? [.. all.Where(model => model.Type != ModelType.Classifier)] : all
            },
            "openrouter" => new ModelOperationsProvider("openrouter", "OpenRouter")
            {
                Models = list, Classifiers = One(TypeSafeSystemOneClassifier.Instance),
                Images = ImmutableDictionary<string, IImagesApi>.Empty.WithComparers(StringComparer.Ordinal).Add(OpenRouterImages.ApiId, OpenRouterImages.Instance)
            },
            "typesafe" => new ModelOperationsProvider("typesafe", "TypeSafe") { Models = list, Classifiers = One(TypeSafeSystemOneClassifier.Instance) },
            "cloudflare-workers-ai" => new ModelOperationsProvider("cloudflare-workers-ai", "Cloudflare Workers AI")
            {
                Models = list, Classifiers = One(CloudflareWorkersAISystemOneClassifier.Instance),
                ResolveModel = (model, env) => CloudflareWorkersAISystemOneClassifier.ResolveModel(model, env)
            },
            "opencode" => new ModelOperationsProvider("opencode", "OpenCode Zen") { Models = list, Classifiers = One(TypeSafeSystemOneClassifier.Instance) },
            "vercel-ai-gateway" => new ModelOperationsProvider("vercel-ai-gateway", "Vercel AI Gateway")
            { Models = list, Classifiers = One(TypeSafeSystemOneClassifier.Instance) },
            _ => new ModelOperationsProvider(providerId, providerId) { Models = list }
        };
    }

    /// <summary>A llama.cpp server's classifier models (coding-agent extensions/llama/provider.ts): decision models
    /// answer through System One at the server's <c>/v1</c> URL, chat models through <c>llama-cpp-classify</c>. Model
    /// discovery is the caller's.</summary>
    public static ModelOperationsProvider LlamaCpp(IEnumerable<OperationModel> models, Func<ImmutableArray<OperationModel>>? source = null) => new("llama.cpp", "llama.cpp")
    {
        Models = models.ToImmutableArray(), ModelSource = source,
        Classifiers = ImmutableDictionary<string, IClassifierApi>.Empty.WithComparers(StringComparer.Ordinal)
            .Add(LlamaCppClassifier.ApiId, LlamaCppClassifier.Instance).Add(TypeSafeSystemOneClassifier.ApiId, TypeSafeSystemOneClassifier.Instance)
    };
}

/// <summary>Standard provider auth for classification and image generation.</summary>
public static class ModelOperationsAuth
{
    private const string CloudflareApiKey = "CLOUDFLARE_API_KEY", CloudflareAccountId = "CLOUDFLARE_ACCOUNT_ID";

    /// <summary>
    /// auth/helpers.ts <c>envApiKeyAuth</c> and providers/cloudflare-auth.ts: an explicit request key, else a stored
    /// api_key credential (its key and env), else the provider's environment variable from env-api-keys.ts. Cloudflare
    /// Workers AI also needs <c>CLOUDFLARE_ACCOUNT_ID</c> (credential env first, then the environment) and passes it on as
    /// provider env. <paramref name="readEnvironment"/> is the process environment; request env values overlay it.
    /// Keyless providers (llama.cpp) resolve to an empty key.
    /// </summary>
    public static ProviderAuthResolver Standard(Func<string, string?> readEnvironment,
        Func<string, CancellationToken, ValueTask<StoredApiKeyCredential?>>? storedCredential = null)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        return async (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var overlay = request.Env ?? ImmutableDictionary<string, string>.Empty;
            string? Env(string name) => overlay.TryGetValue(name, out var scoped) && scoped.Length != 0 ? scoped
                : readEnvironment(name) is { Length: > 0 } value ? value : null;
            StoredApiKeyCredential? credential = null;
            if (request.ApiKey is not null) credential = new(request.ApiKey, new ProviderEnvironmentSnapshot(overlay.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))));
            else if (storedCredential is not null)
            {
                credential = await storedCredential(request.Provider, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            var empty = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
            if (request.Provider == "llama.cpp")
            {
                // extensions/llama/provider.ts: the server URL (credential env, then LLAMA_BASE_URL) configures the provider.
                var configured = credential?.Environment?.GetValue("LLAMA_BASE_URL") ?? Env("LLAMA_BASE_URL");
                if (string.IsNullOrWhiteSpace(configured) || NormalizeLlamaServerUrl(configured) is not { } serverUrl) return null;
                return new ProviderAuthResult(credential?.Key ?? Env("LLAMA_API_KEY") ?? "local")
                { BaseUrl = serverUrl + "/v1", Env = empty.Add("LLAMA_BASE_URL", serverUrl) };
            }
            if (request.Provider == "cloudflare-workers-ai")
            {
                // Per-field merge: the credential value wins, the ambient environment fills the rest.
                var key = credential is not null ? credential.Key : Env(CloudflareApiKey);
                var account = credential?.Environment?.GetValue(CloudflareAccountId) ?? Env(CloudflareAccountId);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(account)) return null;
                return new ProviderAuthResult(key) { Env = empty.Add(CloudflareAccountId, account) };
            }
            if (credential is { Key: { Length: > 0 } stored }) return new ProviderAuthResult(stored) { Env = request.Env ?? empty };
            var resolved = InjectedAuthenticationResolver.GetEnvApiKey(request.Provider,
                new ProviderEnvironmentSnapshot(process: ApiKeyVariables.Select(name => KeyValuePair.Create(name, Env(name)))));
            return resolved is { Diagnostic: AuthenticationDiagnostic.Resolved, Authentication: { } found } ? new ProviderAuthResult(found.Secret) : null;
        };
    }

    /// <summary>extensions/llama/client.ts <c>normalizeLlamaServerUrl</c>: http(s) only, no query or fragment, and no
    /// trailing slash or <c>/v1</c>.</summary>
    internal static string? NormalizeLlamaServerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return null;
        var path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.Ordinal)) path = path[..^3];
        return (url.GetLeftPart(UriPartial.Authority) + (path.Length == 0 ? "/" : path)).TrimEnd('/');
    }

    // The env-api-keys.ts variables InjectedAuthenticationResolver maps; the snapshot reads only these.
    private static readonly string[] ApiKeyVariables =
    [
        "COPILOT_GITHUB_TOKEN", "ANT_LING_API_KEY", "QWEN_TOKEN_PLAN_API_KEY", "QWEN_TOKEN_PLAN_CN_API_KEY", "OPENAI_API_KEY",
        "AZURE_OPENAI_API_KEY", "NVIDIA_API_KEY", "DEEPSEEK_API_KEY", "GEMINI_API_KEY", "GOOGLE_CLOUD_API_KEY", "GROQ_API_KEY",
        "CEREBRAS_API_KEY", "XAI_API_KEY", "TYPESAFE_API_KEY", "RADIUS_API_KEY", "OPENROUTER_API_KEY", "AI_GATEWAY_API_KEY",
        "ZAI_API_KEY", "ZAI_CODING_CN_API_KEY", "MISTRAL_API_KEY", "MINIMAX_API_KEY", "MINIMAX_CN_API_KEY", "MOONSHOT_API_KEY",
        "HF_TOKEN", "FIREWORKS_API_KEY", "TOGETHER_API_KEY", "BASETEN_API_KEY", "OPENCODE_API_KEY", "KIMI_API_KEY", "META_API_KEY",
        "CLOUDFLARE_API_KEY", "XIAOMI_API_KEY", "XIAOMI_TOKEN_PLAN_CN_API_KEY", "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
        "XIAOMI_TOKEN_PLAN_SGP_API_KEY", InjectedAuthenticationResolver.AnthropicAuthToken, InjectedAuthenticationResolver.AnthropicOAuthToken,
        InjectedAuthenticationResolver.AnthropicApiKey
    ];
}

/// <summary>
/// The model registry facade for classification and image generation (coding-agent <c>ModelRegistry.classify</c> and
/// <c>generateImages</c> over pi-ai <c>Models</c>). It holds providers with their models of every type, resolves
/// request auth per call, and never throws for request failures: they arrive as error or aborted results.
/// Thread-safe; providers can be replaced at any time.
/// </summary>
public sealed class ModelOperationsRegistry
{
    private readonly ProviderAuthResolver auth;
    private ImmutableDictionary<string, ModelOperationsProvider> providers = ImmutableDictionary<string, ModelOperationsProvider>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableArray<string> order = [];
    private readonly object gate = new();

    public ModelOperationsRegistry(ProviderAuthResolver auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        this.auth = auth;
    }

    /// <summary>A registry of the built-in providers over released catalog shards (one provider per shard).</summary>
    public static ModelOperationsRegistry CreateBuiltin(IEnumerable<FrozenModelCatalog> catalogs, ProviderAuthResolver auth)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var registry = new ModelOperationsRegistry(auth);
        foreach (var catalog in catalogs)
            registry.SetProvider(BuiltinModelOperationProviders.Create(catalog.Provider, catalog.Models.Select(OperationModel.FromCatalog)));
        return registry;
    }

    /// <summary>Adds or replaces a provider.</summary>
    public void SetProvider(ModelOperationsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (gate)
        {
            if (!providers.ContainsKey(provider.Id)) order = order.Add(provider.Id);
            providers = providers.SetItem(provider.Id, provider);
        }
    }

    public bool RemoveProvider(string id)
    {
        lock (gate)
        {
            if (!providers.ContainsKey(id)) return false;
            providers = providers.Remove(id); order = order.Remove(id);
            return true;
        }
    }

    public ModelOperationsProvider? GetProvider(string id) => Volatile.Read(ref providers).GetValueOrDefault(id);

    public ImmutableArray<ModelOperationsProvider> GetProviders()
    {
        lock (gate) return [.. order.Select(id => providers[id])];
    }

    /// <summary>Every model of every type, of one provider or of all providers in registration order.</summary>
    public ImmutableArray<OperationModel> GetAllModels(string? provider = null) => provider is not null
        ? GetProvider(provider)?.CurrentModels ?? []
        : [.. GetProviders().SelectMany(entry => entry.CurrentModels)];

    /// <summary>models.ts <c>getModelsOfType</c>.</summary>
    public ImmutableArray<OperationModel> GetModelsOfType(ModelType type, string? provider = null) =>
        [.. GetAllModels(provider).Where(model => model.Type == type)];

    /// <summary>models.ts <c>getModelOfType</c> (coding-agent <c>findOfType</c>).</summary>
    public OperationModel? GetModelOfType(ModelType type, string provider, string id) =>
        GetModelsOfType(type, provider).FirstOrDefault(model => model.Id == id);

    /// <summary>Request auth for a provider: null when the provider is unknown or not configured.</summary>
    public async Task<ProviderAuthResult?> GetAuthAsync(string provider, string? apiKey = null, ImmutableDictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetProvider(provider) is null) return null;
        return await auth(new(provider, apiKey, env), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>models.ts <c>getAvailableOfType</c>: models of one type whose providers have complete auth, after each
    /// provider's credential-dependent filter.</summary>
    public async Task<ImmutableArray<OperationModel>> GetAvailableOfTypeAsync(ModelType type, string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var available = ImmutableArray.CreateBuilder<OperationModel>();
        foreach (var entry in provider is null ? GetProviders() : GetProvider(provider) is { } one ? [one] : [])
        {
            var resolved = await auth(new(entry.Id) { Check = true }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved is null) continue;
            var models = entry.FilterAllModels?.Invoke(entry.CurrentModels, resolved) ?? entry.CurrentModels;
            available.AddRange(models.Where(model => model.Type == type));
        }
        return available.ToImmutable();
    }

    /// <summary>coding-agent model-runtime.ts <c>classify</c> (pi-ai <c>Models.classify</c>): the supplied model is used as
    /// given. It must accept the context; then the auth of <c>model.provider</c> is resolved and applied (explicit option values
    /// win per field, a resolved base URL replaces the model's) and the call dispatches on <c>model.api</c> among that
    /// provider's classifier APIs. Never throws for request failures.</summary>
    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (context.ImageList.Length != 0 && !model.AcceptsImages)
                throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"Model {model.Provider}/{model.Id} does not accept image input");
            var provider = RequireProvider(model);
            var (requestModel, requestOptions) = await ApplyAuthAsync(provider, model, options ?? new ClassifierOptions(), cancellationToken).ConfigureAwait(false);
            if (provider.Classifiers.IsEmpty)
                throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"Provider {model.Provider} does not support classification");
            if (!provider.Classifiers.TryGetValue(model.Api, out var implementation))
                throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"Provider {provider.Id} has no classifier implementation for \"{model.Api}\"");
            return await implementation.ClassifyAsync(requestModel, context, requestOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new ClassifierResult(model.Api, model.Provider, model.Id, [],
                cancellationToken.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error,
                ProviderRequest.Now(options)) { ErrorMessage = error.Message };
        }
    }

    /// <summary>coding-agent model-runtime.ts <c>generateImages</c>: the supplied model is used as given with the auth of
    /// <c>model.provider</c> applied, then the call dispatches on <c>model.api</c>. Never throws for request failures.</summary>
    public async Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        try
        {
            var provider = RequireProvider(model);
            var (requestModel, requestOptions) = await ApplyAuthAsync(provider, model, options ?? new ImagesOptions(), cancellationToken).ConfigureAwait(false);
            if (provider.Images.IsEmpty)
                throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"Provider {model.Provider} does not support image generation");
            if (!provider.Images.TryGetValue(model.Api, out var implementation))
                throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"Provider {provider.Id} has no image generation implementation for \"{model.Api}\"");
            return await implementation.GenerateImagesAsync(requestModel, context, requestOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new AssistantImages(model.Api, model.Provider, model.Id, [],
                cancellationToken.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error,
                ProviderRequest.Now(options)) { ErrorMessage = error.Message };
        }
    }

    private ModelOperationsProvider RequireProvider(OperationModel model) => GetProvider(model.Provider)
        ?? throw new ModelOperationsException(ModelOperationsErrorCode.Provider, $"Unknown provider: {model.Provider}");

    /// <summary>models.ts <c>applyAuth</c>: explicit request options win per field; headers merge auth, model and request
    /// headers, then <see cref="ModelRequestOptions.TransformHeaders"/> runs last; env merges resolved and request values;
    /// a resolved base URL replaces the model's. The provider's model rewrite then applies the merged env.</summary>
    private async Task<(TModel Model, TOptions Options)> ApplyAuthAsync<TModel, TOptions>(ModelOperationsProvider provider, TModel model,
        TOptions options, CancellationToken cancellationToken) where TModel : OperationModel where TOptions : ModelRequestOptions
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = await auth(new(model.Provider, options.ApiKey, options.Env.IsEmpty ? null : options.Env), cancellationToken).ConfigureAwait(false)
            ?? throw new ModelOperationsException(ModelOperationsErrorCode.Auth, $"Provider is not configured: {model.Provider}");
        cancellationToken.ThrowIfCancellationRequested();
        var headers = MergeHeaders(MergeHeaders(resolution.Headers, ProviderRequest.Nullable(model.Headers)), options.Headers);
        if (options.TransformHeaders is { } transform) headers = await transform(headers, cancellationToken).ConfigureAwait(false);
        var env = resolution.Env.SetItems(options.Env);
        OperationModel requestModel = resolution.BaseUrl is { } baseUrl ? model with { BaseUrl = baseUrl } : model;
        if (provider.ResolveModel is { } rewrite) requestModel = rewrite(requestModel, env);
        // `with` on the base record clones the runtime options type (ClassifierOptions or ImagesOptions).
        var requestOptions = (ModelRequestOptions)options with { ApiKey = options.ApiKey ?? resolution.ApiKey, Headers = headers, Env = env, TransformHeaders = null };
        return ((TModel)requestModel, (TOptions)requestOptions);
    }

    /// <summary>models.ts <c>mergeHeaders</c>: an override removes every case-insensitive match, then sets its own value
    /// (null included, which later suppresses the header on the wire).</summary>
    private static ImmutableArray<KeyValuePair<string, string?>> MergeHeaders(IEnumerable<KeyValuePair<string, string?>> baseHeaders,
        IEnumerable<KeyValuePair<string, string?>> overrides)
    {
        var merged = baseHeaders.ToList();
        foreach (var (name, value) in overrides)
        {
            merged.RemoveAll(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            merged.Add(new(name, value));
        }
        return [.. merged];
    }
}
