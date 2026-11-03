using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;
using PiSharp.CodingAgent.Configuration;

// Authored from the Pi v1.2.0..v1.5.0 (f9a26365568127461c47a07a37dcb42c140a3ac5) changes to the provider catalog shards of
// @earendil-works/pi-ai@1.5.0, packages/ai/src/providers/all.ts, packages/coding-agent/src/core/{model-resolver,provider-composer}.ts
// and packages/coding-agent/schemas/models.schema.json. The catalog values are read from the embedded 1.5.0 shards and compared
// with expectations written from the upstream catalog definitions; nothing here is an upstream capture.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CatalogCases() =>
    [
        ("sync150.catalog-drops-realtime-and-image-only-chat-models", Sync(Sync150RealtimeAndImageDropped)),
        ("sync150.gpt-6.1-terra", Sync(Sync150Terra)),
        ("sync150.codex-gpt-6.1-sol-500k", Sync(Sync150CodexSolContext)),
        ("sync150.codex-chatgpt-models-removed", Sync(Sync150CodexRemoved)),
        ("sync150.minimax-m3.1-flash", Sync(Sync150MiniMaxFlash)),
        ("sync150.grok-xhigh-thinking", Sync(Sync150GrokXhigh)),
        ("sync150.kimi-k3.1-k3-family", Sync(Sync150KimiK31)),
        ("sync150.claude-disabled-thinking-off-map", Sync(Sync150ClaudeOffMap)),
        ("sync150.xiaomi-mimo-thinking-toggle", Sync(Sync150XiaomiToggle)),
        ("sync150.opencode-inference-endpoints", Sync(Sync150OpenCodeInference)),
        ("sync150.opencode-go-minimax-messages", Sync(Sync150OpenCodeGoMiniMax)),
        ("sync150.opencode-protocol-endpoints", Sync(Sync150OpenCodeProtocolEndpoints)),
        ("sync150.opencode-deepseek-v4.1-retention", Sync(Sync150OpenCodeDeepSeekRetention)),
        ("sync150.gpt-5.4-5.5-top-level-tools", Sync(Sync150TopLevelTools)),
        ("sync150.workers-ai-classifier-models", Sync(Sync150WorkersClassifiers)),
        ("sync150.workers-ai-classifier-pricing", Sync(Sync150WorkersPricing)),
        ("sync150.openrouter-anthropic-latest-aliases", Sync(Sync150OpenRouterLatestAliases)),
        ("sync150.kimi-coding-chat-completions", Sync(Sync150KimiCodingCompletions)),
        ("sync150.system-one-compat-catalog", Sync(Sync150SystemOneCatalogCompat)),
        ("sync150.system-one-compat-models-json-provider-compat", Sync150SystemOneModelsJsonCompat),
        ("sync150.default-models", Sync(Sync150DefaultModels)),
        ("sync150.default-openai-model-only-without-a-selection", Sync(Sync150DefaultOpenAiModel)),
        ("sync150.models-schema-v1.5.0", Sync(Sync150ModelsSchema)),
    ];

    private static JsonElement Shard(string provider, string id, CatalogModelType type = CatalogModelType.Chat)
    {
        Check(BuiltinModelCatalog.Get(provider).TryGetModel(type, id, out var model), $"{provider} lists {type} {id}");
        return model!.Raw.Value;
    }

    private static bool Absent(string provider, string id, CatalogModelType type = CatalogModelType.Chat) =>
        !BuiltinModelCatalog.Get(provider).TryGetModel(type, id, out _);

    /// <summary>A catalog field as compact JSON, or <c>absent</c>.</summary>
    private static string Field(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) ? JsonNode.Parse(value.GetRawText())?.ToJsonString() ?? "null" : "absent";

    private static void Sync150RealtimeAndImageDropped()
    {
        // models.dev v2 types these as realtime or image models, so they leave the chat catalog.
        foreach (var provider in new[] { "openai", "azure" }) Check(Absent(provider, "gpt-realtime-2.1"), provider + " gpt-realtime-2.1 dropped");
        Check(Absent("google", "gemini-3.1-flash-live-preview") && Absent("google", "gemini-3.1-flash-lite-image"), "google live and image models dropped");
        Check(Absent("google-vertex", "gemini-3.1-flash-lite-image"), "vertex image model dropped");
        foreach (var (provider, id, api) in new[] { ("openai", "chat-latest", "openai-responses"), ("openai", "gpt-5.6-cyber", "openai-responses"),
            ("google", "gemini-robotics-er-2-preview", "google-generative-ai") })
            Equal(api, Shard(provider, id).GetProperty("api").GetString(), provider + "/" + id);
        // Neither the 1.2.0 nor the 1.5.0 google shards list image models; OpenRouter's image catalog is still embedded.
        Check(BuiltinModelCatalog.Get("openrouter").Models.Any(model => model.Type == CatalogModelType.Image), "openrouter image models");
        foreach (var provider in BuiltinModelCatalog.ShardHashes.Keys)
            foreach (var model in BuiltinModelCatalog.Get(provider).Models.Where(model => model.Type == CatalogModelType.Chat))
                Check(!model.Id.Contains("realtime", StringComparison.Ordinal), $"{provider}/{model.Id} is not a realtime chat model");
    }

    private static void Sync150Terra()
    {
        var openai = Shard("openai", "gpt-6.1-terra");
        Equal("openai-responses", openai.GetProperty("api").GetString(), "openai api");
        Equal("""{"input":1,"output":5,"cacheRead":0.05,"cacheWrite":1.25,"tiers":[{"inputTokensAbove":272000,"input":2,"output":7.5,"cacheRead":0.1,"cacheWrite":2.5}]}""",
            Field(openai, "cost"), "openai cost and long-context tier");
        Equal("272000", Field(openai, "contextWindow"), "openai context"); Equal("128000", Field(openai, "maxTokens"), "openai max tokens");
        var map = """{"off":"none","minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":"max"}""";
        Equal(map, Field(openai, "thinkingLevelMap"), "openai thinking map: off is reasoning effort none, minimal unsupported");
        var compat = openai.GetProperty("compat");
        foreach (var flag in new[] { "supportsToolSearch", "supportsAdditionalTools", "supportsMidConvoEffort" })
            Check(compat.TryGetProperty(flag, out var value) && value.GetBoolean(), "openai " + flag);
        var azure = Shard("azure", "gpt-6.1-terra");
        Equal("azure-openai-responses", azure.GetProperty("api").GetString(), "azure api");
        Equal("""{"input":1,"output":5,"cacheRead":0.05,"cacheWrite":1.25}""", Field(azure, "cost"), "azure cost");
        Equal(map, Field(azure, "thinkingLevelMap"), "azure thinking map");
        var codex = Shard("openai-codex", "gpt-6.1-terra");
        Equal("openai-codex-responses", codex.GetProperty("api").GetString(), "codex api");
        Equal("500000", Field(codex, "contextWindow"), "codex context");
        Equal("none", codex.GetProperty("thinkingLevelMap").GetProperty("off").GetString(), "codex off");
    }

    private static void Sync150CodexSolContext()
    {
        Equal("500000", Field(Shard("openai-codex", "gpt-6.1-sol"), "contextWindow"), "codex gpt-6.1-sol");
        Equal("272000", Field(Shard("openai", "gpt-6.1-sol"), "contextWindow"), "openai gpt-6.1-sol keeps 272K");
    }

    private static void Sync150CodexRemoved()
    {
        Check(Absent("openai-codex", "gpt-5.5") && Absent("openai-codex", "gpt-5.3-codex-spark"), "codex drops the ChatGPT-rejected models");
        Check(!Absent("openai", "gpt-5.5"), "openai keeps gpt-5.5");
        Equal("gpt-6.1-sol", ModelResolver.DefaultModelFor("openai-codex"), "codex default");
    }

    private static void Sync150MiniMaxFlash()
    {
        foreach (var provider in new[] { "minimax", "minimax-cn" })
        {
            var row = Shard(provider, "MiniMax-M3.1-Flash");
            Equal("anthropic-messages", row.GetProperty("api").GetString(), provider + " api");
            Equal("""{"off":null,"minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":"max"}""", Field(row, "thinkingLevelMap"), provider + " effort map");
            Equal("""{"forceAdaptiveThinking":true}""", Field(row, "compat"), provider + " compat");
        }
        var global = Shard("minimax", "MiniMax-M3.1-Flash");
        Equal("""{"input":0.15,"output":0.6,"cacheRead":0.015,"cacheWrite":0}""", Field(global, "cost"), "minimax cost");
        Equal("1000000", Field(global, "contextWindow"), "context"); Equal("524288", Field(global, "maxTokens"), "max tokens");
        // The denylist hides MiniMax-M2, M2.1, M2.5 and M2.5-highspeed.
        foreach (var id in new[] { "MiniMax-M2", "MiniMax-M2.1", "MiniMax-M2.5", "MiniMax-M2.5-highspeed" }) Check(Absent("minimax", id), "hidden " + id);
    }

    private static void Sync150GrokXhigh()
    {
        Equal("""{"off":"none","minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":null}""", Field(Shard("xai", "grok-4.3"), "thinkingLevelMap"), "grok-4.3");
        Equal("""{"off":null,"minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":null}""", Field(Shard("xai", "grok-4.5"), "thinkingLevelMap"), "grok-4.5");
    }

    private static void Sync150KimiK31()
    {
        const string k3Map = """{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"}""";
        foreach (var (provider, pro, highspeed) in new[] { ("kimi-coding", "k3.1-pro", "k3.1-pro-highspeed"), ("moonshotai", "kimi-k3.1-pro", "kimi-k3.1-pro-highspeed") })
        {
            var proRow = Shard(provider, pro); var fastRow = Shard(provider, highspeed);
            Equal("""{"input":2,"output":10,"cacheRead":0.2,"cacheWrite":0}""", Field(proRow, "cost"), provider + " K3.1 Pro cost");
            Equal("""{"input":4,"output":20,"cacheRead":0.2,"cacheWrite":0}""", Field(fastRow, "cost"), provider + " K3.1 Pro Highspeed cost");
            foreach (var row in new[] { proRow, fastRow })
            {
                Equal(k3Map, Field(row, "thinkingLevelMap"), provider + " K3 effort map");
                var compat = row.GetProperty("compat");
                Check(compat.GetProperty("supportsReasoningEffort").GetBoolean() && compat.GetProperty("thinkingFormat").GetString() == "openai" &&
                    compat.GetProperty("supportsMidConvoToolAdditions").GetBoolean(), provider + " K3 compat, not K2's");
            }
        }
        Equal(k3Map, Field(Shard("opencode-go", "kimi-k3.1-pro"), "thinkingLevelMap"), "opencode-go K3.1 Pro uses the K3 map");
        foreach (var id in new[] { "moonshotai/kimi-k3.1-pro", "moonshotai/kimi-k3.1-pro-highspeed" })
            Equal("131072", Field(Shard("vercel-ai-gateway", id), "maxTokens"), "gateway output limit clamped like K3: " + id);
        Equal("131072", Field(Shard("openrouter", "moonshotai/kimi-k3.1-pro"), "maxTokens"), "openrouter output limit");
    }

    private static void Sync150ClaudeOffMap()
    {
        // DISABLED_THINKING_MODELS: off is a real level (no off key, so it is supported); claude-opus-5-5 still cannot turn thinking off.
        Equal("""{"xhigh":"xhigh","max":"max"}""", Field(Shard("anthropic", "claude-opus-5"), "thinkingLevelMap"), "claude-opus-5");
        Equal("""{"minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":"max"}""", Field(Shard("anthropic", "claude-haiku-5-5"), "thinkingLevelMap"),
            "claude-haiku-5-5");
        Equal("null", Field(Shard("anthropic", "claude-opus-5-5").GetProperty("thinkingLevelMap"), "off"), "claude-opus-5-5 off");
        Equal("\"none\"", Field(Shard("openrouter", "anthropic/claude-opus-5").GetProperty("thinkingLevelMap"), "off"), "openrouter claude-opus-5 off");
    }

    private static void Sync150XiaomiToggle()
    {
        foreach (var provider in new[] { "xiaomi", "xiaomi-token-plan-cn", "xiaomi-token-plan-ams", "xiaomi-token-plan-sgp" })
            foreach (var model in BuiltinModelCatalog.Get(provider).Models.Where(model => model.Type == CatalogModelType.Chat))
            {
                var row = model.Raw.Value;
                Equal("false", Field(row.GetProperty("compat"), "supportsReasoningEffort"), $"{provider}/{model.Id} sends only thinking.type");
                if (row.GetProperty("reasoning").GetBoolean())
                    Equal("""{"off":"off","minimal":null,"low":null,"medium":null,"high":"high","xhigh":null,"max":null}""", Field(row, "thinkingLevelMap"),
                        $"{provider}/{model.Id} exposes off and high");
            }
    }

    private static void Sync150OpenCodeInference()
    {
        foreach (var provider in new[] { "opencode", "opencode-go" })
            foreach (var model in BuiltinModelCatalog.Get(provider).Models)
                Check(model.BaseUrl.StartsWith(provider == "opencode" ? "https://opencode.ai/inference/" : "https://opencode.ai/inference/go/", StringComparison.Ordinal),
                    $"{provider}/{model.Id} at {model.BaseUrl}");
        Check(BuiltinProviders.TryGet("opencode-go", out var go) && go.Apis.Contains("google-generative-ai"), "opencode-go registers google-generative-ai");
        Equal("https://opencode.ai/inference/go/google/v1beta", Shard("opencode-go", "gemini-4-argon").GetProperty("baseUrl").GetString(), "opencode-go Gemini");
    }

    private static void Sync150OpenCodeGoMiniMax()
    {
        // The catalog no longer rewrites OpenCode Go MiniMax models to openai-completions; minimax-m2.7 is not listed at 1.5.0.
        Check(Absent("opencode-go", "minimax-m2.7"), "minimax-m2.7 not listed as openai-completions");
        var m3 = Shard("opencode-go", "minimax-m3");
        Equal("anthropic-messages", m3.GetProperty("api").GetString(), "minimax-m3 api");
        Equal("https://opencode.ai/inference/go/anthropic", m3.GetProperty("baseUrl").GetString(), "minimax-m3 base URL");
    }

    private static void Sync150OpenCodeProtocolEndpoints()
    {
        var expected = new Dictionary<(string, string), string>
        {
            [("opencode", "anthropic-messages")] = "https://opencode.ai/inference/anthropic",
            [("opencode", "google-generative-ai")] = "https://opencode.ai/inference/google/v1beta",
            [("opencode", "openai-completions")] = "https://opencode.ai/inference/openai/v1",
            [("opencode", "openai-responses")] = "https://opencode.ai/inference/openai/v1",
            [("opencode", "typesafe-system-one")] = "https://opencode.ai/inference/systemone/v1",
            [("opencode-go", "anthropic-messages")] = "https://opencode.ai/inference/go/anthropic",
            [("opencode-go", "google-generative-ai")] = "https://opencode.ai/inference/go/google/v1beta",
            [("opencode-go", "openai-completions")] = "https://opencode.ai/inference/go/openai/v1",
            [("opencode-go", "openai-responses")] = "https://opencode.ai/inference/go/openai/v1",
        };
        foreach (var provider in new[] { "opencode", "opencode-go" })
            foreach (var model in BuiltinModelCatalog.Get(provider).Models)
            {
                Check(expected.TryGetValue((provider, model.DeclaredApi), out var url), $"{provider} {model.DeclaredApi} has an endpoint");
                Equal(url, model.BaseUrl, $"{provider}/{model.Id}");
            }
        Equal("https://opencode.ai/inference/systemone/v1", Shard("opencode", "jev-1.13", CatalogModelType.Classifier).GetProperty("baseUrl").GetString(), "Jev classifier");
    }

    private static void Sync150OpenCodeDeepSeekRetention()
    {
        Equal("DeepSeek V4.1 Flash", Shard("opencode", "deepseek-v4.1-flash").GetProperty("name").GetString(), "renamed from V4 Flash");
        Equal("false", Field(Shard("opencode", "deepseek-v4.1-flash").GetProperty("compat"), "supportsLongCacheRetention"), "no prompt_cache_retention");
    }

    private static void Sync150TopLevelTools()
    {
        foreach (var provider in new[] { "openai", "opencode" })
            foreach (var id in new[] { "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-pro", "gpt-5.5" })
            {
                var compat = Shard(provider, id).GetProperty("compat");
                Check(!compat.TryGetProperty("supportsToolSearch", out _) && !compat.TryGetProperty("supportsAdditionalTools", out _), $"{provider}/{id} declares tools at the top level");
                Equal("true", Field(compat, "supportsMidConvoSystemMessages"), $"{provider}/{id} keeps mid-conversation system messages");
            }
    }

    private static void Sync150WorkersClassifiers()
    {
        Names(["@cf/cloudflare/clef", "@cf/cloudflare/clef-flash", "@cf/cloudflare/clef-omni", "typesafe/jev", "typesafe/jev-1.14.0"],
            BuiltinModelCatalog.Get("cloudflare-workers-ai").Models.Where(model => model.Type == CatalogModelType.Classifier).Select(model => model.Id),
            "workers ai classifiers");
        var omni = Shard("cloudflare-workers-ai", "@cf/cloudflare/clef-omni", CatalogModelType.Classifier);
        Equal("Clef Omni", omni.GetProperty("name").GetString(), "name");
        Equal("""["text","image"]""", Field(omni, "input"), "Clef Omni takes images");
        Equal("""{"imageInput":"images"}""", Field(omni, "compat"), "Clef Omni sends data URLs");
        Equal("""{"imageInput":"assets"}""", Field(Shard("cloudflare-workers-ai", "typesafe/jev-1.14.0", CatalogModelType.Classifier), "compat"), "Jev 1.14.0 assets");
    }

    private static void Sync150WorkersPricing()
    {
        var flash = Shard("cloudflare-workers-ai", "@cf/cloudflare/clef-flash", CatalogModelType.Classifier);
        Equal("0.038", Field(flash.GetProperty("cost"), "input"), "Clef Flash price"); Equal("24576", Field(flash, "contextWindow"), "Clef Flash context");
        Equal("0.042", Field(Shard("cloudflare-workers-ai", "typesafe/jev", CatalogModelType.Classifier).GetProperty("cost"), "input"), "Jev no longer free");
    }

    private static void Sync150OpenRouterLatestAliases()
    {
        var aliases = BuiltinModelCatalog.Get("openrouter").Models.Where(model => model.Id.StartsWith("~anthropic/claude-", StringComparison.Ordinal)).ToList();
        Check(aliases.Count >= 4, "the ~anthropic/claude-*-latest aliases");
        foreach (var alias in aliases)
        {
            Equal("anthropic-messages", alias.DeclaredApi, alias.Id + " api"); Equal("https://openrouter.ai/api", alias.BaseUrl, alias.Id + " base URL");
            Equal("""{"forceAdaptiveThinking":true}""", Field(alias.Raw.Value, "compat"), alias.Id + " adaptive thinking");
        }
    }

    private static void Sync150KimiCodingCompletions()
    {
        Check(BuiltinProviders.TryGet("kimi-coding", out var kimi), "kimi-coding provider");
        Names(["openai-completions"], kimi.Apis, "kimi-coding speaks Chat Completions");
        Equal<string?>("https://api.kimi.com/coding/v1", kimi.BaseUrl, "kimi-coding base URL");
        foreach (var model in BuiltinModelCatalog.Get("kimi-coding").Models)
        {
            Equal("openai-completions", model.DeclaredApi, model.Id + " api"); Equal("https://api.kimi.com/coding/v1", model.BaseUrl, model.Id + " base URL");
            var compat = model.Raw.Value.GetProperty("compat");
            Equal("false", Field(compat, "supportsDeveloperRole"), model.Id + " no developer role"); Equal("\"max_tokens\"", Field(compat, "maxTokensField"), model.Id + " max_tokens");
        }
        // Models without effort options keep the Kimi compat (deepseek thinking, no reasoning_effort).
        var highspeed = Shard("kimi-coding", "kimi-for-coding-highspeed").GetProperty("compat");
        Equal("\"deepseek\"", Field(highspeed, "thinkingFormat"), "highspeed thinking format"); Equal("false", Field(highspeed, "supportsReasoningEffort"), "highspeed effort");
    }

    private static void Sync150SystemOneCatalogCompat()
    {
        Equal("""{"imageInput":"assets","sendImageOptInHeader":true}""", Field(Shard("typesafe", "jev-latest", CatalogModelType.Classifier), "compat"), "TypeSafe");
        foreach (var model in BuiltinModelCatalog.Get("openrouter").Models.Where(model => model.Type == CatalogModelType.Classifier))
            Equal("""{"imageInput":"state-image-urls","sendImageOptInHeader":false}""", Field(model.Raw.Value, "compat"), "openrouter " + model.Id);
        // Text-only gateway entries carry no compat; the classifier detects it when needed.
        Equal("absent", Field(Shard("vercel-ai-gateway", "typesafe-ai/jev", CatalogModelType.Classifier), "compat"), "vercel gateway");
    }

    private static Task Sync150SystemOneModelsJsonCompat() => WithTemp("sync150-classifier-compat", root =>
    {
        // provider-composer.ts applyModelsJson: provider-level compat spreads over a classifier's compat; modelOverrides stay chat-only.
        var path = Path.Combine(root, "models.json");
        File.WriteAllText(path, """
            {"providers":{"typesafe":{"compat":{"imageInput":"state-image-urls"}},
              "cloudflare-workers-ai":{"baseUrl":"https://proxy.invalid/ai","modelOverrides":{"typesafe/jev":{"compat":{"imageInput":"images"}}}}}}
            """);
        Registry(out var registry, modelsPath: path);
        Equal<string?>(null, registry.GetError(), "no error");
        var typesafe = registry.GetAllOfType(CatalogModelType.Classifier, "typesafe").Single(model => model.Id == "jev-latest");
        Equal("""{"imageInput":"state-image-urls","sendImageOptInHeader":true}""", typesafe.Compat?.ToJsonString(), "provider compat over the catalog compat");
        var jev = registry.GetAllOfType(CatalogModelType.Classifier, "cloudflare-workers-ai").Single(model => model.Id == "typesafe/jev");
        Equal<string?>("https://proxy.invalid/ai", jev.BaseUrl, "provider baseUrl applies");
        Equal("""{"imageInput":"assets"}""", jev.Compat?.ToJsonString(), "modelOverrides do not reach classifiers");
        return Task.CompletedTask;
    });

    private static void Sync150DefaultModels()
    {
        foreach (var (provider, id) in new[] { ("github-copilot", "gpt-6.1-sol"), ("nvidia", "nvidia/nemotron-3.5-super-vl-120b-a12b"), ("xiaomi", "mimo-v2.6-pro"),
            ("xiaomi-token-plan-cn", "mimo-v2.6-pro"), ("xiaomi-token-plan-ams", "mimo-v2.6-pro"), ("xiaomi-token-plan-sgp", "mimo-v2.6-pro") })
        {
            Equal(id, ModelResolver.DefaultModelFor(provider), provider + " default");
            Check(!Absent(provider, id), $"{provider}/{id} is in its shard");
        }
        Check(Absent("xiaomi", "mimo-v2.5-pro") && Absent("github-copilot", "gpt-5.4"), "the old defaults left the catalog");
        Registry(out var registry, Env(("XIAOMI_API_KEY", "k")));
        Equal("xiaomi/mimo-v2.6-pro", ModelResolver.FindInitialModel(null, null, [], false, null, null, null, null, registry).Model?.Reference, "new default without a selection");
        Equal("xiaomi/mimo-v2.6-flash", ModelResolver.FindInitialModel(null, null, [], false, "xiaomi", "mimo-v2.6-flash", null, null, registry).Model?.Reference,
            "a saved selection is kept");
    }

    private static void Sync150DefaultOpenAiModel()
    {
        Equal("gpt-6.1-sol", ModelResolver.DefaultModelFor("openai"), "openai default");
        Registry(out var registry, Env(("OPENAI_API_KEY", "k")));
        Equal("openai/gpt-6.1-sol", ModelResolver.FindInitialModel(null, null, [], false, null, null, null, null, registry).Model?.Reference, "startup fallback");
        Equal("openai/gpt-5.5", ModelResolver.FindInitialModel(null, null, [], false, "openai", "gpt-5.5", null, null, registry).Model?.Reference,
            "the saved gpt-5.5 selection is never replaced");
        Equal("openai/gpt-5.5", ModelResolver.FindInitialModel("openai", "gpt-5.5", [], false, null, null, null, null, registry).Model?.Reference, "--model gpt-5.5 kept");
    }

    private static void Sync150ModelsSchema()
    {
        var raw = PiConfigurationSchemas.Read(PiConfigurationSchemas.ModelsFileName);
        Check(raw.Contains("\"API base URL. Also applies to the provider's built-in models.\"", StringComparison.Ordinal), "provider field descriptions");
        Check(raw.Contains("\"imageInput\"", StringComparison.Ordinal) && raw.Contains("\"sendImageOptInHeader\"", StringComparison.Ordinal), "SystemOneCompat fields");
        Check(PiConfigurationSchemas.Models.Check(JsonNode.Parse("""{"providers":{"typesafe":{"compat":{"imageInput":"assets","sendImageOptInHeader":false}}}}""")),
            "classifier compat accepted");
        Check(!PiConfigurationSchemas.Models.Check(JsonNode.Parse("""{"providers":{"typesafe":{"compat":{"imageInput":"urls"}}}}""")), "unknown imageInput rejected");
        Check(!PiConfigurationSchemas.Models.Check(JsonNode.Parse("""{"providers":{"local":{"models":[{"id":"m","cost":{"input":1}}]}}}""")), "cost fields still required");
        // pi-schema lists `required` after `properties` in the cost object; validation does not depend on keyword order.
        var cost = raw.IndexOf("Input cost in USD per million tokens.", StringComparison.Ordinal);
        Check(cost > 0 && raw.IndexOf("\"required\"", cost, StringComparison.Ordinal) > cost, "required after properties");
    }
}
