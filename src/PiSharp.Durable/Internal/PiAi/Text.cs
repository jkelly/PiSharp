// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/text.ts (contentText, getSystemMessageText) over pi-ai wire
// JSON. PiSharp.AI's ProviderTranscript has the same helpers over System.Text.Json nodes, internal to PiSharp.AI.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Internal.PiAi;

/// <summary>pi-ai text helpers over wire messages (upstream <c>utils/text.ts</c>).</summary>
internal static class PiAiText
{
    /// <summary>Extracts and joins the text of message content: a string is itself, an array joins its text blocks (upstream
    /// <c>contentText</c>).</summary>
    public static string ContentText(JsonValue? content, string separator = "\n") => content switch
    {
        JsonStringValue text => text.Value,
        JsonArrayValue blocks => string.Join(separator, blocks.OfType<JsonObjectValue>()
            .Where(block => block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" })
            .Select(block => block.TryGetValue("text", out var text) && text is JsonStringValue value ? value.Value : "")),
        _ => "",
    };

    /// <summary>A system message as a complete prompt: its content followed by its sections (upstream <c>getSystemMessageText</c>).</summary>
    public static string GetSystemMessageText(JsonObjectValue message)
    {
        var parts = new List<string> { ContentText(message.TryGetValue("content", out var content) ? content : null) };
        if (message.TryGetValue("sections", out var sections) && sections is JsonObjectValue keyed)
            foreach (var value in keyed.Values)
                if (value is JsonStringValue text) parts.Add(text.Value);
        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }
}
