using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Literal pinned Source expectations; authored offline native consumers, never SDK/Source captures.
internal static class GoogleSimpleCases
{
    private const string Key = "inert-google-simple-private-key";
    private const string ToolWire = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"id\":\"one\",\"name\":\"inspect\",\"args\":{\"value\":7}}}]},\"finishReason\":\"STOP\"}]}\n\n";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonData Vectors() => JsonData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "google-simple-cases.json")));
    private static JsonElement EmptyRow => JsonData.Parse("{}").Value;
    internal static IEnumerable<(string Id, Func<Task> Run)> Cases()
    {
        yield return ("google.simple-context-and-max-token-resolution", ContextResolution);
        yield return ("google.simple-model-reasoning-and-budgets", Thinking);
        yield return ("google.simple-admission-before-effects", Admission);
        yield return ("google.simple-real-http-hooks-and-retry", HttpBinding);
        yield return ("google.simple-cancellation-original-joins", Cancellation);
        yield return ("google.simple-actual-agent-tool-authority", AgentAuthority);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static async Task Join(Task task) { try { await task; } catch { } }
    private static ModelDescriptor Model(JsonElement row) => new(row.TryGetProperty("modelId", out var id) ? id.GetString()! : "gemini-2.5-flash", "google-generative-ai", "google");
    private static GoogleSimpleOptions Options(JsonElement row)
    {
        var model = Model(row);
        var value = JsonNode.Parse("""{"type":"chat","id":"","api":"google-generative-ai","provider":"google","name":"authored","baseUrl":"https://google.invalid/v1beta","reasoning":true,"input":["text","image"],"contextWindow":100000,"maxTokens":1000,"cost":{"input":2,"output":3,"cacheRead":0.5,"cacheWrite":0},"headers":{"x-model":"owned"}}""")!.AsObject();
        value["id"] = model.Id;
        foreach (var name in new[] { "contextWindow", "maxTokens" }) if (row.TryGetProperty(name, out var field)) value[name] = JsonNode.Parse(field.GetRawText());
        if (row.TryGetProperty("reasoningModel", out var reasoningModel)) value["reasoning"] = reasoningModel.GetBoolean();
        if (row.TryGetProperty("map", out var map)) value["thinkingLevelMap"] = JsonNode.Parse(map.GetRawText());
        var direct = new GoogleGenerativeAIOptions(JsonData.Parse(value.ToJsonString()), Key)
        { MaxTokens = row.TryGetProperty("callerMaxTokens", out var maximum) ? maximum.GetDouble() : null };
        return new(direct, row.TryGetProperty("reasoning", out var reasoning) ? reasoning.GetString() : null)
        { ThinkingBudgets = row.TryGetProperty("customBudgets", out var budgets) ? JsonData.FromElement(budgets) : null };
    }
    private static ImmutableArray<TranscriptEntry> Inputs() => [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":123}"""))];
    private static ChatRequest Request(ModelDescriptor model, JsonElement row) => new(model,
        row.TryGetProperty("messages", out var messages) ? messages.EnumerateArray().Select(message =>
            new TranscriptEntry(message.GetProperty("role").GetString()!, JsonData.FromElement(message))).ToImmutableArray() : Inputs(), 123);
    private static Task ContextResolution()
    {
        foreach (var row in Vectors().Value.GetProperty("contextVectors").EnumerateArray())
        {
            var model = Model(row); var options = Options(row); var request = Request(model, row);
            using var fixture = new Fixture(model, options); var before = request.Messages.Select(x => x.WireBody.ToString()).ToArray();
            var metadata = options.DirectOptions.ModelMetadata.ToString(); var result = fixture.Factory.Resolve(request); var expected = row.GetProperty("expected");
            Equal(expected.GetProperty("tokens").GetDouble(), result.ContextEstimate.Tokens);
            Equal(expected.GetProperty("usageTokens").GetDouble(), result.ContextEstimate.UsageTokens);
            Equal(expected.GetProperty("trailingTokens").GetDouble(), result.ContextEstimate.TrailingTokens);
            var index = expected.GetProperty("lastUsageIndex"); Equal(index.ValueKind == JsonValueKind.Null ? (int?)null : index.GetInt32(), result.ContextEstimate.LastUsageIndex);
            Equal(expected.GetProperty("maxTokens").GetDouble(), result.MaxTokens);
            Check(before.SequenceEqual(request.Messages.Select(x => x.WireBody.ToString())), "Simple mutated caller transcript.");
            Equal(metadata, options.DirectOptions.ModelMetadata.ToString()); Equal(0, fixture.Requests.Count); Equal(0, fixture.PayloadCalls);
            Check(!fixture.Handler.Disposed, "Pure resolution owned borrowed client.");
        }
        return Task.CompletedTask;
    }
    private static Task Thinking()
    {
        foreach (var row in Vectors().Value.GetProperty("thinkingVectors").EnumerateArray())
        {
            var model = Model(row); var options = Options(row); var request = Request(model, row); using var fixture = new Fixture(model, options);
            var result = fixture.Factory.Resolve(request); var projected = GoogleRequestProjector.Project(request,
                options.DirectOptions with { MaxTokens = result.MaxTokens, Thinking = result.Thinking }).Value.GetProperty("config");
            var expected = row.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null) Check(!projected.TryGetProperty("thinkingConfig", out _), "Nonreasoning model gained thinking fields.");
            else Check(JsonElement.DeepEquals(expected, projected.GetProperty("thinkingConfig")), "Simple thinking differs from literal pinned expectation: " + row.GetProperty("id").GetString());
            if (row.TryGetProperty("expectedClamped", out var clamped)) Equal(clamped.GetString(), result.ClampedReasoning);
            if (row.TryGetProperty("supported", out var supported)) Check(supported.EnumerateArray().Select(x => x.GetString()).SequenceEqual(result.SupportedLevels), "Supported level domain differs.");
            if (row.TryGetProperty("callerMaxTokens", out var maximum)) Equal(maximum.GetDouble(), result.MaxTokens);
            Equal(0, fixture.Requests.Count); Equal(0, fixture.PayloadCalls);
        }
        return Task.CompletedTask;
    }
    private static async Task Admission()
    {
        foreach (var scenario in new[] { "key-null", "key-empty", "metadata-id", "metadata-api", "context-type", "reasoning", "budget-null", "map-type", "message-count", "context-characters", "role", "timestamp", "usage-overflow", "unused-usage-number", "mapped-target" })
        {
            var model = Model(EmptyRow); var options = Options(EmptyRow); var request = Request(model, EmptyRow); var payload = 0;
            var metadata = JsonNode.Parse(options.DirectOptions.ModelMetadata.ToString())!.AsObject();
            switch (scenario)
            {
                case "key-null": options = options with { DirectOptions = options.DirectOptions with { ApiKey = null } };
                    request = request with { Messages = [new("user", JsonData.Parse("""{"role":"user","content":"hello"}"""))] }; break;
                case "key-empty": options = options with { DirectOptions = options.DirectOptions with { ApiKey = "" } }; break;
                case "metadata-id": metadata["id"] = "other"; break;
                case "metadata-api": metadata["api"] = "other-api"; break;
                case "context-type": metadata["contextWindow"] = "invalid"; break;
                case "reasoning": options = options with { Reasoning = "unsupported" }; break;
                case "budget-null": options = options with { ThinkingBudgets = JsonData.Parse("""{"low":null}""") }; break;
                case "map-type": metadata["thinkingLevelMap"] = JsonNode.Parse("""{"low":true}"""); break;
                case "message-count": options = options with { MaximumContextMessages = 1 }; request = request with { Messages = [.. Inputs(), .. Inputs()] }; break;
                case "context-characters": options = options with { MaximumContextCharacters = 1 }; break;
                case "role": request = request with { Messages = [new("user", JsonData.Parse("""{"role":"system","content":"hello","timestamp":1}"""))] }; break;
                case "timestamp": request = request with { Messages = [new("user", JsonData.Parse("""{"role":"user","content":"hello"}"""))] }; break;
                case "usage-overflow": request = request with { Messages = [new("assistant", JsonData.Parse("""{"role":"assistant","content":[],"timestamp":1,"stopReason":"stop","usage":{"totalTokens":0,"input":1e308,"output":1e308,"cacheRead":0,"cacheWrite":0}}"""))] }; break;
                case "unused-usage-number": request = request with { Messages = [new("assistant", JsonData.Parse("""{"role":"assistant","content":[],"timestamp":1,"stopReason":"stop","usage":{"totalTokens":100,"input":1e999,"output":0,"cacheRead":0,"cacheWrite":0}}"""))] }; break;
                case "mapped-target": options = options with { Reasoning = "low" }; metadata["thinkingLevelMap"] = JsonNode.Parse("""{"low":"unsupported"}"""); break;
            }
            options = options with { DirectOptions = options.DirectOptions with { ModelMetadata = JsonData.Parse(metadata.ToJsonString()),
                Hooks = new() { OnPayload = (_, _, _) => { payload++; return ValueTask.FromResult<JsonData?>(null); } } } };
            using var handler = new Handler((_, _) => throw new InvalidOperationException("HTTP before admission.")); using var client = new HttpClient(handler);
            try
            {
                var factory = new GoogleSimpleRequestFactory(client, model, options);
                if (scenario is "key-null" or "key-empty") _ = factory.StreamAsync(request); else _ = factory.Resolve(request);
                throw new InvalidOperationException("Invalid Simple data admitted: " + scenario);
            }
            catch (GoogleGenerativeAIException error)
            {
                Equal(scenario is "key-null" or "key-empty" ? GoogleFailure.MissingKey :
                    scenario is "message-count" or "context-characters" ? GoogleFailure.ResourceLimit :
                    scenario is "metadata-id" or "metadata-api" ? GoogleFailure.Configuration : GoogleFailure.UnsupportedValue, error.Failure);
                Check(!error.Message.Contains(Key, StringComparison.Ordinal), "Simple admission exposed private key.");
            }
            if (scenario == "mapped-target")
            {
                var result = await new ChatClient(new GoogleSimpleRequestFactory(client, model, options)).CompleteAsync(request);
                Equal(new NativeChatDiagnostic(NativeChatAdapter.GoogleGenerativeAI, NativeChatFailureCode.UnsupportedFeature), result.NativeDiagnostic);
                Equal(result.NativeDiagnostic, result.Failure!.NativeDiagnostic);
            }
            Equal(0, payload); Check(!handler.Disposed && !options.ToString().Contains(Key, StringComparison.Ordinal), "Simple admission mutated client or exposed options.");
        }
    }
    private sealed record Outcome(StreamTerminalEvent Terminal, ChatResult? Result);
    private static async Task<Outcome> Consume(Fixture fixture, ChatRequest request, bool shared, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, fixture.Cancellation.Token); ChatRun? run = null;
        try
        {
            if (shared) run = await new ChatClient(fixture.Factory, 1).StartAsync(request, linked.Token);
            StreamTerminalEvent? terminal = null; var terminals = 0;
            await foreach (var value in shared ? run!.ReadEventsAsync() : fixture.Factory.StreamAsync(request, linked.Token))
            { if (value is StreamTerminalEvent end) { terminal = end; terminals++; } }
            Equal(1, terminals); return new(terminal!, run is null ? null : await run.Completion);
        }
        finally { if (run is not null) await run.DisposeAsync(); }
    }
    private static void AssertOutcome(Outcome outcome, bool cancelled = false)
    {
        var expected = cancelled ? new NativeChatDiagnostic(NativeChatAdapter.GoogleGenerativeAI, NativeChatFailureCode.Cancelled) : null;
        Equal(expected, outcome.Terminal.NativeDiagnostic); Equal<NativeChatDiagnostic?>(null, outcome.Terminal.NativeCleanupDiagnostic);
        Check(cancelled ? outcome.Terminal is StreamError : outcome.Terminal is StreamDone, "Simple terminal has wrong authority.");
        if (outcome.Result is { } result)
        {
            Equal(expected, result.NativeDiagnostic); Equal(expected, result.Failure?.NativeDiagnostic); Equal<NativeChatDiagnostic?>(null, result.NativeCleanupDiagnostic);
            Check(JsonElement.DeepEquals(PiWireJson.WriteMessage(result.Message).Value, PiWireJson.WriteMessage(outcome.Terminal.Message).Value), "Simple terminal/result messages differ.");
        }
        Check(JsonElement.DeepEquals(PiWireJson.WriteEvent(outcome.Terminal).Value,
            PiWireJson.WriteEvent(outcome.Terminal with { NativeDiagnostic = null, NativeCleanupDiagnostic = null }).Value), "Simple native diagnostic entered Pi wire.");
        Check(!PiWireJson.WriteEvent(outcome.Terminal).ToString().Contains(Key, StringComparison.Ordinal), "Simple private key entered output.");
    }
    private static async Task HttpBinding()
    {
        foreach (var scenario in new[] { "direct", "shared", "retry" })
        {
            var row = JsonData.Parse("""{"modelId":"gemini-2.5-pro","reasoning":"low","callerMaxTokens":17}""").Value;
            var model = Model(row); var options = Options(row); var metadata = options.DirectOptions.ModelMetadata.ToString();
            options = options with { DirectOptions = options.DirectOptions with { MaxRetries = scenario == "retry" ? 1 : 0,
                Headers = JsonData.Parse("""{"accept":null,"x-simple":"owned"}"""), Hooks = new() { OnPayload = (payload, observation, _) => {
                    Equal(metadata, observation.Raw.ToString()); var config = payload.Value.GetProperty("config");
                    Equal(17d, config.GetProperty("maxOutputTokens").GetDouble()); Equal(2048d, config.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetDouble());
                    var replacement = JsonNode.Parse(payload.ToString())!.AsObject(); replacement["config"]!["maxOutputTokens"] = 7;
                    return ValueTask.FromResult<JsonData?>(JsonData.Parse(replacement.ToJsonString()));
                } } } };
            using var fixture = new Fixture(model, options, scenario == "retry" ? [429, 200] : [200]);
            var outcome = await Consume(fixture, Request(model, row), scenario != "direct"); AssertOutcome(outcome);
            Equal(StopReason.ToolUse, outcome.Terminal.Reason); Equal(1, fixture.PayloadCalls); Equal(scenario == "retry" ? 2 : 1, fixture.Requests.Count);
            for (var i = 0; i < fixture.Requests.Count; i++)
            {
                fixture.AssertReleased(i); var body = JsonData.Parse(fixture.RequestBodies[i]).Value.GetProperty("generationConfig");
                Equal(7d, body.GetProperty("maxOutputTokens").GetDouble()); Equal(2048d, body.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetDouble());
                Equal(Key, fixture.Requests[i].Headers.GetValues("x-goog-api-key").Single()); Equal("owned", fixture.Requests[i].Headers.GetValues("x-simple").Single());
                Check(!fixture.Requests[i].Headers.Contains("accept"), "Simple retry restored removed header.");
                if (i > 0) { Check(!ReferenceEquals(fixture.Requests[0], fixture.Requests[i]), "Simple retry reused request."); Equal(fixture.RequestBodies[0], fixture.RequestBodies[i]); }
            }
            Check(!fixture.Handler.Disposed, "Simple binding owned borrowed client.");
        }
    }
    private static async Task Cancellation()
    {
        foreach (var seam in new[] { "payload", "send", "cleanup", "preaborted" })
        {
            using var cancellation = new CancellationTokenSource(); var model = Model(EmptyRow);
            using var fixture = new Fixture(model, Options(EmptyRow));
            fixture.HoldPayload = seam == "payload"; fixture.HoldSend = seam == "send"; fixture.HoldBody = seam == "cleanup";
            if (seam == "preaborted") cancellation.Cancel();
            var original = Consume(fixture, Request(model, EmptyRow), true, cancellation.Token);
            try
            {
                if (seam != "preaborted")
                {
                    await (seam == "payload" ? fixture.PayloadEntered.Task : seam == "send" ? fixture.SendEntered.Task : fixture.CleanupEntered.Task).WaitAsync(Deadline);
                    cancellation.Cancel(); Check(!original.IsCompleted, "Simple cancellation detached original " + seam + "."); fixture.ReleaseAll();
                }
                AssertOutcome(await original, cancelled: true);
                Equal(seam is "preaborted" or "payload" ? 0 : 1, fixture.Requests.Count); Equal(seam == "preaborted" ? 0 : 1, fixture.PayloadCalls);
                for (var i = 0; i < fixture.Requests.Count; i++) fixture.AssertReleased(i, requireAsyncBody: seam != "send");
                Check(!fixture.Handler.Disposed, "Simple cancel owned borrowed client.");
            }
            finally { fixture.Cancel(); fixture.ReleaseAll(); await Join(original); }
        }
    }
    private static async Task AgentAuthority()
    {
        foreach (var cancel in new[] { false, true })
        {
            var row = JsonData.Parse("""{"modelId":"gemini-2.5-pro","reasoning":"high"}""").Value; var model = Model(row);
            using var fixture = new Fixture(model, Options(row)); fixture.HoldBody = true; var executor = new Executor(); StreamTerminalEvent? terminal = null;
            await using var agent = new NativeAgent(new(model, fixture.Factory, [new("inspect", executor)],
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
                new Sink((value, _) => { if (value is TurnStreamObserved { Event: StreamTerminalEvent end }) terminal = end; return ValueTask.CompletedTask; }));
            Task<AgentLoopResult>? original = null;
            try
            {
                original = agent.PromptAsync(Inputs()); await fixture.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && terminal is null && executor.Count == 0, "Simple gained tool authority before original release.");
                if (cancel) agent.Abort(); fixture.ReleaseAll(); var chat = (await original).Turns.Single().Result.Chat;
                Equal(cancel ? 0 : 1, executor.Count); Equal(1, fixture.PayloadCalls); Equal(1, fixture.Requests.Count); fixture.AssertReleased(0);
                var expected = cancel ? new NativeChatDiagnostic(NativeChatAdapter.GoogleGenerativeAI, NativeChatFailureCode.Cancelled) : null;
                Equal(expected, chat.NativeDiagnostic); Equal(expected, terminal!.NativeDiagnostic); Equal(expected, chat.Failure?.NativeDiagnostic);
                Equal(32768d, JsonData.Parse(fixture.RequestBodies[0]).Value.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetDouble());
                Check(!agent.Snapshot.IsRunning && !fixture.Handler.Disposed, "Simple Agent ownership did not settle.");
            }
            finally { agent.Abort(); fixture.ReleaseAll(); if (original is not null) await Join(original); await agent.WaitForIdleAsync(); }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler; private readonly HttpClient _client; internal readonly GoogleSimpleRequestFactory Factory;
        internal readonly CancellationTokenSource Cancellation = new(); internal readonly List<HttpRequestMessage> Requests = [];
        internal readonly List<string> RequestBodies = []; internal readonly List<RequestContent> RequestContents = []; internal readonly List<BodyContent> Responses = []; internal readonly List<Body> Bodies = [];
        internal readonly TaskCompletionSource PayloadEntered = Gate(), ReleasePayload = Gate(), SendEntered = Gate(), ReleaseSend = Gate(), CleanupEntered = Gate(), ReleaseCleanup = Gate();
        internal bool HoldPayload, HoldSend, HoldBody; internal int PayloadCalls;
        internal Fixture(ModelDescriptor model, GoogleSimpleOptions options, int[]? statuses = null)
        {
            var responseStatuses = statuses ?? [200]; var direct = options.DirectOptions; var hooks = direct.Hooks;
            options = options with { DirectOptions = direct with { Hooks = hooks with { OnPayload = async (payload, observation, token) => {
                PayloadCalls++; if (HoldPayload) { PayloadEntered.TrySetResult(); await ReleasePayload.Task; }
                return hooks.OnPayload is { } hook ? await hook(payload, observation, token) : null;
            } } } };
            Handler = new(async (request, _) => {
                var index = Requests.Count; Requests.Add(request); RequestBodies.Add(await request.Content!.ReadAsStringAsync());
                var owner = new RequestContent(request.Content!); request.Content = owner; RequestContents.Add(owner);
                var status = responseStatuses[Math.Min(index, responseStatuses.Length - 1)]; var body = new Body(status == 200 ? ToolWire : "authored rejected response", this); Bodies.Add(body);
                var content = new BodyContent(body); Responses.Add(content); var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
                if (status != 200) response.Headers.TryAddWithoutValidation("retry-after-ms", "0");
                if (HoldSend) { SendEntered.TrySetResult(); await ReleaseSend.Task; } return response;
            });
            _client = new(Handler); Factory = new(_client, model, options);
        }
        internal void AssertReleased(int index, bool requireAsyncBody = true) => Check(RequestContents[index].Disposed && Responses[index].Disposed &&
            (!requireAsyncBody || Bodies[index].AsyncDisposals == 1 && Bodies[index].Settled), "Simple original owners not released.");
        internal void ReleaseAll() { ReleasePayload.TrySetResult(); ReleaseSend.TrySetResult(); ReleaseCleanup.TrySetResult(); }
        internal void Cancel() => Cancellation.Cancel();
        public void Dispose() { Cancellation.Dispose(); _client.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class RequestContent : HttpContent
    {
        private readonly HttpContent _original; internal bool Disposed;
        internal RequestContent(HttpContent original) { _original = original; foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _original.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; _original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class BodyContent(Body body) : HttpContent
    {
        internal bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override void Dispose(bool disposing) { Disposed = true; body.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Body(string wire, Fixture fixture) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        internal int AsyncDisposals; internal bool Settled;
        public override async ValueTask DisposeAsync()
        { AsyncDisposals++; fixture.CleanupEntered.TrySetResult(); if (fixture.HoldBody) await fixture.ReleaseCleanup.Task; await base.DisposeAsync(); Settled = true; }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Executor : IToolExecutor
    {
        internal int Count;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { Count++; Equal(7, invocation.Call.Arguments.Value.GetProperty("value").GetInt32()); return ValueTask.FromResult(ToolResult.Success("owned")); }
    }
}
