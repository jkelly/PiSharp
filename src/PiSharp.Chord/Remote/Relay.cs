// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/relay.ts.
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Remote;

internal sealed class RelayedHold(ProviderSide downstream, HoldMessage request, Caller caller, RemotePath path) : IIncomingHold, IUpstream
{
    private HostLink? upstream;
    private long origin;
    public long Id => request.Hold;
    public Caller Caller => caller;
    internal bool Waiting => upstream is null;
    internal bool HeldOn(HostLink link) => ReferenceEquals(link, upstream);
    internal void Start() => path.Attach(this);
    public void Attach(HostLink link)
    {
        upstream = link; origin = downstream.RelayTo(link);
        var outgoing = request with { Origin = origin, Principal = null, Incarnation = null };
        if (link.Role == LinkRole.Route && Caller.Principal is not null) outgoing = outgoing with { Principal = Caller.Principal, Incarnation = Caller.Incarnation };
        link.Send(outgoing);
    }
    public void Dropped(ChordError error) { var previous = upstream; upstream = null; if (previous is not null) downstream.UpstreamDropped(this, error); }
    public void Unreachable(ChordError error) => downstream.Send(new FailedMessage(Id, new("waiting", WireError.FromException(error))));
    internal void Call(CallMessage message)
    {
        if (upstream is { } link) link.Send(message with { Origin = origin });
        else downstream.Reply(message.Call, new ErrorMessage(message.Call, WireError.FromException(new ChordError("unavailable", "the upstream link is down"))));
    }
    internal void Abort(long id) => upstream?.Send(new AbortMessage(id) { Origin = origin });
    public void Retry(long hops) => upstream?.Send(new RetryMessage(Id, hops) { Origin = origin });
    public void Release() { var link = upstream; Drop(); link?.Send(new ReleaseMessage(Id) { Origin = origin }); }
    public void Drop() { upstream = null; path.Detach(this); }
    internal void Lost() { Drop(); downstream.Forget(this); }
    internal void Revoke(ChordError error) { downstream.Send(new GoneMessage(Id, WireError.FromException(error))); Drop(); downstream.Forget(this); }
}
