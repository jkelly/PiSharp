// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/index.ts (apply, assertValidOp, assertSafePath, PathError,
// UnsafePathError)
//
// Delta's applier as Chord v2's documents engine runs it (area cv2-store-engine): a document's deltas, stored as JSON tuples, applied once
// to its base at load (engine.ts's loadContent). Like Ops.cs, this is Chord v2's own copy beside PiSharp.Durable's port of the same file
// (PiSharp.Durable.Chord.Delta's DeltaApply, on Durable's JsonValue); it works on JsonNode trees and on the tuples as JSON, which is how
// the engine holds them, so it validates each tuple as upstream's assertValidOp does before applying it. Upstream applies in place and
// adopts the tuple's values; a JsonNode has one parent, so each value is copied into the target instead. Upstream's TypeError is an
// ArgumentException here, its PathError and UnsafePathError exceptions of their own, as in Durable's port.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Delta;

/// <summary>A path segment that reaches the prototype chain, a non-integer or negative index, or an index past an array's end (upstream
/// <c>UnsafePathError</c>).</summary>
internal sealed class UnsafePathError(string segment) : Exception($"unsafe path segment: {segment}")
{
    /// <summary>The offending segment, as JavaScript's <c>String(segment)</c> writes it.</summary>
    internal string Segment { get; } = segment;
}

/// <summary>A path that does not resolve (upstream <c>PathError</c>).</summary>
internal sealed class PathError(string path) : Exception($"unresolvable path: {path}")
{
    /// <summary>The path as <c>JSON.stringify</c> writes it.</summary>
    internal string Path { get; } = path;
}

/// <summary>Applying decoded operations to a JSON value (upstream <c>apply</c>).</summary>
internal static class DeltaApply
{
    /// <summary>Segments that reach a JavaScript prototype chain (upstream <c>RESERVED_SEGMENTS</c>).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "__proto__", "constructor", "prototype" };

    /// <summary>Applies <paramref name="ops"/>, decoded operation tuples, to <paramref name="target"/> in place and returns the value (an
    /// <c>r</c> replaces it outright). Each tuple is checked first (upstream <c>assertValidOp</c>).</summary>
    internal static JsonNode? Apply(JsonNode? target, JsonArray ops)
    {
        var root = target;
        foreach (var raw in ops)
        {
            AssertValidOp(raw);
            var op = (JsonArray)raw!;
            var verb = (string)op[0]!;
            if (verb == "r")
            {
                root = op[1]?.DeepClone();
                continue;
            }
            var path = Path(op[1]!.AsArray());
            if (verb == "p")
            {
                var spliced = (path.Count == 0 ? root : Resolve(root, path)) as JsonArray ?? throw new PathError(Text(path));
                Splice(spliced, (long)Number(op[2])!, (long)Number(op[3])!, op[4]!.AsArray());
                continue;
            }
            if (verb == "m")
            {
                var permutation = op[2]!.AsArray();
                if ((path.Count == 0 ? root : Resolve(root, path)) is not JsonArray moved || moved.Count != permutation.Count) throw new PathError(Text(path));
                var previous = moved.ToList();
                moved.Clear();
                foreach (var index in permutation) moved.Add(previous[(int)Number(index)!.Value]);
                continue;
            }
            // s, d, a and t never target the root: their paths are not empty.
            var parentPath = path.GetRange(0, path.Count - 1);
            var parent = Resolve(root, parentPath);
            var key = path[^1];
            if (parent is JsonArray array)
            {
                if (key.IsIndex is false) throw new UnsafePathError(key.ToString());
                // An index addresses an element or appends one past the end: a sparse array would not survive JSON.
                if (key.Index > array.Count) throw new UnsafePathError(key.ToString());
            }
            switch (verb)
            {
                case "s":
                    Write(parent!, key, op[2]?.DeepClone());
                    break;
                case "d":
                    if (parent is JsonArray list)
                    {
                        if (key.Index >= list.Count) throw new PathError(Text(path));
                        list.RemoveAt((int)key.Index);
                    }
                    else ((JsonObject)parent!).Remove(key.ToString());
                    break;
                case "a":
                {
                    var current = ChordJson.AsString(Read(parent!, key)) ?? throw new PathError(Text(path));
                    Write(parent!, key, JsonValue.Create(current + (string)op[2]!));
                    break;
                }
                case "t":
                {
                    var current = ChordJson.AsString(Read(parent!, key)) ?? throw new PathError(Text(path));
                    var count = (long)Number(op[2])!;
                    Write(parent!, key, JsonValue.Create(count >= current.Length ? "" : current[(int)count..]));
                    break;
                }
            }
        }
        return root;
    }

