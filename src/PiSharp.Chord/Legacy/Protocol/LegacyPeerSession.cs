// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/connection.ts, remote.ts.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Remote;

namespace PiSharp.Chord.Legacy;

public sealed record LegacyRoot(string RootId, int CodeVersion);
public sealed class LegacyRootLease(LegacyRoot root, CancellationToken cancellationToken, Action? release = null, Func<Exception>? cancellationError = null) : IDisposable
{
    private Action? close = release;
    public LegacyRoot Root { get; } = root;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public Func<Exception>? CancellationError { get; } = cancellationError;
    public void Dispose() => Interlocked.Exchange(ref close, null)?.Invoke();
}
public sealed record LegacyCallResult(bool HasValue, JsonNode? Value)
{
    public static LegacyCallResult Void { get; } = new(false, null);
    public static LegacyCallResult From(JsonNode? value) => new(true, value);
}
public interface ILegacyServedReference : IDisposable
{
    string? Variant { get; }
    string ContractFingerprint { get; }
    IReadOnlyList<string> Members { get; }
    IReadOnlyDictionary<string, JsonNode?> State { get; }
    event Action<LegacyFrame>? StateChanged;
    Task<LegacyCallResult> CallAsync(string member, bool hasArgs, JsonNode? args, LegacyRoot? root, CancellationToken cancellationToken);
}
public sealed record LegacyPeerSessionOptions
{
    public required string LocalName { get; init; }
    public Func<LegacyFrame, JsonNode?, CancellationToken, Task<ILegacyServedReference>>? Provider { get; init; }
    public Func<string, JsonNode?, Func<bool>, CancellationToken, Task<LegacyConnection>>? RelayResolver { get; init; }
    public int? MaxCalls { get; init; }
    public Action<Exception>? OnError { get; init; }
    public Func<LegacyRoot?, JsonNode?, CancellationToken, Task<LegacyRootLease>>? AdmitRoot { get; init; }
    public Action<LegacyRoot, LegacyConnection>? RootSent { get; init; }
    public Action<string>? RootEnded { get; init; }
}

