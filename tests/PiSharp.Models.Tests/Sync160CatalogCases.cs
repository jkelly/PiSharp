// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/test/{qwen-token-plan-models,supports-xhigh,max-thinking,providers,
// together-models,fireworks-models}.test.ts (their catalog parts), packages/coding-agent/test/model-resolver.test.ts (default model
// selection), packages/ai/catalog/{families,providers}/*.ts, packages/ai/src/env-api-keys.ts,
// packages/ai/src/providers/qwen-token-plan*.ts and packages/coding-agent/schemas/models.schema.json.
// Authored offline expectations (no upstream capture): catalog values are read from the embedded @earendil-works/pi-ai@1.6.0 release
// and were compared with those sources by reading. Fake HTTP endpoints answer in process; no network or live credentials are used.
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Providers;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

internal static partial class Program
{
    private const string Pi160 = "faac585884ae2c013a6b1400f3e9e77f10b34bac";
    private const string QwenInternationalGateway = "https://token-plan.maas.qwencloudapi.com/compatible-mode/v1";
    private const string QwenChinaGateway = "https://token-plan.maas.qianwenaiapi.com/compatible-mode/v1";

    /// <summary>Provider, display name, gateway, per-plan variable, shared region variable (providers/qwen-token-plan*.ts).</summary>
    private static readonly (string Provider, string Name, string Gateway, string PlanVariable, string SharedVariable)[] QwenPlans =
    [
        ("qwen-token-plan", "Qwen Token Plan Team", QwenInternationalGateway, "QWEN_TOKEN_PLAN_TEAM_API_KEY", "QWEN_TOKEN_PLAN_API_KEY"),
        ("qwen-token-plan-cn", "Qwen Token Plan Team CN", QwenChinaGateway, "QWEN_TOKEN_PLAN_TEAM_CN_API_KEY", "QWEN_TOKEN_PLAN_CN_API_KEY"),
        ("qwen-token-plan-individual", "Qwen Token Plan Individual", QwenInternationalGateway, "QWEN_TOKEN_PLAN_INDIVIDUAL_API_KEY", "QWEN_TOKEN_PLAN_API_KEY"),
        ("qwen-token-plan-individual-cn", "Qwen Token Plan Individual CN", QwenChinaGateway, "QWEN_TOKEN_PLAN_INDIVIDUAL_CN_API_KEY", "QWEN_TOKEN_PLAN_CN_API_KEY"),
    ];

    // qwen-token-plan-models.test.ts TEXT_MODELS, INDIVIDUAL_TEXT_MODELS, IMAGE_MODELS and QWEN_THINKING_MODELS at v1.6.0.
    private static readonly string[] QwenTeamTextModels = ["MiniMax-M2.5", "deepseek-v4-pro", "glm-5", "glm-5.1", "glm-5.2", "kimi-k2.5", "kimi-k2.6",
        "kimi-k2.7-code", "qwen3.6-flash", "qwen3.6-plus", "qwen3.7-max", "qwen3.7-plus", "qwen3.8-flash", "qwen3.8-max"];
    private static readonly string[] QwenIndividualTextModels = ["deepseek-v4-flash-0731", "deepseek-v4-pro", "deepseek-v4-pro-0813", "deepseek-v4.1-flash",
        "deepseek-v4.1-flash-1017", "glm-5.2", "glm-5.3", "qwen3.6-flash", "qwen3.7-max", "qwen3.7-plus", "qwen3.8-flash", "qwen3.8-max", "qwen4-max-preview"];
    private static readonly string[] QwenImageModels = ["qwen-image-2.0", "qwen-image-2.0-pro", "wan2.7-image", "wan2.7-image-pro"];
    private static readonly string[] QwenTeamThinkingModels = ["deepseek-v4-pro", "glm-5", "glm-5.1", "glm-5.2", "kimi-k2.5", "kimi-k2.6", "kimi-k2.7-code",
        "qwen3.6-flash", "qwen3.6-plus", "qwen3.7-max", "qwen3.7-plus", "qwen3.8-flash", "qwen3.8-max"];
    private static readonly string[] QwenRetiredModels = ["qwen3.8-max-preview", "deepseek-v3.2", "deepseek-v4-flash"];

    private static readonly string[] Gpt6MidConversationEffort = ["gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6.2-astra", "gpt-6.2-sol"];

    private static IEnumerable<FrozenCatalogModel> Rows(ReleasedModelCatalog release) => release.Providers.SelectMany(provider => release.Get(provider)!.Models);

    /// <summary>A row of the current release as a JSON object.</summary>
    private static JsonObject Row160(string provider, string id, CatalogModelType type = CatalogModelType.Chat)
    {
        Check(CurrentModelCatalog.Get(provider)!.TryGetModel(type, id, out var model), $"1.6.0 {provider} lists {type} {id}");
        return JsonNode.Parse(model!.Raw.ToString())!.AsObject();
    }

