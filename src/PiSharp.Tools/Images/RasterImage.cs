using System.Buffers.Binary;
using System.IO.Compression;

namespace PiSharp.Tools.Images;

/// <summary>An owned 8-bit RGBA raster, the pixel model Photon's PhotonImage uses.</summary>
internal sealed class RasterImage
{
    /// <summary>Decoded rasters are bounded; a larger image is treated as undecodable.</summary>
    internal const long MaximumPixels = 40_000_000;
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RasterImage(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || (long)width * height > MaximumPixels || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid raster.");
        Width = width; Height = height; Pixels = pixels;
    }

    public void FlipHorizontal()
    {
        for (var y = 0; y < Height; y++)
            for (int left = 0, right = Width - 1; left < right; left++, right--)
                for (var c = 0; c < 4; c++)
                    (Pixels[(y * Width + left) * 4 + c], Pixels[(y * Width + right) * 4 + c]) = (Pixels[(y * Width + right) * 4 + c], Pixels[(y * Width + left) * 4 + c]);
    }

    public void FlipVertical()
    {
        var row = new byte[Width * 4];
        for (int top = 0, bottom = Height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(Pixels, top * Width * 4, row, 0, row.Length);
            Buffer.BlockCopy(Pixels, bottom * Width * 4, Pixels, top * Width * 4, row.Length);
            Buffer.BlockCopy(row, 0, Pixels, bottom * Width * 4, row.Length);
        }
    }

    /// <summary>Separable Lanczos3 resampling (Photon's SamplingFilter.Lanczos3 through the image crate):
    /// vertical pass, then horizontal pass, on straight (non-premultiplied) RGBA.</summary>
    public RasterImage Resize(int width, int height)
    {
        if (width == Width && height == Height) return new(width, height, (byte[])Pixels.Clone());
        var vertical = Sample(Pixels, Width, Height, height, vertical: true);
        var horizontal = Sample(vertical, Width, height, width, vertical: false);
        var result = new byte[horizontal.Length];
        for (var index = 0; index < result.Length; index++) result[index] = (byte)Math.Clamp(MathF.Round(horizontal[index]), 0, 255);
        return new(width, height, result);
    }

    private static float[] Sample<T>(T[] source, int width, int height, int target, bool vertical) where T : struct, System.Numerics.INumberBase<T>
    {
        var length = vertical ? height : width; var other = vertical ? width : height;
        var output = new float[(vertical ? (long)width * target : (long)target * height) * 4];
        var ratio = (float)length / target;
        var scale = Math.Max(1f, ratio);
        var support = 3f * scale;
        var weights = new float[(int)Math.Ceiling(support * 2) + 2];
        for (var outIndex = 0; outIndex < target; outIndex++)
        {
            var center = (outIndex + 0.5f) * ratio;
            var left = Math.Max(0, (int)MathF.Floor(center - support));
            var right = Math.Min(length, (int)MathF.Ceiling(center + support));
            if (right <= left) right = Math.Min(length, left + 1);
            var count = right - left; var sum = 0f;
            if (weights.Length < count) weights = new float[count];
            for (var i = 0; i < count; i++) { var w = Lanczos3((left + i - center + 0.5f) / scale); weights[i] = w; sum += w; }
            if (sum != 0) for (var i = 0; i < count; i++) weights[i] /= sum;
            for (var o = 0; o < other; o++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var i = 0; i < count; i++)
                {
                    var p = (vertical ? (long)(left + i) * width + o : (long)o * width + left + i) * 4; var w = weights[i];
                    r += float.CreateTruncating(source[p]) * w; g += float.CreateTruncating(source[p + 1]) * w;
                    b += float.CreateTruncating(source[p + 2]) * w; a += float.CreateTruncating(source[p + 3]) * w;
                }
                var q = (vertical ? (long)outIndex * width + o : (long)o * target + outIndex) * 4;
                output[q] = r; output[q + 1] = g; output[q + 2] = b; output[q + 3] = a;
            }
        }
        return output;
    }

    private static float Lanczos3(float x)
    {
        if (x == 0) return 1;
        if (x is <= -3 or >= 3) return 0;
        var px = MathF.PI * x;
        return 3 * MathF.Sin(px) * MathF.Sin(px / 3) / (px * px);
    }
}

