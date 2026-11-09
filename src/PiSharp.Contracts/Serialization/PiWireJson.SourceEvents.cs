using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Contracts;

public static partial class PiWireJson
{
    /// <summary>Projects the Pi v0.99.1 event union using the caller's actual emission snapshot.
    /// Compact native WriteEvent serialization and native immutable values are unchanged.
    /// No partial snapshot is inferred from a delta or a provider-specific sidecar.</summary>
    public static JsonData WriteSourceEvent(StreamEvent value, AssistantMessage? partial = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var properties = value.ExtraProperties;
        // The native reducer carries the final signature on ThinkingEnded. Pi carries it
        // inside partial.content, not as another event field. Refuse contradictory data.
        if (value is ThinkingEnded thinking && properties is { } signatureProperties && signatureProperties.TryGet("thinkingSignature", out var signature))
        {
            var content = SourceSlot<ThinkingContent>(partial, thinking.ContentIndex);
            if (content.ExtraProperties is not { } capturedProperties || !capturedProperties.TryGet("thinkingSignature", out var captured) ||
                !JsonUtf16.DeepEquals(signature!.Value, captured!.Value))
                throw new JsonException("Thinking signature differs from the actual source partial.");
            properties = signatureProperties.Remove("thinkingSignature");
        }
        var node = Object(properties);
        // Reserved shape fields cannot hide in the native extension bag and be overwritten.
        foreach (var key in node.Select(pair => pair.Key))
            if (SourceReservedFields.Contains(key)) throw new JsonException("Reserved source event field in native properties: " + key);
        switch (value)
        {
            case StreamStarted start:
                if (partial is null || !SameMessage(start.Partial, partial)) throw new JsonException("Start requires its actual partial.");
                node["type"] = "start"; break;
            case TextStarted start:
                SameContent(start.Content, SourceSlot<TextContent>(partial, start.ContentIndex));
                Indexed("text_start", start.ContentIndex); break;
            case TextDelta delta:
                _ = SourceSlot<TextContent>(partial, delta.ContentIndex);
                Indexed("text_delta", delta.ContentIndex); node["delta"] = delta.Delta; break;
            case TextEnded end:
                if (SourceSlot<TextContent>(partial, end.ContentIndex).Text != end.Content) throw new JsonException("Text end differs from partial.");
                Indexed("text_end", end.ContentIndex); node["content"] = end.Content; break;
            case ThinkingStarted start:
                SameContent(start.Content, SourceSlot<ThinkingContent>(partial, start.ContentIndex));
                Indexed("thinking_start", start.ContentIndex); break;
            case ThinkingDelta delta:
                _ = SourceSlot<ThinkingContent>(partial, delta.ContentIndex);
                Indexed("thinking_delta", delta.ContentIndex); node["delta"] = delta.Delta; break;
            case ThinkingEnded end:
                if (SourceSlot<ThinkingContent>(partial, end.ContentIndex).Thinking != end.Content) throw new JsonException("Thinking end differs from partial.");
                Indexed("thinking_end", end.ContentIndex); node["content"] = end.Content; break;
            case ToolCallStarted start:
                SameContent(start.ToolCall, SourceSlot<ToolCallContent>(partial, start.ContentIndex));
                Indexed("toolcall_start", start.ContentIndex); break;
            case ToolCallDelta delta:
                _ = SourceSlot<ToolCallContent>(partial, delta.ContentIndex);
                Indexed("toolcall_delta", delta.ContentIndex); node["delta"] = delta.Delta; break;
            case ToolCallEnded end:
                SameContent(end.ToolCall, SourceSlot<ToolCallContent>(partial, end.ContentIndex));
                Indexed("toolcall_end", end.ContentIndex); node["toolCall"] = ContentNode(end.ToolCall); break;
            case StreamTerminalEvent terminal:
                if (partial is not null || terminal.Message.StopReason != terminal.Reason ||
                    !(terminal is StreamDone && terminal.Reason is StopReason.Stop or StopReason.Length or StopReason.ToolUse or StopReason.Deferred ||
                      terminal is StreamError && terminal.Reason is StopReason.Aborted or StopReason.Error))
                    throw new JsonException("Invalid source terminal reason or partial.");
                node["type"] = terminal is StreamDone ? "done" : "error";
                node["reason"] = StopReasonName(terminal.Reason);
                node[terminal is StreamDone ? "message" : "error"] = MessageNode(terminal.Message); break;
            default: throw new JsonException("Native-only progress has no Pi source event projection.");
        }
        if (value is not StreamTerminalEvent) node["partial"] = MessageNode(partial!);
        return Own(node);

        void Indexed(string kind, int index) { node["type"] = kind; node["contentIndex"] = index; }
    }

