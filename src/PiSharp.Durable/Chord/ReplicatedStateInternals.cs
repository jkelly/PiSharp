// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/src/services/state-internals.ts. Upstream hides the exact
// publication stream behind a module-private WeakMap; here it is an internal interface the in-process states implement explicitly,
// so it stays invisible to public callers and a lookup on any other object (a replica, an arbitrary value) finds nothing.
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Chord;

/// <summary>The exact publication stream of a local or attached replicated state (<c>ReplicatedStateInternals</c>).</summary>
/// <typeparam name="T">The immutable value type.</typeparam>
internal interface IReplicatedStateInternals<T>
{
    /// <summary>Atomically captures the immutable value and its matching publication sequence.</summary>
    (T Value, long Sequence) Snapshot();

    /// <summary>
    /// Receives every publication synchronously, in sequence order, with its exact operation batch, before public subscribers see it.
    /// A listener failure is collected by the publisher (thrown from a local commit, reported by an attached state).
    /// </summary>
    IDisposable SubscribeSource(Action<IReadOnlyList<Op>, long, DurableContext> listener);
}

/// <summary><c>getReplicatedStateInternals</c>.</summary>
internal static class ReplicatedStateInternals
{
    /// <summary>The internals of <paramref name="value"/>, or null when it is not a local or attached replicated state.</summary>
    public static IReplicatedStateInternals<T>? Get<T>(object? value) => value as IReplicatedStateInternals<T>;
}
