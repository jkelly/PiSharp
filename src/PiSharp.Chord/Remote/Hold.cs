// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/hold.ts.
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
    private object? instance;
    private bool released, counted = true, binding, toldFailed;
    internal IncomingHold(ProviderSide provider, HoldMessage request, Caller caller, Limits limits)
    {
        this.provider = provider; this.request = request; Caller = caller; Limits = limits;
        provider.Link.Host.CountReference(1);
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
    internal bool Callable => !released && names is not null && target?.State.Kind == "live";
    internal bool Unbound => !released && target is null;
    private void Published()
    {
        if (released) return;
        if (target is ChordObject local)
        {
            if (!DataInternal.Binds(local.Served.Provider, request.Version, request.Fingerprint))
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
            var made = host.LocalObject(found, new CheckedParams(parameters.Params, parameters.Key), Caller);
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
        toldFailed = true; provider.Send(new FailedMessage(Id, new WireFailure(kind.ToString().ToLowerInvariant(), WireError.FromException(error))));
    }
    public void Retarget()
    {
        // StopFollowing can detach an old attempt without changing the object's loading/failed state.
        if (!released && names is not null && !FollowsBoundInstance) Moved();
    }
    public void Moved()
    {
        if (released || target is null) return;
        var state = target.State;
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
        if (names is not null && FollowsBoundInstance) return;
        if (names is not null || binding)
        {
            Unfollow(); names = null; variant = null; instance = null; binding = false;
            provider.Unbound(this, state.Cause?.Error ?? new ChordError("unavailable", "hold rebinds"));
            if (state.Kind == "loading") provider.Send(new LoadingMessage(Id));
        }
        if (state.Cause is { } cause)
        {
            var now = provider.Link.Host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            provider.Send(new FailedMessage(Id, new(cause.Kind.ToString().ToLowerInvariant(), WireError.FromException(cause.Error)),
                state.RetryAt is { } retry ? (long)Math.Max(0, retry - now) : null)); toldFailed = true;
        }
        else if (toldFailed) { toldFailed = false; provider.Send(new LoadingMessage(Id)); }
    }
    internal BoundEntry BoundEntry()
    {
        if (target is null || served is null || target.State.Kind != "live") throw new InvalidOperationException("bind a live hold");
        Unfollow(); binding = false; instance = target.Instance;
        var variants = ContractsInternal.VariantsAt(served.Contract, request.Version);
        var name = target.Variant ?? ContractsInternal.Local;
        if (!variants.TryGetValue(name, out variant)) throw new ArgumentException("variant is not in consumer version");
        names = ContractsInternal.VariantNamesOf(variant);
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
        if (names is null || variant is null || index < 0 || index >= names.Methods.Count) throw new ArgumentException($"hold {Id}: no method {index}");
        var name = names.Methods[(int)index]; return (name, variant.Methods[name].Result.Bytes);
    }
    internal Task<object?> CallAsync(string method, object? args, ChordContext context) => target!.CallAsync(method, args, context, Caller, request.Version);
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
        served = null; names = null; variant = null; instance = null;
        if (counted) { counted = false; provider.Link.Host.CountReference(-1); }
    }
    private void Unfollow()
    {
        foreach (var member in followed) if (member.Source is Source changing) changing.Unsubscribe(member);
        followed.Clear();
    }
}
