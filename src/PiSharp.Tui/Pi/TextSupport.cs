// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/kill-ring.ts, undo-stack.ts, word-navigation.ts, fuzzy.ts.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>Emacs-style kill ring.</summary>
public sealed class KillRing
{
    private readonly List<string> ring = [];
    public void Push(string text, bool prepend, bool accumulate = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (accumulate && ring.Count > 0)
        {
            var last = ring[^1]; ring.RemoveAt(ring.Count - 1);
            ring.Add(prepend ? text + last : last + text);
        }
        else ring.Add(text);
    }
    public string? Peek() => ring.Count > 0 ? ring[^1] : null;
    public void Rotate()
    {
        if (ring.Count <= 1) return;
        var last = ring[^1]; ring.RemoveAt(ring.Count - 1); ring.Insert(0, last);
    }
    public int Length => ring.Count;
}

/// <summary>Undo stack of immutable snapshots.</summary>
public sealed class UndoStack<T>
{
    private readonly List<T> stack = [];
    public void Push(T state) => stack.Add(state);
    public bool TryPop(out T state)
    {
        if (stack.Count == 0) { state = default!; return false; }
        state = stack[^1]; stack.RemoveAt(stack.Count - 1); return true;
    }
    public void Clear() => stack.Clear();
    public int Length => stack.Count;
}

/// <summary>An Intl.Segmenter "word" segment.</summary>
public readonly record struct WordSegment(string Segment, int Index, bool IsWordLike);

/// <summary>Word segmentation after UAX #29 (the subset Pi's editor depends on): letters, digits and marks join into word-like
/// segments, MidLetter/MidNum punctuation joins letters and digits, CJK ideographs stand alone, everything else is one segment
/// per grapheme.</summary>
public static class WordSegmenter
{
    private enum Kind { Letter, Numeric, Ideograph, Space, MidLetter, MidNum, MidNumLet, ExtendNumLet, Other }
    private static Kind Classify(string grapheme)
    {
        var rune = Rune.GetRuneAt(grapheme, 0);
        if (grapheme.Length > 0 && TextUtils.IsJsWhitespace(grapheme[0])) return Kind.Space;
        if (TextUtils.IsCjkBreak(grapheme)) return Rune.IsLetter(rune) ? Kind.Ideograph : Kind.Other;
        if (Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) return Kind.Letter;
        if (Rune.IsDigit(rune)) return Kind.Numeric;
        return rune.Value switch
        {
            '_' or 0x203f or 0x2040 or 0x2054 or 0xfe33 or 0xfe34 or 0xfe4d or 0xfe4e or 0xfe4f or 0xff3f => Kind.ExtendNumLet,
            '.' or 0x2018 or 0x2019 or 0x2024 or 0xfe52 or 0xff07 or 0xff0e or '\'' => Kind.MidNumLet,
            ':' or 0xb7 or 0x387 or 0x55f or 0x5f4 or 0x2027 or 0xfe13 or 0xfe55 or 0xff1a => Kind.MidLetter,
            ',' or ';' or 0x37e or 0x589 or 0x60c or 0x60d or 0x66c or 0x7f8 or 0x2044 or 0xfe10 or 0xfe14 or 0xfe50 or 0xfe54 or 0xff0c or 0xff1b => Kind.MidNum,
            _ => Kind.Other
        };
    }

