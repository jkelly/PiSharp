using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/state/framed.test.ts. Framed sources (design §11): one frame per
// batch however often the batch set the source, of the state the batch left, diffed from the value its watches had before the batch;
// failures and recoveries within one batch; its dependents marked by every change; nothing listed while unwatched. The eight upstream
// cases are ported as they are (state.framed.*); upstream's `source.failure` carries the last good value, which here a watch's start
// (Start) carries. Four cases are authored (state.framed.160-*): what upstream's subclasses exercise and no test of the base class
// does (reads before the first value and after the end, Quiet), and the Diff seam the native port adds for the store's live query (a
// JsonNode has one parent: Framed.cs).
internal static partial class Program
{
    /// <summary>A fed source, its batch, what its owner was told, and a subscriber's calls (upstream <c>fed</c>).</summary>
    private sealed class Sync160Feed
    {
        public Sync160Feed(string? initial = null)
        {
            Batch = new Batch(Errors.Add, null);
            Source = new Sync160Fed(Batch, Owner);
            if (initial is not null) Batch.Commit(() => Source.Put(initial), ChordContext.Background);
        }

        public List<BatchError> Errors { get; } = [];

        public Sync160FramedOwner Owner { get; } = new();

        public Batch Batch { get; }

        public Sync160Fed Source { get; }

        public Sync160Recorder Recorder { get; } = new();

        public List<WatchCall<JsonNode?>> Calls => Recorder.Calls;

        public void Subscribe() => Source.Subscribe(Recorder);

        public void Commit(Action adopt) => Batch.Commit(adopt, ChordContext.Background);
    }

