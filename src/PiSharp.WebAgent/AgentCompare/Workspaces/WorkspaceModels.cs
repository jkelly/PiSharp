// Decision 0011 (run W4, unit R4): the shapes the workspace manager answers with. Agent compare gives each model its own workspace: a
// detached git worktree of the project's base commit, or (for other folders, or when the user asks) a pristine base copy plus one working
// copy per model. These records are what the runner (R5), the agent compare views (R6) and Settings › Data (R9) read; nothing here is a
// sandbox, and nothing here commits to the user's branches or pushes. Paths in diffs and change lists are relative, with '/' separators.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>How a run's workspaces are made (the agent compare isolation setting; wire names <c>automatic</c>, <c>worktree</c>,
/// <c>copy</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceIsolation>))]
public enum WorkspaceIsolation
{
    /// <summary>A git worktree for a git repo with at least one commit, otherwise a temporary copy (the default).</summary>
    [JsonStringEnumMemberName("automatic")] Automatic,

    /// <summary>Always a detached git worktree; refused for a folder that is not in a git repo.</summary>
    [JsonStringEnumMemberName("worktree")] Worktree,

    /// <summary>Always a temporary copy, uncommitted changes included.</summary>
    [JsonStringEnumMemberName("copy")] Copy,
}

/// <summary>What a run's workspaces are.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceKind>))]
public enum WorkspaceKind
{
    /// <summary>One detached <c>git worktree</c> per model, at the base commit.</summary>
    [JsonStringEnumMemberName("worktree")] Worktree,

    /// <summary>A pristine base copy plus one working copy per model.</summary>
    [JsonStringEnumMemberName("copy")] Copy,
}

/// <summary>Where one model's workspace stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceSlotState>))]
public enum WorkspaceSlotState
{
    /// <summary>On disk and usable.</summary>
    [JsonStringEnumMemberName("ready")] Ready,

    /// <summary>Removed (Discard, or the run's other workspaces were discarded on Keep).</summary>
    [JsonStringEnumMemberName("discarded")] Discarded,
}

/// <summary>One uncommitted change in the project (from <c>git status</c>).</summary>
/// <param name="Path">Relative to the repository root.</param>
/// <param name="Status">One of <c>modified</c>, <c>added</c>, <c>deleted</c>, <c>renamed</c>, <c>copied</c>, <c>type changed</c>,
/// <c>unmerged</c> or <c>untracked</c>.</param>
/// <param name="OriginalPath">For <c>renamed</c> and <c>copied</c>: the path it came from.</param>
public sealed record UncommittedChange(string Path, string Status, string? OriginalPath = null);

/// <summary>What <see cref="WorkspaceManager.InspectAsync"/> looks at.</summary>
public sealed record WorkspaceInspectOptions
{
    /// <summary>The isolation the run would use.</summary>
    public WorkspaceIsolation Isolation { get; init; } = WorkspaceIsolation.Automatic;

    /// <summary>Extra gitignore-style patterns a copy skips, on top of <see cref="WorkspaceManagerOptions.DefaultExcludes"/>.</summary>
    public IReadOnlyList<string> ExtraExcludes { get; init; } = [];

    /// <summary>How many models (workspaces) the run would have, for the disk estimates.</summary>
    public int Slots { get; init; } = 3;

    /// <summary>The copy size that needs the user's go-ahead; default <see cref="WorkspaceManagerOptions.WarnAboveBytes"/>.</summary>
    public long? WarnAboveBytes { get; init; }

    /// <summary>Walk the folder for a copy's size even when the run would use worktrees (the workspace modal shows both panes).</summary>
    public bool EstimateCopy { get; init; } = true;
}

/// <summary>A project folder as agent compare's setup shows it: the isolation chip, the "not copied" list, the base branch and commit, and
/// the disk estimates. Every number is measured (git's tree sizes, or a walk of the folder).</summary>
public sealed record ProjectInspection
{
    /// <summary>The project folder, a full path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Whether git could be run.</summary>
    public required bool GitAvailable { get; init; }

    /// <summary>Whether the folder is inside a git work tree with at least one commit (so worktrees can be used).</summary>
    public required bool IsGitRepo { get; init; }

