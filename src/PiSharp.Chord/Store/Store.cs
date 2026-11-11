// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/store.ts
//
// The store (slice 6a §2, §3, slice 6b §1): opened over a backend, one commit queue that serializes only the commit step (the seal fence's
// admission check, BEGIN, the writes in their order, the outbox commit, lastSeq and the allocator's value, COMMIT, then the owner's
// adoption), the id allocator, the claims of its tables and documents, reads guarded as Durable's GuardedStorage guards them, Durable's
// Storage semantics for a refused step (D4), and the stop after an unknown outcome. Area cv2-store-tables added this file with CommitFence
// and the interface IChordStore (what table handles and the eager migrations call); area cv2-store-engine ports the rest.
//
// Upstream runs on one JavaScript thread, each queued step a promise continuation. Here a continuation may run on any thread: the queue's
// tail, the store's state and its counters are kept under a short lock, a queued step never starts inline (it yields first when nothing is
// ahead of it, as a promise's then does), and the catalog is replaced, never changed in place, after a COMMIT. Upstream's Store interface
// is IStore (also IAsyncDisposable, C#'s form of a resource the app owns and closes); its export(context) is Export, whose StoreExport and
// the parts it reads are in Export.cs (area cv2-store-archive, which added it here). ChordStore's members whose types are internal here
// (TableRecord, CommitFence, Attachment) are internal: table(), tables, commit(), attach() and replay(); claims and batch are public, for
// adapters (area store-storage: Durable's StoreStorage in PiSharp.Durable.Store), with only the members they use public. Its error for a
// seq past the safe range is an InvalidOperationException, and a close's abort is the context's OperationCanceledException (upstream's
// awaitWithContext rejects with the signal's reason).
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Store;

/// <summary>A store as apps hold it: opened by <see cref="Tables.OpenStoreAsync"/>, owned and closed by the app (upstream <c>Store</c>).</summary>
public interface IStore : IAsyncDisposable
{
    /// <summary>The seq of the store's last commit (0 for a new store).</summary>
    long LastSeq { get; }

    /// <summary>The outbox the sync layer reads; null without one.</summary>
    Outbox? Outbox { get; }

    /// <summary>The store's state after its <c>LastSeq</c> as archive parts (slice 6a §13), holding the commit queue from the first
    /// <c>MoveNextAsync</c> until the iteration ends, throws, is disposed or <paramref name="context"/> aborts (Export.cs).</summary>
    StoreExport Export(ChordContext context);

    /// <summary>Closes the store: waits for a running commit step, rejects queued commits and reads <c>released</c>, closes the backend.
    /// Idempotent, also after <c>storage_failed</c>; <paramref name="context"/> ends only this caller's wait (the close goes on).</summary>
    Task CloseAsync(ChordContext context);
}

/// <summary>What <see cref="Tables.OpenStoreAsync"/> takes besides the backend (upstream <c>StoreOptions</c>).</summary>
public sealed record StoreOptions
{
    /// <summary>Turns the outbox on (<c>true</c>) or off (<c>false</c>); null, the persisted setting stays (a new store: off).</summary>
    public bool? Outbox { get; init; }
}

public static partial class Tables
{
    /// <summary>Opens a store over <paramref name="backend"/>: the store's schema migrations, the UTF-8 check, the metadata read once, the
    /// outbox switched when asked. A failure closes the backend and fails (rule 12; upstream <c>openStore</c>).</summary>
    public static async Task<IStore> OpenStoreAsync(Backend backend, ChordContext context, StoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(context);
        var db = backend.Db;
        try
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            var meta = await StoreInternal.SwitchOutboxAsync(db, await StoreInternal.ApplyStoreSchemaAsync(db).ConfigureAwait(false), options?.Outbox)
                .ConfigureAwait(false);
            return new ChordStore(db, meta, await StoreInternal.ReadCatalogAsync(db).ConfigureAwait(false));
        }
        catch (Exception)
        {
            try
            {
                await db.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The open's own failure is the one to report.
            }
            throw;
        }
    }
}

/// <summary>The seal fence's two checks of a commit (§1.3), as the commit step calls them (upstream <c>CommitFence</c>).</summary>
/// <param name="Admit">At the step's entry: <c>false</c> rejects the commit <c>released</c> and writes nothing.</param>
/// <param name="Adopt">After <c>COMMIT</c>: adopts the commit, the ids being its created documents' ids in the order of their writes.</param>
internal sealed record CommitFence(Func<bool> Admit, Action<long, IReadOnlyList<long>> Adopt)
{
    /// <summary>The number of table rows changed by the successful commit, reported immediately before adoption.</summary>
    internal Action<long>? Rows { get; init; }

    /// <summary>The fence of work no attempt owns (an activation's preparation, an adapter): every commit admitted, nothing adopted
    /// (upstream's <c>{ admit: () =&gt; true, adopt: () =&gt; {} }</c>).</summary>
    internal static CommitFence Open { get; } = new(() => true, (_, _) => { });
}

