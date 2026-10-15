using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/test/json.test.ts, delta.test.ts and delta-apply-immutable.test.ts
// (the cases that do not need the tracker; tracker cases join with the draft API). JavaScript-only constructions (accessors, prototypes,
// sparse arrays, typed arrays) cannot occur in immutable C# values; the cases keep the behaviour they guard.
internal static partial class Program
{
    private static JsonValue J(string json) => JsonValue.Parse(json);
    private static ImmutableArray<Op> Ops(string json) => [.. ((JsonArrayValue)J(json)).Select(Op.FromJson)];
    private static void Same(string expected, JsonValue actual, string what) =>
        Check(JsonValue.DeepEquals(J(expected), actual), $"{what}: expected {expected}, actual {actual.ToJson()}");
    private static JsonValue At(JsonValue value, params object[] path)
    {
        foreach (var segment in path) value = segment is string key ? ((JsonObjectValue)value)[key] : ((JsonArrayValue)value)[(int)segment];
        return value;
    }

    private static IEnumerable<(string, Func<Task>)> ChordJsonCases() =>
    [
        Case("chord.json.strict-json-only", () =>
        {
            Throws<ArgumentException>(() => JsonValue.Number(double.PositiveInfinity), "non-finite number");
            Throws<ArgumentException>(() => JsonValue.Number(double.NaN), "NaN");
            Throws<FormatException>(() => JsonValue.Parse("{\"a\":1,}"), "trailing comma");
            Equal("Expected double-quoted property name in JSON at position 7 (line 1 column 8)", Throws<FormatException>(() => JsonValue.Parse("{\"a\":1,}"), "message").Message, "V8 message");
        }),
        Case("chord.json.javascript-stringify-and-key-order", () =>
        {
            var value = J("{\"b\":1,\"2\":true,\"a\":[null,\"x\"],\"1\":-0,\"10\":0.5}");
            Equal("{\"1\":0,\"2\":true,\"10\":0.5,\"b\":1,\"a\":[null,\"x\"]}", value.ToJson(), "integer keys first, then insertion order; -0 prints 0");
            Equal("[\"\\ud800\",\"\u00e9\",\"\\u0001\"]", J("[\"\\ud800\",\"\u00e9\",\"\\u0001\"]").ToJson(), "lone surrogate kept and escaped");
            Equal("{\"a\":2,\"b\":3}", J("{\"a\":1,\"b\":3,\"a\":2}").ToJson(), "a repeated key keeps its first position");
            Equal("1e+21", J("1e21").ToJson(), "JavaScript number text");
        }),
        Case("chord.json.structural-equality-and-sharing", () =>
        {
            var shared = J("{\"value\":1}");
            var input = JsonValue.Object(KeyValuePair.Create("left", shared), KeyValuePair.Create("right", shared));
            Check(JsonValue.DeepEquals(input, J("{\"right\":{\"value\":1},\"left\":{\"value\":1}}")), "deep equality ignores key order");
            Check(ReferenceEquals(((JsonObjectValue)input)["left"], ((JsonObjectValue)input)["right"]), "immutable values may be shared");
            var data = J("{\"x\":[1,\"\\udc00\"]}").ToData();
            Equal("{\"x\":[1,\"\\udc00\"]}", JsonValue.FromData(data).ToJson(), "JsonData round trip keeps lone surrogates");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> ChordDeltaCases() =>
    [
        Case("chord.delta.finds-bounded-overlaps", () =>
        {
            Equal(5, DeltaValidation.Overlap("abcdefgh", "defghxyz", 65_536), "overlap");
            Equal(0, DeltaValidation.Overlap("abcdef", "defghi", 0), "zero scan");
        }),
        Case("chord.delta.applies-immutable-operations", () =>
        {
            var operations = Ops("[[\"a\",[\"text\"],\"b\"],[\"p\",[\"values\"],1,1,[3,4]],[\"s\",[\"nested\",\"value\"],2]]");
            var input = J("{\"text\":\"a\",\"values\":[1,2],\"nested\":{\"value\":1},\"stable\":{\"value\":9}}");
            var result = DeltaApply.Apply(input, operations);
            Same("{\"text\":\"ab\",\"values\":[1,3,4],\"nested\":{\"value\":2},\"stable\":{\"value\":9}}", result, "result");
            Same("{\"text\":\"a\",\"values\":[1,2],\"nested\":{\"value\":1},\"stable\":{\"value\":9}}", input, "input unchanged");
            Check(ReferenceEquals(At(result, "stable"), At(input, "stable")), "untouched subtree shared");
        }),
        Case("chord.delta.root-replacement-root-splice-and-permutation", () =>
        {
            var batch = Ops("[[\"r\",[1,2,3]]]");
            Check(DeltaValidation.IsBase(batch), "isBase");
            var value = DeltaApply.Apply(null, batch);
            value = DeltaApply.Apply(value, Ops("[[\"p\",[],1,1,[4]]]"));
            value = DeltaApply.Apply(value, Ops("[[\"m\",[],[2,0,1]]]"));
            Same("[3,1,4]", value, "result");
        }),
        Case("chord.delta.rejects-unsafe-and-malformed-paths", () =>
        {
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{}"), Ops("[[\"s\",[\"constructor\",\"prototype\",\"x\"],true]]")), "constructor walk");
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{\"values\":[1]}"), Ops("[[\"s\",[\"values\",3],2]]")), "gap");
            Throws<PathError>(() => DeltaApply.Apply(J("{\"value\":1}"), Ops("[[\"a\",[\"value\"],\"x\"]]")), "append to a number");
            Throws<ArgumentException>(() => DeltaValidation.AssertValidOp(J("[\"s\",\"value\",1]")), "string path");
            Equal("m permutation is not a bijection", Throws<ArgumentException>(() => DeltaValidation.AssertValidOp(J("[\"m\",[],[0,0]]")), "bijection").Message, "message");
        }),
        Case("chord.delta.validates-decoded-and-wire-vocabularies-separately", () =>
        {
            DeltaValidation.AssertValidOp(J("[\"s\",[\"value\"],1]"));
            Throws<ArgumentException>(() => DeltaValidation.AssertValidOp(J("[\"s\",1]")), "short form is wire-only");
            DeltaValidation.AssertValidWireOp(J("[\"s\",1]"));
            DeltaValidation.AssertValidWireOp(J("[\"#\",0,[\"value\"]]"));
            foreach (var decoded in new[] { "[\"r\",{\"value\":1}]", "[\"s\",[\"value\"],1]", "[\"d\",[\"value\"]]", "[\"a\",[\"value\"],\"x\"]",
                "[\"t\",[\"value\"],2]", "[\"p\",[\"value\"],0,0,[]]", "[\"m\",[\"value\"],[0]]" })
                DeltaValidation.AssertValidOp(J(decoded));
            foreach (var wireOnly in new[] { "[\"s\",1]", "[\"d\"]", "[\"a\",\"x\"]", "[\"t\",2]", "[\"p\",0,0,[]]", "[\"#\",0,[\"value\"]]", "[\"s\",0,1]" })
            {
                Throws<ArgumentException>(() => DeltaValidation.AssertValidOp(J(wireOnly)), wireOnly + " is not a decoded op");
                DeltaValidation.AssertValidWireOp(J(wireOnly));
            }
        }),
        Case("chord.delta.codec-interns-omits-and-round-trips", () =>
        {
            var path = DeltaPath.Of("nested", "text");
            var encoder = new DeltaEncoder(); var decoder = new DeltaDecoder();
            ImmutableArray<Op> first = [new TruncateOp(path, 1), new AppendOp(path, "x")];
            Check(first.SequenceEqual(decoder.Decode(encoder.Encode(first)), OpComparer), "first batch round trip");
            ImmutableArray<Op> second = [new AppendOp(path, "y")];
            var wire = encoder.Encode(second);
            Same("[[\"#\",0,[\"nested\",\"text\"]],[\"a\",0,\"y\"]]", JsonValue.Array(wire), "second use interns");
            Check(second.SequenceEqual(decoder.Decode(wire), OpComparer), "second batch round trip");
        }),
        Case("chord.delta.codec-resets-on-a-base", () =>
        {
            var encoder = new DeltaEncoder(); var path = DeltaPath.Of("value");
            encoder.Encode([new SetOp(path, 1)]);
            encoder.Encode([new SetOp(path, 2)]);
            Same("[[\"r\",{\"value\":3}]]", JsonValue.Array(encoder.Encode([new ReplaceOp(J("{\"value\":3}"))])), "base");
            Same("[[\"s\",[\"value\"],4]]", JsonValue.Array(encoder.Encode([new SetOp(path, 4)])), "after a base");
            var decoder = new DeltaDecoder();
            decoder.Decode([J("[\"#\",0,[\"a\"]]"), J("[\"a\",0,\"1\"]")]);
            decoder.Decode([J("[\"r\",{\"a\":\"\"}]")]);
            Throws<PathError>(() => decoder.Decode([J("[\"a\",0,\"2\"]")]), "decoder ids cleared on a base");
        }),
        Case("chord.delta.codec-rejects-unresolved-and-unsafe", () =>
        {
            Throws<PathError>(() => new DeltaDecoder().Decode([J("[\"a\",\"x\"]")]), "short form without a previous path");
            Throws<UnsafePathError>(() => new DeltaDecoder().Decode([J("[\"#\",0,[\"__proto__\"]]"), J("[\"s\",0,true]")]), "unsafe interned path");
            Throws<UnsafePathError>(() => new DeltaDecoder().Decode([J("[\"#\",0,[\"__proto__\",\"w\"]]"), J("[\"s\",0,true]")]), "unsafe interned prefix");
            Throws<ArgumentException>(() => new DeltaDecoder().Decode([J("[\"t\",[\"value\"],-1]")]), "negative truncate");
        }),
        Case("chord.delta.codec-short-forms-and-second-use", () =>
        {
            var path = DeltaPath.Of("value");
            Same("[[\"s\",[\"value\"],1],[\"s\",2]]", JsonValue.Array(new DeltaEncoder().Encode([new SetOp(path, 1), new SetOp(path, 2)])), "adjacent path omitted");
            var encoder = new DeltaEncoder(); var deep = DeltaPath.Of("a", "deep");
            Same("[[\"a\",[\"a\",\"deep\"],\"1\"]]", JsonValue.Array(encoder.Encode([new AppendOp(deep, "1")])), "first use inline");
            Same("[[\"#\",0,[\"a\",\"deep\"]],[\"a\",0,\"2\"]]", JsonValue.Array(encoder.Encode([new AppendOp(deep, "2")])), "second use interned");
            ImmutableArray<Op> nul = [new SetOp(DeltaPath.Of("a\u0000b"), 1), new SetOp(DeltaPath.Of("a", "b"), 2)];
            Check(nul.SequenceEqual(new DeltaDecoder().Decode(new DeltaEncoder().Encode(nul)), OpComparer), "paths with NUL do not collide");
            var baseEncoder = new DeltaEncoder();
            baseEncoder.Encode([new AppendOp(deep, "1")]); baseEncoder.Encode([new AppendOp(deep, "2")]);
            var baseWire = baseEncoder.Encode([new ReplaceOp(J("{\"a\":{\"deep\":\"x\"}}"))]);
            var after = baseEncoder.Encode([new AppendOp(deep, "3")]);
            Same("[[\"a\",[\"a\",\"deep\"],\"3\"]]", JsonValue.Array(after), "batches after a base are self-contained");
            var fresh = new DeltaDecoder(); fresh.Decode(baseWire);
            Check(fresh.Decode(after).SequenceEqual([new AppendOp(deep, "3")], OpComparer), "a fresh decoder recovers from the base");
        }),
        Case("chord.delta.codec-round-trips-mixed-streams", () =>
        {
            var batches = Enumerable.Range(0, 100).Select(index => ImmutableArray.Create<Op>(
                new SetOp(DeltaPath.Of("rows", index, "value"), index), new AppendOp(DeltaPath.Of("output"), index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new SpliceOp(DeltaPath.Of("tail"), index, 0, [JsonValue.Number(index)]))).ToList();
            var encoder = new DeltaEncoder(); var decoder = new DeltaDecoder();
            foreach (var batch in batches) Check(batch.SequenceEqual(decoder.Decode(encoder.Encode(batch)), OpComparer), "batch round trip");
        }),
        Case("chord.delta.replacement-payload-is-not-mutated", () =>
        {
            var replacement = J("{\"nested\":{\"value\":1}}");
            var next = DeltaApply.Apply(null, [new ReplaceOp(replacement), new SetOp(DeltaPath.Of("nested", "value"), 2)]);
            Same("{\"nested\":{\"value\":1}}", replacement, "replacement unchanged");
            Same("{\"nested\":{\"value\":2}}", next, "result");
        }),
        Case("chord.delta.reserved-names-inside-values-are-data", () =>
        {
            var value = J("{\"__proto__\":{\"z\":1}}");
            var output = DeltaApply.Apply(J("{}"), [new SetOp(DeltaPath.Of("value"), value)]);
            Check(((JsonObjectValue)At(output, "value")).ContainsKey("__proto__"), "own __proto__ kept as data");
            Same("{\"trap\":1}", DeltaApply.Apply(J("{}"), [new SetOp(DeltaPath.Of("trap"), 1)]), "plain property write");
        }),
        Case("chord.delta.array-index-safety", () =>
        {
            Same("{\"values\":[1,9,3]}", DeltaApply.Apply(J("{\"values\":[1,2,3]}"), Ops("[[\"s\",[\"values\",1],9]]")), "existing index");
            Same("{\"values\":[1,2,3,9]}", DeltaApply.Apply(J("{\"values\":[1,2,3]}"), Ops("[[\"s\",[\"values\",3],9]]")), "one past the end");
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{\"values\":[1,2,3]}"), Ops("[[\"s\",[\"values\",5],9]]")), "gap");
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{\"values\":[]}"), Ops("[[\"s\",[\"values\",4294967290],1]]")), "huge index");
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{\"values\":[1]}"), Ops("[[\"s\",[\"values\",\"0\"],9]]")), "string-spelled index");
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{\"values\":[\"a\"]}"), Ops("[[\"a\",[\"values\",\"0\"],\"b\"]]")), "string-spelled index append");
            Same("{\"values\":[1,null,null,9]}", DeltaApply.Apply(J("{\"values\":[1]}"), Ops("[[\"p\",[\"values\"],1,0,[null,null,9]]]")), "explicit growth");
            Throws<PathError>(() => DeltaApply.Apply(J("{\"values\":[1]}"), Ops("[[\"d\",[\"values\",1]]]")), "delete past the end");
            var items = Enumerable.Repeat<JsonValue>(JsonValue.Null, 300_000).ToImmutableArray();
            Equal(300_000, ((JsonArrayValue)At(DeltaApply.Apply(J("{\"values\":[]}"), [new SpliceOp(DeltaPath.Of("values"), 0, 0, items)]), "values")).Count, "large splice");
            Same("{\"values\":[]}", DeltaApply.Apply(J("{\"values\":[1,2]}"), Ops("[[\"p\",[\"values\"],0,1000000000,[]]]")), "removal past the end is clamped");
        }),
        Case("chord.delta.operation-structure-safety", () =>
        {
            Throws<ArgumentException>(() => Op.FromJson(J("[\"ZZZ\",[\"value\"],9]")), "unknown verb");
            Throws<ArgumentException>(() => Op.FromJson(J("[\"p\",[\"values\"],0,0,\"not-an-array\"]")), "splice payload");
            Throws<ArgumentException>(() => Op.FromJson(J("[\"s\",\"value\",9]")), "string path");
            Throws<ArgumentException>(() => Op.FromJson(J("{\"op\":\"s\"}")), "not a tuple");
            Throws<ArgumentException>(() => Op.FromJson(JsonValue.Null), "null");
            Throws<PathError>(() => DeltaApply.Apply(J("{\"value\":1}"), Ops("[[\"a\",[\"missing\"],\"x\"]]")), "append to a missing key");
            Throws<ArgumentException>(() => Op.FromJson(J("[\"t\",[\"value\"],-1]")), "negative truncate");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> ChordApplyImmutableCases() =>
    [
        Case("chord.apply-immutable.copies-touched-containers-and-preserves-payloads", () =>
        {
            var shared = J("{\"nested\":{\"value\":1}}"); var untouched = J("{\"value\":9}"); var rowPayload = J("{\"id\":4,\"label\":\"placed\"}");
            var input = J("{\"text\":\"abcdef\",\"stable\":{\"value\":7},\"branch\":{\"value\":1},\"copy\":null,\"placed\":null,\"untouched\":null," +
                "\"left\":null,\"right\":null,\"meta\":{\"count\":0,\"obsolete\":true},\"rows\":[{\"id\":1,\"label\":\"one\"},{\"id\":2,\"label\":\"two\"},{\"id\":3,\"label\":\"three\"}]}");
            ImmutableArray<Op> operations =
            [
                new TruncateOp(DeltaPath.Of("text"), 2), new AppendOp(DeltaPath.Of("text"), "!"),
                new SetOp(DeltaPath.Of("meta", "count"), 1), new SetOp(DeltaPath.Of("meta", "count"), 2), new DeleteOp(DeltaPath.Of("meta", "obsolete")),
                new SetOp(DeltaPath.Of("copy"), At(input, "branch")), new SetOp(DeltaPath.Of("copy", "value"), 2),
                new SetOp(DeltaPath.Of("placed"), shared), new SetOp(DeltaPath.Of("placed", "nested", "value"), 2),
                new SetOp(DeltaPath.Of("untouched"), untouched), new SetOp(DeltaPath.Of("left"), shared), new SetOp(DeltaPath.Of("right"), shared),
                new SetOp(DeltaPath.Of("left", "nested", "value"), 3),
                new SpliceOp(DeltaPath.Of("rows"), 1, 1, [rowPayload]), new SetOp(DeltaPath.Of("rows", 1, "label"), "edited"),
                new MoveOp(DeltaPath.Of("rows"), [1, 0, 2]), new SetOp(DeltaPath.Of("rows", 0, "label"), "moved"),
            ];
            var result = DeltaApply.Apply(input, operations);
            Equal("cdef!", ((JsonStringValue)At(result, "text")).Value, "text");
            Check(ReferenceEquals(At(result, "stable"), At(input, "stable")), "stable shared");
            Check(ReferenceEquals(At(result, "untouched"), untouched), "placed untouched payload shared");
            Check(!ReferenceEquals(At(result, "copy"), At(input, "branch")), "edited copy is new");
            Same("{\"value\":2}", At(result, "copy"), "copy");
            Same("{\"nested\":{\"value\":2}}", At(result, "placed"), "placed");
            Check(!ReferenceEquals(At(result, "left"), shared) && ReferenceEquals(At(result, "right"), shared), "only the edited alias is new");
            Same("{\"id\":4,\"label\":\"moved\"}", At(result, "rows", 0), "moved row");
            Same("{\"value\":1}", At(input, "branch"), "input branch");
            Same("{\"nested\":{\"value\":1}}", shared, "shared payload");
            Same("{\"id\":4,\"label\":\"placed\"}", rowPayload, "row payload");
            Same("{\"count\":2}", At(result, "meta"), "meta");
        }),
        Case("chord.apply-immutable.protects-root-replacement-payloads", () =>
        {
            var replacement = J("{\"nested\":{\"value\":1},\"values\":[1,2,3]}");
            var result = DeltaApply.ApplyBatches(null, [
                [new ReplaceOp(replacement)], [new SetOp(DeltaPath.Of("nested", "value"), 2)],
                [new SpliceOp(DeltaPath.Of("values"), 1, 1, [4, 5]), new MoveOp(DeltaPath.Of("values"), [3, 0, 1, 2])]]);
            Same("{\"nested\":{\"value\":2},\"values\":[3,1,4,5]}", result, "result");
            Same("{\"nested\":{\"value\":1},\"values\":[1,2,3]}", replacement, "replacement unchanged");
            var array = J("[1,2,3]");
            Same("[3,4,5]", DeltaApply.Apply(array, Ops("[[\"p\",[],1,1,[4,5]],[\"m\",[],[3,0,1,2]],[\"d\",[1]]]")), "root array edits");
            Same("[1,2,3]", array, "array unchanged");
        }),
        Case("chord.apply-immutable.batch-partitions-equal-flattened", () =>
        {
            var input = J("{\"text\":\"abcdef\",\"meta\":{\"count\":0},\"values\":[{\"id\":1,\"value\":1},{\"id\":2,\"value\":2},{\"id\":3,\"value\":3}]}");
            ImmutableArray<Op>[] batches =
            [
                [.. Ops("[[\"s\",[\"meta\",\"count\"],1],[\"p\",[\"values\"],1,1,[{\"id\":4,\"value\":4}]]]")], [],
                [.. Ops("[[\"m\",[\"values\"],[2,0,1]],[\"s\",[\"values\",2,\"value\"],40]]")], [.. Ops("[[\"t\",[\"text\"],2],[\"a\",[\"text\"],\"!\"]]")],
            ];
            var intermediate = DeltaApply.Apply(input, batches[0]);
            var snapshot = intermediate.ToJson();
            var sequential = intermediate;
            for (var index = 1; index < batches.Length; index++) sequential = DeltaApply.Apply(sequential, batches[index]);
            var streamed = DeltaApply.ApplyBatches(input, batches.Select(batch => (IEnumerable<Op>)batch));
            var flattened = DeltaApply.Apply(input, batches.SelectMany(batch => batch));
            Check(JsonValue.DeepEquals(streamed, sequential) && JsonValue.DeepEquals(streamed, flattened), "partitions agree");
            Equal(snapshot, intermediate.ToJson(), "intermediate unchanged");
            Equal(0d, ((JsonNumberValue)At(input, "meta", "count")).Value, "input unchanged");
        }),
        Case("chord.apply-immutable.no-partial-result-on-failure", () =>
        {
            var input = J("{\"nested\":{\"value\":1}}");
            Throws<UnsafePathError>(() => DeltaApply.ApplyBatches(input, [[new SetOp(DeltaPath.Of("nested", "value"), 2)],
                [new SetOp(DeltaPath.Of("constructor", "prototype", "polluted"), true)]]), "unsafe path in a later batch");
            Same("{\"nested\":{\"value\":1}}", input, "input unchanged");
            IEnumerable<IEnumerable<Op>> Throwing() { yield return [new SetOp(DeltaPath.Of("nested", "value"), 3)]; throw new InvalidOperationException("revision stream failed"); }
            Equal("revision stream failed", Throws<InvalidOperationException>(() => DeltaApply.ApplyBatches(input, Throwing()), "stream").Message, "stream failure");
            Throws<PathError>(() => DeltaApply.Apply(J("{\"values\":[]}"), Ops("[[\"s\",[\"values\",\"missing\",\"value\"],1]]")), "missing segment");
            Throws<UnsafePathError>(() => DeltaApply.Apply(J("{\"values\":[{}]}"), Ops("[[\"s\",[\"values\",\"0\",\"value\"],1]]")), "string index on an array");
        }),
        Case("chord.apply-immutable.fan-out-does-not-share-edits", () =>
        {
            var payload = J("{\"nested\":{\"value\":1}}");
            ImmutableArray<Op> operations = [new SetOp(DeltaPath.Of("placed"), payload), new SetOp(DeltaPath.Of("placed", "nested", "value"), 2)];
            var input = J("{\"placed\":null}");
            var first = DeltaApply.Apply(input, operations); var second = DeltaApply.Apply(input, operations);
            Check(JsonValue.DeepEquals(first, second) && !ReferenceEquals(first, second) && !ReferenceEquals(At(first, "placed"), At(second, "placed")), "independent results");
            Same("{\"nested\":{\"value\":1}}", payload, "payload unchanged");
        }),
        // Adapted: freezeDeep is unnecessary (values are immutable), and fractional indices (["list", 1.5]) cannot be written as C# path
        // segments; the other invalid paths are upstream's.
        Case("chord.apply-immutable.appends-to-an-array-not-yet-copied-in-one-copy-sharing-everything-else", () =>
        {
            var entries = JsonValue.Array(Enumerable.Range(0, 1_000).Select(index => (JsonValue)Obj(("index", index))));
            var input = Obj(("log", Obj(("entries", entries), ("meta", Obj(("count", 1_000))))), ("other", Obj(("value", 1))));
            var result = DeltaApply.Apply(input, Ops("[[\"p\",[\"log\",\"entries\"],1000,0,[{\"index\":1000}]],[\"p\",[\"log\",\"entries\"],1001,0,[{\"index\":1001}]]]"));
            var appended = (JsonArrayValue)At(result, "log", "entries");
            Equal(1_002, appended.Count, "length");
            Same("[{\"index\":1000},{\"index\":1001}]", JsonValue.Array(appended.Items[^2..]), "appended items");
            Equal(1_000, ((JsonArrayValue)At(input, "log", "entries")).Count, "base unchanged");
            Check(ReferenceEquals(entries[0], appended[0]), "elements shared");
            Check(ReferenceEquals(At(input, "log", "meta"), At(result, "log", "meta")), "sibling shared");
            Check(ReferenceEquals(At(input, "other"), At(result, "other")), "other branch shared");
            Check(!ReferenceEquals(At(input, "log"), At(result, "log")), "parent copied");

            var rootArray = J("[1,2]");
            Same("[1,2,3]", DeltaApply.Apply(rootArray, Ops("[[\"p\",[],2,0,[3]]]")), "root append");
            Same("[1,2]", rootArray, "root unchanged");
            // An appended item that is itself an array stays one element.
            Same("[1,2,[3,4]]", DeltaApply.Apply(rootArray, Ops("[[\"p\",[],2,0,[[3,4]]]]")), "array item");
            // Invalid paths fail with the general path's errors.
            var document = J("{\"values\":[],\"list\":[[1]],\"text\":\"a\"}");
            (string Op, Type Error)[] invalid =
            [
                ("[\"p\",[\"values\",\"missing\"],0,0,[]]", typeof(PathError)),
                ("[\"p\",[\"missing\"],0,0,[]]", typeof(PathError)),
                ("[\"p\",[\"values\",0],0,0,[]]", typeof(PathError)),
                ("[\"p\",[\"__proto__\"],0,0,[]]", typeof(UnsafePathError)),
                ("[\"p\",[\"list\",\"x\"],0,0,[]]", typeof(PathError)),
                ("[\"p\",[\"text\"],0,0,[]]", typeof(PathError)),
                ("[\"p\",[\"list\",-1],0,0,[]]", typeof(UnsafePathError)),
                ("[\"p\",[\"list\",0,\"constructor\"],0,0,[]]", typeof(UnsafePathError)),
            ];
            foreach (var (op, error) in invalid)
            {
                Exception? thrown = null;
                try { DeltaApply.Apply(document, [Op.FromJson(J(op))]); }
                catch (Exception caught) { thrown = caught; }
                Check(thrown?.GetType() == error, $"{op}: expected {error.Name}, got {thrown?.GetType().Name ?? "no error"}");
            }
            // An insert that is not at the end takes the general path.
            Same("[1,9,2]", DeltaApply.Apply(rootArray, Ops("[[\"p\",[],1,0,[9]]]")), "middle insert");
        }),
        Case("chord.apply-immutable.wide-object-repeated-writes", () =>
        {
            var builder = JsonObjectValue.CreateBuilder();
            for (var index = 0; index < 20_000; index++) builder["field" + index] = index;
            var input = builder.ToImmutable();
            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = DeltaApply.Apply(input, Enumerable.Range(0, 1_000).Select(index => (Op)new SetOp(DeltaPath.Of("field" + index), -index)));
            Equal(-999d, ((JsonNumberValue)At(result, "field999")).Value, "written");
            Equal(999d, ((JsonNumberValue)At(input, "field999")).Value, "input unchanged");
            Check(started.ElapsedMilliseconds < 2_000, "1,000 writes to a 20,000-key object stay proportional: " + started.ElapsedMilliseconds + " ms");
        }),
    ];

    private static readonly IEqualityComparer<Op> OpComparer = EqualityComparer<Op>.Create(
        (left, right) => left is not null && right is not null && JsonValue.DeepEquals(left.ToJson(), right.ToJson()), op => op.Verb.GetHashCode(StringComparison.Ordinal));
}
