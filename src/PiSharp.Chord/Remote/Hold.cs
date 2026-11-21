// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/hold.ts.
// 1.6.0: a bound hold taps its instance's event members from the counter value of its bound (decision "Events: fire-and-forget
// members"), until it unbinds or its object no longer shows it live. Its calls are decoded by what its consumer had applied when it sent
// them: one binding fence per unacknowledged bound, replacing the single previous binding of 1.5.2. A denial (slice 14 §2.2: an
// admission refused its caller) rejects its calls in flight, and the ones that crossed its failed on the wire, forbidden/revoked.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.State;
using WireFailure = PiSharp.Chord.Link.FailureCause;
using StateEntry = PiSharp.Chord.Link.StateEntry;

namespace PiSharp.Chord.Remote;

internal sealed class IncomingHold : IIncomingHold, IHolder, IReloadAwareHolder
{
    private sealed class ProtocolPartException(Exception inner) : Exception("a malformed owned params part", inner);
    private sealed class FollowedMember(IncomingHold hold, long index, IWatchable<JsonNode?> source) : ISubscriber
    {
        internal readonly long Index = index;
        internal readonly IWatchable<JsonNode?> Source = source;
        internal readonly WireDeltaEncoder Encoder = new();
        internal readonly PendingOps Pending = new();
        internal Exception? Failure;
        internal bool Retired;
        public void Advance(WatchCall<JsonNode?> call, ChordContext context)
        {
            if (call is WatchValueCall<JsonNode?> value)
            { Failure = null; Pending.Push(value.Ops); Retired |= value.Retired; }
            else if (call is WatchFailedCall<JsonNode?> failed)
            { Pending.Take(); Failure = failed.Error as Exception ?? new Exception(failed.Error?.ToString()); }
            hold.provider.Touched(hold);
        }
        public void End(WatchEnd end) { }
        public void EndAfterDelivery() { }
    }
    private readonly ProviderSide provider;
    private readonly HoldMessage request;
    private readonly List<FollowedMember> followed = [];
    private ITarget? target;
    private ServedType? served;
    private VariantNames? names;
    private VariantData? variant;
    // What a call is decoded by: the names of a binding (while the hold rebinds after a loading its object's gate takes the call by name),
    // or the failure its consumer was told (the call is refused with it: sent before the consumer learnt of the failure, its indexes are of
    // a binding that is gone). Only contract data: kept after an unbind, it keeps no instance and no source.
    private sealed record Decoding(VariantNames? Names, VariantData? Variant, ChordError? Error);
    // What decodes a call of a consumer that applied everything sent so far: the names of the last bound, or the failure it was told
    // since; null before either.
    private Decoding? latest;
    // One fence per bound its consumer has not acknowledged, oldest first: until it acknowledged the bound numbered Until (this host's own
    // message number), a call it sends was made before it applied that bound, so it is decoded by Before. A, B, C sent before the first
    // ack are two fences, and a call sent between B's and C's application is decoded by B's names.
    private readonly List<(long Until, Decoding Before)> fences = [];
    // Its binding's taps on its instance's event members: their events go into the event flush's message; none for a variant without
    // event members, and once its object no longer shows it live (state may still follow its instance then).
    private List<(EventNode Node, Tap Tap)>? taps;
    private ChordError? refusal;
    private object? instance;
    private bool released, counted = true, binding, toldFailed;
    internal IncomingHold(ProviderSide provider, HoldMessage request, Caller caller, Limits limits)
    {
        this.provider = provider; this.request = request; Caller = caller; Limits = limits;
        provider.Link.Host.CountReference(1);
        ChordTelemetry.Metrics(provider.Link.Host.Telemetry.Metrics).UpDownCounter("chord.holds.active").Add(1);
        provider.Link.Host.PublishedChanged += Published;
    }
    public long Id => request.Hold;
    public Caller Caller { get; }
    public Contract Contract => served?.Contract ?? throw new InvalidOperationException("hold has no contract until placed");
    public bool Following => false;
    public ChordObject? Dependent => null;
    internal Limits Limits { get; }
    private bool FollowsBoundInstance => instance is not null && target is ChordObject local &&
        local.State.Kind != "ended" && ReferenceEquals(local.FollowingAttempt, instance);
    internal bool Bound => !released && !binding && names is not null && FollowsBoundInstance;
    // What a call arriving now is decoded by: what its consumer had applied when it sent it, which is what the oldest bound it has not
    // acknowledged replaced (another variant's indexes name other methods), else the latest. Acks arrive in order: the fences they end
    // are the first ones, so a hold keeps one fence per bound not acknowledged yet, never one per rebinding of its life.
    private Decoding? Decode()
    {
        while (fences.Count > 0 && provider.Acknowledged(fences[0].Until)) fences.RemoveAt(0);
        return fences.Count > 0 ? fences[0].Before : latest;
    }
    /// <summary>Its consumer acknowledged one of this host's own messages: the fences that ack ends go. Whether none is left.</summary>
    internal bool Acknowledged() { Decode(); return fences.Count == 0; }
    internal int Fences => fences.Count;
    internal bool Callable => !released && target is not null && Decode() is { Names: not null };
    internal ChordError Refusal => Decode()?.Error ?? refusal ?? HostErrors.NotReady("hold " + Id);
    internal bool Unbound => !released && target is null;
    private void Published()
    {
        if (released) return;
        if (target is ChordObject local)
        {
            if (!DataInternal.Binds(local.Served.Provider, request.Version, request.Fingerprint) && !local.OldServes(request.Version, request.Fingerprint))
                Removed(new ChordError("contract_mismatch", $"{request.Type} v{request.Version}: not served"));
            else served = local.Served;
        }
        if (target is null) { provider.Send(new LoadingMessage(Id)); Start(); }
    }
    internal void Start()
    {
        var link = provider.Link; var host = link.Host;
        if (released || host.Closed || !host.Published) return;
        try
        {
            if (Parts.Oversized(request.Params, Limits.Part) is { } tooLarge) throw tooLarge;
            var here = host.Address is { } address && address.Kind == request.Address.Kind && JsonNode.DeepEquals(address.Params, request.Address.Params);
            if (!here)
            {
                var path = host.PathOf(request.Address, Caller, link);
                if (path is not null) { provider.Replace(this, new RelayedHold(provider, request, Caller, path)); return; }
            }
            if (!here) { Refuse(FailureKind.Waiting, new ChordError("unavailable", $"{request.Type}: not served here")); return; }
            JsonNode? raw;
            try { raw = Parts.Parse(link.OpenPart(request.Params, Limits)); }
            catch (ChordError) { throw; }
            catch (Exception error) { throw new ProtocolPartException(error); }
            if (!host.Types.TryGetValue(request.Type, out var found) || found.Contract.Kind != ContractKind.Remote)
            { Refuse(FailureKind.Waiting, new ChordError("unavailable", $"{request.Type}: not served here")); return; }
            if (!DataInternal.Binds(found.Provider, request.Version, request.Fingerprint))
            { Refuse(FailureKind.Waiting, new ChordError("contract_mismatch", $"{request.Type} v{request.Version}: not served")); return; }
            var parameters = DataInternal.CheckParams(found.Contract, raw, link.Context);
            var actualAddress = ContractsInternal.DataOf(found.Contract).HostAddress!(parameters.Params);
            if (!actualAddress.Equals(host.Address))
            { Refuse(FailureKind.Waiting, new ChordError("unavailable", $"{request.Type}: its provider places it elsewhere")); return; }
            served = found;
            var trigger = ChordHost.WithTelemetryParent(ChordTelemetry.ParentOf(request.Trace, host.Telemetry), link.Context);
            var made = host.LocalObject(found, new CheckedParams(parameters.Params, parameters.Key), Caller, trigger);
            if (request.Chain is { } chain && made.ActivationId is { } activation && chain.Contains(activation))
                throw DataInternal.Invalid("cycle", $"{request.Type}: activation cycle");
            made.InheritActivationChain(request.Chain);
            target = made; target.Hold(this); Moved();
        }
        catch (ProtocolPartException) { throw; }
        catch (Exception error)
        {
            if (!link.Open) return;
            Refuse(FailureKind.Broken, DataInternal.AsChordError(error, request.Type));
        }
    }
    private void Refuse(FailureKind kind, ChordError error)
    {
        toldFailed = true; refusal = error; latest = new(null, null, error);
        provider.Send(new FailedMessage(Id, new WireFailure(kind.ToString().ToLowerInvariant(), WireError.FromException(error))));
    }
    public void Retarget()
    {
        // StopFollowing can detach an old attempt without changing the object's loading/failed state.
        if (!released && names is not null && !FollowsBoundInstance) Moved();
    }
    public void Moved()
    {
        if (released || target is null) return;
        // Its version is no longer bound and no open old attempt serves it any more (a version-dropping reload's drain ended): it leaves
        // its object and waits unbound, as at the publication that removes a type (upstream hold.ts `removed`).
        if (target is ChordObject held && !DataInternal.Binds(held.Served.Provider, request.Version, request.Fingerprint) && !held.OldServes(request.Version, request.Fingerprint))
        { Removed(new ChordError("contract_mismatch", $"{request.Type} v{request.Version}: not served")); return; }
        var state = target.StateFor(request.Version, request.Fingerprint);
        if (state.Kind == "ended") { End(state.Error ?? HostErrors.Released()); return; }
        if (state.Kind == "live")
        {
            if (names is null || !ReferenceEquals(instance, target.Instance))
            {
                Unfollow(); provider.Unbind(this); binding = true; provider.Bind(this);
            }
            return;
        }
        // Admitted commits keep their exact old source until H2 detaches it. This also covers a
        // replacement parked on an async attach, with no opportunity to publish a new binding yet.
        // Its events stop now, as a reference's do: a binding not shown live gets no event.
        if (names is not null && FollowsBoundInstance) { Untap(); return; }
        if (names is not null || binding)
        {
            Unfollow(); instance = null; binding = false;
            if (state.Cause is { } failure)
            {
                names = null; variant = null;
                // Its calls in flight are rejected with the failure's error, forbidden/revoked for a denial (access withdrawn under a
                // running call).
                provider.Unbound(this, AdmissionInternal.Cut(target.Label, failure));
            }
            else provider.Unbind(this);
            if (state.Kind == "loading") provider.Send(new LoadingMessage(Id));
        }
        if (state.Cause is { } cause)
        {
            var now = provider.Link.Host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            provider.Send(new FailedMessage(Id, new(cause.Kind.ToString().ToLowerInvariant(), WireError.FromException(cause.Error)),
                state.RetryAt is { } retry ? (long)Math.Max(0, retry - now) : null));
            // Told a failure, its consumer rejects what it still waits on: calls by the old names are not taken any more. One that still
            // arrives was sent before its consumer learnt of the failure; for a denial that is access withdrawn under the call
            // (forbidden/revoked), whether it had reached this host or not.
            var crossed = AdmissionInternal.Cut(target.Label, cause);
            names = null; variant = null; refusal = crossed; toldFailed = true; latest = new(null, null, crossed);
        }
        else if (toldFailed) { toldFailed = false; refusal = null; provider.Send(new LoadingMessage(Id)); }
    }
    internal BoundEntry BoundEntry()
    {
        if (target is null || served is null || target.StateFor(request.Version, request.Fingerprint).Kind != "live") throw new InvalidOperationException("bind a live hold");
        var previous = latest;
        Unfollow(); binding = false; instance = target.Instance;
        var boundContract = instance is Attempt attempt ? attempt.Served.Contract : served.Contract;
        var variants = ContractsInternal.VariantsAt(boundContract, request.Version);
        var name = target.Variant ?? ContractsInternal.Local;
        if (!variants.TryGetValue(name, out variant)) throw new ArgumentException("variant is not in consumer version");
        names = ContractsInternal.VariantNamesOf(variant);
        // What its consumer's calls were decoded by so far is fenced until it acknowledged this bound, the next of this host's own messages.
        if (previous is not null) { fences.Add((provider.NextOwn, previous)); provider.Fenced(this); }
        latest = new(names, variant, null);
        toldFailed = false; refusal = null;
        var members = new List<BoundMember>(); var failed = new List<FailedMember>();
        for (var i = 0; i < names.State.Count; i++)
        {
            var member = names.State[i];
            var state = target.Followed(member, request.Version);
            var source = ChordHost.ResolveSource(state.Source) as IWatchable<JsonNode?>;
            if (source is null) { failed.Add(new(i, WireError.FromException(state.Error ?? HostErrors.InvalidMember(member)))); continue; }
            var followedMember = new FollowedMember(this, i, source); followed.Add(followedMember);
            try { members.Add(new(i, provider.Link.Part(ChordJson.Stringify(source.Value)))); }
            catch (Exception error) { followedMember.Failure = error; failed.Add(new(i, WireError.FromException(error))); }
            if (source is Source changing) changing.Subscribe(followedMember);
        }
        // A variant without event members taps nothing (no cost per binding for contracts without events); the others from the counter
        // value now: it gets only what is emitted after its bound.
        if (names.Events.Count > 0 && instance is IEventInstance emitting) TapEvents(emitting);
        provider.Link.Context.Count("chord.link.encodes");
        return new(Id, variants.Keys.Order(StringComparer.Ordinal).ToList().IndexOf(name), members, failed);
    }
    internal void Queue() { } // PendingOps already receives each source frame, before the shared batch flush.
    internal StateEntry StateEntry() => PendingEntry();
    internal StateEntry PendingEntry()
    {
        var members = new List<StateMember>(); var failed = new List<FailedMember>(); var retired = new List<long>();
        foreach (var member in followed)
        {
            if (member.Failure is { } failure)
            {
                failed.Add(new(member.Index, WireError.FromException(failure))); member.Failure = null; member.Pending.Take(); continue;
            }
            var pending = member.Pending.Take();
            ImmutableArray<Op> ops = pending.Kind == PendingTakeKind.Replace ? [new ReplaceOp(member.Source.Value)] : pending.Ops;
            if (!ops.IsEmpty) members.Add(new(member.Index, provider.Link.Part(ChordJson.Stringify(member.Encoder.Encode(ops)))));
            if (member.Retired) { retired.Add(member.Index); member.Retired = false; }
        }
        provider.Link.Context.Count("chord.link.encodes"); return new(Id, members, failed, retired);
    }
    internal (string Name, bool Bytes) Method(long index)
    {
        if (Decode() is not { Names: { } bound, Variant: { } data } || index < 0 || index >= bound.Methods.Count) throw new ArgumentException($"hold {Id}: no method {index}");
        var name = bound.Methods[(int)index]; return (name, data.Methods[name].Result.Bytes);
    }
    internal Task<object?> CallAsync(string method, object? args, ChordContext context) => ((ChordObject)target!).CallAs(method, args, context, Caller, request.Version);
    public void Retry(long hops)
    {
        if (released) return;
        if (target is not null) target.Retry(new(new(ReferenceEqualityComparer.Instance), (int)hops - 1));
        else { provider.Send(new LoadingMessage(Id)); Start(); }
    }
    public void Removed(ChordError error)
    {
        if (released) return;
        var previous = target; target = null; previous?.Unhold(this);
        Unfollow(); names = null; variant = null; instance = null; served = null; binding = false;
        provider.Unbound(this, error); provider.Send(new LoadingMessage(Id)); Refuse(FailureKind.Waiting, error);
    }
    public void End(ChordError error)
    {
        if (released) return; provider.Send(new GoneMessage(Id, WireError.FromException(error))); provider.Forget(this, error); Drop();
    }
    public void Release() => Drop();
    public void Drop()
    {
        if (released) return; released = true;
        provider.Link.Host.PublishedChanged -= Published; Unfollow(); provider.Unbind(this);
        var held = target; target = null; held?.Unhold(this);
        served = null; names = null; variant = null; latest = null; fences.Clear(); instance = null;
        if (counted)
        {
            counted = false; provider.Link.Host.CountReference(-1);
            ChordTelemetry.Metrics(provider.Link.Host.Telemetry.Metrics).UpDownCounter("chord.holds.active").Add(-1);
        }
    }
    private void Unfollow()
    {
        foreach (var member in followed) if (member.Source is Source changing) changing.Unsubscribe(member);
        followed.Clear(); Untap();
    }
    // Stops its event members (its state may still follow its instance).
    private void Untap()
    {
        if (taps is null) return;
        foreach (var (node, tap) in taps) node.Untap(tap);
        taps = null;
    }
    // Taps the event members of its variant (it has some) on its instance at its version, from the counter now.
    private void TapEvents(IEventInstance emitting)
    {
        var boundAt = provider.Link.Host.Events.Counter; taps = [];
        for (var index = 0; index < names!.Events.Count; index++)
        {
            if (emitting.EventNode(names.Events[index], request.Version) is not { } node) continue;
            var member = index;
            var tap = new Tap(boundAt, (entry, flush) => provider.QueueEvent(Id, member, entry, flush));
            node.Tap(tap); taps.Add((node, tap));
        }
    }
}
