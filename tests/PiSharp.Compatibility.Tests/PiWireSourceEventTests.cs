using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

// Authored from d86654 packages/ai/src/types.ts AssistantMessageEvent, not
// regenerated from native serialization or claimed as a new upstream capture.
internal static class PiWireSourceEventTests
{
    private static readonly AssistantMessage Empty = new("anthropic-messages", "anthropic", "fixture", 123, [], TokenUsage.Zero, StopReason.Pending);
    private static readonly TextContent Text = new("hello");
    private static readonly ThinkingContent Thinking = new("consider", JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"signed\"")));
    private static readonly ToolCallContent Tool = new("call-1", "inspect", JsonData.Parse("{\"x\":7}"),
        JsonFields.Empty.Set("opaqueTool", JsonData.Null));
    private static readonly AssistantMessage Partial = Empty with { Content = [Text, Thinking, Tool] };

    internal static Task ExactSourceShapes()
    {
        (StreamEvent Frame, AssistantMessage? Partial, string Fields)[] rows =
        [
            (new StreamStarted(Empty), Empty, "partial,type"),
            (new TextStarted(0, Text), Partial, "contentIndex,partial,type"),
            (new TextDelta(0, "hello"), Partial, "contentIndex,delta,partial,type"),
            (new TextEnded(0, "hello"), Partial, "content,contentIndex,partial,type"),
            (new ThinkingStarted(1, Thinking), Partial, "contentIndex,partial,type"),
            (new ThinkingDelta(1, "consider"), Partial, "contentIndex,delta,partial,type"),
            (new ThinkingEnded(1, "consider", JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"signed\""))), Partial, "content,contentIndex,partial,type"),
            (new ToolCallStarted(2, Tool), Partial, "contentIndex,partial,type"),
            (new ToolCallDelta(2, "{\"x\":7}"), Partial, "contentIndex,delta,partial,type"),
            (new ToolCallEnded(2, Tool), Partial, "contentIndex,partial,toolCall,type"),
            (new StreamDone(StopReason.ToolUse, Partial with { StopReason = StopReason.ToolUse }), null, "message,reason,type"),
            (new StreamError(StopReason.Aborted, Partial with { StopReason = StopReason.Aborted }), null, "error,reason,type")
        ];
        foreach (var row in rows)
        {
            var wire = PiWireJson.WriteSourceEvent(row.Frame, row.Partial);
            var fields = string.Join(",", wire.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Check(fields == row.Fields, "Exact source field set");
            var read = PiWireJson.ReadSourceEvent(wire.Value, out var snapshot);
            Same(wire, PiWireJson.WriteSourceEvent(read, snapshot));
            Check(read.GetType() == row.Frame.GetType(), "Native event kind retained");
        }
        var end = PiWireJson.WriteSourceEvent(new ToolCallEnded(2, Tool), Partial).Value;
        Check(end.GetProperty("toolCall").GetProperty("type").GetString() == "toolCall", "Nested discriminator");
        Check(end.GetProperty("toolCall").GetProperty("opaqueTool").ValueKind == JsonValueKind.Null, "Nested opaque null retained");
        Check(!end.TryGetProperty("arguments", out _), "No flattened tool arguments");
        return Task.CompletedTask;
    }

    internal static Task ReadOriginalShapesAndOwnership()
    {
        // The source start has no direct content property. Its complete current
        // block is on the emission partial, including a nonempty initial value.
        var partial = PiWireJson.WriteMessage(Partial).ToString();
        var literal = "{\"type\":\"toolcall_start\",\"contentIndex\":2,\"futureEvent\":null,\"partial\":" + partial + "}";
        StreamEvent frame; AssistantMessage? captured;
        using (var document = JsonDocument.Parse(literal)) frame = PiWireJson.ReadSourceEvent(document.RootElement, out captured);
        Check(frame is ToolCallStarted tool && tool.ToolCall.Id == "call-1" && tool.ToolCall.Arguments.ToString() == "{\"x\":7}", "Source partial recovers tool payload");
        Check(frame.ExtraProperties!.Values["futureEvent"].Value.ValueKind == JsonValueKind.Null, "Event null remains event metadata");
        Check(!frame.ExtraProperties.Values.ContainsKey("opaqueTool"), "Tool metadata does not leak to event");
        var saved = PiWireJson.WriteSourceEvent(frame, captured);
        var later = Partial with { Content = [new TextContent("later")] };
        Check(((TextContent)captured!.Content[0]).Text == "hello" && ((TextContent)later.Content[0]).Text == "later", "Owned earlier snapshot unchanged");
        Same(JsonData.Parse(literal), saved);
        var toolEndLiteral = "{\"type\":\"toolcall_end\",\"contentIndex\":2,\"toolCall\":{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"inspect\",\"arguments\":{\"x\":7},\"opaqueTool\":null},\"partial\":" + partial + "}";
        var toolEnd = PiWireJson.ReadSourceEvent(JsonData.Parse(toolEndLiteral).Value, out var toolSnapshot);
        Same(JsonData.Parse(toolEndLiteral), PiWireJson.WriteSourceEvent(toolEnd, toolSnapshot));
        var contradictoryEnd = JsonNode.Parse(toolEndLiteral)!.AsObject();
        contradictoryEnd["toolCall"]!["name"] = "wrong";
        Reject(() => PiWireJson.ReadSourceEvent(JsonData.Parse(contradictoryEnd.ToJsonString()).Value, out _));
        var thinkingLiteral = "{\"type\":\"thinking_end\",\"contentIndex\":1,\"content\":\"consider\",\"partial\":" + partial + "}";
        var thinking = PiWireJson.ReadSourceEvent(JsonData.Parse(thinkingLiteral).Value, out var thinkingSnapshot);
        Check(thinking.ExtraProperties!.Values["thinkingSignature"].Value.GetString() == "signed", "Authoritative signature recovered for reducer");
        Same(JsonData.Parse(thinkingLiteral), PiWireJson.WriteSourceEvent(thinking, thinkingSnapshot));
        return Task.CompletedTask;
    }

    internal static Task RefuseAmbiguousOrInventedSourceFrames()
    {
        Reject(() => PiWireJson.WriteSourceEvent(new TextStarted(0, Text)));
        Reject(() => PiWireJson.WriteSourceEvent(new TextStarted(0, new("wrong")), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new TextDelta(-1, "x"), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new TextDelta(1, "x"), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new ToolCallEnded(2, Tool with { Name = "other" }), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new ThinkingEnded(1, "consider", JsonFields.Empty.Set("thinkingSignature", JsonData.Null)), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new ThinkingCheckpoint(1, "consider"), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new ToolCallProvisionalStarted(2, Tool), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new ToolCallHeaderUpdated(2, "call-1", "inspect"), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new ToolCallCheckpoint(2, "{}"), Partial));
        Reject(() => PiWireJson.WriteSourceEvent(new StreamDone(StopReason.Aborted, Partial with { StopReason = StopReason.Aborted })));
        Reject(() => PiWireJson.WriteSourceEvent(new StreamError(StopReason.Error, Partial)));
        Reject(() => PiWireJson.WriteSourceEvent(new StreamDone(StopReason.Stop, Partial with { StopReason = StopReason.Stop }), Partial));
        var start = JsonNode.Parse(PiWireJson.WriteSourceEvent(new TextStarted(0, Text), Partial).ToString())!.AsObject();
        start["content"] = JsonNode.Parse(PiWireJson.WriteContent(Text).ToString());
        Reject(() => PiWireJson.ReadSourceEvent(JsonData.Parse(start.ToJsonString()).Value, out _));
        start.Remove("content"); start.Remove("partial");
        Reject(() => PiWireJson.ReadSourceEvent(JsonData.Parse(start.ToJsonString()).Value, out _));
        using var duplicate = JsonDocument.Parse("{\"type\":\"start\",\"type\":\"error\"}");
        Reject(() => PiWireJson.ReadSourceEvent(duplicate.RootElement, out _));
        return Task.CompletedTask;
    }

    internal static Task CompactNativeBoundaryRemainsSeparate()
    {
        var native = PiWireJson.WriteEvent(new TextStarted(0, Text));
        Check(native.Value.TryGetProperty("content", out _) && !native.Value.TryGetProperty("partial", out _), "Compact native encoding retained");
        Check(PiWireJson.ReadEvent(native.Value) is TextStarted start && start.Content.Text == "hello", "Compact native roundtrip retained");
        var nativeEnd = PiWireJson.WriteEvent(new ToolCallEnded(2, Tool));
        Check(nativeEnd.Value.TryGetProperty("arguments", out _), "Existing compact end retained");
        Check(PiWireJson.ReadEvent(nativeEnd.Value) is ToolCallEnded, "Existing compact reader retained");
        Reject(() => PiWireJson.ReadSourceEvent(native.Value, out _));
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Same(JsonData expected, JsonData actual)
        => Check(JsonNode.DeepEquals(JsonNode.Parse(expected.ToString()), JsonNode.Parse(actual.ToString())), "Exact source object including retained fields");
    private static void Reject(Action action)
    { try { action(); } catch (JsonException) { return; } throw new InvalidOperationException("Expected source-boundary rejection."); }
}
