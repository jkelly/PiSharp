// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/tools-images.test.ts, as tools.images.<case>, on LocalExecutionEnv
// (upstream NodeExecutionEnv). Verbatim apart from these adaptations:
// - Photon is PiSharp.Tools.Skia's SkiaImageCodec (Images.cs in PiSharp.Durable.Tools/Images maps the two): the fixtures are built, and the
//   prepared images decoded, with SkiaSharp where upstream builds and decodes them with Photon; the processor is
//   ImageProcessors.CreateThreaded(SkiaImageCodec.Instance) where upstream's is createNodePhotonImages().
// - The api is ImageToolApi: the agent names model test/test, which api.Models resolves to a model taking `input` with `inputLimits.images
//   .resize` when given, as upstream's stand-in api does.
// - readEnv's wrapped openBinaryReader is a LocalExecutionEnv subclass wrapping the reader.
// - createNodePhotonImages "prepares in a worker thread, leaving the event loop free" measures the longest gap between timer ticks (inline
//   over 200 ms, threaded under 100 ms). Here a gated codec shows the same without timing: on the calling thread the work is done before
//   PrepareAsync returns; threaded, PrepareAsync returns while the codec is still held, on another thread, and resolves once it is released.
// - "starts a new worker after the idle one exited" waits until the worker has exited (bounded) instead of 100 ms, and also checks that the
//   next image started a new worker.
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using PiSharp.Durable.Tools.Images;
using PiSharp.Tools.Images;
using PiSharp.Tools.Skia;
using SkiaSharp;

/// <summary>Upstream's stand-in api of tools-images.test.ts: env, no-op output, and a model the agent names.</summary>
internal sealed class ImageToolApi(IExecutionEnv env, DurableModel model) : DelegatingToolExecutionApi(DispatchProxy.Create<IToolExecutionApi, ToolApiStub>())
{
    public override IExecutionEnv? Env => env;
    public override void Output(string chunk, ShellOutputSkip? skipped = null) { }
    public override void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null) { }
    public override void Diagnostic(ToolDiagnostic diagnostic) { }
    public override Task DetailsAsync(JsonValue value, DurableContext context) => Task.CompletedTask;
    public override Task<Agent> AgentAsync(DurableContext context) =>
        Task.FromResult(FakeToolApi.NoModelAgent with { Model = new ModelRef("test", "test") });
    public override IDurableModels Models => new OneModel(model);

    private sealed class OneModel(DurableModel model) : IDurableModels
    {
        public DurableModel? GetModel(string provider, string modelId) => model;
        public IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PiSharp.Contracts.AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages,
            DurableStreamOptions? options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PiSharp.Contracts.AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

/// <summary>Upstream's readEnv: each positional read of an opened file goes to <c>onRead</c> after it resolved; the largest so far is kept.</summary>
internal sealed class ObservedReadEnv(string cwd, Action<long, long>? onRead) : LocalExecutionEnv(cwd)
{
    private long largest;

    public long Largest => Interlocked.Read(ref largest);

    public override async Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context)
    {
        var opened = await base.OpenBinaryReaderAsync(path, options, context);
        return opened.IsOk ? new ObservedReader(opened.Value, this) : opened;
    }

    private sealed class ObservedReader(IBinaryReader inner, ObservedReadEnv env) : IBinaryReader
    {
        public Task<Result<FileMetadata, FileError>> InfoAsync(DurableContext context) => inner.InfoAsync(context);
        public Task<Result<LineScan, FileError>> ScanLinesAsync(long startLine, long? endLine, DurableContext context) =>
            inner.ScanLinesAsync(startLine, endLine, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);

        public async Task<Result<byte[], FileError>> ReadAsync(long offset, long length, DurableContext context)
        {
            long seen;
            do seen = Interlocked.Read(ref env.largest);
            while (length > seen && Interlocked.CompareExchange(ref env.largest, length, seen) != seen);
            var result = await inner.ReadAsync(offset, length, context);
            env.onRead?.Invoke(offset, length);
            return result;
        }
    }

    private readonly Action<long, long>? onRead = onRead;
}

/// <summary>A codec that runs <see cref="SkiaImageCodec"/> once <see cref="Release"/> was called, recording the threads it ran on.</summary>
internal sealed class GatedCodec(bool open) : IImageCodec
{
    private readonly ManualResetEventSlim gate = new(open);
    public SyncList<int> Threads { get; } = new();
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => gate.Set();

