// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/store/host-column.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

public static partial class Tables
{
    /// <summary>A required column containing addresses of <paramref name="kind"/>. The store keeps their canonical keys.</summary>
    public static JsonObject Host(HostKind kind) => new() { ["host"] = JsonValue.Create(new HostColumn(kind)) };
}

/// <summary>A host address column's declared kind and conversions at the storage boundary.</summary>
internal sealed class HostColumn
{
    internal HostColumn(HostKind kind)
    {
        ContractsInternal.KindParams(kind);
        Kind = kind;
    }

    internal HostKind Kind { get; }

    internal string Key(object? value, string what)
    {
        if (value is JsonValue holder && holder.TryGetValue<Address>(out var address)) value = address;
        var key = ContractsInternal.KeyOfAddress(Kind, value);
        return key is null
            ? throw DataInternal.Invalid("args", $"{what}: expected an address of {Kind.Kind}")
            : StoreInternal.StoredText(key, what);
    }

    internal JsonObject Address(string key) => ContractsInternal.AddressOfKey(Kind, key).ToJson();
}

internal static partial class StoreInternal
{
    internal const string HostIndex = "host";

    /// <summary>The recorded host column of one kind. Malformed multiple-host metadata is rejected at import.</summary>
    internal static string? HostColumn(TableRecord record, string kind) => HostColumn(record.Columns, kind);

    internal static string? HostColumn(IReadOnlyDictionary<string, ColumnRecord> columns, string kind) =>
        columns.FirstOrDefault(each => each.Value.Host == kind).Key;

    internal static Column? HostColumnOf(IReadOnlyList<Column> columns, IReadOnlyList<string> key,
        OrderedDictionary<string, IReadOnlyList<string>> indexes, string table)
    {
        var hosts = columns.Where(column => column.Host is not null).ToList();
        if (hosts.Count > 1) throw new ArgumentException($"{table}: more than one host(...) column");
        var column = hosts.FirstOrDefault();
        var needsIndex = column is not null && key[0] != column.Name;
        if (indexes.TryGetValue(HostIndex, out var declared) &&
            (!needsIndex || declared.Count != 1 || declared[0] != column!.Name))
            throw new ArgumentException($"{table}: index \"{HostIndex}\" is the host(...) column's own");
        if (needsIndex) indexes[HostIndex] = new[] { column!.Name };
        return column;
    }
}
