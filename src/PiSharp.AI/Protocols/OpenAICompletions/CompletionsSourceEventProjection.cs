using System.Text.Json.Nodes;
using System.Text.Json;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

internal sealed record CompletionsToolSourceView(int ContentIndex, int? WireIndex, string RawArguments,
    JsonData DisplayArguments, bool Ended)
{
    internal JsonData? CustomInput { get; init; }
    internal bool UndefinedPartialArguments { get; init; }
}

/// <summary>
/// Source-facing owned push snapshots. Native compact frames and their immutable
/// timing are distinct from the source queue's mutable message aliases.
/// </summary>
public static class CompletionsSourceEventProjection
{
    /// <summary>Selects bounded full push capture without changing the native progress contract.</summary>
    public static OpenAICompletionsWireOptions CaptureOwnedSnapshots(OpenAICompletionsWireOptions? options = null) =>
        (options ?? new()) with { CaptureSourceEmissionSnapshots = true };

    public static JsonData? ReadEmission(StreamEvent frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame is ToolCallHeaderUpdated ? null : frame.SourceEmissionSnapshot;
    }

    public static JsonData? ReadDrain(StreamEvent frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame is ToolCallHeaderUpdated ? null : frame.SourceDrainSnapshot;
    }

    /// <summary>Owned source-facing final view and presence sidecar; the canonical native message remains unchanged.</summary>
    public static JsonData? ReadFinalObservation(StreamTerminalEvent frame, int maximumCharacters = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (ReadEmission(frame) is not { } captured) return null;
        var input = captured.ToString();
        if (input.Length > maximumCharacters + 256L) throw new StreamLimitException("Captured source final input limit exceeded.");
        var member = frame is StreamDone ? "message" : "error";
        // Public frame sidecars can retain permissive JsonElement syntax; admit the entire retained input strictly.
        var root = JsonData.Parse(input).Value; var prefix = "/" + member;
        var paths = root.GetProperty("ownUndefinedPaths").EnumerateArray().Select(item => item.GetString()!)
            .Where(path => path.StartsWith(prefix + "/", StringComparison.Ordinal)).Select(path => path[prefix.Length..]).ToArray();
        var raw = "{\"value\":" + root.GetProperty("value").GetProperty(member).GetRawText() + ",\"ownUndefinedPaths\":" + JsonSerializer.Serialize(paths) + "}";
        if (raw.Length > maximumCharacters) throw new StreamLimitException("Captured source final limit exceeded.");
        return JsonData.Parse(raw);
    }

