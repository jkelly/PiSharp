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
