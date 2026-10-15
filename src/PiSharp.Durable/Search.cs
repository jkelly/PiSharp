// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/search.ts.
// BM25 tool search for code mode's `searchTools()` (work list area d) and the `tool_search` tool (PiSharp.Durable.Tools' ToolSearch), a copy
// of the coding agent's tool search ranking: durable keeps its own copy of what it needs from the coding agent. Upstream does not export it
// from the package, so it is internal here. Main's port of the coding agent's ranking (PiSharp.Extensions.Abstractions'
// Mcp/Discovery/ToolSearchRanker.cs) is the same algorithm; PiSharp.Durable may not reference it (decision 0005), and
// tests/PiSharp.Durable.Tests/ToolSearchCases.cs compares the two (tool-search.ranking-differential.*).
// - JavaScript's `toLowerCase()` maps U+0130 (capital I with dot above) to "i" and U+0307; .NET's invariant lowercasing maps one UTF-16 unit
//   to one, so that character is expanded first. Every other character either lowercases the same way in both or is not ASCII afterwards,
//   and only the ASCII letters and digits survive the split.
// - The regular expressions are ASCII-only, as upstream's are; ties keep document order through LINQ's stable ordering, as through
//   Array.prototype.sort.
using System.Text.RegularExpressions;

namespace PiSharp.Durable;

/// <summary>A document to rank: a name and its search text (upstream <c>SearchDocument</c>).</summary>
internal sealed record SearchDocument(string Name, string Text);

/// <summary>Upstream <c>search.ts</c>: <see cref="Tokenize"/> and Okapi BM25 <see cref="RankDocuments"/>.</summary>
internal static partial class Search
{
    private static readonly HashSet<string> StopWords = new(
        ["a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"],
        StringComparer.Ordinal);

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex LowerUpper();

    [GeneratedRegex("([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex Acronym();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex("(ches|shes|sses|xes|zes)$", RegexOptions.CultureInvariant)]
    private static partial Regex SibilantPlural();

    /// <summary>Naive singular form, so <c>issues</c> matches <c>issue</c> and <c>searches</c> matches <c>search</c>.</summary>
    private static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && SibilantPlural().IsMatch(term)) return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }

    /// <summary>String.prototype.toLowerCase() as far as the split can tell (see the file header).</summary>
    private static string ToLowerCase(string text) => (text.Contains('\u0130') ? text.Replace("\u0130", "i\u0307", StringComparison.Ordinal) : text).ToLowerInvariant();

    /// <summary>Lowercase terms, split at camelCase boundaries and non-alphanumerics, without stop words.</summary>
    public static List<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spaced = ToLowerCase(Acronym().Replace(LowerUpper().Replace(text, "$1 $2"), "$1 $2"));
        return [.. Separators().Split(spaced).Where(term => term.Length > 0 && !StopWords.Contains(term)).Select(Stem)];
    }

    /// <summary>Okapi BM25 (k1 1.2, b 0.75): the names of the best matches, best first; ties keep document order.</summary>
    public static List<string> RankDocuments(string query, IReadOnlyList<SearchDocument> documents, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        const double k1 = 1.2;
        const double b = 0.75;
        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (queryTerms.Count == 0 || documents.Count == 0 || limit <= 0) return [];
        var termCounts = documents.Select(document =>
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in Tokenize(document.Text)) counts[term] = counts.GetValueOrDefault(term) + 1;
            return counts;
        }).ToList();
        // Term counts are integers, exact as doubles, so the sums do not depend on the order of addition.
        var lengths = termCounts.Select(counts => (double)counts.Values.Sum()).ToList();
        var averageLength = lengths.Sum() / documents.Count;
        if (averageLength == 0) averageLength = 1;
        var idf = queryTerms.ToDictionary(term => term, term =>
        {
            var frequency = termCounts.Count(counts => counts.ContainsKey(term));
            return Math.Log(1 + (documents.Count - frequency + 0.5) / (frequency + 0.5));
        }, StringComparer.Ordinal);
        var matches = new List<(string Name, double Score)>();
        for (var index = 0; index < documents.Count; index++)
        {
            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!termCounts[index].TryGetValue(term, out var count) || count == 0) continue;
                var norm = k1 * (1 - b + b * lengths[index] / averageLength);
                score += idf[term] * (count * (k1 + 1) / (count + norm));
            }
            if (score > 0) matches.Add((documents[index].Name, score));
        }
        return [.. matches.OrderByDescending(match => match.Score).Take(limit).Select(match => match.Name)];
    }
}
