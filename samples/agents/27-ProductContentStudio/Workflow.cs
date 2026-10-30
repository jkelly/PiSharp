// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.Samples.ProductContentStudio;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("product-content-studio", "Product content studio",
        "Turn structured product facts into channel copy and a visual asset.",
        [
            new("write_copy", "Draft copy constrained to supplied product facts", SampleJson.Schema(), async (_, ct) =>
            {
                var name = c.Input.Text("product");
                var features = c.Input["features"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                var result = new JsonObject
                {
                    ["headline"] = $"Meet {name}",
                    ["shortDescription"] = $"{name} for {c.Input.Text("audience")}: {string.Join("; ", features)}.",
                    ["featureCount"] = features.Length
                };
                await c.WriteJsonAsync("product-copy.json", result, ct);
                c.State["copy"] = result.DeepClone();
                return result;
            }),
            new("prepare_visual_asset", "Write a visual specification or generate an image with the selected provider",
                SampleJson.Schema(), async (_, ct) =>
            {
                if (c.State["copy"] is null) throw new InvalidOperationException("Run write_copy first.");
                if (c.Input["generateImage"]?.GetValue<bool>() == true)
                {
                    if (!c.Approved) throw new InvalidOperationException("Image generation requires --approve.");
                    if (c.Provider != "openrouter" || string.IsNullOrWhiteSpace(c.Model))
                        throw new InvalidOperationException("Image generation requires --provider openrouter and --model IMAGE_MODEL.");
                    var key = Environment.GetEnvironmentVariable(c.KeyEnvironment);
                    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Configured image API key is unavailable.");
                    return await GenerateImageAsync(c, key, c.HttpHandler, ct);
                }
                var result = new JsonObject
                {
                    ["kind"] = "illustration-specification", ["status"] = "fixture-only",
                    ["prompt"] = c.Input.Text("visualBrief"), ["product"] = c.Input.Text("product"),
                    ["generatedImage"] = false
                };
                await c.WriteJsonAsync("visual-asset.json", result, ct);
                return result;
            })
        ],
        [new("write_copy", new()), new("prepare_visual_asset", new())]);

    // Uses PiSharp.AI's registered OpenRouter image API. The injected handler is solely for offline regression tests.
    public static async Task<JsonObject> GenerateImageAsync(SampleContext c, string apiKey,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (c.Provider != "openrouter" || string.IsNullOrWhiteSpace(c.Model))
            throw new InvalidOperationException("Choose a supported image provider and model.");
        var brief = c.Input.Text("visualBrief");
        if (string.IsNullOrWhiteSpace(brief)) throw new ArgumentException("visualBrief is required.");
        var model = new ImageModel(c.Model, c.Model, OpenRouterImages.ApiId, "openrouter",
            string.IsNullOrWhiteSpace(c.BaseUrl) ? "https://openrouter.ai/api/v1" : c.BaseUrl,
            ["text"], new ModelCost(0, 0, 0, 0), ["image"]);
        using var http = handler is null ? null : new HttpMessageInvoker(handler, disposeHandler: false);
        var output = await ModelOperationApis.GenerateImagesAsync(model,
            new ImagesContext([new ImagesTextBlock(brief)]),
            new ImagesOptions { ApiKey = apiKey, Http = http, MaxRetries = 0 }, ct);
        if (output.StopReason != ModelOperationStopReason.Stop)
            throw new InvalidOperationException("Image provider failed: " + SecretRedactor.Redact(output.ErrorMessage ?? output.StopReason.ToString()));
        var images = output.Output.OfType<ImageContent>().ToArray();
        if (images.Length == 0) throw new InvalidOperationException("Image provider returned no image bytes.");
        var image = images[0];
        var extension = image.MimeType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => throw new InvalidDataException("Unsupported image media type.") };
        if (image.Data.Length > 14_000_000) throw new InvalidDataException("Generated image exceeds the 10 MiB limit.");
        var bytes = Convert.FromBase64String(image.Data);
        if (bytes.Length == 0 || bytes.Length > 10_485_760) throw new InvalidDataException("Generated image size is invalid.");
        var path = "product-image" + extension;
        await File.WriteAllBytesAsync(c.ResolvePath(path), bytes, ct);
        var result = new JsonObject
        {
            ["kind"] = "generated-image", ["status"] = "generated", ["generatedImage"] = true,
            ["path"] = path, ["mediaType"] = image.MimeType, ["bytes"] = bytes.Length,
            ["provider"] = "openrouter", ["model"] = c.Model
        };
        await c.WriteJsonAsync("visual-asset.json", result, ct);
        return result;
    }
}
