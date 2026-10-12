// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/storage/scan.ts.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable.Storage;

/// <summary>Where a built-in storage scan starts: its order and the last ID a previous page returned.</summary>
/// <param name="Order">The scan order.</param>
/// <param name="After">The last returned ID, or null for the first page.</param>
public readonly record struct ScanStart(ScanOrder Order, long? After);

/// <summary>Cursor handling shared by the built-in backends.</summary>
public static class StorageScan
{
    /// <summary>
    /// Resolves a scan's order and position. A cursor continues in the order it was created with, whether the query repeats that order or
    /// omits it; a different order throws. A cursor without an order, written before scans had one, continues in the scan's default order.
    /// </summary>
    public static ScanStart Start(ScanOrder? requested, JsonObjectValue? cursor, ScanOrder fallback)
    {
        if (requested is { } order0 && !Enum.IsDefined(order0)) throw new ArgumentException($"Invalid scan order: {(int)order0}");
        if (cursor is null) return new(requested ?? fallback, null);
        if (!cursor.TryGetValue("after", out var afterValue) || afterValue is not JsonNumberValue { Value: var after } ||
            Math.Floor(after) != after || Math.Abs(after) > Ids.MaxSafeInteger)
            throw new ArgumentException("Invalid storage cursor");
        ScanOrder? stored = null;
        if (cursor.TryGetValue("order", out var storedValue))
        {
            if (storedValue is not JsonStringValue { Value: var text } || !WireName.TryParse<ScanOrder>(text, out var parsed))
                throw new ArgumentException("Invalid storage cursor");
            stored = parsed;
        }
        var order = stored ?? fallback;
        if (requested is { } asked && asked != order)
            throw new ArgumentException($"The cursor continues a {WireName.Of(order)} scan; the query asks for {WireName.Of(asked)}");
        return new(order, (long)after);
    }

    /// <summary>The continuation of a scan whose last returned item has <paramref name="id"/>: <c>{after, order}</c>.</summary>
    public static JsonObjectValue NextCursor(long id, ScanOrder order) =>
        new RecordJson.Writer().Number("after", id).Add("order", WireName.Of(order)).Build();
}
