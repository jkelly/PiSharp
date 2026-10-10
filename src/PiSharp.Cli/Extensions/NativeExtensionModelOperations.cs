// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-registry.ts (classify,
// generateImages, getModelsOfType, findOfType, getAvailableOfType), core/model-runtime.ts (prepareRequest),
// core/auth-storage.ts (stored credentials), packages/ai/src/auth/resolve.ts (resolveProviderAuth, resolveStoredOAuth) and
// packages/ai/src/models.ts (checkProviderAuth).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.AI.ModelOperations;
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
    /// fallback. Without a stored credential the provider's environment variable applies.
    /// </summary>
    public static ModelOperationsRegistry CreateDefaultRegistry(Func<string, string?> readEnvironment, string? authPath,
        Func<HttpMessageInvoker>? createAuthHttp = null, TimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, IAdmittedOAuthRefresh>? oauthRefreshes = null)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        var catalogs = new List<FrozenModelCatalog>();
        foreach (var provider in BuiltinModelCatalog.ShardHashes.Keys.Order(StringComparer.Ordinal))
        {
            try { catalogs.Add(BuiltinModelCatalog.Get(provider)); }
            catch (Exception error) when (error is BuiltinCatalogException or CatalogReadException) { }
        }
        var store = authPath is null ? null : new AuthJsonCredentialStore(authPath, timeProvider);
        createAuthHttp ??= () => new HttpClient();
        var refreshes = new ProviderOAuthRefreshes(oauthRefreshes ?? DefaultOAuthRefreshes(readEnvironment, createAuthHttp, timeProvider));
        var lifecycle = store is null ? null : new StoredOAuthLifecycle(store, refreshes, timeProvider);
        var standard = ModelOperationsAuth.Standard(readEnvironment, store is null ? null : async (provider, token) =>
        {
            var entry = await store.ReadEntryAsync(provider, token).ConfigureAwait(false);
            if (entry is not { Type: "api_key" }) return null;
            return new StoredApiKeyCredential(entry.Key is null ? null : ConfigValueTemplate.Resolve(entry.Key, entry.Environment, readEnvironment),
                entry.Environment is null ? null : new ProviderEnvironmentSnapshot(scoped: entry.Environment.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))));
        });
        var registry = ModelOperationsRegistry.CreateBuiltin(catalogs, async (request, token) =>
        {
            // auth/resolve.ts: an explicit key bypasses the store; otherwise a stored credential owns the provider.
            if (store is not null && request.ApiKey is null && await store.ReadEntryAsync(request.Provider, token).ConfigureAwait(false) is { } entry)
            {
                if (entry.Type == "oauth")
                {
                    if (!refreshes.Supports(request.Provider)) return null;
                    // checkProviderAuth: a stored OAuth credential is configured without being refreshed.
                    if (request.Check) return new ProviderAuthResult(null) { IsOAuth = true };
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
                    return credential is null ? null : new ProviderAuthResult(credential.Access) { IsOAuth = true };
                }
                if (entry.Type != "api_key") return null;
            }
            return await standard(request, token).ConfigureAwait(false);
        });
        // The built-in llama.cpp extension's provider (extensions/llama/provider.ts getAllModels): the classifier models of the catalog
        // the models store next to auth.json holds, as the latest refresh or /llama left them.
        var llama = PiSharp.Cli.Llama.LlamaCatalog.For(authPath is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(authPath))!, "models-store.json"));
        registry.SetProvider(BuiltinModelOperationProviders.LlamaCpp([], () =>
        {
            var models = ImmutableArray.CreateBuilder<OperationModel>();
            foreach (var model in llama.LoadedClassifierJson())
                try { models.Add(OperationModel.FromJson(JsonData.Parse(model.ToJsonString()))); } catch (FormatException) { }
            return models.ToImmutable();
        }));
        return registry;
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
