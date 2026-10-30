using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.EmailReplyAssistant;
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
        var ctx = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-email-reply-" + Guid.NewGuid()), input, approved: true);
        var spec = Workflow.Create(ctx);
        if (spec.Tools.Count < 2 || spec.DemoCalls.Count < 2 || spec.DemoCalls.Any(c => !spec.Tools.Any(t => t.Name == c.Tool))) return 1;
        foreach (var call in spec.DemoCalls)
        {
            var tool = spec.Tools.Single(t => t.Name == call.Tool);
            var result = await tool.Execute(call.Arguments, default);
            if (result is not JsonObject || result.ToJsonString().Contains("placeholder", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        var draft = (JsonObject)(await spec.Tools.Single(t => t.Name == "draft_reply").Execute(SampleJson.Object("{\"id\":\"t-201\"}"), default))!;
        if (draft["status"]?.GetValue<string>() != "draft" || !draft["body"]!.GetValue<string>().Contains("includes the training session")) return 1;
        Console.WriteLine("PASS email-reply"); return 0;
    }
}
