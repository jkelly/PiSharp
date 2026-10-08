using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

// Authored controls derived from pinned Source and R41 failure analysis.
// They are additional coverage, not captured Source cases or an executed result.
internal sealed record SimpleRepairRegressionCase(string Id, bool Passed, string? Error,
    IReadOnlyList<object> Observations, bool HasUnjoinedOwner);
internal sealed record SimpleRepairRegressionReport(IReadOnlyList<SimpleRepairRegressionCase> Cases,
    int Failed, bool StoppedForUnjoinedOwner, bool StoppedForOwnershipDeadline = false,
    IReadOnlyList<string>? UnexecutedCaseIds = null, bool StoppedForProgressJournalFailure = false);

internal static class SimpleRepairRegressionCases
{
    internal static Uri EndpointFor(JsonData metadata) => new(
        (metadata.Value.GetProperty("baseUrl").GetString() ?? throw new JsonException("Missing baseUrl.")).TrimEnd('/') +
        "/chat/completions", UriKind.Absolute);
    private const string SuccessWire = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    private static readonly string[] CoreMessageFields = ["role", "content", "api", "provider", "model", "usage", "stopReason", "timestamp"];

    internal static async Task<SimpleRepairRegressionReport> RunAsync(JsonData metadata, Func<SimpleRepairProgress, Task> persist,
        bool skipAdmission = false, bool priorOwnershipDeadline = false, bool priorJournalFailure = false)
    {
        var model = new ModelDescriptor(metadata.Value.GetProperty("id").GetString() ?? throw new JsonException("Missing id."),
            "openai-completions", metadata.Value.GetProperty("provider").GetString() ?? throw new JsonException("Missing provider."));
        var request = new ChatRequest(model, [], 123);
        var tests = new (string Id, Func<CancellationTokenSource, SimpleRepairOwnership, Task> Test)[]
        {
            ("source-order-and-late-property-assignment", (deadline, evidence) => SourceOrder(metadata, request, deadline, evidence)),
            ("cf-auth-and-options-header-precedence", (deadline, evidence) => HeaderPrecedence(metadata, request, deadline, evidence)),
            ("forbidden-framing-headers-retained", (deadline, evidence) => ForbiddenHeaders(metadata, model, deadline, evidence)),
            ("bounded-opaque-options-and-explicit-cache-priority", (deadline, evidence) => OpaqueOptions(metadata, request, deadline, evidence)),
            ("http-timeout-cancels-original-send", (deadline, evidence) => SendTimeout(metadata, request, deadline, evidence)),
            ("http-timeout-does-not-time-sse-body", (deadline, evidence) => BodyAfterHeaders(metadata, request, deadline, evidence)),
            ("provider-failure-has-joined-successful-cleanup", (deadline, evidence) => CleanupFault(metadata, request, true, false, false, deadline, evidence)),
            ("provider-failure-retains-physical-release-failure", (deadline, evidence) => CleanupFault(metadata, request, true, false, true, deadline, evidence)),
            ("exact-done-joins-nonfatal-reader-cancel-failure", (deadline, evidence) => CleanupFault(metadata, request, false, true, false, deadline, evidence)),
            ("exact-done-joins-nonfatal-reader-release-failure", (deadline, evidence) => CleanupFault(metadata, request, false, false, true, deadline, evidence))
        };
        var allTests = tests.Concat(SimpleRepairOwnershipCases.Cases(metadata, request, persist)).ToArray();
        if (skipAdmission) return new([], 0, false, priorOwnershipDeadline, allTests.Select(test => test.Id).ToArray(), priorJournalFailure);
        return await RunBatchAsync(allTests, persist);
    }

