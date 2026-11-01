// PiSharp-only (no upstream Pi source): ordered offboarding and approval/replay tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.EmployeeOffboardingCoordinator;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-offboarding-" + Guid.NewGuid().ToString("N")); var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message) { var failed = false; try { action(); } catch (ArgumentException) { failed = true; } Check(failed, message); }
        async Task RejectAsync(Func<Task> action, string message) { var failed = false; try { await action(); } catch (ArgumentException) { failed = true; } catch (InvalidOperationException) { failed = true; } Check(failed, message); }
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var plan = Workflow.Plan(c, "emp-201"); var steps = plan["steps"]!.AsArray();
            Check(steps.Select(s => s!["phase"]!.GetValue<int>()).SequenceEqual(new[] { 1, 1, 2, 3 }), "Access, equipment, handoff phase order.");
            Check(steps[2]!["status"]!.GetValue<string>() == "blocked-by-access", "Equipment return sequencing follows access.");
            Check(plan["pendingObligations"]!.AsArray().Count == 1 && plan["pendingObligations"]![0]!["daysAfterDeparture"]!.GetValue<int>() == 7, "Pending financial obligation retained with deadline.");
            var run = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(run.Success && run.Report["outputs"]!.AsArray().Count == 3 && File.Exists(c.ResolvePath("offboarding-emp-201.json")), "Native tools persist plan.");
            Reject(() => Workflow.Plan(c, "unknown"), "Missing employee rejected.");
            var duplicate = (JsonObject)input.DeepClone(); duplicate["employees"]!.AsArray().Add(duplicate["employees"]![0]!.DeepClone());
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "duplicate"), duplicate), "emp-201"), "Ambiguous employee rejected.");
            foreach (var collection in new[] { "employees", "access" })
            {
                var malformed = (JsonObject)input.DeepClone();
                var records = collection == "employees" ? malformed[collection]!.AsArray() : malformed["employees"]![0]![collection]!.AsArray();
                records.Add(collection == "employees" ? null : JsonValue.Create("not-a-record"));
                var badContext = new SampleContext(Path.Combine(root, "malformed-" + collection), malformed, approved: true);
                Reject(() => Workflow.Plan(badContext, "emp-201"), $"Malformed {collection} record rejected.");
                await RejectAsync(async () => { await Workflow.Revoke(badContext, "emp-201"); }, "Malformed source blocks local revocation.");
                Check(!File.Exists(badContext.ResolvePath("revocations-emp-201.json")), "Malformed source creates no revocation ledger.");
            }
            Check(Workflow.Plan(c, "emp-202")["complete"]!.GetValue<bool>(), "Second completed departure is calculated independently.");
            await RejectAsync(async () => { await Workflow.Create(c).Tools.Single(t => t.Name == "revoke_local_access").Execute(Workflow.Args("emp-201"), default); }, "Direct callback blocked without approval.");
            Check(!File.Exists(c.ResolvePath("revocations-emp-201.json")), "Denied mutation leaves ledger absent.");
            var approved = new SampleContext(root, input, approved: true); var receipt = await Workflow.Revoke(approved, "emp-201");
            Check(receipt["revokedAccessIds"]!.AsArray().Count == 2, "Local access revocations persisted.");
            Check(!(await Workflow.Revoke(approved, "emp-202"))["manualObligationsRemain"]!.GetValue<bool>(), "Completed departure has no invented manual obligations.");
            var after = Workflow.Plan(approved, "emp-201");
            Check(after["steps"]![2]!["status"]!.GetValue<string>() == "awaiting-return" && !after["complete"]!.GetValue<bool>() && after["pendingObligations"]!.AsArray().Count == 1, "Access revocation never marks equipment, handoffs or obligations complete.");
            var replay = await Workflow.Revoke(new SampleContext(root, (JsonObject)input.DeepClone(), approved: true), "emp-201");
            Check(JsonNode.DeepEquals(receipt, replay) && Directory.GetFiles(root, "revocations-emp-201.json").Length == 1, "Cross-context replay is idempotent.");
            var changed = (JsonObject)input.DeepClone(); changed["employees"]![0]!["access"]!.AsArray().Add(new JsonObject { ["id"] = "extra", ["active"] = true });
            await RejectAsync(async () => { await Workflow.Revoke(new SampleContext(root, changed, approved: true), "emp-201"); }, "Changed-input replay rejected.");
            var approvedContext = new SampleContext(Path.Combine(root, "approved-native"), input, approved: true);
            var approvedRun = await SampleHost.ExecuteAsync(Workflow.Create(approvedContext), approvedContext);
            Check(approvedRun.Success && approvedRun.Report["outputs"]!.AsArray().Count == 4, "Native approval path executes all four stages.");
            Console.WriteLine($"PASS EmployeeOffboardingCoordinator: {checks} focused checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root, true); }
    }
}
