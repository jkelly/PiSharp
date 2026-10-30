// Decision 0011 (run W4, R5): what the user does with a run: stop one model or all, continue a model that reached its share of the run cap,
// answer approvals (one call; the waiting calls of every model the page listed, at once; or "allow this command in all workspaces", which
// becomes an exact run grant that answers the same command now and later), look at a model's diff, keep its changes (apply them to the
// project as uncommitted changes, or keep a branch; both through R4, which refuses on a conflict and never commits to the user's branches
// or pushes), discard workspaces (each one's patch is kept with the run first, so its diff can still be shown: W4 fix 2), and open a
// model as a chat (a chat binding for its session: from then on it is a plain chat with Settings › Tools approvals, working in its
// workspace). Every action is idempotent: repeating it answers what the first one did.
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>A pending tool call of a run's model.</summary>
/// <param name="Slot">The model's slot.</param>
/// <param name="Approval">The call.</param>
public sealed record AgentPendingApproval(string Slot, PendingApproval Approval);

/// <summary>How an approval request answers calls.</summary>
public enum AgentApprovalScope
{
    /// <summary>One call (slot and task id).</summary>
    Call,

    /// <summary>Allow the call's command (exactly that command) in every workspace for the rest of the run (a run grant), answering every
    /// waiting call it covers.</summary>
    Command,

    /// <summary>The waiting calls the page listed (<see cref="AgentApprovalItem"/>), in every column; a call that started waiting after
    /// the page showed its list is left waiting.</summary>
    All,
}

/// <summary>One waiting call a page showed and answers with <see cref="AgentApprovalScope.All"/>.</summary>
/// <param name="Slot">The model's slot.</param>
/// <param name="TaskId">The call's task.</param>
public sealed record AgentApprovalItem(string Slot, long TaskId);

/// <summary>One call an approval request answered.</summary>
/// <param name="Slot">The model's slot.</param>
/// <param name="TaskId">The call's task.</param>
/// <param name="Status">How the broker took it.</param>
public sealed record AgentApprovalAnswer(string Slot, long TaskId, ApprovalOutcomeStatus Status);

/// <summary>A Keep and the workspaces it discarded.</summary>
public sealed record AgentKeepAnswer(KeepResult Result, IReadOnlyList<string> Discarded, IReadOnlyList<string> KeptBack);

public sealed partial class AgentCompareRunner
{
    /// <summary>The tool calls of the run's models that wait for an approval, by slot then age.</summary>
    public IReadOnlyList<AgentPendingApproval> Pending(AgentCompareRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return [.. run.Models.Where(model => model.SessionId is not null && model.ChatId is null)
            .SelectMany(model => broker.Pending(model.SessionId).Select(item => new AgentPendingApproval(model.Slot, item)))
            .OrderBy(item => item.Slot, StringComparer.Ordinal).ThenBy(item => item.Approval.Since)];
    }

    /// <summary>Stops one model (<paramref name="slot"/>) or every model of the run (null): its task is aborted and it is not checked. A model
    /// whose check runs has its check ended (the check and every process it started are killed; the check is recorded as stopped), and a
    /// model that a limit stopped and that waits for its check is not checked (W4 fix 2). A model that is done is left alone.</summary>
    public async Task<AgentCompareRun> StopAsync(string id, string? slot, CancellationToken cancellationToken = default)
    {
        var run = Require(id);
        var slots = slot is null ? run.Models.Select(model => model.Slot).ToList() : [RequireSlot(run, slot).Slot];
        foreach (var name in slots)
        {
            var model = run.Model(name)!;
            if (model.Phase == AgentModelPhase.Done) continue;
            if (model.Phase == AgentModelPhase.Checking || model.Outcome is not null)
            {
                // Its task ended (or is ending): what is left to stop is its check (a model the user stopped is never checked).
                if (model.Phase == AgentModelPhase.Checking || (model.Outcome != AgentModelOutcome.Stopped && run.Spec.CheckCommand is not null))
                    StopCheck(id, name);
                continue;
            }
            gate.Stop(run, name, AgentModelOutcome.Stopped, AgentCompareGate.StoppedText);
            if (model.SessionId is { } session && device.IsOpen && device.Host.Sessions.FirstOrDefault(item => item.Id == session) is { } hosted)
                await AbortQuietlyAsync(hosted, cancellationToken).ConfigureAwait(false);
        }
        if (store.Get(id) is { IsActive: true }) Drive(id);
        return Require(id);
    }

