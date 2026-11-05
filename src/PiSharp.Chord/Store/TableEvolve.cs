// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table-evolve.ts
//
// Tables.EvolveTable(previous, t => t...) (slice 6a §9; decision "Second spec review: the majority"): the next version of a table from
// the edits of one migration. Upstream's editor types order the edits (rename, add and json before the data step, drop, index and
// dropIndex after it) and track the columns and key through the chain; at runtime one editor records the edits, checks each as it is
// made, and materializes the new version at the end. C# has no such types, so there is one editor, TableEditor (as cv2-evolve's one
// ContractEditor), and the order is the runtime check upstream's editor makes for JavaScript callers. Upstream's TableTailEditor,
// AddedColumn, PatchOf and the Renamed/With/Without/Next types are type-level only. A declaration refusal is an ArgumentException with
// upstream's message (a compileWhere or compileSet refusal of an update step stays their ChordError).
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Store;

public static partial class Tables
{
    /// <summary>Appends the next version (<c>previous.Version + 1</c>) with the edits <paramref name="edit"/> makes; it must return the
    /// editor it was given. Every version is materialized and fingerprinted here; the definition serves the newest, and its activation
    /// migrates a store at an older version through each one (upstream <c>evolveTable</c>).</summary>
    public static TableDefinition EvolveTable(TableDefinition previous, Func<TableEditor, TableEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(edit);
        var editor = new TableEditor(previous);
        ContractsInternal.EditWith(editor, edit, $"{previous.Name}: evolveTable");
        return editor.Finish();
    }
}

/// <summary>The edits of one <see cref="Tables.EvolveTable"/> call (upstream's <c>TableEditor</c> and <c>TableTailEditor</c>, and the
/// runtime <c>Editor</c>): the columns, key and indexes as the edits leave them, the edits before and after the data step, and the step.
/// <see cref="Rename"/>, <see cref="Add"/> and <see cref="Json"/> come before the data step (<see cref="Rows"/> or <see cref="Update"/>,
/// at most one) and before <see cref="Drop"/>, <see cref="Index"/> and <see cref="DropIndex"/>. Each edit is checked as it is made
/// (declaration policy) and returns this editor.</summary>
public sealed class TableEditor
{
    private readonly TableDefinition previous;
    private readonly string what;
    private OrderedDictionary<string, Property> schemas;
    private List<string> key;
    private readonly OrderedDictionary<string, IReadOnlyList<string>> indexes;
    /// <summary>False until the data step or the first Drop, Index or DropIndex.</summary>
    private bool after;
    private readonly List<TableEdit> before = [];
    private DataStep? step;
    private readonly List<TableEdit> afterStep = [];

    internal TableEditor(TableDefinition previous)
    {
        this.previous = previous;
        what = $"{previous.Name} version {previous.Version + 1}";
        schemas = new OrderedDictionary<string, Property>(previous.ColumnSchemas, StringComparer.Ordinal);
        key = [.. previous.Key];
        indexes = new OrderedDictionary<string, IReadOnlyList<string>>(previous.Indexes, StringComparer.Ordinal);
    }

    /// <summary>Renames a column; the key, the indexes and a <c>json(...)</c> column's version column follow.</summary>
    public TableEditor Rename(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        AssertBefore("rename");
        AssertColumn(from, "rename");
        // Checked here, not by the final definition: a later edit may rename it again, but this one runs as DDL.
        StoreInternal.CheckColumnName(to, $"{what}: rename");
        AssertNewName(to, "rename", from);
        string Renamed(string column) => column == from ? to : column;
        var renamed = new OrderedDictionary<string, Property>(StringComparer.Ordinal);
        foreach (var (name, schema) in schemas) renamed[Renamed(name)] = schema;
        schemas = renamed;
        key = [.. key.Select(Renamed)];
        foreach (var name in indexes.Keys.ToList()) indexes[name] = [.. indexes[name].Select(Renamed)];
        before.Add(new AlterEdit.Rename(from, to));
        return this;
    }

