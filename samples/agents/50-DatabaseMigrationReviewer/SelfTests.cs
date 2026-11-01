// PiSharp-only (no upstream Pi source): in-memory migration review verification.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.DatabaseMigrationReviewer;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-50-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var passed = 0;
        var source = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
        SampleContext Context() => new(Path.Combine(root, Guid.NewGuid().ToString("N")), (JsonObject)source.DeepClone());
        async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            await Check("rename roundtrip restores schema and values without input mutation", async () =>
            {
                var c = Context(); var before = c.Input.ToJsonString(); var w = Workflow.Create(c); await Call(w, 0); var r = await Call(w, 1);
                Assert(r.Text("status") == "ready-for-database-validation" && r["rollbackRestoresOriginal"]!.GetValue<bool>(), "Roundtrip failed.");
                Assert(before == c.Input.ToJsonString() && r["realDatabaseExecuted"]!.GetValue<bool>() == false, "Simulation mutated input or claimed DB execution."); await Call(w, 2);
                Assert(File.Exists(c.ResolvePath("migration-rename-display.json")), "Report missing.");
            });
            await Check("drop and recreate loses row data and is blocked", async () =>
            {
                var c = Context(); c.Input["migrations"]![0]!["operations"] = JsonNode.Parse("[{\"kind\":\"dropColumn\",\"table\":\"users\",\"column\":\"display\"}]");
                c.Input["migrations"]![0]!["rollback"] = JsonNode.Parse("[{\"kind\":\"addColumn\",\"table\":\"users\",\"column\":\"display\",\"nullable\":false,\"default\":\"unknown\"}]");
                var w = Workflow.Create(c); await Call(w, 0); var r = await Call(w, 1);
                Assert(r.Text("status") == "blocked" && !r["rollbackRestoresOriginal"]!.GetValue<bool>() && r["reviewFindings"]!.ToJsonString().Contains("2 non-null"), "Data loss must be visible.");
            });
            await Check("missing rollback blocks even a valid forward migration", async () =>
            {
                var c = Context(); c.Input["migrations"]![0]!.AsObject().Remove("rollback"); var w = Workflow.Create(c); await Call(w, 0);
                Assert((await Call(w, 1)).Text("status") == "blocked", "Missing rollback passed.");
            });
            await Check("unsupported operations and non-null additions are blocked", async () =>
            {
                var c = Context(); c.Input["migrations"]![0]!["operations"] = JsonNode.Parse("[{\"kind\":\"addColumn\",\"table\":\"users\",\"column\":\"email\",\"nullable\":false},{\"kind\":\"executeSql\",\"table\":\"users\",\"column\":\"id\"}]");
                var w = Workflow.Create(c); var r = await Call(w, 0); Assert(r["findings"]!.AsArray().Count == 2, "Expected constraint and unsupported-operation findings.");
            });
            await Check("repeat and distinct migration IDs isolate state", async () =>
            {
                var c = Context(); var second = (JsonObject)c.Input["migrations"]![0]!.DeepClone(); second["id"] = "other"; second.Remove("rollback"); c.Input["migrations"]!.AsArray().Add(second);
                var w = Workflow.Create(c); var first = (await Call(w, 0)).ToJsonString(); await Call(w, 0, "other");
                Assert((await Call(w, 1, "other")).Text("status") == "blocked", "Other ID reused result."); Assert(first == (await Call(w, 0)).ToJsonString(), "Repeated simulation drifted.");
            });
            await Check("stage invalid-model and stale-evidence guards", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Reject(() => Call(w, 2)); await Call(w, 0);
                c.Input["database"]!["tables"]!["users"]!["rows"]![0]!["display"] = "Changed"; await Reject(() => Call(w, 1));
                c.Input.Remove("database"); await Reject(() => Call(w, 0)); await Reject(() => Call(w, 0, "missing"));
            });
            Console.WriteLine($"{passed} tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JsonObject> Call(WorkflowSpec w, int stage, string id = "rename-display") => (await w.Tools[stage].Execute(new() { ["migrationId"] = id }, default))!.AsObject();
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task<JsonObject>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
}
