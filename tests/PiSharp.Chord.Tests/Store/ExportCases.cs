using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/export.test.ts. Every case is ported, over ChordStore on
// TestSqliteDatabase (ChordSupport.cs); each case closes its store (upstream's afterEach). An iterator's next() is MoveNextAsync, its
// return() DisposeAsync; withCancel's cancel takes no reason. getEventListeners(signal, "abort") has no .NET counterpart (a
// CancellationToken counts no registrations, and a cancelled one keeps none): the two cases that count listeners assert the rest, and
// the PiSharp case at the end shows an ended export's registration goes, as cv2-state's abort-listener case does (an export its token's
// registration kept would stay alive while the token does). flushed() and settledHeap() are ChordSupport's (a short delay; the managed
// heap after full collections).
internal static partial class Program
{
    /// <summary>An export of <paramref name="store"/> read twice, then returned; a weak reference to it (upstream's
    /// <c>new WeakRef(iterator)</c> with the variable cleared).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StoreExportReturned(ChordStore store, ChordContext context)
    {
        var iterator = store.Export(context);
        await iterator.MoveNextAsync();
        await iterator.MoveNextAsync();
        await iterator.DisposeAsync();
        return new WeakReference(iterator);
    }

    /// <summary>An export of <paramref name="store"/> read to its end; a weak reference to it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StoreExportRead(ChordStore store, ChordContext context)
    {
        var iterator = store.Export(context);
        while (await iterator.MoveNextAsync())
        {
        }
        return new WeakReference(iterator);
    }

