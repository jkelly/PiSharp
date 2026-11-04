using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table-migration.test.ts, over TestStore
// (TableSupport.cs) with its outbox for upstream's memoryStore({ outbox: true }); each case closes its store (upstream's afterEach); the
// it.each rows of "refusals" are a case each. Ported: every case but the chunk yield's first, "yields between chunks without Node's
// setImmediate (Workers, browsers), through a message, not a timer", which deletes globalThis.setImmediate to take the MessageChannel path
// of upstream's macrotask: .NET has one yield (Task.Yield, TableMigration.cs) and no runtime without it. The chunk yield's second, the
// latency guard, runs the migration on a single-threaded synchronization context (StoreMigrationPump) with a callback posted before it, as
// upstream's setTimeout(0) is due before the migration starts: the callback runs between chunks only because the migration yields (a
// loop of completed awaits would run it after the migration returned). Its spy on the migration's setTimeout calls has no counterpart
// (Task.Yield is no timer). A data step's thrown Error is an exception of the patch's own (InvalidOperationException here), checked by
// its message as toMatchObject({ message }) does. expect.objectContaining parts are checked field by field.
internal static partial class Program
{
    private const string StoreMigrationTable = "app.store:edits";

    private static readonly TableDefinition StoreMigrationEditsV1 = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("file", Str()), ("at", Int())),
        Key = ["id"],
        Indexes = StoreIndexes(("by_file", ["file"])),
    });

    private static readonly TableDefinition StoreMigrationEditsV2 = Tables.EvolveTable(StoreMigrationEditsV1, t => t
        .Rename("file", "path")
        .Add("dir", Str(@default: ""))
        .Rows(row =>
        {
            var path = (string)row["path"]!;
            return new JsonObject { ["dir"] = path[..path.LastIndexOf('/')] };
        }));

    private static readonly TableDefinition StoreMigrationEditsV3 = Tables.EvolveTable(StoreMigrationEditsV2, t => t.Drop("dir").Index("by_path", ["path", "at"]));

    /// <summary>Prepares <paramref name="definition"/> as the edits table of <paramref name="store"/>; a handle to it.</summary>
    private static async Task<TableHandle> StoreMigrationPrepared(TestStore store, TableDefinition definition)
    {
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(definition, StoreMigrationTable)], store, CommitFence.Open, "app.store", ChordContext.Background);
        return new TableHandle(definition, StoreMigrationTable, store, TableOwner());
    }

    /// <summary>A store whose edits table is at version 1 with <paramref name="count"/> rows e000.. (file d&lt;i % 3&gt;/f&lt;i&gt;, at i).</summary>
    private static async Task<TestStore> StoreMigrationFilled(int count)
    {
        var store = await MemoryStore(outbox: true);
        var edits = await StoreMigrationPrepared(store, StoreMigrationEditsV1);
        await StoreCommitWith(store, edits, changes =>
        {
            for (var i = 0; i < count; i++) changes.Put(new JsonObject { ["id"] = StoreMigrationId(i), ["file"] = $"d{i % 3}/f{i}", ["at"] = i });
        });
        return store;
    }

    private static string StoreMigrationId(int i) => "e" + i.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>The kinds of the writes of every commit after <paramref name="seq"/>, each kind with its count.</summary>
    private static async Task<List<string>> StoreMigrationCommitsAfter(TestStore store, long seq)
    {
        var commits = await store.OutboxAsync(seq, 100, ChordContext.Background);
        return [.. commits.Select(commit => string.Join(", ", commit["writes"]!.AsArray()
            .GroupBy(write => (string)write!["kind"]!)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key} x{group.Count()}")))];
    }

    /// <summary>Every row of <paramref name="handle"/>, in key order, as JSON.</summary>
    private static async Task<JsonArray> StoreMigrationAll(TableHandle handle, TableDefinition definition)
    {
        var rows = new List<IReadOnlyDictionary<string, JsonNode?>>();
        await foreach (var row in handle.Rows(Tables.Query(definition), ChordContext.Background)) rows.Add(row);
        return StoreJson(rows);
    }

    /// <summary>The kind, version and state of a write: <c>expect.objectContaining({ kind, version, state })</c>'s fields.</summary>
    private static string StoreMigrationWriteHead(JsonNode? write) => $"{write!["kind"]} {write["version"]} {write["state"]}";

    private static IEnumerable<(string, Func<Task>)> StoreTableMigrationCases()
    {
        var context = ChordContext.Background;
        var (v1, v2, v3) = (StoreMigrationEditsV1, StoreMigrationEditsV2, StoreMigrationEditsV3);
        const string table = StoreMigrationTable;
        var refusals = new (string Case, Func<TableDefinition> Definition, string Message)[]
        {
            ("a-newer-version-recorded", () => v1, "newer than the code's 1"),
            ("a-version-below-the-history-s-base",
                () => Tables.DefineTable("edits", new TableOptions { Version = 7, Columns = StoreColumns(("id", Str())), Key = ["id"] }), "older than the history's base 7"),
            ("a-history-edited-in-place", () => Tables.EvolveTable(v1, t => t.Rename("file", "name")), "recorded with another definition"),
        };
        return
        [
            Case("store.table-migration.fresh-tables-are-created-at-the-newest-version-ready-with-their-indexes-in-one-commit-no-replay", async () =>
            {
                await using var store = await MemoryStore(outbox: true);
                await StoreMigrationPrepared(store, v3);
                var record = store.Table(table)!;
                Check(record.Version == 3 && record.State == TableState.Ready && record.Fingerprint == v3.Fingerprint, "version 3, ready, its fingerprint");
                Json("""{"by_file":["path"],"by_path":["path","at"]}""", StoreInternal.IndexesJson(record.Indexes), "the indexes");
                Sequence(["table, index x2"], await StoreMigrationCommitsAfter(store, 0), "one commit");
            }),
            Case("store.table-migration.fresh-tables-a-table-at-the-newest-version-ready-is-used-as-recorded-nothing-is-written", async () =>
            {
                await using var store = await StoreMigrationFilled(1);
                var seq = store.LastSeq;
                await StoreMigrationPrepared(store, v1);
                Equal(seq, store.LastSeq, "no commit");
            }),
            Case("store.table-migration.eager-migrations-one-version-at-a-time-a-chunks-of-100-in-key-order-z-with-the-later-edits-and-ready", async () =>
            {
                await using var store = await StoreMigrationFilled(250);
                var seq = store.LastSeq;
                var edits = await StoreMigrationPrepared(store, v3);
                Sequence(["alter x2, table", "update x100", "update x100", "update x50, table", "alter, index, table"],
                    await StoreMigrationCommitsAfter(store, seq), "the commits");
                var record = store.Table(table)!;
                Check(record.Version == 3 && record.State == TableState.Ready, "version 3, ready");
                Json("""{"id":{"type":"str"},"path":{"type":"str"},"at":{"type":"int"}}""", StoreInternal.ColumnsJson(record.Columns), "the columns");
                Json("""{"by_file":["path"],"by_path":["path","at"]}""", StoreInternal.IndexesJson(record.Indexes), "the indexes");
                var rows = await StoreMigrationAll(edits, v3);
                Equal(250, rows.Count, "every row");
                Json("""{"id":"e004","path":"d1/f4","at":4}""", rows[4], "e004");
            }),
            Case("store.table-migration.eager-migrations-the-data-step-reads-the-columns-as-they-stand-at-its-version-and-fills-the-one-it-adds", async () =>
            {
                await using var store = await StoreMigrationFilled(3);
                var edits = await StoreMigrationPrepared(store, v2);
                Json("""
                    [
                        {"id":"e000","path":"d0/f0","at":0,"dir":"d0"},
                        {"id":"e001","path":"d1/f1","at":1,"dir":"d1"},
                        {"id":"e002","path":"d2/f2","at":2,"dir":"d2"}
                    ]
                    """, await StoreMigrationAll(edits, v2), "the rows");
            }),
            Case("store.table-migration.eager-migrations-the-migration-s-rows-carry-the-internal-form-the-patch-and-the-version-stamped-as-v", async () =>
            {
                await using var store = await StoreMigrationFilled(1);
                var seq = store.LastSeq;
                await StoreMigrationPrepared(store, v2);
                var writes = (await store.OutboxAsync(seq, 10, context))[1]["writes"]!.AsArray();
                Equal(2, writes.Count, "two writes");
                Json("""{"kind":"update","table":"app.store:edits","key":{"id":"e000"},"set":{"dir":"d0"},"columns":{},"count":1,"version":2}""", writes[0], "the patch");
                Equal("table 2 ready", StoreMigrationWriteHead(writes[1]), "the ready record");
            }),
            Case("store.table-migration.eager-migrations-an-empty-table-still-runs-a-and-z-a-version-without-a-data-step-is-one-commit", async () =>
            {
                await using var store = await StoreMigrationFilled(0);
                var seq = store.LastSeq;
                await StoreMigrationPrepared(store, v3);
                Sequence(["alter x2, table", "table", "alter, index, table"], await StoreMigrationCommitsAfter(store, seq), "the commits");
            }),
            Case("store.table-migration.eager-migrations-a-dropped-column-s-indexes-are-dropped-first-in-the-same-commit", async () =>
            {
                await using var store = await StoreMigrationFilled(2);
                var dropped = Tables.EvolveTable(v1, t => t.Drop("file"));
                var seq = store.LastSeq;
                var edits = await StoreMigrationPrepared(store, dropped);
                var writes = (await store.OutboxAsync(seq, 10, context))[0]["writes"]!.AsArray();
                Json("""
                    [
                        {"kind":"dropIndex","table":"app.store:edits","name":"by_file"},
                        {"kind":"alter","table":"app.store:edits","version":2,"edit":{"kind":"drop","name":"file"}}
                    ]
                    """, new JsonArray([.. writes.Take(2).Select(write => write?.DeepClone())]), "the first writes");
                var record = store.Table(table)!;
                Json("""{"id":{"type":"str"},"at":{"type":"int"}}""", StoreInternal.ColumnsJson(record.Columns), "the columns");
                Json("{}", StoreInternal.IndexesJson(record.Indexes), "no index");
                Json("""[{"id":"e000","at":0},{"id":"e001","at":1}]""", await StoreMigrationAll(edits, dropped), "the rows");
            }),
            Case("store.table-migration.eager-migrations-an-update-step-is-one-updatewhere-over-the-rows-below-its-version-stamping-it-with-ready", async () =>
            {
                await using var store = await StoreMigrationFilled(5);
                var bumped = Tables.EvolveTable(v1, t => t.Update(StoreValue("""{"file":"old"}"""), e => Lt(e["at"], 3)));
                var seq = store.LastSeq;
                var edits = await StoreMigrationPrepared(store, bumped);
                var commits = await store.OutboxAsync(seq, 10, context);
                Sequence(["table", "updateWhere, table"],
                    commits.Select(commit => string.Join(", ", commit["writes"]!.AsArray().Select(write => (string)write!["kind"]!))), "the commits");
                var update = commits[1]["writes"]![0]!;
                Equal("updateWhere 3 2", $"{update["kind"]} {update["count"]} {update["version"]}", "the update's kind, count and version");
                Json("""{"file":"old"}""", update["set"], "the update's set");
                Sequence(["old", "old", "old", "d0/f3", "d1/f4"], (await StoreMigrationAll(edits, bumped)).Select(row => (string)row!["file"]!), "the files");
            }),
            Case("store.table-migration.eager-migrations-a-json-version-change-records-the-column-s-version-no-ddl-cells-migrate-on-access", async () =>
            {
                var notes1 = Tables.DefineTable("edits", new TableOptions
                {
                    Version = 1,
                    Columns = StoreColumns(("id", Str()), ("meta", Opt(Tables.Json(Obj(("a", Int())), new JsonColumnOptions { Version = 1 })))),
                    Key = ["id"],
                });
                var notes2 = Tables.EvolveTable(notes1, t => t.Json("meta", Tables.Json(Obj(("a", Int()), ("b", Int())), new JsonColumnOptions
                {
                    Version = 2,
                    Migrate = (old, _) =>
                    {
                        var migrated = old!.DeepClone().AsObject();
                        migrated["b"] = 0;
                        return migrated;
                    },
                })));
                await using var store = await MemoryStore(outbox: true);
                var first = await StoreMigrationPrepared(store, notes1);
                await StoreCommitWith(store, first, changes => changes.Put(StoreValue("""{"id":"a","meta":{"a":1}}""")));
                var seq = store.LastSeq;
                var second = await StoreMigrationPrepared(store, notes2);
                var writes = (await store.OutboxAsync(seq, 10, context))[0]["writes"]!.AsArray();
                Equal(2, writes.Count, "two writes");
                var alter = writes[0]!;
                Equal("alter app.store:edits 2", $"{alter["kind"]} {alter["table"]} {alter["version"]}", "the alter write");
                var edit = alter["edit"]!;
                Equal("json meta json 2", $"{edit["kind"]} {edit["name"]} {edit["column"]!["type"]} {edit["column"]!["version"]}", "the json edit");
                Equal("table 2 ready", StoreMigrationWriteHead(writes[1]), "the ready record");
                Json("""{"id":"a","meta":{"a":1,"b":0}}""", StoreJson((await second.GetAsync(StoreValue("""{"id":"a"}"""), context))!), "the migrated cell");
            }),
            Case("store.table-migration.the-chunk-yield-yields-between-chunks-without-a-timer-no-1-ms-clamp-while-timers-due-run-in-between-a-latency-guard", async () =>
            {
                await using var store = await StoreMigrationFilled(10 * StoreInternal.MigrationChunk);
                var fired = false;
                StoreMigrationPump.Run(async () =>
                {
                    // Due during the migration: it runs between chunks (a loop of completed awaits would starve it).
                    SynchronizationContext.Current!.Post(_ => fired = true, null);
                    await StoreMigrationPrepared(store, v2);
                    Equal(true, fired, "the posted callback ran during the migration");
                });
                var record = store.Table(table)!;
                Check(record.Version == 2 && record.State == TableState.Ready, "version 2, ready");
            }),
            Case("store.table-migration.patches-a-rows-patch-writes-a-json-null-into-a-json-cell-and-clears-an-opt-column-with-undefined", async () =>
            {
                var cells1 = Tables.DefineTable("edits", new TableOptions
                {
                    Version = 1,
                    Columns = StoreColumns(("id", Str()), ("data", Tables.Json(Nul(), new JsonColumnOptions { Version = 1 })), ("note", Opt(Str()))),
                    Key = ["id"],
                });
                var cells2 = Tables.EvolveTable(cells1, t => t
                    .Add("more", Opt(Tables.Json(Nul(), new JsonColumnOptions { Version = 1 })))
                    .Rows(_ => StorePatch(("more", (JsonNode?)null), ("note", Operand.Undefined))));
                await using var store = await MemoryStore();
                var first = await StoreMigrationPrepared(store, cells1);
                await StoreCommitWith(store, first, changes => changes.Put(StoreValue("""{"id":"a","data":null,"note":"n"}""")));
                var second = await StoreMigrationPrepared(store, cells2);
                Json("""{"id":"a","data":null,"more":null}""", StoreJson((await second.GetAsync(StoreValue("""{"id":"a"}"""), context))!), "the patched row");
            }),
            Case("store.table-migration.resumption-a-data-step-that-throws-leaves-the-table-migrating-with-its-committed-chunks-the-next-run-continues", async () =>
            {
                await using var store = await StoreMigrationFilled(250);
                var fail = true;
                var failing = Tables.EvolveTable(v1, t => t.Rows(row =>
                {
                    if (fail && (string?)row["id"] == StoreMigrationId(150)) throw new InvalidOperationException("crash");
                    return new JsonObject { ["file"] = (string)row["file"]! + "!" };
                }));
                Rejected(await StoreRejection(StoreMigrationPrepared(store, failing)), null, null, "the failing step", "crash");
                var stopped = store.Table(table)!;
                Check(stopped.Version == 2 && stopped.State == TableState.Migrating, "version 2, migrating");
                var seq = store.LastSeq;
                fail = false;
                var edits = await StoreMigrationPrepared(store, failing);
                Sequence(["update x100", "update x50, table"], await StoreMigrationCommitsAfter(store, seq), "the commits of the second run");
                var files = (await StoreMigrationAll(edits, failing)).Select(row => (string)row!["file"]!).ToList();
                // Every row patched once: the first chunk's rows were skipped on the second run.
                Check(files.All(file => file.EndsWith('!') && !file.EndsWith("!!", StringComparison.Ordinal)), "every row patched once");
                var ready = store.Table(table)!;
                Check(ready.Version == 2 && ready.State == TableState.Ready, "version 2, ready");
            }),
            Case("store.table-migration.resumption-a-refused-commit-the-activation-abandoned-stops-the-migration-between-chunks", async () =>
            {
                await using var store = await StoreMigrationFilled(StoreInternal.MigrationChunk * 2 + 1);
                var admitted = 2;
                var fence = new CommitFence(() => admitted-- > 0, (_, _) => { });
                var migration = StoreInternal.PrepareTablesAsync([new DeclaredTable(v2, table)], store, fence, "app.store", context);
                Rejected(await StoreRejection(migration), "released", null, "the refused commit");
                var record = store.Table(table)!;
                Check(record.Version == 2 && record.State == TableState.Migrating, "version 2, migrating");
            }),
            .. refusals.Select(row => Case($"store.table-migration.refusals-{row.Case}-the-store-at-version-2-throws-before-anything-is-written", async () =>
            {
                await using var store = await StoreMigrationFilled(1);
                await StoreMigrationPrepared(store, v2);
                var seq = store.LastSeq;
                Rejected(await StoreRejection(StoreMigrationPrepared(store, row.Definition())), null, null, row.Case, row.Message);
                Equal(seq, store.LastSeq, "nothing written");
            })),
        ];
    }

    /// <summary>A single-threaded synchronization context: <see cref="Run"/> calls its function on the calling thread and runs every
    /// callback posted to it there, in order, until the function's task completes (the test's event loop).</summary>
    private sealed class StoreMigrationPump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("StoreMigrationPump.Send");

        internal static void Run(Func<Task> main)
        {
            var previous = Current;
            var pump = new StoreMigrationPump();
            SetSynchronizationContext(pump);
            try
            {
                var task = main();
                task.ContinueWith(_ => pump.queue.CompleteAdding(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                foreach (var (callback, state) in pump.queue.GetConsumingEnumerable()) callback(state);
                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
