// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/mcp/src/model-content.ts and protocol/content.ts.
using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Runtime.Mcp.Resources;

public static partial class McpModelContent
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant)] private static partial Regex Extension();

    public static async Task<ImmutableArray<JsonData>> ConvertAsync(string server, JsonData result,
        McpOutputSaver? saveOutput = null, string? readResourceTool = null, CancellationToken token = default)
    {
        var value = result.Value;
        var blocks = value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToArray() : [];
        var converted = ImmutableArray.CreateBuilder<JsonData>();
        foreach (var block in blocks) converted.Add(await BlockAsync(server, block, readResourceTool, saveOutput, token).ConfigureAwait(false));
        // toLlmContent: without content blocks the structured content is shown as JSON.
        if (blocks.Length == 0 && value.TryGetProperty("structuredContent", out var structured))
            converted.Add(Text(JsonSerializer.Serialize(structured, Indented)));
        return converted.ToImmutable();
    }

    private static async ValueTask<JsonData> BlockAsync(string server, JsonElement block, string? readResourceTool, McpOutputSaver? save, CancellationToken token)
    {
        var type = String(block, "type");
        if (type == "resource_link")
        {
            var details = new List<string>();
            if (String(block, "mimeType") is { Length: > 0 } mime) details.Add(mime);
            if (block.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number) details.Add(FormatSize(size.GetDouble()));
            var read = !string.IsNullOrEmpty(readResourceTool) ? $". Read it with {readResourceTool} (server \"{server}\")" : "";
            var description = String(block, "description") is { Length: > 0 } text ? ": " + text : "";
            return Text($"[Resource {String(block, "uri")} \"{String(block, "title") ?? String(block, "name")}\"{(details.Count > 0 ? $" ({string.Join(", ", details)})" : "")}{description}{read}]");
        }
        if (type == "resource" && block.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object &&
            String(resource, "blob") is { } blob && String(resource, "mimeType")?.StartsWith("image/", StringComparison.Ordinal) != true)
        {
            var uri = String(resource, "uri") ?? ""; var mime = String(resource, "mimeType");
            byte[] data;
            try { data = Convert.FromBase64String(blob); } catch (FormatException) { data = []; }
            if (IsTextMimeType(mime)) return Text(Encoding.UTF8.GetString(data));
            if (save is null) return Text($"[binary resource {uri} ({mime ?? "unknown type"}) omitted]");
            var kind = $"{mime ?? "unknown type"}, {FormatSize(data.Length)}";
            try
            {
                var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;
                var extension = Extension().Match(path) is { Success: true } match ? match.Value : ".bin";
                return Text($"[Binary resource {uri} ({kind}) saved to {await save(data, extension, token).ConfigureAwait(false)}]");
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !token.IsCancellationRequested)
            { return Text($"[Binary resource {uri} ({kind}) could not be saved: {failure.Message}]"); }
        }
        // toLlmContent's blockToLlmContent.
        return type switch
        {
            "text" => Text(String(block, "text") ?? ""),
            "image" => Image(String(block, "data"), String(block, "mimeType")),
            "audio" => Text($"[audio {String(block, "mimeType")} omitted]"),
            "resource" when block.TryGetProperty("resource", out var embedded) && embedded.ValueKind == JsonValueKind.Object => String(embedded, "text") is { } embeddedText
                ? Text(embeddedText)
                : String(embedded, "mimeType")?.StartsWith("image/", StringComparison.Ordinal) == true
                    ? Image(String(embedded, "blob"), String(embedded, "mimeType"))
                    : Text($"[binary resource {String(embedded, "uri")} ({String(embedded, "mimeType") ?? "unknown type"}) omitted]"),
            _ => Text($"[unsupported MCP content {type}]")
        };
    }

    private static string FormatSize(double bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + "B"
        : bytes < 1024 * 1024 ? (bytes / 1024).ToString("F1", CultureInfo.InvariantCulture) + "KB"
        : (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";

    private static bool IsTextMimeType(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return false;
        var type = mime.Split(';', 2)[0].Trim().ToLowerInvariant();
        return type.StartsWith("text/", StringComparison.Ordinal) || type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal) || type.EndsWith("+xml", StringComparison.Ordinal);
    }

    private static JsonData Text(string text) => Owned(new() { ["type"] = "text", ["text"] = JsonUtf16.StringNode(text) });
    private static JsonData Image(string? data, string? mime) => Owned(new()
    {
        ["type"] = "image", ["data"] = data is null ? null : JsonUtf16.StringNode(data),
        ["mimeType"] = mime is null ? null : JsonUtf16.StringNode(mime)
    });
    private static JsonData Owned(JsonObject block) => JsonData.Parse(JsonUtf16.ToJsonString(block, JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? JsonUtf16.GetString(property) : null;
}
