// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table-writes.ts
//
// Table writes (slice 6a §5, §7, §9; S17; decisions "Writes carry their versions", "Index definitions are recorded"): the recorded form of
// every table write, the staged writes a transaction or an activation hands the commit step (their SQL rendered at staging, rule 8), and
// the appliers of the schema writes, which render DDL from the record. Upstream's TableWrite union is a record per kind here, each with
// ToJson in upstream's data form and key order, as the outbox and archives record it. A key as writes record it (upstream StoredKey) is a
// map of the key columns' Scalars; the json(...) columns' versions a write wrote (upstream ColumnVersions) a map of column to version. A
// write for a table the step does not have is an InvalidOperationException (memory and disk disagree: the step fails, which stops the
// store).
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Store;

/// <summary>The table writes as the outbox and archives record them (upstream <c>TableWrite</c>).</summary>
internal abstract record TableWrite(string Table) : StoreWrite
{
    /// <summary><c>{ kind: "table", table, version, state, columns, key, fingerprint }</c>: the table created when the store does not have
    /// it yet, its record replaced otherwise (upstream <c>TableDefinitionWrite</c>).</summary>
    internal sealed record Definition(string Table, long Version, TableState State, IReadOnlyDictionary<string, ColumnRecord> Columns,
        IReadOnlyList<string> Key, string Fingerprint) : TableWrite(Table)
    {
        public override string Kind => "table";

        public override JsonObject ToJson() => new()
        {
            ["kind"] = Kind,
            ["table"] = Table,
            ["version"] = Version,
            ["state"] = StoreInternal.Name(State),
            ["columns"] = StoreInternal.ColumnsJson(Columns),
            ["key"] = StoreInternal.Strings(Key),
            ["fingerprint"] = Fingerprint,
        };
    }

    /// <summary><c>{ kind: "index", table, name, columns }</c>.</summary>
    internal sealed record Index(string Table, string Name, IReadOnlyList<string> Columns) : TableWrite(Table)
    {
        public override string Kind => "index";

        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["table"] = Table, ["name"] = Name, ["columns"] = StoreInternal.Strings(Columns) };
    }

    /// <summary><c>{ kind: "dropIndex", table, name }</c>.</summary>
    internal sealed record DropIndex(string Table, string Name) : TableWrite(Table)
    {
        public override string Kind => "dropIndex";

        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["table"] = Table, ["name"] = Name };
    }

    /// <summary><c>{ kind: "alter", table, version, edit }</c>: a column edit of the migration to <paramref name="Version"/>; DDL rendered
    /// from it.</summary>
    internal sealed record Alter(string Table, long Version, AlterEdit Edit) : TableWrite(Table)
    {
        public override string Kind => "alter";

        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["table"] = Table, ["version"] = Version, ["edit"] = Edit.ToJson() };
    }

    /// <summary><c>{ kind: "put", table, row, version, columns }</c>: <paramref name="Version"/> is the row's eager-step version (the
    /// table's version at the put).</summary>
    internal sealed record Put(string Table, IReadOnlyDictionary<string, JsonNode?> Row, long Version, IReadOnlyDictionary<string, long> Columns)
        : TableWrite(Table)
    {
        public override string Kind => "put";

        public override JsonObject ToJson() => new()
        {
            ["kind"] = Kind,
            ["table"] = Table,
            ["row"] = StoreInternal.RowJson(Row),
            ["version"] = Version,
            ["columns"] = StoreInternal.VersionsJson(Columns),
        };
    }

    /// <summary><c>{ kind: "update", table, key, set, columns, count, version? }</c>: <paramref name="Version"/> only for a migration's data
    /// step, the version it stamps as the row's <c>$v</c> (a plugin's update never does).</summary>
    internal sealed record Update(string Table, IReadOnlyDictionary<string, Scalar> Key, SetNode Set, IReadOnlyDictionary<string, long> Columns,
        long Count, long? Version = null) : TableWrite(Table)
    {
        public override string Kind => "update";

        public override JsonObject ToJson()
        {
            var json = new JsonObject
            {
                ["kind"] = Kind,
                ["table"] = Table,
                ["key"] = StoreInternal.KeyJson(Key),
                ["set"] = Set.ToJson(),
                ["columns"] = StoreInternal.VersionsJson(Columns),
                ["count"] = Count,
            };
            if (Version is { } version) json["version"] = version;
            return json;
        }
    }

    /// <summary><c>{ kind: "delete", table, key, count }</c>.</summary>
    internal sealed record Delete(string Table, IReadOnlyDictionary<string, Scalar> Key, long Count) : TableWrite(Table)
    {
        public override string Kind => "delete";

        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["table"] = Table, ["key"] = StoreInternal.KeyJson(Key), ["count"] = Count };
    }

    /// <summary><c>{ kind: "updateWhere", table, where, set, columns, count, version? }</c>: <paramref name="Version"/> only for a
    /// migration's update step, the version it stamps as the rows' <c>$v</c>.</summary>
    internal sealed record UpdateWhere(string Table, WhereNode Where, SetNode Set, IReadOnlyDictionary<string, long> Columns, long Count,
        long? Version = null) : TableWrite(Table)
    {
        public override string Kind => "updateWhere";

        public override JsonObject ToJson()
        {
            var json = new JsonObject
            {
                ["kind"] = Kind,
                ["table"] = Table,
                ["where"] = Where.ToJson(),
                ["set"] = Set.ToJson(),
                ["columns"] = StoreInternal.VersionsJson(Columns),
                ["count"] = Count,
            };
            if (Version is { } version) json["version"] = version;
            return json;
        }
    }

    /// <summary><c>{ kind: "deleteWhere", table, where, count }</c>.</summary>
    internal sealed record DeleteWhere(string Table, WhereNode Where, long Count) : TableWrite(Table)
    {
        public override string Kind => "deleteWhere";

        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["table"] = Table, ["where"] = Where.ToJson(), ["count"] = Count };
    }
}

