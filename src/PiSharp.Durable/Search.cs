// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/search.ts.
// BM25 tool search for code mode's `searchTools()` (work list area d) and the `tool_search` tool (PiSharp.Durable.Tools' ToolSearch), a copy
// of the coding agent's tool search ranking: durable keeps its own copy of what it needs from the coding agent. Upstream does not export it
// from the package, so it is internal here. Main's port of the coding agent's ranking (PiSharp.Extensions.Abstractions'
// Mcp/Discovery/ToolSearchRanker.cs, with its UnicodeWordSegmenter) is the same algorithm; PiSharp.Durable may not reference it (decision
// 0005), so this file copies it, ICU binding included, and tests/PiSharp.Durable.Tests/ToolSearchCases.cs compares the two
// (tool-search.ranking-differential.*).
// - JavaScript's `toLowerCase()` maps U+0130 (capital I with dot above) to "i" and U+0307; .NET's invariant lowercasing maps one UTF-16 unit
//   to one, so that character is expanded first. JavaScript's final sigma (a word-final U+03A3 lowercases to U+03C2) is not reproduced.
// - `Intl.Segmenter`'s word granularity is ICU's word break iterator, bound from the operating system's ICU (WordBreak below): the ICU
//   version is the system's, not the one Node bundles, and the rules are ICU's root locale. Without ICU a run of word characters stays one
//   word, so Chinese, Japanese and Thai text without spaces is not split; without normalization data (invariant globalization) NFC is a no-op.
// - The identifier regular expressions are ASCII-only, as upstream's are; ties keep document order through LINQ's stable ordering, as through
//   Array.prototype.sort.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable;

/// <summary>A tool as the ranker sees it (upstream <c>SearchDocument</c>): its name, its text, and the text of its namespace.</summary>
/// <param name="Name">The tool name, a field of its own for the ranker.</param>
/// <param name="Text">Description and parameter names and descriptions; not the name, which the ranker reads from <paramref name="Name"/>.</param>
/// <param name="NamespaceText">Name, description and instructions of the tool's namespace; empty without one.</param>
internal sealed record SearchDocument(string Name, string Text, string NamespaceText = "");

