// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/index.ts (Seg, Path, Op, isReplace, isBase, overlap,
// ownedSlice, RESERVED_SEGMENTS)
//
// Delta's operations as Chord v2 holds them (area cv2-state): the subset the state layer's watches and derives need, beside
// PiSharp.Durable's port of the same file (PiSharp.Durable.Chord.Delta), which works on Durable's JsonValue. Chord v2 references no Durable
// project and its values are JsonNode trees (Json.cs), so it has its own copy, as decision 0013 has it for ChordContext and watch.ts; the
// adapter (PiSharp.Durable.Store) converts between them, and phase 6 decides whether the copies merge. The records, their names and their
// JSON tuples are Durable's. An operation's values are the nodes of the revision it was computed from, held by reference (a JsonNode has
// one parent, so an op never adopts them): ToJson copies them. Wire ops, the encoder and decoder, validation, apply and the tracker are not
// ported here; the documents areas bring what they need.
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Delta;

/// <summary>One path segment: an object key or an array index (upstream <c>Seg</c>, <c>string | number</c>).</summary>
public readonly record struct PathSegment
{
    private PathSegment(string? key, long index)
    {
        Key = key;
        Index = index;
    }

    /// <summary>The object key, or null for an index.</summary>
    public string? Key { get; }

    /// <summary>The array index.</summary>
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
    public JsonNode ToJson() => Key is { } key ? JsonValue.Create(key) : JsonValue.Create(Index);

    /// <inheritdoc/>
    public override string ToString() => Key ?? Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Path helpers (upstream <c>Path</c> is <c>readonly Seg[]</c>).</summary>
public static class DeltaPath
{
    /// <summary>A path from segments.</summary>
    public static ImmutableArray<PathSegment> Of(params PathSegment[] segments) => [.. segments];

    /// <summary>The path as a JSON array.</summary>
    public static JsonArray ToJson(ImmutableArray<PathSegment> path) => [.. path.Select(segment => segment.ToJson())];

    /// <summary><c>JSON.stringify(path)</c>, the key upstream indexes paths by.</summary>
    public static string Key(ImmutableArray<PathSegment> path) => ChordJson.Stringify(ToJson(path));
}

/// <summary>
/// One operation (upstream <c>Op</c>, a tuple in memory, on the wire and on disk): <c>["r", value]</c>, <c>["s", path, value]</c>,
/// <c>["d", path]</c>, <c>["a", path, text]</c>, <c>["t", path, count]</c>, <c>["p", path, index, remove, items]</c> and
/// <c>["m", path, permutation]</c>. <c>r</c> is the only operation that replaces a whole value; <c>s/d/a/t</c> cannot target the root,
/// <c>p</c> and <c>m</c> may.
/// </summary>
public abstract record Op
{
    private protected Op()
    {
    }

    /// <summary>The verb: <c>r s d a t p m</c>.</summary>
    public abstract string Verb { get; }

    /// <summary>The operation as its JSON tuple; its values are copies, as a JsonNode has one parent.</summary>
    public abstract JsonArray ToJson();

    /// <summary>The tuple's JSON text, as JavaScript writes it.</summary>
    public override string ToString() => ChordJson.Stringify(ToJson());
}

/// <summary>An operation addressed by a path.</summary>
public abstract record PathOp(ImmutableArray<PathSegment> Path) : Op
{
    private protected JsonArray Tuple(params JsonNode?[] rest) => [Verb, DeltaPath.ToJson(Path), .. rest];
}

/// <summary><c>["r", value]</c>: replace the whole value.</summary>
public sealed record ReplaceOp(JsonNode? Value) : Op
{
    /// <inheritdoc/>
    public override string Verb => "r";

    /// <inheritdoc/>
    public override JsonArray ToJson() => ["r", Value?.DeepClone()];
}

/// <summary><c>["s", path, value]</c>: set a property or an array element.</summary>
public sealed record SetOp(ImmutableArray<PathSegment> Path, JsonNode? Value) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "s";

    /// <inheritdoc/>
    public override JsonArray ToJson() => Tuple(Value?.DeepClone());
}

/// <summary><c>["d", path]</c>: delete a property, or remove an array element.</summary>
public sealed record DeleteOp(ImmutableArray<PathSegment> Path) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "d";

    /// <inheritdoc/>
    public override JsonArray ToJson() => Tuple();
}

/// <summary><c>["a", path, text]</c>: append to a string.</summary>
public sealed record AppendOp(ImmutableArray<PathSegment> Path, string Text) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "a";

    /// <inheritdoc/>
    public override JsonArray ToJson() => Tuple(JsonValue.Create(Text));
}

/// <summary><c>["t", path, count]</c>: drop the first <c>count</c> code units of a string.</summary>
public sealed record TruncateOp(ImmutableArray<PathSegment> Path, long Count) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "t";

    /// <inheritdoc/>
    public override JsonArray ToJson() => Tuple(JsonValue.Create(Count));
}

/// <summary><c>["p", path, index, remove, items]</c>: splice an array.</summary>
public sealed record SpliceOp(ImmutableArray<PathSegment> Path, long Index, long Remove, ImmutableArray<JsonNode?> Items) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "p";

    /// <inheritdoc/>
    public override JsonArray ToJson() => Tuple(JsonValue.Create(Index), JsonValue.Create(Remove), new JsonArray([.. Items.Select(item => item?.DeepClone())]));
}

/// <summary><c>["m", path, permutation]</c>: reorder an array in place, <c>new[i] = old[permutation[i]]</c>.</summary>
public sealed record MoveOp(ImmutableArray<PathSegment> Path, ImmutableArray<int> Permutation) : PathOp(Path)
{
    /// <inheritdoc/>
    public override string Verb => "m";

    /// <inheritdoc/>
    public override JsonArray ToJson() => Tuple(new JsonArray([.. Permutation.Select(index => (JsonNode)JsonValue.Create(index))]));
}

/// <summary>Classification and the string helpers of <c>delta/index.ts</c>.</summary>
public static class DeltaOps
{
    /// <summary>Object keys a diff never addresses by path (upstream <c>RESERVED_SEGMENTS</c>).</summary>
    public static readonly ImmutableHashSet<string> ReservedSegments = ImmutableHashSet.Create(StringComparer.Ordinal, "__proto__", "constructor", "prototype");

    /// <summary>Whether <paramref name="op"/> replaces the whole value (<c>isReplace</c>).</summary>
    public static bool IsReplace(Op op) => op is ReplaceOp;

    /// <summary>Whether a batch starts with a replacement (<c>isBase</c>).</summary>
    public static bool IsBase(IReadOnlyList<Op> ops) => ops.Count > 0 && ops[0] is ReplaceOp;

    /// <summary>
    /// The longest suffix of <paramref name="a"/> that is a prefix of <paramref name="b"/>, looking at most <paramref name="scan"/> code
    /// units back (<c>overlap</c>). Candidates are bounded; giving up returns 0, which emits a set: larger, never wrong.
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
}
