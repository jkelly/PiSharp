using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Contracts;

var repo = FindRepo();
string? report = null;
var requireNodeAbsent = false;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--repo" && index + 1 < args.Length) repo = Path.GetFullPath(args[++index]);
    else if (args[index] == "--report" && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
    else if (args[index] == "--require-node-absent") requireNodeAbsent = true;
    else throw new ArgumentException($"Unknown or incomplete option: {args[index]}");
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("anthropic.payload.InspectOnlyOrderAndLegacyBody", AnthropicPayloadHookTests.InspectOnlyOrderAndLegacyBody),
    ("anthropic.payload.ReplaceForceStreamAndBetaExtraction", AnthropicPayloadHookTests.ReplaceForceStreamAndBetaExtraction),
    ("anthropic.payload.HeldCallbackAndCancellationJoin", AnthropicPayloadHookTests.HeldCallbackAndCancellationJoin),
    ("anthropic.payload.ActualFaultAndCancellationInventory", AnthropicPayloadHookTests.ActualFaultAndCancellationInventory),
    ("anthropic.payload.RejectedReplacementAndPriorAdmission", AnthropicPayloadHookTests.RejectedReplacementAndPriorAdmission),
    ("anthropic.payload.SimpleAndAuthenticatedForwarding", AnthropicPayloadHookTests.SimpleAndAuthenticatedForwarding),
    ("anthropic.payload.LegacyMulticastAndPreparationReplay", AnthropicPayloadHookTests.LegacyMulticastAndPreparationReplay),
    ("anthropic.source.rejected-usage-keeps-committed-cost-and-failure", AnthropicSourceSnapshotTests.RejectedUsageKeepsCommittedCostAndNativeFailure),
    ("anthropic.source.binary64-costs-are-native-costs", AnthropicSourceSnapshotTests.Binary64CostsAreNativeCosts),
    ("anthropic.source.options-preserve-constructor-and-deconstruction", AnthropicSourceSnapshotTests.OptionsKeepConstructorAndDeconstructionShape),
    ("anthropic.source.usage-properties-signatures-and-ownership", AnthropicSourceSnapshotTests.UsagePropertiesSignaturesAndOwnership),
    ("anthropic.source.partial-tool-arguments-observation-only", AnthropicSourceSnapshotTests.PartialToolArgumentsAreObservationOnly),
    ("anthropic.source.nonobject-preview-observed-and-final", AnthropicSourceSnapshotTests.NonObjectPreviewIsObservedAndFinal),
    ("source.wire.exact-original-event-shapes", PiWireSourceEventTests.ExactSourceShapes),
    ("source.wire.original-starts-owned-partials-and-nested-metadata", PiWireSourceEventTests.ReadOriginalShapesAndOwnership),
    ("source.wire.reject-ambiguous-and-native-only-events", PiWireSourceEventTests.RefuseAmbiguousOrInventedSourceFrames),
    ("source.wire.compact-native-boundary-remains-separate", PiWireSourceEventTests.CompactNativeBoundaryRemainsSeparate),
    ("native.reducer.thinking-checkpoint-contract", ThinkingCheckpointTests.Contract),
    ("native.stream.thinking-checkpoint-cancel-cleanup", ThinkingCheckpointTests.CancellationAndCleanup),
    ("native.reducer.thinking-checkpoint-bounds-kinds", ThinkingCheckpointTests.BoundsAndKinds),
    ("native.reducer.interleaved-authoritative-ends", Interleaved),
    ("native.reducer.invalid-transitions", InvalidTransitions),
    ("native.json.owned-immutable-and-optional", OwnedJson),
    ("native.json.reject-duplicate-known-wire-keys", DuplicateWireKeys),
    ("native.tools.strict-complete-json", StrictArguments),
    ("native.stream.eof-settles", EofSettles),
    ("native.stream.malformed-settles", MalformedSettles),
    ("native.stream.pre-start-error", PreStartError),
    ("native.stream.terminal-once", TerminalOnce),
    ("native.stream.complete-drains-capacity-one", CompleteDrains),
    ("native.stream.cancel-before-start", CancelBeforeStart),
    ("native.stream.cancel-mid-stream", CancelMidStream),
    ("native.stream.dispose-under-backpressure", DisposeBackpressure),
    ("native.stream.disposal-awaits-shared-cleanup", SharedDisposal),
    ("native.stream.throwing-cancellation-callback-cleans-up", ThrowingCancellationCallback),
    ("native.stream.abandoned-reader", AbandonedReader),
    ("native.stream.single-reader", SingleReader),
    ("native.stream.resource-limits", ResourceLimits),
    ("native.stream.tool-arguments-and-metadata-limits", ToolArgumentLimits),
    ("native.stream.error-terminal-limits", ErrorTerminalLimits),
    ("native.stream.terminal-wins-cleanup-failure", TerminalWinsCleanup),
    ("native.stream.cancel-dispose-race", CancelDisposeRace),
    ("native.stream.transport-failure-redacted", TransportFailure),
    ("differential.frame.interleaved-signed-completed-blocks-and-final", () => FrameDifferential(repo)),
    ("differential.frame.comparison-preserves-signatures-null-order-and-fields", () => FrameComparisonMutations(repo)),
    ("differential.preview.supported-corpus-and-explicit-exclusions", () => StreamingJsonPreviewTests.Corpus(repo)),
    ("native.preview.limits-and-sanitized-hardening", StreamingJsonPreviewTests.Boundaries),
    ("native.preview.cannot-bypass-final-argument-validation", StreamingJsonPreviewTests.DisplayOnly),
    ("native.preview.lone-surrogate-runtime-policy", StreamingJsonPreviewTests.LoneSurrogate),
    ("differential.responses.text-tool-completed-overlap-and-failures", () => ResponsesTextToolTests.Corpus(repo)),
    ("native.responses.parse-streaming-json-final-arguments", ResponsesTextToolTests.FinalArgumentsFollowParseStreamingJson),
    ("native.responses.malformed-and-unsupported-dtos", ResponsesTextToolTests.MalformedAndUnsupported),
    ("native.responses.bounded-admission", ResponsesTextToolTests.Limits),
    ("native.responses.pull-backpressure-and-early-disposal", ResponsesTextToolTests.PullAndEarlyDisposal),
    ("native.responses.cancellation-and-cleanup", ResponsesTextToolTests.Cancellation),
    ("native.responses.cleanup-before-terminal-and-fault", ResponsesTextToolTests.CleanupBeforeTerminalAndFault),
    ("native.responses.authoritative-checkpoints-and-ends", ResponsesTextToolTests.AuthoritativeCheckpointAndEnds),
    ("native.responses.signature-json-stringify-quoting", ResponsesTextToolTests.SignatureUnicode),
    ("native.responses.pending-metadata-separate-budget", ResponsesTextToolTests.SeparatePendingMetadataBudget),
    ("differential.responses-replay.three-frozen-projections-and-immutability", () => ResponsesTranscriptProjectionTests.Corpus(repo)),
    ("native.responses-replay.pairing-and-identity-collisions", ResponsesTranscriptProjectionTests.PairingAndCollisions),
    ("native.responses-replay.system-folding-and-ordering", ResponsesTranscriptProjectionTests.SystemsAndOrdering),
    ("native.responses-replay.argument-quoting-and-numeric-profile", ResponsesTranscriptProjectionTests.ArgumentQuotingAndNumbers),
    ("native.responses-replay.unsupported-and-final-validation", ResponsesTranscriptProjectionTests.UnsupportedAndValidation),
    ("native.responses-replay.bounds-and-cancellation", ResponsesTranscriptProjectionTests.BoundsAndCancellation)
};
FrozenModelCatalogTests.Register(tests, repo);
tests.AddRange(AnthropicMessagesTests.Cases());
tests.AddRange(AnthropicMessagesRequestTests.Cases());
tests.AddRange(OpenAICompletionsWireTests.Cases());
tests.AddRange(CompletionsSourceDifferentialTests.Cases(repo));
tests.AddRange(EcmaScriptJsonProjectionTests.Cases(repo));
tests.AddRange(CompletionsRequestFactoryTests.Cases());
tests.AddRange(CompletionsUserImageTests.Cases());
tests.AddRange(CompletionsToolImageTests.Cases());
if (requireNodeAbsent) tests.Add(("native.runtime.node-unavailable-on-path", NodeUnavailable));
var fixtureDirectory = Path.Combine(repo, "fixtures", "pi-v0.99.1", "providers");
if (!File.Exists(Path.Combine(repo, "PiSharp.slnx")) || !Directory.Exists(fixtureDirectory))
{
    Console.Error.WriteLine($"Repository or required provider fixture directory is missing: {repo}");
    return 1;
}
if (Directory.Exists(fixtureDirectory))
    foreach (var path in Directory.GetFiles(fixtureDirectory, "*.json", SearchOption.AllDirectories))
    {
        using var candidate = JsonDocument.Parse(File.ReadAllText(path));
        if (candidate.RootElement.TryGetProperty("frames", out _) && candidate.RootElement.TryGetProperty("terminal", out _))
        {
            var fixturePath = path;
            tests.Add(($"fixture.{candidate.RootElement.GetProperty("fixtureId").GetString()}", () => Fixture(fixturePath, repo)));
        }
    }

