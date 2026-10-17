// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/index.ts (operations, classification, overlap, path
// safety and validation).
using System.Collections.Immutable;
using System.Globalization;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>One path segment: an object key or an array index. Upstream segments are <c>string | number</c>.</summary>
public readonly record struct PathSegment
{
    private PathSegment(string? key, long index) { Key = key; Index = index; }
    /// <summary>The object key, or null for an index.</summary>
    public string? Key { get; }
    /// <summary>The array index (any integer; validation decides whether it is safe).</summary>
    public long Index { get; }
    /// <summary>Whether this is an array index.</summary>
    public bool IsIndex => Key is null;
    /// <summary>An object key.</summary>
    public static PathSegment Of(string key) => new(key ?? throw new ArgumentNullException(nameof(key)), 0);
    /// <summary>An array index.</summary>
    public static PathSegment Of(long index) => new(null, index);
    /// <summary>Segments convert from keys and indices.</summary>
    public static implicit operator PathSegment(string key) => Of(key);
    /// <inheritdoc cref="op_Implicit(string)"/>
    public static implicit operator PathSegment(int index) => Of(index);
    /// <inheritdoc cref="op_Implicit(string)"/>
    public static implicit operator PathSegment(long index) => Of(index);
    /// <summary>The segment as JSON: a string or a number.</summary>
    public JsonValue ToJson() => Key is { } key ? JsonValue.String(key) : JsonValue.Number(Index);
    /// <inheritdoc/>
    public override string ToString() => Key ?? Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Path helpers.</summary>
public static class DeltaPath
{
    /// <summary>A path from segments.</summary>
    public static ImmutableArray<PathSegment> Of(params PathSegment[] segments) => [.. segments];
    /// <summary>The path as a JSON array.</summary>
    public static JsonArrayValue ToJson(ImmutableArray<PathSegment> path) => JsonValue.Array(path.Select(segment => segment.ToJson()));
    /// <summary><c>JSON.stringify(path)</c>, the key upstream interns paths by.</summary>
    public static string Key(ImmutableArray<PathSegment> path) => ToJson(path).ToJson();
    /// <summary>Whether two paths are equal segment by segment.</summary>
    public static bool Equal(ImmutableArray<PathSegment> left, ImmutableArray<PathSegment> right) => left.AsSpan().SequenceEqual(right.AsSpan());
}

/// <summary>
/// One operation. Tuples are the form in memory, on the wire and on disk: <c>["r", value]</c>, <c>["s", path, value]</c>,
/// <c>["d", path]</c>, <c>["a", path, text]</c>, <c>["t", path, count]</c>, <c>["p", path, index, remove, items]</c> and
/// <c>["m", path, permutation]</c>. <c>r</c> is the only operation that replaces a whole value; <c>s/d/a/t</c> cannot target the root,
/// <c>p</c> and <c>m</c> may. Operation shape is not canonical: an array may be emptied by a replacement or by a root splice.
/// </summary>
public abstract record Op
{
    private protected Op() { }
    /// <summary>The verb: <c>r s d a t p m</c>.</summary>
    public abstract string Verb { get; }
    /// <summary>The operation as its JSON tuple.</summary>
    public abstract JsonArrayValue ToJson();
    /// <summary>The operation of a decoded JSON tuple; throws as <see cref="DeltaValidation.AssertValidOp"/> does.</summary>
    public static Op FromJson(JsonValue tuple)
    {
        DeltaValidation.AssertValidOp(tuple);
        var items = (JsonArrayValue)tuple;
        var verb = ((JsonStringValue)items[0]).Value;
        if (verb == "r") return new ReplaceOp(items[1]);
        var path = DeltaValidation.ReadPath(items[1]);
        return verb switch
        {
            "s" => new SetOp(path, items[2]),
            "d" => new DeleteOp(path),
            "a" => new AppendOp(path, ((JsonStringValue)items[2]).Value),
            "t" => new TruncateOp(path, (long)((JsonNumberValue)items[2]).Value),
            "p" => new SpliceOp(path, (long)((JsonNumberValue)items[2]).Value, (long)((JsonNumberValue)items[3]).Value, ((JsonArrayValue)items[4]).Items),
            _ => new MoveOp(path, [.. ((JsonArrayValue)items[2]).Select(item => (int)((JsonNumberValue)item).Value)])
        };
    }
    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>An operation addressed by a path.</summary>
public abstract record PathOp(ImmutableArray<PathSegment> Path) : Op
{
    private protected JsonArrayValue Tuple(params JsonValue[] rest) => JsonValue.Array([JsonValue.String(Verb), DeltaPath.ToJson(Path), .. rest]);
}

/// <summary><c>["r", value]</c>: replace the whole value.</summary>
public sealed record ReplaceOp(JsonValue Value) : Op
{
    /// <inheritdoc/>
    public override string Verb => "r";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => JsonValue.Array(JsonValue.String("r"), Value);
}

/// <summary><c>["s", path, value]</c>: set a property or an array element (an index may append exactly one past the end).</summary>
public sealed record SetOp(ImmutableArray<PathSegment> Path, JsonValue Value) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "s";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => Tuple(Value);
}

/// <summary><c>["d", path]</c>: delete a property, or remove an array element.</summary>
public sealed record DeleteOp(ImmutableArray<PathSegment> Path) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "d";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => Tuple();
}

