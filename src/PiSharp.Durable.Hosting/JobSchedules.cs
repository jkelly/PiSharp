// PiSharp-only: durable recurring admission above the task scheduler (decision 0009).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>A persisted recurring job. Missed slots coalesce; an active previous run skips the next slot.</summary>
public sealed record JobSchedule(string Id, string SessionId, long AgentId, string JobId, JsonObjectValue Input,
    int EveryMinutes, bool Enabled, long AnchorAt, int Revision = 1, long LastSlot = 0, long? LastTaskId = null,
    string? LastStatus = null, string? LastError = null)
{
    /// <summary>The next unprocessed slot, as Unix milliseconds.</summary>
    public long NextAt => checked(AnchorAt + (LastSlot + 1) * EveryMinutes * 60_000L);
    /// <summary>The wire representation.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([
        new("id", Id), new("sessionId", SessionId), new("agentId", AgentId), new("jobId", JobId), new("input", Input),
        new("everyMinutes", EveryMinutes), new("enabled", Enabled), new("anchorAt", AnchorAt), new("revision", Revision),
        new("lastSlot", LastSlot), new("lastTaskId", LastTaskId is { } task ? task : JsonValue.Null),
        new("lastStatus", LastStatus is { } status ? status : JsonValue.Null), new("lastError", LastError is { } error ? error : JsonValue.Null),
        new("nextAt", Enabled ? NextAt : JsonValue.Null)]);

    internal static JobSchedule FromJson(JsonObjectValue value) => new(Jobs.String(value, "id")!, Jobs.String(value, "sessionId")!,
        Jobs.Long(value, "agentId")!.Value, Jobs.String(value, "jobId")!, (JsonObjectValue)value["input"],
        (int)Jobs.Long(value, "everyMinutes")!.Value, value["enabled"] is JsonBooleanValue { Value: true }, Jobs.Long(value, "anchorAt")!.Value,
        (int)Jobs.Long(value, "revision")!.Value, Jobs.Long(value, "lastSlot")!.Value, Jobs.Long(value, "lastTaskId"),
        Jobs.String(value, "lastStatus"), Jobs.String(value, "lastError"));
}

/// <summary>The outcome of ticking a schedule; a replay returns the same admission.</summary>
public sealed record ScheduleTick(string ScheduleId, long Slot, string Status, long? TaskId = null, string? Reason = null)
{
    /// <summary>The wire representation.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([new("scheduleId", ScheduleId), new("slot", Slot), new("status", Status),
        new("taskId", TaskId is { } task ? task : JsonValue.Null), new("reason", Reason is { } reason ? reason : JsonValue.Null)]);
}

public sealed partial class DurableDeviceHost
{
    private readonly CancellationTokenSource scheduleStop = new();
    private Task scheduleLoop = Task.CompletedTask;

    /// <summary>Every persisted schedule. Credential secrets are never part of a schedule.</summary>
    public IReadOnlyList<JobSchedule> Schedules() { ThrowIfDisposed(); return Catalog.Schedules(); }

    /// <summary>Creates a schedule. Its first run is due after one interval.</summary>
    public async Task<JobSchedule> CreateScheduleAsync(string sessionId, long agentId, string jobId, int everyMinutes,
        JsonObjectValue? input = null, CancellationToken cancellationToken = default)
    {
        ValidateInterval(everyMinutes);
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = ResolveSession(sessionId);
            var job = AllJobs.FirstOrDefault(job => job.Id == jobId) ?? throw new HostNotFoundException($"No job {jobId}.");
            var conversation = await session.ConversationForToolAsync(new ConversationId(agentId), DurableContext.Background.WithCancellation(cancellationToken)).ConfigureAwait(false);
            // Task-owned conversations are short-lived; a schedule must belong to a stable agent.
            if (!(await session.AgentsAsync(cancellationToken).ConfigureAwait(false)).Any(agent => agent.Id == conversation.Id.Value && agent.OwnerTaskId is null))
                throw new HostInvalidException("Choose an agent that is not owned by a task.");
            // Numeric jobs keep their finite-number inputs; a typed job's input is checked against its schema by Normalize.
            if (job.InputSchema is null && job.Admit is null && input?.Values.Any(value => value is not JsonNumberValue number || !double.IsFinite(number.Value)) == true)
                throw new HostInvalidException("Schedule inputs must be finite numbers.");
            var schedule = new JobSchedule(Guid.NewGuid().ToString("D"), session.Id, agentId, jobId, Hosting.Jobs.Normalize(job, input),
                everyMinutes, true, Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            Catalog.SaveSchedule(schedule);
            return schedule;
        }
        finally { mutations.Release(); }
    }