    /// <summary>Reads a source event and returns its owned immutable partial separately.
    /// Starts recover their native payload from partial.content; opaque nested tool fields
    /// stay on the tool, while event fields stay on the event.</summary>
    public static StreamEvent ReadSourceEvent(JsonElement value, out AssistantMessage? partial)
    {
        JsonData.Validate(value);
        var kind = String(value, "type");
        string[] fields = kind switch
        {
            "start" => ["type", "partial"],
            "text_start" or "thinking_start" or "toolcall_start" => ["type", "contentIndex", "partial"],
            "text_delta" or "thinking_delta" or "toolcall_delta" => ["type", "contentIndex", "delta", "partial"],
            "text_end" or "thinking_end" => ["type", "contentIndex", "content", "partial"],
            "toolcall_end" => ["type", "contentIndex", "toolCall", "partial"],
            "done" => ["type", "reason", "message"],
            "error" => ["type", "reason", "error"],
            _ => throw new JsonException("Not a Pi v0.99.1 source event.")
        };
        foreach (var field in fields)
            if (!TryWireProperty(value, field, out _)) throw new JsonException("Required source event field absent: " + field);
        foreach (var property in value.EnumerateObject())
        {
            var name = JsonUtf16.GetName(property);
            if (SourceReservedFields.Contains(name) && !fields.Contains(name, StringComparer.Ordinal))
                throw new JsonException("Unexpected source event shape field: " + name);
        }
        var snapshot = kind is "done" or "error" ? null : ReadMessage(GetWireProperty(value, "partial"));
        var extras = JsonFields.FromObjectExcept(value, fields);
        int Index() => GetWireProperty(value, "contentIndex").GetInt32();
        StreamEvent result = kind switch
        {
            "start" => new StreamStarted(snapshot!, extras),
            "text_start" => new TextStarted(Index(), SourceSlot<TextContent>(snapshot, Index()), extras),
            "text_delta" => new TextDelta(Index(), String(value, "delta"), extras),
            "text_end" => new TextEnded(Index(), String(value, "content"), extras),
            "thinking_start" => new ThinkingStarted(Index(), SourceSlot<ThinkingContent>(snapshot, Index()), extras),
            "thinking_delta" => new ThinkingDelta(Index(), String(value, "delta"), extras),
            "thinking_end" => new ThinkingEnded(Index(), String(value, "content"), SourceThinkingEndProperties(snapshot, Index(), extras)),
            "toolcall_start" => new ToolCallStarted(Index(), SourceSlot<ToolCallContent>(snapshot, Index()), extras),
            "toolcall_delta" => new ToolCallDelta(Index(), String(value, "delta"), extras),
            "toolcall_end" => new ToolCallEnded(Index(), ReadContent(GetWireProperty(value, "toolCall")) as ToolCallContent
                ?? throw new JsonException("Source toolcall_end requires a toolCall."), extras),
            "done" => new StreamDone(ReadStopReason(String(value, "reason")), ReadMessage(GetWireProperty(value, "message")), extras),
            "error" => new StreamError(ReadStopReason(String(value, "reason")), ReadMessage(GetWireProperty(value, "error")), extras),
            _ => throw new JsonException("Unknown source event.")
        };
        // Apply the same index/kind/payload/terminal consistency rules in both directions.
        _ = WriteSourceEvent(result, snapshot);
        partial = snapshot;
        return result;
    }

    private static readonly HashSet<string> SourceReservedFields = new(StringComparer.Ordinal)
    { "type", "contentIndex", "partial", "delta", "content", "toolCall", "id", "name", "arguments", "thinkingSignature", "reason", "message", "error" };

    private static T SourceSlot<T>(AssistantMessage? partial, int index) where T : AssistantContent
        => partial is not null && !partial.Content.IsDefault && index >= 0 && index < partial.Content.Length && partial.Content[index] is T content
            ? content : throw new JsonException("Source event requires an actual partial slot of the matching kind.");

    private static JsonFields SourceThinkingEndProperties(AssistantMessage? partial, int index, JsonFields extras)
    {
        var content = SourceSlot<ThinkingContent>(partial, index);
        return content.ExtraProperties is { } properties && properties.TryGet("thinkingSignature", out var signature)
            ? extras.Set("thinkingSignature", signature!) : extras;
    }

    private static void SameContent(AssistantContent left, AssistantContent right)
    { if (!SameContentValue(left, right)) throw new JsonException("Event payload differs from the actual source partial."); }

    private static bool SameContentValue(AssistantContent left, AssistantContent right)
    {
        // Compare the typed identities first: their existing node writer may normalize a malformed
        // string, which must not make distinct tool identities compare equal at this boundary.
        if (left is TextContent firstText && right is TextContent secondText && firstText.Text != secondText.Text) return false;
        if (left is ThinkingContent firstThinking && right is ThinkingContent secondThinking && firstThinking.Thinking != secondThinking.Thinking) return false;
        if (left is ToolCallContent first && right is ToolCallContent second && (first.Id != second.Id || first.Name != second.Name)) return false;
        return JsonUtf16.DeepEquals(Own(ContentNode(left)).Value, Own(ContentNode(right)).Value);
    }

    private static bool SameMessage(AssistantMessage left, AssistantMessage right)
        => left.Api == right.Api && left.Provider == right.Provider && left.Model == right.Model &&
            left.Content.Length == right.Content.Length && left.Content.Zip(right.Content).All(pair => SameContentValue(pair.First, pair.Second)) &&
            JsonUtf16.DeepEquals(Own(MessageNode(left)).Value, Own(MessageNode(right)).Value);
}
