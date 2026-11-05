// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/modes/interactive/components/session-selector-search.ts.
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

    // Match tiers, best first. Each token is scored by the best field it matches; tiers dominate fuzzy scores so an exact hit anywhere
    // beats a scattered subsequence in a short field.
    private const int TierName = 0; // query text appears in the session name
    private const int TierText = 1; // query text appears in the session id, messages, or cwd
    private const int TierFuzzy = 2; // query characters appear scattered in the session name or cwd
    private const double TierWeight = 1_000_000;

    private sealed class LowercaseCache(string source, string lower)
    {
        public string Source { get; } = source;
        public string Lower { get; } = lower;
        public string? Normalized { get; set; }
    }

    /// <summary>Lowercased message text per session. AllMessagesText holds every user/assistant message and can be megabytes, so it is
    /// lowercased once instead of on every keystroke.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PiSessionInfo, LowercaseCache> MessageTextCache = new();

    private static LowercaseCache GetMessageText(PiSessionInfo session)
    {
        if (MessageTextCache.TryGetValue(session, out var cached) && ReferenceEquals(cached.Source, session.AllMessagesText)) return cached;
        cached = new LowercaseCache(session.AllMessagesText, session.AllMessagesText.ToLowerInvariant());
        MessageTextCache.AddOrUpdate(session, cached);
        return cached;
    }

    private static string GetNormalizedMessageText(PiSessionInfo session)
    {
        var cached = GetMessageText(session);
        return cached.Normalized ??= TextUtils.JsTrim(CollapseWhitespace(cached.Lower));
    }

    private static double TierScore(int tier, double score = 0) => tier * TierWeight + score;

    /// <summary>Exact (case-insensitive) text match. Messages and ids are only matched exactly: scattered matches in them are noise.</summary>
    private static double? MatchLiteralToken(PiSessionInfo session, string value)
    {
        var name = session.Name?.ToLowerInvariant() ?? "";
        if (name.Contains(value, StringComparison.Ordinal)) return TierScore(TierName, Fuzzy.Match(value, name).Score);
        if (session.Id.ToLowerInvariant().Contains(value, StringComparison.Ordinal)) return TierScore(TierText);
        if (GetMessageText(session).Lower.Contains(value, StringComparison.Ordinal)) return TierScore(TierText);
        if (session.Cwd.ToLowerInvariant().Contains(value, StringComparison.Ordinal)) return TierScore(TierText);
        return null;
    }

    private static double? MatchFuzzyToken(PiSessionInfo session, string value)
    {
        if (MatchLiteralToken(session, value.ToLowerInvariant()) is { } literal) return literal;
        double? best = null;
        foreach (var field in new[] { session.Name ?? "", session.Cwd })
        {
            var m = Fuzzy.Match(value, field);
            if (m.Matches && (best is null || m.Score < best)) best = m.Score;
        }
        return best is { } score ? TierScore(TierFuzzy, score) : null;
    }

    private static double? MatchPhraseToken(PiSessionInfo session, string phrase)
    {
        var name = NormalizeWhitespaceLower(session.Name ?? "");
        if (name.Contains(phrase, StringComparison.Ordinal)) return TierScore(TierName, Fuzzy.Match(phrase, name).Score);
        if (session.Id.ToLowerInvariant().Contains(phrase, StringComparison.Ordinal)) return TierScore(TierText);
        if (GetNormalizedMessageText(session).Contains(phrase, StringComparison.Ordinal)) return TierScore(TierText);
        if (NormalizeWhitespaceLower(session.Cwd).Contains(phrase, StringComparison.Ordinal)) return TierScore(TierText);
        return null;
    }

    private static double? MatchRegex(PiSessionInfo session, Regex regex)
    {
        if (!string.IsNullOrEmpty(session.Name) && regex.IsMatch(session.Name)) return TierScore(TierName);
        if (regex.IsMatch(session.Id) || regex.IsMatch(session.AllMessagesText) || regex.IsMatch(session.Cwd)) return TierScore(TierText);
        return null;
    }

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

    /// <summary>Each field is matched separately, so a token never matches across field boundaries. Different tokens may match
    /// different fields (Pi 1.5.0, 8c03fef4d).</summary>
    public static MatchResult MatchSession(PiSessionInfo session, ParsedSearchQuery parsed)
    {
        if (parsed.Mode == "regex")
        {
            if (parsed.Regex is null) return new(false, 0);
            return MatchRegex(session, parsed.Regex) is { } regexScore ? new(true, regexScore) : new(false, 0);
        }

        double totalScore = 0;
        foreach (var token in parsed.Tokens)
        {
            double? score;
            if (token.Kind == "phrase")
            {
                var phrase = NormalizeWhitespaceLower(token.Value);
                if (phrase.Length == 0) continue;
                score = MatchPhraseToken(session, phrase);
            }
            else score = MatchFuzzyToken(session, token.Value);
            if (score is null) return new(false, 0);
            totalScore += score.Value;
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
