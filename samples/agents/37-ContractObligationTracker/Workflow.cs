// PiSharp-only (no upstream Pi source): sourced contractual obligation dates from structured local clauses.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.ContractObligationTracker;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"contractId\":{\"type\":\"string\"}}", "contractId"); var id = c.Input.Text("contractId");
        return new("37", "Contract Obligation Tracker",
            "Run inspect_contract, calculate_deadlines and save_obligation_register for the requested contractId. Use the explicit asOf date and structured source clauses. Preserve source IDs, clause IDs, owners, lead periods and unresolved event dates; never invent missing terms. Outputs are local review artifacts, not legal advice or external notifications.",
            [new("inspect_contract", "Inspect a contract and validate each obligation against its source clause.", schema, (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("contractId")))),
             new("calculate_deadlines", "Compute fixed/event-based due dates, lead windows and overdue state.", schema, (a, _) => Task.FromResult<JsonNode?>(Calculate(c, a.Text("contractId")))),
             new("save_obligation_register", "Persist the sourced obligation register and status counts.", schema, async (a, ct) => { var id = Id(a.Text("contractId")); var register = Calculate(c, id); var path = $"obligations-{id}.json"; await c.WriteJsonAsync(path, register, ct); return new JsonObject { ["artifact"] = path, ["register"] = register }; })],
            [new("inspect_contract", Args(id)), new("calculate_deadlines", Args(id)), new("save_obligation_register", Args(id))]);
    }
    internal static JsonObject Args(string id) => new() { ["contractId"] = id };
    static string Id(string id) => id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? id : throw new ArgumentException("Invalid contract ID.");
    static JsonArray Rows(JsonObject obj, string key) => obj[key] as JsonArray ?? throw new ArgumentException($"Missing array: {key}");
    static IEnumerable<JsonObject> Objects(JsonArray rows) => rows.Select(n => n as JsonObject ?? throw new ArgumentException("Every record must be a JSON object."));
    static JsonObject One(JsonArray rows, string id) => Objects(rows).Where(n => n.Text("id") == id).ToArray() is [var row] ? row : throw new ArgumentException($"Missing or ambiguous source ID: {id}");
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var contract = One(Rows(c.Input, "contracts"), Id(id)); var obligations = Rows(contract, "obligations");
        if (Objects(obligations).Select(n => n.Text("id")).Distinct(StringComparer.Ordinal).Count() != obligations.Count) throw new ArgumentException("Ambiguous obligation IDs.");
        foreach (var obligation in Objects(obligations))
        {
            if (string.IsNullOrWhiteSpace(obligation.Text("id")) || string.IsNullOrWhiteSpace(obligation.Text("owner")) || string.IsNullOrWhiteSpace(obligation.Text("action"))) throw new ArgumentException("Obligation needs ID, owner and action.");
            var source = One(Rows(contract, "sources"), obligation.Text("sourceId")); var clause = One(Rows(source, "clauses"), obligation.Text("clauseId"));
            if (string.IsNullOrWhiteSpace(clause.Text("text"))) throw new ArgumentException("Source clause text is missing.");
        }
        return (JsonObject)contract.DeepClone();
    }
    internal static JsonObject Calculate(SampleContext c, string id)
    {
        var contract = Inspect(c, id); var asOf = DateOnly.ParseExact(c.Input.Text("asOf"), "yyyy-MM-dd"); var results = new List<(DateOnly? Due, JsonObject Row)>();
        foreach (var o in Objects(Rows(contract, "obligations")))
        {
            var lead = o["leadDays"]!.GetValue<int>(); if (lead is < 0 or > 3650) throw new ArgumentException("Lead days must be between zero and 3650.");
            var fixedDate = o.Text("dueDate"); var eventId = o.Text("eventId");
            if ((fixedDate.Length == 0) == (eventId.Length == 0)) throw new ArgumentException("An obligation requires exactly one fixed date or event rule.");
            DateOnly? due = null; string? unresolved = null;
            if (fixedDate.Length > 0) due = DateOnly.ParseExact(fixedDate, "yyyy-MM-dd");
            else
            {
                var offset = o["offsetDays"]?.GetValue<int>() ?? throw new ArgumentException("Event obligation requires offsetDays.");
                if (offset is < -3650 or > 3650) throw new ArgumentException("Event offset must be within ten years.");
                var events = Objects(Rows(contract, "events")).Where(e => e.Text("id") == eventId).ToArray();
                if (events.Length > 1) throw new ArgumentException("Ambiguous trigger event.");
                if (events.Length == 0 || events[0].Text("date").Length == 0) unresolved = $"Trigger event {eventId} has no known date.";
                else due = DateOnly.ParseExact(events[0].Text("date"), "yyyy-MM-dd").AddDays(offset);
            }
            var fulfilled = o["fulfilled"]!.GetValue<bool>(); var days = due?.DayNumber - asOf.DayNumber;
            var status = fulfilled ? "fulfilled" : due is null ? "unresolved" : days < 0 ? "overdue" : days == 0 ? "due-today" : days <= lead ? "due-soon" : "upcoming";
            var source = One(Rows(contract, "sources"), o.Text("sourceId")); var clause = One(Rows(source, "clauses"), o.Text("clauseId"));
            results.Add((due, new JsonObject { ["obligationId"] = o.Text("id"), ["action"] = o.Text("action"), ["owner"] = o.Text("owner"), ["dueDate"] = due?.ToString("yyyy-MM-dd"), ["leadDays"] = lead, ["remindOn"] = due?.AddDays(-lead).ToString("yyyy-MM-dd"), ["daysRemaining"] = days, ["status"] = status, ["unresolvedReason"] = unresolved,
                ["source"] = new JsonObject { ["sourceId"] = source.Text("id"), ["title"] = source.Text("title"), ["clauseId"] = clause.Text("id"), ["text"] = clause.Text("text") } }));
        }
        var ordered = results.OrderBy(r => r.Due is null).ThenBy(r => r.Due).ThenBy(r => r.Row.Text("obligationId"), StringComparer.Ordinal).Select(r => r.Row).ToArray();
        var counts = new JsonObject(); foreach (var group in ordered.GroupBy(r => r.Text("status"))) counts[group.Key] = group.Count();
        return new() { ["contractId"] = id, ["asOf"] = asOf.ToString("yyyy-MM-dd"), ["obligations"] = new JsonArray(ordered.Select(r => (JsonNode?)r).ToArray()), ["statusCounts"] = counts, ["externalNotificationsSent"] = false };
    }
}
