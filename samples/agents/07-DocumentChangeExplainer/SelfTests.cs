using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.DocumentChangeExplainer;
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
        var ctx = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-document-change-explainer-" + Guid.NewGuid()), input, approved: true);
        var spec = Workflow.Create(ctx);
        if (spec.Tools.Count < 2 || spec.DemoCalls.Count < 2 || spec.DemoCalls.Any(c => !spec.Tools.Any(t => t.Name == c.Tool))) return 1;
        foreach (var call in spec.DemoCalls)
        {
            var tool = spec.Tools.Single(t => t.Name == call.Tool);
            var result = await tool.Execute(call.Arguments, default);
            if (result is not JsonObject || result.ToJsonString().Contains("placeholder", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        var comparison = (JsonObject)(await spec.Tools.Single(t => t.Name == "compare_sections").Execute(SampleJson.Object("{\"id\":\"policy-701\"}"), default))!;
        if (comparison["changes"]?.AsArray().Count != 3 || !comparison["changes"]!.AsArray().Any(x => x?["kind"]?.GetValue<string>() == "added")) return 1;
        Console.WriteLine("PASS document-change-explainer"); return 0;
    }
}
