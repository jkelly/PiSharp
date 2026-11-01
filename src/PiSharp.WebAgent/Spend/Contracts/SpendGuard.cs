// Decision 0011 (run W3, S0): the guard around one provider call, the contract of the Guard unit. For durable calls the guard is a decorator
// over the app's final IDurableModels that reads metadata.pisharp (account, op, source, run; SpendMetadata); for non-durable calls (W4's
// Playground, Compare and Bench) it is the same rules through a GuardedRunExecutor. Each call is estimated, reserved under its op, then
// settled from how it ended: StreamDone with usage -> the actual cost; an error or cancel before the first token -> released; a cancel
// after tokens -> the estimate; a stream that ends without usage -> uncertain. A Block cap ends the call with spend_blocked; an Ask cap
// with spend_ask (or spend_unknown_price) unless the user approved it once. Preflight answers "what would happen if I sent this now"
// without reserving, for the composer's estimate and the confirm modal. The guard never changes the model, the account or the messages.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Spend;

/// <summary>One provider call to preflight or reserve.</summary>
public sealed record SpendCall
{
    /// <summary>The provider id.</summary>
    public required string Provider { get; init; }

    /// <summary>The model id.</summary>
    public required string Model { get; init; }

    /// <summary>The account id, or null when the provider needs none.</summary>
    public string? Account { get; init; }

    /// <summary>Where the call was started.</summary>
    public required SpendSource Source { get; init; }

    /// <summary>The run key (a chat id, a run id), or null.</summary>
    public string? Run { get; init; }

    /// <summary>The idempotent operation key (a durable call's <c>metadata.pisharp.op</c>; a new one per call otherwise). A preflight may
    /// leave it empty.</summary>
    public string Op { get; init; } = "";

    /// <summary>The request's size in UTF-8 bytes (system prompt, messages, tools).</summary>
    public long InputBytes { get; init; }

    /// <summary>The request's <c>maxTokens</c>, or null when it sets none.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Whether the user approved this call once (it passes Ask causes; Block still stops it).</summary>
    public bool Approved { get; init; }

    /// <summary>A short note for the ledger, or null.</summary>
    public string? Note { get; init; }

    /// <summary>The submission the request answers: the key of the newest user message it sends (a durable generation request's; see
    /// <c>GuardedDurableModels.SubmissionKey</c>), or null (a compaction summary, a non-durable call, a preflight). A one-time approval of a
    /// chat send (<see cref="SpendApproval.Submission"/>) answers only a call that carries that send's key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Submission { get; init; }

    /// <summary>When the newest user message the request sends was placed (UTC Unix milliseconds), with <see cref="Submission"/>; null
    /// otherwise. An approval of a send answers only a message placed at or after the approval was given.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SubmittedAt { get; init; }

    /// <summary>What the estimator prices.</summary>
    [JsonIgnore]
    public SpendEstimateInput EstimateInput => new(Provider, Model, Account, InputBytes, MaxTokens);
}

/// <summary>How a provider call ended. Sent as <c>completed</c>, <c>failed-before-output</c>, <c>cancelled-after-output</c>,
/// <c>ended-without-usage</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpendOutcomeKind>))]
public enum SpendOutcomeKind
{
    /// <summary><c>StreamDone</c> with usage: settle at the actual cost.</summary>
    [JsonStringEnumMemberName("completed")] Completed,

    /// <summary>An error or a cancel before the first token: release.</summary>
    [JsonStringEnumMemberName("failed-before-output")] FailedBeforeOutput,

    /// <summary>A cancel after tokens arrived: settle at the estimate (input estimate + output tokens seen).</summary>
    [JsonStringEnumMemberName("cancelled-after-output")] CancelledAfterOutput,

    /// <summary>The stream ended without reporting usage (dropped): uncertain, never settled automatically.</summary>
    [JsonStringEnumMemberName("ended-without-usage")] EndedWithoutUsage,
}

/// <summary>How a call ended, for the guard to settle its reservation.</summary>
/// <param name="Kind">How it ended.</param>
public sealed record SpendOutcome(SpendOutcomeKind Kind)
{
    /// <summary>What the provider reported: for <see cref="SpendOutcomeKind.Completed"/>, the actual usage; for
    /// <see cref="SpendOutcomeKind.CancelledAfterOutput"/> and <see cref="SpendOutcomeKind.EndedWithoutUsage"/>, the partial usage it reported
    /// before the end (never the actual cost: the estimate is at least what it costs), or null.</summary>
    public SpendUsage? Usage { get; init; }

    /// <summary>About how many output tokens arrived before the end (for the estimate).</summary>
    public double OutputTokensSeen { get; init; }

    /// <summary>Why it failed or dropped, for the ledger (no secret), or null.</summary>
    public string? Reason { get; init; }

    /// <summary>A finished call.</summary>
    public static SpendOutcome Completed(SpendUsage usage) => new(SpendOutcomeKind.Completed) { Usage = usage ?? throw new ArgumentNullException(nameof(usage)) };

