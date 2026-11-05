// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/diff.ts (diffRevisions)
//
// Chord v2's copy (area cv2-state; see Ops.cs) of PiSharp.Durable's port of diff.ts, on JsonNode trees. Identity is JavaScript's ===:
// containers (JsonObject, JsonArray) compare by reference and primitives by value (a string by its UTF-16 code units, a number as a double,
// JSON null as null), so two revisions that share a subtree diff it narrowly, as upstream does. A JsonNode has one parent, though, so two
// JsonNode revisions share no subtree: their containers are never identical below the root, and the identity steps (the permutation, the
// identity subsequence and anchors, and semantic alignment through a shared child) find matches only among primitives. Diffing such
// revisions is structural (deep equality, then the positional and semantic steps), as upstream's diff of two reconstructed revisions is:
// the same JSON before and after gives upstream's ops for reconstructed values. Object keys are walked in JavaScript's own-key order
// (ChordJson.KeysInJavaScriptOrder), so ops come in the order Object.keys gives. Values are JSON (JsonObject and JsonArray trees whose
// leaves are JSON primitives); a JsonValue that is no JSON primitive is compared by reference.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.Chord.Delta;

/// <summary>Computes compact operation batches between JSON revisions.</summary>
public static class DeltaDiff
{
    private const int DefaultOverlapScan = 65_536;
    private const int MaxDeltaOperations = 4_096;
    private const int MaxIdentityCandidates = 200_000;
    private const int MaxSemanticCells = 65_536;

    // JSON null as an identity key (a Dictionary takes no null key).
    private static readonly object NullKey = new();

