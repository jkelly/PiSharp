// PiSharp-only (no upstream Pi source): source-grounded knowledge coverage workflow.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.KnowledgeGapFinder;

public static class Workflow
{
    private static JsonObject Schema() => SampleJson.Schema("{\"corpusId\":{\"type\":\"string\"}}", "corpusId");
    public static WorkflowSpec Create(SampleContext c) => new("58", "Knowledge gap finder",
        "Inspect unresolved questions and unsuccessful searches, compare topic keywords against article text, then save sourced article tasks. Use corpusId support-q4 for the fixture. Coverage is lexical evidence, not a claim of semantic completeness.",
        [new("inspect_signals", "Read unresolved source signals and topic definitions for a corpus", Schema(), (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("corpusId")))),
         new("analyze_coverage", "Cluster signals by topic and measure existing article keyword coverage", Schema(), (a, _) => Task.FromResult<JsonNode?>(Analyze(c, a.Text("corpusId")))),
         new("save_article_tasks", "Persist a local review backlog with source IDs and missing coverage", Schema(), async (a, ct) =>
         {
             var id = a.Text("corpusId"); var report = Analyze(c, id); var path = "knowledge-" + Safe(id) + ".json";
             await c.WriteJsonAsync(path, report, ct); return new JsonObject { ["path"] = path, ["report"] = report };
         })], [new("inspect_signals", Args("support-q4")), new("analyze_coverage", Args("support-q4")), new("save_article_tasks", Args("support-q4"))]);

    internal static JsonObject Args(string id) => new() { ["corpusId"] = id };
    private static string Safe(string id) => Regex.IsMatch(id, "^[A-Za-z0-9_-]+$") ? id : throw new ArgumentException("Invalid corpus ID.");
    private static JsonObject Corpus(SampleContext c, string id)
    {
        Safe(id);
        var rows = Rows(c.Input, "corpora").Where(x => x.Text("id") == id).ToArray();
        if (rows.Length != 1) throw new ArgumentException("Unknown or conflicting corpus ID.");
        var corpus = rows[0];
        foreach (var key in new[] { "signals", "topics", "articles" })
        {
            var items = Rows(corpus, key);
            if (items.Any(x => string.IsNullOrWhiteSpace(x.Text("id"))) || items.Select(x => x.Text("id")).Distinct().Count() != items.Length)
                throw new ArgumentException("Missing or duplicate source ID in " + key);
            if (key is "signals" or "articles")
                foreach (var item in items)
                    if (item["text"] is not JsonValue value || !value.TryGetValue<string>(out _))
                        throw new ArgumentException("Every " + key + " source must explicitly supply string-valued text.");
        }
        return corpus;
    }
    private static JsonObject[] Rows(JsonObject parent, string key)
    {
        if (parent[key] is not JsonArray array || array.Any(x => x is not JsonObject)) throw new ArgumentException("Expected object source rows in " + key);
        return array.Select(x => x!.AsObject()).ToArray();
    }
    private static IEnumerable<JsonObject> Unresolved(JsonObject corpus) => corpus["signals"]!.AsArray().OfType<JsonObject>().Where(s =>
        s.Text("kind") switch { "question" => !s["answered"]!.GetValue<bool>(), "search" => s["resultCount"]!.GetValue<int>() == 0, _ => throw new ArgumentException("Unsupported signal kind.") });
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var corpus = Corpus(c, id);
        return new() { ["corpusId"] = id, ["signals"] = new JsonArray(Unresolved(corpus).Select(s => s.DeepClone()).ToArray()), ["topics"] = corpus["topics"]!.DeepClone(), ["articleCount"] = corpus["articles"]!.AsArray().Count };
    }
    private static HashSet<string> Words(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
    internal static JsonObject Analyze(SampleContext c, string id)
    {
        var corpus = Corpus(c, id); var topics = corpus["topics"]!.AsArray().OfType<JsonObject>().ToArray();
        var clusters = topics.ToDictionary(t => t.Text("id"), _ => new List<JsonObject>(), StringComparer.Ordinal);
        var unmatched = new JsonArray();
        foreach (var signal in Unresolved(corpus))
        {
            var words = Words(signal.Text("text"));
            var scores = topics.Select(t => (Topic: t, Score: t["keywords"]!.AsArray().Count(k => words.Contains(k!.GetValue<string>().ToLowerInvariant())))).ToArray();
            var best = scores.Length == 0 ? 0 : scores.Max(s => s.Score);
            var winners = scores.Where(s => s.Score == best && best > 0).ToArray();
            // Tied topics retain provenance and explicitly ask for manual classification.
            if (winners.Length != 1) { unmatched.Add(new JsonObject { ["sourceId"] = signal.Text("id"), ["reason"] = best == 0 ? "no-keyword-match" : "ambiguous-topic" }); continue; }
            clusters[winners[0].Topic.Text("id")].Add(signal);
        }
        var coverage = new JsonArray(); var tasks = new JsonArray();
        foreach (var topic in topics)
        {
            var topicId = topic.Text("id"); var signals = clusters[topicId];
            if (signals.Count == 0) continue;
            var keywords = topic["keywords"]!.AsArray().Select(k => k!.GetValue<string>().ToLowerInvariant()).Distinct().ToArray();
            if (keywords.Length == 0) throw new ArgumentException("A topic needs coverage keywords.");
            var articles = corpus["articles"]!.AsArray().OfType<JsonObject>().Where(a => a["topicIds"]!.AsArray().Any(t => t!.GetValue<string>() == topicId)).ToArray();
            var covered = articles.SelectMany(a => Words(a.Text("text"))).ToHashSet();
            var missing = keywords.Where(k => !covered.Contains(k)).ToArray();
            var row = new JsonObject { ["topicId"] = topicId, ["signalIds"] = new JsonArray(signals.Select(s => (JsonNode?)JsonValue.Create(s.Text("id"))).ToArray()), ["articleIds"] = new JsonArray(articles.Select(a => (JsonNode?)JsonValue.Create(a.Text("id"))).ToArray()), ["missingKeywords"] = new JsonArray(missing.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()), ["coveragePercent"] = (keywords.Length - missing.Length) * 100 / keywords.Length };
            coverage.Add(row);
            if (missing.Length > 0) tasks.Add(new JsonObject { ["taskId"] = "article-" + topicId, ["title"] = "Document " + topic.Text("label"), ["sourceIds"] = row["signalIds"]!.DeepClone(), ["existingArticleIds"] = row["articleIds"]!.DeepClone(), ["requiredCoverage"] = row["missingKeywords"]!.DeepClone(), ["questionsToAddress"] = new JsonArray(signals.Select(s => (JsonNode?)JsonValue.Create(s.Text("text"))).ToArray()), ["status"] = "draft-for-review" });
        }
        return new() { ["corpusId"] = id, ["coverage"] = coverage, ["articleTasks"] = tasks, ["unclassifiedSignals"] = unmatched, ["method"] = "Exact normalized word coverage within declared article topics; editorial review required." };
    }
}
