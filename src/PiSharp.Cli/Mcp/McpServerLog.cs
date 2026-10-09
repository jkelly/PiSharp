// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/log.ts (formatMcpLogMessage, McpServerLog).
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Cli.Mcp;

/// <summary>Log messages MCP servers send with <c>notifications/message</c>, appended to <c>mcp.log</c> in the agent directory as
/// <c>&lt;time&gt; [&lt;server&gt;] &lt;level&gt; &lt;logger&gt;: &lt;message&gt;</c>. Several processes may write to the same file, so every
/// message is one append. The file moves to <c>mcp.log.1</c> once it grows past 5 MB. Write errors are ignored: logging must not
/// break tools.</summary>
internal sealed class McpServerLog(string path)
{
    internal const long MaximumBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object gate = new();
    private long? size;
    public string Path { get; } = path;

    /// <summary>formatMcpLogMessage: one line; continuation lines are indented.</summary>
    internal static string Format(string server, JsonData? parameters, DateTimeOffset now)
    {
        var value = parameters?.Value;
        var isRecord = value is { ValueKind: JsonValueKind.Object };
        var level = isRecord && value!.Value.TryGetProperty("level", out var supplied) && supplied.ValueKind == JsonValueKind.String ? supplied.GetString()! : "info";
        var logger = isRecord && value!.Value.TryGetProperty("logger", out var name) && name.ValueKind == JsonValueKind.String && name.GetString()!.Length > 0
            ? $" {name.GetString()}:" : "";
        JsonElement? data = isRecord ? value!.Value.TryGetProperty("data", out var field) ? field : null : value;
        var text = data is { ValueKind: JsonValueKind.String } textual ? textual.GetString()!
            : data is { } other ? JsonSerializer.Serialize(other, Compact) : "undefined";
        text = System.Text.RegularExpressions.Regex.Replace(text, "\r?\n", "\n    ");
        return $"{now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} [{server}] {level}{logger} {text}\n";
    }

    public void Write(string server, JsonData? parameters)
    {
        var line = Format(server, parameters, DateTimeOffset.UtcNow);
        lock (gate)
        {
            try
            {
                if (size is null)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    size = CurrentSize();
                }
                if (size > MaximumBytes)
                {
                    // Another process may have rotated it already; check before renaming.
                    if (CurrentSize() > MaximumBytes) File.Move(Path, Path + ".1", overwrite: true);
                    size = CurrentSize();
                }
                var bytes = Encoding.UTF8.GetBytes(line);
                using (var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) file.Write(bytes);
                size += bytes.Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The log is best effort. */ }
        }
    }

    private long CurrentSize()
    {
        try { return new FileInfo(Path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }
}
