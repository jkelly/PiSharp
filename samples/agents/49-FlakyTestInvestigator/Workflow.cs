// PiSharp-only (no upstream Pi source): repeated-evidence test investigation.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.FlakyTestInvestigator;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"testId\":{\"type\":\"string\"}}", "testId");
        JsonObject Test(JsonObject a)
        {
            var id = a.Text("testId");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ArgumentException("Invalid test ID.");
            var tests = (c.Input["tests"] as JsonArray ?? throw new ArgumentException("tests required.")).OfType<JsonObject>().Where(t => t.Text("id") == id).ToArray();
            return tests.Length == 1 ? tests[0] : throw new ArgumentException("Test must exist uniquely.");
        }
        return new("flaky-test-investigator", "Flaky test investigator",
            "Analyze each requested test with analyze_runs, draft_repair, then write_investigation. Use existing repeated-run evidence. No tests are executed and proposals are not applied.",
            [new("analyze_runs", "Classify supplied repeated runs and identify outcome correlations with seed, order and worker.", schema, (a, ct) =>
            {
                var t = Test(a); var runs = t["runs"] as JsonArray ?? throw new ArgumentException("runs required.");
                if (runs.OfType<JsonObject>().Count() != runs.Count || runs.OfType<JsonObject>().Any(r => r.Text("outcome") is not ("passed" or "failed"))) throw new ArgumentException("Every run needs passed or failed outcome.");
                var ids = runs.OfType<JsonObject>().Select(r => r.Text("runId")).ToArray();
                if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("Run IDs must be unique and nonempty.");
                var failures = runs.OfType<JsonObject>().Count(r => r.Text("outcome") == "failed");
                var status = runs.Count < 3 ? "insufficient-evidence" : failures == 0 ? "stable-in-observed-runs" : failures == runs.Count ? "consistently-broken" : "flaky";
                var correlations = new JsonArray();
                if (status == "flaky") foreach (var dimension in new[] { "seed", "order", "worker" })
                {
                    if (runs.OfType<JsonObject>().Any(r => string.IsNullOrWhiteSpace(r.Text(dimension)))) continue;
                    foreach (var group in runs.OfType<JsonObject>().GroupBy(r => r.Text(dimension)))
                    {
                        var failed = group.Count(r => r.Text("outcome") == "failed");
                        var outside = runs.OfType<JsonObject>().Where(r => r.Text(dimension) != group.Key).ToArray();
                        if (group.Count() >= 2 && failed == group.Count() && outside.Length >= 2 && outside.All(r => r.Text("outcome") == "passed"))
                            correlations.Add(new JsonObject { ["dimension"] = dimension, ["value"] = group.Key, ["failedRuns"] = failed, ["comparisonPassedRuns"] = outside.Length });
                    }
                }
                var result = new JsonObject { ["testId"] = a.Text("testId"), ["classification"] = status, ["runs"] = runs.Count, ["failures"] = failures, ["correlations"] = correlations, ["executedTests"] = false };
                c.State[a.Text("testId") + "-source"] = t.ToJsonString();
                c.State[a.Text("testId")] = result.DeepClone(); return Task.FromResult<JsonNode?>(result);
            }), new("draft_repair", "Create a bounded evidence-backed repair proposal and validation plan; correlation is not proof of cause.", schema, (a, ct) =>
            {
                if (c.State[a.Text("testId") + "-source"]?.GetValue<string>() != Test(a).ToJsonString()) throw new InvalidOperationException("Run evidence changed; analyze again.");
                var result = c.State[a.Text("testId")]?.AsObject() ?? throw new InvalidOperationException("Analyze this test first.");
                var status = result.Text("classification"); var correlations = result["correlations"]!.AsArray();
                result["proposal"] = status switch
                {
                    "consistently-broken" => "Investigate the deterministic assertion failure before considering flaky-test remedies; do not add retries.",
                    "flaky" when correlations.Count > 0 => "Inspect setup/teardown and shared state for the correlated dimensions. Make one isolated fixture-state change, then repeat both failing and passing configurations.",
                    "flaky" => "Capture timing and isolation evidence for at most 20 repeats of this test before proposing a code change; no discriminating correlation is established.",
                    "insufficient-evidence" => "Collect at least three distinct run outcomes before classifying this test.",
                    _ => "No repair justified by the observed passing runs. Preserve coverage and collect new failure evidence if it recurs."
                };
                result["validationRunLimit"] = 20; result["applied"] = false;
                return Task.FromResult<JsonNode?>(result.DeepClone());
            }), new("write_investigation", "Save the analysis and unexecuted repair proposal to a local artifact.", schema, async (a, ct) =>
            {
                if (c.State[a.Text("testId") + "-source"]?.GetValue<string>() != Test(a).ToJsonString()) throw new InvalidOperationException("Run evidence changed; analyze again.");
                var result = c.State[a.Text("testId")]?.AsObject();
                if (result?["proposal"] is null) throw new InvalidOperationException("Draft the repair first.");
                var path = "investigation-" + a.Text("testId") + ".json"; await c.WriteJsonAsync(path, result, ct);
                return new JsonObject { ["artifact"] = path, ["classification"] = result.Text("classification") };
            })], [new("analyze_runs", new() { ["testId"] = "cache-isolation" }), new("draft_repair", new() { ["testId"] = "cache-isolation" }), new("write_investigation", new() { ["testId"] = "cache-isolation" })]);
    }
}
