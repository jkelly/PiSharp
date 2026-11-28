// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/test/{pi-image,pi-decision,openrouter-images}.test.ts.
using System.Net;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync170Cases()
    {
        yield return ("sync170.pi-image.wire-retry-and-usage", async () =>
        {
            var model = Flux with { Provider = "radius", Api = "pi-image", BaseUrl = "https://gateway.invalid/v1", Cost = new(1, 2, 3, 4) { ImageOutput = 10 } };
            var statuses = new List<int>();
            var http = new FakeHttp((_, attempt, _) => Task.FromResult(attempt == 1 ?
                FakeHttp.Text("temporarily unavailable", HttpStatusCode.ServiceUnavailable, "text/plain", ("retry-after-ms", "0")) :
                FakeHttp.Json("""{"responseId":"image-1","usage":{"input":10,"output":9,"reasoning":3,"imageOutput":4},"output":[{"type":"image","data":"YQ==","mimeType":"image/png"}]}""")));
            var options = new ImagesOptions { ApiKey = "fixture", Http = http.Client, Size = "2K", AspectRatio = "4:3", SessionId = "s",
                Metadata = JsonData.Parse("""{"name":"example"}"""), OnResponse = (r, _, _) => { statuses.Add(r.Status); return ValueTask.CompletedTask; } };
            var result = await ModelOperationApis.GenerateImagesAsync(model, DogPrompt, options);
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal("image-1", result.ResponseId, "response id");
            Equal("https://gateway.invalid/v1/image", http.Requests[0].Url, "image route");
            Equal("2K", http.Requests[0].Json.GetProperty("options").GetProperty("size").GetString(), "image size");
            Equal("503,200", string.Join(',', statuses), "every attempt observable");
            Close(0.00005, (double)result.Usage!.Cost.Output, "split reasoning/image pricing");
            Equal("4", result.Usage.ExtraProperties!.Values["imageOutput"].ToString(), "image token count");
            Equal(new ImageContent("YQ==", "image/png"), result.Output.Single(), "image data");
        });
        yield return ("sync170.pi-decision.validates-answers-and-preserves-billing", async () =>
        {
            var model = TestClassifier("radius", "decision", "pi-decision") with { BaseUrl = "https://gateway.invalid/v1" };
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"responseId":"decision-1","usage":{"cost":{"total":1.25,"totalFromProvider":true}},"answers":{"approved":{"type":"bool","probability":2}}}"""));
            var result = await PiDecision.Instance.ClassifyAsync(model, ApprovalContext, new ClassifierOptions { ApiKey = "fixture", Http = http.Client });
            Equal(ModelOperationStopReason.Error, result.StopReason, "out of range");
            Contains("out-of-range", result.ErrorMessage, "invalid probability");
            Equal("decision-1", result.ResponseId, "response id survives malformed answers");
            Equal(1.25m, result.Usage!.Cost.Total, "provider billing");
            Equal("https://gateway.invalid/v1/decision", http.Requests.Single().Url, "decision route");
            Equal("decision-1", ModelOperationJson.ParseClassifierResult(ModelOperationJson.WriteClassifierResult(result).Value).ResponseId, "response ID round trip");
        });
        yield return ("sync170.openrouter-image.options-native-error-and-usage", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"id":"r","choices":[{"finish_reason":"error","native_finish_reason":"MODEL_ARMOR"}],"usage":{"completion_tokens":18,"completion_tokens_details":{"reasoning_tokens":2,"image_tokens":16},"cost":0.5,"is_byok":false}}"""));
            var result = await OpenRouterImages.Instance.GenerateImagesAsync(Flux, DogPrompt, new ImagesOptions
                { ApiKey = "fixture", Http = http.Client, Size = "1K", AspectRatio = "1:1" });
            Equal(ModelOperationStopReason.Error, result.StopReason, "unsuccessful finish");
            Contains("native_finish_reason: MODEL_ARMOR", result.ErrorMessage, "native failure preserved");
            Equal(0.5m, result.Usage!.Cost.Total, "provider total");
            Equal("1K", http.Requests.Single().Json.GetProperty("image_config").GetProperty("image_size").GetString(), "image size");
        });
    }
}
