using System.Collections.Immutable;
using System.Text;
using System.Globalization;

namespace PiSharp.Tools.Files;

public sealed record EditPlanOptions(int MaximumCharacters = 1_048_576, int MaximumEdits = 64);
public enum EditPlanFailure { EmptyOldText, NotFound, Ambiguous, Overlap, NoChange, InvalidRange, ResourceLimit }
public sealed class EditPlanException : Exception
{
    public EditPlanFailure Failure { get; }
    internal EditPlanException(EditPlanFailure failure, string message) : base(message) => Failure = failure;
}
public sealed record EditReplacement(int EditIndex, int MatchIndex, int MatchLength, string NewText);

/// <summary>Immutable all-original-content plan. Matching, overlap/no-op checks and output construction precede effects.</summary>
public sealed class EditPlan
{
    public string BaseContent { get; }
    public string NewContent { get; }
    public string Content { get; }
    public bool UsedFuzzyMatch { get; }
    public string OriginalLineEnding { get; }
    public bool HasBom { get; }
    public ImmutableArray<EditReplacement> Replacements { get; }
    private EditPlan(string before, string after, string content, bool fuzzy, string ending, bool bom, ImmutableArray<EditReplacement> replacements)
    { BaseContent = before; NewContent = after; Content = content; UsedFuzzyMatch = fuzzy; OriginalLineEnding = ending; HasBom = bom; Replacements = replacements; }

