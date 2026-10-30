// Decision 0011 (run W4, R5): what an agent compare model may do, decided on each of its requests and tool calls. The device's sessions all
// use DeviceHostService's SessionStream, which names a session's requests {source: "chat", run: <session id>}; for a session that belongs to
// a run's model (and was not opened as a chat) the gate rewrites them to {source: "agent-compare", run: "<run>-<slot>", account: the model's
// bound account} (the op stays: the Spend Guard and runs.db key on it), so each model has its own Spend Guard run key and its calls are
// runs.db children of its model row. Before a request is sent it checks, in order: a stop (by the user or a limit) refuses it; a recovered
// request (its op was let through before) goes on; the time limit; the turn limit (agent_compare_turns counts let-through ops); the model's
// share of the run cap (what its run key counts in the ledger plus the request's worst case from the Spend Guard's preflight; metered models
// with a known price only) - Block stops the model, Ask pauses it until the user continues (one turn, or the rest of the run) or stops it. A
// refused request is never sent: its stream is one StreamError with stop reason "aborted" and the reason as errorMessage, which the durable
// engine never retries; the run of the model's task ends unanswered and the runner checks its workspace. The gate also notes each model's
// latest tool call and the files its write and edit calls name, and answers the approval override (the run's mode) for its sessions.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>Admits, rewrites and observes agent compare models' requests (see the file comment). A singleton.</summary>
public sealed class AgentCompareGate(AgentCompareStore store, IServiceProvider services, ILogger<AgentCompareGate>? logger = null)
{
    /// <summary>The most files a model's touched list keeps.</summary>
    public const int MaxTouched = 200;

    private readonly ConcurrentDictionary<string, TaskCompletionSource> signals = new(StringComparer.Ordinal);
    private readonly ILogger log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Raised when a model was stopped by a limit at a request (its run id and slot), outside any lock.</summary>
    public event Action<string, string>? LimitReached;

    /// <summary>The store.</summary>
    public AgentCompareStore Store => store;

    /// <summary>The run and model a request of <paramref name="options"/> belongs to: a run's model session not opened as a chat; else null.</summary>
    public (AgentCompareRun Run, AgentCompareModelState Model)? Find(DurableStreamOptions? options)
    {
        var fields = SpendMetadata.Read(options);
        if (fields.Run is not { } session || fields.Source is not (SpendSource.Chat or null)) return null;
        return store.FindSession(session) is { } found && found.Model.ChatId is null ? found : null;
    }