    internal static async Task<SimpleRepairRegressionReport> RunBatchAsync(
        IEnumerable<(string Id, Func<CancellationTokenSource, SimpleRepairOwnership, Task> Test)> source,
        Func<SimpleRepairProgress, Task> persist, TimeSpan? diagnosticDeadline = null)
    {
        var tests = source.ToArray();
        var rows = new List<SimpleRepairRegressionCase>();
        var stoppedForDeadline = false;
        var stoppedForJournalFailure = false;
        string[] unexecuted = [];
        for (var index = 0; index < tests.Length; index++)
        {
            var (id, test) = tests[index];
            var evidence = new SimpleRepairOwnership(id, persist, diagnosticDeadline);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await evidence.ReportAsync("case-started", "case", "RUNNING; NOT A PASS", incomplete: true);
            Exception? failure = null;
            Task? execution = null;
            try
            {
                if (evidence.JournalFailure is { } journalFailure) throw journalFailure;
                execution = test(deadline, evidence);
                await execution.WaitAsync(deadline.Token);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                // An iterator can itself be waiting for physical cleanup, before its
                // surrounding disposal finally is reachable. Own the whole original
                // case task as well as the start/disposal tasks it admits.
                if (execution is not null)
                {
                    if (!execution.IsCompleted)
                    { var cancellationFailure = evidence.CancelOwner(deadline, "original-case-execution"); failure ??= cancellationFailure; }
                    var actualFailure = await evidence.JoinAsync(execution, "original-case-execution");
                    failure ??= actualFailure;
                }
            }
            if (evidence.StopLaterCases)
                failure ??= evidence.JournalFailure ?? new TimeoutException("Original task joined after diagnostic deadline; case remains nonpassing.");
            await evidence.ReportAsync("case-settled", "case", failure is null ? "PASSED" : "FAILED", incomplete: false, failure?.ToString());
            failure ??= evidence.JournalFailure;
            rows.Add(new(id, failure is null, failure?.ToString(), evidence.Snapshot(), false));
            if (evidence.StopLaterCases)
            {
                stoppedForDeadline = evidence.DiagnosticDeadlineExceeded;
                stoppedForJournalFailure = evidence.JournalFailure is not null;
                unexecuted = tests.Skip(index + 1).Select(item => item.Id).ToArray(); break;
            }
        }
        return new(rows, rows.Count(row => !row.Passed), false, stoppedForDeadline, unexecuted, stoppedForJournalFailure);
    }

    private static CompletionsSimpleOptions Options(JsonData metadata) => new(metadata)
    {
        ApiKey = "authored-inert-repair-key",
        DirectOptions = new(CacheRetention: CompletionsCacheRetention.None),
        HttpOptions = new() { Retry = new(0) }
    };

    private static async Task SourceOrder(JsonData metadata, ChatRequest request, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        // responseId is assigned undefined first, finish_reason arrives before responseModel,
        // and a later id updates its original insertion position. Alphabetic or fixed-tail
        // sorting would fail this real producer schedule.
        var wire = "data: {\"choices\":[{\"delta\":{\"content\":\"A\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"id\":\"late-id\",\"model\":\"late-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"completion_tokens_details\":{\"reasoning_tokens\":1}}}\n\n" +
            "data: [DONE]\n\n";
        using var handler = new OfflineHandler((_, _) => Task.FromResult(Response(wire)));
        using var client = new HttpClient(handler);
        var observed = await Execute(new(client, EndpointFor(metadata), request.Model, Options(metadata)), request, deadline, evidence);
        Require(observed.Cleanup.Succeeded && observed.Canonical.Failure is null, "successful producer joins");
        Equal(Fields(observed.Emissions[0].Raw.Value.GetProperty("value").GetProperty("partial")), CoreMessageFields, "initial Source order");
        Equal(Fields(observed.Result.Raw.Value.GetProperty("value")), [.. CoreMessageFields, "responseId", "rawStopReason", "responseModel"], "late Source insertion order");
        Equal(Fields(observed.Result.Raw.Value.GetProperty("value").GetProperty("usage")),
            ["input", "output", "cacheRead", "cacheWrite", "reasoning", "totalTokens", "cost"], "usage Source order");
        Require(observed.Emissions.Any(item => item.OwnUndefinedPaths.Contains("/partial/responseId")), "real own-undefined response id");
        Require(observed.Result.OwnUndefinedPaths.IsEmpty, "late id replaces undefined presence");
        Require(observed.Result.Raw.Value.GetProperty("value").GetProperty("responseId").GetString() == "late-id", "late id value");
    }

