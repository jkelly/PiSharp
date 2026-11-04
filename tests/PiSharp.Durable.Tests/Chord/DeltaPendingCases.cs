using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/delta-pending.test.ts. Adaptations:
// - The fuzz cases keep upstream's seeded generator (the same LCG, so each sequence reproduces from its seed), its edit mix and its run
//   counts, written with the explicit draft API (decision 0005, as in TrackerCases.cs). JSON sort's localeCompare is an ordinal comparison.
//   appendText is work list area chord-append-text, which has not landed: DraftObject.AppendString stands in for it (the draft records a
//   string set, and the tracker emits the append), so the queue still folds appends.
// - Object.freeze is unnecessary: Op records and ImmutableArray batches cannot be written to. The check that the received ops are
//   unchanged stays; "toBe" on a batch is the same underlying array (ImmutableArray's ==).
// - The cost case counts PendingOps.PathKeys (the queue's path keys) instead of spying on JSON.stringify.
// - The references case uses WeakReference and GC.Collect in place of WeakRef and --expose-gc, building what it drops in methods the JIT
//   does not inline.
// - FUZZ_TIMEOUT is the runner's per-case limit (Program.cs).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ChordDeltaPendingCases() =>
    [
        Case("chord.delta-pending.folding.is-invisible-to-a-replica-tracker-edits-anywhere-appends-splices-and-reorders", () =>
        {
            var rnd = new PendingRandom(1);
            var (ops, folded) = (0, 0);
            for (var run = 0; run < 800; run++)
            {
                var sequence = PendingTrackedSequence(rnd.Root(), 1 + rnd.Int(30), rnd, draft => PendingMutateAnywhere(draft.AsObject(), rnd, PendingAppendToDraft),
                    rnd.Root);
                var result = PendingCheckSequence(sequence, rnd, $"run {run}");
                ops += result.Ops;
                folded += result.Folded;
            }
            // The sequences fold: a fuzzer whose ops never merge proves nothing.
            Check(folded < ops * 0.9, $"folded {folded} of {ops} ops");
        }),
        Case("chord.delta-pending.folding.is-invisible-to-a-replica-leaf-edits-of-list-elements-between-splices-and-reorders", () =>
        {
            var rnd = new PendingRandom(2);
            var (ops, folded) = (0, 0);
            for (var run = 0; run < 800; run++)
            {
                var initial = J("{\"list\":[{\"x\":\"a\",\"n\":0},{\"x\":\"b\",\"n\":1}]}");
                var sequence = PendingTrackedSequence(initial, 5 + rnd.Int(40), rnd, draft => PendingMutateList(draft.AsObject(), rnd), () => J("{\"list\":[]}"));
                var result = PendingCheckSequence(sequence, rnd, $"run {run}");
                ops += result.Ops;
                folded += result.Folded;
            }
            Check(folded < ops * 0.9, $"folded {folded} of {ops} ops");
        }),
        Case("chord.delta-pending.folding.is-invisible-to-a-replica-a-root-array", () =>
        {
            var rnd = new PendingRandom(3);
            for (var run = 0; run < 300; run++)
            {
                JsonValue initial = JsonValue.Array(Enumerable.Range(0, rnd.Int(6)).Select(_ => rnd.Value()).ToList());
                JsonValue Replace() => JsonValue.Array(Enumerable.Range(0, rnd.Int(6)).Select(_ => rnd.Value()).ToList());
                PendingCheckSequence(PendingTrackedSequence(initial, 40, rnd, draft => PendingMutateRootArray(draft.AsArray(), rnd), Replace), rnd, $"run {run}");
            }
        }),
        Case("chord.delta-pending.folding.is-invisible-to-a-replica-diff-revisions-between-arbitrary-values", () =>
        {
            var rnd = new PendingRandom(4);
            for (var run = 0; run < 500; run++) PendingCheckSequence(PendingDiffSequence(rnd, 30), rnd, $"run {run}");
        }),

        Case("chord.delta-pending.adversarial.a-leaf-set-in-an-array-element-a-splice-before-it-the-same-path-set-again", () =>
        {
            const string sets = "[[\"s\",[\"arr\",2,\"x\"],1],[\"p\",[\"arr\"],0,0,[{\"x\":9}]],[\"s\",[\"arr\",2,\"x\"],2]]";
            PendingSameOps(sets, PendingFoldOneByOne(J("{\"arr\":[{\"x\":0},{\"x\":0},{\"x\":0}]}"), Ops(sets)), "sets");
            const string appends = "[[\"a\",[\"arr\",0,\"x\"],\"1\"],[\"p\",[\"arr\"],0,0,[{\"x\":\"new\"}]],[\"a\",[\"arr\",0,\"x\"],\"2\"]]";
            PendingSameOps(appends, PendingFoldOneByOne(J("{\"arr\":[{\"x\":\"a\"},{\"x\":\"b\"}]}"), Ops(appends)), "appends");
        }),
        Case("chord.delta-pending.adversarial.t-then-a-and-appends-around-a-trim", () =>
        {
            Equal(2, PendingFoldOneByOne(J("{\"s\":\"hello\"}"), Ops("[[\"t\",[\"s\"],2],[\"a\",[\"s\"],\"!\"]]")).Length, "t then a");
            Equal(3, PendingFoldOneByOne(J("{\"s\":\"hello\"}"), Ops("[[\"a\",[\"s\"],\"AB\"],[\"t\",[\"s\"],3],[\"a\",[\"s\"],\"C\"]]")).Length, "around a trim");
        }),
        Case("chord.delta-pending.adversarial.an-object-s-at-a-parent-then-leaf-sets-below-it", () =>
        {
            Equal(3, PendingFoldOneByOne(J("{\"o\":{\"a\":1}}"), Ops("[[\"s\",[\"o\",\"a\"],2],[\"s\",[\"o\"],{\"a\":5,\"b\":6}],[\"s\",[\"o\",\"a\"],7]]")).Length,
                "object set between");
            PendingSameOps("[[\"s\",[\"x\"],{\"s\":\"b\"}],[\"s\",[\"x\",\"s\"],\"d\"]]",
                PendingFoldOneByOne(J("{\"x\":{\"s\":\"a\"}}"), Ops("[[\"s\",[\"x\"],{\"s\":\"b\"}],[\"a\",[\"x\",\"s\"],\"c\"],[\"s\",[\"x\",\"s\"],\"d\"]]")), "leaf sets below");
        }),
        Case("chord.delta-pending.adversarial.an-ancestor-primitive-s-between-leaf-ops-below-it", () =>
        {
            PendingSameOps("[[\"a\",[\"o\",\"a\"],\"1\"],[\"s\",[\"o\",\"b\"],\"q\"],[\"s\",[\"o\"],\"flat!\"]]",
                PendingFoldOneByOne(J("{\"o\":{\"a\":\"x\",\"b\":\"y\"}}"),
                    Ops("[[\"a\",[\"o\",\"a\"],\"1\"],[\"s\",[\"o\",\"b\"],\"q\"],[\"s\",[\"o\"],\"flat\"],[\"a\",[\"o\"],\"!\"]]")), "flattened");
            PendingSameOps("[[\"s\",[\"a\",\"b\"],1],[\"s\",[\"a\"],\"x\"]]",
                PendingFoldOneByOne(J("{\"a\":{\"b\":0}}"), Ops("[[\"s\",[\"a\",\"b\"],1],[\"s\",[\"a\"],null],[\"s\",[\"a\"],\"x\"]]")), "nulled");
        }),
        Case("chord.delta-pending.adversarial.deletes-moves-and-splices-shift-what-a-path-names", () =>
        {
            Equal(3, PendingFoldOneByOne(J("{\"arr\":[\"a\",\"b\",\"c\"]}"), Ops("[[\"s\",[\"arr\",2],\"C\"],[\"d\",[\"arr\",0]],[\"s\",[\"arr\",1],\"Z\"]]")).Length, "delete");
            Equal(3, PendingFoldOneByOne(J("{\"arr\":[1,2,3]}"), Ops("[[\"s\",[\"arr\",0],10],[\"m\",[\"arr\"],[2,1,0]],[\"s\",[\"arr\",0],20]]")).Length, "move");
            Equal(4, PendingFoldOneByOne(J("{\"arr\":[{\"x\":0}]}"),
                Ops("[[\"s\",[\"arr\",0,\"x\"],1],[\"p\",[\"arr\"],1,0,[{\"x\":0}]],[\"s\",[\"arr\",0,\"x\"],2],[\"s\",[\"arr\",1,\"x\"],3]]")).Length, "splice");
        }),
        Case("chord.delta-pending.adversarial.keys-of-one-parent-never-collide", () =>
        {
            PendingFoldOneByOne(J("{\"a\":{\"b\":\"x\"},\"a.b\":\"z\"}"), Ops("[[\"a\",[\"a\",\"b\"],\"1\"],[\"s\",[\"a.b\"],\"y\"],[\"a\",[\"a\",\"b\"],\"2\"]]"));
            PendingFoldOneByOne(J("{\"o\":{\"1\":\"a\"},\"arr\":[\"x\",\"y\"]}"),
                Ops("[[\"s\",[\"o\",\"1\"],\"b\"],[\"s\",[\"arr\",1],\"z\"],[\"s\",[\"o\",\"1\"],\"c\"],[\"s\",[\"arr\",1],\"w\"]]"));
        }),
        Case("chord.delta-pending.adversarial.an-array-set-at-the-end-appends-and-a-later-set-of-that-index-folds-into-it", () =>
            PendingSameOps("[[\"s\",[\"arr\",1],3],[\"s\",[\"arr\",0],9]]",
                PendingFoldOneByOne(J("{\"arr\":[1]}"), Ops("[[\"s\",[\"arr\",1],2],[\"s\",[\"arr\",0],9],[\"s\",[\"arr\",1],3]]")), "folded")),
        Case("chord.delta-pending.adversarial.r-drops-everything-before-it", () =>
            PendingSameOps("[[\"r\",{\"b\":\"x\"}],[\"a\",[\"b\"],\"y\"]]",
                PendingFoldOneByOne(J("{\"a\":1}"), Ops("[[\"s\",[\"a\"],2],[\"r\",{\"b\":\"x\"}],[\"a\",[\"b\"],\"y\"]]")), "folded")),
        Case("chord.delta-pending.adversarial.joins-appends-folds-appends-into-a-string-set-and-keeps-split-surrogates", () =>
        {
            PendingSameOps("[[\"s\",[\"s\"],\"abcd\"],[\"s\",[\"t\"],1]]",
                PendingFoldOneByOne(J("{\"s\":\"\"}"), Ops("[[\"s\",[\"s\"],\"ab\"],[\"a\",[\"s\"],\"c\"],[\"s\",[\"t\"],1],[\"a\",[\"s\"],\"d\"]]")), "into a set");
            PendingSameOps("[[\"a\",[\"s\"],\"\\ud83d\\ude00\\u00e9\"],[\"s\",[\"n\"],1]]",
                PendingFoldOneByOne(J("{\"s\":\"\"}"), Ops("[[\"a\",[\"s\"],\"\\ud83d\"],[\"s\",[\"n\"],1],[\"a\",[\"s\"],\"\\ude00\"],[\"a\",[\"s\"],\"\\u00e9\"]]")),
                "split surrogates");
        }),
        Case("chord.delta-pending.adversarial.interleaved-leaves-a-structural-op-then-the-same-leaves-again", () =>
            PendingSameOps("[[\"a\",[\"a\"],\"12\"],[\"a\",[\"b\"],\"1\"],[\"p\",[\"list\"],1,0,[1]],[\"a\",[\"b\"],\"2\"],[\"a\",[\"a\"],\"3\"]]",
                PendingFoldOneByOne(J("{\"a\":\"\",\"b\":\"\",\"list\":[0]}"),
                    Ops("[[\"a\",[\"a\"],\"1\"],[\"a\",[\"b\"],\"1\"],[\"a\",[\"a\"],\"2\"],[\"p\",[\"list\"],1,0,[1]],[\"a\",[\"b\"],\"2\"],[\"a\",[\"a\"],\"3\"]]")), "folded")),

        Case("chord.delta-pending.cap.keeps-max-pending-ops-and-replaces-past-them", () =>
        {
            var queue = new PendingOps();
            queue.Push([]);
            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps));
            var exact = queue.Take();
            Equal(PendingTakeKind.Ops, exact.Kind, "exact kind");
            Equal(PendingOps.MaxPendingOps, exact.Ops.Length, "exact");

            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps / 2));
            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps / 2, PendingOps.MaxPendingOps / 2));
            var full = queue.Take();
            Equal(PendingTakeKind.Ops, full.Kind, "full kind");
            Equal(PendingOps.MaxPendingOps, full.Ops.Length, "full");

            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps / 2));
            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps / 2 + 1, PendingOps.MaxPendingOps / 2));
            Check(ReferenceEquals(PendingTake.Replace, queue.Take()), "past the cap: replace");
        }),
        Case("chord.delta-pending.cap.counts-ops-after-folding", () =>
        {
            var queue = new PendingOps();
            for (var index = 0; index < 3 * PendingOps.MaxPendingOps; index++) queue.Push(Ops("[[\"a\",[\"text\"],\"x\"]]"));
            PendingSameTake("{\"kind\":\"ops\",\"ops\":[[\"a\",[\"text\"],\"" + new string('x', 3 * PendingOps.MaxPendingOps) + "\"]]}", queue.Take(), "folded");
        }),
        Case("chord.delta-pending.cap.accumulates-nothing-once-replaced-until-taken-or-a-batch-starting-with-r", () =>
        {
            var queue = new PendingOps();
            queue.Push(PendingDistinctSets(1));
            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps + 1));
            queue.Push(PendingDistinctSets(3));
            Check(ReferenceEquals(PendingTake.Replace, queue.Take()), "replaced");
            PendingSameTake("{\"kind\":\"ops\",\"ops\":[]}", queue.Take(), "then empty");

            queue.Push(PendingDistinctSets(1));
            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps + 1));
            var fresh = Ops("[[\"r\",{\"k\":[0]}]]");
            queue.Push(fresh);
            var taken = queue.Take();
            Check(taken.Kind == PendingTakeKind.Ops && taken.Ops == fresh, "a batch starting with r is kept as received");
            queue.Push(PendingDistinctSets(1));
            queue.Push(fresh);
            queue.Push(Ops("[[\"s\",[\"n\"],1]]"));
            PendingSameTake("{\"kind\":\"ops\",\"ops\":[[\"r\",{\"k\":[0]}],[\"s\",[\"n\"],1]]}", queue.Take(), "r then a set");
        }),
        Case("chord.delta-pending.cap.hands-one-batch-over-as-received-whatever-its-size", () =>
        {
            var queue = new PendingOps();
            var batch = PendingDistinctSets(PendingOps.MaxPendingOps + 1);
            queue.Push(batch);
            var taken = queue.Take();
            Check(taken.Kind == PendingTakeKind.Ops && taken.Ops == batch, "one batch by identity");
            queue.Push(batch);
            queue.Push(Ops("[[\"s\",[\"n\"],1]]"));
            Check(ReferenceEquals(PendingTake.Replace, queue.Take()), "a second batch overflows");
        }),

        Case("chord.delta-pending.ownership.hands-over-a-received-batch-by-identity-and-never-mutates-batches-or-op-tuples", () =>
        {
            var queue = new PendingOps();
            var first = Ops("[[\"a\",[\"s\"],\"1\"]]");
            queue.Push(first);
            var alone = queue.Take();
            Check(alone.Kind == PendingTakeKind.Ops && alone.Ops == first, "alone by identity");

            var second = Ops("[[\"a\",[\"s\"],\"2\"]]");
            queue.Push(first);
            queue.Push(second);
            PendingSameTake("{\"kind\":\"ops\",\"ops\":[[\"a\",[\"s\"],\"12\"]]}", queue.Take(), "joined");
            Equal("[[\"a\",[\"s\"],\"1\"]]", OpsJson(first).ToJson(), "first unchanged");
            Equal("[[\"a\",[\"s\"],\"2\"]]", OpsJson(second).ToJson(), "second unchanged");
        }),

        Case("chord.delta-pending.size.counts-pending-ops-after-folding-the-replacement-once-overflowed-and-nothing-once-taken", () =>
        {
            var queue = new PendingOps();
            Equal(0, queue.Size, "empty");
            queue.Push([]);
            Equal(0, queue.Size, "empty batch");
            queue.Push(Ops("[[\"a\",[\"text\"],\"a\"],[\"s\",[\"n\"],1]]"));
            Equal(2, queue.Size, "one batch");
            queue.Push(Ops("[[\"a\",[\"text\"],\"b\"],[\"s\",[\"n\"],2],[\"p\",[\"list\"],0,0,[1]]]"));
            Equal(3, queue.Size, "folded");
            queue.Take();
            Equal(0, queue.Size, "taken");

            queue.Push(PendingDistinctSets(PendingOps.MaxPendingOps));
            Equal(PendingOps.MaxPendingOps, queue.Size, "at the cap");
            queue.Push(Ops("[[\"s\",[\"n\"],1]]"));
            Equal(1, queue.Size, "overflowed");
            Check(ReferenceEquals(PendingTake.Replace, queue.Take()), "replace");
            Equal(0, queue.Size, "taken again");
        }),

        Case("chord.delta-pending.cost.computes-one-path-key-per-leaf-op-once-folding-none-for-one-batch-or-structural-ops", () =>
        {
            var queue = new PendingOps();
            var leaves = Ops("[[\"a\",[\"entries\",0,\"text\"],\"a\"],[\"s\",[\"meta\",\"tokens\"],1]]");
            queue.Push(leaves);
            queue.Take();
            Equal(0, queue.PathKeys, "one batch");

            queue.Push(leaves);
            Equal(0, queue.PathKeys, "one batch again");
            queue.Push([.. leaves, .. Ops("[[\"p\",[\"entries\"],1,0,[{\"text\":\"\"}]],[\"s\",[\"meta\"],{\"tokens\":0}],[\"d\",[\"x\"]]]")]);
            Equal(4, queue.PathKeys, "one per leaf op once folding");
        }),
        Case("chord.delta-pending.cost.keeps-one-op-per-leaf-path-however-many-commits-write-it", () =>
        {
            var queue = new PendingOps();
            for (var index = 0; index < 50_000; index++)
                queue.Push([new AppendOp(DeltaPath.Of("entries", 0, "text"), "tok "), new SetOp(DeltaPath.Of("meta", "tokens"), index)]);
            var taken = queue.Take();
            Equal(PendingTakeKind.Ops, taken.Kind, "kind");
            Equal("[[\"a\",[\"entries\",0,\"text\"],\"" + string.Concat(Enumerable.Repeat("tok ", 50_000)) + "\"],[\"s\",[\"meta\",\"tokens\"],49999]]",
                OpsJson(taken.Ops).ToJson(), "one op per leaf path");
        }),

        Case("chord.delta-pending.references.releases-a-folded-over-op-and-the-index-and-everything-pending-once-taken", () =>
        {
            var queue = new PendingOps();
            var first = PendingPayload(queue);
            CollectGarbage();
            // The set of `n` replaced the first one: only its newest op is held.
            Check(!first.Replaced.IsAlive, "folded-over op released");
            Check(first.Payload.IsAlive, "pending payload held");
            PendingDiscard(queue);
            CollectGarbage();
            Check(!first.Payload.IsAlive, "taken payload released");

            // Taken while the folding state holds the index: nothing stays behind in the queue.
            var second = PendingPayload(queue);
            queue.Push(Ops("[[\"p\",[\"list\"],0,0,[1]]]"));
            PendingDiscard(queue);
            CollectGarbage();
            Check(!second.Payload.IsAlive, "payload released after a structural op and take");
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Payload, WeakReference Replaced) PendingPayload(PendingOps queue)
    {
        var payload = J("{\"big\":\"" + new string('x', 1000) + "\"}");
        Op replaced = new SetOp(DeltaPath.Of("n"), 1);
        queue.Push([replaced]);
        queue.Push([new SetOp(DeltaPath.Of("n"), 2), new SetOp(DeltaPath.Of("o"), payload)]);
        return (new WeakReference(payload), new WeakReference(replaced));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PendingDiscard(PendingOps queue) => queue.Take();

    /// <summary>Upstream's collect(): five rounds of a full collection.</summary>
    private static void CollectGarbage()
    {
        for (var round = 0; round < 5; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary><paramref name="count"/> set ops on distinct paths.</summary>
    private static ImmutableArray<Op> PendingDistinctSets(int count, int from = 0) =>
        [.. Enumerable.Range(0, count).Select(index => (Op)new SetOp(DeltaPath.Of("k", from + index), index))];

    private static void PendingSameOps(string expected, ImmutableArray<Op> actual, string what) =>
        Equal(J(expected).ToJson(), OpsJson(actual).ToJson(), what);

    private static void PendingSameTake(string expected, PendingTake actual, string what) => Equal(J(expected).ToJson(), actual.ToString(), what);

    private static IEnumerable<JsonValue> PendingRoundTrip(ImmutableArray<JsonArrayValue> wire) => wire.Select(tuple => JsonValue.Parse(tuple.ToJson()));

    /// <summary>Pushes each op as its own batch, takes, and checks the replay; returns the folded ops.</summary>
    private static ImmutableArray<Op> PendingFoldOneByOne(JsonValue initial, ImmutableArray<Op> ops)
    {
        var queue = new PendingOps();
        foreach (var op in ops) queue.Push([op]);
        var batch = queue.Take();
        if (batch.Kind != PendingTakeKind.Ops) throw new InvalidOperationException("unexpected overflow");
        var expected = DeltaApply.Apply(initial, ops);
        var replayed = DeltaApply.Apply(initial, batch.Ops);
        Check(JsonValue.DeepEquals(replayed, expected), $"replay: {replayed.ToJson()} vs {expected.ToJson()}");
        var encoder = new DeltaEncoder();
        var decoder = new DeltaDecoder();
        var @base = DeltaApply.Apply(null, decoder.Decode(PendingRoundTrip(encoder.Encode([new ReplaceOp(initial)]))));
        var decoded = DeltaApply.Apply(@base, decoder.Decode(PendingRoundTrip(encoder.Encode(batch.Ops))));
        Check(JsonValue.DeepEquals(decoded, expected), $"wire replay: {decoded.ToJson()} vs {expected.ToJson()}");
        return batch.Ops;
    }

    /// <summary>One sequence of commits from a value: the commits' op batches and the value after each.</summary>
    private sealed record PendingSequence(JsonValue Initial, List<(ImmutableArray<Op> Ops, JsonValue Value)> Commits);

    /// <summary>Upstream's seeded generator, so a failing sequence reproduces from its seed.</summary>
    private sealed class PendingRandom(int seed)
    {
        // Surrogate halves and separators: an append may split a code point, and paths hold any string.
        private static readonly string[] Texts = ["a", "bc", " tok", "é", "😀", "\u2028", "\ud83d", "\ude00", "日本語", "\"q\"", "\\", "", new string('x', 30)];
        private static readonly string[] Keys = ["a", "b", "0", "1", "01", "a.b", "x y", "text", "list"];
        private uint state = (uint)seed;

        public double Next()
        {
            state = unchecked(state * 1664525u + 1013904223u);
            return state / 4294967296.0;
        }

        public int Int(int max) => (int)Math.Floor(Next() * max);

        public T Pick<T>(IReadOnlyList<T> items) => items[Int(items.Count)];

        public string Text() => Pick(Texts);

        public JsonValue Primitive() => Int(4) switch
        {
            0 => Int(100),
            1 => Text(),
            2 => Next() < 0.5,
            _ => JsonValue.Null,
        };

        public JsonValue Value(int depth = 0)
        {
            var roll = Next();
            if (depth > 2 || roll < 0.4) return Primitive();
            if (roll < 0.7)
            {
                var items = new List<JsonValue>();
                for (var count = Int(4); count > 0; count--) items.Add(Value(depth + 1));
                return JsonValue.Array(items);
            }
            var builder = JsonObjectValue.CreateBuilder();
            for (var count = Int(4); count >= 0; count--)
            {
                var key = Pick(Keys);
                builder[key] = Value(depth + 1);
            }
            return builder.ToImmutable();
        }

        public JsonValue Root()
        {
            var builder = JsonObjectValue.CreateBuilder();
            for (var count = Int(4) + 1; count >= 0; count--)
            {
                var key = Pick(Keys);
                builder[key] = Value();
            }
            return builder.ToImmutable();
        }
    }

    /// <summary>Every slot (a path) under <paramref name="node"/>, parents first.</summary>
    private static List<ImmutableArray<PathSegment>> PendingSlots(JsonValue node)
    {
        var slots = new List<ImmutableArray<PathSegment>>();
        void Walk(JsonValue value, ImmutableArray<PathSegment> path)
        {
            switch (value)
            {
                case JsonArrayValue array:
                    for (var index = 0; index < array.Count; index++)
                    {
                        var child = path.Add(index);
                        slots.Add(child);
                        Walk(array[index], child);
                    }
                    break;
                case JsonObjectValue obj:
                    foreach (var (key, item) in obj)
                    {
                        var child = path.Add(key);
                        slots.Add(child);
                        Walk(item, child);
                    }
                    break;
            }
        }
        Walk(node, []);
        return slots;
    }

    private static Draft PendingDraftAt(Draft root, ImmutableArray<PathSegment> path)
    {
        var draft = root;
        foreach (var segment in path)
            draft = draft is DraftObject obj ? obj.GetDraft(segment.Key!) : ((DraftArray)draft).GetDraft((int)segment.Index);
        return draft;
    }

    private static JsonValue PendingRead(Draft parent, PathSegment key) =>
        parent is DraftObject obj ? obj.Get(key.Key!)! : ((DraftArray)parent).Get((int)key.Index);

    private static void PendingWrite(Draft parent, PathSegment key, JsonValue value)
    {
        if (parent is DraftObject obj) obj.Set(key.Key!, value);
        else ((DraftArray)parent).Set((int)key.Index, value);
    }

    private static Draft PendingChild(Draft parent, PathSegment key) =>
        parent is DraftObject obj ? obj.GetDraft(key.Key!) : ((DraftArray)parent).GetDraft((int)key.Index);

    private static int PendingJsonOrder(JsonValue left, JsonValue right) => string.CompareOrdinal(left.ToJson(), right.ToJson());

    /// <summary>Appends through the draft's append (upstream appendText, area chord-append-text).</summary>
    private static void PendingAppendToDraft(DraftObject parent, string key, string text) => parent.AppendString(key, text);

    private static void PendingAppendToValue(DraftObject parent, string key, string text) =>
        parent.Set(key, ((JsonStringValue)parent.Get(key)!).Value + text);

    /// <summary>One random edit anywhere in <paramref name="draft"/>: string appends and trims, array splices and reorders, object edits.</summary>
    private static void PendingMutateAnywhere(DraftObject draft, PendingRandom rnd, Action<DraftObject, string, string> append)
    {
        var all = PendingSlots(draft.ToValue());
        if (all.Count == 0)
        {
            var key = rnd.Pick(["a", "b"]);
            draft.Set(key, rnd.Value());
            return;
        }
        var path = rnd.Pick(all);
        var parent = PendingDraftAt(draft, path[..^1]);
        var slot = path[^1];
        var current = PendingRead(parent, slot);
        var roll = rnd.Next();
        switch (current)
        {
            case JsonStringValue text:
                if (roll < 0.3 && parent is DraftObject owner) append(owner, slot.Key!, rnd.Text());
                else if (roll < 0.6) PendingWrite(parent, slot, text.Value + rnd.Text());
                else if (roll < 0.8) PendingWrite(parent, slot, text.Value[Math.Min(text.Value.Length, rnd.Int(3))..]);
                else PendingWrite(parent, slot, rnd.Primitive());
                break;
            case JsonArrayValue array:
            {
                var list = (DraftArray)PendingChild(parent, slot);
                if (roll < 0.3) list.Add(rnd.Value());
                else if (roll < 0.5)
                {
                    var start = rnd.Int(list.Count + 1);
                    var remove = rnd.Int(2);
                    list.Splice(start, remove, rnd.Next() < 0.5 ? [rnd.Value()] : []);
                }
                else if (roll < 0.65 && array.Count > 0)
                {
                    var index = rnd.Int(list.Count);
                    list.Set(index, rnd.Value());
                }
                else if (roll < 0.75 && array.Count > 1) list.Reverse();
                else if (roll < 0.85 && array.Count > 1) list.Sort(PendingJsonOrder);
                else if (roll < 0.95) PendingWrite(parent, slot, rnd.Value());
                else list.Clear();
                break;
            }
            case JsonObjectValue obj:
            {
                var target = (DraftObject)PendingChild(parent, slot);
                var keys = obj.Keys.ToList();
                if (roll < 0.4)
                {
                    var key = rnd.Pick(["a", "b", "1", "x y"]);
                    target.Set(key, rnd.Value());
                }
                else if (roll < 0.6 && keys.Count > 0) target.Delete(rnd.Pick(keys));
                else PendingWrite(parent, slot, rnd.Value());
                break;
            }
            default:
                PendingWrite(parent, slot, rnd.Next() < 0.7 ? rnd.Primitive() : rnd.Value());
                break;
        }
    }

    /// <summary>Leaf edits of a list of elements interleaved with splices, pushes and reorders of the list.</summary>
    private static void PendingMutateList(DraftObject draft, PendingRandom rnd)
    {
        static JsonValue Element(string x) => JsonValue.Object(KeyValuePair.Create("x", (JsonValue)x), KeyValuePair.Create("n", (JsonValue)0));
        if (draft.Get("list") is not JsonArrayValue)
        {
            draft.Set("list", JsonValue.Array(Element(""), Element(""), Element("")));
            return;
        }
        var list = draft.GetArray("list");
        var roll = rnd.Next();
        if (roll < 0.45 && list.Count > 0)
        {
            var index = rnd.Int(list.Count);
            if (list.Get(index) is JsonObjectValue)
            {
                var element = list.GetObject(index);
                if (rnd.Next() < 0.3) PendingAppendToDraft(element, "x", rnd.Text());
                else if (rnd.Next() < 0.5) element.Set("x", ((JsonStringValue)element.Get("x")!).Value + rnd.Text());
                else element.Set("n", rnd.Int(9));
            }
            else
            {
                list.Set(rnd.Int(list.Count), Element(""));
            }
        }
        else if (roll < 0.6)
        {
            var start = rnd.Int(list.Count + 1);
            var remove = rnd.Int(2);
            list.Splice(start, remove, rnd.Next() < 0.6 ? [Element(rnd.Text())] : []);
        }
        else if (roll < 0.7) list.Add(Element(""));
        else if (roll < 0.8 && list.Count > 1) list.Reverse();
        else if (roll < 0.9 && list.Count > 0)
        {
            var index = rnd.Int(list.Count);
            list.Set(index, rnd.Primitive());
        }
        else if (list.Count > 0) list.RemoveAt(rnd.Int(list.Count));
    }

    /// <summary>Edits of a root array: pushes, splices, reorders, element sets and shifts.</summary>
    private static void PendingMutateRootArray(DraftArray draft, PendingRandom rnd)
    {
        switch (rnd.Int(7))
        {
            case 0:
                draft.Add(rnd.Value());
                break;
            case 1:
            {
                var start = rnd.Int(draft.Count + 1);
                var remove = rnd.Int(4);
                draft.Splice(start, remove, rnd.Value());
                break;
            }
            case 2:
                draft.Reverse();
                break;
            case 3:
                draft.Sort(PendingJsonOrder);
                break;
            case 4:
                if (draft.Count > 0)
                {
                    var index = rnd.Int(draft.Count);
                    draft.Set(index, rnd.Value());
                }
                break;
            case 5:
                if (draft.Count > 0) draft.RemoveAt(0);
                break;
            default:
            {
                var start = rnd.Int(draft.Count + 1);
                draft.Splice(start, rnd.Int(4));
                break;
            }
        }
    }

    private static PendingSequence PendingTrackedSequence(JsonValue initial, int commits, PendingRandom rnd, Action<Draft> mutate, Func<JsonValue> replace)
    {
        var tracker = Tracker.Track(initial);
        var output = new List<(ImmutableArray<Op>, JsonValue)>();
        for (var index = 0; index < commits; index++)
        {
            Prepared prepared;
            if (rnd.Next() < 0.03)
            {
                prepared = tracker.PrepareReplace(replace());
            }
            else
            {
                var change = tracker.BeginChange();
                for (var edits = 1 + rnd.Int(4); edits > 0; edits--) mutate(change.State);
                prepared = change.Prepare();
            }
            tracker.Adopt(prepared);
            output.Add((prepared.Ops, tracker.Value));
        }
        return new PendingSequence(initial, output);
    }

    /// <summary>Diffs between values, each an edit of the previous one or a new value.</summary>
    private static PendingSequence PendingDiffSequence(PendingRandom rnd, int commits)
    {
        var initial = rnd.Value();
        var output = new List<(ImmutableArray<Op>, JsonValue)>();
        var value = initial;
        for (var index = 0; index < commits; index++)
        {
            // Upstream edits `{ value: structuredClone(value) }` in place; here a draft over the same object.
            var change = Tracker.Track(JsonValue.Object(KeyValuePair.Create("value", value))).BeginChange();
            var edited = change.State.AsObject();
            for (var edits = 1 + rnd.Int(3); edits > 0; edits--) PendingMutateAnywhere(edited, rnd, PendingAppendToValue);
            var editedValue = ((JsonObjectValue)edited.ToValue())["value"];
            change.Abort();
            var next = rnd.Next() < 0.2 ? rnd.Value() : editedValue;
            output.Add((DeltaDiff.DiffRevisions(value, next), next));
            value = next;
        }
        return new PendingSequence(initial, output);
    }

    /// <summary>
    /// Feeds the sequence through one queue, taking at random points as a slow consumer would, and replays what was taken on a replica, once
    /// as ops and once through Delta's encoder and decoder. Both must reach the final value; the received ops must be untouched.
    /// </summary>
    private static (int Ops, int Folded) PendingCheckSequence(PendingSequence sequence, PendingRandom rnd, string where)
    {
        var received = string.Join("\n", sequence.Commits.Select(commit => OpsJson(commit.Ops).ToJson()));
        var queue = new PendingOps();
        var taken = new List<(PendingTake Batch, JsonValue Value)>();
        for (var index = 0; index < sequence.Commits.Count; index++)
        {
            var commit = sequence.Commits[index];
            queue.Push(commit.Ops);
            if (rnd.Next() < 0.1 || index == sequence.Commits.Count - 1) taken.Add((queue.Take(), commit.Value));
        }
        var final = sequence.Commits.Count == 0 ? sequence.Initial : sequence.Commits[^1].Value;
        var replica = sequence.Initial;
        var encoder = new DeltaEncoder();
        var decoder = new DeltaDecoder();
        var decoded = DeltaApply.Apply(null, decoder.Decode(PendingRoundTrip(encoder.Encode([new ReplaceOp(replica)]))));
        var folded = 0;
        foreach (var (batch, value) in taken)
        {
            ImmutableArray<Op> ops = batch.Kind == PendingTakeKind.Ops ? batch.Ops : [new ReplaceOp(value)];
            folded += ops.Length;
            replica = DeltaApply.Apply(replica, ops);
            decoded = DeltaApply.Apply(decoded, decoder.Decode(PendingRoundTrip(encoder.Encode(ops))));
        }
        Check(JsonValue.DeepEquals(replica, final), $"{where}: replica {replica.ToJson()} vs {final.ToJson()}");
        Check(JsonValue.DeepEquals(decoded, final), $"{where}: decoded {decoded.ToJson()} vs {final.ToJson()}");
        Equal(received, string.Join("\n", sequence.Commits.Select(commit => OpsJson(commit.Ops).ToJson())), where + ": received ops untouched");
        return (sequence.Commits.Sum(commit => commit.Ops.Length), folded);
    }
}
