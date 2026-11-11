// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/host.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

public sealed partial class ChordHost : IAsyncDisposable
{
    internal readonly HostDispatcher Dispatcher = new();
    internal readonly HostOptions Options;
    internal readonly OldWork OldWork = new();
    internal readonly ReloadCycles ReloadCycles = new();
    internal readonly UnboundClock UnboundClock;
    internal TimeSpan ReloadDrainTimeout => Options.ReloadDrainTimeout < Options.CloseTimeout ? Options.ReloadDrainTimeout : Options.CloseTimeout;
    internal readonly Batch Batch;
    internal readonly ChordStore? Store;
    internal readonly OrderedDictionary<string, LoadedPlugin> Plugins = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, ServedType> Types = new(StringComparer.Ordinal);
    internal readonly OrderedDictionary<string, RegistryRecord> RegistryRecords = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, ChordObject> Objects = new(StringComparer.Ordinal);
    internal readonly HashSet<ChordObject> EndingObjects = new(ReferenceEqualityComparer.Instance);
    internal readonly TaskCompletionSource Publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Published;
    internal bool LoadStarted;
    internal bool Closed;
    internal event Action? PublishedChanged;
    internal event Action? Closing;
    internal Func<Contract, CheckedParams, Caller, ChordContext, ITarget?>? RemoteTarget;
    private readonly List<Binding> bindings = [];
    private readonly List<CallContext> roots = [];
    private readonly List<RegistryContribution> facetContributions = [];
    private readonly Dictionary<string, HostValue<IReadOnlyList<Presence>>> presences = new(StringComparer.Ordinal);
    private readonly HostValue<ActivityCounts> activity;
    private Task? closing;
    private int runningCalls, runningJobs, lingering, incomingReferences;
    private long liveOrder;
    internal long NextLiveOrder() => ++liveOrder;

    internal ChordHost(HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.OnError);
        if (options.Principal is { } principal && ChordJson.AsString(principal["kind"]) is null)
            throw new ArgumentException("A host principal needs a kind", nameof(options));
        if (options.ReadyTimeout < TimeSpan.Zero || options.CloseTimeout < TimeSpan.Zero || options.ReloadDrainTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        Options = options;
        UnboundClock = new(options.TimeProvider, Dispatcher.Post, Report);
        Dispatcher.Unhandled = error => Report(new("runtime_failed", error));
        Store = options.Store as ChordStore;
        if (options.Store is not null && Store is null) throw new ArgumentException("Store must be opened by Chord", nameof(options));
        var dispatcher = Dispatcher;
        Batch = new(error => Report(new HostError(error.Kind == BatchErrorKind.ListenerError ? "listener_error" : "delivery_failed", error.Error)),
            options.Telemetry?.Metrics, Dispatcher.Post) { IsOwnerThread = () => dispatcher.IsCurrent, InvokeOwner = dispatcher.Invoke,
                RetainCallback = (callback, owner) => RetainCallback(callback, owner as Facet) };
        activity = new HostValue<ActivityCounts>(new(0, 0, 0, 0), Batch);
        Store?.Attach(new Attachment(error => Dispatcher.Post(() =>
        {
            if (Closed) return;
            Report(new HostError("storage_failed", error));
            _ = CloseAsync(ChordContext.Background);
        }), Batch));
        InitializeRemote();
    }

