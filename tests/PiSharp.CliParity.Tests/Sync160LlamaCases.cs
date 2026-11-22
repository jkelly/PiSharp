// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/llama-extension.test.ts (the v1.6.0 cases), ported
// against packages/coding-agent/src/extensions/llama/provider.ts: the 9931 default server URL (#12001), image input of decision
// classifiers (#12282) and the instruction role of llama.cpp chat models (#12134).
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Llama;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// The fake llama.cpp router of LlamaCases (LlamaServer) answers on 127.0.0.1; the default-URL login goes to a recording handler, so
// nothing reaches a real server. Authored from the pinned sources by reading; nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160LlamaCases() =>
    [
        ("sync160.llama.default-server-url-is-9931", Sync160LlamaDefaultUrl),
        ("sync160.llama.decision-models-with-image-input-send-data-urls", Sync160LlamaDecisionImages),
        ("sync160.llama.reasoning-models-send-the-system-role", Sync160LlamaInstructionRole),
    ];

    /// <summary>Answers every request with an empty router catalog and records its line.</summary>
    private sealed class Sync160LlamaRecorder : HttpMessageHandler
    {
        public List<string> Lines { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Lines) Lines.Add($"{request.Method.Method} {request.RequestUri!.AbsoluteUri} {request.Headers.Authorization?.ToString() ?? "no authorization"}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") });
        }
    }

    // #12001: DEFAULT_LLAMA_SERVER_URL is llama-server's current default port. A login that enters no URL, without LLAMA_BASE_URL,
    // reads the router's catalog there and stores that URL.
    private static async Task Sync160LlamaDefaultUrl()
    {
        Equal("http://127.0.0.1:9931", LlamaCatalog.DefaultServerUrl, "DEFAULT_LLAMA_SERVER_URL");
        var recorder = new Sync160LlamaRecorder();
        using var http = new HttpMessageInvoker(recorder);
        var interaction = new ScriptedAuth("", "");
        var (key, environment) = await LlamaCatalog.LoginAsync(interaction, _ => null, CancellationToken.None, http);
        Names(["Text llama.cpp server URL http://127.0.0.1:9931", "Secret API key (optional) "],
            interaction.Prompts.Select(prompt => $"{prompt.Kind} {prompt.Message} {prompt.Placeholder}"), "prompts");
        Names(["GET http://127.0.0.1:9931/models no authorization"], recorder.Lines, "the default server's catalog is read");
        Equal(null, key, "no key stored");
        Equal("http://127.0.0.1:9931", environment["LLAMA_BASE_URL"], "the default URL is stored");
        // LLAMA_BASE_URL still wins over the default, as the placeholder and as the URL an empty answer takes.
        var configured = new ScriptedAuth("  ", "k");
        (key, environment) = await LlamaCatalog.LoginAsync(configured, name => name == "LLAMA_BASE_URL" ? "http://llama-box.test:8080/v1/" : null, CancellationToken.None, http);
        Equal("http://llama-box.test:8080/v1/", configured.Prompts[0].Placeholder, "placeholder from LLAMA_BASE_URL");
        Equal("GET http://llama-box.test:8080/models Bearer k", recorder.Lines[^1], "the configured server");
        Check(key == "k" && environment["LLAMA_BASE_URL"] == "http://llama-box.test:8080", "configured login result");
    }

    private const string Sync160ImageCatalog = """{"data":[{"id":"clef","status":{"value":"loaded"},"architecture":{"input_modalities":["text","image"],"output_modalities":["decisions"]}},{"id":"vision-chat","status":{"value":"loaded"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}}]}""";

    // #12282: decision models that advertise image input were registered as text-only. They are System One classifiers whose images
    // go out as llama.cpp reads them (the images list of data URLs, no TypeSafe assets and no opt-in header); the next-token fallback
    // cannot take images, whatever the model reads.
    private static async Task Sync160LlamaDecisionImages()
    {
        var detached = LlamaCatalog.Detached();
        detached.SetCatalog([
            Info("""{"id":"clef","status":{"value":"loaded"},"architecture":{"input_modalities":["text","image"],"output_modalities":["decisions"]}}"""),
            Info("""{"id":"vision-chat","status":{"value":"loaded"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}}"""),
            Info("""{"id":"kev","status":{"value":"sleeping"},"architecture":{"input_modalities":["text"],"output_modalities":["decisions"]}}""")], "http://localhost:9931");
        Names(["""{"type":"classifier","id":"clef","name":"clef","api":"typesafe-system-one","provider":"llama.cpp","baseUrl":"http://localhost:9931/v1","input":["text","image"],"cost":""" + LlamaFree +
            ""","contextWindow":128000,"compat":{"imageInput":"images","sendImageOptInHeader":false}}""",
            """{"type":"classifier","id":"vision-chat","name":"vision-chat","api":"llama-cpp-classify","provider":"llama.cpp","baseUrl":"http://localhost:9931","input":["text"],"cost":""" + LlamaFree +
            ""","contextWindow":128000}""",
            """{"type":"classifier","id":"kev","name":"kev","api":"typesafe-system-one","provider":"llama.cpp","baseUrl":"http://localhost:9931/v1","input":["text"],"cost":""" + LlamaFree +
            ""","contextWindow":128000}"""],
            detached.ClassifierJson.Select(model => model.ToJsonString()), "classifier models");
        Names(["\"vision-chat\" [\"text\",\"image\"]"], Rows(detached.ChatModels, "id", "input"), "the chat model keeps its image input");

        var systemOne = new List<LlamaServer.Request>();
        using var server = new LlamaServer(request =>
        {
            switch (request.Path)
            {
                case "/models": return LlamaServer.Json(Sync160ImageCatalog);
                case "/props": return LlamaServer.Json("{}");
                case "/v1/systemone":
                    lock (systemOne) systemOne.Add(request);
                    return LlamaServer.Json("""{"answers":{"screenshot":{"type":"noul","noul":0.9}}}""");
                default: return LlamaServer.NotFound();
            }
        });
        using var sandbox = new Sandbox("sync160-llama-images");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), $$$$"""{"llama.cpp":{"type":"api_key","key":"local","env":{"LLAMA_BASE_URL":"{{{{server.Url}}}}"}}}""");
        var registry = await sandbox.Runtime().CreateModelRegistryAsync(CancellationToken.None);
        var errors = await registry.RefreshAsync(allowNetwork: true, force: null, providers: ["llama.cpp"], CancellationToken.None);
        Equal(0, errors.Count, "refresh errors: " + string.Join(",", errors.Select(pair => pair.Key + ": " + pair.Value.Message)));
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox.AgentDir, "models-store.json")))!["llama.cpp"]!["models"]!.AsArray();
        Names(["\"vision-chat\" \"openai-completions\" [\"text\",\"image\"]", "\"clef\" \"typesafe-system-one\" [\"text\",\"image\"]",
            "\"vision-chat\" \"llama-cpp-classify\" [\"text\"]"], Rows(stored.OfType<JsonObject>(), "id", "api", "input"), "models-store.json");
        Names(["\"clef\" {\"imageInput\":\"images\",\"sendImageOptInHeader\":false}", "\"vision-chat\" undefined"],
            Rows(stored.OfType<JsonObject>().Where(model => (string?)model["type"] == "classifier"), "id", "compat"), "persisted classifier compat");
        // A cache-only refresh restores the persisted classifier with its image input and compat.
        var restored = LlamaCatalog.Detached();
        await LlamaRefresh(restored, server.Url, new LlamaPublish { Persisted = new(stored.DeepClone().AsArray(), CheckedAt: 1) }, allowNetwork: false);
        Names(["\"clef\" [\"text\",\"image\"] {\"imageInput\":\"images\",\"sendImageOptInHeader\":false}", "\"vision-chat\" [\"text\"] undefined"],
            Rows(restored.ClassifierJson, "id", "input", "compat"), "restored classifiers");

        // models.classify() through the classifier registry extensions and codemode use.
        var operations = new PiSharp.Cli.Extensions.NativeExtensionModelOperations(PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(
            name => sandbox.Vars.GetValueOrDefault(name), Path.Combine(sandbox.AgentDir, "auth.json")));
        Names(["clef [\"text\",\"image\"]", "vision-chat [\"text\"]"], operations.GetModelsOfType(ModelType.Classifier, "llama.cpp")
            .Select(model => model.Value.GetProperty("id").GetString() + " " + model.Value.GetProperty("input").GetRawText()), "classifier ids and inputs");
        var clef = operations.GetModelOfType(ModelType.Classifier, "llama.cpp", "clef")!;
        Equal("""{"imageInput":"images","sendImageOptInHeader":false}""", clef.Value.GetProperty("compat").GetRawText(), "clef compat in the registry");
        var context = new ClassifierContext(JsonData.Parse("""{"task":"Check the screenshot."}"""),
            [new("screenshot", new ClassifierBoolQuestion("Is this a screenshot?", "", ""))], [new ImageContent("iVBORw0KGgo=", "image/png")]);
        var result = await operations.ClassifyAsync(clef, context, null, CancellationToken.None);
        Equal(null, result.ErrorMessage, "classify error");
        Equal(0.9, ((ClassifierBoolAnswer)result.GetAnswer("screenshot")!).Probability, "answer");
        Equal(1, systemOne.Count, "one System One request");
        Equal("POST /v1/systemone", $"{systemOne[0].Method} {systemOne[0].Target}", "System One route");
        Equal("Bearer local", systemOne[0].Header("authorization"), "System One bearer key");
        Equal(null, systemOne[0].Header("typesafe-danger-zone-images"), "no TypeSafe image opt-in header");
        Equal("""{"model":"clef","state":{"task":"Check the screenshot."},"questions":{"screenshot":{"type":"noul","instructions":"Is this a screenshot?","criteria":{"true":"","false":""}}},"images":["data:image/png;base64,iVBORw0KGgo="]}""",
            systemOne[0].Body, "System One request body (images as data URLs, no assets)");

        // The next-token fallback is text-only: the image never leaves the process.
        var before = server.Seen().Length;
        var visionChat = operations.GetModelOfType(ModelType.Classifier, "llama.cpp", "vision-chat")!;
        var rejected = await operations.ClassifyAsync(visionChat, context, null, CancellationToken.None);
        Equal("Model llama.cpp/vision-chat does not accept image input", rejected.ErrorMessage, "llama-cpp-classify rejects images");
        Equal(before, server.Seen().Length, "no request for the rejected classification");
    }

    // #12134: upstream dropped supportsDeveloperRole from the llama.cpp chat compat because pi-ai's openai-completions now sends the
    // developer role only to api.openai.com. Either way a llama.cpp reasoning model's system prompt goes out with the system role
    // (chat templates such as Qwen's reject developer). This pins the wire role, so the explicit false can only be removed from
    // LlamaCatalog.ToChatModel once the native default is the v1.6.0 one.
    private static async Task Sync160LlamaInstructionRole()
    {
        using var server = new LlamaServer(request => request.Path switch
        {
            "/models" => LlamaServer.Json("""{"data":[{"id":"qwen","status":{"value":"loaded"},"meta":{"n_ctx":4096}}]}"""),
            "/props" => LlamaServer.Json("""{"chat_template":"{% if enable_thinking %}think{% endif %}"}"""),
            _ => LlamaServer.NotFound()
        });
        using var sandbox = new Sandbox("sync160-llama-role");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), $$$$"""{"llama.cpp":{"type":"api_key","env":{"LLAMA_BASE_URL":"{{{{server.Url}}}}"}}}""");
        var registry = await sandbox.Runtime().CreateModelRegistryAsync(CancellationToken.None);
        Equal(0, (await registry.RefreshAsync(allowNetwork: true, force: null, providers: ["llama.cpp"], CancellationToken.None)).Count, "refresh");
        Check(registry.Find("llama.cpp", "qwen") is { Reasoning: true }, "the chat template makes qwen a reasoning model");
        sandbox.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: " + JsonSerializer.Serialize(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "qwen", choices = new[] { new { index = 0, delta = new { role = "assistant", content = "hello from llama" }, finish_reason = (string?)null } } }) + "\n\n" +
                "data: " + JsonSerializer.Serialize(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "qwen", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 5, completion_tokens = 3, total_tokens = 8 } }) + "\n\n" +
                "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        };
        foreach (var thinking in new[] { "off", "medium" })
        {
            sandbox.Requests.Clear();
            var (code, stdout, stderr) = await sandbox.Run("-p", "--model", "llama.cpp/qwen", "--thinking", thinking, "hi");
            Equal(0, code, thinking + " exit; " + stderr);
            Equal("hello from llama\n", stdout, thinking + " stdout");
            var request = sandbox.Requests.Single();
            Equal(server.Url + "/v1/chat/completions", request.Url, thinking + " route");
            Equal("system", request.Json.GetProperty("messages")[0].GetProperty("role").GetString(), thinking + ": the system prompt keeps the system role");
            Check(!request.Body!.Contains("\"developer\"", StringComparison.Ordinal), thinking + ": no developer role in the request");
        }
    }
}
