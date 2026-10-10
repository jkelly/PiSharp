// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/diff.ts.
// Diff.diffWords is a port of jsdiff 8.0.4 (BSD-3-Clause, Copyright (c) 2009-2015 Kevin Decker and contributors):
// libesm/diff/base.js (Myers diff with the diagonal pruning), libesm/diff/word.js (tokenizer, join, whitespace dedupe) and
// libesm/util/string.js (prefix/suffix helpers), without the callback, timeout, segmenter and ignoreCase options.
using System.Text.RegularExpressions;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <param name="FilePath">File path (unused, kept for API compatibility)</param>
internal sealed record RenderDiffOptions(string? FilePath = null);

internal static partial class Diff
{
    // ECMAScript semantics: \d is ASCII and "." stops at line terminators.
    [GeneratedRegex(@"^([+\-\s])(\s*[0-9]*)\s([^\n\r  ]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DiffLine();

    /// <summary>
    /// Parse diff line to extract prefix, line number, and content.
    /// Format: "+123 content" or "-123 content" or " 123 content" or "     ..."
    /// </summary>
    private static (string Prefix, string LineNum, string Content)? ParseDiffLine(string line)
    {
        var match = DiffLine().Match(line);
        if (!match.Success) return null;
        return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
    }

    /// <summary>Replace tabs with spaces for consistent rendering.</summary>
    private static string ReplaceTabs(string text) => text.Replace("\t", "   ", StringComparison.Ordinal);

    /// <summary>
    /// Compute word-level diff and render with inverse on changed parts.
    /// Uses diffWords which groups whitespace with adjacent words for cleaner highlighting.
    /// Strips leading whitespace from inverse to avoid highlighting indentation.
    /// </summary>
    private static (string RemovedLine, string AddedLine) RenderIntraLineDiff(string oldContent, string newContent)
    {
        var wordDiff = JsDiff.DiffWords(oldContent, newContent);

        var removedLine = "";
        var addedLine = "";
        var isFirstRemoved = true;
        var isFirstAdded = true;

        foreach (var part in wordDiff)
        {
            if (part.Removed)
            {
                var value = part.Value;
                // Strip leading whitespace from the first removed part
                if (isFirstRemoved)
                {
                    var leadingWs = JsDiff.LeadingWs(value);
                    value = value[leadingWs.Length..];
                    removedLine += leadingWs;
                    isFirstRemoved = false;
                }
                if (value.Length > 0) removedLine += theme.Inverse(value);
            }
            else if (part.Added)
            {
                var value = part.Value;
                // Strip leading whitespace from the first added part
                if (isFirstAdded)
                {
                    var leadingWs = JsDiff.LeadingWs(value);
                    value = value[leadingWs.Length..];
                    addedLine += leadingWs;
                    isFirstAdded = false;
                }
                if (value.Length > 0) addedLine += theme.Inverse(value);
            }
            else
            {
                removedLine += part.Value;
                addedLine += part.Value;
            }
        }

        return (removedLine, addedLine);
    }

    /// <summary>
    /// Render a diff string with colored lines and intra-line change highlighting.
    /// - Context lines: dim/gray
    /// - Removed lines: red, with inverse on changed tokens
    /// - Added lines: green, with inverse on changed tokens
    /// </summary>
    public static string RenderDiff(string diffText, RenderDiffOptions? options = null)
    {
        _ = options;
        var lines = diffText.Split('\n');
        var result = new List<string>();

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var parsed = ParseDiffLine(line);

            if (parsed is not { } p0)
            {
                result.Add(theme.Fg("toolDiffContext", line));
                i++;
                continue;
            }

            if (p0.Prefix == "-")
            {
                // Collect consecutive removed lines
                var removedLines = new List<(string LineNum, string Content)>();
                while (i < lines.Length)
                {
                    var p = ParseDiffLine(lines[i]);
                    if (p is null || p.Value.Prefix != "-") break;
                    removedLines.Add((p.Value.LineNum, p.Value.Content));
                    i++;
                }

                // Collect consecutive added lines
                var addedLines = new List<(string LineNum, string Content)>();
                while (i < lines.Length)
                {
                    var p = ParseDiffLine(lines[i]);
                    if (p is null || p.Value.Prefix != "+") break;
                    addedLines.Add((p.Value.LineNum, p.Value.Content));
                    i++;
                }

                // Only do intra-line diffing when there's exactly one removed and one added line
                // (indicating a single line modification). Otherwise, show lines as-is.
                if (removedLines.Count == 1 && addedLines.Count == 1)
                {
                    var removed = removedLines[0];
                    var added = addedLines[0];

                    var (removedLine, addedLine) = RenderIntraLineDiff(ReplaceTabs(removed.Content), ReplaceTabs(added.Content));

                    result.Add(theme.Fg("toolDiffRemoved", $"-{removed.LineNum} {removedLine}"));
                    result.Add(theme.Fg("toolDiffAdded", $"+{added.LineNum} {addedLine}"));
                }
                else
                {
                    // Show all removed lines first, then all added lines
                    foreach (var removed in removedLines)
                        result.Add(theme.Fg("toolDiffRemoved", $"-{removed.LineNum} {ReplaceTabs(removed.Content)}"));
                    foreach (var added in addedLines)
                        result.Add(theme.Fg("toolDiffAdded", $"+{added.LineNum} {ReplaceTabs(added.Content)}"));
                }
            }
            else if (p0.Prefix == "+")
            {
                // Standalone added line
                result.Add(theme.Fg("toolDiffAdded", $"+{p0.LineNum} {ReplaceTabs(p0.Content)}"));
                i++;
            }
            else
            {
                // Context line
                result.Add(theme.Fg("toolDiffContext", $" {p0.LineNum} {ReplaceTabs(p0.Content)}"));
                i++;
            }
        }

        return string.Join("\n", result);
    }
}

/// <summary>A jsdiff change object.</summary>
internal sealed class JsDiffChange
{
    public int Count;
    public bool Added;
    public bool Removed;
    public string Value = "";
    public JsDiffChange? Previous;
}

/// <summary>jsdiff's diffWords (word diff with surrounding whitespace folded into tokens).</summary>
internal static partial class JsDiff
{
    private const string ExtendedWordChars = @"a-zA-Z0-9_­À-ÖØ-öø-ˆˈ-˗˞-˿Ḁ-ỿ";

