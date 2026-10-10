using System.Collections.Immutable;
using System.Net;
using PiSharp.AI.Authentication;
using PiSharp.AI.Catalogs;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// test/classifier-models.test.ts and test/images-models.test.ts (models.ts classify/generateImages, typed reads,
// getAvailableOfType and auth merging), over the released catalog shards the CLI embeds.
internal static partial class Program
{
    private static FrozenModelCatalog Shard(string provider) => FrozenModelCatalog.ReadProviderJson(provider,
        File.ReadAllBytes(Path.Combine(RepositoryRoot(), "src", "PiSharp.Cli", "Models", provider + ".json")));

    private static ModelOperationsRegistry BuiltinRegistry(IReadOnlyDictionary<string, string> env) =>
        ModelOperationsRegistry.CreateBuiltin([Shard("openai"), Shard("openrouter")], ModelOperationsAuth.Standard(name => env.GetValueOrDefault(name)));

    private static readonly ClassifierContext ApprovalContext = new(Json("""{"text":"ok"}"""),
        Questions(("approved", new ClassifierBoolQuestion("Does the user approve?", "Approval", "No approval"))));

    private static ClassifierModel TestClassifier(string provider, string id, string api = "test-classifier", bool images = false) => Classifier($$$"""
        {"type":"classifier","id":"{{{id}}}","name":"{{{id}}}","api":"{{{api}}}","provider":"{{{provider}}}","baseUrl":"https://{{{provider}}}.test/v1","input":["text"{{{(images ? ",\"image\"" : "")}}}],"cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0},"contextWindow":1000}
        """);

    private static ImageModel TestImage(string provider, string id, string api = "test-images") => Image($$$"""
        {"type":"image","id":"{{{id}}}","name":"{{{id}}}","api":"{{{api}}}","provider":"{{{provider}}}","baseUrl":"https://{{{provider}}}.test/v1","input":["text"],"output":["image"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}
        """);

