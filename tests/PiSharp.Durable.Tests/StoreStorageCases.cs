// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/store-storage.test.ts
//
// StoreStorage (slice 6b §2): Durable's storage conformance suite on SQLite :memory: (storage.store.conformance.<case>) and across reopen
// (storage.store.reopen.conformance.<case>, a storage that closes and reopens its store file after every commit), then what is its own:
// claims and close, owners sharing a store, the store's semantics for a refused commit, the allocator, archives and outbox replays of
// Durable data, head markers, guarded reads, publication, GC. Adapted, as phase 6 ports Chord's host (createHost) and these cases build
// objects through it:
// - export-and-import-keep-...: Chord's own data in the same store is another owner's StoreStorage document (owner app.memo) instead of a
//   Chord object's persisted state (a facet's state(MemoDoc)); the export holds its document.create, the archive round-trips, and the
//   harness owner's reads at a point agree, as upstream checks.
// - publishes-...-inside-the-attached-hosts-batch: the host is a hand-built batch attached to the store (as the host's open does; the
//   attachment is internal to PiSharp.Chord, reached through InternalsVisibleTo) and the publications are the storage's own Publishing
//   calls, as upstream's are; a Session publishing through it is storage.store.commit.publishes-each-commit-of-a-call-in-a-host-batch-...
//   (StoreStorageCommitCases.cs).
// - counts-four-statements-...: the statements are counted by a backend wrapper (the commit cases' CountedStore), as PiSharp's
//   DurableContext has no telemetry span to carry chord.store.statements until area telemetry; the backend counts every statement of the
//   commit's one transaction, which is what the metric counts.
// Not ported here: the two "Durable's views as Chord sources on the store" cases (a derive over followed views and a deferred commit's
// order): they run a Harness inside a Chord object (createHost, defineFacet, self.open/follow/derive), phase 6.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Store;
using PiSharp.Durable.Testing;
using static SessionTests;
using JsonValue = PiSharp.Durable.Chord.JsonValue;
using Attachment = PiSharp.Chord.Store.Attachment;
using ChordStore = PiSharp.Chord.Store.ChordStore;
using IStore = PiSharp.Chord.Store.IStore;
using StoreOptions = PiSharp.Chord.Store.StoreOptions;
using Tables = PiSharp.Chord.Store.Tables;

internal static partial class Program
{
    private static readonly DurableContext StoreContext = DurableContext.Background;

