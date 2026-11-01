// PiSharp-only (no upstream Pi source): metadata evidence inventory and receipt reconciliation.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.InsuranceClaimOrganizer;

public static class Workflow
{
    private static JsonObject Schema() => SampleJson.Schema("{\"claimId\":{\"type\":\"string\"}}", "claimId");
    internal static JsonObject Args(string id) => new() { ["claimId"] = id };
    public static WorkflowSpec Create(SampleContext c) => new("62", "Insurance claim organizer",
        "Inventory photo, receipt and correspondence metadata; reconcile duplicate receipts and integer-cent totals; assemble a sourced local manifest. For the fixture use claim-1. Surface missing evidence and conflicts. Do not claim to inspect photo pixels, verify authenticity, file a claim, decide coverage or provide legal advice.",
        [new("inventory_evidence", "Inventory supplied evidence metadata, keeping source IDs and missing fields", Schema(), (a, _) => Task.FromResult<JsonNode?>(Inventory(c, a.Text("claimId")))),
         new("reconcile_receipts", "Deduplicate receipts by issuer and number, reject conflicting totals, reconcile claimed cents", Schema(), (a, _) => Task.FromResult<JsonNode?>(Reconcile(c, a.Text("claimId")))),
         new("assemble_claim_manifest", "Persist sourced evidence, deduplicated totals and outstanding requirements", Schema(), async (a, ct) =>
         {
             var id = a.Text("claimId"); var claim = Claim(c, id); var inventory = Inventory(c, id); var reconciliation = Reconcile(c, id); var missing = inventory["missingRequirements"]!.AsArray();
             var manifest = new JsonObject { ["claimId"] = id, ["lossDate"] = claim.Text("lossDate"), ["inventory"] = inventory, ["reconciliation"] = reconciliation, ["status"] = missing.Count == 0 && reconciliation["balanced"]!.GetValue<bool>() ? "ready-for-human-review" : "needs-evidence-review", ["filed"] = false, ["scope"] = "Metadata organization only; no coverage determination or legal advice." };
             var path = "claim-" + id + ".json"; await c.WriteJsonAsync(path, manifest, ct);
             return new JsonObject { ["path"] = path, ["manifest"] = manifest };
         })], [new("inventory_evidence", Args("claim-1")), new("reconcile_receipts", Args("claim-1")), new("assemble_claim_manifest", Args("claim-1"))]);
    private static JsonObject Claim(SampleContext c, string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Invalid claim ID.");
        var claims = Rows(c.Input, "claims").Where(x => x.Text("id") == id).ToArray();
        if (claims.Length != 1) throw new ArgumentException("Unknown or conflicting claim ID.");
        var claim = claims[0]; var items = Rows(claim, "evidence");
        if (items.Any(x => string.IsNullOrWhiteSpace(x.Text("id"))) || items.Select(x => x.Text("id")).Distinct().Count() != items.Length) throw new ArgumentException("Duplicate or missing evidence ID.");
        if (Amount(claim["claimedAmountCents"]) < 0) throw new ArgumentException("Claimed amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(claim.Text("currency")) || Rows(claim, "requirements").Length == 0) throw new ArgumentException("Currency and evidence requirements are required.");
        DateOnly.ParseExact(claim.Text("lossDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return claim;
    }
    private static JsonObject[] Rows(JsonObject parent, string key)
    {
        if (parent[key] is not JsonArray array || array.Any(x => x is not JsonObject)) throw new ArgumentException("Expected object rows in " + key);
        return array.Select(x => x!.AsObject()).ToArray();
    }
    private static string[] MetadataProblems(JsonObject item)
    {
        var missing = new List<string>();
        foreach (var name in new[] { "fileName", "date", "description" }) if (string.IsNullOrWhiteSpace(item.Text(name))) missing.Add(name);
        if (item.Text("date").Length > 0 && !DateOnly.TryParseExact(item.Text("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) missing.Add("valid-date");
        if (item.Text("kind") == "receipt")
            foreach (var name in new[] { "issuer", "receiptNumber", "currency" }) if (string.IsNullOrWhiteSpace(item.Text(name))) missing.Add(name);
        if (item.Text("kind") is not ("photo" or "receipt" or "correspondence" or "policy")) missing.Add("supported-kind");
        return missing.ToArray();
    }
    internal static JsonObject Inventory(SampleContext c, string id)
    {
        var claim = Claim(c, id); var items = claim["evidence"]!.AsArray().OfType<JsonObject>().ToArray(); var missing = new JsonArray(); var inventory = new JsonArray();
        foreach (var item in items)
        {
            var problems = MetadataProblems(item);
            inventory.Add(new JsonObject { ["sourceId"] = item.Text("id"), ["kind"] = item.Text("kind"), ["fileName"] = item.Text("fileName"), ["date"] = item.Text("date"), ["description"] = item.Text("description"), ["missingMetadata"] = new JsonArray(problems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) });
            if (problems.Length > 0) missing.Add(new JsonObject { ["sourceId"] = item.Text("id"), ["action"] = "Supply complete metadata.", ["fields"] = new JsonArray(problems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) });
        }
        foreach (var requirement in claim["requirements"]!.AsArray().OfType<JsonObject>())
        {
            var kind = requirement.Text("kind"); var minimum = requirement["minCount"]!.GetValue<int>();
            if (minimum < 1) throw new ArgumentException("Evidence requirement count must be positive.");
            var count = kind == "receipt" ? Reconcile(c, id)["receipts"]!.AsArray().Count : items.Count(x => x.Text("kind") == kind && MetadataProblems(x).Length == 0);
            if (count < minimum) missing.Add(new JsonObject { ["kind"] = kind, ["missingCount"] = minimum - count, ["action"] = "Obtain required " + kind + " evidence." });
        }
        return new() { ["claimId"] = id, ["evidence"] = inventory, ["missingRequirements"] = missing, ["contentInspected"] = false };
    }
    internal static JsonObject Reconcile(SampleContext c, string id)
    {
        var claim = Claim(c, id); var receipts = claim["evidence"]!.AsArray().OfType<JsonObject>().Where(x => x.Text("kind") == "receipt").ToArray();
        var lines = new JsonArray(); var issues = new JsonArray(); long total = 0;
        // Receipt identity is issuer + receipt number; raw source IDs remain available for audit.
        foreach (var group in receipts.GroupBy(x => (x.Text("issuer").Trim().ToUpperInvariant(), x.Text("receiptNumber").Trim().ToUpperInvariant())))
        {
            var sources = group.OrderBy(x => x.Text("id"), StringComparer.Ordinal).ToArray(); var first = sources[0];
            var sourceIds = new JsonArray(sources.Select(x => (JsonNode?)JsonValue.Create(x.Text("id"))).ToArray());
            var invalid = sources.Any(x => MetadataProblems(x).Length > 0 || !TryAmount(x["amountCents"], out var amount) || amount < 0 || x.Text("currency") != claim.Text("currency"));
            var conflicting = !invalid && sources.Any(x => Amount(x["amountCents"]) != Amount(first["amountCents"]) || x.Text("date") != first.Text("date"));
            if (invalid || conflicting)
            {
                issues.Add(new JsonObject { ["sourceIds"] = sourceIds, ["reason"] = invalid ? "invalid-or-incomplete-receipt" : "conflicting-duplicate-receipt", ["includedInTotal"] = false }); continue;
            }
            var cents = Amount(first["amountCents"]); total = checked(total + cents);
            lines.Add(new JsonObject { ["canonicalSourceId"] = first.Text("id"), ["sourceIds"] = sourceIds, ["issuer"] = first.Text("issuer"), ["receiptNumber"] = first.Text("receiptNumber"), ["amountCents"] = cents, ["duplicateCount"] = sources.Length - 1 });
        }
        var claimed = Amount(claim["claimedAmountCents"]);
        return new() { ["claimId"] = id, ["currency"] = claim.Text("currency"), ["receipts"] = lines, ["issues"] = issues, ["receiptTotalCents"] = total, ["claimedAmountCents"] = claimed, ["differenceCents"] = checked(claimed - total), ["balanced"] = total == claimed && issues.Count == 0 };
    }
    private static bool TryAmount(JsonNode? value, out long cents) => long.TryParse(value?.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cents);
    private static long Amount(JsonNode? value) => TryAmount(value, out var cents) ? cents : throw new ArgumentException("Amount must be integer cents.");
}