    private static IEnumerable<(string, Func<Task>)> StoreExportCases()
    {
        var context = ChordContext.Background;
        var open = CommitFence.Open;
        return
        [
            Case("store.export.parts-come-in-archive-order-manifest-per-table-its-table-write-puts-and-indexes-each-record-and-its-revisions-end", async () =>
            {
                await using var store = await ArchiveStore();
                var parts = await Exported(store);
                Json($$"""
                    { "kind": "manifest", "format": {{StoreInternal.ArchiveFormat}}, "schema": {{StoreInternal.StoreSchema}}, "lastSeq": {{store.LastSeq}},
                      "nextId": {{store.NextId}} }
                    """, parts[0], "the manifest");
                var kinds = parts.Select(part => (string)part["kind"]! switch
                {
                    ("put" or "table" or "index") and var kind =>$"{kind} {((string)part["table"]!).Split(':')[1]}",
                    var kind => kind,
                });
                var runs = new List<(string Kind, int Count)>();
                foreach (var kind in kinds)
                {
                    if (runs.Count > 0 && runs[^1].Kind == kind) runs[^1] = (kind, runs[^1].Count + 1);
                    else runs.Add((kind, 1));
                }
                Sequence(
                [
                    ("manifest", 1), ("table edits", 1), ("put edits", 150), ("index edits", 1), ("table notes", 1), ("put notes", 150),
                    ("document.create", 1), ("document.revision", 1), ("document.create", 1), ("document.revision", 3), ("document.create", 1),
                    ("document.revision", 2), ("end", 1),
                ], runs, "the runs of kinds");
                Json($$"""{ "kind": "end", "writes": {{parts.Count - 2}} }""", parts[^1], "the end");
            }),
            Case("store.export.parts-are-lossless-stored-values-and-versions-null-cells-absent-a-migrating-table-s-state-revisions-as-stored", async () =>
            {
                await using var store = await ArchiveStore();
                var parts = await Exported(store);
                var edits = parts.Where(part => (string)part["kind"]! == "put" && ((string)part["table"]!).EndsWith(":edits", StringComparison.Ordinal)).ToList();
                SameParts(
                [
                    StoreValue("""
                        { "kind": "put", "table": "app.store:edits", "row": { "id": "e000", "path": "p/0", "at": 0, "note": "n0", "meta": { "a": 0 }, "done": false },
                          "version": 1, "columns": { "meta": 1 } }
                        """),
                    StoreValue("""{ "kind": "put", "table": "app.store:edits", "row": { "id": "e001", "path": "p/1", "at": 1, "done": false }, "version": 1, "columns": {} }"""),
                ], edits.Take(2), "the first puts of edits");
                var notesTable = parts.First(part => (string)part["kind"]! == "table" && ((string)part["table"]!).EndsWith(":notes", StringComparison.Ordinal));
                Equal(2L, (long)notesTable["version"]!, "the notes table's version");
                Equal("migrating", (string?)notesTable["state"], "the notes table's state");
                var notes = parts.Where(part => (string)part["kind"]! == "put" && ((string)part["table"]!).EndsWith(":notes", StringComparison.Ordinal));
                Equal(100, notes.Count(part => (long)part["version"]! == 2), "notes rows at version 2");
                var documents = new JsonArray();
                foreach (var part in parts)
                {
                    if ((string)part["kind"]! == "document.create")
                    {
                        var record = part["record"]!.AsObject();
                        documents.Add(new JsonArray(record["key"]?.DeepClone(), record["revision"]!.DeepClone(), record["history"]!.DeepClone()));
                    }
                    else if ((string)part["kind"]! == "document.revision")
                    {
                        var content = part["content"]!.AsObject();
                        documents.Add(new JsonArray(content["kind"]!.DeepClone(), ((string)content["kind"]! == "base" ? content["value"] : content["ops"])!.DeepClone()));
                    }
                }
                // A singleton's record has no key (upstream's undefined is null here, and the field is absent).
                Check(!parts.First(part => (string)part["kind"]! == "document.create")["record"]!.AsObject().ContainsKey("key"), "the singleton's record has no key");
                Json("""
                    [
                      [null, 1, "none"], ["base", { "title": "" }],
                      ["a", 3, "none"], ["base", { "title": "a" }], ["delta", [["s", ["title"], "a2"]]], ["delta", [["s", ["title"], "a3"]]],
                      ["b", 2, "none"], ["base", { "title": "b" }], ["delta", [["s", ["title"], "b2"]]]
                    ]
                    """, documents, "the documents");
            }),
            Case("store.export.revisions-a-history-longer-than-a-page-is-read-page-by-page-and-exported-whole-in-seq-order", async () =>
            {
                await using var store = await MemoryStore();
                var count = StoreInternal.ExportPage * 2 + 1;
                var record = new DocumentRecord
                {
                    Id = 2,
                    Owner = "app.store",
                    Namespace = "session",
                    Kind = "long",
                    History = DocumentHistory.Rewindable,
                    CreatedAt = 1,
                    Revision = 1,
                    Version = 1,
                    Fingerprint = "",
                };
                await store.CommitAsync([new StagedWrite(WriteOrder.Write, async step =>
                {
                    await DocumentEngine.InsertDocumentAsync(step, record, "{}");
                    return [];
                })], open, context);
                for (var n = 1; n < count; n++)
                {
                    var ops = ChordJson.Stringify(new JsonArray(new JsonArray("s", new JsonArray("n"), n)));
                    await store.CommitAsync([new StagedWrite(WriteOrder.Write, async step =>
                    {
                        await StoreInternal.InsertRevisionAsync(step, record.Id, step.Seq, "delta", 1, ops);
                        return [];
                    })], open, context);
                }
                var revisions = (await Exported(store)).Where(part => (string)part["kind"]! == "document.revision").Select(part => (long)part["seq"]!);
                Sequence(Enumerable.Range(1, count).Select(seq => (long)seq), revisions, "the revisions' seqs");
            }),
            Case("store.export.the-commit-queue-is-held-from-the-first-next-until-the-export-ends-a-commit-waits", async () =>
            {
                await using var store = await ArchiveStore();
                var iterator = store.Export(context);
                await iterator.MoveNextAsync();
                var commit = store.CommitAsync([], open, context);
                await ChordSupport.Flushed();
                Check(!commit.IsCompleted, "the commit waits");
                while (await iterator.MoveNextAsync())
                {
                }
                await commit;
            }),
            Case("store.export.the-commit-queue-is-given-back-by-return-the-closed-iterator-keeps-nothing", async () =>
            {
                await using var store = await ArchiveStore();
                var collected = await StoreExportReturned(store, context);
                await store.CommitAsync([], open, context);
                await ChordSupport.ExpectCollected(collected);
            }),
            Case("store.export.the-commit-queue-is-given-back-at-once-when-ctx-aborts-while-the-consumer-is-suspended-a-later-next-rejects-aborted", async () =>
            {
                await using var store = await ArchiveStore();
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var iterator = store.Export(cancellable);
                await iterator.MoveNextAsync();
                // getEventListeners(signal, "abort") is 1 here and 0 after the abort: not countable on a token (the file's summary).
                cancel();
                await store.CommitAsync([], open, context);
                Rejected(await StoreRejection(iterator.MoveNextAsync().AsTask()), "aborted", null, "a later next()");
            }),
            Case("store.export.the-commit-queue-a-retained-aborted-iterator-keeps-no-page", async () =>
            {
                await using var store = await MemoryStore();
                var big = Tables.DefineTable("big", new TableOptions { Version = 1, Columns = StoreColumns(("id", Int()), ("text", Str())), Key = ["id"] });
                var staged = new List<IStagedWrite>();
                var changes = new TableChanges(await TableIn(store, big), staged);
                for (var i = 0; i < StoreInternal.ExportPage; i++)
                    changes.Put(new JsonObject { ["id"] = i, ["text"] = i.ToString(CultureInfo.InvariantCulture).PadRight(8_000, 'x') });
                await store.CommitAsync(staged, open, context);
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var before = await ChordSupport.SettledHeap();
                var iterator = store.Export(cancellable);
                // The manifest, the table write, and the first put of a page of 100 rows of 8 KB each.
                for (var i = 0; i < 3; i++) await iterator.MoveNextAsync();
                cancel();
                var after = await ChordSupport.SettledHeap();
                Check(after - before < 200 * 1024, $"the heap grew {after - before} bytes");
                Rejected(await StoreRejection(iterator.MoveNextAsync().AsTask()), "aborted", null, "a later next()");
            }),
            Case("store.export.the-commit-queue-code-that-commits-while-consuming-its-own-export-waits-until-ctx-aborts", async () =>
            {
                await using var store = await ArchiveStore();
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var iterator = store.Export(cancellable);
                await iterator.MoveNextAsync();
                var commit = store.CommitAsync([], open, context);
                await ChordSupport.Flushed();
                Check(!commit.IsCompleted, "the commit waits");
                cancel();
                await commit;
            }),
            Case("store.export.the-commit-queue-closing-the-store-ends-a-held-export-its-next-rejects-released-also-for-a-part-that-needs-no-read", async () =>
            {
                await using var store = await ArchiveStore();
                var iterator = store.Export(context);
                await iterator.MoveNextAsync();
                await store.CloseAsync(context);
                // The table write would need no read.
                Rejected(await StoreRejection(iterator.MoveNextAsync().AsTask()), "released", null, "the next part");
                Rejected(await StoreRejection(iterator.MoveNextAsync().AsTask()), "released", null, "the one after");
            }),
            Case("store.export.the-commit-queue-an-abort-while-next-waits-for-the-queue-rejects-at-once-no-abort-listener-is-left", async () =>
            {
                await using var store = await ArchiveStore();
                var first = store.Export(context);
                await first.MoveNextAsync();
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var second = store.Export(cancellable);
                var waiting = StoreRejection(second.MoveNextAsync().AsTask());
                await ChordSupport.Flushed();
                cancel();
                Rejected(await waiting, "aborted", null, "the waiting next()");
                // getEventListeners(signal, "abort") is 0: a cancelled token keeps no registration (the file's summary).
                // The first export still holds the queue; once it ends, the aborted one gives the queue back at once.
                await first.DisposeAsync();
                await store.CommitAsync([], open, context);
                Rejected(await StoreRejection(second.MoveNextAsync().AsTask()), "aborted", null, "a later next()");
            }),
            Case("store.export.the-commit-queue-closing-the-store-before-an-export-s-first-next-rejects-it-released-not-even-a-manifest", async () =>
            {
                await using var store = await ArchiveStore();
                var iterator = store.Export(context);
                await store.CloseAsync(context);
                Rejected(await StoreRejection(iterator.MoveNextAsync().AsTask()), "released", null, "the first next()");
                Rejected(await StoreRejection(iterator.MoveNextAsync().AsTask()), "released", null, "the second");
            }),
            Case("store.export.the-commit-queue-closing-the-store-while-an-export-waits-for-the-queue-rejects-its-next-released", async () =>
            {
                await using var store = await ArchiveStore();
                var first = store.Export(context);
                await first.MoveNextAsync();
                var second = store.Export(context);
                var waiting = StoreRejection(second.MoveNextAsync().AsTask());
                await ChordSupport.Flushed();
                await store.CloseAsync(context);
                Rejected(await waiting, "released", null, "the waiting next()");
            }),
            Case("store.export.the-commit-queue-pisharp-an-ended-export-leaves-no-registration-on-its-live-token", async () =>
            {
                // PiSharp: what getEventListeners shows upstream. An export holding the queue registers on its context's token; one returned
                // and one read to its end are collected while the token lives, so neither registration stayed.
                await using var store = await ArchiveStore();
                var (cancellable, cancel) = ChordContext.WithCancel(context);
                var references = new[] { await StoreExportReturned(store, cancellable), await StoreExportRead(store, cancellable) };
                await store.CommitAsync([], open, context);
                await ChordSupport.ExpectCollected(references);
                GC.KeepAlive(cancellable);
                GC.KeepAlive(cancel);
            }),
        ];
    }
}
