using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Contracts.Schema;

// Authored native regressions against the contracts in Pi 7d073328 (2026-11-08).
// These extend the historical suite; they are not captured upstream execution evidence.
internal static partial class Program
{
    private const string GapRegressionUpstream = "7d073328e44337d3292a7db9b0f0308f679c5e97";

    private static IEnumerable<(string Id, Func<Task> Run)> GapCases =>
    [
        ("gap.C01.relative-root-local-refs", Sync(RelativeRootLocalRefs)),
        ("gap.C01.relative-root-annotations", Sync(RelativeRootAnnotations)),
        ("gap.C01.nested-resource-errors", Sync(NestedResourceErrors)),
        ("gap.C01.dynamic-root-coercion", Sync(DynamicRootCoercion)),
        ("gap.C01.relative-root-tool-arguments", Sync(RelativeRootToolArguments)),
        ("gap.F09.strict-validate-result", Sync(StrictValidateResult)),
        ("gap.F09.json-equality", Sync(JsonEquality)),
        ("gap.F09.recursive-and-typed-schema", Sync(RecursiveAndTypedSchema)),
        ("gap.schema.public-json-surrogate-boundary", Sync(PublicJsonSurrogateBoundary)),
        ("gap.contracts.source-generated-typed-schema", Sync(SourceGeneratedTypedSchema)),
        ("gap.contracts.aot-json-boundaries", Sync(AotJsonBoundaries)),
    ];

    private const string RelativeToolSchema = """
        {"$id":"schemas/tool.json","type":"object","properties":{"count":{"$ref":"#/$defs/count"}},
         "required":["count"],"$defs":{"count":{"type":"integer"}}}
        """;

    private static void RelativeRootLocalRefs()
    {
        foreach (var draft in new[] { JsonSchemaDraft.Draft202012, JsonSchemaDraft.Draft7 })
        foreach (var unresolved in new[] { JsonSchemaUnresolvedRefs.Throw, JsonSchemaUnresolvedRefs.Reject })
        {
            var validator = JsonSchemaValidator.Compile(JsonNode.Parse(RelativeToolSchema), new() { Draft = draft, UnresolvedRefs = unresolved, Coerce = true });
            Check(validator.Check(JsonNode.Parse("{\"count\":2}")), "relative root ref under " + draft + "/" + unresolved);
            Equal("/count type: expected integer, got string", string.Join("; ", validator.Errors(JsonNode.Parse("{\"count\":\"a\"}")).Select(e => $"{e.Path} {e.Keyword}: {e.Message}")), "relative root error");
            var parsed = ((CoercingJsonSchemaValidator)validator).Parse(JsonNode.Parse("{\"count\":\"2\"}"));
            Check(parsed.Ok, "relative root parse");
            Equal(2d, parsed.Value!["count"]!.GetValue<double>(), "relative root coerced number");
        }
    }

    private static void RelativeRootAnnotations()
    {
        var props = Compile("""
            {"$id":"schemas/tool.json","type":"object","properties":{"count":{"$ref":"#/$defs/count"}},
             "$defs":{"count":{"type":"integer"}},"unevaluatedProperties":false}
            """, new() { RejectUnresolvedRefs = true });
        Check(props.Check(Js.Parse("{\"count\":2}")), "annotating root applies id once");
        ErrorsEqual("/extra unevaluatedProperties: unknown field", props, "{\"count\":2,\"extra\":1}", "synthetic schema keeps resource base");
        var items = Compile("""
            {"$id":"schemas/list.json","type":"array","prefixItems":[{"$ref":"#/$defs/count"}],
             "$defs":{"count":{"type":"integer"}},"unevaluatedItems":false}
            """);
        Check(items.Check(Js.Parse("[2]")), "relative id prefixItems");
        ErrorsEqual("/1 unevaluatedItems: unexpected item", items, "[2,3]", "relative id array annotation error");
    }

