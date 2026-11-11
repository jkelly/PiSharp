// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/live-query.ts
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Store;

/// <summary>The owner of a live query; a failed refresh reports once until a successful refresh.</summary>
internal interface IQueryOwner
{
    string Label { get; }
    void Report(Exception error);
}

/// <summary>An object's live queries, retained across its overlapping attempts and indexed by SQL table.</summary>
internal sealed class LiveQueries
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, HashSet<LiveQuery>> byTable = new(StringComparer.Ordinal);

    internal void Add(LiveQuery query)
    {
        lock (gate)
        {
            if (!byTable.TryGetValue(query.Handle.Sql.Table, out var queries))
                byTable.Add(query.Handle.Sql.Table, queries = []);
            queries.Add(query);
        }
    }

    internal void Delete(LiveQuery query)
    {
        lock (gate)
        {
            if (!byTable.TryGetValue(query.Handle.Sql.Table, out var queries)) return;
            queries.Remove(query);
            if (queries.Count == 0) byTable.Remove(query.Handle.Sql.Table);
        }
    }

    internal IReadOnlyCollection<LiveQuery>? On(string table)
    {
        lock (gate) return byTable.TryGetValue(table, out var queries) ? queries.ToArray() : null;
    }

    internal IReadOnlyList<LiveQuery> OnHostKind(string kind)
    {
        lock (gate)
            return [.. byTable.Values.SelectMany(queries => queries).Where(query => query.Handle.Definition.HostColumn?.Host?.Kind.Kind == kind)];
    }
}

internal static partial class StoreInternal
{
    internal static void CheckLiveQuery(TableDefinition table, Query query, long limit, string label)
    {
        if (!ReferenceEquals(query?.Table, table))
            throw DataInternal.Invalid("args", $"{label}: self.query: a query of table \"{query?.Table.Name}\" over \"{table.Name}\"");
        if (limit < 1 || limit > MaxSafeInteger)
            throw DataInternal.Invalid("args", $"{label}: self.query: a limit is required, a positive integer");
    }

    /// <summary>Only tables actually written by a transaction invalidate queries.</summary>
    internal static QueryRefresh? RefreshOf(IEnumerable<TableHandle> handles, ChordContext context)
    {
        HashSet<LiveQuery>? queries = null;
        foreach (var handle in handles)
        {
            if (handle.Owner.Queries.On(handle.Sql.Table) is not { } on) continue;
            queries ??= [];
            queries.UnionWith(on);
        }
        return queries is null || queries.Count == 0 ? null : new QueryRefresh(queries, context);
    }
}

/// <summary>Reads after a commit's writes and adopts only after that commit succeeds.</summary>
internal sealed class QueryRefresh
{
    private readonly LiveQuery[] queries;
    private readonly ChordContext context;
    private readonly List<LiveQuery.Run> runs = [];

    internal QueryRefresh(IEnumerable<LiveQuery> queries, ChordContext context)
    {
        this.queries = [.. queries.Distinct()];
        this.context = context;
        Write = new StagedWrite(WriteOrder.Write, RerunAsync);
    }

    internal IStagedWrite Write { get; }

    private async Task<IReadOnlyList<StoreWrite>> RerunAsync(Step step)
    {
        // A grouped commit can roll back and replay this write; never adopt the rolled-back runs.
        runs.Clear();
        foreach (var query in queries) runs.Add(await query.RerunAsync(step, context).ConfigureAwait(false));
        return Array.Empty<StoreWrite>();
    }

    internal void Adopt()
    {
        for (var index = 0; index < queries.Length; index++) queries[index].Adopt(runs[index]);
    }

    internal void Committed() { }
    internal void Discarded() { }
}

/// <summary>
/// A bounded query whose snapshots are adopted in the same batch as the writes they reflect. Decoded row maps are
/// cached by physical row identity. Public JsonArray snapshots own cloned cells because JsonNode permits only one
/// parent; identity-token arrays let the existing revision differ retain its linear row-identity matching without
/// sharing or mutating any published snapshot.
/// </summary>
internal sealed class LiveQuery : Source
{
    private readonly Lock stateGate = new();
    private readonly IQueryOwner owner;
    private readonly Statement statement;
    private QueryState state;
    private Rows? rows;
    private Exception? error;
    private Rows? framed;
    private bool hasFrame;
    private bool failing;
    private long adopted;
    private long nextToken;

