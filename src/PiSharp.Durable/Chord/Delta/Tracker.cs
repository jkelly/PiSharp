// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/tracker.ts (track, Tracker, Change, Prepared, lifecycle,
// operation emission). The lifecycle, its messages and the emitted operations follow upstream; the Proxy overlay is replaced by the
// explicit drafts of Draft.cs. Emission walks only dirty drafts: objects emit s/d per key (a/t for strings that grew or rolled), arrays
// emit removal splices, one permutation over retained base elements, insertion splices and per-element changes at final indices, dense
// edit regions fold into one splice, reserved keys fold into the nearest safe ancestor, and more than 4,096 operations fold into r. An
// array whose draft is still a piece table kept its base elements in order, so its plan comes from the pieces alone (orderedArrayPlan)
// and an append to a long array costs no walk over it; a draft flattened into entries is planned by a walk over them (elementArrayPlan).
using System.Collections.Immutable;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>
/// Owns one immutable revision sequence (upstream's <c>track()</c>). <see cref="Value"/> is the latest adopted revision; it is never
/// modified. Changes are drafted with <see cref="BeginChange"/>, sealed with <see cref="Change.Prepare"/> (which computes the candidate
/// revision and its exact operations) and committed with <see cref="Adopt"/>, an infallible pointer swap.
/// </summary>
public sealed class Tracker
{
    private readonly HashSet<WeakReference<ChangeContext>> contexts = [];
    private int pruneBudget = 256;

    private Tracker(JsonValue initial) { Value = initial; }

    /// <summary>Takes ownership of an object or array root in O(1); the root is never traversed or copied.</summary>
    public static Tracker Track(JsonValue initial)
    {
        RequireContainer(initial, nameof(initial));
        return new Tracker(initial);
    }

    /// <summary>The latest adopted revision.</summary>
    public JsonValue Value { get; private set; }

    /// <summary>The number of adoptions so far.</summary>
    public long Revision { get; private set; }

    /// <summary>Opens a draft over <see cref="Value"/>. Several changes may be open at once; adopting one makes the others stale.</summary>
    public Change BeginChange()
    {
        var context = new ChangeContext(this, Revision, Value, replacement: false);
        context.Root = Draft.CreateRoot(context, Value);
        Register(context);
        return new Change(context);
    }

    /// <summary>
    /// A whole-root replacement, not a diff: when <paramref name="value"/> deeply equals <see cref="Value"/> the operations are empty and
    /// the current root is kept (the comparison may traverse both trees); otherwise they are <c>[["r", value]]</c> and the prepared value
    /// is <paramref name="value"/> itself.
    /// </summary>
    public Prepared PrepareReplace(JsonValue value)
    {
        RequireContainer(value, nameof(value));
        var context = new ChangeContext(this, Revision, Value, replacement: true) { Status = ChangeStatus.Prepared };
        Register(context);
        try
        {
            var noop = JsonValue.DeepEquals(Value, value);
            return new Prepared(context, noop ? Value : value, noop ? [] : [new ReplaceOp(value)]);
        }
        catch
        {
            context.Status = ChangeStatus.Aborted;
            throw;
        }
        finally
        {
            context.Clear();
        }
    }

    /// <summary>Adopts a prepared change of this tracker at its current revision: <see cref="Value"/> becomes
    /// <see cref="Prepared.Value"/>, <see cref="Revision"/> advances (even for a no-op) and every competing change becomes stale.</summary>
    public void Adopt(Prepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var context = prepared.Context;
        if (!ReferenceEquals(context.Owner, this)) throw new InvalidOperationException("Prepared change belongs to a different tracker");
        switch (context.Status)
        {
            case ChangeStatus.Consumed: throw new InvalidOperationException("Prepared change has already been used");
            case ChangeStatus.Aborted: throw new InvalidOperationException("Prepared change has been aborted");
            case ChangeStatus.Stale: throw new InvalidOperationException("Prepared change is stale");
            case ChangeStatus.Prepared: break;
            default: throw new InvalidOperationException("Prepared change is not ready");
        }
        if (context.BaseRevision != Revision || !ReferenceEquals(Value, prepared.Base))
        {
            context.Status = ChangeStatus.Stale;
            context.Clear();
            throw new InvalidOperationException("Prepared change is stale");
        }
        Value = prepared.Value;
        context.Status = ChangeStatus.Consumed;
        Revision++;
        Invalidate(context);
    }