    private static void NestedResourceErrors()
    {
        var nested = Compile("""
            {"$id":"https://example.com/root.json","allOf":[{"$ref":"#/$defs/child"}],"$defs":{"child":{
             "$id":"schemas/tool.json","$ref":"#/$defs/props","unevaluatedProperties":false,
             "$defs":{"props":{"properties":{"count":{"type":"integer"}}}}}}}
            """);
        ErrorsEqual("/extra unevaluatedProperties: unknown field", nested, "{\"count\":2,\"extra\":1}", "nested relative resource annotation error");
        var branch = Compile("""
            {"$id":"https://example.com/root.json","anyOf":[
              {"$id":"a/b.json","$ref":"#/$defs/n","$defs":{"n":{"type":"integer"}}},{"type":"boolean"}]}
            """);
        ErrorsEqual(" type: expected integer, got object", branch, "{}", "union branch resolves its own id");
    }

    private static void DynamicRootCoercion()
    {
        var schema = JsonNode.Parse("""
            {"$id":"schemas/root.json","$dynamicAnchor":"node","type":"object",
             "properties":{"count":{"$ref":"defs/n.json"},"children":{"type":"array","items":{"$dynamicRef":"#node"}}}}
            """);
        var validator = (CoercingJsonSchemaValidator)JsonSchemaValidator.Compile(schema, new()
        {
            Coerce = true,
            Schemas = new Dictionary<string, JsonNode?> { ["https://json-schema.invalid/schemas/defs/n.json"] = JsonNode.Parse("{\"type\":\"integer\"}") },
        });
        for (var i = 0; i < 2; i++)
        {
            var result = validator.Parse(JsonNode.Parse("{\"count\":\"2\",\"children\":[{\"count\":3}]}"));
            Check(result.Ok, "first and repeated dynamic parse");
            Equal(2d, result.Value!["count"]!.GetValue<double>(), "dynamic ref uses root base");
        }
        Check(!validator.Check(JsonNode.Parse("{\"children\":[{\"count\":\"bad\"}]}")), "dynamic children enforce root");
        Check(validator.Errors(JsonNode.Parse("{\"children\":[{\"count\":\"bad\"}]}")).Count > 0, "dynamic errors are reported");
    }

    private static void RelativeRootToolArguments()
    {
        var result = ToolArgumentValidation.ValidateJson("counter", RelativeToolSchema, "{\"count\":\"2\"}", ToolSchemaOrigin.JsonSchema);
        Check(result.IsValid, "relative root tool schema is usable");
        Equal("{\"count\":2}", result.ArgumentsJson, "relative root tool coercion");
        var invalid = ToolArgumentValidation.ValidateJson("counter", RelativeToolSchema, "{\"count\":\"bad\"}", ToolSchemaOrigin.JsonSchema);
        Check(!invalid.IsValid && invalid.ErrorMessage!.Contains("expected integer", StringComparison.Ordinal), "relative root tool reports type error, not unresolved ref");
    }

    private static void StrictValidateResult()
    {
        var validator = JsonSchemaValidator.Compile(JsonNode.Parse("""
            {"type":"object","properties":{"count":{"type":"integer"},"tags":{"type":"array","items":{"type":"string"}}},
             "required":["count"],"additionalProperties":false}
            """), new() { Coerce = true });
        var original = JsonNode.Parse("{\"count\":42,\"tags\":[\"a\"]}");
        var valid = validator.Validate(original);
        Check(valid.Ok && ReferenceEquals(original, valid.Value) && valid.Errors.Count == 0, "validate returns original node");
        var input = JsonNode.Parse("{\"count\":\"42\",\"tags\":\"a\"}");
        var invalid = validator.Validate(input);
        Check(!invalid.Ok && invalid.Value is null && invalid.Errors.Count == 2, "strict validate never coerces");
        Equal("42", input!["count"]!.GetValue<string>(), "strict validation never mutates input");
        Check(((CoercingJsonSchemaValidator)validator).Parse(input).Ok, "parse still coerces");
        var nullResult = JsonSchemaValidator.Compile(JsonNode.Parse("{\"type\":\"null\"}")).Validate(null);
        Check(nullResult.Ok && nullResult.Value is null && nullResult.Errors.Count == 0, "null success distinguished by Ok");
        var defaultSchema = JsonSchemaValidator.Compile(JsonNode.Parse("""
            {"type":"object","properties":{"n":{"type":"integer","default":2}},"required":["n"]}
            """));
        Check(!defaultSchema.Validate(new JsonObject()).Ok, "validation never inserts defaults");
    }

