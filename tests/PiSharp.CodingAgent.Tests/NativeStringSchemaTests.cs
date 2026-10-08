using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;

/// <summary>Direct tests of the production profile; no TypeBox conversion or source execution is implied.</summary>
internal static class NativeStringSchemaTests
{
    internal const string HelloParameters = "{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name to greet\"}}}";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("native string schema admits Hello annotations and retains opaque arguments without conversion", Hello),
        ("native string schema preserves optional, additional-property and legacy object behavior", OptionalAndLegacy),
        ("native string schema rejects unsupported validation keywords and invalid schema shapes", Unsupported),
        ("native string schema enforces exact schema, annotation, property and Unicode bounds", Bounds)
    ];

    private static Task Hello()
    {
        var source = JsonData.Parse(HelloParameters); var schema = NativeStringObjectSchema.Read(source);
        Check(!schema.Unconstrained && schema.AllowAdditional && schema.Properties.SetEquals(["name"]) &&
            schema.Required.SetEquals(["name"]), "Hello's omitted additionalProperties or required name changed.");
        foreach (var name in new[] { "Ada", "", "Ada \U0001f44b\nnext", "\0e\u0301" })
        {
            var arguments = JsonData.Parse(JsonSerializer.Serialize(new { name, opaque = new { ordered = new object?[] { 2, null, "\u6587" } } }));
            var before = arguments.ToString(); Check(schema.Validate(arguments), "Valid plain-string Hello input was rejected.");
            Equal(before, arguments.ToString()); Equal(HelloParameters, source.ToString());
            Equal(JsonValueKind.Null, arguments.Value.GetProperty("opaque").GetProperty("ordered")[1].ValueKind);
        }
        foreach (var input in new[] { "{}", "{\"name\":null}", "{\"name\":42}", "{\"name\":true}",
            "{\"name\":[]}", "{\"name\":{}}", "{\"Name\":\"Ada\"}", "null", "[]", "\"Ada\"" })
            Check(!schema.Validate(JsonData.Parse(input)), "Plain-string validation unexpectedly converted or accepted " + input);
        return Task.CompletedTask;
    }

    private static Task OptionalAndLegacy()
    {
        var optional = NativeStringObjectSchema.Read(JsonData.Parse("{\"type\":\"object\",\"description\":\"annotation\",\"properties\":{\"text\":{\"type\":\"string\"}}}"));
        Check(optional.Required.IsEmpty && optional.Validate(JsonData.EmptyObject) && optional.Validate(JsonData.Parse("{\"future\":null}")),
            "Omitted required or default additional-property behavior changed.");
        Check(!optional.Validate(JsonData.Parse("{\"text\":null}")), "A present optional field ceased to require a string.");
        foreach (var additional in new[] { true, false })
        {
            var value = JsonNode.Parse(HelloParameters)!; value["additionalProperties"] = additional;
            var schema = NativeStringObjectSchema.Read(JsonData.Parse(value.ToJsonString()));
            Check(schema.Validate(JsonData.Parse("{\"name\":\"Ada\"}")), "Known string field was rejected.");
            Equal(additional, schema.Validate(JsonData.Parse("{\"name\":\"Ada\",\"future\":{\"x\":null}}")));
            Check(!schema.Validate(JsonData.Parse("{\"name\":1}")), "Additional-property setting relaxed declared string validation.");
        }
        var empty = NativeStringObjectSchema.Read(JsonData.EmptyObject);
        Check(empty.Unconstrained && empty.Validate(JsonData.Parse("{\"name\":42,\"future\":null}")) && !empty.Validate(JsonData.Null),
            "Legacy empty schema lost its object-only behavior.");
        var strict = NativeStringObjectSchema.Read(JsonData.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}"));
        Check(strict.Validate(JsonData.Parse("{\"text\":\"kept\"}")) && !strict.Validate(JsonData.Parse("{\"text\":\"kept\",\"extra\":1}")) &&
            !strict.Validate(JsonData.EmptyObject), "Previously supported strict schema was widened.");
        var noRequired = JsonNode.Parse(HelloParameters)!; noRequired["required"] = new JsonArray();
        Check(NativeStringObjectSchema.Read(JsonData.Parse(noRequired.ToJsonString())).Validate(JsonData.EmptyObject), "Empty required array was rejected.");
        return Task.CompletedTask;
    }

    private static Task Unsupported()
    {
        foreach (var raw in new[] { "null", "[]", "true", "{\"type\":\"string\"}", "{\"type\":\"object\"}",
            "{\"type\":\"object\",\"properties\":null}", "{\"type\":\"object\",\"properties\":[]}" }) Reject(raw);
        foreach (var keyword in new[] { "minProperties", "maxProperties", "patternProperties", "dependentRequired", "allOf", "$ref", "$defs", "title", "default", "~kind" })
        {
            var schema = JsonNode.Parse(HelloParameters)!; schema[keyword] = JsonValue.Create(0); Reject(schema.ToJsonString());
        }
        foreach (var keyword in new[] { "minLength", "maxLength", "pattern", "enum", "const", "format", "default", "anyOf", "~optional" })
        {
            var schema = JsonNode.Parse(HelloParameters)!; schema["properties"]!["name"]![keyword] = JsonValue.Create(0); Reject(schema.ToJsonString());
        }
        foreach (var replacement in new[] { "null", "true", "\"string\"", "{\"type\":\"number\"}", "{\"type\":[\"string\",\"null\"]}", "{}" })
        {
            var schema = JsonNode.Parse(HelloParameters)!; schema["properties"]!["name"] = JsonNode.Parse(replacement); Reject(schema.ToJsonString());
        }
        foreach (var required in new[] { "null", "\"name\"", "[1]", "[\"missing\"]", "[\"name\",\"name\"]" })
        {
            var schema = JsonNode.Parse(HelloParameters)!; schema["required"] = JsonNode.Parse(required); Reject(schema.ToJsonString());
        }
        foreach (var value in new[] { "null", "0", "\"true\"", "{}", "{\"type\":\"string\"}" })
        {
            var schema = JsonNode.Parse(HelloParameters)!; schema["additionalProperties"] = JsonNode.Parse(value); Reject(schema.ToJsonString());
        }
        foreach (var annotation in new[] { "null", "1", "{}" })
        {
            var schema = JsonNode.Parse(HelloParameters)!; schema["description"] = JsonNode.Parse(annotation); Reject(schema.ToJsonString());
            schema = JsonNode.Parse(HelloParameters)!; schema["properties"]!["name"]!["description"] = JsonNode.Parse(annotation); Reject(schema.ToJsonString());
        }
        return Task.CompletedTask;
    }

    private static Task Bounds()
    {
        var properties = new JsonObject(); var required = new JsonArray();
        for (var index = 0; index < 32; index++) { var name = "p" + index; properties[name] = new JsonObject { ["type"] = "string" }; required.Add(name); }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        Equal(32, NativeStringObjectSchema.Read(JsonData.Parse(schema.ToJsonString())).Properties.Count);
        properties["p32"] = new JsonObject { ["type"] = "string" }; Reject(schema.ToJsonString()); properties.Remove("p32");
        required.Add("p32"); Reject(schema.ToJsonString());
        foreach (var name in new[] { "", new string('n', 129), "line\n", "zero\0" })
            Reject(JsonSerializer.Serialize(new { type = "object", properties = new Dictionary<string, object> { [name] = new { type = "string" } } }));
        var longest = JsonSerializer.Serialize(new { type = "object", properties = new Dictionary<string, object> { [new string('n', 128)] = new { type = "string" } } });
        _ = NativeStringObjectSchema.Read(JsonData.Parse(longest));
        foreach (var propertyAnnotation in new[] { false, true })
        {
            var annotated = JsonNode.Parse(HelloParameters)!;
            var target = propertyAnnotation ? annotated["properties"]!["name"]! : annotated;
            target["description"] = new string('d', 4096); _ = NativeStringObjectSchema.Read(JsonData.Parse(annotated.ToJsonString()));
            target["description"] = new string('d', 4097); Reject(annotated.ToJsonString());
        }
        var padding = 16_384 - HelloParameters.Length;
        _ = NativeStringObjectSchema.Read(JsonData.Parse(HelloParameters.Insert(1, new string(' ', padding))));
        Reject(HelloParameters.Insert(1, new string(' ', padding + 1)));
        var malformedPropertyRejected = false;
        try { _ = JsonData.Parse("{\"type\":\"object\",\"properties\":{\"\\ud800\":{\"type\":\"string\"}}}"); }
        catch (InvalidOperationException) { malformedPropertyRejected = true; }
        Check(malformedPropertyRejected, "Strict JsonData admitted an unpaired-surrogate property name before profile validation.");
        Reject("{\"type\":\"object\",\"properties\":{},\"description\":\"\\ud800\"}");
        var malformedValue = JsonData.Parse("{\"name\":\"\\ud800\"}"); var malformedValueRejected = false;
        try { _ = malformedValue.Value.GetProperty("name").GetString(); }
        catch (InvalidOperationException) { malformedValueRejected = true; }
        Check(malformedValueRejected, "Owned malformed value unexpectedly became a scalar string.");
        Check(!NativeStringObjectSchema.Read(JsonData.Parse(HelloParameters)).Validate(malformedValue), "Unpaired surrogate became an admitted string.");
        return Task.CompletedTask;
    }

    private static void Reject(string raw)
    {
        try { _ = NativeStringObjectSchema.Read(JsonData.Parse(raw)); }
        catch (NativeExtensionException error) when (error.Failure == NativeExtensionFailure.UnsupportedSchema) { return; }
        throw new InvalidOperationException("Unsupported schema gained admission: " + raw[..Math.Min(raw.Length, 256)]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Native schema values differ.");
}
