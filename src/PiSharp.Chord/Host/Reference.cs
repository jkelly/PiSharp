// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/{reference,reference-source,ref-state}.ts
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
    private TaskCompletionSource changed = NewSignal();
    internal event Action? Changed;
    internal Binding(ChordHost host, Contract contract, CheckedParams parameters, Caller caller, TimeSpan timeout, ChordObject? dependent)
    {
        this.host = host; Contract = contract; Parameters = parameters; Caller = caller; this.timeout = timeout; Dependent = dependent;
        source = new ReferenceState(this, host.Batch);
    }
    public INativeWatchable<RefState> State => source;
    internal RefState RefState => state;
    public string? Variant => Host.Dispatcher.Invoke(() => { AssertUsable(); return Target?.Variant; });
    public bool Following => members.Count > 0;
    internal string Label => Target?.Label ?? Contract.Name;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void AssertUsable() { if (Ended) throw state.Error ?? HostErrors.Released(); }
    internal void Bind(ITarget target)
    {
        if (Ended || ReferenceEquals(Target, target)) return;
        Host.UnboundClock.Stop(this); Target?.Unhold(this); Target = target; target.Hold(this); Retarget(); Moved();
    }
    internal void Waiting(ChordError error) => Failed(new(FailureKind.Waiting, error));
    internal void UnbindForReload()
    {
        var target = Target; Target = null; target?.Unhold(this);
        Waiting(new ChordError("unavailable", $"{Contract.Name} is not served here")); Retarget();
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
        Host.ChangedPresence();
    }
    private void Move(RefState next)
    {
        if (ReferenceEquals(next, state)) return;
        state = next; source.Moved();
        var signal = changed; changed = NewSignal(); signal.TrySetResult(); Changed?.Invoke();
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
            try { await changed.Task.WaitAsync(remaining, Host.Options.TimeProvider, context.AbortSignal ?? CancellationToken.None); }
            catch (TimeoutException) { throw state.Cause?.Error ?? new ChordError("unavailable", $"{Label}: did not become live within {timeout.TotalMilliseconds} ms"); }
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
            return await Target!.CallAsync(name, arguments, context, Caller, Contract.Version);
        });
    }
    public void Retry() => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable(); Host.Batch.AssertOutside("retry");
        if (Target is null) Host.Place(this); else Target.Retry(new(new(ReferenceEqualityComparer.Instance)));
    });
    public void Release() { if (!Ended) Host.Dispatcher.Invoke(() => End(HostErrors.Released())); }
    public void Dispose() => Release();
    public void End(ChordError error)
    {
        if (Ended) return;
        Host.UnboundClock.Stop(this);
        Ended = true; var target = Target; Target = null;
        state = RefState.Ended(error);
        foreach (var member in members.Values) member.Release();
        foreach (var key in KeySources.Values.ToArray()) key.Release();
        KeySources.Clear(); keyed.Clear();
        members.Clear(); source.Released(); changed.TrySetResult(); Changed?.Invoke(); Changed = null;
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
            catch (Exception error) { Record(value, error); }
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
