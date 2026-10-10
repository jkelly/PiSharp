// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (registerProvider and
// unregisterProvider: assertActive, then the runtime's model registry).
using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope : IExtensionModelOperationProviderRegistry
{
    private int modelOperationCleanup;

    public void RegisterModelOperationProvider(ExtensionModelOperationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = ModelOperationProviderHost();
        host.Register(OwnerId, Leased(provider));
        // The registrations leave with the extension's lifetime (a failed activation, reload or shutdown).
        if (Interlocked.Exchange(ref modelOperationCleanup, 1) == 0)
            ExtensionLifetimeCancellationToken.UnsafeRegister(static state =>
            {
                var (owner, target) = ((string, IExtensionModelOperationProviderHost))state!;
                try { target.UnregisterOwner(owner); } catch (Exception) { /* A retired owner's cleanup is the host's. */ }
            }, (OwnerId, host));
    }

    public void UnregisterModelOperationProvider(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ModelOperationProviderHost().Unregister(OwnerId, name);
    }

    private IExtensionModelOperationProviderHost ModelOperationProviderHost()
    {
        if (ExtensionLifetimeCancellationToken.IsCancellationRequested) throw new ObjectDisposedException(nameof(RegistrationScope), "The extension is no longer active.");
        return Registry.ModelOperationProviderHost ?? throw new NotSupportedException("Provider registration is unavailable: this host has no model registry.");
    }

    /// <summary>The provider with every classifier and image implementation run as the owner's callback, as the registry runs a tool:
    /// admitted only while this owner generation is active, under its callback lease (disposal and quiescence wait for it), in its
    /// callback frame, and cancelled with the extension's lifetime.</summary>
    private ExtensionModelOperationProvider Leased(ExtensionModelOperationProvider provider) => provider with
    {
        // Missing implementation maps stay missing: the host rejects them.
        Classifiers = provider.Classifiers is { } classifiers ? classifiers.ToImmutableDictionary(pair => pair.Key, pair => (ExtensionClassifierImplementation)((model, context, options, token) =>
            InvokeLeasedAsync("classify", token, owned => pair.Value(model, context, options, owned))), classifiers.KeyComparer) : provider.Classifiers!,
        Images = provider.Images is { } images ? images.ToImmutableDictionary(pair => pair.Key, pair => (ExtensionImagesImplementation)((model, context, options, token) =>
            InvokeLeasedAsync("generate-images", token, owned => pair.Value(model, context, options, owned))), images.KeyComparer) : provider.Images!
    };

    private async Task<T> InvokeLeasedAsync<T>(string operation, CancellationToken token, Func<CancellationToken, Task<T>> callback)
    {
        Registry.AdmitOwnerCallback(this, operation, token);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ExtensionLifetimeCancellationToken);
            using var frame = new CallbackFrame(this);
            return await callback(linked.Token).ConfigureAwait(false);
        }
        finally { Registry.ReleaseOwnerCallback(this); }
    }
}

public sealed partial class ExtensionRegistry
{
    /// <summary>The host's classifier and image providers, which <see cref="IExtensionModelOperationProviderRegistry"/> reaches; null
    /// when the host has no model registry. Set before the extensions initialize.</summary>
    public IExtensionModelOperationProviderHost? ModelOperationProviderHost { get; set; }

    /// <summary>The callback lease of an owner-level callback (no registration entry): the owner generation must be the active one, and
    /// the lease counts against the dispatch limit until <see cref="ReleaseOwnerCallback"/>.</summary>
    internal void AdmitOwnerCallback(RegistrationScope scope, string operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closing) throw Failure(ExtensionRegistrationFailure.InactiveScope, "registry", operation);
            if (!owners.TryGetValue(scope.OwnerId, out var current) || !ReferenceEquals(current, scope) || scope.State != RegistrationScopeState.Active)
                throw Failure(ExtensionRegistrationFailure.StaleSnapshot, scope.OwnerId, operation);
            if (dispatches >= options.MaximumConcurrentDispatches) throw Failure(ExtensionRegistrationFailure.LimitExceeded, "registry", operation);
            dispatches++;
            if (scope.ActiveCallbacks++ == 0) scope.Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void ReleaseOwnerCallback(RegistrationScope scope)
    {
        lock (gate)
        {
            if (--scope.ActiveCallbacks == 0) scope.Idle!.TrySetResult();
            dispatches--;
        }
    }
}