    /// <summary>The repository's top-level folder, when <see cref="IsGitRepo"/>.</summary>
    public string? RepoRoot { get; init; }

    /// <summary>The project folder relative to <see cref="RepoRoot"/> ('/' separators, empty for the root itself).</summary>
    public string ProjectSubpath { get; init; } = "";

    /// <summary>What the run would use for the isolation asked for.</summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>Why worktrees cannot be used (not a git repo, no commits yet, git missing); null when they can.</summary>
    public string? WorktreeUnavailable { get; init; }

    /// <summary>The project's current branch; null when HEAD is detached or not a repo.</summary>
    public string? CurrentBranch { get; init; }

    /// <summary>The project's HEAD commit (full id): the default base commit.</summary>
    public string? HeadCommit { get; init; }

    /// <summary><see cref="HeadCommit"/> abbreviated the way git abbreviates it.</summary>
    public string? ShortCommit { get; init; }

    /// <summary>Local branches (at most 200), for the base branch picker.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>The repository's uncommitted changes. A worktree does not copy them; a copy includes them.</summary>
    public IReadOnlyList<UncommittedChange> Uncommitted { get; init; } = [];

    /// <summary>The size of the base commit's files, one worktree's checkout (before any build).</summary>
    public long? CheckoutBytes { get; init; }

    /// <summary>What one copy would hold: the folder without the excluded and ignored files.</summary>
    public long? CopyBytes { get; init; }

    /// <summary>How many files one copy would hold.</summary>
    public int? CopyFiles { get; init; }

    /// <summary>False when the walk stopped early (a very large folder), so <see cref="CopyBytes"/> is a lower bound.</summary>
    public bool CopyEstimateComplete { get; init; } = true;

    /// <summary>Symbolic links and junctions a copy leaves out (relative paths).</summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];

    /// <summary>Folders a copy leaves out because they can't be read (relative paths).</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

    /// <summary>What a copy skips: <see cref="WorkspaceManagerOptions.DefaultExcludes"/>, then the extra patterns ("anything in
    /// .gitignore" is always skipped too).</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>The size above which a copy needs the user's go-ahead.</summary>
    public required long WarnAboveBytes { get; init; }

    /// <summary>Whether one copy is larger than <see cref="WarnAboveBytes"/> (a copy run then needs
    /// <see cref="WorkspaceRequest.ConfirmLargeCopy"/>).</summary>
    public bool OverWarnLimit => CopyBytes is { } bytes && bytes > WarnAboveBytes;

    /// <summary>Where workspaces live (<c>&lt;data&gt;\workspaces</c>).</summary>
    public required string WorkspacesRoot { get; init; }

    /// <summary>The isolation chip: "git worktree from main @ 4f9c2e1" or "temporary copy · 84 MB".</summary>
    public string Label => Kind == WorkspaceKind.Worktree
        ? "git worktree from " + WorkspaceText.BaseLabel(CurrentBranch, ShortCommit)
        : "temporary copy" + (CopyBytes is { } bytes ? " · " + WorkspaceText.Size(bytes) : "");
}

/// <summary>One model's workspace asked for in <see cref="WorkspaceRequest"/>.</summary>
/// <param name="Slot">Its column letter or name: lowercase letters and digits (also <c>-</c>, <c>_</c>), at most 32, not <c>base</c>.</param>
/// <param name="Label">What it is for, usually <c>provider/model</c>; shown in Settings › Data and in the kept branch's message.</param>
public sealed record WorkspaceSlotRequest(string Slot, string? Label = null);

/// <summary>A run's workspaces to make (<see cref="WorkspaceManager.CreateAsync"/>).</summary>
public sealed record WorkspaceRequest
{
    /// <summary>The run id (letters, digits, <c>-</c> and <c>_</c>, at most 64), also the folder name.</summary>
    public required string Run { get; init; }

    /// <summary>The project folder, an absolute path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>One entry per model, 1 to <see cref="WorkspaceManagerOptions.MaxSlots"/>.</summary>
    public required IReadOnlyList<WorkspaceSlotRequest> Slots { get; init; }

