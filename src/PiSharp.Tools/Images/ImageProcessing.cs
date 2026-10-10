// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/image-process.ts, utils/image-resize.ts,
// utils/image-resize-core.ts, utils/image-convert.ts (convertImageBytesToPng) and utils/tool-result-images.ts.
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Tools.Images;

/// <summary>Source ImageResizeOptions (and pi-ai ModelImageResizeOptions): 2000x2000 and 4.5MB of base64 by default.</summary>
public sealed record ImageResizeOptions(int MaxWidth = 2000, int MaxHeight = 2000, int MaxBytes = ImageResizeOptions.DefaultMaxBytes, int JpegQuality = 80)
{
    /// <summary>Source DEFAULT_MAX_BYTES: 4.5MB of base64 payload, below Anthropic's 5MB limit.</summary>
    public const int DefaultMaxBytes = 4_718_592;
}

/// <summary>Source ResizedImage: base64 data and the dimensions before and after resizing.</summary>
public sealed record ResizedImage(string Data, string MimeType, int OriginalWidth, int OriginalHeight, int Width, int Height, bool WasResized);

/// <summary>
/// The decode/encode backend Photon provides upstream. Implementations return null when they cannot decode or
/// encode, exactly as the source treats an unavailable or failing Photon.
/// </summary>
public interface IImageCodec
{
    /// <summary>Oriented pixel dimensions from the encoded header, or null when the data cannot be read.</summary>
    (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType);
    /// <summary>Source convertImageBytesToPng: decode (EXIF-oriented) and re-encode as PNG.</summary>
    byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType);
    /// <summary>Decoded, EXIF-oriented candidates for one target size: PNG first, then JPEG at each requested quality the
    /// backend can encode. Null when the backend cannot decode this format.</summary>
    ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width, int height,
        IReadOnlyList<int> jpegQualities);
}

/// <summary>
/// Dependency-free backend. Dimensions are read from PNG, JPEG (with EXIF orientation), GIF, WebP and BMP headers, so any
/// image already within the limits passes through unchanged exactly as with Photon. PNG and BMP decode natively, which
/// covers BMP conversion and resizing PNG/BMP with Lanczos3 to PNG. JPEG, GIF and WebP decoding and JPEG encoding need a
/// codec this backend does not have (Photon upstream), so oversized images of those formats cannot be resized here.
/// </summary>
public sealed class BuiltinImageCodec : IImageCodec
{
    public static BuiltinImageCodec Instance { get; } = new();

    public (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType)
    {
        try
        {
            var size = mimeType switch
            {
                "image/png" => Png(bytes), "image/jpeg" => Jpeg(bytes), "image/gif" => Gif(bytes),
                "image/webp" => Webp(bytes), "image/bmp" => Bmp(bytes), _ => null
            };
            if (size is not { } value || value.Width < 1 || value.Height < 1) return null;
            return ExifOrientation.Transposes(ExifOrientation.Read(bytes)) ? (value.Height, value.Width) : value;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException) { return null; }
    }

    public byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType)
    {
        var raster = RasterCodec.Decode(bytes, mimeType);
        return raster is null ? null : RasterCodec.EncodePng(ExifOrientation.Apply(raster, ExifOrientation.Read(bytes)));
    }

    public ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width, int height,
        IReadOnlyList<int> jpegQualities)
    {
        var raster = RasterCodec.Decode(bytes, mimeType);
        if (raster is null) return null;
        raster = ExifOrientation.Apply(raster, ExifOrientation.Read(bytes));
        return [(RasterCodec.EncodePng(raster.Resize(width, height)), "image/png")];
    }

    private static (int Width, int Height)? Png(ReadOnlySpan<byte> b) => b.Length < 24 ? null
        : ((int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(b[16..])), (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(b[20..])));
    private static (int Width, int Height)? Gif(ReadOnlySpan<byte> b) => b.Length < 10 ? null
        : (BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));
    private static (int Width, int Height)? Bmp(ReadOnlySpan<byte> b)
    {
        var header = BinaryPrimitives.ReadUInt32LittleEndian(b[14..]);
        if (header == 12) return (BinaryPrimitives.ReadUInt16LittleEndian(b[18..]), Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(b[20..])));
        if (b.Length < 26) return null;
        var height = BinaryPrimitives.ReadInt32LittleEndian(b[22..]);
        return height == int.MinValue ? null : (BinaryPrimitives.ReadInt32LittleEndian(b[18..]), Math.Abs(height));
    }
    private static (int Width, int Height)? Webp(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30) return null;
        var chunk = b.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8)) return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (b[20] != 0x2f) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
            return (1 + (int)(bits & 0x3fff), 1 + (int)(bits >> 14 & 0x3fff));
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (b[23] != 0x9d || b[24] != 0x01 || b[25] != 0x2a) return null;
            return (BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3fff);
        }
        return null;
    }
    private static (int Width, int Height)? Jpeg(ReadOnlySpan<byte> b)
    {
        var offset = 2;
        while (offset + 3 < b.Length)
        {
            if (b[offset] != 0xff) return null;
            var marker = b[offset + 1];
            if (marker == 0xff) { offset++; continue; }
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) { offset += 2; continue; }
            if (marker is 0xd9 or 0xda) return null;
            var length = b[offset + 2] << 8 | b[offset + 3];
            if (length < 2) return null;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (offset + 9 > b.Length) return null;
                return (b[offset + 7] << 8 | b[offset + 8], b[offset + 5] << 8 | b[offset + 6]);
            }
            offset += 2 + length;
        }
        return null;
    }
}

