using System.Net;
using System.Text.Json.Nodes;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

// test/openrouter-images.test.ts (there against a mocked OpenAI SDK; here against fake HTTP with the request pinned) and
// the OpenAI SDK request and APIError behaviour the adapter relies on.
internal static partial class Program
{
    private static readonly ImageModel GeminiImage = Image("""
        {"type":"image","id":"google/gemini-3.1-flash-image-preview","name":"Gemini 3.1 Flash Image Preview","api":"openrouter-images","provider":"openrouter","baseUrl":"https://openrouter.ai/api/v1","input":["text","image"],"output":["text","image"],"cost":{"input":0.015,"output":0.03,"cacheRead":0,"cacheWrite":0},"headers":{"HTTP-Referer":"https://example.com"}}
        """);
    private static readonly ImageModel Flux = Image("""
        {"type":"image","id":"black-forest-labs/flux.2-pro","name":"FLUX.2 Pro","api":"openrouter-images","provider":"openrouter","baseUrl":"https://openrouter.ai/api/v1","input":["text","image"],"output":["image"],"cost":{"input":0.015,"output":0.03,"cacheRead":0,"cacheWrite":0}}
        """);
    private static ImagesContext DogPrompt => new([new ImagesTextBlock("Generate a dog")]);
    private const string ImageResponse = """{"id":"img-1","usage":{"prompt_tokens":12,"completion_tokens":34,"prompt_tokens_details":{"cached_tokens":0}},"choices":[{"message":{"content":"Here is your image.","images":[{"image_url":"data:image/png;base64,ZmFrZS1wbmc="}]}}]}""";

