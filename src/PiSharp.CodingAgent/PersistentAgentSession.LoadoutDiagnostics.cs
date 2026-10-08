using System.Runtime.ExceptionServices;

namespace PiSharp.CodingAgent;

// Preserve primary admission failure and exact reporter causes; never replace either with a generic diagnostic.
internal static class SessionLoadoutDiagnosticBoundary
{
    internal static async Task<T> RunAsync<T>(Func<T> prepare, Func<ValueTask> drain)
    {
        T result = default!; Exception? preparationFailure = null;
        try { result = prepare(); } catch (Exception error) { preparationFailure = error; }
        try { await drain().ConfigureAwait(false); }
        catch (Exception reporter)
        {
            if (preparationFailure is not null)
                throw new AggregateException("Loadout preparation and diagnostic delivery failed.", preparationFailure, reporter);
            throw;
        }
        if (preparationFailure is not null) ExceptionDispatchInfo.Capture(preparationFailure).Throw();
        return result;
    }
}

public sealed partial class PersistentAgentSession
{
    private readonly AsyncLocal<bool> _inLoadoutDiagnosticDrain = new();
    private readonly HashSet<LoadoutDrainWork> _loadoutDrains = [];
    private int _synchronousLoadoutWork;
    private TaskCompletionSource? _synchronousLoadoutIdle;
    private sealed class LoadoutDrainWork { internal Task Original = null!; }

    /// <summary>Drain captured diagnostics using the exact borrowed registry callback. No detached reporter or runtime authority.
    /// Once admitted, caller cancellation cannot abandon the original callback; close joins it before runtime release.</summary>
    public Task DrainLoadoutDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        if (_inLoadoutDiagnosticDrain.Value) throw new InvalidOperationException("A diagnostic reporter cannot await its own session drain.");
        TaskCompletionSource start; LoadoutDrainWork owned;
        lock (_gate)
        {
            ThrowAvailable(); cancellationToken.ThrowIfCancellationRequested();
            var registry = _registry;
            if (registry is null || !registry.HasLoadoutDiagnosticDrain) return Task.CompletedTask;
            if (_loadoutDrains.Count >= (_agentOptions?.MaximumSubscribers ?? 128))
                throw new InvalidOperationException("Session diagnostic drain capacity reached.");
            start = new(TaskCreationOptions.RunContinuationsAsynchronously); owned = new();
            owned.Original = DrainLoadoutCoreAsync(registry, cancellationToken, start.Task, owned);
            _loadoutDrains.Add(owned);
        }
        start.SetResult(); return owned.Original;
    }
    private async Task DrainLoadoutCoreAsync(SessionRuntimeRegistry registry, CancellationToken token, Task start, LoadoutDrainWork owned)
    {
        await start.ConfigureAwait(false); var prior = _inLoadoutDiagnosticDrain.Value; _inLoadoutDiagnosticDrain.Value = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            await registry.DrainLoadoutDiagnosticsAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _inLoadoutDiagnosticDrain.Value = prior; lock (_gate) _loadoutDrains.Remove(owned); }
    }
    private Task<T> PrepareAndDrainLoadoutAsync<T>(Func<T> prepare, CancellationToken token)
        => SessionLoadoutDiagnosticBoundary.RunAsync(prepare, () => new ValueTask(DrainLoadoutDiagnosticsAsync(token)));

    /// <summary>Awaited alternative to synchronous logical selection. Selection remains pending until the existing request checkpoint.
    /// A reporting failure does not roll back an already accepted logical selection.</summary>
    public Task<SessionToolActivationSelection> ScheduleToolActivationAsync(System.Collections.Immutable.ImmutableArray<string> names,
        CancellationToken cancellationToken = default) => PrepareAndDrainLoadoutAsync(() => ScheduleToolActivation(names, cancellationToken), cancellationToken);

    private IDisposable ReserveSynchronousLoadoutWork()
    {
        lock (_gate)
        {
            ThrowAvailable();
            if (_synchronousLoadoutWork >= (_agentOptions?.MaximumSubscribers ?? 128))
                throw new InvalidOperationException("Session loadout preparation capacity reached.");
            if (_synchronousLoadoutWork++ == 0) _synchronousLoadoutIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new SynchronousLoadoutLease(this);
    }
    private sealed class SynchronousLoadoutLease(PersistentAgentSession owner) : IDisposable
    {
        private PersistentAgentSession? current = owner;
        public void Dispose()
        {
            var session = Interlocked.Exchange(ref current, null); if (session is null) return; TaskCompletionSource? idle = null;
            lock (session._gate) if (--session._synchronousLoadoutWork == 0) { idle = session._synchronousLoadoutIdle; session._synchronousLoadoutIdle = null; }
            idle?.TrySetResult();
        }
    }
    private Task LoadoutDiagnosticIdleLocked() => Task.WhenAll(
        _loadoutDrains.Select(work => work.Original).Append(_synchronousLoadoutIdle?.Task ?? Task.CompletedTask));
    private async ValueTask DrainLoadoutAtCloseAsync()
    {
        var registry = _registry; if (registry is null || !registry.HasLoadoutDiagnosticDrain) return;
        var prior = _inLoadoutDiagnosticDrain.Value; _inLoadoutDiagnosticDrain.Value = true;
        try { await registry.DrainLoadoutDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { _inLoadoutDiagnosticDrain.Value = prior; }
    }
}
