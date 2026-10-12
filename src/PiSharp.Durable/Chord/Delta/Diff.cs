// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/src/delta/diff.ts. Identity is JavaScript's: containers compare by
// reference, scalars by value, so structurally shared revisions diff narrowly and reconstructed ones fall back to semantic alignment.
using System.Collections.Immutable;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>Computes compact operation batches between immutable revisions.</summary>
public static class DeltaDiff
{
    private const int DefaultOverlapScan = 65_536;
    private const int MaxDeltaOperations = 4_096;
    private const int MaxIdentityCandidates = 200_000;
    private const int MaxSemanticCells = 65_536;

    /// <summary>
    /// The operations from <paramref name="before"/> to <paramref name="after"/>: per-key sets and deletes, string append and front
    /// truncation, array splices and permutations. Deeply equal values yield no operations; more than 4,096 operations, or a delta whose
    /// JSON would be at least 64 KiB and no smaller than the snapshot, yield <c>[["r", after]]</c>.
    /// </summary>
    public static ImmutableArray<Op> DiffRevisions(JsonValue before, JsonValue after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var batch = new Batch();
        DiffValue(before, after, [], batch);
        if (batch.Overflowed) return [new ReplaceOp(after)];
        if (batch.Ops.Count == 0 || batch.Ops[0] is ReplaceOp) return [.. batch.Ops];
        long deltaCost = 2;
        foreach (var operation in batch.Ops) deltaCost += OperationCost(operation) + 1;
        if (deltaCost < 65_536) return [.. batch.Ops];
        var snapshotCost = JsonCost(after) + 6;
        return deltaCost >= snapshotCost ? [new ReplaceOp(after)] : [.. batch.Ops];
    }

    private sealed class Batch
    {
        public List<Op> Ops { get; } = [];
        public bool Overflowed { get; private set; }

        public void Emit(Op operation)
        {
            if (Overflowed) return;
            if (Ops.Count >= MaxDeltaOperations)
            {
                Overflowed = true;
                return;
            }
            Ops.Add(operation);
        }
    }

    private static void EmitSet(ImmutableArray<PathSegment> path, JsonValue value, Batch batch) =>
        batch.Emit(path.IsEmpty ? new ReplaceOp(value) : new SetOp(path, value));

    private static int[]? Permutation(JsonArrayValue before, JsonArrayValue after)
    {
        if (before.Count != after.Count) return null;
        var positions = new Dictionary<JsonValue, (List<int> Indices, int Used)>(JsonIdentity.Comparer);
        for (var index = 0; index < before.Count; index++)
        {
            if (positions.TryGetValue(before[index], out var entry)) entry.Indices.Add(index);
            else positions[before[index]] = ([index], 0);
        }
        var result = new int[after.Count];
        for (var index = 0; index < after.Count; index++)
        {
            if (!positions.TryGetValue(after[index], out var entry) || entry.Used == entry.Indices.Count) return null;
            result[index] = entry.Indices[entry.Used];
            positions[after[index]] = (entry.Indices, entry.Used + 1);
        }
        return result;
    }

    private static void EmitString(string before, string after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        if (after.Length > before.Length && after.StartsWith(before, StringComparison.Ordinal))
        {
            batch.Emit(new AppendOp(path, after[before.Length..]));
            return;
        }
        var shared = DeltaValidation.Overlap(before, after, DefaultOverlapScan);
        if (shared == 0)
        {
            batch.Emit(new SetOp(path, after));
            return;
        }
        batch.Emit(new TruncateOp(path, before.Length - shared));
        if (after.Length > shared) batch.Emit(new AppendOp(path, after[shared..]));
    }

    private static List<(int Before, int After)>? LcsMatches(JsonValue[] before, JsonValue[] after, Func<JsonValue, JsonValue, bool> equal, int maxCells)
    {
        if (before.Length == 0 || after.Length == 0) return [];
        if ((long)before.Length * after.Length > maxCells) return null;
        var width = after.Length + 1;
        var lengths = new uint[(before.Length + 1) * width];
        for (var left = before.Length - 1; left >= 0; left--)
        {
            for (var right = after.Length - 1; right >= 0; right--)
            {
                var at = left * width + right;
                lengths[at] = equal(before[left], after[right])
                    ? lengths[(left + 1) * width + right + 1] + 1
                    : Math.Max(lengths[(left + 1) * width + right], lengths[left * width + right + 1]);
            }
        }
        var matches = new List<(int, int)>();
        int l = 0, r = 0;
        while (l < before.Length && r < after.Length)
        {
            if (equal(before[l], after[r]) && lengths[l * width + r] == lengths[(l + 1) * width + r + 1] + 1) matches.Add((l++, r++));
            else if (lengths[(l + 1) * width + r] >= lengths[l * width + r + 1]) l++;
            else r++;
        }
        return matches;
    }

