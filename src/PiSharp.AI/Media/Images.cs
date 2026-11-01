// PiSharp-only (no upstream Pi source): image generation over OpenAI's Images API, OpenRouter's Image API and Google's Gemini and
// Imagen APIs, as implementations of the upstream image API contract (IImagesApi), next to the ported OpenRouterImages (which goes
// through OpenRouter's chat completions and takes no image options).
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>
/// The request parameters image APIs read from <see cref="ImagesOptions.Metadata"/>. Each API sends the ones its provider documents and
/// leaves the rest out; the provider rejects values a model does not support. <see cref="Options"/> is a JSON object whose members are
/// added to the request as they are (provider-specific fields such as OpenRouter's <c>provider</c> routing or FLUX's <c>steps</c>).
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record ImageParameters
{
    /// <summary>Number of images (<c>n</c>).</summary>
    public int? Count { get; init; }
    /// <summary><c>auto</c>, <c>WIDTHxHEIGHT</c> (OpenAI), or a tier such as <c>2K</c> (OpenRouter).</summary>
    public string? Size { get; init; }
    /// <summary><c>auto</c>, <c>low</c>, <c>medium</c>, <c>high</c> (and <c>xhigh</c>, <c>max</c> on some OpenAI models).</summary>
    public string? Quality { get; init; }
    /// <summary><c>auto</c>, <c>transparent</c> or <c>opaque</c>; transparency needs PNG or WebP output.</summary>
    public string? Background { get; init; }
    /// <summary><c>png</c>, <c>jpeg</c>, <c>webp</c> (and <c>svg</c> for OpenRouter vectorization models).</summary>
    public string? OutputFormat { get; init; }
    /// <summary>0 to 100 for JPEG and WebP output.</summary>
    public int? OutputCompression { get; init; }
    /// <summary>For example <c>1:1</c>, <c>16:9</c> or <c>auto</c> (Google, OpenRouter).</summary>
    public string? AspectRatio { get; init; }
    /// <summary>A resolution tier: <c>512</c>, <c>1K</c>, <c>2K</c>, <c>4K</c> (OpenRouter; Gemini's <c>imageSize</c>).</summary>
    public string? Resolution { get; init; }
    /// <summary>OpenAI moderation: <c>auto</c> or <c>low</c>.</summary>
    public string? Moderation { get; init; }
    /// <summary>OpenAI edits: how closely to keep the input images' features, <c>low</c> or <c>high</c>.</summary>
    public string? InputFidelity { get; init; }
    /// <summary>A seed for repeatable output, where the model supports one (OpenRouter).</summary>
    public long? Seed { get; init; }
    /// <summary>A stable, hashed end-user id for abuse monitoring (OpenAI <c>safety_identifier</c>, OpenRouter <c>user</c>).</summary>
    public string? User { get; init; }
    /// <summary>OpenAI edits: a PNG whose fully transparent pixels mark where the first reference image is edited.</summary>
    public ImageContent? Mask { get; init; }
    /// <summary>A JSON object merged into the request as it is.</summary>
    public JsonData? Options { get; init; }

    public JsonData ToJson()
    {
        var node = new JsonObject();
        if (Count is { } count) node["n"] = count;
        if (Size is { Length: > 0 } size) node["size"] = size;
        if (Quality is { Length: > 0 } quality) node["quality"] = quality;
        if (Background is { Length: > 0 } background) node["background"] = background;
        if (OutputFormat is { Length: > 0 } format) node["outputFormat"] = format;
        if (OutputCompression is { } compression) node["outputCompression"] = compression;
        if (AspectRatio is { Length: > 0 } ratio) node["aspectRatio"] = ratio;
        if (Resolution is { Length: > 0 } resolution) node["resolution"] = resolution;
        if (Moderation is { Length: > 0 } moderation) node["moderation"] = moderation;
        if (InputFidelity is { Length: > 0 } fidelity) node["inputFidelity"] = fidelity;
        if (Seed is { } seed) node["seed"] = seed;
        if (User is { Length: > 0 } user) node["user"] = user;
        if (Mask is { } mask) node["mask"] = new JsonObject { ["data"] = mask.Data, ["mimeType"] = mask.MimeType };
        if (Options is { Value.ValueKind: JsonValueKind.Object } options) node["options"] = JsonNode.Parse(options.ToString());
        return JsonData.Parse(node.ToJsonString());
    }

    public static ImageParameters From(JsonData? metadata)
    {
        if (metadata is not { Value: { ValueKind: JsonValueKind.Object } value }) return new();
        return new ImageParameters
        {
            Count = MediaHttp.Number(value, "n") is { } n ? (int)n : null,
            Size = MediaHttp.String(value, "size"),
            Quality = MediaHttp.String(value, "quality"),
            Background = MediaHttp.String(value, "background"),
            OutputFormat = MediaHttp.String(value, "outputFormat"),
            OutputCompression = MediaHttp.Number(value, "outputCompression") is { } compression ? (int)compression : null,
            AspectRatio = MediaHttp.String(value, "aspectRatio"),
            Resolution = MediaHttp.String(value, "resolution"),
            Moderation = MediaHttp.String(value, "moderation"),
            InputFidelity = MediaHttp.String(value, "inputFidelity"),
            Seed = MediaHttp.Number(value, "seed") is { } seed ? (long)seed : null,
            User = MediaHttp.String(value, "user"),
            Mask = MediaHttp.Field(value, "mask") is { ValueKind: JsonValueKind.Object } mask && MediaHttp.String(mask, "data") is { } data
                ? new ImageContent(data, MediaHttp.String(mask, "mimeType") ?? "image/png") : null,
            Options = MediaHttp.Field(value, "options") is { ValueKind: JsonValueKind.Object } options ? JsonData.Parse(options.GetRawText()) : null,
        };
    }

    /// <summary>Adds the <see cref="Options"/> members to <paramref name="body"/>, replacing fields of the same name.</summary>
    internal void MergeOptions(JsonObject body) => MediaHttp.MergeOptions(body, Options);
}

