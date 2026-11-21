// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/relay.ts.
// 1.6.0: a relayed call waits in its upstream link's call window for its origin (decision "Call windows only for untrusted links": a relay
// honours each upstream window) and is forwarded as a slot frees.
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Remote;

/// <summary>
/// A downstream call on a relayed hold, as its downstream origin keeps it: registered (answered at once when its hold has no upstream
/// link), waiting in its upstream link's window for the origin until a slot forwards it (<see cref="Send"/>), then in flight (with its
/// <c>chord.relay</c> span when this host records) until its reply, its abort, its hold's end or the upstream's drop.
/// </summary>
internal sealed class RelayedCall(RelayedHold hold, CallMessage message) : IQueuedCall
{
    private enum Stage { New, Queued, Forwarded, Ended }
    private Stage stage;
    private CallWindow<RelayedCall>? window;
    private OpenSpan? span;
    internal RelayedHold Hold => hold;
    internal CallMessage Message => message;
    /// <summary>Whether it was forwarded upstream and is not answered yet.</summary>
    internal bool Forwarded => stage == Stage.Forwarded;
    /// <summary>Waits in <paramref name="queue"/> (its hold's upstream link's, for its origin), forwarded at once when a slot is free.</summary>
    internal void Queue(CallWindow<RelayedCall> queue) { stage = Stage.Queued; window = queue; queue.Submit(this); }
    /// <summary>Its window has a slot for it: forwarded upstream.</summary>
    public void Send() { stage = Stage.Forwarded; span = hold.Call(message); }
    /// <summary>Takes it out of its window when it still waits there (ended unsent); whether it did.</summary>
    internal bool Unqueue()
    {
        if (stage != Stage.Queued) return false;
        stage = Stage.Ended; window!.Remove(this); return true;
    }
    /// <summary>Ends it: a waiting one leaves its window, a forwarded one frees its slot (which may forward the next waiting call) and its
    /// span ends with <paramref name="reply"/> (none: aborted).</summary>
    internal void End(Message? reply = null)
    {
        var was = stage; stage = Stage.Ended;
        if (was == Stage.Queued) window!.Remove(this);
        if (was != Stage.Forwarded) return;
        RelayedHold.EndCall(span, reply); window!.Freed();
    }
    /// <summary>Ends <paramref name="calls"/> (of one hold or origin): the waiting ones leave their windows first, so a slot a forwarded
    /// one frees never forwards one of them.</summary>
    internal static void EndAll(IReadOnlyList<RelayedCall> calls)
    {
        foreach (var call in calls) call.Unqueue();
        foreach (var call in calls) call.End();
    }
}

