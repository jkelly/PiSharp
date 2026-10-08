using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

// Consumes the immutable captured Source fixture without skipping later assertions.
// Authored repair controls are reported separately and do not replace captured Source cases.
internal static class Program
{
    private static readonly string[] RequiredAssertions = ["simple admission", "supported/clamped level", "context estimate/cap", "whole payload/body", "whole provider callbacks", "public final/ownUndefined/binary64", "all push/delivery/held live values", "actual cleanup/canonical completion", "actual continuation"];
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private const string FixtureSha256 = "3a1887bd368cfb1ac3f3120931aada3c91400552746ced89becb64aca1bcad21";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Supply immutable Source fixture and fresh native results path.");
        byte[] fixtureBytes = []; string? actualFixtureSha = null;
        JsonDocument fixture;
        try
        {
            fixtureBytes = await File.ReadAllBytesAsync(args[0]);
            actualFixtureSha = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant();
            Require(actualFixtureSha == FixtureSha256, "immutable fixture SHA-256 admission");
            fixture = JsonDocument.Parse(fixtureBytes);
            try { AdmitFixture(fixture.RootElement); } catch { fixture.Dispose(); throw; }
        }
        catch (Exception error)
        {
            await WriteReport(args[1], new { status = "FIXTURE REJECTED BEFORE CASE EXECUTION", expectedSha256 = FixtureSha256,
                actualSha256 = actualFixtureSha, bytes = fixtureBytes.Length, error.Message, casesExecuted = 0, assertionsExecuted = 0 });
            return 1;
        }
        using var fixtureOwner = fixture;
        var assembly = typeof(CompletionsHttpSseTransport).Assembly;
        var optionsType = assembly.GetType("PiSharp.AI.Protocols.OpenAICompletions.CompletionsSimpleOptions");
        var factoryType = assembly.GetType("PiSharp.AI.Protocols.OpenAICompletions.CompletionsSimpleRequestFactory");
        var repairProgressPath = args[1] + ".repair-progress.jsonl";
        await using var repairJournal = new SimpleRepairProgressJournal(repairProgressPath);
        var cases = new List<object>(); var failed = 0; var stopBatch = false; var genuineOwnershipDeadline = false; var genuineJournalFailure = false;
        foreach (var observation in fixture.RootElement.GetProperty("observations").EnumerateArray())
        {
            var id = observation.GetProperty("id").GetString()!;
            if (stopBatch) { cases.Add(new { id, status = "UNEXECUTED; PRIOR OWNERSHIP DEADLINE OR JOURNAL FAILURE", assertionsExecuted = 0, unexecuted = RequiredAssertions }); continue; }
            if (optionsType is null || factoryType is null)
            {
                failed++;
                cases.Add(new { id, status = "MISSING_API; LATER ASSERTIONS UNEXERCISED", assertionsExecuted = 0, unexecuted = RequiredAssertions });
                continue;
            }
            var assertions = new List<object>(); var evidence = new TestEvidence();
            var ownership = new SimpleRepairOwnership(id, record => repairJournal.WriteAsync(record with
            {
                Observations = record.Kind is "original-join-deadline" or "case-settled" ?
                    [.. record.Observations, new { kind = "genuine-source-case-evidence", id,
                        sourceExpectation = observation.Clone(), completeNativeObservations = evidence.Records() }] : record.Observations
            }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            evidence.Record("case-deadline", new { seconds = 15 });
            await ownership.ReportAsync("case-started", "genuine-source-case", "RUNNING; NOT A PASS", incomplete: true);
            Task? execution = null; Exception? failure = null;
            try
            {
                if (ownership.JournalFailure is { } journalFailure) throw journalFailure;
                execution = CheckCase(observation, optionsType, factoryType, assertions, evidence, deadline, ownership);
                await execution.WaitAsync(deadline.Token);
            }
            catch (Exception error)
            {
                failure = Unwrap(error);
            }
            finally
            {
                if (execution is not null)
                {
                    if (!execution.IsCompleted)
                    { var cancellationFailure = ownership.CancelOwner(deadline, "original-genuine-case-execution"); failure ??= cancellationFailure; }
                    var actualFailure = await ownership.JoinAsync(execution, "original-genuine-case-execution");
                    if (actualFailure is not null) failure = Unwrap(actualFailure);
                }
            }
            if (ownership.StopLaterCases)
                failure ??= ownership.JournalFailure ?? new TimeoutException("Original case joined after diagnostic deadline; case remains nonpassing.");
            await ownership.ReportAsync("case-settled", "genuine-source-case", failure is null ? "OBSERVATIONS COMPARED" : "FAILED",
                incomplete: false, failure?.ToString());
            failure ??= ownership.JournalFailure;
            if (failure is null)
                cases.Add(new { id, status = "OBSERVATIONS COMPARED; FULL SOURCE API STILL OPEN", assertions,
                    completeNativeObservations = evidence.Records(), sourceExpectation = observation.Clone(), ownership = ownership.Snapshot(),
                    unexecuted = evidence.Unexecuted(RequiredAssertions), incomplete = evidence.Incomplete(RequiredAssertions) });
            else
            {
                failed++; var error = failure;
                cases.Add(new { id, status = "FAILED OR UNSUPPORTED BOUNDARY; RETAIN COMPLETE ACTUALS", failureType = error.GetType().FullName,
                    error.Message, error.StackTrace, mismatch = error is ComparisonFailure difference ? new { difference.Actual, difference.Expected } : null,
                    assertions, completeNativeObservations = evidence.Records(), sourceExpectation = observation.Clone(),
                    unexecuted = evidence.Unexecuted(RequiredAssertions), incomplete = evidence.Incomplete(RequiredAssertions), evidence.HasUnjoinedOwner,
                    ownership = ownership.Snapshot(), ownership.DiagnosticDeadlineExceeded });
            }
            genuineOwnershipDeadline |= ownership.DiagnosticDeadlineExceeded;
            genuineJournalFailure |= ownership.JournalFailure is not null;
            stopBatch = evidence.HasUnjoinedOwner || ownership.StopLaterCases;
        }
        var repairControls = await SimpleRepairRegressionCases.RunAsync(
            JsonData.FromElement(fixture.RootElement.GetProperty("observations")[0].GetProperty("model").GetProperty("value")),
            repairJournal.WriteAsync, stopBatch, genuineOwnershipDeadline, genuineJournalFailure);
        var result = new { status = "NATIVE TARGETED CONSUMER; NO PACKAGE/PHASE ACCEPTANCE", fixture = args[0], fixtureSha256 = actualFixtureSha,
            nativeSourceApiQualificationOpen = true, originalSourceThinkingLevelMetadataQualificationOpen = true, all378StrictFindingsPreserved = true,
            all79ScopesRetained = true, allEightPhaseGates = "OPEN", stoppedForUnjoinedOwner = repairControls.StoppedForUnjoinedOwner,
            executedAssemblies = new[] { AssemblyPin(assembly), AssemblyPin(typeof(ChatRequest).Assembly), AssemblyPin(typeof(Program).Assembly) }, failed, cases,
            authoredRepairControls = repairControls, repairProgressPath,
            stoppedForOwnershipDeadline = genuineOwnershipDeadline || repairControls.StoppedForOwnershipDeadline,
            stoppedForProgressJournalFailure = genuineJournalFailure || repairControls.StoppedForProgressJournalFailure,
            stoppedForOwnershipOrJournalFailure = stopBatch || repairControls.StoppedForOwnershipDeadline || repairControls.StoppedForProgressJournalFailure };
        await WriteReport(args[1], result);
        Console.WriteLine(JsonSerializer.Serialize(new { cases = cases.Count, failed, fullSourceApiQualificationOpen = true }));
        return failed == 0 && repairControls.Failed == 0 && !stopBatch && !repairControls.StoppedForUnjoinedOwner &&
            !repairControls.StoppedForOwnershipDeadline && !repairControls.StoppedForProgressJournalFailure ? 0 : 1;
    }

    private static async Task CheckCase(JsonElement expected, Type optionsType, Type factoryType, List<object> assertions, TestEvidence evidence,
        CancellationTokenSource deadlineOwner, SimpleRepairOwnership ownership)
    {
        var deadline = deadlineOwner.Token;
        var profile = expected.GetProperty("case"); var modelValue = expected.GetProperty("model").GetProperty("value");
        evidence.Record("admitted-case-input", new { expected = expected.Clone(), profile = profile.Clone(), model = modelValue.Clone() });
        var model = new ModelDescriptor(Text(modelValue, "id")!, Text(modelValue, "api")!, Text(modelValue, "provider")!);
        var sourceOptions = profile.TryGetProperty("options", out var input) ? input : default;
        var simple = Activator.CreateInstance(optionsType, [JsonData.FromElement(modelValue), Text(sourceOptions, "reasoning")])!;
        var auth = Text(profile, "auth");
        var headers = auth switch
        {
            "authorization-header" => JsonData.Parse("{\"Authorization\":\"Bearer authored-inert-header-key\"}"),
            "cloudflare-header" => JsonData.Parse("{\"cf-aig-authorization\":\"Bearer authored-inert-gateway-key\"}"),
            "empty-header" => JsonData.Parse("{\"Authorization\":\"   \"}"),
            "missing" => JsonData.EmptyObject,
            _ => Json(sourceOptions, "headers")
        };
        var direct = new CompletionsKeyAuthRequestOptions(MaxTokens: Number(sourceOptions, "maxTokens"), Temperature: Number(sourceOptions, "temperature"),
            CacheRetention: Text(sourceOptions, "cacheRetention") == "long" ? CompletionsCacheRetention.Long : CompletionsCacheRetention.None,
            SessionId: Text(sourceOptions, "sessionId"), ToolChoice: Json(sourceOptions, "toolChoice"), Headers: headers)
        { SamplingParams = Json(sourceOptions, "samplingParams"), ThinkingBudgets = Json(sourceOptions, "thinkingBudgets") };
        Set(simple, "ApiKey", auth is null ? "authored-inert-simple-key" : null); Set(simple, "DirectOptions", direct);
        MapSourceFields(simple, profile, sourceOptions);
        var ledger = new List<string>(); var payloads = new List<CompletionsPayloadObservation>();
        var responses = new List<CompletionsResponseObservation>(); var providers = new List<CompletionsSourceSnapshot>();
        var callbackModels = new List<ModelDescriptor>(); var nativeBodies = new List<string>();
        var hook = Text(profile, "hook"); var sourceTurns = expected.GetProperty("turns"); var currentTurn = 0; var publicationCount = 0;
        HeldCallbackBarrier? barrier = null;
        async ValueTask Gate(string name, CancellationToken token)
        { ledger.Add(name + "-await-enter"); await barrier!.BlockAsync(token); ledger.Add(name + "-await-release"); }
        var hooks = new CompletionsLifecycleHooks
        {
            OnPayload = async (payload, selected, token) =>
            {
                callbackModels.Add(selected); payloads.Add(payload); ledger.Add("payload");
                evidence.Model("payload-model", selected); evidence.Value("payload-value", payload.Value, payload.OwnUndefinedPaths);
                if (hook == "payload-throw") throw PublicFailure("Authored inert payload callback failure");
                if (hook is "payload-async-undefined" or "payload-async-replacement") await Gate("payload", token);
                if (hook != "payload-async-replacement") return null;
                var replacement = JsonNode.Parse(payload.Value.ToString())!.AsObject();
                replacement["max_tokens"] = 17; replacement["authored_replacement"] = true; replacement.Remove("stream_options");
                var owned = JsonData.Parse(replacement.ToJsonString()); evidence.Value("payload-replacement", owned, []); return owned;
            },
            OnResponse = async (response, selected, token) =>
            {
                callbackModels.Add(selected); responses.Add(response); ledger.Add("response");
                evidence.Model("response-model", selected); evidence.Record("response-value", new { response.Status, headers = response.Headers.ToString() });
                if (hook == "response-throw") throw PublicFailure("Authored inert response callback failure");
                if (hook == "response-async-await") await Gate("response", token);
            },
            OnProviderStreamEventSnapshot = async (value, selected, token) =>
            {
                callbackModels.Add(selected); providers.Add(value); ledger.Add("provider-event");
                evidence.Model("provider-model", selected); evidence.Snapshot("provider-value", value);
                if (hook == "provider-throw") throw PublicFailure("Authored inert provider callback failure");
                if (hook == "provider-async-await" && providers.Count == 1) await Gate("provider", token);
            },
            OnSourcePublished = publication => { Interlocked.Increment(ref publicationCount); evidence.Snapshot("actual-producer-publication", publication.Emission); }
        };
        Set(simple, "HttpOptions", new CompletionsHttpSseOptions { Hooks = hooks, Retry = new CompletionsRetryOptions(0, 3) });
        using var handler = new OfflineHandler(async request =>
        {
            ledger.Add("http-fetch"); var bytes = await request.Content!.ReadAsByteArrayAsync(deadline);
            nativeBodies.Add(Encoding.UTF8.GetString(bytes));
            evidence.Record("actual-http-request", new { turn = currentTurn, uri = request.RequestUri?.ToString(), method = request.Method.Method,
                headers = request.Headers.Select(h => new { h.Key, values = h.Value.ToArray() }).ToArray(),
                contentHeaders = request.Content.Headers.Select(h => new { h.Key, values = h.Value.ToArray() }).ToArray(),
                rawBodyUtf8Base64 = Convert.ToBase64String(bytes), rawBody = nativeBodies[^1] });
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(sourceTurns[currentTurn].GetProperty("wire").GetString()!));
            content.Headers.TryAddWithoutValidation("content-type", "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var factory = Activator.CreateInstance(factoryType, [client, new Uri("https://simple-capture.invalid/v1/chat/completions"), model, simple])!;
        var initial = expected.GetProperty("initialContext").GetProperty("value").GetProperty("messages");
        var history = initial.EnumerateArray().Select(m => new TranscriptEntry(Text(m, "role")!, JsonData.FromElement(m))).ToImmutableArray();
        for (currentTurn = 0; currentTurn < sourceTurns.GetArrayLength(); currentTurn++)
        {
            var sourceTurn = sourceTurns[currentTurn]; var request = new ChatRequest(model, history, 123);
            evidence.Record("native-turn-input", new { turn = currentTurn, transcript = history.Select(h => h.WireBody.Value.Clone()).ToArray() });
            var gateName = hook switch { "payload-async-undefined" or "payload-async-replacement" => "payload", "response-async-await" => "response", "provider-async-await" => "provider", _ => null };
            barrier = gateName is null ? null : new HeldCallbackBarrier(evidence, gateName);
            if (profile.TryGetProperty("expectedSynchronousError", out var rejected) && rejected.GetBoolean())
            {
                evidence.Started("simple admission");
                try
                {
                    var unexpected = ((ValueTask<CompletionsRun>)Invoke(factory, "StartAsync", request, deadline)!).AsTask();
                    evidence.Record("unexpected-auth-start-admitted", new { status = unexpected.Status.ToString() });
                    _ = ownership.CancelOwner(deadlineOwner, "unexpected-auth-start");
                    CompletionsRun? unexpectedRun = null;
                    var unexpectedFailure = await ownership.JoinAsync(unexpected, "unexpected-auth-original-start");
                    if (unexpected.IsCompletedSuccessfully) unexpectedRun = unexpected.Result;
                    if (unexpectedFailure is not null)
                        evidence.Record("unexpected-auth-start-join-failure", new { type = unexpectedFailure.GetType().FullName, unexpectedFailure.Message });
                    if (unexpectedRun is not null)
                    {
                        var disposalFailure = await JoinRun(ownership, evidence, unexpectedRun, "unexpected-auth");
                        if (disposalFailure is not null)
                            evidence.Record("unexpected-auth-disposal-failure", new { type = disposalFailure.GetType().FullName, disposalFailure.Message });
                    }
                    throw new Exception("Auth rejection was not synchronous.");
                }
                catch (TargetInvocationException error) when (error.InnerException is CompletionsRequestException { Failure: CompletionsRequestFailure.InvalidKey } failure)
                { evidence.Record("actual-synchronous-auth-failure", new { type = failure.GetType().FullName, failure.Message, failure.Failure, sends = nativeBodies.Count, callbacks = ledger.ToArray() }); }
                Require(nativeBodies.Count == 0 && ledger.Count == 0, "auth rejection effects");
                evidence.Completed("simple admission");
                assertions.Add(new { assertion = "simple admission", matched = true, laterAssertionsUnexecutedByDesign = true }); return;
            }
            var resolved = Invoke(factory, "Resolve", request)!;
            evidence.Record("actual-simple-resolution", JsonSerializer.SerializeToElement(resolved));
            var sourceHelper = sourceTurn.GetProperty("independentHelperProbe");
            evidence.Started("context estimate/cap");
            Compare(JsonSerializer.SerializeToElement(Get(resolved, "ContextEstimate")), sourceHelper.GetProperty("contextEstimate").GetProperty("value"), "context estimate", camelCase: true);
            Equal(Convert.ToDouble(Get(resolved, "MaxTokens")), sourceHelper.GetProperty("baseOptions").GetProperty("value").GetProperty("maxTokens").GetDouble(), "context cap");
            evidence.Completed("context estimate/cap"); evidence.Started("supported/clamped level");
            var supported = (IEnumerable<string>)Invoke(factory, "GetSupportedThinkingLevels")!;
            Equal(supported.ToArray(), sourceHelper.GetProperty("supportedLevels").EnumerateArray().Select(x => x.GetString()!).ToArray(), "supported levels");
            var clamped = sourceHelper.GetProperty("clampedRequested");
            Equal((string?)Get(resolved, "ResolvedThinkingLevel"), clamped.ValueKind == JsonValueKind.Null ? null : clamped.GetString(), "clamped level");
            evidence.Completed("supported/clamped level");
            assertions.Add(new { assertion = "supported/clamped level", matched = true }); assertions.Add(new { assertion = "context estimate/cap", matched = true });
            var callbacksBefore = providers.Count; var bodiesBefore = nativeBodies.Count;
            var payloadsBefore = payloads.Count; var responsesBefore = responses.Count;
            evidence.Started("simple admission");
            var start = ((ValueTask<CompletionsRun>)Invoke(factory, "StartAsync", request, deadline)!).AsTask();
            CompletionsRun? run = null; Exception? executionFailure = null;
            try
            {
            if (barrier is not null)
            {
                await barrier.Entered.WaitAsync(deadline);
                Require(!barrier.IsReleased, "callback genuinely held by external controller");
                var heldRun = await start.WaitAsync(deadline) ?? throw new InvalidOperationException("StartAsync returned no held run.");
                run = heldRun;
                evidence.Record("held-controller-checkpoint", new { gateName, startStatus = start.Status.ToString(),
                    sends = nativeBodies.Count - bodiesBefore, sourceResultCompleted = heldRun.SourceResult.IsCompleted,
                    cleanupCompleted = heldRun.CleanupCompletion.IsCompleted, canonicalCompleted = heldRun.CanonicalCompletion.IsCompleted,
                    producerPublications = Volatile.Read(ref publicationCount), providerCallbacks = providers.Count - callbacksBefore });
                if (gateName == "payload") Require(nativeBodies.Count == bodiesBefore && providers.Count == callbacksBefore && !heldRun.SourceResult.IsCompleted, "held payload prohibits send/provider/result settlement");
                if (gateName == "response") Require(nativeBodies.Count == bodiesBefore + 1 && providers.Count == callbacksBefore && !heldRun.SourceResult.IsCompleted, "held response prohibits provider/result settlement");
                if (gateName == "provider") Require(!heldRun.SourceResult.IsCompleted && !heldRun.CanonicalCompletion.IsCompleted, "held provider prohibits result/commit settlement");
                Require(!heldRun.CleanupCompletion.IsCompleted && !heldRun.CanonicalCompletion.IsCompleted, "held callback prohibits joined cleanup/canonical settlement");
                barrier.Release();
            }
            run ??= await start.WaitAsync(deadline);
            evidence.Completed("simple admission");
            var delivered = new List<CompletionsSourceEvent>(); var deliveryValues = new List<CompletionsSourceSnapshot>();
            await foreach (var item in run.ReadSourceEventsAsync(deadline))
            { delivered.Add(item); deliveryValues.Add(item.Snapshot); evidence.Snapshot("actual-delivery", item.Snapshot); evidence.Record("delivery-identities", new { eventIdentity = evidence.Identity(item), messageIdentity = evidence.Identity(item.Message) }); }
            var result = await run.SourceResult.WaitAsync(deadline); evidence.Snapshot("actual-public-result", result.Snapshot);
            var cleanup = await run.CleanupCompletion.WaitAsync(deadline); evidence.Record("actual-cleanup-outcome", cleanup);
            var canonical = await run.CanonicalCompletion.WaitAsync(deadline); evidence.Record("actual-canonical-result", new { message = PiWireJson.WriteMessage(canonical.Message).Value.Clone(), canonical.Failure });
            foreach (var snapshot in run.SourceEmissions) evidence.Snapshot("all-producer-emissions", snapshot);
            foreach (var item in delivered) evidence.Snapshot("actual-held-after-terminal", item.Snapshot);
            evidence.Started("actual cleanup/canonical completion");
            Require(cleanup.Succeeded, "real cleanup join"); ledger.Add("cleanup-joined"); evidence.Completed("actual cleanup/canonical completion");
            var expectedRequests = sourceTurn.GetProperty("requests");
            evidence.Started("whole payload/body");
            Require(nativeBodies.Count - bodiesBefore == expectedRequests.GetArrayLength(), "actual request count");
            for (var i = 0; i < expectedRequests.GetArrayLength(); i++)
            {
                Compare(JsonData.Parse(nativeBodies[bodiesBefore + i]).Value, expectedRequests[i].GetProperty("bodyJson"), "whole request body");
                Equal(nativeBodies[bodiesBefore + i], expectedRequests[i].GetProperty("body").GetString(), "whole actual request UTF-8 text");
                Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeBodies[bodiesBefore + i]))).ToLowerInvariant(),
                    expectedRequests[i].GetProperty("bodyUtf8Sha256").GetString(), "whole actual request UTF-8 SHA-256");
            }
            var sourceCallbacks = sourceTurn.GetProperty("callbacks").EnumerateArray().ToArray();
            var expectedPayloads = sourceCallbacks.Where(c => Text(c, "kind") == "payload").ToArray();
            Require(payloads.Count - payloadsBefore == expectedPayloads.Length, "payload callback count");
            for (var i = 0; i < expectedPayloads.Length; i++)
                CheckObservedValue(payloads[payloadsBefore + i].Value, payloads[payloadsBefore + i].OwnUndefinedPaths, expectedPayloads[i].GetProperty("payload"), "whole payload callback");
            evidence.Completed("whole payload/body"); evidence.Started("whole provider callbacks");
            var expectedResponses = sourceCallbacks.Where(c => Text(c, "kind") == "response").ToArray();
            Require(responses.Count - responsesBefore == expectedResponses.Length, "response callback count");
            for (var i = 0; i < expectedResponses.Length; i++)
            {
                var response = responses[responsesBefore + i];
                CheckObservedValue(JsonData.Parse("{\"status\":" + response.Status + ",\"headers\":" + response.Headers + "}"), [], expectedResponses[i].GetProperty("response"), "whole response callback");
            }
            var expectedProviders = sourceTurn.GetProperty("providerEvents");
            Require(providers.Count - callbacksBefore == expectedProviders.GetArrayLength(), "provider callback count");
            for (var i = 0; i < expectedProviders.GetArrayLength(); i++) CheckSnapshot(providers[callbacksBefore + i], expectedProviders[i], "whole provider event");
            evidence.Completed("whole provider callbacks"); evidence.Started("public final/ownUndefined/binary64");
            CheckSnapshot(result.Snapshot, sourceTurn.GetProperty("result"), "whole public final");
            evidence.Completed("public final/ownUndefined/binary64"); evidence.Started("all push/delivery/held live values");
            var expectedEmissions = sourceTurn.GetProperty("emissions"); Require(run.SourceEmissions.Length == expectedEmissions.GetArrayLength(), "push count");
            for (var i = 0; i < expectedEmissions.GetArrayLength(); i++) CheckSnapshot(run.SourceEmissions[i], expectedEmissions[i], "push snapshot");
            var expectedDelivered = sourceTurn.GetProperty("delivered"); Require(deliveryValues.Count == expectedDelivered.GetArrayLength(), "delivery count");
            for (var i = 0; i < expectedDelivered.GetArrayLength(); i++) CheckSnapshot(deliveryValues[i], expectedDelivered[i], "delivery snapshot");
            foreach (var item in delivered) Require(ReferenceEquals(item.Message, result), "actual shared live message handle");
            foreach (var modelArgument in callbackModels) Require(ReferenceEquals(modelArgument, model), "actual callback descriptor reference");
            foreach (var held in sourceTurn.GetProperty("heldAfterTerminal").EnumerateArray().Where(h => h.GetProperty("origin").GetProperty("phase").GetString() == "delivery"))
                CheckSnapshot(delivered[held.GetProperty("origin").GetProperty("ordinal").GetInt32()].Snapshot, held.GetProperty("event"), "held live snapshot");
            evidence.Completed("all push/delivery/held live values");
            assertions.Add(new { assertion = "whole payload/body", matched = true, actualPayloadObservations = payloads.Count, actualResponseObservations = responses.Count });
            assertions.Add(new { assertion = "whole provider callbacks", matched = true, sourceModelFullShapeOpen = true, nativeCallbackModelShape = "existing ModelDescriptor" });
            assertions.Add(new { assertion = "public final/ownUndefined/binary64", matched = true }); assertions.Add(new { assertion = "all push/delivery/held live values", matched = true, nestedRawObjectIdentityApiOpen = true });
            assertions.Add(new { assertion = "actual cleanup/canonical completion", matched = true });
            if (currentTurn + 1 < sourceTurns.GetArrayLength())
            {
                evidence.Started("actual continuation");
                Require(canonical.Message.StopReason == StopReason.ToolUse, "actual first canonical tool final");
                history = history.Add(new TranscriptEntry("assistant", PiWireJson.WriteMessage(canonical.Message))); ledger.Add("assistant-commit");
                var call = canonical.Message.Content.OfType<ToolCallContent>().Single(); Require(call.Name == "inspect" && call.Arguments.Value.GetProperty("value").GetInt32() == 7, "actual validated tool call");
                ledger.Add("tool-effect");
                var resultBody = new JsonObject { ["role"] = "toolResult", ["toolCallId"] = call.Id, ["toolName"] = call.Name,
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Observed seven." }), ["details"] = new JsonObject { ["opaque"] = null }, ["isError"] = false, ["timestamp"] = 123 };
                history = history.Add(new TranscriptEntry("toolResult", JsonData.Parse(resultBody.ToJsonString()))); ledger.Add("tool-result-commit");
                evidence.Record("actual-native-continuation", new { order = ledger.ToArray(), transcript = history.Select(h => h.WireBody.Value.Clone()).ToArray() });
                evidence.Completed("actual continuation");
                assertions.Add(new { assertion = "actual continuation", matched = true, sourceTranscriptSeeded = false, order = ledger.ToArray() });
            }
            }
            catch (Exception error) { executionFailure = error; evidence.Record("actual-execution-failure", new { type = error.GetType().FullName, error.Message, error.StackTrace }); throw; }
            finally
            {
                if (executionFailure is not null)
                    try { deadlineOwner.Cancel(); } catch (Exception error) { evidence.Record("owned-cancellation-failure", new { type = error.GetType().FullName, error.Message }); }
                barrier?.Release();
                if (run is null)
                {
                    var startFailure = await ownership.JoinAsync(start, "owned-original-start");
                    if (start.IsCompletedSuccessfully) run = start.Result;
                    if (startFailure is not null)
                    {
                        evidence.Record("owned-start-join-failure", new { type = startFailure.GetType().FullName, startFailure.Message, status = start.Status.ToString() });
                        if (executionFailure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startFailure).Throw();
                    }
                }
                if (run is not null)
                {
                    evidence.Record("owner-before-disposal", new { source = run.SourceResult.Status.ToString(), cleanup = run.CleanupCompletion.Status.ToString(), canonical = run.CanonicalCompletion.Status.ToString() });
                    var disposalFailure = await JoinRun(ownership, evidence, run, "owned");
                    if (disposalFailure is null) evidence.Record("owned-disposal-settled", true);
                    else
                    {
                        evidence.Record("owned-disposal-failure", new { type = disposalFailure.GetType().FullName, disposalFailure.Message, unjoinedOwner = false });
                        if (executionFailure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalFailure).Throw();
                    }
                }
                else evidence.Record("start-without-received-run", new { status = start.Status.ToString(), unjoinedStart = !start.IsCompleted });
            }
        }
    }

