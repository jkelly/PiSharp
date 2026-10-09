// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): cases ported from packages/coding-agent/test/tools.test.ts,
// test/image-process.test.ts, test/image-processing.test.ts, test/tool-result-images.test.ts and
// test/builtin-tool-strict-mode.test.ts. Authored native expectations, not upstream captures.
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools;
using PiSharp.Tools.Files;
using PiSharp.Tools.Images;
using PiSharp.Tools.Processes;

internal static class ToolParityTests
{
    // Upstream test fixtures (image-processing.test.ts, tools.test.ts).
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACAQMAAABIeJ9nAAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAAGUExURf8AAP///0EdNBEAAAABYktHRAH/Ai3eAAAAB3RJTUUH6gEOADM5Ddoh/wAAAAxJREFUCNdjYGBgAAAABAABJzQnCgAAACV0RVh0ZGF0ZTpjcmVhdGUAMjAyNi0wMS0xNFQwMDo1MTo1NyswMDowMOnKzHgAAAAldEVYdGRhdGU6bW9kaWZ5ADIwMjYtMDEtMTRUMDA6NTE6NTcrMDA6MDCYl3TEAAAAKHRFWHRkYXRlOnRpbWVzdGFtcAAyMDI2LTAxLTE0VDAwOjUxOjU3KzAwOjAwz4JVGwAAAABJRU5ErkJggg==";
    private const string TinyJpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAGCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AD3VTB3/2Q==";
    private const string TinyJpeg2x1 = "/9j/4AAQSkZJRgABAgAAAQABAAD/wAARCAABAAIDAREAAhEBAxEB/9sAQwADAgIDAgIDAwMDBAMDBAUIBQUEBAUKBwcGCAwKDAwLCgsLDQ4SEA0OEQ4LCxAWEBETFBUVFQwPFxgWFBgSFBUU/9sAQwEDBAQFBAUJBQUJFA0LDRQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD4H8Q/8h/Uv+vmX/0M1/o1wJ/ySWU/9g1D/wBNRMOM/wDkp8z/AOv9b/05I//Z";
    private const string MediumPng100 = "iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAAAAABVicqIAAAAAmJLR0QA/4ePzL8AAAAHdElNRQfqAQ4AMzkN2iH/AAAAP0lEQVRo3u3NQQEAAAQEMASXXYrz2gqst/Lm4ZBIJBKJRCKRSCQSiUQikUgkEolEIpFIJBKJRCKRSCQSiSTsAP1cAUZeKtreAAAAJXRFWHRkYXRlOmNyZWF0ZQAyMDI2LTAxLTE0VDAwOjUxOjU3KzAwOjAw6crMeAAAACV0RVh0ZGF0ZTptb2RpZnkAMjAyNi0wMS0xNFQwMDo1MTo1NyswMDowMJiXdMQAAAAodEVYdGRhdGU6dGltZXN0YW1wADIwMjYtMDEtMTRUMDA6NTE6NTcrMDA6MDDPglUbAAAAAElFTkSuQmCC";
    private const string LargePng200 = "iVBORw0KGgoAAAANSUhEUgAAAMgAAADIEAIAAAByquWKAAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAAGYktHRP///////wlY99wAAAAHdElNRQfqAQ4AMzqU03BFAAAEEElEQVR42u3dvWqUURSF4XXMRAyIhSAWQvwnWtjaWusFx9bW1kKD/wELESxEiBjDjJdwysOC57mCXW32+zGcGbtdACpsdi+T8Xz1GABzm91xMl6sHgNgblzcT/Y+rh4DYG6cv0/2H64eA2BOEgI1fHQHaoyzbXIwVo8BMCcJgRqSEKgxft9Lrn5aPQbA3Ph1klw7Wj0GwJwkBGpYWECN8fMiub63egyAORcWUMPCAmqMH3eTG59XjwEwN76/S24+Wj0GwJwkBGpYWECN8e1fcmuzegyAORcWUMPCAmqM09vJ4dfVYwDMjS9vkzuPV48BMCcJgRqeSAZqjA/nyYP91WMAzElCoIYkBGqMk8Pk6HT1GABzm+3L5JIkBApIQqCGj+5AjfHmb/Lk8uoxAOYkIVBDEgI1LCygxmZ7nFyShEABFxZQw8ICaozXf5KnV1aPATDnwgJqWFhADQsLqOFnDUANFxZQw8ICaoxXZ8mzg9VjAMy5sIAaFhZQw3tYQA1/QgHUkIRADUkI1JCEQA1JCNSQhEANFxZQwzcsoIYkBGpIQqCGJARqSEKghiQEalhYQA1/QgHUcGEBNSwsoIYkBGq4sIAaFhZQw8ICaviGBdRwYQE1LCyghiQEariwgBoWFlDDe1hADS+OAjUkIVBDEgI1JCFQQxICNSQhUMOFBdTwDQuoIQmBGpIQqCEJgRqSEKghCYEaFhZQw4ujQA0XFlDDwgJqSEKghgsLqGFhATUsLKCGb1hADRcWUMPCAmpIQqCGCwuoYWEBNbyHBdTw4ihQQxICNSQhUEMSAjUkIVBDEgI1XFhADd+wgBqSEKghCYEakhCoIQmBGpIQqGFhATW8OArUcGEBNSwsoIYkBGq4sIAaFhZQw8ICaviGBdRwYQE1LCyghiQEariwgBoWFlDDe1hADS+OAjUkIVBDEgI1JCFQQxICNSQhUMOFBdTwDQuoIQmBGpIQqCEJgRqSEKghCYEa49lB8ups9RgAc14cBWpIQqCGhQXUkIRADRcWUMPCAmqMp1eS139WjwEw5xsWUEMSAjUsLKCGJARquLCAGhYWUGM8uZy8+bt6DIA5L44CNSQhUMMTyUCNcXSanByuHgNgThICNSQhUGM82E8+nK8eA2DOzxqAGpIQqDHuPE6+vF09BsDcOPyanN5ePQbAnCQEavgdFlBj3Nok3/6tHgNgzoujQA1JCNQYNx8l39+tHgNgbtz4nPy4u3oMgDlJCNSwsIAa4/pe8vNi9RgAc37WANSQhECNce0o+XWyegyAuXH1U/L73uoxAOYkIVDDwgJqjIORnG1XjwEw508ogBqSEKgx9h8m5+9XjwEwN/Y+Jhf3V48BMCcJgRpjPE+2x6vHAJgbSbLbrR4DYO4/GqiSgXN+ksgAAAAldEVYdGRhdGU6Y3JlYXRlADIwMjYtMDEtMTRUMDA6NTE6NTcrMDA6MDDpysx4AAAAJXRFWHRkYXRlOm1vZGlmeQAyMDI2LTAxLTE0VDAwOjUxOjU3KzAwOjAwmJd0xAAAACh0RVh0ZGF0ZTp0aW1lc3RhbXAAMjAyNi0wMS0xNFQwMDo1MTo1NyswMDowMM+CVRsAAAAASUVORK5CYII=";

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("parity read text structured content and missing file ENOENT", ReadTextAndMissing),
        ("parity read detects image type from magic not extension for each format", ReadEachImageFormat),
        ("parity read BMP becomes a PNG attachment with conversion hint", ReadBmp),
        ("parity read image extension with text content stays text and non-vision note", ReadNonImageAndNonVision),
        ("parity image size limits resize omit and auto-resize off", ImageSizeLimits),
        ("parity image mime detection exif orientation and dimension notes", ImageDetection),
        ("parity tool result image normalization", ToolResultImages),
        ("parity shell discovery on Windows", WindowsShellDiscovery),
        ("parity shell discovery on Unix and shellPath normalization", UnixShellDiscovery),
        ("parity bash shellCommandPrefix argv and stdin transport", BashPrefixAndTransport),
        ("parity bash PI_* session environment spawn hook and missing cwd", BashSessionEnvironment),
        ("parity bash stdin transport and prefix run in real processes", BashRealProcesses),
        ("parity user_bash interception selects result operations or local", UserBashInterception),
        ("parity tool settings from settings.json", Settings),
        ("parity declarations prompt snippets and guidelines", DeclarationsAndPrompts),
    ];

    private static async Task ReadTextAndMissing()
    {
        using var temp = new Temp();
        var tools = new ReadWriteTools(temp.Root, temp.Root);
        await File.WriteAllTextAsync(temp.File("test.txt"), "Hello, world!\nLine 2\nLine 3");
        var result = await Read(tools, "test.txt");
        Equal("Hello, world!\nLine 2\nLine 3", Text(result)); Equal(false, result.HasProperty("details"));
        Equal("\"Hello, world!\\nLine 2\\nLine 3\"", result.StructuredContent!.ToString());
        // Pi normalizeOptionalNulls: strict schemas make optional properties nullable, and a null is the property's absence.
        var nulls = await tools.CreateInvoker(new Allow()).ExecuteAsync(Invocation("read", new { path = "test.txt", offset = (int?)null, limit = (int?)null }), default);
        Equal("Hello, world!\nLine 2\nLine 3", Text(nulls));
        var listing = await new LsTool(temp.Root, temp.Root).CreateInvoker(new Allow()).ExecuteAsync(Invocation("ls", new { path = (string?)null, limit = (int?)null }), default);
        Equal("test.txt", listing.Content.Single().Text);
        var bash = new BashTool(new Runner(), new BashToolOptions(Environment.ProcessPath!, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Root));
        var noTimeout = await bash.PrepareAsync(Invocation("bash", new { command = "ls", timeout = (double?)null }), default);
        Check(!noTimeout.Arguments.Value.TryGetProperty("timeout", out _), "Null timeout was not treated as absent.");
        var missing = await Read(tools, "nonexistent.txt");
        Check(missing.IsError, "Missing file read succeeded.");
        Equal($"ENOENT: no such file or directory, access '{temp.File("nonexistent.txt")}'", Text(missing));
        Directory.CreateDirectory(temp.File("folder"));
        var folder = await Read(tools, "folder"); Check(folder.IsError, "Directory read succeeded.");
        Check(Text(folder).StartsWith("EISDIR", StringComparison.Ordinal) || Text(folder).StartsWith("EACCES", StringComparison.Ordinal), "Directory read error differs.");
    }

    private static async Task ReadEachImageFormat()
    {
        using var temp = new Temp(); var tools = new ReadWriteTools(temp.Root, temp.Root);
        var formats = new (string Name, byte[] Bytes, string Mime)[]
        {
            ("image.txt", Convert.FromBase64String(Png1x1), "image/png"),
            ("photo.dat", Convert.FromBase64String(TinyJpeg), "image/jpeg"),
            ("anim.bin", Gif(3, 2), "image/gif"),
            ("lossless", WebpLossless(5, 4), "image/webp"),
            ("extended.webp.txt", WebpExtended(7, 6), "image/webp"),
        };
        foreach (var (name, bytes, mime) in formats)
        {
            await File.WriteAllBytesAsync(temp.File(name), bytes);
            var result = await Read(tools, name); Check(!result.IsError, name + " read failed.");
            var blocks = result.ContentValue.Value;
            Equal(2, blocks.GetArrayLength());
            Equal($"Read image file [{mime}]", blocks[0].GetProperty("text").GetString());
            Equal("image", blocks[1].GetProperty("type").GetString()); Equal(mime, blocks[1].GetProperty("mimeType").GetString());
            // Within the limits the source returns the original bytes unchanged.
            Equal(Convert.ToBase64String(bytes), blocks[1].GetProperty("data").GetString());
            Equal(JsonSerializer.Serialize(new { type = "image", data = Convert.ToBase64String(bytes), mimeType = mime, note = $"Read image file [{mime}]" }, Relaxed),
                result.StructuredContent!.ToString());
        }
    }

    private static async Task ReadBmp()
    {
        using var temp = new Temp(); var tools = new ReadWriteTools(temp.Root, temp.Root);
        await File.WriteAllBytesAsync(temp.File("image.bmp"), Bmp1x1Red());
        var result = await Read(tools, "image.bmp");
        var blocks = result.ContentValue.Value;
        Equal("Read image file [image/png]\n[Image converted from image/bmp to image/png.]", blocks[0].GetProperty("text").GetString());
        Equal("image/png", blocks[1].GetProperty("mimeType").GetString());
        var png = Convert.FromBase64String(blocks[1].GetProperty("data").GetString()!);
        Equal(0x89, png[0]); Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16))); Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
        // The decoded pixel is red (BGR 00 00 FF): convert back through the processor without resizing.
        var off = ImageProcessor.Process(Bmp1x1Red(), "image/bmp", autoResizeImages: false);
        Check(off.Ok && off.MimeType == "image/png" && off.Hints.SequenceEqual(["[Image converted from image/bmp to image/png.]"]), "BMP conversion without resize differs.");
        // RLE-compressed BMP has no native decoder: the source conversion failure message.
        var rle = Bmp1x1Red(); BinaryPrimitives.WriteUInt32LittleEndian(rle.AsSpan(30), 1); BinaryPrimitives.WriteUInt16LittleEndian(rle.AsSpan(28), 8);
        var failed = ImageProcessor.Process(rle, "image/bmp");
        Check(!failed.Ok && failed.Message == "[Image omitted: could not be converted to a supported inline image format.]", "Undecodable BMP was not omitted.");
    }

    private static async Task ReadNonImageAndNonVision()
    {
        using var temp = new Temp();
        await File.WriteAllTextAsync(temp.File("not-an-image.png"), "definitely not a png");
        var text = await Read(new ReadWriteTools(temp.Root, temp.Root), "not-an-image.png");
        Equal("definitely not a png", Text(text));
        await File.WriteAllBytesAsync(temp.File("pixel.png"), Convert.FromBase64String(Png1x1));
        var blind = new ReadWriteTools(temp.Root, temp.Root, options: new() { CurrentModelSupportsImages = () => false });
        var result = await Read(blind, "pixel.png");
        Equal("Read image file [image/png]\n[Current model does not support images. The image will be omitted from this request.]",
            result.ContentValue.Value[0].GetProperty("text").GetString());
        var noModel = new ReadWriteTools(temp.Root, temp.Root, options: new() { CurrentModelSupportsImages = () => null });
        Equal("Read image file [image/png]", (await Read(noModel, "pixel.png")).ContentValue.Value[0].GetProperty("text").GetString());
    }

    private static async Task ImageSizeLimits()
    {
        // resizeImage: within limits keeps the caller's bytes; dimension and byte limits resize; impossible limits fail.
        var tiny = Convert.FromBase64String(TinyPng);
        var kept = ImageProcessor.Resize(tiny, "image/png", new(100, 100, 1024 * 1024))!;
        Check(!kept.WasResized && kept.Data == TinyPng && kept.OriginalWidth == 2 && kept.Height == 2, "Within-limit image changed.");
        var medium = ImageProcessor.Resize(Convert.FromBase64String(MediumPng100), "image/png", new(50, 50, 1024 * 1024))!;
        Check(medium.WasResized && medium.OriginalWidth == 100 && medium.Width == 50 && medium.Height == 50 && medium.MimeType == "image/png", "Dimension resize differs.");
        Equal("[Image: original 100x100, displayed at 50x50. Multiply coordinates by 2.00 to map to original image.]", ImageProcessor.FormatDimensionNote(medium));
        var resizedPng = Convert.FromBase64String(medium.Data);
        Equal(50u, BinaryPrimitives.ReadUInt32BigEndian(resizedPng.AsSpan(16)));
        var large = ImageProcessor.Resize(Convert.FromBase64String(LargePng200), "image/png", new(2000, 2000, (int)(LargePng200.Length * 0.9)))!;
        Check(large.Data.Length < LargePng200.Length, "Byte-limit resize did not shrink the payload.");
        Check(ImageProcessor.Resize(Convert.FromBase64String(LargePng200), "image/png", new(2000, 2000, 1)) is null, "Impossible byte limit was met.");
        Check(ImageProcessor.Resize(Convert.FromBase64String(TinyJpeg), "image/jpeg", new(100, 100, 1024 * 1024)) is { WasResized: false, OriginalWidth: 2 }, "JPEG passthrough differs.");
        // Native deviation: no JPEG/GIF/WebP decoder, so an oversized JPEG is omitted as when Photon cannot resize it.
        Check(ImageProcessor.Resize(Convert.FromBase64String(TinyJpeg), "image/jpeg", new(1, 1, 1024 * 1024)) is null, "Oversized JPEG resized without a codec.");

        using var temp = new Temp();
        await File.WriteAllBytesAsync(temp.File("medium.png"), Convert.FromBase64String(MediumPng100));
        var limited = new ReadWriteTools(temp.Root, temp.Root, options: new() { ImageResizeOptions = new(50, 50) });
        var read = await Read(limited, "medium.png");
        Equal("Read image file [image/png]\n[Image: original 100x100, displayed at 50x50. Multiply coordinates by 2.00 to map to original image.]",
            read.ContentValue.Value[0].GetProperty("text").GetString());
        var impossible = new ReadWriteTools(temp.Root, temp.Root, options: new() { ImageResizeOptions = new(MaxBytes: 1) });
        var omitted = await Read(impossible, "medium.png");
        Equal(1, omitted.ContentValue.Value.GetArrayLength());
        Equal("Read image file [image/png]\n[Image omitted: could not be resized below the inline image size limit.]", Text(omitted));
        Equal("\"Read image file [image/png]\\n[Image omitted: could not be resized below the inline image size limit.]\"", omitted.StructuredContent!.ToString());
        // autoResizeImages false: the original bytes, even beyond the limits.
        var raw = new ReadWriteTools(temp.Root, temp.Root, options: new() { AutoResizeImages = false, ImageResizeOptions = new(MaxBytes: 1) });
        Equal(MediumPng100, (await Read(raw, "medium.png")).ContentValue.Value[1].GetProperty("data").GetString());
    }

    private static Task ImageDetection()
    {
        foreach (var signature in new[] { "GIF87a", "GIF89a" }) Equal("image/gif", ImageMime.DetectSupportedImageMimeType(Encoding.ASCII.GetBytes(signature)));
        Equal("image/bmp", ImageMime.DetectSupportedImageMimeType(Bmp1x1Red()));
        Equal<string?>(null, ImageMime.DetectSupportedImageMimeType([0xff, 0xd8, 0xff, 0xf7]));
        Equal("image/jpeg", ImageMime.DetectSupportedImageMimeType([0xff, 0xd8, 0xff, 0xe0]));
        // An APNG (acTL before IDAT) is not a supported still image.
        var png = Convert.FromBase64String(Png1x1).ToList();
        png.InsertRange(33, [0, 0, 0, 8, (byte)'a', (byte)'c', (byte)'T', (byte)'L', 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
        Equal<string?>(null, ImageMime.DetectSupportedImageMimeType(png.ToArray()));
        Equal("image/png", ImageMime.DetectSupportedImageMimeType(Convert.FromBase64String(Png1x1)));
        // Source getExifOrientation follows APP1 segments past an XMP packet; orientation 6 transposes 2x1 to 1x2.
        var oriented = JpegWithXmpBeforeOrientation();
        Equal(6, ExifOrientation.Read(oriented));
        Equal<(int, int)?>((1, 2), BuiltinImageCodec.Instance.ProbeDimensions(oriented, "image/jpeg"));
        Equal<(int, int)?>((2, 1), BuiltinImageCodec.Instance.ProbeDimensions(Convert.FromBase64String(TinyJpeg2x1), "image/jpeg"));
        Equal<string?>(null, ImageProcessor.FormatDimensionNote(new("", "image/png", 100, 100, 100, 100, false)));
        Equal("[Image: original 2000x1000, displayed at 1000x500. Multiply coordinates by 2.00 to map to original image.]",
            ImageProcessor.FormatDimensionNote(new("", "image/png", 2000, 1000, 1000, 500, true)));
        return Task.CompletedTask;
    }

    private static Task ToolResultImages()
    {
        var unchanged = JsonData.Parse(JsonSerializer.Serialize(new object[] { new { type = "text", text = "x" }, new { type = "image", data = Png1x1, mimeType = "image/png" } }));
        Check(ReferenceEquals(unchanged, ImageProcessor.NormalizeToolResultImages(unchanged)), "Unchanged content was rewritten.");
        var textOnly = JsonData.Parse("""[{"type":"text","text":"x"}]""");
        Check(ReferenceEquals(textOnly, ImageProcessor.NormalizeToolResultImages(textOnly)), "Text-only content was rewritten.");
        var oversized = JsonData.Parse(JsonSerializer.Serialize(new object[] { new { type = "image", data = MediumPng100, mimeType = "image/png" } }));
        var normalized = ImageProcessor.NormalizeToolResultImages(oversized, resizeOptions: new(50, 50)).Value;
        Equal(2, normalized.GetArrayLength()); Equal("image", normalized[0].GetProperty("type").GetString());
        Equal("[Image: original 100x100, displayed at 50x50. Multiply coordinates by 2.00 to map to original image.]", normalized[1].GetProperty("text").GetString());
        // A block the backend cannot process is kept as it is.
        var failing = JsonData.Parse(JsonSerializer.Serialize(new object[] { new { type = "image", data = TinyJpeg, mimeType = "image/jpeg" } }));
        Check(ReferenceEquals(failing, ImageProcessor.NormalizeToolResultImages(failing, resizeOptions: new(1, 1))), "Failed image was dropped or rewritten.");
        return Task.CompletedTask;
    }

    private static Task WindowsShellDiscovery()
    {
        ShellHost Host(Dictionary<string, string?> env, params string[] files) =>
            new(true, name => env.TryGetValue(name, out var value) ? value : null, path => files.Contains(path, StringComparer.OrdinalIgnoreCase), @"C:\Users\pi");
        var env = new Dictionary<string, string?> { ["ProgramFiles"] = @"C:\Program Files", ["ProgramFiles(x86)"] = @"C:\Program Files (x86)", ["PATH"] = @"C:\cygwin\bin;C:\Windows\System32" };
        Equal(new ShellConfiguration(@"C:\Program Files\Git\bin\bash.exe", ["-c"]).ToString(),
            ShellDiscovery.Resolve(null, Host(env, @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe")).ToString());
        Equal(@"C:\Program Files (x86)\Git\bin\bash.exe", ShellDiscovery.Resolve(null, Host(env, @"C:\Program Files (x86)\Git\bin\bash.exe")).Shell);
        Equal(@"C:\cygwin\bin\bash.exe", ShellDiscovery.Resolve(null, Host(env, @"C:\cygwin\bin\bash.exe", @"C:\Windows\System32\bash.exe")).Shell);
        // Legacy inbox WSL bash receives the command over stdin with -s.
        var wsl = ShellDiscovery.Resolve(null, Host(env, @"C:\Windows\System32\bash.exe"));
        Check(wsl.Shell == @"C:\Windows\System32\bash.exe" && wsl.Arguments.SequenceEqual(["-s"]) && wsl.CommandTransport == ShellCommandTransport.Stdin, "WSL transport differs.");
        Check(wsl.CommandArguments("echo hi").SequenceEqual(["-s"]), "Stdin transport put the command in argv.");
        Check(ShellDiscovery.IsLegacyWslBashPath("c:/windows/sysnative/BASH.EXE") && !ShellDiscovery.IsLegacyWslBashPath(@"C:\Program Files\Git\bin\bash.exe"), "WSL path detection differs.");
        try { ShellDiscovery.Resolve(null, Host(env)); throw new InvalidOperationException("Missing bash resolved."); }
        catch (ShellDiscoveryException error)
        {
            Equal("No bash shell found. Options:\n  1. Install Git for Windows: https://git-scm.com/download/win\n  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n  3. Set shellPath in settings.json\n\nSearched Git Bash in:\n  C:\\Program Files\\Git\\bin\\bash.exe\n  C:\\Program Files (x86)\\Git\\bin\\bash.exe", error.Message);
        }
        Equal(@"D:\tools\bash.exe", ShellDiscovery.Resolve(@"D:\tools\bash.exe", Host(env, @"D:\tools\bash.exe")).Shell);
        try { ShellDiscovery.Resolve("/custom/bash", Host(env)); throw new InvalidOperationException("Missing custom shell resolved."); }
        catch (ShellDiscoveryException error) { Equal("Custom shell path not found: /custom/bash", error.Message); }
        return Task.CompletedTask;
    }

    private static Task UnixShellDiscovery()
    {
        ShellHost Host(string? path, params string[] files) =>
            new(false, name => name == "PATH" ? path : null, files.Contains, "/home/pi");
        Equal("/bin/bash", ShellDiscovery.Resolve(null, Host("/usr/local/bin:/usr/bin", "/bin/bash", "/usr/local/bin/bash")).Shell);
        var onPath = ShellDiscovery.Resolve(null, Host("/data/termux/bin:/usr/bin", "/data/termux/bin/bash"));
        Check(onPath.Shell == "/data/termux/bin/bash" && onPath.Arguments.SequenceEqual(["-c"]), "PATH bash differs.");
        Equal("/usr/bin/sh", ShellDiscovery.Resolve(null, Host("/usr/bin", "/usr/bin/sh")).Shell);
        Equal("/bin/sh", ShellDiscovery.Resolve(null, Host(null)).Shell);
        // Settings shellPath: leading ~ expansion, and MSYS/Cygwin/WSL drive paths on Windows.
        Equal(Path.Join("/home/pi", "bin/bash"), ShellDiscovery.NormalizeShellPath("~/bin/bash", Host(null)));
        var windows = new ShellHost(true, _ => null, _ => false, @"C:\Users\pi");
        Equal(@"C:\Program Files\Git\bin\bash.exe", ShellDiscovery.NormalizeShellPath("/c/Program Files/Git/bin/bash.exe", windows));
        Equal(@"D:\msys\bash.exe", ShellDiscovery.NormalizeShellPath("/mnt/d/msys/bash.exe", windows));
        // getShellEnv: Pi's bin directory first on PATH, once, keeping the PATH key spelling.
        var shellEnv = ShellDiscovery.ShellEnvironment(new Dictionary<string, string> { ["Path"] = @"C:\a;C:\b" }, @"C:\Users\pi\.pi\agent\bin", windows: true);
        Equal(@"C:\Users\pi\.pi\agent\bin;C:\a;C:\b", shellEnv["Path"]);
        Equal(shellEnv["Path"], ShellDiscovery.ShellEnvironment(shellEnv, @"C:\Users\pi\.pi\agent\bin", windows: true)["Path"]);
        Equal(Path.Join("/home/pi", ".pi", "agent", "bin"), ShellDiscovery.BinDirectory(Host(null)));
        return Task.CompletedTask;
    }

    private static async Task BashPrefixAndTransport()
    {
        using var temp = new Temp(); var runner = new Runner();
        var options = new BashToolOptions(Environment.ProcessPath!, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Root)
        { CommandPrefix = "export TEST_VAR=hello" };
        var tool = new BashTool(runner, options);
        var prepared = await tool.PrepareAsync(Invocation("bash", new { command = "echo $TEST_VAR" }), default);
        Check(prepared.CommandArguments.SequenceEqual(["-c", "export TEST_VAR=hello\necho $TEST_VAR"]), "Prefix was not prepended with a newline.");
        Equal("echo $TEST_VAR", prepared.Arguments.Value.GetProperty("command").GetString());
        Check(await tool.ValidateAsync(prepared, default), "Prefixed action failed validation.");
        Check(!await tool.ValidateAsync(prepared with { CommandArguments = ["-c", "echo $TEST_VAR"] }, default), "Unprefixed argv validated.");
        var plain = new BashTool(runner, options with { CommandPrefix = "" });
        Check((await plain.PrepareAsync(Invocation("bash", new { command = "echo no-prefix" }), default)).CommandArguments.SequenceEqual(["-c", "echo no-prefix"]), "Empty prefix changed the command.");
        // Stdin transport: argv carries only the shell arguments; the command is the standard input.
        var stdin = new BashTool(runner, options with { ShellArguments = ["-s"], CommandTransport = ShellCommandTransport.Stdin });
        var piped = await stdin.PrepareAsync(Invocation("bash", new { command = "pwd" }), default);
        Check(piped.CommandArguments.SequenceEqual(["-s"]), "Stdin transport kept the command in argv.");
        Equal("export TEST_VAR=hello\npwd", piped.Arguments.Value.GetProperty("standardInput").GetString());
        var result = await stdin.CreateInvoker(new Allow()).ExecuteAsync(Invocation("bash", new { command = "pwd" }), default);
        Check(!result.IsError, "Stdin action failed.");
        Equal("export TEST_VAR=hello\npwd", Encoding.UTF8.GetString(runner.Requests.Single().StandardInput!));
        // Source stdin.end(command): a NUL byte is standard-input data, not a spawn argument, so the command runs.
        var nul = await stdin.CreateInvoker(new Allow()).ExecuteAsync(Invocation("bash", new { command = "echo a\0b" }), default);
        Check(!nul.IsError, "Stdin NUL command failed: " + Text(nul));
        Equal("export TEST_VAR=hello\necho a\0b", Encoding.UTF8.GetString(runner.Requests[^1].StandardInput!));
        Check(runner.Requests[^1].Arguments.SequenceEqual(["-s"]), "NUL reached argv.");
    }

    private static async Task BashSessionEnvironment()
    {
        using var temp = new Temp(); var runner = new Runner();
        var inherited = ImmutableDictionary<string, string>.Empty.Add("KEEP", "1").Add("PI_SESSION_ID", "stale").Add("PI_MODEL", "stale-model");
        BashSessionEnvironment? session = new("session-1", temp.File("s.jsonl"), "anthropic", "claude", "medium");
        var options = new BashToolOptions(Environment.ProcessPath!, temp.Root, inherited, temp.Root) { SessionEnvironment = () => session };
        var tool = new BashTool(runner, options);
        var env = (await tool.PrepareAsync(Invocation("bash", new { command = "env" }), default)).Environment;
        Equal("1", env["KEEP"]); Equal("session-1", env["PI_SESSION_ID"]); Equal(temp.File("s.jsonl"), env["PI_SESSION_FILE"]);
        Equal("anthropic", env["PI_PROVIDER"]); Equal("claude", env["PI_MODEL"]); Equal("medium", env["PI_REASONING_LEVEL"]);
        Check(tool.PromptGuidelines.SequenceEqual(["You can inspect PI_* environment variables for current model and session details."]), "PI_* guideline missing.");
        // No session file or model: those variables are unset, and inherited PI_* values never leak.
        session = new("session-2");
        env = (await tool.PrepareAsync(Invocation("bash", new { command = "env" }), default)).Environment;
        Check(env["PI_SESSION_ID"] == "session-2" && !env.ContainsKey("PI_SESSION_FILE") && !env.ContainsKey("PI_MODEL") && !env.ContainsKey("PI_PROVIDER"), "Partial session environment differs.");
        var hidden = new BashTool(runner, options with { ExposeSessionEnvironment = false });
        env = (await hidden.PrepareAsync(Invocation("bash", new { command = "env" }), default)).Environment;
        Check(!env.Keys.Any(key => key.StartsWith("PI_", StringComparison.Ordinal)) && env["KEEP"] == "1" && hidden.PromptGuidelines.IsEmpty, "Hidden session environment leaked.");
        // spawnHook rewrites the command, cwd and environment of the final action.
        var other = temp.File("other"); Directory.CreateDirectory(other);
        var hooked = new BashTool(runner, options with
        {
            SpawnHook = context => context with { Command = "wrapped " + context.Command, WorkingDirectory = other, Environment = context.Environment.SetItem("HOOK", "yes") }
        });
        var action = await hooked.PrepareAsync(Invocation("bash", new { command = "ls" }), default);
        Check(action.CommandArguments.SequenceEqual(["-c", "wrapped ls"]) && action.WorkingDirectory == other && action.Environment["HOOK"] == "yes", "Spawn hook result was not used.");
        Check(await hooked.ValidateAsync(action, default), "Hooked action failed validation.");
        // Source createLocalShellOperations: a missing working directory is reported before spawning.
        var gone = temp.File("gone"); Directory.CreateDirectory(gone);
        var transient = new BashTool(runner, options with { WorkingDirectory = gone });
        var prepared = await transient.PrepareAsync(Invocation("bash", new { command = "echo test" }), default);
        Directory.Delete(gone);
        var failed = await transient.ExecuteAsync(prepared, default);
        Check(failed.IsError, "Missing cwd ran.");
        Equal($"Working directory does not exist: {gone}\nCannot execute bash commands.", failed.Content.Single().Text);
        Equal(0, runner.Requests.Count);
    }

    private static async Task BashRealProcesses()
    {
        // Pi spills any amount of output to its file, so hosts may lift the raw capture cap past the old 64 MiB bound.
        _ = new NativeProcessRunner(new ProcessRunnerOptions(MaximumRawBytes: int.MaxValue));
        if (!OperatingSystem.IsWindows()) return; // The native process backend is Windows-only.
        using var temp = new Temp();
        // Mirrors the source stdin test: the "shell" is a child that echoes its standard input.
        var (executable, arguments) = NativeProcessRunnerTests.ChildCommand(["echo-stdin"]);
        var command = "name='World'; echo \"Hello, ${name}!\"; count=3; for i in $(seq 1 ${count}); do echo \"Iteration ${i} of ${count}\"; done";
        var echo = new BashTool(new NativeProcessRunner(), new BashToolOptions(executable, temp.Root, NativeProcessRunnerTests.ParentEnvironment(), temp.Root)
        { ShellArguments = arguments, CommandTransport = ShellCommandTransport.Stdin });
        var echoed = await echo.CreateInvoker(new Allow()).ExecuteAsync(Invocation("bash", new { command }), default);
        Check(!echoed.IsError, "Stdin child failed: " + echoed.Content.Single().Text);
        Equal(command, echoed.Content.Single().Text);
        Equal(0, echoed.StructuredContent!.Value.GetProperty("exit_code").GetInt32());
        // shellCommandPrefix through a real bash when one is discoverable.
        ShellConfiguration shell;
        try { shell = ShellDiscovery.Resolve(); } catch (ShellDiscoveryException) { return; }
        if (shell.CommandTransport != ShellCommandTransport.Argv) return;
        var environment = NativeProcessRunnerTests.ParentEnvironment();
        async Task<string> Run(string? prefix, string text)
        {
            var tool = new BashTool(new NativeProcessRunner(), BashToolOptions.FromShell(shell, temp.Root, environment, temp.Root) with { CommandPrefix = prefix });
            var result = await tool.CreateInvoker(new Allow()).ExecuteAsync(Invocation("bash", new { command = text }), default);
            Check(!result.IsError, "Real bash failed: " + result.Content.Single().Text);
            return result.Content.Single().Text.Trim();
        }
        Equal("hello", await Run("export TEST_VAR=hello", "echo $TEST_VAR"));
        Equal("prefix-output\ncommand-output", await Run("echo prefix-output", "echo command-output"));
        Equal("no-prefix", await Run(null, "echo no-prefix"));
    }

    private static async Task UserBashInterception()
    {
        var calls = new List<string>();
        UserBashHandler Pass(string name) => (_, _) => { calls.Add(name); return ValueTask.FromResult<UserBashInterception?>(null); };
        var result = new ShellCommandResult("handled", 0, false, false, null);
        UserBashHandler Handle(string name) => (_, _) => { calls.Add(name); return ValueTask.FromResult<UserBashInterception?>(new() { Result = result }); };
        var userEvent = new UserBashEvent("ls", true, @"C:\work");
        Equal<UserBashInterception?>(null, await UserBashInterceptors.EmitAsync([Pass("a"), Pass("b")], userEvent, default));
        var handled = await UserBashInterceptors.EmitAsync([Pass("c"), Handle("d"), Handle("e")], userEvent, default);
        Check(ReferenceEquals(result, handled!.Result) && calls.SequenceEqual(["a", "b", "c", "d"]), "First non-undefined handler did not win.");
        foreach (var invalid in new[] { new UserBashInterception(), new UserBashInterception { Result = result, Operations = new Ops([]) } })
        {
            try { await UserBashInterceptors.EmitAsync([(_, _) => ValueTask.FromResult<UserBashInterception?>(invalid)], userEvent, default); throw new InvalidOperationException("Invalid result admitted."); }
            catch (InvalidOperationException error) when (error.Message == UserBashInterceptors.InvalidResultMessage) { }
        }
        // Custom operations run through the executor's own spill and sanitization.
        using var temp = new Temp();
        var local = new ShellCommandExecutor(new Ops([]), temp.Root);
        var custom = new Ops([Encoding.UTF8.GetBytes("\u001b[31mremote\u001b[0m\r\n")]);
        var ran = await local.WithOperations(custom).ExecuteAsync("prefix\nls", temp.Root, null, default);
        Equal("remote\n", ran.Output); Equal("prefix\nls", custom.Commands.Single());
    }

    private static Task Settings()
    {
        var host = new ShellHost(false, _ => null, _ => true, "/home/pi");
        Equal(BuiltinToolSettings.Default, BuiltinToolSettings.FromSettings(null));
        Equal(new BuiltinToolSettings(), BuiltinToolSettings.FromSettings(JsonData.Parse("{}")));
        var all = BuiltinToolSettings.FromSettings(JsonData.Parse("""{"shellPath":"~/bin/bash","shellCommandPrefix":"shopt -s expand_aliases","images":{"autoResize":false,"blockImages":true}}"""), host);
        Equal(new BuiltinToolSettings(Path.Join("/home/pi", "bin/bash"), "shopt -s expand_aliases", false, true), all);
        // Falsy values mean unset (source `shellPath ? ... : shellPath`, `commandPrefix ? ...`); `?? default` keeps any non-null value.
        var falsy = BuiltinToolSettings.FromSettings(JsonData.Parse("""{"shellPath":"","shellCommandPrefix":"","images":{"autoResize":null,"blockImages":0}}"""), host);
        Equal(new BuiltinToolSettings(), falsy);
        Equal(false, BuiltinToolSettings.FromSettings(JsonData.Parse("""{"images":{"autoResize":0}}""")).AutoResizeImages);
        Equal(true, BuiltinToolSettings.FromSettings(JsonData.Parse("""{"images":{"blockImages":"yes"}}""")).BlockImages);
        return Task.CompletedTask;
    }

    private static Task DeclarationsAndPrompts()
    {
        using var temp = new Temp();
        var edit = new EditTool(temp.Root, temp.Root, new PiSharp.Agent.Tools.FileMutationQueue((path, _) => ValueTask.FromResult(path)));
        Equal("""{"name":"edit","description":"Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.","parameters":{"type":"object","required":["path","edits"],"properties":{"path":{"type":"string","description":"Path to the file to edit (relative or absolute)"},"edits":{"type":"array","items":{"type":"object","required":["oldText","newText"],"properties":{"oldText":{"type":"string","description":"Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call."},"newText":{"type":"string","description":"Replacement text for this targeted edit."}}},"description":"One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead."}}},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}""",
            edit.Declaration.ToString());
        Equal("""{"name":"ls","description":"List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to 500 entries or 50KB (whichever is hit first).","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Directory to list (default: current directory)"},"limit":{"type":"number","description":"Maximum number of entries to return (default: 500)"}}}}""",
            new LsTool(temp.Root, temp.Root).Declaration.ToString());
        // Source strict mode: read, bash, edit and write prefer strict sampling; grep, find and ls do not.
        var catalog = new BuiltinToolCatalog(temp.Root, temp.Root);
        foreach (var tool in catalog.Registered)
            Equal(tool.Name is "read" or "edit" or "write", tool.Declaration.Value.TryGetProperty("constrainedSampling", out _));
        Equal("Read file contents", BuiltinToolPrompts.Snippets["read"]);
        Equal("Execute bash commands (ls, grep, find, etc.)", BuiltinToolPrompts.Snippets["bash"]);
        Equal("Search file contents for patterns (respects .gitignore)", BuiltinToolPrompts.Snippets["grep"]);
        Equal("Find files by glob pattern (respects .gitignore)", BuiltinToolPrompts.Snippets["find"]);
        Equal("List directory contents", BuiltinToolPrompts.Snippets["ls"]);
        Equal("Create or overwrite files", BuiltinToolPrompts.Snippets["write"]);
        Check(BuiltinToolPrompts.Guidelines["read"].SequenceEqual(["Use read to examine files instead of cat or sed."]), "Read guideline differs.");
        Check(BuiltinToolPrompts.Guidelines["write"].SequenceEqual(["Use write only for new files or complete rewrites."]), "Write guideline differs.");
        Equal(4, BuiltinToolPrompts.Guidelines["edit"].Length);
        Check(BuiltinToolPrompts.Guidelines["grep"].IsEmpty && BuiltinToolPrompts.Guidelines["ls"].IsEmpty, "Unexpected search guidelines.");
        Check(BuiltinToolPrompts.DefaultToolNames.SequenceEqual(["read", "bash", "edit", "write"]), "Default tools differ.");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- fixtures

    private static byte[] Bmp1x1Red()
    {
        var buffer = new byte[58];
        "BM"u8.CopyTo(buffer); BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2), 58); BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(14), 40); BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(22), 1); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(28), 24); BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(34), 4);
        buffer[56] = 0xff; return buffer;
    }
    private static byte[] Gif(int width, int height)
    {
        var gif = new List<byte>("GIF89a"u8.ToArray()); gif.AddRange(BitConverter.GetBytes((ushort)width)); gif.AddRange(BitConverter.GetBytes((ushort)height));
        gif.AddRange([0, 0, 0, 0x3b]); return gif.ToArray();
    }
    private static byte[] WebpLossless(int width, int height)
    {
        var bytes = new byte[30]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 22); "WEBPVP8L"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10); bytes[20] = 0x2f;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)(width - 1) | (uint)(height - 1) << 14); return bytes;
    }
    private static byte[] WebpExtended(int width, int height)
    {
        var bytes = new byte[30]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 22); "WEBPVP8X"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        bytes[24] = (byte)(width - 1); bytes[27] = (byte)(height - 1); return bytes;
    }
    private static byte[] JpegWithXmpBeforeOrientation()
    {
        static byte[] App1(byte[] payload)
        {
            var segment = new byte[payload.Length + 4]; segment[0] = 0xff; segment[1] = 0xe1;
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2)); payload.CopyTo(segment, 4); return segment;
        }
        var jpeg = Convert.FromBase64String(TinyJpeg2x1);
        var xmp = App1(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
        var orientation6 = App1([.. "Exif\0\0"u8, .. Convert.FromHexString("49492a0008000000010012010300010000000600000000000000")]);
        return [.. jpeg[..2], .. xmp, .. orientation6, .. jpeg[2..]];
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static async Task<ToolResult> Read(ReadWriteTools tools, string path) =>
        await tools.CreateInvoker(new Allow()).ExecuteAsync(Invocation("read", new { path }), default);
    private static string Text(ToolResult result) => result.ContentValue.Value[0].GetProperty("text").GetString()!;
    private static ToolInvocation Invocation(string name, object arguments)
    {
        var call = new ToolCallContent(name + "-call", name, JsonData.Parse(JsonSerializer.Serialize(arguments)));
        return new(new("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private sealed class Allow : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private sealed class Runner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var snapshot = new ProcessOutputSnapshot("", PiSharp.Agent.Tools.ToolOutputTruncator.Tail(""), 0, 0, null);
            return ValueTask.FromResult(new ProcessRunResult(ProcessRunStatus.Exited, 0, 1, true, true, true, snapshot, new("", false), 0, []));
        }
    }
    private sealed class Ops(byte[][] chunks) : IShellOperations
    {
        public List<string> Commands { get; } = [];
        public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData, CancellationToken token)
        {
            Commands.Add(command);
            foreach (var chunk in chunks) await onData(chunk);
            return 0;
        }
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-tool-parity-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Root);
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}> but was <{actual}>.");
    }
}