/// <summary><c>["a", path, text]</c>: append to a string.</summary>
public sealed record AppendOp(ImmutableArray<PathSegment> Path, string Text) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "a";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => Tuple(JsonValue.String(Text));
}

/// <summary><c>["t", path, count]</c>: drop the first <c>count</c> code units of a string.</summary>
public sealed record TruncateOp(ImmutableArray<PathSegment> Path, long Count) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "t";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => Tuple(JsonValue.Number(Count));
}

/// <summary><c>["p", path, index, remove, items]</c>: splice an array (removal past the end is clamped).</summary>
public sealed record SpliceOp(ImmutableArray<PathSegment> Path, long Index, long Remove, ImmutableArray<JsonValue> Items) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "p";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => Tuple(JsonValue.Number(Index), JsonValue.Number(Remove), new JsonArrayValue(Items));
}

/// <summary><c>["m", path, permutation]</c>: reorder an array in place, <c>new[i] = old[permutation[i]]</c>.</summary>
public sealed record MoveOp(ImmutableArray<PathSegment> Path, ImmutableArray<int> Permutation) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "m";
    /// <inheritdoc/>
    public override JsonArrayValue ToJson() => Tuple(JsonValue.Array(Permutation.Select(index => (JsonValue)index)));
}

/// <summary>A path segment that reaches the prototype chain, a non-integer or negative index, or an index out of range.</summary>
public sealed class UnsafePathError(PathSegment segment) : Exception($"unsafe path segment: {segment}")
{
    /// <summary>The offending segment.</summary>
    public PathSegment Segment { get; } = segment;
}

/// <summary>A path (or path id) that does not resolve.</summary>
public sealed class PathError : Exception
{
    /// <summary>For a path.</summary>
    public PathError(ImmutableArray<PathSegment> path) : base($"unresolvable path: {DeltaPath.Key(path)}") { Path = path; }
    /// <summary>For a path id.</summary>
    public PathError(long id) : base($"unresolvable path: {id.ToString(CultureInfo.InvariantCulture)}") { Id = id; }
    /// <summary>The path, if it was one.</summary>
    public ImmutableArray<PathSegment> Path { get; }
    /// <summary>The path id, if it was one.</summary>
    public long? Id { get; }
}

/// <summary>Classification, overlap and validation of operations and wire operations.</summary>
public static class DeltaValidation
{
    /// <summary>Segments that reach a JavaScript prototype chain; rejected in every path so either side can apply the other's operations.</summary>
    public static readonly ImmutableHashSet<string> ReservedSegments = ImmutableHashSet.Create(StringComparer.Ordinal, "__proto__", "constructor", "prototype");

    /// <summary>Whether an operation replaces the whole value.</summary>
    public static bool IsReplace(Op op) => op is ReplaceOp;
    /// <summary>A batch begins with a replacement (flush puts <c>r</c> at index 0 or nowhere, so this is exact).</summary>
    public static bool IsBase(IReadOnlyList<Op> ops) => ops.Count > 0 && ops[0] is ReplaceOp;
    /// <summary>The same, for wire tuples.</summary>
    public static bool IsBase(IReadOnlyList<JsonValue> wire) => wire.Count > 0 && wire[0] is JsonArrayValue { Count: > 0 } tuple && tuple[0] is JsonStringValue { Value: "r" };

