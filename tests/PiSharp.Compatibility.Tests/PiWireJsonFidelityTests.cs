using System.Text.Json;
using PiSharp.Contracts;

// Public wire writers admit opaque UTF-16 metadata names and values. Their matching
// readers must preserve them without weakening known-field validation.
internal static class PiWireJsonFidelityTests
{
    private static JsonFields Metadata => JsonFields.Empty
        .Set("\uD800", JsonData.Parse("\"\\udfff\""))
        .Set("\uDC00", JsonData.Parse("{\"\\ud801\":[\"\\ud802\",null,7]}"))
        .Set("\uFFFD", JsonData.Parse("\"distinct replacement character\""))
        .Set("opaque", JsonData.Parse("\"\\ud800\""));

    internal static Task PublicWriterReaderRoundtrips()
    {
        foreach (var beforeTotal in new[] { false, true })
        {
            var extras = Metadata;
            AssistantContent[] contents =
            [
                new TextContent("text", extras),
                new ThinkingContent("thinking", extras.Set("thinkingSignature", JsonData.Parse("\"\\ud800\""))),
                new ToolCallContent("call-1", "inspect", JsonData.Parse("{\"\\ud800\":\"\\udfff\"}"), extras),
            ];
            var usage = TokenUsage.Zero with
            {
                ExtraProperties = extras,
                ExtrasBeforeTotal = beforeTotal,
                Cost = TokenUsage.Zero.Cost with { ExtraProperties = extras },
            };
            var partial = new AssistantMessage("anthropic-messages", "anthropic", "fixture", 123,
                [.. contents], usage, StopReason.Pending, extras) { DurationMs = 4 };
            foreach (var content in contents.Concat<AssistantContent>(
                [new TextContent("text\uD800", extras), new ThinkingContent("thinking\uDC00", extras)]))
            {
                var wire = PiWireJson.WriteContent(content);
                Same(wire, PiWireJson.WriteContent(PiWireJson.ReadContent(wire.Value)), "content");
            }
            var messageWire = PiWireJson.WriteMessage(partial);
            var copy = PiWireJson.ReadMessage(messageWire.Value);
            Same(messageWire, PiWireJson.WriteMessage(copy), "message/usage/cost");
            Check(copy.Usage.ExtrasBeforeTotal == beforeTotal, "usage insertion order");
            AssertMetadata(copy.ExtraProperties!);
            AssertMetadata(copy.Usage.ExtraProperties!);
            AssertMetadata(copy.Usage.Cost.ExtraProperties!);
            // Exercise the standalone usage reader as well as its message composition.
            var readUsage = PiWireJson.ReadUsageObject(Field(messageWire.Value, "usage"));
            Same(messageWire, PiWireJson.WriteMessage(partial with { Usage = readUsage }), "standalone usage");

            (StreamEvent Event, AssistantMessage? Partial)[] sourceEvents =
            [
                (new StreamStarted(partial, extras), partial),
                (new TextStarted(0, (TextContent)contents[0], extras), partial),
                (new TextDelta(0, "delta", extras), partial),
                (new TextEnded(0, ((TextContent)contents[0]).Text, extras), partial),
                (new ThinkingStarted(1, (ThinkingContent)contents[1], extras), partial),
                (new ThinkingDelta(1, "delta", extras), partial),
                (new ThinkingEnded(1, ((ThinkingContent)contents[1]).Thinking,
                    extras.Set("thinkingSignature", JsonData.Parse("\"\\ud800\""))), partial),
                (new ToolCallStarted(2, (ToolCallContent)contents[2], extras), partial),
                (new ToolCallDelta(2, "{}", extras), partial),
                (new ToolCallEnded(2, (ToolCallContent)contents[2], extras), partial),
                (new StreamDone(StopReason.Stop, partial with { StopReason = StopReason.Stop }, extras), null),
                (new StreamError(StopReason.Error, partial with { StopReason = StopReason.Error }, extras), null),
            ];
            foreach (var (frame, snapshot) in sourceEvents)
            {
                var wire = PiWireJson.WriteSourceEvent(frame, snapshot);
                var read = PiWireJson.ReadSourceEvent(wire.Value, out var captured);
                Same(wire, PiWireJson.WriteSourceEvent(read, captured), "source " + frame.GetType().Name);
                AssertMetadata(read.ExtraProperties!);
            }
            // Compact events have a different shape but share the same metadata ownership contract.
            foreach (var frame in sourceEvents.Select(row => row.Event).Concat<StreamEvent>(
                [new ContentBlockFinalized(0, contents[0], extras), new ToolCallCheckpoint(2, "{}", extras),
                 new ToolCallProvisionalStarted(2, (ToolCallContent)contents[2], extras), new ToolCallHeaderUpdated(2, "call-1", "inspect", extras),
                 new ThinkingCheckpoint(1, "checkpoint", extras)]))
            {
                var wire = PiWireJson.WriteEvent(frame);
                Same(wire, PiWireJson.WriteEvent(PiWireJson.ReadEvent(wire.Value)), "compact " + frame.GetType().Name);
            }
        }
        return Task.CompletedTask;
    }

