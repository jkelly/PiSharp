using System.Reflection;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/delta/tracker.ts and
// test/delta-tracker/{tracker.test.ts,retention.worker.ts}. Authored CLR lifecycle/GC equivalents; no JS proxy claims.
internal static partial class Program
{
    private static object? TrackerField(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);

    private static IEnumerable<(string, Func<Task>)> ChordTrackerOpenChangeCases() =>
    [
        Case("chord.tracker.open-change.sequential-transactions-allocate-no-competitor-handles", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0}"));
            for (var index = 0; index < 2_000; index++)
            {
                var change = tracker.BeginChange();
                var context = change.State.Context;
                Check(ReferenceEquals(TrackerField(tracker, "open"), context), "latest context is pinned");
                Check(context.RegistryRef is null && TrackerField(tracker, "competitors") is null, "no per-change weak reference or set");
                Root(change).Set("value", index);
                if (index % 2 == 0) change.Abort();
                else tracker.Adopt(change.Prepare());
                Check(TrackerField(tracker, "open") is null && TrackerField(tracker, "competitors") is null, "settled registry is empty");
            }
        }),
        Case("chord.tracker.open-change.demotes-only-open-competitors", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0}"));
            var first = tracker.BeginChange();
            var firstContext = first.State.Context;
            var second = tracker.BeginChange();
            var secondContext = second.State.Context;
            Check(firstContext.RegistryRef is not null && secondContext.RegistryRef is null, "only older context has a GC handle");
            var registered = (HashSet<WeakReference<ChangeContext>>)TrackerField(tracker, "competitors")!;
            Equal(1, registered.Count, "one competitor");
            first.Abort();
            Equal(0, registered.Count, "aborting competitor removes its handle");
            Check(ReferenceEquals(TrackerField(tracker, "open"), secondContext), "aborting competitor leaves newest pinned");
            var replacement = tracker.PrepareReplace(J("{\"value\":2}"));
            Check(ReferenceEquals(TrackerField(tracker, "open"), secondContext) && secondContext.RegistryRef is null,
                "replacement never competes with or demotes the open draft");
            replacement.Abort();
            var prepared = second.Prepare();
            Check(TrackerField(tracker, "open") is null && registered.Count == 0, "prepared contexts are not tracked");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.open-change.three-way-winners-stale-every-loser", () =>
        {
            for (var winner = 0; winner < 3; winner++)
            {
                var tracker = Tracker.Track(J("{\"child\":{\"value\":0}}"));
                var changes = Enumerable.Range(0, 3).Select(_ => tracker.BeginChange()).ToArray();
                var children = changes.Select(change => Root(change).GetObject("child")).ToArray();
                for (var index = 0; index < 3; index++) children[index].Set("value", index + 1);
                var preparedLoser = changes[(winner + 1) % 3].Prepare();
                tracker.Adopt(changes[winner].Prepare());
                Equal((double)winner + 1, Num(At(tracker.Value, "child", "value")), "winner value");
                ThrowsMatching(() => tracker.Adopt(preparedLoser), "stale", "prepared loser");
                var openLoser = changes[(winner + 2) % 3];
                ThrowsMatching(() => openLoser.Prepare(), "settled", "open loser");
                foreach (var child in children) ThrowsMatching(() => child.Set("value", 9), "settled", "retained child");
                Check(TrackerField(tracker, "open") is null && TrackerField(tracker, "competitors") is null, "adoption drops registry storage");
            }
        }),
        Case("chord.tracker.open-change.abandoned-latest-is-pinned-until-demotion-or-adoption", () =>
        {
            var tracker = Tracker.Track(J("{\"payload\":null}"));
            var abandoned = AbandonTrackedDraft(tracker);
            ForceTrackerGc();
            Check(TrackerTargetAlive(abandoned), "latest abandoned payload stays pinned");
            var next = tracker.BeginChange();
            TrackerCollected(abandoned, "demotion allows abandoned payload collection");
            next.Abort();
            var chain = Enumerable.Range(0, 32).Select(_ => AbandonTrackedDraft(tracker)).ToArray();
            foreach (var reference in chain[..^1]) TrackerCollected(reference, "only latest abandoned payload is pinned");
            ForceTrackerGc();
            Check(TrackerTargetAlive(chain[^1]), "latest chained draft stays pinned");
            tracker.Adopt(tracker.PrepareReplace(J("{\"payload\":null}")));
            TrackerCollected(chain[^1], "even no-op adoption releases last abandoned payload");
            GC.KeepAlive(tracker);
        }),
        Case("chord.tracker.open-change.retained-open-handles-do-not-own-tracker", () =>
        {
            var (reference, held) = RetainChangesWithoutTracker();
            TrackerCollected(reference, "open/competing handles have only a weak tracker reference");
            Root(held[0]).Set("value", 3);
            held[0].Abort();
            held[1].Abort();
            GC.KeepAlive(held);
        }),
        Case("chord.tracker.open-change.abandoned-competitor-registry-is-pruned", () =>
        {
            var tracker = Tracker.Track(J("{\"payload\":null}"));
            for (var index = 0; index < 1_025; index++)
            {
                AbandonTrackedDraft(tracker);
                if (index % 128 == 0) ForceTrackerGc();
            }
            var registered = (HashSet<WeakReference<ChangeContext>>)TrackerField(tracker, "competitors")!;
            Check(registered.Count <= 257, "collected competitors are pruned within the 256-demotion budget");
            tracker.Adopt(tracker.PrepareReplace(J("{\"payload\":null}")));
            Check(TrackerField(tracker, "competitors") is null, "adoption drops the weak registry storage");
        }),
        Case("chord.tracker.open-change.settled-detached-handles-release-payloads", () =>
        {
            foreach (var settlement in new[] { "abort", "prepare", "adopt", "stale" })
            {
                var (references, held, tracker) = RetainSettledDrafts(settlement, large: false);
                foreach (var reference in references) TrackerCollected(reference, settlement + " releases base/placement/cache payloads");
                foreach (var draft in held.OfType<Draft>())
                {
                    ThrowsMatching(() => draft.ToValue(), "settled", "retained draft refuses reads");
                    Check(draft.Parent is null && draft.ParentEntry is null && draft.NextAllocated is null && draft.Context.Root is null,
                        "settled handles retain no ancestry or overlay root");
                }
                GC.KeepAlive(held);
                GC.KeepAlive(tracker);
            }
        }),
        Case("chord.tracker.open-change.large-read-and-write-overlays-release-all-nodes", () =>
        {
            var (references, held, tracker) = RetainSettledDrafts("adopt", large: true);
            foreach (var reference in references) TrackerCollected(reference, "large settled overlay releases graph");
            foreach (var draft in held.OfType<Draft>())
            {
                Check(TrackerField(draft.Context, "nodes") is null && TrackerField(draft.Context, "dirtyNodes") is null,
                    "no retained per-node ownership chain or dirty buffer");
                ThrowsMatching(() => draft.ToValue(), "settled", "large retained draft");
            }
            GC.KeepAlive(held);
            GC.KeepAlive(tracker);
        }),
        Case("chord.tracker.open-change.retained-prepared-keeps-public-values-only", () =>
        {
            var (trackerReference, prepared) = RetainPreparedWithoutTracker();
            TrackerCollected(trackerReference, "prepared does not keep tracker alive");
            Same("{\"value\":0}", prepared.Base, "public base intentionally retained");
            Same("{\"value\":1}", prepared.Value, "public candidate intentionally retained");
            prepared.Abort();
            Check(prepared.Context.Root is null && TrackerField(prepared.Context, "baseValue") is null, "context has no overlay or base");
            GC.KeepAlive(prepared);
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> AbandonTrackedDraft(Tracker tracker)
    {
        var change = tracker.BeginChange();
        var payload = JsonValue.Array(Enumerable.Range(0, 1_000).Select(value => (JsonValue)value));
        Root(change).Set("payload", payload);
        return new WeakReference<object>(payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object>, Change[]) RetainChangesWithoutTracker()
    {
        var tracker = Tracker.Track(J("{\"value\":0}"));
        return (new WeakReference<object>(tracker), [tracker.BeginChange(), tracker.BeginChange()]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object>, Prepared) RetainPreparedWithoutTracker()
    {
        var tracker = Tracker.Track(J("{\"value\":0}"));
        var change = tracker.BeginChange();
        Root(change).Set("value", 1);
        var prepared = change.Prepare();
        tracker.Adopt(prepared);
        return (new WeakReference<object>(tracker), prepared);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object>[], object[], Tracker) RetainSettledDrafts(string settlement, bool large)
    {
        var rows = JsonValue.Array(Enumerable.Range(0, large ? 5_000 : 3).Select(value => J($"{{\"value\":{value}}}")));
        var initial = JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)rows), KeyValuePair.Create("payload", (JsonValue)JsonValue.Null));
        var tracker = Tracker.Track(initial);
        var change = tracker.BeginChange();
        var root = Root(change);
        var array = root.GetArray("rows");
        var children = Enumerable.Range(0, array.Count).Select(array.GetObject).ToArray();
        foreach (var child in children) child.Set("value", -1);
        var placement = J("{\"nested\":{\"value\":123}}");
        root.Set("payload", placement);
        var detached = root.GetObject("payload").GetObject("nested");
        detached.Set("value", 456);
        root.Set("payload", JsonValue.Null);
        var cached = root.ToValue();
        var references = new[] { initial, rows, placement, cached }.Select(value => new WeakReference<object>(value)).ToArray();
        switch (settlement)
        {
            case "abort": change.Abort(); break;
            case "prepare": change.Prepare(); break;
            case "adopt": tracker.Adopt(change.Prepare()); break;
            case "stale": break;
        }
        tracker.Adopt(tracker.PrepareReplace(J("{\"rows\":[],\"payload\":null}")));
        return (references, [change, root, array, children[0], children[^1], detached], tracker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TrackerTargetAlive(WeakReference<object> reference) => reference.TryGetTarget(out _);

    private static void ForceTrackerGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void TrackerCollected(WeakReference<object> reference, string what)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ForceTrackerGc();
            if (!TrackerTargetAlive(reference)) return;
        }
        Check(false, what);
    }
}
