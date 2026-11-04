using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/apply-batch.test.ts. Every case is ported, over ChordStore
// on TestSqliteDatabase (ChordSupport.cs), with two changes of form. "the same archive, manifest included" compares exported(replica) with
// exported(source); export comes with area cv2-store-archive, so here the replica's database is compared with the source's instead: every
// table but chord_meta (whose outbox switch differs) and chord_outbox (the replica has none), each table's rows as a set, every index's
// SQL, and lastSeq and nextId (the manifest's position). That compares more than an archive carries (retired records, every revision,
// the catalog's records). "a claim is refused while it applies" tries a claim between microtask turns until the batch applied; here the
// claim is tried before each statement the batch's transaction runs (the database's OnTransactionStatement), which is while it applies,
// and the first refusal is kept. structuredClone of commits is a copy of their JSON.
internal static partial class Program
{
    private static readonly TableDefinition StoreBatchCountsV1 = Tables.DefineTable("counts", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("n", Int()), ("tag", Str())),
        Key = ["id"],
        Indexes = StoreIndexes(("by_tag", ["tag"])),
    });

    private static readonly TableDefinition StoreBatchCountsV2 = Tables.EvolveTable(StoreBatchCountsV1, t => t.Add("extra", Int(@default: 0)).DropIndex("by_tag"));

    /// <summary>A store with an outbox from its first commit holding every write kind: an archive store, then counted writes (upstream
    /// <c>everyKind</c>).</summary>
    private static async Task<ChordStore> StoreBatchEveryKind()
    {
        var context = ChordContext.Background;
        var open = CommitFence.Open;
        var store = await ArchiveStore(outbox: true);
        var counts = await TableIn(store, StoreBatchCountsV1);
        var staged = new List<IStagedWrite>();
        var changes = new TableChanges(counts, staged);
        for (var i = 0; i < 10; i++) changes.Put(new JsonObject { ["id"] = $"c{i}", ["n"] = i, ["tag"] = i % 2 == 0 ? "even" : "odd" });
        await store.CommitAsync(staged, open, context);
        var bulk = new List<IStagedWrite>();
        var more = new TableChanges(counts, bulk);
        more.Update(StoreValue("""{"id":"c1"}"""), c => StorePatch(("n", Add(c["n"], 10))));
        more.Delete(StoreValue("""{"id":"c2"}"""));
        more.UpdateWhere(c => Prefix(c["tag"], "ev"), StoreValue("""{"tag":"EVEN"}"""));
        more.DeleteWhere(c => Lt(c["n"], 1));
        await store.CommitAsync(bulk, open, context);
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(StoreBatchCountsV2, "app.store:counts")], store, open, "app.store", context);
        var created = new List<long>();
        var fence = new CommitFence(() => true, (_, ids) => created.AddRange(ids));
        var address = new DocumentAddress("app.store", "chord", "replayed", "x");
        await store.CommitAsync([StoreInternal.CreateDocument(address, new DocumentBase(1, "f1", new JsonObject { ["n"] = 0 }), """{"n":0}""")], fence, context);
        var ops = new JsonArray(new JsonArray("s", new JsonArray("n"), 1));
        await store.CommitAsync([StoreInternal.ChangeDocument(created[0], 1, new DocumentContent.Delta(1, ops), ChordJson.Stringify(ops))], open, context);
        await store.CommitAsync([StoreInternal.RetireDocument(created[0], 2)], open, context);
        await StoreBatchDurableShaped(store);
        return store;
    }

    /// <summary>What only an adapter writes (Durable's commits): a rewindable creation retired in its own commit (an empty lifetime), and a
    /// change with the retirement of the same record in one commit (upstream <c>durableShaped</c>).</summary>
    private static async Task StoreBatchDurableShaped(ChordStore store)
    {
        var context = ChordContext.Background;
        var @base = new DocumentBase(1, "", new JsonObject { ["n"] = 0 });
        DocumentRecord Made(long id, string key, long seq, bool retired) => new()
        {
            Id = id,
            Owner = "app.durable",
            Namespace = "session",
            Kind = "notes",
            Key = key,
            History = DocumentHistory.Rewindable,
            CreatedAt = seq,
            RetiredAt = retired ? seq : null,
            Meta = "{}",
            Revision = 1,
            Version = 1,
            Fingerprint = "",
        };
        IStagedWrite Create(string key, bool retired) => new StagedWrite(WriteOrder.Write, async step =>
        {
            var record = Made(step.Ids.Mint(), key, step.Seq, retired);
            await DocumentEngine.InsertDocumentAsync(step, record, """{"n":0}""");
            return [new DocumentWrite.Create(record, @base)];
        });
        await store.CommitAsync([Create("empty", true), Create("kept", false)], CommitFence.Open, context);
        var kept = (long)(await store.ReadAsync(executor => executor.GetAsync("SELECT id FROM chord_documents WHERE key = 'kept'"), context))!["id"]!;
        var ops = new JsonArray(new JsonArray("s", new JsonArray("n"), 1));
        var changeAndRetire = new StagedWrite(WriteOrder.Write, async step =>
        {
            var record = (await DocumentEngine.ReadDocumentAsync(step, kept))!;
            var content = new DocumentContent.Delta(1, ops);
            await DocumentEngine.WriteContentAsync(step, kept, DocumentHistory.Rewindable, 1, content, ChordJson.Stringify(ops));
            await DocumentEngine.RetireRecordAsync(step, record);
            return [new DocumentWrite.Change(kept, 1, content), new DocumentWrite.Retire(kept, 2)];
        });
        await store.CommitAsync([changeAndRetire], CommitFence.Open, context);
    }

    /// <summary>Every commit of <paramref name="store"/>'s outbox (upstream <c>commits</c>).</summary>
    private static async Task<List<OutboxCommit>> StoreBatchCommits(ChordStore store) =>
        [.. await store.Outbox!.ReadAsync(store.Outbox.Floor, 10_000, ChordContext.Background)];

    /// <summary>A copy of <paramref name="commits"/> (upstream's structuredClone).</summary>
    private static List<OutboxCommit> StoreBatchCopy(IEnumerable<OutboxCommit> commits) =>
        [.. commits.Select(commit => commit with { Writes = (JsonArray)commit.Writes.DeepClone() })];

    /// <summary>Adds one to the recorded count of every <c>updateWhere</c> of <paramref name="commit"/>.</summary>
    private static void StoreBatchMiscount(OutboxCommit commit)
    {
        foreach (var write in commit.Writes)
        {
            if ((string?)write!["kind"] != "updateWhere") continue;
            write["count"] = double.Parse(write["count"]!.ToJsonString(), CultureInfo.InvariantCulture) + 1;
        }
    }

    /// <summary>The store's database as the replay case compares it: every table but chord_meta and chord_outbox, by name, its rows as a
    /// sorted set of their JSON; every index's SQL.</summary>
    private static async Task<JsonObject> StoreBatchDump(ChordStore store)
    {
        var context = ChordContext.Background;
        var dump = new JsonObject();
        var tables = await store.ReadAsync(executor => executor.AllAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"), context);
        foreach (var table in tables.Select(row => (string)row["name"]!))
        {
            if (table is StoreInternal.StoreTables.Meta or StoreInternal.StoreTables.Outbox) continue;
            var rows = await store.ReadAsync(executor => executor.AllAsync($"SELECT * FROM \"{table}\""), context);
            var texts = rows.Select(row => StoreSqlJson(row)!.ToJsonString()).Order(StringComparer.Ordinal);
            dump[table] = new JsonArray([.. texts.Select(text => (JsonNode?)JsonValue.Create(text))]);
        }
        var indexes = await store.ReadAsync(executor => executor.AllAsync(
            "SELECT name, tbl_name, sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name"), context);
        dump["indexes"] = StoreSqlJson(indexes);
        return dump;
    }

    private static IEnumerable<(string, Func<Task>)> StoreApplyBatchCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("store.apply-batch.replay-a-store-s-outbox-replayed-into-an-empty-store-gives-the-same-archive-manifest-included", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                var kinds = all.SelectMany(commit => commit.Writes.Select(write => (string)write!["kind"]!)).Distinct().Order(StringComparer.Ordinal);
                Sequence(new[]
                {
                    "alter", "document.change", "document.create", "document.retire", "dropIndex", "index", "put", "table", "update", "updateWhere", "delete",
                    "deleteWhere",
                }.Order(StringComparer.Ordinal), kinds, "every write kind");
                await using var replica = await MemoryStore();
                await Tables.ApplyBatchAsync(replica, all[..3], context);
                await Tables.ApplyBatchAsync(replica, all[3..], context);
                Json((await StoreBatchDump(source)).ToJsonString(), await StoreBatchDump(replica), "the replica's data");
                Sequence([source.LastSeq, source.NextId], [replica.LastSeq, replica.NextId], "the position");
            }),
            Case("store.apply-batch.replay-restores-the-allocator-a-replica-mints-above-every-id-it-holds", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                await using var replica = await MemoryStore();
                await Tables.ApplyBatchAsync(replica, await StoreBatchCommits(source), context);
                Equal(source.NextId, replica.MintId(), "the replica's next mint");
            }),
            Case("store.apply-batch.replay-writes-the-applied-commits-to-the-replica-s-own-outbox-when-it-has-one", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                await using var replica = await MemoryStore(outbox: true);
                await Tables.ApplyBatchAsync(replica, all, context);
                Json(StoreOutboxJson(all).ToJsonString(), StoreOutboxJson(await StoreBatchCommits(replica)), "the replica's outbox");
            }),
            Case("store.apply-batch.refusals-while-an-owner-holds-a-claim-on-the-store-its-memory-would-go-stale", async () =>
            {
                await using var replica = await MemoryStore();
                replica.Claims.Documents("app.store", "chord", new StoreClaimant("an object"));
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, [], context)), "invalid", "args", "a claim held");
            }),
            Case("store.apply-batch.refusals-a-claim-taken-between-the-call-and-its-turn-in-the-queue-refuses-the-batch-a-claim-is-refused-while-it-applies", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                await using var replica = await StoreOver(db);
                var held = await replica.TakeAsync(() => { });
                var replaying = Tables.ApplyBatchAsync(replica, [], context);
                var owner = new StoreClaimant("an object");
                replica.Claims.Documents("app.store", "chord", owner);
                held();
                Rejected(await StoreRejection(replaying), "invalid", "args", "a claim taken before its turn");
                Equal(0L, replica.LastSeq, "nothing applied");
                replica.Claims.Release(owner);
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                // A claim made and released in one turn holds nothing; once the batch applies, one is refused.
                replica.Claims.Documents("app.store", "chord", owner);
                replica.Claims.Release(owner);
                Exception? refused = null;
                db.OnTransactionStatement = _ =>
                {
                    if (refused is not null) return;
                    try
                    {
                        replica.Claims.Documents("app.store", "chord", owner);
                        replica.Claims.Release(owner);
                    }
                    catch (Exception error)
                    {
                        refused = error;
                    }
                };
                await Tables.ApplyBatchAsync(replica, all, context);
                db.OnTransactionStatement = null;
                Check(refused is ChordError { Code: "invalid", Reason: "args" }, $"a claim while it applies: expected invalid/args, actual {refused}");
                Equal(all[^1].Seq, replica.LastSeq, "applied");
                replica.Claims.Documents("app.store", "chord", owner);
            }),
            Case("store.apply-batch.refusals-a-batch-queued-behind-one-that-diverges-is-refused-at-its-turn", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                var diverging = StoreBatchCopy(all);
                foreach (var commit in diverging) StoreBatchMiscount(commit);
                await using var replica = await MemoryStore();
                var first = StoreRejection(Tables.ApplyBatchAsync(replica, diverging, context));
                var second = StoreRejection(Tables.ApplyBatchAsync(replica, all, context));
                var diverged = StoreText(await first);
                Check(diverged.Contains("recorded", StringComparison.Ordinal), $"the diverging batch: actual <{diverged}>");
                Rejected(await second, "invalid", "args", "the batch behind it");
                Equal(0L, replica.LastSeq, "nothing applied");
            }),
            Case("store.apply-batch.refusals-a-commit-whose-next-id-is-not-above-a-document-it-creates-a-saturated-id-aside", async () =>
            {
                await using var replica = await MemoryStore();
                var create = JsonNode.Parse("""
                    { "kind": "document.create",
                      "record": { "id": 100, "owner": "app.store", "namespace": "chord", "kind": "x", "history": "rewindable", "createdAt": 1,
                                  "revision": 1, "version": 1, "fingerprint": "" },
                      "base": { "version": 1, "fingerprint": "", "value": {} } }
                    """)!.AsObject();
                OutboxCommit Commit(long seq, long nextId, JsonObject write) => new(seq, nextId, new JsonArray(write.DeepClone()));
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, [Commit(1, 2, create)], context)), "invalid", "args", "next id 2");
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, [Commit(1, 100, create)], context)), null, "args", "next id 100");
                await Tables.ApplyBatchAsync(replica, [Commit(1, 101, create)], context);
                var saturated = (JsonObject)create.DeepClone();
                saturated["record"]!["id"] = StoreInternal.MaxSafeIntegerLong;
                saturated["record"]!["key"] = "max";
                await Tables.ApplyBatchAsync(replica, [Commit(2, StoreInternal.MaxSafeIntegerLong, saturated)], context);
                Equal(2L, replica.LastSeq, "both applied");
            }),
            Case("store.apply-batch.refusals-a-first-commit-that-does-not-follow-lastseq-or-one-that-does-not-follow-the-one-before-nothing-applied", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                await using var replica = await MemoryStore();
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, all[1..], context)), null, "args", "a first commit after lastSeq + 1");
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, [all[0], all[2]], context)), null, "args", "a gap");
                Equal(0L, replica.LastSeq, "nothing applied");
                await Tables.ApplyBatchAsync(replica, all, context);
                Equal(all[^1].Seq, replica.LastSeq, "applied");
            }),
            Case("store.apply-batch.refusals-a-write-the-records-refuse-rolls-the-batch-back-and-the-store-goes-on", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                await using var replica = await MemoryStore();
                var broken = StoreBatchCopy(all);
                broken[0].Writes[0]!["table"] = "no:such:table";
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, broken, context)), "invalid", "args", "a table name the records refuse");
                var forged = StoreBatchCopy(all);
                forged[0].Writes.Add(JsonNode.Parse("""{ "kind": "put", "table": "x", "row": {}, "version": 0 }"""));
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, forged, context)), "invalid", "args", "a forged write");
                await Tables.ApplyBatchAsync(replica, all, context);
            }),
            Case("store.apply-batch.refusals-a-recorded-count-the-store-does-not-reproduce-stops-applying-and-marks-the-store-diverged-s17", async () =>
            {
                await using var source = await StoreBatchEveryKind();
                var all = await StoreBatchCommits(source);
                var index = all.FindIndex(commit => commit.Writes.Any(write => (string?)write!["kind"] == "updateWhere"));
                var diverging = StoreBatchCopy(all);
                var write = diverging[index].Writes.First(write => (string?)write!["kind"] == "updateWhere")!;
                write["count"] = double.Parse(write["count"]!.ToJsonString(), CultureInfo.InvariantCulture) + 1;
                await using var replica = await MemoryStore();
                var diverged = StoreText(await StoreRejection(Tables.ApplyBatchAsync(replica, diverging, context)));
                Check(diverged.Contains("recorded", StringComparison.Ordinal), $"the diverging batch: actual <{diverged}>");
                Rejected(await StoreRejection(Tables.ApplyBatchAsync(replica, all, context)), "invalid", "args", "every later batch");
                Equal(0L, replica.LastSeq, "nothing applied");
            }),
        ];
    }
}
