using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class SourceToolResultValueTests
{
    private static readonly ModelDescriptor Model = new("source-result-model", "openai-responses", "offline-authored");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("source result owns absent/null/empty fields and coherent typed updates", PresenceAndUpdates);
        yield return ("source after-hook scheduler applies nullish patch and separates outcome error", ActualAfterHook);
        yield return ("source result complete raw admission bounds unknown metadata and syntax", StrictAdmission);
        yield return ("source result invoker and progress charge retained usage and unknown fields", BoundaryBudgets);
        yield return ("source result canonical history resumes absent details and projects next provider request", NextRequest);
    }
    private static Task PresenceAndUpdates()
    {
        var absent = ToolResult.FromJson(JsonData.EmptyObject);
        Equal("{}", absent.ToJson().ToString()); Check(absent.Content.IsEmpty && !absent.HasProperty("details") && absent.Usage is null, "Absent compatibility views inserted fields.");
        var nulls = ToolResultValueCodec.Read("{\"content\":null,\"details\":null,\"usage\":null,\"structuredContent\":null,\"terminate\":null}");
        Check(nulls.Content.IsEmpty && ReferenceEquals(nulls.Details, nulls.Property("details")), "Null result ownership changed.");
        Equal(JsonValueKind.Null, nulls.ToJson().Value.GetProperty("content").ValueKind);
        var owned = ToolResultValueCodec.Read("{\"content\":[],\"opaqueResult\":{\"scale\":1.0,\"wide\":9007199254740993,\"nil\":null},\"usage\":{\"cost\":0.005},\"isError\":false}");
        var details = JsonData.Parse("{\"a\":-0}"); var structured = JsonData.Parse("[1e2,null]");
        var changed = owned with { Content = [new("changed")], Details = details, StructuredContent = structured, IsError = true, Terminate = false };
        var body = changed.ToJson().Value;
        Equal("changed", body.GetProperty("content")[0].GetProperty("text").GetString());
        Check(body.GetProperty("isError").GetBoolean() && !body.GetProperty("terminate").GetBoolean(), "Typed flags left stale source values.");
        Check(ReferenceEquals(details, changed.Details) && ReferenceEquals(structured, changed.StructuredContent), "Typed owned metadata was replaced.");
        Equal("1.0", body.GetProperty("opaqueResult").GetProperty("scale").GetRawText());
        Equal("9007199254740993", body.GetProperty("opaqueResult").GetProperty("wide").GetRawText());
        Equal("-0", body.GetProperty("details").GetProperty("a").GetRawText());
        Check(!owned.HasProperty("details") && owned.Content.IsEmpty && !owned.IsError, "A record update mutated its original.");
        var removed = changed.WithProperty("details", null) with { Usage = null, StructuredContent = null };
        Check(!removed.HasProperty("details") && !removed.ToJson().Value.TryGetProperty("usage", out _) && removed.StructuredContent is null, "Removal became explicit null.");
        var message = ToolResultMessageMaterializer.Create(new(Invocation(), absent));
        Check(!ToolResultMessageMaterializer.ToTranscript(message, 123).WireBody.Value.TryGetProperty("details", out _), "Missing details entered history.");
        Check(ToolResultMessageMaterializer.ToTranscript(message with { Details = JsonData.Null }, 123).WireBody.Value.GetProperty("details").ValueKind == JsonValueKind.Null,
            "Message typed update retained stale absence.");
        var error = ToolResult.Error(ToolFailureKind.ExecutionError, "authored error");
        Equal("content|details", string.Join("|", error.ToJson().Value.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)));
        Check(error.IsError && new ToolOutcome(Invocation(), error).IsError && error.Failure is not null, "Source-shaped error lost native failure/outcome disposition.");
        Equal("content|details", string.Join("|", ToolResult.Success("ok").ToJson().Value.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)));
        return Task.CompletedTask;
    }
    private static async Task ActualAfterHook()
    {
        var original = ToolResultValueCodec.Read("{\"content\":[{\"type\":\"text\",\"text\":\"raw\"}],\"details\":{\"nil\":null},\"usage\":{\"cost\":0.005},\"structuredContent\":{\"value\":1.25},\"isError\":false,\"terminate\":true,\"opaqueResult\":[\"x\",null]}");
        var patch = JsonData.Parse("{\"content\":[],\"details\":null,\"usage\":null,\"structuredContent\":null,\"terminate\":null,\"isError\":true,\"ignoredPatch\":1}");
        var hooks = new Hooks(patch); var events = new List<AgentEvent>();
        var batch = await new ToolBatchScheduler([new("read", new Executor(original))], hooks, ToolExecutionMode.Sequential)
            .RunAsync(Message(true), new Sink(events));
        var outcome = batch.Outcomes.Single(); var raw = outcome.Result.ToJson().Value;
        Check(outcome.IsError && !raw.GetProperty("isError").GetBoolean() && batch.Terminate, "After-hook error rewrote result.isError or null termination cleared true.");
        Check(!raw.TryGetProperty("structuredContent", out _) && !raw.TryGetProperty("ignoredPatch", out _) && raw.GetProperty("content").GetArrayLength() == 0,
            "Empty replacement content retained structured output or copied patch extras.");
        Equal("0.005", raw.GetProperty("usage").GetProperty("cost").GetRawText());
        Equal(JsonValueKind.Null, raw.GetProperty("opaqueResult")[1].ValueKind); Check(hooks.SawOriginal && !hooks.OriginalError, "Hook context did not receive original owned result and separate disposition.");
        var canonical = ToolResultMessageMaterializer.ToTranscript(batch.Messages.Single(), 123).WireBody.Value;
        Check(canonical.GetProperty("isError").GetBoolean() && canonical.TryGetProperty("usage", out _) && !canonical.TryGetProperty("opaqueResult", out _), "Canonical projection copied the wrong fields.");
        var emptyPatch = SourceToolAfterHook.Apply(new(Invocation(), original), JsonData.EmptyObject);
        Equal(original.ToJson().ToString(), emptyPatch.Result.ToJson().ToString());
        var nullPatch = SourceToolAfterHook.Apply(new(Invocation(), original), JsonData.Null);
        Check(ReferenceEquals(original, nullPatch.Result), "Null patch replaced the result.");
        var recovered = SourceToolAfterHook.Apply(new ToolOutcome(Invocation(), original) { IsError = true }, JsonData.Parse("{\"isError\":false,\"structuredContent\":[null,7]}"));
        Check(!recovered.IsError && !recovered.Result.IsError && recovered.Result.StructuredContent!.Value.ValueKind == JsonValueKind.Array, "Independent error recovery or nonnull structured replacement failed.");
        Check(events.OfType<ToolExecutionEnded>().Single().Outcome.IsError, "Scheduler emitted pre-hook disposition.");
        var invalidPatch = await new ToolBatchScheduler([new("read", new Executor(original))], new Hooks(JsonData.Parse("{\"usage\":1e309}")))
            .RunAsync(Message(true), new Sink([]));
        var hookError = invalidPatch.Outcomes.Single();
        Check(hookError.IsError && hookError.Result.Failure?.Kind == ToolFailureKind.HookError, "Hook admission failed without an error outcome.");
        Equal("content|details", string.Join("|", hookError.Result.ToJson().Value.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)));
        var thrown = await new ToolBatchScheduler([new("read", new ThrowingExecutor())]).RunAsync(Message(true), new Sink([]));
        Check(thrown.Outcomes.Single().IsError, "Thrown tool lost outcome error.");
        Equal("content|details", string.Join("|", thrown.Outcomes.Single().Result.ToJson().Value.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)));
    }
    private static Task StrictAdmission()
    {
        foreach (var raw in new[] { "{\"usage\":1e309}", "{\"opaqueResult\":{\"a\":1,\"\\u0061\":2}}", "{\"usage\":\"\\uD800\"}", "{\"opaqueResult\":1,}", "{\"opaqueResult\":/*comment*/1}", "[1]" })
            Throws(() => ToolResultValueCodec.Read(raw));
        Throws(() => ToolResultValueCodec.Read("{\"opaqueResult\":{\"nested\":{}}}", new(MaximumJsonDepth: 1)));
        var exact = ToolResultValueCodec.Read("{\"usage\":{\"n\":1.0,\"x\":-0,\"wide\":9007199254740993},\"opaqueResult\":\"\\u0000\"}", new(MaximumJsonDepth: 1));
        Equal("1.0", exact.Usage!.Value.GetProperty("n").GetRawText()); Equal("-0", exact.Usage.Value.GetProperty("x").GetRawText());
        const string text = "{\"opaqueResult\":\"π\"}";
        Throws(() => ToolResultValueCodec.Read(text, new(MaximumRawBytes: System.Text.Encoding.UTF8.GetByteCount(text) - 1)));
        Throws(() => ToolResultValueCodec.Read(text, new(MaximumRawCharacters: text.Length - 1)));
        using var permissive = JsonDocument.Parse("{\"usage\":{\"n\":1,},}", new JsonDocumentOptions { AllowTrailingCommas = true });
        Throws(() => ToolResult.FromJson(JsonData.FromElement(permissive.RootElement)));
        return Task.CompletedTask;
    }
    private static async Task BoundaryBudgets()
    {
        foreach (var field in new[] { "usage", "opaqueResult" })
        {
            var retained = ToolResult.Success("ok").WithProperty(field, JsonData.Parse(JsonSerializer.Serialize(new string('x', 96))));
            var invoker = new ToolInvoker([new Adapter(retained)], new Policy(), options: new(MaximumResultCharacters: 64));
            var result = await invoker.ExecuteAsync(Invocation(), default);
            Check(result.IsError && result.Failure?.Kind == ToolFailureKind.ExecutionError && !result.ToJson().ToString().Contains(new string('x', 20), StringComparison.Ordinal), "Invoker admitted or exposed oversized metadata.");
        }
        foreach (var bad in new[] { JsonData.Parse("1e309"), JsonData.Parse("\"\\uD800\"") })
        {
            var invoker = new ToolInvoker([new Adapter(ToolResult.Success("ok").WithProperty("usage", bad))], new Policy());
            Check((await invoker.ExecuteAsync(Invocation(), default)).Failure?.Kind == ToolFailureKind.ExecutionError, "Invoker admitted malformed opaque usage.");
        }
        // Existing prepared adapters can explicitly admit more than the factory's default
        // ordinary budget. Outer execution/history must not impose an accidental lower cap.
        var large = ToolResult.Success(new string('x', 70_000));
        var configured = new ToolInvoker([new Adapter(large)], new Policy(), options: new(MaximumResultCharacters: 131_072));
        var accepted = await new ToolBatchScheduler([new("read", configured)]).RunAsync(Message(true), new Sink([]));
        Check(!accepted.Outcomes.Single().IsError, "Outer execution rejected an existing configured larger prepared result.");
        Equal(70_000, ToolResultMessageMaterializer.ToTranscript(accepted.Messages.Single(), 123).WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString()!.Length);
        var lowered = await new ToolBatchScheduler([new("read", new Executor(large))], resultOptions: new(MaximumCharacters: 65_536)).RunAsync(Message(true), new Sink([]));
        Check(lowered.Outcomes.Single().IsError && !lowered.Messages.Single().Content[0].Text.Contains(new string('x', 20), StringComparison.Ordinal), "Explicit outer result budget was bypassed.");
        var events = new List<AgentEvent>(); var afterReport = false;
        var tool = new ProgressExecutor(async (progress, token) =>
        {
            await progress(ToolResult.Success("partial").WithProperty("opaqueResult", JsonData.Parse(JsonSerializer.Serialize(new string('x', 96)))), token);
            afterReport = true; return ToolResult.Success("unexpected");
        });
        var scheduler = new ToolBatchScheduler([new("read", tool)], progressOptions: new(Mode: ToolProgressDeliveryMode.SourceCompatible, MaximumRetainedCharacters: 64));
        try { await scheduler.RunAsync(Message(true), new Sink(events)); throw new Exception("Expected terminal source progress budget failure."); }
        catch (InvalidOperationException) { }
        Check(!afterReport && !events.OfType<ToolExecutionUpdated>().Any() && !events.OfType<ToolExecutionEnded>().Any(), "Progress admission failed to charge arbitrary metadata before listener/effect/end.");
    }
    private static async Task NextRequest()
    {
        var result = ToolResultValueCodec.Read("{\"content\":null,\"usage\":{\"futureCost\":0.005,\"wide\":9007199254740993,\"nil\":null},\"structuredContent\":{\"programOnly\":true},\"opaqueResult\":42}");
        var source = new Source([Message(true), Message(false)]);
        await using var agent = new NativeAgent(new(Model, source, [new("read", new Executor(result))]), () => 123, new Sink([]));
        await agent.PromptAsync(Input());
        var canonical = agent.Snapshot.Messages.Single(value => value.Role == "toolResult");
        Check(!canonical.WireBody.Value.TryGetProperty("details", out _) && canonical.WireBody.Value.GetProperty("content").GetArrayLength() == 0, "Canonical null/missing normalization changed.");
        Equal("0.005", source.Requests[1].Messages.Single(value => value.Role == "toolResult").WireBody.Value.GetProperty("usage").GetProperty("futureCost").GetRawText());
        using var request = new ResponsesKeyAuthRequestFactory(new Uri("https://offline.invalid/v1/responses"), Model,
            new(false, AllowedToolCallProviders: ImmutableHashSet.Create(Model.Provider))).Create(source.Requests[1], "inert-authored-key");
        var payload = await request.Content!.ReadAsStringAsync();
        Check(payload.Contains("function_call_output", StringComparison.Ordinal) && !payload.Contains("futureCost", StringComparison.Ordinal) && !payload.Contains("programOnly", StringComparison.Ordinal) && !payload.Contains("opaqueResult", StringComparison.Ordinal), "Provider projection copied program/result metadata.");
        var resumedSource = new Source([Message(false)]);
        await using var resumed = new NativeAgent(new(Model, resumedSource, []), () => 123, new Sink([]));
        resumed.ReplaceMessages(agent.Snapshot.Messages.Take(3).ToImmutableArray());
        await resumed.ContinueAsync();
        Equal(canonical.WireBody.ToString(), resumedSource.Requests.Single().Messages.Single(value => value.Role == "toolResult").WireBody.ToString());
    }
    private static ToolInvocation Invocation() { var message = Message(true); return new(message, (ToolCallContent)message.Content[0], 0); }
    private static AssistantMessage Message(bool tool) => new(Model.Api, Model.Provider, Model.Id, 123, tool ? [new ToolCallContent("source-call", "read", JsonData.EmptyObject)] : [new TextContent("done")], TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"run\",\"timestamp\":123}"));
    private sealed class Hooks(JsonData patch) : ISourceToolHooks
    {
        public bool SawOriginal, OriginalError;
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<JsonData?> AfterToolCallAsync(ToolInvocation invocation, ToolResult result, bool isError, CancellationToken token)
        { SawOriginal = result.HasProperty("opaqueResult"); OriginalError = isError; return ValueTask.FromResult<JsonData?>(patch); }
    }
    private sealed class Executor(ToolResult result) : IToolExecutor
    { public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(result); }
    private sealed class ThrowingExecutor : IToolExecutor
    { public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromException<ToolResult>(new InvalidOperationException("authored thrown")); }
    private sealed class ProgressExecutor(Func<ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> callback) : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => callback((_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback progress, CancellationToken token) => callback(progress, token);
    }
    private sealed class Adapter(ToolResult result) : IPreparedToolAdapter
    {
        public string Name => "read";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "read", PreparedToolActionKind.Path, "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(result);
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Sink(List<AgentEvent> events) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { events.Add(observation); return ValueTask.CompletedTask; } }
    private sealed class Source(AssistantMessage[] messages) : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = messages[Requests.Count]; Requests.Add(request); token.ThrowIfCancellationRequested();
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < final.Content.Length; index++)
                if (final.Content[index] is ToolCallContent call) { yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(index, call); }
                else if (final.Content[index] is TextContent text) { yield return new TextStarted(index, new("")); yield return new TextEnded(index, text.Text); }
            yield return new StreamDone(final.StopReason, final); await Task.CompletedTask;
        }
    }
    private static void Throws(Action action) { try { action(); } catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException) { return; } throw new InvalidOperationException("Expected strict result rejection."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
