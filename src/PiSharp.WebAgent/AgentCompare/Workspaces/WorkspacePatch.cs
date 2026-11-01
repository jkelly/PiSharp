// Decision 0011 (run W4, fix 2 workspaces): a workspace's patch, kept with its run after the workspace is gone. Discard (and the cleanup
// after 7 days) removes the workspace and with it the diff WorkspaceManager.DiffAsync reads from it, so the runner keeps the slot's patch
// with the run (AgentCompareStore) when the model finishes and again just before a discard; the diff modal then reads it back here. A kept
// patch is cut at whole files (Cut), so what is kept always parses and every kept file's lines are complete; a cut patch says how many
// files it left out.
using System.Text;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Cuts a patch to a size for keeping and reads a kept one back as a <see cref="WorkspaceDiff"/>.</summary>
public static class WorkspacePatch
{
    private const string FileHeader = "diff --git ";

    /// <summary>The first whole files of <paramref name="patch"/> that fit in <paramref name="maxBytes"/> UTF-8 bytes (every file when the
    /// patch fits). A file's section runs from its <c>diff --git</c> line to the next one; a hunk line never starts with that text, and
    /// a binary patch's base85 lines hold no space, so the cut never falls inside a file.</summary>
    /// <param name="patch">A patch from <see cref="WorkspaceDiff.Patch"/>.</param>
    /// <param name="maxBytes">The most UTF-8 bytes to keep (at least 0).</param>
    /// <returns>The kept text, how many files the patch has, and how many of them were kept.</returns>
    public static (string Patch, int Files, int KeptFiles) Cut(string patch, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var sections = Sections(patch);
        if (Encoding.UTF8.GetByteCount(patch) <= maxBytes) return (patch, sections.Count, sections.Count);
        var kept = new StringBuilder();
        var bytes = 0;
        var count = 0;
        foreach (var section in sections)
        {
            var size = Encoding.UTF8.GetByteCount(section);
            if (bytes + size > maxBytes) break;
            kept.Append(section);
            bytes += size;
            count++;
        }
        return (kept.ToString(), sections.Count, count);
    }

    /// <summary>A kept patch read back as the diff modal shows it: its files, hunks and lines, with when it was kept and how many files it
    /// left out.</summary>
    /// <param name="run">The run id.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="kind">Worktree or copy.</param>
    /// <param name="baseLabel">What the diff was against ("main @ 4f9c2e1", "the base copy").</param>
    /// <param name="patch">The kept patch.</param>
    /// <param name="storedAt">When it was kept.</param>
    /// <param name="omittedFiles">How many changed files it has no room for.</param>
    public static WorkspaceDiff Read(string run, string slot, WorkspaceKind kind, string baseLabel, string patch, DateTimeOffset storedAt, int omittedFiles = 0)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return new WorkspaceDiff
        {
            Run = run,
            Slot = slot,
            Kind = kind,
            BaseLabel = baseLabel,
            Files = UnifiedDiff.Parse(Encoding.UTF8.GetBytes(patch)),
            Patch = patch,
            StoredAt = storedAt,
            OmittedFiles = Math.Max(0, omittedFiles),
        };
    }

    // The patch split before each "diff --git " that starts a line; any text before the first file belongs to the first section.
    private static List<string> Sections(string patch)
    {
        var sections = new List<string>();
        var start = 0;
        var at = patch.StartsWith(FileHeader, StringComparison.Ordinal) ? 0 : NextHeader(patch, 0);
        if (at < 0) return patch.Length == 0 ? sections : [patch];
        while (at >= 0)
        {
            var next = NextHeader(patch, at + 1);
            var end = next < 0 ? patch.Length : next;
            sections.Add(patch[(sections.Count == 0 ? start : at)..end]);
            at = next;
        }
        return sections;
    }

    // The index of the next "diff --git " at the start of a line, at or after from; -1 when there is none.
    private static int NextHeader(string patch, int from)
    {
        var index = patch.IndexOf("\n" + FileHeader, Math.Max(0, from - 1), StringComparison.Ordinal);
        return index < 0 ? -1 : index + 1;
    }
}
