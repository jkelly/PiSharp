// Decision 0012: plugin DLLs. A plugin is a folder holding pisharp-agents-plugin.json and its assemblies. The loader checks it against the
// host's trust list (default deny), loads it into its own collectible load context from byte copies (so its files stay replaceable while it
// runs), and lets its entry register tools, agents and extensions into a staging context. The staged generation is then applied in one
// step: its tools become the tool source plugin.<id>, its agents are registered or updated, its extensions installed. Any failure leaves
// the previous generation in place. A replaced generation retires: its lifetime token is cancelled, its entry disposed, and its load
// context unloaded once the calls already running its tools (leases) have finished.
//
// Plugins are full trust: a load context is not a sandbox. Assemblies named PiSharp.* (and SharedAssemblies) always come from the host, so
// a plugin implements the host's contract types; its other dependencies load from its folder first, then from the host.
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using JsonValueKind = System.Text.Json.JsonValueKind;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Extensions;

namespace PiSharp.CustomAgents.Plugins;

/// <summary>A plugin's entry point: registers its tools, agents and extensions.</summary>
public interface IAgentPlugin
{
    /// <summary>Registers the plugin's contributions into <paramref name="context"/>. Nothing applies until it returns successfully.</summary>
    Task RegisterAsync(IAgentPluginContext context, CancellationToken cancellationToken);
}

/// <summary>What a plugin registers into while it loads.</summary>
public interface IAgentPluginContext
{
    /// <summary>The plugin ID (from its manifest).</summary>
    string PluginId { get; }
    /// <summary>Which load this is: 1, then one more per reload.</summary>
    int Generation { get; }
    /// <summary>The manifest's <c>settings</c> object (empty when absent).</summary>
    JsonObjectValue Settings { get; }
    /// <summary>Cancelled when this generation is replaced or unloaded.</summary>
    CancellationToken Lifetime { get; }
    /// <summary>The tool source the plugin's tools form (<c>plugin.&lt;id&gt;</c>); agents select it through <see cref="AgentDefinition.ToolSources"/>.</summary>
    string ToolSourceId { get; }
    /// <summary>Adds a tool to the plugin's tool source.</summary>
    void AddTool(ToolRegistration tool);
    /// <summary>Registers an agent; the plugin's tool source is added to its tool sources.</summary>
    void AddAgent(AgentDefinition definition);
    /// <summary>Installs a durable extension named <c>plugin.&lt;id&gt;</c> or <c>plugin.&lt;id&gt;.*</c>. Durable tasks need
    /// <c>"tasks": true</c> in the manifest: while such a plugin is unloaded, its pending tasks wait as missing.</summary>
    void AddExtension(Extension extension);
    /// <summary>A host service (<see cref="AgentPluginLoaderOptions.Services"/>); null when absent.</summary>
    T? GetService<T>() where T : class;
}

/// <summary>Names a plugin assembly's entry type when the manifest does not.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AgentPluginAttribute(Type entryType) : Attribute
{
    /// <summary>The type implementing <see cref="IAgentPlugin"/> (or <see cref="IPiSharpExtension"/>).</summary>
    public Type EntryType { get; } = entryType ?? throw new ArgumentNullException(nameof(entryType));
}

/// <summary>A folder the host trusts plugins from, optionally pinned to the exact bytes of the entry assembly.</summary>
/// <param name="Path">A plugin folder, or a folder of plugin folders.</param>
/// <param name="Sha256">The SHA-256 of the entry assembly (hex); null trusts any build there.</param>
public sealed record PluginTrust(string Path, string? Sha256 = null);

/// <summary>Raised when a plugin is not trusted.</summary>
public sealed class PluginTrustException(string message) : InvalidOperationException(message);

