// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/tools/edit-diff.ts
// Shared diff computation for the edit tool. Upstream's `diff` package (jsdiff 8.0.4) is JsDiff.cs. Strings are UTF-16 as in JavaScript:
// offsets, lengths and substrings count UTF-16 code units, and Unicode normalization leaves lone surrogates as they are.
using System.Globalization;
using System.Text;

namespace PiSharp.Durable.Tools;

/// <summary>One exact-text replacement (upstream <c>Edit</c>).</summary>
/// <param name="OldText">The text to replace; unique in the original file.</param>
/// <param name="NewText">The replacement.</param>
internal sealed record EditReplacement(string OldText, string NewText);

/// <summary>A match of an edit's old text (upstream <c>FuzzyMatchResult</c>).</summary>
/// <param name="Found">Whether a match was found.</param>
/// <param name="Index">Where the match starts, in <paramref name="ContentForReplacement"/>.</param>
/// <param name="MatchLength">Length of the matched text.</param>
/// <param name="UsedFuzzyMatch">Whether fuzzy matching was used (false: an exact match).</param>
/// <param name="ContentForReplacement">The content to replace in: the original for an exact match, the normalized content for a fuzzy
/// one.</param>
internal sealed record FuzzyMatchResult(bool Found, int Index, int MatchLength, bool UsedFuzzyMatch, string ContentForReplacement);

/// <summary>The content edits were matched against and the edited content (upstream <c>AppliedEditsResult</c>).</summary>
internal sealed record AppliedEditsResult(string BaseContent, string NewContent);

/// <summary>A display diff and the first changed line of the new file.</summary>
internal sealed record DiffStringResult(string Diff, int? FirstChangedLine);

/// <summary>Upstream <c>edit-diff.ts</c>.</summary>
internal static class EditDiff
{
    private sealed record MatchedEdit(int EditIndex, int MatchIndex, int MatchLength, string NewText);

    private readonly record struct LineSpan(int Start, int End);

    public static string DetectLineEnding(string content)
    {
        var crlfIndex = content.IndexOf("\r\n", StringComparison.Ordinal);
        var lfIndex = content.IndexOf('\n');
        if (lfIndex == -1) return "\n";
        if (crlfIndex == -1) return "\n";
        return crlfIndex < lfIndex ? "\r\n" : "\n";
    }

    public static string NormalizeToLF(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static string RestoreLineEndings(string text, string ending) =>
        ending == "\r\n" ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;

    /// <summary>
    /// Normalizes text for fuzzy matching: NFKC, trailing whitespace stripped from each line, smart quotes to ASCII quotes, Unicode dashes and
    /// hyphens to <c>-</c>, special Unicode spaces to a space.
    /// </summary>
    public static string NormalizeForFuzzyMatch(string text)
    {
        var lines = Unicode.Normalize(text, NormalizationForm.FormKC).Split('\n');
        for (var index = 0; index < lines.Length; index++) lines[index] = TrimEnd(lines[index]);
        var result = string.Join('\n', lines).ToCharArray();
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = result[index] switch
            {
                // Smart single quotes.
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                // Smart double quotes.
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                // Hyphen, non-breaking hyphen, figure dash, en dash, em dash, horizontal bar, minus.
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                // NBSP, U+2002-U+200A spaces, narrow NBSP, medium mathematical space, ideographic space.
                '\u00A0' or (>= '\u2002' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000' => ' ',
                var other => other,
            };
        }
        return new string(result);
    }

    /// <summary>JavaScript's <c>String.prototype.trimEnd</c>: strips WhiteSpace and LineTerminator code points.</summary>
    private static string TrimEnd(string line)
    {
        var end = line.Length;
        while (end > 0 && IsJsWhitespace(line[end - 1])) end--;
        return line[..end];
    }

    private static bool IsJsWhitespace(char value) => value is '\t' or '\v' or '\f' or ' ' or '\u00A0' or '\uFEFF' or '\n' or '\r' or '\u2028'
        or '\u2029' || CharUnicodeInfo.GetUnicodeCategory(value) == UnicodeCategory.SpaceSeparator;

    /// <summary><c>content.match(/[^\n]*\n|[^\n]+/g) ?? []</c>: the lines, each with its newline.</summary>
    private static List<string> SplitLinesWithEndings(string content)
    {
        var lines = new List<string>();
        for (var start = 0; start < content.Length;)
        {
            var newline = content.IndexOf('\n', start);
            var end = newline == -1 ? content.Length : newline + 1;
            lines.Add(content[start..end]);
            start = end;
        }
        return lines;
    }

    private static List<LineSpan> GetLineSpans(string content)
    {
        var offset = 0;
        return [.. SplitLinesWithEndings(content).Select(line =>
        {
            var span = new LineSpan(offset, offset + line.Length);
            offset = span.End;
            return span;
        })];
    }

    private static (int StartLine, int EndLine) GetReplacementLineRange(List<LineSpan> lines, MatchedEdit replacement)
    {
        var replacementStart = replacement.MatchIndex;
        var replacementEnd = replacement.MatchIndex + replacement.MatchLength;
        var startLine = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (replacementStart >= lines[index].Start && replacementStart < lines[index].End)
            {
                startLine = index;
                break;
            }
        }
        if (startLine == -1) throw new InvalidOperationException("Replacement range is outside the base content.");
        var endLine = startLine;
        while (endLine < lines.Count && lines[endLine].End < replacementEnd) endLine++;
        if (endLine >= lines.Count) throw new InvalidOperationException("Replacement range is outside the base content.");
        return (startLine, endLine + 1);
    }

