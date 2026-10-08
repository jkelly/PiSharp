// Pi v0.99.1 d86654abb8862e201933517d6f1fce9f88dd117f: transform-messages.ts and mistral-conversations.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed partial class MistralTextHttpSseTransport
{
    private JsonArray DowngradeImages(JsonArray content, bool tool)
    {
        var placeholder = tool ? "(tool image omitted: model does not support images)" : "(image omitted: model does not support images)";
        var result = new JsonArray(); var previous = false;
        Limit(content.Count, options.MaximumContentBlocks);
        foreach (var part in content)
        {
            var block = part!.AsObject();
            if (block["type"]!.GetValue<string>() == "image")
            {
                // Nonvision upstream only inspects type; it never reads or decodes image data.
                if (!previous) result.Add(new JsonObject { ["type"] = "text", ["text"] = placeholder });
                previous = true;
            }
            else
            {
                _ = ReadTextPart(JsonSerializer.SerializeToElement(block));
                result.Add(block.DeepClone()); previous = block["text"]!.GetValue<string>() == placeholder;
            }
        }
        return result;
    }
    private JsonObject ProjectInputPart(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.Object && part.GetProperty("type").GetString() == "image")
        {
            if (!options.SupportsImages || part.EnumerateObject().Any(field => field.Name is not ("type" or "mimeType" or "data")) ||
                !part.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String ||
                !part.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral image input.");
            // Match the upstream string interpolation; MIME/base64 validation belongs to the provider.
            var uri = "data:" + mime.GetString() + ";base64," + data.GetString();
            Limit(uri.Length, options.MaximumContentCharacters);
            return new() { ["type"] = "image_url", ["imageUrl"] = uri };
        }
        return new() { ["type"] = "text", ["text"] = Sanitize(ReadTextPart(part)) };
    }
    private void ValidateInputPart(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.Object && part.GetProperty("type").GetString() == "image_url")
        {
            if (!options.SupportsImages || part.EnumerateObject().Any(field => field.Name is not ("type" or "imageUrl")) ||
                !part.TryGetProperty("imageUrl", out var uri) || uri.ValueKind != JsonValueKind.String)
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral image payload.");
            Limit(uri.GetString()!.Length, options.MaximumContentCharacters);
        }
        else _ = ReadTextPart(part);
    }
}
