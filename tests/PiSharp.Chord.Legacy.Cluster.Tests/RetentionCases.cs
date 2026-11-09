using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Reflection;
using PiSharp.Chord.Legacy;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RetentionCases()
    {
        foreach (var test in ActualRemoteRetentionCases()) yield return test;
        yield return ("retention.shared-bindings-lru-and-reacquire", async () =>
        {
            var closed = new List<string>(); var created = 0;
            using var pool = new LegacyBindingRetention<string, Binding>(key => { created++; return new(key); }, item => closed.Add(item.Name), item => item.Hydrated,
                _ => TimeSpan.FromMilliseconds(200), capacity: 2);
            var a = pool.Acquire("a"); var second = pool.Acquire("a"); Check(ReferenceEquals(a.Value, second.Value) && created == 1, "Binding was not shared.");
            a.Dispose(); Check(pool.Lingering == 0, "Active reference lingered."); second.Dispose();
            var reused = pool.Acquire("a"); Check(created == 1, "Reacquire opened another binding."); reused.Dispose();
            pool.Acquire("b").Dispose(); pool.Acquire("c").Dispose();
            Check(closed.SequenceEqual(["a"]) && pool.Lingering == 2, "LRU capacity did not evict oldest release.");
            await Eventually(() => pool.Count == 0); Check(closed.Order().SequenceEqual(new[] { "a", "b", "c" }), "Expiry leaked or double-closed bindings.");
        });
        yield return ("retention.unhydrated-release-and-released-reference-gc", () =>
        {
            using var pool = new LegacyBindingRetention<string, Binding>(key => new(key) { Hydrated = false }, _ => { }, item => item.Hydrated, _ => TimeSpan.FromHours(1));
            var (reference, weak) = Released(pool);
            Check(pool.Count == 0 && pool.Lingering == 0, "Unhydrated binding retained.");
            Collect(weak); GC.KeepAlive(reference); return Task.CompletedTask;
        });
        yield return ("retention.collectible-assembly-shared-identity-and-alias", () =>
        {
            using var loader = new LegacyFacetAssemblyLoader();
            var weak = LoadAndRetire(loader); Collect(weak); return Task.CompletedTask;
        });
        yield return ("retention.actual-host-pins-module-until-root-retires", async () =>
        {
            using var loader = new LegacyFacetAssemblyLoader();
            await using var host = new LegacyHost(new() { Name = "loader", Self = new object() });
            var generation = await loader.LoadIntoAsync(host, [Assembly.GetExecutingAssembly().Location]);
            var weak = generation.LoadContext;
            await Throws<FileNotFoundException>(() => loader.ReloadIntoAsync(host, [Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")]));
            Check(host.CodeVersion == 1 && generation.Facets.Count == 1 && weak.IsAlive, "Failed module evaluation replaced the installed generation.");
            await host.ReloadAsync([new("unrelated", _ => { })]);
            await Eventually(() => host.HighestRetiredCodeVersion >= 1);
            Check(generation.Facets.Count == 1 && weak.IsAlive, "Unchanged installed module unloaded after an unrelated code version retired.");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var root = host.RootAsync(async _ => { entered.SetResult(); await leave.Task; return true; });
            await entered.Task;
            await loader.ReloadIntoAsync(host, [Assembly.GetExecutingAssembly().Location]);
            Check(host.PreviousCodeVersion.HasValue && weak.IsAlive, "Old module retired while root used its version.");
            leave.SetResult(); await root; await Eventually(() => !host.PreviousCodeVersion.HasValue);
            Collect(weak); GC.KeepAlive(generation);
        });
        yield return ("retention.old-resource-close-loads-private-dependency-before-unload", async () =>
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dependency = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var loader = new LegacyFacetAssemblyLoader();
            await using var host = new LegacyHost(new() { Name = "retire-close", Self = "host" });
            var retired = 0; host.Retired += _ => Interlocked.Increment(ref retired);
            AppDomain.CurrentDomain.SetData(TestLegacyFacetModule.ModeKey, "old-resource");
            AppDomain.CurrentDomain.SetData(TestLegacyFacetModule.BridgeKey, (Func<string, Task>)(async message =>
            {
                if (message.StartsWith("close-entered:", StringComparison.Ordinal)) { entered.TrySetResult(message.EndsWith("True", StringComparison.Ordinal)); await allowLoad.Task; }
                else if (message.StartsWith("dependency:", StringComparison.Ordinal)) { dependency.TrySetResult(message); await finishClose.Task; }
            }));
            try
            {
                var generation = await loader.LoadIntoAsync(host, [Assembly.GetExecutingAssembly().Location]);
                var weak = generation.LoadContext;
                AppDomain.CurrentDomain.SetData(TestLegacyFacetModule.ModeKey, "replacement");
                await loader.ReloadIntoAsync(host, [Assembly.GetExecutingAssembly().Location]);
                Check(!await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)), "Fixture private dependency was loaded before Close blocked.");
                Check(retired == 0 && generation.Facets.Count == 1, "Old module retired before its resource Close drained.");
                loader.Dispose(); // Logical disposal must defer the actual Unload while Close owns old code.
                allowLoad.TrySetResult();
                var loaded = await dependency.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(loaded.StartsWith("dependency:private-close-loaded|legacy-facets-", StringComparison.Ordinal), "Close did not first-load its dependency into its own collectible context: " + loaded);
                Check(retired == 0 && weak.IsAlive, "Module retired while resource Close still ran after dependency loading.");
                finishClose.TrySetResult();
                await Eventually(() => retired == 1 && !host.PreviousCodeVersion.HasValue);
                Collect(weak); GC.KeepAlive(generation);
            }
            finally
            {
                allowLoad.TrySetResult(); finishClose.TrySetResult();
                AppDomain.CurrentDomain.SetData(TestLegacyFacetModule.ModeKey, null);
                AppDomain.CurrentDomain.SetData(TestLegacyFacetModule.BridgeKey, null);
            }
        });
    }
    private sealed class Binding(string name) { public string Name { get; } = name; public bool Hydrated { get; init; } = true; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IDisposable Lease, WeakReference Weak) Released(LegacyBindingRetention<string, Binding> pool)
    { var lease = pool.Acquire("ephemeral"); var weak = new WeakReference(lease.Value); lease.Dispose(); return (lease, weak); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndRetire(LegacyFacetAssemblyLoader loader)
    {
        var path = Assembly.GetExecutingAssembly().Location;
        var generation = loader.LoadAsync([path, Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path))]).GetAwaiter().GetResult();
        Check(generation.Facets.Count == 1, "Same file was evaluated twice.");
        Check(AssemblyLoadContext.GetLoadContext(generation.Facets[0].Setup.Method.Module.Assembly)?.IsCollectible == true, "Facet did not come from collectible assembly.");
        var weak = generation.LoadContext; generation.Dispose(); return weak;
    }
    private static void Collect(WeakReference weak)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        // Match the baseline's five-second finalization allowance: collectibility is the
        // assertion, not a scheduling assumption about when the finalizer thread runs.
        while (weak.IsAlive && deadline.Elapsed < TimeSpan.FromSeconds(5))
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Check(!weak.IsAlive, "Retired code/binding remained strongly reachable.");
    }
}

