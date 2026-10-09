// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/session-selector-search.ts.
// Sessions are PiSharp's PiSessionInfo (the source SessionInfo: path, id, cwd, name, parentSessionPath, created, modified,
// messageCount, firstMessage, allMessagesText).
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source SortMode: "threaded" | "recent" | "relevance".</summary>
internal enum SortMode { Threaded, Recent, Relevance }

/// <summary>Source NameFilter: "all" | "named".</summary>
internal enum NameFilter { All, Named }

/// <summary>Source token: kind "fuzzy" | "phrase".</summary>
internal readonly record struct SearchToken(string Kind, string Value);

/// <summary>Source ParsedSearchQuery: mode "tokens" | "regex"; <see cref="Error"/> set when parsing failed (treat the query as non-matching).</summary>
internal sealed record ParsedSearchQuery(string Mode, IReadOnlyList<SearchToken> Tokens, Regex? Regex, string? Error = null);

/// <summary>Source MatchResult: lower score is better (only meaningful when it matches).</summary>
internal readonly record struct MatchResult(bool Matches, double Score);

internal static class SessionSelectorSearch
{
    private static string NormalizeWhitespaceLower(string text) => TextUtils.JsTrim(CollapseWhitespace(text.ToLowerInvariant()));

    /// <summary>JavaScript <c>text.replace(/\s+/g, " ")</c>.</summary>
    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inWhitespace = false;
        foreach (var ch in text)
        {
            if (TextUtils.IsJsWhitespace(ch)) { if (!inWhitespace) builder.Append(' '); inWhitespace = true; }
            else { builder.Append(ch); inWhitespace = false; }
        }
        return builder.ToString();
    }

    private static string GetSessionSearchText(PiSessionInfo session) => $"{session.Id} {session.Name ?? ""} {session.AllMessagesText} {session.Cwd}";

    public static bool HasSessionName(PiSessionInfo session) => session.Name is { } name && TextUtils.JsTrim(name).Length > 0;

    private static bool MatchesNameFilter(PiSessionInfo session, NameFilter filter) => filter == NameFilter.All || HasSessionName(session);

    /// <summary>JavaScript <c>new RegExp(pattern, "i")</c>: ECMAScript semantics where .NET supports them, else the .NET dialect.</summary>
    private static Regex CreateRegex(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.ECMAScript); }
        catch (ArgumentException) { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
    }

    public static ParsedSearchQuery ParseSearchQuery(string query)
    {
        var trimmed = TextUtils.JsTrim(query);
        if (trimmed.Length == 0) return new("tokens", [], null);

        // Regex mode: re:<pattern>
        if (trimmed.StartsWith("re:", StringComparison.Ordinal))
        {
            var pattern = TextUtils.JsTrim(trimmed[3..]);
            if (pattern.Length == 0) return new("regex", [], null, "Empty regex");
            try { return new("regex", [], CreateRegex(pattern)); }
            catch (ArgumentException error) { return new("regex", [], null, error.Message); }
        }

        // Token mode with quote support.
        // Example: foo "node cve" bar
        var tokens = new List<SearchToken>();
        var buf = new StringBuilder();
        var inQuote = false;
        var hadUnclosedQuote = false;

        void Flush(string kind)
        {
            var v = TextUtils.JsTrim(buf.ToString());
            buf.Clear();
            if (v.Length == 0) return;
            tokens.Add(new(kind, v));
        }

        foreach (var ch in trimmed)
        {
            if (ch == '"')
            {
                if (inQuote) { Flush("phrase"); inQuote = false; }
                else { Flush("fuzzy"); inQuote = true; }
                continue;
            }
            if (!inQuote && TextUtils.IsJsWhitespace(ch)) { Flush("fuzzy"); continue; }
            buf.Append(ch);
        }

        if (inQuote) hadUnclosedQuote = true;

        // If quotes were unbalanced, fall back to plain whitespace tokenization.
        if (hadUnclosedQuote)
        {
            var split = CollapseWhitespace(trimmed).Split(' ');
            return new("tokens", [.. split.Select(TextUtils.JsTrim).Where(t => t.Length > 0).Select(t => new SearchToken("fuzzy", t))], null);
        }

        Flush(inQuote ? "phrase" : "fuzzy");
        return new("tokens", tokens, null);
    }

    public static MatchResult MatchSession(PiSessionInfo session, ParsedSearchQuery parsed)
    {
        var text = GetSessionSearchText(session);

        if (parsed.Mode == "regex")
        {
            if (parsed.Regex is null) return new(false, 0);
            var match = parsed.Regex.Match(text);
            if (!match.Success) return new(false, 0);
            return new(true, match.Index * 0.1);
        }

        if (parsed.Tokens.Count == 0) return new(true, 0);

        double totalScore = 0;
        string? normalizedText = null;

        foreach (var token in parsed.Tokens)
        {
            if (token.Kind == "phrase")
            {
                normalizedText ??= NormalizeWhitespaceLower(text);
                var phrase = NormalizeWhitespaceLower(token.Value);
                if (phrase.Length == 0) continue;
                var idx = normalizedText.IndexOf(phrase, StringComparison.Ordinal);
                if (idx < 0) return new(false, 0);
                totalScore += idx * 0.1;
                continue;
            }

            var m = Fuzzy.Match(token.Value, text);
            if (!m.Matches) return new(false, 0);
            totalScore += m.Score;
        }

        return new(true, totalScore);
    }

    public static List<PiSessionInfo> FilterAndSortSessions(IReadOnlyList<PiSessionInfo> sessions, string query, SortMode sortMode, NameFilter nameFilter = NameFilter.All)
    {
        List<PiSessionInfo> nameFiltered = nameFilter == NameFilter.All ? [.. sessions] : sessions.Where(session => MatchesNameFilter(session, nameFilter)).ToList();
        var trimmed = TextUtils.JsTrim(query);
        if (trimmed.Length == 0) return nameFiltered;

        var parsed = ParseSearchQuery(query);
        if (parsed.Error is not null) return [];

        // Recent mode: filter only, keep incoming order.
        if (sortMode == SortMode.Recent)
            return [.. nameFiltered.Where(s => MatchSession(s, parsed).Matches)];

        // Relevance mode: sort by score, tie-break by modified desc.
        var scored = new List<(PiSessionInfo Session, double Score)>();
        foreach (var s in nameFiltered)
        {
            var res = MatchSession(s, parsed);
            if (!res.Matches) continue;
            scored.Add((s, res.Score));
        }

        return [.. scored.OrderBy(r => r.Score).ThenByDescending(r => r.Session.Modified.ToUnixTimeMilliseconds()).Select(r => r.Session)];
    }
}
