// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/import-migration.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

internal static partial class StoreInternal
{
    /// <summary>Brings archived rows through their owner's migration history, preserving row and JSON-cell versions.</summary>
    internal static IReadOnlyList<TableWrite.Put> MigrateRows(TableWrite.Definition write, IReadOnlyList<TableWrite.Put> puts,
        TableDefinition history, TableRecord recorded, ChordContext context)
    {
        var what = $"importHostData: table {write.Table}";
        var versions = HistoryOf(history);
        var from = versions.FirstOrDefault(each => each.Version == write.Version);
        var to = versions.FirstOrDefault(each => each.Version == recorded.Version);
        if (from is null || to is null)
            throw DataInternal.Invalid("args", $"{what}: version {write.Version} or {recorded.Version} is not in its owner's history");
        if (write.State == TableState.Migrating && from.Evolution?.Migration.Step is null)
            throw DataInternal.Invalid("args", $"{what}: version {write.Version} exported migrating has no data step to resume");
        var later = versions.Where(each => each.Version > from.Version && each.Version <= to.Version).ToArray();
        return [.. puts.Select(put => MigrateImportedRow(put, write.State == TableState.Migrating ? from : null, later, context))];
    }

    private sealed class ImportedRow(TableWrite.Put put)
    {
        internal OrderedDictionary<string, JsonNode?> Row { get; set; } = new(put.Row, StringComparer.Ordinal);
        internal OrderedDictionary<string, long> Cells { get; } = new(put.Columns, StringComparer.Ordinal);
        internal long Version { get; set; } = put.Version;
    }

    private static TableWrite.Put MigrateImportedRow(TableWrite.Put put, TableDefinition? resumed, IReadOnlyList<TableDefinition> later,
        ChordContext context)
    {
        var state = new ImportedRow(put);
        if (resumed is not null)
        {
            var migration = resumed.Evolution!.Migration;
            RunImportedStep(state, resumed.Version, migration.Step!, context);
            foreach (var edit in migration.After) ApplyImportedEdit(state, edit);
        }
        foreach (var definition in later)
        {
            var migration = definition.Evolution!.Migration;
            foreach (var edit in migration.Before) ApplyImportedEdit(state, edit);
            if (migration.Step is { } step) RunImportedStep(state, definition.Version, step, context);
            foreach (var edit in migration.After) ApplyImportedEdit(state, edit);
        }
        return put with { Row = state.Row, Version = state.Version, Columns = state.Cells };
    }

    private static void ApplyImportedEdit(ImportedRow state, TableEdit edit)
    {
        switch (edit)
        {
            case AlterEdit.Rename rename:
                if (state.Row.TryGetValue(rename.From, out var value)) state.Row[rename.To] = value;
                state.Row.Remove(rename.From);
                if (state.Cells.TryGetValue(rename.From, out var version)) state.Cells[rename.To] = version;
                state.Cells.Remove(rename.From);
                return;
            case AlterEdit.Add add:
                if (add.Column.Default is { } @default) state.Row[add.Name] = @default.ToNode();
                return;
            case AlterEdit.Drop drop:
                state.Row.Remove(drop.Name);
                state.Cells.Remove(drop.Name);
                return;
        }
        // JSON schema and index edits change no stored value until it is read or explicitly set.
    }

    private static void RunImportedStep(ImportedRow state, long version, DataStep step, ChordContext context)
    {
        if (state.Version >= version) return;
        SetNode set;
        if (step is DataStep.Rows rows)
            set = ImportedPatchSet(rows, rows.Table.Rows.Decode(ImportedSqliteRow(state, rows.Table), context), version);
        else
        {
            var update = (DataStep.Update)step;
            if (update.Where is not null && !Matches(update.Where, state.Row)) return;
            set = update.Set;
        }
        state.Row = ApplySet(set, state.Row, step.Table.Name);
        foreach (var (name, node) in set)
        {
            var column = step.Table.ByName[name];
            if (column.Type != ColumnType.Json) continue;
            if (node is null) state.Cells.Remove(name);
            else state.Cells[name] = column.Json!.Version;
        }
        state.Version = version;
    }

    private static SetNode ImportedPatchSet(DataStep.Rows step, IReadOnlyDictionary<string, JsonNode?> row, long version)
    {
        var definition = step.Table;
        var what = $"{definition.Name} version {version}: rows";
        var entries = PatchEntries(step.Patch(row)) ?? throw new InvalidOperationException($"{what}: expected an object of column values");
        var set = new SetNode();
        foreach (var (name, value) in entries)
        {
            if (!definition.ByName.TryGetValue(name, out var column)) throw new InvalidOperationException($"{what}: no column \"{name}\"");
            if (definition.Key.Contains(name)) throw new InvalidOperationException($"{what}: key column \"{name}\" cannot change");
            set[name] = CellOf(column, value, $"{what}: column \"{name}\"");
        }
        return set;
    }

    private static IReadOnlyDictionary<string, object?> ImportedSqliteRow(ImportedRow state, TableDefinition table)
    {
        var stored = new Dictionary<string, object?>(StringComparer.Ordinal) { [RowVersion] = state.Version };
        foreach (var column in table.Columns)
        {
            if (!state.Row.TryGetValue(column.Name, out var value)) stored[column.Name] = null;
            else if (column.Type == ColumnType.Json)
            {
                stored[column.Name] = ChordJson.Stringify(value);
                stored[VersionColumn(column.Name)] = state.Cells.TryGetValue(column.Name, out var version) ? version : null;
            }
            else stored[column.Name] = ToSqlite(CheckScalar(column.Type, value, $"{table.Name}: imported row"));
        }
        return stored;
    }
}
