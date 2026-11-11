// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/app.ts.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Surfaces;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Store;
using PiSharp.Chord.Surfaces;
using PiSharp.Chord.TelemetryIngest;
using PiSharp.Telemetry;
using AppLayoutTools = PiSharp.Chord.Hosting.App.Layout;
using IngestFacet = PiSharp.Chord.TelemetryIngest.TelemetryIngest;

namespace PiSharp.Chord.Hosting.App;

/// <summary>Owns one application server and every resource started on its behalf.</summary>
public sealed class App : IAsyncDisposable
{
    public static HostKind Server { get; } = new("server");
    private readonly AppOptions options;
    private readonly object closeGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim apply = new(1, 1);
    private readonly Dictionary<string, ProcessWorkers> workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginManager> managers = new(StringComparer.Ordinal);
    private readonly List<IDisposable> baseWatches = [];
    private readonly ConcurrentDictionary<string, JsonObject> workerPrincipals = new(StringComparer.Ordinal);
    private readonly CallbackOwnership callbacks = new();
    private IDisposable? callbackRegistration;
    private AppDataDirectory? directory;
    private IStore? store;
    private ChordHost? host;
    private ChordHttp? http;
    private PluginRuntime? runtime;
    private FilesService? files;
    private Sessions? sessions;
    private OwnerLink? owner;
    private AppArchiveService? archives;
    private BaseInputs? serverInputs;
    private Task? close;
    private Uri? listening;
    public ChordHost Host => host ?? throw new InvalidOperationException("server has not started");
    public ChordHttp Http => http ?? throw new InvalidOperationException("server has not started");
    public IStore Store => store ?? throw new InvalidOperationException("server has not started");
    public IReadOnlyDictionary<string, ProcessWorkers> Workers => workers;
    public PluginManager Plugins => managers["server"];
    public Sessions Sessions => sessions ?? throw new InvalidOperationException("server has not started");
    public AppLayout Layout => directory?.Layout ?? throw new InvalidOperationException("data directory is not open");
    public string DataDir => Layout.DataDir;
    public string OwnerTokenFile => Layout.OwnerTokenFile;
    public string OwnerLink => owner is not null && listening is not null ? owner.Link(listening) : throw new InvalidOperationException("server has not started");
    public Uri Listening => listening ?? throw new InvalidOperationException("server has not started");
    public Task BasesBuilt { get; private set; } = Task.CompletedTask;
    public Task BaseWatched { get; private set; } = Task.CompletedTask;
    private ITelemetryIngestTarget? TelemetryTarget => options.TelemetryTarget ?? options.Telemetry as ITelemetryIngestTarget;
    private App(AppOptions options) => this.options = options;

    public static async Task<App> StartServerAsync(AppOptions options, ChordContext context)
    {
        Validate(options); context.AbortSignal?.ThrowIfCancellationRequested();
        var app = new App(options);
        try { await app.StartAsync(context).ConfigureAwait(false); return app; }
        catch
        {
            try { await app.CloseAsync(ChordContext.Background).ConfigureAwait(false); }
            catch (Exception cleanup) { app.Report(new("cleanup", cleanup)); }
            throw;
        }
    }