    /// <summary>How to make them.</summary>
    public WorkspaceIsolation Isolation { get; init; } = WorkspaceIsolation.Automatic;

    /// <summary>For worktrees: a branch, tag or commit to start from; default the project's HEAD.</summary>
    public string? BaseRef { get; init; }

    /// <summary>Extra gitignore-style patterns a copy (and a worktree's untracked files) skips.</summary>
    public IReadOnlyList<string> ExtraExcludes { get; init; } = [];

    /// <summary>The copy size that needs <see cref="ConfirmLargeCopy"/>; default <see cref="WorkspaceManagerOptions.WarnAboveBytes"/>.</summary>
    public long? WarnAboveBytes { get; init; }

    /// <summary>The user said yes to a copy above <see cref="WarnAboveBytes"/>.</summary>
    public bool ConfirmLargeCopy { get; init; }
}

/// <summary>One model's workspace.</summary>
public sealed record WorkspaceSlot
{
    /// <summary>The slot name.</summary>
    public required string Slot { get; init; }

    /// <summary>What it is for (usually <c>provider/model</c>).</summary>
    public string? Label { get; init; }

    /// <summary>The workspace folder: <c>&lt;root&gt;\&lt;run&gt;\&lt;slot&gt;</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Where the agent works: the workspace, or for a worktree of a sub-folder project the same sub-folder inside it. This is the
    /// durable session's <c>Cwd</c> and where the check command runs.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Whether it is still on disk.</summary>
    public required WorkspaceSlotState State { get; init; }

    /// <summary>When its changes were applied to the project, if they were.</summary>
    public DateTimeOffset? AppliedAt { get; init; }

    /// <summary>The kept branch (<c>pisharp/compare/&lt;run&gt;-&lt;slot&gt;</c>), if Keep the branch was chosen.</summary>
    public string? Branch { get; init; }

    /// <summary>The kept branch's commit.</summary>
    public string? BranchCommit { get; init; }

    /// <summary>Disk use, when measured (<see cref="WorkspaceManager.GetAsync"/> or <see cref="WorkspaceManager.ListAsync"/> with
    /// <c>measure</c>).</summary>
    public long? Bytes { get; init; }
}

/// <summary>A run's workspaces, from its manifest.</summary>
public sealed record WorkspaceRun
{
    /// <summary>The run id.</summary>
    public required string Run { get; init; }

    /// <summary>Worktrees or copies.</summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>The project folder.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>The repository root (worktrees, or a copy of a folder in a git repo).</summary>
    public string? RepoRoot { get; init; }

    /// <summary>The project folder relative to <see cref="RepoRoot"/> ('/' separators).</summary>
    public string ProjectSubpath { get; init; } = "";

    /// <summary>The base commit (worktrees).</summary>
    public string? BaseCommit { get; init; }

    /// <summary>The base commit, abbreviated.</summary>
    public string? ShortCommit { get; init; }

    /// <summary>The branch the base commit was taken from, when the base was a branch (or the project's current branch).</summary>
    public string? BaseBranch { get; init; }

    /// <summary>"main @ 4f9c2e1" for worktrees, "the base copy" for copies: what diffs are against.</summary>
    public string BaseLabel => Kind == WorkspaceKind.Worktree ? WorkspaceText.BaseLabel(BaseBranch, ShortCommit) : "the base copy";

    /// <summary>The isolation chip: "git worktree from main @ 4f9c2e1" or "temporary copy · 84 MB".</summary>
    public string Label => Kind == WorkspaceKind.Worktree
        ? "git worktree from " + BaseLabel
        : "temporary copy" + (CopyBytes is { } bytes ? " · " + WorkspaceText.Size(bytes) : "");

    /// <summary>When the workspaces were made.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the cleanup removes them (<see cref="CreatedAt"/> plus <see cref="WorkspaceManagerOptions.Retention"/>).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The run folder: <c>&lt;root&gt;\&lt;run&gt;</c>.</summary>
    public required string Directory { get; init; }

