// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/tui/launcher.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>The real terminal, owned by the launcher for the lifetime of a session.</summary>
public interface ITerminalIo
{
    int Columns { get; }
    int Rows { get; }
    void Start(Action<string> input, Action resized);
    void Stop();
    void Write(string data);
}

public interface ITerminalSocket : IAsyncDisposable
{
    event Action<ReadOnlyMemory<byte>>? Message;
    event Action? Closed;
    void Start() { }
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>Trusted-side network operations. Implementations retain authentication; clients receive only returned data.</summary>
public interface ITerminalServices : IAsyncDisposable
{
    Task<string> FetchAsync(string path, CancellationToken cancellationToken);
    Task<JsonNode?> BaseAsync(CancellationToken cancellationToken);
    Task<ITerminalSocket> OpenSocketAsync(CancellationToken cancellationToken);
}

/// <summary>A terminal session for caller-trusted native code. It never evaluates downloaded scripts or assemblies.</summary>
public sealed class TerminalLauncherSession : IAsyncDisposable, ITerminalPrompt
{
    public const int MaximumSockets = 4, MaximumRequests = 8, MaximumHeldBytes = 1024 * 1024, MaximumSocketBytes = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly ITerminalIo _io;
    private readonly ITerminalServices _services;
    private readonly TerminalActions _actions;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly NativeBridge _bridge;
    private readonly Dictionary<long, SocketSlot> _sockets = [];
    private readonly HashSet<long> _requestIds = [];
    private readonly List<string> _held = [];
    private readonly Queue<long> _interrupts = [];
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Prompt? _prompt;
    private bool _started, _finished, _confirming;
    private int _heldBytes;
    private Task? _finish;
    public ITerminalBridge Bridge => _bridge;
    public Task<int> Exited => _exited.Task;
    public int Columns => _io.Columns > 0 ? _io.Columns : 80;
    public int Rows => _io.Rows > 0 ? _io.Rows : 24;
    /// <summary>A trusted host can replace its own native client when the server base changes.</summary>
    public event Action<string>? Outdated;

    public TerminalLauncherSession(TerminalStart start, ITerminalIo io, ITerminalServices services,
        TerminalActions? actions = null, TimeProvider? clock = null)
    {
        _io = io; _services = services; _actions = actions ?? new(); _clock = clock ?? TimeProvider.System;
        _bridge = new(start with { Columns = Columns, Rows = Rows }, Handle);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            if (_started) return; _started = true;
        }
        try { _io.Start(Input, Resized); AppDomain.CurrentDomain.ProcessExit += OnProcessExit; }
        catch { _ = StopAsync(1); throw; }
    }

