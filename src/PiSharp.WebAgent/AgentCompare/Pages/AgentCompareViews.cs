// Decision 0011 (run W4, R6): the models of agent compare's views (Views/Compare/_AgentMode and the _Agent* partials, _KeepResultModal),
// after the mockups' compare.html agent states: agent-setup and agent-dirty (_AgentSetup, _AgentSetupColumn, _AgentProject), agent-running,
// agent-approval, agent-done and agent-kept (_AgentRun, _AgentColumn), and the modals modal-agent-workspace (_AgentWorkspace),
// modal-agent-diff (_AgentDiff) and modal-keep-result (_AgentKeep). Every figure is the run's own (R5's AgentCompareRun, the Spend Guard
// ledger's total of each model's run key, the turns the runner counted, the approvals the broker holds) or R4's (an inspection, a diff, a
// keep preview); nothing here is sample data. The run view is built from a run and its live data only (AgentRunView.Build), so a test
// renders any state without a provider. Workspaces are copies, not sandboxes: wherever tools run without asking (Allow-list, Auto, a
// command allowed in every workspace) the views say so in visible text, never only in a tooltip.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Pages;

/// <summary>What <c>_AgentMode</c> shows: a new run's setup, or a run.</summary>
public sealed record AgentModeView
{
    /// <summary>The setup (no <c>?run=</c>, or a run that is gone), else null.</summary>
    public AgentSetupView? Setup { get; init; }

    /// <summary>The run of <c>?run=</c>, else null.</summary>
    public AgentRunView? Run { get; init; }

    /// <summary>A notice above the setup (the run asked for is gone), or null.</summary>
    public string? Notice { get; init; }
}

/// <summary>A choice of the setup's project folder menu.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Note">How a recent run used it ("git worktree", "temporary copy"), or "working directory".</param>
public sealed record AgentRecentProject(string Path, string Note);

/// <summary>The model of <c>_AgentSetup</c>: a new run (agent-setup, agent-dirty), maybe prefilled from an earlier run (New run).</summary>
public sealed record AgentSetupView
{
    /// <summary>The task.</summary>
    public string Task { get; init; } = "";

    /// <summary>The shared system prompt every model gets (empty for none; New run copies the earlier run's).</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>The System prompt toggle's preview: "· " and the prompt's first 70 characters, or "· none".</summary>
    public string SystemPreview => SystemPrompt.Trim() is { Length: > 0 } text ? "· " + (text.Length > 70 ? text[..70] + "…" : text) : "· none";

    /// <summary>The project folder (the Settings › Tools working directory, or the earlier run's).</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Recent project folders, newest first (the working directory first).</summary>
    public IReadOnlyList<AgentRecentProject> RecentProjects { get; init; } = [];

    /// <summary>The Settings › Tools agent compare defaults.</summary>
    public AgentCompareSettings Defaults { get; init; } = new();

    /// <summary>How workspaces are made for this run (the defaults', or the earlier run's).</summary>
    public WorkspaceIsolation Isolation { get; init; } = WorkspaceIsolation.Automatic;

    /// <summary>The base commit or branch of worktrees, or null (HEAD).</summary>
    public string? BaseRef { get; init; }

    /// <summary>This run's extra excludes (on top of the defaults' and the settings').</summary>
    public IReadOnlyList<string> ExtraExcludes { get; init; } = [];

    /// <summary>The check command, or empty.</summary>
    public string CheckCommand { get; init; } = "";

    /// <summary>Turns per model.</summary>
    public int MaxTurns { get; init; } = AgentCompareSettings.DefaultMaxTurns;

    /// <summary>Minutes per model.</summary>
    public int MaxMinutes { get; init; } = AgentCompareSettings.DefaultMaxMinutes;

    /// <summary>The run cap in dollars as the field shows it ("1.50"), or empty for none.</summary>
    public string Cap { get; init; } = "";

    /// <summary>Ask or Block at a model's share.</summary>
    public CapAction CapAction { get; init; } = CapAction.Ask;

    /// <summary>The run's tool approval mode.</summary>
    public ToolApprovalMode Approval { get; init; } = ToolApprovalMode.Ask;

    /// <summary>The shared thinking level (each column clamps it to its model's levels, or overrides it).</summary>
    public string Thinking { get; init; } = "medium";

    /// <summary>The levels the shared control offers (the durable host's: off to xhigh).</summary>
    public IReadOnlyList<string> ThinkingLevels { get; init; } = AgentSetupColumnView.HostLevels;

    /// <summary>The columns (1 to 4).</summary>
    public IReadOnlyList<AgentSetupColumnView> Columns { get; init; } = [];

    /// <summary>The model picker.</summary>
    public required ModelPickerView Picker { get; init; }

    /// <summary>The run this setup was copied from, or null.</summary>
    public string? FromRun { get; init; }

    /// <summary>How many rules the Settings › Tools allow-list has (the Allow-list choice runs those without asking).</summary>
    public int AllowRules { get; init; }

    /// <summary>A notice (the run asked for is gone), or null.</summary>
    public string? Notice { get; init; }

    /// <summary>The "Limits and approvals" toggle's summary: "40 turns · 20 min · no cap · Ask".</summary>
    public string LimitsSummary => $"{MaxTurns} turns · {MaxMinutes} min · {(Cap.Length > 0 ? "cap $" + Cap : "no cap")} · {AgentText.Approval(Approval)}";
}

/// <summary>The model of <c>_AgentSetupColumn</c>: one model of a run being set up.</summary>
public sealed record AgentSetupColumnView
{
    /// <summary>The thinking levels the durable host accepts (<c>max</c> is not one of them yet).</summary>
    public static IReadOnlyList<string> HostLevels { get; } = ["off", "minimal", "low", "medium", "high", "xhigh"];

    /// <summary>The column: <c>a</c> to <c>d</c>.</summary>
    public required string Slot { get; init; }

    /// <summary>The provider id.</summary>
    public required string Provider { get; init; }

    /// <summary>The model id.</summary>
    public required string Model { get; init; }

