// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/writable.ts
//
// Writables (slice 8 §2, design §17): `self.writable(initial)`, an in-process value its attempt sets, identity compared
// (StateInternal.Identical); a contribution, a derive source and an `on` source, never a state member. Mappings: Writable<T> is the public
// IWritable<T>, WritableOwner the internal IWritableOwner (the attempt's context and seal check), and the ownership union a field holding
// the owner or the error its attempt ended with. Upstream makes the kept error's stack text so it holds no frame's objects; a .NET stack
// trace holds none (StateInternal.HandedOut).
using System.Runtime.ExceptionServices;

namespace PiSharp.Chord.State;

/// <summary>An in-process value that changes when its owner sets it (upstream <c>Writable</c>); setting the same value notifies nobody.</summary>
/// <typeparam name="T">The value type; values are compared by identity.</typeparam>
public interface IWritable<T> : INativeWatchable<T>
{
    /// <summary>
    /// Sets the value: a new value (by identity) is a new frame for its watches and marks the derives and registry tables reading it, in the
    /// batch running now or in one of its own. Throws <c>released</c> once its attempt ended, and <c>invalid/reentrant</c> inside a derive
    /// compute or a <c>compose</c>.
    /// </summary>
    void Set(T value);
}

/// <summary>Who owns a writable (upstream <c>WritableOwner</c>): the context its own batches run in, and the check that its attempt still
/// runs.</summary>
internal interface IWritableOwner
{
    /// <summary>The context the writable's own commits run in.</summary>
    ChordContext Context { get; }

    /// <summary>Throws <c>released</c> once the attempt that made it was sealed.</summary>
    void AssertUsable();
}

/// <summary>A writable (upstream <c>WritableSource</c>): its value, set by its owner's code, delivered through the host's batch.</summary>
internal sealed class WritableSource<T> : FollowedSource<T>, IWritable<T>
{
    private T value;
    // Whom it belongs to: its running attempt, or nothing once that attempt ended (its error kept).
    private IWritableOwner? owner;
    private Exception? ended;

    public WritableSource(T initial, IWritableOwner owner, Batch batch)
        : base(batch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(batch);
        value = initial;
        this.owner = owner;
    }

    /// <inheritdoc/>
    public override T Value => value;

    /// <inheritdoc/>
    public void Set(T next)
    {
        if (ended is not null) ExceptionDispatchInfo.Throw(ended);
        var current = owner!;
        current.AssertUsable();
        // The cost guard: setting the value it has costs one comparison past the seal check.
        if (StateInternal.Identical(next, value)) return;
        var batch = Batch!;
        batch.AssertNotComputing("writable.set");
        Change(next, batch, current.Context);
    }

    /// <summary>Its attempt ended with <paramref name="error"/>: its watches end <c>ended</c>, <see cref="Set"/> throws
    /// <paramref name="error"/>, its value stays readable; it keeps neither its attempt nor the host.</summary>
    public void Detach(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ended = StateInternal.HandedOut(error);
        owner = null;
        End();
    }

    /// <summary>Adopts <paramref name="next"/> in the batch running now, or in one of its own. Apart from <see cref="Set"/>: a closure there
    /// would make every set allocate, the same value's too.</summary>
    private void Change(T next, Batch batch, ChordContext context) => batch.Within(() =>
    {
        value = next;
        Changed();
    }, context);
}
