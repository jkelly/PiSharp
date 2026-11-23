using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static PiSharp.Chord.Store.Expressions;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/store/store.test.ts, the five cases 1.6.0 added (describe
// "settled" and "onCommitted"), over the helpers of StoreCases.cs; and authored cases (store.*.160-*) of what 1.6.0 changed in
// expression.ts, query.ts, evaluate.ts and page.ts that no upstream store test asserts on its own. Differences from upstream:
// - Upstream announces a commit to its onCommitted listeners in a microtask, so a test awaits `Promise.resolve()` or a macrotask; here the
//   announcement is queued to the thread pool and a case waits until it arrived (Sync160Until).
// - "never inside a commit" is asserted as upstream asserts it: the listener is not called while the call that queues the commit runs.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StoreSync160StoreCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("store.store.settled-resolves-once-the-commits-queued-before-it-were-adopted-or-rejected", async () =>
            {
                await using var store = await MemoryStore();
                var events = new List<string>();
                CommitFence Fence(string name, bool admit = true) => new(() => admit, (_, _) => { lock (events) events.Add($"adopt {name}"); });
                var held = HoldNextCommit(store);
                var running = store.CommitAsync([StoreCreate("a")], Fence("a"), context);
                await held.Entered;
                var queued = store.CommitAsync([StoreCreate("b")], Fence("b"), context);
                var refused = StoreRejection(store.CommitAsync([StoreCreate("c")], Fence("c", false), context));
                var settled = store.SettledAsync().ContinueWith(_ => { lock (events) events.Add("settled"); }, TaskScheduler.Default);
                await Task.Delay(30);
                Equal(0, events.Count, "nothing settled while the first commit is held");
                held.Release();
                await settled;
                Sequence(["adopt a", "adopt b", "settled"], events, "settled after the commits queued before it");
                Equal(1L, await running, "the running commit");
                Equal(2L, await queued, "the queued commit");
                Rejected(await refused, "released", null, "the refused commit");
            }),
            Case("store.store.settled-does-not-wait-for-a-commit-queued-after-the-call-resolves-on-a-closed-store", async () =>
            {
                var store = await MemoryStore();
                var before = store.SettledAsync();
                var held = HoldNextCommit(store);
                var running = store.CommitAsync([StoreCreate("a")], StoreRecording().Fence, context);
                await held.Entered;
                await before;
                held.Release();
                await running;
                await ((IStore)store).CloseAsync(context);
                await store.SettledAsync();
            }),
            Case("store.store.on-committed-announces-the-new-last-seq-after-commits-are-durable-once-per-turn-never-inside-a-commit", async () =>
            {
                await using var store = await MemoryStore();
                var seen = new List<long>();
                var inside = false;
                var calledInside = false;
                var errors = new List<Exception>();
                var remove = store.OnCommitted(seq =>
                {
                    if (Volatile.Read(ref inside)) calledInside = true;
                    lock (seen) seen.Add(seq);
                }, error => { lock (errors) errors.Add(error); });
                List<long> Seen()
                {
                    lock (seen) return [.. seen];
                }
                var fence = StoreRecording().Fence;
                Volatile.Write(ref inside, true);
                var first = store.CommitAsync([StoreCreate()], fence, context);
                Volatile.Write(ref inside, false);
                await first;
                await Sync160Until(() => Seen().Count == 1, "the first commit is announced");
                Sequence([store.LastSeq], Seen(), "its seq");
                Check(!calledInside, "never inside a commit");
                // Several commits before the announcement runs: the last call carries the last seq.
                await store.CommitAsync([StoreCreate("b")], fence, context);
                await store.CommitAsync([StoreCreate("c")], fence, context);
                await Sync160Until(() => Seen()[^1] == store.LastSeq, "the last seq is announced");
                remove.Dispose();
                await Task.Delay(30);
                var announced = Seen().Count;
                await store.CommitAsync([StoreCreate("d")], fence, context);
                await Task.Delay(50);
                Equal(announced, Seen().Count, "a removed listener hears nothing");
                Equal(store.LastSeq - 1, Seen()[^1], "its last seq is the one before");
                Equal(0, errors.Count, "no listener error");
            }),
            Case("store.store.on-committed-announces-a-series-and-a-refused-commit-not-at-all", async () =>
            {
                await using var store = await MemoryStore();
                var seen = new List<long>();
                List<long> Seen()
                {
                    lock (seen) return [.. seen];
                }
                using var listening = store.OnCommitted(seq => { lock (seen) seen.Add(seq); }, error => throw error);
                await store.CommitSeriesAsync([StoreUnchecked(StoreCreate())], (_, _, _) => { }, context);
                await Sync160Until(() => Seen().Count == 1, "the series is announced");
                Sequence([store.LastSeq], Seen(), "its seq");
                await StoreRejection(store.CommitAsync([StoreBroken], StoreRecording().Fence, context));
                await Task.Delay(50);
                Equal(1, Seen().Count, "a refused commit is not announced");
            }),
            Case("store.store.on-committed-removes-a-listener-that-throws-and-hands-its-error-to-on-error-the-commit-stands", async () =>
            {
                await using var store = await MemoryStore();
                var errors = new List<Exception>();
                var calls = 0;
                using var listening = store.OnCommitted(_ =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("listener");
                }, error => { lock (errors) errors.Add(error); });
                var fence = StoreRecording().Fence;
                await store.CommitAsync([StoreCreate()], fence, context);
                await Sync160Until(() => Volatile.Read(ref calls) == 1, "the listener was called");
                await store.CommitAsync([StoreCreate("b")], fence, context);
                await Task.Delay(50);
                Equal(1, Volatile.Read(ref calls), "called once: removed after its throw");
                Equal(1, errors.Count, "its error went to onError");
                Equal(2L, store.LastSeq, "both commits stand");
            }),
            Case("store.store.group-commit-coalesces-queued-commits-into-one-transaction-and-one-meta-update", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                var transactions = 0;
                db.AfterTransaction = () => { Interlocked.Increment(ref transactions); return Task.CompletedTask; };
                await using var store = await StoreOver(db, outbox: false);
                var docId = await StoreCreated(store);
                Interlocked.Exchange(ref transactions, 0);

                var held = HoldNextCommit(store);
                var events = new List<string>();
                CommitFence Fence(string name) => new(() => true, (seq, _) => { lock (events) events.Add($"{name}:{seq}"); });
                var telemetry = await ChordSupport.Metered();

                var running = store.CommitAsync([StoreCreate("blocker")], Fence("blocker"), context);
                await held.Entered;
                // Three commits queued behind the running one coalesce into a single SQLite transaction.
                var c1 = store.CommitAsync([StoreDelta(docId, 1, 1)], Fence("d1"), telemetry.Context);
                var c2 = store.CommitAsync([StoreDelta(docId, 2, 2)], Fence("d2"), telemetry.Context);
                var c3 = store.CommitAsync([StoreDelta(docId, 3, 3)], Fence("d3"), telemetry.Context);
                held.Release();

                Equal(2L, await running, "blocker seq");
                Sequence([3L, 4L, 5L], await Task.WhenAll(c1, c2, c3), "group seqs");
                Equal(2, Volatile.Read(ref transactions), "two transactions total (blocker + one coalesced group)");
                Sequence(["blocker:2", "d1:3", "d2:4", "d3:5"], events, "adoptions in exact seq order");
                // Without outbox: each delta is 2 statements (UPDATE chord_documents + INSERT chord_document_revisions), plus 1 shared UPDATE chord_meta.
                Json("""{ "chord.store.commits": 3, "chord.store.statements": 7 }""", StoreCounts(telemetry.Counts()), "one shared chord_meta update");
                Equal(5L, store.LastSeq, "last seq");
            }),
            Case("store.store.group-commit-isolates-a-refused-commit-and-commits-its-siblings", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                await using var store = await StoreOver(db, outbox: true);
                var held = HoldNextCommit(store);
                var events = new List<string>();
                CommitFence Fence(string name) => new(() => true, (seq, ids) => { lock (events) events.Add($"{name}:{seq}:{string.Join(",", ids)}"); });

                var running = store.CommitAsync([StoreCreate("a")], Fence("a"), context);
                await held.Entered;
                var first = store.CommitAsync([StoreCreate("b")], Fence("b"), context);
                var broken = StoreRejection(store.CommitAsync([StoreCreate("doomed"), StoreBroken], Fence("broken"), context));
                var second = store.CommitAsync([StoreCreate("c")], Fence("c"), context);
                held.Release();

                Equal(1L, await running, "blocker seq");
                var error = await broken;
                Check(StoreText(error).Contains("no such table", StringComparison.Ordinal), $"refused commit's own error: {StoreText(error)}");
                Check(error is Exception and not ChordError, "not a ChordError");
                Sequence([2L, 3L], await Task.WhenAll(first, second), "siblings commit cleanly");
                Equal(3L, store.LastSeq, "last seq after isolated refusal");
                Equal(3, events.Count, "all three valid commits adopted");
                Check(!events.Any(e => e.StartsWith("broken:", StringComparison.Ordinal)), "refused commit never adopted");

                var storedKeys = await store.ReadAsync(async exec =>
                    (await exec.AllAsync("SELECT key FROM chord_documents ORDER BY id")).Select(r => (string)r["key"]!).ToList(), context);
                Sequence(["a", "b", "c"], storedKeys, "doomed document rolled back and siblings persisted once");
                var outboxRows = await store.ReadAsync(async exec =>
                    (await exec.AllAsync("SELECT seq, writes FROM chord_outbox ORDER BY seq")).ToList(), context);
                Equal(3, outboxRows.Count, "three committed outbox entries");
                Check(!outboxRows.Any(r => ((string)r["writes"]!).Contains("\"doomed\"", StringComparison.Ordinal)), "doomed write absent from outbox");
            }),
            Case("store.store.group-commit-isolates-refused-admission-and-preserves-empty-commit-seq", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                var transactions = 0;
                db.AfterTransaction = () => { Interlocked.Increment(ref transactions); return Task.CompletedTask; };
                await using var store = await StoreOver(db);
                Interlocked.Exchange(ref transactions, 0);

                var held = HoldNextCommit(store);
                var events = new List<string>();
                CommitFence Fence(string name, bool admit = true) => new(() => admit, (seq, _) => { lock (events) events.Add($"{name}:{seq}"); });

                var running = store.CommitAsync([StoreCreate("a")], Fence("a"), context);
                await held.Entered;
                var c1 = store.CommitAsync([StoreCreate("b")], Fence("b"), context);
                var denied = StoreRejection(store.CommitAsync([StoreCreate("denied")], Fence("denied", admit: false), context));
                var empty = store.CommitAsync([], Fence("empty"), context);
                var c2 = store.CommitAsync([StoreCreate("c")], Fence("c"), context);
                held.Release();

                Equal(1L, await running, "blocker seq");
                Rejected(await denied, "released", "discarded", "denied commit");
                Sequence([2L, 2L, 3L], await Task.WhenAll(c1, empty, c2), "empty takes preceding seq, non-empty increments");
                Equal(2, Volatile.Read(ref transactions), "all group members in one transaction");
                Sequence(["a:1", "b:2", "empty:2", "c:3"], events, "adoption order");
            }),
            Case("store.store.group-commit-respects-series-barrier-fifo-order", async () =>
            {
                await using var store = await MemoryStore();
                var held = HoldNextCommit(store);
                var events = new List<string>();
                CommitFence Fence(string name) => new(() => true, (seq, _) => { lock (events) events.Add($"{name}:{seq}"); });

                var running = store.CommitAsync([StoreCreate("a")], Fence("a"), context);
                await held.Entered;
                var before = store.CommitAsync([StoreCreate("before")], Fence("before"), context);
                var series = store.CommitSeriesAsync([StoreUnchecked(StoreCreate("s1")), StoreUnchecked(StoreCreate("s2"))],
                    (index, seq, _) => { lock (events) events.Add($"series{index}:{seq}"); }, context);
                var after = store.CommitAsync([StoreCreate("after")], Fence("after"), context);
                held.Release();

                await Task.WhenAll(running, before, series, after);
                Sequence(["a:1", "before:2", "series0:3", "series1:4", "after:5"], events, "exact FIFO barrier ordering");
            }),
            Case("store.store.settled-closes-open-group-so-later-commits-do-not-piggyback", async () =>
            {
                var db = new TestSqliteDatabase(":memory:");
                var transactions = 0;
                db.AfterTransaction = () => { Interlocked.Increment(ref transactions); return Task.CompletedTask; };
                await using var store = await StoreOver(db, outbox: false);
                Interlocked.Exchange(ref transactions, 0);

                var held = HoldNextCommit(store);
                var fence = StoreRecording().Fence;
                var running = store.CommitAsync([StoreCreate("a")], fence, context);
                await held.Entered;
                var beforeSettled = store.CommitAsync([StoreCreate("before")], fence, context);
                var settled = store.SettledAsync();
                var afterSettled = store.CommitAsync([StoreCreate("after")], fence, context);
                held.Release();

                await settled;
                Check(beforeSettled.IsCompleted, "commits queued before SettledAsync settled before SettledAsync returned");
                await Task.WhenAll(running, beforeSettled, afterSettled);
                Equal(3, Volatile.Read(ref transactions), "SettledAsync closed the open group so afterSettled ran in its own transaction");
            }),
            Case("store.store.group-commit-recomputes-seq-overflow-after-refusal", async () =>
            {
                await using var store = await StoreAtSeq(StoreInternal.MaxSafeIntegerLong - 2);
                var held = HoldNextCommit(store);
                var fence = StoreRecording().Fence;

                // Blocker takes MaxSafeIntegerLong - 1 so broken and valid coalesce with only one seq slot left.
                var blocker = store.CommitAsync([StoreCreate("blocker")], fence, context);
                await held.Entered;
                var broken = StoreRejection(store.CommitAsync([StoreBroken], fence, context));
                var valid = store.CommitAsync([StoreCreate("fits-after-refusal")], fence, context);
                held.Release();

                Equal(StoreInternal.MaxSafeIntegerLong - 1, await blocker, "blocker takes MaxSafeIntegerLong - 1");
                Check(StoreText(await broken).Contains("no such table", StringComparison.Ordinal), "broken commit refused");
                Equal(StoreInternal.MaxSafeIntegerLong, await valid, "valid sibling slides into the freed final seq slot");
                Equal(StoreInternal.MaxSafeIntegerLong, store.LastSeq, "store stays healthy at MAX_SAFE_INTEGER");
            }),
        ];
    }

    private static IEnumerable<(string, Func<Task>)> StoreSync160QueryCases()
    {
        var context = ChordContext.Background;
        var set = Sync160PlanSessionColumns;
        var sessions = Sync160PlanSessions;
        var teamSessions = Sync160PlanTeamSessions;
        return
        [
            Case("store.expression.160-a-compiled-condition-is-data-its-json-reads-back-to-the-same-node", () =>
            {
                var compiled = Tables.Compile(set, And(Eq(set.Refs["app"], "club"), Not(IsNull(set.Refs["archivedAt"])),
                    Exists(teamSessions, Sync160Match(("session", set.Refs["id"])), t => InArray(t["team"], [(JsonNode?)"u12", (JsonNode?)"u14"]))));
                Json("""{"and":[{"eq":["app","club"]},{"not":{"isNull":"archivedAt"}},{"exists":["club.team-sessions",[["session","id"]],{"inArray":["team",["u12","u14"]]}]}]}""",
                    compiled.ToJson(), "the data form");
                Equal(compiled.ToString(), StoreInternal.CheckNode(JsonNode.Parse(compiled.ToString()), set, "whereNode").ToString(), "read back");
                // A reference of another set, and a table's own reference, are no columns of the set.
                StoreArgs(() => Tables.Compile(set, Eq(teamSessions.Refs["team"], "u12")), "no column \"team\"");
                StoreArgs(() => Tables.Compile(set, Eq(sessions.Refs["app"], "club")), "no column \"app\"");
            }),
            Case("store.query.160-a-tables-condition-takes-its-own-references-and-those-of-the-set-it-exposes", () =>
            {
                var both = Tables.Query(sessions).Where(s => And(Eq(s["title"], "t"), Eq(set.Refs["app"], "club")));
                Json("""{"and":[{"eq":["title","t"]},{"eq":["app","club"]}]}""", both.Condition!.ToJson(), "own and exposed references");
                StoreArgs(() => Tables.Query(sessions).Where(_ => Eq(teamSessions.Refs["team"], "u12")), "no column \"team\"");
                // A joined set's columns are named <set>:<column> in the condition and the order.
                var joined = Tables.Query(Sync160PlanTeams).Join(set, t => Sync160Match(("id", t["session"])))
                    .Where((_, session) => Eq(session["app"], "club")).OrderBy((_, session) => session["lastTouch"]);
                Json("""{"eq":["sessions.columns:app","club"]}""", joined.Condition!.ToJson(), "a partner column in the condition");
                Sequence(["sessions.columns:lastTouch", "team", "session"], joined.Order.Columns, "a partner column in the order");
                Check(ReferenceEquals(set, joined.JoinedSet), "the joined set");
            }),
            Case("store.evaluate.160-exists-is-not-evaluated-in-memory", () =>
            {
                var node = new WhereNode.Exists("club.team-sessions", [new ExistsPair("session", "id")], null);
                var error = Throws<InvalidOperationException>(() => StoreInternal.Matches(node, StoreRow("""{"id":"s0"}""")), "exists in memory");
                Sync160Contains("exists is not evaluated in memory", error.Message, "why");
                var written = Throws<InvalidOperationException>(() => StoreInternal.WhereSql(node, []), "exists in a write's SQL");
                Sync160Contains("exists is rendered by a read", written.Message, "why");
            }),
            Case("store.page.160-a-page-of-a-joined-query-continues-after-its-cursor-of-partner-and-own-columns", async () =>
            {
                var (store, tables) = await Sync160PlanSeeded();
                await using (store)
                {
                    var q = Tables.Query(Sync160PlanTeams).Join(set, t => Sync160Match(("id", t["session"])))
                        .OrderBy((_, session) => session["lastTouch"], OrderDirection.Desc);
                    var plan = new ReadPlan(q, tables[Sync160PlanTeams], Projection.All, ReadShape.Rows, store.ReadIndex);
                    var first = await StoreInternal.ReadPageAsync(store, plan, 2, null, context);
                    Sequence(["[\"u14\",\"s1\"]", "[\"u12\",\"s1\"]"], Sync160Column(first.Stored, "team", "session"), "the first page");
                    Check(first.Next is not null, "more follow");
                    var second = await StoreInternal.ReadPageAsync(store, plan, 2, first.Next, context);
                    Sequence(["[\"u12\",\"s0\"]"], Sync160Column(second.Stored, "team", "session"), "the page after the cursor");
                    Check(second.Next is null, "the last page");
                    Rejected(await StoreRejection(StoreInternal.ReadPageAsync(store, plan, 0, null, context)), null, "args", "limit 0", "limit is a positive integer");
                    // A cursor of another order is refused.
                    var other = new ReadPlan(Tables.Query(Sync160PlanTeams), tables[Sync160PlanTeams], Projection.All, ReadShape.Rows, store.ReadIndex);
                    Rejected(await StoreRejection(StoreInternal.ReadPageAsync(store, other, 2, first.Next, context)), null, "args", "a cursor of another order");
                }
            }),
        ];
    }
}
