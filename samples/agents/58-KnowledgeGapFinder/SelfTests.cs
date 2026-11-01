// PiSharp-only (no upstream Pi source): offline knowledge coverage regression tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.KnowledgeGapFinder;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-58-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root, input); var report = Workflow.Analyze(c, "support-q4");
            Check(report["articleTasks"]!.AsArray().Count == 1, "one grounded gap");
            Check(report["articleTasks"]![0]!["sourceIds"]!.AsArray().Count == 2, "question and failed search clustered");
            Check(report["coverage"]![0]!["coveragePercent"]!.GetValue<int>() == 33, "partial article coverage calculated");
            Check(report["unclassifiedSignals"]!.AsArray().Count == 1, "unmatched query retained");
            Check(Workflow.Analyze(c, "support-empty")["articleTasks"]!.AsArray().Count == 0, "second corpus not stale");
            Check(JsonNode.DeepEquals(report, Workflow.Analyze(c, "support-q4")), "repeat deterministic");
            Throws(() => Workflow.Analyze(c, "missing"), "missing corpus rejected");
            foreach (var key in new[] { "signals", "articles" })
            {
                var incomplete = (JsonObject)input.DeepClone(); incomplete["corpora"]![0]![key]![0]!.AsObject().Remove("text");
                Throws(() => Workflow.Analyze(new SampleContext(Path.Combine(root, "missing-" + key), incomplete), "support-q4"), "missing source text rejected for " + key);
            }
            var malformed = (JsonObject)input.DeepClone(); malformed["corpora"]![0]!["signals"]!.AsArray().Add("not-a-source-row");
            Throws(() => Workflow.Analyze(new SampleContext(Path.Combine(root, "malformed"), malformed), "support-q4"), "malformed source row not silently skipped");
            input["corpora"]![0]!["signals"]!.AsArray().Add(SampleJson.Object("{\"id\":\"tie\",\"kind\":\"question\",\"answered\":false,\"text\":\"recovery export\"}"));
            Check(Workflow.Analyze(c, "support-q4")["unclassifiedSignals"]!.AsArray().Count == 2, "ambiguous keyword match explicit");
            input["corpora"]![0]!["signals"]!.AsArray().Add(input["corpora"]![0]!["signals"]![0]!.DeepClone());
            Throws(() => Workflow.Analyze(c, "support-q4"), "duplicate signal rejected");
            var native = new SampleContext(Path.Combine(root, "native"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
            Check((await SampleHost.ExecuteAsync(Workflow.Create(native), native)).Success, "native three-stage workflow");
            Check(File.Exists(native.ResolvePath("knowledge-support-q4.json")), "backlog persisted");
            Console.WriteLine("PASS 58: coverage, clustering, ambiguity, IDs, repeats, persistence and native workflow."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL 58: " + e.Message); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException(message); }
}
