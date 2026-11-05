// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table-migration.ts
//
// Declared tables brought to their version at activation (slice 6a §9): a table the store does not have is created at the newest version
// with its indexes (no replay); an older one is migrated eagerly, one version at a time, in its commits. Per version N: (A) the edits
// before the data step and table { version: N, state: "migrating" }; the data step's chunks; (Z) the last chunk (or the update) with the
// edits after the step and state: "ready". Without a data step (A) and (Z) are one commit. A version recorded migrating resumes after (A),
// skipping the rows already stamped $v >= N. Its record holds the columns during the step but version N's fingerprint, not the
// fingerprint of those columns: the fingerprint names the version being reached, which resume, export and import check.
//
// Upstream's macrotask (setImmediate, else a MessageChannel message) between chunks is Task.Yield here: the continuation is posted to the
// synchronization context or the thread pool, so other commits and timers run between chunks. A rows step's patch returns an
// IReadOnlyDictionary<string, Operand> (Operand.Undefined clears an Opt(...) column), an IReadOnlyDictionary<string, JsonNode?> or a JSON
// object; anything else is upstream's "expected an object of column values". The commits go through IChordStore (Store.cs), which the
// engine's ChordStore implements.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>A declared table: its newest definition and its SQL name (upstream <c>DeclaredTable</c>).</summary>
/// <param name="Definition">The newest definition.</param>
/// <param name="Table">The SQL name.</param>
internal sealed record DeclaredTable(TableDefinition Definition, string Table);

internal static partial class StoreInternal
{
    /// <summary>The size of a <c>rows</c> chunk: one commit each (upstream <c>MIGRATION_CHUNK</c>).</summary>
    internal const int MigrationChunk = 100;

    /// <summary>Brings <paramref name="tables"/> to their definitions: the ones the store does not have created in one commit, then each
    /// older one migrated. A table the code cannot use (see <see cref="PlanTable"/>) throws before anything is written;
    /// <paramref name="what"/> names the owner (upstream <c>prepareTables</c>).</summary>
    internal static async Task PrepareTablesAsync(IReadOnlyList<DeclaredTable> tables, IChordStore store, CommitFence fence, string what,
        ChordContext context)
    {
        var created = new List<IStagedWrite>();
        var migrations = new List<(string Table, TablePlan.Migrate Plan)>();
        foreach (var (definition, table) in tables)
        {
            var plan = PlanTable(definition, store.Table(table), $"{what}: table \"{definition.Name}\"");
            if (plan is TablePlan.Create) created.AddRange(Creation(definition, table));
            else if (plan is TablePlan.Migrate migrate) migrations.Add((table, migrate));
        }
        if (created.Count > 0) await CommitAsync(created, store, fence, context).ConfigureAwait(false);
        foreach (var (table, plan) in migrations) await MigrateTableAsync(plan, new MigrationTarget(store, table, fence), context).ConfigureAwait(false);
    }

    /// <summary>Where a migration runs: the store, the table's SQL name and the commits' fence (upstream <c>MigrationTarget</c>).</summary>
    private sealed record MigrationTarget(IChordStore Store, string Table, CommitFence Fence);

    /// <summary>The writes creating <paramref name="definition"/> fresh as <paramref name="table"/>: at its version, <c>ready</c>, with its
    /// indexes.</summary>
    private static List<IStagedWrite> Creation(TableDefinition definition, string table) =>
    [
        StageTable(new TableWrite.Definition(table, definition.Version, TableState.Ready, definition.Record, definition.Key, definition.Fingerprint)),
        .. definition.Indexes.Select(index => StageIndex(new TableWrite.Index(table, index.Key, index.Value))),
    ];

    /// <summary>Migrates the table through the plan's versions (oldest first, each with its migration), the first resumed after its (A)
    /// commit when the plan says so. <paramref name="context"/>'s abort is checked between chunks (a chunk admitted to the step
    /// completes); a data step that throws, or returns a patch the version cannot take, rejects: the table stays <c>migrating</c> and the
    /// committed chunks stay.</summary>
    private static async Task MigrateTableAsync(TablePlan.Migrate plan, MigrationTarget target, ChordContext context)
    {
        for (var index = 0; index < plan.Versions.Count; index++)
            await MigrateVersionAsync(plan.Versions[index], index == 0 ? plan.First : MigrationStart.Begin, target, context).ConfigureAwait(false);
    }

