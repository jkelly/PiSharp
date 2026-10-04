using System.Text;

namespace PiSharp.Agent.Tools;

public enum ToolOutputTruncationLimit { Lines, Bytes }

/// <summary>Nonnegative integer budgets; zero is an intentional limit.</summary>
public sealed record ToolOutputTruncationOptions(
    int MaxLines = ToolOutputTruncator.DefaultMaxLines,
    int MaxBytes = ToolOutputTruncator.DefaultMaxBytes);

/// <summary>Source-compatible metadata, including the upstream partial-tail line count and limit precedence.</summary>
public sealed record ToolOutputTruncationResult(
    string Content,
    bool Truncated,
    ToolOutputTruncationLimit? TruncatedBy,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes,
    bool LastLinePartial,
    bool FirstLineExceedsLimit,
    int MaxLines,
    int MaxBytes);

/// <summary>
/// Pure string head/tail output limiting. LF separates lines; CR, BOM and NUL remain text.
/// Unpaired UTF-16 surrogates count as UTF-8 U+FFFD, as in Node Buffer. An unchanged or complete-line
/// result preserves the original UTF-16; a partial tail is decoded from the replacement-encoded bytes.
/// This class does not detect binary files, decode external bytes, execute tools or enforce policy.
/// </summary>
public static class ToolOutputTruncator
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultMaxBytes = 50 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static ToolOutputTruncationResult Head(string content, ToolOutputTruncationOptions? options = null) =>
        Truncate(content, options, fromTail: false);

    public static ToolOutputTruncationResult Tail(string content, ToolOutputTruncationOptions? options = null) =>
        Truncate(content, options, fromTail: true);

    private static ToolOutputTruncationResult Truncate(string content, ToolOutputTruncationOptions? options, bool fromTail)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxLines);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxBytes);
        var totalBytes = Utf8.GetByteCount(content);
        var lines = content.Length == 0 ? [] : content.Split('\n');
        var totalLines = lines.Length - (content.EndsWith('\n') ? 1 : 0);
        if (totalLines <= options.MaxLines && totalBytes <= options.MaxBytes)
            return Result(content, false, null, totalLines, totalBytes);

        if (!fromTail && Utf8.GetByteCount(lines[0]) > options.MaxBytes)
            return Result(string.Empty, true, ToolOutputTruncationLimit.Bytes, 0, 0, firstLineExceedsLimit: true);

        var kept = new List<string>();
        var outputBytes = 0;
        var truncatedBy = ToolOutputTruncationLimit.Lines;
        var partial = false;
        for (var offset = 0; offset < totalLines && kept.Count < options.MaxLines; offset++)
        {
            var line = lines[fromTail ? totalLines - offset - 1 : offset];
            var lineBytes = Utf8.GetByteCount(line);
            if ((long)outputBytes + lineBytes + (kept.Count > 0 ? 1 : 0) > options.MaxBytes)
            {
                truncatedBy = ToolOutputTruncationLimit.Bytes;
                if (fromTail && kept.Count == 0)
                {
                    var bytes = Utf8.GetBytes(line);
                    var start = bytes.Length - options.MaxBytes;
                    while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
                    kept.Add(Utf8.GetString(bytes.AsSpan(start)));
                    outputBytes = bytes.Length - start;
                    partial = true;
                }
                break;
            }
            kept.Add(line);
            outputBytes += lineBytes + (kept.Count > 1 ? 1 : 0);
        }
        // The pinned source applies line-limit precedence even to an admitted partial tail.
        if (kept.Count >= options.MaxLines && outputBytes <= options.MaxBytes)
            truncatedBy = ToolOutputTruncationLimit.Lines;
        if (fromTail) kept.Reverse();
        var output = string.Join('\n', kept);
        return Result(output, true, truncatedBy, kept.Count, Utf8.GetByteCount(output), partial);

        ToolOutputTruncationResult Result(string output, bool truncated, ToolOutputTruncationLimit? limit,
            int outputLines, int bytes, bool lastLinePartial = false, bool firstLineExceedsLimit = false) =>
            new(output, truncated, limit, totalLines, totalBytes, outputLines, bytes, lastLinePartial,
                firstLineExceedsLimit, options.MaxLines, options.MaxBytes);
    }
}
