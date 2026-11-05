// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-support.ts. ControlledStorage decorates any IStorage
// instead of subclassing MemoryStorage, so every session.* group runs on whatever backend its factory opens: SessionCases() below runs
// them on MemoryStorage, and P4 (JSONL) and P5 (SQLite) rerun them by calling SessionTests.All with their own prefix and factory.
//
// Shared adaptations, repeated where they matter:
// - flush() (one macrotask turn) is Flush(session): a no-op job on the Session line. Every watch delivery a commit schedules runs on the
//   line before that commit's task completes, and a watch started from outside the line delivers in its own line job, so the barrier
//   covers both. Where upstream flushes while the line is deliberately held, the check is a negative one and a
//   Task.Yield stands in.
// - Deferreds are TaskCompletionSource barriers. A release barrier continues its waiter inline (a resolved promise's then), so a released
//   watch listener has delivered everything buffered behind it when Resolve returns.
// - toBe is ReferenceEquals; for operation batches it is ImmutableArray equality, which compares the underlying array.
// - TypeScript type-level assertions are compile-time overload checks here (see SessionDefinitionsCases.cs).
// - A watch listener's destructured call, ({ value, ops, retired }, context), is Change(call): a Durable watch delivers value calls only
//   (Pi v1.5.0 sync, area watches).
// - ControlledStorage.Adopting is v1.5.0's adopting (Pi v1.5.0 sync, area observers): set while Storage runs a commit's receive. PiSharp
//   publishes a stored commit after its Storage call returned, so observers see it unset, as v1.5.0's do.
// - Pi v1.5.0 sync (area vocabulary, 5f312dac4): openTestSession attaches with ObserveCommits (a CommitObserver; was SubscribeCommits),
//   the internal follower helper is FollowCommits (upstream followCommits, was observeCommits), DocumentValues is upstream's
//   documentValues (the document changes of kind change), and onValue is Change(call) above.
// - Pi v1.5.0 sync (area group-commit, e5c6182ba): RefusingAt is upstream's refusingAt.
// - Pi v1.5.0 sync (area publication-caches, e5c6182ba): ControlledStorage records a call's batches after its hold, and only the first one
//   of a call it refuses, as v1.5.0's does; AfterAdopt is upstream's afterAdopt, run once after the next commit's first receipt.
//   LateResolvingStorage is upstream's, a subclass as there (ControlledStorage's CommitAsync, GetTaskAsync, ScanEntriesAsync and
//   ScanDocumentsAsync are virtual for it and for counting or pausing subclasses); its flush() is a short delay, as the thread pool has no
//   macrotask turn.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionCases() => SessionTests.All("session.", () => new MemoryStorage());

    /// <summary>The value call a Durable watch delivered, which upstream's listeners destructure (<c>({ value, ops }, context)</c>); a
    /// Durable watch sends no failed call.</summary>
    internal static WatchValueCall<T> Change<T>(WatchCall<T> call) =>
        call as WatchValueCall<T> ?? throw new InvalidOperationException("a Durable watch delivered a " + call.Kind + " call");
}

/// <summary>The session.* case groups and their support, parameterized by storage so later backends can rerun them.</summary>
internal static partial class SessionTests
{
    internal static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Every session.* case, with ids <c>{prefix}{file}.{case}</c>, on storages from <paramref name="storage"/>.</summary>
    internal static IEnumerable<(string, Func<Task>)> All(string prefix, Func<IStorage> storage) =>
    [
        .. Tables(prefix + "tables.", storage),
        .. Definitions(prefix + "definitions.", storage),
        .. DocumentCases(prefix + "documents.", storage),
        .. CheckpointsMigrations(prefix + "checkpoints-migrations.", storage),
        .. ForkCases(prefix + "forks.", storage),
        .. Watches(prefix + "watches.", storage),
        .. Line(prefix + "line.", storage),
    ];

    internal sealed record TestSession(ControlledStorage Storage, Session Session, List<CommitPublication> Publications);

    /// <summary>Session kernel plus its controlled storage and every committed publication (upstream <c>openTestSession</c>).</summary>
    internal static TestSession Open(Func<IStorage> factory)
    {
        var storage = new ControlledStorage(factory());
        // These tests pin the cache's identities and loads, so documents stay as long as the default keeps them.
        var session = new Session(storage, null, () => 600_000);
        var publications = new List<CommitPublication>();
        session.ObserveCommits(new CommitObserver((publication, _) =>
        {
            lock (publications) publications.Add(publication);
        }, _ => { }));
        return new TestSession(storage, session, publications);
    }