    [GeneratedRegex("[" + ExtendedWordChars + @"]+|[\t\n\v\f\r    -     　﻿]+|[^" + ExtendedWordChars + "]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenizeIncludingWhitespace();

    private static bool IsWs(char c) => TextUtils.IsJsWhitespace(c);
    /// <summary>/\s/.test(part): the part contains a whitespace character.</summary>
    private static bool HasWs(string part) => part.Any(IsWs);

    public static List<JsDiffChange> DiffWords(string oldStr, string newStr)
    {
        var oldTokens = Tokenize(oldStr).Where(token => token.Length > 0).ToList();
        var newTokens = Tokenize(newStr).Where(token => token.Length > 0).ToList();
        return PostProcess(DiffTokens(oldTokens, newTokens));
    }

    private static List<string> Tokenize(string value)
    {
        // The 'u' flag iterates code points; [^...] then matches a whole surrogate pair. Merge split pairs back.
        var parts = new List<string>();
        foreach (Match match in TokenizeIncludingWhitespace().Matches(value))
        {
            if (parts.Count > 0 && match.Value.Length == 1 && char.IsLowSurrogate(match.Value[0]) && parts[^1].Length == 1 && char.IsHighSurrogate(parts[^1][0]))
                parts[^1] += match.Value;
            else parts.Add(match.Value);
        }
        var tokens = new List<string>();
        string? prevPart = null;
        foreach (var part in parts)
        {
            if (HasWs(part))
            {
                if (prevPart is null) tokens.Add(part);
                else { var last = tokens[^1]; tokens.RemoveAt(tokens.Count - 1); tokens.Add(last + part); }
            }
            else if (prevPart is not null && HasWs(prevPart))
            {
                if (tokens[^1] == prevPart) { var last = tokens[^1]; tokens.RemoveAt(tokens.Count - 1); tokens.Add(last + part); }
                else tokens.Add(prevPart + part);
            }
            else tokens.Add(part);
            prevPart = part;
        }
        return tokens;
    }

    private static bool TokenEquals(string left, string right) => TextUtils.JsTrim(left) == TextUtils.JsTrim(right);

    private static string Join(IEnumerable<string> tokens)
    {
        var builder = new System.Text.StringBuilder();
        var index = 0;
        foreach (var token in tokens)
        {
            builder.Append(index == 0 ? token : TextUtils.JsTrimStart(token));
            index++;
        }
        return builder.ToString();
    }

    private sealed class PathState { public int OldPos; public JsDiffChange? LastComponent; }

    private static List<JsDiffChange> DiffTokens(List<string> oldTokens, List<string> newTokens)
    {
        var newLen = newTokens.Count; var oldLen = oldTokens.Count;
        var editLength = 1;
        var maxEditLength = newLen + oldLen;
        var bestPath = new Dictionary<int, PathState?> { [0] = new PathState { OldPos = -1 } };

        var newPos = ExtractCommon(bestPath[0]!, newTokens, oldTokens, 0);
        if (bestPath[0]!.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
            return BuildValues(bestPath[0]!.LastComponent, newTokens, oldTokens);

        var minDiagonalToConsider = int.MinValue; var maxDiagonalToConsider = int.MaxValue;
        while (editLength <= maxEditLength)
        {
            for (var diagonalPath = Math.Max(minDiagonalToConsider, -editLength); diagonalPath <= Math.Min(maxDiagonalToConsider, editLength); diagonalPath += 2)
            {
                PathState basePath;
                var removePath = bestPath.GetValueOrDefault(diagonalPath - 1);
                var addPath = bestPath.GetValueOrDefault(diagonalPath + 1);
                if (removePath is not null) bestPath[diagonalPath - 1] = null;

                var canAdd = false;
                if (addPath is not null)
                {
                    var addPathNewPos = addPath.OldPos - diagonalPath;
                    canAdd = 0 <= addPathNewPos && addPathNewPos < newLen;
                }
                var canRemove = removePath is not null && removePath.OldPos + 1 < oldLen;
                if (!canAdd && !canRemove)
                {
                    bestPath[diagonalPath] = null;
                    continue;
                }

                if (!canRemove || (canAdd && removePath!.OldPos < addPath!.OldPos)) basePath = AddToPath(addPath!, true, false, 0);
                else basePath = AddToPath(removePath!, false, true, 1);

                newPos = ExtractCommon(basePath, newTokens, oldTokens, diagonalPath);
                if (basePath.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
                    return BuildValues(basePath.LastComponent, newTokens, oldTokens);
                bestPath[diagonalPath] = basePath;
                if (basePath.OldPos + 1 >= oldLen) maxDiagonalToConsider = Math.Min(maxDiagonalToConsider, diagonalPath - 1);
                if (newPos + 1 >= newLen) minDiagonalToConsider = Math.Max(minDiagonalToConsider, diagonalPath + 1);
            }
            editLength++;
        }
        return [];
    }

    private static PathState AddToPath(PathState path, bool added, bool removed, int oldPosInc)
    {
        var last = path.LastComponent;
        if (last is not null && last.Added == added && last.Removed == removed)
            return new PathState { OldPos = path.OldPos + oldPosInc, LastComponent = new JsDiffChange { Count = last.Count + 1, Added = added, Removed = removed, Previous = last.Previous } };
        return new PathState { OldPos = path.OldPos + oldPosInc, LastComponent = new JsDiffChange { Count = 1, Added = added, Removed = removed, Previous = last } };
    }

    private static int ExtractCommon(PathState basePath, List<string> newTokens, List<string> oldTokens, int diagonalPath)
    {
        var newLen = newTokens.Count; var oldLen = oldTokens.Count;
        var oldPos = basePath.OldPos; var newPos = oldPos - diagonalPath; var commonCount = 0;
        while (newPos + 1 < newLen && oldPos + 1 < oldLen && TokenEquals(oldTokens[oldPos + 1], newTokens[newPos + 1]))
        {
            newPos++; oldPos++; commonCount++;
        }
        if (commonCount > 0) basePath.LastComponent = new JsDiffChange { Count = commonCount, Previous = basePath.LastComponent };
        basePath.OldPos = oldPos;
        return newPos;
    }

    private static List<JsDiffChange> BuildValues(JsDiffChange? lastComponent, List<string> newTokens, List<string> oldTokens)
    {
        var components = new List<JsDiffChange>();
        while (lastComponent is not null)
        {
            components.Add(lastComponent);
            var next = lastComponent.Previous;
            lastComponent.Previous = null;
            lastComponent = next;
        }
        components.Reverse();
        int newPos = 0, oldPos = 0;
        foreach (var component in components)
        {
            if (!component.Removed)
            {
                component.Value = Join(newTokens.Skip(newPos).Take(component.Count));
                newPos += component.Count;
                if (!component.Added) oldPos += component.Count;
            }
            else
            {
                component.Value = Join(oldTokens.Skip(oldPos).Take(component.Count));
                oldPos += component.Count;
            }
        }
        return components;
    }

    private static List<JsDiffChange> PostProcess(List<JsDiffChange> changes)
    {
        JsDiffChange? lastKeep = null, insertion = null, deletion = null;
        foreach (var change in changes)
        {
            if (change.Added) insertion = change;
            else if (change.Removed) deletion = change;
            else
            {
                if (insertion is not null || deletion is not null) DedupeWhitespaceInChangeObjects(lastKeep, deletion, insertion, change);
                lastKeep = change;
                insertion = null;
                deletion = null;
            }
        }
        if (insertion is not null || deletion is not null) DedupeWhitespaceInChangeObjects(lastKeep, deletion, insertion, null);
        return changes;
    }

    private static void DedupeWhitespaceInChangeObjects(JsDiffChange? startKeep, JsDiffChange? deletion, JsDiffChange? insertion, JsDiffChange? endKeep)
    {
        if (deletion is not null && insertion is not null)
        {
            var oldWsPrefix = LeadingWs(deletion.Value); var oldWsSuffix = TrailingWs(deletion.Value);
            var newWsPrefix = LeadingWs(insertion.Value); var newWsSuffix = TrailingWs(insertion.Value);
            if (startKeep is not null)
            {
                var commonWsPrefix = LongestCommonPrefix(oldWsPrefix, newWsPrefix);
                startKeep.Value = ReplaceSuffix(startKeep.Value, newWsPrefix, commonWsPrefix);
                deletion.Value = RemovePrefix(deletion.Value, commonWsPrefix);
                insertion.Value = RemovePrefix(insertion.Value, commonWsPrefix);
            }
            if (endKeep is not null)
            {
                var commonWsSuffix = LongestCommonSuffix(oldWsSuffix, newWsSuffix);
                endKeep.Value = ReplacePrefix(endKeep.Value, newWsSuffix, commonWsSuffix);
                deletion.Value = RemoveSuffix(deletion.Value, commonWsSuffix);
                insertion.Value = RemoveSuffix(insertion.Value, commonWsSuffix);
            }
        }
        else if (insertion is not null)
        {
            if (startKeep is not null)
            {
                var ws = LeadingWs(insertion.Value);
                insertion.Value = insertion.Value[ws.Length..];
            }
            if (endKeep is not null)
            {
                var ws = LeadingWs(endKeep.Value);
                endKeep.Value = endKeep.Value[ws.Length..];
            }
        }
        else if (startKeep is not null && endKeep is not null)
        {
            var newWsFull = LeadingWs(endKeep.Value);
            var delWsStart = LeadingWs(deletion!.Value); var delWsEnd = TrailingWs(deletion.Value);
            var newWsStart = LongestCommonPrefix(newWsFull, delWsStart);
            deletion.Value = RemovePrefix(deletion.Value, newWsStart);
            var newWsEnd = LongestCommonSuffix(RemovePrefix(newWsFull, newWsStart), delWsEnd);
            deletion.Value = RemoveSuffix(deletion.Value, newWsEnd);
            endKeep.Value = ReplacePrefix(endKeep.Value, newWsFull, newWsEnd);
            startKeep.Value = ReplaceSuffix(startKeep.Value, newWsFull, newWsFull[..(newWsFull.Length - newWsEnd.Length)]);
        }
        else if (endKeep is not null)
        {
            var endKeepWsPrefix = LeadingWs(endKeep.Value);
            var deletionWsSuffix = TrailingWs(deletion!.Value);
            var overlap = MaximumOverlap(deletionWsSuffix, endKeepWsPrefix);
            deletion.Value = RemoveSuffix(deletion.Value, overlap);
        }
        else if (startKeep is not null)
        {
            var startKeepWsSuffix = TrailingWs(startKeep.Value);
            var deletionWsPrefix = LeadingWs(deletion!.Value);
            var overlap = MaximumOverlap(startKeepWsSuffix, deletionWsPrefix);
            deletion.Value = RemovePrefix(deletion.Value, overlap);
        }
    }

    public static string LeadingWs(string value)
    {
        var end = 0;
        while (end < value.Length && IsWs(value[end])) end++;
        return value[..end];
    }

    public static string TrailingWs(string value)
    {
        int i;
        for (i = value.Length - 1; i >= 0; i--) if (!IsWs(value[i])) break;
        return value[(i + 1)..];
    }

    private static string LongestCommonPrefix(string str1, string str2)
    {
        int i;
        for (i = 0; i < str1.Length && i < str2.Length; i++) if (str1[i] != str2[i]) return str1[..i];
        return str1[..i];
    }

    private static string LongestCommonSuffix(string str1, string str2)
    {
        if (str1.Length == 0 || str2.Length == 0 || str1[^1] != str2[^1]) return "";
        int i;
        for (i = 0; i < str1.Length && i < str2.Length; i++)
            if (str1[str1.Length - (i + 1)] != str2[str2.Length - (i + 1)]) return SliceFromEnd(str1, i);
        return SliceFromEnd(str1, i);
        // JS str.slice(-i): i == 0 is the whole string.
        static string SliceFromEnd(string text, int count) => count == 0 ? text : text[Math.Max(0, text.Length - count)..];
    }

    private static string ReplacePrefix(string value, string oldPrefix, string newPrefix)
    {
        if (!value.StartsWith(oldPrefix, StringComparison.Ordinal)) throw new InvalidOperationException($"string doesn't start with prefix {oldPrefix}; this is a bug");
        return newPrefix + value[oldPrefix.Length..];
    }

    private static string ReplaceSuffix(string value, string oldSuffix, string newSuffix)
    {
        if (oldSuffix.Length == 0) return value + newSuffix;
        if (!value.EndsWith(oldSuffix, StringComparison.Ordinal)) throw new InvalidOperationException($"string doesn't end with suffix {oldSuffix}; this is a bug");
        return value[..^oldSuffix.Length] + newSuffix;
    }

    private static string RemovePrefix(string value, string oldPrefix) => ReplacePrefix(value, oldPrefix, "");
    private static string RemoveSuffix(string value, string oldSuffix) => ReplaceSuffix(value, oldSuffix, "");

    private static string MaximumOverlap(string string1, string string2) => string2[..OverlapCount(string1, string2)];

    private static int OverlapCount(string a, string b)
    {
        var startA = a.Length > b.Length ? a.Length - b.Length : 0;
        var endB = a.Length < b.Length ? a.Length : b.Length;
        if (endB == 0) return 0;
        var map = new int[endB];
        var k = 0;
        map[0] = 0;
        for (var j = 1; j < endB; j++)
        {
            map[j] = b[j] == b[k] ? map[k] : k;
            while (k > 0 && b[j] != b[k]) k = map[k];
            if (b[j] == b[k]) k++;
        }
        k = 0;
        for (var i = startA; i < a.Length; i++)
        {
            while (k > 0 && a[i] != b[k]) k = map[k];
            if (k < b.Length && a[i] == b[k]) k++;
        }
        return k;
    }
}
