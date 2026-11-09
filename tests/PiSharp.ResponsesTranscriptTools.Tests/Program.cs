using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Authored expected behavior from v1.5.0 transcript-tool-changes.test.ts and openai-responses-shared.ts.
// No live provider or regenerated historical captures.
static class Program
{
    static int checks;
    static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    static TranscriptEntry System(string text = "", string[]? add = null, string[]? remove = null, string description = "tool")
    {
        var body = new JsonObject { ["role"] = "system", ["content"] = text, ["timestamp"] = 0 };
        if (add is not null) body["toolsAdded"] = new JsonArray(add.Select(name => (JsonNode)new JsonObject {
            ["name"] = name, ["description"] = description, ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }).ToArray());
        if (remove is not null) body["toolsRemoved"] = new JsonArray(remove.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray());
        return new("system", JsonData.Parse(body.ToJsonString()));
    }
    static TranscriptEntry User() => new("user", JsonData.Parse("""{"role":"user","content":"go","timestamp":1}"""));
    static JsonData Metadata(ModelDescriptor model, bool mid, bool additional, bool search) => JsonData.Parse(new JsonObject {
        ["id"] = model.Id, ["api"] = model.Api, ["provider"] = model.Provider, ["baseUrl"] = "https://proxy.invalid/v1",
        ["reasoning"] = false, ["input"] = new JsonArray("text"), ["maxTokens"] = 4096, ["contextWindow"] = 100000,
        ["compat"] = new JsonObject { ["supportsMidConvoSystemMessages"] = mid, ["supportsAdditionalTools"] = additional, ["supportsToolSearch"] = search }
    }.ToJsonString());
    static async Task<JsonElement> Project(string route, ImmutableArray<TranscriptEntry> history, bool mid = true, bool additional = false, bool search = false)
    {
        var model = new ModelDescriptor("test", route == "azure" ? "azure-openai-responses" : route == "codex" ? "openai-codex-responses" : "openai-responses", route == "azure" ? "azure" : route == "codex" ? "openai-codex" : "openai");
        var request = new ChatRequest(model, history);
        if (route == "azure") return new AzureResponsesRequestFactory(model, new(Metadata(model, mid, additional, search), new(false))).ProjectPayload(request).Value;
        if (route == "codex")
        {
            using var client = new HttpClient();
            var transport = new OpenAICodexResponsesTransport(client, model, Metadata(model, mid, additional, search), new(), _ => ValueTask.FromResult("inert-unused"));
            return JsonData.Parse(JsonUtf16.ToJsonString(transport.BuildBody(request))).Value;
        }
        var projection = new ResponsesTranscriptProjectionOptions(false, SupportsMidConversationSystemMessages: mid)
        { SupportsAdditionalTools = additional, SupportsToolSearch = search };
        using var sent = new ResponsesKeyAuthRequestFactory(new("https://proxy.invalid/v1/responses"), model, projection).Create(request, "inert-key");
        return JsonData.Parse(await sent.Content!.ReadAsStringAsync()).Value;
    }
    static string[] Names(JsonElement tools) => tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
    static JsonElement[] Input(JsonElement body) => body.GetProperty("input").EnumerateArray().ToArray();
    static JsonElement[] Typed(JsonElement body, string type) => Input(body).Where(item => item.TryGetProperty("type", out var kind) && kind.GetString() == type).ToArray();
    static string[] Texts(JsonElement body) => Input(body).Where(item => item.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String).Select(item => item.GetProperty("content").GetString()!).ToArray();
    static void Prefix(JsonElement prior, JsonElement later, string label)
    {
        var before = Input(prior); var after = Input(later); Check(before.Length <= after.Length, label);
        for (int i = 0; i < before.Length; i++) Check(JsonElement.DeepEquals(before[i], after[i]), label + " item " + i);
    }
    static async Task Main()
    {
        foreach (var route in new[] { "responses", "azure", "codex" })
        {
            ImmutableArray<TranscriptEntry> first = [System("base", ["base", "drop"]), User()];
            var empty = await Project(route, [], additional: true); Check(!empty.TryGetProperty("tools", out _) && Typed(empty, "additional_tools").Length == 0, route + " empty");
            var none = await Project(route, first.Add(System("late", ["late"], ["drop"])));
            Check(Names(none.GetProperty("tools")).SequenceEqual(new[] { "base", "late" }), route + " none current tools");
            Check(Typed(none, "additional_tools").Length == 0 && Typed(none, "tool_search_call").Length == 0, route + " unsupported gate");
            var noMid = await Project(route, first.Add(System("late", ["late"])), mid: false, additional: true);
            Check(Names(noMid.GetProperty("tools")).SequenceEqual(new[] { "base", "drop", "late" }), route + " mid gate");
            var baseChanges = await Project(route, first, additional: true);
            Check(!baseChanges.TryGetProperty("tools", out _), route + " changes no top-level duplicates");
            Check(Names(Typed(baseChanges, "additional_tools").Single().GetProperty("tools")).SequenceEqual(new[] { "base", "drop" }), route + " initial declarations");
            var removed = first.Add(System(remove: ["drop"]));
            var afterRemoval = await Project(route, removed, additional: true);
            Prefix(baseChanges, afterRemoval, route + " remove prefix");
            Check(Texts(afterRemoval).Any(text => text.EndsWith("Do not call them:\n- drop", StringComparison.Ordinal)), route + " removal notice");
            var later = await Project(route, removed.Add(System(add: ["late"])), additional: true);
            Prefix(afterRemoval, later, route + " later load prefix");
            Check(Texts(later).Any(text => text.Contains("Still unavailable: drop.", StringComparison.Ordinal)), route + " persistent unavailability");
            var restored = await Project(route, removed.Add(System(add: ["drop"])), additional: true);
            Check(Texts(restored).Last().Contains("incremental tools update") && !Texts(restored).Last().Contains("Still unavailable"), route + " redeclaration restores");
            var redefined = await Project(route, first.Add(System(add: ["base"], remove: ["base"], description: "changed")), additional: true);
            Check(Typed(redefined, "additional_tools").Last().GetProperty("tools")[0].GetProperty("description").GetString() == "changed", route + " changed declaration");
            Check(!Texts(redefined).Any(text => text.Contains("no longer available")), route + " redefinition not removal");
            var additions = await Project(route, first.Add(System(add: ["late"])), search: true);
            Check(Names(additions.GetProperty("tools")).SequenceEqual(new[] { "base", "drop" }), route + " additions initial boundary");
            var call = Typed(additions, "tool_search_call").Single(); var result = Typed(additions, "tool_search_output").Single();
            Check(call.GetProperty("call_id").GetString() == result.GetProperty("call_id").GetString(), route + " search pairing");
            Check(call.GetProperty("arguments").GetProperty("query").GetString() == "late" && call.GetProperty("arguments").GetProperty("limit").GetInt32() == 1, route + " search arguments");
            Check(result.GetProperty("tools")[0].GetProperty("defer_loading").GetBoolean(), route + " deferred search result");
            var searchRemoved = await Project(route, removed, search: true);
            Check(Names(searchRemoved.GetProperty("tools")).SequenceEqual(new[] { "base" }), route + " removals fold");
            var searchLater = await Project(route, removed.Add(System(add: ["late"])), search: true);
            Prefix(searchRemoved, searchLater, route + " additive post-removal prefix");
            var searchRedefined = await Project(route, first.Add(System(add: ["base"], description: "changed")).Add(System(add: ["late"])), search: true);
            Check(searchRedefined.GetProperty("tools")[0].GetProperty("description").GetString() == "changed", route + " redefinition fold");
            Check(Typed(searchRedefined, "tool_search_output").Length == 1, route + " later addition only anchored");
            // Exact declaration UTF-16 fidelity across every placement and provider, with untouched canonical input.
            var escapedDescription = new TranscriptEntry("system", JsonData.Parse("""{"role":"system","content":"","toolsAdded":[{"name":"fidelity","description":"\ud800","parameters":{"type":"object"}}],"timestamp":2}"""));
            var escapedOriginal = escapedDescription.WireBody.ToString();
            foreach (var mode in new[] { "none", "changes", "additions" })
            {
                var fidelity = await Project(route, [System(add: ["base"]), User(), escapedDescription], additional: mode == "changes", search: mode == "additions");
                var declarations = mode == "changes" ? Typed(fidelity, "additional_tools").Last().GetProperty("tools") :
                    mode == "additions" ? Typed(fidelity, "tool_search_output").Single().GetProperty("tools") : fidelity.GetProperty("tools");
                var description = declarations.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "fidelity").GetProperty("description");
                Check(description.GetRawText() == "\"\\ud800\"" && JsonUtf16.GetString(description) == "\uD800", route + " " + mode + " exact escaped declaration description");
                Check(escapedDescription.WireBody.ToString() == escapedOriginal, route + " canonical description ownership");
            }
        }
        Console.WriteLine($"PASS Responses/Codex/Azure transcript tool matrix: {checks} assertions (authored v1.5.0 target).");
    }
}
