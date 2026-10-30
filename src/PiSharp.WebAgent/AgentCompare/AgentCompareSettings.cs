// Decision 0011 (run W4, R5): agent compare's defaults (section "agentCompare" in webagent.db; mockup settings-tools, _AgentCompareDefaults,
// which R6 renders and saves). A new agent compare run starts from these and may change any of them for itself: how workspaces are made
// (automatic: a git worktree for a git repo, else a temporary copy; or always a copy), the extra folders a copy leaves out (on top of
// WorkspaceManagerOptions.DefaultExcludes), whether a copy honours .gitignore, the size above which a copy needs a confirmation, the turn
// and time limits each model gets, the run's tool approval mode (which overrides Settings › Tools for the run's sessions only) and how long
// the check command may run. Workspaces are copies, not sandboxes: the approval mode decides what runs without asking.
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.AgentCompare;

/// <summary>Agent compare's defaults (section <c>agentCompare</c>, Settings › Tools).</summary>
/// <param name="Isolation">How workspaces are made: <c>automatic</c> (a git worktree for a git repo with a commit, otherwise a temporary
/// copy; the default) or <c>copy</c> (always a temporary copy). <c>worktree</c> is accepted too (refused for a folder outside git).</param>
/// <param name="ExtraExcludes">Folders or patterns a copy leaves out on top of <see cref="WorkspaceManagerOptions.DefaultExcludes"/>
/// (bin, obj, node_modules, .vs, .git); default none.</param>
/// <param name="UseGitignore">Whether a copy also leaves out what .gitignore lists (default on). The workspace manager always honours
/// .gitignore today, so turning it off is stored but not applied yet.</param>
/// <param name="CopyWarnGb">A copy larger than this many GB needs a confirmation (0.1 to 1,000; default 1).</param>
/// <param name="MaxTurns">The most model requests (turns) each model may make in a run (1 to 200; default 40).</param>
/// <param name="MaxMinutes">The most minutes each model may run, from when its task was sent (1 to 240; default 20).</param>
/// <param name="Approval">The run's tool approval mode for its workspaces: <c>ask</c> (bash asks; reads and edits inside the workspace
/// don't; the default), <c>allowList</c> (as ask, but commands on Settings › Tools' allow-list run) or <c>auto</c> (nothing asks).</param>
/// <param name="CheckTimeoutMinutes">How long the check command may run before it is stopped (1 to 240; default 10).</param>
public sealed record AgentCompareSettings(
    WorkspaceIsolation Isolation = WorkspaceIsolation.Automatic,
    IReadOnlyList<string>? ExtraExcludes = null,
    bool UseGitignore = true,
    double CopyWarnGb = AgentCompareSettings.DefaultCopyWarnGb,
    int MaxTurns = AgentCompareSettings.DefaultMaxTurns,
    int MaxMinutes = AgentCompareSettings.DefaultMaxMinutes,
    ToolApprovalMode Approval = ToolApprovalMode.Ask,
    int CheckTimeoutMinutes = AgentCompareSettings.DefaultCheckTimeoutMinutes) : ISettingsSection<AgentCompareSettings>
{
    /// <summary>The default copy size that needs a confirmation: 1 GB.</summary>
    public const double DefaultCopyWarnGb = 1;

    /// <summary>The default turn limit per model: 40.</summary>
    public const int DefaultMaxTurns = 40;

    /// <summary>The default time limit per model: 20 minutes.</summary>
    public const int DefaultMaxMinutes = 20;

    /// <summary>The default check command timeout: 10 minutes.</summary>
    public const int DefaultCheckTimeoutMinutes = 10;

    /// <summary>The most turns a model may be given.</summary>
    public const int MaxTurnsLimit = 200;

    /// <summary>The most minutes a model or a check may be given.</summary>
    public const int MaxMinutesLimit = 240;

    /// <summary>The most extra excludes.</summary>
    public const int MaxExcludes = 100;

    /// <summary>Bytes in a GB (2^30, as the workspace manager counts).</summary>
    public const long BytesPerGb = 1L << 30;

    /// <inheritdoc/>
    public static string SectionName => "agentCompare";

    /// <inheritdoc/>
    public static AgentCompareSettings CreateDefault() => new();

    /// <summary>The copy size that needs a confirmation, in bytes.</summary>
    public long CopyWarnBytes => (long)Math.Round(CopyWarnGb * BytesPerGb);

    /// <inheritdoc/>
    public AgentCompareSettings Normalize()
    {
        if (!Enum.IsDefined(Isolation)) throw new ArgumentException($"Unknown isolation {(int)Isolation}.");
        if (!Enum.IsDefined(Approval)) throw new ArgumentException($"Unknown approval mode {(int)Approval}.");
        if (!double.IsFinite(CopyWarnGb) || CopyWarnGb < 0.1 || CopyWarnGb > 1000) throw new ArgumentException("Warn above 0.1 to 1,000 GB.");
        if (MaxTurns is < 1 or > MaxTurnsLimit) throw new ArgumentException($"Give each model 1 to {MaxTurnsLimit} turns.");
        if (MaxMinutes is < 1 or > MaxMinutesLimit) throw new ArgumentException($"Give each model 1 to {MaxMinutesLimit} minutes.");
        if (CheckTimeoutMinutes is < 1 or > MaxMinutesLimit) throw new ArgumentException($"Give the check command 1 to {MaxMinutesLimit} minutes.");
        return this with { ExtraExcludes = NormalizeExcludes(ExtraExcludes) };
    }

    /// <summary>Extra excludes trimmed, without blanks, duplicates or the defaults, in their order.</summary>
    /// <exception cref="ArgumentException">More than <see cref="MaxExcludes"/>, or one with a character a path pattern can't have.</exception>
    public static IReadOnlyList<string> NormalizeExcludes(IEnumerable<string>? excludes)
    {
        var result = new List<string>();
        foreach (var raw in excludes ?? [])
        {
            var exclude = raw?.Trim() ?? "";
            if (exclude.Length == 0) continue;
            if (exclude.Length > 200 || exclude.Any(c => char.IsControl(c) || c is '<' or '>' or '|' or '"'))
                throw new ArgumentException($"\"{exclude}\" is not a folder or pattern to leave out.");
            if (WorkspaceManagerOptions.DefaultExcludes.Contains(exclude, StringComparer.OrdinalIgnoreCase)) continue;
            if (!result.Contains(exclude, StringComparer.OrdinalIgnoreCase)) result.Add(exclude);
        }
        if (result.Count > MaxExcludes) throw new ArgumentException($"Leave out at most {MaxExcludes} extra folders or patterns.");
        return result;
    }
}
