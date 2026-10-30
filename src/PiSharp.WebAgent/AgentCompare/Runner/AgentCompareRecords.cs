// Decision 0011 (run W4, R5): what an agent compare run is, as the runner stores it (webagent.db, AgentCompareStore) and answers it
// (/api/compare/agent, AgentCompareEndpoints; R6's pages read the same shapes). A run is one task given to 1 to 4 models, each in its own
// workspace (R4's WorkspaceManager: a git worktree or a temporary copy), with the same instructions, tools and limits; models are started in a
// shuffled order. A model's key is "<run>-<slot>": its Spend Guard run key, its runs.db row id (a child of the run's row) and the name of
// the branch Keep makes (pisharp/compare/<run>-<slot>). State words are wire words (lower case), stored and answered as they are.
using System.Text.Json.Serialization;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>The states of a run.</summary>
public static class AgentRunState
{
    /// <summary>Making the workspaces (and starting the models).</summary>
    public const string Preparing = "preparing";

    /// <summary>Models are running or being checked.</summary>
    public const string Running = "running";

    /// <summary>Every model is done.</summary>
    public const string Finished = "finished";

    /// <summary>The workspaces could not be made; no model ran (<see cref="AgentCompareRun.Error"/> says why).</summary>
    public const string Failed = "failed";
}

/// <summary>Where one model of a run stands.</summary>
public static class AgentModelPhase
{
    /// <summary>Not started yet (its workspace is being made, or it waits its turn in the start order).</summary>
    public const string Pending = "pending";

    /// <summary>Its task was sent; it works (or waits for an approval or a budget answer).</summary>
    public const string Running = "running";

    /// <summary>It stopped; the check command runs in its workspace.</summary>
    public const string Checking = "checking";

    /// <summary>Finished, stopped or failed, and checked; <see cref="AgentCompareModelState.Outcome"/> says how it ended.</summary>
    public const string Done = "done";
}

/// <summary>How a model's run ended.</summary>
public static class AgentModelOutcome
{
    /// <summary>It answered (its final answer is the summary note).</summary>
    public const string Finished = "finished";

    /// <summary>The model's request failed (a provider error, or the Spend Guard stopped it).</summary>
    public const string Failed = "failed";

    /// <summary>The user stopped it (the check does not run).</summary>
    public const string Stopped = "stopped";

    /// <summary>It used all its turns.</summary>
    public const string TurnLimit = "turn-limit";

    /// <summary>Its time was up.</summary>
    public const string TimeLimit = "time-limit";

    /// <summary>Its next turn would have gone over its share of the run cap (Block, or the user stopped it at the Ask).</summary>
    public const string Budget = "budget";

    /// <summary>The runner could not start or follow it (<see cref="AgentCompareModelState.Error"/> says why).</summary>
    public const string Error = "error";
}

/// <summary>How a check command ended.</summary>
public static class AgentCheckState
{
    /// <summary>Exit code 0.</summary>
    public const string Passed = "passed";

    /// <summary>Another exit code.</summary>
    public const string Failed = "failed";

    /// <summary>It ran longer than its timeout and was stopped (with every process it started).</summary>
    public const string TimedOut = "timeout";

    /// <summary>It could not be started.</summary>
    public const string Error = "error";
}

/// <summary>What a model is waiting for.</summary>
public static class AgentModelWaiting
{
    /// <summary>A tool call waits for the user's approval.</summary>
    public const string Approval = "approval";

    /// <summary>Its next turn would go over its share of the run cap (Ask): continue or stop.</summary>
    public const string Budget = "budget";
}

/// <summary>One model of a run, as chosen at the start.</summary>
public sealed record AgentCompareModelSpec
{
    /// <summary>The model's column: <c>a</c>, <c>b</c>, <c>c</c> or <c>d</c>.</summary>
    public required string Slot { get; init; }

    /// <summary>The provider id.</summary>
    public required string Provider { get; init; }

    /// <summary>The model id.</summary>
    public required string Model { get; init; }

    /// <summary>The model's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The thinking level.</summary>
    public required string Thinking { get; init; }

    /// <summary>The account its requests use (bound when the run was created; never switched), or null when the provider needs none.</summary>
    public string? Account { get; init; }

    /// <summary>The account's label, or null.</summary>
    public string? AccountLabel { get; init; }

    /// <summary>How its requests are billed: <c>metered</c>, <c>subscription</c>, <c>free</c> or <c>unknown</c>.</summary>
    public required BillingKind Billing { get; init; }

