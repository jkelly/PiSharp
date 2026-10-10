using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Sessions.Storage;
using SkiaSharp;

// tools.read-image-budget (owner decision 0004): Pi's read tool sends images of up to 4.5MB of base64 (image-resize-core.ts
// DEFAULT_MAX_BYTES) through processImage with Photon. The CLI read path decodes with SkiaSharp, keeps or resizes the image,
// persists it and sends it in the next request. Authored expectations.
internal static partial class Program
{
    private static async Task ReadImageEndToEnd()
    {
        var root = Temp("read-image-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var session = Path.Combine(root, "session.jsonl"); var script = Path.Combine(root, "script.json");
            // Just under Pi's 4.5MB of base64 and within 2000x2000: sent whole. Over 2000x2000: resized below the budget.
            var whole = NoiseJpeg(1900, 1900, 4_400_000, 4_718_000); var wholePath = Path.Combine(root, "whole.jpg");
            var large = NoiseJpegAt(3000, 2400, 300, 92); var largePath = Path.Combine(root, "large.jpg");
            await File.WriteAllBytesAsync(wholePath, whole); await File.WriteAllBytesAsync(largePath, large);
            var wholeData = Convert.ToBase64String(whole);
            Equal("0", await Run(["session", "create", "--session", session, "--workspace", root, "--offline-api", "openai-completions", "--offline-images", "true"]), "create");
            const string note = "Read image file [image/jpeg]\n[Image: original 3000x2400, displayed at 2000x1600. Multiply coordinates by 1.50 to map to original image.]";
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                turns = new object[]
                {
                    CompletionsToolTurn("read", "read-whole", new { path = wholePath }, ["look"]),
                    // The next request carries the whole image as a data URL; its tail proves nothing was cut.
                    CompletionsToolTurn("read", "read-large", new { path = largePath }, ["Read image file [image/jpeg]", wholeData[^64..]]),
                    CompletionsTextTurn("seen both", [note])
                }
            }), new UTF8Encoding(false));
            Equal("0", await Run(["session", "prompt", "--session", session, "--workspace", root, "--offline-api", "openai-completions", "--offline-images", "true",
                "--offline-script", script, "--message", "look", "--allow-read", wholePath, "--allow-read", largePath]), "prompt");
            // Both results are durable: the whole image byte-exact, the large one resized and under the budget.
            var log = await new SessionLogReader(new(MaximumInputBytes: 256 * 1024 * 1024, MaximumLineBytes: 64 * 1024 * 1024,
                CodecOptions: new(MaximumRecordCharacters: 16 * 1024 * 1024, MaximumUtf8Bytes: 64 * 1024 * 1024))).ReadFileAsync(session);
            Check(log.Status == SessionLogReadStatus.Complete, "Session with images did not reopen completely.");
            var images = log.ValidatedPrefix.Select(record => record.Entry.WireBody.Value)
                .Where(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "toolResult")
                .Select(entry => entry.GetProperty("message").GetProperty("content")).ToArray();
            Equal(2, images.Length, "tool results");
            Equal(wholeData, images[0][1].GetProperty("data").GetString(), "whole image data");
            Equal(note, images[1][0].GetProperty("text").GetString(), "resize note");
            var resized = images[1][1].GetProperty("data").GetString()!;
            Check(resized.Length < PiPayloadBudget.ImageBase64Characters, "Resized image exceeds Pi's budget.");
            using var decoded = SKBitmap.Decode(Convert.FromBase64String(resized));
            Check(decoded is { Width: 2000, Height: 1600 }, "Resized image dimensions differ.");
        }
        finally { Directory.Delete(root, recursive: true); }

        static async Task<string> Run(string[] args)
        {
            using var output = new StringWriter(); using var errors = new StringWriter();
            var code = await SessionCommands.RunAsync(args, output, errors);
            return code + (code == 0 ? "" : " " + errors + " " + output.ToString()[..Math.Min(4000, output.ToString().Length)]);
        }
    }

    private static object CompletionsToolTurn(string name, string call, object arguments, string[] required) => new
    {
        requiredInputTexts = required,
        events = new object[]
        {
            new { id = "cmpl-" + call, @object = "chat.completion.chunk", model = "pisharp-offline-completions-session", choices = new[] { new { index = 0,
                delta = new { tool_calls = new[] { new { index = 0, id = call, type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } } },
                finish_reason = (string?)null } } },
            new { choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } } },
            new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } }
        }
    };
    private static object CompletionsTextTurn(string text, string[] required) => new
    {
        requiredInputTexts = required,
        events = new object[]
        {
            new { id = "cmpl-text", @object = "chat.completion.chunk", model = "pisharp-offline-completions-session", choices = new[] { new { index = 0,
                delta = new { role = "assistant", content = text }, finish_reason = (string?)null } } },
            new { choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } },
            new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } }
        }
    };

    /// <summary>A noise JPEG whose base64 length lies in [minimum, maximum), bisecting the noise amplitude per quality.</summary>
    private static byte[] NoiseJpeg(int width, int height, int minimum, int maximum)
    {
        foreach (var quality in new[] { 90, 95, 98, 100 })
        {
            int low = 0, high = 1000;
            while (low <= high)
            {
                var middle = (low + high) / 2; var bytes = NoiseJpegAt(width, height, middle, quality); var length = (bytes.Length + 2) / 3 * 4;
                if (length >= maximum) high = middle - 1; else if (length < minimum) low = middle + 1; else return bytes;
            }
        }
        throw new InvalidOperationException("No JPEG landed in the requested size window.");
    }
    private static byte[] NoiseJpegAt(int width, int height, int permille, int quality)
    {
        var pixels = new byte[width * height * 4]; new Random(11).NextBytes(pixels);
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = index % 4 == 3 ? (byte)255 : (byte)(128 + (pixels[index] - 128) * permille / 1000);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                ?? throw new InvalidOperationException("Fixture JPEG encoding failed.");
            return data.ToArray();
        }
        finally { handle.Free(); }
    }
}
