using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Discovery;

// tool-search/tool.ts: tokenize, createToolSearchDocument, Bm25Ranker, the tool's schema, description, result text and input
// errors. The corpus and expectations of the first three cases are test/tool-search.test.ts ("tokenize", "Bm25Ranker").
internal static partial class Program
{
    private static ToolSearchDocument Document(string name, string description, string properties = "{}", ToolNamespace? group = null)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + properties + "}");
        return ToolSearch.CreateDocument(name, description, schema.RootElement, group);
    }

    private static readonly ToolSearchDocument[] Corpus =
    [
        Document("mcp__github__list_issues", "List issues in a repository.", """{"state":{"type":"string","description":"open or closed"}}"""),
        Document("mcp__github__create_pull_request", "Open a pull request."),
        Document("mcp__linear__search_issues", "Search Linear issues by text."),
        Document("mcp__docs__search", "Search the documentation.")
    ];

    private static IEnumerable<string> Ranked(string query, int limit = 8) => new Bm25Ranker().Rank(query, Corpus, limit).Select(match => match.Name);

    private static void Tokenize()
    {
        Names(["list", "issue", "git", "hub", "repo"], ToolSearch.Tokenize("listIssues for the GitHub_repo"), "camelCase, snake_case, stop words, plural");
        Names(["search", "query", "http", "server"], ToolSearch.Tokenize("searches queries HTTPServer"), "sibilant and -ies plurals, acronyms");
        // The stems: -ies only above four characters, sibilant -es, a plain -s but not -ss, and nothing at three characters.
        Names(["tie", "class", "box", "bus", "gas", "item", "ys"], ToolSearch.Tokenize("ties classes boxes bus gas items ys"), "stem lengths");
        Names([], ToolSearch.Tokenize(" -- the a__an "), "only stop words and separators");
        Names(["caf", "x2", "v"], ToolSearch.Tokenize("café x2-v"), "non-ASCII letters separate terms");
    }

    private static void RanksCorpus()
    {
        Names(["mcp__linear__search_issues", "mcp__github__list_issues"], Ranked("issue"), "issue");
        Equal("mcp__github__create_pull_request", Ranked("pull requests").First(), "pull requests");
        Equal(1, Ranked("search", 1).Count(), "limit");
        // Property names and descriptions are searchable.
        Names(["mcp__github__list_issues"], Ranked("closed"), "closed");
        Names([], Ranked("issue", 0), "zero limit");
    }

    private static void FindsNothing()
    {
        Names([], Ranked("kubernetes"), "unknown term");
        Names([], Ranked("the"), "stop word");
        // Known v1 limit: no synonyms, so "tickets" does not find "issues".
        Names([], Ranked("tickets"), "synonym");
        Names([], new Bm25Ranker().Rank("issue", [], 8).Select(match => match.Name), "no documents");
    }

    private static void DocumentsAndScores()
    {
        // The namespace name, description and instructions are part of the text.
        var namespaced = Document("mcp__x__run", "Run it.", group: new("mcp__x", "Kubernetes cluster tools"));
        var match = new Bm25Ranker().Rank("kubernetes", [namespaced], 8).Single();
        Check(match.Name == "mcp__x__run" && match.Score > 0, "namespace description searchable");
        Equal("mcp__x__run mcp  x  run Run it. mcp__x Kubernetes cluster tools", namespaced.Text, "namespace document text (blank parts dropped)");
        Equal("mcp__github__list_issues mcp  github  list issues List issues in a repository. state open or closed", Corpus[0].Text, "document text");
        var nested = Document("t", "", """{"filters":{"type":"array","items":{"anyOf":[{"description":"by label"},{"type":"object","properties":{"assignee":{"description":"user login"}}}]}}}""",
            new ToolNamespace("mcp__t") { Instructions = "Use carefully." });
        Equal("t t filters by label assignee user login mcp__t Use carefully.", nested.Text, "items, anyOf, nested properties and instructions");
        // Okapi BM25 with k1 = 1.2 and b = 0.75: one term in a two-term document of a two-document corpus (average length 1.5).
        var scored = new Bm25Ranker().Rank("alpha", [new("a", "alpha beta"), new("b", "gamma")], 8).Single();
        var expected = Math.Log(1 + (2 - 1 + 0.5) / (1 + 0.5)) * (1 * 2.2 / (1 + 1.2 * (1 - 0.75 + 0.75 * 2 / 1.5)));
        Check(Math.Abs(expected - scored.Score) < 1e-12, $"BM25 score {scored.Score} != {expected}");
        // Ties keep document order.
        Names(["first", "second"], new Bm25Ranker().Rank("same", [new("first", "same"), new("second", "same")], 8).Select(item => item.Name), "stable ties");
    }

    private static void Declaration()
    {
        Equal("# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\n" +
            "Some of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool (`tool_search`) " +
            "to search for the required tools. For MCP tool discovery, always use `tool_search`.", ToolSearch.Description, "description");
        // tool-search/tool.ts toolSearchSchema as TypeBox emits it: type, required, properties.
        Equal("""{"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"Search query for deferred tools."},"limit":{"type":"number","description":"Maximum number of tools to return. Defaults to 8."}}}""",
            McpDiscoveryToolIdentity.ToolSearchSchema.ToString(), "schema");
        Equal(8, ToolSearch.DefaultLimit, "default limit");
        var descriptor = McpToolSearch.Create().Descriptor;
        Equal("tool_search", descriptor.Name, "name");
        Equal(ToolExposure.ModelOnly, descriptor.Exposure, "model-only exposure");
        Check(ReferenceEquals(McpDiscoveryToolIdentity.ToolSearchSchema, descriptor.Parameters) &&
            McpDiscoveryToolIdentity.IsToolSearchTool(descriptor.Name, descriptor.Parameters), "the tool carries the identity schema");
        Check(ToolSearch.IsSearchable(ToolExposure.Deferred) && ToolSearch.IsSearchable(ToolExposure.Codemode) &&
            !ToolSearch.IsSearchable(ToolExposure.Direct) && !ToolSearch.IsSearchable(ToolExposure.ModelOnly) && !ToolSearch.IsSearchable(ToolExposure.Hidden),
            "codemode and deferred tools are searchable");
    }

    private static void ResultText()
    {
        Equal("No matching tools found.", ToolSearch.FormatResult([]), "no match");
        Equal("Loaded 1 tool. They are available from your next call:\n- mcp__docs__search: Search the documentation.",
            ToolSearch.FormatResult([new("mcp__docs__search", "  Search the documentation.\r\nSecond line.")]), "one tool, first line of the trimmed description");
        Equal("Loaded 2 tools. They are available from your next call:\n- a: First.\n- b: ",
            ToolSearch.FormatResult([new("a", "First.\nMore."), new("b", "")]), "two tools");
        Equal("query must not be empty", Throws<ArgumentException>(() => ToolSearch.ValidateInput(" \t ", null), "blank query").Message, "blank query");
        foreach (var limit in new[] { 0, -1, 1.5, double.NaN })
            Equal("limit must be a positive integer", Throws<ArgumentException>(() => ToolSearch.ValidateInput("q", limit), "limit " + limit).Message, "limit " + limit);
        Equal(8, ToolSearch.ValidateInput("q", null), "default limit");
        Equal(3, ToolSearch.ValidateInput("q", 3), "explicit limit");
        Check(McpToolSearch.ValidArguments(JsonData.Parse("""{"query":"x"}""")) && McpToolSearch.ValidArguments(JsonData.Parse("""{"query":"x","limit":2}""")) &&
            !McpToolSearch.ValidArguments(JsonData.Parse("""{"query":1}""")) && !McpToolSearch.ValidArguments(JsonData.Parse("""{"query":"x","limit":"2"}""")) &&
            !McpToolSearch.ValidArguments(JsonData.Parse("{}")), "arguments admitted as the schema declares them");
    }
}