    /// <summary>Its share of the run cap in micro-USD (the cap divided equally by the metered models with a known price), or null when the
    /// run has no cap or the model is not metered (then only turns and time limit it, and a subscription's list cost is not counted).</summary>
    public long? ShareMicros { get; init; }
}

/// <summary>What a run was asked to do: the same for every model.</summary>
public sealed record AgentCompareSpec
{
    /// <summary>The task (the first message of every model).</summary>
    public required string Task { get; init; }

    /// <summary>The project folder the workspaces are made from (never changed by the run).</summary>
    public required string ProjectPath { get; init; }

    /// <summary>How the workspaces are made.</summary>
    public WorkspaceIsolation Isolation { get; init; } = WorkspaceIsolation.Automatic;

    /// <summary>The base commit or branch of worktrees (null: HEAD).</summary>
    public string? BaseRef { get; init; }

    /// <summary>Extra excludes of a copy.</summary>
    public IReadOnlyList<string> ExtraExcludes { get; init; } = [];

    /// <summary>Whether a copy also skips what .gitignore lists (the agent compare defaults' <c>useGitignore</c> unless the create request
    /// names it; a run stored before the switch existed has none, and did).</summary>
    public bool UseGitignore { get; init; } = true;

    /// <summary>A copy larger than this needs <see cref="ConfirmLargeCopy"/>.</summary>
    public long WarnAboveBytes { get; init; } = WorkspaceManagerOptions.DefaultWarnAboveBytes;

    /// <summary>The user confirmed a large copy.</summary>
    public bool ConfirmLargeCopy { get; init; }

    /// <summary>The check command, run in each workspace when its model stops (null: none).</summary>
    public string? CheckCommand { get; init; }

    /// <summary>How long the check may run, in seconds.</summary>
    public int CheckTimeoutSeconds { get; init; } = AgentCompareSettings.DefaultCheckTimeoutMinutes * 60;

    /// <summary>The most turns (model requests) each model may make.</summary>
    public int MaxTurns { get; init; } = AgentCompareSettings.DefaultMaxTurns;

    /// <summary>The most minutes each model may run.</summary>
    public int MaxMinutes { get; init; } = AgentCompareSettings.DefaultMaxMinutes;

    /// <summary>The run cap in micro-USD, shared equally by the metered models; null for none.</summary>
    public long? CapMicros { get; init; }

    /// <summary>What happens when a model's next turn would go over its share: Ask (it pauses and asks) or Block (it stops).</summary>
    public CapAction CapAction { get; init; } = CapAction.Ask;

    /// <summary>The run's tool approval mode for its workspaces (overrides Settings › Tools for these sessions only).</summary>
    public ToolApprovalMode Approval { get; init; } = ToolApprovalMode.Ask;

    /// <summary>The user's shared system prompt (the setup's "System prompt"), or null for none. It is part of
    /// <see cref="Instructions"/>; kept on its own so New run can copy it.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The instructions every model gets (the same text for all): the shared system prompt, then the runner's workspace rules.</summary>
    public required string Instructions { get; init; }

    /// <summary>The models, by slot.</summary>
    public required IReadOnlyList<AgentCompareModelSpec> Models { get; init; }
}

/// <summary>The latest tool call of a model ("edit ModelRegistry.cs · src/…").</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="Summary">The command, or the path, cut to 200 characters.</param>
/// <param name="At">When the model asked for it.</param>
public sealed record AgentToolCall(string Tool, string Summary, DateTimeOffset At);

/// <summary>One changed file of a model's workspace.</summary>
public sealed record AgentDiffFile(string Path, WorkspaceFileChange Change, int Added, int Removed, bool Binary);

/// <summary>What a model changed in its workspace (R4's diff against the base), once it stopped.</summary>
public sealed record AgentDiffSummary
{
    /// <summary>Changed files.</summary>
    public int Files { get; init; }

    /// <summary>Added lines.</summary>
    public int Added { get; init; }

    /// <summary>Removed lines.</summary>
    public int Removed { get; init; }

    /// <summary>"3 files · +31 −9".</summary>
    public required string Summary { get; init; }

    /// <summary>The files (at most 200).</summary>
    public IReadOnlyList<AgentDiffFile> Items { get; init; } = [];
}

/// <summary>How a check command ended.</summary>
public sealed record AgentCheckResult
{
    /// <summary><see cref="AgentCheckState"/>.</summary>
    public required string State { get; init; }

