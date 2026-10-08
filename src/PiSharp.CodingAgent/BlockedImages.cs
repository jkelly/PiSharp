// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts (convertToLlmWithBlockImages).
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

/// <summary>Source <c>images.blockImages</c> defense in depth: request-only projection that replaces every image block of user and
/// tool result messages with the text "Image reading is disabled.", without consecutive duplicates. History is never edited.</summary>
public static class BlockedImages
{
    public const string Placeholder = "Image reading is disabled.";

    public static ImmutableArray<TranscriptEntry> Filter(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.IsDefault) return messages;
        ImmutableArray<TranscriptEntry>.Builder? result = null;
        for (var index = 0; index < messages.Length; index++)
        {
            var filtered = Filter(messages[index]);
            if (!ReferenceEquals(filtered, messages[index]) && result is null)
            { result = ImmutableArray.CreateBuilder<TranscriptEntry>(messages.Length); result.AddRange(messages, index); }
            result?.Add(filtered);
        }
        return result?.MoveToImmutable() ?? messages;
    }

    private static TranscriptEntry Filter(TranscriptEntry message)
    {
        if (message.Role is not ("user" or "toolResult") || message.WireBody is null) return message;
        var value = message.WireBody.Value;
        if (!value.TryGetProperty("content", out var content) || content.ValueKind != System.Text.Json.JsonValueKind.Array ||
            !content.EnumerateArray().Any(IsImage)) return message;
        var body = JsonNode.Parse(value.GetRawText())!.AsObject(); var blocks = new JsonArray();
        foreach (var block in content.EnumerateArray())
        {
            var replaced = IsImage(block) ? new JsonObject { ["type"] = "text", ["text"] = Placeholder } : JsonNode.Parse(block.GetRawText());
            // Dedupe consecutive placeholder texts, including an original placeholder text block before an image.
            if (IsPlaceholder(replaced) && blocks.Count > 0 && IsPlaceholder(blocks[^1])) continue;
            blocks.Add(replaced);
        }
        body["content"] = blocks;
        return new(message.Role, JsonData.Parse(body.ToJsonString()));
    }

    private static bool IsImage(System.Text.Json.JsonElement block) => block.ValueKind == System.Text.Json.JsonValueKind.Object &&
        block.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String && type.GetString() == "image";
    private static bool IsPlaceholder(JsonNode? block) => block is JsonObject node && node["type"] is JsonValue type &&
        type.TryGetValue<string>(out var kind) && kind == "text" && node["text"] is JsonValue text && text.TryGetValue<string>(out var value) && value == Placeholder;
}
