// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (registerProvider and
// unregisterProvider for providers with classifiers or images) and packages/coding-agent/src/core/model-runtime.ts (registerProvider:
// the provider joins the session's model registry, unregisterProvider and a reload remove it).
using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;
using PiSharp.Extensions;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>The classifier and image providers native C# extensions registered (<see cref="IExtensionModelOperationProviderRegistry"/>),
/// by owner, in registration order; a later registration of a name wins.</summary>
internal sealed class PiNativeModelProviders : IExtensionModelOperationProviderHost
{
    private readonly object gate = new();
    private readonly List<(string Owner, ExtensionModelOperationProvider Provider)> entries = [];

    /// <summary>Raised after a registration changed (outside the lock).</summary>
    internal event Action? Changed;

    internal ImmutableArray<ExtensionModelOperationProvider> Current { get { lock (gate) return [.. entries.Select(entry => entry.Provider)]; } }

    public void Register(string ownerId, ExtensionModelOperationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(ownerId); ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Name)) throw new ArgumentException("Provider name must not be empty.", nameof(provider));
        if (provider.Models.IsDefault || provider.Classifiers is null || provider.Images is null) throw new ArgumentException($"Provider {provider.Name}: models, classifiers and images must be set.", nameof(provider));
        // Validated alone first, as registerProvider does: a broken registration throws without changing the stored one.
        _ = PiExtensionModels.BuildModels(provider.Name, provider.BaseUrl, [.. provider.Classifiers.Keys], [.. provider.Images.Keys],
            provider.Models.Select(PiExtensionModels.ModelObject), error => throw new ArgumentException(error, nameof(provider)));
        lock (gate)
        {
            entries.RemoveAll(entry => entry.Owner == ownerId && entry.Provider.Name == provider.Name);
            entries.Add((ownerId, provider));
        }
        Changed?.Invoke();
    }

    public void Unregister(string ownerId, string name)
    {
        ArgumentNullException.ThrowIfNull(ownerId); ArgumentNullException.ThrowIfNull(name);
        int removed;
        lock (gate) removed = entries.RemoveAll(entry => entry.Owner == ownerId && entry.Provider.Name == name);
        if (removed > 0) Changed?.Invoke();
    }

    public void UnregisterOwner(string ownerId)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        int removed;
        lock (gate) removed = entries.RemoveAll(entry => entry.Owner == ownerId);
        if (removed > 0) Changed?.Invoke();
    }
}

internal sealed partial class PiExtensionHost
{
    /// <summary>The native extensions' classifier and image providers (every generation of the run registers here).</summary>
    internal PiNativeModelProviders NativeModelProviders { get; } = new();

    /// <summary>The run's model registry for classification and image generation, as the native extensions' <c>ctx.modelRegistry</c>
    /// reads it.</summary>
    internal void BindModelOperations(ModelOperationsRegistry registry)
    {
        RunModelOperations = registry;
        _activation?.BindRunModelOperations(registry);
    }

    /// <summary>The registry <see cref="BindModelOperations"/> bound (an activation created later binds it too).</summary>
    internal ModelOperationsRegistry? RunModelOperations { get; private set; }
}
