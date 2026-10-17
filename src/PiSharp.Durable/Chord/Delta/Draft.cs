// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/draft.ts and the overlay nodes of
// packages/chord/src/delta/tracker.ts. C# has no Proxy, so a draft is an explicit copy-on-write view (decision 0005): an object draft
// records its writes, deletions and re-additions over its base object; an array draft records per-entry overrides and, once elements
// are inserted or removed, upstream's piece table: base ranges in base order and runs of inserted entries, kept as a short list rather
// than upstream's treap. Inserts and removals never visit the base elements, so an append to a long array costs no walk over it. Once an
// element moves (reverse, sort, move), or the list grows past MaxPieces, the pieces flatten into an ordered list of entries that remember
// which base index each came from. Child drafts are created on demand, keep one identity per slot, follow their element through
// reindexing, and are detached (their writes ignored) when their slot is overwritten or removed. Untouched subtrees are never visited and
// keep their base references.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>
/// A mutable, change-scoped view of a JSON object or array (upstream's <c>Draft&lt;T&gt;</c>). Drafts are usable only while their
/// <see cref="Change"/> is open: after <see cref="Change.Prepare"/>, <see cref="Change.Abort"/> or another change's adoption every use
/// throws <see cref="InvalidOperationException"/>. Values placed into a draft are immutable <see cref="JsonValue"/>s, so a placement
/// needs no copy and placing one value at several paths is safe.
/// </summary>
public abstract class Draft
{
    private JsonValue? current;

    private protected Draft(ChangeContext context, Draft? parent, string? parentKey, ArrayEntry? parentEntry, bool placement)
    {
        Context = context; Parent = parent; ParentKey = parentKey; ParentEntry = parentEntry; Placement = placement;
    }

    internal ChangeContext Context { get; }
    internal Draft? Parent { get; }
    internal string? ParentKey { get; }
    internal ArrayEntry? ParentEntry { get; }
    /// <summary>Created over a value placed in this change: its edits fold into the placement's payload.</summary>
    internal bool Placement { get; }
    internal bool Dirty { get; private set; }
    internal bool SubtreeDirty { get; private set; }
    internal abstract JsonValue BaseValue { get; }

    /// <summary>Whether this is an object or an array draft.</summary>
    public abstract JsonValueKind Kind { get; }

    /// <summary>The number of properties or elements.</summary>
    public abstract int Count { get; }

    /// <summary>This draft as an object draft.</summary>
    public DraftObject AsObject() => this as DraftObject ?? throw new InvalidOperationException("The draft is an array, not an object.");

    /// <summary>This draft as an array draft.</summary>
    public DraftArray AsArray() => this as DraftArray ?? throw new InvalidOperationException("The draft is an object, not an array.");

    /// <summary>The draft's current content as an immutable value (unchanged subtrees keep their base references).</summary>
    public JsonValue ToValue()
    {
        Context.AssertReadable();
        return Current();
    }

    internal JsonValue Current() => current ??= Dirty || SubtreeDirty ? Materialize() : BaseValue;

    private protected abstract JsonValue Materialize();

    private protected void MarkDirty()
    {
        current = null;
        if (!Dirty)
        {
            Dirty = true;
            Context.DirtyNodes.Add(this);
        }
        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            parent.current = null;
            parent.SubtreeDirty = true;
        }
    }

    private protected static Draft Create(ChangeContext context, JsonValue value, Draft? parent, string? key, ArrayEntry? entry, bool placement) =>
        value switch
        {
            JsonObjectValue obj => new DraftObject(context, obj, parent, key, entry, placement),
            JsonArrayValue array => new DraftArray(context, array, parent, key, entry, placement),
            _ => throw new InvalidOperationException("Only objects and arrays have drafts.")
        };

    internal static Draft CreateRoot(ChangeContext context, JsonValue value) => Create(context, value, null, null, null, false);
}

/// <summary>An object draft. Key order follows JavaScript: integer-index keys ascending, then insertion order (a deleted and re-added
/// key moves to the end).</summary>
public sealed class DraftObject : Draft
{
    private readonly JsonObjectValue baseValue;
    private Dictionary<string, Draft>? children;

    internal DraftObject(ChangeContext context, JsonObjectValue value, Draft? parent, string? key, ArrayEntry? entry, bool placement)
        : base(context, parent, key, entry, placement)
    {
        baseValue = value;
    }

