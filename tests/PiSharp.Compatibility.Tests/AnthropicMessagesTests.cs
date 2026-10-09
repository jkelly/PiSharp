using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

static class AnthropicMessagesTests
{
    private const string Start = """{"type":"message_start","message":{"id":"message","role":"assistant","model":"fixture-model","content":[],"usage":{"input_tokens":11,"output_tokens":1,"cache_read_input_tokens":2,"cache_creation_input_tokens":5,"cache_creation":{"ephemeral_1h_input_tokens":2}}}}""";
    private const string Stop = """{"type":"message_stop"}""";
    private const string End = """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7,"output_tokens_details":{"thinking_tokens":3}}}""";
    private const string TextStart = """{"type":"content_block_start","index":9,"content_block":{"type":"text","text":"H","opaque":{"n":9007199254740993,"nil":null}}}""";
    private const string ThinkingStart = """{"type":"content_block_start","index":2,"content_block":{"type":"thinking","thinking":"","signature":"s","future":null}}""";
    private const string ToolStart = """{"type":"content_block_start","index":99,"content_block":{"type":"tool_use","id":"call","name":"read","input":{"seed":true},"namespace":null}}""";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (nameof(SparseBlocksAndUsage), SparseBlocksAndUsage),
        (nameof(SignaturesRedactionAndOwnership), SignaturesRedactionAndOwnership),
        (nameof(FinalArgumentsFollowParseStreamingJson), FinalArgumentsFollowParseStreamingJson),
        (nameof(UsagePresenceAndNumberProfile), UsagePresenceAndNumberProfile),
        (nameof(FallbackRatesAndMarkers), FallbackRatesAndMarkers),
        (nameof(FallbackBoundariesAndProfiles), FallbackBoundariesAndProfiles),
        (nameof(StopReasonsAndProtocolFailures), StopReasonsAndProtocolFailures),
        (nameof(LimitsAndStrictDtoAdmission), LimitsAndStrictDtoAdmission),
        (nameof(OwnedCleanupAndFaults), OwnedCleanupAndFaults),
        (nameof(CurrentSourceFailureBoundary), CurrentSourceFailureBoundary),
        (nameof(CurrentFailureAwaitedCleanupAndCancellation), CurrentFailureAwaitedCleanupAndCancellation),
        (nameof(CancellationAndSharedReducer), CancellationAndSharedReducer)
    ];
    private static ChatRequest Request() => new(new("fixture-model", "anthropic-messages", "fixture-provider"), [], 1_700_000_000_000);
    private const string FallbackStart = """{"type":"content_block_start","index":0,"content_block":{"type":"fallback","from":{"model":"fixture-model"},"to":{"model":"untrusted-marker-model"}}}""";
    public static async Task FallbackRatesAndMarkers()
    {
        var rates = new AnthropicTokenRates(2_000_000, 3_000_000, 500_000, 1_500_000);
        var options = new AnthropicMessagesOptions(Rates: new(1_000_000, 1_000_000, 1_000_000, 1_000_000),
            AllowedFallbackModels: [new("other-provider", "actual-model", new(9_000_000)), new("fixture-provider", "actual-model", rates)]);
        var start = Start.Replace("fixture-model", "actual-model");
        var events = await Collect([start, FallbackStart, BlockStop(0), TextStart, BlockStop(9), End, Stop], options);
        var done = Terminal(events); Assert(done is StreamDone, "Pre-output fallback failed.");
        Equal(1, done.Message.Content.Length); Equal(0, events.OfType<TextStarted>().Single().ContentIndex);
        Equal("fixture-model", done.Message.Model); Equal("fixture-provider", done.Message.Provider);
        Equal("\"actual-model\"", Property(done.Message.ExtraProperties, "responseModel"));
        Equal(22m, done.Message.Usage.Cost.Input); Equal(21m, done.Message.Usage.Cost.Output);
        Equal(1m, done.Message.Usage.Cost.CacheRead); Equal(12.5m, done.Message.Usage.Cost.CacheWrite);
        Equal(56.5m, done.Message.Usage.Cost.Total);
        var early = Terminal(await Collect([start, FallbackStart], options));
        Failure(early, AnthropicMessagesFailure.UnexpectedEof); Equal(38.5m, early.Message.Usage.Cost.Total);
        // Marker stops are optional bookkeeping in the pinned helper, never replayable content.
        Assert(Terminal(await Collect([start, FallbackStart, End, Stop], options)) is StreamDone, "Marker invented an unfinished content slot.");
        foreach (var responseModel in new[] { "fixture-model", "unknown-model", "ACTUAL-MODEL" })
        {
            var result = Terminal(await Collect([Start.Replace("fixture-model", responseModel), End, Stop], options));
            Assert(result is StreamDone, "Unknown model failed instead of using requested rates."); Equal(27m, result.Message.Usage.Cost.Total);
        }
        var foreign = options with { AllowedFallbackModels = [new("other-provider", "actual-model", rates)] };
        Equal(27m, Terminal(await Collect([start, End, Stop], foreign)).Message.Usage.Cost.Total);
        var zero = options with { AllowedFallbackModels = [new("fixture-provider", "actual-model", new())] };
        Equal(0m, Terminal(await Collect([start, End, Stop], zero)).Message.Usage.Cost.Total);
    }
    public static async Task FallbackBoundariesAndProfiles()
    {
        var afterContent = await Collect([Start, TextStart, BlockStop(9), FallbackStart, End, Stop]);
        Failure(Terminal(afterContent), AnthropicMessagesFailure.MalformedStream);
        Equal("H", ((TextContent)Terminal(afterContent).Message.Content.Single()).Text);
        Failure(Terminal(await Collect([Start, FallbackStart, FallbackStart, End, Stop])), AnthropicMessagesFailure.MalformedStream);
        Failure(Terminal(await Collect([Start, FallbackStart, BlockStop(0), BlockStop(0), End, Stop])), AnthropicMessagesFailure.MalformedStream);
        Failure(Terminal(await Collect([Start, FallbackStart, TextStart.Replace("\"index\":9", "\"index\":0"), End, Stop])), AnthropicMessagesFailure.MalformedStream);
        Failure(Terminal(await Collect([Start, FallbackStart, Delta(0, "text_delta", "ignored"), End, Stop])), AnthropicMessagesFailure.MalformedStream);
        Failure(Terminal(await Collect([Start, FallbackStart, FallbackStart.Replace("\"index\":0", "\"index\":1"), End, Stop],
            new(MaximumContentSlots: 1))), AnthropicMessagesFailure.ResourceLimit);
        foreach (var profiles in new System.Collections.Immutable.ImmutableArray<AnthropicFallbackModel>[]
        {
            [new("", "model", new())], [new("provider", "", new())], [new("provider", "model", new(-1))],
            [new("provider", "model", new()), new("provider", "model", new())], [new("provider", "\uD800", new())],
            [new("provider", "model", null!)], [null!]
        })
        {
            var rejected = false;
            try { _ = new AnthropicMessagesTransport((request, token) => Sequence([]), new(AllowedFallbackModels: profiles)); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid fallback profile was admitted.");
        }
        var tooMany = false;
        try { _ = new AnthropicMessagesTransport((request, token) => Sequence([]), new(MaximumContentSlots: 1,
            AllowedFallbackModels: [new("provider", "a", new()), new("provider", "b", new())])); }
        catch (ArgumentOutOfRangeException) { tooMany = true; }
        Assert(tooMany, "Fallback profile count was unbounded.");
    }
    private static string BlockStop(int index) => JsonSerializer.Serialize(new { type = "content_block_stop", index });
    private static string Delta(int index, string type, string value) => type switch
    {
        "text_delta" => JsonSerializer.Serialize(new { type = "content_block_delta", index, delta = new { type, text = value } }),
        "thinking_delta" => JsonSerializer.Serialize(new { type = "content_block_delta", index, delta = new { type, thinking = value } }),
        "signature_delta" => JsonSerializer.Serialize(new { type = "content_block_delta", index, delta = new { type, signature = value } }),
        _ => JsonSerializer.Serialize(new { type = "content_block_delta", index, delta = new { type, partial_json = value } })
    };
    private static async Task<List<StreamEvent>> Collect(string[] json, AnthropicMessagesOptions? options = null)
    {
        var result = new List<StreamEvent>();
        await foreach (var value in new AnthropicMessagesTransport((request, token) => Sequence(json.Select(JsonData.Parse).ToArray()), options).StreamAsync(Request()))
            result.Add(value);
        return result;
    }
    private static StreamTerminalEvent Terminal(List<StreamEvent> events)
    {
        Equal(1, events.OfType<StreamTerminalEvent>().Count());
        Assert(events[^1] is StreamTerminalEvent, "Terminal is not last."); return (StreamTerminalEvent)events[^1];
    }
    private static string Property(JsonFields? properties, string name)
    { Assert(properties is not null && properties.TryGet(name, out _), "Missing property: " + name); properties!.TryGet(name, out var value); return value!.ToString(); }
    private static void Failure(StreamTerminalEvent value, AnthropicMessagesFailure expected)
    {
        Assert(value is StreamError, "Failed stream returned success.");
        Equal(JsonSerializer.Serialize(expected.ToString()), Property(value.Message.ExtraProperties, "anthropicFailure"));
    }

    public static async Task SparseBlocksAndUsage()
    {
        var trace = new[] { Start.Replace("\"model\":\"fixture-model\"", "\"model\":\"actual-model\""), TextStart, ThinkingStart,
            Delta(9, "text_delta", "i"), Delta(2, "thinking_delta", "reason"), Delta(2, "signature_delta", "+"),
            ToolStart, Delta(99, "input_json_delta", "{\"path\":"), Delta(99, "input_json_delta", "\"file\",\"n\":9007199254740993}"),
            BlockStop(99), BlockStop(2), BlockStop(9), End, Stop };
        var rates = new AnthropicTokenRates(2_000_000, 3_000_000, 500_000, 1_500_000);
        var events = await Collect(trace, new(Rates: rates)); var done = Terminal(events);
        Assert(done is StreamDone, "Complete authored DTO trace failed."); Equal(StopReason.Stop, done.Reason);
        Equal(0, events.OfType<TextStarted>().Single().ContentIndex); Equal(1, events.OfType<ThinkingStarted>().Single().ContentIndex);
        Equal(2, events.OfType<ToolCallStarted>().Single().ContentIndex);
        Equal("Hi", ((TextContent)done.Message.Content[0]).Text); Equal("reason", ((ThinkingContent)done.Message.Content[1]).Thinking);
        Equal(JsonSerializer.Serialize("s+"), Property(done.Message.Content[1].ExtraProperties, "thinkingSignature"));
        // anthropic-messages.ts:803 parseStreamingJson is JSON.parse: the number is a binary64 Number, 2^53 + 1 prints as 9007199254740992.
        Equal("9007199254740992", ((ToolCallContent)done.Message.Content[2]).Arguments.Value.GetProperty("n").GetRawText());
        Equal("null", Property(done.Message.Content[2].ExtraProperties, "namespace"));
        Equal("fixture-model", done.Message.Model); Equal(1_700_000_000_000L, done.Message.Timestamp);
        Equal("\"actual-model\"", Property(done.Message.ExtraProperties, "responseModel")); Equal("\"message\"", Property(done.Message.ExtraProperties, "responseId"));
        Equal("\"end_turn\"", Property(done.Message.ExtraProperties, "rawStopReason"));
        Equal(11L, done.Message.Usage.Input); Equal(7L, done.Message.Usage.Output); Equal(25L, done.Message.Usage.TotalTokens);
        Equal("2", Property(done.Message.Usage.ExtraProperties, "cacheWrite1h")); Equal("3", Property(done.Message.Usage.ExtraProperties, "reasoning"));
        Equal(56.5m, done.Message.Usage.Cost.Total);
        var client = new ChatClient(new AnthropicMessagesTransport((request, token) => Sequence(trace.Select(JsonData.Parse).ToArray()), new(Rates: rates)), capacity: 1);
        var integrated = await client.CompleteAsync(Request()); Assert(integrated.Failure is null, "Shared ChatRun rejected complete mapper output.");
        Assert(JsonElement.DeepEquals(PiWireJson.WriteMessage(done.Message).Value, PiWireJson.WriteMessage(integrated.Message).Value), "ChatRun changed authoritative output.");
    }

    public static async Task SignaturesRedactionAndOwnership()
    {
        var text = "\u03C0\U0001F600e\u0301";
        var events = await Collect([Start, ThinkingStart, Delta(2, "thinking_delta", text), Delta(2, "signature_delta", "\u03BB"),
            BlockStop(2), """{"type":"content_block_start","index":8,"content_block":{"type":"redacted_thinking","data":"opaque","future":null}}""",
            BlockStop(8), End, Stop]);
        var first = events.OfType<ThinkingStarted>().First().Content; Equal("", first.Thinking); Equal("\"s\"", Property(first.ExtraProperties, "thinkingSignature"));
        var done = Terminal(events); Assert(done is StreamDone, "Reasoning trace failed.");
        var wire = PiWireJson.WriteMessage(done.Message).Value.GetProperty("content");
        Equal(text, wire[0].GetProperty("thinking").GetString()); Equal("s\u03BB", wire[0].GetProperty("thinkingSignature").GetString());
        Equal("[Reasoning redacted]", wire[1].GetProperty("thinking").GetString()); Equal("opaque", wire[1].GetProperty("thinkingSignature").GetString());
        Assert(wire[1].GetProperty("redacted").GetBoolean() && wire[1].GetProperty("future").ValueKind == JsonValueKind.Null, "Redacted or opaque properties lost.");
        var failed = Terminal(await Collect([Start, ThinkingStart, Delta(2, "signature_delta", "+")]));
        Failure(failed, AnthropicMessagesFailure.UnexpectedEof); Equal(JsonSerializer.Serialize("s+"), Property(failed.Message.Content[0].ExtraProperties, "thinkingSignature"));
        using var document = JsonDocument.Parse(TextStart); var owned = JsonData.FromElement(document.RootElement);
        document.Dispose(); var raw = owned.ToString();
        var output = new List<StreamEvent>();
        await foreach (var item in new AnthropicMessagesTransport((request, token) => Sequence([JsonData.Parse(Start), owned, JsonData.Parse(BlockStop(9)), JsonData.Parse(End), JsonData.Parse(Stop)])).StreamAsync(Request())) output.Add(item);
        Equal(raw, owned.ToString()); Equal("9007199254740993", ((TextContent)Terminal(output).Message.Content[0]).ExtraProperties!.Values["opaque"].Value.GetProperty("n").GetRawText());
    }

    public static async Task FinalArgumentsFollowParseStreamingJson()
    {
        var empty = Terminal(await Collect([Start, ToolStart, BlockStop(99), End, Stop]));
        Assert(empty is StreamDone, "Empty source scratch failed."); Equal("{}", ((ToolCallContent)empty.Message.Content[0]).Arguments.ToString());
        // Pi abe508 anthropic-messages.ts:803 finalizes with parseStreamingJson (json-parse.ts:104-124: JSON.parse, repairJson,
        // partial-json, then {}), so malformed, duplicate and non-object arguments complete the turn. Expected values are the installed
        // pi-ai 1.1.0 parseStreamingJson results; a lone surrogate is owned as U+FFFD (StreamingJson's documented representation limit).
        foreach (var (raw, expected) in new[]
        {
            ("{\"secret\":\"unfinished", "{\"secret\":\"unfinished\"}"), ("{\"n\":1,\"n\":2}", "{\"n\":2}"), ("[]", "[]"), ("null", "null"),
            ("{\"n\":1,}", "{\"n\":1}"), ("{\"x\":\"\\uD800\"}", "{\"x\":\"\\uFFFD\"}"), ("{\"x\":\"C:\\q\"}", "{\"x\":\"C:\\\\q\"}")
        })
        {
            var events = await Collect([Start, ToolStart, Delta(99, "input_json_delta", raw), BlockStop(99), End, Stop]);
            var end = Terminal(events); Assert(end is StreamDone, "parseStreamingJson final failed the turn: " + raw);
            Equal(1, events.OfType<ToolCallEnded>().Count());
            Assert(JsonElement.DeepEquals(JsonData.Parse(expected).Value, ((ToolCallContent)end.Message.Content[0]).Arguments.Value),
                "Final arguments differ from parseStreamingJson: " + raw);
        }
        var complete = Terminal(await Collect([Start, ToolStart, Delta(99, "input_json_delta", "{\"x\":null,\"u\":\"\\u03C0\\uD83D\\uDE00\"}"), BlockStop(99), End, Stop]));
        Assert(complete is StreamDone, "Complete Unicode object rejected.");
        var arguments = ((ToolCallContent)complete.Message.Content[0]).Arguments.Value;
        Equal(JsonValueKind.Null, arguments.GetProperty("x").ValueKind); Equal("\u03C0\U0001F600", arguments.GetProperty("u").GetString());
    }

    public static async Task UsagePresenceAndNumberProfile()
    {
        var nulls = """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":null,"output_tokens":null,"cache_read_input_tokens":null,"cache_creation_input_tokens":null,"cache_creation":{"ephemeral_1h_input_tokens":null},"output_tokens_details":{"thinking_tokens":null}}}""";
        var kept = Terminal(await Collect([Start, nulls, Stop])); Assert(kept is StreamDone, "Nullable usage preservation failed.");
        Equal(11L, kept.Message.Usage.Input); Equal(1L, kept.Message.Usage.Output); Equal(19L, kept.Message.Usage.TotalTokens);
        Assert(!kept.Message.Usage.ExtraProperties!.TryGet("reasoning", out _), "Absent reasoning became zero.");
        foreach (var replacement in new[] { "\"model\":\"\"", "\"model\":null" })
        {
            var model = Terminal(await Collect([Start.Replace("\"model\":\"fixture-model\"", replacement), nulls, Stop]));
            Assert(model is StreamDone && !model.Message.ExtraProperties!.TryGet("responseModel", out _), "Empty proxy model invented an override.");
        }
        var zeros = """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,"cache_creation":{"ephemeral_1h_input_tokens":0},"output_tokens_details":{"thinking_tokens":0}}}""";
        var zero = Terminal(await Collect([Start, zeros, Stop])); Assert(zero is StreamDone, "Explicit usage zero failed.");
        Equal(0L, zero.Message.Usage.TotalTokens); Equal("0", Property(zero.Message.Usage.ExtraProperties, "reasoning")); Equal(0m, zero.Message.Usage.Cost.Total);
        foreach (var token in new[] { "-1", "1.0", "1e0", "9223372036854775808", "\"1\"" })
        {
            var bad = Start.Replace("\"input_tokens\":11", "\"input_tokens\":" + token);
            Failure(Terminal(await Collect([bad, End, Stop])), AnthropicMessagesFailure.MalformedStream);
        }
        var mismatch = End.Replace("\"thinking_tokens\":3", "\"thinking_tokens\":8");
        Failure(Terminal(await Collect([Start, mismatch, Stop])), AnthropicMessagesFailure.MalformedStream);
        Failure(Terminal(await Collect([Start.Replace("\"ephemeral_1h_input_tokens\":2", "\"ephemeral_1h_input_tokens\":6"), End, Stop])), AnthropicMessagesFailure.MalformedStream);
    }

    public static async Task StopReasonsAndProtocolFailures()
    {
        foreach (var (wire, expected) in new[] { ("end_turn", StopReason.Stop), ("pause_turn", StopReason.Stop), ("stop_sequence", StopReason.Stop), ("max_tokens", StopReason.Length), ("tool_use", StopReason.ToolUse) })
        {
            var terminal = Terminal(await Collect([Start, End.Replace("end_turn", wire), Stop]));
            Assert(terminal is StreamDone, "Known stop reason rejected."); Equal(expected, terminal.Reason); Equal(JsonSerializer.Serialize(wire), Property(terminal.Message.ExtraProperties, "rawStopReason"));
        }
        var refusal = """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":{"explanation":"Cannot complete."}},"usage":{"output_tokens":2}}""";
        var refused = Terminal(await Collect([Start, refusal, Stop])); Failure(refused, AnthropicMessagesFailure.ProviderError);
        Equal("\"Cannot complete.\"", Property(refused.Message.ExtraProperties, "errorMessage")); Equal(2L, refused.Message.Usage.Output);
        Equal("\"The model refused to complete the request\"", Property(Terminal(await Collect([Start, End.Replace("end_turn", "refusal"), Stop])).Message.ExtraProperties, "errorMessage"));
        Equal("\"Provider stopped with: sensitive\"", Property(Terminal(await Collect([Start, End.Replace("end_turn", "sensitive"), Stop])).Message.ExtraProperties, "errorMessage"));
        Failure(Terminal(await Collect([Start, End.Replace("end_turn", "future"), Stop])), AnthropicMessagesFailure.MalformedStream);
        foreach (var trace in new[] { Array.Empty<string>(), new[] { Start }, new[] { Start, End }, new[] { Start, Stop } })
            Failure(Terminal(await Collect(trace)), AnthropicMessagesFailure.UnexpectedEof);
        foreach (var trace in new[]
        {
            new[] { Start, Start }, new[] { TextStart }, new[] { Start, TextStart, TextStart },
            new[] { Start, TextStart, Delta(9, "thinking_delta", "wrong") },
            new[] { Start, Delta(42, "text_delta", "unknown") }, new[] { Start, TextStart, End, Stop },
            new[] { Start, End, Stop, End },
            new[] { Start, TextStart, """{"type":"content_block_start","index":0,"content_block":{"type":"fallback","model":"other"}}""" }
        }) Failure(Terminal(await Collect(trace)), AnthropicMessagesFailure.MalformedStream);
        // Reported transformations are diagnostics (Pi abe508): a non-array is ignored and entry values are copied unchecked.
        var tolerated = Terminal(await Collect([Start, """{"type":"message_delta","delta":{},"input_transformations":[{"type":1}]}""",
            """{"type":"message_delta","delta":{},"input_transformations":{"type":"unsupported"}}""", End, Stop]));
        Assert(tolerated is StreamDone, "Transformations failed the stream.");
        Assert(Property(tolerated.Message.ExtraProperties, "diagnostics").Contains("\"transformations\":[{\"type\":1}]", StringComparison.Ordinal), "Transformation not copied.");
        Failure(Terminal(await Collect([Start, """{"type":"error","error":{"type":"overloaded_error","message":"private-provider-payload"}}"""])), AnthropicMessagesFailure.ProviderError);
    }

    public static async Task LimitsAndStrictDtoAdmission()
    {
        var signature = new string('s', 5);
        var cases = new (AnthropicMessagesOptions Options, string[] Trace)[]
        {
            (new(MaximumEvents: 2), [Start, End, Stop]),
            (new(MaximumEventCharacters: Start.Length - 1), [Start]),
            (new(MaximumInputCharacters: Start.Length - 1), [Start]),
            (new(MaximumContentSlots: 1), [Start, TextStart, ThinkingStart]),
            (new(MaximumSignatureCharacters: 4), [Start, ThinkingStart, Delta(2, "signature_delta", signature)]),
            (new(MaximumJsonDepth: 2), [Start])
        };
        foreach (var (options, trace) in cases) Failure(Terminal(await Collect(trace, options)), AnthropicMessagesFailure.ResourceLimit);
        var exact = Terminal(await Collect([Start, End, Stop], new(MaximumEvents: 3, MaximumEventCharacters: Start.Length, MaximumInputCharacters: Start.Length + End.Length + Stop.Length)));
        Assert(exact is StreamDone, "Exact raw DTO bounds rejected.");
        foreach (var invalid in new[] { "{\"type\":\"ping\",\"opaque\":\"\\uD800\"}", "[]" })
            Failure(Terminal(await Collect([invalid])), AnthropicMessagesFailure.MalformedStream);
        // JsonData itself rejects this decoded property name while the source factory
        // constructs its owned DTO. No malformed DTO reaches the mapper's Process boundary.
        Failure(Terminal(await Collect(["{\"type\":\"ping\",\"\\uDC00\":null}"])), AnthropicMessagesFailure.SourceFailed);
        foreach (var raw in new[] { "{\"type\":\"ping\",}", "{\"type\":\"ping\",\"opaque\":{\"n\":1,}}", "{\"type\":\"ping\",/*private*/\"opaque\":1}" })
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var source = JsonData.FromElement(document.RootElement); var events = new List<StreamEvent>();
            await foreach (var item in new AnthropicMessagesTransport((request, token) => Sequence([source])).StreamAsync(Request())) events.Add(item);
            Failure(Terminal(events), AnthropicMessagesFailure.MalformedStream);
        }
        foreach (var options in new[] { new AnthropicMessagesOptions(MaximumEvents: 0), new(MaximumJsonDepth: 65), new(Rates: new(-1)) })
            await Throws<ArgumentOutOfRangeException>(() => { _ = new AnthropicMessagesTransport((request, token) => Sequence([]), options); return Task.CompletedTask; });
    }

    public static async Task OwnedCleanupAndFaults()
    {
        var source = new Probe([JsonData.Parse(Start), JsonData.Parse(End), JsonData.Parse(Stop)]) { HoldDisposal = true };
        await using (var reader = new AnthropicMessagesTransport((request, token) => source).StreamAsync(Request()).GetAsyncEnumerator())
        {
            Assert(await reader.MoveNextAsync() && reader.Current is StreamStarted, "Start missing.");
            var terminal = reader.MoveNextAsync().AsTask(); await source.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!terminal.IsCompleted, "Terminal escaped before cleanup."); source.DisposalRelease.SetResult();
            Assert(await terminal && reader.Current is StreamDone, "Done missing after cleanup."); Equal(1, source.Disposals);
        }
        var early = new Probe([]) { HoldDisposal = true };
        var abandoned = new AnthropicMessagesTransport((request, token) => early).StreamAsync(Request()).GetAsyncEnumerator();
        await abandoned.MoveNextAsync(); var disposal = abandoned.DisposeAsync().AsTask();
        await early.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert(!disposal.IsCompleted, "Early disposal did not await source cleanup.");
        Equal(0, early.Pulls); early.DisposalRelease.SetResult(); await disposal; Equal(1, early.Disposals);
        foreach (var error in new Exception[] { new Exception("private-source"), new InvalidOperationException("private-source-state"),
            new JsonException("private-source-json"), new OperationCanceledException("private-foreign-cancellation") })
        {
            var faulty = new Probe([]) { ReadFailure = error, DisposalFailure = new Exception("private-cleanup") };
            var events = new List<StreamEvent>();
            await foreach (var item in new AnthropicMessagesTransport((request, token) => faulty).StreamAsync(Request())) events.Add(item);
            var failed = Terminal(events); Failure(failed, AnthropicMessagesFailure.SourceFailed); Equal(1, faulty.Disposals);
            Assert(!Property(failed.Message.ExtraProperties, "errorMessage").Contains("private", StringComparison.Ordinal), "Source exception leaked.");
        }
        var cleanup = new Probe([JsonData.Parse(Start), JsonData.Parse(End), JsonData.Parse(Stop)]) { DisposalFailure = new Exception("private-cleanup") };
        var collected = new List<StreamEvent>();
        await foreach (var item in new AnthropicMessagesTransport((request, token) => cleanup).StreamAsync(Request())) collected.Add(item);
        Failure(Terminal(collected), AnthropicMessagesFailure.CleanupFailed); Equal(1, cleanup.Disposals);
        var factory = new AnthropicMessagesTransport((request, token) => throw new Exception("private-factory"));
        var acquisition = new List<StreamEvent>(); await foreach (var item in factory.StreamAsync(Request())) acquisition.Add(item);
        Failure(Terminal(acquisition), AnthropicMessagesFailure.SourceFailed); Equal(0, acquisition.OfType<StreamStarted>().Count());
    }

    public static async Task CurrentSourceFailureBoundary()
    {
        foreach (var error in new Exception[] { new InvalidOperationException("private-current-state"),
            new JsonException("private-current-json"), new ArgumentException("private-current-argument"),
            new FormatException("private-current-format"), new OverflowException("private-current-overflow"),
            new KeyNotFoundException("private-current-key"), new OperationCanceledException("private-foreign-current-cancel") })
        {
            var source = new Probe([JsonData.Parse(Start)])
            { CurrentFailure = error, DisposalFailure = new InvalidOperationException("private-disposal") };
            var events = new List<StreamEvent>();
            await foreach (var item in new AnthropicMessagesTransport((_, _) => source).StreamAsync(Request())) events.Add(item);
            var failed = Terminal(events);
            Failure(failed, AnthropicMessagesFailure.SourceFailed);
            Equal(1, events.OfType<StreamStarted>().Count()); Equal(1, source.Pulls); Equal(1, source.Disposals);
            Equal(0, failed.Message.Content.Length);
            Assert(!Property(failed.Message.ExtraProperties, "errorMessage").Contains("private", StringComparison.Ordinal), "Current/cleanup exception leaked.");
        }
        // A successfully acquired malformed DTO remains a protocol error, not a source error.
        Failure(Terminal(await Collect(["""{"type":"unknown-private-event"}"""])), AnthropicMessagesFailure.MalformedStream);
    }

    public static async Task CurrentFailureAwaitedCleanupAndCancellation()
    {
        var source = new Probe([JsonData.Parse(Start)])
        { CurrentFailure = new InvalidOperationException("private-current"), HoldDisposal = true,
            DisposalFailure = new InvalidOperationException("private-cleanup") };
        await using (var iterator = new AnthropicMessagesTransport((_, _) => source).StreamAsync(Request()).GetAsyncEnumerator())
        {
            Assert(await iterator.MoveNextAsync() && iterator.Current is StreamStarted, "Initial start missing.");
            var terminal = iterator.MoveNextAsync().AsTask();
            await source.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!terminal.IsCompleted, "Current failure emitted a terminal before awaited cleanup.");
            source.DisposalRelease.TrySetResult();
            Assert(await terminal, "Current failure lost terminal after cleanup.");
            Failure((StreamTerminalEvent)iterator.Current, AnthropicMessagesFailure.SourceFailed);
            Equal(1, source.Disposals); Assert(!await iterator.MoveNextAsync(), "More than one terminal emitted.");
        }
        foreach (var throwFromGetter in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var canceled = new Probe([JsonData.Parse(Start)])
            { OnCurrent = () => cancellation.Cancel(), CurrentFailure = throwFromGetter ? new JsonException("private-current-cancel") : null,
                HoldDisposal = true, DisposalFailure = new Exception("private-cleanup") };
            await using var iterator = new AnthropicMessagesTransport((_, _) => canceled).StreamAsync(Request(), cancellation.Token).GetAsyncEnumerator();
            await iterator.MoveNextAsync(); var terminal = iterator.MoveNextAsync().AsTask();
            await canceled.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!terminal.IsCompleted, "Getter cancellation skipped source cleanup.");
            canceled.DisposalRelease.TrySetResult(); Assert(await terminal, "Getter cancellation lost aborted terminal.");
            var aborted = (StreamTerminalEvent)iterator.Current;
            Failure(aborted, AnthropicMessagesFailure.Cancelled); Equal(StopReason.Aborted, aborted.Reason);
            Equal(cancellation.Token, canceled.Token); Equal(1, canceled.Disposals);
            Assert(aborted.Message.ExtraProperties?.TryGet("responseId", out _) != true,
                "Cancellation during Current still processed the acquired DTO.");
        }
    }

    public static async Task CancellationAndSharedReducer()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var factories = 0;
        var untouched = new AnthropicMessagesTransport((request, token) => { factories++; return Sequence([]); });
        await Throws<OperationCanceledException>(async () => { await foreach (var _ in untouched.StreamAsync(Request(), before.Token)) { } }); Equal(0, factories);
        using var during = new CancellationTokenSource(); var source = new Probe([]) { HoldRead = true, HoldDisposal = true };
        await using (var reader = new AnthropicMessagesTransport((request, token) => source).StreamAsync(Request(), during.Token).GetAsyncEnumerator())
        {
            await reader.MoveNextAsync(); var pending = reader.MoveNextAsync().AsTask(); await source.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(during.Token, source.Token); during.Cancel(); await source.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!pending.IsCompleted, "Cancelled terminal escaped before cleanup."); source.DisposalRelease.SetResult();
            Assert(await pending, "Cancelled terminal missing."); Failure((StreamTerminalEvent)reader.Current, AnthropicMessagesFailure.Cancelled);
            Equal(StopReason.Aborted, ((StreamTerminalEvent)reader.Current).Reason);
        }
        var longDelta = Delta(9, "text_delta", new string('x', 512));
        var bounded = await new ChatClient(new AnthropicMessagesTransport((request, token) => Sequence(new[] { Start, TextStart, longDelta, BlockStop(9), End, Stop }.Select(JsonData.Parse).ToArray())),
            capacity: 1, limits: new(MaximumCharacters: 256)).CompleteAsync(Request());
        Equal(ChatFailureKind.ResourceLimit, bounded.Failure!.Kind);
        var adapterBound = await new ChatClient(new AnthropicMessagesTransport((request, token) => Sequence(new[] { Start, TextStart, longDelta }.Select(JsonData.Parse).ToArray()),
            new(MaximumContentCharacters: 384)), capacity: 1).CompleteAsync(Request());
        Equal(ChatFailureKind.Provider, adapterBound.Failure!.Kind); Equal("\"ResourceLimit\"", Property(adapterBound.Message.ExtraProperties, "anthropicFailure"));
        using var settledCancellation = new CancellationTokenSource(); var waiting = new Probe([]) { HoldRead = true, HoldDisposal = true };
        await using var run = await new ChatClient(new AnthropicMessagesTransport((request, token) => waiting), capacity: 1).StartWithAbortSettlementAsync(Request(), settledCancellation.Token);
        var drain = Task.Run(async () => { var events = new List<StreamEvent>(); await foreach (var item in run.ReadEventsAsync()) events.Add(item); return events; });
        await waiting.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); settledCancellation.Cancel();
        await waiting.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert(!run.Completion.IsCompleted, "ChatRun cancellation skipped adapter cleanup.");
        waiting.DisposalRelease.SetResult(); var frames = await drain; Equal(1, frames.OfType<StreamTerminalEvent>().Count());
        var result = await run.Completion; Equal(ChatFailureKind.Cancelled, result.Failure!.Kind); Equal("\"Cancelled\"", Property(result.Message.ExtraProperties, "anthropicFailure"));
    }

    private static async IAsyncEnumerable<JsonData> Sequence(JsonData[] values, [EnumeratorCancellation] CancellationToken token = default)
    { await Task.CompletedTask; foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return value; } }
    private sealed class Probe(JsonData[] values) : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>
    {
        private int _position = -1;
        public int Pulls; public int Disposals; public CancellationToken Token;
        public bool HoldRead; public bool HoldDisposal; public Exception? ReadFailure; public Exception? DisposalFailure;
        public Exception? CurrentFailure; public Action? OnCurrent;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonData Current { get { OnCurrent?.Invoke(); if (CurrentFailure is not null) throw CurrentFailure; return values[_position]; } }
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken cancellationToken = default) { Token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            Pulls++; ReadEntered.TrySetResult();
            if (HoldRead) await Task.Delay(Timeout.Infinite, Token);
            if (ReadFailure is not null) throw ReadFailure;
            return ++_position < values.Length;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++; DisposalEntered.TrySetResult(); if (HoldDisposal) await DisposalRelease.Task;
            if (DisposalFailure is not null) throw DisposalFailure;
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; observed {actual}."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { } }
}
