// PiSharp-only (no upstream Pi source): date boundaries and evidence-grounded training tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.TrainingComplianceTracker;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var count = 0;
        SampleContext Context() => new(Path.Combine(Path.GetTempPath(),"pisharp-44-" + Guid.NewGuid().ToString("N")),(JsonObject)input.DeepClone());
        async Task<JsonNode> Call(SampleContext c,string name,string id = "E1") => (await Workflow.Create(c).Tools.Single(t => t.Name == name).Execute(new JsonObject { ["employeeId"] = id },default))!;
        static void Check(bool value) { if (!value) throw new Exception("Compliance assertion failed."); }
        static async Task Reject(Func<Task<JsonNode>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return; } throw new Exception("Expected rejection."); }
        var c = Context(); await Call(c,"inspect_training"); var assessment = await Call(c,"assess_compliance"); var courses = assessment["courses"]!.AsArray(); Check(courses[0]!["status"]!.GetValue<string>() == "expired" && courses[1]!["status"]!.GetValue<string>() == "due-soon" && courses[2]!["status"]!.GetValue<string>() == "missing-overdue"); count++;
        var reminders = await Call(c,"draft_reminders"); Check(reminders["reminders"]!.AsArray().Count == 3 && reminders["escalations"]!.AsArray().Count == 2 && reminders["escalations"]![0]!["managerId"]!.GetValue<string>() == "M1"); count++;
        var other = await Call(c,"assess_compliance","E2"); Check(other["courses"]![0]!["status"]!.GetValue<string>() == "current"); var otherDraft = await Call(c,"draft_reminders","E2"); Check(otherDraft["escalations"]!.AsArray().Count == 0 && otherDraft["employeeId"]!.GetValue<string>() == "E2"); count++;
        await Reject(() => Call(Context(),"draft_reminders")); await Reject(() => Call(Context(),"assess_compliance","missing")); count++;
        var missing = Context(); missing.Input.Remove("requirements"); await Reject(() => Call(missing,"assess_compliance")); count++;
        var future = Context(); future.Input["employees"]![0]!["completions"]![0]!["completedOn"] = "2027-01-01"; await Reject(() => Call(future,"assess_compliance")); count++;
        var boundary = Context(); boundary.Input["asOf"] = "2026-11-15"; Check((await Call(boundary,"assess_compliance"))["courses"]![1]!["daysUntilDue"]!.GetValue<int>() == 0 && (await Call(boundary,"assess_compliance"))["courses"]![1]!["status"]!.GetValue<string>() == "due-soon"); count++;
        var noManager = Context(); noManager.Input["employees"]![0]!["managerId"] = ""; await Call(noManager,"assess_compliance"); Check(!(await Call(noManager,"draft_reminders"))["escalations"]![0]!["deliveryReady"]!.GetValue<bool>()); count++;
        Console.WriteLine($"PASS 44-TrainingComplianceTracker ({count} focused cases)"); return 0;
    }
}
