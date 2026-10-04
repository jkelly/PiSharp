namespace PiSharp.Extensions.Runtime.Loading;

/// <summary>Owns one published generation; disposal joins registered work and requests cooperative unload.</summary>
public sealed class LoadedExtension : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly PluginAssemblyLoader loader;
    private readonly PluginReservation reservation;
    private RegistrationScope? scope;
    private OwnedPluginInstance? instance;
    private PluginPackageSnapshot? snapshot;
    private PluginLoadContext? context;
    private Task? disposal;
    public string OwnerId => reservation.OwnerId;
    public long OwnerGeneration { get; }
    public string ContractProfile => "experimental-published-fixture-0";
    public string? SnapshotDirectory { get; }
    public WeakReference? LoadContextReference { get; }
    public bool IsCollectible { get; }
    public bool UnloadRequested { get; private set; }

    internal LoadedExtension(PluginAssemblyLoader loader, PluginReservation reservation, RegistrationScope? scope,
        OwnedPluginInstance? instance, PluginPackageSnapshot? snapshot, PluginLoadContext? context, Exception? failedCleanup = null)
    {
        this.loader = loader; this.reservation = reservation; this.scope = scope; this.instance = instance;
        this.snapshot = snapshot; this.context = context;
        OwnerGeneration = scope?.OwnerGeneration ?? 0;
        SnapshotDirectory = snapshot?.DirectoryPath;
        LoadContextReference = context is null ? null : new(context);
        IsCollectible = context?.IsCollectible ?? false;
        if (failedCleanup is not null) disposal = Task.FromException(failedCleanup);
    }

    /// <summary>Pauses this loaded owner's registrations and joins already admitted callbacks.
    /// The published snapshot and loader reservation remain owned. Cancelling the wait resumes
    /// the same generation; disposing this extension while paused proceeds through actual cleanup.</summary>
    public ValueTask<RegistrationQuiescenceLease> QuiesceAsync(CancellationToken cancellationToken = default)
    {
        RegistrationScope current;
        lock (gate)
        {
            if (disposal is not null || scope is null)
                throw PluginAssemblyLoader.Failure(PluginLoadFailure.InactiveLoader, OwnerId, "quiesce");
            current = scope;
        }
        return current.QuiesceAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource settlement;
        lock (gate)
        {
            if (scope is not null && CallbackFrame.IsExecuting(scope))
                throw PluginAssemblyLoader.Failure(PluginLoadFailure.ReentrantDisposal, OwnerId, "dispose");
            if (disposal is not null) return new(disposal);
            settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = settlement.Task;
        }
        _ = CloseAsync(settlement);
        return new(settlement.Task);
    }

    private async Task CloseAsync(TaskCompletionSource settlement)
    {
        try
        {
            if (scope is not null) await scope.DisposeAsync().ConfigureAwait(false);
            if (instance is not null) await instance.DisposeAsync().ConfigureAwait(false);
            context?.ReleaseRootsAndRequestUnload();
            UnloadRequested = context is not null;
            context = null; instance = null; scope = null;
            if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
            snapshot = null;
            loader.Release(reservation);
            settlement.TrySetResult();
        }
        catch (Exception error)
        { settlement.TrySetException(PluginAssemblyLoader.Failure(PluginLoadFailure.CleanupFailed, OwnerId, "dispose", error)); }
    }
}

/// <summary>Registry and loader may both own a failure path. Actual plugin shutdown runs exactly once.</summary>
internal sealed class OwnedPluginInstance(IPiSharpExtension extension) : IPiSharpExtension
{
    private readonly object gate = new();
    private IPiSharpExtension? inner = extension;
    private Task? disposal;
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) =>
        (inner ?? throw new ObjectDisposedException(nameof(OwnedPluginInstance))).InitializeAsync(registry, cancellationToken);
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource settlement;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = settlement.Task;
        }
        _ = CloseAsync(settlement);
        return new(settlement.Task);
    }
    private async Task CloseAsync(TaskCompletionSource settlement)
    {
        try { await inner!.DisposeAsync().ConfigureAwait(false); inner = null; settlement.TrySetResult(); }
        catch (Exception error) { settlement.TrySetException(error); }
    }
}
