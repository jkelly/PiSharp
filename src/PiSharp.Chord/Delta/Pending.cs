// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/pending.ts (PendingOps, PendingTake, MAX_PENDING_OPS), which
// packages/chord/src/delta/index.ts exports. The pending queue: the ops a slow consumer of one state has not taken yet, folded while they
// wait. Chord v2's copy (area cv2-state; see Ops.cs), on Chord v2's ops, of PiSharp.Durable's port, whose mappings it keeps:
// - Op records and ImmutableArray batches are immutable, so the queue never writes into a received batch or op; a merge makes a new op, and
//   a batch kept alone is handed over by identity (the same underlying array).
// - V8 joins strings as ropes; .NET copies on every concatenation, so a leaf that folds many appends would copy its text once per append.
//   The folding list keeps each merged leaf as its first op plus the text appended since, in a StringBuilder, and builds the merged op once,
//   when Take() hands the ops over. What a consumer receives is upstream's op.
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Delta;

/// <summary>Whether a <see cref="PendingTake"/> holds the pending ops or stands for a root replacement.</summary>
public enum PendingTakeKind
{
    /// <summary>The pending ops, folded (upstream <c>{ kind: "ops", ops }</c>).</summary>
    Ops,

    /// <summary>The queue overflowed: the owner sends a root replacement with the current value (upstream <c>{ kind: "replace" }</c>).</summary>
    Replace,
}

/// <summary>
/// What <see cref="PendingOps.Take"/> hands over (upstream <c>PendingTake</c>): the pending ops, folded, or
/// <see cref="PendingTakeKind.Replace"/> after an overflow, which the owner of the queue turns into a root replacement with the current value.
/// </summary>
public sealed class PendingTake
{
    private static readonly PendingTake NoOps = new(PendingTakeKind.Ops, []);

    private PendingTake(PendingTakeKind kind, ImmutableArray<Op> ops)
    {
        Kind = kind;
        Ops = ops;
    }

    /// <summary>The replacement: the queue overflowed.</summary>
    public static PendingTake Replace { get; } = new(PendingTakeKind.Replace, []);

    /// <summary><see cref="PendingTakeKind.Ops"/> or <see cref="PendingTakeKind.Replace"/>.</summary>
    public PendingTakeKind Kind { get; }

    /// <summary>The folded ops of <see cref="PendingTakeKind.Ops"/>; empty for <see cref="PendingTakeKind.Replace"/>.</summary>
    public ImmutableArray<Op> Ops { get; }

    /// <summary>The ops <paramref name="ops"/>.</summary>
    public static PendingTake FromOps(ImmutableArray<Op> ops) => ops.IsDefaultOrEmpty ? NoOps : new(PendingTakeKind.Ops, ops);
}

/// <summary>
/// Pending ops of one state for one consumer, in order and raw (upstream <c>PendingOps</c>). <c>r</c> drops everything before it. A leaf op
/// merges into the last pending leaf op on the same path (<c>a</c> + <c>a</c> joined, <c>s</c> replaces <c>s</c> or <c>a</c>, <c>a</c> after
/// a string <c>s</c> appends into it); any other op clears the leaf index. Past <see cref="MaxPendingOps"/> only "replace with the current
/// value" stays pending.
/// </summary>
/// <remarks>Not thread-safe: its owner (a watch) serializes access.</remarks>
public sealed class PendingOps
{
    /// <summary>Pending ops past which only "replace with the current value" stays pending (upstream <c>MAX_PENDING_OPS</c>).</summary>
    public const int MaxPendingOps = 10_000;

    private State state = State.Empty;
    // State.Batch: one batch, kept by reference, so a consumer that keeps up never pays for folding.
    private ImmutableArray<Op> batch;
    // State.Folding: owned by the queue until Take() hands it over; never one of the received batches.
    private List<Folded>? folding;
    // State.Folding: path key to the position of the last pending leaf op on that path.
    private Dictionary<string, int>? leaves;

    private enum State
    {
        Empty,
        Batch,
        Folding,
        // Overflowed: nothing more accumulates until Take() or a batch that starts with `r`.
        Replace,
    }

    /// <summary>Pending ops after folding; 1 once overflowed. One batch is counted as received.</summary>
    public int Size => state switch
    {
        State.Empty => 0,
        State.Batch => batch.Length,
        State.Folding => folding!.Count,
        _ => 1,
    };