    /// <summary>Answers a model's budget Ask with Continue: one more turn over its share, or (<paramref name="restOfRun"/>) no share for the
    /// rest of the run. Nothing changes when the model does not wait on one.</summary>
    public AgentCompareRun Continue(string id, string slot, bool restOfRun)
    {
        var run = Require(id);
        RequireSlot(run, slot);
        store.UpdateModel(id, slot, state =>
        {
            if (state.Outcome is not null) return state;
            if (restOfRun) return state.BudgetLifted ? state : state with { BudgetLifted = true };
            return state.BudgetAsk is not null && state.BudgetPasses == 0 ? state with { BudgetPasses = 1 } : state;
        });
        gate.Signal(AgentCompareRun.ModelKey(id, slot));
        return Require(id);
    }

    /// <summary>Answers waiting tool calls (see <see cref="AgentApprovalScope"/>). <paramref name="allow"/> false denies (with
    /// <paramref name="reason"/> for the model); <see cref="AgentApprovalScope.Command"/> only allows. <see cref="AgentApprovalScope.All"/>
    /// answers only the <paramref name="items"/> that still wait (the calls the page showed), never a call that started waiting since.</summary>
    /// <exception cref="AgentCompareException">A slot or call is missing (404), the call no longer waits (409), or the request is not allowed
    /// (400; also All without <paramref name="items"/>).</exception>
    public async Task<IReadOnlyList<AgentApprovalAnswer>> ApproveAsync(string id, AgentApprovalScope scope, string? slot, long? taskId, bool allow, string? reason,
        IReadOnlyList<AgentApprovalItem>? items = null, CancellationToken cancellationToken = default)
    {
        var run = Require(id);
        var decision = new ApprovalDecision(allow ? ApprovalChoice.AllowOnce : ApprovalChoice.Deny, allow ? null : reason);
        var pending = Pending(run);
        switch (scope)
        {
            case AgentApprovalScope.Call:
            {
                var target = Target(run, pending, slot, taskId);
                return [await DecideAsync(target, decision, cancellationToken).ConfigureAwait(false)];
            }
            case AgentApprovalScope.All:
            {
                if (items is null) throw AgentCompareException.BadRequest("List the calls to answer (items: [{slot, taskId}]), as the page showed them.");
                var listed = items.Select(item => (Slot: item.Slot.Trim().ToLowerInvariant(), item.TaskId)).ToHashSet();
                var chosen = pending.Where(item => listed.Contains((item.Slot, item.Approval.Request.TaskId))).ToList();
                return await Task.WhenAll(chosen.Select(item => DecideAsync(item, decision, cancellationToken))).ConfigureAwait(false);
            }
            case AgentApprovalScope.Command:
            {
                if (!allow) throw AgentCompareException.BadRequest("Allowing a command in every workspace only allows; deny the call instead.");
                var target = Target(run, pending, slot, taskId);
                var request = target.Approval.Request;
                if (!ToolAllowList.CommandTools.Contains(request.Tool) || request.Command is null)
                    throw AgentCompareException.BadRequest("Only a command (bash or powershell) can be allowed in every workspace.");
                var rules = ToolAllowList.RulesFor(request.Tool, request.Arguments);
                store.Update(id, current => current with { Grants = [.. current.Grants.Union(rules)] });
                return await DecideCoveredAsync(id, cancellationToken).ConfigureAwait(false);
            }
            default:
                throw AgentCompareException.BadRequest("Unknown approval scope.");
        }
    }

