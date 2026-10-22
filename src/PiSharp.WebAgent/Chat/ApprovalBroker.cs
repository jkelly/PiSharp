// Decision 0011 (run W2, WU7): the in-process meeting point of the web-approval extension and the pages. A tool call that needs approval
// registers here (keyed by session, agent and tool task) and waits; every tab lists the same pending approvals; the first decision completes
// the wait and later ones get "already decided". The hook then stores the decision as the call's durable memo, and DecideAsync answers once
// that memo is stored, so a decision the page saw accepted survives a crash. Nothing here is durable: after a restart the hook runs again,
// finds no memo for an undecided call and registers it again ("a crash while waiting asks again"). The broker also keeps the chats' grants
// ("Always allow bash in this chat"), which last until the app restarts, as Settings › Tools says.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Chat;

/// <summary>A tool call waiting for approval.</summary>
/// <param name="SessionId">The chat (durable session).</param>
/// <param name="AgentId">The agent (conversation) that made the call.</param>
/// <param name="TaskId">The call's tool task; unique within the session.</param>
/// <param name="CallId">The model's tool call id, which the transcript's tool card carries.</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Arguments">The arguments the call would run with.</param>
/// <param name="ParentCallId">For a nested call, the call that made it.</param>
public sealed record ApprovalRequest(string SessionId, long AgentId, long TaskId, string CallId, string Tool, JsonObject Arguments, string? ParentCallId = null)
{
    /// <summary>The broker key: <c>session/agent/task</c>.</summary>
    public string Key => $"{SessionId}/{AgentId}/{TaskId}";

    /// <summary>The command of a bash or powershell call, else null.</summary>
    public string? Command => ToolAllowList.CommandOf(Tool, Arguments);
}

/// <summary>A pending approval as the pages list it.</summary>
/// <param name="Request">The call.</param>
/// <param name="Since">When it started waiting.</param>
public sealed record PendingApproval(ApprovalRequest Request, DateTimeOffset Since);

/// <summary>What the user decided.</summary>
public enum ApprovalChoice
{
    /// <summary>Run this call.</summary>
    AllowOnce,

    /// <summary>Block this call; the model gets the reason.</summary>
    Deny,

    /// <summary>Run it, and every call of this tool in this chat until the app restarts.</summary>
    AllowChat,

    /// <summary>Run it, and add it to Settings › Tools' allow-list (the exact command for bash, else the tool); this chat stops asking for it
    /// at once, in every mode.</summary>
    AllowAlways,
}

/// <summary>A decision.</summary>
/// <param name="Choice">What was chosen.</param>
/// <param name="Reason">Why it was denied, sent to the model with the block; optional.</param>
public sealed record ApprovalDecision(ApprovalChoice Choice, string? Reason = null)
{
    /// <summary>Whether the call runs.</summary>
    public bool Allows => Choice != ApprovalChoice.Deny;

    /// <summary>The wire name: <c>allow</c>, <c>deny</c>, <c>allowChat</c> or <c>allowAlways</c>.</summary>
    public string Name => Names[Choice];

    /// <summary>The wire names.</summary>
    public static IReadOnlyDictionary<ApprovalChoice, string> Names { get; } = new Dictionary<ApprovalChoice, string>
    {
        [ApprovalChoice.AllowOnce] = "allow",
        [ApprovalChoice.Deny] = "deny",
        [ApprovalChoice.AllowChat] = "allowChat",
        [ApprovalChoice.AllowAlways] = "allowAlways",
    };

    /// <summary>The choice with wire name <paramref name="name"/>, or null.</summary>
    public static ApprovalChoice? Parse(string? name) => Names.FirstOrDefault(pair => string.Equals(pair.Value, name, StringComparison.Ordinal)) is
        { Value: not null } found ? found.Key : null;
}

/// <summary>How a decision went.</summary>
public enum ApprovalOutcomeStatus
{
    /// <summary>This decision completed the approval.</summary>
    Decided,

    /// <summary>An earlier decision won; <see cref="ApprovalOutcome.Decision"/> is that one.</summary>
    AlreadyDecided,

    /// <summary>Nothing waits under that key (the run was stopped, or it never asked).</summary>
    NotPending,
}

