using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Contracts.Schema;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/schema.test.ts. pi-schema's compile is
// PiSharp.Contracts.Schema: JsonSchemaValidator for JSON values, and, where a value holds bytes (a Uint8Array upstream, a byte array here),
// the engine on the JavaScript value model, which alone holds byte arrays. The type-level halves (Static's type, expectTypeOf of method)
// have no C# counterpart; toEqual compares JSON values, ignoring key order, and Object.keys and Reflect.ownKeys compare the keys in order.
internal static partial class Program
{
    private static bool SchemaCheck(JsonNode schema, string value) => JsonSchemaValidator.Compile(schema).Check(JsonNode.Parse(value));

    /// <summary>pi-schema's compile(schema).check(value) on the JavaScript value model (values with byte arrays).</summary>
    private static bool SchemaCheckValue(JsonNode schema, object? value) => SchemaValidator.Compile(Js.FromNode(schema), new SchemaOptions()).Check(value);

    private static JsObject SchemaObject(params (string Key, object? Value)[] entries)
    {
        var value = new JsObject();
        foreach (var (key, item) in entries) value.Set(key, item);
        return value;
    }

    private static IEnumerable<(string, Func<Task>)> ContractsSchemaCases() =>
    [
        Case("contracts.schema.helpers-produce-plain-json-schema-with-bounds-under-their-json-schema-names", () =>
        {
            Json("""{"type":"string","minLength":1,"maxLength":3,"pattern":"^a"}""", Str(min: 1, max: 3, pattern: "^a"), "str");
            Json("""{"type":"number","minimum":0}""", Num(min: 0), "num");
            Json("""{"type":"integer","maximum":9}""", Int(max: 9), "int");
            Json("""{"type":"array","items":{"type":"boolean"},"minItems":1,"maxItems":2}""", Arr(Bool(), min: 1, max: 2), "arr");
            Json("""{"type":"null"}""", Nul(), "nul");
            Json("""{"type":"number","const":3}""", Lit(3), "lit");
            Json("""{"anyOf":[{"type":"string","const":"a"},{"type":"string","const":"b"}]}""", OneOf("a", "b"), "oneOf");
            Json("""{"anyOf":[{"type":"string"},{"type":"null"}]}""", Union(Str(), Nul()), "union");
            Json("""{"type":"object","patternProperties":{"^.*$":{"type":"integer"}}}""", Map(Int()), "map");
            Json("{}", Unknown(), "unknown");
        }),
        Case("contracts.schema.helpers-leave-absent-bounds-out-instead-of-writing-undefined", () =>
        {
            Sequence(["type"], Keys(Str()), "str()'s keys");
            Sequence(["type"], Keys(Bytes()), "bytes()'s keys");
        }),
        Case("contracts.schema.obj-is-always-closed-and-opt-makes-a-field-optional", () =>
        {
            var schema = Obj(("a", Str()), ("b", Opt(Int())));
            Json("""{"type":"object","required":["a"],"properties":{"a":{"type":"string"},"b":{"type":"integer"}},"additionalProperties":false}""", schema, "obj");
            Check(SchemaCheck(schema, """{"a":"x"}"""), "the optional field may be left out");
            Check(!SchemaCheck(schema, """{"a":"x","c":1}"""), "an unknown field is refused");
        }),
        Case("contracts.schema.static-gives-the-values-type-pi-schemas-compiler-checks-them", () =>
        {
            var schema = Obj(("kind", OneOf("chat", "agent")), ("n", Opt(Int())), ("tags", Arr(Str())), ("data", Bytes()));
            Check(SchemaCheckValue(schema, SchemaObject(("kind", "chat"), ("tags", new JsArray()), ("data", new byte[1]))), "a valid value");
        }),
        Case("contracts.schema.bytes-is-plain-data-the-marker-and-nothing-else", () =>
        {
            var schema = Bytes(min: 1, max: 2);
            Sequence(["type", "minLength", "maxLength"], Keys(schema), "bytes()'s own keys");
            Json("""{"type":"bytes","minLength":1,"maxLength":2}""", schema, "bytes()");
        }),
        Case("contracts.schema.bytes-checks-the-type-and-the-length-bounds", () =>
        {
            var schema = Bytes(min: 1, max: 2);
            Check(SchemaCheckValue(schema, new byte[1]), "1 byte");
            Check(SchemaCheckValue(schema, new byte[2]), "2 bytes");
            Check(!SchemaCheckValue(schema, new byte[0]), "0 bytes");
            Check(!SchemaCheckValue(schema, new byte[3]), "3 bytes");
            Check(!SchemaCheckValue(schema, new JsArray([1.0])), "an array of numbers");
            Check(!SchemaCheckValue(schema, "ab"), "a string");
        }),
        Case("contracts.schema.bytes-checks-inside-a-record-slices-md-l1-carry-over", () =>
        {
            var schema = Map(Bytes(max: 1));
            Check(SchemaCheckValue(schema, SchemaObject(("a", new byte[1]))), "1 byte in a record");
            Check(!SchemaCheckValue(schema, SchemaObject(("a", new byte[2]))), "2 bytes in a record");
        }),
        Case("contracts.schema.method-an-omitted-side-is-null", () =>
        {
            static void Same(MethodDeclaration expected, MethodDeclaration actual, string what)
            {
                Json(expected.Args.ToJsonString(), actual.Args, what + " args");
                Json(expected.Result.ToJsonString(), actual.Result, what + " result");
            }
            Same(new MethodDeclaration(Nul(), Nul()), Method(), "method()");
            Same(new MethodDeclaration(Str(), Nul()), Method(Str()), "method(str())");
            Same(new MethodDeclaration(Nul(), Int()), Method(null, Int()), "method(undefined, int())");
        }),
    ];
}
