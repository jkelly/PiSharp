using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Known-field lookup on JSON that may also contain arbitrary UTF-16 property names.</summary>
internal static class AnthropicJsonProperties
{
    internal static JsonData StringData(string value) => JsonData.Parse(JsonUtf16.IsWellFormed(value) ? JsonSerializer.Serialize(value) : JsonUtf16.Quote(value));
    internal static bool TryGetUtf16Property(this JsonElement value, string name, out JsonElement property)
    {
        try { return value.TryGetProperty(name, out property); }
        catch (InvalidOperationException) when (value.ValueKind == JsonValueKind.Object)
        {
            // System.Text.Json may decode other escaped names while searching for this field.
            // Keep its last-match rule; strict owned JsonData still rejects decoded duplicates.
            var found = false; property = default;
            foreach (var candidate in value.EnumerateObject())
                if (JsonUtf16.GetName(candidate) == name) { property = candidate.Value; found = true; }
            return found;
        }
    }
    internal static JsonElement GetUtf16Property(this JsonElement value, string name) =>
        value.TryGetUtf16Property(name, out var property) ? property : throw new KeyNotFoundException("The given key was not present in the JSON object.");
}
