using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Authored current expectations for C05; immutable historical captures and their hashes remain untouched.
internal static class GapEditExpectations
{
    public static JsonElement ForCase(JsonElement historical)
    {
        var id = historical.GetProperty("caseId").GetString();
        if (id is not ("unicode-fuzzy-overlay" or "bom-mixed-endings")) return historical;
        var current = JsonNode.Parse(historical.GetRawText())!.AsObject();
        if (id == "unicode-fuzzy-overlay") Replace(current);
        var text = id == "unicode-fuzzy-overlay"
            ? "untouched: \u201ckeep\u201d   \nuntouched: \"keep\"\nvalue=Bar \U0001f600   \ndone\n"
            : "\ufefffirst\r\nSECOND\r\nTHIRD\rEND";
        var bytes = Encoding.UTF8.GetBytes(text);
        current["filesystem"]!["after"] = JsonSerializer.SerializeToNode(new { bytes = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), base64 = Convert.ToBase64String(bytes), utf8 = text });
        return JsonSerializer.SerializeToElement(current);
    }
    private static void Replace(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[key] = text.Replace("value=Bar \U0001f600\n", "value=Bar \U0001f600   \n", StringComparison.Ordinal);
                else if (obj[key] is { } child) Replace(child);
            }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) Replace(child);
    }
}
