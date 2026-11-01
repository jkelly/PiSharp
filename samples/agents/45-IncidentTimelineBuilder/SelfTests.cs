// PiSharp-only (no upstream Pi source): chronology, deduplication and conflicting evidence tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.IncidentTimelineBuilder;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var count = 0;
        SampleContext Context() => new(Path.Combine(Path.GetTempPath(),"pisharp-45-" + Guid.NewGuid().ToString("N")),(JsonObject)input.DeepClone());
        async Task<JsonNode> Call(SampleContext c,string name,string id = "INC1") => (await Workflow.Create(c).Tools.Single(t => t.Name == name).Execute(new JsonObject { ["incidentId"] = id },default))!;
        static void Check(bool value) { if (!value) throw new Exception("Timeline assertion failed."); }
        static async Task Reject(Func<Task<JsonNode>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return; } throw new Exception("Expected rejection."); }
        var c = Context(); await Call(c,"collect_evidence"); var result = await Call(c,"build_timeline"); var rows = result["timeline"]!.AsArray(); Check(rows.Count == 3 && rows[0]!["eventId"]!.GetValue<string>() == "ev1" && rows[1]!["sourceIds"]!.AsArray().Count == 2); count++;
        Check(result["correlations"]!.AsArray().Single(g => g!["correlationId"]!.GetValue<string>() == "req-17")!["eventIds"]!.AsArray().Count == 2 && result["rootCause"]!.GetValue<string>().StartsWith("not established",StringComparison.Ordinal)); await Call(c,"publish_timeline"); Check(File.Exists(c.ResolvePath("incident-timeline.json"))); count++;
        await Call(c,"collect_evidence","INC2"); var second = await Call(c,"build_timeline","INC2"); Check(second["timeline"]!.AsArray().Count == 1 && second["timeline"]![0]!["eventId"]!.GetValue<string>() == "other1"); Check((await Call(c,"build_timeline"))["timeline"]!.AsArray().Count == 3); count++;
        await Reject(() => Call(Context(),"build_timeline")); await Reject(() => Call(Context(),"collect_evidence","missing")); count++;
        var conflict = Context(); conflict.Input["incidents"]![0]!["sources"]![1]!["events"]![0]!["summary"] = "Different claim"; await Call(conflict,"collect_evidence"); var conflicting = await Call(conflict,"build_timeline"); Check(conflicting["timeline"]!.AsArray().Count == 4 && conflicting["conflicts"]!.AsArray().Count == 1); count++;
        var invalid = Context(); invalid.Input["incidents"]![0]!["sources"]![0]!["events"]![0]!.AsObject().Remove("timestamp"); await Reject(() => Call(invalid,"collect_evidence")); count++;
        var timezone = Context(); timezone.Input["incidents"]![0]!["sources"]![0]!["events"]![0]!["timestamp"] = "2026-11-01T10:02:00"; await Reject(() => Call(timezone,"collect_evidence")); count++;
        await Call(c,"collect_evidence"); await Reject(() => Call(c,"publish_timeline")); count++;
        var delimiter = Context(); var firstVariant = delimiter.Input["incidents"]![0]!["sources"]![0]!["events"]![0]!; var secondVariant = delimiter.Input["incidents"]![0]!["sources"]![1]!["events"]![0]!;
        firstVariant["kind"] = "error\ninjected"; firstVariant["summary"] = "claim"; secondVariant["kind"] = "error"; secondVariant["summary"] = "injected\nclaim";
        await Call(delimiter,"collect_evidence"); var delimiterResult = await Call(delimiter,"build_timeline"); Check(delimiterResult["timeline"]!.AsArray().Count == 4 && delimiterResult["conflicts"]!.AsArray().Count == 1 && delimiterResult["timeline"]!.AsArray().Count(e => e!["eventId"]!.GetValue<string>() == "ev2" && e["conflicted"]!.GetValue<bool>()) == 2); count++;
        Console.WriteLine($"PASS 45-IncidentTimelineBuilder ({count} focused cases)"); return 0;
    }
}