    public string Incarnation { get; } = Guid.CreateVersion7().ToString();
    public bool IsLoaded => LoadStarted;
    public Address? Address => Options.Address;
    public INativeWatchable<ActivityCounts> Activity => activity;
    internal Caller OwnCaller => new(Options.Principal, Incarnation);
    internal ChordContext Context => ChordContext.Background;
    internal void AssertOpen() { if (Closed) throw HostErrors.Closed(); }
    internal void AssertLoaded() { if (!LoadStarted) throw new ChordError("not_ready", "call host.load first"); }
    internal void Report(HostError error) { try { Options.OnError(error); } catch { /* Diagnostic callbacks do not tear down host state. */ } }
    internal RegistryRecord RegistryRecord(string name)
    {
        if (!RegistryRecords.TryGetValue(name, out var record))
        {
            record = new RegistryRecord(name, Batch, error => Report(error switch
            {
                RegistryConflictError conflict => new("registry_conflict", Registry: conflict.Registry, Message: conflict.Message, Plugins: conflict.Plugins),
                RegistryFailedError failed => new("registry_failed", failed.Error, Registry: failed.Registry),
                ContributionFailedError failed => new("contribution_failed", failed.Error, Object: failed.Type, Registry: failed.Registry, Plugin: failed.Plugin),
                ContributionRefusedError refused => new("contribution_refused", Object: refused.Type, Registry: refused.Registry, Plugin: refused.Plugin, Version: refused.Version),
                RegistryUndeclaredError undeclared => new("registry_undeclared", Registry: undeclared.Registry, Plugins: undeclared.Plugins),
                _ => new("registry_failed", Registry: error.Registry),
            }), UnboundClock);
            RegistryRecords.Add(name, record);
        }
        return record;
    }