    private static bool Same(string expectedJson, JsonNode? actual) => JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actual);

    private static IEnumerable<(string, Func<Task>)> Sync160CatalogCases()
    {
        yield return ("sync160.catalog.exact-current-package-resources", Sync(() =>
        {
            Equal("1.6.0", CurrentModelCatalog.PackageVersion, "current package version");
            Equal(Pi160, CurrentModelCatalog.SourceCommit, "current source commit");
            Equal(Pi160, ReviewedModelCatalog.UpstreamCommit, "effective source provenance");
            Equal("d68312ec574bc959cb7fee26da4f4876d067aed9bbbe76e876359e60d2294747", CurrentModelCatalog.PackageSha256, "package SHA256");
            Equal("sha512-9hL+eygFDEiFp//cydeUfmFsKmEqpmvsNPb10Y1W8+m5K9O+sx1hMStfu6nukrBD5sl6mP4RykCYBBNEgsVLfw==", CurrentModelCatalog.PackageIntegrity, "npm integrity");
            const string manifestHash = "cc46553f3f008788e667600a873d3d83a4df8fe46c67b06c65b16fd828450e8b";
            Equal(manifestHash, CurrentModelCatalog.ManifestSha256, "pinned manifest fingerprint");
            using var resource = typeof(CurrentModelCatalog).Assembly.GetManifestResourceStream("PiSharp.AI.Catalogs.Releases.1.6.0..manifest.json");
            Check(resource is not null, "exact release manifest resource");
            using var buffer = new MemoryStream(); resource!.CopyTo(buffer);
            Equal(manifestHash, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())), "manifest byte fingerprint");
            using var manifest = JsonDocument.Parse(buffer.ToArray());
            Equal(6, manifest.RootElement.GetProperty("schemaVersion").GetInt32(), "manifest schema version");
            Equal("2026-11-20T10:48:49.496Z", manifest.RootElement.GetProperty("generatedAt").GetString(), "manifest generatedAt");
            Equal(1_795_171_729_496L, CurrentModelCatalog.GeneratedAtUnixMilliseconds, "current snapshot age gate");
            var files = manifest.RootElement.GetProperty("files").EnumerateObject().ToArray();
            Equal(43, files.Length, "all released provider files");
            Names(files.Select(file => file.Name[..^5]).Order(StringComparer.Ordinal), CurrentModelCatalog.Providers, "provider enumeration derives from the current snapshot");
            foreach (var file in files)
                Equal(file.Value.GetString(), CurrentModelCatalog.Get(file.Name[..^5])!.ProviderJsonSha256, file.Name + " byte fingerprint");
            Check(ReferenceEquals(ReleasedModelCatalog.Current.Get("openai"), CurrentModelCatalog.Get("openai")), "the current facade serves the 1.6.0 release");
            var registry = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".absent") });
            Equal(1698, registry.GetAllOfType(CatalogModelType.Chat).Count, "current chat rows");
            Equal(74, registry.GetAllOfType(CatalogModelType.Image).Count, "current image rows");
            Equal(81, registry.GetAllOfType(CatalogModelType.Classifier).Count, "current classifier rows");
            Equal(1853, Rows(ReleasedModelCatalog.Current).Count(), "current entries of every type");
        }));
        yield return ("sync160.catalog.snapshot-equals-the-package-tarball", async () =>
        {
            if (ReleaseTarball is null) return; // Verified when --release-tarball is given (the report records releaseTarballVerified).
            var bytes = await File.ReadAllBytesAsync(ReleaseTarball);
            Equal(CurrentModelCatalog.PackageSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), "tarball SHA256");
            Equal(CurrentModelCatalog.PackageIntegrity, "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes)), "tarball npm integrity");
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.GetNextEntryAsync() is { } entry)
            {
                const string prefix = "package/dist/providers/data/";
                if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal) || !entry.Name.EndsWith(".json", StringComparison.Ordinal) || entry.DataStream is null) continue;
                using var member = new MemoryStream(); await entry.DataStream.CopyToAsync(member);
                var name = entry.Name[prefix.Length..];
                using var embedded = typeof(CurrentModelCatalog).Assembly.GetManifestResourceStream("PiSharp.AI.Catalogs.Releases.1.6.0." + name);
                Check(embedded is not null, name + " is embedded");
                using var copy = new MemoryStream(); embedded!.CopyTo(copy);
                Check(copy.ToArray().AsSpan().SequenceEqual(member.ToArray()), name + " differs from the tarball");
                seen.Add(name);
            }
            Equal(44, seen.Count, "tarball shards and manifest compared");
            Check(seen.Contains(".manifest.json"), "manifest compared");
        });
        yield return ("sync160.catalog.pi-1.5.2-release-preserved-beside-current", Sync(() =>
        {
            var preserved = ReleasedModelCatalog.Pi152;
            Equal("1.5.2", preserved.PackageVersion, "preserved release version");
            Equal("b4afdddf44e059c67ccf5097e5317d913521b470", preserved.SourceCommit, "preserved release source");
            Equal("71375843feaa116c7d928e74e0500a22cc267e12bfb6869efb92dfd39acf7fee", preserved.ManifestSha256, "preserved manifest pin");
            Equal(1773, Rows(preserved).Count(), "preserved 1.5.2 entries");
            Check(!ReferenceEquals(preserved, ReleasedModelCatalog.Current), "two separate releases");
            // The preserved rows keep what 1.6.0 changed or removed; the registry serves only the current release.
            Check(preserved.Get("qwen-token-plan")!.TryGetModel(CatalogModelType.Chat, "deepseek-v3.2", out var retired), "1.5.2 keeps the retired Team model");
            Equal("https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", retired!.BaseUrl, "1.5.2 keeps its gateway");
            Check(!preserved.Get("openai")!.TryGetModel(CatalogModelType.Chat, "gpt-6.2-sol", out _), "1.5.2 has no GPT-6.2");
            var registry = ModelRegistry.Create(new() { MaintainedCatalogPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".absent") });
            Check(registry.Find("qwen-token-plan", "deepseek-v3.2") is null, "the registry does not read the preserved release");
            Check(registry.Find("openai", "gpt-6.2-sol") is not null, "the registry reads the current release");
            Check(ReviewedModelCatalog.Apply("not-a-provider", null) is null, "no preserved layer invents a provider");
        }));
        yield return ("sync160.catalog.release-delta-from-1.5.2", Sync(() =>
        {
            var before = Rows(ReleasedModelCatalog.Pi152).ToDictionary(model => (model.Provider, model.Type, model.Id));
            var current = Rows(ReleasedModelCatalog.Current).ToDictionary(model => (model.Provider, model.Type, model.Id));
            Names(ReleasedModelCatalog.Pi152.Providers, CurrentModelCatalog.Providers, "no provider added or removed");
            var added = current.Keys.Except(before.Keys).ToArray(); var removed = before.Keys.Except(current.Keys).ToArray();
            Equal(129, added.Length, "all released additions");
            Equal(49, removed.Length, "all released removals");
            Equal("Chat=103,Image=8,Classifier=18", string.Join(',', Enum.GetValues<CatalogModelType>().Select(type => $"{type}={added.Count(key => key.Type == type)}")), "additions by type");
            Equal("Chat=48,Image=0,Classifier=1", string.Join(',', Enum.GetValues<CatalogModelType>().Select(type => $"{type}={removed.Count(key => key.Type == type)}")), "removals by type");
            Equal(651, before.Keys.Intersect(current.Keys).Count(key =>
                !JsonNode.DeepEquals(JsonNode.Parse(before[key].Raw.ToString()), JsonNode.Parse(current[key].Raw.ToString()))), "all released changed rows");
            // catalog/**: every explicit supportsDeveloperRole left the catalog (601 rows carried false in 1.5.2, 570 of them are still
            // listed). The role now comes from the openai-completions default alone (developer only for api.openai.com), which is
            // provider wire behavior.
            static bool DeclaresDeveloperRole(FrozenCatalogModel model) =>
                model.Raw.Value.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("supportsDeveloperRole", out _);
            Equal(601, before.Values.Count(DeclaresDeveloperRole), "1.5.2 rows with an explicit developer-role flag");
            Equal(570, before.Where(pair => current.ContainsKey(pair.Key)).Count(pair => DeclaresDeveloperRole(pair.Value)), "of which 1.6.0 still lists");
            Check(before.Values.Where(DeclaresDeveloperRole).All(model => !model.Raw.Value.GetProperty("compat").GetProperty("supportsDeveloperRole").GetBoolean()), "1.5.2 only ever declared false");
            Equal(0, current.Values.Count(DeclaresDeveloperRole), "1.6.0 rows with an explicit developer-role flag");
            var registry = ModelRegistry.Create(new());
            foreach (var (provider, id) in new[] { ("nvidia", "moonshotai/kimi-k2.6"), ("opencode", "glm-5.2"), ("opencode-go", "glm-5.2"),
                ("openrouter", "x-ai/grok-build-0.1"), ("vercel-ai-gateway", "inception/mercury-2.5"), ("qwen-token-plan", "deepseek-v4-flash") })
            {
                Check(before.ContainsKey((provider, CatalogModelType.Chat, id)), "1.5.2 listed " + provider + "/" + id);
                Check(registry.Find(provider, id) is null, "removed entry absent from the current registry: " + provider + "/" + id);
            }
            foreach (var (provider, id) in new[] { ("openai", "gpt-6-cyber"), ("azure", "gpt-6-cyber"), ("amazon-bedrock", "us.openai.gpt-6.2-sol"),
                ("google", "gemini-4-carbon"), ("google-vertex", "gemini-robotics-er-2-preview"), ("github-copilot", "mai-code-2"), ("radius", "gpt-6.2-astra"),
                ("opencode-go", "qwen4-max"), ("cloudflare-ai-gateway", "claude-fable-5-2"), ("huggingface", "moonshotai/Kimi-K3.1-Pro") })
                Check(registry.Find(provider, id) is not null, "new chat entry available: " + provider + "/" + id);
            Check(registry.GetAllOfType(CatalogModelType.Image, "openrouter").Any(model => model.Id == "microsoft/mai-image-2.7"), "new image available");
            Check(registry.GetAllOfType(CatalogModelType.Classifier, "vercel-ai-gateway").Any(model => model.Id == "modaic/mo"), "new classifier available");
            Check(!registry.GetAllOfType(CatalogModelType.Classifier, "openrouter").Any(model => model.Id == "parallel/decisions-fast:free"), "removed classifier absent");
            // All 13 Fable 5.2 variants of the release (1.5.2 had 11): Cloudflare AI Gateway and GitHub Copilot joined.
            var fable = current.Values.Where(model => model.Id.Contains("fable-5-2", StringComparison.Ordinal) || model.Id.Contains("fable-5.2", StringComparison.Ordinal)).ToArray();
            Equal(13, fable.Length, "Fable 5.2 variants");
            Names(new[] { "amazon-bedrock", "anthropic", "cloudflare-ai-gateway", "github-copilot", "opencode", "openrouter", "radius", "vercel-ai-gateway" },
                fable.Select(model => model.Provider).Distinct().Order(StringComparer.Ordinal), "Fable 5.2 provider coverage");
            foreach (var row in fable)
                Equal(JsonValueKind.Null, row.Raw.Value.GetProperty("thinkingLevelMap").GetProperty("off").ValueKind, row.Provider + "/" + row.Id + " disallows off");
        }));
        yield return ("sync160.catalog.dropped-developer-role-flag-still-sends-system", async () =>
        {
            // Integration guard for the catalog change above. 1.5.2 told these providers' reasoning models to take the system prompt with
            // the system role through an explicit compat flag; 1.6.0 removed the flag and relies on the openai-completions default of the
            // same release (developer only for api.openai.com, #10741). That default is provider wire behavior (packages/ai/src/api/
            // openai-completions.ts detectCompat), so this case passes only once it is ported.
            var wrong = new List<string>(); var driven = new List<string>();
            foreach (var provider in BuiltinProviders.All.Where(provider => provider.Auth == ProviderAuthKind.EnvironmentKey && provider.Id != "azure"))
            {
                var before = ReleasedModelCatalog.Pi152.Get(provider.Id);
                var row = CurrentModelCatalog.Get(provider.Id)!.Models.FirstOrDefault(model => model.Type == CatalogModelType.Chat && model.DeclaredApi == "openai-completions" &&
                    model.Raw.Value.GetProperty("reasoning").GetBoolean() && before is not null && before.TryGetModel(CatalogModelType.Chat, model.Id, out var old) &&
                    old.Raw.Value.TryGetProperty("compat", out var compat) && compat.TryGetProperty("supportsDeveloperRole", out var flag) && flag.ValueKind == JsonValueKind.False);
                if (row is null) continue;
                var endpoint = new LiveEndpoint(seen => Respond(seen, row.Id));
                var runtime = new LiveSessionRuntime(Env((ProviderEnvironmentKeys.GetApiKeyVariables(provider.Id)![0], "fixture-key")), () => endpoint);
                var selection = await new SettingsModelSelection(null, provider.Id + "/" + row.Id, null).ResolveAsync(null, runtime, null, false, CancellationToken.None);
                Equal(provider.Id + "/" + row.Id, selection.Model.Provider + "/" + selection.Model.Id, "selected model");
                using var connection = selection.Connect(runtime);
                var request = new ChatRequest(selection.Model, [new("system", JsonData.Parse("""{"role":"system","content":"Be brief.","timestamp":0}""")),
                    new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":1}"""))], 1);
                try { await foreach (var _ in connection.CreateTransport().StreamAsync(request)) { } }
                catch (Exception error) when (error is not OperationCanceledException) { }
                var seen = endpoint.Snapshot();
                Check(seen.Length == 1, provider.Id + " sent one request");
                var role = JsonNode.Parse(seen[0].Body!)!["messages"]![0]!["role"]!.GetValue<string>();
                driven.Add(provider.Id);
                if (role != "system") wrong.Add($"{provider.Id}/{row.Id} sent {role}");
            }
            Names(["ant-ling", "baseten", "cerebras", "deepseek", "fireworks", "github-copilot", "huggingface", "kimi-coding", "moonshotai", "moonshotai-cn", "nvidia", "opencode", "opencode-go",
                "openrouter", "qwen-token-plan", "qwen-token-plan-cn", "qwen-token-plan-individual", "qwen-token-plan-individual-cn", "together", "zai", "zai-coding-cn"],
                driven.Order(StringComparer.Ordinal), "providers whose rows dropped the flag and have a key-authenticated live route");
            Check(wrong.Count == 0, "the system prompt left as developer where 1.5.2 sent system (needs the 1.6.0 openai-completions developer-role default): " + string.Join("; ", wrong));
        });
        yield return ("sync160.catalog.every-default-model-streams-as-the-cli-asks", async () =>
        {
            // Every provider default of the 1.6.0 catalog has to work the way the CLI uses it: the request asks for the model's own
            // maxTokens (simple-options.ts buildBaseOptions) at the default thinking level, clamped to the levels the route offers.
            // live.one-model-per-provider-against-fake-http drives the same models with a bounded cap and no thinking level.
            var failed = new List<string>(); var driven = 0;
            foreach (var provider in BuiltinProviders.All)
            {
                if (ProviderApiRoutes.Contains(provider.Id) || provider.Id == "anthropic" || LiveModelFor(provider.Id) is not { } id) continue;
                var endpoint = new LiveEndpoint(seen => Respond(seen, id));
                var runtime = new LiveSessionRuntime(Env((ProviderEnvironmentKeys.GetApiKeyVariables(provider.Id)![0], "fixture-key"),
                    ("AZURE_OPENAI_BASE_URL", "https://pisharp-res.openai.azure.com/openai/v1")), () => endpoint);
                var selection = await new SettingsModelSelection(null, provider.Id + "/" + id, null) { UseModelMaximumTokens = true }
                    .ResolveAsync(null, runtime, null, false, CancellationToken.None);
                Equal(ModelResolver.DefaultModelFor(provider.Id) ?? id, selection.Model.Id, provider.Id + " default model");
                var what = $"{provider.Id}/{id} (maxTokens {selection.Definition.Raw.Value.GetProperty("maxTokens").GetDouble()})";
                driven++;
                try
                {
                    using var connection = selection.Connect(runtime);
                    var transport = connection.CreateTransport();
                    var level = transport is IThinkingLevelTransport levels ? ThinkingLevels.Clamp(levels.GetSupportedThinkingLevels(selection.Model), ModelResolver.DefaultThinkingLevel) : null;
                    StreamEvent? last = null;
                    await foreach (var frame in transport.StreamAsync(Request(selection.Model) with { ThinkingLevel = level })) last = frame;
                    var api = selection.Model.Api;
                    if (endpoint.Snapshot().Length == 0) failed.Add($"{what} at {level} sent no request ({last?.GetType().Name})");
                    else if (api is "openai-completions" or "openai-responses" or "anthropic-messages" && provider.Id != "azure" && last is not StreamDone)
                        failed.Add($"{what} at {level} ended with {last?.GetType().Name}");
                }
                catch (Exception error) when (error is not OperationCanceledException) { failed.Add($"{what} threw {error.GetType().Name}: {error.Message}"); }
            }
            Equal(35, driven, "providers with a key-authenticated live route besides anthropic");
            Check(failed.Count == 0, "default models the CLI cannot use: " + string.Join("; ", failed));
        });
        yield return ("sync160.gpt-6.2.official-metadata-openai-and-codex", Sync(() =>
        {
            // supports-xhigh.test.ts "includes official metadata for OpenAI and Codex %s" (the two rows 1.6.0 added).
            foreach (var (id, name, input, output, cacheRead, cacheWrite, codexContext) in new[]
                { ("gpt-6.2-astra", "GPT-6.2 Astra", 8m, 40m, .4m, 10m, 272_000), ("gpt-6.2-sol", "GPT-6.2 Sol", 2m, 10m, .1m, 2.5m, 500_000) })
                foreach (var provider in new[] { "openai", "openai-codex" })
                {
                    var row = Row160(provider, id); var what = provider + "/" + id;
                    Equal(name, row["name"]!.GetValue<string>(), what + " name");
                    Equal(provider == "openai" ? "openai-responses" : "openai-codex-responses", row["api"]!.GetValue<string>(), what + " api");
                    Check(row["reasoning"]!.GetValue<bool>(), what + " reasoning");
                    Check(Same("""["text","image"]""", row["input"]), what + " input");
                    var cost = row["cost"]!.AsObject();
                    Equal(input, cost["input"]!.GetValue<decimal>(), what + " input rate"); Equal(output, cost["output"]!.GetValue<decimal>(), what + " output rate");
                    Equal(cacheRead, cost["cacheRead"]!.GetValue<decimal>(), what + " cached input rate"); Equal(cacheWrite, cost["cacheWrite"]!.GetValue<decimal>(), what + " cache write rate");
                    var tiers = cost["tiers"]!.AsArray();
                    Equal(1, tiers.Count, what + " one long-context tier");
                    Equal(272_000, tiers[0]!["inputTokensAbove"]!.GetValue<int>(), what + " long-context threshold");
                    Equal(input * 2, tiers[0]!["input"]!.GetValue<decimal>(), what + " tier input"); Equal(output * 1.5m, tiers[0]!["output"]!.GetValue<decimal>(), what + " tier output");
                    Equal(cacheRead * 2, tiers[0]!["cacheRead"]!.GetValue<decimal>(), what + " tier cached input"); Equal(cacheWrite * 2, tiers[0]!["cacheWrite"]!.GetValue<decimal>(), what + " tier cache write");
                    Equal(provider == "openai-codex" ? codexContext : 272_000, row["contextWindow"]!.GetValue<int>(), what + " context window");
                    Equal(128_000, row["maxTokens"]!.GetValue<int>(), what + " max tokens");
                    var compat = row["compat"]!.AsObject();
                    foreach (var flag in new[] { "supportsAdditionalTools", "supportsMidConvoSystemMessages", "supportsOpenAIGrammarTools", "supportsToolSearch", "supportsMidConvoEffort" })
                        Check(compat[flag]?.GetValue<bool>() == true, what + " " + flag);
                    Equal(provider == "openai", compat.ContainsKey("supportsExplicitPromptCacheMode"), what + " explicit prompt cache mode only on the API-key provider");
                }
        }));
        yield return ("sync160.gpt-6.2.off-unavailable-and-thinking-levels", Sync(() =>
        {
            // supports-xhigh.test.ts "does not support off for %s": OpenAI and Codex reject reasoning.effort "none" and "minimal".
            var registry = ModelRegistry.Create(new());
            var expected = new (string Provider, string[] Levels)[]
            {
                ("openai", ["low", "medium", "high", "xhigh", "max"]), ("azure", ["low", "medium", "high", "xhigh", "max"]),
                ("openai-codex", ["minimal", "low", "medium", "high", "xhigh", "max"]),
            };
            foreach (var id in new[] { "gpt-6.1-sol", "gpt-6.2-astra", "gpt-6.2-sol" })
                foreach (var (provider, levels) in expected)
                {
                    var model = registry.Find(provider, id)!; var what = provider + "/" + id;
                    Check(model is not null, what + " registered");
                    Names(levels, VirtualModels.SupportedThinkingLevels(model!), what + " supported levels");
                    Names(levels, PiSharp.AI.Protocols.ProviderShared.ProviderTranscriptAccess.SupportedThinkingLevels(JsonData.Parse(model!.ToJsonString())), what + " transport levels");
                    var map = model.CloneJson()["thinkingLevelMap"]!.AsObject();
                    Check(map.ContainsKey("off") && map["off"] is null, what + " off is unavailable");
                    Equal(levels[0], VirtualModels.ClampThinkingLevel(model, "off"), what + " off clamps to the lowest level");
                    // Codex sends "low" for minimal; the API-key providers have no minimal level.
                    Check(provider == "openai-codex" ? map["minimal"]?.GetValue<string>() == "low" : map.ContainsKey("minimal") && map["minimal"] is null, what + " minimal");
                    Check(levels.Contains("xhigh") && levels.Contains("max"), what + " xhigh and max");
                }
            // GPT-6.1 Terra still accepts "none" (supports-xhigh.test.ts "supports off but not minimal for GPT-6.1 Terra").
            foreach (var provider in new[] { "openai", "azure", "openai-codex" })
                Equal("none", Row160(provider, "gpt-6.1-terra")["thinkingLevelMap"]!["off"]!.GetValue<string>(), provider + " gpt-6.1-terra off");
        }));
        yield return ("sync160.gpt-6.2.azure-1.05m-context-and-long-context-cost", async () =>
        {
            // supports-xhigh.test.ts "uses the Azure Foundry 1.05M context window for %s".
            foreach (var id in Gpt6MidConversationEffort) Equal(1_050_000, Row160("azure", id)["contextWindow"]!.GetValue<int>(), "azure " + id + " context window");
            foreach (var id in new[] { "gpt-6.2-astra", "gpt-6.2-sol" })
            {
                var azure = Row160("azure", id);
                Equal("azure-openai-responses", azure["api"]!.GetValue<string>(), "azure " + id + " api");
                Check(JsonNode.DeepEquals(Row160("openai", id)["cost"], azure["cost"]), "azure " + id + " keeps the complete OpenAI price table");
                Equal(128_000, azure["maxTokens"]!.GetValue<int>(), "azure " + id + " max tokens");
            }
            // The long-context tier is billed by the request's total input: at the threshold the base rates apply, one token above the tier's.
            var model = ModelRegistry.Create(new()).Find("azure", "gpt-6.2-astra")!;
            var descriptor = new ModelDescriptor(model.Id, model.Api, model.Provider);
            foreach (var (tokens, inputRate, outputRate, readRate, writeRate) in new[] { (272_000, 8m, 40m, .4m, 10m), (272_001, 16m, 60m, .8m, 20m) })
            {
                var usage = JsonSerializer.Serialize(new { input_tokens = tokens, output_tokens = 3, total_tokens = tokens + 3,
                    input_tokens_details = new { cached_tokens = 1000, cache_write_tokens = 1000 } });
                var sse = ResponsesSse.Replace("{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}", usage, StringComparison.Ordinal);
                var endpoint = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
                using var bound = NativeProviderFactory.CreateAzureResponses(descriptor, "fixture-key", JsonData.Parse(model.ToJsonString()),
                    new(Reasoning: model.Reasoning), azure: new() { AzureBaseUrl = "https://fixture.openai.azure.com/openai/v1" }, handler: endpoint);
                var terminal = await Drive(bound, descriptor);
                Check(terminal is StreamDone, "Azure response completes");
                var cost = ((StreamDone)terminal!).Message.Usage.Cost;
                Near((tokens - 2000) * inputRate / 1_000_000m, cost.Input, "input at " + tokens);
                Near(3 * outputRate / 1_000_000m, cost.Output, "output at " + tokens);
                Near(1000 * readRate / 1_000_000m, cost.CacheRead, "cache read at " + tokens);
                Near(1000 * writeRate / 1_000_000m, cost.CacheWrite, "cache write at " + tokens);
            }
            static void Near(decimal expected, decimal actual, string label) => Check(Math.Abs(expected - actual) < .000000001m, $"{label}: expected {expected}, actual {actual}");
        });
        yield return ("sync160.codex.mid-conversation-effort-flags", Sync(() =>
        {
            // catalog/providers/openai-codex.ts: OPENAI_MID_CONVO_EFFORT_MODEL_IDS now also marks the Codex rows (max-thinking.test.ts sends
            // max as a configuration_update item for exactly these models and as reasoning.effort for gpt-5.6-sol).
            var codex = CurrentModelCatalog.Get("openai-codex")!.Models.Where(model => model.Type == CatalogModelType.Chat).ToArray();
            Equal(10, codex.Length, "Codex chat models");
            Names(Gpt6MidConversationEffort.Order(StringComparer.Ordinal), codex.Where(model => model.Raw.Value.GetProperty("compat").TryGetProperty("supportsMidConvoEffort", out var flag) &&
                flag.ValueKind == JsonValueKind.True).Select(model => model.Id).Order(StringComparer.Ordinal), "Codex models with mid-conversation effort");
            foreach (var id in Gpt6MidConversationEffort)
            {
                Check(Row160("openai", id)["compat"]!["supportsMidConvoEffort"]!.GetValue<bool>(), "openai " + id + " mid-conversation effort");
                Check(!ReleasedModelCatalog.Pi152.Get("openai-codex")!.TryGetModel(CatalogModelType.Chat, id, out var old) ||
                    !old!.Raw.Value.GetProperty("compat").TryGetProperty("supportsMidConvoEffort", out _), "1.5.2 Codex " + id + " had no mid-conversation effort flag");
            }
            foreach (var id in new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" })
                Check(!Row160("openai-codex", id)["compat"]!.AsObject().ContainsKey("supportsMidConvoEffort"), "Codex " + id + " keeps reasoning.effort");
            // Every Codex GPT-6 family row lists max and xhigh (supports-xhigh.test.ts "includes xhigh for openai-codex %s models").
            foreach (var model in codex)
                Check(PiSharp.AI.Protocols.ProviderShared.ProviderTranscriptAccess.SupportedThinkingLevels(model.Raw).Contains("xhigh"), "Codex " + model.Id + " xhigh");
            // No row of the release needs a compat field this port does not declare: supportsMaxOutputTokens appears nowhere in 1.6.0.
            Check(!Rows(ReleasedModelCatalog.Current).Any(model => model.Raw.ToString().Contains("supportsMaxOutputTokens", StringComparison.Ordinal)), "no supportsMaxOutputTokens row");
        }));
        yield return ("sync160.defaults.openai-azure-and-codex-use-gpt-6.2-sol", Sync(() =>
        {
            // model-resolver.test.ts "openai defaults track current models"; github-copilot keeps gpt-6.1-sol upstream.
            foreach (var provider in new[] { "openai", "azure", "openai-codex" }) Equal("gpt-6.2-sol", ModelResolver.DefaultModelFor(provider), provider + " default");
            Equal("gpt-6.1-sol", ModelResolver.DefaultModelFor("github-copilot"), "github-copilot default is unchanged");
            Equal(3, ModelResolver.DefaultModelPerProvider.Count(entry => entry.Model == "gpt-6.2-sol"), "only the three OpenAI-hosted defaults moved");
            var catalog = ModelRegistry.Create(new());
            foreach (var (provider, id) in ModelResolver.DefaultModelPerProvider)
                Check(catalog.Find(provider, id) is not null, provider + " default is in the 1.6.0 catalog: " + id);
            Registry(out var openai, Env(("OPENAI_API_KEY", "k")));
            Equal("openai/gpt-6.2-sol", ModelResolver.FindInitialModel([], false, null, null, null, null, openai).Model?.Reference, "openai startup fallback");
            Equal("openai/gpt-6.1-sol", ModelResolver.FindInitialModel([], false, "openai", "gpt-6.1-sol", null, null, openai).Model?.Reference,
                "a saved gpt-6.1-sol selection is never replaced");
            Equal("openai/gpt-6.1-sol", ModelResolver.ResolveCliModel("openai", "gpt-6.1-sol", null, openai.GetAll(), openai.HasConfiguredAuth).Model?.Reference, "--model gpt-6.1-sol kept");
            Registry(out var azure, Env(("AZURE_OPENAI_API_KEY", "k")));
            Equal("azure/gpt-6.2-sol", ModelResolver.FindInitialModel([], false, null, null, null, null, azure).Model?.Reference, "azure startup fallback");
            Registry(out var codex, stored: new Dictionary<string, ProviderStoredCredential> { ["openai-codex"] = new("oauth") });
            Equal("openai-codex/gpt-6.2-sol", ModelResolver.FindInitialModel([], false, null, null, null, null, codex).Model?.Reference, "codex startup fallback");
            Equal("gpt-6.2-sol", LiveSessionSelection.Parse("openai-codex", "gpt-6.2-sol", null).Model.Id, "exact-identity path admits the new default");
            Equal(1_050_000d, LiveSessionSelection.Parse("azure", "gpt-6.2-sol", null).Definition.Raw.Value.GetProperty("contextWindow").GetDouble(), "azure default context window");
        }));
        yield return ("sync160.qwen.gateways-and-display-names", Sync(() =>
        {
            var registry = ModelRegistry.Create(new());
            foreach (var (provider, name, gateway, planVariable, sharedVariable) in QwenPlans)
            {
                Check(BuiltinProviders.TryGet(provider, out var builtin), provider + " is built in");
                Equal(name, builtin.Name, provider + " provider name");
                Equal(gateway, builtin.BaseUrl, provider + " provider gateway");
                Names([planVariable, sharedVariable], builtin.ApiKeyVariables, provider + " provider key variables");
                var auth = ProviderAuthCatalog.All.Single(entry => entry.Id == provider);
                Equal(name, auth.Name, provider + " login name");
                Equal(name + " API key", auth.ApiKeyName, provider + " API key name");
                Names([planVariable, sharedVariable], auth.EnvironmentVariables, provider + " login key variables");
                Equal(name, registry.GetProviderDisplayName(provider), provider + " display name");
                var rows = CurrentModelCatalog.Get(provider)!.Models;
                Check(rows.Length > 0 && rows.All(model => model.BaseUrl == gateway && model.DeclaredApi == "openai-completions"), provider + " rows use the current gateway");
                Check(!rows.Any(model => model.Raw.ToString().Contains("aliyuncs.com", StringComparison.Ordinal)), provider + " rows no longer name the former host");
            }
        }));
        yield return ("sync160.qwen.model-lists", Sync(() =>
        {
            var registry = ModelRegistry.Create(new());
            string[] Ids(string provider) => [.. registry.GetAll().Where(model => model.Provider == provider).Select(model => model.Id).Order(StringComparer.Ordinal)];
            // "exposes exactly the documented Individual text models", international and China.
            foreach (var provider in new[] { "qwen-token-plan-individual", "qwen-token-plan-individual-cn" })
                Names(QwenIndividualTextModels.Order(StringComparer.Ordinal), Ids(provider), provider + " exact allowlist");
            foreach (var provider in new[] { "qwen-token-plan", "qwen-token-plan-cn" })
            {
                var ids = Ids(provider);
                Equal(20, ids.Length, provider + " text models");
                foreach (var expected in QwenTeamTextModels) Check(ids.Contains(expected), provider + " should include " + expected);
                foreach (var excluded in QwenImageModels) Check(!ids.Contains(excluded), provider + " should not include " + excluded);
                Check(ids.Contains("qwen4-max-preview") && ids.Contains("deepseek-v4-flash-0731") && ids.Contains("deepseek-v4.1-flash"), provider + " successors of the retired models");
            }
            // #11773: deepseek-v3.2 and deepseek-v4-flash left the Token Plan lists; qwen3.8-max-preview was already retired.
            foreach (var (provider, _, _, _, _) in QwenPlans)
            {
                foreach (var retired in QwenRetiredModels) Check(!Ids(provider).Contains(retired), provider + " omits " + retired);
                Check(registry.GetAllOfType(CatalogModelType.Image, provider).Count == 0 && registry.GetAllOfType(CatalogModelType.Classifier, provider).Count == 0, provider + " has chat models only");
                Equal("qwen3.8-max", ModelResolver.DefaultModelFor(provider), provider + " default");
            }
            // The models 1.6.0 added to the Individual providers, with the Qwen thinking format.
            foreach (var provider in new[] { "qwen-token-plan-individual", "qwen-token-plan-individual-cn" })
            {
                var preview = Row160(provider, "qwen4-max-preview");
                Equal("Qwen4 Max Preview", preview["name"]!.GetValue<string>(), provider + " qwen4-max-preview name");
                Equal(1_000_000, preview["contextWindow"]!.GetValue<int>(), provider + " qwen4-max-preview context");
                Check(Same("""{"off":null,"minimal":null,"low":"low","medium":"medium","high":null,"xhigh":"xhigh","max":null}""", preview["thinkingLevelMap"]), provider + " qwen4-max-preview levels");
                foreach (var id in new[] { "deepseek-v4.1-flash", "deepseek-v4.1-flash-1017" })
                {
                    var flash = Row160(provider, id);
                    Check(Same("""{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"}""", flash["thinkingLevelMap"]), provider + " " + id + " levels");
                    Check(Same("""{"supportsStrictMode":true,"thinkingFormat":"qwen","supportsStore":false,"supportsReasoningEffort":true}""", flash["compat"]), provider + " " + id + " compat");
                }
                Check(!ReleasedModelCatalog.Pi152.Get(provider)!.TryGetModel(CatalogModelType.Chat, "qwen4-max-preview", out _), provider + " 1.5.2 had no qwen4-max-preview");
            }
            // "exposes Qwen reasoning_effort levels" and "exposes qwen3.8 reasoning_effort levels".
            foreach (var (provider, ids) in new[] { ("qwen-token-plan", new[] { "deepseek-v4-pro", "glm-5", "glm-5.1", "glm-5.2" }), ("qwen-token-plan-cn", ["deepseek-v4-pro", "glm-5", "glm-5.1", "glm-5.2"]),
                ("qwen-token-plan-individual", ["deepseek-v4-flash-0731", "deepseek-v4-pro", "deepseek-v4-pro-0813", "glm-5.2", "glm-5.3"]),
                ("qwen-token-plan-individual-cn", ["deepseek-v4-flash-0731", "deepseek-v4-pro", "deepseek-v4-pro-0813", "glm-5.2", "glm-5.3"]) })
                foreach (var id in ids)
                {
                    var map = Row160(provider, id)["thinkingLevelMap"]!.AsObject(); var what = provider + "/" + id;
                    Check(map.ContainsKey("minimal") && map["minimal"] is null && map.ContainsKey("medium") && map["medium"] is null && map.ContainsKey("xhigh") && map["xhigh"] is null, what + " unavailable levels");
                    Check(id == "glm-5.3" ? map["low"]?.GetValue<string>() == "low" : map.ContainsKey("low") && map["low"] is null, what + " low");
                    Check(map["high"]?.GetValue<string>() == "high" && map["max"]?.GetValue<string>() == "max", what + " high and max");
                }
            foreach (var (provider, _, _, _, _) in QwenPlans)
                foreach (var id in new[] { "qwen3.8-flash", "qwen3.8-max" })
                    Check(Same("""{"off":null,"minimal":null,"low":"low","medium":"medium","high":null,"xhigh":"xhigh","max":null}""", Row160(provider, id)["thinkingLevelMap"]), provider + "/" + id + " levels");
        }));
        yield return ("sync160.qwen.per-plan-key-preferred-then-shared", Sync(() =>
        {
            // #11773 "prefers the per-plan key on %s and falls back to the shared one".
            foreach (var (provider, _, _, planVariable, sharedVariable) in QwenPlans)
            {
                var both = Env((planVariable, "plan"), (sharedVariable, "shared")); var shared = Env((sharedVariable, "shared")); var plan = Env((planVariable, "plan"));
                Names([planVariable, sharedVariable], ProviderEnvironmentKeys.GetApiKeyVariables(provider)!, provider + " lookup order");
                Equal("plan", ProviderEnvironmentKeys.GetEnvironmentApiKey(provider, both), provider + " getEnvApiKey prefers the plan key");
                Equal("shared", ProviderEnvironmentKeys.GetEnvironmentApiKey(provider, shared), provider + " getEnvApiKey falls back to the shared key");
                Names([planVariable, sharedVariable], ProviderEnvironmentKeys.FindEnvironmentKeys(provider, both)!, provider + " findEnvKeys with both");
                Names([sharedVariable], ProviderEnvironmentKeys.FindEnvironmentKeys(provider, shared)!, provider + " findEnvKeys with the shared key");
                // envApiKeyAuth.resolve through the registry (models.getAuth): the first set variable, reported as the source.
                foreach (var (environment, key, source) in new[] { (both, "plan", planVariable), (shared, "shared", sharedVariable), (plan, "plan", planVariable) })
                {
                    Registry(out var registry, environment);
                    var auth = registry.ResolveRequestAuth(registry.Find(provider, "qwen3.8-max")!, out var error);
                    Equal<string?>(null, error, provider + " auth resolves with " + source);
                    Equal(key, auth?.ApiKey, provider + " request key from " + source);
                    Equal(source, auth?.Source, provider + " request key source");
                    Equal(source, registry.CheckAuth(provider), provider + " auth check source");
                    Check(registry.GetAvailable().Any(model => model.Provider == provider), provider + " is available with " + source);
                }
                // The injected-environment resolver (model operations) follows the same order.
                static ProviderEnvironmentSnapshot Snapshot(params (string Name, string Value)[] values) =>
                    new(process: values.Select(pair => KeyValuePair.Create(pair.Name, (string?)pair.Value)));
                var injected = InjectedAuthenticationResolver.GetEnvApiKey(provider, Snapshot((sharedVariable, "shared"), (planVariable, "plan")));
                Equal(AuthenticationDiagnostic.Resolved, injected.Diagnostic, provider + " injected resolution");
                Equal("plan", injected.Authentication!.Secret, provider + " injected plan key"); Equal(planVariable, injected.Authentication.EnvironmentName, provider + " injected plan variable");
                var fallback = InjectedAuthenticationResolver.GetEnvApiKey(provider, Snapshot((sharedVariable, "shared")));
                Equal("shared", fallback.Authentication!.Secret, provider + " injected shared key"); Equal(sharedVariable, fallback.Authentication.EnvironmentName, provider + " injected shared variable");
                Equal(AuthenticationDiagnostic.Missing, InjectedAuthenticationResolver.GetEnvApiKey(provider, Snapshot()).Diagnostic, provider + " injected missing");
            }
        }));
        yield return ("sync160.qwen.per-plan-key-stays-with-its-plan", Sync(() =>
        {
            // "keeps a per-plan key away from the other plan".
            Check(ProviderEnvironmentKeys.FindEnvironmentKeys("qwen-token-plan", Env(("QWEN_TOKEN_PLAN_INDIVIDUAL_API_KEY", "test"))) is null, "an Individual key is not a Team key");
            Check(ProviderEnvironmentKeys.FindEnvironmentKeys("qwen-token-plan-individual-cn", Env(("QWEN_TOKEN_PLAN_TEAM_CN_API_KEY", "test"))) is null, "a Team CN key is not an Individual CN key");
            foreach (var (provider, _, _, planVariable, _) in QwenPlans)
            {
                Registry(out var registry, Env((planVariable, "plan")));
                Names([provider], registry.GetAvailable().Select(model => model.Provider).Distinct(), planVariable + " enables only its own plan");
                foreach (var (other, _, _, _, _) in QwenPlans.Where(entry => entry.Provider != provider))
                {
                    Check(ProviderEnvironmentKeys.GetEnvironmentApiKey(other, Env((planVariable, "plan"))) is null, planVariable + " does not authenticate " + other);
                    Check(InjectedAuthenticationResolver.FindEnvKeys(other, new(process: [KeyValuePair.Create(planVariable, (string?)"plan")])).IsEmpty, planVariable + " is not an injected key of " + other);
                }
            }
            // The shared region variables still enable both plans of their region and never the other region.
            Registry(out var international, Env(("QWEN_TOKEN_PLAN_API_KEY", "shared")));
            Names(["qwen-token-plan", "qwen-token-plan-individual"], international.GetAvailable().Select(model => model.Provider).Distinct().Order(StringComparer.Ordinal), "shared international key");
            Registry(out var china, Env(("QWEN_TOKEN_PLAN_CN_API_KEY", "shared")));
            Names(["qwen-token-plan-cn", "qwen-token-plan-individual-cn"], china.GetAvailable().Select(model => model.Provider).Distinct().Order(StringComparer.Ordinal), "shared China key");
        }));
        yield return ("sync160.qwen.live-route-gateway-plan-key-and-thinking-fields", async () =>
        {
            // The CLI live route: the request goes to the plan's current gateway with the per-plan key (not the shared one), and every text
            // model takes Qwen's thinking fields ("sends Qwen thinking fields", "sends Qwen reasoning_effort", "sends qwen3.8 xhigh").
            // The session clamps the requested level to the model's levels (clampThinkingLevel), so "high" is xhigh on the qwen3.8 rows.
            var requests = 0;
            foreach (var (provider, _, gateway, planVariable, sharedVariable) in QwenPlans)
            {
                var individual = provider.Contains("individual", StringComparison.Ordinal);
                var effortModels = individual ? new[] { "deepseek-v4-flash-0731", "deepseek-v4-pro", "deepseek-v4-pro-0813", "glm-5.2", "glm-5.3" } : ["deepseek-v4-pro", "glm-5", "glm-5.1", "glm-5.2"];
                foreach (var id in individual ? QwenIndividualTextModels : QwenTeamThinkingModels)
                {
                    var endpoint = new LiveEndpoint(seen => Respond(seen, id));
                    var runtime = new LiveSessionRuntime(Env((sharedVariable, "shared-key"), (planVariable, "plan-key")), () => endpoint);
                    var selection = await new SettingsModelSelection(null, provider + "/" + id, null).ResolveAsync(null, runtime, null, false, CancellationToken.None);
                    Equal(provider + "/" + id, selection.Model.Provider + "/" + selection.Model.Id, "selected model");
                    using var connection = selection.Connect(runtime);
                    var level = PiSharp.AI.Protocols.ProviderShared.ProviderTranscriptAccess.ClampThinkingLevel(selection.Definition.Raw, "high");
                    var what = $"{provider}/{id}:{level}";
                    Equal(id is "qwen3.8-flash" or "qwen3.8-max" or "qwen4-max-preview" ? "xhigh" : level, level, what + " clamped level");
                    Check(level is "high" or "xhigh" or "max", what + " has a level at or above high");
                    StreamEvent? last = null;
                    await foreach (var frame in connection.CreateTransport().StreamAsync(Request(selection.Model) with { ThinkingLevel = level })) last = frame;
                    Check(last is StreamDone, what + " stream completes");
                    var request = endpoint.Snapshot().Single(); requests++;
                    Equal(gateway + "/chat/completions", request.Url, what + " endpoint");
                    Equal("Bearer plan-key", request.Headers["Authorization"], what + " per-plan key");
                    var payload = JsonNode.Parse(request.Body!)!.AsObject();
                    Equal(id, payload["model"]!.GetValue<string>(), what + " model");
                    Check(payload["enable_thinking"]?.GetValue<bool>() == true, what + " enable_thinking: " + request.Body);
                    Check(!payload.ContainsKey("thinking"), what + " sends no thinking object");
                    if (level == "xhigh") Equal("xhigh", payload["reasoning_effort"]?.GetValue<string>(), what + " reasoning_effort");
                    else if (effortModels.Contains(id)) Equal("high", payload["reasoning_effort"]?.GetValue<string>(), what + " reasoning_effort");
                }
            }
            Equal(4 * 13, requests, "one request per provider and model");
        });
        yield return ("sync160.copilot.strict-flags-and-thinking-levels", Sync(() =>
        {
            var rows = CurrentModelCatalog.Get("github-copilot")!.Models.Where(model => model.Type == CatalogModelType.Chat).ToArray();
            Equal(35, rows.Length, "Copilot chat models");
            static JsonObject Compat(FrozenCatalogModel model) => JsonNode.Parse(model.Raw.ToString())!["compat"]!.AsObject();
            // providers.test.ts: Copilot's Messages endpoint accepts strict tools on every Claude model.
            var claude = rows.Where(model => model.DeclaredApi == "anthropic-messages").ToArray();
            Equal(11, claude.Length, "Copilot Claude models");
            foreach (var model in claude) Check(Compat(model)["supportsStrictTools"]?.GetValue<bool>() == true, model.Id + " supportsStrictTools");
            // #12286: Copilot's /responses treats a tool without `strict` as strict, so every Responses row declares strict mode.
            var responses = rows.Where(model => model.DeclaredApi == "openai-responses").ToArray();
            Equal(18, responses.Length, "Copilot Responses models");
            foreach (var model in responses) Check(Compat(model)["supportsStrictMode"]?.GetValue<bool>() == true, model.Id + " supportsStrictMode");
            // #12110: the Gemini and Kimi rows take the thinking level as reasoning_effort and list their levels.
            var completions = rows.Where(model => model.DeclaredApi == "openai-completions").ToArray();
            Names(["gemini-3.8-flash", "gemini-4-argon", "gemini-4-carbon", "gemini-4-hydrogen", "gemini-4-neon", "kimi-k3"], completions.Select(model => model.Id).Order(StringComparer.Ordinal), "Copilot Chat Completions models");
            foreach (var model in completions)
            {
                var compat = Compat(model);
                Check(compat["supportsStrictMode"]?.GetValue<bool>() == true && compat["supportsStore"]?.GetValue<bool>() == false, model.Id + " strict mode, no store");
                Check(compat["supportsReasoningEffort"]?.GetValue<bool>() == true, model.Id + " supportsReasoningEffort");
                Check(!compat.ContainsKey("supportsDeveloperRole"), model.Id + " no explicit developer-role flag");
            }
            foreach (var id in new[] { "gemini-4-argon", "gemini-4-carbon", "gemini-3.8-flash" })
            {
                Check(Same("""{"off":null,"minimal":null,"low":"low","medium":"medium","high":"high","xhigh":null,"max":null}""", Row160("github-copilot", id)["thinkingLevelMap"]), id + " levels");
                Names(["low", "medium", "high"], PiSharp.AI.Protocols.ProviderShared.ProviderTranscriptAccess.SupportedThinkingLevels(
                    CurrentModelCatalog.Get("github-copilot")!.Models.Single(model => model.Id == id).Raw), id + " supported levels");
            }
            Check(Same("""{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"}""", Row160("github-copilot", "kimi-k3")["thinkingLevelMap"]), "kimi-k3 levels");
            Check(Same("""{"supportsStrictMode":true,"supportsStore":false,"supportsReasoningEffort":true,"supportsMidConvoSystemMessages":true}""", Row160("github-copilot", "kimi-k3")["compat"]), "kimi-k3 compat");
            // 1.5.2 shipped these rows with effort off and no levels.
            foreach (var id in new[] { "gemini-4-argon", "gemini-3.8-flash", "kimi-k3" })
            {
                Check(ReleasedModelCatalog.Pi152.Get("github-copilot")!.TryGetModel(CatalogModelType.Chat, id, out var old), "1.5.2 lists " + id);
                Check(!old!.Raw.Value.GetProperty("compat").GetProperty("supportsReasoningEffort").GetBoolean() && !old.Raw.Value.TryGetProperty("thinkingLevelMap", out _), "1.5.2 " + id + " had no thinking levels");
            }
            foreach (var id in new[] { "gpt-6.2-astra", "gpt-6.2-sol" })
            {
                var row = Row160("github-copilot", id);
                Check(Same("""{"off":null,"minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":"max"}""", row["thinkingLevelMap"]), "Copilot " + id + " levels");
                Check(Same("""{"supportsStrictMode":true,"supportsMidConvoSystemMessages":true,"supportsAdditionalTools":true,"supportsOpenAIGrammarTools":true}""", row["compat"]), "Copilot " + id + " compat");
            }
            Check(ModelRegistry.Create(new()).Find("github-copilot", ModelResolver.DefaultModelFor("github-copilot")!) is not null, "Copilot default is still listed");
        }));
        yield return ("sync160.catalog.together-and-fireworks-kimi-k3", Sync(() =>
        {
            // together-models.test.ts and fireworks-models.test.ts: maxTokens follows the context window, and no developer-role flag.
            var together = Row160("together", "moonshotai/Kimi-K3");
            Equal(1_048_576, together["contextWindow"]!.GetValue<int>(), "Together Kimi K3 context"); Equal(1_048_576, together["maxTokens"]!.GetValue<int>(), "Together Kimi K3 max tokens");
            Check(Same("""{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":0}""", together["cost"]), "Together Kimi K3 cost");
            Check(Same("""{"minimal":null,"low":null,"medium":null}""", together["thinkingLevelMap"]), "Together Kimi K3 levels");
            Check(Same("""{"supportsStore":false,"supportsReasoningEffort":false,"maxTokensField":"max_tokens","thinkingFormat":"together","supportsStrictMode":false,"supportsLongCacheRetention":false}""",
                together["compat"]), "Together Kimi K3 compat");
            const string fireworks = """{"supportsStore":false,"supportsStrictMode":true,"requiresReasoningContentOnAssistantMessages":true,"thinkingFormat":"openai","supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true,"sendSessionAffinityHeaders":true,"supportsLongCacheRetention":false}""";
            foreach (var id in new[] { "accounts/fireworks/models/kimi-k3", "accounts/fireworks/routers/kimi-k3-fast" })
                Check(Same(fireworks, Row160("fireworks", id)["compat"]), "Fireworks " + id + " compat");
        }));
        yield return ("sync160.models-schema-v1.6.0", Sync(() =>
        {
            // packages/coding-agent/schemas/models.schema.json at v1.6.0, embedded byte for byte (the derivative of v1.5.0 is retired).
            Equal("832194451e28b45820b27d765c36c1288ad04a6b", GitBlob(PiConfigurationSchemas.ReadBytes(PiConfigurationSchemas.ModelsFileName)), "models schema is the v1.6.0 blob");
            var raw = PiConfigurationSchemas.Read(PiConfigurationSchemas.ModelsFileName);
            Check(raw.Contains("so a change keeps the prompt cache. Used by the anthropic-messages, openai-responses, and openai-codex-responses APIs.", StringComparison.Ordinal),
                "supportsMidConvoEffort names the Codex API");
            Check(raw.Contains("Default: true for a baseUrl on api.anthropic.com, false otherwise. Set true for gateways that forward the thread request field.", StringComparison.Ordinal),
                "supportsMessageThreads default no longer names the anthropic provider");
            static bool Accepts(string compat) => PiConfigurationSchemas.Models.CheckJson("{\"providers\":{\"p\":{\"compat\":" + compat + "}}}");
            Check(Accepts("""{"supportsMessageThreads":true,"supportsMidConvoEffort":true,"supportsMaxOutputTokens":false,"supportsStrictMode":true,"supportsDeveloperRole":true}"""), "1.6.0 compat fields accepted");
            Check(!Accepts("""{"supportsMessageThreads":"yes"}"""), "non-boolean supportsMessageThreads rejected");
            Check(Accepts("""{"requiresNonNullAssistantContent":true,"requiresToolsWithToolHistory":false,"promptCacheKeyMode":"disabled"}"""), "upstream form of the former native extensions accepted");
            Check(!Accepts("""{"promptCacheKeyMode":"sometimes"}"""), "unknown cache mode rejected");
            // A models.json that names a 1.6.0 model with the new per-plan provider still loads.
            Check(PiConfigurationSchemas.Models.CheckJson("""{"providers":{"qwen-token-plan-individual":{"modelOverrides":{"qwen4-max-preview":{"contextWindow":500000}}}}}"""), "override of a 1.6.0 model");
        }));
    }
}
