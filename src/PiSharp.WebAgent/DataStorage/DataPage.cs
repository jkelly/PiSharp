// Decision 0011 (run W4, unit R9): what Settings › Data & storage shows, measured when the page is asked for (DataStorageService.PageAsync):
// the data folder and its drive, the usage meter, each location (chats, runs.db, the Data Protection key ring, credentials.json), the
// archive of deleted chats, the compare workspaces, run history's settings with what a shorter retention would delete, the export, and the
// facts the danger zone states before anything is deleted. Nothing here is a secret: credentials are counted and named by provider, and an
// environment account by its variable's name, never its value.
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>Where the data folder came from.</summary>
public enum DataFolderSource
{
    /// <summary><c>%LOCALAPPDATA%\PiSharpWebAgent</c>.</summary>
    Default,

    /// <summary>The <c>PISHARP_WEBAGENT_DATA</c> environment variable.</summary>
    Environment,

    /// <summary>The <c>--data</c> argument.</summary>
    CommandLine,
}

/// <summary>The data folder.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="Display">As the page shows it (<c>%LOCALAPPDATA%\PiSharpWebAgent\</c>).</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Bytes">Everything in it.</param>
/// <param name="Drive">Its drive or volume (<c>C:</c>), or empty.</param>
/// <param name="FreeBytes">The drive's free space, when known.</param>
public sealed record DataFolderFacts(string Path, string Display, DataFolderSource Source, long Bytes, string Drive, long? FreeBytes);

/// <summary>One part of the usage meter.</summary>
/// <param name="Label">"run history".</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Segment">The meter's class (<c>seg-accent</c>), or empty for the plain "other files" segment.</param>
/// <param name="Legend">The legend icon's colour class (<c>text-ps-accent</c>).</param>
/// <param name="Href">A link on the label (<c>#compare-workspaces</c>), or null.</param>
public sealed record DataUsageSegment(string Label, long Bytes, string Segment, string Legend, string? Href = null);

/// <summary>The usage meter.</summary>
/// <param name="Total">The data folder's size.</param>
/// <param name="Segments">Its parts, largest first as the mockup orders them (run history, workspaces, chats, keys and settings, other).</param>
public sealed record DataUsage(long Total, IReadOnlyList<DataUsageSegment> Segments)
{
    /// <summary>A segment's width in percent (at least 0.4 when it has any bytes, so it shows).</summary>
    public double Percent(DataUsageSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (Total <= 0 || segment.Bytes <= 0) return 0;
        return Math.Max(0.4, Math.Round(segment.Bytes * 100d / Total, 1));
    }

    /// <summary>The meter's accessible label ("Run history 212 MB, compare workspaces 138 MB, …").</summary>
    public string Label => Segments.Count == 0 ? "Nothing stored yet"
        : string.Join(", ", Segments.Select((segment, index) => (index == 0 ? char.ToUpperInvariant(segment.Label[0]) + segment.Label[1..] : segment.Label) + " " + DataFolders.Size(segment.Bytes)));
}

/// <summary>Chats: the durable device's folder and the archive of deleted chats.</summary>
/// <param name="Available">Whether the chats area is part of the app.</param>
/// <param name="Count">Chats in the sidebar (not deleted).</param>
/// <param name="Running">Chats with a run going on now.</param>
/// <param name="DeviceDisplay">The device folder as shown.</param>
/// <param name="DeviceBytes">The device folder, archive included.</param>
/// <param name="ArchiveDisplay">The archive folder as shown (relative to the data folder: <c>device\archive\</c>).</param>
/// <param name="Archived">Deleted chats in the archive.</param>
/// <param name="ArchiveBytes">The archive's size.</param>
public sealed record DataChatFacts(bool Available, int Count, int Running, string DeviceDisplay, long DeviceBytes, string ArchiveDisplay, int Archived, long ArchiveBytes)
{
    /// <summary>The chats' own sessions (the device folder without the archive).</summary>
    public long SessionBytes => Math.Max(0, DeviceBytes - ArchiveBytes);
}

/// <summary>runs.db.</summary>
/// <param name="Available">Whether run history is part of the app.</param>
/// <param name="Display">runs.db as shown.</param>
/// <param name="Count">Runs stored, counted as History › Runs counts them: top-level runs only, so an agent compare run with its models and
/// their calls is one run (<see cref="RunStore.CountRoots"/>).</param>
/// <param name="Pinned">Pinned runs (top-level, as History counts them).</param>
/// <param name="Bytes">runs.db with its -wal and -shm files.</param>
/// <param name="RawBytes">The raw payloads' text.</param>
/// <param name="RawRuns">Runs with raw payloads stored.</param>
/// <param name="EventBytes">The stream events' text.</param>
/// <param name="Settings">Run history's settings.</param>
/// <param name="OlderThan">For each retention the page offers (7, 30 and 90 days), how many runs it would delete now.</param>
public sealed record DataRunFacts(bool Available, string Display, long Count, long Pinned, long Bytes, long RawBytes, long RawRuns, long EventBytes,
    RunHistorySettings Settings, IReadOnlyDictionary<int, long> OlderThan)
{
    /// <summary>The retentions the page offers, in days (null: forever).</summary>
    public static IReadOnlyList<int?> Choices { get; } = [7, 30, 90, null];

    /// <summary>The rows runs.db holds (<see cref="RunStore.Count"/>): each run, plus each model and call a run that groups calls keeps.
    /// The page shows it as records, beside <see cref="Count"/>, only when the two differ.</summary>
    public long Records { get; init; }

    /// <summary>The records as the page shows them beside the runs ("10 records"), or null when each record is a run.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RecordsText => Records != Count ? DataStorageService.Plural(Records, "record") : null;
}