    internal void Release(ChangeContext context)
    {
        if (context.RegistryRef is { } reference) contexts.Remove(reference);
        context.RegistryRef = null;
    }

    private void Register(ChangeContext context)
    {
        context.RegistryRef = new WeakReference<ChangeContext>(context);
        contexts.Add(context.RegistryRef);
        if (--pruneBudget > 0) return;
        contexts.RemoveWhere(reference => !reference.TryGetTarget(out _));
        pruneBudget = Math.Max(256, contexts.Count);
    }

    private void Invalidate(ChangeContext winner)
    {
        foreach (var reference in contexts)
        {
            if (!reference.TryGetTarget(out var context) || ReferenceEquals(context, winner)) continue;
            if (context.Status is ChangeStatus.Open or ChangeStatus.Prepared) context.Status = ChangeStatus.Stale;
            context.RegistryRef = null;
            context.Clear();
        }
        contexts.Clear();
        pruneBudget = 256;
    }

    private static void RequireContainer(JsonValue value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!JsonIdentity.IsContainer(value)) throw new ArgumentException("A tracked root must be an object or an array.", name);
    }
}

/// <summary>An open draft over one revision. Settle it with <see cref="Prepare"/> or <see cref="Abort"/>.</summary>
public sealed class Change
{
    private ChangeContext? context;
    private ChangeContext? prepared;
    private bool settled;

    internal Change(ChangeContext context) { this.context = context; }

    /// <summary>The root draft; throws once the change is settled or stale.</summary>
    public Draft State => context is { Released: false, Root: { } root } ? root : throw new InvalidOperationException("Cannot use a settled overlay");

    /// <summary>Seals the change: revokes every draft, emits detached operations and computes the candidate revision without changing
    /// the tracker. Empty operations mean the prepared value is the base itself.</summary>
    public Prepared Prepare()
    {
        if (settled) throw new InvalidOperationException("Change has already been settled");
        var current = context!;
        current.AssertWritable();
        current.Status = ChangeStatus.Prepared;
        try
        {
            ImmutableArray<Op> operations = current.DirtyNodes.Count == 0 ? [] : new DraftEmitter(current).Emit();
            var result = new Prepared(current, operations.Length == 0 ? current.BaseValue : current.Root!.Current(), operations);
            current.Clear();
            prepared = current;
            settled = true;
            return result;
        }
        catch
        {
            current.Status = ChangeStatus.Aborted;
            current.Clear();
            settled = true;
            throw;
        }
        finally
        {
            context = null;
        }
    }

    /// <summary>Discards the change (idempotent). After <see cref="Prepare"/> it prevents adoption of the prepared result.</summary>
    public void Abort()
    {
        if (settled)
        {
            if (prepared?.Status == ChangeStatus.Prepared) prepared.Status = ChangeStatus.Aborted;
            prepared = null;
            return;
        }
        settled = true;
        context!.Abort();
        context = null;
    }
}

/// <summary>A sealed change: the immutable candidate <see cref="Value"/>, the <see cref="Base"/> it was drafted from, and the exact
/// operations with <c>DeltaApply.Apply(Base, Ops)</c> deeply equal to <see cref="Value"/>. Payloads may be shared with
/// <see cref="Value"/>; everything here stays readable after abort or staleness.</summary>
public sealed class Prepared
{
    internal Prepared(ChangeContext context, JsonValue value, ImmutableArray<Op> ops)
    {
        Context = context; Value = value; Base = context.BaseValue; Ops = ops;
    }

