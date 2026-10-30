// Decision 0011 (run W4, R2): what the Compare page (GET /compare) and its endpoints show and do. A new comparison opens on two columns: the
// default model (Settings › Defaults, as a new chat's) and the next model the catalog lists that is not hidden, so the page starts on the
// user's own models, never sample data. ?group={id} reopens a group: its shared request, its columns with their overrides and runs (live
// while they run, so a closed tab reconnects) and the mark. Run all plans every column (ComparePlanner), then starts them through the run
// engine as one group (RunEngine.StartGroupAsync: the Spend Guard reserves the columns all or none; a refusal answers {posted: false} with
// the confirm modal's content, and the user's choice comes back as a one-time approval of the group), and stores the group. A re-run sends
// one column again into its group (its own reservation, recorded under the group's run). The estimate is the Spend Guard's preflight of
// each column (CompareEstimate). Accounts are resolved as the run engine resolves them and named by their labels only.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Shell;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Compare;

/// <summary>What a Run all or a re-run started.</summary>
/// <param name="Start">The run engine's answer.</param>
/// <param name="Group">The stored group, when something started.</param>
public sealed record CompareStart(RunStart Start, CompareGroup? Group);

/// <summary>Builds the Compare page and runs its groups. A singleton.</summary>
public sealed class ComparePages(IServiceProvider services, ILogger<ComparePages>? log = null)
{
    /// <summary>The partial of one column.</summary>
    public const string ColumnPartial = "/Views/Compare/_CompareColumn.cshtml";

    /// <summary>The partial of the Metrics tab.</summary>
    public const string MetricsPartial = "/Views/Compare/_CompareMetrics.cshtml";

    private RunEngine Engine => services.GetRequiredService<RunEngine>();

    private CompareStore Store => services.GetRequiredService<CompareStore>();

    private IModelCatalog? Catalog => services.GetService<IModelCatalog>();

    private PlaygroundPages Playground => services.GetService<PlaygroundPages>() ?? new PlaygroundPages(services);

    /// <summary>The page for <c>?group=</c> (else a new comparison), in <paramref name="mode"/> (request, or agent for R6's part).</summary>
    public async Task<ComparePage> PageAsync(string? groupId, string? mode, CancellationToken cancellationToken = default)
    {
        var agent = string.Equals(mode?.Trim(), ComparePage.AgentModeName, StringComparison.OrdinalIgnoreCase);
        string? notice = null;
        CompareGroup? group = null;
        if (!agent && !string.IsNullOrWhiteSpace(groupId))
        {
            group = Group(groupId.Trim());
            if (group is null) notice = $"There is no Compare run {groupId.Trim()}; it may have been deleted. A new comparison opened instead.";
        }
        var draft = group?.Draft ?? new CompareDraft { Columns = Defaults() };
        var columns = group is null
            ? [.. draft.Columns.Select((column, index) => DraftColumn(draft.Shared, column, "c" + (index + 1), draft.Columns.Count))]
            : Columns(group);
        var plan = ComparePlanner.Plan(draft, Engine);
        var estimate = agent ? RunEstimateView.None : await EstimateAsync(plan, cancellationToken).ConfigureAwait(false);
        var first = draft.Columns.FirstOrDefault();
        return new ComparePage
        {
            Mode = agent ? ComparePage.AgentModeName : ComparePage.RequestMode,
            Draft = draft,
            GroupId = group?.Id,
            Columns = columns,
            Metrics = new CompareMetricsView { GroupId = group?.Id, Columns = columns },
            Estimate = estimate,
            Message = string.IsNullOrWhiteSpace(draft.Prompt) ? null : plan.Message,
            Notice = notice,
            Picker = new ModelPickerView(new ProviderModelSelect("mp") { Provider = first?.Provider, Model = first?.Model })
            {
                Note = "The column keeps its overrides; one its new model does not take is kept, not sent, until you switch back.",
                Submit = "Use model",
            },
        };
    }