/// <summary>The result of <see cref="ApprovalBroker.DecideAsync"/>.</summary>
/// <param name="Status">How it went.</param>
/// <param name="Decision">The winning decision, when there is one.</param>
/// <param name="Request">The call, when known.</param>
public sealed record ApprovalOutcome(ApprovalOutcomeStatus Status, ApprovalDecision? Decision, ApprovalRequest? Request);

/// <summary>Pending tool approvals and per-chat grants. A singleton; safe to call from many threads.</summary>
public sealed class ApprovalBroker(TimeProvider? time = null)
{
    /// <summary>How many settled keys are remembered for "already decided".</summary>
    public const int RememberedDecisions = 1000;

    /// <summary>How long <see cref="DecideAsync"/> waits for the hook to store the decision before it answers anyway.</summary>
    public static TimeSpan StoreWait { get; } = TimeSpan.FromSeconds(10);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Waiter> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ApprovalDecision Decision, ApprovalRequest Request)> decided = new(StringComparer.Ordinal);
    private readonly Queue<string> decidedOrder = new();
    private readonly Dictionary<string, ImmutableList<ToolAllowRule>> grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Waiter> settling = new(StringComparer.Ordinal);

    /// <summary>Raised after a session's pending approvals or grants changed, outside any lock, with the session id.</summary>
    public event Action<string>? Changed;

    /// <summary>The pending approvals of <paramref name="sessionId"/> (or of every session for null), oldest first.</summary>
    public IReadOnlyList<PendingApproval> Pending(string? sessionId = null)
    {
        lock (gate)
            return [.. pending.Values.Where(waiter => sessionId is null || waiter.Request.SessionId == sessionId)
                .OrderBy(waiter => waiter.Since).ThenBy(waiter => waiter.Request.TaskId).Select(waiter => new PendingApproval(waiter.Request, waiter.Since))];
    }

    /// <summary>The pending approval of tool task <paramref name="taskId"/> in <paramref name="sessionId"/>, or null.</summary>
    public PendingApproval? Find(string sessionId, long taskId)
    {
        lock (gate)
            return pending.Values.FirstOrDefault(waiter => waiter.Request.SessionId == sessionId && waiter.Request.TaskId == taskId) is { } waiter
                ? new PendingApproval(waiter.Request, waiter.Since) : null;
    }

    /// <summary>Registers <paramref name="request"/> and waits for the first decision. Cancelling <paramref name="cancellationToken"/> (the
    /// run stopped, or the app is closing) withdraws it. After the hook stores the decision it calls <see cref="Stored"/>.</summary>
    public async Task<ApprovalDecision> WaitAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter(request, clock.GetUtcNow());
        lock (gate)
        {
            if (pending.Remove(request.Key, out var stale)) stale.Decision.TrySetCanceled();
            ForgetDecision(request.Key);
            pending[request.Key] = waiter;
        }
        Changed?.Invoke(request.SessionId);
        using var registration = cancellationToken.Register(() => Withdraw(waiter));
        return await waiter.Decision.Task.ConfigureAwait(false);
    }

    /// <summary>Called by the hook once the decision for <paramref name="request"/> is stored durably.</summary>
    public void Stored(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Waiter? waiter;
        lock (gate) waiter = settling.TryGetValue(request.Key, out var found) ? found : null;
        waiter?.Stored.TrySetResult();
    }

    /// <summary>Decides tool task <paramref name="taskId"/> of <paramref name="sessionId"/>. The first decision wins: it completes the wait
    /// and is answered once the hook stored it (or after <see cref="StoreWait"/>); a later one gets <see cref="ApprovalOutcomeStatus.AlreadyDecided"/>
    /// with the winner.</summary>
    public async Task<ApprovalOutcome> DecideAsync(string sessionId, long taskId, ApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Waiter? waiter;
        lock (gate)
        {
            waiter = pending.Values.FirstOrDefault(item => item.Request.SessionId == sessionId && item.Request.TaskId == taskId);
            if (waiter is null)
            {
                var earlier = decided.FirstOrDefault(pair => pair.Value.Request.SessionId == sessionId && pair.Value.Request.TaskId == taskId);
                return earlier.Key is null
                    ? new ApprovalOutcome(ApprovalOutcomeStatus.NotPending, null, null)
                    : new ApprovalOutcome(ApprovalOutcomeStatus.AlreadyDecided, earlier.Value.Decision, earlier.Value.Request);
            }
            pending.Remove(waiter.Request.Key);
            Remember(waiter.Request.Key, decision, waiter.Request);
            settling[waiter.Request.Key] = waiter;
        }
        waiter.Decision.TrySetResult(decision);
        Changed?.Invoke(sessionId);
        try { await waiter.Stored.Task.WaitAsync(StoreWait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { /* The hook stores it soon or the run stopped; the decision stands. */ }
        finally
        {
            lock (gate) settling.Remove(waiter.Request.Key);
        }
        return new ApprovalOutcome(ApprovalOutcomeStatus.Decided, decision, waiter.Request);
    }

    /// <summary>The grants of <paramref name="sessionId"/>: calls this chat runs without asking until the app restarts.</summary>
    public IReadOnlyList<ToolAllowRule> Grants(string sessionId)
    {
        lock (gate) return grants.TryGetValue(sessionId, out var list) ? list : [];
    }

    /// <summary>Every chat's grants, by session id (for Settings › Tools' "Always allowed this session").</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ToolAllowRule>> AllGrants()
    {
        lock (gate) return grants.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ToolAllowRule>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Adds grants to <paramref name="sessionId"/>, then allows its pending calls they cover (as "allow once" for each).</summary>
    public async Task GrantAsync(string sessionId, IEnumerable<ToolAllowRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        List<PendingApproval> covered;
        lock (gate)
        {
            var list = grants.TryGetValue(sessionId, out var current) ? current : [];
            foreach (var rule in rules) if (!list.Contains(rule)) list = list.Add(rule);
            grants[sessionId] = list;
            covered = [.. pending.Values.Where(waiter => waiter.Request.SessionId == sessionId && ToolAllowList.Allows(list, waiter.Request.Tool, waiter.Request.Arguments))
                .Select(waiter => new PendingApproval(waiter.Request, waiter.Since))];
        }
        Changed?.Invoke(sessionId);
        foreach (var approval in covered)
            await DecideAsync(sessionId, approval.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce, "Allowed by this chat's grant."), cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Removes a grant; false when the chat did not have it.</summary>
    public bool Revoke(string sessionId, ToolAllowRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        bool removed;
        lock (gate)
        {
            removed = grants.TryGetValue(sessionId, out var list) && list.Contains(rule);
            if (removed) grants[sessionId] = list!.Remove(rule);
        }
        if (removed) Changed?.Invoke(sessionId);
        return removed;
    }

    /// <summary>Drops a chat's grants and pending approvals (it was deleted; its runs stopped).</summary>
    public void Forget(string sessionId, bool notify)
    {
        List<Waiter> dropped;
        lock (gate)
        {
            grants.Remove(sessionId);
            dropped = [.. pending.Values.Where(waiter => waiter.Request.SessionId == sessionId)];
            foreach (var waiter in dropped) pending.Remove(waiter.Request.Key);
        }
        foreach (var waiter in dropped) waiter.Decision.TrySetCanceled();
        if (notify) Changed?.Invoke(sessionId);
    }

    private void Withdraw(Waiter waiter)
    {
        bool removed;
        lock (gate) removed = pending.TryGetValue(waiter.Request.Key, out var current) && ReferenceEquals(current, waiter) && pending.Remove(waiter.Request.Key);
        waiter.Decision.TrySetCanceled();
        if (removed) Changed?.Invoke(waiter.Request.SessionId);
    }

    private void Remember(string key, ApprovalDecision decision, ApprovalRequest request)
    {
        decided[key] = (decision, request);
        decidedOrder.Enqueue(key);
        while (decidedOrder.Count > RememberedDecisions && decidedOrder.TryDequeue(out var oldest)) decided.Remove(oldest);
    }

    private void ForgetDecision(string key) => decided.Remove(key);

    private sealed class Waiter(ApprovalRequest request, DateTimeOffset since)
    {
        public ApprovalRequest Request { get; } = request;
        public DateTimeOffset Since { get; } = since;
        public TaskCompletionSource<ApprovalDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
