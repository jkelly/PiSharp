using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;

namespace PiSharp.CodingAgent;

/// <summary>Standalone summary transport. The trusted factory binds this request's budget/routing options;
/// the generator fully drains and disposes its actual stream and never executes assistant tool calls.</summary>
public sealed class TransportSessionSummaryGenerator(Func<SessionSummaryRequest, IChatTransport> transport,
    Func<long>? clock = null, int maximumEvents = 100_000) : ISessionSummaryGenerator
{
    public async ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(transport);
        if (maximumEvents <= 0) throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = (clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))();
        var messages = ImmutableArray.Create(new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(new
            { role = "system", content = request.SystemPrompt, timestamp }))),
            new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
            { role = "user", content = new[] { new { type = "text", text = request.Prompt } }, timestamp }))));
        var source = transport(request) ?? throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed);
        AssistantMessage? final = null; var count = 0; var cleanupFailed = false;
        // compaction.ts completeSummarization: the request carries the summary's reasoning level (createSummarizationOptions) and its
        // routing session id; the bound route applies model.reasoning and the cache retention.
        var chatRequest = new ChatRequest(request.Model, messages, timestamp) { ThinkingLevel = request.ThinkingLevel, SessionId = request.SessionId };
        await foreach (var observation in source.StreamAsync(chatRequest, cancellationToken).ConfigureAwait(false))
        {
            if (++count > maximumEvents || final is not null) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
            if (observation is StreamTerminalEvent terminal)
            {
                cleanupFailed = terminal.NativeCleanupDiagnostic is not null;
                // An owned transport may publish its aborted terminal after cancellation cleanup.
                // Drain/dispose that actual stream before propagating this caller's cancellation.
                // Provider errors and unrequested aborted terminals remain failed summaries.
                if (terminal.Reason != terminal.Message.StopReason || terminal is StreamError &&
                    !(terminal.Reason == StopReason.Aborted && cancellationToken.IsCancellationRequested))
                    throw Failed(terminal.Message, request.Kind);
                final = terminal.Message;
            }
        }
        // A joined secondary cleanup failure is still a failed summary. Caller cancellation
        // must not erase the separate physical failure reported by the actual transport.
        if (cleanupFailed) throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed);
        cancellationToken.ThrowIfCancellationRequested();
        if (final is null || final.StopReason is StopReason.Error or StopReason.Length or StopReason.Aborted or StopReason.Pending or StopReason.Deferred ||
            final.Content.IsDefault || final.Content.Any(content => content is ToolCallContent))
            throw final is null ? new SessionCompactionException(SessionCompactionFailure.SummaryFailed) : Failed(final, request.Kind);
        // Validate owned usage and final wire envelope without persisting the assistant itself.
        _ = PiWireJson.WriteMessage(final);
        var text = string.Join('\n', final.Content.OfType<TextContent>().Select(content => content.Text));
        if (text.Length > 1_048_576) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        return new(text, final.Usage);
    }

    /// <summary>Carries the response's provider error text so summarization retry can classify it as Pi does.</summary>
    private static SessionCompactionException Failed(AssistantMessage message, SessionSummaryKind kind)
    {
        string? error = null;
        if (message.StopReason == StopReason.Error && message.ExtraProperties?.TryGet("errorMessage", out var value) == true &&
            value is { Value.ValueKind: JsonValueKind.String }) error = value.Value.GetString();
        // compaction.ts getSummarizationFailure with the label of the summary (compaction.ts, branch-summarization.ts).
        var label = kind switch { SessionSummaryKind.TurnPrefix => "Turn prefix summarization", SessionSummaryKind.Branch => "Branch summarization", _ => "Summarization" };
        var text = message.StopReason switch
        {
            StopReason.Error => $"{label} failed: {(string.IsNullOrEmpty(error) ? "Unknown error" : error)}",
            StopReason.Length => $"{label} failed: generation hit the token cap and the summary is incomplete",
            _ when !message.Content.IsDefault && message.Content.Any(content => content is ToolCallContent) => $"{label} attempted to call a tool",
            _ => null
        };
        return new(SessionCompactionFailure.SummaryFailed)
        { ProviderErrorMessage = message.StopReason == StopReason.Error ? error ?? "" : null, ProviderAborted = message.StopReason == StopReason.Aborted, FailureText = text };
    }
}
