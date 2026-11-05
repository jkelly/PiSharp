// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/transaction.ts
//
// Transactions (§9, R12, R13): drafts of an object's documents, prepared and adopted in one batch, with Chord's own staged changes (job
// runs, §15), fenced by the token they run under; they run on the object's line (Line.cs). Mappings:
// - Tx, what self.tx gives its callback, is ITx. A draft is the JsonObject the callback edits in place (Delta/Tracker.cs's Change state);
//   handles and tables are the public ISingletonHandle, IKeyedHandle and ITable, and a handle or table another object made is refused at
//   use, as upstream refuses whatever plugin code passes. Rows are column maps (Store/Rows.cs); a set is a value or a Func of the column
//   references, a where a Func of them, as TableChanges takes them.
// - Tx.start, a run of one of the object's jobs staged with the transaction's writes, imports chord/src/next/jobs (chord.next-jobs, phase
//   6), which is not ported: ITx has no Start yet. Staged (IStaged), what a job stages, and the transaction's Stage and Staged are ported,
//   so the jobs layer can join without changing this file.
// - Promises are Tasks. A warm draft settles at once: its task is complete when DraftAsync returns, so a continuation the callback did not
//   await but attached synchronously runs before the commit (rule 3); a draft of a handle the transaction cannot use is a faulted task, not
//   a throw. Commit returns null for an in-memory commit, which is synchronous (W12), and the store step's task otherwise. Upstream's race
//   of the store's commit with the cut's rejection is Task.WhenAny over the commit and a TaskCompletionSource the cut fails.
// - Errors: upstream's Errors are InvalidOperationExceptions with its messages; released/discarded and the store's refusals stay
//   ChordErrors.
// - Threads: a transaction runs on its caller's continuations, but its token's seal cuts it from whoever seals (the host), and the store's
//   admission runs on the commit queue: the state moves under a short lock, and a cut's effects (aborting the drafts, releasing the line,
//   failing the queued commit) run outside it.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Documents;

/// <summary>What <c>self.tx</c> gives its callback (upstream <c>Tx</c>). Inside it: only awaited tx calls, no other <c>self.tx</c>, no calls
/// to other objects.</summary>
public interface ITx
{
    /// <summary>The document's draft: the same one for the same document within one transaction.</summary>
    Task<JsonObject> DraftAsync(ISingletonHandle handle);

    /// <inheritdoc cref="DraftAsync(ISingletonHandle)"/>
    Task<JsonObject> DraftAsync(IKeyedHandle handle, string key);

    /// <summary>Retires the document when the transaction commits (its address reads <c>initial()</c> again); a no-op when it is absent.</summary>
    Task RetireAsync(ISingletonHandle handle);

    /// <inheritdoc cref="RetireAsync(ISingletonHandle)"/>
    Task RetireAsync(IKeyedHandle handle, string key);

    /// <summary>The row with <paramref name="key"/> as this transaction leaves it (its own put, update and delete overlaid; W10), null when
    /// absent.</summary>
    Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(ITable table, object? key);

    /// <summary>A page of committed rows, stable for the transaction (without its own writes).</summary>
    Task<Page> PageAsync(ITable table, Query query, long limit, PageOptions? options = null);

    /// <summary>Every committed row of <paramref name="query"/>, as <see cref="PageAsync"/>.</summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(ITable table, Query query);

    /// <summary>Writes <paramref name="row"/> when the transaction commits (an upsert: the whole row replaced).</summary>
    void Put(ITable table, object? row);

    /// <summary>Changes the row with <paramref name="key"/> when the transaction commits; an absent key changes nothing.</summary>
    void Update(ITable table, object? key, object? set);

    /// <inheritdoc cref="Update(ITable, object?, object?)"/>
    void Update(ITable table, object? key, Func<IReadOnlyDictionary<string, ColumnRef>, object?> set);

