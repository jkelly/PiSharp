// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/live-query.ts.
// Authored native regression; not an upstream execution capture.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static readonly TableDefinition LiveQueryTable = Tables.DefineTable("live-query", new TableOptions
    {
        Version = 1,
        Columns = StoreColumns(("id", Int()), ("value", Tables.Json(Obj(("n", Int())), new JsonColumnOptions { Version = 1 }))),
        Key = ["id"],
    });

    private static JsonObject LiveQueryRow(int id, int? n = null) => new() { ["id"] = id, ["value"] = new JsonObject { ["n"] = n ?? id } };

    private static async Task<TableHandle> LiveQueryHandle(ChordStore store, LiveQueryTestOwner owner)
    {
        const string sqlName = "app.store:live-query";
        await StoreInternal.PrepareTablesAsync([new DeclaredTable(LiveQueryTable, sqlName)], store, CommitFence.Open, owner.Label, ChordContext.Background);
        return new TableHandle(LiveQueryTable, sqlName, store, owner);
    }

    private static LiveQuery DeclareLiveQuery(TableHandle handle, LiveQueryTestOwner owner, StateHost state, long limit = 100)
    {
        var query = new LiveQuery(handle, Tables.Query(handle.Definition), limit, owner, state.Batch);
        owner.Queries.Add(query);
        return query;
    }

    private static async Task LiveQueryCommit(ChordStore store, TableHandle handle, StateHost state, Action<TableChanges>? fill = null,
        ChordContext? context = null)
    {
        var ctx = context ?? ChordContext.Background;
        List<IStagedWrite> writes = [];
        fill?.Invoke(new TableChanges(handle, writes));
        var refresh = StoreInternal.RefreshOf([handle], ctx)!;
        writes.Add(refresh.Write);
        await store.CommitAsync(writes, new CommitFence(() => true, (_, _) => state.Batch.Within(refresh.Adopt, ctx)), ctx);
        state.Delivered();
    }

    private static IEnumerable<(string, Func<Task>)> StoreLiveQueryCases() =>
    [
        Case("store.live-query.declaration-requires-the-same-table-and-a-bounded-positive-limit", () =>
        {
            var other = Tables.DefineTable("other-live-query", new TableOptions { Version = 1, Columns = StoreColumns(("id", Int())), Key = ["id"] });
            StoreArgs(() => StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(other), 1, "app.store"), "a query of table");
            StoreArgs(() => StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(LiveQueryTable), 0, "app.store"), "a positive integer");
            StoreArgs(() => StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(LiveQueryTable), long.MaxValue, "app.store"), "a positive integer");
            StoreInternal.CheckLiveQuery(LiveQueryTable, Tables.Query(LiveQueryTable), 1, "app.store");
            return Task.CompletedTask;
        }),
        Case("store.live-query.snapshots-are-bounded-commit-coherent-and-preserve-old-json-trees", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => { for (var id = 1; id <= 4; id++) changes.Put(LiveQueryRow(id)); });
            var state = new StateHost();
            var query = DeclareLiveQuery(handle, owner, state, 3);
            await query.LoadAsync(ChordContext.Background);
            var initial = query.Value!;
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}]""", initial, "initial bounded rows");
            var frames = ChordSupport.ChangesOf(await query.WatchAsync(ChordContext.Background));
            await LiveQueryCommit(store, handle, state, changes => changes.Delete(new JsonObject { ["id"] = 1 }));
            Equal(1, frames.Count, "one frame for the commit");
            var frame = (WatchValueCall<JsonNode?>)frames[0];
            Json("""[{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}},{"id":4,"value":{"n":4}}]""", frame.Value, "new window");
            Check(frame.Ops.Length == 2 && frame.Ops.All(op => op is SpliceOp), "window shift is one removal and one insertion");
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}},{"id":3,"value":{"n":3}}]""", initial, "the prior snapshot is still intact");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(2)));
            Equal(1, frames.Count, "an unchanged physical row publishes no frame");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(2, 20)));
            var changed = (WatchValueCall<JsonNode?>)frames[^1];
            Json("""[["s",[0,"value","n"],20]]""", ChordSupport.OpsJson(changed.Ops), "a changed row retains a narrow delta");
            Equal(0, owner.Failures.Count, "query decoding does not fail the table owner");
        }),
        Case("store.live-query.refresh-is-selective-deduplicated-and-caches-decoded-row-identity", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var state = new StateHost();
            var query = DeclareLiveQuery(handle, owner, state);
            var (_, context, counts) = await ChordSupport.Metered();
            await query.LoadAsync(context);
            var step = new Step(new LiveQueryExecutor(await store.ReadAsync(executor => executor.AllAsync("SELECT * FROM \"app.store:live-query\""), context)), 1,
                new IdAllocator(2), new Dictionary<string, TableRecord>());
            var first = await query.RerunAsync(step, context);
            state.Batch.Within(() => query.Adopt(first), context);
            var second = await query.RerunAsync(step, context);
            Check(ReferenceEquals(first.Rows!.Items[0], second.Rows!.Items[0]), "the cached decoded row is reused");
            var unqueried = new TableHandle(LiveQueryTable, "app.store:another", store, owner);
            Check(StoreInternal.RefreshOf([unqueried], context) is null, "a write of an unqueried table does not refresh");
            var refresh = StoreInternal.RefreshOf([handle, handle], context)!;
            Equal(0, (await refresh.Write.ApplyAsync(step)).Count, "the refresh records no store writes");
            Equal(4d, counts()["chord.query.runs"], "one initial load, two direct runs, one deduplicated refresh");
        }),
        Case("store.live-query.host-kind-lookup-and-detach-remove-only-the-matching-query", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            var state = new StateHost();
            var plain = DeclareLiveQuery(handle, owner, state);
            await plain.LoadAsync(ChordContext.Background);
            var kind = new HostKind("query-host");
            var hosted = Tables.DefineTable("host-query", new TableOptions
            {
                Version = 1,
                Columns = StoreColumns(("host", Tables.Host(kind)), ("id", Int())),
                Key = ["host", "id"],
            });
            var hostedHandle = new TableHandle(hosted, "app.store:host-query", store, owner);
            var query = DeclareLiveQuery(hostedHandle, owner, state);
            Sequence([query], owner.Queries.OnHostKind("query-host"), "lookup includes only the host kind's table");
            Equal(0, owner.Queries.OnHostKind("other-host").Count, "another host kind has none");
            var watch = await plain.WatchAsync(ChordContext.Background);
            var calls = ChordSupport.ChangesOf(watch);
            var late = await plain.RerunAsync(new Step(new LiveQueryExecutor([]), 1, new IdAllocator(2), new Dictionary<string, TableRecord>()), ChordContext.Background);
            plain.Detach(new InvalidOperationException("ended"));
            state.Batch.Within(() => plain.Adopt(late), ChordContext.Background);
            state.Delivered();
            Equal(WatchEnd.Ended, await watch.Closed, "detach ends an existing watch");
            Equal(0, calls.Count, "a pending refresh cannot publish after detach");
            Check(owner.Queries.On(handle.Sql.Table) is null, "the detached table's last query is removed");
            Equal(1, owner.Queries.OnHostKind("query-host").Count, "another table's query remains registered");
        }),
        Case("store.live-query.failed-refresh-keeps-last-rows-reports-once-and-recovers-with-replacement", async () =>
        {
            var db = new TestSqliteDatabase(":memory:");
            await using var store = await StoreOver(db);
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var state = new StateHost();
            var query = DeclareLiveQuery(handle, owner, state);
            await query.LoadAsync(ChordContext.Background);
            var calls = ChordSupport.ChangesOf(await query.WatchAsync(ChordContext.Background));
            await db.RunAsync("UPDATE \"app.store:live-query\" SET \"value\" = ?", "not json");
            await LiveQueryCommit(store, handle, state);
            await LiveQueryCommit(store, handle, state);
            Equal(1, owner.Reports.Count, "one report for a continuous failure");
            Equal(1, calls.Count, "one failed call for a continuous failure");
            Check(calls[0] is WatchFailedCall<JsonNode?>, "the query watch failed");
            StoreThrows(() => _ = query.Value, owner.Reports[0].Message, "reads throw the query failure");
            var late = await query.WatchAsync(ChordContext.Background);
            Json("""[{"id":1,"value":{"n":1}}]""", late.Value, "a watch opened during failure has the last good rows");
            var lateCalls = ChordSupport.ChangesOf(late);
            state.Delivered();
            Check(lateCalls.Single() is WatchFailedCall<JsonNode?>, "a late watch first gets the failure");
            await LiveQueryCommit(store, handle, state, changes => changes.Put(LiveQueryRow(1, 10)));
            Check(((WatchValueCall<JsonNode?>)calls[^1]).Ops is [ReplaceOp], "recovery replaces the failed watch value");
            Json("""[{"id":1,"value":{"n":10}}]""", query.Value, "recovered rows");
            await db.RunAsync("UPDATE \"app.store:live-query\" SET \"value\" = ?", "not json again");
            await LiveQueryCommit(store, handle, state);
            Equal(2, owner.Reports.Count, "a successful refresh resets the failure episode");
            Equal(0, owner.Failures.Count, "a query failure remains isolated from its table owner");
        }),
        Case("store.live-query.refused-step-adopts-nothing-and-reapplication-uses-only-the-last-run", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var state = new StateHost();
            var query = DeclareLiveQuery(handle, owner, state);
            await query.LoadAsync(ChordContext.Background);
            var refresh = new QueryRefresh([query], ChordContext.Background);
            List<IStagedWrite> writes = [];
            new TableChanges(handle, writes).Put(LiveQueryRow(2));
            writes.Add(refresh.Write);
            writes.Add(new StagedWrite(WriteOrder.Write, _ => throw new InvalidOperationException("refuse after query read")));
            await StoreRejection(store.CommitAsync(writes, new CommitFence(() => true, (_, _) => state.Batch.Within(refresh.Adopt, ChordContext.Background)), ChordContext.Background));
            Json("""[{"id":1,"value":{"n":1}}]""", query.Value, "the refused run was never adopted");
            Equal(1L, await handle.CountAsync(Tables.Query(LiveQueryTable), ChordContext.Background), "the write rolled back");
            await store.CommitAsync([refresh.Write], new CommitFence(() => true, (_, _) => state.Batch.Within(refresh.Adopt, ChordContext.Background)), ChordContext.Background);
            Json("""[{"id":1,"value":{"n":1}}]""", query.Value, "a replay adopts the second application only");
        }),
        Case("store.live-query.activation-failure-is-fatal-and-slow-load-cannot-replace-a-newer-adoption-or-detach", async () =>
        {
            await using var store = await MemoryStore();
            var owner = new LiveQueryTestOwner();
            var handle = await LiveQueryHandle(store, owner);
            await StoreCommitWith(store, handle, changes => changes.Put(LiveQueryRow(1)));
            var controlled = new LiveQueryControlledStore(store);
            var wrapped = new TableHandle(handle.Definition, handle.Sql.Table, controlled, owner);
            var state = new StateHost();
            var query = DeclareLiveQuery(wrapped, owner, state);
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controlled.AfterRead = async () => { captured.TrySetResult(); await release.Task; };
            var loading = query.LoadAsync(ChordContext.Background);
            await captured.Task;
            await LiveQueryCommit(store, wrapped, state, changes => changes.Put(LiveQueryRow(2)));
            release.TrySetResult();
            await loading;
            Json("""[{"id":1,"value":{"n":1}},{"id":2,"value":{"n":2}}]""", query.Value, "the newer commit wins over the slow activation snapshot");
            controlled.AfterRead = null;
            controlled.Failure = new InvalidOperationException("activation read failed");
            var failed = DeclareLiveQuery(wrapped, owner, state);
            Check(ReferenceEquals(controlled.Failure, await StoreRejection(failed.LoadAsync(ChordContext.Background))), "the initial failure rejects activation");
            Equal(0, owner.Reports.Count, "activation failures are not refresh reports");
            controlled.Failure = null;
            var detached = DeclareLiveQuery(wrapped, owner, state);
            captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controlled.AfterRead = async () => { captured.TrySetResult(); await release.Task; };
            var detachingLoad = detached.LoadAsync(ChordContext.Background);
            await captured.Task;
            var ended = new InvalidOperationException("attempt ended");
            detached.Detach(ended);
            release.TrySetResult();
            await detachingLoad;
            Check(ReferenceEquals(ended, StoreThrows(() => _ = detached.Value, "attempt ended")), "a late activation cannot revive a detached query");
            Check(!owner.Queries.On(handle.Sql.Table)!.Contains(detached), "detach removes the query from refresh lookup");
        }),
    ];
}

