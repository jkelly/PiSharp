// Decision 0011 (run W4, unit R4): a run's manifest, <root>\<run>\.meta\run.json. It is written with state "creating" before anything is
// made and "ready" once every workspace exists, so a run folder without a manifest, or one still "creating", is a leftover of a crash and
// the startup cleanup removes it. It records what the workspaces were made from (project, repository, base commit, excludes, the
// uncommitted changes left out, the copy rules from above the project and whether the copies honour .gitignore) and each slot's state, so
// diffs, Apply and Keep work after a restart. Writes go to a temporary file first and then replace the manifest.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Gitignore lines from a source above the project (its parents' .gitignore files, info/exclude), with the project's path
/// relative to that source's folder.</summary>
internal sealed class ManifestIgnore
{
    /// <summary>The project's path relative to the source's folder, '/'-terminated (empty for the same folder).</summary>
    public string Prefix { get; set; } = "";

    /// <summary>The source's lines.</summary>
    public List<string> Lines { get; set; } = [];
}

/// <summary>One slot in a manifest.</summary>
internal sealed class ManifestSlot
{
    /// <summary>The slot name.</summary>
    public string Slot { get; set; } = "";

    /// <summary>What it is for.</summary>
    public string? Label { get; set; }

    /// <summary>Ready or discarded.</summary>
    public WorkspaceSlotState State { get; set; } = WorkspaceSlotState.Ready;

    /// <summary>When its changes were applied to the project.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>The kept branch.</summary>
    public string? Branch { get; set; }

    /// <summary>The kept branch's commit.</summary>
    public string? BranchCommit { get; set; }

    /// <summary>A worktree's own repository folder (<c>&lt;repo&gt;\.git\worktrees\&lt;name&gt;</c>), recorded right after
    /// <c>git worktree add</c>, before any agent ran there. Every git command in the slot names it (GIT_DIR) instead of reading the slot's
    /// <c>.git</c> file.</summary>
    public string? GitDir { get; set; }
}

/// <summary>A run's manifest.</summary>
internal sealed class WorkspaceManifest
{
    /// <summary>The manifest's folder name inside the run folder.</summary>
    public const string MetaFolder = ".meta";

    /// <summary>The manifest's file name.</summary>
    public const string FileName = "run.json";

    /// <summary>"creating" until every workspace exists.</summary>
    public const string Creating = "creating";

    /// <summary>Every workspace exists.</summary>
    public const string Ready = "ready";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The run id.</summary>
    public string Run { get; set; } = "";

    /// <summary><see cref="Creating"/> or <see cref="Ready"/>.</summary>
    public string State { get; set; } = Creating;

    /// <summary>Worktrees or copies.</summary>
    public WorkspaceKind Kind { get; set; }

    /// <summary>The project folder.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>The repository root (worktrees; a copy of a folder in a repository).</summary>
    public string? RepoRoot { get; set; }

    /// <summary>The project relative to the repository root.</summary>
    public string ProjectSubpath { get; set; } = "";

    /// <summary>The base commit (worktrees).</summary>
    public string? BaseCommit { get; set; }

    /// <summary>The base commit, abbreviated.</summary>
    public string? ShortCommit { get; set; }

    /// <summary>The base branch.</summary>
    public string? BaseBranch { get; set; }

    /// <summary>When the workspaces were made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When they expire.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The exclude patterns (default and extra).</summary>
    public List<string> Excludes { get; set; } = [];

    /// <summary>Gitignore sources above the project (copies), outermost first; empty when <see cref="UseGitignore"/> is off.</summary>
    public List<ManifestIgnore> OuterIgnores { get; set; } = [];

    /// <summary>Whether the copies skip what .gitignore lists (a manifest written before the switch existed has none, and did).</summary>
    public bool UseGitignore { get; set; } = true;

    /// <summary>The uncommitted changes the worktrees do not have.</summary>
    public List<UncommittedChange> NotCopied { get; set; } = [];

    /// <summary>Links the copies left out.</summary>
    public List<string> SkippedLinks { get; set; } = [];

    /// <summary>Unreadable folders the copies left out.</summary>
    public List<string> Unreadable { get; set; } = [];

    /// <summary>One copy's size.</summary>
    public long? CopyBytes { get; set; }

    /// <summary>The slots, in the order asked for.</summary>
    public List<ManifestSlot> Slots { get; set; } = [];

    /// <summary>The manifest's path in <paramref name="runFolder"/>.</summary>
    public static string PathIn(string runFolder) => System.IO.Path.Join(runFolder, MetaFolder, FileName);

    /// <summary>Reads the manifest of <paramref name="runFolder"/>; null when it is missing or unreadable.</summary>
    public static WorkspaceManifest? Read(string runFolder)
    {
        var path = PathIn(runFolder);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<WorkspaceManifest>(File.ReadAllBytes(path), Json) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Writes the manifest into <paramref name="runFolder"/> (through a temporary file).</summary>
    public void Write(string runFolder)
    {
        var path = PathIn(runFolder);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The slot named <paramref name="slot"/>, or null.</summary>
    public ManifestSlot? Find(string slot) => Slots.FirstOrDefault(entry => string.Equals(entry.Slot, slot, StringComparison.OrdinalIgnoreCase));
}