    public static IEnumerable<WordSegment> Segment(string text)
    {
        var graphemes = TextUtils.Graphemes(text).ToList();
        var kinds = graphemes.Select(Classify).ToList();
        var index = 0; var i = 0;
        while (i < graphemes.Count)
        {
            var kind = kinds[i];
            if (kind is Kind.Letter or Kind.Numeric or Kind.ExtendNumLet)
            {
                var start = i; i++;
                while (i < graphemes.Count)
                {
                    var current = kinds[i];
                    if (current is Kind.Letter or Kind.Numeric or Kind.ExtendNumLet) { i++; continue; }
                    // WB6/7 and WB11/12: a single middle character between letters (or digits) does not break.
                    if (i + 1 < graphemes.Count)
                    {
                        var before = kinds[i - 1]; var after = kinds[i + 1];
                        var letterJoin = current is Kind.MidLetter or Kind.MidNumLet && before == Kind.Letter && after == Kind.Letter;
                        var numberJoin = current is Kind.MidNum or Kind.MidNumLet && before == Kind.Numeric && after == Kind.Numeric;
                        if (letterJoin || numberJoin) { i += 2; continue; }
                    }
                    break;
                }
                var segment = string.Concat(graphemes.Skip(start).Take(i - start));
                var wordLike = kinds.Skip(start).Take(i - start).Any(k => k is Kind.Letter or Kind.Numeric);
                yield return new(segment, index, wordLike); index += segment.Length;
                continue;
            }
            if (kind == Kind.Space)
            {
                // Consecutive horizontal whitespace forms one segment (WB3d); line breaks stand alone.
                var start = i; i++;
                if (graphemes[start] is not ("\n" or "\r" or "\r\n"))
                    while (i < graphemes.Count && kinds[i] == Kind.Space && graphemes[i] is not ("\n" or "\r" or "\r\n")) i++;
                var segment = string.Concat(graphemes.Skip(start).Take(i - start));
                yield return new(segment, index, false); index += segment.Length;
                continue;
            }
            yield return new(graphemes[i], index, kind == Kind.Ideograph); index += graphemes[i].Length; i++;
        }
    }
}

/// <summary>Word-boundary cursor movement (word-navigation.ts).</summary>
public static class WordNavigation
{
    private static readonly Regex Punctuation = new(@"[(){}[\]<>.,;:'""!?+\-=*/\\|&%^$#@~`]");

    public static int FindWordBackward(string text, int cursor, Func<string, IEnumerable<WordSegment>>? segment = null, Func<string, bool>? isAtomic = null)
    {
        if (cursor <= 0) return 0;
        var segments = (segment ?? WordSegmenter.Segment)(text[..cursor]).ToList();
        var newCursor = cursor;
        bool Atomic(string value) => isAtomic?.Invoke(value) == true;
        while (segments.Count > 0 && !Atomic(segments[^1].Segment) && TextUtils.IsWhitespaceChar(segments[^1].Segment))
        { newCursor -= segments[^1].Segment.Length; segments.RemoveAt(segments.Count - 1); }
        if (segments.Count == 0) return newCursor;
        var last = segments[^1];
        if (Atomic(last.Segment)) newCursor -= last.Segment.Length;
        else if (last.IsWordLike)
        {
            var matches = Punctuation.Matches(last.Segment);
            if (matches.Count == 0) newCursor -= last.Segment.Length;
            else { var lastMatch = matches[^1]; newCursor -= last.Segment.Length - (lastMatch.Index + lastMatch.Length); }
        }
        else
            while (segments.Count > 0 && !Atomic(segments[^1].Segment) && !segments[^1].IsWordLike && !TextUtils.IsWhitespaceChar(segments[^1].Segment))
            { newCursor -= segments[^1].Segment.Length; segments.RemoveAt(segments.Count - 1); }
        return newCursor;
    }

    public static int FindWordForward(string text, int cursor, Func<string, IEnumerable<WordSegment>>? segment = null, Func<string, bool>? isAtomic = null)
    {
        if (cursor >= text.Length) return text.Length;
        using var iterator = (segment ?? WordSegmenter.Segment)(text[cursor..]).GetEnumerator();
        bool Atomic(string value) => isAtomic?.Invoke(value) == true;
        var newCursor = cursor; var hasNext = iterator.MoveNext();
        while (hasNext && !Atomic(iterator.Current.Segment) && TextUtils.IsWhitespaceChar(iterator.Current.Segment))
        { newCursor += iterator.Current.Segment.Length; hasNext = iterator.MoveNext(); }
        if (!hasNext) return newCursor;
        var current = iterator.Current;
        if (Atomic(current.Segment)) newCursor += current.Segment.Length;
        else if (current.IsWordLike) { var match = Punctuation.Match(current.Segment); newCursor += match.Success ? match.Index : current.Segment.Length; }
        else
            while (hasNext && !Atomic(iterator.Current.Segment) && !iterator.Current.IsWordLike && !TextUtils.IsWhitespaceChar(iterator.Current.Segment))
            { newCursor += iterator.Current.Segment.Length; hasNext = iterator.MoveNext(); }
        return newCursor;
    }
}