    private static IEnumerable<(string, Func<Task>)> ImagesCases() =>
    [
        ("images.openrouter-text-and-images-with-pinned-request", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json(ImageResponse));
            var output = await OpenRouterImages.Instance.GenerateImagesAsync(GeminiImage, DogPrompt, new ImagesOptions { ApiKey = "test", Http = http.Client });
            Equal(ModelOperationStopReason.Stop, output.StopReason, output.ErrorMessage ?? "stop");
            Equal("img-1", output.ResponseId, "response id");
            Equal(new ImagesTextBlock("Here is your image."), output.Output[0], "text block");
            Equal(new ImageContent("ZmFrZS1wbmc=", "image/png"), output.Output[1], "image block");
            Equal(2, output.Output.Length, "blocks");
            var request = http.Requests[0];
            Equal("https://openrouter.ai/api/v1/chat/completions", request.Url, "url");
            Names(["accept", "authorization", "content-type", "http-referer", "user-agent", "x-stainless-retry-count"], request.Headers.Keys.Order(StringComparer.Ordinal), "headers");
            Equal("Bearer test", request.Headers["authorization"], "authorization");
            Equal("https://example.com", request.Headers["http-referer"], "model header");
            Body("""{"model":"google/gemini-3.1-flash-image-preview","messages":[{"role":"user","content":[{"type":"text","text":"Generate a dog"}]}],"stream":false,"modalities":["image","text"]}""",
                request.Body, "images");
            Check(output.Usage is { Input: 12, Output: 34, CacheRead: 0, CacheWrite: 0, TotalTokens: 46 }, "usage");
            Close(0.015 / 1000000 * 12, (double)output.Usage!.Cost.Input, "input cost");
            Close(0.015 / 1000000 * 12 + 0.03 / 1000000 * 34, (double)output.Usage.Cost.Total, "total cost");
        }),
        ("images.image-only-models-reference-images-and-url-forms", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"id":"img-2","usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":30,"cache_write_tokens":10}},"choices":[{"message":{"content":null,"images":[{"image_url":{"url":"data:image/webp;base64,AAAA"}},{"image_url":"https://cdn.example/image.png"},{"image_url":"data:image/png;base64,"},{"image_url":"data:image/png,raw"},{},{"image_url":"data:image/jpeg;base64,BBBB"}]}},{"message":{"content":"ignored"}}]}"""));
            var context = new ImagesContext([new ImagesTextBlock("Make it blue"), new ImageContent("cmVk", "image/png")]);
            var output = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, context, new ImagesOptions { ApiKey = "test", Http = http.Client });
            Body("""{"model":"black-forest-labs/flux.2-pro","messages":[{"role":"user","content":[{"type":"text","text":"Make it blue"},{"type":"image_url","image_url":{"url":"data:image/png;base64,cmVk"}}]}],"stream":false,"modalities":["image"]}""",
                http.Requests[0].Body, "image-only");
            // Only base64 data URLs become image blocks; the first choice alone is read.
            Equal(2, output.Output.Length, "blocks");
            Equal(new ImageContent("AAAA", "image/webp"), output.Output[0], "object form");
            Equal(new ImageContent("BBBB", "image/jpeg"), output.Output[1], "string form");
            Check(output.Usage is { Input: 70, Output: 5, CacheRead: 20, CacheWrite: 10, TotalTokens: 105 }, "cache-aware usage");
            var empty = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions
            { ApiKey = "test", Http = FakeHttp.Always(() => FakeHttp.Json("""{"choices":[]}""")).Client });
            Equal(ModelOperationStopReason.Stop, empty.StopReason, "no choices"); Equal(0, empty.Output.Length, "no output"); Equal(null, empty.ResponseId, "no id");
        }),
        ("images.http-errors-follow-the-sdk-messages", async () =>
        {
            async Task<string?> Error(HttpResponseMessage response) => (await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt,
                new ImagesOptions { ApiKey = "test", Http = FakeHttp.Always(() => response).Client })).ErrorMessage;
            Equal("""400: {"message":"Bad model","code":400}""", await Error(FakeHttp.Json("""{"error":{"message":"Bad model","code":400}}""", HttpStatusCode.BadRequest)), "json error object");
            Equal("502 Gateway down", await Error(FakeHttp.Text("Gateway down", HttpStatusCode.BadGateway)), "text body");
            Equal("500 status code (no body)", await Error(FakeHttp.Text("", HttpStatusCode.InternalServerError)), "empty body");
            Equal("500 status code (no body)", await Error(FakeHttp.Json("""{"message":"x"}""", HttpStatusCode.InternalServerError)), "no error member");
            Equal("400 \"bad\"", await Error(FakeHttp.Json("""{"error":"bad"}""", HttpStatusCode.BadRequest)), "string error member");
            var connection = new FakeHttp((_, _, _) => throw new HttpRequestException("refused"));
            var failed = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions { ApiKey = "test", Http = connection.Client });
            Equal("Connection error.", failed.ErrorMessage, "connection"); Equal(ModelOperationStopReason.Error, failed.StopReason, "stop");
            var noKey = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions { Http = connection.Client });
            Equal("No API key for provider: openrouter", noKey.ErrorMessage, "no key");
        }),
        ("images.no-retries-unless-requested", async () =>
        {
            FakeHttp Flaky() => new((_, attempt, _) => Task.FromResult(attempt == 1
                ? FakeHttp.Text("busy", HttpStatusCode.InternalServerError, "text/plain", ("retry-after-ms", "0")) : FakeHttp.Json(ImageResponse)));
            var once = Flaky();
            var failed = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions { ApiKey = "test", Http = once.Client });
            Equal(1, once.Requests.Count, "default"); Equal("500 busy", failed.ErrorMessage, "first failure");
            var twice = Flaky();
            var retried = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions { ApiKey = "test", Http = twice.Client, MaxRetries = 1 });
            Equal(2, twice.Requests.Count, "retried"); Equal(ModelOperationStopReason.Stop, retried.StopReason, "stop");
        }),
        ("images.abort-hooks-and-surrogate-sanitizing", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var aborted = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt,
                new ImagesOptions { ApiKey = "test", Http = FakeHttp.Always(() => FakeHttp.Json(ImageResponse)).Client }, cancelled.Token);
            Equal(ModelOperationStopReason.Aborted, aborted.StopReason, "aborted"); Equal("Request aborted", aborted.ErrorMessage, "abort message");
            var http = FakeHttp.Always(() => FakeHttp.Json(ImageResponse)); var statuses = new List<int>();
            var lone = "a" + (char)0xD800 + "b" + (char)0xDC00 + "c";
            var output = await OpenRouterImages.Instance.GenerateImagesAsync(GeminiImage, new ImagesContext([new ImagesTextBlock(lone)]), new ImagesOptions
            {
                ApiKey = "test", Http = http.Client, Headers = [new("http-referer", null), new("X-Title", "PiSharp tests")],
                OnPayload = (payload, _, _) => { var replaced = payload.AsObject(); replaced["provider"] = new JsonObject { ["sort"] = "price" }; return ValueTask.FromResult<JsonNode?>(replaced); },
                OnResponse = (response, _, _) => { statuses.Add(response.Status); return ValueTask.CompletedTask; }
            });
            Equal(ModelOperationStopReason.Stop, output.StopReason, "stop");
            Equal("abc", http.Requests[0].Json.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString(), "unpaired surrogates dropped");
            Equal("price", http.Requests[0].Json.GetProperty("provider").GetProperty("sort").GetString(), "payload replaced");
            Check(!http.Requests[0].Headers.ContainsKey("http-referer"), "null removes the model header");
            Equal("PiSharp tests", http.Requests[0].Headers["x-title"], "option header");
            Names(["200"], statuses.Select(status => status.ToString()), "response hook");
            var keepAuth = FakeHttp.Always(() => FakeHttp.Json(ImageResponse));
            await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions { ApiKey = "test", Http = keepAuth.Client, Headers = [new("Authorization", null)] });
            Equal("Bearer test", keepAuth.Requests[0].Headers["authorization"], "a null option cannot remove the SDK authorization");
        }),
        ("images.direct-dispatch-through-the-api-registry", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json(ImageResponse));
            var output = await ModelOperationApis.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions { ApiKey = "test", Http = http.Client });
            Equal(ModelOperationStopReason.Stop, output.StopReason, "dispatch");
            var thrown = false;
            try { await ModelOperationApis.GenerateImagesAsync(Flux with { Api = "other-images" }, DogPrompt); }
            catch (InvalidOperationException error) { thrown = error.Message == "No API provider registered for api: other-images"; }
            Check(thrown, "unknown api throws");
            Check(ModelOperationApis.GetClassifierApi("openai-decisions") is OpenAIDecisionsClassifier, "classifier registry");
            Check(ModelOperationApis.GetClassifierApi("llama-cpp-classify") is LlamaCppClassifier, "llama registry");
        })
    ];
}