    /// <summary>The model's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The provider's name.</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>Whether the catalog lists the model.</summary>
    public bool Known { get; init; }

    /// <summary>The model's thinking levels that the host accepts, off first.</summary>
    public IReadOnlyList<string> Levels { get; init; } = ["off"];

    /// <summary>This column's thinking override, or null (the shared level, clamped).</summary>
    public string? Thinking { get; init; }

    /// <summary>An override the model does not take, kept for when the column switches back (a disabled option keeps its value).</summary>
    public string? KeptThinking { get; init; }

    /// <summary>The shared level.</summary>
    public string SharedThinking { get; init; } = "medium";

    /// <summary>How the provider bills.</summary>
    public BillingKind Billing { get; init; } = BillingKind.Unknown;

    /// <summary>The account a run would bind now (the provider's active account), or null.</summary>
    public string? AccountLabel { get; init; }

    /// <summary>Turns per model.</summary>
    public int MaxTurns { get; init; } = AgentCompareSettings.DefaultMaxTurns;

    /// <summary>Minutes per model.</summary>
    public int MaxMinutes { get; init; } = AgentCompareSettings.DefaultMaxMinutes;

    /// <summary>Whether Remove is offered (more than one column).</summary>
    public bool CanRemove { get; init; } = true;

    /// <summary>The column letter.</summary>
    public string Letter => Slot.ToUpperInvariant();

    /// <summary>The level the run sends: the override, or the shared level clamped to the model's (as the runner clamps it).</summary>
    public string Effective => Thinking ?? Clamp(Levels, SharedThinking);

    /// <summary>How the shared level applies to this model: "thinking n/a", "thinking xhigh → high", or null when it applies as it is.</summary>
    public CompareChipText? Chip
    {
        get
        {
            if (!Known) return new CompareChipText("not available", "danger", "This model is not in the catalog any more; choose another.");
            if (Thinking is not null) return new CompareChipText("thinking " + Thinking, "accent", "This column's own thinking level.");
            if (SharedThinking == "off") return null;
            if (Levels.Count <= 1) return new CompareChipText("thinking n/a", "outline", "This model doesn't think, so it runs without thinking.");
            var effective = Effective;
            return effective == SharedThinking ? null
                : new CompareChipText($"thinking {SharedThinking} → {effective}", "outline", $"{Name} thinks at {string.Join(", ", Levels.Skip(1))}, so it runs at {effective}.");
        }
    }

    /// <summary>The body's billing line: "Personal · subscription", "API key · metered".</summary>
    public string BillingLine => (AccountLabel is { Length: > 0 } label ? label + " · " : "") + Billing switch
    {
        BillingKind.Metered => "metered",
        BillingKind.Subscription => "subscription",
        BillingKind.Free => "free",
        _ => "price unknown",
    };

    /// <summary>The body's limits line.</summary>
    public string LimitsLine => $"Stops at {MaxTurns} turns or {MaxMinutes} min." + Billing switch
    {
        BillingKind.Metered => " With a run cap, it asks (or stops) at its share.",
        BillingKind.Subscription => " List cost isn't counted toward caps.",
        BillingKind.Free => " It costs nothing.",
        _ => " Its cost can't be estimated.",
    };

    /// <summary>The override panel's note.</summary>
    public string OverrideNote => !Known ? "This model is not available; choose another."
        : (Levels.Count <= 1 ? "This model doesn't think." : $"Thinks at {string.Join(", ", Levels.Skip(1))}.")
          + (AccountLabel is { Length: > 0 } label ? $" Runs on {label}, the account that's active when the run starts." : "");

    /// <summary><see cref="PiSharp.AI.ThinkingLevels.Clamp"/> over <paramref name="levels"/>.</summary>
    public static string Clamp(IReadOnlyList<string> levels, string level) =>
        PiSharp.AI.ThinkingLevels.Clamp([.. levels.Count == 0 ? ["off"] : levels], level);
}

/// <summary>A chip: text, tone (<c>ok</c>, <c>warn</c>, <c>danger</c>, <c>accent</c>, <c>outline</c> or null) and a tooltip.</summary>
public sealed record CompareChipText(string Text, string? Tone = null, string? Title = null);

/// <summary>The model of <c>_AgentProject</c>: the setup's project row (path, isolation chip, the dirty and large-copy banners) and the
/// workspace modal's content for it.</summary>
public sealed record AgentProjectView
{
    /// <summary>The folder asked for.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>R4's inspection, or null when the folder could not be inspected (<see cref="Error"/>).</summary>
    public ProjectInspection? Inspection { get; init; }

    /// <summary>Why the folder could not be inspected, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The workspace modal for it.</summary>
    public required AgentWorkspaceView Workspace { get; init; }

    /// <summary>The chip's tone: warn when uncommitted changes are left out or the copy is large.</summary>
    public string? Tone => Inspection is { } inspection && (inspection.Kind == WorkspaceKind.Worktree && inspection.Uncommitted.Count > 0 || inspection.OverWarnLimit) ? "warn" : null;

    /// <summary>The isolation chip: R4's label ("git worktree from main @ 4f9c2e1", "temporary copy · 84 MB"), or, for a worktree whose
    /// base the user chose in Workspace details, that branch or commit ("git worktree from release", "git worktree from 1a2b3c4").</summary>
    public string ChipLabel => Inspection is { Kind: WorkspaceKind.Worktree } inspection && Workspace.BaseRef is { Length: > 0 } chosen
        && chosen != inspection.CurrentBranch && chosen != inspection.HeadCommit
            ? "git worktree from " + (IsCommit(chosen) && chosen.Length > 7 ? chosen[..Math.Max(7, inspection.ShortCommit?.Length ?? 7)] : chosen)
            : Inspection?.Label ?? "";

    private static bool IsCommit(string text) => text.Length is >= 7 and <= 64 && text.All(char.IsAsciiHexDigit);

