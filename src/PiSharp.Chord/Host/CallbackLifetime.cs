using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

public sealed partial class ChordHost
{
    private readonly Followers<CallbackLifetimeRegistration> callbackLifetimes = new();

    /// <summary>
    /// Registers an optional owner of callback code (for example a collectible plugin generation). The resolver
    /// may return null for code it does not own and must be safe on any callback thread. Acquired leases cover
    /// original callback completion and native/follow resource lifetime, independently of caller deadlines.
    /// Disposing this registration unregisters future acquisitions and never releases an outstanding lease.
    /// </summary>
    public IDisposable RegisterCallbackLifetime(Func<Delegate, IDisposable?> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        return RegisterCallbackLifetime((callback, _) => acquire(callback));
    }

    /// <summary>Registers a callback owner with the immutable facet that declared the callback as an additional ownership identity.
    /// A callback supplied by shared library code can still belong to the plugin generation that returned that facet.</summary>
    public IDisposable RegisterCallbackLifetime(Func<Delegate, Facet?, IDisposable?> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        return Dispatcher.Invoke(() =>
        {
            AssertOpen();
            var registration = new CallbackLifetimeRegistration(callbackLifetimes, acquire);
            callbackLifetimes.Add(registration);
            return (IDisposable)registration;
        });
    }

    internal IDisposable? RetainCallback(Delegate callback, Facet? declaringFacet = null)
    {
        List<IDisposable>? leases = null;
        try
        {
            foreach (var registration in callbackLifetimes.Items)
                if (registration.Acquire(callback, declaringFacet) is { } lease) (leases ??= []).Add(lease);
            return leases is null ? null : new CallbackLeases(leases.ToArray());
        }
        catch
        {
            if (leases is not null) foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }
    internal IDisposable? RetainCallbacks(params Delegate[] callbacks) => RetainCallbacks(null, callbacks);
    internal IDisposable? RetainCallbacks(Facet? declaringFacet, params Delegate[] callbacks)
    {
        List<IDisposable>? leases = null;
        try
        {
            foreach (var callback in callbacks)
                if (RetainCallback(callback, declaringFacet) is { } lease) (leases ??= []).Add(lease);
            return leases is null ? null : new CallbackLeases(leases.ToArray());
        }
        catch
        {
            if (leases is not null) foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }
    internal Task<T> InvokeCallbackAsync<T>(Delegate callback, Func<Task<T>> invoke, Facet? declaringFacet = null)
    {
        var lease = RetainCallback(callback, declaringFacet);
        if (lease is null) return invoke();
        return Leased();
        async Task<T> Leased() { using (lease) return await invoke(); }
    }
    internal Task InvokeCallbackAsync(Delegate callback, Func<Task> invoke, Facet? declaringFacet = null)
    {
        var lease = RetainCallback(callback, declaringFacet);
        if (lease is null) return invoke();
        return Leased();
        async Task Leased() { using (lease) await invoke(); }
    }
    private sealed class CallbackLifetimeRegistration(Followers<CallbackLifetimeRegistration> registrations,
        Func<Delegate, Facet?, IDisposable?> acquire) : IDisposable
    {
        internal IDisposable? Acquire(Delegate callback, Facet? declaringFacet) => acquire(callback, declaringFacet);
        public void Dispose() => registrations.Remove(this);
    }
    private sealed class CallbackLeases(IDisposable[] initial) : IDisposable
    {
        private IDisposable[]? leases = initial;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref leases, null) is not { } owned) return;
            List<Exception>? failures = null;
            foreach (var lease in owned.Reverse())
                try { lease.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
            if (failures is not null) throw new AggregateException(failures);
        }
    }
}
