// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): core/agent-session.ts prompt and queued image normalization.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Tools.Images;

namespace PiSharp.Cli.Extensions;

internal static class PromptImageNormalizer
{
    internal static TranscriptEntry Normalize(TranscriptEntry message, bool autoResize, JsonData? model, IImageCodec? codec = null)
    {
        if (message.Role != "user" || !message.WireBody.Value.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return message;
        var blocks = new JsonArray(); var hints = new List<string>(); var changed = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "image" ||
                !block.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String ||
                !block.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String)
            { blocks.Add(JsonUtf16.MutableNode(block.GetRawText())); continue; }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data.GetString()!); }
            catch (FormatException) { hints.Add(ImageProcessor.ConversionFailedMessage); changed = true; continue; }
            var processed = ImageProcessor.Process(bytes, mime.GetString()!, autoResize, ResizeOptions(model), codec);
            if (!processed.Ok) { hints.Add(processed.Message!); changed = true; continue; }
            var image = JsonUtf16.MutableNode(block.GetRawText())!.AsObject();
            image["data"] = processed.Data; image["mimeType"] = processed.MimeType; blocks.Add(image);
            hints.AddRange(processed.Hints);
            changed |= processed.Data != data.GetString() || processed.MimeType != mime.GetString() || !processed.Hints.IsEmpty;
        }
        if (!changed) return message;
        if (hints.Count != 0)
        {
            var firstText = blocks.OfType<JsonObject>().FirstOrDefault(block => block["type"]?.GetValue<string>() == "text");
            if (firstText is null) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = string.Join("\n", hints) });
            else firstText["text"] = (firstText["text"]?.GetValue<string>() ?? "") + "\n\n" + string.Join("\n", hints);
        }
        var body = JsonUtf16.MutableNode(message.WireBody.Value.GetRawText())!.AsObject(); body["content"] = blocks;
        return new(message.Role, JsonData.Parse(body.ToJsonString()));
    }

    private static ImageResizeOptions? ResizeOptions(JsonData? model)
    {
        if (model is null || model.Value.ValueKind != JsonValueKind.Object || !model.Value.TryGetProperty("inputLimits", out var limits) ||
            limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Object || !images.TryGetProperty("resize", out var resize) ||
            resize.ValueKind != JsonValueKind.Object) return null;
        var defaults = new ImageResizeOptions();
        int Read(string name, int fallback) => resize.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number > 0 ? number : fallback;
        return new(Read("maxWidth", defaults.MaxWidth), Read("maxHeight", defaults.MaxHeight), Read("maxBytes", defaults.MaxBytes), Read("jpegQuality", defaults.JpegQuality));
    }
}