    /// <summary>
    /// The longest suffix of <paramref name="a"/> that is a prefix of <paramref name="b"/>, looking at most <paramref name="scan"/>
    /// code units back. Repetitive output can match a head at thousands of positions, so candidates are bounded; giving up returns 0,
    /// which emits a set: larger, never wrong.
    /// </summary>
    public static int Overlap(string a, string b, int scan, int probe = 64, int maxCandidates = 8)
    {
        if (a.Length == 0 || b.Length == 0 || scan == 0) return 0;
        var tail = a.Length > scan ? a[(a.Length - scan)..] : a;
        foreach (var h in new[] { Math.Min(probe, b.Length), 1 })
        {
            var head = b[..h];
            var tried = 0;
            for (var k = tail.IndexOf(head, StringComparison.Ordinal); k != -1; k = tail.IndexOf(head, k + 1, StringComparison.Ordinal))
            {
                if (++tried > maxCandidates) break;
                var n = tail.Length - k;
                if (n <= b.Length && string.CompareOrdinal(tail, k, b, 0, n) == 0) return n;
            }
            if (h == 1) break;
        }
        return 0;
    }

    /// <summary>Verb, arity and payload shape of a decoded operation tuple (paths inline, no <c>#</c>, no short forms). Payload
    /// values are not inspected.</summary>
    public static void AssertValidOp(JsonValue op)
    {
        if (op is not JsonArrayValue { Count: > 0 } tuple) throw new ArgumentException("op is not a tuple");
        if (tuple[0] is not JsonStringValue { Value: var verb }) throw new ArgumentException("unknown op verb: " + tuple[0]);
        switch (verb)
        {
            case "r": if (tuple.Count != 2) throw new ArgumentException("r arity"); return;
            case "s": if (tuple.Count != 3) throw new ArgumentException("s arity"); AssertPathArg(tuple[1], true); return;
            case "d": if (tuple.Count != 2) throw new ArgumentException("d arity"); AssertPathArg(tuple[1], true); return;
            case "a":
                if (tuple.Count != 3 || tuple[2] is not JsonStringValue) throw new ArgumentException("a shape");
                AssertPathArg(tuple[1], true); return;
            case "t":
                if (tuple.Count != 3 || !IsNonNegativeInteger(tuple[2])) throw new ArgumentException("t shape");
                AssertPathArg(tuple[1], true); return;
            case "p":
                if (tuple.Count != 5) throw new ArgumentException("p arity");
                AssertPathArg(tuple[1]);
                if (!IsNonNegativeInteger(tuple[2])) throw new ArgumentException("p index");
                if (!IsNonNegativeInteger(tuple[3])) throw new ArgumentException("p remove");
                if (tuple[4] is not JsonArrayValue) throw new ArgumentException("p items");
                return;
            case "m":
                if (tuple.Count != 3) throw new ArgumentException("m arity");
                AssertPathArg(tuple[1]); AssertPermutation(tuple[2]); return;
            default: throw new ArgumentException("unknown op verb: " + verb); // a newer producer's op must not vanish silently
        }
    }