/// <summary>Upstream <c>search.ts</c>: <see cref="Tokenize"/> and Okapi BM25 <see cref="RankDocuments"/>.</summary>
internal static partial class Search
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>A query term in the name counts this many times its IDF. The text part of a term is at most <c>K1 + 1</c> (2.2) times its IDF
    /// and about 1 for a single mention, so a name match beats any number of mentions in the text.</summary>
    private const double NameWeight = 3;

    /// <summary>A query term in the namespace text counts this many times its IDF. Every tool of the namespace shares it, so it helps find the
    /// namespace but not the tool in it.</summary>
    private const double NamespaceWeight = 0.5;

    private static readonly HashSet<string> StopWords = new(
        ["a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"],
        StringComparer.Ordinal);

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex LowerUpper();

    [GeneratedRegex("([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex Acronym();

    [GeneratedRegex("[^a-z0-9_$]", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifier();

    [GeneratedRegex("(ches|shes|sses|xes|zes)$", RegexOptions.CultureInvariant)]
    private static partial Regex SibilantPlural();

    /// <summary>A document split into the fields the ranker weighs differently.</summary>
    /// <param name="NameTerms">Terms of the name: <c>mcp</c>, <c>app</c>, <c>thread</c>, <c>read</c> for <c>mcp__app__thread_read</c>.</param>
    /// <param name="LocalNameTerms">Terms of the name after its last <c>__</c>, the part models say: <c>thread</c>, <c>read</c>.</param>
    private sealed record IndexedDocument(string Name, HashSet<string> NameTerms, string[] LocalNameTerms, Dictionary<string, int> TextCounts,
        int TextLength, HashSet<string> NamespaceTerms);

    /// <summary>
    /// Okapi BM25 (k1 1.2, b 0.75) over the text, with the name and the namespace as separate fields: the names of the best matches, best
    /// first; ties keep document order.
    /// <list type="bullet">
    /// <item>A tool that matches more of the query's terms, in any field, ranks above one that matches fewer, whatever their scores: for
    /// <c>screenshot window</c>, a tool whose description has both words beats <c>window</c> whose name has one.</item>
    /// <item>A query term in the name scores more than any number of mentions in the text.</item>
    /// <item>A tool whose whole local name (after the last <c>__</c>) is in the query gets a bonus worth one more match of every query term,
    /// so among tools matching as many terms, <c>peekaboo app</c> finds <c>mcp__peekaboo__app</c> over tools that only mention the
    /// words.</item>
    /// <item>A query that is exactly a tool's name, or its name after a <c>__</c>, ranks that tool first, before coverage.</item>
    /// </list>
    /// Only the text is length-normalized, so a long name or long namespace instructions do not lower a tool's score.
    /// </summary>
    public static List<string> RankDocuments(string query, IReadOnlyList<SearchDocument> documents, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (queryTerms.Count == 0 || documents.Count == 0 || limit <= 0) return [];
        var indexed = documents.Select(IndexDocument).ToList();
        // Term counts are integers, exact as doubles, so the sum does not depend on the order of addition.
        var averageLength = (double)indexed.Sum(document => document.TextLength) / indexed.Count;
        if (averageLength == 0) averageLength = 1;
        var idf = queryTerms.ToDictionary(term => term, term =>
        {
            var frequency = indexed.Count(document =>
                document.NameTerms.Contains(term) || document.TextCounts.ContainsKey(term) || document.NamespaceTerms.Contains(term));
            return Math.Log(1 + (indexed.Count - frequency + 0.5) / (frequency + 0.5));
        }, StringComparer.Ordinal);
        var querySet = queryTerms.ToHashSet(StringComparer.Ordinal);
        double fullNameBonus = 0;
        foreach (var term in queryTerms) fullNameBonus += idf[term];
        var exactQuery = NormalizeName(query);
        var matches = new List<(string Name, double Score, int Covered, bool Exact)>();
        foreach (var document in indexed)
        {
            var norm = K1 * (1 - B + B * document.TextLength / averageLength);
            double score = 0;
            var covered = 0;
            foreach (var term in queryTerms)
            {
                var count = document.TextCounts.GetValueOrDefault(term);
                var weight = (document.NameTerms.Contains(term) ? NameWeight : 0) + (document.NamespaceTerms.Contains(term) ? NamespaceWeight : 0) +
                    count * (K1 + 1) / (count + norm);
                if (weight > 0) covered++;
                score += idf[term] * weight;
            }
            if (score == 0) continue;
            if (document.LocalNameTerms.Length > 0 && document.LocalNameTerms.All(querySet.Contains)) score += fullNameBonus;
            matches.Add((document.Name, score, covered, IsExactNameQuery(exactQuery, document.Name)));
        }
        return [.. matches.OrderByDescending(match => match.Exact).ThenByDescending(match => match.Covered).ThenByDescending(match => match.Score)
            .Take(limit).Select(match => match.Name)];
    }

    private static IndexedDocument IndexDocument(SearchDocument document)
    {
        var textCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in Tokenize(document.Text)) textCounts[term] = textCounts.GetValueOrDefault(term) + 1;
        return new(document.Name, new(Tokenize(document.Name), StringComparer.Ordinal),
            [.. Tokenize(document.Name.Split("__")[^1]).Distinct(StringComparer.Ordinal)], textCounts, textCounts.Values.Sum(),
            new(Tokenize(document.NamespaceText ?? ""), StringComparer.Ordinal));
    }

    /// <summary>Unicode word segmentation with identifier boundaries and English stop words and singular forms: NFC, camelCase and acronym
    /// splits, lowercase, and every run of characters other than letters, marks and digits a space. Plain ASCII splits at the spaces; other
    /// text, such as Chinese written without spaces, goes through the word segmenter. Only ASCII words are stemmed.</summary>
    public static List<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = WordCharacters(ToLowerCase(Acronym().Replace(LowerUpper().Replace(Nfc(text), "$1 $2"), "$1 $2")));
        // The segmenter costs about 15 times a split. Plain ASCII splits into the same words at spaces, so only other text goes through it.
        var words = normalized.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == ' ')
            ? normalized.Split(' ') : WordBreak.WordLikeSegments(normalized);
        return [.. words.Where(word => word.Length > 0 && !StopWords.Contains(word))
            .Select(word => word.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) ? Stem(word) : word)];
    }

    /// <summary>Naive singular form, so <c>issues</c> matches <c>issue</c> and <c>searches</c> matches <c>search</c>.</summary>
    private static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && SibilantPlural().IsMatch(term)) return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }

    /// <summary>String.prototype.toLowerCase() as far as the tokenizer can tell (see the file header).</summary>
    private static string ToLowerCase(string text) => (text.Contains('\u0130') ? text.Replace("\u0130", "i\u0307", StringComparison.Ordinal) : text).ToLowerInvariant();

    /// <summary>String.prototype.normalize("NFC"). JavaScript keeps a lone surrogate, which .NET refuses; it becomes U+FFFD here, a separator
    /// as the surrogate is. Without normalization data (invariant globalization) the text stays as it is.</summary>
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

    /// <summary>The source's replace of every run of characters other than <c>\p{L}\p{M}\p{N}</c> by a space, by code point (the <c>u</c>
    /// flag), so a letter outside the BMP stays a letter.</summary>
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

    /// <summary>normalizeName: trimmed and lowercase, with characters invalid in script identifiers as <c>_</c>, so <c>dev-radius</c> matches
    /// <c>dev_radius</c>.</summary>
    private static string NormalizeName(string name) => NonIdentifier().Replace(ToLowerCase(Js.Trim(name)), "_");

    /// <summary>isExactNameQuery: the whole query is the tool's full name or its suffix after a <c>__</c> separator, so <c>thread_read</c> and
    /// <c>app__thread_read</c> both name <c>mcp__app__thread_read</c>.</summary>
    private static bool IsExactNameQuery(string query, string name)
    {
        var normalized = NormalizeName(name);
        return normalized == query || normalized.EndsWith("__" + query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Upstream's <c>wordSegmenter</c>, an <c>Intl.Segmenter</c> with word granularity, and its <c>isWordLike</c> segments: ICU's word break
    /// iterator, the engine behind V8's, from the operating system: <c>icu.dll</c> (or <c>icuuc.dll</c>) in the Windows system directory,
    /// <c>libicucore</c> on macOS, the newest <c>libicuuc.so.N</c> elsewhere. A copy of main's UnicodeWordSegmenter (see the file header).
    /// </summary>
    private static class WordBreak
    {
        private const int WordIterator = 1; // UBRK_WORD
        private const int Done = -1; // UBRK_DONE
        private const int WordNoneLimit = 100; // UBRK_WORD_NONE_LIMIT: statuses below it are spaces and punctuation

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint OpenIterator(int type, [MarshalAs(UnmanagedType.LPStr)] string locale, nint text, int length, ref int status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Step(nint iterator);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseIterator(nint iterator);

        private sealed record Icu(OpenIterator Open, Step First, Step Next, Step RuleStatus, CloseIterator Close);

        private static readonly Lazy<Icu?> Library = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The word-like segments of the text (letters, marks and digits; no spaces), or its space-separated runs without ICU.</summary>
        internal static IEnumerable<string> WordLikeSegments(string text)
        {
            if (text.Length == 0) return [];
            if (Library.Value is not { } icu || Segment(icu, text) is not { } words) return text.Split(' ');
            return words;
        }

        private static List<string>? Segment(Icu icu, string text)
        {
            var buffer = Marshal.StringToHGlobalUni(text);
            try
            {
                var status = 0;
                var iterator = icu.Open(WordIterator, "", buffer, text.Length, ref status);
                if (status > 0 || iterator == 0) return null; // U_FAILURE
                try
                {
                    var words = new List<string>();
                    var start = icu.First(iterator);
                    for (var end = icu.Next(iterator); end != Done; start = end, end = icu.Next(iterator))
                        if (icu.RuleStatus(iterator) >= WordNoneLimit) words.Add(text[start..end]);
                    return words;
                }
                finally { icu.Close(iterator); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static Icu? Load()
        {
            try
            {
                foreach (var (path, suffix) in Candidates())
                {
                    if (!NativeLibrary.TryLoad(path, out var handle)) continue;
                    var exportSuffix = suffix is null || NativeLibrary.TryGetExport(handle, "ubrk_open", out _) ? "" : suffix;
                    if (Bind(handle, exportSuffix) is { } icu) return icu;
                }
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
            {
            }
            return null;
        }

        /// <summary>Library paths with the version suffix their exports may carry (ICU renames symbols to <c>ubrk_open_74</c>).</summary>
        private static IEnumerable<(string Path, string? Suffix)> Candidates()
        {
            if (OperatingSystem.IsWindows())
            {
                // The system directory only: a DLL of the same name next to the app or in the working directory must not load.
                yield return (Path.Combine(Environment.SystemDirectory, "icu.dll"), null);
                yield return (Path.Combine(Environment.SystemDirectory, "icuuc.dll"), null);
            }
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
                yield return ("/usr/lib/libicucore.dylib", null);
            else
                for (var version = 99; version >= 50; version--) yield return ($"libicuuc.so.{version}", "_" + version);
        }

        private static Icu? Bind(nint handle, string suffix)
        {
            T? Export<T>(string name) where T : Delegate =>
                NativeLibrary.TryGetExport(handle, name + suffix, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
            return Export<OpenIterator>("ubrk_open") is { } open && Export<Step>("ubrk_first") is { } first && Export<Step>("ubrk_next") is { } next &&
                Export<Step>("ubrk_getRuleStatus") is { } ruleStatus && Export<CloseIterator>("ubrk_close") is { } close
                ? new(open, first, next, ruleStatus, close) : null;
        }
    }
}
