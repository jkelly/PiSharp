// PiSharp-only (no upstream Pi source): approved bounded source edit verification.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.ApiDeprecationMigration;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-51-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var passed = 0;
        var source = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
        SampleContext Context(bool approved = true) => new(Path.Combine(root, Guid.NewGuid().ToString("N")), (JsonObject)source.DeepClone(), approved);
        async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            await Check("approved migration preserves every unrelated character", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); var before = c.Input["files"]!["repo/Client.cs"]!.GetValue<string>();
                await Call(w, 0); await Call(w, 1); var r = await Call(w, 2); var after = await File.ReadAllTextAsync(c.ResolvePath("repo/Client.cs"));
                Assert(r["verified"]!.GetValue<bool>() && after == before.Replace("client.FetchLegacy(", "client.FetchAsync(", StringComparison.Ordinal), "Unrelated source changed.");
                Assert(File.Exists(c.ResolvePath("verification-client-v2.json")), "Missing verification artifact.");
            });
            await Check("callback and tool metadata enforce approval", async () =>
            {
                var c = Context(false); var w = Workflow.Create(c); await Call(w, 0); Assert(w.Tools[1].RequiresApproval, "Missing approval metadata."); await Reject(() => Call(w, 1));
                Assert(!File.Exists(c.ResolvePath("repo/Client.cs")), "Unapproved edit wrote source.");
            });
            await Check("apply is idempotent and post-edit tampering fails verification", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Call(w, 0); var first = (await Call(w, 1)).ToJsonString(); Assert(first == (await Call(w, 1)).ToJsonString(), "Repeat apply not idempotent.");
                await File.AppendAllTextAsync(c.ResolvePath("repo/Client.cs"), "// tamper\n"); Assert(!(await Call(w, 2))["verified"]!.GetValue<bool>(), "Tampering passed verification."); await Reject(() => Call(w, 1));
            });
            await Check("source changed after inspection is not overwritten", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Call(w, 0); Directory.CreateDirectory(c.ResolvePath("repo")); await File.WriteAllTextAsync(c.ResolvePath("repo/Client.cs"), "human change");
                await Reject(() => Call(w, 1)); Assert(await File.ReadAllTextAsync(c.ResolvePath("repo/Client.cs")) == "human change", "Human change overwritten.");
            });
            await Check("missing usage and invalid bounds rejected", async () =>
            {
                var c = Context(); c.Input["migrations"]![0]!["edits"]![0]!["expectedOccurrences"] = 2; await Reject(() => Call(Workflow.Create(c), 0));
                c.Input["migrations"]![0]!["edits"]![0]!["expectedOccurrences"] = 0; await Reject(() => Call(Workflow.Create(c), 0));
            });
            await Check("multiple files and IDs have independent plans", async () =>
            {
                var c = Context(); c.Input["files"]!["repo/Other.cs"] = "var value = client.FetchLegacy(7);\n";
                var other = (JsonObject)c.Input["migrations"]![0]!.DeepClone(); other["id"] = "other"; other["edits"]![0]!["path"] = "repo/Other.cs"; c.Input["migrations"]!.AsArray().Add(other);
                var w = Workflow.Create(c); await Call(w, 0); await Call(w, 0, "other"); await Call(w, 1); await Call(w, 1, "other"); Assert((await Call(w, 2, "other"))["verified"]!.GetValue<bool>(), "Other ID failed.");
            });
            await Check("path escape stage and stale-configuration guards", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Reject(() => Call(w, 1)); await Call(w, 0); c.Input["migrations"]![0]!["edits"]![0]!["replace"] = "different("; await Reject(() => Call(w, 1));
                c.Input["migrations"]![0]!["edits"]![0]!["path"] = "repo/../../escape.cs"; await Reject(() => Call(w, 0));
            });
            Console.WriteLine($"{passed} tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JsonObject> Call(WorkflowSpec w, int stage, string id = "client-v2") => (await w.Tools[stage].Execute(new() { ["migrationId"] = id }, default))!.AsObject();
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task<JsonObject>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
}
