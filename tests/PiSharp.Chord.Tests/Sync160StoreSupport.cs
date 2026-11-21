using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

// PiSharp-only (no upstream Pi source): the helpers of the chord-store package's 1.6.0 cases (Store/Sync160*Cases.cs, the rewritten
// Store/LiveQueryCases.cs, the host data cases). They stand in for what the upstream tests write inline or take from test/support.ts:
// column sets and tables built from object literals, a Map as the bindings of a read plan, a batch nobody schedules, the commit a
// transaction stages (its table writes and the refresh of the store's reads), a paused commit, and the mulberry32 generator of
// test/store/skip.test.ts.
internal static partial class Program
{
    /// <summary>A column set from its columns, key and indexes (upstream <c>defineColumns(name, { columns, key, indexes })</c>).</summary>
    internal static ColumnSet Sync160Columns(string name, (string Name, Property Schema)[] columns, string[] key, params (string Name, string[] Columns)[] indexes) =>
        Tables.DefineColumns(name, new ColumnSetOptions { Columns = Sync160Schemas(columns), Key = key, Indexes = StoreIndexes(indexes) });

    /// <summary>Columns in the order given.</summary>
    internal static OrderedDictionary<string, Property> Sync160Schemas(params (string Name, Property Schema)[] columns)
    {
        var map = new OrderedDictionary<string, Property>(StringComparer.Ordinal);
        foreach (var (name, schema) in columns) map[name] = schema;
        return map;
    }

    /// <summary><c>{ ...Set.columns, more }</c>: a set's columns and a table's own.</summary>
    internal static OrderedDictionary<string, Property> Sync160Spread(ColumnSet set, params (string Name, Property Schema)[] more)
    {
        var map = new OrderedDictionary<string, Property>(set.Columns, StringComparer.Ordinal);
        foreach (var (name, schema) in more) map[name] = schema;
        return map;
    }

    /// <summary>A table of <paramref name="columns"/> (upstream <c>defineTable(name, { version: 1, columns, key, indexes, exposes })</c>).</summary>
    internal static TableDefinition Sync160Table(string name, OrderedDictionary<string, Property> columns, string[] key, ColumnSet? exposes = null,
        params (string Name, string[] Columns)[] indexes) =>
        Tables.DefineTable(name, new TableOptions { Version = 1, Columns = columns, Key = key, Indexes = StoreIndexes(indexes), Exposes = exposes });

    /// <summary>A match <c>{ setColumn: column }</c>.</summary>
    internal static IReadOnlyDictionary<string, ColumnRef> Sync160Match(params (string SetColumn, ColumnRef Column)[] pairs) => Expressions.Match(pairs);

    /// <summary>A batch of a test without a host: whoever commits runs its adoptions, on its own thread.</summary>
    internal static Batch Sync160Batch() => new(_ => { }, null);

    /// <summary>Stages <paramref name="write"/>'s table writes as one commit with the refresh a transaction stages, and completes once
    /// adopted in <paramref name="batch"/> (upstream's <c>put</c> and <c>commit</c> helpers of reads.test.ts and skip.test.ts).</summary>
    internal static Task<long> Sync160Commit(ChordStore store, Batch batch, ChordContext context, Action<Func<TableHandle, TableChanges>> write,
        params IStagedWrite[] after)
    {
        var staged = new List<IStagedWrite>();
        var byTable = new OrderedDictionary<TableHandle, TableChanges>(ReferenceEqualityComparer.Instance);
        TableChanges Changes(TableHandle handle)
        {
            if (!byTable.TryGetValue(handle, out var found)) byTable[handle] = found = new TableChanges(handle, staged);
            return found;
        }
        write(Changes);
        List<TableChanges> written = [.. byTable.Values];
        var refresh = new ReadRefresh(store.ReadIndex, [.. written.Select(each => each.Handle.Sql.Table)], () => [.. written.SelectMany(each => each.Changes())], context);
        staged.Add(refresh.Write);
        staged.AddRange(after);
        return store.CommitAsync(staged, new CommitFence(() => true, (_, _) => batch.Commit(refresh.Adopt, context)), context);
    }

    /// <summary>A staged write that waits: <c>Ran</c> completes when the step reaches it, <c>Release</c> lets the step go on.</summary>
    internal static (IStagedWrite Write, Task Ran, Action Release) Sync160Pause()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new StagedWrite(WriteOrder.Write, async _ =>
        {
            ran.TrySetResult();
            await release.Task;
            return [];
        });
        return (write, ran.Task, () => release.TrySetResult());
    }

    /// <summary>The query plan SQLite chose for a statement, one step per entry joined with "; " (upstream <c>explained</c>).</summary>
    internal static async Task<string> Sync160Explained(ChordStore store, Statement statement)
    {
        var steps = await store.ReadAsync(executor => executor.AllAsync("EXPLAIN QUERY PLAN " + statement.Sql, [.. statement.Params]), ChordContext.Background);
        return string.Join("; ", steps.Select(step => (string)step["detail"]!));
    }

    /// <summary>Waits until <paramref name="done"/> holds (a run that follows a binding is not awaited by anyone): upstream's
    /// <c>await new Promise(resolve =&gt; setTimeout(resolve, 0))</c> after a bind or a ready.</summary>
    internal static async Task Sync160Until(Func<bool> done, string what)
    {
        for (var round = 0; round < 500 && !done(); round++) await Task.Delay(10);
        Check(done(), what);
    }

    /// <summary>A stored value as JSON text, numbers as JavaScript writes them.</summary>
    internal static string Sync160Stored(IReadOnlyDictionary<string, object?> row, params string[] columns) =>
        StoreInternal.SqliteJson(columns.Select(column => row.GetValueOrDefault(column)));
}

/// <summary>Bindings of a map (upstream's tests pass a <c>Map</c> where a plan takes <c>Bindings</c>).</summary>
internal sealed class Sync160Bindings(params (string Set, string Table, TableDefinition Definition)[] bound) : IBindings
{
    private readonly Dictionary<string, Bound> sets = bound.ToDictionary(each => each.Set, each => new Bound(each.Table, each.Definition), StringComparer.Ordinal);

    public Bound? Get(string set) => sets.GetValueOrDefault(set);
}

/// <summary>The small deterministic generator of skip.test.ts (mulberry32): a failing seed reproduces. JavaScript's <c>Math.imul</c> and
/// <c>&gt;&gt;&gt;</c> are 32-bit; so is every step here.</summary>
internal sealed class Sync160Random(uint seed)
{
    private uint state = seed;

    /// <summary>The next number in [0, 1).</summary>
    internal double Next()
    {
        unchecked
        {
            state += 0x6d2b79f5;
            var t = state;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    internal T Pick<T>(IReadOnlyList<T> items) => items[(int)Math.Floor(Next() * items.Count)];

    internal int Int(int below) => (int)Math.Floor(Next() * below);

    internal bool Chance(double p) => Next() < p;
}
