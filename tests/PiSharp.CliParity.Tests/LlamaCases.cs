// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/test/llama-extension.test.ts, ported against
// packages/coding-agent/src/extensions/llama/{client,provider,huggingface}.ts, plus the built-in provider's integration in the model
// registry (auth check/resolve, the persisted catalog, the live chat route and the classifier registry).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Llama;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// A fake llama.cpp router answers on 127.0.0.1 (LlamaServer); request lines and bodies are pinned. Authored from the pinned sources by
// reading; nothing is captured from a real llama.cpp server or an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LlamaCases() =>
    [
        ("llama.normalizes-management-and-inference-urls", Sync(LlamaUrls)),
        ("llama.exposes-loaded-and-sleeping-models-with-router-metadata", Sync(LlamaSelectable)),
        ("llama.discovers-chat-template-thinking-for-loaded-models", LlamaThinking),
        ("llama.persists-and-restores-selectable-models", LlamaPersist),
        ("llama.keeps-cached-context-for-unloaded-autoload-presets", LlamaCachedContext),
        ("llama.unloaded-presets-only-with-router-autoload", LlamaAutoload),
        ("llama.hides-unloaded-presets-without-autoload", LlamaNoAutoload),
        ("llama.decision-models-are-system-one-classifiers", LlamaDecisionModels),
        ("llama.decision-models-that-output-text-are-chat-models", Sync(LlamaHybrid)),
        ("llama.dormant-until-configured-and-login-stores-url-and-key", LlamaLogin),
        ("llama.huggingface-search-quantizations-and-access", LlamaHuggingFace),
        ("llama.load-waits-with-sse-progress", LlamaLoad),
        ("llama.download-reports-byte-progress-and-returns-the-catalog", LlamaDownload),
        ("llama.registry-provider-auth-availability-and-request-auth", LlamaRegistry),
        ("llama.print-mode-streams-a-restored-model-through-v1", LlamaPrintMode),
    ];

    private const string LlamaFree = """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}""";

    /// <summary>A publication that keeps the persisted entry and runs the update (models-store.ts in memory).</summary>
    private sealed class LlamaPublish
    {
        public ModelsStoreEntry? Persisted { get; set; }
        public Task<bool> Publish(ModelsPublication publication)
        {
            if (publication.Delete) Persisted = null;
            else if (publication.Persist is { } persist) Persisted = persist with { Models = (JsonArray)persist.Models.DeepClone() };
            publication.Update?.Invoke();
            return Task.FromResult(true);
        }
    }

    private static ProviderStoredCredentialView LlamaCredential(string url, string? key = "local") =>
        new("api_key", key, new Dictionary<string, string> { ["LLAMA_BASE_URL"] = url });

    private static Task LlamaRefresh(LlamaCatalog catalog, string url, LlamaPublish publish, bool allowNetwork = true, string? key = "local") =>
        catalog.RefreshAsync(new(publish.Persisted, LlamaCredential(url, key), allowNetwork, publish.Publish, CancellationToken.None));

    private static string[] Rows(IEnumerable<JsonObject> models, params string[] fields) =>
        [.. models.Select(model => string.Join(" ", fields.Select(field => model[field]?.ToJsonString() ?? "undefined")))];

    private static string[] Rows(IEnumerable<RegistryModel> models, params string[] fields) => Rows(models.Select(model => model.CloneJson()), fields);

    private static LlamaModelInfo Info(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static void LlamaUrls()
    {
        Equal("http://127.0.0.1:8080", LlamaClient.NormalizeServerUrl("http://127.0.0.1:8080/v1/"), "trailing /v1/");
        Equal("https://example.com/prefix", LlamaClient.NormalizeServerUrl("https://example.com/prefix/v1"), "prefix");
        Equal("http://localhost:8080", LlamaClient.NormalizeServerUrl("  http://localhost:8080/?x=1#y  "), "query and fragment");
        Equal("Server URL must use http or https", Throws<InvalidOperationException>(() => LlamaClient.NormalizeServerUrl("file:///tmp/llama"), "file").Message, "file URL");
        Equal("http://h:1/v1", LlamaClient.InferenceUrl("http://h:1"), "inference URL");
        Equal("512 B", LlamaClient.FormatBytes(512), "bytes");
        Equal("1.00 KiB", LlamaClient.FormatBytes(1024), "KiB");
        Equal("12.0 MiB", LlamaClient.FormatBytes(12 * 1024 * 1024), "MiB");
    }

    private static void LlamaSelectable()
    {
        var catalog = LlamaCatalog.Detached();
        catalog.SetCatalog([
            Info("""{"id":"loaded","status":{"value":"loaded","args":["llama-server","--n-gpu-layers","999"]},"architecture":{"input_modalities":["text","image"]},"meta":{"n_ctx":65536,"n_ctx_train":131072}}"""),
            Info("""{"id":"sleeping","status":{"value":"sleeping"}}"""),
            Info("""{"id":"unloaded","status":{"value":"unloaded"}}"""),
            Info("""{"id":"loading","status":{"value":"loading"}}""")], "http://localhost:8080");
        var models = catalog.ChatModels;
        Names(["loaded", "sleeping"], models.Select(model => model.Id), "chat models");
        Equal("""{"id":"loaded","name":"loaded","api":"openai-completions","provider":"llama.cpp","baseUrl":"http://localhost:8080/v1","reasoning":false,"input":["text","image"],"cost":""" + LlamaFree +
            ""","contextWindow":65536,"maxTokens":65536,"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsReasoningEffort":false,"supportsUsageInStreaming":true,"supportsStrictMode":false,"maxTokensField":"max_tokens"}}""",
            models[0].ToJsonString(), "loaded model");
        Equal(128000d, models[1].ContextWindow, "sleeping model without metadata");
        Names(["loaded \"llama-cpp-classify\" \"http://localhost:8080\" 65536", "sleeping \"llama-cpp-classify\" \"http://localhost:8080\" 128000"],
            catalog.AllModels.Where(model => model.Type == PiSharp.AI.Catalogs.CatalogModelType.Classifier).Select(model => $"{model.Id} {Rows([model], "api", "baseUrl", "contextWindow")[0]}"), "classifier models");
    }

    // Regression test for #9528.
    private static async Task LlamaThinking()
    {
        using var server = new LlamaServer(request => request.Path switch
        {
            "/models" => LlamaServer.Json("""{"data":[{"id":"qwen","status":{"value":"loaded"},"meta":{"n_ctx":32768}}]}"""),
            "/props" => LlamaServer.Json("""{"chat_template":"{% if enable_thinking %}think{% endif %}"}"""),
            _ => LlamaServer.NotFound()
        });
        var catalog = LlamaCatalog.Detached(); var publish = new LlamaPublish();
        await LlamaRefresh(catalog, server.Url, publish);
        Names(["GET /models", "GET /props?model=qwen&autoload=false"], server.Seen(), "requests");
        Check(server.Requests.All(request => request.Header("authorization") == "Bearer local"), "bearer key on every request");
        Equal("""{"id":"qwen","name":"qwen","api":"openai-completions","provider":"llama.cpp","baseUrl":""" + "\"" + server.Url + "/v1\"" +
            ""","reasoning":true,"thinkingLevelMap":{"off":"off","minimal":null,"low":null,"medium":"medium","high":null,"xhigh":null},"input":["text"],"cost":""" + LlamaFree +
            ""","contextWindow":32768,"maxTokens":32768,"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsReasoningEffort":false,"supportsUsageInStreaming":true,"supportsStrictMode":false,"maxTokensField":"max_tokens","thinkingFormat":"qwen-chat-template"}}""",
            catalog.ChatModels.Single().ToJsonString(), "reasoning chat model");
        Check(publish.Persisted?.CheckedAt is > 0 && publish.Persisted.LastModified is null, "persisted with checkedAt only");
    }

    private static async Task LlamaPersist()
    {
        using var server = new LlamaServer(request => request.Target switch
        {
            "/models" => LlamaServer.Json("""{"data":[{"id":"loaded","status":{"value":"loaded"},"meta":{"n_ctx":32768}},{"id":"sleeping","status":{"value":"sleeping"},"meta":{"n_ctx":32768}},{"id":"unloaded","status":{"value":"unloaded"}}]}"""),
            "/props?model=loaded&autoload=false" => LlamaServer.Json("{}"),
            _ => LlamaServer.NotFound()
        });
        var publish = new LlamaPublish();
        var first = LlamaCatalog.Detached();
        await LlamaRefresh(first, server.Url, publish);
        Names(["loaded", "sleeping"], first.ChatModels.Select(model => model.Id), "first chat models");
        Names(["\"loaded\" \"openai-completions\"", "\"sleeping\" \"openai-completions\"", "\"loaded\" \"llama-cpp-classify\"", "\"sleeping\" \"llama-cpp-classify\""],
            Rows(publish.Persisted!.Models.OfType<JsonObject>(), "id", "api"), "persisted");
        var requests = server.Seen().Length;
        var second = LlamaCatalog.Detached();
        await LlamaRefresh(second, server.Url, publish, allowNetwork: false);
        Equal(requests, server.Seen().Length, "a cache-only refresh sends nothing");
        Names([$"\"loaded\" \"{server.Url}/v1\" 32768", $"\"sleeping\" \"{server.Url}/v1\" 32768"], Rows(second.ChatModels, "id", "baseUrl", "contextWindow"), "restored chat models");
        Names([$"\"loaded\" \"llama-cpp-classify\" \"{server.Url}\" 32768", $"\"sleeping\" \"llama-cpp-classify\" \"{server.Url}\" 32768"],
            Rows(second.ClassifierJson, "id", "api", "baseUrl", "contextWindow"), "restored classifiers");
    }

    private static async Task LlamaCachedContext()
    {
        var loaded = true; string? unloadedArgs = null;
        using var server = new LlamaServer(request => request.Target switch
        {
            "/models" => LlamaServer.Json(loaded
                ? """{"data":[{"id":"qwen","status":{"value":"loaded"},"source":"preset","meta":{"n_ctx":65536,"n_ctx_train":128000}}]}"""
                : "{\"data\":[{\"id\":\"qwen\",\"status\":{\"value\":\"unloaded\"" + (unloadedArgs is null ? "" : ",\"args\":" + unloadedArgs) + """},"source":"preset","meta":{"n_ctx_train":128000}}]}"""),
            "/props?model=qwen&autoload=false" => LlamaServer.Json("{}"),
            "/props" => LlamaServer.Json("""{"role":"router","models_autoload":true}"""),
            _ => LlamaServer.NotFound()
        });
        var publish = new LlamaPublish();
        string[] Windows() => [.. publish.Persisted!.Models.OfType<JsonObject>().Select(model => model["contextWindow"]!.ToJsonString())];
        await LlamaRefresh(LlamaCatalog.Detached(), server.Url, publish);
        Names(["65536", "65536"], Windows(), "loaded windows");
        loaded = false;
        var second = LlamaCatalog.Detached();
        await LlamaRefresh(second, server.Url, publish);
        Names(["\"qwen\" 65536"], Rows(second.ChatModels, "id", "contextWindow"), "cached window for the unloaded preset");
        Names(["65536", "65536"], Windows(), "cached windows persisted");
        unloadedArgs = """["llama-server","--ctx-size","32768"]""";
        await LlamaRefresh(second, server.Url, publish);
        Names(["32768", "32768"], Windows(), "configured --ctx-size wins over the cache");
    }

    private static async Task LlamaAutoload()
    {
        using var server = new LlamaServer(request => request.Target switch
        {
            "/models" => LlamaServer.Json("""{"data":[{"id":"preset","status":{"value":"unloaded"},"source":"preset","meta":{"n_ctx":65536}},{"id":"failed-preset","status":{"value":"unloaded","failed":true},"source":"preset"},{"id":"cache","status":{"value":"unloaded"},"source":"cache"},{"id":"models-dir","status":{"value":"unloaded"},"source":"models_dir"}]}"""),
            "/props" => LlamaServer.Json("""{"role":"router","models_autoload":true}"""),
            _ => LlamaServer.NotFound()
        });
        var catalog = LlamaCatalog.Detached(); var publish = new LlamaPublish();
        await LlamaRefresh(catalog, server.Url, publish);
        Names(["GET /models", "GET /props"], server.Seen(), "one props request (router autoload)");
        Check(server.Requests.All(request => request.Header("authorization") == "Bearer local"), "bearer key");
        Names(["preset"], catalog.ChatModels.Select(model => model.Id), "chat models");
        Names(["\"preset\" \"openai-completions\"", "\"preset\" \"llama-cpp-classify\""], Rows(publish.Persisted!.Models.OfType<JsonObject>(), "id", "api"), "persisted");
    }

    private static async Task LlamaNoAutoload()
    {
        using var server = new LlamaServer(request => request.Target switch
        {
            "/models" => LlamaServer.Json("""{"data":[{"id":"preset","status":{"value":"unloaded"},"source":"preset"}]}"""),
            "/props" => LlamaServer.Json("""{"role":"router","models_autoload":false}"""),
            _ => LlamaServer.NotFound()
        });
        var catalog = LlamaCatalog.Detached();
        await LlamaRefresh(catalog, server.Url, new LlamaPublish());
        Equal(0, catalog.ChatModels.Count, "no chat models");
    }

    private const string LlamaDecisionCatalog = """{"data":[{"id":"qwen","status":{"value":"loaded"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"meta":{"n_ctx":32768}},{"id":"kev","status":{"value":"loaded"},"architecture":{"input_modalities":["text"],"output_modalities":["decisions"]},"meta":{"n_ctx":8192}},{"id":"laya","status":{"value":"sleeping"},"architecture":{"input_modalities":["text"],"output_modalities":["decisions"]}},{"id":"legacy","status":{"value":"loaded"}}]}""";

    // The catalog identifies decision models, so a refresh neither probes them nor reads their chat template; the classifier registry the
    // codemode tool and ctx.modelRegistry use (NativeExtensionModelOperations.CreateDefaultRegistry) restores them from models-store.json
    // and answers through /v1/systemone with the stored credential.
    private static async Task LlamaDecisionModels()
    {
        var systemOne = new List<string>();
        using var server = new LlamaServer(request =>
        {
            switch (request.Path)
            {
                case "/models": return LlamaServer.Json(LlamaDecisionCatalog);
                case "/props": return LlamaServer.Json("{}");
                case "/v1/systemone":
                    lock (systemOne) systemOne.Add(request.Body);
                    var payload = JsonNode.Parse(request.Body)!;
                    var answers = new JsonObject();
                    foreach (var (id, _) in payload["questions"]!.AsObject()) answers[id] = new JsonObject { ["type"] = "noul", ["noul"] = 0.82 };
                    return LlamaServer.Json(new JsonObject { ["model"] = payload["model"]!.DeepClone(), ["answers"] = answers, ["usage"] = new JsonObject { ["input_tokens"] = 42, ["output_tokens"] = 0 } }.ToJsonString());
                default: return LlamaServer.NotFound();
            }
        });
        using var sandbox = new Sandbox("llama-decisions");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), $$$$"""{"llama.cpp":{"type":"api_key","key":"local","env":{"LLAMA_BASE_URL":"{{{{server.Url}}}}"}}}""");
        var runtime = sandbox.Runtime();
        var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        var errors = await registry.RefreshAsync(allowNetwork: true, force: null, providers: ["llama.cpp"], CancellationToken.None);
        Equal(0, errors.Count, "refresh errors: " + string.Join(",", errors.Select(pair => pair.Key + ": " + pair.Value.Message)));
        Equal(0, systemOne.Count, "no System One probe");
        Names(["GET /props?model=qwen&autoload=false", "GET /props?model=legacy&autoload=false"],
            [.. server.Seen().Where(line => line.StartsWith("GET /props", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Reverse()], "props of the chat models only");
        Names(["qwen", "legacy"], registry.GetAllOfType(PiSharp.AI.Catalogs.CatalogModelType.Chat, "llama.cpp").Select(model => model.Id), "chat models");
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox.AgentDir, "models-store.json")))!["llama.cpp"]!["models"]!.AsArray();
        Names([$"\"qwen\" \"openai-completions\" \"{server.Url}/v1\"", $"\"legacy\" \"openai-completions\" \"{server.Url}/v1\"",
            $"\"qwen\" \"llama-cpp-classify\" \"{server.Url}\"", $"\"kev\" \"typesafe-system-one\" \"{server.Url}/v1\"",
            $"\"laya\" \"typesafe-system-one\" \"{server.Url}/v1\"", $"\"legacy\" \"llama-cpp-classify\" \"{server.Url}\""],
            Rows(stored.OfType<JsonObject>(), "id", "api", "baseUrl"), "models-store.json");

        // A process that has not refreshed reads the persisted catalog (the classifier registry of codemode and ctx.modelRegistry).
        var operations = new PiSharp.Cli.Extensions.NativeExtensionModelOperations(PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(
            name => sandbox.Vars.GetValueOrDefault(name), Path.Combine(sandbox.AgentDir, "auth.json")));
        Names(["qwen llama-cpp-classify", "kev typesafe-system-one", "laya typesafe-system-one", "legacy llama-cpp-classify"],
            operations.GetModelsOfType(ModelType.Classifier, "llama.cpp").Select(model => model.Value.GetProperty("id").GetString() + " " + model.Value.GetProperty("api").GetString()), "classifier registry");
        Names(["qwen", "kev", "laya", "legacy"], (await operations.GetAvailableOfTypeAsync(ModelType.Classifier, "llama.cpp", CancellationToken.None))
            .Select(model => model.Value.GetProperty("id").GetString()!), "available with the stored credential");
        var kev = operations.GetModelOfType(ModelType.Classifier, "llama.cpp", "kev")!;
        Equal(8192, kev.Value.GetProperty("contextWindow").GetInt32(), "kev context window");
        var result = await operations.ClassifyAsync(kev, new ClassifierContext(JsonData.Parse("""{"message":"I was charged twice."}"""),
            [new("angry", new ClassifierBoolQuestion("Is the customer angry?", "angry", "calm"))]), null, CancellationToken.None);
        Equal(null, result.ErrorMessage, "classify error");
        Equal(0.82, ((ClassifierBoolAnswer)result.GetAnswer("angry")!).Probability, "answer");
        Equal(42L, result.Usage?.Input ?? -1, "usage input");
        Names(["""{"model":"kev","state":{"message":"I was charged twice."},"questions":{"angry":{"type":"noul","instructions":"Is the customer angry?","criteria":{"true":"angry","false":"calm"}}}}"""],
            systemOne, "System One request body");
        Equal("Bearer local", server.Requests.Last(request => request.Path == "/v1/systemone").Header("authorization"), "System One bearer key");
    }

    private static void LlamaHybrid()
    {
        var catalog = LlamaCatalog.Detached();
        catalog.SetCatalog([
            Info("""{"id":"decide","status":{"value":"sleeping"},"architecture":{"output_modalities":["decisions"]}}"""),
            Info("""{"id":"hybrid","status":{"value":"loaded"},"architecture":{"output_modalities":["text","decisions"]}}""")], "http://localhost:8080");
        Names(["hybrid"], catalog.ChatModels.Select(model => model.Id), "chat models");
        Names(["\"decide\" \"typesafe-system-one\"", "\"hybrid\" \"typesafe-system-one\""], Rows(catalog.ClassifierJson, "id", "api"), "classifiers");
    }

    private sealed class ScriptedAuth(params string[] answers) : IProviderAuthInteraction
    {
        private readonly Queue<string> queue = new(answers);
        public List<AuthPrompt> Prompts { get; } = [];
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken) { Prompts.Add(prompt); return Task.FromResult(queue.Dequeue()); }
        public void Notify(AuthEvent authEvent) { }
    }

    private static async Task LlamaLogin()
    {
        Func<string, string?> none = _ => null;
        Equal(null, LlamaCatalog.Check(null, none), "check without configuration");
        Equal(null, LlamaCatalog.Resolve(null, none), "resolve without configuration");
        using var server = new LlamaServer(_ => LlamaServer.Json("""{"data":[]}"""));
        var interaction = new ScriptedAuth(server.Url, "secret");
        var (key, environment) = await LlamaCatalog.LoginAsync(interaction, none, CancellationToken.None);
        Names(["GET /models"], server.Seen(), "login reads the catalog");
        Equal("Bearer secret", server.Requests.Single().Header("authorization"), "with the entered key");
        Names(["Text llama.cpp server URL http://127.0.0.1:8080", "Secret API key (optional) "], interaction.Prompts.Select(prompt => $"{prompt.Kind} {prompt.Message} {prompt.Placeholder}"), "prompts");
        Equal("secret", key, "key");
        Equal(server.Url, environment["LLAMA_BASE_URL"], "env");
        var resolved = LlamaCatalog.Resolve(new("api_key", key, environment), none)!;
        Check(resolved.ApiKey == "secret" && resolved.BaseUrl == server.Url + "/v1" && resolved.ServerUrl == server.Url && resolved.Source == "stored credential", "resolve: " + resolved.BaseUrl);
        // LLAMA_BASE_URL alone configures the provider; LLAMA_API_KEY, else "local", is the key.
        var fromEnvironment = LlamaCatalog.Resolve(null, name => name == "LLAMA_BASE_URL" ? server.Url + "/v1/" : null)!;
        Check(fromEnvironment.ApiKey == "local" && fromEnvironment.ServerUrl == server.Url && fromEnvironment.Source == "LLAMA_BASE_URL", "env resolve");
        Equal("LLAMA_BASE_URL", LlamaCatalog.Check(null, name => name == "LLAMA_BASE_URL" ? server.Url : null), "env check source");

        // /login llama.cpp through the CLI's login host: an empty URL takes LLAMA_BASE_URL, an empty key stores none.
        using var sandbox = new Sandbox("llama-login");
        var host = new ProviderLoginHost(new AuthJsonCredentialStore(Path.Combine(sandbox.AgentDir, "auth.json")), () => new HttpClient())
        { ReadEnvironment = name => name == "LLAMA_BASE_URL" ? server.Url + "/v1" : null };
        var option = ProviderAuthCatalog.LoginOptions("api_key").Single(entry => entry.Provider.Id == "llama.cpp");
        Equal("llama.cpp server", option.Provider.ApiKeyName, "api key method name");
        await host.LoginAsync(option, new ScriptedAuth("", "  "), CancellationToken.None);
        Equal($$$$"""{"llama.cpp":{"type":"api_key","env":{"LLAMA_BASE_URL":"{{{{server.Url}}}}"}}}""",
            JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox.AgentDir, "auth.json")))!.ToJsonString(), "auth.json");
        // A server that is not reachable fails the login (nothing stored).
        server.Handle = _ => new(500, """{"error":{"message":"router offline"}}""");
        var failure = await Assert(() => LlamaCatalog.LoginAsync(new ScriptedAuth(server.Url, ""), none, CancellationToken.None));
        Equal("router offline", failure.Message, "login failure");
    }

    private static async Task<Exception> Assert(Func<Task> run)
    {
        try { await run(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("expected a failure");
    }

    private static async Task LlamaHuggingFace()
    {
        using var server = new LlamaServer(request => request.Target switch
        {
            "/api/models?search=qwen+coder&filter=gguf&sort=downloads&direction=-1&limit=20" => LlamaServer.Json("""[{"id":"owner/model-GGUF","downloads":1200},{"downloads":3},{"id":"other/x"}]"""),
            "/api/models/owner/model-GGUF?blobs=true" => LlamaServer.Json("""{"id":"owner/model-GGUF","gated":"manual","siblings":[{"rfilename":"model-Q5_K_M.gguf","size":6000},{"rfilename":"model-Q4_K_M-00001-of-00002.gguf","size":2000},{"rfilename":"model-Q4_K_M-00002-of-00002.gguf","size":3000},{"rfilename":"mmproj-F16.gguf","size":1000},{"rfilename":"sub/model-IQ2_XS.gguf"},{"rfilename":"README.md","size":1}]}"""),
            "/api/models/limited/x?blobs=true" => new(429, "{}"),
            _ => LlamaServer.NotFound()
        });
        var client = new HuggingFaceClient("hf-secret", server.Url);
        var results = await client.SearchAsync("qwen coder");
        Names(["owner/model-GGUF 1200", "other/x 0"], results.Select(model => $"{model.Id} {model.Downloads}"), "search");
        var details = await client.DetailsAsync("owner/model-GGUF");
        Equal("manual", details.Gated, "gated");
        Names(["Q4_K_M 5000", "Q5_K_M 6000", "IQ2_XS "], details.Quantizations.Select(entry => $"{entry.Name} {entry.Size}"), "quantizations");
        Check(server.Requests.All(request => request.Header("authorization") == "Bearer hf-secret"), "token on every request");
        Equal("Hugging Face rate limit reached", (await Assert(() => client.DetailsAsync("limited/x"))).Message, "rate limit");
        Equal("hf-secret", await HuggingFaceClient.FindTokenAsync(name => name == "HF_TOKEN" ? " hf-secret " : null), "HF_TOKEN");
        using var sandbox = new Sandbox("hf-token");
        sandbox.Write(Path.Combine(sandbox.Home, ".cache", "huggingface", "token"), "file-token\n");
        Equal("file-token", await HuggingFaceClient.FindTokenAsync(_ => null, sandbox.Home), "token file");
    }

    private static async Task LlamaLoad()
    {
        var status = "unloaded";
        using var server = new LlamaServer();
        server.Handle = request =>
        {
            if (request.Target == "/models/load" && request.Method == "POST")
            {
                status = "loading";
                _ = Task.Run(async () =>
                {
                    for (var wait = 0; wait < 100 && server.OpenStreams == 0; wait++) await Task.Delay(20);
                    server.Send("""{"model":"test-model","event":"status_change","data":{"status":"loading","progress":{"stages":["text_model","mmproj_model"],"current":"text_model","value":0.5}}}""");
                    await Task.Delay(50);
                    status = "loaded";
                    server.Send("""{"model":"test-model","event":"status_change","data":{"status":"loaded"}}""");
                });
                return LlamaServer.Json("""{"success":true}""");
            }
            return request.Target == "/models" ? LlamaServer.Json($$$"""{"data":[{"id":"test-model","status":{"value":"{{{status}}}"}}]}""") : LlamaServer.NotFound();
        };
        var progress = new List<LlamaProgress>();
        var model = await new LlamaClient(server.Url).LoadAndWaitAsync("test-model", entry => { lock (progress) progress.Add(entry); });
        Equal("loaded", model.Status, "status");
        Check(progress.Any(entry => entry.Message == "Loading text model" && entry.Ratio == 0.25), "stage progress: " + string.Join(",", progress));
        Equal("POST /models/load {\"model\":\"test-model\"}", server.Seen()[0], "load request");
        Check(server.Requests.Any(request => request.Path == "/models/sse"), "the event stream was read");
        // A failed load reports its exit code.
        server.Handle = request => request.Method == "POST" ? LlamaServer.Json("{}") : LlamaServer.Json("""{"data":[{"id":"broken","status":{"value":"unloaded","failed":true,"exit_code":3}}]}""");
        Equal("Model exited with code 3", (await Assert(() => new LlamaClient(server.Url).LoadAndWaitAsync("broken", _ => { }))).Message, "failed load");
        // unloadAndWait polls until the model is unloaded.
        var unloads = 0;
        server.Handle = request => request.Method == "POST" ? LlamaServer.Json("{}") : LlamaServer.Json($$$"""{"data":[{"id":"m","status":{"value":"{{{(Interlocked.Increment(ref unloads) > 1 ? "unloaded" : "loaded")}}}"}}]}""");
        await new LlamaClient(server.Url, "k").UnloadAndWaitAsync("m");
        Check(server.Seen().Contains("POST /models/unload {\"model\":\"m\"}"), "unload request");
    }

    private static async Task LlamaDownload()
    {
        var status = "missing";
        using var server = new LlamaServer();
        server.Handle = request =>
        {
            if (request.Target == "/models" && request.Method == "POST")
            {
                status = "downloading";
                _ = Task.Run(async () =>
                {
                    for (var wait = 0; wait < 100 && server.OpenStreams == 0; wait++) await Task.Delay(20);
                    server.Send("""{"model":"owner/repo:Q4_K_M","event":"download_progress","data":{"progress":{"https://example/model.gguf":{"done":512,"total":1024}}}}""");
                    await Task.Delay(50);
                    status = "unloaded";
                    server.Send("""{"model":"owner/repo:Q4_K_M","event":"download_finished","data":{}}""");
                });
                return LlamaServer.Json("""{"success":true}""");
            }
            if (request.Path == "/models")
                return LlamaServer.Json(status == "missing" ? """{"data":[]}""" : $$$"""{"data":[{"id":"owner/repo:Q4_K_M","status":{"value":"{{{status}}}"}}]}""");
            return LlamaServer.NotFound();
        };
        var progress = new List<LlamaProgress>();
        var models = await new LlamaClient(server.Url).DownloadAndWaitAsync("owner/repo:Q4_K_M", entry => { lock (progress) progress.Add(entry); });
        Names(["""{"id":"owner/repo:Q4_K_M","status":{"value":"unloaded"}}"""], models.Select(model => model.Raw.ToJsonString()), "returned catalog");
        Check(progress.Any(entry => entry is { Message: "Downloading model", Ratio: 0.5, Detail: "512 B / 1.00 KiB" }), "byte progress: " + string.Join(",", progress));
        Equal("POST /models {\"model\":\"owner/repo:Q4_K_M\"}", server.Seen()[0], "download request");
        Check(server.Seen().Contains("GET /models?reload=1"), "the catalog is reloaded at the end");
    }

    private static async Task LlamaRegistry()
    {
        using var sandbox = new Sandbox("llama-registry");
        var runtime = sandbox.Runtime();
        var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        Check(registry.GetProviderIds().Contains("llama.cpp"), "the built-in extension's provider is registered");
        Equal("llama.cpp", registry.GetProviderDisplayName("llama.cpp"), "display name");
        Equal(null, registry.CheckAuth("llama.cpp"), "dormant without a server URL");
        Equal(null, registry.ResolveLlamaAuth(), "no auth");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "models-store.json"), $$$"""
            {"llama.cpp":{"models":[{"id":"qwen","name":"qwen","api":"openai-completions","provider":"llama.cpp","baseUrl":"http://127.0.0.1:9/v1","reasoning":false,"input":["text"],"cost":{{{LlamaFree}}},"contextWindow":4096,"maxTokens":4096},{"type":"classifier","id":"qwen","name":"qwen","api":"llama-cpp-classify","provider":"llama.cpp","baseUrl":"http://127.0.0.1:9","input":["text"],"cost":{{{LlamaFree}}},"contextWindow":4096}],"checkedAt":1}}
            """);
        sandbox.Vars["LLAMA_BASE_URL"] = "http://127.0.0.1:9/v1";
        registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        Equal("LLAMA_BASE_URL", registry.CheckAuth("llama.cpp"), "configured by LLAMA_BASE_URL");
        Check(registry.GetAvailable().Any(model => model.Provider == "llama.cpp" && model.Id == "qwen"), "the restored model is available");
        Equal(1, registry.GetAllOfType(PiSharp.AI.Catalogs.CatalogModelType.Classifier, "llama.cpp").Count, "the classifier model is listed");
        var auth = registry.ResolveRequestAuth(registry.Find("llama.cpp", "qwen")!, out var error);
        Check(auth is { ApiKey: "local", Source: "LLAMA_BASE_URL" } && error is null, "request auth: " + error);
        sandbox.Vars["LLAMA_API_KEY"] = "env-key";
        registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        Equal("env-key", registry.ResolveRequestAuth(registry.Find("llama.cpp", "qwen")!, out _)?.ApiKey, "LLAMA_API_KEY");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), """{"llama.cpp":{"type":"api_key","key":"stored-key","env":{"LLAMA_BASE_URL":"http://127.0.0.1:9"}}}""");
        registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        Equal("stored credential", registry.CheckAuth("llama.cpp"), "stored credential");
        Check(registry.ResolveLlamaAuth() is { ApiKey: "stored-key", ServerUrl: "http://127.0.0.1:9" }, "getProviderAuth");
        // PI_OFFLINE-style startup restores without contacting the server; a live refresh of an unreachable router reports an error.
        var errors = await registry.RefreshAsync(allowNetwork: true, force: null, providers: ["llama.cpp"], CancellationToken.None);
        Check(errors.TryGetValue("llama.cpp", out var failure) && failure is LlamaConnectionException, "unreachable router: " + string.Join(",", errors.Values.Select(value => value.GetType().Name + " " + value.Message)));
        Check(registry.Find("llama.cpp", "qwen") is not null, "the restored model stays");
    }

    // The live chat route of a llama.cpp model is the openai-completions catalog route at the server's /v1 URL with the resolved key.
    private static async Task LlamaPrintMode()
    {
        using var server = new LlamaServer(request => request.Path switch
        {
            "/models" => LlamaServer.Json("""{"data":[{"id":"qwen","status":{"value":"loaded"},"meta":{"n_ctx":4096}}]}"""),
            "/props" => LlamaServer.Json("{}"),
            _ => LlamaServer.NotFound()
        });
        using var sandbox = new Sandbox("llama-print");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), $$$$"""{"llama.cpp":{"type":"api_key","env":{"LLAMA_BASE_URL":"{{{{server.Url}}}}"}}}""");
        var registry = await sandbox.Runtime().CreateModelRegistryAsync(CancellationToken.None);
        Equal(0, (await registry.RefreshAsync(allowNetwork: true, force: null, providers: ["llama.cpp"], CancellationToken.None)).Count, "refresh");
        sandbox.Respond = (_, _) => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(
                "data: " + JsonSerializer.Serialize(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "qwen", choices = new[] { new { index = 0, delta = new { role = "assistant", content = "hello from llama" }, finish_reason = (string?)null } } }) + "\n\n" +
                "data: " + JsonSerializer.Serialize(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "qwen", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 5, completion_tokens = 3, total_tokens = 8 } }) + "\n\n" +
                "data: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream")
        };
        var (code, stdout, stderr) = await sandbox.Run("-p", "--model", "llama.cpp/qwen", "hi");
        Equal(0, code, "exit; " + stderr);
        Equal("hello from llama\n", stdout, "stdout");
        var request = sandbox.Requests.Single();
        Equal(server.Url + "/v1/chat/completions", request.Url, "the server's /v1 route");
        Equal("Bearer local", request.Headers["Authorization"], "the resolved key (no stored key, no LLAMA_API_KEY)");
        var body = request.Json;
        Equal("qwen", body.GetProperty("model").GetString(), "model");
        Equal(4096, body.GetProperty("max_tokens").GetInt32(), "maxTokensField max_tokens with the model's maximum");
        Check(!body.TryGetProperty("store", out _), "supportsStore false sends no store: " + request.Body);
        Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString(), "supportsDeveloperRole false keeps the system role");
        Check(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(), "supportsUsageInStreaming");
        // The whole body, with the system prompt (dates, paths) and the built-in tools' descriptions and schemas elided.
        var pinned = JsonNode.Parse(request.Body!)!.AsObject();
        pinned["messages"]![0]!["content"] = "<system prompt>";
        foreach (var tool in pinned["tools"]!.AsArray())
        {
            tool!["function"]!["description"] = "<" + tool["function"]!["name"]!.GetValue<string>() + ">";
            tool["function"]!["parameters"] = "<schema>";
        }
        static string Tool(string name) => $$$"""{"type":"function","function":{"name":"{{{name}}}","description":"<{{{name}}}>","parameters":"<schema>"}}""";
        Equal("""{"model":"qwen","messages":[{"role":"system","content":"<system prompt>"},{"role":"user","content":[{"type":"text","text":"hi"}]}],"stream":true,"stream_options":{"include_usage":true},"max_tokens":4096,"tools":[""" +
            string.Join(",", new[] { "read", "bash", "edit", "write" }.Select(Tool)) + "]}",
            pinned.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), "chat request body");
    }
}
