// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/remote-object.ts.
// An object held over a link: what the remote references of this host with one sharing key are bound to, across the links of its path
// (the parent, or a route address); one hold for all of them (decision "References share one wire hold"). Its state, the replicas of its
// last bound variant (kept while its link is down), the nodes its events arrive at (decision "Events: fire-and-forget members"), its
// bindings (each reference's own release, calls and seen members), its hold on the path's current link and calls through it: its hold in
// a chord.hold span until bound (shared work: a root on the host's adapter), each call in a chord.call span, their carriers on the wire.
// The admission paths of 1.6.0 (withheld, admitted, a denial dropping its replicas) are chord-admission's; QueryMember is chord-queries'.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Telemetry;
using StateEntry = PiSharp.Chord.Link.StateEntry;

namespace PiSharp.Chord.Remote;

internal sealed class RemoteObject : ITarget, IUpstream
{
    // The last binding: its variant, the bound version's names (the wire's indexes), a replica per member, and a node per event member,
    // at which the events arriving for this binding go out (a new binding has new nodes, so its reference's subscriptions move there from
    // its counter value: nothing of an earlier binding reaches them).
    private sealed record Bound(string Variant, VariantData Data, VariantNames Names, Dictionary<string, Replica> Replicas) : IEventInstance
    {
        private readonly Dictionary<string, EventNode>? nodes = Names.Events.Count == 0 ? null
            : Names.Events.ToDictionary(member => member, _ => new EventNode(), StringComparer.Ordinal);
        // Its reference binds at its own version: the provider mapped what arrives.
        public EventNode? EventNode(string member, long version) => nodes?.GetValueOrDefault(member);
    }
    private readonly ChordHost host;
    private readonly Contract contract;
    private readonly CheckedParams parameters;
    private readonly Address address;
    // Its path: the parent's or a route address's; the host's unplaced one after a part past the limits.
    private RemotePath path;
    // The object whose activation declared the reference (self.Get): its holds carry its chain while it activates.
    private ChordObject? dependent;
    private Bound? bound;
    private (ConsumerSide Side, long Id)? hold;
    // Its references' bindings, in the order they joined; the hold ends with the last.
    private readonly List<Binding> bindings = [];
    // Takes its entry out of its path's sharing table, once; none for a private object or once taken out.
    private Action? unindex;
    private OpenSpan? holding;
    private IReadOnlyDictionary<string, string>? firstHold;
    private int holds;
    public ITelemetrySpan? Span => holding?.Span;
    internal Caller Caller { get; }
    internal HoldMessage Request { get; }
    internal IReadOnlyList<string>? ActivationChain => dependent?.ActivationChain();
    public string Label { get; }
    public RefState State { get; private set; } = RefState.Loading;
    public bool Hydrated => bound is not null;
    public string? Variant => bound?.Variant;
    public ChordError? LastFailure { get; private set; }
    public string? ActivationId => null;
    public bool BlocksFirstPass => false;
    public object? Instance => bound;
    /// <summary>Whether its path is the host's unplaced one: a part past the limits failed it, so it holds nothing anywhere.</summary>
    internal bool Unplaced => ReferenceEquals(path, host.UnplacedPath);
    /// <summary>How many references it is bound to.</summary>
    internal int Bindings => bindings.Count;

    /// <summary>Made for its first reference (its getter's <paramref name="trigger"/>), on <paramref name="path"/>;
    /// <paramref name="unindex"/> takes it out of its path's sharing table (none: a private object, never shared).</summary>
    internal RemoteObject(ChordHost host, Contract contract, CheckedParams parameters, Address address, Caller caller, RemotePath path,
        ChordObject? dependent, ITelemetryContext trigger, Action? unindex = null)
    {
        this.host = host; this.contract = contract; this.parameters = parameters; this.address = address; this.path = path; Caller = caller;
        this.dependent = dependent; this.unindex = unindex;
        Label = parameters.Key == "" ? contract.Name : $"{contract.Name}({parameters.Key})";
        Request = new(0, contract.Name, contract.Version, contract.Fingerprint, new Part.Text(ChordJson.Stringify(parameters.Value)), new(address.Kind, address.Params));
        // Its hold is shared work: a root on the host's adapter whatever its first getter's context, linked to that getter's span when
        // traced; its first hold carries the span's carrier (HoldTrace).
        if (!ChordTelemetry.IsUntraced(host.Telemetry))
        {
            holding = new OpenSpan(host.Telemetry, "chord.hold", HoldAttributes());
            if (!ChordTelemetry.IsUntraced(trigger)) holding.Span.AddLink(new(trigger));
            firstHold = ChordTelemetry.CarrierOf(holding.Span);
        }
    }

