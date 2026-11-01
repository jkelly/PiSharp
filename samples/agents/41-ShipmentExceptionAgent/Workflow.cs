// PiSharp-only (no upstream Pi source): event-time shipment exception analysis and grounded draft notifications.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.ShipmentExceptionAgent;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("41-ShipmentExceptionAgent", "Shipment Exception Agent",
        "Inspect the requested shipment's timestamped tracking events, detect delay/stall using fixture asOf, and save grounded customer/carrier drafts. Sort events by occurrence time. Do not invent a new ETA or claim notifications were sent.",
        [new("inspect_tracking", "Inspect and sort tracking events for an exact shipment ID.",Schema(),(a,t) => Task.FromResult<JsonNode?>(Inspect(c,a))),
         new("detect_exception", "Compute overdue hours and stalled hours against configured thresholds.",Schema(),(a,t) => Task.FromResult<JsonNode?>(Analyze(c,a))),
         new("save_notification_drafts", "Write factual local drafts with event IDs; no message is sent.",Schema(),(a,t) => SaveAsync(c,a,t))],
        [new("inspect_tracking",Args("SHIP-501")),new("detect_exception",Args("SHIP-501")),new("save_notification_drafts",Args("SHIP-501"))]);
    static JsonObject Schema() => SampleJson.Schema("""{"shipmentId":{"type":"string"}}""","shipmentId");
    static JsonObject Args(string id) => new() { ["shipmentId"] = id };
    static JsonObject? Find(SampleContext c, JsonObject a) => Data.Find(c.Input["shipments"],Data.Id(a,"shipmentId"));
    static JsonObject[] Events(SampleContext c, JsonObject s)
    {
        var now = Data.Time(c.Input,"asOf");
        var events = Data.Rows(s["events"]).OrderBy(e => Data.Time(e,"occurredAt")).ThenBy(e => Data.Text(e,"id"),StringComparer.Ordinal).ToArray();
        if (events.GroupBy(e => Data.Text(e,"id")).Any(g => g.Count() > 1)) throw new InvalidOperationException("Ambiguous tracking event IDs.");
        if (events.Any(e => Data.Time(e,"occurredAt") > now)) throw new InvalidOperationException("Tracking event occurs after asOf.");
        if (events.GroupBy(e => Data.Time(e,"occurredAt")).Any(g => g.Select(e => Data.Text(e,"status")).Distinct().Count() > 1)) throw new InvalidOperationException("Conflicting statuses at the same event timestamp.");
        return events;
    }
    static JsonNode Inspect(SampleContext c, JsonObject a)
    {
        var s = Find(c,a);
        return s is null ? Data.Missing(Data.Id(a,"shipmentId")) : Data.Json(new { found = true, shipmentId = Data.Text(s,"id"), promisedAt = Data.Time(s,"promisedAt"), events = Events(c,s) });
    }
    static JsonNode Analyze(SampleContext c, JsonObject a)
    {
        var s = Find(c,a);
        if (s is null) return Data.Missing(Data.Id(a,"shipmentId"));
        var events = Events(c,s);
        var last = events.LastOrDefault();
        var now = Data.Time(c.Input,"asOf");
        var promised = Data.Time(s,"promisedAt");
        var delivered = last is not null && Data.Text(last,"status") == "delivered";
        var overdue = Math.Max(0,((delivered ? Data.Time(last!,"occurredAt") : now) - promised).TotalHours);
        var silence = last is null ? (double?)null : (now - Data.Time(last,"occurredAt")).TotalHours;
        var stalled = !delivered && silence >= Data.Number(c.Input,"stallAfterHours",1);
        var explicitException = !delivered && last is not null && Data.Text(last,"status") == "exception";
        return Data.Json(new { found = true, shipmentId = Data.Text(s,"id"), asOf = now, promisedAt = promised, delivered,
            overdueHours = overdue, hoursSinceLastEvent = silence, stalled, explicitException,
            needsAttention = !delivered && (overdue > 0 || stalled || explicitException || last is null),
            trackingMissing = last is null, latestEventId = last is null ? null : Data.Text(last,"id"),
            latestStatus = last is null ? "unknown" : Data.Text(last,"status"),
            latestLocation = last is null ? "unknown" : Data.Text(last,"location"),
            latestOccurredAt = last is null ? (DateTimeOffset?)null : Data.Time(last,"occurredAt"),
            evidenceIds = events.Select(e => Data.Text(e,"id")).ToArray() });
    }
    static async Task<JsonNode?> SaveAsync(SampleContext c, JsonObject a, CancellationToken token)
    {
        var analysis = Analyze(c,a);
        if (analysis["found"]!.GetValue<bool>() != true) return analysis;
        var id = Data.Id(a,"shipmentId");
        var attention = analysis["needsAttention"]!.GetValue<bool>();
        var fact = analysis["trackingMissing"]!.GetValue<bool>() ? "No tracking events are available."
            : $"Latest scan {Data.Text(analysis,"latestEventId")}: {Data.Text(analysis,"latestStatus")} at {Data.Text(analysis,"latestLocation")} ({Data.Text(analysis,"latestOccurredAt")}).";
        var draft = Data.Json(new { shipmentId = id, analysis,
            customerDraft = attention ? $"Shipment {id} needs a delivery update. {fact} Promised delivery: {Data.Text(analysis,"promisedAt")}. We have no confirmed revised ETA." : $"Shipment {id} has no active delivery exception. {fact}",
            carrierDraft = attention ? $"Please investigate {id} and confirm location and delivery estimate. {fact}" : $"No carrier investigation requested for {id}.",
            requiresHumanReview = true, sent = false });
        var path = "shipments/" + id + ".json";
        await c.WriteJsonAsync(path,draft,token);
        return Data.Json(new { saved = true, path, draft });
    }
}
