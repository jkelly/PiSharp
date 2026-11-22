// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/examples/47-fit-images.ts (Pi v1.6.0). It prints what upstream's
// console.log prints, its arguments joined with spaces. ScriptedModels stands in for the faux provider, with a model whose images may be at
// most 1000 pixels wide.
// - createNodePhotonImages() is ImageProcessors.CreateThreaded over an image codec: PiSharp.Tools' dependency-free BuiltinImageCodec here,
//   which decodes and encodes the PNGs this example uses. A host whose input includes JPEG, GIF or WebP passes PiSharp.Tools.Skia's
//   SkiaImageCodec, the codec the coding agent's read tool resizes with.
// - fitImage, imageLimits and createFitImagesExtension are FitImages.FitImageAsync, CodingTools.ImageLimitsOf and FitImages.CreateExtension.
// - zlib's deflateSync is ZLibStream; the CRC-32 is System.IO.Hashing-free, computed as upstream computes it.
// PiSharp-only: on a file backend the storage is reopened in a plain Session and the transcript's kinds read back.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Images;
using PiSharp.Tools.Images;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    /// <summary>A <paramref name="width"/> by <paramref name="height"/> grey PNG, enough to show sizes.</summary>
    private static byte[] GreyPng(int width, int height)
    {
        var crcTable = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            crcTable[n] = c;
        }
        uint Crc(ReadOnlySpan<byte> bytes)
        {
            var c = 0xffffffff;
            foreach (var value in bytes) c = crcTable[(c ^ value) & 0xff] ^ (c >> 8);
            return c ^ 0xffffffff;
        }
        byte[] Chunk(string type, byte[] data)
        {
            byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
            var chunk = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
            body.CopyTo(chunk, 4);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc(body));
            return chunk;
        }
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        var rows = new byte[(width + 1) * height];
        Array.Fill(rows, (byte)128);
        for (var row = 0; row < height; row++) rows[row * (width + 1)] = 0;
        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(rows);
        return [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, .. Chunk("IHDR", header), .. Chunk("IDAT", deflated.ToArray()), .. Chunk("IEND", [])];
    }

    /// <summary>A PNG image block's size, from its header.</summary>
    private static string ImageSizeOf(JsonObjectValue image)
    {
        var bytes = Convert.FromBase64String(((JsonStringValue)image["data"]).Value);
        return $"{BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16))}x{BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))} " +
            ((JsonStringValue)image["mimeType"]).Value;
    }

    // 47: images fitted to the model's limits before they are stored, so the transcript (and the provider's prompt cache) never changes
    // later: a host fits the photo a person attaches with FitImageAsync before it submits the message, and the `fit-images` extension fits a
    // screenshot a tool returns before its result is committed. The model takes images up to 1000 pixels wide here.
    private static async Task FitImagesExampleAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // The scripted provider plays a model whose images may be at most 1000 pixels wide: it asks for a screenshot, then answers.
        var limited = new DurableModel
        {
            Id = "faux-1", Name = "Faux Model", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text", "image"],
            ContextWindow = 128000, MaxTokens = 16384,
            InputLimits = new DurableModelInputLimits
            {
                Images = new DurableModelImageInputLimits { Resize = new DurableModelImageResizeOptions { MaxWidth = 1000 } },
            },
        };
        var models = new ScriptedModels(ScriptedReply.ToolCall("screenshot", JsonObjectValue.Empty, "s"), "Both look grey.") { Model = limited };

        // One processor (on a worker thread) for the extension and the host's own input.
        var images = ImageProcessors.CreateThreaded(BuiltinImageCodec.Instance);
        var screenshot = Define.Tool(new ToolRegistration
        {
            Name = "screenshot",
            Description = "Take a screenshot",
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Output = JsonValue.Array([JsonValue.Object(
                    [new("type", "image"), new("data", Convert.ToBase64String(GreyPng(3000, 1000))), new("mimeType", "image/png")])]),
            }),
        });
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "screen", Tools = [screenshot] });
        registry.Install(FitImages.CreateExtension(images));

        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        // The host fits the attached photo to the conversation model's limits before it submits the message.
        var model = models.GetModel("faux", "faux-1");
        var photo = await FitImages.FitImageAsync(GreyPng(4000, 3000), "image/png", CodingTools.ImageLimitsOf(model), images);
        if (photo is FittedImage.Omitted omitted) throw new InvalidOperationException(omitted.Note);
        var fitted = (FittedImage.Fitted)photo;
        var text = string.Join("\n", ["Compare this photo with the screen.", .. fitted.Notes]);
        var settled = await (await root.SubmitAsync(
            SubmissionDraft.Input(JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)]), fitted.Image])), Context)).WaitAsync(Context);
        await LogAsync(output, "status:", StatusName(settled.Status));

        // What the transcript holds, and so what every later request replays.
        static string Shown(JsonObjectValue item) =>
            item["type"] is JsonStringValue { Value: "image" } ? ImageSizeOf(item) : Json(item["text"]);
        foreach (var entry in (await root.EntriesAsync(new EntryQueryBounds(), 20, null, Context)).Items.Reverse())
        {
            if (entry.Model is not { Count: > 0 } stored || stored[0] is not JsonObjectValue message) continue;
            if (Entries.UserEntry.Is(entry) && message["content"] is JsonArrayValue content)
                foreach (var item in content.OfType<JsonObjectValue>()) await LogAsync(output, "user:", Shown(item));
            if (message["role"] is JsonStringValue { Value: "toolResult" })
                foreach (var item in ((JsonArrayValue)message["content"]).OfType<JsonObjectValue>()) await LogAsync(output, "tool result:", Shown(item));
        }
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }
}