    internal ChangeContext Context { get; }
    /// <summary>The revision the change was drafted from.</summary>
    public JsonValue Base { get; }
    /// <summary>The candidate revision.</summary>
    public JsonValue Value { get; }
    /// <summary>The operations from <see cref="Base"/> to <see cref="Value"/>; empty for a no-op.</summary>
    public ImmutableArray<Op> Ops { get; }
    /// <summary>The tracker revision the change was drafted at.</summary>
    public long BaseRevision => Context.BaseRevision;
    /// <summary>Prevents adoption (idempotent).</summary>
    public void Abort() => Context.Abort();
}

internal enum ChangeStatus { Open, Prepared, Consumed, Aborted, Stale }

/// <summary>The shared state of one change: its status cell, its drafts and the drafts dirtied so far, in first-dirty order.</summary>
internal sealed class ChangeContext
{
    internal ChangeContext(Tracker owner, long baseRevision, JsonValue baseValue, bool replacement)
    {
        Owner = owner; BaseRevision = baseRevision; BaseValue = baseValue; Replacement = replacement;
    }

    internal Tracker Owner { get; }
    internal long BaseRevision { get; }
    internal JsonValue BaseValue { get; }
    internal bool Replacement { get; }
    internal ChangeStatus Status { get; set; }
    internal bool Released { get; private set; }
    internal Draft? Root { get; set; }
    internal List<Draft> DirtyNodes { get; } = [];
    internal WeakReference<ChangeContext>? RegistryRef { get; set; }

    internal void AssertReadable()
    {
        if (Released || Status is ChangeStatus.Consumed or ChangeStatus.Aborted or ChangeStatus.Stale)
            throw new InvalidOperationException("Cannot use a settled overlay");
    }

    internal void AssertWritable()
    {
        AssertReadable();
        if (Status != ChangeStatus.Open) throw new InvalidOperationException("Prepared overlays are read-only");
    }

    internal void Abort()
    {
        if (Status is ChangeStatus.Aborted or ChangeStatus.Consumed or ChangeStatus.Stale) return;
        Status = ChangeStatus.Aborted;
        Clear();
    }

    /// <summary>Revokes every draft and drops the overlay so held handles keep nothing alive but themselves.</summary>
    internal void Clear()
    {
        Owner.Release(this);
        Released = true;
        Root = null;
        DirtyNodes.Clear();
    }
}

/// <summary>Upstream's <c>emitOperations</c> over explicit drafts.</summary>
internal sealed class DraftEmitter(ChangeContext context)
{
    private const int MaxDeltaOperations = 4_096;
    private const int OverlapScan = 65_536;
    private const int DenseThreshold = 256;

    private readonly List<Op> ops = [];
    private readonly Dictionary<Draft, ImmutableArray<PathSegment>?> paths = new();

