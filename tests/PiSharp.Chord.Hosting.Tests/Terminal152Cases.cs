// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/tui/launcher.ts, packages/chord/src/node/tui/actions.ts, packages/chord/src/tui/bridge.ts.
// Authored native regression; not an upstream execution capture.
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Terminal;
using PiSharp.Chord.Link;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Surfaces;

internal static class Terminal152Cases
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Refused(Func<Task> run)
    { try { await run(); } catch (Exception error) when (error is IOException or ArgumentException or ObjectDisposedException or PlatformNotSupportedException) { return; } throw new Exception("expected refusal"); }
    private static async Task Until(Func<bool> value)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!value()) await Task.Delay(5, deadline.Token);
    }

    public static Task FramesAndFilter()
    {
        const string esc = "\u001b";
        var safe = "text\r\n\t" + esc + "[38;2;1;2;3mred" + esc + "[0m" + esc + "[2;3H" + esc + "[?1049h";
        Check(TerminalOutput.Filter(safe) == safe, "drawing and supported modes changed");
        Check(TerminalOutput.Filter("a" + esc + "]52;c;secret\a" + "b" + esc + "Pdevice" + esc + "\\c") == "abc", "OSC/DCS escaped the boundary");
        Check(TerminalOutput.Filter(esc + "[?47h" + esc + "[6n" + esc + "[12345A" + "\u009b31m" + "visible") == "visible", "unsafe private mode/query/C1 escaped");
        Check(TerminalOutput.Filter(esc + "]unterminated") == "", "unterminated escape persisted");
        Check(TerminalOutput.Filter("next") == "next", "frame state leaked");
        var rejected = false; try { TerminalOutput.Filter(new string('界', 22000)); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "UTF8 frame bound used UTF16 length only");
        var text = new string('x', TerminalOutput.FrameUnits - 1) + "😀" + esc + "[31m" + new string('y', 50000);
        var split = TerminalOutput.SplitFrames(text);
        Check(string.Concat(split) == text && split.All(frame => frame.Length <= TerminalOutput.FrameUnits), "split lost text or overran units");
        Check(split.All(frame => frame.Length == 0 || !char.IsHighSurrogate(frame[^1])), "split cut a surrogate");
        Check(split.All(frame => frame.Length == 0 || !frame.EndsWith(esc, StringComparison.Ordinal)), "split cut an escape");

        ClientMessage[] messages = [new ClientMessage.Write("😀" + esc + "[0m"), new ClientMessage.SocketSend(4, new byte[] { 0, 255, 32 }),
            new ClientMessage.Request(7, new TerminalRequest.Copy("test")), new ClientMessage.Outdated(), new ClientMessage.Exit(130)];
        var reader = TerminalFrameReaders.Client(); var received = new List<ClientMessage>();
        foreach (var b in messages.SelectMany(TerminalPipe.Encode)) received.AddRange(reader.Push([b]));
        reader.Complete();
        Check(received.Count == messages.Length && received[0] == messages[0], "fragmented frames lost header or UTF8 payload");
        Check(received[1] is ClientMessage.SocketSend bytes && bytes.Bytes.Span.SequenceEqual(new byte[] { 0, 255, 32 }), "binary payload changed");
        Check(received[2] == messages[2] && received[4] == messages[4], "request/exit frame changed");
        var oversized = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(oversized, TerminalPipe.MaximumFrameBytes + 1);
        var failed = false; try { TerminalFrameReaders.Client().Push(oversized); } catch (IOException) { failed = true; }
        Check(failed, "oversize prefix accepted before allocation");
        var invalid = Encoding.UTF8.GetBytes("{\"kind\":\"socket_open\",\"socket\":1.5}");
        var packet = new byte[invalid.Length + 8]; BinaryPrimitives.WriteInt32BigEndian(packet, invalid.Length + 4);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), invalid.Length); invalid.CopyTo(packet, 8);
        failed = false; try { TerminalFrameReaders.Client().Push(packet); } catch (IOException) { failed = true; }
        Check(failed, "non-integer socket accepted");
        var launcher = TerminalFrameReaders.Launcher();
        var attached = (LauncherMessage.Attached)launcher.Push(TerminalPipe.Encode(new LauncherMessage.Attached(1, "a.bin", new byte[] { 0, 255 })))[0];
        Check(attached.Bytes.Span.SequenceEqual(new byte[] { 0, 255 }), "attachment body changed");
        Check(TerminalOutput.ResetModes.Contains("\u001b7\u001b[r\u001b8", StringComparison.Ordinal), "reset malformed C# hex escape");
        return Task.CompletedTask;
    }

    public static async Task LauncherAndChannel()
    {
        var io = new FakeIo(); var services = new FakeServices(); var clock = new FakeClock();
        await using var session = new TerminalLauncherSession(new("base-1", "", 80, 24), io, services, clock: clock);
        using var channel = new TerminalChannel(session.Bridge); using var terminal = new BridgeTerminal(channel);
        session.Start(); channel.Write("first\u001b]52;c;bad\a"); Check(io.Text == "first", "launcher failed to filter");
        var input = ""; terminal.Input += data => input += data;
        io.Send("a"); Check(input == "a", "input failed to traverse bridge");
        io.Columns = 91; io.Resize(); Check(terminal.Columns == 91, "resize did not update bridged terminal");
        var links = Enumerable.Range(0, 5).Select(_ => channel.OpenSocket()).ToArray();
        Check(services.Sockets.Count == 4 && links[4].Closed, "more than four links admitted");
        services.Sockets[0].Receive(new byte[] { 2, 3 }); byte[]? received = null;
        links[0].OnMessage(data => received = data.ToArray());
        Check(received?.SequenceEqual(new byte[] { 2, 3 }) == true, "relay lost buffered bytes");
        links[0].Send(new byte[] { 4, 5 }); await Until(() => services.Sockets[0].Sent.Count == 1);
        Check(services.Sockets[0].Sent[0].SequenceEqual(new byte[] { 4, 5 }), "relay send changed bytes");
        links[0].Send(new byte[TerminalLauncherSession.MaximumSocketBytes + 1]);
        Check(links[0].Closed && services.Sockets[0].Disposed, "socket over the queued-byte bound stayed open");
        var waiting = Enumerable.Range(0, 8).Select(_ => channel.RequestAsync(new TerminalRequest.Fetch("file"))).ToArray();
        await Refused(() => channel.RequestAsync(new TerminalRequest.Base()));
        services.Fetch.TrySetResult("source"); await Task.WhenAll(waiting);

        var copy = channel.RequestAsync(new TerminalRequest.Copy("safe"));
        await Until(() => io.Text.Contains("Copy these", StringComparison.Ordinal));
        var atPrompt = io.Text; channel.Write("HELD"); Check(io.Text == atPrompt, "untrusted output covered prompt");
        io.Send("y"); Check(!copy.IsCompleted, "key quarantine failed");
        await Refused(() => channel.RequestAsync(new TerminalRequest.Open("https://example.test")));
        clock.Advance(TimeSpan.FromMilliseconds(301)); io.Send("y");
        Check((await copy as JsonValue)?.GetValue<bool>() == true, "copy failed after confirmed input");
        Check(io.Text.Contains("\u001b]52;c;c2FmZQ==\a", StringComparison.Ordinal) && io.Text.Contains("HELD", StringComparison.Ordinal), "copy or held output missing");
        Check(input == "a", "confirmation keys reached client");
        var outdated = ""; session.Outdated += digest => outdated = digest;
        channel.Outdated(); await Until(() => outdated == "base-2");
        io.Send("\u0003\u0003\u0003");
        Check(await session.Exited.WaitAsync(TimeSpan.FromSeconds(3)) == 130, "triple interrupt did not exit 130");
        Check(io.Stopped && io.Text.EndsWith(TerminalOutput.ResetModes, StringComparison.Ordinal), "terminal modes not restored");
        Check(services.Sockets.All(socket => socket.Disposed), "socket leaked at exit");
    }

    public static async Task ConfirmationBoundsAndShutdown()
    {
        var io = new FakeIo(); var services = new FakeServices(); var clock = new FakeClock();
        await using var session = new TerminalLauncherSession(new("base-1", ""), io, services, clock: clock);
        using var channel = new TerminalChannel(session.Bridge); session.Start();
        var prompt = channel.RequestAsync(new TerminalRequest.Copy("data")); await Until(() => io.Text.Contains("Copy these", StringComparison.Ordinal));
        var before = io.Text.Length;
        for (var i = 0; i < 40; i++) channel.Write(new string('x', 32768));
        io.Rows = 1; io.Resize();
        Check((await prompt as JsonValue)?.GetValue<bool>() == false, "resize hid part of confirmation without refusing");
        Check(io.Text.Length - before <= TerminalLauncherSession.MaximumHeldBytes + 200, "held output exceeded limit");
        io.Rows = 24;
        var inFlight = channel.RequestAsync(new TerminalRequest.Fetch("file"));
        await session.StopAsync(); await Refused(() => inFlight);
        await Refused(() => channel.RequestAsync(new TerminalRequest.Base()));
        await Refused(() => TerminalLauncher.StartDownloadedAsync(new Uri("https://example.test"), new TerminalCredential.File("must-not-read"), io));
        var connecting = new FakeServices { Connecting = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var waitingSession = new TerminalLauncherSession(new("base-1", ""), new FakeIo(), connecting);
        using var waitingChannel = new TerminalChannel(waitingSession.Bridge);
        var link = waitingChannel.OpenSocket(); link.Send(new byte[] { 1 }); link.Close();
        Check(connecting.OpenToken.IsCancellationRequested, "closing a connecting link left its network dial alive");
    }

    public static async Task ActionsAndNetwork()
    {
        var server = new Uri("https://example.test/app/");
        Check(HttpTerminalServices.TerminalFile("/_chord/tui/plugin/a.js", server) == "/_chord/tui/plugin/a.js", "terminal file refused");
        foreach (var path in new[] { "https://other.test/_chord/tui/a.js", "/secret", "/_chord/tui/a.js?token=x", "/_chord/tui/a/b.js", "/_chord/tui/%2e%2e/secret" })
            await Refused(() => Task.FromResult(HttpTerminalServices.TerminalFile(path, server)));
        foreach (var path in new[] { "\\\\server\\share\\secret", "//server/share", "NUL.txt", "x/COM1", "x:secret", " x", "x\u202e.txt", "x\t.txt" })
            Check(!TerminalActions.Attachable(path, windows: true), "unsafe Windows attachment path allowed: " + path);
        var prompt = new AutoPrompt(); var opened = ""; var lookups = 0;
        var actions = new TerminalActions((url, _) => { opened = url; return Task.CompletedTask; }, (_, _) => { lookups++; return Task.FromResult<ITerminalAttachment>(new FakeFile("/resolved/b.txt", Encoding.UTF8.GetBytes("file"))); });
        await Refused(() => actions.ActAsync(new TerminalRequest.Copy("hidden\u202e"), prompt));
        await Refused(() => actions.ActAsync(new TerminalRequest.Open("https://user:pass@example.test"), prompt));
        Check(prompt.Prompts.Count == 0 && lookups == 0, "invalid action prompted or touched filesystem");
        Check(await actions.ActAsync(new TerminalRequest.Open("https://example.test/a?b=c&d=e"), prompt) && opened.Contains("b=c&d=e", StringComparison.Ordinal), "URL argument changed");
        prompt.Yes = false;
        Check(await actions.AttachAsync("a.txt", prompt) is null && lookups == 0, "declined attachment looked up path");
        prompt.Yes = true; prompt.Prompts.Clear();
        var file = await actions.AttachAsync("a.txt", prompt);
        Check(file is not null && Encoding.UTF8.GetString(file.Bytes.Span) == "file" && prompt.Prompts.Count == 2, "linked file not reconfirmed/read");
        var handler = new FakeHttpHandler(); using var http = new HttpClient(handler);
        await using var network = new HttpTerminalServices(server, "private-token", http);
        Check(await network.FetchAsync("/_chord/tui/a.js", default) == "bundle", "HTTP fetch failed");
        Check(handler.Authorization == "Bearer private-token", "trusted fetch lacked authorization");
        handler.Oversized = true;
        await Refused(() => network.FetchAsync("/_chord/tui/a.js", default));
        Check(handler.ReadBytes <= HttpTerminalServices.MaximumDownloadBytes + 1, "oversize download read unbounded data");
    }

    public static async Task ScopeAndOptimistic()
    {
        await using var host = Hosts.Create(new() { OnError = _ => { } });
        await host.LoadAsync([], ChordContext.Background);
        var redraw = 0; var scope = new TerminalScope(host, () => redraw++);
        var state = scope.PageState("draft", "one"); state.Set("two"); Check(redraw == 1, "page state did not redraw");
        var source = new FakeWatchable(); var read = scope.Watch(source);
        await Until(() => redraw > 1); Check(read() == 1, "scope watch lost current value");
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = 10; var optimistic = scope.Optimistic<int, int>(() => value, (current, action) => current + action);
        Check(ReferenceEquals(optimistic.Add(1, first.Task), first.Task), "optimistic changed caller task"); _ = optimistic.Add(2, second.Task);
        Check(optimistic.Read() == 13 && optimistic.Pending, "optimistic actions not overlaid");
        first.SetResult(1); await first.Task; await Task.Delay(20);
        Check(optimistic.Read() == 13 && optimistic.Pending, "first settlement prematurely removed shown action");
        value = 20; second.SetException(new IOException("call failed"));
        await Until(() => !optimistic.Pending); Check(optimistic.Read() == 20, "failed call did not clear whole group");
        scope.Dispose(); var stopped = redraw; state.Set("three"); await Task.Delay(20);
        Check(redraw == stopped, "disposed scope redrew");
        Check(source.Stopped, "disposed scope did not stop native watch");
        using var next = new TerminalScope(host, () => { }); Check(next.PageState("draft", "fresh").Read() == "three", "page state lost between scopes");
    }

    public static async Task ActualAttachment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "test.bin"); var bytes = new byte[] { 0, 1, 128, 255 };
            await File.WriteAllBytesAsync(file, bytes);
            var attached = await new TerminalActions().AttachAsync(file, new AutoPrompt());
            Check(attached is not null && attached.Name == "test.bin" && attached.Bytes.Span.SequenceEqual(bytes), "native regular-file handle attachment failed");
            await Refused(() => new TerminalActions().AttachAsync(directory, new AutoPrompt()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static async Task NativeRuntime()
    {
        var set = new ScriptSet("base-1", "/_chord/tui/all.js", []);
        await using var server = Hosts.Create(new()
        {
            Address = Scripts.Server.Address(), Principal = new() { ["kind"] = "server" },
            OnError = _ => { }
        });
        var definition = StateDefinition.Define("test.terminal.scripts", new() { Version = 1, Schema = Scripts.Schema, Initial = set.ToJson });
        var loaded = await server.LoadAsync([new("test.scripts", Facet.Define((facet, _) => facet.Serve(Scripts.TuiScripts, (_, _, self) =>
            new(new Dictionary<string, object?> { ["set"] = self.State(definition) }, "main"))))], ChordContext.Background);
        Check(loaded.Failed.Count == 0, "terminal script server failed to load: " + string.Join(" | ", loaded.Failed.Select(failure => failure.Error.Message)));
        var io = new FakeIo();
        await using var session = new TerminalLauncherSession(new("base-1", ""), io, new LocalServices(server));
        session.Start();
        await using var runtime = await TerminalRuntime.StartAsync(session.Bridge, new()
        {
            ResolveTrustedScript = (_, _) => throw new Exception("Empty native set unexpectedly requested JavaScript execution"),
            Render = app => app.Terminal.Write(app.Theme.Foreground("accent", "native terminal ready")),
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Check(io.Text.Contains("native terminal ready", StringComparison.Ordinal), "real client/runtime never rendered");
        using var scope = new TerminalScope(runtime.Host, () => { });
        var messages = Messages.Define("test.terminal", new Dictionary<string, Catalog> { ["en"] = new() { ["hello"] = "Hello" } });
        Check(scope.Messages(messages).Text("hello") == "Hello", "runtime language scope could not render a catalog");
        await runtime.StopAsync(); await session.StopAsync();
    }

    private sealed class FakeIo : ITerminalIo
    {
        private readonly StringBuilder _output = new(); private Action<string>? _input; private Action? _resized;
        public int Columns { get; set; } = 80; public int Rows { get; set; } = 24; public bool Stopped;
        public string Text { get { lock (_output) return _output.ToString(); } }
        public void Start(Action<string> input, Action resized) { _input = input; _resized = resized; }
        public void Stop() { Stopped = true; _input = null; _resized = null; }
        public void Write(string data) { lock (_output) _output.Append(data); }
        public void Send(string text) => _input?.Invoke(text); public void Resize() => _resized?.Invoke();
    }
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks; public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks; public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
    private sealed class FakeServices : ITerminalServices
    {
        public TaskCompletionSource<string> Fetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FakeSocket> Sockets = [];
        public TaskCompletionSource<ITerminalSocket>? Connecting; public CancellationToken OpenToken;
        public Task<string> FetchAsync(string path, CancellationToken cancellationToken) => Fetch.Task.WaitAsync(cancellationToken);
        public Task<JsonNode?> BaseAsync(CancellationToken cancellationToken) => Task.FromResult<JsonNode?>(JsonValue.Create("base-2"));
        public Task<ITerminalSocket> OpenSocketAsync(CancellationToken cancellationToken)
        {
            OpenToken = cancellationToken;
            if (Connecting is not null) return Connecting.Task.WaitAsync(cancellationToken);
            var socket = new FakeSocket(); Sockets.Add(socket); return Task.FromResult<ITerminalSocket>(socket);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeSocket : ITerminalSocket
    {
        public event Action<ReadOnlyMemory<byte>>? Message; public event Action? Closed;
        public List<byte[]> Sent = []; public bool Disposed;
        public void Receive(byte[] bytes) => Message?.Invoke(bytes);
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { Sent.Add(data.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { if (!Disposed) { Disposed = true; Closed?.Invoke(); } return ValueTask.CompletedTask; }
    }
    private sealed class AutoPrompt : ITerminalPrompt
    {
        public bool Yes = true; public int Columns => 80; public List<IReadOnlyList<string>> Prompts = []; public string Written = "";
        public Task<bool> AskAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken) { Prompts.Add(lines); return Task.FromResult(Yes); }
        public void WriteTrusted(string data) => Written += data;
    }
    private sealed class FakeFile(string path, byte[] bytes) : ITerminalAttachment
    {
        public string Path => path; public long Length => bytes.Length;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken)
        { var count = Math.Min(buffer.Length, bytes.Length - (int)offset); bytes.AsMemory((int)offset, count).CopyTo(buffer); return ValueTask.FromResult(count); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeWatchable : INativeWatchable<int>, INativeWatchHandle<int>
    {
        public bool Stopped; public int Value => 1;
        private readonly TaskCompletionSource<WatchEnd> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WatchEnd> Closed => _closed.Task;
        public Task<INativeWatchHandle<int>> WatchAsync(ChordContext context) => Task.FromResult<INativeWatchHandle<int>>(this);
        public void Start(Func<NativeWatchCall<int>, ChordContext, Task> listener) { }
        public Task<WatchEnd> StopAsync() { Stopped = true; _closed.TrySetResult(WatchEnd.Stopped); return _closed.Task; }
    }
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public string? Authorization; public bool Oversized; public int ReadBytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            HttpContent content = Oversized ? new StreamContent(new Endless(this)) : new StringContent("bundle");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        private sealed class Endless(FakeHttpHandler owner) : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'a', offset, count); owner.ReadBytes += count; return count; }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { buffer.Span.Fill((byte)'a'); owner.ReadBytes += buffer.Length; return ValueTask.FromResult(buffer.Length); }
            public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
    private sealed class LocalServices(ChordHost server) : ITerminalServices
    {
        public Task<string> FetchAsync(string path, CancellationToken cancellationToken) => throw new IOException("No JavaScript execution");
        public Task<JsonNode?> BaseAsync(CancellationToken cancellationToken) => Task.FromResult<JsonNode?>(JsonValue.Create("base-1"));
        public Task<ITerminalSocket> OpenSocketAsync(CancellationToken cancellationToken)
        {
            var pair = MemoryLinkPair.Create(); var socket = new LocalSocket(pair.Second);
            server.Accept(pair.First, new() { Principal = new() { ["kind"] = "user", ["id"] = "terminal-test" } }, ChordContext.Background);
            return Task.FromResult<ITerminalSocket>(socket);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class LocalSocket : ITerminalSocket
        {
            private readonly ILink _link; private readonly object _gate = new(); private readonly List<byte[]> _pending = []; private bool _started;
            public event Action<ReadOnlyMemory<byte>>? Message; public event Action? Closed;
            public LocalSocket(ILink link)
            {
                _link = link; link.OnClose(() => Closed?.Invoke());
                link.OnMessage(bytes => { lock (_gate) { if (!_started) _pending.Add(bytes.ToArray()); else Message?.Invoke(bytes); } });
            }
            public void Start() { lock (_gate) { _started = true; foreach (var bytes in _pending) Message?.Invoke(bytes); _pending.Clear(); } }
            public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
            { _link.Send(bytes); return ValueTask.CompletedTask; }
            public ValueTask DisposeAsync() { _link.Close(); return ValueTask.CompletedTask; }
        }
    }
}
