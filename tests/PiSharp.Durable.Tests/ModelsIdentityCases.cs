using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

#pragma warning disable PISHARP_IDENTITY001
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ModelsIdentityCases()
    {
        yield return Case("models.identity.defaults-merge-for-native-chat", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = new NativeDurableModels(new()
            {
                Providers = ["openai"], Environment = _ => "fake-key", HttpHandler = handler,
                Identity = new() { UserAgent = ClientUserAgent.Custom("default-agent/1"), Version = "default-version" },
            });
            var model = models.GetModel("openai", "gpt-5")!;
            await models.CompleteSimpleAsync(model, UserTurn("a"), null, default);
            await models.CompleteSimpleAsync(model, UserTurn("b"), new() { Identity = new() { Version = "request-version" } }, default);
            await models.CompleteSimpleAsync(model, UserTurn("c"), new() { Identity = new() { UserAgent = ClientUserAgent.Custom("request-agent/2") } }, default);
            Equal("default-agent/1", handler.Requests[0].Header("User-Agent"), "model default");
            Equal("default-agent/1", handler.Requests[1].Header("User-Agent"), "partial request keeps default user agent");
            Equal("request-agent/2", handler.Requests[2].Header("User-Agent"), "explicit request wins");
            Check(handler.Requests.All(request => request.Header("HTTP-Referer") is null), "no automatic attribution");
        });
        yield return Case("models.identity.classifier-and-images-default-override-disable", async () =>
        {
            var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("systemone")
                    ? "{\"answers\":{\"good\":{\"type\":\"noul\",\"noul\":0.8}}}" : "{\"choices\":[{\"message\":{\"content\":\"painted\"}}]}", Encoding.UTF8, "application/json"),
            });
            using var models = new NativeDurableModels(new()
            {
                Providers = ["openrouter"], Environment = _ => "fake-key", HttpHandler = handler,
                Identity = new() { UserAgent = ClientUserAgent.Custom("operation-agent/1"), Attribution = new("Example", "https://example.invalid") },
            });
            var classifier = models.GetModelsOfType("classifier", "openrouter")[0];
            var image = models.GetModelsOfType("image", "openrouter")[0];
            var classifyContext = (JsonObjectValue)JsonValue.Parse("{\"state\":{\"review\":\"helpful\"},\"questions\":{\"good\":{\"type\":\"bool\",\"instructions\":\"Is the review positive?\",\"criteria\":{\"true\":\"positive\",\"false\":\"negative\"}}}}");
            var imageContext = (JsonObjectValue)JsonValue.Parse("{\"input\":[{\"type\":\"text\",\"text\":\"a cat\"}]}");
            foreach (var identity in new ClientIdentity?[] { null, new() { UserAgent = ClientUserAgent.Custom("override/2") }, new() { AttributionOff = true } })
            {
                var options = identity is null ? null : new DurableRequestOptions { Identity = identity };
                var classified = await models.ClassifyAsync(classifier, classifyContext, options, default);
                var painted = await models.GenerateImagesAsync(image, imageContext, options, default);
                Equal("stop", ((JsonStringValue)classified["stopReason"]).Value, "classifier success");
                Equal("stop", ((JsonStringValue)painted["stopReason"]).Value, "images success");
            }
            foreach (var index in new[] { 0, 1, 2, 3 })
            {
                Equal("Example", handler.Requests[index].Header("X-OpenRouter-Title"), "default attribution");
                Equal(index < 2 ? "operation-agent/1" : "override/2", handler.Requests[index].Header("User-Agent"), "user agent merge");
            }
            foreach (var index in new[] { 4, 5 })
            {
                Equal<string?>(null, handler.Requests[index].Header("HTTP-Referer"), "explicit attribution null");
                Equal("operation-agent/1", handler.Requests[index].Header("User-Agent"), "off retains other defaults");
            }
            var headers = (JsonObjectValue)JsonValue.Parse("{\"User-Agent\":\"model-header/3\",\"X-OpenRouter-Title\":\"Model Title\"}");
            await models.GenerateImagesAsync(image.SetItem("headers", headers), imageContext, null, default);
            Equal("model-header/3", handler.Requests[^1].Header("User-Agent"), "model header wins over identity");
            Equal("Model Title", handler.Requests[^1].Header("X-OpenRouter-Title"), "attribution header wins over identity");
            using var unattributed = new NativeDurableModels(new() { Providers = ["openrouter"], Environment = _ => "fake-key", HttpHandler = handler });
            await unattributed.GenerateImagesAsync(image, imageContext, default);
            Equal<string?>(null, handler.Requests[^1].Header("HTTP-Referer"), "no default attribution");
        });
    }
}
