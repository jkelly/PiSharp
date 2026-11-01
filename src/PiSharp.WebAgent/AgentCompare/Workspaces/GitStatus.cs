// Decision 0011 (run W4, unit R4): the project's uncommitted changes, from `git status --porcelain=v1 -z --untracked-files=all` (stable
// across git versions and locales; -z keeps paths unquoted). A rename or copy entry is followed by its original path. Each entry becomes
// one UncommittedChange with a plain status word, which setup lists as "not copied" and Apply checks against the files it would change.
using System.Text;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Reads <c>git status</c> output.</summary>
internal static class GitStatus
{
    /// <summary>The arguments for the status command this parser reads.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none"];

    /// <summary>The changes in <paramref name="output"/>, in git's order.</summary>
    public static List<UncommittedChange> Parse(byte[] output)
    {
        var entries = Encoding.UTF8.GetString(output).Split('\0');
        var changes = new List<UncommittedChange>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4 || entry[2] != ' ') continue;
            char x = entry[0], y = entry[1];
            var path = entry[3..];
            string? original = null;
            if (x is 'R' or 'C' || y is 'R' or 'C')
                original = index + 1 < entries.Length ? entries[++index] : null;
            changes.Add(new UncommittedChange(path, Word(x, y), original));
        }
        return changes;
    }

    /// <summary>Every path an entry touches (both sides of a rename).</summary>
    public static IEnumerable<string> Paths(UncommittedChange change) =>
        change.OriginalPath is { } original ? [change.Path, original] : [change.Path];

    private static string Word(char x, char y) => (x, y) switch
    {
        ('?', '?') => "untracked",
        ('U', _) or (_, 'U') or ('D', 'D') or ('A', 'A') => "unmerged",
        ('R', _) or (_, 'R') => "renamed",
        ('C', _) or (_, 'C') => "copied",
        ('A', _) => "added",
        ('D', _) or (_, 'D') => "deleted",
        ('T', _) or (_, 'T') => "type changed",
        _ => "modified",
    };
}