    private static string ApplyReplacements(string content, IReadOnlyList<MatchedEdit> replacements, int offset = 0)
    {
        var result = content;
        for (var index = replacements.Count - 1; index >= 0; index--)
        {
            var replacement = replacements[index];
            var matchIndex = replacement.MatchIndex - offset;
            result = result[..matchIndex] + replacement.NewText + result[(matchIndex + replacement.MatchLength)..];
        }
        return result;
    }

    /// <summary>
    /// Applies replacements matched against <paramref name="baseContent"/> to <paramref name="originalContent"/> while preserving unchanged
    /// line blocks from the original. Each replacement is widened to the lines it touches, those lines are rewritten from the normalized base,
    /// and all other lines are copied back from the original, so duplicate normalized lines cannot be aligned to the wrong occurrence.
    /// </summary>
    private static string ApplyReplacementsPreservingUnchangedLines(string originalContent, string baseContent, IReadOnlyList<MatchedEdit> replacements)
    {
        var originalLines = SplitLinesWithEndings(originalContent);
        var baseLines = GetLineSpans(baseContent);
        if (originalLines.Count != baseLines.Count)
            throw new InvalidOperationException("Cannot preserve unchanged lines because the base content has a different line count.");

        var groups = new List<(int StartLine, int EndLine, List<MatchedEdit> Replacements)>();
        foreach (var replacement in replacements.OrderBy(replacement => replacement.MatchIndex))
        {
            var range = GetReplacementLineRange(baseLines, replacement);
            if (groups.Count > 0 && range.StartLine < groups[^1].EndLine)
            {
                var current = groups[^1];
                current.Replacements.Add(replacement);
                groups[^1] = (current.StartLine, Math.Max(current.EndLine, range.EndLine), current.Replacements);
                continue;
            }
            groups.Add((range.StartLine, range.EndLine, [replacement]));
        }

        var originalLineIndex = 0;
        var result = new StringBuilder();
        foreach (var group in groups)
        {
            foreach (var line in originalLines.Skip(originalLineIndex).Take(Math.Max(0, group.StartLine - originalLineIndex))) result.Append(line);
            var groupStartOffset = baseLines[group.StartLine].Start;
            var groupEndOffset = baseLines[group.EndLine - 1].End;
            result.Append(ApplyReplacements(baseContent[groupStartOffset..groupEndOffset], group.Replacements, groupStartOffset));
            originalLineIndex = group.EndLine;
        }
        foreach (var line in originalLines.Skip(originalLineIndex)) result.Append(line);
        return result.ToString();
    }