    /// <summary>The command.</summary>
    public required string Command { get; init; }

    /// <summary>Its exit code, or null (timed out, or not started).</summary>
    public int? ExitCode { get; init; }

    /// <summary>The last part of its output (stdout and stderr together, at most <see cref="AgentCheckCommand.TailCharacters"/>).</summary>
    public string Tail { get; init; } = "";

    /// <summary>A test summary found in the output ("38 passed", "2 failed · 36 passed"), or null.</summary>
    public string? Summary { get; init; }

    /// <summary>The timeout it had, in seconds.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>How long it ran.</summary>
    public long DurationMs { get; init; }

    /// <summary>When it started.</summary>
    public DateTimeOffset Started { get; init; }

    /// <summary>When it ended.</summary>
    public DateTimeOffset Finished { get; init; }
}

/// <summary>A budget Ask a model waits on: its next turn would go over its share.</summary>
/// <param name="SpentMicros">What its run key counts so far.</param>
/// <param name="NextMicros">The next turn's worst case.</param>
/// <param name="ShareMicros">Its share.</param>
/// <param name="Since">When it started waiting.</param>
public sealed record AgentBudgetAsk(long SpentMicros, long NextMicros, long ShareMicros, DateTimeOffset Since);

/// <summary>One model of a run, as it stands.</summary>
public sealed record AgentCompareModelState
{
    /// <summary>The slot.</summary>
    public required string Slot { get; init; }

    /// <summary><see cref="AgentModelPhase"/>.</summary>
    public string Phase { get; init; } = AgentModelPhase.Pending;

    /// <summary><see cref="AgentModelOutcome"/>, set when it stops (or when a stop was asked for), else null.</summary>
    public string? Outcome { get; init; }

    /// <summary>Why it stopped, in a sentence (a limit, the user, an error), or null.</summary>
    public string? Message { get; init; }

    /// <summary>Its durable session (a chat session without a chat binding until it is opened as a chat).</summary>
    public string? SessionId { get; init; }

    /// <summary>The submission of its task.</summary>
    public long? SubmissionId { get; init; }

    /// <summary>Its workspace's root.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>Its working directory (the project folder inside the workspace).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>When its task was sent (its time limit counts from here).</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it stopped working (before the check).</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Its final answer (cut to 8,000 characters), or null.</summary>
    public string? Answer { get; init; }

    /// <summary>The error, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Its latest tool call, or null.</summary>
    public AgentToolCall? LatestTool { get; init; }

    /// <summary>The files its write and edit calls named, relative to its working directory where they are inside it (at most 200).</summary>
    public IReadOnlyList<string> Touched { get; init; } = [];

    /// <summary>The budget Ask it waits on, or null.</summary>
    public AgentBudgetAsk? BudgetAsk { get; init; }

    /// <summary>Turns the user let go over the share once each (Continue).</summary>
    public int BudgetPasses { get; init; }

    /// <summary>Whether the user let it go over its share for the rest of the run.</summary>
    public bool BudgetLifted { get; init; }

    /// <summary>Its check, or null (none, not yet, or stopped by the user).</summary>
    public AgentCheckResult? Check { get; init; }

    /// <summary>What it changed, once it stopped.</summary>
    public AgentDiffSummary? Diff { get; init; }

    /// <summary>The chat it continues in (its session id), once opened as a chat.</summary>
    public string? ChatId { get; init; }

    /// <summary>The Keep that went ahead (applied, or a branch), or null.</summary>
    public KeepResult? Kept { get; init; }

    /// <summary>Whether its workspace was discarded.</summary>
    public bool Discarded { get; init; }
}

/// <summary>Where a run's workspaces are (a copy of R4's facts, kept after they are discarded).</summary>
public sealed record AgentWorkspaceInfo
{
    /// <summary>Worktree or copy.</summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>"git worktree from main @ 4f9c2e1", "temporary copy".</summary>
    public required string Label { get; init; }

    /// <summary>What diffs are against ("main @ 4f9c2e1", "the base copy").</summary>
    public required string BaseLabel { get; init; }

    /// <summary>The base commit (worktrees), or null.</summary>
    public string? BaseCommit { get; init; }

    /// <summary>Its short form, or null.</summary>
    public string? ShortCommit { get; init; }

    /// <summary>The base branch, or null.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>The run's folder under the workspaces root.</summary>
    public required string Directory { get; init; }

    /// <summary>The project's uncommitted changes, which were not copied into worktrees.</summary>
    public IReadOnlyList<UncommittedChange> NotCopied { get; init; } = [];

