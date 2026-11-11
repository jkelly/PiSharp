using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using Generation = PiSharp.Chord.Hosting.Plugins.PluginGeneration;

namespace PiSharp.Chord.Hosting.Plugins;

/// <summary>The assembly entry point. Setup returns declarations; host factories open resources at activation.</summary>
public interface IChordPlugin { Facet CreateFacet(PluginContext context); }
public sealed class PluginContext(string name, string directory, string kind = "server")
{
    public string Name { get; } = name;
    public string Directory { get; } = directory;
    public string Kind { get; } = kind;
    public string File(string relative) => PluginScanner.ContainedPath(Directory, relative);
}
public sealed class PluginRuntimeOptions
{
    public string Kind { get; init; } = "server";
    public PluginManagerOptions? Policy { get; init; }
    /// <summary>A bounded wait for plugin callbacks at retirement; timeout never forces an executing context to unload.</summary>
    public TimeSpan CallbackDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Explicit host contract libraries. Their exact loaded identities are shared across all generations.</summary>
    public IReadOnlyList<Assembly> SharedAssemblies { get; init; } = [];
}

/// <summary>Owns collectible generations for one host. Dispose only after closing that host.</summary>
public sealed class PluginRuntime : IAsyncDisposable
{
    private readonly ChordHost host;
    private readonly PluginRuntimeOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Generation> loaded = new(StringComparer.Ordinal);
    private readonly List<WeakReference> retired = [];
    private readonly List<Generation> retiring = [];
    private IReadOnlyList<PluginFacet> core = [];
    private bool initialized, disposed;
    private readonly object disposalSync = new();
    private Task? disposal;
    private readonly HashSet<Task> originalDrains = [];
    private readonly IDisposable callbackRegistration;
    public PluginRuntime(ChordHost host, PluginRuntimeOptions? options = null)
    { this.host = host; this.options = options ?? new(); if (this.options.Policy is { } policy && policy.Kind != this.options.Kind) throw new ArgumentException("runtime kind must match plugin policy"); callbackRegistration = host.RegisterCallbackLifetime((callback, facet) => PluginCallbacks.Acquire(callback, facet)); }
    public IReadOnlyList<WeakReference> RetiredContexts => retired.ToArray();