    /// <summary>Deletes the row with <paramref name="key"/> when the transaction commits.</summary>
    void Delete(ITable table, object? key);

    /// <summary>Changes every row matching <paramref name="where"/> when the transaction commits (one statement, its count recorded).</summary>
    void UpdateWhere(ITable table, Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where, object? set);

    /// <inheritdoc cref="UpdateWhere(ITable, Func{IReadOnlyDictionary{string, ColumnRef}, Condition}, object?)"/>
    void UpdateWhere(ITable table, Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where, Func<IReadOnlyDictionary<string, ColumnRef>, object?> set);

    /// <summary>Deletes every row matching <paramref name="where"/> when the transaction commits.</summary>
    void DeleteWhere(ITable table, Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where);
}

/// <summary>
/// Chord's own changes staged in a transaction (upstream <c>Staged</c>; a job's run records): adopted in its commit step with the documents,
/// then told how the transaction ended, once the owner's line is free.
/// </summary>
internal interface IStaged
{
    /// <summary>Adopts the changes, inside the commit's batch.</summary>
    void Adopt();

    /// <summary>The transaction committed: the actions after the commit (a run starts, waiters settle).</summary>
    void Committed();

    /// <summary>The transaction was discarded: nothing was adopted.</summary>
    void Discarded();
}

/// <summary>
/// One transaction of an object (upstream <c>Transaction</c>), under the seal token of whoever runs it (an attempt, a job run's invocation,
/// the object). Commit prepares every draft and adopts the changed documents and the staged changes in one batch; drafts are revoked once it
/// settles. Cut at its token's seal, it discards everything, releases the line and refuses every later use.
/// </summary>
internal sealed class Transaction : ITx, ILineHolder
{
    private readonly Documents documents;
    private readonly Action release;
    private readonly ChordContext context;
    private readonly Lock gate = new();
    private readonly Dictionary<IDocumentSlot, Dictionary<string, Entry>> entries = new(ReferenceEqualityComparer.Instance);
    // When its commit reaches the store (a run state's deferred change, D8).
    private readonly CommitPersistence persistence;
    // Chord's own staged changes, by their owner (a job); made by the first.
    private Dictionary<object, IStaged>? staged;
    // The store writes, in staging order, and the store they commit to; made by the first (the flag that sends the commit through the
    // store's step, W12).
    private (IChordStore Store, List<IStagedWrite> Staged)? writes;
    // The tables written, each with its writes and overlay.
    private Dictionary<TableHandle, TableChanges>? tables;
    // tx reads underway: a callback settling with one is refused (rule 3, as Durable's).
    private int pending;
    // tx reads started so far: a callback that completed without yielding after starting one awaited none (Documents.TxAsync, rule 3).
    private int started;
    // Set once Documents.TxAsync saw how the callback returned (Returned): the reads it started resume no sooner (TrackedYield).
    private readonly TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TxState state = TxState.Open;
    // Failed by Cut while the commit waits in the store's queue: its caller is told at once (rule 3).
    private TaskCompletionSource? queued;

    public Transaction(Documents documents, SealToken token, Action release, ChordContext context, CommitPersistence? persistence = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(context);
        this.documents = documents;
        Token = token;
        this.release = release;
        this.context = context;
        this.persistence = persistence ?? CommitPersistence.Immediate;
    }

    private enum TxState
    {
        Open,
        Committing,
        Stepping,
        Committed,
        Discarded,
        Cut,
    }

    public SealToken Token { get; }

    /// <summary>The object whose transaction this is.</summary>
    public Documents Documents => documents;

    /// <summary>
    /// The store writes of this transaction, which writes join (Chord's own documents: a job's run records): made by the first, which sends
    /// the commit through <paramref name="store"/>'s step.
    /// </summary>
    public List<IStagedWrite> StageStore(IChordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        writes ??= (store, []);
        return writes.Value.Staged;
    }

    public Task<JsonObject> DraftAsync(ISingletonHandle handle) => Draft(handle, "");

