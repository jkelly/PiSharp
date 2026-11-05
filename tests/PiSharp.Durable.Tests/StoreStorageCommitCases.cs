// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/store-storage-commit.test.ts
//
// StoreStorage's commit path (IStorage.CommitAsync over ChordStore.CommitSeriesAsync), p3's primary storage: per call one backend
// transaction with one seq per batch; Durable's checks of a batch refuse it in its turn, the batches before it stored; a failed statement
// rolls the call back and rejects, the store going on; a failed COMMIT stops the store; receipts after COMMIT; other users of the store wait
// their turn. The counted backend (CountedStore) wraps the store's Chord ISqliteDatabase as upstream's wraps its SqliteDatabase: it counts
// transactions and the statements of the last one, and takes faults for the next one. Adapted:
// - writes-the-metadata-once-per-call: the statements are the counted backend's of the call's transaction, not the chord.store.statements
//   metric (PiSharp's DurableContext has no telemetry span to carry it until area telemetry); both count every statement of the commit's
//   transaction. Upstream's first figure (5, an append alone) is the backend's count of that commit's transaction.
// - delays are Task.Delay(10) where upstream waits a macrotask; Promise.withResolvers are TaskCompletionSources.
// - an injected failure's cause chain is the exception's InnerException chain.
// - "publishes each commit of a call in a host batch of its own ..." (Pi v1.5.0 sync, area publication-caches): Chord's host (createHost)
//   is phase 6's, so the host batch is a hand-built one attached to the store, as in StoreStorageCases.cs; the Session publishes through
//   a ControlledStorage decorator (HostPublishing) that forwards IStorage.Publishing to the store's storage and holds the first call. The
//   setTimeout(0) after the later commits is the Session's line tick: their line parts ran, so they wait behind the held call.
// Not ported: "refuses a batch whose rows staging cannot encode" (an id of 1.5: PiSharp's ids are longs, so no such batch can be built).
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.State;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Store;
using PiSharp.Durable.Testing;
using static SessionTests;
using ChordSqliteDatabase = PiSharp.Chord.Sqlite.ISqliteDatabase;
using ISqliteExecutor = PiSharp.Chord.Sqlite.ISqliteExecutor;
using Attachment = PiSharp.Chord.Store.Attachment;
using ChordStore = PiSharp.Chord.Store.ChordStore;
using StoreOptions = PiSharp.Chord.Store.StoreOptions;
using Tables = PiSharp.Chord.Store.Tables;

internal static partial class Program
{
    private static EntryWrite StoreEntryWrite(EntryId id, string kind = "m") =>
        new(new EntryRecord { Id = id, ConversationId = ConversationId.Root, Kind = kind });

    /// <summary>One wait in which what can run before it has run (upstream's macrotask).</summary>
    private static Task Macrotask() => Task.Delay(10);

    /// <summary>The store's outbox commits, by seq.</summary>
    private static async Task<List<long>> OutboxSeqsAsync(ChordStore store, long after = 0)
    {
        var rows = await store.ReadAsync(executor => executor.AllAsync("SELECT seq FROM chord_outbox WHERE seq > ? ORDER BY seq", after), ChordContext.Background);
        return [.. rows.Select(row => StoreValuesOf(row["seq"]))];
    }

    private static long StoreValuesOf(object? value) => value switch { long integer => integer, int small => small, double number => (long)number, _ => -1 };

