// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/src/types.ts (ReplicatedStateDelivery, ReplicatedState,
// ReplicatedStateSourceFrame, ReplicatedStateSourceAttachment, ReplicatedStateSource, ReplicatedStateSourceOptions). Upstream's
// structural interfaces become C# interfaces with the I prefix (IReplicatedState, IReplicatedStateSource,
// IReplicatedStateSourceAttachment); unsubscribe and dispose functions become IDisposable.
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Chord;

/// <summary>Whether a delivery is a subscription's first value or a later revision.</summary>
public enum ReplicatedStateDeliveryKind
{
    /// <summary>The value current when the subscription started (or, for a replica, when it hydrated).</summary>
    Hydrate,
    /// <summary>A revision published after the subscriber hydrated.</summary>
    Update,
}

/// <summary>How a subscriber received a value: <c>{ kind, sequence }</c>.</summary>
/// <param name="Kind">Hydration or update.</param>
/// <param name="Sequence">The publication sequence of the delivered value (0 for the initial value of a local state).</param>
public readonly record struct ReplicatedStateDelivery(ReplicatedStateDeliveryKind Kind, long Sequence)
{
    /// <summary>A hydration at <paramref name="sequence"/>.</summary>
    public static ReplicatedStateDelivery Hydrate(long sequence) => new(ReplicatedStateDeliveryKind.Hydrate, sequence);
    /// <summary>An update at <paramref name="sequence"/>.</summary>
    public static ReplicatedStateDelivery Update(long sequence) => new(ReplicatedStateDeliveryKind.Update, sequence);
}

/// <summary>A replicated state that can be observed (upstream <c>ReplicatedState</c>).</summary>
/// <typeparam name="T">The immutable value type.</typeparam>
public interface IReplicatedState<T>
{
    /// <summary>
    /// The current immutable value, or <c>default</c> until a replica hydrates. Later revisions never change a value already returned.
    /// </summary>
    T? Value { get; }

    /// <summary>
    /// Subscribes a synchronous listener. Each subscription serializes its callbacks, hydration first. At most 100 deliveries wait
    /// behind a running callback; on overflow only the newest pending delivery is kept, so update sequences may skip. Failures are
    /// reported in isolation and delivery continues. Disposing discards pending work without aborting or joining a running callback.
    /// </summary>
    IDisposable Subscribe(Action<T, DurableContext, ReplicatedStateDelivery> listener);

    /// <summary>
    /// Subscribes an asynchronous listener: the next delivery of this subscription waits for the returned task. A faulted or cancelled
    /// task is reported like a thrown exception. Otherwise as <see cref="Subscribe(Action{T, DurableContext, ReplicatedStateDelivery})"/>.
    /// </summary>
    IDisposable Subscribe(Func<T, DurableContext, ReplicatedStateDelivery, Task> listener);
}

/// <summary>One immutable authoritative revision committed after an attachment snapshot (upstream <c>ReplicatedStateSourceFrame</c>).</summary>
/// <typeparam name="T">The immutable value type.</typeparam>
/// <param name="Cursor">Monotonic source cursor; the first frame after a snapshot must be <c>snapshot.Cursor + 1</c>.</param>
/// <param name="Value">The exact immutable value produced by this commit.</param>
/// <param name="Ops">The exact immutable operation batch that produced <paramref name="Value"/> from the preceding revision. It is
/// published by reference and must not be mutated.</param>
/// <param name="Context">The committing invocation's context.</param>
public sealed record ReplicatedStateSourceFrame<T>(long Cursor, T Value, IReadOnlyList<Op> Ops, DurableContext Context);

/// <summary>The fixed immutable snapshot captured at an attachment boundary.</summary>
/// <typeparam name="T">The immutable value type.</typeparam>
/// <param name="Value">The value at the boundary.</param>
/// <param name="Cursor">The source cursor of <paramref name="Value"/>.</param>
public readonly record struct ReplicatedStateSnapshot<T>(T Value, long Cursor);

/// <summary>One registration on a source (upstream <c>ReplicatedStateSourceAttachment</c>). <see cref="IDisposable.Dispose"/> stops
/// delivery and releases source resources, and must be idempotent.</summary>
/// <typeparam name="T">The immutable value type.</typeparam>
public interface IReplicatedStateSourceAttachment<T> : IDisposable
{
    /// <summary>The fixed immutable snapshot captured at the atomic attachment boundary.</summary>
    ReplicatedStateSnapshot<T> Snapshot { get; }

    /// <summary>
    /// Installs the sole listener and synchronously drains every buffered frame in source commit order. Single use. After it begins,
    /// every new committed frame must also be delivered in order until disposal, including commits made reentrantly while a prior
    /// frame is being delivered.
    /// </summary>
    void Activate(Action<ReplicatedStateSourceFrame<T>> listener);
}

/// <summary>
/// An authoritative immutable revision source (upstream <c>ReplicatedStateSource</c>). <see cref="Attach"/> must synchronously and
/// atomically capture one snapshot and register the attachment to buffer every later committed frame: the snapshot includes every
/// commit before that boundary, buffered frames every commit after it, with no overlap or gap. Chord only publishes these references;
/// it never applies or re-diffs them.
/// </summary>
/// <typeparam name="T">The immutable value type.</typeparam>
public interface IReplicatedStateSource<T>
{
    /// <summary>Captures a snapshot and registers a buffering attachment.</summary>
    IReplicatedStateSourceAttachment<T> Attach();
}

/// <summary>Options for <see cref="ReplicatedState.Attach{T}"/>.</summary>
public sealed class ReplicatedStateSourceOptions
{
    /// <summary>Receives source-contract and listener failures without throwing them into the source. When null, failures go to
    /// <see cref="ReplicatedState.UnhandledError"/>.</summary>
    public Action<Exception>? OnError { get; init; }
}
