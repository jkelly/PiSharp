// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.DocumentChangeExplainer;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("document-change-explainer", "Document change explainer",
        "Read both document versions, compare their named sections and explain substantive changes with old and new evidence.",
        [
            new("read_versions", "Read old and new version metadata and sections.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Document(context, a).DeepClone())),
            new("compare_sections", "Report added, removed and changed sections.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Compare(context, a)))
        ],
        [new("read_versions", SampleJson.Object("{\"id\":\"policy-701\"}")), new("compare_sections", SampleJson.Object("{\"id\":\"policy-701\"}"))]);
    static JsonObject Document(SampleContext c, JsonObject a) => c.Input["documents"]?.AsArray()
        .FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))?.AsObject()
        ?? throw new ArgumentException("Unknown document");
    static JsonObject Compare(SampleContext c, JsonObject a)
    {
        var doc = Document(c, a);
        var oldVersion = doc["old"]!.AsObject(); var newVersion = doc["new"]!.AsObject();
        var oldSections = oldVersion["sections"]!.AsObject(); var newSections = newVersion["sections"]!.AsObject();
        var changes = new JsonArray();
        foreach (var name in oldSections.Select(p => p.Key).Union(newSections.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var before = oldSections[name]?.GetValue<string>(); var after = newSections[name]?.GetValue<string>();
            if (before == after) continue;
            changes.Add(new JsonObject { ["section"] = name, ["kind"] = before is null ? "added" : after is null ? "removed" : "changed",
                ["before"] = before, ["after"] = after });
        }
        return new JsonObject { ["document_id"] = doc["id"]?.DeepClone(), ["old_version"] = oldVersion["version"]?.DeepClone(),
            ["new_version"] = newVersion["version"]?.DeepClone(), ["changes"] = changes };
    }
}