    internal override JsonValue BaseValue => baseValue;
    internal JsonObjectValue Base => baseValue;
    /// <summary>Written keys in first-write order (emission order).</summary>
    internal OrderedDictionary<string, JsonValue>? Writes { get; private set; }
    /// <summary>Deleted keys in deletion order.</summary>
    internal OrderedDictionary<string, bool>? Deletes { get; private set; }
    /// <summary>Base keys deleted and then written again: replay must delete before setting so the key moves to the end.</summary>
    internal OrderedDictionary<string, bool>? Readded { get; private set; }

    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Object;

    /// <inheritdoc/>
    public override int Count
    {
        get
        {
            Context.AssertReadable();
            var count = baseValue.Count;
            if (Deletes is not null) foreach (var key in Deletes.Keys) if (baseValue.ContainsKey(key)) count--;
            if (Writes is not null) foreach (var key in Writes.Keys) if (!baseValue.ContainsKey(key)) count++;
            return count;
        }
    }

    /// <summary>The keys in JavaScript enumeration order.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            Context.AssertReadable();
            return KeysCore();
        }
    }

    /// <summary>Whether the key is present.</summary>
    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Context.AssertReadable();
        return Has(key);
    }

    /// <summary>The key's current value (a container is returned as its current immutable content), or null when absent.</summary>
    public JsonValue? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Context.AssertReadable();
        if (!Has(key)) return null;
        return children is not null && children.TryGetValue(key, out var child) ? child.Current() : Raw(key);
    }

    /// <summary>Gets a present key's current value, or sets it.</summary>
    public JsonValue this[string key]
    {
        get => Get(key) ?? throw new KeyNotFoundException($"The draft has no property '{key}'.");
        set => Set(key, value);
    }

    /// <summary>The draft of a container property, created once per slot and reused while the slot is not overwritten.</summary>
    public Draft GetDraft(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Context.AssertReadable();
        if (children is not null && children.TryGetValue(key, out var existing)) return existing;
        if (!Has(key)) throw new KeyNotFoundException($"The draft has no property '{key}'.");
        var value = Raw(key);
        if (value is not (JsonObjectValue or JsonArrayValue)) throw new InvalidOperationException($"Property '{key}' is not an object or array.");
        var child = Create(Context, value, this, key, null, Writes?.ContainsKey(key) == true);
        (children ??= new Dictionary<string, Draft>(StringComparer.Ordinal))[key] = child;
        return child;
    }

    /// <summary>The draft of an object property.</summary>
    public DraftObject GetObject(string key) => GetDraft(key).AsObject();

    /// <summary>The draft of an array property.</summary>
    public DraftArray GetArray(string key) => GetDraft(key).AsArray();

    /// <summary>Sets a property. Rewriting a scalar with an equal scalar changes nothing; any other write detaches the slot's old draft.</summary>
    public void Set(string key, JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Context.AssertWritable();
        var wasDeleted = Deletes?.ContainsKey(key) == true;
        if (!wasDeleted && !JsonIdentity.IsContainer(value) && TryRaw(key, out var existing) && JsonIdentity.Strict(existing, value)) return;
        Writes ??= new OrderedDictionary<string, JsonValue>(StringComparer.Ordinal);
        Writes[key] = value;
        if (wasDeleted && baseValue.ContainsKey(key)) (Readded ??= new OrderedDictionary<string, bool>(StringComparer.Ordinal)).TryAdd(key, true);
        Deletes?.Remove(key);
        children?.Remove(key);
        MarkDirty();
    }

    /// <summary>Deletes a property; returns whether it was present.</summary>
    public bool Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Context.AssertWritable();
        if (!Has(key)) return false;
        Writes?.Remove(key);
        Readded?.Remove(key);
        (Deletes ??= new OrderedDictionary<string, bool>(StringComparer.Ordinal)).TryAdd(key, true);
        children?.Remove(key);
        MarkDirty();
        return true;
    }

    /// <summary>Appends to a string property (emitted as <c>["a", path, text]</c>).</summary>
    public void AppendString(string key, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Get(key) is not JsonStringValue existing) throw new InvalidOperationException($"Property '{key}' is not a string.");
        Set(key, existing.Value + text);
    }

    internal bool Has(string key) => Deletes?.ContainsKey(key) != true && (Writes?.ContainsKey(key) == true || baseValue.ContainsKey(key));

    internal JsonValue Raw(string key) => Writes is not null && Writes.TryGetValue(key, out var written) ? written : baseValue[key];

    private bool TryRaw(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonValue? value)
    {
        if (Writes is not null && Writes.TryGetValue(key, out value)) return true;
        return baseValue.TryGetValue(key, out value);
    }

    internal bool IsChild(string key, Draft child) => children is not null && children.TryGetValue(key, out var existing) && ReferenceEquals(existing, child);

    /// <summary>The slot's current value with its draft's edits.</summary>
    internal JsonValue SlotCurrent(string key, JsonValue raw) => children is not null && children.TryGetValue(key, out var child) ? child.Current() : raw;

    internal bool HasReservedMutation()
    {
        if (Writes is not null) foreach (var key in Writes.Keys) if (DeltaValidation.ReservedSegments.Contains(key)) return true;
        if (Deletes is not null) foreach (var key in Deletes.Keys) if (DeltaValidation.ReservedSegments.Contains(key)) return true;
        return false;
    }

    private IReadOnlyList<string> KeysCore()
    {
        var existingOnly = Deletes is null or { Count: 0 } && Readded is null or { Count: 0 };
        if (existingOnly && Writes is not null)
            foreach (var key in Writes.Keys) if (!baseValue.ContainsKey(key)) { existingOnly = false; break; }
        if (existingOnly) return baseValue.Keys;
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in baseValue.Keys)
            if (Deletes?.ContainsKey(key) != true && Readded?.ContainsKey(key) != true && seen.Add(key)) keys.Add(key);
        if (Writes is not null) foreach (var key in Writes.Keys) if (seen.Add(key)) keys.Add(key);
        var indices = keys.Where(JsonObjectValue.IsArrayIndex).OrderBy(key => uint.Parse(key, CultureInfo.InvariantCulture));
        return [.. indices, .. keys.Where(key => !JsonObjectValue.IsArrayIndex(key))];
    }

    // The same result replay produces: re-added keys move to the end, writes keep an existing key's position, a write deeply equal to
    // the base keeps the base reference, and a container that ends up slot-for-slot identical is the base itself.
    private protected override JsonValue Materialize()
    {
        JsonObjectValue.Builder? builder = null;
        JsonObjectValue.Builder Edit() => builder ??= baseValue.ToBuilder();
        if (Readded is not null) foreach (var key in Readded.Keys) Edit().Remove(key);
        if (Writes is not null)
        {
            foreach (var (key, raw) in Writes)
            {
                var after = SlotCurrent(key, raw);
                if (Readded?.ContainsKey(key) != true && baseValue.TryGetValue(key, out var before) && JsonIdentity.Same(before, after)) continue;
                Edit()[key] = after;
            }
        }
        if (Deletes is not null) foreach (var key in Deletes.Keys) if (baseValue.ContainsKey(key)) Edit().Remove(key);
        if (children is not null)
        {
            foreach (var (key, child) in children)
            {
                if (Writes?.ContainsKey(key) == true) continue;
                var value = child.Current();
                if (!ReferenceEquals(value, baseValue[key])) Edit()[key] = value;
            }
        }
        return builder?.ToImmutable() ?? baseValue;
    }
}

