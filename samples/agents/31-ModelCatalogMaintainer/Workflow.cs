using System.Text.Json.Nodes;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context)
    {
        ScanResult? result = null;
        var config = CatalogJson.Read<MaintainerConfig>(context.Input["config"]!.ToJsonString());
        return new WorkflowSpec("31", "Model Catalog and Pricing Maintainer",
            "Review configured providers, refresh the model catalog from the supplied recorded responses, and draft a source-linked price review. Explain skipped providers, unknown prices, increases and decreases. Publication is a separate explicitly approved maintenance command.",
            [
                new ToolSpec("inspect_providers", "Inspect enabled and excluded providers without making network calls.", SampleJson.Schema(), (_, _) =>
                    Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["providers"] = new JsonArray(config.Providers.Select(p => (JsonNode?)new JsonObject
                        { ["id"] = p.Id, ["enabled"] = p.Enabled, ["excluded"] = config.ExcludedProviders.Contains(p.Id, StringComparer.Ordinal), ["source"] = p.Endpoint }).ToArray())
                    })),
                new ToolSpec("refresh_catalog", "Compare recorded official-format source responses, preserve excluded entries, normalize explicit prices, and write an overlay proposal.", SampleJson.Schema(), async (_, token) =>
                {
                    result = await CatalogEngine.ScanAsync(config, context.Input["baseline"]!.AsObject(), Fixture(context.Input), token);
                    await context.WriteJsonAsync("maintenance-report.json", result.Report(), token);
                    if (!result.Complete) throw new InvalidOperationException("Catalog scan incomplete; inspect maintenance-report.json.");
                    await File.WriteAllTextAsync(context.ResolvePath("models.overlay.json"), CatalogJson.Canonical(result.Overlay), token);
                    return new JsonObject { ["complete"] = result.Complete, ["changed"] = result.Changed, ["modelChanges"] = result.Changes.Count, ["priceChanges"] = result.PriceChanges.Count, ["warnings"] = new JsonArray(result.Warnings.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
                }),
                new ToolSpec("draft_change_review", "Write a review with old/new amounts, absolute/percent differences, source evidence and a stable publication identity.", SampleJson.Schema(), async (_, token) =>
                {
                    if (result is null || !result.Complete) throw new InvalidOperationException("Refresh the catalog successfully first.");
                    await File.WriteAllTextAsync(context.ResolvePath("model-catalog-review.md"), ChangeReport.Markdown(result), token);
                    var plan = new JsonObject
                    {
                        ["branch"] = "automation/model-catalog", ["priceIssueIdentity"] = result.Fingerprint,
                        ["priceIssueRequired"] = result.PriceChanges.Count > 0, ["draftPullRequest"] = result.Changed,
                        ["publication"] = "not performed", ["automaticMerge"] = false
                    };
                    await context.WriteJsonAsync("publication-plan.json", plan, token);
                    return plan;
                })
            ],
            [new PlannedCall("inspect_providers", new JsonObject()), new PlannedCall("refresh_catalog", new JsonObject()), new PlannedCall("draft_change_review", new JsonObject())]);
    }

    public static FixtureFetcher Fixture(JsonObject input) => new(input["responses"]!.AsObject().ToDictionary(
        p => p.Key, p => p.Value is JsonValue text && text.TryGetValue<string>(out var body) ? body : p.Value!.ToJsonString(), StringComparer.Ordinal));
}
