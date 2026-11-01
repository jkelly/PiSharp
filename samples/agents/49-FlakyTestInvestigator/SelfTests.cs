// PiSharp-only (no upstream Pi source): deterministic repeated-run analysis tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.FlakyTestInvestigator;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-49-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var passed = 0;
        var source = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
        SampleContext Context() => new(Path.Combine(root, Guid.NewGuid().ToString("N")), (JsonObject)source.DeepClone());
        async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            await Check("mixed outcomes correlate with order and produce proposal", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); var result = await Call(w, 0);
                Assert(result.Text("classification") == "flaky" && result["correlations"]!.AsArray().Count == 1, "Incorrect correlation.");
                result = await Call(w, 1); Assert(result["executedTests"]!.GetValue<bool>() == false && result["applied"]!.GetValue<bool>() == false, "False execution claim."); await Call(w, 2);
                Assert(File.Exists(c.ResolvePath("investigation-cache-isolation.json")), "Missing artifact.");
            });
            await Check("all failures are consistently broken", async () =>
            {
                var c = Context(); foreach (var r in c.Input["tests"]![0]!["runs"]!.AsArray()) r!["outcome"] = "failed";
                var w = Workflow.Create(c); Assert((await Call(w, 0)).Text("classification") == "consistently-broken", "All fail is not flaky.");
            });
            await Check("too few and missing outcomes are not trusted", async () =>
            {
                var c = Context(); var runs = c.Input["tests"]![0]!["runs"]!.AsArray(); while (runs.Count > 1) runs.RemoveAt(1);
                var w = Workflow.Create(c); Assert((await Call(w, 0)).Text("classification") == "insufficient-evidence", "Need repeats."); runs[0]!.AsObject().Remove("outcome"); await Reject(() => Call(w, 0));
            });
            await Check("distinct tests and repeats do not reuse results", async () =>
            {
                var c = Context(); var second = (JsonObject)c.Input["tests"]![0]!.DeepClone(); second["id"] = "stable";
                foreach (var r in second["runs"]!.AsArray()) r!["outcome"] = "passed"; c.Input["tests"]!.AsArray().Add(second);
                var w = Workflow.Create(c); await Call(w, 0); Assert((await Call(w, 0, "stable")).Text("classification") == "stable-in-observed-runs", "Leaked state."); Assert((await Call(w, 0)).Text("classification") == "flaky", "Repeat changed result.");
            });
            await Check("changed evidence requires reanalysis", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Call(w, 0); c.Input["tests"]![0]!["runs"]![0]!["outcome"] = "failed"; await Reject(() => Call(w, 1));
            });
            await Check("duplicate evidence and stage guards", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Reject(() => Call(w, 2)); c.Input["tests"]![0]!["runs"]![1]!["runId"] = "r1"; await Reject(() => Call(w, 0)); await Reject(() => Call(w, 0, "missing"));
            });
            Console.WriteLine($"{passed} tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JsonObject> Call(WorkflowSpec w, int stage, string id = "cache-isolation") => (await w.Tools[stage].Execute(new() { ["testId"] = id }, default))!.AsObject();
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task<JsonObject>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
}
