using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/import.test.ts. Every case is ported, over ChordStore on
// TestSqliteDatabase (ChordSupport.cs), each store closed by its case (upstream's afterEach); the it.each rows of the refusals are a case
// each. An archive is its parts' JSON, a fresh export each time, so an edit changes its own copy (upstream's structuredClone); a part an
// edit places twice is one object twice, as upstream's spread places it. Upstream's `record.key = undefined` (a singleton's absent key
// copied) is the key removed. Upstream's stalled input is a hand-written enumerator, closed by DisposeAsync at once; the PiSharp case after
// it stalls a C# async iterator, which refuses DisposeAsync while its MoveNextAsync runs, so the import closes it once that call settled.
internal static partial class Program
{
    /// <summary><paramref name="parts"/> imported into a fresh <c>:memory:</c> backend (upstream <c>imported</c>).</summary>
    private static async Task<ChordStore> StoreImported(IEnumerable<JsonNode?> parts, bool? outbox = null) =>
        (ChordStore)await Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(":memory:")), parts, ChordContext.Background,
            new StoreOptions { Outbox = outbox });

    /// <summary>The archive of <see cref="ArchiveStore"/> (upstream <c>archive</c>).</summary>
    private static async Task<List<JsonObject>> StoreImportArchive()
    {
        await using var source = await ArchiveStore();
        return await Exported(source);
    }

    /// <summary>The index of the first part of <paramref name="kind"/> (and table suffix; upstream <c>first</c>).</summary>
    private static int StoreImportFirst(List<JsonObject> parts, string kind, string? table = null) => parts.FindIndex(part =>
        (string)part["kind"]! == kind && (table is null || part["table"] is { } name && ((string)name!).EndsWith(table, StringComparison.Ordinal)));

    /// <summary>The positions of every part of <paramref name="kind"/> (upstream <c>all</c>).</summary>
    private static List<int> StoreImportAll(List<JsonObject> parts, string kind) =>
        [.. parts.Select((part, index) => (part, index)).Where(entry => (string)entry.part["kind"]! == kind).Select(entry => entry.index)];

    /// <summary>The record of the document.create part at <paramref name="index"/>, writable.</summary>
    private static JsonObject StoreImportRecord(List<JsonObject> parts, int index) => parts[index]["record"]!.AsObject();

    /// <summary>The index of the first revision part holding a delta.</summary>
    private static int StoreImportDelta(List<JsonObject> parts) =>
        parts.FindIndex(part => (string)part["kind"]! == "document.revision" && (string)part["content"]!["kind"]! == "delta");

    /// <summary>Upstream's stalled input: three parts, then a next() that never settles; return() marks it closed.</summary>
    private sealed class StoreStalledParts(List<JsonObject> parts) : IAsyncEnumerable<JsonNode?>, IAsyncEnumerator<JsonNode?>
    {
        private int index;

        internal bool Closed { get; private set; }

        public JsonNode? Current { get; private set; }

        public IAsyncEnumerator<JsonNode?> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (index >= 3) return new ValueTask<bool>(new TaskCompletionSource<bool>().Task);
            Current = parts[index++];
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static IEnumerable<(string, Func<Task>)> StoreImportCases()
    {
        var context = ChordContext.Background;
        var open = CommitFence.Open;
        var refusals = new (string Case, Func<List<JsonObject>, List<JsonNode?>> Edit, string Message)[]
        {
            ("a-part-outside-the-schema", parts => [parts[0], StoreValue("""{ "kind": "put" }"""), .. parts[1..]], "not an archive part"),
            ("no-manifest-first", parts => [.. parts[1..]], "the manifest comes first"),
            ("a-second-manifest", parts => [parts[0], .. parts], "the manifest comes first, once"),
            ("another-format", parts =>
            {
                parts[0]["format"] = 2;
                return [.. parts];
            }, "format 2 is not 1"),
            ("a-newer-store-schema", parts =>
            {
                parts[0]["schema"] = 9;
                return [.. parts];
            }, "schema 9 is newer"),
            ("a-lastseq-of-2-53-a-later-seq-would-repeat", parts =>
            {
                parts[0]["lastSeq"] = 1L << 53;
                return [.. parts];
            }, "part 0: not an archive part"),
            ("a-document-revision-beyond-2-53-1", parts =>
            {
                StoreImportRecord(parts, StoreImportFirst(parts, "document.create"))["revision"] = 1L << 53;
                return [.. parts];
            }, "not an archive part"),
            ("a-part-after-the-end", parts => [.. parts, StoreValue("""{ "kind": "end", "writes": 0 }""")], "after the end"),
            ("no-end", parts => [.. parts[..^1]], "has no end part"),
            ("a-wrong-end-count", parts =>
            {
                parts[^1]["writes"] = 1;
                return [.. parts];
            }, "counts 1 writes"),
            ("a-put-before-its-table", parts =>
            {
                var put = StoreImportFirst(parts, "put", ":notes");
                var table = StoreImportFirst(parts, "table");
                return [.. parts[..table], parts[put], .. parts[table..]];
            }, "a put follows its table's table write"),
            ("a-put-after-its-table-s-indexes", parts =>
            {
                var index = StoreImportFirst(parts, "index");
                return [.. parts[..(index + 1)], parts[index - 1], .. parts[(index + 1)..]];
            }, "a put follows its table's table write, before its indexes"),
            ("a-table-twice", parts =>
            {
                var table = StoreImportFirst(parts, "table", ":notes");
                return [.. parts[..table], parts[StoreImportFirst(parts, "table")], .. parts[table..]];
            }, "occurs twice"),
            ("a-table-after-the-documents", parts =>
            {
                var table = StoreImportFirst(parts, "table", ":notes");
                var documents = StoreImportFirst(parts, "document.create");
                return [.. parts[..table], .. parts[documents..^1], .. parts[table..documents], parts[^1]];
            }, "tables come before the documents"),
            ("a-table-name-that-is-not-owner-table", parts =>
            {
                parts[StoreImportFirst(parts, "table")]["table"] = "edits";
                return [.. parts];
            }, "not owner:table"),
            ("a-column-name-breaking-the-rules", parts =>
            {
                parts[StoreImportFirst(parts, "table")]["columns"]!["bad name"] = StoreValue("""{ "type": "str", "optional": true }""");
                return [.. parts];
            }, "not a column name"),
            ("a-row-with-an-unknown-column", parts =>
            {
                parts[StoreImportFirst(parts, "put")]["row"]!["extra"] = 1;
                return [.. parts];
            }, "unknown column \"extra\""),
            ("a-row-without-a-non-opt-column", parts =>
            {
                parts[StoreImportFirst(parts, "put")]["row"]!.AsObject().Remove("path");
                return [.. parts];
            }, "column \"path\" is missing"),
            ("a-value-of-another-type", parts =>
            {
                parts[StoreImportFirst(parts, "put")]["row"]!["at"] = "x";
                return [.. parts];
            }, "is not a int value"),
            ("a-json-cell-newer-than-its-column", parts =>
            {
                parts[StoreImportFirst(parts, "put")]["columns"]!["meta"] = 5;
                return [.. parts];
            }, "no readable version"),
            ("a-row-newer-than-its-table", parts =>
            {
                parts[StoreImportFirst(parts, "put")]["version"] = 7;
                return [.. parts];
            }, "version 7 is newer"),
            ("an-id-not-below-the-manifest-s-next-id", parts =>
            {
                StoreImportRecord(parts, StoreImportFirst(parts, "document.create"))["id"] = 10_000;
                return [.. parts];
            }, "is not below"),
            ("an-id-twice", parts =>
            {
                var creates = StoreImportAll(parts, "document.create");
                StoreImportRecord(parts, creates[1])["id"] = StoreImportRecord(parts, creates[0])["id"]!.DeepClone();
                return [.. parts];
            }, "occurs twice"),
            ("a-record-created-after-lastseq", parts =>
            {
                StoreImportRecord(parts, StoreImportFirst(parts, "document.create"))["createdAt"] = 10_000;
                return [.. parts];
            }, "a lifetime outside the manifest's lastSeq"),
            ("a-base-at-another-version-than-its-record-s", parts =>
            {
                parts[StoreImportFirst(parts, "document.revision")]["content"]!["version"] = 2;
                return [.. parts];
            }, "has no base at its version"),
            ("a-delta-without-a-base-at-its-version", parts =>
            {
                parts[StoreImportDelta(parts)]["content"]!["version"] = 2;
                return [.. parts];
            }, "a delta without a base at its version"),
            ("a-revision-out-of-seq-order", parts =>
            {
                parts[StoreImportDelta(parts)]["seq"] = 1;
                return [.. parts];
            }, "out of seq order"),
            ("a-revision-of-another-record", parts =>
            {
                parts[StoreImportFirst(parts, "document.revision")]["id"] = 9_999;
                return [.. parts];
            }, "follows its record's create"),
            ("a-live-record-without-revisions", parts =>
            {
                var revision = StoreImportFirst(parts, "document.revision");
                return [.. parts.Where((_, index) => index != revision)];
            }, "has no base at its version"),
            ("revisions-of-a-retired-none-record", parts =>
            {
                var record = StoreImportRecord(parts, StoreImportFirst(parts, "document.create"));
                record["retiredAt"] = record["createdAt"]!.DeepClone();
                return [.. parts];
            }, "keeps no revisions"),
            ("a-retired-rewindable-record-without-its-history", parts =>
            {
                var record = StoreImportRecord(parts, StoreImportFirst(parts, "document.create"));
                record["history"] = "rewindable";
                record["retiredAt"] = record["createdAt"]!.DeepClone();
                var id = (long)record["id"]!;
                return [.. parts.Where(part => (string)part["kind"]! != "document.revision" || (long)part["id"]! != id)];
            }, "has no base at its version"),
            ("two-live-records-at-one-address", parts =>
            {
                var creates = StoreImportAll(parts, "document.create");
                var (one, two) = (StoreImportRecord(parts, creates[0]), StoreImportRecord(parts, creates[1]));
                // The first is the singleton: its absent key, copied (upstream assigns undefined).
                if (one["key"] is { } key) two["key"] = key.DeepClone();
                else two.Remove("key");
                return [.. parts];
            }, "a second record at one address for an overlapping lifetime"),
            ("a-record-s-meta-with-a-nul", parts =>
            {
                StoreImportRecord(parts, StoreImportFirst(parts, "document.create"))["meta"] = "a\u0000b";
                return [.. parts];
            }, "text may not contain NUL"),
            ("a-record-s-meta-with-a-lone-surrogate", parts =>
            {
                StoreImportRecord(parts, StoreImportFirst(parts, "document.create"))["meta"] = "a\uD800";
                return [.. parts];
            }, "the meta is not well-formed text"),
        };
        return
        [
            Case("store.import.round-trips-an-imported-archive-exports-the-same-parts-lossless-at-the-manifest-s-lastseq-and-next-document-id", async () =>
            {
                var parts = await StoreImportArchive();
                await using var store = await StoreImported(parts);
                SameParts(parts, await Exported(store), "the copy's export");
                Sequence(new[] { (long)parts[0]["lastSeq"]!, (long)parts[0]["nextId"]! }, new[] { store.LastSeq, store.NextId }, "lastSeq and nextId");
            }),
            Case("store.import.round-trips-the-store-keeps-its-ids-seqs-and-folded-documents-the-outbox-when-asked-starts-after-lastseq", async () =>
            {
                var parts = await StoreImportArchive();
                await using var store = await StoreImported(parts, true);
                Equal(0, (await store.Outbox!.ReadAsync(store.LastSeq, 10, context)).Count, "the outbox after lastSeq");
                Rejected(await StoreRejection(store.Outbox!.ReadAsync(0, 10, context)), "invalid", null, "the outbox before it");
                var record = await store.ReadAsync(executor => DocumentEngine.FindDocumentAsync(executor, new DocumentAddress("app.store", "chord", "memo", "a")), context);
                var original = parts.First(part => (string)part["kind"]! == "document.create" && (string?)part["record"]!["key"] == "a");
                Equal(StoreInternal.RecordOfJson(original["record"]!.AsObject()), record, "the record");
                Json("""{ "title": "a3" }""", (await store.ReadAsync(executor => DocumentEngine.LoadDocumentAsync(executor, record!), context)).Value, "the folded value");
            }),
            Case("store.import.round-trips-a-migrating-table-resumes-on-the-imported-store-rows-written-at-version-1-read-as-the-table-s", async () =>
            {
                await using var store = await StoreImported(await StoreImportArchive());
                var thrown = await StoreRejection(StoreInternal.PrepareTablesAsync([new DeclaredTable(ArchiveTables.NotesV2, "app.store:notes")], store, open, "app", context));
                Check(StoreText(thrown).Contains("crash", StringComparison.Ordinal), $"the migration resumes and crashes again: actual <{StoreText(thrown)}>");
                var edits = new TableHandle(ArchiveTables.EditsV2, "app.store:edits", store, TableOwner());
                Json("""{ "id": "e003", "path": "p/3", "at": 3, "meta": { "a": 3 }, "done": false }""",
                    StoreJson((await edits.GetAsync(StoreValue("""{ "id": "e003" }"""), context))!), "e003");
            }),
            Case("store.import.json-cells-holding-null-a-json-null-round-trips-required-and-optional-put-and-updated-an-absent-optional-cell-stays-absent", async () =>
            {
                await using var source = await MemoryStore();
                var cells = Tables.DefineTable("cells", new TableOptions
                {
                    Version = 1,
                    Columns = StoreColumns(("id", Str()), ("data", Tables.Json(Nul(), new JsonColumnOptions { Version = 1 })),
                        ("more", Opt(Tables.Json(Nul(), new JsonColumnOptions { Version = 1 })))),
                    Key = ["id"],
                });
                var handle = await TableIn(source, cells);
                var staged = new List<IStagedWrite>();
                var changes = new TableChanges(handle, staged);
                changes.Put(StoreValue("""{ "id": "a", "data": null, "more": null }"""));
                changes.Put(StoreValue("""{ "id": "b", "data": null }"""));
                changes.Put(StoreValue("""{ "id": "c", "data": null, "more": null }"""));
                changes.Update(StoreValue("""{ "id": "c" }"""), StorePatch(("more", Operand.Undefined)));
                await source.CommitAsync(staged, open, context);
                string[] rows = ["""{ "id": "a", "data": null, "more": null }""", """{ "id": "b", "data": null }""", """{ "id": "c", "data": null }"""];
                foreach (var row in rows)
                {
                    var key = new JsonObject { ["id"] = StoreValue(row)["id"]!.DeepClone() };
                    Json(row, StoreJson((await handle.GetAsync(key, context))!), $"the source's {key["id"]}");
                }
                var parts = await Exported(source);
                await using var store = await StoreImported(parts);
                SameParts(parts, await Exported(store), "the copy's export");
                var copy = new TableHandle(cells, handle.Sql.Table, store, TableOwner());
                foreach (var row in rows)
                {
                    var key = new JsonObject { ["id"] = StoreValue(row)["id"]!.DeepClone() };
                    Json(row, StoreJson((await copy.GetAsync(key, context))!), $"the copy's {key["id"]}");
                }
            }),
            Case("store.import.json-cells-holding-null-a-json-null-at-an-older-cell-version-round-trips-with-its-version-and-migrates-on-access-in-the-copy", async () =>
            {
                await using var source = await MemoryStore();
                var v1 = Tables.DefineTable("cells", new TableOptions
                {
                    Version = 1,
                    Columns = StoreColumns(("id", Str()), ("data", Tables.Json(Nul(), new JsonColumnOptions { Version = 1 }))),
                    Key = ["id"],
                });
                var old = await TableIn(source, v1);
                var staged = new List<IStagedWrite>();
                new TableChanges(old, staged).Put(StoreValue("""{ "id": "a", "data": null }"""));
                await source.CommitAsync(staged, open, context);
                var v2 = Tables.EvolveTable(v1, t => t.Json("data", Tables.Json(Obj(("was", Nul())), new JsonColumnOptions
                {
                    Version = 2,
                    Migrate = (value, _) => new JsonObject { ["was"] = value?.DeepClone() },
                })));
                var handle = await TableIn(source, v2);
                Json("""{ "id": "a", "data": { "was": null } }""", StoreJson((await handle.GetAsync(StoreValue("""{ "id": "a" }"""), context))!), "the source's row");
                var parts = await Exported(source);
                var put = parts.First(part => (string)part["kind"]! == "put");
                Json("""{ "id": "a", "data": null }""", put["row"], "the put's row");
                Json("""{ "data": 1 }""", put["columns"], "the put's cell versions");
                await using var store = await StoreImported(parts);
                SameParts(parts, await Exported(store), "the copy's export");
                var copy = new TableHandle(v2, handle.Sql.Table, store, TableOwner());
                Json("""{ "id": "a", "data": { "was": null } }""", StoreJson((await copy.GetAsync(StoreValue("""{ "id": "a" }"""), context))!), "the copy's row");
            }),
            .. refusals.Select(row => Case($"store.import.refusals-{row.Case}", async () =>
                Rejected(await StoreRejection(StoreImported(row.Edit(await StoreImportArchive()))), "invalid", "args", row.Case, row.Message))),
            Case("store.import.refusals-a-non-empty-backend-is-refused-a-refused-import-closes-its-backend-and-leaves-it-as-it-was", async () =>
            {
                var directory = Directory.CreateTempSubdirectory("chord-import-");
                try
                {
                    var path = Path.Combine(directory.FullName, "store.db");
                    var parts = await StoreImportArchive();
                    List<JsonNode?> broken = [.. parts[..^1], StoreValue("""{ "kind": "end", "writes": 0 }""")];
                    Rejected(await StoreRejection(Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(path)), broken, context)), "invalid", null,
                        "a refused import");
                    var db = new TestSqliteDatabase(path);
                    Json("[]", StoreSqlJson(await db.AllAsync("SELECT name FROM sqlite_master")), "the backend as it was");
                    await db.CloseAsync();
                    var store = await Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(path)), parts, context);
                    await store.CloseAsync(context);
                    var again = await StoreRejection(Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(path)), parts, context));
                    Equal("import: the backend holds a store already", StoreText(again), "a non-empty backend");
                }
                finally
                {
                    directory.Delete(recursive: true);
                }
            }),
            Case("store.import.refusals-an-input-that-stalls-is-cancelled-by-ctx-the-import-rejects-aborted-and-the-input-is-closed", async () =>
            {
                var stalled = new StoreStalledParts(await StoreImportArchive());
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var importing = Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(":memory:")), stalled, cancellable);
                await Task.Delay(10);
                cancel();
                Rejected(await StoreRejection(importing), "aborted", null, "the import");
                Check(stalled.Closed, "the input is closed");
            }),
            Case("store.import.refusals-pisharp-a-stalled-async-iterator-is-closed-once-its-running-move-next-settles", async () =>
            {
                // PiSharp: a C# async iterator refuses DisposeAsync while its MoveNextAsync runs; the import closes it once that call settled.
                var parts = await StoreImportArchive();
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async IAsyncEnumerable<JsonNode?> Stalled()
                {
                    try
                    {
                        for (var i = 0; i < 3; i++) yield return parts[i];
                        await gate.Task;
                        foreach (var part in parts.Skip(3)) yield return part;
                    }
                    finally
                    {
                        closed.TrySetResult();
                    }
                }
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var importing = Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(":memory:")), Stalled(), cancellable);
                await Task.Delay(10);
                cancel();
                Rejected(await StoreRejection(importing), "aborted", null, "the import");
                Check(!closed.Task.IsCompleted, "the input's MoveNextAsync still runs");
                gate.SetResult();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }),
        ];
    }
}
