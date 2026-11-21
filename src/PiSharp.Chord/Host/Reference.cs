// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/{reference,reference-source,ref-state}.ts
// 1.6.0 here: a binding's event members (decision "Events: fire-and-forget members"), and what references sharing one wire hold need
// (decision "References share one wire hold"): calls and retries name their binding, a binding joining a bound remote object records
// the members it has not seen (Joined, Seen), and a retry alone moves it to a private object (Transfer). Named queries (QueryMember,
// FollowInstance) are a partial of Binding in Host/NamedQuery.cs; the denied state is chord-admission's.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

internal sealed partial class Binding : IReference, IHolder
{
    private ChordHost? host;
    internal ChordHost Host => host ?? throw (state.Error ?? HostErrors.Released());
    public Contract Contract { get; }
    internal CheckedParams Parameters { get; }
    public Caller Caller { get; }
    public ChordObject? Dependent { get; private set; }
    internal ITarget? Target { get; private set; }
    internal bool Ended { get; private set; }
    public bool Listed { get; private set; } = true;
    internal void Unlist() => Listed = false;
    internal ChordContext AcquisitionContext { get; set; } = ChordContext.Background;
    private RefState state = RefState.Loading;
    private readonly ReferenceState source;
    private readonly Dictionary<string, ReferenceMember> members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReferenceKeyed> keyed = new(StringComparer.Ordinal);
    internal readonly Dictionary<(string Member, string Key), ReferenceKeySource> KeySources = new();
    internal int PendingKeys;
    private readonly TimeSpan timeout;
    // What waits for its next state change (a getter, a parked call), in the order the waits were made: each is told in that order, so
    // calls parked on one reference, and on references sharing one remote object, go out in the order they were made once it is live.
    private readonly List<TaskCompletionSource<bool>> waiters = [];
    internal event Action? Changed;
    // Its events, for a contract with event members only (decision "Events: fire-and-forget members"): the instance its members tap and
    // the host's counter value of its binding to it (taken whenever it attaches to another instance shown live, used or not, so a
    // subscription made later still gets what was emitted since; none while it points at nothing); its members' subscriptions and
    // re-exposing nodes (made by the first use); the members it handed out.
    private readonly bool hasEvents;
    private (IEventInstance Instance, long BoundAt)? eventsAt;
    private ReferenceEventTaps? eventTaps;
    private Dictionary<string, ReferenceEvents>? eventMembers;
    // The state members it has not seen since it joined a shared remote object, each with its replica's count of values then; one is seen
    // once that count passes. Made at the join.
    private Dictionary<string, long>? unseen;
    internal Binding(ChordHost host, Contract contract, CheckedParams parameters, Caller caller, TimeSpan timeout, ChordObject? dependent)
    {
        this.host = host; Contract = contract; Parameters = parameters; Caller = caller; this.timeout = timeout; Dependent = dependent;
        source = new ReferenceState(this, host.Batch);
        hasEvents = ContractsInternal.CurrentVariants(contract).Values.Any(variant => variant.Events.Count > 0);
    }
    public INativeWatchable<RefState> State => source;
    internal RefState RefState => state;
    public string? Variant => Host.Dispatcher.Invoke(() => { AssertUsable(); return Target?.Variant; });
    public bool Following => members.Count > 0;
    internal string Label => Target?.Label ?? Contract.Name;
    private void Wake()
    {
        if (waiters.Count == 0) return;
        var waiting = waiters.ToArray(); waiters.Clear();
        foreach (var waiter in waiting) waiter.TrySetResult(true);
    }
    // Waits for its next state change: false at the timeout; the context's abort cancels the wait. A waiter's continuation is posted to
    // the dispatcher when it is told, so waiters go on in the order they were told (Task.WaitAsync would send each through the pool).
    private Task<bool> NextChange(TimeSpan remaining, ChordContext context)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Host; waiters.Add(waiter);
        var timer = current.Options.TimeProvider.CreateTimer(_ => waiter.TrySetResult(false), null, remaining, Timeout.InfiniteTimeSpan);
        var abort = context.AbortSignal is { CanBeCanceled: true } signal ? signal.Register(() => waiter.TrySetCanceled(signal)) : default;
        _ = waiter.Task.ContinueWith(_ =>
        {
            timer.Dispose(); abort.Dispose();
            current.Dispatcher.Post(() => waiters.Remove(waiter));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return waiter.Task;
    }
    internal void AssertUsable() { if (Ended) throw state.Error ?? HostErrors.Released(); }
    internal void Bind(ITarget target)
    {
        if (Ended || ReferenceEquals(Target, target)) return;
        Host.UnboundClock.Stop(this); Target?.Unhold(this); Target = target; target.Hold(this); Retarget(); Moved();
    }
    /// <summary>It joined a shared remote object that was bound already: <paramref name="members"/> are the members it has not seen, with
    /// their replicas' counts of values now. Until a value arrives for one, it shows no last value of it from before the join (as on a
    /// fresh failed snapshot). A member it holds a value of its own for (a binding that followed another object before) keeps its history.</summary>
    internal void Joined(IReadOnlyDictionary<string, long> members)
    {
        foreach (var (member, count) in members)
        {
            if (this.members.TryGetValue(member, out var source) && source.HasOwnValue) continue;
            (unseen ??= new(StringComparer.Ordinal))[member] = count;
        }
    }
    /// <summary>Whether member <paramref name="member"/>, unseen at its join, took a value since (its replica's count of values passed the
    /// one recorded then, also across rebinds and a private retry's copies): from then on it is seen for good.</summary>
    internal bool Seen(string member)
    {
        if (unseen is null || !unseen.TryGetValue(member, out var count)) return true;
        if (Target?.GenerationOf(member) is not { } generation || generation <= count) return false;
        unseen.Remove(member); return true;
    }
    /// <summary>Moves to <paramref name="target"/> keeping everything of its own (its reference, watches, subscriptions, waiters, last
    /// values, unseen members): a reference retrying alone after <c>too_large/part</c>. Its previous target let it go already; its members
    /// and keys follow <paramref name="target"/>, and it is told.</summary>
    internal void Transfer(ITarget target)
    {
        if (Ended) return;
        Target = target; target.Hold(this);
        if (Following) Host.Batch.Within(Retarget, Host.Context); else Retarget();
        Moved();
    }
    internal void Waiting(ChordError error) => Failed(new(FailureKind.Waiting, error));
    internal void UnbindForReload()
    {
        var target = Target; Target = null; target?.Unhold(this);
        Waiting(new ChordError("unavailable", $"{Contract.Name} is not served here")); Retarget();
        // Unbound: no events until it binds again (then from that binding's counter value, even to the same instance).
        RetargetEvents();
    }
    internal void Failed(FailureCause failure)
    {
        if (Ended) return;
        Move(RefState.Failed(failure));
        if (Target is null && Host.Published)
        {
            Host.UnboundClock.Failed(this, failure.Error);
            Host.UnboundClock.Start(this, Contract.Name, failure.Error);
        }
    }
    public void Retarget()
    {
        foreach (var member in members.Values) member.Retarget();
        foreach (var source in KeySources.Values.ToArray()) source.Retarget();
    }
    public void Moved()
    {
        if (Ended) return;
        var next = Target?.StateFor(Contract.Version, Contract.Fingerprint) ?? RefState.Loading;
        if (next.Kind == "live" && Target is ChordObject provider && !provider.OldServes(Contract.Version, Contract.Fingerprint) && !DataInternal.Binds(provider.Served.Provider, Contract.Version, Contract.Fingerprint))
            next = RefState.Failed(new(FailureKind.Waiting, new ChordError("contract_mismatch", $"{Contract.Name} fingerprint is no longer served")));
        if (next.Kind == "live" && Target is ChordObject local && AdaptedMembers.Refusal(local.FollowingAttempt?.Served ?? local.Served, Contract.Version, local.Variant) is { } refusal)
            next = RefState.Failed(new(FailureKind.Waiting, refusal));
        if (next.Kind == "live" && PendingKeys > 0) next = RefState.Loading;
        Move(next);
        RetargetEvents();
        // The readers of its object's named queries read another live instance's (a restart's successor).
        ToldOfInstance();
        Host.ChangedPresence();
    }
    // Its event members follow the instance it points at from the host's counter value now, when that is another instance than they
    // follow: only while it shows its object live (a failed instance's admitted commits still reach its members, never its events).
    private void RetargetEvents()
    {
        if (!hasEvents || Ended) return;
        var instance = state.Kind == "live" ? Target?.Instance as IEventInstance : null;
        if (ReferenceEquals(instance, eventsAt?.Instance)) return;
        eventsAt = instance is null ? null : (instance, Host.Events.Counter);
        eventTaps?.Retarget(eventsAt);
    }
    public IEventMember Event(string name) => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable();
        if (!ContractsInternal.DataOf(Contract).Kinds.TryGetValue(name, out var kind) || kind != MemberKind.Event) throw HostErrors.InvalidMember(name);
        eventMembers ??= new(StringComparer.Ordinal);
        if (!eventMembers.TryGetValue(name, out var member)) eventMembers.Add(name, member = new ReferenceEvents(this, name));
        return (IEventMember)member;
    });
    /// <summary>Subscribes <paramref name="listener"/> to event member <paramref name="member"/> at once, bound or not: it gets what this
    /// reference receives while it is bound to an instance whose variant has the member. Fails <c>released</c> once released, and with the
    /// cancellation of an aborted <paramref name="context"/>.</summary>
    internal Task<IEventSubscription> SubscribeEvents(string member, Action<ChordEvent> listener, ChordContext context)
    {
        if (host is not { } current) return Task.FromException<IEventSubscription>(state.Error ?? HostErrors.Released());
        return current.Dispatcher.Invoke(() =>
        {
            if (Ended) return Task.FromException<IEventSubscription>(state.Error ?? HostErrors.Released());
            var taps = EventTaps();
            return EventSubscriptions.Subscribe(current, listener, context, subscription => taps.Subscribe(member, subscription),
                subscription => eventTaps?.Unsubscribe(member, subscription));
        });
    }
    /// <summary>Feeds <paramref name="node"/> (an object's member re-exposing event member <paramref name="member"/>) what this reference
    /// receives, until <see cref="UnforwardEvents"/>; nothing for a released reference.</summary>
    internal void ForwardEvents(string member, EventNode node) { if (!Ended) EventTaps().Forward(member, node); }
    internal void UnforwardEvents(string member, EventNode node) => eventTaps?.Unforward(member, node);
    private ReferenceEventTaps EventTaps() => eventTaps ??= new ReferenceEventTaps(Contract.Version, eventsAt);
    private void Move(RefState next)
    {
        if (ReferenceEquals(next, state)) return;
        state = next; source.Moved();
        Wake(); Changed?.Invoke();
    }
    internal async Task ReadyAsync(ChordContext context)
    {
        var deadline = Host.Options.TimeProvider.GetUtcNow() + timeout;
        while (true)
        {
            if (state.Kind == "live" && Target?.Hydrated == true) return;
            if (state.Kind == "ended") throw state.Error!;
            if (state.Cause?.Kind == FailureKind.Broken) throw state.Cause.Error;
            var remaining = deadline - Host.Options.TimeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw state.Cause?.Error ?? new ChordError("unavailable", $"{Label}: did not become live within {timeout.TotalMilliseconds} ms");
            context.AbortSignal?.ThrowIfCancellationRequested();
            if (!await NextChange(remaining, context)) throw state.Cause?.Error ?? new ChordError("unavailable", $"{Label}: did not become live within {timeout.TotalMilliseconds} ms");
        }
    }
    internal Task ReadyAsOldWorkAsync(ChordContext context)
    {
        var waiting = ReadyAsync(context);
        return Target is ChordObject target && Host.OldWork.Of(context)
            ? Host.ReloadCycles.WaitAsync(waiting, target, Contract, context) : waiting;
    }
    public IWatchable<JsonNode?> Member(string name) => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable();
        if (!ContractsInternal.DataOf(Contract).Kinds.TryGetValue(name, out var kind) || kind != MemberKind.State) throw HostErrors.InvalidMember(name);
        if (!members.TryGetValue(name, out var member))
        {
            member = new ReferenceMember(this, name); members.Add(name, member); member.Retarget();
        }
        return (IWatchable<JsonNode?>)member;
    });
    public IKeyedHandle KeyedMember(string name) => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable();
        if (!ContractsInternal.DataOf(Contract).Kinds.TryGetValue(name, out var kind) || kind != MemberKind.Keyed) throw HostErrors.InvalidMember(name);
        if (ContractsInternal.CurrentVariants(Contract)[ContractsInternal.Local].Keyed[name].KeyHost is not null)
            throw HostErrors.InvalidMember(name);
        if (!keyed.TryGetValue(name, out var handle)) keyed.Add(name, handle = new(this, name));
        return (IKeyedHandle)handle;
    });
    public Task<object?> CallAsync(string name, object? arguments, ChordContext context)
    {
        HostTransactions.AssertOutside("call");
        CallContext.Validate(Host, context);
        return Host.Dispatcher.RunAsync(async () =>
        {
            AssertUsable(); Host.Batch.AssertOutside("call");
            await ReadyAsOldWorkAsync(context);
            AssertUsable();
            return await Target!.CallAsync(name, arguments, this, context);
        });
    }
    public void Retry() => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable(); Host.Batch.AssertOutside("retry");
        if (Target is null) Host.Place(this); else Target.Retry(new(new(ReferenceEqualityComparer.Instance)), this);
    });
    public void Release() { if (!Ended) Host.Dispatcher.Invoke(() => End(HostErrors.Released())); }
    public void Dispose() => Release();
    public void End(ChordError error)
    {
        if (Ended) return;
        Host.UnboundClock.Stop(this);
        Ended = true; var target = Target; Target = null; unseen = null;
        state = RefState.Ended(error);
        // Its subscriptions end and its event members untap (taken out first: a stopping subscription finds none).
        var taps = eventTaps; eventTaps = null; eventsAt = null; eventMembers = null; taps?.End();
        foreach (var member in members.Values) member.Release();
        foreach (var key in KeySources.Values.ToArray()) key.Release();
        KeySources.Clear(); keyed.Clear(); queryMembers = null; instanceFollowers = null; followedInstance = null;
        members.Clear(); source.Released(); Wake(); Changed?.Invoke(); Changed = null;
        target?.Unhold(this); Host.Forget(this); host = null; Dependent = null; AcquisitionContext = ChordContext.Background;
    }
}