[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
internal static class ImageApiShared
{
    /// <summary>The text blocks of an image request, joined by blank lines.</summary>
    internal static string Prompt(ImagesContext context) =>
        string.Join("\n\n", (context.Input.IsDefault ? [] : context.Input).OfType<ImagesTextBlock>().Select(block => block.Text));

    internal static ImmutableArray<ImageContent> References(ImagesContext context) =>
        [.. (context.Input.IsDefault ? [] : context.Input).OfType<ImageContent>()];

    /// <summary>Runs an image request, turning a failure into an error or aborted result as the image contract requires.</summary>
    internal static async Task<AssistantImages> RunAsync(ImageModel model, ImagesOptions? options, CancellationToken cancellationToken,
        Func<AssistantImages, ImagesOptions, Task<AssistantImages>> run)
    {
        var output = new AssistantImages(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Stop, ProviderRequest.Now(options));
        try
        {
            if (options is null || string.IsNullOrEmpty(options.ApiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            return await run(output, options).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return output with
            {
                StopReason = cancellationToken.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error,
                ErrorMessage = error.Message,
            };
        }
    }

    internal static MediaEndpoint Endpoint(ImageModel model, ImagesOptions options) => new(model.BaseUrl, options.ApiKey)
    {
        Headers = [.. ProviderRequest.MergeHeaders(ProviderRequest.Nullable(model.Headers), options.Headers).Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))],
    };

    internal static TokenUsage Usage(double input, double output) =>
        ProviderRequest.Usage(input, output, 0, 0, input + output, (0, 0, 0, 0, 0));
}

/// <summary>
/// OpenAI Images (GPT image models): <c>POST {baseUrl}/images/generations</c> (JSON) for a text prompt, or <c>POST {baseUrl}/images/edits</c>
/// (multipart: one <c>image[]</c> part per reference image, up to 16, and an optional <c>mask</c>) when reference images are given. Sends
/// <c>n</c>, <c>size</c>, <c>quality</c>, <c>background</c>, <c>output_format</c>, <c>output_compression</c>, <c>moderation</c>,
/// <c>safety_identifier</c> and, for edits, <c>input_fidelity</c>. Images come back as <c>data[].b64_json</c>; <c>usage</c> input and
/// output tokens are reported. The retired DALL·E models are not supported.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenAIImages : IImagesApi
{
    public const string ApiId = "openai-images";
    public static OpenAIImages Instance { get; } = new();
    public string Api => ApiId;

    public Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        return ImageApiShared.RunAsync(model, options, cancellationToken, async (output, options) =>
        {
            var prompt = ImageApiShared.Prompt(context);
            if (prompt.Length == 0) throw new ArgumentException("An image prompt is required.");
            var parameters = ImageParameters.From(options.Metadata);
            var references = ImageApiShared.References(context);
            var edit = references.Length != 0;
            if (parameters.Mask is not null && !edit) throw new ArgumentException("A mask needs a reference image to edit.");
            var endpoint = ImageApiShared.Endpoint(model, options);
            var fields = new JsonObject { ["model"] = model.Id, ["prompt"] = prompt };
            if (parameters.Count is { } n) fields["n"] = n;
            if (parameters.Size is { } size) fields["size"] = size;
            if (parameters.Quality is { } quality) fields["quality"] = quality;
            if (parameters.Background is { } background) fields["background"] = background;
            if (parameters.OutputFormat is { } format) fields["output_format"] = format;
            if (parameters.OutputCompression is { } compression) fields["output_compression"] = compression;
            if (parameters.Moderation is { } moderation) fields["moderation"] = moderation;
            if (parameters.User is { } user) fields["safety_identifier"] = user;
            if (edit && parameters.InputFidelity is { } fidelity) fields["input_fidelity"] = fidelity;
            parameters.MergeOptions(fields);
            Func<HttpContent> content = edit ? () =>
            {
                var form = new MultipartFormDataContent();
                foreach (var (name, value) in fields)
                    if (value is not null)
                        form.Add(new StringContent(value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString()), name);
                var index = 0;
                foreach (var image in references) form.Add(File(image), "image[]", $"reference-{++index}{Extension(image.MimeType)}");
                if (parameters.Mask is { } mask) form.Add(File(mask), "mask", "mask" + Extension(mask.MimeType));
                return form;
            } : MediaHttp.Json(fields);
            var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, edit ? "images/edits" : "images/generations"),
                MediaHttp.BearerHeaders(endpoint), content, options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
            var mime = MediaHttp.ImageMime(MediaHttp.String(response, "output_format") ?? parameters.OutputFormat);
            var blocks = ImmutableArray.CreateBuilder<ImagesContentBlock>();
            if (MediaHttp.Field(response, "data") is { ValueKind: JsonValueKind.Array } data)
                foreach (var item in data.EnumerateArray())
                    if (MediaHttp.String(item, "b64_json") is { Length: > 0 } b64) blocks.Add(new ImageContent(b64, mime));
            if (blocks.Count == 0) throw new InvalidDataException($"{model.Provider} returned no images");
            var result = output with { Output = blocks.ToImmutable() };
            if (MediaHttp.Field(response, "usage") is { ValueKind: JsonValueKind.Object } usage)
                result = result with { Usage = ImageApiShared.Usage(MediaHttp.Number(usage, "input_tokens") ?? 0, MediaHttp.Number(usage, "output_tokens") ?? 0) };
            return result;
        });
    }

    internal static ByteArrayContent File(ImageContent image)
    {
        var file = new ByteArrayContent(Convert.FromBase64String(image.Data));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(image.MimeType);
        return file;
    }

    internal static string Extension(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };
}

