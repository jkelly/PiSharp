// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-group-commit.test.ts, new in Pi v1.5.0 (e5c6182ba, sync
// area group-commit). Group commit on the Session line (docs/group-commit.md): batching, gates, outcome fences, publication, failures per
// part, upkeep through the writer, backpressure, bounds, and a randomized interleaving against an oracle. Verbatim apart from the
// adaptations in SessionSupport.cs, and these, each for a step upstream's single thread orders and the thread pool does not:
// - BatchStorage decorates a MemoryStorage kept open for reopen, as ControlledStorage does, where upstream's extends ControlledStorage. It
//   records each call as it starts and holds the call that starts next (Hold), as upstream's does.
// - flush() (a macrotask) is the line tick (Session.LineTickAsync) where the case needs the jobs before it to have run their callbacks up to
//   admission. Where a job then waits at a gate, so a tick would wait with it, the case waits for the Session's count of gate hits
//   (Session.GateHits) to grow instead: the job is at its gate, which stays shut while the call in flight is held. Where a job waits
//   elsewhere (an upkeep job behind the call in flight, the line past 256 outcomes), a 20 ms delay stands in; those checks are negative,
//   so the delay can only miss a bug, never fail correct code.
// - settleOrder: .NET schedules each awaiting continuation on its own, so the order continuations run in is no order. SettleOrder watches
//   the tasks themselves, newest first, and fails if a later one is ever settled while an earlier one is not.
// - The hop between two parts of one call (a macrotask upstream, a Task.Yield here; see Writer.cs) orders nothing on the thread pool. The
//   cases about what a part's outcome resumes before the next part is published hold the hop (Session.PublicationHop) until that
//   continuation ran, for at most 5 s: a continuation that ran too late shows as the next part published first.
// - vi.spyOn on Transaction.adopt and adoptHeld is Session.AdoptFault and AdoptHeldFault, on storage.task BatchStorage.TaskFault; a
//   storage's publishing() is BatchStorage.Scope; session.report is a Session that records its reports.
// - Exceptions are never null in .NET: the undefined and null rejection and refusal values are one case each, with an exception of a type of
//   its own for upstream's value that is not an Error, compared by reference.
// - The randomized cases draw the storage latency from a second generator (seed + 1): storage calls run on the thread pool, so drawing it
//   from the plans' generator would make the plans depend on thread timing. Each seed's plans differ from upstream's, which interleave
//   the two draws on one thread.
// - "fails the Session for a throw in the read caches' write-through, and leaves the later parts unpublished" is area publication-caches':
//   PiSharp has no CachedStorage yet.
// - toEqual of a failed Session end compares its kind and its error's message (or reference).
using System.Diagnostics;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionGroupCommitCases() => GroupCommitTests.Cases("session.group-commit.");
}

