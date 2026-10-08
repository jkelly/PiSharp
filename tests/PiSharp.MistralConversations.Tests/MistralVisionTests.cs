using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class MistralVisionTests
{
    private static readonly ModelDescriptor Model = new("vision-fixture", "mistral-conversations", "mistral");
    private static MistralTextOptions Options => new(new("https://fixture.invalid/"), true, new(0, 0, 0, 0), "offline-fixture") { ApiKey = "explicit", SupportsImages = true };
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mistral-vision user mixed blocks expose camel imageUrl hook and string snake wire", UserImages),
        ("mistral-vision tool text first then original ordered images and exact linked IDs", ToolImages),
        ("mistral-vision tool image only whitespace error and no output fallbacks", ToolFallbacks),
        ("mistral-vision nonvision user and tool placeholders deduplicate before formatter", Downgrade),
        ("mistral-vision Simple model input overrides stale options both ways and budgets images", SimpleCapability),
        ("mistral-vision accepted hook imageUrl replacement reaches actual HTTP unchanged", HookReplacement),
        ("mistral-vision unsupported media roles and malformed supported images refuse before effects", Unsupported),
        ("mistral-vision raw image bounds precede downgrade hook and HTTP", RawBounds),
        ("mistral-vision hook payload and final wire byte expansion refuse before HTTP", ReplacementBounds)
    ];
    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonObject Image(string data = "AA==", string mime = "image/png") => new() { ["type"] = "image", ["mimeType"] = mime, ["data"] = data };
    private static TranscriptEntry User(params JsonNode[] content) => new("user", JsonData.Parse(new JsonObject
        { ["role"] = "user", ["content"] = new JsonArray(content), ["timestamp"] = 1 }.ToJsonString()));
    private static TranscriptEntry Assistant() => new("assistant", JsonData.Parse(JsonSerializer.Serialize(new
    {
        role = "assistant", provider = Model.Provider, api = Model.Api, model = Model.Id, stopReason = "toolUse", timestamp = 2,
        content = new[] { new { type = "toolCall", id = "same|raw-ID", name = "lookup", arguments = new Dictionary<string, object>() } }
    })));
    private static TranscriptEntry Result(JsonArray content, bool error = false) => new("toolResult", JsonData.Parse(new JsonObject
        { ["role"] = "toolResult", ["toolCallId"] = "same|raw-ID", ["toolName"] = "lookup", ["content"] = content, ["isError"] = error, ["timestamp"] = 3 }.ToJsonString()));
    private static JsonData Metadata(JsonNode? input) => JsonData.Parse(new JsonObject
        { ["id"] = Model.Id, ["provider"] = Model.Provider, ["api"] = Model.Api, ["contextWindow"] = 6000, ["maxTokens"] = 4000,
          ["reasoning"] = false, ["input"] = input }.ToJsonString());
    private sealed record CaptureResult(JsonData? Wire, StreamEvent[] Events, int Calls);
    private static async Task<CaptureResult> Capture(TranscriptEntry[] input, MistralTextOptions options, JsonData? metadata = null)
    {
        var before = input.Select(entry => entry.WireBody.ToString()).ToArray(); JsonData? wire = null;
        using var handler = new Handler(async request =>
        {
            wire = JsonData.Parse(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }); using var client = new HttpClient(handler);
        IChatTransport transport = metadata is null ? new MistralTextHttpSseTransport(client, Model, options) : new MistralSimpleHttpSseTransport(client, Model, metadata, options);
        var events = new List<StreamEvent>(); await foreach (var observation in transport.StreamAsync(new(Model, [.. input], 4))) events.Add(observation);
        Check(before.SequenceEqual(input.Select(entry => entry.WireBody.ToString())));
        return new(wire, [.. events], handler.Calls);
    }
    private static JsonElement Success(CaptureResult result)
    { Check(result.Calls == 1 && result.Events.Last() is StreamDone); return result.Wire!.Value.GetProperty("messages"); }
    private static void Refusal(CaptureResult result, NativeChatFailureCode code)
    { Check(result.Calls == 0 && result.Wire is null && result.Events.Last() is StreamError error && error.NativeDiagnostic?.Code == code && !result.Events.OfType<StreamStarted>().Any()); }
    private static async Task UserImages()
    {
        JsonData? hook = null; var calls = 0;
        var messages = Success(await Capture([User(Text("first"), Image(), Text("last"), Image("/w==", "image/jpeg"))],
            Options with { OnPayload = (payload, _, _) => { calls++; hook = payload; return ValueTask.FromResult<JsonData?>(null); } }));
        var content = messages[0].GetProperty("content"); var observed = hook!.Value.GetProperty("messages")[0].GetProperty("content");
        Check(calls == 1 && content.GetArrayLength() == 4 && content[0].GetProperty("text").GetString() == "first" && content[2].GetProperty("text").GetString() == "last");
        Check(content[1].GetProperty("image_url").GetString() == "data:image/png;base64,AA==" && content[3].GetProperty("image_url").GetString() == "data:image/jpeg;base64,/w==" &&
            observed[1].GetProperty("imageUrl").GetString() == "data:image/png;base64,AA==" && !observed[1].TryGetProperty("image_url", out _) && !content[1].TryGetProperty("imageUrl", out _));
        // Upstream interpolates the strings; it does not decode base64 or reject a provider-specific MIME.
        var raw = Success(await Capture([User(Image("not-base64", "custom/mime"), Image("", ""))], Options))[0].GetProperty("content");
        Check(raw[0].GetProperty("image_url").GetString() == "data:custom/mime;base64,not-base64" && raw[1].GetProperty("image_url").GetString() == "data:;base64,");
    }
    private static async Task ToolImages()
    {
        var messages = Success(await Capture([Assistant(), Result(new(Image(), Text("  first "), Image("second"), Text("last  ")))], Options));
        var content = messages[1].GetProperty("content");
        Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("tool_calls")[0].GetProperty("id").GetString() == "same|raw-ID" &&
            messages[1].GetProperty("tool_call_id").GetString() == "same|raw-ID" && messages[1].GetProperty("name").GetString() == "lookup");
        Check(content.GetArrayLength() == 3 && content[0].GetProperty("text").GetString() == "first \nlast" &&
            content[1].GetProperty("image_url").GetString() == "data:image/png;base64,AA==" && content[2].GetProperty("image_url").GetString() == "data:image/png;base64,second");
    }
    private static async Task ToolFallbacks()
    {
        foreach (var error in new[] { false, true }) foreach (var attached in new[] { false, true })
        foreach (var whitespace in new[] { false, true })
        {
            var blocks = new JsonArray(); if (whitespace) blocks.Add(Text(" \t\uFEFF\n")); if (attached) blocks.Add(Image());
            var content = Success(await Capture([Assistant(), Result(blocks, error)], Options))[1].GetProperty("content");
            Check(content[0].GetProperty("text").GetString() == (error ? "[tool error] " : "") + (attached ? "(see attached image)" : "(no tool output)") && content.GetArrayLength() == (attached ? 2 : 1));
        }
    }
    private static async Task Downgrade()
    {
        const string userPlaceholder = "(image omitted: model does not support images)";
        const string toolPlaceholder = "(tool image omitted: model does not support images)";
        var options = Options with { SupportsImages = false };
        var messages = Success(await Capture([User(Image(), Image(), Text("middle"), Text(userPlaceholder), Image()), Assistant(),
            Result(new(Image(), Image(), Text("middle"), Text(toolPlaceholder), Image()), true)], options));
        var user = messages[0].GetProperty("content"); var tool = messages[2].GetProperty("content");
        Check(user.GetArrayLength() == 3 && user[0].GetProperty("text").GetString() == userPlaceholder && user[1].GetProperty("text").GetString() == "middle" && user[2].GetProperty("text").GetString() == userPlaceholder);
        Check(tool.GetArrayLength() == 1 && tool[0].GetProperty("text").GetString() == "[tool error] " + toolPlaceholder + "\nmiddle\n" + toolPlaceholder);
        var only = Success(await Capture([User(new JsonObject { ["type"] = "image" }, Image())], options))[0].GetProperty("content");
        Check(only.GetArrayLength() == 1 && only[0].GetProperty("text").GetString() == userPlaceholder);
    }
    private static async Task SimpleCapability()
    {
        foreach (var supports in new[] { false, true })
        {
            var row = Metadata(supports ? new JsonArray("text", "image") : new JsonArray("text")); var original = row.ToString();
            var captured = await Capture([User(Image())], Options with { SupportsImages = !supports }, row);
            var part = Success(captured)[0].GetProperty("content")[0];
            Check(part.GetProperty("type").GetString() == (supports ? "image_url" : "text") && captured.Wire!.Value.GetProperty("max_tokens").GetDouble() == 532 && row.ToString() == original);
        }
        var absent = JsonNode.Parse(Metadata(new JsonArray("text", "image")).ToString())!.AsObject(); absent.Remove("input");
        Check(Success(await Capture([User(Image())], Options, JsonData.Parse(absent.ToJsonString())))[0].GetProperty("content")[0].GetProperty("type").GetString() == "text");
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No HTTP allowed.")));
        foreach (var invalid in new JsonNode[] { new JsonArray("audio"), JsonValue.Create("image")!, new JsonArray(1) })
        {
            try { _ = new MistralSimpleHttpSseTransport(client, Model, Metadata(invalid), Options); throw new InvalidOperationException("Invalid metadata accepted."); }
            catch (MistralTextException error) { Check(error.Code == NativeChatFailureCode.UnsupportedFeature); }
        }
    }
    private static async Task HookReplacement()
    {
        var replacement = JsonData.Parse(JsonSerializer.Serialize(new { model = Model.Id, stream = true,
            messages = new[] { new { role = "user", content = new[] { new { type = "image_url", imageUrl = "data:image/gif;base64,replaced" } } } } }));
        var before = replacement.ToString(); var options = Options with { OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(replacement) };
        Check(Success(await Capture([User(Text("original"))], options))[0].GetProperty("content")[0].GetProperty("image_url").GetString() == "data:image/gif;base64,replaced" && replacement.ToString() == before);
        Refusal(await Capture([User(Text("original"))], options with { SupportsImages = false }), NativeChatFailureCode.UnsupportedFeature);
    }
    private static async Task Unsupported()
    {
        var hooks = 0; var options = Options with { OnPayload = (_, _, _) => { hooks++; return ValueTask.FromResult<JsonData?>(null); } };
        foreach (var input in new[] {
            User(new JsonObject { ["type"] = "image", ["data"] = "missing mime" }),
            User(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = new JsonObject() }),
            User(new JsonObject { ["type"] = "document", ["data"] = "unsupported" }),
            new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":[{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"AA==\"}]}")),
            new TranscriptEntry("assistant", JsonData.Parse("{\"role\":\"assistant\",\"content\":[{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"AA==\"}]}")) })
            Refusal(await Capture([input], options), NativeChatFailureCode.UnsupportedFeature);
        Check(hooks == 0);
        var badHook = JsonData.Parse(JsonSerializer.Serialize(new { model = Model.Id, stream = true,
            messages = new[] { new { role = "user", content = new[] { new { type = "image_url", imageUrl = new { url = "data:image/png;base64,AA==" } } } } } }));
        Refusal(await Capture([User(Text("original"))], Options with { OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(badHook) }), NativeChatFailureCode.UnsupportedFeature);
    }
    private static async Task RawBounds()
    {
        var hooks = 0;
        foreach (var supports in new[] { false, true })
        {
            var options = Options with { SupportsImages = supports, OnPayload = (_, _, _) => { hooks++; return ValueTask.FromResult<JsonData?>(null); } };
            Refusal(await Capture([User(Image(new string('a', 1200)))], options with { MaximumContentCharacters = 500 }), NativeChatFailureCode.ResourceLimit);
            Refusal(await Capture([User(Image(new string('\u00E9', 500)))], options with { MaximumPayloadBytes = 500 }), NativeChatFailureCode.ResourceLimit);
            Refusal(await Capture([User(Image(), Image(), Image())], options with { MaximumContentBlocks = 2 }), NativeChatFailureCode.ResourceLimit);
        }
        Check(hooks == 0);
    }
    private static async Task ReplacementBounds()
    {
        var replacement = new JsonObject { ["model"] = Model.Id, ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "image_url", ["imageUrl"] = "data:image/png;base64," + new string('a', 800) }) }) };
        Refusal(await Capture([User(Text("tiny"))], Options with { MaximumPayloadBytes = 500,
            OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse(replacement.ToJsonString())) }), NativeChatFailureCode.ResourceLimit);
        var exact = JsonData.Parse(replacement.ToJsonString()); var before = exact.ToString();
        // imageUrl -> image_url adds one byte. Camel admission fits exactly; final wire must refuse.
        Refusal(await Capture([User(Text("tiny"))], Options with { MaximumPayloadBytes = Encoding.UTF8.GetByteCount(before),
            OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(exact) }), NativeChatFailureCode.ResourceLimit);
        Check(exact.ToString() == before);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { internal int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request); } }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Mistral vision fixture assertion failed."); }
}
