using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/test/delta-tracker/tracker.test.ts, the tracker cases of
// delta.test.ts and delta-apply-immutable.test.ts, and delta-clone.test.ts, adapted to the explicit draft API (decision 0005): property
// assignment is Set, `+=` on a string is AppendString, push/unshift/pop/shift are Add/Insert/RemoveAt, fill/copyWithin are expressed with
// Set, and JS TypeError/Error are InvalidOperationException with upstream's message words. Not ported: Promise thenable inspection,
// borrowed mutators on foreign receivers, valueOf coercion side effects, prototype/accessor/inherited-setter/null-prototype cases and
// non-strict placement types (C# values are immutable strict JSON by construction), reentrant sort comparators (out of contract; the
// comparison sees immutable values), alias-freedom of placements (immutable values may be shared), and retention.test.ts (JS GC).
internal static partial class Program
{
    private static DraftObject Root(Change change) => change.State.AsObject();
    private static double Num(JsonValue value) => ((JsonNumberValue)value).Value;
    private static string Str(JsonValue value) => ((JsonStringValue)value).Value;
    private static JsonArrayValue OpsJson(IEnumerable<Op> ops) => JsonValue.Array(ops.Select(op => (JsonValue)op.ToJson()));

    private static void OpsAre(string expected, Prepared prepared) =>
        Check(JsonValue.DeepEquals(J(expected), OpsJson(prepared.Ops)), $"ops: expected {expected}, actual {OpsJson(prepared.Ops).ToJson()}");

    private static void HasOp(string expected, Prepared prepared) =>
        Check(prepared.Ops.Any(op => JsonValue.DeepEquals(J(expected), op.ToJson())), $"ops {OpsJson(prepared.Ops).ToJson()} lack {expected}");

    /// <summary>Immutable replay and wire round trip of a prepared batch reproduce its value.</summary>
    private static void Replays(Prepared prepared, string what = "replay")
    {
        Check(JsonValue.DeepEquals(DeltaApply.Apply(prepared.Base, prepared.Ops), prepared.Value), what + ": applyImmutable(base, ops) equals value");
        var wire = new DeltaDecoder().Decode(new DeltaEncoder().Encode(prepared.Ops));
        Check(JsonValue.DeepEquals(DeltaApply.Apply(prepared.Base, wire), prepared.Value), what + ": encoded replay equals value");
    }

    private static void ThrowsMatching(Action run, string fragment, string what)
    {
        var message = Throws<InvalidOperationException>(run, what).Message;
        Check(message.Contains(fragment, StringComparison.Ordinal), $"{what}: message '{message}' lacks '{fragment}'");
    }

    /// <summary>Upstream's settle(): prepare, check replay and base stability, adopt by pointer swap.</summary>
    private static JsonValue Settle(Tracker tracker, Action<Draft> mutate)
    {
        var baseRoot = tracker.Value;
        var baseJson = baseRoot.ToJson();
        var change = tracker.BeginChange();
        mutate(change.State);
        var prepared = change.Prepare();
        Replays(prepared);
        Equal(baseJson, baseRoot.ToJson(), "base unchanged by prepare");
        tracker.Adopt(prepared);
        Check(ReferenceEquals(tracker.Value, prepared.Value), "adopted by pointer swap");
        return prepared.Value;
    }

