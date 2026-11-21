// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/test/llama-cpp-classify.test.ts (the 9931 server URLs of #12001) and
// the classify request of llama.cpp decision models that take images (#12282, packages/coding-agent/src/extensions/llama/provider.ts
// toPiClassifierModel over packages/ai/src/api/{system-one-shared,typesafe-system-one}.ts), following
// packages/coding-agent/test/llama-extension.test.ts by reading. Fake HTTP answers in process.
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

internal static partial class Program
{
    /// <summary>A llama.cpp decision model as the built-in extension registers it: System One at the server's <c>/v1</c> URL.</summary>
    private static ClassifierModel LlamaDecisionModel(string? compat, bool images = true) =>
        SystemOneModel("llama.cpp", "clef", TypeSafeSystemOneClassifier.ApiId, "http://127.0.0.1:9931/v1", compat, images);

    private const string LlamaImagesCompat = """{"imageInput":"images","sendImageOptInHeader":false}""";

    private static IEnumerable<(string, Func<Task>)> Sync160LlamaCases() =>
    [
        ("sync160.llama.server-root-of-the-9931-default-url", Sync(() =>
        {
            Equal("http://127.0.0.1:9931", LlamaCppClassifier.ServerRoot("http://127.0.0.1:9931/v1/"), "trailing slash");
            Equal("https://example.com/prefix", LlamaCppClassifier.ServerRoot("https://example.com/prefix/v1"), "prefix");
            Equal("http://127.0.0.1:9931", LlamaCppClassifier.ServerRoot("http://127.0.0.1:9931"), "no v1");
        })),
        ("sync160.llama.decision-model-images-go-out-as-data-urls", async () =>
        {
            // llama-server's System One parser reads images from the images list of data URLs: no assets, no opt-in header.
            var (result, http) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, LlamaDecisionModel(LlamaImagesCompat),
                ImagesContext(("cG5n", "image/png"), ("d2VicA==", "image/webp")), options: new ClassifierOptions { ApiKey = "local" });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal(0.5, Answer<ClassifierBoolAnswer>(result, "approved").Probability, "answer");
            Equal("http://127.0.0.1:9931/v1/systemone", http.Requests[0].Url, "System One at the /v1 URL");
            Equal("Bearer local", http.Requests[0].Headers["authorization"], "key");
            Check(!http.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "no TypeSafe opt-in header");
            Body("""{"model":"clef","state":{"text":"ok"},"questions":""" + ApprovalQuestions + ""","images":["data:image/png;base64,cG5n","data:image/webp;base64,d2VicA=="]}""",
                http.Requests[0].Body, "images");
            // Without the compat the typesafe-system-one default applies (TypeSafe's assets behind the opt-in header), which
            // llama-server ignores: accepting image input alone would not have been enough.
            var (_, defaults) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, LlamaDecisionModel(null), ImagesContext(("cG5n", "image/png")));
            Equal("true", defaults.Requests[0].Headers["typesafe-danger-zone-images"], "detected opt-in header without compat");
            Body("""{"model":"clef","state":["<image-1>",{"text":"ok"}],"questions":""" + ApprovalQuestions + ""","assets":{"<image-1>":{"type":"image","format":"png","data_base64":"cG5n"}}}""",
                defaults.Requests[0].Body, "assets without compat");
            // A text-only decision model takes no images.
            var (textOnly, none) = await SystemOneRun(TypeSafeSystemOneClassifier.Instance, LlamaDecisionModel(null, images: false), ImagesContext(("cG5n", "image/png")));
            Equal("System One API does not support image input for clef", textOnly.ErrorMessage, "text-only decision model");
            Equal(0, none.Requests.Count, "rejected before sending");
        }),
        ("sync160.llama.registry-classifies-images-with-decision-models-only", async () =>
        {
            var env = new Dictionary<string, string> { ["LLAMA_BASE_URL"] = "http://llama-box.test:9931/v1/" };
            var registry = new ModelOperationsRegistry(ModelOperationsAuth.Standard(name => env.GetValueOrDefault(name)));
            var clef = LlamaDecisionModel(LlamaImagesCompat);
            var chat = TestClassifier("llama.cpp", "vision-chat", LlamaCppClassifier.ApiId) with { BaseUrl = "http://127.0.0.1:9931" };
            registry.SetProvider(BuiltinModelOperationProviders.LlamaCpp([clef, chat]));
            var systemOne = FakeHttp.Always(() => FakeHttp.Json(ApprovalAnswer));
            var native = await registry.ClassifyAsync(clef, ImagesContext(("cG5n", "image/png")), new ClassifierOptions { Http = systemOne.Client });
            Equal(ModelOperationStopReason.Stop, native.StopReason, native.ErrorMessage ?? "decision model");
            Equal("http://llama-box.test:9931/v1/systemone", systemOne.Requests[0].Url, "the configured server's System One route");
            Equal("Bearer local", systemOne.Requests[0].Headers["authorization"], "keyless llama");
            Check(!systemOne.Requests[0].Headers.ContainsKey("typesafe-danger-zone-images"), "no opt-in header");
            Body("""{"model":"clef","state":{"text":"ok"},"questions":""" + ApprovalQuestions + ""","images":["data:image/png;base64,cG5n"]}""",
                systemOne.Requests[0].Body, "images through the registry");
            // The next-token fallback cannot take images, whatever the model reads.
            var server = LlamaServer();
            var rejected = await registry.ClassifyAsync(chat, ApprovalContext with { Images = [new("cG5n", "image/png")] }, new ClassifierOptions { Http = server.Client });
            Equal("Model llama.cpp/vision-chat does not accept image input", rejected.ErrorMessage, "llama-cpp-classify is text-only");
            Equal(0, server.Requests.Count, "no request");
        }),
    ];
}
