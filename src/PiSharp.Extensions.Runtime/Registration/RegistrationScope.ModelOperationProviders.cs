// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (registerProvider and
// unregisterProvider: assertActive, then the runtime's model registry).
namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope : IExtensionModelOperationProviderRegistry
{
    private int modelOperationCleanup;

    public void RegisterModelOperationProvider(ExtensionModelOperationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = ModelOperationProviderHost();
        host.Register(OwnerId, provider);
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
}

public sealed partial class ExtensionRegistry
{
    /// <summary>The host's classifier and image providers, which <see cref="IExtensionModelOperationProviderRegistry"/> reaches; null
    /// when the host has no model registry. Set before the extensions initialize.</summary>
    public IExtensionModelOperationProviderHost? ModelOperationProviderHost { get; set; }
}
