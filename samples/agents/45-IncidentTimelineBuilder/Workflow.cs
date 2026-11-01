// PiSharp-only (no upstream Pi source): evidence-preserving multi-source incident timelines.
using System.Globalization;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.IncidentTimelineBuilder;

public static class Workflow
{
    static JsonObject Schema() => SampleJson.Schema("""{"incidentId":{"type":"string"}}""", "incidentId");
    public static WorkflowSpec Create(SampleContext c) => new("45-IncidentTimelineBuilder", "Incident timeline builder",
        "For each incident call collect_evidence, build_timeline, and publish_timeline. Deduplicate shared event IDs, preserve source references, flag conflicts, normalize UTC chronology and correlate only explicit correlation IDs. Do not infer root causes or causality from temporal proximity.",
        [new("collect_evidence", "Collect bounded source records for the requested incident", Schema(), (a,t) => Collect(c,a,t)),
         new("build_timeline", "Sort, deduplicate, and correlate collected evidence", Schema(), (a,t) => Build(c,a,t)),
         new("publish_timeline", "Persist the evidence timeline and unresolved conflicts", Schema(), (a,t) => Publish(c,a,t))],
        [new("collect_evidence",new() { ["incidentId"] = "INC1" }),new("build_timeline",new() { ["incidentId"] = "INC1" }),new("publish_timeline",new() { ["incidentId"] = "INC1" })]);
    static JsonArray Rows(JsonObject o,string k) => o[k] as JsonArray ?? throw new FormatException("Missing " + k);
    static JsonObject Incident(SampleContext c,string id) => Rows(c.Input,"incidents").OfType<JsonObject>().SingleOrDefault(i => i.Text("id") == id) ?? throw new ArgumentException("Unknown incident: " + id);
    static Task<JsonNode?> Collect(SampleContext c,JsonObject a,CancellationToken t)
    {
        t.ThrowIfCancellationRequested(); var id = a.Text("incidentId"); var incident = Incident(c,id); var evidence = new JsonArray(); var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Rows(incident,"sources").OfType<JsonObject>())
        {
            if (source.Text("id").Length == 0 || !sourceIds.Add(source.Text("id"))) throw new FormatException("Missing or duplicate source ID.");
            foreach (var entry in Rows(source,"events").OfType<JsonObject>())
            {
                if (entry.Text("id").Length == 0 || entry.Text("summary").Length == 0 || entry.Text("kind").Length == 0 || !DateTimeOffset.TryParse(entry.Text("timestamp"),CultureInfo.InvariantCulture,DateTimeStyles.None,out var time) || !(entry.Text("timestamp").EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(entry.Text("timestamp"),@"[+-]\d{2}:\d{2}$"))) throw new FormatException("Event requires ID, kind, summary and timestamp with timezone.");
                evidence.Add(new JsonObject { ["eventId"] = entry.Text("id"), ["timestamp"] = time.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture), ["kind"] = entry.Text("kind"), ["summary"] = entry.Text("summary"), ["correlationId"] = entry.Text("correlationId"), ["sourceId"] = source.Text("id") });
            }
        }
        if (evidence.Count == 0) throw new InvalidOperationException("No incident evidence.");
        c.State["evidence:" + id] = evidence.DeepClone(); c.State.Remove("timeline:" + id);
        return Task.FromResult<JsonNode?>(new JsonObject { ["incidentId"] = id, ["evidence"] = evidence });
    }
    static Task<JsonNode?> Build(SampleContext c,JsonObject a,CancellationToken t)
    {
        t.ThrowIfCancellationRequested(); var id = a.Text("incidentId"); var evidence = c.State["evidence:" + id] as JsonArray ?? throw new InvalidOperationException("Collect this incident first."); var timeline = new List<JsonObject>(); var conflicts = new JsonArray();
        foreach (var group in evidence.OfType<JsonObject>().GroupBy(e => e.Text("eventId")))
        {
            var variants = group.GroupBy(e => (Timestamp: e.Text("timestamp"), Kind: e.Text("kind"), Summary: e.Text("summary"), CorrelationId: e.Text("correlationId"))).ToArray();
            if (variants.Length > 1) conflicts.Add(new JsonObject { ["eventId"] = group.Key, ["reason"] = "Sources disagree; variants retained", ["variants"] = new JsonArray(group.Select(e => e.DeepClone()).ToArray()) });
            foreach (var variant in variants)
            {
                var row = (JsonObject)variant.First().DeepClone(); row.Remove("sourceId"); row["sourceIds"] = new JsonArray(variant.Select(e => e.Text("sourceId")).Distinct().Order().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()); row["conflicted"] = variants.Length > 1; timeline.Add(row);
            }
        }
        var sorted = timeline.OrderBy(e => e.Text("timestamp"),StringComparer.Ordinal).ThenBy(e => e.Text("eventId"),StringComparer.Ordinal).ThenBy(e => e.Text("summary"),StringComparer.Ordinal).ToArray(); var correlations = new JsonArray();
        foreach (var group in sorted.Where(e => e.Text("correlationId").Length > 0).GroupBy(e => e.Text("correlationId")))
            correlations.Add(new JsonObject { ["correlationId"] = group.Key, ["eventIds"] = new JsonArray(group.Select(e => e.Text("eventId")).Distinct().Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()), ["interpretation"] = "shared explicit correlation ID; causality unproven" });
        var result = new JsonObject { ["incidentId"] = id, ["timeline"] = new JsonArray(sorted.Select(e => (JsonNode?)e).ToArray()), ["correlations"] = correlations, ["conflicts"] = conflicts, ["rootCause"] = "not established by supplied evidence" };
        c.State["timeline:" + id] = result.DeepClone(); return Task.FromResult<JsonNode?>(result);
    }
    static async Task<JsonNode?> Publish(SampleContext c,JsonObject a,CancellationToken t)
    { var result = c.State["timeline:" + a.Text("incidentId")]?.DeepClone() ?? throw new InvalidOperationException("Build this incident timeline first."); await c.WriteJsonAsync("incident-timeline.json",result,t); return result; }
}
