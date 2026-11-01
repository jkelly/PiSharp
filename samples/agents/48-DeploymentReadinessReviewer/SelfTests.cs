// PiSharp-only (no upstream Pi source): deterministic deployment gate tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.DeploymentReadinessReviewer;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-48-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var passed = 0;
        var source = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
        SampleContext Context() => new(Path.Combine(root, Guid.NewGuid().ToString("N")), (JsonObject)source.DeepClone());
        async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            await Check("nominal all gates and persisted report", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Call(w, 0); var result = await Call(w, 1);
                Assert(result.Text("status") == "ready", "Expected ready."); await Call(w, 2);
                Assert(File.Exists(c.ResolvePath("readiness-release-42.json")), "Missing report.");
            });
            await Check("missing test result blocks", async () =>
            {
                var c = Context(); c.Input["releases"]![0]!["tests"]!.AsArray().RemoveAt(1);
                var w = Workflow.Create(c); await Call(w, 0); Assert((await Call(w, 1)).Text("status") == "blocked", "Missing tests cannot pass.");
            });
            await Check("failed test and absent migration inventory block", async () =>
            {
                var c = Context(); c.Input["releases"]![0]!["tests"]![0]!["status"] = "failed"; c.Input["releases"]![0]!.AsObject().Remove("migrations");
                var w = Workflow.Create(c); await Call(w, 0); Assert((await Call(w, 1))["blockingGateCount"]!.GetValue<int>() >= 2, "Expected independent blockers.");
            });
            await Check("two release identities never share state", async () =>
            {
                var c = Context(); var other = (JsonObject)c.Input["releases"]![0]!.DeepClone(); other["id"] = "release-43"; other["rollback"]!["rehearsed"] = false; c.Input["releases"]!.AsArray().Add(other);
                var w = Workflow.Create(c); await Call(w, 0); await Call(w, 0, "release-43");
                Assert((await Call(w, 1)).Text("status") == "ready" && (await Call(w, 1, "release-43")).Text("status") == "blocked", "Cross-release state.");
            });
            await Check("stale test and rollback evidence rejected", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Call(w, 0); c.Input["releases"]![0]!["tests"]![0]!["status"] = "failed";
                await Reject(() => Call(w, 1)); await Call(w, 0); await Call(w, 1); c.Input["releases"]![0]!["rollback"]!["rehearsed"] = false; await Reject(() => Call(w, 2));
            });
            await Check("stage and invalid identity guards", async () =>
            {
                var w = Workflow.Create(Context()); await Reject(() => Call(w, 2)); await Reject(() => Call(w, 0, "../escape")); await Reject(() => Call(w, 0, "missing"));
            });
            await Check("malformed migration evidence cannot be ready", async () =>
            {
                var c = Context(); c.Input["releases"]![0]!["migrations"]!.AsArray().Add("invalid");
                var w = Workflow.Create(c); await Call(w, 0); Assert((await Call(w, 1)).Text("status") == "blocked", "Malformed evidence silently ignored.");
            });
            Console.WriteLine($"{passed} tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JsonObject> Call(WorkflowSpec w, int stage, string id = "release-42") => (await w.Tools[stage].Execute(new() { ["releaseId"] = id }, default))!.AsObject();
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task<JsonObject>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
}