    /// <summary>The same storage opened again on the file at <paramref name="path"/>, as another process would.</summary>
    private static async Task<(ChordStore Store, StoreStorage Storage)> ReopenStoreAsync(string path)
    {
        var store = (ChordStore)await Tables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(path), ChordContext.Background);
        return (store, await StoreStorage.OpenAsync(store, "harness", StoreContext));
    }

    private static string StorePath(string directory) => Path.Combine(directory, $"store-{Guid.NewGuid():N}.sqlite");

    private static async Task<ChordError> RejectsChordAsync(Func<Task> run, string code, string? reason, string what)
    {
        var error = await ThrowsAsync<ChordError>(run, what);
        Check(error.Code == code && (reason is null || error.Reason == reason), $"{what}: expected {code}/{reason}, actual {error.Code}/{error.Reason}: {error.Message}");
        return error;
    }

    private static IEnumerable<(string, Func<Task>)> StoreStorageCommitCases()
    {
        // ─── one transaction per call ───────────────────────────────────────

        yield return Case("storage.store.commit.stores-the-batches-in-one-backend-transaction-one-seq-and-one-outbox-commit-each-receipts-in-order", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            EntryId[] ids = [await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>()];
            var before = counted.Transactions;
            var last = store.LastSeq;
            var receipts = new List<(int, long)>();
            var result = await storage.CommitAsync([.. ids.Select(id => (IReadOnlyList<StorageWrite>)[StoreEntryWrite(id)])],
                (index, seq) => receipts.Add((index, seq.Value)), StoreContext);
            Equal(new CommitResult(3), result, "result");
            Equal(1, counted.Transactions - before, "transactions");
            SessionTests.Seq([(0, last + 1), (1, last + 2), (2, last + 3)], receipts, "receipts");
            for (var index = 0; index < ids.Length; index++)
                Equal<long?>(last + 1 + index, (await storage.GetEntryAsync(ids[index], StoreContext))?.CommitSeq.Value, $"entry {index}'s seq");
            SessionTests.Seq([last + 1, last + 2, last + 3], await OutboxSeqsAsync(store, last), "outbox");
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commit.writes-the-metadata-once-per-call-three-statements-per-entry-append-plus-one-cost-guard", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            // Alone, an append is its id check, its row and claim, its outbox commit, and the metadata.
            await storage.CommitWritesAsync([StoreEntryWrite(await storage.MintIdAsync<EntryId>())], StoreContext);
            Equal(5L, counted.Statements, "an append alone");
            EntryId[] ids = [await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>()];
            var before = counted.Transactions;
            await storage.CommitAsync([.. ids.Select(id => (IReadOnlyList<StorageWrite>)[StoreEntryWrite(id)])], (_, _) => { }, StoreContext);
            Equal((1, 3L * 4 + 1), (counted.Transactions - before, counted.Statements), "three appends in one call");
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── refusals: the batches before the refused one stand ─────────────

        yield return Case("storage.store.commit.stores-the-batches-before-a-refused-one-with-durables-message-none-after-it-and-goes-on", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            var first = await storage.MintIdAsync<EntryId>();
            var after = await storage.MintIdAsync<EntryId>();
            var last = store.LastSeq;
            var receipts = new List<int>();
            var result = await storage.CommitAsync([[StoreEntryWrite(first)], [StoreEntryWrite(first, "again")], [StoreEntryWrite(after)]],
                (index, _) => receipts.Add(index), StoreContext);
            Equal(1, result.Stored, "stored");
            Equal($"ID {first} already belongs to entry", result.Refused?.Message, "refused");
            SessionTests.Seq([0], receipts, "receipts");
            Equal("m", (await storage.GetEntryAsync(first, StoreContext))?.Entry.Kind, "first entry");
            Equal<CommittedEntry?>(null, await storage.GetEntryAsync(after, StoreContext), "entry after");
            Equal(last + 1, store.LastSeq, "last seq");
            Equal(new Seq(last + 2), await storage.CommitWritesAsync([StoreEntryWrite(after)], StoreContext), "the next commit");
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commit.refuses-a-batch-its-staging-fails-in-that-batchs-turn-keeping-the-batches-before-it", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            var entry = await storage.MintIdAsync<EntryId>();
            var document = await storage.MintIdAsync<DocumentId>();
            var receipts = new List<int>();
            // Staging refuses it: one document retired twice.
            var result = await storage.CommitAsync([[StoreEntryWrite(entry)], [new DocumentRetireWrite(document), new DocumentRetireWrite(document)]],
                (index, _) => receipts.Add(index), StoreContext);
            Equal(1, result.Stored, "stored");
            Equal($"Document {document} is retired more than once", result.Refused?.Message, "refused");
            SessionTests.Seq([0], receipts, "receipts");
            Check(await storage.GetEntryAsync(entry, StoreContext) is not null, "the first batch's entry");
            await store.CloseAsync(ChordContext.Background);
        });

        foreach (var (name, malformed) in (ValueTuple<string, StorageWrite[]>[])
        [
            ("a-duplicate-retirement", [new DocumentRetireWrite(new DocumentId(101)), new DocumentRetireWrite(new DocumentId(101))]),
            ("a-duplicate-content-command",
            [
                new DocumentChangeWrite(new DocumentId(101), DocumentContent.Base(1, O("{}"))),
                new DocumentChangeWrite(new DocumentId(101), DocumentContent.Base(1, O("{}"))),
            ]),
        ])
            yield return Case($"storage.store.commit.refuses-a-later-batch-with-{name}-as-memory-does-keeping-the-prefix", async () =>
            {
                IReadOnlyList<IReadOnlyList<StorageWrite>> batches = [[new ConversationWrite(new ConversationRecord(new ConversationId(100)))], malformed];
                var outcomes = new List<(int Stored, string? Refused, string Receipts, ConversationRecord? Conversation)>();
                foreach (var storage in (IStorage[])[new MemoryStorage(), await OpenStoreStorageAsync(":memory:")])
                {
                    var receipts = new List<int>();
                    var result = await storage.CommitAsync(batches, (index, _) => receipts.Add(index), StoreContext);
                    outcomes.Add((result.Stored, result.Refused?.Message, string.Join(",", receipts),
                        await storage.GetConversationAsync(new ConversationId(100), StoreContext)));
                    await storage.CloseAsync(StoreContext);
                }
                Equal((1, "0", new ConversationRecord(new ConversationId(100))), (outcomes[0].Stored, outcomes[0].Receipts, outcomes[0].Conversation), "Memory");
                Equal(outcomes[0], outcomes[1], "the store's outcome");
            });

        foreach (var (name, calls, message) in (ValueTuple<string, Func<DocumentId, IReadOnlyList<IReadOnlyList<StorageWrite>>>, string>[])
        [
            ("a-change-of-a-document-an-earlier-batch-retired", id =>
                [[new DocumentRetireWrite(id)], [new DocumentChangeWrite(id, DocumentContent.Base(1, O("{\"n\":2}")))]], "is retired"),
            ("a-creation-at-the-address-an-earlier-batch-took", id =>
            [
                [new DocumentCreateWrite(new DocumentCreate { Id = new DocumentId(id.Value + 1), Kind = "n", Key = "taken", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, O("{\"n\":2}")))],
                [new DocumentCreateWrite(new DocumentCreate { Id = new DocumentId(id.Value + 2), Kind = "n", Key = "taken", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, O("{\"n\":3}")))],
            ], "already has a current incarnation"),
        ])
            yield return Case($"storage.store.commit.refuses-{name}-against-the-state-the-batches-before-it-left", async () =>
            {
                var counted = await CountedStore.OpenAsync();
                var (store, storage) = (counted.Store, counted.Storage);
                var document = new DocumentId(200);
                await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = document, Kind = "n", Scope = DocumentScope.Session },
                    DocumentContent.Base(1, O("{\"n\":1}")))], StoreContext);
                var last = store.LastSeq;
                var receipts = new List<int>();
                var result = await storage.CommitAsync(calls(document), (index, _) => receipts.Add(index), StoreContext);
                Equal(1, result.Stored, "stored");
                Check(result.Refused?.Message.Contains(message, StringComparison.Ordinal) == true, $"refused: {result.Refused?.Message}");
                Equal(("0", last + 1), (string.Join(",", receipts), store.LastSeq), "receipts and last seq");
                Equal(last + 1, (await OutboxSeqsAsync(store))[^1], "the outbox's last commit");
                await store.CloseAsync(ChordContext.Background);
            });

        yield return Case("storage.store.commit.checks-a-batch-against-the-batches-before-it-in-the-call-a-copy-reads-their-state", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            var source = await storage.MintIdAsync<DocumentId>();
            var child = await storage.MintIdAsync<ConversationId>();
            var copy = await storage.MintIdAsync<DocumentId>();
            static DocumentCreate Record(DocumentId id, ConversationId conversationId) => new()
            {
                Id = id, Kind = "c", Scope = DocumentScope.Conversation(conversationId), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            var result = await storage.CommitAsync(
            [
                [new DocumentCreateWrite(Record(source, ConversationId.Root), DocumentContent.Base(1, O("{\"n\":1}")))],
                [new DocumentChangeWrite(source, DocumentContent.Delta(1, OpsOf("[[\"s\",[\"n\"],2]]")))],
                [new ConversationWrite(new ConversationRecord(child)), new DocumentCopyWrite(Record(copy, child), new DocumentCopySource(source, DocumentPoint.Current))],
            ], (_, _) => { }, StoreContext);
            Equal(new CommitResult(3), result, "result");
            SameJson("{\"n\":2}", (await storage.GetDocumentAsync(copy, DocumentPoint.Current, StoreContext))?.Value, "the copy");
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── failures: a rollback rejects, an unknown outcome stops the store ─

        yield return Case("storage.store.commit.rejects-with-nothing-stored-when-a-batchs-check-fails-in-the-backend-and-the-store-goes-on", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            EntryId first = await storage.MintIdAsync<EntryId>(), second = await storage.MintIdAsync<EntryId>();
            var last = store.LastSeq;
            // The second batch's id check: its first read of the record ids.
            var claimReads = 0;
            counted.Faults.Fail = (sql, _) => sql.Contains("record_ids", StringComparison.Ordinal) && ++claimReads == 2;
            var receipts = new List<int>();
            await RejectsAsync(() => storage.CommitAsync([[StoreEntryWrite(first)], [StoreEntryWrite(second)]], (index, _) => receipts.Add(index), StoreContext),
                "injected", "the call");
            Equal(("", last), (string.Join(",", receipts), store.LastSeq), "receipts and last seq");
            Equal<CommittedEntry?>(null, await storage.GetEntryAsync(first, StoreContext), "first entry");
            Equal(new Seq(last + 1), await storage.CommitWritesAsync([StoreEntryWrite(first)], StoreContext), "the next commit");
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commit.a-failed-commit-is-an-unknown-outcome-the-call-rejects-storage-failed-nothing-is-received-or-stored", async () =>
        {
            var directory = Temp("store-commit");
            var path = StorePath(directory);
            var counted = await CountedStore.OpenAsync(path);
            var (store, storage) = (counted.Store, counted.Storage);
            EntryId[] ids = [await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>()];
            var last = store.LastSeq;
            counted.Faults.Commit = true;
            var receipts = new List<int>();
            await RejectsChordAsync(() => storage.CommitAsync([.. ids.Select(id => (IReadOnlyList<StorageWrite>)[StoreEntryWrite(id)])],
                (index, _) => receipts.Add(index), StoreContext), "unavailable", "storage_failed", "the call");
            Equal("", string.Join(",", receipts), "receipts");
            await RejectsChordAsync(() => storage.GetEntryAsync(ids[0], StoreContext), "unavailable", "storage_failed", "a read after");
            await store.CloseAsync(ChordContext.Background);
            var reopened = await ReopenStoreAsync(path);
            Equal<CommittedEntry?>(null, await reopened.Storage.GetEntryAsync(ids[0], StoreContext), "the entry after reopen");
            Equal(new Seq(last + 1), await reopened.Storage.CommitWritesAsync([StoreEntryWrite(ids[1])], StoreContext), "the next commit");
            await reopened.Store.CloseAsync(ChordContext.Background);
            Directory.Delete(directory, recursive: true);
        });

        foreach (var third in (string[])["valid", "refused"])
            yield return Case($"storage.store.commit.rolls-a-3-batch-call-back-at-a-failure-of-any-of-its-statements-as-at-a-failed-commit-and-reopens-before-it-third-{third}",
                () => AuditAsync(third));

        yield return Case("storage.store.commit.a-call-on-a-closed-storage-or-on-a-stopped-store-rejects-and-stores-nothing", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var entry = await counted.Storage.MintIdAsync<EntryId>();
            counted.Faults.Commit = true;
            await RejectsChordAsync(() => counted.Storage.CommitWritesAsync([StoreEntryWrite(entry)], StoreContext), "unavailable", "storage_failed", "the failed commit");
            await RejectsChordAsync(() => counted.Storage.CommitAsync([[StoreEntryWrite(entry)]], (_, _) => { }, StoreContext), "unavailable", "storage_failed",
                "a call on the stopped store");
            await counted.Store.CloseAsync(ChordContext.Background);
            var other = await CountedStore.OpenAsync();
            await other.Storage.CloseAsync(StoreContext);
            await RejectsAsync(() => other.Storage.CommitAsync([[StoreEntryWrite(entry)]], (_, _) => { }, StoreContext), "closed", "a call on a closed storage");
            await other.Store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commit.a-call-queued-when-the-store-closes-rejects-released-and-stores-nothing-the-running-one-is-stored", async () =>
        {
            var directory = Temp("store-commit");
            var path = StorePath(directory);
            var counted = await CountedStore.OpenAsync(path);
            var (store, storage) = (counted.Store, counted.Storage);
            EntryId first = await storage.MintIdAsync<EntryId>(), second = await storage.MintIdAsync<EntryId>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            counted.Faults.Hold = (_, index) =>
            {
                if (index != 1) return null;
                entered.TrySetResult();
                return release.Task;
            };
            var running = storage.CommitAsync([[StoreEntryWrite(first)]], (_, _) => { }, StoreContext);
            await entered.Task;
            var queued = storage.CommitAsync([[StoreEntryWrite(second)]], (_, _) => { }, StoreContext);
            var closing = store.CloseAsync(ChordContext.Background);
            release.SetResult();
            Equal(new CommitResult(1), await running, "the running call");
            await RejectsChordAsync(() => queued, "released", null, "the queued call");
            await closing;
            var again = await ReopenStoreAsync(path);
            Check(await again.Storage.GetEntryAsync(first, StoreContext) is not null, "the running call's entry");
            Equal<CommittedEntry?>(null, await again.Storage.GetEntryAsync(second, StoreContext), "the queued call's entry");
            await again.Store.CloseAsync(ChordContext.Background);
            Directory.Delete(directory, recursive: true);
        });

        // ─── durability ─────────────────────────────────────────────────────

        yield return Case("storage.store.commit.keeps-every-batch-of-a-stored-call-across-a-crash-reopened-the-seqs-rows-documents-and-ids-are-there", async () =>
        {
            var directory = Temp("store-commit");
            var path = StorePath(directory);
            var counted = await CountedStore.OpenAsync(path);
            var (store, storage) = (counted.Store, counted.Storage);
            var entry = await storage.MintIdAsync<EntryId>();
            var document = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = document, Kind = "n", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            var seqs = new List<Seq>();
            await storage.CommitAsync(
            [
                [StoreEntryWrite(entry)],
                [new DocumentCreateWrite(record, DocumentContent.Base(1, O("{\"n\":1}")))],
                [new DocumentChangeWrite(document, DocumentContent.Delta(1, OpsOf("[[\"s\",[\"n\"],2]]")))],
            ], (_, seq) => seqs.Add(seq), StoreContext);
            var minted = await storage.MintIdAsync<EntryId>();
            // A crash: the store is never closed; another process opens the file.
            var (other, reopened) = await ReopenStoreAsync(path);
            Equal<Seq?>(seqs[0], (await reopened.GetEntryAsync(entry, StoreContext))?.CommitSeq, "the entry's seq");
            SameJson("{\"n\":1}", (await reopened.GetDocumentAsync(document, DocumentPoint.At(seqs[1]), StoreContext))?.Value, "the document at its creation");
            SameJson("{\"n\":2}", (await reopened.GetDocumentAsync(document, DocumentPoint.Current, StoreContext))?.Value, "the document now");
            Check((await reopened.MintIdAsync<EntryId>()).Value > document.Value, "the allocator is above the call's ids");
            Equal(new Seq(seqs[2].Value + 1), await reopened.CommitWritesAsync([StoreEntryWrite(minted)], StoreContext), "the next commit");
            await other.CloseAsync(ChordContext.Background);
            await store.CloseAsync(ChordContext.Background);
            Directory.Delete(directory, recursive: true);
        });

        // ─── publication ────────────────────────────────────────────────────

        yield return Case("storage.store.commit.publishes-each-commit-of-a-call-in-a-host-batch-of-its-own-after-the-calls-one-transaction", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var store = counted.Store;
            // Adapted: a hand-built host batch, attached as the host's open attaches it (as in StoreStorageCases.cs).
            var batch = new Batch(_ => { }, null);
            store.Attach(new Attachment(_ => { }, batch));
            var storage = new HostPublishing(counted.Storage);
            var session = new Session(storage);
            var events = new SyncList<string>();
            FollowCommits(session, (commit, _) =>
            {
                if (commit.IsMemory) return;
                var seq = commit.Seq!.Value.Value;
                events.Add($"publish {seq} running={store.Batch!.Running}");
                batch.Flushing(_ => events.Add($"flush {seq}"));
            });
            // Hold the first call, so the commits after it are sent together in the next one.
            var held = storage.HoldCommits();
            var first = session.CommitAsync(tx => tx.AppendEntryAsync(ConversationId.Root, new EntryDraft { Kind = "a" }), StoreContext);
            await held.Entered;
            var rest = new[] { 1, 2, 3 }.Select(n =>
                session.CommitAsync(tx => tx.AppendEntryAsync(ConversationId.Root, new EntryDraft { Kind = $"b{n}" }), StoreContext)).ToList();
            // Their line parts ran: each is admitted behind the held call.
            await session.LineTickAsync();
            var before = counted.Transactions;
            held.Release();
            await Task.WhenAll([first, .. rest]);
            SessionTests.Seq([1, 3], storage.Calls.Snapshot(), "batches per call");
            Equal(2, counted.Transactions - before, "transactions");
            var last = store.LastSeq;
            SessionTests.Seq(new[] { last - 3, last - 2, last - 1, last }.SelectMany(each => new[] { $"publish {each} running=True", $"flush {each}" }),
                events.Snapshot(), "publications");
            await session.CloseAsync(StoreContext);
            store.Detach();
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── other users of the store ───────────────────────────────────────

        yield return Case("storage.store.commit.another-owners-call-waits-for-a-series-in-its-transaction-the-series-receipts-come-first", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            var other = await StoreStorage.OpenAsync(store, "other", StoreContext);
            EntryId first = await storage.MintIdAsync<EntryId>(), second = await storage.MintIdAsync<EntryId>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            counted.Faults.Hold = (_, index) =>
            {
                if (index != 2) return null;
                entered.TrySetResult();
                return release.Task;
            };
            var events = new List<string>();
            var series = storage.CommitAsync([[StoreEntryWrite(first)], [StoreEntryWrite(second)]], (index, seq) => { lock (events) events.Add($"harness {index} {seq.Value}"); }, StoreContext);
            await entered.Task;
            var waiting = other.CommitAsync([[new ConversationWrite(new ConversationRecord(ConversationId.Root))]],
                (index, seq) => { lock (events) events.Add($"other {index} {seq.Value}"); }, StoreContext);
            await Macrotask();
            lock (events) Equal(0, events.Count, "events while the series is held");
            var last = store.LastSeq;
            release.SetResult();
            await Task.WhenAll(series, waiting);
            SessionTests.Seq([$"harness 0 {last + 1}", $"harness 1 {last + 2}", $"other 0 {last + 3}"], events, "events");
            SessionTests.Seq([last + 1, last + 2, last + 3], (await OutboxSeqsAsync(store))[^3..], "outbox");
            await other.CloseAsync(StoreContext);
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commit.a-read-during-a-series-sees-all-of-it-or-none-of-it", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            EntryId first = await storage.MintIdAsync<EntryId>(), second = await storage.MintIdAsync<EntryId>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Held after the first batch's writes, in the second batch's check.
            var claimReads = 0;
            counted.Faults.Hold = (sql, _) =>
            {
                if (!sql.Contains("record_ids", StringComparison.Ordinal) || ++claimReads != 2) return null;
                entered.TrySetResult();
                return release.Task;
            };
            var series = storage.CommitAsync([[StoreEntryWrite(first)], [StoreEntryWrite(second)]], (_, _) => { }, StoreContext);
            await entered.Task;
            var read = Task.WhenAll(storage.GetEntryAsync(first, StoreContext), storage.GetEntryAsync(second, StoreContext));
            await Macrotask();
            release.SetResult();
            await series;
            var found = await read;
            Equal((false, false), (found[0] is null, found[1] is null), "the read's entries are absent");
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commit.an-export-holds-the-queue-a-series-waits-for-it-and-is-not-in-its-archive", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var (store, storage) = (counted.Store, counted.Storage);
            var entry = await storage.MintIdAsync<EntryId>();
            var last = store.LastSeq;
            var parts = store.Export(ChordContext.Background);
            Check(await parts.MoveNextAsync(), "the manifest");
            Equal(("manifest", last), ((string?)parts.Current["kind"], (long?)parts.Current["lastSeq"] ?? -1), "the manifest");
            var settled = false;
            var series = storage.CommitAsync([[StoreEntryWrite(entry)]], (_, _) => { }, StoreContext).ContinueWith(task =>
            {
                settled = true;
                return task;
            }, TaskScheduler.Default).Unwrap();
            await Macrotask();
            Check(!settled, "the series waits for the export");
            await parts.DisposeAsync();
            Equal(new CommitResult(1), await series, "the series");
            Equal(last + 1, store.LastSeq, "last seq");
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── GC ─────────────────────────────────────────────────────────────

        yield return Case("storage.store.commit.gc.keeps-nothing-of-a-settled-call-its-batches-are-collected", async () =>
        {
            var counted = await CountedStore.OpenAsync();
            var reference = await SentBatchesAsync(counted.Storage);
            for (var round = 0; round < 10 && reference.IsAlive; round++)
            {
                await Task.Yield();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Check(!reference.IsAlive, "the call's batches are collected");
            await counted.Store.CloseAsync(ChordContext.Background);
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SentBatchesAsync(IStorage storage)
    {
        List<IReadOnlyList<StorageWrite>> batches = [[StoreEntryWrite(await storage.MintIdAsync<EntryId>())]];
        await storage.CommitAsync(batches, (_, _) => { }, StoreContext);
        return new WeakReference(batches);
    }

    // ─── the 3-batch audit ──────────────────────────────────────────────────

    /// <summary>The record the audit's source document and its copy share.</summary>
    private static DocumentCreate AuditRecord(long id, long conversationId) => new()
    {
        Id = new DocumentId(id), Kind = "audit", Scope = DocumentScope.Conversation(new ConversationId(conversationId)),
        History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
    };

    /// <summary>The source document, stored before the call.</summary>
    private static StorageWrite[] AuditSeed() => [new DocumentCreateWrite(AuditRecord(100, ConversationId.Root.Value), DocumentContent.Base(1, O("{\"n\":0}")))];

    private static StorageWrite AuditDelta(int n) => new DocumentChangeWrite(new DocumentId(100), DocumentContent.Delta(1, OpsOf($"[[\"s\",[\"n\"],{n}]]")));

    /// <summary>Three batches: an entry and a delta; a conversation, a copy of the source as the first batch left it and an entry; then an
    /// entry and a delta, or, refused, an entry whose id the first batch took.</summary>
    private static IReadOnlyList<IReadOnlyList<StorageWrite>> AuditBatches(string third) =>
    [
        [StoreEntryWrite(new EntryId(101), "b1"), AuditDelta(1)],
        [
            new ConversationWrite(new ConversationRecord(new ConversationId(105))),
            new DocumentCopyWrite(AuditRecord(102, 105), new DocumentCopySource(new DocumentId(100), DocumentPoint.Current)),
            StoreEntryWrite(new EntryId(103), "b2"),
        ],
        third == "valid" ? [StoreEntryWrite(new EntryId(104), "b3"), AuditDelta(2)] : [StoreEntryWrite(new EntryId(101), "again")],
    ];

    /// <summary>What a storage holds of the audit's records, as text.</summary>
    private static async Task<string> AuditStateAsync(IStorage storage)
    {
        var entries = new List<string>();
        foreach (var id in (long[])[101, 103, 104]) entries.Add((await storage.GetEntryAsync(new EntryId(id), StoreContext))?.Entry.Kind ?? "null");
        var source = (await storage.GetDocumentAsync(new DocumentId(100), DocumentPoint.Current, StoreContext))?.Value.ToJson() ?? "null";
        var copy = (await storage.GetDocumentAsync(new DocumentId(102), DocumentPoint.Current, StoreContext))?.Value.ToJson() ?? "null";
        var child = (await storage.GetConversationAsync(new ConversationId(105), StoreContext))?.ToJson().ToJson() ?? "null";
        return $"entries=[{string.Join(",", entries)}] source={source} copy={copy} child={child}";
    }

    /// <summary>What a call returned and what it left: its result, its receipts' seqs after the store's last one, the state.</summary>
    private static async Task<string> AuditOutcomeAsync(IStorage storage, string third, long last)
    {
        var seqs = new List<long>();
        var result = await storage.CommitAsync(AuditBatches(third), (_, seq) => seqs.Add(seq.Value - last), StoreContext);
        return $"stored={result.Stored} refused={result.Refused?.Message} seqs=[{string.Join(",", seqs)}] {await AuditStateAsync(storage)}";
    }

    /// <summary>Every message in an error's cause chain.</summary>
    private static List<string> Chain(Exception? error)
    {
        var messages = new List<string>();
        for (var at = error; at is not null; at = at.InnerException) messages.Add(at.Message);
        return messages;
    }

    private static async Task AuditAsync(string third)
    {
        // What MemoryStorage makes of the call: the outcome every retry must reach.
        var memory = new MemoryStorage();
        await CreateStoreRoot(memory);
        await memory.CommitWritesAsync(AuditSeed(), StoreContext);
        var expected = await AuditOutcomeAsync(memory, third, 2);
        Check(expected.StartsWith(third == "valid" ? "stored=3 refused= seqs=[1,2,3]" : "stored=2 refused=ID 101 already belongs to entry seqs=[1,2]", StringComparison.Ordinal),
            "Memory's outcome: " + expected);
        // The statements of the call's transaction (a read transaction after it would count its own).
        var probe = await CountedStore.OpenAsync();
        await probe.Storage.CommitWritesAsync(AuditSeed(), StoreContext);
        var probed = await probe.Storage.CommitAsync(AuditBatches(third), (_, _) => { }, StoreContext);
        var total = (int)probe.Statements;
        // Its cost: no read beyond a batch's checks, the metadata once (statement counts per batch in the cost guard).
        Equal(third == "valid" ? 29 : 23, total, "the call's statements");
        var memoryState = await AuditStateAsync(memory);
        Equal(memoryState, await AuditStateAsync(probe.Storage), "the probe's state");
        Equal(third == "valid" ? 3 : 2, probed.Stored, "the probe's stored");
        await probe.Store.CloseAsync(ChordContext.Background);
        var directory = Temp("store-audit");
        // Each statement in turn, then the COMMIT.
        for (var at = 1; at <= total + 1; at++)
        {
            var path = StorePath(directory);
            var opened = await CountedStore.OpenAsync(path);
            await opened.Storage.CommitWritesAsync(AuditSeed(), StoreContext);
            var last = opened.Store.LastSeq;
            var outbox = await OutboxSeqsAsync(opened.Store);
            var before = await AuditStateAsync(opened.Storage);
            var failing = at;
            if (at <= total) opened.Faults.Fail = (_, index) => index == failing;
            else opened.Faults.Commit = true;
            var receipts = new List<int>();
            Exception? error = null;
            try
            {
                await opened.Storage.CommitAsync(AuditBatches(third), (index, _) => receipts.Add(index), StoreContext);
            }
            catch (Exception rejection)
            {
                error = rejection;
            }
            Equal("", string.Join(",", receipts), $"statement {at}: receipts");
            Equal(last, opened.Store.LastSeq, $"statement {at}: last seq");
            if (at <= total)
            {
                // A rollback: the call rejects with its failure, the store goes on.
                Check(Chain(error).Contains($"injected: statement {at} failed"), $"statement {at}: the rejection: {error}");
                Equal(before, await AuditStateAsync(opened.Storage), $"statement {at}: the state");
            }
            else
            {
                Check(error is ChordError { Code: "unavailable", Reason: "storage_failed" }, $"the failed COMMIT: {error}");
            }
            await opened.Store.CloseAsync(ChordContext.Background);
            // On disk: as before the call, the seq, the outbox, the records and the allocator.
            var again = await ReopenStoreAsync(path);
            Equal(last, again.Store.LastSeq, $"statement {at}: reopened last seq");
            SessionTests.Seq(outbox, await OutboxSeqsAsync(again.Store), $"statement {at}: reopened outbox");
            Equal(before, await AuditStateAsync(again.Storage), $"statement {at}: reopened state");
            Check((await again.Storage.MintIdAsync<EntryId>()).Value > 100, $"statement {at}: the allocator");
            // A retry reaches MemoryStorage's outcome.
            Equal(expected, await AuditOutcomeAsync(again.Storage, third, last), $"statement {at}: the retry");
            var seqs = third == "valid" ? new long[] { 1, 2, 3 } : [1, 2];
            SessionTests.Seq([.. outbox, .. seqs.Select(seq => last + seq)], await OutboxSeqsAsync(again.Store), $"statement {at}: the retry's outbox");
            await again.Store.CloseAsync(ChordContext.Background);
        }
        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>What the next backend transaction does besides its work: <see cref="Commit"/> fails it after its work (a failed COMMIT, an
/// unknown outcome); <see cref="Fail"/> fails a statement it matches (by its SQL and its index, from 1), as a backend error; <see cref="Hold"/>
/// holds a statement until the task it returns completes (upstream <c>Faults</c>).</summary>
internal sealed class StoreFaults
{
    public bool Commit { get; set; }
    public Func<string, int, bool>? Fail { get; set; }
    public Func<string, int, Task?>? Hold { get; set; }
}

/// <summary>A storage with its root conversation, over a store with an outbox on a SQLite file, whose backend counts its transactions and
/// the statements of the last one, and takes faults for its next transaction (upstream <c>counted</c>).</summary>
/// <summary>Controlled storage over a <see cref="StoreStorage"/> that publishes through it (<see cref="IStorage.Publishing"/>, which
/// ControlledStorage leaves to the default, as upstream's MemoryStorage has none) and records how many batches each call sends (upstream's
/// <c>calls.push(batches.length)</c>).</summary>
internal sealed class HostPublishing(StoreStorage inner) : ControlledStorage(inner), IStorage
{
    public SyncList<int> Calls { get; } = new();

    public override Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive,
        DurableContext context)
    {
        Calls.Add(batches.Count);
        return base.CommitAsync(batches, receive, context);
    }

    void IStorage.Publishing(Action publish, DurableContext context) => inner.Publishing(publish, context);
}

internal sealed class CountedStore
{
    private int transactions;
    private long statements;

    private CountedStore() { }

    public ChordStore Store { get; private set; } = null!;
    public StoreStorage Storage { get; private set; } = null!;
    public StoreFaults Faults { get; } = new();
    public int Transactions => Volatile.Read(ref transactions);
    public long Statements => Interlocked.Read(ref statements);

    public static async Task<CountedStore> OpenAsync(string path = ":memory:", bool outbox = true)
    {
        var counted = new CountedStore();
        var db = new StoreSqliteDatabase(await SqliteConnectionDatabase.OpenAsync(path));
        counted.Store = (ChordStore)await Tables.OpenStoreAsync(Tables.SqliteBackend(new Counting(db, counted)), ChordContext.Background,
            outbox ? new StoreOptions { Outbox = true } : null);
        counted.Storage = await StoreStorage.OpenAsync(counted.Store, "harness", DurableContext.Background);
        await counted.Storage.CommitWritesAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root))], DurableContext.Background);
        return counted;
    }

    private sealed class Counting(ChordSqliteDatabase db, CountedStore counted) : ChordSqliteDatabase
    {
        public Task ExecAsync(string sql) => db.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => db.RunAsync(sql, parameters);
        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => db.RunCountedAsync(sql, parameters);
        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => db.GetAsync(sql, parameters);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) => db.AllAsync(sql, parameters);
        public Task CloseAsync() => db.CloseAsync();

        public Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
        {
            Interlocked.Increment(ref counted.transactions);
            Interlocked.Exchange(ref counted.statements, 0);
            var faults = counted.Faults;
            var (commit, fail, hold) = (faults.Commit, faults.Fail, faults.Hold);
            faults.Commit = false;
            faults.Fail = null;
            faults.Hold = null;
            return db.TransactionAsync(async executor =>
            {
                var result = await callback(new Executor(executor, counted, fail, hold)).ConfigureAwait(false);
                if (commit) throw new InvalidOperationException("COMMIT failed");
                return result;
            });
        }
    }

    private sealed class Executor(ISqliteExecutor executor, CountedStore counted, Func<string, int, bool>? fail, Func<string, int, Task?>? hold) : ISqliteExecutor
    {
        private async Task<T> Call<T>(string sql, Func<Task<T>> run)
        {
            var index = (int)Interlocked.Increment(ref counted.statements);
            if (hold?.Invoke(sql, index) is { } held) await held.ConfigureAwait(false);
            if (fail?.Invoke(sql, index) == true) throw new InvalidOperationException($"injected: statement {index} failed");
            return await run().ConfigureAwait(false);
        }

        public Task ExecAsync(string sql) => Call(sql, async () => { await executor.ExecAsync(sql).ConfigureAwait(false); return true; });
        public Task RunAsync(string sql, params object?[] parameters) =>
            Call(sql, async () => { await executor.RunAsync(sql, parameters).ConfigureAwait(false); return true; });
        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => Call(sql, () => executor.RunCountedAsync(sql, parameters));
        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => Call(sql, () => executor.GetAsync(sql, parameters));
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) =>
            Call(sql, () => executor.AllAsync(sql, parameters));
    }
}
