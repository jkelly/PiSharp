// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): cases ported from packages/coding-agent/test/image-processing.test.ts,
// test/image-process.test.ts and test/tools.test.ts (read images), with real JPEG, GIF, WebP, PNG and BMP files decoded
// through the SkiaSharp codec that stands in for Photon. Authored native expectations, not upstream captures.
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Images;
using PiSharp.Tools.Skia;
using SkiaSharp;

internal static class SkiaCodecTests
{
    private static readonly SkiaImageCodec Codec = SkiaImageCodec.Instance;
    private static readonly int[] Qualities = [80, 85, 70, 55, 40];

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("skia decodes real png jpeg gif webp and bmp files and passes them through within limits", Formats),
        ("skia dimension limits at and over the edge resize with Lanczos3 and the dimension note", DimensionLimits),
        ("skia byte limit at and over the edge and the PNG then JPEG quality-step choice", ByteLimitsAndQualitySteps),
        ("skia shrinks by 0.75 per step until a candidate fits and fails at 1x1", ShrinkSteps),
        ("skia applies EXIF orientation after XMP before resizing and converting", ExifOrientationCases),
        ("skia decodes the first GIF frame and converts BMP to PNG", FirstFrameAndBmp),
        ("skia rejects undecodable bodies as Photon does", Undecodable),
        ("skia read tool reads each real format and a 4.5MB-class JPEG", ReadToolFormats),
    ];

    private static Task Formats()
    {
        foreach (var (bytes, mime) in new[] { (Png(Gradient(37, 23)), "image/png"), (Jpeg(Gradient(41, 19), 90), "image/jpeg"),
            (Gif(Gradient(29, 17)), "image/gif"), (Webp(Gradient(31, 13)), "image/webp") })
        {
            Equal(mime, ImageMime.DetectSupportedImageMimeType(bytes));
            var size = Codec.ProbeDimensions(bytes, mime) ?? throw new InvalidOperationException(mime + " did not decode.");
            var processed = ImageProcessor.Process(bytes, mime, codec: Codec);
            Check(processed.Ok && processed.MimeType == mime && processed.Data == Convert.ToBase64String(bytes) && processed.Hints.IsEmpty,
                mime + " within the limits changed.");
            Check(size.Width > 0 && size.Height > 0, "Empty dimensions.");
        }
        Equal<(int, int)?>((41, 19), Codec.ProbeDimensions(Jpeg(Gradient(41, 19), 90), "image/jpeg"));
        Equal<(int, int)?>((29, 17), Codec.ProbeDimensions(Gif(Gradient(29, 17)), "image/gif"));
        Equal<(int, int)?>((31, 13), Codec.ProbeDimensions(Webp(Gradient(31, 13)), "image/webp"));
        return Task.CompletedTask;
    }

    private static Task DimensionLimits()
    {
        // At the 2000x2000 limit (and below the byte limit) the source returns the original bytes.
        var atLimit = Jpeg(Gradient(2000, 2000), 60);
        var kept = ImageProcessor.Resize(atLimit, "image/jpeg", codec: Codec)!;
        Check(!kept.WasResized && kept.Data == Convert.ToBase64String(atLimit) && kept.Width == 2000, "At-limit image was changed.");
        // One pixel over: round(10 * 2000 / 2001) = 10, the PNG candidate fits first.
        var over = ImageProcessor.Resize(Jpeg(Gradient(2001, 10), 90), "image/jpeg", codec: Codec)!;
        Check(over.WasResized && over.Width == 2000 && over.Height == 10 && over.MimeType == "image/png", "One-pixel-over resize differs.");
        Equal("[Image: original 2001x10, displayed at 2000x10. Multiply coordinates by 1.00 to map to original image.]", ImageProcessor.FormatDimensionNote(over));
        var tall = ImageProcessor.Resize(Webp(Gradient(300, 900)), "image/webp", new(MaxWidth: 400, MaxHeight: 300), Codec)!;
        Check(tall.WasResized && tall.Width == 100 && tall.Height == 300, "Height limit resize differs.");
        var decoded = Decode(Convert.FromBase64String(tall.Data));
        Check(decoded.Width == 100 && decoded.Height == 300, "Encoded dimensions differ from the reported ones.");
        // processImage hints: the conversion hint precedes the dimension note.
        var bmp = ImageProcessor.Process(Bmp(Gradient(64, 32)), "image/bmp", resizeOptions: new(MaxWidth: 32, MaxHeight: 32), codec: Codec);
        Check(bmp.Ok && bmp.Hints.SequenceEqual(["[Image converted from image/bmp to image/png.]",
            "[Image: original 64x32, displayed at 32x16. Multiply coordinates by 2.00 to map to original image.]"]), "Hint order differs.");
        return Task.CompletedTask;
    }

    private static Task ByteLimitsAndQualitySteps()
    {
        var noise = Jpeg(Noise(300, 300, 7), 95);
        var base64 = (noise.Length + 2) / 3 * 4;
        // Source: inputBase64Size < maxBytes passes through; equal is over the limit.
        Check(ImageProcessor.Resize(noise, "image/jpeg", new(MaxBytes: base64 + 1), Codec) is { WasResized: false }, "Below the byte limit was resized.");
        var atBytes = ImageProcessor.Resize(noise, "image/jpeg", new(MaxBytes: base64), Codec)!;
        Check(atBytes.WasResized && atBytes.Width == 300 && atBytes.MimeType == "image/jpeg", "At the byte limit was not re-encoded at full size.");
        // Candidates are PNG, then JPEG at [80, 85, 70, 55, 40]; the first below maxBytes wins.
        var candidates = Codec.Encode(noise, "image/jpeg", 300, 300, Qualities)!.Value;
        Equal(6, candidates.Length);
        Check(candidates[0].MimeType == "image/png" && candidates.Skip(1).All(item => item.MimeType == "image/jpeg"), "Candidate order differs.");
        int Length(int index) => Convert.ToBase64String(candidates[index].Bytes).Length;
        Check(Length(0) > Length(2) && Length(2) > Length(1) && Length(1) > Length(3) && Length(3) > Length(4) && Length(4) > Length(5),
            "Noise candidates are not ordered by size as the step test needs.");
        foreach (var (index, quality) in new[] { (3, 70), (4, 55), (5, 40), (1, 80) })
        {
            var limit = Length(index) + 1;
            var chosen = ImageProcessor.Resize(noise, "image/jpeg", new(MaxBytes: limit), Codec)!;
            Check(chosen.MimeType == "image/jpeg" && chosen.Width == 300 && chosen.Data == Convert.ToBase64String(candidates[index].Bytes),
                $"Quality {quality} was not the first fitting candidate.");
        }
        // A configured jpegQuality leads the steps; duplicates are skipped.
        var custom = Codec.Encode(noise, "image/jpeg", 300, 300, [95, 85, 70, 55, 40])!.Value;
        var first = ImageProcessor.Resize(noise, "image/jpeg", new(MaxBytes: Convert.ToBase64String(custom[1].Bytes).Length + 1, JpegQuality: 95), Codec)!;
        Equal(Convert.ToBase64String(custom[1].Bytes), first.Data);
        // A flat image fits as PNG, which is tried first.
        var flat = ImageProcessor.Resize(Jpeg(Gradient(2400, 100), 90), "image/jpeg", codec: Codec)!;
        Equal("image/png", flat.MimeType);
        return Task.CompletedTask;
    }

    private static Task ShrinkSteps()
    {
        var noise = Png(Noise(200, 200, 3));
        var smallest = Codec.Encode(noise, "image/png", 150, 150, Qualities)!.Value.Min(item => Convert.ToBase64String(item.Bytes).Length);
        var fullSmallest = Codec.Encode(noise, "image/png", 200, 200, Qualities)!.Value.Min(item => Convert.ToBase64String(item.Bytes).Length);
        Check(smallest < fullSmallest, "Smaller dimensions did not shrink the candidates.");
        var stepped = ImageProcessor.Resize(noise, "image/png", new(MaxBytes: smallest + 1), Codec)!;
        Check(stepped.WasResized && stepped.Width == 150 && stepped.Height == 150 && stepped.OriginalWidth == 200, "0.75 step differs.");
        Check(ImageProcessor.Resize(noise, "image/png", new(MaxBytes: 4), Codec) is null, "An impossible budget produced an image.");
        var omitted = ImageProcessor.Process(noise, "image/png", resizeOptions: new(MaxBytes: 4), codec: Codec);
        Check(!omitted.Ok && omitted.Message == ImageProcessor.ResizeFailedMessage, "Impossible budget was not omitted.");
        return Task.CompletedTask;
    }

    private static Task ExifOrientationCases()
    {
        // A real 4x2 JPEG, left half red and right half blue, with XMP before the orientation-6 APP1 segment.
        var pixels = new byte[4 * 2 * 4];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
            {
                var offset = (y * 4 + x) * 4;
                pixels[offset] = (byte)(x < 2 ? 255 : 0); pixels[offset + 2] = (byte)(x < 2 ? 0 : 255); pixels[offset + 3] = 255;
            }
        var plain = Jpeg(new(4, 2, pixels), 100);
        var oriented = WithOrientation(plain, 6);
        Equal(6, ExifOrientation.Read(oriented));
        Equal<(int, int)?>((2, 4), Codec.ProbeDimensions(oriented, "image/jpeg"));
        Equal<(int, int)?>((4, 2), Codec.ProbeDimensions(plain, "image/jpeg"));
        // Orientation 6 rotates clockwise: the left (red) columns become the top rows.
        var png = Codec.Encode(oriented, "image/jpeg", 2, 4, [80])!.Value[0].Bytes;
        var bitmap = Decode(png);
        Check(bitmap.Width == 2 && bitmap.Height == 4, "Oriented output has the wrong shape.");
        var top = bitmap.GetPixel(0, 0); var bottom = bitmap.GetPixel(1, 3);
        Check(top.Red > 200 && top.Blue < 60 && bottom.Blue > 200 && bottom.Red < 60, "Orientation 6 was not applied clockwise.");
        // Over the limits the oriented dimensions drive the resize and the note.
        var resized = ImageProcessor.Resize(oriented, "image/jpeg", new(MaxWidth: 1, MaxHeight: 2), Codec)!;
        Check(resized.OriginalWidth == 2 && resized.OriginalHeight == 4 && resized.Width == 1 && resized.Height == 2, "Oriented resize differs.");
        foreach (var (orientation, width, height) in new[] { (3, 4, 2), (5, 2, 4), (8, 2, 4), (2, 4, 2) })
            Equal<(int, int)?>((width, height), Codec.ProbeDimensions(WithOrientation(plain, orientation), "image/jpeg"));
        return Task.CompletedTask;
    }

    private static Task FirstFrameAndBmp()
    {
        var red = Solid(6, 4, 255, 0, 0); var blue = Solid(6, 4, 0, 0, 255);
        var animated = Gif(red, blue);
        var frame = Decode(Codec.Encode(animated, "image/gif", 6, 4, [80])!.Value[0].Bytes);
        var pixel = frame.GetPixel(3, 2);
        Check(pixel.Red > 200 && pixel.Blue < 60, "The first GIF frame was not decoded.");
        var bmp = Bmp(Solid(3, 2, 0, 255, 0));
        var processed = ImageProcessor.Process(bmp, "image/bmp", autoResizeImages: false, codec: Codec);
        Check(processed.Ok && processed.MimeType == "image/png" && processed.Hints.SequenceEqual(["[Image converted from image/bmp to image/png.]"]),
            "BMP conversion differs.");
        var converted = Decode(Convert.FromBase64String(processed.Data!));
        Check(converted.Width == 3 && converted.Height == 2 && converted.GetPixel(2, 1).Green > 200, "BMP pixels changed.");
        return Task.CompletedTask;
    }

    private static Task Undecodable()
    {
        var jpeg = Jpeg(Noise(64, 64, 9), 90);
        var truncated = jpeg[..(jpeg.Length / 2)];
        Equal<(int, int)?>(null, Codec.ProbeDimensions(truncated, "image/jpeg"));
        var result = ImageProcessor.Process(truncated, "image/jpeg", codec: Codec);
        Check(!result.Ok && result.Message == ImageProcessor.ResizeFailedMessage, "A truncated JPEG was sent.");
        Equal<(int, int)?>(null, Codec.ProbeDimensions("RIFFxxxxWEBPjunk"u8.ToArray(), "image/webp"));
        Check(Codec.ConvertToPng("BM not really"u8, "image/bmp") is null, "Garbage BMP converted.");
        return Task.CompletedTask;
    }

    private static async Task ReadToolFormats()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-skia-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var tools = new ReadWriteTools(root, root, options: new() { ImageCodec = Codec });
            foreach (var (name, bytes, mime) in new[] { ("a.jpg.txt", Jpeg(Gradient(50, 40), 90), "image/jpeg"), ("b.gif", Gif(Gradient(20, 10)), "image/gif"),
                ("c.webp", Webp(Gradient(30, 30)), "image/webp"), ("d.png", Png(Gradient(12, 12)), "image/png") })
            {
                await File.WriteAllBytesAsync(Path.Combine(root, name), bytes);
                var result = await Read(tools, name);
                Equal($"Read image file [{mime}]", result.ContentValue.Value[0].GetProperty("text").GetString());
                Equal(Convert.ToBase64String(bytes), result.ContentValue.Value[1].GetProperty("data").GetString());
            }
            // A 4.5MB-class JPEG: under 2000x2000 and just under 4.5MB of base64 passes through whole; an oversized one is
            // resized below the budget and annotated.
            var large = LargeJpeg(1900, 1900, 4_400_000, 4_718_000);
            await File.WriteAllBytesAsync(Path.Combine(root, "large.jpg"), large);
            var whole = await Read(tools, "large.jpg");
            var data = whole.ContentValue.Value[1].GetProperty("data").GetString()!;
            Check(data == Convert.ToBase64String(large) && data.Length is > 4_400_000 and < ImageResizeOptions.DefaultMaxBytes, "The 4.5MB-class JPEG was not sent whole.");
            var huge = Jpeg(Blend(Noise(3000, 2400, 5), 300), 92);
            await File.WriteAllBytesAsync(Path.Combine(root, "huge.jpg"), huge);
            var resized = await Read(tools, "huge.jpg");
            Equal("Read image file [image/jpeg]\n[Image: original 3000x2400, displayed at 2000x1600. Multiply coordinates by 1.50 to map to original image.]",
                resized.ContentValue.Value[0].GetProperty("text").GetString());
            Check(resized.ContentValue.Value[1].GetProperty("data").GetString()!.Length < ImageResizeOptions.DefaultMaxBytes, "Resized image exceeds the budget.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------------------------------------------------------------- fixtures

    internal sealed record Raster(int Width, int Height, byte[] Pixels);

    internal static Raster Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 255 / Math.Max(1, width - 1)); pixels[offset + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[offset + 2] = 128; pixels[offset + 3] = 255;
            }
        return new(width, height, pixels);
    }
    internal static Raster Noise(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 4]; new Random(seed).NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        return new(width, height, pixels);
    }
    private static Raster Solid(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4) { pixels[index] = r; pixels[index + 1] = g; pixels[index + 2] = b; pixels[index + 3] = 255; }
        return new(width, height, pixels);
    }

    internal static byte[] Jpeg(Raster raster, int quality) => Encode(raster, pixmap => pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)));
    private static byte[] Png(Raster raster) => Encode(raster, pixmap => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)));
    private static byte[] Webp(Raster raster) => Encode(raster, pixmap => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80)));
    private static byte[] Encode(Raster raster, Func<SKPixmap, SKData?> encode)
    {
        var info = new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
        try { using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes); using var data = encode(pixmap) ?? throw new InvalidOperationException("Fixture encoding failed."); return data.ToArray(); }
        finally { handle.Free(); }
    }
    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("Encoded output did not decode.");

    /// <summary>Noise scaled around mid-grey by <paramref name="permille"/>/1000: lower amplitude compresses better.</summary>
    internal static Raster Blend(Raster noise, int permille)
    {
        var pixels = (byte[])noise.Pixels.Clone();
        for (var index = 0; index < pixels.Length; index++) if (index % 4 != 3) pixels[index] = (byte)(128 + (pixels[index] - 128) * permille / 1000);
        return new(noise.Width, noise.Height, pixels);
    }

    /// <summary>A JPEG of noise (quality 90 to 100) whose base64 length falls in [minimum, maximum), by bisecting the noise amplitude.</summary>
    internal static byte[] LargeJpeg(int width, int height, int minimum, int maximum)
    {
        var noise = Noise(width, height, 11);
        foreach (var quality in new[] { 90, 95, 98, 100 })
        {
            int low = 0, high = 1000;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var bytes = Jpeg(Blend(noise, middle), quality); var length = (bytes.Length + 2) / 3 * 4;
                if (length >= maximum) high = middle - 1;
                else if (length < minimum) low = middle + 1;
                else return bytes;
            }
        }
        throw new InvalidOperationException("No JPEG landed in the requested size window.");
    }

    /// <summary>Uncompressed-LZW GIF89a frames (8-bit colour cube palette, clear code every 250 pixels).</summary>
    private static byte[] Gif(params Raster[] frames)
    {
        var width = frames[0].Width; var height = frames[0].Height;
        var output = new List<byte>("GIF89a"u8.ToArray());
        output.AddRange(BitConverter.GetBytes((ushort)width)); output.AddRange(BitConverter.GetBytes((ushort)height));
        output.AddRange([0xf7, 0, 0]);
        for (var index = 0; index < 256; index++) output.AddRange([(byte)((index >> 5) * 255 / 7), (byte)((index >> 2 & 7) * 255 / 7), (byte)((index & 3) * 255 / 3)]);
        foreach (var frame in frames)
        {
            output.AddRange([0x21, 0xf9, 4, 0, 10, 0, 0, 0]);
            output.Add(0x2c); output.AddRange(new byte[4]);
            output.AddRange(BitConverter.GetBytes((ushort)width)); output.AddRange(BitConverter.GetBytes((ushort)height)); output.Add(0);
            output.Add(8);
            var codes = new List<int> { 256 };
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                if (pixel > 0 && pixel % 250 == 0) codes.Add(256);
                var p = pixel * 4; var px = frame.Pixels;
                codes.Add((px[p] * 7 / 255) << 5 | (px[p + 1] * 7 / 255) << 2 | px[p + 2] * 3 / 255);
            }
            codes.Add(257);
            var data = new List<byte>(); int buffer = 0, bits = 0;
            foreach (var code in codes)
            {
                buffer |= code << bits; bits += 9;
                while (bits >= 8) { data.Add((byte)buffer); buffer >>= 8; bits -= 8; }
            }
            if (bits > 0) data.Add((byte)buffer);
            for (var offset = 0; offset < data.Count; offset += 255)
            {
                var count = Math.Min(255, data.Count - offset);
                output.Add((byte)count); output.AddRange(data.GetRange(offset, count));
            }
            output.Add(0);
        }
        output.Add(0x3b);
        return output.ToArray();
    }

    /// <summary>A 24-bit bottom-up BMP.</summary>
    private static byte[] Bmp(Raster raster)
    {
        var stride = (raster.Width * 3 + 3) / 4 * 4; var size = 54 + stride * raster.Height; var bytes = new byte[size];
        "BM"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)size); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), raster.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), raster.Height); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        for (var y = 0; y < raster.Height; y++)
            for (var x = 0; x < raster.Width; x++)
            {
                var source = ((raster.Height - 1 - y) * raster.Width + x) * 4; var target = 54 + y * stride + x * 3;
                bytes[target] = raster.Pixels[source + 2]; bytes[target + 1] = raster.Pixels[source + 1]; bytes[target + 2] = raster.Pixels[source];
            }
        return bytes;
    }

    /// <summary>The JPEG with an XMP APP1 segment and then an EXIF APP1 orientation tag after SOI (image-processing.test.ts).</summary>
    private static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        static byte[] App1(byte[] payload)
        {
            var segment = new byte[payload.Length + 4]; segment[0] = 0xff; segment[1] = 0xe1;
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2)); payload.CopyTo(segment, 4); return segment;
        }
        var xmp = App1(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
        var tiff = Convert.FromHexString("49492a0008000000010012010300010000000600000000000000"); tiff[18] = (byte)orientation;
        var exif = App1([.. "Exif\0\0"u8, .. tiff]);
        return [.. jpeg[..2], .. xmp, .. exif, .. jpeg[2..]];
    }

    private static async Task<ToolResult> Read(ReadWriteTools tools, string path)
    {
        var call = new ToolCallContent("read-call", "read", JsonData.Parse(JsonSerializer.Serialize(new { path })));
        var invocation = new ToolInvocation(new("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        var result = await tools.CreateInvoker(new Allow()).ExecuteAsync(invocation, default);
        Check(!result.IsError, "Read failed: " + result.ContentValue);
        return result;
    }
    private sealed class Allow : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected <{expected}> but was <{actual}>.");
    }
}