    private static void Validate(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        _ = AppLayoutTools.DefaultDataDir(options.Name);
        ArgumentNullException.ThrowIfNull(options.OnError);
        AppPrincipals.Require(options.Principal.Server); AppPrincipals.Require(options.Principal.Owner);
        if (AppPrincipals.Same(options.Principal.Server, options.Principal.Owner)) throw new ArgumentException("owner must differ from the server principal", nameof(options));
        if (options.Api.Versions.Count == 0 || options.Api.Versions.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("API needs newest-first version names", nameof(options));
        if (!options.Kinds.ContainsKey("server")) throw new ArgumentException("server facet kind is required", nameof(options));
        if (options.Workers.Count > 0 && options.Principal.Worker is null) throw new ArgumentException("worker principal is required when worker kinds are configured", nameof(options));
        if (options.Workers.Select(worker => worker.Kind.Kind).Distinct(StringComparer.Ordinal).Count() != options.Workers.Count) throw new ArgumentException("duplicate worker kind", nameof(options));
        foreach (var worker in options.Workers)
        {
            if (!options.Kinds.ContainsKey(worker.Facet)) throw new ArgumentException($"worker facet kind '{worker.Facet}' is not configured", nameof(options));
            if (worker.Kind.Kind is "server" || worker.Kind.Kind.Contains('/') || worker.Kind.Kind.Contains('\\')) throw new ArgumentException("worker kind must be a distinct directory name", nameof(options));
        }
        if (options.Limits.EphemeralSessions < 1 || options.Limits.PendingPairings < 1 || options.Limits.PairingsPerAddress < 1 || options.Limits.Uploads < 1 || options.Limits.ImportBytes < 0 || options.Limits.ImportEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "application limits are invalid");
        if (options.Listen.Port is < 0 or > 65535 || options.Timeouts.Close <= TimeSpan.Zero || options.Timeouts.Ready <= TimeSpan.Zero || options.Timeouts.ReloadDrain <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "application listen or timeout options are invalid");
        if (options.TelemetryTarget is not null && options.Telemetry is null) throw new ArgumentException("telemetry ingestion requires the server telemetry context", nameof(options));
    }

    private PluginManager Manager(string kind)
    {
        if (managers.TryGetValue(kind, out var existing)) return existing;
        var policy = new PluginManagerOptions { AppApi = options.Api, Kind = kind, Kinds = options.Kinds };
        var builder = new PluginBuilder(new()
        {
            DotnetPath = options.DotnetPath, ArtifactDirectory = Path.Combine(Layout.CacheDirectory, "plugins", kind),
            PackageDirectory = options.PackageDirectory, Restore = options.RestorePlugins, Timeout = options.PluginBuildTimeout, Policy = policy,
        });
        var manager = new PluginManager([new PluginRoot(Path.GetFullPath(options.Root ?? "plugins"), true), new PluginRoot(Layout.PluginsDirectory)], builder,
            error => Report(new("plugins", error)));
        managers.Add(kind, manager); return manager;
    }

    private async Task StartAsync(ChordContext context)
    {
        // Best effort native server graph: loaded file-backed assemblies and explicit app roots.
        // A later dynamic assembly needs an explicit Base entry; the snapshot is never claimed as a sandbox.
        try
        {
            var paths = options.Base.Select(Path.GetFullPath).ToArray();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
                .Select(assembly => assembly.Location).Where(path => path.Length > 0);
            serverInputs = BaseInputs.Capture(assemblies.Concat(paths.Where(path => !Directory.Exists(path))), paths.Where(Directory.Exists));
        }
        catch (Exception error) { Report(new("server_scan", error)); }
        directory = await AppLayoutTools.OpenAsync(options.DataDir ?? AppLayoutTools.DefaultDataDir(options.Name), context).ConfigureAwait(false);
        var secret = await Secrets.SecretFileAsync(Layout.CookieSecretFile).ConfigureAwait(false);
        store = await AppLayoutTools.OpenStoreAsync(Layout.StoreFile, context).ConfigureAwait(false);
        host = Hosts.Create(new()
        {
            Address = Server.Address(), Principal = AppPrincipals.Require(options.Principal.Server), Store = store,
            Telemetry = options.Telemetry, PrincipalAttributes = options.PrincipalAttributes, TimeProvider = options.TimeProvider,
            CloseTimeout = options.Timeouts.Close ?? TimeSpan.FromSeconds(30), ReadyTimeout = options.Timeouts.Ready ?? TimeSpan.FromSeconds(60),
            ReloadDrainTimeout = options.Timeouts.ReloadDrain ?? TimeSpan.FromSeconds(5), OnError = error => Report(new("host", error.Error, Details: error)),
        });
        callbackRegistration = host.RegisterCallbackLifetime((_, _) => callbacks.Acquire());
        sessions = new(secret, options.Limits.EphemeralSessions, options.TimeProvider);
        owner = new(options.Principal.Owner, sessions, options.TimeProvider);
        var pairings = new Pairings(options.Principal.Owner, sessions, new(options.Limits.PendingPairings, options.Limits.PairingsPerAddress), options.TimeProvider);
        http = new(host, new()
        {
            Address = options.Listen.Address, Port = options.Listen.Port, LinksPath = "/chord", LinksOpened = sessions.LinksOpenedAsync,
            Telemetry = options.Telemetry, Configure = options.ConfigureHttp, CloseTimeout = options.Timeouts.Close ?? TimeSpan.FromSeconds(30),
            OnError = error => Report(new("http", error.Error, Details: error)),
        });
        var manager = Manager("server");
        runtime = new(host, new() { Kind = "server", Policy = new() { AppApi = options.Api, Kind = "server", Kinds = options.Kinds } });
        var life = ChordContext.WithAbortSignal(lifetime.Token, ChordContext.Background);
        foreach (var specification in options.Workers)
        {
            var workerManager = Manager(specification.Facet);
            JsonObject Principal(Address address)
            {
                return workerPrincipals.GetOrAdd(address.Key, _ => AppPrincipals.Require(options.Principal.Worker!(address)));
            }
            var component = new ProcessWorkers(specification.Kind.Kind, host, specification.Kind, new()
            {
                Executable = specification.Executable, Arguments = specification.Arguments, WorkingDirectory = specification.WorkingDirectory,
                ApplicationDirectory = specification.ApplicationDirectory, BaseDirectory = Path.Combine(Layout.WorkerBasesDirectory, specification.Kind.Kind),
                Environment = specification.Environment, Principal = Principal, ParentPrincipal = options.Principal.Server,
                Plugins = (_, ctx) => workerManager.BuildAsync(ctx.AbortSignal ?? CancellationToken.None),
                Authorize = specification.Authorize,
                LockDirectory = Layout.WorkersDirectory, IdleTimeout = specification.IdleTimeout, TimeProvider = options.TimeProvider,
                CloseTimeout = options.Timeouts.Close ?? TimeSpan.FromSeconds(30), StartTimeout = options.Timeouts.Ready ?? TimeSpan.FromSeconds(60),
                OnError = (address, error) => Report(new("workers", error, address.Kind)),
                StartData = async (address, ctx) => new JsonObject
                {
                    ["kit"] = new JsonObject
                    {
                        ["dir"] = AppLayoutTools.WorkerDirectory(Layout, address), ["facet"] = specification.Facet,
                        ["principal"] = Principal(address).DeepClone(), ["server"] = options.Principal.Server.DeepClone(),
                        ["files"] = new JsonObject { ["maxFile"] = options.Files.MaxFile, ["maxTotal"] = options.Files.MaxTotal },
                        ["telemetry"] = TelemetryTarget is not null, ["uploads"] = options.Limits.Uploads,
                        ["timeouts"] = new JsonObject { ["close"] = (options.Timeouts.Close ?? TimeSpan.FromSeconds(30)).TotalMilliseconds,
                            ["ready"] = (options.Timeouts.Ready ?? TimeSpan.FromSeconds(60)).TotalMilliseconds,
                            ["reloadDrain"] = (options.Timeouts.ReloadDrain ?? TimeSpan.FromSeconds(5)).TotalMilliseconds },
                    },
                    ["app"] = await specification.Start(address, ctx).ConfigureAwait(false),
                },
            }, life);
            workers.Add(specification.Kind.Kind, component);
        }
        files = await FilesService.OpenAsync((ChordStore)store, Layout.FilesDirectory, context, options.Files, options.Limits.Uploads, Server.Address().Key, options.TimeProvider).ConfigureAwait(false);
        var core = new List<PluginFacet>
        {
            new("chord.app.http", http.Facet), new("chord.app.sessions", sessions.Facet), new("chord.app.owner", owner.Facet!),
            new("chord.app.pairing", pairings.Facet!), new("chord.app.info", Info.AppInfoFacet(new(DataDir, options.Name, options.Api.Versions[0], options.Principal.Owner))),
            new("chord.app.files", FilesFacet.Create(files)),
            new("chord.themes", Themes.Facet()), new("chord.theme-choice", ThemeChoice.Facet()),
            new("chord.locale-choice", LocaleChoice.Facet()), new("chord.translations", SurfaceTranslations.Facet()),
        };
        foreach (var worker in workers) core.Add(new("chord.app.worker-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(worker.Key)))[..12], worker.Value.Facet));
        for (var i = 0; i < options.Surfaces.Count; i++)
        {
            var surface = options.Surfaces[i]; core.Add(new("chord.app.surface-" + i, Facet.Define((_, contribution) => contribution.Contribute(HttpRegistries.Http, surface.Contribution))));
        }
        if (TelemetryTarget is { } target) core.Add(new("chord.app.telemetry", IngestFacet.CreateFacet(new()
        {
            Target = target, TimeProvider = options.TimeProvider,
            WorkerHost = principal => workerPrincipals.FirstOrDefault(pair => AppPrincipals.Same(pair.Value, principal)).Key,
            PrincipalAttributes = principal => principal is null ? new SpanAttributes() : options.PrincipalAttributes?.Invoke(principal) ?? new SpanAttributes(),
        })));
        if (options.CoreFacets is { } additional) core.AddRange(additional(new(host, store, Layout, sessions)));
        var build = await manager.BuildAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        var loaded = await runtime.LoadAsync(build, core, context).ConfigureAwait(false);
        var coreFailure = loaded.Failed.FirstOrDefault(failed => core.Any(item => item.Name == failed.Plugin));
        if (coreFailure is not null) throw new PluginError("application core failed: " + coreFailure.Plugin + ": " + coreFailure.Error.Message);
        await sessions.AttachAsync(host, life).WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        await global::PiSharp.Chord.Hosting.App.OwnerLink.EnsureTokenFileAsync(Layout.OwnerTokenFile, options.Principal.Owner, sessions, context).ConfigureAwait(false);
        listening = await http.ListeningAsync(context).ConfigureAwait(false);
        archives = new(options.Name, options.Api.Versions[0], Layout, host, workers, new(options.Limits.ImportBytes, options.Limits.ImportEntries));
        BasesBuilt = BuildBasesAsync(); BaseWatched = WatchBasesAsync();
    }

    private async Task BuildBasesAsync()
    {
        await Task.Yield();
        var work = workers.Select(async pair =>
        {
            try { var built = await pair.Value.BuildAsync().ConfigureAwait(false); if (built.Error is not null) Report(new("base_build", built.Error, pair.Key)); }
            catch (Exception error) { Report(new("base_build", error, pair.Key)); }
        }).Concat(options.Surfaces.Select(async surface =>
        {
            try { var built = await surface.BuildAsync().ConfigureAwait(false); if (built.Error is not null) Report(new("base_build", built.Error, surface.Prefix)); }
            catch (Exception error) { Report(new("base_build", error, surface.Prefix)); }
        }));
        await Task.WhenAll(work).ConfigureAwait(false);
    }
    private async Task WatchBasesAsync()
    {
        await BasesBuilt.ConfigureAwait(false);
        try
        {
            if (lifetime.IsCancellationRequested) return;
            lock (closeGate)
            {
                if (lifetime.IsCancellationRequested) return;
                if (serverInputs is not null) baseWatches.Add(Plugins.WatchBase(serverInputs));
                foreach (var component in workers.Values)
                    if (component.Built?.Inputs is { } workerInputs) baseWatches.Add(Plugins.WatchBase(workerInputs));
                foreach (var surface in options.Surfaces)
                    if (surface.Built?.Inputs is { } surfaceInputs) baseWatches.Add(Plugins.WatchBase(surfaceInputs));
            }
        }
        catch (Exception error) { Report(new("server_scan", error)); }
    }

    public async Task<IReadOnlyList<PluginRolloutStepReport>> ApplyChangesAsync(ChordContext context)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, context.AbortSignal ?? CancellationToken.None);
        await apply.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
            var current = ChordContext.WithAbortSignal(linked.Token, context);
            var results = new List<PluginRolloutStepReport>();
            var serverBuild = await Plugins.BuildAsync(linked.Token).ConfigureAwait(false);
            results.AddRange(await Plugins.RolloutAsync(serverBuild, [new HostPluginRolloutStep("server", runtime!)], current).ConfigureAwait(false));
            if (results.Any(result => result.Stopped)) return results;
            foreach (var group in options.Workers.GroupBy(worker => worker.Facet, StringComparer.Ordinal))
            {
                var manager = managers[group.Key]; var build = ReferenceEquals(manager, Plugins) ? serverBuild : await manager.BuildAsync(linked.Token).ConfigureAwait(false);
                var report = await manager.RolloutAsync(build, group.Select(worker => (IPluginRolloutStep)workers[worker.Kind.Kind]), current).ConfigureAwait(false);
                results.AddRange(report); if (report.Any(result => result.Stopped)) return results;
            }
            foreach (var surface in options.Surfaces)
                try { await surface.ReloadAsync(linked.Token).ConfigureAwait(false); }
                catch (Exception error) { Report(new("surface", error, surface.Prefix)); }
            return results;
        }
        finally { apply.Release(); }
    }
    public Task ExportDataAsync(Address address, string path, ChordContext context) => (archives ?? throw new InvalidOperationException("server has not started")).ExportAsync(address, path, context);
    public Task ImportDataAsync(string archive, Address address, ChordContext context, DataImportOptions? options = null) => (archives ?? throw new InvalidOperationException("server has not started")).ImportAsync(archive, address, context, options);
    public Task DeleteDataAsync(Address address, ChordContext context) => (archives ?? throw new InvalidOperationException("server has not started")).DeleteAsync(address, context);