    private static async Task HeaderPrecedence(JsonData metadata, ChatRequest request, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        var combinations = new[]
        {
            (Key: (string?)null, Headers: "{\"cf-aig-authorization\":\"Bearer authored-inert-cf\"}", Authorization: "Bearer unused", Cf: (string?)"Bearer authored-inert-cf"),
            (Key: (string?)"authored-inert-explicit", Headers: "{\"cf-aig-authorization\":\"Bearer authored-inert-cf\"}", Authorization: "Bearer authored-inert-explicit", Cf: (string?)"Bearer authored-inert-cf"),
            (Key: (string?)"authored-inert-explicit", Headers: "{\"Authorization\":\"Bearer authored-inert-override\",\"cf-aig-authorization\":null}", Authorization: "Bearer authored-inert-override", Cf: (string?)null)
        };
        foreach (var item in combinations)
        {
            var actualAuthorization = ""; string? actualCf = null; var sends = 0;
            using var handler = new OfflineHandler((message, _) =>
            {
                sends++; actualAuthorization = Header(message, "authorization");
                actualCf = message.Headers.TryGetValues("cf-aig-authorization", out var values) ? values.Single() : null;
                return Task.FromResult(Response(SuccessWire));
            });
            using var client = new HttpClient(handler);
            var options = Options(metadata) with { ApiKey = item.Key,
                DirectOptions = new(CacheRetention: CompletionsCacheRetention.None, Headers: JsonData.Parse(item.Headers)) };
            var observed = await Execute(new(client, EndpointFor(metadata), request.Model, options), request, deadline, evidence);
            Require(sends == 1 && observed.Cleanup.Succeeded, "CF admission reaches one joined send");
            Require(actualAuthorization == item.Authorization && actualCf == item.Cf, "explicit/header/CF precedence");
            evidence.Add(new { sends, actualAuthorization, actualCf, authoredInertHeadersOnly = true });
        }
        using var emptyHandler = new OfflineHandler((_, _) => throw new Exception("Missing-key admission sent a request."));
        using var emptyClient = new HttpClient(emptyHandler);
        var emptyFactory = new CompletionsSimpleRequestFactory(emptyClient, EndpointFor(metadata), request.Model, Options(metadata) with
        { ApiKey = null, DirectOptions = new(Headers: JsonData.Parse("{\"cf-aig-authorization\":\"  \"}")) });
        try { _ = emptyFactory.StartAsync(request, deadline.Token); throw new Exception("Empty CF header admitted."); }
        catch (CompletionsRequestException error) when (error.Failure == CompletionsRequestFailure.InvalidKey)
        { evidence.Add(new { synchronousEmptyCfRejection = true }); }
    }

