// Decision 0011 (run W5, E0): tags. An Arena vote is saved under one tag (mockup compare _ArenaVoteBar: coding, writing; agent mode's default
// is agent), and the leaderboard shows one tag at a time (evals-leaderboard _LeaderboardToolbar). Request and agent votes are kept apart by
// their ArenaMode, so "agent" in request mode and "agent" in agent mode are two leaderboards. A test case has a few tags too ("coding,
// regex", modal-test-case). A tag is lower case, 1 to 40 characters: letters, digits, spaces and # + . _ - (so c#, c++ and node.js work),
// with runs of spaces folded to one.
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.Evals;

/// <summary>Tag rules for votes and test cases.</summary>
public static partial class EvalTags
{
    /// <summary>The longest tag.</summary>
    public const int MaxLength = 40;

    /// <summary>The most tags a test case has.</summary>
    public const int MaxPerCase = 10;

    /// <summary>The default tag of an agent-mode vote (mockup <c>agent-blind</c>).</summary>
    public const string Agent = "agent";

    /// <summary><paramref name="tag"/> as stored: trimmed, lower case (invariant), runs of white space folded to one space.</summary>
    /// <exception cref="ArgumentException">It is empty, too long, or has a character a tag may not have.</exception>
    public static string Normalize(string? tag)
    {
        var folded = Spaces().Replace(tag?.Trim() ?? "", " ").ToLowerInvariant().Normalize(NormalizationForm.FormC);
        if (folded.Length == 0) throw new ArgumentException("Give the tag a name.");
        if (folded.Length > MaxLength) throw new ArgumentException($"A tag is at most {MaxLength} characters.");
        if (!Pattern().IsMatch(folded)) throw new ArgumentException($"\"{tag}\" is not a tag: use letters, digits, spaces and # + . _ -.");
        return folded;
    }

    /// <summary>Whether <paramref name="tag"/> is a valid tag (after <see cref="Normalize"/>).</summary>
    public static bool IsValid(string? tag)
    {
        try
        {
            Normalize(tag);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The tags of a comma-separated list such as <c>"coding, regex"</c>, each normalized, without repeats, in order.</summary>
    /// <exception cref="ArgumentException">A tag is not valid, or there are more than <see cref="MaxPerCase"/>.</exception>
    public static IReadOnlyList<string> ParseList(string? text) =>
        NormalizeList((text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    /// <summary><paramref name="tags"/> normalized, without repeats or empty entries, in order.</summary>
    /// <exception cref="ArgumentException">A tag is not valid, or there are more than <see cref="MaxPerCase"/>.</exception>
    public static IReadOnlyList<string> NormalizeList(IEnumerable<string?>? tags)
    {
        var result = new List<string>();
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var normalized = Normalize(tag);
            if (!result.Contains(normalized, StringComparer.Ordinal)) result.Add(normalized);
        }
        if (result.Count > MaxPerCase) throw new ArgumentException($"A test case has at most {MaxPerCase} tags.");
        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\p{L}\p{N}#+][\p{L}\p{N}\p{M} #+._-]*$")]
    private static partial Regex Pattern();
}