    private static void JsonEquality()
    {
        (string Left, string Right, bool Expected)[] cases =
        [
            ("1", "1.0", true), ("true", "1", false), ("null", "null", true),
            ("{\"a\":1,\"b\":[1,{\"c\":2}]}", "{\"b\":[1,{\"c\":2}],\"a\":1}", true),
            ("{\"a\":1}", "{\"a\":1,\"b\":2}", false),
            ("[1,2]", "[2,1]", false), ("[1]", "{\"0\":1}", false), ("{}", "[]", false),
        ];
        foreach (var (left, right, expected) in cases)
            Equal(expected, JsonSchemaValidator.Equal(JsonNode.Parse(left), JsonNode.Parse(right)), $"JSON equality of {left} and {right}");
    }

    private static void PublicJsonSurrogateBoundary()
    {
        var single = JsonSchemaValidator.Compile(JsonNode.Parse("""{"type":"string","minLength":1,"maxLength":1}"""));
        foreach (var escaped in new[] { "\"\\ud800\"", "\"\\udc00\"", "\"\\ud83d\\ude00\"" })
        {
            var value = JsonNode.Parse(escaped);
            Check(single.Check(value), "public schema accepts one escaped UTF16 scalar or lone code unit: " + escaped);
            Check(single.Validate(value).Ok && single.Errors(value).Count == 0, "public validation and explanation preserve escaped code units");
        }
        var pair = JsonNode.Parse("\"\\ud800A\"");
        Check(!single.Check(pair) && single.Errors(pair).Any(e => e.Keyword == "maxLength"), "lone high surrogate and ASCII count as two");
        var exact = JsonSchemaValidator.Compile(JsonNode.Parse("""{"type":"object","properties":{"values":{"type":"array","items":{"const":"\ud800"}}}}"""));
        var nested = JsonNode.Parse("""{"values":["\ud800"]}""");
        Check(exact.Check(nested), "escaped lone code unit survives schema constants and nested value adaptation");
        Check(!exact.Check(JsonNode.Parse("""{"values":["\ufffd"]}""")), "lone surrogate is not replaced by U+FFFD");
        Check(!JsonSchemaValidator.Equal(JsonNode.Parse("\"\\ud800\""), JsonNode.Parse("\"\\ufffd\"")), "public equality preserves the original code unit");
    }

