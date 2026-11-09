namespace PiSharp.Chord.Legacy;

public sealed partial class LegacyHost
{
    internal readonly HashSet<string> GatedTypes = [];
    public Task<LegacyReloadReport> ReloadAsync(IReadOnlyList<LegacyFacet> facets, LegacyReloadOptions? options = null) => RunAsync(async () =>
    {
        if (Network is not null) return await Network.Cluster.ReloadPreparedAsync(async () => await PrepareReloadAsync(facets, options), options);
        var reload = await PrepareReloadAsync(facets, options);
        try { await reload.WaitForRetirementAsync(); await reload.PrepareAsync(); return reload.Commit(); }
        catch { await reload.RollbackAsync(); throw; }
        finally { EndReload(); }
    });
    public Task<LegacyPreparedReload> PrepareReloadAsync(IReadOnlyList<LegacyFacet> facets, LegacyReloadOptions? options = null, int? codeVersion = null) => RunAsync(() =>
    {
        CheckOpen(); if (PendingReload is not null || LoadingReload) throw new ChordException("unavailable", "a reload is already running", "reload_running");
        if (codeVersion is not null && codeVersion <= Current.Id) throw new ArgumentException("code version must increase");
        var reload = new LegacyPreparedReload(this, facets, options ?? new(), codeVersion ?? Current.Id + 1); PendingReload = reload; return Task.FromResult(reload);
    });
    public Task<ILegacyPreparedReload> BeginReloadAsync(IReadOnlyList<string> modules, LegacyReloadOptions options, int? codeVersion = null, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        CheckOpen(); if (PendingReload is not null || LoadingReload) throw new ChordException("unavailable", "a reload is already running", "reload_running");
        LoadingReload = true;
        try
        {
            if (modules.Count > 0 && Options.Loader is null) throw new InvalidOperationException("module reload needs LegacyHostOptions.Loader");
            IReadOnlyList<LegacyFacet> facets = modules.Count == 0 ? [] : await Options.Loader!(modules, cancellationToken);
            CheckOpen(); cancellationToken.ThrowIfCancellationRequested();
            if (codeVersion is not null && codeVersion <= Current.Id) throw new ArgumentException("code version must increase");
            var reload = new LegacyPreparedReload(this, facets, options, codeVersion ?? Current.Id + 1); PendingReload = reload; return (ILegacyPreparedReload)reload;
        }
        finally { LoadingReload = false; }
    });
    public void EndReload() => Invoke(() => { PendingReload = null; LoadingReload = false; GatedTypes.Clear(); });
}

