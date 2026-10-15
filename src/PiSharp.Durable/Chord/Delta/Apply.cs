// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/index.ts (apply, applyImmutable, applyImmutableBatches,
// appendCopy, copyContainers, resolve). Values here are immutable, so every application is the immutable one: containers on a written path
// are copied once per call (shared across batches, as upstream's owned set is) into a private work tree, and frozen back at the end;
// untouched subtrees keep their references. An append to an array not yet copied in this call is built as one copy with the items
// (appendCopy) instead of a work copy that the insert grows and the freeze copies again; the appended array then counts as copied.
using System.Collections.Immutable;
using System.Globalization;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>Applies operations to immutable values.</summary>
public static class DeltaApply
{
    /// <summary>Applies one batch to <paramref name="target"/> (null when there is no value yet; only <c>r</c> can start one).</summary>
    public static JsonValue Apply(JsonValue? target, IEnumerable<Op> ops) => ApplyBatches(target, [ops]);

    /// <summary>Applies batches as one final-result-only replay. Nothing partial is exposed: a failure throws before any result.</summary>
    public static JsonValue ApplyBatches(JsonValue? target, IEnumerable<IEnumerable<Op>> batches) => ApplyBatches(target, batches, trusted: false);

    /// <summary>
    /// The shared replay. <paramref name="trusted"/> (applyImmutableTrusted, see <see cref="DeltaApplyTrusted"/>) skips the path-safety
    /// check for operations this process produced and already validated; resolution failures still throw.
    /// </summary>
    internal static JsonValue ApplyBatches(JsonValue? target, IEnumerable<IEnumerable<Op>> batches, bool trusted)
    {
        ArgumentNullException.ThrowIfNull(batches);
        object? root = target;
        HashSet<JsonArrayValue>? appended = null; // arrays appendCopy built in this call: owned, so later edits take the general path
        foreach (var ops in batches)
        {
            ArgumentNullException.ThrowIfNull(ops);
            foreach (var op in ops)
            {
                ArgumentNullException.ThrowIfNull(op);
                if (op is ReplaceOp replace) { root = replace.Value; continue; }
                if (op is SpliceOp { Remove: 0 } splice && AppendCopy(root, splice, appended ??= new(ReferenceEqualityComparer.Instance)) is { } grown)
                {
                    root = grown;
                    continue;
                }
                var path = ((PathOp)op).Path;
                if (!trusted) DeltaValidation.AssertSafePath(path);
                var containerPath = op is SpliceOp or MoveOp ? path : path[..^1];
                root = Own(root, containerPath, path);
                ApplyOne((Work)root, op, path);
            }
        }
        return root switch
        {
            Work work => work.Freeze(),
            JsonValue value => value,
            _ => throw new PathError(ImmutableArray<PathSegment>.Empty)
        };
    }

    // appendCopy: an append to an array not yet copied in this replay, built as one copy with the items. Null for any other splice and for
    // any path that is not a plain walk over own properties, which takes the general path and so fails with its errors.
    private static object? AppendCopy(object? root, SpliceOp op, HashSet<JsonArrayValue> appended)
    {
        var path = op.Path;
        var target = root;
        foreach (var segment in path)
        {
            var valid = segment.Key is { } key
                ? !DeltaValidation.ReservedSegments.Contains(key) && target is JsonObjectValue or WorkObject
                : segment.Index >= 0 && target is JsonArrayValue or WorkArray;
            if (!valid) return null;
            switch (target)
            {
                case WorkObject work:
                    if (!work.HasOwn(segment)) return null;
                    target = work.Peek(segment);
                    break;
                case WorkArray work:
                    if (!work.HasOwn(segment)) return null;
                    target = work.Peek(segment);
                    break;
                case JsonObjectValue obj:
                    if (!obj.TryGetValue(segment.Key!, out var property)) return null;
                    target = property;
                    break;
                case JsonArrayValue items:
                    if (segment.Index >= items.Count) return null;
                    target = items[(int)segment.Index];
                    break;
            }
        }
        if (target is not JsonArrayValue array || appended.Contains(array) || op.Index != array.Count) return null;
        var grown = new JsonArrayValue(array.Items.AddRange(op.Items));
        appended.Add(grown);
        if (path.IsEmpty) return grown;
        var owned = Own(root, path[..^1], path);
        Resolve(owned, path[..^1]).Set(path[^1], grown);
        return owned;
    }

