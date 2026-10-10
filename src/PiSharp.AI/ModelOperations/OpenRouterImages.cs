// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openrouter-images.ts, the request and error
// behaviour of the OpenAI SDK client it uses (chat.completions.create with maxRetries 0, APIError messages), and
// packages/ai/src/images-api-registry.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>types.ts <c>ProviderImages</c>: one image-generation API implementation. Implementations never throw for
/// request failures; they return an error or aborted <see cref="AssistantImages"/>.</summary>
public interface IImagesApi
{
    /// <summary>The image API id this implementation serves (<c>model.api</c>).</summary>
    string Api { get; }
    Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Image generation over OpenRouter's chat completions endpoint: <c>POST {baseUrl}/chat/completions</c> with one user
/// message holding the text and image-reference parts, <c>stream: false</c> and <c>modalities</c> (<c>["image"]</c>, or
/// <c>["image", "text"]</c> for models that can also answer in text). The answer's text becomes a text block and every
/// <c>data:</c> image URL in <c>message.images</c> (string or <c>{ url }</c>) an image block; other URLs are skipped.
/// No retries unless <see cref="ModelRequestOptions.MaxRetries"/> asks for them.
/// </summary>
public sealed partial class OpenRouterImages : IImagesApi
{
    public const string ApiId = "openrouter-images";
    public static OpenRouterImages Instance { get; } = new();
    public string Api => ApiId;

    public async Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        var output = new AssistantImages(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Stop, ProviderRequest.Now(options));
        try
        {
            if (string.IsNullOrEmpty(options?.ApiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            JsonNode payload = BuildParams(model, context);
            if (options.OnPayload is { } onPayload && await onPayload(payload.DeepClone(), model, cancellationToken).ConfigureAwait(false) is { } replaced)
                payload = replaced;
            var text = ProviderRequest.Stringify(payload);
            // OpenAI SDK defaults (as the native Completions port sends them), then the merged model and option headers;
            // a null option value only removes a model header of that name.
            var headers = ProviderRequest.MergeHeaders(
                [new("accept", "application/json"), new("content-type", "application/json"), new("user-agent", "PiSharp"),
                 new("x-stainless-retry-count", "0"), new("authorization", "Bearer " + options.ApiKey)],
                ProviderRequest.Nullable(ProviderRequest.MergeHeaders(ProviderRequest.Nullable(model.Headers), options.Headers)));
            var url = new Uri(model.BaseUrl + (model.BaseUrl.EndsWith('/') ? "chat/completions" : "/chat/completions"));
            var http = ProviderRequest.Http(options);
            var (response, body) = await ProviderRequest.RetryAsync(async () =>
            {
                var (info, responseText) = await ProviderRequest.PostAsync(http, url, headers, text, options, cancellationToken,
                    error => new ProviderRequestException("Connection error.", null, []),
                    timeoutMessage: "Request timed out.").ConfigureAwait(false);
                if (!ProviderRequest.IsSuccess(info.Status)) throw ApiError(info, responseText);
                return (info, ProviderRequest.ParseJson("OpenRouter", responseText));
            }, options.MaxRetries ?? 0, options, null, cancellationToken).ConfigureAwait(false);
            if (options.OnResponse is { } onResponse) await onResponse(response, model, cancellationToken).ConfigureAwait(false);
            return Parse(output, model, body);
        }
        catch (Exception error)
        {
            return output with
            {
                StopReason = cancellationToken.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error,
                ErrorMessage = ProviderRequest.FormatError(error)
            };
        }
    }

    private static JsonObject BuildParams(ImageModel model, ImagesContext context)
    {
        var content = new JsonArray();
        foreach (var item in context.Input.IsDefault ? [] : context.Input)
            content.Add(item switch
            {
                ImagesTextBlock block => new JsonObject { ["type"] = "text", ["text"] = ProviderRequest.SanitizeSurrogates(block.Text) },
                ImageContent image => new JsonObject
                {
                    ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{image.MimeType};base64,{image.Data}" }
                },
                _ => throw new ArgumentException("Unsupported image input block.")
            });
        return new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
            ["stream"] = false,
            ["modalities"] = !model.Output.IsDefault && model.Output.Contains("text") ? new JsonArray("image", "text") : new JsonArray("image")
        };
    }