    public static EditPlan Create(string original, ImmutableArray<TextEdit> edits, string displayPath, EditPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(displayPath);
        options ??= new();
        if (options.MaximumCharacters <= 0 || options.MaximumEdits <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (edits.IsDefault) throw new ArgumentException("An initialized edit array is required.", nameof(edits));
        long characters = original.Length;
        if (edits.Length > options.MaximumEdits || original.Length > options.MaximumCharacters) throw Limit();
        var normalized = ImmutableArray.CreateBuilder<TextEdit>(edits.Length);
        var strict = new UTF8Encoding(false, true); _ = strict.GetByteCount(original);
        foreach (var edit in edits)
        {
            if (edit is null || edit.OldText is null || edit.NewText is null) throw new ArgumentException("Invalid edit text.", nameof(edits));
            _ = strict.GetByteCount(edit.OldText); _ = strict.GetByteCount(edit.NewText);
            characters += (long)edit.OldText.Length + edit.NewText.Length;
            if (characters > options.MaximumCharacters * 3L) throw Limit();
            normalized.Add(new(EditMatcher.NormalizeToLF(edit.OldText), EditMatcher.NormalizeToLF(edit.NewText)));
        }
        for (var index = 0; index < normalized.Count; index++)
            if (normalized[index].OldText.Length == 0) throw Failure(EditPlanFailure.EmptyOldText, normalized.Count == 1
                ? $"oldText must not be empty in {displayPath}." : $"edits[{index}].oldText must not be empty in {displayPath}.");
        var bom = original.StartsWith('\ufeff'); var withoutBom = bom ? original[1..] : original;
        var ending = EditMatcher.DetectLineEnding(withoutBom); var before = EditMatcher.NormalizeToLF(withoutBom);
        var fuzzy = normalized.Any(edit => EditMatcher.Find(before, edit.OldText).UsedFuzzyMatch);
        var basis = fuzzy ? EditMatcher.NormalizeForFuzzyMatch(before) : before;
        var replacements = ImmutableArray.CreateBuilder<EditReplacement>(normalized.Count);
        for (var index = 0; index < normalized.Count; index++)
        {
            var edit = normalized[index]; var match = EditMatcher.Find(basis, edit.OldText);
            if (!match.Found) throw Failure(EditPlanFailure.NotFound, normalized.Count == 1
                ? $"Could not find the exact text in {displayPath}. The old text must match exactly including all whitespace and newlines."
                : $"Could not find edits[{index}] in {displayPath}. The oldText must match exactly including all whitespace and newlines.");
            var count = EditMatcher.CountOccurrences(basis, edit.OldText);
            if (count > 1) throw Failure(EditPlanFailure.Ambiguous, normalized.Count == 1
                ? $"Found {count} occurrences of the text in {displayPath}. The text must be unique. Please provide more context to make it unique."
                : $"Found {count} occurrences of edits[{index}] in {displayPath}. Each oldText must be unique. Please provide more context to make it unique.");
            replacements.Add(new(index, match.Index, match.MatchLength, edit.NewText));
        }
        var ordered = replacements.OrderBy(value => value.MatchIndex).ToImmutableArray();
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index - 1].MatchIndex + ordered[index - 1].MatchLength > ordered[index].MatchIndex)
                throw Failure(EditPlanFailure.Overlap, $"edits[{ordered[index - 1].EditIndex}] and edits[{ordered[index].EditIndex}] overlap in {displayPath}. Merge them into one edit or target disjoint regions.");
        var preserved = fuzzy ? ApplyFuzzyOriginal(withoutBom, before, basis, ordered, normalized.ToImmutable(), ending) : ApplyOriginal(withoutBom, ordered, ending);
        var after = EditMatcher.NormalizeToLF(preserved);
        if (before == after) throw Failure(EditPlanFailure.NoChange, normalized.Count == 1
            ? $"No changes made to {displayPath}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
            : $"No changes made to {displayPath}. The replacements produced identical content.");
        if (after.Length > options.MaximumCharacters) throw Limit();
        var content = (bom ? "\ufeff" : "") + preserved;
        if (content.Length > options.MaximumCharacters) throw Limit();
        return new(before, after, content, fuzzy, ending, bom, ordered);
    }

    internal static string[] SplitLines(string text)
    {
        var lines = new List<string>(); var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start); var end = newline < 0 ? text.Length : newline + 1;
            lines.Add(text[start..end]); start = end;
        }
        return lines.ToArray();
    }
    private static string Apply(string content, IEnumerable<EditReplacement> replacements, int baseOffset)
    {
        var result = new StringBuilder(); var copied = 0;
        foreach (var replacement in replacements)
        {
            var start = replacement.MatchIndex - baseOffset;
            result.Append(content.AsSpan(copied, start - copied)); result.Append(replacement.NewText); copied = start + replacement.MatchLength;
        }
        result.Append(content.AsSpan(copied)); return result.ToString();
    }
    private static string ApplyFuzzyOriginal(string original, string before, string basis, ImmutableArray<EditReplacement> edits, ImmutableArray<TextEdit> requested, string ending)
    {
        // Map each normalized text element back to its source span. Characters outside a matched span, including
        // trailing whitespace, compatibility characters and mixed line terminators, are copied verbatim.
        var starts = new List<int>(); var ends = new List<int>(); var normalized = new StringBuilder();
        var offset = 0;
        foreach (var line in SplitLines(before))
        {
            var hasNewline = line.EndsWith('\n');
            var body = hasNewline ? line[..^1] : line;
            var expected = EditMatcher.NormalizeForFuzzyMatch(body);
            var lineText = new StringBuilder(); var lineStarts = new List<int>(); var lineEnds = new List<int>();
            var elements = StringInfo.GetTextElementEnumerator(body);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var mapped = EditMatcher.NormalizeForFuzzyMatch(element + "x")[..^1];
                lineText.Append(mapped);
                for (var i = 0; i < mapped.Length; i++)
                {
                    lineStarts.Add(offset + elements.ElementIndex);
                    lineEnds.Add(offset + elements.ElementIndex + element.Length);
                }
            }
            if (!lineText.ToString().StartsWith(expected, StringComparison.Ordinal))
                throw Failure(EditPlanFailure.InvalidRange, "Cannot map normalized edit to original text safely.");
            normalized.Append(expected);
            starts.AddRange(lineStarts.Take(expected.Length)); ends.AddRange(lineEnds.Take(expected.Length));
            if (hasNewline) { normalized.Append('\n'); starts.Add(offset + body.Length); ends.Add(offset + line.Length); }
            offset += line.Length;
        }
        if (normalized.ToString() != basis) throw Failure(EditPlanFailure.InvalidRange, "Cannot map normalized edit to original text safely.");
        var mappedEdits = edits.Select(edit =>
        {
            var exact = before.IndexOf(requested[edit.EditIndex].OldText, StringComparison.Ordinal);
            if (exact >= 0) return edit with { MatchIndex = exact, MatchLength = requested[edit.EditIndex].OldText.Length };
            var start = edit.MatchIndex < starts.Count ? starts[edit.MatchIndex] : before.Length;
            var end = edit.MatchLength == 0 ? start : ends[edit.MatchIndex + edit.MatchLength - 1];
            return edit with { MatchIndex = start, MatchLength = end - start };
        }).OrderBy(edit => edit.MatchIndex).ToImmutableArray();
        for (var i = 1; i < mappedEdits.Length; i++)
            if (mappedEdits[i - 1].MatchIndex + mappedEdits[i - 1].MatchLength > mappedEdits[i].MatchIndex)
                throw Failure(EditPlanFailure.Overlap, "Edits overlap in their original Unicode text elements.");
        return ApplyOriginal(original, mappedEdits, ending);
    }
    private static string ApplyOriginal(string original, ImmutableArray<EditReplacement> replacements, string ending)
    {
        // Every normalized boundary maps back to the same original character boundary, including CRLF pairs.
        var offsets = new List<int> { 0 };
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n') i++;
            offsets.Add(i + 1);
        }
        return Apply(original, replacements.Select(edit => edit with
        {
            MatchIndex = offsets[edit.MatchIndex],
            MatchLength = offsets[edit.MatchIndex + edit.MatchLength] - offsets[edit.MatchIndex],
            NewText = EditMatcher.RestoreLineEndings(edit.NewText, ending)
        }), 0);
    }
    private static EditPlanException Failure(EditPlanFailure kind, string message) => new(kind, message);
    internal static EditPlanException Limit() => Failure(EditPlanFailure.ResourceLimit, "Edit plan exceeds a configured logical limit.");
}