/// <summary>
/// OpenRouter's Image API: <c>POST {baseUrl}/images</c> with <c>model</c>, <c>prompt</c> and the documented options (<c>n</c>,
/// <c>resolution</c>, <c>aspect_ratio</c>, <c>size</c>, <c>quality</c>, <c>output_format</c>, <c>background</c>,
/// <c>output_compression</c>, <c>seed</c>, <c>user</c>), reference images as <c>input_references</c> data URLs, and
/// <see cref="ImageParameters.Options"/> merged in (for example <c>provider</c> routing). Each model endpoint supports its own subset
/// (<c>GET /images/models/{id}/endpoints</c>). Images come back as <c>data[].b64_json</c> with <c>media_type</c>; <c>usage</c> reports
/// tokens and <c>cost</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenRouterImageApi : IImagesApi
{
    public const string ApiId = "openrouter-image-api";
    public static OpenRouterImageApi Instance { get; } = new();
    public string Api => ApiId;

    public Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        return ImageApiShared.RunAsync(model, options, cancellationToken, async (output, options) =>
        {
            var prompt = ImageApiShared.Prompt(context);
            if (prompt.Length == 0) throw new ArgumentException("An image prompt is required.");
            var parameters = ImageParameters.From(options.Metadata);
            var endpoint = ImageApiShared.Endpoint(model, options);
            var body = new JsonObject { ["model"] = model.Id, ["prompt"] = prompt };
            if (parameters.Count is { } n) body["n"] = n;
            if (parameters.Resolution is { } resolution) body["resolution"] = resolution;
            if (parameters.AspectRatio is { } ratio) body["aspect_ratio"] = ratio;
            if (parameters.Size is { } size) body["size"] = size;
            if (parameters.Quality is { } quality) body["quality"] = quality;
            if (parameters.OutputFormat is { } format) body["output_format"] = format;
            if (parameters.Background is { } background) body["background"] = background;
            if (parameters.OutputCompression is { } compression) body["output_compression"] = compression;
            if (parameters.Seed is { } seed) body["seed"] = seed;
            if (parameters.User is { } user) body["user"] = user;
            var references = ImageApiShared.References(context);
            if (references.Length != 0)
                body["input_references"] = new JsonArray([.. references.Select(image => (JsonNode)new JsonObject
                {
                    ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{image.MimeType};base64,{image.Data}" },
                })]);
            parameters.MergeOptions(body);
            var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, "images"),
                MediaHttp.BearerHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
            var blocks = ImmutableArray.CreateBuilder<ImagesContentBlock>();
            if (MediaHttp.Field(response, "data") is { ValueKind: JsonValueKind.Array } data)
                foreach (var item in data.EnumerateArray())
                    if (MediaHttp.String(item, "b64_json") is { Length: > 0 } b64)
                        blocks.Add(new ImageContent(b64, MediaHttp.String(item, "media_type") ?? MediaHttp.ImageMime(parameters.OutputFormat)));
            if (blocks.Count == 0) throw new InvalidDataException($"{model.Provider} returned no images");
            var result = output with { Output = blocks.ToImmutable() };
            if (MediaHttp.Field(response, "usage") is { ValueKind: JsonValueKind.Object } usage)
            {
                double input = MediaHttp.Number(usage, "prompt_tokens") ?? 0, completion = MediaHttp.Number(usage, "completion_tokens") ?? 0;
                var cost = MediaHttp.Number(usage, "cost") ?? 0;
                result = result with { Usage = ProviderRequest.Usage(input, completion, 0, 0, input + completion, (0, cost, 0, 0, cost)) };
            }
            return result;
        });
    }
}

