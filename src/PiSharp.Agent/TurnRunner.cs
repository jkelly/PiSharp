using PiSharp.AI;
using PiSharp.Contracts;
using System.Text.Json;

namespace PiSharp.Agent;

public sealed record AssistantMessageStarted(AssistantMessage Message) : AgentEvent;

/// <summary>
/// Composes one owned chat run with one tool batch. No automatic next request, queues,
/// transcript preparation, session state, policy broker or complete Agent lifecycle is supplied.
/// </summary>
public sealed class TurnRunner
{
    private readonly ChatClient client;
    private readonly ToolBatchScheduler scheduler;
    private readonly string? thinkingLevel;

    public TurnRunner(ChatClient client, ToolBatchScheduler scheduler, string? thinkingLevel = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scheduler);
        this.client = client;
        this.scheduler = scheduler;
        if (thinkingLevel is not null && !ThinkingLevels.Ordered.Contains(thinkingLevel, StringComparer.Ordinal))
            throw new ArgumentException("Invalid thinking level.", nameof(thinkingLevel));
        this.thinkingLevel = thinkingLevel;
    }

    public Task<TurnResult> RunAsync(ChatRequest request, IAgentEventSink sink,
        CancellationToken cancellationToken = default) => RunCoreAsync(request, sink, cancellationToken, false, null);

    /// <summary>Drains canceled work, awaits cleanup and delivers the final assistant through an uncanceled settlement sink.</summary>
    public Task<TurnResult> RunWithAbortSettlementAsync(ChatRequest request, IAgentEventSink sink,
        CancellationToken cancellationToken = default, long? fallbackTimestamp = null) =>
        RunCoreAsync(request, sink, cancellationToken, true, fallbackTimestamp);

    private async Task<TurnResult> RunCoreAsync(ChatRequest request, IAgentEventSink sink,
        CancellationToken cancellationToken, bool settleAbort, long? fallbackTimestamp)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        // High-level configuration owns this control, including cancellation fallback requests.
        if (thinkingLevel is not null) request = request with { ThinkingLevel = thinkingLevel };
        ChatResult chat;
        var deliveryToken = settleAbort ? CancellationToken.None : cancellationToken;
        var assistantStarted = false;
        var run = settleAbort ? await client.StartWithAbortSettlementAsync(request, cancellationToken, fallbackTimestamp).ConfigureAwait(false) :
            await client.StartAsync(request, cancellationToken).ConfigureAwait(false);
        await using (run.ConfigureAwait(false))
        {
            // The producer observes cancellation; drain its normalized terminal outcome when the sink accepts it.
            await foreach (var observation in run.ReadEventsAsync().ConfigureAwait(false))
            {
                if (settleAbort && observation is StreamStarted started)
                {
                    assistantStarted = true;
                    await sink.EmitAsync(new AssistantMessageStarted(started.Partial), deliveryToken).ConfigureAwait(false);
                }
                await sink.EmitAsync(new TurnStreamObserved(observation), deliveryToken).ConfigureAwait(false);
            }
            chat = await run.Completion.ConfigureAwait(false);
        }

        // Disposal/transport cleanup settles before any assistant commit barrier or tool preflight.
        // Caller cancellation during the chat phase propagates only after owned cleanup completes.
        if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
        if (settleAbort || thinkingLevel is not null)
        {
            // Stamp the same requested control sent on this turn, after transport cleanup.
            // Raw provider progress remains untouched.
            chat = chat with { Message = chat.Message with
            { ExtraProperties = (chat.Message.ExtraProperties ?? JsonFields.Empty).Set("thinkingLevel", JsonData.Parse(JsonSerializer.Serialize(request.ThinkingLevel ?? "off"))) } };
        }
        if (settleAbort)
        {
            if (!assistantStarted)
                await sink.EmitAsync(new AssistantMessageStarted(chat.Message), deliveryToken).ConfigureAwait(false);
        }
        var tools = await scheduler.RunAsync(chat.Message, settleAbort ? new SettlementSink(sink) : sink, cancellationToken).ConfigureAwait(false);
        return new(chat, tools, run.CleanupFailure);
    }

    private sealed class SettlementSink(IAgentEventSink sink) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) =>
            sink.EmitAsync(observation, CancellationToken.None);
    }
}
