using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/engine.test.ts. Every case is ported, over ChordStore
// on TestSqliteDatabase (ChordSupport.cs). The two "counters at the safe-integer limit" cases start from a store near the limit made as
// upstream makes it, with reimported (an export with the manifest's nextId, or a record's revision, edited, imported into a fresh store;
// area cv2-store-archive moved them there from a hand-written chord_meta row and an UPDATE); the stores exported are closed here. A read's
// outcome as a value (upstream's .then(() => "loaded", String)) is the error's message. toMatchObject on a record checks the fields it
// names.
internal static partial class Program
{
    /// <summary>The owner, namespace and kind of the engine cases' documents (upstream <c>OWNER</c>).</summary>
    private static readonly DocumentScope StoreEngineOwner = new("app.store", "chord", "notes");

    /// <summary>upstream <c>LIMIT</c>: Number.MAX_SAFE_INTEGER.</summary>
    private const long StoreEngineLimit = StoreInternal.MaxSafeIntegerLong;

    /// <summary>A store and its helpers (upstream <c>engine</c>'s result).</summary>
    private sealed class StoreEngineFixture(ChordStore store)
    {
        internal ChordStore Store { get; } = store;

        /// <summary>Commits <paramref name="writes"/> and returns the created ids.</summary>
        internal async Task<IReadOnlyList<long>> Commit(params IStagedWrite[] writes)
        {
            IReadOnlyList<long> created = [];
            await Store.CommitAsync(writes, new CommitFence(() => true, (_, ids) => created = ids), ChordContext.Background);
            return created;
        }

        internal static DocumentAddress At(string key) => new(StoreEngineOwner.Owner, StoreEngineOwner.Namespace, StoreEngineOwner.Kind!, key);

        internal static DocumentBase Base(JsonNode value, long version = 1) => new(version, $"f{version}".PadRight(16, '0'), value);

        internal static IStagedWrite Create(string key, string value) =>
            StoreInternal.CreateDocument(At(key), Base(JsonNode.Parse(value)!), ChordJson.Stringify(JsonNode.Parse(value)));

        internal static IStagedWrite Delta(long id, long revision, string ops)
        {
            var parsed = JsonNode.Parse(ops)!.AsArray();
            return StoreInternal.ChangeDocument(id, revision, new DocumentContent.Delta(1, parsed), ChordJson.Stringify(parsed));
        }

        internal Task<T> Read<T>(Func<ISqliteExecutor, Task<T>> read) => Store.ReadAsync(read, ChordContext.Background);

        internal async Task<JsonArray> All(string sql) => StoreSqlJson(await Read(executor => executor.AllAsync(sql)));
    }

    /// <summary>A store on <c>:memory:</c> (or <paramref name="on"/>) and its helpers (upstream <c>engine</c>).</summary>
    private static async Task<StoreEngineFixture> StoreEngine(ChordStore? on = null) => new(on ?? await MemoryStore());