    private static void MapSourceFields(object options, JsonElement profile, JsonElement input)
    {
        if (Number(input, "timeoutMs") is { } timeout) Set(options, "TimeoutMilliseconds", checked((int)timeout));
        if (Json(input, "metadata") is { } metadata) Set(options, "Metadata", metadata);
        switch (Text(profile, "runtimeField"))
        {
            case "signal": break; // CancellationToken is passed through the actual start/stream seam; fault cases remain separate.
            case "transport": Set(options, "TransportPreference", "sse"); break;
            case "telemetryContext": Set(options, "TelemetryContext", JsonData.Parse("{\"authoredInertOpaqueContext\":true}")); break;
            case "websocketConnectTimeoutMs": Set(options, "WebsocketConnectTimeoutMilliseconds", 4321); break;
            case "env": Set(options, "Environment", JsonData.Parse("{\"PI_CACHE_RETENTION\":\"none\",\"HTTP_PROXY\":\"\",\"HTTPS_PROXY\":\"\",\"ALL_PROXY\":\"\"}")); break;
        }
    }
    private static void CheckSnapshot(CompletionsSourceSnapshot native, JsonElement expected, string criterion)
    {
        CheckObservedValue(JsonData.FromElement(native.Raw.Value.GetProperty("value")), native.OwnUndefinedPaths, expected, criterion);
        Equal(native.SerializedJson, expected.GetProperty("serializedJson").GetString(), criterion + " ECMAScript bytes");
    }
    private static void CheckObservedValue(JsonData native, ImmutableArray<string> undefined, JsonElement expected, string criterion)
    {
        Compare(native.Value, expected.GetProperty("value"), criterion);
        Equal(undefined.ToArray(), expected.GetProperty("ownUndefinedPaths").EnumerateArray().Select(p => p.GetString()!).ToArray(), criterion + " ownUndefined");
        Equal(EcmaScriptJsonProjection.Project(native), expected.GetProperty("serializedJson").GetString(), criterion + " ECMAScript bytes");
        var bits = new List<(string Path, string Hex)>();
        void Scan(JsonElement v, string p)
        {
            if (v.ValueKind == JsonValueKind.Number) bits.Add((p, unchecked((ulong)BitConverter.DoubleToInt64Bits(v.GetDouble())).ToString("x16")));
            else if (v.ValueKind == JsonValueKind.Object) foreach (var x in v.EnumerateObject()) Scan(x.Value, p + "/" + x.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            else if (v.ValueKind == JsonValueKind.Array) { var i = 0; foreach (var x in v.EnumerateArray()) Scan(x, p + "/" + i++); }
        }
        Scan(native.Value, "");
        Equal(bits.Select(x => new { path = x.Path, hex = x.Hex }).ToArray(),
            expected.GetProperty("numberBits").EnumerateArray().Select(x => new { path = Text(x, "path"), hex = Text(x, "hex") }).ToArray(), criterion + " binary64");
    }
    private static void Compare(JsonElement actual, JsonElement expected, string criterion, bool camelCase = false)
    {
        var value = actual.GetRawText();
        if (camelCase) { var node = JsonNode.Parse(value)!.AsObject(); value = new JsonObject(node.Select(x => new KeyValuePair<string, JsonNode?>(char.ToLowerInvariant(x.Key[0]) + x.Key[1..], x.Value?.DeepClone()))).ToJsonString(); }
        if (!JsonNode.DeepEquals(JsonNode.Parse(value), JsonNode.Parse(expected.GetRawText())))
            throw new ComparisonFailure(criterion, JsonNode.Parse(value), JsonNode.Parse(expected.GetRawText()));
    }
    private static void AdmitFixture(JsonElement fixture)
    {
        Require(Text(fixture, "sourceSha") == "d86654abb8862e201933517d6f1fce9f88dd117f", "pinned Source identity");
        Require(Text(fixture, "acceptedNativeBase") == "0671c335007ae03246ee044f460d93069e2a6356", "pinned accepted native base");
        Require(Text(fixture.GetProperty("node"), "sha256") == "3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237", "pinned Node identity");
        var observations = fixture.GetProperty("observations");
        Require(observations.GetArrayLength() == 69 && observations.EnumerateArray().Select(o => Text(o, "id")).Distinct(StringComparer.Ordinal).Count() == 69, "exact nonempty unique case set");
        var counts = fixture.GetProperty("executionCounts");
        Require(counts.GetProperty("streamSimpleInvocations").GetInt32() == 71 && counts.GetProperty("synchronousAuthRejections").GetInt32() == 2 && counts.GetProperty("actualHttpAttempts").GetInt32() == 68, "pinned Source execution counts");
        Require(fixture.GetProperty("prohibitedNetworkCalls").GetInt32() == 0 && fixture.GetProperty("nativeProductExecutions").GetInt32() == 0, "offline Source-only fixture");
    }
    private static async Task WriteReport(string path, object report)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, ReportJson);
    }
    private static void Equal(object? actual, object? expected, string criterion)
        => Compare(JsonSerializer.SerializeToElement(actual), JsonSerializer.SerializeToElement(expected), criterion);
    private static object AssemblyPin(Assembly assembly)
    {
        var bytes = File.ReadAllBytes(assembly.Location);
        return new { path = assembly.Location, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion };
    }
    private static void RecordSettledRun(TestEvidence evidence, CompletionsRun run)
    {
        evidence.Record("settled-owner-statuses", new { source = run.SourceResult.Status.ToString(), cleanup = run.CleanupCompletion.Status.ToString(), canonical = run.CanonicalCompletion.Status.ToString() });
        if (run.SourceResult.IsCompletedSuccessfully) evidence.Snapshot("settled-owner-public-result", run.SourceResult.Result.Snapshot);
        else if (run.SourceResult.Exception is { } sourceFailure) evidence.Record("settled-owner-source-failure", sourceFailure.ToString());
        if (run.CleanupCompletion.IsCompletedSuccessfully) evidence.Record("settled-owner-cleanup", run.CleanupCompletion.Result);
        else if (run.CleanupCompletion.Exception is { } cleanupFailure) evidence.Record("settled-owner-cleanup-failure", cleanupFailure.ToString());
        if (run.CanonicalCompletion.IsCompletedSuccessfully)
            evidence.Record("settled-owner-canonical", new { message = PiWireJson.WriteMessage(run.CanonicalCompletion.Result.Message).Value.Clone(), run.CanonicalCompletion.Result.Failure });
        else if (run.CanonicalCompletion.Exception is { } canonicalFailure) evidence.Record("settled-owner-canonical-failure", canonicalFailure.ToString());
        foreach (var emission in run.SourceEmissions) evidence.Snapshot("settled-owner-emission", emission);
    }
    private static async Task<Exception?> JoinRun(SimpleRepairOwnership ownership, TestEvidence evidence, CompletionsRun run, string operation)
    {
        var disposal = run.DisposeAsync().AsTask();
        var failure = await ownership.JoinAsync(disposal, operation + "-original-disposal");
        var sourceFailure = await ownership.JoinAsync(run.SourceResult, operation + "-original-source-result");
        var cleanupFailure = await ownership.JoinAsync(run.CleanupCompletion, operation + "-original-cleanup-completion");
        var canonicalFailure = await ownership.JoinAsync(run.CanonicalCompletion, operation + "-original-canonical-completion");
        RecordSettledRun(evidence, run);
        return failure ?? sourceFailure ?? cleanupFailure ?? canonicalFailure;
    }
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name)!.Invoke(target, args);
    private static object? Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var x) && x.ValueKind != JsonValueKind.Null ? x.GetString() : null;
    private static double? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var x) && x.ValueKind != JsonValueKind.Null ? x.GetDouble() : null;
    private static JsonData? Json(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var x) ? JsonData.FromElement(x) : null;
    private static void Require(bool condition, string criterion) { if (!condition) throw new Exception(criterion); }
    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? inner : error;
    private static CompletionsPublicFailureException PublicFailure(string message) => new(new CompletionsPublicFailure(message));
    private sealed class OfflineHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
