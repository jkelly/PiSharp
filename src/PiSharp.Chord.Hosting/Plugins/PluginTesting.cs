// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/plugins/testing.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Hosting.Plugins;

public sealed class TestPluginOptions
{
    public required PluginBuilder Builder { get; init; }
    /// <summary>Opens a fresh in-memory store using the application's real native SQLite adapter.</summary>
    public required Func<ChordContext, Task<IStore>> OpenStore { get; init; }
    public IReadOnlyList<PluginFacet> Stubs { get; init; } = [];
    public PluginRuntimeOptions Runtime { get; init; } = new();
    public JsonObject? Principal { get; init; }
    public Address? Address { get; init; }
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A production-built plugin with stub facets and automatically collected unprovided registries.</summary>
public sealed class TestedPlugin : IAsyncDisposable
{
    private readonly IStore store;
    private readonly PluginRuntime runtime;
    private readonly List<HostError> errors;
    private readonly Dictionary<string, IRegistryValue<IReadOnlyList<RegistryEntry<object?>>>> collected;
    private Task? closing;
    private readonly object closeSync = new();
    internal TestedPlugin(ChordHost host, IStore store, PluginRuntime runtime, NativePlugin plugin, List<HostError> errors,
        Dictionary<string, IRegistryValue<IReadOnlyList<RegistryEntry<object?>>>> collected)
    { Host = host; this.store = store; this.runtime = runtime; Plugin = plugin; this.errors = errors; this.collected = collected; }
    public ChordHost Host { get; }
    public NativePlugin Plugin { get; }
    public IReadOnlyList<HostError> Errors { get { lock (errors) return errors.ToArray(); } }
    public IReadOnlyList<string> Unserved() => Host.Dispatcher.Invoke(Host.UnservedNames);
    public IReadOnlyList<RegistryEntry<C>> Contributions<C, V>(Registry<C, V> registry)
    {
        if (!collected.TryGetValue(registry.Name, out var value)) throw new ArgumentException($"registry {registry.Name} is not collected");
        return Host.Dispatcher.Invoke(() => value.Value.Select(entry => new RegistryEntry<C>
        { Plugin = entry.Plugin, Type = entry.Type, Version = entry.Version, Value = (C)entry.Value!, CallbackOwner = entry.CallbackOwner }).ToArray());
    }
    public Task CloseAsync() { lock (closeSync) return closing ??= CloseCoreAsync(); }
    private async Task CloseCoreAsync()
    { try { await Host.CloseAsync(ChordContext.Background).ConfigureAwait(false); } finally { try { await runtime.JoinOriginalsAsync().ConfigureAwait(false); } finally { await store.CloseAsync(ChordContext.Background).ConfigureAwait(false); } } }
    public ValueTask DisposeAsync() => new(CloseAsync());
}

public static class PluginTesting
{
    public static async Task<TestedPlugin> TestPluginAsync(NativePlugin plugin, TestPluginOptions options, ChordContext context)
    {
        if (!ReferenceEquals(options.Builder.Policy, options.Runtime.Policy)) throw new ArgumentException("test build and runtime must use the same production policy instance");
        var built = await options.Builder.BuildAsync(plugin, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        if (built.Error is { } buildError) throw new PluginError($"{plugin.Manifest.Name}: {buildError}");
        var store = await options.OpenStore(context).ConfigureAwait(false);
        var errors = new List<HostError>();
        ChordHost host;
        try { host = Hosts.Create(new() { Store = store, Principal = options.Principal, Address = options.Address, ReadyTimeout = options.ReadyTimeout, OnError = error => { lock (errors) errors.Add(error); } }); }
        catch { await store.CloseAsync(ChordContext.Background).ConfigureAwait(false); throw; }
        PluginRuntime runtime;
        try { runtime = new PluginRuntime(host, options.Runtime); }
        catch { try { await host.CloseAsync(ChordContext.Background).ConfigureAwait(false); } finally { await store.CloseAsync(ChordContext.Background).ConfigureAwait(false); } throw; }
        try
        {
            var report = await runtime.LoadAsync(new([built], []), options.Stubs, context).ConfigureAwait(false);
            if (report.Failed.Count != 0) throw new PluginError("test setup failed: " + string.Join("; ", report.Failed.Select(f => f.Error.Message)));
            var tokens = new Dictionary<string, Registry<object?, IReadOnlyList<RegistryEntry<object?>>>>(StringComparer.Ordinal);
            var collected = new Dictionary<string, IRegistryValue<IReadOnlyList<RegistryEntry<object?>>>>(StringComparer.Ordinal);
            // Reloading through PluginRuntime retains the actual production generation and replaces only our collector facet.
            while (true)
            {
                var deadline = DateTime.UtcNow + options.ReadyTimeout;
                do { await Task.Delay(5, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false); }
                while (host.Dispatcher.Invoke(host.ActivationsUnderWay) != 0 && DateTime.UtcNow < deadline);
                var fresh = host.Dispatcher.Invoke(host.UndeclaredRegistries).Where(item => !tokens.ContainsKey(item.Name)).ToArray();
                if (fresh.Length == 0) break;
                foreach (var item in fresh) tokens.Add(item.Name, Registry.Define<object?, IReadOnlyList<RegistryEntry<object?>>>(item.Name, new() { Version = item.Version }));
                var adapters = host.Dispatcher.Invoke(() => tokens.ToDictionary(pair => pair.Key, pair => host.RegistryRecord(pair.Key).ContributionVersions));
                var collector = Facet.Define((facetHost, _) =>
                {
                    foreach (var token in tokens.Values)
                    {
                        var accepts = new RegistryAccepts<object?, IReadOnlyList<RegistryEntry<object?>>>();
                        foreach (var version in adapters[token.Name].Where(v => v < token.Version)) accepts[version] = new() { Contribution = value => value, Value = table => table };
                        facetHost.Serve(token, new() { Compose = (table, _) => table, Accepts = accepts });
                    }
                });
                // Core facets are retained by runtime; collector generations need the updated core list.
                await runtime.SetCoreForTestingAsync(options.Stubs.Concat([new PluginFacet("chord.test-plugin-collector", collector)]).ToArray(), new([built], []), context).ConfigureAwait(false);
                foreach (var token in tokens.Values) if (!collected.ContainsKey(token.Name)) collected.Add(token.Name, await host.RegistryAsync(token, context).ConfigureAwait(false));
            }
            return new(host, store, runtime, plugin, errors, collected);
        }
        catch
        {
            try { await host.CloseAsync(ChordContext.Background).ConfigureAwait(false); }
            finally { try { await runtime.JoinOriginalsAsync().ConfigureAwait(false); } finally { await store.CloseAsync(ChordContext.Background).ConfigureAwait(false); } }
            throw;
        }
    }
}