    /// <summary>A classifier API that records what the registry hands it.</summary>
    private sealed class RecordingClassifier(string api = "test-classifier") : IClassifierApi
    {
        public List<(ClassifierModel Model, ClassifierOptions? Options)> Calls { get; } = [];
        public string Api => api;
        public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((model, options));
            return Task.FromResult(new ClassifierResult(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Stop, 1));
        }
    }

    private static IEnumerable<(string, Func<Task>)> RegistryCases() =>
    [
        // extensions/llama/provider.ts getAllModels: a discovered catalog (llama.cpp) is read on every lookup, and the provider is
        // available once LLAMA_BASE_URL (or a stored credential) names the server.
        ("registry.provider-model-source-follows-a-discovered-llama-catalog", async () =>
        {
            var current = ImmutableArray<OperationModel>.Empty;
            var env = new Dictionary<string, string>();
            var registry = new ModelOperationsRegistry(ModelOperationsAuth.Standard(name => env.GetValueOrDefault(name)));
            registry.SetProvider(BuiltinModelOperationProviders.LlamaCpp([], () => current));
            Equal(0, registry.GetModelsOfType(ModelType.Classifier, "llama.cpp").Length, "empty catalog");
            current = [TestClassifier("llama.cpp", "qwen", "llama-cpp-classify"), TestClassifier("llama.cpp", "kev", "typesafe-system-one")];
            Names(["qwen", "kev"], registry.GetModelsOfType(ModelType.Classifier, "llama.cpp").Select(model => model.Id), "discovered models");
            Equal(0, (await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "llama.cpp")).Length, "dormant without a server URL");
            env["LLAMA_BASE_URL"] = "http://127.0.0.1:8080/v1/";
            Names(["qwen", "kev"], (await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "llama.cpp")).Select(model => model.Id), "available");
            var auth = await registry.GetAuthAsync("llama.cpp");
            Check(auth is { ApiKey: "local", BaseUrl: "http://127.0.0.1:8080/v1" } && auth.Env["LLAMA_BASE_URL"] == "http://127.0.0.1:8080", "llama auth");
            current = [];
            Equal(null, registry.GetModelOfType(ModelType.Classifier, "llama.cpp", "qwen"), "a later catalog drops the model");
        }),
        ("registry.builtin-shards-route-luna-to-decisions-and-openrouter-to-system-one-and-images", async () =>
        {
            var registry = BuiltinRegistry(new Dictionary<string, string> { ["OPENAI_API_KEY"] = "env-openai", ["OPENROUTER_API_KEY"] = "env-openrouter" });
            var luna = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna")!;
            Equal("openai-decisions", luna.Api, "api"); Names(["text", "image"], luna.Input, "input"); Equal(922000d, luna.ContextWindow, "context window");
            // The chat entry with the same id stays separate.
            Equal("openai-responses", registry.GetModelOfType(ModelType.Chat, "openai", "gpt-6-luna")?.Api, "chat entry");
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":[{"type":"predicate","name":"approved","probability":0.8}]}"""));
            var result = await registry.ClassifyAsync(luna, ApprovalContext with { Images = [new("aW1hZ2U=", "image/png")] }, new ClassifierOptions { Http = http.Client });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal(new ClassifierBoolAnswer(0.8), Answer<ClassifierBoolAnswer>(result, "approved"), "answer");
            Equal("https://api.openai.com/v1/decisions", http.Requests[0].Url, "url");
            Equal("Bearer env-openai", http.Requests[0].Headers["authorization"], "environment key");
            await registry.ClassifyAsync(luna, ApprovalContext, new ClassifierOptions { Http = http.Client, ApiKey = "explicit" });
            Equal("Bearer explicit", http.Requests[1].Headers["authorization"], "explicit key wins");
            var openRouterClassifiers = registry.GetModelsOfType(ModelType.Classifier, "openrouter");
            Check(openRouterClassifiers.Length > 0, "openrouter classifiers");
            foreach (var model in openRouterClassifiers)
            {
                Check(model is ClassifierModel { Api: "typesafe-system-one", BaseUrl: "https://openrouter.ai/api/v1" }, $"system one {model.Id}");
                Equal(null, registry.GetModelOfType(ModelType.Chat, "openrouter", model.Id), $"not chat {model.Id}");
            }
            var images = registry.GetModelsOfType(ModelType.Image, "openrouter");
            Check(images.Length > 0 && images.All(model => model.Api == "openrouter-images"), "openrouter images");
            var jev = (ClassifierModel)openRouterClassifiers.First(model => !((ClassifierModel)model).AcceptsImages);
            var systemOne = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"approved":{"type":"noul","noul":0.6}}}"""));
            var routed = await registry.ClassifyAsync(jev, ApprovalContext, new ClassifierOptions { Http = systemOne.Client });
            Equal(ModelOperationStopReason.Stop, routed.StopReason, routed.ErrorMessage ?? "system one");
            Equal("https://openrouter.ai/api/v1/systemone", systemOne.Requests[0].Url, "system one url");
            Equal("Bearer env-openrouter", systemOne.Requests[0].Headers["authorization"], "openrouter key");
            var imageHttp = FakeHttp.Always(() => FakeHttp.Json(ImageResponse));
            var generated = await registry.GenerateImagesAsync((ImageModel)registry.GetModelOfType(ModelType.Image, "openrouter", "google/gemini-2.5-flash-image")!,
                DogPrompt, new ImagesOptions { Http = imageHttp.Client });
            Equal(ModelOperationStopReason.Stop, generated.StopReason, generated.ErrorMessage ?? "images");
            Equal("Bearer env-openrouter", imageHttp.Requests[0].Headers["authorization"], "image key");
            Body("""{"model":"google/gemini-2.5-flash-image","messages":[{"role":"user","content":[{"type":"text","text":"Generate a dog"}]}],"stream":false,"modalities":["image","text"]}""",
                imageHttp.Requests[0].Body, "catalog image model");
        }),
        ("registry.failures-are-results-never-exceptions", async () =>
        {
            var empty = BuiltinRegistry(new Dictionary<string, string>());
            var http = FakeHttp.Always(() => FakeHttp.Json("{}"));
            var luna = (ClassifierModel)empty.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna")!;
            var unconfigured = await empty.ClassifyAsync(luna, ApprovalContext, new ClassifierOptions { Http = http.Client });
            Equal("Provider is not configured: openai", unconfigured.ErrorMessage, "unconfigured"); Equal(ModelOperationStopReason.Error, unconfigured.StopReason, "stop");
            var ghost = await empty.ClassifyAsync(TestClassifier("ghost", "m"), ApprovalContext);
            Equal("Unknown provider: ghost", ghost.ErrorMessage, "ghost");
            var ghostImages = await empty.GenerateImagesAsync(TestImage("ghost", "m"), DogPrompt);
            Equal("Unknown provider: ghost", ghostImages.ErrorMessage, "ghost images");
            var recording = new RecordingClassifier();
            var registry = new ModelOperationsRegistry((_, _) => ValueTask.FromResult<ProviderAuthResult?>(new(null)));
            registry.SetProvider(new ModelOperationsProvider("test", "Test")
            {
                Models = [TestClassifier("test", "text-only")], Classifiers = ImmutableDictionary<string, IClassifierApi>.Empty.Add("test-classifier", recording)
            });
            var textOnly = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "test", "text-only")!;
            var rejected = await registry.ClassifyAsync(textOnly, ApprovalContext with { Images = [new("aW1hZ2U=", "image/png")] });
            var withoutImages = await registry.ClassifyAsync(textOnly, ApprovalContext with { Images = [] });
            Equal("Model test/text-only does not accept image input", rejected.ErrorMessage, "image input");
            Equal(ModelOperationStopReason.Stop, withoutImages.StopReason, "empty images");
            Equal(1, recording.Calls.Count, "one provider call");
            registry.SetProvider(new ModelOperationsProvider("chat-only", "Chat") { Models = [TestClassifier("chat-only", "c"), TestImage("chat-only", "i")] });
            Equal("Provider chat-only does not support classification", (await registry.ClassifyAsync(TestClassifier("chat-only", "c"), ApprovalContext)).ErrorMessage, "no classifiers");
            Equal("Provider chat-only does not support image generation", (await registry.GenerateImagesAsync(TestImage("chat-only", "i"), DogPrompt)).ErrorMessage, "no images");
            registry.SetProvider(new ModelOperationsProvider("wrong-api", "Wrong")
            {
                Models = [TestClassifier("wrong-api", "c")], Classifiers = ImmutableDictionary<string, IClassifierApi>.Empty.Add("other", new RecordingClassifier("other")),
                Images = ImmutableDictionary<string, IImagesApi>.Empty.Add("other-images", OpenRouterImages.Instance)
            });
            Equal("Provider wrong-api has no classifier implementation for \"test-classifier\"", (await registry.ClassifyAsync(TestClassifier("wrong-api", "c"), ApprovalContext)).ErrorMessage, "classifier api");
            Equal("Provider wrong-api has no image generation implementation for \"test-images\"", (await registry.GenerateImagesAsync(TestImage("wrong-api", "i"), DogPrompt)).ErrorMessage, "image api");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var aborted = await registry.ClassifyAsync(textOnly, ApprovalContext, cancellationToken: cancelled.Token);
            Equal(ModelOperationStopReason.Aborted, aborted.StopReason, "aborted"); Equal(1, recording.Calls.Count, "no call when aborted");
            Equal(0, http.Requests.Count, "no requests");
        }),
        ("registry.available-models-follow-credentials", async () =>
        {
            var apiKey = ModelOperationsRegistry.CreateBuiltin([Shard("openai")], (request, _) => ValueTask.FromResult<ProviderAuthResult?>(new("secret")));
            var oauth = ModelOperationsRegistry.CreateBuiltin([Shard("openai")], (request, _) => ValueTask.FromResult<ProviderAuthResult?>(new("access") { IsOAuth = true }));
            var none = ModelOperationsRegistry.CreateBuiltin([Shard("openai")], (request, _) => ValueTask.FromResult<ProviderAuthResult?>(null));
            Names(["gpt-6-luna"], (await apiKey.GetAvailableOfTypeAsync(ModelType.Classifier, "openai")).Select(model => model.Id), "api key");
            Equal(0, (await oauth.GetAvailableOfTypeAsync(ModelType.Classifier, "openai")).Length, "ChatGPT OAuth lists no Decisions models");
            Check((await oauth.GetAvailableOfTypeAsync(ModelType.Chat, "openai")).Any(model => model.Id == "gpt-6-luna"), "chat models stay available");
            Equal(0, (await none.GetAvailableOfTypeAsync(ModelType.Classifier)).Length, "unconfigured");
            Equal(0, (await apiKey.GetAvailableOfTypeAsync(ModelType.Classifier, "ghost")).Length, "unknown provider");
        }),
        ("registry.auth-headers-env-transform-and-base-url-merge-in-order", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"approved":{"type":"noul","noul":0.5}}}"""));
            ProviderAuthRequest? seen = null;
            var registry = new ModelOperationsRegistry((request, _) =>
            {
                seen = request;
                return ValueTask.FromResult<ProviderAuthResult?>(new("resolved") { BaseUrl = "https://resolved.test/api", Headers = [new("x-base", "1"), new("x-shared", "auth")],
                    Env = ImmutableDictionary<string, string>.Empty.Add("A", "auth").Add("B", "auth") });
            });
            var model = TestClassifier("proxy", "jev", "typesafe-system-one") with { Headers = [new("X-Shared", "model"), new("x-model", "m")] };
            registry.SetProvider(BuiltinModelOperationProviders.Create("proxy", [model]) with
            { Classifiers = ImmutableDictionary<string, IClassifierApi>.Empty.Add("typesafe-system-one", TypeSafeSystemOneClassifier.Instance) });
            var result = await registry.ClassifyAsync(model, ApprovalContext, new ClassifierOptions
            {
                Http = http.Client, Headers = [new("x-extra", "2")], Env = ImmutableDictionary<string, string>.Empty.Add("B", "request"),
                TransformHeaders = (headers, _) => ValueTask.FromResult(headers.Add(new("x-transformed", string.Join(",", headers.Select(pair => pair.Key)))))
            });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal("https://resolved.test/api/systemone", http.Requests[0].Url, "resolved base url");
            Equal("Bearer resolved", http.Requests[0].Headers["authorization"], "resolved key");
            Equal("model", http.Requests[0].Headers["x-shared"], "model header beats auth header");
            Equal("1", http.Requests[0].Headers["x-base"], "auth header"); Equal("2", http.Requests[0].Headers["x-extra"], "request header");
            Equal("x-base,X-Shared,x-model,x-extra", http.Requests[0].Headers["x-transformed"], "transform sees the merged headers last");
            Equal("B", string.Join(",", seen!.Env!.Keys), "request env reaches auth");
        }),
        ("registry.standard-auth-stored-keys-and-llama-servers", async () =>
        {
            var env = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "env-openai", ["LLAMA_BASE_URL"] = "http://llama-box.test:8080/v1/" };
            var stored = ModelOperationsAuth.Standard(name => env.GetValueOrDefault(name),
                (provider, _) => ValueTask.FromResult(provider == "openai" ? new StoredApiKeyCredential("stored-openai") : null));
            Equal("stored-openai", (await stored(new("openai"), default))?.ApiKey, "stored key owns the provider");
            Equal("explicit", (await stored(new("openai", "explicit"), default))?.ApiKey, "explicit key");
            Equal(null, await stored(new("typesafe"), default), "missing typesafe key");
            env["TYPESAFE_API_KEY"] = "env-typesafe";
            Equal("env-typesafe", (await stored(new("typesafe"), default))?.ApiKey, "env key");
            var llama = await stored(new("llama.cpp"), default);
            Equal("local", llama?.ApiKey, "keyless llama"); Equal("http://llama-box.test:8080/v1", llama?.BaseUrl, "llama inference url");
            var server = LlamaServer();
            var registry = new ModelOperationsRegistry(stored);
            var chat = TestClassifier("llama.cpp", "qwen", "llama-cpp-classify") with { BaseUrl = "http://127.0.0.1:8080" };
            var decision = TestClassifier("llama.cpp", "jev", "typesafe-system-one") with { BaseUrl = "http://127.0.0.1:8080/v1" };
            registry.SetProvider(BuiltinModelOperationProviders.LlamaCpp([chat, decision]));
            var result = await registry.ClassifyAsync(chat, Pick, new ClassifierOptions { Http = server.Client });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "llama");
            Check(server.Requests.All(request => request.Url.StartsWith("http://llama-box.test:8080/", StringComparison.Ordinal) && !request.Path.StartsWith("/v1", StringComparison.Ordinal)), "server root");
            Check(server.Requests.All(request => request.Headers["authorization"] == "Bearer local"), "local key");
            var systemOne = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"pick":{"type":"choice","choice":"a","probabilities":{"a":0.6,"b":0.4},"confidence":0.2}}}"""));
            var native = await registry.ClassifyAsync(decision, Pick, new ClassifierOptions { Http = systemOne.Client });
            Equal(ModelOperationStopReason.Stop, native.StopReason, native.ErrorMessage ?? "decision model");
            Equal("http://llama-box.test:8080/v1/systemone", systemOne.Requests[0].Url, "decision models use System One");
            env.Remove("LLAMA_BASE_URL");
            Equal(null, await stored(new("llama.cpp"), default), "llama needs a server url");
        }),
        // provider-composer.ts composeApiKeyAuth for an extension provider without apiKey, oauth or a built-in provider: an explicit
        // request key or the stored api_key credential, else not configured. The registry resolves it for providers it does not list.
        ("registry.resolve-auth-reaches-providers-the-registry-does-not-list", async () =>
        {
            var registry = new ModelOperationsRegistry(ModelOperationsAuth.Standard(_ => null,
                (provider, _) => ValueTask.FromResult(provider == "ext-acme" ? new StoredApiKeyCredential("stored-acme") : null)));
            Equal(null, await registry.GetAuthAsync("ext-acme"), "GetAuthAsync covers listed providers only");
            Equal("stored-acme", (await registry.ResolveAuthAsync(new("ext-acme")))?.ApiKey, "stored credential");
            Equal("explicit", (await registry.ResolveAuthAsync(new("ext-acme", "explicit")))?.ApiKey, "explicit request key");
            Equal(null, await registry.ResolveAuthAsync(new("ext-other")), "nothing configures it");
            Equal("explicit", (await registry.ResolveAuthAsync(new("ext-other", "explicit")))?.ApiKey, "a request key alone");
        }),
        ("registry.cli-default-registry-embeds-the-catalog-shards", async () =>
        {
            var registry = PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(
                name => name == "OPENAI_API_KEY" ? "env-openai" : null, authPath: null);
            Check(registry.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna") is ClassifierModel, "openai shard");
            Check(registry.GetModelsOfType(ModelType.Image, "openrouter").Length > 0, "openrouter shard");
            Names(["gpt-6-luna"], (await registry.GetAvailableOfTypeAsync(ModelType.Classifier)).Select(model => model.Id), "available with OPENAI_API_KEY only");
            var directory = Directory.CreateTempSubdirectory("pisharp-classifiers-");
            try
            {
                var auth = Path.Combine(directory.FullName, "auth.json");
                await File.WriteAllTextAsync(auth, """{"openai":{"type":"oauth","access":"chatgpt-token","refresh":"r","expires":9999999999999},"openrouter":{"type":"api_key","key":"$ROUTER_KEY"}}""");
                var withStore = PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(
                    name => name switch { "OPENAI_API_KEY" => "env-openai", "ROUTER_KEY" => "templated", _ => null }, auth);
                Equal(0, (await withStore.GetAvailableOfTypeAsync(ModelType.Classifier, "openai")).Length, "stored OAuth owns openai");
                var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"approved":{"type":"noul","noul":0.5}}}"""));
                var jev = (ClassifierModel)withStore.GetModelsOfType(ModelType.Classifier, "openrouter").First(model => !model.AcceptsImages);
                await withStore.ClassifyAsync(jev, ApprovalContext, new ClassifierOptions { Http = http.Client });
                Equal("Bearer templated", http.Requests[0].Headers["authorization"], "stored key template resolves from the environment");
            }
            finally { directory.Delete(recursive: true); }
        }),
        ("registry.cli-stored-oauth-is-refreshed-persisted-and-owns-the-provider", async () =>
        {
            var directory = Directory.CreateTempSubdirectory("pisharp-classifiers-oauth-");
            try
            {
                var auth = Path.Combine(directory.FullName, "auth.json");
                await File.WriteAllTextAsync(auth, """{"openai":{"type":"oauth","access":"stale-access","refresh":"old-refresh","expires":1000,"clientId":"client-7","scopes":["openid"]},"typesafe":{"type":"oauth","access":"t","refresh":"r","expires":9999999999999}}""");
                var tokenServer = FakeHttp.Always(() => FakeHttp.Json("""{"access_token":"fresh-access","refresh_token":"new-refresh","expires_in":3600,"scope":"openid resource.invoke chatgpt.tokens.use.direct","token_type":"Bearer"}"""));
                var env = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "env-openai", ["TYPESAFE_API_KEY"] = "env-typesafe" };
                var registry = PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(name => env.GetValueOrDefault(name), auth, () => tokenServer.Client);
                var luna = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna")!;
                var decisions = FakeHttp.Always(() => FakeHttp.Json("""{"answers":[{"type":"predicate","name":"approved","probability":0.8}]}"""));
                // Listing never refreshes: a stored OAuth credential counts as configured.
                Equal(0, (await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "openai")).Length, "ChatGPT OAuth lists no Decisions models");
                Equal(0, tokenServer.Requests.Count, "no refresh while listing");
                var result = await registry.ClassifyAsync(luna, ApprovalContext, new ClassifierOptions { Http = decisions.Client });
                Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "refreshed");
                Equal("Bearer fresh-access", decisions.Requests[0].Headers["authorization"], "refreshed access token");
                Equal(1, tokenServer.Requests.Count, "one refresh");
                Equal("https://auth.openai.com/api/accounts/oauth/token", tokenServer.Requests[0].Url, "token url");
                Equal("application/x-www-form-urlencoded", tokenServer.Requests[0].Headers["content-type"], "form");
                Equal("application/json", tokenServer.Requests[0].Headers["accept"], "accept");
                Body("grant_type=refresh_token&client_id=client-7&refresh_token=old-refresh&resource=https%3A%2F%2Fapi.openai.com%2Fv1", tokenServer.Requests[0].Body, "refresh form");
                var stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(auth))!["openai"]!;
                Equal("fresh-access", stored["access"]!.GetValue<string>(), "rotation persisted");
                Equal("new-refresh", stored["refresh"]!.GetValue<string>(), "refresh token rotated");
                Equal("client-7", stored["clientId"]!.GetValue<string>(), "client id kept");
                Check(stored["expires"]!.GetValue<double>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_000_000, "new expiry minus the three-minute margin");
                // The refreshed token is reused without another refresh.
                await registry.ClassifyAsync(luna, ApprovalContext, new ClassifierOptions { Http = decisions.Client });
                Equal(1, tokenServer.Requests.Count, "fresh token reused"); Equal("Bearer fresh-access", decisions.Requests[1].Headers["authorization"], "reused");
                // A stored OAuth credential of a provider without an OAuth refresh owns it: no environment fallback.
                var jev = new ClassifierModel("jev", "Jev", "typesafe-system-one", "typesafe", "https://api.typesafe.ai/v1", ["text"], ModelCost.Free, 1000);
                Equal("Provider is not configured: typesafe", (await registry.ClassifyAsync(jev, ApprovalContext, new ClassifierOptions { Http = decisions.Client })).ErrorMessage, "typesafe oauth");
                var systemOne = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"approved":{"type":"noul","noul":0.5}}}"""));
                var explicitKey = await registry.ClassifyAsync(jev, ApprovalContext, new ClassifierOptions { Http = systemOne.Client, ApiKey = "explicit" });
                Equal(ModelOperationStopReason.Stop, explicitKey.StopReason, explicitKey.ErrorMessage ?? "explicit key");
                Equal("Bearer explicit", systemOne.Requests[0].Headers["authorization"], "an explicit key bypasses the store");
                // A failed refresh is an error result with upstream's message and cause.
                await File.WriteAllTextAsync(auth, """{"openai":{"type":"oauth","access":"stale-access","refresh":"old-refresh","expires":1000,"clientId":"client-7"}}""");
                var failing = FakeHttp.Always(() => FakeHttp.Text("invalid_grant", HttpStatusCode.BadRequest));
                var broken = PiSharp.Cli.Extensions.NativeExtensionModelOperations.CreateDefaultRegistry(name => env.GetValueOrDefault(name), auth, () => failing.Client);
                var failed = await broken.ClassifyAsync(luna, ApprovalContext, new ClassifierOptions { Http = decisions.Client });
                Equal(ModelOperationStopReason.Error, failed.StopReason, "refresh failure");
                Equal("OAuth refresh failed for openai: OpenAI OAuth token request failed (400): invalid_grant", failed.ErrorMessage, "refresh message");
                Equal(2, decisions.Requests.Count, "nothing sent with a failed refresh");
            }
            finally { directory.Delete(recursive: true); }
        })
    ];
}
