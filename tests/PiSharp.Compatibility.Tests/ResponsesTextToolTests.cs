using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

static class ResponsesTextToolTests
{
    private static ChatRequest Request() => new(new("synthetic-responses-model", "openai-responses", "fixture-provider"), [], 1_700_000_000_000);
    private const string Completed = """{"type":"response.completed","response":{"id":"response","status":"completed","output":[]}}""";
    private const string TextStart = """{"type":"response.output_item.added","output_index":4,"item":{"type":"message","id":"message","content":[]}}""";
    private const string ToolStart = """{"type":"response.output_item.added","output_index":9,"item":{"type":"function_call","id":"item","call_id":"call","name":"read","arguments":""}}""";

    public static async Task Corpus(string repo)
    {
        ComparisonMutations();
        await ComputedCostTokens();
        var directory = Path.Combine(repo, "fixtures", "pi-v0.99.1", "responses-wire");
        var inputBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core.input.json"));
        var expectedBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core.expected.json"));
        Equal("87b6d02b821774c2ace59d8a011973a38b95731c1c8c5c271bf358ebe8554361", Convert.ToHexStringLower(SHA256.HashData(inputBytes)));
        Equal("7b9b2aeeaefe6d62b2c9e0ad9f042dbc938a47fe41dc59a3a66d2cc5893703d6", Convert.ToHexStringLower(SHA256.HashData(expectedBytes)));
        using var input = JsonDocument.Parse(inputBytes); using var expected = JsonDocument.Parse(expectedBytes);
        var cases = input.RootElement.GetProperty("cases"); var oracle = expected.RootElement.GetProperty("observations").GetProperty("cases");
        Equal(3, cases.GetArrayLength()); Equal(3, oracle.GetArrayLength());
        var results = new JsonArray(); var overlapping = 0;
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var item = cases[index]; var observation = oracle[index]; var id = item.GetProperty("caseId").GetString()!;
            Equal(id, observation.GetProperty("caseId").GetString());
            var source = new Probe(item.GetProperty("events").EnumerateArray().Select(JsonData.FromElement).ToArray());
            var transport = new ResponsesTextToolTransport((request, token) => source,
                new(Rates: new(1_000_000, 2_000_000, 500_000, 1_500_000)));
            var progress = new List<StreamEvent>(); Exception? failure = null;
            try { await foreach (var value in transport.StreamAsync(Request())) progress.Add(value); }
            catch (StreamProtocolException exception) { failure = exception; }
            Equal(1, source.Disposals);
            var overlappingEvents = progress.Where(value => value is not StreamStarted and not StreamTerminalEvent).ToArray();
            var emissions = observation.GetProperty("emissionSnapshots"); Equal(emissions.GetArrayLength(), overlappingEvents.Length);
            for (var emissionIndex = 0; emissionIndex < emissions.GetArrayLength(); emissionIndex++)
            {
                CompareOperation(emissions[emissionIndex], overlappingEvents[emissionIndex]); overlapping++;
            }
            if (index == 0)
            {
                Assert(failure is null, "Completed oracle case rejected.");
                var terminal = progress.OfType<StreamDone>().Single();
                Assert(Same(observation.GetProperty("finalOutput"), PiWireJson.WriteMessage(terminal.Message).Value), "Completed Responses final output differs.");
                Equal(StopReason.ToolUse, terminal.Reason); Equal(34.5m, terminal.Message.Usage.Cost.Total);
                var tool = (ToolCallContent)terminal.Message.Content[1];
                Equal("call_complete|fc_complete", tool.Id);
                Assert(tool.ExtraProperties!.TryGet("namespace", out var ns) && ns!.Value.GetString() == "fs.final", "Final namespace lost.");
                var integrated = await new ChatClient(transport, capacity: 1).CompleteAsync(Request());
                Assert(integrated.Failure is null && Same(observation.GetProperty("finalOutput"), PiWireJson.WriteMessage(integrated.Message).Value), "Authored ChatRun integration differs.");
            }
            else
            {
                Assert(failure is not null && progress.All(value => value is not StreamTerminalEvent), "Failed source trace authorized a terminal.");
                var result = await new ChatClient(new ResponsesTextToolTransport((request, token) => Sequence(item.GetProperty("events").EnumerateArray().Select(JsonData.FromElement).ToArray())), capacity: 1).CompleteAsync(Request());
                Equal(ChatFailureKind.MalformedStream, result.Failure!.Kind); Equal(StopReason.Error, result.Message.StopReason);
            }
            results.Add(new JsonObject { ["caseId"] = id, ["overlappingOperations"] = overlappingEvents.Length,
                ["status"] = index == 0 ? "completed-final-matched" : "failure-no-success-terminal",
                ["nativeEvents"] = new JsonArray(progress.Select(value => JsonNode.Parse(PiWireJson.WriteEvent(value).ToString())).ToArray()),
                ["nativeFailure"] = failure?.Message });
        }
        Equal(12, overlapping);
        var artifact = Path.Combine(repo, "artifacts", "native", "responses-core.actual.json");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, new JsonObject { ["schemaVersion"] = 1,
            ["captureCommit"] = "2a8441dfbed6d9b01fc616e897e7b886f8f793f9", ["sourceSha"] = "d86654abb8862e201933517d6f1fce9f88dd117f",
            ["scope"] = "parsed-responses-text-function-completed-overlap", ["partialSnapshotParity"] = "not-claimed",
            ["wrapperParity"] = "not-claimed-authored-Start-Done-errors", ["effects"] = new JsonArray(), ["cases"] = results
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CompareOperation(JsonElement oracle, StreamEvent native)
    {
        var wire = PiWireJson.WriteEvent(native).Value;
        Equal(oracle.GetProperty("type").GetString(), wire.GetProperty("type").GetString());
        var index = oracle.GetProperty("contentIndex").GetInt32(); Equal(index, wire.GetProperty("contentIndex").GetInt32());
        switch (native)
        {
            case TextDelta text: Equal(oracle.GetProperty("delta").GetString(), text.Delta); break;
            case ToolCallDelta tool: Equal(oracle.GetProperty("delta").GetString(), tool.Delta); break;
            case TextStarted text:
                Assert(Same(oracle.GetProperty("partial").GetProperty("content")[index], PiWireJson.WriteContent(text.Content).Value), "Text start differs."); break;
            case ToolCallStarted tool:
                var content = JsonNode.Parse(oracle.GetProperty("partial").GetProperty("content")[index].GetRawText())!.AsObject();
                content.Remove("partialJson");
                Assert(Same(JsonSerializer.SerializeToElement(content), PiWireJson.WriteContent(tool.ToolCall).Value), "Tool start fields differ."); break;
            case TextEnded text:
                Equal(oracle.GetProperty("content").GetString(), text.Content);
                var expectedText = oracle.GetProperty("partial").GetProperty("content")[index];
                Assert(Same(expectedText, PiWireJson.WriteContent(new TextContent(text.Content, text.ExtraProperties)).Value), "Authoritative text/signature differs."); break;
            case ToolCallEnded tool:
                Assert(Same(oracle.GetProperty("toolCall"), PiWireJson.WriteContent(tool.ToolCall).Value), "Authoritative tool end differs."); break;
            default: throw new Exception("Unexpected overlap operation.");
        }
    }

    public static async Task StrictFinalArguments()
    {
        foreach (var raw in new[] { "{\"path\":\"private", "{\"a\":1,\"a\":2}", "[1]", "null", "{\"path\":\"C:\\q\"}" })
        {
            var end = JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 9,
                item = new { type = "function_call", id = "item", call_id = "call", name = "read", arguments = raw } });
            var result = await Complete(ToolStart, end, Completed);
            Equal(ChatFailureKind.MalformedStream, result.Failure!.Kind); Equal(StopReason.Error, result.Message.StopReason);
            Assert(!result.Failure.Message.Contains("private", StringComparison.Ordinal), "Rejected argument leaked.");
        }
        var checkpoint = """{"type":"response.function_call_arguments.done","output_index":9,"item_id":"item","arguments":"{\"value\":null}"}""";
        var endFallback = """{"type":"response.output_item.done","output_index":9,"item":{"type":"function_call","id":"item","call_id":"call","name":"read","arguments":""}}""";
        var accepted = await Complete(ToolStart, checkpoint, endFallback, Completed);
        Assert(accepted.Failure is null && ((ToolCallContent)accepted.Message.Content[0]).Arguments.Value.GetProperty("value").ValueKind == JsonValueKind.Null, "Complete fallback arguments were not validated.");
    }

    public static async Task MalformedAndUnsupported()
    {
        foreach (var invalid in new[]
        {
            "{}", "[]", "{\"type\":\"private-event\"}",
            """{"type":"response.output_item.added","output_index":-1,"item":{"type":"message","id":"message"}}""",
            """{"type":"response.output_item.added","output_index":4,"item":{"type":"reasoning","id":"private-reasoning"}}""",
            """{"type":"response.output_item.added","output_index":4,"item":{"type":"custom_tool_call","id":"custom"}}""",
            """{"type":"response.output_text.delta","output_index":4,"item_id":"wrong","delta":"private-data"}""",
            """{"type":"response.output_text.delta","output_index":99,"delta":"private-data"}""",
            """{"type":"response.incomplete","response":{"status":"completed"}}""",
            """{"type":"error","message":"private-provider-error"}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"reasoning"}]}}"""
        })
        {
            var result = await Complete(TextStart, invalid);
            Equal(ChatFailureKind.MalformedStream, result.Failure!.Kind);
            Assert(!result.Failure.Message.Contains("private", StringComparison.Ordinal), "Protocol diagnostic leaks DTO.");
        }
        Equal(ChatFailureKind.MalformedStream, (await Complete(TextStart, TextStart)).Failure!.Kind);
        Equal(ChatFailureKind.MalformedStream, (await Complete(Completed, Completed)).Failure!.Kind);
        var bookkeeping = """{"type":"response.in_progress","response":{"output":[]}}""";
        Assert((await Complete(bookkeeping, Completed)).Failure is null, "Documented empty lifecycle bookkeeping rejected.");
    }

    public static async Task Limits()
    {
        var sample = JsonData.Parse(Completed);
        var configurations = new[]
        {
            (new ResponsesTextToolOptions(MaximumEvents: 1), new[] { sample, sample }),
            (new ResponsesTextToolOptions(MaximumEventCharacters: sample.ToString().Length - 1), new[] { sample }),
            (new ResponsesTextToolOptions(MaximumInputCharacters: sample.ToString().Length - 1), new[] { sample }),
            (new ResponsesTextToolOptions(MaximumContentSlots: 1), new[] { JsonData.Parse(TextStart), JsonData.Parse(ToolStart) }),
            (new ResponsesTextToolOptions(MaximumJsonDepth: 1), new[] { sample }),
            (new ResponsesTextToolOptions(MaximumContentCharacters: 100), new[] { JsonData.Parse(TextStart), JsonData.Parse(JsonSerializer.Serialize(new { type = "response.output_text.delta", output_index = 4, delta = new string('x', 101) })) })
        };
        foreach (var (options, events) in configurations)
        {
            var source = new Probe(events); var result = await new ChatClient(new ResponsesTextToolTransport((request, token) => source, options)).CompleteAsync(Request());
            Equal(ChatFailureKind.ResourceLimit, result.Failure!.Kind); Equal(1, source.Disposals);
        }
        Assert((await new ChatClient(new ResponsesTextToolTransport((request, token) => Sequence([sample]),
            new(MaximumEvents: 1, MaximumEventCharacters: sample.ToString().Length, MaximumInputCharacters: sample.ToString().Length))).CompleteAsync(Request())).Failure is null, "Exact raw admission boundary rejected.");
        foreach (var options in new[] { new ResponsesTextToolOptions(MaximumEvents: 0), new(MaximumJsonDepth: 65), new(Rates: new(-1)) })
        {
            try { _ = new ResponsesTextToolTransport((request, token) => Sequence([]), options); throw new Exception("Invalid options accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
    }

    public static async Task PullAndEarlyDisposal()
    {
        var source = new Probe([JsonData.Parse(TextStart)]) { HoldDisposal = true }; var factories = 0;
        var expected = Request();
        var transport = new ResponsesTextToolTransport((request, token) => { Assert(ReferenceEquals(expected, request), "Request was replaced."); factories++; return source; });
        var reader = transport.StreamAsync(expected).GetAsyncEnumerator();
        Assert(await reader.MoveNextAsync() && reader.Current is StreamStarted, "Missing authored start."); Equal(0, factories);
        Assert(await reader.MoveNextAsync() && reader.Current is TextStarted, "Missing text start."); Equal(1, factories); Equal(1, source.Pulls);
        var disposal = reader.DisposeAsync().AsTask(); await source.DisposalEntered.Task;
        Assert(!disposal.IsCompleted, "Enumerator disposal failed to await owned cleanup."); Equal(1, source.Pulls);
        source.DisposalRelease.SetResult(); await disposal; Equal(1, source.Disposals);
    }

    public static async Task Cancellation()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var factories = 0;
        var transport = new ResponsesTextToolTransport((request, token) => { factories++; return Sequence([]); });
        await Throws<OperationCanceledException>(async () => { await foreach (var _ in transport.StreamAsync(Request(), before.Token)) { } }); Equal(0, factories);
        using var during = new CancellationTokenSource();
        var source = new Probe([]) { HoldRead = true, HoldDisposal = true }; CancellationToken factoryToken = default;
        var reader = new ResponsesTextToolTransport((request, token) => { factoryToken = token; return source; }).StreamAsync(Request(), during.Token).GetAsyncEnumerator();
        await reader.MoveNextAsync(); var pull = reader.MoveNextAsync().AsTask(); await source.ReadEntered.Task;
        Equal(during.Token, factoryToken); Equal(during.Token, source.Token); during.Cancel(); await source.DisposalEntered.Task;
        Assert(!pull.IsCompleted, "Cancellation did not await source cleanup."); source.DisposalRelease.SetResult();
        await Throws<OperationCanceledException>(async () => { await pull; }); await reader.DisposeAsync(); Equal(1, source.Disposals);
    }

    public static async Task CleanupBeforeTerminalAndFault()
    {
        var source = new Probe([JsonData.Parse(Completed)]) { HoldDisposal = true };
        await using (var reader = new ResponsesTextToolTransport((request, token) => source).StreamAsync(Request()).GetAsyncEnumerator())
        {
            await reader.MoveNextAsync(); var terminal = reader.MoveNextAsync().AsTask(); await source.DisposalEntered.Task;
            Assert(!terminal.IsCompleted, "Done escaped before asynchronous source cleanup."); source.DisposalRelease.SetResult();
            Assert(await terminal && reader.Current is StreamDone, "Terminal missing after cleanup."); Equal(1, source.Disposals);
        }
        var faulting = new Probe([]) { ReadFailure = new InvalidOperationException("private-source-failure"), HoldDisposal = true };
        var completion = new ChatClient(new ResponsesTextToolTransport((request, token) => faulting)).CompleteAsync(Request());
        await faulting.DisposalEntered.Task; Assert(!completion.IsCompleted, "Source fault skipped cleanup."); faulting.DisposalRelease.SetResult();
        var failed = await completion; Equal(ChatFailureKind.Provider, failed.Failure!.Kind); Equal(1, faulting.Disposals);
        Assert(!failed.Failure.Message.Contains("private", StringComparison.Ordinal), "Source fault leaked.");
        var cleanupFault = new Probe([JsonData.Parse(Completed)]) { DisposalFailure = new InvalidOperationException("private-cleanup-failure") };
        var cleanupResult = await new ChatClient(new ResponsesTextToolTransport((request, token) => cleanupFault)).CompleteAsync(Request());
        Equal(ChatFailureKind.Provider, cleanupResult.Failure!.Kind); Equal(StopReason.Error, cleanupResult.Message.StopReason); Equal(1, cleanupFault.Disposals);
    }

    public static async Task AuthoritativeCheckpointAndEnds()
    {
        var seeded = """{"type":"response.output_item.added","output_index":9,"item":{"type":"function_call","id":"item","call_id":"call","name":"read","arguments":"{\"old\":"}}""";
        var checkpoint = """{"type":"response.function_call_arguments.done","output_index":9,"item_id":"item","arguments":"{\"new\":null}"}""";
        var end = """{"type":"response.output_item.done","output_index":9,"item":{"type":"function_call","id":"item","call_id":"call","name":"read","arguments":"{\"final\":[\"b\",\"a\"]}"}}""";
        var events = new List<StreamEvent>();
        await foreach (var value in new ResponsesTextToolTransport((request, token) => Sequence([JsonData.Parse(seeded), JsonData.Parse(checkpoint), JsonData.Parse(end), JsonData.Parse(Completed)])).StreamAsync(Request())) events.Add(value);
        Equal(2, events.OfType<ToolCallCheckpoint>().Count());
        var final = events.OfType<StreamDone>().Single().Message.Content.OfType<ToolCallContent>().Single();
        Equal("b", final.Arguments.Value.GetProperty("final")[0].GetString());
        Assert(!final.Arguments.Value.TryGetProperty("old", out _) && !final.Arguments.Value.TryGetProperty("new", out _), "Preview replaced authoritative final arguments.");
        var standaloneEnd = """{"type":"response.output_item.done","output_index":99,"item":{"type":"message","id":"standalone","content":[{"type":"refusal","refusal":"No"},{"type":"output_text","text":"."}]}}""";
        var result = await Complete(standaloneEnd, Completed); Assert(result.Failure is null, "Done-without-added supported helper behavior rejected."); Equal("No.", ((TextContent)result.Message.Content[0]).Text);
    }

    public static async Task SignatureUnicode()
    {
        // Authored formula regression against pinned encodeTextSignatureV1's JSON.stringify,
        // not another captured provider fixture or a claim about arbitrary metadata.
        foreach (var id in new[] { "m\u2028", "m\u2029", "m\uE000", "m\u03bb\U0001F600", "m\"\\\b\f\n\r\t\u0001" })
        {
            var wire = JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 4,
                item = new { type = "message", id, phase = "final_answer", content = new[] { new { type = "output_text", text = "" } } } });
            var result = await Complete(wire, Completed); Assert(result.Failure is null, "Valid Unicode signature rejected.");
            var content = (TextContent)result.Message.Content[0];
            Assert(content.ExtraProperties!.TryGet("textSignature", out var signature), "Signature missing.");
            var quoted = id == "m\"\\\b\f\n\r\t\u0001" ? "m\\\"\\\\\\b\\f\\n\\r\\t\\u0001" : id;
            Equal("{\"v\":1,\"id\":\"" + quoted + "\",\"phase\":\"final_answer\"}", signature!.Value.GetString());
        }
    }

    public static async Task SeparatePendingMetadataBudget()
    {
        var longId = new string('x', 256);
        var created = JsonSerializer.Serialize(new { type = "response.created", response = new { id = longId } });
        var start = JsonSerializer.Serialize(new { type = "response.output_item.added", output_index = 4, item = new { type = "message", id = longId } });
        var source = new Probe([JsonData.Parse(created), JsonData.Parse(start)]);
        var adapter = new ResponsesTextToolTransport((request, token) => source, new(MaximumContentCharacters: 100));
        var progress = new List<StreamEvent>();
        await Throws<StreamProtocolException>(async () => { await foreach (var value in adapter.StreamAsync(Request())) progress.Add(value); });
        Assert(progress.OfType<TextStarted>().Count() == 1, "Pending slot/metadata was incorrectly charged to the reducer payload budget.");
        var completed = JsonSerializer.Serialize(new { type = "response.completed", response = new { id = longId, status = "completed", output = Array.Empty<object>() } });
        var result = await new ChatClient(new ResponsesTextToolTransport((request, token) => Sequence([JsonData.Parse(completed)]),
            new(MaximumContentCharacters: 100))).CompleteAsync(Request());
        Equal(ChatFailureKind.ResourceLimit, result.Failure!.Kind);
    }

    private static Task<ChatResult> Complete(params string[] json) => new ChatClient(new ResponsesTextToolTransport((request, token) => Sequence(json.Select(JsonData.Parse).ToArray()))).CompleteAsync(Request());
    private static async IAsyncEnumerable<JsonData> Sequence(JsonData[] values, [EnumeratorCancellation] CancellationToken token = default)
    { await Task.CompletedTask; foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return value; } }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); throw new Exception($"Expected {typeof(T).Name}."); } catch (T) { } }
    private static void ComparisonMutations()
    {
        static bool Match(string expected, string actual)
        {
            using var left = JsonDocument.Parse(expected); using var right = JsonDocument.Parse(actual);
            return Same(left.RootElement, right.RootElement);
        }
        Assert(Match("{\"count\":1,\"parts\":[\"a\",null]}", "{\"parts\":[\"\\u0061\",null],\"count\":1}"), "Object-key order or equivalent string escaping changed comparison.");
        Assert(!Match("{\"cost\":1}", "{\"cost\":1.0}"), "Numeric token normalization hid a 1 versus 1.0 difference.");
        Assert(!Match("[9007199254740992]", "[9007199254740993]"), "Large-integer precision difference was hidden.");
        Assert(!Match("{\"signature\":\"{\\\"v\\\":1,\\\"id\\\":\\\"m\\\"}\"}", "{\"signature\":\"{\\\"id\\\":\\\"m\\\",\\\"v\\\":1}\"}"), "Opaque signature string was normalized as an object.");
        Assert(!Match("{\"namespace\":null}", "{}"), "Explicit null was equated with an absent field.");
        Assert(!Match("[\"b\",\"a\"]", "[\"a\",\"b\"]"), "Array order was ignored.");
        Assert(!Match("\"line\\r\\nnext\"", "\"line\\nnext\""), "String line endings were normalized.");
        Assert(!Match("\"\\u00e9\"", "\"e\\u0301\""), "Distinct Unicode strings were normalized.");
    }
    private static async Task ComputedCostTokens()
    {
        var rates = new ResponsesTokenRates(2_000_000.00m, 250_000.0m, 500_000.00m, 125_000.0m);
        foreach (var zero in new[] { false, true })
        {
            var dto = JsonData.Parse(JsonSerializer.Serialize(new { type = "response.completed", response = new
            {
                id = "cost-boundary", status = "completed", output = Array.Empty<object>(), usage = new
                {
                    input_tokens = zero ? 0 : 5, output_tokens = zero ? 0 : 2, total_tokens = zero ? 0 : 7,
                    input_tokens_details = new { cached_tokens = zero ? 0 : 2, cache_write_tokens = zero ? 0 : 1 }
                }
            } }));
            var result = await new ChatClient(new ResponsesTextToolTransport((request, token) => Sequence([dto]), new(Rates: rates))).CompleteAsync(Request());
            Assert(result.Failure is null, "Computed-cost boundary failed.");
            var cost = PiWireJson.WriteMessage(result.Message).Value.GetProperty("usage").GetProperty("cost");
            var tokens = zero ? new[] { "0", "0", "0", "0", "0" } : new[] { "4", "0.5", "1", "0.125", "5.625" };
            var names = new[] { "input", "output", "cacheRead", "cacheWrite", "total" };
            for (var index = 0; index < names.Length; index++) Equal(tokens[index], cost.GetProperty(names[index]).GetRawText());
        }
        // Cost canonicalization must not touch authoritative opaque argument numeric tokens.
        var end = """{"type":"response.output_item.done","output_index":9,"item":{"type":"function_call","id":"item","call_id":"call","name":"read","arguments":"{\"integerScale\":1.0,\"large\":9007199254740993}"}}""";
        var opaque = await Complete(ToolStart, end, Completed); Assert(opaque.Failure is null, "Strict complete numeric argument object rejected.");
        var arguments = PiWireJson.WriteMessage(opaque.Message).Value.GetProperty("content")[0].GetProperty("arguments");
        Equal("1.0", arguments.GetProperty("integerScale").GetRawText()); Equal("9007199254740993", arguments.GetProperty("large").GetRawText());
    }
    private static bool Same(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count() &&
                expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Same(property.Value, value)),
            JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength() &&
                expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => expected.GetRawText() == actual.GetRawText(),
            _ => true
        };
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Probe(JsonData[] values) : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>
    {
        private int _index = -1;
        public int Pulls, Disposals;
        public bool HoldRead, HoldDisposal;
        public Exception? ReadFailure, DisposalFailure;
        public CancellationToken Token;
        public TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonData Current => values[_index];
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken token = default) { Token = token; _index = -1; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            Pulls++; ReadEntered.TrySetResult(); if (HoldRead) await ReadRelease.Task.WaitAsync(Token);
            Token.ThrowIfCancellationRequested(); if (ReadFailure is not null) throw ReadFailure; return ++_index < values.Length;
        }
        public async ValueTask DisposeAsync()
        { DisposalEntered.TrySetResult(); if (HoldDisposal) await DisposalRelease.Task; Disposals++; if (DisposalFailure is not null) throw DisposalFailure; }
    }
}
