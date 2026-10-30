// Decision 0011 (run W4, fix 2): deleting an agent compare run. A run is deleted by its own delete (the API), by History (its runs.db row
// deleted on History, History's Clear, the retention's cleanup) and by Settings › Data's danger zone (which waits for every delete). A
// delete hides the run at once (AgentCompareRun.Deleting, stored: pages, the gate and the follower no longer see it; the follower and any
// running check are ended first), then removes, in order: each model's durable session (DurableDeviceHost.DeleteSessionAsync: its files and
// catalog rows) with its approvals, the run's workspaces, its runs.db rows (the run, its models and their calls), and last its own records
// (the run and its models' turns). A model continued as a chat keeps its session and workspace (the chat works there and is the user's
// now). When a step fails the run stays hidden and its delete runs again at the next start; a stop in the middle does the same. The spend
// ledger keeps the run's spend (money spent stays spent). The retention: when runs.db's cleanup removes a finished run's row, the run goes
// with it (also a run whose row went while the app was not running, at the next start); a run still running is never pruned by it.
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.AgentCompare.Runner;

public sealed partial class AgentCompareRunner
{
    /// <summary>The most a delete waits for the run's follower to end (it was cancelled) and for the device to open.</summary>
    private static readonly TimeSpan DeleteWait = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Task<AgentCompareDeleted>> deletions = new(StringComparer.Ordinal);

    // The runs.db store whose changes the runner follows (from StartingAsync to DisposeAsync).
    private RunStore? followedRuns;

    /// <summary>runs.db (null when the app has no History, or the app's services are gone).</summary>
    private RunStore? Runs
    {
        get
        {
            try { return services.GetService<RunStore>(); }
            catch (ObjectDisposedException) { return null; }
        }
    }

