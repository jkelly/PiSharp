// Decision 0011 (run W3, S3): the Spend Guard for one provider call (ISpendGuard), over the Estimation unit's ICostEstimator and the Ledger
// unit's ISpendLedger. A call is estimated (the worst case: S1's rules) and reserved under its op with every cap checked (S2's ledger, one
// lock and one BEGIN IMMEDIATE transaction). When the ledger refuses it (an Ask or Block cap, or an unknown price set to Ask or Block), the
// guard looks for the user's one-time approval of that run (SpendApprovalStore): "proceed once" lets Ask causes through; "cap the output"
// re-estimates with maxTokens lowered to the user's number and also answers the Ask causes still left (the mockup's sc-period pane), so it
// can make a Block cap fit but never sends over one. The approval is used up by that stopped call whether or not it can then be sent, so
// one answer never lets two requests through. A call that is still refused sends nothing; its decision is recorded as a stop (by its op)
// for the confirm modal, and its error message is SpendErrors' "code: sentence [spend op=…; cap=…]" after the words "Spend Guard billing
// check:" (the durable engine never retries a message that says "billing", and SpendErrors.Parse finds the code after words before it).
// The guard never changes the model, the account or the messages; it only lowers maxTokens when the user chose that.
//
// W3 fix (chat spend): an approval the chat gives for a send belongs to that send's submission (SpendApproval.Submission), and only a call
// that carries it (SpendCall.Submission: its newest user message is that message, placed after the approval) uses it; a call that carries
// it and fits the caps as it is retires it as unneeded. PreflightAsync(call, withApprovals: false) answers whether a call needs the user's
// answer itself, without counting an approval that waits for another send.
//
// Ops and recovery: a durable request keeps its op when it is recovered, and the ledger returns an op's row unchanged whatever its state.
// So the guard walks the op's ledger keys (op, op~2, op~3, ...): a key still reserved or uncertain is that request's own reservation and is
// reused (a recovered request never reserves twice; its uncertain row stays uncertain until the user resolves it), while a key that already
// settled or was released means the request was sent again after it ended (a restart after the answer arrived but before the engine saved
// it), which is a new provider call with a new key. A reused reservation is sent with the output cap its approval chose.
//
// Settling (SettleAsync, from how the call ended): StreamDone with usage -> the actual cost; an error or cancel before any output ->
// released; a cancel after output -> settled at the estimate (the input estimate plus the output seen); a stream that failed or ended
// without a final answer after output (dropped, or finished without reporting usage on a row that counts) -> uncertain, counted until the
// user resolves it. Partial usage reported before such an end (Anthropic reports the input at message_start) is never taken as the actual
// cost; the estimate is only raised to at least what it costs.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.Spend;

/// <summary>A call as the guard reserved it, with what to send.</summary>
/// <param name="Call">The call as asked for.</param>
/// <param name="Reservation">The ledger's answer: the reservation, or the decision that refused it.</param>
public sealed record GuardedReservation(SpendCall Call, SpendReservation Reservation)
{
    /// <summary>The output cap to send (<c>maxTokens</c>): the one the user chose ("cap the output") when it is lower than the call's own,
    /// else the call's own (null when it sets none). The guard never raises it.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The approval this reservation used (or, for a reused reservation, the one it was first sent with), or null.</summary>
    public StoredApproval? Approval { get; init; }

    /// <summary>Whether the call holds a reservation and may be sent.</summary>
    public bool Reserved => Reservation.Reserved;

    /// <summary>The decision.</summary>
    public SpendDecision Decision => Reservation.Decision;

    /// <summary>The ledger op of the reservation (the call's op, or <c>op~n</c> for a later send of it); null when it was refused.</summary>
    public string? LedgerOp => Reservation.Reservation?.Op;

    /// <summary>For a call of a group that was refused although this call fitted: the decision that refused the group; else null.</summary>
    public SpendDecision? GroupDecision { get; init; }

    /// <summary>Whether the output cap was lowered for the user's "cap the output" choice.</summary>
    public bool OutputCapped => MaxTokens is { } sent && (Call.MaxTokens is not { } own || sent < own);

