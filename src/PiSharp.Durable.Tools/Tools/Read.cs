// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/read.ts
// Line numbers, offsets and limits are JavaScript numbers here too (doubles): the model may send 2.5, a negative number or 1e20, and the
// result, its notices and its errors are upstream's for each, numbers printed as JavaScript prints them.
// The conversation's model comes from api.AgentAsync() and api.Models.GetModel(), and its image limits from DurableModel.InputLimits (pi-ai
// 1.2.0 model.inputLimits.images.resize), as upstream's imageModel() reads them.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>Options of the read tool (upstream <c>ReadToolOptions</c>).</summary>
public sealed record ReadToolOptions
{
    /// <summary>Resizes and converts images; without one, images within <see cref="ImageLimits.Default"/> pass through as they are.</summary>
    public IImageProcessor? Images { get; init; }
}

/// <summary>Upstream <c>createReadTool</c>.</summary>
internal static class ReadTool
{
    /// <summary><c>Type.Object({ path, offset?, limit? })</c> as TypeBox prints it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"Path to the file to read (relative or absolute)"},"offset":{"type":"number","description":"Line number to start reading from (1-indexed)"},"limit":{"type":"number","description":"Maximum number of lines to read"}}}""");

    private const int ReadChunk = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Reads text files and images. Remarks about truncation, continuation, and image processing are diagnostics; the content is only file
    /// text, or one image, which programs get as its image content.
    /// </summary>
    public static ToolRegistration Create(ReadToolOptions? options = null)
    {
        var images = options?.Images;
        var formats = images is null ? "jpg, png, gif, webp" : "jpg, png, gif, webp, bmp";
        return Define.Tool(new ToolRegistration
        {
            Name = "read",
            Description = $"Read the contents of a file. Supports text files and images ({formats}). Images are sent as attachments. For text files, output is truncated to {Truncation.DefaultMaxLines} lines or {Truncation.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.",
            Parameters = Parameters,
            Execute = (arguments, api, context) =>
            {
                var args = (JsonObjectValue)arguments;
                return ExecuteAsync(((JsonStringValue)args["path"]).Value, Number(args, "offset"), Number(args, "limit"), api, context, images);
            },
        });
    }

    private static double? Number(JsonObjectValue args, string key) => args.TryGetValue(key, out var value) && value is JsonNumberValue number ? number.Value : null;