var results = new List<object>();
var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        if (name.StartsWith("native.reducer.thinking-checkpoint-", StringComparison.Ordinal) ||
            name == "native.stream.thinking-checkpoint-cancel-cleanup") await run();
        else await run().WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"PASS {name}");
        results.Add(new { testId = name, status = "passed" });
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        results.Add(new { testId = name, status = "failed", error = exception.Message });
    }
}
Console.WriteLine($"Native contract tests: {tests.Count - failed} passed, {failed} failed.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        scope = "native-contract-tests", nodeAbsentRequested = requireNodeAbsent,
        frameDifferential = new
        {
            captureCommit = "f1c63707b965fa1a30c28f8e18d87b2a7e5e19be", fixtureId = "interleaved-signed",
            comparisonScope = "completed-blocks-and-final-message",
            partialToolArguments = "known-difference-native-does-not-repair-partials",
            wholePrefixParity = "not-claimed"
        },
        jsonPreviewDifferential = new
        {
            captureCommit = "14a1b04690b81d1b1c1621a4a05123346dbee88d", fixtureId = "core-preview",
            scope = "standalone-display-only-preview-subset", supportedCorpusCases = 32, excludedCorpusCases = 7,
            numericParity = "not-qualified", reducerIntegration = "not-implemented"
        },
        responsesDifferential = new
        {
            captureCommit = "2a8441dfbed6d9b01fc616e897e7b886f8f793f9", fixtureId = "responses-core",
            scope = "parsed-text-function-completed-overlap", overlappingOperations = 12,
            partialSnapshotParity = "not-claimed", wrapperParity = "not-claimed-authored-envelope",
            costProfile = "authored-synthetic-decimal-rates"
        },
        responsesReplayDifferential = new
        {
            captureCommit = "560d1fad202019748fbd3e9fe9b1aae7d03d7e0a", fixtureId = "responses-replay-core",
            scope = "serialized-input-array-subset", matchedCases = 3, expectedItemCounts = new[] { 6, 5, 7 },
            undefinedParity = "not-claimed-native-omission", argumentNumbers = "canonical-safe-integers-only"
        },
        tests = results, passed = tests.Count - failed, failed
    }, new JsonSerializerOptions { WriteIndented = true }));
}
return failed == 0 ? 0 : 1;

