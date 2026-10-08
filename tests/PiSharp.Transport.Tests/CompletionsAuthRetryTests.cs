using System.Collections.Immutable;
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsAuthRetryTests
{
    private static readonly ModelDescriptor Model = new("auth-retry", "openai-completions", "authored-offline");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Key = "authored-inert-key";
    private const string Success = "data: {\"choices\":[{\"delta\":{\"content\":\"retry-ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-auth-retry.caller-auth-and-generated-zero-precedence", Authorization);
        yield return ("completions-auth-retry.source-status-directive-attempt-and-callback-policy", StatusProfiles);
        yield return ("completions-auth-retry.connection-and-timeout-only-before-headers", Connections);
        yield return ("completions-auth-retry.source-delay-precedence-dates-prefix-cap-and-jitter", Delays);
        yield return ("completions-auth-retry.abortable-backoff-has-no-next-send", AbortBackoff);
        yield return ("completions-auth-retry.bounded-replay-and-once-only-materialization", ReplayBounds);
        yield return ("completions-auth-retry.rejected-ownership-before-retry-and-cleanup-precedence", Ownership);
        yield return ("completions-auth-retry.source-start-retries-and-post-headers-never-replay", SourceAndFailures);
    }

    private static Task Authorization()
    {
        foreach (var row in new[] {
            (Model: "{}", Caller: "{}", Auth: "Bearer " + Key, Retry: "0"),
            (Model: "{\"Authorization\":\"Bearer authored-model\"}", Caller: "{}", Auth: "Bearer authored-model", Retry: "0"),
            (Model: "{\"AUTHORIZATION\":\"Bearer authored-model\"}", Caller: "{\"authorization\":\"Bearer authored-caller\",\"X-Stainless-Retry-Count\":\"42\"}", Auth: "Bearer authored-caller", Retry: "42"),
            (Model: "{\"Authorization\":\"Bearer authored-model\"}", Caller: "{\"Authorization\":null,\"X-Stainless-Retry-Count\":null}", Auth: (string?)null, Retry: (string?)null) })
        {
            var options = new CompletionsKeyAuthRequestOptions(ModelHeaders: JsonData.Parse(row.Model), Headers: JsonData.Parse(row.Caller));
            using var request = Factory(options).Create(Request(), Key);
            Check(Header(request, "authorization") == row.Auth && Header(request, "x-stainless-retry-count") == row.Retry, "Actual auth/retry header precedence differs.");
            Throws<CompletionsRequestException>(() => Factory(options).Create(Request(), "private\r\nkey"));
        }
        foreach (var text in new[] { "{\"Authorization\":\"private\\r\\nvalue\"}", "{\"Authorization\":42}" })
            Throws<CompletionsRequestException>(() => Factory(new(Headers: JsonData.Parse(text))));
        Throws<CompletionsRequestException>(() => Factory(new(Headers: JsonData.Parse("{\"Authorization\":\"Bearer oversized-authored\"}"), MaximumHeaderCharacters: 12)));
        return Task.CompletedTask;
    }

    private static async Task StatusProfiles()
    {
        foreach (var row in new[] {
            (Statuses: new[] { 429 }, Retries: 0, Directive: (string?)null, Sends: 1, Success: false),
            (Statuses: new[] { 429, 500, 200 }, Retries: 2, Directive: (string?)null, Sends: 3, Success: true),
            (Statuses: new[] { 429, 500 }, Retries: 1, Directive: (string?)null, Sends: 2, Success: false),
            (Statuses: new[] { 429 }, Retries: 2, Directive: "false", Sends: 1, Success: false),
            (Statuses: new[] { 400, 200 }, Retries: 1, Directive: "true", Sends: 2, Success: true),
            (Statuses: new[] { 400, 200 }, Retries: 1, Directive: " true ", Sends: 2, Success: true),
            (Statuses: new[] { 400 }, Retries: 1, Directive: "TRUE", Sends: 1, Success: false),
            (Statuses: new[] { 408, 200 }, Retries: 1, Directive: (string?)null, Sends: 2, Success: true),
            (Statuses: new[] { 409, 200 }, Retries: 1, Directive: (string?)null, Sends: 2, Success: true),
            (Statuses: new[] { 404 }, Retries: 2, Directive: (string?)null, Sends: 1, Success: false) })
        foreach (var callerRetry in new[] { false, true })
        {
            using var fixture = new Fixture(row.Statuses, new(row.Retries), callerRetry ? "{\"X-Stainless-Retry-Count\":\"42\"}" : "{}");
            if (row.Directive is not null) fixture.ResponseHeaders["x-should-retry"] = row.Directive;
            var result = await fixture.Complete();
            Check(fixture.Handler.Sends == row.Sends && fixture.PayloadCalls == 1 && fixture.FactoryCalls == 1 &&
                fixture.ResponseCalls == (row.Success ? 1 : 0) && result.Message.StopReason == (row.Success ? StopReason.Stop : StopReason.Error),
                "Actual source HTTP retry/accepted callback policy changed.");
            Check(fixture.Decisions.Count == row.Sends - 1 && fixture.Decisions.Select(value => value.RetryIndex).SequenceEqual(Enumerable.Range(0, row.Sends - 1)), "Native retry provenance is missing or fabricated.");
            Check(fixture.Handler.Requests.All(request => Header(request, "x-stainless-retry-count") == (callerRetry ? "42" : "0")), "Native outer index replaced fresh SDK retry-count zero/caller override.");
            Check(fixture.Handler.Bodies.Distinct(StringComparer.Ordinal).Count() == 1 && fixture.Handler.Requests.Distinct(ReferenceEqualityComparer.Instance).Count() == row.Sends &&
                fixture.Handler.Requests.Select(request => request.Content).Distinct(ReferenceEqualityComparer.Instance).Count() == row.Sends, "Owned retry requests/content were reused or changed.");
            Closed(fixture); Clean(result);
        }
    }

    private static async Task Connections()
    {
        foreach (var timeout in new[] { false, true })
        {
            using var fixture = new Fixture([0, 200], new(1, 20));
            fixture.Handler.SendFailure = timeout ? new OperationCanceledException("PRIVATE_TIMEOUT") : new HttpRequestException("PRIVATE_CONNECTION");
            var result = await fixture.Complete();
            Check(result.Message.StopReason == StopReason.Stop && fixture.Handler.Sends == 2 && fixture.Decisions.Single().Status is null && fixture.FactoryCalls == 1,
                "Pre-header connection/timeout did not retry through a fresh owned request.");
            Check(fixture.Decisions[0].Delay.TotalMilliseconds is >= 375 and <= 500, "Source first exponential jitter bounds changed.");
            Closed(fixture); Clean(result);
        }
    }

    private static async Task Delays()
    {
        foreach (var row in new[] {
            (Ms: "1.5tail", After: "60", Expected: 1.5), (Ms: "invalid", After: "0.002tail", Expected: 2d),
            (Ms: "-12", After: (string?)null, Expected: 0d),
            (Ms: (string?)null, After: "Sat, 03 Oct 2026 00:00:01 GMT", Expected: 1000d),
            (Ms: (string?)null, After: "Fri, 02 Oct 2026 23:59:59 GMT", Expected: 0d),
            (Ms: "60001", After: (string?)null, Expected: 60001d),
            // Pi 0.99.2 (provider-retry.ts): infinite values and unparseable dates use exponential backoff (-1 marks the jitter range).
            (Ms: "-Infinity", After: (string?)null, Expected: -1d), (Ms: (string?)null, After: "invalid-date", Expected: -1d),
            (Ms: "Infinity", After: (string?)null, Expected: -1d), (Ms: "1e999", After: (string?)null, Expected: -1d),
            (Ms: (string?)null, After: "Infinity", Expected: -1d) })
        {
            var clock = new Clock();
            using var fixture = new Fixture([429, 200], new(1, row.Expected > 60000 ? 0 : 60000) { TimeProvider = clock });
            fixture.ResponseHeaders.Clear();
            if (row.Ms is not null) fixture.ResponseHeaders["retry-after-ms"] = row.Ms;
            if (row.After is not null) fixture.ResponseHeaders["retry-after"] = row.After;
            var task = fixture.Complete();
            if (row.Expected is > 0 or < 0)
            {
                await clock.Entered.Task.WaitAsync(Deadline); Check(fixture.Handler.Sends == 1 && fixture.Handler.Responses.Single().Disposals == 1, "Delay began before rejected ownership closed.");
                clock.Release();
            }
            var result = await task; var actual = fixture.Decisions.Single().Delay.TotalMilliseconds;
            Check(result.Message.StopReason == StopReason.Stop && (row.Expected < 0 ? actual is >= 375 and <= 500 : actual == row.Expected) && fixture.Handler.Sends == 2, "Pinned source delay precedence/parser/date behavior differs.");
            Closed(fixture);
        }
        foreach (var delay in new[] { "60001" })
        {
            using var fixture = new Fixture([429], new(2, 20)); fixture.ResponseHeaders["retry-after-ms"] = delay;
            var result = await fixture.Complete(); Check(result.Message.StopReason == StopReason.Error && fixture.Handler.Sends == 1 && fixture.Decisions.Count == 0, "Server delay cap permitted a next attempt."); Closed(fixture); Clean(result);
        }
        var jitterClock = new Clock();
        using var jitter = new Fixture([429, 200], new(1) { TimeProvider = jitterClock }); jitter.ResponseHeaders.Clear();
        var pending = jitter.Complete(); await jitterClock.Entered.Task.WaitAsync(Deadline);
        Check(jitter.Decisions.Single().Delay.TotalMilliseconds is >= 375 and <= 500, "Source exponential jitter was replaced with server cap/default delay.");
        jitterClock.Release(); await pending; Closed(jitter);
        var capClock = new Clock(); using var capped = new Fixture([429, 429, 429, 429, 429, 429, 200], new(6, 20) { TimeProvider = capClock });
        capped.ResponseHeaders.Clear(); var capTask = capped.Complete();
        for (var index = 0; index < 6; index++) await capClock.ReleaseNextAsync();
        await capTask;
        for (var index = 0; index < 6; index++)
        { var ceiling = Math.Min(500 * Math.Pow(2, index), 8000); Check(capped.Decisions[index].Delay.TotalMilliseconds >= ceiling * 0.75 && capped.Decisions[index].Delay.TotalMilliseconds <= ceiling,
            "Actual exponential backoff exceeded the pinned source jitter/eight-second cap or incorrectly used the server-delay cap."); }
        Closed(capped);
    }

    private static async Task AbortBackoff()
    {
        using var fixture = new Fixture([429], new(2)); fixture.ResponseHeaders["retry-after-ms"] = "1000";
        using var cancellation = new CancellationTokenSource();
        fixture.OnDecision = _ => cancellation.Cancel();
        var result = await fixture.Complete(cancellation.Token);
        Check(result.Message.StopReason == StopReason.Aborted && result.Failure?.Kind == ChatFailureKind.Cancelled && fixture.Handler.Sends == 1 && fixture.Decisions.Count == 1,
            "Abort during owned provider backoff sent again or lost abort classification.");
        Closed(fixture); Clean(result);
    }

    private static async Task ReplayBounds()
    {
        foreach (var row in new[] { new CompletionsRetryOptions(1) { MaximumRequestBodyBytes = 2 },
            new CompletionsRetryOptions(1) { MaximumRequestHeaders = 1 }, new CompletionsRetryOptions(1) { MaximumRequestHeaderCharacters = 3 },
            new CompletionsRetryOptions(1) { MaximumRequestHeaderCharacters = 256 },
            new CompletionsRetryOptions(1) { MaximumRequestOptions = 1 } })
        {
            using var fixture = new Fixture([], row); fixture.AddRequestOptions = true; fixture.AddEmptyHeaders = true;
            var result = await fixture.Complete(); Check(result.Message.StopReason == StopReason.Error && fixture.Handler.Sends == 0 && fixture.FactoryCalls == 1 && fixture.PayloadCalls == 1,
                "Replay admission bypassed its body/header/option bounds or retried a factory failure."); Clean(result);
            Throws<ObjectDisposedException>(() => fixture.CreatedRequest!.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }
        using var opaque = new Fixture([429, 200], new(1), "{\"X-Opaque\":\"owned\",\"X-Remove\":null}"); opaque.AddRequestOptions = true;
        opaque.PayloadReplacement = JsonData.Parse("{\"model\":\"auth-retry\",\"messages\":[],\"stream\":true,\"opaque\":{\"keep\":null}}");
        var successful = await opaque.Complete();
        Check(successful.Message.StopReason == StopReason.Stop && opaque.Handler.Requests.All(request => Header(request, "x-opaque") == "owned" && !request.Headers.Contains("x-remove") &&
            request.Version == HttpVersion.Version20 && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact && request.Options.TryGetValue(new HttpRequestOptionsKey<object>("opaque"), out var value) && ReferenceEquals(value, opaque.Opaque)),
            "Fresh request replay lost actual protocol/opaque options/unknown fields.");
        Check(opaque.PayloadCalls == 1 && opaque.FactoryCalls == 1 && opaque.Handler.Bodies.All(body => body == opaque.PayloadReplacement.ToString()),
            "An actual replacement payload was rebuilt, mutated or reinvoked across retries."); Closed(opaque);
        foreach (var invalid in new[] { new CompletionsRetryOptions(-1), new CompletionsRetryOptions(33), new CompletionsRetryOptions(1, -1),
            new CompletionsRetryOptions(1) { MaximumRequestBodyBytes = 0 }, new CompletionsRetryOptions(1) { TimeProvider = null! } })
        { using var handler = new Handler([]); using var client = new HttpClient(handler); Throws<ArgumentOutOfRangeException>(() => new CompletionsHttpSseTransport(client, (_, _) => new(), new() { Retry = invalid })); }
    }

    private static async Task Ownership()
    {
        foreach (var failedCleanup in new[] { false, true })
        {
            using var fixture = new Fixture(failedCleanup ? [429] : [429, 200], new(2)); fixture.Handler.FailRejectedDisposal = failedCleanup;
            fixture.OnDecision = _ => Check(fixture.Handler.Responses.All(response => response.Disposals == 1) && fixture.Handler.Requests.All(request => IsClosed(request.Content!)),
                "Retry decision escaped rejected request/response disposal.");
            var result = await fixture.Complete();
            Check(fixture.Handler.Sends == (failedCleanup ? 1 : 2) && fixture.Decisions.Count == (failedCleanup ? 0 : 1), "Cleanup failure invisibly authorized another request.");
            if (failedCleanup) Check(result.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed && result.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed,
                "Rejected physical cleanup failure was lost or replaced primary semantics.");
            Closed(fixture); Clean(result);
        }
    }

    private static async Task SourceAndFailures()
    {
        using (var fixture = new Fixture([429, 500, 200], new(2)))
        {
            await using var run = await fixture.Transport.StartAsync(Request());
            while (!(await run.NextAsync()).Done) { }
            var source = await run.SourceResult; var native = await run.CanonicalCompletion;
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" && native.Message.StopReason == StopReason.Stop &&
                fixture.Handler.Sends == 3 && fixture.FactoryCalls == 1 && fixture.PayloadCalls == 1 && fixture.ResponseCalls == 1, "Actual source startup did not honor preparation retry ownership."); Closed(fixture);
        }
        foreach (var stage in new[] { "factory", "payload", "response", "stream", "retry-observer" })
        {
            using var fixture = new Fixture(stage == "retry-observer" ? [429] : [200], new(2)); fixture.FailureStage = stage;
            var result = await fixture.Complete();
            Check(result.Message.StopReason == StopReason.Error && fixture.Handler.Sends == (stage is "factory" or "payload" ? 0 : 1), "A callback or post-header stream failure was replayed.");
            Clean(result); Closed(fixture);
        }
    }

    private static CompletionsKeyAuthRequestFactory Factory(CompletionsKeyAuthRequestOptions? options = null) => new(new("https://auth-retry.invalid/completions"), Model, options: options);
    private static ChatRequest Request() => new(Model, [], 123);
    private static string? Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
    private static bool IsClosed(HttpContent content) { try { content.ReadAsByteArrayAsync().GetAwaiter().GetResult(); return false; } catch (ObjectDisposedException) { return true; } }
    private static void Closed(Fixture fixture)
    { Check(fixture.Handler.Requests.All(request => IsClosed(request.Content!)) && fixture.Handler.Responses.All(response => response.Disposals == 1), "An HTTP attempt leaked or multiply disposed owned content."); }
    private static void Clean(ChatResult result)
    { var json = PiWireJson.WriteMessage(result.Message).ToString(); Check(!json.Contains("PRIVATE", StringComparison.Ordinal) && !json.Contains(Key, StringComparison.Ordinal) && !json.Contains("openAICompletionsFailure", StringComparison.Ordinal), "Retry/private request data entered a Pi message."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Fixture : IDisposable
    {
        public Handler Handler { get; } public HttpClient Client { get; } public CompletionsHttpSseTransport Transport { get; }
        public Dictionary<string, string> ResponseHeaders => Handler.Headers;
        public int FactoryCalls, PayloadCalls, ResponseCalls; public string? FailureStage; public bool AddRequestOptions, AddEmptyHeaders;
        public JsonData? PayloadReplacement;
        public object Opaque { get; } = new(); public HttpRequestMessage? CreatedRequest;
        public List<CompletionsRetryObservation> Decisions { get; } = []; public Action<CompletionsRetryObservation>? OnDecision;
        public Fixture(int[] statuses, CompletionsRetryOptions retry, string headers = "{}")
        {
            Handler = new(statuses); Client = new(Handler);
            var factory = Factory(new(Headers: JsonData.Parse(headers)));
            var hooks = new CompletionsLifecycleHooks {
                OnPayload = (_, _, _) => { PayloadCalls++; if (FailureStage == "payload") throw new HttpRequestException("PRIVATE_PAYLOAD"); return ValueTask.FromResult(PayloadReplacement); },
                OnResponse = (_, _, _) => { ResponseCalls++; if (FailureStage == "response") throw new HttpRequestException("PRIVATE_RESPONSE"); return ValueTask.CompletedTask; },
                OnProviderStreamEvent = (_, _, _) => { if (FailureStage == "stream") throw new HttpRequestException("PRIVATE_STREAM"); return ValueTask.CompletedTask; } };
            Transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(Client, async (request, token) => {
                FactoryCalls++; if (FailureStage == "factory") throw new HttpRequestException("PRIVATE_FACTORY");
                var owned = await factory.CreateAsync(request, Key, hooks, token); CreatedRequest = owned;
                if (AddEmptyHeaders) owned.Headers.TryAddWithoutValidation("X-Empty", Enumerable.Repeat("", 2000));
                if (AddRequestOptions) { owned.Version = HttpVersion.Version20; owned.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    owned.Options.Set(new HttpRequestOptionsKey<object>("opaque"), Opaque); owned.Options.Set(new HttpRequestOptionsKey<int>("second"), 2); }
                return owned;
            }, new() { Hooks = hooks, Retry = retry with { OnRetry = observation => { Decisions.Add(observation); if (FailureStage == "retry-observer") throw new HttpRequestException("PRIVATE_RETRY_OBSERVER"); OnDecision?.Invoke(observation); } } });
        }
        public async Task<ChatResult> Complete(CancellationToken token = default) => await new ChatClient(Transport).CompleteAsync(Request(), token).WaitAsync(Deadline);
        public void Dispose() { Client.Dispose(); Handler.Dispose(); }
    }

    private sealed class Handler(int[] statuses) : HttpMessageHandler
    {
        public int Sends; public Exception? SendFailure; public bool FailRejectedDisposal;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase) { ["retry-after-ms"] = "1" };
        public List<HttpRequestMessage> Requests { get; } = []; public List<string> Bodies { get; } = []; public List<Content> Responses { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var ordinal = Sends++; Requests.Add(request); Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (ordinal >= statuses.Length) throw new InvalidOperationException("Unexpected offline send.");
            if (statuses[ordinal] == 0) throw SendFailure ?? new HttpRequestException("PRIVATE_CONNECTION");
            var success = statuses[ordinal] == 200;
            var content = new Content(success ? Success : "{\"error\":{\"message\":\"PRIVATE_RESPONSE_BODY\"}}", !success && FailRejectedDisposal); Responses.Add(content);
            var response = new HttpResponseMessage((HttpStatusCode)statuses[ordinal]) { Content = content };
            foreach (var pair in Headers) response.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            return response;
        }
    }
    private sealed class Content(string body, bool fail) : StringContent(body, Encoding.UTF8)
    {
        public int Disposals;
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); if (disposing && fail) throw new IOException("PRIVATE_REJECTED_CLOSE"); }
    }
    private sealed class Clock : TimeProvider
    {
        private ManualTimer? _timer;
        private readonly global::System.Threading.Channels.Channel<ManualTimer> _pending = global::System.Threading.Channels.Channel.CreateUnbounded<ManualTimer>();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _timer = new(callback, state); _pending.Writer.TryWrite(_timer); Entered.TrySetResult(); return _timer; }
        public void Release() => _timer!.Fire();
        public async Task ReleaseNextAsync() => (await _pending.Reader.ReadAsync().AsTask().WaitAsync(Deadline)).Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