    /// <summary>The tool's execution with its arguments as JavaScript numbers, NaN included (JSON arguments cannot carry NaN).</summary>
    internal static async Task<ToolExecutionResult> ExecuteAsync(string path, double? offset, double? limit, IToolExecutionApi api, DurableContext context,
        IImageProcessor? images = null)
    {
        var env = ToolEnv.Require(api);
        var absolutePath = await PathUtils.ResolveReadToolPathAsync(env, path, context).ConfigureAwait(false);
        var reader = (await env.OpenBinaryReaderAsync(absolutePath, null, context).ConfigureAwait(false)).GetOrThrow();
        try
        {
            // A concurrent writer can change the file between the scan and the reads. Appending (a growing log) leaves the scanned text as it
            // was; an image must be whole. Otherwise the file is read again once.
            for (var attempt = 0; ; attempt++)
            {
                var before = (await reader.InfoAsync(context).ConfigureAwait(false)).GetOrThrow();
                var mimeType = await ImageDetection.DetectSupportedImageMimeTypeOfAsync(new ByteSource(before.Size,
                    async (position, length) => (await reader.ReadAsync(position, length, context).ConfigureAwait(false)).GetOrThrow())).ConfigureAwait(false);
                var result = mimeType is null
                    ? await ReadTextAsync(reader, path, offset, limit, context).ConfigureAwait(false)
                    : await ReadImageAsync(reader, before, path, mimeType, images, await ImageModelAsync(api, context).ConfigureAwait(false), context)
                        .ConfigureAwait(false);
                var after = (await reader.InfoAsync(context).ConfigureAwait(false)).GetOrThrow();
                var unchanged = after.Size == before.Size && after.MtimeMs == before.MtimeMs;
                if (unchanged || (mimeType is null && after.Size > before.Size)) return result;
                if (attempt == 1) throw new InvalidOperationException($"{path} changed while it was read");
            }
        }
        finally
        {
            await reader.CloseAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One image block, prepared by <paramref name="processor"/> or, without one, as it is when its format is inline and it fits the limits
    /// (its size is checked before reading it, its dimensions from its header); otherwise an error result saying why. What happened to the
    /// image is an <c>info</c> diagnostic, so programs get the bare image content. A model without image input sees pi-ai's placeholder
    /// instead; a diagnostic says so.
    /// </summary>
    private static async Task<ToolExecutionResult> ReadImageAsync(IBinaryReader reader, FileMetadata info, string path, string mimeType,
        IImageProcessor? processor, ImageModel model, DurableContext context)
    {
        var limits = model.Limits;
        string data, imageMimeType;
        var notes = new List<string>();
        if (processor is not null)
        {
            var bytes = (await reader.ReadAsync(0, info.Size, context).ConfigureAwait(false)).GetOrThrow();
            var prepared = await processor.PrepareAsync(bytes, mimeType, limits).ConfigureAwait(false);
            if (prepared is null) return ImageError($"{path} is an image ({mimeType}) that cannot be prepared for the model");
            (data, imageMimeType) = (prepared.Data, prepared.MimeType);
            if (prepared.ConvertedFrom is { } convertedFrom) notes.Add($"Converted from {convertedFrom} to {prepared.MimeType}.");
            if (prepared.Resized is { } resized)
            {
                var (from, to) = (resized.From, resized.To);
                // toFixed(2).
                var scale = ((double)from.Width / to.Width).ToString("F2", CultureInfo.InvariantCulture);
                notes.Add($"Resized from {from.Width}x{from.Height} to {to.Width}x{to.Height}. Multiply coordinates by {scale} to map them to the original.");
            }
        }
        else
        {
            if (!ImageFormats.Inline.Contains(mimeType))
                return ImageError($"{path} is an image ({mimeType}) that needs converting, and no image processor is configured");
            const string unshrinkable = "and no image processor is configured to shrink it";
            if (ImageFormats.Base64Length(info.Size) > limits.MaxBytes)
                return ImageError($"{path} is an image ({mimeType}) of {Truncation.FormatSize(info.Size)}, too large to send, {unshrinkable}");
            var bytes = (await reader.ReadAsync(0, info.Size, context).ConfigureAwait(false)).GetOrThrow();
            if (ImageDetection.ImageDimensions(bytes, mimeType) is not { } size)
                return ImageError($"{path} is an image ({mimeType}) whose size cannot be read");
            if (size.Width > limits.MaxWidth || size.Height > limits.MaxHeight)
                return ImageError($"{path} is an image ({mimeType}) of {size.Width}x{size.Height}, larger than {limits.MaxWidth}x{limits.MaxHeight}, {unshrinkable}");
            (data, imageMimeType) = (ImageFormats.ToBase64(bytes), mimeType);
        }
        if (!model.Vision) notes.Add("The current model does not support images; it sees a placeholder instead.");
        return new ToolExecutionResult
        {
            Output = JsonValue.Array(JsonValue.Object([new("type", "image"), new("data", data), new("mimeType", imageMimeType)])),
            Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Info, string.Join(" ", [$"Read image file [{imageMimeType}].", .. notes]), "image")],
        };
    }

    /// <summary>What the conversation's model takes: whether images, and within which limits.</summary>
    private sealed record ImageModel(bool Vision, ImageLimits Limits);

    /// <summary>
    /// The conversation model's image input: <see cref="ImageLimits.Default"/> overridden by its <c>inputLimits.images.resize</c>. Without a
    /// resolvable model, it counts as one that takes images within the defaults.
    /// </summary>
    private static async Task<ImageModel> ImageModelAsync(IToolExecutionApi api, DurableContext context)
    {
        var reference = (await api.AgentAsync(context).ConfigureAwait(false)).Model;
        var model = reference is null ? null : api.Models.GetModel(reference.Provider, reference.ModelId);
        var resize = model?.InputLimits?.Images?.Resize;
        var defaults = ImageLimits.Default;
        return new ImageModel(model is null || model.Input.Contains("image"), new ImageLimits(resize?.MaxWidth ?? defaults.MaxWidth,
            resize?.MaxHeight ?? defaults.MaxHeight, resize?.MaxBytes ?? defaults.MaxBytes));
    }

    private static ToolExecutionResult ImageError(string message) => new()
    {
        Output = JsonArrayValue.Empty,
        IsError = true,
        Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Error, message, "unsupported_image")],
    };

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
    private static async Task<ToolExecutionResult> ReadTextAsync(IBinaryReader reader, string path, double? offset, double? limit, DurableContext context)
    {
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
            Output = outputText.Length == 0 ? JsonArrayValue.Empty : ToolContent.Text(outputText),
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
