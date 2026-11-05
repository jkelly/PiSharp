using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/outbox.test.ts. Every case is ported, over ChordStore
// and TestSqliteDatabase (ChordSupport.cs). A store on a file lives in a fresh temporary directory, deleted after its case (upstream's
// mkdtemp and afterEach). Upstream's replaced db.transaction (a trim whose commit has not returned) and db.all (a read in flight) are the
// database's AfterTransaction and BeforeAll seams. toEqual on commits compares their JSON (key order ignored); toMatchObject on the
// recorded creation checks the fields it names.
internal static partial class Program
{
    /// <summary>The address of the outbox cases' documents (upstream <c>ADDRESS</c>), with <paramref name="key"/>.</summary>
    private static DocumentAddress StoreOutboxAddress(string key = "") => new("app.store", "chord", "notes", key);

    /// <summary>The base of the outbox cases' documents (upstream <c>BASE</c>).</summary>
    private static DocumentBase StoreOutboxBase() => new(1, "0000000000000000", new JsonObject { ["n"] = 0 });

    /// <summary>Creates the singleton at the address (with <paramref name="key"/>) in one commit (upstream <c>created</c>).</summary>
    private static async Task StoreOutboxCreated(ChordStore store, string key = "")
    {
        var @base = StoreOutboxBase();
        await store.CommitAsync([StoreInternal.CreateDocument(StoreOutboxAddress(key), @base, ChordJson.Stringify(@base.Value))], CommitFence.Open,
            ChordContext.Background);
    }

