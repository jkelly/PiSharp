using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/store.test.ts. Every case is ported, over ChordStore on
// TestSqliteDatabase (ChordSupport.cs); the it.each rows are a case each. A store on a file lives in a fresh temporary directory, deleted
// after its case (upstream's mkdtemp and afterEach). Two cases start from a store near the seq bound that upstream makes with importStore
// (an empty store's export with the manifest's lastSeq edited); import comes with area cv2-store-archive, so here chord_meta's last_seq is
// written by hand before the store opens, which is the state that import leaves. What each case asserts after that is upstream's.
// String(error) is StoreText (an exception's type and message); a plain Error thrown by a test is an InvalidOperationException; the
// receipts' JavaScript template of an id array (`${ids}`) is its ids joined by commas. Telemetry counts are compared as a JSON object.
internal static partial class Program
{
    /// <summary>A table a series creates (upstream <c>Edits</c>).</summary>
    private static readonly TableDefinition StoreStoreEdits = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("at", Int())),
        Key = ["id"],
    });

    private const string StoreStoreTable = "app.store:edits";

    /// <summary>A fence that admits and records what it adopted (upstream <c>recording</c>).</summary>
    private static (List<(long Seq, IReadOnlyList<long> Ids)> Adopted, CommitFence Fence) StoreRecording()
    {
        var adopted = new List<(long, IReadOnlyList<long>)>();
        return (adopted, new CommitFence(() => true, (seq, ids) => adopted.Add((seq, ids))));
    }

    /// <summary>The adopted pairs as text, for toEqual.</summary>
    private static string StoreAdopted(IEnumerable<(long Seq, IReadOnlyList<long> Ids)> adopted) =>
        string.Join("; ", adopted.Select(each => $"{each.Seq} [{string.Join(",", each.Ids)}]"));

    /// <summary>Creates the singleton at the address and returns its id (upstream <c>created</c>).</summary>
    private static async Task<long> StoreCreated(ChordStore store)
    {
        var (adopted, fence) = StoreRecording();
        var @base = StoreOutboxBase();
        await store.CommitAsync([StoreInternal.CreateDocument(StoreOutboxAddress(), @base, ChordJson.Stringify(@base.Value))], fence, ChordContext.Background);
        return adopted[0].Ids[0];
    }

    /// <summary>A creation at the address with key <paramref name="key"/>, its text <c>{}</c>.</summary>
    private static IStagedWrite StoreCreate(string key = "") => StoreInternal.CreateDocument(StoreOutboxAddress(key), StoreOutboxBase(), "{}");

    /// <summary>A delta change of <paramref name="id"/> on <paramref name="revision"/> setting n to <paramref name="n"/>.</summary>
    private static IStagedWrite StoreDelta(long id, long revision, int n)
    {
        var ops = new JsonArray(new JsonArray("s", new JsonArray("n"), n));
        return StoreInternal.ChangeDocument(id, revision, new DocumentContent.Delta(1, ops), ChordJson.Stringify(ops));
    }

    /// <summary>A staged write whose step fails in the backend: its own code throws, a refusal (upstream <c>BROKEN</c>).</summary>
    private static readonly IStagedWrite StoreBroken = new StagedWrite(WriteOrder.Write, async step =>
    {
        await step.RunAsync("INSERT INTO no_such_table VALUES (1)");
        return [new DocumentWrite.Retire(0, 0)];
    });

    /// <summary>An attachment whose failures are told to <paramref name="told"/>.</summary>
    private static Attachment StoreAttachment(List<Exception> told) => new(told.Add, new Batch(_ => { }, null));

    /// <summary>A store on <c>:memory:</c> whose last seq stands at <paramref name="lastSeq"/>, as upstream's import of an empty store's
    /// export with that lastSeq leaves it.</summary>
    private static async Task<ChordStore> StoreAtSeq(long lastSeq)
    {
        var db = new TestSqliteDatabase(":memory:");
        await StoreInternal.ApplyStoreSchemaAsync(db);
        await db.RunAsync($"UPDATE {StoreInternal.StoreTables.Meta} SET last_seq = ? WHERE singleton = 1", lastSeq);
        return await StoreOver(db);
    }

    /// <summary>Runs <paramref name="run"/> with a path in a fresh directory, deleting the directory after it.</summary>
    private static async Task StoreOnFile(Func<string, Task> run)
    {
        var directory = Directory.CreateTempSubdirectory("chord-store-");
        try
        {
            await run(Path.Combine(directory.FullName, "store.sqlite"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A series commit (upstream's <c>{ check, writes }</c>).</summary>
    private sealed class StoreSeriesCommit(Func<Step, Task> check, IReadOnlyList<IStagedWrite> writes) : ISeriesCommit
    {
        public IReadOnlyList<IStagedWrite> Writes { get; } = writes;

        public Task CheckAsync(Step step) => check(step);
    }

    /// <summary>A series commit with no check of its own (upstream <c>unchecked</c>).</summary>
    private static ISeriesCommit StoreUnchecked(params IStagedWrite[] writes) => new StoreSeriesCommit(_ => Task.CompletedTask, writes);

    /// <summary>Telemetry counts as JSON, for toEqual.</summary>
    private static JsonObject StoreCounts(IReadOnlyDictionary<string, double> counts) =>
        new([.. counts.Select(count => KeyValuePair.Create(count.Key, (JsonNode?)JsonValue.Create(count.Value)))]);

    private static IEnumerable<(string, Func<Task>)> StoreStoreCases()
    {
        var context = ChordContext.Background;
        var unknownOutcomes = new (string Statement, IStagedWrite? Write)[] { ("BEGIN IMMEDIATE", null), ("COMMIT", null), ("ROLLBACK", StoreBroken) };
        static string Kebab(string statement) => statement.ToLowerInvariant().Replace(' ', '-');
        return
        [
            Case("store.store.opening-creates-a-new-store-at-seq-0-without-an-outbox-and-reopens-a-file-at-its-last-seq-and-ids", () => StoreOnFile(async path =>
            {
                var store = await StoreOver(new TestSqliteDatabase(path));
                Equal(0L, store.LastSeq, "a new store's seq");
                Equal(null, store.Outbox, "no outbox");
                Equal(2L, await StoreCreated(store), "the first id");
                await store.CloseAsync(context);
                var reopened = await StoreOver(new TestSqliteDatabase(path));
                Equal(1L, reopened.LastSeq, "the reopened seq");
                var (adopted, fence) = StoreRecording();
                await reopened.CommitAsync([StoreCreate("b")], fence, context);
                Equal("2 [3]", StoreAdopted(adopted), "the next seq and id");
                await reopened.CloseAsync(context);
            })),
            Case("store.store.opening-refuses-a-database-that-is-not-utf-8-closing-the-backend-rule-12", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                await db.ExecAsync("PRAGMA encoding = \"UTF-16le\"");
                var refused = StoreText(await StoreRejection(Tables.OpenStoreAsync(Tables.SqliteBackend(db), context)));
                Check(refused.Contains("not UTF-8", StringComparison.Ordinal), $"the refusal: actual <{refused}>");
                await StoreRejection(db.GetAsync("SELECT 1"));
            }),
            Case("store.store.the-commit-step-assigns-one-seq-per-commit-applies-retirements-before-creations-and-adopts-before-the-next-step", async () =>
            {
                await using var store = await MemoryStore();
                var id = await StoreCreated(store);
                var events = new List<string>();
                CommitFence Fence(string name) => new(() => true, (seq, ids) => events.Add($"{name} {seq} {string.Join(",", ids)}"));
                var first = store.CommitAsync([StoreCreate(), StoreInternal.RetireDocument(id, 1)], Fence("recreate"), context);
                var second = store.CommitAsync([StoreDelta(3, 1, 1)], Fence("change"), context);
                Sequence([2L, 3L], await Task.WhenAll(first, second), "the seqs");
                Sequence(["recreate 2 3", "change 3 "], events, "the adoptions");
                Equal(3L, store.LastSeq, "the last seq");
            }),
            Case("store.store.the-commit-step-rejects-a-commit-its-fence-refuses-at-the-entry-released-writing-nothing-an-empty-commit-takes-no-seq", async () =>
            {
                await using var store = await MemoryStore();
                var refused = store.CommitAsync([StoreCreate()], new CommitFence(() => false, (_, _) => { }), context);
                Rejected(await StoreRejection(refused), "released", null, "the refused admission");
                var (adopted, fence) = StoreRecording();
                Equal(0L, await store.CommitAsync([], fence, context), "an empty commit's seq");
                Equal("0 []", StoreAdopted(adopted), "its adoption");
                Equal(2L, await StoreCreated(store), "nothing was written");
            }),
            Case("store.store.the-commit-step-numbers-up-to-number-max-safe-integer-after-an-import-near-it-then-fails-storage-failed-no-seq-repeats", async () =>
            {
                await using var store = await StoreAtSeq(StoreInternal.MaxSafeIntegerLong - 1);
                Equal(2L, await StoreCreated(store), "the last commit's id");
                Equal(StoreInternal.MaxSafeIntegerLong, store.LastSeq, "the last seq");
                var refused = store.CommitAsync([StoreCreate("b")], StoreRecording().Fence, context);
                Rejected(await StoreRejection(refused), "unavailable", "storage_failed", "a commit past the bound");
                Equal(StoreInternal.MaxSafeIntegerLong, store.LastSeq, "no seq repeats");
                Rejected(await StoreRejection(store.ReadAsync(_ => Task.FromResult<object?>(null), context)), null, "storage_failed", "a read after the stop");
            }),
            Case("store.store.the-commit-step-counts-one-commit-and-three-statements-for-a-delta-four-with-an-outbox-cost-guard", async () =>
            {
                foreach (var (outbox, statements) in new[] { (false, 3), (true, 4) })
                {
                    await using var store = await MemoryStore(outbox);
                    var id = await StoreCreated(store);
                    var telemetry = await ChordSupport.Metered();
                    await store.CommitAsync([StoreDelta(id, 1, 1)], StoreRecording().Fence, telemetry.Context);
                    Json($$"""{ "chord.store.commits": 1, "chord.store.statements": {{statements}} }""", StoreCounts(telemetry.Counts()), $"outbox {outbox}");
                }
            }),
            Case("store.store.refusals-and-unknown-outcomes-d4-a-step-whose-own-code-throws-is-refused-the-commit-rejects-with-that-error-nothing-is-adopted-the-store-goes-on", async () =>
            {
                await using var store = await MemoryStore();
                var told = new List<Exception>();
                store.Attach(StoreAttachment(told));
                var (adopted, fence) = StoreRecording();
                var error = await StoreRejection(store.CommitAsync([StoreBroken], fence, context));
                Check(StoreText(error).Contains("no such table", StringComparison.Ordinal), $"the step's own error: actual <{StoreText(error)}>");
                Check(error is Exception and not ChordError, "not a ChordError");
                Sequence([0, 0, 0L], [adopted.Count, told.Count, store.LastSeq], "nothing adopted or told, no seq");
                Equal(2L, await StoreCreated(store), "the store goes on");
                Equal(1L, store.LastSeq, "at the next seq");
            }),
            .. unknownOutcomes.Select(row => Case(
                $"store.store.refusals-and-unknown-outcomes-d4-a-failed-{Kebab(row.Statement)}-is-an-unknown-outcome-the-store-stops-storage-failed-its-host-told-once", async () =>
                {
                    var (store, fail) = await FaultyStore();
                    var told = new List<Exception>();
                    store.Attach(StoreAttachment(told));
                    fail(row.Statement);
                    IStagedWrite[] writes = row.Write is null ? [StoreCreate()] : [row.Write];
                    Rejected(await StoreRejection(store.CommitAsync(writes, StoreRecording().Fence, context)), "unavailable", "storage_failed", "the commit");
                    Rejected(await StoreRejection(store.CommitAsync([StoreCreate()], StoreRecording().Fence, context)), null, "storage_failed", "a later commit");
                    Rejected(await StoreRejection(store.ReadAsync(_ => Task.FromResult(1), context)), null, "storage_failed", "a read");
                    Equal(1, told.Count, "the host told once");
                    await store.CloseAsync(context);
                    await store.CloseAsync(context);
                })),
            Case("store.store.refusals-and-unknown-outcomes-d4-an-adopt-that-throws-stops-the-store-an-empty-commit-s-too-a-conditional-update-on-another-revision-is-refused", async () =>
            {
                await using var store = await MemoryStore();
                var id = await StoreCreated(store);
                var throwing = new CommitFence(() => true, (_, _) => throw new InvalidOperationException("adopt threw"));
                Rejected(await StoreRejection(store.CommitAsync([StoreInternal.RetireDocument(id, 1)], throwing, context)), null, "storage_failed", "a throwing adopt");
                await using var empty = await MemoryStore();
                Rejected(await StoreRejection(empty.CommitAsync([], throwing, context)), null, "storage_failed", "an empty commit's throwing adopt");
                Rejected(await StoreRejection(empty.ReadAsync(_ => Task.FromResult(1), context)), null, "storage_failed", "a read after it");
                await using var other = await MemoryStore();
                var otherId = await StoreCreated(other);
                var stale = StoreInternal.ChangeDocument(otherId, 7, new DocumentContent.Delta(1, []), "[]");
                var refused = StoreText(await StoreRejection(other.CommitAsync([stale], StoreRecording().Fence, context)));
                Check(refused.Contains("not live at revision 7", StringComparison.Ordinal), $"a stale change: actual <{refused}>");
                Equal(1L, other.LastSeq, "no seq");
                Equal(1, await other.ReadAsync(_ => Task.FromResult(1), context), "the store goes on");
            }),
            Case("store.store.refusals-and-unknown-outcomes-d4-spare-a-read-whose-context-was-aborted-aborted-and-reject-a-read-that-settles-after-the-stop", async () =>
            {
                var (store, fail) = await FaultyStore();
                var (cancelled, cancel) = ChordContext.WithCancel(context);
                cancel();
                var failing = store.ReadAsync<int>(_ => Task.FromException<int>(new InvalidOperationException("backend")), cancelled);
                Rejected(await StoreRejection(failing), "aborted", null, "the aborted read");
                var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var late = store.ReadAsync(_ => gate.Task, context);
                fail("COMMIT");
                await StoreRejection(store.CommitAsync([StoreCreate()], StoreRecording().Fence, context));
                gate.SetResult(1);
                Rejected(await StoreRejection(late), null, "storage_failed", "the read settling after the stop");
                await store.CloseAsync(context);
            }),
            Case("store.store.refusals-and-unknown-outcomes-d4-runs-a-read-transaction-s-queries-on-one-state-guarded-as-a-read", async () =>
            {
                await using var store = await MemoryStore();
                await StoreCreated(store);
                var counts = await store.ReadTransactionAsync(async executor => new JsonArray(
                    StoreSqlJson(await executor.GetAsync("SELECT count(*) AS n FROM chord_documents")),
                    StoreSqlJson(await executor.GetAsync("SELECT 1 AS one"))), context);
                Json("""[{ "n": 1 }, { "one": 1 }]""", counts, "one state");
                Rejected(await StoreRejection(store.ReadTransactionAsync<int>(_ => Task.FromException<int>(new InvalidOperationException("backend")), context)),
                    null, "storage_failed", "a failing read transaction");
            }),
            Case("store.store.the-id-allocator-d3-starts-at-2-takes-a-creation-s-id-as-it-applies-and-gives-a-mint-during-a-running-step-the-next-one", async () =>
            {
                await using var store = await MemoryStore(outbox: true);
                var (entered, release) = HoldNextCommit(store);
                var commit = store.CommitAsync([StoreCreate()], StoreRecording().Fence, context);
                await entered;
                Equal(3L, store.MintId(), "a mint during the step");
                release();
                await commit;
                Equal(4L, store.MintId(), "a mint after it");
                var first = (await store.Outbox!.ReadAsync(0, 10, context))[0];
                Sequence([4L, 2L], [first.NextId, long.Parse(first.Writes[0]!["record"]!["id"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)], "the commit's next id and the creation's id");
                Equal(4L, store.NextId, "the store's next id");
            }),
            Case("store.store.the-id-allocator-d3-leaves-a-refused-step-s-ids-unused-and-keeps-the-raised-value", async () =>
            {
                await using var store = await MemoryStore();
                await StoreRejection(store.CommitAsync([StoreCreate(), StoreBroken], StoreRecording().Fence, context));
                Equal(3L, await StoreCreated(store), "the next id");
            }),
            Case("store.store.the-id-allocator-d3-saturates-at-number-max-safe-integer-across-reopen-mints-and-chord-creations-are-refused-the-store-goes-on", () => StoreOnFile(async path =>
            {
                var max = StoreInternal.MaxSafeIntegerLong;
                var raising = new StagedWrite(WriteOrder.Write, step =>
                {
                    step.Ids.Raise(max);
                    return Task.FromResult<IReadOnlyList<StoreWrite>>([]);
                });
                var store = await StoreOver(new TestSqliteDatabase(path));
                await store.CommitAsync([raising], StoreRecording().Fence, context);
                Equal(max, store.NextId, "raised to the bound");
                await store.CloseAsync(context);
                var reopened = await StoreOver(new TestSqliteDatabase(path));
                Equal(max, reopened.NextId, "the reopened allocator");
                StoreThrows(() => reopened.MintId(), "ID space is exhausted", "a mint");
                var refused = StoreText(await StoreRejection(reopened.CommitAsync([StoreCreate()], StoreRecording().Fence, context)));
                Check(refused.Contains("ID space is exhausted", StringComparison.Ordinal), $"a creation: actual <{refused}>");
                Equal(1, await reopened.ReadAsync(_ => Task.FromResult(1), context), "the store goes on");
                await reopened.CloseAsync(context);
            })),
            Case("store.store.one-host-per-store-rule-7-refuses-a-second-attachment-until-the-first-detaches", async () =>
            {
                await using var store = await MemoryStore();
                var attachment = StoreAttachment([]);
                store.Attach(attachment);
                Check(ReferenceEquals(attachment.Batch, store.Batch), "the attached host's batch");
                StoreThrows(() => store.Attach(attachment), "already attached", "a second attachment");
                store.Detach();
                Equal(null, store.Batch, "no batch once detached");
                store.Attach(attachment);
            }),
            Case("store.store.close-waits-for-the-running-step-rejects-queued-commits-and-later-reads-released-and-closes-the-backend", async () =>
            {
                var store = await MemoryStore();
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var create = StoreCreate();
                var held = new StagedWrite(WriteOrder.Create, async step =>
                {
                    entered.TrySetResult();
                    await gate.Task;
                    return await create.ApplyAsync(step);
                });
                var running = store.CommitAsync([held], StoreRecording().Fence, context);
                await entered.Task;
                var queued = store.CommitAsync([StoreCreate("q")], StoreRecording().Fence, context);
                var closed = ((IStore)store).CloseAsync(context);
                gate.SetResult();
                Equal(1L, await running, "the running step");
                Rejected(await StoreRejection(queued), "released", null, "the queued commit");
                await closed;
                Rejected(await StoreRejection(store.ReadAsync(_ => Task.FromResult(1), context)), "released", null, "a later read");
            }),
            Case("store.store.a-commit-series-stores-its-commits-in-one-transaction-one-seq-each-each-checked-against-the-ones-before-it", async () =>
            {
                await using var store = await MemoryStore(outbox: true);
                var seen = new List<long?>();
                var receipts = new List<string>();
                var telemetry = await ChordSupport.Metered();
                var result = await store.CommitSeriesAsync(
                [
                    StoreUnchecked(StoreCreate()),
                    // No writes: no seq, it stands at the commit before it.
                    StoreUnchecked(),
                    // The check reads through its step: it sees the first commit's creation.
                    new StoreSeriesCommit(async step => seen.Add((await DocumentEngine.FindDocumentAsync(step, StoreOutboxAddress()))?.Id), [StoreCreate("b")]),
                ], (index, seq, ids) => receipts.Add($"{index} {seq} [{string.Join(",", ids)}]"), telemetry.Context);
                Equal(new SeriesResult(3), result, "the result");
                Sequence([2L], seen, "the check's read");
                Sequence(["0 1 [2]", "1 1 []", "2 2 [3]"], receipts, "the receipts");
                Equal(2L, store.LastSeq, "the last seq");
                Equal(2d, telemetry.Counts()["chord.store.commits"], "the commits counted");
                var outbox = await store.ReadAsync(executor => executor.AllAsync("SELECT seq FROM chord_outbox"), context);
                Json("[{ \"seq\": 1 }, { \"seq\": 2 }]", StoreSqlJson(outbox), "an outbox commit each");
            }),
            Case("store.store.a-commit-series-stops-at-a-check-that-refuses-the-commits-before-it-are-stored-it-and-the-ones-after-are-not", async () =>
            {
                await using var store = await MemoryStore();
                var refusal = new InvalidOperationException("refused");
                var receipts = new List<int>();
                var result = await store.CommitSeriesAsync(
                [
                    StoreUnchecked(StoreCreate()),
                    new StoreSeriesCommit(_ => Task.FromException(refusal), [StoreCreate("b")]),
                    StoreUnchecked(StoreCreate("c")),
                ], (index, _, _) => receipts.Add(index), context);
                Equal(new SeriesResult(1, refusal), result, "the result");
                Sequence([0], receipts, "the receipts");
                Equal(1L, store.LastSeq, "the last seq");
                var keys = await store.ReadAsync(executor => executor.AllAsync("SELECT key FROM chord_documents ORDER BY id"), context);
                Json("""[{ "key": "" }]""", StoreSqlJson(keys), "the stored keys");
                // The store goes on at the next seq.
                var (adopted, fence) = StoreRecording();
                await store.CommitAsync([StoreCreate("b")], fence, context);
                Equal("2 [3]", StoreAdopted(adopted), "the next commit");
            }),
            Case("store.store.a-commit-series-rolls-the-whole-series-back-when-a-check-s-statement-fails-even-if-the-check-throws-its-own-error", async () =>
            {
                await using var store = await MemoryStore();
                var own = new InvalidOperationException("own");
                var series = store.CommitSeriesAsync(
                [
                    StoreUnchecked(StoreCreate()),
                    new StoreSeriesCommit(async step =>
                    {
                        try
                        {
                            await step.GetAsync("SELECT * FROM no_such_table");
                        }
                        catch (Exception)
                        {
                            // Swallowed, as upstream's .catch(() => {}).
                        }
                        throw own;
                    }, []),
                ], (_, _, _) => { }, context);
                Check(ReferenceEquals(own, await StoreRejection(series)), "the check's own error");
                Equal(0L, store.LastSeq, "rolled back");
                // A rollback, not a stop: the store goes on; the id the rolled back creation took is not given back (D3).
                Equal(3L, await StoreCreated(store), "the next id");
            }),
            Case("store.store.a-commit-series-rolls-the-whole-series-back-when-a-commit-s-writes-throw-the-store-going-on", async () =>
            {
                await using var store = await MemoryStore();
                var receipts = new List<int>();
                var error = await StoreRejection(store.CommitSeriesAsync([StoreUnchecked(StoreCreate()), StoreUnchecked(StoreBroken)],
                    (index, _, _) => receipts.Add(index), context));
                Check(StoreText(error).Contains("no such table", StringComparison.Ordinal), $"the writes' error: actual <{StoreText(error)}>");
                Sequence([0L, 0L], [receipts.Count, store.LastSeq], "nothing received or stored");
                Equal(3L, await StoreCreated(store), "the store goes on");
            }),
            Case("store.store.a-commit-series-writes-the-metadata-once-per-series-three-statements-per-delta-with-its-outbox-commit-plus-one-cost-guard", async () =>
            {
                await using var store = await MemoryStore(outbox: true);
                var id = await StoreCreated(store);
                var telemetry = await ChordSupport.Metered();
                await store.CommitSeriesAsync([StoreUnchecked(StoreDelta(id, 1, 1)), StoreUnchecked(StoreDelta(id, 2, 2)), StoreUnchecked(StoreDelta(id, 3, 3))],
                    (_, _, _) => { }, telemetry.Context);
                // A delta committed alone is four statements with an outbox, the metadata among them; a series writes it once.
                Json($$"""{ "chord.store.commits": 3, "chord.store.statements": {{3 * 3 + 1}} }""", StoreCounts(telemetry.Counts()), "the counts");
                var meta = await store.ReadAsync(executor => executor.GetAsync("SELECT last_seq FROM chord_meta"), context);
                Equal(4L, (long?)meta?["last_seq"], "the metadata");
            }),
            .. unknownOutcomes.Select(row => Case(
                $"store.store.a-commit-series-a-failed-{Kebab(row.Statement)}-is-an-unknown-outcome-nothing-is-received-the-store-stops-storage-failed", async () =>
                {
                    var (store, fail) = await FaultyStore();
                    var told = new List<Exception>();
                    store.Attach(StoreAttachment(told));
                    fail(row.Statement);
                    var receipts = new List<int>();
                    var second = row.Write ?? StoreCreate("b");
                    var series = store.CommitSeriesAsync([StoreUnchecked(StoreCreate()), StoreUnchecked(second)], (index, _, _) => receipts.Add(index), context);
                    Rejected(await StoreRejection(series), "unavailable", "storage_failed", "the series");
                    Sequence([0L, 0L, 1L], [receipts.Count, store.LastSeq, told.Count], "nothing received or stored, the host told once");
                    var after = store.CommitSeriesAsync([StoreUnchecked(StoreCreate())], (_, _, _) => { }, context);
                    Rejected(await StoreRejection(after), null, "storage_failed", "a later series");
                    await store.CloseAsync(context);
                })),
            Case("store.store.a-commit-series-lets-a-later-commit-see-the-table-records-an-earlier-one-changed-a-rollback-leaves-the-catalog-as-it-was", async () =>
            {
                await using var store = await MemoryStore();
                var edits = StoreStoreEdits;
                var table = StoreInternal.StageTable(new TableWrite.Definition(StoreStoreTable, edits.Version, TableState.Ready, edits.Record, edits.Key, edits.Fingerprint));
                await StoreRejection(store.CommitSeriesAsync([StoreUnchecked(table), StoreUnchecked(StoreBroken)], (_, _, _) => { }, context));
                Equal(null, store.Table(StoreStoreTable), "the catalog after the rollback");
                var seen = new List<long?>();
                var sees = new StagedWrite(WriteOrder.Write, step =>
                {
                    seen.Add(step.Table(StoreStoreTable)?.Version);
                    return Task.FromResult<IReadOnlyList<StoreWrite>>([]);
                });
                var later = new StoreSeriesCommit(step =>
                {
                    seen.Add(step.Table(StoreStoreTable)?.Version);
                    return Task.CompletedTask;
                }, [sees]);
                Equal(new SeriesResult(2), await store.CommitSeriesAsync([StoreUnchecked(table), later], (_, _, _) => { }, context), "the result");
                Sequence([1L, 1L], seen, "the later commit's check and writes");
                Equal(1L, store.Table(StoreStoreTable)?.Version, "the catalog after it");
            }),
            Case("store.store.a-commit-series-runs-a-commit-queued-behind-a-series-after-the-series-receipts", async () =>
            {
                await using var store = await MemoryStore();
                var events = new List<string>();
                var series = store.CommitSeriesAsync([StoreUnchecked(StoreCreate()), StoreUnchecked(StoreCreate("b"))],
                    (index, seq, _) => events.Add($"receive {index} {seq}"), context);
                var fence = new CommitFence(() =>
                {
                    events.Add("admit");
                    return true;
                }, (seq, _) => events.Add($"adopt {seq}"));
                var queued = store.CommitAsync([StoreCreate("c")], fence, context);
                await Task.WhenAll(series, queued);
                Sequence(["receive 0 1", "receive 1 2", "admit", "adopt 3"], events, "the order");
            }),
            Case("store.store.a-commit-series-counts-only-commits-with-writes-against-the-seq-bound-empty-ones-at-number-max-safe-integer-still-stand", async () =>
            {
                var max = StoreInternal.MaxSafeIntegerLong;
                await using var store = await StoreAtSeq(max - 1);
                await StoreCreated(store);
                var receipts = new List<string>();
                var empty = await store.CommitSeriesAsync([StoreUnchecked(), StoreUnchecked()], (index, seq, _) => receipts.Add($"{index} {seq}"), context);
                Equal(new SeriesResult(2), empty, "the empty series");
                Sequence([$"0 {max}", $"1 {max}"], receipts, "their receipts at the bound");
                var writing = store.CommitSeriesAsync([StoreUnchecked(StoreCreate("b"))], (_, _, _) => { }, context);
                Rejected(await StoreRejection(writing), null, "storage_failed", "a series with writes past the bound");
            }),
            Case("store.store.a-commit-series-stops-the-store-when-a-receipt-throws", async () =>
            {
                await using var store = await MemoryStore();
                var series = store.CommitSeriesAsync([StoreUnchecked(StoreCreate())], (_, _, _) => throw new InvalidOperationException("receipt threw"), context);
                Rejected(await StoreRejection(series), null, "storage_failed", "the series");
                Rejected(await StoreRejection(store.ReadAsync(_ => Task.FromResult(1), context)), null, "storage_failed", "a read after it");
            }),
        ];
    }
}
