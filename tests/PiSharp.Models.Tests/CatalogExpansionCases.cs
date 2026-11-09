// PiSharp-only (no upstream Pi source): reviewed F09 provider/model expansion tests.
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CatalogExpansionCases()
    {
        yield return ("catalog-f09.default-map-exact-upstream", Sync(() =>
        {
            var expected = """
                amazon-bedrock/us.anthropic.claude-opus-5-5
                ant-ling/Ring-2.6-1T
                anthropic/claude-opus-5-5
                openai/gpt-6.1-sol
                azure/gpt-6.1-sol
                openai-codex/gpt-6.1-sol
                radius/balanced
                nvidia/nvidia/nemotron-3.5-super-vl-120b-a12b
                deepseek/deepseek-v4-pro
                google/gemini-4-argon
                google-vertex/gemini-4-argon
                github-copilot/gpt-6.1-sol
                openrouter/moonshotai/kimi-k2.7-code
                vercel-ai-gateway/zai/glm-5.3
                xai/grok-4.7
                groq/openai/gpt-oss-120b
                cerebras/gpt-oss-120b
                zai/glm-5.3
                zai-coding-cn/glm-5.3
                mistral/devstral-medium-latest
                minimax/MiniMax-M3
                minimax-cn/MiniMax-M3
                moonshotai/kimi-k2.7-code
                moonshotai-cn/kimi-k2.7-code
                huggingface/moonshotai/Kimi-K2.7-Code
                fireworks/accounts/fireworks/models/kimi-k3
                together/moonshotai/Kimi-K3
                baseten/zai-org/GLM-5.3
                opencode/kimi-k2.7-code
                opencode-go/kimi-k3.1-pro
                kimi-coding/kimi-for-coding
                meta/muse-spark-1.3
                cloudflare-workers-ai/@cf/moonshotai/kimi-k2.7-code
                cloudflare-ai-gateway/workers-ai/@cf/moonshotai/kimi-k2.7-code
                qwen-token-plan/qwen3.8-max
                qwen-token-plan-cn/qwen3.8-max
                qwen-token-plan-individual/qwen3.8-max
                qwen-token-plan-individual-cn/qwen3.8-max
                xiaomi/mimo-v2.6-pro
                xiaomi-token-plan-cn/mimo-v2.6-pro
                xiaomi-token-plan-ams/mimo-v2.6-pro
                xiaomi-token-plan-sgp/mimo-v2.6-pro
                """.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Names(expected, ModelResolver.DefaultModelPerProvider.Select(p => p.Provider + "/" + p.Model), "reviewed default map");
            var registry = ModelRegistry.Create(new());
            foreach (var (provider, id) in ModelResolver.DefaultModelPerProvider)
                Check(registry.Find(provider, id) is not null, provider + " default is available");
            Equal(1724, Enum.GetValues<CatalogModelType>().Sum(type => registry.GetAllOfType(type).Count), "exact twelve added rows");
            var exactAzure = LiveSessionSelection.Parse("azure", "gpt-6.1-sol", null);
            Equal(1050000d, exactAzure.Definition.Raw.Value.GetProperty("contextWindow").GetDouble(), "exact-identity path uses reviewed corrections");
            Equal("qwen3.8-max", LiveSessionSelection.Parse("qwen-token-plan-individual-cn", "qwen3.8-max", null).Model.Id, "exact-identity path admits new provider");
        }));
        yield return ("catalog-f09.qwen-cn-exact-allowlist-and-auth", Sync(() =>
        {
            const string provider = "qwen-token-plan-individual-cn";
            Check(BuiltinProviders.All.Any(item => item.Id == provider), "reviewed provider is registered");
            Check(!BuiltinModelCatalog.Has(provider), "reviewed provider does not invent a historical shard");
            var missingShard = Throws<BuiltinCatalogException>(() => BuiltinModelCatalog.Get(provider), "no historical shard");
            Equal("LiveCatalogUnavailable", missingShard.Code, "missing shard retains explicit failure");
            var reviewed = ReviewedModelCatalog.Apply(provider, null)!;
            Equal(10, reviewed.Models.Length, "reviewed-only provider admits its exact model set");
            var registry = ModelRegistry.Create(new());
            var models = registry.GetAll().Where(m => m.Provider == "qwen-token-plan-individual-cn").OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
            Names(["deepseek-v4-flash-0731", "deepseek-v4-pro", "deepseek-v4-pro-0813", "glm-5.2", "glm-5.3", "qwen3.6-flash", "qwen3.7-max", "qwen3.7-plus", "qwen3.8-flash", "qwen3.8-max"], models.Select(m => m.Id), "individual CN allowlist");
            var source = BuiltinModelCatalog.Get("qwen-token-plan-cn");
            foreach (var model in models)
            {
                var original = source.Models.Single(m => m.Id == model.Id);
                var expected = JsonNode.Parse(original.Raw.ToString())!.AsObject();
                expected["provider"] = "qwen-token-plan-individual-cn";
                Check(JsonNode.DeepEquals(expected, model.CloneJson()), model.Id + " only changes provider identity");
            }
            var wrongKey = ModelRegistry.Create(new() { Environment = Env(("QWEN_TOKEN_PLAN_API_KEY", "fixture-international")) });
            Check(!wrongKey.GetAvailable().Any(m => m.Provider == "qwen-token-plan-individual-cn"), "international key does not enable China provider");
            var chinaKey = ModelRegistry.Create(new() { Environment = Env(("QWEN_TOKEN_PLAN_CN_API_KEY", "fixture-china")) });
            Equal(10, chinaKey.GetAvailable().Count(m => m.Provider == "qwen-token-plan-individual-cn"), "China key enables exact allowlist");
            Check(registry.Find("qwen-token-plan-individual", "glm-5.3") is not null, "international individual adds GLM 5.3");
        }));
        yield return ("catalog-f09.classifier-is-not-chat-and-user-overrides-win", () => WithTemp("catalog-f09", async root =>
        {
            var registry = ModelRegistry.Create(new());
            var classifier = registry.GetAllOfType(CatalogModelType.Classifier, "github-copilot").Single(m => m.Id == "microsoft-decision-1");
            Equal("typesafe-system-one", classifier.Api, "Copilot classifier API");
            Equal(32768d, classifier.ContextWindow, "Copilot classifier context");
            Equal(0.042d, classifier.CloneJson()["cost"]!["input"]!.GetValue<double>(), "Copilot source price");
            Check(registry.Find("github-copilot", "microsoft-decision-1") is null, "classifier cannot become chat default");
            var path = Path.Combine(root, "models.json");
            await File.WriteAllTextAsync(path, """{"providers":{"azure":{"modelOverrides":{"gpt-6.1-sol":{"contextWindow":123456}}}}}""");
            var overridden = ModelRegistry.Create(new() { ModelsPath = path, MaintainedCatalogPath = Path.Combine(root, "missing") });
            Equal(123456d, overridden.Find("azure", "gpt-6.1-sol")!.ContextWindow, "explicit user override follows required corrections");
        }));
    }
}