    /// <summary>Verb, arity and payload shape of a decoded operation (paths inline, no <c>#</c>, no short forms); payload values are not
    /// inspected (upstream <c>assertValidOp</c>).</summary>
    internal static void AssertValidOp(JsonNode? op)
    {
        if (op is not JsonArray { Count: > 0 } tuple) throw new ArgumentException("op is not a tuple");
        var verb = ChordJson.AsString(tuple[0]);
        switch (verb)
        {
            case "r":
                if (tuple.Count != 2) throw new ArgumentException("r arity");
                return;
            case "s":
                if (tuple.Count != 3) throw new ArgumentException("s arity");
                AssertPathArg(tuple[1], true);
                return;
            case "d":
                if (tuple.Count != 2) throw new ArgumentException("d arity");
                AssertPathArg(tuple[1], true);
                return;
            case "a":
                if (tuple.Count != 3 || ChordJson.AsString(tuple[2]) is null) throw new ArgumentException("a shape");
                AssertPathArg(tuple[1], true);
                return;
            case "t":
                if (tuple.Count != 3 || !IsNonNegativeInteger(tuple[2])) throw new ArgumentException("t shape");
                AssertPathArg(tuple[1], true);
                return;
            case "p":
                if (tuple.Count != 5) throw new ArgumentException("p arity");
                AssertPathArg(tuple[1]);
                if (!IsNonNegativeInteger(tuple[2])) throw new ArgumentException("p index");
                if (!IsNonNegativeInteger(tuple[3])) throw new ArgumentException("p remove");
                if (tuple[4] is not JsonArray) throw new ArgumentException("p items");
                return;
            case "m":
                if (tuple.Count != 3) throw new ArgumentException("m arity");
                AssertPathArg(tuple[1]);
                AssertPermutation(tuple[2]);
                return;
            default:
                // Silently skipping an unknown verb is how a newer producer's op vanishes.
                throw new ArgumentException($"unknown op verb: {ChordJson.ToJavaScriptString(tuple[0])}");
        }
    }

    private static void AssertPathArg(JsonNode? path, bool nonEmpty = false)
    {
        if (path is not JsonArray segments) throw new ArgumentException("path is not an array");
        if (nonEmpty && segments.Count == 0) throw new ArgumentException("path is empty");
        Path(segments);
    }

    private static void AssertPermutation(JsonNode? value)
    {
        if (value is not JsonArray permutation) throw new ArgumentException("m permutation is not an array");
        var seen = new bool[permutation.Count];
        foreach (var index in permutation)
        {
            if (!IsNonNegativeInteger(index) || Number(index) >= permutation.Count || seen[(int)Number(index)!.Value])
                throw new ArgumentException("m permutation is not a bijection");
            seen[(int)Number(index)!.Value] = true;
        }
    }

    /// <summary>A path's segments, checked as upstream's <c>assertSafePath</c> checks them: no reserved key, every index a non-negative
    /// integer (any other segment is unsafe).</summary>
    private static List<PathSegment> Path(JsonArray segments)
    {
        var path = new List<PathSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (ChordJson.AsString(segment) is { } key)
            {
                if (Reserved.Contains(key)) throw new UnsafePathError(key);
                path.Add(PathSegment.Of(key));
            }
            else if (IsNonNegativeInteger(segment)) path.Add(PathSegment.Of((long)Number(segment)!));
            else throw new UnsafePathError(ChordJson.ToJavaScriptString(segment));
        }
        return path;
    }

    /// <summary>The node at <paramref name="path"/>, an object or an array (upstream <c>resolve</c>): own properties only.</summary>
    private static JsonNode Resolve(JsonNode? root, List<PathSegment> path)
    {
        var node = root;
        foreach (var segment in path)
        {
            switch (node)
            {
                case JsonArray array:
                    if (!segment.IsIndex) throw new UnsafePathError(segment.ToString());
                    if (segment.Index >= array.Count) throw new PathError(Text(path));
                    node = array[(int)segment.Index];
                    break;
                case JsonObject value:
                    if (!value.TryGetPropertyValue(segment.ToString(), out var child)) throw new PathError(Text(path));
                    node = child;
                    break;
                default:
                    throw new PathError(Text(path));
            }
        }
        return node is JsonObject or JsonArray ? node : throw new PathError(Text(path));
    }

    /// <summary>The own property <paramref name="key"/> of <paramref name="parent"/>, or null when it has none.</summary>
    private static JsonNode? Read(JsonNode parent, PathSegment key) => parent switch
    {
        JsonArray array => key.Index < array.Count ? array[(int)key.Index] : null,
        _ => ((JsonObject)parent).TryGetPropertyValue(key.ToString(), out var value) ? value : null,
    };

    /// <summary>Sets <paramref name="key"/> of <paramref name="parent"/> (an index at most one past an array's end appends).</summary>
    private static void Write(JsonNode parent, PathSegment key, JsonNode? value)
    {
        if (parent is JsonArray array)
        {
            if (key.Index == array.Count) array.Add(value);
            else array[(int)key.Index] = value;
        }
        else ((JsonObject)parent)[key.ToString()] = value;
    }

    /// <summary>JavaScript's <c>splice(index, remove, ...items)</c>: the start and the count clamped to the array.</summary>
    private static void Splice(JsonArray array, long index, long remove, JsonArray items)
    {
        var start = (int)Math.Min(index, array.Count);
        var removed = (int)Math.Min(remove, array.Count - start);
        for (var count = 0; count < removed; count++) array.RemoveAt(start);
        for (var offset = 0; offset < items.Count; offset++) array.Insert(start + offset, items[offset]?.DeepClone());
    }

    private static double? Number(JsonNode? node) => ChordJson.AsNumber(node);

    private static bool IsNonNegativeInteger(JsonNode? node) => Number(node) is { } number && double.IsInteger(number) && number >= 0;

    /// <summary>A path as <c>JSON.stringify</c> writes it, for <see cref="PathError"/>.</summary>
    private static string Text(List<PathSegment> path) => ChordJson.Stringify(new JsonArray([.. path.Select(segment => segment.ToJson())]));
}