    /// <summary>Migrates to <paramref name="definition"/>'s version from its (A) commit (<c>Begin</c>) or after it (<c>Resume</c>).</summary>
    private static async Task MigrateVersionAsync(TableDefinition definition, MigrationStart from, MigrationTarget target, ChordContext context)
    {
        // Old handles are refused from now on: their SQL must never queue behind this version's DDL.
        var withdraw = target.Store.AnnounceTable(target.Table, definition.Version);
        try
        {
            await MigrateStepsAsync(definition, from, target, context).ConfigureAwait(false);
        }
        finally
        {
            withdraw();
        }
    }

    /// <summary>The commits of <see cref="MigrateVersionAsync"/>.</summary>
    private static async Task MigrateStepsAsync(TableDefinition definition, MigrationStart from, MigrationTarget target, ChordContext context)
    {
        // PlanTable puts only evolved versions in a plan.
        var (before, step, after) = definition.Evolution!.Migration;
        var table = target.Table;
        List<IStagedWrite> Edits(IReadOnlyList<TableEdit> list) => [.. list.Select(edit => StageEdit(table, definition.Version, edit))];
        List<IStagedWrite> ready = [.. Edits(after), RecordOf(definition, definition, TableState.Ready, table)];
        if (step is null)
        {
            await CommitAsync([.. Edits(before), .. ready], target.Store, target.Fence, context).ConfigureAwait(false);
            return;
        }
        if (from == MigrationStart.Begin)
            await CommitAsync([.. Edits(before), RecordOf(step.Table, definition, TableState.Migrating, table)], target.Store, target.Fence, context).ConfigureAwait(false);
        if (step is DataStep.Update update)
        {
            await CommitAsync([UpdateStep(update, definition.Version, table), .. ready], target.Store, target.Fence, context).ConfigureAwait(false);
            return;
        }
        await RowsStepAsync((DataStep.Rows)step, definition.Version, ready, target, context).ConfigureAwait(false);
    }

    /// <summary>The <c>table</c> write of <paramref name="columns"/>' columns and key, with <paramref name="definition"/>'s version and
    /// fingerprint, in <paramref name="state"/>.</summary>
    private static IStagedWrite RecordOf(TableDefinition columns, TableDefinition definition, TableState state, string table) =>
        StageTable(new TableWrite.Definition(table, definition.Version, state, columns.Record, columns.Key, definition.Fingerprint));

    /// <summary>The <c>update</c> step: one <c>updateWhere</c> over the rows below version <paramref name="v"/>, stamping it, so it never
    /// applies twice.</summary>
    private static IStagedWrite UpdateStep(DataStep.Update step, long v, string table)
    {
        var sql = SqlOf(step.Table, table);
        WhereNode below = new WhereNode.Compare(Comparison.Lt, RowVersion, (double)v);
        var where = step.Where is null ? below : new WhereNode.And([step.Where, below]);
        var versions = JsonVersions(step.Table, step.Set);
        var statement = sql.UpdateWhere(where, Stamped(step.Set, v), versions);
        return StageUpdateWhere(statement, table, where, step.Set, RecordedVersions(step.Set, versions), v);
    }

