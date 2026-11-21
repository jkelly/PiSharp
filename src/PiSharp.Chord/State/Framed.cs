// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/state/framed.ts
//
// Framed sources: a JSON source whose owner sets its value whole (a live query's rows, a list's pages joined), one frame per batch as the
// diff from the value its watches had, however often the batch set it (a list sets once per page a commit touched); it may fail and
// recover, as a derive does (design §11). Mappings:
// - FramedOwner is IFramedOwner; its report gets the error as it is (an Exception: .NET throws nothing else), which the owner wraps for
//   its host (a live query's `query_failed`), as upstream's owners do.
// - FramedState is an enum with the value, the error and the last good value beside it; JSON null is a value, so "no last value" is a
//   flag (hasLast), as WatchStart keeps it.
// - Upstream's frame is diffRevisions(before, after), which leans on JavaScript identity: a subclass that reuses unchanged row objects
//   gets a narrow diff for free. A JsonNode has one parent, so two values of a framed source share no node, and the default diff here is
//   the structural one a derive's is (Delta/Diff.cs). A subclass that can do better overrides Diff. The contract of that seam:
//   1. Every value given to Set is a tree of its own: no node of it is, or later becomes, part of another value given to Set. A subclass
//      that carries rows over from one value to the next copies them (DeepClone) or builds them again; it never moves them.
//   2. A value given to Set is never changed afterwards. The source keeps it by reference as what its watches have, hands it to watches
//      and subscribers by reference, and passes it back as `before`.
//   3. Diff(before, after) is called by Deliver, at most once per batch, only when the batch left a value and the watches had one. Both
//      arguments are references the subclass gave to Set, unchanged: `after` the last one of the batch, `before` the one that was current
//      when the batch first changed the source while it was watched. A subclass may therefore key its own bookkeeping (row tokens, decoded
//      rows) by these references, weakly, and compute the ops from that instead of walking the trees.
//   4. The ops it returns turn `before` into a value deeply equal to `after` when applied (DeltaApply), in the order given; an empty
//      array means the batch ended where it began and no frame is published. It must not throw for values it was given: a throw is
//      reported by the batch as a failed delivery and the watches miss the frame.
// - Threads: as every source, a framed source belongs to its batch's host and is not thread-safe (Internal.cs). Set, FailWith, Quiet and
//   End are called inside the batch (Batch.Commit or Batch.Within, which moves to the owner's thread when the host installed one); a
//   store that finishes a read on another thread goes through Batch.Within before it sets the source. Deliver and Diff run inside the same
//   commit, on the same thread.
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.State;

/// <summary>Who a framed source belongs to (upstream <c>FramedOwner</c>): named in errors, and told its failures after its first value
/// (once per failure episode).</summary>
internal interface IFramedOwner
{
    /// <summary>The owner's name in errors (<c>not_ready</c>: "<c>label</c>: read before its first value").</summary>
    string Label { get; }

    /// <summary>A failure of the source, once per failure episode (a value ends the episode).</summary>
    void Report(Exception error);
}

/// <summary>
/// A source its subclass feeds with <see cref="Set"/> and <see cref="FailWith"/>, inside a batch, as often as it likes (upstream
/// <c>FramedSource</c>): the batch delivers one frame, of the state it ended in. A failure makes it a failed member (as a throwing derive):
/// its watches get one failure call, its owner one report per failure episode; the next value replaces the value whole
/// (<c>[["r", value]]</c>).
/// </summary>
internal abstract class FramedSource : Source
{
    private readonly IFramedOwner owner;
    // Where it stands (upstream FramedState): before its first value, with a value, failed (its last good value kept for a watch opened
    // meanwhile), or detached (its owner ended).
    private FramedState state = FramedState.Loading;
    // The value; while failed, the last good value when hasLast.
    private JsonNode? value;
    private bool hasLast;
    // The failure while failed, the owner's error once detached.
    private Exception? error;
    // The value its watches have, kept from its first change in a batch to that batch's frame (upstream #framed); None: not listed in
    // the batch.
    private Framed framed = Framed.None;
    private JsonNode? framedValue;
    // Set from a failure until the next value: a failure episode is reported once.
    private bool failing;

    private enum FramedState
    {
        Loading,
        Value,
        Failed,
        Detached,
    }

    // Upstream's #framed: undefined (not listed), NO_VALUE (its watches had no value: a failure, or none yet) or the value.
    private enum Framed
    {
        None,
        NoValue,
        Value,
    }

    protected FramedSource(Batch batch, IFramedOwner owner)
        : base(batch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.owner = owner;
    }

    /// <summary>Whether it has a value.</summary>
    public bool Ready => state == FramedState.Value;