    internal static Task StrictInvalidControls()
    {
        var partial = new AssistantMessage("a", "p", "m", 0, [], TokenUsage.Zero, StopReason.Pending);
        var message = PiWireJson.WriteMessage(partial).ToString();
        var suffix = ",\"\\ud800\":\"\\udfff\"";
        Expect<JsonException>(() => PiWireJson.ReadSourceEvent(JsonData.Parse("{\"type\":\"start\"" + suffix + "}").Value, out _));
        Expect<JsonException>(() => PiWireJson.ReadSourceEvent(JsonData.Parse("{\"type\":\"start\",\"partial\":" + message + ",\"content\":1" + suffix + "}").Value, out _));
        Expect<InvalidOperationException>(() => PiWireJson.ReadSourceEvent(JsonData.Parse("{\"type\":1" + suffix + "}").Value, out _));
        Expect<InvalidOperationException>(() => PiWireJson.ReadContent(JsonData.Parse("{\"type\":\"text\",\"text\":1" + suffix + "}").Value));
        Expect<InvalidOperationException>(() => PiWireJson.ReadContent(JsonData.Null.Value));
        Expect<KeyNotFoundException>(() => PiWireJson.ReadContent(JsonData.Parse("{\"type\":\"text\"" + suffix + "}").Value));
        var metadataMessage = message[..^1] + suffix + "}";
        Expect<KeyNotFoundException>(() => PiWireJson.ReadMessage(JsonData.Parse(metadataMessage.Replace("\"model\":\"m\",", "", StringComparison.Ordinal)).Value));
        var usage = Field(PiWireJson.WriteMessage(partial).Value, "usage").GetRawText();
        Expect<InvalidOperationException>(() => PiWireJson.ReadUsageObject(JsonData.Parse(usage.Replace("\"cost\":{", "\"cost\":null,\"unused\":{", StringComparison.Ordinal)).Value));
        Expect<KeyNotFoundException>(() => PiWireJson.ReadUsageObject(JsonData.Parse(usage.Replace("\"totalTokens\":0,", "", StringComparison.Ordinal)[..^1] + suffix + "}").Value));
        Expect<JsonException>(() => PiWireJson.ReadContent(JsonData.Parse("{\"type\":\"unknown\"" + suffix + "}").Value));
        Expect<JsonException>(() => PiWireJson.ReadSourceEvent(JsonData.Parse("{\"type\":\"text_delta\",\"contentIndex\":-1,\"delta\":\"x\",\"partial\":" + message + suffix + "}").Value, out _));

        // Bypass JsonData.Parse so these public readers must enforce decoded duplicate names themselves.
        foreach (var json in new[]
        {
            "{\"type\":\"text\",\"t\\u0079pe\":\"thinking\",\"text\":\"x\"}",
            "{\"type\":\"text\",\"text\":\"x\",\"\\ud800\":1,\"\\uD800\":2}",
        })
        {
            using var document = JsonDocument.Parse(json);
            Expect<JsonException>(() => PiWireJson.ReadContent(document.RootElement));
        }
        using (var duplicate = JsonDocument.Parse("{\"type\":\"start\",\"type\":\"start\",\"partial\":" + message + suffix + "}"))
            Expect<JsonException>(() => PiWireJson.ReadSourceEvent(duplicate.RootElement, out _));
        using (var deep = JsonDocument.Parse("{\"type\":\"text\",\"text\":\"x\",\"metadata\":" +
            new string('[', JsonData.MaximumDepth) + "0" + new string(']', JsonData.MaximumDepth) + "}",
            new JsonDocumentOptions { MaxDepth = JsonData.MaximumDepth + 2 }))
            Expect<JsonException>(() => PiWireJson.ReadContent(deep.RootElement));
        return Task.CompletedTask;
    }

    private static JsonElement Field(JsonElement value, string name) => value.EnumerateObject().Single(p => JsonUtf16.GetName(p) == name).Value;
    private static void AssertMetadata(JsonFields fields)
    {
        Check(fields.Values.ContainsKey("\uD800") && fields.Values.ContainsKey("\uDC00") && fields.Values.ContainsKey("\uFFFD"), "distinct UTF-16 names");
        Check(JsonUtf16.GetString(fields.Values["\uD800"].Value) == "\uDFFF", "opaque UTF-16 value");
    }
    private static void Same(JsonData expected, JsonData actual, string label)
    {
        Check(JsonUtf16.DeepEquals(expected.Value, actual.Value), label + " JSON fidelity");
        Check(expected.ToString() == actual.ToString(), label + " writer bytes/order");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