    /// <summary>Creates one conversation and returns its ID.</summary>
    internal static Task<ConversationId> CreateConversation(Session session) =>
        session.CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id, Context);

    /// <summary>Upstream <c>flush()</c>; see the file header.</summary>
    internal static Task Flush(Session session) => session.FlushLineAsync();

    /// <summary>
    /// Commits <paramref name="batches"/> through <paramref name="commit"/> up to the first one <paramref name="refuses"/> matches, which it
    /// refuses with <paramref name="error"/>: what a storage does whose own check refuses that batch (upstream v1.5.0 <c>refusingAt</c>, Pi
    /// v1.5.0 sync, area group-commit).
    /// </summary>
    internal static async Task<CommitResult> RefusingAt(
        Func<IReadOnlyList<IReadOnlyList<StorageWrite>>, Action<int, Seq>, DurableContext, Task<CommitResult>> commit,
        IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context,
        Func<IReadOnlyList<StorageWrite>, bool> refuses, Exception error)
    {
        var at = -1;
        for (var index = 0; index < batches.Count; index++)
        {
            if (!refuses(batches[index])) continue;
            at = index;
            break;
        }
        if (at < 0) return await commit(batches, receive, context);
        var result = await commit([.. batches.Take(at)], receive, context);
        return result.Stored < at ? result : new CommitResult(at, error);
    }

    internal static List<DocumentCommitChange> DocumentChanges(CommitPublication publication) =>
        [.. publication.Changes.OfType<DocumentCommitChange>()];

    /// <summary>The document changes of what an internal observer saw (Pi v1.5.0 sync, area deferred-commits: upstream's
    /// <c>documentChanges</c> takes anything with changes).</summary>
    internal static List<DocumentCommitChange> DocumentChanges(ObservedCommit publication) =>
        [.. publication.Changes.OfType<DocumentCommitChange>()];

    /// <summary>
    /// Follow every commit as memory adopts it, deferred ones before they are stored, as views and document watches do;
    /// <see cref="ISession.ObserveCommits"/> sees only what was stored (upstream <c>followCommits</c>, 5f312dac4; 6c2fd73b0's
    /// <c>observeCommits</c>). The internal follower is on <see cref="Session"/>, which every Session and Harness is.
    /// </summary>
    internal static IDisposable FollowCommits(ISession session, Action<ObservedCommit, DurableContext> listener) =>
        ((Session)session).FollowCommits(listener);

    /// <summary>The document changes of <paramref name="publication"/> that carry a value: creations and updates, no retirements
    /// (upstream <c>documentValues</c>, 5f312dac4).</summary>
    internal static List<DocumentValueChange> DocumentValues(CommitPublication publication) =>
        [.. publication.Changes.OfType<DocumentValueChange>()];

    /// <summary>The document changes that carry a value of what an internal follower saw.</summary>
    internal static List<DocumentValueChange> DocumentValues(ObservedCommit publication) =>
        [.. publication.Changes.OfType<DocumentValueChange>()];

    internal static List<DocumentCopyCommitChange> DocumentCopyChanges(CommitPublication publication) =>
        [.. publication.Changes.OfType<DocumentCopyCommitChange>()];

    internal static CommitPublication Last(List<CommitPublication> publications)
    {
        lock (publications) return publications[^1];
    }

    internal static int Count(List<CommitPublication> publications)
    {
        lock (publications) return publications.Count;
    }

    // ─── JSON and assertion helpers ─────────────────────────────────────────

    internal static JsonValue J(string json) => JsonValue.Parse(json);
    internal static JsonObjectValue O(string json) => (JsonObjectValue)JsonValue.Parse(json);
    internal static JsonObjectValue Obj(params (string Key, JsonValue Value)[] properties) =>
        JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, property.Value)));
    internal static double N(JsonValue? value) => ((JsonNumberValue)value!).Value;
    internal static string S(JsonValue? value) => ((JsonStringValue)value!).Value;
    internal static JsonValue At(JsonValue? value, params object[] path)
    {
        foreach (var segment in path) value = segment is string key ? ((JsonObjectValue)value!)[key] : ((JsonArrayValue)value!)[(int)segment];
        return value!;
    }

    /// <summary>Deep JSON equality (vitest's toEqual).</summary>
    internal static void SameJson(string expected, JsonValue? actual, string what) =>
        Check(actual is not null && JsonValue.DeepEquals(J(expected), actual), $"{what}: expected {expected}, actual {actual?.ToJson() ?? "undefined"}");

    internal static void Same(object? expected, object? actual, string what) => Check(ReferenceEquals(expected, actual), what + ": not the same reference");

    internal static void SameOps(ImmutableArray<Op> expected, ImmutableArray<Op> actual, string what) => Check(expected == actual, what + ": not the same batch");

    internal static void SameOps(ImmutableArray<Op> expected, IReadOnlyList<Op>? actual, string what) =>
        Check(actual is ImmutableArray<Op> batch && batch == expected, what + ": not the same batch");

    internal static string OpsJson(IEnumerable<Op> ops) => JsonValue.Array(ops.Select(op => (JsonValue)op.ToJson())).ToJson();

    internal static void Rejects(Exception error, string fragment, string what) =>
        Check(error.Message.Contains(fragment, StringComparison.Ordinal), $"{what}: expected a message containing \"{fragment}\", actual \"{error.Message}\"");

    /// <summary><c>await expect(promise).rejects.toThrow(fragment)</c>.</summary>
    internal static async Task<Exception> RejectsAsync(Func<Task> run, string fragment, string what)
    {
        try { await run(); }
        catch (Exception error)
        {
            Rejects(error, fragment, what);
            return error;
        }
        throw new InvalidOperationException($"{what}: expected a rejection containing \"{fragment}\"");
    }

    /// <summary><c>expect(() => ...).toThrow(fragment)</c>.</summary>
    internal static Exception Throws(Action run, string fragment, string what)
    {
        try { run(); }
        catch (Exception error)
        {
            Rejects(error, fragment, what);
            return error;
        }
        throw new InvalidOperationException($"{what}: expected a throw containing \"{fragment}\"");
    }

    internal static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");

    internal static List<StorageWrite> DocumentWrites(IEnumerable<StorageWrite> writes) =>
        [.. writes.Where(write => write is DocumentCreateWrite or DocumentChangeWrite or DocumentRetireWrite)];

    internal static T Single<T>(IEnumerable<object> values) => values.OfType<T>().Single();

    /// <summary>A deferred (upstream's <c>deferred()</c>). <paramref name="inline"/> continues waiters inside <see cref="Resolve"/>, as a
    /// resolved promise continues its then-callbacks before anything else runs; otherwise they continue on the thread pool.</summary>
    internal sealed class Deferred(bool inline = true)
    {
        private readonly TaskCompletionSource source = new(inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => source.Task;
        public void Resolve() => source.TrySetResult();
    }
}

