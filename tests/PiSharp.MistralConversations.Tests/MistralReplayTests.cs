using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class MistralReplayTests
{
    private static readonly ModelDescriptor Model = new("replay-fixture", "mistral-conversations", "mistral");
    private static MistralTextOptions Options => new(new("https://fixture.invalid/"), true, new(0, 0, 0, 0), "offline-fixture") { ApiKey = "explicit" };
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mistral-replay collapsed system sections and latest add remove replace tool declarations", CollapsedSystems),
        ("mistral-replay midconversation system update waits real and synthetic tool results", MidSystem),
        ("mistral-replay orphan repair closes at user assistant and EOF without duplicate results", OrphanBoundaries),
        ("mistral-replay failed assistants are omitted before provider and never create new synthetic results", FailedAssistants),
        ("mistral-replay same model IDs retained foreign IDs normalized and results linked", Identity),
        ("mistral-replay foreign thinking converted redaction dropped same model thinking retained", Thinking),
        ("mistral-replay null content normalized and ECMAScript arguments serialized without mutation", NullAndArguments),
        ("mistral-replay Simple model compat selects collapse or original system update path", SimpleCompat),
        ("mistral-replay refused transcript bounds precede hooks and HTTP without mutation", Bounds)
    ];
    private static TranscriptEntry Entry(string role, string json) => new(role, JsonData.Parse(json));
    private static TranscriptEntry User() => Entry("user", "{\"role\":\"user\",\"content\":\"next\",\"timestamp\":1}");
    private static JsonObject Call(string id, string name = "lookup") => new() { ["type"] = "toolCall", ["id"] = id, ["name"] = name, ["arguments"] = new JsonObject() };
    private static TranscriptEntry Assistant(JsonArray blocks, bool same = true, string stop = "toolUse") => Entry("assistant", new JsonObject
    { ["role"] = "assistant", ["content"] = blocks, ["stopReason"] = stop, ["provider"] = same ? Model.Provider : "foreign",
        ["api"] = same ? Model.Api : "foreign", ["model"] = same ? Model.Id : "foreign", ["timestamp"] = 2 }.ToJsonString());
    private static TranscriptEntry Result(string id) => Entry("toolResult", new JsonObject { ["role"] = "toolResult", ["toolCallId"] = id,
        ["toolName"] = "lookup", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "receipt" }), ["timestamp"] = 3 }.ToJsonString());
    private static async Task<JsonData> Capture(TranscriptEntry[] messages, MistralTextOptions? options = null, JsonData? metadata = null)
    {
        var before = messages.Select(message => message.WireBody.ToString()).ToArray(); JsonData? wire = null;
        using var handler = new Handler(async request =>
        {
            wire = JsonData.Parse(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }); using var client = new HttpClient(handler);
        IChatTransport transport = metadata is null ? new MistralTextHttpSseTransport(client, Model, options ?? Options) :
            new MistralSimpleHttpSseTransport(client, Model, metadata, options ?? Options);
        StreamEvent? last = null; await foreach (var observation in transport.StreamAsync(new(Model, [.. messages], 4))) last = observation;
        Check(last is StreamDone && handler.Calls == 1 && before.SequenceEqual(messages.Select(message => message.WireBody.ToString())));
        return wire ?? throw new InvalidOperationException("No captured request.");
    }
    private static async Task CollapsedSystems()
    {
        var head = Entry("system", "{\"role\":\"system\",\"content\":\"base\",\"sections\":{\"keep\":\"old\",\"remove\":\"gone\"},\"toolsAdded\":[{\"name\":\"old\",\"description\":\"old\",\"parameters\":{\"type\":\"object\"}},{\"name\":\"same\",\"description\":\"v1\",\"parameters\":{\"type\":\"object\"}}]}");
        var update = Entry("system", "{\"role\":\"system\",\"content\":\"update\",\"sections\":{\"keep\":\"new\",\"remove\":null,\"extra\":\"added\"},\"toolsRemoved\":[{\"name\":\"old\"}],\"toolsAdded\":[{\"name\":\"same\",\"description\":\"v2\",\"parameters\":{\"type\":\"object\"}},{\"name\":\"next\",\"description\":\"next\",\"parameters\":{\"type\":\"object\"}}]}");
        var wire = (await Capture([head, User(), update, User()])).Value;
        var messages = wire.GetProperty("messages").EnumerateArray().ToArray();
        Check(messages.Length == 3 && messages[0].GetProperty("content").GetString() == "base\n\nupdate\n\nnew\n\nadded" &&
            messages.Count(message => message.GetProperty("role").GetString() == "system") == 1);
        var tools = wire.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function")).ToArray();
        Check(tools.Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(["same", "next"]) && tools[0].GetProperty("description").GetString() == "v2");
    }
    private static async Task MidSystem()
    {
        var update = Entry("system", "{\"role\":\"system\",\"content\":\"update\",\"sections\":{\"first\":\"new\",\"removed\":null}}");
        var wire = (await Capture([User(), Assistant(new(Call("keep"), Call("missing"))), update, Result("keep"), User()],
            Options with { SupportsMidConversationSystemMessages = true })).Value;
        var messages = wire.GetProperty("messages").EnumerateArray().ToArray();
        Check(messages.Select(message => message.GetProperty("role").GetString()).SequenceEqual(["user", "assistant", "tool", "tool", "system", "user"]));
        Check(messages[2].GetProperty("tool_call_id").GetString() == "keep" && messages[3].GetProperty("tool_call_id").GetString() == "missing" &&
            messages[3].GetProperty("content")[0].GetProperty("text").GetString() == "[tool error] No result provided");
        Check(messages[4].GetProperty("content").GetString() == "update\n\nUpdated system prompt section \"first\":\n\nnew\n\nRemoved system prompt section \"removed\".");
    }
    private static async Task OrphanBoundaries()
    {
        foreach (var boundary in new[] { "user", "assistant", "EOF" })
        {
            var input = new List<TranscriptEntry> { User(), Assistant(new(Call("answered"), Call("orphan"))), Result("answered") };
            if (boundary == "user") input.Add(User());
            if (boundary == "assistant") input.Add(Assistant(new(new JsonObject { ["type"] = "text", ["text"] = "later" }), stop: "stop"));
            var messages = (await Capture([.. input])).Value.GetProperty("messages").EnumerateArray().ToArray();
            var tools = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            Check(tools.Length == 2 && tools.Select(tool => tool.GetProperty("tool_call_id").GetString()).SequenceEqual(["answered", "orphan"]));
        }
    }
    private static async Task FailedAssistants()
    {
        foreach (var stop in new[] { "error", "aborted" })
        {
            var messages = (await Capture([User(), Assistant(new(Call("valid-pending"))),
                Assistant(new(Call("failed-only"), new JsonObject { ["type"] = "text", ["text"] = "partial" }), stop: stop), User()])).Value.GetProperty("messages").EnumerateArray().ToArray();
            Check(messages.Count(message => message.GetProperty("role").GetString() == "assistant") == 1 &&
                messages.Count(message => message.GetProperty("role").GetString() == "tool") == 1 &&
                messages.Single(message => message.GetProperty("role").GetString() == "tool").GetProperty("tool_call_id").GetString() == "valid-pending");
        }
    }
    private static async Task Identity()
    {
        foreach (var same in new[] { true, false })
        {
            var wire = (await Capture([User(), Assistant(new(Call("long|foreign-ID")), same), Result("long|foreign-ID")])).Value;
            var messages = wire.GetProperty("messages").EnumerateArray().ToArray();
            var id = messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString()!;
            Check(id == messages[2].GetProperty("tool_call_id").GetString() && (same ? id == "long|foreign-ID" : id.Length == 9 && id.All(char.IsAsciiLetterOrDigit)));
        }
    }
    private static async Task Thinking()
    {
        foreach (var same in new[] { true, false })
        {
            var blocks = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "ordinary", ["thinkingSignature"] = "signature" },
                new JsonObject { ["type"] = "thinking", ["thinking"] = "redacted", ["redacted"] = true },
                new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["thinkingSignature"] = "opaque" });
            var wire = (await Capture([User(), Assistant(blocks, same, "stop")])).Value;
            var content = wire.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
            Check(same ? content.Length == 2 && content.All(part => part.GetProperty("type").GetString() == "thinking") :
                content.Length == 1 && content[0].GetProperty("type").GetString() == "text" && content[0].GetProperty("text").GetString() == "ordinary");
        }
    }
    private static async Task NullAndArguments()
    {
        var call = Call("call"); call["arguments"] = JsonNode.Parse("{\"9\":1,\"2\":2,\"n\":9007199254740993}");
        var wire = (await Capture([Entry("user", "{\"role\":\"user\",\"content\":null}"), Assistant(new(call)), Result("call")])).Value;
        var messages = wire.GetProperty("messages").EnumerateArray().ToArray();
        Check(messages.Length == 2 && messages[0].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString() == "{\"2\":2,\"9\":1,\"n\":9007199254740992}");
    }
    private static async Task SimpleCompat()
    {
        foreach (var enabled in new[] { false, true })
        {
            var row = JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, api = Model.Api, provider = Model.Provider,
                contextWindow = 20000, maxTokens = 1000, reasoning = false, compat = new { supportsMidConvoSystemMessages = enabled } }));
            var input = new[] { Entry("system", "{\"role\":\"system\",\"content\":\"base\",\"timestamp\":0}"), User(),
                Entry("system", "{\"role\":\"system\",\"content\":\"update\",\"timestamp\":2}") };
            var wire = (await Capture(input, Options with { SupportsMidConversationSystemMessages = !enabled }, row)).Value;
            Check(wire.GetProperty("messages").EnumerateArray().Count(message => message.GetProperty("role").GetString() == "system") == (enabled ? 2 : 1));
        }
    }
    private static async Task Bounds()
    {
        // Pi caps no message count; the replay character budget bounds the transcript before any payload hook or HTTP.
        var input = Enumerable.Repeat(User(), 257).ToArray(); var before = input.Select(message => message.WireBody.ToString()).ToArray(); var hooks = 0;
        var characters = input.Sum(message => message.WireBody.Value.GetRawText().Length);
        using var handler = new Handler(_ => throw new InvalidOperationException("Refused HTTP admission.")); using var client = new HttpClient(handler);
        var transport = new MistralTextHttpSseTransport(client, Model, Options with { MaximumContentCharacters = characters - 1,
            OnPayload = (_, _, _) => { hooks++; return ValueTask.FromResult<JsonData?>(null); } });
        var observations = new List<StreamEvent>(); await foreach (var observation in transport.StreamAsync(new(Model, [.. input]))) observations.Add(observation);
        Check(observations.Last() is StreamError { NativeDiagnostic.Code: NativeChatFailureCode.ResourceLimit } &&
            !observations.OfType<StreamStarted>().Any() && hooks == 0 && handler.Calls == 0 && before.SequenceEqual(input.Select(message => message.WireBody.ToString())));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { internal int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request); } }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Mistral replay fixture assertion failed."); }
}
