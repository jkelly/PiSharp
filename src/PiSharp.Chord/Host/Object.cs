// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/object.ts
// 1.6.0 here: UnbindSets (chord-store), calls for a holder (chord-remote), and in Host/NamedQuery.cs a partial with what its lists keep
// across its attempts and its queries members (chord-queries). What a change of an admitted type's admission does to it (Readmit, Rebuild,
// SealDraining, FollowFailed, the cut and presence, slice 14 §2.2) is a partial in Host/Admission.cs; here: an attempt of an admitted
// type is handed the previous teardown instead of started behind it, closes accumulate, a denied state is no failure a retry restarts,
// and a denied object's members are followed as `denied`.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;
using PiSharp.Chord.State;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

internal sealed partial class ChordObject : ITarget, IDocumentsOwner, ITableOwner, IClaimant
{
    internal readonly ChordHost Host;
    internal readonly string Identity;
    internal ServedType Served;
    internal readonly CheckedParams Parameters;
    internal readonly Caller Caller;
    internal readonly ITelemetryContext Trigger;
    internal Attempt? Current;
    internal readonly Documents.Documents Documents;
    // The column sets its tables exposed are served no more (slice 14 §5): reads over them fail unavailable.
    internal void UnbindSets()
    {
        if (Host.Store?.ReadIndex is not { } reads) return;
        foreach (var set in reads.BoundBy(this)) reads.Unbind(set, this);
    }
    internal readonly List<Attempt> OldAttempts = [];
    internal long LiveOrder;
    internal readonly HashSet<IHolder> Holders = new(ReferenceEqualityComparer.Instance);
    private readonly TaskCompletionSource firstOutcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RefState state = RefState.Loading;
    private bool ending;
    private Task? endingTask;
    private CancellationTokenSource? retry;
    private CancellationTokenSource? linger;
    private int failures;
    private int runs;
    private bool retryPending;
    private Task previousClose = Task.CompletedTask;
    private readonly List<Action> retryDetach = [];
    internal ChordObject(ChordHost host, string identity, ServedType served, CheckedParams parameters, Caller caller, ChordContext? context = null)
    {
        Host = host; Identity = identity; Served = served; Parameters = parameters;
        Caller = served.Contract.Kind == ContractKind.Local ? host.OwnCaller : caller;
        Trigger = context is null ? host.Telemetry : ChordHost.ParentOf(context);
        Label = parameters.Key.Length == 0 ? served.Contract.Name : $"{served.Contract.Name}({parameters.Key})";
        Documents = new(this, host.Options.TimeProvider);
    }
    public string Label { get; }
    public RefState State => state;
    public bool Hydrated => Current?.Live == true || OpenOldAttempt is not null;
    public RefState StateFor(long version, string fingerprint) => state.Kind == "loading" && OldServes(version, fingerprint) ? RefState.Live : state;
    internal bool OldServes(long version, string fingerprint) => OpenOldAttempt is { } old && DataInternal.Binds(old.Served.Provider, version, fingerprint);
    public string? Variant => FollowingAttempt?.Implementation?.Variant;
    public ChordError? LastFailure => state.Cause?.Error;
    public string? ActivationId => Current is { Live: false, Sealed: false } attempt ? attempt.InstanceId : null;
    public bool BlocksFirstPass => !firstOutcome.Task.IsCompleted;
    public object? Instance => FollowingAttempt;
    internal Task FirstOutcome => firstOutcome.Task;
    internal Task EndCompletion => endingTask ?? Task.CompletedTask;
    public Batch Batch => Host.Batch;
    public ChordContext Context => Host.Context;
    ITelemetryContext IDocumentsOwner.Telemetry => Host.Telemetry;
    public DocumentsStore? Store => Host.Store is { } store && Served.HostLifetime ? new(store, Served.Contract.Name) : null;
    object ITableOwner.Scope => Documents;
    internal void BeginAttempt()
    {
        if (ending || Host.Closed) return;
        retry?.Cancel(); retry?.Dispose(); retry = null;
        var attempt = new Attempt(this, Served);
        retryPending = false;
        Current = attempt;
        SetState(RefState.Loading);
        Activate(attempt);
    }
    // Starts attempt in a later turn (posted, never inline: a seal's flush has ended by then). An admitted type's attempt runs its
    // admission before it waits for the previous teardown (slice 14 §2.2: only its factory waits for that); any other is started once the
    // teardown finished.
    private void Activate(Attempt attempt) => _ = Host.Dispatcher.RunAsync(async () =>
    {
        if (attempt.Served.Admission is null) await Ended();
        if (ending || !ReferenceEquals(Current, attempt)) return;
        if (attempt.Served.Admission is null) await attempt.ActivateAsync(); else await attempt.ActivateAsync(Ended);
    });
    // The teardown a factory waits for: the closes of the attempts before it, and an ending object of the same identity.
    private async Task Ended()
    {
        try { await previousClose; } catch { }
        foreach (var predecessor in Host.EndingObjects.Where(obj => obj.Identity == Identity && !ReferenceEquals(obj, this)).ToArray())
            try { await predecessor.EndCompletion; } catch { }
    }
    internal void SetState(RefState next)
    {
        if (state == next) return;
        // The same denial again (a new attempt's own admission reads the denial its predecessor's cut showed): nothing new to tell.
        if (next.Cause is { Kind: FailureKind.Denied } denial && state.Cause is { Kind: FailureKind.Denied } shown && DataInternal.SameCause(shown, denial)) return;
        state = next;
        // Shown denied (a parked attempt's own check found the denial): presence lists its holders no more, until its next allow.
        if (next.Cause?.Kind == FailureKind.Denied) allowed = false;
        Notify();
    }
    // Tells every holder what the object shows now (also when the state is the one it had: a cut changes what its holders follow).
    private void Notify()
    {
        if (state.Kind == "failed" && OpenOldAttempt is null)
            foreach (var previous in OldAttempts) previous.WithdrawContributions();
        var before = stateChanged; stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously); before.TrySetResult();
        Host.Batch.Within(() => { foreach (var holder in Holders.ToArray()) holder.Retarget(); }, Context);
        foreach (var holder in Holders.ToArray()) holder.Moved();
        Host.ChangedPresence();
    }
    internal void FirstChecked(bool complete) { if (complete) firstOutcome.TrySetResult(); }
    internal void WentLive(Attempt attempt)
    {
        if (ending || !ReferenceEquals(Current, attempt)) return;
        LiveOrder = Host.NextLiveOrder();
        Allow();
        failures = 0; SetState(RefState.Live); firstOutcome.TrySetResult();
        if (!Served.HostLifetime && Holders.Count == 0 && runs == 0) ScheduleEnd();
    }
    internal void Failed(Attempt attempt, FailureCause cause)
    {
        if (ending || !ReferenceEquals(Current, attempt)) return;
        if (attempt.Live) swappingSource = attempt;
        attempt.Seal(cause.Error, keepSources: true);
        // An admitted type's attempt can fail (a throwing admit) while its predecessor still closes: the next factory waits for both.
        var closing = attempt.CloseAsync(NativeCloseReason.End);
        previousClose = previousClose.IsCompleted ? closing : Task.WhenAll(Settled(previousClose), Settled(closing));
        var delay = TimeSpan.FromMilliseconds(Math.Min(60000, 1000 * Math.Pow(2, Math.Min(failures++, 6))));
        SetState(RefState.Failed(cause, cause.Kind == FailureKind.Transient ? Host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() + delay.TotalMilliseconds : null));
        firstOutcome.TrySetResult();
        Host.Report(new("activation_failed", Object: Label, Failure: cause));
        if (!Served.HostLifetime && Holders.Count == 0)
        {
            if (linger is not null) { linger.Cancel(); linger.Dispose(); linger = null; Host.CountLinger(-1); }
            ScheduleEnd();
            if (ending) return;
        }
        if (retryPending) { Host.Dispatcher.Post(BeginAttempt); return; }
        if (cause.Kind != FailureKind.Transient) return;
        retry = new CancellationTokenSource();
        _ = RetryLater(delay, retry.Token);
    }
    private static async Task Settled(Task work) { try { await work; } catch { } }
    private async Task RetryLater(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, Host.Options.TimeProvider, token); if (!ending) BeginAttempt(); }
        catch (OperationCanceledException) { }
    }
    public void Hold(IHolder holder)
    {
        Holders.Add(holder);
        if (linger is not null) { linger.Cancel(); linger.Dispose(); linger = null; Host.CountLinger(-1); }
    }
    public void Unhold(IHolder holder)
    {
        Holders.Remove(holder);
        if (!Holders.Any(h => h.Contract.Version == holder.Contract.Version)) Current?.DropAdaptation(holder.Contract.Version);
        if (!Served.HostLifetime && Holders.Count == 0 && runs == 0) ScheduleEnd();
    }
    internal void CountRuns(int by)
    {
        runs += by; Host.CountJob(by);
        if (by > 0 && linger is not null) { linger.Cancel(); linger.Dispose(); linger = null; Host.CountLinger(-1); }
        if (runs == 0 && !Served.HostLifetime && Holders.Count == 0) ScheduleEnd();
    }
    private void ScheduleEnd()
    {
        if (ending || Host.Closed || (runs > 0 && Current?.Sealed == false) || linger is not null) return;
        var duration = Served.Options.Linger ?? TimeSpan.Zero;
        if (state.Kind != "live" || duration <= TimeSpan.Zero) { _ = EndAsync(new("released", $"{Label}: ended", "ended"), NativeCloseReason.End); return; }
        linger = new CancellationTokenSource(); Host.CountLinger(1);
        _ = LingerAsync(duration, linger.Token);
    }
    private async Task LingerAsync(TimeSpan duration, CancellationToken token)
    {
        try { await Task.Delay(duration, Host.Options.TimeProvider, token); if (!ending && Holders.Count == 0 && runs == 0) await EndAsync(new("released", $"{Label}: ended", "ended"), NativeCloseReason.End); }
        catch (OperationCanceledException) { }
    }
    private TaskCompletionSource stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<ChordObject> gateWaits = new(ReferenceEqualityComparer.Instance);
    internal IEnumerable<ChordObject> WaitEdges() => gateWaits
        .Concat(Current?.Self.Dependencies.Concat(Current.AdmissionBindings).Select(binding => binding.Target).OfType<ChordObject>() ?? [])
        .Concat(readiness?.Targets ?? []);
    /// <summary>Calls a method of the live object for a reference's holder: as its caller, at its version (<see cref="CallAs"/>).</summary>
    public Task<object?> CallAsync(string method, object? args, IHolder holder, ChordContext context) =>
        CallAs(method, args, context, holder.Caller, holder.Contract.Version);
    internal async Task<object?> CallAs(string method, object? args, ChordContext context, Caller caller, long version)
    {
        if (Host.OldWork.Of(context) && OpenOldAttempt is { } old)
            return await old.CallAsync(method, args, context, caller, version);
        var waiting = GateAsync(context);
        if (Host.OldWork.Of(context))
            await Host.ReloadCycles.WaitAsync(waiting, this, Current?.Served.Contract ?? Served.Contract, context);
        else await waiting;
        if (state.Kind == "ended") throw state.Error!;
        if (state.Cause is { } failure) throw failure.Error;
        return await (Current ?? throw HostErrors.NotReady(Label)).CallAsync(method, args, context, caller, version);
    }
    private async Task GateAsync(ChordContext context)
    {
        var opener = context.Value(OldWork.Opening);
        if (opener is not null) { opener.gateWaits.Add(this); Host.ReloadCycles.Recheck(); }
        var due = Host.Options.TimeProvider.GetUtcNow() + Host.Options.ReadyTimeout;
        try
        {
            while (state.Kind == "loading")
            {
                var left = due - Host.Options.TimeProvider.GetUtcNow();
                if (left <= TimeSpan.Zero) throw new ChordError("unavailable", $"{Label}: a call waited for its object to go live", "park_timeout");
                try { await stateChanged.Task.WaitAsync(left, Host.Options.TimeProvider, context.AbortSignal ?? CancellationToken.None); }
                catch (TimeoutException) { throw new ChordError("unavailable", $"{Label}: a call waited for its object to go live", "park_timeout"); }
            }
        }
        finally { opener?.gateWaits.Remove(this); }
    }
    public Followed Followed(string member, long version)
    {
        var current = FollowingAttempt;
        if (current?.HasSources == true)
        {
            try
            {
                if (version == current.Served.Contract.Version && !current.Members.ContainsKey(member)) return new("lacking", Error: HostErrors.InvalidMember(member));
                return new("live", current.AdaptedMember(member, version), current);
            }
            catch (ChordError error) { return new("failed", Error: error); }
        }
        // A denial keeps no value (slice 14 §2.2): what the caller may read is withdrawn with its access.
        if (state.Cause is { Kind: FailureKind.Denied } denied) return new("denied", Error: denied.Error);
        return new(state.Kind == "failed" ? "failed" : "loading", Error: state.Cause?.Error, Instance: current);
    }
    public IKeyedHandle KeyedMember(string member, long version)
    {
        if (FollowingAttempt is not { HasSources: true } current) throw HostErrors.InvalidMember(member);
        var value = current.Members.GetValueOrDefault(member);
        var handle = value is HostKeyed hostKeyed ? hostKeyed.Inner : value as IKeyedHandle;
        return handle is not null ? current.AdaptedKeyed(member, handle, version) : throw HostErrors.InvalidMember(member);
    }
    internal (IWatchable<JsonNode?> Source, Action Release, Task Loaded) PinKey(string member, string key, long version)
    {
        var current = FollowingAttempt;
        if (current?.HasSources != true || !current.Members.TryGetValue(member, out var memberValue)) throw HostErrors.NotReady(Label);
        if (memberValue is HostKeyed hostKeyed) memberValue = hostKeyed.Inner;
        if (memberValue is Keyed own)
        {
            var release = own.Slot.Pin(key, Context);
            try
            {
                var source = own.Slot.Source(key)!;
                var mapped = current.HoldKeySource(member, source, version);
                return (mapped.Source, () => { mapped.Release(); release(); }, own.Slot.EnsureAsync(key, Context));
            }
            catch { release(); throw; }
        }
        if (memberValue is IKeyedHandle other)
        {
            var source = other.At(key);
            var mapped = current.HoldKeySource(member, source, version);
            return (mapped.Source, mapped.Release, Task.CompletedTask);
        }
        throw HostErrors.InvalidMember(member);
    }
    public void Retry(RetryWalk walk, IHolder? holder = null)
    {
        if (!walk.Visited.Add(this) || ending) return;
        if (Current is { Live: false } attempt)
            foreach (var dependency in attempt.Self.Dependencies.Concat(attempt.AdmissionBindings).ToArray()) dependency.Target?.Retry(walk, dependency);
        // An attempt parked on its admission's denial is still activating: a retry starts nothing, it only passes on and checks again.
        var parkedDenied = state.Cause?.Kind == FailureKind.Denied && Current is { Sealed: false, Live: false };
        if (state.Kind == "failed" && state.Cause?.Kind != FailureKind.Waiting && !parkedDenied) BeginAttempt();
        else Current?.Wake();
    }
    public bool Reaches(ITarget target, HashSet<ITarget> visited) => ReferenceEquals(this, target) ||
        (visited.Add(this) && Current is { } attempt && attempt.Self.Dependencies.Concat(attempt.AdmissionBindings).Any(d => d.Target?.Reaches(target, visited) == true));
    public void AssertLive() => Documents.AssertLive();
    public void Fail(ChordError error) { if (Current is { Live: true } attempt) Failed(attempt, new(FailureKind.Broken, error)); }
    public void ReportTransactionCancelled(Exception error) => Host.Report(new("transaction_cancelled", error, Object: Label));
    internal void InstallRetrySources(IEnumerable<object> sources)
    {
        foreach (var detach in retryDetach) detach();
        retryDetach.Clear();
        foreach (var input in sources)
        {
            var source = ChordHost.ResolveSource(input);
            var trigger = new RetryTrigger(this, source!);
            if (source is Source json) { json.Dependents.Add(trigger); retryDetach.Add(() => json.Dependents.Remove(trigger)); }
            else if (source is NativeSource native) { native.Dependents.Add(trigger); retryDetach.Add(() => native.Dependents.Remove(trigger)); }
        }
    }
    private sealed class RetryTrigger(ChordObject owner, object source) : IDependent
    {
        private bool scheduled;
        public void Invalidate()
        {
            if (scheduled) return; scheduled = true;
            owner.Host.Dispatcher.Post(() =>
            {
                scheduled = false;
                if (owner.ending) return;
                try
                {
                    if (source is Source json && json.Start().Failed) return;
                    if (source is NativeSource native) _ = native.UntypedCurrent();
                }
                catch { return; }
                owner.retryPending = true;
                if (owner.State.Kind == "failed") owner.BeginAttempt();
            });
        }
        public void SourceRetired(ReferenceSet<IDependent> visited) { }
    }
    internal void Seal(ChordError error)
    {
        if (ending) return;
        ending = true; retry?.Cancel();
        foreach (var detach in retryDetach) detach(); retryDetach.Clear();
        if (linger is not null) { linger.Cancel(); linger.Dispose(); linger = null; Host.CountLinger(-1); }
        Documents.Seal(); Current?.Seal(error, keepSources: true);
        openOld?.Seal(error, keepSources: true); readiness?.Release();
        foreach (var previous in OldAttempts) previous.WithdrawContributions();
        SetState(RefState.Ended(error)); firstOutcome.TrySetResult();
        foreach (var holder in Holders.ToArray()) holder.End(error);
        Holders.Clear();
    }
    internal Task EndAsync(ChordError error, NativeCloseReason reason)
    {
        if (endingTask is not null) return endingTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endingTask = completion.Task;
        Host.EndingObjects.Add(this);
        Seal(error);
        _ = Finish();
        return endingTask;
        async Task Finish()
        {
            try { await FinishEndAsync(reason); completion.TrySetResult(); }
            catch (Exception failure) { completion.TrySetException(failure); }
            finally { Host.EndingObjects.Remove(this); }
        }
    }
    private async Task FinishEndAsync(NativeCloseReason reason)
    {
        var failures = new List<Exception>();
        foreach (var attempt in OldAttempts.Append(Current).Where(a => a is not null).Cast<Attempt>().Distinct().ToArray())
            try { await attempt.CloseAsync(reason); } catch (Exception error) { failures.Add(error); }
        ReleaseAdmissions();
        await Documents.SettledAsync(); Documents.End(); Host.Store?.Claims.Release(this);
        if (Host.Objects.TryGetValue(Identity, out var current) && ReferenceEquals(current, this)) Host.Objects.Remove(Identity);
        Current = null; OldAttempts.Clear();
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