    public Task<JsonObject> DraftAsync(IKeyedHandle handle, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Draft(handle, key);
    }

    /// <summary>
    /// The draft of the document at <paramref name="key"/> of <paramref name="slot"/> (Chord's own slots, a job's run states). A cold
    /// persisted document is loaded first, inside the transaction (it holds the line); the draft counts as pending until it settles (rule 3).
    /// A warm draft settles at once.
    /// </summary>
    public Task<JsonObject> DraftSlotAsync(IDocumentSlot slot, string key)
    {
        Entry entry;
        try
        {
            entry = EntryOf(slot, key);
            var persisted = slot.Persisted;
            if (entry.Draft is not null || entry.Retire || persisted?.Cold(key) != true) return Task.FromResult(Begin(entry));
            return ColdDraftAsync(entry, persisted, key);
        }
        catch (Exception error)
        {
            return Task.FromException<JsonObject>(error);
        }
    }

    public Task RetireAsync(ISingletonHandle handle) => Retire(handle, "");

    public Task RetireAsync(IKeyedHandle handle, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Retire(handle, key);
    }

    /// <summary>Retires the document at <paramref name="key"/> of <paramref name="slot"/> when the transaction commits.</summary>
    public Entry RetireSlot(IDocumentSlot slot, string key)
    {
        var entry = EntryOf(slot, key);
        entry.Draft?.Change.Abort();
        entry.Draft = null;
        entry.Retire = true;
        return entry;
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(ITable table, object? key)
    {
        try
        {
            var handle = HandleOf(table);
            var changes = tables?.GetValueOrDefault(handle);
            return Track(changes is null ? handle.GetAsync(key, context) : changes.GetAsync(key, context));
        }
        catch (Exception error)
        {
            return Task.FromException<IReadOnlyDictionary<string, JsonNode?>?>(error);
        }
    }

    public Task<Page> PageAsync(ITable table, Query query, long limit, PageOptions? options = null)
    {
        try
        {
            // As in GetAsync; the query's table is checked against the handle's.
            return Track(HandleOf(table).PageAsync(query, limit, context, options));
        }
        catch (Exception error)
        {
            return Task.FromException<Page>(error);
        }
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(ITable table, Query query)
    {
        string? after = null;
        do
        {
            var page = await PageAsync(table, query, StoreInternal.RowsPage, after is null ? null : new PageOptions { After = after }).ConfigureAwait(false);
            foreach (var row in page.Items) yield return row;
            after = page.Next;
        } while (after is not null);
    }

    public void Put(ITable table, object? row) => ChangesOf(table).Put(row);

    public void Update(ITable table, object? key, object? set) => ChangesOf(table).Update(key, set);

    public void Update(ITable table, object? key, Func<IReadOnlyDictionary<string, ColumnRef>, object?> set) => ChangesOf(table).Update(key, set);

    public void Delete(ITable table, object? key) => ChangesOf(table).Delete(key);

    public void UpdateWhere(ITable table, Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where, object? set) =>
        ChangesOf(table).UpdateWhere(where, set);

    public void UpdateWhere(ITable table, Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where, Func<IReadOnlyDictionary<string, ColumnRef>, object?> set) =>
        ChangesOf(table).UpdateWhere(where, set);

    public void DeleteWhere(ITable table, Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where) => ChangesOf(table).DeleteWhere(where);

    /// <summary>The changes <paramref name="owner"/> staged in this transaction, made by <paramref name="make"/> on its first use.</summary>
    public TStaged Stage<TStaged>(object owner, Func<TStaged> make)
        where TStaged : IStaged
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(make);
        AssertOpen();
        staged ??= new Dictionary<object, IStaged>(ReferenceEqualityComparer.Instance);
        if (!staged.TryGetValue(owner, out var made))
        {
            made = make();
            staged[owner] = made;
        }
        // Each owner stages one kind of change, the one its make returns.
        return (TStaged)made;
    }

    /// <summary>The changes <paramref name="owner"/> staged in this transaction so far, if any.</summary>
    public TStaged? Staged<TStaged>(object owner)
        where TStaged : class, IStaged =>
        staged?.GetValueOrDefault(owner) as TStaged;

    /// <summary>
    /// Prepares every draft and adopts what changed, and the staged changes, in one batch; nothing when nothing changed. A document retired
    /// and drafted anew is removed, then adopted: its frame carries the retirement. Persisted documents stage their writes here (rule 8),
    /// which sends the commit through the store's step (the task returned; null for an in-memory commit, done already).
    /// </summary>
    public Task? Commit()
    {
        lock (gate)
        {
            AssertOpenLocked();
            if (pending > 0) throw new InvalidOperationException($"{documents.Label}: the callback settled before its pending tx operations");
            state = TxState.Committing;
        }
        context.AbortSignal?.ThrowIfCancellationRequested();
        var adoptions = new List<Adoption>();
        try
        {
            foreach (var entry in AllEntries())
            {
                var prepared = entry.Draft?.Change.Prepare();
                if (prepared is not null && prepared.Ops.Length == 0)
                {
                    prepared.Abort();
                    prepared = null;
                }
                var (slot, key) = (entry.Slot, entry.Key);
                var retires = entry.Retire && (slot.Current(key) is not null || entry.Found is not null);
                if (prepared is null && !retires) continue;
                var persisted = slot.Persisted;
                StoredChange? stored = null;
                if (persisted is not null)
                {
                    var staging = StageStore(persisted.Persistence.Store);
                    var was = persisted.Stored(key) ?? entry.Found;
                    stored = persisted.Stage(key, new DocumentChange(prepared, retires), was, staging, persistence);
                }
                adoptions.Add(new Adoption(entry, prepared, retires, stored));
            }
        }
        catch when (IsCut())
        {
            // Cut from another thread while preparing (its drafts aborted under the loop): the cut's error, not the overlay's.
            throw CutError();
        }
        if (writes is not { } store)
        {
            // Admitted as the store's step admits (a cut from another thread now is too late to stop it), then adopted at once (W12).
            lock (gate)
            {
                if (state != TxState.Committing) throw CutError();
                state = TxState.Stepping;
            }
            AdoptAll(adoptions);
            lock (gate) state = TxState.Committed;
            return null;
        }
        return StepAsync(store.Store, store.Staged, adoptions);
    }

    /// <summary>Aborts every draft and prepared change.</summary>
    public void Discard()
    {
        lock (gate)
        {
            if (state == TxState.Cut) return;
            state = TxState.Discarded;
        }
        AbortDrafts();
    }

    /// <summary>
    /// Its token was sealed while it held the line: every draft is aborted, the line released, and every later use, its commit included,
    /// throws <c>released</c>.
    /// </summary>
    public void Cut()
    {
        TaskCompletionSource? waiting;
        lock (gate)
        {
            // A commit admitted to the step runs to its end; one still queued is refused at admission.
            if (state is not (TxState.Open or TxState.Committing)) return;
            state = TxState.Cut;
            waiting = queued;
        }
        AbortDrafts();
        release();
        waiting?.TrySetException(CutError());
    }

    /// <summary>Tells the staged changes how the transaction ended; runs once the object's line is free.</summary>
    public void Settle()
    {
        if (staged is null) return;
        bool committed;
        lock (gate) committed = state == TxState.Committed;
        foreach (var each in staged.Values)
        {
            if (committed) each.Committed();
            else each.Discarded();
        }
    }

    /// <summary>Throws, failing <paramref name="slot"/>'s owner <c>broken</c>, when its definition cannot read the cold document
    /// <paramref name="found"/> (R15).</summary>
    private static void CheckFound(FoundDocument found, IPersistedSlot persisted, IDocumentSlot slot)
    {
        try
        {
            DocumentsInternal.CheckReadable(found.Stored.Version, found.Fingerprint, persisted.Definition.Current);
        }
        catch (Exception error)
        {
            var failure = DataInternal.AsChordError(error, slot.Owner.Label);
            slot.Owner.Fail(failure);
            throw failure;
        }
    }

    private Task<JsonObject> Draft(object handle, string key)
    {
        // Keyed keys follow the text rules (KeyedSlot.Key); a singleton's key is always "".
        IDeclaredSlot slot;
        string at;
        try
        {
            slot = SlotOf(handle);
            at = key.Length == 0 ? key : ((KeyedSlot)slot).Key(key);
        }
        catch (Exception error)
        {
            return Task.FromException<JsonObject>(error);
        }
        return DraftSlotAsync((IDocumentSlot)slot, at);
    }

    private async Task<JsonObject> ColdDraftAsync(Entry entry, IPersistedSlot persisted, string key)
    {
        lock (gate) { pending++; started++; }
        try
        {
            // As in Track: a load that settled already still counts until a later turn.
            var load = persisted.LoadAsync(key, context, Token);
            await TrackedYield(load).ConfigureAwait(false);
            await load.ConfigureAwait(false);
            AssertOpen();
            return Begin(entry);
        }
        finally
        {
            lock (gate) pending--;
        }
    }

    private async Task Retire(object handle, string key)
    {
        var slot = SlotOf(handle);
        var at = key.Length == 0 ? key : ((KeyedSlot)slot).Key(key);
        var documentSlot = (IDocumentSlot)slot;
        var entry = RetireSlot(documentSlot, at);
        var persisted = documentSlot.Persisted;
        // A cold persisted document is found, not loaded (one read; absent: a no-op, R13); a version or fingerprint the code cannot read
        // fails the owner as a load would (R15): a retirement of it is no exception.
        if (entry.Found is null && persisted?.Cold(at) == true)
        {
            var found = await Track(persisted.Persistence.FindAsync(at, context)).ConfigureAwait(false);
            // Cut meanwhile: its attempt was sealed, and failing the owner now would fail its successor.
            AssertOpen();
            if (found is not null) CheckFound(found, persisted, documentSlot);
            entry.Found = found?.Stored;
        }
    }

    /// <summary>
    /// Commits through the store's step (§3): admitted while neither cut nor sealed (else <c>released</c>, nothing written), then the
    /// documents and staged changes adopted inside the step. Once queued, the caller's abort no longer counts; a cut while queued rejects
    /// <c>released</c> at once (the queue drops the entry at its admission, writing nothing).
    /// </summary>
    private async Task StepAsync(IChordStore store, List<IStagedWrite> staging, List<Adoption> adoptions)
    {
        var fence = new CommitFence(() =>
        {
            lock (gate)
            {
                if (state != TxState.Committing || Token.Sealed) return false;
                state = TxState.Stepping;
                return true;
            }
        }, (_, _) => AdoptAll(adoptions));
        var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (state == TxState.Cut) cut.TrySetException(CutError());
            queued = cut;
        }
        try
        {
            var commit = store.CommitAsync(staging, fence, ChordContext.WithoutAbortSignal(context));
            // The race handles the commit's own later rejection too.
            var first = await Task.WhenAny(commit, cut.Task).ConfigureAwait(false);
            if (first != commit) _ = StoreInternal.Settled(commit);
            await first.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (IsCut()) throw CutError();
            throw Refused(error);
        }
        finally
        {
            lock (gate) queued = null;
        }
        lock (gate) state = TxState.Committed;
    }

