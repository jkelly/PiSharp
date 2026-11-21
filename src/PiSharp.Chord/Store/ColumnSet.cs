// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/column-set.ts
//
// Column sets (slice 14 §5): Tables.DefineColumns(name, { Columns, Key, Indexes }) in a plugin's contract file names the scalar columns of
// one of its tables that other plugins' conditions and joins may read, the table's key, and the indexes others may match by. A table copies
// the set's Columns into its own (upstream spreads `...Set.columns`) and declares Exposes = Set. Upstream's ColumnSet<C, K> carries
// TypeScript types of the columns and key; the C# class is untyped, as TableDefinition is, and what the types refuse is refused when the
// set is declared or used. A declaration refusal is an ArgumentException with upstream's message.
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>What <see cref="Tables.DefineColumns"/> takes (upstream <c>ColumnSetOptions</c>).</summary>
public sealed class ColumnSetOptions
{
    /// <summary>The public columns: scalar ones (<c>Str()</c>, <c>Int()</c>, <c>Num()</c>, <c>Bool()</c>, <c>Opt(...)</c> of them,
    /// <see cref="Tables.Host"/>).</summary>
    public OrderedDictionary<string, Property> Columns { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The key of the table that exposes the set.</summary>
    public required IReadOnlyList<string> Key { get; init; }

    /// <summary>The indexes others may match by, each a leading prefix of one of the table's.</summary>
    public OrderedDictionary<string, IReadOnlyList<string>> Indexes { get; init; } = new(StringComparer.Ordinal);
}

public static partial class Tables
{
    /// <summary>Declares the columns of a table that other plugins' conditions and joins may read (upstream <c>defineColumns</c>).</summary>
    public static ColumnSet DefineColumns(string name, ColumnSetOptions options) => new(name, options);
}

/// <summary>A declared column set: its columns, key and indexes, checked once here. <see cref="Refs"/> are its column references, for
/// conditions over the set wherever it appears: a query's own table, a joined set, an <c>Exists</c> (upstream <c>ColumnSet</c>).</summary>
public sealed class ColumnSet : IMatchedSet
{
    private readonly OrderedDictionary<string, Property> columns;

    internal ColumnSet(string name, ColumnSetOptions options)
    {
        ContractsInternal.CheckName(name, "column set");
        ArgumentNullException.ThrowIfNull(options);
        columns = new OrderedDictionary<string, Property>(options.Columns ?? new OrderedDictionary<string, Property>(), StringComparer.Ordinal);
        var described = StoreInternal.DescribeColumns(columns, name);
        foreach (var column in described)
        {
            // Declaration policy: a json(...) cell has no scalar value a condition could compare.
            if (column.Type == ColumnType.Json) throw new ArgumentException($"{name}: column \"{column.Name}\": json(...) is not in a column set");
        }
        Name = name;
        ByName = described.ToDictionary(column => column.Name, StringComparer.Ordinal);
        ColumnNames = [.. described.Select(column => column.Name)];
        Key = StoreInternal.OrderColumns(ByName, options.Key, $"{name}: the key");
        if (Key.Count == 0) throw new ArgumentException($"{name}: the key has no column");
        var indexes = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var declared = options.Indexes ?? new OrderedDictionary<string, IReadOnlyList<string>>();
        foreach (var index in StoreInternal.JavaScriptOrder(declared.Keys))
            indexes[index] = StoreInternal.OrderColumns(ByName, declared[index], $"{name}: index \"{index}\"");
        Indexes = indexes;
        // One reference per column, tagged with the set: a condition names the side it means by it.
        Refs = described.ToDictionary(column => column.Name, column => new ColumnRef(column.Name, name), StringComparer.Ordinal);
    }

    /// <summary>The set's name.</summary>
    public string Name { get; }

    /// <summary>The column schemas as declared, in declaration order: what a table copies into its own columns.</summary>
    public IReadOnlyDictionary<string, Property> Columns => columns;

    /// <summary>The columns' names in declaration order.</summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>The columns by name.</summary>
    internal IReadOnlyDictionary<string, Column> ByName { get; }

    /// <summary>The key of the table that exposes the set.</summary>
    public IReadOnlyList<string> Key { get; }

