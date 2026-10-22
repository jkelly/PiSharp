// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/extensions/llama/provider.ts (the built-in llama.cpp
// provider: auth.apiKey check/resolve, refreshModels and its api-key login method) and packages/coding-agent/src/extensions/index.ts
// (builtInExtensions). Web Agent W2 (WU2): ModelRegistry and ProviderAuthCatalog moved to PiSharp.ModelRuntime, so the provider joins
// them through IRegistryProviderLayer and ProviderAuthCatalog.BuiltinExtensions and stays in the CLI.
using System.Runtime.CompilerServices;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Models;

namespace PiSharp.Cli.Llama;

/// <summary>The built-in llama.cpp provider as a registry layer: the catalog a models store shares, loaded unless the built-in
/// extension is disabled (<c>-builtin:llama.cpp</c>, <c>--no-extensions</c>).</summary>
internal sealed class LlamaRegistryLayer(LlamaCatalog catalog, Func<bool>? loaded, Func<HttpMessageInvoker>? createHttp = null) : IRegistryProviderLayer
{
    /// <summary>The llama.cpp catalog this layer composes (provider.ts createLlamaProvider's state).</summary>
    internal LlamaCatalog Catalog => catalog;

    public string ProviderId => LlamaCatalog.ProviderId;

    public bool Loaded => loaded?.Invoke() != false;

    public IReadOnlyList<RegistryModel> Models => catalog.AllModels;

    /// <summary>provider.ts refreshModels with the stored api_key credential of llama.cpp.</summary>
    public Task RefreshAsync(RegistryLayerRefreshContext context) =>
        catalog.RefreshAsync(new(context.Stored, View(context.Credential), context.AllowNetwork, context.Publish, context.CancellationToken, createHttp?.Invoke()));

    /// <summary>provider.ts auth.apiKey.check: configured once a server URL is known (credential env, then LLAMA_BASE_URL).</summary>
    public string? CheckAuth(ProviderStoredCredential? credential, Func<string, string?> environment) => LlamaCatalog.Check(View(credential), environment);

    /// <summary>provider.ts auth.apiKey.resolve: the key, and the server URL as <c>LLAMA_BASE_URL</c>.</summary>
    public ModelRequestAuth? ResolveRequestAuth(ProviderStoredCredential? credential, Func<string, string?> environment, out string? error)
    {
        error = null;
        LlamaAuth? resolved;
        try { resolved = LlamaCatalog.Resolve(View(credential), environment); }
        catch (InvalidOperationException failure) { error = failure.Message; return null; }
        if (resolved is null) { error = $"No API key found for \"{ProviderId}\""; return null; }
        return new(resolved.ApiKey, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["LLAMA_BASE_URL"] = resolved.ServerUrl }, resolved.Source);
    }

    internal static ProviderStoredCredentialView? View(ProviderStoredCredential? credential) =>
        credential is null ? null : new(credential.Type, credential.Key, credential.Environment);

    /// <summary>llama.cpp's api-key login method (provider.ts), listed while the built-in extension is loaded (the Pi entry's
    /// <see cref="PiSharp.Cli.Pi.PiEntryOptions.Current"/>; loaded outside it).</summary>
    internal static readonly ProviderAuthEntry AuthEntry =
        new(LlamaCatalog.ProviderId, "llama.cpp", "llama.cpp server", ["LLAMA_BASE_URL"], ApiKeyLoginKind.LlamaCpp, null);

    [ModuleInitializer]
    internal static void RegisterBuiltinExtension() => ProviderAuthCatalog.BuiltinExtensions = () =>
        PiSharp.Cli.Pi.PiEntryOptions.Current?.BuiltinExtensions?.IsEnabled(PiSharp.Cli.Extensions.Pi.PiBuiltinExtensions.Llama) == false ? [] : [AuthEntry];
}

/// <summary>The llama.cpp members the CLI reads from a registry (the /llama command and its catalog sync).</summary>
internal static class LlamaRegistry
{
    extension(ModelRegistry registry)
    {
        /// <summary>The llama.cpp catalog this registry's models store shares (provider.ts createLlamaProvider's state); a registry built
        /// without the layer reads the catalog of its models store.</summary>
        internal LlamaCatalog Llama => registry.Layer(LlamaCatalog.ProviderId) is LlamaRegistryLayer layer ? layer.Catalog
            : LlamaCatalog.For(registry.Options.ModelsStore is FileModelsStore file ? file.Path : null);

        /// <summary>provider.ts auth.apiKey.resolve for llama.cpp (the /llama command's <c>getProviderAuth</c>): the server URL and key,
        /// or null when no server URL is configured. An invalid configured URL throws.</summary>
        internal LlamaAuth? ResolveLlamaAuth() =>
            LlamaCatalog.Resolve(LlamaRegistryLayer.View(registry.StoredCredential(LlamaCatalog.ProviderId)), registry.EnvironmentValue);
    }
}
