// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/consumer.ts, calls.ts.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Remote;

internal sealed class ConsumerSide(HostLink link)
{
    private sealed class Outgoing(long hold, long method, Part? args, IReadOnlyList<ReadOnlyMemory<byte>>? segments,
        IReadOnlyDictionary<string, string>? trace, bool old)
    {
        internal readonly long Hold = hold, Method = method;
        internal readonly Part? Args = args;
        internal readonly IReadOnlyList<ReadOnlyMemory<byte>>? Segments = segments;
        internal readonly IReadOnlyDictionary<string, string>? Trace = trace;
        internal readonly bool Old = old;
        internal readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long? Id;
        internal CancellationTokenRegistration Abort;
        internal void Resolve(object? value) { Abort.Dispose(); Completion.TrySetResult(value); }
        internal void Reject(Exception error) { Abort.Dispose(); Completion.TrySetException(error); }
    }
    private readonly Dictionary<long, RemoteObject> entries = [];
    private readonly Dictionary<long, Outgoing> calls = [];
    private readonly List<Outgoing> waiting = [];
    private long nextHold, nextCall, limit;
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
    internal Task<object?> CallAsync(long hold, long method, MethodData data, object? args, ChordContext context)
    {
        context.AbortSignal?.ThrowIfCancellationRequested();
        if (closed || !entries.ContainsKey(hold)) return Task.FromException<object?>(Unavailable());
        Part? part = null; IReadOnlyList<ReadOnlyMemory<byte>>? segments = null;
        if (!ContractsInternal.TakesNoArgs(data))
        {
            var payload = Parts.Payload(args, data.Args.Bytes); segments = payload.Segments;
            part = link.Role == LinkRole.Parent ? new Part.Text(payload.Json) : link.Part(payload.Json);
        }
        var call = new Outgoing(hold, method, part, segments, ChordTelemetry.CarrierOf(context.Telemetry), link.Host.OldWork.Of(context));
        if (context.AbortSignal is { } token)
            call.Abort = token.Register(() => link.Host.Dispatcher.Post(() =>
            {
                if (call.Completion.Task.IsCompleted) return;
                Cancel(call); call.Reject(new ChordError("aborted", "call aborted"));
            }));
        waiting.Add(call); Drain(); return call.Completion.Task;
    }
    internal void Opened(long callsLimit) { limit = callsLimit; Drain(); }
    private void Drain()
    {
        while (!closed && calls.Count < limit && waiting.Count != 0)
        {
            var call = waiting[0]; waiting.RemoveAt(0);
            if (call.Completion.Task.IsCompleted) continue;
            var id = nextCall++; call.Id = id; calls.Add(id, call);
            link.Send(new CallMessage(id, call.Hold, call.Method, call.Args, call.Segments, call.Trace, call.Old ? true : null));
        }
    }
    private void Cancel(Outgoing call)
    {
        if (call.Id is { } id && calls.Remove(id)) { link.Send(new AbortMessage(id)); Drain(); }
        else waiting.Remove(call);
    }
    internal void Release(long hold, ChordError error) { link.Send(new ReleaseMessage(hold)); Forget(hold, error); }
    internal void Retry(long hold, int hops) => link.Send(new RetryMessage(hold, hops));
    internal void Forget(long hold, ChordError error)
    {
        entries.Remove(hold);
        FailWaiting(hold, error);
        var failed = calls.Where(x => x.Value.Hold == hold).ToArray();
        foreach (var item in failed) { calls.Remove(item.Key); item.Value.Reject(error); }
        Drain();
    }
    private void FailWaiting(long hold, Exception error)
    {
        foreach (var call in waiting.Where(x => x.Hold == hold).ToArray()) { waiting.Remove(call); call.Reject(error); }
    }
    private void FailCalls(long hold, Exception error)
    {
        FailWaiting(hold, error);
        foreach (var (id, call) in calls.Where(x => x.Value.Hold == hold).ToArray())
        { calls.Remove(id); call.Reject(error); }
        Drain();
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
            Adopt(adopted); foreach (var holder in live) holder.WentLive(); foreach (var (holder, error) in oversized) holder.TooLarge(error);
            link.Send(new AckMessage());
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
        else if (message is LoadingMessage loading && entries.TryGetValue(loading.Hold, out var loadingHolder))
        {
            // Queued calls still carry indexes from the old variant. Only calls
            // already sent have a provider that can finish them across rebinding.
            FailWaiting(loading.Hold, new ChordError("unavailable", $"hold {loading.Hold}: rebinding"));
            loadingHolder.Loading();
        }
        else if (message is FailedMessage failed && entries.TryGetValue(failed.Hold, out var failedHolder))
        { FailCalls(failed.Hold, DataInternal.AsChordError(failed.Cause.Error.ToException(), "remote hold")); failedHolder.Failed(failed); }
        else if (message is GoneMessage gone && entries.TryGetValue(gone.Hold, out var goneHolder)) goneHolder.Lost(DataInternal.AsChordError(gone.Error.ToException(), "remote hold"));
        else if (message is ResultMessage result) await Reply(result.Call, result, null);
        else if (message is ErrorMessage error) await Reply(error.Call, null, error);
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
        try
        {
            if (error is not null) call.Reject(error.Error.ToException());
            else call.Resolve(result!.Value is null ? null : Parts.ParsePayload(link.OpenPart(result.Value, link.Limits), result.Segments));
        }
        catch (ChordError tooLarge) when (tooLarge.Code == "too_large") { call.Reject(tooLarge); }
        catch { call.Reject(new ChordError("unavailable", $"call {id}: a malformed reply")); throw; }
        finally { Drain(); }
    }
    internal void Close()
    {
        if (closed) return; closed = true;
        var error = Unavailable(); foreach (var call in calls.Values.Concat(waiting)) call.Reject(error);
        calls.Clear(); waiting.Clear(); entries.Clear();
    }
    private static ChordError Unavailable() => new("unavailable", "the link closed or is rebinding");
}