    /// <summary>A column that has not run: <paramref name="column"/> under <paramref name="shared"/>.</summary>
    /// <param name="shared">The shared request.</param>
    /// <param name="column">The model and overrides.</param>
    /// <param name="key">The element id part.</param>
    /// <param name="count">How many columns the page has (Remove is offered above the fewest).</param>
    /// <param name="groupId">The group the page shows, when it has run (Re-run then sends this column into it), else null.</param>
    public CompareColumnView DraftColumn(CompareDraft shared, CompareColumnDraft column, string key, int count, string? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(column);
        column = column.Trimmed();
        var capabilities = column.Provider.Length > 0 && column.Model.Length > 0 ? Engine.Capabilities(column.Provider, column.Model) : null;
        // An override the new model does not list is kept on the page, not sent (rule 4: a disabled option keeps its draft value).
        string? kept = null;
        if (column.Thinking is { } own && capabilities is not null && !capabilities.ThinkingLevels.Contains(own))
        {
            kept = own;
            column = column with { Thinking = null };
        }
        var plan = ComparePlanner.Column(shared with { Columns = [] }, column, 0, Engine);
        return View(key, column, shared, capabilities, plan.Chips, groupId, null, false, false, count > CompareDraft.MinColumns) with { KeptThinking = kept };
    }

