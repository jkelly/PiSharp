// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/extensions/tool-search/tool.ts.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Extensions.Mcp.Discovery;

/// <summary>A tool as the ranker sees it: its name and the texts built by <see cref="ToolSearch.CreateDocument"/>.</summary>
/// <param name="Name">The tool name, a field of its own for the ranker.</param>
/// <param name="Text">Description, schema descriptions and property names.</param>
public sealed record ToolSearchDocument(string Name, string Text)
{
    /// <summary>Name, description and instructions of the tool's namespace; empty without one.</summary>
    [Experimental("PISHARP_TOOLSEARCH001")]
    public string NamespaceText { get; init; } = "";
}
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
    [GeneratedRegex("[^a-z0-9_$]")] private static partial Regex NonIdentifier();
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

    /// <summary>String.prototype.toLowerCase() as far as the ASCII split can tell. JavaScript maps U+0130 (capital I with dot above)
    /// to "i" and U+0307, while .NET's invariant lowercasing maps one UTF-16 unit to one and leaves it as it is, so it is expanded
    /// first. Every other character lowercases to the same ASCII letters and digits in both, or to none.</summary>
    private static string ToLowerCase(string text) =>
        (text.Contains('İ') ? text.Replace("İ", "i̇", StringComparison.Ordinal) : text).ToLowerInvariant();

    /// <summary>Unicode word segmentation with identifier boundaries and English stop words and singular forms: NFC, camelCase
    /// and acronym splits, lowercase, and every run of characters other than letters, marks and digits a space. ASCII text splits
    /// at the spaces; other text, such as Chinese written without spaces, goes through ICU's word segmentation
    /// (<see cref="UnicodeWordSegmenter"/>). Only ASCII words are stemmed.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = WordCharacters(ToLowerCase(Acronym().Replace(LowerUpper().Replace(Nfc(text), "$1 $2"), "$1 $2")));
        var words = normalized.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == ' ')
            ? normalized.Split(' ') : UnicodeWordSegmenter.WordLikeSegments(normalized);
        return [.. words.Where(word => word.Length > 0 && !StopWords.Contains(word))
            .Select(word => word.All(char.IsAsciiLetterOrDigit) ? Stem(word) : word)];
    }

    /// <summary>String.prototype.normalize("NFC"). JavaScript keeps a lone surrogate, which .NET refuses; it becomes U+FFFD here,
    /// a separator as the surrogate is. Without normalization data (invariant globalization) the text stays as it is.</summary>
    private static string Nfc(string text)
    {
        if (Ascii.IsValid(text)) return text;
        try { return text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException)
        {
            var builder = new StringBuilder(text.Length);
            for (var index = 0; index < text.Length;)
            {
                var valid = Rune.DecodeFromUtf16(text.AsSpan(index), out _, out var consumed) == System.Buffers.OperationStatus.Done;
                if (valid) builder.Append(text, index, consumed); else builder.Append('\uFFFD');
                index += consumed;
            }
            var repaired = builder.ToString();
            return repaired == text ? text : Nfc(repaired);
        }
        catch (PlatformNotSupportedException) { return text; }
    }

    /// <summary>The source's replace of every run of characters other than <c>\p{L}\p{M}\p{N}</c> by a space, by code point (the
    /// <c>u</c> flag), so a letter outside the BMP stays a letter.</summary>
    private static string WordCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        var separated = false;
        for (var index = 0; index < text.Length;)
        {
            // A lone surrogate decodes as U+FFFD, a symbol: a separator, as its own code point is to the source.
            Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (Rune.GetUnicodeCategory(rune) is >= UnicodeCategory.UppercaseLetter and <= UnicodeCategory.OtherNumber)
            {
                builder.Append(text, index, consumed);
                separated = false;
            }
            else if (!separated)
            {
                builder.Append(' ');
                separated = true;
            }
            index += consumed;
        }
        return builder.ToString();
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

    /// <summary>Search text of a tool: the description and the schema descriptions and property names, and separately the
    /// namespace name, description and instructions. The ranker reads the name from <see cref="ToolSearchDocument.Name"/>.</summary>
    public static ToolSearchDocument CreateDocument(string name, string description, JsonElement parameters, ToolNamespace? toolNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(description);
        var parts = new List<string> { description };
        SchemaText(parameters, parts, 0);
        var namespaceText = toolNamespace is null ? ""
            : string.Join(' ', toolNamespace.Name, toolNamespace.Description ?? "", toolNamespace.Instructions ?? "");
        return new(name, string.Join(' ', parts.Where(part => McpJson.JsTrim(part).Length > 0))) { NamespaceText = namespaceText };
    }

    /// <summary>normalizeName: lowercase, with characters invalid in script identifiers as <c>_</c>, so <c>dev-radius</c> matches
    /// <c>dev_radius</c>.</summary>
    internal static string NormalizeName(string name) => NonIdentifier().Replace(ToLowerCase(McpJson.JsTrim(name)), "_");

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