    /// <summary>The <c>rows</c> step: chunks of <see cref="MigrationChunk"/> rows below version <paramref name="v"/> in key order, paging
    /// forward by the last key; each patched row stamped <paramref name="v"/>; the last chunk commits with <c>ready</c>.</summary>
    private static async Task RowsStepAsync(DataStep.Rows step, long v, IReadOnlyList<IStagedWrite> ready, MigrationTarget target, ChordContext context)
    {
        var columns = step.Table;
        var sql = SqlOf(columns, target.Table);
        var order = new Order(columns.Key, OrderDirection.Asc);
        WhereNode below = new WhereNode.Compare(Comparison.Lt, RowVersion, (double)v);
        List<object?>? after = null;
        while (true)
        {
            var statement = sql.Select(below, order, after, MigrationChunk + 1);
            var stored = (await target.Store.ReadAsync(executor => executor.AllAsync(statement.Sql, [.. statement.Params]), context)
                .ConfigureAwait(false)).ToList();
            var last = stored.Count <= MigrationChunk;
            if (!last) stored.RemoveRange(MigrationChunk, stored.Count - MigrationChunk);
            var writes = stored.Select(row => PatchRow(step, row, v, sql, target.Table, context)).ToList();
            if (last)
            {
                await CommitAsync([.. writes, .. ready], target.Store, target.Fence, context).ConfigureAwait(false);
                return;
            }
            await CommitAsync(writes, target.Store, target.Fence, context).ConfigureAwait(false);
            var final = stored[MigrationChunk - 1];
            after = [.. columns.Key.Select(name => final[name])];
            // Upstream's macrotask: other commits and timers run between chunks.
            await Task.Yield();
            // An abandoned activation stops here.
            context.AbortSignal?.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The write of one row's patch: the patch run on the decoded row, its result checked (an object of non-key columns of the
    /// version, values of their types) and stamped <paramref name="v"/> through the internal form of <c>update</c>.</summary>
    private static IStagedWrite PatchRow(DataStep.Rows step, IReadOnlyDictionary<string, object?> stored, long v, TableSql sql, string table,
        ChordContext context)
    {
        var definition = step.Table;
        var patch = step.Patch(definition.Rows.Decode(stored, context));
        var what = $"{definition.Name} version {v}: rows";
        var entries = PatchEntries(patch) ?? throw new InvalidOperationException($"{what}: expected an object of column values");
        var set = new SetNode();
        foreach (var (name, value) in entries)
        {
            if (!definition.ByName.TryGetValue(name, out var column)) throw new InvalidOperationException($"{what}: no column \"{name}\"");
            if (definition.Key.Contains(name)) throw new InvalidOperationException($"{what}: key column \"{name}\" cannot change");
            set[name] = CellOf(column, value, $"{what}: column \"{name}\"");
        }
        var key = new OrderedDictionary<string, Scalar>(StringComparer.Ordinal);
        // Key columns are non-opt str or int: stored scalars.
        foreach (var name in definition.Key) key[name] = StoredScalar(stored[name]!);
        var versions = JsonVersions(definition, set);
        var parameters = definition.Key.Select(name => stored[name]).ToList();
        var statement = sql.Update(Stamped(set, v), parameters, versions);
        return StageUpdate(statement, table, key, set, RecordedVersions(set, versions), v);
    }

    /// <summary>A patch's entries in its own order; null when it is no object of column values.</summary>
    private static List<KeyValuePair<string, Operand>>? PatchEntries(object? patch) => patch switch
    {
        IReadOnlyDictionary<string, Operand> map => [.. map],
        JsonNode node when ChordJson.AsObject(node) is { } values =>
            [.. ChordJson.KeysInJavaScriptOrder(values).Select(name => KeyValuePair.Create(name, (Operand)values[name]))],
        IReadOnlyDictionary<string, JsonNode?> row => [.. row.Select(entry => KeyValuePair.Create(entry.Key, (Operand)entry.Value))],
        _ => null,
    };

    /// <summary>A patch value as a <c>set</c> node: <c>Undefined</c> clears an <c>Opt(...)</c> column, <c>json(...)</c> cells as JSON
    /// literals (a JSON null too); a scalar null is refused by <see cref="EncodeCell"/>. An expression is no value (upstream's
    /// <c>encodeCell</c> refuses an object for a scalar column).</summary>
    private static ExprNode? CellOf(Column column, Operand value, string what)
    {
        if (value.IsUndefined)
        {
            if (!column.Optional) throw new InvalidOperationException($"{what} is not opt(...) and cannot be cleared");
            return null;
        }
        if (value.Expression is not null) throw DataInternal.Invalid("args", $"{what}: expected a value, not an expression");
        var cell = EncodeCell(column, value.Value, what);
        // EncodeCell returns a scalar for a scalar column.
        return column.Type == ColumnType.Json ? new ExprNode.Json(cell.Stored) : new ExprNode.Literal(CheckScalar(column.Type, cell.Stored, what));
    }

    /// <summary><paramref name="set"/> with the row's <c>$v</c> stamped <paramref name="v"/>.</summary>
    private static SetNode Stamped(SetNode set, long v)
    {
        var stamped = new SetNode();
        foreach (var (column, node) in set) stamped[column] = node;
        stamped[RowVersion] = new ExprNode.Literal((double)v);
        return stamped;
    }

    /// <summary>A stored key cell (a string or a SQLite integer) as a scalar.</summary>
    private static Scalar StoredScalar(object value) => value is string text ? text : SqliteNumber(value);

    private static TableSql SqlOf(TableDefinition definition, string table) =>
        new(table, definition.Columns, definition.Rows.Physical, definition.Key);

    /// <summary>Commits <paramref name="writes"/> without the activation's abort: a commit admitted to the step completes (the fence
    /// refuses one).</summary>
    private static Task CommitAsync(IReadOnlyList<IStagedWrite> writes, IChordStore store, CommitFence fence, ChordContext context) =>
        store.CommitAsync(writes, fence, ChordContext.WithoutAbortSignal(context));
}
