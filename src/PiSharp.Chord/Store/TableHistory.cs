// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table-history.ts
//
// A table's history (slice 6a §9; decision "Second spec review: the majority"): the versions from a base Tables.DefineTable through each
// Tables.EvolveTable, the migration each version recorded, and what an activation does with the version the store recorded: create the
// table, use it, migrate it from there, or refuse it. Upstream's tagged unions are records here (TableEdit, DataStep, TablePlan), each
// edit with ToJson in upstream's data form, as a migration's writes record it. A planning refusal is an InvalidOperationException
// (upstream's Error at activation: the object fails broken, the data stays).
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Store;

/// <summary>One edit of a migration as its writes record it (<c>alter</c>, <c>index</c>, <c>dropIndex</c>); the table is named when
/// applied (upstream <c>TableEdit</c>).</summary>
internal abstract record TableEdit
{
    private protected TableEdit()
    {
    }

    /// <summary>The edit's tag: <c>rename</c>, <c>add</c>, <c>drop</c>, <c>json</c>, <c>index</c> or <c>dropIndex</c>.</summary>
    internal abstract string Kind { get; }

    /// <summary>The edit in upstream's data form.</summary>
    internal abstract JsonObject ToJson();

    /// <summary><c>{ kind: "index", name, columns }</c>: an index added over <paramref name="Columns"/> (declared; the key is appended when
    /// rendered).</summary>
    internal sealed record Index(string Name, IReadOnlyList<string> Columns) : TableEdit
    {
        internal override string Kind => "index";

        internal override JsonObject ToJson() => new() { ["kind"] = Kind, ["name"] = Name, ["columns"] = StoreInternal.Strings(Columns) };
    }

    /// <summary><c>{ kind: "dropIndex", name }</c>: an index dropped.</summary>
    internal sealed record DropIndex(string Name) : TableEdit
    {
        internal override string Kind => "dropIndex";

        internal override JsonObject ToJson() => new() { ["kind"] = Kind, ["name"] = Name };
    }
}

/// <summary>The column edits an <c>alter</c> write records (upstream <c>AlterEdit</c>: <c>rename</c>, <c>add</c>, <c>drop</c>,
/// <c>json</c>).</summary>
internal abstract record AlterEdit : TableEdit
{
    private protected AlterEdit()
    {
    }

    /// <summary><c>{ kind: "rename", from, to }</c>: a column renamed; the key, the indexes and a <c>json(...)</c> column's version column
    /// follow.</summary>
    internal sealed record Rename(string From, string To) : AlterEdit
    {
        internal override string Kind => "rename";

        internal override JsonObject ToJson() => new() { ["kind"] = Kind, ["from"] = From, ["to"] = To };
    }

    /// <summary><c>{ kind: "add", name, column }</c>: a column added (<c>Opt(...)</c>, or with a default existing rows get).</summary>
    internal sealed record Add(string Name, ColumnRecord Column) : AlterEdit
    {
        internal override string Kind => "add";

        internal override JsonObject ToJson() => new() { ["kind"] = Kind, ["name"] = Name, ["column"] = Column.ToJson() };
    }

    /// <summary><c>{ kind: "drop", name }</c>: a non-key column dropped, with a <c>json(...)</c> one's version column.</summary>
    internal sealed record Drop(string Name) : AlterEdit
    {
        internal override string Kind => "drop";

        internal override JsonObject ToJson() => new() { ["kind"] = Kind, ["name"] = Name };
    }

    /// <summary><c>{ kind: "json", name, column }</c>: a <c>json(...)</c> column's new version; no DDL.</summary>
    internal sealed record Json(string Name, ColumnRecord Column) : AlterEdit
    {
        internal override string Kind => "json";

        internal override JsonObject ToJson() => new() { ["kind"] = Kind, ["name"] = Name, ["column"] = Column.ToJson() };
    }
}

