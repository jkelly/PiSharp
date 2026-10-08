using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class ToolImageResumeTests
{
    private const string FixtureHash = "56f2ee42059897b387e44a6f9300cd0e05f16a00cd1c40fea3ab364736e0f593";
    private const string ResumeFixtureHash = "16f3fd0a372ecb7672fa8b59a96124136f91652a45d838f362f6681e0e7fbac3";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly List<object> Evidence = [];
    public static object DifferentialEvidence => new { fixtureSha256 = FixtureHash, freshResumeFixtureSha256 = ResumeFixtureHash, originalExpectationsUnchanged = true,
        actualInjectedHttp = true, cliDurableQualification = false, observations = Evidence };
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        using var fixture = Fixture();
        foreach (var value in fixture.RootElement.GetProperty("observations").EnumerateArray())
        {
            var id = value.GetProperty("profile").GetProperty("id").GetString()!;
            foreach (var route in new[] { "context", "context-abort-settlement", "public-continue" })
            { var selectedRoute = route; yield return ("tool-image-resume." + id + "." + route + ".exact-original-continuation-no-tool-replay", () => Resume(id, selectedRoute)); }
        }
        yield return ("tool-image-resume.canonical-bounded-content-metadata-and-existing-history-guards", Admission);
        yield return ("tool-image-resume.additive-options-legacy-ABI-configured-scalar-byte-depth-and-aggregate-boundaries", ConfiguredLimits);
        yield return ("tool-image-resume.public-cancellation-joins-owned-http-cleanup-before-idle", Cancellation);
    }
    private static async Task Resume(string id, string route)
    {
        using var fixture = Fixture(); using var fresh = FreshFixture(); var source = Case(fixture, id); var freshSource = Case(fresh, id); var history = History(source);
        Check(JsonElement.DeepEquals(source.GetProperty("contexts")[1], freshSource.GetProperty("initialHistory")), "Fresh Source continuation changed the original canonical history.");
        Check(JsonElement.DeepEquals(source.GetProperty("requests")[1], freshSource.GetProperty("requests")[0]), "Fresh Source continuation changed the exact original request expectation.");
        Equal(0, freshSource.GetProperty("effects").GetInt32());
        var retained = history.Select(message => message.WireBody.ToString()).ToArray();
        var (model, factory) = SelectedFactory(source); var body = new OwnedBody(source.GetProperty("secondResponseWire").GetString()!);
        var content = new BodyContent(body); var requests = new List<HttpRequestMessage>(); var actualBodies = new List<JsonData>();
        var executor = new NoReplay(); var assistantEntered = Gate(); var releaseAssistant = Gate(); var inputEvents = 0; var toolEvents = 0;
        var prepared = 0; ChatRequest? preparedRequest = null;
        using var handler = new Handler(async (request, token) =>
        {
            requests.Add(request); Check(requests.Count == 1, "Resume issued more than the original second request.");
            Equal(source.GetProperty("requests")[1].GetProperty("url").GetString(), request.RequestUri!.AbsoluteUri); Equal("POST", request.Method.Method);
            actualBodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var transport = new CompletionsHttpSseTransport(client, (request, token) => factory.Create(request, "authored-inert-key", token));
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is AgentLoopInputMessageStarted or AgentLoopInputMessageEnded) inputEvents++;
            if (observation is ToolResultMessageEnded or ToolExecutionStarted or ToolExecutionEnded) toolEvents++;
            if (observation is AssistantMessageEnded)
            { Check(body.Disposed && content.Disposed, "Resumed assistant commit preceded owned HTTP cleanup."); assistantEntered.TrySetResult(); await releaseAssistant.Task; }
        });
        var hooks = new AgentHooks((snapshot, _) =>
        { prepared++; preparedRequest = new(snapshot.Model, snapshot.Transcript, 123); return ValueTask.FromResult(preparedRequest); });
        await using var agent = new NativeAgent(new(model, transport, [new("inspect", executor)], Hooks: hooks), () => 123, sink);
        using var cancellation = new CancellationTokenSource();
        Task<AgentLoopResult> running;
        if (route == "public-continue") { agent.ReplaceMessages(history); running = agent.ContinueAsync(cancellation.Token); }
        else
        {
            var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("inspect", executor)])), model, () => 123);
            running = route == "context-abort-settlement"
                ? runner.RunWithContextAndAbortSettlementAsync(history, [], new(hooks.PrepareRequest!), sink, cancellation.Token)
                : runner.RunWithContextAsync(history, [], new(hooks.PrepareRequest!), sink, cancellation.Token);
        }
        AgentLoopResult? result = null; string? error = null;
        try
        {
            await Task.WhenAny(body.CleanupEntered.Task, running).WaitAsync(Deadline);
            if (running.IsCompleted) await running;
            Check(body.CleanupEntered.Task.IsCompleted && !running.IsCompleted && !assistantEntered.Task.IsCompleted, "Resume lost owned response cleanup before canonical commit.");
            Equal(1, prepared); Equal(1, requests.Count); Equal(0, executor.Calls); Equal(0, inputEvents); Equal(0, toolEvents);
            if (route == "public-continue") Check(!agent.WaitForIdleAsync().IsCompleted, "Public resume idle overtook owned cleanup.");
            body.ReleaseCleanup.TrySetResult(); await assistantEntered.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted, "Resumed generation overtook its canonical assistant sink.");
            if (route == "public-continue") Check(!agent.WaitForIdleAsync().IsCompleted, "Public resume idle overtook its canonical sink.");
            releaseAssistant.TrySetResult(); result = await running.WaitAsync(Deadline);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(1, result.Turns.Length); Equal(1, requests.Count); Equal(0, executor.Calls);
            Check(JsonElement.DeepEquals(source.GetProperty("requests")[1].GetProperty("bodyJson"), actualBodies.Single().Value), "Complete resumed HTTP body differs from the exact original source continuation.");
            Equal("Observed image", ((TextContent)result.Turns.Single().Result.Chat.Message.Content.Single()).Text);
            for (var index = 0; index < history.Length; index++)
            {
                Equal(retained[index], result.Transcript[index].WireBody.ToString());
                Check(ReferenceEquals(history[index].WireBody, preparedRequest!.Messages[index].WireBody), "History preparation created a second canonical authority.");
                if (route == "public-continue") Equal(retained[index], agent.Snapshot.Messages[index].WireBody.ToString());
            }
            Equal(1, body.AsyncDisposeCalls); Check(content.Disposed && !handler.Disposed, "Resume has incorrect response/borrowed-client lifetime.");
        }
        catch (Exception failure) { error = failure.Message; throw; }
        finally
        {
            body.ReleaseCleanup.TrySetResult(); releaseAssistant.TrySetResult(); cancellation.Cancel(); agent.Abort(); await Observe(running);
            Evidence.Add(new { id, route, sourceOriginalContinuation = true, supportsImages = source.GetProperty("profile").GetProperty("supportsImages").GetBoolean(),
                originalHistory = history.Select(message => message.WireBody.Value.Clone()).ToArray(),
                actualBodies = actualBodies.Select(value => value.Value.Clone()).ToArray(), expectedBody = source.GetProperty("requests")[1].GetProperty("bodyJson").Clone(),
                wholeBodyMatch = actualBodies.Count == 1 && JsonElement.DeepEquals(source.GetProperty("requests")[1].GetProperty("bodyJson"), actualBodies[0].Value),
                nativeTranscript = result?.Transcript.Select(message => message.WireBody.Value.Clone()).ToArray(), prepared, sends = requests.Count, toolExecutions = executor.Calls,
                inputEvents, toolEvents, cleanupCount = body.AsyncDisposeCalls, error });
        }
    }
    private static async Task Admission()
    {
        using var fixture = Fixture(); var history = History(Case(fixture, "image-only-vision-plain"));
        foreach (var raw in new[]
        {
            "null", "{}", "[null]", "[{\"type\":\"audio\"}]", "[{\"type\":\"image\",\"data\":null,\"mimeType\":\"x\"}]",
            "[{\"type\":\"image\",\"data\":\"x\"}]", "[{\"type\":\"text\",\"text\":null}]",
            "[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\",\"extra\":1e400}]",
            "[{\"type\":\"image\",\"data\":\"\\uD800\",\"mimeType\":\"x\"}]",
            "[{\"type\":\"image\",\"data\":\"x\",\"mimeType\":\"x\",\"extra\":{\"x\":1,\"x\":2}}]",
            "[{\"type\":\"image\",\"data\":\"x\",\"data\":\"y\",\"mimeType\":\"x\"}]"
        }) await RejectedRaw(history, "\"content\":" + raw, "malformed-content");
        await Rejected(ReplaceTool(history, "\"content\":[],\"details\":{\"number\":1e400}"), "nonfinite-details");
        await RejectedRaw(history, "\"content\":[],\"usage\":{\"x\":1,\"x\":2}", "duplicate-usage");
        await Rejected(ReplaceTool(history, "\"content\":[],\"opaque\":{\"s\":\"\\uD800\"}"), "opaque-Unicode");
        await RejectedRaw(history, "\"content\":[],\"toolCallId\":\"duplicate\"", "duplicate-envelope-identity");
        var nested = "0"; for (var i = 0; i < ToolResultValueOptions.ExecutionBoundary.MaximumJsonDepth + 3; i++) nested = "{\"x\":" + nested + "}";
        await Rejected(ReplaceTool(history, "\"content\":[],\"opaque\":" + nested), "metadata-depth");
        var maximum = ToolResultValueOptions.ExecutionBoundary;
        var image = "{\"type\":\"image\",\"data\":\"\",\"mimeType\":\"x\"}";
        await Rejected(ReplaceTool(history, "\"content\":[" + string.Join(',', Enumerable.Repeat(image, maximum.MaximumContentBlocks + 1)) + "]"), "block-quota");
        await Rejected(ReplaceTool(history, "\"content\":[{\"type\":\"image\",\"data\":\"" + new string('x', maximum.MaximumCharacters) + "\",\"mimeType\":\"x\"}]"), "ordinary-quota");
        await Rejected(ReplaceTool(history, "\"content\":[" + new string(' ', maximum.MaximumRawCharacters) + image + "]"), "raw-character-quota");
        var tool = history[^1].WireBody.ToString();
        foreach (var name in new[] { "toolCallId", "toolName", "timestamp", "isError" })
        {
            var changed = JsonNode.Parse(tool)!.AsObject(); changed.Remove(name); await Rejected(history.SetItem(history.Length - 1, Entry(changed.ToJsonString())), "missing-" + name);
        }
        foreach (var (name, value) in new (string, JsonNode?)[] { ("toolCallId", JsonValue.Create(" ")), ("toolName", JsonValue.Create("")), ("timestamp", JsonValue.Create("wrong")), ("isError", JsonValue.Create("wrong")) })
        { var changed = JsonNode.Parse(tool)!.AsObject(); changed[name] = value; await Rejected(history.SetItem(history.Length - 1, Entry(changed.ToJsonString())), "invalid-" + name); }
        await Rejected(ReplaceTool(history, "\"content\":[{\"type\":\"image\",\"data\":\"PRIVATE_RESUME_BLOB\",\"mimeType\":null}]"), "sanitized-image-diagnostic");
        var wrong = JsonNode.Parse(tool)!.AsObject(); wrong["role"] = "assistant";
        await Rejected(history.SetItem(history.Length - 1, new("toolResult", JsonData.Parse(wrong.ToJsonString()))), "role-mismatch");
        var assistantIndex = Array.FindIndex(history.ToArray(), message => message.Role == "assistant");
        foreach (var reason in new[] { "pending", "deferred" })
        { var assistant = JsonNode.Parse(history[assistantIndex].WireBody.ToString())!.AsObject(); assistant["stopReason"] = reason; await Rejected(history.SetItem(assistantIndex, Entry(assistant.ToJsonString())), "unfinished-assistant"); }
        await Rejected(history.AddRange(Enumerable.Repeat(history[^1], 1024)), "history-count");
        await Admitted(ReplaceTool(history, "\"content\":[{\"type\":\"image\",\"data\":\"" + new string('x', maximum.MaximumCharacters - 1) + "\",\"mimeType\":\"x\"}]"), "exact-ordinary-quota");
        await Admitted(ReplaceTool(history, "\"content\":[" + string.Join(',', Enumerable.Repeat(image, maximum.MaximumContentBlocks)) + "]"), "exact-block-quota");
        await Admitted(ReplaceTool(history, "\"content\":[{\"type\":\"image\",\"data\":\"not-base64\\u0000π\",\"mimeType\":\"opaque\\u0000\",\"wide\":9007199254740993}],\"details\":null,\"usage\":{\"n\":1.00},\"opaque\":{\"keep\":null}"), "opaque-and-raw-number-retention");
    }
    private static async Task ConfiguredLimits()
    {
        var options = new AgentLoopOptions(3, 77); options.Deconstruct(out var turns, out var messages); Equal(3, turns); Equal(77, messages);
        var property = typeof(AgentLoopOptions).GetProperty("CanonicalToolResultLimits");
        Check(property?.PropertyType == typeof(ToolResultValueOptions) && property.GetMethod is not null && property.SetMethod is not null, "Additive canonical-history limit API is missing.");
        Equal(ToolResultValueOptions.ExecutionBoundary, (ToolResultValueOptions)property!.GetValue(options)!);
        using var fixture = Fixture(); var history = History(Case(fixture, "image-only-vision-plain"));
        const string array = "[{\"type\":\"image\",\"data\":\"€\",\"mimeType\":\"x\"}]";
        var minimal = ReplaceTool(history, "\"content\":" + array); var raw = "{\"content\":" + array + "}";
        var limits = new ToolResultValueOptions(2, 64, 1, 1, raw.Length, Encoding.UTF8.GetByteCount(raw));
        await Admitted(minimal, "configured-exact-scalar-character-raw-character-UTF8-byte-and-block-quotas", WithLimits(limits));
        foreach (var denied in new[] { limits with { MaximumCharacters = 1 }, limits with { MaximumRawCharacters = raw.Length - 1 }, limits with { MaximumRawBytes = Encoding.UTF8.GetByteCount(raw) - 1 } })
            await Rejected(minimal, "configured-exact-quota-minus-one", WithLimits(denied));
        await Rejected(ReplaceTool(history, "\"content\":[{\"type\":\"image\",\"data\":\"\",\"mimeType\":\"x\",\"meta\":{\"inner\":{\"n\":1}}}]"), "configured-metadata-depth", WithLimits(new(MaximumCharacters: 128, MaximumJsonDepth: 1)));
        await Rejected(ReplaceTool(history, "\"content\":" + array + ",\"details\":null"), "configured-aggregate-details-budget", WithLimits(limits with { MaximumRawCharacters = 128, MaximumRawBytes = 128 }));
        foreach (var invalid in new[] { limits with { MaximumCharacters = 0 }, limits with { MaximumContentBlocks = 0 }, limits with { MaximumRawBytes = 0 }, limits with { MaximumJsonDepth = 65 } })
        {
            try { _ = new AgentLoopRunner(new TurnRunner(new ChatClient(new AcceptedTransport()), new ToolBatchScheduler([])), AcceptedTransport.Model, () => 123, WithLimits(invalid)); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("Invalid configured history limits were admitted.");
        }
        var nullOptions = new AgentLoopOptions(); property.SetValue(nullOptions, null);
        try { _ = new AgentLoopRunner(new TurnRunner(new ChatClient(new AcceptedTransport()), new ToolBatchScheduler([])), AcceptedTransport.Model, () => 123, nullOptions); }
        catch (ArgumentNullException) { return; }
        throw new InvalidOperationException("Null configured history limits were admitted.");

        AgentLoopOptions WithLimits(ToolResultValueOptions value) { var selected = new AgentLoopOptions(); property.SetValue(selected, value); return selected; }
    }
    private static async Task RejectedRaw(ImmutableArray<TranscriptEntry> history, string resultFields, string label)
    {
        ImmutableArray<TranscriptEntry> candidate;
        try { candidate = ReplaceTool(history, resultFields); }
        catch (JsonException)
        { Evidence.Add(new { nativeAdmissionControl = label, rejectedByOwnedJsonAdmission = true, requestPreparation = 0, sends = 0 }); return; }
        await Rejected(candidate, label);
    }
    private static async Task Rejected(ImmutableArray<TranscriptEntry> history, string label, AgentLoopOptions? options = null)
    {
        var source = new AcceptedTransport(); var prepared = 0; var events = 0; var executor = new NoReplay();
        var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(source), new ToolBatchScheduler([new("inspect", executor)])), AcceptedTransport.Model, () => 123, options);
        try { await runner.RunWithContextAsync(history, [], new((snapshot, _) => { prepared++; return ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript)); }), new Sink((_, _) => { events++; return ValueTask.CompletedTask; })); }
        catch (ArgumentException error)
        { Equal(0, prepared); Equal(0, events); Equal(0, source.Sends); Equal(0, executor.Calls); Check(!error.ToString().Contains("PRIVATE_RESUME_BLOB", StringComparison.Ordinal), "History rejection disclosed image data.");
            Evidence.Add(new { nativeAdmissionControl = label, rejectedByCanonicalHistoryAdmission = true, requestPreparation = prepared, sends = source.Sends, toolExecutions = executor.Calls }); return; }
        throw new InvalidOperationException("Rejected history gained request authority: " + label);
    }
    private static async Task Admitted(ImmutableArray<TranscriptEntry> history, string label, AgentLoopOptions? options = null)
    {
        var source = new AcceptedTransport(); var before = history.Select(message => message.WireBody.ToString()).ToArray();
        var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(source), new ToolBatchScheduler([])), AcceptedTransport.Model, () => 123, options);
        var result = await runner.RunWithContextAsync(history, [], new((snapshot, _) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript))), new Sink((_, _) => ValueTask.CompletedTask));
        Equal(1, source.Sends); Equal(AgentLoopStopReason.Completed, result.Reason);
        for (var index = 0; index < history.Length; index++) Equal(before[index], result.Transcript[index].WireBody.ToString());
        Evidence.Add(new { nativeAdmissionControl = label, admitted = true, sends = source.Sends, canonicalRawValuesRetained = true });
    }
    private static async Task Cancellation()
    {
        using var fixture = Fixture(); var source = Case(fixture, "mixed-images-vision-plain"); var history = History(source); var (model, factory) = SelectedFactory(source);
        var body = new OwnedBody(source.GetProperty("secondResponseWire").GetString()!); var content = new BodyContent(body); var sends = 0; var ended = 0;
        using var handler = new Handler((_, _) => { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); });
        using var client = new HttpClient(handler); var transport = new CompletionsHttpSseTransport(client, (request, token) => factory.Create(request, "authored-inert-key", token));
        await using var agent = new NativeAgent(new(model, transport, []), () => 123, new Sink((observation, _) => { if (observation is AssistantMessageEnded) ended++; return ValueTask.CompletedTask; }));
        agent.ReplaceMessages(history); var running = agent.ContinueAsync();
        try
        {
            await Task.WhenAny(body.CleanupEntered.Task, running).WaitAsync(Deadline); if (running.IsCompleted) await running;
            Check(body.CleanupEntered.Task.IsCompleted, "Resume failed before owned cancellation cleanup."); Equal(1, sends); Equal(0, ended);
            Check(agent.Abort(), "Public resume abort lost active ownership."); var idle = agent.WaitForIdleAsync();
            Check(!running.IsCompleted && !idle.IsCompleted, "Canceled resume abandoned owned HTTP cleanup.");
            body.ReleaseCleanup.TrySetResult(); var result = await running.WaitAsync(Deadline); await idle.WaitAsync(Deadline);
            Equal(StopReason.Aborted, result.Turns.Single().Result.Chat.Message.StopReason); Equal(1, sends); Equal(1, body.AsyncDisposeCalls);
            for (var index = 0; index < history.Length; index++) Equal(history[index].WireBody.ToString(), agent.Snapshot.Messages[index].WireBody.ToString());
        }
        finally { body.ReleaseCleanup.TrySetResult(); agent.Abort(); await Observe(running); }
    }
    private static ImmutableArray<TranscriptEntry> ReplaceTool(ImmutableArray<TranscriptEntry> history, string resultFields) => history.SetItem(history.Length - 1,
        Entry("{\"role\":\"toolResult\",\"toolCallId\":\"call-inspect\",\"toolName\":\"inspect\",\"timestamp\":123,\"isError\":false," + resultFields + "}"));
    private static TranscriptEntry Entry(string raw) { var value = JsonData.Parse(raw); return new(value.Value.GetProperty("role").GetString()!, value); }
    private static ImmutableArray<TranscriptEntry> History(JsonElement source) => source.GetProperty("contexts")[1].EnumerateArray().Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
    private static JsonElement Case(JsonDocument fixture, string id) => fixture.RootElement.GetProperty("observations").EnumerateArray().Single(value => value.GetProperty("profile").GetProperty("id").GetString() == id);
    private static (ModelDescriptor, CompletionsKeyAuthRequestFactory) SelectedFactory(JsonElement source)
    {
        var raw = JsonNode.Parse(source.GetProperty("model").GetRawText())!.AsObject(); raw["type"] = "chat";
        var provider = raw["provider"]!.GetValue<string>(); var api = raw["api"]!.GetValue<string>(); var id = raw["id"]!.GetValue<string>();
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { [api] = new JsonObject { ["chat:" + id] = raw } }.ToJsonString());
        var catalog = FrozenModelCatalog.ReadProviderJson(provider, bytes); Check(catalog.TryGetModel(CatalogModelType.Chat, id, out var selected), "Original configured model not admitted.");
        var supportsImages = selected!.Raw.Value.GetProperty("input").EnumerateArray().Any(value => value.GetString() == "image"); Equal(source.GetProperty("profile").GetProperty("supportsImages").GetBoolean(), supportsImages);
        var compatibility = selected.Raw.Value.GetProperty("compat"); var model = new ModelDescriptor(selected.Id, selected.DeclaredApi, selected.Provider);
        return (model, new(new Uri(selected.BaseUrl + "/chat/completions"), model,
            new(RequiresAssistantAfterToolResult: compatibility.GetProperty("requiresAssistantAfterToolResult").GetBoolean(), RequiresToolResultName: compatibility.GetProperty("requiresToolResultName").GetBoolean()) { ModelSupportsImages = supportsImages }));
    }
    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "fixtures/native/completions-image-agent-source.json")); Equal(FixtureHash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private static JsonDocument FreshFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "fixtures/native/completions-image-resume-source.json")); Equal(ResumeFixtureHash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private sealed class NoReplay : IToolExecutor
    { public int Calls; public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) { Calls++; throw new InvalidOperationException("Resume replayed an already finalized tool."); } }
    private sealed class AcceptedTransport : IChatTransport
    {
        public static readonly ModelDescriptor Model = new("agent-image-model", "openai-completions", "openai"); public int Sends;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Sends++; token.ThrowIfCancellationRequested(); var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [new TextContent("accepted")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new TextContent("")); yield return new TextEnded(0, "accepted");
            yield return new StreamDone(StopReason.Stop, final); await Task.CompletedTask; }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { public bool Disposed; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); } }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    { public bool Disposed; protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body); protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected buffering."); protected override bool TryComputeLength(out long length) { length = 0; return false; } protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); } }
    private sealed class OwnedBody(string wire) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    { public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource ReleaseCleanup { get; } = Gate(); public bool Disposed; public int AsyncDisposeCalls; private Task? cleanup;
        public override ValueTask DisposeAsync() => new(cleanup ??= CloseCore()); private async Task CloseCore() { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; base.Dispose(true); } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