/// <summary>Raised when a plugin cannot be loaded; the previous generation, if any, stays.</summary>
public sealed class PluginLoadException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Options of a plugin loader.</summary>
public sealed record AgentPluginLoaderOptions
{
    /// <summary>The trusted folders. Required: a plugin outside them is refused.</summary>
    public required IReadOnlyList<PluginTrust> Trust { get; init; }
    /// <summary>More assembly names always taken from the host (PiSharp.* always are).</summary>
    public IReadOnlyList<string> SharedAssemblies { get; init; } = [];
    /// <summary>How long a retiring generation waits for running calls before unloading anyway.</summary>
    public TimeSpan UnloadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long a watched folder must be quiet before it reloads.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Services plugins may ask for.</summary>
    public IServiceProvider? Services { get; init; }
    /// <summary>Failures of background work (watched reloads, retiring generations).</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>A loaded plugin.</summary>
/// <param name="Id">The plugin ID.</param>
/// <param name="Generation">The loaded generation.</param>
/// <param name="Directory">The plugin folder.</param>
/// <param name="ToolSourceId">Its tool source.</param>
/// <param name="Tools">Its tool names.</param>
/// <param name="Agents">Its agent names.</param>
/// <param name="Diagnostics">What the loader ignored or changed (for example unsupported extension registrations).</param>
public sealed record AgentPluginInfo(string Id, int Generation, string Directory, string ToolSourceId, IReadOnlyList<string> Tools,
    IReadOnlyList<string> Agents, IReadOnlyList<string> Diagnostics);

/// <summary>Loads, reloads and unloads plugin folders into an agent catalog.</summary>
public sealed class AgentPluginLoader : IAsyncDisposable
{
    /// <summary>The manifest file name.</summary>
    public const string ManifestName = "pisharp-agents-plugin.json";
    /// <summary>The contract a manifest must name.</summary>
    public const string Contract = "pisharp.agents/1";

    private readonly AgentCatalog catalog;
    private readonly SemaphoreSlim line = new(1, 1);
    private readonly Dictionary<string, Loaded> plugins = new(StringComparer.Ordinal);
    private readonly List<Task> retiring = [];

