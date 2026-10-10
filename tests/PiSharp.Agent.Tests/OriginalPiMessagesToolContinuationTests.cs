using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

/// <summary>Original frozen case through real Agent and ToolInvoker; authored evidence, not acceptance.</summary>
internal static class OriginalPiMessagesToolContinuationTests
{
    private const string FixtureHash = "1b8a6fbb80726e6f1a6cb1848d2df9b7da41c9015d91592d9f077f2aa8c04891";
    public static object? Evidence { get; private set; }
    public static object? FailureEvidence { get; private set; }
    public static object? ControlledFailureEvidence { get; private set; }
    private static bool failureOriginalJoined, failureEarlyAssertionsPassed, failureBeforeSecondCleanup;
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("pi-messages-agent.original-pm-second-request-failure-retains-root", SecondRequestFailure);
        yield return ("pi-messages-agent.original-pm-native-tool-continuation", () => OriginalContinuation());
    }

    private static async Task SecondRequestFailure()
    {
        var injected = new InvalidOperationException("injected original second-request comparison failure");
        var caught = false;
        try { await OriginalContinuation(injected); }
        catch (Exception actual) when (ReferenceEquals(actual, injected)) { caught = true; }
        Check(caught && failureOriginalJoined && failureEarlyAssertionsPassed && failureBeforeSecondCleanup && FailureEvidence is not null && Evidence is null,
            "Second-request failure lost original identity, joined receipt or null complete evidence.");
        ControlledFailureEvidence = new { status = "EXPECTED_INJECTED_FAILURE_CONTROL", intentionalControl = true,
            runtimeAcceptance = false, actual = FailureEvidence };
    }

    private static async Task OriginalContinuation(Exception? injectedSecondFailure = null)
    {
        Evidence = null; FailureEvidence = null; failureOriginalJoined = false; failureEarlyAssertionsPassed = false; failureBeforeSecondCleanup = false;
        var firstCleanupChecked = false; var assistantChecked = false; var toolResultChecked = false;
        ExceptionDispatchInfo? comparisonFailure = null; AssistantMessage? errorAssistant = null;
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-messages-authored-cases-r2.json"));
        Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == FixtureHash, "Frozen fixture hash differs.");
        using var fixture = JsonDocument.Parse(bytes);
        var row = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "PM-NATIVE-TOOL-CONTINUATION");
        var input = row.GetProperty("input"); var expected = row.GetProperty("expected");
        var metadata = input.GetProperty("model");
        var model = new ModelDescriptor(metadata.GetProperty("id").GetString()!, "pi-messages", metadata.GetProperty("provider").GetString()!);
        var frozenInputs = input.GetProperty("context").GetProperty("messages").EnumerateArray()
            .Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
        // Native admission needs content; the frozen provider envelope uses systemMessage.
        Check(frozenInputs.Length == 2 && frozenInputs[0].Role == "system" && frozenInputs[1].Role == "user", "Input seeded a continuation.");
        var originals = frozenInputs.SetItem(0, MapSystemEnvelope(frozenInputs[0], "systemMessage", "content"));
        AgentLoopRunner.ValidateRequestMessages(originals);
        SameJson(frozenInputs[0].WireBody.Value, MapSystemEnvelope(originals[0], "content", "systemMessage").WireBody.Value);
        Check(ReferenceEquals(originals[1], frozenInputs[1]), "User input changed during mapping.");
        var first = new OwnedBody(Wire(input.GetProperty("events")));
        var second = new OwnedBody(Wire(input.GetProperty("secondTurn").GetProperty("events")));
        var owners = new List<(RequestContent Request, Response Response, BodyContent Content)>();
        var requests = new List<object>();
        var assistantEntered = Gate(); var releaseAssistant = Gate();
        var resultEntered = Gate(); var releaseResult = Gate();
        var finalEntered = Gate(); var releaseFinal = Gate();
        var adapter = new Adapter(); var policy = new Policy(adapter);
        var sends = 0; var terminalEvents = 0;
        AssistantMessage? assistant = null; ToolResultMessage? toolResult = null; AssistantMessage? final = null;
        using var handler = new Handler(async (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var turn = sends++;
            Check(turn < 2, "Unexpected retry or third physical request.");
            if (turn == 1) Check(releaseResult.Task.IsCompleted && adapter.Effects == 1, "Continuation preceded real tool result barrier.");
            var originalContent = request.Content!;
            var before = Headers(request);
            var body = await originalContent.ReadAsByteArrayAsync(token);
            var after = Headers(request);
            var wanted = turn == 0 ? expected.GetProperty("request") : expected.GetProperty("continuation").GetProperty("secondRequest");
            // Retain the actual rejected request before any assertion; failure evidence is distinct from success.
            requests.Add(new { turn, uri = request.RequestUri!.AbsoluteUri, method = request.Method.Method,
                beforeBodyReadHeaders = before, nativeMaterializedHeaders = after, bodyUtf8Base64 = Convert.ToBase64String(body) });
            try
            {
                var physicalExpected = turn == 0 ? JsonData.FromElement(wanted) : AgentRequestExpectation(wanted);
                CompareRequest(physicalExpected.Value, request, before, after, body);
                if (turn == 1)
                {
                    using var physical = JsonDocument.Parse(body);
                    var messages = physical.RootElement.GetProperty("context").GetProperty("messages");
                    Check(messages.GetArrayLength() == 4 && assistant is not null && toolResult is not null, "Actual continuation messages missing.");
                    SameJson(PiWireJson.WriteMessage(assistant!).Value, messages[2]);
                    SameJson(ToolResultMessageMaterializer.ToTranscript(toolResult!, 123).WireBody.Value, messages[3]);
                    var providerLayer = RewriteContinuationBody(physical.RootElement, addStamp: false);
                    SameJson(wanted.GetProperty("body"), providerLayer.Value);
                    using var frozenBytes = JsonDocument.Parse(Convert.FromBase64String(wanted.GetProperty("bodyUtf8Base64").GetString()!));
                    SameJson(frozenBytes.RootElement, wanted.GetProperty("body"));
                    // The frozen provider-layer bytes list the assistant's fields in an authored order; the actual bytes are pi-ai's.
                    Check(providerLayer.ToString() == RewriteContinuationBody(wanted.GetProperty("body"), addStamp: false, frozen: true).ToString(),
                        "Complete projected provider-layer UTF8 bytes differ from frozen request.");
                    if (injectedSecondFailure is not null) throw injectedSecondFailure;
                }
            }
            catch (Exception error) { comparisonFailure = ExceptionDispatchInfo.Capture(error); throw; }
            var requestOwner = new RequestContent(originalContent); request.Content = requestOwner;
            var content = new BodyContent(turn == 0 ? first : second);
            content.Headers.ContentType = new("text/event-stream");
            var response = new Response((HttpStatusCode)input.GetProperty("httpStatus").GetInt32()) { Content = content };
            owners.Add((requestOwner, response, content)); return response;
        });
        using var client = new HttpClient(handler);
        var options = new PiMessagesOptions(JsonData.FromElement(metadata), input.GetProperty("options").GetProperty("apiKey").GetString())
        { EnvironmentLookup = _ => null };
        var transport = new FrozenSystemEnvelopeTransport(new PiMessagesHttpSseTransport(client, model, options), frozenInputs[0], originals[0]);
        var invoker = new ToolInvoker([adapter], policy, options: new ToolInvokerOptions
        { AllowedRootTools = ImmutableHashSet.Create(StringComparer.Ordinal, "inspect") });
        adapter.BeforePrepare = () => Check(releaseAssistant.Task.IsCompleted && first.Disposed && terminalEvents == 1 && sends == 1,
            "Preparation preceded awaited authoritative assistant or HTTP cleanup.");
        await using var agent = new NativeAgent(new(model, transport, [new("inspect", invoker)],
            Hooks: new(PrepareRequest: (snapshot, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript, 123));
            })), () => 123,
            new Sink(async (observation, _) =>
            {
                if (observation is TurnStreamObserved { Event: StreamDone }) terminalEvents++;
                if (observation is AssistantMessageEnded ended)
                {
                    if (ended.Message.StopReason is not (StopReason.ToolUse or StopReason.Stop))
                    {
                        errorAssistant = ended.Message;
                        comparisonFailure?.Throw();
                        throw new InvalidOperationException("Unexpected native continuation error assistant: " + PiWireJson.WriteMessage(ended.Message));
                    }
                    Check(owners.Count > 0 && owners[^1].Request.Disposed && owners[^1].Response.Disposed && owners[^1].Content.Disposed,
                        "Assistant settlement preceded HTTP owner disposal.");
                    if (ended.Message.StopReason == StopReason.ToolUse)
                    { assistant = ended.Message; assistantEntered.TrySetResult(); await releaseAssistant.Task; }
                    else
                    { final = ended.Message; finalEntered.TrySetResult(); await releaseFinal.Task; }
                }
                if (observation is ToolResultMessageEnded endedTool)
                { toolResult = endedTool.Message; resultEntered.TrySetResult(); await releaseResult.Task; }
            }), new(StreamCapacity: 1));
        Task? original = null; var completed = false;
        try
        {
            original = agent.PromptAsync(originals);
            await Enter(first.CleanupEntered, original);
            Check(!original.IsCompleted && !assistantEntered.Task.IsCompleted && adapter.Prepares == 0 && policy.Authorizations == 0 && adapter.Effects == 0 && sends == 1,
                "Held first HTTP cleanup leaked assistant or effect authority.");
            firstCleanupChecked = true; first.ReleaseCleanup.TrySetResult();
            await Enter(assistantEntered, original);
            Check(!original.IsCompleted && adapter.Prepares == 0 && policy.Authorizations == 0 && adapter.Effects == 0 && sends == 1,
                "Held assistant barrier leaked authorization or effect.");
            assistantChecked = true; releaseAssistant.TrySetResult();
            await Enter(resultEntered, original);
            Check(!original.IsCompleted && adapter.Prepares == 1 && adapter.Validations == 2 && policy.Authorizations == 1 && adapter.Effects == 1 && sends == 1,
                "Real tool effect count or held result barrier differs.");
            Check(toolResult is not null && !toolResult.HasDetails && !toolResult.IsError, "Real result presence/error differs.");
            SameJson(expected.GetProperty("continuation").GetProperty("toolResult"), ToolResultMessageMaterializer.ToTranscript(toolResult!, 123).WireBody.Value);
            toolResultChecked = true; releaseResult.TrySetResult();
            await Enter(second.CleanupEntered, original);
            Check(!original.IsCompleted && !finalEntered.Task.IsCompleted && sends == 2 && adapter.Effects == 1,
                "Held continuation HTTP cleanup leaked final settlement or repeated effect.");
            second.ReleaseCleanup.TrySetResult();
            await Enter(finalEntered, original);
            Check(!original.IsCompleted && terminalEvents == 2 && sends == 2 && adapter.Effects == 1, "Held final assistant did not own original task.");
            var secondFrames = expected.GetProperty("continuation").GetProperty("secondFrames");
            SameJson(StampAssistant(secondFrames[secondFrames.GetArrayLength() - 1].GetProperty("wire").GetProperty("message")).Value, PiWireJson.WriteMessage(final!).Value);
            releaseFinal.TrySetResult(); await original;
            Check(!agent.Snapshot.IsRunning && agent.Snapshot.Failure is null && agent.Snapshot.CompletedToolOutcomes.Length == 1,
                "Real Agent did not settle exactly one successful tool outcome.");
            Check(transport.Calls == 2, "Exactly two native-to-frozen request mappings required.");
            Check(first.Disposed && second.Disposed && owners.Count == 2 && owners.All(owner => owner.Request.Disposed && owner.Response.Disposed && owner.Content.Disposed), "Owned HTTP operations did not settle.");
            Check(ReferenceEquals(adapter.Prepared, policy.Authorized) && ReferenceEquals(policy.Authorized, adapter.Executed), "Authorized action identity changed.");
            completed = true;
        }
        finally
        {
            first.ReleaseCleanup.TrySetResult(); second.ReleaseCleanup.TrySetResult();
            releaseAssistant.TrySetResult(); releaseResult.TrySetResult(); releaseFinal.TrySetResult();
            agent.Abort();
            if (original is not null) { try { await original; } catch { /* Preserve the assertion/operation failure already propagating. */ } }
            await agent.WaitForIdleAsync();
            failureOriginalJoined = original?.IsCompleted == true;
            failureEarlyAssertionsPassed = firstCleanupChecked && assistantChecked && toolResultChecked;
            failureBeforeSecondCleanup = !second.CleanupEntered.Task.IsCompleted && !finalEntered.Task.IsCompleted;
            if (!completed) FailureEvidence = new { criterionStatus = "OPEN", status = "NONPASSING_JOINED",
                originalTaskJoined = failureOriginalJoined, comparisonFailure = comparisonFailure?.SourceException.ToString(),
                errorAssistant = errorAssistant is null ? null : (JsonElement?)PiWireJson.WriteMessage(errorAssistant).Value,
                firstCleanupAssertionPassed = firstCleanupChecked, assistantBarrierAssertionPassed = assistantChecked,
                toolResultAssertionPassed = toolResultChecked, firstCleanupSettled = first.Disposed, assistantBarrierReleased = releaseAssistant.Task.IsCompleted,
                toolResultBarrierEntered = resultEntered.Task.IsCompleted, toolResultBarrierReleased = releaseResult.Task.IsCompleted,
                secondCleanupEntered = second.CleanupEntered.Task.IsCompleted, finalAssistantEntered = finalEntered.Task.IsCompleted,
                actualEffectCount = adapter.Effects, physicalRequests = requests, completeEvidence = false, runtimeAcceptance = false };
            if (completed) Evidence = new { criterion = "PM-NATIVE-TOOL-CONTINUATION/3", criterionStatus = "OPEN",
                evidenceKind = "authored-native-integration", runtimeAcceptance = false, fixtureSha256 = FixtureHash,
                originalTaskJoined = original!.IsCompleted, actualEffectCount = adapter.Effects, finalizedArgument = adapter.Executed!.Arguments.Value.GetProperty("value").GetInt32(),
                preparations = adapter.Prepares, validations = adapter.Validations, authorizations = policy.Authorizations,
                nativeSystemInput = originals[0].WireBody.Value, mappedRequests = transport.Calls,
                mapping = "systemMessage-to-content-at-admission; content-to-systemMessage-only-at-provider-boundary",
                assistantExpectationLayer = "frozen provider assistant plus pinned Agent thinkingLevel=off; actual wire retains stamp",
                completePhysicalRequests = requests, awaitedBarriers = new[] { "first-http-cleanup", "assistant", "tool-result", "second-http-cleanup", "final-assistant" } };
        }
    }

    // A fixture boundary adapter, not a change to production validation or provider serialization.
    // Round-trip the actual native input; never seed an assistant or fabricate a tool result.
    private static TranscriptEntry MapSystemEnvelope(TranscriptEntry system, string from, string to)
    {
        var body = system.WireBody.Value;
        Check(system.Role == "system" && body.GetProperty("role").GetString() == "system" &&
            body.TryGetProperty(from, out var text) && text.ValueKind == JsonValueKind.String &&
            !body.TryGetProperty(to, out _) && body.GetProperty("timestamp").GetInt64() == 123,
            "System envelope cannot be mapped without losing semantics.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in body.EnumerateObject())
            { writer.WritePropertyName(field.Name == from ? to : field.Name); field.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return new("system", JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray())));
    }
    private sealed class FrozenSystemEnvelopeTransport(IChatTransport inner, TranscriptEntry frozenSystem,
        TranscriptEntry nativeSystem) : IChatTransport
    {
        public int Calls;
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Check(request.Messages.Length == (Calls == 0 ? 2 : 4) && Calls < 2 && request.Timestamp == 123,
                "Native request schedule/count/timestamp differs.");
            Check(ReferenceEquals(request.Messages[0], nativeSystem), "Canonical native system input changed.");
            SameJson(nativeSystem.WireBody.Value, request.Messages[0].WireBody.Value);
            var mapped = request.Messages.SetItem(0, MapSystemEnvelope(request.Messages[0], "content", "systemMessage"));
            SameJson(frozenSystem.WireBody.Value, mapped[0].WireBody.Value);
            for (var index = 1; index < mapped.Length; index++)
                Check(ReferenceEquals(request.Messages[index], mapped[index]), "Mapping changed a user/actual assistant/actual tool result.");
            Calls++;
            // Return the original underlying enumerable, preserving cancellation, disposal and joins.
            return inner.StreamAsync(request with { Messages = mapped }, token);
        }
    }
    private static JsonData StampAssistant(JsonElement providerAssistant) => PiAiAssistant(providerAssistant, stamp: true);
    // pi-ai builds the assistant as one literal (packages/ai/src/api/pi-messages.ts:181-190: role, content, api, provider, model,
    // usage, stopReason, timestamp); later assignments (responseId, providerThinkingLevel: pi-messages.ts:196-203) follow, and
    // agent-loop.ts:409 Object.assign adds thinkingLevel last. The frozen fixture lists the fields in an authored order.
    private static readonly string[] PiAiAssistantOrder = ["role", "content", "api", "provider", "model", "usage", "stopReason", "timestamp"];
    private static JsonData PiAiAssistant(JsonElement providerAssistant, bool stamp)
    {
        Check(providerAssistant.GetProperty("role").GetString() == "assistant" && !providerAssistant.TryGetProperty("thinkingLevel", out _),
            "Frozen provider assistant already has an Agent stamp.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var name in PiAiAssistantOrder)
                if (providerAssistant.TryGetProperty(name, out var value)) { writer.WritePropertyName(name); value.WriteTo(writer); }
            foreach (var field in providerAssistant.EnumerateObject())
                if (!PiAiAssistantOrder.Contains(field.Name, StringComparer.Ordinal)) { writer.WritePropertyName(field.Name); field.Value.WriteTo(writer); }
            if (stamp) writer.WriteString("thinkingLevel", "off");
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
    // Explicit r1 Agent expectation layer; retain the pinned provider-only fixture as a separate full assertion.
    private static JsonData RewriteContinuationBody(JsonElement body, bool addStamp, bool frozen = false)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in body.EnumerateObject())
            {
                writer.WritePropertyName(field.Name);
                if (field.Name != "context") { field.Value.WriteTo(writer); continue; }
                writer.WriteStartObject();
                foreach (var context in field.Value.EnumerateObject())
                {
                    writer.WritePropertyName(context.Name);
                    if (context.Name != "messages") { context.Value.WriteTo(writer); continue; }
                    Check(context.Value.GetArrayLength() == 4, "Continuation shape differs.");
                    writer.WriteStartArray(); var index = 0;
                    foreach (var message in context.Value.EnumerateArray())
                    {
                        if (index++ != 2) { message.WriteTo(writer); continue; }
                        if (addStamp || frozen) { PiAiAssistant(message, stamp: addStamp).Value.WriteTo(writer); continue; }
                        Check(message.GetProperty("role").GetString() == "assistant" && message.GetProperty("thinkingLevel").GetString() == "off",
                            "Actual Agent assistant lacks the pinned off stamp.");
                        writer.WriteStartObject();
                        foreach (var property in message.EnumerateObject())
                            if (property.Name != "thinkingLevel") { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
    private static JsonData AgentRequestExpectation(JsonElement frozenRequest)
    {
        var agentBody = RewriteContinuationBody(frozenRequest.GetProperty("body"), addStamp: true);
        var bodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentBody.ToString()));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in frozenRequest.EnumerateObject())
            {
                writer.WritePropertyName(field.Name);
                if (field.Name == "body") agentBody.Value.WriteTo(writer);
                else if (field.Name == "bodyUtf8Base64") writer.WriteStringValue(bodyBase64);
                else field.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
    private static string Wire(JsonElement events) => string.Concat(events.EnumerateArray().Select(dto => "data: " + JsonSerializer.Serialize(dto) + "\n\n"));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Enter(TaskCompletionSource entry, Task original)
    {
        using var lifetime = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
        try
        {
            await Task.WhenAny(entry.Task, original, deadline);
            if (entry.Task.IsCompleted) { await entry.Task; return; }
            if (original.IsCompleted) { await original; throw new InvalidOperationException("Original finished before required barrier."); }
            throw new TimeoutException("Original continuation did not enter required barrier.");
        }
        finally { lifetime.Cancel(); }
    }
    private static Dictionary<string, string[]> Headers(HttpRequestMessage request) => request.Headers.Concat(request.Content!.Headers)
        .ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value.ToArray(), StringComparer.Ordinal);
    private static void CompareRequest(JsonElement expected, HttpRequestMessage actual, Dictionary<string, string[]> before,
        Dictionary<string, string[]> after, byte[] body)
    {
        Check(actual.RequestUri!.AbsoluteUri == expected.GetProperty("uri").GetString() && actual.Method.Method == expected.GetProperty("method").GetString(), "Complete URI/method differs.");
        SameJson(expected.GetProperty("headers"), JsonSerializer.SerializeToElement(before));
        var wanted = expected.GetProperty("headers").EnumerateObject().ToDictionary(field => field.Name,
            field => field.Value.EnumerateArray().Select(value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        wanted.TryAdd("content-length", [Convert.FromBase64String(expected.GetProperty("bodyUtf8Base64").GetString()!).Length.ToString(CultureInfo.InvariantCulture)]);
        SameJson(JsonSerializer.SerializeToElement(wanted), JsonSerializer.SerializeToElement(after));
        Check(Convert.ToBase64String(body) == expected.GetProperty("bodyUtf8Base64").GetString(), "Complete physical UTF8 request bytes differ.");
        using var parsed = JsonDocument.Parse(body); SameJson(expected.GetProperty("body"), parsed.RootElement);
    }
    private static void SameJson(JsonElement expected, JsonElement actual)
    {
        Check(expected.ValueKind == actual.ValueKind, "JSON kind differs.");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            Check(expected.EnumerateObject().Count() == actual.EnumerateObject().Count(), "Complete object shape differs.");
            foreach (var field in expected.EnumerateObject())
            { Check(actual.TryGetProperty(field.Name, out var value), "Missing field " + field.Name); SameJson(field.Value, value); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Check(expected.GetArrayLength() == actual.GetArrayLength(), "Array size differs."); for (var i = 0; i < expected.GetArrayLength(); i++) SameJson(expected[i], actual[i]); }
        else if (expected.ValueKind == JsonValueKind.Number) Check(BitConverter.DoubleToInt64Bits(expected.GetDouble()) == BitConverter.DoubleToInt64Bits(actual.GetDouble()), "Number differs.");
        else if (expected.ValueKind == JsonValueKind.String) Check(expected.GetString() == actual.GetString(), "String differs.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "inspect";
        public int Prepares, Validations, Effects;
        public Action? BeforePrepare;
        public PreparedToolAction? Prepared, Executed;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BeforePrepare!(); Prepares++;
            Check(invocation.Call.Id == "authored-call-1" && invocation.Call.Name == Name, "Original finalized call identity differs.");
            SameJson(JsonData.Parse("{\"value\":7}").Value, invocation.Call.Arguments.Value);
            var action = new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path, "inert-inspect/7", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty);
            Prepared = action; return ValueTask.FromResult(action);
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Validations++; return ValueTask.FromResult(ReferenceEquals(Prepared, action) && action.Arguments.Value.GetProperty("value").GetInt32() == 7); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(ReferenceEquals(Prepared, action) && Validations == 2 && Effects == 0, "Execution changed authorized action or repeated effect.");
            Executed = action; Effects++; // The sole effect counter lives in the actual trusted adapter execution.
            var value = action.Arguments.Value.GetProperty("value").GetInt32();
            var text = "Observed " + (value == 7 ? "seven" : value.ToString(CultureInfo.InvariantCulture)) + ".";
            return ValueTask.FromResult(ToolResult.Success(text).WithProperty("details", null));
        }
    }
    private sealed class Policy(Adapter adapter) : IToolActionPolicy
    {
        public int Authorizations; public PreparedToolAction? Authorized;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(adapter.Effects == 0 && adapter.Validations == 2 && ReferenceEquals(adapter.Prepared, action), "Policy did not receive final validated action.");
            Check(invocation.Call.Id == "authored-call-1" && action.Target == "inert-inspect/7", "Policy target/call differs.");
            Authorizations++; Authorized = action; return ValueTask.FromResult(new ToolActionAuthorization(true));
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Response(HttpStatusCode status) : HttpResponseMessage(status)
    { public bool Disposed; protected override void Dispose(bool disposing) { base.Dispose(disposing); Disposed |= disposing; } }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        public bool Disposed;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Disposed |= disposing; }
    }
    private sealed class RequestContent(HttpContent inner) : HttpContent
    {
        public bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); Disposed |= disposing; }
    }
    private sealed class OwnedBody(string wire) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool Disposed; private Task? cleanup;
        public override ValueTask DisposeAsync() => new(cleanup ??= Cleanup());
        private async Task Cleanup()
        { CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; base.Dispose(true); Disposed = true; }
    }
}