/// <summary>
/// Okapi BM25 over the description, with the name and the namespace as separate fields:
/// <list type="bullet">
/// <item>a tool that matches more of the query's terms, in any field, ranks above one that matches fewer, whatever their scores;</item>
/// <item>a query term in the name scores more than any number of mentions in the description;</item>
/// <item>a tool whose whole local name (its last <c>__</c> segment) is in the query gets a bonus worth one more match of every
/// query term;</item>
/// <item>a query that is exactly a tool's name, or its name after a <c>__</c> separator, ranks that tool first.</item>
/// </list>
/// Only the description is length-normalized, so a long name or long namespace instructions do not lower a score. Ties keep
/// document order.
/// </summary>
public sealed class Bm25Ranker(double k1 = 1.2, double b = 0.75)
{
    /// <summary>A query term in the name counts this many times its IDF, more than the at most <c>k1 + 1</c> of any number of
    /// mentions in the description.</summary>
    private const double NameWeight = 3;
    /// <summary>A query term in the namespace counts this many times its IDF. Every tool of the server shares the namespace, so it
    /// helps find the server but not the tool in it.</summary>
    private const double NamespaceWeight = 0.5;

    private sealed record IndexedDocument(string Name, HashSet<string> NameTerms, string[] LocalNameTerms, Dictionary<string, int> TextCounts,
        int TextLength, HashSet<string> NamespaceTerms);

    private static IndexedDocument Index(ToolSearchDocument document)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in ToolSearch.Tokenize(document.Text)) counts[term] = counts.GetValueOrDefault(term) + 1;
        var local = document.Name.Split("__")[^1];
        return new(document.Name, new(ToolSearch.Tokenize(document.Name), StringComparer.Ordinal),
            [.. ToolSearch.Tokenize(local).Distinct(StringComparer.Ordinal)], counts, counts.Values.Sum(),
            new(ToolSearch.Tokenize(document.NamespaceText ?? ""), StringComparer.Ordinal));
    }

    public IReadOnlyList<ToolSearchMatch> Rank(string query, IReadOnlyList<ToolSearchDocument> documents, int limit)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(documents);
        var queryTerms = ToolSearch.Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTerms.Length == 0 || documents.Count == 0 || limit <= 0) return [];
        var indexed = documents.Select(Index).ToArray();
        var average = (double)indexed.Sum(document => document.TextLength) / indexed.Length;
        if (average == 0 || double.IsNaN(average)) average = 1;
        var idf = queryTerms.ToDictionary(term => term, term =>
        {
            var frequency = indexed.Count(document =>
                document.NameTerms.Contains(term) || document.TextCounts.ContainsKey(term) || document.NamespaceTerms.Contains(term));
            return Math.Log(1 + (indexed.Length - frequency + 0.5) / (frequency + 0.5));
        }, StringComparer.Ordinal);
        var querySet = queryTerms.ToHashSet(StringComparer.Ordinal);
        double fullNameBonus = 0;
        foreach (var term in queryTerms) fullNameBonus += idf[term];
        var exactQuery = ToolSearch.NormalizeName(query);
        var matches = new List<(ToolSearchMatch Match, int Covered, bool Exact)>();
        foreach (var document in indexed)
        {
            var norm = k1 * (1 - b + b * document.TextLength / average);
            double score = 0;
            var covered = 0;
            foreach (var term in queryTerms)
            {
                var count = document.TextCounts.GetValueOrDefault(term);
                var weight = (document.NameTerms.Contains(term) ? NameWeight : 0) + (document.NamespaceTerms.Contains(term) ? NamespaceWeight : 0) +
                    count * (k1 + 1) / (count + norm);
                if (weight > 0) covered++;
                score += idf[term] * weight;
            }
            if (score == 0) continue;
            if (document.LocalNameTerms.Length > 0 && document.LocalNameTerms.All(querySet.Contains)) score += fullNameBonus;
            matches.Add((new(document.Name, score), covered, IsExactNameQuery(exactQuery, document.Name)));
        }
        // LINQ ordering is stable, like Array.prototype.sort.
        return [.. matches.OrderByDescending(match => match.Exact).ThenByDescending(match => match.Covered)
            .ThenByDescending(match => match.Match.Score).Take(limit).Select(match => match.Match)];
    }

    /// <summary>isExactNameQuery: the whole query is the tool's full name or its suffix after a <c>__</c> separator, so
    /// <c>thread_read</c> and <c>app__thread_read</c> both name <c>mcp__app__thread_read</c>.</summary>
    private static bool IsExactNameQuery(string query, string name)
    {
        var normalized = ToolSearch.NormalizeName(name);
        return normalized == query || normalized.EndsWith("__" + query, StringComparison.Ordinal);
    }
}
