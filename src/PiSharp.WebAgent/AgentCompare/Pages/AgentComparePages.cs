// Decision 0011 (run W4, R6): what agent compare's views show (GET /compare?mode=agent, and the fragments of AgentComparePagesModule). A new
// run opens on the user's own models (the default model of Settings › Defaults and the next model the catalog lists that is not hidden),
// the Settings › Tools working directory and the Settings › Tools agent compare defaults; New run (?from={run}) copies an earlier run's
// task, folder, models, check and limits. ?run={id} opens a run: the stored run (R5's AgentCompareStore) with what is live (each model's
// turns, the Spend Guard ledger's total of its run key "<run>-<slot>", the tool calls waiting for an answer). The project folder is
// inspected by R4's WorkspaceManager on its own request, after the page loads, because a copy's size can take a while to count. Every
// write goes through R5's /api/compare/agent; nothing here starts, answers or keeps anything, and no provider is called.
using PiSharp.Web.Common;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Shell;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Pages;

/// <summary>Builds agent compare's views. A singleton.</summary>
public sealed class AgentComparePages(IServiceProvider services, ILogger<AgentComparePages>? log = null)
{
    /// <summary>A run (the page's run view, and its live fragment).</summary>
    public const string RunPartial = "/Views/Compare/_AgentRun.cshtml";

    /// <summary>One column of the setup.</summary>
    public const string SetupColumnPartial = "/Views/Compare/_AgentSetupColumn.cshtml";

    /// <summary>The setup's project row and its workspace modal content.</summary>
    public const string ProjectPartial = "/Views/Compare/_AgentProject.cshtml";

    /// <summary>The workspace modal's content.</summary>
    public const string WorkspacePartial = "/Views/Compare/_AgentWorkspace.cshtml";

    /// <summary>The diff modal's content.</summary>
    public const string DiffPartial = "/Views/Compare/_AgentDiff.cshtml";

    /// <summary>The keep modal's content.</summary>
    public const string KeepPartial = "/Views/Compare/_AgentKeep.cshtml";

    /// <summary>How many recent project folders the setup offers.</summary>
    public const int RecentProjects = 6;

    private AgentCompareRunner? Runner => services.GetService<AgentCompareRunner>();

    private IModelCatalog? Catalog => services.GetService<IModelCatalog>();

    private ISettingsStore? Settings => services.GetService<ISettingsStore>();

    private WorkspaceManager? Workspaces => services.GetService<WorkspaceManager>();

