// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/tracker.ts (track, Tracker, Change, Prepared, the change
// lifecycle; in part)
//
// Delta's tracker as Chord v2's documents hold it: a tracked root, its revision, drafted changes (beginChange, Change: state, prepare,
// abort) and their adoption. Like Ops.cs, this is Chord v2's own copy beside PiSharp.Durable's port of the same file (PiSharp.Durable.Chord
// .Delta's Tracker, on Durable's JsonValue): a root is a JsonObject or a JsonArray, held by reference and never copied when tracked, and
// immutable by convention once tracked. Areas cv2-documents-core (track, value, Prepared's shape) and cv2-documents-slots (the rest the
// transactions use) ported it. Mappings:
// - Upstream's draft is a Proxy overlay that records each write and emits the ops from the writes it saw. A JsonObject cannot be overlaid,
//   so here a change's state is a deep copy of its base that the caller edits in place (the documents' change callbacks get it as their
//   draft); prepare copies the edited state into the candidate revision and computes the ops with diffRevisions (Diff.cs), the base against
//   the candidate. The candidate is a tree of its own: a draft kept past its change edits nothing a tracker holds, and the ops' values are
//   the candidate's nodes (a JsonNode has one parent), as upstream's ops share the prepared value's payloads. A JsonNode revision shares no
//   subtree with another, so the ops are diffRevisions' structural ones, which are upstream's for the edits drafts make (a set, a delete,
//   an append to a string or an array); upstream's dirty-tracking emission (dense regions, permutations of moved containers) has no
//   counterpart. Each change costs two copies of the document, where upstream's costs the touched paths.
// - The lifecycle and its messages are upstream's: a change is open until prepared or aborted, a prepared one is adopted once, an abort
//   after prepare prevents its adoption, and adopting one change makes every other open or prepared change of the tracker stale (checked by
//   revision when it is next used, which upstream's registry of open contexts marks at once). A settled change's state throws "Cannot use a
//   settled overlay" (upstream's TypeError, an InvalidOperationException here); the JsonObject a caller already took cannot be revoked.
// - Not ported: prepareReplace and appendText, which only Chord's job runs call (chord.next-jobs, phase 6). A Prepared made by hand (an
//   upstream test's object literal) uses the internal constructor and belongs to no tracker.
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
/// modified. Changes are drafted with <see cref="BeginChange"/>, sealed with <see cref="Change{T}.Prepare"/> and committed with
/// <see cref="Adopt"/>, a pointer swap.</summary>
/// <typeparam name="T">The root's type: a <see cref="JsonObject"/> (a document) or a <see cref="JsonArray"/>.</typeparam>
public sealed class Tracker<T>
    where T : JsonNode
{
    internal Tracker(T initial) => Value = initial;

    /// <summary>The latest adopted revision.</summary>
    public T Value { get; private set; }

    /// <summary>The number of adoptions so far.</summary>
    public long Revision { get; private set; }

    /// <summary>Opens a draft over <see cref="Value"/>. Several changes may be open at once; adopting one makes the others stale.</summary>
    public Change<T> BeginChange() => new(new ChangeCell<T>(this, Revision, Value));

    /// <summary>
    /// Adopts a prepared change of this tracker at its current revision: <see cref="Value"/> becomes <see cref="Prepared{T}.Value"/>,
    /// <see cref="Revision"/> advances (even for a no-op) and every competing change becomes stale. Throws for a change of another tracker,
    /// one used, aborted or stale already, and one drafted on another revision (which it makes stale).
    /// </summary>
    public void Adopt(Prepared<T> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var cell = prepared.Cell;
        if (cell is null || !ReferenceEquals(cell.Tracker, this)) throw new InvalidOperationException("Prepared change belongs to a different tracker");
        switch (cell.Status)
        {
            case ChangeStatus.Consumed: throw new InvalidOperationException("Prepared change has already been used");
            case ChangeStatus.Aborted: throw new InvalidOperationException("Prepared change has been aborted");
            case ChangeStatus.Stale: throw new InvalidOperationException("Prepared change is stale");
            case ChangeStatus.Prepared: break;
            default: throw new InvalidOperationException("Prepared change is not ready");
        }
        if (cell.BaseRevision != Revision || !ReferenceEquals(Value, prepared.Base))
        {
            cell.Status = ChangeStatus.Stale;
            throw new InvalidOperationException("Prepared change is stale");
        }
        // Materialized at prepare: adoption is a pointer swap, so a storage failure can discard the candidate without touching the tracker.
        Value = prepared.Value;
        cell.Status = ChangeStatus.Consumed;
        Revision++;
    }
}

