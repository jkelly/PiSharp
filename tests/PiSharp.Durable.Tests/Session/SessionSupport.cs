// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/session-support.ts. ControlledStorage decorates any IStorage
// instead of subclassing MemoryStorage, so every session.* group runs on whatever backend its factory opens: SessionCases() below runs
// them on MemoryStorage, and P4 (JSONL) and P5 (SQLite) rerun them by calling SessionTests.All with their own prefix and factory.
//
// Shared adaptations, repeated where they matter:
// - flush() (one macrotask turn) is Flush(session): a no-op job on the Session line. Every document-state and watch delivery a commit
//   schedules runs on the line before that commit's task completes, and a watch started from outside the line delivers in its own line
//   job, so the barrier covers both. Where upstream flushes while the line is deliberately held, the check is a negative one and a
//   Task.Yield stands in.
// - Deferreds are TaskCompletionSource barriers. A release barrier continues its waiter inline (a resolved promise's then), so a released
//   watch listener has delivered everything buffered behind it when Resolve returns.
// - toBe is ReferenceEquals; for operation batches it is ImmutableArray equality, which compares the underlying array.
// - TypeScript type-level assertions are compile-time overload checks here (see SessionDefinitionsCases.cs).
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionCases() => SessionTests.All("session.", () => new MemoryStorage());
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
        .. States(prefix + "states.", storage),
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
        session.SubscribeCommits((publication, _) =>
        {
            lock (publications) publications.Add(publication);
        });
        return new TestSession(storage, session, publications);
    }

    /// <summary>Creates one conversation and returns its ID.</summary>
    internal static Task<ConversationId> CreateConversation(Session session) =>
        session.CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id, Context);

    /// <summary>Upstream <c>flush()</c>; see the file header.</summary>
    internal static Task Flush(Session session) => session.FlushLineAsync();

    internal static List<DocumentCommitChange> DocumentChanges(CommitPublication publication) =>
        [.. publication.Changes.OfType<DocumentCommitChange>()];

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
internal sealed class ControlledStorage(IStorage inner) : IStorage
{
    private readonly Lock gate = new();
    private Gate? commitGate;
    private Gate? findGate;
    private Exception? commitFailure;
    private int mintCount;
    private int documentReadCount;

    /// <summary>Exact borrowed batches admitted by the Session.</summary>
    public List<IReadOnlyList<StorageWrite>> AdmittedCommits { get; } = [];
    /// <summary>Detached batches for value assertions.</summary>
    public List<ImmutableArray<StorageWrite>> Commits { get; } = [];
    public int MintCount => Volatile.Read(ref mintCount);
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

    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        Gate? held;
        lock (gate)
        {
            AdmittedCommits.Add(writes);
            Commits.Add([.. writes]);
            held = commitGate;
        }
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
        }
        if (failure is not null) throw failure;
        return await Inner.CommitAsync(writes, context);
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
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => Inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        Inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
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

    public Task CloseAsync(DurableContext context) => KeepOpenForReopen ? Task.CompletedTask : Inner.CloseAsync(context);
}
