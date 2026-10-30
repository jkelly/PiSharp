// Decision 0011 (run W4, R5): the agent compare runner (the plan's AgentCompareRunner). A run gives one task to 1 to 4 models, each in its own
// workspace (R4: a git worktree or a temporary copy of the same base), as one durable session per model on the app's device (DeviceHostService)
// with Cwd = the workspace's working directory, the same instructions (identical text), the same default tool registry and the same limits;
// the models are started in a shuffled order (stored). Each session is bound to its model's account (chosen when the run was created and never
// switched), and AgentCompareGate gives it its own Spend Guard run key (<run>-<slot>) and an equal share of the run cap among the metered
// models; turn, time and budget limits stop only that model. When a model stops, the check command runs in its working directory with a
// timeout (default 10 minutes; exit code and output tail kept), then its changes are summarized (R4's diff) and the model is done; a model the
// user stopped is not checked. The run's approval mode replaces Settings › Tools for its sessions (WebApproval's override, attached before the
// device opens); approvals queue per model and can be answered for all models at once (AgentCompareRunner.Actions).
//
// Durability: everything is stored as it happens (AgentCompareStore), so after a restart (StartAsync) every run that was preparing or running
// resumes: workspaces are made again if they were not finished (R4's create is idempotent), sessions are created by spawn key and tasks sent by
// request id (both exactly once), the durable device recovers each model's work itself (a tool call that waited for an approval asks again),
// the runner follows each model's task to its end again, and a check that was running runs again. runs.db has one parent row per run and one
// child row per model (kind "parent", parent = the run); each model's provider calls are recorded by R0's recorder as children of its row. The
// parent and child rows carry no cost or usage of their own (History's totals would count them twice): sum a model's calls for its cost.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>Runs agent compare runs (see the file comment). A singleton and hosted service.</summary>
public sealed partial class AgentCompareRunner(AgentCompareStore store, AgentCompareGate gate, DeviceHostService device, WorkspaceManager workspaces,
    ApprovalBroker broker, ChatBindings bindings, ISettingsStore settings, IModelCatalog catalog, IServiceProvider services,
    ILogger<AgentCompareRunner>? logger = null) : IHostedLifecycleService, IWorkspaceActivity, IAsyncDisposable
{
    /// <summary>The most models in a run.</summary>
    public const int MaxModels = 4;

    /// <summary>The slots, in column order.</summary>
    public static IReadOnlyList<string> Slots { get; } = ["a", "b", "c", "d"];

    /// <summary>How long the runner waits for a submission before it looks at the time limit again.</summary>
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(10);

    /// <summary>The most a finished model's background work is waited for before its check runs.</summary>
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<string, Task> drivers = new(StringComparer.Ordinal);
    private readonly ILogger log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private int disposed;

    /// <summary>The store.</summary>
    public AgentCompareStore Store => store;

    /// <summary>The runs being followed now.</summary>
    public IReadOnlyCollection<string> Driving
    {
        get { lock (drivers) return [.. drivers.Keys]; }
    }

    /// <inheritdoc/>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        // Before the device opens (DeviceHostService.StartAsync), so a recovered tool call of a model's session is judged by the run's mode.
        store.Load();
        WebApproval.SetOverrides(broker, gate.OverrideFor);
        broker.Changed += OnApprovalsChanged;
        gate.LimitReached += OnLimitReached;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.List(500).Where(run => run.IsActive)) Drive(run.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) == 0) await stopping.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        await stopping.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(Drivers()).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { /* Resumed at the next start. */ }
    }

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public bool IsActive(string run)
    {
        if (store.Get(run) is not { } found) return false;
        if (found.IsActive) return true;
        return found.Models.Any(model => model.ChatId is { } chat && !model.Discarded && bindings.Get(chat) is { IsArchived: false });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        broker.Changed -= OnApprovalsChanged;
        gate.LimitReached -= OnLimitReached;
        WebApproval.SetOverrides(broker, null);
        await stopping.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(Drivers()).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { /* Stopped. */ }
        stopping.Dispose();
    }

    /// <summary>Starts a run (or answers the run an earlier create with the same request id started). The project, the models and the
    /// workspaces' size are checked now; the workspaces are made and the models started in the background.</summary>
    /// <exception cref="AgentCompareException">A value is not allowed, the project is missing, worktrees were asked for outside git, or a copy
    /// is too large without a confirmation.</exception>
    public async Task<AgentCompareRun> CreateAsync(AgentCompareCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = request.RequestId?.Trim() ?? "";
        if (requestId.Length is 0 or > 128) throw AgentCompareException.BadRequest("A create needs a request id of 1 to 128 characters.");
        if (store.FindByRequest(requestId) is { } existing)
        {
            if (existing.IsActive) Drive(existing.Id);
            return existing;
        }
        var task = request.Task?.Trim() ?? "";
        if (task.Length == 0) throw AgentCompareException.BadRequest("Describe the task the models should do.");
        if (task.Length > HostedSession.MaxMessageLength) throw AgentCompareException.BadRequest($"A task is at most {HostedSession.MaxMessageLength:N0} characters.");
        if (request.Models is not { Count: >= 1 and <= MaxModels }) throw AgentCompareException.BadRequest($"Compare 1 to {MaxModels} models.");
        var defaults = settings.Get<AgentCompareSettings>();
        var project = Project(request.ProjectPath);
        var isolation = request.Isolation ?? defaults.Isolation;
        if (!Enum.IsDefined(isolation)) throw AgentCompareException.BadRequest("Unknown isolation.");
        IReadOnlyList<string> excludes;
        try { excludes = AgentCompareSettings.NormalizeExcludes([.. defaults.ExtraExcludes ?? [], .. request.ExtraExcludes ?? []]); }
        catch (ArgumentException error) { throw AgentCompareException.BadRequest(error.Message); }
        var maxTurns = request.MaxTurns ?? defaults.MaxTurns;
        if (maxTurns is < 1 or > AgentCompareSettings.MaxTurnsLimit) throw AgentCompareException.BadRequest($"Give each model 1 to {AgentCompareSettings.MaxTurnsLimit} turns.");
        var maxMinutes = request.MaxMinutes ?? defaults.MaxMinutes;
        if (maxMinutes is < 1 or > AgentCompareSettings.MaxMinutesLimit) throw AgentCompareException.BadRequest($"Give each model 1 to {AgentCompareSettings.MaxMinutesLimit} minutes.");
        var checkTimeout = request.CheckTimeoutSeconds ?? defaults.CheckTimeoutMinutes * 60;
        if (checkTimeout is < 1 or > AgentCompareSettings.MaxMinutesLimit * 60) throw AgentCompareException.BadRequest($"Give the check command 1 second to {AgentCompareSettings.MaxMinutesLimit} minutes.");
        var check = string.IsNullOrWhiteSpace(request.CheckCommand) ? null : request.CheckCommand.Trim();
        if (check is not null && (check.Length > AgentCheckCommand.MaxCommandLength || check.Contains('\n') || check.Contains('\r') || check.Contains('\0')))
            throw AgentCompareException.BadRequest($"A check command is one line of at most {AgentCheckCommand.MaxCommandLength:N0} characters.");
        if (request.CapMicros is { } cap && (cap < 1 || cap > SpendMoney.MaxMicros)) throw AgentCompareException.BadRequest("A run cap is more than $0 and at most $1,000,000.");
        var capAction = request.CapAction ?? CapAction.Ask;
        if (!Enum.IsDefined(capAction)) throw AgentCompareException.BadRequest("Unknown cap action.");
        var approval = request.Approval ?? defaults.Approval;
        if (!Enum.IsDefined(approval)) throw AgentCompareException.BadRequest("Unknown approval mode.");

        // The workspaces' facts now, so a refusal comes back to the page instead of failing the run later.
        ProjectInspection inspection;
        try
        {
            inspection = await workspaces.InspectAsync(project, new WorkspaceInspectOptions
            {
                Isolation = isolation,
                ExtraExcludes = excludes,
                Slots = request.Models.Count,
                WarnAboveBytes = defaults.CopyWarnBytes,
                EstimateCopy = false,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException error)
        {
            throw Translate(error);
        }
        if (isolation == WorkspaceIsolation.Worktree && inspection.WorktreeUnavailable is { } why)
            throw new AgentCompareException(400, AgentCompareException.NotGit, why + " Use a temporary copy instead.");
        if (inspection.Kind == WorkspaceKind.Copy && inspection.OverWarnLimit && !request.ConfirmLargeCopy)
            throw AgentCompareException.Conflict(AgentCompareException.TooLarge,
                $"A copy of this folder is {WorkspaceText.Size(inspection.CopyBytes ?? 0)} per model, more than {WorkspaceText.Size(inspection.WarnAboveBytes)}. Confirm to copy it anyway, or leave more out.");

        var instructions = Instructions(check);
        var models = new List<AgentCompareModelSpec>();
        for (var index = 0; index < request.Models.Count; index++)
            models.Add(await ModelAsync(request.Models[index], Slots[index], task, instructions, cancellationToken).ConfigureAwait(false));
        if (request.CapMicros is { } total)
        {
            var metered = models.Count(model => model.ShareMicros is not null);
            models = [.. models.Select(model => model with { ShareMicros = model.ShareMicros is null || metered == 0 ? null : total / metered })];
        }
        else models = [.. models.Select(model => model with { ShareMicros = null })];

        var order = models.Select(model => model.Slot).ToArray();
        Random.Shared.Shuffle(order);
        var now = DateTimeOffset.UtcNow;
        var run = new AgentCompareRun
        {
            Id = NewRunId(now),
            RequestId = requestId,
            State = AgentRunState.Preparing,
            Spec = new AgentCompareSpec
            {
                Task = task,
                ProjectPath = project,
                Isolation = isolation,
                BaseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? null : request.BaseRef.Trim(),
                ExtraExcludes = excludes,
                WarnAboveBytes = defaults.CopyWarnBytes,
                ConfirmLargeCopy = request.ConfirmLargeCopy,
                CheckCommand = check,
                CheckTimeoutSeconds = checkTimeout,
                MaxTurns = maxTurns,
                MaxMinutes = maxMinutes,
                CapMicros = request.CapMicros,
                CapAction = capAction,
                Approval = approval,
                Instructions = instructions,
                Models = models,
            },
            Order = order,
            Created = now,
            Models = [.. models.Select(model => new AgentCompareModelState { Slot = model.Slot })],
        };
        var stored = store.Add(run);
        if (ReferenceEquals(stored, run) || stored.Id == run.Id)
        {
            BeginParentRow(stored);
            log.LogInformation("Agent compare run {Run} created with {Count} models.", stored.Id, models.Count);
        }
        if (stored.IsActive) Drive(stored.Id);
        return stored;
    }

    /// <summary>The run <paramref name="id"/>.</summary>
    /// <exception cref="AgentCompareException">No such run (404).</exception>
    public AgentCompareRun Require(string id) =>
        (id is not null && store.Get(id) is { } run) ? run : throw AgentCompareException.Missing($"There is no agent compare run {id}.");

    /// <summary>The identical instructions every model of a run gets.</summary>
    public static string Instructions(string? checkCommand)
    {
        var text = new StringBuilder()
            .Append("You are one of several AI models given the same task. Each model works in its own workspace: a separate copy of the same project. ")
            .Append("Your current working directory is the project in your workspace. Work only inside it: don't read or change files outside it, ")
            .Append("and don't commit, push or switch branches. You can't see the other models' work, and they can't see yours.\n")
            .Append("When you are done, end with a short summary of what you changed and why.");
        if (checkCommand is not null)
            text.Append("\nWhen you finish, this check command runs in your working directory: `").Append(checkCommand).Append("`. Make it pass.");
        return text.ToString();
    }

    /// <summary>A new run id: <c>cmp_</c>, 10 Crockford base32 characters of the time in milliseconds, then 6 random ones.</summary>
    public static string NewRunId(DateTimeOffset now)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var text = new char[16];
        var time = (ulong)Math.Max(0, now.ToUnixTimeMilliseconds());
        for (var index = 9; index >= 0; index--)
        {
            text[index] = Alphabet[(int)(time & 31)];
            time >>= 5;
        }
        for (var index = 10; index < 16; index++) text[index] = Alphabet[RandomNumberGenerator.GetInt32(32)];
        return "cmp_" + new string(text);
    }

    /// <summary>Starts following run <paramref name="id"/> in the background (once; a run already followed is left alone).</summary>
    private void Drive(string id)
    {
        if (stopping.IsCancellationRequested) return;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (drivers)
        {
            if (drivers.ContainsKey(id)) return;
            drivers[id] = Task.Run(async () =>
            {
                await go.Task.ConfigureAwait(false);
                try { await DriveAsync(id, stopping.Token).ConfigureAwait(false); }
                finally { lock (drivers) drivers.Remove(id); }
            });
        }
        go.TrySetResult();
    }

    private Task[] Drivers()
    {
        lock (drivers) return [.. drivers.Values];
    }

    private async Task DriveAsync(string id, CancellationToken token)
    {
        try
        {
            if (store.Get(id) is not { } run) return;
            if (run.State == AgentRunState.Preparing && !await PrepareAsync(run, token).ConfigureAwait(false)) return;
            run = store.Get(id)!;
            if (run.State != AgentRunState.Running) return;
            var host = await device.WhenOpen.WaitAsync(token).ConfigureAwait(false);
            BeginParentRow(run);
            foreach (var slot in run.Order)
            {
                token.ThrowIfCancellationRequested();
                if (store.Get(id)?.Model(slot) is { Phase: AgentModelPhase.Pending, Outcome: null })
                    await StartModelAsync(id, slot, host, token).ConfigureAwait(false);
            }
            run = store.Get(id)!;
            await Task.WhenAll(run.Models.Select(model => FollowAsync(id, model.Slot, host, token))).ConfigureAwait(false);
            FinishRun(id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The app is stopping: the run resumes at the next start.
        }
        catch (Exception error)
        {
            log.LogError(error, "Agent compare run {Run} could not be followed.", id);
        }
    }

    private async Task<bool> PrepareAsync(AgentCompareRun run, CancellationToken token)
    {
        WorkspaceRun made;
        try
        {
            made = await workspaces.CreateAsync(new WorkspaceRequest
            {
                Run = run.Id,
                ProjectPath = run.Spec.ProjectPath,
                Slots = [.. run.Spec.Models.Select(model => new WorkspaceSlotRequest(model.Slot, model.Name ?? model.Model))],
                Isolation = run.Spec.Isolation,
                BaseRef = run.Spec.BaseRef,
                ExtraExcludes = run.Spec.ExtraExcludes,
                WarnAboveBytes = run.Spec.WarnAboveBytes,
                ConfirmLargeCopy = run.Spec.ConfirmLargeCopy,
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            log.LogWarning(error, "Agent compare run {Run}: its workspaces could not be made.", run.Id);
            var message = "The workspaces could not be made: " + error.Message;
            var failed = store.Update(run.Id, current => current with
            {
                State = AgentRunState.Failed,
                Error = message,
                Finished = DateTimeOffset.UtcNow,
                Models = [.. current.Models.Select(model => model with
                {
                    Phase = AgentModelPhase.Done,
                    Outcome = model.Outcome ?? AgentModelOutcome.Error,
                    Message = model.Message ?? message,
                })],
            });
            if (failed is not null) FinishParentRow(failed);
            return false;
        }
        store.Update(run.Id, current => current with
        {
            State = current.State == AgentRunState.Preparing ? AgentRunState.Running : current.State,
            Workspace = new AgentWorkspaceInfo
            {
                Kind = made.Kind,
                Label = made.Label,
                BaseLabel = made.BaseLabel,
                BaseCommit = made.BaseCommit,
                ShortCommit = made.ShortCommit,
                BaseBranch = made.BaseBranch,
                Directory = made.Directory,
                NotCopied = made.NotCopied,
                Excludes = made.Excludes,
                SkippedLinks = made.SkippedLinks,
            },
            Models = [.. current.Models.Select(model => made.Find(model.Slot) is { } slot
                ? model with { WorkspacePath = slot.Path, WorkingDirectory = slot.WorkingDirectory }
                : model)],
        });
        return true;
    }

    private async Task StartModelAsync(string id, string slot, DurableDeviceHost host, CancellationToken token)
    {
        var run = store.Get(id)!;
        var spec = run.ModelSpec(slot)!;
        var model = run.Model(slot)!;
        try
        {
            if (model.WorkingDirectory is not { } cwd) throw new InvalidOperationException("The model has no workspace.");
            var session = await host.CreateSessionAsync(new SessionCreateRequest
            {
                Cwd = cwd,
                Name = ChatDisplay.Cut($"Agent compare {run.Id} · {slot.ToUpperInvariant()} · {spec.Name ?? spec.Model}", 100),
                Model = new ModelRef(spec.Provider, spec.Model),
                ThinkingLevel = spec.Thinking,
                SpawnKey = $"agent-compare:{run.Id}:{slot}",
            }, token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            // Stored before anything is sent, so the gate knows the session from its first request.
            run = store.UpdateModel(id, slot, state => state with { SessionId = session.Id, StartedAt = state.StartedAt ?? now })!;
            run = store.Update(id, current => current.Started is null ? current with { Started = now } : current)!;
            BeginChildRow(run, slot);
            await session.ConfigureAsync(1, new AgentConfigurationChange { Instructions = run.Spec.Instructions }, token).ConfigureAwait(false);
            var sent = await session.SendAsync(1, new SendRequest(run.Spec.Task, TaskRequestId(id, slot)), token).ConfigureAwait(false);
            store.UpdateModel(id, slot, state => state with { SubmissionId = sent.SubmissionId.Value, Phase = AgentModelPhase.Running });
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            log.LogWarning(error, "Agent compare run {Run}: model {Slot} could not be started.", id, slot);
            store.UpdateModel(id, slot, state => state with
            {
                Phase = AgentModelPhase.Done,
                Outcome = state.Outcome ?? AgentModelOutcome.Error,
                Message = state.Message ?? "This model could not be started: " + error.Message,
                Error = error.Message,
                EndedAt = DateTimeOffset.UtcNow,
            });
            FinishChildRow(id, slot);
        }
    }

    private async Task FollowAsync(string id, string slot, DurableDeviceHost host, CancellationToken token)
    {
        try
        {
            var model = store.Get(id)?.Model(slot);
            if (model is null || model.Phase == AgentModelPhase.Done) return;
            if (model.Phase == AgentModelPhase.Pending)
            {
                // Stopped before it started (or it could not start): nothing ran in its workspace.
                store.UpdateModel(id, slot, state => state with
                {
                    Phase = AgentModelPhase.Done,
                    Outcome = state.Outcome ?? AgentModelOutcome.Stopped,
                    Message = state.Message ?? AgentCompareGate.StoppedText,
                    EndedAt = state.EndedAt ?? DateTimeOffset.UtcNow,
                });
                FinishChildRow(id, slot);
                return;
            }
            if (model.Phase == AgentModelPhase.Running) await WaitForModelAsync(id, slot, host, token).ConfigureAwait(false);
            model = store.Get(id)?.Model(slot);
            if (model?.Phase == AgentModelPhase.Checking) await CheckAsync(id, slot, token).ConfigureAwait(false);
            await SummarizeAsync(id, slot, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            log.LogError(error, "Agent compare run {Run}: model {Slot} could not be followed.", id, slot);
            store.UpdateModel(id, slot, state => state with
            {
                Phase = AgentModelPhase.Done,
                Outcome = state.Outcome ?? AgentModelOutcome.Error,
                Message = state.Message ?? "The runner lost track of this model: " + error.Message,
                Error = state.Error ?? error.Message,
                EndedAt = state.EndedAt ?? DateTimeOffset.UtcNow,
            });
            FinishChildRow(id, slot);
        }
    }

    private async Task WaitForModelAsync(string id, string slot, DurableDeviceHost host, CancellationToken token)
    {
        var run = store.Get(id)!;
        var model = run.Model(slot)!;
        var session = host.Sessions.FirstOrDefault(item => item.Id == model.SessionId)
            ?? throw new InvalidOperationException($"Its session {model.SessionId} is gone.");
        BeginChildRow(run, slot);
        var submission = model.SubmissionId;
        if (submission is null)
        {
            // Stopped between the send and storing its submission: the same request id answers the same submission.
            var sent = await session.SendAsync(1, new SendRequest(run.Spec.Task, TaskRequestId(id, slot)), token).ConfigureAwait(false);
            submission = sent.SubmissionId.Value;
            store.UpdateModel(id, slot, state => state with { SubmissionId = submission });
        }
        SendResult result;
        while (true)
        {
            model = store.Get(id)!.Model(slot)!;
            var deadline = (model.StartedAt ?? DateTimeOffset.UtcNow).AddMinutes(run.Spec.MaxMinutes);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero && model.Outcome is null)
            {
                gate.Stop(run, slot, AgentModelOutcome.TimeLimit, AgentCompareGate.TimeText(run.Spec.MaxMinutes));
                await AbortQuietlyAsync(session, token).ConfigureAwait(false);
            }
            var wait = remaining > TimeSpan.Zero && remaining < FollowInterval ? remaining + TimeSpan.FromMilliseconds(50) : FollowInterval;
            result = await session.SubmissionAsync(submission.Value, wait, token).ConfigureAwait(false);
            if (result.Status is SendStatus.Done or SendStatus.Unanswered) break;
        }
        model = store.Get(id)!.Model(slot)!;
        if (model.Outcome is not null) await AbortQuietlyAsync(session, token).ConfigureAwait(false);
        else await IdleAsync(session, token).ConfigureAwait(false);
        var answered = result.Status == SendStatus.Done;
        var answer = result.AnswerText is { Length: > 0 } text ? (text.Length > 8000 ? text[..7999] + "…" : text) : null;
        store.UpdateModel(id, slot, state =>
        {
            var outcome = state.Outcome ?? (answered ? AgentModelOutcome.Finished : AgentModelOutcome.Failed);
            var check = run.Spec.CheckCommand is not null && outcome != AgentModelOutcome.Stopped;
            return state with
            {
                Outcome = outcome,
                Message = state.Message ?? (answered ? null : "The model stopped without an answer" + (result.Reason is { Length: > 0 } reason ? ": " + reason : ".")),
                Error = answered || state.Outcome is not null ? state.Error : result.Reason,
                Answer = answer ?? state.Answer,
                EndedAt = state.EndedAt ?? DateTimeOffset.UtcNow,
                BudgetAsk = null,
                Phase = check ? AgentModelPhase.Checking : state.Phase,
            };
        });
    }

    private async Task CheckAsync(string id, string slot, CancellationToken token)
    {
        var run = store.Get(id)!;
        var model = run.Model(slot)!;
        if (run.Spec.CheckCommand is not { } command || model.WorkingDirectory is not { } cwd || !Directory.Exists(cwd)) return;
        var result = await AgentCheckCommand.RunAsync(command, cwd, TimeSpan.FromSeconds(run.Spec.CheckTimeoutSeconds), token).ConfigureAwait(false);
        store.UpdateModel(id, slot, state => state with { Check = result });
    }

    private async Task SummarizeAsync(string id, string slot, CancellationToken token)
    {
        AgentDiffSummary? summary = null;
        var model = store.Get(id)?.Model(slot);
        if (model is { Discarded: false, WorkspacePath: not null })
        {
            try
            {
                var diff = await workspaces.DiffAsync(id, slot, token).ConfigureAwait(false);
                summary = new AgentDiffSummary
                {
                    Files = diff.Files.Count,
                    Added = diff.Added,
                    Removed = diff.Removed,
                    Summary = diff.Summary,
                    Items = [.. diff.Files.Take(200).Select(file => new AgentDiffFile(file.Path, file.Change, file.Added, file.Removed, file.Binary))],
                };
            }
            catch (Exception error) when (error is WorkspaceException or IOException or InvalidOperationException && !token.IsCancellationRequested)
            {
                log.LogWarning(error, "Agent compare run {Run}: the changes of model {Slot} could not be read.", id, slot);
            }
        }
        store.UpdateModel(id, slot, state => state with { Diff = summary ?? state.Diff, Phase = AgentModelPhase.Done, EndedAt = state.EndedAt ?? DateTimeOffset.UtcNow });
        FinishChildRow(id, slot);
    }

    private void FinishRun(string id)
    {
        var finished = store.Update(id, run => run.State == AgentRunState.Running && run.Models.All(model => model.Phase == AgentModelPhase.Done)
            ? run with { State = AgentRunState.Finished, Finished = DateTimeOffset.UtcNow }
            : run);
        if (finished is { State: AgentRunState.Finished }) FinishParentRow(finished);
    }

    private static async Task AbortQuietlyAsync(HostedSession session, CancellationToken token)
    {
        try { await session.AbortAsync(1, background: true, token).ConfigureAwait(false); }
        catch (Exception error) when (error is DurableHostException or InvalidOperationException or ObjectDisposedException) { /* Idle or gone. */ }
    }

    private static async Task IdleAsync(HostedSession session, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + IdleLimit;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!(await session.AgentsAsync(token).ConfigureAwait(false)).Any(agent => agent.Busy)) return;
            }
            catch (Exception error) when (error is DurableHostException or InvalidOperationException) { return; }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        await AbortQuietlyAsync(session, token).ConfigureAwait(false);
    }

    private async Task<AgentCompareModelSpec> ModelAsync(AgentCompareModelRequest request, string slot, string task, string instructions, CancellationToken token)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Model))
            throw AgentCompareException.BadRequest("Name each model's provider and model.");
        var provider = catalog.FindProvider(request.Provider.Trim());
        var model = provider is null ? null : catalog.FindModel(provider.Id, request.Model.Trim());
        if (provider is null || model is null) throw AgentCompareException.BadRequest($"{request.Provider}/{request.Model} is not one of the models this app offers.");
        var level = request.Thinking ?? settings.Get<DefaultsSettings>().Thinking ?? (model.Reasoning ? "medium" : "off");
        var thinking = PiSharp.AI.ThinkingLevels.Clamp([.. ChatDisplay.HostThinkingLevels(model.ThinkingLevels)], level);
        var accounts = services.GetService<IChatAccounts>() ?? NoChatAccounts.Instance;
        ChatAccount? account;
        if (!string.IsNullOrWhiteSpace(request.Account))
            account = await accounts.FindAsync(provider.Id, request.Account.Trim(), token).ConfigureAwait(false)
                ?? throw AgentCompareException.BadRequest($"{provider.DisplayName} has no account {request.Account}.");
        else account = await accounts.BindAsync(provider.Id, token).ConfigureAwait(false);
        var billing = provider.Billing;
        long? share = null;
        if (services.GetService<ISpendGuard>() is { } guard)
        {
            try
            {
                var decision = await guard.PreflightAsync(new SpendCall
                {
                    Provider = provider.Id,
                    Model = model.Id,
                    Account = account?.Id,
                    Source = SpendSource.AgentCompare,
                    InputBytes = Encoding.UTF8.GetByteCount(task) + Encoding.UTF8.GetByteCount(instructions),
                }, token).ConfigureAwait(false);
                billing = decision.Estimate.Billing;
                // A placeholder: the run cap is shared among the models marked here (metered, with a known price).
                if (billing == BillingKind.Metered && decision.Estimate.WorstMicros is not null) share = 0;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogWarning(error, "Agent compare: the billing of {Provider}/{Model} could not be read.", provider.Id, model.Id);
            }
        }
        return new AgentCompareModelSpec
        {
            Slot = slot,
            Provider = provider.Id,
            Model = model.Id,
            Name = model.Name,
            Thinking = thinking,
            Account = account?.Id,
            AccountLabel = account?.Label,
            Billing = billing,
            ShareMicros = share,
        };
    }

    private static string Project(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw AgentCompareException.BadRequest("Choose the project folder.");
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            throw AgentCompareException.BadRequest($"\"{path}\" is not a folder path.");
        }
        if (!Path.IsPathFullyQualified(path.Trim())) throw AgentCompareException.BadRequest("Give the project folder as a full path.");
        if (!Directory.Exists(full)) throw new AgentCompareException(400, AgentCompareException.ProjectMissing, $"There is no folder {full}.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static AgentCompareException Translate(WorkspaceException error) => error.Code switch
    {
        WorkspaceException.ProjectMissing => new AgentCompareException(400, AgentCompareException.ProjectMissing, error.Message, error),
        WorkspaceException.NotGit => new AgentCompareException(400, AgentCompareException.NotGit, error.Message, error),
        WorkspaceException.NotFound => new AgentCompareException(404, AgentCompareException.NotFound, error.Message, error),
        WorkspaceException.Invalid => new AgentCompareException(400, AgentCompareException.Invalid, error.Message, error),
        WorkspaceException.TooLarge => new AgentCompareException(409, AgentCompareException.TooLarge, error.Message, error),
        _ => new AgentCompareException(409, error.Code, error.Message, error),
    };

    private static string TaskRequestId(string run, string slot) => $"agent-compare:{run}:{slot}:task";

    // ─── runs.db ────────────────────────────────────────────────────────────

    private void BeginParentRow(AgentCompareRun run)
    {
        if (services.GetService<RunStore>() is not { } runs) return;
        try
        {
            if (runs.Get(run.Id) is { Status: not RunStatus.Running and not RunStatus.Interrupted }) return;
            runs.BeginCall(new RunRecord
            {
                Id = run.Id,
                Op = "agent-compare:" + run.Id,
                Kind = RunKind.Parent,
                Source = SpendSource.AgentCompare.Wire(),
                Status = RunStatus.Running,
                Provider = "",
                Model = "",
                Title = ChatDisplay.Cut(run.Spec.Task.ReplaceLineEndings(" "), 100),
                Options = Options(run),
                Created = run.Created,
                Started = run.Started ?? run.Created,
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Agent compare run {Run}: its runs.db row could not be written.", run.Id);
        }
    }

    private void BeginChildRow(AgentCompareRun run, string slot)
    {
        if (services.GetService<RunStore>() is not { } runs || run.ModelSpec(slot) is not { } spec || run.Model(slot) is not { } model) return;
        var key = AgentCompareRun.ModelKey(run.Id, slot);
        try
        {
            if (runs.Get(key) is { Status: not RunStatus.Running and not RunStatus.Interrupted }) return;
            runs.BeginCall(new RunRecord
            {
                Id = key,
                Op = "agent-compare:" + key,
                Kind = RunKind.Parent,
                Source = SpendSource.AgentCompare.Wire(),
                Status = RunStatus.Running,
                Provider = spec.Provider,
                Model = spec.Model,
                Account = spec.Account,
                ParentId = run.Id,
                Session = model.SessionId,
                Title = $"{slot.ToUpperInvariant()} · {spec.Name ?? spec.Model}",
                MessagesRef = "agent-compare:" + key,
                Billing = Wire(spec.Billing),
                Options = new JsonObject
                {
                    ["slot"] = slot,
                    ["thinking"] = spec.Thinking,
                    ["maxTurns"] = run.Spec.MaxTurns,
                    ["maxMinutes"] = run.Spec.MaxMinutes,
                    ["shareMicros"] = spec.ShareMicros,
                    ["approval"] = JsonSerializer.SerializeToNode(run.Spec.Approval, SettingsStore.JsonOptions),
                },
                Created = model.StartedAt ?? DateTimeOffset.UtcNow,
                Started = model.StartedAt ?? DateTimeOffset.UtcNow,
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Agent compare run {Run}: the runs.db row of model {Slot} could not be written.", run.Id, slot);
        }
    }

    private void FinishChildRow(string id, string slot)
    {
        if (services.GetService<RunStore>() is not { } runs || store.Get(id) is not { } run || run.Model(slot) is not { } model) return;
        var key = AgentCompareRun.ModelKey(id, slot);
        try
        {
            if (runs.Get(key) is null) BeginChildRow(run, slot);
            var notes = new List<string> { store.Turns(key) + " turns" };
            if (model.Check is { } check) notes.Add("check " + check.State + (check.ExitCode is { } exit ? " (exit " + exit + ")" : ""));
            if (model.Diff is { } diff) notes.Add(diff.Summary);
            runs.Finish(key, new RunOutcome
            {
                Status = model.Outcome switch
                {
                    AgentModelOutcome.Finished => RunStatus.Completed,
                    AgentModelOutcome.Failed or AgentModelOutcome.Error => RunStatus.Error,
                    _ => RunStatus.Aborted,
                },
                OutputText = model.Answer,
                StopReason = model.Outcome,
                Error = model.Outcome is AgentModelOutcome.Finished ? null : model.Message ?? model.Error,
                Notes = string.Join(" · ", notes),
                DurationMs = model.StartedAt is { } started ? (long)((model.EndedAt ?? DateTimeOffset.UtcNow) - started).TotalMilliseconds : null,
                Finished = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Agent compare run {Run}: the runs.db row of model {Slot} could not be finished.", id, slot);
        }
    }

    private void FinishParentRow(AgentCompareRun run)
    {
        if (services.GetService<RunStore>() is not { } runs) return;
        try
        {
            if (runs.Get(run.Id) is null) BeginParentRow(run);
            var finished = run.Models.Count(model => model.Outcome == AgentModelOutcome.Finished);
            runs.Finish(run.Id, new RunOutcome
            {
                Status = run.State == AgentRunState.Failed ? RunStatus.Error : RunStatus.Completed,
                OutputText = $"{finished} of {run.Models.Count} models finished.",
                Error = run.Error,
                Notes = string.Join(" · ", run.Models.Select(model => model.Slot.ToUpperInvariant() + ": " + (model.Outcome ?? model.Phase))),
                DurationMs = (long)((run.Finished ?? DateTimeOffset.UtcNow) - (run.Started ?? run.Created)).TotalMilliseconds,
                Finished = run.Finished ?? DateTimeOffset.UtcNow,
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Agent compare run {Run}: its runs.db row could not be finished.", run.Id);
        }
    }

    private static JsonObject Options(AgentCompareRun run) => new()
    {
        ["models"] = new JsonArray([.. run.Spec.Models.Select(model => (JsonNode)new JsonObject
        {
            ["slot"] = model.Slot,
            ["provider"] = model.Provider,
            ["model"] = model.Model,
            ["thinking"] = model.Thinking,
            ["account"] = model.Account,
        })]),
        ["order"] = new JsonArray([.. run.Order.Select(slot => (JsonNode)JsonValue.Create(slot)!)]),
        ["maxTurns"] = run.Spec.MaxTurns,
        ["maxMinutes"] = run.Spec.MaxMinutes,
        ["capMicros"] = run.Spec.CapMicros,
        ["capAction"] = JsonSerializer.SerializeToNode(run.Spec.CapAction, SettingsStore.JsonOptions),
        ["approval"] = JsonSerializer.SerializeToNode(run.Spec.Approval, SettingsStore.JsonOptions),
        ["checkCommand"] = run.Spec.CheckCommand,
        ["checkTimeoutSeconds"] = run.Spec.CheckTimeoutSeconds,
        ["isolation"] = JsonSerializer.SerializeToNode(run.Spec.Isolation, SettingsStore.JsonOptions),
        ["projectPath"] = run.Spec.ProjectPath,
    };

    private static string Wire(BillingKind billing) => JsonSerializer.SerializeToNode(billing, SettingsStore.JsonOptions)!.GetValue<string>();

    // ─── events ─────────────────────────────────────────────────────────────

    private void OnLimitReached(string id, string slot)
    {
        // A limit refused a request: the model's task ends unanswered by itself; stop its background work too.
        if (store.Get(id)?.Model(slot)?.SessionId is not { } session || !device.IsOpen) return;
        if (device.Host.Sessions.FirstOrDefault(item => item.Id == session) is { } hosted)
            _ = Task.Run(() => AbortQuietlyAsync(hosted, CancellationToken.None));
    }

    private void OnApprovalsChanged(string sessionId)
    {
        if (store.FindSession(sessionId) is not { } found || found.Model.ChatId is not null) return;
        store.Touch(found.Run.Id);
        if (found.Run.Grants.Count == 0) return;
        // A call that asked just as "allow in all workspaces" was given: the run's grants answer it.
        _ = Task.Run(() => DecideCoveredAsync(found.Run.Id, CancellationToken.None));
    }
}