    private static AssistantImages Parse(AssistantImages output, ImageModel model, JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OpenRouter returned an unexpected response");
        if (ProviderRequest.IsString(response, "id", out var id)) output = output with { ResponseId = id };
        if (ClassifierShared.Field(response, "usage") is { ValueKind: JsonValueKind.Object } usage) output = output with { Usage = ParseUsage(usage, model) };
        if (ClassifierShared.Field(response, "choices") is not { ValueKind: JsonValueKind.Array } choices)
            throw new InvalidDataException("OpenRouter returned an unexpected response");
        if (choices.GetArrayLength() == 0) return output;
        var blocks = ImmutableArray.CreateBuilder<ImagesContentBlock>();
        if (ClassifierShared.Field(choices[0], "message") is not { ValueKind: JsonValueKind.Object } message)
            throw new InvalidDataException("OpenRouter returned an unexpected response");
        if (ProviderRequest.IsString(message, "content", out var content) && content.Length > 0) blocks.Add(new ImagesTextBlock(content));
        if (ClassifierShared.Field(message, "images") is { ValueKind: JsonValueKind.Array } images)
            foreach (var image in images.EnumerateArray())
            {
                var imageUrl = ClassifierShared.Field(image, "image_url") switch
                {
                    { ValueKind: JsonValueKind.String } text => text.GetString(),
                    { ValueKind: JsonValueKind.Object } nested when ProviderRequest.IsString(nested, "url", out var url) => url,
                    _ => null
                };
                if (imageUrl is null || !imageUrl.StartsWith("data:", StringComparison.Ordinal)) continue;
                var match = DataUrl().Match(imageUrl);
                if (!match.Success) continue;
                blocks.Add(new ImageContent(match.Groups[2].Value, match.Groups[1].Value));
            }
        return output with { Output = blocks.ToImmutable() };
    }

    /// <summary>openrouter-images.ts <c>parseUsage</c>: cached prompt tokens are cache reads, minus reported cache writes.</summary>
    private static Contracts.TokenUsage ParseUsage(JsonElement raw, ImageModel model)
    {
        static double Count(JsonElement? value) => value is { } element && ProviderRequest.FiniteNumber(element, out var number) && number != 0 ? number : 0;
        var details = ClassifierShared.Field(raw, "prompt_tokens_details");
        var promptTokens = Count(ClassifierShared.Field(raw, "prompt_tokens"));
        var reportedCached = details is { } d ? Count(ClassifierShared.Field(d, "cached_tokens")) : 0;
        var cacheWrite = details is { } w ? Count(ClassifierShared.Field(w, "cache_write_tokens")) : 0;
        var cacheRead = cacheWrite > 0 ? Math.Max(0, reportedCached - cacheWrite) : reportedCached;
        var input = Math.Max(0, promptTokens - cacheRead - cacheWrite);
        var completion = Count(ClassifierShared.Field(raw, "completion_tokens"));
        var inputCost = model.Cost.Input / 1000000 * input; var outputCost = model.Cost.Output / 1000000 * completion;
        var readCost = model.Cost.CacheRead / 1000000 * cacheRead; var writeCost = model.Cost.CacheWrite / 1000000 * cacheWrite;
        static long Tokens(double value) => (long)Math.Min(value, long.MaxValue);
        return ProviderRequest.Usage(Tokens(input), Tokens(completion), Tokens(cacheRead), Tokens(cacheWrite),
            Tokens(input + completion + cacheRead + cacheWrite),
            (inputCost, outputCost, readCost, writeCost, inputCost + outputCost + readCost + writeCost));
    }

    /// <summary>OpenAI SDK <c>APIError</c>: the message is built from the parsed body's <c>error</c> member (or the raw text
    /// when the body is not JSON), and that <c>error</c> member is the body error-body.ts reports.</summary>
    private static ProviderRequestException ApiError(ProviderResponseInfo response, string text)
    {
        JsonElement? parsed = null;
        try { using var document = JsonDocument.Parse(text); parsed = document.RootElement.Clone(); } catch (JsonException) { }
        // openai makeStatusError: an object or array body whose `error` is null or undefined is itself the error ({ error: body }).
        JsonElement? error = parsed is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } body
            ? body.ValueKind == JsonValueKind.Object && ClassifierShared.Field(body, "error") is { ValueKind: not JsonValueKind.Null } member ? member : body
            : null;
        var composed = PiSharp.AI.Protocols.ProviderShared.ProviderErrorText.OpenAIStatus(response.Status, text).Message;
        return new ProviderRequestException(composed, response.Status, response.Headers, sdkError: error);
    }

    // JavaScript's `.` excludes \n, \r, U+2028 and U+2029; `$` without the m flag is the end of the input.
    [GeneratedRegex("^data:([^;]+);base64,([^\\n\\r\\u2028\\u2029]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DataUrl();
}
