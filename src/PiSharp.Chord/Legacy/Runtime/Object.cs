using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

internal sealed class LegacyRun
{
    internal required LegacyService Service;
    internal LegacyImplementation Implementation = null!;
    internal readonly Dictionary<string, LegacyCell> Cells = [];
    internal readonly Dictionary<string, (JsonNode Schema, LegacyStateOptions Options)> CellSchemas = [];
    internal readonly Dictionary<string, LegacyResourceCore> Resources = [];
    internal readonly Dictionary<LegacyResourceCore, (int Version, bool Exclusive, IReadOnlyList<object> Dependencies, Func<CancellationToken, Task<object?>> Open, Func<object?, Task>? Close)> ResourceSpecs = [];
    internal readonly Dictionary<string, LegacyReference> References = [];
    internal readonly List<LegacyDerived> Derivations = [];
    internal readonly List<(LegacyCell Cell, Func<LegacyContext, Task<JsonNode?>> Init)> Initializers = [];
    internal readonly Dictionary<string, (ILegacyReadable Source, Action<JsonNode?, LegacyContext> Handler)> Listeners = [];
    internal readonly Dictionary<string, (Func<Action<object?>, IDisposable> Subscribe, Func<object?, LegacyContext, Task> Handler)> EventListeners = [];
    internal readonly List<IDisposable> Subscriptions = [];
    internal readonly List<Func<LegacyContext, Task>> Tasks = [];
    internal readonly List<Task> RunningTasks = [];
    internal readonly List<(string Registry, string Conflict, string Key, object? Value)> Registrations = [];
    internal CancellationTokenSource Stop = new();
    internal readonly Dictionary<LegacyCell, JsonNode?> Migrated = [];
    internal readonly Dictionary<LegacyDerived, JsonNode?> DerivedValues = [];
    internal bool RuntimeStarted;
}