    /// <summary>
    /// Counts <paramref name="holder"/> as one of its references. One joining it after it was bound has not seen what it finds: every
    /// member when it is not live, else the members failed or missing from the bound variant; each counts as seen once its replica takes a
    /// value after the join. <c>Binding.Bind</c> retargets and tells it.
    /// </summary>
    public void Hold(IHolder holder)
    {
        var binding = (Binding)holder;
        if (bound is not null && bindings.Count > 0) binding.Joined(Unseen(bound));
        if (!bindings.Contains(binding)) bindings.Add(binding);
    }
    // The members a binding joining now has not seen, each with its replica's count of values now (0 without one): its own history
    // starts there.
    private Dictionary<string, long> Unseen(Bound current)
    {
        var live = State.Kind == "live"; var unseen = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var variant in ContractsInternal.CurrentVariants(contract).Values)
            foreach (var member in variant.State.Keys)
            {
                var replica = current.Replicas.GetValueOrDefault(member);
                var good = live && current.Names.State.Contains(member) && replica?.Start().Failed == false;
                if (!good) unseen[member] = replica?.Generation ?? 0;
            }
        return unseen;
    }
    public long? GenerationOf(string member) => bound?.Replicas.GetValueOrDefault(member)?.Generation;
    /// <summary><paramref name="holder"/> was released. While others remain nothing is sent but the cancellation of its own calls (queued
    /// ones leave unsent, sent ones are aborted); with the last the hold is released and the object ends.</summary>
    public void Unhold(IHolder holder)
    {
        if (holder is not Binding binding || !bindings.Remove(binding)) return;
        if (bindings.Count > 0) { hold?.Side.CancelCalls(hold.Value.Id, binding, Label); return; }
        var error = new ChordError("released", $"{Label}: released");
        // Out of the table before anything else runs: a reference made from here on holds anew.
        Unpublish();
        hold?.Side.Release(hold.Value.Id, error);
        End(error);
    }
    // Leaves its path's sharing table (once): it is never put back.
    private void Unpublish() { var take = unindex; unindex = null; take?.Invoke(); }
    /// <summary>Holds on <paramref name="link"/>, the path's link now (a first hold, or a re-hold on a new link), with the activation chain
    /// of its dependent while that activates.</summary>
    public void Attach(HostLink link) { if (State.Kind != "ended") hold = (link.Consumer, link.Consumer.Attach(this, HoldTrace())); }
    public void Dropped(ChordError error) { hold = null; Loading(); }
    public void Unreachable(ChordError error) { Held("failed", error); LastFailure = error; Move(RefState.Failed(new(FailureKind.Waiting, error))); }
    public Task<object?> CallAsync(string method, object? args, IHolder holder, ChordContext context)
    {
        if (State.Kind != "live" || bound is null || hold is null) return Task.FromException<object?>(HostErrors.NotReady(Label));
        if (!bound.Data.Methods.TryGetValue(method, out var data)) return Task.FromException<object?>(HostErrors.InvalidMember(method));
        var current = hold.Value;
        var index = bound.Names.Methods.ToList().IndexOf(method);
        var parent = context.Telemetry;
        if (ChordTelemetry.IsUntraced(parent)) return current.Side.CallAsync(current.Id, index, data, args, holder, context);
        var attributes = HoldAttributes(); attributes["member"] = ChordTelemetry.Capped(method);
        foreach (var pair in host.PrincipalAttributes(Caller.Principal, context)) attributes[pair.Key] = pair.Value;
        return parent.StartSpanAsync(new SpanOptions("chord.call") { Attributes = attributes }, async span =>
        {
            try
            {
                var result = await current.Side.CallAsync(current.Id, index, data, args, holder, ChordContext.WithTelemetry(span, context));
                span.SetAttributes(new() { ["outcome"] = "ok" }); return result;
            }
            catch (Exception error)
            { span.SetAttributes(new() { ["outcome"] = ChordTelemetry.Ended(span, "call_failed", error, context.AbortSignal) }); throw; }
        });
    }
    public Followed Followed(string member, long version)
    {
        Replica? replica = null;
        bound?.Replicas.TryGetValue(member, out replica);
        if (State.Kind == "live") return replica is null || !bound!.Data.State.ContainsKey(member)
            ? new("lacking", Error: HostErrors.InvalidMember(member)) : new("live", replica, bound);
        return State.Kind == "failed" ? new("failed", Instance: bound, Error: State.Cause!.Error, Last: replica)
            : new("loading", Instance: bound, Last: replica);
    }
    public IKeyedHandle KeyedMember(string member, long version) => throw HostErrors.InvalidMember(member);
    /// <summary><c>queries</c> members are only in local contracts, as keyed ones (Pi 1.6.0, slice 14 §3.1).</summary>
    public OwnQuery QueryMember(string member, string name) =>
        throw DataInternal.Invalid("member", $"{Label} has no queries member \"{member}\": named queries are local");
    public bool Reaches(ITarget target, HashSet<ITarget> visited) => ReferenceEquals(this, target);
    /// <summary>
    /// <c>Retry()</c> from <paramref name="holder"/>, as a step of <paramref name="walk"/>: a failed object's provider is asked for an
    /// attempt with the walk's hop budget, none left: nothing is sent (a hold it refused is started there again, for every reference
    /// sharing it). After a part past the link's limits the holder alone moves to a private object of its own, placed anew and held with a
    /// new id (<see cref="RetryAlone"/>).
    /// </summary>
    public void Retry(RetryWalk walk, IHolder? holder = null)
    {
        if (State.Kind != "failed" || !walk.Visited.Add(this)) return;
        if (Unplaced) { if (holder is Binding binding && bindings.Contains(binding)) RetryAlone(binding); return; }
        if (walk.Hops > 0 && hold is { } current) current.Side.Retry(current.Id, walk.Hops);
    }
    // Moves binding to a private object (never shared) that keeps what this one had for it (its variant, copies of its replicas with
    // their counts: its last values and seen members stay its own) and places it anew; the others stay here, failed, until they retry.
    // The binding leaves first, so this object ending with its last binding never ends it. A placement that throws fails only the private
    // object broken (HostAddress, a route that refuses).
    private void RetryAlone(Binding binding)
    {
        var own = new RemoteObject(host, contract, parameters, address, Caller, host.UnplacedPath, dependent, NoopTelemetry.Context);
        if (bound is { } current)
            own.bound = new(current.Variant, current.Data, current.Names,
                current.Replicas.ToDictionary(pair => pair.Key, pair => pair.Value.Fork(host.Batch), StringComparer.Ordinal));
        host.UnplacedPath.Attach(own);
        bindings.Remove(binding);
        binding.Transfer(own);
        if (bindings.Count == 0) End(new ChordError("released", $"{Label}: released"));
        own.Place();
    }
    // Places a private object anew, on its path now (a throwing HostAddress or route fails it broken; no path at all leaves it waiting).
    private void Place()
    {
        RemotePath? next;
        try { next = host.Repath(contract, parameters.Value, Caller); }
        catch (Exception error) { Fail(DataInternal.AsChordError(error, contract.Name)); return; }
        if (next is null) { Unreachable(new ChordError("unavailable", $"{contract.Name}: no provider")); return; }
        path.Detach(this); path = next; next.Attach(this);
    }
    internal Action PrepareBound(BoundEntry entry, HostLink link)
    {
        var order = ContractsInternal.VariantOrder(contract);
        if (entry.Variant < 0 || entry.Variant >= order.Count) throw new ArgumentException($"hold {entry.Hold}: no variant {entry.Variant}");
        var variant = order[(int)entry.Variant]; var data = ContractsInternal.CurrentVariants(contract)[variant];
        var names = ContractsInternal.VariantNamesOf(data); var snapshots = new Dictionary<string, MemberSnapshot>(StringComparer.Ordinal);
        string Name(long index)
        {
            if (index < 0 || index >= names.State.Count) throw new ArgumentException("bound member index");
            var name = names.State[(int)index]; if (snapshots.ContainsKey(name)) throw new ArgumentException("bound member repeated"); return name;
        }
        foreach (var member in entry.Members) snapshots.Add(Name(member.Member), new(Parts.Parse(link.OpenPart(member.Value, link.Limits))));
        foreach (var failed in entry.Failed) snapshots.Add(Name(failed.Member), new(null, failed.Error.ToException()));
        if (snapshots.Count != names.State.Count) throw new ArgumentException("the snapshot misses a member");
        return () =>
        {
            var previous = bound; var replicas = new Dictionary<string, Replica>(StringComparer.Ordinal);
            bound = new(variant, data, names, replicas);
            foreach (var pair in snapshots)
            {
                if (previous?.Replicas.TryGetValue(pair.Key, out var replica) == true) replica.Reset(pair.Value);
                else replica = new Replica(host.Batch, pair.Value);
                replicas.Add(pair.Key, replica);
            }
            if (previous is not null) foreach (var pair in previous.Replicas)
                if (!snapshots.ContainsKey(pair.Key)) { pair.Value.Fail(HostErrors.InvalidMember(pair.Key)); replicas.Add(pair.Key, pair.Value); }
        };
    }
    internal Action Prepare(StateEntry entry, HostLink link)
    {
        if (State.Kind != "live" || bound is null) throw new ArgumentException("state before bound");
        Replica Get(long index) => index < 0 || index >= bound.Names.State.Count ? throw new ArgumentException("state member index") : bound.Replicas[bound.Names.State[(int)index]];
        var changed = new HashSet<Replica>(); var retired = new HashSet<Replica>();
        foreach (var index in entry.Retired) if (!retired.Add(Get(index))) throw new ArgumentException("member retired twice");
        var updates = entry.Members.Select(member =>
        {
            var replica = Get(member.Member); if (!changed.Add(replica)) throw new ArgumentException("member repeated in state");
            var staged = replica.Prepare(Parts.Parse(link.OpenPart(member.Ops, link.Limits))); return (replica, staged);
        }).ToArray();
        var failures = entry.Failed.Select(failed =>
        {
            var replica = Get(failed.Member); if (!changed.Add(replica)) throw new ArgumentException("member repeated in state");
            return (replica, error: failed.Error.ToException());
        }).ToArray();
        return () =>
        {
            foreach (var (replica, staged) in updates) replica.Adopt(staged.Value, staged.Ops, staged.Decoder, retired.Contains(replica));
            foreach (var (replica, error) in failures) replica.Fail(error);
            foreach (var replica in retired) if (!updates.Any(x => ReferenceEquals(x.replica, replica))) replica.Retire();
        };
    }
    /// <summary>Where an event of member <paramref name="member"/> (an index into the bound version's event members) of its hold goes:
    /// its live binding's node for it, when something taps it; null when the value is to be dropped unopened (not live, no listener).
    /// Throws on an index outside the bound version (a protocol error).</summary>
    internal EventNode? EventNode(long member)
    {
        if (State.Kind != "live" || bound is null) return null;
        if (member < 0 || member >= bound.Names.Events.Count) throw new ArgumentException($"{Label}: no event member {member}");
        var node = bound.EventNode(bound.Names.Events[(int)member], contract.Version)!;
        return node.Tapped ? node : null;
    }
    /// <summary>One entry of an event message for its hold: the value of event member <paramref name="member"/> stamped
    /// <paramref name="at"/>, taking this host's next counter value, at the node its reference's subscriptions tap; dropped past the link's
    /// limits (<c>too_large</c>, counted; the hold stays bound).</summary>
    internal void Event(long member, long at, Part value, HostLink link)
    {
        if (EventNode(member) is not { } node) return;
        JsonNode? json;
        try { json = Parts.Parse(link.OpenPart(value, link.Limits)); }
        catch (ChordError error) when (error.Code == "too_large")
        {
            host.Events.Metrics.Counter("chord.events.dropped").Add(1, ChordTelemetry.EventsDropped("too_large")); return;
        }
        // Fresh JSON from the link: event values are never validated (as state).
        host.Events.Push(node, json, at);
    }
    internal void WentLive() { Held("bound"); LastFailure = null; Move(RefState.Live); }
    internal void Failed(FailedMessage message)
    {
        var error = DataInternal.AsChordError(message.Cause.Error.ToException(), Label); LastFailure = error;
        Held("failed", error);
        var kind = Enum.Parse<FailureKind>(message.Cause.Kind, true);
        Move(RefState.Failed(new(kind, error), message.RetryIn is { } retry ? host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() + retry : null));
    }
    internal void Loading() { if (State.Kind is not ("ended" or "loading")) Move(RefState.Loading); }
    /// <summary>A part of its snapshot or of a state change was past the link's limits (<c>too_large/part</c>): the hold is released at the
    /// provider, it leaves the sharing table, and every binding fails broken with <paramref name="error"/> (they share the part), final: no
    /// link holds it again (a reconnect included); each binding's retry moves it alone (<see cref="RetryAlone"/>).</summary>
    internal void TooLarge(ChordError error)
    {
        if (hold is not { } current) return;
        hold = null; Unpublish();
        current.Side.Release(current.Id, error);
        path.Detach(this); path = host.UnplacedPath; path.Attach(this);
        Fail(error);
    }
    // Fails broken with error: no automatic retry.
    private void Fail(ChordError error)
    {
        LastFailure = error; Held("failed", error);
        Move(RefState.Failed(new(FailureKind.Broken, error)));
    }
    /// <summary>Its principal was revoked on this host: released at its provider if it is held there, then ended with
    /// <paramref name="error"/> as <see cref="Lost"/> does; no later link holds it.</summary>
    internal void Revoke(ChordError error)
    {
        var current = hold; hold = null; Unpublish();
        current?.Side.Release(current.Value.Id, error);
        Lost(error);
    }
    /// <summary><c>gone</c>, or the host closes: every reference ends, their calls reject with <paramref name="error"/>. The bindings are
    /// taken and the object ends first, so each ends exactly once whatever ending one does.</summary>
    internal void Lost(ChordError error)
    {
        var all = bindings.ToArray(); Unpublish();
        if (hold is { } current) current.Side.Forget(current.Id, error);
        End(error);
        foreach (var binding in all) binding.End(error);
    }
    // Ends the object: out of the table, detached from its path, replicas ended; keeps none of its references.
    private void End(ChordError error)
    {
        if (State.Kind == "ended") return;
        Unpublish(); Held("ended");
        var last = bound;
        State = RefState.Ended(error); bindings.Clear(); dependent = null; hold = null; bound = null; path.Detach(this);
        if (last is not null) foreach (var replica in last.Replicas.Values) replica.End(error);
    }
    private SpanAttributes HoldAttributes() => new()
    {
        ["chord.host"] = ChordTelemetry.HostAttribute(host.Address), ["type"] = ChordTelemetry.Capped(contract.Name),
        ["object"] = ChordTelemetry.Capped(Label), ["address.kind"] = ChordTelemetry.Capped(Request.Address.Kind),
    };
    // The carrier of this hold: the first one's span's (a root of this host's adapter); a re-hold on a later link is a fresh chord.hold
    // root linked to the first, so nothing keeps adding children under a span long ended. None when the host's adapter records nothing.
    private IReadOnlyDictionary<string, string>? HoldTrace()
    {
        if (holds++ == 0) return firstHold;
        if (firstHold is null || ChordTelemetry.IsUntraced(host.Telemetry)) return null;
        Held("ended"); holding = new OpenSpan(host.Telemetry, "chord.hold", HoldAttributes());
        holding.Span.AddLink(new(ChordTelemetry.ParentOf(firstHold, host.Telemetry)));
        return ChordTelemetry.CarrierOf(holding.Span);
    }
    private void Held(string outcome, Exception? error = null)
    {
        if (holding is not { } span) return; holding = null;
        if (error is not null) ChordTelemetry.SpanFailed(span.Span, "hold_failed", error);
        span.Span.SetAttributes(new() { ["outcome"] = outcome }); span.End();
    }
    // Moves to state and tells every binding, as a local object tells its holders: their members follow what it shows now (all in one
    // batch), then each its state; one released meanwhile is not told. A broken failure takes it out of the sharing table first, for
    // good: a new reference holds anew.
    private void Move(RefState state)
    {
        if (State.Kind == "ended") return;
        if (state is { Kind: "failed", Cause.Kind: FailureKind.Broken }) Unpublish();
        State = state;
        if (bindings.Count == 0) return;
        var all = bindings.ToArray();
        if (all.Any(binding => binding.Following)) host.Batch.Within(() => { foreach (var binding in all) binding.Retarget(); }, host.Context);
        else foreach (var binding in all) binding.Retarget();
        foreach (var binding in all) if (bindings.Contains(binding)) binding.Moved();
    }
}