    /// <summary>Pauses/resumes a schedule, or changes its interval (which resets the clock). The target and inputs stay fixed.</summary>
    public async Task<JobSchedule> UpdateScheduleAsync(string id, bool? enabled, int? everyMinutes = null, CancellationToken cancellationToken = default)
    {
        if (everyMinutes is { } interval) ValidateInterval(interval);
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = RequireSchedule(id);
            var changed = current with { Enabled = enabled ?? current.Enabled };
            if (everyMinutes is { } next && next != current.EveryMinutes)
                changed = changed with { EveryMinutes = next, AnchorAt = Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    Revision = checked(current.Revision + 1), LastSlot = 0, LastStatus = null, LastError = null };
            Catalog.SaveSchedule(changed);
            return changed;
        }
        finally { mutations.Release(); }
    }

    /// <summary>Removes a schedule and revokes credentials restricted to it. Already admitted jobs continue.</summary>
    public async Task DeleteScheduleAsync(string id, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); Catalog.DeleteSchedule(RequireSchedule(id).Id); }
        finally { mutations.Release(); }
    }

    /// <summary>Admits a due slot once. Triggers cannot force paused, early or overlapping runs.</summary>
    public async Task<ScheduleTick> TickScheduleAsync(string id, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var schedule = RequireSchedule(id);
            var now = Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var slot = Math.Max(0, (now - schedule.AnchorAt) / (schedule.EveryMinutes * 60_000L));
            if (!schedule.Enabled) return new(schedule.Id, slot, "paused");
            if (slot <= schedule.LastSlot) return new(schedule.Id, slot, "not_due", schedule.LastTaskId);
            try
            {
                var result = await ResolveSession(schedule.SessionId).StartScheduledJobAsync(schedule, slot, cancellationToken).ConfigureAwait(false);
                Catalog.SaveSchedule(schedule with { LastSlot = slot, LastTaskId = result.TaskId ?? schedule.LastTaskId,
                    LastStatus = result.Status, LastError = result.Reason });
                return result;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Do not acknowledge admission failures: this slot can be retried after its target recovers.
                Catalog.SaveSchedule(schedule with { LastStatus = "error", LastError = error.Message });
                throw;
            }
        }
        finally { mutations.Release(); }
    }

    private JobSchedule RequireSchedule(string id) => (Guid.TryParse(id, out var guid) ? Catalog.Schedule(guid.ToString("D")) : null)
        ?? throw new HostNotFoundException($"No schedule {id}.");

    private static void ValidateInterval(int minutes)
    { if (minutes is < 1 or > 525600) throw new HostInvalidException("The interval must be 1 to 525600 minutes."); }

    private void StartSchedules()
    {
        if (!Options.RunSchedules) return;
        scheduleLoop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(Options.SchedulePollInterval, Options.TimeProvider);
                do
                {
                    foreach (var schedule in Catalog.Schedules())
                    {
                        try { await TickScheduleAsync(schedule.Id, scheduleStop.Token).ConfigureAwait(false); }
                        catch (Exception error) when (error is not OperationCanceledException)
                        { Options.OnReport?.Invoke(schedule.SessionId, error); }
                    }
                } while (await timer.WaitForNextTickAsync(scheduleStop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (scheduleStop.IsCancellationRequested) { }
        });
    }
}

