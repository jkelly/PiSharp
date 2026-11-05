// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-observers.test.ts (Pi v1.5.0 sync, areas observers,
// 8b0caefc2, and vocabulary, 5f312dac4): Session.observeDoc(), the synchronous observer of one document address that watches are built on
// (session.observers.observe-doc.*, observe-doc-throwing-asynchronously.* and observe-doc-detached-during-a-commit.*), and
// Session.ObserveCommits() with a CommitObserver (session.observers.observe-commits.*). Verbatim apart from the adaptations in
// SessionSupport.cs, and these:
// - ReportingSession overrides the internal Session.Report, as upstream's overrides report().
// - An observer is Observer<T> over two callbacks; "an observer whose call returns a rejected promise" is its task-returning form with an
//   async callback that throws.
// - A missing scope ID is an owner ID that is not a safe integer: ResolveAddress's ArgumentException, where upstream's is a TypeError.
// - An aborted controller is a DurableCancellation cancelled with an exception, which the attachment faults with.
// - The garbage-collection cases use WeakReference and GC.Collect for WeakRef and --expose-gc, with what they hold built in a method the JIT
//   does not inline (SessionWatchesCases.cs).
// - toEqual of recorded calls compares their descriptions (Describe); toBe is ReferenceEquals.
// - commits()'s `{ seq: expect.any(Number) }` is a recorded "seq <n>"; flush() (a macrotask) is the Session line barrier, Flush.
using System.Runtime.CompilerServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionObserversCases() => SessionObserverTests.Cases("session.observers.");
}

