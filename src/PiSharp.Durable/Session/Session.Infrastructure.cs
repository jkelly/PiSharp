using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>
/// What the Session's own components observe of one commit (<c>Session.FollowCommits</c>; upstream <c>ObservedCommit</c>): a stored commit
/// with its own changes, or a deferred one adopted in memory only (<see cref="CommitPersistence"/>), which changes documents only. Memory is
/// never behind: a later store of held changes is not observed again.
/// </summary>
internal sealed class ObservedCommit
{
    private ObservedCommit(bool memory, Seq? seq, IReadOnlyList<ICommitChange> changes)
    {
        IsMemory = memory;
        Seq = seq;
        Changes = changes;
    }

    /// <summary>Whether it is <c>{kind: "memory"}</c>: adopted in memory and held, not stored yet.</summary>
    public bool IsMemory { get; }
    /// <summary>The commit sequence of a stored commit; null for one held in memory.</summary>
    public Seq? Seq { get; }
    /// <summary>The changes: of a held commit, its document changes only.</summary>
    public IReadOnlyList<ICommitChange> Changes { get; }

    /// <summary><c>{kind: "stored", seq, changes}</c>.</summary>
    public static ObservedCommit Stored(Seq seq, IReadOnlyList<ICommitChange> changes) => new(false, seq, changes);
    /// <summary><c>{kind: "memory", changes}</c>.</summary>
    public static ObservedCommit Memory(IReadOnlyList<ICommitChange> changes) => new(true, null, changes);
}

/// <summary>
/// The Session mutation line (upstream <c>#tail</c> / <c>#enqueue</c>): a FIFO queue of jobs run one at a time, in enqueue order. See the
/// concurrency model in Session.cs.
/// </summary>
internal sealed class SessionLine
{
    private readonly Lock gate = new();
    private readonly Queue<Func<Task>> jobs = new();
    private bool running;
    /// <summary>Jobs this thread enqueues inside <see cref="Together"/>, queued when it returns.</summary>
    [ThreadStatic] private static List<(SessionLine Line, Func<Task> Run, bool Inline)>? held;

    /// <summary>How many jobs wait behind the running one.</summary>
    public int Waiting
    {
        get { lock (gate) return jobs.Count; }
    }

    /// <summary>
    /// Enqueues <paramref name="job"/>, a job's line part. On an idle line it starts inline when <paramref name="inline"/> is true, else on
    /// the thread pool; <paramref name="inCaller"/>, when given, is set while it runs inline in the caller. The Session delivers outcomes
    /// (Session.Enqueue and Session.Maintain): the line only runs the line parts, one at a time.
    /// </summary>
    public void Enqueue(Func<Task> job, bool inline = true, StrongBox<bool>? inCaller = null)
    {
        if (held is { } together)
        {
            together.Add((this, job, inline));
            return;
        }
        bool start;
        lock (gate)
        {
            jobs.Enqueue(job);
            start = !running;
            running = true;
        }
        if (!start) return;
        if (inline)
        {
            if (inCaller is not null) Volatile.Write(ref inCaller.Value, true);
            Pump();
            if (inCaller is not null) Volatile.Write(ref inCaller.Value, false);
        }
        else ThreadPool.UnsafeQueueUserWorkItem(static line => line.Pump(), this, preferLocal: false);
    }

    /// <summary>
    /// Runs <paramref name="enqueue"/>, holding the jobs it enqueues on this thread, and queues them when it returns, back to back under one
    /// lock: as upstream's enqueues in one synchronous stretch, no job another thread enqueues meanwhile gets between them, and on an idle
    /// line the first starts only once all are queued. A call inside another joins it.
    /// </summary>
    public static void Together(Action enqueue)
    {
        if (held is not null)
        {
            enqueue();
            return;
        }
        var together = held = [];
        try
        {
            enqueue();
        }
        finally
        {
            held = null;
            foreach (var line in together.Select(job => job.Line).Distinct())
                line.EnqueueHeld([.. together.Where(job => job.Line == line)]);
        }
    }

    private void EnqueueHeld(List<(SessionLine Line, Func<Task> Run, bool Inline)> together)
    {
        bool start;
        lock (gate)
        {
            foreach (var job in together) jobs.Enqueue(job.Run);
            start = !running;
            running = true;
        }
        if (!start) return;
        if (together[0].Inline) Pump();
        else ThreadPool.UnsafeQueueUserWorkItem(static line => line.Pump(), this, preferLocal: false);
    }

