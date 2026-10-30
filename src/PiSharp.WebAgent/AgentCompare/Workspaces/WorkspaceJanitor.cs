// Decision 0011 (run W4, unit R4): the workspace cleanup at start and once a day. It removes leftovers of runs that never finished making
// their workspaces (a crash or a kill mid-create), workspaces marked discarded that are still on disk, and every run older than the
// retention (7 days) unless the agent compare runner says it is still in use (IWorkspaceActivity, registered by R5 when it exists). It
// never stops the app: a failure is logged and retried the next day (and a run it could not remove is retried at the next start). The data
// folder's lock (WebAgentDataLock) means no other app shares these workspaces, so a run left "creating" really is a leftover.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Says whether a run's workspaces are still in use (an agent compare run that is going, or a model continued as a chat), so the
/// expiry cleanup keeps them. Optional: without one, only age counts.</summary>
public interface IWorkspaceActivity
{
    /// <summary>Whether <paramref name="run"/> is still in use.</summary>
    bool IsActive(string run);
}

/// <summary>Runs <see cref="WorkspaceManager.CleanupAsync"/> when the app starts and then once a day.</summary>
public sealed class WorkspaceJanitor(WorkspaceManager manager, IEnumerable<IWorkspaceActivity> activity, TimeProvider? time = null,
    ILogger<WorkspaceJanitor>? logger = null) : BackgroundService
{
    private readonly IWorkspaceActivity[] activity = [.. activity];
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly ILogger log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>How often the cleanup runs after the first.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromDays(1);

    /// <summary>The last cleanup's report (null until the first finished).</summary>
    public WorkspaceCleanupReport? LastReport { get; private set; }

    /// <summary>Completes when the first cleanup has finished (or failed).</summary>
    public Task FirstRun => first.Task;

    private readonly TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never hold up the app's start
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                first.TrySetResult();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            first.TrySetResult();
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            LastReport = await manager.CleanupAsync(activity.Length == 0 ? null : run => activity.Any(source => source.IsActive(run)), stoppingToken).ConfigureAwait(false);
            if (LastReport.Removed.Count > 0)
                log.LogInformation("Agent compare: removed {Count} old or unfinished workspace folder(s).", LastReport.Removed.Count);
        }
        catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            log.LogWarning(error, "Agent compare: the workspace cleanup failed; it runs again tomorrow and at the next start.");
        }
    }
}
