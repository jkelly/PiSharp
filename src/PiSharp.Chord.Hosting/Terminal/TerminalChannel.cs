// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/tui/channel.ts, packages/chord/src/tui/terminal.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>Client-side requests, relayed Chord links and terminal input. Dispose settles every pending request.</summary>
public sealed class TerminalChannel : IDisposable
{
    private readonly ITerminalBridge _bridge;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Dictionary<long, TaskCompletionSource<object?>> _requests = [];
    private readonly Dictionary<long, TerminalRelayLink> _sockets = [];
    private bool _disposed;
    private long _next;
    public TerminalStart Start => _bridge.Start;
    public event Action<string>? Input;
    public event Action<int, int>? Resize;

    public TerminalChannel(ITerminalBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _subscription = bridge.Subscribe(Dispatch);
    }

    public async Task<object?> RequestAsync(TerminalRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<object?> waiting; long id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = checked(++_next); waiting = new(TaskCreationOptions.RunContinuationsAsynchronously); _requests.Add(id, waiting);
        }
        try
        {
            _bridge.Send(new ClientMessage.Request(id, request));
            return await waiting.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { lock (_gate) _requests.Remove(id); }
    }

    public void Write(string data)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var frame in TerminalOutput.SplitFrames(data)) _bridge.Send(new ClientMessage.Write(frame));
    }
    public void Outdated() => _bridge.Send(new ClientMessage.Outdated());
    public void Exit(int code = 0) => _bridge.Send(new ClientMessage.Exit(code));

    public TerminalRelayLink OpenSocket(Action? opened = null, Action? received = null)
    {
        TerminalRelayLink link; long id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); id = checked(++_next);
            link = new(id, _bridge, opened, received, () => { lock (_gate) _sockets.Remove(id); }); _sockets.Add(id, link);
        }
        try { _bridge.Send(new ClientMessage.SocketOpen(id)); return link; }
        catch { link.Dropped(); throw; }
    }

    private void Dispatch(LauncherMessage message)
    {
        TerminalRelayLink? socket = null; TaskCompletionSource<object?>? waiting = null;
        lock (_gate)
        {
            if (_disposed) return;
            switch (message)
            {
                case LauncherMessage.SocketOpened m: _sockets.TryGetValue(m.Socket, out socket); break;
                case LauncherMessage.SocketMessage m: _sockets.TryGetValue(m.Socket, out socket); break;
                case LauncherMessage.SocketClosed m: _sockets.Remove(m.Socket, out socket); break;
                case LauncherMessage.Reply m: _requests.Remove(m.Id, out waiting); break;
                case LauncherMessage.Attached m: _requests.Remove(m.Id, out waiting); break;
                case LauncherMessage.Refused m: _requests.Remove(m.Id, out waiting); break;
            }
        }
        switch (message)
        {
            case LauncherMessage.Input m: Input?.Invoke(m.Data); break;
            case LauncherMessage.Resize m: Resize?.Invoke(m.Columns, m.Rows); break;
            case LauncherMessage.SocketOpened: socket?.Opened(); break;
            case LauncherMessage.SocketMessage m: socket?.Received(m.Bytes); break;
            case LauncherMessage.SocketClosed: socket?.Dropped(); break;
            case LauncherMessage.Reply m: waiting?.TrySetResult(m.Value); break;
            case LauncherMessage.Attached m: waiting?.TrySetResult(new TerminalAttachment(m.Name, m.Bytes)); break;
            case LauncherMessage.Refused m: waiting?.TrySetException(new IOException(m.Message)); break;
        }
    }

    public void Dispose()
    {
        TerminalRelayLink[] sockets; TaskCompletionSource<object?>[] requests;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            sockets = _sockets.Values.ToArray(); _sockets.Clear(); requests = _requests.Values.ToArray(); _requests.Clear();
            Input = null; Resize = null;
        }
        _subscription.Dispose();
        foreach (var request in requests) request.TrySetException(new ObjectDisposedException(nameof(TerminalChannel)));
        foreach (var socket in sockets) try { socket.Close(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>A Chord link whose socket and credentials stay on the launcher side.</summary>
public sealed class TerminalRelayLink : LinkEnd
{
    private readonly long _socket;
    private readonly ITerminalBridge _bridge;
    private readonly Action? _opened, _received;
    private readonly Action _forget;
    private readonly object _gate = new();
    private Queue<byte[]>? _connecting = new();
    private int _queuedBytes;
    internal TerminalRelayLink(long socket, ITerminalBridge bridge, Action? opened, Action? received, Action forget)
    { _socket = socket; _bridge = bridge; _opened = opened; _received = received; _forget = forget; }

    public override void Send(ReadOnlyMemory<byte> message)
    {
        lock (_gate)
        {
            if (Closed) return;
            if (_connecting is not null)
            {
                if (message.Length > TerminalLauncherSession.MaximumSocketBytes - _queuedBytes) { Close(); return; }
                _connecting.Enqueue(message.ToArray()); _queuedBytes += message.Length; return;
            }
            _bridge.Send(new ClientMessage.SocketSend(_socket, message.ToArray()));
        }
    }
    public override void Close()
    {
        lock (_gate)
        {
            if (Closed) return;
            try { _bridge.Send(new ClientMessage.SocketClose(_socket)); }
            finally { Dropped(); }
        }
    }
    internal void Opened()
    {
        lock (_gate)
        {
            if (Closed || _connecting is null) return;
            var queued = _connecting; _connecting = null; _queuedBytes = 0;
            _opened?.Invoke();
            foreach (var message in queued) if (!Closed) _bridge.Send(new ClientMessage.SocketSend(_socket, message));
        }
    }
    internal void Received(ReadOnlyMemory<byte> bytes) { if (Closed) return; _received?.Invoke(); Deliver(bytes.ToArray()); }
    internal void Dropped() { lock (_gate) { _connecting = null; _queuedBytes = 0; _forget(); Ended(); } }
}

/// <summary>A UI-library-independent terminal; all output goes through the launcher's filter.</summary>
public sealed class BridgeTerminal : IDisposable
{
    private readonly TerminalChannel _channel;
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public event Action? Resized;
    public event Action<string>? Input;
    public BridgeTerminal(TerminalChannel channel)
    {
        _channel = channel; Columns = channel.Start.Columns; Rows = channel.Start.Rows;
        channel.Resize += OnResize; channel.Input += OnInput;
    }
    public void Write(string data) => _channel.Write(data);
    private void OnResize(int columns, int rows) { Columns = columns; Rows = rows; Resized?.Invoke(); }
    private void OnInput(string data) => Input?.Invoke(data);
    public void Dispose() { _channel.Resize -= OnResize; _channel.Input -= OnInput; Resized = null; Input = null; }
}