public sealed class TestLegacyFacetModule : ILegacyFacetModule
{
    public const string ModeKey = "PiSharp.Legacy.LoaderFixture.Mode";
    public const string BridgeKey = "PiSharp.Legacy.LoaderFixture.Bridge";
    public LegacyFacet CreateFacet()
    {
        var mode = AppDomain.CurrentDomain.GetData(ModeKey) as string;
        if (mode is not ("old-resource" or "replacement")) return new("collectible-fixture", _ => { });
        var bridge = (Func<string, Task>)AppDomain.CurrentDomain.GetData(BridgeKey)!;
        var contract = LegacyContract.Local("loader.resource", 1, PiSharp.Chord.Contracts.Schemas.Obj());
        return new("collectible-fixture", api => api.Serve(contract, (_, _, self) =>
        {
            if (mode == "old-resource") self.Resource("old-only", new LegacyResourceDeclaration<object>
            {
                Open = _ => Task.FromResult(new object()),
                Close = async _ =>
                {
                    var context = AssemblyLoadContext.GetLoadContext(typeof(TestLegacyFacetModule).Assembly)!;
                    var alreadyLoaded = context.Assemblies.Any(assembly => assembly.GetName().Name == "PiSharp.Chord.Legacy.LoaderDependency");
                    await bridge("close-entered:" + alreadyLoaded);
                    await bridge("dependency:" + LoadCloseDependency());
                },
            });
            return new(new Dictionary<string, object?>());
        }, new() { Lifetime = "host" }));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LoadCloseDependency()
    {
        var value = PiSharp.Chord.Legacy.LoaderDependency.CloseDependency.Read();
        var context = AssemblyLoadContext.GetLoadContext(typeof(PiSharp.Chord.Legacy.LoaderDependency.CloseDependency).Assembly);
        return value + "|" + context?.Name;
    }
}