static AssistantMessage Header() => new("recorded", "fixture", "test-model", 1_700_000_000_000,
    [], TokenUsage.Zero, StopReason.Pending);
static ChatRequest Request() => new(new("test-model", "recorded", "fixture"), [], Header().Timestamp);
static Task NodeUnavailable()
{
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var executable in new[] { "node", "node.exe", "node.cmd", "node.bat" })
            Assert(!File.Exists(Path.Combine(directory, executable)), $"Node is available on the smoke-test PATH: {directory}");
    return Task.CompletedTask;
}
static StreamEvent[] TextEvents()
{
    var start = Header();
    var final = start with { Content = [new TextContent("hello")], StopReason = StopReason.Stop };
    return [new StreamStarted(start), new TextStarted(0, new("")), new TextDelta(0, "hel"),
        new TextDelta(0, "lo"), new TextEnded(0, "hello"), new StreamDone(StopReason.Stop, final)];
}
static Task Interleaved()
{
    var reducer = new AssistantStreamReducer(Header());
    reducer.Apply(new StreamStarted(Header()));
    reducer.Apply(new ThinkingStarted(0, new("", JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"old\"")))));
    reducer.Apply(new ToolCallStarted(1, new("call-1", "read", JsonData.EmptyObject)));
    reducer.Apply(new TextStarted(2, new("prefix")));
    reducer.Apply(new ThinkingDelta(0, "private"));
    reducer.Apply(new ToolCallDelta(1, "{\"path\":\"C:\\\\"));
    var before = reducer.Snapshot();
    reducer.Apply(new TextDelta(2, " transient"));
    reducer.Apply(new ToolCallDelta(1, "x\"}"));
    Equal("{\"path\":\"C:\\\\x\"}", reducer.GetToolJsonPreview(1));
    Equal("{}", ((ToolCallContent)before.Content[1]).Arguments.ToString());
    Equal("prefix", ((TextContent)before.Content[2]).Text);
    var signature = JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"opaque\\u0000=\""));
    reducer.Apply(new ThinkingEnded(0, "", signature));
    reducer.Apply(new ToolCallEnded(1, new("call-1", "read", JsonData.Parse("{\"path\":\"C:\\\\x\"}"))));
    reducer.Apply(new TextEnded(2, "authoritative"));
    var final = reducer.Snapshot() with { StopReason = StopReason.ToolUse };
    reducer.Apply(new StreamDone(StopReason.ToolUse, final));
    Equal("authoritative", ((TextContent)reducer.Snapshot().Content[2]).Text);
    Equal("private", ((ThinkingContent)before.Content[0]).Thinking);
    Equal(StopReason.ToolUse, reducer.Snapshot().StopReason);
    return Task.CompletedTask;
}
static Task InvalidTransitions()
{
    Action<AssistantStreamReducer>[] invalid =
    [
        reducer => reducer.Apply(new TextDelta(0, "x")),
        reducer => { reducer.Apply(new StreamStarted(Header())); reducer.Apply(new StreamStarted(Header())); },
        reducer => { reducer.Apply(new StreamStarted(Header())); reducer.Apply(new TextStarted(1, new(""))); },
        reducer => { reducer.Apply(new StreamStarted(Header())); reducer.Apply(new TextStarted(0, new(""))); reducer.Apply(new ThinkingDelta(0, "x")); },
        reducer => { reducer.Apply(new StreamStarted(Header())); reducer.Apply(new TextStarted(0, new(""))); reducer.Apply(new TextEnded(0, "")); reducer.Apply(new TextDelta(0, "x")); },
        reducer => { reducer.Apply(new StreamStarted(Header())); reducer.Apply(new TextStarted(0, new(""))); reducer.Apply(new StreamDone(StopReason.Stop, Header() with { StopReason = StopReason.Stop })); },
        reducer => { reducer.Apply(new StreamStarted(Header())); reducer.Apply(new ToolCallStarted(0, new("same", "x", JsonData.EmptyObject))); reducer.Apply(new ToolCallStarted(1, new("same", "y", JsonData.EmptyObject))); },
        reducer => { foreach (var value in TextEvents()) reducer.Apply(value); reducer.Apply(TextEvents()[^1]); },
        reducer => { foreach (var value in TextEvents()[..^1]) reducer.Apply(value); reducer.Apply(new StreamDone(StopReason.Stop, Header() with { Content = [new TextContent("changed")], StopReason = StopReason.Stop })); }
    ];
    foreach (var operation in invalid) Throws<StreamProtocolException>(() => operation(new(Header())));
    return Task.CompletedTask;
}
static Task OwnedJson()
{
    JsonData owned;
    using (var document = JsonDocument.Parse("{\"opaque\":[\"exact\\u0000\",null],\"unknown\":{\"x\":1}}"))
        owned = JsonData.FromElement(document.RootElement);
    Equal(JsonValueKind.Null, owned.Value.GetProperty("opaque")[1].ValueKind);
    var message = Header() with { ExtraProperties = JsonFields.Empty.Set("responseId", JsonData.Null).Set("unknown", owned),
        Usage = TokenUsage.Zero with { ExtraProperties = JsonFields.Empty.Set("reasoning", JsonData.Parse("0")) } };
    var serialized = PiWireJson.WriteMessage(message);
    Equal(JsonValueKind.Null, serialized.Value.GetProperty("responseId").ValueKind);
    Assert(!serialized.Value.TryGetProperty("responseModel", out _), "Missing field became present.");
    Equal(0, serialized.Value.GetProperty("usage").GetProperty("reasoning").GetInt32());
    Assert(JsonElement.DeepEquals(serialized.Value, PiWireJson.WriteMessage(PiWireJson.ReadMessage(serialized.Value)).Value), "Wire round trip changed metadata.");
    Throws<JsonException>(() => JsonData.Parse("{\"x\":1,\"x\":2}"));
    return Task.CompletedTask;
}
static Task StrictArguments()
{
    foreach (var text in new[] { "{\"path\":", "{\"x\":1,}", "[]", "null", "{\"a\":1,\"a\":2}", "{\"bad\":\"\\x\"}", "{} {}" })
        Throws<JsonException>(() => FinalToolArguments.ParseStrict(text));
    var arguments = FinalToolArguments.ParseStrict("{\"path\":\"C:\\\\文\\\\a\",\"exact\":0.000001}");
    Equal("C:\\文\\a", arguments.Json.Value.GetProperty("path").GetString());
    Equal(0.000001m, arguments.Json.Value.GetProperty("exact").GetDecimal());
    return Task.CompletedTask;
}
static Task DuplicateWireKeys()
{
    var message = PiWireJson.WriteMessage(Header()).ToString();
    using var duplicateMessage = JsonDocument.Parse("{\"role\":\"user\"," + message[1..]);
    Throws<JsonException>(() => PiWireJson.ReadMessage(duplicateMessage.RootElement));
    using var duplicateEvent = JsonDocument.Parse("{\"type\":\"thinking_delta\",\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"x\"}");
    Throws<JsonException>(() => PiWireJson.ReadEvent(duplicateEvent.RootElement));
    using var duplicateContent = JsonDocument.Parse("{\"type\":\"text\",\"text\":\"first\",\"text\":\"last\"}");
    Throws<JsonException>(() => PiWireJson.ReadContent(duplicateContent.RootElement));
    using var duplicateUsage = JsonDocument.Parse(message.Replace("\"input\":0", "\"input\":99,\"input\":0", StringComparison.Ordinal));
    Throws<JsonException>(() => PiWireJson.ReadMessage(duplicateUsage.RootElement));
    return Task.CompletedTask;
}
static async Task EofSettles()
{
    var outcome = await new ChatClient(new RecordedChatTransport(TextEvents()[..^1]), 1).CompleteAsync(Request());
    Equal(ChatFailureKind.UnexpectedEof, outcome.Failure?.Kind);
    Equal(StopReason.Error, outcome.Message.StopReason);
    Equal("hello", ((TextContent)outcome.Message.Content[0]).Text);
}
static async Task MalformedSettles()
{
    var outcome = await new ChatClient(new RecordedChatTransport([new TextDelta(0, "x")])).CompleteAsync(Request());
    Equal(ChatFailureKind.MalformedStream, outcome.Failure?.Kind);
}
static async Task PreStartError()
{
    var error = Header() with { StopReason = StopReason.Error, ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"Missing fake auth\"")) };
    await using var run = await new ChatClient(new RecordedChatTransport([new StreamError(StopReason.Error, error)])).StartAsync(Request());
    var events = await Drain(run);
    Equal(1, events.Count); Assert(events[0] is StreamError, "Expected pre-start error.");
    Equal(error, (await run.Completion).Message);
}
static async Task TerminalOnce()
{
    await using var run = await new ChatClient(new RecordedChatTransport([.. TextEvents(), new TextDelta(0, "stale"), TextEvents()[^1]])).StartAsync(Request());
    var events = await Drain(run);
    Equal(1, events.Count(value => value is StreamTerminalEvent));
    Equal(6, events.Count);
    var first = await run.Completion;
    Assert(ReferenceEquals(first, await run.Completion), "Completion settled more than once.");
    Equal(StopReason.Stop, first.Message.StopReason);
}
static async Task CompleteDrains()
{
    var result = await new ChatClient(new RecordedChatTransport(TextEvents()), 1).CompleteAsync(Request());
    Equal(StopReason.Stop, result.Message.StopReason);
    Assert(result.Failure is null, "Successful replay returned failure.");
}
static async Task CancelBeforeStart()
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await using var run = await new ChatClient(new RecordedChatTransport(TextEvents())).StartAsync(Request(), cancellation.Token);
    Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure?.Kind);
    var events = await Drain(run); Equal(1, events.Count);
    Assert(events[0] is StreamError { Reason: StopReason.Aborted }, "Cancellation must settle with aborted.");
}
static async Task CancelMidStream()
{
    var gate = new GatedTransport();
    await using var run = await new ChatClient(gate, 1).StartAsync(Request());
    await using var reader = run.ReadEventsAsync().GetAsyncEnumerator();
    Assert(await reader.MoveNextAsync(), "Missing start.");
    Assert(await reader.MoveNextAsync(), "Missing content start.");
    await gate.Waiting.Task; run.Cancel();
    var remaining = new List<StreamEvent>();
    while (await reader.MoveNextAsync()) remaining.Add(reader.Current);
    Equal(1, remaining.Count(value => value is StreamTerminalEvent));
    Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure?.Kind);
}
static async Task DisposeBackpressure()
{
    var transport = new ObservedTransport();
    var run = await new ChatClient(transport, 1).StartAsync(Request());
    await transport.SecondFrame.Task;
    await run.DisposeAsync();
    Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure?.Kind);
    Assert(transport.Disposed, "Transport iterator was not disposed.");
    await run.DisposeAsync();
}
static async Task AbandonedReader()
{
    var transport = new ObservedTransport();
    await using var run = await new ChatClient(transport, 1).StartAsync(Request());
    await using (var reader = run.ReadEventsAsync().GetAsyncEnumerator())
        Assert(await reader.MoveNextAsync(), "Missing start.");
    Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure?.Kind);
}
static async Task SharedDisposal()
{
    var transport = new CleanupGateTransport();
    var run = await new ChatClient(transport).StartAsync(Request());
    await transport.Waiting.Task;
    var first = run.DisposeAsync().AsTask();
    await transport.CleanupEntered.Task;
    var second = run.DisposeAsync().AsTask();
    var shared = ReferenceEquals(first, second);
    var premature = second.IsCompleted;
    transport.ReleaseCleanup.TrySetResult();
    await Task.WhenAll(first, second);
    Assert(shared, "Concurrent disposal did not return its shared cleanup task.");
    Assert(!premature, "Second disposal returned before transport cleanup finished.");
    Assert(transport.CleanupFinished, "Disposal completed before transport cleanup.");
}
static async Task ThrowingCancellationCallback()
{
    foreach (var explicitCancel in new[] { false, true })
    {
        var transport = new CallbackThrowsTransport();
        var run = await new ChatClient(transport).StartAsync(Request());
        await transport.Waiting.Task;
        if (explicitCancel) run.Cancel();
        await run.DisposeAsync();
        await run.DisposeAsync();
        Assert(transport.Disposed, "Throwing cancellation callback skipped iterator cleanup.");
        Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure?.Kind);
        Equal(ChatFailureKind.Provider, run.CleanupFailure?.Kind);
        Assert(!run.CleanupFailure!.Message.Contains("fake-secret", StringComparison.Ordinal), "Callback diagnostics leaked exception content.");
    }
}
static async Task SingleReader()
{
    await using var run = await new ChatClient(new RecordedChatTransport(TextEvents())).StartAsync(Request());
    await Drain(run);
    await using var reader = run.ReadEventsAsync().GetAsyncEnumerator();
    await ThrowsAsync<InvalidOperationException>(async () => { await reader.MoveNextAsync(); });
}
static async Task ResourceLimits()
{
    var result = await new ChatClient(new RecordedChatTransport(TextEvents()), 1, new(MaximumCharacters: 28)).CompleteAsync(Request());
    Equal(ChatFailureKind.ResourceLimit, result.Failure?.Kind);
}
static async Task ToolArgumentLimits()
{
    var huge = new ToolCallContent("call", "tool", JsonData.Parse(JsonSerializer.Serialize(new { value = new string('x', 200) })));
    foreach (var frames in new StreamEvent[][]
    {
        [new StreamStarted(Header()), new ToolCallStarted(0, huge)],
        [new StreamStarted(Header()), new ToolCallStarted(0, new("call", "tool", JsonData.EmptyObject)), new ToolCallEnded(0, huge)],
        [new StreamStarted(Header() with { ExtraProperties = JsonFields.Empty.Set("opaque", huge.Arguments) })]
    })
    {
        var result = await new ChatClient(new RecordedChatTransport(frames), 1, new(MaximumCharacters: 100)).CompleteAsync(Request());
        Equal(ChatFailureKind.ResourceLimit, result.Failure?.Kind);
    }
    var hugeProperties = JsonFields.Empty.Set("thinkingSignature", huge.Arguments);
    foreach (var frames in new StreamEvent[][]
    {
        [new StreamStarted(Header()), new ThinkingStarted(0, new("")), new ThinkingEnded(0, "", hugeProperties)],
        [new StreamStarted(Header()), new StreamDone(StopReason.Stop, Header() with { StopReason = StopReason.Stop, ExtraProperties = hugeProperties })]
    })
    {
        var result = await new ChatClient(new RecordedChatTransport(frames), 1, new(MaximumCharacters: 100)).CompleteAsync(Request());
        Equal(ChatFailureKind.ResourceLimit, result.Failure?.Kind);
        Assert(!PiWireJson.WriteMessage(result.Message).ToString().Contains(new string('x', 200), StringComparison.Ordinal), "Rejected oversized metadata was retained.");
    }
}
static async Task ErrorTerminalLimits()
{
    foreach (var message in new AssistantMessage[]
    {
        Header() with { StopReason = StopReason.Error, Content = [new TextContent(""), new TextContent(""), new TextContent("")] },
        Header() with { StopReason = StopReason.Error, Content = [new TextContent(new string('x', 200))] },
        Header() with { StopReason = StopReason.Error, ExtraProperties = JsonFields.Empty.Set("opaque", JsonData.Parse(JsonSerializer.Serialize(new string('x', 200)))) }
    })
    {
        var result = await new ChatClient(new RecordedChatTransport([new StreamError(StopReason.Error, message)]),
            1, new(MaximumBlocks: 2, MaximumCharacters: 100)).CompleteAsync(Request());
        Equal(ChatFailureKind.ResourceLimit, result.Failure?.Kind);
        Equal(0, result.Message.Content.Length);
        Assert(!PiWireJson.WriteMessage(result.Message).ToString().Contains(new string('x', 200), StringComparison.Ordinal), "Rejected error payload was retained.");
    }
}
static async Task TerminalWinsCleanup()
{
    await using var run = await new ChatClient(new CleanupFailingTransport(TextEvents())).StartAsync(Request());
    var events = await Drain(run);
    Equal(1, events.Count(value => value is StreamTerminalEvent));
    var result = await run.Completion;
    Equal(StopReason.Stop, result.Message.StopReason);
    Assert(result.Failure is null, "Cleanup changed an already settled terminal outcome.");
}
static async Task CancelDisposeRace()
{
    for (var iteration = 0; iteration < 100; iteration++)
    {
        var run = await new ChatClient(new ObservedTransport(), 1).StartAsync(Request());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = Task.Run(async () => { await gate.Task; run.Cancel(); });
        var dispose = Task.Run(async () => { await gate.Task; await run.DisposeAsync(); });
        gate.SetResult();
        await Task.WhenAll(cancel, dispose);
        run.Cancel();
        Equal(ChatFailureKind.Cancelled, (await run.Completion).Failure?.Kind);
    }
}
static async Task TransportFailure()
{
    var result = await new ChatClient(new BrokenTransport()).CompleteAsync(Request());
    Equal(ChatFailureKind.Provider, result.Failure?.Kind);
    Assert(!PiWireJson.WriteMessage(result.Message).ToString().Contains("fake-secret", StringComparison.Ordinal), "Transport diagnostics leaked input.");
}
static async Task<(JsonData Input, JsonData Expected)> ReadFrozenFrame(string repo)
{
    var directory = Path.Combine(repo, "fixtures", "pi-v0.99.1", "frame");
    var input = await File.ReadAllBytesAsync(Path.Combine(directory, "interleaved-signed.input.json"));
    var expected = await File.ReadAllBytesAsync(Path.Combine(directory, "interleaved-signed.expected.json"));
    Equal("986b394b81014fae385688b136049cbe81ef665f478f6e6a72e2f4784f301729", Convert.ToHexStringLower(SHA256.HashData(input)));
    Equal("8ed97e0a7779566e94321e2b46306061c24a10440ad3f1c7903e0409260712a4", Convert.ToHexStringLower(SHA256.HashData(expected)));
    using var inputDocument = JsonDocument.Parse(input); using var expectedDocument = JsonDocument.Parse(expected);
    foreach (var root in new[] { inputDocument.RootElement, expectedDocument.RootElement })
    {
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f", root.GetProperty("sourceSha").GetString());
        Equal("interleaved-signed", root.GetProperty("fixtureId").GetString());
    }
    Equal("authored-frame-event-input", inputDocument.RootElement.GetProperty("kind").GetString());
    Equal("captured-upstream-frame-oracle", expectedDocument.RootElement.GetProperty("kind").GetString());
    return (JsonData.FromElement(inputDocument.RootElement), JsonData.FromElement(expectedDocument.RootElement));
}
static async Task FrameDifferential(string repo)
{
    var (inputData, expectedData) = await ReadFrozenFrame(repo);
    var input = inputData.Value; var observations = expectedData.Value.GetProperty("observations");
    var operations = input.GetProperty("operations"); var frames = observations.GetProperty("frames");
    var prefixes = observations.GetProperty("prefixReductions");
    Equal(operations.GetArrayLength() + 1, frames.GetArrayLength());
    Equal(frames.GetArrayLength() + 1, prefixes.GetArrayLength());
    var reducer = new AssistantStreamReducer(PiWireJson.ReadMessage(input.GetProperty("initialMessage")));
    var mapped = new JsonArray(); var completed = new JsonArray(); var previews = new JsonArray();
    var rawToolJson = new Dictionary<int, string>();
    for (var frameIndex = 0; frameIndex < frames.GetArrayLength(); frameIndex++)
    {
        var frame = frames[frameIndex]; var kind = frame.GetProperty("type").GetString()!;
        var sourceOperation = frameIndex == 0 ? "start" : operations[frameIndex - 1].GetProperty("type").GetString()!;
        int? contentIndex = frameIndex == 0 ? null : frame.GetProperty("contentIndex").GetInt32();
        var prefix = prefixes[frameIndex];
        Equal(frameIndex, prefix.GetProperty("afterEmissionIndex").GetInt32());
        Equal(frameIndex + 1, prefix.GetProperty("frameCount").GetInt32());
        if (frameIndex == 0)
            Assert(FrameJsonEqual(input.GetProperty("initialMessage"), frame.GetProperty("partial")), "Captured start changed input envelope.");
        else
        {
            var operation = operations[frameIndex - 1]; Equal(operation.GetProperty("contentIndex").GetInt32(), contentIndex!.Value);
            Assert(kind == sourceOperation || sourceOperation == "toolcall_delta" && kind == "toolcall_checkpoint", "Unexpected operation-to-frame mapping.");
            if (sourceOperation == "toolcall_start") rawToolJson[contentIndex.Value] = "";
            if (sourceOperation == "toolcall_delta")
            {
                rawToolJson[contentIndex.Value] += operation.GetProperty("delta").GetString();
                Equal(kind == "toolcall_checkpoint" ? rawToolJson[contentIndex.Value] : operation.GetProperty("delta").GetString(),
                    frame.GetProperty(kind == "toolcall_checkpoint" ? "json" : "delta").GetString());
            }
        }
        var nativeEvent = PiWireJson.ReadEvent(frame); reducer.Apply(nativeEvent);
        mapped.Add(new JsonObject { ["frameIndex"] = frameIndex, ["sourceOperation"] = sourceOperation, ["frameType"] = kind,
            ["nativeEvent"] = nativeEvent.GetType().Name, ["contentIndex"] = contentIndex });
        var upstreamContent = prefix.GetProperty("reducedMessage").GetProperty("content");
        var snapshot = reducer.Snapshot();
        if (nativeEvent is TextEnded or ThinkingEnded or ToolCallEnded)
        {
            var nativeContent = PiWireJson.WriteContent(snapshot.Content[contentIndex!.Value]);
            completed.Add(new JsonObject { ["frameIndex"] = frameIndex, ["contentIndex"] = contentIndex.Value,
                ["matches"] = FrameJsonEqual(upstreamContent[contentIndex.Value], nativeContent.Value),
                ["nativeContent"] = JsonNode.Parse(nativeContent.ToString()) });
        }
        if (nativeEvent is ToolCallCheckpoint or ToolCallDelta)
        {
            Equal(rawToolJson[contentIndex!.Value], reducer.GetToolJsonPreview(contentIndex.Value));
            var nativeArguments = ((ToolCallContent)snapshot.Content[contentIndex.Value]).Arguments;
            var upstreamArguments = upstreamContent[contentIndex.Value].GetProperty("arguments");
            previews.Add(new JsonObject { ["frameIndex"] = frameIndex, ["contentIndex"] = contentIndex.Value,
                ["rawPreview"] = reducer.GetToolJsonPreview(contentIndex.Value),
                ["nativeArguments"] = JsonNode.Parse(nativeArguments.ToString()),
                ["upstreamArguments"] = JsonNode.Parse(upstreamArguments.GetRawText()),
                ["argumentsMatch"] = FrameJsonEqual(upstreamArguments, nativeArguments.Value) });
            Assert(FrameJsonEqual(JsonData.EmptyObject.Value, nativeArguments.Value), "Native partial arguments unexpectedly changed; review the documented gap.");
        }
    }
    var reduced = PiWireJson.WriteMessage(reducer.Snapshot());
    // The source capture chooses toolUse explicitly; derive the native final from its own ends/envelope.
    reducer.Apply(new StreamDone(StopReason.ToolUse, reducer.Snapshot() with { StopReason = StopReason.ToolUse }));
    var terminal = PiWireJson.WriteMessage(reducer.Snapshot());
    var reducedMatches = FrameJsonEqual(observations.GetProperty("reducedMessage"), reduced.Value);
    var terminalMatches = FrameJsonEqual(observations.GetProperty("terminalResult"), terminal.Value);
    var actual = new JsonObject
    {
        ["schemaVersion"] = 1, ["fixtureId"] = "interleaved-signed", ["captureCommit"] = "f1c63707b965fa1a30c28f8e18d87b2a7e5e19be",
        ["sourceSha"] = input.GetProperty("sourceSha").GetString(), ["kind"] = "native-final-contract-differential",
        ["comparisonScope"] = "completed-blocks-and-final-message", ["wholePrefixParity"] = "not-claimed",
        ["partialToolArguments"] = "known-difference-native-does-not-repair-partials", ["mappedFrames"] = mapped,
        ["completedBlocks"] = completed, ["rawToolPreviewsAndKnownDifference"] = previews,
        ["reducedMessage"] = JsonNode.Parse(reduced.ToString()), ["terminalResult"] = JsonNode.Parse(terminal.ToString()),
        ["reducedMessageMatches"] = reducedMatches, ["terminalResultMatches"] = terminalMatches
    };
    var output = Path.Combine(repo, "artifacts", "native", "frame-interleaved-signed.actual.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Equal(3, completed.Count); Assert(completed.All(block => block!["matches"]!.GetValue<bool>()), $"Completed block differs: {output}");
    Equal(2, previews.Count); Assert(previews.All(preview => !preview!["argumentsMatch"]!.GetValue<bool>()), "Captured partial-JSON difference disappeared; reassess scope.");
    Assert(reducedMatches && terminalMatches, $"Frame final-contract differential differs: {output}");
    Equal(StopReason.ToolUse, reducer.Snapshot().StopReason); Assert(reducer.IsTerminal, "Native terminal did not settle.");
}
static async Task FrameComparisonMutations(string repo)
{
    var (_, expected) = await ReadFrozenFrame(repo);
    var terminal = expected.Value.GetProperty("observations").GetProperty("terminalResult");
    var source = JsonNode.Parse(terminal.GetRawText())!.AsObject();
    var reordered = new JsonObject(source.Reverse().Select(property => new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone())));
    Assert(FrameJsonEqual(terminal, JsonData.Parse(reordered.ToJsonString()).Value), "Object-key reordering changed comparison.");
    Action<JsonNode>[] mutations =
    [
        node => node["content"]![0]!["thinkingSignature"] = "changed",
        node => node["content"]![1]!["textSignature"] = "changed",
        node => node["content"]![2]!["thoughtSignature"] = "changed",
        node => node["content"]![2]!["namespace"] = "changed",
        node => node["content"]![2]!["arguments"]!["options"]!.AsObject().Remove("preserve"),
        node => node["content"]![2]!["arguments"]!["options"]!["order"] = new JsonArray("a", "b"),
        node => node["content"]![0]!["redacted"] = null,
        node => node["content"]![1]!["text"] = node["content"]![1]!["text"]!.GetValue<string>().Replace("\r\n", "\n", StringComparison.Ordinal),
        node => node.AsObject().Remove("responseId"),
        node => node["timestamp"] = 1_700_000_000_001L,
        node => node["stopReason"] = "stop",
        node => node["usage"]!["input"] = 1,
        node => node["usage"]!["cost"]!["total"] = JsonNode.Parse("0.0")
    ];
    foreach (var mutate in mutations)
    {
        var changed = source.DeepClone(); mutate(changed);
        Assert(!FrameJsonEqual(terminal, JsonData.Parse(changed.ToJsonString()).Value), "Frame comparison erased a meaningful mutation.");
    }
    Assert(!FrameJsonEqual(JsonData.Parse("9007199254740992").Value, JsonData.Parse("9007199254740993").Value), "Large numeric tokens were rounded during comparison.");
}
static bool FrameJsonEqual(JsonElement expected, JsonElement actual)
{
    if (expected.ValueKind != actual.ValueKind) return false;
    return expected.ValueKind switch
    {
        JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count() &&
            expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && FrameJsonEqual(property.Value, value)),
        JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength() &&
            expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => FrameJsonEqual(pair.First, pair.Second)),
        JsonValueKind.String => expected.GetString() == actual.GetString(),
        JsonValueKind.Number => expected.GetRawText() == actual.GetRawText(),
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        _ => false
    };
}
static async Task Fixture(string path, string repo)
{
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    var root = document.RootElement;
    Equal("d86654abb8862e201933517d6f1fce9f88dd117f", root.GetProperty("sourceSha").GetString());
    var frames = root.GetProperty("frames").EnumerateArray().Select(PiWireJson.ReadEvent).ToImmutableArray();
    var terminal = PiWireJson.ReadEvent(root.GetProperty("terminal"));
    var header = frames.OfType<StreamStarted>().FirstOrDefault()?.Partial ?? ((StreamTerminalEvent)terminal).Message;
    var request = new ChatRequest(new(header.Model, header.Api, header.Provider), [], header.Timestamp);
    await using var run = await new ChatClient(new RecordedChatTransport([.. frames, terminal]), 1).StartAsync(request);
    var events = await Drain(run);
    var result = await run.Completion;
    var actual = new JsonObject
    {
        ["events"] = new JsonArray(events.Select(value => JsonNode.Parse(PiWireJson.WriteEvent(value).ToString())).ToArray()),
        ["result"] = JsonNode.Parse(PiWireJson.WriteMessage(result.Message).ToString())
    };
    var output = Path.Combine(repo, "artifacts", "native", root.GetProperty("fixtureId").GetString() + ".actual.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Assert(JsonElement.DeepEquals(root.GetProperty("expected"), JsonData.Parse(actual.ToJsonString()).Value), $"Fixture mismatch: {path}; actual: {output}");
}
static async Task<List<StreamEvent>> Drain(ChatRun run)
{
    var events = new List<StreamEvent>();
    await foreach (var value in run.ReadEventsAsync()) events.Add(value);
    return events;
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
static void Throws<T>(Action operation) where T : Exception
{
    try { operation(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static async Task ThrowsAsync<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static string FindRepo()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    return Directory.GetCurrentDirectory();
}

sealed class GatedTransport : IChatTransport
{
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new StreamStarted(new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));
        yield return new TextStarted(0, new(""));
        Waiting.SetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
sealed class ObservedTransport : IChatTransport
{
    public TaskCompletionSource SecondFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            yield return new StreamStarted(new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));
            SecondFrame.TrySetResult();
            yield return new TextStarted(0, new(""));
            while (true) { cancellationToken.ThrowIfCancellationRequested(); yield return new TextDelta(0, "x"); await Task.Yield(); }
        }
        finally { Disposed = true; }
    }
}
sealed class BrokenTransport : IChatTransport
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("fake-secret");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
sealed class CleanupFailingTransport(IEnumerable<StreamEvent> events) : IChatTransport
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var value in events)
            {
                cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield();
            }
        }
        finally { throw new IOException("Iterator cleanup failed after terminal."); }
    }
}
sealed class CleanupGateTransport : IChatTransport
{
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool CleanupFinished { get; private set; }
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            yield return new StreamStarted(new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));
            Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; CleanupFinished = true;
        }
    }
}
sealed class CallbackThrowsTransport : IChatTransport
{
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(() => throw new IOException("fake-secret callback failure"));
        try
        {
            yield return new StreamStarted(new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));
            Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally { Disposed = true; }
    }
}