/// <summary>A gate that holds calls until released and reports when the first held call arrives (upstream <c>Gate</c>).</summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Released inline: the held call continues inside Release, so its Session job progresses before Release returns.
    private readonly TaskCompletionSource released = new();
    public Task Entered => entered.Task;
    internal Task Released => released.Task;
    internal void Enter() => entered.TrySetResult();
    public void Release() => released.TrySetResult();
}

/// <summary>Storage with observable commits, held calls and injected commit failures over any backend (upstream
/// <c>ControlledStorage</c>).</summary>
internal class ControlledStorage(IStorage inner) : IStorage
{
    private readonly Lock gate = new();
    private Gate? commitGate;
    private Gate? findGate;
    private Exception? commitFailure;
    private int mintCount;
    private int documentReadCount;
    private bool adopting;

    /// <summary>Exact borrowed batches admitted by the Session.</summary>
    public List<IReadOnlyList<StorageWrite>> AdmittedCommits { get; } = [];
    /// <summary>Detached batches for value assertions.</summary>
    public List<ImmutableArray<StorageWrite>> Commits { get; } = [];
    public int MintCount => Volatile.Read(ref mintCount);
    /// <summary>Set while a receipt runs, Storage's synchronous step: code that sees it set runs inside the commit's <c>receive</c> (upstream
    /// v1.5.0 <c>adopting</c>, area observers).</summary>
    public bool Adopting => Volatile.Read(ref adopting);
    public int DocumentReadCount => Volatile.Read(ref documentReadCount);
    public IStorage Inner { get; } = inner;

    public Gate HoldCommits()
    {
        var held = new Gate();
        lock (gate) commitGate = held;
        return held;
    }

    public Gate HoldFindDocument()
    {
        var held = new Gate();
        lock (gate) findGate = held;
        return held;
    }

    /// <summary>Simulates a crash during the held commit: it never reaches storage, and later commits proceed.</summary>
    public void Crash()
    {
        lock (gate) commitGate = null;
    }

    public void FailNextCommit(Exception error)
    {
        lock (gate) commitFailure = error;
    }

    public ImmutableArray<StorageWrite> LastCommit
    {
        get { lock (gate) return Commits[^1]; }
    }

    public IReadOnlyList<StorageWrite> LastAdmitted
    {
        get { lock (gate) return AdmittedCommits[^1]; }
    }

    public int CommitCount
    {
        get { lock (gate) return Commits.Count; }
    }

    /// <summary>Runs once, in the next commit's receipts right after its first receipt returned (upstream <c>afterAdopt</c>).</summary>
    public Action? AfterAdopt
    {
        get { lock (gate) return afterAdopt; }
        set { lock (gate) afterAdopt = value; }
    }

    private Action? afterAdopt;