internal sealed class ReferenceState(Binding binding, Batch batch) : NativeSource<RefState>(batch)
{
    private readonly Followers<StateWatch> watches = new();
    public override RefState Value => binding.RefState;
    public override Task<INativeWatchHandle<RefState>> WatchAsync(ChordContext context)
    {
        if (binding.Ended)
        {
            var ended = new NativeWatch<RefState>(Value, () => { }, _ => { }); ended.End(WatchEnd.Ended);
            return Task.FromResult<INativeWatchHandle<RefState>>(ended);
        }
        return Task.FromResult(binding.Host.Dispatcher.Invoke<INativeWatchHandle<RefState>>(() =>
        {
        StateWatch? entry = null;
        var watch = new NativeWatch<RefState>(Value, () => { if (entry is not null) watches.Remove(entry); }, Batch?.ReportListener ?? (_ => { }));
        if (binding.Ended) watch.End(WatchEnd.Ended);
        else { entry = new(watch, context); watches.Add(entry); if (context.AbortSignal is { } signal) watch.ObserveAbort(signal); }
            return watch;
        }));
    }
    internal void Moved()
    {
        foreach (var entry in watches.Items) entry.Watch.Advance(Value, entry.Context);
        Batch?.Within(Changed, ChordContext.Background);
    }
    internal void Released()
    {
        foreach (var entry in watches.Clear()) entry.Watch.End(WatchEnd.Ended);
        Batch?.Within(() => { Changed(); End(); }, ChordContext.Background);
    }
    private sealed record StateWatch(NativeWatch<RefState> Watch, ChordContext Context);
}