    /// <summary>A call that failed or was cancelled before any output.</summary>
    public static SpendOutcome FailedBeforeOutput(string? reason) => new(SpendOutcomeKind.FailedBeforeOutput) { Reason = reason };

    /// <summary>A call cancelled after <paramref name="outputTokens"/> output tokens.</summary>
    public static SpendOutcome CancelledAfterOutput(double outputTokens, string? reason = null) =>
        new(SpendOutcomeKind.CancelledAfterOutput) { OutputTokensSeen = outputTokens, Reason = reason };

    /// <summary>A call whose stream ended without usage.</summary>
    public static SpendOutcome EndedWithoutUsage(double outputTokens, string reason) =>
        new(SpendOutcomeKind.EndedWithoutUsage) { OutputTokensSeen = outputTokens, Reason = reason };
}

/// <summary>The user's answer to an Ask. Sent as <c>proceed-once</c>, <c>cap-output</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpendChoice>))]
public enum SpendChoice
{
    /// <summary>Send it once as it is; the caps stay as they are.</summary>
    [JsonStringEnumMemberName("proceed-once")] ProceedOnce,

    /// <summary>Send it once with <c>maxTokens</c> lowered to <see cref="SpendApproval.MaxOutputTokens"/> so it fits.</summary>
    [JsonStringEnumMemberName("cap-output")] CapOutput,
}

/// <summary>A one-time approval the user gave in the confirm modal. It applies to the next call of <see cref="Run"/> (of
/// <see cref="Op"/> only, or of <see cref="Submission"/> only, when set) that needs an Ask answer, once; a Block cap still blocks.</summary>
/// <param name="Run">The run key the approval is for (the chat id).</param>
/// <param name="Choice">Proceed once, or cap the output.</param>
public sealed record SpendApproval(string Run, SpendChoice Choice)
{
    /// <summary>The output cap the user chose, for <see cref="SpendChoice.CapOutput"/>.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Only this op (a call stopped inside a run), or null for the run's next call.</summary>
    public string? Op { get; init; }

    /// <summary>Only a call that answers this submission (<see cref="SpendCall.Submission"/>: the chat message the user approved the send
    /// of, placed at or after the approval), or null for any call of the run.</summary>
    public string? Submission { get; init; }

    /// <summary>The same approval, trimmed and checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public SpendApproval Normalize()
    {
        if (string.IsNullOrWhiteSpace(Run)) throw new ArgumentException("An approval needs the run it is for.");
        if (!Enum.IsDefined(Choice)) throw new ArgumentException($"Unknown choice {(int)Choice}.");
        if (Choice == SpendChoice.CapOutput && MaxOutputTokens is not >= 1) throw new ArgumentException("Capping the output needs a token count of 1 or more.");
        if (Choice == SpendChoice.ProceedOnce && MaxOutputTokens is not null) throw new ArgumentException("Proceed once takes no output cap.");
        return this with
        {
            Run = Run.Trim(),
            Op = string.IsNullOrWhiteSpace(Op) ? null : Op.Trim(),
            Submission = string.IsNullOrWhiteSpace(Submission) ? null : Submission.Trim(),
        };
    }
}

/// <summary>The Spend Guard for one provider call (the Guard unit). A singleton; safe to call from many threads.</summary>
public interface ISpendGuard
{
    /// <summary>What would happen if <paramref name="call"/> were sent now: its estimate, the decision and its causes. Reserves nothing.</summary>
    Task<SpendDecision> PreflightAsync(SpendCall call, CancellationToken cancellationToken = default);

    /// <summary>What would happen if <paramref name="calls"/> (one run's, such as a Compare run's columns) were reserved now as one group, all
    /// or none: each call's estimate and decision, checked with the group's earlier calls counted, so a run, day or month cap that only the
    /// group's total crosses shows before anything is sent. A waiting approval of the run answers the group once, as it would when the group
    /// is reserved. Reserves nothing.</summary>
    /// <exception cref="ArgumentException">The group is empty, two calls share an op, the calls are of different runs, or a value is not
    /// allowed.</exception>
    Task<SpendGroupDecision> PreflightGroupAsync(IReadOnlyList<SpendCall> calls, CancellationToken cancellationToken = default);

    /// <summary>Estimates <paramref name="call"/>, consumes a matching approval (<see cref="ApproveAsync"/>) when it needs one, and reserves
    /// it under its op; an op already reserved returns that reservation (a recovered request never reserves twice).</summary>
    Task<SpendReservation> ReserveAsync(SpendCall call, CancellationToken cancellationToken = default);

    /// <summary>Settles the reservation of <paramref name="op"/> from <paramref name="outcome"/>; returns the row, or null when the op has
    /// none.</summary>
    Task<Reservation?> SettleAsync(string op, SpendOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Records the user's one-time approval (<see cref="SpendApproval.Normalize"/>) and returns it as stored.</summary>
    Task<SpendApproval> ApproveAsync(SpendApproval approval, CancellationToken cancellationToken = default);
}
