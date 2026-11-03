using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

// Authored from the Pi v1.5.0 (f9a26365568127461c47a07a37dcb42c140a3ac5) System One image input (7f225f25d):
// packages/ai/src/api/{system-one-shared,typesafe-system-one,cloudflare-workers-ai-system-one}.ts and the 1.5.0 catalog compat,
// following test/typesafe-system-one.test.ts and test/cloudflare-workers-ai-system-one.test.ts by reading. Fake HTTP answers in process.
internal static partial class Program
{
    private static ClassifierModel SystemOneModel(string provider, string id, string api, string baseUrl, string? compat = null, bool images = true) => Classifier($$$"""
        {"type":"classifier","id":"{{{id}}}","name":"{{{id}}}","api":"{{{api}}}","provider":"{{{provider}}}","baseUrl":"{{{baseUrl}}}","input":{{{(images ? """["text","image"]""" : """["text"]""")}}},"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":64000{{{(compat is null ? "" : ",\"compat\":" + compat)}}}}
        """);

    private static ClassifierModel ImageJev(string? compat = null, string provider = "typesafe", string baseUrl = "https://api.typesafe.ai/v1/") =>
        SystemOneModel(provider, "jev-latest", TypeSafeSystemOneClassifier.ApiId, baseUrl, compat);

    private static ClassifierModel WorkersModel(string id, string? compat = null) =>
        SystemOneModel("cloudflare-workers-ai", id, CloudflareWorkersAISystemOneClassifier.ApiId, CloudflareCatalog, compat);

    private static ClassifierContext ImagesContext(params (string Data, string MimeType)[] images) =>
        ApprovalContext with { Images = [.. images.Select(image => new ImageContent(image.Data, image.MimeType))] };

    private const string ApprovalAnswer = """{"answers":{"approved":{"type":"noul","noul":0.5}}}""";
    private const string ApprovalQuestions = """{"approved":{"type":"noul","instructions":"Does the user approve?","criteria":{"true":"Approval","false":"No approval"}}}""";
    private const string WorkersApproval = """{"result":{"answers":{"approved":{"type":"noul","noul":0.5}}},"success":true,"errors":[],"messages":[]}""";

