// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/entries.ts
//
// Table helpers for compose (slice 8 §1, Q8): a map by key (the first by plugin name wins, duplicates reported) and an ordered list; both
// keep each item's plugin, so a use site reports the contributor of an entry that throws. Mappings: keyedEntries and orderedEntries are
// RegistryTable.KeyedEntries and RegistryTable.OrderedEntries; TableItem is a record. An entry's keyed items are its key-value pairs, read
// in JavaScript's own-key order as Object.entries reads an object (array-index keys ascending, then the rest in their order), and the map
// keeps insertion order as a JavaScript Map does. orderOf is a number, compared as upstream's `orderOf(a) - orderOf(b)`, and the sort is
// stable, as Array.prototype.sort is.
using System.Globalization;

namespace PiSharp.Chord.Registries;

/// <summary>An item taken from a table, with the contributor it came from (upstream <c>TableItem</c>).</summary>
/// <typeparam name="I">The item type.</typeparam>
/// <param name="Value">The item.</param>
/// <param name="Plugin">The plugin of the entry it came from.</param>
/// <param name="Type">The object type of the entry it came from; null for a facet.</param>
public sealed record TableItem<I>(I Value, string Plugin, string? Type = null);

/// <summary>A registry's table (upstream <c>RegistryTable</c>, an <c>IReadOnlyList&lt;RegistryEntry&lt;C&gt;&gt;</c>: one entry per live
/// contributor, sorted by plugin name, then type, a facet's first) and its helpers for <c>compose</c>.</summary>
public static class RegistryTable
{
    /// <summary>
    /// A map of every entry's keyed items (<paramref name="entriesOf"/>, e.g. its routes), in table order: a key taken already is reported
    /// through <paramref name="report"/> (<c>compose</c>'s) naming both plugins, and the first by plugin name keeps it (upstream
    /// <c>keyedEntries</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, TableItem<I>> KeyedEntries<C, I>(
        IReadOnlyList<RegistryEntry<C>> table,
        Func<C, IEnumerable<KeyValuePair<string, I>>?> entriesOf,
        Action<RegistryConflict> report)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(entriesOf);
        ArgumentNullException.ThrowIfNull(report);
        var map = new OrderedDictionary<string, TableItem<I>>(StringComparer.Ordinal);
        foreach (var entry in table)
        {
            var items = entriesOf(entry.Value);
            if (items is null) continue;
            foreach (var (key, value) in InJavaScriptOrder(items))
            {
                if (map.TryGetValue(key, out var taken))
                {
                    report(new RegistryConflict($"\"{key}\" is contributed twice", [taken.Plugin, entry.Plugin]));
                    continue;
                }
                map.Add(key, new TableItem<I>(value, entry.Plugin, entry.Type));
            }
        }
        return map;
    }

    /// <summary>
    /// Every entry's items (<paramref name="itemsOf"/>) in one list: in table order, or sorted by <paramref name="orderOf"/> (a phase's
    /// index, for example), table order kept among equals (upstream <c>orderedEntries</c>).
    /// </summary>
    public static IReadOnlyList<TableItem<I>> OrderedEntries<C, I>(
        IReadOnlyList<RegistryEntry<C>> table,
        Func<C, IEnumerable<I>?> itemsOf,
        Func<I, double>? orderOf = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(itemsOf);
        var items = new List<TableItem<I>>();
        foreach (var entry in table)
        {
            foreach (var value in itemsOf(entry.Value) ?? []) items.Add(new TableItem<I>(value, entry.Plugin, entry.Type));
        }
        // OrderBy is stable: equal orders keep table order.
        return orderOf is null ? items : [.. items.OrderBy(item => orderOf(item.Value))];
    }

    /// <summary>Key-value pairs in JavaScript's own-key order, as <c>Object.entries</c> reads an object: array-index keys ascending, then the
    /// rest in their order.</summary>
    private static IEnumerable<KeyValuePair<string, I>> InJavaScriptOrder<I>(IEnumerable<KeyValuePair<string, I>> items)
    {
        var list = items.ToList();
        if (!list.Any(item => ArrayIndex(item.Key) is not null)) return list;
        return list.Where(item => ArrayIndex(item.Key) is not null).OrderBy(item => ArrayIndex(item.Key))
            .Concat(list.Where(item => ArrayIndex(item.Key) is null));
    }

    /// <summary>A key's array index: the canonical decimal form of an integer from 0 to 2^32 - 2; otherwise null.</summary>
    private static uint? ArrayIndex(string key) =>
        uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index != uint.MaxValue &&
        index.ToString(CultureInfo.InvariantCulture) == key ? index : null;
}