    public async Task<LoadReport> LoadAsync(PluginBuildSet builds, IReadOnlyList<PluginFacet> coreFacets, ChordContext context)
    {
        await gate.WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) throw new InvalidOperationException("plugin runtime already loaded");
            var (next, failed) = Prepare(builds, false);
            try
            {
                var report = await host.LoadAsync(coreFacets.Concat(next.Select(x => new PluginFacet(x.Key, x.Value.Facet!))).ToArray(), context).ConfigureAwait(false);
                var rejected = report.Failed.Select(x => x.Plugin).ToHashSet(StringComparer.Ordinal);
                foreach (var (name, generation) in next) { if (!rejected.Contains(name)) loaded.Add(name, generation); else _ = Retire(generation); }
                core = coreFacets.ToArray(); initialized = true;
                var all = failed.Concat(report.Failed).ToArray();
                var required = builds.Problems.Where(p => p.Required).Select(p => p.Error).Concat(all.Where(f => builds.Plugins.Any(p => p.Plugin.Required && p.Plugin.Manifest.Name == f.Plugin)).Select(f => f.Error.Message)).ToArray();
                if (required.Length != 0) throw new PluginError("required plugins failed: " + string.Join("; ", required));
                return new(all);
            }
            catch { foreach (var generation in next.Values.Where(g => !loaded.Values.Contains(g))) _ = Retire(generation); throw; }
        }
        finally { gate.Release(); }
    }

    internal async Task SetCoreForTestingAsync(IReadOnlyList<PluginFacet> facets, PluginBuildSet builds, ChordContext context)
    {
        core = facets;
        var report = await ReloadAsync(builds, context).ConfigureAwait(false);
        if (report.Failed.Count != 0) throw new PluginError("test setup failed: " + string.Join("; ", report.Failed.Select(f => f.Error.Message)));
        await DrainRetiredAsync(context).ConfigureAwait(false);
    }

    public async Task<ReloadReport> ReloadAsync(PluginBuildSet builds, ChordContext context)
    {
        await gate.WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized) throw new InvalidOperationException("plugin runtime has not loaded its host");
            var (next, failed) = Prepare(builds, true);
            try
            {
                var report = await host.ReloadAsync(core.Concat(next.Select(x => new PluginFacet(x.Key, x.Value.Facet!))).ToArray(), context, new(failed)).ConfigureAwait(false);
                Adopt(next, report.Plugins.Failed.ToHashSet(StringComparer.Ordinal));
                return report;
            }
            catch (ChordError error) when (error.Details?["committed"] is JsonObject committed)
            {
                Adopt(next, committed["failed"]?.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? []);
                throw;
            }
            catch
            {
                foreach (var generation in next.Values.Where(g => !loaded.Values.Contains(g))) _ = Retire(generation);
                throw;
            }
        }
        finally { gate.Release(); }
    }
    private (Dictionary<string, Generation> Next, List<PluginFailure> Failed) Prepare(PluginBuildSet builds, bool reuse)
    {
        var next = new Dictionary<string, Generation>(StringComparer.Ordinal); var failed = new List<PluginFailure>();
        foreach (var build in builds.Plugins)
        {
            var name = build.Plugin.Manifest.Name;
            if (next.ContainsKey(name)) throw new ArgumentException($"duplicate plugin {name}");
            loaded.TryGetValue(name, out var old);
            if (reuse && old is not null && old.Digest == build.Digest && build.Error is null) { next.Add(name, old); continue; }
            try
            {
                if (build.Error is { } error) throw new InvalidDataException(error);
                if (build.Plugin.Manifest.Type is null) continue;
                next.Add(name, Evaluate(build));
            }
            catch (Exception error)
            {
                // Never retain a plugin exception/type in the manager: an error could root its collectible context.
                failed.Add(new(name, new InvalidDataException($"{name}: {error.GetType().Name}: {error.Message}")));
                if (reuse && old is not null) next.Add(name, old);
            }
        }
        return (next, failed);
    }
    private Generation Evaluate(BuiltPlugin build)
    {
        PluginPolicy.ValidateManifest(build.Plugin, options.Policy);
        PluginPolicy.ValidateAssemblies(Path.GetDirectoryName(build.AssemblyPath!)!, options.Policy);
        var context = new PluginLoadContext(build.AssemblyPath!, options.SharedAssemblies.Concat(PluginPolicy.Shared(options.Policy)).Distinct().ToArray(), options.Policy);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(build.AssemblyPath!));
            var type = assembly.GetType(build.Plugin.Manifest.Type!, true)!;
            if (!typeof(IChordPlugin).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidDataException("entry type must implement IChordPlugin");
            var plugin = (IChordPlugin)(Activator.CreateInstance(type) ?? throw new InvalidDataException("entry constructor returned null"));
            var declared = plugin.CreateFacet(new(build.Plugin.Manifest.Name, build.Plugin.Directory, options.Kind)) ?? throw new InvalidDataException("entry returned a null facet");
            // A shared library may cache its declaration object. Keep its actual setup delegate unchanged, while assigning
            // an independent weak owner identity to each evaluated generation. Never key a generation by a shared static facet.
            var facet = Facet.Define(declared.Setup);
            return new(context, facet, build.Digest!);
        }
        catch { context.Unload(); throw; }
    }
    private void Adopt(Dictionary<string, Generation> next, HashSet<string> failed)
    {
        foreach (var name in failed)
        {
            if (next.Remove(name, out var refused) && (!loaded.TryGetValue(name, out var previous) || !ReferenceEquals(refused, previous))) _ = Retire(refused);
            if (loaded.TryGetValue(name, out var old)) next[name] = old;
        }
        foreach (var old in loaded.Values.Where(g => !next.Values.Contains(g))) retiring.Add(old);
        loaded.Clear(); foreach (var entry in next) loaded.Add(entry.Key, entry.Value);
    }
    /// <summary>Drains retired host calls/resources before releasing generation handles and requesting collection.</summary>
    public async Task DrainRetiredAsync(ChordContext context)
    {
        await gate.WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        try
        {
            await host.DrainReloadsAsync(context).ConfigureAwait(false);
            await Task.WhenAll(retiring.Select(Retire)).WaitAsync(options.CallbackDrainTimeout, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
            retiring.Clear();
        }
        finally { gate.Release(); }
    }
    private Task Retire(Generation generation)
    {
        if (!generation.Recorded) { generation.Recorded = true; retired.Add(new(generation.Context)); }
        var drain = generation.Retire();
        lock (disposalSync) originalDrains.Add(drain);
        return drain;
    }
    public ValueTask DisposeAsync() { lock (disposalSync) return new(disposal ??= DisposeCoreAsync()); }
    /// <summary>Starts disposal and joins every actual generation callback, beyond the public disposal timeout.
    /// Close the host first; stores and other callback resources must remain open until this settles.</summary>
    public async Task JoinOriginalsAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (TimeoutException) { /* Public timeout does not end ownership of actual callbacks. */ }
        Task[] drains; lock (disposalSync) drains = originalDrains.ToArray();
        await Task.WhenAll(drains).ConfigureAwait(false);
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return; disposed = true; core = [];
            var drains = loaded.Values.Concat(retiring).Select(Retire).ToArray(); loaded.Clear(); retiring.Clear();
            await Task.WhenAll(drains).WaitAsync(options.CallbackDrainTimeout).ConfigureAwait(false);
        }
        finally { callbackRegistration.Dispose(); gate.Release(); }
    }
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;
    private readonly string directory;
    private readonly string[] frameworkDirectories = [Path.GetDirectoryName(typeof(object).Assembly.Location)!, Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.Location)!];
    private readonly Dictionary<string, Assembly> shared;
    private readonly PluginManagerOptions? policy;
    internal PluginLoadContext(string assembly, IReadOnlyList<Assembly> hostAssemblies, PluginManagerOptions? policy = null) : base($"chord:{Path.GetFileNameWithoutExtension(assembly)}:{Guid.NewGuid():N}", true)
    {
        this.policy = policy;
        resolver = new(assembly);
        directory = Path.GetDirectoryName(Path.GetFullPath(assembly))!;
        shared = new[] { typeof(Facet).Assembly, typeof(IChordPlugin).Assembly, typeof(PiSharp.Contracts.Schema.JsonSchemaBuilder).Assembly, typeof(PiSharp.Telemetry.ITelemetryContext).Assembly }
            .Concat(hostAssemblies).Distinct().ToDictionary(x => x.GetName().Name!, StringComparer.OrdinalIgnoreCase);
    }
    protected override Assembly? Load(AssemblyName name)
    {
        if (policy?.AppApi is not null && name.Name!.StartsWith("PiSharp.Chord", StringComparison.OrdinalIgnoreCase) && !PluginPolicy.Shared(policy).Any(a => a.GetName().Name == name.Name))
            throw new FileLoadException($"Direct Chord dependency {name.Name} is not admitted");
        if (shared.TryGetValue(name.Name!, out var assembly))
        {
            if (!string.Equals(name.FullName, assembly.GetName().FullName, StringComparison.Ordinal)) throw new FileLoadException($"shared assembly identity mismatch: {name}");
            return assembly;
        }
        var path = resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is { } simple) { var local = Path.Combine(directory, simple + ".dll"); if (File.Exists(local)) path = local; }
        if (path is not null)
        {
            if (!PluginScanner.Inside(directory, Path.GetFullPath(path))) throw new FileLoadException($"Private dependency {name.Name} resolves outside its immutable plugin artifact");
            return LoadFromAssemblyPath(path);
        }
        if (name.Name is { } frameworkName && frameworkDirectories.Any(root => File.Exists(Path.Combine(root, frameworkName + ".dll")))) return null;
        throw new FileNotFoundException($"Private dependency {name.Name} was not included in the plugin build or explicitly shared by its host");
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        if (resolver.ResolveUnmanagedDllToPath(name) is not { } path) return 0;
        if (!PluginScanner.Inside(directory, Path.GetFullPath(path))) throw new FileLoadException($"Native dependency {name} resolves outside its immutable plugin artifact");
        return LoadUnmanagedDllFromPath(path);
    }
}
