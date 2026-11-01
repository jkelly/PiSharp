// PiSharp-only (no upstream Pi source): auditable local offboarding with approval-gated access revocation.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.EmployeeOffboardingCoordinator;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"employeeId\":{\"type\":\"string\"}}", "employeeId");
        var id = c.Input.Text("employeeId"); var calls = new List<PlannedCall> { new("inspect_departure", Args(id)), new("plan_offboarding", Args(id)), new("save_offboarding_plan", Args(id)) };
        if (c.Approved) calls.Add(new("revoke_local_access", Args(id)));
        return new("35", "Employee Offboarding Coordinator",
            "Inspect the named departure, plan ordered access/equipment/handoff work and save_offboarding_plan. Retain pending obligations and distinguish manual tasks from actual local actions. With host approval revoke_local_access updates only the workspace ledger. No external identity, payroll or device systems are connected.",
            [new("inspect_departure", "Inspect employee departure, accesses, assets and obligations.", schema, (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("employeeId")))),
             new("plan_offboarding", "Calculate ordered offboarding steps and outstanding obligations.", schema, (a, _) => Task.FromResult<JsonNode?>(Plan(c, a.Text("employeeId")))),
             new("save_offboarding_plan", "Persist a reviewable offboarding plan.", schema, async (a, ct) => { var id = Id(a.Text("employeeId")); var plan = Plan(c, id); var path = $"offboarding-{id}.json"; await c.WriteJsonAsync(path, plan, ct); return new JsonObject { ["artifact"] = path, ["plan"] = plan }; }),
             new("revoke_local_access", "Approve and record local access revocations, retaining manual obligations.", schema, async (a, ct) => await Revoke(c, a.Text("employeeId"), ct), RequiresApproval: true)], calls);
    }
    internal static JsonObject Args(string id) => new() { ["employeeId"] = id };
    static string Id(string id) => id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? id : throw new ArgumentException("Invalid employee ID.");
    static JsonArray Rows(JsonObject obj, string key) => obj[key] as JsonArray ?? throw new ArgumentException($"Missing array: {key}");
    static IEnumerable<JsonObject> Objects(JsonArray rows) => rows.Select(n => n as JsonObject ?? throw new ArgumentException("Every record must be a JSON object."));
    static JsonObject Employee(SampleContext c, string id) => Objects(Rows(c.Input, "employees")).Where(e => e.Text("id") == Id(id)).ToArray() is [var row] ? row : throw new ArgumentException($"Missing or ambiguous employee: {id}");
    static void ValidateRows(JsonArray rows)
    {
        var ids = Objects(rows).Select(n => n.Text("id")).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("Items need unique nonempty IDs.");
    }
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var e = Employee(c, id);
        _ = DateOnly.ParseExact(e.Text("departureDate"), "yyyy-MM-dd");
        foreach (var key in new[] { "access", "equipment", "handoffs", "obligations" }) ValidateRows(Rows(e, key));
        return (JsonObject)e.DeepClone();
    }
    static string Fingerprint(JsonObject e) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.ToJsonString())));
    internal static JsonObject Plan(SampleContext c, string id)
    {
        var e = Inspect(c, id); var revoked = new HashSet<string>(StringComparer.Ordinal); var path = $"revocations-{Id(id)}.json";
        if (File.Exists(c.ResolvePath(path)))
        {
            var prior = JsonNode.Parse(File.ReadAllText(c.ResolvePath(path)))!.AsObject();
            if (prior.Text("fingerprint") != Fingerprint(e)) throw new InvalidOperationException("Existing revocation ledger has different source records.");
            revoked.UnionWith(Rows(prior, "revokedAccessIds").Select(n => n!.GetValue<string>()));
        }
        var steps = new JsonArray();
        foreach (var access in Objects(Rows(e, "access")))
            steps.Add(new JsonObject { ["phase"] = 1, ["kind"] = "access", ["id"] = access.Text("id"), ["owner"] = "it", ["status"] = !access["active"]!.GetValue<bool>() || revoked.Contains(access.Text("id")) ? "revoked" : "approval-required", ["dependsOn"] = new JsonArray() });
        var accessPending = steps.Any(s => s!["status"]!.GetValue<string>() == "approval-required");
        foreach (var asset in Objects(Rows(e, "equipment")))
            steps.Add(new JsonObject { ["phase"] = 2, ["kind"] = "equipment", ["id"] = asset.Text("id"), ["owner"] = "facilities", ["status"] = asset["returned"]!.GetValue<bool>() ? "returned" : accessPending ? "blocked-by-access" : "awaiting-return", ["dependsOn"] = new JsonArray("access") });
        foreach (var handoff in Objects(Rows(e, "handoffs")))
        {
            if (string.IsNullOrWhiteSpace(handoff.Text("owner"))) throw new ArgumentException("Handoff needs a receiving owner.");
            steps.Add(new JsonObject { ["phase"] = 3, ["kind"] = "handoff", ["id"] = handoff.Text("id"), ["owner"] = handoff.Text("owner"), ["status"] = handoff["accepted"]!.GetValue<bool>() ? "accepted" : "awaiting-acceptance", ["dependsOn"] = new JsonArray("access") });
        }
        var departure = DateOnly.ParseExact(e.Text("departureDate"), "yyyy-MM-dd");
        var pending = new JsonArray(Objects(Rows(e, "obligations")).Where(o => !o["complete"]!.GetValue<bool>()).Select(o => {
            var result = (JsonObject)o.DeepClone(); result["daysAfterDeparture"] = DateOnly.ParseExact(o.Text("dueDate"), "yyyy-MM-dd").DayNumber - departure.DayNumber; return (JsonNode?)result;
        }).ToArray());
        return new() { ["employeeId"] = id, ["departureDate"] = e.Text("departureDate"), ["steps"] = steps, ["pendingObligations"] = pending, ["complete"] = steps.All(s => s!["status"]!.GetValue<string>() is "revoked" or "returned" or "accepted") && pending.Count == 0, ["scope"] = "workspace-only" };
    }
    internal static async Task<JsonObject> Revoke(SampleContext c, string id, CancellationToken ct = default)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required for local access revocation.");
        var e = Inspect(c, id); var path = $"revocations-{Id(id)}.json"; var hash = Fingerprint(e);
        if (File.Exists(c.ResolvePath(path)))
        {
            var prior = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath(path), ct))!.AsObject();
            if (prior.Text("fingerprint") != hash) throw new InvalidOperationException("Revocation replay conflicts with changed input.");
            return prior;
        }
        var manualPending = Rows(e, "equipment").Any(n => !n!["returned"]!.GetValue<bool>()) || Rows(e, "handoffs").Any(n => !n!["accepted"]!.GetValue<bool>()) || Rows(e, "obligations").Any(n => !n!["complete"]!.GetValue<bool>());
        var ledger = new JsonObject { ["employeeId"] = id, ["fingerprint"] = hash, ["revokedAccessIds"] = new JsonArray(Objects(Rows(e, "access")).Where(a => a["active"]!.GetValue<bool>()).Select(a => (JsonNode?)JsonValue.Create(a.Text("id"))).ToArray()), ["scope"] = "workspace-only", ["manualObligationsRemain"] = manualPending };
        await c.WriteJsonAsync(path, ledger, ct); return ledger;
    }
}
