// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.FormIntakeAssistant;
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
        var ctx = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-form-intake-assistant-" + Guid.NewGuid()), input, approved: true);
        var spec = Workflow.Create(ctx);
        if (spec.Tools.Count < 2 || spec.DemoCalls.Count < 2 || spec.DemoCalls.Any(c => !spec.Tools.Any(t => t.Name == c.Tool))) return 1;
        foreach (var call in spec.DemoCalls)
        {
            var tool = spec.Tools.Single(t => t.Name == call.Tool);
            var result = await tool.Execute(call.Arguments, default);
            if (result is not JsonObject || result.ToJsonString().Contains("placeholder", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        var invalid = (JsonObject)(await spec.Tools.Single(t => t.Name == "validate_form").Execute(SampleJson.Object("{\"id\":\"form-802\"}"), default))!;
        var route = (JsonObject)(await spec.Tools.Single(t => t.Name == "prepare_routing").Execute(SampleJson.Object("{\"id\":\"form-802\"}"), default))!;
        if (invalid["valid"]?.GetValue<bool>() != false || invalid["missing"]?.AsArray().Count != 2 || route["status"]?.GetValue<string>() != "needs-information") return 1;
        Console.WriteLine("PASS form-intake-assistant"); return 0;
    }
}
