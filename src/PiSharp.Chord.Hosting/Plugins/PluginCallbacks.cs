using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Hosting.Plugins;

// Callback ownership is separate from a request's cancellable wait. An abandoned wait must not unload code still executing.
internal static class PluginCallbacks
{
    private static readonly ConditionalWeakTable<AssemblyLoadContext, PluginGeneration> generations = new();
    private static readonly ConditionalWeakTable<Facet, PluginGeneration> facets = new();
    internal static void Register(PluginGeneration generation) { generations.Add(generation.Context, generation); facets.Add(generation.Facet!, generation); }
    private static PluginGeneration? Owner(Delegate callback)
    {
        if (callback.Target is { } target && AssemblyLoadContext.GetLoadContext(target.GetType().Assembly) is { } targetContext && generations.TryGetValue(targetContext, out var targetOwner)) return targetOwner;
        return AssemblyLoadContext.GetLoadContext(callback.Method.Module.Assembly) is { } context && generations.TryGetValue(context, out var owner) ? owner : null;
    }
    internal static IDisposable? Acquire(Delegate callback, Facet? declaringFacet)
    {
        if (Owner(callback) is null && (declaringFacet is null || !facets.TryGetValue(declaringFacet, out _))) return null;
        return Retain([callback], declaringFacet is null ? [] : [declaringFacet]);
    }
    internal static IDisposable Retain(IEnumerable<Delegate> callbacks, IEnumerable<object?>? declaringOwners = null)
    {
        var owners = new HashSet<PluginGeneration>(ReferenceEqualityComparer.Instance); var leases = new List<IDisposable>();
        try
        {
            foreach (var callback in callbacks) if (Owner(callback) is { } owner && owners.Add(owner)) leases.Add(owner.Acquire());
            foreach (var facet in (declaringOwners ?? []).OfType<Facet>()) if (facets.TryGetValue(facet, out var owner) && owners.Add(owner)) leases.Add(owner.Acquire());
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
        return new CallbackLeases(leases);
    }
    internal static async Task<T> InvokeAsync<T>(Delegate callback, Func<Task<T>> invoke, IEnumerable<Delegate>? dependencies = null, IEnumerable<object?>? declaringOwners = null)
    { using var lease = Retain((dependencies ?? []).Append(callback), declaringOwners); return await invoke().ConfigureAwait(false); }
    internal static async Task InvokeAsync(Delegate callback, Func<Task> invoke, IEnumerable<Delegate>? dependencies = null, IEnumerable<object?>? declaringOwners = null)
    { using var lease = Retain((dependencies ?? []).Append(callback), declaringOwners); await invoke().ConfigureAwait(false); }
    internal static async Task<T> InvokeWithDependenciesAsync<T>(IEnumerable<Delegate> dependencies, Func<Task<T>> invoke, IEnumerable<object?>? declaringOwners = null)
    { using var lease = Retain(dependencies, declaringOwners); return await invoke().ConfigureAwait(false); }
    private sealed class CallbackLeases(List<IDisposable> leases) : IDisposable
    { public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); } }
}

internal sealed class PluginGeneration
{
    private readonly object sync = new();
    private readonly TaskCompletionSource unloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int leases;
    private bool retired, unloading;
    internal PluginLoadContext Context { get; }
    internal Facet? Facet { get; set; }
    internal string Digest { get; }
    internal bool Recorded;
    internal PluginGeneration(PluginLoadContext context, Facet facet, string digest)
    { Context = context; Facet = facet; Digest = digest; PluginCallbacks.Register(this); }
    internal IDisposable Acquire()
    {
        lock (sync)
        {
            if (unloading) throw new ChordError("released", "plugin generation has retired", "retired");
            leases++; return new Lease(this);
        }
    }
    internal Task Retire()
    {
        bool unload;
        lock (sync) { retired = true; Facet = null; unload = leases == 0 && !unloading; if (unload) unloading = true; }
        if (unload) Unload();
        return unloaded.Task;
    }
    private void Release()
    {
        bool unload;
        lock (sync) { leases--; unload = retired && leases == 0 && !unloading; if (unload) unloading = true; }
        if (unload) Unload();
    }
    private void Unload()
    { try { Context.Unload(); unloaded.TrySetResult(); } catch (Exception error) { unloaded.TrySetException(error); } }
    private sealed class Lease(PluginGeneration generation) : IDisposable
    {
        private PluginGeneration? owner = generation;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
