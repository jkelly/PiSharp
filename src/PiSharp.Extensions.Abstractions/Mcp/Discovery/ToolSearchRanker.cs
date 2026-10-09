// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/tool-search/tool.ts.
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Extensions.Mcp.Discovery;

/// <summary>A tool as the ranker sees it: its name and the text built by <see cref="ToolSearch.CreateDocument"/>.</summary>
public sealed record ToolSearchDocument(string Name, string Text);
public sealed record ToolSearchMatch(string Name, double Score);
/// <summary>A tool loaded by one search: its name and description, as the result lists it.</summary>
public sealed record ToolSearchResultTool(string Name, string Description);

/// <summary>Tool discovery: a BM25 ranker over tool metadata and the optional `tool_search` tool's text. `tool_search` searches
/// tools that are not declared to the model (`codemode` and `deferred` exposure) and loads the matches, so they are declared for
/// the next model call. Pure: the session-bound search and activation belong to the host.</summary>
public static partial class ToolSearch
{
    public const string Name = McpDiscoveryToolIdentity.ToolSearchName;
    public const int DefaultLimit = 8;
    /// <summary>The `tool_search` description. It does not list the searchable tools or their namespaces, so it stays the same
    /// while tools are registered, for example when MCP servers connect.</summary>
    public const string Description = "# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\n" +
        "Some of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool (`tool_search`) " +
        "to search for the required tools. For MCP tool discovery, always use `tool_search`.";
    /// <summary>The tool's promptSnippet, listed in the system prompt (PiSystemPrompt.ToolSnippets) while tool_search is active.</summary>
    public const string PromptSnippet = "Search for tools that are not loaded yet and load the matches";
    public const string NoMatches = "No matching tools found.";

    private static readonly HashSet<string> StopWords = new(["a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is",
        "it", "of", "on", "or", "that", "the", "this", "to", "with"], StringComparer.Ordinal);
    [GeneratedRegex("([a-z0-9])([A-Z])")] private static partial Regex LowerUpper();
    [GeneratedRegex("([A-Z]+)([A-Z][a-z])")] private static partial Regex Acronym();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex Separators();
    [GeneratedRegex("(ches|shes|sses|xes|zes)$")] private static partial Regex SibilantPlural();
    [GeneratedRegex("\r?\n")] private static partial Regex LineBreak();

    /// <summary>Naive singular form, so `issues` matches `issue` and `searches` matches `search`.</summary>
    private static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && SibilantPlural().IsMatch(term)) return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }

    /// <summary>Lowercase terms, split at camelCase boundaries and non-alphanumerics, without stop words.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spaced = Acronym().Replace(LowerUpper().Replace(text, "$1 $2"), "$1 $2").ToLowerInvariant();
        return [.. Separators().Split(spaced).Where(term => term.Length > 0 && !StopWords.Contains(term)).Select(Stem)];
    }

    /// <summary>Schema descriptions and property names, recursively.</summary>
    private static void SchemaText(JsonElement schema, List<string> parts, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > 64) return;
        var fields = McpJson.Properties(schema).ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        if (fields.TryGetValue("description", out var description) && description.ValueKind == JsonValueKind.String) parts.Add(description.GetString()!);
        if (fields.TryGetValue("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in McpJson.Properties(properties)) { parts.Add(property.Name); SchemaText(property.Value, parts, depth + 1); }
        if (fields.TryGetValue("items", out var items)) SchemaText(items, parts, depth + 1);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (fields.TryGetValue(key, out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray()) SchemaText(variant, parts, depth + 1);
    }

    /// <summary>Search text of a tool: the name, the name with `_` as spaces, the description, schema descriptions and property
    /// names, and the namespace with its description and instructions.</summary>
    public static ToolSearchDocument CreateDocument(string name, string description, JsonElement parameters, ToolNamespace? toolNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(description);
        var parts = new List<string> { name, name.Replace('_', ' '), description };
        SchemaText(parameters, parts, 0);
        if (toolNamespace is not null) parts.AddRange([toolNamespace.Name, toolNamespace.Description ?? "", toolNamespace.Instructions ?? ""]);
        return new(name, string.Join(' ', parts.Where(part => McpJson.JsTrim(part).Length > 0)));
    }

    /// <summary>Whether `tool_search` can load a tool with this exposure.</summary>
    public static bool IsSearchable(ToolExposure exposure) => exposure is ToolExposure.Codemode or ToolExposure.Deferred;

    /// <summary>The limit a call asks for: <see cref="DefaultLimit"/> without one. Throws the tool's errors for an empty query or a
    /// limit that is not a positive integer.</summary>
    public static int ValidateInput(string query, double? limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (McpJson.JsTrim(query).Length == 0) throw new ArgumentException("query must not be empty");
        var max = limit ?? DefaultLimit;
        if (!double.IsFinite(max) || Math.Floor(max) != max || max <= 0) throw new ArgumentException("limit must be a positive integer");
        return max >= int.MaxValue ? int.MaxValue : (int)max;
    }

    /// <summary>The tool result text: the loaded tools with the first line of each description, or that nothing matched.</summary>
    public static string FormatResult(IReadOnlyList<ToolSearchResultTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0) return NoMatches;
        return $"Loaded {tools.Count} tool{(tools.Count == 1 ? "" : "s")}. They are available from your next call:\n" +
            string.Join("\n", tools.Select(tool => $"- {tool.Name}: {LineBreak().Split(McpJson.JsTrim(tool.Description))[0]}"));
    }
}

/// <summary>Okapi BM25 with the usual parameters. Ties keep document order.</summary>
public sealed class Bm25Ranker(double k1 = 1.2, double b = 0.75)
{
    public IReadOnlyList<ToolSearchMatch> Rank(string query, IReadOnlyList<ToolSearchDocument> documents, int limit)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(documents);
        var queryTerms = ToolSearch.Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTerms.Length == 0 || documents.Count == 0 || limit <= 0) return [];
        var termCounts = documents.Select(document =>
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in ToolSearch.Tokenize(document.Text)) counts[term] = counts.GetValueOrDefault(term) + 1;
            return counts;
        }).ToArray();
        var lengths = termCounts.Select(counts => (double)counts.Values.Sum()).ToArray();
        var average = lengths.Sum() / documents.Count;
        if (average == 0 || double.IsNaN(average)) average = 1;
        var idf = queryTerms.ToDictionary(term => term, term =>
        {
            var frequency = termCounts.Count(counts => counts.ContainsKey(term));
            return Math.Log(1 + (documents.Count - frequency + 0.5) / (frequency + 0.5));
        }, StringComparer.Ordinal);
        var matches = new List<ToolSearchMatch>();
        for (var index = 0; index < documents.Count; index++)
        {
            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!termCounts[index].TryGetValue(term, out var count) || count == 0) continue;
                var norm = k1 * (1 - b + b * lengths[index] / average);
                score += idf[term] * (count * (k1 + 1) / (count + norm));
            }
            if (score > 0) matches.Add(new(documents[index].Name, score));
        }
        // LINQ ordering is stable, like Array.prototype.sort.
        return [.. matches.OrderByDescending(match => match.Score).Take(limit)];
    }
}
