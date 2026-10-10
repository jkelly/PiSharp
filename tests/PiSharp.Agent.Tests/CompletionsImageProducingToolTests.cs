using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class CompletionsImageProducingToolTests
{
    private const string FixtureHash = "56f2ee42059897b387e44a6f9300cd0e05f16a00cd1c40fea3ab364736e0f593";
    // The captured wire carries {"value":1.00,"keep":null}; pi-ai finalizes it with parseStreamingJson
    // (packages/ai/src/api/openai-completions.ts:464, packages/ai/src/utils/json-parse.ts:104-110), and the fixture's own
    // toolExecutions record the unchanged Pi Agent executing with args {"value":1,"keep":null}.
    private const string Arguments = "{\"value\":1,\"keep\":null}";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly List<object> Evidence = [];
    public static object DifferentialEvidence => new { fixtureSha256 = FixtureHash, actualNativeLoop = true,
        actualPiAgentLoop = true, selectedCatalogCapability = true, cliCapabilityQualification = false, observations = Evidence };

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        using var fixture = Fixture();
        foreach (var observation in fixture.RootElement.GetProperty("observations").EnumerateArray())
        {
            var id = observation.GetProperty("profile").GetProperty("id").GetString()!;
            yield return ("completions-image-producing-agent." + id + ".source-request-body-and-owned-tool-continuation", () => RoundTrip(id));
        }
        foreach (var failure in new[] { "invalid-schema", "truncated", "malformed-provider", "pre-canceled" })
        {
            var control = failure;
            yield return ("completions-image-producing-agent." + control + ".no-tool-authority", () => NoAuthority(control));
        }
    }

    private static async Task RoundTrip(string id)
    {
        using var fixture = Fixture();
        var source = fixture.RootElement.GetProperty("observations").EnumerateArray()
            .Single(value => value.GetProperty("profile").GetProperty("id").GetString() == id);
        var (model, factory) = SelectedFactory(source);
        var first = new OwnedBody(source.GetProperty("firstResponseWire").GetString()!, gated: true);
        var second = new OwnedBody(source.GetProperty("secondResponseWire").GetString()!);
        var responses = new[] { new BodyContent(first), new BodyContent(second) };
        var requests = new List<HttpRequestMessage>(); var actualBodies = new List<JsonData>();
        using var handler = new Handler(async (request, token) =>
        {
            requests.Add(request); actualBodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            Check(requests.Count <= 2, "An unexpected third provider request was issued.");
            Equal(source.GetProperty("requests")[requests.Count - 1].GetProperty("url").GetString(), request.RequestUri!.AbsoluteUri);
            Equal("POST", request.Method.Method);
            return new(HttpStatusCode.OK) { Content = responses[requests.Count - 1] };
        });
        using var client = new HttpClient(handler);
        var transport = new CompletionsHttpSseTransport(client, (request, token) => factory.Create(request, "authored-inert-key", token));
        var adapter = new Adapter(source); var policy = new Policy(); var hooks = new Hooks();
        var transforms = new ConcurrentDictionary<string, JsonData>(StringComparer.Ordinal);
        var invoker = new ToolInvoker([adapter], policy, resultTransforms: [(invocation, action, result, token) =>
        {
            token.ThrowIfCancellationRequested();
            Check(ReferenceEquals(action, policy.Actions[invocation.Call.Id]), "Final result transform changed the authorized action identity.");
            transforms[invocation.Call.Id] = result.ToJson();
            return ValueTask.FromResult(result);
        }]);
        var assistantEntered = Gate(); var releaseAssistant = Gate(); var resultEntered = Gate(); var releaseResult = Gate();
        var nativeToolMessages = new List<TranscriptEntry>(); var trace = new ConcurrentQueue<string>();
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is AssistantMessageEnded && requests.Count == 1)
            {
                Check(first.Disposed && responses[0].Disposed, "Assistant commit preceded owned HTTP cleanup.");
                await ThrowsAsync<ObjectDisposedException>(() => requests[0].Content!.ReadAsStringAsync());
                trace.Enqueue("assistant-sink-enter"); assistantEntered.TrySetResult();
                await releaseAssistant.Task; trace.Enqueue("assistant-sink-release");
            }
            if (observation is ToolResultMessageEnded ended)
            {
                nativeToolMessages.Add(ToolResultMessageMaterializer.ToTranscript(ended.Message, 123));
                trace.Enqueue("result-sink-enter:" + ended.Message.ToolCallId);
                resultEntered.TrySetResult(); await releaseResult.Task;
                trace.Enqueue("result-sink-release:" + ended.Message.ToolCallId);
            }
        });
        var initial = source.GetProperty("initialMessages").EnumerateArray()
            .Select(message => new TranscriptEntry(message.GetProperty("role").GetString()!, JsonData.FromElement(message))).ToImmutableArray();
        var initialBodies = initial.Select(entry => entry.WireBody.ToString()).ToArray();
        var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(transport, capacity: 1),
            new ToolBatchScheduler([new("inspect", invoker)], hooks)), model, () => 123);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(initial, Callbacks(), sink, cancellation.Token);
        AgentLoopResult? result = null; string? error = null;
        try
        {
            await Task.WhenAny(first.CleanupEntered.Task, running).WaitAsync(Deadline);
            Check(first.CleanupEntered.Task.IsCompleted, "The first request failed before the owned cleanup barrier.");
            Check(!running.IsCompleted && !assistantEntered.Task.IsCompleted && adapter.Executions == 0 && policy.Authorizations == 0 && hooks.Preflights == 0,
                "HTTP cleanup did not guard assistant commit, preflight, policy and effects.");
            _ = await requests.Single().Content!.ReadAsStringAsync();
            trace.Enqueue("cleanup-release"); first.ReleaseCleanup.TrySetResult();
            await assistantEntered.Task.WaitAsync(Deadline);
            Equal(0, adapter.Executions); Equal(0, policy.Authorizations); Equal(0, hooks.Preflights);
            releaseAssistant.TrySetResult();
            await resultEntered.Task.WaitAsync(Deadline);
            Equal(1, requests.Count); Check(!running.IsCompleted, "Continuation crossed an awaited tool-result message sink.");
            var count = source.GetProperty("toolExecutions").GetArrayLength();
            Equal(count, adapter.Executions); Equal(count, policy.Authorizations); Equal(count, hooks.Preflights);
            foreach (var (callId, action) in adapter.Actions)
            {
                Equal(Arguments, action.Arguments.ToString());
                Check(ReferenceEquals(action, policy.Actions[callId]), "Policy and effect did not receive the identical final action.");
            }
            releaseResult.TrySetResult(); result = await running.WaitAsync(Deadline);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(2, result.Turns.Length); Equal(2, requests.Count);
            Equal("Observed image", ((TextContent)result.Turns[1].Result.Chat.Message.Content.Single()).Text);
            Check(initialBodies.SequenceEqual(result.Transcript.Take(initial.Length).Select(entry => entry.WireBody.ToString())), "Request projection mutated canonical user image history.");
            foreach (var call in result.Turns[0].Result.Chat.Message.Content.OfType<ToolCallContent>()) Equal(Arguments, call.Arguments.ToString());
            var expectedMessages = source.GetProperty("toolMessages").EnumerateArray().ToArray();
            Equal(expectedMessages.Length, nativeToolMessages.Count);
            for (var index = 0; index < expectedMessages.Length; index++)
            {
                var actual = nativeToolMessages[index].WireBody.Value;
                Check(JsonElement.DeepEquals(expectedMessages[index], actual),
                    id + ": native tool result differs from actual Pi Agent image-producing result; failure=" + result.Turns[0].Result.Tools.Outcomes[index].Result.Failure?.Kind);
                Equal("1.00", actual.GetProperty("details").GetProperty("n").GetRawText());
                Equal("1.00", actual.GetProperty("usage").GetProperty("raw").GetRawText());
            }
            Equal(count, transforms.Count); Equal(count, hooks.AfterResults.Count);
            foreach (var outcome in result.Turns[0].Result.Tools.Outcomes)
            {
                var raw = adapter.RawResults[outcome.Invocation.Call.Id];
                EqualOwnedProperties(raw, transforms[outcome.Invocation.Call.Id]);
                EqualOwnedProperties(raw, hooks.AfterResults[outcome.Invocation.Call.Id]);
                EqualOwnedProperties(raw, outcome.Result.ToJson());
                Check(!outcome.IsError, "A valid image-producing tool was finalized as an execution failure.");
            }
            for (var index = 0; index < 2; index++)
                Check(JsonElement.DeepEquals(source.GetProperty("requests")[index].GetProperty("bodyJson"), actualBodies[index].Value),
                    id + ": complete native HTTP request body differs from actual Pi Agent/SDK request " + index);
            Equal(1, first.AsyncDisposeCalls); Equal(1, second.AsyncDisposeCalls);
            Check(responses.All(value => value.Disposed) && !handler.Disposed, "Owned responses or borrowed HTTP handler have incorrect lifetime.");
        }
        catch (Exception failure) { error = failure.Message; throw; }
        finally
        {
            releaseAssistant.TrySetResult(); releaseResult.TrySetResult(); first.ReleaseCleanup.TrySetResult(); cancellation.Cancel(); await Observe(running);
            Evidence.Add(new { id, sourceActualAgent = true, selectedModel = model, capabilityFromCatalog = source.GetProperty("profile").GetProperty("supportsImages").GetBoolean(),
                actualRequests = actualBodies.Select(body => body.Value.Clone()).ToArray(),
                sourceRequests = source.GetProperty("requests").EnumerateArray().Select(request => request.GetProperty("bodyJson").Clone()).ToArray(),
                wholeBodyMatches = actualBodies.Select((body, index) => JsonElement.DeepEquals(source.GetProperty("requests")[index].GetProperty("bodyJson"), body.Value)).ToArray(),
                nativeToolMessages = nativeToolMessages.Select(message => message.WireBody.Value.Clone()).ToArray(),
                sourceToolMessages = source.GetProperty("toolMessages").Clone(),
                nativeOutcomes = result?.Turns[0].Result.Tools.Outcomes.Select(outcome => new { id = outcome.Invocation.Call.Id, failure = outcome.Result.Failure,
                    result = outcome.Result.ToJson().Value.Clone() }).ToArray(),
                adapter.Executions, policy.Authorizations, hooks.Preflights, transformed = transforms.Count, afterHook = hooks.AfterResults.Count,
                barrierTrace = trace.ToArray(), firstCleanupCount = first.AsyncDisposeCalls, secondCleanupCount = second.AsyncDisposeCalls, error });
        }
    }

    private static async Task NoAuthority(string failure)
    {
        using var fixture = Fixture();
        var source = fixture.RootElement.GetProperty("observations").EnumerateArray()
            .Single(value => value.GetProperty("profile").GetProperty("id").GetString() == "image-only-vision-plain");
        var (model, factory) = SelectedFactory(source);
        var wire = source.GetProperty("firstResponseWire").GetString()!;
        if (failure == "invalid-schema") wire = wire.Replace("1.00", "\\\"wrong-type\\\"", StringComparison.Ordinal);
        if (failure == "truncated") wire = wire.Replace("\"finish_reason\":\"tool_calls\"", "\"finish_reason\":\"length\"", StringComparison.Ordinal);
        if (failure == "malformed-provider") wire = "data: {invalid-json}\n\n";
        var body = new OwnedBody(wire); var content = new BodyContent(body); var sends = 0;
        using var handler = new Handler((_, _) => { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); });
        using var client = new HttpClient(handler);
        var transport = new CompletionsHttpSseTransport(client, (request, token) => factory.Create(request, "authored-inert-key", token));
        var adapter = new Adapter(source); var policy = new Policy(); var hooks = new Hooks();
        var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(transport), new ToolBatchScheduler([new("inspect", new ToolInvoker([adapter], policy))], hooks)), model, () => 123);
        var initial = source.GetProperty("initialMessages").EnumerateArray().Select(message => new TranscriptEntry(message.GetProperty("role").GetString()!, JsonData.FromElement(message))).ToImmutableArray();
        using var cancellation = new CancellationTokenSource(); if (failure == "pre-canceled") cancellation.Cancel();
        AgentLoopResult? result = null;
        try { result = await runner.RunAsync(initial, Callbacks(), new Sink((_, _) => ValueTask.CompletedTask), cancellation.Token).WaitAsync(Deadline); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Equal(0, adapter.Executions); Equal(0, policy.Authorizations);
        if (failure == "pre-canceled") Equal(0, sends);
        else
        {
            Check(result is not null, "A provider admission control did not settle.");
            if (failure == "invalid-schema") Equal(ToolFailureKind.InvalidArguments, result!.Turns[0].Result.Tools.Outcomes.Single().Result.Failure!.Kind);
            else if (failure == "truncated") Equal(ToolFailureKind.Truncated, result!.Turns[0].Result.Tools.Outcomes.Single().Result.Failure!.Kind);
            else Check(result!.Turns[0].Result.Chat.Failure is not null, "Malformed provider input became authoritative.");
            Check(content.Disposed, "Rejected tool authority retained a response.");
        }
        Evidence.Add(new { id = failure, nativeControl = true, sends, adapter.Executions, policy.Authorizations });
    }

    private static (ModelDescriptor Model, CompletionsKeyAuthRequestFactory Factory) SelectedFactory(JsonElement source)
    {
        var raw = JsonNode.Parse(source.GetProperty("model").GetRawText())!.AsObject(); raw["type"] = "chat";
        var provider = raw["provider"]!.GetValue<string>(); var api = raw["api"]!.GetValue<string>(); var id = raw["id"]!.GetValue<string>();
        var catalogBytes = Encoding.UTF8.GetBytes(new JsonObject { [api] = new JsonObject { ["chat:" + id] = raw } }.ToJsonString());
        var catalog = FrozenModelCatalog.ReadProviderJson(provider, catalogBytes);
        Check(catalog.TryGetModel(CatalogModelType.Chat, id, out var selected), "Authored selected model was not admitted by the actual catalog.");
        var supportsImages = selected!.Raw.Value.GetProperty("input").EnumerateArray().Any(value => value.GetString() == "image");
        Equal(source.GetProperty("profile").GetProperty("supportsImages").GetBoolean(), supportsImages);
        var compatibility = selected.Raw.Value.GetProperty("compat");
        var projection = new CompletionsTranscriptProjectionOptions(
            RequiresAssistantAfterToolResult: compatibility.GetProperty("requiresAssistantAfterToolResult").GetBoolean(),
            RequiresToolResultName: compatibility.GetProperty("requiresToolResultName").GetBoolean()) { ModelSupportsImages = supportsImages };
        var model = new ModelDescriptor(selected.Id, selected.DeclaredApi, selected.Provider);
        return (model, new CompletionsKeyAuthRequestFactory(new Uri(selected.BaseUrl + "/chat/completions"), model, projection));
    }
    private static AgentLoopCallbacks Callbacks() => new((snapshot, _) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript, 123)));
    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository missing.");
        var bytes = File.ReadAllBytes(Path.Combine(directory.FullName, "fixtures/native/completions-image-agent-source.json"));
        Equal(FixtureHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonDocument.Parse(bytes);
    }
    private sealed class Adapter(JsonElement source) : IInvocationPreparedToolAdapter
    {
        public string Name => "inspect"; private int _executions;
        public int Executions => _executions;
        public ConcurrentDictionary<string, PreparedToolAction> Actions { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, JsonData> RawResults { get; } = new(StringComparer.Ordinal);
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(
            new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path, "authored-safe-target", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            var value = action.Arguments.Value;
            return ValueTask.FromResult(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 2 &&
                value.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var finite) && double.IsFinite(finite) &&
                value.TryGetProperty("keep", out var keep) && keep.ValueKind == JsonValueKind.Null);
        }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Invocation identity was discarded.");
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action, ToolProgressCallback progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Actions[invocation.Call.Id] = action; Interlocked.Increment(ref _executions);
            var expected = source.GetProperty("rawResults").EnumerateArray().Single(result => result.GetProperty("id").GetString() == invocation.Call.Id).GetProperty("result");
            var raw = JsonData.Parse("{\"content\":" + expected.GetProperty("content").GetRawText() +
                ",\"details\":{\"keep\":null,\"n\":1.00},\"usage\":{\"raw\":1.00,\"keep\":null},\"opaque\":{\"retained\":[2,1]}}");
            RawResults[invocation.Call.Id] = raw;
            return ValueTask.FromResult(ToolResult.FromJson(raw));
        }
    }
    private sealed class Policy : IToolActionPolicy
    {
        private int _authorizations; public int Authorizations => _authorizations;
        public ConcurrentDictionary<string, PreparedToolAction> Actions { get; } = new(StringComparer.Ordinal);
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions[invocation.Call.Id] = action; Interlocked.Increment(ref _authorizations); return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Hooks : IToolHooks
    {
        private int _preflights; public int Preflights => _preflights;
        public ConcurrentDictionary<string, JsonData> AfterResults { get; } = new(StringComparer.Ordinal);
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _preflights); return ValueTask.FromResult(ToolPreflightDecision.Allow); }
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token)
        { token.ThrowIfCancellationRequested(); AfterResults[invocation.Call.Id] = result.ToJson(); return ValueTask.FromResult(result); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        public bool Disposed;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected body buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
    private sealed class OwnedBody(string wire, bool gated = false) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool Disposed; public int AsyncDisposeCalls; private Task? _cleanup;
        public override ValueTask DisposeAsync() => new(_cleanup ??= CloseCore());
        private async Task CloseCore() { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); if (gated) await ReleaseCleanup.Task; Disposed = true; base.Dispose(true); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void EqualOwnedProperties(JsonData expected, JsonData actual)
    {
        // The owned immutable property map does not promise object property ordering.
        // Every retained property must still preserve its exact value tokens and presence.
        Equal(expected.Value.EnumerateObject().Count(), actual.Value.EnumerateObject().Count());
        foreach (var property in expected.Value.EnumerateObject())
        {
            Check(actual.Value.TryGetProperty(property.Name, out var value), "Owned result lost property " + property.Name);
            Equal(property.Value.GetRawText(), value.GetRawText());
        }
    }
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