    internal LiveQuery(TableHandle handle, Query query, long limit, IQueryOwner owner, Batch batch) : base(batch)
    {
        StoreInternal.CheckLiveQuery(handle.Definition, query, limit, owner.Label);
        Handle = handle;
        this.owner = owner;
        // Ordinary table reads omit the eager migration version. Query reuse compares every physical column,
        // including $v, so request the complete projection explicitly.
        statement = handle.Sql.Select(query.Condition, query.Order, null, limit, handle.Definition.Rows.Physical);
    }

    internal TableHandle Handle { get; }

    public override JsonNode? Value
    {
        get
        {
            lock (stateGate)
            {
                if (state == QueryState.Rows) return rows!.Value;
                if (state == QueryState.Loading)
                    throw new ChordError("not_ready", $"{owner.Label}: a live query read before it ran");
                ExceptionDispatchInfo.Throw(error!);
                return null;
            }
        }
    }

    public override WatchStart Start()
    {
        lock (stateGate)
            return state == QueryState.Failed
                ? WatchStart.Failure(error!, rows is not null, rows?.Value)
                : WatchStart.Of(Value);
    }

    /// <summary>The activation read fails activation, and cannot overwrite a refresh that overtook it.</summary>
    internal async Task LoadAsync(ChordContext context)
    {
        long generation;
        lock (stateGate) generation = adopted;
        context.Count("chord.query.runs");
        var stored = await Handle.Store.ReadAsync(executor => executor.AllAsync(statement.Sql, [.. statement.Params]), context).ConfigureAwait(false);
        var loaded = Decode(stored, context);
        lock (stateGate)
        {
            if (adopted != generation || state == QueryState.Detached) return;
            rows = loaded;
            state = QueryState.Rows;
        }
    }

    internal async Task<Run> RerunAsync(Step step, ChordContext context)
    {
        try
        {
            context.Count("chord.query.runs");
            return new Run(Decode(await step.AllAsync(statement.Sql, [.. statement.Params]).ConfigureAwait(false), context), null);
        }
        catch (Exception failed)
        {
            return new Run(null, failed);
        }
    }

    internal void Adopt(Run run)
    {
        Exception? reported = null;
        lock (stateGate)
        {
            if (state == QueryState.Detached) return;
            adopted++;
            if (Watched && !hasFrame)
            {
                hasFrame = true;
                framed = state == QueryState.Rows ? rows : null;
            }
            if (run.Rows is { } good)
            {
                failing = false;
                rows = good;
                error = null;
                state = QueryState.Rows;
            }
            else
            {
                if (!failing) reported = StateInternal.HandedOut(run.Error!);
                failing = true;
                error = run.Error;
                state = QueryState.Failed;
            }
        }
        if (reported is not null) owner.Report(reported);
        Changed();
    }

    public override void Deliver(ChordContext context)
    {
        lock (stateGate)
        {
            if (!hasFrame) return;
            var before = framed;
            framed = null;
            hasFrame = false;
            if (state == QueryState.Rows)
            {
                ImmutableArray<Op> ops = before is null ? [new ReplaceOp(rows!.Value)] : DiffRows(before, rows!);
                if (ops.Length > 0) Publish(rows!.Value, ops, false, context);
            }
            else if (state == QueryState.Failed && before is not null)
            {
                FailWatches(error!, context);
            }
        }
    }

    internal void Detach(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Handle.Owner.Queries.Delete(this);
        lock (stateGate)
        {
            state = QueryState.Detached;
            error = failure;
            rows = null;
            framed = null;
            hasFrame = false;
        }
        EndWatches();
    }