    /// <summary>Holds the call, then records its batches and refuses the first one if a failure is set (upstream v1.5.0: a refusal, nothing
    /// stored, and only that batch recorded; the Session fails with it), or records them all and commits them through <see cref="Inner"/>,
    /// <see cref="Adopting"/> set while each receipt runs and <see cref="AfterAdopt"/> run after the first one.</summary>
    public virtual async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive,
        DurableContext context)
    {
        Gate? held;
        lock (gate) held = commitGate;
        if (held is not null)
        {
            held.Enter();
            await held.Released;
            lock (gate) if (ReferenceEquals(commitGate, held)) commitGate = null;
        }
        Exception? failure;
        lock (gate)
        {
            failure = commitFailure;
            commitFailure = null;
            foreach (var writes in failure is null ? batches : batches.Take(1))
            {
                AdmittedCommits.Add(writes);
                Commits.Add([.. writes]);
            }
        }
        if (failure is not null) return new CommitResult(0, failure);
        return await Inner.CommitAsync(batches, (index, seq) =>
        {
            Volatile.Write(ref adopting, true);
            try { receive(index, seq); }
            finally { Volatile.Write(ref adopting, false); }
            Action? after;
            lock (gate)
            {
                after = afterAdopt;
                afterAdopt = null;
            }
            after?.Invoke();
        }, context);
    }

    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId>
    {
        Interlocked.Increment(ref mintCount);
        return Inner.MintIdAsync<TId>();
    }

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context)
    {
        Interlocked.Increment(ref documentReadCount);
        return Inner.GetDocumentAsync(id, at, context);
    }

    public async Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context)
    {
        Gate? held;
        lock (gate) held = findGate;
        if (held is not null)
        {
            held.Enter();
            await held.Released;
            lock (gate) if (ReferenceEquals(findGate, held)) findGate = null;
        }
        return await Inner.FindDocumentAsync(address, at, context);
    }

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => Inner.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => Inner.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        Inner.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        Inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public virtual Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanEntriesAsync(query, limit, cursor, context);
    public virtual Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => Inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        Inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public virtual Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanDocumentsAsync(query, limit, cursor, context);
    /// <summary>Keeps the decorated storage open when the Session closes this one, so <see cref="Reopen"/> can hand it to a new Session.</summary>
    public bool KeepOpenForReopen { get; set; }

    /// <summary>Reopens after close, as a fresh process would reopen the same database, keeping every committed record (upstream
    /// <c>reopen()</c>, which clears MemoryStorage's closed flag). Here the decorated storage stays open from the start
    /// (<see cref="KeepOpenForReopen"/>), on any backend.</summary>
    public ControlledStorage Reopen()
    {
        if (!KeepOpenForReopen) throw new InvalidOperationException("Set KeepOpenForReopen before the Session closes this storage");
        return this;
    }

    /// <summary>How many times the Session closed this storage, kept open or not (Pi v1.5.0 sync, area deferred-commits: upstream reads
    /// MemoryStorage's closed flag).</summary>
    public int CloseCount => Volatile.Read(ref closeCount);
    private int closeCount;

    public Task CloseAsync(DurableContext context)
    {
        Interlocked.Increment(ref closeCount);
        return KeepOpenForReopen ? Task.CompletedTask : Inner.CloseAsync(context);
    }
}

/// <summary>
/// Controlled storage whose commits resolve a macrotask after they adopted the batch, as a storage with work after durability does (JSONL
/// reclaims sidecars): code woken inside the receipt runs before the commit resolves (upstream <c>LateResolvingStorage</c>). <see cref="Gap"/>
/// runs in that work, a turn after adoption, and <see cref="RejectAfterAdopt"/> makes a commit it matches reject there, durable and adopted,
/// as a failed reclaim does; each fires once. Upstream's flush() (a macrotask) is a short delay here, as the thread pool has no turn to
/// wait for: what the receipt woke runs on other threads meanwhile.
/// </summary>
internal sealed class LateResolvingStorage(IStorage inner) : ControlledStorage(inner)
{
    private volatile bool committing;

    /// <summary>Set from a commit's start until it resolves.</summary>
    public bool Committing => committing;

    public Func<IReadOnlyList<StorageWrite>, bool>? Gap { get; set; }

    public Func<IReadOnlyList<StorageWrite>, bool>? RejectAfterAdopt { get; set; }

    public override async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive,
        DurableContext context)
    {
        committing = true;
        try
        {
            var result = await base.CommitAsync(batches, receive, context);
            var stored = batches.Take(result.Stored).ToList();
            await Task.Delay(10);
            if (Gap is { } gap && stored.Any(gap))
            {
                Gap = null;
                // What the gap started runs before the commit resolves.
                await Task.Delay(10);
            }
            if (RejectAfterAdopt is { } reject && stored.Any(reject))
            {
                RejectAfterAdopt = null;
                throw new InvalidOperationException("reclaim failed");
            }
            return result;
        }
        finally
        {
            committing = false;
        }
    }
}