    /// <summary>What <c>_AgentMode</c> shows for <c>?run=</c> (a run) or <c>?from=</c> (a setup copied from a run), else a new setup.</summary>
    public async Task<AgentModeView> ModeAsync(string? runId, string? fromId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(runId))
        {
            var id = runId.Trim();
            if (await RunAsync(id, cancellationToken).ConfigureAwait(false) is { } run) return new AgentModeView { Run = run };
            var notice = $"There is no agent compare run {Cut(id)}; it may have been deleted. A new run opened instead.";
            return new AgentModeView { Setup = await SetupAsync(null, cancellationToken).ConfigureAwait(false) with { Notice = notice }, Notice = notice };
        }
        return new AgentModeView { Setup = await SetupAsync(string.IsNullOrWhiteSpace(fromId) ? null : fromId.Trim(), cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>A new run's setup, copied from run <paramref name="fromId"/> when it exists.</summary>
    public async Task<AgentSetupView> SetupAsync(string? fromId, CancellationToken cancellationToken = default)
    {
        var defaults = Settings?.Get<AgentCompareSettings>() ?? new AgentCompareSettings();
        var tools = Settings?.Get<ToolsSettings>() ?? ToolsSettings.CreateDefault();
        var from = fromId is null ? null : Runner?.Store.Get(fromId);
        var shared = SharedThinking();
        var maxTurns = from?.Spec.MaxTurns ?? defaults.MaxTurns;
        var maxMinutes = from?.Spec.MaxMinutes ?? defaults.MaxMinutes;
        var picks = from is not null
            ? from.Spec.Models.Select(model => (model.Provider, model.Model, (string?)model.Thinking)).ToList()
            : DefaultModels().Select(model => (model.Provider, model.Model, (string?)null)).ToList();
        var columns = new List<AgentSetupColumnView>();
        for (var index = 0; index < picks.Count && index < AgentCompareRunner.MaxModels; index++)
        {
            var (provider, model, thinking) = picks[index];
            var column = await SetupColumnAsync(AgentCompareRunner.Slots[index], provider, model, null, shared, maxTurns, maxMinutes, picks.Count > 1, cancellationToken)
                .ConfigureAwait(false);
            // An earlier run's level stays an override only where it differs from what the shared level gives this model.
            if (thinking is not null && column.Known && thinking != column.Effective && column.Levels.Contains(thinking)) column = column with { Thinking = thinking };
            columns.Add(column);
        }
        var first = columns.FirstOrDefault();
        return new AgentSetupView
        {
            Task = from?.Spec.Task ?? "",
            ProjectPath = from?.Spec.ProjectPath ?? tools.EffectiveWorkingDirectory,
            RecentProjects = Recent(tools.EffectiveWorkingDirectory),
            Defaults = defaults,
            Isolation = from?.Spec.Isolation ?? defaults.Isolation,
            BaseRef = from?.Spec.BaseRef,
            ExtraExcludes = from is null ? [] : [.. from.Spec.ExtraExcludes.Where(item => !(defaults.ExtraExcludes ?? []).Contains(item, StringComparer.OrdinalIgnoreCase))],
            CheckCommand = from?.Spec.CheckCommand ?? "",
            MaxTurns = maxTurns,
            MaxMinutes = maxMinutes,
            Cap = from?.Spec.CapMicros is { } cap ? SpendMoney.ToUsd(cap).ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture) : "",
            CapAction = from?.Spec.CapAction ?? CapAction.Ask,
            Approval = from?.Spec.Approval ?? defaults.Approval,
            Thinking = shared,
            Columns = columns,
            Picker = new ModelPickerView(new ProviderModelSelect("mp") { Provider = first?.Provider, Model = first?.Model })
            {
                Note = "The column keeps its thinking override; one its new model does not take is kept, not sent, until you switch back.",
                Submit = "Use model",
            },
            FromRun = from?.Id,
            AllowRules = tools.AllowList?.Count ?? 0,
        };
    }

    /// <summary>A setup column: <paramref name="provider"/>/<paramref name="model"/> in <paramref name="slot"/>.</summary>
    /// <param name="slot">a to d.</param>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model id.</param>
    /// <param name="thinking">The column's override, or null.</param>
    /// <param name="sharedThinking">The shared level.</param>
    /// <param name="maxTurns">Turns per model.</param>
    /// <param name="maxMinutes">Minutes per model.</param>
    /// <param name="canRemove">Whether Remove is offered.</param>
    /// <param name="cancellationToken">Cancels the account lookup.</param>
    public async Task<AgentSetupColumnView> SetupColumnAsync(string slot, string provider, string model, string? thinking, string? sharedThinking, int maxTurns,
        int maxMinutes, bool canRemove, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        var catalog = Catalog;
        var entry = catalog?.FindProvider(provider.Trim());
        var found = entry is null ? null : catalog!.FindModel(entry.Id, model.Trim());
        var levels = found is null ? ["off"] : ChatDisplay.HostThinkingLevels(found.ThinkingLevels);
        var shared = sharedThinking is { } level && AgentSetupColumnView.HostLevels.Contains(level) ? level : SharedThinking();
        string? kept = null;
        if (string.IsNullOrWhiteSpace(thinking)) thinking = null;
        else if (!levels.Contains(thinking.Trim()))
        {
            kept = thinking.Trim();
            thinking = null;
        }
        else thinking = thinking.Trim();
        string? account = null;
        if (entry is not null)
        {
            try { account = (await (services.GetService<IChatAccounts>() ?? NoChatAccounts.Instance).BindAsync(entry.Id, cancellationToken).ConfigureAwait(false))?.Label; }
            catch (Exception error) when (error is not OperationCanceledException) { log?.LogDebug(error, "The account of {Provider} could not be read.", entry.Id); }
        }
        return new AgentSetupColumnView
        {
            Slot = slot,
            Provider = entry?.Id ?? provider.Trim(),
            Model = found?.Id ?? model.Trim(),
            Name = found?.Name ?? model.Trim(),
            ProviderName = entry?.DisplayName ?? provider.Trim(),
            Known = found is not null,
            Levels = levels,
            Thinking = thinking,
            KeptThinking = kept,
            SharedThinking = shared,
            Billing = entry?.Billing ?? BillingKind.Unknown,
            AccountLabel = account,
            MaxTurns = maxTurns,
            MaxMinutes = maxMinutes,
            CanRemove = canRemove,
        };
    }

    /// <summary>The setup's project row for <paramref name="path"/>: R4's inspection with this run's isolation and excludes.</summary>
    public async Task<AgentProjectView> ProjectAsync(string? path, string? isolation, string? baseRef, IReadOnlyList<string>? excludes, int models,
        CancellationToken cancellationToken = default)
    {
        var defaults = Settings?.Get<AgentCompareSettings>() ?? new AgentCompareSettings();
        var folder = path?.Trim() ?? "";
        var slots = AgentCompareRunner.Slots.Take(Math.Clamp(models, 1, AgentCompareRunner.MaxModels)).ToList();
        var requested = ParseIsolation(isolation) ?? defaults.Isolation;
        var settingsExcludes = defaults.ExtraExcludes ?? [];
        AgentWorkspaceView Empty(string? note) => new()
        {
            ProjectPath = folder,
            Isolation = requested,
            SettingsExcludes = settingsExcludes,
            Directory = Workspaces?.Options.Root ?? WorkspaceManagerOptions.FolderName,
            Slots = slots,
            WarnAboveBytes = defaults.CopyWarnBytes,
            RetentionDays = Retention(),
            Note = note,
        };
        if (folder.Length == 0) return new AgentProjectView { ProjectPath = folder, Error = "Choose the project folder.", Workspace = Empty(null) };
        if (!Path.IsPathFullyQualified(folder)) return new AgentProjectView { ProjectPath = folder, Error = "The project folder must be a full path.", Workspace = Empty(null) };
        if (!Directory.Exists(folder)) return new AgentProjectView { ProjectPath = folder, Error = $"There is no folder {folder}.", Workspace = Empty(null) };
        IReadOnlyList<string> extra;
        try { extra = AgentCompareSettings.NormalizeExcludes(excludes); }
        catch (ArgumentException error) { return new AgentProjectView { ProjectPath = folder, Error = error.Message, Workspace = Empty(null) }; }
        if (Workspaces is not { } workspaces) return new AgentProjectView { ProjectPath = folder, Error = "Workspaces are not available in this app.", Workspace = Empty(null) };
        ProjectInspection inspection;
        try
        {
            inspection = await workspaces.InspectAsync(folder, new WorkspaceInspectOptions
            {
                Isolation = requested,
                ExtraExcludes = AgentCompareSettings.NormalizeExcludes([.. settingsExcludes, .. extra]),
                Slots = slots.Count,
                WarnAboveBytes = defaults.CopyWarnBytes,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException error)
        {
            return new AgentProjectView { ProjectPath = folder, Error = error.Message, Workspace = Empty(null) };
        }
        return new AgentProjectView
        {
            ProjectPath = inspection.ProjectPath,
            Inspection = inspection,
            Workspace = new AgentWorkspaceView
            {
                ProjectPath = inspection.ProjectPath,
                Kind = inspection.Kind,
                Isolation = requested,
                WorktreeUnavailable = inspection.WorktreeUnavailable,
                Branch = inspection.CurrentBranch,
                ShortCommit = inspection.ShortCommit,
                Branches = inspection.Branches,
                BaseRef = string.IsNullOrWhiteSpace(baseRef) ? null : baseRef.Trim(),
                Uncommitted = inspection.Uncommitted,
                CheckoutBytes = inspection.CheckoutBytes,
                CopyBytes = inspection.CopyBytes,
                CopyComplete = inspection.CopyEstimateComplete,
                SettingsExcludes = settingsExcludes,
                ExtraExcludes = extra,
                SkippedLinks = inspection.SkippedLinks,
                WarnAboveBytes = inspection.WarnAboveBytes,
                Directory = inspection.WorkspacesRoot,
                Slots = slots,
                RetentionDays = Retention(),
            },
        };
    }

    /// <summary>Run <paramref name="id"/> with what is live, or null when there is no such run.</summary>
    public async Task<AgentRunView?> RunAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (Runner is not { } runner || runner.Store.Get(id.Trim()) is not { } run) return null;
        var ledger = services.GetService<ISpendLedger>();
        var turns = new Dictionary<string, int>(StringComparer.Ordinal);
        var spend = new Dictionary<string, SpendTotal?>(StringComparer.Ordinal);
        foreach (var model in run.Models)
        {
            var key = AgentCompareRun.ModelKey(run.Id, model.Slot);
            turns[model.Slot] = runner.Store.Turns(key);
            if (ledger is null || model.SessionId is null) continue;
            try { spend[model.Slot] = await ledger.TotalAsync(new SpendLedgerQuery { Run = key }, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { log?.LogDebug(error, "The spend of {Key} could not be read.", key); }
        }
        return AgentRunView.Build(run, turns, spend, runner.Pending(run), RunWorkspace(run), DateTimeOffset.UtcNow, Provider);
    }

    /// <summary>The read-only workspace modal of <paramref name="run"/>.</summary>
    public AgentWorkspaceView RunWorkspace(AgentCompareRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var info = run.Workspace;
        return new AgentWorkspaceView
        {
            ReadOnly = true,
            Run = run.Id,
            ProjectPath = run.Spec.ProjectPath,
            Kind = info?.Kind,
            Isolation = run.Spec.Isolation,
            Branch = info?.BaseBranch,
            ShortCommit = info?.ShortCommit,
            Branches = info?.BaseBranch is { } branch ? [branch] : [],
            BaseRef = run.Spec.BaseRef,
            Uncommitted = info?.NotCopied ?? [],
            DefaultExcludes = info is { Excludes.Count: > 0 } ? info.Excludes : WorkspaceManagerOptions.DefaultExcludes,
            ExtraExcludes = info is { Excludes.Count: > 0 } ? [] : run.Spec.ExtraExcludes,
            SkippedLinks = info?.SkippedLinks ?? [],
            WarnAboveBytes = run.Spec.WarnAboveBytes,
            Directory = info?.Directory ?? Path.Join(Workspaces?.Options.Root ?? WorkspaceManagerOptions.FolderName, run.Id),
            Slots = [.. run.Models.Select(model => model.Slot)],
            RetentionDays = Retention(),
            Note = info is not null ? null
                : run.State == AgentRunState.Failed ? "The workspaces were not made: " + (run.Error ?? "the run failed before they were.")
                : "The workspaces are being made.",
        };
    }

    /// <summary>The diff modal of <paramref name="slot"/> in run <paramref name="id"/>, or null when there is no such run or model.</summary>
    public async Task<AgentDiffView?> DiffAsync(string id, string slot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (await RunAsync(id, cancellationToken).ConfigureAwait(false) is not { } view) return null;
        if (view.Columns.FirstOrDefault(column => column.Slot == slot.Trim().ToLowerInvariant()) is not { } column) return null;
        WorkspaceDiff? diff = null;
        string? error = null;
        if (column.State.Discarded) error = "This workspace was discarded, so its line-by-line diff is gone. The files it changed are kept with the run.";
        else if (column.State.WorkspacePath is null) error = view.Run.State == AgentRunState.Failed ? "This model never ran: its workspace was not made." : "Its workspace is still being made.";
        else
        {
            try { diff = await Runner!.DiffAsync(view.Id, column.Slot, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is AgentCompareException or WorkspaceException) { error = failure.Message; }
        }
        return new AgentDiffView { Run = view.Run, Column = column, Diff = diff, Error = error };
    }

    /// <summary>The keep modal of <paramref name="slot"/> in run <paramref name="id"/>, or null when there is no such run or model.</summary>
    public async Task<AgentKeepView?> KeepAsync(string id, string slot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (await RunAsync(id, cancellationToken).ConfigureAwait(false) is not { } view) return null;
        if (view.Columns.FirstOrDefault(column => column.Slot == slot.Trim().ToLowerInvariant()) is not { } column) return null;
        KeepPreview? preview = null;
        string? error = null;
        if (column.State.Discarded) error = "This workspace was discarded, so there is nothing to keep.";
        else if (column.State.Phase != AgentModelPhase.Done) error = "Wait until this model is done to keep its changes.";
        else
        {
            try { preview = await Runner!.PreviewKeepAsync(view.Id, column.Slot, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is AgentCompareException or WorkspaceException) { error = failure.Message; }
        }
        return new AgentKeepView { Run = view.Run, Column = column, Preview = preview, Error = error, RetentionDays = Retention() };
    }

    /// <summary><c>automatic</c> (or <c>auto</c>), <c>worktree</c> or <c>copy</c>; null for anything else.</summary>
    public static WorkspaceIsolation? ParseIsolation(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "automatic" or "auto" => WorkspaceIsolation.Automatic,
        "worktree" => WorkspaceIsolation.Worktree,
        "copy" => WorkspaceIsolation.Copy,
        _ => null,
    };

    // The shared thinking level a new run starts on: Settings › Defaults', as the host takes it (max is not one of its levels yet).
    private string SharedThinking()
    {
        var level = Settings?.Get<DefaultsSettings>().Thinking;
        if (level is null) return "medium";
        return AgentSetupColumnView.HostLevels.Contains(level) ? level : level == "max" ? "xhigh" : "medium";
    }

    // The default model (as a new chat's) and the next catalog model that is not hidden: the user's own models, never sample data.
    private List<(string Provider, string Model)> DefaultModels()
    {
        var models = new List<(string Provider, string Model)>();
        var catalog = Catalog;
        var hidden = Settings?.Get<ModelsSettings>() ?? ModelsSettings.CreateDefault();
        try
        {
            if (services.GetService<ChatService>()?.NewChat() is { Provider: { } provider, Model: { } model } && catalog?.FindModel(provider, model) is not null)
                models.Add((provider, model));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.LogDebug(error, "The default model could not be read for agent compare.");
        }
        if (catalog is null) return models;
        foreach (var model in catalog.Providers.SelectMany(provider => catalog.Models(provider.Id)))
        {
            if (models.Count >= 2) break;
            if (hidden.IsHidden(model.Provider, model.Id) || models.Contains((model.Provider, model.Id))) continue;
            models.Add((model.Provider, model.Id));
        }
        return models;
    }

    private List<AgentRecentProject> Recent(string workingDirectory)
    {
        var recent = new List<AgentRecentProject> { new(workingDirectory, "working directory") };
        if (Runner is not { } runner) return recent;
        foreach (var run in runner.Store.List(50))
        {
            if (recent.Count >= RecentProjects) break;
            if (recent.Any(item => string.Equals(item.Path, run.Spec.ProjectPath, StringComparison.OrdinalIgnoreCase))) continue;
            recent.Add(new AgentRecentProject(run.Spec.ProjectPath, run.Workspace?.Kind switch
            {
                WorkspaceKind.Worktree => "git worktree",
                WorkspaceKind.Copy => "temporary copy",
                _ => "recent run",
            }));
        }
        return recent;
    }

    private (string Name, string? Logo) Provider(string id)
    {
        var entry = Catalog?.FindProvider(id);
        return (entry?.DisplayName ?? id, entry?.LogoKey is { } key ? ProviderLogos.KeyUrl(key) : ProviderLogos.Url(id));
    }

    private int Retention() => Math.Max(1, (int)Math.Round((Workspaces?.Options.Retention ?? TimeSpan.FromDays(7)).TotalDays));

    private static string Cut(string text) => text.Length > 80 ? text[..79] + "…" : text;
}
