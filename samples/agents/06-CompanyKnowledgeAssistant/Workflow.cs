// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.CompanyKnowledgeAssistant;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("company-knowledge-assistant", "Company knowledge assistant",
        "Search local source passages. Ground every answer in a citation with document ID, revision and section. State when no passage supports an answer.",
        [
            new("search_passages", "Search company document passages by words in a query.", SampleJson.Schema("{\"query\":{\"type\":\"string\"}}", "query"),
                (a, _) => Task.FromResult<JsonNode?>(Search(context, a))),
            new("get_passage", "Retrieve an exact passage by document ID and section.", SampleJson.Schema("{\"document_id\":{\"type\":\"string\"},\"section\":{\"type\":\"string\"}}", "document_id", "section"),
                (a, _) => Task.FromResult<JsonNode?>(Get(context, a)))
        ],
        [new("search_passages", SampleJson.Object("{\"query\":\"hotel approval\"}")),
         new("get_passage", SampleJson.Object("{\"document_id\":\"doc-601\",\"section\":\"Travel\"}"))]);
    static JsonObject Citation(JsonObject doc, JsonObject passage) => new()
    {
        ["document_id"] = doc["id"]?.DeepClone(), ["title"] = doc["title"]?.DeepClone(),
        ["revision"] = doc["revision"]?.DeepClone(), ["section"] = passage["section"]?.DeepClone(),
        ["text"] = passage["text"]?.DeepClone(),
        ["citation"] = $"{doc.Text("id")}@{doc.Text("revision")}#{passage.Text("section")}"
    };
    static JsonObject Search(SampleContext c, JsonObject a)
    {
        var terms = a.Text("query").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) throw new ArgumentException("Query required");
        var hits = new List<(int Score, JsonObject Item)>();
        foreach (var node in c.Input["documents"]?.AsArray() ?? throw new InvalidOperationException("documents missing"))
        {
            var doc = node!.AsObject();
            foreach (var passageNode in doc["passages"]?.AsArray() ?? [])
            {
                var passage = passageNode!.AsObject();
                var haystack = $"{doc.Text("title")} {passage.Text("section")} {passage.Text("text")}";
                var score = terms.Count(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (score > 0) hits.Add((score, Citation(doc, passage)));
            }
        }
        return new JsonObject { ["query"] = a.Text("query"),
            ["hits"] = new JsonArray(hits.OrderByDescending(h => h.Score).ThenBy(h => h.Item.Text("citation"), StringComparer.Ordinal).Select(h => (JsonNode?)h.Item).ToArray()) };
    }
    static JsonObject Get(SampleContext c, JsonObject a)
    {
        var doc = c.Input["documents"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("document_id"))?.AsObject()
            ?? throw new ArgumentException("Unknown document");
        var passage = doc["passages"]?.AsArray().FirstOrDefault(x => x?["section"]?.GetValue<string>() == a.Text("section"))?.AsObject()
            ?? throw new ArgumentException("Unknown section");
        return Citation(doc, passage);
    }
}
