using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/test/delta-diff.test.ts. JavaScript object identity is reference
// identity of immutable values: a value reused in both revisions is the same reference, a reconstructed one (parsed again) is not.
// Object.freeze is unnecessary (values are immutable); the codec case reuses the existing encoder/decoder.
internal static partial class Program
{
    private static JsonValue Obj(params (string Key, JsonValue Value)[] properties) =>
        JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, property.Value)));
    private static JsonValue WithValues(params IEnumerable<JsonValue> items) => Obj(("values", JsonValue.Array(items)));

    private static void ExpectDiff(JsonValue before, JsonValue after, string expected)
    {
        var operations = DeltaDiff.DiffRevisions(before, after);
        Check(JsonValue.DeepEquals(J(expected), OpsJson(operations)), $"diff: expected {expected}, actual {OpsJson(operations).ToJson()}");
        Check(JsonValue.DeepEquals(DeltaApply.Apply(before, operations), after), "diff replays to after");
    }

    private static void ExpectDiff(JsonValue before, JsonValue after, JsonArrayValue expected)
    {
        var operations = DeltaDiff.DiffRevisions(before, after);
        Check(JsonValue.DeepEquals(expected, OpsJson(operations)), $"diff: expected {expected.ToJson()}, actual {OpsJson(operations).ToJson()}");
        Check(JsonValue.DeepEquals(DeltaApply.Apply(before, operations), after), "diff replays to after");
    }

    private static JsonArrayValue Splice(string path, int index, int remove, params IEnumerable<JsonValue> items) =>
        new SpliceOp(DeltaPath.Of(path), index, remove, [.. items]).ToJson();

    private static IEnumerable<(string, Func<Task>)> ChordDiffCases() =>
    [
        Case("chord.diff.emits-sets-and-deletes", () =>
            ExpectDiff(J("{\"keep\":1,\"change\":1,\"remove\":true}"), J("{\"keep\":1,\"change\":2,\"add\":3}"),
                "[[\"s\",[\"change\"],2],[\"s\",[\"add\"],3],[\"d\",[\"remove\"]]]")),
        Case("chord.diff.emits-string-append-and-front-truncation", () =>
        {
            ExpectDiff(J("{\"text\":\"hello\"}"), J("{\"text\":\"hello world\"}"), "[[\"a\",[\"text\"],\" world\"]]");
            ExpectDiff(J("{\"text\":\"hello world\"}"), J("{\"text\":\"world\"}"), "[[\"t\",[\"text\"],6]]");
            ExpectDiff(J("{\"text\":\"abcdefgh\"}"), J("{\"text\":\"defghxyz\"}"), "[[\"t\",[\"text\"],3],[\"a\",[\"text\"],\"xyz\"]]");
        }),
        Case("chord.diff.represents-insertion-removal-and-shift-with-splices", () =>
        {
            var (a, b, c) = (J("{\"id\":\"a\"}"), J("{\"id\":\"b\"}"), J("{\"id\":\"c\"}"));
            ExpectDiff(WithValues(a, b), WithValues(a, c, b), "[[\"p\",[\"values\"],1,0,[{\"id\":\"c\"}]]]");
            ExpectDiff(WithValues(a, b, c), WithValues(b, c), "[[\"p\",[\"values\"],0,1,[]]]");
        }),
        Case("chord.diff.collapses-a-same-length-queue-update-to-two-splices", () =>
        {
            var (a, b, c, d) = (J("{\"id\":\"a\"}"), J("{\"id\":\"b\"}"), J("{\"id\":\"c\"}"), J("{\"id\":\"d\"}"));
            ExpectDiff(WithValues(a, b, c), WithValues(b, c, d), "[[\"p\",[\"values\"],0,1,[]],[\"p\",[\"values\"],2,0,[{\"id\":\"d\"}]]]");
        }),
        Case("chord.diff.emits-a-permutation-for-a-pure-reorder", () =>
        {
            var (a, b, c) = (J("{\"id\":\"a\"}"), J("{\"id\":\"b\"}"), J("{\"id\":\"c\"}"));
            ExpectDiff(WithValues(a, b, c), WithValues(c, a, b), "[[\"m\",[\"values\"],[2,0,1]]]");
        }),
        Case("chord.diff.normalizes-reordered-deeply-equal-objects-to-a-no-op", () =>
        {
            var first = J("{\"nested\":{\"value\":1}}");
            var second = J("{\"nested\":{\"value\":1}}");
            Check(!ReferenceEquals(first, second), "distinct");
            Equal(0, DeltaDiff.DiffRevisions(WithValues(first, second), WithValues(second, first)).Length, "no-op");
        }),
        Case("chord.diff.validates-and-encodes-permutations", () =>
        {
            ImmutableArray<Op> operations = [new MoveOp(DeltaPath.Of("values"), [2, 0, 1]), new MoveOp(DeltaPath.Of("values"), [1, 2, 0])];
            foreach (var operation in operations) DeltaValidation.AssertValidOp(operation.ToJson());
            var wire = new DeltaEncoder().Encode(operations);
            Same("[[\"m\",[\"values\"],[2,0,1]],[\"m\",[1,2,0]]]", JsonValue.Array(wire), "wire");
            foreach (var operation in wire) DeltaValidation.AssertValidWireOp(operation);
            Check(new DeltaDecoder().Decode(wire).SequenceEqual(operations, OpComparer), "round trip");
            Check(Throws<ArgumentException>(() => DeltaValidation.AssertValidOp(J("[\"m\",[\"values\"],[0,0]]")), "bijection").Message.Contains("bijection", StringComparison.Ordinal), "message");
        }),
        Case("chord.diff.emits-nothing-for-deeply-equal-reconstructed-values", () =>
        {
            Equal(0, DeltaDiff.DiffRevisions(J("{\"value\":{\"nested\":[1,2]}}"), J("{\"value\":{\"nested\":[1,2]}}")).Length, "nested");
            Equal(0, DeltaDiff.DiffRevisions(J("{\"values\":[{\"id\":1},{\"id\":2}]}"), J("{\"values\":[{\"id\":1},{\"id\":2}]}")).Length, "rows");
            Equal(0, DeltaDiff.DiffRevisions(J("{\"values\":[true,true,true]}"), J("{\"values\":[true,true,true]}")).Length, "booleans");
        }),
        Case("chord.diff.keeps-a-leaf-edit-inside-a-reconstructed-array-narrow", () =>
            ExpectDiff(J("{\"values\":[{\"id\":1,\"label\":\"one\"},{\"id\":2,\"label\":\"two\"}]}"),
                J("{\"values\":[{\"id\":1,\"label\":\"one\"},{\"id\":2,\"label\":\"changed\"}]}"), "[[\"s\",[\"values\",1,\"label\"],\"changed\"]]")),
        Case("chord.diff.emits-payload-free-splices-for-scattered-removals", () =>
        {
            var rows = "abcde".Select(id => J($"{{\"id\":\"{id}\"}}")).ToArray();
            ExpectDiff(WithValues(rows), WithValues(rows[0], rows[2], rows[4]), "[[\"p\",[\"values\"],1,1,[]],[\"p\",[\"values\"],2,1,[]]]");
        }),
        Case("chord.diff.encodes-front-removal-canonically", () => ExpectDiff(J("{\"values\":[1,2,3,4]}"), J("{\"values\":[2,3,4]}"), "[[\"p\",[\"values\"],0,1,[]]]")),
        Case("chord.diff.encodes-tail-removal-canonically", () => ExpectDiff(J("{\"values\":[1,2,3,4]}"), J("{\"values\":[1,2,3]}"), "[[\"p\",[\"values\"],3,1,[]]]")),
        Case("chord.diff.encodes-middle-removal-canonically", () => ExpectDiff(J("{\"values\":[1,2,3,4]}"), J("{\"values\":[1,3,4]}"), "[[\"p\",[\"values\"],1,1,[]]]")),
        Case("chord.diff.encodes-all-removal-canonically", () => ExpectDiff(J("{\"values\":[1,2,3,4]}"), J("{\"values\":[]}"), "[[\"p\",[\"values\"],0,4,[]]]")),
        Case("chord.diff.encodes-none-removal-canonically", () => ExpectDiff(J("{\"values\":[1,2,3,4]}"), J("{\"values\":[1,2,3,4]}"), "[]")),
        Case("chord.diff.does-not-field-diff-unrelated-shifted-objects", () =>
            ExpectDiff(J("{\"values\":[{\"type\":\"row\",\"value\":1},{\"type\":\"row\",\"value\":2},{\"type\":\"row\",\"value\":3}]}"),
                J("{\"values\":[{\"type\":\"row\",\"value\":2},{\"type\":\"row\",\"value\":3},{\"type\":\"row\",\"value\":4}]}"),
                "[[\"p\",[\"values\"],0,1,[]],[\"p\",[\"values\"],2,0,[{\"type\":\"row\",\"value\":4}]]]")),
        Case("chord.diff.ignores-coincidental-id-and-key-fields", () =>
        {
            var left = J("{\"value\":\"left\"}");
            var right = J("{\"value\":\"right\"}");
            JsonValue[] before = [left, J("{\"id\":1,\"key\":\"a\",\"value\":\"first\"}"), J("{\"id\":2,\"key\":\"b\",\"value\":\"second\"}"), right];
            JsonValue[] replacements = [J("{\"id\":2,\"key\":\"b\",\"value\":\"edited-second\"}"), J("{\"id\":1,\"key\":\"a\",\"value\":\"edited-first\"}")];
            ExpectDiff(WithValues(before), WithValues([left, .. replacements, right]), JsonValue.Array((JsonValue)Splice("values", 1, 2, replacements)));
        }),
        Case("chord.diff.combines-removals-append-and-a-shared-subtree-survivor-edit", () =>
        {
            JsonValue Row(string id) => J($"{{\"id\":\"{id}\",\"stable\":{{}},\"detail\":{{\"text\":\"{id}\"}}}}");
            var (a, b, c, d) = (Row("a"), Row("b"), Row("c"), Row("d"));
            var changedC = Obj(("id", "c"), ("stable", At(c, "stable")), ("detail", J("{\"text\":\"changed\"}")));
            var appended = Row("e");
            ExpectDiff(WithValues(a, b, c, d), WithValues(a, changedC, d, appended), JsonValue.Array(
                J("[\"p\",[\"values\"],1,1,[]]"), J("[\"a\",[\"values\",1,\"detail\",\"text\"],\"hanged\"]"), Splice("values", 3, 0, appended)));
        }),
        Case("chord.diff.does-not-retain-a-256KiB-removed-neighbor-payload", () => RemovedNeighbors(256 * 1024)),
        Case("chord.diff.does-not-retain-a-1MiB-removed-neighbor-payload", () => RemovedNeighbors(1024 * 1024)),
        Case("chord.diff.keeps-push-pop-and-middle-removal-narrow-at-1001-items", () => PushPopMiddle(1_001)),
        Case("chord.diff.keeps-push-pop-and-middle-removal-narrow-at-10000-items", () => PushPopMiddle(10_000)),
        Case("chord.diff.keeps-forty-thousand-row-sparse-edits-narrow", () =>
        {
            var values = Enumerable.Range(0, 40_000).Select(value => J($"{{\"value\":{value},\"stable\":{{\"value\":{value}}}}}")).ToArray();
            var after = values.ToArray();
            var changed = new List<int>();
            for (var index = 100; index < after.Length; index += 400)
            {
                after[index] = Obj(("value", -index), ("stable", At(values[index], "stable")));
                changed.Add(index);
            }
            var operations = DeltaDiff.DiffRevisions(WithValues(values), WithValues(after));
            Check(operations.SequenceEqual(changed.Select(index => (Op)new SetOp(DeltaPath.Of("values", index, "value"), -index)), OpComparer), "narrow sets");
            Check(OpsJson(operations).ToJson().Length < 7_500, "small");
            Check(JsonValue.DeepEquals(DeltaApply.Apply(WithValues(values), operations), WithValues(after)), "replay");
        }),
        Case("chord.diff.keeps-a-reconstructed-large-array-leaf-edit-narrow", () =>
        {
            var before = Enumerable.Range(0, 1_000).Select(value => J($"{{\"value\":{value},\"label\":\"row-{value}\"}}")).ToArray();
            var after = before.Select(row => J(row.ToJson())).ToArray();
            after[700] = J("{\"value\":700,\"label\":\"changed\"}");
            var operations = DeltaDiff.DiffRevisions(WithValues(before), WithValues(after));
            Same("[[\"s\",[\"values\",700,\"label\"],\"changed\"]]", OpsJson(operations), "narrow");
            Check(JsonValue.DeepEquals(DeltaApply.Apply(WithValues(before), operations), WithValues(after)), "replay");
        }),
        Case("chord.diff.splices-an-ambiguous-moved-and-edited-gap", () =>
        {
            var left = J("{\"value\":\"left\"}");
            var right = J("{\"value\":\"right\"}");
            JsonValue[] replacements = [J("{\"id\":2,\"value\":\"edited\"}"), J("{\"id\":1,\"value\":\"also-edited\"}")];
            ExpectDiff(WithValues(left, J("{\"id\":1,\"value\":\"a\"}"), J("{\"id\":2,\"value\":\"b\"}"), right), WithValues([left, .. replacements, right]),
                JsonValue.Array((JsonValue)Splice("values", 1, 2, replacements)));
        }),
        Case("chord.diff.keeps-a-large-rotation-payload-free", () =>
        {
            var values = Enumerable.Range(0, 10_000).Select(value => J($"{{\"value\":{value}}}")).ToArray();
            var rotated = values[1_000..].Concat(values[..1_000]).ToArray();
            var operations = DeltaDiff.DiffRevisions(WithValues(values), WithValues(rotated));
            Check(operations is [MoveOp], "one permutation");
            Check(OpsJson(operations).ToJson().Length < 60_000, "payload free");
            Check(JsonValue.DeepEquals(DeltaApply.Apply(WithValues(values), operations), WithValues(rotated)), "replay");
        }),
        Case("chord.diff.encodes-five-hundred-unshifts-without-snapshotting", () =>
        {
            var retained = Enumerable.Range(0, 10_000).Select(value => J($"{{\"value\":{value},\"payload\":\"{new string('x', 100)}\"}}")).ToArray();
            var inserted = Enumerable.Range(0, 500).Select(value => J($"{{\"value\":{-value - 1}}}")).ToArray();
            var operations = DeltaDiff.DiffRevisions(WithValues(retained), WithValues([.. inserted, .. retained]));
            Same(JsonValue.Array((JsonValue)Splice("values", 0, 0, inserted)).ToJson(), OpsJson(operations), "one splice");
            Check(OpsJson(operations).ToJson().Length < 20_000, "small");
            Check(JsonValue.DeepEquals(DeltaApply.Apply(WithValues(retained), operations), WithValues([.. inserted, .. retained])), "replay");
        }),
        Case("chord.diff.bounds-wide-object-emission-with-a-root-replacement", () =>
        {
            var before = JsonValue.Object(Enumerable.Range(0, 20_000).Select(index => KeyValuePair.Create("field" + index, (JsonValue)0)));
            var after = JsonValue.Object(Enumerable.Range(0, 20_000).Select(index => KeyValuePair.Create("field" + index, (JsonValue)1)));
            var operations = DeltaDiff.DiffRevisions(before, after);
            Check(operations is [ReplaceOp { Value: var value }] && ReferenceEquals(value, after), "r");
        }),
        Case("chord.diff.bounds-a-wide-normalized-array-fallback-with-a-root-replacement", () =>
        {
            var before = WithValues(Enumerable.Repeat<JsonValue>(0, 40_000).Select(_ => (JsonValue)0));
            var after = WithValues(Enumerable.Range(0, 40_000).Select(_ => (JsonValue)1));
            var operations = DeltaDiff.DiffRevisions(before, after);
            Check(operations is [ReplaceOp { Value: var value }] && ReferenceEquals(value, after), "r");
            Check(JsonValue.DeepEquals(DeltaApply.Apply(before, operations), after), "replay");
        }),
    ];

    private static void RemovedNeighbors(int size)
    {
        var payload = new string('x', size);
        var retained = "abcde".Select(id => Obj(("id", id.ToString()), ("payload", payload))).ToArray();
        var before = WithValues(retained);
        var after = WithValues(retained[0], retained[2], retained[4]);
        var operations = DeltaDiff.DiffRevisions(before, after);
        Same("[[\"p\",[\"values\"],1,1,[]],[\"p\",[\"values\"],2,1,[]]]", OpsJson(operations), "payload-free");
        Check(OpsJson(operations).ToJson().Length < 100, "small");
        Check(JsonValue.DeepEquals(DeltaApply.Apply(before, operations), after), "replay");
    }

    private static void PushPopMiddle(int size)
    {
        var values = Enumerable.Range(0, size).Select(value => J($"{{\"value\":{value}}}")).ToArray();
        var appended = J($"{{\"value\":{size}}}");
        ExpectDiff(WithValues(values), WithValues([.. values, appended]), JsonValue.Array((JsonValue)Splice("values", size, 0, appended)));
        ExpectDiff(WithValues(values), WithValues(values[..^1]), JsonValue.Array((JsonValue)Splice("values", size - 1, 1)));
        var middle = size / 2;
        ExpectDiff(WithValues(values), WithValues([.. values[..middle], .. values[(middle + 1)..]]), JsonValue.Array((JsonValue)Splice("values", middle, 1)));
    }
}
