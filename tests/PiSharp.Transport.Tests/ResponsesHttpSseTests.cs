using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

internal static class ResponsesHttpSseTests
{
    private const string Unicode = "\u6587\U0001F642";
    private const string ToolStart = """{"type":"response.output_item.added","output_index":9,"item":{"type":"function_call","id":"fc-http","call_id":"call-http","name":"inspect","arguments":""}}""";
    private const string Completed = """{"type":"response.completed","response":{"id":"response-http","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}}""";
    private const string Created = """{"type":"response.created","response":{"id":"response-http"}}""";
    private static readonly string FinalArguments = "{\"text\":\"" + Unicode + "\",\"exact\":9007199254740993,\"scale\":1.0}";
    // openai-responses-shared.ts:716 finalizes item.arguments with parseStreamingJson (JSON.parse): binary64 Numbers, so 2^53 + 1
    // reads as 2^53 and 1.0 as 1 (captured from the installed pi-ai 1.1.0 against a local fake server).
    private static readonly string ParsedFinalArguments = "{\"text\":\"" + Unicode + "\",\"exact\":9007199254740992,\"scale\":1}";

    public static async Task FragmentedMappingAndOwnedCleanup()
    {
        var textStart = """{"type":"response.output_item.added","output_index":4,"item":{"type":"message","id":"msg-http","content":[]}}""";
        var textDelta = "{\"type\":\"response.output_text.delta\",\"output_index\":4,\"item_id\":\"msg-http\",\"delta\":\"" + Unicode + "\"}";
        var textEnd = JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 4,
            item = new { type = "message", id = "msg-http", content = new[] { new { type = "output_text", text = Unicode } } } });
        var toolDelta = """{"type":"response.function_call_arguments.delta","output_index":9,"item_id":"fc-http","delta":"{\"text\":\"partial"}""";
        var body = new HttpCleanupGateStream(Bytes(Sse(textStart, textDelta, textEnd, ToolStart, toolDelta, ToolEnd(FinalArguments), Completed, "[DONE]")));
        using var fixture = new Fixture(body, new(Framing: new(ReadBufferBytes: 1, RejectInvalidUtf8: true)));
        var observed = new List<StreamEvent>();
        var chatRequest = Request();
        var run = Drain(fixture.Transport.StreamAsync(chatRequest), observed);
        try
        {
            await body.CleanupEntered.Task;
            Check(!run.IsCompleted && observed.All(value => value is not StreamDone), "Done escaped before HTTP cleanup.");
            Check(!fixture.ResponseContent.Disposed && !fixture.Requests.Single().Disposed,
                "Response or owned request was disposed before asynchronous body cleanup.");
            body.ReleaseCleanup.TrySetResult();
            await run;
            var final = observed.OfType<StreamDone>().Single();
            Equal(StopReason.ToolUse, final.Reason);
            Equal(Unicode, ((TextContent)final.Message.Content[0]).Text);
            var call = (ToolCallContent)final.Message.Content[1];
            Equal("call-http|fc-http", call.Id);
            Equal("inspect", call.Name);
            Equal(ParsedFinalArguments, call.Arguments.Value.GetRawText());
            Equal("9007199254740992", call.Arguments.Value.GetProperty("exact").GetRawText());
            Equal("1", call.Arguments.Value.GetProperty("scale").GetRawText());
            Equal(5L, final.Message.Usage.Input);
            Equal(3L, final.Message.Usage.Output);
            Equal(8L, final.Message.Usage.TotalTokens);
            Equal(Unicode, observed.OfType<TextDelta>().Single().Delta);
            Equal(1, fixture.Handler.SendCalls);
            Equal(1, fixture.FactoryCalls);
            Check(ReferenceEquals(chatRequest, fixture.LastChatRequest), "Factory did not receive the caller's immutable request.");
            Check(body.ReadCalls > Unicode.Length && body.Disposed && !body.SyncDisposedBeforeAsync,
                "Fragmented body did not settle asynchronous cleanup first.");
            Equal(1, body.AsyncDisposeCalls);
            Equal(0, fixture.ResponseContent.SerializeCalls);
            Check(fixture.Requests.Single().DisposedAfterResponse && !fixture.Handler.Disposed,
                "Owned request preceded response cleanup or the borrowed client was disposed.");
        }
        finally
        {
            body.ReleaseCleanup.TrySetResult();
            try { await run; } catch { /* Release cleanup before preserving a failed assertion. */ }
        }
    }

    public static async Task InvalidDataAndTerminationCannotSucceed()
    {
        foreach (var sse in new[]
        {
            Sse("{private-invalid"),
            Sse("[]"),
            Sse("""{"type":"response.created","response":{"id":"private-id","id":"duplicate"}}"""),
            Sse("""{"type":"response.created","response":{"id":"\uD800"}}"""),
            Sse(ToolStart, ToolEnd(FinalArguments), "[DONE]", Completed),
            Sse(ToolStart, ToolEnd(FinalArguments)),
            Sse(" [DONE] ")
        })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(sse)));
            var result = await new ChatClient(fixture.Observed, capacity: 1).CompleteAsync(Request());
            Failure(result, ChatFailureKind.Provider);
            ResponsesRawContractAssertions.Error(fixture.Frames, typeof(StreamProtocolException));
            Check(!result.Failure!.Message.Contains("private", StringComparison.Ordinal), "JSON failure leaked source data.");
            Check(fixture.Requests.Single().DisposedAfterResponse && fixture.ResponseContent.Disposed,
                "Rejected JSON/termination retained HTTP ownership.");
        }
        using (var fixture = new Fixture(new ProbeStream([.. Bytes("data: "), 0xff, .. Bytes("\n\n")])))
        {
            Failure(await new ChatClient(fixture.Observed).CompleteAsync(Request()), ChatFailureKind.Provider);
            ResponsesRawContractAssertions.Error(fixture.Frames, typeof(StreamProtocolException));
            Check(fixture.ResponseContent.Disposed && fixture.Requests.Single().DisposedAfterResponse, "Invalid UTF8 escaped owned cleanup.");
        }
        // Pinned parseToolCallArguments finishes malformed argument text with {} plus argumentsError; this is transport output,
        // not evidence of execution. Agent.Tests/Sync150Cases and CompletionsHttpTurnTests cover rejection before tool execution.
        using (var repaired = new Fixture(new ProbeStream(Bytes(Sse(ToolStart, ToolEnd("{\"private-argument\":"), Completed)))))
        {
            var result = await new ChatClient(repaired.Observed).CompleteAsync(Request());
            Check(result.Failure is null && result.Message.StopReason == StopReason.ToolUse, "Malformed argument text failed transport finalization.");
            var tool = (ToolCallContent)result.Message.Content.Single();
            Check(tool.Arguments.ToString() == "{}" && tool.ExtraProperties!.Values["argumentsError"].Value.GetString() ==
                "Unexpected end of JSON input\n\nReceived arguments:\n{\"private-argument\":", "Malformed argument marker or empty object changed.");
            Check(repaired.Frames.OfType<ToolCallEnded>().Count() == 1 && repaired.Frames.OfType<StreamDone>().Count() == 1 &&
                repaired.ResponseContent.Disposed && repaired.Requests.Single().DisposedAfterResponse, "Tool argument finalization duplicated ends or skipped cleanup.");
        }
        using (var loneId = new Fixture(new ProbeStream(Bytes(Sse("""{"type":"response.created","response":{"id":"\uD800"}}""", Completed)))))
            Check((await new ChatClient(loneId.Observed).CompleteAsync(Request())).Failure is null, "Valid escaped response metadata failed a real completion.");

        // EOF and an exact sentinel are both supported source ends after a validated completion DTO.
        foreach (var suffix in new[] { Array.Empty<string>(), new[] { "[DONE]", "{ignored-after-sentinel" } })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(Sse([Completed, .. suffix]))));
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request());
            Check(result.Failure is null && result.Message.StopReason == StopReason.Stop,
                "Completed DTO did not survive the documented EOF/sentinel boundary.");
        }

        using (var fixture = new Fixture(new ProbeStream(Bytes("private-error-body")), status: HttpStatusCode.TooManyRequests))
        {
            Failure(await new ChatClient(fixture.Observed).CompleteAsync(Request()), ChatFailureKind.Provider);
            var rejected = ResponsesRawContractAssertions.Error(fixture.Frames, typeof(HttpRequestException));
            Check(rejected.NativeSourceException is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } && fixture.Frames.OfType<StreamStarted>().Count() == 0, "Status rejection lost original status or emitted Start.");
            Equal(1, fixture.ResponseContent.AcquireCalls);
            Equal(0, fixture.ResponseContent.SerializeCalls);
            Check(fixture.Requests.Single().DisposedAfterResponse, "HTTP rejection retained its owned request.");
            Equal(1, fixture.Handler.SendCalls);
        }
    }

    public static async Task AdmissionLimitsAndOptions()
    {
        using (var fixture = new Fixture(new ProbeStream(Bytes(Sse(Created, Completed, "[DONE]"))),
            new(MaximumDataEvents: 3, MaximumDataCharacters: Completed.Length,
                MaximumTotalDataCharacters: Created.Length + Completed.Length + 6, MaximumJsonDepth: 3)))
            Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is null,
                "Exact event/character/depth boundaries rejected a supported response.");
        foreach (var options in new ResponsesHttpSseOptions[]
        {
            new(MaximumDataEvents: 2),
            new(MaximumDataCharacters: Completed.Length - 1),
            new(MaximumTotalDataCharacters: Created.Length + Completed.Length + 5),
            new(MaximumJsonDepth: 2),
            new(Framing: new(MaximumLineCharacters: 8, RejectInvalidUtf8: true))
        })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(Sse(Created, Completed, "[DONE]"))), options);
            Failure(await new ChatClient(fixture.Observed).CompleteAsync(Request()), ChatFailureKind.Provider);
            ResponsesRawContractAssertions.Error(fixture.Frames, typeof(StreamLimitException));
            Check(fixture.Requests.Single().DisposedAfterResponse && fixture.ResponseContent.Disposed, "Admission failure retained owned HTTP work.");
        }
        using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected send."));
        using var client = new HttpClient(handler);
        var factoryCalls = 0;
        HttpRequestMessage Factory(ChatRequest _) { factoryCalls++; return new(HttpMethod.Post, "https://synthetic.invalid/responses"); }
        foreach (var options in new ResponsesHttpSseOptions[]
        {
            new(MaximumDataEvents: 0), new(MaximumDataCharacters: 0), new(MaximumTotalDataCharacters: 0),
            new(MaximumJsonDepth: PiSharp.Contracts.JsonData.MaximumDepth + 1), new(Framing: new(RejectInvalidUtf8: false))
        })
            Throws<ArgumentException>(() => new ResponsesHttpSseTransport(client, Factory, options));
        Equal(0, factoryCalls);
        Equal(0, handler.SendCalls);
    }

    public static async Task CooperativeCancellationAwaitsCleanup()
    {
        var body = new HttpCleanupGateStream([]) { BlockRead = true };
        using var fixture = new Fixture(body);
        using var cancellation = new CancellationTokenSource();
        var run = new ChatClient(fixture.Transport).CompleteAsync(Request(), cancellation.Token);
        try
        {
            await body.ReadEntered.Task;
            cancellation.Cancel();
            await body.CleanupEntered.Task;
            Check(!run.IsCompleted && !fixture.Requests.Single().Disposed && !fixture.ResponseContent.Disposed,
                "Cancellation propagated or released request/response before body cleanup.");
            body.ReleaseCleanup.TrySetResult();
            Failure(await run, ChatFailureKind.Cancelled);
            Check(fixture.Requests.Single().DisposedAfterResponse && body.Disposed,
                "Cancellation retained owned resources.");
            Check(!fixture.Handler.Disposed, "Cancellation disposed the borrowed client.");
        }
        finally
        {
            body.ReleaseCleanup.TrySetResult();
            try { await run; } catch { /* Settle owned resources before preserving an assertion. */ }
        }
        using var before = new CancellationTokenSource(); before.Cancel();
        using var unused = new Fixture(new ProbeStream([]));
        Failure(await new ChatClient(unused.Transport).CompleteAsync(Request(), before.Token), ChatFailureKind.Cancelled);
        Equal(0, unused.FactoryCalls);
        Equal(0, unused.Handler.SendCalls);
    }

    public static async Task EarlyDisposalAwaitsCleanup()
    {
        var body = new HttpCleanupGateStream(Bytes(Sse(ToolStart, ToolEnd(FinalArguments), Completed)));
        using var fixture = new Fixture(body);
        var reader = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current is StreamStarted, "Missing authored start.");
        Equal(1, fixture.FactoryCalls); Equal(1, fixture.Handler.SendCalls); Equal(0, fixture.ResponseContent.AcquireCalls);
        Check(await reader.MoveNextAsync() && reader.Current is ToolCallStarted, "Missing mapped tool start.");
        var cleanup = reader.DisposeAsync().AsTask();
        try
        {
            await body.CleanupEntered.Task;
            Check(!cleanup.IsCompleted && !fixture.Requests.Single().Disposed && !fixture.ResponseContent.Disposed,
                "Early stop did not await body cleanup before owned request disposal.");
            body.ReleaseCleanup.TrySetResult();
            await cleanup;
            Check(fixture.Requests.Single().DisposedAfterResponse && body.Disposed && !body.SyncDisposedBeforeAsync,
                "Early stop leaked or reordered resources.");
            Equal(1, body.AsyncDisposeCalls);
            Equal(1, fixture.Handler.SendCalls);
        }
        finally { body.ReleaseCleanup.TrySetResult(); await cleanup; }
    }

    public static async Task FactorySendAndSourceFaults()
    {
        var sendOriginal = new HttpRequestException("private-send-error");
        using var handler = new FakeHttpHandler((_, _) => throw sendOriginal);
        using var client = new HttpClient(handler);
        // Pinned wrapper preserves caller/source exception identity and display text; malformed DTO checks above remain sanitized.
        var factoryOriginal = new InvalidOperationException("private-factory-error");
        var throwingFactory = new ResponsesHttpSseTransport(client, _ => throw factoryOriginal);
        var factoryFrames = new List<StreamEvent>();
        var factoryFailure = await new ChatClient(new ResponsesRawFrameObserver(throwingFactory, factoryFrames)).CompleteAsync(Request());
        Failure(factoryFailure, ChatFailureKind.Provider);
        var factoryError = ResponsesRawContractAssertions.Error(factoryFrames, typeof(InvalidOperationException));
        Check(ReferenceEquals(factoryOriginal, factoryError.NativeSourceException) && factoryFailure.Failure!.Message == factoryOriginal.Message &&
            factoryFrames.OfType<StreamStarted>().Count() == 0, "Factory original exception/display or pre-Start ordering changed.");
        var nullFactory = new ResponsesHttpSseTransport(client, _ => null!);
        var nullFrames = new List<StreamEvent>();
        Failure(await new ChatClient(new ResponsesRawFrameObserver(nullFactory, nullFrames)).CompleteAsync(Request()), ChatFailureKind.Provider);
        ResponsesRawContractAssertions.Error(nullFrames, typeof(StreamProtocolException));
        Equal(0, nullFrames.OfType<StreamStarted>().Count());
        Equal(0, handler.SendCalls);
        var requestContent = new OwnedRequestContent(() => true);
        var sendFailure = new ResponsesHttpSseTransport(client, _ => new(HttpMethod.Post, "https://synthetic.invalid/responses") { Content = requestContent });
        var sendFrames = new List<StreamEvent>();
        var sendResult = await new ChatClient(new ResponsesRawFrameObserver(sendFailure, sendFrames)).CompleteAsync(Request());
        Failure(sendResult, ChatFailureKind.Provider);
        var sendError = ResponsesRawContractAssertions.Error(sendFrames, typeof(HttpRequestException));
        Check(ReferenceEquals(sendOriginal, sendError.NativeSourceException) && sendError.NativeSourceTask?.IsFaulted == true && sendFrames.OfType<StreamStarted>().Count() == 0,
            "Original HTTP send failure/task or acquisition ordering changed.");
        Check(requestContent.Disposed && !handler.Disposed, "Send fault retained the owned request or disposed the client.");
        Equal(1, handler.SendCalls);

        var body = new HttpCleanupGateStream([]) { FailRead = true };
        using (var fixture = new Fixture(body))
        {
            var run = new ChatClient(fixture.Observed).CompleteAsync(Request());
            try
            {
                await body.CleanupEntered.Task;
                Check(!run.IsCompleted && !fixture.Requests.Single().Disposed, "Read fault escaped before cleanup.");
                body.ReleaseCleanup.TrySetResult();
                Failure(await run, ChatFailureKind.Provider);
                var readError = ResponsesRawContractAssertions.Error(fixture.Frames, typeof(IOException));
                Check(readError.NativeSourceTask?.IsFaulted == true, "Original read task was lost.");
                Check(fixture.Requests.Single().DisposedAfterResponse && fixture.ResponseContent.Disposed, "Read fault retained owned HTTP work.");
            }
            finally { body.ReleaseCleanup.TrySetResult(); try { await run; } catch { } }
        }
        foreach (var asynchronous in new[] { false, true })
        {
            var original = new IOException(asynchronous ? "inert asynchronous cleanup failure" : "inert synchronous cleanup failure");
            var cleanupBody = new CleanupFailureStream(original, asynchronous);
            using var fixture = new Fixture(cleanupBody);
            var result = await new ChatClient(fixture.Observed).CompleteAsync(Request());
            Failure(result, ChatFailureKind.Provider);
            Check(fixture.Frames.OfType<StreamDone>().Count() == 0 && fixture.Frames.OfType<StreamTerminalEvent>().Count() == 1 && fixture.Frames.Last() is StreamError,
                "Cleanup failure produced executable success or duplicate terminals.");
            var cleanup = (StreamError)fixture.Frames.Last();
            Check(cleanup.NativeCleanupExceptions?.Count == 1 && ReferenceEquals(cleanup.NativeCleanupExceptions[0], original) && cleanup.NativeSourceException is null,
                "Original cleanup exception was replaced or mislabeled as a source failure.");
            // A synchronously throwing ValueTask method never returned a task; the raw contract must not fabricate one.
            if (asynchronous)
                Check(cleanup.NativeCleanupTasks?.Count == 1 && cleanup.NativeCleanupTasks[0].IsFaulted &&
                    ReferenceEquals(cleanup.NativeCleanupTasks[0].Exception!.InnerExceptions.Single(), original), "Original faulted asynchronous cleanup task was lost.");
            else Check(cleanup.NativeCleanupTasks is null or { Count: 0 }, "Synchronous cleanup fabricated an original task.");
            Check(result.Failure!.Message == original.Message && cleanupBody.Attempts == 1 && fixture.Requests.Single().DisposedAfterResponse,
                "Body cleanup error text, invocation count or owned request disposal changed.");
        }

        // One adapter may be enumerated repeatedly; the factory must supply a fresh owned request each time.
        using var freshHandler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamProbeContent(new ProbeStream(Bytes(Sse(Completed)))) }));
        using var freshClient = new HttpClient(freshHandler);
        var requests = new List<HttpRequestMessage>();
        var contents = new List<OwnedRequestContent>();
        var repeated = new ResponsesHttpSseTransport(freshClient, _ =>
        {
            var content = new OwnedRequestContent(() => true); contents.Add(content);
            var request = new HttpRequestMessage(HttpMethod.Post, "https://synthetic.invalid/responses") { Content = content };
            requests.Add(request); return request;
        });
        for (var index = 0; index < 2; index++)
            Check((await new ChatClient(repeated).CompleteAsync(Request())).Failure is null, "Repeated adapter enumeration failed.");
        Equal(2, freshHandler.SendCalls);
        Check(!ReferenceEquals(requests[0], requests[1]) && contents.All(value => value.Disposed),
            "Repeated enumeration reused or retained factory requests.");
    }

    private sealed class CleanupFailureStream(Exception original, bool asynchronous) : ProbeStream([])
    {
        internal int Attempts;
        public override ValueTask DisposeAsync()
        {
            Attempts++;
            if (!asynchronous) throw original;
            return new ValueTask(FailAsync());
        }
        private async Task FailAsync() { await Task.Yield(); throw original; }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly StreamProbeContent ResponseContent;
        public readonly FakeHttpHandler Handler;
        public readonly HttpClient Client;
        public readonly ResponsesHttpSseTransport Transport;
        public readonly List<OwnedRequestContent> Requests = [];
        public readonly List<StreamEvent> Frames = [];
        public IChatTransport Observed => new ResponsesRawFrameObserver(Transport, Frames);
        public int FactoryCalls;
        public ChatRequest? LastChatRequest;

        public Fixture(Stream body, ResponsesHttpSseOptions? options = null, HttpStatusCode status = HttpStatusCode.OK)
        {
            ResponseContent = new(body);
            Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = ResponseContent }));
            Client = new(Handler);
            Transport = new(Client, request =>
            {
                FactoryCalls++; LastChatRequest = request;
                var content = new OwnedRequestContent(() => ResponseContent.Disposed); Requests.Add(content);
                return new(HttpMethod.Post, "https://synthetic.invalid/responses") { Content = content };
            }, options);
        }

        public void Dispose() { Client.Dispose(); ResponseContent.Dispose(); }
    }

    private sealed class OwnedRequestContent(Func<bool> responseDisposed) : ByteArrayContent([])
    {
        public bool Disposed;
        public bool DisposedAfterResponse;
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposed = true; DisposedAfterResponse = responseDisposed(); }
            base.Dispose(disposing);
        }
    }

    private static ChatRequest Request() => new(new("synthetic-http-model", "openai-responses", "fixture-provider"), [], 123);
    private static string ToolEnd(string arguments) => JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 9,
        item = new { type = "function_call", id = "fc-http", call_id = "call-http", name = "inspect", arguments } });
    private static string Sse(params string[] data) => string.Concat(data.Select(value => "event: provider\r\ndata: " + value + "\r\n\r\n"));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static async Task Drain(IAsyncEnumerable<StreamEvent> source, List<StreamEvent> output)
    { await foreach (var value in source) output.Add(value); }
    private static void Failure(ChatResult result, ChatFailureKind kind)
    {
        Equal(kind, result.Failure!.Kind);
        Check(result.Message.StopReason is StopReason.Error or StopReason.Aborted, "Fault exposed a successful assistant terminal.");
        Check(result.Message.StopReason != StopReason.ToolUse, "Fault authorized ToolUse.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
