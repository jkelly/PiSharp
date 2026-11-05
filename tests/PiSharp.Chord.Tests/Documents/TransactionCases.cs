using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/transaction.test.ts
//
// Transactions (§9, R12, R13): one at a time per object, drafts, commits in one batch, staged changes, the seal fence, tables and persisted
// documents in a transaction. Upstream's cases serve app.owner, app.restartable or app.store through createHost (loaded, restartable,
// storeRestartable; phase 6); here they run on the hand-built host (Documents/DocumentsHost.cs). Mappings:
// - Upstream's objects() serves app.owner objects 1 and 2, each with documents a and b and a member sum, a derive over them; here each
//   object's attempt declares the same, and reference.sum is object 1's derive. The telemetry a case meters is the host's batch's metrics
//   (chord.derive.computes) and the transaction's context (the store's counters).
// - A warm draft's task is complete when DraftAsync returns: upstream's then on it, a microtask before the commit, is a continuation that
//   runs synchronously (TaskContinuationOptions.ExecuteSynchronously). A cancelled context's abort carries no reason in .NET: upstream's
//   rejects.toThrow("cancelled") is an OperationCanceledException. store.commit wrapped is ChordStore.NextCommit (holdNextCommit) and
//   CommitFences (the admission wrapped); the outbox's writes are read as their JSON.
// - "revoke their drafts when they settle": a JsonObject draft cannot be revoked (Delta/Tracker.cs), so upstream's expect(() => kept.n)
//   .toThrow() has no counterpart; a pisharp- case checks instead that a kept draft is detached (editing it changes no document) and, as
//   upstream does, that a settled transaction refuses its drafts.
// Not ported: "settles an in-memory change within five microtask turns" counts JavaScript microtask turns, which have no .NET counterpart
// (a transaction's continuations run on the thread pool); "a handler's commit held in the SQL past closeTimeout" needs a method handler's
// drain and the host's closeTimeout (phase 6).
internal static partial class Program
{
    private static readonly StateDefinition TxA = DocumentsDefine("a", Obj(("n", Int())), 1, () => new JsonObject { ["n"] = 0 });

    private static readonly StateDefinition TxB = DocumentsDefine("b", Obj(("s", Str())), 1, () => new JsonObject { ["s"] = "" });

    private static readonly StateDefinition TxN = DocumentsDefine("n", Obj(("n", Int())), 1, () => new JsonObject { ["n"] = 0 });

    private static readonly StateDefinition TxP = DocumentsDefine("p", Obj(("n", Int())), 1, () => new JsonObject { ["n"] = 0 }, persist: true);

    private static readonly StateDefinition TxK = DocumentsDefine("k", Obj(("n", Int())), 1, () => new JsonObject { ["n"] = 0 }, keyed: true, persist: true);