    // copyContainers: the root and every container on the path become owned work nodes (existence is checked before the segment's
    // type, as upstream does).
    private static Work Own(object? root, ImmutableArray<PathSegment> containerPath, ImmutableArray<PathSegment> path)
    {
        var owned = root switch
        {
            Work work => work,
            JsonObjectValue obj => new WorkObject(obj),
            JsonArrayValue array => new WorkArray(array),
            _ => throw new PathError(path)
        };
        var destination = owned;
        foreach (var segment in containerPath)
        {
            if (!destination.HasOwn(segment)) throw new PathError(path);
            if (destination is WorkArray && !segment.IsIndex) throw new UnsafePathError(segment);
            destination = destination.OwnChild(segment) ?? throw new PathError(path);
        }
        return owned;
    }

    private static void ApplyOne(Work root, Op op, ImmutableArray<PathSegment> path)
    {
        if (op is SpliceOp splice)
        {
            if (Resolve(root, path) is not WorkArray array) throw new PathError(path);
            array.Splice(splice.Index, splice.Remove, splice.Items);
            return;
        }
        if (op is MoveOp move)
        {
            if (Resolve(root, path) is not WorkArray array || array.Count != move.Permutation.Length) throw new PathError(path);
            array.Permute(move.Permutation);
            return;
        }
        var parent = Resolve(root, path[..^1]);
        var key = path[^1];
        if (parent is WorkArray parentArray)
        {
            if (!key.IsIndex) throw new UnsafePathError(key);
            if (key.Index > parentArray.Count) throw new UnsafePathError(key); // an index may append exactly one past the end
        }
        switch (op)
        {
            case SetOp set: parent.Set(key, set.Value); break;
            case DeleteOp:
                if (parent is WorkArray array)
                {
                    if (key.Index >= array.Count) throw new PathError(path);
                    array.Splice(key.Index, 1, []);
                }
                else ((WorkObject)parent).Delete(key);
                break;
            case AppendOp append:
                if (parent.Read(key) is not JsonStringValue current) throw new PathError(path);
                parent.Set(key, JsonValue.String(current.Value + append.Text));
                break;
            case TruncateOp truncate:
                if (parent.Read(key) is not JsonStringValue text) throw new PathError(path);
                parent.Set(key, JsonValue.String(truncate.Count >= text.Value.Length ? "" : text.Value[(int)truncate.Count..]));
                break;
        }
    }

    // resolve: own properties only; a key segment on an array is unsafe.
    private static Work Resolve(Work root, ImmutableArray<PathSegment> path)
    {
        var node = root;
        foreach (var segment in path)
        {
            if (node is WorkArray && !segment.IsIndex) throw new UnsafePathError(segment);
            if (!node.HasOwn(segment)) throw new PathError(path);
            node = node.OwnChild(segment) ?? throw new PathError(path);
        }
        return node;
    }

    private abstract class Work
    {
        public abstract bool HasOwn(PathSegment segment);
        /// <summary>The child container as an owned work node, or null when the child is not a container.</summary>
        public abstract Work? OwnChild(PathSegment segment);
        public abstract JsonValue? Read(PathSegment segment);
        /// <summary>The child as it is: a work node when already owned, otherwise the immutable value.</summary>
        public abstract object Peek(PathSegment segment);
        public abstract void Set(PathSegment segment, JsonValue value);
        public abstract JsonValue Freeze();

