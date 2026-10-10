// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Contracts;

/// <summary>Explicit wire adapter; default CLR record serialization is not a Pi protocol.</summary>
public static partial class PiWireJson
{
    public static AssistantMessage ReadMessage(JsonElement value)
    {
        JsonData.Validate(value);
        RequireString(value, "role", "assistant");
        // Only a whole nonnegative duration is typed; any other legacy value stays an opaque extra field.
        long? duration = value.TryGetProperty("durationMs", out var measured) && measured.ValueKind == JsonValueKind.Number &&
            measured.TryGetInt64(out var milliseconds) && milliseconds >= 0 ? milliseconds : null;
        string[] known = ["role", "api", "provider", "model", "timestamp", "content", "usage", "stopReason"];
        return new(
            String(value, "api"), String(value, "provider"), String(value, "model"),
            value.GetProperty("timestamp").GetInt64(),
            value.GetProperty("content").EnumerateArray().Select(ReadContent).ToImmutableArray(),
            ReadUsage(value.GetProperty("usage")), ReadStopReason(String(value, "stopReason")),
            JsonFields.FromObjectExcept(value, duration is null ? known : [.. known, "durationMs"]))
        { DurationMs = duration };
    }

    public static AssistantContent ReadContent(JsonElement value)
    {
        JsonData.Validate(value);
        return String(value, "type") switch
        {
            "text" => new TextContent(String(value, "text"), JsonFields.FromObjectExcept(value, "type", "text")),
            "thinking" => new ThinkingContent(String(value, "thinking"), JsonFields.FromObjectExcept(value, "type", "thinking")),
            "toolCall" => new ToolCallContent(String(value, "id"), String(value, "name"),
                JsonData.FromElement(value.GetProperty("arguments")), JsonFields.FromObjectExcept(value, "type", "id", "name", "arguments")),
            var kind => throw new JsonException($"Unsupported assistant content type: {kind}")
        };
    }

    public static StreamEvent ReadEvent(JsonElement value)
    {
        JsonData.Validate(value);
        var kind = String(value, "type");
        JsonFields Extras(params string[] known) => JsonFields.FromObjectExcept(value, ["type", .. known]);
        int Index() => value.GetProperty("contentIndex").GetInt32();
        return kind switch
        {
            "start" => new StreamStarted(ReadMessage(value.GetProperty("partial")), Extras("partial")),
            "text_start" => new TextStarted(Index(), ReadContent(value.GetProperty("content")) as TextContent
                ?? throw new JsonException("text_start must contain text."), Extras("contentIndex", "content")),
            "text_delta" => new TextDelta(Index(), String(value, "delta"), Extras("contentIndex", "delta")),
            "text_end" => new TextEnded(Index(), String(value, "content"), Extras("contentIndex", "content")),
            "thinking_start" => new ThinkingStarted(Index(), ReadContent(value.GetProperty("content")) as ThinkingContent
                ?? throw new JsonException("thinking_start must contain thinking."), Extras("contentIndex", "content")),
            "thinking_delta" => new ThinkingDelta(Index(), String(value, "delta"), Extras("contentIndex", "delta")),
            "thinking_checkpoint" => new ThinkingCheckpoint(Index(), String(value, "content"), Extras("contentIndex", "content")),
            "thinking_end" => new ThinkingEnded(Index(), String(value, "content"), Extras("contentIndex", "content")),
            "toolcall_start" => new ToolCallStarted(Index(), ReadContent(value.GetProperty("toolCall")) as ToolCallContent
                ?? throw new JsonException("toolcall_start must contain a tool call."), Extras("contentIndex", "toolCall")),
            "toolcall_provisional_start" => new ToolCallProvisionalStarted(Index(), ReadContent(value.GetProperty("toolCall")) as ToolCallContent
                ?? throw new JsonException("Provisional start must contain a tool call."), Extras("contentIndex", "toolCall")),
            "toolcall_header_update" => new ToolCallHeaderUpdated(Index(), String(value, "id"), String(value, "name"), Extras("contentIndex", "id", "name")),
            "toolcall_checkpoint" => new ToolCallCheckpoint(Index(), String(value, "json"), Extras("contentIndex", "json")),
            "toolcall_delta" => new ToolCallDelta(Index(), String(value, "delta"), Extras("contentIndex", "delta")),
            "toolcall_end" => new ToolCallEnded(Index(), new ToolCallContent(String(value, "id"), String(value, "name"),
                JsonData.FromElement(value.GetProperty("arguments")), Extras("contentIndex", "id", "name", "arguments")),
                Extras("contentIndex", "id", "name", "arguments")),
            "content_finalized" => new ContentBlockFinalized(Index(), ReadContent(value.GetProperty("content")), Extras("contentIndex", "content")),
            "done" => new StreamDone(ReadStopReason(String(value, "reason")), ReadMessage(value.GetProperty("message")), Extras("reason", "message")),
            "error" => new StreamError(ReadStopReason(String(value, "reason")), ReadMessage(value.GetProperty("error")), Extras("reason", "error")),
            _ => throw new JsonException($"Unknown stream event: {kind}")
        };
    }