    /// <summary>Adds a column: <c>Opt(...)</c>, or a scalar one with a default (existing rows get it).</summary>
    public TableEditor Add(string name, Property column)
    {
        ArgumentNullException.ThrowIfNull(name);
        AssertBefore("add");
        AssertNewName(name, "add");
        var described = StoreInternal.DescribeColumns([KeyValuePair.Create(name, column)], $"{what}: add")[0];
        if (!described.Optional && (described.Type == ColumnType.Json || described.Default is null))
            throw new ArgumentException($"{what}: add: column \"{name}\" needs opt(...) or a default (existing rows get it)");
        schemas[name] = column;
        before.Add(new AlterEdit.Add(name, StoreInternal.RecordOf(described)));
        return this;
    }

    /// <summary>A <c>json(...)</c> column's new version (its nullability kept): no DDL, no data step, cells migrate on access. Pass
    /// <see cref="Tables.Json"/> itself, not <c>Opt(...)</c> of it.</summary>
    public TableEditor Json(string name, Property column)
    {
        ArgumentNullException.ThrowIfNull(name);
        AssertBefore("json");
        var existing = StoreInternal.DescribeColumns([KeyValuePair.Create(name, AssertColumn(name, "json"))], what)[0];
        if (existing.Type != ColumnType.Json) throw new ArgumentException($"{what}: json: column \"{name}\" is not a json(...) column");
        if (ContractsInternal.IsOptional(column.Value)) throw new ArgumentException($"{what}: json: pass json(...) itself; the column keeps its nullability");
        Property schema = existing.Optional ? Schemas.Opt((JsonNode)column.Value!) : column;
        var described = StoreInternal.DescribeColumns([KeyValuePair.Create(name, schema)], $"{what}: json")[0];
        if (described.Type != ColumnType.Json) throw new ArgumentException($"{what}: json: expected a json(...) column");
        var (was, now) = (existing.Json!, described.Json!);
        // D9 across table versions: a cell's version says which schema it was written with.
        if (now.Version < was.Version) throw new ArgumentException($"{what}: json: column \"{name}\" goes back from version {was.Version}");
        if (now.Version == was.Version && now.Fingerprint != was.Fingerprint)
            throw new ArgumentException($"{what}: json: column \"{name}\" changes its schema at version {was.Version} (raise its version)");
        schemas[name] = schema;
        before.Add(new AlterEdit.Json(name, StoreInternal.RecordOf(described)));
        return this;
    }

    /// <summary>The data step: <paramref name="patch"/> gets each row as the columns stand here and returns new values of non-key columns
    /// (an empty map stamps the row only): an <c>IReadOnlyDictionary&lt;string, Operand&gt;</c>, where <see cref="Operand.Undefined"/>
    /// clears an <c>Opt(...)</c> column, or a JsonObject of plain values. Synchronous and pure, run in chunks of 100 in key order.</summary>
    public TableEditor Rows(Func<IReadOnlyDictionary<string, JsonNode?>, object?> patch)
    {
        AssertBefore("rows");
        if (patch is null) throw new ArgumentException($"{what}: rows: expected a function");
        step = new DataStep.Rows(Current(), patch);
        after = true;
        return this;
    }

    /// <summary>The data step as one <c>updateWhere</c> over the rows not migrated yet: <paramref name="set"/> gets the column references
    /// and returns the set (as <c>TableChanges</c>' sets: an <c>IReadOnlyDictionary&lt;string, Operand&gt;</c> or a JsonObject);
    /// <paramref name="where"/>, when given, limits it.</summary>
    public TableEditor Update(Func<IReadOnlyDictionary<string, ColumnRef>, object?> set, Func<IReadOnlyDictionary<string, ColumnRef>, Condition>? where = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Update((object)set, where);
    }

    /// <summary>The data step as one <c>updateWhere</c> over the rows not migrated yet, with the plain values <paramref name="set"/> (an
    /// <c>IReadOnlyDictionary&lt;string, Operand&gt;</c> or a JsonObject); <paramref name="where"/>, when given, limits it.</summary>
    public TableEditor Update(object? set, Func<IReadOnlyDictionary<string, ColumnRef>, Condition>? where = null)
    {
        AssertBefore("update");
        var table = Current();
        var stepWhat = $"version {table.Version} update";
        var condition = where?.Invoke(table.Refs);
        var values = set is Func<IReadOnlyDictionary<string, ColumnRef>, object?> function ? function(table.Refs) : set;
        step = new DataStep.Update(table, condition is null ? null : StoreInternal.CompileWhere(table, condition, stepWhat),
            StoreInternal.CompileSet(table, values, stepWhat));
        after = true;
        return this;
    }

