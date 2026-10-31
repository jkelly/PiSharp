// Decision 0011 (run W4, unit R4): the workspace manager's fixed settings. The root is <data>\workspaces (WorkspacesModule sets it from
// WebAgentOptions, so --data and PISHARP_WEBAGENT_DATA move it); the per-run choices (isolation, extra excludes, the 1 GB warning) come
// with each request from the agent compare settings (R5's AgentCompareSettings).
namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Where workspaces live and how long they are kept.</summary>
public sealed class WorkspaceManagerOptions
{
    /// <summary>The workspaces folder's name under the data folder.</summary>
    public const string FolderName = "workspaces";

    /// <summary>What a copy always skips (by name, at any depth), before the project's <c>.gitignore</c> rules and the extra excludes.
    /// <c>.git</c> is never copied; the others are build output, packages, editor state and Python's caches, which a check command such
    /// as pytest writes into the workspace (after the W5 live test: without a .gitignore they were counted as the model's changes).</summary>
    public static IReadOnlyList<string> DefaultExcludes { get; } =
        ["bin", "obj", "node_modules", ".vs", ".git", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache"];

    /// <summary>The default size above which a copy needs the user's go-ahead: 1 GB.</summary>
    public const long DefaultWarnAboveBytes = 1L << 30;

    /// <summary>The workspaces folder, an absolute path; one sub-folder per run, with one workspace per model.</summary>
    public required string Root { get; init; }

    /// <summary>How long workspaces are kept before the cleanup removes them; default 7 days.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The size above which a copy needs the user's go-ahead, unless a request names another.</summary>
    public long WarnAboveBytes { get; init; } = DefaultWarnAboveBytes;

    /// <summary>The most workspaces one run can have.</summary>
    public int MaxSlots { get; init; } = 8;

    /// <summary>The git executable (found on PATH by default).</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary>The time limit of one git command.</summary>
    public TimeSpan GitTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The time limit of <c>git worktree add</c>, which checks out the whole tree.</summary>
    public TimeSpan CheckoutTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The most files a copy estimate walks before it stops and reports a lower bound.</summary>
    public int EstimateFileLimit { get; init; } = 250_000;

    /// <summary>Environment variables for every git command (tests point <c>GIT_CONFIG_GLOBAL</c> at an empty file); a null value removes
    /// the variable. Never a secret: these are visible to git's child processes.</summary>
    public IReadOnlyDictionary<string, string?> GitEnvironment { get; init; } = new Dictionary<string, string?>();
}