internal sealed class LiveQueryTestOwner : ITableOwner, IQueryOwner
{
    public string Label => "app.store";
    public object Scope { get; } = new();
    public LiveQueries Queries { get; } = new();
    internal List<Exception> Reports { get; } = [];
    internal List<ChordError> Failures { get; } = [];
    public void AssertLive() { }
    public void Fail(ChordError error) => Failures.Add(error);
    public void Report(Exception error) => Reports.Add(error);
}

/// <summary>Holds the result of a completed read, so a newer commit can overtake activation without blocking SQLite.</summary>
internal sealed class LiveQueryControlledStore(IChordStore store) : IChordStore
{
    internal Func<Task>? AfterRead { get; set; }
    internal Exception? Failure { get; set; }
    public TableRecord? Table(string name) => store.Table(name);
    public long? TableVersion(string name) => store.TableVersion(name);
    public Action AnnounceTable(string name, long version) => store.AnnounceTable(name, version);
    public Task<long> CommitAsync(IReadOnlyList<IStagedWrite> writes, CommitFence fence, ChordContext context) => store.CommitAsync(writes, fence, context);
    public async Task<T> ReadAsync<T>(Func<ISqliteExecutor, Task<T>> read, ChordContext context)
    {
        if (Failure is { } failure) throw failure;
        var result = await store.ReadAsync(read, context);
        if (AfterRead is { } after) await after();
        return result;
    }
}

/// <summary>A fixed physical snapshot for checking codec reuse without another write or database transaction.</summary>
internal sealed class LiveQueryExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : ISqliteExecutor
{
    public Task ExecAsync(string sql) => throw new NotSupportedException();
    public Task RunAsync(string sql, params object?[] parameters) => throw new NotSupportedException();
    public Task<long> RunCountedAsync(string sql, params object?[] parameters) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => throw new NotSupportedException();
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) => Task.FromResult(rows);
}
