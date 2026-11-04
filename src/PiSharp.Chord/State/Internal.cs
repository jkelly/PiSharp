// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/internal.ts, which only re-exports: state for Chord's
// layers. Its exports are internal types of PiSharp.Chord.State: Batch and BatchError (with its BatchErrorKind) and IDelivered (batch.ts),
// Derive (derive.ts; DeriveSource is a Source or a Constant and DeriveInput those or a NativeSource, both plain objects here),
// FollowedSource, NativeSource and NativeStart (native-source.ts; NativeObservation and INativeObserver are public, for apps), Constant,
// IDependent, Source, ISubscriber and WatchStart (watchable.ts) and IWritableOwner and WritableSource (writable.ts); handedOut is
// StateInternal.HandedOut. StateInternal also holds the two helpers every file of the layer shares: JavaScript's identity on in-process
// values (Identical) and the follower set (Followers).
//
// Threads: upstream's state runs on one thread. A Batch, and the sources, derives and writables on it, belong to their host, which
// serializes the calls that reach them (phase 6 decides how, as a Durable Session serializes on its line); only the watch handles they make
// are safe on any thread (Watch.cs). A watch may end on another thread (StopAsync, its signal's cancellation), which unfollows it from its
// source, so the sets of watches, subscribers, observers and dependents are copy-on-write (Followers): adding or removing takes a short
// lock and makes a new array, and a batch walks the array it read, unlocked and without allocating, as upstream walks a Set that a delete
// may shrink.

namespace PiSharp.Chord.State;

/// <summary>State for Chord's layers (upstream <c>state/internal.ts</c>): the helpers the layer's files share.</summary>
internal static partial class StateInternal
{
    /// <summary>
    /// An error Chord hands to apps (a failure call, a report), as it is (upstream <c>handedOut</c>). Upstream makes a V8 error's stack text
    /// first, as until then the stack keeps its frames' receivers, and so the host behind the throw, alive; a .NET exception's stack trace
    /// keeps methods, never receivers, so there is nothing to drop.
    /// </summary>
    internal static T HandedOut<T>(T error) => error;

    /// <summary>
    /// JavaScript's <c>a === b</c> on in-process values: a value type or a string by value, anything else by reference (a record's own
    /// equality is not identity). Writables and followed sources compare their values with it.
    /// </summary>
    internal static bool Identical<T>(T a, T b)
    {
        if (typeof(T).IsValueType) return EqualityComparer<T>.Default.Equals(a, b);
        object? left = a, right = b;
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return (left is string || left.GetType().IsValueType) && left.GetType() == right.GetType() && left.Equals(right);
    }
}

/// <summary>
/// A set that keeps insertion order (a JavaScript <c>Set</c>), copy-on-write: <see cref="Add"/> and <see cref="Remove"/> take a short lock
/// and replace the array, and <see cref="Items"/> is the array as it is now, which a later change leaves as it was.
/// </summary>
internal sealed class Followers<T>
    where T : class
{
    private readonly Lock gate = new();
    private T[] items = [];

    /// <summary>The members now, in insertion order.</summary>
    public T[] Items => Volatile.Read(ref items);

    /// <summary>How many members there are.</summary>
    public int Count => Items.Length;

    /// <summary>Adds <paramref name="item"/> at the end; nothing when it is a member already.</summary>
    public void Add(T item)
    {
        lock (gate)
        {
            if (IndexOf(items, item) >= 0) return;
            Volatile.Write(ref items, [.. items, item]);
        }
    }

    /// <summary>Removes <paramref name="item"/>; false when it was no member.</summary>
    public bool Remove(T item)
    {
        lock (gate)
        {
            var at = IndexOf(items, item);
            if (at < 0) return false;
            var next = new T[items.Length - 1];
            Array.Copy(items, next, at);
            Array.Copy(items, at + 1, next, at, items.Length - at - 1);
            Volatile.Write(ref items, next);
            return true;
        }
    }

    /// <summary>Whether <paramref name="item"/> is a member.</summary>
    public bool Contains(T item) => IndexOf(Items, item) >= 0;

    // Members are compared by reference: an observer may be a record with an equality of its own.
    private static int IndexOf(T[] members, T item)
    {
        for (var index = 0; index < members.Length; index++)
            if (ReferenceEquals(members[index], item)) return index;
        return -1;
    }

    /// <summary>Removes every member and returns them, in order.</summary>
    public T[] Clear()
    {
        lock (gate)
        {
            var all = items;
            Volatile.Write(ref items, []);
            return all;
        }
    }
}

/// <summary>Reference identity for the visited set of a retirement walk (upstream's <c>Set&lt;Dependent&gt;</c>).</summary>
internal sealed class ReferenceSet<T> : HashSet<T>
    where T : class
{
    public ReferenceSet()
        : base(ReferenceEqualityComparer.Instance)
    {
    }
}
