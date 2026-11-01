// PiSharp-only (no upstream Pi source): the media.* cases for PiSharp.AI's media APIs (embeddings, images, videos) and the durable media
// tools. Every provider answers through an in-process fake HTTP handler; no network or live credentials are used. The request shapes
// follow the providers' published REST documentation.
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.AI.Media;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> MediaCases() => MediaTests.Cases("media.");
}

internal static class MediaTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    /// <summary>A 1x1 PNG.</summary>
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly Dictionary<string, string> Keys = new()
    {
        ["OPENAI_API_KEY"] = "sk-openai", ["GEMINI_API_KEY"] = "g-key", ["OPENROUTER_API_KEY"] = "or-key", ["MISTRAL_API_KEY"] = "m-key",
    };

    private static MediaRegistry Registry(MediaFakeHttp http, IReadOnlyDictionary<string, string>? keys = null)
    {
        var table = keys ?? Keys;
        return new MediaRegistry(MediaAuth.Environment(name => table.GetValueOrDefault(name)), new MediaRegistryOptions { Http = http.Client });
    }

    private static MediaModel Model(MediaRegistry registry, MediaKind kind, string spec) =>
        registry.TryResolve(kind, spec, out var model, out var error) ? model : throw new InvalidOperationException(error);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly MediaOptions Fast = new() { PollIntervalMs = 0 };

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ----- embeddings -----

        yield return Case(prefix + "embeddings.openai-request-usage-cost-and-index-order", async () =>
        {
            var http = MediaFakeHttp.Json("""{"data":[{"index":1,"embedding":[0.5,0.25]},{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":10}}""");
            var registry = Registry(http);
            var result = await registry.EmbedAsync(Model(registry, MediaKind.Embedding, "openai/text-embedding-3-small"),
                new EmbeddingRequest(["a", "b"]) { Dimensions = 2 });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop reason " + result.ErrorMessage);
            var request = http.Requests.Single();
            Equal("https://api.openai.com/v1/embeddings", request.Url, "url");
            Equal("Bearer sk-openai", request.Headers["authorization"], "auth");
            var body = Json(request.Body);
            Equal("text-embedding-3-small", body.GetProperty("model").GetString(), "model");
            Equal(2, body.GetProperty("input").GetArrayLength(), "inputs");
            Equal("float", body.GetProperty("encoding_format").GetString(), "encoding");
            Equal(2, body.GetProperty("dimensions").GetInt32(), "dimensions");
            Equal(1f, result.Vectors[0][0], "first vector is input 0");
            Equal(0.25f, result.Vectors[1][1], "second vector is input 1");
            Equal(10, result.InputTokens, "tokens");
            Check(Math.Abs(result.Cost!.Value - 0.0000002) < 1e-12, "cost at $0.02 per million");
        });

        yield return Case(prefix + "embeddings.unlisted-provider-gets-a-versioned-openai-compatible-url", async () =>
        {
            var http = MediaFakeHttp.Json("""{"data":[{"embedding":[1]}]}""");
            var registry = Registry(http);
            var model = Model(registry, MediaKind.Embedding, "mistral/codestral-embed");
            Check(!model.Listed, "unlisted");
            Equal(OpenAIEmbeddings.ApiId, model.Api, "default api");
            var result = await registry.EmbedAsync(model, new EmbeddingRequest(["x"]));
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal("https://api.mistral.ai/v1/embeddings", http.Requests.Single().Url, "url gets /v1");
            Equal("Bearer m-key", http.Requests.Single().Headers["authorization"], "auth");
        });

        yield return Case(prefix + "embeddings.openrouter-model-ids-keep-their-slash", async () =>
        {
            var http = MediaFakeHttp.Json("""{"data":[{"embedding":[1]}]}""");
            var registry = Registry(http);
            var model = Model(registry, MediaKind.Embedding, "openrouter/openai/text-embedding-3-small");
            Equal("openai/text-embedding-3-small", model.Id, "id");
            await registry.EmbedAsync(model, new EmbeddingRequest(["x"]));
            Equal("https://openrouter.ai/api/v1/embeddings", http.Requests.Single().Url, "url");
            Equal("openai/text-embedding-3-small", Json(http.Requests.Single().Body).GetProperty("model").GetString(), "wire model");
        });

        yield return Case(prefix + "embeddings.google-batch-embed-contents", async () =>
        {
            var http = MediaFakeHttp.Json("""{"embeddings":[{"values":[0.1,0.2]},{"values":[0.3,0.4]}]}""");
            var registry = Registry(http);
            var result = await registry.EmbedAsync(Model(registry, MediaKind.Embedding, "google/gemini-embedding-001"),
                new EmbeddingRequest(["a", "b"]) { TaskType = "RETRIEVAL_DOCUMENT", Dimensions = 768 });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            var request = http.Requests.Single();
            Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents", request.Url, "url");
            Equal("g-key", request.Headers["x-goog-api-key"], "key header");
            Check(!request.Headers.ContainsKey("authorization"), "no bearer");
            var first = Json(request.Body).GetProperty("requests")[0];
            Equal("models/gemini-embedding-001", first.GetProperty("model").GetString(), "model");
            Equal("a", first.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString(), "text");
            Equal("RETRIEVAL_DOCUMENT", first.GetProperty("taskType").GetString(), "task type");
            Equal(768, first.GetProperty("outputDimensionality").GetInt32(), "dimensions");
            Equal(0.4f, result.Vectors[1][1], "vector");
        });

        yield return Case(prefix + "embeddings.batches-past-the-input-limit-and-merges-options", async () =>
        {
            var http = new MediaFakeHttp((request, _) =>
            {
                var count = JsonDocument.Parse(request.Body).RootElement.GetProperty("input").GetArrayLength();
                var data = string.Join(",", Enumerable.Range(0, count).Select(i => $$$"""{"index":{{{i}}},"embedding":[{{{i}}}]}"""));
                return MediaFakeHttp.Response($$$"""{"data":[{{{data}}}],"usage":{"prompt_tokens":{{{count}}}}}""");
            });
            var registry = Registry(http);
            var texts = Enumerable.Range(0, OpenAIEmbeddings.MaxInputsPerRequest + 3).Select(i => "t" + i).ToImmutableArray();
            var result = await registry.EmbedAsync(Model(registry, MediaKind.Embedding, "openrouter/openai/text-embedding-3-small"),
                new EmbeddingRequest(texts) { Options = PiSharp.Contracts.JsonData.Parse("""{"provider":{"order":["openai"]},"user":"u"}""") });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal(2, http.Requests.Count, "two requests");
            Equal(OpenAIEmbeddings.MaxInputsPerRequest, Json(http.Requests[0].Body).GetProperty("input").GetArrayLength(), "first batch");
            Equal(texts.Length, result.Vectors.Length, "all vectors");
            Equal(2f, result.Vectors[OpenAIEmbeddings.MaxInputsPerRequest + 2][0], "second batch keeps its order");
            Equal(texts.Length, result.InputTokens, "tokens summed");
            Equal("openai", Json(http.Requests[1].Body).GetProperty("provider").GetProperty("order")[0].GetString(), "options merged");
        });

        yield return Case(prefix + "embeddings.retries-429-then-succeeds", async () =>
        {
            var http = new MediaFakeHttp((request, count) => count == 1
                ? MediaFakeHttp.Response("""{"error":{"message":"slow down"}}""", HttpStatusCode.TooManyRequests, ("retry-after-ms", "0"))
                : MediaFakeHttp.Response("""{"data":[{"embedding":[1]}]}"""));
            var registry = Registry(http);
            var result = await registry.EmbedAsync(Model(registry, MediaKind.Embedding, "openai/text-embedding-3-small"), new EmbeddingRequest(["x"]));
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal(2, http.Requests.Count, "two attempts");
        });

        yield return Case(prefix + "errors.provider-message-and-not-configured", async () =>
        {
            var http = MediaFakeHttp.Json("""{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""", HttpStatusCode.Unauthorized);
            var registry = Registry(http);
            var failed = await registry.EmbedAsync(Model(registry, MediaKind.Embedding, "openai/text-embedding-3-small"), new EmbeddingRequest(["x"]));
            Equal(ModelOperationStopReason.Error, failed.StopReason, "error");
            Equal("401 Incorrect API key provided", failed.ErrorMessage, "message");
            var unconfigured = Registry(http, new Dictionary<string, string>());
            var missing = await unconfigured.EmbedAsync(Model(unconfigured, MediaKind.Embedding, "openai/text-embedding-3-small"), new EmbeddingRequest(["x"]));
            Equal("Provider is not configured: openai (set OPENAI_API_KEY, or log in)", missing.ErrorMessage, "not configured");
            Equal(1, http.Requests.Count, "no request without auth");
            Equal(0, (await unconfigured.GetAvailableAsync()).Length, "nothing available");
        });

        yield return Case(prefix + "resolve.specs", () =>
        {
            var registry = Registry(MediaFakeHttp.Json("{}"));
            Equal("openai/gpt-image-1", Model(registry, MediaKind.Image, "gpt-image-1").Spec, "bare id");
            Equal("google/veo-3.0-generate-001", Model(registry, MediaKind.Video, "veo-3.0-generate-001").Spec, "bare video id");
            Check(!registry.TryResolve(MediaKind.Image, "anthropic/claude", out _, out var noApi), "no image api");
            Equal("Provider anthropic has no image API", noApi, "reason");
            Check(!registry.TryResolve(MediaKind.Video, "nope", out _, out var unknown), "unknown bare id");
            Check(unknown.Contains("provider/model"), "hint: " + unknown);
            // OpenAI shut down its Videos API (Sora 2) on 2026-09-24.
            Check(!registry.TryResolve(MediaKind.Video, "openai/sora-2", out _, out var noVideo), "no OpenAI video");
            Equal("Provider openai has no video API", noVideo, "sora reason");
            Equal(GoogleVideos.ApiId, Model(registry, MediaKind.Video, "google/veo-9").Api, "unlisted Veo model");
            return Task.CompletedTask;
        });

        // ----- images -----

        yield return Case(prefix + "images.openai-generations", async () =>
        {
            var http = MediaFakeHttp.Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}],"output_format":"webp","usage":{"input_tokens":12,"output_tokens":300}}""");
            var registry = Registry(http);
            var result = await registry.GenerateImagesAsync(Model(registry, MediaKind.Image, "openai/gpt-image-1"),
                new ImagesContext([new ImagesTextBlock("a fox")]), new ImageParameters { Count = 1, Size = "1024x1024", Quality = "low", OutputFormat = "webp" });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            var request = http.Requests.Single();
            Equal("https://api.openai.com/v1/images/generations", request.Url, "url");
            var body = Json(request.Body);
            Equal("gpt-image-1", body.GetProperty("model").GetString(), "model");
            Equal("a fox", body.GetProperty("prompt").GetString(), "prompt");
            Equal("1024x1024", body.GetProperty("size").GetString(), "size");
            Equal("low", body.GetProperty("quality").GetString(), "quality");
            Equal("webp", body.GetProperty("output_format").GetString(), "format");
            Check(!body.TryGetProperty("response_format", out _), "gpt-image takes no response_format");
            var image = (ImageContent)result.Output.Single();
            Equal("image/webp", image.MimeType, "mime from output_format");
            Equal(300d, result.Usage!.Output, "usage");
        });

        yield return Case(prefix + "images.openai-edits-send-reference-images-as-multipart", async () =>
        {
            var http = MediaFakeHttp.Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}]}""");
            var registry = Registry(http);
            var result = await registry.GenerateImagesAsync(Model(registry, MediaKind.Image, "openai/gpt-image-1"),
                new ImagesContext([new ImagesTextBlock("make it blue"), new ImageContent(Png, "image/png")]));
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            var request = http.Requests.Single();
            Equal("https://api.openai.com/v1/images/edits", request.Url, "url");
            Check(request.Headers["content-type"].StartsWith("multipart/form-data", StringComparison.Ordinal), "multipart");
            Check(request.Body.Contains("name=\"image[]\"", StringComparison.Ordinal) || request.Body.Contains("name=image[]", StringComparison.Ordinal), "image[] part");
            Check(request.Body.Contains("make it blue", StringComparison.Ordinal), "prompt part");
        });

        yield return Case(prefix + "images.openai-sends-every-documented-option", async () =>
        {
            var http = MediaFakeHttp.Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}]}""");
            var registry = Registry(http);
            var model = Model(registry, MediaKind.Image, "openai/gpt-image-1.5");
            var parameters = new ImageParameters
            {
                Background = "transparent", OutputFormat = "webp", OutputCompression = 80, Moderation = "low", User = "user-hash", InputFidelity = "high",
                Options = PiSharp.Contracts.JsonData.Parse("""{"partial_images":0,"quality":"high"}"""),
            };
            await registry.GenerateImagesAsync(model, new ImagesContext([new ImagesTextBlock("logo")]), parameters with { Quality = "low" });
            var body = Json(http.Requests[0].Body);
            Equal("transparent", body.GetProperty("background").GetString(), "background");
            Equal(80, body.GetProperty("output_compression").GetInt32(), "compression");
            Equal("low", body.GetProperty("moderation").GetString(), "moderation");
            Equal("user-hash", body.GetProperty("safety_identifier").GetString(), "safety identifier");
            Equal("high", body.GetProperty("quality").GetString(), "options replace a field");
            Equal(0, body.GetProperty("partial_images").GetInt32(), "options add a field");
            Check(!body.TryGetProperty("input_fidelity", out _), "input_fidelity is for edits only");
            await registry.GenerateImagesAsync(model, new ImagesContext([new ImagesTextBlock("edit"), new ImageContent(Png, "image/png")]),
                parameters with { Mask = new ImageContent(Png, "image/png") });
            var edit = http.Requests[1].Body;
            foreach (var part in new[] { "input_fidelity", "mask", "background", "output_compression", "safety_identifier", "partial_images" })
                Check(edit.Contains($"name={part}", StringComparison.Ordinal) || edit.Contains($"name=\"{part}\"", StringComparison.Ordinal), part + " part");
            var noMask = await registry.GenerateImagesAsync(model, new ImagesContext([new ImagesTextBlock("x")]), new ImageParameters { Mask = new ImageContent(Png, "image/png") });
            Equal("A mask needs a reference image to edit.", noMask.ErrorMessage, "mask without an image");
            Check(!registry.TryResolve(MediaKind.Image, "dall-e-3", out _, out _), "DALL-E is retired");
        });

        yield return Case(prefix + "images.gemini-parts", async () =>
        {
            var http = MediaFakeHttp.Json($$$"""
                {"candidates":[{"content":{"parts":[{"text":"planning","thought":true},{"text":"Here you go"},{"inlineData":{"mimeType":"image/png","data":"{{{Png}}}"}}]}}],
                 "usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1290}}
                """);
            var registry = Registry(http);
            var result = await registry.GenerateImagesAsync(Model(registry, MediaKind.Image, "google/gemini-2.5-flash-image"),
                new ImagesContext([new ImagesTextBlock("a cat"), new ImageContent(Png, "image/png")]), new ImageParameters { AspectRatio = "16:9" });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            var request = http.Requests.Single();
            Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-image:generateContent", request.Url, "url");
            var body = Json(request.Body);
            var parts = body.GetProperty("contents")[0].GetProperty("parts");
            Equal("a cat", parts[0].GetProperty("text").GetString(), "text part");
            Equal("image/png", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString(), "image part");
            Equal("16:9", body.GetProperty("generationConfig").GetProperty("imageConfig").GetProperty("aspectRatio").GetString(), "aspect ratio");
            Equal(2, result.Output.Length, "thought skipped");
            Equal("Here you go", ((ImagesTextBlock)result.Output[0]).Text, "text");
            Check(result.Output[1] is ImageContent, "image");
        });

        yield return Case(prefix + "images.gemini-without-image-reports-the-block-reason", async () =>
        {
            var http = MediaFakeHttp.Json("""{"promptFeedback":{"blockReason":"SAFETY"}}""");
            var registry = Registry(http);
            var result = await registry.GenerateImagesAsync(Model(registry, MediaKind.Image, "google/gemini-2.5-flash-image"), new ImagesContext([new ImagesTextBlock("x")]));
            Equal("google returned no image (SAFETY)", result.ErrorMessage, "message");
        });

        yield return Case(prefix + "images.imagen-predict", async () =>
        {
            var http = MediaFakeHttp.Json($$$"""{"predictions":[{"bytesBase64Encoded":"{{{Png}}}","mimeType":"image/png"},{"bytesBase64Encoded":"{{{Png}}}","mimeType":"image/png"}]}""");
            var registry = Registry(http);
            var result = await registry.GenerateImagesAsync(Model(registry, MediaKind.Image, "google/imagen-4.0-generate-001"),
                new ImagesContext([new ImagesTextBlock("a lighthouse")]), new ImageParameters { Count = 2, AspectRatio = "9:16" });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal("https://generativelanguage.googleapis.com/v1beta/models/imagen-4.0-generate-001:predict", http.Requests.Single().Url, "url");
            var body = Json(http.Requests.Single().Body);
            Equal(2, body.GetProperty("parameters").GetProperty("sampleCount").GetInt32(), "count");
            Equal("9:16", body.GetProperty("parameters").GetProperty("aspectRatio").GetString(), "ratio");
            Equal(2, result.Output.Length, "two images");
        });

        yield return Case(prefix + "images.openrouter-catalog-model-uses-the-image-api", async () =>
        {
            var http = MediaFakeHttp.Json($$$"""{"created":1,"data":[{"b64_json":"{{{Png}}}","media_type":"image/webp"}],"usage":{"prompt_tokens":10,"completion_tokens":1290,"cost":0.039}}""");
            var registry = Registry(http);
            var catalog = new ImageModel("openai/gpt-image-1", "GPT Image 1", OpenRouterImages.ApiId, "openrouter", "https://openrouter.ai/api/v1",
                ["text", "image"], ModelCost.Free, ["image"]);
            registry.AddModels([MediaCatalog.FromImageModel(catalog)]);
            var model = Model(registry, MediaKind.Image, "openrouter/openai/gpt-image-1");
            Equal(OpenRouterImageApi.ApiId, model.Api, "catalog entries move to the Image API");
            var result = await registry.GenerateImagesAsync(model, new ImagesContext([new ImagesTextBlock("sticker"), new ImageContent(Png, "image/png")]),
                new ImageParameters
                {
                    Count = 2, Background = "transparent", OutputFormat = "webp", OutputCompression = 90, AspectRatio = "3:2", Resolution = "1K",
                    Quality = "high", Seed = 7, User = "u1", Options = PiSharp.Contracts.JsonData.Parse("""{"provider":{"only":["openai"]}}"""),
                });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            var request = http.Requests.Single();
            Equal("https://openrouter.ai/api/v1/images", request.Url, "url");
            Equal("Bearer or-key", request.Headers["authorization"], "auth");
            var body = Json(request.Body);
            Equal("openai/gpt-image-1", body.GetProperty("model").GetString(), "model");
            Equal("sticker", body.GetProperty("prompt").GetString(), "prompt");
            Equal("transparent", body.GetProperty("background").GetString(), "background");
            Equal("webp", body.GetProperty("output_format").GetString(), "format");
            Equal(90, body.GetProperty("output_compression").GetInt32(), "compression");
            Equal("3:2", body.GetProperty("aspect_ratio").GetString(), "ratio");
            Equal("1K", body.GetProperty("resolution").GetString(), "resolution");
            Equal(7, body.GetProperty("seed").GetInt32(), "seed");
            Equal(2, body.GetProperty("n").GetInt32(), "n");
            Equal("u1", body.GetProperty("user").GetString(), "user");
            Equal("openai", body.GetProperty("provider").GetProperty("only")[0].GetString(), "provider routing");
            Check(body.GetProperty("input_references")[0].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "reference");
            Equal("image/webp", ((ImageContent)result.Output.Single()).MimeType, "media_type");
            Equal<double>(0.039, (double)result.Usage!.Cost.Total, "cost");
        });

        // ----- videos -----

        yield return Case(prefix + "videos.resume-polls-without-submitting", async () =>
        {
            const string operation = "models/veo-3.0-generate-001/operations/op9";
            var statuses = new List<VideoJobStatus>();
            var http = new MediaFakeHttp((request, _) => request.Method == "POST"
                ? MediaFakeHttp.Response("{}", HttpStatusCode.InternalServerError)
                : request.Url.Contains("/files/", StringComparison.Ordinal) ? MediaFakeHttp.Bytes([9], "video/mp4")
                : MediaFakeHttp.Response("""{"done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://generativelanguage.googleapis.com/v1beta/files/f9:download?alt=media"}}]}}}"""));
            var registry = Registry(http);
            var result = await registry.GenerateVideoAsync(Model(registry, MediaKind.Video, "google/veo-3.0-generate-001"), new VideoRequest("x"), Fast,
                new VideoJobHooks
                {
                    ResumeJobId = operation,
                    OnSubmittedAsync = (_, _) => throw new InvalidOperationException("must not submit"),
                    OnStatusAsync = (status, _) => { statuses.Add(status); return ValueTask.CompletedTask; },
                });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Check(http.Requests.All(request => request.Method == "GET"), "no submission");
            Equal("https://generativelanguage.googleapis.com/v1beta/" + operation, http.Requests[0].Url, "polls the resumed operation");
            Equal(VideoJobState.Completed, statuses.Single().State, "status reported");
        });

        yield return Case(prefix + "videos.google-operation-and-redirected-download-drops-the-key", async () =>
        {
            const string operation = "models/veo-3.0-generate-001/operations/op1";
            var http = new MediaFakeHttp((request, count) => request.Url switch
            {
                "https://generativelanguage.googleapis.com/v1beta/models/veo-3.0-generate-001:predictLongRunning" =>
                    MediaFakeHttp.Response($$$"""{"name":"{{{operation}}}"}"""),
                "https://generativelanguage.googleapis.com/v1beta/" + operation => MediaFakeHttp.Response(count < 3 ? $$$"""{"name":"{{{operation}}}"}"""
                    : """{"done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://generativelanguage.googleapis.com/v1beta/files/f1:download?alt=media"}}]}}}"""),
                "https://generativelanguage.googleapis.com/v1beta/files/f1:download?alt=media" => MediaFakeHttp.Redirect("https://storage.example.com/signed/f1"),
                "https://storage.example.com/signed/f1" => MediaFakeHttp.Bytes([7, 7], "video/mp4"),
                _ => MediaFakeHttp.Response("{}", HttpStatusCode.NotFound),
            });
            var registry = Registry(http);
            var result = await registry.GenerateVideoAsync(Model(registry, MediaKind.Video, "google/veo-3.0-generate-001"),
                new VideoRequest("a city at night") { AspectRatio = "16:9", NegativePrompt = "text", Image = new ImageContent(Png, "image/png") }, Fast);
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal(operation, result.JobId, "operation name is the job id");
            Equal(2, result.Video!.Data.Length, "bytes");
            var submit = Json(http.Requests[0].Body);
            Equal("a city at night", submit.GetProperty("instances")[0].GetProperty("prompt").GetString(), "prompt");
            Equal("image/png", submit.GetProperty("instances")[0].GetProperty("image").GetProperty("inlineData").GetProperty("mimeType").GetString(), "first frame");
            Equal("16:9", submit.GetProperty("parameters").GetProperty("aspectRatio").GetString(), "ratio");
            Equal("text", submit.GetProperty("parameters").GetProperty("negativePrompt").GetString(), "negative prompt");
            var download = http.Requests.Single(request => request.Url.Contains("/files/f1", StringComparison.Ordinal));
            Equal("g-key", download.Headers["x-goog-api-key"], "the API host gets the key");
            var signed = http.Requests.Single(request => request.Url.StartsWith("https://storage.example.com", StringComparison.Ordinal));
            Check(!signed.Headers.ContainsKey("x-goog-api-key"), "the other host does not");
        });

        yield return Case(prefix + "videos.failed-and-filtered-operations-keep-the-job-id", async () =>
        {
            var http = new MediaFakeHttp((request, _) => request.Method == "POST" ? MediaFakeHttp.Response("""{"name":"operations/o1"}""")
                : MediaFakeHttp.Response("""{"name":"operations/o1","done":true,"error":{"code":3,"message":"Prompt blocked"}}"""));
            var registry = Registry(http);
            var failed = await registry.GenerateVideoAsync(Model(registry, MediaKind.Video, "google/veo-3.0-generate-001"), new VideoRequest("x"), Fast);
            Equal(ModelOperationStopReason.Error, failed.StopReason, "error");
            Equal("Prompt blocked", failed.ErrorMessage, "message");
            Equal("operations/o1", failed.JobId, "job id");
            var google = new MediaFakeHttp((request, _) => request.Method == "POST" ? MediaFakeHttp.Response("""{"name":"operations/o"}""")
                : MediaFakeHttp.Response("""{"done":true,"response":{"generateVideoResponse":{"raiMediaFilteredCount":1,"raiMediaFilteredReasons":["Unsafe content"]}}}"""));
            var filteredRegistry = Registry(google);
            var filtered = await filteredRegistry.GenerateVideoAsync(Model(filteredRegistry, MediaKind.Video, "google/veo-3.0-generate-001"), new VideoRequest("x"), Fast);
            Equal("The video was filtered: Unsafe content", filtered.ErrorMessage, "filtered");
        });

        yield return Case(prefix + "videos.max-wait-ends-the-request", async () =>
        {
            var http = new MediaFakeHttp((request, _) => request.Method == "POST" ? MediaFakeHttp.Response("""{"name":"operations/o3"}""")
                : MediaFakeHttp.Response("""{"name":"operations/o3"}"""));
            var registry = Registry(http);
            var result = await registry.GenerateVideoAsync(Model(registry, MediaKind.Video, "google/veo-3.0-generate-001"), new VideoRequest("x"),
                new MediaOptions { PollIntervalMs = 0, MaxWaitMs = 0 });
            Equal(ModelOperationStopReason.Error, result.StopReason, "error");
            Check(result.ErrorMessage!.StartsWith("Video job operations/o3 did not finish", StringComparison.Ordinal), result.ErrorMessage);
            Equal("operations/o3", result.JobId, "job id kept");
        });

        // ----- tools -----

        yield return Case(prefix + "tools.output-paths-and-stems", () =>
        {
            SeqEqual([".pi/media/s.png"], MediaTools.OutputPaths(null, ".pi/media", "s", [".png"]), "default");
            SeqEqual(["out/s-1.png", "out/s-2.jpg"], MediaTools.OutputPaths("out/", ".pi/media", "s", [".png", ".jpg"]), "directory");
            SeqEqual(["art/logo.png", "art/logo-2.png"], MediaTools.OutputPaths("art/logo.png", ".pi/media", "s", [".png", ".png"]), "file");
            var stem = MediaTools.Stem("image", "A red fox, jumping over the lazy dog!");
            Check(Regex.IsMatch(stem, "^image-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}-a-red-fox-jumping-over$"), stem);
            // Calls in the same second with the same prompt (variants, subagents) still get files of their own.
            Check(stem != MediaTools.Stem("image", "A red fox, jumping over the lazy dog!"), "a random id per call");
            return Task.CompletedTask;
        });

        yield return Case(prefix + "tools.a-sent-request-without-a-saved-result-is-not-sent-again", async () =>
        {
            var cwd = Temp("media-sent");
            var api = new MediaToolApi(new LocalExecutionEnv(cwd));
            var http = new MediaFakeHttp((_, _) =>
            {
                Check(api.Memos.ContainsKey("request-sent"), "marked as sent before it goes out");
                return MediaFakeHttp.Response($$$"""{"data":[{"b64_json":"{{{Png}}}"}]}""");
            });
            var options = new MediaToolsOptions { Media = Registry(http) };
            var image = MediaTools.CreateImageTool(options);
            var args = JsonValue.Parse("""{"prompt":"a fox","model":"openai/gpt-image-1"}""");
            await image.Execute(args, api, Context);
            Equal(1, http.Requests.Count, "sent once");
            // The process stopped after the request went out and before its result was saved: the rerun reports that instead of paying again.
            api.Memos.Remove("result");
            var error = await ThrowsAsync<InvalidOperationException>(() => image.Execute(args, api, Context), "interrupted image");
            Check(error.Message.StartsWith("The image request was sent before the process stopped", StringComparison.Ordinal), error.Message);
            foreach (var tool in new[] { MediaTools.CreateSpeechTool(options), MediaTools.CreateMusicTool(options) })
            {
                var stopped = new MediaToolApi(new LocalExecutionEnv(cwd));
                stopped.Memos["request-sent"] = true;
                await ThrowsAsync<InvalidOperationException>(() => tool.Execute(JsonValue.Parse("""{"text":"hi","prompt":"piano"}"""), stopped, Context), tool.Name);
            }
            // A video submission whose job id was not saved is not submitted again (one whose job id was saved resumes the job).
            var video = new MediaToolApi(new LocalExecutionEnv(cwd));
            video.Memos["request-sent"] = true;
            var videoError = await ThrowsAsync<InvalidOperationException>(() => MediaTools.CreateVideoTool(options).Execute(
                JsonValue.Parse("""{"prompt":"waves","model":"google/veo-3.0-generate-001"}"""), video, Context), "interrupted video");
            Check(videoError.Message.StartsWith("The video request was sent", StringComparison.Ordinal), videoError.Message);
            Equal(1, http.Requests.Count, "nothing sent again");
        });

        yield return Case(prefix + "tools.a-sent-transcription-without-a-saved-transcript-is-not-sent-again", async () =>
        {
            var cwd = Temp("media-transcribe-sent");
            await File.WriteAllBytesAsync(Path.Combine(cwd, "talk.mp3"), [0x49, 0x44, 0x33, 0]);
            var api = new MediaToolApi(new LocalExecutionEnv(cwd));
            var http = new MediaFakeHttp((_, _) =>
            {
                Check(api.Memos.ContainsKey("request-sent"), "marked as sent before it goes out");
                return MediaFakeHttp.Response("""{"text":"hi there","segments":[{"start":0,"end":1.2,"text":"hi there"}]}""");
            });
            var tool = MediaTools.CreateTranscribeTool(new MediaToolsOptions { Media = Registry(http) });
            var args = JsonValue.Parse("""{"path":"talk.mp3","model":"openai/whisper-1","timestamps":true,"output_path":"notes/talk.json"}""");
            var first = await tool.Execute(args, api, Context);
            Equal(1, http.Requests.Count, "sent once");
            var file = Path.Combine(cwd, "notes", "talk.json");
            var written = await File.ReadAllTextAsync(file);
            var saved = Json(written);
            Check(saved.GetProperty("details").TryGetProperty("segments", out _) && !saved.TryGetProperty("path", out _), written);
            // The process stopped after the transcript was saved and before its file was written: the rerun writes it from the saved transcript.
            File.Delete(file);
            var replay = await tool.Execute(args, api, Context);
            Equal(1, http.Requests.Count, "replayed without a request");
            Equal(Text(first), Text(replay), "replayed text");
            Equal(first.StructuredOutput!.ToString(), replay.StructuredOutput!.ToString(), "replayed structured output");
            Equal(written, await File.ReadAllTextAsync(file), "file written again from the saved transcript");
            // The process stopped after the request went out and before its transcript was saved: the rerun reports that instead of paying again.
            api.Memos.Remove("result");
            var error = await ThrowsAsync<InvalidOperationException>(() => tool.Execute(args, api, Context), "interrupted transcription");
            Check(error.Message.StartsWith("The transcription request was sent before the process stopped", StringComparison.Ordinal), error.Message);
            Check(error.Message.EndsWith("call the tool again to transcribe the audio anew.", StringComparison.Ordinal), error.Message);
            Equal(1, http.Requests.Count, "nothing sent again");
        });

        yield return Case(prefix + "tools.embed-rerank-and-decide-reuse-a-saved-result-and-resend-an-unsaved-one", async () =>
        {
            var cwd = Temp("media-lookups");
            var http = new MediaFakeHttp((request, _) => new Uri(request.Url).AbsolutePath switch
            {
                "/v1/embeddings" => MediaFakeHttp.Response("""{"data":[{"embedding":[1,0]},{"embedding":[0.6,0.8]}],"usage":{"prompt_tokens":3}}"""),
                "/api/v1/rerank" => MediaFakeHttp.Response("""{"results":[{"index":1,"relevance_score":0.7}],"usage":{"cost":0.002}}"""),
                _ => MediaFakeHttp.Response("""{"answers":{"ok":{"type":"noul","noul":0.2}},"usage":{"cost":0.00001}}"""),
            });
            var options = new MediaToolsOptions { Media = Registry(http) };
            var vectors = Path.Combine(cwd, "vectors.json");
            var calls = new (ToolRegistration Tool, string Arguments, string Expected)[]
            {
                (MediaTools.CreateEmbedTool(options),
                    """{"input":["cats"],"query":"kittens","model":"openai/text-embedding-3-small","output_path":"vectors.json"}""", "0.6000  [0] cats"),
                (MediaTools.CreateRerankTool(options), """{"query":"q","documents":["a","b"],"model":"openrouter/cohere/rerank-4-fast"}""", "0.7000  [1] b"),
                (MediaTools.CreateDecideTool(options),
                    """{"state":"x","questions":{"ok":{"type":"noul","instructions":"ok?"}},"model":"openrouter/typesafe/jev-1.14"}""", "\"noul\":0.2"),
            };
            foreach (var (tool, json, expected) in calls)
            {
                var api = new MediaToolApi(new LocalExecutionEnv(cwd));
                var args = JsonValue.Parse(json);
                var sent = http.Requests.Count;
                var first = await tool.Execute(args, api, Context);
                Equal(sent + 1, http.Requests.Count, tool.Name + " sent once");
                Check(Text(first).Contains(expected, StringComparison.Ordinal), Text(first));
                Check(api.Memos.ContainsKey("result") && !api.Memos.ContainsKey("request-sent"), tool.Name + ": result saved, request not guarded");
                // The process stopped after the result was saved: the rerun reuses it (and writes embed's output file from it).
                File.Delete(vectors);
                var replay = await tool.Execute(args, api, Context);
                Equal(sent + 1, http.Requests.Count, tool.Name + " replayed without a request");
                Equal(Text(first), Text(replay), tool.Name + " replayed text");
                Equal(first.StructuredOutput!.ToString(), replay.StructuredOutput!.ToString(), tool.Name + " replayed structured output");
                if (tool.Name == MediaTools.EmbedToolName) Check(File.Exists(vectors), "vectors written again from the saved result");
                // The process stopped after the request went out and before its result was saved: a cheap, read-only call simply runs again.
                api.Memos.Remove("result");
                var rerun = await tool.Execute(args, api, Context);
                Equal(sent + 2, http.Requests.Count, tool.Name + " sent again");
                Equal(Text(first), Text(rerun), tool.Name + " rerun text");
            }
        });

        yield return Case(prefix + "tools.defaults-that-cannot-be-saved-still-apply-to-the-session", async () =>
        {
            var defaults = new MediaDefaults { Persist = (_, _, _, _) => throw new InvalidOperationException("The defaults apply to this session but were not saved: disk full") };
            var http = MediaFakeHttp.Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}]}""");
            var options = new MediaToolsOptions { Media = Registry(http), Defaults = defaults };
            var api = new MediaToolApi(new LocalExecutionEnv(Temp("media-defaults-unsaved")));
            var error = await ThrowsAsync<InvalidOperationException>(() => MediaTools.CreateDefaultsTool(options).Execute(
                JsonValue.Parse("""{"kind":"image","model":"openai/gpt-image-1-mini"}"""), api, Context), "unsaved");
            Check(error.Message.Contains("disk full", StringComparison.Ordinal), error.Message);
            Equal("openai/gpt-image-1-mini", defaults.Get(MediaKind.Image).Model, "applied before the save");
            await MediaTools.CreateImageTool(options).Execute(JsonValue.Parse("""{"prompt":"x"}"""), api, Context);
            Equal("gpt-image-1-mini", Json(http.Requests.Single().Body).GetProperty("model").GetString(), "the session uses it");
        });

        yield return Case(prefix + "tools.generate-image-saves-files-and-replays-from-the-memo", async () =>
        {
            var cwd = Temp("media-image");
            await File.WriteAllBytesAsync(Path.Combine(cwd, "ref.png"), Convert.FromBase64String(Png));
            var http = MediaFakeHttp.Json($$$"""{"data":[{"b64_json":"{{{Png}}}"},{"b64_json":"{{{Png}}}"}]}""");
            var tool = MediaTools.CreateImageTool(new MediaToolsOptions { Media = Registry(http) });
            var api = new MediaToolApi(new LocalExecutionEnv(cwd));
            var args = JsonValue.Parse("""{"prompt":"a fox","model":"openai/gpt-image-1","count":2,"reference_images":"ref.png","output_path":"art/fox.png"}""");
            var result = await tool.Execute(tool.PrepareArguments!(args), api, Context);
            Equal("images/edits", new Uri(http.Requests.Single().Url).AbsolutePath[4..], "reference image sent as an edit");
            Check(File.Exists(Path.Combine(cwd, "art", "fox.png")) && File.Exists(Path.Combine(cwd, "art", "fox-2.png")), "both files saved");
            var text = Text(result);
            Check(text.StartsWith("Saved 2 images from openai/gpt-image-1:\n- art/fox.png (image/png, ", StringComparison.Ordinal), text);
            Equal(2, ((JsonArrayValue)result.Output!).Count(block => block is JsonObjectValue o && o["type"] is JsonStringValue { Value: "image" }), "images shown");
            var files = (JsonArrayValue)((JsonObjectValue)result.StructuredOutput!)["files"];
            Equal(2, files.Count(), "structured files");
            // A rerun after a crash finds the memo and does not pay again.
            var replay = await tool.Execute(args, api, Context);
            Equal(1, http.Requests.Count, "no second request");
            Equal(text, Text(replay), "replayed text");
        });

        yield return Case(prefix + "tools.generate-image-reports-provider-errors", async () =>
        {
            var http = MediaFakeHttp.Json("""{"error":{"message":"Your request was rejected by the safety system"}}""", HttpStatusCode.BadRequest);
            var tool = MediaTools.CreateImageTool(new MediaToolsOptions { Media = Registry(http) });
            var error = await ThrowsAsync<InvalidOperationException>(() => tool.Execute(JsonValue.Parse("""{"prompt":"x","model":"openai/gpt-image-1"}"""),
                new MediaToolApi(new LocalExecutionEnv(Temp("media-error"))), Context), "provider error");
            Equal("openai/gpt-image-1: 400 Your request was rejected by the safety system", error.Message, "message");
        });

        yield return Case(prefix + "tools.generate-video-memoizes-the-job-and-resumes-it", async () =>
        {
            var cwd = Temp("media-video");
            var http = new MediaFakeHttp((request, _) => request.Method == "POST" ? MediaFakeHttp.Response("""{"name":"operations/new"}""")
                : request.Url == "https://generativelanguage.googleapis.com/v1beta/operations/old"
                    ? MediaFakeHttp.Response("""{"done":true,"response":{"generateVideoResponse":{"generatedSamples":[{"video":{"uri":"https://generativelanguage.googleapis.com/v1beta/files/old:download?alt=media"}}]}}}""")
                : request.Url.Contains("/files/old", StringComparison.Ordinal) ? MediaFakeHttp.Bytes([1, 2, 3, 4], "video/mp4")
                : MediaFakeHttp.Response("{}", HttpStatusCode.NotFound));
            var tool = MediaTools.CreateVideoTool(new MediaToolsOptions { Media = Registry(http), VideoPollIntervalMs = 0 });
            var api = new MediaToolApi(new LocalExecutionEnv(cwd));
            // A crash after the submission left the job id in the memo.
            api.Memos["video-job"] = "operations/old";
            var result = await tool.Execute(JsonValue.Parse("""{"prompt":"waves","model":"google/veo-3.0-generate-001","output_path":"clips/"}"""), api, Context);
            Check(http.Requests.All(request => request.Method == "GET"), "resumed without submitting");
            var summary = (JsonObjectValue)result.StructuredOutput!;
            Equal("operations/old", ((JsonStringValue)summary["jobId"]).Value, "job id");
            var path = ((JsonStringValue)((JsonObjectValue)((JsonArrayValue)summary["files"]).First())["path"]).Value;
            Check(path.StartsWith("clips/video-", StringComparison.Ordinal) && path.EndsWith("-waves.mp4", StringComparison.Ordinal), path);
            Equal(4, (await File.ReadAllBytesAsync(Path.Combine(cwd, path))).Length, "saved bytes");
            Check(api.Details.Count > 0, "progress details reported");
        });

        yield return Case(prefix + "tools.embed-ranks-by-cosine-and-writes-vectors", async () =>
        {
            var cwd = Temp("media-embed");
            // The query first, then the inputs: "cats" matches the query, "stocks" does not.
            var http = MediaFakeHttp.Json("""{"data":[{"embedding":[1,0]},{"embedding":[0,1]},{"embedding":[0.9,0.1]}],"usage":{"prompt_tokens":6}}""");
            var tool = MediaTools.CreateEmbedTool(new MediaToolsOptions
            {
                Media = Registry(http),
                Defaults = new MediaDefaults(new Dictionary<MediaKind, MediaDefault> { [MediaKind.Embedding] = new("openai/text-embedding-3-small", JsonObjectValue.Empty) }),
            });
            var args = tool.PrepareArguments!(JsonValue.Parse("""{"input":["stocks","cats"],"query":"kittens","output_path":"vectors.json"}"""));
            var result = await tool.Execute(args, new MediaToolApi(new LocalExecutionEnv(cwd)), Context);
            Equal(3, Json(http.Requests.Single().Body).GetProperty("input").GetArrayLength(), "query embedded with the inputs");
            var text = Text(result);
            Check(text.Contains("Ranked by similarity to the query:\n0.9939  [1] cats\n0.0000  [0] stocks", StringComparison.Ordinal), text);
            var structured = (JsonObjectValue)result.StructuredOutput!;
            Equal(2d, ((JsonNumberValue)structured["dimensions"]).Value, "dimensions");
            Equal(2, ((JsonArrayValue)structured["vectors"]).Count(), "query vector left out");
            var file = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cwd, "vectors.json"))).RootElement;
            Equal("cats", file.GetProperty("items")[1].GetProperty("text").GetString(), "file items");
            Equal(0.9, file.GetProperty("items")[1].GetProperty("embedding")[0].GetDouble(), "float written as its shortest decimal");
        });

        yield return Case(prefix + "tools.embed-and-rerank-reject-blank-entries-instead-of-shifting-positions", async () =>
        {
            var http = MediaFakeHttp.Json("{}");
            var options = new MediaToolsOptions { Media = Registry(http) };
            var api = new MediaToolApi(null);
            var embed = await ThrowsAsync<InvalidOperationException>(() => MediaTools.CreateEmbedTool(options).Execute(
                JsonValue.Parse("""{"input":["alpha","  ","beta"],"model":"openai/text-embedding-3-small"}"""), api, Context), "blank input");
            Equal("input[1] must be a non-empty string", embed.Message, "embed");
            var rerank = await ThrowsAsync<InvalidOperationException>(() => MediaTools.CreateRerankTool(options).Execute(
                JsonValue.Parse("""{"query":"q","documents":["alpha","beta",3],"model":"openrouter/cohere/rerank-4-fast"}"""), api, Context), "non-string document");
            Equal("documents[2] must be a non-empty string", rerank.Message, "rerank");
            Equal(0, http.Requests.Count, "nothing sent");
        });

        yield return Case(prefix + "tools.list-models-and-defaults", async () =>
        {
            var tool = MediaTools.CreateModelsTool(new MediaToolsOptions
            {
                Media = Registry(MediaFakeHttp.Json("{}"), new Dictionary<string, string> { ["GEMINI_API_KEY"] = "g" }),
                Defaults = new MediaDefaults(new Dictionary<MediaKind, MediaDefault>
                {
                    [MediaKind.Video] = new("google/veo-3.0-fast-generate-001", (JsonObjectValue)JsonValue.Parse("""{"resolution":"720p"}""")),
                }),
            });
            var text = Text(await tool.Execute(JsonValue.Parse("{}"), new MediaToolApi(null), Context));
            Check(text.Contains("image models:\n  google/gemini-2.5-flash-image (Gemini 2.5 Flash Image) [default] [reference images]", StringComparison.Ordinal), text);
            Check(text.Contains("google/veo-3.0-fast-generate-001 (Veo 3 Fast) [default]", StringComparison.Ordinal), text);
            Check(text.Contains("default options: {\"resolution\":\"720p\"}", StringComparison.Ordinal), text);
            Check(!text.Contains("openai/", StringComparison.Ordinal), "unconfigured providers are not listed");
            Check(text.Contains("google/gemini-embedding-001 (Gemini Embedding) [default] [3072 dims]", StringComparison.Ordinal), text);
        });

        // ----- OpenRouter video, speech, transcription, music, rerank and decisions -----

        yield return Case(prefix + "videos.openrouter-submit-poll-download-with-every-option", async () =>
        {
            var http = new MediaFakeHttp((request, count) => (request.Method, new Uri(request.Url).AbsolutePath) switch
            {
                ("POST", "/api/v1/videos") => MediaFakeHttp.Response("""{"id":"gen-vid-1-abcdefghijklmnopqrst","polling_url":"x","status":"pending"}""", HttpStatusCode.Accepted),
                ("GET", "/api/v1/videos/gen-vid-1-abcdefghijklmnopqrst") => MediaFakeHttp.Response(count < 3
                    ? """{"id":"gen-vid-1-abcdefghijklmnopqrst","polling_url":"x","status":"in_progress"}"""
                    : """{"id":"gen-vid-1-abcdefghijklmnopqrst","polling_url":"x","status":"completed","usage":{"cost":0.8}}"""),
                ("GET", "/api/v1/videos/gen-vid-1-abcdefghijklmnopqrst/content") => MediaFakeHttp.Bytes([5, 6, 7], "video/mp4"),
                _ => MediaFakeHttp.Response("{}", HttpStatusCode.NotFound),
            });
            var registry = Registry(http);
            var model = Model(registry, MediaKind.Video, "openrouter/google/veo-3.1-fast");
            var result = await registry.GenerateVideoAsync(model, new VideoRequest("a fox")
            {
                Image = new ImageContent(Png, "image/png"), LastFrame = new ImageContent(Png, "image/png"),
                References = [new MediaReference(MediaReferenceKind.Audio, "https://example.com/beat.mp3")],
                DurationSeconds = 8, AspectRatio = "16:9", Resolution = "1080p", GenerateAudio = true, Seed = 3,
                Options = PiSharp.Contracts.JsonData.Parse("""{"provider":{"options":{"google-vertex":{"enhancePrompt":true}}}}"""),
            }, Fast);
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal(3, result.Video!.Data.Length, "bytes");
            Equal(0.8, result.Cost, "cost");
            var body = Json(http.Requests[0].Body);
            Equal("google/veo-3.1-fast", body.GetProperty("model").GetString(), "model");
            Equal(8, body.GetProperty("duration").GetInt32(), "duration");
            Equal("1080p", body.GetProperty("resolution").GetString(), "resolution");
            Check(body.GetProperty("generate_audio").GetBoolean(), "audio");
            Equal("first_frame", body.GetProperty("frame_images")[0].GetProperty("frame_type").GetString(), "first frame");
            Equal("last_frame", body.GetProperty("frame_images")[1].GetProperty("frame_type").GetString(), "last frame");
            Equal("audio_url", body.GetProperty("input_references")[0].GetProperty("type").GetString(), "audio reference");
            Check(body.GetProperty("provider").GetProperty("options").TryGetProperty("google-vertex", out _), "provider options");
            Equal("https://openrouter.ai/api/v1/videos/gen-vid-1-abcdefghijklmnopqrst/content?index=0", http.Requests[^1].Url, "download");
        });

        yield return Case(prefix + "videos.openrouter-failed-job-reports-its-error", async () =>
        {
            var http = new MediaFakeHttp((request, _) => request.Method == "POST" ? MediaFakeHttp.Response("""{"id":"j","status":"pending","polling_url":"x"}""")
                : MediaFakeHttp.Response("""{"id":"j","status":"failed","polling_url":"x","error":"Content policy violation"}"""));
            var registry = Registry(http);
            var result = await registry.GenerateVideoAsync(Model(registry, MediaKind.Video, "openrouter/kwaivgi/kling-v3.0-std"), new VideoRequest("x"), Fast);
            Equal("Content policy violation", result.ErrorMessage, "error");
            Equal("j", result.JobId, "job id");
        });

        yield return Case(prefix + "speech.openai-default-voice-and-openrouter-turns-and-cloning", async () =>
        {
            var http = new MediaFakeHttp((_, _) => MediaFakeHttp.Bytes([0x49, 0x44, 0x33, 1, 2], "audio/mpeg"));
            var registry = Registry(http);
            var result = await registry.SpeakAsync(Model(registry, MediaKind.Speech, "openai/gpt-4o-mini-tts"),
                new SpeechRequest("Hello") { Instructions = "cheerful", Format = "mp3", Speed = 1.25 });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal("audio/mpeg", result.Audio!.MimeType, "mime");
            Equal("https://api.openai.com/v1/audio/speech", http.Requests[0].Url, "openai url");
            var openai = Json(http.Requests[0].Body);
            Equal("alloy", openai.GetProperty("voice").GetString(), "default voice");
            Equal("cheerful", openai.GetProperty("instructions").GetString(), "instructions");
            Equal(1.25, openai.GetProperty("speed").GetDouble(), "speed");
            await registry.SpeakAsync(Model(registry, MediaKind.Speech, "openrouter/google/gemini-3.1-flash-tts-preview"), new SpeechRequest("")
            {
                Turns = [new SpeechTurn("Hi", "Kore"), new SpeechTurn("Hello", "Puck", "softly")],
                References = [MediaReference.FromBytes(MediaReferenceKind.Audio, [1, 2, 3], "audio/wav")],
            });
            Equal("https://openrouter.ai/api/v1/audio/speech", http.Requests[1].Url, "openrouter url");
            var turns = Json(http.Requests[1].Body);
            Equal("Puck", turns.GetProperty("input")[1].GetProperty("voice").GetString(), "turn voice");
            Check(!turns.TryGetProperty("voice", out _), "no default voice for turns");
            Equal("wav", turns.GetProperty("input_references")[0].GetProperty("input_audio").GetProperty("format").GetString(), "clone format");
        });

        yield return Case(prefix + "transcription.openai-multipart", async () =>
        {
            var http = MediaFakeHttp.Json("""{"text":"hello"}""");
            var registry = Registry(http);
            var result = await registry.TranscribeAsync(Model(registry, MediaKind.Transcription, "openai/whisper-1"),
                new TranscriptionRequest([1, 2, 3], "talk.mp3") { Prompt = "PiSharp", Timestamps = true });
            Equal("hello", result.Text, "text");
            var request = http.Requests.Single();
            Equal("https://api.openai.com/v1/audio/transcriptions", request.Url, "url");
            Check(request.Headers["content-type"].StartsWith("multipart/form-data", StringComparison.Ordinal), "multipart");
            foreach (var part in new[] { "verbose_json", "timestamp_granularities[]", "PiSharp", "talk.mp3", "whisper-1" })
                Check(request.Body.Contains(part, StringComparison.Ordinal), part);
        });

        yield return Case(prefix + "transcription.openrouter-json-with-timestamps-diarize-keyterms-and-options", async () =>
        {
            var http = MediaFakeHttp.Json("""{"text":"hello world","language":"en","duration":1.5,"words":[{"word":"hello","speaker":"A"}],"usage":{"cost":0.001}}""");
            var registry = Registry(http);
            var result = await registry.TranscribeAsync(Model(registry, MediaKind.Transcription, "openrouter/elevenlabs/scribe-v2"),
                new TranscriptionRequest([1, 2, 3], "talk.wav")
                {
                    Language = "en", Diarize = true, Timestamps = true, KeyTerms = ["PiSharp"],
                    Options = PiSharp.Contracts.JsonData.Parse("""{"provider":{"zdr":true}}"""),
                });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal("hello world", result.Text, "text");
            Equal(0.001, result.Cost, "cost");
            Check(result.Data!.Value.TryGetProperty("words", out _) && !result.Data.Value.TryGetProperty("text", out _), "data keeps details, not text");
            var request = http.Requests.Single();
            Equal("https://openrouter.ai/api/v1/audio/transcriptions", request.Url, "url");
            var body = Json(request.Body);
            Equal("AQID", body.GetProperty("input_audio").GetProperty("data").GetString(), "base64 audio");
            Equal("wav", body.GetProperty("input_audio").GetProperty("format").GetString(), "format");
            Equal("verbose_json", body.GetProperty("response_format").GetString(), "verbose");
            Equal("word", body.GetProperty("timestamp_granularities")[1].GetString(), "granularities");
            Check(body.GetProperty("diarize").GetBoolean(), "diarize");
            Equal("PiSharp", body.GetProperty("keyterms")[0].GetString(), "keyterms");
            Check(body.GetProperty("provider").GetProperty("zdr").GetBoolean(), "options merged");
        });

        yield return Case(prefix + "music.chat-audio-output", async () =>
        {
            var first = Convert.ToBase64String([0x49, 0x44, 0x33]);
            var second = Convert.ToBase64String([4, 5, 6, 7]);
            var http = new MediaFakeHttp((_, _) => MediaFakeHttp.Sse(
                $$$$"""{"choices":[{"delta":{"audio":{"data":"{{{{first}}}}","transcript":"Verse "}}}]}""",
                $$$$"""{"choices":[{"delta":{"audio":{"data":"{{{{second}}}}","transcript":"one"}}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"cost":0.04}}"""));
            var registry = Registry(http);
            var result = await registry.ComposeAsync(Model(registry, MediaKind.Music, "openrouter/google/lyria-3-clip-preview"),
                new MusicRequest("lofi beat") { Seed = 9, Images = [new ImageContent(Png, "image/png")] });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal("audio/mpeg", result.Audio!.MimeType, "sniffed mp3");
            Equal("Verse one", result.Text, "transcript");
            Equal(7, result.Audio.Data.Length, "chunks joined");
            Equal(0.04, result.Cost, "cost from the last chunk");
            var body = Json(http.Requests.Single().Body);
            Check(body.GetProperty("stream").GetBoolean(), "streamed");
            Equal("https://openrouter.ai/api/v1/chat/completions", http.Requests.Single().Url, "url");
            Equal("audio", body.GetProperty("modalities")[1].GetString(), "audio modality");
            Equal(9, body.GetProperty("seed").GetInt32(), "seed");
            Equal("image_url", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString(), "image part");
        });

        yield return Case(prefix + "music.base64-split-at-any-boundary-is-joined", async () =>
        {
            var full = Convert.ToBase64String([0x49, 0x44, 0x33, 4, 5, 6, 7, 8, 9]);
            var http = new MediaFakeHttp((_, _) => MediaFakeHttp.Sse(
                $$$$"""{"choices":[{"delta":{"audio":{"data":"{{{{full[..5]}}}}"}}}]}""",
                $$$$"""{"choices":[{"delta":{"audio":{"data":"{{{{full[5..]}}}}"}}}]}"""));
            var registry = Registry(http);
            var result = await registry.ComposeAsync(Model(registry, MediaKind.Music, "openrouter/google/lyria-3-clip-preview"), new MusicRequest("x"));
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal(9, result.Audio!.Data.Length, "joined before decoding");
            Equal(9, OpenRouterChatAudio.DecodePieces([Convert.ToBase64String([1, 2, 3, 4]), Convert.ToBase64String([5, 6, 7, 8, 9])]).Length,
                "independently padded pieces");
        });

        yield return Case(prefix + "rerank.orders-by-score", async () =>
        {
            var http = MediaFakeHttp.Json("""{"results":[{"index":0,"relevance_score":0.1},{"index":2,"relevance_score":0.9}],"usage":{"cost":0.002}}""");
            var registry = Registry(http);
            var result = await registry.RerankAsync(Model(registry, MediaKind.Rerank, "openrouter/cohere/rerank-4-fast"),
                new RerankRequest("capital of France", ["Berlin", "Madrid", "Paris"]) { TopN = 2 });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal(2, result.Ranking[0].Index, "best first");
            Equal("https://openrouter.ai/api/v1/rerank", http.Requests.Single().Url, "url");
            Equal(2, Json(http.Requests.Single().Body).GetProperty("top_n").GetInt32(), "top_n");
        });

        yield return Case(prefix + "decisions.posts-to-the-origin-and-returns-answers", async () =>
        {
            var http = MediaFakeHttp.Json("""{"answers":{"spam":{"type":"noul","noul":0.93}},"usage":{"cost":0.00001}}""");
            var registry = Registry(http);
            var result = await registry.DecideAsync(Model(registry, MediaKind.Decision, "openrouter/typesafe/jev-1.14"),
                new DecisionRequest(PiSharp.Contracts.JsonData.Parse("\"BUY NOW!!!\""),
                    PiSharp.Contracts.JsonData.Parse("""{"spam":{"type":"noul","instructions":"Is this spam?"}}""")));
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop " + result.ErrorMessage);
            Equal("https://openrouter.ai/api/alpha/decisions", http.Requests.Single().Url, "origin path");
            Equal(0.93, result.Data!.Value.GetProperty("spam").GetProperty("noul").GetDouble(), "answer");
            Equal("BUY NOW!!!", Json(http.Requests.Single().Body).GetProperty("state").GetString(), "state passed through");
        });

        yield return Case(prefix + "discovery.lists-openrouter-models-by-output-modality", async () =>
        {
            var http = MediaFakeHttp.Json("""{"data":[{"id":"a/video-1","name":"V","architecture":{"input_modalities":["text"],"output_modalities":["video"]}},{"id":"b/tts","architecture":{"output_modalities":["speech"]}}]}""");
            var registry = Registry(http);
            var videos = await registry.DiscoverAsync(MediaKind.Video);
            Equal("openrouter/a/video-1", videos.Single().Spec, "video");
            Equal("b/tts", (await registry.DiscoverAsync(MediaKind.Speech)).Single().Id, "speech");
            Equal(1, http.Requests.Count, "cached");
            Equal("https://openrouter.ai/api/v1/models?output_modalities=all", http.Requests[0].Url, "url");
        });

        // ----- defaults -----

        yield return Case(prefix + "tools.defaults-merge-under-call-arguments-and-persist", async () =>
        {
            var saved = new List<(MediaKind Kind, MediaDefault? Value, string Scope)>();
            var defaults = new MediaDefaults { Persist = (kind, value, scope, _) => { saved.Add((kind, value, scope)); return Task.CompletedTask; } };
            var http = MediaFakeHttp.Json($$$"""{"data":[{"b64_json":"{{{Png}}}"}]}""");
            var options = new MediaToolsOptions { Media = Registry(http), Defaults = defaults };
            var set = MediaTools.CreateDefaultsTool(options);
            var api = new MediaToolApi(new LocalExecutionEnv(Temp("media-defaults")));
            var text = Text(await set.Execute(JsonValue.Parse("""{"kind":"images","model":"openai/gpt-image-1-mini","options":{"background":"transparent","output_format":"png","quality":"low"}}"""), api, Context));
            Check(text.StartsWith("Saved the image defaults (global): model openai/gpt-image-1-mini", StringComparison.Ordinal), text);
            Equal(MediaKind.Image, saved.Single().Kind, "persisted kind");
            await set.Execute(JsonValue.Parse("""{"kind":"image","options":{"quality":null,"output_compression":50},"scope":"project"}"""), api, Context);
            Equal("project", saved[^1].Scope, "scope");
            var now = defaults.Get(MediaKind.Image);
            Check(!now.Options.ContainsKey("quality") && now.Options.ContainsKey("output_compression") && now.Model == "openai/gpt-image-1-mini", now.Options.ToString());
            var image = MediaTools.CreateImageTool(options);
            await image.Execute(JsonValue.Parse("""{"prompt":"x","output_format":"webp"}"""), api, Context);
            var body = Json(http.Requests.Single().Body);
            Equal("gpt-image-1-mini", body.GetProperty("model").GetString(), "default model");
            Equal("transparent", body.GetProperty("background").GetString(), "default option");
            Equal("webp", body.GetProperty("output_format").GetString(), "call overrides default");
            Equal(50, body.GetProperty("output_compression").GetInt32(), "merged option");
            var error = await ThrowsAsync<InvalidOperationException>(() => set.Execute(JsonValue.Parse("""{"kind":"video","model":"openai/sora-2"}"""), api, Context), "bad model");
            Equal("Provider openai has no video API", error.Message, "validated");
            await set.Execute(JsonValue.Parse("""{"kind":"image","clear":true}"""), api, Context);
            Check(defaults.Get(MediaKind.Image).IsEmpty && saved[^1].Value is null, "cleared");
        });

        yield return Case(prefix + "tools.specialized-tools-are-deferred-and-run", async () =>
        {
            var http = new MediaFakeHttp((request, _) => new Uri(request.Url).AbsolutePath switch
            {
                "/api/v1/rerank" => MediaFakeHttp.Response("""{"results":[{"index":1,"relevance_score":0.7}]}"""),
                "/api/alpha/decisions" => MediaFakeHttp.Response("""{"answers":{"ok":{"type":"noul","noul":0.2}}}"""),
                "/api/v1/audio/speech" => MediaFakeHttp.Bytes([0x49, 0x44, 0x33, 0], "audio/mpeg"),
                "/api/v1/audio/transcriptions" => MediaFakeHttp.Response("""{"text":"hi there"}"""),
                _ => MediaFakeHttp.Sse("""{"choices":[{"delta":{"audio":{"data":"UklGRgAAAABXQVZF"}}}]}"""),
            });
            var options = new MediaToolsOptions { Media = Registry(http, new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "or" }) };
            var extension = MediaTools.CreateExtension(options);
            var deferred = extension.Tools!.Where(tool => tool.Deferred == true).Select(tool => tool.Name).Order().ToList();
            SeqEqual([MediaTools.DecideToolName, MediaTools.MusicToolName, MediaTools.SpeechToolName, MediaTools.RerankToolName, MediaTools.TranscribeToolName], deferred, "deferred");
            var cwd = Temp("media-specialized");
            await File.WriteAllBytesAsync(Path.Combine(cwd, "talk.mp3"), [0x49, 0x44, 0x33, 0]);
            // Each call is a tool task of its own, with its own memos.
            MediaToolApi Api() => new(new LocalExecutionEnv(cwd));
            Check(Text(await MediaTools.CreateRerankTool(options).Execute(JsonValue.Parse("""{"query":"q","documents":["a","b"]}"""), Api(), Context))
                .Contains("0.7000  [1] b", StringComparison.Ordinal), "rerank");
            Check(Text(await MediaTools.CreateDecideTool(options).Execute(JsonValue.Parse("""{"state":{"x":1},"questions":{"ok":{"type":"noul","instructions":"ok?"}}}"""), Api(), Context))
                .Contains("\"noul\":0.2", StringComparison.Ordinal), "decide");
            var speech = await MediaTools.CreateSpeechTool(options).Execute(JsonValue.Parse("""{"text":"Hello there","output_path":"voice/hello.mp3"}"""), Api(), Context);
            Check(File.Exists(Path.Combine(cwd, "voice", "hello.mp3")), "speech saved: " + Text(speech));
            var transcript = await MediaTools.CreateTranscribeTool(options).Execute(JsonValue.Parse("""{"path":"talk.mp3","output_path":"talk.txt"}"""), Api(), Context);
            Equal("hi there", await File.ReadAllTextAsync(Path.Combine(cwd, "talk.txt")), "transcript file");
            Check(Text(transcript).Contains("hi there", StringComparison.Ordinal), "transcript text");
            var music = await MediaTools.CreateMusicTool(new MediaToolsOptions { Media = options.Media }).Execute(
                JsonValue.Parse("""{"prompt":"calm piano","seed":4294967295}"""), new MediaToolApi(new LocalExecutionEnv(cwd)), Context);
            Check(Text(music).Contains("(audio/wav,", StringComparison.Ordinal), Text(music));
            var musicRequest = http.Requests.Single(request => request.Url.EndsWith("/chat/completions", StringComparison.Ordinal));
            Equal(4294967295L, Json(musicRequest.Body).GetProperty("seed").GetInt64(), "64-bit seed");
        });

        // ----- through the Harness -----

        yield return Case(prefix + "harness.model-calls-generate-image-and-a-tool-composes-embed", async () =>
        {
            var cwd = Temp("media-harness");
            var http = new MediaFakeHttp((request, _) => request.Url.EndsWith("/embeddings", StringComparison.Ordinal)
                ? MediaFakeHttp.Response("""{"data":[{"embedding":[3,4]}]}""")
                : MediaFakeHttp.Response($$$"""{"data":[{"b64_json":"{{{Png}}}"}]}"""));
            var chat = ChatTests.Setup();
            chat.Registry.Install(MediaTools.CreateExtension(new MediaToolsOptions { Media = Registry(http) }));
            // A workflow step: a tool that calls embed as a nested call and reads its structured output.
            chat.Registry.Install(Define.Extension(new Extension
            {
                Name = "pipeline",
                Tools =
                [
                    Define.Tool(new ToolRegistration
                    {
                        Name = "pipeline",
                        Description = "Embeds a fixed text through the embed tool",
                        Execute = async (_, api, context) =>
                        {
                            var nested = await api.ExecuteToolAsync(MediaTools.EmbedToolName,
                                (JsonObjectValue)JsonValue.Parse("""{"input":["hello"],"model":"openai/text-embedding-3-small"}"""), context);
                            if (nested.IsError) throw new InvalidOperationException("embed failed");
                            var dimensions = ((JsonNumberValue)((JsonObjectValue)nested.StructuredOutput!)["dimensions"]).Value;
                            return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"dimensions {dimensions}")) };
                        },
                    }),
                ],
            }));
            chat.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall(MediaTools.ImageToolName, (JsonObjectValue)JsonValue.Parse("""{"prompt":"a logo","model":"openai/gpt-image-1"}"""), "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxToolCall("pipeline", JsonObjectValue.Empty, "c2")], stopReason: "toolUse"),
                FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), chat, (_, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(cwd)));
            var offered = (await root.AgentAsync(Context)).Tools.Select(tool => tool.Name).ToList();
            foreach (var name in new[] { MediaTools.ImageToolName, MediaTools.VideoToolName, MediaTools.EmbedToolName, MediaTools.ModelsToolName })
                Check(offered.Contains(name), name + " offered");
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var results = (await AllEntries(root)).Where(entry => entry.Kind == "pi.tool-result")
                .Select(entry => (JsonObjectValue)entry.Model![0]).ToList();
            var imageText = string.Concat(((JsonArrayValue)results[0]["content"]).OfType<JsonObjectValue>()
                .Where(block => block["type"] is JsonStringValue { Value: "text" }).Select(block => ((JsonStringValue)block["text"]).Value));
            Check(imageText.StartsWith("Saved 1 image from openai/gpt-image-1:\n- .pi/media/image-", StringComparison.Ordinal), imageText);
            Check(Directory.GetFiles(Path.Combine(cwd, ".pi", "media"), "*.png").Length == 1, "image saved under the media directory");
            var pipelineText = ((JsonStringValue)((JsonObjectValue)((JsonArrayValue)results[1]["content"]).First())["text"]).Value;
            Equal("dimensions 2", pipelineText, "nested call structured output");
            await harness.CloseAsync(Context);
        });
    }

    private static string Text(ToolExecutionResult result) => string.Join("\n", (result.Output ?? JsonArrayValue.Empty).OfType<JsonObjectValue>()
        .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => ((JsonStringValue)part["text"]).Value));

    private static void SeqEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
}