internal sealed class RelayedHold(ProviderSide downstream, HoldMessage request, Caller caller, RemotePath path) : IIncomingHold, IUpstream
{
    private HostLink? upstream;
    private long origin;
    private OpenSpan? relaying;
    private IReadOnlyDictionary<string, string>? firstTrace;
    private int holds;
    public ITelemetrySpan? Span => relaying?.Span;
    public long Id => request.Hold;
    public Caller Caller => caller;
    internal bool Waiting => upstream is null;
    internal bool HeldOn(HostLink link) => ReferenceEquals(link, upstream);
    internal void Start()
    {
        firstTrace = request.Trace;
        var adapter = downstream.Link.Host.Telemetry;
        if (!ChordTelemetry.IsUntraced(adapter))
        {
            relaying = new OpenSpan(ChordTelemetry.ParentOf(request.Trace, adapter), "chord.relay", Attributes("hold"));
            firstTrace = ChordTelemetry.CarrierOf(relaying.Span) ?? firstTrace;
        }
        path.Attach(this);
    }
    public void Attach(HostLink link)
    {
        upstream = link; origin = downstream.RelayTo(link);
        var outgoing = request with { Origin = origin, Principal = null, Incarnation = null, Trace = HoldTrace() };
        if (link.Role == LinkRole.Route && Caller.Principal is not null) outgoing = outgoing with { Principal = Caller.Principal, Incarnation = Caller.Incarnation };
        link.Send(outgoing);
        Relayed("ok");
    }
    public void Dropped(ChordError error) { var previous = upstream; upstream = null; if (previous is not null) downstream.UpstreamDropped(this, error); }
    public void Unreachable(ChordError error)
    { Relayed("error", error); downstream.Send(new FailedMessage(Id, new("waiting", WireError.FromException(error)))); }
    /// <summary>A call from downstream (registered there under its id): it waits in its upstream link's window for this origin and is
    /// forwarded (<see cref="Call"/>) as a slot frees, at once when one is free and nothing waits; its reply comes back by the origin.
    /// Without an upstream link it is answered <c>unavailable</c> at once (it crossed the <c>loading</c> notice).</summary>
    internal void Submit(RelayedCall call)
    {
        if (upstream is not { } link)
        {
            var id = call.Message.Call;
            downstream.Reply(id, new ErrorMessage(id, WireError.FromException(new ChordError("unavailable", "the upstream link is down"))));
            return;
        }
        call.Queue(link.Consumer.RelayWindow(origin));
    }
    /// <summary>Forwards a submitted call upstream, now that its window has a slot; returns its <c>chord.relay</c> span when this host
    /// records.</summary>
    internal OpenSpan? Call(CallMessage message)
    {
        // A call waits only in its hold's current upstream window: the drop answers it first (UpstreamDropped), a release forgets it.
        var link = upstream!;
        // A relay is a trust boundary even when it is not recording telemetry.
        var forwarded = message with { Origin = origin, Old = downstream.Link.Trusted ? message.Old : null };
        OpenSpan? span = null;
        var adapter = downstream.Link.Host.Telemetry;
        if (!ChordTelemetry.IsUntraced(adapter))
        {
            span = new OpenSpan(ChordTelemetry.ParentOf(message.Trace, adapter), "chord.relay", Attributes("#" + message.Method));
            forwarded = forwarded with { Trace = ChordTelemetry.CarrierOf(span.Span) ?? message.Trace };
        }
        link.Send(forwarded); return span;
    }
    internal void Abort(long id) => upstream?.Send(new AbortMessage(id) { Origin = origin });
    public void Retry(long hops) => upstream?.Send(new RetryMessage(Id, hops) { Origin = origin });
    public void Release() { var link = upstream; Drop(); link?.Send(new ReleaseMessage(Id) { Origin = origin }); }
    public void Drop() { Relayed("aborted"); upstream = null; path.Detach(this); }
    internal void Lost() { Drop(); downstream.Forget(this); }
    internal void Revoke(ChordError error) { downstream.Send(new GoneMessage(Id, WireError.FromException(error))); Drop(); downstream.Forget(this); }
    private SpanAttributes Attributes(string member) => new()
    {
        ["chord.host"] = ChordTelemetry.HostAttribute(downstream.Link.Host.Address),
        ["type"] = ChordTelemetry.Capped(request.Type), ["member"] = member,
        ["upstream.kind"] = ChordTelemetry.Capped(request.Address.Kind),
    };
    private IReadOnlyDictionary<string, string>? HoldTrace()
    {
        if (holds++ == 0) return firstTrace;
        var adapter = downstream.Link.Host.Telemetry;
        if (firstTrace is null || ChordTelemetry.IsUntraced(adapter)) return null;
        var again = new OpenSpan(adapter, "chord.relay", Attributes("hold"));
        again.Span.AddLink(new(ChordTelemetry.ParentOf(firstTrace, adapter)));
        var carrier = ChordTelemetry.CarrierOf(again.Span);
        again.Span.SetAttributes(new() { ["outcome"] = "ok" }); again.End(); return carrier;
    }
    private void Relayed(string outcome, Exception? error = null)
    {
        if (relaying is not { } span) return; relaying = null;
        if (error is not null) ChordTelemetry.SpanFailed(span.Span, "relay_failed", error);
        span.Span.SetAttributes(new() { ["outcome"] = outcome }); span.End();
    }
    internal static void EndCall(OpenSpan? span, Message? reply)
    {
        if (span is null) return;
        var outcome = reply is ErrorMessage error ? ChordTelemetry.Ended(span.Span, "relay_failed", error.Error.ToException()) : reply is null ? "aborted" : "ok";
        span.Span.SetAttributes(new() { ["outcome"] = outcome }); span.End();
    }
}