/// <summary>Fuzzy matching: all query characters in order; lower scores are better.</summary>
public static partial class Fuzzy
{
    public readonly record struct FuzzyMatch(bool Matches, double Score);
    [GeneratedRegex(@"^(?<letters>[a-z]+)(?<digits>[0-9]+)$")] private static partial Regex AlphaNumeric();
    [GeneratedRegex(@"^(?<digits>[0-9]+)(?<letters>[a-z]+)$")] private static partial Regex NumericAlpha();

    public static FuzzyMatch Match(string query, string text)
    {
        var queryLower = query.ToLowerInvariant(); var textLower = text.ToLowerInvariant();
        FuzzyMatch MatchQuery(string normalized)
        {
            if (normalized.Length == 0) return new(true, 0);
            if (normalized.Length > textLower.Length) return new(false, 0);
            int queryIndex = 0, lastMatch = -1, consecutive = 0; double score = 0;
            while (queryIndex < normalized.Length)
            {
                var i = textLower.IndexOf(normalized[queryIndex], lastMatch + 1);
                if (i == -1) break;
                var boundary = i == 0 || TextUtils.IsJsWhitespace(textLower[i - 1]) || textLower[i - 1] is '-' or '_' or '.' or '/' or ':';
                if (lastMatch == i - 1) { consecutive++; score -= consecutive * 5; }
                else { consecutive = 0; if (lastMatch >= 0) score += (i - lastMatch - 1) * 2; }
                if (boundary) score -= 10;
                score += i * 0.1;
                lastMatch = i; queryIndex++;
            }
            if (queryIndex < normalized.Length) return new(false, 0);
            if (normalized == textLower) score -= 100;
            return new(true, score);
        }
        var primary = MatchQuery(queryLower);
        if (primary.Matches) return primary;
        var alpha = AlphaNumeric().Match(queryLower); var numeric = NumericAlpha().Match(queryLower);
        var swapped = alpha.Success ? alpha.Groups["digits"].Value + alpha.Groups["letters"].Value :
            numeric.Success ? numeric.Groups["letters"].Value + numeric.Groups["digits"].Value : "";
        if (swapped.Length == 0) return primary;
        var swappedMatch = MatchQuery(swapped);
        return swappedMatch.Matches ? new(true, swappedMatch.Score + 5) : primary;
    }

    /// <summary>Filters and sorts by match quality; whitespace- and slash-separated tokens must all match.</summary>
    public static List<T> Filter<T>(IReadOnlyList<T> items, string query, Func<T, string> getText)
    {
        if (TextUtils.JsTrim(query).Length == 0) return [.. items];
        var tokens = Regex.Split(TextUtils.JsTrim(query), @"[\s/]+").Where(token => token.Length > 0).ToList();
        if (tokens.Count == 0) return [.. items];
        var results = new List<(T Item, double Score, int Order)>();
        var order = 0;
        foreach (var item in items)
        {
            var text = getText(item); double total = 0; var all = true;
            foreach (var token in tokens)
            {
                var match = Match(token, text);
                if (match.Matches) total += match.Score; else { all = false; break; }
            }
            if (all) results.Add((item, total, order));
            order++;
        }
        return results.OrderBy(result => result.Score).ThenBy(result => result.Order).Select(result => result.Item).ToList();
    }
}
