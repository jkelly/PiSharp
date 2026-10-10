// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts (emitUIPromptEvent queues
// each emit with queueMicrotask, so the emits of one source start in the order they were raised; emit awaits every handler in turn).
namespace PiSharp.Cli.Extensions;

/// <summary>
/// Observations one source raises without waiting for them (ui_prompt_start/ui_prompt_end, mcp_servers_change): each dispatch runs
/// off the caller's thread, after the previous one of the same source has finished, so every observer receives them in emission
/// order (start before end) however its handlers interleave or the thread pool schedules them. Failures stay with their dispatch.
/// </summary>
internal sealed class OrderedObservationQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Queues <paramref name="dispatch"/> behind every earlier one and returns without running it on the caller's thread.</summary>
    internal Task Enqueue(Func<Task> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate) return _tail = RunAfterAsync(_tail, dispatch);
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> dispatch)
    {
        // Earlier dispatches never fault the queue (their failures are their own), so this only waits.
        try { await previous.ConfigureAwait(false); } catch (Exception) { }
        try { await Task.Run(dispatch).ConfigureAwait(false); } catch (Exception) { }
    }
}