    /// <summary>What a copy left out.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>Links a copy skipped.</summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];
}

/// <summary>One agent compare run.</summary>
public sealed record AgentCompareRun
{
    /// <summary>The run id: <c>cmp_</c> and 16 Crockford base32 characters (time, then random).</summary>
    public required string Id { get; init; }

    /// <summary>The create request's id (a repeated create answers this run).</summary>
    public required string RequestId { get; init; }

    /// <summary><see cref="AgentRunState"/>.</summary>
    public string State { get; init; } = AgentRunState.Preparing;

    /// <summary>What it was asked to do.</summary>
    public required AgentCompareSpec Spec { get; init; }

    /// <summary>The slots in the shuffled order their models were started.</summary>
    public required IReadOnlyList<string> Order { get; init; }

    /// <summary>Commands allowed in every workspace for the rest of the run ("Allow this command in all workspaces").</summary>
    public IReadOnlyList<ToolAllowRule> Grants { get; init; } = [];

    /// <summary>The workspaces, once made.</summary>
    public AgentWorkspaceInfo? Workspace { get; init; }

    /// <summary>Why the run failed, or null.</summary>
    public string? Error { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>When its first model was started.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>When its last model was done.</summary>
    public DateTimeOffset? Finished { get; init; }

    /// <summary>Its models, by slot.</summary>
    public required IReadOnlyList<AgentCompareModelState> Models { get; init; }

    /// <summary>The model of <paramref name="slot"/>, or null.</summary>
    public AgentCompareModelState? Model(string slot) => Models.FirstOrDefault(model => model.Slot == slot);

    /// <summary>The spec of <paramref name="slot"/>, or null.</summary>
    public AgentCompareModelSpec? ModelSpec(string slot) => Spec.Models.FirstOrDefault(model => model.Slot == slot);

    /// <summary>Whether a model still runs or is checked (or the workspaces are being made).</summary>
    [JsonIgnore]
    public bool IsActive => State is AgentRunState.Preparing or AgentRunState.Running;

    /// <summary>The key of <paramref name="slot"/>'s model: <c>&lt;run&gt;-&lt;slot&gt;</c>.</summary>
    public static string ModelKey(string run, string slot) => run + "-" + slot;
}

/// <summary>A model to compare, as a create request names it.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Thinking">The thinking level, or null for the default.</param>
/// <param name="Account">The account to use, or null for the provider's active account.</param>
public sealed record AgentCompareModelRequest(string Provider, string Model, string? Thinking = null, string? Account = null);

/// <summary>A request to start a run. Null fields take the Settings › Tools agent compare defaults.</summary>
public sealed record AgentCompareCreateRequest
{
    /// <summary>The request's id: a repeated create with it answers the run it made.</summary>
    public required string RequestId { get; init; }

    /// <summary>The task.</summary>
    public required string Task { get; init; }

    /// <summary>The shared system prompt every model gets, or null (or blank) for none.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The project folder.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>1 to 4 models.</summary>
    public required IReadOnlyList<AgentCompareModelRequest> Models { get; init; }

    /// <summary>How the workspaces are made.</summary>
    public WorkspaceIsolation? Isolation { get; init; }

    /// <summary>The base commit or branch of worktrees.</summary>
    public string? BaseRef { get; init; }

    /// <summary>Extra excludes (on top of the defaults' and the settings').</summary>
    public IReadOnlyList<string>? ExtraExcludes { get; init; }

    /// <summary>Whether a copy also skips what .gitignore lists, or null for the agent compare defaults' setting.</summary>
    public bool? UseGitignore { get; init; }

    /// <summary>Confirms a copy larger than the warning size.</summary>
    public bool ConfirmLargeCopy { get; init; }

    /// <summary>The check command, or null for none.</summary>
    public string? CheckCommand { get; init; }

    /// <summary>The check timeout in seconds (1 to 14,400), or null for the settings' minutes.</summary>
    public int? CheckTimeoutSeconds { get; init; }

    /// <summary>The turn limit per model.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>The time limit per model, in minutes.</summary>
    public int? MaxMinutes { get; init; }

    /// <summary>The run cap in micro-USD, or null for none.</summary>
    public long? CapMicros { get; init; }

    /// <summary>Ask (the default) or Block at a model's share.</summary>
    public CapAction? CapAction { get; init; }

    /// <summary>The run's approval mode.</summary>
    public ToolApprovalMode? Approval { get; init; }
}
