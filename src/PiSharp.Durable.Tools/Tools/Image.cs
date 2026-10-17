// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/image.ts
// Detection and header dimensions only: decoding is an image processor's (ImageProcessor.cs, Images/).
namespace PiSharp.Durable.Tools;

/// <summary>Positional reads of a file of <see cref="Size"/> bytes (upstream <c>ByteSource</c>).</summary>
/// <param name="Size">The file size.</param>
/// <param name="Read">Up to <c>length</c> bytes at <c>offset</c>; fewer only at end of file.</param>
internal sealed record ByteSource(long Size, Func<long, long, Task<byte[]>> Read);

/// <summary>Upstream <c>detectSupportedImageMimeType</c> and <c>detectSupportedImageMimeTypeOf</c>.</summary>
internal static class ImageDetection
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    /// <summary>Bytes every check except the APNG chunk walk needs: BMP reads up to offset 29.</summary>
    private const int HeaderBytes = 32;
    private const int BlockBytes = 64 * 1024;

    /// <summary><see cref="DetectSupportedImageMimeType"/> of a whole file, reading only its header and, for PNG, the chunk headers up to
    /// the first <c>acTL</c> or <c>IDAT</c>.</summary>
    public static async Task<string?> DetectSupportedImageMimeTypeOfAsync(ByteSource source)
    {
        var header = await source.Read(0, HeaderBytes).ConfigureAwait(false);
        if (!StartsWith(header, PngSignature)) return DetectSupportedImageMimeType(header);
        return IsPng(header) && !await IsAnimatedPngOfAsync(source).ConfigureAwait(false) ? "image/png" : null;
    }

    /// <summary><see cref="IsAnimatedPng"/> over a file read in blocks.</summary>
    private static async Task<bool> IsAnimatedPngOfAsync(ByteSource source)
    {
        var block = Array.Empty<byte>();
        long blockStart = 0;
        async Task<byte[]> BytesAt(long offset, int length)
        {
            if (offset < blockStart || offset + length > blockStart + block.Length)
            {
                blockStart = offset;
                block = await source.Read(offset, BlockBytes).ConfigureAwait(false);
            }
            var from = (int)Math.Min(offset - blockStart, block.Length);
            return block[from..Math.Min(from + length, block.Length)];
        }
        long offset = PngSignature.Length;
        while (offset + 8 <= source.Size)
        {
            var chunkHeader = await BytesAt(offset, 8).ConfigureAwait(false);
            var chunkLength = ReadUint32BE(chunkHeader, 0);
            if (StartsWithAscii(chunkHeader, 4, "acTL")) return true;
            if (StartsWithAscii(chunkHeader, 4, "IDAT")) return false;
            var nextOffset = offset + 8 + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > source.Size) return false;
            offset = nextOffset;
        }
        return false;
    }

    /// <summary>The MIME type of a supported image (JPEG, non-animated PNG, GIF, WebP, BMP) by its leading bytes; null otherwise.</summary>
    public static string? DetectSupportedImageMimeType(ReadOnlySpan<byte> buffer)
    {
        if (StartsWith(buffer, [0xff, 0xd8, 0xff])) return buffer.Length > 3 && buffer[3] == 0xf7 ? null : "image/jpeg";
        if (StartsWith(buffer, PngSignature)) return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        if (StartsWithAscii(buffer, 0, "GIF87a") || StartsWithAscii(buffer, 0, "GIF89a")) return "image/gif";
        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP")) return "image/webp";
        if (StartsWithAscii(buffer, 0, "BM") && IsBmp(buffer)) return "image/bmp";
        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 16 && ReadUint32BE(buffer, PngSignature.Length) == 13 && StartsWithAscii(buffer, 12, "IHDR");

    private static bool IsAnimatedPng(ReadOnlySpan<byte> buffer)
    {
        long offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = ReadUint32BE(buffer, (int)offset);
            var chunkTypeOffset = (int)offset + 4;
            if (StartsWithAscii(buffer, chunkTypeOffset, "acTL")) return true;
            if (StartsWithAscii(buffer, chunkTypeOffset, "IDAT")) return false;
            var nextOffset = offset + 8 + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > buffer.Length) return false;
            offset = nextOffset;
        }
        return false;
    }

    /// <summary>
    /// The width and height an image of <paramref name="mimeType"/> declares in its header, without decoding it: PNG's IHDR, GIF's screen,
    /// WebP's VP8, VP8L, or VP8X chunk, JPEG's first SOF segment. Null when the header cannot be read.
    /// </summary>
    public static (long Width, long Height)? ImageDimensions(ReadOnlySpan<byte> bytes, string mimeType)
    {
        switch (mimeType)
        {
            case "image/png":
                return bytes.Length < 24 ? null : (ReadUint32BE(bytes, 16), ReadUint32BE(bytes, 20));
            case "image/gif":
                return bytes.Length < 10 ? null : (ReadUint16LE(bytes, 6), ReadUint16LE(bytes, 8));
            case "image/webp":
                if (bytes.Length < 30) return null;
                if (StartsWithAscii(bytes, 12, "VP8 ")) return (ReadUint16LE(bytes, 26) & 0x3fff, ReadUint16LE(bytes, 28) & 0x3fff);
                if (StartsWithAscii(bytes, 12, "VP8L"))
                {
                    var bits = ReadUint32LE(bytes, 21);
                    return ((int)(bits & 0x3fff) + 1, (int)((bits >>> 14) & 0x3fff) + 1);
                }
                if (StartsWithAscii(bytes, 12, "VP8X"))
                {
                    static int Uint24(ReadOnlySpan<byte> buffer, int offset) => ReadUint16LE(buffer, offset) + (At(buffer, offset + 2) << 16);
                    return (Uint24(bytes, 24) + 1, Uint24(bytes, 27) + 1);
                }
                return null;
            case "image/jpeg":
                for (var offset = 2; offset + 9 <= bytes.Length;)
                {
                    if (bytes[offset] != 0xff) return null;
                    var marker = bytes[offset + 1];
                    if (marker == 0xff)
                    {
                        offset++;
                        continue;
                    }
                    // SOF0 to SOF15, except DHT (C4), JPG (C8), and DAC (CC), which share the range.
                    if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
                        return (ReadUint16BE(bytes, offset + 7), ReadUint16BE(bytes, offset + 5));
                    offset += 2 + ReadUint16BE(bytes, offset + 2);
                }
                return null;
            default:
                return null;
        }
    }

    private static bool IsBmp(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 26) return false;
        var declaredFileSize = ReadUint32LE(buffer, 2);
        var pixelDataOffset = ReadUint32LE(buffer, 10);
        var dibHeaderSize = ReadUint32LE(buffer, 14);
        if (declaredFileSize != 0 && declaredFileSize < 26) return false;
        if (pixelDataOffset < 14 + dibHeaderSize) return false;
        if (declaredFileSize != 0 && pixelDataOffset >= declaredFileSize) return false;

        int colorPlanes, bitsPerPixel;
        if (dibHeaderSize == 12)
        {
            colorPlanes = ReadUint16LE(buffer, 22);
            bitsPerPixel = ReadUint16LE(buffer, 24);
        }
        else if (dibHeaderSize is >= 40 and <= 124)
        {
            if (buffer.Length < 30) return false;
            colorPlanes = ReadUint16LE(buffer, 26);
            bitsPerPixel = ReadUint16LE(buffer, 28);
        }
        else return false;
        return colorPlanes == 1 && bitsPerPixel is 1 or 4 or 8 or 16 or 24 or 32;
    }

    // A read past the end is 0, as upstream's `?? 0` makes it.
    private static int At(ReadOnlySpan<byte> buffer, int offset) => offset >= 0 && offset < buffer.Length ? buffer[offset] : 0;

    private static int ReadUint16LE(ReadOnlySpan<byte> buffer, int offset) => At(buffer, offset) + (At(buffer, offset + 1) << 8);

    private static int ReadUint16BE(ReadOnlySpan<byte> buffer, int offset) => (At(buffer, offset) << 8) + At(buffer, offset + 1);

    private static long ReadUint32BE(ReadOnlySpan<byte> buffer, int offset) =>
        At(buffer, offset) * 0x1000000L + (At(buffer, offset + 1) << 16) + (At(buffer, offset + 2) << 8) + At(buffer, offset + 3);

    private static long ReadUint32LE(ReadOnlySpan<byte> buffer, int offset) =>
        At(buffer, offset) + (At(buffer, offset + 1) << 8) + (At(buffer, offset + 2) << 16) + At(buffer, offset + 3) * 0x1000000L;

    private static bool StartsWith(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> bytes) =>
        buffer.Length >= bytes.Length && buffer[..bytes.Length].SequenceEqual(bytes);

    private static bool StartsWithAscii(ReadOnlySpan<byte> buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length) return false;
        for (var index = 0; index < text.Length; index++)
            if (buffer[offset + index] != text[index]) return false;
        return true;
    }
}
