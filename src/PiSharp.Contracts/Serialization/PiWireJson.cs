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
                RequireObject(value.GetProperty("arguments")), JsonFields.FromObjectExcept(value, "type", "id", "name", "arguments")),
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
                RequireObject(value.GetProperty("arguments")), Extras("contentIndex", "id", "name", "arguments")),
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
            case TextDelta delta: Indexed("text_delta", delta.ContentIndex); node["delta"] = delta.Delta; break;
            case TextEnded end: Indexed("text_end", end.ContentIndex); node["content"] = end.Content; break;
            case ThinkingStarted start: Indexed("thinking_start", start.ContentIndex); node["content"] = ContentNode(start.Content); break;
            case ThinkingDelta delta: Indexed("thinking_delta", delta.ContentIndex); node["delta"] = delta.Delta; break;
            case ThinkingCheckpoint checkpoint: Indexed("thinking_checkpoint", checkpoint.ContentIndex); node["content"] = checkpoint.Content; break;
            case ThinkingEnded end: Indexed("thinking_end", end.ContentIndex); node["content"] = end.Content; break;
            case ToolCallStarted start: Indexed("toolcall_start", start.ContentIndex); node["toolCall"] = ContentNode(start.ToolCall); break;
            case ToolCallProvisionalStarted start: Indexed("toolcall_provisional_start", start.ContentIndex); node["toolCall"] = ContentNode(start.ToolCall); break;
            case ToolCallHeaderUpdated header: Indexed("toolcall_header_update", header.ContentIndex); node["id"] = header.Id; node["name"] = header.Name; break;
            case ToolCallCheckpoint checkpoint: Indexed("toolcall_checkpoint", checkpoint.ContentIndex); node["json"] = checkpoint.Json; break;
            case ToolCallDelta delta: Indexed("toolcall_delta", delta.ContentIndex); node["delta"] = delta.Delta; break;
            case ToolCallEnded end:
                Indexed("toolcall_end", end.ContentIndex);
                foreach (var item in ContentNode(end.ToolCall))
                    if (item.Key != "type") node[item.Key] = item.Value?.DeepClone();
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
        return new(value.GetProperty("input").GetInt64(), value.GetProperty("output").GetInt64(),
            value.GetProperty("cacheRead").GetInt64(), value.GetProperty("cacheWrite").GetInt64(),
            value.GetProperty("totalTokens").GetInt64(),
            new(cost.GetProperty("input").GetDecimal(), cost.GetProperty("output").GetDecimal(),
                cost.GetProperty("cacheRead").GetDecimal(), cost.GetProperty("cacheWrite").GetDecimal(),
                cost.GetProperty("total").GetDecimal(), JsonFields.FromObjectExcept(cost, "input", "output", "cacheRead", "cacheWrite", "total"),
                JsonData.Parse("{" + string.Join(",", new[] { "input", "output", "cacheRead", "cacheWrite", "total" }
                    .Select(name => JsonSerializer.Serialize(name) + ":" + cost.GetProperty(name).GetRawText())) + "}")),
            JsonFields.FromObjectExcept(value, "input", "output", "cacheRead", "cacheWrite", "totalTokens", "cost"));
    }

    private static JsonObject MessageNode(AssistantMessage message)
    {
        var node = Object(message.ExtraProperties);
        node["role"] = "assistant";
        node["api"] = message.Api; node["provider"] = message.Provider; node["model"] = message.Model;
        node["timestamp"] = message.Timestamp; node["stopReason"] = StopReasonName(message.StopReason);
        node["content"] = new JsonArray(message.Content.Select(content => (JsonNode)ContentNode(content)).ToArray());
        var usage = Object(message.Usage.ExtraProperties);
        usage["input"] = message.Usage.Input; usage["output"] = message.Usage.Output;
        usage["cacheRead"] = message.Usage.CacheRead; usage["cacheWrite"] = message.Usage.CacheWrite;
        usage["totalTokens"] = message.Usage.TotalTokens;
        var cost = Object(message.Usage.Cost.ExtraProperties);
        cost["input"] = message.Usage.Cost.Input; cost["output"] = message.Usage.Cost.Output;
        cost["cacheRead"] = message.Usage.Cost.CacheRead; cost["cacheWrite"] = message.Usage.Cost.CacheWrite;
        cost["total"] = message.Usage.Cost.Total;
        if (message.Usage.Cost.SourceBinary64Cost is { } sourceCost)
        {
            var source = sourceCost.Value;
            var names = new[] { "input", "output", "cacheRead", "cacheWrite", "total" };
            if (source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Count() != names.Length)
                throw new JsonException("Source cost must contain exactly five numeric fields.");
            foreach (var name in names)
            {
                if (!source.TryGetProperty(name, out var number) || number.ValueKind != JsonValueKind.Number ||
                    !number.TryGetDouble(out var value) || !double.IsFinite(value) || value < 0)
                    throw new JsonException("Source cost must contain finite nonnegative numbers.");
                var typed = name switch
                {
                    "input" => message.Usage.Cost.Input, "output" => message.Usage.Cost.Output,
                    "cacheRead" => message.Usage.Cost.CacheRead, "cacheWrite" => message.Usage.Cost.CacheWrite,
                    _ => message.Usage.Cost.Total
                };
                if (!number.TryGetDecimal(out var owned) || owned != typed)
                    throw new JsonException("Source cost differs from its typed decimal value.");
                cost[name] = JsonNode.Parse(number.GetRawText());
            }
        }
        usage["cost"] = cost; node["usage"] = usage;
        // The source assigns the duration to the finished message, after its other fields.
        if (message.DurationMs is { } duration) node["durationMs"] = duration;
        return node;
    }

    private static JsonObject ContentNode(AssistantContent content)
    {
        var node = Object(content.ExtraProperties);
        switch (content)
        {
            case TextContent text: node["type"] = "text"; node["text"] = text.Text; break;
            case ThinkingContent thinking: node["type"] = "thinking"; node["thinking"] = thinking.Thinking; break;
            case ToolCallContent tool:
                node["type"] = "toolCall"; node["id"] = tool.Id; node["name"] = tool.Name;
                node["arguments"] = JsonNode.Parse(tool.Arguments.ToString()); break;
            default: throw new ArgumentException("Unknown assistant content.", nameof(content));
        }
        return node;
    }

    private static JsonObject Object(JsonFields? properties)
    {
        var node = new JsonObject();
        foreach (var item in (properties ?? JsonFields.Empty).Values)
            node[item.Key] = JsonNode.Parse(item.Value.ToString());
        return node;
    }

    private static JsonData Own(JsonNode node) => JsonData.Parse(node.ToJsonString());
    private static JsonData RequireObject(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? JsonData.FromElement(value) : throw new JsonException("Tool arguments must be a JSON object.");
    private static string String(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new JsonException($"{name} cannot be null.");
    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (String(value, name) != expected) throw new JsonException($"Expected {name}={expected}.");
    }
}