    /// <summary>Its failure while it is failed; null with a value, before its first one and once detached.</summary>
    public Exception? Failure => state == FramedState.Failed ? error : null;

    /// <summary>Whether its owner ended.</summary>
    public bool Detached => state == FramedState.Detached;

    /// <inheritdoc/>
    public override JsonNode? Value
    {
        get
        {
            switch (state)
            {
                case FramedState.Value:
                    return value;
                case FramedState.Loading:
                    throw new ChordError("not_ready", $"{owner.Label}: read before its first value");
                default:
                    // Every read throws the same error object, as upstream's `throw state.error` does.
                    ExceptionDispatchInfo.Throw(error!);
                    return null;
            }
        }
    }

    /// <summary>Its value, or while failed its failure and last good value (a detached one throws).</summary>
    public override WatchStart Start() =>
        state == FramedState.Failed ? WatchStart.Failure(error!, hasLast, value) : WatchStart.Of(Value);

    /// <summary>
    /// Delivers the batch's one frame, of the state the batch left: the diff from the value its watches had before the batch, or one
    /// failure call when they had a value.
    /// </summary>
    public override void Deliver(ChordContext context)
    {
        var before = framed;
        var beforeValue = framedValue;
        framed = Framed.None;
        framedValue = null;
        // Listed by a change, which kept what the watches had; a detach in the batch leaves nothing to deliver.
        if (before == Framed.None) return;
        if (state == FramedState.Value)
        {
            var after = value;
            ImmutableArray<Op> ops = before == Framed.NoValue ? [new ReplaceOp(after)] : Diff(beforeValue, after);
            if (ops.Length > 0) Publish(after, ops, false, context);
            return;
        }
        // Still failed since the last call: nothing new for the watches.
        if (state == FramedState.Failed && before != Framed.NoValue) FailWatches(error!, context);
    }

    /// <summary>
    /// The ops of a frame: from <paramref name="before"/>, the value the watches have, to <paramref name="after"/>, the value the batch
    /// left. Both are values this source was given with <see cref="Set"/>, by reference. The default is the structural diff
    /// (<see cref="DeltaDiff.DiffRevisions"/>); a subclass that knows which parts changed returns them without walking the trees. The
    /// contract is in the file comment: applied to <paramref name="before"/> the ops give a value deeply equal to
    /// <paramref name="after"/>, and no ops mean no frame.
    /// </summary>
    protected virtual ImmutableArray<Op> Diff(JsonNode? before, JsonNode? after) => DeltaDiff.DiffRevisions(before, after);

    /// <summary>Its next failure is not reported: whoever awaits its first value reports that failure itself.</summary>
    protected void Quiet() => failing = true;

    /// <summary>Takes <paramref name="next"/>, inside a batch: listed for the batch's frame when watched, its dependents marked. The value
    /// is a tree of its own and is not changed afterwards (the file comment's contract).</summary>
    protected void Set(JsonNode? next)
    {
        if (state == FramedState.Detached) return;
        failing = false;
        Change(FramedState.Value, next, true, null);
    }

    /// <summary>Fails with <paramref name="failure"/>, inside a batch, keeping its last good value for a watch opened meanwhile.</summary>
    protected void FailWith(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (state == FramedState.Detached) return;
        if (!failing) owner.Report(StateInternal.HandedOut(failure));
        failing = true;
        // The last good value: the value it has, or the one a failure before this one kept.
        var keeps = state == FramedState.Value || (state == FramedState.Failed && hasLast);
        Change(FramedState.Failed, keeps ? value : null, keeps, failure);
    }

    /// <summary>Its owner ended: reads throw <paramref name="ended"/>, its watches end <c>ended</c>.</summary>
    protected void End(Exception ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        state = FramedState.Detached;
        value = null;
        hasLast = false;
        error = ended;
        framed = Framed.None;
        framedValue = null;
        EndWatches();
    }

    /// <summary>
    /// Its state becomes the given one in the batch running now. The first change of a batch lists it for that batch's frame and keeps what
    /// its watches have, which the frame diffs from; a later one lists nothing (a source listed twice delivers twice: two frames of one
    /// member in one commit, which a link refuses). Every change marks its dependents: one may have read the value since the last.
    /// </summary>
    private void Change(FramedState next, JsonNode? nextValue, bool nextHasLast, Exception? nextError)
    {
        if (Watched && framed == Framed.None)
        {
            framed = state == FramedState.Value ? Framed.Value : Framed.NoValue;
            framedValue = state == FramedState.Value ? value : null;
            Batch.Changed(this);
        }
        state = next;
        value = nextValue;
        hasLast = nextHasLast;
        error = nextError;
        foreach (var dependent in Dependents.Items) dependent.Invalidate();
    }
}
