using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>The local legacy Chord kernel: placement, ownership, readiness, roots, registries and retained generations.</summary>
public sealed partial class LegacyHost : IAsyncDisposable, ILegacyReloadHost, ILegacyFacetLifetimeHost
{
    // The dispatcher is an implementation-neutral FIFO; all transitions and continuations run on it.
    private readonly Host.HostDispatcher _dispatcher = new();
    internal readonly LegacyHostOptions Options;
    internal readonly Dictionary<string, LegacyObject> Objects = [];
    internal readonly Dictionary<string, LegacyFacet> Facets = [];
    internal readonly Dictionary<string, CancellationTokenSource> FacetRuns = [];
    internal readonly Dictionary<int, LegacyGeneration> Generations = [];
    internal LegacyGeneration Current;
    private readonly Dictionary<string, HashSet<Action>> _registryListeners = [];
    private readonly Dictionary<string, LegacyReference> _hostReferences = [];
    private readonly Dictionary<LegacyObject, HashSet<LegacyObject>> _dependencies = [];
    private readonly HashSet<LegacyContext> _roots = [];
    private readonly Dictionary<string, HashSet<LegacyObject>> _leases = [];
    private readonly CancellationTokenSource _close = new();
    private Task? _closing;
    internal bool InFactory;
    private long _rootSequence;
    private int _retireAllowed;
    private int _publicationDepth;
    private bool _publishing;
    private readonly Queue<Action> _publications = [];
    internal LegacyPreparedReload? PendingReload;
    internal bool LoadingReload;
    public LegacyHost(LegacyHostOptions options)
    {
        Options = options; ArgumentException.ThrowIfNullOrEmpty(options.Name);
        if (options.Kind is not ("host" or "client")) throw new ArgumentException("kind must be host or client");
        Current = new(1, []); Generations.Add(1, Current);
    }
    public string Name => Options.Name;
    public string Kind => Options.Kind;
    public int CodeVersion => Invoke(() => Current.Id);
    public int? PreviousCodeVersion => Invoke(() => Generations.Keys.Where(x => x != Current.Id).Select(x => (int?)x).Max());
    public bool Reloading => Invoke(() => PendingReload is not null || LoadingReload);
    public int HighestRetiredCodeVersion { get; private set; }
    public event Action<int>? Retired;
    public bool IsFacetInstalled(LegacyFacet facet) => Invoke(() => Facets.TryGetValue(facet.Name, out var installed) && ReferenceEquals(installed, facet));
    internal bool Dev => Options.Dev;
    internal T Invoke<T>(Func<T> action) => _dispatcher.Invoke(action);
    internal void Invoke(Action action) => _dispatcher.Invoke(action);
    internal Task<T> RunAsync<T>(Func<Task<T>> action) => _dispatcher.RunAsync(action);
    internal Task RunAsync(Func<Task> action) => _dispatcher.RunAsync(action);
    internal void Fire(Func<Task> action) => Observe(RunAsync(action));
    internal void Observe(Task task) { _ = task.ContinueWith(t => Report(t.Exception!.GetBaseException()), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); }
    internal void Report(Exception error) { try { Options.OnError?.Invoke(error); } catch { /* The error reporter may not break lifetime cleanup. */ } }
    internal void Publish(Action action)
    { if (_publicationDepth > 0 || _publishing) _publications.Enqueue(action); else action(); }
    internal IDisposable BatchPublication()
    {
        _publicationDepth++;
        return new LegacySubscription(() =>
        {
            if (--_publicationDepth != 0 || _publishing) return;
            _publishing = true;
            try { while (_publications.TryDequeue(out var publication)) publication(); }
            finally { _publishing = false; }
        });
    }
    internal bool HasCodeVersion(int version) => Invoke(() => Generations.ContainsKey(version));
    internal void CheckOpen() { if (_close.IsCancellationRequested) throw new ChordException("gone", "host is closed"); }
    internal string PrincipalKey(object? principal) => Options.PrincipalKey?.Invoke(principal) ?? LegacySchema.CanonicalJson(principal as JsonNode ?? JsonSerializer.SerializeToNode(principal));
    internal string AddressOf(LegacyContract contract, JsonNode? input, LegacyGetOptions? options, out JsonNode? normalized, out object? principal)
    {
        normalized = LegacySchema.Normalize(contract.RawInput, input, contract.Name + " input");
        principal = options is { HasPrincipal: true } || options?.Principal is not null ? options.Principal : Options.Self;
        var host = contract.Placement?.Resolve(contract.Name, normalized) ?? Name;
        string Part(object? value) => PrincipalKey(value) == PrincipalKey(Options.Self) ? "host" : "p:" + PrincipalKey(value);
        if (host != Name) return "@" + contract.Name + "\0" + LegacySchema.Identity(contract.RawInput, normalized) + "\0" + Part(principal) + "\0" + contract.Fingerprint;
        var localService = InFactory ? PendingReload?.FindService(contract.Name) ?? Current.Services.GetValueOrDefault(contract.Name) : Current.Services.GetValueOrDefault(contract.Name);
        if (localService is { } service)
        {
            normalized = LegacySchema.Normalize(service.Contract.RawInput, input, contract.Name + " input");
            var identity = LegacySchema.Identity(service.Contract.RawInput, normalized);
            if (service.Options.Lifetime == "host")
            {
                var empty = LegacySchema.Normalize(service.Contract.RawInput, new JsonObject(), contract.Name + " input");
                if (identity != LegacySchema.Identity(service.Contract.RawInput, empty)) throw ChordException.Invalid("input", contract.Name + " has host lifetime: its only address has empty input");
                principal = Options.Self;
            }
            return contract.Name + "\0" + identity + "\0" + Part(principal);
        }
        return contract.Name + "\0" + LegacySchema.Identity(contract.RawInput, normalized) + "\0" + Part(principal);
    }
    public void Serve(LegacyContract contract, LegacyFactory factory, LegacyServeOptions? options = null) => Invoke(() =>
    { CheckOpen(); if (Reloading) throw new ChordException("unavailable", "serve during reload", "reload_running"); AddService(Current.Services, new(contract, factory, options ?? new(), "")); StartHostObjects(); });
    internal void AddService(Dictionary<string, LegacyService> services, LegacyService service)
    {
        if (services.ContainsKey(service.Contract.Name)) throw ChordException.Invalid("duplicate", service.Contract.Name + " is already served");
        var options = service.Options;
        if (options.Lifetime is not ("referenced" or "host")) throw new ArgumentException("unknown object lifetime");
        if (service.Contract.Placement is { } placement && !placement.Allows(Name)) throw new ArgumentException(service.Contract.Name + " is placed on another host");
        if (options.Lifetime == "host")
        {
            if (service.Contract.Scope != "local" || options.Linger is not null) throw new ArgumentException("host lifetime requires a local type without linger");
            LegacySchema.Normalize(service.Contract.RawInput, new JsonObject(), service.Contract.Name + " host input");
        }
        services.Add(service.Contract.Name, service);
    }
    public Task LoadAsync(IReadOnlyList<LegacyFacet> facets, LegacyLoadOptions? options = null) => RunAsync(async () =>
    {
        CheckOpen(); if (Reloading) throw new ChordException("unavailable", "load during reload", "reload_running");
        // All declarations publish before any host-object activation can await another facet.
        var services = new Dictionary<string, LegacyService>(Current.Services);
        var signals = new List<(LegacyFacet Facet, CancellationTokenSource Signal)>();
        try
        {
            foreach (var facet in facets)
            {
                if (Facets.ContainsKey(facet.Name) || signals.Any(x => x.Facet.Name == facet.Name)) throw ChordException.Invalid("duplicate", "facet already installed: " + facet.Name);
                var signal = new CancellationTokenSource(); signals.Add((facet, signal)); var open = true;
                facet.Setup(new(this, (type, factory, serveOptions) => { if (!open) throw ChordException.Invalid("declaration", "serve after facet setup"); AddService(services, new(type, factory, serveOptions ?? new(), facet.Name)); }, signal.Token)); open = false;
            }
        }
        catch { foreach (var (_, signal) in signals) { signal.Cancel(); signal.Dispose(); } throw; }
        Current.Services.Clear(); foreach (var entry in services) Current.Services.Add(entry.Key, entry.Value);
        foreach (var (facet, signal) in signals) { Facets[facet.Name] = facet; FacetRuns[facet.Name] = signal; }
        StartHostObjects(); if (options?.AwaitHostObjects != false) await ReadyAsync();
    });
    public Task InstallAsync(LegacyFacet facet, LegacyLoadOptions? options = null) => LoadAsync([facet], options);
    public Task LoadModulesAsync(IReadOnlyList<string> modules, LegacyLoadOptions? options = null, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        CheckOpen(); if (modules.Count == 0) return;
        if (Options.Loader is null) throw new InvalidOperationException("module loading needs LegacyHostOptions.Loader");
        var facets = await Options.Loader(modules, cancellationToken); cancellationToken.ThrowIfCancellationRequested(); await LoadAsync(facets, options);
    });
    internal void StartHostObjects()
    {
        foreach (var service in Current.Services.Values.Where(s => s.Options.Lifetime == "host"))
            if (!_hostReferences.TryGetValue(service.Contract.Name, out var previous) || previous.Contract != service.Contract)
            {
                _hostReferences[service.Contract.Name] = Acquire(service.Contract, new JsonObject(), null, null);
                previous?.Release();
            }
        foreach (var name in _hostReferences.Keys.Where(name => !Current.Services.TryGetValue(name, out var service) || service.Options.Lifetime != "host").ToArray())
        { _hostReferences[name].Release(); _hostReferences.Remove(name); }
    }
    internal bool IsHostReference(LegacyReference reference) => _hostReferences.Values.Contains(reference);
    public Task ReadyAsync() => RunAsync(async () => { foreach (var reference in _hostReferences.Values.ToArray()) await AwaitReady(reference, _close.Token); });
    public LegacyReference Hold(LegacyContract contract, JsonNode? input, LegacyGetOptions? options = null) => Invoke(() => Acquire(contract, input, options, null));
    internal LegacyReference Acquire(LegacyContract contract, JsonNode? input, LegacyGetOptions? options, LegacyObject? from)
    {
        CheckOpen(); var address = AddressOf(contract, input, options, out var normalized, out var principal);
        if (GatedTypes.Contains(contract.Name) && !InFactory) throw new ChordException("upgrade_conflict", "type is behind the reload gate");
        if (contract.Placement is { } remotePlacement && remotePlacement.Resolve(contract.Name, normalized) != Name)
            return AcquireRemote(contract, normalized, principal, options?.State != false, address);
        var service = (InFactory ? PendingReload?.FindService(contract.Name) : null) ?? Current.Services.GetValueOrDefault(contract.Name);
        if (service is null) throw new ChordException("unavailable", "unknown type " + contract.Name, "unknown_type");
        if (contract.Placement is { } placement && placement.Resolve(contract.Name, normalized) != Name) throw new ChordException("unavailable", "no local route for " + placement.Resolve(contract.Name, normalized), "route");
        LegacyContract.Bind(service.Contract, service.Options.Accepts, contract);
        if (!Objects.TryGetValue(address, out var owner))
        {
            owner = new(this, address, normalized, principal); Objects[address] = owner;
            if (InFactory && PendingReload is { } reload) reload.TrackNew(owner);
            var activation = RunAsync(() => owner.ActivateAsync(service));
            TrackWork(owner.ActivationVersion ?? Current.Id, activation); Observe(activation);
        }
        if (from is not null)
        {
            bool Reaches(LegacyObject start, HashSet<LegacyObject> seen) => ReferenceEquals(start, from) || seen.Add(start) && _dependencies.TryGetValue(start, out var deps) && deps.Any(x => Reaches(x, seen));
            if (Reaches(owner, [])) throw ChordException.Invalid("cycle", "local reference cycle");
            if (!_dependencies.TryGetValue(from, out var targets)) _dependencies[from] = targets = []; targets.Add(owner);
        }
        var reference = new LegacyReference(this, owner, contract, options?.State != false) { PendingBinding = InFactory && PendingReload is not null }; owner.Add(reference); reference.Changed();
        if (owner.Error is { } error) reference.Gone(error); return reference;
    }
    public Task<LegacyReference> GetAsync(LegacyContract contract, JsonNode? input, LegacyGetOptions? options = null, CancellationToken cancellationToken = default) => RunAsync(async () =>
    { var reference = Acquire(contract, input, options, null); try { await AwaitReady(reference, cancellationToken); return reference; } catch { reference.Release(); throw; } });
    internal async Task AwaitReady(LegacyReference reference, CancellationToken cancellationToken)
    {
        try { await reference.Ready.WaitAsync(Options.ReadyTimeout, cancellationToken); }
        catch (TimeoutException error) { throw new ChordException("unavailable", "reference readiness timed out", "ready_timeout", cause: error); }
        catch (OperationCanceledException error) { throw new ChordException("aborted", "reference wait aborted", cause: error); }
    }
    public Task<T> RootAsync<T>(Func<LegacyContext, Task<T>> work, LegacyRootOptions? options = null) => RunAsync(async () =>
    {
        CheckOpen(); var version = options?.CodeVersion ?? Current.Id;
        if (!Generations.TryGetValue(version, out var generation)) throw new ChordException("superseded", "code version is retired");
        var principal = options is { HasPrincipal: true } || options?.Principal is not null ? options.Principal : Options.Self;
        using var context = new LegacyContext(this, Name + ":" + Interlocked.Increment(ref _rootSequence), version, principal, _close.Token, options?.CancellationToken ?? default, generation.Abort.Token);
        generation.Roots++; _roots.Add(context);
        var settlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); TrackWork(version, settlement.Task);
        try { return await work(context); }
        finally { context.Dispose(); _roots.Remove(context); generation.Roots--; _leases.Remove(context.RootId); EndRootLinks(context.RootId); settlement.TrySetResult(); TryRetire(); }
    });
    internal void Lease(LegacyObject owner, LegacyContext context)
    {
        if (!_leases.TryGetValue(context.RootId, out var objects)) _leases[context.RootId] = objects = [];
        if (GatedTypes.Contains(owner.Current!.Service.Contract.Name) && !objects.Contains(owner)) throw new ChordException("upgrade_conflict", "new work cannot enter a reload gate");
        objects.Add(owner);
    }
    internal bool GateBusy(IReadOnlySet<string> addresses) => _leases.Values.Any(objects => objects.Any(owner => addresses.Contains(owner.Address)));
    public LegacyRegistryView<T> Registry<T>(LegacyRegistry<T> registry) => new(this, registry);
    internal IReadOnlyDictionary<string, T> ReadRegistry<T>(string name, LegacyContext? context) => Invoke(() =>
    { context?.Check(this); var generation = context is null ? Current : Generations[context.CodeVersion]; return generation.Tables.TryGetValue(name, out var table) ? table.ToDictionary(x => x.Key, x => (T)x.Value!) : new Dictionary<string, T>(); });
    internal IDisposable SubscribeRegistry(string name, Action callback) => Invoke(() =>
    { if (!_registryListeners.TryGetValue(name, out var list)) _registryListeners[name] = list = []; list.Add(callback); callback(); return new LegacySubscription(() => Invoke(() => list.Remove(callback))); });
    internal void CheckRegistrations(LegacyObject owner, LegacyRun run) => BuildTables(Current.Id, owner, run);
    internal Dictionary<string, Dictionary<string, object?>> BuildTables(int version, LegacyObject? replacing = null, LegacyRun? candidate = null)
    {
        var tables = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var owner in Objects.Values)
        {
            if (owner.Deferred && !ReferenceEquals(owner, replacing)) continue;
            var run = ReferenceEquals(owner, replacing) ? candidate : owner.Runs.GetValueOrDefault(version) ?? (version == Current.Id ? owner.Current : null);
            if (run is null || owner.Status != "live" && !ReferenceEquals(owner, replacing)) continue;
            foreach (var entry in run.Registrations)
            { if (!tables.TryGetValue(entry.Registry, out var table)) tables[entry.Registry] = table = []; if (table.ContainsKey(entry.Key) && entry.Conflict == "error") throw ChordException.Invalid("registry_conflict", entry.Registry + ": duplicate key " + entry.Key); table[entry.Key] = entry.Value; }
        }
        return tables;
    }
    internal void RegistriesChanged()
    { Current.Tables = BuildTables(Current.Id); Publish(() => { foreach (var list in _registryListeners.Values) foreach (var listener in list.ToArray()) try { listener(); } catch (Exception error) { Report(error); } }); }
    internal void Remove(LegacyObject owner) { if (Objects.GetValueOrDefault(owner.Address) == owner) Objects.Remove(owner.Address); RemoteActive(owner); _dependencies.Remove(owner); foreach (var targets in _dependencies.Values) targets.Remove(owner); }
    public Task InvalidatePrincipalAsync(Func<object?, bool> match) => RunAsync(async () =>
    {
        foreach (var owner in Objects.Values.Where(x => PrincipalKey(x.Principal) != PrincipalKey(Options.Self) && match(x.Principal)).ToArray()) await owner.DisposeAsync(new ChordException("gone", "principal invalidated"));
        bool Revoked(System.Text.Json.Nodes.JsonNode? principal) => PrincipalKey(principal) != PrincipalKey(Options.Self) && match(principal);
        Network?.Invalidate(Revoked);
        foreach (var session in Sessions.Values) session.Revoke(Revoked);
    });
    public Task InvalidatePrincipalAsync(JsonObject pattern) => RunAsync(async () =>
    {
        bool Matches(object? principal)
        {
            var json = PrincipalJson(principal) as JsonObject;
            return json is not null && pattern.All(x => json.TryGetPropertyValue(x.Key, out var value) && LegacySchema.Equal(value, x.Value));
        }
        await InvalidatePrincipalAsync(Matches);
        foreach (var session in Sessions.Values.Where(s => s.Connection.Role == "downstream" && s.Connection.PeerKind == "host"))
            session.Connection.Send(LegacyProtocol.Frame("invalidate", ("pattern", pattern)));
    });
    public Task CloseAsync() => Invoke(() => _closing ??= RunAsync(async () =>
    {
        try { _close.Cancel(); } catch (Exception error) { Report(error); }
        try { Network?.Dispose(); } catch (Exception error) { Report(error); }
        foreach (var signal in FacetRuns.Values) try { signal.Cancel(); } catch (Exception error) { Report(error); }
        if (PendingReload is { } reload) await reload.RollbackAsync();
        foreach (var owner in Objects.Values.ToArray()) Observe(owner.DisposeAsync());
        _hostReferences.Clear(); foreach (var root in _roots.ToArray()) root.Dispose();
        foreach (var generation in Generations.Values.ToArray()) RetireGeneration(generation);
        Facets.Clear(); foreach (var signal in FacetRuns.Values) signal.Dispose(); FacetRuns.Clear();
        foreach (var session in Sessions.Values.ToArray()) session.Dispose(); Sessions.Clear(); Network = null;
        RootLinks.Clear(); ForeignRoots.Clear(); _leases.Clear(); RemoteLingers.Clear(); _registryListeners.Clear(); _dependencies.Clear();
        try { await Task.WhenAll(Retirements.Values.Select(retirement => retirement.Completion.Task)).WaitAsync(Options.TaskStopDeadline); }
        catch (TimeoutException error) { Report(new ChordException("unavailable", "host cleanup continues after the close deadline", "drain_timeout", cause: error)); }
    }));
    public async ValueTask DisposeAsync() => await CloseAsync();
    internal async Task DisposeUninstalled(LegacyRun run, LegacyRun? previous)
    {
        var errors = new List<Exception>();
        try { run.Stop.Cancel(); } catch (Exception error) { errors.Add(error); }
        try { await Task.WhenAll(run.RunningTasks); } catch (Exception) { /* Tasks report their failures through their owners. */ }
        foreach (var subscription in run.Subscriptions) try { subscription.Dispose(); } catch (Exception error) { errors.Add(error); }
        foreach (var derived in run.Derivations) try { derived.Dispose(); } catch (Exception error) { errors.Add(error); }
        // Native resources must finish before their dependencies and delegates are released.
        foreach (var resource in run.Resources.Values.Reverse())
            if (previous?.Resources.Values.Contains(resource) != true) try { await resource.CloseAsync(); } catch (Exception error) { errors.Add(error); }
        foreach (var reference in run.References.Values)
            if (previous?.References.Values.Contains(reference) != true) try { reference.Release(); } catch (Exception error) { errors.Add(error); }
        run.Stop.Dispose();
        run.Subscriptions.Clear(); run.Derivations.Clear(); run.References.Clear(); run.Resources.Clear(); run.ResourceSpecs.Clear(); run.Tasks.Clear(); run.RunningTasks.Clear(); run.Listeners.Clear(); run.EventListeners.Clear(); run.Registrations.Clear(); run.Initializers.Clear();
        run.DerivedValues.Clear(); run.Migrated.Clear(); run.Cells.Clear(); run.CellSchemas.Clear();
        run.Implementation = new(new Dictionary<string, object?>()); run.Service = null!;
        foreach (var error in errors) Report(error);
    }
    internal void TryRetire()
    {
        foreach (var generation in Generations.Values.Where(g => g.Id != Current.Id && g.Roots == 0 && (Kind == "client" || Network?.Upstream is null || g.Id <= _retireAllowed)).ToArray()) RetireGeneration(generation);
    }
    public void AllowRetirement(int codeVersion) => Invoke(() => { _retireAllowed = Math.Max(_retireAllowed, codeVersion); TryRetire(); });
}
