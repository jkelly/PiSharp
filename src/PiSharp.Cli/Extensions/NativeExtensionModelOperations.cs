// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-registry.ts (classify,
// generateImages, getModelsOfType, findOfType, getAvailableOfType), core/model-runtime.ts (prepareRequest),
// core/auth-storage.ts (stored credentials) and packages/coding-agent/src/extensions/codemode/execute.ts (toModelInfo:
// headers are not exposed; models are resolved by provider and id).
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using PiSharp.AI.Authentication;
using PiSharp.AI.Catalogs;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Authentication;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>
/// The extension view of a <see cref="ModelOperationsRegistry"/>: models as catalog JSON without <c>headers</c>, and
/// classification and image generation for a model resolved by type, provider and id in the registry, so a supplied
/// <c>baseUrl</c> or <c>headers</c> never receives the provider's credentials. Every failure is an error result.
/// </summary>
public sealed class NativeExtensionModelOperations(ModelOperationsRegistry registry)
{
    public ModelOperationsRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));

    public ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider) =>
        [.. Registry.GetModelsOfType(type, provider).Select(model => model.ToPublicJson())];

    public JsonData? GetModelOfType(ModelType type, string provider, string id) => Registry.GetModelOfType(type, provider, id)?.ToPublicJson();

    public async Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider, CancellationToken cancellationToken) =>
        [.. (await Registry.GetAvailableOfTypeAsync(type, provider, cancellationToken).ConfigureAwait(false)).Select(model => model.ToPublicJson())];

    public async Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, ExtensionModelRequestOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        var (resolved, error) = Resolve(model, ModelType.Classifier, "classifier");
        if (resolved is not ClassifierModel classifier)
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
        var (resolved, error) = Resolve(model, ModelType.Image, "image");
        if (resolved is not ImageModel image)
            return new AssistantImages(Text(model, "api"), Text(model, "provider"), Text(model, "id"), [], ModelOperationStopReason.Error,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { ErrorMessage = error };
        return await Registry.GenerateImagesAsync(image, context, new ImagesOptions
        {
            ApiKey = options?.ApiKey, Headers = options?.Headers ?? [], TimeoutMs = options?.TimeoutMs, MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The registry's entry for the supplied model's provider and id, with upstream's wrong-type and unknown-model
    /// messages (utils/model-operations.ts <c>assertClassifierModel</c>/<c>assertImageModel</c>).</summary>
    private (OperationModel? Model, string Error) Resolve(JsonData model, ModelType type, string name)
    {
        var provider = Text(model, "provider"); var id = Text(model, "id");
        if (provider.Length == 0 || id.Length == 0) return (null, $"Expected {(name == "image" ? "an" : "a")} {name} model with a provider and an id");
        if (Registry.GetProvider(provider) is null) return (null, $"Unknown provider: {provider}");
        if (Registry.GetModelOfType(type, provider, id) is { } found) return (found, "");
        return Registry.GetAllModels(provider).Any(entry => entry.Id == id)
            ? (null, $"Model {provider}/{id} is not {(name == "image" ? "an" : "a")} {name} model")
            : (null, $"Unknown {name} model \"{provider}/{id}\"");
    }

    private static string Text(JsonData model, string name) =>
        model.Value.ValueKind == JsonValueKind.Object && model.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    /// <summary>The CLI's registry: every embedded provider catalog shard (<c>PiSharp.Cli.Models.*.json</c>) as the
    /// built-in providers, with auth resolved per request as upstream resolveProviderAuth does for api-key providers: a
    /// stored <c>auth.json</c> credential owns the provider (an api_key credential supplies its key and env, an OAuth
    /// credential its access token, marked OAuth), else the provider's environment variable.</summary>
    public static ModelOperationsRegistry CreateDefaultRegistry(Func<string, string?> readEnvironment, string? authPath)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        var catalogs = new List<FrozenModelCatalog>();
        var assembly = typeof(NativeExtensionModelOperations).Assembly;
        const string prefix = "PiSharp.Cli.Models.";
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!; using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            try { catalogs.Add(FrozenModelCatalog.ReadProviderJson(name[prefix.Length..^".json".Length], buffer.ToArray())); }
            catch (CatalogReadException) { }
        }
        var store = authPath is null ? null : new AuthJsonCredentialStore(authPath);
        var standard = ModelOperationsAuth.Standard(readEnvironment, store is null ? null : async (provider, token) =>
        {
            var entry = await store.ReadEntryAsync(provider, token).ConfigureAwait(false);
            if (entry is not { Type: "api_key" }) return null;
            if (entry.Key is { Length: > 0 } configured && ConfigValueTemplate.IsCommand(configured))
                throw new InvalidOperationException("Stored API key commands (\"!command\") are not run by PiSharp; store the key value instead.");
            return new StoredApiKeyCredential(entry.Key is null ? null : ConfigValueTemplate.Resolve(entry.Key, entry.Environment, readEnvironment),
                entry.Environment is null ? null : new ProviderEnvironmentSnapshot(scoped: entry.Environment.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))));
        });
        return ModelOperationsRegistry.CreateBuiltin(catalogs, async (request, token) =>
        {
            if (store is not null && request.ApiKey is null && await store.ReadEntryAsync(request.Provider, token).ConfigureAwait(false) is { } entry)
            {
                // A stored credential owns the provider: no environment fallback for an OAuth or unknown entry. OAuth
                // tokens travel in the apiKey channel; refreshing them is the chat route's (not done here).
                if (entry.Type == "oauth")
                    return await store.ReadAsync(request.Provider, token).ConfigureAwait(false) is { } oauth
                        ? new ProviderAuthResult(oauth.Access) { IsOAuth = true } : null;
                if (entry.Type != "api_key") return null;
            }
            return await standard(request, token).ConfigureAwait(false);
        });
    }
}
