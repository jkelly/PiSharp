// Pi 6c2fd73b0c545a4034672cb60e34d53e035dd751 (MIT): packages/durable/test/session-deferred-commits.test.ts, new in Pi v1.5.0 (sync area
// deferred-commits). Deferred commits (CommitPersistence): what a deferred commit holds in memory, what stores it, and what Storage and each
// kind of listener then sees. Verbatim apart from the adaptations in SessionSupport.cs, and these:
// - Upstream's ControlledStorage extends MemoryStorage; here it decorates one (session.deferred-commits.* runs on MemoryStorage, as
//   upstream's file does), kept open for reopen. "... closes Storage when that store fails" counts the Session's close of it
//   (ControlledStorage.CloseCount) where upstream's backend refuses calls until reopened.
// - A held commit's withinMs of the wrong value is an ArgumentOutOfRangeException where upstream throws a TypeError; there is no
//   wrong-type counterpart.
// - `+=` on a string member is DraftObject.AppendString, which Change.Prepare turns into an append op as upstream's tracker does.
// - flush() is Flush(session) (see SessionSupport.cs); readOnLine(async () => {}) is ReadOnLineAsync.
// - "retains a held revision only until it is stored" uses WeakReference and GC.Collect in place of WeakRef and --expose-gc, taking the
//   reference in a method of its own so no local keeps the revision.
// The v1.5.0 blob (7de47e659) differs only by later areas: observers renames subscribeCommits to observeCommits and the internal
// observeCommits to followCommits (here SubscribeCommits and SessionTests.ObserveCommits, upstream 6c2fd73b0's names), and vocabulary
// (5f312dac4) spells a Session end's reason and a document change as kinds (here SessionEnd.Reason and DocumentCommitChange).
using System.Runtime.CompilerServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionDeferredCommitsCases() => DeferredCommitsTests.Cases("session.deferred-commits.");
}