/// <summary>The store as declared tables use it (upstream <c>ChordStore</c>'s members that <c>table-handle.ts</c> and
/// <c>table-migration.ts</c> call).</summary>
internal interface IChordStore
{
    /// <summary>The recorded table <paramref name="name"/> (its SQL name), as of the last commit.</summary>
    TableRecord? Table(string name);

    /// <summary>The version table <paramref name="name"/> is at or is being migrated to: the recorded one, or a higher one a migration
    /// announced before its first commit (<see cref="AnnounceTable"/>), so an old handle is refused before its SQL could queue behind the
    /// migration's DDL (fix round 1, Sol 8).</summary>
    long? TableVersion(string name);

    /// <summary>Announces a migration of table <paramref name="name"/> to <paramref name="version"/> until the returned withdrawal (after
    /// its last commit).</summary>
    Action AnnounceTable(string name, long version);

    /// <summary>Commits <paramref name="writes"/> as one seq when the commit queue reaches it (<see cref="ChordStore.CommitAsync"/>).</summary>
    Task<long> CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context);

    /// <summary>Runs <paramref name="read"/> on the backend under the guard (<see cref="ChordStore.ReadAsync{T}"/>).</summary>
    Task<T> ReadAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context);
}

/// <summary>One commit of a series (<see cref="ChordStore.CommitSeriesAsync"/>): its own checks, which write nothing, and its writes. The
/// checks read through the commit's step, which sees what the series' earlier commits wrote (upstream <c>SeriesCommit</c>).</summary>
public interface ISeriesCommit
{
    /// <summary>Throws to refuse the commit: the series stops before it, the commits before it stand.</summary>
    Task CheckAsync(Step step);

    /// <summary>The commit's writes; none takes no seq.</summary>
    IReadOnlyList<IStagedWrite> Writes { get; }
}

/// <summary>What a series stored: how many of its commits, and why the next one was refused, if one was (upstream <c>SeriesResult</c>).</summary>
public sealed record SeriesResult(int Stored, Exception? Refused = null);

/// <summary>What the open host gives the store: its <c>storage_failed</c> listener and its batch, in which adapters publish (§1.3; upstream
/// <c>Attachment</c>).</summary>
internal sealed record Attachment(Action<Exception> Failed, Batch Batch);

/// <summary>Where a replayed batch leaves the store: its last commit's seq and allocator value, the tables it changed (upstream
/// <c>Replayed</c>).</summary>
internal sealed record Replayed(long LastSeq, long NextId, IReadOnlyDictionary<string, TableRecord> Tables);

/// <summary>The store behind <see cref="IStore"/>, with what Chord's layers use: commits, guarded reads, the host's failure listener
/// (upstream <c>ChordStore</c>).</summary>
public sealed class ChordStore : IStore, IChordStore
{
    private static readonly WriteOrder[] Order = [WriteOrder.Retire, WriteOrder.Create, WriteOrder.Write];

    private readonly ISqliteDatabase db;
    private readonly Lock gate = new();
    private long lastSeq;
    /// <summary>The allocator's value as the last commit persisted it (an export's manifest).</summary>
    private long committedNextId;
    private StoreState state = StoreState.Open;
    private Exception? failure;
    /// <summary>Set once a batch's recorded count did not reproduce (S17): no batch applies any more.</summary>
    private bool diverged;
    /// <summary>The commit queue's tail: each step starts after the one before it settled; never faults.</summary>
    private Task tail = Task.CompletedTask;
    /// <summary>Reads and maintenance writes underway, which close waits for.</summary>
    private int underway;
    /// <summary>Completes close's wait once no read is underway.</summary>
    private TaskCompletionSource? drained;
    /// <summary>The open host's listener for <c>storage_failed</c> (told once) and batch; one host per store (rule 7).</summary>
    private Attachment? attachment;
    private Task? closing;
    /// <summary>The versions migrations of tables announced (<see cref="AnnounceTable"/>), by SQL name, until their last commit.</summary>
    private readonly Dictionary<string, long> announced = new(StringComparer.Ordinal);
    /// <summary>The recorded tables, by SQL name; replaced only after a step's <c>COMMIT</c>.</summary>
    private volatile IReadOnlyDictionary<string, TableRecord> catalog;
    /// <summary>The ends of the exports holding the queue or waiting for it, which closing the store calls.</summary>
    private readonly HashSet<Action> held = new(ReferenceEqualityComparer.Instance);

    internal ChordStore(ISqliteDatabase db, MetaRow meta, IReadOnlyDictionary<string, TableRecord> catalog)
    {
        this.db = db;
        this.catalog = catalog;
        lastSeq = meta.LastSeq;
        committedNextId = meta.NextId;
        Ids = new IdAllocator(meta.NextId);
        Outbox = meta.Outbox == 1 ? new Outbox(this, meta.OutboxFloor) : null;
    }

