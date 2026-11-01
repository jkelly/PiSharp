// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.PersistentTaskManager;
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
        var ctx = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-persistent-task-manager-" + Guid.NewGuid()), input, approved: true);
        var spec = Workflow.Create(ctx);
        if (spec.Tools.Count < 2 || spec.DemoCalls.Count < 2 || spec.DemoCalls.Any(c => !spec.Tools.Any(t => t.Name == c.Tool))) return 1;
        foreach (var call in spec.DemoCalls)
        {
            var tool = spec.Tools.Single(t => t.Name == call.Tool);
            var result = await tool.Execute(call.Arguments, default);
            if (result is not JsonObject || result.ToJsonString().Contains("placeholder", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        var add = spec.Tools.Single(t => t.Name == "add_task");
        if (!add.RequiresApproval) return 1;
        await add.Execute(SampleJson.Object("{\"id\":\"task-503\",\"title\":\"Ship report\"}"), default);
        var persisted = (JsonObject)(await spec.Tools.Single(t => t.Name == "list_tasks").Execute(new JsonObject(), default))!;
        if (!File.Exists(ctx.ResolvePath("tasks.json")) || persisted["tasks"]?.AsArray().Count != 3) return 1;
        await spec.Tools.Single(t => t.Name == "complete_task").Execute(SampleJson.Object("{\"id\":\"task-503\"}"), default);
        var completed = (JsonObject)(await spec.Tools.Single(t => t.Name == "inspect_task").Execute(SampleJson.Object("{\"id\":\"task-503\"}"), default))!;
        if (completed["task"]?["status"]?.GetValue<string>() != "done") return 1;
        var denied = Workflow.Create(new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-deny-" + Guid.NewGuid()), input));
        try { await denied.Tools.Single(t => t.Name == "add_task").Execute(SampleJson.Object("{\"id\":\"task-504\",\"title\":\"Forbidden\"}"), default); return 1; }
        catch (UnauthorizedAccessException) { }
        Console.WriteLine("PASS persistent-task-manager"); return 0;
    }
}