    private static Task ForbiddenHeaders(JsonData metadata, ModelDescriptor model, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        foreach (var name in new[] { "host", "content-length", "transfer-encoding", "connection" })
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                _ = new CompletionsKeyAuthRequestFactory(EndpointFor(metadata), model, options: new()
                { ModelMetadata = metadata, Headers = JsonData.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "authored-inert" })) });
                throw new Exception("Forbidden framing header admitted.");
            }
            catch (CompletionsRequestException error) when (error.Failure == CompletionsRequestFailure.UnsupportedContent)
            { evidence.Add(new { rejectedHeader = name }); }
        }
        return Task.CompletedTask;
    }

    private static async Task OpaqueOptions(JsonData metadata, ChatRequest request, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        string body = "";
        using var handler = new OfflineHandler(async (message, token) =>
        { body = await message.Content!.ReadAsStringAsync(token); return Response(SuccessWire); });
        using var client = new HttpClient(handler);
        var options = Options(metadata) with
        {
            TelemetryContext = JsonData.Parse("{\"authored\":true}"), Metadata = JsonData.Parse("{\"retained\":null}"),
            TransportPreference = "websocket", WebsocketConnectTimeoutMilliseconds = 4321,
            Environment = JsonData.Parse("{\"PI_CACHE_RETENTION\":\"long\",\"HTTP_PROXY\":\"\",\"HTTPS_PROXY\":\"\"}")
        };
        var observed = await Execute(new(client, EndpointFor(metadata), request.Model, options), request, deadline, evidence);
        Require(observed.Cleanup.Succeeded, "opaque options admitted and joined");
        using var parsed = JsonDocument.Parse(body);
        foreach (var name in new[] { "telemetryContext", "metadata", "transport", "websocketConnectTimeoutMs", "env", "prompt_cache_key", "prompt_cache_retention" })
            Require(!parsed.RootElement.TryGetProperty(name, out _), "inert option or explicit no-cache priority");
        try
        {
            _ = new CompletionsSimpleRequestFactory(client, EndpointFor(metadata), request.Model, options with
            { TelemetryContext = JsonData.Parse("{\"oversized\":\"" + new string('x', 1024) + "\"}"), DirectOptions = new(MaximumPayloadBytes: 64) });
            throw new Exception("Unbounded opaque option admitted.");
        }
        catch (CompletionsRequestException error) when (error.Failure == CompletionsRequestFailure.ResourceLimit)
        { evidence.Add(new { boundedOpaqueAdmission = true, borrowedClientUnmodified = true }); }
    }

    private static async Task SendTimeout(JsonData metadata, ChatRequest request, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        var entered = false; var settled = false; var canceled = false;
        using var handler = new OfflineHandler(async (_, token) =>
        {
            entered = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("Infinite send completed."); }
            finally { canceled = token.IsCancellationRequested; settled = true; }
        });
        using var client = new HttpClient(handler);
        var observed = await Execute(new(client, EndpointFor(metadata), request.Model, Options(metadata) with { TimeoutMilliseconds = 30 }), request, deadline, evidence);
        Require(entered && settled && canceled && !deadline.IsCancellationRequested, "configured timeout cancels and joins original send");
        Require(observed.Result.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "error" && observed.Cleanup.Succeeded,
            "request timeout is a provider failure with successful ownership cleanup");
        evidence.Add(new { entered, settled, canceled, callerCancellation = deadline.IsCancellationRequested });
    }

    private static async Task BodyAfterHeaders(JsonData metadata, ChatRequest request, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        using var body = new HeldBody(Encoding.UTF8.GetBytes(SuccessWire));
        using var handler = new OfflineHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(body) }));
        using var client = new HttpClient(handler);
        var task = Execute(new(client, EndpointFor(metadata), request.Model, Options(metadata) with { TimeoutMilliseconds = 30 }), request, deadline, evidence);
        try
        {
            await ReachAsync(body.Entered.Task, task, deadline.Token, "held SSE body read");
            await Task.Delay(90, deadline.Token);
            Require(!task.IsCompleted, "response-header timeout does not abort a later held SSE read");
            body.Release(); var observed = await task;
            Require(observed.Cleanup.Succeeded && observed.Canonical.Failure is null, "body succeeds after header deadline");
        }
        finally
        {
            body.Release();
            var cancellationFailure = !task.IsCompleted ? evidence.CancelOwner(deadline, "held-body-original-execution") : null;
            var failure = await evidence.JoinAsync(task, "held-body-original-execution");
            failure ??= cancellationFailure;
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task CleanupFault(JsonData metadata, ChatRequest request, bool callbackFailure, bool cancelFailure,
        bool releaseFailure, CancellationTokenSource deadline, SimpleRepairOwnership evidence)
    {
        FaultReader? reader = null;
        using var handler = new OfflineHandler((_, _) => Task.FromResult(Response(SuccessWire)));
        using var client = new HttpClient(handler);
        var options = Options(metadata) with { HttpOptions = new()
        {
            Retry = new(0),
            Hooks = new() { OnProviderStreamEventSnapshot = (_, _, _) => callbackFailure
                ? ValueTask.FromException(new CompletionsPublicFailureException(new("Authored repair callback failure"))) : ValueTask.CompletedTask },
            BodyReaderFactory = (body, _) =>
            {
                reader = new(CompletionsResponseBodyReader.FromStream(body), cancelFailure, releaseFailure);
                return ValueTask.FromResult<ICompletionsResponseBodyReader>(reader);
            }
        } };
        var observed = await Execute(new(client, EndpointFor(metadata), request.Model, options), request, deadline, evidence);
        var actualReader = reader ?? throw new Exception("Actual reader was not acquired.");
        Require(actualReader.Releases == 1, "actual reader lease released exactly once");
        var fatalReaderCleanup = callbackFailure && (cancelFailure || releaseFailure);
        Require(observed.Cleanup.Succeeded == !fatalReaderCleanup, "exact DONE reader policy and pre-DONE failure separation");
        Require((observed.Canonical.Failure is not null) == callbackFailure, "canonical completion retains source failure and exact DONE success");
        if (callbackFailure)
            Require(observed.Result.Raw.Value.GetProperty("value").GetProperty("errorMessage").GetString() == "Authored repair callback failure", "original public callback failure retained");
        else
            Require(observed.Result.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                observed.Canonical.Message.StopReason == StopReason.Stop && observed.Canonical.NativeDiagnostic is null,
                "joined exact DONE reader faults retain source success without fabricated native diagnostics");
        if (fatalReaderCleanup)
            Require(observed.Cleanup.Failure?.NativeDiagnostic?.Code == NativeChatFailureCode.CleanupFailed, "physical failure has CleanupFailed diagnostic");
        evidence.Add(new { callbackFailure, cancelFailure, releaseFailure, actualReader.Cancels, actualReader.Releases,
            cleanupSucceeded = observed.Cleanup.Succeeded, originalOwnerActuallyJoined = true });
    }

    internal sealed record Observation(CompletionsSourceSnapshot Result, CompletionsCleanupOutcome Cleanup,
        ChatResult Canonical, IReadOnlyList<CompletionsSourceSnapshot> Emissions);

    internal static async Task ReachAsync(Task barrier, Task original, CancellationToken token, string boundary)
    {
        await Task.WhenAny(barrier, original).WaitAsync(token);
        if (!barrier.IsCompleted)
        {
            await original; // Preserve an original fault/cancellation rather than timing out at an unentered gate.
            throw new InvalidOperationException("Original operation completed before " + boundary + ".");
        }
        await barrier;
    }

    internal static async Task<Observation> Execute(CompletionsSimpleRequestFactory factory, ChatRequest request,
        CancellationTokenSource deadline, SimpleRepairOwnership evidence,
        Func<CompletionsSimpleRequestFactory, ChatRequest, CancellationToken, Task<CompletionsRun>>? startOverride = null)
    {
        var start = startOverride is null ? factory.StartAsync(request, deadline.Token).AsTask() : startOverride(factory, request, deadline.Token);
        CompletionsRun? run = null; Exception? failure = null;
        try
        {
            run = await start.WaitAsync(deadline.Token);
            await foreach (var item in run.ReadSourceEventsAsync(deadline.Token))
                evidence.Add(new { kind = "delivery", item.Type, emission = item.Emission.SerializedJson });
            var result = await run.SourceResult.WaitAsync(deadline.Token);
            var cleanup = await run.CleanupCompletion.WaitAsync(deadline.Token);
            var canonical = await run.CanonicalCompletion.WaitAsync(deadline.Token);
            var emissions = run.SourceEmissions;
            evidence.Add(new { kind = "actual-results", sourceJson = result.Snapshot.SerializedJson, cleanup,
                canonicalStopReason = canonical.Message.StopReason, canonical.Failure, source = run.SourceResult.Status.ToString(),
                cleanupStatus = run.CleanupCompletion.Status.ToString(), canonicalStatus = run.CanonicalCompletion.Status.ToString() });
            return new(result.Snapshot, cleanup, canonical, emissions);
        }
        catch (Exception error) { failure = error; _ = evidence.CancelOwner(deadline, "original-execution"); throw; }
        finally
        {
            if (run is null)
            {
                var startFailure = await evidence.JoinAsync(start, "original-start");
                if (start.IsCompletedSuccessfully) run = start.Result; // A late run still has an owner and is disposed below.
                else if (failure is null && startFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startFailure).Throw();
            }
            if (run is not null)
            {
                var disposal = run.DisposeAsync().AsTask();
                var disposalFailure = await evidence.JoinAsync(disposal, "original-disposal");
                // Observe every original semantic/cleanup/canonical task after disposal,
                // including late source faults and a cleanup outcome containing failure.
                _ = await evidence.JoinAsync(run.SourceResult, "original-source-result");
                _ = await evidence.JoinAsync(run.CleanupCompletion, "original-cleanup-completion");
                _ = await evidence.JoinAsync(run.CanonicalCompletion, "original-canonical-completion");
                evidence.Add(new { kind = "original-disposal-settled", start = start.Status.ToString(), disposal = disposal.Status.ToString() });
                if (run.CleanupCompletion.IsCompletedSuccessfully)
                    evidence.Add(new { kind = "joined-cleanup-outcome", outcome = run.CleanupCompletion.Result });
                if (run.CanonicalCompletion.IsCompletedSuccessfully)
                    evidence.Add(new { kind = "joined-canonical-outcome", outcome = run.CanonicalCompletion.Result });
                if (disposalFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalFailure).Throw();
            }
        }
    }

    private sealed class OfflineHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }

    private sealed class FaultReader(ICompletionsResponseBodyReader inner, bool cancelFailure, bool releaseFailure) : ICompletionsResponseBodyReader
    {
        public int Cancels { get; private set; }
        public int Releases { get; private set; }
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) => inner.ReadAsync(destination, cancellationToken);
        public async ValueTask CancelAsync()
        { Cancels++; await inner.CancelAsync(); if (cancelFailure) throw new IOException("Authored physical cancellation failure."); }
        public void Release()
        { Releases++; inner.Release(); if (releaseFailure) throw new IOException("Authored physical release failure."); }
    }

    private sealed class HeldBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first;
        public void Release() => _released.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _first, 1) == 0) { Entered.TrySetResult(); await _released.Task.WaitAsync(cancellationToken); }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static HttpResponseMessage Response(string wire) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(wire)) };
    private static string Header(HttpRequestMessage message, string name) => message.Headers.TryGetValues(name, out var values) ? values.Single() : "";
    private static string[] Fields(JsonElement value) => value.EnumerateObject().Select(field => field.Name).ToArray();
    private static void Equal(string[] actual, string[] expected, string criterion) => Require(actual.SequenceEqual(expected), criterion);
    private static void Require(bool condition, string criterion) { if (!condition) throw new Exception(criterion); }
}
