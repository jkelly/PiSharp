// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/mime.ts.
namespace PiSharp.Tools.Images;

/// <summary>Source mime.ts: supported image types detected from file magic, never from the file name.</summary>
public static class ImageMime
{
    /// <summary>Source IMAGE_TYPE_SNIFF_BYTES: the leading bytes inspected for a file.</summary>
    public const int SniffBytes = 4100;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>Source detectSupportedImageMimeType: jpeg (not JPEG-LS), non-animated png, gif, webp and bmp.</summary>
    public static string? DetectSupportedImageMimeType(ReadOnlySpan<byte> buffer)
    {
        if (buffer.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])) return buffer.Length > 3 && buffer[3] == 0xf7 ? null : "image/jpeg";
        if (buffer.StartsWith(PngSignature)) return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        if (Ascii(buffer, 0, "GIF87a") || Ascii(buffer, 0, "GIF89a")) return "image/gif";
        if (Ascii(buffer, 0, "RIFF") && Ascii(buffer, 8, "WEBP")) return "image/webp";
        if (Ascii(buffer, 0, "BM") && IsBmp(buffer)) return "image/bmp";
        return null;
    }

    /// <summary>Source detectSupportedImageMimeTypeFromFile over the first <see cref="SniffBytes"/> bytes.</summary>
    public static async ValueTask<string?> DetectSupportedImageMimeTypeFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
        var buffer = new byte[SniffBytes]; var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return DetectSupportedImageMimeType(buffer.AsSpan(0, count));
    }

    private static bool IsPng(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 16 && ReadUInt32BE(buffer, PngSignature.Length) == 13 && Ascii(buffer, 12, "IHDR");

    private static bool IsAnimatedPng(ReadOnlySpan<byte> buffer)
    {
        long offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var length = ReadUInt32BE(buffer, (int)offset);
            if (Ascii(buffer, (int)offset + 4, "acTL")) return true;
            if (Ascii(buffer, (int)offset + 4, "IDAT")) return false;
            var next = offset + 8 + length + 4;
            if (next <= offset || next > buffer.Length) return false;
            offset = next;
        }
        return false;
    }

    private static bool IsBmp(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 26) return false;
        var declaredFileSize = ReadUInt32LE(buffer, 2);
        var pixelDataOffset = ReadUInt32LE(buffer, 10);
        var dibHeaderSize = ReadUInt32LE(buffer, 14);
        if (declaredFileSize != 0 && declaredFileSize < 26) return false;
        if (pixelDataOffset < 14L + dibHeaderSize) return false;
        if (declaredFileSize != 0 && pixelDataOffset >= declaredFileSize) return false;
        int planes, bits;
        if (dibHeaderSize == 12) { planes = ReadUInt16LE(buffer, 22); bits = ReadUInt16LE(buffer, 24); }
        else if (dibHeaderSize is >= 40 and <= 124)
        {
            if (buffer.Length < 30) return false;
            planes = ReadUInt16LE(buffer, 26); bits = ReadUInt16LE(buffer, 28);
        }
        else return false;
        return planes == 1 && bits is 1 or 4 or 8 or 16 or 24 or 32;
    }

    // Source readers treat bytes past the end as zero.
    private static int ReadUInt16LE(ReadOnlySpan<byte> buffer, int offset) => At(buffer, offset) | At(buffer, offset + 1) << 8;
    private static long ReadUInt32LE(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)(At(buffer, offset) | At(buffer, offset + 1) << 8 | At(buffer, offset + 2) << 16 | At(buffer, offset + 3) << 24);
    private static long ReadUInt32BE(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)(At(buffer, offset) << 24 | At(buffer, offset + 1) << 16 | At(buffer, offset + 2) << 8 | At(buffer, offset + 3));
    private static int At(ReadOnlySpan<byte> buffer, int offset) => offset >= 0 && offset < buffer.Length ? buffer[offset] : 0;
    private static bool Ascii(ReadOnlySpan<byte> buffer, int offset, string text)
    {
        if (offset < 0 || buffer.Length < offset + text.Length) return false;
        for (var index = 0; index < text.Length; index++) if (buffer[offset + index] != text[index]) return false;
        return true;
    }
}
