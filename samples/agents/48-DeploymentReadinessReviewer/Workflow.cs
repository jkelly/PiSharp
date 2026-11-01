// PiSharp-only (no upstream Pi source): evidence-based deployment review.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.DeploymentReadinessReviewer;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"releaseId\":{\"type\":\"string\"}}", "releaseId");
        JsonObject Release(JsonObject a)
        {
            var id = a.Text("releaseId");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ArgumentException("Invalid release ID.");
            var matches = (c.Input["releases"] as JsonArray ?? throw new ArgumentException("releases required.")).OfType<JsonObject>().Where(x => x.Text("id") == id).ToArray();
            return matches.Length == 1 ? matches[0] : throw new ArgumentException("Release ID must identify exactly one release.");
        }
        return new("deployment-readiness-reviewer", "Deployment readiness reviewer",
            "For each requested release, inspect_release, evaluate_gates, then write_readiness_report. Missing checks block readiness. This reviews supplied evidence only; no deployment or test execution occurs.",
            [new("inspect_release", "Inspect required tests, migration rollback evidence, required configuration and release rollback rehearsal.", schema, (a, ct) =>
            {
                var r = Release(a); var gates = new JsonArray();
                void Gate(string name, bool ok, string detail) => gates.Add(new JsonObject { ["gate"] = name, ["passed"] = ok, ["detail"] = detail });
                var required = r["requiredTests"] as JsonArray;
                Gate("test-inventory", required is { Count: > 0 } && required.All(t => t is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)) && required.Select(t => t!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == required.Count, "At least one uniquely named required test must be declared.");
                var testRows = r["tests"] as JsonArray;
                Gate("test-evidence-structure", testRows is not null && testRows.All(t => t is JsonObject obj && !string.IsNullOrWhiteSpace(obj.Text("name")) && obj.Text("status") is "passed" or "failed" or "skipped" or "unknown"), "Test evidence must contain named structured results.");
                foreach (var test in required ?? [])
                {
                    var name = test!.GetValue<string>();
                    var results = (r["tests"] as JsonArray ?? []).OfType<JsonObject>().Where(t => t.Text("name") == name).ToArray();
                    Gate("test:" + name, results.Length == 1 && results[0].Text("status") == "passed", results.Length == 1 ? results[0].Text("status", "missing") : "missing or ambiguous result");
                }
                var migrations = r["migrations"] as JsonArray;
                Gate("migration-inventory", migrations is not null, "An explicit migrations array is required, even when empty.");
                Gate("migration-evidence-structure", migrations is not null && migrations.All(m => m is JsonObject obj && !string.IsNullOrWhiteSpace(obj.Text("id"))) && migrations.OfType<JsonObject>().Select(m => m.Text("id")).Distinct(StringComparer.Ordinal).Count() == migrations.Count, "Migration entries need unique IDs and structured evidence.");
                foreach (var m in migrations?.OfType<JsonObject>() ?? [])
                    Gate("migration:" + m.Text("id"), m["reviewed"]?.GetValue<bool>() == true && m["rollbackVerified"]?.GetValue<bool>() == true, "Requires review and verified rollback evidence.");
                var keys = r["requiredConfig"] as JsonArray;
                Gate("configuration-inventory", keys is { Count: > 0 } && keys.All(k => k is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)) && r["config"] is JsonObject, "Named required configuration and a values object must be declared.");
                foreach (var key in keys ?? [])
                {
                    var name = key!.GetValue<string>(); var value = r["config"]?[name]?.GetValue<string>();
                    Gate("config:" + name, !string.IsNullOrWhiteSpace(value) && !value.Contains("TODO", StringComparison.OrdinalIgnoreCase), "Required non-placeholder value.");
                }
                Gate("release-rollback", r["rollback"]?["rehearsed"]?.GetValue<bool>() == true && !string.IsNullOrWhiteSpace(r["rollback"]?["artifact"]?.GetValue<string>()), "Requires rehearsal and prior release artifact.");
                var result = new JsonObject { ["releaseId"] = a.Text("releaseId"), ["gates"] = gates, ["evidenceOnly"] = true };
                c.State[a.Text("releaseId") + "-source"] = r.ToJsonString();
                c.State[a.Text("releaseId")] = result.DeepClone(); return Task.FromResult<JsonNode?>(result);
            }), new("evaluate_gates", "Compute blocked/ready from every inspected gate; unknown and failed checks block.", schema, (a, ct) =>
            {
                if (c.State[a.Text("releaseId") + "-source"]?.GetValue<string>() != Release(a).ToJsonString()) throw new InvalidOperationException("Release evidence changed; inspect again.");
                var result = c.State[a.Text("releaseId")]?.AsObject() ?? throw new InvalidOperationException("Inspect this release first.");
                var gates = result["gates"]!.AsArray(); var failed = gates.OfType<JsonObject>().Count(g => g["passed"]?.GetValue<bool>() != true);
                result["status"] = failed == 0 ? "ready" : "blocked"; result["blockingGateCount"] = failed;
                return Task.FromResult<JsonNode?>(result.DeepClone());
            }), new("write_readiness_report", "Persist the evaluated release gates as a local JSON review artifact.", schema, async (a, ct) =>
            {
                if (c.State[a.Text("releaseId") + "-source"]?.GetValue<string>() != Release(a).ToJsonString()) throw new InvalidOperationException("Release evidence changed; inspect again.");
                var result = c.State[a.Text("releaseId")]?.AsObject();
                if (result?["status"] is null) throw new InvalidOperationException("Evaluate this release first.");
                var path = "readiness-" + a.Text("releaseId") + ".json"; await c.WriteJsonAsync(path, result, ct);
                return new JsonObject { ["artifact"] = path, ["status"] = result.Text("status") };
            })],
            [new("inspect_release", new() { ["releaseId"] = "release-42" }), new("evaluate_gates", new() { ["releaseId"] = "release-42" }), new("write_readiness_report", new() { ["releaseId"] = "release-42" })]);
    }
}
