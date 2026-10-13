// Authored for the examples (no upstream counterpart): a manual clock in place of Node's timers, for the examples whose upstream output
// depends on setTimeout (21-late-join, 23-subagent-background, 24-child-tasks). Passed as HarnessOptions.TimeProvider it drives the Harness
// clock, sleeps and throttles; the examples' own pauses and scripted streaming wait on it too. Nothing moves until the example advances it,
// so a slow machine cannot reorder what upstream's event loop orders by time.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

/// <summary>A clock that moves only when advanced; due timers fire in due order, ties in creation order.</summary>
public sealed class ExampleClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;
    private long created;

    /// <summary>A clock starting at the current wall-clock time, so message timestamps look like upstream's.</summary>
    public ExampleClock() : this(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) { }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    /// <summary>The clock in milliseconds (upstream <c>Date.now()</c>).</summary>
    public double NowMs() => GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>When the next timer is due, or null when none is pending.</summary>
    public DateTimeOffset? NextDue
    {
        get { lock (gate) return timers.Where(timer => timer.Due is not null).Min(timer => timer.Due); }
    }

    /// <summary>Waits <paramref name="ms"/> on this clock (upstream <c>setTimeout</c>).</summary>
    public Task Delay(double ms, CancellationToken cancellationToken = default) =>
        Task.Delay(TimeSpan.FromMilliseconds(ms), this, cancellationToken);

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timer.Order = created++;
            timers.Add(timer);
        }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock to the next due timer and fires every timer due then; false when no timer is pending.</summary>
    public bool FireNext()
    {
        List<ManualTimer> due;
        lock (gate)
        {
            if (timers.Where(timer => timer.Due is not null).Min(timer => timer.Due) is not { } next) return false;
            if (next > now) now = next;
            due = [.. timers.Where(timer => timer.Due is { } at && at <= now).OrderBy(timer => timer.Due).ThenBy(timer => timer.Order)];
            foreach (var timer in due) timer.Due = timer.Period is { } period ? now + period : null;
        }
        foreach (var timer in due) timer.Fire();
        return true;
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, firing due timers in due order on the way.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate) target = now + by;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.Due is { } due && due <= target).OrderBy(timer => timer.Due).ThenBy(timer => timer.Order)
                    .FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }
                now = next.Due!.Value;
                next.Due = next.Period is { } period ? now + period : null;
            }
            next.Fire();
        }
    }

    /// <summary>Fires the earliest due timer alone (ties in creation order), moving the clock to it; false when no timer is pending.</summary>
    public bool FireOne()
    {
        ManualTimer? next;
        lock (gate)
        {
            next = timers.Where(timer => timer.Due is not null).OrderBy(timer => timer.Due).ThenBy(timer => timer.Order).FirstOrDefault();
            if (next is null) return false;
            if (next.Due!.Value > now) now = next.Due.Value;
            next.Due = next.Period is { } period ? now + period : null;
        }
        next.Fire();
        return true;
    }

    // ─── Running the clock like an event loop ──────────────────────────────

    private int commitsInFlight;

    /// <summary>Counts <paramref name="storage"/>'s commits in flight, so <see cref="SettleAsync"/> waits for their I/O.</summary>
    public IStorage Observe(IStorage storage) => new ObservedStorage(storage ?? throw new ArgumentNullException(nameof(storage)), this);

    /// <summary>
    /// Waits until everything the last timer started has run: on five checks in a row, no observed commit is in flight, no thread-pool work
    /// is queued, and the pool completed no work item but this wait's own. Upstream's event loop runs every callback a timer starts before
    /// the next timer; this is the same rule for the thread pool. It waits longer on a slow machine; it does not fire anything early.
    /// </summary>
    public async Task SettleAsync()
    {
        var completed = ThreadPool.CompletedWorkItemCount;
        for (var quiet = 0; quiet < 5;)
        {
            await Task.Delay(1).ConfigureAwait(false);
            var now = ThreadPool.CompletedWorkItemCount;
            var idle = Volatile.Read(ref commitsInFlight) == 0 && ThreadPool.PendingWorkItemCount == 0 && now - completed <= 1;
            completed = now;
            quiet = idle ? quiet + 1 : 0;
        }
    }

    /// <summary>Runs timers in order, settling after each, until <paramref name="ms"/> have passed (upstream <c>await pause(ms)</c>).</summary>
    public async Task RunForAsync(double ms)
    {
        DateTimeOffset target;
        lock (gate) target = now + TimeSpan.FromMilliseconds(ms);
        while (true)
        {
            await SettleAsync().ConfigureAwait(false);
            if (NextDue is { } due && due <= target)
            {
                FireOne();
                continue;
            }
            lock (gate) if (now < target) now = target;
            await SettleAsync().ConfigureAwait(false);
            return;
        }
    }

    /// <summary>Runs timers in order, settling after each, until <paramref name="task"/> completes, and returns its result.</summary>
    public async Task<T> RunUntilAsync<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        await RunUntilAsync((Task)task).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    /// <summary>Runs timers in order, settling after each, until <paramref name="task"/> completes.</summary>
    public async Task RunUntilAsync(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        for (var stalled = 0; ; )
        {
            await SettleAsync().ConfigureAwait(false);
            if (task.IsCompleted) break;
            if (FireOne()) stalled = 0;
            else if (++stalled > 1000) throw new InvalidOperationException("Nothing is due and the awaited work does not finish.");
        }
        await task.ConfigureAwait(false);
    }

    private sealed class ObservedStorage(IStorage inner, ExampleClock clock) : IStorage
    {
        public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            Interlocked.Increment(ref clock.commitsInFlight);
            try { return await inner.CommitAsync(writes, context).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref clock.commitsInFlight); }
        }
        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            inner.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanEntriesAsync(query, limit, cursor, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            inner.FindDocumentAsync(address, at, context);
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanDocumentsAsync(query, limit, cursor, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }

    private sealed class ManualTimer(ExampleClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimeSpan? Period { get; private set; }
        public long Order { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                Due = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
