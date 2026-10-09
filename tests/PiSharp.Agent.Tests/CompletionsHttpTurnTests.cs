using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class CompletionsHttpTurnTests
{
    private static readonly ModelDescriptor Model = new("agent-http-model", "openai-completions", "openai");
    // The wire carries {"value":1.00,"keep":null}; pi-ai finalizes tool-call arguments with parseStreamingJson
    // (packages/ai/src/api/openai-completions.ts:464, packages/ai/src/utils/json-parse.ts:104-110), so the call the tool
    // receives and the assistant message record carry the JavaScript value {"value":1,"keep":null}.
    private const string Arguments = "{\"value\":1,\"keep\":null}";
    private const string Tool = """{"choices":[{"delta":{"tool_calls":[{"index":9,"id":"call-inspect","type":"function","function":{"name":"inspect","arguments":"{\"value\":1.00,\"keep\":null}"}}]}}]}""";
    private const string ToolFinish = """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-http-agent.actual-invoker-two-turns-cleanup-message-barriers-and-source-request-replay", () => ToolRoundTrip());
        yield return ("completions-http-agent.inline-user-image-two-turn-source-requests-canonical-retention-and-tool-barriers", () => ToolRoundTrip(userImages: true, supportsImages: true));
        yield return ("completions-http-agent.text-only-image-placeholder-two-turn-source-requests-and-tool-barriers", () => ToolRoundTrip(userImages: true, supportsImages: false));
        yield return ("completions-http-agent.malformed-truncated-rejected-and-invalid-schema-have-no-tool-authority", NoAuthorityOnFailure);
    }
    private static async Task ToolRoundTrip(bool userImages = false, bool supportsImages = false)
    {
        var firstBody = new OwnedBody(Sse(Tool, ToolFinish, "[DONE]"), gated: true);
        var secondBody = new OwnedBody(Sse("""{"choices":[{"delta":{"content":"Observed \u03c0\u0000"}}]}""", """{"choices":[{"delta":{},"finish_reason":"stop"}]}""", "[DONE]"));
        var responses = new[] { new BodyContent(firstBody), new BodyContent(secondBody) };
        var requests = new List<HttpRequestMessage>(); var bodies = new List<JsonData>(); Exception? observedFailure = null;
        using var handler = new Handler(async (request, token) =>
        {
            try
            {
                requests.Add(request); bodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
                Equal("Bearer authored-inert-key", request.Headers.GetValues("authorization").Single());
                Check(requests.Count <= 2, "Agent issued an unexpected provider generation.");
                return new(HttpStatusCode.OK) { Content = responses[requests.Count - 1] };
            }
            catch (Exception error) { observedFailure = error; throw; }
        });
        using var client = new HttpClient(handler); var factory = new CompletionsKeyAuthRequestFactory(new Uri("https://agent-completions.invalid/v1/chat/completions"), Model, new() { ModelSupportsImages = supportsImages });
        var transport = new CompletionsHttpSseTransport(client, (request, token) => factory.Create(request, "authored-inert-key", token));
        var adapter = new Adapter(); var policy = new Policy(); var invoker = new ToolInvoker([adapter], policy);
        var assistantEntered = Gate(); var releaseAssistant = Gate(); var resultEntered = Gate(); var releaseResult = Gate();
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is AssistantMessageEnded && requests.Count == 1)
            {
                Check(firstBody.Disposed && responses[0].Disposed, "Assistant commit preceded owned HTTP cleanup.");
                await ThrowsAsync<ObjectDisposedException>(() => requests[0].Content!.ReadAsStringAsync());
                assistantEntered.TrySetResult(); await releaseAssistant.Task;
            }
            if (observation is ToolResultMessageEnded)
            { resultEntered.TrySetResult(); await releaseResult.Task; }
        });
        var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("inspect", invoker)])), Model, () => 123);
        var initial = Inputs();
        if (userImages) initial = initial.SetItem(1, new("user", JsonData.Parse("""{"role":"user","content":[{"type":"text","text":"Use inspect."},{"type":"image","data":"AA==","mimeType":"image/png"},{"type":"text","text":""}],"timestamp":123} """)));
        var initialBodies = initial.Select(entry => entry.WireBody.ToString()).ToArray();
        var callbacks = new AgentLoopCallbacks((snapshot, _) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript, 123)));
        using var cancellation = new CancellationTokenSource(); var running = runner.RunAsync(initial, callbacks, sink, cancellation.Token);
        try
        {
            if (userImages)
            {
                await Task.WhenAny(firstBody.CleanupEntered.Task, running).WaitAsync(Deadline);
                if (!firstBody.CleanupEntered.Task.IsCompleted)
                    throw new InvalidOperationException("Inline image request never reached HTTP response cleanup; agent stopped with " + (await running).Reason + ".");
            }
            await firstBody.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted && !assistantEntered.Task.IsCompleted && adapter.Executions == 0 && policy.Authorizations == 0, "HTTP cleanup did not guard assistant commit and tool preflight.");
            _ = await requests.Single().Content!.ReadAsStringAsync();
            firstBody.ReleaseCleanup.TrySetResult(); await assistantEntered.Task.WaitAsync(Deadline);
            Equal(0, adapter.Executions); Equal(0, policy.Authorizations); releaseAssistant.TrySetResult();
            await resultEntered.Task.WaitAsync(Deadline); Equal(1, adapter.Executions); Equal(1, policy.Authorizations); Equal(1, requests.Count);
            Equal(Arguments, adapter.Action!.Arguments.ToString()); Check(ReferenceEquals(adapter.Action, policy.Action), "Policy and effect did not receive the same final prepared action.");
            releaseResult.TrySetResult(); var result = await running.WaitAsync(Deadline);
            if (observedFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observedFailure).Throw();
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(2, result.Turns.Length); Equal(2, requests.Count); Equal(5, result.Transcript.Length);
            Equal("Observed \u03c0\0", ((TextContent)result.Turns[1].Result.Chat.Message.Content.Single()).Text);
            Equal(Arguments, ((ToolCallContent)result.Turns[0].Result.Chat.Message.Content.Single()).Arguments.ToString());
            var canonicalResult = result.Transcript[3].WireBody.Value;
            Equal(JsonValueKind.Null, canonicalResult.GetProperty("details").ValueKind);
            Equal("1.00", canonicalResult.GetProperty("usage").GetProperty("raw").GetRawText());
            var projected = bodies[1].Value.GetProperty("messages"); Equal(4, projected.GetArrayLength());
            Equal("{\"value\":1,\"keep\":null}", projected[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
            Equal("tool", projected[3].GetProperty("role").GetString()); Equal("call-inspect", projected[3].GetProperty("tool_call_id").GetString());
            Equal("owned \u03c0\0", projected[3].GetProperty("content").GetString());
            Check(!projected[3].TryGetProperty("details", out _) && !projected[3].TryGetProperty("usage", out _), "Provider projection forwarded retained result metadata absent from source payload.");
            Equal(1, firstBody.AsyncDisposeCalls); Equal(1, secondBody.AsyncDisposeCalls); Check(responses.All(value => value.Disposed), "Completed tool loop retained an HTTP response.");
            Check(!handler.Disposed, "Agent composition disposed the borrowed HTTP client.");
            if (userImages)
            {
                Check(initialBodies.SequenceEqual(result.Transcript.Take(initial.Length).Select(entry => entry.WireBody.ToString())), "Image canonical history changed during the tool loop.");
                using var fixture = ImageFixture(); var observations = fixture.RootElement.GetProperty("observations");
                foreach (var (profile, body) in new[] { ("agent-first-image-declaration", bodies[0]), ("agent-image-history-replay", bodies[1]) })
                {
                    var id = profile + (supportsImages ? "-vision" : "");
                    var source = observations.EnumerateArray().Single(value => value.GetProperty("id").GetString() == id).GetProperty("requests")[0].GetProperty("bodyJson");
                    Check(JsonElement.DeepEquals(source, body.Value), "Actual HTTP agent request differs from unchanged Pi/SDK capture: " + profile);
                }
            }
        }
        finally { releaseAssistant.TrySetResult(); releaseResult.TrySetResult(); firstBody.ReleaseCleanup.TrySetResult(); cancellation.Cancel(); await Observe(running); }
    }
    private static async Task NoAuthorityOnFailure()
    {
        foreach (var (wire, status, failure) in new[]
        {
            // Truncated arguments {"value": finalize through parseStreamingJson to {} (packages/ai/src/utils/json-parse.ts:112-114), a
            // toolUse assistant whose call packages/agent/src/agent-loop.ts:727 validateToolArguments rejects: an InvalidArguments result, no effect.
            (Sse(Tool.Replace("1.00,\\\"keep\\\":null}", "", StringComparison.Ordinal), ToolFinish), HttpStatusCode.OK, ToolFailureKind.InvalidArguments),
            (Sse(Tool, """{"choices":[{"delta":{},"finish_reason":"length"}]}""", "[DONE]"), HttpStatusCode.OK, ToolFailureKind.Truncated),
            ("event: error\ndata: {\"error\":{\"message\":\"private\"}}\n\n", HttpStatusCode.OK, ToolFailureKind.ExecutionError),
            (Sse(Tool, ToolFinish), HttpStatusCode.TooManyRequests, ToolFailureKind.ExecutionError),
            (Sse(Tool.Replace("1.00", "\\\"wrong-type\\\"", StringComparison.Ordinal), ToolFinish), HttpStatusCode.OK, ToolFailureKind.InvalidArguments)
        })
        {
            var adapter = new Adapter(); var policy = new Policy(); var content = new BodyContent(new OwnedBody(wire));
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content })); using var client = new HttpClient(handler);
            var factory = Factory(); var transport = new CompletionsHttpSseTransport(client, (request, token) => factory.Create(request, "authored-inert-key", token));
            var runner = new TurnRunner(new ChatClient(transport), new ToolBatchScheduler([new("inspect", new ToolInvoker([adapter], policy))]));
            var result = await runner.RunAsync(new(Model, Inputs(), 123), new Sink((_, _) => ValueTask.CompletedTask));
            Equal(0, adapter.Executions); Equal(0, policy.Authorizations); Check(content.Disposed, "Rejected provider/tool admission retained its response.");
            if (failure is ToolFailureKind.Truncated or ToolFailureKind.InvalidArguments)
                Equal(failure, result.Tools.Outcomes.Single().Result.Failure!.Kind);
            else Check(result.Chat.Failure is not null && result.Chat.Message.StopReason == StopReason.Error, "Malformed/HTTP failure authorized a successful assistant.");
        }
    }
    private static CompletionsKeyAuthRequestFactory Factory() => new(new Uri("https://agent-completions.invalid/v1/chat/completions"), Model);
    private static JsonDocument ImageFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository missing.");
        var bytes = File.ReadAllBytes(Path.Combine(directory.FullName, "fixtures/native/completions-user-images-source.json"));
        Equal("eb5f5e5c1c4170e3e997ac5d9738d520f3044012bbcc165c3714f61edd9e22a0", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonDocument.Parse(bytes);
    }
    private static ImmutableArray<TranscriptEntry> Inputs() =>
    [
        new("system", JsonData.Parse("""{"role":"system","content":"Inspect once.","toolsAdded":[{"name":"inspect","description":"An authored inert adapter.","parameters":{"type":"object","properties":{"value":{"type":"number"},"keep":{"type":"null"}},"required":["value","keep"],"additionalProperties":false}}],"timestamp":123}""")),
        new("user", JsonData.Parse("""{"role":"user","content":"Use inspect.","timestamp":123}"""))
    ];
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "inspect";
        public int Executions;
        public PreparedToolAction? Action;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path,
            "authored-safe-target", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            var value = action.Arguments.Value;
            return ValueTask.FromResult(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 2 &&
                value.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var finite) && double.IsFinite(finite) &&
                value.TryGetProperty("keep", out var keep) && keep.ValueKind == JsonValueKind.Null);
        }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Action = action; Executions++; return ValueTask.FromResult(ToolResult.FromJson(JsonData.Parse("""{"content":[{"type":"text","text":"owned \u03c0\u0000"}],"details":null,"usage":{"raw":1.00,"keep":null},"opaque":{"retained":[2,1]}}"""))); }
    }
    private sealed class Policy : IToolActionPolicy
    {
        public int Authorizations; public PreparedToolAction? Action;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Action = action; Authorizations++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
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
        public bool Disposed; public int AsyncDisposeCalls;
        private Task? _cleanup;
        public override ValueTask DisposeAsync() => new(_cleanup ??= CloseCore());
        private async Task CloseCore() { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); if (gated) await ReleaseCleanup.Task; Disposed = true; base.Dispose(true); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Sse(params string[] chunks) => string.Concat(chunks.Select(value => "data: " + value + "\r\n\r\n"));
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
