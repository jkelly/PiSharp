// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/output-limit.ts.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Runtime.Mcp.Resources;

public delegate ValueTask<string> McpOutputSaver(ReadOnlyMemory<byte> data, string extension, CancellationToken token);
public sealed record LimitedMcpContent(ImmutableArray<JsonData> Content, string? FullOutputPath = null);

/// <summary>Bounds model-facing MCP text. Saving the full output is explicitly optional.</summary>
public static class McpContentLimiter
{
    public const int MaximumBytes = 20 * 1024;
    public static async Task<LimitedMcpContent> LimitAsync(ImmutableArray<JsonData> content,
        McpOutputSaver? saveOutput = null, CancellationToken token = default)
    {
        string? Kind(JsonData block) => block.Value.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        var combined = string.Join('\n', content.Where(block => Kind(block) == "text")
            .Select(block => JsonUtf16.GetString(block.Value.GetProperty("text"))));
        var bytes = Encoding.UTF8.GetBytes(combined);
        if (bytes.Length <= MaximumBytes) return new(content);
        var head = MaximumBytes / 2;
        while (head > 0 && (bytes[head] & 0xc0) == 0x80) head--;
        var tail = bytes.Length - MaximumBytes / 2;
        while (tail < bytes.Length && (bytes[tail] & 0xc0) == 0x80) tail++;
        var removed = Encoding.UTF8.GetString(bytes, head, tail - head).EnumerateRunes().Count();
        var lines = combined.Split('\n').Length - (combined.EndsWith('\n') ? 1 : 0);
        var text = $"Warning: truncated output (original token count: {(bytes.Length + 3) / 4})\nTotal output lines: {lines}\n\n" +
            Encoding.UTF8.GetString(bytes, 0, head) + $"…{removed} chars truncated…" + Encoding.UTF8.GetString(bytes, tail, bytes.Length - tail);
        string? path = null;
        if (saveOutput is not null)
        {
            try { path = await saveOutput(bytes, ".txt", token).ConfigureAwait(false); text += $"\n\n[Full output: {path} (read it with offset/limit)]"; }
            catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested))
            { text += $"\n\n[Could not save the full output: {error.Message}]"; }
        }
        return new([JsonData.Parse(JsonSerializer.Serialize(new { type = "text", text })), .. content.Where(block => Kind(block) == "image")], path);
    }
}