    /// <summary>The chip's tooltip.</summary>
    public string ChipTitle => Inspection switch
    {
        null => "The folder could not be checked.",
        { Kind: WorkspaceKind.Worktree, Uncommitted.Count: > 0 } inspection =>
            $"{AgentText.Count(inspection.Uncommitted.Count, "uncommitted file")} {(inspection.Uncommitted.Count == 1 ? "isn't" : "aren't")} copied into the worktrees. Click for details.",
        { Kind: WorkspaceKind.Worktree } => "Each model gets its own git worktree. Click for details.",
        _ => "Each model gets its own temporary copy of the folder. Click for details.",
    };
}

/// <summary>The model of <c>_AgentWorkspace</c> (modal-agent-workspace): how a run's workspaces are made. Editable in setup (from R4's
/// inspection); read-only once a run made them (from the run's <see cref="AgentWorkspaceInfo"/>).</summary>
public sealed record AgentWorkspaceView
{
    /// <summary>Whether the run's workspaces exist (the modal is read-only).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The run, or null in setup.</summary>
    public string? Run { get; init; }

    /// <summary>The project folder.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Worktree or copy (what the run made, or what the setup would make), or null when not known.</summary>
    public WorkspaceKind? Kind { get; init; }

    /// <summary>The isolation asked for.</summary>
    public WorkspaceIsolation Isolation { get; init; } = WorkspaceIsolation.Automatic;

    /// <summary>Why a worktree can't be used (not git, no commit, git missing), or null.</summary>
    public string? WorktreeUnavailable { get; init; }

    /// <summary>The base branch (the project's current branch), or null.</summary>
    public string? Branch { get; init; }

    /// <summary>The base commit's short form, or null.</summary>
    public string? ShortCommit { get; init; }

    /// <summary>The project's branches.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>The base ref asked for (a branch or commit), or null.</summary>
    public string? BaseRef { get; init; }

    /// <summary>Uncommitted changes, which worktrees don't get.</summary>
    public IReadOnlyList<UncommittedChange> Uncommitted { get; init; } = [];

    /// <summary>A checkout's size, or null.</summary>
    public long? CheckoutBytes { get; init; }

    /// <summary>A copy's size, or null.</summary>
    public long? CopyBytes { get; init; }

    /// <summary>Whether the copy size is complete (false: the count stopped early, so it's at least that).</summary>
    public bool CopyComplete { get; init; } = true;

    /// <summary>What every copy leaves out (the defaults).</summary>
    public IReadOnlyList<string> DefaultExcludes { get; init; } = WorkspaceManagerOptions.DefaultExcludes;

    /// <summary>The extra excludes of Settings › Tools.</summary>
    public IReadOnlyList<string> SettingsExcludes { get; init; } = [];

    /// <summary>This run's extra excludes.</summary>
    public IReadOnlyList<string> ExtraExcludes { get; init; } = [];

    /// <summary>Links a copy skipped.</summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];

    /// <summary>A copy above this asks first.</summary>
    public long WarnAboveBytes { get; init; } = (long)(AgentCompareSettings.DefaultCopyWarnGb * AgentCompareSettings.BytesPerGb);

    /// <summary>Where the run's workspaces are (read-only), or the workspaces root (setup).</summary>
    public required string Directory { get; init; }

    /// <summary>The slots.</summary>
    public IReadOnlyList<string> Slots { get; init; } = ["a", "b"];

    /// <summary>How many days a workspace is kept.</summary>
    public int RetentionDays { get; init; } = 7;

    /// <summary>The note of a run whose workspaces are not there (being made, or never made), or null.</summary>
    public string? Note { get; init; }

    /// <summary>The pane shown first: <c>worktree</c> or <c>copy</c>.</summary>
    public string Pane => Kind == WorkspaceKind.Copy || Isolation == WorkspaceIsolation.Copy || WorktreeUnavailable is not null && !ReadOnly ? "copy" : "worktree";

    /// <summary>"\a, \b and \c".</summary>
    public string SlotList => AgentText.Join(Slots.Select(slot => Path.DirectorySeparatorChar + slot).ToList());

    /// <summary>The disk line of worktrees.</summary>
    public string WorktreeDisk => CheckoutBytes is { } bytes
        ? $"about {WorkspaceText.Size(bytes)} per workspace before it's built · about {WorkspaceText.Size(bytes * Slots.Count)} for {Slots.Count}"
        : "the size of a checkout per workspace";

    /// <summary>The disk line of copies (a base copy for diffs plus one per model).</summary>
    public string CopyDisk => CopyBytes is { } bytes
        ? $"{(CopyComplete ? "about" : "at least")} {WorkspaceText.Size(bytes * (Slots.Count + 1))}: a base copy for diffs plus {Slots.Count} working {(Slots.Count == 1 ? "copy" : "copies")}"
        : $"a base copy for diffs plus {Slots.Count} working {(Slots.Count == 1 ? "copy" : "copies")}";
}

/// <summary>The model of <c>_AgentRun</c>: a run (agent-running, agent-approval, agent-done, agent-kept).</summary>
public sealed record AgentRunView
{
    /// <summary>The run.</summary>
    public required AgentCompareRun Run { get; init; }

    /// <summary>Its columns, by slot.</summary>
    public required IReadOnlyList<AgentColumnView> Columns { get; init; }

    /// <summary>The tool calls that wait for an answer, by slot then age.</summary>
    public IReadOnlyList<AgentPendingApproval> Pending { get; init; } = [];

    /// <summary>When the view was built.</summary>
    public DateTimeOffset Now { get; init; }

    /// <summary>The workspace modal (read-only).</summary>
    public required AgentWorkspaceView Workspace { get; init; }

    /// <summary>The run id.</summary>
    public string Id => Run.Id;

    /// <summary>Whether a model still runs or is checked.</summary>
    public bool Active => Run.IsActive;

    /// <summary>"Run cmp_… · today 09:12 UTC".</summary>
    public string CreatedText => AgentText.When(Run.Created, Now);

    /// <summary>The top bar's subtitle.</summary>
    public string Subtitle => "Agent · run " + Run.Id;

    /// <summary>Counted spend of every model.</summary>
    public long CountedMicros => Columns.Sum(column => column.Spend?.CountedMicros ?? 0);