    /// <summary>Where a store stands: open, stopped by a backend error, or closed.</summary>
    private enum StoreState { Open, Failed, Closed }

    /// <inheritdoc/>
    public long LastSeq => Interlocked.Read(ref lastSeq);

    /// <inheritdoc/>
    public Outbox? Outbox { get; }

    /// <summary>The allocator's value as the last commit persisted it.</summary>
    public long NextId => Interlocked.Read(ref committedNextId);

    /// <summary>The id allocator: <see cref="IdAllocator.Mint"/> takes the next id, <see cref="IdAllocator.Raise"/> makes room above an
    /// explicit one (D3).</summary>
    public IdAllocator Ids { get; }

    /// <summary>The claims of its tables and documents (slice 6b).</summary>
    public Claims Claims { get; } = new();

    /// <summary>The open host's batch, in which an adapter publishes its commits; null without a host.</summary>
    public Batch? Batch
    {
        get
        {
            lock (gate) return attachment?.Batch;
        }
    }

    /// <summary>Every recorded table, by SQL name, as of the last commit (upstream <c>tables</c>).</summary>
    internal IReadOnlyDictionary<string, TableRecord> Catalog => catalog;

    /// <summary>Whether the store has an outbox, which a replayed batch writes too.</summary>
    internal bool RecordsOutbox => Outbox is not null;

    /// <summary>A test seam standing for upstream's tests replacing <c>store.commit</c> once (test/next/support.ts's
    /// <c>holdNextCommit</c>): the next <see cref="CommitAsync"/> passes its writes through it, then clears it.</summary>
    internal Func<IReadOnlyList<IStagedWrite>, IReadOnlyList<IStagedWrite>>? NextCommit { get; set; }

    /// <summary>A test seam standing for upstream's tests wrapping <c>store.commit</c>'s fence (the documents' seal fence tests): while set,
    /// every <see cref="CommitAsync"/> commits under the fence it returns for the caller's.</summary>
    internal Func<CommitFence, CommitFence>? CommitFences { get; set; }

    /// <summary>A test seam standing for upstream's tests spying on or replacing <c>store.read</c> (<c>vi.spyOn(store, "read")</c>, the
    /// documents tests' <c>holdNextRead</c>): while set, every <see cref="ReadAsync{T}"/> call hands it the read's task, and completes
    /// once the task it returns did (with the read's outcome).</summary>
    internal Func<Task, Task>? Reads { get; set; }

    /// <summary>Takes the next id from the allocator (Durable's <c>mintId</c>); refused once the store stopped or closed.</summary>
    public long MintId()
    {
        AssertUsable();
        return Ids.Mint();
    }

    /// <summary>The recorded table <paramref name="name"/> (its SQL name), as of the last commit.</summary>
    internal TableRecord? Table(string name) => catalog.GetValueOrDefault(name);

    /// <summary>The version table <paramref name="name"/> is at or is being migrated to (<see cref="IChordStore.TableVersion"/>).</summary>
    internal long? TableVersion(string name)
    {
        long? recorded = catalog.TryGetValue(name, out var record) ? record.Version : null;
        long? announcedVersion;
        lock (gate) announcedVersion = announced.TryGetValue(name, out var version) ? version : null;
        return announcedVersion is null || (recorded is { } at && at >= announcedVersion) ? recorded : announcedVersion;
    }

    /// <summary>Announces a migration of table <paramref name="name"/> to <paramref name="version"/> until the returned withdrawal.</summary>
    internal Action AnnounceTable(string name, long version)
    {
        lock (gate) announced[name] = version;
        return () =>
        {
            lock (gate)
            {
                if (announced.TryGetValue(name, out var current) && current == version) announced.Remove(name);
            }
        };
    }

    TableRecord? IChordStore.Table(string name) => Table(name);

    long? IChordStore.TableVersion(string name) => TableVersion(name);

    Action IChordStore.AnnounceTable(string name, long version) => AnnounceTable(name, version);