    private static readonly TableDefinition TxEdits = Tables.DefineTable("edits", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Str()), ("at", Int())),
        Key = ["id"],
    });

    /// <summary>An app.owner object's handles: its attempt, documents a and b, and its member sum.</summary>
    private sealed record TxHandles(DocumentsAttempt Self, ISingletonHandle A, ISingletonHandle B, Derive Sum);

    /// <summary>A store object's handles of in-memory n and the table edits.</summary>
    private sealed record TxTables(ISingletonHandle N, TableHandle Edits);

    /// <summary>A store object's handles of in-memory n, persisted p and keyed persisted k.</summary>
    private sealed record TxPersistedHandles(ISingletonHandle N, ISingletonHandle P, IKeyedHandle K);

    /// <summary>A staged change recording what happens to it, and the document value its adoption sees (upstream <c>recorder</c>).</summary>
    private sealed class TxRecorder(List<string> events, ISingletonHandle a) : IStaged
    {
        public void Adopt() => events.Add($"adopt sees {ChordJson.ToJavaScriptString(a.Value!["n"])}");

        public void Committed() => events.Add("committed");

        public void Discarded() => events.Add("discarded");
    }

    /// <summary>Objects 1 and 2 of app.owner, with two documents and a derive over them (upstream <c>objects</c>).</summary>
    private static async Task<(DocumentsHost Host, TxHandles One, TxHandles Two)> TxObjects(ITelemetryMetrics? metrics = null)
    {
        var host = new DocumentsHost(metrics: metrics);
        var handles = new Dictionary<int, TxHandles>();
        foreach (var id in new[] { 1, 2 })
        {
            await host.GetAsync("app.owner", $"app.owner({id})", (_, self) =>
            {
                var a = self.State(TxA);
                var b = self.State(TxB);
                var sum = self.Derive([a, b], values =>
                    $"{ChordJson.ToJavaScriptString(((JsonNode)values[0]!)["n"])}{ChordJson.ToJavaScriptString(((JsonNode)values[1]!)["s"])}");
                handles[id] = new TxHandles(self, a, b, sum);
            });
        }
        return (host, handles[1], handles[2]);
    }

    /// <summary>A restartable object with n; its handles by attempt (upstream <c>sealable</c>).</summary>
    private static async Task<(DocumentsObject Object, List<ISingletonHandle> Handles)> TxSealable()
    {
        var handles = new List<ISingletonHandle>();
        var made = await DocumentsHost.Restartable((_, self) => handles.Add(self.State(TxN)));
        return (made, handles);
    }

    /// <summary>A store object with n and edits, on a store with an outbox; its handles by attempt (upstream's tables <c>stored</c>).</summary>
    private static async Task<(DocumentsObject Object, ChordStore Store, List<TxTables> Handles)> TxStored(ITelemetryMetrics? metrics = null)
    {
        var store = await MemoryStore(outbox: true);
        var handles = new List<TxTables>();
        var made = await DocumentsHost.StoreRestartable((_, self) => handles.Add(new TxTables(self.State(TxN), self.Table(TxEdits))), store, metrics);
        return (made, store, handles);
    }

    /// <summary>A store object with n, p and k, on <paramref name="on"/> or a store with an outbox (upstream's persisted <c>stored</c>).</summary>
    private static async Task<(DocumentsObject Object, ChordStore Store, List<TxPersistedHandles> Handles)> TxPersisted(ITelemetryMetrics? metrics = null, ChordStore? on = null)
    {
        var store = on ?? await MemoryStore(outbox: true);
        var handles = new List<TxPersistedHandles>();
        var made = await DocumentsHost.StoreRestartable((_, self) => handles.Add(new TxPersistedHandles(self.State(TxN), self.State(TxP), self.KeyedState(TxK))), store, metrics);
        return (made, store, handles);
    }

    private static JsonObject TxRow(string id, int at) => new() { ["id"] = id, ["at"] = at };

    private static JsonObject TxKey(string id) => new() { ["id"] = id };

    /// <summary>A row as JSON.</summary>
    private static JsonObject? TxRowJson(IReadOnlyDictionary<string, JsonNode?>? row) =>
        row is null ? null : new JsonObject([.. row.Select(cell => KeyValuePair.Create(cell.Key, cell.Value?.DeepClone()))]);

    private static double TxCount(Func<IReadOnlyDictionary<string, double>> counts, string name) => counts().GetValueOrDefault(name);

    private static IEnumerable<(string, Func<Task>)> DocumentsTransactionCases() =>
    [
        Case("documents.transaction.transactions-run-one-at-a-time-per-object-and-return-what-their-callback-returns", async () =>
        {
            var (_, one, _) = await TxObjects();
            var order = new List<string>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = one.Self.TxAsync(async tx =>
            {
                lock (order) order.Add("first");
                await gate.Task;
                (await tx.DraftAsync(one.A))["n"] = 1;
                lock (order) order.Add("first done");
                return "one";
            }, DocumentsBg);
            var second = one.Self.TxAsync(async tx =>
            {
                lock (order) order.Add($"second sees {ChordJson.ToJavaScriptString(one.A.Value!["n"])}");
                var draft = await tx.DraftAsync(one.A);
                draft["n"] = DocumentsNumber(draft, "n") + 1;
                return "two";
            }, DocumentsBg);
            await Flushed();
            gate.TrySetResult();
            Sequence(["one", "two"], await Task.WhenAll(first, second), "the results");
            Sequence(["first", "first done", "second sees 1"], order, "the order");
            Json("""{"n":2}""", one.A.Value, "a");
        }),
        Case("documents.transaction.transactions-give-the-same-draft-for-the-same-document", async () =>
        {
            var (_, one, _) = await TxObjects();
            await one.Self.TxAsync(async tx => Check(ReferenceEquals(await tx.DraftAsync(one.A), await tx.DraftAsync(one.A)), "the same draft"), DocumentsBg);
        }),
        Case("documents.transaction.transactions-settle-a-warm-draft-at-once-a-continuation-the-callback-did-not-await-runs-before-the-commit-rule-3", async () =>
        {
            var (_, one, _) = await TxObjects();
            await one.Self.TxAsync(tx =>
            {
                _ = tx.DraftAsync(one.A).ContinueWith(draft => draft.Result["n"] = 7, TaskContinuationOptions.ExecuteSynchronously);
                return Task.CompletedTask;
            }, DocumentsBg);
            Json("""{"n":7}""", one.A.Value, "a");
        }),
        Case("documents.transaction.transactions-reject-a-draft-of-a-handle-a-transaction-cannot-use-instead-of-throwing", async () =>
        {
            var (_, one, two) = await TxObjects();
            await one.Self.TxAsync(async tx =>
            {
                Task<JsonObject>? draft = null;
                Accepts(() => draft = tx.DraftAsync(two.A), "the draft of another object's document");
                DocumentsRejectsWith(await Rejection(draft!), "drafts only its own object's documents", "its rejection");
            }, DocumentsBg);
        }),
        Case("documents.transaction.transactions-commit-nothing-when-nothing-changed", async () =>
        {
            var (_, context, counts) = await Metered();
            var (host, one, _) = await TxObjects(context.Telemetry!.Metrics);
            var frames = FramesOf(await one.Sum.WatchAsync(DocumentsBg));
            var value = one.A.Value;
            var computes = TxCount(counts, "chord.derive.computes");
            await one.Self.TxAsync(async tx => { _ = (await tx.DraftAsync(one.A))["n"]; }, DocumentsBg);
            host.Delivered();
            Check(ReferenceEquals(value, one.A.Value), "the same value");
            Equal(0, frames.Count, "no frame");
            Equal(computes, TxCount(counts, "chord.derive.computes"), "no compute");
        }),
        Case("documents.transaction.transactions-discard-everything-on-a-throw-and-on-an-aborted-context", async () =>
        {
            var (_, one, _) = await TxObjects();
            async Task Change(Transaction tx) => (await tx.DraftAsync(one.A))["n"] = 5;
            DocumentsRejectsWith(await Rejection(one.Self.TxAsync(async tx =>
            {
                await Change(tx);
                throw new InvalidOperationException("no");
            }, DocumentsBg)), "no", "a throw");
            using var cancel = new CancellationTokenSource();
            var cancelled = ChordContext.WithAbortSignal(cancel.Token, DocumentsBg);
            Check(await Rejection(one.Self.TxAsync(async tx =>
            {
                await Change(tx);
                cancel.Cancel();
            }, cancelled)) is OperationCanceledException, "an abort");
            Json("""{"n":0}""", one.A.Value, "a");
        }),
        Case("documents.transaction.pisharp-transactions-a-kept-draft-is-detached-and-a-settled-transaction-refuses-its-drafts", async () =>
        {
            var (_, one, _) = await TxObjects();
            JsonObject? kept = null;
            await one.Self.TxAsync(async tx =>
            {
                kept = await tx.DraftAsync(one.A);
                kept["n"] = 1;
            }, DocumentsBg);
            Json("""{"n":1}""", one.A.Value, "committed");
            // A JsonObject cannot be revoked: the kept draft is a tree of its own, and editing it changes no document.
            kept!["n"] = 9;
            Json("""{"n":1}""", one.A.Value, "after the kept draft's edit");
            var settled = await one.Self.TxAsync(tx => Task.FromResult(tx), DocumentsBg);
            DocumentsRejectsWith(await Rejection(settled.DraftAsync(one.A)), "app.owner(1): the transaction has settled", "a settled transaction's draft");
        }),
        Case("documents.transaction.transactions-refuse-another-objects-documents", async () =>
        {
            var (_, one, two) = await TxObjects();
            var rejected = await Rejection(one.Self.TxAsync(tx => tx.DraftAsync(two.A), DocumentsBg));
            Check(rejected is InvalidOperationException { Message: "app.owner(1): a transaction drafts only its own object's documents" }, $"the refusal: {rejected}");
        }),
        Case("documents.transaction.transactions-commit-the-objects-documents-in-one-batch-one-compute-one-frame", async () =>
        {
            var (_, context, counts) = await Metered();
            var (host, one, _) = await TxObjects(context.Telemetry!.Metrics);
            var frames = FramesOf(await one.Sum.WatchAsync(DocumentsBg));
            var computes = TxCount(counts, "chord.derive.computes");
            await one.Self.TxAsync(async tx =>
            {
                (await tx.DraftAsync(one.A))["n"] = 1;
                (await tx.DraftAsync(one.B))["s"] = "b";
            }, DocumentsBg);
            host.Delivered();
            DocumentsFrames("""[["1b",[["r","1b"]]]]""", frames, "one frame");
            Equal(computes + 1, TxCount(counts, "chord.derive.computes"), "one compute");
        }),
        Case("documents.transaction.transactions-commit-their-other-changes-when-they-retire-an-absent-document-r13", async () =>
        {
            var (_, one, _) = await TxObjects();
            await one.Self.TxAsync(async tx =>
            {
                await tx.RetireAsync(one.B);
                (await tx.DraftAsync(one.A))["n"] = 7;
            }, DocumentsBg);
            Json("""[{"n":7},{"s":""}]""", new JsonArray(one.A.Value!.DeepClone(), one.B.Value!.DeepClone()), "a and b");
        }),
        Case("documents.transaction.transactions-draft-a-retired-document-anew-watches-get-a-root-replacement", async () =>
        {
            var (host, one, _) = await TxObjects();
            await one.A.ChangeAsync(draft => { draft["n"] = 3; }, DocumentsBg);
            var frames = FramesOf(await one.A.WatchAsync(DocumentsBg));
            await one.Self.TxAsync(async tx =>
            {
                await tx.RetireAsync(one.A);
                var draft = await tx.DraftAsync(one.A);
                draft["n"] = DocumentsNumber(draft, "n") + 1;
            }, DocumentsBg);
            host.Delivered();
            DocumentsFrames("""[[{"n":1},[["r",{"n":1}]]]]""", frames, "a root replacement");
        }),
        Case("documents.transaction.staged-changes-adopt-in-the-commit-step-with-the-documents-then-hear-of-the-commit-once-the-line-is-free", async () =>
        {
            var (_, one, _) = await TxObjects();
            var events = new List<string>();
            var owner = new object();
            await one.Self.TxAsync(async tx =>
            {
                (await tx.DraftAsync(one.A))["n"] = 7;
                var staged = tx.Stage(owner, () => new TxRecorder(events, one.A));
                // One per owner and transaction.
                Check(ReferenceEquals(tx.Stage(owner, () => new TxRecorder([], one.A)), staged), "one per owner");
                events.Add("callback done");
            }, DocumentsBg);
            Sequence(["callback done", "adopt sees 7", "committed"], events, "the events");
        }),
        Case("documents.transaction.staged-changes-adopt-without-any-document-change-and-hear-of-a-discard-instead-when-the-transaction-throws", async () =>
        {
            var (_, one, _) = await TxObjects();
            var events = new List<string>();
            await one.Self.TxAsync(tx =>
            {
                tx.Stage(new object(), () => new TxRecorder(events, one.A));
                return Task.CompletedTask;
            }, DocumentsBg);
            await Rejection(one.Self.TxAsync(tx =>
            {
                tx.Stage(new object(), () => new TxRecorder(events, one.A));
                throw new InvalidOperationException("no");
            }, DocumentsBg));
            Sequence(["adopt sees 0", "committed", "discarded"], events, "the events");
        }),
        Case("documents.transaction.the-seal-fence-discards-the-transaction-holding-the-line-at-the-seal-the-line-is-free-its-later-uses-are-refused", async () =>
        {
            var (made, handles) = await TxSealable();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Transaction? held = null;
            var first = made.Attempts[0].TxAsync(async tx =>
            {
                (await tx.DraftAsync(handles[0]))["n"] = 1;
                held = tx;
                await gate.Task;
                return "late";
            }, DocumentsBg);
            await Flushed();
            await made.RestartAsync();
            // The next attempt's transaction runs while the cut one's callback still waits.
            await handles[1].ChangeAsync(draft => { draft["n"] = 2; }, DocumentsBg);
            gate.TrySetResult();
            DocumentsMatches("""{"code":"released"}""", await Rejection(first), "the cut transaction");
            DocumentsMatches("""{"code":"released","message":"app.restartable(1): the transaction was discarded at the seal"}""",
                await Rejection(held!.DraftAsync(handles[1])), "its later draft");
            Json("""{"n":2}""", handles[1].Value, "n");
        }),
        Case("documents.transaction.the-seal-fence-refuses-a-sealed-attempts-transactions-waiting-for-the-line-and-every-new-one-its-handles-still-read", async () =>
        {
            var (made, handles) = await TxSealable();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = made.Attempts[0].TxAsync(_ => gate.Task, DocumentsBg);
            var queued = handles[0].ChangeAsync(draft => { draft["n"] = 1; }, DocumentsBg);
            await Flushed();
            made.FailAttempt();
            DocumentsMatches("""{"code":"released"}""", await Rejection(queued), "the queued change");
            DocumentsMatches("""{"code":"released"}""", await Rejection(handles[0].ChangeAsync(_ => { }, DocumentsBg)), "a new change");
            Json("""{"n":0}""", handles[0].Value, "n still reads");
            gate.TrySetResult();
            DocumentsMatches("""{"code":"released"}""", await Rejection(first), "the transaction holding the line");
        }),
        Case("documents.transaction.tables-in-a-transaction-commit-rows-and-documents-in-one-step-the-documents-adopt-inside-it-one-seq", async () =>
        {
            var (made, store, handles) = await TxStored();
            var (n, edits) = handles[0];
            var watch = await n.WatchAsync(DocumentsBg);
            var seen = new List<long>();
            watch.Start((_, _) =>
            {
                lock (seen) seen.Add(store.LastSeq);
                return Task.CompletedTask;
            });
            made.Host.Delivered();
            var before = store.LastSeq;
            await made.Attempts[0].TxAsync(async tx =>
            {
                tx.Put(edits, TxRow("e1", 1));
                (await tx.DraftAsync(n))["n"] = 1;
            }, DocumentsBg);
            made.Host.Delivered();
            Equal(before + 1, store.LastSeq, "one seq");
            Equal(before + 1, seen[^1], "the frame after it");
            Json("""{"n":1}""", n.Value, "n");
            Json("""{"id":"e1","at":1}""", TxRowJson(await edits.GetAsync(TxKey("e1"), DocumentsBg)), "the row");
            await watch.StopAsync();
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.tables-in-a-transaction-in-memory-only-commits-take-no-step-and-no-seq-w12", async () =>
        {
            var (telemetry, counting, counts) = await Metered();
            var (made, store, handles) = await TxStored(counting.Telemetry!.Metrics);
            var commits = TxCount(counts, "chord.store.commits");
            var seq = store.LastSeq;
            await made.Attempts[0].TxAsync(async tx => { (await tx.DraftAsync(handles[0].N))["n"] = 5; }, counting);
            Equal(seq, store.LastSeq, "no seq");
            Equal(commits, TxCount(counts, "chord.store.commits"), "no commit");
            _ = telemetry;
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.tables-in-a-transaction-read-committed-rows-with-tx-page-and-tx-rows-overlay-own-writes-with-tx-get", async () =>
        {
            var (made, store, handles) = await TxStored();
            var edits = handles[0].Edits;
            await made.Attempts[0].TxAsync(tx =>
            {
                tx.Put(edits, TxRow("e1", 1));
                return Task.CompletedTask;
            }, DocumentsBg);
            await made.Attempts[0].TxAsync(async tx =>
            {
                tx.Put(edits, TxRow("e2", 2));
                Sequence(["e1"], (await tx.PageAsync(edits, Tables.Query(TxEdits), 10)).Items.Select(row => (string)row["id"]!), "the page");
                var all = new List<string>();
                await foreach (var row in tx.Rows(edits, Tables.Query(TxEdits))) all.Add((string)row["id"]!);
                Sequence(["e1"], all, "the rows");
                Json("""{"id":"e2","at":2}""", TxRowJson(await tx.GetAsync(edits, TxKey("e2"))), "its own write");
            }, DocumentsBg);
            Equal(2L, await edits.CountAsync(Tables.Query(TxEdits), DocumentsBg), "both committed");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.tables-in-a-transaction-write-nothing-on-a-throw-or-an-abort-and-refuse-a-callback-that-settles-with-a-pending-read-rule-3", async () =>
        {
            var (made, store, handles) = await TxStored();
            var (self, edits) = (made.Attempts[0], handles[0].Edits);
            var seq = store.LastSeq;
            DocumentsRejectsWith(await Rejection(self.TxAsync(tx =>
            {
                tx.Put(edits, TxRow("e1", 1));
                throw new InvalidOperationException("no");
            }, DocumentsBg)), "no", "a throw");
            using var cancel = new CancellationTokenSource();
            var cancelled = ChordContext.WithAbortSignal(cancel.Token, DocumentsBg);
            await Rejection(self.TxAsync(tx =>
            {
                tx.Put(edits, TxRow("e1", 1));
                cancel.Cancel();
                return Task.CompletedTask;
            }, cancelled));
            DocumentsRejectsWith(await Rejection(self.TxAsync(tx =>
            {
                _ = tx.GetAsync(edits, TxKey("e1"));
                tx.Put(edits, TxRow("e1", 1));
                return Task.CompletedTask;
            }, DocumentsBg)), "settled before its pending tx operations", "a pending read");
            Equal(seq, store.LastSeq, "nothing written");
            Equal(0L, await edits.CountAsync(Tables.Query(TxEdits), DocumentsBg), "no row");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        // PiSharp's own: a callback that awaits its tx read is pending when it returns, as a promise is however long the synchronous code
        // before its return runs. The read settles on the thread pool, which once finished the callback before the rule 3 check saw it
        // return and refused it (CI: "the callback settled before its pending tx operations" seeding keyed documents); the read now
        // resumes only once the check saw the callback return (Transaction.TrackedYield).
        Case("documents.transaction.pisharp-tables-in-a-transaction-accept-a-callback-that-awaits-its-read-however-late-it-returns", async () =>
        {
            var (made, store, handles) = await TxStored();
            var (self, edits) = (made.Attempts[0], handles[0].Edits);
            await self.TxAsync(tx =>
            {
                tx.Put(edits, TxRow("e1", 1));
                return Task.CompletedTask;
            }, DocumentsBg);
            var early = true;
            await self.TxAsync(tx =>
            {
                var read = ReadThenWrite();
                // Time for the thread pool to finish the callback before it returns, were the read not held.
                early = SpinWait.SpinUntil(() => read.IsCompleted, TimeSpan.FromMilliseconds(500));
                return read;

                async Task ReadThenWrite()
                {
                    Json("""{"id":"e1","at":1}""", TxRowJson(await tx.GetAsync(edits, TxKey("e1"))), "the committed row");
                    tx.Put(edits, TxRow("e2", 2));
                }
            }, DocumentsBg);
            Check(!early, "the callback finished only after it returned");
            Equal(2L, await edits.CountAsync(Tables.Query(TxEdits), DocumentsBg), "both committed");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.tables-in-a-transaction-refuse-another-objects-table", async () =>
        {
            var (made, store, _) = await TxStored();
            var other = await TxStored();
            DocumentsRejectsWith(await Rejection(made.Attempts[0].TxAsync(tx =>
            {
                tx.Put(other.Handles[0].Edits, TxRow("e1", 1));
                return Task.CompletedTask;
            }, DocumentsBg)), "writes only its own object's tables", "another object's table");
            await made.Host.CloseAsync();
            await other.Object.Host.CloseAsync();
            await store.DisposeAsync();
            await other.Store.DisposeAsync();
        }),
        Case("documents.transaction.tables-in-a-transaction-a-commit-queued-behind-another-owners-step-is-refused-at-the-seal-an-admitted-one-is-adopted", async () =>
        {
            var (made, store, handles) = await TxStored();
            // Hold the commit queue with a step that waits.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holding = store.CommitAsync([new StagedWrite(WriteOrder.Write, async _ =>
            {
                await gate.Task;
                return [new TableWrite.DropIndex("x", "y")];
            })], CommitFence.Open, DocumentsBg);
            var queued = made.Attempts[0].TxAsync(async tx =>
            {
                tx.Put(handles[0].Edits, TxRow("e1", 1));
                (await tx.DraftAsync(handles[0].N))["n"] = 1;
            }, DocumentsBg);
            await Flushed();
            made.FailAttempt();
            gate.TrySetResult();
            await StoreInternal.Settled(holding);
            DocumentsMatches("""{"code":"released"}""", await Rejection(queued), "the queued commit");
            await made.RetryAsync();
            Json("""{"n":0}""", handles[1].N.Value, "n");
            Check(await handles[1].Edits.GetAsync(TxKey("e1"), DocumentsBg) is null, "no row");
            // Admitted, then sealed while in the step: durable, adopted into the object's slots, self.tx resolves.
            store.CommitFences = fence => new CommitFence(() =>
            {
                var admitted = fence.Admit();
                if (admitted) made.FailAttempt();
                return admitted;
            }, fence.Adopt);
            var admitted = await made.Attempts[1].TxAsync(async tx =>
            {
                tx.Put(handles[1].Edits, TxRow("e2", 2));
                (await tx.DraftAsync(handles[1].N))["n"] = 2;
                return "done";
            }, DocumentsBg);
            store.CommitFences = null;
            Equal("done", admitted, "the admitted commit");
            await made.RetryAsync();
            Json("""{"n":2}""", handles[2].N.Value, "adopted");
            Json("""{"id":"e2","at":2}""", TxRowJson(await handles[2].Edits.GetAsync(TxKey("e2"), DocumentsBg)), "durable");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-commit-persisted-and-in-memory-documents-in-one-step-a-delta-commit-takes-three-statements", async () =>
        {
            var (_, counting, counts) = await Metered();
            var (made, store, handles) = await TxPersisted(counting.Telemetry!.Metrics);
            var (n, p, _) = handles[0];
            await made.Attempts[0].TxAsync(async tx =>
            {
                (await tx.DraftAsync(n))["n"] = 1;
                (await tx.DraftAsync(p))["n"] = 1;
            }, counting);
            Equal(1L, store.LastSeq, "one seq");
            var before = counts();
            await made.Attempts[0].TxAsync(async tx => { (await tx.DraftAsync(p))["n"] = 2; }, counting);
            Equal(1.0, TxCount(counts, "chord.store.commits") - before.GetValueOrDefault("chord.store.commits"), "one commit");
            // The revision, the record's conditional UPDATE, lastSeq; plus the outbox commit.
            Equal(4.0, TxCount(counts, "chord.store.statements") - before.GetValueOrDefault("chord.store.statements"), "the statements");
            Json("""[{"n":1},{"n":2}]""", new JsonArray(n.Value!.DeepClone(), p.Value!.DeepClone()), "n and p");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-a-keyed-retirement-takes-6as-statements-the-counted-delete-the-revisions-lastseq-cost-guard", async () =>
        {
            var (_, counting, counts) = await Metered();
            var (made, store, handles) = await TxPersisted(counting.Telemetry!.Metrics);
            var k = handles[0].K;
            await made.Attempts[0].TxAsync(async tx => { (await tx.DraftAsync(k, "a"))["n"] = 1; }, counting);
            var before = counts();
            await made.Attempts[0].TxAsync(tx => tx.RetireAsync(k, "a"), counting);
            // No read of the record first, no second DELETE of its row; plus the outbox commit.
            Equal(4.0, TxCount(counts, "chord.store.statements") - before.GetValueOrDefault("chord.store.statements"), "the statements");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-assign-the-created-documents-ids-by-their-order-in-the-commit-retire-and-recreate-in-one-commit", async () =>
        {
            var (made, store, handles) = await TxPersisted();
            var k = handles[0].K;
            await made.Attempts[0].TxAsync(async tx =>
            {
                (await tx.DraftAsync(k, "b"))["n"] = 1;
                (await tx.DraftAsync(k, "a"))["n"] = 2;
            }, DocumentsBg);
            await made.Attempts[0].TxAsync(async tx =>
            {
                await tx.RetireAsync(k, "b");
                (await tx.DraftAsync(k, "b"))["n"] = 3;
            }, DocumentsBg);
            var commits = await store.Outbox!.ReadAsync(0, 10, DocumentsBg);
            var created = commits.SelectMany(commit => commit.Writes.Where(write => (string?)write!["kind"] == "document.create")
                .Select(write => $"{(string?)write!["record"]!["key"]}:{ChordJson.ToJavaScriptString(write["record"]!["id"])}"));
            Sequence(["b:2", "a:3", "b:4"], created, "the created ids");
            Sequence(["document.retire", "document.create"], commits[1].Writes.Select(write => (string?)write!["kind"]), "the second commit");
            // What is stored of b is the new record's: the next change is a delta on it.
            await k.ChangeAsync("b", draft => { draft["n"] = 4; }, DocumentsBg);
            var writes = (await store.Outbox.ReadAsync(2, 10, DocumentsBg))[0].Writes;
            Equal(1, writes.Count, "one write");
            Equal(("document.change", "4"), ((string?)writes[0]!["kind"], ChordJson.ToJavaScriptString(writes[0]!["id"])), "a change of record 4");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-draft-a-cold-keyed-document-by-loading-it-inside-the-transaction-refuse-a-callback-with-a-pending-load", async () =>
        {
            var first = await TxPersisted();
            var store = first.Store;
            await first.Handles[0].K.ChangeAsync("a", draft => { draft["n"] = 1; }, DocumentsBg);
            await first.Object.Host.CloseAsync();
            var (made, _, handles) = await TxPersisted(on: store);
            var k = handles[0].K;
            await made.Attempts[0].TxAsync(async tx =>
            {
                var draft = await tx.DraftAsync(k, "a");
                draft["n"] = DocumentsNumber(draft, "n") + 1;
            }, DocumentsBg);
            Json("""{"n":2}""", await k.GetAsync("a", DocumentsBg), "a");
            DocumentsRejectsWith(await Rejection(made.Attempts[0].TxAsync(tx =>
            {
                // Not awaited: the draft is still loading when the callback returns (it rejects later, settled).
                _ = StoreInternal.Settled(tx.DraftAsync(k, "b"));
                return Task.CompletedTask;
            }, DocumentsBg)), "the callback settled before its pending tx operations", "a pending load");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-settle-a-warm-keyed-draft-at-once-a-continuation-the-callback-did-not-await-runs-before-the-commit", async () =>
        {
            var (made, store, handles) = await TxPersisted();
            var k = handles[0].K;
            await made.Attempts[0].TxAsync(async tx => { (await tx.DraftAsync(k, "a"))["n"] = 1; }, DocumentsBg);
            await made.Attempts[0].TxAsync(tx =>
            {
                _ = tx.DraftAsync(k, "a").ContinueWith(draft => draft.Result["n"] = 7, TaskContinuationOptions.ExecuteSynchronously);
                return Task.CompletedTask;
            }, DocumentsBg);
            Json("""{"n":7}""", await k.GetAsync("a", DocumentsBg), "a");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-a-commit-cut-at-the-seal-while-it-waits-in-the-stores-queue-rejects-released-at-once-writing-nothing", async () =>
        {
            var (made, store, handles) = await TxPersisted();
            // A suspended export holds the commit queue.
            var parts = store.Export(DocumentsBg);
            await parts.MoveNextAsync();
            var change = Rejection(handles[0].P.ChangeAsync(draft => { draft["n"] = 5; }, DocumentsBg));
            await Flushed();
            await made.Host.CloseAsync();
            DocumentsMatches("""{"code":"released"}""", await change, "the change");
            await parts.DisposeAsync();
            Equal(0L, store.LastSeq, "nothing written");
            var next = await TxPersisted(on: store);
            Json("""{"n":0}""", next.Handles[0].P.Value, "p");
            await next.Handles[0].P.ChangeAsync(draft => { draft["n"] = 6; }, DocumentsBg);
            Equal(1L, store.LastSeq, "the store stays usable");
            await next.Object.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.transaction.persisted-documents-in-a-transaction-a-host-closing-while-an-admitted-commit-is-in-the-sql-waits-for-its-adoption-the-store-stays-usable", async () =>
        {
            var (made, store, handles) = await TxPersisted();
            // The next commit's step waits inside the backend transaction, after its admission.
            var (inStep, release) = HoldNextCommit(store);
            // Not from a method handler: the drain does not wait for it.
            var change = handles[0].P.ChangeAsync(draft => { draft["n"] = 5; }, DocumentsBg);
            await inStep;
            var closing = made.Host.CloseAsync();
            await Flushed();
            Check(!closing.IsCompleted, "the close waits for the admitted step");
            release();
            await change;
            await closing;
            Equal(1L, store.LastSeq, "durable");
            var next = await TxPersisted(on: store);
            Json("""{"n":5}""", next.Handles[0].P.Value, "p");
            await next.Handles[0].P.ChangeAsync(draft => { draft["n"] = 6; }, DocumentsBg);
            Equal(2L, store.LastSeq, "the store stays usable");
            await next.Object.Host.CloseAsync();
            await store.DisposeAsync();
        }),
    ];
}
