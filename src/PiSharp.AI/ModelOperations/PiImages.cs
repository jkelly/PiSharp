// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/api/pi-image.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

public sealed class PiImages : IImagesApi
{
    public const string ApiId = "pi-image";
    public static PiImages Instance { get; } = new();
    public string Api => ApiId;

    public async Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new AssistantImages(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Stop, ProviderRequest.Now(options));
        try
        {
            if (model.Api != ApiId) throw new InvalidOperationException($"Unsupported image API: {model.Api}");
            if (!model.AcceptsImages && !context.Input.IsDefault && context.Input.Any(item => item is ImageContent))
                throw new InvalidOperationException($"Pi Image model {model.Id} does not support image input");
            var payload = new JsonObject { ["model"] = model.Id, ["context"] = JsonNode.Parse(ModelOperationJson.WriteImagesContext(context).ToString()) };
            var wire = new JsonObject();
            if (options?.AspectRatio is { } ratio) wire["aspectRatio"] = ratio;
            if (options?.Size is { } size) wire["size"] = size;
            if (options?.SessionId is { } session) wire["sessionId"] = session;
            if (options?.Metadata is { } metadata) wire["metadata"] = JsonNode.Parse(metadata.ToString());
            if (wire.Count > 0) payload["options"] = wire;
            var body = await ClassifierShared.PostAsync("Pi Image", new Uri(model.BaseUrl.TrimEnd('/') + "/image"), model, payload, options, cancellationToken).ConfigureAwait(false);
            if (ProviderRequest.IsString(body, "responseId", out var id) && id.Length > 0) result = result with { ResponseId = id };
            result = result with { Usage = PiUsage.Parse(ClassifierShared.Field(body, "usage"), model) };
            if (ClassifierShared.Field(body, "output") is not { ValueKind: JsonValueKind.Array } blocks)
                throw new InvalidDataException("Pi Image returned an unexpected response");
            return result with { Output = [.. blocks.EnumerateArray().Select(ParseBlock)] };
        }
        catch (Exception error)
        {
            return result with { StopReason = cancellationToken.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error,
                ErrorMessage = ProviderRequest.FormatError(error, "Pi Image error") };
        }
    }

    private static ImagesContentBlock ParseBlock(JsonElement item, int index)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Pi Image returned an invalid output block at index {index}");
        ProviderRequest.IsString(item, "type", out var type);
        return type switch
        {
            "text" when ProviderRequest.IsString(item, "text", out var text) => new ImagesTextBlock(text),
            "image" when ProviderRequest.IsString(item, "data", out var data) && data.Length > 0 &&
                ProviderRequest.IsString(item, "mimeType", out var mime) && mime.Length > 0 => new ImageContent(data, mime),
            "image" or "text" => throw new InvalidDataException($"Pi Image returned an invalid {type} block at index {index}"),
            _ => throw new InvalidDataException($"Pi Image returned an unknown output block type at index {index}")
        };
    }
}