    /// <summary>A loader into <paramref name="catalog"/>.</summary>
    public AgentPluginLoader(AgentCatalog catalog, AgentPluginLoaderOptions options)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Trust);
    }

    /// <summary>The options.</summary>
    public AgentPluginLoaderOptions Options { get; }

    /// <summary>The loaded plugins.</summary>
    public IReadOnlyList<AgentPluginInfo> Plugins
    {
        get { lock (plugins) return [.. plugins.Values.Select(plugin => plugin.Info)]; }
    }

    /// <summary>Loads the plugin in <paramref name="directory"/>; a team opened meanwhile waits for it. Loading a folder whose plugin is
    /// loaded already reloads it.</summary>
    public Task<AgentPluginInfo> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var load = ApplyAsync(Path.GetFullPath(directory), cancellationToken);
        catalog.Track(load.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        return load;
    }

    /// <summary>Loads the plugin <paramref name="id"/> again from its folder (after a rebuild); on failure the loaded generation stays.</summary>
    public Task<AgentPluginInfo> ReloadAsync(string id, CancellationToken cancellationToken = default)
    {
        Loaded? loaded;
        lock (plugins) plugins.TryGetValue(id, out loaded);
        return loaded is null ? throw new InvalidOperationException($"Plugin '{id}' is not loaded.") : ApplyAsync(loaded.Directory, cancellationToken);
    }

    /// <summary>Unloads the plugin <paramref name="id"/>: removes its tools, agents and extensions, then retires it.</summary>
    public async Task<bool> UnloadAsync(string id, CancellationToken cancellationToken = default)
    {
        await line.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Loaded? loaded;
            lock (plugins) if (!plugins.Remove(id, out loaded)) return false;
            foreach (var agent in loaded.Generation.Agents) catalog.Unregister(agent.Name);
            foreach (var extension in loaded.Generation.Extensions) catalog.Registry.Uninstall(extension);
            catalog.RemoveSource(loaded.Source.Id);
            await RetireAsync(loaded.Generation).ConfigureAwait(false);
            return true;
        }
        finally
        {
            line.Release();
        }
    }

    /// <summary>Reloads the plugin <paramref name="id"/> whenever its folder changes (debounced); dispose the result to stop.</summary>
    public IDisposable Watch(string id)
    {
        Loaded? loaded;
        lock (plugins) plugins.TryGetValue(id, out loaded);
        if (loaded is null) throw new InvalidOperationException($"Plugin '{id}' is not loaded.");
        return new FolderWatch(loaded.Directory, Options.Debounce, () => ReloadAsync(id), Options.OnError);
    }

    /// <summary>Unloads every plugin.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var id in Plugins.Select(plugin => plugin.Id).ToList()) await UnloadAsync(id).ConfigureAwait(false);
        Task[] pending;
        lock (retiring) pending = [.. retiring];
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>Completes once every retired generation has unloaded (or timed out).</summary>
    public Task WhenRetiredAsync()
    {
        lock (retiring) return Task.WhenAll([.. retiring]);
    }

    private async Task<AgentPluginInfo> ApplyAsync(string directory, CancellationToken cancellationToken)
    {
        await line.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = PluginManifest.Read(directory);
            Loaded? previous;
            lock (plugins) plugins.TryGetValue(manifest.Id, out previous);
            var assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.Assembly));
            CheckTrust(directory, assemblyPath);
            var generation = await StageAsync(manifest, directory, assemblyPath, (previous?.Generation.Number ?? 0) + 1, cancellationToken).ConfigureAwait(false);
            try
            {
                var source = previous?.Source ?? new PluginToolSource(manifest.SourceId);
                Commit(manifest, source, previous?.Generation, generation);
                var loaded = new Loaded(directory, source, generation);
                lock (plugins) plugins[manifest.Id] = loaded;
                if (previous is not null) Retire(previous.Generation);
                return loaded.Info;
            }
            catch
            {
                Retire(generation);
                throw;
            }
        }
        finally
        {
            line.Release();
        }
    }

    private void CheckTrust(string directory, string assemblyPath)
    {
        static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var folder = Folder(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = Options.Trust.Where(trust => folder.StartsWith(Folder(trust.Path), comparison)).ToList();
        if (matches.Count == 0) throw new PluginTrustException($"The plugin in '{directory}' is not in a trusted folder (AgentPluginLoaderOptions.Trust).");
        if (matches.All(trust => trust.Sha256 is not null))
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)));
            if (!matches.Any(trust => string.Equals(trust.Sha256, hash, StringComparison.OrdinalIgnoreCase)))
                throw new PluginTrustException($"The plugin assembly '{assemblyPath}' does not match its trusted SHA-256 (it is {hash}).");
        }
    }

    /// <summary>Loads the assembly into a fresh context and runs its entry into a staging context.</summary>
    private async Task<PluginGeneration> StageAsync(PluginManifest manifest, string directory, string assemblyPath, int number, CancellationToken cancellationToken)
    {
        if (!File.Exists(assemblyPath)) throw new PluginLoadException($"Plugin '{manifest.Id}' has no assembly '{manifest.Assembly}'.");
        var context = new PluginLoadContext(manifest.Id + "#" + number, assemblyPath, Options.SharedAssemblies);
        var generation = new PluginGeneration(manifest.Id, number, context);
        try
        {
            var assembly = context.LoadEntry();
            var type = manifest.EntryType is { } name
                ? assembly.GetType(name, throwOnError: false) ?? throw new PluginLoadException($"Plugin '{manifest.Id}' has no type '{name}'.")
                : assembly.GetCustomAttribute<AgentPluginAttribute>()?.EntryType
                    ?? throw new PluginLoadException($"Plugin '{manifest.Id}' names no entry type: set entryType in its manifest or add [assembly: AgentPlugin(typeof(...))].");
            var entry = Activator.CreateInstance(type) ?? throw new PluginLoadException($"Plugin '{manifest.Id}' could not create '{type.FullName}'.");
            generation.Entry = entry;
            var staging = new StagingContext(manifest, generation, Options.Services);
            switch (entry)
            {
                case IAgentPlugin plugin:
                    await plugin.RegisterAsync(staging, cancellationToken).ConfigureAwait(false);
                    break;
                case IPiSharpExtension extension:
                    var adapted = await PiSharpExtensionAdapter.LoadAsync(extension, manifest.Id, new PiSharpExtensionAdapterOptions
                    {
                        Generation = number,
                        ReplaySafe = manifest.ReplaySafe,
                        Lifetime = generation.Lifetime,
                    }, cancellationToken).ConfigureAwait(false);
                    foreach (var tool in adapted.Tools) staging.AddTool(tool);
                    generation.Diagnostics.AddRange(adapted.Diagnostics);
                    break;
                default:
                    throw new PluginLoadException($"Plugin '{manifest.Id}': '{type.FullName}' implements neither IAgentPlugin nor IPiSharpExtension " +
                        "(of the host's contract assemblies).");
            }
            generation.Tools = [.. staging.Tools.Select(tool => generation.Lease(tool))];
            generation.Agents = [.. staging.Agents];
            generation.Extensions = [.. staging.Extensions];
            return generation;
        }
        catch (Exception error)
        {
            Retire(generation);
            throw error is PluginLoadException or PluginTrustException or OperationCanceledException
                ? error
                : new PluginLoadException($"Plugin '{manifest.Id}' failed to load: {error.Message}", error);
        }
    }

    /// <summary>Applies <paramref name="next"/> in place of <paramref name="previous"/>: tools, then agents, then extensions; undone on failure.</summary>
    private void Commit(PluginManifest manifest, PluginToolSource source, PluginGeneration? previous, PluginGeneration next)
    {
        var owned = previous?.Agents.Select(agent => agent.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var agent in next.Agents)
            if (!owned.Contains(agent.Name) && catalog.Find(agent.Name) is not null)
                throw new PluginLoadException($"Plugin '{manifest.Id}' registers agent '{agent.Name}', which is already registered by the host or another plugin.");
        var oldTools = source.Tools.ToImmutableArray();
        var added = previous is null;
        if (added) catalog.AddSource(source);
        var applied = new List<AgentDefinition>();
        try
        {
            source.Set(next.Tools);
            foreach (var agent in next.Agents)
            {
                if (owned.Contains(agent.Name)) catalog.Update(agent); else catalog.Register(agent);
                applied.Add(agent);
            }
        }
        catch (Exception error)
        {
            foreach (var agent in applied)
            {
                if (previous?.Agents.FirstOrDefault(old => old.Name == agent.Name) is { } old) catalog.Update(old);
                else catalog.Unregister(agent.Name);
            }
            if (added) catalog.RemoveSource(source.Id); else source.Set(oldTools);
            throw error is PluginLoadException ? error : new PluginLoadException($"Plugin '{manifest.Id}' could not be applied: {error.Message}", error);
        }
        foreach (var gone in previous?.Agents.Where(old => next.Agents.All(agent => agent.Name != old.Name)) ?? []) catalog.Unregister(gone.Name);
        foreach (var extension in next.Extensions) catalog.Registry.Install(extension);
        foreach (var gone in previous?.Extensions.Where(old => next.Extensions.All(extension => extension.Name != old.Name)) ?? [])
            catalog.Registry.Uninstall(gone);
    }

    private void Retire(PluginGeneration generation)
    {
        var task = RetireAsync(generation);
        lock (retiring)
        {
            retiring.RemoveAll(item => item.IsCompleted);
            retiring.Add(task);
        }
    }

    private async Task RetireAsync(PluginGeneration generation)
    {
        try
        {
            await generation.RetireAsync(Options.UnloadTimeout).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Options.OnError?.Invoke(error);
        }
    }

    private sealed record Loaded(string Directory, PluginToolSource Source, PluginGeneration Generation)
    {
        public AgentPluginInfo Info => new(Generation.Id, Generation.Number, Directory, Source.Id, [.. Generation.Tools.Select(tool => tool.Name)],
            [.. Generation.Agents.Select(agent => agent.Name)], [.. Generation.Diagnostics]);
    }
}