    private sealed record SchemaTree(string Name, SchemaTree[] Children);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(SchemaTree))]
    private partial class SchemaGapJsonContext : JsonSerializerContext { }

    private static void SourceGeneratedTypedSchema()
    {
        var schema = JsonSchemaBuilder.Unsafe<SchemaTree>(JsonNode.Parse("""
            {"$id":"Tree","type":"object","properties":{"name":{"type":"string"},
             "children":{"type":"array","items":{"$ref":"Tree"}}},"required":["name","children"]}
            """));
        var validator = schema.Compile(SchemaGapJsonContext.Default.SchemaTree);
        var valid = new SchemaTree("root", [new("child", [])]);
        Check(validator.Check(valid) && ReferenceEquals(valid, validator.Validate(valid).Value), "source-generated metadata validates and preserves CLR identity");
        var invalid = new SchemaTree("root", [new(null!, [])]);
        Check(!validator.Validate(invalid).Ok && validator.Errors(invalid).Any(e => e.Path == "/children/0/name"),
            "source-generated metadata retains nested errors and serializer naming");
        var nullable = JsonSchemaBuilder.Unsafe<SchemaTree>().Compile(SchemaGapJsonContext.Default.SchemaTree).Validate(null);
        Check(nullable.Ok && nullable.Value is null, "explicit source-generated type parameter preserves null serialization");
    }

    private static void AotJsonBoundaries()
    {
        var node = new JsonObject { ["raw"] = JsonUtf16.StringNode("\ud800"), ["normal"] = "ok", ["number"] = 2, ["flag"] = true };
        using var written = JsonDocument.Parse(node.ToJsonString(new JsonSerializerOptions { TypeInfoResolver = JsonUtf16.AotResolver }));
        Equal("\ud800", JsonUtf16.GetString(written.RootElement.GetProperty("raw")), "AOT resolver preserves raw UTF16 node");
        Equal(2, written.RootElement.GetProperty("number").GetInt32(), "AOT resolver writes primitive numbers");
        Check(written.RootElement.GetProperty("flag").GetBoolean(), "AOT resolver writes primitive booleans");

        var visible = new ToolLoadoutTool(JsonData.Parse("""{"name":"visible","description":"before","parameters":{}}"""), ToolExposure.Direct);
        var hidden = new ToolLoadoutTool(JsonData.Parse("""{"name":"hidden","description":"before","parameters":{}}"""), ToolExposure.Direct);
        var presentation = ToolLoadoutPresentation.Prepare(new([visible, hidden], [visible, hidden], [visible, hidden]), (name, _) =>
            name == "visible" ? new() { Descriptions = ImmutableDictionary<string, string>.Empty.Add("visible", "after <&\n"), HiddenDeclarations = ["hidden"] } : null);
        var message = new TranscriptEntry("system", JsonData.Parse("""
            {"role":"system","opaque":"\ud800","toolsAdded":[{"name":"visible","description":"before","parameters":{}},{"name":"hidden","description":"before","parameters":{}}],
             "toolsRemoved":[{"name":"hidden"},{"name":"visible"}]}
            """));
        var projected = presentation.Project([message])[0].WireBody.Value;
        Equal(1, projected.GetProperty("toolsAdded").GetArrayLength(), "explicit JSON projection hides additions");
        Equal("after <&\n", projected.GetProperty("toolsAdded")[0].GetProperty("description").GetString(), "explicit JSON projection changes description");
        Equal("visible", projected.GetProperty("toolsRemoved")[0].GetProperty("name").GetString(), "explicit JSON projection hides removals");
        Equal("\ud800", JsonUtf16.GetString(projected.GetProperty("opaque")), "explicit JSON projection preserves unknown UTF16 value");
        Equal(2, message.WireBody.Value.GetProperty("toolsAdded").GetArrayLength(), "projection preserves original transcript");
        var usage = PiWireJson.ReadUsageObject(JsonData.Parse("""
            {"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3,
             "cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"total":3,"extra":"kept"}}
            """).Value);
        Equal(1m, usage.Cost.Input, "explicit cost field quoting retains usage decoding");
    }

    private static void RecursiveAndTypedSchema()
    {
        JsonObject BuildTree(JsonObject self) => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["children"] = new JsonObject { ["type"] = "array", ["items"] = self },
            },
            ["required"] = new JsonArray("name", "children"),
            ["additionalProperties"] = false,
        };
        var treeSchema = JsonSchemaBuilder.Recursive<SchemaTree>("schemas/tree.json", BuildTree);
        var serializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var validator = treeSchema.Compile(serializerOptions: serializerOptions);
        var tree = new SchemaTree("root", [new SchemaTree("leaf", [])]);
        var result = validator.Validate(tree);
        Check(result.Ok && ReferenceEquals(tree, result.Value), "typed recursive result preserves original CLR value");
        var invalid = validator.Validate(new SchemaTree("root", [new SchemaTree(null!, [])]));
        Check(!invalid.Ok && invalid.Errors.Any(e => e.Path == "/children/0/name"), "typed recursive validation checks each level");

        // The builder result is a portable JSON schema, and distinct nested resources work together.
        var forest = new JsonObject { ["type"] = "array", ["items"] = treeSchema.ToJsonSchema() };
        Check(JsonSchemaValidator.Compile(forest).Check(JsonNode.Parse("[{\"name\":\"a\",\"children\":[]}]")), "nested recursive resource");
        var schemaCopy = treeSchema.ToJsonSchema();
        schemaCopy["type"] = "null";
        Check(treeSchema.Compile(serializerOptions: serializerOptions).Check(tree), "exported schema is a defensive copy");

        var raw = JsonNode.Parse("{\"type\":\"integer\",\"minimum\":2}")!;
        var associated = JsonSchemaBuilder.Unsafe<int>(raw);
        raw["minimum"] = 10;
        var numbers = associated.Compile();
        Check(numbers.Check(2) && !numbers.Check(1), "unsafe association snapshots schema and performs real validation");
        Check(JsonSchemaBuilder.Unsafe<int>(new JsonObject { ["type"] = "string" }).Compile().Validate(2).Ok == false,
            "unsafe does not assert a false CLR/schema correspondence");
        Check(JsonSchemaBuilder.Unsafe<SchemaTree>().Compile().Check(tree), "omitted unsafe schema is unconstrained");
        Throws<ArgumentException>(() => JsonSchemaBuilder.Recursive("tree#anchor", BuildTree), "ambiguous recursive fragment rejected");
    }
}