    /// <summary>What the copies skip (and a worktree's untracked files).</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>The project's uncommitted changes the worktrees do not have ("Not copied"); empty for copies, which include them.</summary>
    public IReadOnlyList<UncommittedChange> NotCopied { get; init; } = [];

    /// <summary>Symbolic links and junctions the copies left out.</summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];

    /// <summary>Folders the copies left out because they can't be read.</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

    /// <summary>One copy's size (copies).</summary>
    public long? CopyBytes { get; init; }

    /// <summary>The base copy's disk use, when measured (copies).</summary>
    public long? BaseBytes { get; init; }

    /// <summary>One entry per model, in the order asked for.</summary>
    public required IReadOnlyList<WorkspaceSlot> Slots { get; init; }

    /// <summary>The slot named <paramref name="slot"/>, or null.</summary>
    public WorkspaceSlot? Find(string slot) => Slots.FirstOrDefault(entry => string.Equals(entry.Slot, slot, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How a file changed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceFileChange>))]
public enum WorkspaceFileChange
{
    /// <summary>A new file.</summary>
    [JsonStringEnumMemberName("added")] Added,

    /// <summary>A removed file.</summary>
    [JsonStringEnumMemberName("deleted")] Deleted,

    /// <summary>A changed file (content or mode).</summary>
    [JsonStringEnumMemberName("modified")] Modified,
}

/// <summary>What a diff line is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceDiffLineKind>))]
public enum WorkspaceDiffLineKind
{
    /// <summary>Unchanged context (<c> </c>).</summary>
    [JsonStringEnumMemberName("context")] Context,

    /// <summary>An added line (<c>+</c>).</summary>
    [JsonStringEnumMemberName("added")] Added,

    /// <summary>A removed line (<c>-</c>).</summary>
    [JsonStringEnumMemberName("removed")] Removed,

    /// <summary>A note such as "No newline at end of file" (<c>\</c>).</summary>
    [JsonStringEnumMemberName("note")] Note,
}

/// <summary>One line of a hunk, without its marker.</summary>
public sealed record WorkspaceDiffLine(WorkspaceDiffLineKind Kind, string Text);

/// <summary>One hunk: its <c>@@ … @@</c> header line and its lines.</summary>
public sealed record WorkspaceDiffHunk(string Header, IReadOnlyList<WorkspaceDiffLine> Lines);

/// <summary>One changed file of a <see cref="WorkspaceDiff"/>.</summary>
public sealed record WorkspaceFileDiff
{
    /// <summary>Relative to the workspace (the repository root for worktrees), '/' separators.</summary>
    public required string Path { get; init; }

    /// <summary>New, deleted or modified.</summary>
    public required WorkspaceFileChange Change { get; init; }

    /// <summary>Whether git treats it as binary (no lines are shown).</summary>
    public bool Binary { get; init; }

    /// <summary>Added lines.</summary>
    public int Added { get; init; }

    /// <summary>Removed lines.</summary>
    public int Removed { get; init; }

    /// <summary>The old file mode (a deleted file, or a mode change).</summary>
    public string? OldMode { get; init; }

    /// <summary>The new file mode (a new file, or a mode change).</summary>
    public string? NewMode { get; init; }

    /// <summary>The unified diff's hunks (empty for binary files and mode-only changes).</summary>
    public IReadOnlyList<WorkspaceDiffHunk> Hunks { get; init; } = [];
}

/// <summary>A workspace's changes against its base: <c>git diff</c> (untracked files included) for a worktree, <c>git diff
/// --no-index</c> against the base copy for a copy.</summary>
public sealed record WorkspaceDiff
{
    /// <summary>The run id.</summary>
    public required string Run { get; init; }

    /// <summary>The slot.</summary>
    public required string Slot { get; init; }

    /// <summary>Worktree or copy.</summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>What the diff is against ("main @ 4f9c2e1", "the base copy").</summary>
    public required string BaseLabel { get; init; }

    /// <summary>The changed files, in path order.</summary>
    public required IReadOnlyList<WorkspaceFileDiff> Files { get; init; }

    /// <summary>Added lines over all files.</summary>
    public int Added => Files.Sum(file => file.Added);

    /// <summary>Removed lines over all files.</summary>
    public int Removed => Files.Sum(file => file.Removed);

