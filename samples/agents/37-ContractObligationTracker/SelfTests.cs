// PiSharp-only (no upstream Pi source): sourced deadline and lead-window boundary tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.ContractObligationTracker;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-obligations-" + Guid.NewGuid().ToString("N")); var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message) { var failed = false; try { action(); } catch (ArgumentException) { failed = true; } Check(failed, message); }
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var result = Workflow.Calculate(c, "contract-a"); var rows = result["obligations"]!.AsArray().OfType<JsonObject>().ToArray();
            Check(rows.Single(r => r.Text("obligationId") == "quarterly-report").Text("status") == "overdue", "Past deadline is overdue.");
            var notice = rows.Single(r => r.Text("obligationId") == "renewal-notice");
            Check(notice.Text("status") == "due-soon" && notice.Text("remindOn") == "2026-11-01", "Lead period includes first reminder day.");
            Check(rows.Single(r => r.Text("obligationId") == "acceptance-pack").Text("dueDate") == "2026-11-04", "Event offset calculates calendar deadline.");
            Check(rows.Single(r => r.Text("obligationId") == "transition-plan").Text("status") == "unresolved", "Missing event remains unresolved.");
            Check(rows.All(r => r["source"]!["text"]!.GetValue<string>().Length > 0), "Every result retains source clause text.");
            var run = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(run.Success && run.Report["outputs"]!.AsArray().Count == 3 && File.Exists(c.ResolvePath("obligations-contract-a.json")), "Native tools persist obligation register.");
            Reject(() => Workflow.Calculate(c, "missing"), "Missing contract rejected.");
            var duplicate = (JsonObject)input.DeepClone(); duplicate["contracts"]![0]!["sources"]!.AsArray().Add(duplicate["contracts"]![0]!["sources"]![0]!.DeepClone());
            Reject(() => Workflow.Calculate(new SampleContext(Path.Combine(root, "duplicate"), duplicate), "contract-a"), "Ambiguous source rejected.");
            foreach (var collection in new[] { "contracts", "sources", "events-null", "events-scalar" })
            {
                var malformed = (JsonObject)input.DeepClone();
                var records = collection == "contracts" ? malformed[collection]!.AsArray() : malformed["contracts"]![0]![collection.StartsWith("events", StringComparison.Ordinal) ? "events" : collection]!.AsArray();
                records.Add(collection is "contracts" or "events-scalar" ? JsonValue.Create("not-a-record") : null);
                Reject(() => Workflow.Calculate(new SampleContext(Path.Combine(root, "malformed-" + collection), malformed), "contract-a"), $"Malformed {collection} record cannot silently disappear.");
            }
            var missingClause = (JsonObject)input.DeepClone(); missingClause["contracts"]![0]!["obligations"]![0]!["clauseId"] = "unknown";
            Reject(() => Workflow.Calculate(new SampleContext(Path.Combine(root, "missing-clause"), missingClause), "contract-a"), "Missing source clause rejected.");
            var ambiguousRule = (JsonObject)input.DeepClone(); ambiguousRule["contracts"]![0]!["obligations"]![0]!["eventId"] = "delivery";
            Reject(() => Workflow.Calculate(new SampleContext(Path.Combine(root, "ambiguous-rule"), ambiguousRule), "contract-a"), "Fixed and event deadline cannot both apply.");
            var missingOffset = (JsonObject)input.DeepClone(); missingOffset["contracts"]![0]!["obligations"]![3]!.AsObject().Remove("offsetDays");
            Reject(() => Workflow.Calculate(new SampleContext(Path.Combine(root, "missing-offset"), missingOffset), "contract-a"), "Missing offset rejected even before trigger occurs.");
            var other = Workflow.Calculate(c, "contract-b");
            Check(other["obligations"]!.AsArray().Count == 1 && other["obligations"]![0]!["status"]!.GetValue<string>() == "due-today", "Second contract computed independently at exact due date.");
            var completed = (JsonObject)input.DeepClone(); completed["contracts"]![0]!["obligations"]![0]!["fulfilled"] = true;
            Check(Workflow.Calculate(new SampleContext(Path.Combine(root, "fulfilled"), completed), "contract-a")["obligations"]![0]!["status"]!.GetValue<string>() == "fulfilled", "Fulfilled obligation is not reported overdue.");
            var repeated = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(repeated.Success && JsonNode.DeepEquals(result, JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath("obligations-contract-a.json")))), "Repeated run is deterministic.");
            Console.WriteLine($"PASS ContractObligationTracker: {checks} focused checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root, true); }
    }
}
