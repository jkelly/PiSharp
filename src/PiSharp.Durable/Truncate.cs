// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/truncate.ts.
// Shared truncation utilities for tool outputs. Truncation is based on two independent limits, whichever is hit first: a line limit
// (default 2000 lines) and a byte limit (default 50KB). Never returns partial lines. Tool output streams are bounded by the harness's
// output buffer (P9) instead. Strings are UTF-16 as in JavaScript; byte counts are UTF-8, a lone surrogate counting as U+FFFD.
using System.Globalization;

namespace PiSharp.Durable;

/// <summary>Which limit truncated the content.</summary>
public enum TruncationLimit
{
    /// <summary><c>"lines"</c>.</summary>
    Lines,
    /// <summary><c>"bytes"</c>.</summary>
    Bytes,
}

/// <summary>Limits for <see cref="Truncation.TruncateHead"/>; null takes the default.</summary>
/// <param name="MaxLines">Maximum number of lines (default 2000).</param>
/// <param name="MaxBytes">Maximum number of UTF-8 bytes (default 50KB).</param>
public sealed record TruncationOptions(int? MaxLines = null, int? MaxBytes = null);

/// <summary>The result of a truncation (upstream <c>TruncationResult</c>).</summary>
public sealed record TruncationResult
{
    /// <summary>The truncated content.</summary>
    public required string Content { get; init; }
    /// <summary>Whether truncation occurred.</summary>
    public required bool Truncated { get; init; }
    /// <summary>Which limit was hit, or null if not truncated.</summary>
    public required TruncationLimit? TruncatedBy { get; init; }
    /// <summary>Total number of lines in the original content.</summary>
    public required int TotalLines { get; init; }
    /// <summary>Total number of bytes in the original content.</summary>
    public required long TotalBytes { get; init; }
    /// <summary>Number of complete lines in the truncated output.</summary>
    public required int OutputLines { get; init; }
    /// <summary>Number of bytes in the truncated output.</summary>
    public required long OutputBytes { get; init; }
    /// <summary>Whether the last line was partially truncated (only for the tail-truncation edge case).</summary>
    public required bool LastLinePartial { get; init; }
    /// <summary>Whether the first line exceeded the byte limit (head truncation).</summary>
    public required bool FirstLineExceedsLimit { get; init; }
    /// <summary>The max lines limit that was applied.</summary>
    public required int MaxLines { get; init; }
    /// <summary>The max bytes limit that was applied.</summary>
    public required int MaxBytes { get; init; }
}

/// <summary>Head truncation of tool output (upstream <c>truncate.ts</c>).</summary>
public static class Truncation
{
    /// <summary>The default line limit.</summary>
    public const int DefaultMaxLines = 2000;
    /// <summary>The default byte limit, 50KB.</summary>
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>The UTF-8 byte length of <paramref name="content"/>, a lone surrogate counting three bytes (as U+FFFD).</summary>
    public static long Utf8ByteLength(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        long bytes = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var code = content[index];
            if (code <= 0x7f) bytes += 1;
            else if (code <= 0x7ff) bytes += 2;
            else if (char.IsHighSurrogate(code) && index + 1 < content.Length && char.IsLowSurrogate(content[index + 1])) { bytes += 4; index++; }
            else bytes += 3;
        }
        return bytes;
    }

    /// <summary>Formats a byte count as a human-readable size: <c>1023B</c>, <c>1.5KB</c>, <c>3.0MB</c>.</summary>
    public static string FormatSize(long bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + "B"
        : bytes < 1024 * 1024 ? ToFixed1(bytes / 1024.0) + "KB"
        : ToFixed1(bytes / (1024.0 * 1024.0)) + "MB";

    /// <summary>Truncates content from the head (keeps the first lines and bytes), as for file reads. Never returns partial lines: when the
    /// first line alone exceeds the byte limit, the content is empty and <see cref="TruncationResult.FirstLineExceedsLimit"/> is set.</summary>
    public static TruncationResult TruncateHead(string content, TruncationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return TruncateHeadOf(content, SplitLinesForCounting(content).Count, Utf8ByteLength(content), options);
    }

    /// <summary><see cref="TruncateHead"/> of a text known by a prefix and its totals (<paramref name="totalLines"/> counted like
    /// <see cref="TruncateHead"/>, ignoring a trailing newline). The prefix must be the whole text, or longer than <c>maxBytes + 1</c> UTF-8
    /// bytes, or hold at least <c>maxLines</c> newlines; then the result equals <see cref="TruncateHead"/> of the whole text.</summary>
    public static TruncationResult TruncateHeadOf(string prefix, int totalLines, long totalBytes, TruncationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;
        var lines = SplitLinesForCounting(prefix);

        if (totalLines <= maxLines && totalBytes <= maxBytes)
            return Result(prefix, false, null, totalLines, totalBytes, totalLines, totalBytes, false, maxLines, maxBytes);

        var firstLineBytes = lines.Count == 0 ? 0 : Utf8ByteLength(lines[0]);
        if (firstLineBytes > maxBytes)
            return Result("", true, TruncationLimit.Bytes, totalLines, totalBytes, 0, 0, true, maxLines, maxBytes);

        var output = new List<string>();
        long outputBytes = 0;
        var truncatedBy = TruncationLimit.Lines;
        for (var index = 0; index < lines.Count && index < maxLines; index++)
        {
            var line = lines[index];
            var lineBytes = Utf8ByteLength(line) + (index > 0 ? 1 : 0); // +1 for the newline
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = TruncationLimit.Bytes;
                break;
            }
            output.Add(line);
            outputBytes += lineBytes;
        }

        // Without a byte break, only omitted lines prove the line limit was reached; otherwise a trailing newline exceeded bytes.
        if (truncatedBy != TruncationLimit.Bytes) truncatedBy = output.Count < totalLines ? TruncationLimit.Lines : TruncationLimit.Bytes;

        var outputContent = string.Join('\n', output);
        return Result(outputContent, true, truncatedBy, totalLines, totalBytes, output.Count, Utf8ByteLength(outputContent), false, maxLines, maxBytes);
    }

    private static TruncationResult Result(string content, bool truncated, TruncationLimit? truncatedBy, int totalLines, long totalBytes,
        int outputLines, long outputBytes, bool firstLineExceedsLimit, int maxLines, int maxBytes) => new()
    {
        Content = content, Truncated = truncated, TruncatedBy = truncatedBy, TotalLines = totalLines, TotalBytes = totalBytes,
        OutputLines = outputLines, OutputBytes = outputBytes, LastLinePartial = false, FirstLineExceedsLimit = firstLineExceedsLimit,
        MaxLines = maxLines, MaxBytes = maxBytes,
    };

    private static List<string> SplitLinesForCounting(string content)
    {
        if (content.Length == 0) return [];
        var lines = content.Split('\n').ToList();
        if (content.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary><c>Number.prototype.toFixed(1)</c> for the finite non-negative values sizes produce.</summary>
    private static string ToFixed1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