/// <summary>A version's data step over <see cref="Table"/>, the columns between the edits before it and after it (upstream
/// <c>DataStep</c>): <see cref="Rows"/> patches each row (synchronous and pure), <see cref="Update"/> is one <c>updateWhere</c>.</summary>
internal abstract record DataStep(TableDefinition Table)
{
    /// <summary><c>{ kind: "rows", table, patch }</c>: <paramref name="Patch"/> gets each row as the columns stand here and returns new
    /// values of non-key columns: an <c>IReadOnlyDictionary&lt;string, Operand&gt;</c> (<see cref="Operand.Undefined"/> clears an
    /// <c>Opt(...)</c> column) or a JsonObject of plain values, checked when it runs.</summary>
    internal sealed record Rows(TableDefinition Table, Func<IReadOnlyDictionary<string, JsonNode?>, object?> Patch) : DataStep(Table);

    /// <summary><c>{ kind: "update", table, where, set }</c>: the step compiled at declaration.</summary>
    internal sealed record Update(TableDefinition Table, WhereNode? Where, SetNode Set) : DataStep(Table);
}

/// <summary>What one <c>EvolveTable</c> changed: the edits before the data step, the step, the edits after it (upstream
/// <c>Migration</c>).</summary>
internal sealed record Migration(IReadOnlyList<TableEdit> Before, DataStep? Step, IReadOnlyList<TableEdit> After);

/// <summary>Where the first version of a migration plan starts: from its (A) commit, or after it (upstream <c>"begin" | "resume"</c>).</summary>
internal enum MigrationStart { Begin, Resume }

/// <summary>What an activation does with a declared table (upstream <c>TablePlan</c>): create it at the newest version, use it as recorded,
/// or migrate it through <see cref="Migrate.Versions"/> (oldest first), the first one from its start or, recorded <c>migrating</c>, after
/// its (A) commit.</summary>
internal abstract record TablePlan
{
    private protected TablePlan()
    {
    }

    /// <summary><c>{ kind: "create" }</c>.</summary>
    internal sealed record Create : TablePlan;

    /// <summary><c>{ kind: "ready" }</c>.</summary>
    internal sealed record Ready : TablePlan;

    /// <summary><c>{ kind: "migrate", versions, first }</c>.</summary>
    internal sealed record Migrate(IReadOnlyList<TableDefinition> Versions, MigrationStart First) : TablePlan;
}

internal static partial class StoreInternal
{
    /// <summary>The plan for <paramref name="definition"/> (the code's newest version) against <paramref name="recorded"/>. A recorded
    /// version newer than the newest, older than the history's base, or the same version with another fingerprint (a history edited in
    /// place) throws: the object fails <c>broken</c>, the data stays (upstream <c>planTable</c>).</summary>
    internal static TablePlan PlanTable(TableDefinition definition, TableRecord? recorded, string what)
    {
        if (recorded is null) return new TablePlan.Create();
        var (version, state, fingerprint) = (recorded.Version, recorded.State, recorded.Fingerprint);
        if (version > definition.Version)
            throw new InvalidOperationException($"{what}: the store has version {version}, newer than the code's {definition.Version}");
        var versions = HistoryOf(definition);
        var at = versions.FirstOrDefault(each => each.Version == version)
            ?? throw new InvalidOperationException($"{what}: the store has version {version}, older than the history's base {versions[0].Version}");
        if (at.Fingerprint != fingerprint)
            throw new InvalidOperationException($"{what}: version {version} is recorded with another definition (a history edited in place)");
        if (state == TableState.Ready && version == definition.Version) return new TablePlan.Ready();
        if (state == TableState.Migrating && at.Evolution?.Migration.Step is null)
            throw new InvalidOperationException($"{what}: version {version} is recorded migrating, but has no data step to resume");
        if (state == TableState.Migrating) return new TablePlan.Migrate([.. versions.Where(each => each.Version >= version)], MigrationStart.Resume);
        return new TablePlan.Migrate([.. versions.Where(each => each.Version > version)], MigrationStart.Begin);
    }

    /// <summary>Every version of <paramref name="definition"/>'s history, the base first.</summary>
    private static List<TableDefinition> HistoryOf(TableDefinition definition)
    {
        var versions = new List<TableDefinition>();
        for (var at = definition; at is not null; at = at.Evolution?.Previous) versions.Insert(0, at);
        return versions;
    }
}
