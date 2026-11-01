// PiSharp-only (no upstream Pi source): evidence-bound RFP response drafting.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.RfpResponseBuilder;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("39-RfpResponseBuilder", "RFP Response Builder",
        "Inspect the requested RFP, match requirements to current approved evidence, then save the grounded response draft. Cite source IDs per claim. Unsupported claims and missing/expired evidence must remain explicit gaps; do not invent compliance or send the response.",
        [new("inspect_rfp", "Read the requested RFP's requirements and available evidence IDs.", Schema(), (a,t) => Task.FromResult<JsonNode?>(Inspect(c,a))),
         new("match_requirements", "Match exact capability keys to approved unexpired evidence and flag unsupported draft claims.", Schema(), (a,t) => Task.FromResult<JsonNode?>(Match(c,a))),
         new("save_response_draft", "Persist an evidence-cited workspace draft, not an external submission.", Schema(), (a,t) => SaveAsync(c,a,t))],
        [new("inspect_rfp", Args("RFP-ORBIT")),new("match_requirements",Args("RFP-ORBIT")),new("save_response_draft",Args("RFP-ORBIT"))]);
    static JsonObject Schema() => SampleJson.Schema("""{"rfpId":{"type":"string"}}""","rfpId");
    static JsonObject Args(string id) => new() { ["rfpId"] = id };
    static JsonObject? Find(SampleContext c, JsonObject a) => Data.Find(c.Input["rfps"],Data.Id(a,"rfpId"));
    static JsonNode Inspect(SampleContext c, JsonObject a)
    {
        var rfp = Find(c,a);
        if (rfp is null) return Data.Missing(Data.Id(a,"rfpId"));
        return Data.Json(new { found = true, rfp = rfp.DeepClone(),
            evidenceIds = Data.Rows(c.Input["evidence"]).Select(x => Data.Text(x,"id")).ToArray() });
    }
    static JsonNode Match(SampleContext c, JsonObject a)
    {
        var rfp = Find(c,a);
        if (rfp is null) return Data.Missing(Data.Id(a,"rfpId"));
        var now = Data.Time(c.Input,"asOf");
        var evidence = Data.Rows(c.Input["evidence"]).ToArray();
        if (evidence.GroupBy(x => Data.Text(x,"id")).Any(g => g.Count() > 1)) throw new InvalidOperationException("Ambiguous evidence ID.");
        var requirements = Data.Rows(rfp["requirements"]).ToArray();
        if (requirements.GroupBy(x => Data.Text(x,"id")).Any(g => g.Count() > 1)) throw new InvalidOperationException("Ambiguous requirement ID.");
        var rows = requirements.Select(r =>
        {
            var key = Data.Text(r,"capabilityKey");
            var relevant = evidence.Where(e => Data.Text(e,"capabilityKey") == key).ToArray();
            var valid = relevant.Where(e => e["approved"]?.GetValue<bool>() == true && Data.Time(e,"validUntil") >= now).OrderBy(e => Data.Text(e,"id"),StringComparer.Ordinal).ToArray();
            return new { requirementId = Data.Text(r,"id"), requirement = Data.Text(r,"text"), mandatory = r["mandatory"]!.GetValue<bool>(),
                status = valid.Length > 0 ? "supported" : "missing_evidence",
                response = valid.Length > 0 ? string.Join(" ",valid.Select(e => Data.Text(e,"statement") + " [" + Data.Text(e,"id") + "]")) : "Evidence required; compliance is not claimed.",
                sourceIds = valid.Select(e => Data.Text(e,"id")).ToArray(),
                rejectedSourceIds = relevant.Except(valid).Select(e => Data.Text(e,"id")).ToArray() };
        }).ToArray();
        var unsupported = Data.Rows(rfp["draftClaims"]).Where(claim =>
        {
            var e = evidence.SingleOrDefault(x => Data.Text(x,"id") == Data.Text(claim,"sourceId"));
            var requirement = requirements.SingleOrDefault(x => Data.Text(x,"id") == Data.Text(claim,"requirementId"));
            return e is null || requirement is null || e["approved"]?.GetValue<bool>() != true || Data.Time(e,"validUntil") < now ||
                Data.Text(e,"capabilityKey") != Data.Text(requirement,"capabilityKey") || Data.Text(e,"statement") != Data.Text(claim,"text");
        }).Select(x => new { text = Data.Text(x,"text"), sourceId = Data.Text(x,"sourceId"), reason = "Claim is not the exact approved current evidence statement for this requirement." }).ToArray();
        return Data.Json(new { found = true, rfpId = Data.Text(rfp,"id"), requirements = rows,
            supportedCount = rows.Count(x => x.status == "supported"), missingMandatoryCount = rows.Count(x => x.mandatory && x.status != "supported"),
            unsupportedClaims = unsupported, readyForHumanReview = true, readyToSubmit = rows.All(x => !x.mandatory || x.status == "supported") && unsupported.Length == 0 });
    }
    static async Task<JsonNode?> SaveAsync(SampleContext c, JsonObject a, CancellationToken token)
    {
        var draft = Match(c,a);
        if (draft["found"]!.GetValue<bool>() != true) return draft;
        var path = "rfp/" + Data.Id(a,"rfpId") + ".json";
        await c.WriteJsonAsync(path,draft,token);
        return Data.Json(new { saved = true, path, draft, submitted = false });
    }
}
