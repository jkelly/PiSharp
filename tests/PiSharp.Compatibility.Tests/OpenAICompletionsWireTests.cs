using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

internal static class OpenAICompletionsWireTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Completions authored interleaved text thinking indexed tools exact finals reducer and ChatRun", CompletedInterleaving),
        ("Completions source stop aliases EOF compatibility and cache usage precedence", StopAndUsage),
        ("Completions parseStreamingJson finals and unsupported partial identities without authority", StrictAndUnsupported),
        ("Completions raw DTO depth Unicode count and logical payload bounds", LimitsAndAdmission),
        ("Completions factory Current read disposal failures preserve distinct sanitized owned boundaries", OwnershipAndFaults),
        ("Completions cancellation pull pacing cleanup and final-event race compose with ChatRun", CancellationAndChatRun)
    ];

    private static readonly string[] Completed =
    [
        """{"object":"chat.completion.chunk","id":"resp-first","model":"actual-model","choices":[{"delta":{"role":"assistant","content":"Hi ","reasoning_content":"plan ","reasoning":"duplicate ignored","tool_calls":[{"index":9,"id":"call-a","type":"function","function":{"name":"read","arguments":"{\"path\":\"a"}},{"index":3,"id":"call-b","type":"function","function":{"name":"sum","arguments":"{\"n\":"}}]}}]}""",
        """{"id":"resp-later","model":"later-model","choices":[{"delta":{"content":"there","reasoning_text":"more","tool_calls":[{"index":3,"function":{"arguments":"1.00}"}},{"index":9,"function":{"arguments":"\\nb\"}"}}]}}]}""",
        """{"choices":[{"finish_reason":"tool_calls","delta":{}}]}""",
        """{"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":7,"total_tokens":999,"cached_tokens":19,"prompt_cache_hit_tokens":18,"prompt_tokens_details":{"cached_tokens":2,"cache_write_tokens":3},"completion_tokens_details":{"reasoning_tokens":1}},"ignoredMetadata":{"explicitNull":null,"ordered":[2,1]}}"""
    ];

    private static ChatRequest Request(string provider = "fixture-provider") =>
        new(new("requested-model", "openai-completions", provider), [], 123);

    private static async Task CompletedInterleaving()
    {
        var source = new Probe(Completed.Select(JsonData.Parse).ToArray());
        var mapper = new OpenAICompletionsWireSource((request, token) => source,
            new(Rates: new(1_000_000, 2_000_000, 500_000, 1_500_000)));
        var frames = await Collect(mapper);
        Equal(1, source.Disposals); Equal(Completed.Length + 1, source.Pulls);
        var expectedOperations = new[]
        {
            "start", "text_start:0", "text_delta:0", "thinking_start:1", "thinking_delta:1",
            "toolcall_start:2", "toolcall_delta:2", "toolcall_start:3", "toolcall_delta:3",
            "text_delta:0", "thinking_delta:1", "toolcall_delta:3", "toolcall_delta:2",
            "text_end:0", "thinking_end:1", "toolcall_end:2", "toolcall_end:3", "done"
        };
        Check(expectedOperations.SequenceEqual(frames.Select(Operation)), "Source-informed operation/index order differs.");
        var terminal = (StreamDone)frames[^1]; Equal(StopReason.ToolUse, terminal.Reason);
        // Pi abe508 openai-completions.ts:464 finishCurrentBlock: block.arguments = parseStreamingJson(block.partialArgs), a JSON.parse
        // whose Number 1.00 is 1 (installed pi-ai 1.1.0 prints {"n":1}).
        var expected = JsonData.Parse("""
            {"role":"assistant","api":"openai-completions","provider":"fixture-provider","model":"requested-model","timestamp":123,
             "stopReason":"toolUse","responseId":"resp-first","responseModel":"actual-model","rawStopReason":"tool_calls",
             "content":[{"type":"text","text":"Hi there"},{"type":"thinking","thinking":"plan more","thinkingSignature":"reasoning_content"},
                {"type":"toolCall","id":"call-a","name":"read","arguments":{"path":"a\nb"}},
                {"type":"toolCall","id":"call-b","name":"sum","arguments":{"n":1}}],
             "usage":{"input":15,"output":7,"cacheRead":2,"cacheWrite":3,"reasoning":1,"totalTokens":27,
                "cost":{"input":15,"output":14,"cacheRead":1,"cacheWrite":4.5,"total":34.5}}}
            """);
        Check(Same(expected.Value, PiWireJson.WriteMessage(terminal.Message).Value), "Complete authored final message fields differ.");
        var reducer = new AssistantStreamReducer(((StreamStarted)frames[0]).Partial);
        foreach (var frame in frames)
        {
            reducer.Apply(frame);
            if (frame is ToolCallDelta { ContentIndex: 3, Delta: "1.00}" })
            {
                Equal("{\"n\":1.00}", reducer.GetToolJsonPreview(3));
                Equal("{}", ((ToolCallContent)reducer.Snapshot().Content[3]).Arguments.ToString());
            }
        }
        Check(Same(expected.Value, PiWireJson.WriteMessage(reducer.Snapshot()).Value), "Native authoritative reducer integration differs.");
        var result = await new ChatClient(new OpenAICompletionsWireSource((_, _) => Sequence(Completed),
            new(Rates: new(1_000_000, 2_000_000, 500_000, 1_500_000))), capacity: 1).CompleteAsync(Request());
        Check(result.Failure is null && Same(expected.Value, PiWireJson.WriteMessage(result.Message).Value),
            "Native bounded ChatRun composition lost final fields.");
        Check(!Same(JsonData.Parse("""{"n":1}""").Value, JsonData.Parse("""{"n":1.00}""").Value),
            "Comparison weakened numeric token identity.");
        Check(!Same(JsonData.Parse("""{"a":null}""").Value, JsonData.EmptyObject.Value), "Comparison erased null/absence.");
        Check(!Same(JsonData.Parse("[1,2]").Value, JsonData.Parse("[2,1]").Value), "Comparison erased array order.");
        var noIndex = await Collect(new OpenAICompletionsWireSource((_, _) => Sequence([
            ToolChunk(new { id = "no-index", function = new { name = "read", arguments = "{\"n\":" } }),
            ToolChunk(new { index = 7, id = "no-index", function = new { arguments = "2}" } }), Finish("tool_calls")])));
        Equal(StopReason.ToolUse, Terminal(noIndex).Reason);
        Equal(2, ((ToolCallContent)Terminal(noIndex).Message.Content[0]).Arguments.Value.GetProperty("n").GetInt32());
    }

    private static async Task StopAndUsage()
    {
        foreach (var (wire, reason) in new[]
        {
            ("stop", StopReason.Stop), ("end", StopReason.Stop), ("length", StopReason.Length),
            ("function_call", StopReason.ToolUse), ("tool_calls", StopReason.ToolUse),
            ("content_filter", StopReason.Error), ("network_error", StopReason.Error), ("private-unknown-stop", StopReason.Error)
        })
        {
            var terminal = Terminal(await Collect([Text("answer"), Finish(wire)]));
            Equal(reason, terminal.Reason); Equal(wire, Property(terminal.Message.ExtraProperties, "rawStopReason"));
            Equal("answer", ((TextContent)terminal.Message.Content[0]).Text);
            if (reason == StopReason.Error)
            {
                Failed(terminal, OpenAICompletionsWireFailure.ProviderError);
                Check(!Property(terminal.Message.ExtraProperties, "errorMessage").Contains("private", StringComparison.Ordinal),
                    "Unknown provider reason was copied into fixed diagnostic.");
            }
        }
        Failed(Terminal(await Collect([Text("partial")])), OpenAICompletionsWireFailure.UnexpectedEof);
        var inferred = Terminal(await Collect([Text("answer")], new(SupportsFinishReason: false)));
        Check(inferred is StreamDone && inferred.Reason == StopReason.Stop, "Explicit no-finish capability was ignored.");
        var inferredTool = Terminal(await Collect([ToolChunk(new { index = 4, id = "call", function = new { name = "read", arguments = "{}" } })],
            new(SupportsFinishReason: false)));
        Equal(StopReason.ToolUse, inferredTool.Reason);
        var opencode = Terminal(await Collect([
            """{"choices":[{"delta":{"reasoning":"visible","reasoning_text":"duplicate"}}]}""", Finish("stop")], provider: "opencode-go"));
        Equal("reasoning_content", Property(opencode.Message.Content[0].ExtraProperties, "thinkingSignature"));
        foreach (var (usage, input, read, write) in new[]
        {
            ("""{"prompt_tokens":20,"completion_tokens":7,"cached_tokens":2}""", 18L, 2L, 0L),
            ("""{"prompt_tokens":20,"completion_tokens":7,"cached_tokens":2,"prompt_cache_hit_tokens":4}""", 16L, 4L, 0L),
            ("""{"prompt_tokens":20,"completion_tokens":7,"cached_tokens":2,"prompt_cache_hit_tokens":4,"prompt_tokens_details":{"cached_tokens":0,"cache_write_tokens":3}}""", 17L, 0L, 3L),
            ("""{"prompt_tokens":20,"completion_tokens":7,"cached_tokens":"ignored","prompt_cache_hit_tokens":"ignored","prompt_tokens_details":{"cached_tokens":0,"cache_write_tokens":3}}""", 17L, 0L, 3L),
            ("""{"prompt_tokens":20,"completion_tokens":7,"cached_tokens":2,"prompt_cache_hit_tokens":null,"prompt_tokens_details":{"cached_tokens":null}}""", 18L, 2L, 0L),
            ("""{"prompt_tokens":1,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":2,"cache_write_tokens":3}}""", 0L, 2L, 3L)
        })
        {
            var chunk = """{"choices":[{"usage":""" + usage + ""","delta":{}}]}""";
            var result = Terminal(await Collect([chunk, Finish("stop")])).Message.Usage;
            Equal(input, result.Input); Equal(read, result.CacheRead); Equal(write, result.CacheWrite);
            Equal(input + 7 + read + write, result.TotalTokens);
            Equal("0", Property(result.ExtraProperties, "reasoning"));
        }
        var priority = Terminal(await Collect([
            """{"usage":{},"choices":[{"usage":{"prompt_tokens":99},"delta":{},"finish_reason":"stop"}]}"""])).Message.Usage;
        Equal(0L, priority.Input); Equal("0", Property(priority.ExtraProperties, "reasoning"));
        var missing = Terminal(await Collect([Finish("stop")])).Message.Usage;
        Check(missing.ExtraProperties?.TryGet("reasoning", out _) != true, "Absent complete usage became a reported reasoning zero.");
        foreach (var count in new[] { "1.0", "1e0", "-1", "9007199254740992", "\"1\"" })
            Failed(Terminal(await Collect(["{\"usage\":{\"prompt_tokens\":" + count + "},\"choices\":[]}", Finish("stop")])),
                OpenAICompletionsWireFailure.MalformedStream);
        var safe = Terminal(await Collect(["""{"usage":{"prompt_tokens":9007199254740991},"choices":[]}""", Finish("stop")])).Message.Usage;
        Equal(9_007_199_254_740_991L, safe.Input); Equal(safe.Input, safe.TotalTokens);
    }

    private static async Task StrictAndUnsupported()
    {
        // Pi abe508 openai-completions.ts:464 finalizes every tool call with parseStreamingJson (json-parse.ts:104-124), so partial,
        // duplicate, invalid-escape, out-of-range and non-object arguments end the call and the turn completes. Expected values are the
        // installed pi-ai 1.1.0 results as JSON.stringify writes them (1e999 is Infinity, written null); a lone surrogate is owned as
        // U+FFFD (StreamingJson's documented representation limit).
        foreach (var (raw, expected) in new[]
        {
            ("{\"path\":\"private", "{\"path\":\"private\"}"), ("{\"n\":1,\"n\":2}", "{\"n\":2}"), ("[1]", "[1]"), ("null", "null"),
            ("{\"path\":\"\\q\"}", "{\"path\":\"\\\\q\"}"), ("{\"n\":1e999}", "{\"n\":null}"), ("{\"s\":\"\\ud800\"}", "{\"s\":\"\\uFFFD\"}")
        })
        {
            var frames = await Collect([ToolChunk(new { index = 9, id = "call", function = new { name = "read", arguments = raw } }),
                Finish("tool_calls")]);
            Check(Terminal(frames) is StreamDone { Reason: StopReason.ToolUse } && frames.OfType<ToolCallEnded>().Count() == 1,
                "parseStreamingJson final failed the turn: " + raw);
            Check(Same(JsonData.Parse(expected).Value, ((ToolCallContent)Terminal(frames).Message.Content[0]).Arguments.Value),
                "Final arguments differ from parseStreamingJson: " + raw);
        }
        foreach (var unsupported in new[]
        {
            ToolChunk(new { index = 9, id = "call", type = "custom", custom = new { name = "read", input = "private" } }),
            """{"choices":[{"delta":{"content":[{"type":"image_url","image_url":{"url":"private"}}]}}]}""",
            """{"choices":[{"delta":{"function_call":{"name":"read","arguments":"{}"}}}]}""",
            """{"choices":[{"delta":{"audio":{"data":"private"}}}]}""",
            """{"choices":[{"delta":{"refusal":"private"}}]}""",
            """{"choices":[{"delta":{"new_content_type":{"value":"private"}}}]}"""
        })
            Failed(Terminal(await Collect([unsupported, Finish("stop")])), OpenAICompletionsWireFailure.UnsupportedFeature);
        // Qualified whole-source observations replace these two former unsupported
        // controls; incomplete identity and executable arguments remain fail closed.
        var provisional = await Collect([
            ToolChunk(new { index = 9, function = new { arguments = "{\"n\":" } }),
            ToolChunk(new { index = 9, id = "filled", function = new { name = "read", arguments = "1}" } }), Finish("tool_calls")]);
        Check(Terminal(provisional) is StreamDone && provisional.OfType<ToolCallProvisionalStarted>().Count() == 1 &&
            provisional.OfType<ToolCallHeaderUpdated>().Count() == 1 && provisional.OfType<ToolCallEnded>().Count() == 1,
            "Indexed provisional identity was delayed, invented or never finalized.");
        var filled = (ToolCallContent)Terminal(provisional).Message.Content[0];
        Equal("filled", filled.Id); Equal("read", filled.Name); Equal("{\"n\":1}", filled.Arguments.ToString());
        var neverFilled = await Collect([ToolChunk(new { index = 9, function = new { arguments = "{}" } }), Finish("tool_calls")]);
        Failed(Terminal(neverFilled), OpenAICompletionsWireFailure.MalformedStream);
        Equal(0, neverFilled.OfType<ToolCallEnded>().Count()); Equal(0, neverFilled.OfType<StreamDone>().Count());
        var encrypted = Terminal(await Collect([
            """{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.encrypted","data":"private"}]}}]}""", Finish("stop")]));
        Check(encrypted is StreamDone && encrypted.Message.Content[0] is ThinkingContent { Thinking: "" },
            "Valid encrypted replay detail was rejected or emitted as visible thinking.");
        Equal("[{\"type\":\"reasoning.encrypted\",\"data\":\"private\"}]",
            Property(encrypted.Message.Content[0].ExtraProperties, "thinkingSignature"));
        var conflict = await Collect([
            ToolChunk(new { index = 9, id = "one", function = new { name = "read", arguments = "{}" } }),
            ToolChunk(new { index = 3, id = "two", function = new { name = "read", arguments = "{}" } }),
            ToolChunk(new { index = 9, id = "two", function = new { arguments = "" } }), Finish("tool_calls")]);
        Failed(Terminal(conflict), OpenAICompletionsWireFailure.MalformedStream);
        var validPrefix = await Collect([
            """{"choices":[{"delta":{"content":"observed prefix","tool_calls":[{"index":1,"id":"call","function":{"name":"read","arguments":9}}]}}]}"""]);
        Failed(Terminal(validPrefix), OpenAICompletionsWireFailure.MalformedStream);
        Equal("observed prefix", validPrefix.OfType<TextDelta>().Single().Delta);
        Equal(0, validPrefix.OfType<TextEnded>().Count()); Equal(0, validPrefix.OfType<ToolCallEnded>().Count());
        var prefixReducer = new AssistantStreamReducer(((StreamStarted)validPrefix[0]).Partial);
        foreach (var frame in validPrefix) prefixReducer.Apply(frame);
        Equal("observed prefix", ((TextContent)prefixReducer.Snapshot().Content[0]).Text);
        var invalidUsage = await Collect([
            """{"usage":{"completion_tokens":1,"completion_tokens_details":{"reasoning_tokens":2}},"choices":[]}""", Finish("stop")]);
        Failed(Terminal(invalidUsage), OpenAICompletionsWireFailure.MalformedStream);
        Failed(Terminal(await Collect(["""{"error":{"message":"private-provider","code":"private"}}"""])),
            OpenAICompletionsWireFailure.ProviderError);
    }

    private static async Task LimitsAndAdmission()
    {
        var text = Text("answer"); var end = Finish("stop");
        foreach (var (options, chunks) in new (OpenAICompletionsWireOptions, string[])[]
        {
            (new(MaximumChunks: 1), [text, end]),
            (new(MaximumChunkCharacters: text.Length - 1), [text]),
            (new(MaximumInputCharacters: text.Length + end.Length - 1), [text, end]),
            (new(MaximumContentSlots: 1), ["""{"choices":[{"delta":{"content":"x","reasoning":"y"}}]}"""]),
            (new(MaximumContentCharacters: 64), [Text(new string('x', 100))]),
            (new(MaximumJsonDepth: 2), [text])
        })
            Failed(Terminal(await Collect(chunks, options)), OpenAICompletionsWireFailure.ResourceLimit);
        var exact = Terminal(await Collect([text, end], new(MaximumChunks: 2,
            MaximumChunkCharacters: Math.Max(text.Length, end.Length), MaximumInputCharacters: text.Length + end.Length)));
        Check(exact is StreamDone, "Exact raw admission budget rejected.");
        foreach (var malformed in new[] { "[]", """{"choices":[{"delta":{"content":"\ud800"}}]}""",
            """{"choices":[],"opaque":1e999}""", """{"choices":[{"delta":{},"finish_reason":9}]}""" })
            Failed(Terminal(await Collect([malformed])), OpenAICompletionsWireFailure.MalformedStream);
        foreach (var raw in new[] { """{"choices":[],}""", """{"choices":[],/*private*/"opaque":1}""" })
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var value = JsonData.FromElement(document.RootElement);
            Failed(Terminal(await Collect(new OpenAICompletionsWireSource((_, _) => OwnedSequence([value])))),
                OpenAICompletionsWireFailure.MalformedStream);
        }
        foreach (var options in new[] { new OpenAICompletionsWireOptions(MaximumChunks: 0), new(MaximumJsonDepth: 65),
            new(Rates: new(-1)) })
            Throws<ArgumentOutOfRangeException>(() => new OpenAICompletionsWireSource((_, _) => Sequence([]), options));
        var acquisitions = 0;
        await ThrowsAsync<StreamProtocolException>(() => Collect(new OpenAICompletionsWireSource((_, _) =>
        { acquisitions++; return Sequence([]); }), new(new("model", "wrong-api", "provider"), [])));
        Equal(0, acquisitions);
    }

    private static async Task OwnershipAndFaults()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new Probe([JsonData.Parse(Finish("stop"))]) { HoldDisposal = true };
        var reader = new OpenAICompletionsWireSource((_, _) => source).StreamAsync(Request()).GetAsyncEnumerator();
        Task<bool>? pending = null;
        try
        {
            Check(await reader.MoveNextAsync() && reader.Current is StreamStarted, "Start missing.");
            Equal(0, source.Pulls);
            pending = reader.MoveNextAsync().AsTask();
            await source.DisposalEntered.Task.WaitAsync(deadline.Token);
            Check(!pending.IsCompleted, "Successful terminal escaped before owned source cleanup.");
            source.DisposalRelease.TrySetResult();
            Check(await pending.WaitAsync(deadline.Token) && reader.Current is StreamDone, "Done missing after cleanup.");
            Equal(1, source.Disposals); Check(!await reader.MoveNextAsync(), "Duplicate terminal.");
        }
        finally { source.DisposalRelease.TrySetResult(); if (pending is not null) await JoinAsync(pending); await reader.DisposeAsync(); }
        var early = new Probe([]) { HoldDisposal = true };
        var abandoned = new OpenAICompletionsWireSource((_, _) => early).StreamAsync(Request()).GetAsyncEnumerator();
        Task? disposal = null;
        try
        {
            await abandoned.MoveNextAsync(); disposal = abandoned.DisposeAsync().AsTask();
            await early.DisposalEntered.Task.WaitAsync(deadline.Token);
            Check(!disposal.IsCompleted, "Early disposal detached owned enumerator cleanup.");
            Equal(0, early.Pulls); early.DisposalRelease.TrySetResult(); await disposal.WaitAsync(deadline.Token); Equal(1, early.Disposals);
        }
        finally { early.DisposalRelease.TrySetResult(); if (disposal is not null) await JoinAsync(disposal); }
        foreach (var stage in new[] { "read", "current", "acquire" })
        {
            var faulty = new Probe([JsonData.Parse(Finish("stop"))])
            { DisposalFailure = new IOException("private-cleanup") };
            if (stage == "read") faulty.ReadFailure = new JsonException("private-read");
            if (stage == "current") faulty.CurrentFailure = new FormatException("private-current");
            if (stage == "acquire") faulty.AcquisitionFailure = new InvalidOperationException("private-acquisition");
            var frames = await Collect(new OpenAICompletionsWireSource((_, _) => faulty));
            Failed(Terminal(frames), OpenAICompletionsWireFailure.SourceFailed);
            Equal(stage == "acquire" ? 0 : 1, faulty.Disposals);
            Equal(stage == "acquire" ? 0 : 1, frames.OfType<StreamStarted>().Count());
        }
        var cleanup = new Probe([JsonData.Parse(Finish("stop"))]) { DisposalFailure = new IOException("private-cleanup") };
        Failed(Terminal(await Collect(new OpenAICompletionsWireSource((_, _) => cleanup))),
            OpenAICompletionsWireFailure.CleanupFailed);
        var foreign = new Probe([]) { ReadFailure = new OperationCanceledException("private-foreign") };
        Failed(Terminal(await Collect(new OpenAICompletionsWireSource((_, _) => foreign))), OpenAICompletionsWireFailure.SourceFailed);
        var factory = await Collect(new OpenAICompletionsWireSource((_, _) => throw new IOException("private-factory")));
        Failed(Terminal(factory), OpenAICompletionsWireFailure.SourceFailed); Equal(0, factory.OfType<StreamStarted>().Count());
    }

    private static async Task CancellationAndChatRun()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var acquisitions = 0;
        await ThrowsAsync<OperationCanceledException>(() => Collect(new OpenAICompletionsWireSource((_, _) =>
        { acquisitions++; return Sequence([]); }), token: before.Token)); Equal(0, acquisitions);
        using var during = new CancellationTokenSource(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new Probe([]) { HoldRead = true, HoldDisposal = true };
        var reader = new OpenAICompletionsWireSource((_, _) => source).StreamAsync(Request(), during.Token).GetAsyncEnumerator();
        Task<bool>? pending = null;
        try
        {
            await reader.MoveNextAsync(); pending = reader.MoveNextAsync().AsTask();
            await source.ReadEntered.Task.WaitAsync(deadline.Token);
            Equal(during.Token, source.Token); during.Cancel();
            await source.DisposalEntered.Task.WaitAsync(deadline.Token);
            Check(!pending.IsCompleted, "Canceled terminal escaped before actual source cleanup.");
            source.DisposalRelease.TrySetResult();
            Check(await pending.WaitAsync(deadline.Token), "Aborted terminal missing.");
            Failed((StreamTerminalEvent)reader.Current, OpenAICompletionsWireFailure.Cancelled); Equal(1, source.Disposals);
        }
        finally { during.Cancel(); source.DisposalRelease.TrySetResult(); source.ReadRelease.TrySetResult(); if (pending is not null) await JoinAsync(pending); await reader.DisposeAsync(); }
        foreach (var throwFromCurrent in new[] { false, true })
        {
            using var canceled = new CancellationTokenSource();
            var getter = new Probe([JsonData.Parse("""{"id":"must-not-process","choices":[]}""")])
            { OnCurrent = canceled.Cancel, CurrentFailure = throwFromCurrent ? new JsonException("private-current") : null };
            var frames = await Collect(new OpenAICompletionsWireSource((_, _) => getter), token: canceled.Token);
            Failed(Terminal(frames), OpenAICompletionsWireFailure.Cancelled);
            Check(Terminal(frames).Message.ExtraProperties?.TryGet("responseId", out _) != true,
                "Cancellation in Current still dispatched the DTO.");
            Equal(1, getter.Disposals);
        }
        using var afterEnd = new CancellationTokenSource();
        var endFrames = new List<StreamEvent>();
        await foreach (var item in new OpenAICompletionsWireSource((_, _) => Sequence([Text("known"), Finish("stop")]))
            .StreamAsync(Request(), afterEnd.Token))
        { endFrames.Add(item); if (item is TextEnded) afterEnd.Cancel(); }
        Failed(Terminal(endFrames), OpenAICompletionsWireFailure.Cancelled);
        Equal("known", ((TextContent)Terminal(endFrames).Message.Content[0]).Text);
        Equal(0, endFrames.OfType<StreamDone>().Count());
        using var runCancel = new CancellationTokenSource();
        var held = new Probe([]) { HoldRead = true, HoldDisposal = true };
        await using var run = await new ChatClient(new OpenAICompletionsWireSource((_, _) => held), capacity: 1)
            .StartWithAbortSettlementAsync(Request(), runCancel.Token);
        var drain = Task.Run(async () => { var frames = new List<StreamEvent>(); await foreach (var item in run.ReadEventsAsync()) frames.Add(item); return frames; });
        try
        {
            await held.ReadEntered.Task.WaitAsync(deadline.Token); runCancel.Cancel();
            await held.DisposalEntered.Task.WaitAsync(deadline.Token);
            Check(!run.Completion.IsCompleted, "ChatRun canceled completion detached adapter cleanup.");
            held.DisposalRelease.TrySetResult();
            var frames = await drain.WaitAsync(deadline.Token);
            Equal(1, frames.OfType<StreamTerminalEvent>().Count());
            var result = await run.Completion.WaitAsync(deadline.Token);
            Equal(ChatFailureKind.Cancelled, result.Failure!.Kind);
            Equal("Cancelled", result.NativeDiagnostic?.Code.ToString());
            Equal(result.NativeDiagnostic, result.Failure.NativeDiagnostic);
            Check(!(result.Message.ExtraProperties?.TryGet("openAICompletionsFailure", out _) ?? false), "Native classification entered the Pi message.");
        }
        finally { runCancel.Cancel(); held.ReadRelease.TrySetResult(); held.DisposalRelease.TrySetResult(); await JoinAsync(drain); }
    }

    private static string Text(string value) => JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = value } } } });
    private static string Finish(string reason) => JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = reason, delta = new { } } } });
    private static string ToolChunk(object tool) => JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { tool } } } } });
    private static async IAsyncEnumerable<JsonData> Sequence(string[] values, [EnumeratorCancellation] CancellationToken token = default)
    { await Task.CompletedTask; foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(value); } }
    private static async IAsyncEnumerable<JsonData> OwnedSequence(JsonData[] values)
    { await Task.CompletedTask; foreach (var value in values) yield return value; }
    private static Task<List<StreamEvent>> Collect(string[] chunks, OpenAICompletionsWireOptions? options = null, string provider = "fixture-provider") =>
        Collect(new OpenAICompletionsWireSource((_, _) => Sequence(chunks), options), Request(provider));
    private static async Task<List<StreamEvent>> Collect(OpenAICompletionsWireSource mapper, ChatRequest? request = null, CancellationToken token = default)
    { var frames = new List<StreamEvent>(); await foreach (var item in mapper.StreamAsync(request ?? Request(), token)) frames.Add(item); return frames; }
    private static StreamTerminalEvent Terminal(List<StreamEvent> frames)
    { Equal(1, frames.OfType<StreamTerminalEvent>().Count()); return (StreamTerminalEvent)frames[^1]; }
    private static string Property(JsonFields? fields, string name)
    { Check(fields is not null && fields.TryGet(name, out _), "Missing owned metadata field."); return fields!.Values[name].Value.ValueKind == JsonValueKind.String
        ? fields.Values[name].Value.GetString()! : fields.Values[name].ToString(); }
    private static void Failed(StreamTerminalEvent terminal, OpenAICompletionsWireFailure failure)
    {
        Check(terminal is StreamError, "Failed wire stream produced executable success.");
        Equal(failure == OpenAICompletionsWireFailure.Cancelled ? StopReason.Aborted : StopReason.Error, terminal.Reason);
        Equal(terminal.Reason, terminal.Message.StopReason);
        Equal(failure.ToString(), terminal.NativeDiagnostic?.Code.ToString());
        Equal(NativeChatAdapter.OpenAICompletions, terminal.NativeDiagnostic!.Adapter);
        Check(!(terminal.Message.ExtraProperties?.TryGet("openAICompletionsFailure", out _) ?? false), "Native classification entered the Pi message.");
        Check(!Property(terminal.Message.ExtraProperties, "errorMessage").Contains("private", StringComparison.Ordinal), "Private failure payload leaked.");
    }
    private static string Operation(StreamEvent value)
    { var wire = PiWireJson.WriteEvent(value).Value; return wire.GetProperty("type").GetString()! +
        (wire.TryGetProperty("contentIndex", out var index) ? ":" + index.GetRawText() : ""); }
    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var properties = left.EnumerateObject().ToArray();
            return properties.Length == right.EnumerateObject().Count() &&
                properties.All(property => right.TryGetProperty(property.Name, out var other) && Same(property.Value, other));
        }
        if (left.ValueKind == JsonValueKind.Array)
            return left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
    private sealed class Probe(JsonData[] values) : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>
    {
        private int _position = -1;
        public int Pulls, Disposals;
        public CancellationToken Token;
        public bool HoldRead, HoldDisposal;
        public Exception? AcquisitionFailure, ReadFailure, CurrentFailure, DisposalFailure;
        public Action? OnCurrent;
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource ReadRelease { get; } = Gate();
        public TaskCompletionSource DisposalEntered { get; } = Gate();
        public TaskCompletionSource DisposalRelease { get; } = Gate();
        public JsonData Current { get { OnCurrent?.Invoke(); if (CurrentFailure is not null) throw CurrentFailure; return values[_position]; } }
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { if (AcquisitionFailure is not null) throw AcquisitionFailure; Token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            Pulls++; ReadEntered.TrySetResult();
            if (HoldRead) await ReadRelease.Task.WaitAsync(Token);
            if (ReadFailure is not null) throw ReadFailure;
            return ++_position < values.Length;
        }
        public async ValueTask DisposeAsync()
        { Disposals++; DisposalEntered.TrySetResult(); if (HoldDisposal) await DisposalRelease.Task; if (DisposalFailure is not null) throw DisposalFailure; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task JoinAsync(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { if (!task.IsCompleted) throw; } }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected failure."); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected failure."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Completions values differ.");
}