/// <summary>A tool api with an environment, memos and recorded details; everything else throws.</summary>
internal sealed class MediaToolApi(IExecutionEnv? env) : DelegatingToolExecutionApi(DispatchProxy.Create<IToolExecutionApi, ToolApiStub>())
{
    public Dictionary<string, JsonValue> Memos { get; } = [];
    public List<JsonValue> Details { get; } = [];
    public override TaskId TaskId => new(1);
    public override ConversationId ConversationId => new(1);
    public override string CallId => "call";
    public override IExecutionEnv? Env => env;
    public override Task<Agent> AgentAsync(DurableContext context) => Task.FromResult(FakeToolApi.NoModelAgent);
    public override Task DetailsAsync(JsonValue value, DurableContext context) { Details.Add(value); return Task.CompletedTask; }
    public override Task<JsonValue?> MemoAsync(string name, DurableContext context) => Task.FromResult(Memos.GetValueOrDefault(name));
    public override Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context)
    {
        if (!Memos.TryGetValue(name, out var winner)) Memos[name] = winner = candidate;
        return Task.FromResult(winner);
    }
}

/// <summary>A recorded request: method, URL, lower-cased headers (content headers included) and the body as text.</summary>
internal sealed record MediaRecorded(string Method, string Url, ImmutableDictionary<string, string> Headers, string Body);

/// <summary>A fake HTTP endpoint: records every request and answers through a function of the request and its 1-based count.</summary>
internal sealed class MediaFakeHttp(Func<MediaRecorded, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly object gate = new();
    public List<MediaRecorded> Requests { get; } = [];
    public HttpClient Client => new(this, disposeHandler: false);

    public static MediaFakeHttp Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new((_, _) => Response(json, status));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(pair => !pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            .ToImmutableDictionary(pair => pair.Key.ToLowerInvariant(), pair => string.Join(", ", pair.Value));
        var recorded = new MediaRecorded(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, body);
        int count;
        lock (gate) { Requests.Add(recorded); count = Requests.Count; }
        return respond(recorded, count);
    }

    public static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage Bytes(byte[] data, string mediaType)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events.Select(data => $"data: {data}\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    public static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
        response.Headers.Location = new Uri(location);
        return response;
    }
}
