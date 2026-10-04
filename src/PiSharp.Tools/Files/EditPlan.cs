using System.Collections.Immutable;
using System.Text;

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
        var after = fuzzy ? PreserveUnchangedLines(before, basis, ordered) : Apply(basis, ordered, 0);
        if (before == after) throw Failure(EditPlanFailure.NoChange, normalized.Count == 1
            ? $"No changes made to {displayPath}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
            : $"No changes made to {displayPath}. The replacements produced identical content.");
        if (after.Length > options.MaximumCharacters) throw Limit();
        var content = (bom ? "\ufeff" : "") + EditMatcher.RestoreLineEndings(after, ending);
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
    private sealed class Group(int start, int end)
    { public int Start = start; public int End = end; public List<EditReplacement> Edits = []; }
    private static string PreserveUnchangedLines(string original, string basis, ImmutableArray<EditReplacement> replacements)
    {
        var originals = SplitLines(original); var lines = SplitLines(basis);
        if (originals.Length != lines.Length) throw Failure(EditPlanFailure.InvalidRange, "Cannot preserve unchanged lines because the base content has a different line count.");
        var offsets = new int[lines.Length + 1]; for (var index = 0; index < lines.Length; index++) offsets[index + 1] = offsets[index] + lines[index].Length;
        var groups = new List<Group>();
        foreach (var replacement in replacements)
        {
            var start = 0;
            while (start < lines.Length && !(replacement.MatchIndex >= offsets[start] && replacement.MatchIndex < offsets[start + 1])) start++;
            var end = start;
            while (end < lines.Length && offsets[end + 1] < replacement.MatchIndex + replacement.MatchLength) end++;
            if (start >= lines.Length || end >= lines.Length) throw Failure(EditPlanFailure.InvalidRange, "Replacement range is outside the base content.");
            var group = groups.LastOrDefault();
            if (group is null || start >= group.End) { group = new(start, end + 1); groups.Add(group); }
            else group.End = Math.Max(group.End, end + 1);
            group.Edits.Add(replacement);
        }
        var result = new StringBuilder(); var copied = 0;
        foreach (var group in groups)
        {
            for (; copied < group.Start; copied++) result.Append(originals[copied]);
            result.Append(Apply(basis[offsets[group.Start]..offsets[group.End]], group.Edits, offsets[group.Start])); copied = group.End;
        }
        for (; copied < originals.Length; copied++) result.Append(originals[copied]); return result.ToString();
    }
    private static EditPlanException Failure(EditPlanFailure kind, string message) => new(kind, message);
    internal static EditPlanException Limit() => Failure(EditPlanFailure.ResourceLimit, "Edit plan exceeds a configured logical limit.");
}
