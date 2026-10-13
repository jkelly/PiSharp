// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/tools/read.ts
// Line numbers, offsets and limits are JavaScript numbers here too (doubles): the model may send 2.5, a negative number or 1e20, and the
// result, its notices and its errors are upstream's for each, numbers printed as JavaScript prints them.
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>Upstream <c>createReadTool</c>.</summary>
internal static class ReadTool
{
    /// <summary><c>Type.Object({ path, offset?, limit? })</c> as TypeBox prints it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"Path to the file to read (relative or absolute)"},"offset":{"type":"number","description":"Line number to start reading from (1-indexed)"},"limit":{"type":"number","description":"Maximum number of lines to read"}}}""");

    private const int ReadChunk = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Reads text files. Remarks about truncation and continuation are diagnostics; the content is only file text.</summary>
    public static ToolRegistration Create() => Define.Tool(new ToolRegistration
    {
        Name = "read",
        Description = $"Read the contents of a text file. Output is truncated to {Truncation.DefaultMaxLines} lines or {Truncation.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.",
        Parameters = Parameters,
        Execute = (arguments, api, context) =>
        {
            var args = (JsonObjectValue)arguments;
            return ExecuteAsync(((JsonStringValue)args["path"]).Value, Number(args, "offset"), Number(args, "limit"), api, context);
        },
    });

    private static double? Number(JsonObjectValue args, string key) => args.TryGetValue(key, out var value) && value is JsonNumberValue number ? number.Value : null;