public sealed partial class HostedSession
{
    private static readonly ConversationDocFamilyToken ScheduleReceipts = Documents.DefineConversationDocFamily(
        "pisharp.schedule-receipt", 1, DocumentFork.Initial, _ => JsonObjectValue.Empty, checkpointWhen: (_, _, _) => false);

    internal async Task<ScheduleTick> StartScheduledJobAsync(JobSchedule schedule, long slot, CancellationToken cancellationToken)
    {
        var job = host.AllJobs.FirstOrDefault(item => item.Id == schedule.JobId) ?? throw new HostNotFoundException($"No job {schedule.JobId}.");
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), schedule.AgentId, context).ConfigureAwait(false);
        return await conversation.CommitAsync(async tx =>
        {
            var receipt = await tx.DocAsync(ScheduleReceipts, conversation.Id, $"{schedule.Id}:{schedule.Revision}", JsonValue.Null).ConfigureAwait(false);
            var earlier = receipt.ToValue() as JsonObjectValue ?? JsonObjectValue.Empty;
            if (Hosting.Jobs.Long(earlier, "slot") is { } admitted && admitted >= slot)
                return new ScheduleTick(schedule.Id, admitted, Hosting.Jobs.String(earlier, "status")!, Hosting.Jobs.Long(earlier, "taskId"), Hosting.Jobs.String(earlier, "reason"));
            var previousId = Hosting.Jobs.Long(earlier, "taskId") ?? schedule.LastTaskId;
            var previous = previousId is { } task ? await tx.GetTaskAsync(new TaskId(task)).ConfigureAwait(false) : null;
            // A catalog acknowledgement can be lost before an interval edit starts a new revision. Find the admitted run by its
            // immutable task input too, so resetting the clock cannot bypass the same schedule's overlap policy.
            if (previous is null || previous.State.Status == DurableTaskStatus.Terminal)
                previous = await ActiveScheduledTaskAsync(tx, conversation.Id, schedule).ConfigureAwait(false);
            previousId = previous?.Id.Value ?? previousId;
            var active = previous is not null && previous.State.Status != DurableTaskStatus.Terminal;
            TaskId? created = null;
            if (!active)
                created = await Hosting.Jobs.StartAsync(tx, conversation.Id, job, jobTasks[job.Id], schedule.Input, TaskOwnership.Conversation,
                    true, "schedule", null, host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(), schedule.Id, slot).ConfigureAwait(false);
            var result = new ScheduleTick(schedule.Id, slot, active ? "skipped" : "started", created?.Value ?? previousId, active ? "The previous run is still active." : null);
            // Keep the previous task through skipped slots so subsequent slots still check the actual active run.
            receipt.Set("slot", slot);
            receipt.Set("status", result.Status);
            receipt.Set("taskId", created?.Value is { } fresh ? fresh : previousId is { } old ? old : JsonValue.Null);
            receipt.Set("reason", result.Reason is { } reason ? reason : JsonValue.Null);
            return result;
        }, context).ConfigureAwait(false);
    }

    private static async Task<TaskRecord?> ActiveScheduledTaskAsync(ITx tx, ConversationId conversation, JobSchedule schedule)
    {
        foreach (var status in new[] { DurableTaskStatus.Pending, DurableTaskStatus.Running, DurableTaskStatus.Waiting, DurableTaskStatus.Completing })
        {
            JsonObjectValue? cursor = null;
            do
            {
                var page = await tx.ScanTasksAsync(new TaskQuery { ConversationId = conversation, Kind = "pisharp.job." + schedule.JobId, Status = status }, 100, cursor).ConfigureAwait(false);
                foreach (var task in page.Items)
                    if (task.Input is JsonObjectValue input && Hosting.Jobs.String(input, "scheduleId") == schedule.Id) return task;
                cursor = page.Next;
            } while (cursor is not null);
        }
        return null;
    }
}
