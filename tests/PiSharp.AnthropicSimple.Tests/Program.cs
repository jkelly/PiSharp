using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Authored deterministic expectations from pinned source text; no genuine source capture claim.
internal static class Program
{
    private static readonly ModelDescriptor Model = new("claude-simple", "anthropic-messages", "anthropic");
    private const string Key = "authored-inert-key";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new (string Id, Func<Task> Run)[]
        {
            ("anthropic-simple.whole-bodies-caps-budgets-efforts-toolchoice", Bodies),
            ("anthropic-simple.context-estimates-prefix-usage-and-clamping", Context),
            ("anthropic-simple.admission-cancellation-and-immutability", Admission),
            ("anthropic-simple.direct-fake-http-joined-owner", () => Integration("direct")),
            ("anthropic-simple.chat-client-fake-http-joined-owner", () => Integration("chat")),
            ("anthropic-simple.agent-fake-http-joined-owner", () => Integration("agent")),
            ("anthropic-simple.agent-successive-prompts-recompute-context", AgentPrompts),
            ("anthropic-simple.chat-cancel-held-cleanup-original-join", CancelledCleanup),
            ("anthropic-simple.direct-early-dispose-original-join", EarlyDispose),
            ("anthropic-simple.string-content-exact-source-body", StringBody),
            ("anthropic-simple.direct-body-failure-retains-primary-and-original-owners", () => PrimaryFailure("direct")),
            ("anthropic-simple.chat-body-failure-retains-primary-and-original-owners", () => PrimaryFailure("chat")),
            ("anthropic-simple.agent-body-failure-retains-primary-and-original-owners", () => PrimaryFailure("agent"))
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run(); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString(),
                ownership = error is IntegrationFailureException integration ? integration.Ownership : null }); }
        }
        if (args.Length == 2)
        {
            await using var report = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(report, new { sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
                status = "AUTHORED NATIVE; SOURCE QUALIFICATION OPEN", failures, results, genuineSourceCasesCaptured = 0 }, new JsonSerializerOptions { WriteIndented = true });
        }
        else foreach (var result in results) Console.WriteLine(JsonSerializer.Serialize(result));
        return failures == 0 ? 0 : 1;
    }
    private static AnthropicMessagesSimpleOptions Options(string? level = null, int cap = 32000, int window = 1000000,
        bool adaptive = false, string? map = null, bool reasoning = true) => new(
            JsonData.FromElement(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["id"] = Model.Id, ["api"] = Model.Api, ["provider"] = Model.Provider, ["maxTokens"] = cap,
                ["contextWindow"] = window, ["reasoning"] = reasoning, ["compat"] = new { forceAdaptiveThinking = adaptive },
                ["thinkingLevelMap"] = map is null ? null : JsonSerializer.Deserialize<JsonElement>(map)
            })),
            new(cap, CacheRetention: AnthropicCacheRetention.None, SupportsEagerToolInputStreaming: false), level) { ApiKey = Key };
    // These cases freeze block-array bodies. Source preserves the caller's string/array
    // distinction when cache retention is None; author their input in the same form.
    private static TranscriptEntry User(string text = "ask", long timestamp = 1) => new("user", JsonData.FromElement(
        JsonSerializer.SerializeToElement(new { role = "user", content = new[] { new { type = "text", text } }, timestamp })));
    private static ChatRequest Request(params TranscriptEntry[] messages) => new(Model, messages.ToImmutableArray(), 123);
    private static string Expected(int cap, string suffix = "", string text = "ask") =>
        "{\"model\":\"claude-simple\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) +
        "}]}],\"max_tokens\":" + cap.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"stream\":true" + suffix + "}";
    private static string Enabled(int budget) => ",\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":" +
        budget.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"display\":\"summarized\"}";
    private const string Disabled = ",\"thinking\":{\"type\":\"disabled\"}";

    private static async Task StringBody()
    {
        using var client = new HttpClient(new RejectSend());
        var factory = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, Options());
        var input = new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"ask","timestamp":1}"""));
        var before = input.WireBody.ToString();
        using var request = factory.Create(Request(input), Key);
        const string expected = """{"model":"claude-simple","messages":[{"role":"user","content":"ask"}],"max_tokens":32000,"stream":true,"thinking":{"type":"disabled"}}""";
        Check(await request.Content!.ReadAsStringAsync() == expected, "String-form whole compact source body differs.");
        Check(input.WireBody.ToString() == before, "Caller string-form transcript mutated.");
    }

    private static async Task Bodies()
    {
        using var client = new HttpClient(new RejectSend());
        async Task Body(AnthropicMessagesSimpleOptions options, string expected, string text = "ask")
        {
            var request = Request(User(text)); var before = request.Messages[0].WireBody.ToString(); var metadata = options.ModelMetadata.ToString();
            var factory = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, options);
            using var actual = factory.Create(request, Key);
            Check(await actual.Content!.ReadAsStringAsync() == expected, "Whole compact body differs: " + await actual.Content.ReadAsStringAsync());
            Check(actual.RequestUri!.AbsoluteUri == "https://anthropic.invalid/v1/messages?beta=true" && actual.Headers.GetValues("x-api-key").Single() == Key, "Direct factory binding differs.");
            Check(request.Messages[0].WireBody.ToString() == before && options.ModelMetadata.ToString() == metadata, "Caller data mutated.");
        }
        await Body(Options(), Expected(32000, Disabled));
        await Body(Options() with { ProjectionOptions = Options().ProjectionOptions with { ThinkingEnabled = true } }, Expected(32000, Disabled));
        await Body(Options() with { MaxTokens = 2000 }, Expected(2000, Disabled));
        await Body(Options() with { MaxTokens = 40000 }, Expected(40000, Disabled));
        foreach (var (level, budget) in new[] { ("minimal", 1024), ("low", 2048), ("medium", 8192), ("high", 16384), ("xhigh", 16384), ("max", 16384) })
        {
            await Body(Options(level), Expected(32000, Enabled(budget)));
            await Body(Options(level) with { MaxTokens = 2000 }, Expected(2000 + budget, Enabled(budget)));
        }
        await Body(Options("high", cap: 10000), Expected(10000, Enabled(8976)));
        await Body(Options("high") with { MaxTokens = 30000 }, Expected(32000, Enabled(16384)));
        await Body(Options("max", cap: 10000) with { MaxTokens = 5000, ThinkingBudgets = JsonData.Parse("""{"high":4000}""") }, Expected(9000, Enabled(4000)));
        await Body(Options("low") with { ThinkingBudgets = JsonData.Parse("""{"low":0}""") }, Expected(32000, Enabled(1024)));
        await Body(Options("high", window: 5000), Expected(903, Enabled(1024)));
        await Body(Options("high", window: 6500), Expected(2403, Enabled(1379)));
        await Body(Options("high", window: 4096), Expected(1, Enabled(1024)));
        await Body(Options(window: 0) with { MaxTokens = 50 }, Expected(50, Disabled));
        foreach (var (level, effort) in new[] { ("minimal", "low"), ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "high"), ("max", "high") })
            await Body(Options(level, adaptive: true) with { MaxTokens = 2000 }, Expected(2000, ",\"thinking\":{\"type\":\"adaptive\",\"display\":\"summarized\"},\"output_config\":{\"effort\":\"" + effort + "\"}"));
        await Body(Options("xhigh", adaptive: true, map: """{"xhigh":"max"}""") with { ThinkingBudgets = JsonData.Parse("""{"high":1}""") },
            Expected(32000, ",\"thinking\":{\"type\":\"adaptive\",\"display\":\"summarized\"},\"output_config\":{\"effort\":\"max\"}"));
        await Body(Options("max", adaptive: true, map: """{"max":null}"""), Expected(32000, ",\"thinking\":{\"type\":\"adaptive\",\"display\":\"summarized\"},\"output_config\":{\"effort\":\"high\"}"));
        await Body(Options(map: """{"off":null}"""), Expected(32000));
        await Body(Options(adaptive: true), Expected(32000, Disabled));
        await Body(Options(reasoning: false), Expected(32000));
        await Body(Options("high", reasoning: false) with { MaxTokens = 2000 }, Expected(18384));
        var temperature = Options() with { ProjectionOptions = Options().ProjectionOptions with { Temperature = 0.25m }, ToolChoice = JsonData.Parse("\"none\"") };
        await Body(temperature, Expected(32000, ",\"temperature\":0.25" + Disabled + ",\"tool_choice\":{\"type\":\"none\"}"));
        await Body(temperature with { Reasoning = "minimal", ToolChoice = JsonData.Parse("""{"type":"tool","name":"inspect"}""") },
            Expected(32000, Enabled(1024) + ",\"tool_choice\":{\"type\":\"tool\",\"name\":\"inspect\"}"));
    }

    private static Task Context()
    {
        using var client = new HttpClient(new RejectSend());
        AnthropicMessagesSimpleResolution Resolve(AnthropicMessagesSimpleOptions options, params TranscriptEntry[] messages) =>
            new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, options).Resolve(Request(messages));
        TranscriptEntry Assistant(long timestamp, int total, StopReason stop = StopReason.Stop) => new("assistant", PiWireJson.WriteMessage(
            new(Model.Api, Model.Provider, Model.Id, timestamp, [new TextContent("old")], new(70, 20, 5, 5, total, new(0, 0, 0, 0, 0)), stop)));
        var system = new TranscriptEntry("system", JsonData.Parse("""{"role":"system","content":"prefix","timestamp":3}"""));
        // Pi 1.1.0 estimate.ts divides characters by 3.5 instead of 4 (for example "12345678" is 3 tokens, text plus image 1373).
        var current = Resolve(Options(window: 5000), User(), Assistant(2, 100), User("12345678", 3));
        Check(current.ContextEstimate == new AnthropicMessagesContextUsageEstimate(103, 100, 3, 1) && current.MaxTokens == 801, "Applicable usage plus trailing estimation differs.");
        Check(Resolve(Options(), User(), Assistant(2, 0)).ContextEstimate == new AnthropicMessagesContextUsageEstimate(100, 100, 0, 1), "Zero total fallback differs.");
        Check(Resolve(Options(), system, Assistant(2, 100)).ContextEstimate == new AnthropicMessagesContextUsageEstimate(3, 0, 3, null), "Newer prefix must invalidate stale usage.");
        foreach (var stop in new[] { StopReason.Error, StopReason.Aborted })
            Check(Resolve(Options(), User(), Assistant(2, 100, stop)).ContextEstimate.Tokens == 2, "Failed usage was selected.");
        var content = new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":[{"type":"text","text":"abcd"},{"type":"image","data":"inert","mimeType":"image/png"}],"timestamp":1}"""));
        Check(Resolve(Options(), content).ContextEstimate.Tokens == 1373, "Image estimate differs.");
        var tool = new TranscriptEntry("assistant", JsonData.Parse("""{"role":"assistant","content":[{"type":"thinking","thinking":"abc"},{"type":"toolCall","name":"x","arguments":{"n":1}}],"timestamp":1,"stopReason":"error","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0}}"""));
        Check(Resolve(Options(), tool).ContextEstimate.Tokens == 4, "Thinking/tool JSON estimate differs.");
        var sections = new TranscriptEntry("system", JsonData.Parse("""{"role":"system","content":"a","sections":{"part":"b","removed":null},"toolsAdded":["x"],"toolsRemoved":["y"],"timestamp":1}"""));
        Check(Resolve(Options(), sections).ContextEstimate.Tokens == 6, "System sections/declaration estimates differ.");
        var bounded = Resolve(Options("high", window: 5000), User());
        Check(bounded.MaxTokens == 903 && bounded.ThinkingBudgetTokens == 0, "Second context clamp/answer room differs.");
        return Task.CompletedTask;
    }

    private static Task Admission()
    {
        using var client = new HttpClient(new RejectSend());
        AnthropicMessagesSimpleRequestFactory Factory(AnthropicMessagesSimpleOptions options) => new(client, new("https://anthropic.invalid"), Model, options);
        Throws<AnthropicMessagesSimpleException>(() => Factory(Options("off")));
        Throws<AnthropicMessagesSimpleException>(() => Factory(Options() with { ThinkingBudgets = JsonData.Parse("""{"high":-1}""") }));
        Throws<AnthropicMessagesSimpleException>(() => Factory(Options() with { ThinkingBudgets = JsonData.Parse("""{"high":1.5}""") }));
        Throws<AnthropicMessagesSimpleException>(() => Factory(Options()).Resolve(Request(User()) with { Model = Model with { Id = "other" } }));
        Throws<AnthropicMessagesSimpleException>(() => Factory(Options() with { MaximumContextMessages = 1 }).Resolve(Request(User(), User())));
        var factory = Factory(Options() with { ApiKey = null });
        Throws<AnthropicMessagesKeyAuthRequestException>(() => factory.StreamAsync(Request(User())));
        Throws<AnthropicMessagesKeyAuthRequestException>(() => factory.StreamAsync(new(Model, default)));
        Throws<AnthropicMessagesKeyAuthRequestException>(() => factory.Create(Request(User()), "sk-ant-oat-authored"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => factory.Create(Request(User()), Key, cancellation.Token));
        Check(!Options().ToString().Contains(Key, StringComparison.Ordinal), "Simple diagnostics contain explicit key.");
        return Task.CompletedTask;
    }

    private static async Task Integration(string consumer, bool injectBodyMismatch = false)
    {
        var body = new OwnedBody(Wire(), gated: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
        var options = Options("low") with { MaxTokens = 2000 };
        var factory = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, options);
        var request = Request(User(injectBodyMismatch ? "authored-mismatch" : "ask")); var before = request.Messages[0].WireBody.ToString(); var terminals = 0; var committed = 0;
        AssistantMessage? finalMessage = null; ChatFailure? chatFailure = null;
        using var cancellation = new CancellationTokenSource();
        NativeAgent? agent = null; Task operation;
        async Task Direct()
        {
            await foreach (var frame in factory.StreamAsync(request, cancellation.Token))
                if (frame is StreamTerminalEvent terminal) { finalMessage = terminal.Message; Check(body.Disposed && handler.ContentDisposed, "Terminal preceded cleanup."); Check(terminal is StreamDone && terminal.Reason == StopReason.Stop, "Unexpected terminal."); Check(terminal.Message.Content.OfType<TextContent>().Single().Text == "ok", "Direct terminal text differs."); terminals++; }
        }
        async Task Chat()
        {
            var result = await new ChatClient(factory).CompleteAsync(request, cancellation.Token);
            finalMessage = result.Message; chatFailure = result.Failure;
            Check(result.Failure is null && result.Message.Content.OfType<TextContent>().Single().Text == "ok" && body.Disposed, "Chat completion differs."); terminals++;
        }
        if (consumer == "agent")
        {
            agent = new(new(Model, factory, []), () => 123, new Sink(observation =>
            { if (observation is AssistantMessageEnded ended) { finalMessage = ended.Message; Check(body.Disposed && handler.ContentDisposed, "Agent commit preceded cleanup."); committed++; } }));
            operation = agent.PromptAsync(request.Messages[0], cancellation.Token);
        }
        else operation = consumer == "direct" ? Direct() : Chat();
        var failures = new List<Exception>();
        try
        {
            await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!operation.IsCompleted && terminals == 0 && committed == 0, "Original operation escaped held cleanup.");
            Check(handler.Bodies.Single() == Expected(4048, Enabled(2048)), "Fake HTTP complete Simple body differs.");
            body.ReleaseCleanup.TrySetResult(); await operation.WaitAsync(Deadline);
            Check(consumer == "agent" ? committed == 1 && agent!.Snapshot.Messages.Length == 2 : terminals == 1, "Consumer terminal/commit count differs.");
            Check(finalMessage is { StopReason: StopReason.Stop } && finalMessage.Content.OfType<TextContent>().Single().Text == "ok" &&
                finalMessage.Usage.Input == 10 && finalMessage.Usage.Output == 2 && finalMessage.Usage.TotalTokens == 12,
                "Successful consumer assistant text/reason/usage differs.");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            body.ReleaseCleanup.TrySetResult();
            try { if (!operation.IsCompleted) cancellation.Cancel(); } catch (Exception error) { failures.Add(error); }
            // Join the original operation even after an assertion failure. A cancellation
            // or terminal assertion here is secondary and must not erase the primary.
            try { await operation; } catch (Exception error) { failures.Add(error); }
            try { if (agent is not null) await agent.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        }
        var requestDisposed = false;
        try
        {
            Check(body.AsyncDisposeCalls == 1 && body.Disposed && handler.ContentDisposed && handler.Bodies.Count == 1, "Original HTTP owners were not joined exactly once.");
            await DisposedRequest(handler.Requests.Single()); requestDisposed = true;
            Check(request.Messages[0].WireBody.ToString() == before, "Caller transcript changed during integration.");
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new IntegrationFailureException(failures, new
        {
            consumer, originalOperationCompleted = operation.IsCompleted, asyncDisposeCalls = body.AsyncDisposeCalls,
            bodyDisposed = body.Disposed, contentDisposed = handler.ContentDisposed, originalRequestContentDisposed = requestDisposed,
            completeActualBodies = handler.Bodies.ToArray(), inputBefore = before, inputAfter = request.Messages[0].WireBody.ToString(),
            completeAssistant = finalMessage is null ? null : PiWireJson.WriteMessage(finalMessage), completeChatFailure = chatFailure
        });
    }

    private static async Task PrimaryFailure(string consumer)
    {
        try { await Integration(consumer, injectBodyMismatch: true); }
        catch (IntegrationFailureException error)
        {
            Check(error.InnerExceptions[0].Message == "Fake HTTP complete Simple body differs.", "Original body assertion was masked by join/cleanup failure.");
            var ownership = JsonSerializer.SerializeToElement(error.Ownership);
            Check(ownership.GetProperty("originalOperationCompleted").GetBoolean() && ownership.GetProperty("asyncDisposeCalls").GetInt32() == 1 &&
                ownership.GetProperty("bodyDisposed").GetBoolean() && ownership.GetProperty("contentDisposed").GetBoolean() &&
                ownership.GetProperty("originalRequestContentDisposed").GetBoolean(), "Primary failure control did not settle its original owners.");
            Check(ownership.GetProperty("completeActualBodies").GetArrayLength() == 1 &&
                ownership.GetProperty("inputBefore").GetString() == ownership.GetProperty("inputAfter").GetString(), "Failure lost complete actuals or changed caller input.");
            return;
        }
        throw new InvalidOperationException("Injected complete-body mismatch unexpectedly passed.");
    }

    private sealed class IntegrationFailureException(IEnumerable<Exception> failures, object ownership)
        : AggregateException("Anthropic integration failure; primary assertion retained and original operations joined.", failures)
    { internal object Ownership { get; } = ownership; }

    private static async Task AgentPrompts()
    {
        var body = new OwnedBody(Wire()); using var handler = new Handler(body, () => new OwnedBody(Wire())); using var client = new HttpClient(handler);
        var factory = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, Options(window: 5000));
        var hooks = new AgentHooks(PrepareRequest: (snapshot, _) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript, 123)));
        await using var agent = new NativeAgent(new(Model, factory, [], Hooks: hooks), () => 123, new Sink());
        var first = User(); var before = first.WireBody.ToString();
        await agent.PromptAsync(first); var retained = agent.Snapshot;
        await agent.PromptAsync(User("12345678", 124));
        Check(handler.Bodies.Count == 2 && retained.Messages.Length == 2 && agent.Snapshot.Messages.Length == 4, "Successive Agent prompts differ.");
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Check(second.RootElement.GetProperty("max_tokens").GetInt32() == 889, "Current usage plus trailing input was not resolved again.");
        Check(second.RootElement.GetProperty("messages").GetArrayLength() == 3 && first.WireBody.ToString() == before, "Canonical replay changed.");
        Check(handler.AllBodies.All(value => value.Disposed && value.AsyncDisposeCalls == 1), "Agent successive original owners were not joined.");
        foreach (var request in handler.Requests) await DisposedRequest(request);
    }

    private static async Task CancelledCleanup()
    {
        var body = new OwnedBody(Wire(), gated: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
        var factory = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, Options("minimal"));
        using var cancellation = new CancellationTokenSource();
        var operation = new ChatClient(factory).CompleteAsync(Request(User()), cancellation.Token);
        async Task Join()
        {
            try { var result = await operation; Check(result.Message.StopReason == StopReason.Aborted, "Cancellation retained a successful assistant."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        try
        {
            await body.CleanupEntered.Task.WaitAsync(Deadline); cancellation.Cancel();
            Check(!operation.IsCompleted && !body.Disposed, "Canceled original run escaped held cleanup.");
            body.ReleaseCleanup.TrySetResult(); await Join().WaitAsync(Deadline);
        }
        finally { body.ReleaseCleanup.TrySetResult(); cancellation.Cancel(); await Join(); }
        Check(body.Disposed && body.AsyncDisposeCalls == 1 && handler.ContentDisposed && handler.Bodies.Count == 1, "Canceled HTTP owner not joined exactly once.");
        await DisposedRequest(handler.Requests.Single());
    }

    private static async Task EarlyDispose()
    {
        var body = new OwnedBody(Wire(), gated: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
        var factory = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Model, Options());
        using var cancellation = new CancellationTokenSource();
        var iterator = factory.StreamAsync(Request(User()), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Task? disposal = null;
        try
        {
            while (await iterator.MoveNextAsync()) if (iterator.Current is TextDelta) break;
            Check(handler.Bodies.Count == 1, "Early disposal control did not acquire a real HTTP body.");
            disposal = iterator.DisposeAsync().AsTask();
            await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!disposal.IsCompleted && !body.Disposed, "Early iterator disposal escaped held cleanup.");
            body.ReleaseCleanup.TrySetResult(); await disposal.WaitAsync(Deadline);
        }
        finally
        {
            body.ReleaseCleanup.TrySetResult(); cancellation.Cancel();
            if (disposal is not null) await disposal; else await iterator.DisposeAsync();
        }
        Check(body.Disposed && body.AsyncDisposeCalls == 1 && handler.ContentDisposed, "Early return did not join original owner.");
        await DisposedRequest(handler.Requests.Single());
    }

    private static async Task DisposedRequest(HttpRequestMessage request)
    {
        try { await request.Content!.ReadAsStringAsync(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Original request content remained undisposed.");
    }

    private static string Wire() => string.Join("", new[]
    {
        ("message_start", """{"type":"message_start","message":{"id":"authored-response","role":"assistant","model":"claude-simple","content":[],"usage":{"input_tokens":10,"output_tokens":0}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        ("message_stop", """{"type":"message_stop"}""")
    }.Select(frame => "event: " + frame.Item1 + "\ndata: " + frame.Item2 + "\n\n"));
    private sealed class RejectSend : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Body-only control must not send."); }
    private sealed class Handler(OwnedBody first, Func<OwnedBody>? next = null) : HttpMessageHandler
    {
        internal readonly List<string> Bodies = []; internal readonly List<OwnedBody> AllBodies = [];
        internal readonly List<HttpRequestMessage> Requests = [];
        internal bool ContentDisposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request); Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            Check(request.Headers.GetValues("x-api-key").Single() == Key, "Inert explicit-key binding differs.");
            var body = Bodies.Count == 1 ? first : next!(); AllBodies.Add(body);
            return new(HttpStatusCode.OK) { Content = new BodyContent(body, () => ContentDisposed = true) };
        }
    }
    private sealed class BodyContent(OwnedBody body, Action disposed) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected body buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
    private sealed class OwnedBody(string wire, bool gated = false) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        internal readonly TaskCompletionSource CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed; internal int AsyncDisposeCalls; private Task? cleanup;
        public override ValueTask DisposeAsync() => new(cleanup ??= CloseCore());
        private async Task CloseCore() { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); if (gated) await ReleaseCleanup.Task; Disposed = true; base.Dispose(true); }
    }
    private sealed class Sink(Action<AgentEvent>? observe = null) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { observe?.Invoke(observation); return ValueTask.CompletedTask; } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