    /// <summary><paramref name="options"/> with metadata.pisharp naming the model: source agent-compare, run = the model key, its account.</summary>
    public static DurableStreamOptions Rewrite(DurableStreamOptions? options, AgentCompareRun run, AgentCompareModelState model)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(model);
        var metadata = options?.Metadata ?? JsonObjectValue.Empty;
        var pisharp = (SessionStreamMetadata.Read(metadata) ?? JsonObjectValue.Empty).Remove(SpendMetadata.TierField);
        pisharp = run.ModelSpec(model.Slot)?.Account is { } account ? pisharp.SetItem(SpendMetadata.AccountField, account) : pisharp.Remove(SpendMetadata.AccountField);
        pisharp = SpendMetadata.With(pisharp, SpendSource.AgentCompare, AgentCompareRun.ModelKey(run.Id, model.Slot));
        return (options ?? new DurableStreamOptions()) with { Metadata = metadata.SetItem(SessionStreamMetadata.Key, pisharp) };
    }

    /// <summary>Decides whether a request of <paramref name="model"/> may be sent (see the file comment); with <paramref name="turn"/> it
    /// counts as a turn (a generation request; a compaction summary does not). Waits while a budget Ask is open.</summary>
    /// <returns>Null to send it; else why it was refused.</returns>
    public async Task<string?> AdmitAsync(AgentCompareRun run, AgentCompareModelState model, DurableModel durableModel, ImmutableArray<JsonValue> messages,
        DurableStreamOptions options, bool turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(model);
        var key = AgentCompareRun.ModelKey(run.Id, model.Slot);
        var op = SessionStreamMetadata.Field(options, SessionStreamMetadata.OperationField);
        var current = Current(run.Id, model.Slot) ?? model;
        if (current.Outcome is not null) return StopText(run, current);
        if (turn && op is not null && store.HasTurn(key, op)) return null;
        var now = DateTimeOffset.UtcNow;
        if (current.StartedAt is { } started && now >= started.AddMinutes(run.Spec.MaxMinutes))
            return Stop(run, current.Slot, AgentModelOutcome.TimeLimit, TimeText(run.Spec.MaxMinutes));
        if (turn && store.Turns(key) >= run.Spec.MaxTurns)
            return Stop(run, current.Slot, AgentModelOutcome.TurnLimit, TurnText(run.Spec.MaxTurns));
        if (await BudgetAsync(run, current, durableModel, messages, options, cancellationToken).ConfigureAwait(false) is { } refused) return refused;
        if (turn) store.AddTurn(key, op ?? "turn-" + Guid.NewGuid().ToString("N"));
        store.Touch(run.Id);
        return null;
    }

    /// <summary>Notes the tool calls of a model's answer: its latest tool call and the files write and edit name.</summary>
    public void Observe(AgentCompareRun run, AgentCompareModelState model, AssistantMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var calls = message.Content.OfType<ToolCallContent>().ToList();
        if (calls.Count == 0) return;
        try
        {
            store.UpdateModel(run.Id, model.Slot, state =>
            {
                var touched = state.Touched.ToList();
                AgentToolCall? latest = null;
                foreach (var call in calls)
                {
                    var arguments = call.Arguments.Value;
                    var path = Text(arguments, "path");
                    var summary = Text(arguments, "command") ?? path ?? "";
                    latest = new AgentToolCall(call.Name, summary.Length > 200 ? summary[..199] + "…" : summary, DateTimeOffset.UtcNow);
                    if (WebApproval.FileTools.Contains(call.Name) && path is not null && Relative(state, path) is { } file && touched.Count < MaxTouched &&
                        !touched.Contains(file, StringComparer.Ordinal))
                        touched.Add(file);
                }
                return state with { LatestTool = latest, Touched = touched };
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Agent compare: the tool calls of {Run}/{Slot} could not be noted.", run.Id, model.Slot);
        }
    }

    /// <summary>The approval override of <paramref name="sessionId"/>: the run's mode for a model's workspace session not opened as a chat;
    /// null for every other session.</summary>
    public ApprovalOverride? OverrideFor(string sessionId)
    {
        if (store.FindSession(sessionId) is not { } found || found.Model.ChatId is not null) return null;
        var (run, model) = found;
        if (model.WorkspacePath is not { } root || model.WorkingDirectory is not { } cwd)
            return new ApprovalOverride(run.Spec.Approval == ToolApprovalMode.Auto ? ToolApprovalMode.Auto : ToolApprovalMode.Ask, "", "");
        return new ApprovalOverride(run.Spec.Approval, root, cwd) { Grants = run.Grants };
    }

    /// <summary>Wakes whatever waits on <paramref name="modelKey"/> (a budget Ask) to look at the model again.</summary>
    public void Signal(string modelKey)
    {
        if (signals.TryRemove(modelKey, out var signal)) signal.TrySetResult();
    }

    /// <summary>Marks the model stopped by <paramref name="outcome"/> (unless it already has an outcome), wakes its waits and raises
    /// <see cref="LimitReached"/> for a limit; returns the sentence the refused request carries.</summary>
    public string Stop(AgentCompareRun run, string slot, string outcome, string message)
    {
        var updated = store.UpdateModel(run.Id, slot, state => state.Outcome is not null ? state : state with { Outcome = outcome, Message = message, BudgetAsk = null });
        Signal(AgentCompareRun.ModelKey(run.Id, slot));
        var model = updated?.Model(slot);
        if (outcome is AgentModelOutcome.TurnLimit or AgentModelOutcome.TimeLimit or AgentModelOutcome.Budget && model?.Outcome == outcome)
        {
            try { LimitReached?.Invoke(run.Id, slot); }
            catch (Exception error) { log.LogWarning(error, "Agent compare: a limit handler failed for {Run}/{Slot}.", run.Id, slot); }
        }
        return model is null ? message : StopText(run, model);
    }

    /// <summary>The sentence of a turn limit.</summary>
    public static string TurnText(int turns) => $"Agent compare stopped this model: it used all {turns} of its turns.";

    /// <summary>The sentence of a time limit.</summary>
    public static string TimeText(int minutes) => $"Agent compare stopped this model: its {minutes} minute{(minutes == 1 ? "" : "s")} are up.";

    /// <summary>The sentence of a budget stop.</summary>
    public static string BudgetText(long spentMicros, long nextMicros, long shareMicros) =>
        $"Agent compare stopped this model: its next turn could cost up to {SpendMoney.Format(nextMicros)}, which would take it past its " +
        $"{SpendMoney.Format(shareMicros)} share of the run cap ({SpendMoney.Format(spentMicros)} so far).";

    /// <summary>The sentence of a stop by the user.</summary>
    public const string StoppedText = "Agent compare stopped this model: you stopped it.";

    private static string StopText(AgentCompareRun run, AgentCompareModelState model) => model.Message ?? model.Outcome switch
    {
        AgentModelOutcome.Stopped => StoppedText,
        AgentModelOutcome.TurnLimit => TurnText(run.Spec.MaxTurns),
        AgentModelOutcome.TimeLimit => TimeText(run.Spec.MaxMinutes),
        _ => "Agent compare: this model's part of the run has ended.",
    };

    private async Task<string?> BudgetAsync(AgentCompareRun run, AgentCompareModelState model, DurableModel durableModel, ImmutableArray<JsonValue> messages,
        DurableStreamOptions options, CancellationToken cancellationToken)
    {
        if (run.ModelSpec(model.Slot)?.ShareMicros is not { } share || model.BudgetLifted) return null;
        if (services.GetService<ISpendGuard>() is not { } guard || services.GetService<ISpendLedger>() is not { } ledger) return null;
        var key = AgentCompareRun.ModelKey(run.Id, model.Slot);
        long spent, next;
        try
        {
            var decision = await guard.PreflightAsync(GuardedDurableModels.Call(durableModel, GuardedDurableModels.Untag(messages), options), cancellationToken)
                .ConfigureAwait(false);
            if (decision.Estimate.Billing != BillingKind.Metered || decision.Estimate.WorstMicros is not { } worst) return null;
            spent = (await ledger.TotalAsync(new SpendLedgerQuery { Run = key }, cancellationToken).ConfigureAwait(false)).CountedMicros;
            next = worst;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The Spend Guard refuses the request itself when it cannot check it; the share is checked again at the next one.
            log.LogWarning(error, "Agent compare: the budget share of {Key} could not be checked.", key);
            return null;
        }
        if (spent + next <= share) return null;
        if (TakePass(run.Id, model.Slot)) return null;
        if (run.Spec.CapAction == CapAction.Block) return Stop(run, model.Slot, AgentModelOutcome.Budget, BudgetText(spent, next, share));
        store.UpdateModel(run.Id, model.Slot, state => state with { BudgetAsk = new AgentBudgetAsk(spent, next, share, DateTimeOffset.UtcNow) });
        while (true)
        {
            var signal = signals.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            var current = Current(run.Id, model.Slot);
            if (current is null || current.Outcome is not null)
            {
                store.UpdateModel(run.Id, model.Slot, state => state.BudgetAsk is null ? state : state with { BudgetAsk = null });
                return current is null ? "Agent compare: this run is gone." : StopText(run, current);
            }
            if (current.BudgetLifted)
            {
                store.UpdateModel(run.Id, model.Slot, state => state with { BudgetAsk = null });
                return null;
            }
            if (TakePass(run.Id, model.Slot)) return null;
            try
            {
                await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The model was aborted (stopped, or its time ran out) while it waited; the next request asks again.
                store.UpdateModel(run.Id, model.Slot, state => state.BudgetAsk is null ? state : state with { BudgetAsk = null });
                throw;
            }
        }
    }

    private bool TakePass(string run, string slot)
    {
        var taken = false;
        store.UpdateModel(run, slot, state =>
        {
            if (state.BudgetPasses <= 0) return state;
            taken = true;
            return state with { BudgetPasses = state.BudgetPasses - 1, BudgetAsk = null };
        });
        return taken;
    }

    private AgentCompareModelState? Current(string run, string slot) => store.Get(run)?.Model(slot);

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == System.Text.Json.JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : null;

    private static string? Relative(AgentCompareModelState model, string path)
    {
        var text = path.Trim();
        if (text.StartsWith('@')) text = text[1..];
        if (text.Length == 0) return null;
        if (model.WorkingDirectory is not { } cwd) return text.Replace('\\', '/');
        try
        {
            var full = Path.GetFullPath(Path.IsPathFullyQualified(text) ? text : Path.Join(cwd, text));
            var relative = Path.GetRelativePath(cwd, full);
            return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) ? full : relative).Replace('\\', '/');
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return text.Replace('\\', '/');
        }
    }
}
