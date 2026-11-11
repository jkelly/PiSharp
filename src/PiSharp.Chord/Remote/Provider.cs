// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/provider.ts.
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
internal sealed class ProviderSide(HostLink link, long origin)
{
    private sealed class Running(IIncomingHold hold, CancellationTokenSource? abort)
    {
        internal IIncomingHold Hold { get; } = hold;
        internal CancellationTokenSource? Abort { get; } = abort;
        internal OpenSpan? Span;
    }
    internal HostLink Link => link;
    internal long Origin => origin;
    private readonly Dictionary<long, IIncomingHold> entries = [];
    private readonly Dictionary<long, Running> calls = [];
    private readonly Queue<HostLink?> sent = [];
    private readonly HashSet<IncomingHold> bindings = [], waiting = [], touched = [];
    private readonly HashSet<HostLink> relayLinks = [];
    private long? relayOrigin;
    private long ownSent, ownAcked;
    private long? waitingSince;
    private int inFlight;
    private bool bindingTurn, closed;
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
                if (entries.TryGetValue(release.Hold, out var released)) { Forget(released); released.Release(); } break;
            case RetryMessage retry: entries.GetValueOrDefault(retry.Hold)?.Retry(retry.Hops); break;
            case AbortMessage abort:
                if (calls.Remove(abort.Call, out var aborted))
                { if (aborted.Hold is RelayedHold relay) { relay.Abort(abort.Call); RelayedHold.EndCall(aborted.Span, null); } else aborted.Abort!.Cancel(); }
                break;
            case AckMessage: Ack(); break;
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
        if (calls.Count >= 256) throw new ArgumentException("more than 256 calls on an origin");
        if (message.Args is Part.Deflated && link.Role == LinkRole.Child) throw new ArgumentException("compressed arguments from a child");
        if (hold is RelayedHold relayed)
        {
            if (message.Args is { } part && Parts.Oversized(part, link.Limits.Part) is { } tooLarge) { Refuse(message.Call, tooLarge); return; }
            var forwarded = new Running(hold, null);
            calls.Add(message.Call, forwarded); forwarded.Span = relayed.Call(message); return;
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
        // One state message may pass the window before each answer. Its ACK precedes any
        // next call admitted by this answer, keeping messages plus calls bounded together.
        if (waiting.Count > 0) SendWaiting();
        Send(reply); RelayedHold.EndCall(call.Span, reply); Settle();
    }
    internal void Send(Message message) { if (!closed) link.Send(message with { Origin = origin == 0 ? null : origin }); }
    internal void Replace(IncomingHold local, RelayedHold relay) { entries[local.Id] = relay; local.Drop(); relay.Start(); }
    internal void Forget(IIncomingHold hold, ChordError? error = null)
    {
        if (entries.GetValueOrDefault(hold.Id) == hold) entries.Remove(hold.Id);
        foreach (var (id, call) in calls.Where(x => ReferenceEquals(x.Value.Hold, hold)).ToArray())
        { calls.Remove(id); call.Abort?.Cancel(); call.Abort?.Dispose(); RelayedHold.EndCall(call.Span, null); }
        if (hold is IncomingHold local) Unbind(local);
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
    private void Ack()
    {
        if (!sent.TryDequeue(out var sender)) return;
        if (sender is not null) { if (relayOrigin is { } id) sender.Send(new AckMessage { Origin = id }); return; }
        inFlight--; ownAcked++;
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
        inFlight++; ownSent++; sent.Enqueue(null);
        if (message is StateMessage) ChordTelemetry.Metrics(link.Host.Telemetry.Metrics).Counter("chord.link.state_messages").Add(1);
        Send(message);
    }
    private void Settle() { if (!closed && entries.Count == 0 && calls.Count == 0 && sent.Count == 0) link.Drop(this); }
    internal long RelayTo(HostLink upstream)
    {
        relayOrigin ??= link.Host.RelayOrigin(this); relayLinks.Add(upstream); return relayOrigin.Value;
    }
    internal void Unlinked(HostLink upstream) => relayLinks.Remove(upstream);
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
                sent.Enqueue(from); Send(new BoundMessage(selected)); break;
            }
            case StateMessage state:
            {
                var selected = state.Holds.Where(x => Relayed(x.Hold)).ToArray();
                if (selected.Length == 0) { from.Send(new AckMessage { Origin = relayOrigin }); return; }
                sent.Enqueue(from); Send(new StateMessage(selected)); break;
            }
            case ResultMessage result when calls.GetValueOrDefault(result.Call)?.Hold is RelayedHold hold && hold.HeldOn(from): Reply(result.Call, result); break;
            case ErrorMessage error when calls.GetValueOrDefault(error.Call)?.Hold is RelayedHold hold && hold.HeldOn(from): Reply(error.Call, error); break;
            case GoneMessage gone when entries.GetValueOrDefault(gone.Hold) is RelayedHold hold && hold.HeldOn(from): Send(gone); hold.Lost(); break;
            case LoadingMessage loading when Relayed(loading.Hold): Send(loading); break;
            case FailedMessage failed when Relayed(failed.Hold): Send(failed); break;
        }
    }
    internal void UpstreamDropped(RelayedHold hold, ChordError error)
    {
        foreach (var (id, _) in calls.Where(x => ReferenceEquals(x.Value.Hold, hold)).ToArray()) Reply(id, new ErrorMessage(id, WireError.FromException(error)));
        Send(new LoadingMessage(hold.Id));
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
        foreach (var call in calls.Values) { call.Abort?.Cancel(); call.Abort?.Dispose(); RelayedHold.EndCall(call.Span, null); }
        calls.Clear(); var previous = entries.Values.ToArray(); entries.Clear();
        foreach (var hold in previous) hold.Drop();
        touched.Clear(); waiting.Clear(); bindings.Clear(); sent.Clear();
        if (relayOrigin is { } id)
        {
            link.Host.Relays.Remove(id); foreach (var upstream in relayLinks) upstream.Send(new ClosedMessage { Origin = id });
        }
        relayLinks.Clear();
    }
}
