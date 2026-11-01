// PiSharp-only (no upstream Pi source): evidence mapping and grant review packages.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.GrantApplicationOrganizer;

public static class Workflow
{
    private static JsonObject Schema() => SampleJson.Schema("{\"applicationId\":{\"type\":\"string\"}}", "applicationId");
    internal static JsonObject Args(string id) => new() { ["applicationId"] = id };
    public static WorkflowSpec Create(SampleContext c) => new("59", "Grant application organizer",
        "Inspect application requirements and supplied document evidence, validate completeness, then assemble a local review package. For the fixture use arts-2026. Report missing evidence honestly; never submit an application or infer eligibility.",
        [new("inspect_requirements", "Read grant requirements and available document metadata", Schema(), (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("applicationId")))),
         new("validate_completeness", "Map requirements to valid evidence and list missing or expired documents", Schema(), (a, _) => Task.FromResult<JsonNode?>(Validate(c, a.Text("applicationId")))),
         new("assemble_review_package", "Write a sourced local review manifest, including all gaps", Schema(), async (a, ct) =>
         {
             var id = a.Text("applicationId"); var report = Validate(c, id); var path = "grant-" + id + ".json";
             await c.WriteJsonAsync(path, report, ct); return new JsonObject { ["path"] = path, ["package"] = report };
         })], [new("inspect_requirements", Args("arts-2026")), new("validate_completeness", Args("arts-2026")), new("assemble_review_package", Args("arts-2026"))]);
    private static JsonObject Application(SampleContext c, string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Invalid application ID.");
        var matches = Rows(c.Input, "applications").Where(x => x.Text("id") == id).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Unknown or conflicting application ID.");
        foreach (var key in new[] { "requirements", "documents" })
        {
            var rows = Rows(matches[0], key);
            if (rows.Any(x => string.IsNullOrWhiteSpace(x.Text("id"))) || rows.Select(x => x.Text("id")).Distinct().Count() != rows.Length) throw new ArgumentException("Missing or duplicate " + key + " ID.");
            if (key == "requirements" && rows.Length == 0) throw new ArgumentException("An application needs a nonempty requirement inventory.");
        }
        foreach (var requirement in Rows(matches[0], "requirements"))
            if (string.IsNullOrWhiteSpace(requirement.Text("label")) || requirement["minCount"] is not JsonValue count || !count.TryGetValue<int>(out var minimum) || minimum < 1 ||
                requirement["acceptedKinds"] is not JsonArray kinds || kinds.Count == 0 || kinds.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out var kind) || string.IsNullOrWhiteSpace(kind)))
                throw new ArgumentException("Malformed grant requirement.");
        foreach (var document in Rows(matches[0], "documents"))
            if (string.IsNullOrWhiteSpace(document.Text("kind")) || document["requirementIds"] is not JsonArray links || links.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out var link) || string.IsNullOrWhiteSpace(link)))
                throw new ArgumentException("Malformed document references.");
        return matches[0];
    }
    private static JsonObject[] Rows(JsonObject parent, string key)
    {
        if (parent[key] is not JsonArray array || array.Any(x => x is not JsonObject)) throw new ArgumentException("Expected object rows in " + key);
        return array.Select(x => x!.AsObject()).ToArray();
    }
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var app = Application(c, id);
        return new() { ["applicationId"] = id, ["deadline"] = app["deadline"]!.DeepClone(), ["requirements"] = app["requirements"]!.DeepClone(), ["documents"] = app["documents"]!.DeepClone() };
    }
    internal static JsonObject Validate(SampleContext c, string id)
    {
        var app = Application(c, id); var deadline = DateOnly.ParseExact(app.Text("deadline"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var documents = app["documents"]!.AsArray().OfType<JsonObject>().ToArray(); var map = new JsonArray(); var gaps = new JsonArray(); var used = new HashSet<string>();
        foreach (var req in app["requirements"]!.AsArray().OfType<JsonObject>())
        {
            var rid = req.Text("id"); var count = req["minCount"]!.GetValue<int>();
            if (count < 1 || req["acceptedKinds"]!.AsArray().Count == 0) throw new ArgumentException("Requirement needs positive count and accepted kinds.");
            var linked = documents.Where(d => d["requirementIds"]!.AsArray().Any(x => x!.GetValue<string>() == rid)).ToArray();
            var evidence = new JsonArray(); var rejected = new JsonArray();
            foreach (var doc in linked)
            {
                var reason = !req["acceptedKinds"]!.AsArray().Any(x => x!.GetValue<string>() == doc.Text("kind")) ? "wrong-kind" :
                    string.IsNullOrWhiteSpace(doc.Text("evidence")) ? "missing-evidence" :
                    doc["expires"] is not null && DateOnly.ParseExact(doc.Text("expires"), "yyyy-MM-dd", CultureInfo.InvariantCulture) < deadline ? "expires-before-deadline" : "";
                if (reason.Length > 0) { rejected.Add(new JsonObject { ["documentId"] = doc.Text("id"), ["reason"] = reason }); continue; }
                used.Add(doc.Text("id"));
                evidence.Add(new JsonObject { ["documentId"] = doc.Text("id"), ["evidence"] = doc.Text("evidence"), ["kind"] = doc.Text("kind") });
            }
            var missing = Math.Max(0, count - evidence.Count);
            map.Add(new JsonObject { ["requirementId"] = rid, ["label"] = req.Text("label"), ["evidence"] = evidence, ["rejected"] = rejected, ["missingCount"] = missing });
            if (missing > 0) gaps.Add(new JsonObject { ["requirementId"] = rid, ["missingCount"] = missing, ["action"] = "Obtain " + missing + " valid " + req.Text("label") + " document(s)." });
        }
        var requirementIds = app["requirements"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToHashSet();
        var unknownLinks = documents.SelectMany(d => d["requirementIds"]!.AsArray().Select(r => (Document: d.Text("id"), Requirement: r!.GetValue<string>()))).Where(x => !requirementIds.Contains(x.Requirement)).ToArray();
        foreach (var link in unknownLinks) gaps.Add(new JsonObject { ["documentId"] = link.Document, ["requirementId"] = link.Requirement, ["action"] = "Correct unknown requirement reference." });
        return new() { ["applicationId"] = id, ["deadline"] = app.Text("deadline"), ["status"] = gaps.Count == 0 ? "ready-for-human-review" : "incomplete", ["requirementMap"] = map, ["gaps"] = gaps, ["unusedDocumentIds"] = new JsonArray(documents.Where(d => !used.Contains(d.Text("id"))).Select(d => (JsonNode?)JsonValue.Create(d.Text("id"))).ToArray()), ["submitted"] = false };
    }
}
