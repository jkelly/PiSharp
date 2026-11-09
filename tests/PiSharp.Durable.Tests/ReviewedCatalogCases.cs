// PiSharp-only (no upstream Pi source): default durable postbaseline catalog coverage.
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Durable;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ReviewedCatalogCases() =>
    [
        Case("models.catalog.reviewed-corrections-apply-without-maintained-file", () =>
        {
            var changed = 0;
            foreach (var provider in DurableBuiltinCatalog.ShardHashes.Keys)
            {
                var original = FrozenModelCatalog.ReadProviderJson(provider, DurableBuiltinCatalog.ReadShard(provider));
                var current = DurableBuiltinCatalog.Get(provider)!;
                foreach (var old in original.Models)
                {
                    var actual = current.Models.Single(m => m.Type == old.Type && m.Id == old.Id);
                    var expected = JsonNode.Parse(old.Raw.ToString())!.AsObject();
                    if (provider == "azure" && new[] { "gpt-6-astra", "gpt-6-luna", "gpt-6-sol", "gpt-6.1-sol", "gpt-6.1-terra" }.Contains(old.Id))
                        expected["contextWindow"] = 1050000;
                    if (old.DeclaredApi == "anthropic-messages")
                    {
                        var compat = expected["compat"] as JsonObject ?? new JsonObject();
                        if ((provider is "opencode" or "opencode-go") && new[] { "claude-fable-5-1", "claude-haiku-5-5", "claude-opus-5", "claude-opus-5-5", "claude-sonnet-5-5" }.Contains(old.Id))
                        {
                            compat["supportsMidConvoEffort"] = true;
                            if (old.Id is not ("claude-opus-5" or "claude-haiku-5-5"))
                            {
                                var levels = expected["thinkingLevelMap"]!.AsObject(); levels["off"] = null;
                            }
                        }
                        if (old.Id.Contains("sonnet-5") || old.Id.Contains("sonnet.5")) compat["supportsTemperature"] = false;
                        if (compat.Count > 0) expected["compat"] = compat;
                    }
                    Check(JsonNode.DeepEquals(expected, JsonNode.Parse(actual.Raw.ToString())), provider + "/" + old.Id + " scope");
                    if (!JsonNode.DeepEquals(expected, JsonNode.Parse(old.Raw.ToString()))) changed++;
                }
            }
            Equal(18, changed, "exact C19 changed rows in default durable path");
            using var models = new NativeDurableModels();
            Equal(10, models.GetModels("qwen-token-plan-individual-cn").Count, "CN individual allowlist");
            Check(models.GetModel("qwen-token-plan-individual", "glm-5.3") is not null, "international individual GLM 5.3");
            Equal(1, models.GetModelsOfType("classifier", "github-copilot").Length, "Copilot classifier advertised separately");
            Check(models.GetModel("github-copilot", "microsoft-decision-1") is null, "classifier not exposed as chat");
        }),
        Case("models.catalog.qwen-individual-cn-routes-with-china-key", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(CompletionsText));
            using var models = Models(new() { ["QWEN_TOKEN_PLAN_CN_API_KEY"] = "fixture-china-key" }, handler);
            var model = models.GetModel("qwen-token-plan-individual-cn", "qwen3.8-max")!;
            await models.CompleteSimpleAsync(model, UserTurn("hello"), new DurableStreamOptions { MaxTokens = 128 }, CancellationToken.None);
            var request = handler.Requests.Single();
            Equal("https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions", request.Uri, "China endpoint");
            Equal("Bearer fixture-china-key", request.Header("Authorization"), "China key");
            Equal("qwen3.8-max", request.Json.GetProperty("model").GetString(), "default model payload");
        })
    ];
}