    /// <summary>
    /// The error a commit the store rejected rejects with (D4): the store's own (<c>released</c>, <c>storage_failed</c>) as it is; a refused
    /// step classified, an <c>int</c> <c>CHECK</c> failing only this transaction (<c>invalid/args</c>), any other refusal failing the live
    /// object <c>broken</c> too (memory and disk disagree).
    /// </summary>
    private Exception Refused(Exception error)
    {
        var refusal = StoreInternal.RefusalOf(error, documents.Label);
        if (refusal is null) return error;
        if (refusal is Refusal.Broken) documents.Fail(refusal.Error);
        return refusal.Error;
    }

    /// <summary>Adopts the changed documents (persisted ones with what the commit stored of them) and the staged changes in one batch;
    /// nothing when nothing changed.</summary>
    private void AdoptAll(List<Adoption> adoptions)
    {
        var changes = staged;
        if (adoptions.Count == 0 && changes is null) return;
        documents.Batch.Commit(() =>
        {
            foreach (var (entry, prepared, retires, stored) in adoptions)
            {
                if (retires) entry.Slot.Retire(entry.Key);
                if (prepared is null) continue;
                entry.Slot.Adopt(entry.Key, entry.Draft!.Tracker, prepared, stored);
            }
            if (changes is not null)
                foreach (var each in changes.Values) each.Adopt();
        }, context);
    }