    Task<long> IChordStore.CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context) => CommitAsync(writes, fence, context);

    /// <summary>Attaches the open host; a store attached to an open host is refused (rule 7).</summary>
    internal void Attach(Attachment attachment)
    {
        lock (gate)
        {
            if (this.attachment is not null) throw DataInternal.Invalid("args", "store: already attached to an open host");
            this.attachment = attachment;
        }
    }

    /// <summary>The host closed: the store may serve another one.</summary>
    internal void Detach()
    {
        lock (gate) attachment = null;
    }

    /// <summary>Takes the commit queue (an export): completes once every earlier commit settled, with the release that lets the later ones
    /// run (idempotent). Closing the store calls <paramref name="end"/> (from the call on, also while waiting, and at once for a store closed
    /// before the call took the queue), which must release.</summary>
    internal Task<Action> TakeAsync(Action end)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Release()
        {
            lock (gate) held.Remove(end);
            holding.TrySetResult();
        }
        async Task<Action> Taken(Task before)
        {
            await StoreInternal.Turn(before);
            // A store closed meanwhile (also before the export's first next()) holds nothing: the export ends.
            if (IsClosed)
            {
                Release();
                end();
            }
            return Release;
        }
        async Task Holding(Task<Action> taken)
        {
            await taken.ConfigureAwait(false);
            await holding.Task.ConfigureAwait(false);
        }
        lock (gate)
        {
            held.Add(end);
            var taken = Taken(tail);
            tail = StoreInternal.Settled(Holding(taken));
            return taken;
        }
    }

    /// <summary>Commits <paramref name="writes"/> as one seq when the commit queue reaches it: <c>fence.Admit()</c> at the step's entry, the
    /// writes in their order (retirements, creations, the rest), the outbox commit, <c>lastSeq</c> and the allocator's value,
    /// <c>COMMIT</c>, then <c>fence.Adopt</c> before the next step starts. Returns the seq. A refused admission fails <c>released</c>. A
    /// step the backend rolled back after the step's own code threw (a refusal, D4) fails with that error unchanged and the store goes on;
    /// any other failure (<c>BEGIN</c>, <c>COMMIT</c>, <c>ROLLBACK</c>, a throwing adopt) is an unknown outcome that stops the store
    /// (<c>storage_failed</c>). <paramref name="context"/> counts the step (no abort once queued).</summary>
    internal Task<long> CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context)
    {
        if (NextCommit is { } next)
        {
            NextCommit = null;
            writes = next(writes);
        }
        if (CommitFences is { } wrap) fence = wrap(fence);
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<long>(error);
        }
        return Enqueue(() => StepAsync(writes, fence, context));
    }

    /// <summary>Commits <paramref name="commits"/> in one backend transaction when the commit queue reaches them, one seq each, in order:
    /// each commit's check, which writes nothing, then its writes as <see cref="CommitAsync"/> writes them, with its own outbox commit and
    /// the metadata (a commit without writes takes no seq: it stands at the one before). A check that throws refuses its commit: the series
    /// stops there and the transaction commits the ones before it. A check whose backend statement failed, and a throw of a commit's
    /// writes, roll the whole series back and fail with that error, the store going on (D4); any other failure is an unknown outcome that
    /// stops the store, as for a commit. After <c>COMMIT</c>, <paramref name="receive"/>(index, seq, ids) is called per stored commit, in
    /// order, before the next step starts (ids: its created documents). Returns what it stored (upstream <c>commitSeries</c>).</summary>
    public Task<SeriesResult> CommitSeriesAsync(IReadOnlyList<ISeriesCommit> commits, Action<int, long, IReadOnlyList<long>> receive, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<SeriesResult>(error);
        }
        return Enqueue(() => SeriesAsync(commits, receive, context));
    }

    /// <summary>Runs <paramref name="read"/> on the backend under the guard (rule 11, Durable's GuardedStorage): refused once the store
    /// stopped or closed; a backend error fails the store unless <paramref name="context"/> was aborted (then <c>aborted</c>); a read
    /// settling after the store stopped fails <c>unavailable/storage_failed</c>. <paramref name="read"/> returns data and never throws for a
    /// request: an adapter checks a request from what it returned, after the guard (slice 6b §1.4).</summary>
    public Task<T> ReadAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(context);
        var task = GuardedAsync(() => read(db), context);
        return Reads is { } observe ? ObservedAsync(task, observe) : task;
    }

    /// <summary>A read <see cref="Reads"/> observes: done once the observer's task is, with the read's outcome.</summary>
    private static async Task<T> ObservedAsync<T>(Task<T> read, Func<Task, Task> observe)
    {
        await observe(read).ConfigureAwait(false);
        return await read.ConfigureAwait(false);
    }

    /// <summary><see cref="ReadAsync{T}"/> for several queries that must see one state (S4, a document from its record and revisions): one
    /// backend transaction, guarded as a read (upstream <c>readTransaction</c>).</summary>
    public Task<T> ReadTransactionAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(context);
        return GuardedAsync(() => db.TransactionAsync(read), context);
    }

    /// <summary>Runs <paramref name="write"/> on the backend outside the commit queue (the outbox's trim), in a backend transaction,
    /// guarded as a read without a context: any error stops the store (upstream <c>maintain</c>).</summary>
    internal async Task MaintainAsync(Func<ISqliteExecutor, Task> write)
    {
        AssertUsable();
        lock (gate) underway++;
        try
        {
            await db.TransactionAsync(async executor =>
            {
                await write(executor).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            AssertUsable();
            throw Fail(error);
        }
        finally
        {
            Settle();
        }
    }

    /// <inheritdoc/>
    public StoreExport Export(ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AssertUsable();
        return new StoreExport(this, context);
    }

    /// <inheritdoc/>
    public Task CloseAsync(ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Task closed;
        lock (gate) closed = closing ??= CloseCoreAsync();
        if (context.AbortSignal is not { } signal) return closed;
        return signal.IsCancellationRequested ? Task.FromCanceled(signal) : closed.WaitAsync(signal);
    }

    /// <summary>Closes the store as <see cref="CloseAsync"/> does, without a context.</summary>
    public async ValueTask DisposeAsync() => await CloseAsync(ChordContext.Background).ConfigureAwait(false);

    /// <summary>Throws <c>unavailable/storage_failed</c> once the store stopped, <c>released</c> once it closed.</summary>
    internal void AssertUsable()
    {
        lock (gate)
        {
            if (state == StoreState.Failed) throw StorageFailed(failure!);
            if (state == StoreState.Closed) throw new ChordError("released", "store: closed", "closed");
        }
    }

    /// <summary>Batch apply's turn (slice 6b §1.6, S5): refused (<c>invalid/args</c>) while an owner holds a claim (its memory would go
    /// stale) and once the store diverged; otherwise <paramref name="apply"/> runs in the commit queue, in one backend transaction, and
    /// returns the position its last commit leaves (lastSeq, the allocator's value) and the tables it changed, which memory takes after
    /// <c>COMMIT</c>. <paramref name="apply"/>'s own error rolls back and fails as a refusal (a divergence marks the store diverged); any
    /// other failure stops the store (upstream <c>replay</c>).</summary>
    internal Task ReplayAsync(Func<ISqliteExecutor, IReadOnlyDictionary<string, TableRecord>, Task<Replayed>> apply, Func<Exception, bool> isDivergence)
    {
        try
        {
            AssertReplayable();
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
        return Enqueue(async () =>
        {
            await ReplayTurnAsync(apply, isDivergence).ConfigureAwait(false);
            return true;
        });
    }

    private bool IsClosed
    {
        get
        {
            lock (gate) return state == StoreState.Closed;
        }
    }

    /// <summary>Runs <paramref name="run"/> after every queued step settled, never inline; the tail never faults.</summary>
    private Task<T> Enqueue<T>(Func<Task<T>> run)
    {
        lock (gate)
        {
            var step = StoreInternal.After(tail, run);
            tail = StoreInternal.Settled(step);
            return step;
        }
    }

    private async Task<T> GuardedAsync<T>(Func<Task<T>> run, ChordContext context)
    {
        AssertUsable();
        lock (gate) underway++;
        T result;
        try
        {
            // A read throwing before its first await is the backend's too: inside the try.
            result = await run().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            AssertUsable();
            if (context.AbortSignal is { IsCancellationRequested: true })
                throw new ChordError("aborted", "store: read aborted", cause: error);
            throw Fail(error);
        }
        finally
        {
            Settle();
        }
        AssertUsable();
        return result;
    }

    /// <summary>One read or maintenance write settled: close goes on once none is underway.</summary>
    private void Settle()
    {
        TaskCompletionSource? done = null;
        lock (gate)
        {
            if (--underway == 0) done = drained;
        }
        done?.TrySetResult();
    }

    private async Task CloseCoreAsync()
    {
        Action[] ends;
        lock (gate)
        {
            state = StoreState.Closed;
            ends = [.. held];
        }
        foreach (var end in ends) end();
        Task waited;
        lock (gate) waited = tail;
        await waited.ConfigureAwait(false);
        Task? reads = null;
        lock (gate)
        {
            if (underway > 0)
            {
                drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reads = drained.Task;
            }
        }
        if (reads is not null) await reads.ConfigureAwait(false);
        await db.CloseAsync().ConfigureAwait(false);
    }

    private async Task<long> StepAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context)
    {
        AssertUsable();
        if (!fence.Admit()) throw new ChordError("released", "store: the commit's attempt ended", "discarded");
        if (writes.Count == 0)
        {
            // An empty commit takes no seq; a throwing adopt stops the store as for any commit.
            try
            {
                fence.Rows?.Invoke(0);
                fence.Adopt(LastSeq, []);
            }
            catch (Exception error)
            {
                throw Fail(error);
            }
            return LastSeq;
        }
        var seq = LastSeq + 1;
        // Past the safe range seqs stop counting exactly (a later one would repeat): the store cannot number further.
        if (seq > StoreInternal.MaxSafeIntegerLong) throw Fail(new InvalidOperationException("store: no seq past Number.MAX_SAFE_INTEGER"));
        Exception? thrown = null;
        (Step Step, long NextId) done;
        try
        {
            done = await db.TransactionAsync(async executor =>
            {
                try
                {
                    var step = new Step(executor, seq, Ids, catalog);
                    var nextId = await WriteAsync(step, writes).ConfigureAwait(false);
                    await WriteMetaAsync(step, nextId).ConfigureAwait(false);
                    return (step, nextId);
                }
                catch (Exception error)
                {
                    thrown = error;
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The facade rolled back and fails with the step's own error: a refusal, the store goes on (D4).
            if (ReferenceEquals(error, thrown)) throw;
            throw Fail(error);
        }
        // COMMIT returned: the commit is durable, and memory follows.
        Interlocked.Exchange(ref lastSeq, seq);
        Interlocked.Exchange(ref committedNextId, done.NextId);
        AdoptTables(done.Step.Tables);
        context.Count("chord.store.commits");
        context.Count("chord.store.statements", done.Step.Statements);
        try
        {
            fence.Rows?.Invoke(done.Step.Rows);
            fence.Adopt(seq, done.Step.Created);
        }
        catch (Exception error)
        {
            throw Fail(error);
        }
        return seq;
    }

    /// <summary>A series inside one backend transaction: per commit a step at the next seq, its check (a throw refuses it, unless one of
    /// its statements failed), then its writes; then, after <c>COMMIT</c>, memory and the receipts as after a commit step.</summary>
    private async Task<SeriesResult> SeriesAsync(IReadOnlyList<ISeriesCommit> commits, Action<int, long, IReadOnlyList<long>> receive, ChordContext context)
    {
        AssertUsable();
        // Past the safe range seqs stop counting exactly (a later one would repeat): the store cannot number further. Only a commit with
        // writes takes a seq, as for a commit.
        var seqs = commits.Count(commit => commit.Writes.Count > 0);
        if (LastSeq + seqs > StoreInternal.MaxSafeIntegerLong) throw Fail(new InvalidOperationException("store: no seq past Number.MAX_SAFE_INTEGER"));
        Exception? thrown = null;
        Exception? refused = null;
        Series done;
        try
        {
            done = await db.TransactionAsync(async executor =>
            {
                try
                {
                    return await WriteSeriesAsync(executor, commits, error => refused = error).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    thrown = error;
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The facade rolled back and fails with the series' own error: the store goes on (D4).
            if (ReferenceEquals(error, thrown)) throw;
            throw Fail(error);
        }
        // COMMIT returned: the stored commits are durable, and memory follows.
        if (done.Last is { } last)
        {
            Interlocked.Exchange(ref lastSeq, last.Seq);
            Interlocked.Exchange(ref committedNextId, last.NextId);
            AdoptTables(last.Tables);
        }
        context.Count("chord.store.commits", done.Written);
        context.Count("chord.store.statements", done.Statements);
        try
        {
            for (var index = 0; index < done.Stored.Count; index++) receive(index, done.Stored[index].Seq, done.Stored[index].Created);
        }
        catch (Exception error)
        {
            throw Fail(error);
        }
        return new SeriesResult(done.Stored.Count, refused);
    }

    /// <summary>The commits of a series in its backend transaction, until one is refused (<paramref name="refuse"/>): each commit's check,
    /// then its writes; the metadata once, at the end.</summary>
    private async Task<Series> WriteSeriesAsync(ISqliteExecutor executor, IReadOnlyList<ISeriesCommit> commits, Action<Exception> refuse)
    {
        var calls = new WatchedExecutor(executor);
        var progress = new SeriesProgress(Ids.Value);
        foreach (var commit in commits)
        {
            var refusal = await CheckCommitAsync(commit, calls, progress).ConfigureAwait(false);
            if (refusal is not null)
            {
                refuse(refusal);
                break;
            }
            await WriteCommitAsync(commit, executor, progress).ConfigureAwait(false);
        }
        return await CloseSeriesAsync(progress).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="commit"/>'s check on a step at the series' next seq whose backend calls are watched; returns its
    /// refusal, if it refused. A check one of whose backend calls failed throws instead: a failure, not a refusal.</summary>
    private async Task<Exception?> CheckCommitAsync(ISeriesCommit commit, WatchedExecutor watched, SeriesProgress progress)
    {
        var step = new Step(watched, NextSeq(progress), Ids, progress.Changed ?? catalog);
        watched.Failed = false;
        try
        {
            await commit.CheckAsync(step).ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            if (watched.Failed) throw;
            return error;
        }
        finally
        {
            progress.Statements += step.Statements;
        }
    }

    /// <summary>Writes <paramref name="commit"/> on its own step at the series' next seq, unwatched, and keeps the table records it
    /// changed in a copy of the catalog, made only once one changed; a commit without writes takes no seq.</summary>
    private async Task WriteCommitAsync(ISeriesCommit commit, ISqliteExecutor executor, SeriesProgress progress)
    {
        var seq = NextSeq(progress);
        if (commit.Writes.Count == 0)
        {
            progress.Stored.Add((seq - 1, []));
            return;
        }
        var step = new Step(executor, seq, Ids, progress.Changed ?? catalog);
        progress.NextId = await WriteAsync(step, commit.Writes).ConfigureAwait(false);
        progress.Statements += step.Statements;
        if (step.Tables.Count > 0)
        {
            progress.Changed ??= new OrderedDictionary<string, TableRecord>(catalog, StringComparer.Ordinal);
            foreach (var (name, record) in step.Tables) progress.Changed[name] = record;
        }
        progress.Last = step;
        progress.Stored.Add((seq, step.Created));
    }

    /// <summary>The seq the series' next commit takes.</summary>
    private long NextSeq(SeriesProgress progress) => (progress.Last?.Seq ?? LastSeq) + 1;

    /// <summary>Ends the series' writes with the metadata, once, after its last commit that wrote; returns what memory takes.</summary>
    private async Task<Series> CloseSeriesAsync(SeriesProgress progress)
    {
        if (progress.Last is not { } last) return new Series(progress.Stored, null, 0, progress.Statements);
        await WriteMetaAsync(last, progress.NextId).ConfigureAwait(false);
        IReadOnlyDictionary<string, TableRecord> tables = progress.Changed ?? new OrderedDictionary<string, TableRecord>(StringComparer.Ordinal);
        return new Series(progress.Stored, (last.Seq, progress.NextId, tables), last.Seq - LastSeq, progress.Statements + 1);
    }

    /// <summary>The step's writes inside its backend transaction: each staged write in its phase, the catalog, the outbox commit. Returns the
    /// allocator's value, taken once, after the writes applied (mints meanwhile only raise it); the metadata is the caller's.</summary>
    private async Task<long> WriteAsync(Step step, IReadOnlyList<IStagedWrite> writes)
    {
        var recorded = Outbox is not null ? new List<StoreWrite>() : null;
        foreach (var order in Order)
        {
            foreach (var write in writes)
            {
                if (write.Order != order) continue;
                var applied = await write.ApplyAsync(step).ConfigureAwait(false);
                recorded?.AddRange(applied);
            }
        }
        await StoreInternal.WriteCatalogAsync(step).ConfigureAwait(false);
        var nextId = Ids.Value;
        if (recorded is not null)
        {
            // JSON.stringify of the array: each write's own text, comma-separated.
            var json = "[" + string.Join(",", recorded.Select(write => write.ToString())) + "]";
            await step.RunAsync($"INSERT INTO {StoreInternal.StoreTables.Outbox} (seq, format, next_id, writes) VALUES (?, ?, ?, ?)",
                step.Seq, StoreInternal.OutboxFormat, nextId, json).ConfigureAwait(false);
        }
        return nextId;
    }

    /// <summary>At its call and again at its turn: a claim or a divergence may come between the two.</summary>
    private void AssertReplayable()
    {
        AssertUsable();
        bool stale;
        lock (gate) stale = diverged;
        if (stale) throw DataInternal.Invalid("args", "store: diverged; a batch can no longer apply");
        if (Claims.Held) throw DataInternal.Invalid("args", "store: an owner holds claims; a batch would stale its memory");
    }

    private async Task ReplayTurnAsync(Func<ISqliteExecutor, IReadOnlyDictionary<string, TableRecord>, Task<Replayed>> apply, Func<Exception, bool> isDivergence)
    {
        AssertReplayable();
        // While it applies, no owner may claim (and read) what it is changing.
        var unlock = Claims.Lock();
        try
        {
            await ApplyReplayAsync(apply, isDivergence).ConfigureAwait(false);
        }
        finally
        {
            unlock();
        }
    }

    private async Task ApplyReplayAsync(Func<ISqliteExecutor, IReadOnlyDictionary<string, TableRecord>, Task<Replayed>> apply, Func<Exception, bool> isDivergence)
    {
        Exception? thrown = null;
        Replayed replayed;
        try
        {
            replayed = await db.TransactionAsync(async executor =>
            {
                try
                {
                    return await apply(executor, catalog).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    thrown = error;
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(error, thrown)) throw Fail(error);
            if (isDivergence(error))
            {
                lock (gate) diverged = true;
            }
            throw;
        }
        Interlocked.Exchange(ref lastSeq, replayed.LastSeq);
        Interlocked.Exchange(ref committedNextId, replayed.NextId);
        Ids.Raise(replayed.NextId - 1);
        AdoptTables(replayed.Tables);
    }

    /// <summary>The catalog with <paramref name="changed"/> taken in: a new map, so a step or a read holding the old one keeps it whole.</summary>
    private void AdoptTables(IReadOnlyDictionary<string, TableRecord> changed)
    {
        if (changed.Count == 0) return;
        var next = new OrderedDictionary<string, TableRecord>(catalog, StringComparer.Ordinal);
        foreach (var (name, record) in changed) next[name] = record;
        catalog = next;
    }

    /// <summary>Stops the store after <paramref name="error"/> (once; its host is told once); returns the error the caller fails with.</summary>
    private ChordError Fail(Exception error)
    {
        Action<Exception>? tell = null;
        lock (gate)
        {
            if (state == StoreState.Open)
            {
                state = StoreState.Failed;
                failure = error;
                tell = attachment?.Failed;
            }
        }
        tell?.Invoke(error);
        return StorageFailed(error);
    }

    /// <summary>Records <paramref name="step"/>'s seq as the store's last, and the allocator's value <paramref name="nextId"/>.</summary>
    private static Task WriteMetaAsync(Step step, long nextId) =>
        step.RunAsync($"UPDATE {StoreInternal.StoreTables.Meta} SET last_seq = ?, next_id = ? WHERE singleton = 1", step.Seq, nextId);

    /// <summary>The <c>unavailable/storage_failed</c> error of a stopped store, its backend error as the cause.</summary>
    private static ChordError StorageFailed(Exception error) => new("unavailable", "store: storage failed", "storage_failed", cause: error);

    /// <summary>A series as its transaction leaves it: each stored commit's seq and created documents; once a commit wrote, the last seq,
    /// the allocator's value after it and the table records the series changed; the commits that wrote and the statements run.</summary>
    private sealed record Series(List<(long Seq, IReadOnlyList<long> Created)> Stored,
        (long Seq, long NextId, IReadOnlyDictionary<string, TableRecord> Tables)? Last, long Written, long Statements);

    /// <summary>A series while its transaction runs: each stored commit's seq and created documents, its last step that wrote, the
    /// allocator's value after it, the table records it changed (a copy of the catalog, made once one changed) and the statements run.</summary>
    private sealed class SeriesProgress(long nextId)
    {
        internal List<(long Seq, IReadOnlyList<long> Created)> Stored { get; } = [];

        internal Step? Last { get; set; }

        internal long NextId { get; set; } = nextId;

        internal OrderedDictionary<string, TableRecord>? Changed { get; set; }

        internal long Statements { get; set; }
    }

    /// <summary>An executor that records whether one of its calls failed (upstream <c>watchCalls</c>): a check that throws then failed in
    /// the backend, not by its own decision.</summary>
    private sealed class WatchedExecutor(ISqliteExecutor executor) : ISqliteExecutor
    {
        internal bool Failed { get; set; }

        public Task ExecAsync(string sql) => Watched(async () =>
        {
            await executor.ExecAsync(sql).ConfigureAwait(false);
            return true;
        });

        public Task RunAsync(string sql, params object?[] parameters) => Watched(async () =>
        {
            await executor.RunAsync(sql, parameters).ConfigureAwait(false);
            return true;
        });

        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Watched(() => executor.RunCountedAsync(sql, parameters));

        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => Watched(() => executor.GetAsync(sql, parameters));

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) =>
            Watched(() => executor.AllAsync(sql, parameters));

        private async Task<T> Watched<T>(Func<Task<T>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (Exception)
            {
                Failed = true;
                throw;
            }
        }
    }
}

internal static partial class StoreInternal
{
    /// <summary>Turns the outbox on (from the current lastSeq) or off (its commits dropped) when <paramref name="outbox"/> asks for the
    /// other (upstream <c>switchOutbox</c>).</summary>
    internal static async Task<MetaRow> SwitchOutboxAsync(ISqliteDatabase db, MetaRow meta, bool? outbox)
    {
        if (outbox is not { } on || on == (meta.Outbox == 1)) return meta;
        var floor = on ? meta.LastSeq : 0;
        await db.TransactionAsync(async executor =>
        {
            await executor.RunAsync($"UPDATE {StoreTables.Meta} SET outbox = ?, outbox_floor = ? WHERE singleton = 1", on ? 1L : 0L, floor).ConfigureAwait(false);
            if (!on) await executor.RunAsync($"DELETE FROM {StoreTables.Outbox}").ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        return meta with { Outbox = on ? 1 : 0, OutboxFloor = floor };
    }

    /// <summary>Runs <paramref name="run"/> once <paramref name="before"/> settled, never inline: a promise's <c>then</c>.</summary>
    internal static async Task<T> After<T>(Task before, Func<Task<T>> run)
    {
        await Turn(before);
        return await run().ConfigureAwait(false);
    }

    /// <summary>Waits for <paramref name="before"/> (which never faults), or yields once when it already completed.</summary>
    internal static async Task Turn(Task before)
    {
        if (before.IsCompleted) await Task.Yield();
        else await before.ConfigureAwait(false);
    }

    /// <summary><paramref name="task"/> settled either way (its own caller sees its failure).</summary>
    internal static async Task Settled(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The operation's own caller sees its failure.
        }
    }
}
