// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/consumer.ts.
// The consumer side of a link: the holds this host's own remote references sent on it, by this side's ids, the calls through them within
// the peer's call window, and each bound and state message applied to their replicas as one batch, then acknowledged. Messages with an
// origin answer holds this host relays: they go to that origin's provider side; the calls this host relays for an origin wait here within
// the same window (one per origin). Each event message of its own holds is consumed (its values handed to the host's event flush, or
// deliberately dropped) and acknowledged with an event ack (decision "Events: fire-and-forget members").
//
// The event ack's turn. Upstream sends one event ack per inbound turn, in a microtask queued behind the event flush's, so the ack follows
// the delivery of what it consumed. Here the first event message consumed since the last event ack posts the ack to the host's dispatcher,
// behind the flush that message's entries posted (HostEvents.Push posts it first; the dispatcher is one FIFO), and the ack counts every
// event message consumed by the time it runs. The channel hands this side one message at a time and reads the next once the previous one
// was handled, so in practice each event message is a turn of its own and gets its own ack; the counts add up to the messages consumed, in
// order, each after its delivery, which is all the provider's event window relies on.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Remote;

internal sealed class ConsumerSide(HostLink link)
{
    private sealed class Outgoing(ConsumerSide side, long hold, long method, Part? args, IReadOnlyList<ReadOnlyMemory<byte>>? segments,
        IReadOnlyDictionary<string, string>? trace, bool old, object owner) : IQueuedCall
    {
        internal readonly long Hold = hold, Method = method;
        internal readonly Part? Args = args;
        internal readonly IReadOnlyList<ReadOnlyMemory<byte>>? Segments = segments;
        internal readonly IReadOnlyDictionary<string, string>? Trace = trace;
        internal readonly bool Old = old;
        // The reference that made it (never on the wire): releasing one of several references sharing the hold cancels its own calls only.
        internal readonly object Owner = owner;
        internal readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long? Id;
        internal CancellationTokenRegistration Abort;
        public void Send() => side.Send(this);
        internal void Resolve(object? value) { Abort.Dispose(); Completion.TrySetResult(value); }
        internal void Reject(Exception error) { Abort.Dispose(); Completion.TrySetException(error); }
    }
    private readonly Dictionary<long, RemoteObject> entries = [];
    private readonly Dictionary<long, Outgoing> calls = [];
    private readonly CallWindow<Outgoing> window = new();
    // The calls this host relays upstream on this link, one window per downstream origin's number here (a relay honours each upstream
    // window); made by an origin's first call.
    private readonly Dictionary<long, CallWindow<RelayedCall>> relayed = [];
    // The peer's hello: its call window (null: none), once it arrived.
    private bool greeted;
    private long? peerCalls;
    // The event messages consumed since the last event ack (one is posted behind the flush by the first).
    private long consumed;
    private long nextHold, nextCall;
    private bool closed;
    internal long Attach(RemoteObject holder, IReadOnlyDictionary<string, string>? trace = null)
    {
        var id = nextHold++; entries.Add(id, holder);
        var request = holder.Request with { Hold = id };
        request = request with { Chain = holder.ActivationChain, Trace = trace };
        if (link.Role == LinkRole.Route && holder.Caller.Principal is not null)
            request = request with { Principal = holder.Caller.Principal, Incarnation = holder.Caller.Incarnation };
        link.Send(request); return id;
    }
    /// <summary>Calls method <paramref name="method"/> (an index into the bound variant) on hold <paramref name="hold"/>. The caller's
    /// cancellation rejects it at once and sends <c>abort</c> when it is in flight. <paramref name="owner"/> is the reference making it.</summary>
    internal Task<object?> CallAsync(long hold, long method, MethodData data, object? args, object owner, ChordContext context)
    {
        context.AbortSignal?.ThrowIfCancellationRequested();
        if (closed || !entries.ContainsKey(hold)) return Task.FromException<object?>(Unavailable());
        Part? part = null; IReadOnlyList<ReadOnlyMemory<byte>>? segments = null;
        if (!ContractsInternal.TakesNoArgs(data))
        {
            var payload = Parts.Payload(args, data.Args.Bytes); segments = payload.Segments;
            part = link.Role == LinkRole.Parent ? new Part.Text(payload.Json) : link.Part(payload.Json);
        }
        var call = new Outgoing(this, hold, method, part, segments, ChordTelemetry.CarrierOf(context.Telemetry), link.Host.OldWork.Of(context), owner);
        if (context.AbortSignal is { } token)
            call.Abort = token.Register(() => link.Host.Dispatcher.Post(() =>
            {
                if (call.Completion.Task.IsCompleted) return;
                Cancel(call); call.Reject(new ChordError("aborted", "call aborted"));
            }));
        window.Submit(call); return call.Completion.Task;
    }
    /// <summary>The peer's <c>hello</c> arrived: calls go out within its window (null: none), relayed ones too.</summary>
    internal void Opened(long? callsLimit)
    {
        greeted = true; peerCalls = callsLimit; window.Opened(callsLimit);
        foreach (var each in relayed.Values.ToArray()) each.Opened(callsLimit);
    }
    /// <summary>The window of the calls this host relays on this link for the downstream origin with number <paramref name="origin"/>
    /// here: they wait in it while the peer's N calls of that origin are in flight (before its <c>hello</c>, all of them).</summary>
    internal CallWindow<RelayedCall> RelayWindow(long origin)
    {
        if (!relayed.TryGetValue(origin, out var found))
        {
            found = new(); if (greeted) found.Opened(peerCalls); relayed.Add(origin, found);
        }
        return found;
    }
    /// <summary>The downstream origin with number <paramref name="origin"/> here closed: its relayed calls' window goes.</summary>
    internal void Unrelay(long origin) => relayed.Remove(origin);
    // Sends a call that got its slot, with the next id.
    private void Send(Outgoing call)
    {
        var id = nextCall++; call.Id = id; calls.Add(id, call);
        link.Send(new CallMessage(id, call.Hold, call.Method, call.Args, call.Segments, call.Trace, call.Old ? true : null));
    }
    // A waiting call leaves the window and nothing is sent; one in flight sends abort and frees its slot.
    private void Cancel(Outgoing call)
    {
        if (call.Id is not { } id) { window.Remove(call); return; }
        if (!calls.Remove(id)) return;
        link.Send(new AbortMessage(id)); window.Freed();
    }
    internal void Release(long hold, ChordError error) { link.Send(new ReleaseMessage(hold)); Forget(hold, error); }
    /// <summary>
    /// One of several references sharing hold <paramref name="hold"/> released (<paramref name="owner"/>, of the object
    /// <paramref name="label"/>): its calls reject <c>released</c> and leave the tables (decision "References share one wire hold"). Its
    /// waiting calls leave the window unsent first, then each of its sent calls is aborted, the last sent first (a relay drops a later
    /// call still waiting in its upstream window before an earlier abort frees a slot for it); then their slots free.
    /// </summary>
    internal void CancelCalls(long hold, object owner, string label)
    {
        var waiting = window.RemoveWhere(call => call.Hold == hold && ReferenceEquals(call.Owner, owner));
        // Ids grow in the order calls were sent.
        var sent = calls.Where(entry => entry.Value.Hold == hold && ReferenceEquals(entry.Value.Owner, owner)).OrderByDescending(entry => entry.Key).ToArray();
        if (waiting.Count == 0 && sent.Length == 0) return;
        foreach (var (id, _) in sent) { calls.Remove(id); link.Send(new AbortMessage(id)); }
        foreach (var _ in sent) window.Freed();
        var error = new ChordError("released", $"{label}: released");
        foreach (var call in waiting) call.Reject(error);
        foreach (var (_, call) in sent) call.Reject(error);
    }
    internal void Retry(long hold, int hops) => link.Send(new RetryMessage(hold, hops));
    internal void Forget(long hold, ChordError error) { entries.Remove(hold); FailCalls(hold, error); }
    // Fails hold's calls in flight and waiting with error; each in-flight one frees its slot. Waiting ones first: a freed slot must not
    // send one of them.
    private void FailCalls(long hold, Exception error)
    {
        var failed = window.RemoveWhere(call => call.Hold == hold);
        foreach (var (id, call) in calls.Where(x => x.Value.Hold == hold).ToArray())
        { calls.Remove(id); window.Freed(); failed.Add(call); }
        foreach (var call in failed) call.Reject(error);
    }
    internal async Task MessageAsync(Message message)
    {
        if (message.Origin is { } origin)
        {
            if (link.Host.Relays.TryGetValue(origin, out var relay)) relay.Forward(message, link);
            return;
        }
        if (message is BoundMessage bound)
        {
            foreach (var entry in bound.Holds) if (entries.ContainsKey(entry.Hold))
                foreach (var member in entry.Members) await link.InflateOwned(member.Value, link.Limits);
            if (!link.Open) return;
            var adopted = new List<Action>(); var live = new List<RemoteObject>(); var oversized = new List<(RemoteObject, ChordError)>(); var seen = new HashSet<long>();
            foreach (var entry in bound.Holds)
            {
                if (!entries.TryGetValue(entry.Hold, out var holder)) continue;
                if (!seen.Add(entry.Hold)) throw new ArgumentException("hold repeated in bound");
                try { adopted.Add(holder.PrepareBound(entry, link)); live.Add(holder); }
                catch (ChordError error) when (error.Code == "too_large") { oversized.Add((holder, error)); }
            }
            Adopt(adopted);
            // Acknowledged before the references go live: the calls they send then (their parked ones) follow the ack, so the provider
            // decodes them by this binding's indexes, never by a rebinding's previous ones.
            link.Send(new AckMessage());
            foreach (var holder in live) holder.WentLive(); foreach (var (holder, error) in oversized) holder.TooLarge(error);
        }
        else if (message is StateMessage state)
        {
            foreach (var entry in state.Holds) if (entries.ContainsKey(entry.Hold))
                foreach (var member in entry.Members) await link.InflateOwned(member.Ops, link.Limits);
            if (!link.Open) return;
            var adopted = new List<Action>(); var oversized = new List<(RemoteObject, ChordError)>(); var seen = new HashSet<long>();
            foreach (var entry in state.Holds)
            {
                if (!entries.TryGetValue(entry.Hold, out var holder)) continue;
                if (!seen.Add(entry.Hold)) throw new ArgumentException("hold repeated in state");
                try { adopted.Add(holder.Prepare(entry, link)); }
                catch (ChordError error) when (error.Code == "too_large") { oversized.Add((holder, error)); }
            }
            Adopt(adopted); foreach (var (holder, error) in oversized) holder.TooLarge(error); link.Send(new AckMessage());
        }
        else if (message is EventMessage events)
        {
            // Only values something listens to are inflated: those of holds this side gave up, of a hold not live or of a member nobody
            // listens to are dropped unopened (an index outside the bound version throws here, a protocol error).
            foreach (var entry in events.Events)
                if (entries.TryGetValue(entry.Hold, out var holder) && holder.EventNode(entry.Member) is not null) await link.InflateOwned(entry.Value, link.Limits);
            if (!link.Open) return;
            foreach (var entry in events.Events)
                if (entries.TryGetValue(entry.Hold, out var holder)) holder.Event(entry.Member, entry.At, entry.Value, link);
            // Consumed: acknowledged behind the flush that delivers it (see the file comment).
            if (consumed++ == 0) link.Host.Dispatcher.Post(AckEvents);
        }
        else if (message is LoadingMessage loading && entries.TryGetValue(loading.Hold, out var loadingHolder))
        {
            // Queued calls still carry indexes from the old variant. Only calls
            // already sent have a provider that can finish them across rebinding.
            var error = new ChordError("unavailable", $"hold {loading.Hold}: rebinding");
            foreach (var call in window.RemoveWhere(call => call.Hold == loading.Hold)) call.Reject(error);
            loadingHolder.Loading();
        }
        else if (message is FailedMessage failed && entries.TryGetValue(failed.Hold, out var failedHolder))
        { FailCalls(failed.Hold, DataInternal.AsChordError(failed.Cause.Error.ToException(), "remote hold")); failedHolder.Failed(failed); }
        else if (message is GoneMessage gone && entries.TryGetValue(gone.Hold, out var goneHolder)) goneHolder.Lost(DataInternal.AsChordError(gone.Error.ToException(), "remote hold"));
        else if (message is ResultMessage result) await Reply(result.Call, result, null);
        else if (message is ErrorMessage error) await Reply(error.Call, null, error);
    }
    private void AckEvents()
    {
        var count = consumed; consumed = 0;
        if (count > 0 && !closed) link.Send(new AckMessage(count));
    }
    private void Adopt(List<Action> actions)
    {
        if (actions.Count != 0) link.Host.Batch.Commit(() => { foreach (var action in actions) action(); }, link.Context);
    }
    private async Task Reply(long id, ResultMessage? result, ErrorMessage? error)
    {
        if (!calls.TryGetValue(id, out var call)) return;
        if (result is not null) await link.InflateOwned(result.Value, link.Limits);
        if (!link.Open || !calls.Remove(id)) return;
        window.Freed();
        try
        {
            if (error is not null) call.Reject(error.Error.ToException());
            else call.Resolve(result!.Value is null ? null : Parts.ParsePayload(link.OpenPart(result.Value, link.Limits), result.Segments));
        }
        catch (ChordError tooLarge) when (tooLarge.Code == "too_large") { call.Reject(tooLarge); }
        catch { call.Reject(new ChordError("unavailable", $"call {id}: a malformed reply")); throw; }
    }
    internal void Close()
    {
        if (closed) return; closed = true;
        var error = Unavailable(); var all = calls.Values.Concat(window.Clear()).ToArray();
        // The relays' upstream drops answer their calls (ProviderSide.UpstreamDropped).
        calls.Clear(); entries.Clear(); relayed.Clear();
        foreach (var call in all) call.Reject(error);
    }
    private static ChordError Unavailable() => new("unavailable", "the link closed or is rebinding");
}
