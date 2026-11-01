// PiSharp-only (no upstream Pi source): access policy/expiry/exception and local removal tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.AccessReviewAssistant;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-access-" + Guid.NewGuid().ToString("N")); var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message) { var failed = false; try { action(); } catch (ArgumentException) { failed = true; } Check(failed, message); }
        async Task RejectAsync(Func<Task> action, string message) { var failed = false; try { await action(); } catch (ArgumentException) { failed = true; } catch (InvalidOperationException) { failed = true; } Check(failed, message); }
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var review = Workflow.Review(c, "emp-301"); var decisions = review["decisions"]!.AsArray().OfType<JsonObject>().ToArray();
            Check(review["recommendedRemovalCount"]!.GetValue<int>() == 2, "Outside-role and expired grants recommended for removal.");
            Check(decisions.Single(d => d.Text("grantId") == "g-admin").Text("reason") == "approved-exception", "Current approved exception retained.");
            Check(decisions.Single(d => d.Text("grantId") == "g-docs").Text("reason") == "grant-expired", "Exclusive expiry boundary applies even to allowed resource.");
            var run = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(run.Success && run.Report["outputs"]!.AsArray().Count == 3 && File.Exists(c.ResolvePath("access-review-emp-301.json")), "Native tools save review artifact.");
            Reject(() => Workflow.Review(c, "missing"), "Missing employee rejected.");
            var duplicate = (JsonObject)input.DeepClone(); duplicate["roles"]!.AsArray().Add(duplicate["roles"]![0]!.DeepClone());
            Reject(() => Workflow.Review(new SampleContext(Path.Combine(root, "duplicate"), duplicate), "emp-301"), "Ambiguous role policy rejected.");
            foreach (var collection in new[] { "employees", "roles", "exceptions-null", "exceptions-scalar" })
            {
                var malformed = (JsonObject)input.DeepClone();
                var records = collection.StartsWith("exceptions", StringComparison.Ordinal) ? malformed["employees"]![0]!["exceptions"]!.AsArray() : malformed[collection]!.AsArray();
                records.Add(collection is "employees" or "exceptions-scalar" ? JsonValue.Create("not-a-record") : null);
                var badContext = new SampleContext(Path.Combine(root, "malformed-" + collection), malformed, approved: true);
                Reject(() => Workflow.Review(badContext, "emp-301"), $"Malformed {collection} record rejected.");
                await RejectAsync(async () => { await Workflow.Remove(badContext, "emp-301"); }, "Malformed source blocks local removals.");
                Check(!File.Exists(badContext.ResolvePath("access-removals-emp-301.json")), "Malformed source creates no removal ledger.");
            }
            var expired = (JsonObject)input.DeepClone(); expired["employees"]![0]!["exceptions"]![0]!["validUntil"] = "2026-11-01";
            Check(Workflow.Review(new SampleContext(Path.Combine(root, "expired"), expired), "emp-301")["recommendedRemovalCount"]!.GetValue<int>() == 3, "Exception expiration cannot retain grant at boundary.");
            Check(Workflow.Review(c, "emp-302")["recommendedRemovalCount"]!.GetValue<int>() == 0, "Second employee has independent policy result.");
            await RejectAsync(async () => { await Workflow.Create(c).Tools.Single(t => t.Name == "remove_local_access").Execute(Workflow.Args("emp-301"), default); }, "Direct callback requires approval.");
            Check(!File.Exists(c.ResolvePath("access-removals-emp-301.json")), "Denied callback makes no local changes.");
            var approved = new SampleContext(root, input, approved: true); var receipt = await Workflow.Remove(approved, "emp-301");
            Check(receipt["removedGrantIds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "g-payroll", "g-docs" }), "Only recommended grants removed locally.");
            Check(Workflow.Review(approved, "emp-301")["recommendedRemovalCount"]!.GetValue<int>() == 0, "Subsequent review sees local removal state.");
            var replay = await Workflow.Remove(new SampleContext(root, (JsonObject)input.DeepClone(), approved: true), "emp-301");
            Check(JsonNode.DeepEquals(receipt, replay), "Cross-context removal replay is idempotent.");
            var changed = (JsonObject)input.DeepClone(); changed["asOf"] = "2026-11-02";
            await RejectAsync(async () => { await Workflow.Remove(new SampleContext(root, changed, approved: true), "emp-301"); }, "Changed review input cannot reuse removal ID.");
            var approvedContext = new SampleContext(Path.Combine(root, "approved-native"), input, approved: true);
            var approvedRun = await SampleHost.ExecuteAsync(Workflow.Create(approvedContext), approvedContext);
            Check(approvedRun.Success && approvedRun.Report["outputs"]!.AsArray().Count == 4, "Native approval executes local removal.");
            Console.WriteLine($"PASS AccessReviewAssistant: {checks} focused checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root, true); }
    }
}
