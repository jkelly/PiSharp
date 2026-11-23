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
    // ─── The commit ─────────────────────────────────────────────────────────

    private async Task<Ran<T>> RunCommitAsync<T>(Func<Transaction, Task<T>> change, DurableContext context, TransactionScope? scope,
        double? withinMs, long index)
    {
        AssertHealthy();
        // Cancellation is checked when the job starts (changelog 1.5.0, Changed#7): a job may start while earlier commits are stored.
        context.ThrowIfCancellationRequested();
        var tx = new Transaction(host, context, scope);
        T result;
        try
        {
            result = await change(tx).ConfigureAwait(false);
            // A callback that caught a failed read must not commit, nor succeed as if it had.
            AssertHealthy();
        }
        catch
        {
            await tx.SettleFailureAsync().ConfigureAwait(false);
            SweepDocuments();
            throw;
        }
        IReadOnlyList<StorageWrite> writes;
        try
        {
            writes = await tx.SettleSuccessAsync().ConfigureAwait(false);
        }
        catch
        {
            // Settlement aborted every change already.
            SweepDocuments();
            throw;
        }
        if (writes.Count == 0)
        {
            tx.Discard();
            SweepDocuments();
            return new Ran<T>(result);
        }
        if (readOnly)
        {
            // Refused before Storage: the Session stays healthy, and the caller sees why.
            tx.Discard();
            SweepDocuments();
            throw new ReadOnlySessionException("a commit with writes");
        }
        if (withinMs is { } within && tx.Holdable() is { } held)
        {
            // Adopted in memory when the commits before it are published, as a part of no storage call.
            var heldPart = writer.Admit(new PartSpec(index, null, tx.WriteKeys(), context,
                (_, unregister) => HoldCommit(tx, held, within, context, unregister), tx.Discard));
            SweepDocuments();
            return new Ran<T>(result, heldPart.Outcome);
        }
        IReadOnlyList<string> membership;
        try
        {
            membership = await MembershipKeysAsync(tx).ConfigureAwait(false);
        }
        catch
        {
            tx.Discard();
            SweepDocuments();
            throw;
        }
        // Logical admission. Held changes leave their queue only here, once nothing but Storage can fail; a commit never stores a hold adopted
        // after it in line order. Taken before the keys, so the documents it stores held changes of are its writes too: a later job that reads
        // one waits for it instead of storing them again.
        List<Hold> taken;
        lock (holdsGate) taken = holds.Count == 0 ? NoHolds : tx.TakeHeld<Hold>(id => holds.GetValueOrDefault(id));
        var keys = tx.WriteKeys();
        keys.AddRange(membership);
        var part = writer.Admit(new PartSpec(index, writes, keys, context,
            (seq, unregister) => AdoptCommit(tx, writes, taken, seq!.Value, context, unregister), () =>
            {
                OnDiscard?.Invoke();
                tx.Discard();
            }));
        SweepDocuments();
        return new Ran<T>(result, part.Outcome);
    }

    /// <summary>
    /// Publishes a stored commit (Writer.cs; upstream <c>#adopt</c>): adopts it in memory, drops its keys, and runs its publication. A throw
    /// of the adoption leaves memory behind durable state; the writer fails the Session with it.
    /// </summary>
    private PublishFailure? AdoptCommit(Transaction tx, IReadOnlyList<StorageWrite> writes, List<Hold> taken, Seq seq, DurableContext context,
        Action unregister)
    {
        AdoptedDocuments documentChanges;
        try
        {
            if (AdoptFault?.Invoke() is { } fault) throw fault;
            documentChanges = tx.Adopt(seq);
        }
        catch (Exception error)
        {
            return PublishFailure.OfAdoption(error);
        }
        // Before the keys leave, as a task's documents are read on the line once its scope's key has no unpublished writer.
        IndexTaskDocuments(tx, writes, documentChanges, seq);
        // Before the keys leave: a job that then touches a document whose held changes this commit stored must not find them held.
        if (taken.Count > 0) Release(taken);
        var failure = Publish(seq, writes, documentChanges, context, unregister);
        foreach (var adopted in documentChanges.Stored)
        {
            var record = adopted switch
            {
                DocumentCommitChange document => document.Record,
                DocumentCopyCommitChange copy => copy.Record,
                _ => null,
            };
            if (record is not null) TouchDocument(Documents.AddressId(DocumentAddress.Of(record)));
        }
        return failure is null ? null : PublishFailure.OfListener(failure);
    }

    /// <summary>
    /// At publication: keeps the task-document index in step with a stored commit (upstream <c>#indexTaskDocuments</c>): the tasks it creates
    /// start with no documents, task documents it creates join their task, ones it retires leave, and tasks it ends leave the index.
    /// </summary>
    private void IndexTaskDocuments(Transaction tx, IReadOnlyList<StorageWrite> writes, AdoptedDocuments documentChanges, Seq seq)
    {
        var created = tx.CreatedTaskIds();
        lock (taskDocumentsGate)
        {
            foreach (var id in created) taskDocuments[id] = [];
            if (taskDocuments.Count == 0) return;
            foreach (var change in documentChanges.Stored)
            {
                if (change is not DocumentCommitChange { Record: { Scope.TaskId: { } taskId } record } document) continue;
                if (!taskDocuments.TryGetValue(taskId, out var known)) continue;
                if (document.Kind == DocumentCommitChangeKind.Retire) known.Remove(record.Id);
                else if (record.CreatedAt == seq) known[record.Id] = record;
            }
            foreach (var write in writes)
                if (write is TaskWrite { Value: { State.Status: DurableTaskStatus.Terminal } ended }) taskDocuments.Remove(ended.Id);
        }
    }

    /// <summary>
    /// At publication: publishes a stored commit (upstream <c>#publish</c>). Its telemetry events first, listeners or not, then the Session's
    /// own observers, then the dispatch to host observers, then the deliveries, waits and abort signals they scheduled (<see cref="Defer"/>,
    /// <see cref="AfterPublication"/>). Upstream queues the dispatch a microtask ahead of the waits and aborts its own observers wake, and
    /// only when a host observer is attached at the publication; the dispatch calls the observers attached when it runs. Here it runs right
    /// after the own observers, under the same rule: none when no host observer was attached at the publication, and the observers attached
    /// by then otherwise (one an own observer attached or detached is in or out). Never throws: returns the first error of an own observer,
    /// which fails the Session once its storage call settled, after the hosts got the commit. <paramref name="unregister"/> drops the
    /// commit's keys once the Session's own observers adopted it, before host observers run: upstream drops them before the publication, as
    /// nothing else runs until it returns, but here a job on another thread that touched one in between would read scheduler memory that has
    /// not adopted the commit yet.
    /// </summary>
    private Exception? Publish(Seq seq, IReadOnlyList<StorageWrite> writes, AdoptedDocuments documentChanges, DurableContext context,
        Action unregister)
    {
        // Its telemetry events first, listeners or not.
        foreach (var write in writes)
        {
            if (write is EntryWrite entry)
                Event("pi.durable.entry", new SpanAttributes { ["entry.id"] = entry.Value.Id.Value, ["entry.kind"] = entry.Value.Kind }, context);
            else if (write is SubmissionWrite { Value: { Status: SubmissionStatus.Done or SubmissionStatus.Unanswered } submission })
                Event("pi.durable.submission", new SpanAttributes
                {
                    ["submission.id"] = submission.Id.Value,
                    ["status"] = WireName.Of(submission.Status),
                    ["answer.entry_id"] = submission.Answer?.Value,
                }, context);
        }
        Action<ObservedCommit, DurableContext>[] internals;
        bool hosts;
        lock (listenersGate)
        {
            internals = [.. internalListeners];
            hosts = commitListeners.Count > 0;
        }
        if (internals.Length == 0 && !hosts)
        {
            unregister();
            return null;
        }
        var changes = new List<ICommitChange>();
        foreach (var write in writes)
            if (write is ICommitChange change) changes.Add(change);
        var tables = changes.Count;
        changes.AddRange(documentChanges.Observed);
        var observed = ObservedCommit.Stored(seq, changes);
        // One list for both, unless the commit also stored held changes, which only durable listeners get.
        var stored = ReferenceEquals(documentChanges.Stored, documentChanges.Observed)
            ? new CommitPublication(seq, changes)
            : new CommitPublication(seq, [.. changes.Take(tables), .. documentChanges.Stored]);
        Exception? failure = null;
        RunPublishing(() =>
        {
            // The commit is durable: a failing listener neither fails the commit nor skips the others. A host observer's guard reports its
            // throw, detaches it and ends it listener_error.
            foreach (var listener in internals) Contained(() => listener(observed, context), error => failure ??= error);
            unregister();
            if (hosts) NotifyHosts(stored, context);
        });
        return failure;
    }

    /// <summary>At publication: calls the host commit observers attached now with <paramref name="publication"/> (upstream
    /// <c>#notifyHosts</c>'s dispatch); their guards report a throw.</summary>
    private void NotifyHosts(CommitPublication publication, DurableContext context)
    {
        ObserverGuard<CommitPublication>[] observers;
        lock (listenersGate) observers = [.. commitListeners];
        foreach (var observer in observers) observer.Advance(publication, context);
    }

    /// <summary>Runs <paramref name="publish"/> as this thread's publication: the deliveries it schedules (<see cref="Defer"/>) run right
    /// after it, before it returns.</summary>
    private void RunPublishing(Action publish)
    {
        var outer = publishing;
        publishing = (this, []);
        try
        {
            publish();
        }
        finally
        {
            RunDeferred();
            publishing = outer;
        }
    }

    // ─── Held commits ───────────────────────────────────────────────────────

    /// <summary>
    /// Holds a deferred commit at its publication (<see cref="Transaction.Holdable"/>; upstream <c>#hold</c>): adopts it in memory, queues its
    /// changes per document, pinned, by the earliest deadline of the document's held commits, and shows it to the Session's own observers.
    /// A throw of the adoption is the writer's to fail the Session with, as a stored commit's.
    /// </summary>
    private PublishFailure? HoldCommit(Transaction tx, List<HeldChange> held, double withinMs, DurableContext context, Action unregister)
    {
        List<ICommitChange> documentChanges;
        try
        {
            if (AdoptHeldFault?.Invoke() is { } fault) throw fault;
            documentChanges = tx.AdoptHeld();
        }
        catch (Exception error)
        {
            return PublishFailure.OfAdoption(error);
        }
        var deadline = clock() + withinMs;
        foreach (var (loaded, ops, isBase) in held)
        {
            var id = loaded.Record.Id;
            Hold? hold;
            lock (holdsGate) hold = holds.GetValueOrDefault(id);
            if (hold is null)
            {
                // Host code (the pin may arm a timer), outside holdsGate.
                var unpin = PinDocument(loaded.AddressId, id);
                hold = new Hold(loaded, deadline, unpin);
                lock (holdsGate) holds.Add(id, hold);
            }
            lock (holdsGate)
            {
                hold.Pending.Push(ops);
                if (isBase) hold.Base = true;
                if (deadline < hold.Deadline) hold.Deadline = deadline;
            }
        }
        unregister();
        ArmHoldTimer();
        Action<ObservedCommit, DurableContext>[] internals;
        lock (listenersGate) internals = [.. internalListeners];
        if (internals.Length == 0) return null;
        var publication = ObservedCommit.Memory(documentChanges);
        Exception? failure = null;
        RunPublishing(() =>
        {
            foreach (var listener in internals) Contained(() => listener(publication, context), error => failure ??= error);
        });
        return failure is null ? null : PublishFailure.OfListener(failure);
    }

    /// <summary>The held documents, in the order they were first held.</summary>
    private List<Hold> HeldDocuments()
    {
        lock (holdsGate) return [.. holds.Values];
    }

    private int HoldCount
    {
        get { lock (holdsGate) return holds.Count; }
    }

    /// <summary>
    /// Stores the held changes <paramref name="select"/> returns in one storage transaction, through the writer, from a job on the line
    /// (upstream <c>#storeHolds</c>). A barrier: they are selected once every commit admitted before is published. Memory already has them, so
    /// only <c>ObserveCommits</c> observers see it, published as the writer publishes it. A failure fails the Session, as every Storage failure
    /// does, and is this task's error.
    /// </summary>
    private async Task StoreHoldsAsync(Func<List<Hold>> select, DurableContext context)
    {
        await DrainedAsync().ConfigureAwait(false);
        var held = select();
        if (held.Count == 0) return;
        var writes = new List<StorageWrite>(held.Count);
        var changes = new List<ICommitChange>(held.Count);
        var keys = new List<string>(held.Count * 2);
        foreach (var hold in held)
        {
            var loaded = hold.Loaded;
            var value = loaded.Value;
            var version = loaded.ValueVersion;
            (DocumentContent content, ImmutableArray<Op> ops) stored;
            lock (holdsGate) stored = Transaction.StoreHeld(hold, NoOperations, value, version);
            writes.Add(new DocumentChangeWrite(loaded.Record.Id, stored.content));
            keys.Add(LineKeys.Document(loaded.AddressId));
            keys.Add(LineKeys.Incarnation(loaded.Record.Id));
            // Held documents are conversation documents.
            changes.Add(new DocumentValueChange(loaded.Record, loaded.Record.Scope.ConversationId, version, value, stored.ops));
        }
        PublishFailure? Publish(Seq? seq, Action unregister)
        {
            for (var index = 0; index < held.Count; index++)
            {
                var loaded = held[index].Loaded;
                if (((DocumentChangeWrite)writes[index]).Content is DocumentBase) loaded.DeltasSinceBase = 0;
                else loaded.DeltasSinceBase++;
            }
            Release(held);
            unregister();
            bool hosts;
            lock (listenersGate) hosts = commitListeners.Count > 0;
            if (!hosts) return null;
            var publication = new CommitPublication(seq!.Value, changes);
            RunPublishing(() => NotifyHosts(publication, context));
            return null;
        }
        // Run from a job on the line, which a failure it causes is the outcome of.
        var index = current?.Index ?? Interlocked.Read(ref nextIndex) - 1;
        await writer.Admit(new PartSpec(index, writes, keys, context, Publish, static () => { })).Outcome.ConfigureAwait(false);
    }

    /// <summary>After their store: forgets <paramref name="held"/>, unpins their documents, and moves the timer to the next deadline (upstream
    /// <c>#release</c>).</summary>
    private void Release(IReadOnlyList<Hold> held)
    {
        lock (holdsGate)
            foreach (var hold in held) holds.Remove(hold.Loaded.Record.Id);
        foreach (var hold in held) hold.Unpin();
        ArmHoldTimer();
    }

    /// <summary>
    /// Keeps a foreground timer (<see cref="HarnessOptions.SetTimer"/>) for the earliest deadline of the held documents (upstream
    /// <c>#armHoldTimer</c>). When it fires, every held document is stored, in one storage transaction: a deadline is the latest time, and
    /// the others cost little more in the same transaction. Once fired, it stays until its job ran. Called on the line and at publication.
    /// </summary>
    private void ArmHoldTimer()
    {
        HoldTimer? armedTimer = null;
        Action? replaced;
        var delay = 0.0;
        // Host code, read outside the lock.
        var current = clock();
        lock (holdsGate)
        {
            if (closingStarted) return;
            var at = double.PositiveInfinity;
            foreach (var hold in holds.Values)
                if (hold.Deadline < at) at = hold.Deadline;
            var armed = holdTimer;
            if (armed is not null && (armed.Fired || armed.At == at)) return;
            replaced = armed?.Disarm;
            holdTimer = null;
            if (!double.IsPositiveInfinity(at))
            {
                armedTimer = new HoldTimer(at);
                holdTimer = armedTimer;
                delay = Math.Max(0, at - current);
            }
        }
        if (replaced is not null) Contained(replaced, SetTimerError);
        if (armedTimer is not { } timer) return;
        void Run()
        {
            lock (holdsGate) timer.Fired = true;
            var stored = Maintain(async _ =>
            {
                lock (holdsGate)
                    if (ReferenceEquals(holdTimer, timer)) holdTimer = null;
                // Close stores what is held itself.
                if (closingStarted || failure is not null) return true;
                await StoreHoldsAsync(HeldDocuments, DurableContext.Background).ConfigureAwait(false);
                return true;
            }, inline: false);
            _ = stored.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        var disarm = timers.Foreground(Run, delay);
        lock (holdsGate)
        {
            // Still the armed timer: keep its disarm.
            if (ReferenceEquals(holdTimer, timer) && !closingStarted)
            {
                timer.Disarm = disarm;
                return;
            }
            // Fired already (its job owns it now), or replaced or closed meanwhile.
            if (timer.Fired) return;
        }
        Contained(disarm, SetTimerError);
    }

    /// <summary>At publication: records one of a commit's telemetry events (upstream <c>#event</c>). A throwing adapter is reported, as
    /// publication must not throw.</summary>
    private void Event(string name, SpanAttributes attributes, DurableContext context)
    {
        try
        {
            context.Telemetry.AddEvent(name, attributes);
        }
        catch (Exception error)
        {
            Report(error, ReportInfo.HostError(HostCallback.Telemetry), context);
        }
    }

    /// <summary>
    /// Schedules observer delivery work (upstream <c>queueMicrotask</c>). During a publication on this thread it runs in that publication right
    /// after its listeners; otherwise it becomes its own line job, so it never runs inline in the caller.
    /// </summary>
    internal void Defer(Action work)
    {
        if (publishing is { } scope && ReferenceEquals(scope.Owner, this))
        {
            scope.Work.Add(work);
            return;
        }
        _ = Maintain(_ =>
        {
            Run(work);
            return Task.FromResult(true);
        }, inline: false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> after the host observers of the publication this thread runs, if it runs one of this Session's (upstream:
    /// a promise continuation or a microtask queued behind the dispatch to host observers), else at once: the waits a commit resolves and
    /// the aborts it signals, which must not resume before <c>ObserveCommits</c> observers saw it.
    /// </summary>
    internal void AfterPublication(Action work)
    {
        if (publishing is { } scope && ReferenceEquals(scope.Owner, this)) scope.Work.Add(work);
        else work();
    }

    private void RunDeferred()
    {
        if (publishing is not { } scope || !ReferenceEquals(scope.Owner, this)) return;
        // Deliveries may schedule more deliveries; they join the list.
        for (var index = 0; index < scope.Work.Count; index++) Run(scope.Work[index]);
    }

    private static void Run(Action work)
    {
        try { work(); }
        catch (Exception error) { ReplicatedState.ReportUnhandled(error); }
    }

    /// <summary>
    /// Runs a host callback that must not fail its caller (upstream <c>contained</c>): a throw goes to <paramref name="onError"/>, which is
    /// not awaited.
    /// </summary>
    internal static void Contained(Action callback, Action<Exception> onError)
    {
        try { callback(); }
        catch (Exception error) { onError(error); }
    }

    /// <summary>Reports a faulted or cancelled task a host callback returned (upstream's rejected promise in <c>contained</c>), without
    /// awaiting it.</summary>
    internal static void Observe(Task? task, Action<Exception> onError)
    {
        if (task is null || task.IsCompletedSuccessfully) return;
        task.ContinueWith(static (completed, state) =>
        {
            var report = (Action<Exception>)state!;
            Exception error = completed.IsFaulted
                ? completed.Exception!.InnerExceptions.Count == 1 ? completed.Exception.InnerException! : completed.Exception
                : new TaskCanceledException(completed);
            try { report(error); }
            catch (Exception reportError) { ReplicatedState.ReportUnhandled(reportError); }
        }, onError, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Follows the address of the incarnation <paramref name="loaded"/>, after checking the definition, with the observer
    /// <paramref name="create"/> makes from its value, the source's idempotent detach, and the report of a listener error; returns that
    /// observer (upstream <c>#observeDocument</c>). See <see cref="DocumentFollower"/>.
    /// </summary>
    private TObserver ObserveDocument<TObserver>(DocDefinition definition, LoadedDocument loaded,
        Func<JsonObjectValue, Action, Action<Exception>, TObserver> create) where TObserver : SyncObserver<JsonObjectValue>
    {
        Documents.CheckRecordScope(definition, loaded.Record);
        Documents.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        var follower = new DocumentFollower(this, loaded);
        var observer = create(loaded.Value, follower.Detach, Report);
        follower.Attach(observer);
        return observer;
    }

    private async Task<LoadedDocument?> LoadDocumentAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context)
    {
        // What an unpublished commit wrote at the address is in neither the cache nor what Storage shows yet.
        if (Touch(LineKeys.Document(addressId)) is { } gate) await gate.ConfigureAwait(false);
        LoadedDocument? cached;
        lock (documentsGate) cached = documents.GetValueOrDefault(addressId);
        // A tracker serves only tokens of the version its value was materialized for; others reload from Storage.
        if (cached is not null && cached.ValueVersion == definition.Version)
        {
            TouchDocument(addressId);
            return cached;
        }
        if (cached is not null)
        {
            // The reload must find what memory holds: held changes are stored before their tracker goes, after a barrier. On the line, as
            // every load is.
            var id = cached.Record.Id;
            await StoreHoldsAsync(() =>
            {
                lock (holdsGate) return holds.GetValueOrDefault(id) is { } hold ? [hold] : NoHolds;
            }, context).ConfigureAwait(false);
            lock (documentsGate)
                if (documents.TryGetValue(addressId, out var current) && ReferenceEquals(current, cached)) DropDocumentLocked(addressId);
        }
        var record = await storage.FindDocumentAsync(address, DocumentPoint.Current, context).ConfigureAwait(false);
        if (record is null) return null;
        var stored = await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, context).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Current document {record.Id} ({record.Kind}) cannot be read");
        var value = Documents.MaterializeDocument(definition, stored);
        var loaded = new LoadedDocument(addressId, stored.Record, stored.Version, definition.Version, stored.DeltasSinceBase, Tracker.Track(value));
        lock (documentsGate) documents[addressId] = loaded;
        TouchDocument(addressId);
        return loaded;
    }

    private LoadedDocument? Cached(string addressId)
    {
        lock (documentsGate) return documents.GetValueOrDefault(addressId);
    }

    /// <summary>Internal: throws <see cref="SessionFailedException"/> when failed, else an exception when closing.</summary>
    internal void AssertUsable()
    {
        AssertHealthy();
        if (closingStarted) throw new InvalidOperationException("Session is closed");
    }

    private void AssertHealthy()
    {
        if (failure is { } failed) throw new SessionFailedException(failed.Error);
    }

    private sealed record FailureBox(Exception Error);

    /// <summary>
    /// What a document observer follows (upstream <c>#observeDocument</c>): the address (kind, scope and key) of the incarnation it was
    /// attached at, from that incarnation on. Its observer, a watch (<see cref="WatchObserver{T}"/>) or a guarded host observer
    /// (<see cref="GuardedObserver{T}"/>), gets each committed change there; a retirement as a <c>retired</c> call with the last value it was
    /// given and no ops (a change in the retiring commit is not published: that commit publishes the retirement only); a later incarnation,
    /// or a value of another definition version, whose shape the observer's value does not have, as a root replacement. When a commit makes
    /// the owner task of a task document terminal, no later commit can change the address: after its changes the commit subscription and
    /// the pin go, and <see cref="SyncObserver{T}.OwnerEnded"/> tells the observer (a watch ends <c>ended</c> once it delivered them, Chord's
    /// <c>endAfterDelivery</c>; a host observer ends <c>ended</c> at once), which stays subscribed to the Session's close until it ends. At
    /// close the source detaches, then ends the observer as <see cref="CloseEnds"/> says. The current incarnation is pinned in the cache,
    /// nothing while the address is absent. Once the detach ran, nothing more of the commit underway reaches the observer or pins anything
    /// for it.
    /// </summary>
    /// <remarks>
    /// Publications run on the line; the detach (a stop, an abort, a listener failure, an observation's detach) may run on any thread, so one
    /// short lock guards what is followed and is never held while the observer runs, a pin is released (which may arm a host timer) or a
    /// subscription is removed. Only the address is kept (the cache's ID and the first record), never <c>LoadedDocument</c>, whose tracker a
    /// retirement frees.
    /// </remarks>
    private sealed class DocumentFollower
    {
        private static readonly Action Unpinned = static () => { };

        private readonly Session session;
        private readonly string addressId;
        private readonly DocumentRecord address;
        private readonly Lock gate = new();
        private SyncObserver<JsonObjectValue>? observer;
        private IDisposable? commitSubscription;
        private IDisposable? closeSubscription;
        // Written under gate; read without it between calls, where a detach on another thread may clear it at any time.
        private volatile bool attached = true;
        // The incarnation followed and its value: none while the address is absent, so the Session keeps no retired value.
        private DocumentId? incarnation;
        private JsonObjectValue? value;
        // The definition version the observer's value has: a change of another version replaces it whole.
        private int version;
        // The incarnation followed stays cached; once it retires or the observer detaches, it is idle again.
        private Action unpin;

        public DocumentFollower(Session session, LoadedDocument loaded)
        {
            this.session = session;
            addressId = loaded.AddressId;
            address = loaded.Record;
            incarnation = loaded.Record.Id;
            value = loaded.Value;
            version = loaded.ValueVersion;
            unpin = session.PinDocument(addressId, loaded.Record.Id);
        }

        /// <summary>Subscribes <paramref name="followed"/> to the Session's commits and close; a Session closing meanwhile attaches nothing.</summary>
        public void Attach(SyncObserver<JsonObjectValue> followed)
        {
            observer = followed;
            try
            {
                commitSubscription = session.FollowCommits(Follow);
                closeSubscription = session.SubscribeClose(Close);
            }
            catch
            {
                // Close or failure began while the document loaded: attach nothing.
                Detach();
                throw;
            }
        }

        /// <summary>The source's detach: idempotent; once it ran, nothing more of a commit underway reaches the observer or pins anything for
        /// it.</summary>
        public void Detach()
        {
            Action release;
            IDisposable? commits;
            IDisposable? closes;
            lock (gate)
            {
                if (!attached) return;
                attached = false;
                release = unpin;
                unpin = Unpinned;
                commits = commitSubscription;
                closes = closeSubscription;
                commitSubscription = null;
                closeSubscription = null;
                incarnation = null;
                value = null;
            }
            commits?.Dispose();
            closes?.Dispose();
            release();
        }

        /// <summary>The Session's close: detach, then end the observer, a watch <c>ended</c>, an observer <c>failed</c> with "session closed"
        /// (or both with the error that failed the Session).</summary>
        private void Close()
        {
            Detach();
            if (observer is { } followed) followed.End(CloseEnds.Of(session.Failure).For(followed.IsWatch));
        }

        private void Follow(ObservedCommit publication, DurableContext context)
        {
            // Listeners run from a copy of the list: another observer's call may have detached this one.
            if (!attached || observer is not { } followed) return;
            var ownerEnds = false;
            foreach (var change in publication.Changes)
            {
                if (change is TaskWrite task)
                {
                    if (address.Scope.Kind == DocumentScopeKind.Task && task.Value.Id.Value == address.Scope.OwnerId &&
                        task.Value.State.Status == DurableTaskStatus.Terminal)
                        ownerEnds = true;
                    continue;
                }
                if (change is not DocumentCommitChange document) continue;
                WatchValueCall<JsonObjectValue> call;
                Action? released = null;
                lock (gate)
                {
                    // A detach on another thread since the last call: the rest of the commit is not the observer's.
                    if (!attached) return;
                    // The incarnation followed is at the address by definition; only another record needs the address compared.
                    var record = document.Record;
                    if (record.Id != incarnation && !AtAddress(record)) continue;
                    if (document is not DocumentValueChange valued)
                    {
                        if (record.Id != incarnation) continue;
                        // Set with every incarnation.
                        var last = value!;
                        incarnation = null;
                        value = null;
                        Release(ref released);
                        call = new WatchValueCall<JsonObjectValue>(last, NoOperations, Retired: true);
                    }
                    else if (record.Id != incarnation || valued.Version != version)
                    {
                        // A new incarnation at the address, or a value of another definition version, whose shape the observer's value
                        // does not have: shown whole.
                        if (record.Id != incarnation)
                        {
                            incarnation = record.Id;
                            Release(ref released);
                            unpin = session.PinDocument(addressId, record.Id);
                        }
                        version = valued.Version;
                        value = valued.Value;
                        call = new WatchValueCall<JsonObjectValue>(valued.Value, [new ReplaceOp(valued.Value)]);
                    }
                    else
                    {
                        // A migration-only base changes nothing for an observer of the new version.
                        if (valued.Ops.IsEmpty) continue;
                        value = valued.Value;
                        call = new WatchValueCall<JsonObjectValue>(valued.Value, valued.Ops);
                    }
                }
                released?.Invoke();
                followed.Advance(call, context);
                // The observer may detach in its call, or throw and be detached: the rest of the commit is not its.
                if (!attached) return;
            }
            if (!ownerEnds) return;
            IDisposable? commits;
            Action? ownerReleased = null;
            lock (gate)
            {
                if (!attached) return;
                commits = commitSubscription;
                commitSubscription = null;
                Release(ref ownerReleased);
            }
            commits?.Dispose();
            ownerReleased?.Invoke();
            followed.OwnerEnded();
        }

        /// <summary>Takes the pin of the incarnation followed, to release outside the lock.</summary>
        private void Release(ref Action? released)
        {
            var pin = unpin;
            unpin = Unpinned;
            if (!ReferenceEquals(pin, Unpinned)) released += pin;
        }

        /// <summary>Whether <paramref name="record"/> is an incarnation at the address followed (upstream <c>atAddress</c>).</summary>
        private bool AtAddress(DocumentRecord record) =>
            record.Kind == address.Kind && record.Key == address.Key && record.Scope.Kind == address.Scope.Kind &&
            record.Scope.OwnerId == address.Scope.OwnerId;
    }

    /// <summary>The ops of a retirement call (upstream <c>NO_OPERATIONS</c>).</summary>
    private static readonly ImmutableArray<Op> NoOperations = [];

    /// <summary>No holds taken (upstream <c>NO_HOLDS</c>).</summary>
    private static readonly List<Hold> NoHolds = [];

    /// <summary>A held document (<see cref="HeldDocument"/>) as the Session keeps it: pinned in the cache until stored (upstream
    /// <c>Hold</c>). Used on the line only.</summary>
    private sealed class Hold(LoadedDocument loaded, double deadline, Action unpin) : HeldDocument(loaded)
    {
        /// <summary>When the earliest of its held commits must be stored, on the Session clock.</summary>
        public double Deadline { get; set; } = deadline;
        public Action Unpin { get; } = unpin;
    }

    /// <summary>The armed store of held documents (upstream <c>HoldTimer</c>): a foreground timer for the earliest deadline, <c>Fired</c> once
    /// its job is queued. Guarded by holdsGate.</summary>
    private sealed class HoldTimer(double at)
    {
        public double At { get; } = at;
        public Action? Disarm { get; set; }
        public bool Fired { get; set; }
    }

    private sealed class DocumentSweep(double at)
    {
        public double At { get; } = at;
        /// <summary>Disarms its timer, once armed. Guarded by documentsGate.</summary>
        public Action? Disarm { get; set; }
    }

    private sealed class Host(Session session) : ITransactionHost
    {
        /// <summary>Reads wait while their key has an unpublished writer (<see cref="GatedStorage"/>).</summary>
        public IStorage Storage => session.gated;
        public Task? Touch(string key) => session.Touch(key);
        public Task<EntryId?> LatestEntryIdAsync(ConversationId conversationId, DurableContext context) =>
            session.LatestEntryIdAsync(conversationId, context);
        public Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId, DurableContext context) =>
            session.LatestHeadMarkerAsync(conversationId, null, context);
        /// <summary>The documents of task <paramref name="taskId"/> by the Session's index (upstream <c>#taskDocuments</c>); null when the
        /// Session does not know them (a task an earlier Session created), which the transaction then scans for.</summary>
        public IEnumerable<DocumentRecord>? TaskDocuments(TaskId taskId)
        {
            lock (session.taskDocumentsGate)
                return session.taskDocuments.TryGetValue(taskId, out var known) ? [.. known.Values] : null;
        }
        public double Now() => session.now();
        public LoadedDocument? Cached(string addressId)
        {
            var cached = session.Cached(addressId);
            if (cached is not null) session.TouchDocument(addressId);
            return cached;
        }
        public Task<LoadedDocument?> LoadAsync(DocDefinition definition, string addressId, DocumentAddress address, DurableContext context) =>
            session.LoadDocumentAsync(definition, addressId, address, context);
        public void Install(LoadedDocument document)
        {
            lock (session.documentsGate) session.documents[document.AddressId] = document;
            session.TouchDocument(document.AddressId);
        }
        public void Evict(string addressId, DocumentId recordId)
        {
            lock (session.documentsGate)
                if (session.documents.TryGetValue(addressId, out var cached) && cached.Record.Id == recordId) session.DropDocumentLocked(addressId);
        }
        public Task ConversationCreatedAsync(Transaction tx, ConversationRecord record) => session.ConversationCreatedAsync(tx, record);
    }

    /// <summary>What the writer needs of its Session (upstream's <c>new Writer({...})</c> in the SessionImpl constructor).</summary>
    private sealed class WriterHost(Session session) : IWriterHost
    {
        public Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive,
            DurableContext context) => session.storage.CommitAsync(batches, receive, context);

        /// <summary>Applies a stored batch to the read caches and conversation tails, right before it is published (upstream's
        /// <c>stored</c> callback: <c>#cached.published</c> and <c>#tails.apply</c>).</summary>
        public void Stored(IReadOnlyList<StorageWrite> writes)
        {
            session.cached?.Published(writes);
            session.tails?.Apply(writes);
        }

        public Task? Publishing(Action publish, DurableContext context) => session.PublishingScope(publish, context);

        public Exception? Failure => session.failure?.Error;

        // Upstream's (error, at, own) => this.fail(error, undefined, at, own): reported without a commit's context.
        public void Fail(Exception error, long? at, IReadOnlyCollection<long> own) => session.Fail(error, null, at, own);

        public void Report(Exception error, DurableContext context) => session.Report(error, ReportInfo.ListenerError, context);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