/// <summary>The Data Protection key ring.</summary>
/// <param name="Display">The keys folder as shown.</param>
/// <param name="Count">Keys in it.</param>
/// <param name="ActiveExpires">When the key that encrypts now expires (a new one is made before), or null when there is none yet.</param>
/// <param name="Dpapi">Whether the keys are protected with Windows DPAPI.</param>
public sealed record DataKeyRingFacts(string Display, int Count, DateTimeOffset? ActiveExpires, bool Dpapi);

/// <summary>credentials.json.</summary>
/// <param name="Available">Whether the credentials area is part of the app.</param>
/// <param name="Display">The file as shown.</param>
/// <param name="Exists">Whether it exists.</param>
/// <param name="Stored">Stored API keys and sign-ins.</param>
/// <param name="ApiKeys">The providers with a stored API key, by name.</param>
/// <param name="SignIns">The providers with a stored sign-in, by name.</param>
/// <param name="Environment">The environment variables that provide keys (names only).</param>
public sealed record DataCredentialFacts(bool Available, string Display, bool Exists, int Stored, IReadOnlyList<string> ApiKeys, IReadOnlyList<string> SignIns,
    IReadOnlyList<string> Environment);

/// <summary>One compare workspace (a model's folder of an agent compare run).</summary>
/// <param name="Run">The run id.</param>
/// <param name="Slot">The model's column (<c>a</c>…<c>d</c>).</param>
/// <param name="Model">The model (<c>anthropic/claude-sonnet-5-5</c>), or the slot's label when the run is not in this app's store.</param>
/// <param name="Kind">"git worktree" or "temporary copy".</param>
/// <param name="Worktree">Whether it is a git worktree.</param>
/// <param name="Bytes">Its disk use, when measured.</param>
/// <param name="AppliedAt">When its changes were applied to the project, or null.</param>
/// <param name="Branch">The branch its changes were kept on, or null.</param>
/// <param name="ExpiresAt">When the cleanup removes it.</param>
/// <param name="ProjectPath">The project it was made from.</param>
/// <param name="RunKnown">Whether Compare knows the run (View run).</param>
/// <param name="Busy">Why it can't be removed now (still running, continued as a chat), or null.</param>
public sealed record DataWorkspaceRow(string Run, string Slot, string Model, string Kind, bool Worktree, long? Bytes, DateTimeOffset? AppliedAt, string? Branch,
    DateTimeOffset ExpiresAt, string ProjectPath, bool RunKnown, string? Busy)
{
    /// <summary>"cmp_01JBQ7 · b".</summary>
    public string Name => Run + " · " + Slot;

    /// <summary>The View run link.</summary>
    public string RunHref => "/compare?mode=agent&run=" + Uri.EscapeDataString(Run);

    /// <summary>"Oct 28 UTC".</summary>
    public string Until => ExpiresAt.UtcDateTime.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture) + " UTC";

    /// <summary>The row's facts: "git worktree · 138 MB · applied to project · kept until Oct 28 UTC".</summary>
    public string Summary => string.Join(" · ", new[]
    {
        Kind, Bytes is { } bytes ? DataFolders.Size(bytes) : null, AppliedAt is not null ? "applied to project" : null,
        Branch is { } branch ? "kept on branch " + branch : null, "kept until " + Until,
    }.OfType<string>());

    /// <summary>What Remove says it does.</summary>
    public string RemoveBody => $"The workspace {Name} ({Model}) is removed now instead of on {Until}. " + (AppliedAt is not null
        ? $"Its changes were already applied to {ProjectPath} as uncommitted changes, and those stay. "
        : Branch is { } branch ? $"Its changes are on the branch {branch}, which stays. "
        : "Its changes were not applied or kept on a branch, so they go with it. ") +
        "You can't continue that agent as a chat in its workspace afterwards.";

    /// <summary>What Remove runs, and what it frees.</summary>
    public string RemoveDetail => (Worktree ? "git worktree remove · git worktree prune" : "folder delete") + (Bytes is { } bytes ? " · " + DataFolders.Size(bytes) + " freed" : "");
}