    private static IEnumerable<(string, Func<Task>)> ChordTrackerLifecycleCases() =>
    [
        Case("chord.tracker.materializes-and-adopts-by-pointer-swap", () =>
        {
            var initial = J("{\"count\":1,\"nested\":{\"text\":\"a\"},\"values\":[1]}");
            var tracker = Tracker.Track(initial);
            Check(ReferenceEquals(tracker.Value, initial), "track takes the root");
            var change = tracker.BeginChange();
            var state = Root(change);
            state.Set("count", 2);
            state.GetObject("nested").AppendString("text", "b");
            state.GetArray("values").Add(2);
            Same("{\"count\":1,\"nested\":{\"text\":\"a\"},\"values\":[1]}", initial, "initial during draft");
            var prepared = change.Prepare();
            Equal(0L, prepared.BaseRevision, "base revision");
            Check(ReferenceEquals(prepared.Base, initial) && !ReferenceEquals(prepared.Value, initial), "base and candidate");
            Same("{\"count\":2,\"nested\":{\"text\":\"ab\"},\"values\":[1,2]}", prepared.Value, "candidate");
            OpsAre("[[\"s\",[\"count\"],2],[\"a\",[\"nested\",\"text\"],\"b\"],[\"p\",[\"values\"],1,0,[2]]]", prepared);
            Check(ReferenceEquals(tracker.Value, initial), "prepare does not change authority");
            tracker.Adopt(prepared);
            Same("{\"count\":1,\"nested\":{\"text\":\"a\"},\"values\":[1]}", initial, "initial after adopt");
            Check(ReferenceEquals(tracker.Value, prepared.Value) && ReferenceEquals(prepared.Base, initial), "pointer swap");
            Equal(1L, tracker.Revision, "revision");
            ThrowsMatching(() => state.Get("count"), "settled", "draft after prepare");
            ThrowsMatching(() => _ = change.State, "settled", "state after prepare");
        }),
        Case("chord.tracker.keeps-a-change-live-across-await", async () =>
        {
            var tracker = Tracker.Track(J("{\"left\":0,\"nested\":{\"right\":0}}"));
            var change = tracker.BeginChange();
            Root(change).Set("left", 1);
            await Task.Yield();
            Root(change).GetObject("nested").Set("right", 2);
            var state = Root(change);
            var prepared = change.Prepare();
            Same("{\"left\":1,\"nested\":{\"right\":2}}", prepared.Value, "candidate");
            ThrowsMatching(() => state.Set("left", 3), "settled", "write after prepare");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.revokes-large-drafts-at-prepare", () =>
        {
            var tracker = Tracker.Track(JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(
                Enumerable.Range(0, 4_100).Select(value => (JsonValue)JsonValue.Object(KeyValuePair.Create("value", (JsonValue)value)))))));
            var change = tracker.BeginChange();
            var rows = Root(change).GetArray("rows");
            for (var index = 0; index < rows.Count; index++) Check(Num(rows.GetObject(index)["value"]) >= 0, "read");
            rows.GetObject(0).Set("value", -1);
            var prepared = change.Prepare();
            ThrowsMatching(() => _ = rows.Count, "settled", "large draft after prepare");
            tracker.Adopt(prepared);
            Equal(-1d, Num(At(tracker.Value, "rows", 0, "value")), "written");
        }),
        Case("chord.tracker.aborts-idempotently-and-revokes-held-descendants", () =>
        {
            var tracker = Tracker.Track(J("{\"child\":{\"value\":1}}"));
            var change = tracker.BeginChange();
            var child = Root(change).GetObject("child");
            child.Set("value", 2);
            change.Abort();
            change.Abort();
            Equal(1d, Num(At(tracker.Value, "child", "value")), "committed value");
            ThrowsMatching(() => child.Get("value"), "settled", "held descendant");
            ThrowsMatching(() => change.Prepare(), "settled", "prepare after abort");
        }),
        Case("chord.tracker.allows-competing-changes-and-invalidates-losers", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0}"));
            var first = tracker.BeginChange();
            var second = tracker.BeginChange();
            Root(first).Set("value", 1);
            Root(second).Set("value", 2);
            var firstPrepared = first.Prepare();
            var secondPrepared = second.Prepare();
            var held = secondPrepared.Value;
            tracker.Adopt(firstPrepared);
            Equal(1d, Num(At(tracker.Value, "value")), "winner");
            Equal(2d, Num(At(held, "value")), "loser candidate readable");
            ThrowsMatching(() => tracker.Adopt(secondPrepared), "stale", "loser");
            ThrowsMatching(() => tracker.Adopt(firstPrepared), "already been used", "repeat");
        }),
        Case("chord.tracker.invalidates-competing-open-drafts", () =>
        {
            var initial = J("{\"rows\":[{\"value\":0},{\"value\":1}]}");
            var tracker = Tracker.Track(initial);
            var staleChange = tracker.BeginChange();
            var held = Root(staleChange).GetArray("rows").GetObject(1);
            held.Set("value", 2);
            var winner = tracker.BeginChange();
            Root(winner).GetArray("rows").GetObject(0).Set("value", 3);
            tracker.Adopt(winner.Prepare());
            Same("{\"rows\":[{\"value\":0},{\"value\":1}]}", initial, "initial");
            Same("{\"rows\":[{\"value\":3},{\"value\":1}]}", tracker.Value, "adopted");
            ThrowsMatching(() => held.Get("value"), "settled", "stale handle");
            ThrowsMatching(() => staleChange.Prepare(), "settled", "stale prepare");
            staleChange.Abort();
        }),
        Case("chord.tracker.adopts-object-edits-as-immutable-revisions", () =>
        {
            var initial = J("{\"first\":1,\"second\":2}");
            var tracker = Tracker.Track(initial);
            var change = tracker.BeginChange();
            Root(change).Set("first", 3);
            Root(change).Delete("second");
            Root(change).Set("third", 4);
            tracker.Adopt(change.Prepare());
            Check(!ReferenceEquals(tracker.Value, initial), "new revision");
            Same("{\"first\":1,\"second\":2}", initial, "initial");
            Equal("{\"first\":3,\"third\":4}", tracker.Value.ToJson(), "adopted");
        }),
        Case("chord.tracker.supports-adopt-then-publish-ordering", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0,\"nested\":{\"count\":0}}"));
            var change = tracker.BeginChange();
            Root(change).Set("value", 1);
            Root(change).GetObject("nested").Set("count", 1);
            var prepared = change.Prepare();
            var operations = prepared.Ops;
            tracker.Adopt(prepared);
            Check(prepared.Ops == operations, "ops stable after adopt");
            var published = prepared.Ops.Length == 0 ? null : prepared.Value;
            Equal(2, operations.Length, "op count");
            Check(published is not null && ReferenceEquals(published, tracker.Value), "published value is authority");
            Same("{\"value\":1,\"nested\":{\"count\":1}}", published!, "published");
            var next = tracker.BeginChange();
            Root(next).Set("value", 2);
            tracker.Adopt(next.Prepare());
            Check(!ReferenceEquals(published, tracker.Value), "retained publication is older");
            Same("{\"value\":1,\"nested\":{\"count\":1}}", prepared.Value, "retained");
            Same("{\"value\":2,\"nested\":{\"count\":1}}", tracker.Value, "current");
        }),
        Case("chord.tracker.replacement-base-and-value-stay-readable", () =>
        {
            var original = J("{\"value\":0}");
            var replacement = J("{\"value\":1}");
            var tracker = Tracker.Track(original);
            var prepared = tracker.PrepareReplace(replacement);
            Check(ReferenceEquals(prepared.Base, original) && ReferenceEquals(prepared.Value, replacement), "before adopt");
            tracker.Adopt(prepared);
            Check(ReferenceEquals(prepared.Base, original) && ReferenceEquals(prepared.Value, replacement) && ReferenceEquals(tracker.Value, replacement), "after adopt");
        }),
        Case("chord.tracker.aborted-and-stale-candidates-stay-readable", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0}"));
            var abortedChange = tracker.BeginChange();
            Root(abortedChange).Set("value", 1);
            var aborted = abortedChange.Prepare();
            var abortedView = aborted.Value;
            aborted.Abort();
            Equal(1d, Num(At(abortedView, "value")), "aborted view");
            var loserChange = tracker.BeginChange();
            Root(loserChange).Set("value", 2);
            var loser = loserChange.Prepare();
            tracker.Adopt(tracker.PrepareReplace(J("{\"value\":3}")));
            Equal(2d, Num(At(loser.Value, "value")), "stale view");
        }),
        Case("chord.tracker.settled-change-abort-prevents-adoption", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0,\"nested\":{\"value\":1}}"));
            var change = tracker.BeginChange();
            Root(change).Set("value", 1);
            var prepared = change.Prepare();
            var operations = prepared.Ops;
            change.Abort();
            change.Abort();
            Check(prepared.Ops == operations, "ops kept");
            Same("{\"value\":1,\"nested\":{\"value\":1}}", prepared.Value, "candidate kept");
            ThrowsMatching(() => tracker.Adopt(prepared), "aborted", "adopt aborted");
        }),
        Case("chord.tracker.rejects-foreign-and-aborted-preparations", () =>
        {
            var first = Tracker.Track(J("{\"value\":0}"));
            var second = Tracker.Track(J("{\"value\":0}"));
            var prepared = first.PrepareReplace(J("{\"value\":1}"));
            ThrowsMatching(() => second.Adopt(prepared), "different tracker", "foreign");
            prepared.Abort();
            prepared.Abort();
            ThrowsMatching(() => first.Adopt(prepared), "aborted", "aborted");
        }),
        Case("chord.tracker.reads-deleted-properties-as-absent", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":1}"));
            var change = tracker.BeginChange();
            var state = Root(change);
            state.Delete("value");
            state.Set("added", 1);
            state.Delete("added");
            Check(state.Get("value") is null && state.Get("added") is null && !state.ContainsKey("value"), "absent");
            Equal(0, state.Count, "count");
            var prepared = change.Prepare();
            OpsAre("[[\"d\",[\"value\"]]]", prepared);
            tracker.Adopt(prepared);
            Equal(0, ((JsonObjectValue)tracker.Value).Count, "no keys");
        }),
        Case("chord.tracker.detaches-old-handles-for-deeply-equal-assignments", () =>
        {
            var initial = J("{\"object\":{\"nested\":{\"value\":1}},\"array\":[{\"value\":1}],\"rows\":[{\"value\":1}]}");
            var tracker = Tracker.Track(initial);
            var change = tracker.BeginChange();
            var state = Root(change);
            var oldObject = state.GetObject("object");
            var oldArray = state.GetArray("array");
            var oldRow = state.GetArray("rows").GetObject(0);
            state.Set("object", J("{\"nested\":{\"value\":1}}"));
            state.Set("array", J("[{\"value\":1}]"));
            state.GetArray("rows").Set(0, J("{\"value\":1}"));
            Check(!ReferenceEquals(state.GetObject("object"), oldObject) && !ReferenceEquals(state.GetArray("array"), oldArray) &&
                !ReferenceEquals(state.GetArray("rows").GetObject(0), oldRow), "new handles");
            oldObject.GetObject("nested").Set("value", 9);
            oldArray.GetObject(0).Set("value", 9);
            oldRow.Set("value", 9);
            Same("{\"object\":{\"nested\":{\"value\":1}},\"array\":[{\"value\":1}],\"rows\":[{\"value\":1}]}", state.ToValue(), "detached writes ignored");
            var prepared = change.Prepare();
            OpsAre("[]", prepared);
            tracker.Adopt(prepared);
            Check(ReferenceEquals(tracker.Value, initial) && ReferenceEquals(At(tracker.Value, "object"), At(initial, "object")) &&
                ReferenceEquals(At(tracker.Value, "array"), At(initial, "array")) && ReferenceEquals(At(tracker.Value, "rows", 0), At(initial, "rows", 0)), "identity kept");
        }),
        Case("chord.tracker.normalizes-a-deeply-equal-replacement", () =>
        {
            var initial = J("{\"nested\":{\"value\":1},\"rows\":[1,2,3]}");
            var tracker = Tracker.Track(initial);
            var prepared = tracker.PrepareReplace(J("{\"nested\":{\"value\":1},\"rows\":[1,2,3]}"));
            OpsAre("[]", prepared);
            tracker.Adopt(prepared);
            Check(ReferenceEquals(tracker.Value, initial), "identity kept");
        }),
        Case("chord.tracker.replacement-takes-o1-ownership", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0,\"rows\":[]}"));
            var replacement = J("{\"value\":1,\"rows\":[{\"value\":2}]}");
            var prepared = tracker.PrepareReplace(replacement);
            Check(ReferenceEquals(prepared.Base, tracker.Value) && ReferenceEquals(prepared.Value, replacement), "base and value");
            Check(prepared.Ops is [ReplaceOp { Value: var payload }] && ReferenceEquals(payload, replacement), "r carries the replacement itself");
            tracker.Adopt(prepared);
            Check(ReferenceEquals(tracker.Value, replacement), "adopted");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> ChordTrackerPolicyCases() =>
    [
        Case("chord.tracker.supports-reads-keys-iteration-and-json", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[1,2,3],\"object\":{\"a\":1}}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            Same("[2]", JsonValue.Array(values.Splice(1, 1, 4, 5)), "removed");
            Root(change).GetObject("object").Set("b", 2);
            Equal(4, values.Count, "count");
            Same("[1,4,5,3]", values.ToValue(), "values");
            Same("[2,8,10,6]", JsonValue.Array(Enumerable.Range(0, values.Count).Select(index => (JsonValue)(Num(values[index]) * 2))), "map");
            Equal(5d, Num(values.Get(2)), "index read");
            Check(Root(change).GetObject("object").Keys.SequenceEqual(["a", "b"]), "keys");
            Equal("{\"values\":[1,4,5,3],\"object\":{\"a\":1,\"b\":2}}", change.State.ToValue().ToJson(), "json");
            change.Abort();
        }),
        Case("chord.tracker.document-keys-are-plain-data", () =>
        {
            var tracker = Tracker.Track(J("{\"object\":{\"context\":1,\"base\":2,\"parent\":3,\"dirty\":4,\"target\":5,\"proxy\":6}}"));
            var change = tracker.BeginChange();
            var obj = Root(change).GetObject("object");
            obj.Set("context", 7);
            obj.Set("proxy", 8);
            Check(obj.Keys.SequenceEqual(["context", "base", "parent", "dirty", "target", "proxy"]), "keys");
            var prepared = change.Prepare();
            Equal("{\"context\":7,\"base\":2,\"parent\":3,\"dirty\":4,\"target\":5,\"proxy\":8}", At(prepared.Value, "object").ToJson(), "value");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.emits-string-operation-forms", () =>
        {
            var tracker = Tracker.Track(J("{\"text\":\"abcdefgh\",\"values\":[1,2,3]}"));
            var change = tracker.BeginChange();
            Root(change).Set("text", Str(Root(change)["text"])[3..] + "xyz");
            Root(change).GetArray("values").Splice(1, 1, 4);
            var prepared = change.Prepare();
            Same("{\"text\":\"defghxyz\",\"values\":[1,4,3]}", prepared.Value, "candidate");
            HasOp("[\"t\",[\"text\"],3]", prepared);
            HasOp("[\"a\",[\"text\"],\"xyz\"]", prepared);
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.emits-deletion-and-supports-root-arrays", () =>
        {
            var objectTracker = Tracker.Track(J("{\"keep\":1,\"remove\":2}"));
            Settle(objectTracker, draft => draft.AsObject().Delete("remove"));
            Same("{\"keep\":1}", objectTracker.Value, "object");
            var arrayTracker = Tracker.Track(J("[1,2,3]"));
            Settle(arrayTracker, draft =>
            {
                draft.AsArray().Reverse();
                draft.AsArray().Add(4);
            });
            Same("[3,2,1,4]", arrayTracker.Value, "root array");
        }),
        Case("chord.tracker.preserves-key-order-after-delete-and-readd", () =>
        {
            var tracker = Tracker.Track(J("{\"first\":1,\"second\":2}"));
            var change = tracker.BeginChange();
            Root(change).Delete("first");
            Root(change).Set("first", 1);
            Check(Root(change).Keys.SequenceEqual(["second", "first"]), "draft keys");
            var prepared = change.Prepare();
            Replays(prepared);
            tracker.Adopt(prepared);
            Check(((JsonObjectValue)tracker.Value).Keys.SequenceEqual(["second", "first"]), "adopted keys");
        }),
        Case("chord.tracker.orders-new-integer-keys-before-strings", () =>
        {
            var tracker = Tracker.Track(J("{\"object\":{\"label\":\"x\"},\"first\":\"\"}"));
            var change = tracker.BeginChange();
            var obj = Root(change).GetObject("object");
            obj.Set("2", "two");
            obj.Set("1", "one");
            Check(obj.Keys.SequenceEqual(["1", "2", "label"]), "draft keys");
            Root(change).Set("first", obj.Keys[0]);
            var prepared = change.Prepare();
            Equal("1", Str(At(prepared.Value, "first")), "first");
            Check(((JsonObjectValue)At(prepared.Value, "object")).Keys.SequenceEqual(["1", "2", "label"]), "candidate keys");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.normalizes-deeply-equal-container-assignments", () =>
        {
            var initial = J("{\"child\":{\"a\":1,\"b\":2},\"count\":0}");
            var tracker = Tracker.Track(initial);
            var change = tracker.BeginChange();
            Root(change).Set("child", J("{\"b\":2,\"a\":1}"));
            Root(change).Set("count", 1);
            var prepared = change.Prepare();
            var replayed = DeltaApply.Apply(initial, prepared.Ops);
            OpsAre("[[\"s\",[\"count\"],1]]", prepared);
            Check(((JsonObjectValue)At(prepared.Value, "child")).Keys.SequenceEqual(["a", "b"]), "candidate keeps base order");
            Check(((JsonObjectValue)At(replayed, "child")).Keys.SequenceEqual(["a", "b"]), "replay keeps base order");
            Check(ReferenceEquals(At(prepared.Value, "child"), At(initial, "child")), "base reference kept");
            Equal(replayed.ToJson(), prepared.Value.ToJson(), "replay equals candidate");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.keeps-integer-and-string-order-across-readdition", () =>
        {
            var tracker = Tracker.Track(J("{\"object\":{\"1\":\"one\",\"2\":\"two\",\"first\":\"a\",\"second\":\"b\"}}"));
            var change = tracker.BeginChange();
            var obj = Root(change).GetObject("object");
            obj.Delete("2");
            obj.Set("2", "two");
            obj.Delete("first");
            obj.Set("first", "a");
            obj.Set("3", "three");
            obj.Set("0", "zero");
            string[] expected = ["0", "1", "2", "3", "second", "first"];
            Check(obj.Keys.SequenceEqual(expected), "draft keys");
            var prepared = change.Prepare();
            Check(((JsonObjectValue)At(prepared.Value, "object")).Keys.SequenceEqual(expected), "candidate keys");
            Replays(prepared);
            tracker.Adopt(prepared);
            Check(((JsonObjectValue)At(tracker.Value, "object")).Keys.SequenceEqual(expected), "adopted keys");
        }),
        Case("chord.tracker.keeps-repeated-identical-writes", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0,\"values\":[0]}"));
            Settle(tracker, draft =>
            {
                var state = draft.AsObject();
                state.Set("value", 1);
                state.Set("value", 1);
                var values = state.GetArray("values");
                values.Set(0, JsonValue.Null);
                values.Set(0, JsonValue.Null);
                values.Add(2);
                values.Set(1, JsonValue.Null);
                values.Set(1, JsonValue.Null);
            });
            Same("{\"value\":1,\"values\":[null,null]}", tracker.Value, "adopted");
        }),
        Case("chord.tracker.rejects-array-gaps-and-allows-replacement-and-append", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[1,2]}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            ThrowsMatching(() => values.Set(3, 4), "holes", "gap");
            Same("[1,2]", values.ToValue(), "draft unchanged");
            values.Set(1, 9);
            values.Set(2, 3);
            var prepared = change.Prepare();
            Same("[1,9,3]", At(prepared.Value, "values"), "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.deletion-null-growth-shrinking-and-default-sort", () =>
        {
            var tracker = Tracker.Track(J("{\"optional\":\"remove\",\"values\":[3,1,2]}"));
            Settle(tracker, draft =>
            {
                draft.AsObject().Delete("optional");
                var values = draft.AsObject().GetArray("values");
                values.SetLength(5);
                Same("[3,1,2,null,null]", values.ToValue(), "grown");
                values.SetLength(4);
                values.Sort();
            });
            Same("{\"values\":[1,2,3,null]}", tracker.Value, "adopted");
        }),
        Case("chord.tracker.enumerates-wide-objects-without-duplicates", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":{}}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetObject("values");
            for (var index = 0; index < 20_000; index++) values.Set("field" + index, index);
            var keys = values.Keys;
            Equal(20_000, keys.Count, "key count");
            Equal(20_000, keys.Distinct().Count(), "distinct");
            Check(keys[0] == "field0" && keys[^1] == "field19999", "order");
            change.Abort();
        }),
        Case("chord.tracker.read-only-traversals-stay-clean", () =>
        {
            var tracker = Tracker.Track(J("{\"nested\":{\"rows\":[{\"value\":1}]}}"));
            var change = tracker.BeginChange();
            Equal(1d, Num(Root(change).GetObject("nested").GetArray("rows").GetObject(0)["value"]), "read");
            var prepared = change.Prepare();
            OpsAre("[]", prepared);
            Check(ReferenceEquals(prepared.Value, prepared.Base), "no-op keeps identity");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.one-draft-identity-per-container", () =>
        {
            var tracker = Tracker.Track(J("{\"nested\":{\"value\":1},\"rows\":[{\"value\":2}]}"));
            var change = tracker.BeginChange();
            var state = Root(change);
            Check(ReferenceEquals(change.State, change.State), "root");
            Check(ReferenceEquals(state.GetObject("nested"), state.GetObject("nested")), "object");
            Check(ReferenceEquals(state.GetArray("rows"), state.GetArray("rows")), "array");
            Check(ReferenceEquals(state.GetArray("rows").GetObject(0), state.GetArray("rows").GetObject(0)), "element");
            change.Abort();
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> ChordTrackerPlacementCases() =>
    [
        Case("chord.tracker.places-values-by-value", () =>
        {
            var tracker = Tracker.Track(J("{\"property\":null,\"values\":[{\"value\":0},{\"value\":1},{\"value\":2}]}"));
            var external = J("{\"value\":5}");
            var change = tracker.BeginChange();
            var state = Root(change);
            var values = state.GetArray("values");
            state.Set("property", external);
            values.Set(0, external);
            values.Add(external);
            values.Insert(0, external);
            values.Splice(2, 0, external);
            var fill = values[0];
            values.Set(1, fill);
            values.Set(2, fill);
            var copied = new[] { values[0], values[1] };
            values.Set(3, copied[0]);
            values.Set(4, copied[1]);
            var prepared = change.Prepare();
            Same("{\"value\":5}", At(prepared.Value, "property"), "property");
            Same("[{\"value\":5},{\"value\":5},{\"value\":5},{\"value\":5},{\"value\":5}]",
                JsonValue.Array(((JsonArrayValue)At(prepared.Value, "values")).Take(5)), "placements");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.rejects-null-placements-before-changing-the-draft", () =>
        {
            var initial = J("{\"optional\":\"remove\",\"payload\":null,\"number\":0,\"values\":[1,2]}");
            var tracker = Tracker.Track(initial);
            var change = tracker.BeginChange();
            var state = Root(change);
            var values = state.GetArray("values");
            Throws<ArgumentNullException>(() => values.Set(0, null!), "index");
            Throws<ArgumentNullException>(() => values.AddRange([J("{\"staged\":true}"), null!]), "push");
            Throws<ArgumentNullException>(() => values.Insert(0, null!), "unshift");
            Throws<ArgumentNullException>(() => values.Splice(1, 0, [null!]), "splice");
            Throws<ArgumentNullException>(() => state.Set("payload", null!), "property");
            Throws<ArgumentException>(() => state.Set("number", JsonValue.Number(double.NaN)), "NaN");
            Throws<ArgumentException>(() => state.Set("number", JsonValue.Number(double.PositiveInfinity)), "Infinity");
            Check(JsonValue.DeepEquals(state.ToValue(), initial) && ReferenceEquals(state.ToValue(), initial), "draft unchanged");
            state.Delete("optional");
            state.Set("payload", J("{\"valid\":true}"));
            values.Add(J("{\"valid\":true}"));
            var prepared = change.Prepare();
            Same("{\"payload\":{\"valid\":true},\"number\":0,\"values\":[1,2,{\"valid\":true}]}", prepared.Value, "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.places-one-value-at-several-paths-independently", () =>
        {
            var tracker = Tracker.Track(J("{\"left\":null,\"right\":null,\"rows\":[]}"));
            var shared = J("{\"nested\":{\"value\":1}}");
            var change = tracker.BeginChange();
            var state = Root(change);
            state.Set("left", shared);
            state.Set("right", shared);
            state.GetArray("rows").AddRange([shared, shared]);
            state.GetObject("left").GetObject("nested").Set("value", 9);
            state.GetArray("rows").GetObject(0).GetObject("nested").Set("value", 8);
            var prepared = change.Prepare();
            Same("{\"left\":{\"nested\":{\"value\":9}},\"right\":{\"nested\":{\"value\":1}},\"rows\":[{\"nested\":{\"value\":8}},{\"nested\":{\"value\":1}}]}",
                prepared.Value, "candidate");
            Check(!ReferenceEquals(At(prepared.Value, "left"), At(prepared.Value, "right")), "left and right independent");
            Check(!ReferenceEquals(At(prepared.Value, "rows", 0), At(prepared.Value, "rows", 1)), "rows independent");
            Same("{\"nested\":{\"value\":1}}", shared, "payload unchanged");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.copies-draft-sourced-placements-across-revisions", () =>
        {
            var tracker = Tracker.Track(J("{\"a\":{\"child\":{\"value\":1}},\"b\":null,\"rows\":[{\"child\":{\"value\":1}},{\"child\":{\"value\":2}}]}"));
            var first = tracker.BeginChange();
            var state = Root(first);
            state.Set("b", state.GetObject("a").ToValue());
            state.GetObject("b").GetObject("child").Set("value", 3);
            state.GetArray("rows").Set(1, state.GetArray("rows")[0]);
            var firstPrepared = first.Prepare();
            Equal(1d, Num(At(firstPrepared.Value, "a", "child", "value")), "a");
            Equal(3d, Num(At(firstPrepared.Value, "b", "child", "value")), "b");
            Check(!ReferenceEquals(At(firstPrepared.Value, "a"), At(firstPrepared.Value, "b")), "a and b independent");
            Replays(firstPrepared);
            tracker.Adopt(firstPrepared);
            var second = tracker.BeginChange();
            Root(second).GetArray("rows").GetObject(1).GetObject("child").Set("value", 4);
            var secondPrepared = second.Prepare();
            Same("[{\"child\":{\"value\":1}},{\"child\":{\"value\":4}}]", At(secondPrepared.Value, "rows"), "rows");
            Replays(secondPrepared);
            tracker.Adopt(secondPrepared);
        }),
        Case("chord.tracker.distinguishes-committed-values-from-draft-values", () =>
        {
            var tracker = Tracker.Track(J("{\"source\":{\"value\":1},\"rawCopy\":null,\"draftCopy\":null}"));
            var raw = At(tracker.Value, "source");
            var change = tracker.BeginChange();
            var state = Root(change);
            state.GetObject("source").Set("value", 2);
            state.Set("rawCopy", raw);
            state.Set("draftCopy", state.GetObject("source").ToValue());
            var prepared = change.Prepare();
            Same("{\"source\":{\"value\":2},\"rawCopy\":{\"value\":1},\"draftCopy\":{\"value\":2}}", prepared.Value, "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.folds-edits-to-introduced-subtrees-into-placements", () =>
        {
            var tracker = Tracker.Track(J("{\"nested\":null,\"rows\":[]}"));
            var change = tracker.BeginChange();
            var state = Root(change);
            state.Set("nested", J("{\"rows\":[{\"value\":1}]}"));
            state.GetObject("nested").GetArray("rows").GetObject(0).Set("value", 2);
            state.GetObject("nested").GetArray("rows").Add(J("{\"value\":3}"));
            state.GetArray("rows").Add(J("{\"values\":[1]}"));
            state.GetArray("rows").GetObject(0).GetArray("values").Add(2);
            var prepared = change.Prepare();
            OpsAre("[[\"s\",[\"nested\"],{\"rows\":[{\"value\":2},{\"value\":3}]}],[\"p\",[\"rows\"],0,0,[{\"values\":[1,2]}]]]", prepared);
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.folds-pathological-operation-counts", () =>
        {
            var wide = JsonValue.Object(Enumerable.Range(0, 5_000).Select(index => KeyValuePair.Create("field" + index, (JsonValue)0)));
            var wideTracker = Tracker.Track(wide);
            var wideChange = wideTracker.BeginChange();
            for (var index = 0; index < 5_000; index++) Root(wideChange).Set("field" + index, 1);
            var widePrepared = wideChange.Prepare();
            Check(widePrepared.Ops is [ReplaceOp], "wide edit folds to r");
            Replays(widePrepared);
            wideTracker.Adopt(widePrepared);

            var sparseTracker = Tracker.Track(JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(
                Enumerable.Range(0, 15_000).Select(value => (JsonValue)JsonValue.Object(KeyValuePair.Create("value", (JsonValue)value)))))));
            var sparseChange = sparseTracker.BeginChange();
            var rows = Root(sparseChange).GetArray("rows");
            for (var index = 0; index < 15_000; index += 3) rows.GetObject(index).Set("value", -index - 1);
            var sparsePrepared = sparseChange.Prepare();
            Check(sparsePrepared.Ops is [ReplaceOp], "sparse edit folds to r");
            Replays(sparsePrepared);
            sparseTracker.Adopt(sparsePrepared);
        }),
        Case("chord.tracker.shares-operation-placements-with-the-candidate", () =>
        {
            var tracker = Tracker.Track(J("{\"rows\":[]}"));
            var change = tracker.BeginChange();
            Root(change).GetArray("rows").Add(J("{\"value\":1}"));
            var prepared = change.Prepare();
            Check(prepared.Ops is [SpliceOp { Items: [var placed] }] && ReferenceEquals(placed, At(prepared.Value, "rows", 0)), "payload shared");
            tracker.Adopt(prepared);
            Check(ReferenceEquals(tracker.Value, prepared.Value), "adopted");
        }),
    ];

    private static JsonValue Rows(int count, Func<int, string> row) =>
        JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(Enumerable.Range(0, count).Select(index => J(row(index))))));

    private static JsonValue Values(int count, Func<int, string> row) =>
        JsonValue.Object(KeyValuePair.Create("values", (JsonValue)JsonValue.Array(Enumerable.Range(0, count).Select(index => J(row(index))))));

    private static IEnumerable<(string, Func<Task>)> ChordTrackerArrayCases() =>
    [
        Case("chord.tracker.sort-emits-a-detached-permutation", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[3,1,2]}"));
            var change = tracker.BeginChange();
            Root(change).GetArray("values").Sort((left, right) => Num(left).CompareTo(Num(right)));
            var prepared = change.Prepare();
            OpsAre("[[\"m\",[\"values\"],[1,2,0]]]", prepared);
            tracker.Adopt(prepared);
            Same("[1,2,3]", At(tracker.Value, "values"), "sorted");
        }),
        Case("chord.tracker.splice-with-zero-or-one-argument", () =>
        {
            var noArguments = Tracker.Track(J("{\"values\":[1,2,3]}"));
            var noArgumentsChange = noArguments.BeginChange();
            Equal(0, Root(noArgumentsChange).GetArray("values").Splice(0, 0).Length, "nothing removed");
            var noArgumentsPrepared = noArgumentsChange.Prepare();
            Same("[1,2,3]", At(noArgumentsPrepared.Value, "values"), "unchanged");
            OpsAre("[]", noArgumentsPrepared);
            noArguments.Adopt(noArgumentsPrepared);

            var oneArgument = Tracker.Track(J("{\"values\":[1,2,3,4]}"));
            var oneArgumentChange = oneArgument.BeginChange();
            Same("[2,3,4]", JsonValue.Array(Root(oneArgumentChange).GetArray("values").Splice(1)), "removed tail");
            var oneArgumentPrepared = oneArgumentChange.Prepare();
            Same("[1]", At(oneArgumentPrepared.Value, "values"), "candidate");
            Replays(oneArgumentPrepared);
            oneArgument.Adopt(oneArgumentPrepared);

            var pastEnd = Tracker.Track(J("{\"values\":[1,2,3]}"));
            var pastEndChange = pastEnd.BeginChange();
            Equal(0, Root(pastEndChange).GetArray("values").Splice(3).Length, "nothing past the end");
            var pastEndPrepared = pastEndChange.Prepare();
            Same("[1,2,3]", At(pastEndPrepared.Value, "values"), "unchanged");
            pastEnd.Adopt(pastEndPrepared);
        }),
        Case("chord.tracker.supports-all-structural-mutators", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[3,1,2]}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            values.Add(4);
            Equal(4, values.Count, "push");
            Equal(4d, Num(values.RemoveAt(values.Count - 1)), "pop");
            values.Insert(0, 0);
            Equal(4, values.Count, "unshift");
            Equal(0d, Num(values.RemoveAt(0)), "shift");
            Same("[1]", JsonValue.Array(values.Splice(1, 1, 5, 4)), "splice");
            values.Sort((left, right) => Num(left).CompareTo(Num(right)));
            values.Reverse();
            values.Set(1, 9);
            values.Set(2, 9);
            var copied = new[] { values[0], values[1] };
            values.Set(1, copied[0]);
            values.Set(2, copied[1]);
            values.Move(3, 3);
            var prepared = change.Prepare();
            Same("{\"values\":[5,5,9,2]}", prepared.Value, "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.held-handles-follow-reindexing-and-detach-on-removal", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[{\"value\":\"a\"},{\"value\":\"b\"},{\"value\":\"c\"}]}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            var held = values.GetObject(1);
            values.Insert(0, J("{\"value\":\"front\"}"));
            held.Set("value", "moved");
            Equal("moved", Str(values.GetObject(2)["value"]), "follows reindex");
            Check(ReferenceEquals(values.GetObject(2), held), "same handle");
            values.Splice(2, 1);
            held.Set("value", "detached");
            var prepared = change.Prepare();
            Same("{\"values\":[{\"value\":\"front\"},{\"value\":\"a\"},{\"value\":\"c\"}]}", prepared.Value, "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.replays-introduced-moved-then-edited-descendants", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[]}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            values.Add(J("{\"id\":1,\"nested\":[1]}"));
            var held = values.GetObject(0);
            values.Insert(0, J("{\"id\":0,\"nested\":[]}"));
            held.GetArray("nested").Add(2);
            values.Reverse();
            held.GetArray("nested").Add(3);
            var prepared = change.Prepare();
            Same("{\"values\":[{\"id\":1,\"nested\":[1,2,3]},{\"id\":0,\"nested\":[]}]}", prepared.Value, "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.combines-permutation-with-final-path-edits", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[{\"rank\":3,\"edited\":0},{\"rank\":1,\"edited\":0},{\"rank\":2,\"edited\":0}]}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            var held = values.GetObject(0);
            values.Sort((left, right) => Num(At(left, "rank")).CompareTo(Num(At(right, "rank"))));
            held.Set("edited", 1);
            var prepared = change.Prepare();
            Check(JsonValue.DeepEquals(J("[\"m\",[\"values\"],[1,2,0]]"), prepared.Ops[0].ToJson()), "permutation first: " + OpsJson(prepared.Ops).ToJson());
            OpsAre("[[\"m\",[\"values\"],[1,2,0]],[\"s\",[\"values\",2,\"edited\"],1]]", prepared);
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.folds-dense-child-and-index-edits-into-one-splice", () =>
        {
            var rowsTracker = Tracker.Track(Rows(1_000, value => $"{{\"value\":{value}}}"));
            var rowsChange = rowsTracker.BeginChange();
            var rows = Root(rowsChange).GetArray("rows");
            for (var index = 0; index < rows.Count; index++) rows.GetObject(index).Set("value", Num(rows.GetObject(index)["value"]) + 1);
            var rowsPrepared = rowsChange.Prepare();
            Check(rowsPrepared.Ops is [SpliceOp], "rows: one splice");
            Replays(rowsPrepared);
            rowsTracker.Adopt(rowsPrepared);

            var valuesTracker = Tracker.Track(Values(1_000, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var valuesChange = valuesTracker.BeginChange();
            for (var index = 0; index < 600; index++) Root(valuesChange).GetArray("values").Set(index, -index - 1);
            var valuesPrepared = valuesChange.Prepare();
            Check(valuesPrepared.Ops is [SpliceOp], "values: one splice");
            Replays(valuesPrepared);
            valuesTracker.Adopt(valuesPrepared);
        }),
        Case("chord.tracker.folds-only-a-deeply-nested-dense-region", () =>
        {
            var tracker = Tracker.Track(Rows(2_000, value => $"{{\"nested\":{{\"value\":{value}}}}}"));
            var change = tracker.BeginChange();
            var rows = Root(change).GetArray("rows");
            rows.GetObject(0).GetObject("nested").Set("value", -1);
            for (var index = 500; index < 1_000; index++) rows.GetObject(index).GetObject("nested").Set("value", -index);
            var prepared = change.Prepare();
            var splice = prepared.Ops.OfType<SpliceOp>().Single();
            Check(splice.Path is [{ Key: "rows" }] && splice.Index == 500 && splice.Remove == 500 && splice.Items.Length == 500, "regional splice");
            Equal(2, prepared.Ops.Length, "op count");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.reserved-key-folds-covered-by-an-outer-region", () =>
        {
            var rows = Enumerable.Range(0, 400).Select(index => index == 100
                ? J("{\"flag\":0,\"special\":{\"__proto__\":{\"values\":[" + string.Join(",", Enumerable.Range(0, 400)) + "]}}}")
                : J("{\"flag\":0}"));
            var tracker = Tracker.Track(JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(rows))));
            var change = tracker.BeginChange();
            var draftRows = Root(change).GetArray("rows");
            for (var index = 0; index < 256; index++) draftRows.GetObject(index).Set("flag", 1);
            for (var index = 0; index < 256; index++)
                draftRows.GetObject(100).GetObject("special").GetObject("__proto__").GetArray("values").Set(index, -index - 1);
            var prepared = change.Prepare();
            Check(prepared.Ops is [SpliceOp { Path: [{ Key: "rows" }], Index: 0, Remove: 256 }], "one outer splice: " + OpsJson(prepared.Ops).ToJson().Length);
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.outer-dense-region-suppresses-nested-operations", () =>
        {
            var tracker = Tracker.Track(JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(Enumerable.Range(0, 400).Select(index =>
                (JsonValue)JsonValue.Object(KeyValuePair.Create("flag", (JsonValue)0), KeyValuePair.Create("values", index == 100
                    ? (JsonValue)JsonValue.Array(Enumerable.Range(0, 400).Select(value => J($"{{\"value\":{value}}}")))
                    : JsonArrayValue.Empty)))))));
            var change = tracker.BeginChange();
            var rows = Root(change).GetArray("rows");
            for (var index = 0; index < 256; index++) rows.GetObject(index).Set("flag", 1);
            for (var index = 0; index < 256; index++) rows.GetObject(100).GetArray("values").GetObject(index).Set("value", -index - 1);
            rows.GetObject(100).GetArray("values").Add(J("{\"value\":999}"));
            var prepared = change.Prepare();
            Check(prepared.Ops is [SpliceOp { Path: [{ Key: "rows" }], Index: 0, Remove: 256 }], "one outer splice");
            Equal(401, ((JsonArrayValue)At(prepared.Value, "rows", 100, "values")).Count, "nested growth kept");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.emits-disjoint-dense-regions-and-outside-edits", () =>
        {
            var tracker = Tracker.Track(Values(1_400, value => $"{{\"value\":{value}}}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            int[] outside = [97, 358, 897, 1_158];
            foreach (var index in outside) values.GetObject(index).Set("value", -index - 1);
            for (var index = 100; index < 356; index++) values.GetObject(index).Set("value", -index - 1);
            for (var index = 900; index < 1_156; index++) values.GetObject(index).Set("value", -index - 1);
            var prepared = change.Prepare();
            var splices = prepared.Ops.OfType<SpliceOp>().Select(op => (op.Index, op.Remove)).ToList();
            Check(splices.SequenceEqual([(100L, 256L), (900L, 256L)]), "regions");
            foreach (var index in outside) HasOp($"[\"s\",[\"values\",{index},\"value\"],{-index - 1}]", prepared);
            Equal(6, prepared.Ops.Length, "op count");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.normalizes-a-20000-operation-queue-to-two-operations", () =>
        {
            var tracker = Tracker.Track(Values(20_000, value => $"{{\"value\":{value}}}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            for (var index = 0; index < 10_000; index++)
            {
                values.RemoveAt(0);
                values.Add(J($"{{\"value\":{20_000 + index}}}"));
            }
            var prepared = change.Prepare();
            Equal(2, prepared.Ops.Length, "op count");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.handles-adversarial-fragmentation", () =>
        {
            const int size = 20_000;
            var tracker = Tracker.Track(Values(size, value => $"{{\"value\":{value}}}"));
            var expected = ((JsonArrayValue)At(tracker.Value, "values")).ToList();
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            int[] heldAt = [1, 1_001, 5_001, 10_001, 15_001, 19_999];
            var held = heldAt.Select(values.GetObject).ToList();
            for (var index = 0; index < size; index += 2)
            {
                values.Splice(index, 1, J($"{{\"value\":{-index - 1}}}"));
                expected[index] = J($"{{\"value\":{-index - 1}}}");
            }
            foreach (var handle in held) handle.Set("value", Num(handle["value"]) + 100_000);
            foreach (var index in heldAt) expected[index] = J($"{{\"value\":{index + 100_000}}}");
            var prepared = change.Prepare();
            Check(JsonValue.DeepEquals(At(prepared.Value, "values"), JsonValue.Array(expected)), "candidate");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.normalizes-restored-overrides-and-cancelled-structure", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[1,2,3]}"));
            var change = tracker.BeginChange();
            var values = Root(change).GetArray("values");
            values.Set(1, 9);
            values.Set(1, 2);
            values.Reverse();
            values.Reverse();
            values.Add(4);
            Equal(4d, Num(values.RemoveAt(values.Count - 1)), "pop");
            var prepared = change.Prepare();
            Same("{\"values\":[1,2,3]}", prepared.Value, "candidate");
            OpsAre("[]", prepared);
            Check(ReferenceEquals(prepared.Value, prepared.Base), "identity kept");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.null-sparse-overrides", () =>
        {
            var tracker = Tracker.Track(Values(10_000, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Settle(tracker, draft =>
            {
                draft.AsObject().GetArray("values").Set(17, JsonValue.Null);
                draft.AsObject().GetArray("values").Set(9_000, JsonValue.Null);
            });
            Check(At(tracker.Value, "values", 17) is JsonNullValue && At(tracker.Value, "values", 9_000) is JsonNullValue, "nulls");
        }),
        Case("chord.tracker.self-overlapping-copies-by-value", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[{\"n\":0},{\"n\":1},{\"n\":2},{\"n\":3}]}"));
            Settle(tracker, draft =>
            {
                var values = draft.AsObject().GetArray("values");
                var copied = new[] { values[0], values[1], values[2] }; // copyWithin(1, 0, 3)
                for (var offset = 0; offset < 3; offset++) values.Set(1 + offset, copied[offset]);
                var fill = values[1]; // fill(values[1], 0, 2)
                values.Set(0, fill);
                values.Set(1, fill);
                values.GetObject(0).Set("n", 9);
            });
            Same("[{\"n\":9},{\"n\":0},{\"n\":1},{\"n\":2}]", At(tracker.Value, "values"), "values");
            Check(!ReferenceEquals(At(tracker.Value, "values", 0), At(tracker.Value, "values", 1)), "edited copy independent");
        }),
        Case("chord.tracker.inserts-100000-items-with-unshift", () => InsertsMany(unshift: true)),
        Case("chord.tracker.inserts-100000-items-with-splice", () => InsertsMany(unshift: false)),
    ];

    private static void InsertsMany(bool unshift)
    {
        var tracker = Tracker.Track(J("{\"values\":[-1]}"));
        var change = tracker.BeginChange();
        var items = Enumerable.Range(0, 100_000).Select(value => (JsonValue)value).ToList();
        if (unshift) Root(change).GetArray("values").InsertRange(0, items);
        else Root(change).GetArray("values").Splice(1, 0, items);
        var prepared = change.Prepare();
        Equal(100_001, ((JsonArrayValue)At(prepared.Value, "values")).Count, "length");
        Equal(99_999d, Num(At(prepared.Value, "values", unshift ? 99_999 : 100_000)), "last inserted");
        Replays(prepared);
        tracker.Adopt(prepared);
    }

    // The randomized transactions of tracker.test.ts: a plain C# model mutated in lockstep with the draft.
    private sealed class RandomDocument
    {
        public List<(int Id, int Score)> Values { get; } = [.. Enumerable.Range(0, 4).Select(id => (id, 0))];
        public string Text { get; set; } = "start";
        public int Revision { get; set; }
        public string? Label { get; set; }

        public JsonValue ToValue()
        {
            var meta = JsonObjectValue.CreateBuilder();
            meta["revision"] = Revision;
            if (Label is not null) meta["label"] = Label;
            return JsonValue.Object(
                KeyValuePair.Create("values", (JsonValue)JsonValue.Array(Values.Select(item => (JsonValue)JsonValue.Object(
                    KeyValuePair.Create("id", (JsonValue)item.Id), KeyValuePair.Create("score", (JsonValue)item.Score))))),
                KeyValuePair.Create("text", (JsonValue)Text), KeyValuePair.Create("meta", (JsonValue)meta.ToImmutable()));
        }
    }

    private static Func<double> Mulberry32(uint seed) => () =>
    {
        unchecked
        {
            seed += 0x6D2B79F5;
            var value = (seed ^ (seed >> 15)) * (1 | seed);
            value = (value + (value ^ (value >> 7)) * (61 | value)) ^ value;
            return (value ^ (value >> 14)) / 4_294_967_296d;
        }
    };

    private static JsonValue Item(int value) => JsonValue.Object(KeyValuePair.Create("id", (JsonValue)value), KeyValuePair.Create("score", (JsonValue)(value % 7)));

    private static void MutateModel(RandomDocument document, int choice, int value)
    {
        var values = document.Values;
        switch (choice)
        {
            case 0: document.Text += "-" + value; break;
            case 1: values.Add((value, value % 7)); break;
            case 2: values.Insert(0, (value, value % 7)); break;
            case 3: if (values.Count > 0) values.RemoveAt(0); break;
            case 4: if (values.Count > 0) values.RemoveAt(values.Count - 1); break;
            case 5:
            {
                var index = values.Count == 0 ? 0 : value % (values.Count + 1);
                var remove = Math.Min(values.Count == 0 ? 0 : value % 2, values.Count - index);
                values.RemoveRange(index, remove);
                values.Insert(index, (value, value % 7));
                break;
            }
            case 6: values.Reverse(); break;
            case 7:
            {
                var sorted = values.OrderBy(item => item.Id).ToList();
                values.Clear();
                values.AddRange(sorted);
                break;
            }
            case 8: if (values.Count > 0) values[value % values.Count] = (values[value % values.Count].Id, value); break;
            case 9: document.Revision++; document.Label = "r-" + value; break;
            default: document.Label = null; break;
        }
    }

    private static void MutateDraft(DraftObject document, int choice, int value)
    {
        var values = document.GetArray("values");
        switch (choice)
        {
            case 0: document.AppendString("text", "-" + value); break;
            case 1: values.Add(Item(value)); break;
            case 2: values.Insert(0, Item(value)); break;
            case 3: if (values.Count > 0) values.RemoveAt(0); break;
            case 4: if (values.Count > 0) values.RemoveAt(values.Count - 1); break;
            case 5:
            {
                var index = values.Count == 0 ? 0 : value % (values.Count + 1);
                values.Splice(index, values.Count == 0 ? 0 : value % 2, Item(value));
                break;
            }
            case 6: values.Reverse(); break;
            case 7: values.Sort((left, right) => Num(At(left, "id")).CompareTo(Num(At(right, "id")))); break;
            case 8: if (values.Count > 0) values.GetObject(value % values.Count).Set("score", value); break;
            case 9:
            {
                var meta = document.GetObject("meta");
                meta.Set("revision", Num(meta["revision"]) + 1);
                meta.Set("label", "r-" + value);
                break;
            }
            default: document.GetObject("meta").Delete("label"); break;
        }
    }

    private static IEnumerable<(string, Func<Task>)> ChordTrackerRandomizedCases() =>
    [
        Case("chord.tracker.randomized-transactions-match-model-replay-and-adoption", () =>
        {
            for (var seed = 1; seed <= 40; seed++)
            {
                var rng = Mulberry32((uint)seed);
                var expected = new RandomDocument();
                var tracker = Tracker.Track(expected.ToValue());
                for (var transaction = 0; transaction < 25; transaction++)
                {
                    var baseRoot = tracker.Value;
                    var baseJson = baseRoot.ToJson();
                    var change = tracker.BeginChange();
                    for (var operation = 0; operation < 5; operation++)
                    {
                        var choice = (int)Math.Floor(rng() * 11);
                        var value = seed * 10_000 + transaction * 10 + operation;
                        MutateModel(expected, choice, value);
                        MutateDraft(Root(change), choice, value);
                    }
                    var prepared = change.Prepare();
                    var where = $"seed {seed} transaction {transaction}";
                    Equal(baseJson, baseRoot.ToJson(), "prepare changed base " + where);
                    Check(JsonValue.DeepEquals(prepared.Value, expected.ToValue()), "policy " + where);
                    Replays(prepared, "replay " + where);
                    tracker.Adopt(prepared);
                    Check(ReferenceEquals(tracker.Value, prepared.Value), "adopt " + where);
                }
            }
        }),
        Case("chord.tracker.orders-many-reverse-depth-object-edits", () =>
        {
            JsonValue initial = J("{\"value\":0}");
            for (var depth = 0; depth < 512; depth++) initial = JsonValue.Object(KeyValuePair.Create("value", (JsonValue)0), KeyValuePair.Create("next", initial));
            var tracker = Tracker.Track(initial);
            var change = tracker.BeginChange();
            var nodes = new List<DraftObject>();
            for (var node = Root(change); ; node = node.GetObject("next"))
            {
                nodes.Add(node);
                if (!node.ContainsKey("next")) break;
            }
            for (var index = nodes.Count - 1; index >= 0; index--) nodes[index].Set("value", index + 1);
            var prepared = change.Prepare();
            Equal(nodes.Count, prepared.Ops.Length, "one op per node");
            Check(prepared.Ops.Select(op => ((SetOp)op).Path.Length).SequenceEqual(Enumerable.Range(1, nodes.Count)), "shallow first");
            Replays(prepared);
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.reserved-own-keys-fold-into-safe-ancestors", () =>
        {
            var tracker = Tracker.Track(J("{\"safe\":{\"__proto__\":{\"value\":1}}}"));
            var change = tracker.BeginChange();
            Root(change).GetObject("safe").GetObject("__proto__").Set("value", 2);
            var prepared = change.Prepare();
            Equal(2d, Num(At(prepared.Value, "safe", "__proto__", "value")), "candidate");
            OpsAre("[[\"s\",[\"safe\"],{\"__proto__\":{\"value\":2}}]]", prepared);
            Replays(prepared);
            tracker.Adopt(prepared);
            var root = tracker.BeginChange();
            Root(root).Set("__proto__", 1);
            var rootPrepared = root.Prepare();
            Check(rootPrepared.Ops is [ReplaceOp], "a reserved key at the root folds into r");
            Replays(rootPrepared);
        }),
        Case("chord.tracker.prepared-values-survive-a-storage-style-copy", () =>
        {
            var tracker = Tracker.Track(J("{\"rows\":[{\"value\":1}]}"));
            var change = tracker.BeginChange();
            Root(change).GetArray("rows").GetObject(0).Set("value", 2);
            Root(change).GetArray("rows").Add(J("{\"value\":3}"));
            var prepared = change.Prepare();
            Same("{\"rows\":[{\"value\":2},{\"value\":3}]}", J(prepared.Value.ToJson()), "copy");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.revision-validator-skips-validated-containers", () =>
        {
            var validator = new JsonRevisionValidator();
            var tracker = Tracker.Track(J("{\"stable\":{\"rows\":[{\"value\":1}]},\"changing\":{\"value\":0}}"));
            Check(ReferenceEquals(validator.Validate(tracker.Value), tracker.Value), "returns the revision");
            Check(validator.HasValidated(At(tracker.Value, "stable", "rows", 0)), "descendants validated");
            var change = tracker.BeginChange();
            Root(change).GetObject("changing").Set("value", 1);
            var prepared = change.Prepare();
            Check(!validator.HasValidated(prepared.Value) && validator.HasValidated(At(prepared.Value, "stable")), "only new containers are unvalidated");
            validator.Validate(prepared.Value);
            Check(validator.HasValidated(prepared.Value) && validator.HasValidated(At(prepared.Value, "changing")), "new containers validated");
            Throws<ArgumentNullException>(() => validator.Validate<JsonValue>(null!), "null");
        }),
    ];

    // delta.test.ts ("immutable tracker lifecycle", "canonical strings"), delta-apply-immutable.test.ts (tracker batches) and
    // delta-clone.test.ts ("tracker ownership").
    private static IEnumerable<(string, Func<Task>)> ChordTrackerDeltaCases() =>
    [
        Case("chord.tracker.delta.keeps-a-draft-across-await-and-adopts-only-prepared", async () =>
        {
            var input = J("{\"count\":1,\"nested\":{\"text\":\"a\"},\"values\":[1]}");
            var tracker = Tracker.Track(input);
            Check(ReferenceEquals(tracker.Value, input), "owned in O(1)");
            var change = tracker.BeginChange();
            Root(change).Set("count", 2);
            await Task.Yield();
            Root(change).GetObject("nested").AppendString("text", "b");
            Root(change).GetArray("values").Add(2);
            var prepared = change.Prepare();
            Check(ReferenceEquals(prepared.Base, tracker.Value), "base");
            Same("{\"count\":2,\"nested\":{\"text\":\"ab\"},\"values\":[1,2]}", prepared.Value, "candidate");
            OpsAre("[[\"s\",[\"count\"],2],[\"a\",[\"nested\",\"text\"],\"b\"],[\"p\",[\"values\"],1,0,[2]]]", prepared);
            ThrowsMatching(() => _ = change.State, "settled", "state after prepare");
            Same("{\"count\":1,\"nested\":{\"text\":\"a\"},\"values\":[1]}", tracker.Value, "authority unchanged");
            tracker.Adopt(prepared);
            Check(ReferenceEquals(tracker.Value, prepared.Value), "adopted");
        }),
        Case("chord.tracker.delta.aborts-and-revokes-without-changing-the-value", () =>
        {
            var tracker = Tracker.Track(J("{\"child\":{\"value\":1}}"));
            var change = tracker.BeginChange();
            var child = Root(change).GetObject("child");
            child.Set("value", 2);
            change.Abort();
            Equal(1d, Num(At(tracker.Value, "child", "value")), "committed");
            ThrowsMatching(() => child.Get("value"), "settled", "held");
            change.Abort();
            ThrowsMatching(() => change.Prepare(), "settled", "prepare after abort");
            tracker.BeginChange().Abort();
        }),
        Case("chord.tracker.delta.grows-arrays-with-explicit-nulls-and-revokes", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[1,2]}"));
            var change = tracker.BeginChange();
            Root(change).GetArray("values").SetLength(4);
            var state = Root(change);
            var prepared = change.Prepare();
            Same("[1,2,null,null]", At(prepared.Value, "values"), "grown");
            OpsAre("[[\"p\",[\"values\"],2,0,[null,null]]]", prepared);
            ThrowsMatching(() => state.GetArray("values"), "settled", "revoked");
            tracker.Adopt(prepared);
        }),
        Case("chord.tracker.delta.normalizes-a-deep-no-op-to-previous-identity", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":{\"nested\":[1,2]}}"));
            var change = tracker.BeginChange();
            Root(change).Set("value", J("{\"nested\":[1,2]}"));
            var prepared = change.Prepare();
            Check(ReferenceEquals(prepared.Value, prepared.Base), "identity");
            OpsAre("[]", prepared);
            tracker.Adopt(prepared);
            Check(ReferenceEquals(tracker.Value, prepared.Base), "adopted identity");
            Equal(1L, tracker.Revision, "a no-op still advances the revision");
        }),
        Case("chord.tracker.delta.replacement-ownership-and-no-op-normalization", () =>
        {
            var tracker = Tracker.Track(J("{\"nested\":{\"value\":1}}"));
            var replacement = J("{\"nested\":{\"value\":2}}");
            var prepared = tracker.PrepareReplace(replacement);
            Check(ReferenceEquals(prepared.Value, replacement), "value");
            OpsAre("[[\"r\",{\"nested\":{\"value\":2}}]]", prepared);
            tracker.Adopt(prepared);
            var noOp = tracker.PrepareReplace(J("{\"nested\":{\"value\":2}}"));
            Check(ReferenceEquals(noOp.Value, tracker.Value), "no-op keeps the current root");
            OpsAre("[]", noOp);
        }),
        Case("chord.tracker.delta.shares-operation-placements", () =>
        {
            var tracker = Tracker.Track(J("{\"rows\":[]}"));
            var change = tracker.BeginChange();
            Root(change).GetArray("rows").Add(J("{\"id\":1}"));
            var prepared = change.Prepare();
            Check(prepared.Ops is [SpliceOp { Items: [var placed] }] && ReferenceEquals(placed, At(prepared.Value, "rows", 0)), "shared payload");
        }),
        Case("chord.tracker.delta.rejects-foreign-stale-and-repeated-preparations", () =>
        {
            var first = Tracker.Track(J("{\"value\":0}"));
            var second = Tracker.Track(J("{\"value\":0}"));
            var change = first.BeginChange();
            var competing = first.BeginChange();
            Root(change).Set("value", 1);
            Root(competing).Set("value", 2);
            var prepared = change.Prepare();
            var competingPrepared = competing.Prepare();
            ThrowsMatching(() => second.Adopt(prepared), "different tracker", "foreign");
            first.Adopt(prepared);
            ThrowsMatching(() => first.Adopt(prepared), "already been used", "repeat");
            ThrowsMatching(() => first.Adopt(competingPrepared), "stale", "competing");
            var stale = first.PrepareReplace(J("{\"value\":2}"));
            var winner = first.PrepareReplace(J("{\"value\":3}"));
            first.Adopt(winner);
            ThrowsMatching(() => first.Adopt(stale), "stale", "stale replacement");
            ThrowsMatching(() => first.Adopt(stale), "stale", "stale replacement again");
        }),
        Case("chord.tracker.delta.abort-invalidates-a-prepared-result", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":0}"));
            var change = tracker.BeginChange();
            Root(change).Set("value", 1);
            var prepared = change.Prepare();
            change.Abort();
            ThrowsMatching(() => tracker.Adopt(prepared), "aborted", "aborted");
            change.Abort();
        }),
        Case("chord.tracker.delta.adopting-a-no-op-stales-competitors", () =>
        {
            var tracker = Tracker.Track(J("{\"value\":{\"count\":1}}"));
            var first = tracker.PrepareReplace(J("{\"value\":{\"count\":1}}"));
            var competing = tracker.PrepareReplace(J("{\"value\":{\"count\":1}}"));
            Check(ReferenceEquals(first.Value, first.Base) && ReferenceEquals(competing.Value, competing.Base), "no-op identity");
            tracker.Adopt(first);
            ThrowsMatching(() => tracker.Adopt(first), "already been used", "repeat");
            ThrowsMatching(() => tracker.Adopt(competing), "stale", "competing");
        }),
        Case("chord.tracker.delta.root-replacement-does-not-traverse-large-input", () =>
        {
            var rows = Enumerable.Range(0, 10_000).Select(value => J($"{{\"value\":{value},\"stable\":{{\"value\":{value}}}}}")).ToList();
            var tracker = Tracker.Track(JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(rows))));
            var replacementRows = ((JsonArrayValue)At(tracker.Value, "rows")).ToList();
            replacementRows[5_000] = JsonValue.Object(KeyValuePair.Create("value", (JsonValue)(-1)), KeyValuePair.Create("stable", At(replacementRows[5_000], "stable")));
            var replacement = JsonValue.Object(KeyValuePair.Create("rows", (JsonValue)JsonValue.Array(replacementRows)));
            var prepared = tracker.PrepareReplace(replacement);
            Check(ReferenceEquals(prepared.Value, replacement), "value");
            Check(prepared.Ops is [ReplaceOp { Value: var payload }] && ReferenceEquals(payload, replacement), "r");
            Check(JsonValue.DeepEquals(DeltaApply.Apply(prepared.Base, prepared.Ops), prepared.Value), "replay");
        }),
        Case("chord.tracker.delta.emits-append-and-rolling-window-operations", () =>
        {
            var tracker = Tracker.Track(J("{\"text\":\"abcdefgh\"}"));
            var change = tracker.BeginChange();
            Root(change).AppendString("text", "ij");
            var prepared = change.Prepare();
            OpsAre("[[\"a\",[\"text\"],\"ij\"]]", prepared);
            tracker.Adopt(prepared);
            change = tracker.BeginChange();
            Root(change).Set("text", Str(Root(change)["text"])[3..] + "xyz");
            prepared = change.Prepare();
            OpsAre("[[\"t\",[\"text\"],3],[\"a\",[\"text\"],\"xyz\"]]", prepared);
        }),
        Case("chord.tracker.delta.tracker-batches-across-revision-boundaries", () =>
        {
            var initial = JsonValue.Object(KeyValuePair.Create("text", (JsonValue)"start"),
                KeyValuePair.Create("values", (JsonValue)JsonValue.Array(Enumerable.Range(0, 8).Select(id => J($"{{\"id\":{id},\"score\":0}}")))),
                KeyValuePair.Create("revision", (JsonValue)0));
            var tracker = Tracker.Track(initial);
            var batches = new List<ImmutableArray<Op>>();
            for (var revision = 1; revision <= 40; revision++)
            {
                var change = tracker.BeginChange();
                var state = Root(change);
                state.Set("revision", revision);
                state.Set("text", Str(state["text"])[1..] + revision);
                var values = state.GetArray("values");
                switch (revision % 5)
                {
                    case 0: values.Reverse(); break;
                    case 1: values.Add(J($"{{\"id\":{100 + revision},\"score\":{revision}}}")); break;
                    case 2: values.RemoveAt(0); break;
                    case 3: values.GetObject(revision % values.Count).Set("score", revision); break;
                    default: values.Splice(1, 1, J($"{{\"id\":{200 + revision},\"score\":{revision}}}")); break;
                }
                var prepared = change.Prepare();
                batches.Add(prepared.Ops);
                tracker.Adopt(prepared);
            }
            Check(JsonValue.DeepEquals(DeltaApply.ApplyBatches(initial, batches.Select(batch => (IEnumerable<Op>)batch)), tracker.Value), "batched replay");
        }),
        Case("chord.tracker.clone.takes-ownership-in-o1", () =>
        {
            var input = J("{\"point\":{\"x\":3,\"y\":7,\"pressure\":0.1},\"rows\":[{\"values\":[0,false,null,\"text\",{\"n\":1}]}]}");
            var tracker = Tracker.Track(input);
            Check(ReferenceEquals(tracker.Value, input) && ReferenceEquals(At(tracker.Value, "point"), At(input, "point")) &&
                ReferenceEquals(At(tracker.Value, "rows", 0, "values", 4), At(input, "rows", 0, "values", 4)), "references kept");
            Throws<ArgumentException>(() => Tracker.Track(JsonValue.Number(1)), "scalar root");
        }),
        Case("chord.tracker.clone.placed-values-are-immutable-copies", () =>
        {
            var tracker = Tracker.Track(J("{\"rows\":[]}"));
            var assigned = J("{\"nested\":{\"value\":1}}");
            var change = tracker.BeginChange();
            Root(change).GetArray("rows").Add(assigned);
            Root(change).GetArray("rows").GetObject(0).GetObject("nested").Set("value", 9);
            Same("{\"nested\":{\"value\":1}}", assigned, "the placed value is never modified");
            var prepared = change.Prepare();
            Equal(9d, Num(At(prepared.Value, "rows", 0, "nested", "value")), "draft edit");
            Replays(prepared);
        }),
    ];
}
