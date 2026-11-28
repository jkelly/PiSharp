// PiSharp-only authored tests (no upstream capture): Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT), catalog/providers/anthropic.ts,
// catalog/families/anthropic.ts, catalog/providers/azure.ts and providers/openai-codex.ts.
// Authored offline expectations; no source capture or provider requests.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync152CatalogCases()
    {
        // Since the Pi 1.6.0 sync the 1.5.2 release is the preserved historical layer (ReleasedModelCatalog.Pi152). The cases that pinned
        // its bytes, counts and its delta from the 1.5.0 shards read that layer; the current-release pins are the sync160.catalog cases.
        yield return ("sync152.catalog.exact-current-package-resources", Sync(() =>
        {
            var release = ReleasedModelCatalog.Pi152;
            const string manifestHash = "71375843feaa116c7d928e74e0500a22cc267e12bfb6869efb92dfd39acf7fee";
            Equal(manifestHash, release.ManifestSha256, "pinned manifest fingerprint");
            Equal("d5715efa834c23bfdf10a042b49df4ee169cc99000bf16d7998fab2d533b45f8", release.PackageSha256, "pinned package SHA256");
            using var resource = typeof(CurrentModelCatalog).Assembly.GetManifestResourceStream("PiSharp.AI.Catalogs.Releases.1.5.2..manifest.json")!;
            Check(resource is not null, "exact release manifest resource");
            using var buffer = new MemoryStream(); resource!.CopyTo(buffer);
            Equal(manifestHash, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())), "manifest byte fingerprint");
            using var manifest = JsonDocument.Parse(buffer.ToArray());
            var files = manifest.RootElement.GetProperty("files").EnumerateObject().ToArray();
            Equal(43, files.Length, "all released provider files");
            Names(files.Select(f => f.Name[..^5]).Order(StringComparer.Ordinal), release.Providers, "provider enumeration derives from the 1.5.2 snapshot");
            foreach (var file in files)
                Equal(file.Value.GetString(), release.Get(file.Name[..^5])!.ProviderJsonSha256, file.Name + " byte fingerprint");
            Equal(1_794_312_337_628L, release.GeneratedAtUnixMilliseconds, "1.5.2 snapshot age gate");
            Equal("b4afdddf44e059c67ccf5097e5317d913521b470", release.SourceCommit, "1.5.2 source provenance");
            var rows = release.Providers.SelectMany(p => release.Get(p)!.Models).ToArray();
            Equal(1643, rows.Count(m => m.Type == CatalogModelType.Chat), "1.5.2 chat rows");
            Equal(66, rows.Count(m => m.Type == CatalogModelType.Image), "1.5.2 image rows");
            Equal(64, rows.Count(m => m.Type == CatalogModelType.Classifier), "1.5.2 classifier rows");
        }));
        yield return ("sync152.catalog.release-additions-removals-and-metadata", Sync(() =>
        {
            var historical = BuiltinModelCatalog.ShardHashes.Keys.SelectMany(p => BuiltinModelCatalog.Get(p).Models)
                .ToDictionary(m => (m.Provider, m.Type, m.Id));
            var current = ReleasedModelCatalog.Pi152.Providers.SelectMany(p => ReleasedModelCatalog.Pi152.Get(p)!.Models)
                .ToDictionary(m => (m.Provider, m.Type, m.Id));
            Equal(82, current.Keys.Except(historical.Keys).Count(), "all released additions");
            Equal(21, historical.Keys.Except(current.Keys).Count(), "all released removals");
            Equal(104, historical.Keys.Intersect(current.Keys).Count(key =>
                !JsonNode.DeepEquals(JsonNode.Parse(historical[key].Raw.ToString()), JsonNode.Parse(current[key].Raw.ToString()))), "all released changed rows");
            var registry = ModelRegistry.Create(new());
            foreach (var (provider, id) in new[] { ("nvidia", "meta/llama-3.2-11b-vision-instruct"),
                ("openrouter", "inception/mercury-2"), ("fireworks", "accounts/fireworks/models/gpt-oss-120b"),
                ("vercel-ai-gateway", "meituan/longcat-2.5-preview") })
            {
                Check(historical.ContainsKey((provider, CatalogModelType.Chat, id)), "historical fixture retained: " + id);
                Check(registry.Find(provider, id) is null, "removed entry absent from current registry: " + id);
            }
            Check(registry.GetAllOfType(CatalogModelType.Image, "openrouter").Any(m => m.Id == "openai/gpt-image-2.6-flare"), "new image available");
            Check(registry.GetAllOfType(CatalogModelType.Classifier, "openrouter").Any(m => m.Id == "morph/reflex"), "new classifier available");
            Equal(1_048_576d, registry.Find("google", "deep-research-preview-04-2026")!.ContextWindow, "released context correction");
            Equal(.75m, registry.Find("opencode", "gemini-3.8-flash")!.CloneJson()["cost"]!["input"]!.GetValue<decimal>(), "released price correction");
        }));
        yield return ("sync152.catalog.fable-all-released-provider-variants", Sync(() =>
        {
            // The 1.5.2 release had 11 variants on six providers; sync160.catalog.release-delta-from-1.5.2 pins the 13 of 1.6.0.
            var rows = ReleasedModelCatalog.Pi152.Providers.SelectMany(p => ReleasedModelCatalog.Pi152.Get(p)!.Models)
                .Where(m => m.Id.Contains("fable-5-2", StringComparison.Ordinal) || m.Id.Contains("fable-5.2", StringComparison.Ordinal)).ToArray();
            Equal(11, rows.Length, "all direct, dotted, prefixed and gateway Fable 5.2 variants");
            Names(new[] { "amazon-bedrock", "anthropic", "opencode", "openrouter", "radius", "vercel-ai-gateway" },
                rows.Select(m => m.Provider).Distinct().Order(StringComparer.Ordinal), "Fable 5.2 provider coverage");
            foreach (var row in rows)
                Equal(JsonValueKind.Null, row.Raw.Value.GetProperty("thinkingLevelMap").GetProperty("off").ValueKind, row.Id + " disallows off");
            foreach (var provider in new[] { "anthropic", "opencode", "openrouter" })
            {
                var row = rows.Single(m => m.Provider == provider && m.DeclaredApi == "anthropic-messages");
                Check(row.Raw.Value.GetProperty("compat").GetProperty("supportsMidConvoEffort").GetBoolean(), provider + " per-message effort");
            }
        }));
        yield return ("sync152.catalog.explicit-user-model-can-restore-removed-id", () => WithTemp("sync152-removed-override", async root =>
        {
            const string id = "meta/llama-3.2-11b-vision-instruct";
            var old = BuiltinModelCatalog.Get("nvidia").Models.Single(m => m.Id == id);
            var path = Path.Combine(root, "models.json");
            var config = new JsonObject { ["providers"] = new JsonObject { ["nvidia"] = new JsonObject
                { ["models"] = new JsonArray(JsonNode.Parse(old.Raw.ToString())) } } };
            await File.WriteAllTextAsync(path, config.ToJsonString());
            var registry = ModelRegistry.Create(new() { ModelsPath = path, MaintainedCatalogPath = Path.Combine(root, "missing") });
            Check(registry.Find("nvidia", id) is not null, "explicit custom model survives released removal");
            Check(!CurrentModelCatalog.Get("nvidia")!.Models.Any(m => m.Id == id), "current snapshot remains unchanged");
        }));
        yield return ("sync152.catalog.fable-finalized-metadata-and-selection", Sync(() =>
        {
            var registry = ModelRegistry.Create(new());
            var fable = registry.Find("anthropic", "claude-fable-5-2")!;
            Check(fable is not null, "Fable 5.2 registered");
            var raw = fable!.CloneJson();
            Equal("Claude Fable 5.2", fable.Name, "display name");
            Equal("anthropic-messages", fable.Api, "API");
            Equal("https://api.anthropic.com", fable.BaseUrl, "endpoint");
            Check(fable.Reasoning, "reasoning");
            Equal(1_000_000d, fable.ContextWindow, "context");
            Equal(128_000d, raw["maxTokens"]!.GetValue<double>(), "output limit");
            Equal("[\"text\",\"image\"]", raw["input"]!.ToJsonString(), "input modalities");
            Equal("{\"input\":8,\"output\":40,\"cacheRead\":0.2,\"cacheWrite\":10}", raw["cost"]!.ToJsonString(), "prices");
            var compat = raw["compat"]!.AsObject();
            foreach (var key in new[] { "forceAdaptiveThinking", "supportsMidConvoEffort", "supportsMidConvoSystemMessages", "supportsMidConvoToolChanges", "supportsStrictTools" })
                Equal(true, compat[key]!.GetValue<bool>(), key);
            Check(Same("""{"off":null,"xhigh":"xhigh","max":"max"}""", raw["thinkingLevelMap"]), "effort mapping");
            Equal("{\"short\":300,\"long\":3600}", raw["promptCache"]!.ToJsonString(), "cache retention");
            var limits = raw["inputLimits"]!;
            Equal(33_554_432, limits["maxRequestBytes"]!.GetValue<int>(), "request bytes");
            Equal(600, limits["images"]!["maxPerRequest"]!.GetValue<int>(), "image count");
            Check(JsonNode.DeepEquals(registry.Find("anthropic", "claude-fable-5-1")!.CloneJson()["inputLimits"]!["images"]!["resize"], limits["images"]!["resize"]), "standard image resize");
            var fallback = compat["allowedFallbackModels"]!.AsArray();
            Equal(1, fallback.Count, "finalizeAll removes Opus 4.8, which lacks mid-conversation effort");
            Equal("anthropic", fallback[0]!["provider"]!.GetValue<string>(), "fallback provider");
            Equal("claude-opus-5", fallback[0]!["model"]!.GetValue<string>(), "fallback model");
            Check(JsonNode.DeepEquals(registry.Find("anthropic", "claude-opus-5")!.CloneJson()["cost"], fallback[0]!["cost"]), "pinned fallback prices");
            Equal(fable.Id, LiveSessionSelection.Parse("anthropic", fable.Id, null).Model.Id, "CLI exact selection");
            Equal("claude-opus-5-5", ModelResolver.DefaultModelFor("anthropic"), "default unchanged");
            Check(!BuiltinModelCatalog.Get("anthropic").Models.Any(m => m.Id == fable.Id), "historical shard remains unchanged");
        }));
        yield return ("sync152.catalog.fable-request-adaptive-effort", Sync(() =>
        {
            var fable = ModelRegistry.Create(new()).Find("anthropic", "claude-fable-5-2")!;
            var descriptor = new ModelDescriptor(fable.Id, fable.Api, fable.Provider);
            using var client = new HttpClient();
            var options = new AnthropicMessagesSimpleOptions(JsonData.Parse(fable.ToJsonString()), new(MaximumTokens: 128, AllowedFallbackModels: NativeProviderFactory.AnthropicFallbackModels(JsonData.Parse(fable.ToJsonString()).Value)), "xhigh");
            var factory = new AnthropicMessagesSimpleRequestFactory(client, new Uri(fable.BaseUrl), descriptor, options);
            var payload = factory.Prepare(Request(descriptor), "fixture-key").Payload.Value;
            Equal("adaptive", payload.GetProperty("thinking").GetProperty("type").GetString(), "adaptive thinking");
            Equal("drop_block", payload.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString(), "thinking block binding");
            Check(payload.GetProperty("messages").ToString().Contains("output_config", StringComparison.Ordinal), "per-message effort");
            Check(payload.GetProperty("messages").ToString().Contains("xhigh", StringComparison.Ordinal), "requested effort");
            Equal("claude-opus-5", payload.GetProperty("fallbacks")[0].GetProperty("model").GetString(), "finalized fallback request");
            Equal(1, payload.GetProperty("fallbacks").GetArrayLength(), "only compatible fallback sent");
        }));
        yield return ("sync152.catalog.fable-fallback-billing", async () =>
        {
            var model = ModelRegistry.Create(new()).Find("anthropic", "claude-fable-5-2")!;
            var metadata = JsonData.Parse(model.ToJsonString());
            var descriptor = new ModelDescriptor(model.Id, model.Api, model.Provider);
            var endpoint = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(MessagesSse("claude-opus-5"), Encoding.UTF8, "text/event-stream") });
            using var bound = NativeProviderFactory.CreateAnthropic(descriptor, new Uri(model.BaseUrl), "fixture-key",
                new(MaximumTokens: 128, ModelReasoning: true, AllowedFallbackModels: NativeProviderFactory.AnthropicFallbackModels(metadata.Value)),
                handler: endpoint, modelMetadata: metadata);
            var terminal = await Drive(bound, descriptor);
            Check(terminal is StreamDone, "fallback response completes");
            var cost = ((StreamDone)terminal!).Message.Usage.Cost;
            Check(Math.Abs(cost.Input - .00001m) < .000000001m, "fallback uses Opus input rate");
            Check(Math.Abs(cost.Output - .000025m) < .000000001m, "fallback uses Opus output rate");
        });
        yield return ("sync152.catalog.azure-all-tiered-counterparts", Sync(() =>
        {
            var registry = ModelRegistry.Create(new());
            var tiered = BuiltinModelCatalog.Get("openai").Models.Where(m => m.DeclaredApi == "openai-responses" && m.Cost.Value.TryGetProperty("tiers", out _)).ToArray();
            var count = 0;
            foreach (var openai in tiered)
            {
                var azure = registry.Find("azure", openai.Id);
                if (azure is null) continue;
                Check(JsonNode.DeepEquals(JsonNode.Parse(openai.Cost.ToString()), azure.CloneJson()["cost"]), openai.Id + " retains complete OpenAI price table");
                count++;
            }
            Equal(13, count, "all tiered Azure counterparts, including Daybreak alias");
            var oldDeepSeek = BuiltinModelCatalog.Get("azure").Models.Single(m => m.Id == "deepseek-v4-pro");
            Check(JsonNode.DeepEquals(JsonNode.Parse(oldDeepSeek.Cost.ToString()), registry.Find("azure", oldDeepSeek.Id)!.CloneJson()["cost"]), "Azure-specific DeepSeek rates retained");
        }));
        yield return ("sync152.catalog.azure-tier-boundary-runtime-cost", async () =>
        {
            var model = ModelRegistry.Create(new()).Find("azure", "gpt-6.1-sol")!;
            var descriptor = new ModelDescriptor(model.Id, model.Api, model.Provider);
            foreach (var (tokens, inputRate, outputRate, readRate, writeRate) in new[]
                { (272_000, 2m, 10m, .1m, 2.5m), (272_001, 4m, 15m, .2m, 5m) })
            {
                var usage = JsonSerializer.Serialize(new { input_tokens = tokens, output_tokens = 3, total_tokens = tokens + 3,
                    input_tokens_details = new { cached_tokens = 1000, cache_write_tokens = 1000 } });
                var sse = ResponsesSse.Replace("{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}", usage, StringComparison.Ordinal);
                var endpoint = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
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
        yield return ("sync152.catalog.user-cost-and-capability-overrides-win", () => WithTemp("sync152-catalog", async root =>
        {
            var path = Path.Combine(root, "models.json");
            await File.WriteAllTextAsync(path, """
                {"providers":{"azure":{"modelOverrides":{"gpt-6.1-sol":{"cost":{"input":1,"output":2,"cacheRead":3,"cacheWrite":4,"tiers":[]}}}},
                "anthropic":{"modelOverrides":{"claude-fable-5-2":{"compat":{"supportsMidConvoEffort":false}}}}}}
                """);
            var registry = ModelRegistry.Create(new() { ModelsPath = path, MaintainedCatalogPath = Path.Combine(root, "missing") });
            var cost = registry.Find("azure", "gpt-6.1-sol")!.CloneJson()["cost"]!;
            Equal(1, cost["input"]!.GetValue<int>(), "explicit price override");
            Equal(0, cost["tiers"]!.AsArray().Count, "explicit empty tiers override");
            Equal(false, registry.Find("anthropic", "claude-fable-5-2")!.Compat!["supportsMidConvoEffort"]!.GetValue<bool>(), "explicit capability override");
        }));
        yield return ("sync152.catalog.codex-label", Sync(() =>
        {
            Equal("OpenAI Codex", BuiltinProviders.All.Single(p => p.Id == "openai-codex").Name, "provider label");
            Equal("OpenAI Codex", ProviderAuthCatalog.All.Single(p => p.Id == "openai-codex").Name, "auth label");
        }));
    }
}