/// <summary>The compare workspaces.</summary>
/// <param name="Available">Whether agent compare's workspaces are part of the app.</param>
/// <param name="Display">The workspaces folder as shown.</param>
/// <param name="Bytes">The folder's size.</param>
/// <param name="Exists">Whether the folder exists.</param>
/// <param name="Rows">Each workspace on disk, newest run first.</param>
/// <param name="Retention">How long a workspace is kept.</param>
public sealed record DataWorkspaceFacts(bool Available, string Display, long Bytes, bool Exists, IReadOnlyList<DataWorkspaceRow> Rows, TimeSpan Retention)
{
    /// <summary>The workspaces Clean up would remove (not busy).</summary>
    public int Removable => Rows.Count(row => row.Busy is null);

    /// <summary>What Clean up says it does.</summary>
    public string CleanupBody
    {
        get
        {
            var free = Rows.Where(row => row.Busy is null).ToList();
            var busy = Rows.Where(row => row.Busy is not null).ToList();
            var text = $"The {(free.Count == 1 ? "workspace" : free.Count + " workspaces")} not in use {(free.Count == 1 ? "is" : "are")} removed now: " +
                string.Join(", ", free.Select(row => row.Name)) + ". Changes already applied to your projects stay, and so do branches you kept.";
            if (busy.Count > 0) text += " Kept: " + string.Join(", ", busy.Select(row => $"{row.Name} ({row.Busy})")) + ".";
            return text;
        }
    }

    /// <summary>What Clean up frees.</summary>
    public string CleanupDetail => "git worktree remove · folder delete · " + DataFolders.Size(Rows.Where(row => row.Busy is null).Sum(row => row.Bytes ?? 0)) + " freed";
}

/// <summary>The export and import section.</summary>
/// <param name="FileName">Today's file name.</param>
/// <param name="Bytes">The size of an export made now.</param>
/// <param name="Parts">What it holds ("settings", "custom providers").</param>
/// <param name="Notes">What it leaves out, and why.</param>
/// <param name="Chats">Chats an "Export all chats" would hold.</param>
/// <param name="ChatsFileName">Its file name.</param>
public sealed record DataExportFacts(string FileName, long Bytes, IReadOnlyList<string> Parts, IReadOnlyList<string> Notes, int Chats, string ChatsFileName);

/// <summary>What "Reset all settings" would do.</summary>
/// <param name="Changes">What it resets, one phrase per part (empty when everything is at its defaults).</param>
/// <param name="RetentionDaysAfter">The run retention after the reset, when it changes it.</param>
/// <param name="RunsPastRetention">Runs the next cleanup would then delete.</param>
public sealed record DataResetFacts(IReadOnlyList<string> Changes, int? RetentionDaysAfter, long RunsPastRetention)
{
    /// <summary>Whether the retention changes.</summary>
    public bool RetentionChanges { get; init; }
}

/// <summary>Settings › Data &amp; storage.</summary>
public sealed record DataPage
{
    /// <summary>The data folder.</summary>
    public required DataFolderFacts Folder { get; init; }

    /// <summary>The usage meter.</summary>
    public required DataUsage Usage { get; init; }

    /// <summary>Chats.</summary>
    public required DataChatFacts Chats { get; init; }

    /// <summary>runs.db.</summary>
    public required DataRunFacts Runs { get; init; }

    /// <summary>The key ring.</summary>
    public required DataKeyRingFacts Keys { get; init; }

    /// <summary>credentials.json.</summary>
    public required DataCredentialFacts Credentials { get; init; }

    /// <summary>The compare workspaces.</summary>
    public required DataWorkspaceFacts Workspaces { get; init; }

    /// <summary>Export and import.</summary>
    public required DataExportFacts Export { get; init; }

    /// <summary>Reset all settings.</summary>
    public required DataResetFacts Reset { get; init; }

    /// <summary>Whether Open folder works here.</summary>
    public bool OpenFolders { get; init; }

    /// <summary>What the file manager is called ("File Explorer").</summary>
    public string Opener { get; init; } = "the file manager";

    /// <summary>The Open folder targets whose folders exist now (<see cref="DataOpenTargets"/>; a workspace row's folder exists).</summary>
    public IReadOnlySet<string> Existing { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>An Open folder button of <paramref name="target"/>.</summary>
    public DataOpenButton Open(string target, string label = "Open folder", string? run = null, string? slot = null) =>
        new(target, Existing.Contains(target) || target == DataOpenTargets.Workspace, OpenFolders, label, run, slot);
}

/// <summary>An Open folder button (_DataOpenButton).</summary>
/// <param name="Target">What it opens (<see cref="DataOpenTargets"/>).</param>
/// <param name="Exists">Whether the folder exists.</param>
/// <param name="Enabled">Whether Open folder works in this app.</param>
/// <param name="Label">Its accessible label ("Open archive folder").</param>
/// <param name="Run">The run, for a workspace.</param>
/// <param name="Slot">The slot, for a workspace.</param>
public sealed record DataOpenButton(string Target, bool Exists, bool Enabled, string Label, string? Run, string? Slot)
{
    /// <summary>Why it is disabled, or null.</summary>
    public string? Disabled => !Enabled ? "Opening folders is turned off for this app" : !Exists ? "This folder doesn't exist yet" : null;
}
