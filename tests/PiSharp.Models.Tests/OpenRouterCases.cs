using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;

// Authored from Pi v1.2.0 (06a466935536f8c9bcacb2d5efc0fc021be1bdff) packages/ai/src/providers/openrouter.ts
// (personalizeOpenRouterCatalog), providers/openrouter-api.ts (openRouterCost), coding-agent/src/core/remote-catalog-provider.ts
// (withPersonalizedCatalog), core/model-runtime.ts and ai/src/models.ts (RefreshModelsContext.baseUrl), following
// test/openrouter-user-models.test.ts and test/models-runtime.test.ts. Fake OpenRouter and pi.dev endpoints answer in process.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> OpenRouterCases() =>
    [
        ("openrouter.personalize-keeps-the-key-chat-models-limits-and-prices", OpenRouterPersonalize),
        ("openrouter.personalize-unchanged-on-401-403-404-and-failures", OpenRouterPersonalizeStatuses),
        ("openrouter.cost-per-million-negative-invalid-and-prompt-length-tiers", Sync(OpenRouterCost)),
        ("openrouter.registry-refresh-personalizes-saves-and-restores-offline", OpenRouterRegistryRefresh),
        ("openrouter.registry-refresh-needs-a-credential-and-uses-oauth-access", OpenRouterRegistryCredentials),
    ];

    private static readonly JsonObject ZeroCost = new() { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 };
    private static RegistryModel OpenRouterChat(string id) => RegistryModel.FromJson(new JsonObject
    {
        ["id"] = id, ["name"] = id, ["api"] = "openai-completions", ["provider"] = "openrouter", ["baseUrl"] = "https://openrouter.ai/api/v1",
        ["reasoning"] = false, ["input"] = new JsonArray("text"), ["cost"] = new JsonObject { ["input"] = 1, ["output"] = 2, ["cacheRead"] = 0.1, ["cacheWrite"] = 0 },
        ["contextWindow"] = 200_000, ["maxTokens"] = 50_000
    });
    private static readonly RegistryModel OpenRouterImage = RegistryModel.FromJson(new JsonObject
    {
        ["type"] = "image", ["id"] = "image", ["name"] = "image", ["api"] = "openrouter-images", ["provider"] = "openrouter",
        ["baseUrl"] = "https://openrouter.ai/api/v1", ["input"] = new JsonArray("text"), ["output"] = new JsonArray("image"), ["cost"] = ZeroCost.DeepClone()
    });
    private static readonly RegistryModel OpenRouterClassifier = RegistryModel.FromJson(new JsonObject
    {
        ["type"] = "classifier", ["id"] = "classifier", ["name"] = "classifier", ["api"] = "typesafe-system-one", ["provider"] = "openrouter",
        ["baseUrl"] = "https://openrouter.ai/api/v1", ["input"] = new JsonArray("text"), ["cost"] = ZeroCost.DeepClone(), ["contextWindow"] = 64_000
    });
    private const string UserModels = """
        {"data":[
          {"id":"listed","context_length":1000000,"top_provider":{"context_length":262144,"max_completion_tokens":32768},"pricing":{"prompt":"0.000003","completion":"0.000015"}},
          {"id":"no-limits","context_length":0,"top_provider":{"context_length":null,"max_completion_tokens":null}},
          {"id":"router","pricing":{"prompt":"-1","completion":"-1"}},
          {"name":"no id"},
          3
        ]}
        """;
    private static string Cost(RegistryModel model) => model.CloneJson()["cost"]!.ToJsonString();
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task OpenRouterPersonalize()
    {
        var endpoint = new CatalogEndpoint(_ => Json(UserModels));
        RegistryModel[] catalog = [OpenRouterChat("listed"), OpenRouterChat("no-limits"), OpenRouterChat("router"), OpenRouterChat("blocked"), OpenRouterImage, OpenRouterClassifier];
        var result = await OpenRouterUserModels.PersonalizeAsync(catalog, "sk-or-test", null, () => new HttpClient(endpoint, disposeHandler: false), CancellationToken.None);
        Names(["listed", "no-limits", "router", "image", "classifier"], result.Select(model => model.Id), "the key's chat models, other types kept");
        var request = endpoint.Requests.Single();
        Equal("https://openrouter.ai/api/v1/models/user", request.Url, "default base URL");
        Equal("application/json", request.Headers["accept"], "accept"); Equal("Bearer sk-or-test", request.Headers["authorization"], "bearer key");
        Equal(262_144d, result[0].ContextWindow, "top provider context"); Equal(32_768d, result[0].MaxTokens, "top provider max output");
        Equal("""{"input":3,"output":15,"cacheRead":0,"cacheWrite":0}""", Cost(result[0]), "the key's prices per 1M tokens");
        // A missing limit keeps the catalog value, so generator fixes for missing OpenRouter data survive; no pricing keeps the cost.
        Equal(200_000d, result[1].ContextWindow, "missing context"); Equal(50_000d, result[1].MaxTokens, "missing max output");
        Equal(Cost(OpenRouterChat("x")), Cost(result[1]), "missing pricing");
        // Router models report -1, meaning the price depends on the model they pick (Pi 1.2.0: 0, not -$1,000,000 per 1M tokens).
        Equal(ZeroCost.ToJsonString(), Cost(result[2]), "negative placeholder prices");
        Check(ReferenceEquals(OpenRouterImage, result[3]) && ReferenceEquals(OpenRouterClassifier, result[4]), "image and classifier models unchanged");
        // The configured base URL (a regional endpoint) is used without its trailing slashes.
        await OpenRouterUserModels.PersonalizeAsync(catalog, "k", "https://us.openrouter.ai/api/v1//", () => new HttpClient(endpoint, disposeHandler: false), CancellationToken.None);
        Equal("https://us.openrouter.ai/api/v1/models/user", endpoint.Requests[^1].Url, "regional base URL");
        // Without a key nothing is requested.
        Check(ReferenceEquals(catalog, await OpenRouterUserModels.PersonalizeAsync(catalog, null, null, () => throw new InvalidOperationException("network"), CancellationToken.None)), "no key");
    }

    private static async Task OpenRouterPersonalizeStatuses()
    {
        RegistryModel[] catalog = [OpenRouterChat("a")];
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            var endpoint = new CatalogEndpoint(_ => new HttpResponseMessage(status) { Content = new StringContent("") });
            Check(ReferenceEquals(catalog, await OpenRouterUserModels.PersonalizeAsync(catalog, "k", null, () => new HttpClient(endpoint, disposeHandler: false), CancellationToken.None)),
                $"{(int)status} returns the catalog unchanged");
        }
        var failing = new CatalogEndpoint(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Equal("OpenRouter model list request failed: 500", (await ThrowsAsync<InvalidOperationException>(() =>
            OpenRouterUserModels.PersonalizeAsync(catalog, "k", null, () => new HttpClient(failing, disposeHandler: false), CancellationToken.None), "500")).Message, "failure");
        foreach (var body in new[] { """{"models":[]}""", """{"data":{}}""", "[]", "null" })
        {
            var invalid = new CatalogEndpoint(_ => Json(body));
            Equal("Invalid OpenRouter model list response", (await ThrowsAsync<InvalidOperationException>(() =>
                OpenRouterUserModels.PersonalizeAsync(catalog, "k", null, () => new HttpClient(invalid, disposeHandler: false), CancellationToken.None), body)).Message, body);
        }
    }

    private static void OpenRouterCost()
    {
        Equal("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}""", OpenRouterUserModels.Cost(null).ToJsonString(), "no pricing");
        Equal("""{"input":0,"output":15,"cacheRead":0,"cacheWrite":0.3,"tiers":[{"inputTokensAbove":200000,"input":6,"output":15,"cacheRead":0,"cacheWrite":0.6}]}""",
            OpenRouterUserModels.Cost((JsonObject)JsonNode.Parse("""
                {"prompt":"-1","completion":"0.000015","input_cache_read":"abc","input_cache_write":"0.0000003",
                 "overrides":[{"min_prompt_tokens":200000,"prompt":"0.000006","input_cache_write":"0.0000006"},
                   {"min_prompt_tokens":1,"utc_start":0,"prompt":"1"},{"prompt":"2"},{"min_prompt_tokens":5,"utc_days":["mon"]}]}
                """)!).ToJsonString(), "fallbacks and prompt-length tiers");
        // A tier's negative rate keeps the base price.
        Equal("""{"input":2,"output":0,"cacheRead":0,"cacheWrite":0,"tiers":[{"inputTokensAbove":10,"input":2,"output":0,"cacheRead":0,"cacheWrite":0}]}""",
            OpenRouterUserModels.Cost((JsonObject)JsonNode.Parse("""{"prompt":"0.000002","overrides":[{"min_prompt_tokens":10,"prompt":"-1"}]}""")!).ToJsonString(), "tier fallback");
        // Number((parseFloat(value) * 1e6).toFixed(6)).
        Equal(0.123457, OpenRouterUserModels.PerMillion(JsonValue.Create("0.0000001234567"), -1), "six decimals");
        Equal(0.25, OpenRouterUserModels.PerMillion(JsonValue.Create(" \t2.5e-7xyz"), -1), "parseFloat prefix");
        Equal(0d, OpenRouterUserModels.PerMillion(JsonValue.Create("0"), -1), "free");
        Equal(-1d, OpenRouterUserModels.PerMillion(JsonValue.Create(""), -1), "empty");
        Equal(-1d, OpenRouterUserModels.PerMillion(JsonValue.Create("-0.5"), -1), "negative");
        Equal(-1d, OpenRouterUserModels.PerMillion(null, -1), "missing");
        Equal(3d, OpenRouterUserModels.PerMillion(JsonValue.Create(0.000003), -1), "a JSON number");
        Equal(0.007813, OpenRouterUserModels.ToFixed6(0.0078125), "a tie rounds up at the exact binary value");
        Equal(1.000001, OpenRouterUserModels.ToFixed6(1.0000005), "1.0000005 is above the tie in binary");
    }

    private static CatalogEndpoint OpenRouterEndpoint(Func<string> userModels, Func<HttpResponseMessage>? catalog = null) => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/models/user", StringComparison.Ordinal) ? Json(userModels())
            : request.RequestUri.AbsolutePath == "/api/models/providers/openrouter" ? catalog?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.NotFound));

    private static Task OpenRouterRegistryRefresh() => WithTemp("openrouter", async root =>
    {
        var modelsPath = Path.Combine(root, "models.json");
        await File.WriteAllTextAsync(modelsPath, """{"providers":{"openrouter":{"baseUrl":"https://us.openrouter.ai/api/v1/"}}}""");
        var builtin = BuiltinModelCatalog.Get("openrouter").Models;
        var chat = builtin.Where(model => model.Type == CatalogModelType.Chat).Select(model => model.Id).ToArray();
        var allowed = new[] { chat[0], chat[2], "openrouter/auto", "remote/new" };
        var data = string.Join(",", allowed.Select(id => "{\"id\":\"" + id + "\",\"top_provider\":{\"context_length\":4242},\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"-1\"}}"));
        // A pi.dev catalog newer than the built-in data adds a model; the key lists it too.
        var endpoint = OpenRouterEndpoint(() => "{\"data\":[" + data + "]}", () => Catalog("""
            {"models":[{"id":"remote/new","name":"Remote New","api":"openai-completions","baseUrl":"https://openrouter.ai/api/v1","reasoning":false,
              "input":["text"],"cost":{"input":9,"output":9,"cacheRead":0,"cacheWrite":0},"contextWindow":1000,"maxTokens":100}]}
            """));
        var store = new InMemoryModelsStore(); var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        ModelRegistry Registry(Func<string, string?> environment) => ModelRegistry.Create(new()
        {
            ModelsPath = modelsPath, Environment = environment, ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
            CreateCatalogClient = () => new HttpClient(endpoint, disposeHandler: false), Now = () => clock
        });
        var registry = Registry(Env(("OPENROUTER_API_KEY", "sk-or-env")));
        Names(chat, registry.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "built-in list before a refresh");
        var errors = await registry.RefreshAsync(true, null, ["openrouter"], CancellationToken.None);
        Equal(0, errors.Count, "no errors");
        Names(["https://catalog.invalid/api/models/providers/openrouter?types=chat%2Cimage%2Cclassifier", "https://us.openrouter.ai/api/v1/models/user"],
            endpoint.Requests.Select(request => request.Url), "pi.dev catalog, then the key's list at the models.json base URL");
        Equal("Bearer sk-or-env", endpoint.Requests[1].Headers["authorization"], "environment key");
        var personal = registry.GetAll().Where(model => model.Provider == "openrouter").ToList();
        Names(allowed, personal.Select(model => model.Id), "only the key's chat models");
        Check(personal.All(model => model.ContextWindow == 4242 && model.BaseUrl == "https://us.openrouter.ai/api/v1/"), "the key's limits; models.json still applies");
        Equal("""{"input":1,"output":0,"cacheRead":0,"cacheWrite":0}""", Cost(personal[2]), "openrouter/auto priced from the key's list");
        Names(builtin.Where(model => model.Type != CatalogModelType.Chat).Select(model => model.Type + ":" + model.Id),
            registry.GetAllOfType(CatalogModelType.Image, "openrouter").Concat(registry.GetAllOfType(CatalogModelType.Classifier, "openrouter")).Select(model => model.Type + ":" + model.Id),
            "image and classifier models kept");
        // The saved entry is the personalized list (all types), without validators.
        var stored = (await store.ReadAsync("openrouter", CancellationToken.None))!;
        Equal(personal.Count + builtin.Count(model => model.Type != CatalogModelType.Chat), stored.Models.Count, "personalized list saved");
        Check(stored.CheckedAt == clock.ToUnixTimeMilliseconds() && stored.LastModified is null && stored.ETag is null, "checkedAt only");
        // Offline, a new registry uses the saved list without any request.
        var offline = await ModelRegistry.CreateAsync(new()
        {
            ModelsPath = modelsPath, Environment = Env(), ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
            CreateCatalogClient = () => throw new InvalidOperationException("network offline")
        }, CancellationToken.None);
        Names(allowed, offline.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "restored offline");
        // Every network refresh personalizes again: no refresh interval or etag revalidation.
        clock = clock.AddMinutes(1);
        await registry.RefreshAsync(true, null, ["openrouter"], CancellationToken.None);
        Equal(4, endpoint.Requests.Count, "refreshed again within the interval");
        Check(!endpoint.Requests[2].Headers.ContainsKey("if-none-match"), "no validator");
        // An empty saved list falls back to the built-in models.
        await store.WriteAsync("openrouter", new([]), CancellationToken.None);
        var empty = await ModelRegistry.CreateAsync(new() { Environment = Env(), ModelsStore = store }, CancellationToken.None);
        Names(chat, empty.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "empty saved list");
    });

    private static async Task OpenRouterRegistryCredentials()
    {
        var builtin = BuiltinModelCatalog.Get("openrouter").Models.Where(model => model.Type == CatalogModelType.Chat).Select(model => model.Id).ToArray();
        // Without a credential the network phase is skipped (models.ts refresh), so neither pi.dev nor OpenRouter is asked.
        var endpoint = OpenRouterEndpoint(() => """{"data":[{"id":"openrouter/auto"}]}""");
        var store = new InMemoryModelsStore();
        var anonymous = ModelRegistry.Create(new() { Environment = Env(), ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
            CreateCatalogClient = () => new HttpClient(endpoint, disposeHandler: false) });
        Equal(0, (await anonymous.RefreshAsync(true, null, ["openrouter"], CancellationToken.None)).Count, "no errors without a key");
        Equal(0, endpoint.Requests.Count, "no requests without a credential");
        Names(builtin, anonymous.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "built-in list kept");
        // A stored OAuth credential uses its (refreshed) access token; a stored api_key wins over the environment.
        var oauth = ModelRegistry.Create(new()
        {
            Environment = Env(("OPENROUTER_API_KEY", "env")), ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
            CreateCatalogClient = () => new HttpClient(endpoint, disposeHandler: false),
            StoredCredentials = new Dictionary<string, ProviderStoredCredential> { ["openrouter"] = new("oauth") },
            ResolveOAuthKey = (provider, _) => Task.FromResult<string?>(provider == "openrouter" ? "oauth-access" : null)
        });
        await oauth.RefreshAsync(true, null, ["openrouter"], CancellationToken.None);
        Equal("Bearer oauth-access", endpoint.Requests[^1].Headers["authorization"], "OAuth access token");
        Equal("https://openrouter.ai/api/v1/models/user", endpoint.Requests[^1].Url, "built-in base URL");
        Names(["openrouter/auto"], oauth.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "OAuth key's list");
        var stored = ModelRegistry.Create(new()
        {
            Environment = Env(("OPENROUTER_API_KEY", "env")), ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
            CreateCatalogClient = () => new HttpClient(endpoint, disposeHandler: false),
            StoredCredentials = new Dictionary<string, ProviderStoredCredential> { ["openrouter"] = new("api_key", "stored-key") }
        });
        await stored.RefreshAsync(true, null, ["openrouter"], CancellationToken.None);
        Equal("Bearer stored-key", endpoint.Requests[^1].Headers["authorization"], "stored api_key");
        // A rejected key keeps the full list (for example a proxy without /models/user) and saves it.
        var rejecting = new CatalogEndpoint(request => request.RequestUri!.AbsolutePath.EndsWith("/models/user", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var proxied = ModelRegistry.Create(new() { Environment = Env(("OPENROUTER_API_KEY", "bad")), ModelsStore = new InMemoryModelsStore(),
            CatalogBaseUrl = "https://catalog.invalid", CreateCatalogClient = () => new HttpClient(rejecting, disposeHandler: false) });
        Equal(0, (await proxied.RefreshAsync(true, null, ["openrouter"], CancellationToken.None)).Count, "401 is not an error");
        Names(builtin, proxied.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "401 keeps the catalog");
        // A failing model list is the provider's refresh error, and the previous list stays.
        var failing = new CatalogEndpoint(request => request.RequestUri!.AbsolutePath.EndsWith("/models/user", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var broken = ModelRegistry.Create(new() { Environment = Env(("OPENROUTER_API_KEY", "k")), ModelsStore = new InMemoryModelsStore(),
            CatalogBaseUrl = "https://catalog.invalid", CreateCatalogClient = () => new HttpClient(failing, disposeHandler: false) });
        Equal("OpenRouter model list request failed: 502", (await broken.RefreshAsync(true, null, ["openrouter"], CancellationToken.None))["openrouter"].Message, "refresh error");
        Names(builtin, broken.GetAll().Where(model => model.Provider == "openrouter").Select(model => model.Id), "previous list kept");
    }
}
