// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (AgentSessionEvent:
// entry_appended, thinking_level_changed, summarization_retry_*; _summarizationRetryCallbacks; setThinkingLevel; _emitModelSelect)
// and packages/ai/src/utils/retry.ts (retryAssistantCall).
using System.Text.Json;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

/// <summary>Source <c>entry_appended</c>: an entry appended outside the message flow (extension custom entries, recovery
/// omissions, virtual-model router state, cache-warming usage). Published after the durable checkpoint.</summary>
public sealed record SessionEntryAppended(long OperationGeneration, SessionEntry Entry) : SessionOperationEvent(OperationGeneration)
{
    public JsonData ToJson() => JsonData.Parse("{\"type\":\"entry_appended\",\"entry\":" + Entry.WireBody.Value.GetRawText() + "}");
}
/// <summary>Source <c>thinking_level_changed</c> (session listeners) and <c>thinking_level_select</c> (extensions).</summary>
public sealed record SessionThinkingLevelChanged(long OperationGeneration, string Level, string PreviousLevel) : SessionOperationEvent(OperationGeneration)
{
    public JsonData ToJson() => JsonData.Parse(JsonSerializer.Serialize(new { type = "thinking_level_changed", level = Level }));
    public JsonData ToExtensionJson() => JsonData.Parse(JsonSerializer.Serialize(new { type = "thinking_level_select", level = Level, previousLevel = PreviousLevel }));
}
/// <summary>Source <c>model_select</c> for extensions. Source is "set", "cycle" or "restore"; the host maps descriptors to model objects.</summary>
public sealed record SessionModelSelected(long OperationGeneration, ModelDescriptor Model, ModelDescriptor? PreviousModel, string Source)
    : SessionOperationEvent(OperationGeneration);
/// <summary>Source <c>summarization_retry_scheduled</c>, emitted before the backoff of a compaction or branch-summary retry.</summary>
public sealed record SessionSummarizationRetryScheduled(long OperationGeneration, int Attempt, int MaxAttempts, long DelayMs, string ErrorMessage)
    : SessionOperationEvent(OperationGeneration)
{
    public JsonData ToJson() => JsonData.Parse(JsonSerializer.Serialize(new
    { type = "summarization_retry_scheduled", attempt = Attempt, maxAttempts = MaxAttempts, delayMs = DelayMs, errorMessage = ErrorMessage }));
}
/// <summary>Source <c>summarization_retry_attempt_start</c>: source "branchSummary", or "compaction" with its reason.</summary>
public sealed record SessionSummarizationRetryAttemptStarted(long OperationGeneration, string Source, SessionCompactionReason? Reason)
    : SessionOperationEvent(OperationGeneration)
{
    public JsonData ToJson() => Reason is { } reason
        ? JsonData.Parse(JsonSerializer.Serialize(new { type = "summarization_retry_attempt_start", source = Source, reason = ReasonText(reason) }))
        : JsonData.Parse(JsonSerializer.Serialize(new { type = "summarization_retry_attempt_start", source = Source }));
    public static string ReasonText(SessionCompactionReason reason) => reason switch
    {
        SessionCompactionReason.Manual => "manual", SessionCompactionReason.Threshold => "threshold",
        SessionCompactionReason.Overflow => "overflow", _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
/// <summary>Source <c>summarization_retry_finished</c>; the source event carries no fields, success and attempt are native detail.</summary>
public sealed record SessionSummarizationRetryFinished(long OperationGeneration, bool Success, int Attempt, string? FinalError = null)
    : SessionOperationEvent(OperationGeneration)
{
    public JsonData ToJson() => JsonData.Parse("{\"type\":\"summarization_retry_finished\"}");
}

public sealed partial class PersistentAgentSession
{
    /// <summary>Publishes acknowledged out-of-flow entries to session listeners in append order.</summary>
    private async ValueTask PublishAppendedAsync(IEnumerable<SessionEntry> entries)
    {
        long generation; lock (_gate) generation = _operationGeneration;
        foreach (var entry in entries) await EmitOperationAsync(new SessionEntryAppended(generation, entry)).ConfigureAwait(false);
    }

    /// <summary>Source completeSummarization/retryAssistantCall around one summary request: transient provider errors are
    /// retried with the configured retry budget and backoff, emitting the summarization retry events.</summary>
    private ValueTask<SessionGeneratedSummary> GenerateRetryingSummaryAsync(ISessionSummaryGenerator generator,
        SessionSummaryRequest request, CancellationToken token, string source, SessionCompactionReason? reason) =>
        RetrySummaryAsync(() => GenerateSummaryAsync(generator, request, token), token, source, reason);
    private async ValueTask<SessionGeneratedSummary> RetrySummaryAsync(Func<ValueTask<SessionGeneratedSummary>> produce,
        CancellationToken token, string source, SessionCompactionReason? reason)
    {
        AgentRetryPolicy? policy; Func<long, CancellationToken, Task>? delay; long generation;
        lock (_gate) { policy = _retryConfigured ? _retryPolicy : null; delay = _retryDelay; generation = _operationGeneration; }
        var maxAttempts = policy?.Enabled == true ? policy.MaxRetries : 0;
        var attempt = 0; string? lastError = null;
        while (true)
        {
            try
            {
                var summary = await produce().ConfigureAwait(false);
                if (lastError is not null) await EmitOperationAsync(new SessionSummarizationRetryFinished(generation, true, attempt)).ConfigureAwait(false);
                return summary;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (lastError is not null) await EmitOperationAsync(new SessionSummarizationRetryFinished(generation, false, attempt)).ConfigureAwait(false);
                throw;
            }
            catch (SessionCompactionException error) when (error.Failure == SessionCompactionFailure.SummaryFailed)
            {
                // Aborted responses are terminal; non-retryable errors and an exhausted budget return the final failure.
                if (error.ProviderAborted || error.ProviderErrorMessage is not { } message || attempt >= maxAttempts ||
                    !AgentRetryPolicy.IsRetryableError(StopReason.Error, message))
                {
                    if (lastError is not null)
                        await EmitOperationAsync(new SessionSummarizationRetryFinished(generation, false, attempt,
                            error.ProviderAborted ? null : error.ProviderErrorMessage)).ConfigureAwait(false);
                    throw;
                }
                attempt++; lastError = message.Length == 0 ? "Unknown error" : message;
                var wait = policy!.DelayMs(attempt);
                await EmitOperationAsync(new SessionSummarizationRetryScheduled(generation, attempt, maxAttempts, wait, lastError)).ConfigureAwait(false);
                try { await (delay ?? DelayRetryAsync)(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await EmitOperationAsync(new SessionSummarizationRetryFinished(generation, false, attempt, lastError)).ConfigureAwait(false);
                    throw;
                }
                await EmitOperationAsync(new SessionSummarizationRetryAttemptStarted(generation, source, reason)).ConfigureAwait(false);
            }
        }
    }
    private static async Task DelayRetryAsync(long milliseconds, CancellationToken token)
    {
        do { var chunk = Math.Min(milliseconds, int.MaxValue - 1L); await Task.Delay((int)chunk, token).ConfigureAwait(false); milliseconds -= chunk; }
        while (milliseconds > 0);
    }
}