    internal static JsonData Capture(StreamEvent frame, AssistantMessage snapshot,
        IReadOnlyList<CompletionsToolSourceView> tools, bool responseIdAssigned = false, JsonData? responseId = null,
        IReadOnlyList<string>? addedPropertyOrder = null)
    {
        var partial = SourceMessage(snapshot, responseIdAssigned, responseId, addedPropertyOrder);
        var content = partial["content"]!.AsArray();
        var undefined = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool.Ended) continue;
            var node = content[tool.ContentIndex]!.AsObject();
            node["arguments"] = JsonNode.Parse(tool.DisplayArguments.ToString());
            // Pi's catch removes parsing buffers before its error publication.
            // Retain the real parsed preview, without reconstructing scratch fields on a terminal.
            if (frame is StreamTerminalEvent) continue;
            if (tool.CustomInput is { } custom)
            {
                node.Remove("partialArgs");
                node["customInput"] = JsonNode.Parse(custom.ToString());
                if (tool.UndefinedPartialArguments) undefined.Add($"/partial/content/{tool.ContentIndex}/partialArgs");
            }
            else
            {
                node["partialArgs"] = tool.RawArguments;
                undefined.Add($"/partial/content/{tool.ContentIndex}/customInput");
            }
            if (tool.WireIndex is { } index) node["streamIndex"] = index;
            if (tool.WireIndex is null) undefined.Add($"/partial/content/{tool.ContentIndex}/streamIndex");
        }
        var value = new JsonObject();
        switch (frame)
        {
            case StreamStarted: value["type"] = "start"; break;
            case TextStarted start: Indexed("text_start", start.ContentIndex); break;
            case TextDelta delta: Indexed("text_delta", delta.ContentIndex); value["delta"] = delta.Delta; break;
            case TextEnded end: Indexed("text_end", end.ContentIndex); value["content"] = end.Content; break;
            case ThinkingStarted start: Indexed("thinking_start", start.ContentIndex); break;
            case ThinkingDelta delta: Indexed("thinking_delta", delta.ContentIndex); value["delta"] = delta.Delta; break;
            case ThinkingEnded end: Indexed("thinking_end", end.ContentIndex); value["content"] = end.Content; break;
            case ToolCallStarted start: Indexed("toolcall_start", start.ContentIndex); break;
            case ToolCallProvisionalStarted start: Indexed("toolcall_start", start.ContentIndex); break;
            case ToolCallDelta delta: Indexed("toolcall_delta", delta.ContentIndex); value["delta"] = delta.Delta; break;
            case ToolCallEnded end:
                Indexed("toolcall_end", end.ContentIndex); value["toolCall"] = SourceContent(end.ToolCall); break;
            case StreamTerminalEvent terminal:
                value["type"] = terminal is StreamDone ? "done" : "error";
                value["reason"] = PiWireJson.StopReasonName(terminal.Reason);
                value[terminal is StreamDone ? "message" : "error"] = partial;
                undefined.Clear();
                if (responseIdAssigned && responseId is null) undefined.Add(terminal is StreamDone ? "/message/responseId" : "/error/responseId");
                return Own(value, undefined);
            default: throw new StreamProtocolException("Native operation has no Completions source push.");
        }
        value["partial"] = partial;
        if (responseIdAssigned && responseId is null) undefined.Add("/partial/responseId");
        // The oracle sorts undefined paths; array order is itself compared exactly.
        var sorted = new JsonArray(undefined.Select(node => node!.GetValue<string>()).Order(StringComparer.Ordinal)
            .Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        return Own(value, sorted);

        void Indexed(string type, int index) { value["type"] = type; value["contentIndex"] = index; }
    }

    // The generic native serializer retains a different schema order. Source snapshots
    // must retain Pi's object insertion order, including fields assigned after construction.
    // Values, binary64 cost syntax and the generic ECMAScript serializer remain unchanged.
    private static JsonObject SourceMessage(AssistantMessage snapshot, bool responseIdAssigned, JsonData? responseId,
        IReadOnlyList<string>? addedPropertyOrder)
    {
        var native = JsonNode.Parse(PiWireJson.WriteMessage(snapshot).ToString())!.AsObject();
        native["content"] = new JsonArray(snapshot.Content.Select(block => (JsonNode)SourceContent(block)).ToArray());
        var usage = native["usage"]!.AsObject();
        usage["cost"] = InOrder(usage["cost"]!.AsObject(), ["input", "output", "cacheRead", "cacheWrite", "total"]);
        native["usage"] = InOrder(usage, ["input", "output", "cacheRead", "cacheWrite", "reasoning", "totalTokens", "cost"]);
        if (responseIdAssigned)
        {
            if (responseId is null) native.Remove("responseId");
            else native["responseId"] = JsonNode.Parse(responseId.ToString());
        }
        return InOrder(native, new[] { "role", "content", "api", "provider", "model", "usage", "stopReason", "timestamp" }
            .Concat(addedPropertyOrder ?? []).Concat(["responseId", "responseModel", "rawStopReason", "errorMessage"]));
    }

    private static JsonObject SourceContent(AssistantContent content)
    {
        var native = JsonNode.Parse(PiWireJson.WriteContent(content).ToString())!.AsObject();
        return InOrder(native, content switch
        {
            TextContent => ["type", "text"],
            ThinkingContent => ["type", "thinking", "thinkingSignature"],
            ToolCallContent => ["type", "id", "name", "arguments"],
            _ => throw new StreamProtocolException("Unknown Completions source content.")
        });
    }

    private static JsonObject InOrder(JsonObject source, IEnumerable<string> names)
    {
        var ordered = new JsonObject();
        foreach (var name in names)
            if (source.TryGetPropertyValue(name, out var value))
            { source.Remove(name); ordered.Add(name, value); }
        foreach (var item in source.ToArray())
        { source.Remove(item.Key); ordered.Add(item.Key, item.Value); }
        return ordered;
    }

    /// <summary>
    /// Returns owned drain views captured at the qualified source-await boundaries.
    /// It neither replaces a partial with a final nor infers arbitrary consumer timing.
    /// Error/cancellation drain timing is not qualified by the successful corpus.
    /// </summary>
    public static IReadOnlyList<JsonData> ReadCapturedDrainObservations(IReadOnlyList<StreamEvent> frames,
        int maximumCharacters = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (frames.Count == 0 || frames[^1] is not StreamDone || ReadEmission(frames[^1]) is null || ReadDrain(frames[^1]) is null)
            throw new StreamProtocolException("Captured source drain requires owned successful terminal push and drain snapshots.");
        var result = new List<JsonData>();
        long characters = 0;
        foreach (var frame in frames)
        {
            if (frame is ToolCallHeaderUpdated) continue;
            var emission = ReadEmission(frame) ?? throw new StreamProtocolException("A source push lacks its owned snapshot.");
            var drain = ReadDrain(frame) ?? throw new StreamProtocolException("A source push lacks its owned drain boundary snapshot.");
            Charge(emission.ToString().Length);
            Charge(drain.ToString().Length);
            result.Add(drain);
        }
        return result;

        void Charge(int size)
        {
            if (size > maximumCharacters - characters) throw new StreamLimitException("Captured source drain limit exceeded.");
            characters += size;
        }
    }

    private static JsonData Own(JsonObject value, JsonArray undefined) => JsonData.Parse(
        new JsonObject { ["value"] = value, ["ownUndefinedPaths"] = undefined }.ToJsonString());
}
