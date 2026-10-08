using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class Program
{
    private static readonly ModelDescriptor Model = new("authored-text-model", "mistral-conversations", "mistral");
    private static MistralTextOptions Options => new(new Uri("https://authored.invalid/prefix/"), true, new(2, 4, 1, .5), "authored-offline-user-agent") { ApiKey = "authored-only-key" };
    private static ChatRequest Request => new(Model, [new("system", JsonData.Parse("{\"content\":\"Be brief\"}")), new("user", JsonData.Parse("{\"content\":\"Hi\"}"))], 123);
    private const string Text = "{\"id\":\"first\",\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}";
    private static string Finish(string reason = "stop") => "{\"id\":\"later\",\"choices\":[{\"finish_reason\":\"" + reason + "\",\"delta\":{}}]}";
    private const string Usage = "{\"choices\":[],\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":3,\"prompt_tokens_details\":{\"cached_tokens\":2},\"total_tokens\":11}}";
    private static string Frames(params string[] json) => string.Join("", json.Select(x => "data: " + x + "\n\n")) + "data: [DONE]\n\n";
    private static void Require(bool condition, [CallerLineNumber] int line = 0, string? subcase = null)
    { if (!condition) throw new MistralFixtureAssertionException(nameof(Program), line, subcase); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request, token); }
    }
    private sealed class Body(string text, bool failCleanup = false) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public bool Released { get; private set; }
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); if (failCleanup) throw new IOException("authored cleanup failure"); }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private static HttpResponseMessage Response(Body body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StreamContent(body), ReasonPhrase = "Authored status" };
    private sealed class HeldRead : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released { get; private set; }
        public HeldRead() : base(Encoding.UTF8.GetBytes(Frames(Text, Finish()))) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task; return await base.ReadAsync(buffer, token); }
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }
    private sealed class HeldDispose : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AsyncDisposals { get; private set; }
        public bool Settled { get; private set; }
        public HeldDispose() : base(Encoding.UTF8.GetBytes(Frames(Text, Finish(), Usage))) { }
        public override async ValueTask DisposeAsync()
        { AsyncDisposals++; Entered.TrySetResult(); await Release.Task; base.Dispose(); Settled = true; }
    }
    private sealed class SplitUtf8(string value) : MemoryStream(Encoding.UTF8.GetBytes(value))
    {
        public int Reads { get; private set; }
        public bool Released { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Reads++; return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], token); }
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }
    private sealed class RejectRead(Func<Exception> error) : MemoryStream
    {
        public bool Released { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => throw error();
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }
    private static async Task<List<StreamEvent>> Collect(IChatTransport transport, ChatRequest? request = null, CancellationToken token = default)
    { var list = new List<StreamEvent>(); await foreach (var item in transport.StreamAsync(request ?? Request, token)) list.Add(item); return list; }
    private static StreamTerminalEvent Terminal(List<StreamEvent> events) => (StreamTerminalEvent)events[^1];
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Expected optional --report and fresh path.");
        var results = new List<object>(); var failed = 0;
        async Task Check(string id, Func<Task> action)
        {
            try { await action(); results.Add(new { id, status = "passed" }); }
            catch (Exception error)
            {
                failed++;
                results.Add(new { id, status = "failed", diagnostic = MistralFixtureAssertionException.Diagnostic(error),
                    fixture = (error as MistralFixtureAssertionException)?.Fixture, line = (error as MistralFixtureAssertionException)?.Line,
                    subcase = (error as MistralFixtureAssertionException)?.Subcase });
            }
        }
        foreach (var (name, run) in MistralToolStreamingTests.Cases()) await Check(name, run);
        foreach (var (name, run) in MistralFactorySimpleTests.Cases()) await Check(name, run);
        foreach (var (name, run) in MistralReplayTests.Cases()) await Check(name, run);
        foreach (var (name, run) in MistralVisionTests.Cases()) await Check(name, run);
        await Check("bound-identity-before-any-callback-or-send", async () =>
        {
            var callbacks = 0; using var h = new Handler((_, _) => throw new InvalidOperationException()); using var client = new HttpClient(h);
            var t = new MistralTextHttpSseTransport(client, Model, Options with { OnPayload = (_, _, _) => { callbacks++; return ValueTask.FromResult<JsonData?>(null); } });
            try { await Collect(t, Request with { Model = Model with { Id = "other" } }); Require(false); } catch (MistralTextException e) { Require(e.Code == NativeChatFailureCode.UnsupportedFeature); }
            Require(callbacks == 0 && h.Calls == 0 && t.Models.Count == 1);
        });
        await Check("exact-request-hook-casing-bearer-and-trailing-usage", async () =>
        {
            var body = new Body(Frames(Text, Finish(), Usage)); var hooks = new List<string>();
            using var h = new Handler(async (request, token) =>
            {
                Require(request.RequestUri!.AbsoluteUri == "https://authored.invalid/prefix/v1/chat/completions" && request.Headers.Authorization!.ToString() == "Bearer authored-only-key");
                var json = JsonData.Parse(await request.Content!.ReadAsStringAsync()); Require(json.Value.GetProperty("max_tokens").GetDouble() == 17 && !json.Value.TryGetProperty("maxTokens", out _));
                hooks.Add("send"); return Response(body);
            }); using var client = new HttpClient(h);
            var options = Options with { MaxTokens = 17, Temperature = 0, OnPayload = (payload, _, _) => { Require(payload.Value.TryGetProperty("maxTokens", out _)); hooks.Add("payload"); return ValueTask.FromResult<JsonData?>(null); }, OnResponse = (_, _, _) => { hooks.Add("response"); return ValueTask.CompletedTask; }, OnProviderStreamEvent = (_, _, _) => { hooks.Add("chunk"); return ValueTask.CompletedTask; } };
            var events = await Collect(new MistralTextHttpSseTransport(client, Model, options)); var terminal = Terminal(events);
            Require(body.Released && terminal is StreamDone && events[^2] is TextEnded { Content: "Hello" } && h.Calls == 1);
            Require(terminal.Message.Usage.Input == 6 && terminal.Message.Usage.CacheRead == 2 && terminal.Message.Usage.Output == 3 && terminal.Message.Usage.TotalTokens == 11);
            var cost = terminal.Message.Usage.Cost; var binaryCost = cost.SourceBinary64Cost!.Value;
            Require(cost.Input == binaryCost.GetProperty("input").GetDecimal() && cost.Output == binaryCost.GetProperty("output").GetDecimal() &&
                cost.CacheRead == binaryCost.GetProperty("cacheRead").GetDecimal() && cost.Total == binaryCost.GetProperty("total").GetDecimal());
            Require(PiWireJson.ReadMessage(PiWireJson.WriteMessage(terminal.Message).Value).Usage.TotalTokens == 11);
            Require(terminal.Message.ExtraProperties!.TryGet("responseId", out var id) && id!.Value.GetString() == "first");
            Require(hooks.SequenceEqual(["payload", "send", "response", "chunk", "chunk", "chunk"]));
        });
        await Check("all-text-finish-reasons-and-text-arrays", async () =>
        {
            foreach (var reason in new[] { "stop", "length", "model_length" })
            {
                var body = new Body(Frames("{\"choices\":[{\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"\"},{\"type\":\"text\",\"text\":\"Hi\"}]}}]}", Finish(reason)));
                using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options)); Require(events.OfType<TextStarted>().Count() == 1 && events.OfType<TextDelta>().Count() == 1 && body.Released);
                Require(Terminal(events).Reason == (reason == "stop" ? StopReason.Stop : StopReason.Length));
            }
        });
        await Check("eight-boundary-patterns-and-eof-tail", async () =>
        {
            foreach (var separator in new[] { "\r\n\r\n", "\r\n\r", "\r\n\n", "\r\r\n", "\n\r\n", "\r\r", "\n\r", "\n\n" })
            {
                var body = new Body("data:\t" + Text + separator + "data: " + Finish()); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options)); Require(Terminal(events) is StreamDone && events[^2] is TextEnded && body.Released);
            }
        });
        await Check("missing-finish-and-provider-error", async () =>
        {
            foreach (var reason in new[] { "missing", "error", "unknown" })
            {
                var body = new Body(reason == "missing" ? Frames(Text) : Frames(Text, Finish(reason))); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options)); var terminal = Terminal(events);
                Require(terminal is StreamError && body.Released && terminal.NativeDiagnostic?.Adapter == NativeChatAdapter.MistralConversations && events[^2] is TextEnded);
                Require(terminal.NativeDiagnostic?.Code == (reason == "missing" ? NativeChatFailureCode.UnexpectedEof : NativeChatFailureCode.ProviderError));
            }
        });
        await Check("http-error-response-hook-before-body-and-source-truncation", async () =>
        {
            var body = new Body(new string('x', 4010)); var hook = false; using var h = new Handler((_, _) => Task.FromResult(Response(body, HttpStatusCode.BadRequest))); using var client = new HttpClient(h);
            var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options with { OnResponse = (_, _, _) => { Require(body.Position == 0); hook = true; return ValueTask.CompletedTask; } }));
            var terminal = Terminal(events); Require(hook && body.Released && !events.OfType<StreamStarted>().Any() && terminal.NativeDiagnostic?.Code == NativeChatFailureCode.ProviderError);
            Require(terminal.Message.ExtraProperties!.TryGet("errorMessage", out var error) && error!.Value.GetString() == "Mistral API error (400): " + new string('x', 4000) + "... [truncated 10 chars]");
        });
        await Check("request-unsupported-modes-before-send", async () =>
        {
            foreach (var entry in new[] { new TranscriptEntry("assistant", JsonData.Parse("{\"content\":\"replay\"}")), new TranscriptEntry("user", JsonData.Parse("{\"content\":[{\"type\":\"audio\",\"data\":\"fake\"}]}")) })
            {
                using var h = new Handler((_, _) => throw new InvalidOperationException()); using var client = new HttpClient(h);
                var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options), new(Model, [entry]));
                Require(Terminal(events).NativeDiagnostic?.Code == NativeChatFailureCode.UnsupportedFeature && h.Calls == 0,
                    subcase: entry.Role == "assistant" ? "assistant-scalar" : "user-audio");
            }
        });
        await Check("response-unsupported-modes-and-resource-limits", async () =>
        {
            var body = new Body(Frames("{\"choices\":[{\"delta\":{\"content\":[{\"type\":\"unsupported\",\"thinking\":[]}]}}]}")); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
            var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options)); Require(body.Released && Terminal(events).NativeDiagnostic?.Code == NativeChatFailureCode.UnsupportedFeature);
            var limitedBody = new Body(Frames(Text, Finish())); using var limitedHandler = new Handler((_, _) => Task.FromResult(Response(limitedBody))); using var limitedClient = new HttpClient(limitedHandler);
            events = await Collect(new MistralTextHttpSseTransport(limitedClient, Model, Options with { MaximumFrameCharacters = 10 })); Require(limitedBody.Released && Terminal(events).NativeDiagnostic?.Code == NativeChatFailureCode.ResourceLimit);
        });
        await Check("held-callback-original-operation-join", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var body = new Body(Frames(Text, Finish()));
            using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
            var pending = Collect(new MistralTextHttpSseTransport(client, Model, Options with { OnResponse = async (_, _, _) => { entered.SetResult(); await release.Task; } }));
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!pending.IsCompleted && !body.Released); }
            finally { release.TrySetResult(); await pending; }
            Require(body.Released && Terminal(await pending) is StreamDone);
        });
        await Check("caller-cancellation-joins-held-send", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var cancellation = new CancellationTokenSource();
            using var h = new Handler(async (_, _) => { entered.SetResult(); await release.Task; return Response(new Body(Frames(Text, Finish()))); }); using var client = new HttpClient(h);
            var pending = Collect(new MistralTextHttpSseTransport(client, Model, Options), token: cancellation.Token);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); Require(!pending.IsCompleted); }
            finally { release.TrySetResult(); await pending; }
            Require(Terminal(await pending) is StreamError { Reason: StopReason.Aborted, NativeDiagnostic.Code: NativeChatFailureCode.Cancelled });
        });
        await Check("caller-cancellation-joins-original-held-read", async () =>
        {
            var body = new HeldRead(); using var cancellation = new CancellationTokenSource();
            using var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })); using var client = new HttpClient(h);
            var pending = Collect(new MistralTextHttpSseTransport(client, Model, Options), token: cancellation.Token);
            try { await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); Require(!pending.IsCompleted && !body.Released); }
            finally { body.Release.TrySetResult(); await pending; }
            Require(body.Released && Terminal(await pending).Reason == StopReason.Aborted);
        });
        await Check("timeout-joins-send-and-is-not-caller-cancellation", async () =>
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var h = new Handler(async (_, token) => { using var registration = token.Register(() => cancelled.TrySetResult()); await release.Task; return Response(new Body(Frames(Text, Finish()))); }); using var client = new HttpClient(h);
            var pending = Collect(new MistralTextHttpSseTransport(client, Model, Options with { TimeoutMilliseconds = 50 }));
            try { await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!pending.IsCompleted); }
            finally { release.TrySetResult(); await pending; }
            var terminal = Terminal(await pending); Require(terminal.Reason == StopReason.Error && terminal.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed);
        });
        await Check("invalid-capability-cost-and-options-reject-before-send", () =>
        {
            using var h = new Handler((_, _) => throw new InvalidOperationException()); using var client = new HttpClient(h);
            foreach (var invalid in new[] { Options with { SupportsText = false }, Options with { Costs = new(double.NaN, 1, 1, 1) }, Options with { MaxTokens = double.PositiveInfinity } })
            { try { _ = new MistralTextHttpSseTransport(client, Model, invalid); Require(false); } catch (MistralTextException error) { Require(error.Code == NativeChatFailureCode.UnsupportedFeature); } }
            Require(h.Calls == 0); return Task.CompletedTask;
        });
        await Check("cleanup-failure-prevents-success", async () =>
        {
            var body = new Body(Frames(Text, Finish()), true); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
            var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options)); Require(body.Released && Terminal(events) is StreamError { NativeDiagnostic.Code: NativeChatFailureCode.CleanupFailed, NativeCleanupDiagnostic.Adapter: NativeChatAdapter.MistralConversations });
        });
        await Check("chatrun-generated-protocol-and-cleanup-diagnostics", async () =>
        {
            var result = await new ChatClient(new Broken()).CompleteAsync(Request); Require(result.NativeDiagnostic?.Adapter == NativeChatAdapter.MistralConversations && result.NativeDiagnostic?.Code == NativeChatFailureCode.MalformedStream);
            result = await new ChatClient(new TerminalThenCleanup()).CompleteAsync(Request); Require(result.NativeDiagnostic?.Code == NativeChatFailureCode.CleanupFailed && result.NativeCleanupDiagnostic?.Adapter == NativeChatAdapter.MistralConversations);
            using var caller = new CancellationTokenSource(); caller.Cancel(); result = await new ChatClient(new Broken()).CompleteAsync(Request, caller.Token); Require(result.NativeDiagnostic?.Code == NativeChatFailureCode.Cancelled);
        });
        await Check("replacement-depth-headers-and-cumulative-content-bounds", async () =>
        {
            foreach (var options in new[] {
                Options with { OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse("{\"model\":\"authored-text-model\",\"stream\":true,\"messages\":[],\"tools\":[{\"type\":\"unsupported\"}]}")) },
                Options with { MaximumJsonDepth = 1 },
                Options with { MaximumContentCharacters = 4 } })
            {
                using var h = new Handler((_, _) => throw new InvalidOperationException()); using var client = new HttpClient(h);
                var events = await Collect(new MistralTextHttpSseTransport(client, Model, options));
                var subcase = options.OnPayload is not null ? "replacement-tool-kind" : options.MaximumJsonDepth == 1 ? "request-depth" : "request-content";
                Require(h.Calls == 0 && Terminal(events) is StreamError, subcase: subcase);
                Require(Terminal(events).NativeDiagnostic?.Code == (options.OnPayload is null ? NativeChatFailureCode.ResourceLimit : NativeChatFailureCode.UnsupportedFeature), subcase: subcase);
            }
            // Admit the full immutable request, then exceed the separate output accumulation.
            var boundedText = "{\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 40) + "\"}}]}";
            var body = new Body(Frames(boundedText, boundedText, Finish())); using var handler = new Handler((_, _) => Task.FromResult(Response(body))); using var http = new HttpClient(handler);
            var shortRequest = new ChatRequest(Model, [new("user", JsonData.Parse("{\"content\":\"x\"}"))]);
            var output = await Collect(new MistralTextHttpSseTransport(http, Model, Options with { MaximumContentCharacters = 64 }), shortRequest);
            Require(handler.Calls == 1 && output.OfType<StreamStarted>().Any() && body.Released &&
                Terminal(output).NativeDiagnostic?.Code == NativeChatFailureCode.ResourceLimit && output[^2] is TextEnded ended && ended.Content == new string('x', 40), subcase: "output-accumulation");
            var headerBody = new Body(Frames(Text, Finish())); using var headerHandler = new Handler((_, _) => {
                var response = Response(headerBody); response.Headers.TryAddWithoutValidation("X-Authored", new string('x', 70)); return Task.FromResult(response);
            }); using var headerHttp = new HttpClient(headerHandler);
            var headerOutput = await Collect(new MistralTextHttpSseTransport(headerHttp, Model, Options with { MaximumHeaderCharacters = 64 }));
            Require(headerBody.Released && Terminal(headerOutput).NativeDiagnostic?.Code == NativeChatFailureCode.ResourceLimit && !headerOutput.OfType<StreamStarted>().Any(), subcase: "response-header");
        });
        await Check("atomic-chunk-failure-before-first-published-text", async () =>
        {
            foreach (var unsupported in new[] { true, false }) foreach (var throughClient in new[] { true, false })
            {
                var parts = unsupported ? "[{\"type\":\"text\",\"text\":\"Hi\"},{\"type\":\"unsupported\",\"thinking\":[]}]" : JsonSerializer.Serialize(new[] { new string('x', 40), new string('y', 40) });
                var body = new Body(Frames("{\"choices\":[{\"delta\":{\"content\":" + parts + "}}]}"));
                using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var transport = new MistralTextHttpSseTransport(client, Model, Options with { MaximumContentCharacters = 64 });
                var request = new ChatRequest(Model, [new("user", JsonData.Parse("{\"content\":\"x\"}"))]);
                var expected = unsupported ? NativeChatFailureCode.UnsupportedFeature : NativeChatFailureCode.ResourceLimit;
                var subcase = (unsupported ? "unsupported" : "resource") + (throughClient ? "-client" : "-stream");
                if (throughClient) { var result = await new ChatClient(transport).CompleteAsync(request); Require(result.NativeDiagnostic?.Code == expected && result.Message.Content.Length == 0, subcase: subcase); }
                else { var events = await Collect(transport, request); Require(Terminal(events).NativeDiagnostic?.Code == expected && !events.OfType<TextStarted>().Any() && !events.OfType<TextDelta>().Any() && !events.OfType<TextEnded>().Any(), subcase: subcase); }
                Require(h.Calls == 1 && body.Released, subcase: subcase);
            }
        });
        await Check("atomic-chunk-failure-retains-only-earlier-published-text", async () =>
        {
            foreach (var throughClient in new[] { true, false })
            {
                var bad = "{\"choices\":[{\"delta\":{\"content\":[\"unpublished\",{\"type\":\"unsupported\",\"thinking\":[]}]}}]}";
                var body = new Body(Frames(Text, bad)); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var transport = new MistralTextHttpSseTransport(client, Model, Options);
                if (throughClient) { var result = await new ChatClient(transport).CompleteAsync(Request); Require(result.NativeDiagnostic?.Code == NativeChatFailureCode.UnsupportedFeature && result.Message.Content is [TextContent { Text: "Hello" }]); }
                else { var events = await Collect(transport); Require(Terminal(events).NativeDiagnostic?.Code == NativeChatFailureCode.UnsupportedFeature && events.OfType<TextDelta>().Single().Delta == "Hello" && events[^2] is TextEnded { Content: "Hello" }); }
                Require(body.Released);
            }
        });
        await Check("foreign-hook-send-read-shape-exceptions-are-source-failures", async () =>
        {
            Func<Exception>[] errors = [() => new InvalidOperationException("authored private detail"), () => new KeyNotFoundException("authored private detail"), () => new JsonException("authored private detail")];
            foreach (var error in errors) foreach (var phase in new[] { "payload", "response", "provider", "send", "read" }) foreach (var throughClient in new[] { false, true })
            {
                var body = new Body(Frames(Text, Finish()), phase == "provider"); var rejectedRead = new RejectRead(error);
                using var h = new Handler((_, _) => phase == "send" ? throw error() : Task.FromResult(phase == "read" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(rejectedRead) } : Response(body)));
                using var client = new HttpClient(h);
                var options = Options with {
                    OnPayload = phase == "payload" ? (_, _, _) => throw error() : null,
                    OnResponse = phase == "response" ? (_, _, _) => throw error() : null,
                    OnProviderStreamEvent = phase == "provider" ? (_, _, _) => throw error() : null };
                var transport = new MistralTextHttpSseTransport(client, Model, options);
                StreamTerminalEvent terminal;
                if (throughClient) { var result = await new ChatClient(transport).CompleteAsync(Request); Require(result.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed); if (phase == "provider") Require(result.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed); Require(!PiWireJson.WriteMessage(result.Message).ToString().Contains("authored private detail")); }
                else { terminal = Terminal(await Collect(transport)); Require(terminal.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed); if (phase == "provider") Require(terminal.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed); Require(!PiWireJson.WriteMessage(terminal.Message).ToString().Contains("authored private detail")); }
                if (phase is "response" or "provider") Require(body.Released);
                if (phase == "read") Require(rejectedRead.Released);
                if (phase == "payload") Require(h.Calls == 0);
            }
            // Owned shape/JSON admission still diagnoses malformed stream data.
            foreach (var invalid in new[] { "{", "{\"choices\":[{}]}" })
            {
                var body = new Body(Frames(invalid)); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var result = await new ChatClient(new MistralTextHttpSseTransport(client, Model, Options)).CompleteAsync(Request);
                Require(body.Released && result.NativeDiagnostic?.Code == NativeChatFailureCode.MalformedStream);
            }
        });
        await Check("early-consumer-disposal-joins-original-release", async () =>
        {
            var body = new HeldDispose(); using var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })); using var client = new HttpClient(h);
            var reader = new MistralTextHttpSseTransport(client, Model, Options).StreamAsync(Request).GetAsyncEnumerator();
            Task? disposal = null;
            try
            {
                Require(await reader.MoveNextAsync() && reader.Current is StreamStarted);
                Require(await reader.MoveNextAsync() && reader.Current is TextStarted);
                Require(await reader.MoveNextAsync() && reader.Current is TextDelta);
                disposal = reader.DisposeAsync().AsTask();
                await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!disposal.IsCompleted && !body.Settled);
            }
            finally { body.Release.TrySetResult(); if (disposal is not null) await disposal; else await reader.DisposeAsync(); }
            Require(body.Settled && body.AsyncDisposals == 1 && h.Calls == 1);
        });
        await Check("terminal-waits-for-held-asynchronous-body-disposal", async () =>
        {
            foreach (var throughClient in new[] { false, true })
            {
                var body = new HeldDispose(); using var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })); using var client = new HttpClient(h);
                var transport = new MistralTextHttpSseTransport(client, Model, Options); var observed = new List<StreamEvent>();
                async Task Consume() { await foreach (var frame in transport.StreamAsync(Request)) { Require(frame is not StreamTerminalEvent || body.Settled, subcase: "disposal-stream-terminal"); observed.Add(frame); } }
                Task pending = throughClient ? new ChatClient(transport).CompleteAsync(Request) : Consume();
                try { await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!pending.IsCompleted && !body.Settled && !observed.OfType<TextEnded>().Any(), subcase: throughClient ? "disposal-client-held" : "disposal-stream-held"); }
                finally { body.Release.TrySetResult(); await pending; }
                Require(body.Settled && body.AsyncDisposals == 1, subcase: throughClient ? "disposal-client-settled" : "disposal-stream-settled");
                if (!throughClient) Require(Terminal(observed) is StreamDone && observed[^2] is TextEnded && Terminal(observed).Message.Usage.TotalTokens == 11, subcase: "disposal-stream-usage");
            }
        });
        await Check("held-payload-and-provider-callbacks-join-on-cancellation", async () =>
        {
            foreach (var payload in new[] { true, false })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var caller = new CancellationTokenSource();
                var body = new Body(Frames(Text, Finish())); using var h = new Handler((_, _) => Task.FromResult(Response(body))); using var client = new HttpClient(h);
                var options = Options with {
                    OnPayload = payload ? async (_, _, _) => { entered.TrySetResult(); await release.Task; return null; } : null,
                    OnProviderStreamEvent = payload ? null : async (_, _, _) => { entered.TrySetResult(); await release.Task; } };
                var pending = Collect(new MistralTextHttpSseTransport(client, Model, options), token: caller.Token);
                try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); Require(!pending.IsCompleted && !body.Released && h.Calls == (payload ? 0 : 1)); }
                finally { release.TrySetResult(); await pending; }
                Require(Terminal(await pending).NativeDiagnostic?.Code == NativeChatFailureCode.Cancelled);
                if (!payload) Require(body.Released);
            }
        });
        await Check("split-byte-utf8-bom-and-multibyte-text", async () =>
        {
            // C# escapes become real UTF-8 bytes at runtime, with one byte per read.
            var unicode = "\u00e9\U0001f642";
            var chunk = "{\"choices\":[{\"delta\":{\"content\":\"" + unicode + "\"}}]}";
            var body = new SplitUtf8("\uFEFF" + Frames(chunk, Finish(), Usage));
            using var h = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })); using var client = new HttpClient(h);
            var events = await Collect(new MistralTextHttpSseTransport(client, Model, Options));
            Require(body.Released && body.Reads > 20, subcase: "split-read-release");
            Require(string.Concat(events.OfType<TextDelta>().Select(x => x.Delta)) == unicode, subcase: "split-decoded-text");
            Require(events[^2] is TextEnded ended && ended.Content == unicode, subcase: "split-text-end");
            Require(Terminal(events) is StreamDone && Terminal(events).Message.Usage.TotalTokens == 11, subcase: "split-trailing-usage");
        });
        var report = JsonSerializer.Serialize(new { scope = "Authored fake HTTP only; no source differential/catalog/durable qualification", tests = results.Count, passed = results.Count - failed, failed, results }); Console.WriteLine(report);
        if (args.Length == 2) { await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write); await using var writer = new StreamWriter(file); await writer.WriteLineAsync(report); }
        return failed == 0 ? 0 : 1;
    }
    private sealed class Broken : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default) { await Task.CompletedTask; yield return new TextDelta(0, "invalid-before-start"); }
    }
    private sealed class TerminalThenCleanup : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask; var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);
            try { yield return new StreamStarted(message); yield return new StreamDone(StopReason.Stop, message with { StopReason = StopReason.Stop }); }
            finally { throw new IOException("authored release fault"); }
        }
    }
}
