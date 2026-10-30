// Decision 0011 (run W3, S3): the Spend Guard's reserve-send-settle rules for any provider call, durable or not. GuardedDurableModels uses it
// for every chat request; W4's Playground, Compare and Bench use it directly (a Compare run reserves its columns as a group, all or none).
// A call is reserved first (SpendGuard: the worst case against every cap, the user's one-time approval when a cap asks); a refused call
// sends nothing and ends with one error terminal whose message is the spend error. A reserved call is sent once, with maxTokens lowered only
// when the user chose "cap the output", and the stream is watched as it passes through unchanged. Its reservation is settled from how it
// ended: a finished answer (stop, length or tool use) settles at the actual cost its usage reports; an error or a cancel before any output
// releases it; a cancel after output settles at the estimate (the input estimate plus the output seen, at the reservation's rates); a
// stream that failed or ended without a final answer after output, or that finished without reporting usage on a row that counts, becomes
// uncertain (counted until the user resolves it). The usage on an error or abort is partial (providers report it before the end: Anthropic
// at message_start, often the input alone), never the actual cost; it only raises that estimate to at least what it reports. A deferred
// response (stop reason "deferred") keeps its reservation until the deferred answer arrives. A reserved lease that is never sent is released
// when it is disposed. Settling never throws into the caller's stream: a ledger failure is logged, and the row stays reserved (startup
// recovery makes it uncertain).
//
// "Output" is any content the provider streamed (text, thinking or a tool call; the opening start event is not). The output seen is the
// UTF-8 size of the streamed deltas priced as the input is (bytes / 3 x 1.1, at least one token), which over- rather than under-counts.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Contracts;

namespace PiSharp.WebAgent.Spend;

/// <summary>What a guarded call sends: the call as reserved, its ledger op and the <c>maxTokens</c> to use.</summary>
/// <param name="Call">The call.</param>
/// <param name="LedgerOp">The ledger op of its reservation.</param>
/// <param name="MaxTokens">The output cap to send (the user's chosen cap when lower than the call's own), or null for none.</param>
public sealed record GuardedSend(SpendCall Call, string LedgerOp, int? MaxTokens);

/// <summary>Sends a guarded call as a stream (an <c>IChatTransport</c>'s or <c>IDurableModels.StreamSimple</c>'s).</summary>
public delegate IAsyncEnumerable<StreamEvent> GuardedStreamSend(GuardedSend send, CancellationToken cancellationToken);

/// <summary>Sends a guarded call and waits for its final message (<c>IDurableModels.CompleteSimpleAsync</c>'s).</summary>
public delegate Task<AssistantMessage> GuardedCompleteSend(GuardedSend send, CancellationToken cancellationToken);

/// <summary>Runs provider calls under the Spend Guard. A singleton; safe to call from many threads.</summary>
public sealed class GuardedRunExecutor
{
    private readonly ILogger log;

    /// <summary>An executor over <paramref name="guard"/>.</summary>
    public GuardedRunExecutor(SpendGuard guard, ILogger<GuardedRunExecutor>? log = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Guard = guard;
        this.log = log ?? NullLogger<GuardedRunExecutor>.Instance;
    }

    /// <summary>The guard.</summary>
    public SpendGuard Guard { get; }

    /// <summary>Reserves <paramref name="call"/> (<see cref="SpendGuard.ReserveCallAsync"/>) for sending later through the lease.</summary>
    /// <exception cref="ArgumentException"><paramref name="call"/> has no op, or a value is not allowed.</exception>
    public async Task<GuardedLease> ReserveAsync(SpendCall call, CancellationToken cancellationToken = default) =>
        new(this, await Guard.ReserveCallAsync(call, cancellationToken).ConfigureAwait(false));

    /// <summary>Reserves every call of <paramref name="calls"/> or none (<see cref="SpendGuard.ReserveGroupAsync"/>): a Compare run's
    /// columns. Each lease is sent on its own; disposing the group releases the leases never sent.</summary>
    /// <exception cref="ArgumentException">The group is not allowed (empty, no op, a shared op, several runs).</exception>
    public async Task<GuardedLeaseGroup> ReserveGroupAsync(IReadOnlyList<SpendCall> calls, CancellationToken cancellationToken = default)
    {
        var group = await Guard.ReserveGroupAsync(calls, cancellationToken).ConfigureAwait(false);
        return new GuardedLeaseGroup(group, [.. group.Items.Select(item => new GuardedLease(this, item))]);
    }