        protected static Work? Wrap(JsonValue value) => value switch
        {
            JsonObjectValue obj => new WorkObject(obj),
            JsonArrayValue array => new WorkArray(array),
            _ => null
        };
        protected static JsonValue Frozen(object item) => item is Work work ? work.Freeze() : (JsonValue)item;
    }

    private sealed class WorkObject(JsonObjectValue start) : Work
    {
        private readonly JsonObjectValue.Builder values = start.ToBuilder();
        private readonly Dictionary<string, Work> owned = new(StringComparer.Ordinal);

        private static string Key(PathSegment segment) => segment.Key ?? segment.Index.ToString(CultureInfo.InvariantCulture);
        public override bool HasOwn(PathSegment segment) => values.ContainsKey(Key(segment));
        public override Work? OwnChild(PathSegment segment)
        {
            var key = Key(segment);
            if (owned.TryGetValue(key, out var existing)) return existing;
            if (Wrap(values[key]) is not { } child) return null;
            owned[key] = child;
            return child;
        }
        public override JsonValue? Read(PathSegment segment)
        {
            var key = Key(segment);
            return owned.TryGetValue(key, out var work) ? work.Freeze() : values.TryGetValue(key, out var value) ? value : null;
        }
        public override object Peek(PathSegment segment)
        {
            var key = Key(segment);
            return owned.TryGetValue(key, out var work) ? work : values[key];
        }
        public override void Set(PathSegment segment, JsonValue value)
        {
            var key = Key(segment);
            owned.Remove(key);
            values[key] = value;
        }
        public void Delete(PathSegment segment)
        {
            var key = Key(segment);
            owned.Remove(key);
            values.Remove(key);
        }
        public override JsonValue Freeze()
        {
            foreach (var (key, work) in owned) values[key] = work.Freeze();
            owned.Clear();
            return values.ToImmutable();
        }
    }

    private sealed class WorkArray(JsonArrayValue start) : Work
    {
        private readonly List<object> items = [.. start.Items];

        public int Count => items.Count;
        private bool InRange(PathSegment segment) => segment.IsIndex && segment.Index >= 0 && segment.Index < items.Count;
        // An object key that spells an index exists on an array too (Object.hasOwn(array, "0")); its type is rejected after.
        public override bool HasOwn(PathSegment segment) =>
            InRange(segment) || (segment.Key is { } key && JsonObjectValue.IsArrayIndex(key) && long.Parse(key, CultureInfo.InvariantCulture) < items.Count);
        public override Work? OwnChild(PathSegment segment)
        {
            var index = (int)segment.Index;
            if (items[index] is Work work) return work;
            if (Wrap((JsonValue)items[index]) is not { } child) return null;
            items[index] = child;
            return child;
        }
        public override JsonValue? Read(PathSegment segment) => InRange(segment) ? Frozen(items[(int)segment.Index]) : null;
        public override object Peek(PathSegment segment) => items[(int)segment.Index];
        public override void Set(PathSegment segment, JsonValue value)
        {
            if (segment.Index == items.Count) items.Add(value);
            else items[(int)segment.Index] = value;
        }
        public void Splice(long index, long remove, ImmutableArray<JsonValue> inserted)
        {
            var start = (int)Math.Min(index, items.Count);
            var count = (int)Math.Min(remove, items.Count - start);
            items.RemoveRange(start, count);
            items.InsertRange(start, inserted.Select(item => (object)item));
        }
        public void Permute(ImmutableArray<int> permutation)
        {
            var previous = items.ToArray();
            for (var index = 0; index < permutation.Length; index++) items[index] = previous[permutation[index]];
        }
        public override JsonValue Freeze()
        {
            var builder = ImmutableArray.CreateBuilder<JsonValue>(items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                var frozen = Frozen(items[index]);
                items[index] = frozen;
                builder.Add(frozen);
            }
            return new JsonArrayValue(builder.MoveToImmutable());
        }
    }
}
