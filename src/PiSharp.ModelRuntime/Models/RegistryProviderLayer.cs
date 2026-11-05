// Web Agent W2 (WU2, plan "Architecture" item 2): the hook that keeps the built-in llama.cpp provider in PiSharp.Cli. Pi
// f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/extensions/index.ts (builtInExtensions register their
// provider after the pi-ai built-ins) and packages/coding-agent/src/extensions/llama/provider.ts (the provider's auth check/resolve and
// refreshModels), which ModelRegistry called directly before the move.
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>What one provider layer's catalog refresh may do (refreshModels' RefreshModelsContext): the stored catalog, the provider's
/// stored credential (its key resolved as a config value), whether the network may be used, and the publication.</summary>
internal sealed record RegistryLayerRefreshContext(ModelsStoreEntry? Stored, ProviderStoredCredential? Credential, bool AllowNetwork,
    Func<ModelsPublication, Task<bool>> Publish, CancellationToken CancellationToken);

/// <summary>
/// A built-in extension's provider that a host adds to <see cref="ModelRegistry"/> (<see cref="ModelRegistryOptions.ProviderLayers"/>):
/// its models after the pi-ai built-ins, its persisted catalog in the models store, and its own auth check and request auth while
/// models.json and extensions leave the provider unconfigured. The CLI's llama.cpp provider is the one layer; without it the registry
/// has no <c>llama.cpp</c> provider.
/// </summary>
internal interface IRegistryProviderLayer
{
    /// <summary>The provider id (unique among the layers).</summary>
    string ProviderId { get; }

    /// <summary>Whether the provider is registered now (false: its extension is disabled). Read at every composition.</summary>
    bool Loaded { get; }

    /// <summary>The provider's models of every type, as the latest refresh left them.</summary>
    IReadOnlyList<RegistryModel> Models { get; }

    /// <summary>refreshModels: restore <see cref="RegistryLayerRefreshContext.Stored"/>, then, when the network is allowed, revalidate and
    /// publish. Errors are reported per provider by <see cref="ModelRegistry.RefreshAsync"/>.</summary>
    Task RefreshAsync(RegistryLayerRefreshContext context);

    /// <summary>auth.apiKey.check: the source label, or null when the provider is not configured. An invalid configuration throws
    /// <see cref="InvalidOperationException"/> (read as not configured).</summary>
    string? CheckAuth(ProviderStoredCredential? credential, Func<string, string?> environment);

    /// <summary>auth.apiKey.resolve for one request: the key, environment and source, or null with <paramref name="error"/> set.</summary>
    ModelRequestAuth? ResolveRequestAuth(ProviderStoredCredential? credential, Func<string, string?> environment, out string? error);
}