/// <summary>An open draft over one revision (upstream <c>Change</c>). Settle it with <see cref="Prepare"/> or <see cref="Abort"/>.</summary>
/// <typeparam name="T">The root's type.</typeparam>
public sealed class Change<T>
    where T : JsonNode
{
    private readonly ChangeCell<T> cell;
    // The draft: a copy of the base the caller edits, until the change settles.
    private T? state;
    private bool settled;

    internal Change(ChangeCell<T> cell)
    {
        this.cell = cell;
        // Upstream's overlay costs nothing until written; a JsonNode draft is a copy from the start.
        state = (T)cell.Base.DeepClone();
    }

    /// <summary>The draft: the base's value for the caller to edit in place. Throws once the change settled or went stale.</summary>
    public T State
    {
        get
        {
            if (state is null || cell.Status != ChangeStatus.Open || cell.IsStale) throw new InvalidOperationException("Cannot use a settled overlay");
            return state;
        }
    }

    /// <summary>
    /// Seals the change: the draft is copied into the candidate revision and the operations from the base to it are computed, without
    /// changing the tracker. Empty operations mean the prepared value is the base itself. Throws once settled ("Change has already been
    /// settled"), and for a change gone stale ("Cannot use a settled overlay").
    /// </summary>
    public Prepared<T> Prepare()
    {
        if (settled) throw new InvalidOperationException("Change has already been settled");
        var draft = state;
        if (draft is null || cell.Status != ChangeStatus.Open || cell.IsStale) throw new InvalidOperationException("Cannot use a settled overlay");
        cell.Status = ChangeStatus.Prepared;
        settled = true;
        state = null;
        try
        {
            // The candidate is a tree of its own: the draft stays with whoever holds it, and the ops share the candidate's nodes.
            var candidate = (T)draft.DeepClone();
            var ops = DeltaDiff.DiffRevisions(cell.Base, candidate);
            return new Prepared<T>(cell, ops.Length == 0 ? cell.Base : candidate, ops);
        }
        catch
        {
            cell.Status = ChangeStatus.Aborted;
            throw;
        }
    }

    /// <summary>Discards the change (idempotent). After <see cref="Prepare"/> it prevents the adoption of the prepared result.</summary>
    public void Abort()
    {
        state = null;
        settled = true;
        cell.Abort();
    }
}

/// <summary>
/// A sealed change (upstream <c>Prepared</c>): the revision it was drafted on (<see cref="Base"/>, at <see cref="BaseRevision"/>), the
/// candidate revision (<see cref="Value"/>) and the operations from one to the other.
/// </summary>
/// <typeparam name="T">The root's type.</typeparam>
public sealed class Prepared<T>
    where T : JsonNode
{
    private readonly Action abort;

    /// <summary>A prepared change of these parts made by hand (it belongs to no tracker); <paramref name="abort"/> is what
    /// <see cref="Abort"/> runs.</summary>
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

    /// <summary>A change's prepared result.</summary>
    internal Prepared(ChangeCell<T> cell, T value, ImmutableArray<Op> ops)
    {
        Cell = cell;
        Base = cell.Base;
        Value = value;
        Ops = ops;
        BaseRevision = cell.BaseRevision;
        abort = cell.Abort;
    }

    /// <summary>The revision the change was drafted on.</summary>
    public T Base { get; }

    /// <summary>The candidate revision.</summary>
    public T Value { get; }

    /// <summary>The operations from <see cref="Base"/> to <see cref="Value"/>; empty for a no-op.</summary>
    public ImmutableArray<Op> Ops { get; }

    /// <summary>The tracker's revision when the change was drafted.</summary>
    public long BaseRevision { get; }

    /// <summary>The change's status, shared with its <see cref="Change{T}"/>; null for one made by hand.</summary>
    internal ChangeCell<T>? Cell { get; }

    /// <summary>Gives the change up: it is never adopted (idempotent).</summary>
    public void Abort() => abort();
}

/// <summary>Where a change stands (upstream <c>Status</c>).</summary>
internal enum ChangeStatus
{
    Open,
    Prepared,
    Consumed,
    Aborted,
    Stale,
}

/// <summary>One change's status, shared by its <see cref="Change{T}"/> and its <see cref="Prepared{T}"/> (upstream <c>StatusCell</c> with
/// the context's tracker, base and revision).</summary>
internal sealed class ChangeCell<T>(Tracker<T> tracker, long baseRevision, T @base)
    where T : JsonNode
{
    public Tracker<T> Tracker { get; } = tracker;

    public long BaseRevision { get; } = baseRevision;

    public T Base { get; } = @base;

    public ChangeStatus Status { get; set; }

    /// <summary>Whether another change was adopted since this one was drafted: an open or prepared change is then stale.</summary>
    public bool IsStale
    {
        get
        {
            if (Status is not (ChangeStatus.Open or ChangeStatus.Prepared) || Tracker.Revision == BaseRevision) return Status == ChangeStatus.Stale;
            Status = ChangeStatus.Stale;
            return true;
        }
    }

    /// <summary>Aborts an open or prepared change; one consumed, aborted or stale stays as it is.</summary>
    public void Abort()
    {
        if (Status is ChangeStatus.Open or ChangeStatus.Prepared && !IsStale) Status = ChangeStatus.Aborted;
    }
}