    /// <summary>A store on <c>:memory:</c>.</summary>
    private static async Task<ChordStore> MemoryStoreAsync(StoreOptions? options = null) =>
        (ChordStore)await Tables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(":memory:"), ChordContext.Background, options);

    private static Task<Seq> CreateStoreRoot(IStorage storage) =>
        storage.CommitWritesAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root))], StoreContext);

    private static async Task<List<JsonNode?>> ExportedAsync(IStore store)
    {
        var parts = new List<JsonNode?>();
        await foreach (var part in store.Export(ChordContext.Background)) parts.Add(part.DeepClone());
        return parts;
    }

    private static void SameParts(IReadOnlyList<JsonNode?> expected, IReadOnlyList<JsonNode?> actual, string what)
    {
        Equal(expected.Count, actual.Count, what + ": parts");
        for (var index = 0; index < expected.Count; index++)
            Check(JsonNode.DeepEquals(expected[index], actual[index]), $"{what}: part {index}: expected {expected[index]?.ToJsonString()}, actual {actual[index]?.ToJsonString()}");
    }

    private static IEnumerable<(string, Func<Task>)> StoreStorageCases()
    {
        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var storage = await OpenStoreStorageAsync(":memory:");
            try
            {
                await use(storage);
            }
            finally
            {
                await storage.CloseAsync(StoreContext);
            }
        }))
            yield return Case("storage.store.conformance." + testCase.Slug, testCase.Run);

        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var directory = Temp("store-conformance");
            var path = Path.Combine(directory, "store.sqlite");
            var storage = new ReopeningStoreStorage(await OpenStoreStorageAsync(path), path);
            try
            {
                await use(storage);
            }
            finally
            {
                await storage.CloseAsync(StoreContext);
                Directory.Delete(directory, recursive: true);
            }
        }))
            yield return Case("storage.store.reopen.conformance." + testCase.Slug, testCase.Run);

        // ─── claims and close ───────────────────────────────────────────────

        yield return Case("storage.store.claims.refuse-a-second-storestorage-on-one-owner-until-the-first-closed-two-owners-share-a-store", async () =>
        {
            var store = await MemoryStoreAsync();
            var first = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            var refused = await ThrowsAsync<ChordError>(() => StoreStorage.OpenAsync(store, "harness", StoreContext), "second open");
            Equal(("invalid", "args"), (refused.Code, refused.Reason), "second open's error");
            var other = await StoreStorage.OpenAsync(store, "other", StoreContext);
            await CreateStoreRoot(first);
            Equal<ConversationRecord?>(null, await other.GetConversationAsync(ConversationId.Root, StoreContext), "the other owner's root");
            await first.CloseAsync(StoreContext);
            await RejectsAsync(() => first.GetConversationAsync(ConversationId.Root, StoreContext), "StoreStorage is closed", "a read after close");
            var reopened = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            Equal(new ConversationRecord(ConversationId.Root), await reopened.GetConversationAsync(ConversationId.Root, StoreContext), "root");
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.claims.waits-at-close-for-its-calls-underway-then-releases-its-claims", async () =>
        {
            var store = await MemoryStoreAsync();
            var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            var commit = CreateStoreRoot(storage);
            var closing = storage.CloseAsync(StoreContext);
            await commit;
            await closing;
            await StoreStorage.OpenAsync(store, "harness", StoreContext);
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── the store's semantics (D4) ─────────────────────────────────────

        yield return Case("storage.store.semantics.a-refused-durable-commit-rejects-with-durables-message-and-keeps-the-store-usable", async () =>
        {
            var store = await MemoryStoreAsync();
            var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            await CreateStoreRoot(storage);
            await RejectsAsync(() => CreateStoreRoot(storage), "ID 1 already belongs to conversation", "a second root");
            var entry = new EntryRecord { Id = await storage.MintIdAsync<EntryId>(), ConversationId = ConversationId.Root, Kind = "m" };
            await storage.CommitWritesAsync([new EntryWrite(entry)], StoreContext);
            Equal(entry.ToJson().ToJson(), (await storage.GetEntryAsync(entry.Id, StoreContext))?.Entry.ToJson().ToJson(), "the entry");
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.semantics.raises-the-allocator-above-every-id-it-holds-when-it-opens", async () =>
        {
            var store = await MemoryStoreAsync();
            var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            var entry = new EntryRecord { Id = new EntryId(500), ConversationId = ConversationId.Root, Kind = "m" };
            await storage.CommitWritesAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root)), new EntryWrite(entry)], StoreContext);
            Equal(new EntryId(501), await storage.MintIdAsync<EntryId>(), "the next id");
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── archives and batches of Durable data ───────────────────────────

        yield return Case("storage.store.archives.export-and-import-keep-rewindable-history-retired-records-and-another-owners-data", async () =>
        {
            var (store, storage) = await RecordedStoreAsync();
            var (rewindable, seqs) = await StoreHistoryAsync(storage);
            // Another owner's data in the same store (adapted: upstream's is a Chord object's persisted document).
            var app = await StoreStorage.OpenAsync(store, "app.memo", StoreContext);
            var memo = await app.MintIdAsync<DocumentId>();
            await app.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = memo, Kind = "memo", Scope = DocumentScope.Session },
                DocumentContent.Base(1, O("{\"m\":1}")))], StoreContext);
            await app.CommitWritesAsync([new DocumentChangeWrite(memo, DocumentContent.Delta(1, OpsOf("[[\"s\",[\"m\"],2]]")))], StoreContext);
            await app.CloseAsync(StoreContext);
            var parts = await ExportedAsync(store);
            Check(parts.Any(part => (string?)part!["kind"] == "document.create" && (string?)part["record"]!["owner"] == "app.memo"),
                "the export holds the other owner's document");
            var copy = await Tables.ImportStoreAsync(await StoreBackends.OpenSqliteAsync(":memory:"), parts, ChordContext.Background);
            var imported = await StoreStorage.OpenAsync(copy, "harness", StoreContext);
            foreach (var at in (DocumentPoint[])[DocumentPoint.At(seqs[0]), DocumentPoint.At(seqs[1]), DocumentPoint.Current])
                Equal(await storage.GetDocumentAsync(rewindable, at, StoreContext), await imported.GetDocumentAsync(rewindable, at, StoreContext), $"at {at}");
            var query = new DocumentQuery(DocumentScope.Conversation(ConversationId.Root), DocumentPoint.At(seqs[1]));
            var expected = await storage.ScanDocumentsAsync(query, 10, null, StoreContext);
            var actual = await imported.ScanDocumentsAsync(query, 10, null, StoreContext);
            Check(expected.Items.SequenceEqual(actual.Items) && expected.Next is null == actual.Next is null, "the scans at a point agree");
            var app2 = await StoreStorage.OpenAsync(copy, "app.memo", StoreContext);
            SameJson("{\"m\":2}", (await app2.GetDocumentAsync(memo, DocumentPoint.Current, StoreContext))?.Value, "the other owner's document");
            await app2.CloseAsync(StoreContext);
            SameParts(parts, await ExportedAsync(copy), "the copy's archive");
            Equal(await storage.MintIdAsync<EntryId>(), await imported.MintIdAsync<EntryId>(), "the allocators");
            await copy.CloseAsync(ChordContext.Background);
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.archives.an-outbox-of-durable-commits-replayed-into-an-empty-store-gives-the-same-archive", async () =>
        {
            var (store, storage) = await RecordedStoreAsync();
            await StoreHistoryAsync(storage);
            var commits = await store.Outbox!.ReadAsync(0, 1_000, ChordContext.Background);
            var replica = await MemoryStoreAsync();
            await Tables.ApplyBatchAsync(replica, commits, ChordContext.Background);
            SameParts(await ExportedAsync(store), await ExportedAsync(replica), "the replica's archive");
            await replica.CloseAsync(ChordContext.Background);
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── head markers (the entry_heads table) ───────────────────────────

        yield return Case("storage.store.heads.a-head-entry-is-found-after-reopen-in-an-imported-archive-and-in-a-replayed-outbox", async () =>
        {
            var directory = Temp("store-heads");
            var path = Path.Combine(directory, "store.sqlite");
            async Task<ChordStore> Open() =>
                (ChordStore)await Tables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(path), ChordContext.Background, new StoreOptions { Outbox = true });
            var store = await Open();
            var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            await CreateStoreRoot(storage);
            async Task<EntryId> Entry(EntryId? head = null)
            {
                var id = await storage.MintIdAsync<EntryId>();
                await storage.CommitWritesAsync([new EntryWrite(new EntryRecord
                {
                    Id = id, ConversationId = ConversationId.Root, Kind = "k", Data = JsonValue.Object([]), Head = head,
                })], StoreContext);
                return id;
            }
            var first = await Entry();
            var marker = await Entry(first);
            await Entry();
            async Task<(EntryId?, EntryId?)> Heads(IStorage of) =>
                ((await of.FindLatestHeadMarkerAsync(ConversationId.Root, null, StoreContext))?.Id,
                    (await of.FindLatestHeadMarkerAsync(ConversationId.Root, first, StoreContext))?.Id);
            Equal<(EntryId?, EntryId?)>((marker, null), await Heads(storage), "heads");
            await storage.CloseAsync(StoreContext);
            await store.CloseAsync(ChordContext.Background);
            store = await Open();
            storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            Equal<(EntryId?, EntryId?)>((marker, null), await Heads(storage), "heads after reopen");
            var parts = await ExportedAsync(store);
            var puts = parts.Where(part => (string?)part!["kind"] == "put" && (string?)part["table"] == "harness:entry_heads").ToList();
            Equal(1, puts.Count, "entry_heads puts");
            Check(JsonNode.DeepEquals(JsonNode.Parse($"{{\"id\":{marker.Value},\"conversation_id\":{ConversationId.Root.Value}}}"), puts[0]!["row"]),
                "the put's row: " + puts[0]!["row"]?.ToJsonString());
            var copy = await Tables.ImportStoreAsync(await StoreBackends.OpenSqliteAsync(":memory:"), parts, ChordContext.Background);
            Equal<(EntryId?, EntryId?)>((marker, null), await Heads(await StoreStorage.OpenAsync(copy, "harness", StoreContext)), "heads in the copy");
            var replica = await MemoryStoreAsync();
            await Tables.ApplyBatchAsync(replica, await store.Outbox!.ReadAsync(0, 1_000, ChordContext.Background), ChordContext.Background);
            Equal<(EntryId?, EntryId?)>((marker, null), await Heads(await StoreStorage.OpenAsync(replica, "harness", StoreContext)), "heads in the replica");
            foreach (var each in (IStore[])[copy, replica, store]) await each.CloseAsync(ChordContext.Background);
            Directory.Delete(directory, recursive: true);
        });

        // ─── reads (slice 6b §1.4, §2.4) ────────────────────────────────────

        yield return Case("storage.store.reads.a-request-error-inside-a-multi-query-read-rejects-that-read-only-the-store-goes-on", async () =>
        {
            var (store, storage) = await RecordedStoreAsync();
            await ThrowsAsync<StorageRequestException>(() =>
                storage.ScanEntriesAsync(new EntryQuery(new ConversationId(999)), 10, null, StoreContext), "an unknown conversation's scan");
            Equal<StoredDocument?>(null, await storage.GetDocumentAsync(new DocumentId(999), DocumentPoint.Current, StoreContext), "an unknown document");
            Equal(new ConversationRecord(ConversationId.Root), await storage.GetConversationAsync(ConversationId.Root, StoreContext), "root");
            await store.CloseAsync(ChordContext.Background);
        });

        // ─── commits (slice 6b §1.3, §2.5) ──────────────────────────────────

        yield return Case("storage.store.commits.publishes-inside-the-attached-hosts-batch-and-plainly-without-a-host", async () =>
        {
            var store = await MemoryStoreAsync();
            var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
            var plain = new List<bool>();
            storage.Publishing(() => plain.Add(store.Batch is null), StoreContext);
            SessionTests.Seq([true], plain, "without a host");
            // Adapted: a hand-built host batch, attached as the host's open attaches it.
            var batch = new Batch(_ => { }, null);
            store.Attach(new Attachment(_ => { }, batch));
            var events = new List<string>();
            await CreateStoreRoot(storage);
            foreach (var kind in (string[])["a", "b"])
            {
                var id = await storage.MintIdAsync<EntryId>();
                Seq? stored = null;
                await storage.CommitAsync([[new EntryWrite(new EntryRecord { Id = id, ConversationId = ConversationId.Root, Kind = kind })]],
                    (_, seq) => stored = seq, StoreContext);
                storage.Publishing(() =>
                {
                    events.Add($"publish {stored!.Value.Value} running={store.Batch!.Running}");
                    batch.Flushing(_ => events.Add($"flush {stored!.Value.Value}"));
                }, StoreContext);
            }
            var last = store.LastSeq;
            SessionTests.Seq([$"publish {last - 1} running=True", $"flush {last - 1}", $"publish {last} running=True", $"flush {last}"], events, "publications");
            store.Detach();
            await storage.CloseAsync(StoreContext);
            await store.CloseAsync(ChordContext.Background);
        });

        yield return Case("storage.store.commits.counts-four-statements-for-an-entry-append-and-for-a-delta", async () =>
        {
            var counted = await CountedStore.OpenAsync(outbox: false);
            var storage = counted.Storage;
            var entry = new EntryRecord { Id = await storage.MintIdAsync<EntryId>(), ConversationId = ConversationId.Root, Kind = "m" };
            await storage.CommitAsync([[new EntryWrite(entry)]], (_, _) => { }, StoreContext);
            var appended = counted.Statements;
            var id = await storage.MintIdAsync<DocumentId>();
            await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id = id, Kind = "s", Scope = DocumentScope.Session },
                DocumentContent.Base(1, O("{\"n\":0}")))], StoreContext);
            await storage.CommitWritesAsync([new DocumentChangeWrite(id, DocumentContent.Delta(1, OpsOf("[[\"s\",[\"n\"],1]]")))], StoreContext);
            Equal((4L, 4L), (appended, counted.Statements), "statements of an append and of a delta");
            await counted.Store.CloseAsync(ChordContext.Background);
        });

        // ─── review regressions ─────────────────────────────────────────────

        yield return Case("storage.store.review.a-head-marker-entry-whose-kind-is-refused-is-returned-not-taken-for-a-request-error", async () =>
        {
            var storage = await OpenStoreStorageAsync(":memory:");
            await CreateStoreRoot(storage);
            var first = await storage.MintIdAsync<EntryId>();
            var marker = await storage.MintIdAsync<EntryId>();
            await storage.CommitWritesAsync(
            [
                new EntryWrite(new EntryRecord { Id = first, ConversationId = ConversationId.Root, Kind = "message" }),
                new EntryWrite(new EntryRecord { Id = marker, ConversationId = ConversationId.Root, Kind = "refused", Head = first }),
            ], StoreContext);
            Equal<EntryId?>(marker, (await storage.FindLatestHeadMarkerAsync(ConversationId.Root, null, StoreContext))?.Id, "the head marker");
            var page = await storage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 10, null, StoreContext);
            SessionTests.Seq(["refused", "message"], page.Items.Select(item => item.Kind), "kinds");
            await storage.CloseAsync(StoreContext);
        });

        // ─── GC ─────────────────────────────────────────────────────────────

        yield return Case("storage.store.gc.a-closed-storestorage-is-collected-while-its-store-stays-open", async () =>
        {
            var store = await MemoryStoreAsync();
            var reference = await ClosedStoreStorageAsync(store);
            for (var round = 0; round < 10 && reference.IsAlive; round++)
            {
                await Task.Yield();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Check(!reference.IsAlive, "the closed storage is collected");
            await store.CloseAsync(ChordContext.Background);
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ClosedStoreStorageAsync(ChordStore store)
    {
        var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
        await CreateStoreRoot(storage);
        await storage.CloseAsync(StoreContext);
        return new WeakReference(storage);
    }

    private static System.Collections.Immutable.ImmutableArray<PiSharp.Durable.Chord.Delta.Op> OpsOf(string json) =>
        [.. ((JsonArrayValue)JsonValue.Parse(json)).Select(PiSharp.Durable.Chord.Delta.Op.FromJson)];

    /// <summary>A store on <c>:memory:</c> with an outbox from its first commit, and Durable's storage in it with its root.</summary>
    private static async Task<(ChordStore Store, StoreStorage Storage)> RecordedStoreAsync()
    {
        var store = await MemoryStoreAsync(new StoreOptions { Outbox = true });
        var storage = await StoreStorage.OpenAsync(store, "harness", StoreContext);
        await CreateStoreRoot(storage);
        return (store, storage);
    }

    /// <summary>Rewindable history, a retired latest document, a create-plus-retire and a change-plus-retire.</summary>
    private static async Task<(DocumentId Rewindable, List<Seq> Seqs)> StoreHistoryAsync(IStorage storage)
    {
        var scope = DocumentScope.Conversation(ConversationId.Root);
        var rewindable = await storage.MintIdAsync<DocumentId>();
        var latest = await storage.MintIdAsync<DocumentId>();
        var empty = await storage.MintIdAsync<DocumentId>();
        static DocumentBase Base(int n) => DocumentContent.Base(1, O($"{{\"n\":{n}}}"));
        var seqs = new List<Seq>
        {
            await storage.CommitWritesAsync(
            [
                new DocumentCreateWrite(new DocumentCreate { Id = rewindable, Kind = "r", Scope = scope, History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf },
                    Base(0)),
                new DocumentCreateWrite(new DocumentCreate { Id = latest, Kind = "l", Scope = scope, History = DocumentHistory.Latest, Fork = DocumentFork.Current },
                    Base(0)),
            ], StoreContext),
            await storage.CommitWritesAsync([new DocumentChangeWrite(rewindable, DocumentContent.Delta(1, OpsOf("[[\"s\",[\"n\"],1]]")))], StoreContext),
            await storage.CommitWritesAsync(
            [
                new DocumentChangeWrite(latest, DocumentContent.Delta(1, OpsOf("[[\"s\",[\"n\"],2]]"))),
                new DocumentRetireWrite(latest),
                new DocumentCreateWrite(new DocumentCreate { Id = empty, Kind = "e", Scope = DocumentScope.Session }, Base(3)),
                new DocumentRetireWrite(empty),
            ], StoreContext),
        };
        return (rewindable, seqs);
    }

    /// <summary>A storage that closes and reopens its store file after every commit (Durable's reopen harness, upstream
    /// <c>ReopeningStorage</c>).</summary>
    private sealed class ReopeningStoreStorage(IStorage initial, string path) : IStorage
    {
        private IStorage current = initial;
        private bool closed;

        public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
        {
            if (closed) return await current.CommitAsync(batches, receive, context);
            try
            {
                return await current.CommitAsync(batches, receive, context);
            }
            finally
            {
                await current.CloseAsync(StoreContext);
                current = await OpenStoreStorageAsync(path);
            }
        }

        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => current.MintIdAsync<TId>();
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => current.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => current.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            current.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            current.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanEntriesAsync(query, limit, cursor, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => current.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => current.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            current.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            current.FindDocumentAsync(address, at, context);
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => current.GetDocumentAsync(id, at, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanDocumentsAsync(query, limit, cursor, context);

        public async Task CloseAsync(DurableContext context)
        {
            if (closed) return;
            closed = true;
            await current.CloseAsync(context);
        }
    }
}