internal static class GroupCommitTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>A latest, initial-fork conversation document: one key per conversation, and one a deferred commit may hold.</summary>
    private static readonly ConversationDocToken Note = Documents.DefineConversationDoc("gc.note", 1, DocumentFork.Initial, () => O("""{"n":0}"""),
        checkpointWhen: (_, _, _) => false);

    private static readonly SessionDocToken Shared = Documents.DefineSessionDoc("gc.shared", 1, () => O("""{"n":0}"""),
        checkpointWhen: (_, _, _) => false);

    private static readonly CommitOptions DeferredOptions = new(CommitPersistence.Deferred(double.PositiveInfinity));

    /// <summary>Upstream's rejection or refusal value that is not an Error (see the file header).</summary>
    private sealed class OddValue : Exception;

    /// <summary>
    /// Memory storage whose <c>CommitAsync</c> calls are recorded and can be held in flight, and whose batches can be refused (failed before
    /// adoption) or whose calls can reject after their receipts (upstream <c>BatchStorage</c>).
    /// </summary>
    private sealed class BatchStorage : IStorage
    {
        private readonly MemoryStorage inner = new();
        private readonly Lock gate = new();
        private readonly List<IReadOnlyList<IReadOnlyList<StorageWrite>>> calls = [];
        private Gate? held;
        private Func<IReadOnlyList<StorageWrite>, bool>? refuse;
        private int? rejectCall;

        /// <summary>Refuse the next batch this matches, with <see cref="Refusal"/>.</summary>
        public Func<IReadOnlyList<StorageWrite>, bool>? Refuse
        {
            get { lock (gate) return refuse; }
            set { lock (gate) refuse = value; }
        }

        public Exception Refusal { get; set; } = new InvalidOperationException("refused");

        /// <summary>Reject the call with this index after its batches are stored, with <see cref="Rejection"/>.</summary>
        public int? RejectCall
        {
            get { lock (gate) return rejectCall; }
            set { lock (gate) rejectCall = value; }
        }

        public Exception Rejection { get; set; } = new InvalidOperationException("connection lost");

        /// <summary>Delay each call by this many milliseconds.</summary>
        public Func<int>? Latency { get; set; }

        /// <summary>A fault <see cref="GetTaskAsync"/> rejects with, or null (upstream's spy on <c>storage.task</c>).</summary>
        public Func<Exception?>? TaskFault { get; set; }

        /// <summary>The storage's publication scope (upstream <c>publishing</c>); null runs each publication at once.</summary>
        public Action<Action, DurableContext>? Scope { get; set; }

        public List<IReadOnlyList<IReadOnlyList<StorageWrite>>> Calls
        {
            get { lock (gate) return [.. calls]; }
        }

        public int CallCount
        {
            get { lock (gate) return calls.Count; }
        }

        public IReadOnlyList<IReadOnlyList<StorageWrite>> LastCall
        {
            get { lock (gate) return calls[^1]; }
        }

        /// <summary>Hold the next call in flight until released.</summary>
        public Gate Hold()
        {
            var next = new Gate();
            lock (gate) held = next;
            return next;
        }

        public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
        {
            int call;
            Gate? hold;
            lock (gate)
            {
                call = calls.Count;
                calls.Add(batches);
                hold = held;
                held = null;
            }
            if (hold is not null)
            {
                hold.Enter();
                await hold.Released;
            }
            if (Latency?.Invoke() is > 0 and var delay) await Task.Delay(delay);
            bool Refuses(IReadOnlyList<StorageWrite> writes)
            {
                lock (gate)
                {
                    if (refuse?.Invoke(writes) != true) return false;
                    refuse = null;
                    return true;
                }
            }
            var result = await RefusingAt(inner.CommitAsync, batches, receive, context, Refuses, Refusal);
            if (RejectCall == call) throw Rejection;
            return result;
        }

        public void Publishing(Action publish, DurableContext context)
        {
            if (Scope is { } scope) scope(publish, context);
            else ((IStorage)inner).Publishing(publish, context);
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

        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) =>
            TaskFault?.Invoke() is { } fault ? Task.FromException<TaskRecord?>(fault) : inner.GetTaskAsync(id, context);

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

        /// <summary>Kept open, so <see cref="Reopen"/> hands the same records to a new Session (upstream <c>reopen()</c>).</summary>
        public Task CloseAsync(DurableContext context) => Task.CompletedTask;

        public BatchStorage Reopen() => this;
    }

    /// <summary>A Session that records its reports (upstream's <c>session.report</c>).</summary>
    private sealed class GroupSession(IStorage storage) : Session(storage, null, () => 600_000)
    {
        public SyncList<Exception> Reports { get; } = new();

        internal override void Report(Exception error, ReportInfo info, DurableContext? context = null) => Reports.Add(error);
    }

    private sealed record Opened(BatchStorage Storage, GroupSession Session, ConversationId[] Ids, SyncList<ObservedCommit> Published);

    private static async Task<Opened> OpenAsync(int conversations)
    {
        var storage = new BatchStorage();
        var session = new GroupSession(storage);
        var ids = await session.CommitAsync(async tx =>
        {
            var created = new ConversationId[conversations];
            for (var n = 0; n < conversations; n++) created[n] = (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id;
            return created;
        }, Context);
        var published = new SyncList<ObservedCommit>();
        FollowCommits(session, (publication, _) => published.Add(publication));
        return new Opened(storage, session, ids, published);
    }

    /// <summary>Increments a note; returns the value it read.</summary>
    private static int Increment(DraftObject note)
    {
        var n = (int)N(note.Get("n"));
        note.Set("n", n + 1);
        return n;
    }

    /// <summary>Increment conversation <paramref name="id"/>'s note; resolves with the value it read.</summary>
    private static Task<int> Bump(Session session, ConversationId id, CommitOptions? options = null) =>
        session.CommitAsync(async tx => Increment(await tx.DocAsync(Note, id)), Context, options);

    /// <summary>The note values a reopened Session over <paramref name="storage"/> reads, per conversation.</summary>
    private static async Task<string> NotesAsync(BatchStorage storage, params ConversationId[] ids)
    {
        var reopened = new Session(storage.Reopen());
        var values = new List<int>();
        foreach (var id in ids) values.Add(await reopened.SnapshotAsync(Note, id, Context) is { } value ? (int)N(value["n"]) : 0);
        await reopened.CloseAsync(Context);
        return string.Join(",", values);
    }

    /// <summary>The kinds of the entries <paramref name="published"/> holds, in order.</summary>
    private static string EntryKinds(SyncList<ObservedCommit> published) =>
        string.Join(",", published.Snapshot().Where(publication => !publication.IsMemory)
            .SelectMany(publication => publication.Changes.OfType<EntryWrite>().Select(write => write.Value.Kind)));

    private static IEnumerable<StorageWrite> Flat(IEnumerable<IReadOnlyList<StorageWrite>> call) => call.SelectMany(batch => batch);

    private static bool IsEntry(IReadOnlyList<StorageWrite> writes, string kind) =>
        writes.Any(write => write is EntryWrite entry && entry.Value.Kind == kind);

    private static EntryDraft Entry(string kind) => new() { Kind = kind };

    /// <summary>Waits until a job reached a gate after <paramref name="hits"/> (see the file header).</summary>
    private static Task AtGate(Session session, int hits) => HarnessTests.Eventually(() => session.GateHits > hits);

    /// <summary>A negative check's wait, where a job waits elsewhere than at a gate (see the file header).</summary>
    private static Task Pause() => Task.Delay(20);

    /// <summary>The outcome of <paramref name="task"/>: null when it succeeded, else its error.</summary>
    private static async Task<Exception?> Outcome(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void SessionFailed(Exception? error, string what, Exception? cause = null)
    {
        if (error is not SessionFailedException failed) throw new InvalidOperationException($"{what}: expected SessionFailed, got {error?.GetType().Name ?? "success"}");
        if (cause is not null) Same(cause, failed.Cause, what + " cause");
    }

    private static void Failed(SessionEnd end, string message, string what)
    {
        Equal(SessionEndKind.Failed, end.Kind, what + " kind");
        Equal(message, end.Error?.Message, what + " error");
    }

    private static void FailedWith(SessionEnd end, Exception error, string what)
    {
        Equal(SessionEndKind.Failed, end.Kind, what + " kind");
        Same(error, end.Error, what + " error");
    }

    private static void Empty((int Outstanding, int Unpublished, int Reads) sizes, string what) =>
        Equal((0, 0, 0), sizes, what);

    /// <summary>
    /// Upstream <c>settleOrder</c> (see the file header): watches <paramref name="tasks"/> newest first until all settle, and fails if a later
    /// one is ever settled while an earlier one is not; their names in order, a rejection as <c>name!</c>.
    /// </summary>
    private sealed class SettleOrder
    {
        private readonly (string Name, Task Task)[] tasks;
        private readonly Task watching;
        private string? outOfOrder;

        public SettleOrder(params (string Name, Task Task)[] tasks)
        {
            this.tasks = tasks;
            foreach (var (_, task) in tasks)
                _ = task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            watching = Task.Factory.StartNew(Watch, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void Watch()
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                var settled = new bool[tasks.Length];
                for (var index = tasks.Length - 1; index >= 0; index--) settled[index] = tasks[index].Task.IsCompleted;
                var gap = -1;
                for (var index = 0; index < settled.Length; index++)
                {
                    if (!settled[index]) { if (gap < 0) gap = index; }
                    else if (gap >= 0) outOfOrder ??= $"{tasks[index].Name} settled before {tasks[gap].Name}";
                }
                if (gap < 0) return;
                Thread.Yield();
            }
        }

        private static string Name((string Name, Task Task) task) => task.Task.IsFaulted || task.Task.IsCanceled ? task.Name + "!" : task.Name;

        /// <summary>What settled so far.</summary>
        public List<string> Settled() => [.. tasks.Where(task => task.Task.IsCompleted).Select(Name)];

        /// <summary>Once every task settled: their names in line order, or a failure if they settled out of it.</summary>
        public async Task<string> AllAsync()
        {
            await watching;
            if (outOfOrder is { } reason) throw new InvalidOperationException("outcomes out of line order: " + reason);
            if (tasks.Any(task => !task.Task.IsCompleted)) throw new InvalidOperationException("outcomes did not all settle");
            return string.Join(",", tasks.Select(Name));
        }
    }

    /// <summary>A small deterministic generator, so a failing seed reproduces.</summary>
    private static Func<double> Random(long seed)
    {
        var state = seed;
        return () =>
        {
            state = (state * 1_103_515_245 + 12_345) % 2_147_483_648;
            return state / 2_147_483_648.0;
        };
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ─── batching ───────────────────────────────────────────────────────

        yield return Case(prefix + "stores-the-commits-admitted-while-a-call-is-in-flight-in-one-call-each-with-its-seq-published-in-order", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var ran = new SyncList<string>();
            var second = session.CommitAsync(async tx =>
            {
                Increment(await tx.DocAsync(Note, b));
                ran.Add("b");
            }, Context);
            var third = session.CommitAsync(async tx =>
            {
                Increment(await tx.DocAsync(Note, c));
                ran.Add("c");
            }, Context);
            // Their callbacks run while the first commit is in flight: they touch nothing it writes.
            await HarnessTests.Eventually(() => ran.Count == 2);
            Equal(0, published.Count, "published while in flight");
            var order = new SettleOrder(("a", first), ("b", second), ("c", third));
            held.Release();
            await Task.WhenAll(first, second, third);
            Equal("a,b,c", await order.AllAsync(), "order");
            Equal("1,2", string.Join(",", storage.Calls.TakeLast(2).Select(call => call.Count)), "batches per call");
            var seqs = published.Snapshot().Select(publication => publication.Seq?.Value ?? 0).ToList();
            Equal(string.Join(",", seqs.Order()), string.Join(",", seqs), "seqs in order");
            Equal(3, seqs.Distinct().Count(), "distinct seqs");
            await session.CloseAsync(Context);
        });

        // ─── gates ──────────────────────────────────────────────────────────

        yield return Case(prefix + "makes-a-read-of-an-entry-id-leaked-by-an-unpublished-commit-wait-for-its-publication", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(1);
            var held = storage.Hold();
            EntryId? leaked = null;
            var appending = session.CommitAsync(async tx => { leaked = (await tx.AppendEntryAsync(ids[0], Entry("note"))).Id; }, Context);
            await held.Entered;
            var hits = session.GateHits;
            var reading = session.CommitAsync(tx => tx.GetEntryAsync(leaked!.Value), Context);
            await AtGate(session, hits);
            Check(!reading.IsCompleted, "the read waits for the publication");
            held.Release();
            await appending;
            Equal(leaked, (await reading)?.Id, "read");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-job-wait-for-an-unpublished-creation-at-a-document-address-instead-of-creating-it-again", async () =>
        {
            var (storage, session, _, _) = await OpenAsync(0);
            var held = storage.Hold();
            var first = session.CommitAsync(async tx => { Increment(await tx.DocAsync(Shared)); }, Context);
            await held.Entered;
            var hits = session.GateHits;
            var second = session.CommitAsync(async tx =>
            {
                var shared = await tx.DocAsync(Shared);
                return Increment(shared) + 1;
            }, Context);
            await AtGate(session, hits);
            Check(!second.IsCompleted, "the second waits for the creation");
            held.Release();
            await first;
            Equal(2, await second, "second");
            var creations = storage.Calls.SelectMany(Flat).OfType<DocumentCreateWrite>().Count(write => write.Record.Kind == "gc.shared");
            Equal(1, creations, "creations");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-write-to-a-conversation-an-unpublished-commit-creates-wait-for-it", async () =>
        {
            var (storage, session, _, _) = await OpenAsync(0);
            var held = storage.Hold();
            ConversationId? leaked = null;
            var creating = session.CommitAsync(async tx => { leaked = (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id; }, Context);
            await held.Entered;
            var hits = session.GateHits;
            var appending = session.CommitAsync(tx => tx.AppendEntryAsync(leaked!.Value, Entry("note")), Context);
            await AtGate(session, hits);
            Check(!appending.IsCompleted, "the append waits for the creation");
            held.Release();
            await creating;
            Equal(leaked, (await appending).ConversationId, "conversation");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-each-read-of-the-entries-of-a-conversation-an-unpublished-commit-creates-wait-for-its-creation", async () =>
        {
            // The Session's own helpers (newest entry, head marker), a transaction's scan, and the line's reads, each alone behind the
            // creation, of a conversation Storage does not have yet. Each describes its value: none, or a list's length.
            var reads = new (string Name, Func<Session, ConversationId, Task<string>> Read)[]
            {
                ("latestEntryId", async (session, id) => await session.CommitAsync(tx => tx.LatestEntryIdAsync(id), Context) is null ? "none" : "some"),
                ("latestHeadMarker", async (session, id) => await session.CommitAsync(tx => tx.LatestHeadMarkerAsync(id), Context) is null ? "none" : "some"),
                ("tx.scanEntries", async (session, id) =>
                    $"[{(await session.CommitAsync(async tx => (await tx.ScanEntriesAsync(new EntryQuery(id), 1)).Items, Context)).Length}]"),
                ("line scanEntries", async (session, id) =>
                    $"[{(await session.ReadOnLineAsync(async line => (await line.ScanEntriesAsync(new EntryQuery(id), 1, null, Context)).Items)).Length}]"),
                ("line findLatestHeadMarker", async (session, id) =>
                    await session.ReadOnLineAsync(line => line.FindLatestHeadMarkerAsync(id, null, Context)) is null ? "none" : "some"),
            };
            foreach (var (name, read) in reads)
            {
                var (storage, session, _, _) = await OpenAsync(0);
                var held = storage.Hold();
                ConversationId? leaked = null;
                var creating = session.CommitAsync(async tx => { leaked = (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id; }, Context);
                await held.Entered;
                var hits = session.GateHits;
                var reading = read(session, leaked!.Value);
                await AtGate(session, hits);
                held.Release();
                await creating;
                string result;
                try { result = await reading; }
                catch (Exception error) { result = "error: " + error.Message; }
                Equal(name.Contains("scan", StringComparison.Ordinal) ? "[0]" : "none", result, name);
                await session.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "makes-a-scan-that-cannot-be-keyed-wait-for-every-unpublished-commit", async () =>
        {
            var (storage, session, _, _) = await OpenAsync(0);
            var held = storage.Hold();
            var creating = session.CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id, Context);
            await held.Entered;
            var hits = session.GateHits;
            var scanning = session.CommitAsync(async tx =>
                (await tx.ScanConversationsAsync(new ConversationQuery(), 10)).Items.Select(conversation => conversation.Id).ToList(), Context);
            await AtGate(session, hits);
            Check(!scanning.IsCompleted, "the scan waits");
            held.Release();
            Check((await scanning).Contains(await creating), "the scan sees the creation");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-line-read-after-an-un-awaited-commit-see-it", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(1);
            var held = storage.Hold();
            EntryId? leaked = null;
            _ = session.CommitAsync(async tx => { leaked = (await tx.AppendEntryAsync(ids[0], Entry("note"))).Id; }, Context);
            await held.Entered;
            var hits = session.GateHits;
            var reading = session.ReadOnLineAsync(line => line.GetEntryAsync(leaked!.Value, Context));
            await AtGate(session, hits);
            held.Release();
            Equal(leaked, (await reading)?.Entry.Id, "read");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-reader-of-a-document-whose-held-changes-an-unpublished-commit-stores-wait-and-store-them-once", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(1);
            var id = ids[0];
            await Bump(session, id);
            await Bump(session, id, DeferredOptions);
            // The first commit only reads the held document: storing its held changes is its write of it.
            var held = storage.Hold();
            var storing = session.CommitAsync(async tx =>
            {
                await tx.DocAsync(Note, id);
                await tx.AppendEntryAsync(id, Entry("a"));
            }, Context);
            await held.Entered;
            var read = false;
            var hits = session.GateHits;
            var reading = session.CommitAsync(async tx =>
            {
                await tx.DocAsync(Note, id);
                Volatile.Write(ref read, true);
                await tx.AppendEntryAsync(id, Entry("b"));
            }, Context);
            await AtGate(session, hits);
            Check(!Volatile.Read(ref read), "the reader waits");
            held.Release();
            await Task.WhenAll(storing, reading);
            var changes = storage.Calls.TakeLast(2).Select(call => Flat(call).OfType<DocumentChangeWrite>().Count());
            Equal("1,0", string.Join(",", changes), "document changes per call");
            var record = (await storage.FindDocumentAsync(new DocumentAddress("gc.note", DocumentScope.Conversation(id)), DocumentPoint.Current, Context))!;
            Equal(1, (await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, Context))?.DeltasSinceBase, "deltas since base");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-a-reader-of-a-document-held-by-a-deferred-commit-behind-a-call-wait-for-it-and-store-it", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            await Bump(session, a);
            var held = storage.Hold();
            var first = Bump(session, b);
            await held.Entered;
            var deferredCommit = session.CommitAsync(async tx => (await tx.DocAsync(Note, a)).Set("n", 5), Context, DeferredOptions);
            var hits = session.GateHits;
            var reader = Bump(session, a);
            await AtGate(session, hits);
            // Nothing is published while the first commit is in flight, the deferred one neither.
            Equal(1, published.Count, "published");
            held.Release();
            await Task.WhenAll(first, deferredCommit);
            Equal(5, await reader, "reader");
            // The reader's own batch stored what the deferred commit held.
            Equal(1, Flat(storage.LastCall).OfType<DocumentChangeWrite>().Count(), "document changes");
            await session.CloseAsync(Context);
            Equal("6,1", await NotesAsync(storage, a, b), "notes");
        });

        // ─── outcomes ───────────────────────────────────────────────────────

        yield return Case(prefix + "delivers-a-read-a-commit-without-writes-a-callback-error-and-a-deferred-commit-after-an-earlier-commit", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var order = new SettleOrder(
                ("first", first),
                ("read", session.ReadOnLineAsync(() => Task.FromResult("read"))),
                ("none", session.CommitAsync(_ => Task.FromResult("none"), Context)),
                ("error", session.CommitAsync(new Func<ITx, Task>(_ => throw new InvalidOperationException("own")), Context)),
                ("deferred", Bump(session, b, DeferredOptions)));
            await session.LineTickAsync();
            Equal(0, order.Settled().Count, "settled while in flight");
            held.Release();
            // A callback's own error affects only its job.
            Equal("first,read,none,error!,deferred", await order.AllAsync(), "order");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "makes-every-outcome-after-a-fatal-failure-sessionfailed-and-keeps-the-earlier-ones", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var stored = Bump(session, b);
            var refused = session.CommitAsync(tx => tx.AppendEntryAsync(c, Entry("refuse")), Context);
            var after = Bump(session, d);
            var read = session.ReadOnLineAsync(() => Task.FromResult("read"));
            storage.Refuse = writes => IsEntry(writes, "refuse");
            // Every callback has run, so the three are admitted while the first call is in flight.
            await session.LineTickAsync();
            held.Release();
            Equal(0, await first, "first");
            Equal(0, await stored, "stored");
            Equal("refused", (await Outcome(refused))?.Message, "refused");
            SessionFailed(await Outcome(after), "after");
            SessionFailed(await Outcome(read), "read");
            Failed(await session.Closed, "refused", "closed");
            // One call stored the first two of the three batches it got; the refused one and the one after it are not stored.
            Equal(3, storage.LastCall.Count, "batches of the last call");
            Equal("1,1,0", await NotesAsync(storage, a, b, d), "notes");
        });

        // ─── failures per part ──────────────────────────────────────────────

        yield return Case(prefix + "publishes-the-parts-received-before-a-call-rejects-then-rejects-them-with-its-error", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var second = Bump(session, b);
            var third = Bump(session, c);
            storage.RejectCall = storage.CallCount;
            await session.LineTickAsync();
            held.Release();
            await first;
            Equal("connection lost", (await Outcome(second))?.Message, "second");
            Equal("connection lost", (await Outcome(third))?.Message, "third");
            // Durable, so published.
            Equal(3, published.Count, "published");
            Failed(await session.Closed, "connection lost", "closed");
            Equal("1,1,1", await NotesAsync(storage, a, b, c), "notes");
        });

        yield return Case(prefix + "fails-every-job-after-the-first-part-of-a-call-that-rejects-except-its-other-parts-with-their-storage-error", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(1);
            var id = ids[0];
            var held = storage.Hold();
            var first = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("a")), Context);
            await held.Entered;
            // Between the two parts of the failing call, a job without writes.
            var before = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("b")), Context);
            var between = session.CommitAsync(_ => Task.FromResult("no write"), Context);
            var after = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("d")), Context);
            await session.LineTickAsync();
            storage.RejectCall = storage.CallCount;
            held.Release();
            Equal(null, await Outcome(first), "first");
            Equal("connection lost", (await Outcome(before))?.Message, "before");
            SessionFailed(await Outcome(between), "between");
            Equal("connection lost", (await Outcome(after))?.Message, "after");
            // Both parts were received, so they were published before they rejected.
            Equal(2, storage.LastCall.Count, "batches of the last call");
            Equal(3, published.Count, "published");
            Failed(await session.Closed, "connection lost", "closed");
        });

        yield return Case(prefix + "reports-sessionfailed-with-the-first-error-for-a-call-that-rejects-after-another-failure-failed-the-session", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(1);
            var id = ids[0];
            var held = storage.Hold();
            var inFlight = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("a")), Context);
            await held.Entered;
            // A later job's read fails first, and fails the Session; then the call in flight rejects with another error.
            var firstFailure = new InvalidOperationException("first failure: read");
            var once = 0;
            storage.TaskFault = () => Interlocked.Exchange(ref once, 1) == 0 ? firstFailure : null;
            var reading = session.CommitAsync(tx => tx.GetTaskAsync(new TaskId(999)), Context);
            _ = Outcome(reading);
            // The failed Session's close waits on the line for the call in flight, so a line tick would too: this waits for the failure.
            await HarnessTests.Eventually(() => session.Failure is not null);
            Same(firstFailure, session.Failure, "failure");
            storage.RejectCall = storage.CallCount - 1;
            held.Release();
            Same(firstFailure, await Outcome(reading), "reading");
            SessionFailed(await Outcome(inFlight), "in flight", firstFailure);
            // Nothing of the call is published once the Session failed.
            Equal(0, published.Count, "published");
            FailedWith(await session.Closed, firstFailure, "closed");
        });

        yield return Case(prefix + "rejects-every-part-of-a-call-with-its-rejection-value", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var parts = new[] { Outcome(Bump(session, b)), Outcome(Bump(session, c)) };
            await session.LineTickAsync();
            var value = new OddValue();
            storage.Rejection = value;
            storage.RejectCall = storage.CallCount;
            held.Release();
            await first;
            var outcomes = await Task.WhenAll(parts);
            Same(value, outcomes[0], "first part");
            Same(value, outcomes[1], "second part");
            FailedWith(await session.Closed, value, "closed");
        });

        yield return Case(prefix + "rejects-a-refused-part-with-its-refusal-and-the-parts-after-it-with-sessionfailed", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var refused = Outcome(session.CommitAsync(tx => tx.AppendEntryAsync(b, Entry("refuse")), Context));
            var after = Outcome(Bump(session, c));
            await session.LineTickAsync();
            var value = new OddValue();
            storage.Refusal = value;
            storage.Refuse = writes => IsEntry(writes, "refuse");
            held.Release();
            await first;
            Same(value, await refused, "refused");
            SessionFailed(await after, "after");
            FailedWith(await session.Closed, value, "closed");
        });

        yield return Case(prefix + "leaves-the-parts-after-one-whose-adoption-throws-unpublished-rejected-with-sessionfailed", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var before = Bump(session, b);
            var failing = Bump(session, c);
            var after = Bump(session, d);
            var failure = new InvalidOperationException("adoption failed");
            var adoptions = 0;
            session.AdoptFault = () => Interlocked.Increment(ref adoptions) == 3 ? failure : null;
            await session.LineTickAsync();
            held.Release();
            await first;
            await before;
            Same(failure, await Outcome(failing), "failing");
            SessionFailed(await Outcome(after), "after");
            Equal(2, published.Count, "published");
            FailedWith(await session.Closed, failure, "closed");
            // All four are durable: reopening recovers the unpublished one.
            session.AdoptFault = null;
            Equal("1,1,1,1", await NotesAsync(storage, a, b, c, d), "notes");
        });

        // ─── a publication that throws ──────────────────────────────────────

        yield return Case(prefix + "fails-the-session-for-a-deferred-commit-memory-cannot-adopt-and-still-closes", async () =>
        {
            var (_, session, ids, published) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            await Bump(session, a);
            await Bump(session, b);
            published.Clear();
            var failure = new InvalidOperationException("held adoption failed");
            var once = 0;
            session.AdoptHeldFault = () => Interlocked.Exchange(ref once, 1) == 0 ? failure : null;
            var heldCommit = Bump(session, a, DeferredOptions);
            var after = Bump(session, b);
            Same(failure, await Outcome(heldCommit), "held");
            SessionFailed(await Outcome(after), "after");
            Equal(0, published.Count, "published");
            FailedWith(await session.Closed, failure, "closed");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "settles-a-deferred-commit-whose-adoption-throws-alone-and-closes", async () =>
        {
            // Astra's: the only commit is the deferred one, on a storage that runs nothing else.
            var storage = new ControlledStorage(new MemoryStorage());
            var session = new Session(storage);
            var id = await session.CommitAsync(async tx =>
            {
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                await tx.DocAsync(Note, conversation.Id);
                return conversation.Id;
            }, Context);
            var failure = new InvalidOperationException("injected held adoption failure");
            var once = 0;
            session.AdoptHeldFault = () => Interlocked.Exchange(ref once, 1) == 0 ? failure : null;
            Same(failure, await Outcome(Bump(session, id, DeferredOptions)), "deferred");
            Same(failure, session.Failure, "failure");
            await session.CloseAsync(Context);
            FailedWith(await session.Closed, failure, "closed");
            Empty(session.LineSizes, "line sizes");
        });

        // ─── deferred commits ───────────────────────────────────────────────

        yield return Case(prefix + "publishes-a-deferred-commit-only-once-the-commit-before-it-is-published", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            // Only a change of an existing document is held.
            await Bump(session, b);
            published.Clear();
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var later = Bump(session, b, DeferredOptions);
            await session.LineTickAsync();
            Equal(0, published.Count, "published while in flight");
            held.Release();
            await Task.WhenAll(first, later);
            Equal("stored,memory", string.Join(",", published.Snapshot().Select(publication => publication.IsMemory ? "memory" : "stored")), "kinds");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "never-publishes-a-deferred-commit-behind-a-commit-that-fails", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            await Bump(session, b);
            published.Clear();
            var held = storage.Hold();
            var first = session.CommitAsync(tx => tx.AppendEntryAsync(a, Entry("refuse")), Context);
            await held.Entered;
            var later = Bump(session, b, DeferredOptions);
            storage.Refuse = writes => IsEntry(writes, "refuse");
            held.Release();
            Equal("refused", (await Outcome(first))?.Message, "first");
            SessionFailed(await Outcome(later), "later");
            Equal(0, published.Count, "published");
        });

        // ─── publication ────────────────────────────────────────────────────

        yield return Case(prefix + "publishes-the-parts-of-one-call-a-macrotask-apart-what-one-resolves-runs-before-the-next-is-published", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var second = Bump(session, b);
            var third = Bump(session, c);
            var seenAtSecond = -1;
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = second.ContinueWith(_ =>
            {
                Volatile.Write(ref seenAtSecond, published.Count);
                resumed.TrySetResult();
            }, TaskScheduler.Default);
            // The hop after the second's part waits until what the second resolved ran (see the file header).
            session.PublicationHop = _ => Task.WhenAny(resumed.Task, Task.Delay(5_000));
            await session.LineTickAsync();
            held.Release();
            await Task.WhenAll(first, second, third);
            await resumed.Task;
            Equal(2, storage.LastCall.Count, "batches of the last call");
            Equal(2, Volatile.Read(ref seenAtSecond), "published when the second resolved");
            Equal(3, published.Count, "published");
            await session.CloseAsync(Context);
        });

        // ─── the storage's publication scope ────────────────────────────────

        yield return Case(prefix + "publishes-each-commit-inside-the-storage-s-scope-the-next-only-once-one-it-runs-later-ran", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            var scopes = new SyncList<string>();
            var depth = 0;
            // Runs each publication later, inside a scope of its own.
            storage.Scope = (publish, scopeContext) =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Yield();
                    Interlocked.Increment(ref depth);
                    scopes.Add($"open {published.Count}");
                    publish();
                    scopes.Add($"close {published.Count}");
                    Interlocked.Decrement(ref depth);
                });
            };
            var inside = new SyncList<bool>();
            FollowCommits(session, (_, _) => inside.Add(Volatile.Read(ref depth) == 1));
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var rest = new[] { Bump(session, b), Bump(session, c) };
            await session.LineTickAsync();
            inside.Clear();
            held.Release();
            await Task.WhenAll([first, .. rest]);
            Equal("True,True,True", string.Join(",", inside.Snapshot()), "inside");
            Equal("open 0,close 1,open 1,close 2,open 2,close 3", string.Join(",", scopes.Snapshot()), "scopes");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-a-scope-that-throws-instead-of-running-the-publication-and-publishes-the-commit", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(1);
            storage.Scope = (_, _) => throw new InvalidOperationException("no scope");
            Equal(0, await Bump(session, ids[0]), "bump");
            Equal(1, published.Count, "published");
            Equal("no scope", string.Join(",", session.Reports.Snapshot().Select(error => error.Message)), "reports");
            await session.CloseAsync(Context);
        });

        // ─── a failure between two publications ─────────────────────────────

        yield return Case(prefix + "publishes-no-part-of-a-call-after-a-failure-that-a-continuation-of-an-earlier-part-caused", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(1);
            var id = ids[0];
            var held = storage.Hold();
            var lead = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("lead")), Context);
            await held.Entered;
            var one = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("one")), Context);
            var two = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("two")), Context);
            _ = Outcome(two);
            // What `one` resolves runs in the hop before `two` is published: a read there fails the Session.
            var read = new InvalidOperationException("read failed between publications");
            storage.TaskFault = () => read;
            var failing = one.ContinueWith(_ => session.CommitAsync(tx => tx.GetTaskAsync(new TaskId(999)), Context), TaskScheduler.Default).Unwrap();
            _ = Outcome(failing);
            // The hop waits until that read failed the Session (see the file header).
            session.PublicationHop = _ => Task.WhenAny(session.Failed, Task.Delay(5_000));
            await session.LineTickAsync();
            held.Release();
            foreach (var task in new Task[] { lead, one, two, failing }) await Outcome(task);
            Equal("lead,one", EntryKinds(published), "published entries");
            SessionFailed(await Outcome(two), "two", read);
        });

        yield return Case(prefix + "publishes-no-part-of-a-call-after-the-session-fails-in-a-continuation-of-an-earlier-part", async () =>
        {
            // Sol's: `one`'s continuation fails the Session itself.
            var (storage, session, ids, published) = await OpenAsync(1);
            var id = ids[0];
            var held = storage.Hold();
            var lead = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("lead")), Context);
            await held.Entered;
            var failure = new InvalidOperationException("failed between publications");
            var one = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("one")), Context);
            var failed = one.ContinueWith(_ => session.Fail(failure), TaskScheduler.Default);
            var two = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("two")), Context);
            _ = Outcome(two);
            session.PublicationHop = _ => Task.WhenAny(failed, Task.Delay(5_000));
            await session.LineTickAsync();
            held.Release();
            foreach (var task in new Task[] { lead, one, two }) await Outcome(task);
            Equal("lead,one", EntryKinds(published), "published entries");
            SessionFailed(await Outcome(two), "two");
        });

        yield return Case(prefix + "checks-the-failure-when-a-storage-s-scope-runs-a-publication-it-deferred-not-when-it-was-asked-to", async () =>
        {
            var (storage, session, ids, published) = await OpenAsync(1);
            var id = ids[0];
            var failure = new InvalidOperationException("failed before a deferred publication ran");
            var calls = 0;
            // The second publication's scope is entered before the failure; it runs the publication only after it.
            storage.Scope = (publish, scopeContext) =>
            {
                if (Interlocked.Increment(ref calls) == 2) session.Fail(failure);
                _ = Task.Run(async () =>
                {
                    await Task.Yield();
                    publish();
                });
            };
            var held = storage.Hold();
            var lead = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("lead")), Context);
            await held.Entered;
            var one = session.CommitAsync(tx => tx.AppendEntryAsync(id, Entry("one")), Context);
            _ = Outcome(one);
            await session.LineTickAsync();
            held.Release();
            foreach (var task in new Task[] { lead, one }) await Outcome(task);
            Equal("lead", EntryKinds(published), "published entries");
            SessionFailed(await Outcome(one), "one");
        });

        // ─── upkeep through the writer ──────────────────────────────────────

        yield return Case(prefix + "stores-held-changes-for-an-unload-after-the-call-in-flight", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            await Bump(session, a);
            await Bump(session, b, DeferredOptions);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var unloading = session.UnloadDocumentsAsync();
            await Pause();
            Check(!unloading.IsCompleted, "the unload waits for the call in flight");
            held.Release();
            await Task.WhenAll(first, unloading);
            var last = Flat(storage.LastCall).ToList();
            Equal("document.change", string.Join(",", last.Select(write => write.Type)), "the last call");
            SameJson("""{"n":1}""", await session.SnapshotAsync(Note, b, Context), "snapshot");
            await session.CloseAsync(Context);
            Equal("2,1", await NotesAsync(storage, a, b), "notes");
        });

        yield return Case(prefix + "stores-held-changes-for-a-version-reload-inside-a-callback-after-the-call-in-flight", async () =>
        {
            var noteV2 = Documents.DefineConversationDoc("gc.note", 2, DocumentFork.Initial, () => O("""{"n":0,"migrated":true}"""),
                checkpointWhen: (_, _, _) => false, migrate: (value, _) => value.SetItem("migrated", true));
            var (storage, session, ids, _) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            await Bump(session, a);
            await Bump(session, b);
            await Bump(session, b, DeferredOptions);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var reloading = session.CommitAsync(async tx => (int)N((await tx.DocAsync(noteV2, b)).Get("n")), Context);
            await Pause();
            Check(!reloading.IsCompleted, "the reload waits for the call in flight");
            held.Release();
            await first;
            // The reload found what memory held, stored first.
            Equal(2, await reloading, "reloaded");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "stores-held-changes-at-their-deadline-after-the-call-in-flight", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            await Bump(session, a);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var later = Bump(session, b, new CommitOptions(CommitPersistence.Deferred(0)));
            await session.LineTickAsync();
            var calls = storage.CallCount;
            held.Release();
            await Task.WhenAll(first, later);
            await HarnessTests.Eventually(() => storage.CallCount == calls + 1);
            Equal("document.create", string.Join(",", Flat(storage.LastCall).Select(write => write.Type)), "the last call");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "drains-the-calls-in-flight-and-stores-held-changes-when-it-closes", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var later = Bump(session, b, DeferredOptions);
            await session.LineTickAsync();
            var closing = session.CloseAsync(Context);
            held.Release();
            await Task.WhenAll(first, later);
            await closing;
            Equal(SessionEndKind.Closed, (await session.Closed).Kind, "closed");
            Equal("1,1", await NotesAsync(storage, a, b), "notes");
        });

        // ─── backpressure and bounds ────────────────────────────────────────

        yield return Case(prefix + "starts-no-job-past-256-without-a-delivered-outcome-and-returns-its-line-state-to-zero-when-idle", async () =>
        {
            var (storage, session, ids, _) = await OpenAsync(2);
            var (a, b) = (ids[0], ids[1]);
            var held = storage.Hold();
            var first = Bump(session, a);
            await held.Entered;
            var ran = 0;
            var appends = new List<Task>();
            for (var n = 0; n < 300; n++)
            {
                appends.Add(session.CommitAsync(async tx =>
                {
                    Interlocked.Increment(ref ran);
                    await tx.AppendEntryAsync(b, Entry("note"));
                }, Context));
            }
            await HarnessTests.Eventually(() => Volatile.Read(ref ran) == 255);
            await Pause();
            Equal(255, Volatile.Read(ref ran), "ran while in flight");
            Equal(256, session.LineSizes.Outstanding, "outstanding");
            held.Release();
            await Task.WhenAll([first, .. appends]);
            Equal(300, Volatile.Read(ref ran), "ran");
            Empty(session.LineSizes, "line sizes");
            await session.CloseAsync(Context);
        });

        // ─── randomized interleavings ───────────────────────────────────────

        foreach (var seed in new long[] { 1, 7, 42, 1_234, 99_991 })
        {
            yield return Case(prefix + $"stores-publishes-and-reads-only-what-an-order-of-the-commits-implies-seed-{seed}", async () =>
            {
                var Next = Random(seed);
                var (storage, session, ids, _) = await OpenAsync(4);
                var log = ids[0];
                // Storage calls run on the thread pool, so they draw their latency from a generator of their own: the plans stay a function of
                // the seed.
                var delays = Random(seed + 1);
                var latency = new Lock();
                storage.Latency = () =>
                {
                    lock (latency) return (int)Math.Floor(delays() * 3);
                };
                var refuseAt = seed % 2 == 0 ? (int)Math.Floor(Next() * 60) : -1;
                storage.Refuse = writes => writes.Any(write =>
                    write is EntryWrite entry && entry.Value.Data is JsonObjectValue data && data.TryGetValue("index", out var index) && N(index) == refuseAt);
                // Per commit, in line order: the notes it increments and the values it read.
                var plans = new List<ConversationId[]>();
                var reads = new int[]?[60];
                var outcomes = new List<Task>();
                for (var index = 0; index < 60; index++)
                {
                    var touched = ids.Where(_ => Next() < 0.4).ToArray();
                    plans.Add(touched);
                    var at = index;
                    var outcome = session.CommitAsync(async tx =>
                    {
                        var values = new List<int>();
                        foreach (var id in touched) values.Add(Increment(await tx.DocAsync(Note, id)));
                        Volatile.Write(ref reads[at], values.ToArray());
                        await tx.AppendEntryAsync(log, new EntryDraft { Kind = "log", Data = Obj(("index", at)) });
                    }, Context);
                    _ = Outcome(outcome);
                    outcomes.Add(outcome);
                    var pause = Next();
                    if (pause < 0.3) await Outcome(outcome);
                    else if (pause < 0.5) await session.LineTickAsync();
                }
                var stored = new bool[60];
                for (var index = 0; index < 60; index++) stored[index] = await Outcome(outcomes[index]) is null;
                if (refuseAt >= 0) await session.Closed;
                else await session.CloseAsync(Context);
                // Outcomes match storage: a commit resolved exactly when its log entry is stored.
                var reopened = new Session(storage.Reopen());
                var entries = await reopened.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(log), 1_000), Context);
                var logged = entries.Items.Select(entry => (int)N(((JsonObjectValue)entry.Data!)["index"])).ToHashSet();
                for (var index = 0; index < 60; index++) Equal(stored[index], logged.Contains(index), $"commit {index}");
                // Each callback read every earlier stored increment of what it touched, and nothing else.
                for (var index = 0; index < 60; index++)
                {
                    if (Volatile.Read(ref reads[index]) is not { } values) continue;
                    for (var position = 0; position < plans[index].Length; position++)
                    {
                        var id = plans[index][position];
                        var expected = Enumerable.Range(0, index).Count(earlier => stored[earlier] && plans[earlier].Contains(id));
                        Equal(expected, values[position], $"commit {index} read of {id}");
                    }
                }
                // Storage equals the stored commits replayed in order.
                foreach (var id in ids)
                {
                    var increments = Enumerable.Range(0, 60).Count(index => stored[index] && plans[index].Contains(id));
                    var value = await reopened.SnapshotAsync(Note, id, Context);
                    Equal(increments, value is null ? 0 : (int)N(value["n"]), $"note {id}");
                }
                Empty(session.LineSizes, "line sizes");
                await reopened.CloseAsync(Context);
            });
        }
    }
}
