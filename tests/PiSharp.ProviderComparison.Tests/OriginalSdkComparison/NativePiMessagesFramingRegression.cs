using System.Text.Json;

// Synchronous fixture regression; no extra HTTP, stream or task originals.
internal static class NativePiMessagesFramingRegression
{
    internal static void Verify()
    {
        using var input = JsonDocument.Parse("""
            [
              {
                "type": "toolcall_end",
                "contentIndex": 0,
                "toolCall": {
                  "type": "toolCall",
                  "id": "final-call",
                  "name": "other",
                  "arguments": { "text": "first\nsecond\r\nthird", "value": 7 }
                }
              },
              {
                "type": "start"
              }
            ]
            """);
        var wire = NativePiMessages.BuildWire(input.RootElement);
        var frames = wire.Split("\n\n", StringSplitOptions.None);
        if (frames.Length != input.RootElement.GetArrayLength() + 1 || frames[^1] != "")
            throw new InvalidOperationException("SSE fixture must terminate every event with one blank line.");
        for (var index = 0; index < frames.Length - 1; index++)
        {
            var frame = frames[index];
            if (!frame.StartsWith("data: ", StringComparison.Ordinal) || frame.Contains('\n') || frame.Contains('\r'))
                throw new InvalidOperationException("SSE fixture JSON must occupy one physical data line.");
            // Independently parse exactly the payload that both pinned decoders read.
            using var decoded = JsonDocument.Parse(frame[6..]);
            if (!JsonElement.DeepEquals(input.RootElement[index], decoded.RootElement))
                throw new InvalidOperationException("SSE fixture serialization changed the event value.");
        }
    }
}
