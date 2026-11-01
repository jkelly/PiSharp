// PiSharp-only (no upstream Pi source): evidence-based local access review and guarded removals.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.AccessReviewAssistant;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"employeeId\":{\"type\":\"string\"}}", "employeeId"); var id = c.Input.Text("employeeId");
        var calls = new List<PlannedCall> { new("inspect_access", Args(id)), new("review_access", Args(id)), new("save_access_review", Args(id)) };
        if (c.Approved) calls.Add(new("remove_local_access", Args(id)));
        return new("36", "Access Review Assistant",
            "Inspect the requested employee's grants, review_access against role policy, grant expiry and approved exceptions at the supplied asOf date, then save_access_review. If approved, remove_local_access records only recommended removals in the local workspace. Never claim an external account was changed.",
            [new("inspect_access", "Inspect grants and the unique role policy for an employee.", schema, (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("employeeId")))),
             new("review_access", "Determine retention/removal with explicit evidence and exception expiry.", schema, (a, _) => Task.FromResult<JsonNode?>(Review(c, a.Text("employeeId")))),
             new("save_access_review", "Write the computed access review artifact.", schema, async (a, ct) => { var id = Id(a.Text("employeeId")); var review = Review(c, id); var path = $"access-review-{id}.json"; await c.WriteJsonAsync(path, review, ct); return new JsonObject { ["artifact"] = path, ["review"] = review }; }),
             new("remove_local_access", "Approval-gated local removal of policy violations and expired grants.", schema, async (a, ct) => await Remove(c, a.Text("employeeId"), ct), RequiresApproval: true)], calls);
    }
    internal static JsonObject Args(string id) => new() { ["employeeId"] = id };
    static string Id(string id) => id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? id : throw new ArgumentException("Invalid employee ID.");
    static JsonArray Rows(JsonObject obj, string key) => obj[key] as JsonArray ?? throw new ArgumentException($"Missing array: {key}");
    static IEnumerable<JsonObject> Objects(JsonArray rows) => rows.Select(n => n as JsonObject ?? throw new ArgumentException("Every record must be a JSON object."));
    static JsonObject One(JsonArray rows, string id) => Objects(rows).Where(n => n.Text("id") == id).ToArray() is [var row] ? row : throw new ArgumentException($"Missing or ambiguous ID: {id}");
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var employee = One(Rows(c.Input, "employees"), Id(id)); var role = One(Rows(c.Input, "roles"), employee.Text("roleId"));
        var grants = Rows(employee, "grants");
        if (Objects(grants).Select(n => n.Text("id")).Distinct(StringComparer.Ordinal).Count() != grants.Count) throw new ArgumentException("Ambiguous grant IDs.");
        return new() { ["employee"] = employee.DeepClone(), ["policy"] = role.DeepClone(), ["asOf"] = c.Input.Text("asOf") };
    }
    static string Fingerprint(JsonObject data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString())));
    internal static JsonObject Review(SampleContext c, string id)
    {
        var data = Inspect(c, id); var employee = data["employee"]!.AsObject(); var policy = data["policy"]!.AsObject();
        var asOf = DateOnly.ParseExact(data.Text("asOf"), "yyyy-MM-dd"); var allowed = Rows(policy, "allowedResources").Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var exceptions = Objects(Rows(employee, "exceptions")).ToArray(); var grants = Rows(employee, "grants");
        if (exceptions.GroupBy(e => e.Text("grantId"), StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new ArgumentException("Multiple exceptions for a grant are ambiguous.");
        if (exceptions.Any(e => !grants.Any(g => g!.AsObject().Text("id") == e.Text("grantId")))) throw new ArgumentException("Exception references unknown grant.");
        var removed = new HashSet<string>(StringComparer.Ordinal); var path = $"access-removals-{Id(id)}.json";
        if (File.Exists(c.ResolvePath(path)))
        {
            var prior = JsonNode.Parse(File.ReadAllText(c.ResolvePath(path)))!.AsObject();
            if (prior.Text("fingerprint") != Fingerprint(data)) throw new InvalidOperationException("Removal ledger conflicts with source records.");
            removed.UnionWith(Rows(prior, "removedGrantIds").Select(n => n!.GetValue<string>()));
        }
        var decisions = new JsonArray();
        foreach (var grant in Objects(grants))
        {
            var grantId = grant.Text("id"); if (grantId.Length == 0) throw new ArgumentException("Grant ID required.");
            var expired = DateOnly.ParseExact(grant.Text("expiresOn"), "yyyy-MM-dd") <= asOf;
            var exception = exceptions.SingleOrDefault(e => e.Text("grantId") == grantId);
            var validException = exception is not null && exception.Text("approvedBy").Length > 0 && DateOnly.ParseExact(exception.Text("validUntil"), "yyyy-MM-dd") > asOf;
            var inRole = allowed.Contains(grant.Text("resource"));
            var reason = expired ? "grant-expired" : inRole ? "role-policy" : validException ? "approved-exception" : exception is null ? "outside-role" : "exception-expired-or-unapproved";
            var decision = removed.Contains(grantId) ? "removed" : !grant["active"]!.GetValue<bool>() ? "inactive" : expired || (!inRole && !validException) ? "remove" : "retain";
            decisions.Add(new JsonObject { ["grantId"] = grantId, ["resource"] = grant.Text("resource"), ["decision"] = decision, ["reason"] = reason, ["policyId"] = policy.Text("id"), ["exceptionId"] = exception?.Text("id"), ["expiresOn"] = grant.Text("expiresOn") });
        }
        return new() { ["employeeId"] = id, ["asOf"] = data.Text("asOf"), ["decisions"] = decisions, ["recommendedRemovalCount"] = decisions.Count(n => n!["decision"]!.GetValue<string>() == "remove"), ["scope"] = "workspace-only" };
    }
    internal static async Task<JsonObject> Remove(SampleContext c, string id, CancellationToken ct = default)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required for local access removal.");
        var data = Inspect(c, id); var hash = Fingerprint(data); var path = $"access-removals-{Id(id)}.json";
        if (File.Exists(c.ResolvePath(path)))
        {
            var prior = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath(path), ct))!.AsObject();
            if (prior.Text("fingerprint") != hash) throw new InvalidOperationException("Removal replay conflicts with changed input.");
            return prior;
        }
        var review = Review(c, id); var ids = review["decisions"]!.AsArray().OfType<JsonObject>().Where(d => d.Text("decision") == "remove").Select(d => (JsonNode?)JsonValue.Create(d.Text("grantId"))).ToArray();
        var ledger = new JsonObject { ["employeeId"] = id, ["fingerprint"] = hash, ["removedGrantIds"] = new JsonArray(ids), ["scope"] = "workspace-only" };
        await c.WriteJsonAsync(path, ledger, ct); return ledger;
    }
}