/// <summary>Bounded, dependency-free PNG and BMP decoders and an RGBA PNG encoder.</summary>
internal static class RasterCodec
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static RasterImage? Decode(ReadOnlySpan<byte> bytes, string mimeType)
    {
        try
        {
            return mimeType switch
            {
                "image/png" => DecodePng(bytes),
                "image/bmp" => DecodeBmp(bytes),
                _ => null
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException or IOException)
        { return null; }
    }

    // ---------------------------------------------------------------- PNG decode

    private static RasterImage? DecodePng(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(PngSignature)) return null;
        var offset = 8; int width = 0, height = 0, depth = 0, color = -1, interlace = 0;
        byte[]? palette = null, transparency = null; using var data = new MemoryStream(); var seenHeader = false;
        while (true)
        {
            if (offset + 12 > bytes.Length) return null;
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > int.MaxValue || offset + 12L + length > bytes.Length) return null;
            var type = bytes.Slice(offset + 4, 4); var body = bytes.Slice(offset + 8, (int)length);
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 8 + (int)length)..]) != Crc32(bytes.Slice(offset + 4, 4 + (int)length))) return null;
            offset += 12 + (int)length;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (seenHeader || body.Length != 13) return null;
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body)); height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                depth = body[8]; color = body[9]; interlace = body[12];
                if (body[10] != 0 || body[11] != 0 || interlace > 1 || width < 1 || height < 1 || (long)width * height > RasterImage.MaximumPixels ||
                    !(color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 3 => depth is 1 or 2 or 4 or 8, 2 or 4 or 6 => depth is 8 or 16, _ => false })) return null;
                seenHeader = true;
            }
            else if (!seenHeader) return null;
            else if (type.SequenceEqual("PLTE"u8)) { if (body.Length % 3 != 0 || body.Length == 0) return null; palette = body.ToArray(); }
            else if (type.SequenceEqual("tRNS"u8)) transparency = body.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) data.Write(body);
            else if (type.SequenceEqual("IEND"u8)) break;
        }
        if (color == 3 && palette is null) return null;
        var channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * depth; var bpp = Math.Max(1, bitsPerPixel / 8);
        data.Position = 0;
        using var inflater = new ZLibStream(data, CompressionMode.Decompress);
        var pixels = new byte[(long)width * height * 4];
        if (interlace == 0) DecodePass(inflater, 0, 0, 1, 1);
        else
        {
            ReadOnlySpan<int> starts = [0, 4, 0, 2, 0, 1, 0], startsY = [0, 0, 4, 0, 2, 0, 1], steps = [8, 8, 4, 4, 2, 2, 1], stepsY = [8, 8, 8, 4, 4, 2, 2];
            for (var pass = 0; pass < 7; pass++) DecodePass(inflater, starts[pass], startsY[pass], steps[pass], stepsY[pass]);
        }
        return new(width, height, pixels);

        void DecodePass(Stream input, int x0, int y0, int dx, int dy)
        {
            var passWidth = width <= x0 ? 0 : (width - x0 + dx - 1) / dx;
            var passHeight = height <= y0 ? 0 : (height - y0 + dy - 1) / dy;
            if (passWidth == 0 || passHeight == 0) return;
            var stride = checked((int)(((long)passWidth * bitsPerPixel + 7) / 8));
            var previous = new byte[stride]; var current = new byte[stride];
            for (var row = 0; row < passHeight; row++)
            {
                var filter = input.ReadByte();
                if (filter < 0) throw new InvalidDataException("Truncated PNG data.");
                input.ReadExactly(current);
                Unfilter(filter, current, previous, bpp);
                var y = y0 + row * dy;
                for (var column = 0; column < passWidth; column++) Store(current, column, (x0 + column * dx + (long)y * width) * 4);
                (previous, current) = (current, previous);
            }
        }

        void Store(byte[] line, int index, long target)
        {
            int Sample(int channel)
            {
                if (depth == 16) { var p = (index * channels + channel) * 2; return line[p] << 8 | line[p + 1]; }
                if (depth == 8) return line[index * channels + channel];
                var bit = (index * channels + channel) * depth;
                return line[bit >> 3] >> (8 - depth - (bit & 7)) & ((1 << depth) - 1);
            }
            byte Eight(int value) => depth switch
            {
                16 => (byte)((value * 255 + 32767) / 65535),
                8 => (byte)value,
                _ => (byte)(value * 255 / ((1 << depth) - 1))
            };
            byte r, g, b, a = 255;
            switch (color)
            {
                case 0:
                    { var v = Sample(0); r = g = b = Eight(v); if (transparency is { Length: >= 2 } && v == (transparency[0] << 8 | transparency[1])) a = 0; break; }
                case 2:
                    {
                        int rv = Sample(0), gv = Sample(1), bv = Sample(2);
                        r = Eight(rv); g = Eight(gv); b = Eight(bv);
                        if (transparency is { Length: >= 6 } && rv == (transparency[0] << 8 | transparency[1]) &&
                            gv == (transparency[2] << 8 | transparency[3]) && bv == (transparency[4] << 8 | transparency[5])) a = 0;
                        break;
                    }
                case 3:
                    {
                        var v = Sample(0);
                        if (v * 3 + 2 >= palette!.Length) throw new InvalidDataException("Palette index out of range.");
                        r = palette[v * 3]; g = palette[v * 3 + 1]; b = palette[v * 3 + 2];
                        if (transparency is not null && v < transparency.Length) a = transparency[v];
                        break;
                    }
                case 4: r = g = b = Eight(Sample(0)); a = Eight(Sample(1)); break;
                default: r = Eight(Sample(0)); g = Eight(Sample(1)); b = Eight(Sample(2)); a = Eight(Sample(3)); break;
            }
            pixels[target] = r; pixels[target + 1] = g; pixels[target + 2] = b; pixels[target + 3] = a;
        }
    }

    private static void Unfilter(int filter, byte[] current, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0: return;
            case 1: for (var i = bpp; i < current.Length; i++) current[i] += current[i - bpp]; return;
            case 2: for (var i = 0; i < current.Length; i++) current[i] += previous[i]; return;
            case 3:
                for (var i = 0; i < current.Length; i++) current[i] += (byte)(((i >= bpp ? current[i - bpp] : 0) + previous[i]) >> 1);
                return;
            case 4:
                for (var i = 0; i < current.Length; i++)
                    current[i] += Paeth(i >= bpp ? current[i - bpp] : (byte)0, previous[i], i >= bpp ? previous[i - bpp] : (byte)0);
                return;
            default: throw new InvalidDataException("Unknown PNG filter.");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ---------------------------------------------------------------- BMP decode

    private static RasterImage? DecodeBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || !bytes.StartsWith("BM"u8)) return null;
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var header = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        int width, height, bits, compression = 0, paletteEntrySize; uint colors = 0;
        uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;
        if (header == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]); height = BinaryPrimitives.ReadInt16LittleEndian(bytes[20..]);
            bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[24..]); paletteEntrySize = 3;
        }
        else if (header is >= 40 and <= 124 && bytes.Length >= 14 + 40)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]); height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
            bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]); compression = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[30..]);
            colors = BinaryPrimitives.ReadUInt32LittleEndian(bytes[46..]); paletteEntrySize = 4;
            if (compression == 3 || compression == 6)
            {
                // BI_BITFIELDS masks follow a 40-byte header, or live inside V2+ headers.
                if (bytes.Length < 14 + 40 + 12) return null;
                redMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[54..]); greenMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[58..]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[62..]);
                if (header >= 56 || compression == 6) alphaMask = bytes.Length >= 70 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[66..]) : 0;
            }
            else if (compression != 0) return null; // RLE, JPEG and PNG payloads are not decoded natively.
        }
        else return null;
        var topDown = height < 0; height = Math.Abs(height);
        if (width < 1 || height < 1 || (long)width * height > RasterImage.MaximumPixels || bits is not (1 or 4 or 8 or 16 or 24 or 32)) return null;
        if (bits == 16 && compression == 0) { redMask = 0x7c00; greenMask = 0x03e0; blueMask = 0x001f; }
        byte[]? palette = null;
        if (bits <= 8)
        {
            var count = colors == 0 || colors > 1u << bits ? 1 << bits : (int)colors;
            var start = 14 + header + (compression == 3 && header == 40 ? 12 : 0);
            if (start + (long)count * paletteEntrySize > bytes.Length) return null;
            palette = new byte[count * 3];
            for (var i = 0; i < count; i++)
            {
                var p = start + i * paletteEntrySize;
                palette[i * 3] = bytes[p + 2]; palette[i * 3 + 1] = bytes[p + 1]; palette[i * 3 + 2] = bytes[p];
            }
        }
        var stride = (int)(((long)width * bits + 31) / 32 * 4);
        if (pixelOffset + (long)stride * height > bytes.Length) return null;
        var pixels = new byte[(long)width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var line = bytes.Slice((int)pixelOffset + row * stride, stride);
            var y = topDown ? row : height - 1 - row;
            for (var x = 0; x < width; x++)
            {
                var t = ((long)y * width + x) * 4; byte r, g, b, a = 255;
                switch (bits)
                {
                    case 24: b = line[x * 3]; g = line[x * 3 + 1]; r = line[x * 3 + 2]; break;
                    case 32 when compression == 0: b = line[x * 4]; g = line[x * 4 + 1]; r = line[x * 4 + 2]; break;
                    case 16 or 32:
                        {
                            var v = bits == 16 ? BinaryPrimitives.ReadUInt16LittleEndian(line[(x * 2)..]) : BinaryPrimitives.ReadUInt32LittleEndian(line[(x * 4)..]);
                            r = Mask(v, redMask); g = Mask(v, greenMask); b = Mask(v, blueMask);
                            if (alphaMask != 0) a = Mask(v, alphaMask);
                            break;
                        }
                    default:
                        {
                            var bit = x * bits; var index = line[bit >> 3] >> (8 - bits - (bit & 7)) & ((1 << bits) - 1);
                            if (index * 3 + 2 >= palette!.Length) return null;
                            r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2];
                            break;
                        }
                }
                pixels[t] = r; pixels[t + 1] = g; pixels[t + 2] = b; pixels[t + 3] = a;
            }
        }
        return new(width, height, pixels);
    }

    private static byte Mask(uint value, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        return (byte)(((value & mask) >> shift) * 255 / max);
    }

    // ---------------------------------------------------------------- PNG encode

    /// <summary>An 8-bit RGBA PNG (color type 6), as Photon's get_bytes produces, with per-row adaptive filters.</summary>
    public static byte[] EncodePng(RasterImage image)
    {
        var stride = image.Width * 4;
        using var raw = new MemoryStream();
        using (var deflate = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var previous = new byte[stride]; var candidate = new byte[stride]; var best = new byte[stride]; var current = new byte[stride];
            for (var y = 0; y < image.Height; y++)
            {
                Buffer.BlockCopy(image.Pixels, y * stride, current, 0, stride);
                var bestFilter = 0; long bestScore = long.MaxValue;
                for (var filter = 0; filter < 5; filter++)
                {
                    long score = 0;
                    for (var i = 0; i < stride; i++)
                    {
                        byte left = i >= 4 ? current[i - 4] : (byte)0, up = previous[i], corner = i >= 4 ? previous[i - 4] : (byte)0;
                        var value = (byte)(current[i] - filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) >> 1, _ => Paeth(left, up, corner) });
                        candidate[i] = value; score += Math.Abs((int)(sbyte)value);
                    }
                    if (score < bestScore) { bestScore = score; bestFilter = filter; (best, candidate) = (candidate, best); }
                }
                deflate.WriteByte((byte)bestFilter); deflate.Write(best);
                (previous, current) = (current, previous);
            }
        }
        using var output = new MemoryStream();
        output.Write(PngSignature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width); BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
        Chunk(output, "IHDR"u8, header); Chunk(output, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length)); Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)body.Length); output.Write(number);
        var typed = new byte[4 + body.Length]; type.CopyTo(typed); body.CopyTo(typed.AsSpan(4));
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed)); output.Write(number);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc ^ 0xffffffffu;
    }
}
