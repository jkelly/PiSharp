using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/state/derive.test.ts, the cases 1.6.0 added ("a watched derive
// read between two changes of its source in one batch"): one frame per commit for a watched derive. As upstream's, they run on the state
// layer alone: a Batch, a Cell source set inside a commit, a Derive attached to it, and a subscriber taking the calls inside the batch
// (Sync160FoundationSupport.cs). A cell's value is a JSON number, so a compute reads it through ChordJson.AsNumber.
internal static partial class Program
{
    /// <summary>A derive over a test's sources with a subscriber's calls (upstream <c>derived</c>): the current value taken first, as a
    /// subscriber takes it on.</summary>
    private sealed record Sync160Derived(Derive Derive, JsonNode? Taken, Sync160Recorder Recorder)
    {
        public List<WatchCall<JsonNode?>> Calls => Recorder.Calls;
    }

    /// <summary>A cell at 1 in a batch counting on its telemetry (upstream <c>cell</c>).</summary>
    private sealed class Sync160Cells(Batch batch, List<BatchError> errors, Func<IReadOnlyDictionary<string, double>> counts)
    {
        public Batch Batch => batch;

        public List<BatchError> Errors => errors;

        public Sync160Cell Source { get; } = new(1, batch);

        public double Diffs => counts().GetValueOrDefault("chord.derive.diffs");

        public Sync160Derived Derived(object[] sources, Func<IReadOnlyList<object?>, JsonNode?> compute)
        {
            var derive = new Derive(sources, compute, batch);
            derive.Attach((input, _) => input, _ => { });
            var taken = derive.Value;
            var recorder = new Sync160Recorder();
            derive.Subscribe(recorder);
            return new Sync160Derived(derive, taken, recorder);
        }
    }

    private static async Task<Sync160Cells> Sync160CellsAsync()
    {
        var metered = await Metered();
        var errors = new List<BatchError>();
        return new Sync160Cells(new Batch(errors.Add, metered.Context.Telemetry.Metrics), errors, metered.Counts);
    }

    private static double Sync160N(object? value) => ChordJson.AsNumber((JsonNode?)value) ?? throw new InvalidOperationException("no number");

    private static IEnumerable<(string, Func<Task>)> Sync160StateDeriveCases() =>
    [
        Case("state.derive.read-between-two-changes-delivers-one-frame-its-last-value-diffed-from-the-value-before-the-batch", async () =>
        {
            var cells = await Sync160CellsAsync();
            // `big` changes with the first change only: a diff from the value read in between would not carry it.
            var derived = cells.Derived([cells.Source], values => new JsonObject { ["n"] = Sync160N(values[0]), ["big"] = Sync160N(values[0]) >= 2 });
            var before = cells.Diffs;
            var read = new JsonArray();
            cells.Batch.Commit(() =>
            {
                cells.Source.Put(2);
                // As a reference binding in the same commit reads the member it binds to.
                read.Add(derived.Derive.Value?.DeepClone());
                cells.Source.Put(3);
            }, ChordContext.Background);
            Json("""[{"n":2,"big":true}]""", read, "the value read in between");
            Equal(0, cells.Errors.Count, "nothing reported");
            Equal(1, derived.Calls.Count, "one frame");
            Json("""{"n":3,"big":true}""", Sync160ValueOf(derived.Calls[0]), "the frame's value");
            // The ops apply to what the subscriber had, not to the value read in between.
            Json("""{"n":3,"big":true}""", Sync160Applied(derived.Taken, Sync160OpsOf(derived.Calls[0])), "the ops applied to what the subscriber had");
            Equal(1.0, cells.Diffs - before, "one diff");
            // The next batch starts from the last value.
            cells.Batch.Commit(() => cells.Source.Put(4), ChordContext.Background);
            Equal(2, derived.Calls.Count, "the next batch's frame");
            Json("""[["s",["n"],4]]""", Sync160OpsJson(Sync160OpsOf(derived.Calls[1])), "the next batch's ops");
        }),
        Case("state.derive.read-between-two-changes-delivers-nothing-when-the-batch-ends-at-the-value-it-started-from", async () =>
        {
            var cells = await Sync160CellsAsync();
            var derived = cells.Derived([cells.Source], values => new JsonObject { ["n"] = Sync160N(values[0]) });
            cells.Batch.Commit(() =>
            {
                cells.Source.Put(2);
                _ = derived.Derive.Value;
                cells.Source.Put(1);
            }, ChordContext.Background);
            Equal(0, derived.Calls.Count, "no frame");
        }),
        Case("state.derive.read-between-two-changes-a-derive-over-it-is-one-frame-too-read-or-not", async () =>
        {
            var cells = await Sync160CellsAsync();
            var inner = cells.Derived([cells.Source], values => new JsonObject { ["n"] = Sync160N(values[0]) });
            var outer = cells.Derived([inner.Derive], values => new JsonObject { ["shown"] = $"n={Sync160N(((JsonNode?)values[0])!["n"])}" });
            cells.Batch.Commit(() =>
            {
                cells.Source.Put(2);
                _ = outer.Derive.Value;
                cells.Source.Put(3);
                _ = inner.Derive.Value;
                cells.Source.Put(4);
            }, ChordContext.Background);
            Equal(1, inner.Calls.Count, "the inner derive's frames");
            Equal(1, outer.Calls.Count, "the outer derive's frames");
            Json("""{"n":4}""", Sync160Applied(inner.Taken, Sync160OpsOf(inner.Calls[0])), "the inner ops applied");
            Json("""{"shown":"n=4"}""", Sync160Applied(outer.Taken, Sync160OpsOf(outer.Calls[0])), "the outer ops applied");
        }),
        Case("state.derive.read-between-two-changes-a-failure-read-in-between-and-the-value-after-it-are-one-frame-no-failure-call", async () =>
        {
            var cells = await Sync160CellsAsync();
            var derived = cells.Derived([cells.Source], values =>
            {
                if (Sync160N(values[0]) == 2) throw new InvalidOperationException("two");
                return new JsonObject { ["n"] = Sync160N(values[0]) };
            });
            cells.Batch.Commit(() =>
            {
                cells.Source.Put(2);
                Equal("two", Throws<InvalidOperationException>(() => _ = derived.Derive.Value, "the read in between").Message, "the failure read in between");
                cells.Source.Put(3);
            }, ChordContext.Background);
            Equal(1, derived.Calls.Count, "one frame");
            // Its watches had a value: a diff, not a replacement of the whole.
            Json("""[["s",["n"],3]]""", Sync160OpsJson(Sync160OpsOf(derived.Calls[0])), "the ops");
            Json("""{"n":3}""", Sync160Applied(derived.Taken, Sync160OpsOf(derived.Calls[0])), "the ops applied");
        }),
        Case("state.derive.read-between-two-changes-a-value-read-in-between-and-the-failure-after-it-are-one-failure-call", async () =>
        {
            var cells = await Sync160CellsAsync();
            var derived = cells.Derived([cells.Source], values =>
            {
                if (Sync160N(values[0]) == 3) throw new InvalidOperationException("three");
                return new JsonObject { ["n"] = Sync160N(values[0]) };
            });
            cells.Batch.Commit(() =>
            {
                cells.Source.Put(2);
                _ = derived.Derive.Value;
                cells.Source.Put(3);
            }, ChordContext.Background);
            Equal(1, derived.Calls.Count, "one call");
            Equal("three", Sync160ErrorOf(derived.Calls[0]).Message, "the failure call");
            // Healed in a later batch: the value whole.
            cells.Batch.Commit(() => cells.Source.Put(4), ChordContext.Background);
            Equal(2, derived.Calls.Count, "the healing frame");
            Json("""[["r",{"n":4}]]""", Sync160OpsJson(Sync160OpsOf(derived.Calls[1])), "the value whole");
        }),
    ];
}