/// <summary>Source ProcessImageResult.</summary>
public sealed record ProcessedImage(bool Ok, string? Data, string? MimeType, ImmutableArray<string> Hints, string? Message)
{
    public static ProcessedImage Success(string data, string mimeType, ImmutableArray<string> hints) => new(true, data, mimeType, hints, null);
    public static ProcessedImage Failure(string message) => new(false, null, null, [], message);
}

/// <summary>Source processImage, resizeImage and formatDimensionNote over an <see cref="IImageCodec"/>.</summary>
public static class ImageProcessor
{
    public const string ConversionFailedMessage = "[Image omitted: could not be converted to a supported inline image format.]";
    public const string ResizeFailedMessage = "[Image omitted: could not be resized below the inline image size limit.]";

    /// <summary>Source processImage: normalize to png/jpeg/gif/webp (converting others to PNG), then optionally resize.</summary>
    public static ProcessedImage Process(ReadOnlySpan<byte> bytes, string mimeType, bool autoResizeImages = true,
        ImageResizeOptions? resizeOptions = null, IImageCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        codec ??= BuiltinImageCodec.Instance;
        string? convertedFrom = null; byte[] normalized; string normalizedMime;
        if (NormalizeSupportedMimeType(mimeType) is { } supported) { normalized = bytes.ToArray(); normalizedMime = supported; }
        else
        {
            byte[]? png;
            try { png = codec.ConvertToPng(bytes, BaseMimeType(mimeType)); } catch (Exception) { png = null; }
            if (png is null) return ProcessedImage.Failure(ConversionFailedMessage);
            normalized = png; normalizedMime = "image/png"; convertedFrom = BaseMimeType(mimeType);
        }
        var hints = ImmutableArray.CreateBuilder<string>();
        if (autoResizeImages)
        {
            var resized = Resize(normalized, normalizedMime, resizeOptions, codec);
            if (resized is null) return ProcessedImage.Failure(ResizeFailedMessage);
            if (ConversionHint(convertedFrom, resized.MimeType) is { } converted) hints.Add(converted);
            if (FormatDimensionNote(resized) is { } note) hints.Add(note);
            return ProcessedImage.Success(resized.Data, resized.MimeType, hints.ToImmutable());
        }
        if (ConversionHint(convertedFrom, normalizedMime) is { } conversion) hints.Add(conversion);
        return ProcessedImage.Success(Convert.ToBase64String(normalized), normalizedMime, hints.ToImmutable());
    }