    private Rows Decode(IReadOnlyList<IReadOnlyDictionary<string, object?>> stored, ChordContext context)
    {
        Rows? previousRows;
        lock (stateGate) previousRows = rows;
        var byKey = new Dictionary<string, Decoded>(StringComparer.Ordinal);
        var items = new List<Decoded>(stored.Count);
        foreach (var each in stored)
        {
            var key = StoreInternal.SqliteJson(Handle.Definition.Key.Select(column => each[column]));
            Decoded decoded;
            if (previousRows is not null && previousRows.ByKey.TryGetValue(key, out var previous) && SameStored(previous.Stored, each, Handle.Definition.Rows.Physical))
                decoded = previous;
            else
            {
                // Read directly through the codec: one bad query must not fail the whole table owner.
                var values = Handle.Definition.Rows.Decode(each, context);
                decoded = new Decoded(each.ToDictionary(cell => cell.Key, cell => cell.Value, StringComparer.Ordinal), values, Interlocked.Increment(ref nextToken));
            }
            items.Add(decoded);
            byKey.Add(key, decoded);
        }
        var value = new JsonArray([.. items.Select(item => (JsonNode?)RowValue(item))]);
        return new Rows(value, items, byKey);
    }

    private static bool SameStored(IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after, IReadOnlyList<string> physical)
    {
        foreach (var column in physical)
            if (!Equals(before[column], after[column])) return false;
        return true;
    }

    private static JsonObject RowValue(Decoded row)
    {
        var value = new JsonObject();
        foreach (var (name, cell) in row.Row) value[name] = cell?.DeepClone();
        return value;
    }

    private static ImmutableArray<Op> DiffRows(Rows before, Rows after)
    {
        var oldIds = new JsonArray([.. before.Items.Select(item => (JsonNode?)JsonValue.Create(item.Token))]);
        var newIds = new JsonArray([.. after.Items.Select(item => (JsonNode?)JsonValue.Create(item.Token))]);
        var identities = DeltaDiff.DiffRevisions(oldIds, newIds);
        if (identities.IsEmpty) return [];
        var byToken = after.Items.ToDictionary(item => item.Token);
        var working = before.Items.ToList();
        var result = ImmutableArray.CreateBuilder<Op>();
        foreach (var op in identities)
        {
            switch (op)
            {
                case ReplaceOp:
                    return [new ReplaceOp(after.Value)];
                case SpliceOp splice:
                {
                    var inserted = splice.Items.Select(token => byToken[(long)token!]).ToList();
                    result.Add(new SpliceOp([], splice.Index, splice.Remove, [.. inserted.Select(item => (JsonNode?)RowValue(item))]));
                    working.RemoveRange((int)splice.Index, (int)splice.Remove);
                    working.InsertRange((int)splice.Index, inserted);
                    break;
                }
                case MoveOp move:
                {
                    result.Add(move);
                    var old = working.ToArray();
                    working = [.. move.Permutation.Select(index => old[index])];
                    break;
                }
                case SetOp set when set.Path.Length == 1 && set.Path[0].IsIndex:
                {
                    var index = (int)set.Path[0].Index;
                    var next = byToken[(long)set.Value!];
                    foreach (var rowOp in DeltaDiff.DiffRevisions(RowValue(working[index]), RowValue(next)))
                        result.Add(AtRow(index, rowOp));
                    working[index] = next;
                    break;
                }
                default:
                    // Numeric row tokens produce only replacements, root splices/moves and index sets.
                    throw new InvalidOperationException("Unexpected live-query identity delta.");
            }
        }
        return result.ToImmutable();
    }

    private static Op AtRow(int index, Op op) => op switch
    {
        ReplaceOp replace => new SetOp([index], replace.Value),
        SetOp set => new SetOp([index, .. set.Path], set.Value),
        DeleteOp delete => new DeleteOp([index, .. delete.Path]),
        AppendOp append => new AppendOp([index, .. append.Path], append.Text),
        TruncateOp truncate => new TruncateOp([index, .. truncate.Path], truncate.Count),
        SpliceOp splice => new SpliceOp([index, .. splice.Path], splice.Index, splice.Remove, splice.Items),
        MoveOp move => new MoveOp([index, .. move.Path], move.Permutation),
        _ => throw new InvalidOperationException("Unknown row delta."),
    };

    internal sealed record Run(Rows? Rows, Exception? Error);
    internal sealed record Rows(JsonArray Value, IReadOnlyList<Decoded> Items, IReadOnlyDictionary<string, Decoded> ByKey);
    internal sealed record Decoded(IReadOnlyDictionary<string, object?> Stored, IReadOnlyDictionary<string, JsonNode?> Row, long Token);
    private enum QueryState { Loading, Rows, Failed, Detached }
}
