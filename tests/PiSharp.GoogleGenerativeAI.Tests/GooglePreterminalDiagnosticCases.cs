using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// R49-GOOGLE-PRETERMINAL-CLEANUP-02: literal actual-consumer schedules, all unexecuted.
internal static class GooglePreterminalDiagnosticCases
{
    private static readonly ModelDescriptor Model = new("gemini-3-flash-preview", "google-generative-ai", "google");
    private const string PrivateText = "inert-private-preterminal-sentinel";
    private static readonly JsonData Metadata = JsonData.Parse("""{"type":"chat","id":"gemini-3-flash-preview","api":"google-generative-ai","provider":"google","name":"authored","baseUrl":"https://google.invalid/v1beta","reasoning":true,"input":["text"],"contextWindow":100000,"maxTokens":1000,"cost":{"input":2,"output":3,"cacheRead":0.5,"cacheWrite":0}}""");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    internal static IEnumerable<(string Id, Func<Task> Run)> Cases()
    {
        yield return ("google.native-diagnostics-preterminal-real-consumer-limit", RealConsumerLimits);
        yield return ("google.native-diagnostics-preterminal-source-cleanup-context", SourceCleanupContexts);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static NativeChatDiagnostic Diagnostic(NativeChatFailureCode code) => new(NativeChatAdapter.GoogleGenerativeAI, code);
    private static ImmutableArray<TranscriptEntry> Inputs() => [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":123}"""))];
    private static ChatRequest Request() => new(Model, Inputs(), 123);
    private static string Wire(IEnumerable<JsonElement> chunks) => string.Concat(chunks.Select(chunk => "data: " + JsonSerializer.Serialize(chunk) + "\n\n"));
    private static async Task Join(Task task) { try { await task; } catch { } }
    private static async Task<StreamTerminalEvent> Drain(ChatRun run)
    {
        StreamTerminalEvent? terminal = null; var count = 0;
        await foreach (var value in run.ReadEventsAsync())
            if (value is StreamTerminalEvent end) { terminal = end; count++; }
        Equal(1, count); return terminal ?? throw new InvalidOperationException("Missing settled terminal.");
    }
    private static void AssertChannels(StreamTerminalEvent terminal, ChatResult result, NativeChatFailureCode primary,
        bool cleanup, ChatFailureKind kind, bool compareMessage = true)
    {
        Equal(Diagnostic(primary), terminal.NativeDiagnostic); Equal(terminal.NativeDiagnostic, result.NativeDiagnostic);
        var failure = result.Failure ?? throw new InvalidOperationException("Failure lost through disposal.");
        Equal(terminal.NativeDiagnostic, failure.NativeDiagnostic); Equal(kind, failure.Kind);
        var expectedCleanup = cleanup ? Diagnostic(NativeChatFailureCode.CleanupFailed) : null;
        Equal(expectedCleanup, terminal.NativeCleanupDiagnostic); Equal(expectedCleanup, result.NativeCleanupDiagnostic);
        Check(terminal is StreamError, "Preterminal failure gained tool/success authority.");
        Equal(primary == NativeChatFailureCode.Cancelled ? StopReason.Aborted : StopReason.Error, terminal.Reason);
        Equal(terminal.Reason, terminal.Message.StopReason); Equal(terminal.Reason, result.Message.StopReason);
        if (compareMessage)
            Check(JsonElement.DeepEquals(PiWireJson.WriteMessage(terminal.Message).Value, PiWireJson.WriteMessage(result.Message).Value),
                "Terminal/result message disagreement.");
        var plain = terminal with { NativeDiagnostic = null, NativeCleanupDiagnostic = null };
        Check(JsonElement.DeepEquals(PiWireJson.WriteEvent(plain).Value, PiWireJson.WriteEvent(terminal).Value), "Native fields entered Pi wire.");
        Check(!PiWireJson.WriteEvent(terminal).ToString().Contains(PrivateText, StringComparison.Ordinal) &&
            !failure.Message.Contains(PrivateText, StringComparison.Ordinal), "Private cleanup text leaked.");
    }
    private static void AssertPartial(AssistantMessage message, JsonElement vector)
    {
        Equal(0L, message.Usage.TotalTokens);
        Equal(vector.GetProperty("expectedContentCount").GetInt32(), message.Content.Length);
        var call = message.Content.OfType<ToolCallContent>().Single();
        Equal(vector.GetProperty("firstCallId").GetString(), call.Id); Equal("inspect", call.Name);
        Check(JsonElement.DeepEquals(vector.GetProperty("firstArguments"), call.Arguments.Value), "Admitted first tool arguments lost.");
        Check(message.ExtraProperties is null || !message.ExtraProperties.Values.ContainsKey("rawStopReason"),
            "Unadmitted provider finish reason gained authority.");
    }
    private static async Task RealConsumerLimits()
    {
        var fixture = JsonData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "preterminal-diagnostic-cases.json")));
        Equal(2, fixture.Value.GetProperty("vectors").GetArrayLength());
        foreach (var vector in fixture.Value.GetProperty("vectors").EnumerateArray())
        foreach (var failCleanup in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        foreach (var settleAbort in new[] { false, true })
        {
            var body = new OwnedBody(Wire(vector.GetProperty("chunks").EnumerateArray())) { FailCleanup = failCleanup };
            var responseContent = new BodyContent(body); RequestContent? requestContent = null; var sends = 0;
            using var handler = new Handler((request, _) => {
                sends++; requestContent = new(request.Content!); request.Content = requestContent;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent });
            });
            using var client = new HttpClient(handler);
            var limits = vector.GetProperty("limits");
            var chatClient = new ChatClient(new GoogleGenerativeAIHttpTransport(client, Model, new(Metadata, PrivateText)),
                1, new(MaximumBlocks: limits.GetProperty("blocks").GetInt32(), MaximumCharacters: limits.GetProperty("characters").GetInt32()));
            await using var run = settleAbort ? await chatClient.StartWithAbortSettlementAsync(Request()) : await chatClient.StartAsync(Request());
            var original = Drain(run);
            try
            {
                await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && !run.Completion.IsCompleted && !body.AsyncDisposed &&
                    !responseContent.Disposed && requestContent is { Disposed: false }, "Consumer failure escaped original held cleanup.");
                if (cancel) run.Cancel();
                Check(!original.IsCompleted && !run.Completion.IsCompleted, "Consumer abort detached original disposal.");
                body.ReleaseCleanup.TrySetResult(); var terminal = await original; var result = await run.Completion;
                AssertChannels(terminal, result, cancel ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.ResourceLimit,
                    failCleanup, cancel ? ChatFailureKind.Cancelled : ChatFailureKind.ResourceLimit);
                AssertPartial(result.Message, vector);
                Equal(1, sends); Equal(1, body.AsyncDisposeCalls);
                Check(body.AsyncDisposed && responseContent.Disposed && requestContent is { Disposed: true } && !handler.Disposed,
                    "Original request/response/body ownership was not joined.");
                if (!cancel) Equal("Google data exceeds configured limits.", result.Failure!.Message);
            }
            finally { body.ReleaseCleanup.TrySetResult(); await Join(original); }
        }
        // Agent uses the real default reducer limits. Admit one complete tool first,
        // then exceed the default character limit using a real Google text progress frame.
        foreach (var failCleanup in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            var prefix = """{"candidates":[{"content":{"parts":[{"functionCall":{"id":"one","name":"inspect","args":{}}}]}}]}""";
            var text = JsonSerializer.Serialize(new string('x', PiSharp.AI.PiRequestBudget.StreamCharacters + 1));
            var wire = "data: " + prefix + "\n\ndata: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + text +
                "}]},\"finishReason\":\"STOP\"}]}\n\n";
            var body = new OwnedBody(wire) { FailCleanup = failCleanup }; var executor = new Executor();
            var content = new BodyContent(body); RequestContent? requestContent = null; var sends = 0; StreamTerminalEvent? observed = null;
            using var handler = new Handler((request, _) => {
                sends++; requestContent = new(request.Content!); request.Content = requestContent;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            using var client = new HttpClient(handler);
            var options = new GoogleGenerativeAIOptions(Metadata, PrivateText) { MaximumContentCharacters = 2 * PiSharp.AI.PiRequestBudget.StreamCharacters,
                MaximumFrameCharacters = 2 * PiSharp.AI.PiRequestBudget.StreamCharacters, MaximumPayloadBytes = 2 * PiSharp.AI.PiRequestBudget.StreamCharacters };
            await using var agent = new NativeAgent(new(Model, new GoogleGenerativeAIHttpTransport(client, Model, options),
                [new("inspect", executor)], Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
                new Sink((value, _) => { if (value is TurnStreamObserved { Event: StreamTerminalEvent terminal }) observed = terminal; return ValueTask.CompletedTask; }),
                new(StreamCapacity: 1));
            Task<AgentLoopResult>? original = null;
            try
            {
                original = agent.PromptAsync(Inputs()); await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && observed is null && executor.Executions == 0, "Agent gained preterminal tool authority.");
                if (cancel) agent.Abort();
                Check(!original.IsCompleted && executor.Executions == 0, "Agent abort detached consumer cleanup.");
                body.ReleaseCleanup.TrySetResult(); var chat = (await original).Turns.Single().Result.Chat;
                var terminal = observed ?? throw new InvalidOperationException("Agent omitted failure terminal.");
                AssertChannels(terminal, chat, cancel ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.ResourceLimit,
                    failCleanup, cancel ? ChatFailureKind.Cancelled : ChatFailureKind.ResourceLimit, compareMessage: false);
                Equal(0L, chat.Message.Usage.TotalTokens); Equal("one", chat.Message.Content.OfType<ToolCallContent>().Single().Id);
                Equal(0, executor.Executions); Equal(1, sends); Equal(1, body.AsyncDisposeCalls);
                Check(body.AsyncDisposed && content.Disposed && requestContent is { Disposed: true } &&
                    !handler.Disposed && !agent.Snapshot.IsRunning, "Agent failed to join real ownership.");
            }
            finally { body.ReleaseCleanup.TrySetResult(); agent.Abort(); if (original is not null) await Join(original); await agent.WaitForIdleAsync(); }
        }
    }
    private static async Task SourceCleanupContexts()
    {
        foreach (var kind in new[] { "resource", "malformed", "source" })
        foreach (var cleanup in new[] { "clean", "distinct-fault", "same-instance-fault" })
        foreach (var cancel in new[] { false, true })
        {
            Exception primary = kind switch { "resource" => new StreamLimitException(PrivateText),
                "malformed" => new StreamProtocolException(PrivateText), _ => new IOException(PrivateText) };
            var transport = new ThrowingTransport(primary, cleanup == "clean" ? null :
                cleanup == "same-instance-fault" ? primary : new IOException(PrivateText));
            await using var run = await new ChatClient(transport, 1).StartAsync(Request()); var original = Drain(run);
            try
            {
                await transport.Reader.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && !run.Completion.IsCompleted, "Source failure detached physical cleanup.");
                if (cancel) run.Cancel();
                Check(!run.Completion.IsCompleted, "Source cancellation skipped disposal.");
                transport.Reader.ReleaseCleanup.TrySetResult(); var terminal = await original; var result = await run.Completion;
                var code = cancel ? NativeChatFailureCode.Cancelled : kind switch { "resource" => NativeChatFailureCode.ResourceLimit,
                    "malformed" => NativeChatFailureCode.MalformedStream, _ => NativeChatFailureCode.SourceFailed };
                var failureKind = cancel ? ChatFailureKind.Cancelled : kind switch { "resource" => ChatFailureKind.ResourceLimit,
                    "malformed" => ChatFailureKind.MalformedStream, _ => ChatFailureKind.Provider };
                AssertChannels(terminal, result, code, cleanup != "clean", failureKind);
                Equal(1, transport.Reader.DisposeCalls); Equal(2, transport.Reader.MoveCalls);
                Check(transport.Reader.Disposed, "Source owned reader disposal was not joined.");
            }
            finally { transport.Reader.ReleaseCleanup.TrySetResult(); await Join(original); }
        }
    }
    private sealed class ThrowingTransport(Exception primary, Exception? cleanup) : IChatTransport
    {
        internal readonly ThrowingReader Reader = new(primary, cleanup);
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken token = default)
        { Reader.Request = request; return Reader; }
    }
    private sealed class ThrowingReader(Exception primary, Exception? cleanup) : IAsyncEnumerable<StreamEvent>, IAsyncEnumerator<StreamEvent>
    {
        internal ChatRequest? Request;
        internal bool Disposed; internal int DisposeCalls, MoveCalls; private int _claimed;
        internal readonly TaskCompletionSource CleanupEntered = Gate(), ReleaseCleanup = Gate();
        public StreamEvent Current
        {
            get
            {
                var request = Request ?? throw new InvalidOperationException("Missing request.");
                return new StreamStarted(new(request.Model.Api, request.Model.Provider, request.Model.Id,
                    request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));
            }
        }
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken token = default)
        { Equal(0, Interlocked.Exchange(ref _claimed, 1)); return this; }
        public ValueTask<bool> MoveNextAsync()
        { if (++MoveCalls == 1) return ValueTask.FromResult(true); throw primary; }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true;
            if (cleanup is not null) throw cleanup;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        internal bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override void Dispose(bool disposing) { Disposed = true; body.Dispose(); base.Dispose(disposing); }
    }
    private sealed class RequestContent(HttpContent original) : HttpContent
    {
        internal bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => original.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class OwnedBody(string wire) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(wire); private int _offset;
        internal bool FailCleanup, AsyncDisposed; internal int AsyncDisposeCalls;
        internal readonly TaskCompletionSource CleanupEntered = Gate(), ReleaseCleanup = Gate();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = Math.Min(buffer.Length, _bytes.Length - _offset); _bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return ValueTask.FromResult(count);
        }
        public override async ValueTask DisposeAsync()
        {
            AsyncDisposeCalls++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; AsyncDisposed = true;
            if (FailCleanup) throw new IOException(PrivateText);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Executor : IToolExecutor
    {
        internal int Executions;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { Executions++; return ValueTask.FromResult(ToolResult.Success("authored")); }
    }
}
