using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/state/derive.test.ts. Upstream serves local types through
// createHost (phase 6) whose factories call self.derive over defineState documents (the documents areas); here the derives are the state
// layer's own (Derive), attached as an activation attaches them, over the hand-built host's documents (StateHost.cs), and `set(n)` is a
// commit of the document. What only the host adds waits for phase 6: a derive over another object's member (a reference forwarding its
// member: the cross-object case runs the derive over the other derive directly), the owner's derive_failed report with its object and
// member (the report is the derive's own, named here), releasing a reference (Detach with the object's error) and restartable's attempts.
// The diamond's visits are counted through a dependent attached to each derive (upstream spies on Derive.prototype.sourceRetired, which
// C# cannot): each derive's walk runs once, so each counter is called once. The GC cases use WeakReference and GC.Collect, building what
// they drop in methods the JIT does not inline.
internal static partial class Program
{
    private static readonly InvalidOperationException DeriveThrew = new("t threw");

    /// <summary>The number <c>v.n</c> of a <c>{ n }</c> document's value.</summary>
    private static double StateN(object? value) => ChordJson.AsNumber(((JsonNode)value!)["n"]) ?? throw new InvalidOperationException("no n");

    /// <summary>A failing-derives host (upstream <c>failingDerives</c>): <c>t</c> throws while <c>n</c> is 1 or negative, <c>u</c> is
    /// <c>t + 1</c>, <c>v</c> is <c>n</c>; the derives' reports collected as <c>(member, error)</c>.</summary>
    private static (StateHost Host, StateDoc Num, Derive T, Derive U, Derive V, List<(string Member, Exception Error)> Errors) StateFailing()
    {
        var host = new StateHost();
        var num = host.Doc("""{"n":0}""");
        var errors = new List<(string, Exception)>();
        var t = host.Derive([num], values =>
        {
            var n = StateN(values[0]);
            if (n == 1 || n < 0) throw DeriveThrew;
            return n;
        }, error => errors.Add(("t", error)));
        var u = host.Derive([t], values => ChordJson.AsNumber((JsonNode?)values[0])!.Value + 1, error => errors.Add(("u", error)));
        var v = host.Derive([num], values => StateN(values[0]), error => errors.Add(("v", error)));
        return (host, num, t, u, v, errors);
    }

    private static void StateSetN(StateDoc num, double n) => num.Set(FormattableString.Invariant($"{{\"n\":{n}}}"), ChordContext.Background);

    private static string StateFailed => """{"kind":"failed","error":"t threw"}""";