    /// <summary>The tool's execution with its arguments as JavaScript numbers, NaN included (JSON arguments cannot carry NaN).</summary>
    internal static async Task<ToolExecutionResult> ExecuteAsync(string path, double? offset, double? limit, IToolExecutionApi api, DurableContext context)
    {
        var env = ToolEnv.Require(api);
        var absolutePath = await PathUtils.ResolveReadToolPathAsync(env, path, context).ConfigureAwait(false);
        var reader = (await env.OpenBinaryReaderAsync(absolutePath, null, context).ConfigureAwait(false)).GetOrThrow();
        try
        {
            // A concurrent writer can change the file between the scan and the reads. Appending (a growing log) leaves the scanned bytes as
            // they were; a file that shrank or was rewritten in place is read again once.
            for (var attempt = 0; ; attempt++)
            {
                var before = (await reader.InfoAsync(context).ConfigureAwait(false)).GetOrThrow();
                var result = await ReadTextAsync(reader, before, path, offset, limit, context).ConfigureAwait(false);
                var after = (await reader.InfoAsync(context).ConfigureAwait(false)).GetOrThrow();
                if (after.Size > before.Size || (after.Size == before.Size && after.MtimeMs == before.MtimeMs)) return result;
                if (attempt == 1) throw new InvalidOperationException($"{path} changed while it was read");
            }
        }
        finally
        {
            await reader.CloseAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary><c>Array.prototype.slice</c>'s conversion of an index: NaN is 0, other values truncate toward zero.</summary>
    private static double SliceIndex(double value) => double.IsNaN(value) ? 0 : Math.Truncate(value);

    private static bool IsSafeInteger(double value) => double.IsFinite(value) && Math.Truncate(value) == value && Math.Abs(value) <= Ids.MaxSafeInteger;

    /// <summary>
    /// The decoded start of bytes <c>[start, end)</c> of the file, decoded as part of the whole file: all of it, or enough for
    /// <see cref="Truncation.TruncateHeadOf"/> (more than <c>DefaultMaxBytes + 1</c> bytes, or <c>DefaultMaxLines</c> newlines).
    /// </summary>
    private static async Task<string> ReadHeadAsync(IBinaryReader reader, long start, long end, bool skipBom, DurableContext context)
    {
        var decoder = Utf8Decoding.RangeDecoder();
        var text = new StringBuilder();
        long newlines = 0;
        for (var position = skipBom && start == 0 ? 3 : start; position < end;)
        {
            var bytes = (await reader.ReadAsync(position, Math.Min(ReadChunk, end - position), context).ConfigureAwait(false)).GetOrThrow();
            if (bytes.Length == 0) break;
            position += bytes.Length;
            var decoded = Utf8Decoding.Decode(decoder, bytes, flush: false);
            text.Append(decoded);
            foreach (var character in decoded) if (character == '\n') newlines++;
            if (newlines >= Truncation.DefaultMaxLines || Truncation.Utf8ByteLength(text.ToString()) > Truncation.DefaultMaxBytes + 1)
                return text.ToString();
        }
        return text.Append(Utf8Decoding.Decode(decoder, [], flush: true)).ToString();
    }

    /// <summary>
    /// The read result for the opened file. It equals decoding the whole file with <c>TextDecoder</c>, splitting it on <c>\n</c>, and
    /// bounding the selected lines with <c>truncateHead</c>, while reading only one scan's worth of the file plus the head.
    /// </summary>
    private static async Task<ToolExecutionResult> ReadTextAsync(IBinaryReader reader, FileMetadata info, string path, double? offset, double? limit,
        DurableContext context)
    {
        var mimeType = await ImageDetection.DetectSupportedImageMimeTypeOfAsync(new ByteSource(info.Size,
            async (position, length) => (await reader.ReadAsync(position, length, context).ConfigureAwait(false)).GetOrThrow())).ConfigureAwait(false);
        if (mimeType is not null)
        {
            // Image content is not supported yet.
            return new ToolExecutionResult
            {
                Content = JsonArrayValue.Empty,
                IsError = true,
                Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Error, $"{path} is an image ({mimeType}); reading images is not supported",
                    "unsupported_image")],
            };
        }

        // `offset ? Math.max(0, offset - 1) : 0`: zero and NaN are falsy.
        var startLine = offset is { } given && given != 0 && !double.IsNaN(given) ? Math.Max(0, given - 1) : 0;
        var startLineDisplay = startLine + 1;
        // Lines are selected like `allLines.slice(startLine, endLine)`, which truncates fractional indices.
        var sliceStart = SliceIndex(startLine);
        // One pass finds the line count and the selection; a selection past the last line ends with it, as `slice` does, and an empty one (a
        // zero or negative limit) is scanned as one line and then ignored. A start beyond any file is scanned from 0 only to count lines; the
        // offset check below then fails as before.
        var scanStart = IsSafeInteger(sliceStart) ? (long)sliceStart : 0;
        double? requestedEnd = limit is { } requested ? Math.Max(scanStart + 1, SliceIndex(startLine + requested)) : null;
        long? scanEnd = requestedEnd is { } end && IsSafeInteger(end) ? (long)end : null;
        async Task<LineScan> ScanOf(long? endLine) => (await reader.ScanLinesAsync(scanStart, endLine, context).ConfigureAwait(false)).GetOrThrow();
        var scan = await ScanOf(scanEnd).ConfigureAwait(false);
        double totalFileLines = scan.Newlines + 1;
        if (startLine >= totalFileLines)
            throw new InvalidOperationException($"Offset {Js.NumberToString(offset ?? double.NaN)} is beyond end of file ({Js.NumberToString(totalFileLines)} lines total)");

        double? userLimitedLines = null;
        var selectedLineCount = totalFileLines - sliceStart;
        if (limit is { } lineLimit)
        {
            // Math.min(NaN, x) is NaN.
            var endLine = double.IsNaN(startLine + lineLimit) ? double.NaN : Math.Min(startLine + lineLimit, totalFileLines);
            userLimitedLines = endLine - startLine;
            // `slice` counts a negative end from the end of the lines, which only the line count tells; scan again for it.
            var relativeEnd = SliceIndex(endLine);
            var sliceEnd = relativeEnd < 0 ? Math.Max(totalFileLines + relativeEnd, 0) : relativeEnd;
            selectedLineCount = Math.Max(0, sliceEnd - sliceStart);
            if (selectedLineCount > 0 && relativeEnd < 0) scan = await ScanOf((long)sliceEnd).ConfigureAwait(false);
        }
        var empty = selectedLineCount == 0;
        // Counted like `truncateHead`: a trailing newline adds no line, and empty text has none.
        var endsWithNewline = !empty && scan.LastLineStart == scan.End && scan.LastLineStart > scan.Start;
        var totalLines = empty || scan.SelectedBytes == 0 ? 0 : (int)(selectedLineCount - (endsWithNewline ? 1 : 0));
        var totalBytes = empty ? 0 : scan.SelectedBytes;
        var firstBytes = (await reader.ReadAsync(0, 3, context).ConfigureAwait(false)).GetOrThrow();
        var head = empty ? "" : await ReadHeadAsync(reader, scan.Start, scan.End, Utf8Decoding.StartsWithBom(firstBytes), context).ConfigureAwait(false);

        var truncation = Truncation.TruncateHeadOf(head, totalLines, totalBytes);
        var diagnostics = ImmutableArray.CreateBuilder<ToolDiagnostic>();
        var outputText = truncation.Content;
        JsonValue? details = null;
        if (truncation.FirstLineExceedsLimit)
        {
            // Show the start of the line, cut at the byte limit on a character boundary. Like `allLines[startLine]`, a fractional start line
            // names no line.
            var integral = Math.Truncate(startLine) == startLine;
            var lineBytes = Utf8.GetBytes(integral ? head.Split('\n')[0] : "");
            var lineSize = integral ? scan.FirstLineBytes : 0;
            var cut = ToolOutput.CharacterEnd(lineBytes, Truncation.DefaultMaxBytes);
            outputText = DecodeLikeTextDecoder(lineBytes.AsSpan(0, cut));
            var display = Js.NumberToString(startLineDisplay);
            diagnostics.Add(new ToolDiagnostic(ToolDiagnosticSeverity.Warn,
                $"Line {display} is {Truncation.FormatSize(lineSize)}, exceeds the {Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit; showing its first {Truncation.FormatSize(cut)}. Use bash: sed -n '{display}p' {path} | tail -c +{cut + 1}",
                "truncated"));
            details = Details(truncation with { OutputBytes = cut, OutputLines = 1 });
        }
        else if (truncation.Truncated)
        {
            var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
            var nextOffset = endLineDisplay + 1;
            var limitText = truncation.TruncatedBy == TruncationLimit.Lines ? "" : $" ({Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit)";
            diagnostics.Add(new ToolDiagnostic(ToolDiagnosticSeverity.Info,
                $"Showing lines {Js.NumberToString(startLineDisplay)}-{Js.NumberToString(endLineDisplay)} of {Js.NumberToString(totalFileLines)}{limitText}. Use offset={Js.NumberToString(nextOffset)} to continue.",
                "truncated"));
            details = Details(truncation);
        }
        else if (userLimitedLines is { } limited && startLine + limited < totalFileLines)
        {
            var remaining = totalFileLines - (startLine + limited);
            var nextOffset = startLine + limited + 1;
            diagnostics.Add(new ToolDiagnostic(ToolDiagnosticSeverity.Info,
                $"{Js.NumberToString(remaining)} more lines in file. Use offset={Js.NumberToString(nextOffset)} to continue."));
        }

        return new ToolExecutionResult
        {
            Content = outputText.Length == 0 ? JsonArrayValue.Empty : ToolContent.Text(outputText),
            Details = details,
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    /// <summary><c>new TextDecoder().decode(bytes)</c>: UTF-8 with replacement characters, a leading byte-order mark dropped.</summary>
    private static string DecodeLikeTextDecoder(ReadOnlySpan<byte> bytes)
    {
        var text = Utf8.GetString(bytes);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    /// <summary>Upstream <c>ReadToolDetails</c>: <c>{ truncation }</c>, the truncation without its content, in upstream's key order.</summary>
    private static JsonObjectValue Details(TruncationResult truncation) => JsonValue.Object([new("truncation", TruncationJson(truncation))]);

    internal static JsonObjectValue TruncationJson(TruncationResult truncation) => JsonValue.Object(
    [
        new("truncated", truncation.Truncated),
        new("truncatedBy", truncation.TruncatedBy switch
        {
            TruncationLimit.Lines => "lines",
            TruncationLimit.Bytes => "bytes",
            _ => JsonValue.Null,
        }),
        new("totalLines", truncation.TotalLines),
        new("totalBytes", truncation.TotalBytes),
        new("outputLines", truncation.OutputLines),
        new("outputBytes", truncation.OutputBytes),
        new("lastLinePartial", truncation.LastLinePartial),
        new("firstLineExceedsLimit", truncation.FirstLineExceedsLimit),
        new("maxLines", truncation.MaxLines),
        new("maxBytes", truncation.MaxBytes),
    ]);
}
