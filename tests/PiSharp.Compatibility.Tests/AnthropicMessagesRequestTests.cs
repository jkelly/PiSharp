using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

static class AnthropicMessagesRequestTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (nameof(CompleteStaticRequest), CompleteStaticRequest),
        (nameof(SystemSectionsAndToolState), SystemSectionsAndToolState),
        (nameof(ThinkingAndModelTransforms), ThinkingAndModelTransforms),
        (nameof(ToolResultsAndOrphans), ToolResultsAndOrphans),
        (nameof(ImagesAndDowngrades), ImagesAndDowngrades),
        (nameof(OAuthThinkingCacheAndBetas), OAuthThinkingCacheAndBetas),
        (nameof(StrictToolSchemasAndIdentityGuards), StrictToolSchemasAndIdentityGuards),
        (nameof(BoundsOwnershipAndCancellation), BoundsOwnershipAndCancellation),
        (nameof(StrictRetainedToolChoiceSyntax), StrictRetainedToolChoiceSyntax),
        (nameof(ToolChoiceAdmissionBoundsAndOwnership), ToolChoiceAdmissionBoundsAndOwnership),
        (nameof(NativeToolChangesExactWire), NativeToolChangesExactWire),
        (nameof(NativeToolChangesCachePlacement), NativeToolChangesCachePlacement),
        (nameof(NativeToolChangesRedefinitionAndCapabilityFallback), NativeToolChangesRedefinitionAndCapabilityFallback),
        (nameof(NativeToolChangesAdjacencyOAuthAndBounds), NativeToolChangesAdjacencyOAuthAndBounds),
        (nameof(NativeToolChangesRequestPublication), NativeToolChangesRequestPublication)
    ];
    private static ModelDescriptor Model => new("fixture-model", "anthropic-messages", "fixture-provider");
    private static ChatRequest Request(params TranscriptEntry[] messages) => new(Model, messages.ToImmutableArray(), 1_700_000_000_000);
    private static TranscriptEntry Entry(string json)
    { var owned = JsonData.Parse(json); return new(owned.Value.GetProperty("role").GetString()!, owned); }
    private static TranscriptEntry Assistant(AssistantContent[] content, string model = "fixture-model", StopReason reason = StopReason.Stop) =>
        new("assistant", PiWireJson.WriteMessage(new("anthropic-messages", "fixture-provider", model, 1, content.ToImmutableArray(), TokenUsage.Zero, reason)));
    private static JsonFields Signature(string value, bool redacted = false) => JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(JsonSerializer.Serialize(value)))
        .Set("redacted", JsonData.Parse(redacted ? "true" : "false"));
    private static TranscriptEntry Result(string id, string content = "done") => Entry(JsonSerializer.Serialize(new
    { role = "toolResult", toolCallId = id, toolName = "read", content = new[] { new { type = "text", text = content } }, isError = false, timestamp = 1 }));
    private static JsonData Project(ChatRequest request, AnthropicMessagesRequestOptions? options = null) =>
        new AnthropicMessagesRequestProjector(options ?? new(128, CacheRetention: AnthropicCacheRetention.None)).Project(request);

    private const string InitialRead = """{"role":"system","content":"Base","toolsAdded":[{"name":"read","description":"Read","parameters":{}}]}""";
    private const string ReplaceRead = """{"role":"system","toolsRemoved":[{"name":"read"}],"toolsAdded":[{"name":"write","description":"Write","parameters":{}}]}""";
    private static AnthropicMessagesRequestOptions NativeOptions => new(128, SupportsMidConversationSystemMessages: true,
        SupportsMidConversationToolChanges: true, CacheRetention: AnthropicCacheRetention.None);
    private static void ExactJson(string expected, JsonElement actual) => Assert(JsonElement.DeepEquals(JsonData.Parse(expected).Value, actual), "Exact authored wire mismatch: " + actual.GetRawText());

    // Pi abe508e1b89912adde45528136c3221eb69acdd7 (1.0.1): inline-tools-2026-09-15 replaces mid-conversation-tool-changes-2026-07-01.
    // Later tools are defined by value in tool_addition blocks; the request-level list stays initial tools plus placeholder.
    private const string Placeholder = """{"name":"__pi_deferred_placeholder__","description":"Reserved placeholder. Never available. Never call this.","input_schema":{"type":"object","properties":{},"required":[]},"defer_loading":true}""";
    private static string Definition(string name, string description, string schema = """{"type":"object","properties":{},"required":[]}""", string extra = ""","eager_input_streaming":true""") =>
        """{"type":"tool_addition","tool":{"type":"tool_definition","definition":{"name":""" + "\"" + name + "\",\"description\":\"" + description + "\",\"input_schema\":" + schema + extra + "}}}";

    public static Task NativeToolChangesExactWire()
    {
        var request = Request(Entry(InitialRead), Entry("""{"role":"user","content":"Question"}"""), Entry(ReplaceRead),
            Assistant([new TextContent("Reply")]), Entry("""{"role":"system","toolsRemoved":[{"name":"write"}]}"""));
        var before = request.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
        var actual = Project(request, NativeOptions).Value;
        ExactJson("""{"model":"fixture-model","messages":[{"role":"user","content":"Question"},{"role":"system","content":[{"type":"tool_removal","tool":{"type":"tool_reference","name":"read"}},""" + Definition("write", "Write") + """]},{"role":"assistant","content":[{"type":"text","text":"Reply"}]},{"role":"system","content":[{"type":"tool_removal","tool":{"type":"tool_reference","name":"write"}}]}],"max_tokens":128,"stream":true,"system":[{"type":"text","text":"Base"}],"tools":[{"name":"read","description":"Read","input_schema":{"type":"object","properties":{},"required":[]},"eager_input_streaming":true},""" + Placeholder + """],"betas":["inline-tools-2026-09-15"]}""", actual);
        for (var index = 0; index < before.Length; index++) Equal(before[index], request.Messages[index].WireBody.ToString());
        // The prefix already contains the stable placeholder before any later tool exists, and later tools never extend it.
        var first = Project(Request(Entry(InitialRead)), NativeOptions).Value.GetProperty("tools");
        ExactJson(actual.GetProperty("tools").GetRawText(), first);
        return Task.CompletedTask;
    }

    public static Task NativeToolChangesCachePlacement()
    {
        var initial = Entry("""{"role":"system","toolsAdded":[{"name":"a","description":"A","parameters":{}},{"name":"b","description":"B","parameters":{}}]}""");
        var update = Entry("""{"role":"system","toolsRemoved":[{"name":"a"}],"toolsAdded":[{"name":"c","description":"C","parameters":{}}]}""");
        foreach (var retention in new[] { AnthropicCacheRetention.Short, AnthropicCacheRetention.Long })
        {
            var actual = Project(Request(initial, update), NativeOptions with { CacheRetention = retention }).Value;
            var tools = actual.GetProperty("tools"); Equal(3, tools.GetArrayLength());
            var cache = retention == AnthropicCacheRetention.Long ? """{"type":"ephemeral","ttl":"1h"}""" : """{"type":"ephemeral"}""";
            ExactJson(cache, tools[1].GetProperty("cache_control"));
            foreach (var index in new[] { 0, 2 }) Assert(!tools[index].TryGetProperty("cache_control", out _), "Cache breakpoint moved off the last initial tool.");
            var refs = actual.GetProperty("messages")[0].GetProperty("content");
            Assert(!refs[0].TryGetProperty("cache_control", out _), "Removal acquired a premature breakpoint.");
            ExactJson(cache, refs[1].GetProperty("cache_control"));
            Assert(!refs[1].GetProperty("tool").GetProperty("definition").TryGetProperty("cache_control", out _), "Inline definition acquired a tool breakpoint.");
        }
        var disabled = Project(Request(initial, update), NativeOptions with { CacheRetention = AnthropicCacheRetention.Long, SupportsCacheControlOnTools = false }).Value;
        Assert(disabled.GetProperty("tools").EnumerateArray().All(tool => !tool.TryGetProperty("cache_control", out _)), "Tool cache capability ignored.");
        Assert(disabled.GetProperty("messages")[0].GetProperty("content")[1].TryGetProperty("cache_control", out _), "Tool capability erased message caching.");
        return Task.CompletedTask;
    }

    public static Task NativeToolChangesRedefinitionAndCapabilityFallback()
    {
        var same = Entry("""{"role":"system","toolsAdded":[{"parameters":{},"description":"Read","name":"read","opaque":"ignored"}]}""");
        var repeated = Project(Request(Entry(InitialRead), same), NativeOptions).Value;
        Equal(2, repeated.GetProperty("tools").GetArrayLength());
        ExactJson("""[{"role":"system","content":[""" + Definition("read", "Read") + "]}]", repeated.GetProperty("messages"));
        var readded = Project(Request(Entry(InitialRead), Entry("""{"role":"system","toolsRemoved":[{"name":"read"}]}"""), same), NativeOptions).Value;
        Equal(2, readded.GetProperty("messages").GetArrayLength()); Equal(2, readded.GetProperty("tools").GetArrayLength());
        // Redefinitions no longer fall back to the static tool list: each is sent inline under the same name.
        foreach (var (changed, definition) in new[]
        {
            ("""{"role":"system","toolsAdded":[{"name":"read","description":"Changed","parameters":{}}]}""", Definition("read", "Changed")),
            ("""{"role":"system","toolsAdded":[{"name":"read","description":"Read","parameters":{"properties":{"x":{"type":"string"}}}}]}""",
                Definition("read", "Read", """{"type":"object","properties":{"x":{"type":"string"}},"required":[]}""")),
            ("""{"role":"system","toolsAdded":[{"name":"read","description":"Read","parameters":{},"constrainedSampling":false}]}""", Definition("read", "Read")),
            // A removal in the same record as the new definition is dropped: the definition replaces the old tool.
            ("""{"role":"system","toolsRemoved":[{"name":"read"}],"toolsAdded":[{"name":"read","description":"Changed","parameters":{}}]}""", Definition("read", "Changed"))
        })
        {
            var request = Request(Entry(InitialRead), Entry(changed));
            var actual = Project(request, NativeOptions).Value;
            ExactJson("""[{"name":"read","description":"Read","input_schema":{"type":"object","properties":{},"required":[]},"eager_input_streaming":true},""" + Placeholder + "]", actual.GetProperty("tools"));
            ExactJson("""[{"role":"system","content":[""" + definition + "]}]", actual.GetProperty("messages"));
            ExactJson("""["inline-tools-2026-09-15"]""", actual.GetProperty("betas"));
            var fallback = Project(request, NativeOptions with { SupportsMidConversationToolChanges = false }).Value;
            Equal(1, fallback.GetProperty("tools").GetArrayLength()); Equal(0, fallback.GetProperty("messages").GetArrayLength());
            Assert(!fallback.TryGetProperty("betas", out _), "Disabled capability enabled native tool beta.");
        }
        var ordered = Entry("""{"role":"system","toolsAdded":[{"name":"read","description":"Read","parameters":{"title":"T","type":"object"}}]}""");
        var reordered = Entry("""{"role":"system","toolsAdded":[{"name":"read","description":"Read","parameters":{"type":"object","title":"T"}}]}""");
        var keyOrder = Project(Request(ordered, reordered), NativeOptions).Value;
        Equal(2, keyOrder.GetProperty("tools").GetArrayLength()); Equal(1, keyOrder.GetProperty("messages")[0].GetProperty("content").GetArrayLength());
        var numeric = """{"role":"system","toolsAdded":[{"name":"read","description":"Read","parameters":{"properties":{"x":{"type":"number","minimum":1.0}}}}]}""";
        Equal(2, Project(Request(Entry(numeric), Entry(numeric.Replace("1.0", "1"))), NativeOptions).Value.GetProperty("tools").GetArrayLength());
        foreach (var options in new[] { NativeOptions with { SupportsMidConversationSystemMessages = false }, NativeOptions with { SupportsMidConversationToolChanges = false } })
            Equal(1, Project(Request(Entry(InitialRead), Entry(ReplaceRead)), options).Value.GetProperty("tools").GetArrayLength());
        foreach (var request in new[] { Request(Entry("""{"role":"system","content":"Base"}"""), Entry(ReplaceRead)),
            Request(Entry("""{"role":"user","content":"Question"}"""), Entry(InitialRead), Entry(ReplaceRead)) })
            Equal(1, Project(request, NativeOptions).Value.GetProperty("tools").GetArrayLength());
        return Task.CompletedTask;
    }

    public static Task NativeToolChangesAdjacencyOAuthAndBounds()
    {
        var request = Request(Entry(InitialRead), Assistant([new ToolCallContent("call", "read", JsonData.EmptyObject)]),
            Entry(ReplaceRead), Result("call"), Entry("""{"role":"user","content":"Next"}"""), Assistant([new TextContent("Reply")]));
        var actual = Project(request, NativeOptions with { OAuthProjection = true }).Value;
        var messages = actual.GetProperty("messages"); Equal(5, messages.GetArrayLength());
        Equal("Read", messages[0].GetProperty("content")[0].GetProperty("name").GetString());
        Equal("tool_result", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
        ExactJson("""[{"type":"tool_removal","tool":{"type":"tool_reference","name":"Read"}},""" + Definition("Write", "Write") + "]", messages[3].GetProperty("content"));
        Equal("Read", actual.GetProperty("tools")[0].GetProperty("name").GetString()); Equal(2, actual.GetProperty("tools").GetArrayLength());
        // Removing "read" and defining "Read" project to one OAuth name; the inline definition replaces it without a static-list collision.
        var renamed = Project(Request(Entry(InitialRead), Entry(ReplaceRead.Replace("write", "Read"))), NativeOptions with { OAuthProjection = true }).Value;
        ExactJson("""[{"role":"system","content":[{"type":"tool_removal","tool":{"type":"tool_reference","name":"Read"}},""" + Definition("Read", "Write") + "]}]", renamed.GetProperty("messages"));
        // Native hardening: a record cannot define two tools under one projected name or the reserved placeholder.
        var collision = Entry("""{"role":"system","toolsAdded":[{"name":"write","description":"one","parameters":{}},{"name":"Write","description":"two","parameters":{}}]}""");
        Throws(AnthropicRequestFailure.IdentityCollision, () => Project(Request(Entry(InitialRead), collision), NativeOptions with { OAuthProjection = true }));
        Throws(AnthropicRequestFailure.IdentityCollision, () => Project(Request(Entry(InitialRead.Replace("read", "__pi_deferred_placeholder__"))), NativeOptions));
        Throws(AnthropicRequestFailure.IdentityCollision, () => Project(Request(Entry(InitialRead), Entry(ReplaceRead.Replace("write", "__pi_deferred_placeholder__"))), NativeOptions));
        // The fixed initial tools plus placeholder must fit; later definitions no longer grow the request-level list.
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(Entry(InitialRead), Entry(ReplaceRead)), NativeOptions with { MaximumActiveTools = 1 }));
        Equal(2, Project(Request(Entry(InitialRead), Entry(ReplaceRead)), NativeOptions with { MaximumActiveTools = 2 }).Value.GetProperty("tools").GetArrayLength());
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(Entry(InitialRead), Entry(ReplaceRead)), NativeOptions with { MaximumContentBlocks = 1 }));
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(Entry(InitialRead), Entry(ReplaceRead)), NativeOptions with { MaximumOutputBytes = 32 }));
        var strict = Entry("""{"role":"system","toolsAdded":[{"name":"write","description":"Write","parameters":{"type":"object","properties":{"x":{"type":"string"}}},"constrainedSampling":{"type":"json_schema","strict":"require"}}]}""");
        var strictTool = Project(Request(Entry(InitialRead), strict), NativeOptions with { SupportsStrictTools = true, SupportsEagerToolInputStreaming = false }).Value;
        ExactJson(Definition("write", "Write", """{"type":"object","properties":{"x":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["x"],"additionalProperties":false}""", ""","strict":true"""),
            strictTool.GetProperty("messages")[0].GetProperty("content")[0]);
        ExactJson("""["fine-grained-tool-streaming-2025-05-14","inline-tools-2026-09-15"]""", strictTool.GetProperty("betas"));
        return Task.CompletedTask;
    }

    public static async Task NativeToolChangesRequestPublication()
    {
        var request = Request(Entry(InitialRead), Entry(ReplaceRead));
        var expected = Project(request, NativeOptions).Value;
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid"), Model, NativeOptions);
        using var published = factory.Create(request, "authored-inert-key-noncredential");
        var actual = JsonData.Parse(await published.Content!.ReadAsStringAsync()).Value;
        var body = JsonNodeWithoutBetas(expected);
        ExactJson(body, actual); Equal("inline-tools-2026-09-15", published.Headers.GetValues("anthropic-beta").Single());
        foreach (var header in new[] { "null", "\"custom-beta\"" })
        {
            var overrideFactory = new AnthropicMessagesKeyAuthRequestFactory(new Uri("https://authored.invalid"), Model, NativeOptions,
                new(Headers: JsonData.Parse("{\"anthropic-beta\":" + header + "}")));
            using var owned = overrideFactory.Create(request, "authored-inert-key-noncredential");
            ExactJson(body, JsonData.Parse(await owned.Content!.ReadAsStringAsync()).Value);
            if (header == "null") Assert(!owned.Headers.Contains("anthropic-beta"), "Explicit null beta was ignored.");
            else Equal("custom-beta", owned.Headers.GetValues("anthropic-beta").Single());
        }
    }
    private static string JsonNodeWithoutBetas(JsonElement expected)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())!.AsObject(); body.Remove("betas"); return body.ToJsonString();
    }

    public static Task CompleteStaticRequest()
    {
        var request = Request(Entry("""{"role":"system","content":"System","sections":{"policy":"Policy"},"toolsAdded":[{"name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}}],"timestamp":0}"""),
            Entry("""{"role":"user","content":"Question","timestamp":1}"""));
        var actual = Project(request).Value;
        Equal(6, actual.EnumerateObject().Count());
        Equal("fixture-model", actual.GetProperty("model").GetString()); Equal(128, actual.GetProperty("max_tokens").GetInt32()); Assert(actual.GetProperty("stream").GetBoolean(), "Streaming flag lost.");
        Equal("System\n\nPolicy", actual.GetProperty("system")[0].GetProperty("text").GetString());
        Equal("Question", actual.GetProperty("messages")[0].GetProperty("content").GetString());
        var tool = actual.GetProperty("tools")[0]; Equal("read", tool.GetProperty("name").GetString()); Assert(tool.GetProperty("eager_input_streaming").GetBoolean(), "Default eager input streaming absent.");
        var schema = tool.GetProperty("input_schema"); Equal("object", schema.GetProperty("type").GetString()); Equal("path", schema.GetProperty("required")[0].GetString());
        Assert(!schema.TryGetProperty("additionalProperties", out _) && !tool.TryGetProperty("strict", out _), "Legacy schema retained fields not forwarded by source.");
        Assert(!actual.TryGetProperty("betas", out _) && !actual.TryGetProperty("temperature", out _), "Omitted options were fabricated.");
        Assert(!actual.GetProperty("system")[0].TryGetProperty("cache_control", out _), "Disabled cache was reintroduced.");
        return Task.CompletedTask;
    }

    public static Task SystemSectionsAndToolState()
    {
        Throws(AnthropicRequestFailure.InvalidTranscript, () => Project(Request(Entry("""{"role":"system","content":["invalid"],"timestamp":0}"""))));
        var request = Request(
            Entry("""{"role":"system","content":"Base","sections":{"z":"Z","10":"Ten","2":"Two","keep":"Old"},"toolsAdded":[{"name":"a","description":"A","parameters":{}},{"name":"b","description":"B","parameters":{}}],"timestamp":0}"""),
            Entry("""{"role":"user","content":"Question","timestamp":1}"""),
            Entry("""{"role":"system","content":[{"type":"text","text":"Update"}],"sections":{"z":null,"keep":"New","next":"Next"},"toolsRemoved":[{"name":"a"}],"toolsAdded":[{"name":"b","description":"B2","parameters":{}},{"name":"a","description":"A2","parameters":{}}],"timestamp":2}"""));
        var actual = Project(request).Value;
        Equal("Base\n\nUpdate\n\nTwo\n\nTen\n\nNew\n\nNext", actual.GetProperty("system")[0].GetProperty("text").GetString());
        Equal(1, actual.GetProperty("messages").GetArrayLength());
        Equal("b", actual.GetProperty("tools")[0].GetProperty("name").GetString()); Equal("B2", actual.GetProperty("tools")[0].GetProperty("description").GetString());
        Equal("a", actual.GetProperty("tools")[1].GetProperty("name").GetString());
        var nativeSystems = Project(request, new(128, SupportsMidConversationSystemMessages: true, CacheRetention: AnthropicCacheRetention.None)).Value;
        Equal("Base\n\nTwo\n\nTen\n\nZ\n\nOld", nativeSystems.GetProperty("system")[0].GetProperty("text").GetString());
        var update = nativeSystems.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString();
        Equal("Update\n\nRemoved system prompt section \"z\".\n\nUpdated system prompt section \"keep\":\n\nNew\n\nUpdated system prompt section \"next\":\n\nNext", update);
        return Task.CompletedTask;
    }

    public static Task ThinkingAndModelTransforms()
    {
        var native = Assistant([new ThinkingContent("", Signature("signed")), new ThinkingContent("plain"), new ThinkingContent("", Signature("cipher", true)),
            new TextContent("\uFEFF"), new TextContent("\u0085")]);
        var same = Project(Request(native)).Value.GetProperty("messages")[0].GetProperty("content");
        Equal(4, same.GetArrayLength()); Equal("thinking", same[0].GetProperty("type").GetString()); Equal("", same[0].GetProperty("thinking").GetString());
        Equal("plain", same[1].GetProperty("text").GetString()); Equal("cipher", same[2].GetProperty("data").GetString()); Equal("\u0085", same[3].GetProperty("text").GetString());
        var emptyAllowed = Project(Request(Assistant([new ThinkingContent("plain", Signature(""))])), new(128, AllowEmptyThinkingSignatures: true, CacheRetention: AnthropicCacheRetention.None)).Value;
        Equal("", emptyAllowed.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("signature").GetString());
        var cross = Assistant([new ThinkingContent("", Signature("signed")), new ThinkingContent("cross", Signature("sig")),
            new ThinkingContent("hidden", Signature("cipher", true)), new ToolCallContent("call|item", "read", JsonData.Parse("{\"x\":null}"))], "other-model");
        var output = Project(Request(cross, Result("call|item"))).Value.GetProperty("messages");
        Equal(2, output[0].GetProperty("content").GetArrayLength()); Equal("cross", output[0].GetProperty("content")[0].GetProperty("text").GetString());
        Equal("call_item", output[0].GetProperty("content")[1].GetProperty("id").GetString()); Equal("call_item", output[1].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert(!output[0].GetProperty("content")[0].TryGetProperty("signature", out _), "Cross-model signature leaked.");
        var failed = Project(Request(Assistant([new TextContent("failed")], reason: StopReason.Error), Entry("""{"role":"user","content":"retry"}"""))).Value;
        Equal(1, failed.GetProperty("messages").GetArrayLength()); Equal("retry", failed.GetProperty("messages")[0].GetProperty("content").GetString());
        return Task.CompletedTask;
    }

    public static Task ToolResultsAndOrphans()
    {
        var tool = Assistant([new ToolCallContent("one", "read", JsonData.EmptyObject), new ToolCallContent("two", "read", JsonData.EmptyObject)]);
        var request = Request(tool, Entry("""{"role":"system","content":"Update","timestamp":1}"""), Result("one"), Entry("""{"role":"user","content":"next"}"""), Assistant([new TextContent("reply")]));
        var output = Project(request, new(128, SupportsMidConversationSystemMessages: true, CacheRetention: AnthropicCacheRetention.None)).Value.GetProperty("messages");
        Equal(5, output.GetArrayLength()); Equal("assistant", output[0].GetProperty("role").GetString());
        var results = output[1].GetProperty("content"); Equal(2, results.GetArrayLength()); Equal("done", results[0].GetProperty("content").GetString());
        Equal("two", results[1].GetProperty("tool_use_id").GetString()); Equal("No result provided", results[1].GetProperty("content").GetString()); Assert(results[1].GetProperty("is_error").GetBoolean(), "Orphan result not marked error.");
        Equal("next", output[2].GetProperty("content").GetString()); Equal("system", output[3].GetProperty("role").GetString()); Equal("reply", output[4].GetProperty("content")[0].GetProperty("text").GetString());
        var finalOrphan = Project(Request(Assistant([new ToolCallContent("final", "read", JsonData.EmptyObject)]))).Value.GetProperty("messages");
        Equal(2, finalOrphan.GetArrayLength()); Assert(!finalOrphan[1].GetProperty("content")[0].TryGetProperty("timestamp", out _), "Projection invented a clock field.");
        Throws(AnthropicRequestFailure.UnmatchedToolResult, () => Project(Request(Result("missing"))));
        Throws(AnthropicRequestFailure.UnmatchedToolResult, () => Project(Request(tool, Result("one"), Result("one"))));
        Throws(AnthropicRequestFailure.IdentityCollision, () => Project(Request(Assistant([new ToolCallContent("a|b", "read", JsonData.EmptyObject), new ToolCallContent("a b", "read", JsonData.EmptyObject)], "other"))));
        return Task.CompletedTask;
    }

    public static Task ImagesAndDowngrades()
    {
        const string images = """{"role":"user","content":[{"type":"text","text":" "},{"type":"image","mimeType":"image/png","data":"AA=="},{"type":"image","mimeType":"image/jpeg","data":"AQ=="},{"type":"text","text":"caption"}]}""";
        var tool = Assistant([new ToolCallContent("image", "read", JsonData.EmptyObject)]);
        var imageResult = Entry("""{"role":"toolResult","toolCallId":"image","toolName":"read","content":[{"type":"image","mimeType":"image/webp","data":"Ag=="}],"isError":false}""");
        var output = Project(Request(Entry(images), tool, imageResult)).Value.GetProperty("messages");
        Equal(3, output[0].GetProperty("content").GetArrayLength()); Equal("image/png", output[0].GetProperty("content")[0].GetProperty("source").GetProperty("media_type").GetString());
        var content = output[2].GetProperty("content")[0].GetProperty("content"); Equal("(see attached image)", content[0].GetProperty("text").GetString()); Equal("Ag==", content[1].GetProperty("source").GetProperty("data").GetString());
        var downgraded = Project(Request(Entry(images), tool, imageResult), new(128, ModelSupportsImages: false, CacheRetention: AnthropicCacheRetention.None)).Value.GetProperty("messages");
        Equal(2, downgraded[0].GetProperty("content").GetArrayLength()); Equal("(image omitted: model does not support images)", downgraded[0].GetProperty("content")[0].GetProperty("text").GetString());
        Equal("(tool image omitted: model does not support images)", downgraded[2].GetProperty("content")[0].GetProperty("content").GetString());
        var mixed = Entry("""{"role":"toolResult","toolCallId":"image","content":[{"type":"text","text":""},{"type":"image","mimeType":"image/gif","data":"Aw=="}],"isError":true}""");
        var mixedContent = Project(Request(tool, mixed)).Value.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("content");
        Equal("", mixedContent[0].GetProperty("text").GetString()); Equal(2, mixedContent.GetArrayLength());
        return Task.CompletedTask;
    }

    public static Task OAuthThinkingCacheAndBetas()
    {
        var request = Request(Entry("""{"role":"system","content":"System","toolsAdded":[{"name":"read","description":"Read","parameters":{}}]}"""), Entry("""{"role":"user","content":"Question"}"""));
        var options = new AnthropicMessagesRequestOptions(256, ModelReasoning: true, ThinkingEnabled: true, ThinkingBudgetTokens: 0,
            ThinkingDisplay: "omitted", Temperature: 0.5m, OAuthProjection: true, CacheRetention: AnthropicCacheRetention.Long,
            SupportsEagerToolInputStreaming: false, UserId: "synthetic", ToolChoice: JsonData.Parse("""{"type":"tool","name":"Read"}"""),
            AllowedFallbackModels: ["fallback"]);
        var output = Project(request, options).Value;
        Equal("You are Claude Code, Anthropic's official CLI for Claude.", output.GetProperty("system")[0].GetProperty("text").GetString()); Equal(2, output.GetProperty("system").GetArrayLength());
        Equal("Read", output.GetProperty("tools")[0].GetProperty("name").GetString()); Assert(!output.GetProperty("tools")[0].TryGetProperty("eager_input_streaming", out _), "Unsupported eager flag emitted.");
        Equal("1h", output.GetProperty("tools")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Equal("1h", output.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Equal(1024, output.GetProperty("thinking").GetProperty("budget_tokens").GetInt32()); Equal("omitted", output.GetProperty("thinking").GetProperty("display").GetString()); Assert(!output.TryGetProperty("temperature", out _), "Thinking did not suppress temperature.");
        Equal(5, output.GetProperty("betas").GetArrayLength()); Equal("server-side-fallback-2026-07-01", output.GetProperty("betas")[4].GetString()); Equal("fallback", output.GetProperty("fallbacks")[0].GetProperty("model").GetString());
        Equal("synthetic", output.GetProperty("metadata").GetProperty("user_id").GetString()); Equal("Read", output.GetProperty("tool_choice").GetProperty("name").GetString());
        var adaptive = Project(request, options with { ForceAdaptiveThinking = true, Effort = "xhigh", BetaFeatures = [], SupportsLongCacheRetention = false }).Value;
        Equal("adaptive", adaptive.GetProperty("thinking").GetProperty("type").GetString()); Equal("xhigh", adaptive.GetProperty("output_config").GetProperty("effort").GetString());
        Assert(!adaptive.TryGetProperty("betas", out _) && !adaptive.GetProperty("system")[0].GetProperty("cache_control").TryGetProperty("ttl", out _), "Explicit beta suppression or long-cache downgrade ignored.");
        var off = Project(request, new(128, ModelReasoning: true, ThinkingEnabled: false, Temperature: 0.5m, CacheRetention: AnthropicCacheRetention.None, ToolChoice: JsonData.Parse("\"auto\""))).Value;
        Equal("disabled", off.GetProperty("thinking").GetProperty("type").GetString()); Equal(0.5m, off.GetProperty("temperature").GetDecimal()); Equal("auto", off.GetProperty("tool_choice").GetProperty("type").GetString());
        var configured = Project(request, new(128, BetaFeatures: [" one ", "one", "", "two"], CacheRetention: AnthropicCacheRetention.None)).Value.GetProperty("betas");
        Equal(2, configured.GetArrayLength()); Equal("one", configured[0].GetString()); Equal("two", configured[1].GetString());
        return Task.CompletedTask;
    }

    public static Task StrictToolSchemasAndIdentityGuards()
    {
        const string strict = """{"role":"system","content":"","toolsAdded":[{"name":"strict","description":"Strict","parameters":{"type":"object","properties":{"optional":{"type":"string"},"required":{"type":"integer"}},"required":["required"],"description":"Schema"},"constrainedSampling":{"type":"json_schema","strict":"require"}}]}""";
        var schema = Project(Request(Entry(strict)), new(128, SupportsStrictTools: true, CacheRetention: AnthropicCacheRetention.None)).Value.GetProperty("tools")[0];
        Assert(schema.GetProperty("strict").GetBoolean(), "Strict flag missing."); var input = schema.GetProperty("input_schema");
        Equal(2, input.GetProperty("required").GetArrayLength()); Assert(!input.GetProperty("additionalProperties").GetBoolean(), "Strict object did not close properties.");
        Equal("null", input.GetProperty("properties").GetProperty("optional").GetProperty("anyOf")[1].GetProperty("type").GetString());
        Equal("Schema", input.GetProperty("description").GetString());
        Throws(AnthropicRequestFailure.UnsupportedStrictSchema, () => Project(Request(Entry(strict))));
        var unsupported = strict.Replace("\"type\":\"string\"", "\"$ref\":\"private-schema\"");
        Throws(AnthropicRequestFailure.UnsupportedStrictSchema, () => Project(Request(Entry(unsupported)), new(128, SupportsStrictTools: true)));
        var preferred = unsupported.Replace("\"strict\":\"require\"", "\"strict\":\"prefer\"");
        var legacy = Project(Request(Entry(preferred)), new(128, SupportsStrictTools: true, CacheRetention: AnthropicCacheRetention.None)).Value.GetProperty("tools")[0];
        Assert(!legacy.TryGetProperty("strict", out _), "Prefer did not fall back."); Equal("private-schema", legacy.GetProperty("input_schema").GetProperty("properties").GetProperty("optional").GetProperty("$ref").GetString());
        var collision = Entry("""{"role":"system","content":"","toolsAdded":[{"name":"read","description":"one","parameters":{}},{"name":"Read","description":"two","parameters":{}}]}""");
        Throws(AnthropicRequestFailure.IdentityCollision, () => Project(Request(collision), new(128, OAuthProjection: true)));
        var thought = Assistant([new ToolCallContent("raw", "read", JsonData.Parse("""{"n":1.0,"big":9007199254740993,"nil":null}"""), JsonFields.Empty.Set("namespace", JsonData.Parse("\"inert\"")))]);
        var preserved = Project(Request(thought)).Value.GetProperty("messages")[0].GetProperty("content")[0];
        Equal("1", preserved.GetProperty("input").GetProperty("n").GetRawText()); Equal("9007199254740992", preserved.GetProperty("input").GetProperty("big").GetRawText());
        Assert(!preserved.TryGetProperty("namespace", out _), "Anthropic wire acquired an unsupported namespace field.");
        Equal("9007199254740993", ((ToolCallContent)PiWireJson.ReadMessage(thought.WireBody.Value).Content[0]).Arguments.Value.GetProperty("big").GetRawText());
        foreach (var (raw, expected) in new[] { ("-0", "0"), ("1e0", "1"), ("0.000001", "0.000001"), ("1e-7", "1e-7"), ("1e20", "100000000000000000000"), ("1e21", "1e+21"), ("-0.5", "-0.5") })
        {
            var numeric = Assistant([new ToolCallContent("numeric", "read", JsonData.Parse("{\"n\":" + raw + "}"))]);
            Equal(expected, Project(Request(numeric)).Value.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input").GetProperty("n").GetRawText());
        }
        Throws(AnthropicRequestFailure.UnsupportedNumber, () => Project(Request(Assistant([new ToolCallContent("numeric", "read", JsonData.Parse("{\"n\":1e999}"))]))));
        return Task.CompletedTask;
    }

    public static Task BoundsOwnershipAndCancellation()
    {
        var user = Entry(JsonSerializer.Serialize(new { role = "user", content = "\u03C0\U0001F600e\u0301", opaque = new { nil = (string?)null } }));
        var request = Request(user); var raw = user.WireBody.ToString(); var output = Project(request).ToString();
        var bytes = Encoding.UTF8.GetByteCount(output); Assert(bytes > output.Length, "Unicode byte boundary probe was ASCII.");
        var identity = Model.Id.Length + Model.Provider.Length;
        var exact = new AnthropicMessagesRequestOptions(128, CacheRetention: AnthropicCacheRetention.None, MaximumMessages: 1,
            MaximumEntryCharacters: raw.Length, MaximumInputCharacters: raw.Length + identity, MaximumOutputCharacters: output.Length, MaximumOutputBytes: bytes);
        Equal(output, Project(request, exact).ToString()); Equal(raw, user.WireBody.ToString());
        foreach (var options in new[] { exact with { MaximumEntryCharacters = raw.Length - 1 }, exact with { MaximumInputCharacters = raw.Length + identity - 1 },
            exact with { MaximumOutputCharacters = output.Length - 1 }, exact with { MaximumOutputBytes = bytes - 1 } })
            Throws(AnthropicRequestFailure.ResourceLimit, () => Project(request, options));
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(user, user), exact));
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(request, exact with { MaximumJsonDepth = 1 }));
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(Entry("""{"role":"system","content":"","toolsAdded":[{"name":"one","description":"","parameters":{}},{"name":"two","description":"","parameters":{}}]}""")), new(128, MaximumActiveTools: 1)));
        using var document = JsonDocument.Parse("""{"role":"user","content":"text","opaque":{"n":1,}}""", new JsonDocumentOptions { AllowTrailingCommas = true });
        var permissive = new TranscriptEntry("user", JsonData.FromElement(document.RootElement));
        Throws(AnthropicRequestFailure.InvalidTranscript, () => Project(Request(permissive)));
        Throws(AnthropicRequestFailure.UnsupportedUnicode, () => Project(Request(Entry("""{"role":"user","content":"\uD800"}"""))));
        var invalid = Entry("""{"role":"user","content":[{"type":"audio","data":"private"}]}""");
        var error = Throws(AnthropicRequestFailure.UnsupportedContent, () => Project(Request(invalid)));
        Assert(!error.Message.Contains("private", StringComparison.Ordinal), "Projection diagnostic leaked payload.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { new AnthropicMessagesRequestProjector(exact).Project(request, cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException errorCancelled) { Equal(cancellation.Token, errorCancelled.CancellationToken); }
        Equal(raw, user.WireBody.ToString()); Equal(output, Project(request).ToString());
        foreach (var options in new[] { new AnthropicMessagesRequestOptions(0), new(128, MaximumJsonDepth: 65), new(128, ThinkingDisplay: "private"), new(128, Effort: "private") })
        { try { _ = new AnthropicMessagesRequestProjector(options); throw new Exception("Invalid options accepted."); } catch (ArgumentOutOfRangeException) { } }
        return Task.CompletedTask;
    }
    public static Task StrictRetainedToolChoiceSyntax()
    {
        foreach (var raw in new[]
        {
            """{"type":"tool","name":"read",}""",
            """{"type":"tool",/*private-choice*/"name":"read"}""",
            "{\"type\":\"tool\",//private-choice\n\"name\":\"read\"}",
            """{"type":"tool","name":"read","opaque":[1,2,]}""",
            """{"type":"tool","name":"read","opaque":{"x":1,}}""",
            """{"type":"tool","name":"read","opaque":[1,/*private-choice*/2]}"""
        })
        {
            JsonData choice;
            using (var document = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                choice = JsonData.FromElement(document.RootElement);
            var retained = choice.ToString(); Equal(raw, retained);
            var error = Throws(AnthropicRequestFailure.InvalidTranscript, () => Project(Request(), new(128, ToolChoice: choice)));
            Assert(!error.Message.Contains("private-choice", StringComparison.Ordinal), "Option syntax diagnostic leaked caller payload.");
            Equal(retained, choice.ToString());
        }
        foreach (var type in new[] { "auto", "any", "none" })
        {
            var choice = JsonData.Parse(JsonSerializer.Serialize(type));
            Equal(type, Project(Request(), new(128, ToolChoice: choice)).Value.GetProperty("tool_choice").GetProperty("type").GetString());
        }
        var strict = JsonData.Parse("""{"type":"tool","name":"read","opaque":{"n":1.0,"nil":null},"order":[2,1]}""");
        var before = strict.ToString(); var output = Project(Request(), new(128, ToolChoice: strict)).Value.GetProperty("tool_choice");
        Equal("read", output.GetProperty("name").GetString()); Equal(JsonValueKind.Null, output.GetProperty("opaque").GetProperty("nil").ValueKind);
        Equal(2, output.GetProperty("order")[0].GetInt32()); Equal(before, strict.ToString());
        return Task.CompletedTask;
    }

    public static Task ToolChoiceAdmissionBoundsAndOwnership()
    {
        var choice = JsonData.Parse("""{"type":"tool","name":"read"}"""); var raw = choice.ToString();
        var identity = Model.Id.Length + Model.Provider.Length;
        var exact = new AnthropicMessagesRequestOptions(128, CacheRetention: AnthropicCacheRetention.None, ToolChoice: choice,
            MaximumInputCharacters: identity + raw.Length);
        Equal("read", Project(Request(), exact).Value.GetProperty("tool_choice").GetProperty("name").GetString());
        Equal("read", Project(Request(), exact with { MaximumInputCharacters = identity + raw.Length + 1 }).Value.GetProperty("tool_choice").GetProperty("name").GetString());
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(), exact with { MaximumInputCharacters = identity + raw.Length - 1 }));
        var deep = JsonData.Parse("""{"type":"tool","name":"read","opaque":{"items":[0]}}""");
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(), new(128, ToolChoice: deep, MaximumJsonDepth: 2)));
        Throws(AnthropicRequestFailure.UnsupportedNumber, () => Project(Request(), new(128, ToolChoice: JsonData.Parse("""{"type":"tool","name":"read","opaque":1e999}"""))));
        Throws(AnthropicRequestFailure.UnsupportedUnicode, () => Project(Request(), new(128, ToolChoice: JsonData.Parse("""{"type":"tool","name":"\uD800"}"""))));
        JsonData malformed;
        using (var document = JsonDocument.Parse("""{"type":"tool","name":"read",}""", new JsonDocumentOptions { AllowTrailingCommas = true }))
            malformed = JsonData.FromElement(document.RootElement);
        // Raw-size rejection occurs before strict parsing; conversion cannot erase syntax to evade the budget.
        Throws(AnthropicRequestFailure.ResourceLimit, () => Project(Request(), new(128, ToolChoice: malformed, MaximumInputCharacters: identity + malformed.ToString().Length - 1)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { new AnthropicMessagesRequestProjector(exact).Project(Request(), cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException error) { Equal(cancellation.Token, error.CancellationToken); }
        Equal(raw, choice.ToString()); return Task.CompletedTask;
    }

    private static AnthropicRequestException Throws(AnthropicRequestFailure expected, Action action)
    { try { action(); throw new Exception("Expected " + expected); } catch (AnthropicRequestException error) { Equal(expected, error.Failure); return error; } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; observed {actual}."); }
}
