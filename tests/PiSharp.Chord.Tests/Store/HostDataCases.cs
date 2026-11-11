// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/host-data.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static readonly HostKind StoreDataSession = new("session", new() { ["id"] = Str() });
    private static readonly ChordContext StoreDataContext = ChordContext.Background;

    private static TableDefinition StoreDataDefinition(bool composite = true) => Tables.DefineTable("host_notes", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("host", Tables.Host(StoreDataSession)), ("id", Str()), ("text", Str())),
        Key = composite ? ["host", "id"] : ["id"],
    });

    private static List<JsonNode?> StoreDataParts(string host = "session|one", bool composite = true)
    {
        var definition = StoreDataDefinition(composite);
        var record = new DocumentRecord
        {
            Id = 2, Owner = "app.store", Namespace = "chord", Kind = "notes", Key = host, Host = host,
            History = DocumentHistory.None, CreatedAt = 3, Revision = 2, Version = 1, Fingerprint = "f1",
        };
        var parts = new List<JsonNode?>
        {
            new JsonObject { ["kind"] = "manifest", ["format"] = StoreInternal.ArchiveFormat, ["schema"] = StoreInternal.StoreSchema,
                ["lastSeq"] = 5, ["nextId"] = 3, ["host"] = host },
            new TableWrite.Definition("app.store:host_notes", 1, TableState.Ready, definition.Record, definition.Key, definition.Fingerprint).ToJson(),
            new TableWrite.Put("app.store:host_notes", StoreRow($$"""{"host":"{{host}}","id":"a","text":"first"}"""), 1,
                new Dictionary<string, long>()).ToJson(),
        };
        foreach (var (name, columns) in definition.Indexes) parts.Add(new TableWrite.Index("app.store:host_notes", name, columns).ToJson());
        parts.Add(new DocumentWrite.Create(record, null).ToJson());
        parts.Add(new DocumentWrite.KeptRevision(2, 3, new RevisionContent.Base(1, StoreValue("""{"text":"first"}"""))).ToJson());
        parts.Add(new DocumentWrite.KeptRevision(2, 5, new RevisionContent.Delta(1, new JsonArray(new JsonArray("s", new JsonArray("text"), "last")))).ToJson());
        parts.Add(new JsonObject { ["kind"] = "end", ["writes"] = parts.Count - 1 });
        return parts;
    }

    private static HostCommit StoreDataCommit(Action? adopted = null, IReadOnlyList<IStagedWrite>? after = null) =>
        new(new CommitFence(() => true, (_, _) => adopted?.Invoke()), after ?? []);

    private static HostImportPlan StoreDataPlan(string target, HostExisting existing = HostExisting.Refuse,
        IReadOnlyDictionary<string, TableDefinition>? histories = null) => new(target, existing, histories ?? new Dictionary<string, TableDefinition>());

    private static async Task<HostArchive> StoreDataArchive(string host = "session|one", bool composite = true) =>
        await Tables.ReadHostArchiveAsync(StoreDataParts(host, composite), StoreDataContext);

    private static IEnumerable<(string, Func<Task>)> StoreHostDataCases()
    {
        var malformed = new (string Name, Action<List<JsonNode?>> Edit, string Message)[]
        {
            ("manifest-without-host", p => p[0]!.AsObject().Remove("host"), "not a host's data archive"),
            ("second-manifest", p => p.Insert(1, p[0]!.DeepClone()), "manifest comes first, once"),
            ("table-without-host-column", p => p[1]!["columns"]!["host"]!.AsObject().Remove("host"), "no single host column"),
            ("table-with-two-host-columns", p => p[1]!["columns"]!["text"]!["host"] = "session", "no single host column"),
            ("table-with-other-host-kind", p => p[1]!["columns"]!["host"]!["host"] = "other", "no single host column"),
            ("put-with-other-host", p => p[2]!["row"]!["host"] = "session|two", "does not belong"),
            ("record-with-other-host", p => p.First(x => (string?)x?["kind"] == "document.create")!["record"]!["host"] = "session|two", "does not belong"),
            ("record-with-other-key", p => p.First(x => (string?)x?["kind"] == "document.create")!["record"]!["key"] = "session|two", "does not belong"),
            ("retired-record", p => p.First(x => (string?)x?["kind"] == "document.create")!["record"]!["retiredAt"] = 5, "not a live Chord record"),
            ("historical-record", p => p.First(x => (string?)x?["kind"] == "document.create")!["record"]!["history"] = "latest", "not a live Chord record"),
            ("record-meta", p => p.First(x => (string?)x?["kind"] == "document.create")!["record"]!["meta"] = "extra", "not a live Chord record"),
            ("delta-at-other-version", p => p[^2]!["content"]!["version"] = 2, "delta without a base"),
            ("invalid-delta", p => p[^2]!["content"]!["ops"] = new JsonArray(new JsonArray("unknown")), "delta that does not apply"),
            ("out-of-order-revision", p => p[^2]!["seq"] = 3, "in seq order"),
            ("wrong-base-version", p => { p[^3]!["content"]!["version"] = 2; p[^2]!["content"]!["version"] = 2; }, "no base at its version"),
            ("wrong-count", p => p[^1]!["writes"] = 999, "counts 999 writes"),
            ("part-after-end", p => p.Add(new JsonObject { ["kind"] = "end", ["writes"] = 0 }), "after the end"),
            ("no-end", p => p.RemoveAt(p.Count - 1), "no end part"),
        };
        foreach (var each in malformed)
        {
            yield return Case("store.host-data.archive-refuses-" + each.Name, async () =>
            {
                var parts = StoreDataParts();
                each.Edit(parts);
                var rejection = await StoreRejection(Tables.ReadHostArchiveAsync(parts, StoreDataContext));
                Rejected(rejection, "invalid", "args", each.Name, each.Message);
            });
        }

        yield return Case("store.host-data.import-copy-and-delete-are-single-commits", async () =>
        {
            await using var store = await MemoryStore(true);
            var archive = await StoreDataArchive();
            var adopted = false;
            var observed = false;
            var after = new StagedWrite(WriteOrder.Write, async step =>
            {
                var row = await step.GetAsync("SELECT text FROM \"app.store:host_notes\" WHERE host = ?", "session|copy");
                observed = (string?)row?["text"] == "first";
                return [];
            });
            var counts = await StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan("session|copy"), StoreDataCommit(() => adopted = true, [after]), StoreDataContext);
            Equal(new HostDataCounts(1, 1), counts, "import counts");
            Equal(1L, store.LastSeq, "one import commit");
            Check(adopted && observed, "post-write read and post-commit adoption both ran");
            var imported = await store.ReadAsync(executor => DocumentEngine.ListHostRecordsAsync(executor, "session|copy", 0, 10), StoreDataContext);
            Equal(1, imported.Count, "copy document count");
            Equal("session|copy", imported[0].Key, "record key rewritten");
            Equal("session|copy", imported[0].Host, "record owner rewritten");
            Equal(1L, imported[0].CreatedAt, "record uses new commit seq");
            var loaded = await store.ReadAsync(executor => DocumentEngine.LoadContentAsync(executor, imported[0].Id, DocumentPoint.Current), StoreDataContext);
            Json("""{"text":"last"}""", loaded!.Value, "deltas materialized to the new base");
            var source = await store.ReadAsync(executor => DocumentEngine.ListHostRecordsAsync(executor, "session|one", 0, 10), StoreDataContext);
            Equal(0, source.Count, "no original-address record");
            var deleted = await StoreInternal.DeleteHostDataAsync(store, "session|copy", StoreDataCommit(), StoreDataContext);
            Equal(new HostDataCounts(1, 1), deleted, "delete counts");
            Equal(2L, store.LastSeq, "one delete commit");
            var outbox = await StoreOutbox(store, 1, 10, StoreDataContext);
            Check(outbox[0]["writes"]!.AsArray().Any(write => (string?)write?["kind"] == "deleteWhere"), "delete records counted table write");
        });

        yield return Case("store.host-data.refuse-preserves-existing-and-replace-keeps-other-host", async () =>
        {
            await using var store = await MemoryStore();
            var one = await StoreDataArchive();
            var two = await StoreDataArchive("session|two");
            await StoreInternal.ImportHostDataAsync(store, one, StoreDataPlan(one.Host), StoreDataCommit(), StoreDataContext);
            await StoreInternal.ImportHostDataAsync(store, two, StoreDataPlan(two.Host), StoreDataCommit(), StoreDataContext);
            var before = store.LastSeq;
            var refused = await StoreRejection(StoreInternal.ImportHostDataAsync(store, one, StoreDataPlan(one.Host), StoreDataCommit(), StoreDataContext));
            Rejected(refused, "invalid", "args", "refuse existing", "has data in this store");
            Equal(before, store.LastSeq, "refused import does not commit");
            await StoreInternal.ImportHostDataAsync(store, one, StoreDataPlan(one.Host, HostExisting.Replace), StoreDataCommit(), StoreDataContext);
            var other = await store.ReadAsync(executor => executor.AllAsync("SELECT host FROM \"app.store:host_notes\" ORDER BY host"), StoreDataContext);
            Equal(2, other.Count, "replace preserves other address rows");
            Equal("session|two", (string)other[1]["host"]!, "other address survives");
        });

        yield return Case("store.host-data.key-collision-rolls-back-replacement-and-new-documents", async () =>
        {
            await using var store = await MemoryStore();
            var two = await StoreDataArchive("session|two", false);
            await StoreInternal.ImportHostDataAsync(store, two, StoreDataPlan(two.Host), StoreDataCommit(), StoreDataContext);
            var one = await StoreDataArchive("session|one", false);
            var before = store.LastSeq;
            var refused = await StoreRejection(StoreInternal.ImportHostDataAsync(store, one, StoreDataPlan(one.Host, HostExisting.Replace), StoreDataCommit(), StoreDataContext));
            Rejected(refused, "invalid", "args", "collision", "another address");
            Equal(before, store.LastSeq, "collision has no commit");
            var rows = await store.ReadAsync(executor => executor.AllAsync("SELECT host FROM \"app.store:host_notes\""), StoreDataContext);
            Equal("session|two", (string)rows.Single()["host"]!, "existing row remains");
        });

        yield return Case("store.host-data.export-filters-rows-and-documents-and-holds-queue", async () =>
        {
            await using var store = await MemoryStore();
            foreach (var key in new[] { "session|one", "session|two" })
            {
                var archive = await StoreDataArchive(key);
                await StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan(key), StoreDataCommit(), StoreDataContext);
            }
            await using var export = Tables.ExportHostData(store, StoreDataSession.Address(("id", JsonValue.Create("one"))), StoreDataContext);
            Check(await export.MoveNextAsync(), "manifest yielded");
            Equal("session|one", (string)export.Current["host"]!, "host manifest");
            var deletion = StoreInternal.DeleteHostDataAsync(store, "session|one", StoreDataCommit(), StoreDataContext);
            Check(!deletion.IsCompleted, "export holds commit queue");
            var parts = new List<JsonNode?> { export.Current };
            while (await export.MoveNextAsync()) parts.Add(export.Current);
            await deletion;
            var archiveOne = await Tables.ReadHostArchiveAsync(parts, StoreDataContext);
            Equal(1, archiveOne.Tables.Single().Puts.Count, "only chosen host row exported");
            Equal(1, archiveOne.Documents.Count, "only chosen host record exported");
            Equal("session|one", archiveOne.Documents[0].Record.Host, "exported host");
        });

        yield return Case("store.host-data.owner-enumeration-and-kind-check", async () =>
        {
            await using var store = await MemoryStore();
            var archive = await StoreDataArchive();
            var owners = await StoreInternal.HostDataOwnersAsync(store, archive.Host, archive, StoreDataContext);
            Check(owners.SetEquals(["app.store"]), "archive owners discovered");
            var refusal = await StoreRejection(StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan("other|one"), StoreDataCommit(), StoreDataContext));
            Rejected(refusal, "invalid", "args", "different kind", "another kind");
            Equal(0L, store.LastSeq, "wrong kind commits nothing");
        });

        yield return Case("store.host-data.import-migrates-through-rename-default-and-row-step", async () =>
        {
            await using var store = await MemoryStore();
            var v1 = StoreDataDefinition();
            var v2 = Tables.EvolveTable(v1, table => table.Rename("host", "owner").Add("tag", Str(@default: "new"))
                .Rows(row => new JsonObject { ["text"] = (string)row["text"]! + "!" }));
            await StoreInternal.PrepareTablesAsync([new DeclaredTable(v2, "app.store:host_notes")], store, CommitFence.Open, "app.store", StoreDataContext);
            var archive = await StoreDataArchive();
            Sequence(["app.store:host_notes"], StoreInternal.TablesToMigrate(store, archive), "newer recorded table needs owner history");
            await StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan("session|copy", histories:
                new Dictionary<string, TableDefinition> { ["app.store:host_notes"] = v2 }), StoreDataCommit(), StoreDataContext);
            var row = await store.ReadAsync(executor => executor.GetAsync("SELECT * FROM \"app.store:host_notes\""), StoreDataContext);
            Equal("session|copy", (string)row!["owner"]!, "rewrite follows renamed host column");
            Equal("first!", (string)row["text"]!, "row step applied");
            Equal("new", (string)row["tag"]!, "added default applied");
            Equal(2d, StoreInternal.SqliteNumber(row[StoreInternal.RowVersion]!), "data step stamps version");
        });

        yield return Case("store.host-data.import-resumes-migrating-archive-once-per-row", async () =>
        {
            await using var store = await MemoryStore();
            var v1 = StoreDataDefinition();
            var v2 = Tables.EvolveTable(v1, table => table.Rows(row => new JsonObject { ["text"] = (string)row["text"]! + "!" }));
            await StoreInternal.PrepareTablesAsync([new DeclaredTable(v2, "app.store:host_notes")], store, CommitFence.Open, "app.store", StoreDataContext);
            var parts = new List<JsonNode?>
            {
                new JsonObject { ["kind"] = "manifest", ["format"] = StoreInternal.ArchiveFormat, ["schema"] = StoreInternal.StoreSchema,
                    ["lastSeq"] = 1, ["nextId"] = 2, ["host"] = "session|one" },
                new TableWrite.Definition("app.store:host_notes", 2, TableState.Migrating, v2.Record, v2.Key, v2.Fingerprint).ToJson(),
                new TableWrite.Put("app.store:host_notes", StoreRow("""{"host":"session|one","id":"a","text":"old"}"""), 1, new Dictionary<string, long>()).ToJson(),
                new TableWrite.Put("app.store:host_notes", StoreRow("""{"host":"session|one","id":"b","text":"new!"}"""), 2, new Dictionary<string, long>()).ToJson(),
                new JsonObject { ["kind"] = "end", ["writes"] = 3 },
            };
            var archive = await Tables.ReadHostArchiveAsync(parts, StoreDataContext);
            await StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan(archive.Host, histories:
                new Dictionary<string, TableDefinition> { ["app.store:host_notes"] = v2 }), StoreDataCommit(), StoreDataContext);
            var rows = await store.ReadAsync(executor => executor.AllAsync("SELECT text FROM \"app.store:host_notes\" ORDER BY id"), StoreDataContext);
            Sequence(["old!", "new!"], rows.Select(row => (string)row["text"]!), "only unstamped rows resume the data step");
        });

        yield return Case("store.host-data.import-rechecks-recorded-host-column-after-migration", async () =>
        {
            await using var store = await MemoryStore();
            var v1 = StoreDataDefinition(false);
            var v2 = Tables.EvolveTable(v1, table => table.Rename("host", "owner").Rows(_ =>
                new JsonObject { ["owner"] = StoreDataSession.Address(("id", JsonValue.Create("other"))).ToJson() }));
            await StoreInternal.PrepareTablesAsync([new DeclaredTable(v2, "app.store:host_notes")], store, CommitFence.Open, "app.store", StoreDataContext);
            var archive = await StoreDataArchive(composite: false);
            var before = store.LastSeq;
            var refusal = await StoreRejection(StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan(archive.Host, histories:
                new Dictionary<string, TableDefinition> { ["app.store:host_notes"] = v2 }), StoreDataCommit(), StoreDataContext));
            Rejected(refusal, "invalid", "args", "migration crossed host", "does not belong");
            Equal(before, store.LastSeq, "misplaced row rolls back whole import");
        });

        yield return Case("store.host-data.failed-after-read-rolls-back-without-adoption", async () =>
        {
            await using var store = await MemoryStore();
            var archive = await StoreDataArchive();
            var adopted = false;
            var after = new StagedWrite(WriteOrder.Write, _ => Task.FromException<IReadOnlyList<StoreWrite>>(
                PiSharp.Chord.Data.DataInternal.Invalid("args", "after read refused")));
            var refusal = await StoreRejection(StoreInternal.ImportHostDataAsync(store, archive, StoreDataPlan(archive.Host),
                StoreDataCommit(() => adopted = true, [after]), StoreDataContext));
            Rejected(refusal, "invalid", "args", "after read", "after read refused");
            Check(!adopted, "failed step never adopted");
            Equal(0L, store.LastSeq, "failed step has no commit");
            Check(store.Table("app.store:host_notes") is null, "created table rolled back");
        });

        yield return Case("store.host-data.read-stalled-input-aborts-and-disposes", async () =>
        {
            var input = new StoreStalledParts(StoreDataParts().Select(part => part!.AsObject()).ToList());
            using var abort = new CancellationTokenSource();
            var reading = Tables.ReadHostArchiveAsync(input, ChordContext.WithAbortSignal(abort.Token, StoreDataContext));
            abort.Cancel();
            var refusal = await StoreRejection(reading);
            Rejected(refusal, "aborted", null, "aborted input");
            Check(input.Closed, "input disposed");
        });

        yield return Case("store.host-data.full-store-import-refuses-host-scoped-archive", async () =>
        {
            var refusal = await StoreRejection(Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(":memory:")),
                StoreDataParts(), StoreDataContext));
            Rejected(refusal, "invalid", "args", "full import scoped archive", "host");
        });
    }
}