    /// <summary>Deletes run <paramref name="id"/> (see the file comment): stops it if it runs, then removes its sessions, workspaces, History
    /// rows and records. Repeating it while it runs answers the same delete.</summary>
    /// <exception cref="AgentCompareException">No such run (404).</exception>
    public Task<AgentCompareDeleted> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || store.Find(id) is null) throw AgentCompareException.Missing($"There is no agent compare run {id}.");
        return StartDelete(id).WaitAsync(cancellationToken);
    }

    /// <summary>Deletes every run (Settings › Data's danger zone, after History was cleared) and waits for the deletes.</summary>
    public async Task<IReadOnlyList<AgentCompareDeleted>> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var started = store.All().Select(run => StartDelete(run.Id)).ToList();
        return await Task.WhenAll(started).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts deleting run <paramref name="id"/> (once: a delete in progress is returned).</summary>
    private Task<AgentCompareDeleted> StartDelete(string id)
    {
        lock (deletions)
        {
            if (deletions.TryGetValue(id, out var running)) return running;
            if (store.Find(id) is not { } run || Volatile.Read(ref disposed) != 0) return Task.FromResult(NotDeleted(id));
            CancellationToken token;
            try { token = stopping.Token; }
            catch (ObjectDisposedException) { return Task.FromResult(NotDeleted(id)); }
            // The follower ends first (it sees its token cancelled), then the run is hidden: no follower starts for it again.
            Task? driver;
            lock (drivers)
            {
                driver = CancelDriver(id);
                store.MarkDeleting(id);
            }
            foreach (var model in run.Models)
            {
                StopCheck(id, model.Slot);
                gate.Signal(AgentCompareRun.ModelKey(id, model.Slot));
            }
            var task = Task.Run(() => DeleteCoreAsync(id, driver, token), CancellationToken.None);
            deletions[id] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (deletions)
                {
                    if (deletions.TryGetValue(id, out var current) && current == task) deletions.Remove(id);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private Task[] Deletions()
    {
        lock (deletions) return [.. deletions.Values];
    }

    private static AgentCompareDeleted NotDeleted(string id) => new(id, false, 0, [], [], []);

    private async Task<AgentCompareDeleted> DeleteCoreAsync(string id, Task? driver, CancellationToken token)
    {
        try
        {
            if (driver is not null)
            {
                try { await driver.WaitAsync(DeleteWait, token).ConfigureAwait(false); }
                catch (TimeoutException) { log.LogWarning("Agent compare run {Run}: its follower did not end in time; it is deleted anyway.", id); }
            }
            if (store.Find(id) is not { } run) return NotDeleted(id);
            var kept = run.Models.Where(model => ContinuedAsChat(id, model)).Select(model => model.Slot).ToList();
            var failed = new List<string>();

            // Sessions: a model continued as a chat keeps its session (it is that chat's).
            var sessions = 0;
            DurableDeviceHost? host = null;
            if (run.Models.Any(model => model.SessionId is not null && !kept.Contains(model.Slot)))
            {
                try { host = await device.WhenOpen.WaitAsync(DeleteWait, token).ConfigureAwait(false); }
                catch (TimeoutException) { log.LogWarning("Agent compare run {Run}: the durable device is not open, so its sessions stay for now.", id); }
            }
            foreach (var model in run.Models.Where(model => model.SessionId is not null && !kept.Contains(model.Slot)))
            {
                var session = model.SessionId!;
                broker.Forget(session, notify: true);
                if (host is null)
                {
                    failed.Add("session " + model.Slot);
                    continue;
                }
                try
                {
                    await host.DeleteSessionAsync(session, token).ConfigureAwait(false);
                    sessions++;
                }
                catch (HostNotFoundException)
                {
                    // Gone already (an earlier delete, or it was archived).
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Agent compare run {Run}: the session of model {Slot} could not be deleted.", id, model.Slot);
                    failed.Add("session " + model.Slot);
                }
            }

            // Workspaces: the whole run folder, unless a chat works in one of them (then the others only).
            var removed = new List<string>();
            try
            {
                if (kept.Count == 0)
                {
                    var had = run.Models.Where(model => !model.Discarded && model.WorkspacePath is not null).Select(model => model.Slot).ToList();
                    if (await workspaces.DiscardRunAsync(id, token).ConfigureAwait(false)) removed.AddRange(had);
                }
                else
                {
                    foreach (var model in run.Models.Where(model => !kept.Contains(model.Slot)))
                    {
                        try
                        {
                            if (await workspaces.DiscardAsync(id, model.Slot, token).ConfigureAwait(false)) removed.Add(model.Slot);
                        }
                        catch (WorkspaceException error) when (error.Code == WorkspaceException.NotFound) { /* Already gone. */ }
                    }
                }
            }
            catch (WorkspaceException error) when (error.Code == WorkspaceException.NotFound)
            {
                // Already gone.
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogWarning(error, "Agent compare run {Run}: its workspaces could not be removed.", id);
                failed.Add("workspaces");
            }

            // History: the run's row, its models' rows and their calls (also calls whose model row went first, as the retention may do).
            try { Runs?.Delete([id, .. run.Models.Select(model => AgentCompareRun.ModelKey(id, model.Slot))]); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogWarning(error, "Agent compare run {Run}: its History rows could not be deleted.", id);
                failed.Add("history");
            }

            foreach (var model in run.Models) ForgetCheck(id, model.Slot);
            if (failed.Count > 0)
            {
                log.LogWarning("Agent compare run {Run} was only partly deleted ({Failed}); the rest is deleted at the next start.", id, string.Join(", ", failed));
                return new AgentCompareDeleted(id, false, sessions, removed, kept, failed);
            }
            store.Delete(id);
            log.LogInformation("Agent compare run {Run} deleted: {Sessions} sessions, {Workspaces} workspaces{Kept}.", id, sessions, removed.Count,
                kept.Count > 0 ? "; kept for chats: " + string.Join(", ", kept) : "");
            return new AgentCompareDeleted(id, true, sessions, removed, kept, []);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The app is stopping: the run stays hidden and its delete runs again at the next start.
            return NotDeleted(id);
        }
        catch (Exception error)
        {
            log.LogError(error, "Agent compare run {Run} could not be deleted; its delete runs again at the next start.", id);
            return NotDeleted(id);
        }
    }

    /// <summary>Whether <paramref name="model"/> continues as a chat that is not deleted (Open as chat).</summary>
    private bool ContinuedAsChat(string id, AgentCompareModelState model) =>
        (model.ChatId is { } chat && bindings.Get(chat) is { IsArchived: false })
        || bindings.FindByRequest(ChatRequestId(id, model.Slot)) is { IsArchived: false };

    /// <summary>Deletes the runs whose runs.db row is gone (History deleted or cleaned it; with <paramref name="includeActive"/> false, a run
    /// still running is left alone: the retention never removes a running row).</summary>
    private void Prune(bool includeActive)
    {
        if (Runs is not { } runs) return;
        try
        {
            foreach (var run in store.All())
            {
                if (run.Deleting || (!includeActive && run.IsActive)) continue;
                if (runs.Get(run.Id) is null) StartDelete(run.Id);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogWarning(error, "Agent compare runs whose History row is gone could not be deleted.");
        }
    }

    // runs.db changed: History deleted rows (by id, or all with Clear) or the retention cleaned some. A handler must not throw.
    private void OnRunsChanged(object? sender, RunChange change)
    {
        try
        {
            switch (change.Kind)
            {
                case "deleted" when change.Ids.Count > 0:
                    foreach (var id in change.Ids)
                        if (store.Get(id) is not null) StartDelete(id);
                    break;
                case "deleted":
                    Prune(includeActive: true);
                    break;
                case "cleaned":
                    Prune(includeActive: false);
                    break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogWarning(error, "Agent compare: a History change could not be applied to its runs.");
        }
    }
}
