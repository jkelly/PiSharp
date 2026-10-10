using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

static class ResponsesTranscriptProjectionTests
{
    private static ResponsesTranscriptProjectionOptions Options() => new(true,
        AllowedToolCallProviders: ImmutableHashSet.Create("fixture-openai"));
    private static ChatRequest Request(params TranscriptEntry[] entries) => new(new("model-a", "openai-responses", "fixture-openai"), entries.ToImmutableArray());
    private static TranscriptEntry Entry(string json) { var body = JsonData.Parse(json); return new(body.Value.GetProperty("role").GetString()!, body); }
    private static TranscriptEntry User(string text = "Ask") => Entry(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 1 }));
    private static TranscriptEntry Assistant(AssistantContent[] content, string model = "model-a", string provider = "fixture-openai", StopReason stop = StopReason.ToolUse)
        => new("assistant", PiWireJson.WriteMessage(new("openai-responses", provider, model, 2, content.ToImmutableArray(), TokenUsage.Zero, stop)));
    private static TranscriptEntry Tool(string id, JsonData? arguments = null, string model = "model-a", string provider = "fixture-openai")
        => Assistant([new ToolCallContent(id, "read", arguments ?? JsonData.EmptyObject)], model, provider);
    private static TranscriptEntry Result(string id, string name = "read") => Entry(JsonSerializer.Serialize(new
        { role = "toolResult", toolCallId = id, toolName = name, content = new[] { new { type = "text", text = "ok" } }, isError = false, timestamp = 3 }));
    private static JsonData Project(TranscriptEntry[] entries, ResponsesTranscriptProjectionOptions? options = null)
        => new ResponsesTranscriptProjector(options ?? Options()).Project(Request(entries));

    public static async Task Corpus(string repo)
    {
        var directory = Path.Combine(repo, "fixtures", "pi-v0.99.1", "responses-replay");
        var inputBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core.input.json"));
        var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core.expected.json"));
        Equal("ea51963dab7282dfaf84faf9b5b42af70e961a1494921db8b82da60232c3e76d", Convert.ToHexStringLower(SHA256.HashData(inputBytes)));
        Equal("2d946a5739797f0bef623f4acacc7823ed505d8fb5037d57dfdd45bd749c38ad", Convert.ToHexStringLower(SHA256.HashData(goldenBytes)));
        using var input = JsonDocument.Parse(inputBytes); using var golden = JsonDocument.Parse(goldenBytes);
        var cases = input.RootElement.GetProperty("cases"); var expected = golden.RootElement.GetProperty("observations").GetProperty("cases");
        Equal(3, cases.GetArrayLength()); Equal(3, expected.GetArrayLength());
        var results = new JsonArray(); var counts = new[] { 6, 5, 7 };
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var item = cases[index]; var oracle = expected[index]; var id = item.GetProperty("caseId").GetString()!;
            Equal(id, oracle.GetProperty("caseId").GetString());
            var context = input.RootElement.GetProperty("contexts").GetProperty(item.GetProperty("contextRef").GetString()!);
            var transcript = context.GetProperty("messages").EnumerateArray().Select(body => new TranscriptEntry(body.GetProperty("role").GetString()!, JsonData.FromElement(body))).ToImmutableArray();
            var original = transcript.Select(entry => entry.WireBody.ToString()).ToArray();
            var model = input.RootElement.GetProperty("baseModel");
            var request = new ChatRequest(new(item.GetProperty("targetModel").GetProperty("id").GetString()!, model.GetProperty("api").GetString()!, model.GetProperty("provider").GetString()!), transcript);
            var options = new ResponsesTranscriptProjectionOptions(model.GetProperty("reasoning").GetBoolean(),
                SupportsMidConversationSystemMessages: item.GetProperty("options").GetProperty("supportsMidConvoSystemMessages").GetBoolean(),
                AllowedToolCallProviders: item.GetProperty("allowedToolCallProviders").EnumerateArray().Select(value => value.GetString()!).ToImmutableHashSet(StringComparer.Ordinal));
            var projector = new ResponsesTranscriptProjector(options); var actual = projector.Project(request);
            Assert(Same(oracle.GetProperty("requestInput"), actual.Value), $"Captured request array differs: {id}");
            Equal(counts[index], actual.Value.GetArrayLength());
            Assert(Same(actual.Value, projector.Project(request).Value), "Projection is not deterministic.");
            for (var entryIndex = 0; entryIndex < original.Length; entryIndex++) Equal(original[entryIndex], transcript[entryIndex].WireBody.ToString());
            foreach (var pointer in oracle.GetProperty("undefinedProjectionProperties").EnumerateArray())
            {
                var parts = pointer.GetString()!.Split('/');
                Assert(!actual.Value[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)].TryGetProperty(parts[2], out _), "Source undefined property became native null/present data.");
            }
            var mutation = JsonNode.Parse(actual.ToString())!.AsArray();
            if (index == 0)
            {
                mutation[2]!.AsObject().Remove("future"); Assert(!Same(oracle.GetProperty("requestInput"), JsonSerializer.SerializeToElement(mutation)), "Reasoning explicit null loss hidden.");
                mutation = JsonNode.Parse(actual.ToString())!.AsArray(); mutation[3]!["phase"] = "final_answer";
                Assert(!Same(oracle.GetProperty("requestInput"), JsonSerializer.SerializeToElement(mutation)), "Text phase mutation hidden.");
                mutation = JsonNode.Parse(actual.ToString())!.AsArray(); mutation[4]!.AsObject().Remove("namespace");
                Assert(!Same(oracle.GetProperty("requestInput"), JsonSerializer.SerializeToElement(mutation)), "Namespace removal hidden.");
            }
            mutation = JsonNode.Parse(actual.ToString())!.AsArray();
            var output = mutation.OfType<JsonObject>().Single(value => value["type"]?.GetValue<string>() == "function_call_output"); output["call_id"] = "different-call";
            Assert(!Same(oracle.GetProperty("requestInput"), JsonSerializer.SerializeToElement(mutation)), "Call/result link mutation hidden.");
            var call = actual.Value.EnumerateArray().Single(value => value.TryGetProperty("type", out var type) && type.GetString() == "function_call");
            using var reordered = JsonDocument.Parse("{\"arguments\":\"{\\\"keep\\\":null,\\\"path\\\":\\\"changed\\\"}\"}");
            Assert(!Same(call.GetProperty("arguments"), reordered.RootElement.GetProperty("arguments")), "Opaque argument string was normalized.");
            results.Add(new JsonObject { ["caseId"] = id, ["status"] = "matched", ["requestInput"] = JsonNode.Parse(actual.ToString()),
                ["sourceUndefinedPointers"] = JsonNode.Parse(oracle.GetProperty("undefinedProjectionProperties").GetRawText()), ["inputUnchanged"] = true });
        }
        using var one = JsonDocument.Parse("{\"a\":1,\"b\":null}"); using var reorderedKeys = JsonDocument.Parse("{\"b\":null,\"a\":1}"); using var decimalScale = JsonDocument.Parse("{\"a\":1.0,\"b\":null}");
        Assert(Same(one.RootElement, reorderedKeys.RootElement) && !Same(one.RootElement, decimalScale.RootElement), "Comparison normalized more than object-key order.");
        var artifact = Path.Combine(repo, "artifacts", "native", "responses-replay-core.actual.json"); Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, new JsonObject { ["schemaVersion"] = 1, ["captureCommit"] = "560d1fad202019748fbd3e9fe9b1aae7d03d7e0a",
            ["sourceSha"] = "d86654abb8862e201933517d6f1fce9f88dd117f", ["scope"] = "serialized-responses-input-array-subset", ["undefinedParity"] = "not-claimed-native-omission",
            ["argumentNumbers"] = "canonical-safe-integers-only", ["effects"] = new JsonArray(), ["cases"] = results }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Task PairingAndCollisions()
    {
        foreach (var entries in new[] { new[] { Tool("call|fc_one") }, new[] { Result("unknown") },
            new[] { Tool("call|fc_one"), Result("call|fc_one"), Result("call|fc_one") }, new[] { Tool("call|fc_one"), Result("call|fc_one", "wrong") },
            new[] { Tool("call|fc_one"), User(), Result("call|fc_one") } })
            Equal(ResponsesProjectionFailure.UnmatchedToolResult, Throws(() => Project(entries)).Failure);
        foreach (var entries in new[]
        {
            new[] { Tool("call odd__|fc_a", model: "model-b"), Tool("call_odd|fc_b", model: "model-b") },
            new[] { Tool("call|fc_one|extra") },
            new[] { Assistant([new ToolCallContent("call|fc_one", "read", JsonData.EmptyObject), new ToolCallContent("call|fc_two", "read", JsonData.EmptyObject)]) }
        }) Equal(ResponsesProjectionFailure.IdentityCollision, Throws(() => Project(entries)).Failure);
        var original = "call odd__|foreign:item!/"; var tool = Tool(original, model: "foreign", provider: "foreign"); var result = Result(original);
        var projected = Project([tool, result]).Value;
        Equal("call_odd", projected[0].GetProperty("call_id").GetString()); Equal("fc_1rj8blc1y9mlbs", projected[0].GetProperty("id").GetString());
        Equal("call_odd", projected[1].GetProperty("call_id").GetString()); Equal(original, result.WireBody.Value.GetProperty("toolCallId").GetString());
        // Owner decision 13: openai-responses-shared.ts normalizeIdPart("_") strips the trailing "_", leaving the call id "", which upstream
        // replays (as it replays a call whose id is "|"); several calls may share it.
        var emptied = Project([Tool("_|fc_a", model: "model-b"), Result("_|fc_a")]).Value;
        Equal("", emptied[0].GetProperty("call_id").GetString()); Equal("", emptied[1].GetProperty("call_id").GetString());
        return Task.CompletedTask;
    }

    public static Task SystemsAndOrdering()
    {
        var initial = Entry("""{"role":"system","content":"Base","sections":{"beta":"B","alpha":"A","10":"Ten","2":"Two"},"timestamp":0}""");
        var update = Entry("""{"role":"system","content":"Later","sections":{"beta":null,"alpha":"A2","gamma":"G"},"timestamp":2}""");
        var readd = Entry("""{"role":"system","content":"","sections":{"beta":"B2"},"timestamp":3}""");
        var folded = Project([initial, User(), update, readd]).Value;
        Equal("Base\n\nLater\n\nTwo\n\nTen\n\nA2\n\nG\n\nB2", folded[0].GetProperty("content").GetString());
        var mid = Entry("""{"role":"system","content":"Later","sections":{"rule":"New","drop":null},"timestamp":2}""");
        var ordered = Project([initial, Tool("call|fc_one"), mid, Result("call|fc_one"), User()], Options() with { SupportsMidConversationSystemMessages = true }).Value;
        Equal("function_call_output", ordered[2].GetProperty("type").GetString());
        Equal("Later\n\nUpdated system prompt section \"rule\":\n\nNew\n\nRemoved system prompt section \"drop\".", ordered[3].GetProperty("content").GetString());
        var excludedPrompt = Project([initial, User(), mid], Options() with { SupportsMidConversationSystemMessages = true, IncludeInitialSystemPrompt = false, SupportsDeveloperRole = false }).Value;
        Equal("user", excludedPrompt[0].GetProperty("role").GetString()); Equal("system", excludedPrompt[1].GetProperty("role").GetString());
        var noInitial = Project([Entry("""{"role":"user","content":[],"timestamp":0}"""), Assistant([new TextContent("Answer")], stop: StopReason.Stop)]).Value;
        Equal("msg_pi_0", noInitial[0].GetProperty("id").GetString());
        Assert(Project([Assistant([new TextContent("private failed partial")], stop: StopReason.Error)]).Value.GetArrayLength() == 0, "Failed assistant was replayed.");
        return Task.CompletedTask;
    }

    public static Task ArgumentQuotingAndNumbers()
    {
        var arguments = JsonData.Parse("{\"z\":\"\\u2028\\u2029\\uE000\\uD83D\\uDE00\\n\\\"\\\\\",\"10\":null,\"2\":\"two\",\"01\":true,\"nested\":{\"b\":false,\"a\":[\"b\",\"a\"]}}");
        var actual = Project([Tool("call|fc_one", arguments), Result("call|fc_one")]).Value[0].GetProperty("arguments").GetString();
        Equal("{\"2\":\"two\",\"10\":null,\"z\":\"\u2028\u2029\uE000\U0001F600\\n\\\"\\\\\",\"01\":true,\"nested\":{\"b\":false,\"a\":[\"b\",\"a\"]}}", actual);
        foreach (var raw in new[] { "0", "1", "-1", "9007199254740991", "-9007199254740991" })
            Equal("{\"n\":" + raw + "}", Project([Tool("call|fc_one", JsonData.Parse("{\"n\":" + raw + "}")), Result("call|fc_one")]).Value[0].GetProperty("arguments").GetString());
        foreach (var raw in new[] { "1.0", "0.5", "1e0", "-0", "9007199254740992", "9007199254740993", "1e400" })
            Equal(ResponsesProjectionFailure.UnsupportedNumber, Throws(() => Project([Tool("call|fc_one", JsonData.Parse("{\"n\":" + raw + "}")), Result("call|fc_one")])).Failure);
        return Task.CompletedTask;
    }

    public static Task UnsupportedAndValidation()
    {
        foreach (var entry in new[]
        {
            Entry("""{"role":"user","content":[{"type":"image","mimeType":"image/png","data":"private"}],"timestamp":0}"""),
            Entry("""{"role":"system","content":"","toolsAdded":[{"name":"private"}],"timestamp":0}"""),
            Entry("""{"role":"custom","content":"private"}"""),
            Entry("""{"role":"assistant","content":[{"type":"custom_tool_call"}]}""")
        }) Equal(ResponsesProjectionFailure.UnsupportedContent, Throws(() => Project([entry])).Failure);
        var mismatch = User(); Equal(ResponsesProjectionFailure.InvalidTranscript, Throws(() => Project([mismatch with { Role = "system" }])).Failure);
        foreach (var arguments in new[] { "[]", "null", "1", "\"private\"" })
            Equal(ResponsesProjectionFailure.InvalidTranscript, Throws(() => Project([Tool("call|fc_one", JsonData.Parse(arguments)), Result("call|fc_one")])).Failure);
        Equal(ResponsesProjectionFailure.InvalidTranscript, Throws(() => Project([Assistant([new TextContent("partial")], stop: StopReason.Pending)])).Failure);
        foreach (var signature in new[] { "{private-signature", "[]", "{\"type\":\"private-other\"}" })
        {
            var fields = JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(JsonSerializer.Serialize(signature)));
            var error = Throws(() => Project([Assistant([new ThinkingContent("", fields)], stop: StopReason.Stop)]));
            Equal(ResponsesProjectionFailure.UnsupportedSignature, error.Failure); Assert(!error.Message.Contains("private", StringComparison.Ordinal), "Signature leaked in diagnostic.");
        }
        Equal(ResponsesProjectionFailure.UnsupportedUnicode, Throws(() => Project([Entry("{\"role\":\"user\",\"content\":\"\\uD83D\",\"timestamp\":0}")])).Failure);
        return Task.CompletedTask;
    }

    public static Task BoundsAndCancellation()
    {
        var entry = User("a"); var inputSize = entry.WireBody.ToString().Length; var outputSize = Project([entry]).ToString().Length;
        Assert(Project([entry], Options() with { MaximumEntryCharacters = inputSize, MaximumInputCharacters = inputSize, MaximumOutputCharacters = outputSize, MaximumOutputItems = 1, MaximumMessages = 1 }).Value.GetArrayLength() == 1, "Exact limit boundary rejected.");
        foreach (var options in new[]
        {
            Options() with { MaximumEntryCharacters = inputSize - 1 }, Options() with { MaximumInputCharacters = inputSize - 1 },
            Options() with { MaximumOutputCharacters = outputSize - 1 }, Options() with { MaximumJsonDepth = 2 }
        }) Equal(ResponsesProjectionFailure.ResourceLimit, Throws(() => Project([entry], options)).Failure);
        Equal(ResponsesProjectionFailure.ResourceLimit, Throws(() => Project([entry, entry], Options() with { MaximumMessages = 1 })).Failure);
        Equal(ResponsesProjectionFailure.ResourceLimit, Throws(() => Project([entry, entry], Options() with { MaximumOutputItems = 1 })).Failure);
        var blocks = Entry("""{"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}],"timestamp":0}""");
        Equal(ResponsesProjectionFailure.ResourceLimit, Throws(() => Project([blocks], Options() with { MaximumContentBlocks = 1 })).Failure);
        var signature = JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"{\\\"type\\\":\\\"reasoning\\\",\\\"nested\\\":[[[]]]}\""));
        Equal(ResponsesProjectionFailure.ResourceLimit, Throws(() => Project([Assistant([new ThinkingContent("", signature)], stop: StopReason.Stop)], Options() with { MaximumJsonDepth = 3 })).Failure);
        Equal("[]", Project([], Options() with { MaximumOutputCharacters = 2 }).ToString());
        foreach (var invalid in new[] { Options() with { MaximumMessages = 0 }, Options() with { MaximumJsonDepth = 65 }, Options() with { MaximumOutputCharacters = 1 } })
        { try { _ = new ResponsesTranscriptProjector(invalid); throw new Exception("Invalid limits accepted."); } catch (ArgumentOutOfRangeException) { } }
        var projector = new ResponsesTranscriptProjector(Options()); var request = Request(entry); var previous = projector.Project(request).ToString();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { _ = projector.Project(request, cancelled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Equal(previous, projector.Project(request).ToString()); Equal(inputSize, entry.WireBody.ToString().Length);
        return Task.CompletedTask;
    }

    private static ResponsesProjectionException Throws(Action action)
    { try { action(); } catch (ResponsesProjectionException exception) { return exception; } throw new Exception("Expected fixed projection rejection."); }
    private static bool Same(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count() && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Same(property.Value, value)),
            JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => expected.GetString() == actual.GetString(), JsonValueKind.Number => expected.GetRawText() == actual.GetRawText(), _ => true
        };
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
