using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsLifecycleDifferentialTests
{
    private const string Family = "fixtures/reference/openai-completions-sdk-lifecycle/";
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string InputHash = "2e35c47906fd8c52c0d156dc2af4621f746fdcdb33fb319fecc78abaa438bd2e";
    private const string ExpectedHash = "829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b";
    private const string ManifestHash = "43fa7a67d27c87fba5bfac58e10aca80b94fa774236bec9560ff3114a285ee09";
    private const string LockHash = "5790f389521796f80a2c0d0b8fb8be8ccf6efc13bd0a197527abd6d7cecb5211";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly string[] Ids =
    [
        "missing-empty-named-and-thread-events", "event-only-nonempty-name", "empty-data-event", "named-error-with-falsy-property",
        "exact-done-joins-cancel-and-ignores-tail", "trailing-space-done-is-not-exact", "eof-later-bom-replacement-utf8-every-byte",
        "falsy-error-false-cancel-rejects-after-done", "falsy-error-zero-release-fault-after-done",
        "falsy-error-negative-zero-consumer-returns", "falsy-error-empty-string", "abort-pending-read-cancel-gate-rejects",
        "reader-acquisition-fault", "on-response-fault-before-reader"
    ];
    private static readonly Lazy<Task<CaseObservation>>[] Captures = Ids.Select((_, index) =>
        new Lazy<Task<CaseObservation>>(() => CaptureCase(index), LazyThreadSafetyMode.ExecutionAndPublication)).ToArray();

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-lifecycle.native-owned-observation.all-14", CaptureAll);
    }

    public static IEnumerable<(string Name, Func<Task> Run)> StrictCases()
    {
        for (var index = 0; index < Ids.Length; index++)
        {
            var selected = index;
            yield return ("completions-lifecycle.genuine-sdk-differential." + Ids[selected], () => CompareCase(selected));
        }
    }

    private static async Task CompareCase(int index)
    {
        var observation = await Captures[index].Value;
        if (observation.Differences.Count != 0) throw new InvalidOperationException(Ids[index] + ": " + observation.Differences.Count +
            " complete differential mismatches; first " + observation.Differences[0].Path + " (" + observation.Differences[0].Kind + "). See the owned-observation receipt.");
    }

    private static async Task CaptureAll()
    {
        var failures = new List<Exception>();
        for (var index = 0; index < Ids.Length; index++)
            try { Check((await Captures[index].Value).CaseId == Ids[index], "Actual native observation identity changed."); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Native lifecycle observation failed: " + Ids[index], error)); }
        if (failures.Count != 0) throw new AggregateException("Not all 14 native observations completed their bounded ownership checks.", failures);
        Console.WriteLine("completions-lifecycle: all 14 actual native observations completed; strict compatibility comparisons are separate cases.");
    }

    private sealed record CaseObservation(string CaseId, List<Difference> Differences);

    private static async Task<CaseObservation> CaptureCase(int index)
    {
        var repo = FindRepo();
        using var input = JsonDocument.Parse(Pinned(repo, Family + "input.json", 65_536, InputHash));
        using var source = JsonDocument.Parse(Pinned(repo, Family + "expected.json", 1_048_576, ExpectedHash));
        using var manifest = JsonDocument.Parse(Pinned(repo, Family + "manifest.json", 8192, ManifestHash));
        using var sourceLock = JsonDocument.Parse(Pinned(repo, "tools/ReferenceOracle/openai-completions-sdk-lifecycle.lock.json", 131_072, LockHash));
        var root = input.RootElement; var cases = root.GetProperty("cases"); var observed = source.RootElement.GetProperty("observations").GetProperty("cases");
        Check(root.GetProperty("sourceSha").GetString() == SourceSha && source.RootElement.GetProperty("sourceSha").GetString() == SourceSha, "Lifecycle source identity changed.");
        Check(manifest.RootElement.GetProperty("input").GetProperty("sha256").GetString() == InputHash &&
            manifest.RootElement.GetProperty("expected").GetProperty("sha256").GetString() == ExpectedHash &&
            manifest.RootElement.GetProperty("lock").GetProperty("sha256").GetString() == LockHash, "Manifest hash authority changed.");
        Check(cases.GetArrayLength() == Ids.Length && observed.GetArrayLength() == Ids.Length, "Complete lifecycle case inventory changed.");
        for (var row = 0; row < Ids.Length; row++)
            Check(cases[row].GetProperty("caseId").GetString() == Ids[row] && observed[row].GetProperty("caseId").GetString() == Ids[row], "Lifecycle case order/identity changed.");
        ComparatorControls();
        var test = cases[index]; var expected = observed[index]; var wire = Wire(test.GetProperty("wire"));
        Check(expected.GetProperty("responseWire").GetProperty("hex").GetString() == Convert.ToHexStringLower(wire) &&
            expected.GetProperty("responseWire").GetProperty("sha256").GetString() == Digest(wire), "Native wire differs from the genuine captured wire.");
        using var fixture = new NativeFixture(root, test, wire);
        var native = await fixture.Run();
        using var sourceFixture = new NativeFixture(root, test, wire, admittedSourceFailures: true);
        var sourceApi = await sourceFixture.RunSource();
        var sourceDifferences = CompareSourceApi(expected, sourceApi);
        var differences = new Differences();
        CompareSnapshots(expected.GetProperty("emissionSnapshots"), native.Emitted, "/emissionSnapshots", differences);
        CompareSnapshots(expected.GetProperty("drainedFrames"), native.Drained, "/drainedFrames", differences);
        differences.Json(expected.GetProperty("finalResult").GetProperty("value"), native.Final.Value, "/finalResult/value");
        CompareNumbers(expected.GetProperty("finalResult").GetProperty("numberBits"), native.Final.Value, "/finalResult/numberBits", differences);
        if (native.FinalObservation is { } finalObservation)
            differences.Json(expected.GetProperty("finalResult").GetProperty("ownUndefinedPaths"), finalObservation.Value.GetProperty("ownUndefinedPaths"), "/finalResult/ownUndefinedPaths");
        else foreach (var path in expected.GetProperty("finalResult").GetProperty("ownUndefinedPaths").EnumerateArray())
            differences.Add("unsupported-own-undefined", "/finalResult/ownUndefinedPaths" + path.GetString(), "own undefined", "this native run lacks a captured provider final observation");
        CompareSnapshots(expected.GetProperty("payloadSnapshots"), native.PayloadHooks, "/payloadSnapshots", differences);
        CompareSnapshots(expected.GetProperty("responseHooks"), native.ResponseHooks, "/responseHooks", differences);
        CompareSnapshots(expected.GetProperty("providerEvents"), native.ProviderHooks, "/providerEvents", differences);
        var sourceFetch = expected.GetProperty("fetchRequests")[0];
        differences.Json(sourceFetch.GetProperty("bodyJson").GetProperty("value"), native.RequestBody.Value, "/fetchRequests/0/bodyJson/value");
        CompareNumbers(sourceFetch.GetProperty("bodyJson").GetProperty("numberBits"), native.RequestBody.Value, "/fetchRequests/0/bodyJson/numberBits", differences);
        differences.Json(sourceFetch.GetProperty("url"), native.RequestObservation.Value.GetProperty("url"), "/fetchRequests/0/url");
        differences.Json(sourceFetch.GetProperty("method"), native.RequestObservation.Value.GetProperty("method"), "/fetchRequests/0/method");
        differences.Json(sourceFetch.GetProperty("headers"), native.RequestObservation.Value.GetProperty("headers"), "/fetchRequests/0/headers");
        differences.Json(sourceFetch.GetProperty("body"), native.RequestObservation.Value.GetProperty("body"), "/fetchRequests/0/body");
        differences.Json(sourceFetch.GetProperty("bodyUtf8Sha256"), native.RequestObservation.Value.GetProperty("bodyUtf8Sha256"), "/fetchRequests/0/bodyUtf8Sha256");
        differences.Json(expected.GetProperty("responseHooks")[0].GetProperty("value"), native.ResponseObservation.Value, "/responseHooks/0/value");
        CompareNumbers(expected.GetProperty("responseHooks")[0].GetProperty("numberBits"), native.ResponseObservation.Value, "/responseHooks/0/numberBits", differences);
        differences.Sequence(SourceMilestones(expected.GetProperty("trace")), native.Milestones, "/lifecycle/milestones");
        var checkpoints = expected.GetProperty("readerLedger").EnumerateArray().Where(row => row.GetProperty("operation").GetString() == "cancel-gate-checkpoint").ToArray();
        if (checkpoints.Length != native.Checkpoints.Count) differences.Add("count", "/lifecycle/cancel-gate-checkpoint", checkpoints.Length.ToString(), native.Checkpoints.Count.ToString());
        for (var checkpoint = 0; checkpoint < Math.Min(checkpoints.Length, native.Checkpoints.Count); checkpoint++)
            differences.Json(checkpoints[checkpoint].GetProperty("sourceState").GetProperty("value"), native.Checkpoints[checkpoint].Value, "/lifecycle/cancel-gate-checkpoint/" + checkpoint);
        var sourceState = expected.GetProperty("cleanup").GetProperty("sourceBoundary").GetProperty("sourceState").GetProperty("value");
        differences.Json(sourceState, native.Settlement.Value, "/lifecycle/settlement");
        differences.Sequence(expected.GetProperty("cleanup").GetProperty("sourceCancellation").EnumerateArray().Select(row => row.GetProperty("status").GetString()!), native.AsyncCleanupReceipts, "/lifecycle/mapped-async-cleanup-receipts");
        if (expected.GetProperty("consumer").GetProperty("mode").GetString() == "return-after-start")
            differences.Add("unsupported-return-dto", "/consumer/returnResult", expected.GetProperty("consumer").GetProperty("returnResult").GetRawText(), "native DisposeAsync returns an awaited ValueTask without a return DTO");
        differences.Json(expected.GetProperty("cleanup").GetProperty("sourceBoundary").GetProperty("bodyLocked"),
            JsonSerializer.SerializeToElement(native.ReaderLocked), "/cleanup/sourceBoundary/bodyLocked");
        differences.Add("unsupported-reader-dto", "/readerLedger", expected.GetProperty("readerLedger").GetRawText(),
            "complete WHATWG acquire/read/cancel/release/enqueue ledger remains open; actual bounded read DTOs compared separately");
        CompareBodyReadResults(expected, native.BodyReadResults, "/bodyReadResults", differences);
        differences.Add("different-consumption-seam", "/cleanup/sourceBoundary/bytesEnqueued", expected.GetProperty("cleanup").GetProperty("sourceBoundary").GetProperty("bytesEnqueued").GetRawText(),
            native.BytesRead + " native bytes read; ReadableStream enqueue/prefetch is a distinct observation");

        var receipt = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, caseId = Ids[index], sourceSha = SourceSha, expectedHash = ExpectedHash,
            nativeComparison = "complete-observable-lifecycle-differential-with-explicit-missing-seams",
            nativeReaderPolicy = test.GetProperty("behavior").GetProperty("consumer").GetString() == "return-after-start"
                ? "StartWithDetachedReaderAsync" : "StartAsync",
            sourceCaseSha256 = Digest(Encoding.UTF8.GetBytes(expected.GetRawText())), wireSha256 = Digest(wire),
            completeSourceCase = expected,
            actualFinal = native.Final.Value, actualSettlement = native.Settlement.Value,
            actualEmitted = native.Emitted.Select(row => row.Value).ToArray(), actualDrained = native.Drained.Select(row => row.Value).ToArray(),
            actualRequestBody = native.RequestBody.Value, actualRequestObservation = native.RequestObservation.Value, actualResponseObservation = native.ResponseObservation.Value,
            actualPayloadHooks = native.PayloadHooks.Select(row => row.Value).ToArray(), actualResponseHooks = native.ResponseHooks.Select(row => row.Value).ToArray(),
            actualProviderHooks = native.ProviderHooks.Select(row => row.Value).ToArray(), actualFinalObservation = native.FinalObservation?.Value,
            actualReaderOperations = native.ReaderOperations.Select(row => row.Value).ToArray(), native.ReaderLocked,
            actualBodyReadResults = native.BodyReadResults.Select(row => row.Value).ToArray(),
            sourceTrace = expected.GetProperty("trace"), actualTrace = native.Trace, native.Milestones,
            actualCheckpoints = native.Checkpoints.Select(row => row.Value).ToArray(), native.AsyncCleanupReceipts, native.AsyncDisposals, native.SyncDisposals,
            native.BytesRead, native.ReadCalls, native.BodyDisposedAtNativeSettlement, native.SourceBodyDisposed, native.ResponseDisposed, native.RequestDisposed,
            actualDrainedTerminal = native.DrainedTerminal?.Value,
            sourceEmittedCount = expected.GetProperty("emissionSnapshots").GetArrayLength(), actualEmittedCount = native.Emitted.Count,
            sourceDrainedCount = expected.GetProperty("drainedFrames").GetArrayLength(), actualDrainedCount = native.Drained.Count,
            comparison = differences.Items,
            publicSourceExecution = new
            {
                invocationIdentity = "second-independent-owned-http-invocation",
                sourceApiCheckpointPolicy = "abort-awaits-real-source-task-with-cancel-gate-still-held;normal-DONE-checks-held-gate",
                actualEmitted = sourceApi.Native.Emitted.Select(row => row.Value).ToArray(),
                actualDrained = sourceApi.Native.Drained.Select(row => row.Value).ToArray(),
                sourceResult = sourceApi.Native.FinalObservation?.Value, serializedSourceResult = sourceApi.SerializedResult,
                sourceApi.Native.Trace, sourceApi.Native.Milestones,
                checkpoints = sourceApi.Native.Checkpoints.Select(row => row.Value).ToArray(),
                settlement = sourceApi.Native.Settlement.Value, sourceApi.Native.ReaderLocked,
                sourceApi.Native.BytesRead, sourceApi.Native.ReadCalls,
                readerOperations = sourceApi.Native.ReaderOperations.Select(row => row.Value).ToArray(),
                bodyReadResults = sourceApi.Native.BodyReadResults.Select(row => row.Value).ToArray(),
                inputPublications = sourceApi.Native.InputPublications.Select(row => row.Value).ToArray(),
                inputPublicationComparison = CompareInputPublications(expected, sourceApi.Native.InputPublications).Items,
                returnDto = sourceApi.ReturnDto?.Raw.Value,
                sourceApi.Cleanup, canonicalResult = PiWireJson.WriteMessage(sourceApi.Canonical.Message).Value,
                canonicalFailure = sourceApi.Canonical.Failure,
                publicationWitnesses = sourceApi.Publications.Select(publication => new { publication.Sequence, publication.MonotonicTimestamp,
                    type = publication.Emission.Raw.Value.GetProperty("value").GetProperty("type").GetString() }).ToArray(),
                sourceApi.Native.BodyDisposedAtNativeSettlement, sourceApi.Native.SourceBodyDisposed,
                sourceApi.Native.ResponseDisposed, sourceApi.Native.RequestDisposed,
                payloadHooks = sourceApi.Native.PayloadHooks.Select(row => row.Value).ToArray(),
                responseHooks = sourceApi.Native.ResponseHooks.Select(row => row.Value).ToArray(),
                providerHooks = sourceApi.Native.ProviderHooks.Select(row => row.Value).ToArray(),
                requestObservation = sourceApi.Native.RequestObservation.Value,
                comparison = sourceDifferences.Items
            }
        });
        Check(Encoding.UTF8.GetByteCount(receipt) <= 1_048_576, "Complete native differential receipt exceeded its output bound.");
        Console.WriteLine(receipt);
        return new(Ids[index], differences.Items.Concat(sourceDifferences.Items).ToList());
    }

    private sealed record NativeResult(List<JsonData> Emitted, List<JsonData> Drained, JsonData Final, JsonData RequestBody, JsonData RequestObservation,
        JsonData ResponseObservation, JsonData Settlement, List<JsonData> Checkpoints, string[] Trace, string[] Milestones, string[] AsyncCleanupReceipts,
        int AsyncDisposals, int SyncDisposals, int BytesRead, int ReadCalls, bool BodyDisposedAtNativeSettlement,
        bool SourceBodyDisposed, bool ResponseDisposed, bool RequestDisposed, JsonData? DrainedTerminal,
        List<JsonData> PayloadHooks, List<JsonData> ResponseHooks, List<JsonData> ProviderHooks, JsonData? FinalObservation,
        List<JsonData> ReaderOperations, bool ReaderLocked, List<JsonData> BodyReadResults)
    {
        public List<JsonData> InputPublications { get; init; } = [];
    }
    private sealed record PublicSourceResult(NativeResult Native, CompletionsSourceSnapshot? ReturnDto,
        string SerializedResult, CompletionsCleanupOutcome Cleanup, ChatResult Canonical, ImmutableArray<CompletionsSourcePublication> Publications);

    private sealed class NativeFixture : IDisposable
    {
        private readonly JsonElement _test;
        private readonly List<string> _trace = [];
        private readonly List<JsonData> _emitted = [], _drained = [], _checkpoints = [];
        private readonly List<JsonData> _payloadHooks = [], _responseHooks = [], _providerHooks = [];
        private readonly List<JsonData> _readerOperations = [];
        private readonly List<JsonData> _bodyReadResults = [];
        private readonly List<JsonData> _inputPublications = [];
        private readonly TaskCompletionSource _readerCancelEntered = Gate(), _releaseReaderCancel = Gate();
        private NativeReader? _reader;
        private readonly CancellationTokenSource _caller = new();
        private readonly FakeHttpHandler _handler;
        private readonly HttpClient _client;
        private readonly NativeBody _body;
        private readonly StreamProbeContent _content;
        private readonly CompletionsHttpSseTransport _transport;
        private readonly ChatRequest _request;
        private JsonData _responseObservation = JsonData.EmptyObject;
        private JsonData _requestBody = JsonData.EmptyObject;
        private JsonData _requestObservation = JsonData.EmptyObject;
        private HttpRequestMessage? _ownedRequest;
        private HttpResponseMessage? _ownedResponse;
        private bool _terminal, _producerEnded, _settled;
        private CancellationToken _ownedWorkToken;
        private JsonData? _drainedTerminal;
        private JsonData? _finalObservation;
        private long _snapshotCharacters;
        private ChatRun? _run;
        private CompletionsRun? _sourceRun;
        private readonly bool _admittedSourceFailures;

        public NativeFixture(JsonElement fixture, JsonElement test, byte[] wire, bool admittedSourceFailures = false)
        {
            _admittedSourceFailures = admittedSourceFailures;
            _test = test; var model = fixture.GetProperty("model"); var common = fixture.GetProperty("commonOptions"); var response = fixture.GetProperty("response");
            var descriptor = new ModelDescriptor(model.GetProperty("id").GetString()!, model.GetProperty("api").GetString()!, model.GetProperty("provider").GetString()!);
            _request = new(descriptor, fixture.GetProperty("context").GetProperty("messages").EnumerateArray()
                .Select(message => new TranscriptEntry(message.GetProperty("role").GetString()!, JsonData.FromElement(message))).ToImmutableArray(),
                fixture.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64());
            var behavior = test.GetProperty("behavior");
            Check((behavior.GetProperty("abort").GetString() is "none" or "pending-read") &&
                (behavior.GetProperty("consumer").GetString() is "drain" or "return-after-start"), "Unimplemented lifecycle fixture behavior.");
            _body = new(this, wire, test.GetProperty("wire").GetProperty("chunkBytes").GetInt32(), behavior);
            _content = new(_body);
            var hooks = new CompletionsLifecycleHooks
            {
                OnPayload = (payload, _, _) =>
                {
                    Record("hook:onPayload"); AddSnapshot(_payloadHooks, HookSnapshot(payload.Value, payload.OwnUndefinedPaths));
                    return ValueTask.FromResult<JsonData?>(null);
                },
                OnResponse = (metadata, _, _) =>
                {
                    Record("hook:onResponse"); var value = JsonData.Parse(JsonSerializer.Serialize(new { status = metadata.Status, headers = metadata.Headers.Value }));
                    AddSnapshot(_responseHooks, HookSnapshot(value, []));
                    if (behavior.GetProperty("onResponse").GetString() == "throw") throw InvocationFault("onResponse");
                    return ValueTask.CompletedTask;
                },
                OnProviderStreamEvent = (chunk, _, _) =>
                {
                    Record("hook:provider"); AddSnapshot(_providerHooks, HookSnapshot(chunk, [])); return ValueTask.CompletedTask;
                },
                OnSourcePublished = admittedSourceFailures ? publication =>
                {
                    AddSnapshot(_emitted, publication.Emission.Raw);
                    Record("emit:" + publication.Emission.Raw.Value.GetProperty("value").GetProperty("type").GetString());
                } : null
            };
            _handler = new(async (message, token) =>
            {
                Record("send"); var requestBytes = await message.Content!.ReadAsByteArrayAsync(token);
                Check(requestBytes.Length <= 65_536, "Native fixture request exceeded its observation bound.");
                var requestText = new UTF8Encoding(false, true).GetString(requestBytes); _requestBody = JsonData.Parse(requestText);
                var requestHeaders = message.Headers.Concat(message.Content.Headers).Select(header =>
                    new[] { header.Key.ToLowerInvariant(), header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                        ? message.Headers.UserAgent.ToString() : string.Join(", ", header.Value) }).OrderBy(header => header[0], StringComparer.Ordinal).ToArray();
                _requestObservation = JsonData.Parse(JsonSerializer.Serialize(new { url = message.RequestUri!.AbsoluteUri, method = message.Method.Method,
                    headers = requestHeaders, body = requestText, bodyUtf8Sha256 = Digest(requestBytes) }));
                _ownedResponse = new((HttpStatusCode)response.GetProperty("status").GetInt32()) { Content = _content };
                foreach (var header in response.GetProperty("headers").EnumerateObject())
                    if (!_ownedResponse.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
                        Check(_content.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()), "Owned fixture response header could not be represented.");
                var actualHeaders = _ownedResponse.Headers.Concat(_content.Headers).ToDictionary(header => header.Key.ToLowerInvariant(),
                    header => string.Join(", ", header.Value), StringComparer.Ordinal);
                // This observes actual owned HttpResponseMessage metadata; it does not call a fabricated onResponse hook.
                _responseObservation = JsonData.Parse(JsonSerializer.Serialize(new { status = (int)_ownedResponse.StatusCode, headers = actualHeaders }));
                Record("response:created");
                Record("response:handoff");
                return _ownedResponse;
            });
            _client = new(_handler);
            var factory = new CompletionsKeyAuthRequestFactory(new(model.GetProperty("baseUrl").GetString()! + "/chat/completions"), descriptor,
                new(Reasoning: model.GetProperty("reasoning").GetBoolean()), new(MaxTokens: common.GetProperty("maxTokens").GetDouble(),
                    Temperature: common.GetProperty("temperature").GetDouble(), Headers: JsonData.FromElement(common.GetProperty("headers"))));
            var rates = model.GetProperty("cost");
            _transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(_client, async (request, token) =>
            { Record("factory"); return _ownedRequest = await factory.CreateAsync(request, common.GetProperty("apiKey").GetString()!, hooks, token); },
                options: new() { Hooks = hooks, OnBodyRead = result => AddSnapshot(_bodyReadResults, result.Snapshot),
                    OnInputPublished = publication => AddSnapshot(_inputPublications, publication.PhysicalEof
                        ? JsonData.Parse("{\"operation\":\"eof\"}")
                        : JsonData.Parse(JsonSerializer.Serialize(new { operation = "enqueue", offset = publication.Offset,
                            hex = Convert.ToHexStringLower(publication.Value.AsSpan()) }))),
                    BodyReaderFactory = (body, token) =>
                {
                    token.ThrowIfCancellationRequested(); Record("acquire"); ReaderOperation(new { operation = "acquire" });
                    if (behavior.GetProperty("reader").GetString() == "acquire-throw") { Record("acquire:failed"); throw InvocationFault("acquire"); }
                    _reader = new(this, body, behavior); Record("acquired"); ReaderOperation(new { operation = "acquired", locked = _reader.Locked });
                    return ValueTask.FromResult<ICompletionsResponseBodyReader>(_reader);
                } },
                completionsOptions: CompletionsSourceEventProjection.CaptureOwnedSnapshots(new(Rates: new(rates.GetProperty("input").GetDecimal(),
                    rates.GetProperty("output").GetDecimal(), rates.GetProperty("cacheRead").GetDecimal(), rates.GetProperty("cacheWrite").GetDecimal()))));
        }

        public async Task<NativeResult> Run()
        {
            var observer = new EmissionObserver(_transport, this);
            var client = new ChatClient(observer, capacity: 1);
            _run = _test.GetProperty("behavior").GetProperty("consumer").GetString() == "return-after-start"
                ? await client.StartWithDetachedReaderAsync(_request, _caller.Token)
                : await client.StartAsync(_request, _caller.Token);
            var settled = Settle(_run.Completion); var drain = Drain(_run);
            try
            {
                if (_test.GetProperty("behavior").GetProperty("abort").GetString() == "pending-read")
                { await _body.ReadEntered.Task.WaitAsync(Deadline); Record("caller:abort"); _caller.Cancel(); }
                if (_test.GetProperty("behavior").GetProperty("cancel").GetString()!.StartsWith("gate", StringComparison.Ordinal))
                {
                    await _readerCancelEntered.Task.WaitAsync(Deadline);
                    _checkpoints.Add(State()); _releaseReaderCancel.TrySetResult();
                }
                await Task.WhenAll(drain, settled).WaitAsync(Deadline);
                var result = await _run.Completion; var final = PiWireJson.WriteMessage(result.Message);
                if (_drainedTerminal is not null)
                {
                    var terminal = PiWireJson.ReadEvent(_drainedTerminal.Value) as StreamTerminalEvent;
                    Check(terminal is not null, "A drained native terminal lost its actual type.");
                    var consistency = new Differences(); consistency.Json(final.Value, PiWireJson.WriteMessage(terminal!.Message).Value, "/native-terminal-result");
                    Check(consistency.Items.Count == 0, "Native drained terminal and Completion disagree.");
                }
                var requestDisposed = false;
                if (_ownedRequest is not null)
                    try { _ = await _ownedRequest.Content!.ReadAsStringAsync(); }
                    catch (ObjectDisposedException) { requestDisposed = true; }
                Check(_ownedRequest is not null && requestDisposed && !_handler.Disposed && _content.Disposed, "Native lifecycle retained request/response ownership or disposed the borrowed client.");
                var bodyDisposedAtSettlement = _body.Disposed;
                // Failed acquisition leaves an untransferred fixture stream. Its owner closes it before publishing this receipt.
                if (!_body.Disposed) { Record("fixture:orphan-stream-close"); _body.Dispose(); }
                Check(_body.Disposed, "The fixture retained an untransferred stream after joined settlement.");
                return new(_emitted, _drained, final, _requestBody, _requestObservation, _responseObservation, State(), _checkpoints,
                    Trace(), Milestones(), _reader?.CancelReceipt is { } receipt ? [receipt] : [], _body.AsyncDisposals, _body.SyncDisposals,
                    _body.BytesRead, _body.ReadCalls, bodyDisposedAtSettlement, _body.Disposed, _content.Disposed, requestDisposed, _drainedTerminal,
                    _payloadHooks, _responseHooks, _providerHooks, _finalObservation, _readerOperations, _reader?.Locked ?? false, _bodyReadResults)
                    { InputPublications = _inputPublications };
            }
            finally
            {
                _caller.Cancel(); _body.ReleaseRead.TrySetResult(); _releaseReaderCancel.TrySetResult();
                await _run.DisposeAsync(); await Observe(drain); await Observe(settled);
                // A stream that failed acquisition was never transferred into the transport's body scope.
                if (!_body.Disposed) { Record("fixture:orphan-stream-close"); _body.Dispose(); }
            }
        }

        private async Task Settle(Task<ChatResult> completion) { await completion; lock (_trace) { _settled = true; _trace.Add("result-settled"); } }
        public async Task<PublicSourceResult> RunSource()
        {
            _sourceRun = await _transport.StartAsync(_request, _caller.Token);
            CompletionsSourceSnapshot? returned = null;
            var settled = ObserveSourceSettlement(_sourceRun.SourceResult);
            var drain = ConsumeSource();
            try
            {
                var behavior = _test.GetProperty("behavior");
                if (behavior.GetProperty("abort").GetString() == "pending-read")
                { await _body.ReadEntered.Task.WaitAsync(Deadline); Record("caller:abort"); _caller.Cancel(); }
                if (behavior.GetProperty("cancel").GetString()!.StartsWith("gate", StringComparison.Ordinal))
                {
                    await _readerCancelEntered.Task.WaitAsync(Deadline);
                    if (behavior.GetProperty("abort").GetString() == "pending-read")
                    {
                        await _sourceRun.SourceResult.WaitAsync(Deadline);
                        Check(!_sourceRun.CleanupCompletion.IsCompleted && !_sourceRun.CanonicalCompletion.IsCompleted,
                            "Actual source abort lost the held same-invocation ownership boundary.");
                    }
                    _checkpoints.Add(State()); _releaseReaderCancel.TrySetResult();
                }
                await Task.WhenAll(drain, settled).WaitAsync(Deadline);
                var source = await _sourceRun.SourceResult;
                var cleanup = await _sourceRun.CleanupCompletion.WaitAsync(Deadline);
                var canonical = await _sourceRun.CanonicalCompletion.WaitAsync(Deadline);
                _finalObservation = source.Snapshot.Raw;
                var final = JsonData.FromElement(_finalObservation.Value.GetProperty("value"));
                var requestDisposed = false;
                if (_ownedRequest is not null)
                    try { _ = await _ownedRequest.Content!.ReadAsStringAsync(); }
                    catch (ObjectDisposedException) { requestDisposed = true; }
                Check(requestDisposed && _content.Disposed && !_handler.Disposed,
                    "Public source cleanup/canonical promises failed their actual request/response ownership barrier.");
                var bodyAtSettlement = _body.Disposed;
                if (!_body.Disposed) { Record("fixture:orphan-stream-close"); _body.Dispose(); }
                var native = new NativeResult(_emitted, _drained, final, _requestBody, _requestObservation, _responseObservation,
                    State(), _checkpoints, Trace(), Milestones(), _reader?.CancelReceipt is { } receipt ? [receipt] : [],
                    _body.AsyncDisposals, _body.SyncDisposals, _body.BytesRead, _body.ReadCalls, bodyAtSettlement,
                    _body.Disposed, _content.Disposed, requestDisposed, null, _payloadHooks, _responseHooks, _providerHooks,
                    _finalObservation, _readerOperations, _reader?.Locked ?? false, _bodyReadResults)
                    { InputPublications = _inputPublications };
                return new(native, returned, source.Snapshot.SerializedJson, cleanup, canonical, _sourceRun.SourcePublications);
            }
            finally
            {
                _caller.Cancel(); _body.ReleaseRead.TrySetResult(); _releaseReaderCancel.TrySetResult();
                await _sourceRun.DisposeAsync(); await Observe(drain); await Observe(settled);
                if (!_body.Disposed) _body.Dispose();
            }

            async Task ConsumeSource()
            {
                while (true)
                {
                    var next = await _sourceRun.NextAsync(); if (next.Done) return;
                    var frame = next.Value!; AddSnapshot(_drained, frame.Snapshot.Raw); Record("source:read:" + frame.Type);
                    if (frame.Type == "start" && _test.GetProperty("behavior").GetProperty("consumer").GetString() == "return-after-start")
                    {
                        Record("consumer:return"); returned = (await _sourceRun.ReturnAsync()).Snapshot; Record("consumer:returned");
                        _body.ReleaseRead.TrySetResult(); return;
                    }
                }
            }
        }
        private async Task ObserveSourceSettlement(Task<CompletionsSourceMessage> result)
        { await result; Record("producer-end"); Record("result-settled"); }
        private async Task Drain(ChatRun run)
        {
            await using var reader = run.ReadEventsAsync().GetAsyncEnumerator();
            while (await reader.MoveNextAsync())
            {
                var frame = reader.Current; var snapshot = CompletionsSourceEventProjection.ReadDrain(frame);
                AddSnapshot(_drained, snapshot ?? MissingSnapshot(frame)); Record("drain:" + PiWireJson.WriteEvent(frame).Value.GetProperty("type").GetString());
                if (frame is StreamTerminalEvent) _drainedTerminal = PiWireJson.WriteEvent(frame);
                if (frame is StreamStarted && _test.GetProperty("behavior").GetProperty("consumer").GetString() == "return-after-start")
                { Record("consumer:return"); await reader.DisposeAsync(); Record("consumer:returned"); _body.ReleaseRead.TrySetResult(); return; }
            }
        }
        private sealed class EmissionObserver(IChatTransport inner, NativeFixture owner) : IChatTransport
        {
            public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
            {
                owner._ownedWorkToken = token;
                try
                {
                    await foreach (var frame in inner.StreamAsync(request, token).WithCancellation(token))
                    {
                        var snapshot = CompletionsSourceEventProjection.ReadEmission(frame);
                        owner.AddSnapshot(owner._emitted, snapshot ?? MissingSnapshot(frame));
                        owner.Record("emit:" + PiWireJson.WriteEvent(frame).Value.GetProperty("type").GetString());
                        if (frame is StreamTerminalEvent) lock (owner._trace) owner._terminal = true;
                        if (frame is StreamTerminalEvent terminal) owner._finalObservation = CompletionsSourceEventProjection.ReadFinalObservation(terminal);
                        yield return frame;
                    }
                }
                finally { lock (owner._trace) { owner._producerEnded = true; owner._trace.Add("producer-end"); } }
            }
        }
        private static JsonData MissingSnapshot(StreamEvent frame) => JsonData.Parse(JsonSerializer.Serialize(new
        { nativeSourceSnapshotMissing = true, nativeEvent = PiWireJson.WriteEvent(frame).Value }));
        private static JsonData HookSnapshot(JsonData value, IEnumerable<string> undefined) => JsonData.Parse(JsonSerializer.Serialize(new
        { value = value.Value, ownUndefinedPaths = undefined.ToArray() }));
        private JsonData State()
        {
            lock (_trace) return JsonData.Parse(JsonSerializer.Serialize(new
            {
                terminalPushed = _sourceRun?.SourceTerminalPublished ?? _terminal,
                resultSettled = _sourceRun?.SourceResult.IsCompleted ?? _settled,
                producerEnded = _sourceRun?.SourceResult.IsCompleted ?? _producerEnded,
                signalAborted = _sourceRun?.IsCancellationRequested ?? _ownedWorkToken.IsCancellationRequested
            }));
        }
        private Exception InvocationFault(string operation) => _admittedSourceFailures
            ? new CompletionsPublicFailureException(new("Authored owned-response " + operation + " fault")) : Fault(operation);
        private void AddSnapshot(List<JsonData> target, JsonData snapshot)
        {
            lock (_trace)
            {
                Check(target.Count < 4096 && snapshot.ToString().Length <= 1_048_576 - _snapshotCharacters, "Native owned snapshots exceeded their cumulative bound.");
                _snapshotCharacters += snapshot.ToString().Length; target.Add(snapshot);
            }
        }
        private void Record(string item) { lock (_trace) { Check(_trace.Count < 4096, "Native lifecycle trace exceeded its bound."); _trace.Add(item); } }
        private void ReaderOperation<T>(T operation) => AddSnapshot(_readerOperations, JsonData.Parse(JsonSerializer.Serialize(operation)));
        private string[] Trace() { lock (_trace) return _trace.ToArray(); }
        private string[] Milestones()
        { lock (_trace) return _trace.Where(item => (item is "send" or "acquire" or "acquired" or "cleanup:async" or "cleanup:sync" or "consumer:return" or "consumer:returned" or "caller:abort" or "result-settled") || item.StartsWith("emit:", StringComparison.Ordinal) || item.StartsWith("hook:", StringComparison.Ordinal)).ToArray(); }
        public void Dispose() { _caller.Dispose(); _client.Dispose(); _ownedResponse?.Dispose(); if (!_body.Disposed) _body.Dispose(); }

        private sealed class NativeReader(NativeFixture owner, Stream body, JsonElement behavior) : ICompletionsResponseBodyReader
        {
            private readonly ICompletionsResponseBodyReader _core = CompletionsResponseBodyReader.FromStream(body);
            public bool Locked { get; private set; } = true;
            public string? CancelReceipt;

            public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
            {
                owner.ReaderOperation(new { operation = "read", destinationBytes = destination.Length, cancellationRequested = token.IsCancellationRequested });
                try
                {
                    var count = await _core.ReadAsync(destination, token);
                    owner.ReaderOperation(new { operation = "read-result", count, bytes = destination[..count].ToArray().Select(value => (int)value).ToArray() });
                    return count;
                }
                catch
                {
                    owner.ReaderOperation(new { operation = "read-failed", cancellationRequested = token.IsCancellationRequested }); throw;
                }
            }

            public async ValueTask CancelAsync()
            {
                owner.Record("cleanup:async"); owner.ReaderOperation(new { operation = "cancel" }); owner._readerCancelEntered.TrySetResult();
                try
                {
                    await _core.CancelAsync();
                    if (behavior.GetProperty("cancel").GetString()!.StartsWith("gate", StringComparison.Ordinal)) await owner._releaseReaderCancel.Task;
                    if (behavior.GetProperty("cancel").GetString()!.EndsWith("throw", StringComparison.Ordinal)) throw Fault("cancel");
                    CancelReceipt = "fulfilled"; owner.ReaderOperation(new { operation = "cancel-settled", status = CancelReceipt });
                }
                catch { CancelReceipt = "rejected"; owner.ReaderOperation(new { operation = "cancel-settled", status = CancelReceipt }); throw; }
            }

            public void Release()
            {
                _core.Release(); Locked = false; owner.Record("cleanup:sync"); owner.ReaderOperation(new { operation = "release", locked = Locked });
                if (behavior.GetProperty("reader").GetString() == "release-throw") throw Fault("release");
            }
        }

        private sealed class NativeBody(NativeFixture owner, byte[] bytes, int chunkBytes, JsonElement behavior) : Stream
        {
            private int _offset; private Task? _closing; private bool _syncClosed;
            public int BytesRead => _offset;
            public int ReadCalls, AsyncDisposals, SyncDisposals;
            public bool Disposed;
            public TaskCompletionSource ReadEntered { get; } = Gate();
            public TaskCompletionSource ReleaseRead { get; } = Gate();
            public override bool CanRead => !Disposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested(); ReadCalls++; owner.Record("read:" + _offset); ReadEntered.TrySetResult();
                if (_offset == 0 && (behavior.GetProperty("abort").GetString() == "pending-read" || behavior.GetProperty("consumer").GetString() == "return-after-start"))
                    await ReleaseRead.Task.WaitAsync(token);
                if (_offset == bytes.Length) { owner.Record("read:eof"); return 0; }
                var count = Math.Min(chunkBytes, Math.Min(buffer.Length, bytes.Length - _offset));
                bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; owner.Record("read:result:" + count); return count;
            }
            public override ValueTask DisposeAsync() => new(_closing ??= CloseOwnedBody());
            private Task CloseOwnedBody()
            {
                AsyncDisposals++; owner.Record("owner:body-dispose-async"); Disposed = true;
                return Task.CompletedTask;
            }
            protected override void Dispose(bool disposing)
            {
                if (!disposing || _syncClosed) return;
                _syncClosed = true; Disposed = true; SyncDisposals++; owner.Record("owner:body-dispose-sync");
                base.Dispose(disposing);
            }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static void CompareSnapshots(JsonElement source, List<JsonData> native, string path, Differences differences)
    {
        if (source.GetArrayLength() != native.Count) differences.Add("count", path, source.GetArrayLength().ToString(), native.Count.ToString());
        for (var index = 0; index < Math.Min(source.GetArrayLength(), native.Count); index++)
        {
            var actual = native[index].Value;
            if (!actual.TryGetProperty("value", out var value)) { differences.Add("missing-source-snapshot", path + "/" + index, "owned source snapshot", "native compact frame only"); continue; }
            differences.Json(source[index].GetProperty("value"), value, path + "/" + index + "/value");
            differences.Json(source[index].GetProperty("ownUndefinedPaths"), actual.GetProperty("ownUndefinedPaths"), path + "/" + index + "/ownUndefinedPaths");
            CompareNumbers(source[index].GetProperty("numberBits"), value, path + "/" + index + "/numberBits", differences);
        }
    }
    private static Differences CompareSourceApi(JsonElement expected, PublicSourceResult execution)
    {
        const string prefix = "/publicSourceExecution";
        var actual = execution.Native; var differences = new Differences();
        CompareSnapshots(expected.GetProperty("emissionSnapshots"), actual.Emitted, prefix + "/emissionSnapshots", differences);
        CompareSnapshots(expected.GetProperty("drainedFrames"), actual.Drained, prefix + "/drainedFrames", differences);
        differences.Json(expected.GetProperty("finalResult").GetProperty("value"), actual.Final.Value, prefix + "/finalResult/value");
        CompareNumbers(expected.GetProperty("finalResult").GetProperty("numberBits"), actual.Final.Value, prefix + "/finalResult/numberBits", differences);
        differences.Json(expected.GetProperty("finalResult").GetProperty("ownUndefinedPaths"),
            actual.FinalObservation!.Value.GetProperty("ownUndefinedPaths"), prefix + "/finalResult/ownUndefinedPaths");
        CompareSnapshots(expected.GetProperty("payloadSnapshots"), actual.PayloadHooks, prefix + "/payloadSnapshots", differences);
        CompareSnapshots(expected.GetProperty("responseHooks"), actual.ResponseHooks, prefix + "/responseHooks", differences);
        CompareSnapshots(expected.GetProperty("providerEvents"), actual.ProviderHooks, prefix + "/providerEvents", differences);
        var fetch = expected.GetProperty("fetchRequests")[0];
        differences.Json(fetch.GetProperty("bodyJson").GetProperty("value"), actual.RequestBody.Value, prefix + "/fetchRequests/0/bodyJson/value");
        CompareNumbers(fetch.GetProperty("bodyJson").GetProperty("numberBits"), actual.RequestBody.Value, prefix + "/fetchRequests/0/bodyJson/numberBits", differences);
        foreach (var field in new[] { "url", "method", "headers", "body", "bodyUtf8Sha256" })
            differences.Json(fetch.GetProperty(field), actual.RequestObservation.Value.GetProperty(field), prefix + "/fetchRequests/0/" + field);
        differences.Json(expected.GetProperty("responseHooks")[0].GetProperty("value"), actual.ResponseObservation.Value, prefix + "/responseHooks/0/value");
        CompareNumbers(expected.GetProperty("responseHooks")[0].GetProperty("numberBits"), actual.ResponseObservation.Value, prefix + "/responseHooks/0/numberBits", differences);
        differences.Sequence(SourceMilestones(expected.GetProperty("trace")), actual.Milestones, prefix + "/lifecycle/milestones");
        var checkpoints = expected.GetProperty("readerLedger").EnumerateArray().Where(row => row.GetProperty("operation").GetString() == "cancel-gate-checkpoint").ToArray();
        if (checkpoints.Length != actual.Checkpoints.Count)
            differences.Add("count", prefix + "/lifecycle/cancel-gate-checkpoint", checkpoints.Length.ToString(), actual.Checkpoints.Count.ToString());
        for (var index = 0; index < Math.Min(checkpoints.Length, actual.Checkpoints.Count); index++)
            differences.Json(checkpoints[index].GetProperty("sourceState").GetProperty("value"), actual.Checkpoints[index].Value, prefix + "/lifecycle/cancel-gate-checkpoint/" + index);
        var boundary = expected.GetProperty("cleanup").GetProperty("sourceBoundary");
        differences.Json(boundary.GetProperty("sourceState").GetProperty("value"), actual.Settlement.Value, prefix + "/lifecycle/settlement");
        differences.Sequence(expected.GetProperty("cleanup").GetProperty("sourceCancellation").EnumerateArray().Select(row => row.GetProperty("status").GetString()!),
            actual.AsyncCleanupReceipts, prefix + "/lifecycle/mapped-async-cleanup-receipts");
        if (expected.GetProperty("consumer").GetProperty("mode").GetString() == "return-after-start")
        {
            var source = expected.GetProperty("consumer").GetProperty("returnResult");
            var returned = execution.ReturnDto ?? throw new InvalidOperationException("Actual public return operation did not produce its DTO.");
            differences.Json(source.GetProperty("value"), returned.Raw.Value.GetProperty("value"), prefix + "/consumer/returnResult/value");
            differences.Json(source.GetProperty("ownUndefinedPaths"), returned.Raw.Value.GetProperty("ownUndefinedPaths"), prefix + "/consumer/returnResult/ownUndefinedPaths");
            CompareNumbers(source.GetProperty("numberBits"), returned.Raw.Value.GetProperty("value"), prefix + "/consumer/returnResult/numberBits", differences);
        }
        differences.Json(boundary.GetProperty("bodyLocked"), JsonSerializer.SerializeToElement(actual.ReaderLocked), prefix + "/cleanup/sourceBoundary/bodyLocked");
        differences.Add("unsupported-reader-dto", prefix + "/readerLedger", expected.GetProperty("readerLedger").GetRawText(),
            "complete WHATWG acquire/read/cancel/release/enqueue ledger remains open; actual independent bounded read DTOs compared separately");
        CompareBodyReadResults(expected, actual.BodyReadResults, prefix + "/bodyReadResults", differences);
        differences.Add("different-consumption-seam", prefix + "/cleanup/sourceBoundary/bytesEnqueued", boundary.GetProperty("bytesEnqueued").GetRawText(),
            actual.BytesRead + " actual bytes read; no fabricated enqueue/prefetch");
        return differences;
    }
    private static Differences CompareInputPublications(JsonElement expected, List<JsonData> actual)
    {
        // Additional exact qualification of the real enqueue boundary. The complete
        // original comparison remains unchanged; full reader ABI gaps still fail it.
        var source = expected.GetProperty("readerLedger").EnumerateArray().Where(row =>
            row.GetProperty("operation").GetString() is "enqueue" or "eof").ToArray();
        var differences = new Differences();
        differences.Json(JsonSerializer.SerializeToElement(source),
            JsonSerializer.SerializeToElement(actual.Select(row => row.Value).ToArray()), "/sourceInput/publications");
        return differences;
    }

    private static void CompareBodyReadResults(JsonElement expected, List<JsonData> actual, string path, Differences differences)
    {
        // This additive requirement does not replace/drop any complete old lifecycle row.
        var source = expected.GetProperty("readerLedger").EnumerateArray()
            .Where(row => row.GetProperty("operation").GetString() == "reader-read-result").Select(row => row.GetProperty("result")).ToArray();
        if (source.Length != actual.Count) differences.Add("count", path, source.Length.ToString(), actual.Count.ToString());
        for (var index = 0; index < Math.Min(source.Length, actual.Count); index++)
        {
            var value = actual[index].Value;
            differences.Json(source[index].GetProperty("value"), value.GetProperty("value"), path + "/" + index + "/value");
            differences.Json(source[index].GetProperty("ownUndefinedPaths"), value.GetProperty("ownUndefinedPaths"), path + "/" + index + "/ownUndefinedPaths");
            CompareNumbers(source[index].GetProperty("numberBits"), value.GetProperty("value"), path + "/" + index + "/numberBits", differences);
        }
    }
    private static void CompareNumbers(JsonElement expected, JsonElement actual, string path, Differences differences)
    {
        var native = new Dictionary<string, string>(StringComparer.Ordinal); Numbers(actual, "", native);
        var source = expected.EnumerateArray().ToDictionary(row => row.GetProperty("path").GetString()!, row => row.GetProperty("hex").GetString()!, StringComparer.Ordinal);
        foreach (var key in source.Keys.Union(native.Keys, StringComparer.Ordinal))
            if (!source.TryGetValue(key, out var left) || !native.TryGetValue(key, out var right) || left != right)
                differences.Add("binary64", path + key, source.GetValueOrDefault(key, "missing"), native.GetValueOrDefault(key, "missing"));
    }
    private static void Numbers(JsonElement value, string path, Dictionary<string, string> result)
    {
        if (value.ValueKind == JsonValueKind.Number) result.Add(path, unchecked((ulong)BitConverter.DoubleToInt64Bits(value.GetDouble())).ToString("x16"));
        else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) Numbers(property.Value, path + "/" + Escape(property.Name), result);
        else if (value.ValueKind == JsonValueKind.Array) { var index = 0; foreach (var item in value.EnumerateArray()) Numbers(item, path + "/" + index++, result); }
    }
    private sealed record Difference(string Kind, string Path, string Expected, string Actual);
    private sealed class Differences
    {
        public List<Difference> Items { get; } = [];
        public void Add(string kind, string path, string expected, string actual)
        { Check(Items.Count < 4096, "Lifecycle differential exceeded its mismatch bound."); Items.Add(new(kind, path, expected, actual)); }
        public void Sequence(IEnumerable<string> expected, IEnumerable<string> actual, string path)
        { var left = expected.ToArray(); var right = actual.ToArray(); if (!left.SequenceEqual(right)) Add("ordered-sequence", path, string.Join(",", left), string.Join(",", right)); }
        public void Json(JsonElement expected, JsonElement actual, string path)
        {
            if (expected.ValueKind != actual.ValueKind) { Add("kind", path, expected.ValueKind.ToString(), actual.ValueKind.ToString()); return; }
            if (expected.ValueKind == JsonValueKind.Object)
            {
                var left = expected.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                var right = actual.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal))
                    if (!left.TryGetValue(key, out var a)) Add("native-only-field", path + "/" + Escape(key), "missing", right[key].GetRawText());
                    else if (!right.TryGetValue(key, out var b)) Add("source-only-field", path + "/" + Escape(key), a.GetRawText(), "missing");
                    else Json(a, b, path + "/" + Escape(key));
            }
            else if (expected.ValueKind == JsonValueKind.Array)
            {
                if (expected.GetArrayLength() != actual.GetArrayLength()) Add("count", path, expected.GetArrayLength().ToString(), actual.GetArrayLength().ToString());
                for (var index = 0; index < Math.Min(expected.GetArrayLength(), actual.GetArrayLength()); index++) Json(expected[index], actual[index], path + "/" + index);
            }
            else
            {
                var left = expected.ValueKind == JsonValueKind.String ? expected.GetString()! : expected.GetRawText();
                var right = actual.ValueKind == JsonValueKind.String ? actual.GetString()! : actual.GetRawText();
                if (left != right) Add("value", path, left, right);
            }
        }
    }
    private static IEnumerable<string> SourceMilestones(JsonElement trace)
    {
        foreach (var row in trace.EnumerateArray())
        {
            var item = row.GetString()!;
            var mapped = item switch { "onPayload" => "hook:onPayload", "onResponse" => "hook:onResponse", "fetch" => "send", "body:reader-acquire" => "acquire", "body:reader-acquired" => "acquired",
                "body:reader-cancel" => "cleanup:async", "body:reader-release" => "cleanup:sync", "driver:consumer-return" => "consumer:return",
                "consumer-returned" => "consumer:returned", "body:driver-abort-pending-read" => "caller:abort", "result-settled" => "result-settled", _ => null };
            if (mapped is not null) yield return mapped; else if (item.StartsWith("emit:", StringComparison.Ordinal)) yield return item;
            else if (item.StartsWith("provider:", StringComparison.Ordinal)) yield return "hook:provider";
        }
    }
    private static byte[] Wire(JsonElement wire)
    {
        using var result = new MemoryStream();
        foreach (var segment in wire.GetProperty("segments").EnumerateArray())
        {
            var kind = segment.GetProperty("encoding").GetString(); var value = segment.GetProperty("value").GetString()!;
            var bytes = kind == "utf8" ? new UTF8Encoding(false, true).GetBytes(value) : kind == "hex" ? Convert.FromHexString(value) : throw new InvalidOperationException("Unqualified wire encoding.");
            Check(bytes.Length <= 65_536 - result.Length, "Authored wire exceeds admission bounds."); result.Write(bytes);
        }
        return result.ToArray();
    }
    private static void ComparatorControls()
    {
        foreach (var (left, right) in new[] { ("{\"a\":null}", "{}"), ("[1,2]", "[2,1]"), ("-0", "0"), ("1.0", "1"), ("\"x\"", "\"y\"") })
        { var differences = new Differences(); differences.Json(JsonData.Parse(left).Value, JsonData.Parse(right).Value, ""); Check(differences.Items.Count != 0, "Differential comparator discarded a scalar/presence/order control."); }
    }
    private static byte[] Pinned(string repo, string path, long maximum, string hash)
    { var info = new FileInfo(Path.Combine(repo, path)); Check(info.Exists && info.Length <= maximum, "Frozen lifecycle reference is absent or oversized: " + path); var bytes = File.ReadAllBytes(info.FullName); Check(bytes.Length <= maximum && Digest(bytes) == hash, "Frozen lifecycle reference changed: " + path); return bytes; }
    private static string FindRepo()
    { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, Family + "input.json"))) return path.FullName; throw new InvalidOperationException("Root must copy the frozen lifecycle reference family before running these probes."); }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static IOException Fault(string operation) => new("Authored owned-response " + operation + " fault");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