    /// <summary>
    /// Source resizeImageInProcess: an image within all limits passes through unchanged; otherwise fit the dimensions,
    /// try PNG then JPEG qualities, and shrink by 0.75 per step until a candidate is below maxBytes, or null.
    /// </summary>
    public static ResizedImage? Resize(ReadOnlySpan<byte> bytes, string mimeType, ImageResizeOptions? options = null, IImageCodec? codec = null)
    {
        var opts = options ?? new();
        codec ??= BuiltinImageCodec.Instance;
        var inputBase64Size = (bytes.Length + 2L) / 3 * 4;
        (int Width, int Height)? probed;
        try { probed = codec.ProbeDimensions(bytes, mimeType); } catch (Exception) { return null; }
        if (probed is not { } dimensions) return null;
        var (originalWidth, originalHeight) = dimensions;
        if (originalWidth <= opts.MaxWidth && originalHeight <= opts.MaxHeight && inputBase64Size < opts.MaxBytes)
            return new(Convert.ToBase64String(bytes), mimeType.Length != 0 ? mimeType : "image/png", originalWidth, originalHeight,
                originalWidth, originalHeight, false);
        long targetWidth = originalWidth, targetHeight = originalHeight;
        if (targetWidth > opts.MaxWidth) { targetHeight = JsRound(targetHeight * (double)opts.MaxWidth / targetWidth); targetWidth = opts.MaxWidth; }
        if (targetHeight > opts.MaxHeight) { targetWidth = JsRound(targetWidth * (double)opts.MaxHeight / targetHeight); targetHeight = opts.MaxHeight; }
        var qualities = new List<int> { opts.JpegQuality };
        foreach (var quality in new[] { 85, 70, 55, 40 }) if (!qualities.Contains(quality)) qualities.Add(quality);
        int width = (int)Math.Max(0, targetWidth), height = (int)Math.Max(0, targetHeight);
        var input = bytes.ToArray();
        while (true)
        {
            if (width < 1 || height < 1) return null;
            ImmutableArray<(byte[] Bytes, string MimeType)>? candidates;
            try { candidates = codec.Encode(input, mimeType, width, height, qualities); } catch (Exception) { return null; }
            if (candidates is not { } encoded) return null;
            foreach (var (candidate, candidateMime) in encoded)
            {
                var data = Convert.ToBase64String(candidate);
                if (data.Length < opts.MaxBytes) return new(data, candidateMime, originalWidth, originalHeight, width, height, true);
            }
            if (width == 1 && height == 1) break;
            var nextWidth = width == 1 ? 1 : Math.Max(1, (int)Math.Floor(width * 0.75));
            var nextHeight = height == 1 ? 1 : Math.Max(1, (int)Math.Floor(height * 0.75));
            if (nextWidth == width && nextHeight == height) break;
            width = nextWidth; height = nextHeight;
        }
        return null;
    }

    /// <summary>Source formatDimensionNote: the coordinate mapping note for a resized image.</summary>
    public static string? FormatDimensionNote(ResizedImage result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.WasResized) return null;
        var scale = (double)result.OriginalWidth / result.Width;
        return $"[Image: original {result.OriginalWidth}x{result.OriginalHeight}, displayed at {result.Width}x{result.Height}. Multiply coordinates by {scale.ToString("F2", CultureInfo.InvariantCulture)} to map to original image.]";
    }

    /// <summary>
    /// Source normalizeToolResultImages: run every image block of tool content through <see cref="Process"/>. A failed
    /// block is kept as it is; a changed block is replaced, followed by a text block with its hints. Returns the same
    /// instance when nothing changed.
    /// </summary>
    public static JsonData NormalizeToolResultImages(JsonData content, bool autoResizeImages = true, ImageResizeOptions? resizeOptions = null,
        IImageCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Value.ValueKind != JsonValueKind.Array || !content.Value.EnumerateArray().Any(IsImage)) return content;
        var blocks = new List<string>(); var changed = false;
        foreach (var block in content.Value.EnumerateArray())
        {
            if (!IsImage(block)) { blocks.Add(block.GetRawText()); continue; }
            var data = block.GetProperty("data").GetString()!; var mime = block.GetProperty("mimeType").GetString()!;
            byte[] decoded;
            try { decoded = Convert.FromBase64String(data); } catch (FormatException) { blocks.Add(block.GetRawText()); continue; }
            var processed = Process(decoded, mime, autoResizeImages, resizeOptions, codec);
            if (!processed.Ok || processed.Data == data && processed.MimeType == mime && processed.Hints.IsEmpty)
            { blocks.Add(block.GetRawText()); continue; }
            blocks.Add(JsonSerializer.Serialize(new { type = "image", data = processed.Data, mimeType = processed.MimeType }));
            if (!processed.Hints.IsEmpty) blocks.Add(JsonSerializer.Serialize(new { type = "text", text = string.Join("\n", processed.Hints) }));
            changed = true;
        }
        return changed ? JsonData.Parse("[" + string.Join(",", blocks) + "]") : content;

        static bool IsImage(JsonElement block) => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String && type.GetString() == "image" && block.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.String && block.TryGetProperty("mimeType", out var mime) && mime.ValueKind == JsonValueKind.String;
    }

    internal static string BaseMimeType(string mimeType) => mimeType.Split(';')[0].Trim().ToLowerInvariant();

    private static string? NormalizeSupportedMimeType(string mimeType) => BaseMimeType(mimeType) switch
    {
        "image/png" => "image/png",
        "image/jpeg" or "image/jpg" => "image/jpeg",
        "image/gif" => "image/gif",
        "image/webp" => "image/webp",
        _ => null
    };

    private static string? ConversionHint(string? from, string to) => from is null || from == to ? null : $"[Image converted from {from} to {to}.]";
    private static long JsRound(double value) => (long)Math.Floor(value + 0.5);
}