internal static class DeferredCommitsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static bool Flag(JsonObjectValue value, string key) => value.TryGetValue(key, out var flag) && flag is JsonBooleanValue { Value: true };

    /// <summary>A latest, initial-fork conversation document, the kind a deferred commit may hold.</summary>
    private static readonly ConversationDocToken TextDoc = Documents.DefineConversationDoc("test.deferred.text", 1, DocumentFork.Initial,
        () => O("""{"text":""}"""), checkpointWhen: (value, _, _) =>
        {
            if (Flag(value, "explode")) throw new InvalidOperationException("checkpoint failed");
            return Flag(value, "base");
        });

    /// <summary><see cref="TextDoc"/> at version 2, which a reload migrates to.</summary>
    private static readonly ConversationDocToken TextDocV2 = Documents.DefineConversationDoc("test.deferred.text", 2, DocumentFork.Initial,
        () => O("""{"text":"","migrated":true}"""), checkpointWhen: (_, _, _) => false,
        // The migration gets the older version's value: version 1's text.
        migrate: (value, _) => value.SetItem("migrated", true));

    private static readonly RewindableConversationDocToken RewindableDoc = Documents.DefineRewindableConversationDoc("test.deferred.rewindable", 1,
        DocumentFork.AsOf, () => O("""{"text":""}"""), checkpointWhen: (_, _, _) => false);

    private static readonly ConversationDocToken CurrentForkDoc = Documents.DefineConversationDoc("test.deferred.current-fork", 1, DocumentFork.Current,
        () => O("""{"text":""}"""), checkpointWhen: (_, _, _) => false);

    private static readonly SessionDocToken SessionTextDoc = Documents.DefineSessionDoc("test.deferred.session", 1, () => O("""{"text":""}"""),
        checkpointWhen: (_, _, _) => false);

    /// <summary>Chord's <c>MAX_PENDING_OPS</c>: held operations past which a store writes the whole value.</summary>
    private const int MaxPendingOps = 10_000;

    private static readonly CommitOptions Deferred = new(CommitPersistence.Deferred());
    private static readonly CommitOptions Immediate = new(CommitPersistence.Immediate);
    private static CommitOptions Within(double withinMs) => new(CommitPersistence.Deferred(withinMs));

    /// <summary>One foreground timer the Session armed.</summary>
    private sealed class Armed(Action callback, double delayMs)
    {
        public Action Callback { get; } = callback;
        public double DelayMs { get; } = delayMs;
        public bool Disarmed;
        public bool Fired;
    }

    private sealed class Clock
    {
        public double Now = 1_000;
    }

    /// <summary>What <see cref="OpenAsync"/> returns (upstream <c>open()</c>'s object).</summary>
    private sealed class Opened(Clock clock, ControlledStorage storage, Session session, List<CommitPublication> stored, List<ObservedCommit> observed,
        List<Armed> timers)
    {
        public Clock Clock { get; } = clock;
        public ControlledStorage Storage { get; } = storage;
        public Session Session { get; } = session;
        public List<CommitPublication> Stored { get; } = stored;
        public List<ObservedCommit> Observed { get; } = observed;
        public ConversationId ConversationId { get; set; }
        public DocumentId RecordId { get; set; }

        /// <summary>Timers armed and neither disarmed nor fired.</summary>
        public List<double> Armed()
        {
            lock (timers) return [.. timers.Where(timer => !timer.Disarmed && !timer.Fired).Select(timer => timer.DelayMs)];
        }

        /// <summary>Fire every armed timer and let the line run what they queued.</summary>
        public async Task Fire()
        {
            List<Armed> due;
            lock (timers)
            {
                due = [.. timers.Where(timer => !timer.Disarmed && !timer.Fired)];
                foreach (var timer in due) timer.Fired = true;
            }
            foreach (var timer in due) timer.Callback();
            await Session.ReadOnLineAsync(() => Task.FromResult(true));
        }

        /// <summary>Batches admitted to Storage from here on.</summary>
        public Func<List<IReadOnlyList<StorageWrite>>> Mark()
        {
            var from = Storage.CommitCount;
            return () => [.. Storage.Commits.Skip(from).Select(batch => (IReadOnlyList<StorageWrite>)batch)];
        }

        public async Task<JsonObjectValue?> StoredText(DocumentId? id = null) =>
            (await Storage.GetDocumentAsync(id ?? RecordId, DocumentPoint.Current, Context))?.Value;

        public (int Cached, int Idle, int Attached, int Sweeps, int Held) Sizes() => Session.DocumentCacheSizes;
    }

    /// <summary>
    /// A Session over controlled storage with a manual clock and manual foreground timers (background ones are declined, so commits evict),
    /// recording what <c>SubscribeCommits()</c> and the Session's own observers get.
    /// </summary>
    private static async Task<Opened> OpenAsync(double? retentionMs = null, double? persistWithinMs = null)
    {
        var clock = new Clock();
        var timers = new List<Armed>();
        SetTimer setTimer = (callback, delayMs, options) =>
        {
            if (options.Background) return null;
            var timer = new Armed(callback, delayMs);
            lock (timers) timers.Add(timer);
            return () =>
            {
                lock (timers) timer.Disarmed = true;
            };
        };
        var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
        var session = new Session(storage, () => clock.Now, () => retentionMs ?? 600_000, setTimer: setTimer, persistWithinMs: () => persistWithinMs);
        var stored = new List<CommitPublication>();
        session.SubscribeCommits((publication, _) =>
        {
            lock (stored) stored.Add(publication);
        });
        var observed = new List<ObservedCommit>();
        ObserveCommits(session, (publication, _) =>
        {
            lock (observed) observed.Add(publication);
        });
        var opened = new Opened(clock, storage, session, stored, observed, timers);
        opened.ConversationId = await CreateConversation(session);
        await session.CommitAsync(async tx => await tx.DocAsync(TextDoc, opened.ConversationId), Context);
        opened.RecordId = (await storage.FindDocumentAsync(Address(opened.ConversationId), DocumentPoint.Current, Context))!.Id;
        return opened;
    }

    private static DocumentAddress Address(ConversationId conversationId) => new("test.deferred.text", DocumentScope.Conversation(conversationId));

    private static Task Append(Session session, ConversationId conversationId, string text, CommitOptions? options = null) =>
        session.CommitAsync(async tx => (await tx.DocAsync(TextDoc, conversationId)).AppendString("text", text), Context, options ?? Deferred);

    private static List<DocumentChangeWrite> ChangesOf(IEnumerable<IReadOnlyList<StorageWrite>> batches) =>
        [.. batches.SelectMany(batch => batch).OfType<DocumentChangeWrite>()];

    private static string ContentsJson(IEnumerable<DocumentChangeWrite> writes) =>
        JsonValue.Array(writes.Select(write => (JsonValue)write.Content.ToJson())).ToJson();

    private static string Kinds(IEnumerable<DocumentChangeWrite> writes) => string.Join(",", writes.Select(write => write.Content is DocumentBase ? "base" : "delta"));

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list) return [.. list];
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ─── holding a deferred commit ──────────────────────────────────────

        yield return Case(prefix + "adopts-a-commit-that-only-changes-an-existing-latest-initial-fork-conversation-document-in-memory-unstored", async () =>
        {
            var opened = await OpenAsync();
            var (session, storage, conversationId) = (opened.Session, opened.Storage, opened.ConversationId);
            var admitted = storage.CommitCount;
            var publications = Snapshot(opened.Stored).Count;
            await Append(session, conversationId, "a");
            await Append(session, conversationId, "b");
            Equal(admitted, storage.CommitCount, "admitted");
            Equal(publications, Snapshot(opened.Stored).Count, "stored publications");
            SameJson("""{"text":"ab"}""", await session.SnapshotAsync(TextDoc, conversationId, Context), "snapshot");
            SameJson("""{"text":""}""", await opened.StoredText(), "stored");
            Equal(1, opened.Sizes().Held, "held");
            Equal("memory,memory", string.Join(",", Snapshot(opened.Observed).TakeLast(2).Select(publication => publication.IsMemory ? "memory" : "stored")),
                "observed kinds");
        });

        yield return Case(prefix + "stores-a-deferred-commit-at-once-when-it-writes-anything-else", async () =>
        {
            var opened = await OpenAsync();
            var (session, storage, conversationId) = (opened.Session, opened.Storage, opened.ConversationId);
            async Task<bool> Stores(Func<ITx, Task> change)
            {
                var admitted = storage.CommitCount;
                await session.CommitAsync(change, Context, Deferred);
                return storage.CommitCount > admitted;
            }
            // An entry next to the held document's change.
            Check(await Stores(async tx =>
            {
                (await tx.DocAsync(TextDoc, conversationId)).AppendString("text", "x");
                await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "test.note" });
            }), "entry");
            // Creation, then other document semantics.
            Check(await Stores(async tx => await tx.DocAsync(RewindableDoc, conversationId)), "rewindable creation");
            Check(await Stores(async tx => (await tx.DocAsync(RewindableDoc, conversationId)).Set("text", "r")), "rewindable change");
            Check(await Stores(async tx => await tx.DocAsync(CurrentForkDoc, conversationId)), "current-fork creation");
            Check(await Stores(async tx => (await tx.DocAsync(CurrentForkDoc, conversationId)).Set("text", "c")), "current-fork change");
            Check(await Stores(async tx => await tx.DocAsync(SessionTextDoc)), "session creation");
            Check(await Stores(async tx => (await tx.DocAsync(SessionTextDoc)).Set("text", "s")), "session change");
            // A held document next to an immediate one: the whole commit is stored.
            Check(await Stores(async tx =>
            {
                (await tx.DocAsync(TextDoc, conversationId)).AppendString("text", "y");
                (await tx.DocAsync(SessionTextDoc)).AppendString("text", "s");
            }), "held next to immediate");
            // Retirement.
            Check(await Stores(tx => tx.RetireDocAsync(CurrentForkDoc, conversationId)), "retirement");
            Equal(0, opened.Sizes().Held, "held");
            // The control: the same change alone is held.
            Check(!await Stores(async tx => (await tx.DocAsync(TextDoc, conversationId)).AppendString("text", "z")), "control is held");
        });

        yield return Case(prefix + "stores-a-deferred-commit-that-migrates-its-document-at-once-as-a-base", async () =>
        {
            var opened = await OpenAsync();
            var batches = opened.Mark();
            await opened.Session.CommitAsync(async tx => (await tx.DocAsync(TextDocV2, opened.ConversationId)).Set("text", "v2"), Context, Deferred);
            Equal("base", Kinds(ChangesOf(batches())), "kinds");
            Equal(0, opened.Sizes().Held, "held");
        });

        yield return Case(prefix + "rejects-a-deferred-commit-on-a-read-only-session-like-any-commit-with-writes", async () =>
        {
            var opened = await OpenAsync();
            var viewer = new Session(opened.Storage, null, () => 600_000, readOnly: true);
            try
            {
                await Append(viewer, opened.ConversationId, "a");
                throw new InvalidOperationException("expected a ReadOnlySessionException");
            }
            catch (ReadOnlySessionException) { }
        });

        yield return Case(prefix + "rejects-a-withinms-that-is-not-a-number-of-milliseconds-0-or-more", async () =>
        {
            var opened = await OpenAsync();
            foreach (var within in new[] { -1, double.NaN })
            {
                try
                {
                    await Append(opened.Session, opened.ConversationId, "a", Within(within));
                    throw new InvalidOperationException($"expected an ArgumentOutOfRangeException for {within}");
                }
                catch (ArgumentOutOfRangeException) { }
            }
            await Append(opened.Session, opened.ConversationId, "a", new CommitOptions(CommitPersistence.Immediate));
        });

        // ─── storing held changes ───────────────────────────────────────────

        yield return Case(prefix + "stores-them-first-folded-with-its-own-in-a-commit-that-writes-the-document", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "a");
            await Append(opened.Session, opened.ConversationId, "b");
            var batches = opened.Mark();
            await Append(opened.Session, opened.ConversationId, "c", Immediate);
            Equal(1, batches().Count, "batches");
            SameJson("""[{"version":1,"kind":"delta","ops":[["a",["text"],"abc"]]}]""", J(ContentsJson(ChangesOf(batches()))), "contents");
            SameJson("""{"text":"abc"}""", await opened.StoredText(), "stored");
            Equal(0, opened.Sizes().Held, "held");
        });

        yield return Case(prefix + "stores-them-in-a-commit-that-only-reads-the-document-with-its-other-writes", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "held");
            var batches = opened.Mark();
            await opened.Session.CommitAsync(async tx =>
            {
                var text = S((await tx.DocAsync(TextDoc, opened.ConversationId)).Get("text"));
                await tx.AppendEntryAsync(opened.ConversationId, new EntryDraft { Kind = "test.derived", Data = text });
            }, Context);
            Equal(1, batches().Count, "batches");
            Equal("document.change,entry", string.Join(",", batches()[0].Select(write => write.Type).Order(StringComparer.Ordinal)), "types");
            SameJson("""{"text":"held"}""", await opened.StoredText(), "stored");
        });

        yield return Case(prefix + "leaves-them-held-through-commits-that-neither-read-nor-write-the-document-or-write-nothing", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "held");
            var batches = opened.Mark();
            await opened.Session.CommitAsync(async tx => await tx.AppendEntryAsync(opened.ConversationId, new EntryDraft { Kind = "test.unrelated" }), Context);
            await opened.Session.CommitAsync(async tx => await tx.DocAsync(TextDoc, opened.ConversationId), Context);
            Equal(0, ChangesOf(batches()).Count, "document changes");
            Equal(1, opened.Sizes().Held, "held");
        });

        yield return Case(prefix + "keeps-them-when-a-commit-that-reads-the-document-fails-before-storage", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "held");
            await RejectsAsync(() => opened.Session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(TextDoc, opened.ConversationId)).AppendString("text", "!");
                await tx.AppendEntryAsync(opened.ConversationId, new EntryDraft { Kind = "test.note" });
                throw new InvalidOperationException("callback failed");
            }, Context), "callback failed", "callback");
            // Its checkpoint predicate throws: settlement fails after every other step.
            await RejectsAsync(() => opened.Session.CommitAsync(async tx => (await tx.DocAsync(TextDoc, opened.ConversationId)).Set("explode", true), Context),
                "checkpoint failed", "settlement");
            Equal(1, opened.Sizes().Held, "held");
            await opened.Fire();
            SameJson("""{"text":"held"}""", await opened.StoredText(), "stored");
        });

        yield return Case(prefix + "stores-every-held-document-in-one-transaction-when-the-earliest-deadline-comes", async () =>
        {
            var opened = await OpenAsync();
            var session = opened.Session;
            var other = await CreateConversation(session);
            await session.CommitAsync(async tx => await tx.DocAsync(TextDoc, other), Context);
            await Append(session, opened.ConversationId, "one");
            await Append(session, other, "two");
            Seq([1_000.0], opened.Armed(), "armed");
            var publications = Snapshot(opened.Stored).Count;
            var batches = opened.Mark();
            await opened.Fire();
            Equal(1, batches().Count, "batches");
            Equal(2, ChangesOf(batches()).Count, "document changes");
            Equal(publications + 1, Snapshot(opened.Stored).Count, "stored publications");
            Equal(0, opened.Sizes().Held, "held");
            Seq([], opened.Armed(), "armed after");
            var otherId = (await opened.Storage.FindDocumentAsync(Address(other), DocumentPoint.Current, Context))!.Id;
            SameJson("""{"text":"two"}""", await opened.StoredText(otherId), "other stored");
        });

        yield return Case(prefix + "arms-the-earliest-deadline-of-the-held-commits-each-with-its-withinms-or-the-default", async () =>
        {
            var opened = await OpenAsync(persistWithinMs: 2_000);
            await Append(opened.Session, opened.ConversationId, "a");
            Seq([2_000.0], opened.Armed(), "default");
            opened.Clock.Now += 500;
            // Earlier: the timer moves up.
            await Append(opened.Session, opened.ConversationId, "b", Within(100));
            Seq([100.0], opened.Armed(), "earlier");
            // Later: the deadline stays.
            await Append(opened.Session, opened.ConversationId, "c", Within(60_000));
            Seq([100.0], opened.Armed(), "later");
        });

        yield return Case(prefix + "arms-no-timer-for-an-infinite-window-and-moves-the-timer-on-when-a-commit-stores-the-held-document", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "a", Within(double.PositiveInfinity));
            Seq([], opened.Armed(), "infinite");
            Equal(1, opened.Sizes().Held, "held");
            await Append(opened.Session, opened.ConversationId, "b");
            Seq([1_000.0], opened.Armed(), "default");
            await Append(opened.Session, opened.ConversationId, "c", Immediate);
            Seq([], opened.Armed(), "stored");
            Equal(0, opened.Sizes().Held, "held after");
        });

        yield return Case(prefix + "stores-them-at-close-after-the-commits-admitted-before-it-and-closes-storage-when-that-store-fails", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "held");
            await opened.Session.CloseAsync(Context);
            SameJson("""{"text":"held"}""", (await opened.Storage.Reopen().GetDocumentAsync(opened.RecordId, DocumentPoint.Current, Context))?.Value, "stored at close");

            var failing = await OpenAsync();
            await Append(failing.Session, failing.ConversationId, "lost");
            var failure = new InvalidOperationException("disk gone");
            failing.Storage.FailNextCommit(failure);
            try
            {
                await failing.Session.CloseAsync(Context);
                throw new InvalidOperationException("expected the close to reject");
            }
            catch (Exception error) when (!ReferenceEquals(error.Message, "expected the close to reject"))
            {
                Same(failure, error, "close rejects with the store's failure");
            }
            var end = await failing.Session.Closed;
            Equal(SessionEndReason.Failed, end.Reason, "closed reason");
            Same(failure, end.Error, "closed error");
            // Closed: the Session closed the backend all the same.
            Equal(1, failing.Storage.CloseCount, "backend closes");
            SameJson("""{"text":""}""", (await failing.Storage.Reopen().GetDocumentAsync(failing.RecordId, DocumentPoint.Current, Context))?.Value,
                "nothing stored");
        });

        yield return Case(prefix + "stores-nothing-once-the-session-failed", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "lost");
            var batches = opened.Mark();
            opened.Session.Fail(new InvalidOperationException("crash"));
            await opened.Session.Closed;
            Equal(0, batches().Count, "batches");
            SameJson("""{"text":""}""", (await opened.Storage.Reopen().GetDocumentAsync(opened.RecordId, DocumentPoint.Current, Context))?.Value, "stored");
        });

        yield return Case(prefix + "stores-them-before-unloaddocuments-drops-the-trackers", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "held");
            await opened.Session.UnloadDocumentsAsync();
            SameJson("""{"text":"held"}""", await opened.StoredText(), "stored");
            var sizes = opened.Sizes();
            Equal((0, 0), (sizes.Cached, sizes.Held), "cached and held");
            SameJson("""{"text":"held"}""", await opened.Session.SnapshotAsync(TextDoc, opened.ConversationId, Context), "snapshot");
        });

        yield return Case(prefix + "stores-them-before-a-token-of-another-definition-version-reloads-the-document", async () =>
        {
            var opened = await OpenAsync();
            var session = opened.Session;
            await Append(session, opened.ConversationId, "held");
            var batches = opened.Mark();
            SameJson("""{"text":"held","migrated":true}""", await session.SnapshotAsync(TextDocV2, opened.ConversationId, Context), "migrated snapshot");
            Equal("delta", Kinds(ChangesOf(batches())), "kinds");
            SameJson("""{"text":"held"}""", await opened.StoredText(), "stored");
            // The same inside a commit, which then migrates with a base.
            await session.CommitAsync(async tx => await tx.DocAsync(TextDoc, opened.ConversationId), Context);
            await Append(session, opened.ConversationId, "+");
            await session.CommitAsync(async tx => (await tx.DocAsync(TextDocV2, opened.ConversationId)).AppendString("text", "v2"), Context);
            SameJson("""{"text":"held+v2","migrated":true}""", await opened.StoredText(), "stored after the commit");
        });

        yield return Case(prefix + "stores-them-before-the-documents-retirement-in-the-retiring-commit", async () =>
        {
            var opened = await OpenAsync();
            await Append(opened.Session, opened.ConversationId, "held");
            var batches = opened.Mark();
            await opened.Session.CommitAsync(tx => tx.RetireDocAsync(TextDoc, opened.ConversationId), Context);
            Equal("document.change,document.retire", string.Join(",", batches()[0].Select(write => write.Type)), "types");
            SameJson("""{"version":1,"kind":"delta","ops":[["a",["text"],"held"]]}""", ChangesOf(batches())[0].Content.ToJson(), "content");
            Equal(0, opened.Sizes().Held, "held");
            Check(await opened.Session.SnapshotAsync(TextDoc, opened.ConversationId, Context) is null, "retired");
        });

        // ─── bases and overflow ─────────────────────────────────────────────

        yield return Case(prefix + "stores-a-base-when-a-held-commit-selected-one-and-counts-the-store-as-one-delta-otherwise", async () =>
        {
            var opened = await OpenAsync();
            var batches = opened.Mark();
            await opened.Session.CommitAsync(async tx =>
            {
                var doc = await tx.DocAsync(TextDoc, opened.ConversationId);
                doc.Set("text", "x");
                doc.Set("base", true);
            }, Context, Deferred);
            await Append(opened.Session, opened.ConversationId, "y");
            await opened.Fire();
            SameJson("""[{"version":1,"kind":"base","value":{"text":"xy","base":true}}]""", J(ContentsJson(ChangesOf(batches()))), "contents");
        });

        yield return Case(prefix + "stores-the-whole-value-once-more-than-max-pending-ops-operations-are-held", async () =>
        {
            var opened = await OpenAsync();
            await Overflow(opened.Session, opened.ConversationId);
            var batches = opened.Mark();
            await opened.Fire();
            var value = (await opened.Session.SnapshotAsync(TextDoc, opened.ConversationId, Context))!;
            var contents = ChangesOf(batches());
            Check(contents is [{ Content: DocumentBase { Version: 1 } stored }] && JsonValue.DeepEquals(value, stored.Value), "one base of the value");
            // A durable listener replaces its copy with the stored value.
            var last = Snapshot(opened.Stored)[^1];
            Check(last.Changes is [DocumentCommitChange { Ops: [ReplaceOp replace] }] && JsonValue.DeepEquals(value, replace.Value),
                "the last publication replaces the value: " + string.Join(" ", last.Changes.Select(change => change.Type)));
        });

        yield return Case(prefix + "stores-a-base-of-the-commits-own-value-when-it-stores-an-overflowed-hold", async () =>
        {
            var opened = await OpenAsync();
            await Overflow(opened.Session, opened.ConversationId);
            await Append(opened.Session, opened.ConversationId, "a");
            var batches = opened.Mark();
            await Append(opened.Session, opened.ConversationId, "b", Immediate);
            var value = (await opened.Session.SnapshotAsync(TextDoc, opened.ConversationId, Context))!;
            Equal("ab", S(value["text"]), "text");
            Check(ChangesOf(batches()) is [{ Content: DocumentBase { Version: 1 } stored }] && JsonValue.DeepEquals(value, stored.Value), "one base of the value");
        });

        // ─── publications ───────────────────────────────────────────────────

        yield return Case(prefix + "give-subscribecommits-exactly-what-was-stored-and-nothing-for-a-held-commit", async () =>
        {
            var opened = await OpenAsync();
            var (session, storage, conversationId, recordId) = (opened.Session, opened.Storage, opened.ConversationId, opened.RecordId);
            var from = storage.CommitCount;
            var publications = Snapshot(opened.Stored).Count;
            await Append(session, conversationId, "a");
            await Append(session, conversationId, "b");
            await Append(session, conversationId, "c", Immediate);
            await Append(session, conversationId, "d");
            await opened.Fire();
            await Append(session, conversationId, "e");
            await session.CommitAsync(async tx =>
            {
                await tx.DocAsync(TextDoc, conversationId);
                await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "test.note" });
            }, Context);
            await Append(session, conversationId, "f");
            await session.CloseAsync(Context);
            var batches = storage.Commits.Skip(from).ToList();
            var published = Snapshot(opened.Stored).Skip(publications).ToList();
            Equal(batches.Count, published.Count, "one publication per stored batch");
            // A replica built from the publications alone always equals what Storage holds.
            JsonValue fromStorage = O("""{"text":""}""");
            JsonValue fromPublications = O("""{"text":""}""");
            for (var index = 0; index < batches.Count; index++)
            {
                var write = ChangesOf([batches[index]]).First(candidate => candidate.Id == recordId);
                var change = published[index].Changes.OfType<DocumentCommitChange>().First(candidate => candidate.Record.Id == recordId);
                fromStorage = write.Content is DocumentBase stored ? stored.Value : DeltaApply.Apply(fromStorage, ((DocumentDelta)write.Content).Ops);
                fromPublications = DeltaApply.Apply(fromPublications, change.Ops);
                Check(JsonValue.DeepEquals(fromStorage, fromPublications), $"replica {index}: {fromPublications.ToJson()} vs {fromStorage.ToJson()}");
                Check(JsonValue.DeepEquals(fromStorage, change.Value), $"value {index}");
            }
            SameJson("""{"text":"abcdef"}""", fromStorage, "final");
            Equal("document|document|document,entry|document",
                string.Join("|", published.Select(publication => string.Join(",", publication.Changes.Select(change => change.Type).Order(StringComparer.Ordinal)))),
                "change types");
        });

        yield return Case(prefix + "show-every-held-commit-to-watches-and-the-sessions-own-observers-once", async () =>
        {
            var opened = await OpenAsync();
            var session = opened.Session;
            var watch = (await session.WatchDocAsync(TextDoc, opened.ConversationId, Context))!;
            var values = new List<string>();
            watch.Start((call, _) =>
            {
                lock (values) values.Add(S(Change(call).Value["text"]));
                return Task.CompletedTask;
            });
            var from = Snapshot(opened.Observed).Count;
            foreach (var text in new[] { "a", "b", "c" })
            {
                await Append(session, opened.ConversationId, text);
                await Flush(session);
            }
            await Append(session, opened.ConversationId, "d", Immediate);
            await Flush(session);
            await opened.Fire();
            Equal("a,ab,abc,abcd", string.Join(",", Snapshot(values)), "values");
            var seen = Snapshot(opened.Observed).Skip(from).ToList();
            Equal("memory,memory,memory,stored", string.Join(",", seen.Select(publication => publication.IsMemory ? "memory" : "stored")), "kinds");
            // The storing commit shows its own operations; the held ones were shown already. The timer showed nothing.
            Equal("""[[[["a",["text"],"a"]]],[[["a",["text"],"b"]]],[[["a",["text"],"c"]]],[[["a",["text"],"d"]]]]""",
                JsonValue.Array(seen.Select(publication => (JsonValue)JsonValue.Array(publication.Changes.Select(change =>
                    change is DocumentCommitChange document ? J(OpsJson(document.Ops)) : (JsonValue)false)))).ToJson(), "ops");
            await watch.StopAsync();
        });

        // ─── pinning ────────────────────────────────────────────────────────

        yield return Case(prefix + "keeps-a-held-document-cached-with-retention-0-until-it-is-stored-then-lets-it-go", async () =>
        {
            var opened = await OpenAsync(retentionMs: 0);
            var session = opened.Session;
            await session.ReadOnLineAsync(() => Task.FromResult(true));
            static (int, int, int) Of((int Cached, int Idle, int Attached, int Sweeps, int Held) sizes) => (sizes.Cached, sizes.Held, sizes.Attached);
            Equal((0, 0, 0), Of(opened.Sizes()), "before");
            await Append(session, opened.ConversationId, "held");
            await session.ReadOnLineAsync(() => Task.FromResult(true));
            Equal((1, 1, 1), Of(opened.Sizes()), "held");
            await opened.Fire();
            await session.CommitAsync(_ => Task.CompletedTask, Context);
            Equal((0, 0, 0), Of(opened.Sizes()), "stored");
            SameJson("""{"text":"held"}""", await session.SnapshotAsync(TextDoc, opened.ConversationId, Context), "snapshot");
        });

        yield return Case(prefix + "retains-a-held-revision-only-until-it-is-stored", async () =>
        {
            var opened = await OpenAsync(retentionMs: 0);
            var session = opened.Session;
            await Append(session, opened.ConversationId, new string('x', 1_000));
            var revision = await WeakSnapshot(session, opened.ConversationId);
            await Collect();
            Check(revision.IsAlive, "held: retained");
            await opened.Fire();
            await session.CommitAsync(_ => Task.CompletedTask, Context);
            // The recorded publications would keep it.
            lock (opened.Stored) opened.Stored.Clear();
            lock (opened.Observed) opened.Observed.Clear();
            await Collect();
            Check(!revision.IsAlive, "stored: released");
        });

        // ─── crash ──────────────────────────────────────────────────────────

        yield return Case(prefix + "leaves-storage-at-the-last-stored-state-which-memory-had-and-nothing-held-after-reopen", async () =>
        {
            var opened = await OpenAsync();
            var session = opened.Session;
            var states = new List<string>();
            ObserveCommits(session, (publication, _) =>
            {
                foreach (var change in publication.Changes)
                    if (change is DocumentCommitChange document && document.Record.Id == opened.RecordId) lock (states) states.Add(S(document.Value!["text"]));
            });
            await Append(session, opened.ConversationId, "a");
            await opened.Fire();
            await Append(session, opened.ConversationId, "b");
            await Append(session, opened.ConversationId, "c");
            session.Fail(new InvalidOperationException("crash"));
            await session.Closed;
            var reopened = new Session(opened.Storage.Reopen(), null, () => 600_000);
            var value = (await reopened.SnapshotAsync(TextDoc, opened.ConversationId, Context))!;
            SameJson("""{"text":"a"}""", value, "reopened");
            Check(Snapshot(states).Contains(S(value["text"])), "memory had the stored state");
            Equal(0, reopened.DocumentCacheSizes.Held, "held after reopen");
            await reopened.CloseAsync(Context);
        });
    }

    /// <summary>
    /// Hold more than <see cref="MaxPendingOps"/> operations: deferred commits of one set per key, each below the tracker's own limit, past
    /// which a commit is one root replacement.
    /// </summary>
    private static async Task Overflow(Session session, ConversationId conversationId)
    {
        await session.CommitAsync(async tx => (await tx.DocAsync(TextDoc, conversationId)).Set("keys", JsonObjectValue.Empty), Context);
        for (var from = 0; from <= MaxPendingOps; from += 4_000)
        {
            var start = from;
            await session.CommitAsync(async tx =>
            {
                var keys = (await tx.DocAsync(TextDoc, conversationId)).GetObject("keys");
                for (var index = start; index < start + 4_000; index++) keys.Set($"k{index}", index);
            }, Context, Deferred);
        }
    }

    /// <summary>A weak reference to the document's current revision, taken where no local outlives it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> WeakSnapshot(Session session, ConversationId conversationId) =>
        new((await session.SnapshotAsync(TextDoc, conversationId, Context))!);

    /// <summary>Upstream's collect(): five rounds of a turn and a full collection.</summary>
    private static async Task Collect()
    {
        for (var round = 0; round < 5; round++)
        {
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }
}
