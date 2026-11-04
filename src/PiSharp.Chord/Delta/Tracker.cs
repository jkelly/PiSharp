// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/tracker.ts (track, Tracker, Prepared; in part)
//
// Delta's tracker as Chord v2's documents hold it (area cv2-documents-core): a tracked root (track, Tracker.value) and the shape of a
// prepared change (Prepared: base, value, ops, baseRevision, abort), which the documents' residents and address sources take. Like Ops.cs,
// this is Chord v2's own copy beside PiSharp.Durable's port of the same file (PiSharp.Durable.Chord.Delta's Tracker, on Durable's
// JsonValue): a root is a JsonObject or a JsonArray, held by reference and never copied, and immutable by convention once tracked. Not
// ported here: revision, beginChange and its Change (the draft, prepare and abort), adopt, prepareReplace and appendText, which come with
// the transactions that draft documents (area cv2-documents-slots). Until then a Prepared is made by Chord's own layers (its constructor is
// internal), as upstream's tests build one by hand.
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Delta;

/// <summary>Takes ownership of JSON roots (upstream <c>track</c>).</summary>
public static class Tracker
{
    /// <summary>A tracker of <paramref name="initial"/>, an object or array root, taken in O(1): the root is never traversed or copied.</summary>
    public static Tracker<T> Track<T>(T initial)
        where T : JsonNode
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial is not (JsonObject or JsonArray)) throw new ArgumentException("A tracked root must be an object or an array.", nameof(initial));
        return new Tracker<T>(initial);
    }
}

/// <summary>Owns one revision sequence of a JSON root (upstream <c>Tracker</c>): <see cref="Value"/> is the latest adopted revision, never
/// modified.</summary>
/// <typeparam name="T">The root's type: a <see cref="JsonObject"/> (a document) or a <see cref="JsonArray"/>.</typeparam>
public sealed class Tracker<T>
    where T : JsonNode
{
    internal Tracker(T initial) => Value = initial;

    /// <summary>The latest adopted revision.</summary>
    public T Value { get; }
}

/// <summary>
/// A sealed change of a tracked root (upstream <c>Prepared</c>): the revision it was drafted on (<see cref="Base"/>, at
/// <see cref="BaseRevision"/>), the candidate revision (<see cref="Value"/>) and the operations from one to the other.
/// </summary>
/// <typeparam name="T">The root's type.</typeparam>
public sealed class Prepared<T>
    where T : JsonNode
{
    private readonly Action abort;

    /// <summary>A prepared change of these parts; <paramref name="abort"/> is what <see cref="Abort"/> runs.</summary>
    internal Prepared(T @base, T value, ImmutableArray<Op> ops, long baseRevision, Action abort)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(abort);
        Base = @base;
        Value = value;
        Ops = ops.IsDefault ? [] : ops;
        BaseRevision = baseRevision;
        this.abort = abort;
    }

    /// <summary>The revision the change was drafted on.</summary>
    public T Base { get; }

    /// <summary>The candidate revision.</summary>
    public T Value { get; }

    /// <summary>The operations from <see cref="Base"/> to <see cref="Value"/>; empty for a no-op.</summary>
    public ImmutableArray<Op> Ops { get; }

    /// <summary>The tracker's revision when the change was drafted.</summary>
    public long BaseRevision { get; }

    /// <summary>Gives the change up: it is never adopted.</summary>
    public void Abort() => abort();
}
