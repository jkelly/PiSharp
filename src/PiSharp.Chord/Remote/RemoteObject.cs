// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/remote-object.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using StateEntry = PiSharp.Chord.Link.StateEntry;

namespace PiSharp.Chord.Remote;

internal sealed class RemoteObject : ITarget, IUpstream
{
    private sealed record Bound(string Variant, VariantData Data, VariantNames Names, Dictionary<string, Replica> Replicas);
    private readonly ChordHost host;
    private readonly Contract contract;
    private readonly JsonObject parameters;
    private RemotePath path;
    private Bound? bound;
    private (ConsumerSide Side, long Id)? hold;
    private IHolder? binding;
    private bool attached;
    internal Caller Caller { get; }
    internal HoldMessage Request { get; }
    internal IReadOnlyList<string>? ActivationChain => binding?.Dependent?.ActivationChain();
    public string Label { get; }
    public RefState State { get; private set; } = RefState.Loading;
    public bool Hydrated => bound is not null;
    public string? Variant => bound?.Variant;
    public ChordError? LastFailure { get; private set; }
    public string? ActivationId => null;
    public bool BlocksFirstPass => false;
    public object? Instance => bound;
    internal RemoteObject(ChordHost host, Contract contract, CheckedParams parameters, Address address, Caller caller, RemotePath path)
    {
        this.host = host; this.contract = contract; this.parameters = parameters.Value; this.path = path; Caller = caller;
        Label = parameters.Key == "" ? contract.Name : $"{contract.Name}({parameters.Key})";
        Request = new(0, contract.Name, contract.Version, contract.Fingerprint, new Part.Text(ChordJson.Stringify(parameters.Value)), new(address.Kind, address.Params));
    }
    public void Hold(IHolder holder) { binding = holder; if (!attached) { attached = true; path.Attach(this); } }
    public void Unhold(IHolder holder) { if (State.Kind == "ended") return; hold?.Side.Release(hold.Value.Id, HostErrors.Released()); End(HostErrors.Released()); }
    public void Attach(HostLink link) { if (State.Kind != "ended") hold = (link.Consumer, link.Consumer.Attach(this)); }
    public void Dropped(ChordError error) { hold = null; Loading(); }
    public void Unreachable(ChordError error) { LastFailure = error; Move(RefState.Failed(new(FailureKind.Waiting, error))); }
    public Task<object?> CallAsync(string method, object? args, ChordContext context, Caller caller, long version)
    {
        if (State.Kind != "live" || bound is null || hold is null) return Task.FromException<object?>(HostErrors.NotReady(Label));
        if (!bound.Data.Methods.TryGetValue(method, out var data)) return Task.FromException<object?>(HostErrors.InvalidMember(method));
        return hold.Value.Side.CallAsync(hold.Value.Id, bound.Names.Methods.ToList().IndexOf(method), data, args, context);
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
    public bool Reaches(ITarget target, HashSet<ITarget> visited) => ReferenceEquals(this, target);
    public void Retry(RetryWalk walk)
    {
        if (State.Kind != "failed" || !walk.Visited.Add(this)) return;
        if (ReferenceEquals(path, host.UnplacedPath))
        {
            try
            {
                var next = host.Repath(contract, parameters, Caller);
                if (next is null) return;
                path.Detach(this); path = next; Move(RefState.Loading); path.Attach(this);
            }
            catch (Exception error) { Move(RefState.Failed(new(FailureKind.Broken, DataInternal.AsChordError(error, Label)))); }
        }
        else if (walk.Hops > 0 && hold is { } current) current.Side.Retry(current.Id, walk.Hops);
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
    internal void WentLive() { LastFailure = null; Move(RefState.Live); }
    internal void Failed(FailedMessage message)
    {
        var error = DataInternal.AsChordError(message.Cause.Error.ToException(), Label); LastFailure = error;
        var kind = Enum.Parse<FailureKind>(message.Cause.Kind, true);
        Move(RefState.Failed(new(kind, error), message.RetryIn is { } retry ? host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() + retry : null));
    }
    internal void Loading() { if (State.Kind is not ("ended" or "loading")) Move(RefState.Loading); }
    internal void TooLarge(ChordError error)
    {
        if (hold is { } current) current.Side.Release(current.Id, error);
        hold = null; path.Detach(this); path = host.UnplacedPath; path.Attach(this); LastFailure = error; Move(RefState.Failed(new(FailureKind.Broken, error)));
    }
    internal void Revoke(ChordError error) { if (hold is { } current) current.Side.Release(current.Id, error); Lost(error); }
    internal void Lost(ChordError error)
    {
        var holder = binding; if (hold is { } current) current.Side.Forget(current.Id, error); End(error); holder?.End(error);
    }
    private void End(ChordError error)
    {
        State = RefState.Ended(error); hold = null; binding = null; path.Detach(this);
        if (bound is not null) foreach (var replica in bound.Replicas.Values) replica.End(error);
        bound = null;
    }
    private void Move(RefState state)
    {
        if (State.Kind == "ended") return; State = state;
        if (binding is not { } holder) return;
        if (holder.Following) host.Batch.Within(holder.Retarget, host.Context); else holder.Retarget();
        holder.Moved();
    }
}