    /// <summary>Queues the ops of one commit.</summary>
    public void Push(ImmutableArray<Op> ops)
    {
        if (ops.IsDefaultOrEmpty) return;
        // A batch that starts with `r` drops everything pending, also an overflow, and is kept as received.
        if (ops[0] is ReplaceOp)
        {
            KeepBatch(ops);
            return;
        }
        switch (state)
        {
            case State.Empty:
                KeepBatch(ops);
                return;
            case State.Batch:
                var first = batch;
                batch = default;
                folding = [];
                leaves = new Dictionary<string, int>(StringComparer.Ordinal);
                state = State.Folding;
                if (Fold(first)) Fold(ops);
                return;
            case State.Folding:
                Fold(ops);
                return;
            default:
                return;
        }
    }

    /// <summary>Hands over everything pending and starts empty.</summary>
    public PendingTake Take()
    {
        var taken = state switch
        {
            State.Empty => PendingTake.FromOps([]),
            State.Batch => PendingTake.FromOps(batch),
            State.Folding => PendingTake.FromOps(Build(folding!)),
            _ => PendingTake.Replace,
        };
        state = State.Empty;
        batch = default;
        folding = null;
        leaves = null;
        return taken;
    }

    private void KeepBatch(ImmutableArray<Op> ops)
    {
        state = State.Batch;
        batch = ops;
        folding = null;
        leaves = null;
    }

    /// <summary>Folds <paramref name="ops"/>; false once the queue overflowed into a replacement.</summary>
    private bool Fold(ImmutableArray<Op> ops)
    {
        var list = folding!;
        var index = leaves!;
        foreach (var op in ops)
        {
            switch (op)
            {
                case ReplaceOp:
                    list.Clear();
                    index.Clear();
                    break;
                case AppendOp append:
                    if (MergeLeaf(append)) continue;
                    break;
                case SetOp set:
                    if (IsPrimitive(set.Value))
                    {
                        if (MergeLeaf(set)) continue;
                    }
                    else if (index.Count > 0) index.Clear();
                    break;
                default:
                    if (index.Count > 0) index.Clear();
                    break;
            }
            list.Add(new Folded(op));
            if (list.Count > MaxPendingOps)
            {
                state = State.Replace;
                folding = null;
                leaves = null;
                return false;
            }
        }
        return true;
    }

    private static bool IsPrimitive(JsonNode? value) => value is not (JsonObject or JsonArray);

    /// <summary>
    /// Merges the leaf op <paramref name="op"/> into the last pending leaf op on its path, or indexes it as that op at the position it is
    /// about to be added at. True when merged.
    /// </summary>
    private bool MergeLeaf(PathOp op)
    {
        var key = DeltaPath.Key(op.Path);
        var list = folding!;
        if (leaves!.TryGetValue(key, out var at))
        {
            // The index points at leaf ops only.
            var previous = list[at];
            if (op is SetOp)
            {
                list[at] = new Folded(op);
                return true;
            }
            // Only a string set takes an append; the tracker never appends to another primitive.
            if (previous.Op is AppendOp || previous.Op is SetOp { Value: var text } && ChordJson.AsString(text) is not null)
            {
                (previous.Tail ??= new StringBuilder()).Append(((AppendOp)op).Text);
                list[at] = previous;
                return true;
            }
        }
        leaves[key] = list.Count;
        return false;
    }

    private static ImmutableArray<Op> Build(List<Folded> list)
    {
        var ops = new Op[list.Count];
        for (var index = 0; index < ops.Length; index++) ops[index] = list[index].Build();
        return ImmutableCollectionsMarshal.AsImmutableArray(ops);
    }

    /// <summary>One pending op: the op as folded, and the text appended to its string since (a merged append or string set).</summary>
    private struct Folded(Op op)
    {
        public Op Op = op;
        public StringBuilder? Tail;

        public readonly Op Build() => Tail is null ? Op : Op switch
        {
            AppendOp append => new AppendOp(append.Path, append.Text + Tail),
            SetOp set => new SetOp(set.Path, JsonValue.Create(ChordJson.AsString(set.Value) + Tail)),
            _ => throw new InvalidOperationException("Only an append or a string set takes appended text"),
        };
    }
}
