// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.InboxTriage;
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
        var ctx = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-inbox-" + Guid.NewGuid()), input);
        var spec = Workflow.Create(ctx);
        if (spec.Tools.Count < 2 || spec.DemoCalls.Count < 2) return 1;
        var list = (JsonObject)(await spec.Tools[0].Execute(SampleJson.Object("{\"unread_only\":true}"), default))!;
        var assessed = (JsonObject)(await spec.Tools[1].Execute(SampleJson.Object("{\"id\":\"m-101\"}"), default))!;
        if (list["count"]?.GetValue<int>() != 2 || assessed["priority"]?.GetValue<string>() != "high") return 1;
        Console.WriteLine("PASS inbox triage"); return 0;
    }
}