/// <summary>
/// An array draft. Arrays stay dense: a write may replace an element or append exactly one past the end, and growing the length inserts
/// explicit nulls. Element drafts follow their element through insertions, removals and reordering.
/// </summary>
public sealed class DraftArray : Draft
{
    // Past this many pieces a splice flattens the draft into entries, bounding the linear piece scans (upstream keeps a treap instead).
    private const int MaxPieces = 64;

    private readonly JsonArrayValue baseValue;
    private Dictionary<int, ArrayEntry>? touched; // entries by base index while no element has moved
    private List<ArrayPiece>? pieces; // the piece table once elements were inserted or removed, until an element moves
    private int pieceLength;
    private Dictionary<ArrayEntry, int>? positions;
    private (int[] Starts, int[] Logical, int[] Lengths)? baseRanges; // piece mode: each base piece's base start, position and length

    internal DraftArray(ChangeContext context, JsonArrayValue value, Draft? parent, string? key, ArrayEntry? entry, bool placement)
        : base(context, parent, key, entry, placement)
    {
        baseValue = value;
    }

    internal override JsonValue BaseValue => baseValue;
    internal JsonArrayValue Base => baseValue;
    /// <summary>Every entry in order, once an element moved (or the piece table grew past <see cref="MaxPieces"/>).</summary>
    internal List<ArrayEntry>? Entries { get; private set; }
    /// <summary>The piece table while elements were only inserted or removed: base ranges keep their base order.</summary>
    internal IReadOnlyList<ArrayPiece>? Pieces => pieces;
    /// <summary>Whether entries were inserted, removed or reordered.</summary>
    internal bool Structural { get; private set; }
    /// <summary>Base entries whose value was replaced, by base index in first-override order.</summary>
    internal OrderedDictionary<int, ArrayEntry>? Overrides { get; private set; }

    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Array;

