// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/connection.ts.
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

public sealed record LegacyProtocolMismatch(int Local, int? Peer);

public sealed record LegacyConnectionOptions
{
    public string Name { get; init; } = "host";
    public string Kind { get; init; } = "host";
    public int CodeVersion { get; init; } = 1;
    public string Role { get; init; } = "downstream";
    public string PeerKind { get; init; } = "client";
    public string? PeerName { get; init; }
    public JsonNode? Principal { get; init; }
    public string? OriginId { get; init; }
    public double Window { get; init; } = 256 * 1024;
    public double? PendingCap { get; init; }
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Action<LegacyProtocolMismatch>? OnProtocolMismatch { get; init; }
    public Action<Exception>? OnError { get; init; }
    public Action<LegacyFrame>? OnOpened { get; init; }
    public Action<LegacyFrame>? OnFrame { get; init; }
    public Action<string>? OnClosed { get; init; }
}

/// <summary>Protocol4 negotiation, role enforcement, heartbeats and cumulative frame-count ACK with byte windows.</summary>
public sealed class LegacyConnection : IDisposable
{
    private readonly LegacyDispatcher dispatcher = new();
    private readonly LegacyConnectionOptions options;
    private readonly Queue<long> unacked = new();
    private readonly Dictionary<string, Slot> waiting = new(StringComparer.Ordinal);
    private readonly Queue<LegacyFrame> backlog = new();
    private Action<LegacyFrame>? received, opened;
    private Action<string>? closed;
    private LegacyFrame? hello;
    private JsonNode? principal;
    private string? closeReason;
    private bool? peerAcks;
    private bool mismatched, ackScheduled;
    private long sent, acked, receivedCount, unackedBytes, pendingBytes, nextId = 1, ping;
    private DateTimeOffset lastSeen;
    private ITimer? pinger, ackTimer;
    public LegacyConnection(ILegacyLink link, LegacyConnectionOptions options)
    {
        if (options.Role is not ("upstream" or "downstream") || options.Kind is not ("client" or "host") || options.PeerKind is not ("client" or "host"))
            throw new ArgumentException("Invalid legacy connection role or kind", nameof(options));
        ArgumentException.ThrowIfNullOrEmpty(options.Name);
        if (options.Window <= 0 || double.IsNaN(options.Window) || options.PendingCap is < 0 || options.PingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        Link = link; this.options = options;
        principal = options.Principal?.DeepClone();
        PeerName = options.PeerName; PeerKind = options.PeerKind;
        received = options.OnFrame; opened = options.OnOpened; closed = options.OnClosed;
        lastSeen = options.TimeProvider.GetUtcNow();
        link.OnClose(reason => dispatcher.Post(() => End(reason), options.OnError));
        // Send our hello before installing the receiver: queued peer frames can synchronously send ACKs.
        Send(LegacyProtocol.Frame("hello", ("protocol", JsonValue.Create(4)), ("name", JsonValue.Create(options.Name)),
            ("kind", JsonValue.Create(options.Kind)), ("codeVersion", JsonValue.Create(options.CodeVersion)), ("acks", JsonValue.Create(true))));
        link.Receive(frame => dispatcher.Post(() => Receive(frame), options.OnError));
        if (!link.Closed) pinger = options.TimeProvider.CreateTimer(_ => Post(KeepAlive), null, options.PingInterval, options.PingInterval);
    }
    public ILegacyLink Link { get; }
    public bool Open => !Link.Closed;
    public string Role => options.Role;
    public string PeerKind { get; private set; }
    public string? PeerName { get; private set; }
    public string? Name => PeerName;
    public long? PeerCodeVersion { get; private set; }
    public JsonNode? Principal => Volatile.Read(ref principal)?.DeepClone();
    public void Restamp(JsonNode? value) => Post(() => Volatile.Write(ref principal, value?.DeepClone()));
    public string? OriginId => options.OriginId;
    public (int Slots, long Bytes) Waiting => dispatcher.Invoke(() => (waiting.Count, pendingBytes));
    public long UnacknowledgedBytes => dispatcher.Invoke(() => unackedBytes);
    public event Action<LegacyFrame> Received
    {
        add => Post(() => { received += value; while (backlog.Count > 0) value(backlog.Dequeue()); });
        remove => Post(() => received -= value);
    }
    public event Action<LegacyFrame> Opened
    {
        add => Post(() => { opened += value; if (hello is not null) value(hello); });
        remove => Post(() => opened -= value);
    }
    public event Action<string> Closed
    {
        add => Post(() => { if (closeReason is { } reason) value(reason); else closed += value; });
        remove => Post(() => closed -= value);
    }
    public long NextId() => Interlocked.Increment(ref nextId) - 1;
    internal void Dispatch(Action action) => dispatcher.Invoke(action);
    internal T Dispatch<T>(Func<T> action) => dispatcher.Invoke(action);
    internal void Post(Action action) => dispatcher.Post(action, options.OnError);
    public JsonNode? PrincipalOf(LegacyFrame frame) => Role == "downstream" ? Principal : frame.Has("principal") ? frame["principal"] ?? Principal : Principal;
    public string? OriginOf(LegacyFrame frame) => Role == "downstream" ? OriginId : frame.String("origin");
    public void Send(LegacyFrame frame) => Post(() => SendNow(frame));
    private void SendNow(LegacyFrame frame)
    {
        if (!Open) return;
        var size = Link.Send(frame) ?? 0;
        if (peerAcks == false || frame.Type == "ack") return;
        sent++; unacked.Enqueue(size); unackedBytes += size;
    }
    private bool Full => peerAcks == true && unackedBytes >= options.Window;

    /// <summary>State waits behind a full window. Set resync on relays; otherwise resend builds a current reset at flush.</summary>
    public void SendState(LegacyFrame frame, Action resend, Action? resync = null) => Post(() =>
    {
        if (frame.Type != "state") throw new ArgumentException("Expected state frame", nameof(frame));
        var key = StateKey(frame.Number("hold"), frame.String("m")!);
        waiting.TryGetValue(key, out var slot);
        if (slot is null && !Full) { SendNow(frame); return; }
        if (slot is null)
        {
            slot = new Slot(frame.Number("hold"), frame.String("m")!, resend, resync);
            waiting.Add(key, slot);
        }
        if (frame.String("as") != "ops")
        {
            pendingBytes -= slot.Size; slot.Base = frame; slot.Ops.Clear(); slot.Size = 0; slot.Lazy = false; return;
        }
        if (slot.Lazy) return;
        var ops = frame["ops"] as JsonArray;
        var size = ChordJson.Stringify(ops).Length;
        var cap = options.PendingCap ?? options.Window * 4;
        if (pendingBytes + size > cap)
        {
            foreach (var old in waiting.Values.ToArray())
            {
                if (pendingBytes + size <= cap) break;
                if (old.Member is not null && !ReferenceEquals(old, slot) && old.Size > 0) Drop(old);
            }
            if (pendingBytes + size > cap) Drop(slot);
        }
        if (slot.Lazy || !waiting.ContainsKey(key)) return;
        if (ops is not null) foreach (var op in ops) slot.Ops.Add(op?.DeepClone());
        slot.Size += size; pendingBytes += size;
    });
    public void QueueUnit(long hold, Action flush, bool urgent = false) => Post(() =>
    {
        if (urgent) { waiting.Remove("u:" + hold); flush(); return; }
        waiting.TryAdd("u:" + hold, new Slot(hold, flush)); Flush();
    });
    public void ResetLater(long hold, string member, Action resend) => Post(() =>
    {
        var key = StateKey(hold, member);
        if (!waiting.TryGetValue(key, out var slot)) waiting.Add(key, slot = new Slot(hold, member, resend, null));
        pendingBytes -= slot.Size; slot.Size = 0; slot.Base = null; slot.Ops.Clear(); slot.Lazy = true; Flush();
    });
    public void DropState(long hold, string member) => Post(() =>
    {
        if (waiting.Remove(StateKey(hold, member), out var slot)) pendingBytes -= slot.Size;
    });
    public void DropHoldState(long hold) => Post(() =>
    {
        foreach (var pair in waiting.Where(pair => pair.Value.Hold == hold).ToArray())
        { pendingBytes -= pair.Value.Size; waiting.Remove(pair.Key); }
    });
    private static string StateKey(long hold, string member) => $"s:{hold}:{member}";
    private void Drop(Slot slot)
    {
        pendingBytes -= slot.Size; slot.Size = 0; slot.Base = null; slot.Ops.Clear();
        if (slot.Resync is null) { slot.Lazy = true; return; }
        waiting.Remove(StateKey(slot.Hold, slot.Member!)); slot.Resync();
    }
    private void Flush()
    {
        while (waiting.Count > 0 && !Full && Open)
        {
            var (key, slot) = waiting.First(); waiting.Remove(key); pendingBytes -= slot.Size;
            if (slot.Member is null || slot.Lazy) { slot.Flush(); continue; }
            if (slot.Base is not null) SendNow(slot.Base);
            if (slot.Ops.Count > 0) SendNow(LegacyProtocol.Frame("state", ("hold", JsonValue.Create(slot.Hold)),
                ("m", JsonValue.Create(slot.Member)), ("as", JsonValue.Create("ops")), ("ops", slot.Ops)));
        }
    }
    private void Receive(LegacyFrame frame)
    {
        if (mismatched || !Open) return;
        lastSeen = options.TimeProvider.GetUtcNow();
        if (!LegacyProtocol.IsFrame(frame)) { Violation("malformed frame"); return; }
        if (frame.Type == "ack")
        {
            var n = frame.Number("n");
            if (peerAcks != true || n < acked || n > sent) { Violation($"ack {n} outside {acked}..{sent}"); return; }
            for (var i = acked; i < n; i++) unackedBytes -= unacked.Dequeue();
            acked = n; Flush(); return;
        }
        receivedCount++;
        try
        {
            if (frame.Type == "hello") Hello(frame);
            else if (frame.Type == "ping") SendNow(LegacyProtocol.Frame("pong", ("n", frame["n"])));
            else if (frame.Type != "pong")
            {
                if (frame.Type is "invalidate" or "prepare" or "commit" or "abort" or "retire" && Role != "upstream")
                { Violation(frame.Type + " from a child"); return; }
                if (frame.Type == "vote" && (Role != "downstream" || PeerKind != "host"))
                { Violation("vote from a non-participant"); return; }
                if (received is null) backlog.Enqueue(frame); else received(frame);
            }
        }
        catch (Exception error) { options.OnError?.Invoke(error); }
        ScheduleAck();
    }
    private void Hello(LegacyFrame frame)
    {
        var version = frame.Has("protocol") ? (int)frame.Number("protocol") : 1;
        if (version != 4) { Mismatch(version); Link.Close("protocol-version 4"); return; }
        if (peerAcks is null)
        {
            peerAcks = frame.Is("acks", true);
            if (peerAcks == false) { unacked.Clear(); unackedBytes = 0; }
        }
        if (Role == "upstream")
        {
            if (frame.String("kind") != "host") { Violation("parent is not a host"); return; }
            PeerName ??= frame.String("name"); PeerKind = "host";
        }
        else if (frame.String("kind") != PeerKind || PeerName is not null && frame.String("name") != PeerName)
        { Violation($"hello {frame.String("kind")} {frame.String("name")} does not match the accepted {PeerKind} {PeerName ?? ""}"); return; }
        PeerCodeVersion = frame.Number("codeVersion"); hello = frame; opened?.Invoke(frame);
    }
    private void ScheduleAck()
    {
        if (peerAcks != true || ackScheduled || !Open) return;
        ackScheduled = true;
        ackTimer = options.TimeProvider.CreateTimer(_ => Post(() =>
        {
            ackScheduled = false; ackTimer?.Dispose(); ackTimer = null;
            if (Open) Link.Send(LegacyProtocol.Frame("ack", ("n", JsonValue.Create(receivedCount))));
        }), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
    private void KeepAlive()
    {
        if (options.TimeProvider.GetUtcNow() - lastSeen > options.PingTimeout) Link.Close("ping timeout");
        else SendNow(LegacyProtocol.Frame("ping", ("n", JsonValue.Create(++ping))));
    }
    public void Violation(string reason)
    {
        options.OnError?.Invoke(new ChordException("invalid", $"link to {Name ?? "peer"}: {reason}", "protocol"));
        Link.Close("protocol error: " + reason);
    }
    private void Mismatch(int? peer)
    {
        if (mismatched) return;
        mismatched = true; options.OnProtocolMismatch?.Invoke(new(4, peer));
    }
    private void End(string reason)
    {
        if (closeReason is not null) return;
        closeReason = reason;
        if (reason.StartsWith("protocol-version ", StringComparison.Ordinal) && reason.Length > 17 &&
            reason[17..].All(char.IsAsciiDigit) && int.TryParse(reason[17..], out var peer)) Mismatch(peer);
        pinger?.Dispose(); ackTimer?.Dispose(); waiting.Clear(); backlog.Clear(); unacked.Clear(); pendingBytes = 0; unackedBytes = 0;
        closed?.Invoke(reason); closed = null; received = null; opened = null;
    }
    public void Dispose() => Link.Close();
    private sealed class Slot
    {
        internal Slot(long hold, Action flush) { Hold = hold; Flush = flush; }
        internal Slot(long hold, string member, Action resend, Action? resync) { Hold = hold; Member = member; Flush = resend; Resync = resync; }
        internal long Hold { get; }
        internal string? Member { get; }
        internal Action Flush { get; }
        internal Action? Resync { get; }
        internal LegacyFrame? Base;
        internal JsonArray Ops = [];
        internal long Size;
        internal bool Lazy;
    }
}

/// <summary>Serializes synchronous state transitions without holding the queue lock while invoking callbacks.</summary>
internal sealed class LegacyDispatcher
{
    private readonly object gate = new();
    private readonly Queue<Action> pending = new();
    private int owner;
    public void Post(Action action, Action<Exception>? failure = null)
    {
        if (Volatile.Read(ref owner) == Environment.CurrentManagedThreadId) { action(); return; }
        bool drain;
        lock (gate)
        {
            pending.Enqueue(() => { try { action(); } catch (Exception error) { try { failure?.Invoke(error); } catch (Exception) { } } });
            drain = owner == 0; if (drain) owner = Environment.CurrentManagedThreadId;
        }
        if (drain) Drain();
    }
    private void Drain()
    {
        while (true)
        {
            Action next;
            lock (gate) { if (pending.Count == 0) { owner = 0; break; } next = pending.Dequeue(); }
            next();
        }
    }
    public void Invoke(Action action) => Invoke(() => { action(); return true; });
    public T Invoke<T>(Func<T> action)
    {
        if (Volatile.Read(ref owner) == Environment.CurrentManagedThreadId) return action();
        using var done = new ManualResetEventSlim(); T result = default!; ExceptionDispatchInfo? failure = null;
        bool drain;
        lock (gate)
        {
            pending.Enqueue(() => { try { result = action(); } catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); } finally { done.Set(); } });
            drain = owner == 0; if (drain) owner = Environment.CurrentManagedThreadId;
        }
        if (drain) Drain();
        else done.Wait();
        failure?.Throw(); return result;
    }
}
