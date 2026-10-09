using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;

// Ported from packages/coding-agent/test/model-registry.test.ts (v1.1.0) "baseUrl override", "custom models merge behavior",
// "modelOverrides" and "API key resolution" groups, plus model-config.ts load/validation behaviour.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ModelsJsonCases() =>
    [
        ("models-json.base-url-override-keeps-and-rewrites-built-ins", () => WithModels("""{"providers":{"anthropic":{"baseUrl":"https://my-proxy.example.com/v1"}}}""", BaseUrlOverride)),
        ("models-json.headers-only-override-resolves-at-request-time", () => WithModels("""{"providers":{"deepseek":{"headers":{"X-Custom-Header":"custom-value"}}}}""", HeadersOnly)),
        ("models-json.mix-override-and-custom-models", () => WithModels("""
            {"providers":{"anthropic":{"baseUrl":"https://anthropic-proxy.example.com/v1"},
              "google":{"baseUrl":"https://google-proxy.example.com/v1","apiKey":"test-key","api":"google-generative-ai",
                "models":[{"id":"gemini-custom","name":"gemini-custom","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":100000,"maxTokens":8000}]}}}
            """, MixedOverride)),
        ("models-json.custom-models-inherit-api-and-base-url", () => WithModels("""
            {"providers":{"openrouter":{"models":[{"id":"fake-provider/fake-model","name":"Fake model","reasoning":true,"input":["text"]}]}}}
            """, InheritDefaults)),
        ("models-json.custom-provider-requires-base-url-and-reports-every-error", () => WithModels("""
            {"providers":{"my-custom-provider":{"apiKey":"test-key","models":[{"id":"my-model","api":"openai-completions","reasoning":false,"input":["text"]}]},
              "broken-one":{"api":"openai-completions","models":[{"id":"one"}]},"broken-two":{"api":"openai-completions","models":[{"id":"two"}]},
              "empty":{},"bad-limits":{"baseUrl":"https://x.invalid","api":"openai-completions","models":[{"id":"z","contextWindow":0}]},
              "no-api":{"baseUrl":"https://x.invalid","models":[{"id":"q"}]}}}
            """, CompositionErrors)),
        ("models-json.same-id-replaces-and-provider-compat-merges", () => WithModels("""
            {"providers":{"openrouter":{"baseUrl":"https://my-proxy.example.com/v1","apiKey":"test-key","api":"openai-completions",
               "compat":{"supportsUsageInStreaming":false,"supportsStrictMode":false,"openRouterRouting":{"only":["a"]}},
               "models":[{"id":"anthropic/claude-sonnet-4","name":"anthropic/claude-sonnet-4","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":100000,"maxTokens":8000}]},
              "demo":{"baseUrl":"https://example.com/v1","apiKey":"DEMO_KEY","api":"openai-completions","compat":{"supportsUsageInStreaming":false,"maxTokensField":"max_tokens"},
               "models":[{"id":"demo-model","reasoning":true,"input":["text"],"contextWindow":1000,"maxTokens":100,"thinkingLevelMap":{"minimal":null,"high":"max"},
                  "compat":{"supportsUsageInStreaming":true,"cacheControlFormat":"anthropic"}},{"id":"defaults"}]}}}
            """, ReplaceAndCompat)),
        ("models-json.model-overrides", () => WithModels("""
            {"providers":{"openai":{"modelOverrides":{
               "gpt-4o":{"name":"Renamed","contextWindow":64000,"cost":{"input":9},"compat":{"supportsStrictMode":false},
                 "inputLimits":{"images":{"resize":{"maxWidth":512}}},"samplingParams":{"temperature":0.2},"headers":{"X-Model":"m"}},
               "not-a-model":{"name":"ignored"}}}}}
            """, ModelOverrides)),
        ("models-json.schema-parse-and-load-errors", Sync(SchemaErrors)),
        ("models-json.comments-trailing-commas-bom-and-missing-file", Sync(LenientParsing)),
        ("models-json.auth-status-and-availability", () => WithModels("""
            {"providers":{"env-key":{"baseUrl":"https://a.invalid","apiKey":"$TEAM_KEY","api":"openai-completions","models":[{"id":"a"}]},
              "template":{"baseUrl":"https://b.invalid","apiKey":"pre-${PART_ONE}-${PART_TWO}","api":"openai-completions","models":[{"id":"b"}]},
              "literal":{"baseUrl":"https://c.invalid","apiKey":"sk-literal","api":"openai-completions","models":[{"id":"c"}]},
              "command":{"baseUrl":"https://d.invalid","apiKey":"!print-key","api":"openai-completions","models":[{"id":"d"}]},
              "keyless":{"baseUrl":"https://e.invalid","api":"openai-completions","models":[{"id":"e"}]}}}
            """, AuthStatus)),
        ("models-json.request-auth-headers-and-auth-header", () => WithModels("""
            {"providers":{"team":{"baseUrl":"https://team.invalid/v1","apiKey":"${TEAM_KEY}","api":"openai-completions","authHeader":true,
               "headers":{"X-Team":"$TEAM_NAME","X-Static":"static"},
               "models":[{"id":"m","headers":{"X-Model":"${MODEL_TAG}"}}],"modelOverrides":{"m":{"headers":{"X-Override":"o"}}}},
              "needs-key":{"baseUrl":"https://n.invalid","api":"openai-completions","authHeader":true,"models":[{"id":"n"}]},
              "groq":{"headers":{"X-Missing":"$NOT_SET"}}}}
            """, RequestAuth)),
    ];

    private static Task WithModels(string json, Action<string> run) => WithTemp("models-json", root =>
    {
        var path = Path.Combine(root, "models.json");
        File.WriteAllText(path, json);
        run(path); return Task.CompletedTask;
    });

    private static IReadOnlyList<RegistryModel> Of(ModelRegistry registry, string provider) => [.. registry.GetAll().Where(model => model.Provider == provider)];

    private static void BaseUrlOverride(string path)
    {
        Registry(out var registry, modelsPath: path);
        Equal<string?>(null, registry.GetError(), "no error");
        var anthropic = Of(registry, "anthropic");
        Check(anthropic.Count > 1 && anthropic.Any(model => model.Id.Contains("claude", StringComparison.Ordinal)), "built-ins kept");
        Check(anthropic.All(model => model.BaseUrl == "https://my-proxy.example.com/v1" && model.Pinned is null), "base URL rewritten");
        Check(Of(registry, "google").All(model => model.BaseUrl == "https://generativelanguage.googleapis.com/v1beta" && model.Pinned is not null), "other providers untouched");
        File.WriteAllText(path, """{"providers":{"anthropic":{"baseUrl":"https://second-proxy.example.com/v1"}}}""");
        registry.RefreshAsync(false, null, null, CancellationToken.None).GetAwaiter().GetResult();
        Equal("https://second-proxy.example.com/v1", Of(registry, "anthropic")[0].BaseUrl, "refresh picks up changes");
    }

    private static void HeadersOnly(string path)
    {
        Registry(out var registry, Env(("DEEPSEEK_API_KEY", "ds-key")), path);
        Equal<string?>(null, registry.GetError(), "headers-only is valid");
        var auth = registry.ResolveRequestAuth(Of(registry, "deepseek")[0], out var error);
        Check(error is null && auth!.ApiKey == "ds-key" && auth.Headers!["X-Custom-Header"] == "custom-value", "header resolved at request time");
        Check(Of(registry, "deepseek").All(model => model.Pinned is not null), "headers do not change the models");
    }

    private static void MixedOverride(string path)
    {
        Registry(out var registry, modelsPath: path);
        Check(Of(registry, "anthropic").All(model => model.BaseUrl == "https://anthropic-proxy.example.com/v1"), "anthropic base URL");
        var google = Of(registry, "google");
        Check(google.Count > 1 && google.Any(model => model.Id == "gemini-custom"), "google built-ins plus custom");
        Check(google.All(model => model.BaseUrl == "https://google-proxy.example.com/v1"), "provider base URL applies to built-in and custom models");
    }

    private static void InheritDefaults(string path)
    {
        Registry(out var registry, modelsPath: path);
        Equal<string?>(null, registry.GetError(), "no error");
        var model = registry.Find("openrouter", "fake-provider/fake-model")!;
        Equal("openai-completions", model.Api, "inherited api"); Equal("https://openrouter.ai/api/v1", model.BaseUrl, "inherited base URL");
        Equal(128000d, model.ContextWindow, "default context"); Equal(16384d, model.MaxTokens, "default max tokens");
        Check(model.Reasoning && model.Name == "Fake model", "definition fields");
        Check(Of(registry, "openrouter").Count > 300, "built-ins kept");
        Equal("openai-completions", model.ToDefinition().DeclaredApi, "custom model reads as a catalog row");
    }

    private static void CompositionErrors(string path)
    {
        Registry(out var registry, modelsPath: path);
        var error = registry.GetError()!;
        Check(error.Contains("Provider \"my-custom-provider\": Provider my-custom-provider: \"baseUrl\" is required when defining custom models.", StringComparison.Ordinal), error);
        Check(error.Contains("Provider \"broken-one\"", StringComparison.Ordinal) && error.Contains("Provider \"broken-two\"", StringComparison.Ordinal), "every error");
        Check(error.Contains("Provider \"empty\": Provider empty: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".", StringComparison.Ordinal), "empty");
        Check(error.Contains("Provider bad-limits, model z: invalid contextWindow", StringComparison.Ordinal), "limits");
        Check(error.Contains("Provider no-api, model q: no \"api\" specified. Set at provider or model level.", StringComparison.Ordinal), "api");
        Check(!error.Contains("\n\n\n", StringComparison.Ordinal) && error.Split("\n\n").Length == 6, "joined by blank lines");
        Check(Of(registry, "broken-one").Count == 0 && Of(registry, "anthropic").Count > 0, "failed providers keep their base");
    }

    private static void ReplaceAndCompat(string path)
    {
        Registry(out var registry, modelsPath: path);
        Equal<string?>(null, registry.GetError(), "no error");
        var sonnet = Of(registry, "openrouter").Where(model => model.Id == "anthropic/claude-sonnet-4").ToList();
        Equal(1, sonnet.Count, "replaced, not duplicated"); Equal("https://my-proxy.example.com/v1", sonnet[0].BaseUrl, "replacement base URL");
        foreach (var model in Of(registry, "openrouter"))
            Check(JsonTree.Boolean(model.Compat!, "supportsUsageInStreaming") == false && JsonTree.Boolean(model.Compat!, "supportsStrictMode") == false,
                model.Id + " provider compat");
        var routed = Of(registry, "openrouter").First(model => model.Id != "anthropic/claude-sonnet-4" && model.Api == "openai-completions" &&
            JsonTree.Object(model.Compat!, "openRouterRouting") is not null);
        Check(JsonTree.Object(routed.Compat!, "openRouterRouting")!["only"] is JsonArray, "routing deep-merged");
        var demo = registry.Find("demo", "demo-model")!;
        Equal(true, JsonTree.Boolean(demo.Compat!, "supportsUsageInStreaming"), "model compat wins");
        Equal("max_tokens", JsonTree.String(demo.Compat!, "maxTokensField"), "provider compat kept");
        Equal("anthropic", JsonTree.String(demo.Compat!, "cacheControlFormat"), "cacheControlFormat accepted");
        Equal("""{"minimal":null,"high":"max"}""", demo.CloneJson()["thinkingLevelMap"]!.ToJsonString(), "thinkingLevelMap kept");
        var defaults = registry.Find("demo", "defaults")!;
        Equal("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}""", defaults.CloneJson()["cost"]!.ToJsonString(), "zero cost");
        Names(["text"], defaults.Input, "text input"); Equal("defaults", defaults.Name, "name defaults to id");
    }

    private static void ModelOverrides(string path)
    {
        Registry(out var registry, Env(("OPENAI_API_KEY", "k")), path);
        Equal<string?>(null, registry.GetError(), "no error");
        var model = registry.Find("openai", "gpt-4o")!;
        Equal("Renamed", model.Name, "name"); Equal(64000d, model.ContextWindow, "context");
        var cost = model.CloneJson()["cost"]!.AsObject();
        Equal(9d, cost["input"]!.GetValue<double>(), "cost input overridden");
        Check(cost["output"]!.GetValue<double>() > 0, "cost output kept");
        Equal(false, JsonTree.Boolean(model.Compat!, "supportsStrictMode"), "compat");
        Equal("""{"temperature":0.2}""", model.CloneJson()["samplingParams"]!.ToJsonString(), "sampling");
        Equal(512d, model.CloneJson()["inputLimits"]!["images"]!["resize"]!["maxWidth"]!.GetValue<double>(), "image resize deep-merged");
        Check(model.Pinned is null && registry.Find("openai", "gpt-4.1")!.Pinned is not null, "only the overridden model changes");
        Check(registry.Find("openai", "not-a-model") is null, "override for a missing id is ignored");
        var auth = registry.ResolveRequestAuth(model, out _);
        Equal("m", auth!.Headers!["X-Model"], "override headers at request time");
    }

    private static void SchemaErrors()
    {
        var config = ModelsJsonConfig.Parse("""{"providers":{"x":{"models":[{"name":""}],"authHeader":"yes","oauth":"github","compat":{"maxTokensField":7}}}}""", "/p/models.json");
        // compat {maxTokensField: 7} still matches the Responses compat object (TypeBox objects admit additional properties).
        Equal("Invalid models.json schema:\n" +
            "  - providers.x.oauth: must be equal to constant\n" +
            "  - providers.x.authHeader: must be boolean\n" +
            "  - providers.x.models.0.id: must have required properties id\n" +
            "  - providers.x.models.0.name: must not have fewer than 1 characters\n\nFile: /p/models.json", config.Error, "schema errors");
        Equal("Invalid models.json schema:\n  - providers.x.compat: must match a schema in anyOf\n\nFile: /p",
            ModelsJsonConfig.Parse("""{"providers":{"x":{"compat":5}}}""", "/p").Error, "union");
        Equal("Invalid models.json schema:\n  - providers: must have required properties providers\n\nFile: /p", ModelsJsonConfig.Parse("{}", "/p").Error, "root required");
        Equal("Invalid models.json schema:\n  - root: must be object\n\nFile: /p", ModelsJsonConfig.Parse("[]", "/p").Error, "root type");
        // model-config.ts interpolates JSON.parse's SyntaxError (Node 22 / V8 12.4 wording).
        Equal("Failed to parse models.json: Expected property name or '}' in JSON at position 1 (line 1 column 2)\n\nFile: /p", ModelsJsonConfig.Parse("{", "/p").Error, "parse error");
        Check(ModelsJsonConfig.Parse("{\"providers\":{}}", "/p").Error is null, "empty providers");
        var extra = ModelsJsonConfig.Parse("""{"providers":{"x":{"baseUrl":"https://x","unknownKey":1,"models":[{"id":"m","whatever":true}]}},"other":1}""", "/p");
        Check(extra.Error is null && extra.ProviderIds.Single() == "x", "additional properties are allowed");
        var resize = ModelsJsonConfig.Parse("""{"providers":{"x":{"modelOverrides":{"m":{"inputLimits":{"images":{"resize":{"jpegQuality":101,"maxWidth":1.5}}},"promptCache":{"short":0}}}}}}""", "/p");
        Equal("Invalid models.json schema:\n" +
            "  - providers.x.modelOverrides.m.inputLimits.images.resize.maxWidth: must be integer\n" +
            "  - providers.x.modelOverrides.m.inputLimits.images.resize.jpegQuality: must be <= 100\n" +
            "  - providers.x.modelOverrides.m.promptCache.short: must be > 0\n\nFile: /p", resize.Error, "numeric constraints");
    }

    private static void LenientParsing()
    {
        var config = ModelsJsonConfig.Parse("﻿{\n// comment with \"quotes\"\n\"providers\": {\"x\": {\"baseUrl\": \"https://x//not-a-comment\",},},\n}", "/p");
        Equal<string?>(null, config.Error, "comments, trailing commas and BOM");
        Equal("https://x//not-a-comment", JsonTree.String(config.GetProvider("x")!, "baseUrl"), "strings keep //");
        var duplicate = ModelsJsonConfig.Parse("""{"providers":{"x":{"baseUrl":"first","baseUrl":"second"}}}""", "/p");
        Equal("second", JsonTree.String(duplicate.GetProvider("x")!, "baseUrl"), "duplicate keys: last wins (JSON.parse)");
        var missing = ModelsJsonConfig.Load(Path.Combine(Path.GetTempPath(), "pisharp-missing-" + Guid.NewGuid().ToString("N"), "models.json"));
        Check(missing.Error is null && missing.ProviderIds.IsEmpty, "missing file is empty");
        Check(ModelsJsonConfig.Load(null).ProviderIds.IsEmpty, "no path");
    }

    private static void AuthStatus(string path)
    {
        Registry(out var registry, Env(("TEAM_KEY", "tk"), ("PART_ONE", "1")), path);
        Equal<string?>(null, registry.GetError(), "no error");
        Equal(new ProviderAuthStatus(true, "environment", "TEAM_KEY"), registry.GetProviderAuthStatus("env-key"), "env status");
        Equal(new ProviderAuthStatus(false), registry.GetProviderAuthStatus("template"), "template missing PART_TWO");
        Equal(new ProviderAuthStatus(true, "models_json_key"), registry.GetProviderAuthStatus("literal"), "literal");
        Equal(new ProviderAuthStatus(true, "models_json_command"), registry.GetProviderAuthStatus("command"), "command not executed");
        Equal(new ProviderAuthStatus(false), registry.GetProviderAuthStatus("keyless"), "keyless");
        Names(["env-key/a", "literal/c", "command/d"], registry.GetAvailable().Where(model => model.BaseUrl.EndsWith(".invalid", StringComparison.Ordinal))
            .Select(model => model.Reference), "available custom models");
        Equal("configured API key", registry.CheckAuth("command"), "command check");
        Registry(out var stored, Env(), path, new Dictionary<string, ProviderStoredCredential> { ["keyless"] = new("api_key", "stored-key") });
        Equal(new ProviderAuthStatus(true, "stored"), stored.GetProviderAuthStatus("keyless"), "stored status");
        Check(stored.HasConfiguredAuth("keyless"), "stored credential authenticates a custom provider");
        Equal("stored-key", stored.ResolveRequestAuth(stored.Find("keyless", "e")!, out _)!.ApiKey, "stored key wins");
        // provider-composer.ts: a models.json `!command` apiKey runs (uncached, per request) and its trimmed stdout is the key; a stored
        // auth.json `!command` key runs too (auth-storage read). A command that resolves nothing reports the upstream text.
        var runs = new List<string>();
        var commands = ModelRegistry.Create(new()
        {
            Environment = Env(), ModelsPath = path, RunCommand = command => { runs.Add(command); return command == "print-key" ? "cmd-key" : command == "stored-cmd" ? "stored-cmd-key" : null; },
            StoredCredentials = new Dictionary<string, ProviderStoredCredential> { ["keyless"] = new("api_key", "!stored-cmd"), ["literal"] = new("api_key", "!fails") }
        });
        Equal("cmd-key", commands.ResolveRequestAuth(commands.Find("command", "d")!, out _)!.ApiKey, "models.json command key");
        commands.ResolveRequestAuth(commands.Find("command", "d")!, out _);
        Equal(2, runs.Count(command => command == "print-key"), "models.json commands run on every request");
        Equal("stored-cmd-key", commands.ResolveRequestAuth(commands.Find("keyless", "e")!, out _)!.ApiKey, "stored command key");
        Check(!commands.HasConfiguredAuth("literal"), "a stored command that resolves nothing does not authenticate, and skips the models.json key");
        var failed = ModelRegistry.Create(new() { Environment = Env(), ModelsPath = path, RunCommand = _ => null });
        Check(failed.ResolveRequestAuth(failed.Find("command", "d")!, out var error) is null &&
            error == "Failed to resolve API key for provider \"command\" from shell command: print-key", "failed command: " + error);
    }

    private static void RequestAuth(string path)
    {
        Registry(out var registry, Env(("TEAM_KEY", "tk"), ("TEAM_NAME", "red"), ("MODEL_TAG", "v2"), ("GROQ_API_KEY", "gk")), path);
        var auth = registry.ResolveRequestAuth(registry.Find("team", "m")!, out var error);
        Check(error is null, error ?? "");
        Equal("tk", auth!.ApiKey, "env key");
        Names(["Authorization=Bearer tk", "X-Model=v2", "X-Override=o", "X-Static=static", "X-Team=red"],
            auth.Headers!.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value), "headers");
        Equal<ModelRequestAuth?>(null, registry.ResolveRequestAuth(registry.Find("needs-key", "n")!, out var missing), "authHeader without key");
        Equal("No API key found for \"needs-key\"", missing, "missing key message");
        Equal<ModelRequestAuth?>(null, registry.ResolveRequestAuth(registry.Find("groq", "openai/gpt-oss-120b")!, out var header), "unresolvable header");
        Equal("Failed to resolve provider \"groq\" header \"X-Missing\" from environment variable: NOT_SET", header, "header error");
    }
}