    /// <inheritdoc/>
    public override int Count
    {
        get
        {
            Context.AssertReadable();
            return Length;
        }
    }

    private int Length => Entries?.Count ?? (pieces is not null ? pieceLength : baseValue.Count);

    /// <summary>The element's current value (a container is returned as its current immutable content).</summary>
    public JsonValue Get(int index)
    {
        Context.AssertReadable();
        CheckIndex(index);
        return ValueAt(index);
    }

    /// <summary>Gets an element's current value, or sets it.</summary>
    public JsonValue this[int index]
    {
        get => Get(index);
        set => Set(index, value);
    }

    /// <summary>The draft of a container element; it follows the element through reindexing.</summary>
    public Draft GetDraft(int index)
    {
        Context.AssertReadable();
        CheckIndex(index);
        var entry = EntryAt(index);
        if (entry.Child is { } existing) return existing;
        if (entry.Value is not (JsonObjectValue or JsonArrayValue)) throw new InvalidOperationException($"Element {index} is not an object or array.");
        var child = Create(Context, entry.Value, this, null, entry, entry.Origin < 0 || entry.Overridden);
        entry.Child = child;
        return child;
    }

    /// <summary>The draft of an object element.</summary>
    public DraftObject GetObject(int index) => GetDraft(index).AsObject();

    /// <summary>The draft of an array element.</summary>
    public DraftArray GetArray(int index) => GetDraft(index).AsArray();

    /// <summary>Replaces an element, or appends when <paramref name="index"/> equals <see cref="Count"/>.</summary>
    public void Set(int index, JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Context.AssertWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var length = Length;
        if (index > length) throw new InvalidOperationException("Overlay arrays cannot contain holes");
        if (index == length)
        {
            ReplaceRange(length, 0, [value]);
            return;
        }
        var entry = EntryAt(index);
        if (!JsonIdentity.IsContainer(value) && JsonIdentity.Strict(entry.Value, value)) return;
        if (entry.Origin >= 0)
        {
            var original = baseValue[entry.Origin];
            if (!JsonIdentity.IsContainer(value) && JsonIdentity.Strict(original, value))
            {
                entry.Value = original;
                entry.Overridden = false;
                Overrides?.Remove(entry.Origin);
            }
            else
            {
                entry.Value = value;
                if (!entry.Overridden)
                {
                    entry.Overridden = true;
                    (Overrides ??= new OrderedDictionary<int, ArrayEntry>()).Add(entry.Origin, entry);
                }
            }
        }
        else entry.Value = value;
        entry.Child = null;
        MarkDirty();
    }

    /// <summary>Appends an element.</summary>
    public void Add(JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Context.AssertWritable();
        ReplaceRange(Length, 0, [value]);
    }

    /// <summary>Appends elements.</summary>
    public void AddRange(IEnumerable<JsonValue> values) => InsertRange(Count, values);

    /// <summary>Inserts an element.</summary>
    public void Insert(int index, JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Context.AssertWritable();
        CheckPosition(index);
        ReplaceRange(index, 0, [value]);
    }

    /// <summary>Inserts elements (all are checked before the draft changes).</summary>
    public void InsertRange(int index, IEnumerable<JsonValue> values)
    {
        var items = Placements(values);
        Context.AssertWritable();
        CheckPosition(index);
        ReplaceRange(index, 0, items);
    }

    /// <summary>Removes an element and returns its current value.</summary>
    public JsonValue RemoveAt(int index)
    {
        Context.AssertWritable();
        CheckIndex(index);
        var removed = Get(index);
        ReplaceRange(index, 1, []);
        return removed;
    }

