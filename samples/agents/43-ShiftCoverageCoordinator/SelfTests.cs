// PiSharp-only (no upstream Pi source): focused coverage, eligibility and approval tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.ShiftCoverageCoordinator;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var count = 0;
        SampleContext Context(bool approved = true) => new(Path.Combine(Path.GetTempPath(),"pisharp-43-" + Guid.NewGuid().ToString("N")),(JsonObject)input.DeepClone(),approved);
        async Task<JsonNode> Call(SampleContext c,string name,string id = "S1",string? employee = null)
        { var a = new JsonObject { ["shiftId"] = id }; if (employee is not null) a["employeeId"] = employee; return (await Workflow.Create(c).Tools.Single(t => t.Name == name).Execute(a,default))!; }
        static void Check(bool value) { if (!value) throw new Exception("Coverage assertion failed."); }
        static async Task Reject(Func<Task<JsonNode>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return; } throw new Exception("Expected rejection."); }
        var c = Context(); var ranking = await Call(c,"rank_replacements"); Check(ranking["recommendedEmployeeId"]!.GetValue<string>() == "E2" && ranking["excluded"]!.AsArray().Count == 3); count++;
        var assigned = await Call(c,"assign_replacement",employee:"E2"); var again = await Call(c,"assign_replacement",employee:"E2"); Check(!assigned["alreadyAssigned"]!.GetValue<bool>() && again["alreadyAssigned"]!.GetValue<bool>() && File.Exists(c.ResolvePath("roster-assignments.json"))); count++;
        await Reject(() => Call(c,"assign_replacement",employee:"E1")); await Reject(() => Call(c,"assign_replacement",employee:"E4")); count++;
        var no = Context(false); Check(Workflow.Create(no).Tools.Single(t => t.Name == "assign_replacement").RequiresApproval); await Reject(() => Call(no,"assign_replacement",employee:"E2")); Check(!File.Exists(no.ResolvePath("roster-assignments.json"))); count++;
        var second = await Call(c,"rank_replacements","S2"); Check(second["recommendedEmployeeId"]!.GetValue<string>() == "E1"); await Call(c,"assign_replacement","S2","E1"); count++;
        await Reject(() => Call(Context(),"inspect_shift","missing")); var missing = Context(); missing.Input.Remove("employees"); await Reject(() => Call(missing,"rank_replacements")); count++;
        var conflict = Context(); conflict.Input["shifts"]!.AsArray().Add(new JsonObject { ["id"] = "taken", ["start"] = "2026-11-02T08:00:00Z", ["end"] = "2026-11-02T10:00:00Z", ["qualification"] = "first-aid", ["employeeId"] = "E2" }); Check((await Call(conflict,"rank_replacements"))["ranked"]!.AsArray().Count == 0); await Reject(() => Call(conflict,"assign_replacement",employee:"E2")); count++;
        var restart = Context(); restart.Input["shifts"]!.AsArray().Add(new JsonObject { ["id"] = "S3", ["start"] = "2026-11-02T12:00:00Z", ["end"] = "2026-11-02T14:00:00Z", ["qualification"] = "first-aid", ["employeeId"] = "" });
        await Call(restart,"assign_replacement",employee:"E2");
        var resumed = new SampleContext(restart.Workspace,(JsonObject)restart.Input.DeepClone(),true);
        var resumedRanking = await Call(resumed,"rank_replacements","S3"); Check(!resumedRanking["ranked"]!.AsArray().Any(e => e!["employeeId"]!.GetValue<string>() == "E2")); await Reject(() => Call(resumed,"assign_replacement","S3","E2"));
        Check(JsonNode.Parse(await File.ReadAllTextAsync(restart.ResolvePath("roster-assignments.json")))!["assignments"]!["S1"]!.GetValue<string>() == "E2"); count++;
        Check((await Call(resumed,"assign_replacement",employee:"E2"))["alreadyAssigned"]!.GetValue<bool>()); await Call(resumed,"assign_replacement","S2","E1");
        var preserved = JsonNode.Parse(await File.ReadAllTextAsync(restart.ResolvePath("roster-assignments.json")))!["assignments"]!.AsObject(); Check(preserved.Count == 2 && preserved["S1"]!.GetValue<string>() == "E2" && preserved["S2"]!.GetValue<string>() == "E1"); count++;
        var changedSource = new SampleContext(restart.Workspace,(JsonObject)restart.Input.DeepClone(),true); changedSource.Input["shifts"]![0]!["end"] = "2026-11-02T16:00:00Z"; await Reject(() => Call(changedSource,"rank_replacements")); count++;
        var ledger = JsonNode.Parse(await File.ReadAllTextAsync(restart.ResolvePath("roster-assignments.json")))!.AsObject(); ledger["assignments"]!["S1"] = "unknown"; await File.WriteAllTextAsync(restart.ResolvePath("roster-assignments.json"),ledger.ToJsonString()); await Reject(() => Call(resumed,"rank_replacements")); count++;
        Console.WriteLine($"PASS 43-ShiftCoverageCoordinator ({count} focused cases)"); return 0;
    }
}
