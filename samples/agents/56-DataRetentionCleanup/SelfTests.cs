// PiSharp-only (no upstream Pi source): retention thresholds, exact selection and legal-hold regressions.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.DataRetentionCleanup;

public static class SelfTests
{
    private static readonly string[] Ids = ["logs", "exports"];
    private static readonly string[] Keys = ["logs:log-old", "exports:export-old"];
    public static async Task<int> RunAsync(string fixture)
    {
        var tests = new (string, Func<SampleContext, WorkflowSpec, Task>)[]
        {
            ("two datasets, exact cleanup, holds and unknown policies", async (c, w) =>
            {
                var inspect = await Call(w, "inspect_retention", Workflow.Args(Ids));
                Check(inspect!.AsArray().Count(n => n!["Status"]!.GetValue<string>() == "eligible") == 2, "Wrong expiry candidates.");
                var args = Workflow.Args(Ids, Keys); await Call(w, "plan_retention_cleanup", args); var result = await Call(w, "execute_retention_cleanup", args);
                Check(result!["removedCount"]!.GetValue<int>() == 2 && result["remainingCount"]!.GetValue<int>() == 3, "Wrong records removed.");
                var store = await File.ReadAllTextAsync(c.ResolvePath("retention-state.json"));
                Check(!store.Contains("Expired diagnostic fixture") && !store.Contains("Expired export fixture") && store.Contains("Preserve under legal hold"), "Payload cleanup/hold preservation failed.");
            }),
            ("replay is idempotent and restart sees retained state", async (c, w) =>
            {
                var args = Workflow.Args(Ids, Keys); await Call(w, "plan_retention_cleanup", args);
                var first = await Call(w, "execute_retention_cleanup", args); var second = await Call(w, "execute_retention_cleanup", args);
                Check(JsonNode.DeepEquals(first, second), "Replay differed.");
                var restart = Workflow.Create(new SampleContext(c.Workspace, c.Input, true)); var result = await Call(restart, "inspect_retention", Workflow.Args(Ids));
                Check(result!.AsArray().Count == 3 && result.AsArray().All(n => n!["Status"]!.GetValue<string>() != "eligible"), "Restart ignored cleanup.");
                await Call(restart, "plan_retention_cleanup", args);
                Check((await Call(restart, "execute_retention_cleanup", args))!["removedCount"]!.GetValue<int>() == 0, "Restart removed a key twice.");
            }),
            ("held and unapproved selections refused", async (c, w) =>
            {
                await Throws(() => Call(w, "plan_retention_cleanup", Workflow.Args(Ids, ["logs:log-new"])));
                c.Input["approvedRecordKeys"]!.AsArray().Add("logs:log-held");
                await Throws(() => Call(w, "plan_retention_cleanup", Workflow.Args(Ids, ["logs:log-held"])));
                Check(!File.Exists(c.ResolvePath("retention-state.json")), "Rejected selection removed data.");
            }),
            ("missing and duplicate keys refused", async (_, w) =>
            {
                await Throws(() => Call(w, "inspect_retention", Workflow.Args(["missing"])));
                await Throws(() => Call(w, "plan_retention_cleanup", Workflow.Args(Ids, [Keys[0], Keys[0]])));
                await Throws(() => Call(w, "plan_retention_cleanup", Workflow.Args(Ids, ["logs:missing"])));
            }),
            ("threshold is inclusive and invalid date preserved", async (c, w) =>
            {
                c.Input["datasets"]![0]!["records"]![0]!["createdOn"] = "2026-10-02";
                c.Input["datasets"]![0]!["records"]![2]!["createdOn"] = "not-a-date";
                var result = await Call(w, "inspect_retention", Workflow.Args(["logs"]));
                Check(result![0]!["Status"]!.GetValue<string>() == "eligible" && result[2]!["Status"]!.GetValue<string>() == "invalid-created-date", "Threshold or invalid date handling incorrect.");
            }),
            ("missing legal hold status never defaults to eligible", async (c, w) =>
            {
                c.Input["datasets"]![0]!["records"]![0]!.AsObject().Remove("legalHold");
                var result = await Call(w, "inspect_retention", Workflow.Args(["logs"]));
                Check(result![0]!["Status"]!.GetValue<string>() == "unknown-hold-status", "Missing hold treated as false.");
                await Throws(() => Call(w, "plan_retention_cleanup", Workflow.Args(Ids, Keys)));
            }),
            ("missing retention interval never defaults to zero days", async (c, w) =>
            {
                c.Input["policies"]![0]!.AsObject().Remove("retainDays");
                c.Input["datasets"]![0]!["records"]![0]!["createdOn"] = "2026-10-31";
                var originalRecords = c.Input["datasets"]!.DeepClone();
                await Throws(() => Call(w, "inspect_retention", Workflow.Args(Ids)));
                await Throws(() => Call(w, "plan_retention_cleanup", Workflow.Args(Ids, Keys)));
                Check(!File.Exists(c.ResolvePath("retention-state.json")) && JsonNode.DeepEquals(originalRecords, c.Input["datasets"]), "Missing interval caused cleanup/data loss.");
            }),
            ("new legal hold invalidates plan and approval is mandatory", async (c, w) =>
            {
                var args = Workflow.Args(Ids, Keys); await Call(w, "plan_retention_cleanup", args);
                c.Input["datasets"]![0]!["records"]![0]!["legalHold"] = true;
                await Throws(() => Call(w, "execute_retention_cleanup", args));
                var no = Workflow.Create(new SampleContext(c.Workspace, c.Input));
                Check(no.Tools.Single(t => t.Name == "execute_retention_cleanup").RequiresApproval, "Missing host gate.");
                await Throws(() => Call(no, "execute_retention_cleanup", args)); Check(!File.Exists(c.ResolvePath("retention-state.json")), "Guard wrote state.");
            })
        };
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-56-" + Guid.NewGuid().ToString("N"));
            try { var c = new SampleContext(root, JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), true); await test(c, Workflow.Create(c)); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} retention checks passed."); return failures == 0 ? 0 : 1;
    }
    private static Task<JsonNode?> Call(WorkflowSpec w, string name, JsonObject args) => w.Tools.Single(t => t.Name == name).Execute(args, default);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task<JsonNode?>> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Expected rejection."); }
}