    public Task CloseAsync(ChordContext context)
    {
        Task task; lock (closeGate) task = close ??= CloseCoreAsync();
        return context.AbortSignal is { } signal ? task.WaitAsync(signal) : task;
    }
    private async Task CloseCoreAsync()
    {
        await Task.Yield(); await lifetime.CancelAsync().ConfigureAwait(false);
        var errors = new List<Exception>();
        async Task Cleanup(Func<Task> work) { try { await work().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } }
        if (http is not null) await Cleanup(() => http.CloseAsync(ChordContext.Background)).ConfigureAwait(false);
        foreach (var surface in options.Surfaces) await Cleanup(() => surface.DisposeAsync().AsTask()).ConfigureAwait(false);
        foreach (var worker in workers.Values) await Cleanup(() => worker.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Cleanup(() => apply.WaitAsync()).ConfigureAwait(false);
        if (sessions is not null) await Cleanup(() => sessions.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (host is not null) await Cleanup(() => host.CloseAsync(ChordContext.Background)).ConfigureAwait(false);
        // Public host deadlines may release callers before their originals settle. Keep the store and lock owned.
        await Cleanup(() => callbacks.Drained).ConfigureAwait(false);
        callbackRegistration?.Dispose();
        if (runtime is not null) await Cleanup(() => runtime.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (files is not null) await Cleanup(() => files.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (store is not null) await Cleanup(() => store.CloseAsync(ChordContext.Background)).ConfigureAwait(false);
        await Cleanup(() => BaseWatched).ConfigureAwait(false);
        foreach (var watch in baseWatches) try { watch.Dispose(); } catch (Exception error) { errors.Add(error); }
        foreach (var manager in managers.Values) await Cleanup(() => manager.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (directory is not null) await Cleanup(() => directory.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (errors.Count > 0) throw new AggregateException("application cleanup failed", errors);
    }
    private void Report(AppError error) { try { options.OnError(error); } catch { } }
    public ValueTask DisposeAsync() => new(CloseAsync(ChordContext.Background));
}

/// <summary>Counts actual callback/resource leases independently of cancellable public waits.</summary>
internal sealed class CallbackOwnership
{
    private readonly object gate = new();
    private int count;
    private TaskCompletionSource drained = Completed();
    internal Task Drained { get { lock (gate) return drained.Task; } }
    internal IDisposable Acquire()
    {
        lock (gate)
        {
            if (count++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this);
        }
    }
    private void Release()
    {
        TaskCompletionSource? finished = null;
        lock (gate) { if (--count == 0) finished = drained; }
        finished?.TrySetResult();
    }
    private static TaskCompletionSource Completed()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    private sealed class Lease(CallbackOwnership owner) : IDisposable
    {
        private CallbackOwnership? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
