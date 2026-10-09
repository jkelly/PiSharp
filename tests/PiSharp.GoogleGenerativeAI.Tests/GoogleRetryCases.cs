using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Handwritten expectations against pinned Pi retry source. No runtime/SDK/Source capture.
internal static class GoogleRetryCases
{
    private static readonly ModelDescriptor Model = new("gemini-3-flash-preview", "google-generative-ai", "google");
    private const string PrivateText = "inert-google-retry-private-key";
    // @google/genai ApiError message for the untyped 429 body: JSON.stringify({error:{message,code,status}}).
    private const string RejectedMessage = """{"error":{"message":"authored rejected body","code":429,"status":"Too Many Requests"}}""";
    private const string Success = """{"candidates":[{"content":{"parts":[{"text":"owned"}]},"finishReason":"STOP"}]}""";
    private const string Tool = """{"candidates":[{"content":{"parts":[{"functionCall":{"id":"one","name":"inspect","args":{"value":7}}}]},"finishReason":"STOP"}]}""";
    private static readonly JsonData Metadata = JsonData.Parse("""{"type":"chat","id":"gemini-3-flash-preview","api":"google-generative-ai","provider":"google","name":"authored","baseUrl":"https://google.invalid/v1beta","reasoning":true,"input":["text"],"contextWindow":100000,"maxTokens":1000,"cost":{"input":2,"output":3,"cacheRead":0.5,"cacheWrite":0},"headers":{"x-model":"owned"}}""");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    internal static IEnumerable<(string Id, Func<Task> Run)> Cases()
    {
        yield return ("google.retry-status-fresh-request-single-payload", StatusAndReplay);
        yield return ("google.retry-server-delay-and-native-limits", Delays);
        yield return ("google.retry-cancellation-and-rejected-cleanup", CancellationAndCleanup);
        yield return ("google.retry-no-callback-or-stream-replay", NoStreamReplay);
        yield return ("google.retry-actual-agent-ownership-authority", AgentAuthority);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static async Task Join(Task task) { try { await task; } catch { } }
    private static GoogleGenerativeAIOptions Options(Clock clock, int retries = 1) => new(Metadata, PrivateText)
    { MaxRetries = retries, RetryTimeProvider = clock, RetryJitterSample = () => 0.5, Temperature = 0.2 };
    private static ImmutableArray<TranscriptEntry> Inputs() => [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":123}"""))];
    private static ChatRequest Request() => new(Model, Inputs(), 123);
    private static string Wire(string json) => "data: " + json + "\n\n";
    private static NativeChatDiagnostic Diagnostic(NativeChatFailureCode code) => new(NativeChatAdapter.GoogleGenerativeAI, code);
    private sealed record Outcome(StreamTerminalEvent Terminal, ChatResult? Result);
    private static async Task<Outcome> Consume(Fixture fixture, bool shared, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, fixture.Cancellation.Token);
        token = linked.Token;
        ChatRun? run = null; StreamTerminalEvent? terminal = null; var terminals = 0;
        try
        {
            if (shared) run = await new ChatClient(fixture.Transport, 1).StartAsync(Request(), token);
            var events = shared ? run!.ReadEventsAsync() : fixture.Transport.StreamAsync(Request(), token);
            await foreach (var value in events)
            {
                if (value is StreamStarted) fixture.Starts++;
                if (value is StreamTerminalEvent end) { terminal = end; terminals++; }
            }
            Equal(1, terminals); return new(terminal ?? throw new InvalidOperationException("Missing terminal."),
                run is null ? null : await run.Completion);
        }
        finally { if (run is not null) await run.DisposeAsync(); }
    }
    private static void AssertOutcome(Outcome outcome, NativeChatFailureCode? primary, bool cleanup = false)
    {
        var expected = primary is { } code ? Diagnostic(code) : null;
        var secondary = cleanup ? Diagnostic(NativeChatFailureCode.CleanupFailed) : null;
        Equal(expected, outcome.Terminal.NativeDiagnostic); Equal(secondary, outcome.Terminal.NativeCleanupDiagnostic);
        Check((primary is null) == (outcome.Terminal is StreamDone), "Retry failure gained successful authority.");
        if (outcome.Result is { } result)
        {
            Equal(expected, result.NativeDiagnostic); Equal(secondary, result.NativeCleanupDiagnostic); Equal(expected, result.Failure?.NativeDiagnostic);
            Check((primary is null) == (result.Failure is null), "Retry terminal/result disagreement.");
            Check(JsonElement.DeepEquals(PiWireJson.WriteMessage(result.Message).Value, PiWireJson.WriteMessage(outcome.Terminal.Message).Value), "Retry messages differ.");
            if (primary == NativeChatFailureCode.Cancelled) Equal(ChatFailureKind.Cancelled, result.Failure!.Kind);
        }
        var plain = outcome.Terminal with { NativeDiagnostic = null, NativeCleanupDiagnostic = null };
        Check(JsonElement.DeepEquals(PiWireJson.WriteEvent(plain).Value, PiWireJson.WriteEvent(outcome.Terminal).Value), "Native retry fields entered wire.");
        Check(!PiWireJson.WriteEvent(outcome.Terminal).ToString().Contains(PrivateText, StringComparison.Ordinal), "Private request/error text leaked.");
    }
    private static JsonData Vectors() => JsonData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "google-retry-cases.json")));
    private static async Task StatusAndReplay()
    {
        foreach (var row in Vectors().Value.GetProperty("statusVectors").EnumerateArray())
        foreach (var shared in new[] { false, true })
        {
            var clock = new Clock(); var statuses = row.GetProperty("statuses").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var hooks = new GoogleGenerativeAIHooks { OnPayload = (payload, _, _) => {
                var value = JsonNode.Parse(payload.ToString())!.AsObject();
                value["config"]!.AsObject()["temperature"] = 0.75; return ValueTask.FromResult<JsonData?>(JsonData.Parse(value.ToJsonString()));
            }};
            using var fixture = new Fixture(statuses, Options(clock, row.GetProperty("maxRetries").GetInt32()) with {
                Hooks = hooks, Headers = JsonData.Parse("""{"accept":null,"x-owned":"snapshot"}""")
            });
            if (row.TryGetProperty("directive", out var directive)) fixture.Headers["x-should-retry"] = directive.GetString()!;
            var original = Consume(fixture, shared);
            try
            {
                var retries = row.GetProperty("expectedRetries").GetInt32();
                for (var i = 0; i < retries; i++)
                {
                    var timer = await clock.Next();
                    Check(!original.IsCompleted && fixture.Starts == 0 && fixture.Requests.Count == i + 1, "Backoff admitted a stream or next request early.");
                    fixture.AssertReleased(i); Equal(i, fixture.Retries[i].RetryIndex); Equal(statuses[i], fixture.Retries[i].Status);
                    Equal(Math.Min(500 * Math.Pow(2, i), 8000) * 0.875, fixture.Retries[i].Delay.TotalMilliseconds);
                    timer.Fire();
                }
                var outcome = await original;
                AssertOutcome(outcome, row.GetProperty("success").GetBoolean() ? null : NativeChatFailureCode.ProviderError);
                Equal(retries + 1, fixture.Requests.Count); Equal(retries, fixture.Retries.Count); Equal(1, fixture.PayloadCalls);
                Equal(row.GetProperty("success").GetBoolean() ? 1 : 0, fixture.Starts);
                for (var i = 0; i < fixture.Requests.Count; i++)
                {
                    fixture.AssertReleased(i);
                    Equal(0.75, fixture.Bodies[i].Value.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
                    Equal("https://google.invalid/v1beta/models/gemini-3-flash-preview:streamGenerateContent?alt=sse", fixture.Requests[i].RequestUri!.AbsoluteUri);
                    Equal(PrivateText, fixture.Requests[i].Headers.GetValues("x-goog-api-key").Single());
                    Equal("owned", fixture.Requests[i].Headers.GetValues("x-model").Single());
                    Equal("snapshot", fixture.Requests[i].Headers.GetValues("x-owned").Single());
                    Check(!fixture.Requests[i].Headers.Contains("accept"), "Retry restored removed header.");
                    Equal("application/json", fixture.RequestContents[i].Headers.GetValues("content-type").Single());
                    if (i > 0)
                    {
                        Check(!ReferenceEquals(fixture.Requests[0], fixture.Requests[i]) &&
                            !ReferenceEquals(fixture.RequestContents[0], fixture.RequestContents[i]), "Retry reused disposed request/content.");
                        Check(JsonElement.DeepEquals(fixture.Bodies[0].Value, fixture.Bodies[i].Value), "Prepared payload drifted between attempts.");
                        Equal(fixture.RequestBodies[0], fixture.RequestBodies[i]);
                        Equal(fixture.HeaderSets[0], fixture.HeaderSets[i]);
                    }
                }
                Check(!fixture.Handler.Disposed, "Retry disposed borrowed HTTP client.");
            }
            finally { fixture.Cancel(); clock.ReleaseAll(); await Join(original); }
        }
    }
    private static async Task Delays()
    {
        foreach (var row in Vectors().Value.GetProperty("delayVectors").EnumerateArray())
        {
            var clock = new Clock(); var cap = row.TryGetProperty("cap", out var capValue) ? capValue.GetInt32() : 60_000;
            using var fixture = new Fixture([429, 200], Options(clock) with { MaxRetryDelayMilliseconds = cap });
            if (row.TryGetProperty("milliseconds", out var ms)) fixture.Headers["retry-after-ms"] = ms.GetString()!;
            if (row.TryGetProperty("secondsOrDate", out var after)) fixture.Headers["retry-after"] = after.GetString()!;
            var original = Consume(fixture, true);
            try
            {
                if (row.TryGetProperty("expectedDelay", out var expectedDelay))
                {
                    var milliseconds = expectedDelay.GetDouble();
                    if (milliseconds > 0) { var timer = await clock.Next(); fixture.AssertReleased(0); timer.Fire(); }
                    var result = await original; AssertOutcome(result, null);
                    Equal(milliseconds, fixture.Retries.Single().Delay.TotalMilliseconds); Equal(2, fixture.Requests.Count);
                }
                else
                {
                    var result = await original;
                    AssertOutcome(result, Enum.Parse<NativeChatFailureCode>(row.GetProperty("failure").GetString()!));
                    if (row.GetProperty("id").GetString() == "default-cap-over")
                        Equal("Server requested 61s retry delay (max: 60s). " + RejectedMessage,
                            result.Terminal.Message.ExtraProperties!.Values["errorMessage"].Value.GetString());
                    Equal(1, fixture.Requests.Count); Equal(0, fixture.Retries.Count); Equal(0, clock.Created);
                }
                fixture.AssertReleased(0); Check(!fixture.Handler.Disposed, "Delay policy owned borrowed client.");
            }
            finally { fixture.Cancel(); clock.ReleaseAll(); await Join(original); }
        }
        foreach (var sample in new[] { -1d, 1d, double.NaN })
        {
            var clock = new Clock(); using var fixture = new Fixture([429, 200], Options(clock) with { RetryJitterSample = () => sample });
            AssertOutcome(await Consume(fixture, true), NativeChatFailureCode.UnsupportedFeature);
            Equal(1, fixture.Requests.Count); Equal(0, fixture.Retries.Count); fixture.AssertReleased(0);
        }
        var headerClock = new Clock(); using var longHeader = new Fixture([429, 200], Options(headerClock) with { MaximumHeaderCharacters = 128 });
        longHeader.Headers["retry-after-ms"] = new string('1', 129);
        AssertOutcome(await Consume(longHeader, true), NativeChatFailureCode.ResourceLimit); longHeader.AssertReleased(0);
        foreach (var count in new[] { -1, 33 })
        {
            using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Effects before validation.")));
            try { _ = new GoogleGenerativeAIHttpTransport(client, Model, Options(new Clock(), count)); throw new InvalidOperationException("Invalid retries admitted."); }
            catch (ArgumentOutOfRangeException) { }
        }
    }
    private static async Task CancellationAndCleanup()
    {
        foreach (var cancel in new[] { false, true })
        foreach (var failCleanup in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource(); var clock = new Clock();
            using var fixture = new Fixture([429, 200], Options(clock));
            fixture.FirstBody.HoldCleanup = true; fixture.FirstBody.FailCleanup = failCleanup;
            var original = Consume(fixture, true, cancellation.Token);
            try
            {
                await fixture.FirstBody.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && fixture.Starts == 0 && fixture.Retries.Count == 0 && clock.Created == 0 &&
                    fixture.Requests.Count == 1, "Rejected cleanup admitted backoff or next request.");
                if (cancel) cancellation.Cancel();
                Check(!original.IsCompleted, "Abort detached original rejected cleanup.");
                fixture.FirstBody.ReleaseCleanup.TrySetResult();
                if (!cancel && !failCleanup) { var timer = await clock.Next(); fixture.AssertReleased(0); timer.Fire(); }
                var result = await original;
                AssertOutcome(result, cancel ? NativeChatFailureCode.Cancelled : failCleanup ? NativeChatFailureCode.ProviderError : null, failCleanup);
                Equal(cancel || failCleanup ? 1 : 2, fixture.Requests.Count); fixture.AssertReleased(0);
                if (failCleanup && !cancel)
                    Equal(RejectedMessage, result.Terminal.Message.ExtraProperties!.Values["errorMessage"].Value.GetString());
            }
            finally { fixture.Cancel(); fixture.FirstBody.ReleaseCleanup.TrySetResult(); clock.ReleaseAll(); await Join(original); }
        }
        foreach (var seam in new[] { "send", "acquire", "read", "backoff", "retry-hook" })
        {
            using var cancellation = new CancellationTokenSource(); var clock = new Clock(); var entered = Gate(); var release = Gate();
            var options = Options(clock);
            if (seam == "acquire") options = options with { BodyReaderFactory = async (response, _) => {
                entered.TrySetResult(); await release.Task; return await response.Content.ReadAsStreamAsync();
            }};
            if (seam == "retry-hook") options = options with { Hooks = new() { OnRetry = _ => cancellation.Cancel() } };
            using var fixture = new Fixture([429, 200], options);
            if (seam == "send") { fixture.SendEntered = entered; fixture.ReleaseSend = release; }
            if (seam == "read") { fixture.FirstBody.ReadEntered = entered; fixture.FirstBody.ReleaseRead = release; }
            var original = Consume(fixture, true, cancellation.Token);
            try
            {
                if (seam == "backoff")
                {
                    var timer = await clock.Next(); fixture.AssertReleased(0); cancellation.Cancel();
                    var result = await original; AssertOutcome(result, NativeChatFailureCode.Cancelled);
                    Check(timer.Disposed, "Canceled retry timer remained owned."); timer.Fire();
                }
                else if (seam == "retry-hook") AssertOutcome(await original, NativeChatFailureCode.Cancelled);
                else
                {
                    await entered.Task.WaitAsync(Deadline); cancellation.Cancel();
                    Check(!original.IsCompleted, "Cancellation detached admitted original operation.");
                    release.TrySetResult(); AssertOutcome(await original, NativeChatFailureCode.Cancelled);
                }
                Equal(1, fixture.Requests.Count); Check(fixture.ResponseContents[0].Disposed &&
                    fixture.RequestContents[0].Disposed && !fixture.Handler.Disposed, "Canceled original attempt ownership survived.");
            }
            finally { fixture.Cancel(); release.TrySetResult(); clock.ReleaseAll(); await Join(original); }
        }
        using var preaborted = new CancellationTokenSource(); preaborted.Cancel();
        using var unused = new Fixture([429, 200], Options(new Clock()));
        AssertOutcome(await Consume(unused, true, preaborted.Token), NativeChatFailureCode.Cancelled);
        Equal(0, unused.Requests.Count); Equal(0, unused.PayloadCalls);
    }
    private static async Task NoStreamReplay()
    {
        foreach (var scenario in new[] { "payload", "send", "response", "acquire", "provider-hook", "read", "malformed", "provider-finish", "retry-hook" })
        {
            var clock = new Clock(); var options = Options(clock, 2);
            if (scenario == "payload") options = options with { Hooks = new() { OnPayload = (_, _, _) => throw new IOException(PrivateText) } };
            if (scenario == "response") options = options with { Hooks = new() { OnNativeResponse = (_, _, _) => throw new IOException(PrivateText) } };
            if (scenario == "acquire") options = options with { BodyReaderFactory = (_, _) => throw new IOException(PrivateText) };
            if (scenario == "provider-hook") options = options with { Hooks = new() { OnProviderStreamEvent = (_, _, _) => throw new IOException(PrivateText) } };
            if (scenario == "retry-hook") options = options with { Hooks = new() { OnRetry = _ => throw new IOException(PrivateText) } };
            using var fixture = new Fixture(scenario is "response" or "acquire" or "retry-hook" ? [429, 200] : [200], options);
            if (scenario == "send") fixture.FailSend = true;
            if (scenario == "read") fixture.FirstBody.FailReadAfterData = true;
            if (scenario == "malformed") fixture.FirstBody.SetWire("data: {bad}\n\n");
            if (scenario == "provider-finish") fixture.FirstBody.SetWire(Wire(Tool.Replace("\"STOP\"", "\"SAFETY\"", StringComparison.Ordinal)));
            var result = await Consume(fixture, true);
            AssertOutcome(result, scenario == "malformed" ? NativeChatFailureCode.MalformedStream :
                scenario == "provider-finish" ? NativeChatFailureCode.ProviderError : NativeChatFailureCode.SourceFailed);
            Equal(scenario == "payload" ? 0 : 1, fixture.Requests.Count); Equal(scenario == "retry-hook" ? 1 : 0, fixture.Retries.Count);
            Equal(0, clock.Created); Check(!fixture.Handler.Disposed, "Fault path disposed borrowed client.");
        }
    }
    private static async Task AgentAuthority()
    {
        foreach (var cancel in new[] { false, true })
        foreach (var failCleanup in new[] { false, true })
        {
            var clock = new Clock();
            using var fixture = new Fixture([429, 200], Options(clock)); fixture.SuccessWire = Wire(Tool);
            fixture.FirstBody.HoldCleanup = true; fixture.FirstBody.FailCleanup = failCleanup;
            var executor = new Executor(); StreamTerminalEvent? terminal = null;
            await using var agent = new NativeAgent(new(Model, fixture.Transport, [new("inspect", executor)],
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
                new Sink((value, _) => { if (value is TurnStreamObserved { Event: StreamTerminalEvent end }) terminal = end; return ValueTask.CompletedTask; }));
            Task<AgentLoopResult>? original = null;
            try
            {
                original = agent.PromptAsync(Inputs()); await fixture.FirstBody.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && terminal is null && executor.Count == 0 && fixture.Requests.Count == 1,
                    "Agent gained authority during rejected attempt cleanup.");
                if (cancel) agent.Abort();
                fixture.FirstBody.ReleaseCleanup.TrySetResult();
                if (!cancel && !failCleanup)
                {
                    var timer = await clock.Next(); Check(!original.IsCompleted && executor.Count == 0 && terminal is null, "Agent gained authority during backoff.");
                    fixture.AssertReleased(0); timer.Fire();
                }
                var chat = (await original).Turns.Single().Result.Chat;
                Equal(cancel || failCleanup ? 0 : 1, executor.Count); Equal(cancel || failCleanup ? 1 : 2, fixture.Requests.Count);
                var expected = cancel ? Diagnostic(NativeChatFailureCode.Cancelled) :
                    failCleanup ? Diagnostic(NativeChatFailureCode.ProviderError) : null;
                Equal(expected, chat.NativeDiagnostic); Equal(expected, terminal!.NativeDiagnostic); Equal(expected, chat.Failure?.NativeDiagnostic);
                Equal(failCleanup ? Diagnostic(NativeChatFailureCode.CleanupFailed) : null, chat.NativeCleanupDiagnostic);
                fixture.AssertReleased(0); Check(!agent.Snapshot.IsRunning && !fixture.Handler.Disposed, "Agent retry ownership did not settle.");
            }
            finally { agent.Abort(); fixture.FirstBody.ReleaseCleanup.TrySetResult(); clock.ReleaseAll(); if (original is not null) await Join(original); await agent.WaitForIdleAsync(); }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler; private readonly HttpClient _client;
        internal readonly GoogleGenerativeAIHttpTransport Transport;
        internal readonly List<HttpRequestMessage> Requests = []; internal readonly List<RequestContent> RequestContents = [];
        internal readonly List<BodyContent> ResponseContents = []; internal readonly List<JsonData> Bodies = [];
        internal readonly List<string> RequestBodies = [], HeaderSets = []; internal readonly List<OwnedBody> BodyOwners = [];
        internal readonly List<GoogleRetryObservation> Retries = []; internal readonly Dictionary<string, string> Headers = [];
        internal readonly OwnedBody FirstBody = new("authored rejected body");
        internal readonly CancellationTokenSource Cancellation = new();
        internal int PayloadCalls, Starts; internal bool FailSend;
        internal TaskCompletionSource? SendEntered, ReleaseSend; internal string SuccessWire = Wire(Success);
        internal Fixture(int[] statuses, GoogleGenerativeAIOptions options)
        {
            if (statuses[0] < 300) FirstBody.SetWire(SuccessWire);
            var hooks = options.Hooks;
            options = options with { Hooks = hooks with {
                OnPayload = async (payload, model, token) => { PayloadCalls++; return hooks.OnPayload is { } action ? await action(payload, model, token) : null; },
                OnRetry = observation => { Retries.Add(observation); hooks.OnRetry?.Invoke(observation); }
            }};
            Handler = new(async (request, token) => {
                var index = Requests.Count; Requests.Add(request);
                var preparedBody = await request.Content!.ReadAsStringAsync(); RequestBodies.Add(preparedBody); Bodies.Add(JsonData.Parse(preparedBody));
                HeaderSets.Add(string.Join('\n', request.Headers.Concat(request.Content.Headers)
                    .Select(x => x.Key.ToLowerInvariant() + ":" + string.Join(", ", x.Value)).Order(StringComparer.Ordinal)));
                var owner = new RequestContent(request.Content!); request.Content = owner; RequestContents.Add(owner);
                if (FailSend) throw new IOException(PrivateText);
                var status = statuses[Math.Min(index, statuses.Length - 1)];
                var body = index == 0 ? FirstBody : new OwnedBody(status < 300 ? SuccessWire : "authored rejected body");
                BodyOwners.Add(body);
                var content = new BodyContent(body); ResponseContents.Add(content);
                var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
                if (status >= 300) foreach (var header in Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (index == 0 && SendEntered is { } entered) { entered.TrySetResult(); await ReleaseSend!.Task; }
                return response;
            });
            _client = new(Handler); Transport = new(_client, Model, options);
        }
        internal void AssertReleased(int index)
        { Check(RequestContents[index].Disposed && ResponseContents[index].Disposed && BodyOwners[index].CleanupSettled &&
            BodyOwners[index].AsyncDisposals == 1, "Original body/request/response not settled once before retry."); }
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
        private readonly HttpContent _original;
        internal bool Disposed;
        internal RequestContent(HttpContent original)
        { _original = original; foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _original.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; _original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        internal bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override void Dispose(bool disposing) { Disposed = true; body.Dispose(); base.Dispose(disposing); }
    }
    private sealed class OwnedBody(string wire) : Stream
    {
        private byte[] _bytes = Encoding.UTF8.GetBytes(wire); private int _offset; private bool _held;
        internal bool HoldCleanup, FailCleanup, FailReadAfterData;
        internal int AsyncDisposals; internal bool CleanupSettled;
        internal TaskCompletionSource? ReadEntered, ReleaseRead;
        internal readonly TaskCompletionSource CleanupEntered = Gate(), ReleaseCleanup = Gate();
        internal void SetWire(string value) { if (_offset != 0) throw new InvalidOperationException("Changed active body."); _bytes = Encoding.UTF8.GetBytes(value); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_held && ReadEntered is { } entered) { _held = true; entered.TrySetResult(); await ReleaseRead!.Task; }
            if (FailReadAfterData && _offset == _bytes.Length) throw new IOException(PrivateText);
            var count = Math.Min(buffer.Length, _bytes.Length - _offset); _bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override async ValueTask DisposeAsync()
        {
            AsyncDisposals++; CleanupEntered.TrySetResult();
            try { if (HoldCleanup) await ReleaseCleanup.Task; if (FailCleanup) throw new IOException(PrivateText); }
            finally { CleanupSettled = true; }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Clock : TimeProvider
    {
        private readonly Channel<ManualTimer> _pending = Channel.CreateUnbounded<ManualTimer>();
        private readonly List<ManualTimer> _all = []; internal int Created;
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state); _all.Add(timer); Created++; _pending.Writer.TryWrite(timer); return timer; }
        internal async Task<ManualTimer> Next() => await _pending.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        internal void ReleaseAll() { foreach (var timer in _all.ToArray()) timer.Fire(); }
        internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed; internal bool Disposed => Volatile.Read(ref _disposed) != 0;
            internal void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
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