    private void Handle(ClientMessage message)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_finished, this);
        try
        {
            switch (message)
            {
                case ClientMessage.Write m: WriteClient(m.Data); break;
                case ClientMessage.SocketOpen m: _ = OpenSocketAsync(m.Socket); break;
                case ClientMessage.SocketSend m: Send(m.Socket, m.Bytes); break;
                case ClientMessage.SocketClose m: CloseSocket(m.Socket); break;
                case ClientMessage.Request m: _ = AnswerAsync(m.Id, m.Value); break;
                case ClientMessage.Outdated: _ = CheckOutdatedAsync(); break;
                case ClientMessage.Failed: _io.Write("terminal: the terminal client failed to start\r\n"); _ = StopAsync(1); break;
                case ClientMessage.Exit m: _ = StopAsync(m.Code); break;
                default: _ = StopAsync(1); break;
            }
        }
        catch { _ = StopAsync(1); }
    }

    private void WriteClient(string data)
    {
        // Filter before holding: oversized frames cannot consume the confirmation buffer.
        string filtered; try { filtered = TerminalOutput.Filter(data); } catch (ArgumentException) { return; }
        lock (_gate)
        {
            if (_finished) return;
            if (!_confirming) { _io.Write(filtered); return; }
            var bytes = Encoding.UTF8.GetByteCount(filtered);
            if (bytes <= MaximumHeldBytes - _heldBytes) { _held.Add(filtered); _heldBytes += bytes; }
        }
    }

    private void Input(string data)
    {
        bool exit; Prompt? answered = null;
        lock (_gate)
        {
            if (_finished) return;
            var now = _clock.GetTimestamp();
            foreach (var c in data) if (c == '\x03') _interrupts.Enqueue(now);
            while (_interrupts.TryPeek(out var at) && _clock.GetElapsedTime(at, now) >= TimeSpan.FromSeconds(1)) _interrupts.Dequeue();
            exit = _interrupts.Count >= 3 || _prompt is not null && data.Contains('\x03');
            if (!exit && _prompt is not null)
            {
                if (_clock.GetElapsedTime(_prompt.Started, now) >= TimeSpan.FromMilliseconds(300)) { answered = _prompt; _prompt = null; ClearPrompt(answered.Lines); }
                else return;
            }
            else if (!exit && _confirming) return;
        }
        if (exit) { _ = StopAsync(130); return; }
        if (answered is not null) answered.Answer.TrySetResult(data is "y" or "Y");
        else _bridge.Post(new LauncherMessage.Input(data));
    }

    private void Resized()
    {
        lock (_gate)
        {
            if (_finished) return;
            if (_prompt is not null)
            {
                if (Columns < 12 || TerminalActions.PromptLines(_prompt.Lines, Columns).Count >= Rows)
                { var prompt = _prompt; _prompt = null; prompt.Answer.TrySetResult(false); }
                else DrawPrompt(_prompt.Lines);
            }
        }
        _bridge.Post(new LauncherMessage.Resize(Columns, Rows));
    }

    public Task<bool> AskAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        Prompt prompt;
        lock (_gate)
        {
            if (_finished) return Task.FromResult(false);
            if (_prompt is not null) throw new IOException("terminal: a prompt is open");
            if (Columns < 12 || TerminalActions.PromptLines(lines, Columns).Count >= Rows) throw new IOException("terminal: the prompt does not fit the terminal");
            prompt = new(lines, _clock.GetTimestamp()); _prompt = prompt; DrawPrompt(lines);
        }
        return WaitPromptAsync(prompt, cancellationToken);
    }
    private async Task<bool> WaitPromptAsync(Prompt prompt, CancellationToken cancellationToken)
    {
        try { return await prompt.Answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_prompt, prompt)) { _prompt = null; if (!_finished) ClearPrompt(prompt.Lines); }
        }
    }
    public void WriteTrusted(string data) { lock (_gate) if (!_finished) _io.Write(data); }
    private void DrawPrompt(IReadOnlyList<string> lines)
    {
        var rows = TerminalActions.PromptLines(lines, Columns); var top = Math.Max(1, Rows - rows.Count + 1);
        _io.Write("\u001b[?2026l\u001b[s" + string.Concat(rows.Select((line, i) => $"\u001b[{top + i};1H\u001b[0;30;47m\u001b[2K {line} ")) + "\u001b[0m");
    }
    private void ClearPrompt(IReadOnlyList<string> lines)
    {
        var rows = TerminalActions.PromptLines(lines, Columns); var top = Math.Max(1, Rows - rows.Count + 1);
        _io.Write(string.Concat(rows.Select((_, i) => $"\u001b[{top + i};1H\u001b[0m\u001b[2K")) + "\u001b[u");
    }

    private async Task AnswerAsync(long id, TerminalRequest request)
    {
        string? refused = null;
        lock (_gate)
        {
            if (_finished) { _bridge.Post(new LauncherMessage.Refused(id, "terminal: stopped")); return; }
            if (_requestIds.Count >= MaximumRequests || !_requestIds.Add(id)) refused = "terminal: too many requests at once";
        }
        if (refused is not null) { _bridge.Post(new LauncherMessage.Refused(id, refused)); return; }
        try
        {
            JsonNode? value;
            switch (request)
            {
                case TerminalRequest.Fetch fetch: value = JsonValue.Create(await _services.FetchAsync(fetch.Path, _stop.Token).ConfigureAwait(false)); break;
                case TerminalRequest.Base: value = await _services.BaseAsync(_stop.Token).ConfigureAwait(false); break;
                default:
                    lock (_gate) { if (_confirming) throw new IOException("terminal: a prompt is open"); _confirming = true; }
                    try
                    {
                        if (request is TerminalRequest.Attach attach)
                        {
                            var file = await _actions.AttachAsync(attach.Path, this, _stop.Token).ConfigureAwait(false);
                            if (file is not null) { _bridge.Post(new LauncherMessage.Attached(id, file.Name, file.Bytes)); return; }
                            value = null;
                        }
                        else value = JsonValue.Create(await _actions.ActAsync(request, this, _stop.Token).ConfigureAwait(false));
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _confirming = false;
                            if (!_finished) foreach (var frame in _held) _io.Write(frame);
                            _held.Clear(); _heldBytes = 0;
                        }
                        _bridge.Post(new LauncherMessage.Resize(Columns, Rows));
                    }
                    break;
            }
            _bridge.Post(new LauncherMessage.Reply(id, value));
        }
        catch (Exception error) { _bridge.Post(new LauncherMessage.Refused(id, error is OperationCanceledException ? "terminal: stopped" : error.Message)); }
        finally { lock (_gate) _requestIds.Remove(id); }
    }

    private async Task CheckOutdatedAsync()
    {
        try
        {
            var value = await _services.BaseAsync(_stop.Token).ConfigureAwait(false);
            if (value is JsonValue json && json.TryGetValue<string>(out var digest) && digest != _bridge.Start.Base)
                Outdated?.Invoke(digest);
        }
        catch (Exception) { /* A temporary offline server does not terminate a native client. */ }
    }

    private async Task OpenSocketAsync(long id)
    {
        SocketSlot slot; bool refuse;
        lock (_gate)
        {
            if (_finished) return;
            refuse = _sockets.Count >= MaximumSockets || _sockets.ContainsKey(id);
            slot = new(_stop.Token); if (!refuse) _sockets.Add(id, slot);
        }
        if (refuse) { slot.Stop.Dispose(); _bridge.Post(new LauncherMessage.SocketClosed(id)); return; }
        try
        {
            var socket = await _services.OpenSocketAsync(slot.Stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_finished || !_sockets.TryGetValue(id, out var current) || current != slot) { _ = socket.DisposeAsync(); return; }
                slot.Socket = socket;
                socket.Message += data =>
                {
                    lock (_gate) if (_finished || !_sockets.TryGetValue(id, out var active) || active != slot) return;
                    _bridge.Post(new LauncherMessage.SocketMessage(id, data.ToArray()));
                };
                socket.Closed += () => CloseSocket(id, slot);
            }
            _bridge.Post(new LauncherMessage.SocketOpened(id));
            socket.Start();
        }
        catch { CloseSocket(id, slot); }
    }
    private void Send(long id, ReadOnlyMemory<byte> bytes)
    {
        SocketSlot? slot; bool close = false;
        lock (_gate)
        {
            if (!_sockets.TryGetValue(id, out slot) || slot.Socket is null) return;
            if (bytes.Length > MaximumSocketBytes - slot.BufferedBytes) close = true;
            else
            {
                slot.BufferedBytes += bytes.Length;
                slot.Sends = SendAfterAsync(slot.Sends, id, slot, bytes.ToArray());
            }
        }
        if (close) CloseSocket(id);
    }
    private async Task SendAfterAsync(Task previous, long id, SocketSlot slot, byte[] bytes)
    {
        // Yield before a synchronous transport callback can acquire locks in the opposite order.
        await Task.Yield();
        try
        {
            await previous.ConfigureAwait(false);
            lock (_gate) if (!_sockets.TryGetValue(id, out var current) || current != slot) return;
            await slot.Socket!.SendAsync(bytes, slot.Stop.Token).ConfigureAwait(false);
        }
        catch { CloseSocket(id, slot); }
        finally { lock (_gate) slot.BufferedBytes -= bytes.Length; }
    }
    private void CloseSocket(long id, SocketSlot? expected = null)
    {
        SocketSlot? slot;
        lock (_gate)
        {
            if (!_sockets.TryGetValue(id, out slot) || expected is not null && slot != expected) return;
            _sockets.Remove(id);
        }
        slot.Stop.Cancel(); slot.Stop.Dispose();
        if (slot.Socket is not null) _ = slot.Socket.DisposeAsync();
        _bridge.Post(new LauncherMessage.SocketClosed(id));
    }

    public Task StopAsync(int code = 0)
    {
        lock (_gate)
        {
            if (_finish is not null) return _finish;
            _finished = true;
            return _finish = FinishAsync(code);
        }
    }
    private async Task FinishAsync(int code)
    {
        await Task.Yield();
        try
        {
            _stop.Cancel(); AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            long[] requests; lock (_gate) requests = _requestIds.ToArray();
            foreach (var id in requests) _bridge.Post(new LauncherMessage.Refused(id, "terminal: stopped"));
            lock (_gate) { _prompt?.Answer.TrySetResult(false); _prompt = null; _held.Clear(); _heldBytes = 0; }
            try { _io.Write(TerminalOutput.ResetModes); } finally { _io.Stop(); }
            long[] ids; lock (_gate) ids = _sockets.Keys.ToArray();
            foreach (var id in ids) CloseSocket(id);
            await _services.DisposeAsync().ConfigureAwait(false);
        }
        finally { _bridge.Close(); _exited.TrySetResult(code); }
    }
    private void OnProcessExit(object? sender, EventArgs args)
    { try { _io.Write(TerminalOutput.ResetModes); } finally { _io.Stop(); } }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _stop.Dispose(); }
    private sealed record Prompt(IReadOnlyList<string> Lines, long Started)
    { public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class SocketSlot(CancellationToken stop)
    {
        public ITerminalSocket? Socket; public int BufferedBytes; public Task Sends = Task.CompletedTask;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);
    }

    private sealed class NativeBridge(TerminalStart start, Action<ClientMessage> send) : ITerminalBridge
    {
        private readonly object _lock = new(); private readonly HashSet<Action<LauncherMessage>> _listeners = []; private bool _closed;
        public TerminalStart Start { get; } = start;
        public void Send(ClientMessage message) { lock (_lock) ObjectDisposedException.ThrowIf(_closed, this); send(message); }
        public IDisposable Subscribe(Action<LauncherMessage> listener)
        { lock (_lock) { ObjectDisposedException.ThrowIf(_closed, this); _listeners.Add(listener); } return new Subscription(() => { lock (_lock) _listeners.Remove(listener); }); }
        public void Post(LauncherMessage message)
        { Action<LauncherMessage>[] listeners; lock (_lock) { if (_closed) return; listeners = _listeners.ToArray(); } foreach (var listener in listeners) listener(message); }
        public void Close() { lock (_lock) { _closed = true; _listeners.Clear(); } }
    }
    private sealed class Subscription(Action remove) : IDisposable { private Action? _remove = remove; public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke(); }
}

public static class TerminalLauncher
{
    /// <summary>Secure execution of remote JavaScript requires the separate, exact Node 26.5.1 permission sandbox.</summary>
    public static Task<TerminalLauncherSession> StartDownloadedAsync(Uri server, TerminalCredential credentials, ITerminalIo io,
        CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException(
            "Secure downloaded terminal execution requires the Node 26.5.1 sandbox adapter. Native assembly loading is not a sandbox. No credentials were read and no download was started.");
}