    /// <summary>
    /// The operations from <paramref name="before"/> to <paramref name="after"/> (upstream <c>diffRevisions</c>): per-key sets and deletes,
    /// string append and front truncation, array splices and permutations. Deeply equal values yield no operations; more than 4,096
    /// operations, or a delta whose JSON would be at least 64 KiB and no smaller than the snapshot, yield <c>[["r", after]]</c>.
    /// </summary>
    public static ImmutableArray<Op> DiffRevisions(JsonNode? before, JsonNode? after)
    {
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

    /// <summary>JavaScript's <c>left === right</c> on JSON values: containers by reference, primitives by value.</summary>
    internal static bool Strict(JsonNode? left, JsonNode? right) => ReferenceEquals(left, right) || IdentityKey(left).Equals(IdentityKey(right));

    /// <summary>The key a value is identical by: a container itself, a primitive's value (a string, a double, a boolean, or JSON null's
    /// key), and a JsonValue that is no JSON primitive itself.</summary>
    private static object IdentityKey(JsonNode? node)
    {
        if (node is null) return NullKey;
        if (node is JsonValue value && ChordJson.Primitive(value, out var primitive)) return primitive ?? NullKey;
        return node;
    }

    private static bool IsContainer(JsonNode? node) => node is JsonObject or JsonArray;

    /// <summary>Deep equality of JSON values (upstream <c>equalJson</c>).</summary>
    private static bool EqualJson(JsonNode? left, JsonNode? right)
    {
        if (Strict(left, right)) return true;
        switch (left)
        {
            case JsonArray leftArray when right is JsonArray rightArray:
                if (leftArray.Count != rightArray.Count) return false;
                for (var index = 0; index < leftArray.Count; index++)
                    if (!EqualJson(leftArray[index], rightArray[index])) return false;
                return true;
            case JsonObject leftObject when right is JsonObject rightObject:
                if (leftObject.Count != rightObject.Count) return false;
                foreach (var (key, value) in leftObject)
                    if (!rightObject.TryGetPropertyValue(key, out var other) || !EqualJson(value, other)) return false;
                return true;
            default:
                return false;
        }
    }

    private static bool SameValue(JsonNode? left, JsonNode? right) => Strict(left, right) || EqualJson(left, right);

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

    private static void EmitSet(ImmutableArray<PathSegment> path, JsonNode? value, Batch batch) =>
        batch.Emit(path.IsEmpty ? new ReplaceOp(value) : new SetOp(path, value));

    private static int[]? Permutation(JsonArray before, JsonArray after)
    {
        if (before.Count != after.Count) return null;
        var positions = new Dictionary<object, (List<int> Indices, int Used)>();
        for (var index = 0; index < before.Count; index++)
        {
            var key = IdentityKey(before[index]);
            if (positions.TryGetValue(key, out var entry)) entry.Indices.Add(index);
            else positions[key] = ([index], 0);
        }
        var result = new int[after.Count];
        for (var index = 0; index < after.Count; index++)
        {
            var key = IdentityKey(after[index]);
            if (!positions.TryGetValue(key, out var entry) || entry.Used == entry.Indices.Count) return null;
            result[index] = entry.Indices[entry.Used];
            positions[key] = (entry.Indices, entry.Used + 1);
        }
        return result;
    }

    // O(length): comparing two revisions reads both strings; an op's text is a string of its own (upstream's ownedSlice).
    private static void EmitString(string before, string after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        if (after.Length > before.Length && after.StartsWith(before, StringComparison.Ordinal))
        {
            batch.Emit(new AppendOp(path, after[before.Length..]));
            return;
        }
        var shared = DeltaOps.Overlap(before, after, DefaultOverlapScan);
        if (shared == 0)
        {
            batch.Emit(new SetOp(path, JsonValue.Create(after)));
            return;
        }
        batch.Emit(new TruncateOp(path, before.Length - shared));
        if (after.Length > shared) batch.Emit(new AppendOp(path, after[shared..]));
    }

    private static List<(int Before, int After)>? LcsMatches(JsonNode?[] before, JsonNode?[] after, Func<JsonNode?, JsonNode?, bool> equal, int maxCells)
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

    private static bool SemanticallyAligned(JsonNode? left, JsonNode? right)
    {
        if (SameValue(left, right)) return true;
        switch (left)
        {
            case JsonArray leftArray when right is JsonArray rightArray:
                if (leftArray.Count != rightArray.Count) return false;
                for (var index = 0; index < leftArray.Count; index++)
                    if (IsContainer(leftArray[index]) && ReferenceEquals(leftArray[index], rightArray[index])) return true;
                return false;
            case JsonObject leftObject when right is JsonObject rightObject:
                foreach (var key in ChordJson.KeysInJavaScriptOrder(leftObject))
                {
                    var value = leftObject[key];
                    if (IsContainer(value) && rightObject.TryGetPropertyValue(key, out var other) && ReferenceEquals(value, other)) return true;
                }
                return false;
            default:
                return false;
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

    private static List<(int Before, int After)>? IdentitySubsequence(JsonArray before, int beforeStart, int beforeEnd, JsonArray after,
        int afterStart, int afterEnd)
    {
        var beforeCount = beforeEnd - beforeStart;
        var afterCount = afterEnd - afterStart;
        var matches = new List<(int, int)>();
        if (afterCount < beforeCount)
        {
            var beforeIndex = beforeStart;
            for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
            {
                while (beforeIndex < beforeEnd && !Strict(before[beforeIndex], after[afterIndex])) beforeIndex++;
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
                while (afterIndex < afterEnd && !Strict(before[beforeIndex], after[afterIndex])) afterIndex++;
                if (afterIndex == afterEnd) return null;
                matches.Add((beforeIndex, afterIndex++));
            }
            return matches;
        }
        return null;
    }

    private static List<(int Before, int After)> GreedyIdentityAnchors(Dictionary<object, List<int>> positions, JsonArray after, int afterStart,
        int afterEnd)
    {
        var matches = new List<(int, int)>();
        var previous = -1;
        for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
        {
            if (!positions.TryGetValue(IdentityKey(after[afterIndex]), out var candidates)) continue;
            var at = LowerBound(candidates, previous + 1);
            if (at == candidates.Count) continue;
            matches.Add((candidates[at], afterIndex));
            previous = candidates[at];
        }
        return matches;
    }

    private static List<(int Before, int After)> IdentityAnchors(JsonArray before, int beforeStart, int beforeEnd, JsonArray after, int afterStart,
        int afterEnd)
    {
        var positions = new Dictionary<object, List<int>>();
        for (var index = beforeStart; index < beforeEnd; index++)
        {
            var key = IdentityKey(before[index]);
            if (positions.TryGetValue(key, out var existing)) existing.Add(index);
            else positions[key] = [index];
        }
        var candidateCount = 0L;
        for (var index = afterStart; index < afterEnd; index++)
        {
            candidateCount += positions.TryGetValue(IdentityKey(after[index]), out var found) ? found.Count : 0;
            if (candidateCount > MaxIdentityCandidates) return GreedyIdentityAnchors(positions, after, afterStart, afterEnd);
        }
        if (candidateCount == 0) return [];

        // Longest increasing subsequence of before-indices over after order (patience sorting with back links).
        var candidates = new List<(int Before, int After, int Previous)>();
        var tails = new List<int>();
        var tailValues = new List<int>();
        for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
        {
            if (!positions.TryGetValue(IdentityKey(after[afterIndex]), out var beforePositions)) continue;
            for (var index = beforePositions.Count - 1; index >= 0; index--)
            {
                var beforeIndex = beforePositions[index];
                var at = LowerBound(tailValues, beforeIndex);
                var candidateIndex = candidates.Count;
                candidates.Add((beforeIndex, afterIndex, at == 0 ? -1 : tails[at - 1]));
                if (at == tails.Count)
                {
                    tails.Add(candidateIndex);
                    tailValues.Add(beforeIndex);
                }
                else
                {
                    tails[at] = candidateIndex;
                    tailValues[at] = beforeIndex;
                }
            }
        }
        var matches = new List<(int, int)>();
        for (var candidateIndex = tails.Count == 0 ? -1 : tails[^1]; candidateIndex >= 0; candidateIndex = candidates[candidateIndex].Previous)
            matches.Add((candidates[candidateIndex].Before, candidates[candidateIndex].After));
        matches.Reverse();
        return matches;
    }

    private static ImmutableArray<JsonNode?> Slice(JsonArray array, int start, int end)
    {
        var items = ImmutableArray.CreateBuilder<JsonNode?>(end - start);
        for (var index = start; index < end; index++) items.Add(array[index]);
        return items.MoveToImmutable();
    }

    private static void ProcessArrayMatches(JsonArray before, JsonArray after, ImmutableArray<PathSegment> path, Batch batch, int beforeStart,
        int beforeEnd, int afterStart, int afterEnd, int outputStart, List<(int Before, int After)> matches)
    {
        var beforeAt = beforeStart;
        var afterAt = afterStart;
        var outputAt = outputStart;
        foreach (var (beforeMatch, afterMatch) in matches)
        {
            if (batch.Overflowed) return;
            DiffArrayRegion(before, after, path, batch, beforeAt, beforeMatch, afterAt, afterMatch, outputAt);
            outputAt += afterMatch - afterAt;
            if (!SameValue(before[beforeMatch], after[afterMatch])) DiffValue(before[beforeMatch], after[afterMatch], path.Add(outputAt), batch);
            outputAt++;
            beforeAt = beforeMatch + 1;
            afterAt = afterMatch + 1;
        }
        if (!batch.Overflowed) DiffArrayRegion(before, after, path, batch, beforeAt, beforeEnd, afterAt, afterEnd, outputAt);
    }

    private static void DiffArrayRegion(JsonArray before, JsonArray after, ImmutableArray<PathSegment> path, Batch batch, int beforeStart,
        int beforeEnd, int afterStart, int afterEnd, int outputStart)
    {
        if (batch.Overflowed) return;
        while (beforeStart < beforeEnd && afterStart < afterEnd && SameValue(before[beforeStart], after[afterStart]))
        {
            beforeStart++;
            afterStart++;
            outputStart++;
        }
        while (beforeStart < beforeEnd && afterStart < afterEnd && SameValue(before[beforeEnd - 1], after[afterEnd - 1]))
        {
            beforeEnd--;
            afterEnd--;
        }
        var beforeCount = beforeEnd - beforeStart;
        var afterCount = afterEnd - afterStart;
        if (beforeCount == 0 && afterCount == 0) return;
        if (beforeCount == 0 || afterCount == 0)
        {
            batch.Emit(new SpliceOp(path, outputStart, beforeCount, Slice(after, afterStart, afterEnd)));
            return;
        }

        if (beforeCount == afterCount)
        {
            var positional = new List<(int, int)>();
            for (var offset = 0; offset < beforeCount; offset++)
                if (SameValue(before[beforeStart + offset], after[afterStart + offset])) positional.Add((beforeStart + offset, afterStart + offset));
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

        var semantic = LcsMatches([.. Slice(before, beforeStart, beforeEnd)], [.. Slice(after, afterStart, afterEnd)], SemanticallyAligned, MaxSemanticCells);
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
        batch.Emit(new SpliceOp(path, outputStart, beforeCount, Slice(after, afterStart, afterEnd)));
    }

    private static void DiffArray(JsonArray before, JsonArray after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (ReferenceEquals(before, after) || EqualJson(before, after)) return;
        if (before.Count == after.Count && before.Count > 1 && !SameValue(before[0], after[0]) && !SameValue(before[^1], after[^1]) &&
            Permutation(before, after) is { } order)
        {
            batch.Emit(new MoveOp(path, [.. order]));
            return;
        }
        DiffArrayRegion(before, after, path, batch, 0, before.Count, 0, after.Count, 0);
    }

    private static void DiffObject(JsonObject before, JsonObject after, ImmutableArray<PathSegment> path, Batch batch)
    {
        var beforeKeys = ChordJson.KeysInJavaScriptOrder(before).ToList();
        var afterKeys = ChordJson.KeysInJavaScriptOrder(after).ToList();
        if (beforeKeys.Concat(afterKeys).Any(DeltaOps.ReservedSegments.Contains))
        {
            if (!EqualJson(before, after)) EmitSet(path, after, batch);
            return;
        }
        foreach (var key in afterKeys)
        {
            if (batch.Overflowed) return;
            if (before.TryGetPropertyValue(key, out var previous)) DiffValue(previous, after[key], path.Add(key), batch);
            else EmitSet(path.Add(key), after[key], batch);
        }
        foreach (var key in beforeKeys)
        {
            if (batch.Overflowed) return;
            if (!after.ContainsKey(key)) batch.Emit(new DeleteOp(path.Add(key)));
        }
    }

    private static void DiffValue(JsonNode? before, JsonNode? after, ImmutableArray<PathSegment> path, Batch batch)
    {
        if (Strict(before, after) || batch.Overflowed) return;
        if (!path.IsEmpty && ChordJson.AsString(before) is { } text && ChordJson.AsString(after) is { } next)
        {
            EmitString(text, next, path, batch);
            return;
        }
        switch (before)
        {
            case JsonArray array when after is JsonArray nextArray:
                DiffArray(array, nextArray, path, batch);
                return;
            case JsonObject value when after is JsonObject nextObject:
                DiffObject(value, nextObject, path, batch);
                return;
            default:
                EmitSet(path, after, batch);
                return;
        }
    }

    private static long JsonCost(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return 4;
            case JsonArray array:
            {
                long cost = 2;
                for (var index = 0; index < array.Count; index++) cost += JsonCost(array[index]) + (index == 0 ? 0 : 1);
                return cost;
            }
            case JsonObject obj:
            {
                long cost = 2;
                var index = 0;
                foreach (var (key, item) in obj) cost += key.Length + 3 + JsonCost(item) + (index++ == 0 ? 0 : 1);
                return cost;
            }
            default:
                ChordJson.Primitive((JsonValue)value, out var primitive);
                return primitive switch
                {
                    null => 4,
                    string text => text.Length + 2,
                    double number => JsonNumber.Text(number).Length,
                    bool flag => flag ? 4 : 5,
                    _ => 4,
                };
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
        SpliceOp splice => 10 + PathCost(splice.Path) + Digits(splice.Index) + Digits(splice.Remove) + 2 +
            splice.Items.Select(JsonCost).Sum() + Math.Max(0, splice.Items.Length - 1),
        MoveOp move => 7 + PathCost(move.Path) + 2 + move.Permutation.Sum(index => (long)Digits(index)) + Math.Max(0, move.Permutation.Length - 1),
        _ => throw new ArgumentException("unknown op", nameof(operation)),
    };
}