    // H2 calls once it has staged the complete accepted plugin set. No user code runs between these publication steps.
    internal void PublishPlugins(IReadOnlyList<LoadedPlugin> plugins, bool wake = true)
    {
        AssertOpen();
        var changed = plugins.Where(p => !Plugins.TryGetValue(p.Name, out var old) || !ReferenceEquals(old, p)).ToArray();
        var replaced = Plugins.Keys.Where(name => !plugins.Any(p => p.Name == name) || changed.Any(p => p.Name == name)).ToHashSet(StringComparer.Ordinal);
        var registryNames = plugins.SelectMany(p => p.Registries).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        Batch.Commit(() =>
        {
            foreach (var contribution in facetContributions.Where(c => replaced.Contains(c.Contributor.Plugin)).ToArray())
            { contribution.Record.Remove(contribution); facetContributions.Remove(contribution); }
            foreach (var record in RegistryRecords.Values) if (!registryNames.Contains(record.Name)) record.Unserve();
            Plugins.Clear(); Types.Clear();
            foreach (var plugin in plugins)
            {
                Plugins.Add(plugin.Name, plugin);
                foreach (var type in plugin.Types) Types.Add(type.Contract.Name, type);
                if (!changed.Contains(plugin)) continue;
                foreach (var registry in plugin.Registries) RegistryRecord(registry.Name).Serve(registry.Version, registry.Provider, registry.DeclaringFacet);
                foreach (var contribution in plugin.Contributions)
                {
                    var record = RegistryRecord(contribution.Name);
                    var entry = new RegistryContribution(record, new Contributor(plugin.Name, null), contribution.Version, ContentOf(contribution.Value), plugin.Facet);
                    facetContributions.Add(entry); record.Add(entry);
                }
            }
            foreach (var record in RegistryRecords.Values) record.ComposeServed();
            Published = true;
        }, Context);
        Publication.TrySetResult();
        ReportUndeclared();
        if (wake) WakePublication();
    }
    internal void WakePublication() { ResolveUnbound(); PublishedChanged?.Invoke(); }
    internal void ReportUndeclared() { foreach (var record in RegistryRecords.Values) record.ReportUndeclared(); }
    internal IReadOnlyList<string> UnservedNames() => bindings.Where(binding => binding.Target is null && !binding.Ended)
        .Select(binding => binding.Contract.Name).Where(name => !Types.ContainsKey(name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal IReadOnlyList<(string Name, long Version)> UndeclaredRegistries() => RegistryRecords.Values
        .Where(record => record.ProvidedVersion is null && record.NewestContribution is not null)
        .Select(record => (record.Name, record.NewestContribution!.Value)).OrderBy(record => record.Name, StringComparer.Ordinal).ToArray();
    internal int ActivationsUnderWay() => Objects.Values.Count(obj => obj.Current is { Live: false, Sealed: false });
    internal ChordObject[] EnsureHostObjects(ChordContext? context = null)
    {
        var made = new List<ChordObject>();
        foreach (var served in Types.Values.Where(x => x.HostLifetime))
            made.Add(LocalObject(served, new CheckedParams(new JsonObject(), ""), OwnCaller, context));
        return [.. made];
    }
    internal static ContributionContent ContentOf(object? value) => ResolveSource(value) switch
    {
        Source json => ContributionContent.Json(json),
        Constant constant => ContributionContent.Json(constant),
        NativeSource native => ContributionContent.Native(native),
        _ => ContributionContent.Plain(value),
    };
    internal static object? ResolveSource(object? source) => source switch
    {
        Documents.Singleton singleton => singleton.Slot,
        IRuntimeSource wrapper => wrapper.Source,
        _ => source,
    };
    internal void ResolveUnbound()
    {
        foreach (var binding in bindings.ToArray()) if (binding.Target is null) Place(binding);
    }
    internal ChordObject LocalObject(ServedType served, CheckedParams parameters, Caller caller, ChordContext? context = null)
    {
        var key = served.Contract.Kind == ContractKind.Local ? DataInternal.IdentityKey(served.Contract, parameters.Key)
            : DataInternal.IdentityKey(served.Contract, parameters.Key, DataInternal.PrincipalKey(caller.Principal!, Context, Options.PrincipalKey));
        if (!Objects.TryGetValue(key, out var result) || result.State.Kind == "ended")
        {
            result = new ChordObject(this, key, served, parameters, caller, context);
            Objects[key] = result;
            result.BeginAttempt();
        }
        return result;
    }
    internal Binding Acquire(Contract type, JsonObject parameters, Caller caller, ChordContext context, GetOptions? options = null, ChordObject? dependent = null)
    {
        AssertOpen(); Batch.AssertOutside("get"); context.AbortSignal?.ThrowIfCancellationRequested();
        var (value, key) = DataInternal.CheckParams(type, parameters, context);
        var binding = new Binding(this, type, new(value, key), caller, options?.ReadyTimeout ?? Options.ReadyTimeout, dependent);
        binding.AcquisitionContext = context;
        bindings.Add(binding);
        Place(binding);
        return binding;
    }
    internal void Place(Binding binding)
    {
        if (binding.Ended || !Published) return;
        try
        {
            var type = binding.Contract;
            var data = ContractsInternal.DataOf(type);
            var here = type.Kind == ContractKind.Local || data.HostAddress!(binding.Parameters.Value).Equals(Address);
            if (here && Types.TryGetValue(type.Name, out var served))
            {
                if (type.Kind == ContractKind.Remote && binding.Caller.Principal is null)
                    throw new ChordError("forbidden", "a remote object requires a principal");
                if (!DataInternal.Binds(served.Provider, type.Version, type.Fingerprint))
                {
                    var identity = type.Kind == ContractKind.Local ? DataInternal.IdentityKey(type, binding.Parameters.Key)
                        : DataInternal.IdentityKey(type, binding.Parameters.Key, DataInternal.PrincipalKey(binding.Caller.Principal!, Context, Options.PrincipalKey));
                    var old = Objects.GetValueOrDefault(identity);
                    if (OldWork.Of(binding.AcquisitionContext))
                    {
                        if (old?.OldServes(type.Version, type.Fingerprint) == true) { binding.Bind(old); return; }
                        binding.Failed(new(FailureKind.Broken, new ChordError("contract_mismatch", $"{type.Name} v{type.Version} is not served"))); return;
                    }
                    binding.Waiting(new ChordError("contract_mismatch", $"{type.Name} v{type.Version} is not served")); return;
                }
                if (type.Kind == ContractKind.Remote && binding.Caller.Principal is null)
                    throw new ChordError("forbidden", "a remote object requires a principal");
                binding.Bind(LocalObject(served, binding.Parameters, binding.Caller, binding.AcquisitionContext));
                return;
            }
            var remote = RemoteTarget?.Invoke(type, binding.Parameters, binding.Caller, binding.AcquisitionContext);
            if (remote is not null) { binding.Bind(remote); return; }
            binding.Waiting(new ChordError("unavailable", $"{type.Name}: no provider"));
        }
        catch (Exception error) { binding.Failed(new FailureCause(FailureKind.Broken, DataInternal.AsChordError(error, binding.Contract.Name))); }
    }
    public IReference Hold(Contract type, JsonObject parameters, ChordContext context, GetOptions? options = null) =>
        Dispatcher.Invoke(() => { AssertLoaded(); return Acquire(type, parameters, OwnCaller, context, options); });
    public Task<IReference> GetAsync(Contract type, JsonObject parameters, ChordContext context, GetOptions? options = null) =>
        Dispatcher.RunAsync(async () =>
        {
            AssertLoaded();
            var binding = Acquire(type, parameters, OwnCaller, context, options);
            try { await binding.ReadyAsOldWorkAsync(context); return (IReference)binding; }
            catch { binding.Release(); throw; }
        });
    public Task<IRegistryValue<V>> RegistryAsync<C, V>(Registry<C, V> registry, ChordContext context, GetOptions? options = null) =>
        Dispatcher.RunAsync(async () =>
        {
            AssertOpen(); AssertLoaded(); Batch.AssertOutside("registry");
            var timeout = options?.ReadyTimeout ?? Options.ReadyTimeout;
            var started = Options.TimeProvider.GetUtcNow();
            try { await Publication.Task.WaitAsync(timeout, Options.TimeProvider, context.AbortSignal ?? CancellationToken.None); }
            catch (TimeoutException) { throw new ChordError("unavailable", $"registry {registry.Name}: host has not published"); }
            var value = RegistryRecord(registry.Name).Value<V>(registry.Version);
            var deadline = started + timeout;
            while (true)
            {
                AssertOpen();
                var start = value.Start();
                if (!start.Failed || start.HasLast) return (IRegistryValue<V>)new DispatchRegistryValue<V>(value, Dispatcher);
                var remaining = deadline - Options.TimeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero) throw start.Error!;
                var waiter = new RegistryWait();
                var release = value.Wait(waiter);
                try { await waiter.Changed.Task.WaitAsync(remaining, Options.TimeProvider, context.AbortSignal ?? CancellationToken.None); }
                catch (TimeoutException) { throw start.Error!; }
                finally { release(); }
            }
        });
    public INativeWatchable<IReadOnlyList<Presence>> References(Contract type) => Dispatcher.Invoke(() =>
    {
        if (!presences.TryGetValue(type.Name, out var source)) presences.Add(type.Name, source = new(CurrentPresence(type.Name), Batch));
        return (INativeWatchable<IReadOnlyList<Presence>>)source;
    });
    private IReadOnlyList<Presence> CurrentPresence(string name) =>
        Objects.Values.Where(obj => obj.State.Kind == "live" && obj.Served.Contract.Name == name)
            .SelectMany(obj => obj.Holders.Where(holder => holder.Listed).Select(holder => new Presence(obj.Parameters.Value, holder.Caller.Principal, holder.Caller.Incarnation))).ToArray();
    internal void ChangedPresence()
    {
        activity.SetCurrent(new(incomingReferences, runningCalls, runningJobs, lingering));
        Batch.Soon(() =>
        {
            foreach (var (name, source) in presences)
                source.Set(CurrentPresence(name));
            activity.Notify();
        }, Context);
    }
    internal void Forget(Binding binding) { bindings.Remove(binding); ChangedPresence(); }
    internal void CountCall(int by) { runningCalls += by; ChordTelemetry.Metrics(Telemetry.Metrics).UpDownCounter("chord.calls.in_flight").Add(by); ChangedPresence(); }
    internal void CountJob(int by) { runningJobs += by; ChangedPresence(); }
    internal void CountReference(int by) { incomingReferences += by; ChangedPresence(); }
    internal void CountLinger(int by) { lingering += by; ChangedPresence(); }
    internal void RevokeLocal(Func<JsonObject, bool> predicate, ChordError error)
    {
        foreach (var obj in Objects.Values.ToArray())
            if (obj.Served.Contract.Kind == ContractKind.Remote && obj.Caller.Principal is { } principal && predicate(principal))
                _ = obj.EndAsync(error, NativeCloseReason.End);
        foreach (var binding in bindings.ToArray())
            if (binding.Contract.Kind == ContractKind.Remote && binding.Caller.Principal is { } principal && predicate(principal)) binding.End(error);
    }
    public Task<T> RootAsync<T>(Func<CallContext, Task<T>> work, ChordContext context, RootOptions? options = null) => Dispatcher.RunAsync(async () =>
    {
        AssertOpen(); Batch.AssertOutside("root");
        var caller = new Caller(options?.HasPrincipal == true ? options.Principal : Options.Principal, Incarnation);
        async Task<T> Run(ChordContext inside)
        {
            using var scope = new CallContext(this, caller, Counted(inside));
            roots.Add(scope);
            try { return await InvokeCallbackAsync(work, () => work(scope)).WaitAsync(scope.AbortSignal); }
            finally { roots.Remove(scope); }
        }
        if (options?.Trace is not { } trace || ChordTelemetry.IsUntraced(Telemetry)) return await Run(context);
        var attributes = Attributes(); if (trace.Attributes is { } extra) foreach (var each in extra) attributes[each.Key] = each.Value;
        return await Telemetry.StartSpanAsync(new PiSharp.Telemetry.SpanOptions(trace.Name) { Attributes = attributes }, async span =>
        {
            try { return await Run(ChordContext.WithTelemetry(span, context)); }
            catch (Exception error) { ChordTelemetry.Ended(span, "trace_failed", error, context.AbortSignal); throw; }
        });
    });
    public Task CloseAsync(ChordContext context) => Dispatcher.Invoke(() =>
    {
        if (closing is not null) return closing;
        // Publish ownership before running callbacks: cancelling a root or closing a link may reenter CloseAsync.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closing = completion.Task;
        _ = Finish();
        return closing;
        async Task Finish()
        {
            try { await CloseCoreAsync(context); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    });
    private async Task CloseCoreAsync(ChordContext context)
    {
        Closed = true;
        UnboundClock.Close();
        Closing?.Invoke();
        Publication.TrySetException(HostErrors.Closed());
        foreach (var root in roots.ToArray()) root.Dispose();
        foreach (var binding in bindings.ToArray()) binding.End(HostErrors.Closed());
        var objects = Objects.Values.Concat(EndingObjects).Distinct().OrderByDescending(obj => obj.LiveOrder).ToArray();
        foreach (var obj in objects) obj.Seal(HostErrors.Closed());
        var failures = new List<Exception>();
        foreach (var obj in objects)
            try { await obj.EndAsync(HostErrors.Closed(), NativeCloseReason.Close); } catch (Exception error) { failures.Add(error); }
        foreach (var contribution in facetContributions) contribution.Record.Remove(contribution);
        facetContributions.Clear();
        foreach (var record in RegistryRecords.Values) record.Close();
        RegistryRecords.Clear(); Types.Clear(); Plugins.Clear(); Objects.Clear();
        Store?.Detach();
        Dispatcher.Unhandled = null;
        Batch.ReleaseOwner();
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    public ValueTask DisposeAsync() => new(CloseAsync(ChordContext.Background));
    private sealed class RegistryWait : IRegistryWaiter
    {
        internal readonly TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void DependencyMoved() => Changed.TrySetResult();
        public void Closed(ChordError error) => Changed.TrySetException(error);
    }
}

internal interface IRuntimeSource { object Source { get; } }
internal sealed class DispatchRegistryValue<T>(RegistryValueSource<T> source, HostDispatcher dispatcher) : IRegistryValue<T>, IRuntimeSource
{
    object IRuntimeSource.Source => source;
    public T Value => dispatcher.Invoke(() => source.Value);
    public Task<INativeWatchHandle<T>> WatchAsync(ChordContext context) => dispatcher.RunAsync(() => source.WatchAsync(context));
    public NativeObservation<T> Observe(INativeObserver<T> observer, ChordContext context) => dispatcher.Invoke(() =>
    {
        var observed = source.Observe(observer, context);
        return new NativeObservation<T>(observed.Value, () => dispatcher.Invoke(observed.Detach));
    });
}

internal sealed class HostValue<T>(T initial, Batch batch) : FollowedSource<T>(batch)
{
    private T value = initial;
    public override T Value => value;
    internal void Set(T next) { value = next; Changed(); }
    internal void SetCurrent(T next) => value = next;
    internal void Notify() => Changed();
}
