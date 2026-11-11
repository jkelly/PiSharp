// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/attempt.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

internal sealed partial class Attempt : IOldWorkSource
{
    internal readonly ChordObject Owner;
    internal readonly ServedType Served;
    internal readonly Self Self;
    internal string InstanceId = Guid.CreateVersion7().ToString();
    internal readonly SealToken Token = new();
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly Dictionary<string, object?> Members = new(StringComparer.Ordinal);
    internal HostImplementation? Implementation;
    internal bool Live;
    internal bool Sealed;
    private bool drainStarted;
    internal readonly OldWorkState OldWorkState = new();
    public bool Draining => drainStarted && !Sealed;
    internal bool PreserveReferencesAtClose;
    internal bool PreserveSourcesAtClose;
    private OpenSpan? activation;
    private long activationNumber;
    private DateTimeOffset? parkedAt;
    internal ChordContext OpeningContext => activation is { Ended: false } tracing
        ? ChordContext.WithTelemetry(tracing.Span, Owner.Context) : Owner.Context;
    private void StartActivation()
    {
        if (ChordTelemetry.IsUntraced(Owner.Host.Telemetry)) return;
        var attributes = Owner.Host.Attributes(Served.Contract.Name, Owner.Label); attributes["attempt"] = (double)++activationNumber;
        var previous = activation?.Span; activation = new OpenSpan(Owner.Host.Telemetry, "chord.activate", attributes);
        if (previous is not null) activation.Span.AddLink(new(previous));
        else if (!ChordTelemetry.IsUntraced(Owner.Trigger)) activation.Span.AddLink(new(Owner.Trigger));
    }
    private void Activated(string outcome, Exception? error = null)
    {
        if (activation is not { Ended: false } span) return;
        if (error is not null) ChordTelemetry.SpanFailed(span.Span, "activation_failed", error);
        span.Span.SetAttributes(new SpanAttributes { ["outcome"] = outcome }); span.End();
    }
    private ChordError? sealedError;
    private bool sourcesDetached;
    internal readonly TaskCompletionSource<FailureCause?> Checked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource RetirementEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly Dictionary<Task, Action?> Work = new();
    internal readonly Dictionary<Task, Action?> Runs = new();
    private readonly Dictionary<long, AdaptedMembers> adapted = new();
    private AdaptedMembers Adaptation(long version)
    {
        if (!adapted.TryGetValue(version, out var value))
        {
            value = new(Served, Implementation?.Variant, Owner.Label, Owner.Host.Batch,
                name => Members[name]!, (name, error) => Owner.Host.Report(new("derive_failed", error, Owner.Label, name))) { Version = version };
            adapted.Add(version, value);
        }
        return value;
    }
    internal object? AdaptedMember(string name, long version) => version == Served.Contract.Version ? Members[name] : Adaptation(version).Member(name);
    internal IKeyedHandle AdaptedKeyed(string name, IKeyedHandle value, long version) => version == Served.Contract.Version ? value : Adaptation(version).Keyed(name, value);
    internal (IWatchable<JsonNode?> Source, Action Release) HoldKeySource(string name, IWatchable<JsonNode?> source, long version) =>
        version == Served.Contract.Version ? (source, () => { }) : Adaptation(version).HoldKeySource(name, source);
    internal void DropAdaptation(long version)
    {
        if (adapted.Remove(version, out var value)) value.Detach(new ChordError("released", "last reader released"));
    }
    internal int NativeCount => Self.Natives.Count;
    internal bool OpensNatives => NativeCount > 0;
    internal bool HasSources => Implementation is not null && !sourcesDetached;
    internal Task<FailureCause?> Check => Checked.Task;
    private TaskCompletionSource changed = NewSignal();
    private Task? closing;
    internal Attempt(ChordObject owner, ServedType served) { Owner = owner; Served = served; Self = new(this); }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Wake() { var before = changed; changed = NewSignal(); before.TrySetResult(); }
    internal async Task ActivateAsync()
    {
        StartActivation();
        try
        {
            try
            {
                using var factoryLease = Owner.Host.RetainCallback(Served.Factory, Served.DeclaringFacet);
                Implementation = Served.Factory(Owner.Parameters.Value, Served.Contract.Kind == ContractKind.Local ? null : Owner.Caller.Principal, Self);
                PluginStaging.ValidateImplementation(Served.Contract, Implementation);
            }
            catch (Exception error) { throw new ActivationException(new(FailureKind.Broken, DataInternal.AsChordError(error, Owner.Label))); }
            finally { Self.Declaring = false; }
            Owner.InstallRetrySources(Self.RetrySources);
            foreach (var (name, member) in Implementation.Members) Members.Add(name, member);
            foreach (var derive in Self.Derives) derive.Attach((source, _) => ChordHost.ResolveSource(source) ?? throw new ArgumentException("not a Chord source"),
                error => Owner.Host.Report(new("derive_failed", error, Owner.Label)));
            // Dependency waits do not consume the object's own activation deadline.
            DateTimeOffset? parkedSince = null;
            var parkedReported = false;
            while (true)
            {
                Cancellation.Token.ThrowIfCancellationRequested();
                FailureCause? wait = null; bool pending = false, blocks = false;
                var blocker = "a dependency"; var blockerState = "loading";
                foreach (var dependency in Self.Dependencies)
                {
                    var target = dependency.Target;
                    if (target?.Reaches(Owner, new(ReferenceEqualityComparer.Instance)) == true ||
                        target?.ActivationId is { } activationId && Owner.ChainContains(activationId))
                        throw new ActivationException(new(FailureKind.Broken, DataInternal.Invalid("cycle", $"{Owner.Label}: dependency cycle")));
                    var state = dependency.RefState;
                    if (state.Kind == "ended") throw new ActivationException(new(FailureKind.Broken, state.Error!));
                    if (state.Kind == "live") continue;
                    if (state.Cause is { } cause)
                    {
                        if (cause.Error.Code == "forbidden" || cause.Error.Reason == "cycle") throw new ActivationException(new(FailureKind.Broken, cause.Error));
                        wait ??= DataInternal.WaitingFor(dependency.Label, cause.Error);
                    }
                    if (!pending) { blocker = dependency.Label; blockerState = state.Kind == "failed" ? "failed" : "loading"; }
                    pending = true; blocks |= target?.BlocksFirstPass == true;
                }
                foreach (var registry in Self.RegistryDependencies)
                {
                    var error = registry.Error();
                    if (error is null) continue;
                    if (!pending) { blocker = $"registry {registry.Name}"; blockerState = "failed"; }
                    wait ??= DataInternal.WaitingFor(registry.Name, DataInternal.AsChordError(error, registry.Name)); pending = true;
                }
                if (!pending) break;
                if (parkedAt is null) parkedAt = Owner.Host.Options.TimeProvider.GetUtcNow();
                if (activation is { Ended: false } parking)
                {
                    parking.Span.AddEvent("chord.park", new SpanAttributes { ["cause"] = wait?.Error.Message ?? "loading" });
                    Activated("parked");
                }
            Owner.SetState(wait is null ? RefState.Loading : RefState.Failed(wait));
            Owner.Host.ReloadCycles.Recheck();
                Owner.FirstChecked(!blocks); Checked.TrySetResult(wait);
                parkedSince ??= Owner.Host.Options.TimeProvider.GetUtcNow();
                var wake = changed.Task;
                if (!parkedReported)
                {
                    var remaining = Owner.Host.Options.ReadyTimeout - (Owner.Host.Options.TimeProvider.GetUtcNow() - parkedSince.Value);
                    if (remaining > TimeSpan.Zero)
                    {
                        using var timer = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token);
                        var deadline = Task.Delay(remaining, Owner.Host.Options.TimeProvider, timer.Token);
                        var signaled = await Task.WhenAny(wake, deadline);
                        timer.Cancel();
                        Cancellation.Token.ThrowIfCancellationRequested();
                        if (ReferenceEquals(signaled, wake)) continue;
                    }
                    parkedReported = true;
                    Owner.Host.Report(new("parked_long", Object: Owner.Label, WaitingFor: blocker, State: blockerState,
                        Since: parkedSince.Value.ToUnixTimeMilliseconds()));
                }
                await wake.WaitAsync(Cancellation.Token);
            }
            if (parkedAt is { } parked)
            {
                ChordTelemetry.Metrics(Owner.Host.Telemetry.Metrics).Histogram("chord.park.duration").Record((Owner.Host.Options.TimeProvider.GetUtcNow() - parked).TotalMilliseconds);
                parkedAt = null; StartActivation();
            }
            await ActivateOwnAsync().WaitAsync(Owner.Host.Options.ReadyTimeout, Owner.Host.Options.TimeProvider, Cancellation.Token);
            if (Sealed) return;
            Live = true;
            foreach (var registry in Self.RegistryDependencies) registry.Read();
            Owner.Host.Batch.Within(() =>
            {
                Owner.WithdrawRetiringContributions(this);
                foreach (var declared in Self.Contributions)
                {
                    var record = Owner.Host.RegistryRecord(declared.Name);
                    var contribution = new RegistryContribution(record, new(Served.Plugin, Served.Contract.Name), declared.Version, ChordHost.ContentOf(declared.Value), Served.DeclaringFacet);
                    Self.PublishedContributions.Add(contribution); record.Add(contribution);
                }
            }, Owner.Context);
            Owner.WentLive(this); Checked.TrySetResult(null);
            Activated("live");
            Owner.Host.ReportUndeclared();
            PiSharp.Chord.Jobs.HostedJob.Reconcile(Owner.Documents, Self.Jobs.OfType<PiSharp.Chord.Jobs.HostedJob>().Select(job => job.Name));
            foreach (var job in Self.Jobs) job.Live();
            Self.StartRoutines();
        }
        catch (OperationCanceledException) when (Sealed) { Activated("ended"); Checked.TrySetResult(null); }
        catch (Exception error)
        {
            var cause = error is ActivationException activation ? activation.Cause : DataInternal.OpenFailure(error, Owner.Label);
            Activated("failed", cause.Error);
            Checked.TrySetResult(cause); Owner.Failed(this, cause);
        }
    }
    private async Task ActivateOwnAsync()
    {
        var entering = Owner.Host.HostDataGate.Enter();
        if (entering is not null) await entering;
        try
        {
        Owner.Documents.Hydrate(Self.Definitions, Self.Pins, Owner.Context);
        if (Self.Tables.Count > 0)
            await StoreInternal.PrepareTablesAsync(Self.Tables.Select(t => new DeclaredTable(t.Definition, t.Sql.Table)).ToArray(), Owner.Host.Store!,
                new(() => !Token.Sealed, (_, _) => { }), Owner.Label, Self.Context);
        if (Owner.Documents.Load(Self.Context) is { } loading) await loading;
        foreach (var query in Self.Queries) await query.LoadAsync(Self.Context);
        }
        finally { Owner.Host.HostDataGate.Leave(); }
        foreach (var job in Self.Jobs) await job.LoadAsync(Self.Context);
        foreach (var native in Self.Natives) { Cancellation.Token.ThrowIfCancellationRequested(); await native.OpenAsync(Self.Context); }
        foreach (var follow in Self.Follows) { Cancellation.Token.ThrowIfCancellationRequested(); await follow.AttachAsync(); }
        foreach (var member in Members.Values)
            if (ChordHost.ResolveSource(member) is Source source) _ = source.Value;
    }
    internal Task<object?> CallAsync(string method, object? args, ChordContext context, Caller caller, long version)
    {
        if (!Live || Sealed) return Task.FromException<object?>(HostErrors.NotReady(Owner.Label));
        if (!Members.TryGetValue(method, out var member) || member is not HostMethod handler) return Task.FromException<object?>(HostErrors.InvalidMember(method));
        using var argumentsLease = Served.Provider.Accepts.GetValueOrDefault(version)?.Methods.GetValueOrDefault(method)?.Args is { } mapArguments
            ? Owner.Host.RetainCallback(mapArguments, Served.DeclaringFacet) : null;
        var checkedArgs = DataInternal.CheckArgs(Served.Provider, method, args as JsonNode, version, context);
        OpenSpan? trace = null;
        var parent = ChordHost.ParentOf(context);
        if (!Served.Options.Untraced && !ChordTelemetry.IsUntraced(parent))
        {
            var attributes = Owner.Host.Attributes(Served.Contract.Name, Owner.Label);
            attributes["member"] = method; attributes["principal.kind"] = caller.Principal?["kind"]?.GetValue<string>() ?? "none";
            foreach (var each in Owner.Host.PrincipalAttributes(caller.Principal, context)) attributes[each.Key] = each.Value;
            trace = new OpenSpan(parent, "chord.method", attributes);
            context = ChordHost.WithTelemetryParent(trace.Span, ChordContext.WithTelemetry(trace.Span, context));
        }
        var scope = new CallContext(Owner.Host, caller, context, Cancellation.Token, this);
        // Scope disposal cancels call-scoped references after a successful reply. That cancellation must not
        // race the reply itself; the caller wait follows only its input signal and the attempt's lifetime.
        var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None, Cancellation.Token);
        var waitingToken = waiting.Token;
        Owner.Host.CountCall(1);
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        trace?.EndWhen(completion.Task, "method_failed", context.AbortSignal);
        // Account for the call before plugin code executes (it may synchronously end this object).
        Admit(completion.Task, () => Owner.Host.CountCall(-1));
        _ = Invoke();
        return completion.Task.WaitAsync(waitingToken);
        async Task Invoke()
        {
            try
            {
                using var invocationLease = checkedArgs switch
                {
                    CheckedArgs.Call replacementOwner => Owner.Host.RetainCallback(replacementOwner.Replacement, Served.DeclaringFacet),
                    CheckedArgs.Method { Result: { } resultOwner } => Owner.Host.RetainCallbacks(Served.DeclaringFacet, handler, resultOwner),
                    _ => Owner.Host.RetainCallback(handler, Served.DeclaringFacet),
                };
                var result = checkedArgs is CheckedArgs.Call replaced
                    ? await replaced.Replacement(replaced.Args, scope)
                    : await handler(checkedArgs.Args, scope);
                completion.TrySetResult(checkedArgs is CheckedArgs.Method { Result: { } map } ? map(result) : result);
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { scope.Dispose(); waiting.Dispose(); }
        }
    }
    internal void Admit(Task task, Action? cleanup = null) => Track(Work, task, cleanup);
    internal void AdmitRun(Task task, Action cleanup) => Track(Runs, task, cleanup);
    private void Track(Dictionary<Task, Action?> table, Task task, Action? cleanup)
    {
        table.Add(task, cleanup);
        _ = Finish();
        async Task Finish()
        {
            try { await task; } catch { }
            Owner.Host.Dispatcher.Invoke(() => { if (table.Remove(task, out var done)) done?.Invoke(); });
        }
    }
    internal async Task DrainCallsAsync()
    {
        try { await Task.WhenAll(Work.Keys.ToArray()).WaitAsync(Owner.Host.Options.CloseTimeout, Owner.Host.Options.TimeProvider); }
        catch (TimeoutException) { Owner.Host.Report(new("drain_timeout", Object: Owner.Label)); }
        catch { }
    }
    internal void BeginDrain() { drainStarted = true; OldWorkState.Draining = true; }
    internal async Task DrainCallsAsync(DateTimeOffset due)
    {
        for (;;)
        {
            // Cleanup continuations may still be queued on the dispatcher after the originals settled.
            var work = Work.Keys.Where(work => !work.IsCompleted).ToArray();
            if (work.Length == 0) return;
            var remaining = due - Owner.Host.Options.TimeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) { Owner.Host.Report(new("drain_timeout", Object: Owner.Label)); return; }
            try { await Task.WhenAll(work).WaitAsync(remaining, Owner.Host.Options.TimeProvider); }
            catch (TimeoutException) { Owner.Host.Report(new("drain_timeout", Object: Owner.Label)); return; }
            catch { }
        }
    }
    internal async Task DrainAsync()
    {
        if (!drainStarted) await DrainCallsAsync();
        try { await Task.WhenAll(Runs.Keys.ToArray()).WaitAsync(Owner.Host.Options.CloseTimeout, Owner.Host.Options.TimeProvider); }
        catch (TimeoutException) { Owner.Host.Report(new("drain_timeout", Object: Owner.Label)); } catch { }
    }
    internal void HandOver() { foreach (var job in Self.Jobs) job.HandOver(); Self.StopRoutines(); }
    internal void Seal(ChordError error, string runReason = "restart", bool keepSources = false, bool keepContributions = false)
    {
        if (Sealed) return;
        Sealed = true; Live = false; sealedError = error;
        OldWorkState.Draining = false;
        try { Cancellation.Cancel(); } catch (Exception cancellationError) { Owner.Host.Report(new("cancellation_failed", cancellationError, Owner.Label)); }
        Owner.Documents.Fence(Token); Wake();
        Self.StopRoutines();
        foreach (var job in Self.Jobs) job.Seal(error, runReason);
        if (!keepContributions) WithdrawContributions();
        if (!keepSources) DetachSources(error);
    }
    internal void WithdrawContributions() => Owner.Host.Batch.Within(() =>
    {
        foreach (var contribution in Self.PublishedContributions) contribution.Record.Remove(contribution);
        Self.PublishedContributions.Clear();
    }, Owner.Context);
    internal void DetachSources(ChordError error)
    {
        if (sourcesDetached) return;
        sourcesDetached = true; Self.Detach(error);
        foreach (var value in adapted.Values) value.Detach(error);
        adapted.Clear();
    }
    internal Task CloseAsync(NativeCloseReason reason)
    {
        if (closing is not null) return closing;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closing = completion.Task;
        _ = Finish();
        return closing;
        async Task Finish()
        {
            try { await CloseCoreAsync(reason); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
    private async Task CloseCoreAsync(NativeCloseReason reason)
    {
        try
        {
        if (!Sealed) Seal(new("released", $"{Owner.Label}: attempt ended", "ended"), keepSources: true);
        await DrainAsync();
        await Owner.Documents.SettledAsync();
        Owner.StopFollowing(this);
        if (!PreserveSourcesAtClose) DetachSources(sealedError ?? new("released", $"{Owner.Label}: attempt ended", "ended"));
        var failures = new List<Exception>();
        foreach (var job in Self.Jobs) try { await job.WriteAtCloseAsync(Owner.Context); } catch (Exception error) { failures.Add(error); }
        foreach (var native in Self.Natives.AsEnumerable().Reverse()) try { await native.CloseAsync(reason); } catch (Exception error) { failures.Add(error); }
        if (!PreserveReferencesAtClose) Self.ReleaseReferences();
        foreach (var table in new[] { Work, Runs }) { foreach (var cleanup in table.Values) cleanup?.Invoke(); table.Clear(); }
        Cancellation.Dispose();
        if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { RetirementEnded.TrySetResult(); }
    }
    private sealed class ActivationException(FailureCause cause) : Exception(cause.Error.Message) { internal FailureCause Cause => cause; }
}

internal sealed partial class Self : IStoreSelf, IKeyDeclarer, IJobOwner, IWritableOwner
{
    internal readonly Attempt Attempt;
    internal bool Declaring = true;
    internal readonly Dictionary<string, StateDefinition> Definitions = new(StringComparer.Ordinal);
    internal readonly List<Action> Pins = [];
    internal readonly List<Binding> Dependencies = [];
    internal readonly List<object> RetrySources = [];
    internal readonly List<RecordedRequest> Requests = [];
    internal readonly List<RegistryDependency> RegistryDependencies = [];
    internal readonly List<Derive> Derives = [];
    internal readonly List<INativeSlot> Natives = [];
    internal readonly List<FollowSource> Follows = [];
    internal readonly List<IHostedJob> Jobs = [];
    internal readonly List<TableHandle> Tables = [];
    internal readonly List<LiveQuery> Queries = [];
    internal readonly List<ContributionDeclaration> Contributions = [];
    internal readonly List<RegistryContribution> PublishedContributions = [];
    private readonly List<Action<Exception>> detach = [];
    private readonly HashSet<string> jobNames = new(StringComparer.Ordinal);
    private readonly List<Func<Task>> routines = [];
    private readonly CancellationTokenSource routineCancellation = new();
    internal Self(Attempt attempt) => Attempt = attempt;
    public ChordHost Host => Attempt.Owner.Host;
    public Facet? DeclaringFacet => Attempt.Served.DeclaringFacet;
    public Caller Caller => new(Attempt.Served.Contract.Kind == ContractKind.Local ? Host.Options.Principal : Attempt.Owner.Caller.Principal, Host.Incarnation);
    public ChordContext Context => ChordContext.WithValue(OldWork.Opening, Attempt.Owner, ChordContext.WithAbortSignal(Attempt.Cancellation.Token, Attempt.OpeningContext));
    public CancellationToken Signal => Attempt.Cancellation.Token;
    public SealToken Token => Attempt.Token;
    public Documents.Documents Documents => Attempt.Owner.Documents;
    public DocumentsStore? Store => Attempt.Owner.Store;
    public void Admit(Task work, Action? cleanup = null) => Attempt.Admit(work, cleanup);
    public void AdmitRun(Task work, Action cleanup) => Attempt.AdmitRun(work, cleanup);
    public void Report(HostError error) => Host.Report(error);
    public void CountRuns(int by) => Attempt.Owner.CountRuns(by);
    public void AssertUsable() { if (Attempt.Sealed) throw new ChordError("released", "attempt ended", "ended"); }
    private void Declare(string what) { AssertUsable(); if (!Declaring) throw new InvalidOperationException($"{Attempt.Owner.Label}: {what} is valid only in the factory"); }
    public ISingletonHandle State(StateDefinition definition) => (ISingletonHandle)StateCore(definition, false);
    public IKeyedHandle KeyedState(StateDefinition definition)
    {
        if (definition.KeyHost is not null) throw new ArgumentException("Use HostKeyedState for an address-keyed definition");
        return (IKeyedHandle)StateCore(definition, true);
    }
    private object StateCore(StateDefinition definition, bool keyed)
    {
        Declare("state");
        if (definition.Keyed != keyed) throw new ArgumentException("State and KeyedState require the corresponding definition");
        if (!Definitions.TryAdd(definition.Name, definition)) throw new ArgumentException($"state {definition.Name} declared twice");
        if (definition.Persist)
        {
            if (Store is not { } storage) throw new ArgumentException("persisted state needs a local empty-param object and a store");
            storage.Store.Claims.Documents(storage.Owner, "chord", Attempt.Owner);
        }
        return Documents.Declare(definition, Token, this);
    }
    public KeySource At(KeyedSlot slot, string key)
    {
        Declare("at"); Pins.Add(slot.Pin(key, Context)); return slot.Source(key)!;
    }
    public IWatchable<JsonNode?> Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute)
    { Declare("derive"); var derive = new Derive(sources, compute, Host.Batch); Derives.Add(derive); return derive; }
    public IReference Get(Contract type, JsonObject parameters)
    {
        Declare("get"); var binding = Host.Acquire(type, parameters, Caller, Context, dependent: Attempt.Owner);
        Requests.Add(new(type.Name, type.Version, type, binding.Parameters.Value));
        binding.Changed += Attempt.Wake; Dependencies.Add(binding); return binding;
    }
    public IRegistryValue<V> Registry<C, V>(Registry<C, V> registry)
    {
        Declare("registry"); Requests.Add(new(registry.Name, registry.Version)); var value = Host.RegistryRecord(registry.Name).Value<V>(registry.Version);
        var dependency = new RegistryDependency(registry.Name, () => { var start = value.Start(); return start.Failed && !start.HasLast ? start.Error : null; }, value.Read);
        var waiter = new WakeWaiter(Attempt.Wake);
        dependency.Release = value.Wait(waiter); RegistryDependencies.Add(dependency); return new DispatchRegistryValue<V>(value, Host.Dispatcher);
    }
    public IWritable<T> Writable<T>(T initial)
    {
        Declare("writable"); var source = new WritableSource<T>(initial, this, Host.Batch); detach.Add(source.Detach); return source;
    }
    public void RetryOn(params object[] sources)
    {
        Declare("retryOn");
        foreach (var source in sources)
        {
            var resolved = ChordHost.ResolveSource(source);
            if (resolved is not Source && resolved is not NativeSource && resolved is not Constant) throw new ArgumentException("retryOn requires Chord sources");
            RetrySources.Add(source);
        }
    }
    public INative<T> Open<T>(Func<ChordContext, Task<T>> open, Func<T, NativeCloseReason, ChordContext, Task> close, NativeOptions? options = null)
    {
        Declare("open"); ArgumentNullException.ThrowIfNull(open); ArgumentNullException.ThrowIfNull(close);
        if (options?.CloseTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        var native = new NativeHandle<T>(Attempt, Natives.Count + 1, open, close, options); Natives.Add(native); return native;
    }
    public Task<T> TxAsync<T>(Func<ITx, Task<T>> work, ChordContext context)
    {
        HostTransactions.AssertOutside("self.tx");
        return Host.Dispatcher.RunAsync(() => Documents.TxAsync(async tx =>
        {
            using var lease = Host.RetainCallback(work, Attempt.Served.DeclaringFacet);
            HostTransactions.InTransaction.Value = true;
            try { return await work(tx); }
            finally { HostTransactions.InTransaction.Value = false; }
        }, Token, context));
    }
    public IWatchable<JsonNode?> Follow(FollowAttach attach)
    { Declare("follow"); var source = new FollowSource(Attempt, attach); Follows.Add(source); return source; }
    public ITable Table(TableDefinition definition)
    {
        Declare("table"); if (Store is not { } storage) throw new ArgumentException("tables need a local empty-param object and store");
        if (Tables.Any(t => t.Definition.Name == definition.Name)) throw new ArgumentException("table declared twice");
        var name = StoreInternal.TableSqlName(storage.Owner, definition.Name);
        storage.Store.Claims.Table(name, Attempt.Owner);
        var table = new TableHandle(definition, name, storage.Store, Attempt.Owner); Tables.Add(table); return table;
    }
    public void Contribute<C, V>(Registry<C, V> registry, object? value)
    {
        Declare("contribute"); if (!Attempt.Served.HostLifetime) throw new ArgumentException("only a local empty-param object contributes");
        if (Contributions.Any(c => c.Name == registry.Name)) throw new ArgumentException("registry contributed twice");
        Contributions.Add(new(registry.Name, registry.Version, value));
    }
    internal IHostedJob DeclareJob(string name, Func<IJobOwner, IHostedJob> create)
    {
        Declare("job"); if (!jobNames.Add(name)) throw new ArgumentException($"job {name} declared twice");
        var job = create(this); Jobs.Add(job); return job;
    }
    internal void Detach(Exception error)
    {
        foreach (var action in detach) action(error); detach.Clear();
        foreach (var derive in Derives) derive.Detach(error);
        foreach (var query in Queries) query.Detach(error);
        foreach (var follow in Follows)
            try { follow.Detach(); } catch (Exception failure) { Report(new("follow_detach_failed", failure, Attempt.Owner.Label)); }
    }
    internal void ReleaseReferences()
    {
        foreach (var binding in Dependencies) { binding.Changed -= Attempt.Wake; binding.Release(); } Dependencies.Clear();
        foreach (var registry in RegistryDependencies) registry.Release(); RegistryDependencies.Clear();
    }
    public void On(IWatchable<JsonNode?> source, Func<WatchValueCall<JsonNode?>, CallContext, Task> handler)
    {
        Declare("on"); var position = routines.Count + 1; routines.Add(async () =>
        {
            var watch = await source.WatchAsync(RoutineContext());
            await RunRoutine(handler, ctx => handler(new(watch.Value, [new Delta.ReplaceOp(watch.Value)]), ctx), "on", position);
            if (routineCancellation.IsCancellationRequested) { await watch.StopAsync(); return; }
            watch.Start(async (call, frame) => { if (call is WatchValueCall<JsonNode?> value) await RunRoutine(handler, ctx => handler(value, ctx), "on", position, frame.Telemetry); });
            await watch.Closed;
        });
    }
    public void On<T>(INativeWatchable<T> source, Func<NativeWatchValueCall<T>, CallContext, Task> handler)
    {
        Declare("on"); var position = routines.Count + 1; routines.Add(async () =>
        {
            var watch = await source.WatchAsync(RoutineContext());
            await RunRoutine(handler, ctx => handler(new(watch.Value), ctx), "on", position);
            if (routineCancellation.IsCancellationRequested) { await watch.StopAsync(); return; }
            watch.Start(async (call, frame) => { if (call is NativeWatchValueCall<T> value) await RunRoutine(handler, ctx => handler(value, ctx), "on", position, frame.Telemetry); });
            await watch.Closed;
        });
    }
    public void OnLive(Func<CallContext, Task> handler) { Declare("onLive"); var position = routines.Count + 1; routines.Add(() => RunRoutine(handler, handler, "onLive", position)); }
    private ChordContext RoutineContext() => ChordContext.WithAbortSignal(routineCancellation.Token, Attempt.Owner.Context);
    internal void StartRoutines()
    {
        foreach (var routine in routines)
        {
            if (Attempt.Sealed) break;
            _ = Start(routine);
        }
        async Task Start(Func<Task> routine)
        {
            try { await routine(); }
            catch (OperationCanceledException) when (Attempt.Sealed || routineCancellation.IsCancellationRequested) { }
            catch (Exception error) { Report(new("routine_failed", error, Attempt.Owner.Label)); }
        }
    }
    internal void StopRoutines()
    {
        try { routineCancellation.Cancel(); } catch (Exception error) { Report(new("cancellation_failed", error, Attempt.Owner.Label)); }
    }
    private async Task RunRoutine(Delegate callback, Func<CallContext, Task> handler, string kind, int position, ITelemetryContext? trigger = null)
    {
        var delay = 1000;
        while (!routineCancellation.IsCancellationRequested && !Attempt.Sealed)
        {
            OpenSpan? span = null;
            if (!ChordTelemetry.IsUntraced(Host.Telemetry))
            {
                var attributes = Host.Attributes(Attempt.Served.Contract.Name, Attempt.Owner.Label);
                attributes["routine"] = kind; attributes["position"] = (double)position;
                span = new OpenSpan(Host.Telemetry, "chord.routine", attributes);
                if (trigger is not null && !ChordTelemetry.IsUntraced(trigger)) span.Span.AddLink(new(trigger));
            }
            var parent = span is null ? RoutineContext() : ChordContext.WithTelemetry(span.Span, RoutineContext());
            using var context = new CallContext(Host, Caller, parent);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Admit(completion.Task);
            try { await Host.InvokeCallbackAsync(callback, () => handler(context), DeclaringFacet); span?.Span.SetAttributes(new SpanAttributes { ["outcome"] = "ok" }); completion.TrySetResult(); return; }
            catch (Exception error)
            {
                completion.TrySetResult();
                if (span is not null)
                {
                    var outcome = ChordTelemetry.Ended(span.Span, "routine_failed", error, context.AbortSignal);
                    span.Span.SetAttributes(new SpanAttributes { ["outcome"] = outcome });
                    if (outcome == "error") span.Span.AddEvent("chord.retry", new SpanAttributes { ["backoff"] = (double)delay });
                }
                if (context.AbortSignal.IsCancellationRequested) return;
                Report(new("routine_failed", error, Attempt.Owner.Label));
                try { await Task.Delay(TimeSpan.FromMilliseconds(delay), Host.Options.TimeProvider, routineCancellation.Token); } catch (OperationCanceledException) { return; }
                delay = Math.Min(60000, delay * 2);
            }
            finally { span?.End(); }
        }
    }
    internal sealed class RegistryDependency(string name, Func<Exception?> error, Func<Action> read)
    {
        internal string Name => name;
        internal Func<Exception?> Error => error;
        internal Action Release = () => { };
        internal void Read() { Release(); Release = read(); }
    }
    private sealed class WakeWaiter(Action wake) : IRegistryWaiter { public void DependencyMoved() => wake(); public void Closed(ChordError error) => wake(); }
    private sealed class RetryObserver(ChordObject owner) : ISubscriber
    {
        public void Advance(WatchCall<JsonNode?> call, ChordContext context) { if (call is WatchValueCall<JsonNode?>) owner.Host.Dispatcher.Post(() => owner.Retry(new(new(ReferenceEqualityComparer.Instance)))); }
        public void End(WatchEnd end) { } public void EndAfterDelivery() { }
    }
}