    /// <summary>The whole patch as text (binary files as git binary patches): keep it with the run before discarding the workspace.</summary>
    public required string Patch { get; init; }

    /// <summary>"3 files · +31 −9".</summary>
    public string Summary => WorkspaceText.Summary(Files.Count, Added, Removed);
}

/// <summary>What Keep would do for a slot (the Keep modal): the slot's files, the project's uncommitted changes in the same files (Apply
/// is refused when there are any) and the others (left alone).</summary>
public sealed record KeepPreview
{
    /// <summary>The run id.</summary>
    public required string Run { get; init; }

    /// <summary>The slot.</summary>
    public required string Slot { get; init; }

    /// <summary>Worktree or copy.</summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>The files the slot changed.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>The project's uncommitted changes in those files.</summary>
    public required IReadOnlyList<UncommittedChange> Conflicts { get; init; }

    /// <summary>The project's other uncommitted changes, which Apply leaves alone.</summary>
    public required IReadOnlyList<UncommittedChange> OtherUncommitted { get; init; }

    /// <summary>Whether Apply would go ahead (changes, and no conflicts).</summary>
    public bool CanApply => Files.Count > 0 && Conflicts.Count == 0;

    /// <summary>Whether Keep the branch is offered (worktrees only, with changes).</summary>
    public bool CanKeepBranch => Kind == WorkspaceKind.Worktree && Files.Count > 0;

    /// <summary>The branch Keep the branch would create (worktrees).</summary>
    public string? Branch { get; init; }
}

/// <summary>How a Keep ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeepOutcome>))]
public enum KeepOutcome
{
    /// <summary>The changes are uncommitted changes in the project now.</summary>
    [JsonStringEnumMemberName("applied")] Applied,

    /// <summary>The branch holds one commit with the changes; the user's branch was not switched.</summary>
    [JsonStringEnumMemberName("branchCreated")] BranchCreated,

    /// <summary>Nothing in the project or the repository was changed; <see cref="KeepResult.Refusal"/> says why.</summary>
    [JsonStringEnumMemberName("refused")] Refused,
}

/// <summary>The answer to <see cref="WorkspaceManager.ApplyAsync"/> or <see cref="WorkspaceManager.KeepBranchAsync"/>.</summary>
public sealed record KeepResult
{
    /// <summary>Codes for <see cref="Refusal"/>.</summary>
    public static class Refusals
    {
        /// <summary>The project has uncommitted changes in files the slot changed (<see cref="Conflicts"/>).</summary>
        public const string Conflict = "conflict";

        /// <summary>The patch does not apply to the project any more (its files moved on since the base).</summary>
        public const string DoesNotApply = "does-not-apply";

        /// <summary>The workspace has no changes.</summary>
        public const string NothingToKeep = "nothing-to-keep";

        /// <summary>Keep the branch needs a worktree.</summary>
        public const string NotGit = "not-git";

        /// <summary>The branch already exists with other content.</summary>
        public const string BranchExists = "branch-exists";
    }

    /// <summary>The run id.</summary>
    public required string Run { get; init; }

    /// <summary>The slot.</summary>
    public required string Slot { get; init; }

    /// <summary>What happened.</summary>
    public required KeepOutcome Outcome { get; init; }

    /// <summary>When refused: one of <see cref="Refusals"/>.</summary>
    public string? Refusal { get; init; }

    /// <summary>A sentence for the page.</summary>
    public required string Message { get; init; }

    /// <summary>The slot's changed files.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>For <see cref="Refusals.Conflict"/>: the project's uncommitted changes in the same files.</summary>
    public IReadOnlyList<UncommittedChange> Conflicts { get; init; } = [];

    /// <summary>The kept branch (<see cref="KeepOutcome.BranchCreated"/>, or the one a <see cref="Refusals.BranchExists"/> names).</summary>
    public string? Branch { get; init; }

    /// <summary>The kept branch's commit.</summary>
    public string? Commit { get; init; }

    /// <summary>The branch was already there with exactly these changes (a repeated Keep).</summary>
    public bool AlreadyExisted { get; init; }
}

