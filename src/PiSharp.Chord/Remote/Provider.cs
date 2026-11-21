// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/provider.ts.
// The provider side of one origin on a link: the holds the peer sent for that origin, served here or relayed, by their ids; the calls on
// them, at most this side's N at once (none on a trusted link), relayed ones waiting for their upstream link's window; the window: at most
// K unacknowledged bound and state messages of this host's own, with what waits meanwhile (a call's answer sends the waiting state first,
// past the window), and the sender of every message in flight, so each ack reaches the host that sent its message; and events (decision
// "Events: fire-and-forget members"), with a window of their own: at most EventWindow of this host's event messages unacknowledged (a full
// window drops a flush's events) and a FIFO of tickets naming each message's sender.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using WireFailure = PiSharp.Chord.Link.FailureCause;

namespace PiSharp.Chord.Remote;

internal interface IIncomingHold
{
    long Id { get; }
    Caller Caller { get; }
    void Release();
    void Drop();
    void Retry(long hops);
}
internal sealed class ProviderSide(HostLink link, long origin, long? limit) : IEventSender
{
    // A call being handled: on a hold served here, with the abort of its handler's context; or relayed, waiting for or forwarded to its
    // upstream link, its reply forwarded from there.
    private sealed class Running(IIncomingHold hold, CancellationTokenSource? abort, RelayedCall? relayed = null)
    {
        internal IIncomingHold Hold { get; } = hold;
        internal CancellationTokenSource? Abort { get; } = abort;
        internal RelayedCall? Relayed { get; } = relayed;
    }
    // Who sent a bound or state message in flight: this host (From null), the upstream link a relay forwarded a state from, or for a
    // relayed bound that link and the holds it bound (their queued calls go before its ack does: Ack).
    private readonly record struct Sender(HostLink? From, long[]? Holds = null);
    internal HostLink Link => link;
    internal long Origin => origin;
    private readonly Dictionary<long, IIncomingHold> entries = [];
    private readonly Dictionary<long, Running> calls = [];
    private readonly Queue<Sender> sent = [];
    private readonly HashSet<IncomingHold> bindings = [], waiting = [], touched = [];
    // The served holds with a binding fence not acknowledged yet: each drops its fences as the acks of this host's own messages arrive.
    private readonly HashSet<IncomingHold> fenced = [];
    private readonly HashSet<HostLink> relayLinks = [];
    private long? relayOrigin;
    private long ownSent, ownAcked;
    private long? waitingSince;
    private int inFlight;
    private bool bindingTurn, closed;
    // The event flush's entries for this origin's holds served here, until it sends them (SendEvents).
    private List<WireEvent> events = [];
    // The sender of each event message of this origin in flight, in order (null: this host): an event ack consumes the first ones.
    private readonly Queue<HostLink?> tickets = [];
    // This host's own event messages in flight: at most the host's EventWindow.
    private int eventsInFlight;
    private bool Free => bindings.Count == 0 && waiting.Count == 0 && inFlight < 2;
    internal long NextOwn => ownSent + 1;
    internal bool Acknowledged(long own) => ownAcked >= own;
    internal async Task MessageAsync(Message message)
    {
        if (closed) return;
        switch (message)
        {
            case HoldMessage hold: Hold(hold); break;
            case ReleaseMessage release:
                if (entries.TryGetValue(release.Hold, out var released))
                {
                    // A relayed one is released upstream first: forgetting it frees its forwarded calls' slots in the upstream window,
                    // and a call of another hold that takes one must reach the upstream after this release, which ends the forwarded
                    // ones there (else the upstream counts one call too many).
                    if (released is RelayedHold) { released.Release(); Forget(released); }
                    else { Forget(released); released.Release(); }
                }
                break;
            case RetryMessage retry: entries.GetValueOrDefault(retry.Hold)?.Retry(retry.Hops); break;
            case AbortMessage abort:
                if (calls.Remove(abort.Call, out var aborted))
                {
                    if (aborted.Relayed is not { } relayedCall) aborted.Abort!.Cancel();
                    // A call still waiting for its upstream window leaves it unsent; one forwarded is aborted upstream, then frees its
                    // slot (the upstream counts it until the abort).
                    else if (!relayedCall.Unqueue()) { relayedCall.Hold.Abort(abort.Call); relayedCall.End(); }
                }
                break;
            case AckMessage ack: if (ack.Events is { } count) EventAck(count); else Ack(); break;
            case CallMessage call: await Call(call); break;
        }
        Settle();
    }
    private void Hold(HoldMessage message)
    {
        if (entries.ContainsKey(message.Hold)) throw new ArgumentException($"hold {message.Hold}: the id is in use");
        if (message.Params is Part.Deflated) throw new ArgumentException("compressed params");
        var caller = link.Role == LinkRole.Parent
            ? new Caller((message.Principal as JsonObject) ?? link.Principal, message.Incarnation ?? link.Peer)
            : new Caller(link.Principal, link.Peer);
        var decoded = Math.Min(link.Limits.Decoded, link.Role == LinkRole.Parent ? message.Limit ?? long.MaxValue : long.MaxValue);
        var request = message with
        {
            Principal = null, Incarnation = null, Origin = null,
            Chain = link.Trusted ? message.Chain : null, Limit = decoded == long.MaxValue ? null : decoded,
            Params = message.Params is Part.Bytes bytes ? new Part.Bytes(bytes.Value.ToArray()) : message.Params,
        };
        var hold = new IncomingHold(this, request, caller, new Limits(link.Limits.Part, decoded));
        entries.Add(message.Hold, hold); hold.Start();
    }
    private async Task Call(CallMessage message)
    {
        // Unknown holds consume neither a call slot nor any decoding work.
        if (calls.ContainsKey(message.Call)) throw new ArgumentException($"call {message.Call}: the id is in use");
        if (!entries.TryGetValue(message.Hold, out var hold)) return;
        // No window announced (a trusted link): no count closes the link (decision "Call windows only for untrusted links").
        if (limit is { } most && calls.Count >= most) throw new ArgumentException($"call {message.Call}: more than {most} calls on an origin");
        if (message.Args is Part.Deflated && link.Role == LinkRole.Child) throw new ArgumentException("compressed arguments from a child");
        if (hold is RelayedHold relayed)
        {
            if (message.Args is { } part && Parts.Oversized(part, link.Limits.Part) is { } tooLarge) { Refuse(message.Call, tooLarge); return; }
            var forwarded = new RelayedCall(relayed, message);
            // Registered first: a hold without an upstream link answers at once.
            calls.Add(message.Call, new Running(hold, null, forwarded)); relayed.Submit(forwarded); return;
        }
        var local = (IncomingHold)hold;
        if (!local.Callable) { Refuse(message.Call, local.Refusal); return; }
        var (name, bytes) = local.Method(message.Method);
        await link.InflateOwned(message.Args, local.Limits);
        if (!link.Open || !entries.TryGetValue(message.Hold, out var current) || !ReferenceEquals(current, hold)) return;
        object? args;
        try { args = message.Args is null ? null : ToHostValue(Parts.ParsePayload(link.OpenPart(message.Args, local.Limits), message.Segments)); }
        catch (ChordError error) when (error.Code == "too_large") { Refuse(message.Call, error); return; }
        var abort = new CancellationTokenSource(); var running = new Running(hold, abort); calls.Add(message.Call, running);
        var scope = message.Old == true && link.Trusted ? OldWork.AsRemote(link.Context) : link.Context;
        scope = ChordHost.WithTelemetryParent(ChordTelemetry.ParentOf(message.Trace, link.Host.Telemetry), scope);
        _ = Run(message.Call, local, name, args, bytes, abort, scope);
    }
    private async Task Run(long id, IncomingHold hold, string method, object? args, bool bytes, CancellationTokenSource abort, ChordContext scope)
    {
        Message reply;
        try
        {
            var value = await hold.CallAsync(method, args, ChordContext.WithAbortSignal(abort.Token, scope));
            if (value is null) reply = new ResultMessage(id);
            else { var payload = Parts.Payload(value, bytes); reply = new ResultMessage(id, link.Part(payload.Json), payload.Segments); }
        }
        catch (Exception error) { reply = new ErrorMessage(id, WireError.FromException(error)); }
        Reply(id, reply);
    }
    private static JsonNode? ToHostValue(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        ReadOnlyMemory<byte> bytes => JsonValue.Create(bytes.ToArray()),
        byte[] bytes => JsonValue.Create(bytes),
        IDictionary<string, object?> dictionary => new JsonObject(dictionary.Select(pair => KeyValuePair.Create(pair.Key, ToHostValue(pair.Value)))),
        object?[] array => new JsonArray(array.Select(ToHostValue).ToArray()),
        _ => System.Text.Json.JsonSerializer.SerializeToNode(value),
    };
    private void Refuse(long id, ChordError error) => Send(new ErrorMessage(id, WireError.FromException(error)));
    internal void Reply(long id, Message reply)
    {
        if (!calls.Remove(id, out var call)) return;
        call.Abort?.Dispose();
        call.Relayed?.End(reply);
        // One state message may pass the window before each answer. Its ACK precedes any
        // next call admitted by this answer, keeping messages plus calls bounded together.
        if (waiting.Count > 0) SendWaiting();
        Send(reply); Settle();
    }
    internal void Send(Message message) { if (!closed) link.Send(message with { Origin = origin == 0 ? null : origin }); }
    internal void Replace(IncomingHold local, RelayedHold relay) { entries[local.Id] = relay; local.Drop(); relay.Start(); }
    internal void Forget(IIncomingHold hold, ChordError? error = null)
    {
        if (entries.GetValueOrDefault(hold.Id) == hold) entries.Remove(hold.Id);
        List<RelayedCall>? relayed = null;
        foreach (var (id, call) in calls.Where(x => ReferenceEquals(x.Value.Hold, hold)).ToArray())
        {
            calls.Remove(id);
            if (call.Relayed is { } forwarded) (relayed ??= []).Add(forwarded);
            else { call.Abort?.Cancel(); call.Abort?.Dispose(); }
        }
        // The forwarded ones end upstream with the hold (its release, sent before this, or the upstream's gone).
        if (relayed is not null) RelayedCall.EndAll(relayed);
        if (hold is IncomingHold local) { Unbind(local); fenced.Remove(local); }
        Settle();
    }
    internal void Unbind(IncomingHold hold)
    {
        waiting.Remove(hold); bindings.Remove(hold); touched.Remove(hold);
        if (waiting.Count == 0) waitingSince = null;
    }
    internal void Unbound(IncomingHold hold, ChordError error)
    {
        Unbind(hold);
        foreach (var (id, call) in calls.Where(x => ReferenceEquals(x.Value.Hold, hold)).ToArray())
        { call.Abort?.Cancel(); Reply(id, new ErrorMessage(id, WireError.FromException(error))); }
    }
    internal void Bind(IncomingHold hold)
    {
        if (closed) return;
        if (Free) SendOwn(new BoundMessage([hold.BoundEntry()])); else bindings.Add(hold);
    }
    /// <summary><paramref name="hold"/> has a binding fence its consumer has not acknowledged (a rebinding's bound is about to go out):
    /// the acks of this host's own messages tell it until none is left.</summary>
    internal void Fenced(IncomingHold hold) => fenced.Add(hold);
    internal void Touched(IncomingHold hold)
    {
        if (closed) return;
        if (touched.Count == 0) link.Host.Batch.Flushing(_ => Flush());
        touched.Add(hold);
    }
    private void Flush()
    {
        var changed = touched.ToArray(); touched.Clear(); if (closed) return;
        if (Free)
        {
            var batch = changed.Where(x => x.Bound).Select(x => x.StateEntry()).ToArray();
            if (batch.Length > 0) SendOwn(new StateMessage(batch));
        }
        else foreach (var hold in changed.Where(x => x.Bound))
        {
            hold.Queue(); waiting.Add(hold); waitingSince ??= link.Host.Options.TimeProvider.GetTimestamp();
        }
    }
    /// <summary>
    /// An event flush's entry for served hold <paramref name="hold"/>'s event member <paramref name="member"/> (an index into its bound
    /// variant's). While this host's event window is full for this origin it is dropped as visited (counted), before anything is encoded:
    /// nothing waits for a slow consumer, and no value is encoded only to be dropped. Otherwise it is queued for this flush's message of
    /// this origin, its value encoded once for every hold and link of the host.
    /// </summary>
    internal void QueueEvent(long hold, long member, EventEntry entry, EventFlush flush)
    {
        if (closed) return;
        var metrics = link.Host.Events.Metrics;
        if (eventsInFlight >= link.Host.Options.EventWindow)
        {
            metrics.Counter("chord.events.dropped").Add(1, ChordTelemetry.EventsDropped("window")); return;
        }
        if (events.Count == 0) flush.Sending(this);
        if (entry.Part is null)
        {
            entry.Part = link.Part(ChordJson.Stringify(entry.Value));
            metrics.Counter("chord.events.encodes").Add(1);
        }
        events.Add(new(hold, member, entry.At, entry.Part));
    }
    /// <summary>The event flush visited every entry: this origin's admitted entries go as one event message, taking a slot.</summary>
    public void SendEvents()
    {
        var sending = events; events = [];
        // The side closed during the flush.
        if (sending.Count == 0 || closed) return;
        eventsInFlight++; tickets.Enqueue(null);
        link.Host.Events.Metrics.Counter("chord.events.sent").Add(sending.Count);
        Send(new EventMessage(sending));
    }
    // An ack answers the oldest message in flight: this host's own frees a slot, a relayed one is acknowledged to its upstream link. An
    // ack crossing nothing in flight is ignored. Before a relayed bound's ack goes upstream (the provider there stops decoding the old
    // indexes then), the calls of the holds it bound still waiting for the upstream window are rejected unavailable: they crossed the
    // loading notice and carry the old indexes; calls arriving after this ack were made with the new binding.
    private void Ack()
    {
        if (!sent.TryDequeue(out var sender)) return;
        if (sender.From is { } from)
        {
            foreach (var id in sender.Holds ?? [])
                if (entries.GetValueOrDefault(id) is RelayedHold hold && hold.HeldOn(from))
                    RejectRelayed(hold, new ChordError("unavailable", $"hold {id}: rebound"), all: false);
            if (relayOrigin is { } relay) from.Send(new AckMessage { Origin = relay });
            return;
        }
        inFlight--; ownAcked++;
        if (fenced.Count > 0) fenced.RemoveWhere(hold => hold.Acknowledged());
        while (inFlight < 2)
        {
            if (bindings.Count > 0 && (waiting.Count == 0 || bindingTurn))
            {
                var next = bindings.ToArray(); bindings.Clear(); bindingTurn = false;
                SendOwn(new BoundMessage(next.Select(x => x.BoundEntry()).ToArray()));
            }
            else if (waiting.Count > 0)
            {
                SendWaiting();
            }
            else break;
        }
    }
    // An event ack: its consumer consumed count more event messages of this origin, the first tickets: this host's own free slots of its
    // event window, a relayed one is returned to its upstream link (summed per link into one ack; a link closed meanwhile takes nothing).
    // Separate from the state window and its fence.
    private void EventAck(long count)
    {
        Dictionary<HostLink, long>? returned = null;
        for (var consumed = 0L; consumed < count && tickets.TryDequeue(out var ticket); consumed++)
        {
            if (ticket is null) { eventsInFlight--; continue; }
            returned ??= []; returned[ticket] = returned.GetValueOrDefault(ticket) + 1;
        }
        if (returned is null || relayOrigin is not { } relay) return;
        foreach (var (upstream, consumed) in returned) upstream.Send(new AckMessage(consumed) { Origin = relay });
    }
    private void SendWaiting()
    {
        var next = waiting.ToArray(); waiting.Clear(); bindingTurn = bindings.Count > 0;
        if (waitingSince is { } since)
        {
            ChordTelemetry.Metrics(link.Host.Telemetry.Metrics).Histogram("chord.backpressure.wait")
                .Record(link.Host.Options.TimeProvider.GetElapsedTime(since).TotalMilliseconds);
            waitingSince = null;
        }
        SendOwn(new StateMessage(next.Select(x => x.PendingEntry()).ToArray()));
    }
    private void SendOwn(Message message)
    {
        inFlight++; ownSent++; sent.Enqueue(default);
        if (message is StateMessage) ChordTelemetry.Metrics(link.Host.Telemetry.Metrics).Counter("chord.link.state_messages").Add(1);
        Send(message);
    }
    // Drops this side from its link once it holds nothing: no holds, no calls, no state and no event messages in flight.
    private void Settle() { if (!closed && entries.Count == 0 && calls.Count == 0 && sent.Count == 0 && tickets.Count == 0) link.Drop(this); }
    internal long RelayTo(HostLink upstream)
    {
        relayOrigin ??= link.Host.RelayOrigin(this); relayLinks.Add(upstream); return relayOrigin.Value;
    }
    internal void Unlinked(HostLink upstream) => relayLinks.Remove(upstream);
    /// <summary>
    /// A message from upstream link <paramref name="from"/> for this origin's relayed holds, forwarded with this origin: bound and state
    /// join the window's senders (their ack goes back to <paramref name="from"/>), an event message joins the tickets (forwarded at once,
    /// never dropped for congestion and never acknowledged here, also with no entry left: the downstream's acks, in order, return each
    /// ticket to its sender), a reply ends its call, gone forgets its hold. A hold's calls still waiting for the upstream window are
    /// rejected before its loading (unavailable: they carry the old variant's method indexes; its forwarded ones are kept), failed or gone
    /// (with that cause) is forwarded. A link answers only for what was relayed over it.
    /// </summary>
    internal void Forward(Message message, HostLink from)
    {
        if (closed || relayOrigin is null || !relayLinks.Contains(from)) return;
        bool Relayed(long id) => entries.GetValueOrDefault(id) is RelayedHold hold && hold.HeldOn(from);
        switch (message)
        {
            case BoundMessage bound:
            {
                var selected = bound.Holds.Where(x => Relayed(x.Hold)).ToArray();
                if (selected.Length == 0) { from.Send(new AckMessage { Origin = relayOrigin }); return; }
                sent.Enqueue(new(from, selected.Select(x => x.Hold).ToArray())); Send(new BoundMessage(selected)); break;
            }
            case StateMessage state:
            {
                var selected = state.Holds.Where(x => Relayed(x.Hold)).ToArray();
                if (selected.Length == 0) { from.Send(new AckMessage { Origin = relayOrigin }); return; }
                sent.Enqueue(new(from)); Send(new StateMessage(selected)); break;
            }
            case EventMessage forwarded:
                tickets.Enqueue(from); Send(new EventMessage(forwarded.Events.Where(x => Relayed(x.Hold)).ToArray())); break;
            case ResultMessage result when calls.GetValueOrDefault(result.Call)?.Hold is RelayedHold hold && hold.HeldOn(from): Reply(result.Call, result); break;
            case ErrorMessage error when calls.GetValueOrDefault(error.Call)?.Hold is RelayedHold hold && hold.HeldOn(from): Reply(error.Call, error); break;
            case GoneMessage gone when entries.GetValueOrDefault(gone.Hold) is RelayedHold hold && hold.HeldOn(from):
                RejectRelayed(hold, DataInternal.AsChordError(gone.Error.ToException(), $"hold {hold.Id}"), all: false);
                Send(gone); hold.Lost(); break;
            case LoadingMessage loading when entries.GetValueOrDefault(loading.Hold) is RelayedHold hold && hold.HeldOn(from):
                RejectRelayed(hold, new ChordError("unavailable", $"hold {hold.Id}: rebinding"), all: false);
                Send(loading); break;
            case FailedMessage failed when entries.GetValueOrDefault(failed.Hold) is RelayedHold hold && hold.HeldOn(from):
                RejectRelayed(hold, DataInternal.AsChordError(failed.Cause.Error.ToException(), $"hold {hold.Id}"), all: false);
                Send(failed); break;
        }
    }
    /// <summary>The upstream link of relayed hold <paramref name="hold"/> dropped with <paramref name="error"/>: its calls fail, in flight
    /// or still waiting for the upstream window (never resent), the hold goes loading downstream, and its path's next link holds it again.</summary>
    internal void UpstreamDropped(RelayedHold hold, ChordError error)
    {
        RejectRelayed(hold, error, all: true);
        Send(new LoadingMessage(hold.Id));
    }
    // Answers error to the calls of relayed hold `hold`: those still waiting for the upstream window, or all of them. The waiting ones
    // leave their window before any slot is counted free, so a freed slot never forwards a call being rejected.
    private void RejectRelayed(RelayedHold hold, ChordError error, bool all)
    {
        var rejected = new List<long>();
        foreach (var (id, call) in calls)
        {
            if (!ReferenceEquals(call.Hold, hold) || call.Relayed is not { } relayed) continue;
            if (relayed.Unqueue() || all) rejected.Add(id);
        }
        if (rejected.Count == 0) return;
        var answer = WireError.FromException(error);
        foreach (var id in rejected) Reply(id, new ErrorMessage(id, answer));
    }
    internal void RevokeUnbound(Func<JsonObject, bool> predicate, ChordError error)
    {
        foreach (var hold in entries.Values.ToArray())
            if (hold.Caller.Principal is { } principal && predicate(principal))
            { if (hold is IncomingHold { Unbound: true } local) local.End(error); else if (hold is RelayedHold { Waiting: true } relay) relay.Revoke(error); }
    }
    internal void Close()
    {
        if (closed) return; closed = true;
        var relayed = new List<RelayedCall>();
        foreach (var call in calls.Values)
        {
            if (call.Relayed is { } forwarded) relayed.Add(forwarded);
            else { call.Abort?.Cancel(); call.Abort?.Dispose(); }
        }
        calls.Clear();
        // Relayed calls end with their upstream windows (Unrelay below): closed aborts them upstream.
        RelayedCall.EndAll(relayed);
        var previous = entries.Values.ToArray(); entries.Clear();
        foreach (var hold in previous) hold.Drop();
        // A batch or an event flush running later finds nothing of this side.
        touched.Clear(); waiting.Clear(); bindings.Clear(); fenced.Clear(); sent.Clear(); events = []; tickets.Clear();
        if (relayOrigin is { } id)
        {
            link.Host.Relays.Remove(id);
            foreach (var upstream in relayLinks) { upstream.Send(new ClosedMessage { Origin = id }); upstream.Consumer.Unrelay(id); }
        }
        relayLinks.Clear();
    }
}
