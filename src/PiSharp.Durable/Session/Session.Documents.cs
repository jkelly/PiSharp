using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

public partial class Session
{
    // ─── Reads ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(SessionDocToken token, DurableContext context) => Snapshot(token, null, null, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        Snapshot(token, conversationId.Value, null, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocToken token, TaskId taskId, DurableContext context) => Snapshot(token, taskId.Value, null, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(SessionDocFamilyToken token, string key, DurableContext context) => Snapshot(token, null, key, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key, DurableContext context) =>
        Snapshot(token, conversationId.Value, key, context);
    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        Snapshot(token, taskId.Value, key, context);

    private async Task<JsonObjectValue?> Snapshot(DocumentToken token, long? owner, string? key, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        // An ordered flow (the scheduler's abort invocations) reads through the line even when the document is cached. A line job runs
        // inline in its caller here, where upstream's runs a microtask later: an earlier flow's cold load can then cache the document before
        // a later flow, resumed in the same round, reads it, and the later flow would take this fast path past the earlier one, which waits
        // for its delivery, and commit first (a caller's nested calls abandoned at a restart would settle out of call order). Through the
        // line, its read is delivered behind the earlier flow's, as upstream's continuations run.
        var cached = OrderedLineDelivery.Current is null ? Cached(resolved.Id) : null;
        if (cached?.ValueVersion == definition.Version) TouchDocument(resolved.Id);
        var loaded = cached?.ValueVersion == definition.Version
            ? cached
            : await Enqueue(async _ =>
            {
                AssertHealthy();
                return new Ran<LoadedDocument?>(await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false));
            }).ConfigureAwait(false);
        if (loaded is null) return null;
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return loaded.Value;
    }

    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocToken token, ConversationId conversationId, EntryId at, DurableContext context) =>
        SnapshotAsOf(token, conversationId, null, at, context);

    /// <inheritdoc/>
    public Task<JsonObjectValue?> SnapshotAsOfAsync(RewindableConversationDocFamilyToken token, ConversationId conversationId, string key, EntryId at,
        DurableContext context) => SnapshotAsOf(token, conversationId, key, at, context);

    private async Task<JsonObjectValue?> SnapshotAsOf(DocumentToken token, ConversationId conversationId, string? key, EntryId at, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, conversationId.Value, key);
        if (resolved.Address.Scope.Kind != DocumentScopeKind.Conversation)
            throw new ArgumentException("Session.snapshotAsOf() requires a conversation document");
        return await Enqueue(async _ =>
        {
            AssertHealthy();
            var storedEntry = await gated.GetEntryAsync(conversationId, at, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Entry {at} is not visible from conversation {conversationId}");
            var address = resolved.Address with { Scope = DocumentScope.Conversation(storedEntry.Entry.ConversationId) };
            var record = await gated.FindDocumentAsync(address, storedEntry.CommitSeq, context).ConfigureAwait(false);
            if (record is null) return new Ran<JsonObjectValue?>(null);
            var stored = await gated.GetDocumentAsync(record.Id, storedEntry.CommitSeq, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Historical document {record.Id} ({record.Kind}) cannot be read");
            return new Ran<JsonObjectValue?>(Documents.MaterializeDocument(definition, stored));
        }).ConfigureAwait(false);
    }

    // ─── Watches ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocToken token, DurableContext context) => WatchDoc(token, null, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocToken token, ConversationId conversationId, DurableContext context) =>
        WatchDoc(token, conversationId.Value, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocToken token, TaskId taskId, DurableContext context) =>
        WatchDoc(token, taskId.Value, null, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(SessionDocFamilyToken token, string key, DurableContext context) =>
        WatchDoc(token, null, key, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        DurableContext context) => WatchDoc(token, conversationId.Value, key, context);
    /// <inheritdoc/>
    public Task<IWatchHandle<JsonObjectValue>?> WatchDocAsync(TaskDocFamilyToken token, TaskId taskId, string key, DurableContext context) =>
        WatchDoc(token, taskId.Value, key, context);

    private async Task<IWatchHandle<JsonObjectValue>?> WatchDoc(DocumentToken token, long? owner, string? key, DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        var watch = await AttachDocumentAsync(definition, resolved, context,
            loaded => ObserveDocument(definition, loaded, (value, detach, report) =>
                new WatchObserver<JsonObjectValue>(new Watch<JsonObjectValue>(value, detach, report, schedule: Defer))).Watch,
            static watch => watch.Abort()).ConfigureAwait(false);
        if (watch is not null && context.HasCancellation) watch.ObserveAbort(context.CancellationToken);
        return watch;
    }

    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(SessionDocToken token, Observer<JsonObjectValue> observer, DurableContext context) =>
        ObserveDoc(token, null, null, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(ConversationDocToken token, ConversationId conversationId,
        Observer<JsonObjectValue> observer, DurableContext context) => ObserveDoc(token, conversationId.Value, null, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(TaskDocToken token, TaskId taskId, Observer<JsonObjectValue> observer,
        DurableContext context) => ObserveDoc(token, taskId.Value, null, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(SessionDocFamilyToken token, string key, Observer<JsonObjectValue> observer,
        DurableContext context) => ObserveDoc(token, null, key, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(ConversationDocFamilyToken token, ConversationId conversationId, string key,
        Observer<JsonObjectValue> observer, DurableContext context) => ObserveDoc(token, conversationId.Value, key, observer, context);
    /// <inheritdoc/>
    public Task<Observation<JsonObjectValue>?> ObserveDocAsync(TaskDocFamilyToken token, TaskId taskId, string key,
        Observer<JsonObjectValue> observer, DurableContext context) => ObserveDoc(token, taskId.Value, key, observer, context);

    /// <summary>Upstream <c>observeDoc</c>: asynchronous, so a throw here faults the task, as upstream's rejects.</summary>
    private async Task<Observation<JsonObjectValue>?> ObserveDoc(DocumentToken token, long? owner, string? key, Observer<JsonObjectValue> observer,
        DurableContext context)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(context);
        var definition = token.Definition;
        var resolved = Documents.ResolveAddress(definition, owner, key);
        return await AttachDocumentAsync(definition, resolved, context, loaded =>
        {
            var guarded = ObserveDocument(definition, loaded, (_, detach, report) =>
                new GuardedObserver<JsonObjectValue>(observer, detach, (error, _) => report(error)));
            return new Observation<JsonObjectValue>(loaded.Value, guarded);
        }, static attached => attached.Detach()).ConfigureAwait(false);
    }

    /// <summary>
    /// On the line, as one job: loads the document at <paramref name="resolved"/> and attaches to it with <paramref name="attach"/>, so the
    /// attachment sees every commit after the value it starts from; null when absent (upstream <c>#attachDocument</c>). Cancelling
    /// <paramref name="context"/> meanwhile faults the task with the cancellation error, after <paramref name="cancel"/> undid an attachment
    /// made.
    /// </summary>
    private async Task<TAttached?> AttachDocumentAsync<TAttached>(DocDefinition definition, ResolvedAddress resolved, DurableContext context,
        Func<LoadedDocument, TAttached> attach, Action<TAttached> cancel) where TAttached : class
    {
        var flag = new StrongBox<int>(context.IsCancellationRequested ? 1 : 0);
        var registration = context.CancellationToken.UnsafeRegister(static state => Volatile.Write(ref ((StrongBox<int>)state!).Value, 1), flag);
        try
        {
            var attached = await Enqueue(async _ =>
            {
                AssertHealthy();
                if (Volatile.Read(ref flag.Value) != 0) context.ThrowIfCancellationRequested();
                var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context).ConfigureAwait(false);
                if (Volatile.Read(ref flag.Value) != 0) context.ThrowIfCancellationRequested();
                return new Ran<TAttached?>(loaded is null ? null : attach(loaded));
            }).ConfigureAwait(false);
            if (attached is null) return null;
            if (Volatile.Read(ref flag.Value) != 0)
            {
                cancel(attached);
                context.ThrowIfCancellationRequested();
            }
            return attached;
        }
        finally
        {
            registration.Unregister();
        }
    }

    // ─── Lifecycle ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public virtual Task CloseAsync(DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        BeginClose(context)?.Invoke();
        return context.WaitAsync(closing!);
    }

    /// <summary>
    /// The synchronous part of upstream's <c>close()</c>: seals admission and runs the close listeners. Returns the rest of close, which the
    /// caller starts at once, as upstream's next microtask: the scheduler's join (<see cref="BeforeCloseAsync"/>) and the line job that
    /// closes Storage, run inline up to their first wait, so the Storage guard refuses new calls before an explicit close returns. Null when
    /// close had begun.
    /// </summary>
    private Action? BeginClose(DurableContext context)
    {
        Action[] listeners;
        TaskCompletionSource closingSource;
        lock (closingGate)
        {
            if (closing is not null) return null;
            // Seal admission before anything else runs, then stop observers; admitted work settles before Storage closes.
            closingStarted = true;
            lock (listenersGate)
            {
                listeners = [.. closeListeners];
                closeListeners.Clear();
            }
            closingSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closing = closingSource.Task;
        }
        NotifyClose(listeners);
        var cleanup = context.WithoutCancellation();
        return () => _ = SettleClosingAsync(FinishCloseAsync(cleanup), closingSource);
    }

    private static async Task SettleClosingAsync(Task finishing, TaskCompletionSource closingSource)
    {
        try
        {
            await finishing.ConfigureAwait(false);
            closingSource.TrySetResult();
        }
        catch (Exception error)
        {
            closingSource.TrySetException(error);
        }
    }

    /// <summary>Joins what the Session runs, then closes Storage on the line once admitted work has settled; settles <see cref="Closed"/>.</summary>
    private async Task FinishCloseAsync(DurableContext cleanup)
    {
        try
        {
            await BeforeCloseAsync().ConfigureAwait(false);
            await Maintain(async _ =>
            {
                // After every commit admitted before the seal is published or never will be.
                await DrainedAsync().ConfigureAwait(false);
                // Last, after the writes at close (BeforeCloseAsync), while the trackers are still here. A failed Session writes nothing; a
                // failing store fails it, and the backend still closes.
                ExceptionDispatchInfo? unstored = null;
                // Only with something to store: otherwise the backend's close begins in this very job.
                if (failure is null && HoldCount > 0)
                {
                    try { await StoreHoldsAsync(HeldDocuments, cleanup).ConfigureAwait(false); }
                    catch (Exception error) { unstored = ExceptionDispatchInfo.Capture(error); }
                }
                ObserverGuard<CommitPublication>[] observers;
                lock (listenersGate)
                {
                    observers = [.. commitListeners];
                    commitListeners.Clear();
                    internalListeners.Clear();
                }
                // After the last publication: the commits admitted before the seal have settled, held changes are stored. Commit observers
                // are synchronous observers, all of them (CloseEnds).
                var end = CloseEnds.Of(failure?.Error).Observer;
                foreach (var observer in observers) observer.End(end);
                lock (documentsGate)
                {
                    documents.Clear();
                    idleDocuments.Clear();
                    idleIndex.Clear();
                    attachedDocuments.Clear();
                }
                lock (holdsGate) holds.Clear();
                lock (taskDocumentsGate) taskDocuments.Clear();
                try
                {
                    await storage.CloseAsync(cleanup).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // A Storage that cannot close is a failed one; an earlier failure stays the cause.
                    if (TrySetFailure(error)) Report(error, ReportInfo.StorageFailed);
                    throw;
                }
                unstored?.Throw();
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            closedSource.TrySetResult(failure is { } failed ? SessionEnd.Failed(failed.Error) : SessionEnd.Closed);
        }
    }

    /// <summary>Runs the close listeners once, each on its own: one that fails is reported, and the rest still run.</summary>
    private void NotifyClose(Action[] listeners)
    {
        Action? disarm;
        lock (documentsGate)
        {
            disarm = documentSweep?.Disarm;
            documentSweep = null;
        }
        if (disarm is not null) Contained(disarm, SetTimerError);
        Action? disarmHolds;
        lock (holdsGate)
        {
            disarmHolds = holdTimer?.Disarm;
            holdTimer = null;
        }
        if (disarmHolds is not null) Contained(disarmHolds, SetTimerError);
        foreach (var listener in listeners) Contained(listener, Report);
    }

    /// <summary>
    /// Runs inside every transaction that creates or forks a conversation, after the conversation record is staged. A plain Session stages
    /// nothing; a Harness stages its built-in documents.
    /// </summary>
    internal virtual Task ConversationCreatedAsync(Transaction tx, ConversationRecord record) => Task.CompletedTask;

    /// <summary>Runs after the close listeners (and, for a failure, its report) and before the line closes Storage; must not fail.</summary>
    internal virtual Task BeforeCloseAsync() => Task.CompletedTask;

    /// <summary>
    /// Internal, for <see cref="BeforeCloseAsync"/>: on the line, after every commit admitted before the seal is published or never will be,
    /// writes what <paramref name="writes"/> returns then to Storage, in one batch, through the writer (upstream <c>writeAtClose</c>). Nothing
    /// is published, since the Session is closing; a failed Session writes nothing. A Storage that throws or refuses the batch fails the
    /// Session, and the returned task faults with that error.
    /// </summary>
    internal Task WriteAtCloseAsync(Func<IReadOnlyList<StorageWrite>> writes) => Maintain(async index =>
    {
        await DrainedAsync().ConfigureAwait(false);
        if (failure is not null) return true;
        var batch = writes();
        if (batch.Count == 0) return true;
        // Through the writer, as its own part: a refusal or a rejection fails the Session there, and is this task's error.
        await writer.Admit(new PartSpec(index, batch, [], DurableContext.Background, static (_, _) => null, static () => { })).Outcome
            .ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc/>
    public CommitObservation ObserveCommits(CommitObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        AssertUsable();
        ObserverGuard<CommitPublication>? guarded = null;
        guarded = new ObserverGuard<CommitPublication>(observer.Advance, observer.End, () =>
        {
            lock (listenersGate) commitListeners.Remove(guarded!);
        }, (error, context) => Report(error, ReportInfo.ListenerError, context));
        lock (listenersGate)
        {
            AssertUsable();
            commitListeners.Add(guarded);
        }
        return new CommitObservation(guarded);
    }

    /// <summary>
    /// Internal: follows every commit as memory adopts it (<see cref="ObservedCommit"/>), for the Session's own components, whose state
    /// follows memory (upstream <c>followCommits</c>). A throw leaves that state behind, so it fails the Session instead of being reported.
    /// They run before host observers.
    /// </summary>
    internal IDisposable FollowCommits(Action<ObservedCommit, DurableContext> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        AssertUsable();
        // A distinct wrapper per subscription, so two subscriptions of one delegate are independent and removal is exact.
        Action<ObservedCommit, DurableContext> entry = (publication, context) => listener(publication, context);
        lock (listenersGate)
        {
            AssertUsable();
            internalListeners.Add(entry);
        }
        return new Unsubscriber(() =>
        {
            lock (listenersGate) internalListeners.Remove(entry);
        });
    }

    /// <inheritdoc/>
    public IDisposable SubscribeClose(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        AssertUsable();
        Action entry = () => listener();
        lock (listenersGate)
        {
            AssertUsable();
            closeListeners.Add(entry);
        }
        return new Unsubscriber(() =>
        {
            lock (listenersGate) closeListeners.Remove(entry);
        });
    }

    /// <inheritdoc/>
    public IDisposable SubscribeClose(Func<Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return SubscribeClose(() => Observe(listener(), Report));
    }

    /// <summary>
    /// Internal: called from a line job by a read that continues off the line, such as <c>Conversation.ContextAsync</c>; its first call through
    /// the lease's <see cref="StorageLease.Storage"/> is admitted even once close has begun, and close waits for it; its later calls are
    /// refused from then on. See the file header.
    /// </summary>
    internal StorageLease HoldStorageOpen() => guard.Lease();

    /// <summary>Internal: drops every loaded tracker on the mutation line, held changes stored first; later access cold-loads from
    /// Storage.</summary>
    internal Task UnloadDocumentsAsync() => Maintain(async _ =>
    {
        if (failure is null) await StoreHoldsAsync(HeldDocuments, DurableContext.Background).ConfigureAwait(false);
        lock (documentsGate)
        {
            documents.Clear();
            idleDocuments.Clear();
            idleIndex.Clear();
        }
        return true;
    });

    /// <summary>Internal, for tests: how many documents the cache holds, how many are idle, how many incarnations are pinned, how many
    /// sweep jobs were queued on the line so far, and how many documents have held changes (upstream <c>documentCacheSizes</c>).</summary>
    internal (int Cached, int Idle, int Attached, int Sweeps, int Held) DocumentCacheSizes
    {
        get
        {
            int held;
            lock (holdsGate) held = holds.Count;
            lock (documentsGate) return (documents.Count, idleDocuments.Count, attachedDocuments.Count, sweepJobs, held);
        }
    }

    /// <summary>Internal, for tests: how many live tasks have their documents indexed, and how many conversations have their newest entry
    /// kept (upstream <c>documentCacheSizes</c>' <c>taskDocuments</c> and <c>tails</c>; apart from <see cref="DocumentCacheSizes"/>, whose
    /// shape a benchmark reads by reflection).</summary>
    internal (int TaskDocuments, int Tails) IndexSizes
    {
        get
        {
            lock (taskDocumentsGate) return (taskDocuments.Count, tails?.Count ?? 0);
        }
    }

    // ─── Document cache retention ───────────────────────────────────────────

    /// <summary>Notes a use of a cached document: an unattached one becomes the newest idle one, and an eviction is scheduled.</summary>
    private void TouchDocument(string addressId)
    {
        var at = clock();
        var retention = documentRetentionMs();
        bool schedule;
        lock (documentsGate)
        {
            if (!documents.TryGetValue(addressId, out var cached) || attachedDocuments.ContainsKey(cached.Record.Id)) return;
            // A hot document moves to the back at most once per granule, a tenth of the retention up to 1 s: it may then leave up to a
            // granule early, which costs a reload, while a commit-heavy workload skips the reordering.
            if (idleIndex.TryGetValue(addressId, out var node))
            {
                if (at - node.Value.Since < Math.Min(1_000, retention / 10)) return;
                idleDocuments.Remove(node);
            }
            idleIndex[addressId] = idleDocuments.AddLast((addressId, at));
            // A pending sweep for the oldest document comes first unless the retention was shortened since; then it moves up.
            schedule = documentSweep is null || documentSweep.At > at + retention;
        }
        if (schedule) ScheduleDocumentSweep();
    }

    private void DropDocumentLocked(string addressId)
    {
        documents.Remove(addressId);
        if (idleIndex.Remove(addressId, out var node)) idleDocuments.Remove(node);
    }

    /// <summary>
    /// Schedules the eviction of the oldest idle document: on the line at once when it is due, otherwise when it expires, by a background
    /// timer (<see cref="HarnessOptions.SetTimer"/>), which never keeps the process alive. Where the host declines it, none is kept: the end
    /// of each commit evicts what expired.
    /// </summary>
    private void ScheduleDocumentSweep()
    {
        var current = clock();
        var retention = documentRetentionMs();
        DocumentSweep sweep;
        Action? replaced;
        bool arm;
        double delay;
        lock (documentsGate)
        {
            if (closingStarted) return;
            // Documents with unpublished changes are not swept: their publication touches them again.
            var oldest = idleDocuments.First;
            while (oldest is not null && writer.Gate(LineKeys.Document(oldest.Value.AddressId)) is not null) oldest = oldest.Next;
            if (oldest is null) return;
            var at = oldest.Value.Since + retention;
            if (documentSweep is { } pending && pending.At <= at) return;
            replaced = documentSweep?.Disarm;
            // The pending sweep stays set until its job runs, so touches while the line is busy queue no more.
            sweep = new DocumentSweep(at);
            documentSweep = sweep;
            delay = at - current;
            arm = false;
            if (delay <= 0) QueueDocumentSweepLocked(sweep);
            else if (documentTimersUnavailable) documentSweep = null;
            else arm = true;
        }
        if (replaced is not null) Contained(replaced, SetTimerError);
        if (!arm) return;
        // A timer that fires early sweeps nothing and arms the next one.
        var disarm = timers.Background(() =>
        {
            lock (documentsGate) QueueDocumentSweepLocked(sweep);
        }, delay);
        lock (documentsGate)
        {
            if (disarm is null)
            {
                documentTimersUnavailable = true;
                if (ReferenceEquals(documentSweep, sweep)) documentSweep = null;
                return;
            }
            if (ReferenceEquals(documentSweep, sweep) && !closingStarted)
            {
                sweep.Disarm = disarm;
                return;
            }
        }
        // Replaced, swept or closed while it was armed.
        Contained(disarm, SetTimerError);
    }

    /// <summary>Reports a host timer's throw, such as its disarm's.</summary>
    private void SetTimerError(Exception error) => Report(error, ReportInfo.HostError(HostCallback.SetTimer));

    /// <summary>Queues the sweep job on the line; called under <see cref="documentsGate"/>, and never runs the job inline.</summary>
    private void QueueDocumentSweepLocked(DocumentSweep sweep)
    {
        sweepJobs++;
        _ = Maintain(_ =>
        {
            bool sweepNow;
            lock (documentsGate)
            {
                if (ReferenceEquals(documentSweep, sweep)) documentSweep = null;
                sweepNow = !closingStarted;
            }
            if (sweepNow) SweepDocuments();
            return Task.FromResult(true);
        }, inline: false);
    }

    /// <summary>On the line: evicts the idle documents whose retention passed, oldest first, then schedules the next eviction.</summary>
    private void SweepDocuments()
    {
        var expired = clock() - documentRetentionMs();
        lock (documentsGate)
        {
            for (var node = idleDocuments.First; node is not null && node.Value.Since <= expired;)
            {
                var next = node.Next;
                // A document with an unpublished change stays until its publication touches it.
                if (writer.Gate(LineKeys.Document(node.Value.AddressId)) is null) DropDocumentLocked(node.Value.AddressId);
                node = next;
            }
        }
        ScheduleDocumentSweep();
    }

    /// <summary>Pins a cached incarnation for one observer; the returned release unpins it once, idle again after the last.</summary>
    private Action PinDocument(string addressId, DocumentId recordId)
    {
        lock (documentsGate)
        {
            attachedDocuments[recordId] = attachedDocuments.GetValueOrDefault(recordId) + 1;
            if (idleIndex.Remove(addressId, out var node)) idleDocuments.Remove(node);
        }
        var pinned = 1;
        return () =>
        {
            if (Interlocked.Exchange(ref pinned, 0) == 0) return;
            bool touch;
            lock (documentsGate)
            {
                var count = attachedDocuments.GetValueOrDefault(recordId, 1) - 1;
                if (count > 0)
                {
                    attachedDocuments[recordId] = count;
                    return;
                }
                attachedDocuments.Remove(recordId);
                touch = documents.TryGetValue(addressId, out var cached) && cached.Record.Id == recordId;
            }
            if (touch) TouchDocument(addressId);
        };
    }

}