/// <summary>
/// Google image generation. Gemini image models: <c>POST {baseUrl}/models/{id}:generateContent</c> with the prompt and reference
/// images as parts and <c>responseModalities: ["TEXT", "IMAGE"]</c>; text parts become text blocks and <c>inlineData</c> parts image
/// blocks. Imagen models (ids starting with <c>imagen</c>): <c>POST {baseUrl}/models/{id}:predict</c> with
/// <c>{ instances: [{ prompt }], parameters: { sampleCount, aspectRatio } }</c>; they take no reference images.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class GoogleImages : IImagesApi
{
    public const string ApiId = "google-images";
    public static GoogleImages Instance { get; } = new();
    public string Api => ApiId;

    public Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        return ImageApiShared.RunAsync(model, options, cancellationToken, async (output, options) =>
        {
            var prompt = ImageApiShared.Prompt(context);
            if (prompt.Length == 0) throw new ArgumentException("An image prompt is required.");
            var parameters = ImageParameters.From(options.Metadata);
            var endpoint = ImageApiShared.Endpoint(model, options);
            var name = model.Id.StartsWith("models/", StringComparison.Ordinal) ? model.Id : "models/" + model.Id;
            return model.Id.Contains("imagen", StringComparison.OrdinalIgnoreCase)
                ? await ImagenAsync(output, model, name, endpoint, prompt, ImageApiShared.References(context), parameters, options, cancellationToken).ConfigureAwait(false)
                : await GeminiAsync(output, model, name, endpoint, context, parameters, options, cancellationToken).ConfigureAwait(false);
        });
    }

    private static async Task<AssistantImages> GeminiAsync(AssistantImages output, ImageModel model, string name, MediaEndpoint endpoint,
        ImagesContext context, ImageParameters parameters, ImagesOptions options, CancellationToken cancellationToken)
    {
        var parts = new JsonArray();
        foreach (var block in context.Input.IsDefault ? [] : context.Input)
            parts.Add(block switch
            {
                ImagesTextBlock text => new JsonObject { ["text"] = text.Text },
                ImageContent image => new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = image.Data } },
                _ => throw new ArgumentException("Unsupported image input block."),
            });
        var config = new JsonObject { ["responseModalities"] = new JsonArray("TEXT", "IMAGE") };
        if (parameters.AspectRatio is not null || parameters.Resolution is not null)
        {
            var image = new JsonObject();
            if (parameters.AspectRatio is { } ratio) image["aspectRatio"] = ratio;
            if (parameters.Resolution is { } resolution) image["imageSize"] = resolution;
            config["imageConfig"] = image;
        }
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = config,
        };
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, name + ":generateContent"),
            MediaHttp.GoogleHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        var blocks = ImmutableArray.CreateBuilder<ImagesContentBlock>();
        if (MediaHttp.Field(response, "candidates") is { ValueKind: JsonValueKind.Array } candidates && candidates.GetArrayLength() > 0 &&
            MediaHttp.Field(candidates[0], "content") is { } content && MediaHttp.Field(content, "parts") is { ValueKind: JsonValueKind.Array } answer)
            foreach (var part in answer.EnumerateArray())
            {
                if (MediaHttp.String(part, "text") is { Length: > 0 } text && MediaHttp.Field(part, "thought") is not { ValueKind: JsonValueKind.True })
                    blocks.Add(new ImagesTextBlock(text));
                else if ((MediaHttp.Field(part, "inlineData") ?? MediaHttp.Field(part, "inline_data")) is { ValueKind: JsonValueKind.Object } inline &&
                    MediaHttp.String(inline, "data") is { Length: > 0 } data)
                    blocks.Add(new ImageContent(data, MediaHttp.String(inline, "mimeType") ?? MediaHttp.String(inline, "mime_type") ?? "image/png"));
            }
        if (!blocks.Any(block => block is ImageContent))
        {
            var reason = MediaHttp.Field(response, "promptFeedback") is { } feedback ? MediaHttp.String(feedback, "blockReason") : null;
            var finish = MediaHttp.Field(response, "candidates") is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0
                ? MediaHttp.String(list[0], "finishReason") : null;
            var text = string.Concat(blocks.OfType<ImagesTextBlock>().Select(block => block.Text));
            var why = reason ?? finish;
            throw new InvalidDataException($"{model.Provider} returned no image" + (why is null ? "" : $" ({why})") +
                (text.Length > 0 ? ": " + ProviderRequest.TruncateErrorText(text, 500) : ""));
        }
        var result = output with { Output = blocks.ToImmutable() };
        if (MediaHttp.Field(response, "usageMetadata") is { ValueKind: JsonValueKind.Object } usage)
            result = result with { Usage = ImageApiShared.Usage(MediaHttp.Number(usage, "promptTokenCount") ?? 0, MediaHttp.Number(usage, "candidatesTokenCount") ?? 0) };
        return result;
    }

    private static async Task<AssistantImages> ImagenAsync(AssistantImages output, ImageModel model, string name, MediaEndpoint endpoint, string prompt,
        ImmutableArray<ImageContent> references, ImageParameters parameters, ImagesOptions options, CancellationToken cancellationToken)
    {
        if (references.Length != 0) throw new ArgumentException($"{model.Id} does not accept reference images.");
        var settings = new JsonObject { ["sampleCount"] = parameters.Count ?? 1 };
        if (parameters.AspectRatio is { } ratio) settings["aspectRatio"] = ratio;
        var body = new JsonObject { ["instances"] = new JsonArray(new JsonObject { ["prompt"] = prompt }), ["parameters"] = settings };
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, name + ":predict"),
            MediaHttp.GoogleHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        var blocks = ImmutableArray.CreateBuilder<ImagesContentBlock>();
        if (MediaHttp.Field(response, "predictions") is { ValueKind: JsonValueKind.Array } predictions)
            foreach (var prediction in predictions.EnumerateArray())
                if (MediaHttp.String(prediction, "bytesBase64Encoded") is { Length: > 0 } data)
                    blocks.Add(new ImageContent(data, MediaHttp.String(prediction, "mimeType") ?? "image/png"));
        if (blocks.Count == 0) throw new InvalidDataException($"{model.Provider} returned no images (the prompt may have been filtered)");
        return output with { Output = blocks.ToImmutable() };
    }
}