    public static JsonData WriteMessage(AssistantMessage message) => Own(MessageNode(message));
    public static JsonData WriteContent(AssistantContent content) => Own(ContentNode(content));
    public static JsonData WriteEvent(StreamEvent value)
    {
        var node = Object(value.ExtraProperties);
        switch (value)
        {
            case StreamStarted start: node["type"] = "start"; node["partial"] = MessageNode(start.Partial); break;
            case TextStarted start: Indexed("text_start", start.ContentIndex); node["content"] = ContentNode(start.Content); break;
            case TextDelta delta: Indexed("text_delta", delta.ContentIndex); node["delta"] = JsonUtf16.StringNode(delta.Delta); break;
            case TextEnded end: Indexed("text_end", end.ContentIndex); node["content"] = JsonUtf16.StringNode(end.Content); break;
            case ThinkingStarted start: Indexed("thinking_start", start.ContentIndex); node["content"] = ContentNode(start.Content); break;
            case ThinkingDelta delta: Indexed("thinking_delta", delta.ContentIndex); node["delta"] = JsonUtf16.StringNode(delta.Delta); break;
            case ThinkingCheckpoint checkpoint: Indexed("thinking_checkpoint", checkpoint.ContentIndex); node["content"] = JsonUtf16.StringNode(checkpoint.Content); break;
            case ThinkingEnded end: Indexed("thinking_end", end.ContentIndex); node["content"] = JsonUtf16.StringNode(end.Content); break;
            case ToolCallStarted start: Indexed("toolcall_start", start.ContentIndex); node["toolCall"] = ContentNode(start.ToolCall); break;
            case ToolCallProvisionalStarted start: Indexed("toolcall_provisional_start", start.ContentIndex); node["toolCall"] = ContentNode(start.ToolCall); break;
            case ToolCallHeaderUpdated header: Indexed("toolcall_header_update", header.ContentIndex); node["id"] = header.Id; node["name"] = header.Name; break;
            case ToolCallCheckpoint checkpoint: Indexed("toolcall_checkpoint", checkpoint.ContentIndex); node["json"] = JsonUtf16.StringNode(checkpoint.Json); break;
            case ToolCallDelta delta: Indexed("toolcall_delta", delta.ContentIndex); node["delta"] = JsonUtf16.StringNode(delta.Delta); break;
            case ToolCallEnded end:
                Indexed("toolcall_end", end.ContentIndex);
                // Moved, not cloned: a node holding a lone surrogate is written only (see JsonUtf16).
                var content = ContentNode(end.ToolCall);
                foreach (var key in content.Select(item => item.Key).ToArray())
                {
                    var item = content[key]; content.Remove(key);
                    if (key != "type") node[key] = item;
                }
                break;
            case ContentBlockFinalized finalized: Indexed("content_finalized", finalized.ContentIndex); node["content"] = ContentNode(finalized.Content); break;
            case StreamTerminalEvent terminal:
                node["type"] = terminal is StreamDone ? "done" : "error";
                node["reason"] = StopReasonName(terminal.Reason);
                node[terminal is StreamDone ? "message" : "error"] = MessageNode(terminal.Message);
                break;
            default: throw new ArgumentException("Unknown native stream event.", nameof(value));
        }
        return Own(node);

        void Indexed(string kind, int index) { node["type"] = kind; node["contentIndex"] = index; }
    }

    public static StopReason ReadStopReason(string reason) => reason switch
    {
        "pending" => StopReason.Pending, "stop" => StopReason.Stop, "length" => StopReason.Length,
        "toolUse" => StopReason.ToolUse, "error" => StopReason.Error, "aborted" => StopReason.Aborted,
        "deferred" => StopReason.Deferred, _ => throw new JsonException($"Unknown stop reason: {reason}")
    };

