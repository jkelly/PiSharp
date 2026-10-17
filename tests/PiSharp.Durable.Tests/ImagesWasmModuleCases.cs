// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/images-wasm-module.test.ts, as images.wasm-module.<case>.
// Upstream checks that createPhotonImages() takes Photon's WebAssembly as a compiled module (the form Cloudflare Workers pass), in a file of
// its own so Photon is not yet initialized: a 1x1 GIF comes back as it is only once it decodes, which fails without an initialized Photon.
// PiSharp has no WebAssembly module: ImageProcessors.Create() takes the codec object itself (Images.cs). The case checks the same outcome
// for a processor made from a codec instance, and, for the lazy loading the separate file stands for, that nothing reaches the codec until
// read is given an image: creating the coding tools with the processor and reading a text file leave it untouched.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using PiSharp.Durable.Tools.Images;
using PiSharp.Tools.Skia;

internal static partial class Program
{
    /// <summary><see cref="SkiaImageCodec"/>, counting the calls that reach it.</summary>
    private sealed class CountingCodec : PiSharp.Tools.Images.IImageCodec
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType)
        {
            Interlocked.Increment(ref calls);
            return SkiaImageCodec.Instance.ProbeDimensions(bytes, mimeType);
        }

        public byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType)
        {
            Interlocked.Increment(ref calls);
            return SkiaImageCodec.Instance.ConvertToPng(bytes, mimeType);
        }

        public System.Collections.Immutable.ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width,
            int height, IReadOnlyList<int> jpegQualities)
        {
            Interlocked.Increment(ref calls);
            return SkiaImageCodec.Instance.Encode(bytes, mimeType, width, height, jpegQualities);
        }
    }

    private static IEnumerable<(string, Func<Task>)> ImagesWasmModuleCases()
    {
        yield return Case("images.wasm-module.a-processor-made-from-a-codec-instance-returns-a-decodable-gif-as-it-is", () => WithToolDir(async directory =>
        {
            // A 1x1 GIF; returned as it is only once it decodes.
            const string data = "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
            var codec = new CountingCodec();
            var images = ImageProcessors.Create(codec);
            var tool = CodingTools.CreateCodingTools(new ReadToolOptions { Images = images }).Tools!.First(candidate => candidate.Name == "read");
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "text\n");
            var text = await tool.Execute(JsonValue.Object([new("path", "notes.txt")]), new FakeToolApi(new LocalExecutionEnv(directory)), ToolContext);
            Equal("""[{"type":"text","text":"text\n"}]""", text.Output?.ToJson(), "text read");
            Equal(0, codec.Calls, "nothing reaches the codec before an image");
            var prepared = await images.PrepareAsync(Convert.FromBase64String(data), "image/gif", CodingTools.DefaultImageLimits);
            Equal(new PreparedImage(data, "image/gif"), prepared, "prepared");
            Check(codec.Calls > 0, "the codec decoded it");
        }));
    }
}