    /// <summary>Removes every element from <paramref name="start"/> on and returns them.</summary>
    public ImmutableArray<JsonValue> Splice(int start)
    {
        Context.AssertWritable();
        CheckPosition(start);
        return Splice(start, Length - start);
    }

    /// <summary>JavaScript <c>splice</c>: removes up to <paramref name="deleteCount"/> elements at <paramref name="start"/> (clamped to the
    /// end), inserts <paramref name="items"/> there, and returns the removed values.</summary>
    public ImmutableArray<JsonValue> Splice(int start, int deleteCount, params IEnumerable<JsonValue> items)
    {
        var inserted = Placements(items);
        Context.AssertWritable();
        CheckPosition(start);
        ArgumentOutOfRangeException.ThrowIfNegative(deleteCount);
        var remove = Math.Min(deleteCount, Length - start);
        var removed = ImmutableArray.CreateBuilder<JsonValue>(remove);
        for (var offset = 0; offset < remove; offset++) removed.Add(Get(start + offset));
        ReplaceRange(start, remove, inserted);
        return removed.MoveToImmutable();
    }

    /// <summary>Removes every element.</summary>
    public void Clear() => SetLength(0);

    /// <summary>JavaScript <c>length = n</c>: shrinking removes the tail, growing appends explicit nulls.</summary>
    public void SetLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Context.AssertWritable();
        var current = Length;
        if (length < current) ReplaceRange(length, current - length, []);
        else if (length > current) ReplaceRange(current, 0, [.. Enumerable.Repeat<JsonValue>(JsonValue.Null, length - current)]);
    }

    /// <summary>Reverses the elements in place.</summary>
    public void Reverse()
    {
        Context.AssertWritable();
        if (Length < 2) return;
        EnsureEntries();
        Entries!.Reverse();
        Reordered();
    }

    /// <summary>Stable sort by <paramref name="comparison"/> over the elements' current values, or JavaScript's default order (by
    /// <c>String(value)</c> in UTF-16 code-unit order). The comparison must not use the draft.</summary>
    public void Sort(Comparison<JsonValue>? comparison = null)
    {
        Context.AssertWritable();
        if (Length < 2) return;
        EnsureEntries();
        var entries = Entries!;
        var values = entries.Select(entry => (Entry: entry, Value: SlotCurrent(entry))).ToList();
        var sorted = comparison is null
            ? values.OrderBy(item => JsString(item.Value), StringComparer.Ordinal).ToList()
            : values.OrderBy(item => item.Value, Comparer<JsonValue>.Create(comparison)).ToList();
        var unchanged = true;
        for (var index = 0; index < sorted.Count && unchanged; index++) unchanged = ReferenceEquals(sorted[index].Entry, entries[index]);
        if (unchanged) return;
        entries.Clear();
        entries.AddRange(sorted.Select(item => item.Entry));
        Reordered();
    }

    /// <summary>Moves the element at <paramref name="from"/> so that it ends up at <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        Context.AssertWritable();
        CheckIndex(from);
        CheckIndex(to);
        if (from == to) return;
        EnsureEntries();
        var entry = Entries![from];
        Entries.RemoveAt(from);
        Entries.Insert(to, entry);
        Reordered();
    }

    /// <summary>Appends to a string element (emitted as <c>["a", path, text]</c>).</summary>
    public void AppendString(int index, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Get(index) is not JsonStringValue existing) throw new InvalidOperationException($"Element {index} is not a string.");
        Set(index, existing.Value + text);
    }

    /// <summary>JavaScript <c>String(value)</c>, the key of the default sort.</summary>
    internal static string JsString(JsonValue value)
    {
        switch (value)
        {
            case JsonStringValue text: return text.Value;
            case JsonObjectValue: return "[object Object]";
            case JsonArrayValue array:
            {
                var builder = new StringBuilder();
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    if (array[index] is not JsonNullValue) builder.Append(JsString(array[index]));
                }
                return builder.ToString();
            }
            default: return value.ToJson();
        }
    }

    internal JsonValue SlotCurrent(ArrayEntry entry) => entry.Child?.Current() ?? entry.Value;

    /// <summary>The entry's position in the draft (prepare-time; base index while nothing moved). The entry must not be removed.</summary>
    internal int IndexOf(ArrayEntry entry)
    {
        if (!Structural) return entry.Origin;
        if (positions is null)
        {
            if (Entries is not null)
            {
                positions = new Dictionary<ArrayEntry, int>(Entries.Count);
                for (var index = 0; index < Entries.Count; index++) positions[Entries[index]] = index;
            }
            else
            {
                // Piece mode: inserted entries by position and base pieces as ranges, so no base element is visited.
                positions = [];
                var (starts, logical, lengths) = (new List<int>(), new List<int>(), new List<int>());
                var at = 0;
                foreach (var piece in pieces!)
                {
                    if (piece.Inserted is { } inserted)
                        for (var offset = 0; offset < inserted.Count; offset++) positions[inserted[offset]] = at + offset;
                    else
                    {
                        starts.Add(piece.Start);
                        logical.Add(at);
                        lengths.Add(piece.Length);
                    }
                    at += piece.Count;
                }
                baseRanges = ([.. starts], [.. logical], [.. lengths]);
            }
        }
        if (entry.Origin < 0 || Entries is not null) return positions[entry];
        var ranges = baseRanges!.Value;
        var found = Array.BinarySearch(ranges.Starts, entry.Origin);
        if (found < 0) found = ~found - 1;
        if (found < 0 || entry.Origin >= ranges.Starts[found] + ranges.Lengths[found])
            throw new InvalidOperationException("A removed array entry has no position.");
        return ranges.Logical[found] + entry.Origin - ranges.Starts[found];
    }

    internal ImmutableArray<JsonValue> CurrentRange(int start, int length)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>(length);
        for (var index = start; index < start + length; index++) items.Add(ValueAt(index));
        return items.MoveToImmutable();
    }

    private JsonValue ValueAt(int index)
    {
        var entry = Find(index, out var baseIndex);
        return entry is null ? baseValue[baseIndex] : SlotCurrent(entry);
    }

    // The entry at a position, or null for a base element without one; baseIndex is then that element's base index.
    private ArrayEntry? Find(int index, out int baseIndex)
    {
        baseIndex = -1;
        if (Entries is not null) return Entries[index];
        if (pieces is not null)
        {
            var (piece, offset) = PieceAt(index);
            if (piece.Inserted is { } inserted) return inserted[offset];
            index = piece.Start + offset;
        }
        baseIndex = index;
        return touched is not null && touched.TryGetValue(index, out var found) ? found : null;
    }

    private (ArrayPiece Piece, int Offset) PieceAt(int index)
    {
        foreach (var piece in pieces!)
        {
            if (index < piece.Count) return (piece, index);
            index -= piece.Count;
        }
        throw new InvalidOperationException("Index is outside the array.");
    }

    private ArrayEntry EntryAt(int index)
    {
        if (Find(index, out var baseIndex) is { } entry) return entry;
        touched ??= new Dictionary<int, ArrayEntry>();
        touched[baseIndex] = entry = new ArrayEntry(baseIndex, baseValue[baseIndex]);
        return entry;
    }

    private void EnsureEntries()
    {
        if (Entries is not null) return;
        var entries = new List<ArrayEntry>(Length);
        if (pieces is null) AddBaseEntries(entries, 0, baseValue.Count);
        else
        {
            foreach (var piece in pieces)
            {
                if (piece.Inserted is { } inserted) entries.AddRange(inserted);
                else AddBaseEntries(entries, piece.Start, piece.Length);
            }
        }
        Entries = entries;
        touched = null;
        pieces = null;
        pieceLength = 0;
        positions = null;
        baseRanges = null;
    }

    private void AddBaseEntries(List<ArrayEntry> entries, int start, int count)
    {
        for (var index = start; index < start + count; index++)
            entries.Add(touched is not null && touched.TryGetValue(index, out var entry) ? entry : new ArrayEntry(index, baseValue[index]));
    }

    private void ReplaceRange(int index, int remove, IReadOnlyList<JsonValue> inserted)
    {
        if (remove == 0 && inserted.Count == 0) return;
        if (Entries is null && (pieces?.Count ?? 0) < MaxPieces) SplicePieces(index, remove, inserted);
        else
        {
            EnsureEntries();
            for (var offset = 0; offset < remove; offset++) Entries![index + offset].Removed = true;
            Entries!.RemoveRange(index, remove);
            Entries.InsertRange(index, inserted.Select(value => new ArrayEntry(-1, value)));
        }
        Reordered();
    }

    // replacePieceRange: cut the pieces at both ends of the removed range, mark what it covered removed, and put the inserted entries
    // there, joined to an adjacent run of inserted entries (an append extends the last run).
    private void SplicePieces(int index, int remove, IReadOnlyList<JsonValue> values)
    {
        if (pieces is null)
        {
            pieces = baseValue.Count == 0 ? [] : [ArrayPiece.Base(0, baseValue.Count)];
            pieceLength = baseValue.Count;
        }
        var first = Cut(index);
        var end = remove == 0 ? first : Cut(index + remove);
        for (var at = first; at < end; at++) MarkRemoved(pieces[at]);
        pieces.RemoveRange(first, end - first);
        pieceLength -= remove;
        if (values.Count == 0) return;
        var entries = new ArrayEntry[values.Count];
        for (var offset = 0; offset < entries.Length; offset++) entries[offset] = new ArrayEntry(-1, values[offset]);
        if (first > 0 && pieces[first - 1].Inserted is { } left) left.AddRange(entries);
        else if (first < pieces.Count && pieces[first].Inserted is { } right) right.InsertRange(0, entries);
        else pieces.Insert(first, ArrayPiece.Insert([.. entries]));
        pieceLength += entries.Length;
    }

    // The index of the piece that starts at position, splitting the piece that spans it.
    private int Cut(int position)
    {
        var list = pieces!;
        for (var at = 0; at < list.Count; at++)
        {
            if (position == 0) return at;
            var count = list[at].Count;
            if (position < count)
            {
                list.Insert(at + 1, list[at].SplitAt(position));
                return at + 1;
            }
            position -= count;
        }
        return list.Count;
    }

    private void MarkRemoved(ArrayPiece piece)
    {
        if (piece.Inserted is { } inserted)
        {
            foreach (var entry in inserted) entry.Removed = true;
            return;
        }
        if (touched is null) return;
        if (touched.Count <= piece.Length)
        {
            foreach (var (origin, entry) in touched)
                if (origin >= piece.Start && origin < piece.Start + piece.Length) entry.Removed = true;
        }
        else
        {
            for (var origin = piece.Start; origin < piece.Start + piece.Length; origin++)
                if (touched.TryGetValue(origin, out var entry)) entry.Removed = true;
        }
    }

    private void Reordered()
    {
        Structural = true;
        positions = null;
        baseRanges = null;
        MarkDirty();
    }

    private static JsonValue[] Placements(IEnumerable<JsonValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.ToArray();
        foreach (var item in items) ArgumentNullException.ThrowIfNull(item, nameof(values));
        return items;
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index), index, "Index is outside the array.");
    }

    private void CheckPosition(int index)
    {
        if ((uint)index > (uint)Length) throw new ArgumentOutOfRangeException(nameof(index), index, "Position is outside the array.");
    }

    // Replay's result: an override deeply equal to its base element keeps the base reference, and an array whose entries are the base
    // entries in base order with base references is the base itself.
    private protected override JsonValue Materialize()
    {
        if (pieces is not null) return MaterializePieces();
        if (Entries is null)
        {
            if (touched is null) return baseValue;
            ImmutableArray<JsonValue>.Builder? builder = null;
            foreach (var (index, entry) in touched)
            {
                var value = Slot(entry);
                if (ReferenceEquals(value, baseValue[index])) continue;
                builder ??= baseValue.Items.ToBuilder();
                builder[index] = value;
            }
            return builder is null ? baseValue : new JsonArrayValue(builder.ToImmutable());
        }
        var changed = Entries.Count != baseValue.Count;
        var items = ImmutableArray.CreateBuilder<JsonValue>(Entries.Count);
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            var value = Slot(entry);
            items.Add(value);
            if (!changed && (entry.Origin != index || !ReferenceEquals(value, baseValue[index]))) changed = true;
        }
        return changed ? new JsonArrayValue(items.MoveToImmutable()) : baseValue;
    }

    // The pieces in one copy: base ranges as spans of the base, inserted runs, then the overridden base entries still present.
    private JsonValue MaterializePieces()
    {
        var builder = ImmutableArray.CreateBuilder<JsonValue>(pieceLength);
        var changed = pieceLength != baseValue.Count;
        foreach (var piece in pieces!)
        {
            if (piece.Inserted is { } inserted)
            {
                changed = true;
                foreach (var entry in inserted) builder.Add(Slot(entry));
                continue;
            }
            if (piece.Start != builder.Count) changed = true;
            builder.AddRange(baseValue.Items.AsSpan(piece.Start, piece.Length));
        }
        if (touched is not null)
        {
            foreach (var (origin, entry) in touched)
            {
                if (entry.Removed) continue;
                var value = Slot(entry);
                if (ReferenceEquals(value, baseValue[origin])) continue;
                builder[IndexOf(entry)] = value;
                changed = true;
            }
        }
        return changed ? new JsonArrayValue(builder.MoveToImmutable()) : baseValue;
    }

    private JsonValue Slot(ArrayEntry entry)
    {
        var value = SlotCurrent(entry);
        if (entry.Origin >= 0 && entry.Overridden && JsonIdentity.Same(baseValue[entry.Origin], value)) return baseValue[entry.Origin];
        return value;
    }
}