/// <summary>A plugin's tool source.</summary>
internal sealed class PluginToolSource(string id) : ToolSource(id)
{
    public void Set(ImmutableArray<ToolRegistration> tools) => Publish(_ => tools);
}

/// <summary>One load of a plugin: its context, entry, contributions, lifetime and leases.</summary>
internal sealed class PluginGeneration(string id, int number, PluginLoadContext context)
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private int leases;
    private bool retired;

    public string Id { get; } = id;
    public int Number { get; } = number;
    public object? Entry { get; set; }
    public CancellationToken Lifetime => lifetime.Token;
    public ImmutableArray<ToolRegistration> Tools { get; set; } = [];
    public ImmutableArray<AgentDefinition> Agents { get; set; } = [];
    public ImmutableArray<Extension> Extensions { get; set; } = [];
    public List<string> Diagnostics { get; } = [];
    /// <summary>The load context, weakly: tests check that a retired generation unloads.</summary>
    public WeakReference Context { get; } = new(context);
    private PluginLoadContext? loadContext = context;

    /// <summary>Wraps <paramref name="tool"/> so each call holds a lease and failures become error results that name the plugin.</summary>
    public ToolRegistration Lease(ToolRegistration tool) => tool with
    {
        Execute = async (arguments, api, durable) =>
        {
            lock (gate)
            {
                if (retired) return Error($"Plugin '{Id}' was unloaded; tool '{tool.Name}' is not available.");
                leases++;
            }
            try
            {
                return await tool.Execute(arguments, api, durable).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !durable.IsCancellationRequested)
            {
                return Error($"Plugin '{Id}' tool '{tool.Name}' failed: {error.Message}");
            }
            finally
            {
                lock (gate)
                {
                    leases--;
                    if (retired && leases == 0) drained.TrySetResult();
                }
            }
        },
    };

    /// <summary>Ends the generation: cancels its lifetime, disposes its entry, waits for its calls, and unloads its context.</summary>
    public async Task RetireAsync(TimeSpan timeout)
    {
        lock (gate)
        {
            if (retired) return;
            retired = true;
            if (leases == 0) drained.TrySetResult();
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (Entry is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (Entry is IDisposable disposable) disposable.Dispose();
        }
        finally
        {
            await Task.WhenAny(drained.Task, Task.Delay(timeout)).ConfigureAwait(false);
            Entry = null;
            Tools = [];
            Agents = [];
            Extensions = [];
            loadContext?.Unload();
            loadContext = null;
            lifetime.Dispose();
        }
    }

    private static ToolExecutionResult Error(string message) => AgentTool.ToExecution(AgentToolResult.Error(message));
}