    /// <summary>Runs <paramref name="run"/> with a store opener on a file in a fresh directory (upstream <c>onFile</c>), deleting the
    /// directory after it.</summary>
    private static async Task StoreOutboxOnFile(Func<Func<bool?, Task<ChordStore>>, string, Task> run)
    {
        var directory = Directory.CreateTempSubdirectory("chord-outbox-");
        try
        {
            var path = Path.Combine(directory.FullName, "store.sqlite");
            await run(outbox => StoreOver(new TestSqliteDatabase(path), outbox), path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Commits as JSON, for toEqual.</summary>
    private static JsonArray StoreOutboxJson(IEnumerable<OutboxCommit> commits) => new([.. commits.Select(commit => (JsonNode)commit.ToJson())]);

    private static IEnumerable<(string, Func<Task>)> StoreOutboxCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("store.outbox.the-outbox-is-off-for-a-new-store-on-from-the-seq-it-is-turned-on-at-kept-when-omitted-dropped-when-turned-off", () =>
                StoreOutboxOnFile(async (open, _) =>
                {
                    var store = await open(null);
                    await StoreOutboxCreated(store);
                    await store.CloseAsync(context);
                    store = await open(true);
                    Equal(1L, store.Outbox?.Floor, "the floor it is turned on at");
                    const long id = 2;
                    var ops = new JsonArray(new JsonArray("s", new JsonArray("n"), 2));
                    await store.CommitAsync([StoreInternal.ChangeDocument(id, 1, new DocumentContent.Delta(1, ops), ChordJson.Stringify(ops))],
                        CommitFence.Open, context);
                    await store.CloseAsync(context);
                    store = await open(null);
                    Json("""
                        [{ "seq": 2, "nextId": 3,
                           "writes": [{ "kind": "document.change", "id": 2, "revision": 1, "content": { "kind": "delta", "version": 1, "ops": [["s", ["n"], 2]] } }] }]
                        """, StoreOutboxJson(await store.Outbox!.ReadAsync(1, 10, context)), "kept when omitted");
                    Rejected(await StoreRejection(store.Outbox!.ReadAsync(0, 10, context)), "invalid", "args", "a read before the floor");
                    Rejected(await StoreRejection(store.Outbox!.ReadAsync(3, 10, context)), "invalid", "args", "a read after lastSeq");
                    await store.CloseAsync(context);
                    store = await open(false);
                    Equal(null, store.Outbox, "turned off");
                    await store.CloseAsync(context);
                })),
            Case("store.outbox.the-outbox-records-creations-with-their-ids-and-seqs-trim-is-idempotent-and-refuses-beyond-lastseq", async () =>
            {
                await using var store = await MemoryStore(outbox: true);
                await StoreOutboxCreated(store);
                var commit = (await store.Outbox!.ReadAsync(0, 10, context))[0];
                var write = commit.Writes[0]!;
                Equal("document.create", (string?)write["kind"], "the kind");
                var record = write["record"]!;
                Json("""{ "id": 2, "createdAt": 1, "namespace": "chord", "owner": "app.store", "kind": "notes", "key": "" }""",
                    new JsonObject
                    {
                        ["id"] = record["id"]?.DeepClone(),
                        ["createdAt"] = record["createdAt"]?.DeepClone(),
                        ["namespace"] = record["namespace"]?.DeepClone(),
                        ["owner"] = record["owner"]?.DeepClone(),
                        ["kind"] = record["kind"]?.DeepClone(),
                        ["key"] = record["key"]?.DeepClone(),
                    }, "the record's fields");
                Json("""{ "version": 1, "fingerprint": "0000000000000000", "value": { "n": 0 } }""", write["base"], "the base");
                await store.Outbox.TrimAsync(1, context);
                await store.Outbox.TrimAsync(1, context);
                Equal(1L, store.Outbox.Floor, "the floor");
                Equal(0, (await store.Outbox.ReadAsync(1, 10, context)).Count, "nothing after the floor");
                Rejected(await StoreRejection(store.Outbox.TrimAsync(2, context)), "invalid", "args", "a trim beyond lastSeq");
            }),
            Case("store.outbox.the-outbox-overlapping-trims-run-one-at-a-time-the-stored-floor-never-moves-back-and-equals-the-one-in-memory", () =>
                StoreOutboxOnFile(async (open, _) =>
                {
                    var store = await open(true);
                    for (var i = 0; i < 11; i++) await StoreOutboxCreated(store, $"k{i}");
                    await Task.WhenAll(store.Outbox!.TrimAsync(9, context), store.Outbox.TrimAsync(3, context));
                    Equal(9L, store.Outbox.Floor, "the floor in memory");
                    await store.CloseAsync(context);
                    store = await open(null);
                    Equal(9L, store.Outbox!.Floor, "the stored floor");
                    Rejected(await StoreRejection(store.Outbox.ReadAsync(3, 10, context)), "invalid", "args", "a read before the floor");
                    Sequence([10L, 11L], (await store.Outbox.ReadAsync(9, 10, context)).Select(commit => commit.Seq), "the commits after it");
                    await store.CloseAsync(context);
                })),
            Case("store.outbox.the-outbox-a-read-waits-for-a-trim-whose-commit-has-not-returned-the-trimmed-floor-refuses-it-never-an-empty-page", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var store = await StoreOver(db, outbox: true);
                await StoreOutboxCreated(store);
                // The trim's DELETE committed; its transaction has not returned (the floor in memory is still 0).
                db.AfterTransaction = async () =>
                {
                    entered.TrySetResult();
                    await gate.Task;
                };
                var trim = store.Outbox!.TrimAsync(1, context);
                await entered.Task;
                db.AfterTransaction = null;
                var read = StoreRejection(store.Outbox.ReadAsync(0, 100, context));
                gate.TrySetResult();
                await trim;
                Rejected(await read, "invalid", "args", "the read after the trimmed floor");
                await store.CloseAsync(context);
            }),
            Case("store.outbox.the-outbox-a-trim-waits-for-a-read-in-flight-the-read-gets-the-commits-it-was-admitted-for", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                var store = await StoreOver(db, outbox: true);
                await StoreOutboxCreated(store);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                db.BeforeAll = async () =>
                {
                    entered.TrySetResult();
                    await gate.Task;
                };
                var read = store.Outbox!.ReadAsync(0, 100, context);
                await entered.Task;
                db.BeforeAll = null;
                var trim = store.Outbox.TrimAsync(1, context);
                gate.TrySetResult();
                Sequence([1L], (await read).Select(commit => commit.Seq), "the read's commits");
                await trim;
                Equal(1L, store.Outbox.Floor, "the floor");
                await store.CloseAsync(context);
            }),
            Case("store.outbox.the-outbox-a-commit-stored-in-a-newer-format-is-refused-unavailable-the-data-stays", () =>
                StoreOutboxOnFile(async (open, path) =>
                {
                    var store = await open(true);
                    await StoreOutboxCreated(store);
                    await store.CloseAsync(context);
                    var db = new TestSqliteDatabase(path);
                    await db.RunAsync($"UPDATE {StoreInternal.StoreTables.Outbox} SET format = ? WHERE seq = 1", StoreInternal.OutboxFormat + 1);
                    await db.CloseAsync();
                    store = await open(null);
                    Rejected(await StoreRejection(store.Outbox!.ReadAsync(0, 10, context)), "unavailable", null, "the newer format",
                        $"format {StoreInternal.OutboxFormat + 1}");
                    Rejected(await StoreRejection(store.Outbox.ReadAsync(0, 10, context)), "unavailable", null, "again: the data stays");
                    await store.CloseAsync(context);
                })),
        ];
    }
}
