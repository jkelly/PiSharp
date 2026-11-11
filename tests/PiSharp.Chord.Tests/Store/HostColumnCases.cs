// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/host-column.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StoreHostColumnCases()
    {
        yield return Case("store.commit.reports-committed-row-count-before-adoption", async () =>
        {
            var definition = Tables.DefineTable("commit_rows", new TableOptions
            {
                Version = 1, Columns = StoreColumns(("id", Str()), ("text", Str())), Key = ["id"],
            });
            await using var store = await MemoryStore();
            var handle = await TableIn(store, definition);
            var writes = new List<IStagedWrite>();
            var changes = new TableChanges(handle, writes);
            changes.Put(new JsonObject { ["id"] = "a", ["text"] = "first" });
            changes.Put(new JsonObject { ["id"] = "b", ["text"] = "second" });
            changes.Update(new JsonObject { ["id"] = "absent" }, new JsonObject { ["text"] = "none" });
            changes.Update(new JsonObject { ["id"] = "a" }, new JsonObject { ["text"] = "updated" });
            changes.DeleteWhere(Eq(definition.Refs["text"], "second"));
            long? rows = null;
            await store.CommitAsync(writes, new CommitFence(() => true, (_, _) => Equal(4L, rows, "count available during adoption"))
                { Rows = count => rows = count }, ChordContext.Background);
            Equal(4L, rows, "two puts, one changed update, one delete, absent update counts zero");
            rows = null;
            await store.CommitAsync([], new CommitFence(() => true, (_, _) => Equal(0L, rows, "empty count available during adoption"))
                { Rows = count => rows = count }, ChordContext.Background);
            rows = null;
            await StoreRejection(store.CommitAsync(writes, new CommitFence(() => false, (_, _) => throw new InvalidOperationException("not admitted"))
                { Rows = count => rows = count }, ChordContext.Background));
            Check(rows is null, "refused admission never reports committed rows");
        });

        yield return Case("store.host.schema-upgrade-keeps-existing-records-unscoped", async () =>
        {
            var db = new TestSqliteDatabase(":memory:");
            foreach (var migration in StoreInternal.StoreMigrations.Take(1))
                foreach (var statement in migration.Statements) await db.RunAsync(statement);
            await db.RunAsync("CREATE TABLE chord_store_schema (singleton INTEGER PRIMARY KEY CHECK (singleton = 1), version INTEGER NOT NULL CHECK (version >= 0)) STRICT");
            await db.RunAsync("INSERT INTO chord_store_schema VALUES (1, 1)");
            await db.RunAsync("INSERT INTO chord_documents (id, owner, namespace, kind, key, history, created_at, revision, version, fingerprint) VALUES (2, 'old', 'chord', 'notes', 'session|one', 'none', 1, 1, 1, 'f')");
            await using var store = await StoreOver(db);
            var row = await store.ReadAsync(executor => DocumentEngine.ReadDocumentAsync(executor, 2), ChordContext.Background);
            Check(row is not null && row.Host is null, "legacy key resembling an address does not invent host ownership");
            var owned = await store.ReadAsync(executor => DocumentEngine.ListHostRecordsAsync(executor, "session|one", 0, 100), ChordContext.Background);
            Equal(0, owned.Count, "legacy document excluded from host operations");
            var schema = await store.ReadAsync(executor => executor.GetAsync("SELECT version FROM chord_store_schema"), ChordContext.Background);
            Equal(3L, (long)schema!["version"]!, "schema advanced to host migration");
        });

        yield return Case("store.host.column-metadata-codec-and-query-addresses", () =>
        {
            var kind = new HostKind("host_column_session", new() { ["id"] = Str() });
            var address = kind.Address(("id", "one"));
            var other = new HostKind("host_column_other").Address();
            var definition = Tables.DefineTable("host_column_rows", new TableOptions
            {
                Version = 1, Columns = StoreColumns(("id", Str()), ("host", Tables.Host(kind)), ("text", Str())), Key = ["id"],
            });
            Equal(kind.Kind, definition.Record["host"].Host, "record stores host kind");
            Sequence(["host"], definition.Indexes["host"], "automatic host index");
            var evolved = Tables.EvolveTable(definition, editor => editor.Rename("host", "owner"));
            Equal(kind.Kind, evolved.Record["owner"].Host, "rename keeps host metadata");
            Sequence(["owner"], evolved.Indexes["host"], "rename carries automatic index");
            var row = StoreRow("""{"id":"a","text":"value"}""");
            row["host"] = address.ToJson();
            var encoded = definition.Rows.Put(row, 1);
            Equal(address.Key, (string)encoded.Row["host"]!, "address encoded as key");
            var decoded = definition.Rows.Decode(new Dictionary<string, object?> { ["id"] = "a", ["host"] = address.Key, ["text"] = "value", ["$v"] = 1L }, ChordContext.Background);
            Json(ChordJson.Stringify(address.ToJson()), decoded["host"], "address decoded as JSON");
            var where = (WhereNode.Compare)StoreInternal.CompileWhere(definition, Eq(definition.Refs["host"], address), "query");
            Equal(address.Key, where.Value.Value, "query binds canonical key");
            StoreArgs(() => definition.Rows.Put(new JsonObject { ["id"] = "a", ["host"] = other.ToJson(), ["text"] = "value" }, 1), "expected an address");
            StoreArgs(() => StoreInternal.CompileWhere(definition, Lt(definition.Refs["host"], address.ToJson()), "query"), "eq, ne, inArray only");
            StoreArgs(() => StoreInternal.CompileWhere(definition, IsNull(definition.Refs["host"]), "query"), "eq, ne, inArray only");
            StoreArgs(() => StoreInternal.CompileSet(definition, new Dictionary<string, Operand> { ["text"] = definition.Refs["host"] }, "update"), "holds addresses");
        });

        yield return Case("store.host.column-declarations-refuse-optional-multiple-and-wrong-reserved-index", () =>
        {
            var kind = new HostKind("host_column_rules");
            TableDefinition Define(OrderedDictionary<string, Property> columns, OrderedDictionary<string, IReadOnlyList<string>>? indexes = null) =>
                Tables.DefineTable("host_column_rules", new TableOptions { Version = 1, Columns = columns, Key = ["id"], Indexes = indexes ?? new(StringComparer.Ordinal) });
            StoreThrows(() => Define(StoreColumns(("id", Str()), ("host", Opt(Tables.Host(kind))))), "not opt");
            StoreThrows(() => Define(StoreColumns(("id", Str()), ("host", Tables.Host(kind)), ("other", Tables.Host(kind)))), "more than one");
            StoreThrows(() => Define(StoreColumns(("id", Str()), ("host", Tables.Host(kind))), StoreIndexes(("host", ["id"]))), "own");
        });

        yield return Case("store.host.transaction-overlay-retains-address-values", async () =>
        {
            var kind = new HostKind("host_column_overlay", new() { ["id"] = Str() });
            var first = kind.Address(("id", "one"));
            var next = kind.Address(("id", "two"));
            var definition = Tables.DefineTable("host_column_overlay", new TableOptions
            {
                Version = 1, Columns = StoreColumns(("id", Str()), ("host", Tables.Host(kind)), ("text", Str())), Key = ["id"],
            });
            await using var store = await MemoryStore();
            var handle = await TableIn(store, definition);
            await StoreCommitWith(store, handle, change => change.Put(new JsonObject { ["id"] = "a", ["host"] = first.ToJson(), ["text"] = "old" }));
            var staged = new List<IStagedWrite>();
            var changes = new TableChanges(handle, staged);
            changes.Update(new JsonObject { ["id"] = "a" }, new JsonObject { ["host"] = next.ToJson(), ["text"] = "new" });
            var overlaid = await changes.GetAsync(new JsonObject { ["id"] = "a" }, ChordContext.Background);
            Json(ChordJson.Stringify(next.ToJson()), overlaid!["host"], "overlay exposes decoded address");
            var current = await handle.GetAsync(new JsonObject { ["id"] = "a" }, ChordContext.Background);
            Json(ChordJson.Stringify(first.ToJson()), current!["host"], "uncommitted overlay leaves store unchanged");
        });
    }
}
