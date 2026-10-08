// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/exif-orientation.ts.
namespace PiSharp.Tools.Images;

/// <summary>Source exif-orientation.ts: the EXIF orientation tag of JPEG (APP1) and WebP (EXIF chunk) files.</summary>
public static class ExifOrientation
{
    /// <summary>Source getExifOrientation: 1..8, or 1 when absent or malformed.</summary>
    public static int Read(ReadOnlySpan<byte> bytes)
    {
        var tiff = -1;
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8) tiff = FindJpegTiffOffset(bytes);
        else if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) tiff = FindWebpTiffOffset(bytes);
        return tiff == -1 ? 1 : ReadOrientationFromTiff(bytes, tiff);
    }

    /// <summary>Whether the orientation transposes width and height (source rotate90 cases 5 to 8).</summary>
    public static bool Transposes(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>Source applyExifOrientation over an RGBA raster: flips in place, rotations return a new raster.</summary>
    internal static RasterImage Apply(RasterImage image, int orientation)
    {
        switch (orientation)
        {
            case 2: image.FlipHorizontal(); return image;
            case 3: image.FlipHorizontal(); image.FlipVertical(); return image;
            case 4: image.FlipVertical(); return image;
            case 5: { var rotated = Rotate(image, (x, y, _, h) => x * h + (h - 1 - y)); rotated.FlipHorizontal(); return rotated; }
            case 6: return Rotate(image, (x, y, _, h) => x * h + (h - 1 - y));
            case 7: { var rotated = Rotate(image, (x, y, w, h) => (w - 1 - x) * h + y); rotated.FlipHorizontal(); return rotated; }
            case 8: return Rotate(image, (x, y, w, h) => (w - 1 - x) * h + y);
            default: return image;
        }
    }

    private static RasterImage Rotate(RasterImage image, Func<int, int, int, int, long> destination)
    {
        int w = image.Width, h = image.Height;
        var source = image.Pixels; var target = new byte[source.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Buffer.BlockCopy(source, (y * w + x) * 4, target, checked((int)destination(x, y, w, h) * 4), 4);
        return new(h, w, target);
    }

    private static int ReadOrientationFromTiff(ReadOnlySpan<byte> bytes, int tiffStart)
    {
        if (tiffStart + 8 > bytes.Length) return 1;
        var le = (bytes[tiffStart] << 8 | bytes[tiffStart + 1]) == 0x4949;
        var ifdStart = tiffStart + Read32(bytes, tiffStart + 4, le);
        if (ifdStart + 2 > bytes.Length) return 1;
        var entries = Read16(bytes, ifdStart, le);
        for (var index = 0; index < entries; index++)
        {
            var entry = ifdStart + 2 + index * 12L;
            if (entry + 12 > bytes.Length) return 1;
            if (Read16(bytes, entry, le) == 0x0112)
            {
                var value = Read16(bytes, entry + 8, le);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        return 1;
    }

    private static int FindJpegTiffOffset(ReadOnlySpan<byte> bytes)
    {
        long offset = 2;
        while (offset < bytes.Length - 1)
        {
            if (bytes[(int)offset] != 0xff) return -1;
            var marker = bytes[(int)offset + 1];
            if (marker == 0xff) { offset++; continue; }
            if (marker == 0xe1)
            {
                if (offset + 4 >= bytes.Length) return -1;
                var segment = offset + 4;
                if (segment + 6 > bytes.Length) return -1;
                if (HasExifHeader(bytes, (int)segment)) return (int)segment + 6;
            }
            if (offset + 4 > bytes.Length) return -1;
            offset += 2 + (bytes[(int)offset + 2] << 8 | bytes[(int)offset + 3]);
        }
        return -1;
    }

    private static int FindWebpTiffOffset(ReadOnlySpan<byte> bytes)
    {
        long offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var o = (int)offset;
            var size = (long)(bytes[o + 4] | bytes[o + 5] << 8 | bytes[o + 6] << 16) | (long)(sbyte)bytes[o + 7] << 24;
            var data = offset + 8;
            if (bytes.Slice(o, 4).SequenceEqual("EXIF"u8))
            {
                if (data + size > bytes.Length) return -1;
                return size >= 6 && HasExifHeader(bytes, (int)data) ? (int)data + 6 : (int)data;
            }
            offset = data + size + size % 2;
            if (offset <= o) return -1;
        }
        return -1;
    }

    private static bool HasExifHeader(ReadOnlySpan<byte> bytes, int offset) =>
        offset >= 0 && offset + 6 <= bytes.Length && bytes.Slice(offset, 6).SequenceEqual("Exif\0\0"u8);
    private static int Read16(ReadOnlySpan<byte> b, long p, bool le) => le ? At(b, p) | At(b, p + 1) << 8 : At(b, p) << 8 | At(b, p + 1);
    // Source read32: little-endian is a signed 32-bit value, big-endian is unsigned (>>> 0).
    private static long Read32(ReadOnlySpan<byte> b, long p, bool le) => le
        ? At(b, p) | At(b, p + 1) << 8 | At(b, p + 2) << 16 | At(b, p + 3) << 24
        : (uint)(At(b, p) << 24 | At(b, p + 1) << 16 | At(b, p + 2) << 8 | At(b, p + 3));
    private static int At(ReadOnlySpan<byte> bytes, long position) => position >= 0 && position < bytes.Length ? bytes[(int)position] : 0;
}
