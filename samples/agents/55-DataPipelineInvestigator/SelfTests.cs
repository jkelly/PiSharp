// PiSharp-only (no upstream Pi source): deterministic schema, remediation and replay regressions.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.DataPipelineInvestigator;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var tests = new (string, Func<SampleContext, WorkflowSpec, Task>)[]
        {
            ("trace, repair, quarantine and rerun two pipelines", async (c, w) =>
            {
                var args = Workflow.Args("daily-sales", "refunds"); var trace = await Call(w, "trace_pipeline", args);
                Check(trace![0]!["validate"]!["status"]!.GetValue<string>() == "failed" && trace[0]!["aggregate"]!["status"]!.GetValue<string>() == "blocked", "Failure stage not traced.");
                await Call(w, "propose_pipeline_remediation", args); var result = await Call(w, "apply_remediation_and_rerun", args);
                Check(result!["changesApplied"]!.GetValue<int>() == 3 && result["quarantinedRows"]!.GetValue<int>() == 1, "Wrong remediation count.");
                Check(result["traces"]![0]!["aggregate"]!["totalCents"]!.GetValue<long>() == 3700 && result["traces"]![1]!["aggregate"]!["totalCents"]!.GetValue<long>() == 400, "Aggregates were not recomputed.");
                Check(File.Exists(c.ResolvePath("pipeline-rerun.json")), "Missing rerun report.");
            }),
            ("repeated remediation and restart retain results", async (c, w) =>
            {
                var args = Workflow.Args("daily-sales", "refunds"); await Call(w, "propose_pipeline_remediation", args);
                var first = await Call(w, "apply_remediation_and_rerun", args); var second = await Call(w, "apply_remediation_and_rerun", args);
                Check(JsonNode.DeepEquals(first, second), "Replay duplicated quarantine.");
                var restart = Workflow.Create(new SampleContext(c.Workspace, c.Input, true)); var trace = await Call(restart, "trace_pipeline", args);
                Check(trace![0]!["validate"]!["status"]!.GetValue<string>() == "passed", "Restart lost repairs.");
            }),
            ("missing pipeline and duplicate row identity rejected", async (c, w) =>
            {
                await Throws(() => Call(w, "trace_pipeline", Workflow.Args("absent")));
                c.Input["pipelines"]![0]!["rows"]![1]!["id"] = "sale-1";
                await Throws(() => Call(w, "trace_pipeline", Workflow.Args("daily-sales")));
            }),
            ("missing remedy blocks batch and preserves data", async (c, w) =>
            {
                c.Input["remedies"] = new JsonArray(); var args = Workflow.Args("daily-sales");
                var plan = await Call(w, "propose_pipeline_remediation", args);
                Check(plan!["Unresolved"]!.AsArray().Count == 2, "Missing correction was invented.");
                await Throws(() => Call(w, "apply_remediation_and_rerun", args)); Check(!File.Exists(c.ResolvePath("pipeline-state.json")), "Unresolved data committed.");
            }),
            ("invalid correction remains unresolved", async (c, w) =>
            {
                c.Input["remedies"]![0]!["values"]!["amountCents"] = -1;
                var plan = await Call(w, "propose_pipeline_remediation", Workflow.Args("daily-sales"));
                Check(plan!["Unresolved"]!.AsArray().Count == 1, "Invalid correction accepted.");
            }),
            ("freshness and direct approval guard", async (c, w) =>
            {
                var args = Workflow.Args("daily-sales"); await Call(w, "propose_pipeline_remediation", args);
                c.Input["pipelines"]![0]!["rows"]![0]!["amountCents"] = 2600;
                await Throws(() => Call(w, "apply_remediation_and_rerun", args));
                var no = Workflow.Create(new SampleContext(c.Workspace, c.Input));
                Check(no.Tools.Single(t => t.Name == "apply_remediation_and_rerun").RequiresApproval, "Missing host gate.");
                await Throws(() => Call(no, "apply_remediation_and_rerun", args)); Check(!File.Exists(c.ResolvePath("pipeline-state.json")), "Guard wrote state.");
            })
        };
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-55-" + Guid.NewGuid().ToString("N"));
            try { var c = new SampleContext(root, JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), true); await test(c, Workflow.Create(c)); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} pipeline checks passed."); return failures == 0 ? 0 : 1;
    }
    private static Task<JsonNode?> Call(WorkflowSpec w, string name, JsonObject args) => w.Tools.Single(t => t.Name == name).Execute(args, default);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task<JsonNode?>> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Expected rejection."); }
}
