// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-adoption.test.ts, new in Pi v1.5.0 (8b0caefc2 and
// e5c6182ba, sync area publication-caches). Adoption after Storage stored a commit, and the order of a publication: the Session's own
// observers once Storage resolved, then the host observers attached when the dispatch runs, none for a commit published without one; an
// own observer's throw fails the Session after the hosts got the commit; a commit Storage rejects after adopting it is published, then
// fails the Session. Verbatim on FaultyStorage, a ControlledStorage over MemoryStorage as upstream's, apart from the adaptations in
// SessionSupport.cs and these:
// - vi.spyOn(Transaction.prototype, "adopt") is Session.AdoptFault: it runs once per adoption, so its calls count them, and one returning
//   an error is upstream's mockImplementationOnce that throws. The spy on Transaction.prototype.discard is Session.OnDiscard.
// - "discards the staged changes when Storage rejects before adoption" mocks MemoryStorage's commit to reject once; here FaultyStorage's
//   fault RejectBeforeAdopt throws before it reaches its backend.
// - Upstream's host dispatch is a microtask queued at the publication; PiSharp runs it right after the Session's own observers, in the
//   same publication (Session.Publish), which keeps every order these cases assert: no wait and no other code can run in between.
// - The JSONL case's NodeExecutionEnv subclass is a LocalFileSystem subclass recording each rename.
// - toEqual of a failed Session end compares its kind and its error (by reference, or by message for a storage error built inside it).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionAdoptionCases() => SessionAdoptionTests.Cases("session.adoption.");
}