/// <summary>One removal by <see cref="WorkspaceManager.CleanupAsync"/> or a discard.</summary>
/// <param name="Run">The run id (or the folder name of a leftover).</param>
/// <param name="Slot">The slot, or null for the whole run.</param>
/// <param name="Reason"><c>leftover</c> (an unfinished or unknown folder), <c>expired</c> (older than the retention),
/// <c>discarded</c> (a discarded workspace still on disk) or <c>removed</c> (asked for).</param>
public sealed record WorkspaceRemoval(string Run, string? Slot, string Reason);

/// <summary>A run a cleanup could not remove.</summary>
/// <param name="Run">The run id (or folder name).</param>
/// <param name="Error">Why, in a sentence.</param>
public sealed record WorkspaceFailure(string Run, string Error);

/// <summary>What a cleanup did.</summary>
public sealed record WorkspaceCleanupReport
{
    /// <summary>What was removed.</summary>
    public IReadOnlyList<WorkspaceRemoval> Removed { get; init; } = [];

    /// <summary>Runs that could not be removed (still in use, a locked file); retried at the next cleanup.</summary>
    public IReadOnlyList<WorkspaceFailure> Failed { get; init; } = [];

    /// <summary>Runs kept (ready, not expired, or active).</summary>
    public int Kept { get; init; }
}

/// <summary>A workspace operation that cannot go ahead. <see cref="Code"/> is one of the constants here.</summary>
public sealed class WorkspaceException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>A run id, slot, path or ref is not valid, or a path would leave its root.</summary>
    public const string Invalid = "invalid";

    /// <summary>The run or slot does not exist (or was discarded).</summary>
    public const string NotFound = "not-found";

    /// <summary>The project folder is missing or not a folder.</summary>
    public const string ProjectMissing = "project-missing";

    /// <summary>Worktrees were asked for, but the folder is not in a git repo with a commit.</summary>
    public const string NotGit = "not-git";

    /// <summary>A copy above the warning size, without <see cref="WorkspaceRequest.ConfirmLargeCopy"/>.</summary>
    public const string TooLarge = "too-large";

    /// <summary>The run exists with other workspaces.</summary>
    public const string Exists = "exists";

    /// <summary>Git is not installed (or not on PATH).</summary>
    public const string GitMissing = "git-missing";

    /// <summary>A git command failed.</summary>
    public const string GitFailed = "git-failed";

    /// <summary>A git command ran past its time limit.</summary>
    public const string GitTimeout = "git-timeout";

    /// <summary>The workspaces folder is not this app's: it existed without <see cref="WorkspaceManager.MarkerFile"/> and already held
    /// other files (or it is a file), so no workspace is made there and nothing in it is ever removed.</summary>
    public const string RootInUse = "root-in-use";

    /// <summary>The code.</summary>
    public string Code { get; } = code;
}

/// <summary>Wording shared by the workspace pages: sizes, base labels and diff summaries.</summary>
public static class WorkspaceText
{
    /// <summary>"84 MB", "1.2 GB", "512 KB", "300 bytes" (binary units, as Explorer shows them).</summary>
    public static string Size(long bytes)
    {
        const double Kilo = 1024;
        if (bytes < Kilo) return bytes == 1 ? "1 byte" : bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / Kilo;
        var unit = 0;
        while (value >= Kilo && unit < units.Length - 1)
        {
            value /= Kilo;
            unit++;
        }
        var format = value < 10 && unit >= 2 ? "0.#" : "0";
        return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"main @ 4f9c2e1", or the commit alone when there is no branch.</summary>
    public static string BaseLabel(string? branch, string? shortCommit) =>
        (branch, shortCommit) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => branch + " @ " + shortCommit,
            (_, { Length: > 0 }) => shortCommit!,
            ({ Length: > 0 }, _) => branch!,
            _ => "the base commit",
        };

    /// <summary>"3 files · +31 −9", "1 file · +4 −4", "no changes".</summary>
    public static string Summary(int files, int added, int removed) =>
        files == 0 ? "no changes" : $"{files} file{(files == 1 ? "" : "s")} · +{added} −{removed}";
}
