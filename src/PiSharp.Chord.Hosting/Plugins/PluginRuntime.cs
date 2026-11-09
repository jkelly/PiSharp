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
    private readonly IDisposable callbackRegistration;
    public PluginRuntime(ChordHost host, PluginRuntimeOptions? options = null)
    { this.host = host; this.options = options ?? new(); callbackRegistration = host.RegisterCallbackLifetime((callback, facet) => PluginCallbacks.Acquire(callback, facet)); }
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
                return new(failed.Concat(report.Failed).ToArray());
            }
            catch { foreach (var generation in next.Values) _ = Retire(generation); throw; }
        }
        finally { gate.Release(); }
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
        var context = new PluginLoadContext(build.AssemblyPath!, options.SharedAssemblies);
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
        return generation.Retire();
    }
    public ValueTask DisposeAsync() { lock (disposalSync) return new(disposal ??= DisposeCoreAsync()); }
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
    internal PluginLoadContext(string assembly, IReadOnlyList<Assembly> hostAssemblies) : base($"chord:{Path.GetFileNameWithoutExtension(assembly)}:{Guid.NewGuid():N}", true)
    {
        resolver = new(assembly);
        directory = Path.GetDirectoryName(Path.GetFullPath(assembly))!;
        shared = new[] { typeof(Facet).Assembly, typeof(IChordPlugin).Assembly, typeof(PiSharp.Contracts.Schema.JsonSchemaBuilder).Assembly, typeof(PiSharp.Telemetry.ITelemetryContext).Assembly }
            .Concat(hostAssemblies).Distinct().ToDictionary(x => x.GetName().Name!, StringComparer.OrdinalIgnoreCase);
    }
    protected override Assembly? Load(AssemblyName name)
    {
        if (shared.TryGetValue(name.Name!, out var assembly))
        {
            if (!string.Equals(name.FullName, assembly.GetName().FullName, StringComparison.Ordinal)) throw new FileLoadException($"shared assembly identity mismatch: {name}");
            return assembly;
        }
        var path = resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is { } simple) { var local = Path.Combine(directory, simple + ".dll"); if (File.Exists(local)) path = local; }
        if (path is not null) return LoadFromAssemblyPath(path);
        if (name.Name is { } frameworkName && frameworkDirectories.Any(root => File.Exists(Path.Combine(root, frameworkName + ".dll")))) return null;
        throw new FileNotFoundException($"Private dependency {name.Name} was not included in the plugin build or explicitly shared by its host");
    }
    protected override nint LoadUnmanagedDll(string name) => resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}