    /// <summary>Finds <paramref name="oldText"/> in <paramref name="content"/>: an exact match first, then a match in fuzzy-normalized
    /// space (trailing whitespace stripped, Unicode quotes and dashes normalized to ASCII).</summary>
    public static FuzzyMatchResult FuzzyFindText(string content, string oldText)
    {
        var exactIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exactIndex != -1) return new FuzzyMatchResult(true, exactIndex, oldText.Length, false, content);

        // Work entirely in normalized space; callers compute replacements there and decide how much of it to write back.
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        var fuzzyIndex = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal);
        if (fuzzyIndex == -1) return new FuzzyMatchResult(false, -1, 0, false, content);
        return new FuzzyMatchResult(true, fuzzyIndex, fuzzyOldText.Length, true, fuzzyContent);
    }

    /// <summary>Strips a UTF-8 byte-order mark; returns the mark (or "") and the text without it.</summary>
    public static (string Bom, string Text) StripBom(string content) =>
        content.StartsWith('\uFEFF') ? ("\uFEFF", content[1..]) : ("", content);

    /// <summary><c>fuzzyContent.split(fuzzyOldText).length - 1</c>.</summary>
    private static int CountOccurrences(string content, string oldText)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        // Splitting on "" yields every UTF-16 code unit, and nothing for empty content.
        if (fuzzyOldText.Length == 0) return fuzzyContent.Length - 1;
        var count = 0;
        for (var index = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal); index != -1;
            index = fuzzyContent.IndexOf(fuzzyOldText, index + fuzzyOldText.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static InvalidOperationException NotFoundError(string path, int editIndex, int totalEdits) => new(totalEdits == 1
        ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
        : $"Could not find edits[{editIndex}] in {path}. The oldText must match exactly including all whitespace and newlines.");

    private static InvalidOperationException DuplicateError(string path, int editIndex, int totalEdits, int occurrences) => new(totalEdits == 1
        ? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
        : $"Found {occurrences} occurrences of edits[{editIndex}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");

    private static InvalidOperationException EmptyOldTextError(string path, int editIndex, int totalEdits) => new(totalEdits == 1
        ? $"oldText must not be empty in {path}."
        : $"edits[{editIndex}].oldText must not be empty in {path}.");

    private static InvalidOperationException NoChangeError(string path, int totalEdits) => new(totalEdits == 1
        ? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
        : $"No changes made to {path}. The replacements produced identical content.");

    /// <summary>
    /// Applies one or more exact-text replacements to LF-normalized content. All edits are matched against the same original content, then
    /// applied in reverse order so offsets stay stable. If any edit needs fuzzy matching, the operation runs in fuzzy-normalized space and
    /// overlays those line-level changes onto the original, so unchanged line blocks keep their original text.
    /// </summary>
    public static AppliedEditsResult ApplyEditsToNormalizedContent(string normalizedContent, IReadOnlyList<EditReplacement> edits, string path)
    {
        var normalizedEdits = edits.Select(edit => new EditReplacement(NormalizeToLF(edit.OldText), NormalizeToLF(edit.NewText))).ToList();
        for (var index = 0; index < normalizedEdits.Count; index++)
            if (normalizedEdits[index].OldText.Length == 0) throw EmptyOldTextError(path, index, normalizedEdits.Count);

        var usedFuzzyMatch = normalizedEdits.Any(edit => FuzzyFindText(normalizedContent, edit.OldText).UsedFuzzyMatch);
        var replacementBaseContent = usedFuzzyMatch ? NormalizeForFuzzyMatch(normalizedContent) : normalizedContent;

        var matchedEdits = new List<MatchedEdit>();
        for (var index = 0; index < normalizedEdits.Count; index++)
        {
            var edit = normalizedEdits[index];
            var matchResult = FuzzyFindText(replacementBaseContent, edit.OldText);
            if (!matchResult.Found) throw NotFoundError(path, index, normalizedEdits.Count);
            var occurrences = CountOccurrences(replacementBaseContent, edit.OldText);
            if (occurrences > 1) throw DuplicateError(path, index, normalizedEdits.Count, occurrences);
            matchedEdits.Add(new MatchedEdit(index, matchResult.Index, matchResult.MatchLength, edit.NewText));
        }

        matchedEdits = [.. matchedEdits.OrderBy(edit => edit.MatchIndex)];
        for (var index = 1; index < matchedEdits.Count; index++)
        {
            var previous = matchedEdits[index - 1];
            var current = matchedEdits[index];
            if (previous.MatchIndex + previous.MatchLength > current.MatchIndex)
                throw new InvalidOperationException(
                    $"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
        }

        var baseContent = normalizedContent;
        var newContent = usedFuzzyMatch
            ? ApplyReplacementsPreservingUnchangedLines(normalizedContent, replacementBaseContent, matchedEdits)
            : ApplyReplacements(replacementBaseContent, matchedEdits);
        if (baseContent == newContent) throw NoChangeError(path, normalizedEdits.Count);
        return new AppliedEditsResult(baseContent, newContent);
    }

    /// <summary>A standard unified patch with file headers only.</summary>
    public static string GenerateUnifiedPatch(string path, string oldContent, string newContent, int contextLines = 4) =>
        JsDiff.CreateTwoFilesPatch(path, path, oldContent, newContent, contextLines);

    /// <summary>A display-oriented diff with line numbers and context, and the first changed line number in the new file.</summary>
    public static DiffStringResult GenerateDiffString(string oldContent, string newContent, int contextLines = 4)
    {
        var parts = JsDiff.DiffLines(oldContent, newContent);
        var output = new List<string>();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var maxLineNum = Math.Max(oldLines.Length, newLines.Length);
        var lineNumWidth = maxLineNum.ToString(CultureInfo.InvariantCulture).Length;
        string Number(int value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth, ' ');
        var ellipsis = $" {"".PadLeft(lineNumWidth, ' ')} ...";

        int oldLineNum = 1, newLineNum = 1;
        var lastWasChange = false;
        int? firstChangedLine = null;

        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var raw = part.Value.Split('\n').ToList();
            if (raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);

            if (part.Added || part.Removed)
            {
                // Capture the first changed line (in the new file).
                firstChangedLine ??= newLineNum;
                foreach (var line in raw)
                {
                    if (part.Added)
                    {
                        output.Add($"+{Number(newLineNum)} {line}");
                        newLineNum++;
                    }
                    else
                    {
                        output.Add($"-{Number(oldLineNum)} {line}");
                        oldLineNum++;
                    }
                }
                lastWasChange = true;
                continue;
            }

            // Context lines: only a few before and after changes.
            var nextPartIsChange = index < parts.Count - 1 && (parts[index + 1].Added || parts[index + 1].Removed);
            var hasLeadingChange = lastWasChange;
            var hasTrailingChange = nextPartIsChange;
            void Context(IEnumerable<string> lines)
            {
                foreach (var line in lines)
                {
                    output.Add($" {Number(oldLineNum)} {line}");
                    oldLineNum++;
                    newLineNum++;
                }
            }

            if (hasLeadingChange && hasTrailingChange)
            {
                if (raw.Count <= contextLines * 2) Context(raw);
                else
                {
                    var leadingLines = raw.Take(contextLines).ToList();
                    var trailingLines = raw.Skip(raw.Count - contextLines).ToList();
                    var skippedLines = raw.Count - leadingLines.Count - trailingLines.Count;
                    Context(leadingLines);
                    output.Add(ellipsis);
                    oldLineNum += skippedLines;
                    newLineNum += skippedLines;
                    Context(trailingLines);
                }
            }
            else if (hasLeadingChange)
            {
                var shownLines = raw.Take(contextLines).ToList();
                var skippedLines = raw.Count - shownLines.Count;
                Context(shownLines);
                if (skippedLines > 0)
                {
                    output.Add(ellipsis);
                    oldLineNum += skippedLines;
                    newLineNum += skippedLines;
                }
            }
            else if (hasTrailingChange)
            {
                var skippedLines = Math.Max(0, raw.Count - contextLines);
                if (skippedLines > 0)
                {
                    output.Add(ellipsis);
                    oldLineNum += skippedLines;
                    newLineNum += skippedLines;
                }
                Context(raw.Skip(skippedLines));
            }
            else
            {
                // Skip these context lines entirely.
                oldLineNum += raw.Count;
                newLineNum += raw.Count;
            }
            lastWasChange = false;
        }
        return new DiffStringResult(string.Join('\n', output), firstChangedLine);
    }
}
