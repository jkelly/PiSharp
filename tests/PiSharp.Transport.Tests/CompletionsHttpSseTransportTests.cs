using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

internal static class CompletionsHttpSseTransportTests
{
    private static readonly ModelDescriptor Model = new("http-completions-model", "openai-completions", "openai");
    private const string Key = "authored-inert-noncredential";
    private static readonly Uri Endpoint = new("https://completions.invalid/v1/chat/completions");
    private const string Finish = """{"choices":[{"delta":{},"finish_reason":"stop"}]}""";
    private const string Text = """{"id":"owned-response","choices":[{"delta":{"content":"Exact \u03c0\ud83d\ude00\u0000"}}]}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-http.all-three-recorded-sdk-requests-and-full-current-mapper-composition", RecordedSdkComposition);
        yield return ("completions-http.every-byte-fragmentation-tool-finals-and-awaited-terminal-cleanup", FragmentationAndCleanup);
        yield return ("completions-http.exact-done-eof-named-errors-and-strict-json-admission", FramingAndAdmission);
        yield return ("completions-http.lower-budgets-and-request-admission-before-send", BoundsAndRequestAdmission);
        yield return ("completions-http.cancellation-and-concurrent-run-disposal-join-owned-cleanup", CancellationAndDisposal);
        yield return ("completions-http.pull-pacing-send-body-status-faults-and-independent-requests", FaultsAndPullPacing);
    }

    private static async Task RecordedSdkComposition()
    {
        var root = FindRepo();
        var inputBytes = Pinned(root, "fixtures/reference/openai-completions-stream/input.json", "fad084d8113bdbc482e79bd9fbc0070bb8895d701fee489fdf0cdfcc180e2607");
        var expectedBytes = Pinned(root, "fixtures/reference/openai-completions-stream/expected.json", "3d8214d2471e67580a14fbc8441aa35721c559312463e570411aa01dba68183d");
        _ = Pinned(root, "tools/ReferenceOracle/openai-completions-stream.lock.json", "0f5f4ca5d6a6c93a95c4a0c27b640ff21c3460c9aef8e1112c0700c0605fffe9");
        using var input = JsonDocument.Parse(inputBytes); using var expected = JsonDocument.Parse(expectedBytes);
        var modelJson = input.RootElement.GetProperty("model"); var common = input.RootElement.GetProperty("commonOptions");
        var model = new ModelDescriptor(modelJson.GetProperty("id").GetString()!, modelJson.GetProperty("api").GetString()!, modelJson.GetProperty("provider").GetString()!);
        var cases = input.RootElement.GetProperty("cases"); var captured = expected.RootElement.GetProperty("observations").GetProperty("cases");
        Equal(3, cases.GetArrayLength()); Equal(3, captured.GetArrayLength());
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var item = cases[index]; var observation = captured[index]; var options = item.GetProperty("options");
            Equal(item.GetProperty("caseId").GetString(), observation.GetProperty("caseId").GetString());
            var fetch = observation.GetProperty("fetchRequests")[0]; Equal(1, observation.GetProperty("fetchRequests").GetArrayLength());
            var entries = item.GetProperty("context").GetProperty("messages").EnumerateArray().Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
            var request = new ChatRequest(model, entries, input.RootElement.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64());
            var originals = entries.Select(value => value.WireBody.ToString()).ToArray();
            var reasoning = item.TryGetProperty("modelOverrides", out var overrides) ? overrides.GetProperty("reasoning").GetBoolean() : modelJson.GetProperty("reasoning").GetBoolean();
            var factory = new CompletionsKeyAuthRequestFactory(new(fetch.GetProperty("url").GetString()!), model, new(Reasoning: reasoning),
                new(MaxTokens: common.GetProperty("maxTokens").GetDouble(), Temperature: common.GetProperty("temperature").GetDouble(),
                    ReasoningEffort: options.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null,
                    CacheRetention: options.TryGetProperty("cacheRetention", out var cache) && cache.GetString() == "long" ? CompletionsCacheRetention.Long : CompletionsCacheRetention.Short,
                    SessionId: options.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                    ToolChoice: options.TryGetProperty("toolChoice", out var choice) ? JsonData.FromElement(choice) : null,
                    ModelHeaders: JsonData.FromElement(modelJson.GetProperty("headers")), Headers: JsonData.FromElement(common.GetProperty("headers"))));
            var wire = observation.GetProperty("responseWire").GetProperty("sseText").GetString()!;
            Equal(observation.GetProperty("responseWire").GetProperty("utf8Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wire))));
            var stream = new ProbeStream(Encoding.UTF8.GetBytes(wire), Enumerable.Repeat(7, Encoding.UTF8.GetByteCount(wire) / 7 + 1).ToArray());
            var response = new StreamProbeContent(stream); HttpRequestMessage? sent = null; Exception? observationFailure = null;
            using var handler = new FakeHttpHandler(async (message, token) =>
            {
                try
                {
                    sent = message; Equal("POST", message.Method.Method); Equal(fetch.GetProperty("url").GetString(), message.RequestUri!.AbsoluteUri);
                    Equal("Bearer " + common.GetProperty("apiKey").GetString(), message.Headers.GetValues("authorization").Single());
                    Check(!message.Headers.Contains("x-remove"), "Null header removal was lost before send.");
                    using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                    Check(Same(fetch.GetProperty("bodyJson"), body.RootElement), "Complete genuine SDK request body differs at the actual fake-handler boundary.");
                    return new(HttpStatusCode.OK) { Content = response };
                }
                catch (Exception error) { observationFailure = error; throw; }
            });
            using var client = new HttpClient(handler);
            var mapperOptions = new OpenAICompletionsWireOptions(Rates: new(2, 6, 0.5m, 1));
            var transport = new CompletionsHttpSseTransport(client, (value, token) => factory.Create(value, common.GetProperty("apiKey").GetString()!, token), completionsOptions: mapperOptions);
            var actual = await Collect(transport.StreamAsync(request));
            if (observationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
            // Compare every event and final to the SAME production mapper fed every recorded SDK DTO.
            // This verifies HTTP composition without hiding the separate mapper/source parity gaps.
            var direct = await Collect(new OpenAICompletionsWireSource((_, token) => Recorded(item.GetProperty("wireChunks"), token), mapperOptions).StreamAsync(request));
            Equal(direct.Count, actual.Count);
            for (var frame = 0; frame < actual.Count; frame++)
                Check(Same(PiWireJson.WriteEvent(direct[frame]).Value, PiWireJson.WriteEvent(actual[frame]).Value), "HTTP composition changed a complete mapper frame: " + item.GetProperty("caseId").GetString() + ":" + frame);
            Equal(1, handler.SendCalls); Check(stream.Disposed && response.Disposed && !handler.Disposed, "Recorded replay changed HTTP ownership.");
            await ThrowsAsync<ObjectDisposedException>(() => sent!.Content!.ReadAsStringAsync());
            Check(originals.SequenceEqual(entries.Select(value => value.WireBody.ToString())), "HTTP request/replay modified canonical history.");
        }
    }

    private static async Task FragmentationAndCleanup()
    {
        const string tool = """{"choices":[{"delta":{"tool_calls":[{"index":9,"id":"call-owned","type":"function","function":{"name":"inspect","arguments":"{\"value\":1.00,\"keep\":null}"}}]}}]}""";
        var body = new GateStream(Encoding.UTF8.GetBytes(Sse(Text, tool, """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}""", "[DONE]")));
        using var fixture = new Fixture(body, new(Framing: new(ReadBufferBytes: 1, RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent)));
        using var cancellation = new CancellationTokenSource();
        var frames = new List<StreamEvent>(); var running = Drain(fixture.Transport.StreamAsync(Request(), cancellation.Token), frames);
        try
        {
            await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted && !frames.Any(value => value is StreamTerminalEvent or ToolCallEnded), "Finals escaped before HTTP asynchronous cleanup.");
            Check(!fixture.Response.Disposed, "Response was disposed before body cleanup finished.");
            _ = await fixture.Requests.Single().Content!.ReadAsStringAsync();
            body.ReleaseCleanup.TrySetResult(); await running.WaitAsync(Deadline);
            var final = (StreamDone)frames[^1]; Equal(StopReason.ToolUse, final.Reason);
            Equal("Exact \u03c0\U0001f600\0", ((TextContent)final.Message.Content[0]).Text);
            var call = (ToolCallContent)final.Message.Content[1]; Equal("call-owned", call.Id); Equal("inspect", call.Name);
            // openai-completions.ts:656 finalizes with parseStreamingJson (JSON.parse): 1.00 is the Number 1 (installed pi-ai 1.1.0).
            Equal("{\"value\":1,\"keep\":null}", call.Arguments.ToString()); Equal(8L, final.Message.Usage.TotalTokens);
            Equal(1, body.AsyncDisposeCalls); Check(body.Disposed && !body.SyncBeforeAsync && fixture.Response.Disposed, "Async body/response cleanup order changed.");
            Equal(0, fixture.Response.SerializeCalls); await ThrowsAsync<ObjectDisposedException>(() => fixture.Requests.Single().Content!.ReadAsStringAsync());
            Check(!fixture.Handler.Disposed, "Composition disposed the borrowed client.");
        }
        finally { cancellation.Cancel(); body.ReleaseCleanup.TrySetResult(); await Observe(running); }
    }

    private static async Task FramingAndAdmission()
    {
        foreach (var wire in new[] { Sse(Text, Finish), "data: " + Text + "\r\n\r\ndata: " + Finish,
            Sse(Text, Finish, "[DONE]", "{ignored-after-sentinel"), "event: thread.run\ndata: {\"error\":{\"private\":1}}\n\n" + Sse(Text, Finish) })
        {
            using var fixture = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes(wire), 1, 2, 3));
            Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is null, "Source-shaped EOF/sentinel/named-envelope framing failed.");
        }
        foreach (var (wire, failure) in new[]
        {
            (Sse("[DONE]", Finish), "UnexpectedEof"), (Sse(Text, "[DONE]"), "UnexpectedEof"),
            (Sse(" [DONE] "), "SourceFailed"), (Sse("[DONE]trailing"), "SourceFailed"),
            (Sse("{private-invalid"), "SourceFailed"), (Sse("[]"), "SourceFailed"),
            (Sse("""{"choices":[],"id":"a","\u0069d":"b"}"""), "SourceFailed"),
            (Sse("""{"choices":[],"opaque":"\ud800"}"""), "SourceFailed"),
            ("event: error\ndata: {\"private-message\":\"secret\"}\n\n" + Sse(Finish), "SourceFailed"),
            (Sse("""{"error":{"message":"private-message"}}""", Finish), "SourceFailed"),
            (Sse("""{"choices":[],"opaque":1e400}""", Finish), "MalformedStream")
        })
        {
            using var fixture = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes(wire)));
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request()); Failed(result, failure);
            // openai SDK Stream: an error event or a truthy data.error is shown as the SDK APIError message.
            if (wire.Contains("error", StringComparison.Ordinal)) Check(result.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() is "{\"private-message\":\"secret\"}" or "private-message", "SDK stream error message differs.");
            else Check(!result.Failure!.Message.Contains("private", StringComparison.Ordinal), "SSE failure leaked rejected payload.");
            Check(fixture.Response.Disposed, "Rejected frame retained its response.");
        }
        using (var fixture = new Fixture(new ProbeStream([.. Encoding.UTF8.GetBytes("data: "), 0xff, .. Encoding.UTF8.GetBytes("\n\n")])))
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "SourceFailed");

        // Shared decoder constraint: absent data fields are dropped, whereas SDK JSON.parse(empty) fails.
        // This witness remains a mandatory framing correction; it is not SDK-parity evidence.
        var eventOnly = new SseDecoder(new(RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent));
        var callbacks = 0;
        await foreach (var _ in eventOnly.DecodeAsync(new MemoryStream(Encoding.UTF8.GetBytes("event: error\n\n")))) callbacks++;
        Equal(0, callbacks);
        // An explicit empty data field IS dispatched and must fail admission.
        using (var fixture = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes("event: error\ndata:\n\n"))))
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "SourceFailed");
    }

    private static async Task BoundsAndRequestAdmission()
    {
        using (var exact = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes(Sse(Finish, "[DONE]"))),
            new(MaximumDataEvents: 2, MaximumDataCharacters: Finish.Length, MaximumTotalDataCharacters: Finish.Length + 6, MaximumJsonDepth: 4)))
            Check((await new ChatClient(exact.Transport).CompleteAsync(Request())).Failure is null, "Exact HTTP/SSE budget rejected a valid finish.");
        foreach (var options in new CompletionsHttpSseOptions[]
        {
            new(MaximumDataEvents: 1), new(MaximumDataCharacters: Finish.Length - 1),
            new(MaximumTotalDataCharacters: Finish.Length + 5), new(MaximumJsonDepth: 3),
            new(Framing: new(MaximumLineCharacters: 8, RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent))
        })
        {
            using var fixture = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes(Sse(Finish, "[DONE]"))), options);
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()), "SourceFailed"); Check(fixture.Response.Disposed, "Budget failure skipped response cleanup.");
        }
        using (var envelope = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes("event: thread." + new string('x', 64) + "\ndata: {}\n\n")), new(MaximumDataCharacters: 16)))
            Failed(await new ChatClient(envelope.Transport).CompleteAsync(Request()), "SourceFailed");
        using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP effect.")); using var client = new HttpClient(handler);
        var factoryCalls = 0;
        HttpRequestMessage Factory(ChatRequest request, CancellationToken token) { factoryCalls++; return new CompletionsKeyAuthRequestFactory(Endpoint, Model, options: new(MaximumPayloadBytes: 32)).Create(request, Key, token); }
        var transport = new CompletionsHttpSseTransport(client, Factory);
        Failed(await new ChatClient(transport).CompleteAsync(Request()), "SourceFailed"); Equal(1, factoryCalls); Equal(0, handler.SendCalls);
        foreach (var options in new CompletionsHttpSseOptions[] { new(MaximumDataEvents: 0), new(MaximumDataCharacters: 0), new(MaximumTotalDataCharacters: 0), new(MaximumJsonDepth: 65),
            new(Framing: new(RejectInvalidUtf8: false)), new(Framing: new(RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DiscardPendingEvent)) })
            Throws<ArgumentException>(() => new CompletionsHttpSseTransport(client, Factory, options));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await new ChatClient(transport).CompleteAsync(Request(), cancelled.Token); Equal(ChatFailureKind.Cancelled, result.Failure!.Kind);
        Equal(1, factoryCalls); Equal(0, handler.SendCalls);
        var invalidKey = new CompletionsHttpSseTransport(client, (request, token) => new CompletionsKeyAuthRequestFactory(Endpoint, Model).Create(request, "private\r\nkey", token));
        Failed(await new ChatClient(invalidKey).CompleteAsync(Request()), "SourceFailed"); Equal(0, handler.SendCalls);
    }

    private static async Task CancellationAndDisposal()
    {
        var body = new GateStream([]) { BlockRead = true }; using var fixture = new Fixture(body); using var cancellation = new CancellationTokenSource();
        var frames = new List<StreamEvent>(); var running = Drain(fixture.Transport.StreamAsync(Request(), cancellation.Token), frames);
        try
        {
            await body.ReadEntered.Task.WaitAsync(Deadline);
            // The invocation owns a linked cancellation authority so its own Cancel/Dispose can also cancel acquisition.
            Check(fixture.FactoryToken.CanBeCanceled, "Owned request acquisition received an uncancellable token.");
            Equal(fixture.FactoryToken, fixture.Response.AcquisitionToken);
            cancellation.Cancel();
            Check(fixture.FactoryToken.IsCancellationRequested && fixture.Response.AcquisitionToken.IsCancellationRequested,
                "External cancellation did not reach both owned acquisition boundaries.");
            await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted && !fixture.Response.Disposed, "Cancellation escaped asynchronous cleanup.");
            _ = await fixture.Requests.Single().Content!.ReadAsStringAsync();
            body.ReleaseCleanup.TrySetResult(); await running.WaitAsync(Deadline);
            Equal(StopReason.Aborted, ((StreamError)frames[^1]).Reason); Equal(1, body.AsyncDisposeCalls);
            Check(body.Disposed && fixture.Response.Disposed && !fixture.Handler.Disposed, "Canceled run retained ownership or disposed borrowed client.");
        }
        finally { cancellation.Cancel(); body.ReleaseCleanup.TrySetResult(); await Observe(running); }
        var earlyBody = new GateStream([]) { BlockRead = true }; using var early = new Fixture(earlyBody);
        var run = await new ChatClient(early.Transport, capacity: 1).StartAsync(Request());
        try
        {
            await earlyBody.ReadEntered.Task.WaitAsync(Deadline);
            var first = run.DisposeAsync().AsTask(); var second = run.DisposeAsync().AsTask();
            await earlyBody.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!first.IsCompleted && !second.IsCompleted && !early.Response.Disposed, "Concurrent disposal did not share real cleanup settlement.");
            earlyBody.ReleaseCleanup.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure!.Kind); Equal(1, earlyBody.AsyncDisposeCalls);
            Check(!earlyBody.SyncBeforeAsync && early.Response.Disposed, "Disposal reordered body ownership.");
        }
        finally { earlyBody.ReleaseCleanup.TrySetResult(); await run.DisposeAsync(); }
    }

    private static async Task FaultsAndPullPacing()
    {
        // A sentinel must close without requesting bytes from the held trailing read.
        var sentinelBody = new GateStream(Encoding.UTF8.GetBytes(Sse(Text, Finish, "[DONE]"))) { BlockAfterBytes = true };
        using (var fixture = new Fixture(sentinelBody))
        {
            using var cancellation = new CancellationTokenSource();
            var running = new ChatClient(fixture.Transport).CompleteAsync(Request(), cancellation.Token);
            try
            {
                await sentinelBody.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!sentinelBody.TrailingReadEntered.Task.IsCompleted && !running.IsCompleted, "Exact sentinel drained trailing bytes or skipped cleanup.");
                sentinelBody.ReleaseCleanup.TrySetResult(); Check((await running.WaitAsync(Deadline)).Failure is null, "Validated finish/sentinel failed.");
            }
            finally { cancellation.Cancel(); sentinelBody.ReleaseCleanup.TrySetResult(); await Observe(running); }
        }
        using (var fixture = new Fixture(new ProbeStream(Encoding.UTF8.GetBytes(Sse(Text, Finish)), 1), new(Framing: new(ReadBufferBytes: 1, RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent))))
        {
            await using var iterator = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            Check(await iterator.MoveNextAsync() && iterator.Current is StreamStarted, "Mapper start was not observed.");
            Check(await iterator.MoveNextAsync() && iterator.Current is TextStarted, "Text start missing after acquisition.");
            var reads = ((ProbeStream)fixture.Body).ReadCalls;
            Check(await iterator.MoveNextAsync() && iterator.Current is TextDelta, "Text delta missing."); Equal(reads, ((ProbeStream)fixture.Body).ReadCalls);
            // No queue/prefetch reads are made between the mapper's already available immutable frames.
            while (await iterator.MoveNextAsync()) { }
        }
        foreach (var stage in new[] { "factory", "send", "acquire", "read", "status", "cleanup" })
        {
            var stream = stage == "read" ? (Stream)new ReadFailureStream() : stage == "cleanup" ? new ThrowCleanupStream(Encoding.UTF8.GetBytes(Sse(Text, Finish, "[DONE]"))) : new ProbeStream(Encoding.UTF8.GetBytes(Sse(Text, Finish)));
            var response = new StreamProbeContent(stream, stage == "acquire" ? _ => throw new IOException("private acquire") : null);
            HttpRequestMessage? owned = null;
            using var handler = new FakeHttpHandler((_, _) => stage == "send" ? throw new IOException("private send") : Task.FromResult(new HttpResponseMessage(stage == "status" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = response }));
            using var client = new HttpClient(handler);
            var transport = new CompletionsHttpSseTransport(client, (request, token) => stage == "factory" ? throw new IOException("private factory") : owned = new CompletionsKeyAuthRequestFactory(Endpoint, Model).Create(request, Key, token));
            Failed(await new ChatClient(transport).CompleteAsync(Request()), "SourceFailed");
            if (owned is not null) await ThrowsAsync<ObjectDisposedException>(() => owned.Content!.ReadAsStringAsync());
            if (stage is not ("send" or "factory")) Check(response.Disposed, "Fault stage skipped response disposal: " + stage);
            // The rejected body is read once into the openai SDK APIError message.
            if (stage == "status") { Equal(1, response.AcquireCalls); Equal(0, response.SerializeCalls); }
            Equal(stage == "factory" ? 0 : 1, handler.SendCalls); Check(!handler.Disposed, "Fault disposed borrowed client.");
            response.Dispose();
        }
        var requests = new List<HttpRequestMessage>();
        using var freshHandler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamProbeContent(new ProbeStream(Encoding.UTF8.GetBytes(Sse(Finish)))) }));
        using var freshClient = new HttpClient(freshHandler); var factory = new CompletionsKeyAuthRequestFactory(Endpoint, Model);
        var repeated = new CompletionsHttpSseTransport(freshClient, (request, token) => { var value = factory.Create(request, Key, token); requests.Add(value); return value; });
        for (var index = 0; index < 2; index++) Check((await new ChatClient(repeated).CompleteAsync(Request())).Failure is null, "Repeated enumeration failed.");
        Equal(2, freshHandler.SendCalls); Check(!ReferenceEquals(requests[0], requests[1]) && !ReferenceEquals(requests[0].Content, requests[1].Content), "HTTP enumerations reused owned request/content.");
        foreach (var value in requests) await ThrowsAsync<ObjectDisposedException>(() => value.Content!.ReadAsStringAsync());
    }

    private sealed class Fixture : IDisposable
    {
        public Stream Body { get; }
        public StreamProbeContent Response { get; }
        public FakeHttpHandler Handler { get; }
        private readonly HttpClient _client;
        public CompletionsHttpSseTransport Transport { get; }
        public readonly List<HttpRequestMessage> Requests = [];
        public CancellationToken FactoryToken;
        public Fixture(Stream body, CompletionsHttpSseOptions? options = null)
        {
            Body = body; Response = new(body); Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Response })); _client = new(Handler);
            var factory = new CompletionsKeyAuthRequestFactory(Endpoint, Model);
            Transport = new(_client, (request, token) => { FactoryToken = token; var owned = factory.Create(request, Key, token); Requests.Add(owned); return owned; }, options);
        }
        public void Dispose() { _client.Dispose(); Response.Dispose(); }
    }
    private sealed class GateStream(byte[] bytes) : ProbeStream(bytes)
    {
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource TrailingReadEntered { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool BlockRead, BlockAfterBytes, SyncBeforeAsync;
        public int AsyncDisposeCalls;
        private Task? _cleanup;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            if (BlockRead) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0 && BlockAfterBytes) { TrailingReadEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return count;
        }
        public override ValueTask DisposeAsync() => new(_cleanup ??= CloseCore());
        private async Task CloseCore() { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; }
        protected override void Dispose(bool disposing) { if (disposing) { SyncBeforeAsync |= !Disposed; Disposed = true; } base.Dispose(disposing); }
    }
    private sealed class ThrowCleanupStream(byte[] bytes) : ProbeStream(bytes)
    { public override ValueTask DisposeAsync() { Disposed = true; throw new IOException("private cleanup"); } }
    private static ChatRequest Request() => new(Model, [new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"Owned request","timestamp":123}"""))], 123);
    private static string Sse(params string[] chunks) => string.Concat(chunks.Select(value => "data: " + value + "\r\n\r\n"));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async IAsyncEnumerable<JsonData> Recorded(JsonElement chunks, [EnumeratorCancellation] CancellationToken token)
    { foreach (var chunk in chunks.EnumerateArray()) { token.ThrowIfCancellationRequested(); yield return JsonData.FromElement(chunk); await Task.Yield(); } }
    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> source) { var values = new List<StreamEvent>(); await Drain(source, values); return values; }
    private static async Task Drain(IAsyncEnumerable<StreamEvent> source, List<StreamEvent> output) { await foreach (var value in source) output.Add(value); }
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static void Failed(ChatResult result, string failure)
    {
        Check(result.Failure is not null && result.Message.StopReason == StopReason.Error, "Failed HTTP source authorized a successful terminal.");
        Check(result.NativeDiagnostic is not null, "Failure classification was absent."); Equal(failure, result.NativeDiagnostic!.Code.ToString());
        Equal(result.NativeDiagnostic, result.Failure!.NativeDiagnostic);
        Check(!(result.Message.ExtraProperties?.TryGet("openAICompletionsFailure", out _) ?? false), "Native classification entered the Pi message.");
    }
    private static byte[] Pinned(string root, string path, string hash)
    { var file = new FileInfo(Path.Combine(root, path)); Check(file.Exists && file.Length <= 1_048_576, "Frozen reference is absent or oversized."); var bytes = File.ReadAllBytes(file.FullName); Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return bytes; }
    private static string FindRepo()
    { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "fixtures/reference/openai-completions-stream/input.json"))) return path.FullName; throw new InvalidOperationException("Frozen reference root absent."); }
    private static bool Same(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind && (left.ValueKind switch
    {
        JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() && left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var value) && Same(property.Value, value)),
        JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
        JsonValueKind.String => left.GetString() == right.GetString(), _ => left.GetRawText() == right.GetRawText()
    });
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