/// <summary>
/// One piece of an array draft's piece table (upstream <c>BasePiece</c>/<c>InsertPiece</c>, always step 1 here: a piece that would run
/// backwards flattens the draft into entries): a range of base elements in base order, or a run of inserted entries.
/// </summary>
internal sealed class ArrayPiece
{
    private ArrayPiece(int start, int length, List<ArrayEntry>? inserted)
    {
        Start = start; Length = length; Inserted = inserted;
    }

    /// <summary>A base piece's first base index.</summary>
    public int Start { get; }
    /// <summary>A base piece's element count.</summary>
    public int Length { get; private set; }
    /// <summary>An insert piece's entries; null for a base piece.</summary>
    public List<ArrayEntry>? Inserted { get; }
    /// <summary>The piece's element count.</summary>
    public int Count => Inserted?.Count ?? Length;

    public static ArrayPiece Base(int start, int length) => new(start, length, null);

    public static ArrayPiece Insert(List<ArrayEntry> entries) => new(0, 0, entries);

    /// <summary>Keeps the first <paramref name="offset"/> elements and returns the rest as a new piece.</summary>
    public ArrayPiece SplitAt(int offset)
    {
        if (Inserted is { } inserted)
        {
            var rest = inserted.GetRange(offset, inserted.Count - offset);
            inserted.RemoveRange(offset, inserted.Count - offset);
            return Insert(rest);
        }
        var tail = Base(Start + offset, Length - offset);
        Length = offset;
        return tail;
    }
}