    /// <summary>Allows every waiting call the run's grants cover.</summary>
    private async Task<IReadOnlyList<AgentApprovalAnswer>> DecideCoveredAsync(string id, CancellationToken cancellationToken)
    {
        if (store.Get(id) is not { } run || run.Grants.Count == 0) return [];
        var covered = Pending(run).Where(item => ToolAllowList.Allows(run.Grants, item.Approval.Request.Tool, item.Approval.Request.Arguments)).ToList();
        try
        {
            return await Task.WhenAll(covered.Select(item => DecideAsync(item, new ApprovalDecision(ApprovalChoice.AllowOnce), cancellationToken))).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Agent compare run {Run}: calls its grants cover could not be allowed.", id);
            return [];
        }
    }

    private async Task<AgentApprovalAnswer> DecideAsync(AgentPendingApproval item, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        var request = item.Approval.Request;
        var outcome = await broker.DecideAsync(request.SessionId, request.TaskId, decision, cancellationToken).ConfigureAwait(false);
        return new AgentApprovalAnswer(item.Slot, request.TaskId, outcome.Status);
    }

    private static AgentPendingApproval Target(AgentCompareRun run, IReadOnlyList<AgentPendingApproval> pending, string? slot, long? taskId)
    {
        if (slot is null || taskId is null) throw AgentCompareException.BadRequest("Name the model (slot) and the call (taskId).");
        RequireSlot(run, slot);
        return pending.FirstOrDefault(item => item.Slot == slot && item.Approval.Request.TaskId == taskId)
            ?? throw AgentCompareException.Conflict(AgentCompareException.NotPending, $"Call {taskId} of model {slot.ToUpperInvariant()} no longer waits for an answer.");
    }

    /// <summary>The changes of <paramref name="slot"/>'s workspace against its base (also while it runs: the files changed so far). Once
    /// the workspace is gone (discarded, or removed after 7 days), the diff kept with the run (<see cref="StoredDiff"/>; its
    /// <see cref="WorkspaceDiff.StoredAt"/> is set).</summary>
    /// <exception cref="AgentCompareException">No such run or model (404), its workspace is still being made (409), or it was discarded with
    /// no patch kept (409 discarded).</exception>
    public async Task<WorkspaceDiff> DiffAsync(string id, string slot, CancellationToken cancellationToken = default)
    {
        var run = Require(id);
        var model = RequireSlot(run, slot);
        if (model.Discarded)
            return StoredDiff(run, model.Slot) ?? throw AgentCompareException.Conflict(AgentCompareException.Discarded,
                $"The workspace of model {model.Slot.ToUpperInvariant()} was discarded, so its line-by-line diff is gone. The files it changed are kept with the run.");
        model = RequireWorkspace(run, slot);
        try { return await workspaces.DiffAsync(id, model.Slot, cancellationToken).ConfigureAwait(false); }
        catch (WorkspaceException error) when (error.Code == WorkspaceException.NotFound && StoredDiff(run, model.Slot) is { } stored) { return stored; }
        catch (WorkspaceException error) { throw Translate(error); }
    }