    private static IEnumerable<(string, Func<Task>)> Sync160StateFramedCases() =>
    [
        Case("state.framed.set-several-times-in-one-batch-delivers-one-frame-its-last-value-diffed-from-the-value-before-the-batch", () =>
        {
            var feed = new Sync160Feed("""{"a":1,"b":1,"c":1}""");
            var before = feed.Source.Value;
            feed.Subscribe();
            feed.Commit(() =>
            {
                feed.Source.Put("""{"a":2,"b":1,"c":1}""");
                feed.Source.Put("""{"a":2,"b":3,"c":1}""");
                feed.Source.Put("""{"a":2,"b":3,"c":4}""");
            });
            Equal(0, feed.Errors.Count, "nothing reported");
            Equal(1, feed.Calls.Count, "one frame");
            Json("""{"a":2,"b":3,"c":4}""", Sync160ValueOf(feed.Calls[0]), "the frame's value");
            // The ops apply to what the subscriber had, not to a value in between.
            Json("""{"a":2,"b":3,"c":4}""", Sync160Applied(before, Sync160OpsOf(feed.Calls[0])), "the ops applied to what the subscriber had");
            Json("""[["s",["a"],2],["s",["b"],3],["s",["c"],4]]""", Sync160OpsJson(Sync160OpsOf(feed.Calls[0])), "the ops");
            Check(feed.Calls[0] is WatchValueCall<JsonNode?> { Retired: false }, "no retirement");
            // Listed once in the batch (the cost: one delivery, one diff).
            Equal(1, feed.Source.Deliveries, "one delivery");
            // The next batch starts from the last value.
            feed.Commit(() => feed.Source.Put("""{"a":2,"b":4,"c":4}"""));
            Equal(2, feed.Calls.Count, "the next batch's frame");
            Json("""[["s",["b"],4]]""", Sync160OpsJson(Sync160OpsOf(feed.Calls[1])), "the next batch's ops");
        }),
        Case("state.framed.set-several-times-in-one-batch-delivers-nothing-when-the-batch-ends-at-the-value-it-started-from", () =>
        {
            var feed = new Sync160Feed("""{"n":1}""");
            feed.Subscribe();
            feed.Commit(() =>
            {
                feed.Source.Put("""{"n":2}""");
                feed.Source.Put("""{"n":1}""");
            });
            Equal(0, feed.Calls.Count, "no frame");
            Equal(1, feed.Source.Deliveries, "one delivery");
        }),
        Case("state.framed.marks-its-dependents-with-every-change-a-derive-read-between-two-sets-reads-again-after-the-second", () =>
        {
            var feed = new Sync160Feed("1");
            var twice = new Derive([feed.Source], values => ChordJson.AsNumber((JsonNode?)values[0])!.Value * 2, feed.Batch);
            twice.Attach((_, _) => feed.Source, _ => { });
            Json("2", twice.Value, "the derive's first value");
            var read = new JsonArray();
            feed.Commit(() =>
            {
                feed.Source.Put("2");
                read.Add(twice.Value?.DeepClone());
                feed.Source.Put("3");
                read.Add(twice.Value?.DeepClone());
            });
            Json("[4,6]", read, "the values read in the batch");
            Json("6", twice.Value, "the derive's value after it");
        }),
        Case("state.framed.tells-a-dependent-of-a-change-after-it-took-the-value-one-that-reads-at-once-reads-the-new-one", () =>
        {
            var feed = new Sync160Feed("1");
            var read = new JsonArray();
            feed.Source.Dependents.Add(new Sync160Dependent(() => read.Add(feed.Source.Value?.DeepClone())));
            feed.Commit(() =>
            {
                feed.Source.Put("2");
                feed.Source.Put("3");
            });
            Json("[2,3]", read, "what the dependent read");
        }),
        Case("state.framed.lists-nothing-while-nobody-watches-it-a-subscriber-taken-on-between-two-sets-gets-the-diff-from-what-it-took", () =>
        {
            var feed = new Sync160Feed("""{"n":1}""");
            feed.Commit(() =>
            {
                feed.Source.Put("""{"n":2}""");
                feed.Source.Put("""{"n":3}""");
            });
            Equal(0, feed.Source.Deliveries, "no delivery while unwatched");
            Json("""{"n":3}""", feed.Source.Value, "its value");
            JsonNode? taken = null;
            feed.Commit(() =>
            {
                feed.Source.Put("""{"n":4}""");
                taken = feed.Source.Value;
                feed.Subscribe();
                feed.Source.Put("""{"n":5}""");
                feed.Source.Put("""{"n":6}""");
            });
            Equal(1, feed.Calls.Count, "one frame");
            Json("""{"n":6}""", Sync160Applied(taken, Sync160OpsOf(feed.Calls[0])), "the ops applied to what it took");
        }),
        Case("state.framed.a-failure-and-the-value-after-it-are-one-frame-the-diff-from-the-value-before-no-failure-call", () =>
        {
            var feed = new Sync160Feed("""{"n":1}""");
            feed.Subscribe();
            feed.Commit(() =>
            {
                feed.Source.Fail(new InvalidOperationException("a page in between"));
                feed.Source.Put("""{"n":2}""");
            });
            Equal(1, feed.Calls.Count, "one call");
            Json("""{"n":2}""", Sync160ValueOf(feed.Calls[0]), "the value");
            Json("""[["s",["n"],2]]""", Sync160OpsJson(Sync160OpsOf(feed.Calls[0])), "the ops");
            Check(feed.Calls[0] is WatchValueCall<JsonNode?> { Retired: false }, "no retirement");
            // The episode was the owner's to hear of.
            Equal(1, feed.Owner.Reports.Count, "one report");
        }),
        Case("state.framed.a-value-and-the-failure-after-it-or-two-failures-are-one-failure-call-and-one-report", () =>
        {
            var feed = new Sync160Feed("""{"n":1}""");
            feed.Subscribe();
            var first = new InvalidOperationException("first");
            var last = new InvalidOperationException("last");
            feed.Commit(() =>
            {
                feed.Source.Put("""{"n":2}""");
                feed.Source.Fail(first);
                feed.Source.Fail(last);
            });
            Equal(1, feed.Calls.Count, "one call");
            Check(ReferenceEquals(last, Sync160ErrorOf(feed.Calls[0])), "the last failure");
            Equal(1, feed.Owner.Reports.Count, "one report");
            Check(ReferenceEquals(first, feed.Owner.Reports[0]), "the episode's first failure is the one reported");
            Check(ReferenceEquals(last, feed.Source.Failure), "its failure");
            // source.failure's `last`: what a watch opened meanwhile starts at.
            var start = feed.Source.Start();
            Check(start.Failed && start.HasLast && ReferenceEquals(last, start.Error), "a watch opened meanwhile starts failed, at the last good value");
            Json("""{"n":2}""", start.Value, "the last good value");
            // Still failed in a later batch: nothing new for the watches; the next value replaces the value whole.
            feed.Commit(() => feed.Source.Fail(new InvalidOperationException("again")));
            Equal(1, feed.Calls.Count, "still failed: no call");
            feed.Commit(() =>
            {
                feed.Source.Fail(new InvalidOperationException("once more"));
                feed.Source.Put("""{"n":3}""");
                feed.Source.Put("""{"n":4}""");
            });
            Equal(2, feed.Calls.Count, "the recovery's frame");
            Json("""{"n":4}""", Sync160ValueOf(feed.Calls[1]), "the recovery's value");
            Json("""[["r",{"n":4}]]""", Sync160OpsJson(Sync160OpsOf(feed.Calls[1])), "the value whole");
            Equal(1, feed.Owner.Reports.Count, "one report for the episode");
        }),
        Case("state.framed.an-end-in-the-batch-that-set-it-delivers-nothing", () =>
        {
            var feed = new Sync160Feed("""{"n":1}""");
            feed.Subscribe();
            feed.Commit(() =>
            {
                feed.Source.Put("""{"n":2}""");
                feed.Source.Finish(new InvalidOperationException("ended"));
                feed.Source.Put("""{"n":3}""");
            });
            Equal(0, feed.Calls.Count, "no call");
            Equal(0, feed.Errors.Count, "nothing reported");
            Check(feed.Source.Detached, "detached");
            Equal(1, feed.Recorder.Ends, "its subscriber ended");
        }),
        Case("state.framed.160-reads-are-not-ready-before-the-first-value-and-throw-the-owners-error-after-the-end", () =>
        {
            var feed = new Sync160Feed();
            Check(!feed.Source.Ready && !feed.Source.Detached && feed.Source.Failure is null, "loading");
            var early = Throws<ChordError>(() => _ = feed.Source.Value, "a read before the first value");
            Equal("not_ready", early.Code, "the code");
            Equal("test: read before its first value", early.Message, "the message names the owner");
            // No watch can open before the first value.
            Throws<ChordError>(() => feed.Source.OpenWatch(ChordContext.Background), "a watch before the first value");
            feed.Commit(() => feed.Source.Put("""{"n":1}"""));
            Check(feed.Source.Ready, "ready");
            var ended = new InvalidOperationException("ended");
            feed.Commit(() => feed.Source.Finish(ended));
            Check(!feed.Source.Ready && feed.Source.Detached && feed.Source.Failure is null, "detached");
            Check(ReferenceEquals(ended, Throws<InvalidOperationException>(() => _ = feed.Source.Value, "a read after the end")), "the owner's error");
            Check(ReferenceEquals(ended, Throws<InvalidOperationException>(() => feed.Source.Start(), "a start after the end")), "a start throws it too");
            // Nothing is taken after the end.
            feed.Commit(() => { feed.Source.Put("""{"n":2}"""); feed.Source.Fail(new InvalidOperationException("late")); });
            Check(feed.Source.Detached && feed.Owner.Reports.Count == 0, "an ended source takes no value and reports nothing");
        }),
        Case("state.framed.160-quiet-leaves-the-first-failure-to-whoever-awaits-the-first-value", () =>
        {
            var feed = new Sync160Feed();
            feed.Source.Hush();
            var failure = new InvalidOperationException("the first read failed");
            feed.Commit(() => feed.Source.Fail(failure));
            Equal(0, feed.Owner.Reports.Count, "not reported");
            Check(ReferenceEquals(failure, feed.Source.Failure), "failed");
            var start = feed.Source.Start();
            Check(start.Failed && !start.HasLast, "no last good value: a watch cannot start");
            Check(ReferenceEquals(failure, Throws<InvalidOperationException>(() => _ = feed.Source.Value, "a read while failed")), "reads throw the failure");
            // A value ends the episode: the next failure is the owner's to hear of.
            feed.Commit(() => feed.Source.Put("""{"n":1}"""));
            feed.Commit(() => feed.Source.Fail(new InvalidOperationException("later")));
            Equal(1, feed.Owner.Reports.Count, "the next episode is reported");
            Json("""{"n":1}""", feed.Source.Start().Value, "the last good value is kept");
        }),
        Case("state.framed.160-a-subclass-diff-gets-the-values-it-set-by-reference-and-its-ops-are-the-frame", () =>
        {
            var feed = new Sync160Feed();
            var first = JsonNode.Parse("""{"rows":[{"id":"a"},{"id":"b"}]}""");
            feed.Commit(() => feed.Source.Put(first));
            feed.Subscribe();
            var asked = new List<(JsonNode? Before, JsonNode? After)>();
            // A subclass that knows what changed says so without walking the trees: here, row 1 replaced.
            feed.Source.Differ = (before, after) =>
            {
                asked.Add((before, after));
                return [new SetOp(DeltaPath.Of("rows", 1), after!["rows"]![1]!.DeepClone())];
            };
            var between = JsonNode.Parse("""{"rows":[{"id":"a"},{"id":"x"}]}""");
            var last = JsonNode.Parse("""{"rows":[{"id":"a"},{"id":"c"}]}""");
            feed.Commit(() => { feed.Source.Put(between); feed.Source.Put(last); });
            Equal(1, asked.Count, "one diff per batch");
            Check(ReferenceEquals(first, asked[0].Before), "before: the value its watches had, as it was given");
            Check(ReferenceEquals(last, asked[0].After), "after: the last value of the batch, as it was given");
            Equal(1, feed.Calls.Count, "one frame");
            Check(ReferenceEquals(last, Sync160ValueOf(feed.Calls[0])), "the frame hands out the value as it was given");
            Json("""[["s",["rows",1],{"id":"c"}]]""", Sync160OpsJson(Sync160OpsOf(feed.Calls[0])), "the subclass's ops");
            Json("""{"rows":[{"id":"a"},{"id":"c"}]}""", Sync160Applied(first, Sync160OpsOf(feed.Calls[0])), "the ops applied to what the subscriber had");
            // No ops mean no frame, and the next batch diffs from the value the batch left.
            feed.Source.Differ = (before, after) => { asked.Add((before, after)); return []; };
            var same = JsonNode.Parse("""{"rows":[{"id":"a"},{"id":"c"}]}""");
            feed.Commit(() => feed.Source.Put(same));
            Equal(1, feed.Calls.Count, "no ops: no frame");
            Check(ReferenceEquals(last, asked[1].Before) && ReferenceEquals(same, asked[1].After), "the second diff's values");
            // After a failure the value is replaced whole: the differ is not asked.
            feed.Commit(() => feed.Source.Fail(new InvalidOperationException("failed")));
            feed.Commit(() => feed.Source.Put("""{"rows":[]}"""));
            Equal(2, asked.Count, "a recovery replaces the value whole without a diff");
            Json("""[["r",{"rows":[]}]]""", Sync160OpsJson(Sync160OpsOf(feed.Calls[^1])), "the recovery's ops");
        }),
        Case("state.framed.160-a-watch-gets-one-frame-per-commit-through-the-hosts-scheduler", async () =>
        {
            var host = new StateHost();
            var owner = new Sync160FramedOwner();
            var source = new Sync160Fed(host.Batch, owner);
            host.Batch.Commit(() => source.Put("""{"n":1}"""), ChordContext.Background);
            var watch = await source.WatchAsync(ChordContext.Background);
            Json("""{"n":1}""", watch.Value, "the watch starts at the value");
            var frames = FramesOf(watch);
            host.Batch.Commit(() => { source.Put("""{"n":2}"""); source.Put("""{"n":3}"""); }, ChordContext.Background);
            host.Delivered();
            Equal(1, frames.Count, "one frame for the commit");
            Json("""[["s",["n"],3]]""", Sync160OpsJson(frames[0].Ops), "its ops");
            Json("""{"n":3}""", watch.Value, "the watch's value");
        }),
    ];
}
