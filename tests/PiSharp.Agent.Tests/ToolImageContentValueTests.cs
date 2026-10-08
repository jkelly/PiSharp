using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class ToolImageContentValueTests
{
    private static readonly ModelDescriptor Model = new("agent-image-model", "openai-completions", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("tool-image-content-value.one-slot-raw-presence-legacy-ABI-and-canonical-retention", OneSlot);
        yield return ("tool-image-content-value.strict-shape-Unicode-finite-duplicate-depth-aggregate-and-block-bounds", Admission);
        yield return ("tool-image-content-value.native-awaited-progress-joins-before-transforms-and-source-patch", () => Progress(ToolProgressDeliveryMode.NativeAwaited));
        yield return ("tool-image-content-value.source-progress-joins-before-transforms-and-source-patch", () => Progress(ToolProgressDeliveryMode.SourceCompatible));
        yield return ("tool-image-content-value.source-progress-exact-decoded-image-budget-and-block-admission", ProgressBudgets);
        yield return ("tool-image-content-value.progress-cancellation-joins-owned-cleanup-and-prevents-final-authority", Cancellation);
        yield return ("tool-image-content-value.actual-public-Agent-canonical-sink-and-source-continuation-request", PublicAgent);
    }
    private static Task OneSlot()
    {
        foreach (var raw in new[] { "{}", "{\"content\":null}", "{\"content\":[]}" })
        {
            var result = ToolResult.FromJson(JsonData.Parse(raw)); Equal(raw, result.ToJson().ToString());
            Equal("[]", result.ContentValue.ToString()); Check(result.Content.IsEmpty, "Nullish text view changed.");
        }
        var full = SourceResult(); var original = full.ToJson(); var content = full.ContentValue;
        Check(ReferenceEquals(content, full.Property("content")), "Full content has a second authority.");
        Reject(() => _ = full.Content); Reject(() => full.Deconstruct(out _, out _, out _, out _, out _));
        var changed = full with { Content = [new("legacy replacement")] };
        Equal("legacy replacement", changed.Content.Single().Text); Equal(1, changed.ContentValue.Value.GetArrayLength());
        Check(changed.ContentValue.Value.EnumerateArray().All(block => block.GetProperty("type").GetString() == "text"), "Text replacement retained stale images.");
        var imagesAgain = changed with { ContentValue = content }; Equal(content.ToString(), imagesAgain.Property("content")!.ToString());
        Equal(original.ToString(), full.ToJson().ToString()); Reject(() => _ = imagesAgain.Content);
        var message = ToolResultMessageMaterializer.Create(new(Invocation(), full));
        Check(ReferenceEquals(content, message.ContentValue), "Materializer rebuilt the admitted image value.");
        Reject(() => _ = message.Content); Reject(() => message.Deconstruct(out _, out _, out _, out _, out _));
        Equal(content.ToString(), ToolResultMessageMaterializer.ToTranscript(message, 123).WireBody.Value.GetProperty("content").GetRawText());
        var legacyMessage = message with { Content = [new("message replacement")] };
        Equal("message replacement", legacyMessage.Content.Single().Text);
        Equal(content.ToString(), (legacyMessage with { ContentValue = content }).ContentValue.ToString());
        var nil = full with { ContentValue = JsonData.Null };
        Equal("[]", nil.ContentValue.ToString()); Equal("null", nil.Property("content")!.ToString());
        Check(typeof(ToolResult).GetProperty("Content")!.PropertyType == typeof(ImmutableArray<TextContent>) &&
            typeof(ToolResultMessage).GetProperty("Content")!.PropertyType == typeof(ImmutableArray<TextContent>), "Legacy text property ABI changed.");
        Check(typeof(ToolResult).GetProperty("ContentValue")!.PropertyType == typeof(JsonData) &&
            typeof(ToolResultMessage).GetProperty("ContentValue")!.PropertyType == typeof(JsonData), "R3 property API differs.");
        Reject(() => _ = full with { ContentValue = null! });
        return Task.CompletedTask;
    }
    private static Task Admission()
    {
        foreach (var block in new[]
        {
            "null", "{}", "{\"type\":\"audio\"}", "{\"type\":\"thinking\",\"thinking\":\"x\"}",
            "{\"type\":\"image\",\"data\":\"x\"}", "{\"type\":\"image\",\"data\":null,\"mimeType\":\"x\"}",
            "{\"type\":\"image\",\"data\":\"x\",\"mimeType\":false}",
            "{\"type\":\"image\",\"data\":\"\\uD800\",\"mimeType\":\"x\"}",
            "{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"\\uDC00\"}",
            "{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\",\"future\":1e999}",
            "{\"type\":\"image\",\"data\":\"x\",\"data\":\"y\",\"mimeType\":\"x\"}",
            "{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\",\"future\":{\"x\":1,\"x\":2}}"
        }) Reject(() => _ = ToolResult.FromJson(JsonData.Parse("{\"content\":[" + block + "]}")));
        var raw = "{\"content\":[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}]}";
        var exact = ToolResult.FromJson(JsonData.Parse(raw), new(MaximumCharacters: 13, MaximumRawCharacters: raw.Length, MaximumRawBytes: Encoding.UTF8.GetByteCount(raw), MaximumContentBlocks: 1));
        ToolResultValueCodec.Validate(exact, new(MaximumCharacters: 13));
        Reject(() => ToolResultValueCodec.Validate(exact, new(MaximumCharacters: 12)));
        Reject(() => ToolResultValueCodec.Validate(exact, new(MaximumRawCharacters: raw.Length - 1)));
        Reject(() => ToolResultValueCodec.Validate(exact, new(MaximumRawBytes: Encoding.UTF8.GetByteCount(raw) - 1)));
        var two = "{\"content\":[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\"},{\"type\":\"text\",\"text\":\"\"}]}";
        Reject(() => _ = ToolResult.FromJson(JsonData.Parse(two), new(MaximumContentBlocks: 1)));
        var deep = "{\"content\":[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\",\"future\":{\"deep\":{\"n\":1}}}]}";
        Reject(() => _ = ToolResult.FromJson(JsonData.Parse(deep), new(MaximumJsonDepth: 1)));
        var unicode = ToolResult.FromJson(JsonData.Parse("{\"content\":[{\"type\":\"image\",\"data\":\"\\u0000\\uD83D\\uDE42\",\"mimeType\":\"opaque/文\",\"future\":{\"n\":1.00,\"wide\":9007199254740993,\"nil\":null}}]}"));
        Equal("1.00", unicode.ContentValue.Value[0].GetProperty("future").GetProperty("n").GetRawText());
        Equal("9007199254740993", unicode.ContentValue.Value[0].GetProperty("future").GetProperty("wide").GetRawText());
        var unicodeRaw = unicode.ToJson().ToString();
        Check(Encoding.UTF8.GetByteCount(unicodeRaw) > unicodeRaw.Length, "UTF8 bound control lost its multibyte data.");
        Reject(() => ToolResultValueCodec.Validate(unicode, new(MaximumRawCharacters: unicodeRaw.Length, MaximumRawBytes: unicodeRaw.Length)));
        foreach (var invalid in new[] { "[/*comment*/{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\"}]", "[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\"},]" })
        {
            using var parsed = JsonDocument.Parse(invalid, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var permissive = JsonData.FromElement(parsed.RootElement); Reject(() => _ = ToolResult.Success("") with { ContentValue = permissive });
        }
        return Task.CompletedTask;
    }
    private static async Task Progress(ToolProgressDeliveryMode mode)
    {
        var produced = SourceResult(); var replacement = JsonData.Parse("{\"content\":[{\"type\":\"image\",\"data\":\"after\",\"mimeType\":\"image/jpeg\"}],\"details\":null,\"usage\":null}");
        var entered = Gate(); var release = Gate(); var transforms = 0; var hooks = new SourceHooks(replacement);
        var adapter = new Adapter(async (progress, token) => { await progress(produced, token); return produced; });
        var invoker = new ToolInvoker([adapter], new Policy(), resultTransforms: [(_, _, value, _) => { transforms++; return ValueTask.FromResult(value); }]);
        var events = new List<AgentEvent>();
        var scheduler = new ToolBatchScheduler([new("inspect", invoker)], hooks, progressOptions: new(mode));
        var running = scheduler.RunAsync(Message(true), new Sink(async (observation, token) =>
        {
            events.Add(observation);
            if (observation is ToolExecutionUpdated update)
            { Equal(produced.ContentValue.ToString(), update.PartialResult.ContentValue.ToString()); entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }));
        try
        {
            await entered.Task.WaitAsync(Deadline); Equal(0, transforms); Equal(0, hooks.Calls);
            Check(!running.IsCompleted && !events.OfType<ToolExecutionEnded>().Any(), "Finalization overtook admitted image progress.");
            release.TrySetResult(); var batch = await running.WaitAsync(Deadline);
            Equal(1, transforms); Equal(1, hooks.Calls); Check(!batch.Outcomes.Single().IsError, "Valid image progress failed.");
            Equal(replacement.Value.GetProperty("content").GetRawText(), batch.Messages.Single().ContentValue.ToString());
            Equal(produced.Details.ToString(), batch.Outcomes.Single().Result.Details.ToString());
            Equal(produced.Usage!.ToString(), batch.Outcomes.Single().Result.Usage!.ToString());
            Equal(produced.Property("opaque")!.ToString(), batch.Outcomes.Single().Result.Property("opaque")!.ToString());
        }
        finally { release.TrySetResult(); await Observe(running); }
    }
    private static async Task ProgressBudgets()
    {
        var image = ToolResult.FromJson(JsonData.Parse("{\"content\":[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}]}"));
        foreach (var limit in new[] { 14, 15 })
        {
            var observed = 0; var adapter = new Adapter(async (progress, token) => { await progress(image, token); return image; });
            var scheduler = new ToolBatchScheduler([new("inspect", new ToolInvoker([adapter], new Policy()))],
                progressOptions: new(ToolProgressDeliveryMode.SourceCompatible, MaximumRetainedCharacters: limit));
            var running = scheduler.RunAsync(Message(true), new Sink((observation, _) => { if (observation is ToolExecutionUpdated) observed++; return ValueTask.CompletedTask; }));
            if (limit == 14) { await RejectedProgress(running); Equal(0, observed); }
            else { var result = await running.WaitAsync(Deadline); Equal(1, observed); Check(!result.Outcomes.Single().IsError, "Exact image progress budget failed."); }
        }
        var mixed = SourceResult(); var tooMany = new ToolBatchScheduler([new("inspect", new ToolInvoker([new Adapter(async (progress, token) => { await progress(mixed, token); return mixed; })], new Policy()))],
            progressOptions: new(ToolProgressDeliveryMode.SourceCompatible, MaximumContentBlocks: 1));
        var updates = 0; await RejectedProgress(tooMany.RunAsync(Message(true), new Sink((observation, _) => { if (observation is ToolExecutionUpdated) updates++; return ValueTask.CompletedTask; }))); Equal(0, updates);
    }
    private static async Task Cancellation()
    {
        var entered = Gate(); var cleanup = Gate(); var releaseCleanup = Gate(); var ended = 0; var image = SourceResult();
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(async (progress, token) =>
        { try { await progress(image, token); return image; } finally { cleanup.TrySetResult(); await releaseCleanup.Task; } });
        var scheduler = new ToolBatchScheduler([new("inspect", new ToolInvoker([adapter], new Policy()))]);
        var running = scheduler.RunAsync(Message(true), new Sink(async (observation, token) =>
        {
            if (observation is ToolExecutionUpdated) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (observation is ToolExecutionEnded) ended++;
        }), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline); cancellation.Cancel(); await cleanup.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted, "Image cancellation abandoned owned adapter cleanup."); Equal(0, ended);
            releaseCleanup.TrySetResult();
            try { var result = await running.WaitAsync(Deadline); Check(result.IsCanceled, "Canceled image work gained successful authority."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        finally { releaseCleanup.TrySetResult(); cancellation.Cancel(); await Observe(running); }
    }
    private static async Task PublicAgent()
    {
        using var fixture = Fixture(); var profile = fixture.RootElement.GetProperty("observations").EnumerateArray()
            .Single(value => value.GetProperty("profile").GetProperty("id").GetString() == "mixed-images-vision-plain");
        var source = new Source(); var produced = SourceResult(); var resultEntered = Gate(); var releaseResult = Gate();
        await using var agent = new NativeAgent(new(Model, source, [new("inspect", new ToolInvoker([new Adapter((_, _) => ValueTask.FromResult(produced))], new Policy()))]),
            () => 123, new Sink(async (observation, _) =>
            { if (observation is ToolResultMessageEnded message) { Equal(produced.ContentValue.ToString(), message.Message.ContentValue.ToString()); resultEntered.TrySetResult(); await releaseResult.Task; } }));
        var initial = profile.GetProperty("initialMessages").EnumerateArray().Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
        var running = agent.PromptAsync(initial);
        try
        {
            await resultEntered.Task.WaitAsync(Deadline); Equal(1, source.Requests.Count); Check(!agent.WaitForIdleAsync().IsCompleted, "Public Agent idle overtook image result sink.");
            releaseResult.TrySetResult(); var outcome = await running.WaitAsync(Deadline); Equal(AgentLoopStopReason.Completed, outcome.Reason);
            var canonical = agent.Snapshot.Messages.Single(value => value.Role == "toolResult"); Equal(produced.ContentValue.ToString(), canonical.WireBody.Value.GetProperty("content").GetRawText());
            Equal(canonical.WireBody.ToString(), source.Requests[1].Messages.Single(value => value.Role == "toolResult").WireBody.ToString());
            var factory = new CompletionsKeyAuthRequestFactory(new Uri("https://agent-image.invalid/v1/chat/completions"), Model, new() { ModelSupportsImages = true });
            for (var index = 0; index < 2; index++)
            { using var request = factory.Create(source.Requests[index], "authored-inert-key"); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(JsonElement.DeepEquals(profile.GetProperty("requests")[index].GetProperty("bodyJson"), body.RootElement), "Public Agent complete source request differs at " + index); }
        }
        finally { releaseResult.TrySetResult(); agent.Abort(); await Observe(running); }
    }
    private static ToolResult SourceResult()
    { using var fixture = Fixture(); return ToolResult.FromJson(JsonData.FromElement(fixture.RootElement.GetProperty("observations").EnumerateArray().Single(value => value.GetProperty("profile").GetProperty("id").GetString() == "mixed-images-vision-plain").GetProperty("rawResults")[0].GetProperty("result"))); }
    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "fixtures/native/completions-image-agent-source.json"));
        Equal("56f2ee42059897b387e44a6f9300cd0e05f16a00cd1c40fea3ab364736e0f593", Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private static AssistantMessage Message(bool tool) => new(Model.Api, Model.Provider, Model.Id, 123,
        tool ? [new ToolCallContent("call-inspect", "inspect", JsonData.Parse("{\"value\":1.00,\"keep\":null}"))] : [new TextContent("Observed image")], TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private static ToolInvocation Invocation() { var message = Message(true); return new(message, (ToolCallContent)message.Content[0], 0); }
    private sealed class Adapter(Func<ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> execute) : IPreparedToolAdapter
    {
        public string Name => "inspect";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path, "authored-safe-target", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => execute((_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) => execute(progress, token);
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class SourceHooks(JsonData patch) : ISourceToolHooks
    {
        public int Calls;
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<JsonData?> AfterToolCallAsync(ToolInvocation invocation, ToolResult result, bool isError, CancellationToken token)
        { Check(!isError && result.ContentValue.Value.EnumerateArray().Any(block => block.GetProperty("type").GetString() == "image"), "Source hook lost image output."); Calls++; return ValueTask.FromResult<JsonData?>(patch); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Source : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = Message(Requests.Count == 0); Requests.Add(request); token.ThrowIfCancellationRequested();
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (final.Content[0] is ToolCallContent call) { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
            else { var text = (TextContent)final.Content[0]; yield return new TextStarted(0, text with { Text = "" }); yield return new TextEnded(0, text.Text); }
            yield return new StreamDone(final.StopReason, final); await Task.CompletedTask;
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static async Task RejectedProgress(Task<ToolBatchResult> task)
    {
        // The existing invoker converts its forwarded progress admission failure into an error result.
        // A direct source-mode delivery fault may instead remain a terminal scheduler failure.
        try { var batch = await task.WaitAsync(Deadline); Check(batch.Outcomes.Single().Result.Failure?.Kind == ToolFailureKind.ExecutionError,
            "Rejected image progress gained successful authority."); }
        catch (InvalidOperationException) when (task.IsFaulted) { }
    }
    private static void Reject(Action action) { try { action(); } catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException) { return; } throw new InvalidOperationException("Expected image admission rejection."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
