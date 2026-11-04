// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/table-changes.ts
//
// A transaction's writes to one table (slice 6a §6, §7; W10): checked and staged synchronously like a draft's ops (no I/O, a bad row
// throws at its own call), kept in staging order for the commit step, and overlaid on committed rows for tx.get until a bulk write makes
// that impossible (invalid/read_after_bulk). Upstream's set and where arguments are values or callbacks of the column references
// (typeof x === "function"); here a set is a value (an IReadOnlyDictionary<string, Operand> or a JsonObject, as CompileSet takes) or a
// Func of the table's Refs, and a where a Condition or a Func of the Refs.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>The writes of one transaction to one table: each appended to the transaction's staged list (one order across tables) and, for
/// the single-key ones, to its key's overlay (upstream <c>TableChanges</c>).</summary>
internal sealed class TableChanges
{
    /// <summary>One staged write of one key, as the overlay replays it (upstream <c>KeyWrite</c>): a put's row, an update's set, or a
    /// delete (both null).</summary>
    private readonly record struct KeyWrite(IReadOnlyDictionary<string, JsonNode?>? Row, SetNode? Set);

    private readonly List<IStagedWrite> staged;
    private readonly Dictionary<string, List<KeyWrite>> byKey = new(StringComparer.Ordinal);
    /// <summary>An updateWhere or deleteWhere was staged: Get is refused from then on (W10).</summary>
    private bool bulk;

    internal TableChanges(TableHandle handle, List<IStagedWrite> staged)
    {
        Handle = handle;
        this.staged = staged;
    }

    /// <summary>The table's handle.</summary>
    internal TableHandle Handle { get; }

    /// <summary>Stages a <c>put</c> of <paramref name="row"/> (an upsert).</summary>
    internal void Put(object? row)
    {
        var (definition, sql) = (Handle.Definition, Handle.Sql);
        var encoded = definition.Rows.Put(row, definition.Version);
        staged.Add(StoreInternal.StagePut(sql.Put, sql.Table, encoded, definition.Version));
        Write(encoded.Id, new KeyWrite(encoded.Row, null));
    }

    /// <summary>Stages an <c>update</c> of the row with <paramref name="key"/>; <paramref name="set"/> is values or a callback of column
    /// references.</summary>
    internal void Update(object? key, object? set)
    {
        var (definition, sql) = (Handle.Definition, Handle.Sql);
        var encoded = definition.Rows.Key(key, "update");
        var compiled = SetOf(set, "update");
        var versions = StoreInternal.JsonVersions(definition, compiled);
        staged.Add(StoreInternal.StageUpdate(sql.Update(compiled, encoded.Params, versions), sql.Table, encoded.Key, compiled,
            StoreInternal.RecordedVersions(compiled, versions)));
        Write(encoded.Id, new KeyWrite(null, compiled));
    }

    /// <summary>Stages an <c>update</c> whose set is a callback of the column references.</summary>
    internal void Update(object? key, Func<IReadOnlyDictionary<string, ColumnRef>, object?> set) => Update(key, (object?)set);

    /// <summary>Stages a <c>delete</c> of the row with <paramref name="key"/>.</summary>
    internal void Delete(object? key)
    {
        var (definition, sql) = (Handle.Definition, Handle.Sql);
        var encoded = definition.Rows.Key(key, "delete");
        staged.Add(StoreInternal.StageDelete(new Statement(sql.Delete, [.. encoded.Params]), sql.Table, encoded.Key));
        Write(encoded.Id, new KeyWrite(null, null));
    }

    /// <summary>Stages an <c>updateWhere</c>: every row matching <paramref name="where"/> gets <paramref name="set"/>.</summary>
    internal void UpdateWhere(object? where, object? set)
    {
        var (definition, sql) = (Handle.Definition, Handle.Sql);
        var condition = StoreInternal.CompileWhere(definition, ConditionOf(where), "updateWhere");
        var compiled = SetOf(set, "updateWhere");
        var versions = StoreInternal.JsonVersions(definition, compiled);
        var statement = sql.UpdateWhere(condition, compiled, versions);
        staged.Add(StoreInternal.StageUpdateWhere(statement, sql.Table, condition, compiled, StoreInternal.RecordedVersions(compiled, versions)));
        bulk = true;
    }

    /// <summary>Stages an <c>updateWhere</c> with a callback condition and plain values.</summary>
    internal void UpdateWhere(Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where, object? set) => UpdateWhere((object?)where, set);

    /// <summary>Stages an <c>updateWhere</c> with a callback condition and a callback set.</summary>
    internal void UpdateWhere(Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where, Func<IReadOnlyDictionary<string, ColumnRef>, object?> set) =>
        UpdateWhere((object?)where, (object?)set);

    /// <summary>Stages a <c>deleteWhere</c>: every row matching <paramref name="where"/> goes.</summary>
    internal void DeleteWhere(object? where)
    {
        var (definition, sql) = (Handle.Definition, Handle.Sql);
        var condition = StoreInternal.CompileWhere(definition, ConditionOf(where), "deleteWhere");
        staged.Add(StoreInternal.StageDeleteWhere(sql.DeleteWhere(condition), sql.Table, condition));
        bulk = true;
    }

    /// <summary>Stages a <c>deleteWhere</c> with a callback condition.</summary>
    internal void DeleteWhere(Func<IReadOnlyDictionary<string, ColumnRef>, Condition> where) => DeleteWhere((object?)where);

    /// <summary>The row with <paramref name="key"/> as this transaction leaves it: the committed row with this transaction's <c>put</c>,
    /// <c>update</c> and <c>delete</c> of the key replayed; refused after a bulk write (W10).</summary>
    internal async Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(object? key, ChordContext context)
    {
        var definition = Handle.Definition;
        if (bulk) throw DataInternal.Invalid("read_after_bulk", $"{definition.Name}: tx.get after an updateWhere or deleteWhere on the table");
        var encoded = definition.Rows.Key(key, "get");
        var writes = byKey.TryGetValue(encoded.Id, out var list) ? list : [];
        var start = writes.Count;
        while (start > 0 && writes[start - 1].Set is not null) start--;
        // A committed row is decoded by the table's columns: scalars and JSON, what the evaluator takes.
        IReadOnlyDictionary<string, JsonNode?>? row = start == 0
            ? await Handle.GetAsync(StoredKey(encoded.Key), context).ConfigureAwait(false)
            : writes[start - 1].Row;
        foreach (var write in writes.Skip(start))
        {
            // An update of an absent row changes nothing.
            if (row is not null) row = StoreInternal.ApplySet(write.Set!, row, definition.Name);
        }
        return row;
    }

    /// <summary>A recorded key as a key object (its scalars as JSON values).</summary>
    private static Dictionary<string, JsonNode?> StoredKey(IReadOnlyDictionary<string, Scalar> key) =>
        key.ToDictionary(entry => entry.Key, entry => (JsonNode?)entry.Value.ToNode(), StringComparer.Ordinal);

    private SetNode SetOf(object? set, string what)
    {
        var definition = Handle.Definition;
        return StoreInternal.CompileSet(definition, set is Func<IReadOnlyDictionary<string, ColumnRef>, object?> function ? function(definition.Refs) : set, what);
    }

    private object? ConditionOf(object? where) =>
        where is Func<IReadOnlyDictionary<string, ColumnRef>, Condition> function ? function(Handle.Definition.Refs) : where;

    private void Write(string id, KeyWrite write)
    {
        if (byKey.TryGetValue(id, out var writes)) writes.Add(write);
        else byKey[id] = [write];
    }
}