/// <summary>One array element: where it came from (a base index, or -1 when inserted), its current value and its draft.</summary>
internal sealed class ArrayEntry(int origin, JsonValue value)
{
    public int Origin { get; } = origin;
    public JsonValue Value { get; set; } = value;
    public bool Overridden { get; set; }
    public bool Removed { get; set; }
    public Draft? Child { get; set; }
}

/// <summary>JavaScript identity on immutable values: containers by reference, scalars by value (<c>===</c>).</summary>
internal static class JsonIdentity
{
    public static readonly IEqualityComparer<JsonValue> Comparer = EqualityComparer<JsonValue>.Create(
        (left, right) => left is not null && right is not null && Strict(left, right),
        value => value switch
        {
            JsonBooleanValue boolean => boolean.Value ? 1 : 2,
            JsonNumberValue number => number.Value == 0 ? 0 : number.Value.GetHashCode(),
            JsonStringValue text => string.GetHashCode(text.Value, StringComparison.Ordinal),
            JsonNullValue => 3,
            _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)
        });

    public static bool IsContainer(JsonValue? value) => value is JsonObjectValue or JsonArrayValue;

    /// <summary><c>left === right</c>.</summary>
    public static bool Strict(JsonValue left, JsonValue right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left switch
        {
            JsonNullValue => right is JsonNullValue,
            JsonBooleanValue boolean => right is JsonBooleanValue other && boolean.Value == other.Value,
            JsonNumberValue number => right is JsonNumberValue other && number.Value == other.Value,
            JsonStringValue text => right is JsonStringValue other && string.Equals(text.Value, other.Value, StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>Identical, or deeply equal containers (upstream's <c>sameValue</c>).</summary>
    public static bool Same(JsonValue left, JsonValue right) =>
        Strict(left, right) || (IsContainer(left) && IsContainer(right) && JsonValue.DeepEquals(left, right));
}
