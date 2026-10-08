using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Contracts;
using PiSharp.Tui;

// Test-local arguments for an adapter to the integrator's real command API.
// No alternate CLI/Agent/session implementation belongs in that adapter.
internal sealed record LiveInteractiveAcceptanceInput(
    string Workspace, string Session, bool CreateNew, HttpMessageHandler Handler,
    IConsoleTerminal Terminal, ITerminalViewportSource Viewport, TextWriter Diagnostics,
    Func<JsonData, CancellationToken, ValueTask> Observe, CancellationToken Cancellation,
    string? PermittedRead)
{
    internal static readonly ModelDescriptor Model = new("openai/gpt-4.1-mini", "openai-completions", "openrouter");
    // The factory validates this fixed URI. The supplied handler intercepts every send.
    internal static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/chat/completions");
    internal const string InertKey = "authored-inert-acceptance-value";
}

internal static class LiveInteractiveAcceptanceFixtures
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(12);
    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal static string Sse(params string[] records) => string.Concat(records.Select(record => "data: " + record + "\n\n"));
    internal static string Delta(string text) => JsonSerializer.Serialize(new
    { id = "acceptance-response", choices = new[] { new { index = 0, delta = new { role = "assistant", content = text } } } });
    internal const string Stop = """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";
    internal static string Text(string text) => Sse(Delta(text), Stop, "[DONE]");
    internal static string ReadCall(string path) => Sse(JsonSerializer.Serialize(new
    {
        id = "acceptance-tool-response",
        choices = new[] { new { index = 0, delta = new { role = "assistant", tool_calls = new[] {
            new { index = 0, id = "acceptance-read-call", type = "function", function = new {
                name = "read", arguments = JsonSerializer.Serialize(new { path }) } } } } } }
    }), """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""", "[DONE]");

    internal sealed class Files
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pisharp-live-acceptance-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string ReadTarget => Path.Combine(Root, "permitted.txt");
        internal readonly string Marker = "READ-MARKER-" + Guid.NewGuid().ToString("N");
        internal Files() => Directory.CreateDirectory(Root);
        internal async Task PrepareRead() => await File.WriteAllTextAsync(ReadTarget, Marker, new UTF8Encoding(false));
        internal async Task Receipt(string scenario, object value) => await File.WriteAllTextAsync(
            Path.Combine(Root, "receipt-" + scenario + ".json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, evidence = "injected HTTP and terminal; no real inference", scenario, value }), new UTF8Encoding(false));
    }

    internal sealed class Handler(Func<int, JsonElement, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<JsonElement> requests = [];
        internal bool Disposed;
        internal JsonElement[] Requests { get { lock (gate) return requests.ToArray(); } }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri == LiveInteractiveAcceptanceInput.Endpoint,
                "Composition bypassed the explicit injected endpoint or method.");
            Check(request.Headers.Authorization?.Scheme == "Bearer" &&
                request.Headers.Authorization.Parameter == LiveInteractiveAcceptanceInput.InertKey,
                "Composition did not use the explicit inert auth fixture.");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = document.RootElement.Clone();
            Check(body.GetProperty("model").GetString() == LiveInteractiveAcceptanceInput.Model.Id &&
                body.GetProperty("stream").GetBoolean(), "Explicit model/stream configuration was lost.");
            int index;
            lock (gate) { index = requests.Count; requests.Add(body); }
            return await respond(index, body, token);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        internal static HttpResponseMessage Ok(string wire) => Ok(new MemoryStream(Encoding.UTF8.GetBytes(wire)));
        internal static HttpResponseMessage Ok(Stream stream)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        }
    }

    // An actual cancellable HTTP response body, never a replacement IChatTransport.
    internal sealed class Body : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateBounded<byte[]>(8);
        private byte[]? pending; private int offset, reads;
        internal readonly TaskCompletionSource HeldRead = Gate(), CanceledRead = Gate();
        internal bool Disposed;
        internal int ActiveReads => Volatile.Read(ref reads);
        internal void Feed(string value) => Check(chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(value)), "SSE fixture exceeded capacity.");
        internal void Complete() => chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            Check(Interlocked.Increment(ref reads) == 1, "Concurrent HTTP body reads.");
            try
            {
                if (pending is null || offset == pending.Length)
                {
                    if (!chunks.Reader.TryRead(out pending))
                    {
                        HeldRead.TrySetResult();
                        if (!await chunks.Reader.WaitToReadAsync(token)) return 0;
                        Check(chunks.Reader.TryRead(out pending), "HTTP fixture lost a chunk.");
                    }
                    offset = 0;
                }
                var length = Math.Min(3, Math.Min(destination.Length, pending!.Length - offset));
                pending.AsMemory(offset, length).CopyTo(destination); offset += length; return length;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { CanceledRead.TrySetResult(); throw; }
            finally { Interlocked.Decrement(ref reads); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class Terminal : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly object gate = new();
        private readonly Channel<string> input = Channel.CreateBounded<string>(8);
        private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly StringBuilder output = new();
        private int reads, writes; private long readStarted, readSettled, writeStarted, writeSettled;
        private bool disposed;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false,
            reads, writes, readStarted, readSettled, writeStarted, writeSettled); } }
        public TerminalViewport ReadViewport() => new(120, 40, 0, 0, 120, 40);
        internal string Output { get { lock (gate) return output.ToString(); } }
        internal ValueTask Feed(string text) => input.Writer.WriteAsync(text);
        internal void Complete() => input.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0, "Concurrent physical terminal reads."); reads++; readStarted++; }
            try
            {
                if (!await input.Reader.WaitToReadAsync(token)) return 0;
                if (!input.Reader.TryRead(out var text) || text is null || text.Length > destination.Length)
                    throw new InvalidOperationException("Terminal fixture chunk exceeded decoder buffer.");
                text.AsMemory().CopyTo(destination); return text.Length;
            }
            finally { lock (gate) { reads--; readSettled++; } }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                Check(writes == 0, "Concurrent physical terminal writes."); writes++; writeStarted++;
                try { Check(output.Length + frame.Length <= 2_097_152, "Terminal output exceeded fixture bound."); output.Append(frame.Span); }
                finally { writes--; writeSettled++; }
            }
            changed.Writer.TryWrite(true); return ValueTask.CompletedTask;
        }
        internal async Task WaitText(string text, CancellationToken token)
        {
            while (!Output.Contains(text, StringComparison.Ordinal))
                await changed.Reader.ReadAsync(token).AsTask().WaitAsync(Bound);
        }
        internal void AssertJoined()
        {
            var value = Snapshot;
            Check(value.ActiveReads == 0 && value.ActiveWrites == 0 && value.ReadWorkersStarted == value.ReadWorkersSettled &&
                value.WriteWorkersStarted == value.WriteWorkersSettled && !disposed, "Borrowed terminal was disposed or physical I/O was detached.");
            Check(Output.Contains("\u001b[?1049l", StringComparison.Ordinal), "Terminal composition did not leave alternate screen.");
        }
        public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
    }

    internal sealed class Run : IAsyncDisposable
    {
        internal readonly Terminal Terminal = new();
        internal readonly StringWriter Error = new();
        internal readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(30));
        internal readonly TaskCompletionSource Ready = Gate();
        internal readonly List<JsonElement> Records = [];
        private readonly Channel<bool> ends = Channel.CreateBounded<bool>(8);
        private readonly Channel<bool> admission = Channel.CreateBounded<bool>(8);
        internal readonly Task<int> Running;
        private readonly Handler handler;
        internal Run(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke, Files files, Handler handler,
            bool createNew = true, string? permittedRead = null)
        {
            this.handler = handler;
            Running = invoke(new(files.Root, files.Session, createNew, handler, Terminal, Terminal, Error,
                Observe, Stop.Token, permittedRead));
        }
        private ValueTask Observe(JsonData record, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var body = record.Value.Clone();
            lock (Records) { Check(Records.Count < 2048, "RPC observations exceeded fixture bound."); Records.Add(body); }
            if (body.GetProperty("type").GetString() == "response" && body.GetProperty("command").GetString() == "get_messages")
            { Check(body.GetProperty("success").GetBoolean(), "Initial history was rejected."); Ready.TrySetResult(); }
            if (body.GetProperty("type").GetString() == "agent_settled") Check(ends.Writer.TryWrite(true), "Unexpected unconsumed agent settlements.");
            if (body.GetProperty("type").GetString() == "response" &&
                body.GetProperty("command").GetString() == "get_state" && body.GetProperty("id").GetString() != "chat-start")
            {
                Check(body.GetProperty("success").GetBoolean(), "Admission state query was rejected.");
                Check(admission.Writer.TryWrite(body.GetProperty("data").GetProperty("pisharpRunOwnerSettled").GetBoolean()),
                    "Unexpected unconsumed admission states.");
            }
            return ValueTask.CompletedTask;
        }
        internal async Task Prompt(string text) { await Ready.Task.WaitAsync(Bound); await Terminal.Feed(text + "\r"); }
        internal async Task Ended()
        {
            await ends.Reader.ReadAsync(Stop.Token).AsTask().WaitAsync(Bound);
            for (var attempt = 0; attempt < 128; attempt++)
            {
                await Terminal.Feed("/state\r");
                if (await admission.Reader.ReadAsync(Stop.Token).AsTask().WaitAsync(Bound)) return;
            }
            throw new InvalidOperationException("Dispatcher prompt admission did not settle within the fixture bound.");
        }
        internal JsonElement[] Observations { get { lock (Records) return Records.ToArray(); } }
        internal async Task Finish()
        {
            await Terminal.Feed("\u0004");
            Check(await Running.WaitAsync(Bound) == 0, "Actual terminal command did not exit successfully: " + Error);
            Terminal.AssertJoined(); Check(!handler.Disposed, "Actual command disposed the borrowed HTTP handler.");
        }
        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            try { Stop.Cancel(); } catch (Exception error) { failures.Add(error); }
            Terminal.Complete();
            try { await Running.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            finally { handler.Dispose(); Stop.Dispose(); Error.Dispose(); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }
}