    private static IEnumerable<(string, Func<Task>)> StateDeriveCases() =>
    [
        Case("state.derive.derives-across-objects-update-in-the-batch-of-the-commit-that-changed-their-sources-object", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var doubled = host.Derive([num], values => StateN(values[0]) * 2);
            var label = host.Derive([doubled], values => $"double {ChordJson.AsNumber((JsonNode?)values[0])}");
            var frames = FramesOf(await label.WatchAsync(StateBg));
            StateSetN(num, 2);
            host.Delivered();
            Json("""[["double 4",[["r","double 4"]]]]""", StateFrames(frames), "the frames");
        }),
        Case("state.derive.the-pull-model-computes-a-diamonds-derives-once-each-per-batch", async () =>
        {
            var metered = await Metered();
            var host = new StateHost(metered.Context.Telemetry.Metrics);
            var num = host.Doc("""{"n":0}""");
            var b = host.Derive([num], values => StateN(values[0]) + 1);
            var c = host.Derive([num], values => StateN(values[0]) + 2);
            var d = host.Derive([b, c], values => ChordJson.AsNumber((JsonNode?)values[0])!.Value + ChordJson.AsNumber((JsonNode?)values[1])!.Value);
            var frames = FramesOf(await d.WatchAsync(StateBg));
            double Count() => metered.Counts().GetValueOrDefault("chord.derive.computes");
            var before = Count();
            StateSetN(num, 1);
            Equal(3.0, Count() - before, "computes in the batch");
            host.Delivered();
            Json("[5]", new JsonArray([.. frames.Select(frame => frame.Value?.DeepClone())]), "the values");
        }),
        Case("state.derive.the-pull-model-leaves-unwatched-derives-dirty-until-read-a-one-off-read-does-not-make-them-eager", async () =>
        {
            var metered = await Metered();
            var host = new StateHost(metered.Context.Telemetry.Metrics);
            var num = host.Doc("""{"n":0}""");
            var doubled = host.Derive([num], values => StateN(values[0]) * 2);
            double Count() => metered.Counts().GetValueOrDefault("chord.derive.computes");
            var before = Count();
            StateSetN(num, 1);
            Equal(before, Count(), "no compute in the commit");
            Json("2", doubled.Value, "the first read");
            Json("2", doubled.Value, "the second read");
            Equal(before + 1, Count(), "one compute for both reads");
            StateSetN(num, 2);
            Equal(before + 1, Count(), "still lazy after a read");
        }),
        Case("state.derive.the-pull-model-diffs-a-watched-derives-old-and-new-value-counted-and-sends-no-frame-when-they-are-equal", async () =>
        {
            var metered = await Metered();
            var host = new StateHost(metered.Context.Telemetry.Metrics);
            var num = host.Doc("""{"n":0}""");
            var even = host.Derive([num], values => new JsonObject { ["even"] = StateN(values[0]) % 2 });
            var frames = FramesOf(await even.WatchAsync(StateBg));
            StateSetN(num, 2);
            StateSetN(num, 3);
            host.Delivered();
            Json("""[[{"even":1},[["s",["even"],1]]]]""", StateFrames(frames), "the frames");
            Equal(2.0, metered.Counts()["chord.derive.diffs"], "the diffs");
        }),
        Case("state.derive.the-pull-model-counts-no-diff-for-a-recomputed-primitive-it-had-pisharp", async () =>
        {
            // PiSharp: a derive's number is a fresh JsonValue on every compute; `after !== before` is JavaScript's identity, by value for a
            // primitive, so recomputing the number it had counts no diff and sends no frame, as upstream's derive does.
            var metered = await Metered();
            var host = new StateHost(metered.Context.Telemetry.Metrics);
            var num = host.Doc("""{"n":0,"m":0}""");
            var n = host.Derive([num], values => StateN(values[0]));
            var frames = FramesOf(await n.WatchAsync(StateBg));
            num.Set("""{"n":0,"m":1}""", StateBg);
            host.Delivered();
            Equal(0, frames.Count, "no frame");
            Equal(0.0, metered.Counts().GetValueOrDefault("chord.derive.diffs"), "no diff");
        }),
        Case("state.derive.failures-give-the-throwing-derives-watches-and-those-of-the-derives-over-it-one-failure-call-they-heal-on-a-later-change", async () =>
        {
            var (host, num, t, u, v, errors) = StateFailing();
            var ts = ChangesOf(await t.WatchAsync(StateBg));
            var us = ChangesOf(await u.WatchAsync(StateBg));
            var frames = FramesOf(await v.WatchAsync(StateBg));
            StateSetN(num, 1);
            host.Delivered();
            Json($"[{StateFailed}]", StateCalls(ts), "t's calls");
            Json($"[{StateFailed}]", StateCalls(us), "u's calls");
            Check(ReferenceEquals(DeriveThrew, ((WatchFailedCall<JsonNode?>)ts[0]).Error), "t's failure is the thrown error");
            Check(ReferenceEquals(DeriveThrew, Throws<InvalidOperationException>(() => _ = t.Value, "t's read")), "t's read throws it");
            Check(ReferenceEquals(DeriveThrew, Throws<InvalidOperationException>(() => _ = u.Value, "u's read")), "u's read throws it");
            Json("1", v.Value, "v");
            StateSetN(num, 2);
            host.Delivered();
            // Open all along; the next value replaces the root.
            Json("""{"kind":"value","value":2,"ops":[["r",2]],"retired":false}""", JsonNode.Parse(CallJson(ts[^1])), "t's heal");
            Json("""{"kind":"value","value":3,"ops":[["r",3]],"retired":false}""", JsonNode.Parse(CallJson(us[^1])), "u's heal");
            Json("[1,2]", new JsonArray([.. frames.Select(frame => frame.Value?.DeepClone())]), "v's values");
            // The throwing derive reports it, once; the derive over it (its source failed) does not.
            Sequence(["t"], errors.Select(error => error.Member), "the reports");
            Check(ReferenceEquals(DeriveThrew, errors[0].Error), "the report's error");
        }),
        Case("state.derive.failures-give-one-failure-call-per-episode-a-recompute-that-throws-again-is-quiet-a-heal-ends-the-episode", async () =>
        {
            var (host, num, t, u, _, _) = StateFailing();
            var ts = ChangesOf(await t.WatchAsync(StateBg));
            var us = ChangesOf(await u.WatchAsync(StateBg));
            foreach (var n in new double[] { 1, -1, -2, 3, -3 })
            {
                StateSetN(num, n);
                host.Delivered();
            }
            Json($$"""[{{StateFailed}},{"kind":"value","value":3,"ops":[["r",3]],"retired":false},{{StateFailed}}]""", StateCalls(ts), "t's calls");
            Json($$"""[{{StateFailed}},{"kind":"value","value":4,"ops":[["r",4]],"retired":false},{{StateFailed}}]""", StateCalls(us), "u's calls");
        }),
        Case("state.derive.failures-open-a-new-watch-on-a-failed-derive-at-its-last-good-value-the-failure-its-first-call-then-the-heal", async () =>
        {
            var (host, num, t, u, _, _) = StateFailing();
            StateSetN(num, 5);
            // Lazy: the last good value is the last one computed.
            Json("6", u.Value, "u");
            StateSetN(num, 1);
            var tw = await t.WatchAsync(StateBg);
            var uw = await u.WatchAsync(StateBg);
            Json("[5,6]", new JsonArray(tw.Value?.DeepClone(), uw.Value?.DeepClone()), "the values the watches open at");
            var ts = ChangesOf(tw);
            var us = ChangesOf(uw);
            host.Delivered();
            StateSetN(num, -1);
            StateSetN(num, 7);
            host.Delivered();
            Json($$"""[{{StateFailed}},{"kind":"value","value":7,"ops":[["r",7]],"retired":false}]""", StateCalls(ts), "t's calls");
            Json($$"""[{{StateFailed}},{"kind":"value","value":8,"ops":[["r",8]],"retired":false}]""", StateCalls(us), "u's calls");
        }),
        Case("state.derive.failures-report-once-per-failure-episode-also-when-nothing-watches-a-lazy-derive-fails-when-read", () =>
        {
            var (_, num, t, _, _, errors) = StateFailing();
            StateSetN(num, 1);
            Throws<InvalidOperationException>(() => _ = t.Value, "the first read");
            Throws<InvalidOperationException>(() => _ = t.Value, "the second read");
            StateSetN(num, -1);
            Throws<InvalidOperationException>(() => _ = t.Value, "a read after another throwing change");
            Equal(1, errors.Count, "one report");
            StateSetN(num, 2);
            Json("2", t.Value, "healed");
            StateSetN(num, 1);
            Throws<InvalidOperationException>(() => _ = t.Value, "a new episode");
            Equal(2, errors.Count, "a second report");
        }),
        Case("state.derive.failures-fold-a-failure-and-a-value-for-a-busy-listener-the-value-replaces-the-failure-as-a-root-replacement", async () =>
        {
            var (host, num, t, _, _, _) = StateFailing();
            var watch = await t.WatchAsync(StateBg);
            var calls = new List<WatchCall<JsonNode?>>();
            var gate = new TaskCompletionSource();
            watch.Start(async (call, _) =>
            {
                calls.Add(call);
                if (calls.Count == 1) await gate.Task;
            });
            StateSetN(num, 5);
            host.Delivered();
            StateSetN(num, 1);
            StateSetN(num, 6);
            gate.SetResult();
            host.Delivered();
            Json("""[{"kind":"value","value":5,"ops":[["r",5]],"retired":false},{"kind":"value","value":6,"ops":[["r",6]],"retired":false}]""",
                StateCalls(calls), "the calls");
        }),
        Case("state.derive.gc-a-detached-derive-is-not-held-by-its-former-sources", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var n = host.Derive([num], values => StateN(values[0]));
            var references = StateDetachedLabel(host, n);
            StateSetN(num, 1);
            await ExpectCollected(references);
            Equal(0, n.Dependents.Count, "the former source keeps no dependent");
        }),
        Case("state.derive.gc-a-watch-open-on-a-failed-derive-keeps-nothing-more-it-ends-with-its-reference-and-a-retained-one-holds-no-source", async () =>
        {
            var (watch, references) = await StateFailedWatch();
            Equal(WatchEnd.Ended, await watch.Closed, "the end");
            // `watch` stays referenced here.
            await ExpectCollected(references);
            GC.KeepAlive(watch);
        }),
        Case("state.derive.gc-of-handed-out-errors-a-failure-calls-fresh-error-kept-by-its-listener-keeps-nothing-of-its-host-once-the-host-closes", async () =>
        {
            var kept = new List<object?>();
            var references = await StateFreshError(kept);
            Equal(1, kept.Count, "the listener kept the error");
            await ExpectCollected(references);
            GC.KeepAlive(kept);
        }),
        Case("state.derive.map-shared-maps-an-object-element-once-while-it-stays-the-same-object-primitives-every-time", () =>
        {
            var seen = new List<object>();
            var map = Watchables.MapShared<object, double>(x =>
            {
                seen.Add(x);
                return x is int number ? number : StateN(x);
            });
            var a = Parse("""{"n":1}""");
            var b = Parse("""{"n":2}""");
            object three = 3;
            Sequence([1.0, 2, 3], map([a, b, three]), "the first map");
            var other = Parse("""{"n":2}""");
            Sequence([1.0, 2, 3], map([a, other, three]), "the second map");
            Equal(5, seen.Count, "the calls");
            Check(ReferenceEquals(a, seen[0]) && ReferenceEquals(b, seen[1]) && ReferenceEquals(three, seen[2]) && ReferenceEquals(other, seen[3]) &&
                ReferenceEquals(three, seen[4]), "called for a, b, 3, the new { n: 2 } and 3 again");
        }),
        Case("state.derive.map-shared-keeps-no-element-alive-in-its-cache", async () =>
        {
            var map = Watchables.MapShared<JsonNode, JsonNode>(note => new JsonObject { ["text"] = ChordJson.AsString(note["text"]) });
            var references = StateMappedNote(map);
            await ExpectCollected(references);
            Json("""[{"text":"u"}]""", new JsonArray([.. map([Parse("""{"text":"u"}""")])]), "a later map");
        }),
        Case("state.derive.map-shared-returns-the-previous-result-object-for-an-unchanged-element", () =>
        {
            var strip = Watchables.MapShared<JsonNode, JsonNode>(note => new JsonObject { ["text"] = ChordJson.AsString(note["text"]) });
            var note = Parse("""{"text":"t","at":1}""");
            var first = strip([note])[0];
            Check(ReferenceEquals(first, strip([note, Parse("""{"text":"u","at":2}""")])[0]), "the same result object");
        }),
        Case("state.derive.the-retired-flag-a-derive-over-a-retired-source-publishes-retired-true-although-its-value-and-ops-are-unchanged-rule-21", async () =>
        {
            var host = new StateHost();
            var list = host.Doc("""{"items":[]}""");
            var many = host.Derive([list], values => ((JsonNode)values[0]!)["items"]!.AsArray().Count > 1);
            var twice = host.Derive([many], values => !ChordJson.IsBoolean((JsonNode?)values[0], true));
            list.Set("""{"items":["a"]}""", StateBg);
            var manys = ChangesOf(await many.WatchAsync(StateBg));
            var twices = ChangesOf(await twice.WatchAsync(StateBg));
            list.Retire(StateBg);
            host.Delivered();
            Json("""[{"kind":"value","value":false,"ops":[],"retired":true}]""", StateCalls(manys), "many's calls");
            Json("""[{"kind":"value","value":true,"ops":[],"retired":true}]""", StateCalls(twices), "twice's calls");
            list.Set("""{"items":["a","b"]}""", StateBg);
            host.Delivered();
            Json("""{"kind":"value","value":true,"ops":[["r",true]],"retired":false}""", JsonNode.Parse(CallJson(manys[^1])), "many's next call");
        }),
        Case("state.derive.the-retired-flag-a-retirement-while-the-derive-is-failed-travels-with-its-next-good-value", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var gate = host.Doc("""{"on":0}""");
            var n = host.Derive([num, gate], values =>
            {
                if (ChordJson.AsNumber(((JsonNode)values[1]!)["on"]) == 1 && StateN(values[0]) == 0) throw DeriveThrew;
                return StateN(values[0]);
            });
            StateSetN(num, 1);
            gate.Set("""{"on":1}""", StateBg);
            var calls = ChangesOf(await n.WatchAsync(StateBg));
            // The retirement makes `n` 0 again, which throws. Upstream's awaits let the listener take the failure before the next change.
            num.Retire(StateBg);
            host.Delivered();
            StateSetN(num, 2);
            host.Delivered();
            Json($$"""[{{StateFailed}},{"kind":"value","value":2,"ops":[["r",2]],"retired":true}]""", StateCalls(calls), "the calls");
        }),
        Case("state.derive.the-retired-flag-a-retirement-visits-each-derive-of-a-diamond-once-not-once-per-path-40-derives", async () =>
        {
            var host = new StateHost();
            var list = host.Doc("""{"items":[]}""");
            var visits = new StateVisits();
            Derive Counted(Derive derive)
            {
                derive.Dependents.Add(visits);
                return derive;
            }
            static double Number(object? value) => ChordJson.AsNumber((JsonNode?)value)!.Value;
            var previous = Counted(host.Derive([list], values => ((JsonNode)values[0]!)["items"]!.AsArray().Count));
            var derives = 1;
            // 13 layers of two derives joined by a third: 2^13 paths from the top to the bottom.
            for (var layer = 0; layer < 13; layer++)
            {
                var left = Counted(host.Derive([previous], values => Number(values[0]) + 1));
                var right = Counted(host.Derive([previous], values => Number(values[0]) + 2));
                previous = Counted(host.Derive([left, right], values => Number(values[0]) + Number(values[1])));
                derives += 3;
            }
            Equal(40, derives, "the derives");
            list.Set("""{"items":["a"]}""", StateBg);
            var frames = ChangesOf(await previous.WatchAsync(StateBg));
            list.Retire(StateBg);
            host.Delivered();
            // Every derive walks its dependents once: its counter is called once.
            Check(visits.Retired < 100, $"visits: {visits.Retired}");
            Equal(40, visits.Retired, "one walk per derive");
            Check(frames[^1] is WatchValueCall<JsonNode?> { Retired: true }, "the bottom's last call is retired");
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] StateDetachedLabel(StateHost host, Derive n)
    {
        var label = host.Derive([n], values => values[0] is JsonNode node ? node.DeepClone() : null);
        Json("0", label.Value, "the label");
        Check(n.Dependents.Contains(label), "its source keeps it while attached");
        // Its object ended (upstream releases the viewer's reference).
        label.Detach(new InvalidOperationException("app.viewer: ended"));
        return [new WeakReference(label)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IWatchHandle<JsonNode?> Watch, WeakReference[] References)> StateFailedWatch()
    {
        var host = new StateHost();
        var num = host.Doc("""{"n":0}""");
        var t = host.Derive([num], values => StateN(values[0]) == 1 ? throw DeriveThrew : StateN(values[0]));
        var watch = await t.WatchAsync(StateBg);
        var calls = ChangesOf(watch);
        StateSetN(num, 1);
        host.Delivered();
        Json($"[{StateFailed}]", StateCalls(calls), "the calls");
        // The reference is released: its object ends, which detaches the derive.
        t.Detach(new InvalidOperationException("app.paged(1): ended"));
        return (watch, [new WeakReference(t), new WeakReference(num), new WeakReference(host)]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> StateFreshError(List<object?> kept)
    {
        var host = new StateHost();
        var num = host.Doc("""{"n":0}""");
        // A fresh error: thrown in the compute, whose closure holds the document.
        var t = host.Derive([num], values => StateN(values[0]) == 1 ? throw new InvalidOperationException("fresh") : StateN(values[0]));
        (await t.WatchAsync(StateBg)).Start((call, _) =>
        {
            if (call is WatchFailedCall<JsonNode?> failed) kept.Add(failed.Error);
            return Task.CompletedTask;
        });
        StateSetN(num, 1);
        host.Delivered();
        // The host closes: its objects end, which detaches the derive.
        t.Detach(new InvalidOperationException("closed"));
        return [new WeakReference(t), new WeakReference(num), new WeakReference(host)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] StateMappedNote(Func<IReadOnlyList<JsonNode>, JsonNode[]> map)
    {
        var note = Parse("""{"text":"t"}""");
        var result = map([note])[0];
        return [new WeakReference(note), new WeakReference(result)];
    }
}

/// <summary>A dependent counting the retirement walks that reach it (the diamond case's stand-in for upstream's spy).</summary>
internal sealed class StateVisits : IDependent
{
    public int Retired { get; private set; }

    public void Invalidate()
    {
    }

    public void SourceRetired(ReferenceSet<IDependent> visited) => Retired++;
}
