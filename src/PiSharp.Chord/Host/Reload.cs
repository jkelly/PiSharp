// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/host.ts (load/reload)
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

public sealed partial class ChordHost
{
    private Task reloadTail = Task.CompletedTask;
    private bool loadComplete;

    /// <summary>Checks and publishes facets in one batch. Requests made before publication wait for this batch.</summary>
    public Task<LoadReport> LoadAsync(IReadOnlyList<PluginFacet> facets, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(facets); ArgumentNullException.ThrowIfNull(context);
        var attributes = Attributes(); attributes["plugins"] = (double)facets.Count;
        return TraceOperation("chord.load", attributes, context, traced => LoadCoreAsync(facets, traced));
    }
    private Task<LoadReport> LoadCoreAsync(IReadOnlyList<PluginFacet> facets, ChordContext context) => Dispatcher.RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(facets); ArgumentNullException.ThrowIfNull(context);
        AssertOpen(); Batch.AssertOutside("load");
        PluginStaging.CheckNames(facets);
        if (LoadStarted) throw new InvalidOperationException("load runs once per host");
        context.AbortSignal?.ThrowIfCancellationRequested();
        LoadStarted = true;
        var loaded = new List<LoadedPlugin>();
        var failed = new List<PluginFailure>();
        var given = new HashSet<string>(StringComparer.Ordinal);
        foreach (var facet in facets)
        {
            try
            {
                if (!given.Add(facet.Name)) throw Twice(facet.Name);
                var plugin = PluginStaging.Stage(facet, this);
                if (PluginStaging.Add(loaded, plugin) is { } error) throw error;
            }
            catch (Exception error) { failed.Add(new(facet.Name, error)); }
        }
        if (context.AbortSignal?.IsCancellationRequested == true)
        {
            LoadStarted = false;
            throw new ChordError("aborted", "load: aborted before its publication");
        }
        PublishPlugins(loaded);
        var objects = EnsureHostObjects(context);
        // Loading owns its publication once committed; cancellation of the caller cannot unpublish it.
        await Task.WhenAll(objects.Select(obj => obj.FirstOutcome));
        loadComplete = true;
        return new LoadReport(failed);
    });

    /// <summary>Serializes reload transactions while leaving the dispatcher free for the work they await.</summary>
    public Task<ReloadReport> ReloadAsync(IReadOnlyList<PluginFacet> facets, ChordContext context, ReloadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(facets); ArgumentNullException.ThrowIfNull(context);
        var attributes = Attributes(); attributes["plugins"] = (double)facets.Count;
        return TraceOperation("chord.reload", attributes, context, traced => ReloadQueuedAsync(facets, traced, options));
    }
    private Task<ReloadReport> ReloadQueuedAsync(IReadOnlyList<PluginFacet> facets, ChordContext context, ReloadOptions? options = null) => Dispatcher.Invoke(() =>
    {
        ArgumentNullException.ThrowIfNull(facets); ArgumentNullException.ThrowIfNull(context);
        AssertOpen(); Batch.AssertOutside("reload");
        PluginStaging.CheckNames(facets);
        if (!loadComplete) throw new InvalidOperationException("reload runs after load");
        var previous = reloadTail;
        var reload = Dispatcher.RunAsync(async () =>
        {
            await previous;
            return await ReloadCoreAsync(facets, context, options ?? new());
        });
        // Do not retain a report (or a failed facet's exception closure) in the queue tail.
        reloadTail = reload.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return reload;
    });

    private async Task<ReloadReport> ReloadCoreAsync(IReadOnlyList<PluginFacet> facets, ChordContext context, ReloadOptions options)
    {
        AssertOpen();
        CheckReloadCancellation(context);
        var plan = StageReload(facets, options.Failed ?? [], context);
        CheckReloadCancellation(context);
        var committed = new CommittedReload(plan.Added, plan.Changed, plan.Removed, plan.Failed.Select(failure => failure.Plugin).ToArray());
        PublishPlugins(plan.Plugins, wake: false);
        var checks = new Dictionary<ReloadFate, Task<ReloadedObject>>();
        foreach (var fate in plan.Fates.Where(fate => fate.Kind == "end").Concat(plan.Fates.Where(fate => fate.Kind != "end")))
        {
            if (fate.Kind == "end")
            {
                fate.Object.RemoveForReload();
                checks.Add(fate, Task.FromResult(new ReloadedObject(fate.Object.Label, "ended")));
            }
            else
            {
                var checkedAttempt = fate.Object.ApplyReload(fate.Served!, fate.Kind, fate.Requests ?? []);
                checks.Add(fate, ReportObjectAsync(fate.Object.Label, fate.Kind == "restart" ? "restarted" : "retried", checkedAttempt));
            }
        }
        foreach (var fate in plan.Fates) fate.Object.StartSuccessor();
        EnsureHostObjects(context);
        WakePublication();
        try
        {
            // Task.WaitAsync returns an already-completed task even with an already-cancelled token.
            // The explicit check preserves awaitWithContext's post-commit cancellation contract.
            context.AbortSignal?.ThrowIfCancellationRequested();
            var objects = await Task.WhenAll(plan.Fates.Select(fate => checks[fate])).WaitAsync(context.AbortSignal ?? CancellationToken.None);
            return new(committed, plan.Failed, objects);
        }
        catch (OperationCanceledException)
        {
            throw new ChordError("aborted", "reload: aborted after its commit", details: new JsonObject
            {
                ["committed"] = new JsonObject
                {
                    ["added"] = JsonNames(committed.Added), ["changed"] = JsonNames(committed.Changed),
                    ["removed"] = JsonNames(committed.Removed), ["failed"] = JsonNames(committed.Failed),
                },
            });
        }
    }

    private static async Task<ReloadedObject> ReportObjectAsync(string label, string fate, Task<FailureCause?> check)
    {
        var failure = await check;
        return new(label, failure is null ? fate : "parked", failure);
    }

    private ReloadPlan StageReload(IReadOnlyList<PluginFacet> facets, IReadOnlyList<PluginFailure> before, ChordContext context)
    {
        var given = new OrderedDictionary<string, Facet>(StringComparer.Ordinal);
        var duplicates = new List<PluginFailure>();
        foreach (var facet in facets)
            if (!given.TryAdd(facet.Name, facet.Facet)) duplicates.Add(new(facet.Name, Twice(facet.Name)));
        var failed = new OrderedDictionary<string, Exception>(StringComparer.Ordinal);
        foreach (var failure in before)
        {
            failed[failure.Plugin] = failure.Error;
            // A failed compilation is still a requested plugin: keep its last successful generation.
            if (!given.ContainsKey(failure.Plugin) && Plugins.TryGetValue(failure.Plugin, out var old)) given.Add(failure.Plugin, old.Facet);
        }
        while (true)
        {
            var plugins = new List<LoadedPlugin>();
            var fresh = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, facet) in given)
                if (Plugins.TryGetValue(name, out var loaded) && (ReferenceEquals(loaded.Facet, facet) || failed.ContainsKey(name)))
                    PluginStaging.Add(plugins, loaded);
                else if (!failed.ContainsKey(name)) fresh.Add(name);
            var failures = new Dictionary<string, Exception>(StringComparer.Ordinal);
            foreach (var (name, facet) in given)
            {
                if (!fresh.Contains(name)) continue;
                try
                {
                    var plugin = PluginStaging.Stage(new(name, facet), this);
                    if (PluginStaging.Add(plugins, plugin) is { } error) throw error;
                }
                catch (Exception error) { failures[name] = error; }
            }
            if (failures.Count != 0) { RememberFailures(failed, failures); continue; }
            var types = plugins.SelectMany(plugin => plugin.Types).ToDictionary(type => type.Contract.Name, StringComparer.Ordinal);
            var removed = Plugins.Keys.Where(name => !given.ContainsKey(name)).ToArray();
            var changedOwners = fresh.Concat(removed).ToHashSet(StringComparer.Ordinal);
            var fates = new List<ReloadFate>();
            var recorded = new List<(string Plugin, IReadOnlyList<RecordedRequest> Requests, bool Fresh)>();
            foreach (var obj in Objects.Values.ToArray())
            {
                if (obj.State.Kind == "ended") continue;
                if (!changedOwners.Contains(obj.Served.Plugin))
                {
                    if (obj.Current is { } current) recorded.Add((obj.Served.Plugin, current.Self.Requests, false));
                    continue;
                }
                if (!types.TryGetValue(obj.Served.Contract.Name, out var served) || served.Contract.Kind != obj.Served.Contract.Kind)
                { fates.Add(new(obj, "end")); continue; }
                try
                {
                    var declarations = PluginStaging.Record(obj, served, context);
                    recorded.Add((served.Plugin, declarations.Requests, fresh.Contains(served.Plugin)));
                    var fate = obj.State.Kind != "live" ? "retry" : "restart";
                    fates.Add(new(obj, fate, served, declarations.Requests));
                }
                catch (Exception error) { failures.TryAdd(served.Plugin, error); }
            }
            if (failures.Count != 0) { RememberFailures(failed, failures); continue; }
            foreach (var consumer in recorded)
                foreach (var request in consumer.Requests)
                {
                    if (!PlacedHere(request)) continue;
                    var provider = ProviderPlugin(plugins, request);
                    if (CanBind(plugins, request)) continue;
                    string? reject = null;
                    // A fresh consumer cannot advance past a provider kept after a failure, including a failed
                    // setup whose would-be declarations are unknown. An unchanged consumer also protects the
                    // provider it already used: accepting that provider alone would split the compatibility set.
                    if (consumer.Fresh && (provider is not null && failed.ContainsKey(provider) || provider is null && failed.Count > 0)) reject = consumer.Plugin;
                    else if (!consumer.Fresh && provider is not null && fresh.Contains(provider) && CanBind(Plugins.Values.ToArray(), request)) reject = provider;
                    if (reject is not null) failures.TryAdd(reject, new ArgumentException($"plugin \"{reject}\": needs {request.Name} v{request.Version}, incompatible with the retained plugin set"));
                }
            if (failures.Count != 0) { RememberFailures(failed, failures); continue; }
            return new(plugins, fresh.Where(name => !Plugins.ContainsKey(name)).ToArray(), fresh.Where(Plugins.ContainsKey).ToArray(),
                removed, duplicates.Concat(failed.Select(entry => new PluginFailure(entry.Key, entry.Value))).ToArray(), fates);
        }
    }

    private static void RememberFailures(IDictionary<string, Exception> failed, IReadOnlyDictionary<string, Exception> next)
    {
        foreach (var (plugin, error) in next)
            if (!failed.TryAdd(plugin, error)) throw new InvalidOperationException($"reload staging did not converge for {plugin}", error);
    }
    private bool PlacedHere(RecordedRequest request)
    {
        if (request.Contract is not { Kind: ContractKind.Remote } contract) return true;
        try { return Equals(ContractsInternal.DataOf(contract).HostAddress!(request.Parameters!), Address); }
        catch { return false; }
    }
    private static string? ProviderPlugin(IReadOnlyList<LoadedPlugin> plugins, RecordedRequest request) => request.Contract is not null
        ? plugins.FirstOrDefault(plugin => plugin.Types.Any(type => type.Contract.Name == request.Name))?.Name
        : plugins.FirstOrDefault(plugin => plugin.Registries.Any(registry => registry.Name == request.Name))?.Name;
    private static bool CanBind(IReadOnlyList<LoadedPlugin> plugins, RecordedRequest request)
    {
        if (request.Contract is { } contract)
        {
            var served = plugins.SelectMany(plugin => plugin.Types).FirstOrDefault(type => type.Contract.Name == request.Name);
            return served is not null && served.Contract.Kind == contract.Kind && DataInternal.Binds(served.Provider, request.Version, contract.Fingerprint);
        }
        var registry = plugins.SelectMany(plugin => plugin.Registries).FirstOrDefault(registry => registry.Name == request.Name);
        return registry is not null && (registry.Version == request.Version || request.Version < registry.Version && registry.Provider.ValueAdapter(request.Version) is not null);
    }
    private static void CheckReloadCancellation(ChordContext context)
    { if (context.AbortSignal?.IsCancellationRequested == true) throw new ChordError("aborted", "reload: aborted before its commit"); }
    private static ArgumentException Twice(string name) => new($"plugin \"{name}\" was given twice");
    private static JsonArray JsonNames(IReadOnlyList<string> names) => new(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
    private sealed record ReloadFate(ChordObject Object, string Kind, ServedType? Served = null, IReadOnlyList<RecordedRequest>? Requests = null);
    private sealed record ReloadPlan(IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<string> Added, IReadOnlyList<string> Changed,
        IReadOnlyList<string> Removed, IReadOnlyList<PluginFailure> Failed, IReadOnlyList<ReloadFate> Fates);
}
