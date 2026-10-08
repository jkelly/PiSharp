// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/tools.ts (convertMcpResult,
// toModelContent, blockToContent, limitMcpContent, MCP_OUTPUT_MAX_BYTES), packages/mcp/src/protocol/content.ts (toLlmContent) and
// packages/coding-agent/src/core/tools/truncate.ts (truncateMiddle, formatSize).
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Cli.Mcp;

/// <summary>An MCP <c>CallToolResult</c> as a tool result: the model gets text and images (resource links name
/// <c>read_mcp_resource</c>, binary resources are saved to files, text over 20 KB keeps its start and end around a
/// <c>…N chars truncated…</c> marker with the full text saved to a file); scripts get the whole result without <c>_meta</c> as
/// <c>structuredContent</c>; <c>isError</c> results are error results.</summary>
internal static partial class McpToolResults
{
    /// <summary>Model-facing text of an MCP result beyond this is cut in the middle.</summary>
    internal const int MaximumModelTextBytes = 20 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal delegate ValueTask<string> Saver(ReadOnlyMemory<byte> data, string extension, CancellationToken token);

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant)] private static partial Regex Extension();

    internal static async Task<JsonData> ConvertAsync(string server, string tool, JsonData result, bool readableResources, Saver save, CancellationToken token)
    {
        var value = result.Value;
        var blocks = value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToArray() : [];
        var converted = new List<object>();
        foreach (var block in blocks) converted.AddRange(await BlockAsync(server, block, readableResources, save, token).ConfigureAwait(false));
        // toLlmContent: without content blocks the structured content is shown as JSON.
        if (blocks.Length == 0 && value.TryGetProperty("structuredContent", out var structured))
            converted.Add(Text(JsonSerializer.Serialize(structured, Indented)));
        var isError = value.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        if (isError && TextOf(converted).Length == 0) converted.Add(Text($"MCP tool {server}/{tool} returned an error"));
        var (limited, fullOutputPath) = await LimitAsync(converted, save, token).ConfigureAwait(false);
        var details = new Dictionary<string, object?> { ["server"] = server, ["tool"] = tool };
        if (fullOutputPath is not null) details["fullOutputPath"] = fullOutputPath;
        var scriptResult = value.EnumerateObject().Where(property => property.Name != "_meta").ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
        var output = new Dictionary<string, object?> { ["content"] = limited, ["details"] = details, ["structuredContent"] = scriptResult };
        if (isError) output["isError"] = true;
        return JsonData.Parse(JsonSerializer.Serialize(output, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static async Task<IEnumerable<object>> BlockAsync(string server, JsonElement block, bool readableResources, Saver save, CancellationToken token)
    {
        var type = String(block, "type");
        if (type == "resource_link")
        {
            var details = new List<string>();
            if (String(block, "mimeType") is { Length: > 0 } mime) details.Add(mime);
            if (block.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number) details.Add(FormatSize(size.GetDouble()));
            var read = readableResources ? $". Read it with read_mcp_resource (server \"{server}\")" : "";
            var description = String(block, "description") is { Length: > 0 } text ? ": " + text : "";
            return [Text($"[Resource {String(block, "uri")} \"{String(block, "title") ?? String(block, "name")}\"{(details.Count > 0 ? $" ({string.Join(", ", details)})" : "")}{description}{read}]")];
        }
        if (type == "resource" && block.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object &&
            String(resource, "blob") is { } blob && String(resource, "mimeType")?.StartsWith("image/", StringComparison.Ordinal) != true)
        {
            var uri = String(resource, "uri") ?? ""; var mime = String(resource, "mimeType");
            byte[] data;
            try { data = Convert.FromBase64String(blob); } catch (FormatException) { data = []; }
            if (IsTextMimeType(mime)) return [Text(Encoding.UTF8.GetString(data))];
            var kind = $"{mime ?? "unknown type"}, {FormatSize(data.Length)}";
            try
            {
                var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;
                var extension = Extension().Match(path) is { Success: true } match ? match.Value : ".bin";
                return [Text($"[Binary resource {uri} ({kind}) saved to {await save(data, extension, token).ConfigureAwait(false)}]")];
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { return [Text($"[Binary resource {uri} ({kind}) could not be saved: {failure.Message}]")]; }
        }
        // toLlmContent's blockToLlmContent.
        return type switch
        {
            "text" => [Text(String(block, "text") ?? "")],
            "image" => [new Dictionary<string, object?> { ["type"] = "image", ["data"] = String(block, "data"), ["mimeType"] = String(block, "mimeType") }],
            "audio" => [Text($"[audio {String(block, "mimeType")} omitted]")],
            "resource" when block.TryGetProperty("resource", out var embedded) && embedded.ValueKind == JsonValueKind.Object => String(embedded, "text") is { } embeddedText
                ? [Text(embeddedText)]
                : String(embedded, "mimeType")?.StartsWith("image/", StringComparison.Ordinal) == true
                    ? [new Dictionary<string, object?> { ["type"] = "image", ["data"] = String(embedded, "blob"), ["mimeType"] = String(embedded, "mimeType") }]
                    : [Text($"[binary resource {String(embedded, "uri")} ({String(embedded, "mimeType") ?? "unknown type"}) omitted]")],
            _ => [Text($"[unsupported MCP content {type}]")]
        };
    }

    /// <summary>limitMcpContent: text over the limit becomes one text block in Codex's truncation format, followed by the path of
    /// the file with the full text; images follow it.</summary>
    private static async Task<(List<object> Content, string? FullOutputPath)> LimitAsync(List<object> content, Saver save, CancellationToken token)
    {
        var combined = TextOf(content);
        var bytes = Encoding.UTF8.GetBytes(combined);
        if (bytes.Length <= MaximumModelTextBytes) return (content, null);
        string? path = null; string where;
        try { path = await save(bytes, ".txt", token).ConfigureAwait(false); where = $"[Full output: {path} (read it with offset/limit)]"; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { where = $"[Could not save the full output: {failure.Message}]"; }
        var lines = combined.Length == 0 ? 0 : combined.Split('\n').Length - (combined.EndsWith('\n') ? 1 : 0);
        var text = $"Warning: truncated output (original token count: {(bytes.Length + 3) / 4})\nTotal output lines: {lines}\n\n{TruncateMiddle(bytes, MaximumModelTextBytes)}\n\n{where}";
        return ([Text(text), .. content.Where(block => block is Dictionary<string, object?> image && image["type"] as string == "image")], path);
    }

    /// <summary>truncateMiddle: the start and end at character boundaries around <c>…N chars truncated…</c>.</summary>
    internal static string TruncateMiddle(byte[] bytes, int maximum)
    {
        bool Boundary(int index) => index >= bytes.Length || (bytes[index] & 0xc0) != 0x80;
        var headEnd = maximum / 2;
        while (headEnd > 0 && !Boundary(headEnd)) headEnd--;
        var tailStart = bytes.Length - (maximum - maximum / 2);
        while (tailStart < bytes.Length && !Boundary(tailStart)) tailStart++;
        var removed = Encoding.UTF8.GetString(bytes, headEnd, tailStart - headEnd).EnumerateRunes().Count();
        return $"{Encoding.UTF8.GetString(bytes, 0, headEnd)}…{removed} chars truncated…{Encoding.UTF8.GetString(bytes, tailStart, bytes.Length - tailStart)}";
    }

    /// <summary>formatSize.</summary>
    internal static string FormatSize(double bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + "B"
        : bytes < 1024 * 1024 ? (bytes / 1024).ToString("F1", CultureInfo.InvariantCulture) + "KB"
        : (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";

    private static bool IsTextMimeType(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return false;
        var type = mime.Split(';', 2)[0].Trim().ToLowerInvariant();
        return type.StartsWith("text/", StringComparison.Ordinal) || type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal) || type.EndsWith("+xml", StringComparison.Ordinal);
    }

    private static string TextOf(IEnumerable<object> content) => string.Join("\n", content.OfType<Dictionary<string, object?>>()
        .Where(block => block["type"] as string == "text").Select(block => block["text"] as string));
    private static Dictionary<string, object?> Text(string text) => new() { ["type"] = "text", ["text"] = text };
    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