    private static async Task<(ClassifierResult Result, FakeHttp Http)> SystemOneRun(SystemOneClassifier api, ClassifierModel model, ClassifierContext context,
        string response = ApprovalAnswer, ClassifierOptions? options = null)
    {
        var http = FakeHttp.Always(() => FakeHttp.Json(response));
        var result = await api.ClassifyAsync(model, context, (options ?? new ClassifierOptions { ApiKey = "secret" }) with
        { Http = http.Client, Env = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("CLOUDFLARE_ACCOUNT_ID", "account-id") });
        return (result, http);
    }

    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.typesafe-assets-with-the-opt-in-header", async () =>
        {
            var (result, http) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev("""{"imageInput":"assets","sendImageOptInHeader":true}"""),
                ImagesContext(("cG5n", "image/png"), ("anBlZw==", "image/jpeg")));
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal("true", http.Requests[0].Headers["typesafe-danger-zone-images"], "opt-in header");
            Body("""{"model":"jev-latest","state":["<image-1>","<image-2>",{"text":"ok"}],"questions":""" + ApprovalQuestions + ""","assets":{"<image-1>":{"type":"image","format":"png","data_base64":"cG5n"},"<image-2>":{"type":"image","format":"jpeg","data_base64":"anBlZw=="}}}""",
                http.Requests[0].Body, "assets");
            // A request header of the same name wins over the opt-in default.
            var (_, suppressed) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev(), ImagesContext(("cG5n", "image/png")),
                options: new ClassifierOptions { ApiKey = "secret", Headers = [new("TypeSafe-Danger-Zone-Images", null)] });
            Check(!suppressed.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "null request header suppresses the opt-in");
            var (webp, none) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev(), ImagesContext(("d2VicA==", "image/webp")));
            Equal("System One API accepts PNG and JPEG images for jev-latest, got image/webp", webp.ErrorMessage, "assets formats");
            Equal(0, none.Requests.Count, "rejected before sending");
            var (_, textOnly) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev(), ApprovalContext);
            Check(!textOnly.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "no opt-in header without images");
            Body($$$"""{"model":"jev-latest","state":{"text":"ok"},"questions":{{{ApprovalQuestions}}}}""", textOnly.Requests[0].Body, "text only");
        }),
        ("sync150.typesafe-detects-openrouter-by-provider-or-host", async () =>
        {
            // Without compat: TypeSafe's own API takes assets with the opt-in header; OpenRouter, by provider id or host, takes image_url parts.
            var (_, typesafe) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev(), ImagesContext(("cG5n", "image/png")));
            Equal("true", typesafe.Requests[0].Headers["typesafe-danger-zone-images"], "detected opt-in");
            Contains("\"assets\":{\"<image-1>\"", typesafe.Requests[0].Body, "detected assets");
            foreach (var (provider, baseUrl) in new[] { ("openrouter", "https://proxy.invalid/v1"), ("router-proxy", "https://eu.openrouter.ai/api/v1") })
            {
                var (result, http) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev(provider: provider, baseUrl: baseUrl),
                    ImagesContext(("d2VicA==", "image/webp")));
                Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
                Check(!http.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), provider + " sends no opt-in header");
                Body($$$"""{"model":"jev-latest","state":[{"type":"image_url","image_url":{"url":"data:image/webp;base64,d2VicA=="}},{"text":"ok"}],"questions":{{{ApprovalQuestions}}}}""",
                    http.Requests[0].Body, provider + " state-image-urls");
            }
        }),
        ("sync150.clef-images-as-data-urls", async () =>
        {
            var clef = WorkersModel("@cf/cloudflare/clef");
            var (result, http) = await SystemOneRun(CloudflareWorkersAISystemOneClassifier.Instance, clef, ImagesContext(("cG5n", "image/png"), ("d2VicA==", "image/webp")), WorkersApproval);
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Check(http.Requests[0].Url.EndsWith("/ai/run", StringComparison.Ordinal), "run endpoint " + http.Requests[0].Url);
            Check(!http.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "no opt-in header on Workers AI");
            Body($$$"""{"model":"@cf/cloudflare/clef","input":{"state":{"text":"ok"},"questions":{{{ApprovalQuestions}}},"images":["data:image/png;base64,cG5n","data:image/webp;base64,d2VicA=="]}}""",
                http.Requests[0].Body, "clef");
            var five = Enumerable.Repeat(("cG5n", "image/png"), 5).ToArray();
            Equal("Cloudflare Workers AI accepts at most 4 images for @cf/cloudflare/clef, got 5",
                (await SystemOneRun(CloudflareWorkersAISystemOneClassifier.Instance, clef, ImagesContext(five), WorkersApproval)).Result.ErrorMessage, "at most four");
            var (gif, sent) = await SystemOneRun(CloudflareWorkersAISystemOneClassifier.Instance, clef, ImagesContext(("Z2lm", "image/gif")), WorkersApproval);
            Equal("Cloudflare Workers AI accepts PNG, JPEG, and WebP images for @cf/cloudflare/clef, got image/gif", gif.ErrorMessage, "data URL formats");
            Equal(0, sent.Requests.Count, "rejected before sending");
        }),
        ("sync150.workers-jev-assets-without-the-opt-in-header", async () =>
        {
            var (result, http) = await SystemOneRun(CloudflareWorkersAISystemOneClassifier.Instance, WorkersModel("typesafe/jev"), ImagesContext(("anBlZw==", "image/jpeg")), WorkersApproval);
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Check(!http.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "no opt-in header");
            Body("""{"model":"typesafe/jev","input":{"state":["<image-1>",{"text":"ok"}],"questions":""" + ApprovalQuestions + ""","assets":{"<image-1>":{"type":"image","format":"jpeg","data_base64":"anBlZw=="}}}}""",
                http.Requests[0].Body, "workers jev");
        }),
        ("sync150.compat-overrides-detection", async () =>
        {
            // A proxy that hides both the provider id and the OpenRouter host sets compat.imageInput; sendImageOptInHeader is independent.
            var (_, proxied) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev("""{"imageInput":"state-image-urls"}""", "proxy", "https://proxy.invalid/v1"),
                ImagesContext(("cG5n", "image/png")));
            Equal("true", proxied.Requests[0].Headers["typesafe-danger-zone-images"], "the detected header stays when compat sets only imageInput");
            Contains("\"state\":[{\"type\":\"image_url\"", proxied.Requests[0].Body, "compat imageInput");
            var (_, quiet) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, ImageJev("""{"sendImageOptInHeader":false}"""), ImagesContext(("cG5n", "image/png")));
            Check(!quiet.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "compat header off");
            Contains("\"assets\":{", quiet.Requests[0].Body, "detected assets");
            var (_, clefForm) = await SystemOneRun(CloudflareWorkersAISystemOneClassifier.Instance, WorkersModel("typesafe/jev", """{"imageInput":"images"}"""),
                ImagesContext(("cG5n", "image/png")), WorkersApproval);
            Contains("\"images\":[\"data:image/png;base64,cG5n\"]", clefForm.Requests[0].Body, "compat over the id check");
        }),
        ("sync150.text-only-models-reject-images-by-id", async () =>
        {
            var (typesafe, none) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, Jev, ImagesContext(("cG5n", "image/png")));
            Equal("System One API does not support image input for jev-latest", typesafe.ErrorMessage, "typesafe");
            var (workers, _) = await SystemOneRun(CloudflareWorkersAISystemOneClassifier.Instance,
                SystemOneModel("cloudflare-workers-ai", "text-only", CloudflareWorkersAISystemOneClassifier.ApiId, CloudflareCatalog, images: false), ImagesContext(("cG5n", "image/png")));
            Equal("Cloudflare Workers AI does not support image input for text-only", workers.ErrorMessage, "workers");
            Equal(0, none.Requests.Count, "nothing sent");
        }),
        ("sync150.catalog-compat-drives-the-image-form", async () =>
        {
            var registry = PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(
                name => name switch { "OPENROUTER_API_KEY" => "router", "TYPESAFE_API_KEY" => "typesafe", "OPENCODE_API_KEY" => "opencode", _ => null }, authPath: null);
            var router = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "openrouter", "~typesafe/jev-latest")!;
            var http = FakeHttp.Always(() => FakeHttp.Json(ApprovalAnswer));
            await registry.ClassifyAsync(router, ImagesContext(("cG5n", "image/png")), new ClassifierOptions { Http = http.Client });
            Equal("https://openrouter.ai/api/v1/systemone", http.Requests[0].Url, "openrouter url");
            Contains("\"state\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,cG5n\"}},{\"text\":\"ok\"}]", http.Requests[0].Body, "openrouter form");
            var typesafe = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "typesafe", "jev-latest")!;
            await registry.ClassifyAsync(typesafe, ImagesContext(("cG5n", "image/png")), new ClassifierOptions { Http = http.Client });
            Equal("https://api.typesafe.ai/v1/systemone", http.Requests[1].Url, "typesafe url");
            Equal("true", http.Requests[1].Headers["typesafe-danger-zone-images"], "typesafe opt-in");
            // OpenCode's Jev classifiers are text-only and moved to the System One endpoint.
            var opencode = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "opencode", "jev-1.13")!;
            await registry.ClassifyAsync(opencode, ApprovalContext, new ClassifierOptions { Http = http.Client });
            Equal("https://opencode.ai/inference/systemone/v1/systemone", http.Requests[2].Url, "opencode url");
            // model-operations.ts rejects images for a text-only catalog entry before the API runs.
            Equal("Model opencode/jev-1.13 does not accept image input",
                (await registry.ClassifyAsync(opencode, ImagesContext(("cG5n", "image/png")), new ClassifierOptions { Http = http.Client })).ErrorMessage, "opencode text-only");
            Equal(3, http.Requests.Count, "the text-only rejection sends nothing");
        }),
    ];
}
