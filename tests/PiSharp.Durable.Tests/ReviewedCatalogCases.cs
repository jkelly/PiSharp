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
            Equal(43, DurableBuiltinCatalog.Providers.Length, "all released providers");
            var count = 0;
            foreach (var provider in CurrentModelCatalog.Providers)
            {
                var expected = CurrentModelCatalog.Get(provider)!;
                var current = DurableBuiltinCatalog.Get(provider)!;
                Equal(expected.ProviderJsonSha256, current.ProviderJsonSha256, provider + " exact released snapshot");
                count += current.Models.Length;
            }
            Equal(1773, count, "released entries of all model types");
            var fable = DurableBuiltinCatalog.Get("anthropic")!.Models.Single(m => m.Id == "claude-fable-5-2");
            Equal(1_000_000, fable.Raw.Value.GetProperty("contextWindow").GetInt32(), "Fable 5.2 context");
            Equal(40m, fable.Cost.Value.GetProperty("output").GetDecimal(), "Fable 5.2 output rate");
            Check(fable.Raw.Value.GetProperty("compat").GetProperty("supportsMidConvoEffort").GetBoolean(), "Fable 5.2 finalization");
            using var models = new NativeDurableModels();
            Check(models.GetModel("anthropic", "claude-fable-5-2") is not null, "Fable 5.2 available to durable callers");
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