internal sealed class ReferenceMember(Binding binding, string name) : Source(binding.Host.Batch), ISubscriber
{
    private Source? source;
    private JsonNode? value;
    private JsonNode? before;
    private bool hasValue, framed, hasBefore, retired, ended;
    private Exception? failure;
    private bool heldFailure;
    private bool toldFailure;
    public override JsonNode? Value => binding.Host.Dispatcher.Invoke(() =>
    { if (failure is not null && !heldFailure) throw failure; if (!hasValue) throw failure ?? HostErrors.NotReady(name); return value; });
    public override WatchStart Start() => failure is { } error ? WatchStart.Failure(error, hasValue, value) : WatchStart.Of(Value);
    /// <summary>Whether it holds a value of its own: one it recorded while it followed an object.</summary>
    internal bool HasOwnValue => hasValue;
    internal void Retarget()
    {
        if (ended) return;
        source?.Unsubscribe(this); source = null;
        var followed = binding.Target?.Followed(name, binding.Contract.Version);
        if (followed?.Kind == "live")
        {
            var resolved = ChordHost.ResolveSource(followed.Source);
            source = resolved as Source;
            try
            {
                var next = resolved switch { Source s => s.Value, Constant c => c.Value, _ => throw HostErrors.InvalidMember(name) };
                Record(next, null);
            }
            catch (Exception error)
            {
                // A shared remote object's member it finds failed: it starts with the replica's last good value as its own once its
                // reference has seen the member (never one from before its join; decision "References share one wire hold").
                if (!hasValue && source is PiSharp.Chord.Remote.Replica replica && replica.Start() is { Failed: true, HasLast: true } start && binding.Seen(name))
                { value = start.Value; hasValue = true; }
                Record(value, error);
            }
            source?.Subscribe(this);
        }
        else if (followed?.Error is { } error)
        {
            var holding = binding.Target?.State.Kind == "failed";
            if (!holding || !heldFailure) Record(value, error, holding);
        }
        else if (followed?.Kind == "loading" && heldFailure) { failure = null; heldFailure = false; }
    }
    private void Record(JsonNode? next, Exception? error, bool holding = false)
    {
        // A vanished/failed member remains failed while its object is unavailable. Turning that member's
        // last good value into an ordinary held value would hide its original error before recovery.
        if (holding && failure is not null && !heldFailure) return;
        if (!framed) { before = value; hasBefore = hasValue && failure is null && !toldFailure; framed = true; }
        if (error is null) { value = next; hasValue = true; }
        failure = error; heldFailure = holding && hasValue; toldFailure |= error is not null; Changed();
    }
    public void Advance(WatchCall<JsonNode?> call, ChordContext context)
    {
        if (call is WatchValueCall<JsonNode?> next) { retired |= next.Retired; Record(next.Value, null); }
        else if (call is WatchFailedCall<JsonNode?> failed) Record(value, failed.Error as Exception ?? DataInternal.AsChordError(failed.Error, name));
    }
    public void End(WatchEnd end) { source = null; }
    public void EndAfterDelivery() => End(WatchEnd.Ended);
    public override void Deliver(ChordContext context)
    {
        if (!framed) return;
        framed = false;
        if (failure is { } error) { FailWatches(error, context); return; }
        var ops = hasBefore ? DeltaDiff.DiffRevisions(before, value) : ImmutableArray.Create<Op>(new ReplaceOp(value));
        if (ops.Length > 0 || retired) Publish(value, ops, retired, context);
        retired = false; before = null; toldFailure = false;
    }
    internal void Release() { ended = true; source?.Unsubscribe(this); source = null; EndWatches(); }
}
