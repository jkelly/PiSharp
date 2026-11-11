// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/relay.ts.
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Remote;

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
    internal OpenSpan? Call(CallMessage message)
    {
        // A relay is a trust boundary even when it is not recording telemetry.
        if (upstream is not { } link)
        {
            downstream.Reply(message.Call, new ErrorMessage(message.Call, WireError.FromException(new ChordError("unavailable", "the upstream link is down"))));
            return null;
        }
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
