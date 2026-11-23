// Compact metadata + byte-offset index and bounded byte-budgeted LRU cache for JsonlStorage.
// Replaces an unbounded in-memory MemoryStorage mirror so long-lived sessions keep only lightweight ID/address/status
// indexes and file byte offsets in RAM, reading full entry, task, submission and document JSON payloads lazily from disk.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Storage.Jsonl;

internal readonly record struct EntryLocation(Seq CommitSeq, long LineOffset, int LineLength, int WriteIndex);

internal readonly record struct TaskLocation(
    ConversationId ConversationId,
    string Kind,
    DurableTaskStatus Status,
    bool AbortRequested,
    bool Background,
    bool InMain,
    long Offset,
    int Length,
    int WriteIndex);

internal readonly record struct SubmissionLocation(
    ConversationId ConversationId,
    SubmissionStatus Status,
    string? RequestId,
    long LineOffset,
    int LineLength,
    int WriteIndex);

internal readonly record struct RevisionLocation(Seq Seq, int Version, bool IsBase, long Offset, int Length);

internal sealed class StoredDocumentIndex(DocumentRecord record, List<RevisionLocation> revisions)
{
    public DocumentRecord Record { get; set; } = record;
    public List<RevisionLocation> Revisions { get; set; } = revisions;
    public int Generation { get; set; }
}

internal sealed class DocumentAddressIndex
{
    public List<long> Ids { get; } = [];
    public long? CurrentId { get; set; }
}

internal sealed class ByteBudgetLruCache<TKey, TValue>(int maxEntries, long maxBytes) where TKey : notnull
{
    private sealed class CacheNode(TKey key, TValue value, int byteCost)
    {
        public TKey Key { get; } = key;
        public TValue Value { get; set; } = value;
        public int ByteCost { get; set; } = byteCost;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<TKey, LinkedListNode<CacheNode>> map = [];
    private readonly LinkedList<CacheNode> lru = new();
    private long currentBytes;

    public bool TryGet(TKey key, out TValue value)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                if (!ReferenceEquals(node, lru.First))
                {
                    lru.Remove(node);
                    lru.AddFirst(node);
                }
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value, int byteCost)
    {
        var cost = Math.Max(1, byteCost);
        if (cost > maxBytes || maxEntries <= 0) return;
        lock (gate)
        {
            if (map.TryGetValue(key, out var existing))
            {
                currentBytes -= existing.Value.ByteCost;
                existing.Value.Value = value;
                existing.Value.ByteCost = cost;
                currentBytes += cost;
                if (!ReferenceEquals(existing, lru.First))
                {
                    lru.Remove(existing);
                    lru.AddFirst(existing);
                }
            }
            else
            {
                var node = new LinkedListNode<CacheNode>(new CacheNode(key, value, cost));
                lru.AddFirst(node);
                map[key] = node;
                currentBytes += cost;
            }
            while ((map.Count > maxEntries || currentBytes > maxBytes) && lru.Last is { } tail)
            {
                lru.RemoveLast();
                map.Remove(tail.Value.Key);
                currentBytes -= tail.Value.ByteCost;
            }
        }
    }

    public void RemoveWhere(Func<TKey, bool> predicate)
    {
        lock (gate)
        {
            var node = lru.First;
            while (node is not null)
            {
                var next = node.Next;
                if (predicate(node.Value.Key))
                {
                    lru.Remove(node);
                    map.Remove(node.Value.Key);
                    currentBytes -= node.Value.ByteCost;
                }
                node = next;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            map.Clear();
            lru.Clear();
            currentBytes = 0;
        }
    }
}

internal static class JsonlSortedIndex
{
    public static IEnumerable<int> ScanIndexes(IReadOnlyList<long> ids, ScanStart start)
    {
        if (start.Order == ScanOrder.Ascending)
        {
            for (var index = start.After is { } after ? UpperBound(ids, after) : 0; index < ids.Count; index++) yield return index;
        }
        else
        {
            for (var index = (start.After is { } after ? LowerBound(ids, after) : ids.Count) - 1; index >= 0; index--) yield return index;
        }
    }

    public static void InsertMapId(Dictionary<long, List<long>> map, long key, long id)
    {
        if (!map.TryGetValue(key, out var ids)) map[key] = ids = [];
        InsertSorted(ids, id);
    }

    public static void InsertSorted(List<long> values, long value)
    {
        var index = LowerBound(values, value);
        if (index < values.Count && values[index] == value) return;
        values.Insert(index, value);
    }

    public static void RemoveSorted(List<long> values, long value)
    {
        var index = LowerBound(values, value);
        if (index < values.Count && values[index] == value) values.RemoveAt(index);
    }

    public static int LowerBound(IReadOnlyList<long> values, long target)
    {
        int low = 0, high = values.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] < target) low = middle + 1; else high = middle;
        }
        return low;
    }

    public static int UpperBound(IReadOnlyList<long> values, long target)
    {
        int low = 0, high = values.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] <= target) low = middle + 1; else high = middle;
        }
        return low;
    }
}
