// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/images/exif.ts
// EXIF orientation of JPEG and WebP files, read from the bytes without decoding the image. Positions are JavaScript numbers upstream, and a
// read past the end is undefined, which its bit operations turn into 0; At() does the same here.
namespace PiSharp.Durable.Tools.Images;

/// <summary>Upstream <c>exifOrientation</c>.</summary>
internal static class Exif
{
    /// <summary>The EXIF orientation of a JPEG or WebP, 1 to 8; 1 when absent or unreadable.</summary>
    public static int Orientation(ReadOnlySpan<byte> bytes)
    {
        var tiff = TiffStart(bytes);
        if (tiff < 0 || tiff + 8 > bytes.Length) return 1;
        var little = bytes[(int)tiff] == 0x49 && bytes[(int)tiff + 1] == 0x49;
        var directory = tiff + U32(bytes, tiff + 4, little);
        if (directory + 2 > bytes.Length) return 1;
        var entries = U16(bytes, directory, little);
        for (var index = 0; index < entries; index++)
        {
            var entry = directory + 2 + index * 12L;
            if (entry + 12 > bytes.Length) return 1;
            // Tag 0x0112 is Orientation, a SHORT whose value sits in the entry.
            if (U16(bytes, entry, little) == 0x0112)
            {
                var value = U16(bytes, entry + 8, little);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        return 1;
    }

    /// <summary>Where the TIFF header of a JPEG's APP1 or a WebP's EXIF chunk starts; -1 when there is none.</summary>
    private static long TiffStart(ReadOnlySpan<byte> bytes)
    {
        if (At(bytes, 0) == 0xff && At(bytes, 1) == 0xd8)
        {
            for (long offset = 2; offset + 4 <= bytes.Length;)
            {
                if (At(bytes, offset) != 0xff) return -1;
                var marker = At(bytes, offset + 1);
                if (marker == 0xff)
                {
                    offset++;
                    continue;
                }
                if (marker == 0xe1 && IsExifHeader(bytes, offset + 4)) return offset + 10;
                offset += 2 + ((At(bytes, offset + 2) << 8) | At(bytes, offset + 3));
            }
            return -1;
        }
        if (Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WEBP"))
        {
            for (long offset = 12; offset + 8 <= bytes.Length;)
            {
                var size = (uint)(At(bytes, offset + 4) | (At(bytes, offset + 5) << 8) | (At(bytes, offset + 6) << 16) | (At(bytes, offset + 7) << 24));
                // Some WebP files prefix the TIFF header with "Exif\0\0".
                if (Ascii(bytes, offset, "EXIF")) return IsExifHeader(bytes, offset + 8) ? offset + 14 : offset + 8;
                // RIFF chunks are padded to an even size.
                offset += 8 + size + (size % 2);
            }
        }
        return -1;
    }

    private static bool IsExifHeader(ReadOnlySpan<byte> bytes, long offset) =>
        Ascii(bytes, offset, "Exif") && At(bytes, offset + 4) == 0 && At(bytes, offset + 5) == 0;

    private static bool Ascii(ReadOnlySpan<byte> bytes, long offset, string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (offset + index >= bytes.Length || bytes[(int)(offset + index)] != text[index]) return false;
        return true;
    }

    private static int U16(ReadOnlySpan<byte> bytes, long at, bool little) =>
        little ? At(bytes, at) | (At(bytes, at + 1) << 8) : (At(bytes, at) << 8) | At(bytes, at + 1);

    private static long U32(ReadOnlySpan<byte> bytes, long at, bool little) => little
        ? (uint)(At(bytes, at) | (At(bytes, at + 1) << 8) | (At(bytes, at + 2) << 16) | (At(bytes, at + 3) << 24))
        : (uint)((At(bytes, at) << 24) | (At(bytes, at + 1) << 16) | (At(bytes, at + 2) << 8) | At(bytes, at + 3));

    private static int At(ReadOnlySpan<byte> bytes, long position) => position >= 0 && position < bytes.Length ? bytes[(int)position] : 0;
}
