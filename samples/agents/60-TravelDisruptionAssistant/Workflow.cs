// PiSharp-only (no upstream Pi source): deterministic travel alternatives and guarded local simulation.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.TravelDisruptionAssistant;

public static class Workflow
{
    private static JsonObject Schema() => SampleJson.Schema("{\"itineraryId\":{\"type\":\"string\"}}", "itineraryId");
    internal static JsonObject Args(string id) => new() { ["itineraryId"] = id };
    public static WorkflowSpec Create(SampleContext c) => new("60", "Travel disruption assistant",
        "Inspect the disruption, rank feasible alternatives and save a local rebooking plan. For the fixture use trip-1. If host approval is granted, then call rebook_local for the cheapest feasible option using its returned expectedHash as requested by the fixture. Respect every connection, time, mode, refundability and integer-cent budget constraint. rebook_local only records a simulation in this workspace, never a ticket.",
        [new("inspect_disruption", "Read disrupted itinerary, policy and candidate transport schedules", Schema(), (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("itineraryId")))),
         new("rank_alternatives", "Validate routes and rank feasible transport alternatives by price then arrival", Schema(), (a, _) => Task.FromResult<JsonNode?>(Rank(c, a.Text("itineraryId")))),
         new("save_rebooking_plan", "Save a local proposal with rejection reasons and approval hashes", Schema(), async (a, ct) =>
         {
             var id = a.Text("itineraryId"); var plan = Rank(c, id); var path = "travel-" + id + ".json";
             await c.WriteJsonAsync(path, plan, ct); return new JsonObject { ["path"] = path, ["plan"] = plan };
         }),
         new("rebook_local", "With explicit approval, record one selected proposal as a workspace-only simulation",
             SampleJson.Schema("{\"itineraryId\":{\"type\":\"string\"},\"alternativeId\":{\"type\":\"string\"},\"expectedHash\":{\"type\":\"string\"}}", "itineraryId", "alternativeId", "expectedHash"),
             (a, ct) => Rebook(c, a, ct), RequiresApproval: true)],
        Demo(c));
    private static IReadOnlyList<PlannedCall> Demo(SampleContext c)
    {
        var calls = new List<PlannedCall> { new("inspect_disruption", Args("trip-1")), new("rank_alternatives", Args("trip-1")), new("save_rebooking_plan", Args("trip-1")) };
        if (c.Approved && Rank(c, "trip-1")["feasible"]!.AsArray().FirstOrDefault() is JsonObject selected)
            calls.Add(new("rebook_local", new JsonObject { ["itineraryId"] = "trip-1", ["alternativeId"] = selected.Text("alternativeId"), ["expectedHash"] = selected.Text("expectedHash") }));
        return calls;
    }
    private static JsonObject Trip(SampleContext c, string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Invalid itinerary ID.");
        var rows = Rows(c.Input, "itineraries").Where(t => t.Text("id") == id).ToArray();
        if (rows.Length != 1) throw new ArgumentException("Unknown or conflicting itinerary ID.");
        var trip = rows[0]; var alternatives = Rows(trip, "alternatives");
        if (new[] { "origin", "destination", "disruptedServiceId" }.Any(k => trip[k] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
            throw new ArgumentException("Itinerary origin, destination and disrupted service ID are required.");
        if (alternatives.Any(a => string.IsNullOrWhiteSpace(a.Text("id"))) || alternatives.Select(a => a.Text("id")).Distinct().Count() != alternatives.Length) throw new ArgumentException("Duplicate or missing alternative ID.");
        if (Money(trip["maxBudgetCents"]) < 0 || trip["minConnectionMinutes"]!.GetValue<int>() < 0 || Time(trip.Text("earliestDeparture")) > Time(trip.Text("latestArrival"))) throw new ArgumentException("Invalid time or budget policy.");
        return trip;
    }
    private static JsonObject[] Rows(JsonObject parent, string key)
    {
        if (parent[key] is not JsonArray array || array.Any(x => x is not JsonObject)) throw new ArgumentException("Expected object rows in " + key);
        return array.Select(x => x!.AsObject()).ToArray();
    }
    private static long Money(JsonNode? value) => long.TryParse(value?.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cents) ? cents : throw new ArgumentException("Money must be integer cents.");
    private static DateTimeOffset Time(string value)
    {
        if (!Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$")) throw new ArgumentException("Schedule must include UTC offset.");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
    internal static JsonObject Inspect(SampleContext c, string id) => (JsonObject)Trip(c, id).DeepClone();
    internal static JsonObject Rank(SampleContext c, string id)
    {
        var trip = Trip(c, id); var accepted = new List<JsonObject>(); var rejected = new JsonArray();
        foreach (var candidate in Rows(trip, "alternatives"))
        {
            var reasons = new HashSet<string>(StringComparer.Ordinal); var cost = Money(candidate["costCents"]);
            if (cost < 0 || cost > Money(trip["maxBudgetCents"])) reasons.Add("outside-budget");
            if (trip["requireRefundable"]!.GetValue<bool>() && !candidate["refundable"]!.GetValue<bool>()) reasons.Add("refund-policy");
            var legs = Rows(candidate, "legs");
            if (legs.Length == 0) reasons.Add("empty-route");
            DateTimeOffset? previousArrival = null; string? previousDestination = null;
            for (var i = 0; i < legs.Length; i++)
            {
                var leg = legs[i]; var departure = Time(leg.Text("departure")); var arrival = Time(leg.Text("arrival"));
                if (new[] { "serviceId", "origin", "destination", "mode" }.Any(k => string.IsNullOrWhiteSpace(leg.Text(k)))) reasons.Add("missing-leg-metadata");
                if (arrival <= departure) reasons.Add("invalid-leg-times");
                if (!trip["allowedModes"]!.AsArray().Any(x => x!.GetValue<string>() == leg.Text("mode"))) reasons.Add("mode-policy");
                if (leg.Text("serviceId") == trip.Text("disruptedServiceId")) reasons.Add("disrupted-service");
                if (i == 0 && (leg.Text("origin") != trip.Text("origin") || departure < Time(trip.Text("earliestDeparture")))) reasons.Add("invalid-start");
                if (previousArrival is not null && (previousDestination != leg.Text("origin") || departure < previousArrival.Value.AddMinutes(trip["minConnectionMinutes"]!.GetValue<int>()))) reasons.Add("invalid-connection");
                previousArrival = arrival; previousDestination = leg.Text("destination");
            }
            if (legs.Length > 0 && (previousDestination != trip.Text("destination") || previousArrival > Time(trip.Text("latestArrival")))) reasons.Add("invalid-arrival");
            if (reasons.Count > 0)
                rejected.Add(new JsonObject { ["alternativeId"] = candidate.Text("id"), ["reasons"] = new JsonArray(reasons.Order().Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()) });
            else
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trip.ToJsonString() + "\n" + candidate.Text("id"))));
                accepted.Add(new JsonObject { ["alternativeId"] = candidate.Text("id"), ["costCents"] = cost, ["arrival"] = previousArrival!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ["expectedHash"] = hash, ["legs"] = candidate["legs"]!.DeepClone() });
            }
        }
        return new() { ["itineraryId"] = id, ["feasible"] = new JsonArray(accepted.OrderBy(a => a["costCents"]!.GetValue<long>()).ThenBy(a => a.Text("arrival"), StringComparer.Ordinal).ThenBy(a => a.Text("alternativeId"), StringComparer.Ordinal).Select(a => (JsonNode?)a).ToArray()), ["rejected"] = rejected, ["status"] = accepted.Count > 0 ? "proposal-only" : "no-feasible-alternative", ["externalBookingMade"] = false };
    }
    internal static async Task<JsonNode?> Rebook(SampleContext c, JsonObject args, CancellationToken ct)
    {
        if (!c.Approved) throw new InvalidOperationException("Explicit host approval required.");
        var id = args.Text("itineraryId"); var ranked = Rank(c, id);
        var candidate = ranked["feasible"]!.AsArray().OfType<JsonObject>().SingleOrDefault(a => a.Text("alternativeId") == args.Text("alternativeId")) ?? throw new InvalidOperationException("Alternative is not feasible.");
        if (candidate.Text("expectedHash") != args.Text("expectedHash")) throw new InvalidOperationException("Proposal changed; review again.");
        var path = "local-rebooking-" + id + ".json";
        var receipt = new JsonObject { ["itineraryId"] = id, ["alternativeId"] = candidate.Text("alternativeId"), ["expectedHash"] = candidate.Text("expectedHash"), ["status"] = "workspace-simulation-only", ["externalBookingMade"] = false, ["path"] = path };
        if (File.Exists(c.ResolvePath(path)))
        {
            var prior = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath(path), ct))!;
            if (!JsonNode.DeepEquals(prior, receipt)) throw new InvalidOperationException("A different local rebooking already exists.");
            return prior;
        }
        await c.WriteJsonAsync(path, receipt, ct); return receipt;
    }
}