/// <summary>Declarations made during one legacy factory run. Native work starts after declaration has completed.</summary>
public sealed class LegacySelf
{
    private readonly LegacyObject _object; private readonly LegacyRun _run; private readonly LegacyRun? _previous;
    private bool _sealed;
    internal LegacySelf(LegacyObject owner, LegacyRun run, LegacyRun? previous) { _object = owner; _run = run; _previous = previous; }
    private void Check() { if (_sealed) throw ChordException.Invalid("declaration", "self may only declare while its factory is running"); }
    internal void Seal() => _sealed = true;
    public LegacyCell State(string name, JsonNode schema, JsonNode? initial, LegacyStateOptions? options = null) => DeclareState(name, schema, initial, null, options);
    public LegacyCell State(string name, JsonNode schema, Func<LegacyContext, Task<JsonNode?>> init, LegacyStateOptions? options = null) => DeclareState(name, schema, null, init, options);
    private LegacyCell DeclareState(string name, JsonNode schema, JsonNode? initial, Func<LegacyContext, Task<JsonNode?>>? init, LegacyStateOptions? options)
    {
        Check(); ArgumentException.ThrowIfNullOrEmpty(name); options ??= new();
        if (options.Version < 1) throw new ArgumentException("state version must be positive");
        if (_run.Cells.ContainsKey(name)) throw ChordException.Invalid("duplicate", "duplicate state name " + name);
        var cell = _previous?.Cells.GetValueOrDefault(name) ?? new LegacyCell(_object, name, schema, initial, init is null);
        _run.Cells[name] = cell; _run.CellSchemas[name] = (schema.DeepClone(), options);
        if (init is not null && _previous?.Cells.ContainsKey(name) != true) _run.Initializers.Add((cell, init));
        return cell;
    }
    public LegacyReference Get(LegacyContract contract, JsonNode? input, LegacyGetOptions? options = null)
    {
        Check(); var address = _object.Host.AddressOf(contract, input, options, out _, out _);
        if (_run.References.TryGetValue(address, out var current))
        { if (current.Contract.Fingerprint != contract.Fingerprint) throw ChordException.Invalid("duplicate", contract.Name + " held twice with different contracts"); return current; }
        var existing = _previous?.References.GetValueOrDefault(address);
        var reference = existing is not null && existing.Contract.Fingerprint == contract.Fingerprint && existing.Status is not ("gone" or "released") ? existing : _object.Host.Acquire(contract, input, options, _object);
        _run.References[address] = reference; return reference;
    }
    public LegacyResource<T> Resource<T>(string name, LegacyResourceDeclaration<T> declaration)
    {
        Check(); ArgumentException.ThrowIfNullOrEmpty(name);
        if (_run.Resources.ContainsKey(name)) throw ChordException.Invalid("duplicate", "duplicate resource name " + name);
        if (declaration.Version < 1) throw new ArgumentException("resource version must be positive");
        var dependencies = declaration.Dependencies.Select(x => x is ILegacyResourceHandle handle ? (object)handle.Core : x).ToArray();
        if (dependencies.Any(x => x is not LegacyResourceCore && x is not LegacyReference)) throw ChordException.Invalid("resource", "resource dependencies must be references or resources");
        var previous = _previous?.Resources.GetValueOrDefault(name);
        var kept = previous is not null && previous.Version == declaration.Version && previous.Exclusive == declaration.Exclusive && previous.Dependencies.SequenceEqual(dependencies);
        var resource = kept ? previous! : new LegacyResourceCore(_object, name);
        Func<CancellationToken, Task<object?>> open = async token => await declaration.Open(token);
        Func<object?, Task>? close = declaration.Close is null ? null : value => declaration.Close((T)value!);
        _run.ResourceSpecs[resource] = (declaration.Version, declaration.Exclusive, dependencies, open, close);
        if (!kept) Apply(resource, _run.ResourceSpecs[resource]);
        _run.Resources.Add(name, resource); return new LegacyResource<T>(resource);
    }
    internal static void Apply(LegacyResourceCore resource, (int Version, bool Exclusive, IReadOnlyList<object> Dependencies, Func<CancellationToken, Task<object?>> Open, Func<object?, Task>? Close) spec)
    { resource.Version = spec.Version; resource.Exclusive = spec.Exclusive; resource.Dependencies = spec.Dependencies; resource.Open = spec.Open; resource.Close = spec.Close; }
    public object Expose<T>(LegacyResource<T> resource) { Check(); return new LegacyExposed(resource.Core); }
    public LegacyDerived Derive(IReadOnlyList<ILegacyReadable> sources, Func<IReadOnlyList<JsonNode?>, JsonNode?> compute)
    { Check(); var derived = new LegacyDerived(_object.Host, sources, compute); _run.Derivations.Add(derived); return derived; }
    public void On(string name, ILegacyReadable source, Action<JsonNode?, LegacyContext> handler)
    { Check(); ArgumentException.ThrowIfNullOrEmpty(name); if (_run.EventListeners.ContainsKey(name) || !_run.Listeners.TryAdd(name, (source, handler))) throw ChordException.Invalid("duplicate", "duplicate listener " + name); }
    public void On<T>(string name, ILegacySubscribable<T> source, Func<T, LegacyContext, Task> handler) =>
        OnEvent(name, listener => source.Subscribe(value => listener(value)), (value, context) => handler((T)value!, context));
    public void On<TResource, TEvent>(string name, LegacyResource<TResource> resource, Func<TResource, ILegacySubscribable<TEvent>> select, Func<TEvent, LegacyContext, Task> handler) =>
        OnEvent(name, listener => select(resource.Value).Subscribe(value => listener(value)), (value, context) => handler((TEvent)value!, context));
    private void OnEvent(string name, Func<Action<object?>, IDisposable> subscribe, Func<object?, LegacyContext, Task> handler)
    { Check(); ArgumentException.ThrowIfNullOrEmpty(name); if (_run.Listeners.ContainsKey(name) || !_run.EventListeners.TryAdd(name, (subscribe, handler))) throw ChordException.Invalid("duplicate", "duplicate listener " + name); }
    public void Task(Func<LegacyContext, Task> work) { Check(); _run.Tasks.Add(work); }
    public void Register<T>(LegacyRegistry<T> registry, IReadOnlyDictionary<string, T> entries)
    {
        Check();
        if (_run.Service.Options.Lifetime != "host") throw ChordException.Invalid("not_host_lifetime", "only host-lifetime objects may register");
        foreach (var (key, value) in entries)
        { if (_run.Registrations.Any(r => r.Registry == registry.Name && r.Key == key)) throw ChordException.Invalid("duplicate", "duplicate registration " + key); _run.Registrations.Add((registry.Name, registry.Conflict, key, value)); }
    }
}