internal static partial class StoreInternal
{
    /// <summary>A recorded row in JSON: copies of its cells, in its order.</summary>
    internal static JsonObject RowJson(IReadOnlyDictionary<string, JsonNode?> row)
    {
        var json = new JsonObject();
        foreach (var (column, value) in row) json[column] = value?.DeepClone();
        return json;
    }

    /// <summary>A recorded key in JSON.</summary>
    internal static JsonObject KeyJson(IReadOnlyDictionary<string, Scalar> key)
    {
        var json = new JsonObject();
        foreach (var (column, value) in key) json[column] = value.ToNode();
        return json;
    }

    /// <summary>The <c>json(...)</c> columns' versions in JSON.</summary>
    internal static JsonObject VersionsJson(IReadOnlyDictionary<string, long> versions)
    {
        var json = new JsonObject();
        foreach (var (column, version) in versions) json[column] = version;
        return json;
    }

    /// <summary>The versions of the <c>json(...)</c> columns of <paramref name="definition"/> that <paramref name="set"/> writes, which
    /// its statement stamps (upstream <c>jsonVersions</c>).</summary>
    internal static OrderedDictionary<string, long> JsonVersions(TableDefinition definition, SetNode set)
    {
        var versions = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in set.Keys)
        {
            var column = definition.ByName[name];
            if (column.Type == ColumnType.Json) versions[name] = column.Json!.Version;
        }
        return versions;
    }

    /// <summary>The <c>columns</c> a write records: the versions of the <c>json(...)</c> columns it sets to a value (a clear has none;
    /// upstream <c>recordedVersions</c>).</summary>
    internal static OrderedDictionary<string, long> RecordedVersions(SetNode set, IReadOnlyDictionary<string, long> versions)
    {
        var columns = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (column, version) in versions) if (set[column] is not null) columns[column] = version;
        return columns;
    }

    /// <summary>Stages a <c>table</c> write: the table created when the store does not have it yet, its record replaced otherwise
    /// (upstream <c>stageTable</c>).</summary>
    internal static IStagedWrite StageTable(TableWrite.Definition write) =>
        new StagedWrite(WriteOrder.Write, async step => [await ApplyTableAsync(step, write).ConfigureAwait(false)]);

    /// <summary>Stages an <c>index</c> write (upstream <c>stageIndex</c>).</summary>
    internal static IStagedWrite StageIndex(TableWrite.Index write) =>
        new StagedWrite(WriteOrder.Write, async step => [await ApplyIndexAsync(step, write).ConfigureAwait(false)]);

    /// <summary>Stages one edit of the migration of <paramref name="table"/> to <paramref name="version"/> as its <c>alter</c>,
    /// <c>index</c> or <c>dropIndex</c> write (upstream <c>stageEdit</c>).</summary>
    internal static IStagedWrite StageEdit(string table, long version, TableEdit edit)
    {
        switch (edit)
        {
            case TableEdit.Index index:
                return StageIndex(new TableWrite.Index(table, index.Name, index.Columns));
            case TableEdit.DropIndex dropIndex:
            {
                var write = new TableWrite.DropIndex(table, dropIndex.Name);
                return new StagedWrite(WriteOrder.Write, async step => [await ApplyDropIndexAsync(step, write).ConfigureAwait(false)]);
            }
            default:
            {
                var write = new TableWrite.Alter(table, version, (AlterEdit)edit);
                return new StagedWrite(WriteOrder.Write, async step => [await ApplyAlterAsync(step, write).ConfigureAwait(false)]);
            }
        }
    }

    /// <summary>Stages a <c>put</c> of an encoded row with its <c>INSERT OR REPLACE</c> (an upsert: the whole row replaced; upstream
    /// <c>stagePut</c>).</summary>
    internal static IStagedWrite StagePut(string sql, string table, EncodedRow row, long version)
    {
        StoreWrite write = new TableWrite.Put(table, row.Row, version, row.Columns);
        var parameters = row.Params.ToArray();
        return new StagedWrite(WriteOrder.Write, async step =>
        {
            await step.RunAsync(sql, parameters).ConfigureAwait(false);
            return [write];
        });
    }

    /// <summary>Stages an <c>update</c> of one row by key; its count (0 for an absent key) is recorded, and <paramref name="version"/> when
    /// the statement stamps the row's <c>$v</c> (a migration's <c>rows</c> step; upstream <c>stageUpdate</c>).</summary>
    internal static IStagedWrite StageUpdate(Statement statement, string table, IReadOnlyDictionary<string, Scalar> key, SetNode set,
        IReadOnlyDictionary<string, long> columns, long? version = null) =>
        Counted(statement, count => new TableWrite.Update(table, key, set, columns, count, version));

    /// <summary>Stages a <c>delete</c> of one row by key; its count is recorded (upstream <c>stageDelete</c>).</summary>
    internal static IStagedWrite StageDelete(Statement statement, string table, IReadOnlyDictionary<string, Scalar> key) =>
        Counted(statement, count => new TableWrite.Delete(table, key, count));

    /// <summary>Stages an <c>updateWhere</c>: one statement, its count recorded (a replica affecting another count stops, S17), and
    /// <paramref name="version"/> when it stamps the rows' <c>$v</c> (a migration's <c>update</c> step; upstream
    /// <c>stageUpdateWhere</c>).</summary>
    internal static IStagedWrite StageUpdateWhere(Statement statement, string table, WhereNode where, SetNode set,
        IReadOnlyDictionary<string, long> columns, long? version = null) =>
        Counted(statement, count => new TableWrite.UpdateWhere(table, where, set, columns, count, version));

    /// <summary>Stages a <c>deleteWhere</c>: one statement, its count recorded (upstream <c>stageDeleteWhere</c>).</summary>
    internal static IStagedWrite StageDeleteWhere(Statement statement, string table, WhereNode where) =>
        Counted(statement, count => new TableWrite.DeleteWhere(table, where, count));

    private static StagedWrite Counted(Statement statement, Func<long, TableWrite> record)
    {
        var parameters = statement.Params.ToArray();
        return new StagedWrite(WriteOrder.Write, async step => [record(await step.RunCountedAsync(statement.Sql, parameters).ConfigureAwait(false))]);
    }

    /// <summary>Applies a <c>table</c> write: <c>CREATE TABLE</c> when the store does not have the table yet, then its record (upstream
    /// <c>applyTable</c>).</summary>
    internal static async Task<StoreWrite> ApplyTableAsync(Step step, TableWrite.Definition write)
    {
        var existing = step.Table(write.Table);
        if (existing is null) await step.RunAsync(CreateTableSql(write.Table, write.Columns, write.Key)).ConfigureAwait(false);
        step.Tables[write.Table] = new TableRecord(write.Table, write.Version, write.State, write.Columns, write.Key, write.Fingerprint,
            existing?.Indexes ?? new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        return write;
    }

    /// <summary>Applies an <c>index</c> write: <c>CREATE INDEX</c> and the record's index (upstream <c>applyIndex</c>).</summary>
    internal static async Task<StoreWrite> ApplyIndexAsync(Step step, TableWrite.Index write)
    {
        var existing = Recorded(step, write.Table);
        await step.RunAsync(CreateIndexSql(write.Table, write.Name, write.Columns, existing.Key)).ConfigureAwait(false);
        var indexes = new OrderedDictionary<string, IReadOnlyList<string>>(existing.Indexes, StringComparer.Ordinal) { [write.Name] = write.Columns };
        step.Tables[write.Table] = existing with { Indexes = indexes };
        return write;
    }

    /// <summary>Applies a <c>dropIndex</c> write: <c>DROP INDEX</c> and the record without it (upstream <c>applyDropIndex</c>).</summary>
    internal static async Task<StoreWrite> ApplyDropIndexAsync(Step step, TableWrite.DropIndex write)
    {
        var existing = Recorded(step, write.Table);
        await step.RunAsync(DropIndexSql(write.Table, write.Name)).ConfigureAwait(false);
        var indexes = new OrderedDictionary<string, IReadOnlyList<string>>(existing.Indexes, StringComparer.Ordinal);
        indexes.Remove(write.Name);
        step.Tables[write.Table] = existing with { Indexes = indexes };
        return write;
    }

    /// <summary>Applies an <c>alter</c> write: its DDL (a <c>json(...)</c> column's version column follows it) and the record's change
    /// (upstream <c>applyAlter</c>).</summary>
    internal static async Task<StoreWrite> ApplyAlterAsync(Step step, TableWrite.Alter write)
    {
        var existing = Recorded(step, write.Table);
        var id = Quote(write.Table);
        switch (write.Edit)
        {
            case AlterEdit.Rename rename:
            {
                var json = existing.Columns.TryGetValue(rename.From, out var renamed) && renamed.Type == ColumnType.Json;
                await step.RunAsync($"ALTER TABLE {id} RENAME COLUMN {Quote(rename.From)} TO {Quote(rename.To)}").ConfigureAwait(false);
                if (json)
                {
                    var (from, to) = (VersionColumn(rename.From), VersionColumn(rename.To));
                    await step.RunAsync($"ALTER TABLE {id} RENAME COLUMN {Quote(from)} TO {Quote(to)}").ConfigureAwait(false);
                }
                string Renamed(string column) => column == rename.From ? rename.To : column;
                var columns = new OrderedDictionary<string, ColumnRecord>(StringComparer.Ordinal);
                foreach (var (name, column) in existing.Columns) columns[Renamed(name)] = column;
                var indexes = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                foreach (var (name, on) in existing.Indexes) indexes[name] = [.. on.Select(Renamed)];
                step.Tables[write.Table] = existing with { Columns = columns, Key = [.. existing.Key.Select(Renamed)], Indexes = indexes };
                break;
            }
            case AlterEdit.Add add:
            {
                foreach (var definition in ColumnSql(add.Name, add.Column))
                    await step.RunAsync($"ALTER TABLE {id} ADD COLUMN {definition}").ConfigureAwait(false);
                var columns = new OrderedDictionary<string, ColumnRecord>(existing.Columns, StringComparer.Ordinal) { [add.Name] = add.Column };
                step.Tables[write.Table] = existing with { Columns = columns };
                break;
            }
            case AlterEdit.Drop drop:
            {
                var json = existing.Columns.TryGetValue(drop.Name, out var dropped) && dropped.Type == ColumnType.Json;
                await step.RunAsync($"ALTER TABLE {id} DROP COLUMN {Quote(drop.Name)}").ConfigureAwait(false);
                if (json) await step.RunAsync($"ALTER TABLE {id} DROP COLUMN {Quote(VersionColumn(drop.Name))}").ConfigureAwait(false);
                var columns = new OrderedDictionary<string, ColumnRecord>(existing.Columns, StringComparer.Ordinal);
                columns.Remove(drop.Name);
                step.Tables[write.Table] = existing with { Columns = columns };
                break;
            }
            case AlterEdit.Json edit:
            {
                var columns = new OrderedDictionary<string, ColumnRecord>(existing.Columns, StringComparer.Ordinal) { [edit.Name] = edit.Column };
                step.Tables[write.Table] = existing with { Columns = columns };
                break;
            }
        }
        return write;
    }

    /// <summary>The record of <paramref name="table"/> in <paramref name="step"/>; a write for a table the store does not have fails the
    /// step.</summary>
    private static TableRecord Recorded(Step step, string table) =>
        // Memory and disk disagree: the step fails, which stops the store.
        step.Table(table) ?? throw new InvalidOperationException($"table {table}: not in the store");
}