    /// <summary>The error message the call ends with when it was refused (<see cref="SpendGuard.ErrorMessage"/>, of the group's refusal for a
    /// call that fitted a refused group); null when it may be sent.</summary>
    public string? ErrorMessage => Reserved ? null
        : (GroupDecision ?? Decision) is { Allowed: false } refusing ? SpendGuard.ErrorMessage(refusing, Call.Op)
        : SpendGuard.ErrorPrefix + "this call was not reserved, so nothing was sent.";
}

/// <summary>A group of calls (a Compare run's columns) as the guard reserved them, all or none.</summary>
/// <param name="Items">One per call, in order.</param>
public sealed record GuardedGroupReservation(IReadOnlyList<GuardedReservation> Items)
{
    /// <summary>Whether every call holds a reservation.</summary>
    public bool Reserved => Items.Count > 0 && Items.All(item => item.Reserved);

    /// <summary>The first decision that refused the group, or null.</summary>
    public SpendDecision? Refused => Items.Select(item => item.Decision).FirstOrDefault(decision => !decision.Allowed);
}

/// <summary>The Spend Guard (<see cref="ISpendGuard"/>). A singleton; safe to call from many threads.</summary>
public sealed class SpendGuard : ISpendGuard
{
    /// <summary>The words before the spend error code in a stopped call's message: they say what stopped it, and the durable engine never
    /// retries an error that says "billing".</summary>
    public const string ErrorPrefix = "Spend Guard billing check: ";

    /// <summary>The most ledger keys one op may use (op, op~2, ... op~100).</summary>
    public const int MaxSends = 100;

    /// <summary>The longest op a call may have (the ledger key adds at most <c>~100</c>).</summary>
    public const int MaxOpLength = SpendRequest.MaxKeyLength - 4;

    /// <summary>The longest reason the guard writes to the ledger.</summary>
    public const int MaxReasonLength = 200;

    private readonly ICostEstimator estimator;
    private readonly ISpendLedger ledger;
    private readonly SpendApprovalStore approvals;
    private readonly TimeProvider clock;
    private readonly ILogger log;