    public ImmutableArray<Op> Emit()
    {
        var dirty = context.DirtyNodes;

        // Dense candidates: element positions under the nearest non-structural array, plus that array's own overrides.
        var candidates = new Dictionary<DraftArray, HashSet<int>>();
        var candidateOrder = new List<DraftArray>();
        void Candidate(DraftArray array, int index)
        {
            if (!candidates.TryGetValue(array, out var set))
            {
                candidates[array] = set = [];
                candidateOrder.Add(array);
            }
            set.Add(index);
        }
        foreach (var node in dirty)
        {
            if (LocateDenseArrayPosition(node) is ({ } located, var index)) Candidate(located, index);
            if (node is DraftArray { Structural: false, Overrides.Count: > 0 } array)
                foreach (var origin in array.Overrides!.Keys) Candidate(array, origin);
        }
        var regions = new Dictionary<DraftArray, List<(int Start, int Length)>>();
        var regionOrder = new List<DraftArray>();
        foreach (var array in candidateOrder)
        {
            var path = ResolvePath(array);
            if (path is null || ReservedAt(path.Value) >= 0) continue;
            var built = BuildDenseRegions(candidates[array], array.Base.Count);
            if (built.Count == 0) continue;
            regions[array] = built;
            regionOrder.Add(array);
        }

        // Emission paths in upstream's Map order: dirty drafts, then forced folds, then dense arrays.
        var emission = new Dictionary<Draft, ImmutableArray<PathSegment>>();
        var emissionOrder = new List<Draft>();
        void Emission(Draft node, ImmutableArray<PathSegment> path)
        {
            if (!emission.ContainsKey(node)) emissionOrder.Add(node);
            emission[node] = path;
        }
        var forced = new Dictionary<Draft, ImmutableArray<PathSegment>>();
        var forcedOrder = new List<Draft>();
        void Force(Draft node, ImmutableArray<PathSegment> path)
        {
            if (!forced.ContainsKey(node)) forcedOrder.Add(node);
            forced[node] = path;
        }
        foreach (var node in dirty)
        {
            if (ResolvePath(node) is not { } path || IsCovered(node, path, regions)) continue;
            Emission(node, path);
            if (node is DraftObject obj && obj.HasReservedMutation()) Force(node, path);
            var reservedAt = ReservedAt(path);
            if (reservedAt < 0) continue;
            var ancestor = node;
            for (var depth = path.Length; depth > reservedAt; depth--) ancestor = ancestor.Parent!;
            Force(ancestor, path[..reservedAt]);
        }
        foreach (var node in forcedOrder) Emission(node, forced[node]);
        foreach (var array in regionOrder)
            if (ResolvePath(array) is { } path && !IsCovered(array, path, regions)) Emission(array, path);

        var maxDepth = 0;
        foreach (var node in emissionOrder) maxDepth = Math.Max(maxDepth, emission[node].Length);
        var buckets = new List<Draft>[maxDepth + 1];
        for (var depth = 0; depth <= maxDepth; depth++) buckets[depth] = [];
        foreach (var node in emissionOrder) buckets[emission[node].Length].Add(node);
        var folded = new HashSet<Draft>();
        foreach (var bucket in buckets)
        {
            foreach (var node in bucket)
            {
                var path = emission[node];
                if (HasPlacementAncestor(node) || HasFoldedAncestor(node, folded)) continue;
                if (forced.ContainsKey(node))
                {
                    EmitSet(path, node.Current());
                    folded.Add(node);
                    continue;
                }
                if (node is DraftArray array) EmitArray(array, path, regions.GetValueOrDefault(array));
                else EmitObject((DraftObject)node, path);
                if (ops.Count > MaxDeltaOperations) return [new ReplaceOp(context.Root!.Current())];
            }
        }
        return [.. ops];
    }

    private void EmitObject(DraftObject node, ImmutableArray<PathSegment> path)
    {
        // Immutable replay must encode delete-and-readd explicitly so string-key insertion order matches the draft.
        if (node.Readded is not null)
            foreach (var key in node.Readded.Keys)
                if (node.Base.ContainsKey(key)) ops.Add(new DeleteOp(path.Add(key)));
        if (node.Writes is not null)
        {
            foreach (var (key, raw) in node.Writes)
            {
                if (ops.Count > MaxDeltaOperations) return;
                var before = node.Readded?.ContainsKey(key) == true || !node.Base.TryGetValue(key, out var existing) ? null : existing;
                EmitChanged(path.Add(key), before, node.SlotCurrent(key, raw));
            }
        }
        if (node.Deletes is not null)
        {
            foreach (var key in node.Deletes.Keys)
            {
                if (ops.Count > MaxDeltaOperations) return;
                if (node.Base.ContainsKey(key)) ops.Add(new DeleteOp(path.Add(key)));
            }
        }
    }