/// <summary>Actual protocol4 holds, calls, cancellation, state hydration and opaque relays on a negotiated connection.</summary>
public sealed class LegacyPeerSession : IDisposable
{
    private static readonly ConditionalWeakTable<LegacyConnection, LegacyPeerSession> Sessions = new();
    private readonly LegacyPeerSessionOptions options;
    private readonly Dictionary<long, LegacyRemoteReference> consumers = [];
    private readonly Dictionary<long, Served> served = [];
    private readonly Dictionary<long, PendingCall> calls = [];
    private readonly Dictionary<long, CancellationTokenSource> incoming = [];
    private readonly Dictionary<long, long> incomingHolds = [];
    private readonly Dictionary<long, Relay> relayReplies = [];
    private readonly Dictionary<long, (Relay Relay, long OriginId)> relayCalls = [];
    private bool ended;
    private long generation;
    public LegacyPeerSession(LegacyConnection connection, LegacyPeerSessionOptions options)
    {
        Connection = connection; this.options = options;
        Sessions.Add(connection, this);
        connection.Closed += Closed;
        connection.Received += Receive;
    }
    public LegacyConnection Connection { get; }
    internal void Report(Exception error) => options.OnError?.Invoke(error);
    public static LegacyPeerSession? For(LegacyConnection connection) => Sessions.TryGetValue(connection, out var session) ? session : null;
    public LegacyRemoteReference Hold(LegacyContract contract, JsonNode? input, bool state = true, JsonNode? principal = null, string? origin = null, bool again = false)
    {
        var normalized = LegacySchema.Normalize(contract.Input, input, contract.Name);
        var at = contract.Placement?.Resolve(contract.Name, normalized) ?? throw ChordException.Invalid("placement", "local types cannot cross links");
        var id = Connection.NextId(); var reference = new LegacyRemoteReference(this, id, contract, state);
        Connection.Post(() =>
        {
            if (ended) { reference.Fail(new ChordException("unavailable", "link closed")); return; }
            consumers.Add(id, reference);
            var frame = new JsonObject { ["t"] = "hold", ["id"] = id, ["at"] = at, ["token"] = contract.Description, ["input"] = normalized?.DeepClone() };
            if (principal is not null) frame["principal"] = principal.DeepClone();
            if (origin is not null) frame["origin"] = origin;
            if (!state) frame["state"] = false;
            if (again) frame["again"] = true;
            Connection.Send(new(frame));
        });
        return reference;
    }
    internal void Release(long id) => Connection.Post(() =>
    {
        if (!consumers.Remove(id)) return;
        Connection.Send(LegacyProtocol.Frame("release", ("id", JsonValue.Create(id))));
    });
    internal void FailReference(long hold, Exception error) => Connection.Post(() =>
    {
        Release(hold);
        foreach (var pair in calls.Where(pair => pair.Value.Hold == hold).ToArray())
        {
            calls.Remove(pair.Key); pair.Value.Registration.Unregister(); pair.Value.Completion.TrySetException(error);
        }
    });
    internal void WantState(long id, bool wanted) => Connection.Send(LegacyProtocol.Frame(wanted ? "sub" : "unsub", ("hold", JsonValue.Create(id))));
    internal Task<LegacyCallResult> CallAsync(long hold, string member, bool hasArgs, JsonNode? args, LegacyRoot? root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pending = new PendingCall(hold);
        Connection.Post(() =>
        {
            if (ended) { pending.Completion.TrySetException(new ChordException("unavailable", "link closed")); return; }
            var id = Connection.NextId(); calls.Add(id, pending);
            pending.Registration = token.Register(() => Connection.Post(() =>
            {
                if (!calls.Remove(id, out var canceled)) return;
                Connection.Send(LegacyProtocol.Frame("cancel", ("id", JsonValue.Create(id))));
                canceled.Completion.TrySetException(new ChordException("aborted", "cancelled by caller"));
            }));
            if (!calls.ContainsKey(id)) { pending.Registration.Unregister(); return; }
            var frame = new JsonObject { ["t"] = "call", ["id"] = id, ["hold"] = hold, ["member"] = member };
            if (hasArgs) frame["args"] = args?.DeepClone();
            if (root is not null) frame["root"] = new JsonObject { ["rootId"] = root.RootId, ["codeVersion"] = root.CodeVersion };
            Connection.Send(new(frame));
        });
        return pending.Completion.Task;
    }
    private void Receive(LegacyFrame frame)
    {
        if (ended) return;
        switch (frame.Type)
        {
            case "hold": HoldIncoming(frame); break;
            case "release": Drop(frame.Number("id")); break;
            case "sub": case "unsub":
                if (served.TryGetValue(frame.Number("hold"), out var subscription))
                {
                    if (subscription.State == (frame.Type == "sub")) break;
                    subscription.State = frame.Type == "sub";
                    if (subscription.Relay is { } relay) relay.WantState(subscription.State);
                    else if (subscription.State) Unit(subscription, false);
                    else { subscription.ActiveStates.Clear(); Connection.DropHoldState(subscription.Id); }
                }
                break;
            case "resync":
                if (served.TryGetValue(frame.Number("hold"), out var resync))
                {
                    if (resync.Relay is { } relay) relay.Resync(frame.String("m")!);
                    else if (resync.ActiveStates.Contains(frame.String("m")!)) Connection.ResetLater(resync.Id, frame.String("m")!, () => Reset(resync, frame.String("m")!));
                }
                break;
            case "call": CallIncoming(frame); break;
            case "cancel":
                if (incoming.TryGetValue(frame.Number("id"), out var cancellation)) cancellation.Cancel();
                foreach (var hold in served.Values)
                {
                    hold.Relay?.Cancel(frame.Number("id"));
                    hold.PendingCalls.RemoveAll(call => call.Number("id") == frame.Number("id"));
                }
                break;
            case "shape": case "gone": case "state": Reply(frame); break;
            case "result": Result(frame); break;
            case "root_end": options.RootEnded?.Invoke(frame.String("rootId")!); break;
        }
    }
    private void HoldIncoming(LegacyFrame frame)
    {
        var id = frame.Number("id");
        if (served.ContainsKey(id)) { Connection.Violation("duplicate hold id " + id); return; }
        var owner = new Served(id, !frame.Is("state", false), frame.Is("again", true)) { Principal = Connection.PrincipalOf(frame) }; served.Add(id, owner);
        _ = OpenAsync(owner, frame);
    }
    private async Task OpenAsync(Served owner, LegacyFrame frame)
    {
        try
        {
            var contract = LegacyContract.FromDescription(frame["token"]!.AsObject());
            if (contract.Placement?.Resolve(contract.Name, frame["input"]) != frame.String("at")) throw new ChordException("forbidden", "hold target disagrees with placement");
            var principal = Connection.PrincipalOf(frame);
            if (frame.String("at") != options.LocalName)
            {
                if (Connection.Role == "upstream" || options.RelayResolver is null) throw new ChordException("unavailable", options.LocalName + " does not relay");
                var epoch = generation;
                var down = await options.RelayResolver(frame.String("at")!, principal, () => !ended && !owner.Ended && epoch == generation, CancellationToken.None).ConfigureAwait(false);
                var target = For(down) ?? new LegacyPeerSession(down, options);
                Connection.Dispatch(() =>
                {
                    if (owner.Ended || ended || epoch != generation) return;
                    owner.Relay = new Relay(this, target, owner, frame, principal, Connection.OriginOf(frame)); owner.Relay.Open();
                    var queued = owner.PendingCalls.ToArray(); owner.PendingCalls.Clear(); foreach (var call in queued) CallIncoming(call);
                });
                return;
            }
            if (options.Provider is null) throw new ChordException("unavailable", "no provider", "unknown_type");
            var reference = await options.Provider(frame, principal, owner.Stop.Token).ConfigureAwait(false);
            Connection.Dispatch(() =>
            {
                if (owner.Ended || ended) { reference.Dispose(); return; }
                owner.Reference = reference;
                // A received commit can be waiting on host publication here. Preserve FIFO without making
                // that publication synchronously wait for the same connection dispatcher.
                owner.Listener = changed => Connection.Post(() => Changed(owner, changed));
                reference.StateChanged += owner.Listener;
                Unit(owner, !owner.Again);
                var queued = owner.PendingCalls.ToArray(); owner.PendingCalls.Clear(); foreach (var call in queued) CallIncoming(call);
            });
        }
        catch (Exception error)
        {
            Connection.Dispatch(() =>
            {
                if (owner.Ended || ended) return;
                Connection.Send(LegacyProtocol.Frame("gone", ("id", JsonValue.Create(owner.Id)), ("error", Error(error)))); Drop(owner.Id);
            });
        }
    }
    private void Unit(Served owner, bool urgent)
    {
        if (owner.Reference is null || owner.Ended) return;
        Connection.QueueUnit(owner.Id, () =>
        {
            if (owner.Reference is not { } reference || owner.Ended) return;
            HashSet<string> current = owner.State ? reference.State.Keys.ToHashSet(StringComparer.Ordinal) : [];
            var states = current.Where(name => !owner.ActiveStates.Contains(name)).ToArray();
            foreach (var removed in owner.ActiveStates.Where(name => !current.Contains(name))) Connection.DropState(owner.Id, removed);
            owner.ActiveStates = current;
            var frame = new JsonObject { ["t"] = "shape", ["id"] = owner.Id, ["members"] = new JsonArray(reference.Members.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["contract"] = reference.ContractFingerprint, ["state"] = new JsonArray(states.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
            if (reference.Variant is not null) frame["variant"] = reference.Variant;
            Connection.Send(new(frame));
            foreach (var member in states) Snapshot(owner, member, "snap");
        }, urgent);
    }
    private void Snapshot(Served owner, string member, string mode)
    {
        if (owner.Reference?.State.TryGetValue(member, out var value) != true || owner.Ended) return;
        Connection.Send(LegacyProtocol.Frame("state", ("hold", JsonValue.Create(owner.Id)), ("m", JsonValue.Create(member)),
            ("as", JsonValue.Create(mode)), ("value", value)));
    }
    private void Reset(Served owner, string member) => Snapshot(owner, member, "reset");
    private void Changed(Served owner, LegacyFrame frame)
    {
        if (owner.Ended) return;
        if (frame.Type == "shape") { Unit(owner, false); return; }
        if (!owner.State) return;
        var member = frame.String("m")!;
        if (!owner.ActiveStates.Contains(member)) return;
        Connection.SendState(frame.With("hold", JsonValue.Create(owner.Id)), () => Reset(owner, member));
    }
    private void Drop(long id)
    {
        if (!served.Remove(id, out var owner)) return;
        owner.Ended = true; owner.Stop.Cancel(); owner.Stop.Dispose(); Connection.DropHoldState(id);
        owner.Relay?.Close();
        if (owner.Reference is { } reference) { reference.StateChanged -= owner.Listener; reference.Dispose(); }
    }
    private void CallIncoming(LegacyFrame frame)
    {
        var id = frame.Number("id"); var hold = frame.Number("hold");
        if (incoming.ContainsKey(id) || served.Values.Any(x => x.Relay?.HasCall(id) == true || x.PendingCalls.Any(call => call.Number("id") == id)))
        { Connection.Violation("duplicate call id " + id); return; }
        var active = incoming.Count + served.Values.Sum(value => value.PendingCalls.Count);
        if (options.MaxCalls is { } limit && active >= limit)
        { Connection.Send(LegacyProtocol.Frame("result", ("id", JsonValue.Create(id)), ("error", Error(new ChordException("unavailable", $"Too many calls in progress on this connection ({limit}); wait for earlier ones to finish", "too_many_calls"))))); return; }
        if (!served.TryGetValue(hold, out var owner))
        { Connection.Send(LegacyProtocol.Frame("result", ("id", JsonValue.Create(id)), ("error", Error(new ChordException("gone", "unknown hold"))))); return; }
        if (owner.Reference is null && owner.Relay is null)
        { owner.PendingCalls.Add(frame); return; }
        var stop = new CancellationTokenSource(); incoming.Add(id, stop); incomingHolds.Add(id, hold);
        if (owner.Relay is { } relay) _ = RunRelayCallAsync(id, owner, relay, frame, stop);
        else _ = RunCallAsync(id, owner, frame, stop);
    }
    private LegacyRoot? ReceivedRoot(LegacyFrame frame) => frame["root"] is JsonObject r && Connection.PeerKind == "host"
        ? new LegacyRoot(r["rootId"]!.GetValue<string>(), r["codeVersion"]!.GetValue<int>()) : null;
    private static Exception CallError(Exception error, CancellationTokenSource stop, LegacyRootLease? lease)
    {
        if (stop.IsCancellationRequested) return new ChordException("aborted", "cancelled by caller");
        if (lease?.CancellationToken.IsCancellationRequested == true)
            return lease.CancellationError?.Invoke() ?? new ChordException("aborted", "root cancelled");
        return error is OperationCanceledException ? new ChordException("aborted", "call cancelled", cause: error) : error;
    }
    private async Task RunCallAsync(long id, Served owner, LegacyFrame frame, CancellationTokenSource stop)
    {
        LegacyRootLease? lease = null;
        try
        {
            var root = ReceivedRoot(frame);
            if (options.AdmitRoot is { } admit) lease = await admit(root, owner.Principal, stop.Token).ConfigureAwait(false);
            using var combined = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stop.Token, lease.CancellationToken);
            var result = await owner.Reference!.CallAsync(frame.String("member")!, frame.Has("args"), frame.UnpackPayload(), lease?.Root ?? root, combined?.Token ?? stop.Token).ConfigureAwait(false);
            Connection.Dispatch(() =>
            {
                var response = new JsonObject { ["t"] = "result", ["id"] = id };
                if (result.HasValue) response["value"] = result.Value?.DeepClone();
                if (!ended) Connection.Send(stop.IsCancellationRequested || lease?.CancellationToken.IsCancellationRequested == true
                    ? LegacyProtocol.Frame("result", ("id", JsonValue.Create(id)), ("error", Error(CallError(new OperationCanceledException(), stop, lease)))) : new(response));
            });
        }
        catch (Exception error)
        {
            Connection.Dispatch(() => { if (!ended) Connection.Send(LegacyProtocol.Frame("result", ("id", JsonValue.Create(id)), ("error", Error(CallError(error, stop, lease))))); });
        }
        finally { lease?.Dispose(); Connection.Post(() => { incoming.Remove(id); incomingHolds.Remove(id); stop.Dispose(); }); }
    }
    private async Task RunRelayCallAsync(long id, Served owner, Relay relay, LegacyFrame frame, CancellationTokenSource stop)
    {
        LegacyRootLease? lease = null;
        try
        {
            var root = ReceivedRoot(frame);
            if (options.AdmitRoot is { } admit) lease = await admit(root, owner.Principal, stop.Token).ConfigureAwait(false);
            using var combined = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stop.Token, lease.CancellationToken);
            var forwarded = frame.Without("root");
            if ((lease?.Root ?? root) is { } carried)
            {
                forwarded = forwarded.With("root", new JsonObject { ["rootId"] = carried.RootId, ["codeVersion"] = carried.CodeVersion });
                options.RootSent?.Invoke(carried, relay.DownConnection);
            }
            await relay.CallAsync(forwarded, combined?.Token ?? stop.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Connection.Post(() => { if (!ended) Connection.Send(LegacyProtocol.Frame("result", ("id", JsonValue.Create(id)),
                ("error", Error(CallError(error, stop, lease))))); });
        }
        finally { lease?.Dispose(); Connection.Post(() => { incoming.Remove(id); incomingHolds.Remove(id); stop.Dispose(); }); }
    }
    private void Reply(LegacyFrame frame)
    {
        var id = frame.Number(frame.Type == "state" ? "hold" : "id");
        if (relayReplies.TryGetValue(id, out var relay)) { relay.Reply(frame); return; }
        if (consumers.TryGetValue(id, out var reference)) reference.Receive(frame);
    }
    private void Result(LegacyFrame frame)
    {
        var id = frame.Number("id");
        if (relayCalls.Remove(id, out var relayed)) { relayed.Relay.Result(relayed.OriginId, frame); return; }
        if (!calls.Remove(id, out var pending)) return;
        pending.Registration.Unregister();
        if (frame["error"] is JsonObject error) pending.Completion.TrySetException(FromError(error));
        else pending.Completion.TrySetResult(new(frame.Has("value"), frame.UnpackPayload()));
    }
    public void Revoke()
    {
        Connection.Post(() => { generation++; RevokeMatching(_ => true); });
    }
    public void Revoke(Func<JsonNode?, bool> matches) => Connection.Post(() => RevokeMatching(matches));
    private void RevokeMatching(Func<JsonNode?, bool> matches)
    {
        foreach (var owner in served.Values.Where(value => matches(value.Principal)).ToArray())
        {
            foreach (var call in incomingHolds.Where(pair => pair.Value == owner.Id).ToArray())
                if (incoming.TryGetValue(call.Key, out var cancellation)) cancellation.Cancel();
            Connection.Send(LegacyProtocol.Frame("gone", ("id", JsonValue.Create(owner.Id)), ("error", Error(new ChordException("gone", "principal invalidated")))));
            Drop(owner.Id);
        }
    }
    private void Closed(string reason)
    {
        if (ended) return; ended = true;
        var error = new ChordException("unavailable", "link closed: " + reason);
        foreach (var reference in consumers.Values.ToArray()) reference.Fail(error);
        consumers.Clear(); foreach (var id in served.Keys.ToArray()) Drop(id);
        foreach (var call in calls.Values) { call.Registration.Unregister(); call.Completion.TrySetException(error); } calls.Clear();
        foreach (var cancellation in incoming.Values) cancellation.Cancel();
        foreach (var relay in relayReplies.Values.ToArray()) relay.DownClosed(error); relayReplies.Clear(); relayCalls.Clear();
    }
    public void Dispose()
    {
        Connection.Received -= Receive; Connection.Closed -= Closed;
        Connection.Post(() => Closed("session disposed")); Sessions.Remove(Connection);
    }
    internal static JsonObject Error(Exception error)
    {
        if (error is not ChordException chord) return new JsonObject { ["code"] = "unavailable", ["message"] = error.Message };
        var result = new JsonObject { ["code"] = chord.Code, ["message"] = chord.Message };
        if (chord.Reason is not null) result["reason"] = chord.Reason; return result;
    }
    internal static ChordException FromError(JsonObject error) => new(error["code"]!.GetValue<string>(),
        Regex.Replace(error["message"]!.GetValue<string>(), @"^[a-z_]+(/[a-z_]+)?: ", ""), error["reason"]?.GetValue<string>());
    private sealed class PendingCall(long hold)
    {
        internal long Hold { get; } = hold;
        internal TaskCompletionSource<LegacyCallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
    }
    private sealed class Served(long id, bool state, bool again)
    {
        internal long Id { get; } = id;
        internal bool State = state, Ended;
        internal bool Again { get; } = again;
        internal CancellationTokenSource Stop { get; } = new();
        internal ILegacyServedReference? Reference;
        internal JsonNode? Principal;
        internal Action<LegacyFrame>? Listener;
        internal List<LegacyFrame> PendingCalls { get; } = [];
        internal HashSet<string> ActiveStates = new(StringComparer.Ordinal);
        internal Relay? Relay;
    }
    private sealed class Relay(LegacyPeerSession origin, LegacyPeerSession down, Served owner, LegacyFrame hold, JsonNode? principal, string? originId)
    {
        private long id;
        private readonly Dictionary<long, RelayPending> calls = [];
        private readonly HashSet<string> awaiting = [];
        private HashSet<string>? urgent;
        private volatile bool closed;
        private bool shaped;
        internal void Open()
        {
            id = down.Connection.NextId();
            var frame = hold.With("id", JsonValue.Create(id)).With("principal", principal).Without("origin");
            if (originId is not null) frame = frame.With("origin", JsonValue.Create(originId));
            down.Connection.Post(() => { if (!closed) { down.relayReplies.Add(id, this); down.Connection.Send(frame); } });
        }
        internal bool HasCall(long call) => calls.ContainsKey(call);
        internal int CallCount => calls.Count;
        internal LegacyConnection DownConnection => down.Connection;
        internal void WantState(bool state)
        {
            if (!state) { origin.Connection.DropHoldState(owner.Id); awaiting.Clear(); }
            down.Connection.Send(LegacyProtocol.Frame(state ? "sub" : "unsub", ("hold", JsonValue.Create(id))));
        }
        internal void Resync(string member) { awaiting.Add(member); down.Connection.Send(LegacyProtocol.Frame("resync", ("hold", JsonValue.Create(id)), ("m", JsonValue.Create(member)))); }
        internal void Reply(LegacyFrame frame)
            => origin.Connection.Post(() => ReplyOnOrigin(frame));
        private void ReplyOnOrigin(LegacyFrame frame)
        {
            if (closed) return;
            if (frame.Type == "gone") { origin.Connection.Send(frame.With("id", JsonValue.Create(owner.Id))); origin.Drop(owner.Id); return; }
            if (frame.Type == "shape")
            {
                if (!shaped) { shaped = true; if (!owner.Again) urgent = frame["state"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal); }
                origin.Connection.Send(frame.With("id", JsonValue.Create(owner.Id))); return;
            }
            var member = frame.String("m")!;
            if (awaiting.Contains(member)) { if (frame.String("as") == "ops") return; awaiting.Remove(member); }
            var response = frame.With("hold", JsonValue.Create(owner.Id));
            if (urgent?.Remove(member) == true) origin.Connection.Send(response);
            else origin.Connection.SendState(response, () => Resync(member), () => Resync(member));
        }
        internal Task CallAsync(LegacyFrame frame, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            origin.Connection.Post(() =>
            {
                if (closed) { completion.TrySetException(new ChordException("gone", "relayed hold closed")); return; }
                var next = down.Connection.NextId(); var pending = new RelayPending(next, completion); calls.Add(frame.Number("id"), pending);
                pending.Registration = cancellationToken.Register(() => origin.Connection.Post(() => Cancel(frame.Number("id"))));
                if (!calls.ContainsKey(frame.Number("id"))) { pending.Registration.Unregister(); return; }
                var forwarded = frame.With("id", JsonValue.Create(next)).With("hold", JsonValue.Create(id));
                down.Connection.Post(() => { if (!closed) { down.relayCalls.Add(next, (this, frame.Number("id"))); down.Connection.Send(forwarded); } });
            });
            return completion.Task;
        }
        internal void Cancel(long call)
        {
            if (!calls.Remove(call, out var pending)) return;
            pending.Registration.Unregister(); pending.Completion.TrySetException(new ChordException("aborted", "relayed call cancelled"));
            down.Connection.Post(() => { down.relayCalls.Remove(pending.DownId); down.Connection.Send(LegacyProtocol.Frame("cancel", ("id", JsonValue.Create(pending.DownId)))); });
        }
        internal void Result(long call, LegacyFrame frame) => origin.Connection.Post(() =>
        {
            if (!calls.Remove(call, out var pending)) return;
            pending.Registration.Unregister(); if (!closed) origin.Connection.Send(frame.With("id", JsonValue.Create(call)));
            pending.Completion.TrySetResult();
        });
        internal void Close()
        {
            if (closed) return; closed = true;
            foreach (var call in calls.Keys.ToArray()) Cancel(call);
            down.Connection.Post(() => { down.relayReplies.Remove(id); down.Connection.Send(LegacyProtocol.Frame("release", ("id", JsonValue.Create(id)))); });
        }
        internal void DownClosed(Exception error) => origin.Connection.Post(() => { if (closed) return; origin.Connection.Send(LegacyProtocol.Frame("gone", ("id", JsonValue.Create(owner.Id)), ("error", Error(error)))); origin.Drop(owner.Id); });
        private sealed class RelayPending(long downId, TaskCompletionSource completion)
        {
            internal long DownId { get; } = downId;
            internal TaskCompletionSource Completion { get; } = completion;
            internal CancellationTokenRegistration Registration;
        }
    }
}

/// <summary>A protocol4 reference, hydrated atomically from a shape and its promised first state snapshots.</summary>
public sealed class LegacyRemoteReference : IDisposable
{
    private readonly LegacyPeerSession session;
    private readonly LegacyContract contract;
    private readonly Dictionary<string, (JsonNode? Value, WireDeltaDecoder Decoder)> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<JsonNode?>>> listeners = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LegacyFrame? pendingShape;
    private HashSet<string> missing = [];
    private readonly Dictionary<string, (JsonNode? Value, WireDeltaDecoder Decoder)> pendingValues = new(StringComparer.Ordinal);
    private volatile Exception? failure;
    private IReadOnlyDictionary<string, JsonNode?> snapshot = new Dictionary<string, JsonNode?>();
    private volatile bool released, wantsState;
    internal LegacyRemoteReference(LegacyPeerSession session, long id, LegacyContract contract, bool state)
    { this.session = session; Id = id; this.contract = contract; wantsState = state; }
    public long Id { get; }
    public Task Ready => ready.Task;
    public string? Variant { get; private set; }
    public IReadOnlyList<string> Members { get; private set; } = [];
    public event Action? ShapeChanged;
    public event Action<Exception>? Failed;
    public Exception? Error => failure;
    public JsonNode? Read(string member)
    {
        if (released) throw new ChordException("released", "reference released");
        if (failure is not null) throw failure;
        if (!ready.Task.IsCompletedSuccessfully) throw new ChordException("not_ready", "reference has no first snapshot");
        if (!Members.Contains(member) || !contract.Variants.TryGetValue(Variant ?? "", out var members) ||
            !members.TryGetValue(member, out var definition) || definition is not LegacyStateMember)
            throw new ChordException("not_in_variant", member + " is not a state member of variant " + Variant);
        return Volatile.Read(ref snapshot).TryGetValue(member, out var state) ? state?.DeepClone() : throw new ChordException("not_ready", member + " is not hydrated");
    }
    public IDisposable Subscribe(string member, Action<JsonNode?> listener)
    {
        session.Connection.Post(() =>
        {
            if (released || failure is not null) return;
            if (!listeners.TryGetValue(member, out var list)) listeners.Add(member, list = []); list.Add(listener);
            if (values.TryGetValue(member, out var state)) listener(state.Value?.DeepClone());
        });
        return new Subscription(() => session.Connection.Post(() => { if (listeners.TryGetValue(member, out var list)) list.Remove(listener); }));
    }
    public void SetStateSubscription(bool enabled)
    {
        session.Connection.Post(() =>
        {
            if (wantsState == enabled || released) return;
            wantsState = enabled; session.WantState(Id, enabled);
            if (!enabled && pendingShape is not null) { missing.Clear(); ApplyShape(); }
        });
    }
    public async Task<JsonNode?> CallAsync(string member, JsonNode? args = null, LegacyRoot? root = null, CancellationToken cancellationToken = default)
    {
        try { await Ready.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException error) { throw new ChordException("aborted", "cancelled by caller", cause: error); }
        if (released) throw new ChordException("released", "reference released");
        if (failure is not null) throw failure;
        var key = Variant ?? "";
        if (!contract.Variants.TryGetValue(key, out var variant) || !variant.TryGetValue(member, out var definition) || definition is not LegacyMethodMember method)
            throw new ChordException("not_in_variant", member + " is not a method of variant " + Variant);
        var sent = method.Args is { } schema ? LegacySchema.Clean(schema, args) : null;
        if (method.Args is { } argsSchema) LegacySchema.Assert(argsSchema, sent, "args", member);
        var result = await session.CallAsync(Id, member, method.Args is not null, sent, root, cancellationToken).ConfigureAwait(false);
        if (method.Result is { } resultSchema) LegacySchema.Assert(resultSchema, result.Value, "result", member);
        return result.Value;
    }
    internal void Receive(LegacyFrame frame)
    {
        if (released || failure is not null) return;
        try
        {
            if (frame.Type == "gone") { Fail(LegacyPeerSession.FromError(frame["error"]!.AsObject())); return; }
            if (frame.Type == "shape")
            {
                var nextMissing = frame["state"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                if (pendingShape is not null) nextMissing.UnionWith(missing);
                var shapeMembers = frame["members"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                var shapeVariant = frame.String("variant") ?? "";
                HashSet<string> wanted = contract.Variants.TryGetValue(shapeVariant, out var members)
                    ? members.Where(pair => pair.Value is LegacyStateMember && shapeMembers.Contains(pair.Key)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal) : [];
                nextMissing.RemoveWhere(name => !wantsState || !wanted.Contains(name));
                pendingShape = frame; missing = nextMissing;
                if (missing.Count == 0) ApplyShape(); return;
            }
            var member = frame.String("m")!;
            if (frame.String("as") == "error")
            {
                var error = LegacyPeerSession.FromError(frame["error"]!.AsObject());
                if (!ready.Task.IsCompleted) Fail(error); else session.Report(error); return;
            }
            var target = pendingValues.TryGetValue(member, out var next) ? next : values.TryGetValue(member, out var current) ? current : default;
            if (frame.String("as") == "ops")
            {
                if (target.Decoder is null) return;
                var decoder = target.Decoder.Clone(); var ops = decoder.Decode(frame["ops"]);
                var changed = DeltaApply.Apply(target.Value?.DeepClone(), new JsonArray(ops.Select(op => (JsonNode?)op.ToJson()).ToArray()));
                if (pendingValues.ContainsKey(member)) pendingValues[member] = (changed, decoder);
                else { values[member] = (changed, decoder); PublishValues(); Deliver(member, changed); } return;
            }
            var value = frame.UnpackPayload();
            var variantName = pendingShape?.String("variant") ?? Variant ?? "";
            if (contract.Variants.TryGetValue(variantName, out var variant) && variant.TryGetValue(member, out var declaration) && declaration is LegacyStateMember state)
                LegacySchema.Assert(state.Schema, value, "state", member);
            if (pendingShape is not null && (missing.Contains(member) || pendingValues.ContainsKey(member)))
            { pendingValues[member] = (value, new()); missing.Remove(member); if (missing.Count == 0) ApplyShape(); return; }
            if (!Members.Contains(member)) return;
            values[member] = (value, new()); PublishValues(); Deliver(member, value);
        }
        catch (Exception error) { Fail(error is ChordException ? error : new ChordException("invalid", "bad state: " + error.Message, "protocol", cause: error)); }
    }
    private void ApplyShape()
    {
        var shape = pendingShape!; pendingShape = null;
        Variant = shape.String("variant"); Members = shape["members"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        foreach (var member in values.Keys.Where(x => !Members.Contains(x)).ToArray()) values.Remove(member);
        foreach (var pair in pendingValues) if (Members.Contains(pair.Key)) values[pair.Key] = pair.Value;
        PublishValues();
        foreach (var pair in pendingValues) if (Members.Contains(pair.Key)) Deliver(pair.Key, pair.Value.Value);
        pendingValues.Clear(); ready.TrySetResult();
        foreach (var callback in ShapeChanged?.GetInvocationList().Cast<Action>() ?? [])
            try { callback(); } catch (Exception error) { session.Report(error); }
    }
    private void Deliver(string member, JsonNode? value)
    {
        if (listeners.TryGetValue(member, out var list)) foreach (var listener in list.ToArray())
            try { listener(value?.DeepClone()); } catch (Exception error) { session.Report(error); }
    }
    private void PublishValues() => Volatile.Write(ref snapshot, values.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal));
    internal void Fail(Exception error)
    {
        if (failure is not null) return;
        failure = error;
        if (error is ChordException { Code: "released" }) session.Release(Id); else session.FailReference(Id, error);
        ready.TrySetException(error); pendingValues.Clear(); listeners.Clear();
        foreach (var callback in Failed?.GetInvocationList().Cast<Action<Exception>>() ?? [])
            try { callback(error); } catch (Exception reported) { session.Report(reported); }
        ShapeChanged = null; Failed = null;
    }
    public void Release() => Dispose();
    public void Dispose()
    {
        if (released) return; released = true;
        session.Connection.Post(() => { session.Release(Id); Fail(new ChordException("released", "reference released")); values.Clear(); PublishValues(); });
    }
    private sealed class Subscription(Action stop) : IDisposable { private Action? action = stop; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
}
