using System.Reflection;
using System.Runtime.Loader;

namespace PiSharp.Chord.Legacy;

/// <summary>The native equivalent of a legacy facet module's default export.</summary>
public interface ILegacyFacetModule { LegacyFacet CreateFacet(); }

/// <summary>Explicit assembly paths, one file identity per generation, and collectible code after kernel retirement.</summary>
public sealed class LegacyFacetAssemblyLoader : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<LegacyFacetGeneration> generations = [];
    private bool disposed;
    public Task<LegacyFacetGeneration> LoadAsync(IReadOnlyList<string> assemblyPaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        // Resolve all entry paths and register all directories before evaluating any module.
        var paths = assemblyPaths.Select(CanonicalPath).Distinct(PathComparer).ToArray();
        var context = new FacetContext(paths);
        try
        {
            var facets = new List<LegacyFacet>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assembly = context.LoadModule(path);
                var modules = assembly.GetTypes().Where(type => type.IsPublic && !type.IsAbstract && typeof(ILegacyFacetModule).IsAssignableFrom(type)).ToArray();
                if (modules.Length == 0) throw new ChordException("invalid", "Assembly exports no ILegacyFacetModule: " + path, "module");
                foreach (var type in modules)
                {
                    if (Activator.CreateInstance(type) is not ILegacyFacetModule module) throw new InvalidOperationException("Facet module needs a public parameterless constructor: " + type.FullName);
                    facets.Add(module.CreateFacet() ?? throw new InvalidOperationException("Facet module returned null: " + type.FullName));
                }
            }
            if (facets.GroupBy(facet => facet.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) throw new ArgumentException("Duplicate legacy facet names in a generation.");
            var generation = new LegacyFacetGeneration(context, facets.ToArray(), Forget);
            lock (gate)
            {
                if (disposed) { generation.Dispose(); throw new ObjectDisposedException(nameof(LegacyFacetAssemblyLoader)); }
                generations.Add(generation);
            }
            return Task.FromResult(generation);
        }
        catch { context.Unload(); throw; }
    }
    public async Task<LegacyFacetGeneration> LoadIntoAsync(LegacyHost host, IReadOnlyList<string> assemblyPaths, CancellationToken cancellationToken = default)
    {
        var generation = await LoadAsync(assemblyPaths, cancellationToken).ConfigureAwait(false);
        try { await host.LoadAsync(generation.Facets).ConfigureAwait(false); generation.Bind(host, host.CodeVersion); return generation; }
        catch { generation.Dispose(); throw; }
    }
    public async Task<LegacyReloadReport> ReloadIntoAsync(LegacyHost host, IReadOnlyList<string> assemblyPaths,
        LegacyReloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var generation = await LoadAsync(assemblyPaths, cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await host.ReloadAsync(generation.Facets, options).ConfigureAwait(false);
            generation.Bind(host, report.CodeVersion); return report;
        }
        catch { generation.Dispose(); throw; }
    }
    private void Forget(LegacyFacetGeneration generation) { lock (gate) generations.Remove(generation); }
    public void Dispose()
    {
        LegacyFacetGeneration[] current;
        lock (gate) { if (disposed) return; disposed = true; current = generations.ToArray(); generations.Clear(); }
        foreach (var generation in current) generation.Dispose();
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var next = Path.Combine(current, parts[index]);
            FileSystemInfo info = index == parts.Length - 1 ? new FileInfo(next) : new DirectoryInfo(next);
            if (!info.Exists) throw new FileNotFoundException("Legacy facet assembly does not exist.", full);
            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
        }
        return current;
    }
    private sealed class FacetContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver[] resolvers;
        private readonly Dictionary<string, string> modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Assembly> loaded = new(PathComparer);
        private readonly object loadGate = new();
        internal FacetContext(string[] paths) : base("legacy-facets-" + Guid.NewGuid().ToString("N"), isCollectible: true)
        {
            // Assembly wrappers hold their loader allocator. Drop the resolver's managed
            // cache as soon as unloading starts, including while a generation wrapper is retained.
            Unloading += _ => { lock (loadGate) loaded.Clear(); };
            resolvers = paths.Select(path => new AssemblyDependencyResolver(path)).ToArray();
            foreach (var path in paths)
            {
                var name = AssemblyName.GetAssemblyName(path).Name!;
                if (modules.TryGetValue(name, out var other) && !PathComparer.Equals(path, other))
                    throw new ArgumentException("Two legacy entry assemblies use the same identity: " + name);
                modules[name] = path;
            }
        }
        internal Assembly LoadModule(string path)
        {
            lock (loadGate)
            {
                if (loaded.TryGetValue(path, out var assembly)) return assembly;
                var identity = AssemblyName.GetAssemblyName(path);
                assembly = Assemblies.FirstOrDefault(candidate => candidate.FullName == identity.FullName);
                // Stream loading leaves build outputs replaceable while old roots retain their code generation.
                if (assembly is null) { using var file = File.OpenRead(path); assembly = LoadFromStream(file); }
                loaded[path] = assembly; return assembly;
            }
        }
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "PiSharp.Chord" or "PiSharp.Contracts" or "PiSharp.Telemetry")
                return Default.LoadFromAssemblyName(assemblyName);
            if (modules.TryGetValue(assemblyName.Name!, out var own)) return LoadModule(own);
            foreach (var resolver in resolvers)
                if (resolver.ResolveAssemblyToPath(assemblyName) is { } path) return LoadModule(CanonicalPath(path));
            return null;
        }
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            foreach (var resolver in resolvers)
                if (resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path) return LoadUnmanagedDllFromPath(path);
            return IntPtr.Zero;
        }
    }
}