    /// <summary>The wire grammar: path ids, <c>#</c> definitions and short forms (which reuse the previous path) are legal here.</summary>
    public static void AssertValidWireOp(JsonValue op)
    {
        if (op is not JsonArrayValue { Count: > 0 } tuple) throw new ArgumentException("op is not a tuple");
        if (tuple[0] is not JsonStringValue { Value: var verb }) throw new ArgumentException("unknown op verb: " + tuple[0]);
        void Ref(JsonValue reference)
        {
            if (reference is JsonNumberValue)
            {
                if (!IsNonNegativeInteger(reference)) throw new ArgumentException("bad path id");
                return;
            }
            if (reference is not JsonArrayValue) throw new ArgumentException("path is not an array"); // a string is not a path
            AssertSafePath(ReadPathUnchecked(reference));
        }
        switch (verb)
        {
            case "r": if (tuple.Count != 2) throw new ArgumentException("r arity"); return;
            case "s": if (tuple.Count == 3) Ref(tuple[1]); else if (tuple.Count != 2) throw new ArgumentException("s arity"); return;
            case "d": if (tuple.Count == 2) Ref(tuple[1]); else if (tuple.Count != 1) throw new ArgumentException("d arity"); return;
            case "a":
                if (tuple.Count == 3) { Ref(tuple[1]); if (tuple[2] is not JsonStringValue) throw new ArgumentException("a value"); }
                else if (tuple.Count == 2) { if (tuple[1] is not JsonStringValue) throw new ArgumentException("a value"); }
                else throw new ArgumentException("a arity");
                return;
            case "t":
                if (tuple.Count == 3) { Ref(tuple[1]); if (!IsNonNegativeInteger(tuple[2])) throw new ArgumentException("t count"); }
                else if (tuple.Count == 2) { if (!IsNonNegativeInteger(tuple[1])) throw new ArgumentException("t count"); }
                else throw new ArgumentException("t arity");
                return;
            case "p":
            {
                var (index, remove, items) = tuple.Count == 5 ? (tuple[2], tuple[3], tuple[4]) : tuple.Count == 4 ? (tuple[1], tuple[2], tuple[3]) : default;
                if (items is null) throw new ArgumentException("p arity");
                if (tuple.Count == 5) Ref(tuple[1]);
                if (!IsNonNegativeInteger(index)) throw new ArgumentException("p index");
                if (!IsNonNegativeInteger(remove)) throw new ArgumentException("p remove");
                if (items is not JsonArrayValue) throw new ArgumentException("p items");
                return;
            }
            case "m":
                if (tuple.Count == 3) Ref(tuple[1]); else if (tuple.Count != 2) throw new ArgumentException("m arity");
                AssertPermutation(tuple[^1]);
                return;
            case "#":
                if (tuple.Count != 3 || !IsNonNegativeInteger(tuple[1]) || tuple[2] is not JsonArrayValue) throw new ArgumentException("# shape");
                AssertSafePath(ReadPathUnchecked(tuple[2]));
                return;
            default: throw new ArgumentException("unknown op verb: " + verb);
        }
    }

    /// <summary>Rejects reserved segments and indices that are not non-negative integers.</summary>
    public static void AssertSafePath(IEnumerable<PathSegment> path)
    {
        foreach (var segment in path)
        {
            if (segment.Key is { } key) { if (ReservedSegments.Contains(key)) throw new UnsafePathError(segment); }
            else if (segment.Index < 0) throw new UnsafePathError(segment);
        }
    }

    internal static ImmutableArray<PathSegment> ReadPath(JsonValue value)
    {
        var path = ReadPathUnchecked(value);
        AssertSafePath(path);
        return path;
    }

    private static void AssertPathArg(JsonValue value, bool nonEmpty = false)
    {
        if (value is not JsonArrayValue array) throw new ArgumentException("path is not an array");
        if (nonEmpty && array.Count == 0) throw new ArgumentException("path is empty");
        AssertSafePath(ReadPathUnchecked(value));
    }

    private static ImmutableArray<PathSegment> ReadPathUnchecked(JsonValue value)
    {
        var builder = ImmutableArray.CreateBuilder<PathSegment>();
        foreach (var segment in (JsonArrayValue)value)
        {
            builder.Add(segment switch
            {
                JsonStringValue key => PathSegment.Of(key.Value),
                JsonNumberValue number when number.Value == Math.Floor(number.Value) && Math.Abs(number.Value) < 9007199254740992 => PathSegment.Of((long)number.Value),
                _ => throw new UnsafePathError(PathSegment.Of(segment.ToJson()))
            });
        }
        return builder.ToImmutable();
    }

    private static void AssertPermutation(JsonValue value)
    {
        if (value is not JsonArrayValue array) throw new ArgumentException("m permutation is not an array");
        var seen = new bool[array.Count];
        foreach (var item in array)
        {
            if (!IsNonNegativeInteger(item) || ((JsonNumberValue)item).Value >= array.Count || seen[(int)((JsonNumberValue)item).Value])
                throw new ArgumentException("m permutation is not a bijection");
            seen[(int)((JsonNumberValue)item).Value] = true;
        }
    }

    private static bool IsNonNegativeInteger(JsonValue value) =>
        value is JsonNumberValue { Value: var number } && number >= 0 && number == Math.Floor(number);
}