internal static class SessionObserverTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly SessionDocToken SessionCounter =
        Documents.DefineSessionDoc("observe.session-counter", 1, () => O("""{"count":0}"""), checkpointWhen: (_, _, _) => false);

    /// <summary>A latest, initial-fork conversation document, the kind a deferred commit may hold.</summary>
    private static readonly ConversationDocToken ConversationCounter = Documents.DefineConversationDoc("observe.conversation-counter", 1,
        DocumentFork.Initial, () => O("""{"count":0}"""), checkpointWhen: (_, _, _) => false);

    /// <summary>A Session that keeps its reports, which a plain Session drops.</summary>
    private sealed class ReportingSession(IStorage storage) : Session(storage)
    {
        public SyncList<(Exception Error, ReportInfo Info)> Reports { get; } = new();

        internal override void Report(Exception error, ReportInfo info, DurableContext? context = null) => Reports.Add((error, info));
    }

    /// <summary>An observer that records each call, and whether it ran inside the storage's receive (upstream <c>recorder</c>).</summary>
    private sealed class Recorder
    {
        public Recorder(ControlledStorage storage) => Observer = new Observer<JsonObjectValue>(
            (call, _) =>
            {
                Advances.Add(call);
                Calls.Add(Describe(call, storage.Adopting));
            },
            end => Calls.Add(Describe(end)));

        public SyncList<string> Calls { get; } = new();
        public SyncList<WatchCall<JsonObjectValue>> Advances { get; } = new();
        public Observer<JsonObjectValue> Observer { get; }
    }

    /// <summary>A recorded advance: value, ops, retired and adopting, as upstream's toEqual compares them.</summary>
    internal static string Describe<T>(WatchCall<T> call, bool adopting) where T : class
    {
        var (value, ops, retired) = Change(call);
        var json = value switch
        {
            JsonValue plain => plain.ToJson(),
            ConversationView view => view.ToJson().ToJson(),
            TaskGraph graph => graph.ToJson().ToJson(),
            _ => value.ToString(),
        };
        return $"advance {json} {OpsJson(ops)} retired={retired.ToString().ToLowerInvariant()} adopting={adopting.ToString().ToLowerInvariant()}";
    }

    /// <summary>A recorded end: its kind, and its error's message.</summary>
    internal static string Describe(WatchEnd end) => end.Error is Exception error ? $"end {end.Kind} {error.Message}" : $"end {end.Kind}";

    private static (ControlledStorage Storage, ReportingSession Session) Open()
    {
        var storage = new ControlledStorage(new MemoryStorage());
        return (storage, new ReportingSession(storage));
    }

    private static Task Add(Session session, double by = 1) =>
        session.CommitAsync(async tx =>
        {
            var counter = await tx.DocAsync(SessionCounter);
            counter["count"] = N(counter["count"]) + by;
        }, Context);

    private static Task SetConversationCount(Session session, ConversationId conversationId, double count, CommitOptions? options = null) =>
        session.CommitAsync(async tx => (await tx.DocAsync(ConversationCounter, conversationId))["count"] = count, Context, options);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        foreach (var observeDoc in ObserveDoc(prefix + "observe-doc.")) yield return observeDoc;
        foreach (var throwing in ThrowingAsynchronously(prefix + "observe-doc-throwing-asynchronously.")) yield return throwing;
        foreach (var commits in ObserveCommits(prefix + "observe-commits.")) yield return commits;
        foreach (var detached in DetachedDuringACommit(prefix + "observe-doc-detached-during-a-commit.")) yield return detached;
    }

    private static IEnumerable<(string, Func<Task>)> ObserveDoc(string prefix)
    {
        yield return Case(prefix + "returns-undefined-for-an-absent-address-and-attaches-nothing", async () =>
        {
            var (storage, session) = Open();
            var recorder = new Recorder(storage);
            Check(await session.ObserveDocAsync(SessionCounter, recorder.Observer, Context) is null, "absent");
            await Add(session);
            Seq([], recorder.Calls.Snapshot(), "calls");
            Equal(0, session.DocumentCacheSizes.Attached, "attached");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "starts-at-the-committed-value-and-advances-at-the-publication-of-each-stored-commit-with-its-ops", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var recorder = new Recorder(storage);
            var observation = (await session.ObserveDocAsync(SessionCounter, recorder.Observer, Context))!;
            SameJson("""{"count":1}""", observation.Value, "value");
            var committing = Add(session, 2);
            await committing;
            Seq(["""advance {"count":3} [["s",["count"],3]] retired=false adopting=false"""], recorder.Calls.Snapshot(), "calls");
            observation.Detach();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "advances-when-memory-adopts-a-held-commit-before-anything-is-stored", async () =>
        {
            var (storage, session) = Open();
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(tx => tx.DocAsync(ConversationCounter, conversationId), Context);
            var recorder = new Recorder(storage);
            var observation = (await session.ObserveDocAsync(ConversationCounter, conversationId, recorder.Observer, Context))!;
            var stored = storage.CommitCount;
            await SetConversationCount(session, conversationId, 5, new CommitOptions(CommitPersistence.Deferred(double.PositiveInfinity)));
            Equal(stored, storage.CommitCount, "nothing stored");
            Seq(["""advance {"count":5} [["s",["count"],5]] retired=false adopting=false"""], recorder.Calls.Snapshot(), "calls");
            observation.Detach();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "follows-a-conversation-documents-address-a-retirement-with-the-last-value-then-the-new-incarnation", async () =>
        {
            var (storage, session) = Open();
            var conversationId = await CreateConversation(session);
            await SetConversationCount(session, conversationId, 2);
            var recorder = new Recorder(storage);
            var observation = (await session.ObserveDocAsync(ConversationCounter, conversationId, recorder.Observer, Context))!;
            await session.CommitAsync(tx => tx.RetireDocAsync(ConversationCounter, conversationId), Context);
            // The address is absent now: a new attachment finds nothing, the existing one goes on.
            Check(await session.ObserveDocAsync(ConversationCounter, conversationId, new Recorder(storage).Observer, Context) is null, "absent");
            await SetConversationCount(session, conversationId, 9);
            Seq([
                """advance {"count":2} [] retired=true adopting=false""",
                """advance {"count":9} [["r",{"count":9}]] retired=false adopting=false""",
            ], recorder.Calls.Snapshot(), "calls");
            Same(observation.Value, Change(recorder.Advances[0]).Value, "the retirement's value is the observation's");
            observation.Detach();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "gets-nothing-after-detach-which-is-idempotent-and-unpins-the-document", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var recorder = new Recorder(storage);
            var observation = (await session.ObserveDocAsync(SessionCounter, recorder.Observer, Context))!;
            Equal(1, session.DocumentCacheSizes.Attached, "attached");
            observation.Detach();
            observation.Detach();
            Equal(0, session.DocumentCacheSizes.Attached, "detached");
            await Add(session);
            await session.CloseAsync(Context);
            Seq([], recorder.Calls.Snapshot(), "calls");
        });

        yield return Case(prefix + "ends-once-when-the-session-closes-failed-s1-an-adapter-follows-the-next-session-a-watch-ended", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var recorder = new Recorder(storage);
            await session.ObserveDocAsync(SessionCounter, recorder.Observer, Context);
            var watch = (await session.WatchDocAsync(SessionCounter, Context))!;
            await session.CloseAsync(Context);
            Seq(["end Failed session closed"], recorder.Calls.Snapshot(), "calls");
            Equal(WatchEnd.Ended, await watch.Closed, "the watch");
        });

        yield return Case(prefix + "ends-each-closes-observers-with-an-error-of-that-close-not-one-shared-by-every-session", async () =>
        {
            var errors = new SyncList<object?>();
            for (var i = 0; i < 2; i++)
            {
                var (_, session) = Open();
                await Add(session);
                await session.ObserveDocAsync(SessionCounter, new Observer<JsonObjectValue>((_, _) => { },
                    end => errors.Add(end.Kind == WatchEndKind.Failed ? end.Error : end)), Context);
                await session.CloseAsync(Context);
            }
            var all = errors.Snapshot();
            Seq(["session closed", "session closed"], all.Select(error => (error as Exception)?.Message), "errors");
            Check(!ReferenceEquals(all[0], all[1]), "one error per close");
        });

        yield return Case(prefix + "ends-with-the-error-that-failed-the-session", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var recorder = new Recorder(storage);
            var ends = new SyncList<WatchEnd>();
            await session.ObserveDocAsync(SessionCounter, new Observer<JsonObjectValue>((call, _) => recorder.Advances.Add(call), ends.Add), Context);
            var failure = new InvalidOperationException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsAsync(() => Add(session), "disk gone", "the failing commit");
            await session.Closed;
            Seq([WatchEnd.Failed(failure)], ends.Snapshot(), "ends");
            Equal(0, recorder.Advances.Count, "no call");
        });

        yield return Case(prefix + "reports-a-throwing-observer-detaches-it-and-ends-it-with-the-error-the-commit-and-other-observers-go-on", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var bug = new InvalidOperationException("observer bug");
            var ends = new SyncList<WatchEnd>();
            await session.ObserveDocAsync(SessionCounter, new Observer<JsonObjectValue>((_, _) => throw bug, ends.Add), Context);
            var healthy = new Recorder(storage);
            await session.ObserveDocAsync(SessionCounter, healthy.Observer, Context);
            await Add(session);
            await Add(session);
            Seq([(bug, ReportInfo.ListenerError)], session.Reports.Snapshot(), "reports");
            Seq([WatchEnd.ListenerError(bug)], ends.Snapshot(), "ends");
            Equal(2, healthy.Calls.Count, "the healthy observer");
            SameJson("""{"count":3}""", await session.SnapshotAsync(SessionCounter, Context), "snapshot");
            await session.CloseAsync(Context);
            // Detached at its throw: the close does not end it again.
            Equal(1, ends.Count, "one end");
            Check(session.Failure is null, "the Session did not fail");
        });

        yield return Case(prefix + "attaches-atomically-on-the-line-a-commit-queued-before-the-attachment-is-in-its-value", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var recorder = new Recorder(storage);
            var committing = Add(session, 10);
            var observation = (await session.ObserveDocAsync(SessionCounter, recorder.Observer, Context))!;
            await committing;
            SameJson("""{"count":11}""", observation.Value, "value");
            Seq([], recorder.Calls.Snapshot(), "calls");
            observation.Detach();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-an-attachment-whose-context-is-cancelled-and-attaches-nothing", async () =>
        {
            var (storage, session) = Open();
            await Add(session);
            var cancelled = Context.WithCancel();
            cancelled.Cancel(new InvalidOperationException("gone"));
            await RejectsAsync(() => session.ObserveDocAsync(SessionCounter, new Recorder(storage).Observer, cancelled.Context), "gone", "attachment");
            Equal(0, session.DocumentCacheSizes.Attached, "attached");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-no-detached-observer-alive", async () =>
        {
            var session = new Session(new MemoryStorage());
            await Add(session);
            // Control: an attached observer stays, so the collection below is real.
            var attached = await AttachUnreferenced(session, detach: false);
            await CollectGarbage();
            Check(attached.Observer.IsAlive, "an attached observer stays");
            attached.Observation.Detach();

            var detached = await AttachUnreferenced(session, detach: true);
            await CollectGarbage();
            Check(!detached.Observer.IsAlive, "the detached observer is gone");
            await Flush(session);
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-never-throws-on-a-closed-session-or-a-missing-scope-id", async () =>
        {
            var (storage, session) = Open();
            var missing = Attach(() => session.ObserveDocAsync(ConversationCounter, new ConversationId(long.MaxValue), new Recorder(storage).Observer,
                Context), "missing scope ID");
            var error = await RejectsAsync(() => missing, "requires a conversation ID", "missing scope ID");
            Check(error is ArgumentException, "an argument error");
            await session.CloseAsync(Context);
            var closed = Attach(() => session.ObserveDocAsync(SessionCounter, new Recorder(storage).Observer, Context), "closed");
            await RejectsAsync(() => closed, "Session is closed", "closed");
        });

        yield return Case(prefix + "keeps-no-value-of-a-document-retired-for-good-while-still-attached", async () =>
        {
            var session = new Session(new MemoryStorage());
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(tx => tx.DocAsync(ConversationCounter, conversationId), Context);
            var (observation, last) = await RetireObserved(session, conversationId);
            await CollectGarbage();
            Check(!last.IsAlive, "the retired value is gone");
            observation.Detach();
            await session.CloseAsync(Context);
        });
    }

    private static IEnumerable<(string, Func<Task>)> ThrowingAsynchronously(string prefix)
    {
        yield return Case(prefix + "reports-an-observer-whose-call-returns-a-rejected-promise-then-detaches-it-and-ends-it-listener-error", async () =>
        {
            var (_, session) = Open();
            await Add(session);
            var bug = new InvalidOperationException("async observer bug");
            var ends = new SyncList<WatchEnd>();
            var calls = 0;
            var observer = new Observer<JsonObjectValue>(async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                throw bug;
            }, ends.Add);
            await session.ObserveDocAsync(SessionCounter, observer, Context);
            await Add(session);
            await HarnessTests.Eventually(() => ends.Count == 1);
            await Add(session);
            Equal(1, Volatile.Read(ref calls), "calls");
            Seq([(bug, ReportInfo.ListenerError)], session.Reports.Snapshot(), "reports");
            Seq([WatchEnd.ListenerError(bug)], ends.Snapshot(), "ends");
            await session.CloseAsync(Context);
            Equal(1, ends.Count, "one end");
        });
    }

    /// <summary>A commit observer recording each seq and its end (upstream <c>commits</c>).</summary>
    private sealed class Commits
    {
        public Commits() => Observer = new CommitObserver((publication, _) => Calls.Add($"seq {publication.Seq.Value}"), end =>
        {
            Ends.Add(end);
            Calls.Add(Describe(end));
        });

        public SyncList<string> Calls { get; } = new();
        public SyncList<WatchEnd> Ends { get; } = new();
        public CommitObserver Observer { get; }
    }

    private static IEnumerable<(string, Func<Task>)> ObserveCommits(string prefix)
    {
        yield return Case(prefix + "gets-every-stored-commit-then-failed-once-the-session-closed-s1-after-its-last-publication", async () =>
        {
            var (_, session) = Open();
            var commits = new Commits();
            session.ObserveCommits(commits.Observer);
            await Add(session);
            await Add(session);
            await session.CloseAsync(Context);
            var calls = commits.Calls.Snapshot();
            Equal(3, calls.Count, "calls");
            Check(calls[0].StartsWith("seq ", StringComparison.Ordinal) && calls[1].StartsWith("seq ", StringComparison.Ordinal), "two seqs");
            Equal("end Failed session closed", calls[2], "the end");
        });

        yield return Case(prefix + "ends-failed-with-the-error-that-failed-the-session", async () =>
        {
            var (storage, session) = Open();
            var commits = new Commits();
            session.ObserveCommits(commits.Observer);
            var failure = new InvalidOperationException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsAsync(() => Add(session), "disk gone", "the failing commit");
            await session.Closed;
            Seq(["end Failed disk gone"], commits.Calls.Snapshot(), "calls");
            Seq([WatchEnd.Failed(failure)], commits.Ends.Snapshot(), "ends");
        });

        yield return Case(prefix + "gets-nothing-after-detach-which-is-idempotent-not-even-its-end", async () =>
        {
            var (_, session) = Open();
            var commits = new Commits();
            var observation = session.ObserveCommits(commits.Observer);
            await Add(session);
            await Flush(session);
            observation.Detach();
            observation.Detach();
            await Add(session);
            await session.CloseAsync(Context);
            Equal(1, commits.Calls.Count, "calls");
        });

        yield return Case(prefix + "reports-a-throwing-observer-detaches-it-and-ends-it-listener-error-the-others-go-on", async () =>
        {
            var (_, session) = Open();
            var bug = new InvalidOperationException("commit observer bug");
            var ends = new SyncList<WatchEnd>();
            var thrown = 0;
            session.ObserveCommits(new CommitObserver((_, _) =>
            {
                Interlocked.Increment(ref thrown);
                throw bug;
            }, ends.Add));
            var healthy = new Commits();
            session.ObserveCommits(healthy.Observer);
            await Add(session);
            await Add(session);
            await Flush(session);
            Equal(1, Volatile.Read(ref thrown), "thrown");
            Seq([(bug, ReportInfo.ListenerError)], session.Reports.Snapshot(), "reports");
            Seq([WatchEnd.ListenerError(bug)], ends.Snapshot(), "ends");
            Equal(2, healthy.Calls.Count, "the healthy observer");
            await session.CloseAsync(Context);
            Equal(1, ends.Count, "one end");
        });
    }

    private static IEnumerable<(string, Func<Task>)> DetachedDuringACommit(string prefix)
    {
        /// A Session with a conversation counter at 1, and the commit that retires it and creates it again at 2 (upstream retiring).
        static async Task<(ControlledStorage Storage, ReportingSession Session, ConversationId ConversationId, Func<Task> Recreate)> Retiring()
        {
            var (storage, session) = Open();
            var conversationId = await CreateConversation(session);
            await SetConversationCount(session, conversationId, 1);
            Task Recreate() => session.CommitAsync(async tx =>
            {
                await tx.RetireDocAsync(ConversationCounter, conversationId);
                (await tx.DocAsync(ConversationCounter, conversationId))["count"] = 2;
            }, Context);
            return (storage, session, conversationId, Recreate);
        }

        yield return Case(prefix + "gets-nothing-more-of-the-commit-once-it-throws-on-the-retirement-and-pins-nothing-for-the-recreation", async () =>
        {
            var (storage, session, conversationId, recreate) = await Retiring();
            var bug = new InvalidOperationException("observer bug");
            var changes = new SyncList<string>();
            var ends = new SyncList<WatchEnd>();
            var observation = (await session.ObserveDocAsync(ConversationCounter, conversationId, new Observer<JsonObjectValue>((call, _) =>
            {
                changes.Add(Describe(call, storage.Adopting));
                throw bug;
            }, ends.Add), Context))!;
            await recreate();
            Seq(["""advance {"count":1} [] retired=true adopting=false"""], changes.Snapshot(), "changes");
            Seq([WatchEnd.ListenerError(bug)], ends.Snapshot(), "ends");
            Seq([(bug, ReportInfo.ListenerError)], session.Reports.Snapshot(), "reports");
            Equal(0, session.DocumentCacheSizes.Attached, "attached");
            observation.Detach();
            Equal(0, session.DocumentCacheSizes.Attached, "attached after detach");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "gets-nothing-more-of-the-commit-once-it-detaches-in-the-retirement-call-and-pins-nothing", async () =>
        {
            var (storage, session, conversationId, recreate) = await Retiring();
            var changes = new SyncList<string>();
            var ends = new SyncList<WatchEnd>();
            Observation<JsonObjectValue>? observation = null;
            observation = await session.ObserveDocAsync(ConversationCounter, conversationId, new Observer<JsonObjectValue>((call, _) =>
            {
                changes.Add(Describe(call, storage.Adopting));
                Volatile.Read(ref observation)?.Detach();
            }, ends.Add), Context);
            await recreate();
            Seq(["""advance {"count":1} [] retired=true adopting=false"""], changes.Snapshot(), "changes");
            Equal(0, session.DocumentCacheSizes.Attached, "attached");
            await session.CloseAsync(Context);
            Seq([], ends.Snapshot(), "ends");
        });

        yield return Case(prefix + "gets-nothing-of-the-commit-when-another-observers-call-detached-it-first-and-pins-nothing", async () =>
        {
            var (storage, session, conversationId, recreate) = await Retiring();
            Observation<JsonObjectValue>? second = null;
            var first = (await session.ObserveDocAsync(ConversationCounter, conversationId,
                new Observer<JsonObjectValue>((_, _) => Volatile.Read(ref second)?.Detach(), _ => { }), Context))!;
            var detached = new Recorder(storage);
            second = await session.ObserveDocAsync(ConversationCounter, conversationId, detached.Observer, Context);
            await recreate();
            Seq([], detached.Calls.Snapshot(), "calls");
            // Only the first observer pins the new incarnation; its detach leaves nothing pinned.
            first.Detach();
            Equal(0, session.DocumentCacheSizes.Attached, "attached");
            await session.CloseAsync(Context);
        });
    }

    /// <summary>Runs an attachment that must fault its task rather than throw (upstream's "rejects, never throws").</summary>
    private static Task Attach(Func<Task> attach, string what)
    {
        try
        {
            return attach();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{what}: threw instead of faulting its task", error);
        }
    }

    /// <summary>Attaches an observer only the Session references; <paramref name="detach"/> says whether to detach it before returning
    /// (upstream <c>attach</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Observation<JsonObjectValue> Observation, WeakReference Observer)> AttachUnreferenced(Session session, bool detach)
    {
        var observer = new Observer<JsonObjectValue>((_, _) => { }, _ => { });
        var observation = (await session.ObserveDocAsync(SessionCounter, observer, Context))!;
        if (detach) observation.Detach();
        return (observation, new WeakReference(observer));
    }

    /// <summary>Attaches an observer that keeps nothing, changes the document and retires it: only a weak reference to its last value
    /// (upstream <c>retire</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Observation<JsonObjectValue> Observation, WeakReference Last)> RetireObserved(Session session, ConversationId conversationId)
    {
        var last = new StrongBox<WeakReference?>();
        var observation = (await session.ObserveDocAsync(ConversationCounter, conversationId, new Observer<JsonObjectValue>((call, _) =>
        {
            var (value, _, retired) = Change(call);
            if (!retired) last.Value = new WeakReference(value);
        }, _ => { }), Context))!;
        await SetConversationCount(session, conversationId, 7);
        await session.CommitAsync(tx => tx.RetireDocAsync(ConversationCounter, conversationId), Context);
        await session.UnloadDocumentsAsync();
        return (observation, last.Value!);
    }

    /// <summary>Upstream's <c>collect()</c> (SessionWatchesCases.cs).</summary>
    private static async Task CollectGarbage()
    {
        for (var round = 0; round < 5; round++)
        {
            await Task.Delay(1);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }
}