/// <summary>What a plugin registered while loading.</summary>
internal sealed class StagingContext(PluginManifest manifest, PluginGeneration generation, IServiceProvider? services) : IAgentPluginContext
{
    public List<ToolRegistration> Tools { get; } = [];
    public List<AgentDefinition> Agents { get; } = [];
    public List<Extension> Extensions { get; } = [];

    public string PluginId => manifest.Id;
    public int Generation => generation.Number;
    public JsonObjectValue Settings => manifest.Settings;
    public CancellationToken Lifetime => generation.Lifetime;
    public string ToolSourceId => manifest.SourceId;

    public void AddTool(ToolRegistration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ToolNames.Validate(tool.Name);
        if (Tools.Any(existing => existing.Name == tool.Name)) throw new PluginLoadException($"Plugin '{manifest.Id}' adds tool '{tool.Name}' twice.");
        Tools.Add(tool);
    }

    public void AddAgent(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (Agents.Any(existing => existing.Name == definition.Name)) throw new PluginLoadException($"Plugin '{manifest.Id}' adds agent '{definition.Name}' twice.");
        Agents.Add(definition.ToolSources.Contains(manifest.SourceId) ? definition : definition with { ToolSources = [.. definition.ToolSources, manifest.SourceId] });
    }

    public void AddExtension(Extension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var prefix = "plugin." + manifest.Id;
        if (extension.Name != prefix && !extension.Name.StartsWith(prefix + ".", StringComparison.Ordinal))
            throw new PluginLoadException($"Plugin '{manifest.Id}' extensions must be named '{prefix}' or '{prefix}.*', not '{extension.Name}'.");
        if (extension.Tasks is { Count: > 0 } && !manifest.Tasks)
            throw new PluginLoadException($"Plugin '{manifest.Id}' defines durable tasks; set \"tasks\": true in its manifest to allow them.");
        Extensions.Add(extension);
    }

