// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/object.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

internal sealed partial class ChordObject : ITarget, IDocumentsOwner, ITableOwner, IClaimant
{
    internal readonly ChordHost Host;
    internal readonly string Identity;
    internal ServedType Served;
    internal readonly CheckedParams Parameters;
    internal readonly Caller Caller;
    internal Attempt? Current;
    internal readonly Documents.Documents Documents;
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
    internal ChordObject(ChordHost host, string identity, ServedType served, CheckedParams parameters, Caller caller)
    {
        Host = host; Identity = identity; Served = served; Parameters = parameters;
        Caller = served.Contract.Kind == ContractKind.Local ? host.OwnCaller : caller;
        Label = parameters.Key.Length == 0 ? served.Contract.Name : $"{served.Contract.Name}({parameters.Key})";
        Documents = new(this, host.Options.TimeProvider);
    }
    public string Label { get; }
    public RefState State => state;
    public bool Hydrated => Current?.Live == true;
    public string? Variant => FollowingAttempt?.Implementation?.Variant;
    public ChordError? LastFailure => state.Cause?.Error;
    public string? ActivationId => Current is { Live: false, Sealed: false } attempt ? attempt.InstanceId : null;
    public bool BlocksFirstPass => !firstOutcome.Task.IsCompleted;
    public object? Instance => FollowingAttempt;
    internal Task FirstOutcome => firstOutcome.Task;
    internal Task EndCompletion => endingTask ?? Task.CompletedTask;
    public Batch Batch => Host.Batch;
    public ChordContext Context => Host.Context;
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
        _ = Host.Dispatcher.RunAsync(async () =>
        {
            try { await previousClose; } catch { }
            foreach (var predecessor in Host.EndingObjects.Where(obj => obj.Identity == Identity && !ReferenceEquals(obj, this)).ToArray())
                try { await predecessor.EndCompletion; } catch { }
            if (!ending && ReferenceEquals(Current, attempt)) await attempt.ActivateAsync();
        });
    }
    internal void SetState(RefState next)
    {
        if (state == next) return;
        state = next;
        Host.Batch.Within(() => { foreach (var holder in Holders.ToArray()) holder.Retarget(); }, Context);
        foreach (var holder in Holders.ToArray()) holder.Moved();
        Host.ChangedPresence();
    }
    internal void FirstChecked(bool complete) { if (complete) firstOutcome.TrySetResult(); }
    internal void WentLive(Attempt attempt)
    {
        if (ending || !ReferenceEquals(Current, attempt)) return;
        LiveOrder = Host.NextLiveOrder();
        failures = 0; SetState(RefState.Live); firstOutcome.TrySetResult();
        if (!Served.HostLifetime && Holders.Count == 0 && runs == 0) ScheduleEnd();
    }
    internal void Failed(Attempt attempt, FailureCause cause)
    {
        if (ending || !ReferenceEquals(Current, attempt)) return;
        if (attempt.Live) swappingSource = attempt;
        attempt.Seal(cause.Error, keepSources: true);
        previousClose = attempt.CloseAsync(NativeCloseReason.End);
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
    public Task<object?> CallAsync(string method, object? args, ChordContext context, Caller caller, long version) =>
        Current?.CallAsync(method, args, context, caller, version) ?? Task.FromException<object?>(HostErrors.NotReady(Label));
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
        return new(state.Kind == "failed" ? "failed" : "loading", Error: state.Cause?.Error, Instance: current);
    }
    public IKeyedHandle KeyedMember(string member, long version) =>
        FollowingAttempt is { HasSources: true } current && current.Members.GetValueOrDefault(member) is IKeyedHandle handle ? current.AdaptedKeyed(member, handle, version) : throw HostErrors.InvalidMember(member);
    internal (IWatchable<JsonNode?> Source, Action Release, Task Loaded) PinKey(string member, string key, long version)
    {
        var current = FollowingAttempt;
        if (current?.HasSources != true || !current.Members.TryGetValue(member, out var memberValue)) throw HostErrors.NotReady(Label);
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
    public void Retry(RetryWalk walk)
    {
        if (!walk.Visited.Add(this) || ending) return;
        if (Current is { Live: false } attempt && attempt.Self.Dependencies.Count > 0)
            foreach (var dependency in attempt.Self.Dependencies) dependency.Target?.Retry(walk);
        if (state.Kind == "failed" && state.Cause?.Kind != FailureKind.Waiting) BeginAttempt();
        else Current?.Wake();
    }
    public bool Reaches(ITarget target, HashSet<ITarget> visited) => ReferenceEquals(this, target) ||
        (visited.Add(this) && Current is { } attempt && attempt.Self.Dependencies.Any(d => d.Target?.Reaches(target, visited) == true));
    public void AssertLive() => Documents.AssertLive();
    public void Fail(ChordError error) { if (Current is { Live: true } attempt) Failed(attempt, new(FailureKind.Broken, error)); }
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
        await Documents.SettledAsync(); Documents.End(); Host.Store?.Claims.Release(this);
        if (Host.Objects.TryGetValue(Identity, out var current) && ReferenceEquals(current, this)) Host.Objects.Remove(Identity);
        Current = null; OldAttempts.Clear();
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