internal static class SessionAdoptionTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly SessionDocToken CounterDoc =
        Documents.DefineSessionDoc("adoption.counter", 1, () => O("""{"count":0}"""), checkpointWhen: (_, _, _) => false);

    /// <summary>A latest, initial-fork conversation document, the kind a deferred commit may hold.</summary>
    private static readonly ConversationDocToken NoteDoc =
        Documents.DefineConversationDoc("adoption.note", 1, DocumentFork.Initial, () => O("{}"), checkpointWhen: (_, _, _) => false);

    private static readonly CommitOptions Deferred = new(CommitPersistence.Deferred(double.PositiveInfinity));

    private enum Fault { None, RejectAfterAdopt, SkipAdopt, RejectBeforeAdopt }

    /// <summary>Controlled storage that records the receipt (<c>adopt</c>) and its own resolution in <see cref="Order"/>; its next commit
    /// misbehaves as <see cref="Next"/> says (upstream <c>FaultyStorage</c>).</summary>
    private sealed class FaultyStorage() : ControlledStorage(new MemoryStorage())
    {
        private int next;

        public Fault Next
        {
            set => Volatile.Write(ref next, (int)value);
        }

        public SyncList<string> Order { get; } = new();

        public override async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive,
            DurableContext context)
        {
            var fault = (Fault)Interlocked.Exchange(ref next, (int)Fault.None);
            if (fault == Fault.RejectBeforeAdopt) throw new InvalidOperationException("disk full");
            if (fault == Fault.SkipAdopt) return await base.CommitAsync(batches, (_, _) => { }, context);
            var result = await base.CommitAsync(batches, (index, committed) =>
            {
                Order.Add("adopt");
                receive(index, committed);
                Order.Add("adopted");
            }, context);
            Order.Add("resolve");
            if (fault == Fault.RejectAfterAdopt) throw new InvalidOperationException("reclaim failed");
            return result;
        }
    }

    /// <summary>A Session that keeps its reports.</summary>
    private sealed class ReportingSession(IStorage storage) : Session(storage)
    {
        public SyncList<Exception> Reports { get; } = new();

        internal override void Report(Exception error, ReportInfo info, DurableContext? context = null) => Reports.Add(error);
    }

    private static (FaultyStorage Storage, ReportingSession Session) Open()
    {
        var storage = new FaultyStorage { KeepOpenForReopen = true };
        return (storage, new ReportingSession(storage));
    }

    private static Task Bump(Session session) => session.CommitAsync(async tx =>
    {
        var counter = await tx.DocAsync(CounterDoc);
        counter.Set("count", N(counter.Get("count")) + 1);
    }, Context);

    private static async Task<double> Count(Session session) => N((await session.SnapshotAsync(CounterDoc, Context))!["count"]);

    private static async Task<double> ReopenedCount(FaultyStorage storage)
    {
        var reopened = new Session(storage.Reopen());
        var count = await Count(reopened);
        await reopened.CloseAsync(Context);
        return count;
    }

    private static CommitObservation ObserveHost(Session session, Action<CommitPublication> advance) =>
        session.ObserveCommits(new CommitObserver((publication, _) => advance(publication), _ => { }));

    private static void FailedWith(SessionEnd end, Exception error, string what)
    {
        Equal(SessionEndKind.Failed, end.Kind, what + " kind");
        Check(ReferenceEquals(error, end.Error), what + ": not the same error");
    }

    private static void FailedWithMessage(SessionEnd end, string message, string what)
    {
        Equal(SessionEndKind.Failed, end.Kind, what + " kind");
        Equal(message, end.Error?.Message, what + " error");
    }

    private static string Ops(IEnumerable<ICommitChange> changes) =>
        "[" + string.Join(",", changes.OfType<DocumentValueChange>().Select(change => OpsJson(change.Ops))) + "]";

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        foreach (var adoption in Adoption(prefix + "after-storage.")) yield return adoption;
        foreach (var publication in Publication(prefix + "publication.")) yield return publication;
    }

    /// <summary>Upstream's "Session adoption after Storage stored a commit".</summary>
    private static IEnumerable<(string, Func<Task>)> Adoption(string prefix)
    {
        yield return Case(prefix + "takes-the-durability-receipt-inside-the-commit-and-adopts-once-storage-resolved", async () =>
        {
            var (storage, session) = Open();
            var adoptions = 0;
            session.AdoptFault = () =>
            {
                Interlocked.Increment(ref adoptions);
                return null;
            };
            await Bump(session);
            Equal("adopt,adopted,resolve", string.Join(",", storage.Order.Snapshot()), "order");
            Equal(1, Volatile.Read(ref adoptions), "adoptions");
            Equal(1.0, await Count(session), "count");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-the-session-without-discarding-when-storage-rejects-after-adoption", async () =>
        {
            var (storage, session) = Open();
            await Bump(session);
            var discards = 0;
            session.OnDiscard = () => Interlocked.Increment(ref discards);
            storage.Next = Fault.RejectAfterAdopt;
            await RejectsAsync(() => Bump(session), "reclaim failed", "bump");
            Equal(0, Volatile.Read(ref discards), "discards");
            FailedWithMessage(await session.Closed, "reclaim failed", "closed");
        });

        yield return Case(prefix + "discards-the-staged-changes-when-storage-rejects-before-adoption", async () =>
        {
            var (storage, session) = Open();
            await Bump(session);
            var discards = 0;
            session.OnDiscard = () => Interlocked.Increment(ref discards);
            storage.Next = Fault.RejectBeforeAdopt;
            await RejectsAsync(() => Bump(session), "disk full", "bump");
            Equal(1, Volatile.Read(ref discards), "discards");
            Equal(SessionEndKind.Failed, (await session.Closed).Kind, "closed");
        });

        yield return Case(prefix + "fails-the-session-with-its-own-adoption-error-which-never-reaches-storage", async () =>
        {
            var (storage, session) = Open();
            await Bump(session);
            var failure = new InvalidOperationException("adoption failed");
            var once = 0;
            session.AdoptFault = () => Interlocked.Exchange(ref once, 1) == 0 ? failure : null;
            var error = await RejectsAsync(() => Bump(session), "adoption failed", "bump");
            Check(ReferenceEquals(failure, error), "bump: not the adoption error");
            // Storage saw a normal adoption and resolved.
            Equal("adopt,adopted,resolve", string.Join(",", storage.Order.Snapshot().TakeLast(3)), "order");
            FailedWith(await session.Closed, failure, "closed");
        });

        yield return Case(prefix + "fails-the-session-when-storage-resolves-without-adopting", async () =>
        {
            var (storage, session) = Open();
            storage.Next = Fault.SkipAdopt;
            await RejectsAsync(() => Bump(session), "without adopting", "bump");
            Equal(SessionEndKind.Failed, (await session.Closed).Kind, "closed");
        });
    }

    /// <summary>Upstream's "publication after storage".</summary>
    private static IEnumerable<(string, Func<Task>)> Publication(string prefix)
    {
        yield return Case(prefix + "runs-its-own-observers-once-storage-resolved-and-host-listeners-one-microtask-later", async () =>
        {
            var (storage, session) = Open();
            FollowCommits(session, (_, _) => storage.Order.Add($"observed adopting={storage.Adopting.ToString().ToLowerInvariant()}"));
            ObserveHost(session, _ => storage.Order.Add($"host adopting={storage.Adopting.ToString().ToLowerInvariant()}"));
            await Bump(session);
            Equal("adopt,adopted,resolve,observed adopting=false,host adopting=false", string.Join(",", storage.Order.Snapshot()), "order");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "calls-the-host-listeners-registered-when-its-dispatch-runs-and-none-for-a-commit-published-without-one", async () =>
        {
            var (_, session) = Open();
            var calls = new SyncList<string>();
            var removed = ObserveHost(session, _ => calls.Add("removed"));
            ObserveHost(session, _ => calls.Add("kept"));
            // During the publication, after the dispatch was queued.
            var changed = 0;
            FollowCommits(session, (_, _) =>
            {
                if (Interlocked.Exchange(ref changed, 1) == 1) return;
                removed.Detach();
                ObserveHost(session, _ => calls.Add("added"));
            });
            await Bump(session);
            Equal("kept,added", string.Join(",", calls.Snapshot()), "calls");
            // A Session with no host listener at the publication queues no dispatch, so one added during it misses it.
            var bare = Open();
            var late = new SyncList<Seq>();
            var added = 0;
            FollowCommits(bare.Session, (_, _) =>
            {
                if (Interlocked.Exchange(ref added, 1) == 1) return;
                ObserveHost(bare.Session, publication => late.Add(publication.Seq));
            });
            await Bump(bare.Session);
            Equal(0, late.Count, "late before");
            await Bump(bare.Session);
            Equal(1, late.Count, "late after");
            await session.CloseAsync(Context);
            await bare.Session.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-the-session-after-storage-returned-for-a-throw-of-its-own-observers-the-others-and-hosts-got-the-commit", async () =>
        {
            var (storage, session) = Open();
            var bug = new InvalidOperationException("observer bug");
            FollowCommits(session, (_, _) =>
            {
                storage.Order.Add("observed");
                throw bug;
            });
            FollowCommits(session, (_, _) => storage.Order.Add("observed after"));
            ObserveHost(session, _ => storage.Order.Add($"host failed={(session.Failure is not null).ToString().ToLowerInvariant()}"));
            session.SubscribeClose(() => storage.Order.Add("close"));
            await Bump(session);
            // Published once Storage resolved; the Session failed only after the hosts got the commit.
            Equal("adopt,adopted,resolve,observed,observed after,host failed=false,close", string.Join(",", storage.Order.Snapshot()), "order");
            FailedWith(await session.Closed, bug, "closed");
            Equal(1.0, await ReopenedCount(storage), "reopened count");
        });

        yield return Case(prefix + "publishes-a-commit-that-storage-rejects-after-adopting-it-then-fails-the-session", async () =>
        {
            var (storage, session) = Open();
            await Bump(session);
            var stored = new SyncList<Seq>();
            FollowCommits(session, (_, _) => storage.Order.Add("observed"));
            ObserveHost(session, publication =>
            {
                storage.Order.Add("host");
                stored.Add(publication.Seq);
            });
            storage.Order.Clear();
            storage.Next = Fault.RejectAfterAdopt;
            await RejectsAsync(() => Bump(session), "reclaim failed", "bump");
            Equal("adopt,adopted,resolve,observed,host", string.Join(",", storage.Order.Snapshot()), "order");
            Equal(1, stored.Count, "stored");
            Equal(SessionEndKind.Failed, (await session.Closed).Kind, "closed");
            Equal(2.0, await ReopenedCount(storage), "reopened count");
        });

        yield return Case(prefix + "keeps-what-host-listeners-get-exactly-what-was-stored-held-changes-included", async () =>
        {
            var (storage, session) = Open();
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(async tx => await tx.DocAsync(NoteDoc, conversationId), Context);
            var observed = new SyncList<ObservedCommit>();
            var stored = new SyncList<CommitPublication>();
            FollowCommits(session, (publication, _) =>
            {
                storage.Order.Add("observed");
                observed.Add(publication);
            });
            ObserveHost(session, publication =>
            {
                storage.Order.Add("host");
                stored.Add(publication);
            });
            storage.Order.Clear();
            await session.CommitAsync(async tx => (await tx.DocAsync(NoteDoc, conversationId)).Set("a", 1), Context, Deferred);
            // Held in memory: observed at once, nothing stored.
            Equal("observed", string.Join(",", storage.Order.Snapshot()), "held order");
            await session.CommitAsync(async tx => (await tx.DocAsync(NoteDoc, conversationId)).Set("b", 2), Context);
            Equal("observed,adopt,adopted,resolve,observed,host", string.Join(",", storage.Order.Snapshot()), "order");
            Equal("""[[["s",["b"],2]]]""", Ops(observed[1].Changes), "observed ops");
            Equal(1, stored.Count, "stored");
            Equal("""[[["s",["a"],1],["s",["b"],2]]]""", Ops(stored[0].Changes), "stored ops");
            Check(!observed[1].IsMemory && observed[1].Seq is { } seq && stored[0].Seq == seq, "the same seq");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-a-store-of-held-changes-to-host-listeners-once-storage-resolved", async () =>
        {
            var (storage, session) = Open();
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(async tx => await tx.DocAsync(NoteDoc, conversationId), Context);
            ObserveHost(session, _ => storage.Order.Add($"host adopting={storage.Adopting.ToString().ToLowerInvariant()}"));
            await session.CommitAsync(async tx => (await tx.DocAsync(NoteDoc, conversationId)).Set("a", 1), Context, Deferred);
            storage.Order.Clear();
            await session.UnloadDocumentsAsync();
            Equal("adopt,adopted,resolve,host adopting=false", string.Join(",", storage.Order.Snapshot()), "order");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-a-jsonl-commit-once-the-storage-reclaimed-its-sidecar-and-resolved", async () =>
        {
            // A base at the second count makes JSONL rewrite the document's sidecar after adopting the commit.
            var rebased = Documents.DefineSessionDoc("adoption.rebased", 1, () => O("""{"count":0}"""),
                checkpointWhen: (value, _, _) => N(value["count"]) == 2);
            var directory = Path.Combine(Path.GetTempPath(), "pi-durable-adoption-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fs = new RenameLog(directory);
            var session = new Session(await JsonlStorage.OpenAsync(directory, fs, Context));
            Task Count() => session.CommitAsync(async tx =>
            {
                var counter = await tx.DocAsync(rebased);
                counter.Set("count", N(counter.Get("count")) + 1);
            }, Context);
            try
            {
                await Count();
                ObserveHost(session, _ => fs.Order.Add("host"));
                await Count();
                fs.Order.Add("resolved");
                var order = fs.Order.Snapshot();
                Equal(3, order.Count, "order: " + string.Join(",", order));
                Check(System.Text.RegularExpressions.Regex.IsMatch(order[0], @"^rename doc-\d+\.jsonl$"), "first: " + order[0]);
                Equal("host,resolved", string.Join(",", order.Skip(1)), "order after the rename");
            }
            finally
            {
                await session.CloseAsync(Context);
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });

        yield return Case(prefix + "publishes-a-store-of-held-changes-that-storage-rejects-after-adopting-it-then-fails-the-session", async () =>
        {
            var (storage, session) = Open();
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(async tx => await tx.DocAsync(NoteDoc, conversationId), Context);
            var stored = new SyncList<string>();
            ObserveHost(session, publication => stored.Add(Ops(publication.Changes)));
            await session.CommitAsync(async tx => (await tx.DocAsync(NoteDoc, conversationId)).Set("a", 1), Context, Deferred);
            storage.Order.Clear();
            storage.Next = Fault.RejectAfterAdopt;
            await RejectsAsync(() => session.UnloadDocumentsAsync(), "reclaim failed", "unload");
            Equal("adopt,adopted,resolve", string.Join(",", storage.Order.Snapshot()), "order");
            Equal("""[[["s",["a"],1]]]""", string.Join(",", stored.Snapshot()), "stored");
            FailedWithMessage(await session.Closed, "reclaim failed", "closed");
        });
    }

    /// <summary>A local file system that records each sidecar rename, the last step of a reclaim (upstream's <c>RenameLog</c>).</summary>
    private sealed class RenameLog(string cwd) : LocalFileSystem(cwd)
    {
        public SyncList<string> Order { get; } = new();

        public override Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context)
        {
            Order.Add("rename " + Path.GetFileName(destinationPath));
            return base.RenameFileAsync(sourcePath, destinationPath, context);
        }
    }
}