    /// <summary>A guard over <paramref name="estimator"/>, <paramref name="ledger"/> and <paramref name="approvals"/>.</summary>
    public SpendGuard(ICostEstimator estimator, ISpendLedger ledger, SpendApprovalStore approvals, TimeProvider? time = null, ILogger<SpendGuard>? log = null)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(approvals);
        this.estimator = estimator;
        this.ledger = ledger;
        this.approvals = approvals;
        clock = time ?? TimeProvider.System;
        this.log = log ?? NullLogger<SpendGuard>.Instance;
    }

    /// <summary>The approvals and stops.</summary>
    public SpendApprovalStore Approvals => approvals;

    /// <summary>The estimator.</summary>
    public ICostEstimator Estimator => estimator;

    /// <summary>The ledger.</summary>
    public ISpendLedger Ledger => ledger;

    /// <summary>The message a call stopped by <paramref name="decision"/> ends with: <see cref="ErrorPrefix"/> then
    /// <see cref="SpendErrors.Message"/> (<c>code: sentence [spend op=…; cap=…]</c>).</summary>
    /// <exception cref="ArgumentException">The decision allows the call.</exception>
    public static string ErrorMessage(SpendDecision decision, string? op) => ErrorPrefix + SpendErrors.Message(decision, op);

    /// <summary>The message of a call the guard could not check (its estimate or the ledger failed): nothing was sent.</summary>
    public static string UncheckedMessage(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ErrorPrefix + "this request could not be checked against your spend caps, so nothing was sent (" + Reason(error) + "). Try again.";
    }

    /// <inheritdoc/>
    public Task<SpendDecision> PreflightAsync(SpendCall call, CancellationToken cancellationToken = default) =>
        PreflightAsync(call, withApprovals: true, cancellationToken);

    /// <summary><see cref="PreflightAsync(SpendCall, CancellationToken)"/>; with <paramref name="withApprovals"/> false, an approval that
    /// waits for the run is not counted: the answer is whether the call needs the user's answer itself (the chat's check of a send the user
    /// has not answered, which an approval given for another send never answers).</summary>
    public async Task<SpendDecision> PreflightAsync(SpendCall call, bool withApprovals, CancellationToken cancellationToken = default)
    {
        var checkedCall = Check(call, requireOp: false);
        var estimate = await estimator.EstimateAsync(checkedCall.EstimateInput, cancellationToken).ConfigureAwait(false);
        var key = checkedCall.Op.Length == 0 ? "preflight-" + Guid.NewGuid().ToString("N") : (await LedgerKeyAsync(checkedCall.Op, cancellationToken).ConfigureAwait(false)).Key;
        var first = await ledger.CheckAsync(Request(checkedCall, key, estimate, checkedCall.Approved, null), cancellationToken).ConfigureAwait(false);
        if (first.Allowed || !withApprovals) return first;
        if (await approvals.PeekAsync(checkedCall.Run, Blank(checkedCall.Op), checkedCall.Submission, checkedCall.SubmittedAt, cancellationToken).ConfigureAwait(false)
            is not { } approval) return first;
        var (answered, _) = Apply(estimate, approval.Approval);
        return await ledger.CheckAsync(Request(checkedCall, key, answered, approved: true, null), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SpendReservation> ReserveAsync(SpendCall call, CancellationToken cancellationToken = default) =>
        (await ReserveCallAsync(call, cancellationToken).ConfigureAwait(false)).Reservation;

    /// <summary><see cref="ReserveAsync"/> with what to send: the output cap and the approval used.</summary>
    /// <exception cref="ArgumentException"><paramref name="call"/> has no op, or a value is not allowed.</exception>
    public async Task<GuardedReservation> ReserveCallAsync(SpendCall call, CancellationToken cancellationToken = default)
    {
        var checkedCall = Check(call, requireOp: true);
        var estimate = await EstimateAsync(checkedCall.EstimateInput, cancellationToken).ConfigureAwait(false);
        var (key, existing) = await LedgerKeyAsync(checkedCall.Op, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return await ReusedAsync(checkedCall, estimate, existing, cancellationToken).ConfigureAwait(false);
        var first = await ledger.ReserveAsync(Request(checkedCall, key, estimate, checkedCall.Approved, null), cancellationToken).ConfigureAwait(false);
        if (first.Reserved)
        {
            if (!first.Existing) await RetireUnneededAsync(checkedCall, cancellationToken).ConfigureAwait(false);
            return first.Existing ? await ReusedAsync(checkedCall, estimate, first.Reservation!, cancellationToken).ConfigureAwait(false)
                : new GuardedReservation(checkedCall, first) { MaxTokens = checkedCall.MaxTokens };
        }
        if (await approvals.TakeAsync(checkedCall.Run, Blank(checkedCall.Op), checkedCall.Submission, checkedCall.SubmittedAt, key, cancellationToken).ConfigureAwait(false)
            is not { } approval)
        {
            await StopAsync(checkedCall, first.Decision, cancellationToken).ConfigureAwait(false);
            return new GuardedReservation(checkedCall, first);
        }
        var (answered, note) = Apply(estimate, approval.Approval);
        var second = await ledger.ReserveAsync(Request(checkedCall, key, answered, approved: true, note), cancellationToken).ConfigureAwait(false);
        if (!second.Reserved)
        {
            await approvals.MarkUsedByAsync(approval.Id, SpendApprovalStore.Refused, cancellationToken).ConfigureAwait(false);
            await StopAsync(checkedCall, second.Decision, cancellationToken).ConfigureAwait(false);
            return new GuardedReservation(checkedCall, second) { Approval = approval with { UsedBy = SpendApprovalStore.Refused } };
        }
        log.LogInformation("Spend Guard: {Op} sent after the user's approval ({Choice}).", key, SpendApprovalStore.Wire(approval.Approval.Choice));
        return new GuardedReservation(checkedCall, second) { MaxTokens = Sent(checkedCall.MaxTokens, answered), Approval = approval };
    }

    /// <summary>Reserves every call of <paramref name="calls"/> or none (a Compare run's columns): the ledger checks each with the group's
    /// earlier ones counted. When the group is refused, the run's one-time approval (of the run, or of the first call's op) answers the whole
    /// group once, as for one call, and is recorded against every call it lets through. A call whose reservation still stands (a recovered
    /// one) is reused, and sent with the output cap of the approval that let it through.</summary>
    /// <exception cref="ArgumentException">The group is empty, a call has no op, two calls share an op, or the calls are of different runs.</exception>
    public async Task<GuardedGroupReservation> ReserveGroupAsync(IReadOnlyList<SpendCall> calls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0) throw new ArgumentException("A group needs at least one call.", nameof(calls));
        var checkedCalls = calls.Select(call => Check(call, requireOp: true)).ToList();
        if (checkedCalls.GroupBy(call => call.Op, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } shared)
            throw new ArgumentException($"Two calls of the group share the op {shared.Key}.", nameof(calls));
        var run = checkedCalls[0].Run;
        if (checkedCalls.Any(call => call.Run != run)) throw new ArgumentException("Every call of a group belongs to one run.", nameof(calls));
        var estimates = new List<SpendEstimate>();
        var keys = new List<string>();
        foreach (var call in checkedCalls)
        {
            estimates.Add(await EstimateAsync(call.EstimateInput, cancellationToken).ConfigureAwait(false));
            keys.Add((await LedgerKeyAsync(call.Op, cancellationToken).ConfigureAwait(false)).Key);
        }
        List<SpendRequest> Requests(IReadOnlyList<SpendEstimate> priced, bool approved, IReadOnlyList<string?> notes) =>
            [.. checkedCalls.Select((call, index) => Request(call, keys[index], priced[index], approved || call.Approved, notes[index]))];
        var none = checkedCalls.Select(_ => (string?)null).ToList();

        var first = await ledger.ReserveGroupAsync(Requests(estimates, false, none), cancellationToken).ConfigureAwait(false);
        if (first.Reserved) return await GroupAsync(checkedCalls, estimates, first, null, cancellationToken).ConfigureAwait(false);
        // The approval is recorded against every column it lets through (each new reservation's ledger key), so each recovered column is
        // resent with its output cap; a column whose reservation already stood keeps the approval it was first sent with. A refused group
        // always has a new column (the ledger allows a reservation that already stands).
        var fresh = keys.Where((_, index) => !first.Items[index].Existing).ToList();
        if (await approvals.TakeAsync(run, checkedCalls[0].Op, fresh.Count > 0 ? fresh : keys, cancellationToken).ConfigureAwait(false) is not { } approval)
        {
            await StopGroupAsync(checkedCalls, first, cancellationToken).ConfigureAwait(false);
            return await GroupAsync(checkedCalls, estimates, first, null, cancellationToken).ConfigureAwait(false);
        }
        var answered = estimates.Select(estimate => Apply(estimate, approval.Approval)).ToList();
        var priced = answered.Select(pair => pair.Estimate).ToList();
        var second = await ledger.ReserveGroupAsync(Requests(priced, true, [.. answered.Select(pair => (string?)pair.Note)]), cancellationToken).ConfigureAwait(false);
        if (!second.Reserved)
        {
            await approvals.MarkUsedByAsync(approval.Id, SpendApprovalStore.Refused, cancellationToken).ConfigureAwait(false);
            await StopGroupAsync(checkedCalls, second, cancellationToken).ConfigureAwait(false);
            return await GroupAsync(checkedCalls, priced, second, approval with { UsedBy = SpendApprovalStore.Refused }, cancellationToken).ConfigureAwait(false);
        }
        log.LogInformation("Spend Guard: the group of {Run} sent after the user's approval ({Choice}).", run, SpendApprovalStore.Wire(approval.Approval.Choice));
        return await GroupAsync(checkedCalls, priced, second, approval, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Reservation?> SettleAsync(string op, SpendOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (Blank(op) is not { } key) throw new ArgumentException("Name the reservation's op.", nameof(op));
        try
        {
            var row = await ledger.GetAsync(key, cancellationToken).ConfigureAwait(false);
            // The first ending wins; an uncertain row (a recovered request's) changes only when the user resolves it.
            if (row is null || row.State != ReservationState.Reserved) return row;
            var estimate = EstimateMicros(row, outcome.OutputTokensSeen);
            // A call that ended after output may carry the partial usage the provider reported before the end: never estimate below it.
            if (outcome.Kind is SpendOutcomeKind.CancelledAfterOutput or SpendOutcomeKind.EndedWithoutUsage) estimate = Math.Max(estimate, ReportedMicros(row, outcome.Usage));
            switch (outcome.Kind)
            {
                case SpendOutcomeKind.Completed:
                    var usage = outcome.Usage ?? throw new ArgumentException("A completed call needs its usage.", nameof(outcome));
                    if (row.CountsTowardCaps && !usage.HasTokens && usage.CostMicros is null or 0)
                        return await ledger.MarkUncertainAsync(key, Cut(outcome.Reason) ?? "the answer finished without reporting usage", estimate, cancellationToken).ConfigureAwait(false);
                    return await ledger.SettleAsync(key, usage, cancellationToken).ConfigureAwait(false);
                case SpendOutcomeKind.FailedBeforeOutput:
                    return await ledger.ReleaseAsync(key, Cut(outcome.Reason) ?? "failed before any output", cancellationToken).ConfigureAwait(false);
                case SpendOutcomeKind.CancelledAfterOutput:
                    return await ledger.SettleAtEstimateAsync(key, estimate, Cut(outcome.Reason) ?? "cancelled after output", cancellationToken).ConfigureAwait(false);
                case SpendOutcomeKind.EndedWithoutUsage:
                    return await ledger.MarkUncertainAsync(key, Cut(outcome.Reason) ?? SpendLedger.RecoveryReason, estimate, cancellationToken).ConfigureAwait(false);
                default:
                    throw new ArgumentException($"Unknown outcome {(int)outcome.Kind}.", nameof(outcome));
            }
        }
        catch (SpendException missing) when (missing.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<SpendApproval> ApproveAsync(SpendApproval approval, CancellationToken cancellationToken = default) =>
        (await ApproveStoredAsync(approval, cancellationToken).ConfigureAwait(false)).Approval;

    /// <summary><see cref="ApproveAsync"/> with the stored row (its id and expiry).</summary>
    /// <exception cref="ArgumentException">The approval is not allowed.</exception>
    public async Task<StoredApproval> ApproveStoredAsync(SpendApproval approval, CancellationToken cancellationToken = default)
    {
        var stored = await approvals.AddAsync(approval, cancellationToken).ConfigureAwait(false);
        log.LogInformation("Spend Guard: the user approved the next stopped call of {Run} once ({Choice}).", stored.Approval.Run, SpendApprovalStore.Wire(stored.Approval.Choice));
        return stored;
    }

    /// <summary>The decision on the call of <paramref name="op"/> that the guard stopped, or null.</summary>
    public Task<SpendStop?> StoppedAsync(string op, CancellationToken cancellationToken = default) => approvals.StopAsync(op, cancellationToken);

    /// <summary>What the guard prices <paramref name="input"/> at for a reservation: the estimator's worst case, without the typical cost
    /// (no ledger read) when it is the <see cref="CostEstimator"/>; "typical" is shown, never reserved.</summary>
    private async ValueTask<SpendEstimate> EstimateAsync(SpendEstimateInput input, CancellationToken cancellationToken) =>
        estimator is CostEstimator cost ? cost.Estimate(input) : await estimator.EstimateAsync(input, cancellationToken).ConfigureAwait(false);

    /// <summary>The ledger key of the next send of <paramref name="op"/>, and its row when that send's reservation still stands.</summary>
    private async Task<(string Key, Reservation? Existing)> LedgerKeyAsync(string op, CancellationToken cancellationToken)
    {
        for (var send = 1; send <= MaxSends; send++)
        {
            var key = send == 1 ? op : $"{op}~{send}";
            var row = await ledger.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (row is null || row.State is ReservationState.Reserved or ReservationState.Uncertain) return (key, row);
        }
        throw new InvalidOperationException($"The op {op} was sent {MaxSends} times; the Spend Guard sends it no more.");
    }

    /// <summary>A reservation that still stands for <paramref name="call"/>'s op: returned unchanged, sent with the output cap its approval chose.</summary>
    private async Task<GuardedReservation> ReusedAsync(SpendCall call, SpendEstimate estimate, Reservation existing, CancellationToken cancellationToken)
    {
        var approval = await approvals.UsedByAsync(existing.Op, cancellationToken).ConfigureAwait(false);
        var maxTokens = call.MaxTokens;
        if (approval?.Approval is { Choice: SpendChoice.CapOutput, MaxOutputTokens: { } chosen } && (maxTokens is not { } own || chosen < own)) maxTokens = chosen;
        return new GuardedReservation(call, new SpendReservation(SpendDecision.Allow(estimate), existing, Existing: true)) { MaxTokens = maxTokens, Approval = approval };
    }

    private async Task<GuardedGroupReservation> GroupAsync(IReadOnlyList<SpendCall> calls, IReadOnlyList<SpendEstimate> estimates, SpendGroupReservation group,
        StoredApproval? approval, CancellationToken cancellationToken)
    {
        var items = new List<GuardedReservation>();
        for (var index = 0; index < calls.Count; index++)
        {
            var item = group.Items[index];
            if (item.Existing && item.Reservation is { } existing)
            {
                items.Add(await ReusedAsync(calls[index], estimates[index], existing, cancellationToken).ConfigureAwait(false));
                continue;
            }
            items.Add(new GuardedReservation(calls[index], item)
            {
                MaxTokens = item.Reserved ? Sent(calls[index].MaxTokens, item.Decision.Estimate) : calls[index].MaxTokens,
                Approval = approval,
                GroupDecision = !item.Reserved && item.Decision.Allowed ? group.Refused : null,
            });
        }
        return new GuardedGroupReservation(items);
    }

    /// <summary>The estimate answered by <paramref name="approval"/>, and the ledger note that says so.</summary>
    private static (SpendEstimate Estimate, string Note) Apply(SpendEstimate estimate, SpendApproval approval)
    {
        if (approval.Choice == SpendChoice.CapOutput && approval.MaxOutputTokens is { } chosen && chosen < estimate.OutputTokens)
            return (estimate.WithOutputTokens(chosen), $"asked · output capped at {chosen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} tokens");
        return (estimate, "asked · sent once");
    }

    /// <summary>The <c>maxTokens</c> to send: the estimate's output tokens when the user chose them (lower than the call's own), else the call's.</summary>
    private static int? Sent(int? own, SpendEstimate estimate) =>
        estimate.OutputBasis == OutputTokenBasis.Chosen && (own is not { } requested || estimate.OutputTokens < requested) ? estimate.OutputTokens : own;

    private static SpendRequest Request(SpendCall call, string key, SpendEstimate estimate, bool approved, string? note) => new()
    {
        Op = key,
        Source = call.Source,
        Run = call.Run,
        Estimate = estimate,
        Approved = approved,
        Note = (call.Note, note) switch
        {
            (null, var answer) => answer,
            (var own, null) => own,
            var (own, answer) => own + " · " + answer,
        },
    };

    /// <summary>Retires the approvals given for the submission <paramref name="call"/> answers (it was reserved without one, so they are not
    /// needed and must not answer a later call); best effort.</summary>
    private async Task RetireUnneededAsync(SpendCall call, CancellationToken cancellationToken)
    {
        if (call.Submission is null) return;
        try
        {
            if (await approvals.RetireSubmissionAsync(call.Run, call.Submission, call.SubmittedAt, cancellationToken).ConfigureAwait(false) > 0)
                log.LogInformation("Spend Guard: {Op} fitted the caps as it was; the user's approval of its message was not needed.", call.Op);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The call holds its reservation either way; the approval then waits until it expires, and answers only its own message.
            log.LogWarning(error, "Spend Guard: the unneeded approval of {Op}'s message could not be retired.", call.Op);
        }
    }

    private async Task StopAsync(SpendCall call, SpendDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            await approvals.RecordStopAsync(new SpendStop(call.Op, call, decision, Now()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The call is stopped either way; only the confirm modal's details are missing.
            log.LogWarning(error, "Spend Guard: the stop of {Op} could not be recorded.", call.Op);
        }
        log.LogInformation("Spend Guard: {Op} ({Provider}/{Model}) not sent: {Code}.", call.Op, call.Provider, call.Model, decision.ErrorCode);
    }

    private async Task StopGroupAsync(IReadOnlyList<SpendCall> calls, SpendGroupReservation group, CancellationToken cancellationToken)
    {
        for (var index = 0; index < calls.Count; index++)
            if (!group.Items[index].Decision.Allowed) await StopAsync(calls[index], group.Items[index].Decision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What <paramref name="row"/> cost with <paramref name="outputTokens"/> output tokens seen: its input estimate plus that output at
    /// its rates, rounded up; 0 without a price.</summary>
    private static long EstimateMicros(Reservation row, double outputTokens) =>
        row.Price is { } price ? price.WorstMicros(row.InputTokens, double.IsFinite(outputTokens) && outputTokens > 0 ? outputTokens : 0) : 0;

    /// <summary>What the partial <paramref name="usage"/> a provider reported before a call ended costs for <paramref name="row"/>, as the
    /// ledger prices a settled answer (the reported cost, else the tokens at the row's rates; an entered price always at its rates); 0 without
    /// usage, without a price, or for usage that is not a count.</summary>
    private static long ReportedMicros(Reservation row, SpendUsage? usage)
    {
        if (usage is null || row.Price is not { } price) return 0;
        if (new[] { usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens }.Any(tokens => !double.IsFinite(tokens) || tokens < 0)) return 0;
        if (price.Source != SpendPriceSource.Entered && usage.CostMicros is { } reported and > 0) return reported;
        return Math.Max(0, price.CostMicros(usage));
    }

    /// <summary><paramref name="call"/> trimmed and checked.</summary>
    private static SpendCall Check(SpendCall call, bool requireOp)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.Provider) || string.IsNullOrWhiteSpace(call.Model)) throw new ArgumentException("A call needs a provider and a model.");
        if (!Enum.IsDefined(call.Source)) throw new ArgumentException($"Unknown source {(int)call.Source}.");
        if (call.InputBytes < 0) throw new ArgumentException("A request's size is never negative.");
        if (call.MaxTokens is < 1) throw new ArgumentException("maxTokens must be at least 1.");
        var op = call.Op?.Trim() ?? "";
        if (requireOp && op.Length == 0) throw new ArgumentException("A call needs an op (its operation key).");
        if (op.Length > MaxOpLength) throw new ArgumentException($"An op is at most {MaxOpLength} characters.");
        var run = Blank(call.Run);
        if (run?.Length > SpendRequest.MaxKeyLength) throw new ArgumentException($"A run key is at most {SpendRequest.MaxKeyLength} characters.");
        var submission = Blank(call.Submission);
        if (submission?.Length > SpendRequest.MaxKeyLength) throw new ArgumentException($"A submission key is at most {SpendRequest.MaxKeyLength} characters.");
        return call with
        {
            Provider = call.Provider.Trim(), Model = call.Model.Trim(), Account = Blank(call.Account), Run = run, Op = op, Note = Blank(call.Note),
            Submission = submission, SubmittedAt = submission is null ? null : call.SubmittedAt,
        };
    }

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A reason for the ledger: scrubbed (no token-like run, no JSON body) and cut.</summary>
    internal static string? Cut(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var text = SecretText.Scrub(reason);
        return text.Length <= MaxReasonLength ? text : text[..(MaxReasonLength - 1)] + "…";
    }

    /// <summary>A short, scrubbed description of <paramref name="error"/> for a reason or message.</summary>
    internal static string Reason(Exception error) => Cut(error.Message) is { } message ? $"{error.GetType().Name}: {message}" : error.GetType().Name;
}
