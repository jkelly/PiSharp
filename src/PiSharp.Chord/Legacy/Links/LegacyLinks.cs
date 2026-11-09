// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/link.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>Ordered, bidirectional legacy frame link. Unsized sends are not byte-windowed.</summary>
public interface ILegacyLink
{
    bool Closed { get; }
    long? Send(LegacyFrame frame);
    void Receive(Action<LegacyFrame> handler);
    void OnClose(Action<string> handler);
    void Close(string reason = "closed");
}

/// <summary>Common receive backlog and exactly-once close plumbing for protocol 4 transports.</summary>
public abstract class LegacyLink : ILegacyLink
{
    private readonly object gate = new();
    private readonly Queue<LegacyFrame> queue = new();
    private readonly List<Action<string>> closedHandlers = [];
    private Action<LegacyFrame>? receiver;
    private bool closed, closing, delivering;
    public bool Closed { get { lock (gate) return closed || closing; } }
    public long? Send(LegacyFrame frame)
    {
        if (Closed) return null;
        return Transmit(frame);
    }
    protected abstract long? Transmit(LegacyFrame frame);
    protected abstract void Shutdown();
    public void Receive(Action<LegacyFrame> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate) { if (closed) return; receiver = handler; }
        Drain();
    }
    public void OnClose(Action<string> handler)
    {
        lock (gate)
        {
            if (!closed) { closedHandlers.Add(handler); return; }
        }
        handler("closed");
    }
    public void Close(string reason = "closed")
    {
        lock (gate) { if (closed || closing) return; closing = true; }
        try { Transmit(LegacyProtocol.Frame("bye", ("reason", JsonValue.Create(reason)))); }
        catch (Exception) { /* The peer may already be gone. */ }
        End(reason);
    }
    protected void Deliver(LegacyFrame frame)
    {
        if (frame.Type == "bye") { End(frame.String("reason") ?? "peer closed"); return; }
        lock (gate)
        {
            if (closed || closing) return;
            queue.Enqueue(frame);
        }
        Drain();
    }
    private void Drain()
    {
        lock (gate)
        {
            if (delivering) return;
            delivering = true;
        }
        try
        {
            while (true)
            {
                Action<LegacyFrame>? handler; LegacyFrame frame;
                lock (gate)
                {
                    handler = receiver;
                    if (closed || closing || handler is null || queue.Count == 0) { delivering = false; return; }
                    frame = queue.Dequeue();
                }
                handler(frame);
            }
        }
        catch { lock (gate) delivering = false; throw; }
    }
    protected void End(string reason)
    {
        Action<string>[] handlers;
        lock (gate)
        {
            if (closed) return;
            closed = true; queue.Clear(); receiver = null;
            handlers = closedHandlers.ToArray(); closedHandlers.Clear();
        }
        try { Shutdown(); }
        finally { foreach (var handler in handlers) handler(reason); }
    }
}

/// <summary>FIFO asynchronous, detached JSON memory transport, with upstream's silent network sever seam.</summary>
public sealed class LegacyMemoryLink : LegacyLink
{
    private readonly object gate = new();
    private Task pending = Task.CompletedTask;
    private LegacyMemoryLink? peer;
    private volatile bool severed;
    public static (LegacyMemoryLink A, LegacyMemoryLink B) Pair()
    {
        var a = new LegacyMemoryLink(); var b = new LegacyMemoryLink(); a.peer = b; b.peer = a; return (a, b);
    }
    public void Sever() { severed = true; if (peer is not null) peer.severed = true; }
    protected override long? Transmit(LegacyFrame frame)
    {
        if (severed) return null;
        var data = ChordJson.Stringify(LegacyCodec.PackObject(frame));
        lock (gate) pending = pending.ContinueWith(_ =>
        {
            if (!severed) peer?.Deliver(LegacyCodec.Decode(new(data, null)));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return data.Length;
    }
    protected override void Shutdown()
    {
        lock (gate) pending = pending.ContinueWith(_ => peer?.End("peer closed"), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}

/// <summary>A Node IPC channel adapter. Its implementation owns Node IPC serialization, not raw stdio.</summary>
public interface ILegacyIpcChannel
{
    bool Connected { get; }
    event Action<JsonNode?>? Message;
    event Action? Disconnected;
    void Send(JsonObject message, Action<Exception?> completion);
}

public sealed class LegacyIpcLink : LegacyLink
{
    private readonly ILegacyIpcChannel channel;
    public LegacyIpcLink(ILegacyIpcChannel channel)
    {
        this.channel = channel;
        channel.Message += Message;
        channel.Disconnected += Disconnected;
    }
    protected override long? Transmit(LegacyFrame frame)
    {
        if (channel.Connected)
            try { channel.Send(new JsonObject { ["chord2"] = LegacyCodec.PackObject(frame) }, Sent); }
            catch (Exception error) { Sent(error); }
        return null;
    }
    private void Sent(Exception? error)
    {
        if (error is not null) End("ipc send failed: " + (error.Data["code"] as string ?? error.Message));
    }
    private void Message(JsonNode? message)
    {
        if (message is not JsonObject wrapper || !wrapper.TryGetPropertyValue("chord2", out var body)) return;
        try { Deliver(LegacyCodec.UnpackObject(body as JsonObject ?? throw new FormatException("not a frame"))); }
        catch (Exception) { Close("protocol error"); }
    }
    private void Disconnected() => End("ipc disconnected");
    protected override void Shutdown() { channel.Message -= Message; channel.Disconnected -= Disconnected; }
}

/// <summary>A WebSocket surface implemented by a browser bridge or a native .NET adapter.</summary>
public interface ILegacyWebSocket
{
    int ReadyState { get; }
    event Action? Opened;
    event Action<LegacyMessage>? Message;
    event Action? Closed;
    void Send(LegacyMessage message);
    void Close(int code);
}

public sealed class LegacyWebSocketLink : LegacyLink
{
    private readonly ILegacyWebSocket socket;
    private readonly bool compress, acceptPacked;
    private readonly Queue<LegacyMessage> outbox = new();
    private readonly object gate = new();
    public LegacyWebSocketLink(ILegacyWebSocket socket, bool compress = false, bool acceptPacked = true)
    {
        this.socket = socket; this.compress = compress; this.acceptPacked = acceptPacked;
        socket.Opened += Opened; socket.Message += Message; socket.Closed += SocketClosed;
    }
    protected override long? Transmit(LegacyFrame frame)
    {
        var message = LegacyCodec.Encode(frame, compress);
        lock (gate)
        {
            if (socket.ReadyState == 1) socket.Send(message);
            else outbox.Enqueue(message);
        }
        return message.Size;
    }
    private void Opened()
    {
        lock (gate) while (outbox.Count > 0 && !Closed) socket.Send(outbox.Dequeue());
    }
    private void Message(LegacyMessage message)
    {
        try { Deliver(LegacyCodec.Decode(message, acceptPacked)); }
        catch (Exception) { Close("protocol error"); }
    }
    private void SocketClosed() => End("socket closed");
    protected override void Shutdown()
    {
        lock (gate) outbox.Clear();
        socket.Opened -= Opened; socket.Message -= Message; socket.Closed -= SocketClosed;
        if (socket.ReadyState <= 1) socket.Close(1000);
    }
}