internal sealed class LegacyObject
{
    internal readonly LegacyHost Host;
    internal readonly string Address;
    internal readonly JsonNode? Input;
    internal readonly object? Principal;
    internal readonly HashSet<LegacyReference> References = [];
    internal readonly Dictionary<int, LegacyRun> Runs = [];
    internal readonly List<LegacyResourceCore> OpenOrder = [];
    internal readonly CancellationTokenSource Abort = new();
    internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LegacyRun? Current;
    internal string Status = "loading";
    internal Exception? Error;
    internal IDisposable? External;
    internal LegacyContract? RemoteContract;
    internal bool RemoteConnecting;
    internal bool HasRemoteShape;
    internal bool RemoteViaUpstream;
    internal bool RemoteSubscribed;
    internal bool RemoteStateReady;
    internal bool Deferred;
    internal int? ActivationVersion;
    internal readonly List<IDisposable> RemoteSnapshotObservers = [];
    internal readonly HashSet<Task> Invocations = [];
    private Task? _disposal;
    private CancellationTokenSource? _linger;
    internal LegacyObject(LegacyHost host, string address, JsonNode? input, object? principal) { Host = host; Address = address; Input = input; Principal = principal; _ = Ready.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
    internal LegacyRun Declare(LegacyService service, LegacyRun? previous)
    {
        var run = new LegacyRun { Service = service }; var self = new LegacySelf(this, run, previous);
        Host.InFactory = true;
        try { run.Implementation = service.Factory(Input?.DeepClone(), Principal, self) ?? throw ChordException.Invalid("implementation", "factory must return an implementation"); ValidateImplementation(run); }
        catch { Host.DisposeUninstalled(run, previous).GetAwaiter().GetResult(); throw; }
        finally { Host.InFactory = false; self.Seal(); }
        return run;
    }
    internal static void ValidateImplementation(LegacyRun run)
    {
        var contract = run.Service.Contract; var variant = contract.Scope == "local" ? "" : run.Implementation.Variant;
        if (variant is null || !contract.Variants.TryGetValue(variant, out var members)) throw ChordException.Invalid("implementation", "unknown variant " + variant);
        if (contract.Scope == "remote" && run.Implementation.Members.Keys.Any(k => !members.ContainsKey(k))) throw ChordException.Invalid("implementation", "implementation contains members outside its variant");
        foreach (var (name, description) in members)
        {
            if (!run.Implementation.Members.TryGetValue(name, out var value)) throw ChordException.Invalid("implementation", name + " is missing");
            if (description is LegacyMethodMember && value is not LegacyMethod && !(contract.Scope == "local" && value is LegacyNativeMethod)) throw ChordException.Invalid("implementation", name + " must be a method");
            if (description is LegacyStateMember && value is not ILegacyReadable) throw ChordException.Invalid("implementation", name + " must be a state source");
        }
    }
    internal async Task PrepareAsync(LegacyRun run, CancellationToken cancellationToken, bool reload)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Abort.Token);
        foreach (var reference in run.References.Values) await Host.AwaitReady(reference, linked.Token);
        foreach (var resource in run.Resources.Values)
            if (!reload || !resource.Exclusive || Current?.Resources.Values.Contains(resource) == true) await resource.OpenAsync(linked.Token);
        await Task.WhenAll(run.Initializers.Select(async item => { var value = await Host.RootAsync(item.Init, new LegacyRootOptions { CancellationToken = linked.Token }); item.Cell.Initialize(value); }));
        foreach (var (name, cell) in run.Cells)
        {
            var (schema, options) = run.CellSchemas[name]; var value = cell.RawValue;
            if (reload && Current!.Cells.TryGetValue(name, out var old))
            {
                var oldInfo = Current.CellSchemas[name];
                if (options.Version < oldInfo.Options.Version) throw new ChordException("upgrade_required", "state version moved backwards: " + name);
                if (options.Version == oldInfo.Options.Version && !LegacySchema.Equal(schema, oldInfo.Schema)) throw new ChordException("upgrade_required", "state schema changed without a version bump: " + name);
                if (options.Version > oldInfo.Options.Version && options.Migrate is not null) value = options.Migrate(old.RawValue?.DeepClone(), oldInfo.Options.Version);
                if (options.Version != oldInfo.Options.Version || !LegacySchema.Equal(value, old.RawValue)) run.Migrated[cell] = value?.DeepClone();
            }
            LegacySchema.Assert(schema, value, "state", "state " + name);
        }
        foreach (var derived in run.Derivations) run.DerivedValues[derived] = derived.Compute(source => source is LegacyCell cell && run.Migrated.TryGetValue(cell, out var migrated) ? migrated : source is LegacyDerived dependency && run.DerivedValues.TryGetValue(dependency, out var candidate) ? candidate : source.Value);
        var members = run.Service.Contract.Variants[run.Service.Contract.Scope == "local" ? "" : run.Implementation.Variant!];
        foreach (var (name, spec) in members)
            if (spec is LegacyStateMember state)
            {
                var source = (ILegacyReadable)run.Implementation.Members[name]!;
                var value = source is LegacyCell cell && run.Migrated.TryGetValue(cell, out var migration) ? migration : source is LegacyDerived derived && run.DerivedValues.TryGetValue(derived, out var computed) ? computed : source.Value;
                LegacySchema.Assert(state.RawSchema, value, "state", name);
            }
    }
    internal async Task ActivateAsync(LegacyService service)
    {
        try
        {
            await Task.Yield(); if (Abort.IsCancellationRequested) return;
            var run = Declare(service, null); Current = run; Runs[ActivationVersion ?? Host.CodeVersion] = run;
            await PrepareAsync(run, Abort.Token, false);
            if (Abort.IsCancellationRequested) throw new ChordException("gone", Address + " was disposed");
            if (!Deferred) Host.CheckRegistrations(this, run); Install(run, ActivationVersion ?? Host.CodeVersion); Status = "live";
            if (!Deferred) Host.RegistriesChanged(); foreach (var reference in References.ToArray()) reference.Changed(); Ready.TrySetResult(); if (!Deferred) StartRuntime(run);
        }
        catch (Exception error)
        {
            Error = ChordException.Wrap(error, Address); Status = "gone"; Ready.TrySetException(Error);
            foreach (var reference in References.ToArray()) reference.Gone(Error); Host.Report(Error);
            await DisposeAsync(Error);
        }
    }
    internal void Install(LegacyRun run, int generation)
    {
        if (Current is { } old)
        {
            foreach (var (name, spec) in old.Service.Contract.Variants[old.Service.Contract.Scope == "local" ? "" : old.Implementation.Variant!])
                if (spec is LegacyStateMember state && old.Implementation.Members[name] is LegacySource source) source.ExposedSchemas.Remove(state.RawSchema);
            if (run.Migrated.Count > 0) foreach (var derived in old.Derivations) derived.Dispose();
        }
        Current = run; Runs[generation] = run;
        foreach (var (resource, spec) in run.ResourceSpecs) LegacySelf.Apply(resource, spec);
        foreach (var (cell, value) in run.Migrated) cell.Reset(value, run.CellSchemas[cell.Key].Schema);
        foreach (var derived in run.Derivations) derived.Attach(run.DerivedValues.GetValueOrDefault(derived), true);
        foreach (var (name, spec) in run.Service.Contract.Variants[run.Service.Contract.Scope == "local" ? "" : run.Implementation.Variant!])
            if (spec is LegacyStateMember state && run.Implementation.Members[name] is LegacySource source) source.ExposedSchemas.Add(state.RawSchema);
    }
    internal void StartRuntime(LegacyRun run, Task? tasksStopped = null)
    {
        if (run.RuntimeStarted) return; run.RuntimeStarted = true;
        var runtimeToken = run.Stop.Token;
        void Remember(Task work)
        {
            run.RunningTasks.Add(work); Host.Observe(work);
            _ = work.ContinueWith(_ => Host.Fire(() => { run.RunningTasks.Remove(work); return Task.CompletedTask; }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        foreach (var (_, listener) in run.Listeners)
            run.Subscriptions.Add(listener.Source.Subscribe(value => Host.Fire(async () => { await Host.RootAsync(async context => { if (context.CancellationToken.IsCancellationRequested) return true; listener.Handler(value, context); await System.Threading.Tasks.Task.CompletedTask; return true; }, new LegacyRootOptions { CancellationToken = runtimeToken }); })));
        foreach (var (_, listener) in run.EventListeners)
            run.Subscriptions.Add(listener.Subscribe(value => Host.Fire(async () => { await Host.RootAsync(async context => { if (context.CancellationToken.IsCancellationRequested) return true; await listener.Handler(value, context); return true; }, new LegacyRootOptions { CancellationToken = runtimeToken }); })));
        void StartTasks()
        {
            foreach (var task in run.Tasks)
            {
                var work = Host.RootAsync(async context => { if (context.CancellationToken.IsCancellationRequested) return true; await task(context); return true; }, new LegacyRootOptions { CancellationToken = runtimeToken });
                Remember(work);
            }
        }
        if (tasksStopped is null || tasksStopped.IsCompleted) StartTasks();
        else
        {
            var pending = Host.RunAsync(async () =>
            {
            try { await tasksStopped.WaitAsync(Host.Options.TaskStopDeadline); }
            catch (TimeoutException error) { Host.Report(new ChordException("unavailable", "old tasks did not stop before replacement", "task_stop_deadline", cause: error)); }
            catch (Exception) { /* Old task failures were reported by their owners. */ }
            if (!runtimeToken.IsCancellationRequested && Status == "live" && ReferenceEquals(Current, run)) StartTasks();
            });
            Remember(pending); Host.TrackWork(Host.CodeVersion, pending);
        }
    }
    internal void StopRuntime(LegacyRun run)
    {
        try { run.Stop.Cancel(); } catch (Exception error) { Host.Report(error); }
        foreach (var subscription in run.Subscriptions) try { subscription.Dispose(); } catch (Exception error) { Host.Report(error); }
        run.Subscriptions.Clear();
    }
    internal void ResumeRuntime(LegacyRun run)
    { run.Stop.Dispose(); run.Stop = new(); run.RuntimeStarted = false; run.RunningTasks.Clear(); StartRuntime(run); }
    internal void Add(LegacyReference reference) { _linger?.Cancel(); _linger?.Dispose(); _linger = null; Host.RemoteActive(this); References.Add(reference); Host.UpdateRemoteSubscription(this); }
    internal void Release(LegacyReference reference)
    {
        References.Remove(reference); if (References.Count != 0) { Host.UpdateRemoteSubscription(this); return; }
        if (Current?.Service.Options.Lifetime == "host" || Status == "disposed") return;
        if (RemoteContract is not null) { Host.RemoteIdle(this); return; }
        var linger = Current?.Service.Options.Linger ?? TimeSpan.Zero;
        ScheduleLinger(linger);
    }
    internal void ScheduleLinger(TimeSpan linger)
    {
        if (linger <= TimeSpan.Zero) Host.Observe(DisposeAsync());
        else { _linger = new(); var token = _linger.Token; Host.Fire(async () => { try { await Task.Delay(linger, Host.Options.TimeProvider, token); await DisposeAsync(); } catch (OperationCanceledException) { } }); }
    }
    internal void Guard(LegacyContext context)
    {
        context.Check(Host);
        if (Host.InFactory) throw ChordException.Invalid("read_only", "state cannot change in a factory");
        if (Status != "live") throw new ChordException("gone", Address + " is not live");
        Host.Lease(this, context);
    }
    internal async Task<object?> CallAsync(LegacyReference reference, string name, bool hasArgs, JsonNode? args, LegacyContext context)
    {
        context.Check(Host); if (Status != "live") throw new ChordException("gone", Address + " is not live");
        Host.Lease(this, context);
        var run = Runs.GetValueOrDefault(context.CodeVersion) ?? Current!;
        var adapter = LegacyContract.Bind(run.Service.Contract, run.Service.Options.Accepts, reference.Contract);
        var implementation = adapter is null ? run.Implementation : adapter(run.Implementation);
        var variant = reference.Contract.Scope == "local" ? "" : implementation.Variant;
        if (variant is null || !reference.Contract.Variants.TryGetValue(variant, out var members) || !members.TryGetValue(name, out var spec) || spec is not LegacyMethodMember method) throw new ChordException("not_in_variant", name + " is not in this variant");
        if ((method.RawArgs is not null) != hasArgs) throw ChordException.Invalid("args", name + (hasArgs ? " takes no arguments" : " needs arguments"));
        var cleaned = args?.DeepClone(); if (method.RawArgs is not null) { cleaned = LegacySchema.Clean(method.RawArgs, cleaned); LegacySchema.Assert(method.RawArgs, cleaned, "args", name); }
        var providerMembers = run.Service.Contract.Variants[run.Service.Contract.Scope == "local" ? "" : run.Implementation.Variant!];
        if (adapter is null && providerMembers[name] is LegacyMethodMember provider && provider.RawArgs is not null) { cleaned = LegacySchema.Clean(provider.RawArgs, cleaned); LegacySchema.Assert(provider.RawArgs, cleaned, "args", name); }
        if (!implementation.Members.TryGetValue(name, out var handler) || handler is not LegacyMethod invoke) throw ChordException.Invalid("implementation", name + " is not a method");
        using var child = new LegacyContext(Host, context.RootId, context.CodeVersion, context.Principal, context.CancellationToken, Abort.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Invocations.Add(completion.Task);
        Host.TrackWork(context.CodeVersion, completion.Task);
        object? result;
        try { result = await invoke(cleaned, child); child.Check(Host); }
        finally { Invocations.Remove(completion.Task); completion.TrySetResult(); }
        if (method.RawResult is null) return null;
        var json = result as JsonNode;
        if (result is not null && json is null) { try { json = System.Text.Json.JsonSerializer.SerializeToNode(result); } catch (Exception error) { throw new ChordException("invalid", "result is not JSON", "result", cause: error); } }
        LegacySchema.Assert(method.RawResult, json, "result", name); return LegacySchema.Clean(method.RawResult, json);
    }
    internal async Task<object?> CallNativeAsync(LegacyReference reference, string name, object? args, LegacyContext context)
    {
        context.Check(Host); if (Status != "live") throw new ChordException("gone", Address + " is not live");
        Host.Lease(this, context);
        var run = Runs.GetValueOrDefault(context.CodeVersion) ?? Current!;
        var adapter = LegacyContract.Bind(run.Service.Contract, run.Service.Options.Accepts, reference.Contract);
        var implementation = adapter is null ? run.Implementation : adapter(run.Implementation);
        if (!reference.Contract.Variants[""].TryGetValue(name, out var spec) || spec is not LegacyMethodMember method) throw new ChordException("not_in_variant", name + " is not a method");
        if (method.RawArgs is not null) LegacySchema.AssertNative(method.RawArgs, args, "args", name);
        if (!implementation.Members.TryGetValue(name, out var value) || value is not LegacyNativeMethod invoke) throw ChordException.Invalid("implementation", name + " is not a native method");
        using var child = new LegacyContext(Host, context.RootId, context.CodeVersion, context.Principal, context.CancellationToken, Abort.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Invocations.Add(completion.Task);
        Host.TrackWork(context.CodeVersion, completion.Task);
        try { var result = await invoke(args, child); child.Check(Host); if (method.RawResult is null) return null; LegacySchema.AssertNative(method.RawResult, result, "result", name); return result; }
        finally { Invocations.Remove(completion.Task); completion.TrySetResult(); }
    }
    internal Task DisposeAsync(Exception? reason = null)
    {
        if (_disposal is not null) return _disposal;
        var versions = Runs.Keys.Append(ActivationVersion ?? Host.CodeVersion).Distinct().ToArray();
        _disposal = DisposeCoreAsync(reason);
        var disposal = _disposal;
        foreach (var version in versions) Host.TrackWork(version, disposal);
        if (disposal.IsCompleted) _disposal = Task.CompletedTask;
        else _ = disposal.ContinueWith(_ => Host.Fire(() =>
        {
            // A retained, released public reference still points at this owner. Do not let its completed async
            // cleanup frame keep a last resource/delegate local (and the module it came from) alive forever.
            if (ReferenceEquals(_disposal, disposal)) _disposal = Task.CompletedTask;
            return Task.CompletedTask;
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return disposal;
    }
    private async Task DisposeCoreAsync(Exception? reason)
    {
        if (Status == "disposed") return; Status = "disposed";
        var errors = new List<Exception>();
        try { Abort.Cancel(); } catch (Exception error) { errors.Add(error); }
        try { _linger?.Cancel(); } catch (Exception error) { errors.Add(error); }
        Host.Remove(this);
        try { External?.Dispose(); } catch (Exception error) { errors.Add(error); }
        External = null; foreach (var observer in RemoteSnapshotObservers) observer.Dispose(); RemoteSnapshotObservers.Clear(); foreach (var reference in References.ToArray()) reference.Gone(reason ?? new ChordException("gone", Address + " was disposed"));
        Ready.TrySetException(reason ?? new ChordException("gone", Address + " was disposed"));
        foreach (var run in Runs.Values.Distinct()) StopRuntime(run);
        try { await Task.WhenAll(Invocations.Concat(Runs.Values.Distinct().SelectMany(run => run.RunningTasks))).WaitAsync(Host.Options.TaskStopDeadline); }
        catch (TimeoutException error) { Host.Report(new ChordException("unavailable", "object work did not stop before resource close", "drain_timeout", cause: error)); }
        catch (Exception error) { Host.Report(error); }
        foreach (var resource in OpenOrder.AsEnumerable().Reverse().ToArray()) try { await resource.CloseAsync(); } catch (Exception error) { errors.Add(error); }
        foreach (var run in Runs.Values.Distinct()) { foreach (var derived in run.Derivations) derived.Dispose(); foreach (var reference in run.References.Values) reference.Release(); }
        Runs.Clear(); Current = null; Host.RegistriesChanged(); foreach (var error in errors) Host.Report(error);
    }
}
