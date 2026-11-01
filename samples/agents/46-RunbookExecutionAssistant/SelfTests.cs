// PiSharp-only (no upstream Pi source): local execution, dependency and approval tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.RunbookExecutionAssistant;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var count = 0;
        SampleContext Context(bool approved = true) => new(Path.Combine(Path.GetTempPath(),"pisharp-46-" + Guid.NewGuid().ToString("N")),(JsonObject)input.DeepClone(),approved);
        async Task<JsonNode> Call(SampleContext c,string name,string id = "recovery") => (await Workflow.Create(c).Tools.Single(t => t.Name == name).Execute(new JsonObject { ["runbookId"] = id },default))!;
        static void Check(bool value) { if (!value) throw new Exception("Runbook assertion failed."); }
        static async Task Reject(Func<Task<JsonNode>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return; } throw new Exception("Expected rejection."); }
        var c = Context(); await Call(c,"inspect_runbook"); var result = await Call(c,"execute_runbook"); Check(result["success"]!.GetValue<bool>() && result["results"]!.AsArray().Count == 4 && await File.ReadAllTextAsync(c.ResolvePath("operations/recovery/status.txt")) == "recovery fixture ready\n"); await Call(c,"write_execution_report"); count++;
        var repeated = await Call(c,"execute_runbook"); Check(JsonNode.DeepEquals(result,repeated)); count++;
        await Call(c,"inspect_runbook","audit"); var failed = await Call(c,"execute_runbook","audit"); Check(!failed["success"]!.GetValue<bool>() && failed["results"]![0]!["status"]!.GetValue<string>() == "failed" && failed["results"]![1]!["status"]!.GetValue<string>() == "skipped"); count++;
        var no = Context(false); await Call(no,"inspect_runbook"); Check(Workflow.Create(no).Tools.Single(t => t.Name == "execute_runbook").RequiresApproval); await Reject(() => Call(no,"execute_runbook")); Check(!Directory.Exists(no.ResolvePath("operations/recovery"))); count++;
        await Reject(() => Call(Context(),"execute_runbook")); await Reject(() => Call(Context(),"inspect_runbook","missing")); count++;
        var malicious = Context(); malicious.Input["runbooks"]![0]!["steps"]![0]!["action"] = "shell"; await Reject(() => Call(malicious,"inspect_runbook")); count++;
        foreach (var invalid in new JsonNode?[] { null, JsonValue.Create("invalid step") })
        {
            var malformed = Context(); malformed.Input["runbooks"]![0]!["steps"]!.AsArray().Add(invalid);
            await Reject(() => Call(malformed,"inspect_runbook")); await Reject(() => Call(malformed,"execute_runbook"));
            Check(!Directory.Exists(malformed.ResolvePath("operations/recovery")) && !File.Exists(malformed.ResolvePath("runbook-execution.json"))); count++;
        }
        var oversized = Context(); var oversizedSteps = oversized.Input["runbooks"]![0]!["steps"]!.AsArray();
        while (oversizedSteps.Count <= 20) oversizedSteps.Add((JsonNode?)null);
        await Reject(() => Call(oversized,"inspect_runbook")); await Reject(() => Call(oversized,"execute_runbook"));
        Check(!Directory.Exists(oversized.ResolvePath("operations/recovery")) && !File.Exists(oversized.ResolvePath("runbook-execution.json"))); count++;
        var tooManyObjects = Context(); var validSteps = new JsonArray();
        for (var index = 0; index < 21; index++)
        {
            var step = (JsonObject)tooManyObjects.Input["runbooks"]![0]!["steps"]![0]!.DeepClone();
            step["id"] = "step-" + index; step["path"] = "operations/recovery/step-" + index + ".txt"; validSteps.Add(step);
        }
        tooManyObjects.Input["runbooks"]![0]!["steps"] = validSteps;
        await Reject(() => Call(tooManyObjects,"inspect_runbook")); await Reject(() => Call(tooManyObjects,"execute_runbook"));
        Check(!Directory.Exists(tooManyObjects.ResolvePath("operations/recovery")) && !File.Exists(tooManyObjects.ResolvePath("runbook-execution.json"))); count++;
        var escape = Context(); escape.Input["runbooks"]![0]!["steps"]![0]!["path"] = "operations/recovery/../../outside.txt"; await Reject(() => Call(escape,"inspect_runbook")); count++;
        var cycle = Context(); cycle.Input["runbooks"]![0]!["steps"]![0]!["dependsOn"]!.AsArray().Add("digest"); await Reject(() => Call(cycle,"inspect_runbook")); count++;
        var changed = Context(); await Call(changed,"inspect_runbook"); changed.Input["runbooks"]![0]!["steps"]![0]!["content"] = "changed"; await Reject(() => Call(changed,"execute_runbook")); count++;
        var stale = Context(); await Call(stale,"inspect_runbook"); await Call(stale,"execute_runbook"); stale.Input["runbooks"]![0]!["steps"]![0]!["content"] = "changed after successful execution";
        await Reject(() => Call(stale,"write_execution_report")); Check(!File.Exists(stale.ResolvePath("runbook-execution.json"))); count++;
        await Call(stale,"inspect_runbook"); await Reject(() => Call(stale,"write_execution_report")); Check(!File.Exists(stale.ResolvePath("runbook-execution.json"))); var current = await Call(stale,"execute_runbook"); Check(!current["success"]!.GetValue<bool>()); Check(JsonNode.DeepEquals(current,await Call(stale,"write_execution_report"))); count++;
        var reinspect = Context(); await Call(reinspect,"inspect_runbook"); await Call(reinspect,"execute_runbook"); await Call(reinspect,"inspect_runbook"); await Reject(() => Call(reinspect,"write_execution_report")); count++;
        Console.WriteLine($"PASS 46-RunbookExecutionAssistant ({count} focused cases)"); return 0;
    }
}