/// <summary>A two-phase reload. Preparing may fail without publishing; commit publishes the candidate generation once.</summary>
public sealed class LegacyPreparedReload : ILegacyPreparedReload
{
    private readonly LegacyHost _host;
    private readonly IReadOnlyList<LegacyFacet> _facets;
    private readonly LegacyReloadOptions _options;
    private readonly int _version;
    private readonly int _previous;
    private readonly Dictionary<string, LegacyService> _services;
    private readonly Dictionary<string, CancellationTokenSource> _signals = [];
    private readonly Dictionary<LegacyObject, LegacyRun> _candidates = [];
    private readonly List<LegacyObject> _removed = [];
    private readonly List<LegacyResourceCore> _closed = [];
    private readonly List<(LegacyObject Owner, LegacyRun Run)> _stopped = [];
    private readonly HashSet<string> _changed = [];
    private readonly HashSet<string> _changedTypes = [];
    private readonly HashSet<string> _removedTypes = [];
    private readonly HashSet<string> _gated = [];
    private readonly List<LegacyObject> _newObjects = [];
    private readonly List<LegacyReference> _preparedHolds = [];
    private bool _prepared, _committed, _rolledBack;
    internal LegacyPreparedReload(LegacyHost host, IReadOnlyList<LegacyFacet> facets, LegacyReloadOptions options, int version)
    { _host = host; _facets = facets; _options = options; _version = version; _previous = host.Current.Id; _services = new(host.Current.Services); }
    internal LegacyService? FindService(string name) => _services.GetValueOrDefault(name);
    internal void TrackNew(LegacyObject owner) { owner.Deferred = true; owner.ActivationVersion = _version; _newObjects.Add(owner); }
    public Task WaitForRetirementAsync(CancellationToken cancellationToken = default) => _host.RunAsync(async () =>
    {
        var deadline = DateTime.UtcNow + (_options.RetireDeadline ?? TimeSpan.FromSeconds(5));
        while (_host.Generations.Keys.Any(v => v != _host.Current.Id))
        {
            cancellationToken.ThrowIfCancellationRequested(); _host.TryRetire();
            if (DateTime.UtcNow >= deadline)
            {
                foreach (var generation in _host.Generations.Values.Where(g => g.Id != _host.Current.Id)) generation.Abort.Cancel();
                var grace = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Min(1000, (_options.RetireDeadline ?? TimeSpan.FromSeconds(5)).TotalMilliseconds));
                while (_host.Generations.Values.Any(g => g.Id != _host.Current.Id && g.Roots > 0) && DateTime.UtcNow < grace) await Task.Delay(10, cancellationToken);
                foreach (var generation in _host.Generations.Values.Where(g => g.Id != _host.Current.Id).ToArray()) _host.RetireGeneration(generation);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
        await _host.WaitForRetirementCleanupAsync(_options.RetireDeadline ?? TimeSpan.FromSeconds(5), cancellationToken);
    });
    public Task PrepareAsync(CancellationToken cancellationToken = default) => _host.RunAsync(async () =>
    {
        if (_prepared) return;
        if (_committed || _rolledBack) throw new InvalidOperationException("reload already settled");
        _host.CheckOpen();
        try
        {
            foreach (var name in _options.Remove) { foreach (var service in _services.Values.Where(s => s.Facet == name).ToArray()) _services.Remove(service.Contract.Name); }
            foreach (var facet in _facets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_host.Facets.TryGetValue(facet.Name, out var prior) && ReferenceEquals(prior, facet)) continue;
                if (!_changed.Add(facet.Name)) throw ChordException.Invalid("duplicate", "duplicate facet " + facet.Name);
                foreach (var service in _services.Values.Where(s => s.Facet == facet.Name).ToArray()) _services.Remove(service.Contract.Name);
                var signal = new CancellationTokenSource(); _signals.Add(facet.Name, signal); var open = true;
                facet.Setup(new(_host, (contract, factory, options) => { if (!open) throw ChordException.Invalid("declaration", "serve after facet setup"); _host.AddService(_services, new(contract, factory, options ?? new(), facet.Name)); }, signal.Token)); open = false;
            }
            foreach (var (name, service) in _services) if (!ReferenceEquals(service, _host.Current.Services.GetValueOrDefault(name))) _changedTypes.Add(name);
            foreach (var name in _host.Current.Services.Keys) if (!_services.ContainsKey(name)) _removedTypes.Add(name);
            var facets = _host.Facets.Where(f => !_options.Remove.Contains(f.Key) && !_changed.Contains(f.Key)).Select(x => x.Value).Concat(_facets.Where(f => _changed.Contains(f.Name)));
            foreach (var facet in facets)
                foreach (var uses in facet.Uses ?? [])
                { if (!_services.TryGetValue(uses.Name, out var service)) throw new ChordException("contract_mismatch", "missing provider for " + uses.Name); LegacyContract.Bind(service.Contract, service.Options.Accepts, uses); }
            foreach (var service in _services.Values.Where(service => service.Options.Lifetime == "host" && !_host.Objects.Values.Any(owner => owner.Current?.Service.Contract.Name == service.Contract.Name)))
            {
                _host.InFactory = true;
                try { _preparedHolds.Add(_host.Acquire(service.Contract, new System.Text.Json.Nodes.JsonObject(), null, null)); }
                finally { _host.InFactory = false; }
            }
            foreach (var held in _preparedHolds) await _host.AwaitReady(held, cancellationToken);
            foreach (var owner in _host.Objects.Values.ToArray())
            {
                if (owner.RemoteContract is not null || owner.Deferred) continue;
                if (owner.Current is not { } old) continue;
                if (!_services.TryGetValue(old.Service.Contract.Name, out var service)) { _removed.Add(owner); _gated.Add(owner.Address); _host.GatedTypes.Add(old.Service.Contract.Name); continue; }
                if (ReferenceEquals(service, old.Service)) continue;
                foreach (var reference in owner.References) if (!_host.IsHostReference(reference)) LegacyContract.Bind(service.Contract, service.Options.Accepts, reference.Contract);
                var run = owner.Declare(service, old); _candidates.Add(owner, run);
                if (run.Resources.Any(r => old.Resources.TryGetValue(r.Key, out var resource) && !ReferenceEquals(resource, r.Value) && (resource.Exclusive || r.Value.Exclusive)) || run.CellSchemas.Any(c => old.CellSchemas.TryGetValue(c.Key, out var prior) && (prior.Options.Version != c.Value.Options.Version || !LegacySchema.Equal(prior.Schema, c.Value.Schema))))
                { _gated.Add(owner.Address); _host.GatedTypes.Add(service.Contract.Name); }
                await owner.PrepareAsync(run, cancellationToken, true);
            }
            if (_gated.Count > 0)
            {
                foreach (var owner in _candidates.Keys.Concat(_removed).Where(owner => _gated.Contains(owner.Address)))
                {
                    var previous = owner.Current!; owner.StopRuntime(previous); _stopped.Add((owner, previous));
                    try { await Task.WhenAll(previous.RunningTasks).WaitAsync(_host.Options.TaskStopDeadline, cancellationToken); }
                    catch (TimeoutException error) { _host.Report(new ChordException("unavailable", "tasks did not stop before reload gate", "task_stop_deadline", cause: error)); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested) { _host.Report(error); }
                }
                var deadline = DateTime.UtcNow + (_options.GateDeadline ?? TimeSpan.FromSeconds(5));
                while (_host.GateBusy(_gated))
                { if (DateTime.UtcNow >= deadline) throw new ChordException("upgrade_conflict", "reload gate deadline expired"); await Task.Delay(10, cancellationToken); }
                foreach (var (owner, run) in _candidates)
                    foreach (var (name, resource) in run.Resources)
                        if (!resource.IsOpen)
                        {
                            if (owner.Current!.Resources.TryGetValue(name, out var old) && old.IsOpen) { await old.CloseAsync(); _closed.Add(old); }
                            await resource.OpenAsync(cancellationToken);
                        }
            }
            // Compose the complete prospective table before adoption. No observer can see a partial table.
            foreach (var owner in _newObjects.ToArray()) await owner.Ready.Task.WaitAsync(_host.Options.ReadyTimeout, cancellationToken);
            var all = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var owner in _host.Objects.Values)
            {
                if (_removed.Contains(owner)) continue;
                var run = _candidates.GetValueOrDefault(owner) ?? owner.Current; if (run is null) continue;
                foreach (var entry in run.Registrations)
                { if (!all.TryGetValue(entry.Registry, out var table)) all[entry.Registry] = table = []; if (table.ContainsKey(entry.Key) && entry.Conflict == "error") throw ChordException.Invalid("registry_conflict", entry.Registry + ": duplicate key " + entry.Key); table[entry.Key] = entry.Value; }
            }
            _prepared = true;
        }
        catch (Exception error)
        {
            await RollbackAsync(); var cause = ChordException.Wrap(error, "reload"); throw new ChordException(cause.Code, "reload aborted: " + cause.Message, cause.Reason, cause.Details, cause);
        }
    });
    public LegacyReloadReport Summary() => new() { CodeVersion = _version, Previous = _previous, Changed = _changedTypes.ToArray(), Removed = _removedTypes.ToArray(), Prepared = _candidates.Keys.Concat(_newObjects).Select(x => x.Address).ToArray(), Gated = _gated.ToArray() };
    public LegacyReloadReport Commit(Action? beforeNewWork = null) => _host.Invoke(() =>
    {
        if (!_prepared || _committed || _rolledBack) throw new InvalidOperationException("reload is not prepared"); _host.CheckOpen();
        using var publication = _host.BatchPublication();
        var generation = new LegacyGeneration(_version, _services);
        var stopping = new Dictionary<LegacyObject, Task>();
        foreach (var (owner, run) in _candidates)
        { stopping[owner] = Task.WhenAll(owner.Current!.RunningTasks); owner.StopRuntime(owner.Current!); owner.Install(run, _version); }
        foreach (var owner in _host.Objects.Values) if (!_candidates.ContainsKey(owner) && !_removed.Contains(owner) && owner.Current is not null) owner.Runs[_version] = owner.Current;
        _host.Generations.Add(_version, generation); _host.Current = generation;
        foreach (var owner in _newObjects) owner.Deferred = false;
        foreach (var removed in _options.Remove)
        { _host.Facets.Remove(removed); if (_host.FacetRuns.Remove(removed, out var signal)) signal.Cancel(); }
        foreach (var facet in _facets.Where(f => _changed.Contains(f.Name)))
        { _host.Facets[facet.Name] = facet; if (_host.FacetRuns.Remove(facet.Name, out var oldSignal)) oldSignal.Cancel(); _host.FacetRuns[facet.Name] = _signals[facet.Name]; }
        _committed = true; _host.GatedTypes.Clear(); _host.PendingReload = null;
        foreach (var owner in _removed)
        {
            owner.Status = "retiring"; var error = new ChordException("gone", "provider removed by reload");
            foreach (var reference in owner.References.ToArray()) reference.Gone(error);
            _host.Observe(owner.DisposeAsync(error));
        }
        _host.RegistriesChanged(); foreach (var owner in _candidates.Keys) foreach (var reference in owner.References.ToArray()) reference.Changed();
        beforeNewWork?.Invoke();
        foreach (var (owner, run) in _candidates) owner.StartRuntime(run, stopping[owner]);
        foreach (var owner in _newObjects) if (owner.Current is { } run) owner.StartRuntime(run);
        _host.StartHostObjects(); foreach (var reference in _preparedHolds) reference.Release(); _host.TryRetire(); return Summary();
    });
    public Task RollbackAsync() => _host.RunAsync(async () =>
    {
        if (_rolledBack || _committed) return; _rolledBack = true;
        foreach (var (owner, run) in _candidates) await _host.DisposeUninstalled(run, owner.Current);
        foreach (var reference in _preparedHolds) reference.Release();
        foreach (var owner in _newObjects) await owner.DisposeAsync(new ChordException("gone", "reload aborted"));
        foreach (var old in _closed) try { await old.OpenAsync(CancellationToken.None); } catch (Exception error) { _host.Report(error); await old.Owner.DisposeAsync(error); }
        foreach (var (owner, run) in _stopped) if (owner.Status == "live") owner.ResumeRuntime(run);
        foreach (var signal in _signals.Values) { signal.Cancel(); signal.Dispose(); }
        _host.GatedTypes.Clear(); if (ReferenceEquals(_host.PendingReload, this)) _host.PendingReload = null;
    });
}