    /// <summary>The diff kept with run <paramref name="run"/> for <paramref name="slot"/> (when its model was done, and again just before
    /// its workspace was discarded), or null when none was kept.</summary>
    public WorkspaceDiff? StoredDiff(AgentCompareRun run, string slot)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (store.KeptPatch(run.Id, slot) is not { } kept) return null;
        var kind = run.Workspace?.Kind ?? WorkspaceKind.Copy;
        var baseLabel = run.Workspace?.BaseLabel ?? (kind == WorkspaceKind.Worktree ? "the base commit" : "the base copy");
        return WorkspacePatch.Read(run.Id, slot, kind, baseLabel, kept.Patch, kept.Saved, kept.OmittedFiles);
    }

    /// <summary>What Keep would do for <paramref name="slot"/> (the Keep modal).</summary>
    public async Task<KeepPreview> PreviewKeepAsync(string id, string slot, CancellationToken cancellationToken = default)
    {
        var run = Require(id);
        var model = RequireWorkspace(run, slot);
        try { return await workspaces.PreviewKeepAsync(id, model.Slot, cancellationToken).ConfigureAwait(false); }
        catch (WorkspaceException error) { throw Translate(error); }
    }

    /// <summary>Keeps <paramref name="slot"/>'s changes: applies them to the project as uncommitted changes, or (<paramref name="branch"/>)
    /// commits them on the new branch pisharp/compare/&lt;run&gt;-&lt;slot&gt; without switching to it; with <paramref name="discardOthers"/>,
    /// the other models' workspaces are discarded once it went ahead (except one continued as a chat). A refusal changes nothing.</summary>
    /// <exception cref="AgentCompareException">The model still runs (409), or its workspace was discarded (409).</exception>
    public async Task<AgentKeepAnswer> KeepAsync(string id, string slot, bool branch, bool discardOthers, CancellationToken cancellationToken = default)
    {
        var run = Require(id);
        var model = RequireWorkspace(run, slot);
        if (model.Phase != AgentModelPhase.Done) throw AgentCompareException.Conflict(AgentCompareException.Busy, "Wait until this model is done to keep its changes.");
        if (discardOthers && run.Models.Any(other => other.Slot != model.Slot && other.Phase != AgentModelPhase.Done))
            throw AgentCompareException.Conflict(AgentCompareException.Busy, "Other models are still running; wait until they are done to discard their workspaces.");
        var wanted = branch ? KeepOutcome.BranchCreated : KeepOutcome.Applied;
        KeepResult result;
        if (model.Kept is { } kept && kept.Outcome == wanted) result = kept;
        else
        {
            try
            {
                result = branch ? await workspaces.KeepBranchAsync(id, model.Slot, cancellationToken).ConfigureAwait(false)
                    : await workspaces.ApplyAsync(id, model.Slot, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkspaceException error) { throw Translate(error); }
            if (result.Outcome != KeepOutcome.Refused) store.UpdateModel(id, model.Slot, state => state with { Kept = result });
        }
        var discarded = new List<string>();
        var keptBack = new List<string>();
        if (discardOthers && result.Outcome != KeepOutcome.Refused)
            foreach (var other in Require(id).Models.Where(other => other.Slot != model.Slot && !other.Discarded))
            {
                if (other.ChatId is not null && bindings.Get(other.ChatId) is { IsArchived: false })
                {
                    keptBack.Add(other.Slot);
                    continue;
                }
                await DiscardSlotAsync(id, other.Slot, cancellationToken).ConfigureAwait(false);
                discarded.Add(other.Slot);
            }
        return new AgentKeepAnswer(result, discarded, keptBack);
    }

    /// <summary>Discards <paramref name="slot"/>'s workspace, or (null) every workspace of the run (and its base copy). Refused while the model
    /// runs, and for a model continued as a chat (its chat works in that folder).</summary>
    /// <returns>The slots discarded now.</returns>
    public async Task<IReadOnlyList<string>> DiscardAsync(string id, string? slot, CancellationToken cancellationToken = default)
    {
        var run = Require(id);
        var models = slot is null ? run.Models.Where(model => !model.Discarded).ToList() : [RequireSlot(run, slot)];
        foreach (var model in models)
        {
            if (model.Phase != AgentModelPhase.Done && run.State != AgentRunState.Failed)
                throw AgentCompareException.Conflict(AgentCompareException.Busy, $"Model {model.Slot.ToUpperInvariant()} is still running; stop it first.");
            if (model.ChatId is { } chat && bindings.Get(chat) is { IsArchived: false })
                throw AgentCompareException.Conflict(AgentCompareException.InChat,
                    $"Model {model.Slot.ToUpperInvariant()} continues as a chat that works in its workspace; delete the chat first.");
        }
        var discarded = new List<string>();
        foreach (var model in models.Where(model => !model.Discarded))
        {
            await DiscardSlotAsync(id, model.Slot, cancellationToken).ConfigureAwait(false);
            discarded.Add(model.Slot);
        }
        if (slot is null)
        {
            try { await workspaces.DiscardRunAsync(id, cancellationToken).ConfigureAwait(false); }
            catch (WorkspaceException error) when (error.Code == WorkspaceException.NotFound) { /* Already gone. */ }
        }
        return discarded;
    }

    // Discards one workspace. Its patch is read first and kept with the run (W4 fix 2), with the changed files it lists, so the diff modal
    // still shows the lines afterwards (a model continued as a chat may have changed more since it was done). When the diff can't be read
    // (the folder is already gone, or git refuses a tampered workspace) the patch kept when the model was done stays, and the discard goes on.
    private async Task DiscardSlotAsync(string id, string slot, CancellationToken cancellationToken)
    {
        AgentDiffSummary? summary = null;
        if (Require(id).Model(slot) is { Discarded: false, WorkspacePath: not null })
        {
            try
            {
                var diff = await workspaces.DiffAsync(id, slot, cancellationToken).ConfigureAwait(false);
                KeepPatch(id, slot, diff);
                summary = Summarize(diff);
            }
            catch (Exception error) when (error is WorkspaceException or IOException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                log.LogInformation(error, "Agent compare run {Run}: the changes of model {Slot} could not be read before its discard; the patch kept when it was done stays.", id, slot);
            }
        }
        try { await workspaces.DiscardAsync(id, slot, cancellationToken).ConfigureAwait(false); }
        catch (WorkspaceException error) when (error.Code == WorkspaceException.NotFound) { /* Already gone. */ }
        store.UpdateModel(id, slot, state => state.Discarded ? state : state with { Discarded = true, Diff = summary ?? state.Diff });
    }

    /// <summary>Continues <paramref name="slot"/>'s model as a chat: its session gets a chat binding (its provider, model, account and thinking
    /// level), so it shows in the sidebar and opens at /chat/{id}, working in its workspace; from then on it is a plain chat (Settings › Tools
    /// approvals, its own spend run).</summary>
    /// <returns>The chat id (the session id).</returns>
    /// <exception cref="AgentCompareException">The model still runs or never started (409), or its workspace was discarded (409).</exception>
    public string OpenChat(string id, string slot)
    {
        var run = Require(id);
        var model = RequireSlot(run, slot);
        if (model.SessionId is not { } session) throw AgentCompareException.Conflict(AgentCompareException.Busy, "This model never started, so there is no chat to open.");
        if (model.Phase != AgentModelPhase.Done) throw AgentCompareException.Conflict(AgentCompareException.Busy, "Wait until this model is done to open it as a chat.");
        if (model.Discarded) throw AgentCompareException.Conflict(AgentCompareException.Discarded, "This model's workspace was discarded, so its chat would have nowhere to work.");
        var spec = run.ModelSpec(model.Slot)!;
        var now = bindings.Now();
        var title = ChatDisplay.Cut($"{spec.Name ?? spec.Model} · {run.Spec.Task.ReplaceLineEndings(" ")}", 100);
        var binding = bindings.Add(new ChatBinding(session, title, spec.Provider, spec.Model, spec.Account, spec.Thinking, null, 1, now, now, null,
            ChatRequestId(id, model.Slot)));
        store.UpdateModel(id, model.Slot, state => state.ChatId is not null ? state : state with { ChatId = binding.SessionId });
        _ = device.RefreshAsync(binding.SessionId);
        return binding.SessionId;
    }

    private static AgentCompareModelState RequireSlot(AgentCompareRun run, string slot) =>
        run.Model(slot?.Trim().ToLowerInvariant() ?? "") ?? throw AgentCompareException.Missing($"Run {run.Id} has no model {slot}.");

    private static AgentCompareModelState RequireWorkspace(AgentCompareRun run, string slot)
    {
        var model = RequireSlot(run, slot);
        if (model.Discarded) throw AgentCompareException.Conflict(AgentCompareException.Discarded, $"The workspace of model {model.Slot.ToUpperInvariant()} was discarded.");
        if (model.WorkspacePath is null) throw AgentCompareException.Conflict(AgentCompareException.Busy, "Its workspace is still being made.");
        return model;
    }
}