    private void Enter()
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        Entered.TrySetResult();
        if (!gate.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("the gated codec was never released");
    }

    public (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType)
    {
        Enter();
        return SkiaImageCodec.Instance.ProbeDimensions(bytes, mimeType);
    }

    public byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType) => SkiaImageCodec.Instance.ConvertToPng(bytes, mimeType);

    public ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width, int height,
        IReadOnlyList<int> jpegQualities) => SkiaImageCodec.Instance.Encode(bytes, mimeType, width, height, jpegQualities);
}

internal static partial class Program
{
    private static readonly Lazy<IImageProcessor> ImagesProcessor = new(() => ImageProcessors.CreateThreaded(SkiaImageCodec.Instance));

    private static IImageProcessor Processor => ImagesProcessor.Value;

    private static (string, Func<Task>) ImagesCase(string slug, Func<string, Task> body) => Case("tools.images." + slug, () => WithToolDir(body));

    private static (string, Func<Task>) ImagesCase(string slug, Func<Task> body) => Case("tools.images." + slug, body);

    /// <summary>A <c>width</c> by <c>height</c> image of <c>block</c>-pixel squares coloured row by row from <c>colors</c>.</summary>
    private static (byte[] Rgba, int Width, int Height) Blocks(byte[][][] colors, int block)
    {
        var width = colors[0].Length * block;
        var height = colors.Length * block;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = colors[y / block][x / block];
                rgba[(y * width + x) * 4] = color[0];
                rgba[(y * width + x) * 4 + 1] = color[1];
                rgba[(y * width + x) * 4 + 2] = color[2];
                rgba[(y * width + x) * 4 + 3] = 255;
            }
        return (rgba, width, height);
    }

    /// <summary>RGBA pixels of a <c>width</c> by <c>height</c> image: red, with a green top-left pixel.</summary>
    private static byte[] RedPixels(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++) (rgba[index * 4], rgba[index * 4 + 3]) = (255, 255);
        (rgba[0], rgba[1]) = (0, 255);
        return rgba;
    }

    /// <summary>Upstream's encodeRgba: PNG, or JPEG at quality 95.</summary>
    private static byte[] EncodeRgba(byte[] rgba, int width, int height, string format)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var data = format == "png"
                ? pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6))
                : pixmap.Encode(new SKJpegEncoderOptions(95, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
            return data?.ToArray() ?? throw new InvalidOperationException("SkiaSharp could not encode the fixture");
        }
        finally { handle.Free(); }
    }

    private static byte[] EncodeImage(int width, int height, string format) => EncodeRgba(RedPixels(width, height), width, height, format);

    /// <summary>Noise, which neither PNG nor JPEG compresses well, so the byte limit decides.</summary>
    private static byte[] NoisyPng(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        uint seed = 1;
        for (var index = 0; index < rgba.Length; index++)
        {
            seed = unchecked(seed * 1103515245 + 12345);
            rgba[index] = index % 4 == 3 ? (byte)255 : (byte)(seed >> 24);
        }
        return EncodeRgba(rgba, width, height, "png");
    }

    /// <summary>A 24-bit BMP, which providers do not take inline.</summary>
    private static byte[] Bmp(int width, int height)
    {
        var row = (width * 3 + 3) / 4 * 4;
        var bytes = new byte[54 + row * height];
        bytes[0] = 0x42; bytes[1] = 0x4d;
        BitConverter.TryWriteBytes(bytes.AsSpan(2), (uint)bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), 54u);
        BitConverter.TryWriteBytes(bytes.AsSpan(14), 40u);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), (ushort)24);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (uint)(row * height));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) bytes[54 + y * row + x * 3 + 2] = 255;
        return bytes;
    }

    /// <summary>A 1x1 GIF with its logical screen size set to <c>width</c> by <c>height</c>.</summary>
    private static byte[] Gif(int width = 1, int height = 1)
    {
        var bytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        BitConverter.TryWriteBytes(bytes.AsSpan(6), (ushort)width);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), (ushort)height);
        return bytes;
    }

    /// <summary>A TIFF header whose first directory holds only Orientation = <c>orientation</c>.</summary>
    private static byte[] Tiff(int orientation, bool littleEndian)
    {
        var bytes = new byte[26];
        void U16(int at, int value)
        {
            if (littleEndian) (bytes[at], bytes[at + 1]) = ((byte)value, (byte)(value >> 8));
            else (bytes[at], bytes[at + 1]) = ((byte)(value >> 8), (byte)value);
        }
        void U32(int at, int value)
        {
            if (littleEndian) { U16(at, value & 0xffff); U16(at + 2, value >> 16); }
            else { U16(at, value >> 16); U16(at + 2, value & 0xffff); }
        }
        (bytes[0], bytes[1]) = littleEndian ? ((byte)0x49, (byte)0x49) : ((byte)0x4d, (byte)0x4d);
        U16(2, 42);
        U32(4, 8);
        U16(8, 1);
        U16(10, 0x0112);
        U16(12, 3);
        U32(14, 1);
        U16(18, orientation);
        return bytes;
    }

    private static readonly byte[] ExifHeader = [0x45, 0x78, 0x69, 0x66, 0, 0];

    /// <summary><c>jpeg</c> with an APP1 segment whose EXIF says <c>orientation</c>, after an APP0 segment when <c>app0</c>.</summary>
    private static byte[] WithOrientation(byte[] jpeg, int orientation, bool little = false, bool app0 = false)
    {
        byte[] exif = [.. ExifHeader, .. Tiff(orientation, little)];
        byte[] app1 = [0xff, 0xe1, (byte)((exif.Length + 2) >> 8), (byte)((exif.Length + 2) & 0xff), .. exif];
        byte[] app0Segment = app0 ? [0xff, 0xe0, 0, 16, 0x4a, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0] : [];
        return [.. jpeg.AsSpan(0, 2), .. app0Segment, .. app1, .. jpeg.AsSpan(2)];
    }

    /// <summary>A RIFF WebP with a dummy <c>VP8 </c> chunk and an <c>EXIF</c> chunk saying <c>orientation</c>, its TIFF data bare or prefixed.</summary>
    private static byte[] WebpWithOrientation(int orientation, bool prefixed)
    {
        static byte[] Chunk(string type, byte[] data) =>
            [.. System.Text.Encoding.ASCII.GetBytes(type), (byte)(data.Length & 0xff), (byte)((data.Length >> 8) & 0xff), 0, 0, .. data,
                .. data.Length % 2 == 1 ? new byte[] { 0 } : []];
        byte[] exif = [.. prefixed ? ExifHeader : [], .. Tiff(orientation, true)];
        byte[] body = [.. "WEBP"u8, .. Chunk("VP8 ", [1, 2, 3]), .. Chunk("EXIF", exif)];
        return [.. "RIFF"u8, (byte)(body.Length & 0xff), (byte)((body.Length >> 8) & 0xff), 0, 0, .. body];
    }

    /// <summary>Upstream's decodedImage: the size and RGBA pixels of base64 image data.</summary>
    private static (int Width, int Height, byte[] Rgba) DecodedImage(string data)
    {
        using var codec = SKCodec.Create(new MemoryStream(Convert.FromBase64String(data)));
        Check(codec is not null, "the prepared image decodes");
        var info = new SKImageInfo(codec!.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[info.Width * info.Height * 4];
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try { Equal(SKCodecResult.Success, codec.GetPixels(info, handle.AddrOfPinnedObject()), "decode"); }
        finally { handle.Free(); }
        return (info.Width, info.Height, rgba);
    }

    private static DurableModel ImageTestModel(IReadOnlyList<string>? input, DurableModelImageResizeOptions? resize) => new()
    {
        Id = "test", Name = "test", Api = "test", Provider = "test", Input = [.. input ?? ["text", "image"]],
        InputLimits = resize is null ? null : new DurableModelInputLimits { Images = new DurableModelImageInputLimits { Resize = resize } },
    };

    /// <summary>Upstream's read(): run <c>read</c> on <c>bytes</c> written to <c>name</c>, for a conversation whose model takes <c>input</c>.</summary>
    private static async Task<ToolExecutionResult> ReadImageFile(string directory, string name, byte[] bytes, IImageProcessor? images = null,
        IReadOnlyList<string>? input = null, DurableModelImageResizeOptions? resize = null, IExecutionEnv? env = null, ToolRegistration? tool = null)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
        var api = new ImageToolApi(env ?? new LocalExecutionEnv(directory), ImageTestModel(input, resize));
        tool ??= CodingTools.CreateReadTool(new ReadToolOptions { Images = images });
        return await tool.Execute(JsonValue.Object([new("path", name)]), api, ToolContext);
    }

    private static string[] Messages(ToolExecutionResult result) => [.. (result.Diagnostics ?? []).Select(diagnostic => diagnostic.Message)];

    private static (string Data, string MimeType) OnlyImage(ToolExecutionResult result)
    {
        Equal(1, result.Output?.Count, "one output block");
        var block = (JsonObjectValue)result.Output![0];
        Equal("image", ((JsonStringValue)block["type"]).Value, "block type");
        return (((JsonStringValue)block["data"]).Value, ((JsonStringValue)block["mimeType"]).Value);
    }

    private static string ImageBlocks(params (string Data, string MimeType)[] blocks) =>
        JsonValue.Array(blocks.Select(block => (JsonValue)JsonValue.Object([new("type", "image"), new("data", block.Data), new("mimeType", block.MimeType)]))).ToJson();

    private static void EmptyError(ToolExecutionResult result, string what)
    {
        Equal(0, result.Output?.Count, what + ": no output");
        Equal(true, result.IsError, what + ": isError");
    }

    private static IEnumerable<(string, Func<Task>)> ToolsImagesCases()
    {
        // ─── read of images, without an image processor ─────────────────────
        yield return ImagesCase("without-processor.returns-a-supported-image-within-the-limits-as-it-is", async directory =>
        {
            var png = EncodeImage(8, 6, "png");
            var result = await ReadImageFile(directory, "a.png", png);
            Equal(ImageBlocks((ImageFormats.ToBase64(png), "image/png")), result.Output?.ToJson(), "output");
            Equal("Read image file [image/png].", string.Join("|", Messages(result)), "messages");
            var tiny = await ReadImageFile(directory, "a.gif", Gif());
            Equal((ImageFormats.ToBase64(Gif()), "image/gif"), OnlyImage(tiny), "gif");
        });

        yield return ImagesCase("without-processor.refuses-an-image-too-large-to-send-without-reading-it", async directory =>
        {
            var env = new ObservedReadEnv(directory, null);
            var result = await ReadImageFile(directory, "big.png", NoisyPng(1100, 1100), env: env);
            EmptyError(result, "big.png");
            Check(System.Text.RegularExpressions.Regex.IsMatch(Messages(result)[0], @"^big\.png is an image \(image/png\) of .*, too large to send"),
                Messages(result)[0]);
            // Only the header and the chunk walk, in blocks of at most 64 KiB.
            Check(env.Largest <= 64 * 1024, $"largest read {env.Largest}");
        });

        yield return ImagesCase("without-processor.refuses-an-image-wider-or-taller-than-the-limits-by-its-header", async directory =>
        {
            var wide = await ReadImageFile(directory, "wide.png", EncodeImage(2001, 2, "png"));
            EmptyError(wide, "wide.png");
            Equal("wide.png is an image (image/png) of 2001x2, larger than 2000x2000, and no image processor is configured to shrink it",
                string.Join("|", Messages(wide)), "wide");
            var tall = await ReadImageFile(directory, "tall.gif", Gif(1, 3000));
            Check(Messages(tall)[0].StartsWith("tall.gif is an image (image/gif) of 1x3000, larger than 2000x2000", StringComparison.Ordinal), Messages(tall)[0]);
        });

        yield return ImagesCase("without-processor.refuses-a-format-that-needs-converting", async directory =>
        {
            var result = await ReadImageFile(directory, "a.bmp", Bmp(4, 4));
            EmptyError(result, "a.bmp");
            Equal("a.bmp is an image (image/bmp) that needs converting, and no image processor is configured", string.Join("|", Messages(result)), "message");
        });

        yield return ImagesCase("without-processor.says-when-the-model-sees-a-placeholder-instead-of-the-image", async directory =>
        {
            var result = await ReadImageFile(directory, "a.png", EncodeImage(4, 4, "png"), input: ["text"]);
            Equal(1, result.Output?.Count, "one block");
            Equal("Read image file [image/png]. The current model does not support images; it sees a placeholder instead.", string.Join("|", Messages(result)),
                "messages");
        });

        yield return ImagesCase("without-processor.applies-the-models-image-limits-instead-of-the-defaults", async directory =>
        {
            var png = EncodeImage(8, 6, "png");
            var refused = await ReadImageFile(directory, "a.png", png, resize: new DurableModelImageResizeOptions { MaxWidth = 4 });
            Check(Messages(refused)[0].Contains("of 8x6, larger than 4x2000,", StringComparison.Ordinal), Messages(refused)[0]);
            var resized = await ReadImageFile(directory, "a.png", png, Processor, resize: new DurableModelImageResizeOptions { MaxWidth = 4 });
            var decoded = DecodedImage(OnlyImage(resized).Data);
            Equal((4, 3), (decoded.Width, decoded.Height), "size");
        });

        yield return ImagesCase("without-processor.lists-bmp-in-the-description-only-with-a-processor", () =>
        {
            Check(CodingTools.CreateReadTool().Description.Contains("(jpg, png, gif, webp)", StringComparison.Ordinal), "without");
            Check(CodingTools.CreateReadTool(new ReadToolOptions { Images = Processor }).Description.Contains("(jpg, png, gif, webp, bmp)", StringComparison.Ordinal),
                "with");
            return Task.CompletedTask;
        });

        // ─── read of an image that changes while it is read ─────────────────
        yield return ImagesCase("changing.reads-it-again-rather-than-return-a-cut-off-image-even-when-it-grew", async directory =>
        {
            var png = EncodeImage(8, 6, "png");
            var wholeReads = new SyncList<long>();
            var env = new ObservedReadEnv(directory, (offset, length) =>
            {
                // A writer appends during the first read of the whole file; the second read sees it all.
                if (offset != 0 || length < png.Length) return;
                wholeReads.Add(length);
                if (wholeReads.Count == 1)
                {
                    using var file = new FileStream(Path.Combine(directory, "growing.png"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    file.Write(new byte[16]);
                }
            });
            var result = await ReadImageFile(directory, "growing.png", png, env: env);
            Equal($"{png.Length},{png.Length + 16}", string.Join(",", wholeReads.Snapshot()), "whole reads");
            Equal(ImageFormats.ToBase64([.. png, .. new byte[16]]), OnlyImage(result).Data, "data");
        });

        yield return ImagesCase("changing.fails-when-it-changes-during-the-second-read-too", async directory =>
        {
            var png = EncodeImage(8, 6, "png");
            var env = new ObservedReadEnv(directory, (offset, length) =>
            {
                if (offset != 0 || length < png.Length) return;
                using var file = new FileStream(Path.Combine(directory, "busy.png"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                file.Write(new byte[1]);
            });
            var error = await ThrowsAsync<InvalidOperationException>(() => ReadImageFile(directory, "busy.png", png, env: env), "busy.png");
            Equal("busy.png changed while it was read", error.Message, "message");
        });

        // ─── read of images, with the image processor ───────────────────────
        yield return ImagesCase("processor.passes-an-image-that-needs-no-change-through-untouched", async directory =>
        {
            var png = EncodeImage(8, 6, "png");
            var result = await ReadImageFile(directory, "a.png", png, Processor);
            Equal(ImageBlocks((ImageFormats.ToBase64(png), "image/png")), result.Output?.ToJson(), "output");
            Equal("image/gif", OnlyImage(await ReadImageFile(directory, "a.gif", Gif(), Processor)).MimeType, "gif");
        });

        yield return ImagesCase("processor.scales-an-image-down-to-the-maximum-size-and-says-how-to-map-coordinates-back", async directory =>
        {
            var result = await ReadImageFile(directory, "wide.png", EncodeImage(4000, 1000, "png"), Processor);
            var block = OnlyImage(result);
            var decoded = DecodedImage(block.Data);
            Equal((2000, 500), (decoded.Width, decoded.Height), "size");
            Equal($"Read image file [{block.MimeType}]. Resized from 4000x1000 to 2000x500. Multiply coordinates by 2.00 to map them to the original.",
                string.Join("|", Messages(result)), "messages");
        });

        yield return ImagesCase("processor.re-encodes-a-png-too-large-for-the-byte-limit-as-jpeg-at-its-size-and-shrinks-it-when-that-is-not-enough", async directory =>
        {
            var result = await ReadImageFile(directory, "big.png", NoisyPng(1100, 1100), Processor);
            var jpeg = OnlyImage(result);
            Equal("image/jpeg", jpeg.MimeType, "mime");
            Equal("Read image file [image/jpeg]. Converted from image/png to image/jpeg.", string.Join("|", Messages(result)), "messages");
            Check(jpeg.Data.Length <= CodingTools.DefaultImageLimits.MaxBytes, "within the byte limit");
            var decoded = DecodedImage(jpeg.Data);
            Equal((1100, 1100), (decoded.Width, decoded.Height), "size");
            var tight = CodingTools.DefaultImageLimits with { MaxBytes = 200_000 };
            var shrunk = await Processor.PrepareAsync(NoisyPng(2000, 2000), "image/png", tight);
            Check(shrunk!.Data.Length <= tight.MaxBytes, "shrunk within the byte limit");
            Equal(new ImageSize(2000, 2000), shrunk.Resized?.From, "from");
            Check(shrunk.Resized!.To.Width < 2000, "smaller");
        });

        yield return ImagesCase("processor.keeps-at-least-one-pixel-on-each-side-of-an-extreme-aspect-ratio", async () =>
        {
            var result = await Processor.PrepareAsync(EncodeImage(6000, 2, "png"), "image/png", CodingTools.DefaultImageLimits);
            Equal(new ImageResize(new ImageSize(6000, 2), new ImageSize(2000, 1)), result?.Resized, "resized");
        });

        yield return ImagesCase("processor.gives-up-when-not-even-one-pixel-fits-the-byte-limit", async () =>
        {
            var limits = CodingTools.DefaultImageLimits with { MaxBytes = 8 };
            Equal(null, await Processor.PrepareAsync(EncodeImage(4, 4, "png"), "image/png", limits), "prepared");
        });

        yield return ImagesCase("processor.keeps-a-jpeg-that-must-be-re-encoded-a-jpeg", async () =>
        {
            var result = await Processor.PrepareAsync(WithOrientation(EncodeImage(40, 30, "jpeg"), 3), "image/jpeg", CodingTools.DefaultImageLimits);
            Equal("image/jpeg", result?.MimeType, "mime");
        });

        yield return ImagesCase("processor.converts-a-format-providers-do-not-take-inline-to-png", async directory =>
        {
            var result = await ReadImageFile(directory, "a.bmp", Bmp(4, 4), Processor);
            Equal("image/png", OnlyImage(result).MimeType, "mime");
            Equal("Read image file [image/png]. Converted from image/bmp to image/png.", string.Join("|", Messages(result)), "messages");
        });

        yield return ImagesCase("processor.reports-bytes-it-cannot-decode-as-an-error", async directory =>
        {
            var broken = EncodeImage(4, 4, "png")[..40];
            var result = await ReadImageFile(directory, "broken.png", broken, Processor);
            EmptyError(result, "broken.png");
            Equal("broken.png is an image (image/png) that cannot be prepared for the model", string.Join("|", Messages(result)), "message");
        });

        yield return ImagesCase("processor.is-what-createcodingtools-images-gives-read", async directory =>
        {
            var tool = CodingTools.CreateCodingTools(new ReadToolOptions { Images = Processor }).Tools!.First(candidate => candidate.Name == "read");
            var result = await ReadImageFile(directory, "b.bmp", Bmp(4, 4), tool: tool);
            Equal("image/png", OnlyImage(result).MimeType, "mime");
        });

        // ─── EXIF orientation ───────────────────────────────────────────────
        byte[] r = [255, 0, 0], g = [0, 255, 0], b = [0, 0, 255], w = [255, 255, 255], k = [0, 0, 0], yellow = [255, 255, 0];
        byte[][][] stored = [[r, g, b], [w, k, yellow]];
        static byte[][][] Mirror(byte[][][] grid) => [.. grid.Select(row => row.Reverse().ToArray())];
        // A quarter turn clockwise: the left column, read bottom to top, becomes the top row.
        static byte[][][] Turn(byte[][][] grid) => [.. Enumerable.Range(0, grid[0].Length).Select(x => grid.Select(row => row[x]).Reverse().ToArray())];
        // What each orientation shows, from its EXIF definition: mirror, then turn clockwise.
        var upright = new Dictionary<int, byte[][][]>
        {
            [1] = stored,
            [2] = Mirror(stored),
            [3] = Turn(Turn(stored)),
            [4] = Turn(Turn(Mirror(stored))),
            [5] = Turn(Turn(Turn(Mirror(stored)))),
            [6] = Turn(stored),
            [7] = Turn(Mirror(stored)),
            [8] = Turn(Turn(Turn(stored))),
        };
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var current = orientation;
            yield return ImagesCase($"exif.turns-orientation-{current}-upright", async () =>
            {
                var (rgba, width, height) = Blocks(stored, 16);
                var jpeg = WithOrientation(EncodeRgba(rgba, width, height, "jpeg"), current);
                var prepared = await Processor.PrepareAsync(jpeg, "image/jpeg", CodingTools.DefaultImageLimits);
                var image = DecodedImage(prepared!.Data);
                var expected = upright[current];
                Equal((expected[0].Length * 16, expected.Length * 16), (image.Width, image.Height), "size");
                // The colour at the centre of each block, rounded to full channels against JPEG noise.
                for (var by = 0; by < expected.Length; by++)
                    for (var bx = 0; bx < expected[by].Length; bx++)
                    {
                        var at = ((by * 16 + 8) * image.Width + bx * 16 + 8) * 4;
                        var seen = string.Join(",", image.Rgba[at..(at + 3)].Select(channel => channel > 127 ? 255 : 0));
                        Equal(string.Join(",", expected[by][bx]), seen, $"block ({bx}, {by})");
                    }
            });
        }

        yield return ImagesCase("exif.reads-big-and-little-endian-jpeg-exif-after-an-app0-segment-and-1-when-absent-or-out-of-range", () =>
        {
            var jpeg = EncodeImage(2, 2, "jpeg");
            Equal(6, Exif.Orientation(WithOrientation(jpeg, 6)), "big-endian");
            Equal(8, Exif.Orientation(WithOrientation(jpeg, 8, little: true)), "little-endian");
            Equal(5, Exif.Orientation(WithOrientation(jpeg, 5, app0: true)), "after APP0");
            Equal(1, Exif.Orientation(WithOrientation(jpeg, 9)), "out of range");
            Equal(1, Exif.Orientation(jpeg), "absent");
            Equal(1, Exif.Orientation(EncodeImage(2, 2, "png")), "png");
            return Task.CompletedTask;
        });

        yield return ImagesCase("exif.reads-webp-exif-chunks-with-and-without-the-exif-prefix", () =>
        {
            Equal(6, Exif.Orientation(WebpWithOrientation(6, false)), "bare");
            Equal(3, Exif.Orientation(WebpWithOrientation(3, true)), "prefixed");
            return Task.CompletedTask;
        });

        // ─── imageDimensions ────────────────────────────────────────────────
        yield return ImagesCase("image-dimensions.reads-the-size-each-format-declares-in-its-header", () =>
        {
            Equal(((long)7, (long)5), ImageDetection.ImageDimensions(EncodeImage(7, 5, "png"), "image/png"), "png");
            Equal(((long)7, (long)5), ImageDetection.ImageDimensions(EncodeImage(7, 5, "jpeg"), "image/jpeg"), "jpeg");
            Equal(((long)7, (long)5), ImageDetection.ImageDimensions(WithOrientation(EncodeImage(7, 5, "jpeg"), 6, app0: true), "image/jpeg"), "jpeg with EXIF");
            Equal(((long)300, (long)200), ImageDetection.ImageDimensions(Gif(300, 200), "image/gif"), "gif");
            static byte[] Webp(string type, byte[] data) => [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. System.Text.Encoding.ASCII.GetBytes(type), 0, 0, 0, 0, .. data, .. new byte[16]];
            // VP8: frame tag, start code, then 14-bit width and height.
            Equal(((long)300, (long)200), ImageDetection.ImageDimensions(Webp("VP8 ", [0, 0, 0, 0x9d, 0x01, 0x2a, 0x2c, 0x01, 0xc8, 0x00]), "image/webp"), "VP8");
            // VP8L: signature, then width - 1 and height - 1 in 14 bits each.
            var bits = 299 | (199 << 14);
            Equal(((long)300, (long)200), ImageDetection.ImageDimensions(Webp("VP8L", [0x2f, (byte)(bits & 0xff), (byte)((bits >> 8) & 0xff),
                (byte)((bits >> 16) & 0xff), (byte)(bits >>> 24)]), "image/webp"), "VP8L");
            // VP8X: flags, then width - 1 and height - 1 in 24 bits each.
            Equal(((long)300, (long)200), ImageDetection.ImageDimensions(Webp("VP8X", [0, 0, 0, 0, 0x2b, 0x01, 0, 0xc7, 0, 0]), "image/webp"), "VP8X");
            Equal(null, ImageDetection.ImageDimensions([0xff, 0xd8, 0xff], "image/jpeg"), "truncated jpeg");
            return Task.CompletedTask;
        });

        // ─── toBase64 ───────────────────────────────────────────────────────
        yield return ImagesCase("to-base64.matches-standard-base64-for-every-remainder-length", () =>
        {
            foreach (var length in new[] { 0, 1, 2, 3, 4, 5, 10_000 })
            {
                var bytes = Enumerable.Range(0, length).Select(index => (byte)(index * 37 % 256)).ToArray();
                var utf8 = new byte[System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(length)];
                System.Buffers.Text.Base64.EncodeToUtf8(bytes, utf8, out _, out var written);
                Equal(System.Text.Encoding.ASCII.GetString(utf8, 0, written), ImageFormats.ToBase64(bytes), $"length {length}");
                Equal((long)written, ImageFormats.Base64Length(length), $"base64Length {length}");
            }
            return Task.CompletedTask;
        });

        // ─── createNodePhotonImages (ImageProcessors.CreateThreaded) ────────
        yield return ImagesCase("threaded.prepares-on-a-worker-thread-leaving-the-caller-free", async () =>
        {
            var bmp = Bmp(40, 30);
            var caller = Environment.CurrentManagedThreadId;
            // On the calling thread: the work is done before PrepareAsync returns.
            var inlineCodec = new GatedCodec(open: true);
            var inline = ImageProcessors.CreateThreaded(inlineCodec, new ThreadedImagesOptions { Worker = false }).PrepareAsync(bmp, "image/bmp",
                CodingTools.DefaultImageLimits);
            Check(inline.IsCompleted, "prepared on the calling thread");
            Equal(caller.ToString(), string.Join(",", inlineCodec.Threads.Snapshot()), "the calling thread decoded it");
            Equal("image/png", (await inline)?.MimeType, "inline result");
            // On a worker: PrepareAsync returns while the codec is held, and resolves once it is released.
            var threadedCodec = new GatedCodec(open: false);
            var threaded = ImageProcessors.CreateThreaded(threadedCodec).PrepareAsync(bmp, "image/bmp", CodingTools.DefaultImageLimits);
            await threadedCodec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check(!threaded.IsCompleted, "not prepared while the codec is held");
            Check(!threadedCodec.Threads.Snapshot().Contains(caller), "the worker thread decodes it");
            threadedCodec.Release();
            Equal("image/png", (await threaded.WaitAsync(TimeSpan.FromSeconds(30)))?.MimeType, "threaded result");
        });

        yield return ImagesCase("threaded.gives-the-same-result-as-the-calling-thread-and-keeps-the-callers-bytes", async () =>
        {
            var png = NoisyPng(1100, 1100);
            var copy = png.ToArray();
            var threaded = await Processor.PrepareAsync(png, "image/png", CodingTools.DefaultImageLimits);
            var inline = await ImageProcessors.CreateThreaded(SkiaImageCodec.Instance, new ThreadedImagesOptions { Worker = false })
                .PrepareAsync(png, "image/png", CodingTools.DefaultImageLimits);
            Check(threaded is not null, "prepared");
            Equal(inline, threaded, "same result");
            Check(png.AsSpan().SequenceEqual(copy), "the caller's bytes are unchanged");
        });

        yield return ImagesCase("threaded.prepares-concurrent-images-and-starts-a-new-worker-after-the-idle-one-exited", async () =>
        {
            var images = (ThreadedImageProcessor)ImageProcessors.CreateThreaded(SkiaImageCodec.Instance, new ThreadedImagesOptions { IdleMs = 10 });
            var (bmp, wide) = (Bmp(4, 4), EncodeImage(4000, 1000, "png"));
            var results = await Task.WhenAll(
                images.PrepareAsync(bmp, "image/bmp", CodingTools.DefaultImageLimits),
                images.PrepareAsync(wide, "image/png", CodingTools.DefaultImageLimits),
                images.PrepareAsync(new byte[] { 1, 2, 3 }, "image/png", CodingTools.DefaultImageLimits));
            Equal("image/png", results[0]?.MimeType, "bmp");
            Equal(new ImageSize(2000, 500), results[1]?.Resized?.To, "wide");
            Equal(null, results[2], "undecodable");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (images.Running && DateTime.UtcNow < deadline) await Task.Delay(10);
            Check(!images.Running, "the idle worker exited");
            // How many workers served the three depends on timing (one may exit between them); the next image needs a new one.
            var started = images.ThreadsStarted;
            Equal("image/png", (await images.PrepareAsync(Bmp(2, 2), "image/bmp", CodingTools.DefaultImageLimits))?.MimeType, "after the idle exit");
            Equal(started + 1, images.ThreadsStarted, "a new worker started");
        });
    }
}
