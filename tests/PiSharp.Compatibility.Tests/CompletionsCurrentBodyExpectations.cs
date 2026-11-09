using System.Text.Json;
using System.Text.Json.Nodes;

// Authored current-target derivative: 7a6bd5a97cb64766c9ebbf30e9c0ad7c88a00c47, present at frozen
// 7d073328e44337d3292a7db9b0f0308f679c5e97. Historical captures and hashes remain unchanged.
// The sibling tests also compare the explicit legacy override against the complete original expected body.
internal static class CompletionsCurrentBodyExpectations
{
    internal static JsonElement WithoutEmptyTools(JsonElement legacy)
    {
        var current = JsonNode.Parse(legacy.GetRawText())!.AsObject();
        if (legacy.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0)
            current.Remove("tools");
        using var parsed = JsonDocument.Parse(current.ToJsonString());
        return parsed.RootElement.Clone();
    }
}