    /// <summary><paramref name="store"/>'s archive with <paramref name="edit"/> applied to its parts, imported into a fresh <c>:memory:</c>
    /// store (upstream <c>reimported</c>).</summary>
    private static async Task<ChordStore> StoreEngineReimported(ChordStore store, Func<List<JsonObject>, IEnumerable<JsonNode?>> edit) =>
        (ChordStore)await Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(":memory:")), edit(await Exported(store)),
            ChordContext.Background);

    /// <summary>A staged write applying <paramref name="apply"/> in the step (an adapter's composite; upstream <c>staged</c>).</summary>
    private static IStagedWrite StoreEngineStaged(Func<Step, Task> apply) => new StagedWrite(WriteOrder.Write, async step =>
    {
        await apply(step);
        return [];
    });

    /// <summary>An adapter's record of <paramref name="history"/> at <paramref name="key"/>, with an explicit id and the step's seq
    /// (upstream <c>record</c>).</summary>
    private static DocumentRecord StoreEngineRecord(long id, string key, DocumentHistory history, long createdAt) => new()
    {
        Id = id,
        Owner = StoreEngineOwner.Owner,
        Namespace = StoreEngineOwner.Namespace,
        Kind = StoreEngineOwner.Kind!,
        Key = key,
        History = history,
        CreatedAt = createdAt,
        Revision = 1,
        Version = 1,
        Fingerprint = "",
    };

    /// <summary>Creates adapter record 10 at "a" (its base <c>{ n: 0 }</c>), then changes and retires it in later commits (upstream
    /// <c>lifetime</c>).</summary>
    private static async Task<StoreEngineFixture> StoreEngineLifetime(DocumentHistory history)
    {
        var engine = await StoreEngine();
        await engine.Commit(StoreEngineStaged(step => DocumentEngine.InsertDocumentAsync(step, StoreEngineRecord(10, "a", history, step.Seq), """{"n":0}""")));
        await engine.Commit(StoreEngineStaged(step =>
            DocumentEngine.WriteContentAsync(step, 10, history, 1, new DocumentContent.Base(1, "", new JsonObject { ["n"] = 1 }), """{"n":1}""")));
        await engine.Commit(StoreEngineStaged(step => DocumentEngine.WriteContentAsync(step, 10, history, 2,
            new DocumentContent.Delta(1, JsonNode.Parse("""[["s",["n"],2]]""")!.AsArray()), """[["s",["n"],2]]""")));
        // An adapter's retirement (Durable's): the record read in the step, retired by its history's rule.
        await engine.Commit(StoreEngineStaged(async step => await DocumentEngine.RetireRecordAsync(step, (await DocumentEngine.ReadDocumentAsync(step, 10))!)));
        return engine;
    }

    /// <summary>toMatchObject on a record: the fields of <paramref name="expected"/> (JSON) in the record's JSON.</summary>
    private static void StoreEngineMatches(string expected, DocumentRecord? record, string what)
    {
        Check(record is not null, $"{what}: expected a record, actual none");
        var actual = record!.ToJson();
        var fields = JsonNode.Parse(expected)!.AsObject();
        var picked = new JsonObject();
        foreach (var (name, _) in fields) picked[name] = actual[name]?.DeepClone();
        Json(expected, picked, what);
    }

    private static IEnumerable<(string, Func<Task>)> StoreEngineCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("store.engine.documents-load-as-their-base-with-the-deltas-after-it-applied-a-new-base-prunes-the-older-revisions", async () =>
            {
                var engine = await StoreEngine();
                var id = (await engine.Commit(StoreEngineFixture.Create("a", """{"n":0}""")))[0];
                await engine.Commit(StoreEngineFixture.Delta(id, 1, """[["s",["n"],1]]"""));
                await engine.Commit(StoreEngineFixture.Delta(id, 2, """[["s",["n"],2]]"""));
                var found = await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a")));
                StoreEngineMatches($$"""{ "id": {{id}}, "revision": 3, "version": 1, "history": "none", "createdAt": 1 }""", found, "the record");
                var loaded = await engine.Read(e => DocumentEngine.LoadDocumentAsync(e, found!));
                Json("""{ "n": 2 }""", loaded.Value, "the loaded value");
                Equal(2, loaded.DeltasSinceBase, "the deltas since the base");
                var next = StoreEngineFixture.Base(new JsonObject { ["m"] = 9 }, 2);
                await engine.Commit(StoreInternal.ChangeDocument(id, 3, new DocumentContent.Base(next.Version, next.Fingerprint, next.Value),
                    ChordJson.Stringify(next.Value)));
                var rebased = await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a")));
                StoreEngineMatches($$"""{ "revision": 4, "version": 2, "fingerprint": "{{next.Fingerprint}}" }""", rebased, "the rebased record");
                var reloaded = await engine.Read(e => DocumentEngine.LoadDocumentAsync(e, rebased!));
                Json("""{ "m": 9 }""", reloaded.Value, "the new base");
                Equal(0, reloaded.DeltasSinceBase, "no delta since it");
                Json("""[{ "seq": 4 }]""", await engine.All($"SELECT seq FROM {StoreInternal.StoreTables.Revisions}"), "the revisions left");
                await engine.Store.CloseAsync(context);
            }),
            Case("store.engine.documents-list-live-keys-in-code-point-order-paging-after-a-key", async () =>
            {
                var engine = await StoreEngine();
                await engine.Commit(StoreEngineFixture.Create("\U0001F600", "{}"), StoreEngineFixture.Create("～", "{}"), StoreEngineFixture.Create("b", "{}"),
                    StoreEngineFixture.Create("a", "{}"));
                var all = new DocumentAddress(StoreEngineOwner.Owner, StoreEngineOwner.Namespace, StoreEngineOwner.Kind!);
                Sequence(["a", "b", "～", "\U0001F600"], await engine.Read(e => StoreInternal.ListDocumentKeysAsync(e, all, null, 10)), "every key");
                Sequence(["～"], await engine.Read(e => StoreInternal.ListDocumentKeysAsync(e, all, "b", 1)), "after b");
                await engine.Store.CloseAsync(context);
            }),
            Case("store.engine.documents-retire-a-none-record-with-its-row-and-revisions-r1-the-address-takes-a-new-incarnation", async () =>
            {
                var engine = await StoreEngine();
                var id = (await engine.Commit(StoreEngineFixture.Create("a", """{"n":0}""")))[0];
                var next = (await engine.Commit(StoreInternal.RetireDocument(id, 1), StoreEngineFixture.Create("a", """{"n":1}""")))[0];
                Equal(3L, next, "the new incarnation's id");
                Json("""[{ "id": 3 }]""", await engine.All($"SELECT id FROM {StoreInternal.StoreTables.Documents}"), "the rows");
                Json("""[{ "document_id": 3 }]""", await engine.All($"SELECT document_id FROM {StoreInternal.StoreTables.Revisions}"), "the revisions");
                StoreEngineMatches("""{ "id": 3 }""", await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a"))), "the address");
                await engine.Store.CloseAsync(context);
            }),
            Case("store.engine.documents-refuse-two-live-records-at-one-address-and-a-delta-on-another-version-the-step-is-refused", async () =>
            {
                var first = await StoreEngine();
                await first.Commit(StoreEngineFixture.Create("a", "{}"));
                var twice = StoreText(await StoreRejection(first.Commit(StoreEngineFixture.Create("a", "{}"))));
                Check(twice.Contains("UNIQUE", StringComparison.Ordinal), $"a second live record: expected UNIQUE, actual <{twice}>");
                var second = await StoreEngine();
                var id = (await second.Commit(StoreEngineFixture.Create("a", "{}")))[0];
                var crossing = StoreInternal.ChangeDocument(id, 1, new DocumentContent.Delta(2, []), "[]");
                var refused = StoreText(await StoreRejection(second.Commit(crossing)));
                Check(refused.Contains("not live at revision 1", StringComparison.Ordinal), $"a delta on another version: actual <{refused}>");
                Equal(1, await second.Read(_ => Task.FromResult(1)), "the store goes on");
                await first.Store.CloseAsync(context);
                await second.Store.CloseAsync(context);
            }),
            Case("store.engine.history-rules-r1-and-reads-at-a-point-keep-a-retired-latest-record-s-row-without-revisions-and-every-revision-of-a-rewindable-one", async () =>
            {
                var latest = await StoreEngineLifetime(DocumentHistory.Latest);
                Json("""[{ "id": 10, "retired_at": 4 }]""", await latest.All($"SELECT id, retired_at FROM {StoreInternal.StoreTables.Documents}"), "the latest row");
                Json("[]", await latest.All($"SELECT seq FROM {StoreInternal.StoreTables.Revisions}"), "no latest revision");
                var rewindable = await StoreEngineLifetime(DocumentHistory.Rewindable);
                Json("""[{ "seq": 1, "kind": "base" }, { "seq": 2, "kind": "base" }, { "seq": 3, "kind": "delta" }]""",
                    await rewindable.All($"SELECT seq, kind FROM {StoreInternal.StoreTables.Revisions} ORDER BY seq"), "every rewindable revision");
                await latest.Store.CloseAsync(context);
                await rewindable.Store.CloseAsync(context);
            }),
            Case("store.engine.history-rules-r1-and-reads-at-a-point-find-a-record-at-a-point-by-its-half-open-lifetime-and-load-a-rewindable-one-at-any-point-inside-it", async () =>
            {
                var engine = await StoreEngineLifetime(DocumentHistory.Rewindable);
                DocumentPoint[] points = [0, 1, 3, 4, DocumentPoint.Current];
                var found = new List<DocumentRecord?>();
                foreach (var point in points) found.Add(await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a"), point)));
                Sequence([null, 10L, 10L, null, null], found.Select(each => each?.Id), "the record at each point");
                var record = found[1]!;
                Equal(4L, record.RetiredAt, "its retirement");
                foreach (var (point, n) in new (long, int)[] { (1, 0), (2, 1), (3, 2) })
                {
                    var loaded = await engine.Read(e => DocumentEngine.LoadDocumentAsync(e, record, point));
                    Json($$"""{ "n": {{n}} }""", loaded.Value, $"the value at {point}");
                    Equal(1L, loaded.Version, $"the version at {point}");
                }
                Sequence([10L], (await engine.Read(e => DocumentEngine.ListDocumentsAsync(e, StoreEngineOwner, 3, 0, 10))).Select(each => each.Id), "listed at 3");
                Equal(0, (await engine.Read(e => DocumentEngine.ListDocumentsAsync(e, StoreEngineOwner, DocumentPoint.Current, 0, 10))).Count, "none current");
                await engine.Store.CloseAsync(context);
            }),
            Case("store.engine.history-rules-r1-and-reads-at-a-point-find-a-retired-latest-record-at-a-point-whose-content-is-gone", async () =>
            {
                var engine = await StoreEngineLifetime(DocumentHistory.Latest);
                var found = await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a"), 2));
                StoreEngineMatches("""{ "id": 10, "retiredAt": 4, "history": "latest" }""", found, "the record at 2");
                // A read returns data (the store stops on a read's throw, slice 6b §1.4): the outcome as a value.
                var outcome = await engine.Read(async e =>
                {
                    try
                    {
                        await DocumentEngine.LoadDocumentAsync(e, found!, 2);
                        return "loaded";
                    }
                    catch (Exception error)
                    {
                        return error.Message;
                    }
                });
                Check(outcome.Contains("no base", StringComparison.Ordinal), $"the content is gone: actual <{outcome}>");
                await engine.Store.CloseAsync(context);
            }),
            Case("store.engine.history-rules-r1-and-reads-at-a-point-insert-a-create-plus-retire-as-an-empty-lifetime-that-never-holds-the-live-address", async () =>
            {
                var engine = await StoreEngine();
                var live = (await engine.Commit(StoreEngineFixture.Create("a", "{}")))[0];
                await engine.Commit(StoreEngineStaged(step =>
                    DocumentEngine.InsertDocumentAsync(step, StoreEngineRecord(20, "a", DocumentHistory.Latest, step.Seq) with { RetiredAt = step.Seq }, "{}")));
                StoreEngineMatches($$"""{ "id": {{live}} }""", await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a"))), "the live address");
                Json("[]", await engine.All($"SELECT document_id FROM {StoreInternal.StoreTables.Revisions} WHERE document_id = 20"), "no revision of the empty lifetime");
                await engine.Store.CloseAsync(context);
            }),
            Case("store.engine.counters-at-the-safe-integer-limit-allocate-document-ids-up-to-the-safe-range-after-an-import-near-it-past-it-the-commit-is-refused", async () =>
            {
                await using var source = (await StoreEngine()).Store;
                Task<ChordStore> Near(long next) => StoreEngineReimported(source, parts =>
                {
                    parts[0]["nextId"] = next;
                    return parts;
                });
                var engine = await StoreEngine(await Near(StoreEngineLimit - 1));
                Sequence([StoreEngineLimit - 1], await engine.Commit(StoreEngineFixture.Create("a", """{"n":0}""")), "the last id");
                Equal(StoreEngineLimit, engine.Store.NextId, "the allocator at the bound");
                var past = StoreText(await StoreRejection(engine.Commit(StoreEngineFixture.Create("b", """{"n":0}"""))));
                Check(past.Contains("ID space is exhausted", StringComparison.Ordinal), $"past it: actual <{past}>");
                // Two creations in one commit where only one id is left: nothing of it is written.
                var both = await StoreEngine(await Near(StoreEngineLimit - 1));
                var two = StoreText(await StoreRejection(both.Commit(StoreEngineFixture.Create("a", """{"n":0}"""), StoreEngineFixture.Create("b", """{"n":0}"""))));
                Check(two.Contains("ID space is exhausted", StringComparison.Ordinal), $"two creations: actual <{two}>");
                Sequence([0L, StoreEngineLimit - 1], [both.Store.LastSeq, both.Store.NextId], "nothing written");
                await engine.Store.CloseAsync(context);
                await both.Store.CloseAsync(context);
            }),
            Case("store.engine.counters-at-the-safe-integer-limit-count-revisions-up-to-the-safe-range-after-an-import-near-it-a-change-past-it-is-refused", async () =>
            {
                var seed = await StoreEngine();
                await using var source = seed.Store;
                await seed.Commit(StoreEngineFixture.Create("a", """{"n":0}"""));
                var engine = await StoreEngine(await StoreEngineReimported(source, parts =>
                {
                    foreach (var part in parts.Where(part => (string)part["kind"]! == "document.create")) part["record"]!["revision"] = StoreEngineLimit - 1;
                    return parts;
                }));
                var found = (await engine.Read(e => DocumentEngine.FindDocumentAsync(e, StoreEngineFixture.At("a"))))!;
                await engine.Commit(StoreEngineFixture.Delta(found.Id, StoreEngineLimit - 1, """[["s",["n"],1]]"""));
                var past = StoreText(await StoreRejection(engine.Commit(StoreEngineFixture.Delta(found.Id, StoreEngineLimit, """[["s",["n"],2]]"""))));
                Check(past.Contains("safe range", StringComparison.Ordinal), $"a change past it: actual <{past}>");
                await engine.Store.CloseAsync(context);
            }),
        ];
    }
}