    private void Pump()
    {
        while (true)
        {
            Func<Task> next;
            lock (gate)
            {
                if (!jobs.TryDequeue(out next!))
                {
                    running = false;
                    return;
                }
            }
            var task = next();
            if (task.IsCompleted) continue;
            task.ContinueWith(static (_, state) => ((SessionLine)state!).Pump(), this, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
    }
}

/// <summary>
/// Upstream's microtask order for the line results of chosen flows (the scheduler's abort invocations). Upstream resumes every awaiting
/// handler in the order its line job ran, and each runs until it next waits before the next resumes. Here a result's continuation is
/// otherwise scheduled on the thread pool on its own, and a flow whose next job runs inline on an idle line goes on at once: of two flows that
/// each read and then commit, the one whose read ran second can commit first.
/// <para>A delivery runs segments one at a time. <see cref="RunAsync"/> runs a segment that starts flows; every line result of a flow that
/// set <see cref="Current"/> (in an async method, so the setting stays with that flow) is queued as the job that produced it ends, so in line
/// order, and set by the delivery, which runs the awaiting continuation inline until it next suspends. A flow inside a segment has waited
/// before the segment ends, so its result is never set before its continuation is registered (one registered late is scheduled on the thread
/// pool instead). Line jobs reset <see cref="Current"/>, so nothing they enqueue joins the flow. A flow resumed by anything else (a task
/// signal, a timer) runs outside the segments until it next waits on the line.</para>
/// </summary>
internal sealed class OrderedLineDelivery
{
    private static readonly AsyncLocal<OrderedLineDelivery?> current = new();
    [ThreadStatic] private static OrderedLineDelivery? executing;

    private readonly Lock gate = new();
    private readonly Queue<Action> pending = new();
    private bool running;

    /// <summary>The delivery of the current flow, if any.</summary>
    public static OrderedLineDelivery? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    /// <summary>Whether this thread runs one of the delivery's segments now.</summary>
    public bool Executing => executing == this;

    /// <summary>
    /// Runs <paramref name="segment"/> as a segment of its own: inline when the delivery is idle (or from inside one of its segments), else
    /// after the segments queued before it. Results its flows get meanwhile are delivered after it, on the thread pool.
    /// </summary>
    public Task RunAsync(Action segment)
    {
        if (Executing)
        {
            segment();
            return Task.CompletedTask;
        }
        lock (gate)
        {
            if (running)
            {
                var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Enqueue(() =>
                {
                    try
                    {
                        segment();
                        queued.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        queued.TrySetException(error);
                    }
                });
                return queued.Task;
            }
            running = true;
        }
        var previous = executing;
        executing = this;
        try
        {
            segment();
        }
        finally
        {
            executing = previous;
            bool more;
            lock (gate)
            {
                more = pending.Count > 0;
                if (!more) running = false;
            }
            if (more) ThreadPool.UnsafeQueueUserWorkItem(static delivery => delivery.Deliver(), this, preferLocal: false);
        }
        return Task.CompletedTask;
    }

    /// <summary>Queues one result as the line job that produced it ends, so results queue in line order.</summary>
    public void Post(Action deliver)
    {
        lock (gate)
        {
            pending.Enqueue(deliver);
            if (running) return;
            running = true;
        }
        // Never inline: this runs on the line, and the continuation would run there.
        ThreadPool.UnsafeQueueUserWorkItem(static delivery => delivery.Deliver(), this, preferLocal: false);
    }

    private void Deliver()
    {
        var previous = executing;
        executing = this;
        try
        {
            while (true)
            {
                Action next;
                lock (gate)
                {
                    if (!pending.TryDequeue(out next!))
                    {
                        running = false;
                        return;
                    }
                }
                next();
            }
        }
        finally
        {
            executing = previous;
        }
    }
}

/// <summary>
/// The state behind the Session's Storage guard (upstream <c>GuardedStorage</c>'s fields): the backend, the Session to fail, the calls
/// underway, and whether close has reached the backend. <see cref="GuardedStorage"/> views share it.
/// </summary>
internal sealed class StorageGuard(IStorage storage, Session session)
{
    private readonly Lock gate = new();
    // Backend calls underway, also those off the Session line, which close waits for before it closes the backend.
    private readonly HashSet<Task> underway = [];
    private int unstartedLeases;
    private TaskCompletionSource? leasesStarted;
    private bool closing;

    public IStorage Storage { get; } = storage;

    /// <summary>A lease for one read that continues off the line; see <see cref="Session.HoldStorageOpen"/>.</summary>
    public StorageLease Lease()
    {
        lock (gate) unstartedLeases++;
        return new StorageLease(this);
    }

    /// <summary>Marks a lease started (its first call admitted, or the lease released unused); close waits for every lease to start.</summary>
    internal void StartLease()
    {
        TaskCompletionSource? started = null;
        lock (gate)
        {
            if (--unstartedLeases == 0)
            {
                started = leasesStarted;
                leasesStarted = null;
            }
        }
        started?.TrySetResult();
    }

    /// <summary>
    /// Closes the backend once every lease has started and every call underway has settled, so none outlives it or fails after
    /// <c>Closed</c> settles. New calls are refused from here on: a paged read must not start its next page while the backend closes.
    /// </summary>
    public async Task CloseAsync(DurableContext context)
    {
        Task leases;
        lock (gate)
        {
            closing = true;
            if (unstartedLeases == 0) leases = Task.CompletedTask;
            else
            {
                leasesStarted ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                leases = leasesStarted.Task;
            }
        }
        await leases.ConfigureAwait(false);
        Task[] calls;
        lock (gate) calls = [.. underway];
        await Task.WhenAll(calls).ConfigureAwait(false);
        await Storage.CloseAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one backend call under the guard. <paramref name="read"/> is a read's context; <c>CommitAsync</c> and <c>MintIdAsync</c> pass
    /// none, so nothing exempts their exceptions: once a batch is admitted, whether it committed is unknown. A call still underway when another
    /// fails the Session ends with <see cref="SessionFailedException"/> too, whatever it returns: nothing it read or wrote is used.
    /// <paramref name="lease"/> admits its first call even while closing; its later calls are refused once the Session is closing.
    /// </summary>
    public async Task<T> CallAsync<T>(DurableContext? read, StorageLease? lease, Func<Task<T>> run, bool failsSession = true)
    {
        AssertHealthy();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        lock (gate)
        {
            var admitted = lease is not null && lease.TryStart();
            started = admitted;
            // A leased read's later pages are refused once close has begun, not only once it reached the backend: upstream's close reaches
            // Storage before such a read continues, and here a line job still underway may hold the closing job back.
            if (!admitted && (closing || (lease is not null && session.Closing))) throw new InvalidOperationException("Session is closed");
            underway.Add(done.Task);
        }
        if (started) StartLease();
        try
        {
            T result;
            try
            {
                result = await run().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                AssertHealthy();
                // An invalid read, or one its caller cancelled, says nothing about the Storage; it fails that call only.
                var exempt = read is not null && (error is StorageRequestException || read.IsCancellationRequested);
                if (!exempt && failsSession) session.Fail(error);
                throw;
            }
            AssertHealthy();
            return result;
        }
        finally
        {
            lock (gate) underway.Remove(done.Task);
            done.TrySetResult();
        }
    }

    private void AssertHealthy()
    {
        if (session.Failure is { } failure) throw new SessionFailedException(failure);
    }
}

/// <summary>
/// A read's hold on Storage across the Session line (see <see cref="Session.HoldStorageOpen"/>): its first call through <see cref="Storage"/>
/// is admitted even once close has begun, and close waits until it has started, or the lease is disposed unused. Its later calls are refused
/// once close has begun.
/// </summary>
internal sealed class StorageLease : IDisposable
{
    private readonly StorageGuard guard;
    private int state;

    internal StorageLease(StorageGuard guard)
    {
        this.guard = guard;
        Storage = new GuardedStorage(guard, this);
    }

    /// <summary>The guarded Storage the leased read calls.</summary>
    public IStorage Storage { get; }

    /// <summary>Claims the lease's first call; true once.</summary>
    internal bool TryStart() => Interlocked.CompareExchange(ref state, 1, 0) == 0;

    /// <summary>Releases an unused lease.</summary>
    public void Dispose()
    {
        if (TryStart()) guard.StartLease();
    }
}

/// <summary>
/// The Session's Storage behind its failure guard (upstream <c>GuardedStorage</c>); every component of the Session reads and writes through
/// it. Before a call, a failed Session gets <see cref="SessionFailedException"/> without reaching the backend. A call that throws fails the
/// Session and rethrows the backend's exception, unless it is a <see cref="StorageRequestException"/> or a read whose caller's context was
/// cancelled: neither says the Storage is broken. <see cref="CloseAsync"/> always reaches the backend, so a failed Session still closes it.
/// </summary>
internal sealed class GuardedStorage(StorageGuard guard, StorageLease? lease) : IStorage
{
    private IStorage Inner => guard.Storage;

    /// <summary>Guarded like every call, but its outcome is the Session writer's to judge: it publishes the batches received first, then fails
    /// the Session for a refusal (<see cref="CommitResult.Refused"/>) or a rejection, and for a Session that failed meanwhile.</summary>
    public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context) =>
        guard.CallAsync(null, lease, () => Inner.CommitAsync(batches, receive, context), failsSession: false);

    /// <summary>The backend's own publication scope (upstream <c>scopeOf</c>); not a call to guard.</summary>
    public void Publishing(Action publish, DurableContext context) => Inner.Publishing(publish, context);

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => guard.CallAsync(null, lease, Inner.MintIdAsync<TId>);

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetConversationAsync(id, context));

    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanConversationsAsync(query, limit, cursor, context));

    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetEntryAsync(id, context));

    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetEntryAsync(conversationId, id, context));

    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context));

    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanEntriesAsync(query, limit, cursor, context));

    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetTaskAsync(id, context));

    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanTasksAsync(query, limit, cursor, context));

    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetSubmissionAsync(id, context));

    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanSubmissionsAsync(query, limit, cursor, context));

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetSubmissionByRequestAsync(conversationId, requestId, context));

    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.FindDocumentAsync(address, at, context));

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.GetDocumentAsync(id, at, context));

    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        guard.CallAsync(context, lease, () => Inner.ScanDocumentsAsync(query, limit, cursor, context));

    /// <summary>Closes the backend after the calls underway; see <see cref="StorageGuard.CloseAsync"/>.</summary>
    public Task CloseAsync(DurableContext context) => guard.CloseAsync(context);
}
