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
        await foreach (var observation in source.StreamAsync(new(request.Model, messages, timestamp), cancellationToken).ConfigureAwait(false))
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
                    throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed);
                final = terminal.Message;
            }
        }
        // A joined secondary cleanup failure is still a failed summary. Caller cancellation
        // must not erase the separate physical failure reported by the actual transport.
        if (cleanupFailed) throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed);
        cancellationToken.ThrowIfCancellationRequested();
        if (final is null || final.StopReason is StopReason.Error or StopReason.Length or StopReason.Aborted or StopReason.Pending or StopReason.Deferred ||
            final.Content.IsDefault || final.Content.Any(content => content is ToolCallContent))
            throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed);
        // Validate owned usage and final wire envelope without persisting the assistant itself.
        _ = PiWireJson.WriteMessage(final);
        var text = string.Join('\n', final.Content.OfType<TextContent>().Select(content => content.Text));
        if (text.Length > 1_048_576) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        return new(text, final.Usage);
    }
}
