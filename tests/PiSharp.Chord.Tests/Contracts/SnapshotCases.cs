using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/snapshot.test.ts. A schema is a JsonNode tree here
// (Snapshot.cs), so a raw schema is a JsonObject with upstream's keys in upstream's order, and pi-schema's compile(schema).check(value) is
// SchemaCheck (JSON values) or SchemaCheckValue (values with byte arrays, which only pi-schema's JavaScript value model holds; both in
// SchemaCases.cs). Every refusal a JsonNode tree can carry is ported with upstream's message. Four inputs have no C# counterpart, and their
// assertions are not ported: a non-enumerable key (a TypeBox refinement), a symbol key and an accessor (a JsonObject's keys are all
// enumerable string-keyed data), and a cyclic schema (a JsonNode has one parent, and System.Text.Json refuses a cycle when it is built,
// which a PiSharp case shows). Upstream's `undefined` data has no JsonNode counterpart either (a null node is JSON null, which is data); its
// case refuses the C# value that is no JSON data, a JsonValue wrapping a CLR object, with upstream's message. A foreign prototype is a CLR
// object that is no JsonNode, and opt(...) an Optional.
internal static partial class Program
{
    private static JsonObject SnapshotOpen(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties };

    private static IEnumerable<(string, Func<Task>)> ContractsSnapshotCases() =>
    [
        // describe("vocabulary")
        Case("contracts.snapshot.vocabulary-accepts-primitives-arrays-tuples-objects-records-unions-intersections-defs-ref-constraints", () =>
        {
            var recursive = Parse("""
                {"$defs":{"Node":{"type":"object","properties":{"value":{"type":"number"},"next":{"anyOf":[{"type":"null"},{"$ref":"#/$defs/Node"}]}},
                 "required":["value","next"]}},"$ref":"#/$defs/Node"}
                """);
            var schemas = new JsonNode[]
            {
                Parse("""{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false}"""),
                new JsonObject { ["allOf"] = new JsonArray(Obj(("a", Str())), Obj(("b", Str()))) },
                Parse("""{"enum":["a","b"]}"""),
                recursive,
                Str(min: 1, pattern: "^x"),
                Parse("""{"type":"string","format":"email","default":"a@b.c","readOnly":true,"title":"t","description":"d"}"""),
                Parse("""{"type":"number","multipleOf":2,"exclusiveMinimum":0}"""),
                Parse("""{"type":"array","items":{"type":"string"},"uniqueItems":true}"""),
                Parse("""{"type":"object","minProperties":1,"maxProperties":2,"$comment":"c","deprecated":true,"examples":[{}]}"""),
                Map(Int()),
                Bytes(max: 4),
                Map(Bytes()),
            };
            foreach (var (schema, index) in schemas.Select((schema, index) => (schema, index)))
                Accepts(() => ContractsInternal.SnapshotSchema(schema, "s"), $"schema {index}: {schema.ToJsonString()}");
        }),
        .. new (string Name, Func<object?> Schema, string Message)[]
        {
            ("conditional", () => Parse("""{"if":{"type":"string"},"else":{"minLength":1}}"""), "unsupported keyword \"else\""),
            ("dependent", () => Parse("""{"type":"object","dependentSchemas":{"a":{}}}"""), "unsupported keyword \"dependentSchemas\""),
            ("dependent-required", () => Parse("""{"type":"object","dependentRequired":{"a":["b"]}}"""), "unsupported keyword \"dependentRequired\""),
            ("not", () => Parse("""{"not":{"type":"string"}}"""), "unsupported keyword \"not\""),
            ("contains", () => Parse("""{"type":"array","contains":{"type":"string"}}"""), "unsupported keyword \"contains\""),
            ("property-names", () => Parse("""{"type":"object","propertyNames":{"pattern":"^a"}}"""), "unsupported keyword \"propertyNames\""),
            ("unevaluated-properties", () => Parse("""{"unevaluatedProperties":false}"""), "unsupported keyword \"unevaluatedProperties\""),
            ("unevaluated-items", () => Parse("""{"unevaluatedItems":false}"""), "unsupported keyword \"unevaluatedItems\""),
            ("content-schema", () => Parse("""{"type":"string","contentSchema":{}}"""), "unsupported keyword \"contentSchema\""),
            ("definitions", () => Parse("""{"definitions":{}}"""), "unsupported keyword \"definitions\""),
            ("one-of", () => Parse("""{"oneOf":[{"type":"string"}]}"""), "unsupported keyword \"oneOf\""),
            ("unknown-keyword", () => Parse("""{"type":"string","flavour":"x"}"""), "unsupported keyword \"flavour\""),
            ("non-json-type", () => Parse("""{"type":"bigint"}"""), "unsupported type \"bigint\""),
            ("undefined", () => Parse("""{"type":"undefined"}"""), "unsupported type \"undefined\""),
            ("function", () => Parse("""{"type":"function","parameters":[],"returnType":{"type":"null"}}"""), "s: unsupported"),
            ("opt-outside-an-object", () => Opt(Str()), "s: expected a schema"),
            ("non-json-data", () => new JsonObject { ["type"] = "number", ["default"] = JsonValue.Create(double.NaN) }, "s.default: expected JSON data"),
            ("undefined-data", () => new JsonObject { ["type"] = "number", ["maximum"] = JsonValue.Create<object>(new object()) }, "s.maximum: expected JSON data"),
            ("foreign-prototype", () => new object(), "s: expected a schema"),
            ("array-as-schema", () => new JsonArray(), "s: expected a schema"),
        }.Select(test => Case($"contracts.snapshot.vocabulary-refuses-{test.Name}", () =>
            Refuses(() => ContractsInternal.SnapshotSchema(test.Schema(), "s"), test.Message, test.Name))),
        Case("contracts.snapshot.vocabulary-refuses-an-unsupported-type-naming-the-path", () =>
            Refuses(() => ContractsInternal.SnapshotSchema(Parse("""{"type":"object","properties":{"a":{"type":1}}}"""), "s"), "s.properties.a.type: unsupported type 1")),
        Case("contracts.snapshot.vocabulary-pisharp-a-cyclic-schema-cannot-be-built", () =>
        {
            var cyclic = new JsonObject { ["type"] = "array" };
            Throws<InvalidOperationException>(() => cyclic["items"] = cyclic, "a JsonNode cycle");
            var inner = new JsonObject();
            var outer = new JsonObject { ["items"] = inner };
            Throws<InvalidOperationException>(() => inner["items"] = outer, "an indirect JsonNode cycle");
        }),
        Case("contracts.snapshot.vocabulary-allows-a-schema-object-shared-by-two-places-not-a-cycle", () =>
        {
            var shared = Str();
            Accepts(() => ContractsInternal.SnapshotSchema(Obj(("a", shared), ("b", shared)), "s"), "a shared schema");
        }),
        // describe("closed arguments")
        Case("contracts.snapshot.closed-arguments-requires-every-object-nested-ones-included-to-be-closed", () =>
        {
            Accepts(() => ContractsInternal.SnapshotArguments(Obj(("a", Obj(("b", Str())))), "m.args"), "closed objects");
            Refuses(() => ContractsInternal.SnapshotArguments(Obj(("a", SnapshotOpen(new() { ["b"] = Str() }))), "m.args"),
                "m.args.properties.a: argument objects must be closed (additionalProperties: false)");
            Refuses(() => ContractsInternal.SnapshotArguments(SnapshotOpen(new()), "m.args"), "m.args: argument objects must be closed");
            Refuses(() => ContractsInternal.SnapshotArguments(Obj(("list", Arr(SnapshotOpen(new())))), "m.args"), "m.args.properties.list.items: argument objects must be closed");
            Refuses(() => ContractsInternal.SnapshotArguments(Parse("""{"type":"object"}"""), "m.args"), "argument objects must be closed");
        }),
        Case("contracts.snapshot.closed-arguments-accepts-a-raw-closed-object-schema-and-records", () =>
        {
            var closed = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["a"] = Str() }, ["additionalProperties"] = false };
            Accepts(() => ContractsInternal.SnapshotArguments(closed, "m"), "a raw closed object");
            Accepts(() => ContractsInternal.SnapshotArguments(Obj(("tags", Map(Str()))), "m"), "a record");
        }),
        Case("contracts.snapshot.closed-arguments-checks-objects-inside-defs-unions-and-tuples", () =>
        {
            JsonObject Open() => SnapshotOpen(new() { ["a"] = Str() });
            Refuses(() => ContractsInternal.SnapshotArguments(Union(Open(), Str()), "m"), "m.anyOf[0]: argument objects");
            Refuses(() => ContractsInternal.SnapshotArguments(new JsonObject { ["type"] = "array", ["items"] = new JsonArray(Open()) }, "m"), "m.items[0]: argument objects");
            Refuses(() => ContractsInternal.SnapshotArguments(new JsonObject { ["$defs"] = new JsonObject { ["A"] = Open() }, ["$ref"] = "A" }, "m"), "m.$defs.A: argument objects");
        }),
        Case("contracts.snapshot.closed-arguments-does-not-apply-to-state-or-results", () =>
            Accepts(() => ContractsInternal.SnapshotSchema(SnapshotOpen(new() { ["a"] = Str() }), "state"), "an open state object")),
        // describe("snapshots")
        Case("contracts.snapshot.snapshots-are-copies-independent-of-later-changes-to-the-apps-schema-objects", () =>
        {
            var app = Obj(("text", Str()), ("tags", Arr(Str())));
            var schema = ContractsInternal.SnapshotSchema(app, "s").Schema;
            app["properties"]!["text"]!["minLength"] = 5;
            app["required"]!.AsArray().Add("x");
            Check(!ReferenceEquals(schema, app), "the snapshot is not the app's schema");
            Check(SchemaCheck(schema, """{"text":"","tags":[]}"""), "the snapshot keeps the declared schema");
            Check(!SchemaCheck(app, """{"text":"","tags":[]}"""), "the app's schema changed");
        }),
        Case("contracts.snapshot.snapshots-copy-data-values-defaults-consts", () =>
        {
            var fallback = Parse("""{"a":[1]}""").AsObject();
            var schema = ContractsInternal.SnapshotSchema(new JsonObject { ["type"] = "object", ["default"] = fallback, ["additionalProperties"] = false }, "s").Schema;
            fallback["a"]!.AsArray().Add(2);
            Json("""{"a":[1]}""", schema["default"], "the copied default");
        }),
        Case("contracts.snapshot.snapshots-copy-a-bytes-leaf-as-data-the-compiled-check-comes-from-its-keywords", () =>
        {
            var schema = ContractsInternal.SnapshotSchema(Bytes(max: 2), "s").Schema;
            Json("""{"type":"bytes","maxLength":2}""", schema, "the bytes leaf");
            Check(SchemaCheckValue(schema, new byte[2]), "2 bytes");
            Check(!SchemaCheckValue(schema, new byte[3]), "3 bytes");
            // A record of bytes (slices.md L1 carry-over: refused while bytes carried a TypeBox refinement).
            var record = ContractsInternal.SnapshotSchema(Map(Bytes(min: 1)), "s").Schema;
            Check(SchemaCheckValue(record, SchemaObject(("a", new byte[1]))), "1 byte in a record");
            Check(!SchemaCheckValue(record, SchemaObject(("a", new byte[0]))), "0 bytes in a record");
        }),
        Case("contracts.snapshot.snapshots-refuse-a-bytes-marker-with-other-keywords-or-bad-bounds", () =>
        {
            Refuses(() => ContractsInternal.SnapshotSchema(Parse("""{"type":"bytes","items":{}}"""), "s"), "s: unsupported keyword \"items\"");
            Refuses(() => ContractsInternal.SnapshotSchema(Parse("""{"type":"bytes","maxLength":-1}"""), "s"), "s: bytes bounds must be non-negative integers");
        }),
        Case("contracts.snapshot.snapshots-normal-form-sorted-keys-sorted-required-and-enum-annotations-dropped-on-schema-nodes-only", () =>
        {
            var normal = ContractsInternal.SnapshotSchema(Parse("""
                {"title":"T","type":"object","required":["b","a"],
                 "properties":{"b":{"enum":["y","x"]},"a":{"type":"string","default":{"title":"kept","z":1,"a":2}}},"additionalProperties":false}
                """), "s").Normal;
            Equal("""{"additionalProperties":false,"properties":{"a":{"default":{"a":2,"title":"kept","z":1},"type":"string"},"b":{"enum":["x","y"]}},"required":["a","b"],"type":"object"}""",
                ChordJson.Stringify(normal), "JSON.stringify(normal)");
        }),
        Case("contracts.snapshot.snapshots-normal-form-keeps-union-and-tuple-order", () =>
        {
            var ab = ContractsInternal.SnapshotSchema(Union(Str(), Int()), "s").Normal;
            var ba = ContractsInternal.SnapshotSchema(Union(Int(), Str()), "s").Normal;
            Check(ChordJson.Stringify(ab) != ChordJson.Stringify(ba), "the union orders' normal forms differ");
            var tuple = ContractsInternal.SnapshotSchema(Parse("""{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}]}"""), "s").Normal;
            Equal("""{"prefixItems":[{"type":"string"},{"type":"integer"}],"type":"array"}""", ChordJson.Stringify(tuple), "the tuple keeps its order");
        }),
        // describe("bytes")
        Case("contracts.snapshot.bytes-mark-a-snapshot-with-a-bytes-leaf-anywhere-inside", () =>
        {
            Check(!ContractsInternal.SnapshotSchema(Str(), "s").Bytes, "str()");
            Check(ContractsInternal.SnapshotSchema(Obj(("a", Arr(Obj(("b", Bytes()))))), "s").Bytes, "a nested bytes leaf");
            Check(ContractsInternal.SnapshotSchema(Union(Str(), Bytes()), "s").Bytes, "a bytes union member");
            Check(!ContractsInternal.SnapshotSchema(Parse("""{"type":"object","default":{"type":"bytes"}}"""), "s").Bytes, "bytes inside data");
        }),
    ];
}