    private void EmitArray(DraftArray node, ImmutableArray<PathSegment> path, List<(int Start, int Length)>? dense)
    {
        if (dense is not null)
        {
            foreach (var (start, length) in dense)
            {
                if (ops.Count > MaxDeltaOperations) return;
                ops.Add(new SpliceOp(path, start, length, node.CurrentRange(start, length)));
            }
        }
        if (node.Pieces is { } pieces) EmitOrderedArrayPlan(node, path, pieces);
        else if (node.Structural)
        {
            var entries = node.Entries!;
            var baseCount = node.Base.Count;
            var retained = new bool[baseCount];
            var target = new List<int>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry.Origin < 0) continue;
                retained[entry.Origin] = true;
                target.Add(entry.Origin);
            }
            for (var end = baseCount; end > 0;)
            {
                if (retained[end - 1]) { end--; continue; }
                var start = end - 1;
                while (start > 0 && !retained[start - 1]) start--;
                if (ops.Count > MaxDeltaOperations) return;
                ops.Add(new SpliceOp(path, start, end - start, []));
                end = start;
            }
            var rank = new int[baseCount];
            var permuted = false;
            for (int index = 0, next = 0; index < baseCount; index++)
                if (retained[index]) rank[index] = next++;
            for (var index = 0; index < target.Count && !permuted; index++) permuted = rank[target[index]] != index;
            if (permuted) ops.Add(new MoveOp(path, [.. target.Select(origin => rank[origin])]));
            for (var index = 0; index < entries.Count;)
            {
                if (entries[index].Origin >= 0) { index++; continue; }
                var start = index;
                var items = ImmutableArray.CreateBuilder<JsonValue>();
                for (; index < entries.Count && entries[index].Origin < 0; index++) items.Add(node.SlotCurrent(entries[index]));
                if (ops.Count > MaxDeltaOperations) return;
                ops.Add(new SpliceOp(path, start, 0, items.ToImmutable()));
            }
        }
        if (node.Overrides is null) return;
        foreach (var (origin, entry) in node.Overrides)
        {
            if (ops.Count > MaxDeltaOperations) return;
            if (entry.Removed) continue;
            var index = node.IndexOf(entry);
            if (dense is not null && RegionContains(dense, index)) continue;
            EmitChanged(path.Add(index), node.Base[origin], node.SlotCurrent(entry));
        }
    }

    // orderedArrayPlan: the retained base ranges ascend, so removals are the unretained runs from the end backwards (after the last range,
    // between ranges, before the first) and there is no permutation; then one splice per run of consecutive insert pieces (insertRunsOf).
    private void EmitOrderedArrayPlan(DraftArray node, ImmutableArray<PathSegment> path, IReadOnlyList<ArrayPiece> pieces)
    {
        var ranges = new List<int>();
        foreach (var piece in pieces)
        {
            if (piece.Inserted is not null || piece.Length == 0) continue;
            if (ranges.Count > 0 && piece.Start <= ranges[^1]) throw new InvalidOperationException("Array draft pieces lost their base order.");
            ranges.Add(piece.Start);
            ranges.Add(piece.Start + piece.Length - 1);
        }
        var end = node.Base.Count;
        for (var index = ranges.Count - 2; index >= -2; index -= 2)
        {
            var start = index >= 0 ? ranges[index + 1] + 1 : 0;
            if (end > start)
            {
                if (ops.Count > MaxDeltaOperations) return;
                ops.Add(new SpliceOp(path, start, end - start, []));
            }
            if (index >= 0) end = ranges[index];
        }
        var logical = 0;
        for (var at = 0; at < pieces.Count;)
        {
            if (pieces[at].Inserted is null)
            {
                logical += pieces[at++].Count;
                continue;
            }
            var start = logical;
            var items = ImmutableArray.CreateBuilder<JsonValue>();
            for (; at < pieces.Count && pieces[at].Inserted is { } inserted; at++)
            {
                foreach (var entry in inserted) items.Add(node.SlotCurrent(entry));
                logical += inserted.Count;
            }
            if (ops.Count > MaxDeltaOperations) return;
            ops.Add(new SpliceOp(path, start, 0, items.ToImmutable()));
        }
    }

    private bool EmitChanged(ImmutableArray<PathSegment> path, JsonValue? before, JsonValue after)
    {
        if (before is not null && !JsonIdentity.IsContainer(after) && JsonIdentity.Strict(before, after)) return false;
        if (JsonIdentity.IsContainer(before) && JsonIdentity.IsContainer(after) && (ReferenceEquals(before, after) || JsonValue.DeepEquals(before, after)))
            return false;
        if (before is JsonStringValue { Value: var previous } && after is JsonStringValue { Value: var next })
        {
            if (next.Length > previous.Length && next.StartsWith(previous, StringComparison.Ordinal))
            {
                ops.Add(new AppendOp(path, next[previous.Length..]));
                return true;
            }
            var shared = DeltaValidation.Overlap(previous, next, OverlapScan);
            if (shared > 0)
            {
                ops.Add(new TruncateOp(path, previous.Length - shared));
                if (next.Length > shared) ops.Add(new AppendOp(path, next[shared..]));
                return true;
            }
        }
        ops.Add(new SetOp(path, after));
        return true;
    }

    private void EmitSet(ImmutableArray<PathSegment> path, JsonValue value) =>
        ops.Add(path.IsEmpty ? new ReplaceOp(value) : new SetOp(path, value));

    private ImmutableArray<PathSegment>? ResolvePath(Draft node)
    {
        if (paths.TryGetValue(node, out var cached)) return cached;
        ImmutableArray<PathSegment>? result = null;
        if (node.Parent is null) result = ReferenceEquals(node, context.Root) ? ImmutableArray<PathSegment>.Empty : null;
        else if (ResolvePath(node.Parent) is { } parentPath)
        {
            if (node.ParentKey is { } key) { if (((DraftObject)node.Parent).IsChild(key, node)) result = parentPath.Add(key); }
            else if (node.ParentEntry is { Removed: false } entry && ReferenceEquals(entry.Child, node))
                result = parentPath.Add(((DraftArray)node.Parent).IndexOf(entry));
        }
        paths[node] = result;
        return result;
    }

    // The nearest array ancestor, when the draft sits under one of its base elements and nothing in that array has moved.
    private static (DraftArray? Array, int Index) LocateDenseArrayPosition(Draft node)
    {
        var child = node;
        for (var parent = child.Parent; parent is not null; parent = child.Parent)
        {
            if (parent is DraftArray array)
            {
                if (array.Structural || child.ParentEntry is not { Origin: >= 0 } entry || !ReferenceEquals(entry.Child, child)) return (null, 0);
                return (array, entry.Origin);
            }
            child = parent;
        }
        return (null, 0);
    }

    private static List<(int Start, int Length)> BuildDenseRegions(HashSet<int> candidates, int length)
    {
        var regions = new List<(int, int)>();
        if (candidates.Count < DenseThreshold) return regions;
        var bits = new bool[length];
        foreach (var index in candidates) bits[index] = true;
        for (var at = 0; at < bits.Length;)
        {
            while (at < bits.Length && !bits[at]) at++;
            if (at == bits.Length) break;
            var start = at;
            var end = at;
            var count = 0;
            var gap = 0;
            while (at < bits.Length)
            {
                if (bits[at]) { count++; end = at; gap = 0; }
                else if (++gap > 1) break;
                at++;
            }
            var span = end - start + 1;
            if (count >= DenseThreshold && count * 2 >= span) regions.Add((start, span));
        }
        return regions;
    }

    private static bool RegionContains(List<(int Start, int Length)> regions, long index)
    {
        foreach (var (start, length) in regions) if (index >= start && index < start + length) return true;
        return false;
    }

    private bool IsCovered(Draft node, ImmutableArray<PathSegment> path, Dictionary<DraftArray, List<(int Start, int Length)>> regions)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not DraftArray array || !regions.TryGetValue(array, out var list)) continue;
            if (ResolvePath(array) is not { } parentPath || path.Length <= parentPath.Length) continue;
            if (!path.AsSpan(0, parentPath.Length).SequenceEqual(parentPath.AsSpan())) continue;
            var segment = path[parentPath.Length];
            if (segment.IsIndex && RegionContains(list, segment.Index)) return true;
        }
        return false;
    }

    private static int ReservedAt(ImmutableArray<PathSegment> path)
    {
        for (var index = 0; index < path.Length; index++)
            if (path[index].Key is { } key && DeltaValidation.ReservedSegments.Contains(key)) return index;
        return -1;
    }

    private static bool HasPlacementAncestor(Draft node)
    {
        for (var current = node; current.Parent is not null; current = current.Parent)
            if (current.Placement) return true;
        return false;
    }

    private static bool HasFoldedAncestor(Draft node, HashSet<Draft> folded)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (folded.Contains(parent)) return true;
        return false;
    }
}