    /// <summary>A subscription's list cost of every model.</summary>
    public long ListMicros => Columns.Sum(column => column.Spend?.ListMicros ?? 0);

    /// <summary>The spend chip: "$0.17 of $1.50", or "$0.17 spent".</summary>
    public string SpendText => Run.Spec.CapMicros is { } cap ? $"{SpendMoney.Format(CountedMicros)} of {SpendMoney.Format(cap)}" : SpendMoney.Format(CountedMicros) + " spent";

    /// <summary>The spend chip's tooltip (HTML; each part encoded).</summary>
    public string SpendTooltip
    {
        get
        {
            var html = HtmlEncoder.Default;
            var builder = new StringBuilder();
            builder.Append("<strong>").Append(html.Encode((Active ? "Spent so far · " : "Spent · ") + SpendText + (Run.Spec.CapMicros is not null ? " cap" : ""))).Append("</strong>");
            foreach (var column in Columns)
                builder.Append("<br>").Append(html.Encode(column.Letter + " · " + column.Name + " · " + column.SpendLine));
            if (Run.Spec.CapMicros is null) builder.Append("<br>").Append(html.Encode("No run cap: turns and time limit each model. Each turn still reserves its worst case first."));
            return builder.ToString();
        }
    }

    /// <summary>The status line's text (what is running, waiting or done).</summary>
    public string StatusText
    {
        get
        {
            var count = Columns.Count;
            switch (Run.State)
            {
                case AgentRunState.Failed:
                    return "Not run · the workspaces could not be made";
                case AgentRunState.Preparing:
                    return $"Making {AgentText.Count(count, "workspace")}";
                case AgentRunState.Finished:
                {
                    var outcomes = Columns.GroupBy(column => column.State.Outcome ?? AgentModelOutcome.Finished)
                        .Where(group => group.Key != AgentModelOutcome.Finished)
                        .Select(group => $"{group.Count()} {AgentText.OutcomeWord(group.Key)}").ToList();
                    var text = outcomes.Count == 0 ? (count == 1 ? "Finished" : $"All {count} finished") : $"All {count} done · " + string.Join(" · ", outcomes);
                    var checks = Columns.Where(column => column.State.Check is not null).ToList();
                    if (checks.Count > 0) text += $" · {checks.Count(column => column.State.Check!.State == AgentCheckState.Passed)} of {checks.Count} checks passed";
                    return text;
                }
                default:
                {
                    var parts = new List<string>();
                    var running = Columns.Count(column => column.State.Phase == AgentModelPhase.Running && column.Waiting is null);
                    var checking = Columns.Count(column => column.State.Phase == AgentModelPhase.Checking);
                    var pending = Columns.Count(column => column.State.Phase == AgentModelPhase.Pending);
                    var done = Columns.Count(column => column.State.Phase == AgentModelPhase.Done);
                    if (running > 0) parts.Add($"{running} running");
                    var approval = Columns.Where(column => column.Waiting == AgentModelWaiting.Approval).Select(column => column.Letter).ToList();
                    if (approval.Count > 0) parts.Add($"{AgentText.Join(approval)} waiting for approval");
                    var budget = Columns.Where(column => column.Waiting == AgentModelWaiting.Budget).Select(column => column.Letter).ToList();
                    if (budget.Count > 0) parts.Add($"{AgentText.Join(budget)} at {(budget.Count == 1 ? "its" : "their")} share of the cap");
                    if (checking > 0) parts.Add($"{checking} checking");
                    if (pending > 0) parts.Add($"{pending} starting");
                    if (done > 0) parts.Add($"{done} done");
                    return parts.Count == 0 ? "Starting" : string.Join(" · ", parts);
                }
            }
        }
    }

    /// <summary>The status line's tone: live while it runs, danger when it failed, else faint.</summary>
    public string StatusTone => Run.State == AgentRunState.Failed ? "danger" : Active ? "live" : "faint";