    /// <summary>The indexes others may match by, by name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes { get; }

    /// <summary>One reference per column, tagged with the set's name.</summary>
    public IReadOnlyDictionary<string, ColumnRef> Refs { get; }

    IReadOnlyDictionary<string, Column> ISide.ByName => ByName;
}

/// <summary>The parts of a table or a column set a scope reads (upstream <c>Side</c>).</summary>
internal interface ISide
{
    /// <summary>Named in errors.</summary>
    string Name { get; }

    /// <summary>The columns by name.</summary>
    IReadOnlyDictionary<string, Column> ByName { get; }
}

/// <summary>A set as a match is checked against it: its name, columns, key and declared indexes (upstream <c>MatchedSet</c>).</summary>
internal interface IMatchedSet : ISide
{
    /// <summary>The key columns.</summary>
    IReadOnlyList<string> Key { get; }

    /// <summary>The declared indexes by name.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes { get; }
}

/// <summary>A matched set made of its parts (a join checks its match against the set's key alone).</summary>
internal sealed record MatchedSet(string Name, IReadOnlyDictionary<string, Column> ByName, IReadOnlyList<string> Key,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes) : IMatchedSet;

internal static partial class StoreInternal
{
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoIndexes = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Checks that the table <paramref name="name"/> can expose <paramref name="set"/> (declaration policy): every column of the
    /// set is a column of the table of the same type and nullability, the keys are equal, and each index of the set is a leading prefix of
    /// the key or of one of the table's indexes, which end with the key (upstream <c>checkExposes</c>).</summary>
    internal static void CheckExposes(string name, IReadOnlyDictionary<string, Column> byName, IReadOnlyList<string> key,
        IReadOnlyDictionary<string, IReadOnlyList<string>> indexes, ColumnSet set)
    {
        var what = $"{name}: exposes \"{set.Name}\"";
        foreach (var column in set.ColumnNames)
        {
            if (!byName.TryGetValue(column, out var own)) throw new ArgumentException($"{what}: the table has no column \"{column}\"");
            if (!SameColumn(own, set.ByName[column])) throw new ArgumentException($"{what}: column \"{column}\" is declared differently in the table");
        }
        if (!key.SequenceEqual(set.Key, StringComparer.Ordinal)) throw new ArgumentException($"{what}: the keys differ");
        List<IReadOnlyList<string>> paths = [key, .. indexes.Values.Select(columns => IndexPath(columns, key))];
        foreach (var (index, columns) in set.Indexes)
        {
            if (!paths.Any(path => columns.Count <= path.Count && columns.Select((column, at) => path[at] == column).All(same => same)))
                throw new ArgumentException($"{what}: index \"{index}\" is no leading prefix of the key or of an index of the table");
        }
    }

    /// <summary>Refuses a table exposing a set another table declared by the same object exposes (declaration policy: a set name is bound
    /// to one table), naming both (upstream <c>checkExposedOnce</c>).</summary>
    internal static void CheckExposedOnce(TableDefinition definition, IEnumerable<TableDefinition> declared, string label)
    {
        if (definition.Exposes?.Name is not { } set) return;
        foreach (var other in declared)
        {
            if (other.Exposes?.Name == set)
                throw new ArgumentException($"{label}: column set \"{set}\" is exposed by tables \"{other.Name}\" and \"{definition.Name}\"");
        }
    }

    /// <summary>Whether two columns are the same to a reader: the type, whether <c>Opt(...)</c>, a <c>host(...)</c> column's kind. A default
    /// is the writer's business (upstream <c>sameColumn</c>).</summary>
    internal static bool SameColumn(Column a, Column b)
    {
        var (x, y) = (RecordOf(a), RecordOf(b));
        return x.Type == y.Type && x.Optional == y.Optional && x.Host == y.Host;
    }

    /// <summary>The columns an index is stored by: its own, then the key columns not among them (upstream <c>indexPath</c>).</summary>
    internal static IReadOnlyList<string> IndexPath(IReadOnlyList<string> columns, IReadOnlyList<string> key) =>
        [.. columns, .. key.Where(column => !columns.Contains(column))];
}