    /// <summary>Drops a non-key column; its indexes are dropped first, in the same commit.</summary>
    public TableEditor Drop(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        after = true;
        AssertColumn(name, "drop");
        if (key.Contains(name)) throw new ArgumentException($"{what}: drop: \"{name}\" is a key column");
        foreach (var index in indexes.Keys.ToList())
        {
            if (!indexes[index].Contains(name)) continue;
            indexes.Remove(index);
            afterStep.Add(new TableEdit.DropIndex(index));
        }
        schemas.Remove(name);
        afterStep.Add(new AlterEdit.Drop(name));
        return this;
    }

    /// <summary>Adds an index (<c>[a-z0-9_]+</c>) over non-<c>Opt</c> <c>str</c> or <c>int</c> columns; it ends with the key.</summary>
    public TableEditor Index(string name, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(name);
        after = true;
        var indexWhat = $"{what}: index \"{name}\"";
        if (!StoreInternal.IndexName.IsMatch(name)) throw new ArgumentException($"{indexWhat}: expected [a-z0-9_]+");
        if (indexes.ContainsKey(name)) throw new ArgumentException($"{indexWhat} exists");
        // Checked against the columns at this edit: the migration creates the index here, whatever later edits do.
        var byName = StoreInternal.DescribeColumns(schemas, what).ToDictionary(column => column.Name, StringComparer.Ordinal);
        var checkedColumns = StoreInternal.OrderColumns(byName, columns, indexWhat);
        indexes[name] = checkedColumns;
        afterStep.Add(new TableEdit.Index(name, checkedColumns));
        return this;
    }

    /// <summary>Drops an index.</summary>
    public TableEditor DropIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        after = true;
        if (!indexes.Remove(name)) throw new ArgumentException($"{what}: dropIndex: no index \"{name}\"");
        afterStep.Add(new TableEdit.DropIndex(name));
        return this;
    }

    /// <summary>The new version, materialized and fingerprinted, linked to the previous one and the migration from it.</summary>
    internal TableDefinition Finish() =>
        new(previous.Name, Options(), new Evolution(previous, new Migration([.. before], step, [.. afterStep])));

    /// <summary>The table as the edits so far leave it, at the new version: what a data step reads and writes.</summary>
    private TableDefinition Current() => new(previous.Name, Options());

    /// <summary>The key and indexes are names the edits kept; the constructor checks them as it checks a base's.</summary>
    private TableOptions Options() => new()
    {
        Version = previous.Version + 1,
        Columns = new OrderedDictionary<string, Property>(schemas, StringComparer.Ordinal),
        Key = [.. key],
        Indexes = new OrderedDictionary<string, IReadOnlyList<string>>(indexes, StringComparer.Ordinal),
    };

    private void AssertBefore(string edit)
    {
        if (after) throw new ArgumentException($"{what}: {edit} comes before the data step and before drop, index and dropIndex");
    }

    /// <summary><paramref name="name"/> names no column yet, also ignoring ASCII case (SQLite's column names do): checked per edit, since a
    /// later Drop cannot take back an Add the migration runs first. A rename may change only the case of <paramref name="from"/>.</summary>
    private void AssertNewName(string name, string edit, string? from = null)
    {
        var folded = FoldAscii(name);
        foreach (var existing in schemas.Keys)
        {
            if (existing != from && FoldAscii(existing) == folded)
                throw new ArgumentException($"{what}: {edit}: column \"{existing}\" exists (names differing only in case collide)");
        }
    }

    private Property AssertColumn(string name, string edit) =>
        schemas.TryGetValue(name, out var schema) ? schema : throw new ArgumentException($"{what}: {edit}: no column \"{name}\"");

    /// <summary><paramref name="name"/> with ASCII letters lowercased and nothing else changed, as SQLite folds identifiers (not
    /// ToLowerInvariant, which also folds U+212A KELVIN SIGN to k).</summary>
    private static string FoldAscii(string name) => string.Create(name.Length, name, (span, text) =>
    {
        for (var index = 0; index < text.Length; index++) span[index] = text[index] is >= 'A' and <= 'Z' ? (char)(text[index] + 32) : text[index];
    });
}
