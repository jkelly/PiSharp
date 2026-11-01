// PiSharp-only (no upstream Pi source): role-specific prerequisite and artifact tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.EmployeeOnboardingCoordinator;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-onboarding-" + Guid.NewGuid().ToString("N")); var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message) { var failed = false; try { action(); } catch (ArgumentException) { failed = true; } Check(failed, message); }
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var plan = Workflow.Plan(c, "emp-101"); var items = plan["checklist"]!.AsArray().OfType<JsonObject>().ToArray();
            Check(items.Select(x => x.Text("taskId")).SequenceEqual(new[] { "paperwork", "account", "training", "repo" }), "Prerequisite topological order.");
            Check(items[1].Text("status") == "ready" && items[2].Text("status") == "blocked", "Readiness follows completed prerequisites only.");
            Check(items[3].Text("ownerId") == "mgr-eng" && items[3].Text("dueDate") == "2026-11-11", "Role assignment and relative deadline.");
            var run = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(run.Success && run.Report["outputs"]!.AsArray().Count == 3 && File.Exists(c.ResolvePath("onboarding-emp-101.json")), "Native tools persist plan.");
            Reject(() => Workflow.Plan(c, "missing"), "Missing employee rejected.");
            var duplicate = (JsonObject)input.DeepClone(); duplicate["employees"]!.AsArray().Add(duplicate["employees"]![0]!.DeepClone());
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "duplicate"), duplicate), "emp-101"), "Ambiguous employee rejected.");
            foreach (var collection in new[] { "employees", "roles", "owners" })
            {
                var malformed = (JsonObject)input.DeepClone(); malformed[collection]!.AsArray().Add(collection == "employees" ? JsonValue.Create("not-a-record") : null);
                Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "malformed-" + collection), malformed), "emp-101"), $"Malformed {collection} records cannot be silently ignored.");
            }
            var cycle = (JsonObject)input.DeepClone(); cycle["commonTasks"]![0]!["requires"]!.AsArray().Add("repo");
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "cycle"), cycle), "emp-101"), "Cyclic prerequisite rejected.");
            var unknown = (JsonObject)input.DeepClone(); unknown["roles"]![0]!["tasks"]![0]!["requires"]!.AsArray().Add("missing-task");
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "unknown"), unknown), "emp-101"), "Missing prerequisite rejected.");
            var other = Workflow.Plan(c, "emp-102");
            Check(other["checklist"]!.AsArray().Last()!["taskId"]!.GetValue<string>() == "crm" && other["startDate"]!.GetValue<string>() == "2026-11-16", "Second employee has different role and dates.");
            var rerun = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(rerun.Success && JsonNode.DeepEquals(plan, JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath("onboarding-emp-101.json")))), "Repeat creates same plan.");
            Console.WriteLine($"PASS EmployeeOnboardingCoordinator: {checks} focused checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root, true); }
    }
}