    private void AbortDrafts()
    {
        foreach (var entry in AllEntries()) entry.Draft?.Change.Abort();
    }

    /// <summary>Every entry so far, in drafting order by slot (a snapshot: a cut may read them from another thread).</summary>
    private List<Entry> AllEntries()
    {
        lock (gate) return [.. entries.Values.SelectMany(byKey => byKey.Values)];
    }

    private bool IsCut()
    {
        lock (gate) return state == TxState.Cut;
    }

    /// <summary>The slot behind a handle of this object, after the handle's version fence (R15).</summary>
    private static IDeclaredSlot SlotOf(object handle)
    {
        // Handles are Chord's own: self.state made them.
        (IDeclaredSlot slot, long version) = handle switch
        {
            Singleton singleton => ((IDeclaredSlot)singleton.Slot, singleton.Version),
            Keyed keyed => (keyed.Slot, keyed.Version),
            null => throw new ArgumentNullException(nameof(handle)),
            _ => throw new ArgumentException("A transaction takes the handles self.state made.", nameof(handle)),
        };
        slot.Definition.AssertVersion(version);
        return slot;
    }

    private Entry EntryOf(IDocumentSlot slot, string key)
    {
        ArgumentNullException.ThrowIfNull(slot);
        lock (gate)
        {
            AssertOpenLocked();
            if (!ReferenceEquals(slot.Owner, documents))
                throw new InvalidOperationException($"{documents.Label}: a transaction drafts only its own object's documents");
            if (!entries.TryGetValue(slot, out var byKey))
            {
                byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
                entries[slot] = byKey;
            }
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = new Entry(slot, key);
                byKey[key] = entry;
            }
            return entry;
        }
    }

    private void AssertOpen()
    {
        lock (gate) AssertOpenLocked();
    }

    private void AssertOpenLocked()
    {
        if (state == TxState.Open) return;
        if (state == TxState.Cut) throw CutError();
        throw new InvalidOperationException($"{documents.Label}: the transaction has settled");
    }

    private ChordError CutError() => new("released", $"{documents.Label}: the transaction was discarded at the seal", "discarded");

    /// <summary>The handle behind <paramref name="table"/>, one of this object's tables (another object's is refused).</summary>
    private TableHandle HandleOf(object? table)
    {
        AssertOpen();
        // Plugin code may pass any table: only this object's handles are taken.
        if (table is not TableHandle handle || !ReferenceEquals(handle.Owner.Scope, documents))
            throw new InvalidOperationException($"{documents.Label}: a transaction writes only its own object's tables");
        handle.AssertVersion();
        return handle;
    }

    /// <summary>The writes to <paramref name="table"/> in this transaction, made by the first.</summary>
    private TableChanges ChangesOf(object? table)
    {
        var handle = HandleOf(table);
        tables ??= new Dictionary<TableHandle, TableChanges>(ReferenceEqualityComparer.Instance);
        if (!tables.TryGetValue(handle, out var changes))
        {
            writes ??= (handle.Store, []);
            changes = new TableChanges(handle, writes.Value.Staged);
            tables[handle] = changes;
        }
        return changes;
    }

    /// <summary>The tx reads started so far (Documents.TxAsync).</summary>
    internal int Started
    {
        get { lock (gate) return started; }
    }

    /// <summary>
    /// Refuses a callback that completed without yielding after it started a tx read (rule 3). Each tracked read yields at least once (Track,
    /// ColdDraftAsync), and not before this check (TrackedYield), so the callback awaited none of them: they are pending as it settles, as a
    /// promise's are, however soon the thread pool settles them. Without this, a read that settled at once could finish its yield on another
    /// thread before Commit counts it; without the wait, a callback that awaited its read could finish on another thread before this check.
    /// </summary>
    internal void AssertNoReadsStartedSince(int before)
    {
        lock (gate)
            if (started != before) throw new InvalidOperationException($"{documents.Label}: the callback settled before its pending tx operations");
    }

    /// <summary>Documents.TxAsync saw how the callback returned: the reads it started may resume.</summary>
    internal void Returned() => returned.TrySetResult();

    /// <summary>
    /// The yield of a tracked read (rule 3). One the callback started before it first returned resumes only once Documents.TxAsync saw how
    /// it returned (<see cref="Returned"/>): a callback that awaits it is pending then, however soon the thread pool settles the read, as a
    /// promise's continuation waits for the synchronous code that awaits it. A later read that settled already still yields once, as
    /// awaiting a settled promise takes a turn.
    /// </summary>
    private Task TrackedYield(Task read) => !returned.Task.IsCompleted ? returned.Task : read.IsCompleted ? YieldOnce() : Task.CompletedTask;

    private static async Task YieldOnce() => await Task.Yield();

    /// <summary>Counts <paramref name="read"/> as pending until it settles (rule 3); a read that settled already still counts until a later
    /// turn, as awaiting a settled promise takes one.</summary>
    private async Task<T> Track<T>(Task<T> read)
    {
        lock (gate) { pending++; started++; }
        try
        {
            await TrackedYield(read).ConfigureAwait(false);
            return await read.ConfigureAwait(false);
        }
        finally
        {
            lock (gate) pending--;
        }
    }

    /// <summary>The entry's draft, begun on the current document (or a fresh <c>initial()</c>) by the first use.</summary>
    private JsonObject Begin(Entry entry)
    {
        if (entry.Draft is null)
        {
            var current = entry.Retire ? null : entry.Slot.Current(entry.Key);
            var tracker = current ?? Tracker.Track(entry.Slot.Initial(context));
            entry.Draft = new EntryDraft(tracker, tracker.BeginChange());
        }
        return entry.Draft.Change.State;
    }

    /// <summary>
    /// What one transaction does with one document (upstream <c>Entry</c>): a draft, a retirement, or both (retired, then drafted anew);
    /// <see cref="Found"/> is what is stored of a cold persisted document it retires without loading (R13).
    /// </summary>
    internal sealed class Entry(IDocumentSlot slot, string key)
    {
        public IDocumentSlot Slot { get; } = slot;

        public string Key { get; } = key;

        public EntryDraft? Draft { get; set; }

        public bool Retire { get; set; }

        public Stored? Found { get; set; }
    }

    /// <summary>An entry's draft: the tracker it was begun on and its change.</summary>
    internal sealed record EntryDraft(Tracker<JsonObject> Tracker, Change<JsonObject> Change);

    /// <summary>One document change a commit adopts (upstream <c>Adoption</c>): a removal, a draft's prepared change, or both; and for a
    /// persisted document what the commit stores of it.</summary>
    private sealed record Adoption(Entry Entry, Prepared<JsonObject>? Prepared, bool Retires, StoredChange? Stored);
}