    /// <summary>The status line's tooltip once finished ("Check passed in A and B, failed in C · $0.40 of the $1.50 cap spent").</summary>
    public string? StatusTitle
    {
        get
        {
            if (Active || Run.State == AgentRunState.Failed) return null;
            var parts = new List<string>();
            var checks = Columns.Where(column => column.State.Check is not null).GroupBy(column => column.State.Check!.State).ToList();
            if (checks.Count > 0)
                parts.Add("Check " + string.Join(", ", checks.Select(group => AgentText.CheckWord(group.Key) + " in " + AgentText.Join(group.Select(column => column.Letter).ToList()))));
            parts.Add(SpendText + (Run.Spec.CapMicros is not null ? " cap spent" : "") + (ListMicros > 0 ? ", plus " + SpendMoney.FormatList(ListMicros) : ""));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The visible not-a-sandbox line when tools run without asking in this run (Allow-list, Auto, or commands allowed in every
    /// workspace), else null.</summary>
    public string? SandboxWarning
    {
        get
        {
            var parts = new List<string>();
            if (Run.Spec.Approval == ToolApprovalMode.Auto) parts.Add("Tool approval is Auto in workspaces: every tool call runs without asking.");
            else if (Run.Spec.Approval == ToolApprovalMode.AllowList) parts.Add("Tool approval is Allow-list: bash commands on your Settings › Tools allow-list run without asking.");
            if (Run.Grants.Count > 0)
                parts.Add($"Allowed in every workspace for this run: {string.Join(", ", Run.Grants.Select(grant => grant.Command ?? "every " + grant.Tool + " call"))}.");
            if (parts.Count == 0) return null;
            parts.Add("Workspaces are copies, not sandboxes: these run with your rights and can reach other folders, the network and your credentials.");
            return string.Join(" ", parts);
        }
    }

    /// <summary>The first waiting call, or null.</summary>
    public AgentPendingApproval? FirstPending => Pending.Count > 0 ? Pending[0] : null;

    /// <summary>The slots of the models a run grant still matters to: those that can run another command (pending or running, not
    /// discarded, not continued as a chat).</summary>
    public IReadOnlyList<string> GrantSlots => [.. Columns.Where(column => column.State.Phase is AgentModelPhase.Pending or AgentModelPhase.Running
        && !column.State.Discarded && column.State.ChatId is null).Select(column => column.Slot)];

    /// <summary>The models whose changes were kept (applied or kept as a branch).</summary>
    public IReadOnlyList<AgentColumnView> Kept => [.. Columns.Where(column => column.State.Kept is not null)];

    /// <summary>The line under a kept banner: which workspaces were discarded and which are kept.</summary>
    public string WorkspacesLine(int retentionDays)
    {
        var discarded = Columns.Where(column => column.State.Discarded).Select(column => column.Slot).ToList();
        var kept = Columns.Where(column => !column.State.Discarded).Select(column => column.Slot).ToList();
        var parts = new List<string>();
        if (discarded.Count > 0) parts.Add($"{(discarded.Count == 1 ? "Workspace" : "Workspaces")} {AgentText.Join(discarded)} {(discarded.Count == 1 ? "was" : "were")} discarded.");
        if (kept.Count > 0)
            parts.Add($"{(kept.Count == 1 ? "Workspace" : "Workspaces")} {AgentText.Join(kept)} {(kept.Count == 1 ? "is" : "are")} kept until you discard {(kept.Count == 1 ? "it" : "them")} or {retentionDays} days pass.");
        return string.Join(" ", parts);
    }

    /// <summary>The Details panel's run cap line.</summary>
    public string CapLine
    {
        get
        {
            if (Run.Spec.CapMicros is not { } cap) return "None: turns and time limit each model";
            var metered = Run.Spec.Models.Where(model => model.ShareMicros is not null).ToList();
            var action = Run.Spec.CapAction == CapAction.Block ? "Block" : "Ask";
            return metered.Count == 0 ? $"{SpendMoney.Format(cap)}, {action} · no metered model, so turns and time limit them"
                : $"{SpendMoney.Format(cap)}, {action} · {SpendMoney.Format(metered[0].ShareMicros!.Value)} each for the {AgentText.Count(metered.Count, "metered model")}";
        }
    }

    /// <summary>Builds the view of <paramref name="run"/> from its live data.</summary>
    /// <param name="run">The run.</param>
    /// <param name="turns">Each slot's turns so far.</param>
    /// <param name="spend">Each slot's ledger total (missing or null: nothing yet).</param>
    /// <param name="pending">The waiting tool calls.</param>
    /// <param name="workspace">The workspace modal.</param>
    /// <param name="now">Now.</param>
    /// <param name="provider">A provider's name and logo URL (null: its id, no logo).</param>
    public static AgentRunView Build(AgentCompareRun run, IReadOnlyDictionary<string, int> turns, IReadOnlyDictionary<string, SpendTotal?> spend,
        IReadOnlyList<AgentPendingApproval> pending, AgentWorkspaceView workspace, DateTimeOffset now, Func<string, (string Name, string? Logo)>? provider = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(pending);
        var columns = new List<AgentColumnView>();
        foreach (var state in run.Models)
        {
            var spec = run.ModelSpec(state.Slot);
            var approvals = pending.Where(item => item.Slot == state.Slot).Select(item => item.Approval).ToList();
            (string Name, string? Logo) about = spec is null ? ("", null) : provider?.Invoke(spec.Provider) ?? (spec.Provider, null);
            columns.Add(new AgentColumnView
            {
                RunId = run.Id,
                RunActive = run.IsActive,
                RunFailed = run.State == AgentRunState.Failed,
                State = state,
                Spec = spec,
                Turns = turns.TryGetValue(state.Slot, out var count) ? count : 0,
                MaxTurns = run.Spec.MaxTurns,
                MaxMinutes = run.Spec.MaxMinutes,
                HasCheck = run.Spec.CheckCommand is not null,
                Spend = spend.TryGetValue(state.Slot, out var total) ? total : null,
                Approvals = approvals,
                Now = now,
                LogoUrl = about.Logo,
                ProviderName = about.Name,
                Others = run.Models.Count(other => other.Slot != state.Slot && !other.Discarded && other.ChatId is null),
            });
        }
        return new AgentRunView { Run = run, Columns = columns, Pending = pending, Now = now, Workspace = workspace };
    }
}

/// <summary>The model of <c>_AgentColumn</c>: one model of a run.</summary>
public sealed record AgentColumnView
{
    /// <summary>The run id.</summary>
    public required string RunId { get; init; }

    /// <summary>Whether the run is active.</summary>
    public bool RunActive { get; init; }

    /// <summary>Whether the run failed before any model ran.</summary>
    public bool RunFailed { get; init; }

    /// <summary>The model's state.</summary>
    public required AgentCompareModelState State { get; init; }

    /// <summary>The model as chosen, or null (a run stored without it).</summary>
    public AgentCompareModelSpec? Spec { get; init; }

    /// <summary>Turns so far.</summary>
    public int Turns { get; init; }

    /// <summary>The turn limit.</summary>
    public int MaxTurns { get; init; }

    /// <summary>The time limit in minutes.</summary>
    public int MaxMinutes { get; init; }

    /// <summary>Whether the run has a check command.</summary>
    public bool HasCheck { get; init; }

    /// <summary>The ledger's total of its run key, or null (nothing yet).</summary>
    public SpendTotal? Spend { get; init; }

    /// <summary>Its waiting tool calls, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Approvals { get; init; } = [];

    /// <summary>When the view was built.</summary>
    public DateTimeOffset Now { get; init; }

    /// <summary>The provider's logo, or null.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>The provider's name.</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>How many other workspaces Keep could discard.</summary>
    public int Others { get; init; }

    /// <summary>The slot.</summary>
    public string Slot => State.Slot;

    /// <summary>The column letter.</summary>
    public string Letter => State.Slot.ToUpperInvariant();

    /// <summary>The model's name.</summary>
    public string Name => Spec?.Name ?? Spec?.Model ?? "Model " + Letter;

    /// <summary>"provider/model · thinking".</summary>
    public string Detail => Spec is null ? "" : $"{Spec.Provider}/{Spec.Model} · {(Spec.Thinking == "off" ? "thinking off" : Spec.Thinking)}";

    /// <summary>The element id part (<c>cmp_…-a</c>).</summary>
    public string Key => AgentCompareRun.ModelKey(RunId, State.Slot);

    /// <summary>What it waits on: approval, budget or null (as the status JSON says).</summary>
    public string? Waiting => State.ChatId is not null || State.Phase != AgentModelPhase.Running ? null
        : State.BudgetAsk is not null ? AgentModelWaiting.Budget
        : Approvals.Count > 0 ? AgentModelWaiting.Approval
        : null;

    /// <summary>The view state: pending, running, approval, budget, checking or done.</summary>
    public string View => State.Phase switch
    {
        AgentModelPhase.Running => Waiting ?? "running",
        AgentModelPhase.Checking => "checking",
        AgentModelPhase.Done => "done",
        _ => RunFailed ? "done" : "pending",
    };

    /// <summary>Whether it still works (Stop is offered).</summary>
    public bool Working => State.Phase is AgentModelPhase.Pending or AgentModelPhase.Running && !RunFailed;

    /// <summary>How long it ran (or runs), in ms, or null before it started.</summary>
    public long? ElapsedMs => State.StartedAt is { } started ? (long)Math.Max(0, ((State.EndedAt ?? Now) - started).TotalMilliseconds) : null;

    /// <summary>The ticking clock's start (ms since the epoch) while it runs, else null.</summary>
    public long? TickSince => State.StartedAt is { } started && State.EndedAt is null && State.Phase == AgentModelPhase.Running ? started.ToUnixTimeMilliseconds() : null;

    /// <summary>"turn 4 · " (the elapsed time follows).</summary>
    public string TurnText => Turns == 1 ? "turn 1" : $"turn {Math.Max(Turns, 1)}";

    /// <summary>The limits' tooltip.</summary>
    public string LimitsTitle => $"Limits: {MaxTurns} turns and {MaxMinutes} min";

    /// <summary>The latest tool call's line: the tool and the file (or command), and the folder apart; null when none.</summary>
    public (string Head, string? Tail, string Title)? Tool
    {
        get
        {
            if (State.LatestTool is not { } tool) return null;
            var summary = tool.Summary;
            var title = $"Latest tool call: {tool.Tool} {summary}";
            if (tool.Tool is "bash" or "powershell") return ($"{tool.Tool}: {summary}", null, title);
            var normalized = summary.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            return slash > 0 && slash < normalized.Length - 1
                ? ($"{tool.Tool} {summary[(slash + 1)..]}", "· " + summary[..slash], title)
                : ($"{tool.Tool} {summary}", null, title);
        }
    }

    /// <summary>Whether its model is billed by subscription (its list cost is not counted).</summary>
    public bool Subscription => Spec?.Billing == BillingKind.Subscription;

    /// <summary>The spend line of the run's tooltip: "$0.13 of $0.75", "≈ $0.09 list, subscription (not counted toward caps)", "free".</summary>
    public string SpendLine
    {
        get
        {
            var counted = Spend?.CountedMicros ?? 0;
            var list = Spend?.ListMicros ?? 0;
            return Spec?.Billing switch
            {
                BillingKind.Subscription => SpendMoney.FormatList(list) + ", subscription (not counted toward caps)",
                BillingKind.Free => "free",
                _ => Spec?.ShareMicros is { } share ? $"{SpendMoney.Format(counted)} of {SpendMoney.Format(share)}" : SpendMoney.Format(counted),
            };
        }
    }

    /// <summary>The foot's cost: "$0.13 of $0.75 so far", "≈ $0.09 list so far", "$0.34".</summary>
    public string CostText
    {
        get
        {
            var live = State.Phase != AgentModelPhase.Done && !RunFailed;
            var counted = Spend?.CountedMicros ?? 0;
            var list = Spend?.ListMicros ?? 0;
            var text = Spec?.Billing switch
            {
                BillingKind.Subscription => SpendMoney.FormatList(list),
                BillingKind.Free => "free",
                _ => live && Spec?.ShareMicros is { } share ? $"{SpendMoney.Format(counted)} of {SpendMoney.Format(share)}" : SpendMoney.Format(counted),
            };
            return live && Spec?.Billing != BillingKind.Free ? text + " so far" : text;
        }
    }

    /// <summary>The foot cost's tooltip.</summary>
    public string CostTitle => Spec?.Billing switch
    {
        BillingKind.Subscription => $"List price: {Spec.Provider} uses {(Spec.AccountLabel is { Length: > 0 } label ? label : "a subscription")}, so this isn't counted toward money caps.",
        BillingKind.Free => "This model costs nothing.",
        BillingKind.Metered => $"Counted by the Spend Guard{(Spec.AccountLabel is { Length: > 0 } account ? " · " + account : "")}, metered." + (Spend is { UncertainMicros: > 0 } uncertain ? $" {SpendMoney.Format(uncertain.UncertainMicros)} of it is uncertain." : ""),
        _ => "No known price: counted when the provider reports it.",
    };

    /// <summary>The check chip, or null.</summary>
    public CompareChipText? CheckChip => State.Check?.State switch
    {
        AgentCheckState.Passed => new CompareChipText("Check passed", "ok"),
        AgentCheckState.Failed => new CompareChipText("Check failed", "danger"),
        AgentCheckState.TimedOut => new CompareChipText("Check timed out", "warn", $"It ran longer than {AgentText.Duration((State.Check?.TimeoutSeconds ?? 0) * 1000L)} and was stopped."),
        AgentCheckState.Error => new CompareChipText("Check didn't run", "danger", State.Check?.Tail),
        AgentCheckState.Stopped => new CompareChipText("Check stopped", "outline", "You stopped this model while its check ran (or before it started), so the check was ended."),
        _ => null,
    };

    /// <summary>The outcome chip when it did not simply finish, or null.</summary>
    public CompareChipText? OutcomeChip => State.Outcome switch
    {
        null or AgentModelOutcome.Finished => null,
        AgentModelOutcome.Stopped => new CompareChipText("Stopped", "outline", State.Message),
        AgentModelOutcome.TurnLimit => new CompareChipText("Turn limit", "warn", State.Message),
        AgentModelOutcome.TimeLimit => new CompareChipText("Time limit", "warn", State.Message),
        AgentModelOutcome.Budget => new CompareChipText("Reached its share", "warn", State.Message),
        AgentModelOutcome.Failed => new CompareChipText("Failed", "danger", State.Message),
        _ => new CompareChipText("Error", "danger", State.Message),
    };

    /// <summary>The line after the chips: "Finished · 38 passed · exit 0".</summary>
    public string ResultLine
    {
        get
        {
            var parts = new List<string> { AgentText.OutcomeTitle(State.Outcome ?? AgentModelOutcome.Finished) };
            if (State.Check is { } check)
            {
                if (check.Summary is { Length: > 0 } summary) parts.Add(summary);
                if (check.ExitCode is { } exit) parts.Add("exit " + exit.ToString(CultureInfo.InvariantCulture));
            }
            else if (HasCheck && State.Outcome == AgentModelOutcome.Stopped) parts.Add("not checked");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The files' heading ("3 files changed"), and the delta ("+31 −9"), or null without a diff.</summary>
    public (string Heading, string Delta)? FilesHeading => State.Diff is { } diff
        ? (diff.Files == 0 ? "No files changed" : AgentText.Count(diff.Files, "file") + " changed", diff.Files == 0 ? "" : $"+{diff.Added} −{diff.Removed}")
        : null;

    /// <summary>Whether the diff can be viewed (the workspace is there, or the stored file list is).</summary>
    public bool CanViewDiff => State.WorkspacePath is not null && (!State.Discarded || State.Diff is not null);

    /// <summary>Whether Keep is offered (done, workspace there, something changed, not kept yet).</summary>
    public bool CanKeep => State.Phase == AgentModelPhase.Done && !State.Discarded && State.WorkspacePath is not null && State.Kept is null && (State.Diff?.Files ?? 1) > 0;

    /// <summary>Whether Open as chat is offered (done, started, workspace there).</summary>
    public bool CanOpenChat => State.Phase == AgentModelPhase.Done && State.SessionId is not null && !State.Discarded;

    /// <summary>The chat it continues in, or null.</summary>
    public string? ChatUrl => State.ChatId is { } chat ? "/chat/" + Uri.EscapeDataString(chat) : null;

    /// <summary>Whether its workspace can be discarded now (done, there, not a chat's).</summary>
    public bool CanDiscard => (State.Phase == AgentModelPhase.Done || RunFailed) && !State.Discarded && State.WorkspacePath is not null && State.ChatId is null;

    /// <summary>The budget Ask's sentence, or null.</summary>
    public string? BudgetText => State.BudgetAsk is { } ask
        ? $"Its next turn could cost up to {SpendMoney.Format(ask.NextMicros)}, which would take it over its {SpendMoney.Format(ask.ShareMicros)} share of the run cap ({SpendMoney.Format(ask.SpentMicros)} spent)."
        : null;

    /// <summary>A file's delta in the files list: "+10 −7", "new · +15", "deleted · −4", "binary".</summary>
    public static string Delta(AgentDiffFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Delta(file.Change, file.Added, file.Removed, file.Binary);
    }

    /// <summary>A file's delta in the diff modal.</summary>
    public static string Delta(WorkspaceFileDiff file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Delta(file.Change, file.Added, file.Removed, file.Binary);
    }

    private static string Delta(WorkspaceFileChange change, int added, int removed, bool binary) => (change, binary) switch
    {
        (WorkspaceFileChange.Added, true) => "new · binary",
        (WorkspaceFileChange.Deleted, true) => "deleted · binary",
        (_, true) => "binary",
        (WorkspaceFileChange.Added, _) => $"new · +{added}",
        (WorkspaceFileChange.Deleted, _) => $"deleted · −{removed}",
        _ => $"+{added} −{removed}",
    };

    /// <summary>A waiting call's text: its command, or its path, or its arguments.</summary>
    public static string CallText(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command is { Length: > 0 } command) return command;
        foreach (var name in new[] { "path", "file_path", "file" })
            if (request.Arguments[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var path) && path.Length > 0) return path;
        var json = request.Arguments.ToJsonString();
        return json.Length > 2000 ? json[..1999] + "…" : json;
    }
}

/// <summary>The model of <c>_AgentDiff</c> (modal-agent-diff's content): one model's changes.</summary>
public sealed record AgentDiffView
{
    /// <summary>The most diff lines the modal renders; the rest is in the patch (Copy patch).</summary>
    public const int MaxLines = 5000;

    /// <summary>The run.</summary>
    public required AgentCompareRun Run { get; init; }

    /// <summary>The column.</summary>
    public required AgentColumnView Column { get; init; }

    /// <summary>R4's diff, or null (discarded, or not available: <see cref="Error"/>).</summary>
    public WorkspaceDiff? Diff { get; init; }

    /// <summary>Why the diff is not shown, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The slots the switch offers.</summary>
    public IReadOnlyList<string> Slots => [.. Run.Models.Select(model => model.Slot)];

    /// <summary>What the diff is against.</summary>
    public string BaseLabel => Diff?.BaseLabel ?? Run.Workspace?.BaseLabel ?? "the base";

    /// <summary>The subtitle: "3 files · +31 −9 · against main @ 4f9c2e1 · workspace b".</summary>
    public string Subtitle
    {
        get
        {
            var summary = Diff?.Summary ?? Column.State.Diff?.Summary;
            var parts = new List<string>();
            if (summary is not null) parts.Add(summary);
            parts.Add("against " + BaseLabel);
            var workspace = "workspace " + Column.Slot;
            if (Column.State.Discarded) workspace += " (discarded)";
            else if (Column.State.Kept?.Outcome == KeepOutcome.Applied) workspace += " · applied to your project";
            else if (Column.State.Kept?.Outcome == KeepOutcome.BranchCreated) workspace += " · kept as branch " + Column.State.Kept.Branch;
            else if (Column.State.Phase != AgentModelPhase.Done) workspace += " · still running: the changes so far";
            parts.Add(workspace);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>The model of <c>_AgentKeep</c> (modal-keep-result's content): how to keep one model's changes.</summary>
public sealed record AgentKeepView
{
    /// <summary>The run.</summary>
    public required AgentCompareRun Run { get; init; }

    /// <summary>The column.</summary>
    public required AgentColumnView Column { get; init; }

    /// <summary>R4's preview, or null (<see cref="Error"/>).</summary>
    public KeepPreview? Preview { get; init; }

    /// <summary>Why Keep can't be offered, or null.</summary>
    public string? Error { get; init; }

    /// <summary>How many days a workspace is kept.</summary>
    public int RetentionDays { get; init; } = 7;

    /// <summary>The branch Keep the branch makes.</summary>
    public string Branch => Preview?.Branch ?? "pisharp/compare/" + Column.Key;

    /// <summary>The other workspaces Discard the others would remove (not discarded; a model continued as a chat keeps its own).</summary>
    public IReadOnlyList<string> Others => [.. Run.Models.Where(model => model.Slot != Column.Slot && !model.Discarded && model.ChatId is null).Select(model => model.Slot)];

    /// <summary>Whether the others are all done (Keep refuses to discard a running model's workspace).</summary>
    public bool OthersDone => Run.Models.Where(model => model.Slot != Column.Slot).All(model => model.Phase == AgentModelPhase.Done);

    /// <summary>The pane shown first: <c>conflict</c> when the project has uncommitted changes in the same files, else <c>options</c>.</summary>
    public string Pane => Preview is { Conflicts.Count: > 0 } ? "conflict" : "options";

    /// <summary>The subtitle: "Claude Sonnet 5.5 · 3 files · +31 −9 · check passed".</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { Column.Name };
            if (Column.State.Diff is { } diff) parts.Add(diff.Summary);
            if (Column.State.Check is { } check) parts.Add("check " + AgentText.CheckWord(check.State));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Why Apply is not offered, or null.</summary>
    public string? ApplyBlocked => Preview switch
    {
        null => Error,
        { Files.Count: 0 } => "There is nothing to apply: this model changed no files.",
        { Conflicts.Count: > 0 } => "Your project has uncommitted changes in files this model also changed.",
        _ => null,
    };

    /// <summary>Why Keep the branch is not offered, or null.</summary>
    public string? BranchBlocked => Preview switch
    {
        null => Error,
        { Kind: WorkspaceKind.Copy } => "A branch needs a git worktree; this run used temporary copies.",
        { Files.Count: 0 } => "There is nothing to commit: this model changed no files.",
        _ => null,
    };
}

/// <summary>Words the views share.</summary>
public static class AgentText
{
    /// <summary>"1 file", "3 files".</summary>
    public static string Count(int count, string noun) => count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : noun + "s");

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>"45s", "1m 30s", "1h 05m".</summary>
    public static string Duration(long ms)
    {
        var seconds = Math.Max(0, ms / 1000);
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + "s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60:00}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
    }

    /// <summary>"today 09:12 UTC", or "2026-10-29 09:12 UTC".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now) =>
        (at.UtcDateTime.Date == now.UtcDateTime.Date ? "today " : at.UtcDateTime.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture))
        + at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>The approval mode's name: Ask, Allow-list, Auto in workspaces.</summary>
    public static string Approval(ToolApprovalMode mode) => mode switch
    {
        ToolApprovalMode.AllowList => "Allow-list",
        ToolApprovalMode.Auto => "Auto in workspaces",
        _ => "Ask",
    };

    /// <summary>The wire word of an approval mode: ask, allowList, auto.</summary>
    public static string ApprovalValue(ToolApprovalMode mode) => mode switch
    {
        ToolApprovalMode.AllowList => "allowList",
        ToolApprovalMode.Auto => "auto",
        _ => "ask",
    };

    /// <summary>The wire word of an isolation: automatic, worktree, copy.</summary>
    public static string IsolationValue(WorkspaceIsolation isolation) => isolation switch
    {
        WorkspaceIsolation.Worktree => "worktree",
        WorkspaceIsolation.Copy => "copy",
        _ => "automatic",
    };

    /// <summary>"stopped", "at the turn limit" (after a count: "1 stopped").</summary>
    public static string OutcomeWord(string outcome) => outcome switch
    {
        AgentModelOutcome.Stopped => "stopped",
        AgentModelOutcome.TurnLimit => "at the turn limit",
        AgentModelOutcome.TimeLimit => "at the time limit",
        AgentModelOutcome.Budget => "at their share of the cap",
        AgentModelOutcome.Failed => "failed",
        _ => "with an error",
    };

    /// <summary>"Finished", "Stopped", "Turn limit" (the result line's first word).</summary>
    public static string OutcomeTitle(string outcome) => outcome switch
    {
        AgentModelOutcome.Finished => "Finished",
        AgentModelOutcome.Stopped => "Stopped",
        AgentModelOutcome.TurnLimit => "Stopped at the turn limit",
        AgentModelOutcome.TimeLimit => "Stopped at the time limit",
        AgentModelOutcome.Budget => "Stopped at its share of the cap",
        AgentModelOutcome.Failed => "Failed",
        _ => "Error",
    };

    /// <summary>"passed", "failed", "timed out", "stopped", "didn't run".</summary>
    public static string CheckWord(string state) => state switch
    {
        AgentCheckState.Passed => "passed",
        AgentCheckState.Failed => "failed",
        AgentCheckState.TimedOut => "timed out",
        AgentCheckState.Stopped => "stopped",
        _ => "didn't run",
    };

    /// <summary>A git status letter as a word.</summary>
    public static string Status(string status) => status.Trim() switch
    {
        "M" or "MM" or "AM" or "RM" => "modified",
        "A" => "added",
        "D" => "deleted",
        "R" => "renamed",
        "??" => "untracked",
        "U" or "UU" or "AA" or "DD" => "conflicted",
        var other => other.Length == 0 ? "changed" : other.Contains('D', StringComparison.Ordinal) ? "deleted" : "modified",
    };
}