    private static bool SemanticallyAligned(JsonValue left, JsonValue right)
    {
        if (JsonIdentity.Same(left, right)) return true;
        switch (left)
        {
            case JsonArrayValue leftArray when right is JsonArrayValue rightArray:
                if (leftArray.Count != rightArray.Count) return false;
                for (var index = 0; index < leftArray.Count; index++)
                    if (JsonIdentity.IsContainer(leftArray[index]) && ReferenceEquals(leftArray[index], rightArray[index])) return true;
                return false;
            case JsonObjectValue leftObject when right is JsonObjectValue rightObject:
                foreach (var (key, value) in leftObject)
                    if (JsonIdentity.IsContainer(value) && rightObject.TryGetValue(key, out var other) && ReferenceEquals(value, other)) return true;
                return false;
            default: return false;
        }
    }

    private static int LowerBound(List<int> values, int value)
    {
        int low = 0, high = values.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static List<(int Before, int After)>? IdentitySubsequence(JsonArrayValue before, int beforeStart, int beforeEnd,
        JsonArrayValue after, int afterStart, int afterEnd)
    {
        var beforeCount = beforeEnd - beforeStart;
        var afterCount = afterEnd - afterStart;
        var matches = new List<(int, int)>();
        if (afterCount < beforeCount)
        {
            var beforeIndex = beforeStart;
            for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
            {
                while (beforeIndex < beforeEnd && !JsonIdentity.Strict(before[beforeIndex], after[afterIndex])) beforeIndex++;
                if (beforeIndex == beforeEnd) return null;
                matches.Add((beforeIndex++, afterIndex));
            }
            return matches;
        }
        if (beforeCount < afterCount)
        {
            var afterIndex = afterStart;
            for (var beforeIndex = beforeStart; beforeIndex < beforeEnd; beforeIndex++)
            {
                while (afterIndex < afterEnd && !JsonIdentity.Strict(before[beforeIndex], after[afterIndex])) afterIndex++;
                if (afterIndex == afterEnd) return null;
                matches.Add((beforeIndex, afterIndex++));
            }
            return matches;
        }
        return null;
    }

    private static List<(int Before, int After)> GreedyIdentityAnchors(Dictionary<JsonValue, List<int>> positions, JsonArrayValue after,
        int afterStart, int afterEnd)
    {
        var matches = new List<(int, int)>();
        var previous = -1;
        for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
        {
            if (!positions.TryGetValue(after[afterIndex], out var candidates)) continue;
            var at = LowerBound(candidates, previous + 1);
            if (at == candidates.Count) continue;
            matches.Add((candidates[at], afterIndex));
            previous = candidates[at];
        }
        return matches;
    }

    private static List<(int Before, int After)> IdentityAnchors(JsonArrayValue before, int beforeStart, int beforeEnd,
        JsonArrayValue after, int afterStart, int afterEnd)
    {
        var positions = new Dictionary<JsonValue, List<int>>(JsonIdentity.Comparer);
        for (var index = beforeStart; index < beforeEnd; index++)
        {
            if (positions.TryGetValue(before[index], out var existing)) existing.Add(index);
            else positions[before[index]] = [index];
        }
        var candidateCount = 0L;
        for (var index = afterStart; index < afterEnd; index++)
        {
            candidateCount += positions.TryGetValue(after[index], out var found) ? found.Count : 0;
            if (candidateCount > MaxIdentityCandidates) return GreedyIdentityAnchors(positions, after, afterStart, afterEnd);
        }
        if (candidateCount == 0) return [];

        // Longest increasing subsequence of before-indices over after order (patience sorting with back links).
        var candidates = new List<(int Before, int After, int Previous)>();
        var tails = new List<int>();
        var tailValues = new List<int>();
        for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
        {
            if (!positions.TryGetValue(after[afterIndex], out var beforePositions)) continue;
            for (var index = beforePositions.Count - 1; index >= 0; index--)
            {
                var beforeIndex = beforePositions[index];
                var at = LowerBound(tailValues, beforeIndex);
                var candidateIndex = candidates.Count;
                candidates.Add((beforeIndex, afterIndex, at == 0 ? -1 : tails[at - 1]));
                if (at == tails.Count) { tails.Add(candidateIndex); tailValues.Add(beforeIndex); }
                else { tails[at] = candidateIndex; tailValues[at] = beforeIndex; }
            }
        }
        var matches = new List<(int, int)>();
        for (var candidateIndex = tails.Count == 0 ? -1 : tails[^1]; candidateIndex >= 0; candidateIndex = candidates[candidateIndex].Previous)
            matches.Add((candidates[candidateIndex].Before, candidates[candidateIndex].After));
        matches.Reverse();
        return matches;
    }

    private static void ProcessArrayMatches(JsonArrayValue before, JsonArrayValue after, ImmutableArray<PathSegment> path, Batch batch,
        int beforeStart, int beforeEnd, int afterStart, int afterEnd, int outputStart, List<(int Before, int After)> matches)
    {
        var beforeAt = beforeStart;
        var afterAt = afterStart;
        var outputAt = outputStart;
        foreach (var (beforeMatch, afterMatch) in matches)
        {
            if (batch.Overflowed) return;
            DiffArrayRegion(before, after, path, batch, beforeAt, beforeMatch, afterAt, afterMatch, outputAt);
            outputAt += afterMatch - afterAt;
            if (!JsonIdentity.Same(before[beforeMatch], after[afterMatch]))
                DiffValue(before[beforeMatch], after[afterMatch], path.Add(outputAt), batch);
            outputAt++;
            beforeAt = beforeMatch + 1;
            afterAt = afterMatch + 1;
        }
        if (!batch.Overflowed) DiffArrayRegion(before, after, path, batch, beforeAt, beforeEnd, afterAt, afterEnd, outputAt);
    }

    private static void DiffArrayRegion(JsonArrayValue before, JsonArrayValue after, ImmutableArray<PathSegment> path, Batch batch,
        int beforeStart, int beforeEnd, int afterStart, int afterEnd, int outputStart)
    {
        if (batch.Overflowed) return;
        while (beforeStart < beforeEnd && afterStart < afterEnd && JsonIdentity.Same(before[beforeStart], after[afterStart]))
        {
            beforeStart++; afterStart++; outputStart++;
        }
        while (beforeStart < beforeEnd && afterStart < afterEnd && JsonIdentity.Same(before[beforeEnd - 1], after[afterEnd - 1]))
        {
            beforeEnd--; afterEnd--;
        }
        var beforeCount = beforeEnd - beforeStart;
        var afterCount = afterEnd - afterStart;
        if (beforeCount == 0 && afterCount == 0) return;
        if (beforeCount == 0 || afterCount == 0)
        {
            batch.Emit(new SpliceOp(path, outputStart, beforeCount, after.Items[afterStart..afterEnd]));
            return;
        }

        if (beforeCount == afterCount)
        {
            var positional = new List<(int, int)>();
            for (var offset = 0; offset < beforeCount; offset++)
                if (JsonIdentity.Same(before[beforeStart + offset], after[afterStart + offset])) positional.Add((beforeStart + offset, afterStart + offset));
            if (positional.Count > 0)
            {
                ProcessArrayMatches(before, after, path, batch, beforeStart, beforeEnd, afterStart, afterEnd, outputStart, positional);
                return;
            }
        }

        var subsequence = IdentitySubsequence(before, beforeStart, beforeEnd, after, afterStart, afterEnd);
        if (subsequence is { Count: > 0 })
        {
            ProcessArrayMatches(before, after, path, batch, beforeStart, beforeEnd, afterStart, afterEnd, outputStart, subsequence);
            return;
        }

        var identity = IdentityAnchors(before, beforeStart, beforeEnd, after, afterStart, afterEnd);
        if (identity.Count > 0)
        {
            ProcessArrayMatches(before, after, path, batch, beforeStart, beforeEnd, afterStart, afterEnd, outputStart, identity);
            return;
        }

        var semantic = LcsMatches(before.Items[beforeStart..beforeEnd].ToArray(), after.Items[afterStart..afterEnd].ToArray(), SemanticallyAligned, MaxSemanticCells);
        if (semantic is { Count: > 0 })
        {
            var absolute = semantic.Select(match => (beforeStart + match.Before, afterStart + match.After)).ToList();
            ProcessArrayMatches(before, after, path, batch, beforeStart, beforeEnd, afterStart, afterEnd, outputStart, absolute);
            return;
        }

        if (beforeCount == 1 && afterCount == 1)
        {
            DiffValue(before[beforeStart], after[afterStart], path.Add(outputStart), batch);
            return;
        }
        batch.Emit(new SpliceOp(path, outputStart, beforeCount, after.Items[afterStart..afterEnd]));
    }

    private static void DiffArray(JsonArrayValue before, JsonArrayValue after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (ReferenceEquals(before, after) || JsonValue.DeepEquals(before, after)) return;
        if (before.Count == after.Count && before.Count > 1 && !JsonIdentity.Same(before[0], after[0]) &&
            !JsonIdentity.Same(before[^1], after[^1]) && Permutation(before, after) is { } order)
        {
            batch.Emit(new MoveOp(path, [.. order]));
            return;
        }
        DiffArrayRegion(before, after, path, batch, 0, before.Count, 0, after.Count, 0);
    }

    private static void DiffObject(JsonObjectValue before, JsonObjectValue after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (before.Keys.Concat(after.Keys).Any(DeltaValidation.ReservedSegments.Contains))
        {
            if (!JsonValue.DeepEquals(before, after)) EmitSet(path, after, batch);
            return;
        }
        foreach (var (key, value) in after)
        {
            if (batch.Overflowed) return;
            if (before.TryGetValue(key, out var previous)) DiffValue(previous, value, path.Add(key), batch);
            else EmitSet(path.Add(key), value, batch);
        }
        foreach (var key in before.Keys)
        {
            if (batch.Overflowed) return;
            if (!after.ContainsKey(key)) batch.Emit(new DeleteOp(path.Add(key)));
        }
    }

    private static void DiffValue(JsonValue before, JsonValue after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (JsonIdentity.Strict(before, after) || batch.Overflowed) return;
        switch (before)
        {
            case JsonStringValue text when after is JsonStringValue next && !path.IsEmpty: EmitString(text.Value, next.Value, path, batch); return;
            case JsonArrayValue array when after is JsonArrayValue nextArray: DiffArray(array, nextArray, path, batch); return;
            case JsonObjectValue obj when after is JsonObjectValue nextObject: DiffObject(obj, nextObject, path, batch); return;
            default: EmitSet(path, after, batch); return;
        }
    }

    private static long JsonCost(JsonValue value)
    {
        switch (value)
        {
            case JsonNullValue: return 4;
            case JsonStringValue text: return text.Value.Length + 2;
            case JsonNumberValue number: return PiSharp.Contracts.JsonNumber.Text(number.Value).Length;
            case JsonBooleanValue boolean: return boolean.Value ? 4 : 5;
            case JsonArrayValue array:
            {
                long cost = 2;
                for (var index = 0; index < array.Count; index++) cost += JsonCost(array[index]) + (index == 0 ? 0 : 1);
                return cost;
            }
            default:
            {
                long cost = 2;
                var index = 0;
                foreach (var (key, item) in (JsonObjectValue)value) cost += key.Length + 3 + JsonCost(item) + (index++ == 0 ? 0 : 1);
                return cost;
            }
        }
    }

    private static long PathCost(ImmutableArray<PathSegment> path)
    {
        long cost = 2;
        for (var index = 0; index < path.Length; index++)
            cost += (path[index].Key is { } key ? key.Length + 2 : path[index].ToString().Length) + (index == 0 ? 0 : 1);
        return cost;
    }

    private static int Digits(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

    private static long OperationCost(Op operation) => operation switch
    {
        ReplaceOp replace => 6 + JsonCost(replace.Value),
        SetOp set => 7 + PathCost(set.Path) + JsonCost(set.Value),
        DeleteOp delete => 6 + PathCost(delete.Path),
        AppendOp append => 7 + PathCost(append.Path) + append.Text.Length + 2,
        TruncateOp truncate => 7 + PathCost(truncate.Path) + Digits(truncate.Count),
        SpliceOp splice => 10 + PathCost(splice.Path) + Digits(splice.Index) + Digits(splice.Remove) + JsonCost(new JsonArrayValue(splice.Items)),
        MoveOp move => 7 + PathCost(move.Path) + 2 + move.Permutation.Sum(index => (long)Digits(index)) + Math.Max(0, move.Permutation.Length - 1),
        _ => throw new ArgumentException("unknown op", nameof(operation))
    };
}