    /// <summary>Column <paramref name="runId"/> of group <paramref name="groupId"/>, or null when there is no such group or column.</summary>
    public CompareColumnView? GroupColumn(string groupId, string runId, string key)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(runId);
        if (Group(groupId) is not { } group) return null;
        var columns = Columns(group, key: (runId, key));
        return columns.FirstOrDefault(column => column.Run?.Id == runId);
    }

    /// <summary>The Metrics tab of group <paramref name="groupId"/>, or null when there is no such group.</summary>
    public CompareMetricsView? Metrics(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        return Group(groupId) is { } group ? new CompareMetricsView { GroupId = group.Id, Columns = Columns(group) } : null;
    }

    /// <summary>The plan answer of <paramref name="draft"/> (POST /api/compare/plan): whether Run all can send it, each column's chips and
    /// refusal, and the estimate by Run all with its markup. Nothing is reserved or sent.</summary>
    public async Task<JsonObject> PlanJsonAsync(CompareDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var plan = ComparePlanner.Plan(draft, Engine);
        var estimate = await EstimateAsync(plan, cancellationToken).ConfigureAwait(false);
        var html = "";
        if (estimate.Shown && services.GetService<FragmentRenderer>() is { } renderer)
            html = await renderer.RenderAsync(PlaygroundModule.EstimatePartial, estimate).ConfigureAwait(false);
        return new JsonObject
        {
            ["valid"] = plan.Valid,
            ["message"] = plan.Message,
            ["columns"] = new JsonArray([.. plan.Columns.Select(column => (JsonNode)new JsonObject
            {
                ["index"] = column.Index,
                ["provider"] = column.Column.Provider,
                ["model"] = column.Column.Model,
                ["ok"] = column.Ok,
                ["error"] = column.Error,
                ["thinking"] = column.Thinking,
                ["temperature"] = column.Spec?.Temperature,
                ["maxTokens"] = column.Spec?.MaxTokens,
                ["chips"] = JsonSerializer.SerializeToNode(column.Chips, SettingsStore.JsonOptions),
                ["refused"] = JsonSerializer.SerializeToNode(column.Refused, SettingsStore.JsonOptions),
            })]),
            ["estimate"] = new JsonObject
            {
                ["html"] = html,
                ["verdict"] = estimate.Verdict,
                ["chip"] = estimate.Chip,
                ["over"] = estimate.Over,
                ["pane"] = estimate.Pane,
            },
        };
    }

    /// <summary>Run all: every column of <paramref name="draft"/> as one group, reserved all or none.</summary>
    /// <param name="draft">The shared request and 2 to 4 columns.</param>
    /// <param name="groupId">The group id to use (the one a refusal answered, when the user's choice comes back), or null for a new one.</param>
    /// <param name="spend">The user's answer to the Spend Guard, or null.</param>
    /// <param name="cancellationToken">Cancels the start (not the runs).</param>
    /// <exception cref="CompareRefusedException">A column cannot run (an override its model does not take, an unknown model), or the draft
    /// is not complete; nothing was reserved or sent.</exception>
    public async Task<CompareStart> RunAsync(CompareDraft draft, string? groupId = null, RunSpendAnswer? spend = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var plan = ComparePlanner.Plan(draft, Engine);
        var specs = plan.Specs;
        var start = await Engine.StartGroupAsync(specs, groupId, spend, cancellationToken).ConfigureAwait(false);
        if (!start.Posted) return new CompareStart(start, null);
        var group = Store.Save(new CompareGroup
        {
            Id = start.GroupId!,
            Shared = plan.Draft.Shared,
            Columns = [.. plan.Columns.Select((column, index) => new CompareGroupColumn { Column = column.Column, RunId = start.Ids[index], Chips = column.Chips })],
        });
        return new CompareStart(start, group);
    }

    /// <summary>Sends one column into group <paramref name="groupId"/> again: the group's shared request with <paramref name="column"/>'s model
    /// and overrides. The new run takes the place of <paramref name="replaces"/> (the run the column showed), or is added at
    /// <paramref name="position"/> when the column had not run in this group.</summary>
    /// <exception cref="KeyNotFoundException">There is no such group.</exception>
    /// <exception cref="CompareRefusedException">The column cannot run; nothing was reserved or sent.</exception>
    /// <exception cref="ArgumentException">The group already has the most columns.</exception>
    public async Task<CompareStart> RerunAsync(string groupId, CompareColumnDraft column, string? replaces, int? position, RunSpendAnswer? spend = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(column);
        var group = Group(groupId) ?? throw new KeyNotFoundException($"There is no Compare run {groupId}.");
        var index = replaces is null ? -1 : group.IndexOf(replaces);
        if (index < 0 && group.Columns.Count >= CompareDraft.MaxColumns)
            throw new ArgumentException($"This comparison already has {CompareDraft.MaxColumns} columns; remove one first.");
        var plan = ComparePlanner.Column(group.Shared, column, index < 0 ? Math.Clamp(position ?? group.Columns.Count, 0, group.Columns.Count) : index, Engine);
        if (!plan.Ok) throw new CompareRefusedException(new ComparePlan(group.Shared with { Columns = [plan.Column] }, [plan]));
        var start = await Engine.StartAsync(plan.Spec! with { GroupId = group.Id }, spend, cancellationToken).ConfigureAwait(false);
        if (!start.Posted) return new CompareStart(start with { GroupId = group.Id }, null);
        var entry = new CompareGroupColumn { Column = plan.Column, RunId = start.Id!, Chips = plan.Chips };
        var stored = Store.Update(group.Id, current =>
        {
            var columns = current.Columns.ToList();
            var at = replaces is null ? -1 : current.IndexOf(replaces);
            if (at >= 0) columns[at] = entry;
            else columns.Insert(Math.Clamp(position ?? columns.Count, 0, columns.Count), entry);
            return current with { Columns = columns, Best = current.Best == replaces ? null : current.Best };
        });
        return new CompareStart(start with { GroupId = group.Id }, stored);
    }

    /// <summary>Marks run <paramref name="runId"/> of group <paramref name="groupId"/> best (null clears the mark).</summary>
    /// <exception cref="KeyNotFoundException">There is no such group, or the run is not one of its columns.</exception>
    public CompareGroup MarkBest(string groupId, string? runId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var group = Group(groupId) ?? throw new KeyNotFoundException($"There is no Compare run {groupId}.");
        if (runId is not null && group.IndexOf(runId) < 0) throw new KeyNotFoundException($"Run {runId} is not a column of this comparison.");
        return Store.Update(group.Id, current => current with { Best = runId }) ?? throw new KeyNotFoundException($"There is no Compare run {groupId}.");
    }

    /// <summary>Removes the column showing run <paramref name="runId"/> from group <paramref name="groupId"/> (the run stays in History).</summary>
    /// <exception cref="KeyNotFoundException">There is no such group.</exception>
    public CompareGroup RemoveColumn(string groupId, string runId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(runId);
        if (Group(groupId) is null) throw new KeyNotFoundException($"There is no Compare run {groupId}.");
        return Store.Update(groupId, current => current with
        {
            Columns = [.. current.Columns.Where(column => column.RunId != runId)],
            Best = current.Best == runId ? null : current.Best,
        }) ?? throw new KeyNotFoundException($"There is no Compare run {groupId}.");
    }

    /// <summary>Stops every running column of group <paramref name="groupId"/>; returns the runs it stopped.</summary>
    /// <exception cref="KeyNotFoundException">There is no such group.</exception>
    public IReadOnlyList<string> Stop(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var group = Group(groupId) ?? throw new KeyNotFoundException($"There is no Compare run {groupId}.");
        return [.. group.Columns.Select(column => column.RunId).Where(Engine.Cancel)];
    }

    /// <summary>Group <paramref name="groupId"/>: the stored one, else one rebuilt from runs.db's Compare rows of that group, else null.</summary>
    public CompareGroup? Group(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        if (Store.Get(groupId) is { } stored) return stored;
        return Rebuild(groupId);
    }

    // A group started without Compare's own record (POST /api/runs/group): its columns from runs.db, in the order they were created.
    private CompareGroup? Rebuild(string groupId)
    {
        if (services.GetService<RunStore>() is not { } runs) return null;
        var rows = runs.List(new RunQuery { GroupId = groupId, Limit = CompareDraft.MaxColumns + 1 }).Items
            .Where(row => row.Source == SpendSource.Compare.Wire())
            .OrderBy(row => row.Created).ThenBy(row => row.Id, StringComparer.Ordinal)
            .Select(row => runs.Get(row.Id)).OfType<RunRecord>().ToList();
        if (rows.Count is 0 or > CompareDraft.MaxColumns || PlaygroundPages.FromRun(rows[0]) is not { } first) return null;
        var shared = new CompareDraft
        {
            SystemPrompt = first.SystemPrompt,
            Prompt = first.Messages.FirstOrDefault(message => message.Role == "user")?.Text ?? "",
            Thinking = first.ThinkingLevel,
            MaxTokens = first.MaxTokens,
            Temperature = first.Temperature,
        };
        var columns = rows.Select(row =>
        {
            var spec = PlaygroundPages.FromRun(row);
            var column = new CompareColumnDraft
            {
                Provider = row.Provider,
                Model = row.Model,
                Thinking = spec?.ThinkingLevel != shared.Thinking ? spec?.Thinking ?? "off" : null,
                Temperature = spec?.Temperature != shared.Temperature ? spec?.Temperature : null,
            };
            return new CompareGroupColumn { Column = column, RunId = row.Id, Chips = ComparePlanner.Column(shared, column, 0, Engine).Chips };
        }).ToList();
        return new CompareGroup { Id = groupId, Shared = shared, Columns = columns, Created = rows[0].Created, Updated = rows[^1].Updated };
    }

    private IReadOnlyList<CompareColumnView> Columns(CompareGroup group, (string RunId, string Key)? key = null)
    {
        var store = services.GetService<RunStore>();
        var live = services.GetService<RunLive>();
        var count = group.Columns.Count;
        var views = group.Columns.Select((entry, index) =>
        {
            var run = store?.Get(entry.RunId);
            var capabilities = Engine.Capabilities(entry.Column.Provider, entry.Column.Model);
            var output = Output(run, live);
            var error = output.State == "error" && run is not null ? Playground.ErrorDetails(run.Id) : null;
            var id = key is { } named && named.RunId == entry.RunId ? named.Key : "c" + (index + 1);
            return View(id, entry.Column, group.Shared, capabilities, entry.Chips, group.Id, output, group.Best == entry.RunId && run is not null, false,
                count > CompareDraft.MinColumns) with { Error = error };
        }).ToList();
        // Fastest: the shortest completed run, once nothing streams and at least two completed.
        if (!views.Any(view => view.Streaming))
        {
            var completed = views.Where(view => view is { State: "completed", Run.DurationMs: not null }).ToList();
            if (completed.Count >= 2)
            {
                var fastest = completed.MinBy(view => view.Run!.DurationMs);
                views = [.. views.Select(view => ReferenceEquals(view, fastest) ? view with { Fastest = true } : view)];
            }
        }
        return views;
    }

    private CompareColumnView View(string key, CompareColumnDraft column, CompareDraft shared, RunCapabilities? capabilities, IReadOnlyList<CompareChip> chips,
        string? groupId, RunOutputView? output, bool best, bool fastest, bool canRemove)
    {
        var catalogModel = Catalog?.FindModel(column.Provider, column.Model);
        var provider = Catalog?.FindProvider(column.Provider);
        return new CompareColumnView
        {
            Key = key,
            Column = column,
            Shared = shared.Shared,
            Capabilities = capabilities,
            Name = catalogModel?.Name ?? capabilities?.Name ?? column.Model,
            Billing = provider?.Billing ?? (capabilities?.Offline == true ? BillingKind.Free : BillingKind.Unknown),
            Chips = chips,
            GroupId = groupId,
            Output = (output ?? new RunOutputView()) with { IdPrefix = "cmp-" + key, ModelName = catalogModel?.Name, HistoryUrl = "/history?source=compare" },
            Best = best,
            Fastest = fastest,
            CanRemove = canRemove,
        };
    }

    private static RunOutputView Output(RunRecord? run, RunLive? live)
    {
        if (run is null) return new RunOutputView();
        var running = live?.Find(run.Id);
        IReadOnlyList<RunEventRecord> events = running is { Ended: false }
            ? [.. running.Items.Select(item => new RunEventRecord(item.Seq, item.AtMs, item.Event))]
            : [];
        var thinking = run.Options?["thinking"] is JsonValue level && level.TryGetValue<string>(out var text) && text != "off" ? text : null;
        return new RunOutputView { Run = run, Live = running is { Ended: false }, Events = events, Thinking = thinking };
    }

    // The default model and the next one the catalog lists (not hidden), so a new comparison starts on the user's own models.
    private IReadOnlyList<CompareColumnDraft> Defaults()
    {
        var engine = Engine;
        var hidden = services.GetService<ISettingsStore>()?.Get<ModelsSettings>() ?? ModelsSettings.CreateDefault();
        var columns = new List<CompareColumnDraft>();
        try
        {
            if (services.GetService<ChatService>()?.NewChat() is { Provider: { } provider, Model: { } model } && engine.Capabilities(provider, model) is not null)
                columns.Add(new CompareColumnDraft { Provider = provider, Model = model });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.LogDebug(error, "The default model could not be read for Compare.");
        }
        if (Catalog is { } catalog)
        {
            foreach (var model in catalog.Providers.SelectMany(provider => catalog.Models(provider.Id)))
            {
                if (columns.Count >= CompareDraft.MinColumns) break;
                if (hidden.IsHidden(model.Provider, model.Id) || columns.Any(column => column.Provider == model.Provider && column.Model == model.Id)) continue;
                if (engine.Capabilities(model.Provider, model.Id) is null) continue;
                columns.Add(new CompareColumnDraft { Provider = model.Provider, Model = model.Id });
            }
        }
        return columns;
    }

    private async Task<RunEstimateView> EstimateAsync(ComparePlan plan, CancellationToken cancellationToken)
    {
        var columns = plan.Columns.Where(column => column.Plan is { Allowed: true }).ToList();
        if (columns.Count == 0) return RunEstimateView.None;
        var executor = services.GetService<GuardedRunExecutor>();
        var pages = services.GetService<SpendPages>();
        if (executor is null && pages is null) return RunEstimateView.None;
        var run = "preflight-" + Guid.NewGuid().ToString("N");
        var estimates = new List<CompareColumnEstimate>();
        try
        {
            foreach (var column in columns)
            {
                var request = column.Plan!;
                var account = await AccountAsync(request, cancellationToken).ConfigureAwait(false);
                var call = new SpendCall
                {
                    Provider = request.Model.Provider,
                    Model = request.Model.Id,
                    Account = account?.Id,
                    Source = SpendSource.Compare,
                    Run = run,
                    InputBytes = CostEstimator.InputBytes(request.Messages),
                    MaxTokens = request.Spec.MaxTokens,
                };
                var decision = executor is not null
                    ? await executor.Guard.PreflightAsync(call, withApprovals: false, cancellationToken).ConfigureAwait(false)
                    : await pages!.PreflightAsync(call, cancellationToken).ConfigureAwait(false);
                var providerName = Catalog?.FindProvider(request.Model.Provider)?.DisplayName ?? request.Model.Provider;
                var name = column.Capabilities?.Name ?? request.Model.Id;
                estimates.Add(new CompareColumnEstimate(name, account?.Label is { Length: > 0 } label ? $"{providerName} · {label}" : providerName, decision));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The estimate is advice: a failure leaves the chip out, and Run all still asks the guard for real.
            log?.LogWarning(error, "The Compare estimate failed.");
            return RunEstimateView.None;
        }
        return CompareEstimate.Build(estimates, pages is { } named ? named.CapName : null);
    }

    private async Task<ChatAccount?> AccountAsync(RunRequestPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Model.Api == "faux" || services.GetService<IChatAccounts>() is not { } accounts) return null;
        try
        {
            return plan.Spec.Account is { } id
                ? await accounts.FindAsync(plan.Spec.Provider, id, cancellationToken).ConfigureAwait(false)
                : await accounts.BindAsync(plan.Spec.Provider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.LogDebug(error, "The account of {Provider} could not be read for Compare.", plan.Spec.Provider);
            return null;
        }
    }
}
