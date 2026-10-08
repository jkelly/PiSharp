using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

public sealed record NativeInitializedRegistrationOwner(RegistrationScope Scope,
    NativeExtensionRegistrationFacade Facade, Task<RegistrationScope> ActivationOriginal);

/// <summary>Exact native original inventory; a faulted OCE stays a fault.</summary>
public sealed class NativeRegistrationOriginalException(string phase, Task? original, Exception evidence)
    : IOException("Native registration " + phase + " failed.", evidence)
{
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}

/// <summary>Explicit application installation, using the same real registry as its loader. This owns
/// flag/provider metadata hosts, borrows the registry, and never obtains credentials or transports.</summary>
public sealed partial class NativeExtensionRegistrationBridge : IDisposable
{
    private readonly object gate = new();
    private readonly ExtensionRegistry registry;
    private readonly ExtensionFlagRegistrationHost flags;
    private readonly ExtensionProviderRegistrationHost providers;
    private readonly Dictionary<(string OwnerId, long Generation), RegistrationScope> installed = [];
    private bool disposed;
    public NativeExtensionRegistrationBridge(ExtensionRegistry registry, IExtensionHostFlagValues admittedFlagValues,
        IEnumerable<ExtensionProviderDefinition> admittedBuiltins, IExtensionProviderConfigurationAdapter admittedConfiguration)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        providers = new(registry, admittedBuiltins, admittedConfiguration);
        try { flags = new(registry, admittedFlagValues); }
        catch { providers.Dispose(); throw; }
    }
    public ImmutableArray<ExtensionProviderModel> CaptureModels()
    { lock (gate) { Available(); return providers.CaptureModels(); } }
    public ImmutableArray<ExtensionFlagRegistrationInfo> CaptureFlags()
    { lock (gate) { Available(); Prune(); return flags.CaptureFlags().Where(row => installed.ContainsKey((row.OwnerId, row.OwnerGeneration))).ToImmutableArray(); } }
    /// <summary>The caller installs this actual transport in its admitted session runtime; creation
    /// alone does not publish a model into the application's SessionRuntimeRegistry catalog.</summary>
    public ExtensionProviderTransport CreateTransport()
    { lock (gate) { Available(); return new(providers); } }

    /// <summary>Loader adapters may call this instead of their existing registry.ActivateAsync and
    /// retain returned Scope/ActivationOriginal. Binder injection precedes the original initializer.
    /// Admission refusal before initialization leaves the supplied extension owned by its caller.</summary>
    public async Task<NativeInitializedRegistrationOwner> ActivateOwnerAsync(string ownerId,
        IPiSharpExtension extension, Action<NativeExtensionRegistrationFacade> admittedInitializerBinder,
        CancellationToken initializationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension); ArgumentNullException.ThrowIfNull(admittedInitializerBinder);
        if (admittedInitializerBinder.GetInvocationList().Length != 1) throw new ArgumentException("One initializer binder required.");
        lock (gate) Available();
        var wrapped = new Initializer(this, extension, admittedInitializerBinder);
        var activation = registry.ActivateAsync(ownerId, wrapped, initializationToken);
        RegistrationScope scope;
        try { scope = await activation.ConfigureAwait(false); }
        catch (Exception error)
        {
            wrapped.Facade?.Abort();
            ExceptionDispatchInfo.Capture(Preserve(activation, error, initializationToken, "activation")).Throw(); throw;
        }
        var facade = wrapped.Facade;
        try
        {
            if (facade is null || !ReferenceEquals(facade.Scope, scope)) throw new InvalidOperationException("Exact initialized native owner required.");
            var result = new NativeInitializedRegistrationOwner(scope, facade, activation);
            lock (gate)
            {
                Available(); initializationToken.ThrowIfCancellationRequested();
                Prune();
                if (installed.Count == 128) throw new InvalidOperationException("Installed owner bound exceeded.");
                installed.Add((scope.OwnerId, scope.OwnerGeneration), scope);
                // Provider publication is reversible through the real owner's lifetime. Flag defaults
                // are last: admitted values must obey their atomic/non-reentrant CommitDefaults contract.
                providers.CommitOwnerProviders(scope);
                flags.CommitOwnerFlags(scope);
                RefreshConfiguredCatalog();
                facade.Promote();
            }
            return result;
        }
        catch (Exception publication)
        {
            lock (gate) installed.Remove((scope.OwnerId, scope.OwnerGeneration));
            facade?.Abort(); Task? cleanup = null; Exception? cleanupFailure = null;
            try { cleanup = scope.DisposeAsync().AsTask(); await cleanup.ConfigureAwait(false); }
            catch (Exception error)
            {
                cleanupFailure = Preserve(cleanup, error, initializationToken, "activated owner cleanup");
            }
            try { RefreshConfiguredCatalog(); }
            catch (Exception error)
            {
                var failures = new List<Exception> { publication };
                if (cleanupFailure is not null) failures.Add(cleanupFailure);
                failures.Add(Preserve(null, error, initializationToken, "rollback catalog reconciliation"));
                throw new AggregateException("Registration publication, native cleanup or catalog reconciliation failed.", failures);
            }
            if (cleanupFailure is not null) throw new AggregateException("Registration publication and native owner cleanup failed.", publication, cleanupFailure);
            ExceptionDispatchInfo.Capture(Preserve(null, publication, initializationToken, "publication")).Throw(); throw;
        }
    }
    private void Available() => ObjectDisposedException.ThrowIf(disposed, this);
    private void Prune()
    {
        foreach (var pair in installed.Where(pair => pair.Value.ExtensionLifetimeCancellationToken.IsCancellationRequested).ToArray())
            installed.Remove(pair.Key);
    }
    internal static Exception Preserve(Task? original, Exception error, CancellationToken owned, string phase)
    {
        if (original?.IsFaulted == true) return new NativeRegistrationOriginalException(phase, original, original.Exception!);
        if (error is OperationCanceledException cancellation && !(owned.IsCancellationRequested && cancellation.CancellationToken == owned))
            return new NativeRegistrationOriginalException(phase, original, error);
        return error;
    }
    private sealed class Initializer(NativeExtensionRegistrationBridge bridge, IPiSharpExtension original,
        Action<NativeExtensionRegistrationFacade> binder) : IPiSharpExtension
    {
        internal NativeExtensionRegistrationFacade? Facade { get; private set; }
        public async ValueTask InitializeAsync(IExtensionRegistry native, CancellationToken token)
        {
            if (Facade is not null || native is not RegistrationScope scope) throw new InvalidOperationException("One exact native initialization required.");
            lock (bridge.gate)
            {
                bridge.Available();
                Facade = new(scope, bridge.flags.Bind(native), bridge.providers.Bind(native), bridge.RefreshConfiguredCatalog);
            }
            Task? initialization = null;
            try
            {
                binder(Facade);
                initialization = original.InitializeAsync(native, token).AsTask();
                await initialization.ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); Facade.Seal();
            }
            catch (Exception error)
            {
                Facade.Abort();
                ExceptionDispatchInfo.Capture(Preserve(initialization, error, token, "initializer")).Throw(); throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Task? cleanup = null;
            try { cleanup = original.DisposeAsync().AsTask(); await cleanup.ConfigureAwait(false); }
            catch (Exception error)
            {
                ExceptionDispatchInfo.Capture(Preserve(cleanup, error, CancellationToken.None, "extension cleanup")).Throw(); throw;
            }
        }
    }
    /// <summary>Retires this bridge's metadata. It does not own registry/scope cleanup: its caller must
    /// directly join the native registry's DisposeAsync, including any admitted stream originals.</summary>
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; installed.Clear(); }
        var errors = new List<Exception>();
        try { providers.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { flags.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Registration metadata retirement failed.", errors);
    }
}