    public static string StopReasonName(StopReason reason) => reason switch
    {
        StopReason.Pending => "pending", StopReason.Stop => "stop", StopReason.Length => "length",
        StopReason.ToolUse => "toolUse", StopReason.Error => "error", StopReason.Aborted => "aborted",
        StopReason.Deferred => "deferred", _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    /// <summary>Reads a Pi Usage object (<c>{ input, output, cacheRead, cacheWrite, totalTokens, cost }</c>).</summary>
    public static TokenUsage ReadUsageObject(JsonElement value) => ReadUsage(value);
    private static TokenUsage ReadUsage(JsonElement value)
    {
        var cost = value.GetProperty("cost");
        return new(JsonNumber.Read(value.GetProperty("input")), JsonNumber.Read(value.GetProperty("output")),
            JsonNumber.Read(value.GetProperty("cacheRead")), JsonNumber.Read(value.GetProperty("cacheWrite")),
            JsonNumber.Read(value.GetProperty("totalTokens")),
            new(cost.GetProperty("input").GetDecimal(), cost.GetProperty("output").GetDecimal(),
                cost.GetProperty("cacheRead").GetDecimal(), cost.GetProperty("cacheWrite").GetDecimal(),
                cost.GetProperty("total").GetDecimal(), JsonFields.FromObjectExcept(cost, "input", "output", "cacheRead", "cacheWrite", "total"),
                JsonData.Parse("{" + string.Join(",", new[] { "input", "output", "cacheRead", "cacheWrite", "total" }
                    .Select(name => JsonSerializer.Serialize(name) + ":" + cost.GetProperty(name).GetRawText())) + "}")),
            JsonFields.FromObjectExcept(value, "input", "output", "cacheRead", "cacheWrite", "totalTokens", "cost"))
        { ExtrasBeforeTotal = ExtraBeforeTotal(value) };
    }
    // Whether a field outside the standard ones precedes totalTokens (the literal key order; see TokenUsage.ExtrasBeforeTotal).
    private static bool ExtraBeforeTotal(JsonElement usage)
    {
        foreach (var property in usage.EnumerateObject())
        {
            if (property.Name == "totalTokens") return false;
            if (property.Name is not ("input" or "output" or "cacheRead" or "cacheWrite" or "cost")) return true;
        }
        return false;
    }

    // Pi builds the message as one literal (each provider's `const output: AssistantMessage = { role, content, api, provider, model,
    // [providerThinkingLevel,] usage, stopReason, timestamp }`), assigns optional fields as the stream reports them (responseId,
    // responseModel, rawStopReason, errorMessage, ...), then event-stream.ts end() sets durationMs and agent-loop.ts Object.assign
    // adds thinkingLevel. JSON.stringify keeps that insertion order.
    private static readonly string[] MessageFields = ["role", "content", "api", "provider", "model", "usage", "stopReason", "timestamp", "durationMs"];
    private static JsonObject MessageNode(AssistantMessage message)
    {
        var extras = message.ExtraProperties ?? JsonFields.Empty;
        var node = new JsonObject { ["role"] = "assistant" };
        node["content"] = new JsonArray(message.Content.Select(content => (JsonNode)ContentNode(content)).ToArray());
        node["api"] = message.Api; node["provider"] = message.Provider; node["model"] = message.Model;
        if (extras.TryGet("providerThinkingLevel", out var providerLevel)) node["providerThinkingLevel"] = JsonUtf16.Node(providerLevel!);
        node["usage"] = UsageNode(message.Usage);
        node["stopReason"] = StopReasonName(message.StopReason); node["timestamp"] = message.Timestamp;
        foreach (var item in extras.Ordered)
            if (item.Key is not ("providerThinkingLevel" or "thinkingLevel") && !MessageFields.Contains(item.Key))
                node[item.Key] = JsonUtf16.Node(item.Value);
        if (message.DurationMs is { } duration) node["durationMs"] = duration;
        if (extras.TryGet("thinkingLevel", out var level)) node["thinkingLevel"] = JsonUtf16.Node(level!);
        return node;
    }

    private static readonly string[] UsageFields = ["input", "output", "cacheRead", "cacheWrite", "totalTokens", "cost"];
    private static JsonObject UsageNode(TokenUsage value)
    {
        // Counts are JavaScript numbers: a whole count is written as an integer, a fraction as Number::toString writes it.
        var usage = new JsonObject { ["input"] = JsonNumber.Node(value.Input), ["output"] = JsonNumber.Node(value.Output), ["cacheRead"] = JsonNumber.Node(value.CacheRead), ["cacheWrite"] = JsonNumber.Node(value.CacheWrite) };
        var extras = (value.ExtraProperties ?? JsonFields.Empty).Ordered.Where(item => !UsageFields.Contains(item.Key)).ToArray();
        if (value.ExtrasBeforeTotal) foreach (var item in extras) usage[item.Key] = JsonUtf16.Node(item.Value);
        usage["totalTokens"] = JsonNumber.Node(value.TotalTokens);
        var cost = new JsonObject
        {
            ["input"] = value.Cost.Input, ["output"] = value.Cost.Output, ["cacheRead"] = value.Cost.CacheRead,
            ["cacheWrite"] = value.Cost.CacheWrite, ["total"] = value.Cost.Total
        };
        if (value.Cost.SourceBinary64Cost is { } sourceCost)
        {
            var source = sourceCost.Value;
            var names = new[] { "input", "output", "cacheRead", "cacheWrite", "total" };
            if (source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Count() != names.Length)
                throw new JsonException("Source cost must contain exactly five numeric fields.");
            foreach (var name in names)
            {
                if (!source.TryGetProperty(name, out var number) || number.ValueKind != JsonValueKind.Number ||
                    !number.TryGetDouble(out var parsed) || !double.IsFinite(parsed) || parsed < 0)
                    throw new JsonException("Source cost must contain finite nonnegative numbers.");
                var typed = name switch
                {
                    "input" => value.Cost.Input, "output" => value.Cost.Output,
                    "cacheRead" => value.Cost.CacheRead, "cacheWrite" => value.Cost.CacheWrite,
                    _ => value.Cost.Total
                };
                if (!number.TryGetDecimal(out var owned) || owned != typed)
                    throw new JsonException("Source cost differs from its typed decimal value.");
                cost[name] = JsonNode.Parse(number.GetRawText());
            }
        }
        foreach (var item in (value.Cost.ExtraProperties ?? JsonFields.Empty).Ordered)
            if (!cost.ContainsKey(item.Key)) cost[item.Key] = JsonUtf16.Node(item.Value);
        usage["cost"] = cost;
        if (!value.ExtrasBeforeTotal) foreach (var item in extras) usage[item.Key] = JsonUtf16.Node(item.Value);
        return usage;
    }

    // Pi content blocks are literals that start with their own fields ({ type: "thinking", thinking, thinkingSignature }); a
    // provider's extra fields follow in the order it assigned them.
    private static JsonObject ContentNode(AssistantContent content)
    {
        var node = new JsonObject();
        string[] own;
        switch (content)
        {
            case TextContent text: node["type"] = "text"; node["text"] = JsonUtf16.StringNode(text.Text); own = ["type", "text"]; break;
            case ThinkingContent thinking: node["type"] = "thinking"; node["thinking"] = JsonUtf16.StringNode(thinking.Thinking); own = ["type", "thinking"]; break;
            case ToolCallContent tool:
                node["type"] = "toolCall"; node["id"] = tool.Id; node["name"] = tool.Name;
                node["arguments"] = JsonUtf16.Node(tool.Arguments); own = ["type", "id", "name", "arguments"]; break;
            default: throw new ArgumentException("Unknown assistant content.", nameof(content));
        }
        foreach (var item in (content.ExtraProperties ?? JsonFields.Empty).Ordered)
            if (!own.Contains(item.Key)) node[item.Key] = JsonUtf16.Node(item.Value);
        return node;
    }

    private static JsonObject Object(JsonFields? properties)
    {
        var node = new JsonObject();
        foreach (var item in (properties ?? JsonFields.Empty).Ordered)
            node[item.Key] = JsonUtf16.Node(item.Value);
        return node;
    }

    private static JsonData Own(JsonNode node) => JsonData.Parse(node.ToJsonString());
    private static string String(JsonElement value, string name) => (value.GetProperty(name) is { ValueKind: JsonValueKind.String } text ? JsonUtf16.GetString(text) : value.GetProperty(name).GetString())
        ?? throw new JsonException($"{name} cannot be null.");
    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (String(value, name) != expected) throw new JsonException($"Expected {name}={expected}.");
    }
}