    /// <summary>Reserves <paramref name="call"/> and streams it through <paramref name="send"/>, settling when the stream ends: a refused call
    /// yields only a <see cref="StreamError"/> with the spend error (nothing is sent).</summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(SpendCall call, ModelDescriptor model, GuardedStreamSend send,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var lease = await ReserveAsync(call, cancellationToken).ConfigureAwait(false);
        await foreach (var item in lease.StreamAsync(model, send, cancellationToken).ConfigureAwait(false)) yield return item;
    }

    /// <summary>Reserves <paramref name="call"/> and completes it through <paramref name="send"/>, settling from the final message: a refused
    /// call returns the spend error message (nothing is sent).</summary>
    public async Task<AssistantMessage> CompleteAsync(SpendCall call, ModelDescriptor model, GuardedCompleteSend send, CancellationToken cancellationToken = default)
    {
        await using var lease = await ReserveAsync(call, cancellationToken).ConfigureAwait(false);
        return await lease.CompleteAsync(model, send, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Settles a deferred reservation (<see cref="GuardedLease.DeferredHandle"/>) from the deferred answer's final message; a message
    /// still deferred changes nothing.</summary>
    public Task<Reservation?> SettleDeferredAsync(string ledgerOp, AssistantMessage message) =>
        SpendStreamWatch.FromMessage(message, SpendStreamWatch.HasOutput(message), SpendStreamWatch.ContentTokens(message)) is { } outcome
            ? SettleAsync(ledgerOp, outcome) : Task.FromResult<Reservation?>(null);

    /// <summary>A stopped call's final message: stop reason <c>error</c> with <paramref name="errorMessage"/> (the spend error).</summary>
    public static AssistantMessage StoppedMessage(ModelDescriptor model, string errorMessage, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(errorMessage);
        return new AssistantMessage(model.Api, model.Provider, model.Id, timestamp, [], TokenUsage.Zero, StopReason.Error,
            JsonFields.Empty.Set("errorMessage", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(errorMessage))));
    }

    /// <summary>Settles <paramref name="ledgerOp"/> from <paramref name="outcome"/>, never throwing (a failure is logged; the row stays
    /// reserved, and startup recovery makes it uncertain).</summary>
    internal async Task<Reservation?> SettleAsync(string ledgerOp, SpendOutcome outcome)
    {
        try
        {
            return await Guard.SettleAsync(ledgerOp, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            log.LogError(error, "Spend Guard: the reservation {Op} could not be settled ({Outcome}); it stays reserved until the next start makes it uncertain.",
                ledgerOp, outcome.Kind);
            return null;
        }
    }

    internal long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>One reserved (or refused) call, sent at most once.</summary>
public sealed class GuardedLease : IAsyncDisposable
{
    private readonly GuardedRunExecutor executor;
    private const string ReleasedMessage = SpendGuard.ErrorPrefix + "this call's reservation was released before it was sent, so nothing was sent.";
    private int claimed;
    private int state; // 0: not sent, 1: sending, 2: released

    internal GuardedLease(GuardedRunExecutor executor, GuardedReservation reservation)
    {
        this.executor = executor;
        Reservation = reservation;
    }

    /// <summary>The reservation (or the refusal).</summary>
    public GuardedReservation Reservation { get; }

    /// <summary>Whether the call holds a reservation and may be sent.</summary>
    public bool Reserved => Reservation.Reserved;

    /// <summary>The decision.</summary>
    public SpendDecision Decision => Reservation.Decision;

    /// <summary>The ledger op, or null when refused.</summary>
    public string? LedgerOp => Reservation.LedgerOp;

    /// <summary>How the sent call ended, once it has; null before, for a refused call, and for a deferred one.</summary>
    public SpendOutcome? Outcome { get; private set; }

    /// <summary>The ledger row after settling, once settled.</summary>
    public Reservation? Settled { get; private set; }

    /// <summary>The handle id of a deferred response whose reservation waits for the deferred answer, or null.</summary>
    public string? DeferredHandle { get; private set; }

    /// <summary>Sends the call through <paramref name="send"/> (once) and yields its events unchanged, settling when the stream ends; a
    /// refused call yields one <see cref="StreamError"/> with the spend error and sends nothing.</summary>
    /// <exception cref="InvalidOperationException">The lease was already sent or released.</exception>
    public IAsyncEnumerable<StreamEvent> StreamAsync(ModelDescriptor model, GuardedStreamSend send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(send);
        if (Interlocked.Exchange(ref claimed, 1) != 0) throw new InvalidOperationException("A guarded call is sent once.");
        return Stream(model, send, cancellationToken);
    }

    /// <summary>Sends the call through <paramref name="send"/> (once) and returns its final message, settling from it; a refused call returns
    /// the spend error message and sends nothing.</summary>
    /// <exception cref="InvalidOperationException">The lease was already sent or released.</exception>
    public async Task<AssistantMessage> CompleteAsync(ModelDescriptor model, GuardedCompleteSend send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(send);
        if (Interlocked.Exchange(ref claimed, 1) != 0) throw new InvalidOperationException("A guarded call is sent once.");
        if (!Reserved) return GuardedRunExecutor.StoppedMessage(model, Reservation.ErrorMessage!, executor.Now());
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return GuardedRunExecutor.StoppedMessage(model, ReleasedMessage, executor.Now());
        AssistantMessage message;
        try
        {
            message = await send(Send(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Without the stream, nobody knows whether output arrived: the row keeps counting until the user resolves it.
            await SettleAsync(SpendOutcome.EndedWithoutUsage(0, "cancelled while waiting for the whole answer; usage unknown")).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            // A request failure comes back as an error message; an exception means nothing was answered.
            await SettleAsync(SpendOutcome.FailedBeforeOutput("failed before any output: " + SpendGuard.Reason(error))).ConfigureAwait(false);
            throw;
        }
        await SettleAsync(SpendStreamWatch.FromMessage(message, SpendStreamWatch.HasOutput(message), SpendStreamWatch.ContentTokens(message)), message).ConfigureAwait(false);
        return message;
    }

    /// <summary>Releases a reserved call that will not be sent (a Compare column that was never started). Nothing for a call already being
    /// sent, and for a refused one.</summary>
    public async Task<Reservation?> ReleaseAsync(string? reason = null)
    {
        if (!Reserved || Interlocked.CompareExchange(ref state, 2, 0) != 0) return null;
        await SettleAsync(SpendOutcome.FailedBeforeOutput(reason ?? "not sent")).ConfigureAwait(false);
        return Settled;
    }

    /// <summary>Releases the reservation when the call was never sent (its stream never enumerated).</summary>
    public async ValueTask DisposeAsync() => await ReleaseAsync("not sent").ConfigureAwait(false);

    private GuardedSend Send() => new(Reservation.Call, LedgerOp!, Reservation.MaxTokens);

    private async IAsyncEnumerable<StreamEvent> Stream(ModelDescriptor model, GuardedStreamSend send, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Reserved)
        {
            yield return new StreamError(StopReason.Error, GuardedRunExecutor.StoppedMessage(model, Reservation.ErrorMessage!, executor.Now()));
            yield break;
        }
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
        {
            yield return new StreamError(StopReason.Error, GuardedRunExecutor.StoppedMessage(model, ReleasedMessage, executor.Now()));
            yield break;
        }
        var watch = new SpendStreamWatch();
        Exception? failure = null;
        var ended = false;
        IAsyncEnumerator<StreamEvent>? items = null;
        try
        {
            try
            {
                items = send(Send(), cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            while (true)
            {
                try
                {
                    if (!await items.MoveNextAsync().ConfigureAwait(false))
                    {
                        ended = true;
                        break;
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    throw;
                }
                watch.Observe(items.Current);
                yield return items.Current;
            }
        }
        finally
        {
            Exception? cleanup = null;
            if (items is not null)
            {
                try { await items.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanup = error; }
            }
            await SettleAsync(watch.Outcome(failure ?? cleanup, ended, cancellationToken.IsCancellationRequested), watch.Terminal?.Message).ConfigureAwait(false);
            if (cleanup is not null && failure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cleanup);
        }
    }

    /// <summary>Settles from <paramref name="outcome"/>; null (a deferred response) keeps the reservation for the deferred answer.</summary>
    private async Task SettleAsync(SpendOutcome? outcome, AssistantMessage? message = null)
    {
        if (outcome is null)
        {
            DeferredHandle = SpendStreamWatch.DeferredId(message) ?? LedgerOp;
            return;
        }
        Outcome = outcome;
        if (LedgerOp is { } op) Settled = await executor.SettleAsync(op, outcome).ConfigureAwait(false);
    }
}

/// <summary>A group of leases reserved all or none (a Compare run's columns).</summary>
/// <param name="Reservation">The group's reservation (or refusal).</param>
/// <param name="Leases">One lease per call, in order.</param>
public sealed record GuardedLeaseGroup(GuardedGroupReservation Reservation, IReadOnlyList<GuardedLease> Leases) : IAsyncDisposable
{
    /// <summary>Whether every call holds a reservation.</summary>
    public bool Reserved => Reservation.Reserved;

    /// <summary>Releases every lease never sent.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var lease in Leases) await lease.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Watches one provider stream for the Spend Guard: whether output arrived, about how much, and how it ended.</summary>
internal sealed class SpendStreamWatch
{
    private long outputBytes;

    /// <summary>Whether any content (text, thinking, a tool call) arrived.</summary>
    public bool SawOutput { get; private set; }

    /// <summary>The terminal event, once seen.</summary>
    public StreamTerminalEvent? Terminal { get; private set; }

    /// <summary>About how many output tokens arrived: the deltas' UTF-8 size as tokens (bytes / 3 x 1.1), at least one once any arrived.</summary>
    public double OutputTokens => SawOutput ? Math.Max(1, CostEstimator.InputTokens(outputBytes)) : 0;

    /// <summary>Notes one event.</summary>
    public void Observe(StreamEvent item)
    {
        switch (item)
        {
            case StreamTerminalEvent terminal:
                Terminal ??= terminal;
                break;
            case StreamStarted:
                break;
            case TextStarted text:
                Add(text.Content.Text);
                break;
            case ThinkingStarted thinking:
                Add(thinking.Content.Thinking);
                break;
            case TextDelta text:
                Add(text.Delta);
                break;
            case ThinkingDelta thinking:
                Add(thinking.Delta);
                break;
            case ToolCallDelta call:
                Add(call.Delta);
                break;
            case ToolCallStarted started:
                AddCall(started.ToolCall.Name);
                break;
            case ToolCallProvisionalStarted started:
                AddCall(started.ToolCall.Name);
                break;
            case ToolCallHeaderUpdated header:
                AddCall(header.Name);
                break;
            // A block's end or checkpoint repeats what its deltas carried; it is output only when no delta was seen.
            case TextEnded ended:
                Late(ended.Content);
                break;
            case ThinkingEnded ended:
                Late(ended.Content);
                break;
            case ThinkingCheckpoint checkpoint:
                Late(checkpoint.Content);
                break;
            case ToolCallCheckpoint checkpoint:
                Late(checkpoint.Json);
                break;
            case ToolCallEnded ended:
                if (!SawOutput) AddCall(ended.ToolCall.Name);
                break;
            case ContentBlockFinalized:
                SawOutput = true;
                break;
            default:
                // An event kind this watch does not know: counted as output, so a cancel settles rather than releases.
                SawOutput = true;
                break;
        }
    }

    /// <summary>How the call ended: from the terminal message when there is one; else from <paramref name="failure"/>, a cancel, or a
    /// stream that stopped without a final message. Null for a deferred response.</summary>
    public SpendOutcome? Outcome(Exception? failure, bool ended, bool cancelled)
    {
        if (Terminal is { } terminal) return FromMessage(terminal.Message, SawOutput || HasOutput(terminal.Message), Math.Max(OutputTokens, ContentTokens(terminal.Message)));
        if (failure is OperationCanceledException || (failure is null && (cancelled || !ended)))
            return SawOutput ? SpendOutcome.CancelledAfterOutput(OutputTokens, "cancelled after output") : SpendOutcome.FailedBeforeOutput("cancelled before any output");
        if (failure is not null)
            return SawOutput ? SpendOutcome.EndedWithoutUsage(OutputTokens, "the stream failed after output: " + SpendGuard.Reason(failure))
                : SpendOutcome.FailedBeforeOutput("failed before any output: " + SpendGuard.Reason(failure));
        return SawOutput ? SpendOutcome.EndedWithoutUsage(OutputTokens, SpendLedger.RecoveryReason) : SpendOutcome.FailedBeforeOutput("the stream ended before any output");
    }

    /// <summary>How a call that ended with <paramref name="message"/> settles. Null for a deferred response. Its usage is the actual cost
    /// only when the answer finished (stop, length, tool use). A provider reports usage before a stream ends (Anthropic at message_start),
    /// so an error or abort may carry partial usage (often the input alone): after output, an error is uncertain and an abort settles at the
    /// estimate, either at no less than that partial usage costs (it rides on the outcome); before any output, both release.</summary>
    public static SpendOutcome? FromMessage(AssistantMessage message, bool sawOutput, double outputTokens)
    {
        ArgumentNullException.ThrowIfNull(message);
        var usage = SpendUsage.From(message.Usage);
        var reported = usage.HasTokens || usage.CostMicros is > 0;
        var partial = reported ? usage : null;
        // Output tokens the provider counted are output seen, even when no content event arrived.
        var output = sawOutput || usage.OutputTokens > 0;
        var seen = double.IsFinite(usage.OutputTokens) ? Math.Max(outputTokens, usage.OutputTokens) : outputTokens;
        var error = SpendGuard.Cut(ErrorText(message));
        switch (message.StopReason)
        {
            case StopReason.Deferred:
                return null;
            case StopReason.Stop or StopReason.Length or StopReason.ToolUse:
                return SpendOutcome.Completed(usage) with { OutputTokensSeen = outputTokens };
            case StopReason.Error:
                if (output)
                    return SpendOutcome.EndedWithoutUsage(seen, (reported ? "the stream failed after output with partial usage" : "the stream failed after output without reporting usage") +
                        (error is null ? "" : ": " + error)) with { Usage = partial };
                return SpendOutcome.FailedBeforeOutput(error is null ? "provider error before any output" : "provider error before any output: " + error);
            case StopReason.Aborted:
                return output ? SpendOutcome.CancelledAfterOutput(seen, "cancelled after output") with { Usage = partial } : SpendOutcome.FailedBeforeOutput("cancelled before any output");
            default:
                return output ? SpendOutcome.EndedWithoutUsage(seen, "the stream ended without a final answer") with { Usage = partial }
                    : SpendOutcome.FailedBeforeOutput("the stream ended before any output");
        }
    }

    /// <summary>Whether <paramref name="message"/> holds any content.</summary>
    public static bool HasOutput(AssistantMessage message) => !message.Content.IsDefaultOrEmpty && message.Content.Any(content => content switch
    {
        TextContent text => text.Text.Length > 0,
        ThinkingContent thinking => thinking.Thinking.Length > 0,
        ToolCallContent => true,
        _ => true,
    });

    /// <summary>About how many output tokens <paramref name="message"/>'s content is (as <see cref="OutputTokens"/> counts them).</summary>
    public static double ContentTokens(AssistantMessage message)
    {
        if (!HasOutput(message)) return 0;
        long bytes = 0;
        foreach (var content in message.Content)
            bytes += content switch
            {
                TextContent text => System.Text.Encoding.UTF8.GetByteCount(text.Text),
                ThinkingContent thinking => System.Text.Encoding.UTF8.GetByteCount(thinking.Thinking),
                ToolCallContent call => System.Text.Encoding.UTF8.GetByteCount(call.Name) + call.Arguments.ToString().Length,
                _ => 0,
            };
        return Math.Max(1, CostEstimator.InputTokens(bytes));
    }

    /// <summary>The <c>errorMessage</c> of <paramref name="message"/>, or null.</summary>
    public static string? ErrorText(AssistantMessage? message) =>
        message?.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var value) && value is { Value.ValueKind: System.Text.Json.JsonValueKind.String }
            ? value.Value.GetString() : null;

    /// <summary>The handle id of a deferred <paramref name="message"/> (<c>deferred.id</c>), or null.</summary>
    public static string? DeferredId(AssistantMessage? message) =>
        message?.ExtraProperties is { } extra && extra.TryGet("deferred", out var value) && value is { Value.ValueKind: System.Text.Json.JsonValueKind.Object } handle &&
        handle.Value.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;

    private void Add(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SawOutput = true;
        outputBytes += System.Text.Encoding.UTF8.GetByteCount(text);
    }

    private void AddCall(string? name)
    {
        SawOutput = true;
        if (!string.IsNullOrEmpty(name)) outputBytes += System.Text.Encoding.UTF8.GetByteCount(name);
    }

    private void Late(string? content)
    {
        if (!SawOutput) Add(content);
    }
}