/// <summary>Owner-specific initializer facade. Sealed until native activation and host publication
/// complete; afterward delegates to the real owner-bound facades. No public raw host escape.</summary>
public sealed class NativeExtensionRegistrationFacade : IExtensionFlagRegistrationFacade, IExtensionProviderRegistrationFacade
{
    private readonly object gate = new();
    private readonly IExtensionFlagRegistrationFacade flags;
    private readonly IExtensionProviderRegistrationFacade providers;
    private readonly Action refresh;
    private int phase; // initializing, sealed, active, aborted
    internal RegistrationScope Scope { get; }
    internal NativeExtensionRegistrationFacade(RegistrationScope scope, IExtensionFlagRegistrationFacade flags,
        IExtensionProviderRegistrationFacade providers, Action refresh) { Scope = scope; this.flags = flags; this.providers = providers; this.refresh = refresh; }
    public string OwnerId => Scope.OwnerId;
    public long OwnerGeneration => Scope.OwnerGeneration;
    private void Available()
    {
        if (phase is 1 or 3 || Scope.ExtensionLifetimeCancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("Initializer facade is sealed, aborted or retired.");
    }
    internal void Seal() { lock (gate) { Available(); phase = 1; } }
    internal void Promote() { lock (gate) phase = 2; }
    internal void Abort() { lock (gate) phase = 3; }
    public IExtensionRegistration RegisterFlag(string name, ExtensionFlagOptions options)
    { lock (gate) { Available(); return flags.RegisterFlag(name, options); } }
    public ExtensionFlagValue? GetFlag(string name)
    { lock (gate) { Available(); return flags.GetFlag(name); } }
    public void RegisterProvider(ExtensionProviderDefinition provider)
    { lock (gate) { Available(); providers.RegisterProvider(provider); if (phase == 2) refresh(); } }
    public void RegisterProvider(string name, JsonData configuration)
    { lock (gate) { Available(); providers.RegisterProvider(name, configuration); if (phase == 2) refresh(); } }
    public void UnregisterProvider(string name)
    { lock (gate) { Available(); providers.UnregisterProvider(name); if (phase == 2) refresh(); } }
}
