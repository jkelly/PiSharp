// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/tools/edit.ts.
// Authored native regression; not an upstream execution capture.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Authored Pi 1.5.2 #8544 expectation. The immutable 0.99.1 capture and prior C05 overlay remain intact.
internal static class Sync152EditExpectations
{
    internal const string SourceSha = "b4afdddf44e059c67ccf5097e5317d913521b470";
    internal static JsonElement ForCase(JsonElement previous)
    {
        if (previous.GetProperty("caseId").GetString() != "bom-mixed-endings") return previous;
        var current = JsonNode.Parse(previous.GetRawText())!.AsObject();
        // The replacement starts on the LF-terminated second line; its inserted line break uses LF.
        const string text = "\ufefffirst\r\nSECOND\nTHIRD\rEND";
        var bytes = Encoding.UTF8.GetBytes(text);
        current["filesystem"]!["after"] = JsonSerializer.SerializeToNode(new { bytes = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), base64 = Convert.ToBase64String(bytes), utf8 = text });
        return JsonSerializer.SerializeToElement(current);
    }
}