/// <summary>Owns module code. Bind to the exact committed code version; dispose after host close.</summary>
public sealed class LegacyFacetGeneration : IDisposable
{
    private readonly object gate = new();
    private AssemblyLoadContext? context;
    private IReadOnlyList<LegacyFacet> facets;
    private IReadOnlyList<LegacyFacet> ownedFacets;
    private Action<LegacyFacetGeneration>? forget;
    private ILegacyReloadHost? host;
    private int version;
    private bool disposalRequested;
    internal LegacyFacetGeneration(AssemblyLoadContext context, IReadOnlyList<LegacyFacet> facets, Action<LegacyFacetGeneration> forget)
    { this.context = context; this.facets = facets; ownedFacets = facets; this.forget = forget; LoadContext = new(context, trackResurrection: true); }
    public WeakReference LoadContext { get; }
    public IReadOnlyList<LegacyFacet> Facets { get { lock (gate) { ObjectDisposedException.ThrowIf(context is null || disposalRequested, this); return facets; } } }
    public void Bind(ILegacyReloadHost host, int codeVersion)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(context is null || disposalRequested, this);
            if (this.host is not null) throw new InvalidOperationException("A facet generation already belongs to a host version.");
            this.host = host; version = codeVersion; host.Retired += Retired;
        }
        if (host.HighestRetiredCodeVersion >= codeVersion) Release(retired: true);
    }
    private void Retired(int codeVersion) { if (codeVersion >= version) Release(retired: true); }
    /// <summary>Releases this owner's facet handles. Bound code unloads only after kernel retirement finishes.</summary>
    public void Dispose() => Release(retired: false);
    private void Release(bool retired)
    {
        AssemblyLoadContext? releasing;
        Action<LegacyFacetGeneration>? callback;
        ILegacyReloadHost? boundHost;
        IReadOnlyList<LegacyFacet> originals;
        lock (gate) { boundHost = host; originals = ownedFacets; }
        // A host probe may marshal to its dispatcher. Do not hold the generation lock:
        // retirement notifications run on that dispatcher and also need this lock.
        var installed = boundHost is ILegacyFacetLifetimeHost lifetime && originals.Any(lifetime.IsFacetInstalled);
        var installedVersion = retired && installed ? boundHost!.CodeVersion : 0;
        lock (gate)
        {
            releasing = context; if (releasing is null) return;
            if (retired && installed)
            {
                // An unchanged facet moved into a newer code version. Its original numeric
                // retirement is no longer sufficient evidence that this module stopped serving.
                version = Math.Max(version, installedVersion); return;
            }
            disposalRequested = true; facets = [];
            callback = forget; forget = null;
            if (host is not null && !retired)
            {
                // Host.Close may return at its deadline while a resource Close still runs.
                // Keep its loader available for late dependency resolution until true retirement.
                releasing = null;
            }
            else
            {
                context = null; ownedFacets = [];
                if (host is not null) { host.Retired -= Retired; host = null; }
            }
        }
        callback?.Invoke(this); releasing?.Unload();
    }
}
