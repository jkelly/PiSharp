namespace PiSharp.Chord.Hosting.Plugins;

public sealed record PluginDirtyState(IReadOnlyDictionary<string, long> Dirty, bool SetChanged, bool BaseChanged, bool InstallChanged = false);
public sealed record PluginRolloutHost(string Host, bool Stopped = false, string? Error = null, IReadOnlyList<string>? FailedPlugins = null);
public sealed record PluginRolloutStepReport(string Step, IReadOnlyList<PluginRolloutHost> Hosts)
{ public bool Stopped => Hosts.Any(host => host.Stopped); }
public interface IPluginRolloutStep
{
    string Name { get; }
    Task<PluginRolloutStepReport> RolloutAsync(PluginBuildSet build, ChordContext context);
}

/// <summary>Discovery/build/watch orchestration. Watches mark changes; only an explicit rollout changes running code.</summary>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly PluginRoot[] roots;
    private readonly List<BaseInputs> bases = [];
    private readonly List<BaseInputs> pluginInputs = [];
    private bool installChanged;
    private readonly PluginBuilder builder;
    private readonly object sync = new();
    private readonly Dictionary<string, long> dirty = new(StringComparer.Ordinal);
    private readonly Dictionary<PluginBuildSet, Dictionary<string, long>> marks = new(ReferenceEqualityComparer.Instance);
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly HashSet<string> watched = new(BaseInputs.Paths);
    private NativePlugin[] plugins = [];
    private long generation;
    private bool setChanged, disposed;
    public PluginManager(IEnumerable<string> roots, PluginBuilder builder, Action<Exception>? onWatchError = null, IEnumerable<string>? baseDirectories = null)
        : this(roots.Select(root => new PluginRoot(root)), builder, onWatchError, baseDirectories) { }
    public PluginManager(IEnumerable<PluginRoot> roots, PluginBuilder builder, Action<Exception>? onWatchError = null, IEnumerable<string>? baseDirectories = null)
    {
        this.roots = roots.Select(root => root with { Path = Path.GetFullPath(root.Path) }).ToArray(); this.builder = builder;
        foreach (var root in this.roots) Watch(root.Path, false, onWatchError);
        foreach (var root in baseDirectories ?? []) WatchBase(BaseInputs.Capture([], [root]));
    }
    /// <summary>Tracks the hashes of bytes the artifact actually read. Identical restoration clears the base mark.</summary>
    public IDisposable WatchBase(BaseInputs inputs)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); bases.Add(inputs);
            foreach (var directory in inputs.Directories.Concat(inputs.Files.Keys.Select(Path.GetDirectoryName).OfType<string>()).Distinct(BaseInputs.Paths)) Watch(directory, true, null);
        }
        Changed?.Invoke(State);
        return new BaseRegistration(this, inputs);
    }
    private sealed class BaseRegistration(PluginManager owner, BaseInputs inputs) : IDisposable
    {
        public void Dispose() { lock (owner.sync) owner.bases.Remove(inputs); }
    }
    public event Action<PluginDirtyState>? Changed;
    public PluginDirtyState State { get { lock (sync) { if (pluginInputs.Any(inputs => inputs.HasChanged())) { foreach (var plugin in plugins) if (!dirty.ContainsKey(plugin.Manifest.Name)) dirty[plugin.Manifest.Name] = ++generation; } return new(new Dictionary<string, long>(dirty), setChanged, bases.Any(inputs => inputs.HasChanged()), installChanged); } } }
    private void Watch(string root, bool isBase, Action<Exception>? onError)
    {
        if (!Directory.Exists(root) || !watched.Add(root + (isBase ? "|base" : "|plugin"))) return;
        try
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            FileSystemEventHandler change = (_, e) => Mark(e.FullPath, isBase);
            watcher.Changed += change; watcher.Created += change; watcher.Deleted += change;
            watcher.Renamed += (_, e) => { Mark(e.OldFullPath, isBase); Mark(e.FullPath, isBase); };
            watcher.Error += (_, e) => onError?.Invoke(e.GetException());
            watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
        catch (Exception error) when (error is IOException or ArgumentException) { onError?.Invoke(error); }
    }
    private void Mark(string file, bool isBase)
    {
        if (BaseInputs.Excluded(file)) return;
        lock (sync)
        {
            if (disposed) return;
            generation++;
            if (!isBase)
            {
                setChanged = true;
                // A project reference may consume another plugin's contracts: conservatively dirty the complete set.
                foreach (var plugin in plugins) dirty[plugin.Manifest.Name] = generation;
            }
        }
        Changed?.Invoke(State);
    }
    public async Task<IReadOnlyList<string>> InstallPluginAsync(PluginInstallSource source, string root, ChordContext context, PluginInstallOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var names = await PluginInstaller.InstallAsync(source, root, roots, options ?? new(), context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        lock (sync) { installChanged = true; setChanged = true; foreach (var name in names) dirty[name] = ++generation; }
        Changed?.Invoke(State);
        return names;
    }
    public async Task InstallDependenciesAsync(string name, ChordContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var discovery = await PluginScanner.DiscoverAsync(roots, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        var plugin = discovery.Plugins.SingleOrDefault(plugin => plugin.Manifest.Name == name) ?? throw new ArgumentException($"plugin {name} was not discovered");
        await builder.RestoreDependenciesAsync(plugin, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        lock (sync) { dirty[name] = ++generation; setChanged = true; }
        Changed?.Invoke(State);
    }
    public async Task<PluginBuildSet> BuildAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var discovery = await PluginScanner.DiscoverAsync(roots, cancellationToken).ConfigureAwait(false);
        var applicable = discovery.Plugins.Where(plugin => builder.Policy is null || plugin.Manifest.Kind is null || plugin.Manifest.Kind == builder.Policy.Kind).ToArray();
        Dictionary<string, long> observed;
        lock (sync)
        {
            var old = plugins.Select(plugin => plugin.Manifest.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in old.Except(applicable.Select(plugin => plugin.Manifest.Name), StringComparer.Ordinal)) { dirty[removed] = ++generation; setChanged = true; }
            foreach (var plugin in applicable)
                if (!old.Contains(plugin.Manifest.Name)) { dirty[plugin.Manifest.Name] = ++generation; setChanged = true; }
            plugins = applicable; observed = new(dirty);
        }
        var built = new List<BuiltPlugin>();
        // Build in order so shared project references cannot race MSBuild outputs.
        foreach (var plugin in applicable) built.Add(await builder.BuildAsync(plugin, cancellationToken).ConfigureAwait(false));
        lock (sync)
        {
            pluginInputs.Clear();
            foreach (var item in built) if (item.Inputs is { } inputs)
            {
                pluginInputs.Add(inputs);
                foreach (var directory in inputs.Directories) Watch(directory, false, null);
            }
        }
        var result = new PluginBuildSet(built, discovery.Problems);
        lock (sync) marks[result] = observed;
        return result;
    }
    public async Task<IReadOnlyList<PluginRolloutStepReport>> RolloutAsync(PluginBuildSet build, IEnumerable<IPluginRolloutStep> steps, ChordContext context)
    {
        var results = new List<PluginRolloutStepReport>();
        foreach (var step in steps)
        {
            PluginRolloutStepReport result;
            try { result = await step.RolloutAsync(build, context).ConfigureAwait(false); }
            catch (Exception error) { result = new(step.Name, [new(step.Name, Error: error.Message)]); }
            results.Add(result);
            if (result.Stopped) return results;
        }
        // A host error leaves every mark dirty; a plugin refusal leaves that plugin dirty across later successful hosts.
        if (results.Count == 0 || results.SelectMany(result => result.Hosts).Any(host => host.Error is not null)) return results;
        var failed = results.SelectMany(result => result.Hosts).SelectMany(host => host.FailedPlugins ?? []).ToHashSet(StringComparer.Ordinal);
        lock (sync)
        {
            if (!marks.Remove(build, out var observed)) return results;
            foreach (var entry in observed)
                if (!failed.Contains(entry.Key) && dirty.TryGetValue(entry.Key, out var now) && now == entry.Value && build.Plugins.All(x => x.Plugin.Manifest.Name != entry.Key || x.Error is null)) dirty.Remove(entry.Key);
            if (dirty.Count == 0) { setChanged = false; installChanged = false; }
        }
        Changed?.Invoke(State);
        return results;
    }
    public ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return ValueTask.CompletedTask; disposed = true; foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); marks.Clear(); bases.Clear(); pluginInputs.Clear(); }
        return ValueTask.CompletedTask;
    }
}

public sealed class HostPluginRolloutStep(string name, PluginRuntime runtime) : IPluginRolloutStep
{
    public string Name { get; } = name;
    public async Task<PluginRolloutStepReport> RolloutAsync(PluginBuildSet build, ChordContext context)
    {
        var report = await runtime.ReloadAsync(build, context).ConfigureAwait(false);
        await runtime.DrainRetiredAsync(context).ConfigureAwait(false);
        return new(Name, [new(Name, FailedPlugins: report.Failed.Select(failure => failure.Plugin).ToArray())]);
    }
}