    public T? GetService<T>() where T : class => services?.GetService(typeof(T)) as T;
}

/// <summary>pisharp-agents-plugin.json.</summary>
internal sealed record PluginManifest(string Id, string Assembly, string? EntryType, bool Tasks, IReadOnlyList<string> ReplaySafe, JsonObjectValue Settings)
{
    public string SourceId => "plugin." + Id;

    public static PluginManifest Read(string directory)
    {
        var path = Path.Combine(directory, AgentPluginLoader.ManifestName);
        if (!File.Exists(path)) throw new PluginLoadException($"'{directory}' has no {AgentPluginLoader.ManifestName}.");
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            root = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new PluginLoadException($"{path} is not valid JSON: {error.Message}", error);
        }
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var contract = Text("contract");
        if (contract != AgentPluginLoader.Contract) throw new PluginLoadException($"{path} names contract '{contract}', not '{AgentPluginLoader.Contract}'.");
        var id = Text("id") ?? throw new PluginLoadException($"{path} has no id.");
        try { SourceIds.Validate("plugin." + id); }
        catch (ArgumentException) { throw new PluginLoadException($"'{id}' is not a plugin ID (lowercase letters, digits, '_', '.' or '-')."); }
        var assembly = Text("assembly") ?? throw new PluginLoadException($"{path} has no assembly.");
        if (Path.IsPathRooted(assembly) || assembly.Split('/', '\\').Contains("..")) throw new PluginLoadException($"{path}: the assembly must be inside the plugin folder.");
        var tasks = root.TryGetProperty("tasks", out var flag) && flag.ValueKind == JsonValueKind.True;
        IReadOnlyList<string> replaySafe = root.TryGetProperty("replaySafe", out var safe) && safe.ValueKind == JsonValueKind.Array
            ? [.. safe.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];
        var settings = root.TryGetProperty("settings", out var values) && values.ValueKind == JsonValueKind.Object
            ? (JsonObjectValue)JsonValue.Parse(values.GetRawText())
            : JsonObjectValue.Empty;
        return new PluginManifest(id, assembly, Text("entryType"), tasks, replaySafe, settings);
    }
}

/// <summary>A plugin's collectible load context, loading byte copies so the plugin's files stay replaceable.</summary>
internal sealed class PluginLoadContext(string name, string entryPath, IReadOnlyList<string> shared)
    : AssemblyLoadContext("PiSharp.CustomAgents.Plugin:" + name, isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(entryPath);

    public Assembly LoadEntry() => LoadCopy(entryPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The host's contracts are shared, so the plugin implements the host's types; returning null takes the host's copy.
        if (assemblyName.Name is { } simple && (simple.StartsWith("PiSharp.", StringComparison.Ordinal) || shared.Contains(simple, StringComparer.Ordinal)))
            return null;
        return resolver.ResolveAssemblyToPath(assemblyName) is { } path && File.Exists(path) ? LoadCopy(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;

    private Assembly LoadCopy(string path)
    {
        using var image = new MemoryStream(File.ReadAllBytes(path), writable: false);
        var symbols = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(symbols)) return LoadFromStream(image);
        using var pdb = new MemoryStream(File.ReadAllBytes(symbols), writable: false);
        return LoadFromStream(image, pdb);
    }
}

/// <summary>Runs an action once a folder has been quiet for a while after changing.</summary>
internal sealed class FolderWatch : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly TimeSpan debounce;

    public FolderWatch(string directory, TimeSpan debounce, Func<Task> run, Action<Exception>? onError, string filter = "*", bool deletes = false)
    {
        this.debounce = debounce;
        timer = new Timer(_ => _ = run().ContinueWith(task => onError?.Invoke(task.Exception!.GetBaseException()), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(directory, filter) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += (_, _) => Kick();
        watcher.Created += (_, _) => Kick();
        watcher.Renamed += (_, _) => Kick();
        if (deletes) watcher.Deleted += (_, _) => Kick();
        watcher.EnableRaisingEvents = true;
    }

    private void Kick() => timer.Change(debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        watcher.Dispose();
        timer.Dispose();
    }
}
