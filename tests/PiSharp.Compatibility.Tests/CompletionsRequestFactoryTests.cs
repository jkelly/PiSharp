using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsRequestFactoryTests
{
    private static readonly ModelDescriptor Model = new("fixture-model", "openai-completions", "openai");
    private static readonly Uri Endpoint = new("https://completions.invalid/v1/chat/completions");
    private const string Key = "authored-inert-noncredential";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-request.complete-three-genuine-sdk-bodies", GenuineSdkBodies);
        yield return ("completions-request.system-declaration-and-multiturn-history-replay", HistoryReplay);
        yield return ("completions-request.reasoning-signatures-and-strict-capabilities", ReasoningAndDeclarations);
        yield return ("completions-request.options-headers-and-independent-request-ownership", OptionsAndOwnership);
        yield return ("completions-request.strict-admission-bounds-cancellation-and-comparison-controls", AdmissionAndBounds);
    }
    private static async Task GenuineSdkBodies()
    {
        var root = FindRepo();
        // Check every complete byte identity before parsing any fixture.
        var inputBytes = ReadPinned(root, "fixtures/reference/openai-completions-stream/input.json", "fad084d8113bdbc482e79bd9fbc0070bb8895d701fee489fdf0cdfcc180e2607");
        var expectedBytes = ReadPinned(root, "fixtures/reference/openai-completions-stream/expected.json", "3d8214d2471e67580a14fbc8441aa35721c559312463e570411aa01dba68183d");
        var lockBytes = ReadPinned(root, "tools/ReferenceOracle/openai-completions-stream.lock.json", "0f5f4ca5d6a6c93a95c4a0c27b640ff21c3460c9aef8e1112c0700c0605fffe9");
        using var input = JsonDocument.Parse(inputBytes, new() { MaxDepth = 64 });
        using var expected = JsonDocument.Parse(expectedBytes, new() { MaxDepth = 64 });
        using var oracleLock = JsonDocument.Parse(lockBytes, new() { MaxDepth = 64 });
        const string sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
        Equal(sourceSha, input.RootElement.GetProperty("sourceSha").GetString());
        Equal(sourceSha, expected.RootElement.GetProperty("sourceSha").GetString());
        Equal(sourceSha, oracleLock.RootElement.GetProperty("environmentPins").GetProperty("sourceSha").GetString());
        var observations = expected.RootElement.GetProperty("observations"); var checks = observations.GetProperty("checks");
        Equal(3, checks.GetProperty("caseCount").GetInt32()); Equal(3, checks.GetProperty("fakeFetchCalls").GetInt32());
        Check(checks.GetProperty("noSourceTransformOrSdkShim").GetBoolean() && checks.GetProperty("networkAndProcessesBlocked").GetBoolean(), "Source capture provenance changed.");
        var cases = input.RootElement.GetProperty("cases"); Equal(3, cases.GetArrayLength());
        Equal(3, observations.GetProperty("cases").GetArrayLength());
        var modelBody = input.RootElement.GetProperty("model"); var common = input.RootElement.GetProperty("commonOptions");
        var model = new ModelDescriptor(modelBody.GetProperty("id").GetString()!, modelBody.GetProperty("api").GetString()!, modelBody.GetProperty("provider").GetString()!);
        var names = new[] { "text-and-final-usage", "interleaved-two-tools-and-text", "thinking-signature-and-cache-usage" };
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var item = cases[index]; var captured = observations.GetProperty("cases")[index]; var options = item.GetProperty("options");
            Equal(names[index], item.GetProperty("caseId").GetString()); Equal(names[index], captured.GetProperty("caseId").GetString());
            Equal(1, captured.GetProperty("fetchRequests").GetArrayLength()); var fetch = captured.GetProperty("fetchRequests")[0];
            Equal("POST", fetch.GetProperty("method").GetString());
            var sdkText = fetch.GetProperty("body").GetString()!;
            Equal(fetch.GetProperty("bodyUtf8Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sdkText))));
            using var sdkBody = JsonDocument.Parse(sdkText);
            Check(Same(sdkBody.RootElement, fetch.GetProperty("bodyJson")), "Captured complete raw and parsed SDK bodies differ.");
            var entries = item.GetProperty("context").GetProperty("messages").EnumerateArray().Select(Entry).ToImmutableArray();
            var originals = entries.Select(entry => entry.WireBody.ToString()).ToArray();
            var reasoning = item.TryGetProperty("modelOverrides", out var overrides) ? overrides.GetProperty("reasoning").GetBoolean() : modelBody.GetProperty("reasoning").GetBoolean();
            var factory = new CompletionsKeyAuthRequestFactory(new(fetch.GetProperty("url").GetString()!), model,
                new(Reasoning: reasoning), new(MaxTokens: common.GetProperty("maxTokens").GetDouble(), Temperature: common.GetProperty("temperature").GetDouble(),
                    ReasoningEffort: options.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null,
                    CacheRetention: options.TryGetProperty("cacheRetention", out var cache) && cache.GetString() == "long" ? CompletionsCacheRetention.Long : CompletionsCacheRetention.Short,
                    SessionId: options.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                    ToolChoice: options.TryGetProperty("toolChoice", out var choice) ? JsonData.FromElement(choice) : null,
                    ModelHeaders: JsonData.FromElement(modelBody.GetProperty("headers")), Headers: JsonData.FromElement(common.GetProperty("headers"))));
            using var request = factory.Create(new(model, entries, input.RootElement.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64()), common.GetProperty("apiKey").GetString()!);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(Same(sdkBody.RootElement, body.RootElement), "Complete request body differs from genuine SDK capture: " + names[index] + "\nExpected: " + sdkBody.RootElement.GetRawText() + "\nActual: " + body.RootElement.GetRawText());
            Check(originals.SequenceEqual(entries.Select(entry => entry.WireBody.ToString())), "Factory changed canonical history.");
            if (body.RootElement.TryGetProperty("tools", out var tools))
                Check(tools.EnumerateArray().All(tool => !tool.GetProperty("function").TryGetProperty("strict", out _)), "Source default strict-incapable profile acquired a strict field.");
        }
    }
    private static async Task HistoryReplay()
    {
        var request = Request(
            E("""{"role":"system","content":"Base","sections":{"z":"Z","10":"Ten","2":"Two","keep":"Old"},"toolsAdded":[{"name":"a","description":"A","parameters":{}},{"name":"b","description":"B","parameters":{}}],"timestamp":0,"opaque":{"n":1.00,"keep":null}}"""),
            E("""{"role":"user","content":"Question","timestamp":1}"""),
            Assistant([new TextContent("Before"), new ToolCallContent("call-a", "a", JsonData.Parse("{\"value\":2,\"keep\":null}"))], StopReason.ToolUse),
            E("""{"role":"system","content":"Update","sections":{"z":null,"keep":"New","next":"Next"},"toolsRemoved":[{"name":"a"}],"toolsAdded":[{"name":"b","description":"B2","parameters":{}},{"name":"a","description":"A2","parameters":{}}],"timestamp":2}"""),
            E("""{"role":"toolResult","toolCallId":"call-a","toolName":"a","content":[{"type":"text","text":"first"},{"type":"text","text":"second\u0000"}],"isError":false,"timestamp":3,"details":{"wide":9007199254740993,"missing":null}}"""),
            E("""{"role":"user","content":[{"type":"text","text":""},{"type":"text","text":"Next"}],"timestamp":4}"""));
        var originals = request.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
        using var http = Factory().Create(request, Key); using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync());
        var root = body.RootElement; Equal("Base\n\nUpdate\n\nTwo\n\nTen\n\nNew\n\nNext", root.GetProperty("messages")[0].GetProperty("content").GetString());
        var messages = root.GetProperty("messages"); Equal(5, messages.GetArrayLength());
        Equal("call-a", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Equal("{\"value\":2,\"keep\":null}", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Equal("first\nsecond\0", messages[3].GetProperty("content").GetString()); Equal("tool", messages[3].GetProperty("role").GetString());
        Check(!messages[3].TryGetProperty("details", out _), "Provider projection forwarded metadata source does not forward.");
        Equal("Next", messages[4].GetProperty("content")[0].GetProperty("text").GetString());
        Check(root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).SequenceEqual(new[] { "b", "a" }), "Removal/readd order differs.");
        Equal("B2", root.GetProperty("tools")[0].GetProperty("function").GetProperty("description").GetString());
        Check(originals.SequenceEqual(request.Messages.Select(entry => entry.WireBody.ToString())), "History/declaration replay rewrote raw source entries.");

        var orphan = Request(Assistant([new ToolCallContent("orphan", "a", JsonData.EmptyObject)], StopReason.ToolUse), E("""{"role":"user","content":"Next","timestamp":4}"""));
        var projected = new CompletionsTranscriptProjector().Project(orphan).Value;
        Equal("No result provided", projected[1].GetProperty("content").GetString()); Equal("orphan", projected[1].GetProperty("tool_call_id").GetString());
        var failed = Request(Assistant([new ToolCallContent("", "", JsonData.EmptyObject)], StopReason.Aborted), E("""{"role":"user","content":"Retry","timestamp":4}"""));
        Equal(1, new CompletionsTranscriptProjector().Project(failed).Value.GetArrayLength());
        var removed = Request(E("""{"role":"system","content":"","toolsAdded":[{"name":"a","description":"A","parameters":{}}],"timestamp":0}"""),
            Assistant([new ToolCallContent("call", "a", JsonData.EmptyObject)], StopReason.ToolUse),
            E("""{"role":"system","content":"","toolsRemoved":[{"name":"a"}],"timestamp":1}"""));
        using var noTools = Factory().Create(removed, Key); using var noToolsBody = JsonDocument.Parse(await noTools.Content!.ReadAsStringAsync());
        Equal(0, noToolsBody.RootElement.GetProperty("tools").GetArrayLength());
        var anchored = new CompletionsTranscriptProjector(new(SupportsMidConversationSystemMessages: true)).Project(request).Value;
        Equal("tool", anchored[3].GetProperty("role").GetString());
        Equal("Update\n\nRemoved system prompt section \"z\".\n\nUpdated system prompt section \"keep\":\n\nNew\n\nUpdated system prompt section \"next\":\n\nNext", anchored[4].GetProperty("content").GetString());
        const string rawNumbers = """{"one":1.00,"negative":-0,"wide":9007199254740993,"overflow":1e400,"decimal":0.12345678901234567890123,"tiny":1e-7,"fixed":1e20,"threshold":1e21}""";
        var numericHistory = Request(Assistant([new ToolCallContent("numeric", "a", JsonData.Parse(rawNumbers))], StopReason.ToolUse));
        var numericOriginal = numericHistory.Messages[0].WireBody.ToString();
        var projectedArguments = new CompletionsTranscriptProjector().Project(numericHistory).Value[0].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString();
        Equal("""{"one":1,"negative":0,"wide":9007199254740992,"overflow":null,"decimal":0.12345678901234568,"tiny":1e-7,"fixed":100000000000000000000,"threshold":1e+21}""", projectedArguments);
        Equal(numericOriginal, numericHistory.Messages[0].WireBody.ToString());
        await HistoryIdentityAdmission();
    }
    private static async Task HistoryIdentityAdmission()
    {
        // COMPL-REQ-1: retain the independent review's original three inputs and controls.
        // Global ambiguity rejection is native admission hardening; source ordered mapping is separate authority.
        var model = new ModelDescriptor("review-model", "openai-completions", "openai");
        var factory = new CompletionsKeyAuthRequestFactory(new Uri("https://example.invalid/v1/chat/completions"), model);
        TranscriptEntry Call(bool foreign, params string[] ids) => new("assistant", PiWireJson.WriteMessage(new(
            foreign ? "openai-responses" : model.Api, model.Provider, model.Id, 1,
            ids.Select(id => (AssistantContent)new ToolCallContent(id, "probe", JsonData.EmptyObject)).ToImmutableArray(), TokenUsage.Zero, StopReason.ToolUse)));
        TranscriptEntry Result(string id) => E(JsonSerializer.Serialize(new { role = "toolResult", toolCallId = id,
            toolName = "probe", content = new[] { new { type = "text", text = "result for " + id } }, isError = false, timestamp = 2 }));
        ChatRequest History(params TranscriptEntry[] entries) => new(model, entries.ToImmutableArray(), 123);
        var nextUser = E("""{"role":"user","content":"Next turn","timestamp":3}""");
        var rejected = new (string Name, ChatRequest History)[]
        {
            ("original forward preserved-normalized collision", History(Call(false, "call_item"), Result("call_item"), Call(true, "call|item"), Result("call|item"))),
            ("original reverse normalized-preserved collision", History(Call(true, "call|item"), Result("call|item"), Call(false, "call_item"), Result("call_item"))),
            ("original reused ID and earlier-link rewrite", History(Call(false, "call|item"), Result("call|item"), Call(true, "call|item"), Result("call|item"))),
            ("same-model same-batch duplicate", History(Call(false, "dup", "dup"))),
            ("foreign same-batch outgoing collision", History(Call(true, "call_item", "call|item"))),
            ("same-model reused original across user turns", History(Call(false, "repeat"), Result("repeat"), nextUser, Call(false, "repeat"), Result("repeat"))),
            ("foreign reused original across user turns", History(Call(true, "repeat|item"), Result("repeat|item"), nextUser, Call(true, "repeat|item"), Result("repeat|item"))),
            ("foreign-then-same reused original", History(Call(true, "repeat|item"), Result("repeat|item"), Call(false, "repeat|item"), Result("repeat|item"))),
            ("foreign outgoing collision across assistant turns", History(Call(true, "call_item"), Result("call_item"), nextUser, Call(true, "call|item"), Result("call|item"))),
            ("foreign truncation collision across assistant turns", History(Call(true, new string('x', 40) + "one"), Result(new string('x', 40) + "one"), Call(true, new string('x', 40) + "two"), Result(new string('x', 40) + "two")))
        };
        foreach (var row in rejected)
        {
            var originals = row.History.Messages.Select(entry => entry.WireBody.ToString()).ToArray(); HttpRequestMessage? acquired = null;
            try { Throws(CompletionsRequestFailure.InvalidTranscript, () => acquired = factory.Create(row.History, "inert-review-key")); }
            finally { acquired?.Dispose(); }
            Check(acquired is null, "Ambiguous history acquired a request before rejecting: " + row.Name);
            Check(originals.SequenceEqual(row.History.Messages.Select(entry => entry.WireBody.ToString())), "Rejected history was rewritten: " + row.Name);
        }
        var accepted = new (bool FirstForeign, string First, string FirstWire, bool SecondForeign, string Second, string SecondWire)[]
        {
            (false, "original", "original", true, "call|item", "call_item"),
            (true, "call|item", "call_item", false, "original", "original"),
            (false, "call|item", "call|item", true, "other|piece", "other_piece"),
            (true, "other|piece", "other_piece", false, "call|item", "call|item"),
            (false, "same-one", "same-one", false, "same-two", "same-two"),
            (true, "foreign|one", "foreign_one", true, "foreign|two", "foreign_two")
        };
        foreach (var row in accepted)
        {
            var history = History(Call(row.FirstForeign, row.First), Result(row.First), Call(row.SecondForeign, row.Second), Result(row.Second));
            var originals = history.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
            object ExpectedCall(string id) => new { role = "assistant", content = (string?)null,
                tool_calls = new[] { new { id, type = "function", function = new { name = "probe", arguments = "{}" } } } };
            object ExpectedResult(string original, string wire) => new { role = "tool", tool_call_id = wire, content = "result for " + original };
            var expected = JsonData.Parse(JsonSerializer.Serialize(new { model = model.Id,
                messages = new[] { ExpectedCall(row.FirstWire), ExpectedResult(row.First, row.FirstWire), ExpectedCall(row.SecondWire), ExpectedResult(row.Second, row.SecondWire) },
                stream = true, stream_options = new { include_usage = true }, store = false, tools = Array.Empty<object>() }));
            using var request = factory.Create(history, "inert-review-key"); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(Same(expected.Value, body.RootElement), "Complete noncolliding history body changed: " + row.First + "/" + row.Second);
            Check(originals.SequenceEqual(history.Messages.Select(entry => entry.WireBody.ToString())), "Accepted history source values changed.");
        }
        // Source transform-messages.ts resolves results in transcript order. A future mapping cannot apply retroactively.
        var priorResult = History(Result("future|item"), Call(true, "future|item"), Result("future|item"));
        var causal = new CompletionsTranscriptProjector().Project(priorResult).Value;
        Equal("future|item", causal[0].GetProperty("tool_call_id").GetString());
        Equal("future_item", causal[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Equal("future_item", causal[2].GetProperty("tool_call_id").GetString());
        var orphan = new CompletionsTranscriptProjector().Project(History(Call(true, "orphan|item"), nextUser)).Value;
        Equal("orphan_item", orphan[0].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Equal("orphan_item", orphan[1].GetProperty("tool_call_id").GetString()); Equal("No result provided", orphan[1].GetProperty("content").GetString());
    }
    private static Task ReasoningAndDeclarations()
    {
        const string signature = """[{"type":"reasoning.summary","summary":"Summary","id":null,"opaque":{"keep":null,"n":1.00}},{"type":"reasoning.encrypted","data":"opaque\u0000","id":"signed"}]""";
        var properties = JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(JsonSerializer.Serialize(signature)));
        var request = Request(Assistant([new ThinkingContent("Private", properties), new TextContent("Answer"), new ToolCallContent("call", "a", JsonData.EmptyObject)], StopReason.ToolUse));
        var projected = new CompletionsTranscriptProjector(new(Reasoning: true)).Project(request).Value;
        var expectedSignature = signature.Replace("1.00", "1", StringComparison.Ordinal);
        Check(Same(JsonData.Parse(expectedSignature).Value, projected[0].GetProperty("reasoning_details")), "Complete signed reasoning details/opaque fields changed.");
        Check(!projected[0].TryGetProperty("reasoning_content", out _), "Structured reasoning was duplicated as raw reasoning text.");
        var cross = request with { Model = new("other", "openai-completions", "openai") };
        var other = new CompletionsTranscriptProjector().Project(cross).Value;
        Equal("PrivateAnswer", other[0].GetProperty("content").GetString()); Check(!other[0].TryGetProperty("reasoning_details", out _), "Cross-model reasoning signature leaked.");
        var schema = Request(E("""{"role":"system","content":"","toolsAdded":[{"name":"a","description":"A","parameters":{"type":"object","properties":{"value":{"type":"number","minimum":0.5},"opaque":{"x-num":1e400}},"x-unknown":{"wide":9007199254740993,"keep":null}}}],"timestamp":0}"""));
        var raw = schema.Messages[0].WireBody.ToString(); var plain = new CompletionsToolDeclarationProjector().Project(schema).Value[0].GetProperty("function");
        Equal("0.5", plain.GetProperty("parameters").GetProperty("properties").GetProperty("value").GetProperty("minimum").GetRawText());
        Equal(JsonValueKind.Null, plain.GetProperty("parameters").GetProperty("properties").GetProperty("opaque").GetProperty("x-num").ValueKind);
        Equal("9007199254740992", plain.GetProperty("parameters").GetProperty("x-unknown").GetProperty("wide").GetRawText());
        Check(!plain.TryGetProperty("strict", out _), "Default false capability did not omit strict."); Equal(raw, schema.Messages[0].WireBody.ToString());
        var capable = new CompletionsToolDeclarationProjector(new(SupportsStrictMode: true)).Project(schema).Value[0].GetProperty("function");
        Check(!capable.GetProperty("strict").GetBoolean(), "Source standard capable declaration must carry strict:false.");
        var preferred = Request(E("""{"role":"system","content":"","toolsAdded":[{"name":"a","description":"A","parameters":{"type":"object","properties":{"value":{"type":"string"}}},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}],"timestamp":0}"""));
        var strict = new CompletionsToolDeclarationProjector(new(SupportsStrictMode: true)).Project(preferred).Value[0].GetProperty("function");
        Check(strict.GetProperty("strict").GetBoolean(), "Supported strict preference was ignored.");
        Equal("value", strict.GetProperty("parameters").GetProperty("required")[0].GetString());
        var grammarFallback = Request(E("""{"role":"system","content":"","toolsAdded":[{"name":"a","description":"A","parameters":{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]},"constrainedSampling":{"type":"grammar","variants":{"openai_regex":".*"}}}],"timestamp":0}"""));
        Equal("function", new CompletionsToolDeclarationProjector().Project(grammarFallback).Value[0].GetProperty("type").GetString());
        Throws(CompletionsRequestFailure.UnsupportedContent, () => new CompletionsToolDeclarationProjector().Project(Request(E("""{"role":"system","content":"","toolsAdded":[{"name":"a","description":"A","parameters":{},"type":"custom"}],"timestamp":0}"""))));
        return Task.CompletedTask;
    }
    private static async Task OptionsAndOwnership()
    {
        var request = Request(E("""{"role":"user","content":"Exact π 😀\u0000","timestamp":0}"""));
        var factory = Factory(new(MaxTokens: -1.5, Temperature: 0, MaxTokensField: "max_tokens", SupportsStore: false, SupportsUsageInStreaming: false,
            CacheRetention: CompletionsCacheRetention.Long, SessionId: new string('a', 63) + "😀last", SendSessionAffinityHeaders: false,
            ModelHeaders: JsonData.Parse("{\"X-Model\":\"keep\",\"X-Override\":\"old\",\"X-Remove\":\"old\"}"),
            Headers: JsonData.Parse("{\"x-override\":\"new\",\"x-remove\":null,\"User-Agent\":\"Authored offline client\"}")));
        var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => factory.Create(request, Key))));
        try
        {
            for (var index = 0; index < requests.Length; index++)
                for (var prior = 0; prior < index; prior++) Check(!ReferenceEquals(requests[prior], requests[index]) && !ReferenceEquals(requests[prior].Content, requests[index].Content), "Concurrent creations shared owned request/content.");
            using var body = JsonDocument.Parse(await requests[1].Content!.ReadAsStringAsync()); var root = body.RootElement;
            Equal("-1.5", root.GetProperty("max_tokens").GetRawText()); Equal("0", root.GetProperty("temperature").GetRawText());
            Check(!root.TryGetProperty("store", out _) && !root.TryGetProperty("stream_options", out _), "Disabled capabilities were fabricated.");
            Equal(new string('a', 63) + "😀", root.GetProperty("prompt_cache_key").GetString()); Equal("24h", root.GetProperty("prompt_cache_retention").GetString());
            Equal("new", requests[1].Headers.GetValues("x-override").Single()); Check(!requests[1].Headers.Contains("x-remove"), "Null header removal lost.");
            Equal("Bearer " + Key, requests[1].Headers.GetValues("authorization").Single()); Equal("application/json", requests[1].Content!.Headers.ContentType!.ToString());
            requests[0].Dispose(); _ = await requests[1].Content!.ReadAsByteArrayAsync();
            using var zero = Factory(new(MaxTokens: 0, Temperature: -0d, CacheRetention: CompletionsCacheRetention.None, SessionId: "s")).Create(request, Key);
            using var zeroBody = JsonDocument.Parse(await zero.Content!.ReadAsStringAsync());
            Check(!zeroBody.RootElement.TryGetProperty("max_completion_tokens", out _) && !zeroBody.RootElement.TryGetProperty("prompt_cache_key", out _), "Source zero/none omission changed.");
            Equal("0", zeroBody.RootElement.GetProperty("temperature").GetRawText());
        }
        finally { foreach (var value in requests) value.Dispose(); }
    }
    private static Task AdmissionAndBounds()
    {
        var request = Request(E("""{"role":"user","content":"owned","timestamp":0}"""));
        foreach (var key in new[] { "", "bad\r\nsecret", "bad key", "π" }) Throws(CompletionsRequestFailure.InvalidKey, () => Factory().Create(request, key));
        Throws(CompletionsRequestFailure.InvalidRequest, () => Factory().Create(request with { Model = Model with { Api = "openai-responses" } }, Key));
        Throws(CompletionsRequestFailure.InvalidConfiguration, () => Factory(new(Headers: JsonData.Parse("{\"X-Secret\":\"private\\r\\nvalue\"}"))));
        using (var callerAuth = Factory(new(Headers: JsonData.Parse("{\"Authorization\":\"Bearer authored-caller-inert\"}"))).Create(request, Key))
            Equal("Bearer authored-caller-inert", callerAuth.Headers.GetValues("authorization").Single());
        Throws(CompletionsRequestFailure.InvalidConfiguration, () => Factory(new(Headers: JsonData.Parse("{\"Authorization\":\"private\\r\\nvalue\"}"))));
        Throws(CompletionsRequestFailure.InvalidConfiguration, () => Factory(new(MaxTokens: double.NaN)));
        Throws(CompletionsRequestFailure.ResourceLimit, () => Factory(new(MaximumPayloadBytes: 32)).Create(request, Key));
        Throws(CompletionsRequestFailure.ResourceLimit, () => new CompletionsTranscriptProjector(new(MaximumMessages: 1)).Project(request with { Messages = [request.Messages[0], request.Messages[0]] }));
        Throws(CompletionsRequestFailure.ResourceLimit, () => new CompletionsTranscriptProjector(new(MaximumOutputBytes: 32)).Project(Request(E("""{"role":"user","content":"😀😀😀😀😀😀😀😀","timestamp":0}"""))));
        Throws(CompletionsRequestFailure.ResourceLimit, () => new CompletionsTranscriptProjector(new(MaximumJsonDepth: 2)).Project(Request(E("""{"role":"user","content":"ok","opaque":{"deep":[]},"timestamp":0}"""))));
        // Both user/tool images now have source-backed projection; unsupported audio remains rejected.
        Throws(CompletionsRequestFailure.UnsupportedContent, () => Factory().Create(Request(E("""{"role":"toolResult","toolCallId":"audio-call","toolName":"probe","content":[{"type":"audio","data":"a","mimeType":"audio/wav"}],"timestamp":0}""")), Key));
        // A string value may hold a lone surrogate (Pi keeps it); the agent's TranscriptSurrogates drops it from message text first.
        _ = Factory().Create(Request(E("""{"role":"user","content":"\ud800","timestamp":0}""")), Key);
        using (var permissive = JsonDocument.Parse("{\"role\":\"user\",\"content\":\"ok\",\"timestamp\":0,}", new() { AllowTrailingCommas = true }))
            Throws(CompletionsRequestFailure.InvalidTranscript, () => Factory().Create(Request(new TranscriptEntry("user", JsonData.FromElement(permissive.RootElement))), Key));
        using (var permissive = JsonDocument.Parse("{\"role\":\"system\",\"content\":\"ok\",\"opaque\":{/*retained*/\"keep\":null},\"timestamp\":0}", new() { CommentHandling = JsonCommentHandling.Skip }))
            Throws(CompletionsRequestFailure.InvalidTranscript, () => Factory().Create(Request(new TranscriptEntry("system", JsonData.FromElement(permissive.RootElement))), Key));
        ThrowsAny<JsonException>(() => JsonData.Parse("{\"role\":\"system\",\"toolsAdded\":[{\"name\":\"a\",\"description\":\"A\",\"parameters\":{\"p\":1,\"\\u0070\":2}}]}"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        ThrowsAny<OperationCanceledException>(() => Factory().Create(request, Key, cancellation.Token));
        Check(!Same(JsonData.Parse("{\"n\":1}").Value, JsonData.Parse("{\"n\":1.00}").Value), "Comparator erased numeric spelling.");
        Check(!Same(JsonData.Parse("{\"a\":null}").Value, JsonData.EmptyObject.Value), "Comparator erased explicit null.");
        Check(!Same(JsonData.Parse("[1,2]").Value, JsonData.Parse("[2,1]").Value), "Comparator erased array order.");
        Check(Same(JsonData.Parse("{\"b\":2,\"a\":1}").Value, JsonData.Parse("{\"a\":1,\"b\":2}").Value), "Comparator should ignore only object key order.");
        return Task.CompletedTask;
    }
    private static CompletionsKeyAuthRequestFactory Factory(CompletionsKeyAuthRequestOptions? options = null) => new(Endpoint, Model, options: options);
    private static ChatRequest Request(params TranscriptEntry[] messages) => new(Model, messages.ToImmutableArray(), 1_700_000_000_000);
    private static TranscriptEntry E(string json) => Entry(JsonData.Parse(json).Value);
    private static TranscriptEntry Entry(JsonElement body) => new(body.GetProperty("role").GetString()!, JsonData.FromElement(body));
    private static TranscriptEntry Assistant(AssistantContent[] content, StopReason reason) => new("assistant", PiWireJson.WriteMessage(new(Model.Api, Model.Provider, Model.Id, 1, content.ToImmutableArray(), TokenUsage.Zero, reason)));
    private static byte[] ReadPinned(string root, string path, string hash)
    {
        var file = new FileInfo(Path.Combine(root, path)); Check(file.Exists && file.Length <= 1_048_576, "Pinned source fixture is absent or oversized: " + path);
        var bytes = File.ReadAllBytes(file.FullName); Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return bytes;
    }
    private static string FindRepo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "fixtures/reference/openai-completions-stream/input.json"))) return directory.FullName;
        throw new InvalidOperationException("Frozen Completions reference fixture root was not found.");
    }
    private static bool Same(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count() && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Same(property.Value, value)),
            JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => expected.GetRawText() == actual.GetRawText(),
            _ => expected.GetRawText() == actual.GetRawText()
        };
    }
    private static void Throws(CompletionsRequestFailure failure, Action action)
    {
        try { action(); }
        catch (CompletionsRequestException error) { Equal(failure, error.Failure); Check(!error.Message.Contains("private", StringComparison.Ordinal) && !error.Message.Contains(Key, StringComparison.Ordinal), "Rejected payload/key leaked into diagnostic."); return; }
        throw new InvalidOperationException("Expected Completions rejection: " + failure);
    }
    private static void ThrowsAny<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
}
