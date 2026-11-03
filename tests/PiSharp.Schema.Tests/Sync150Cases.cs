using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Contracts.Schema;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/test/validator.test.ts, packages/schema/test/coerce.test.ts,
// packages/ai/src/utils/validation.ts
//
// Pi v1.5.0 sync, schema area: pi-schema's compile(), check/errors, coercion, unresolved references and formats, and tool argument
// validation on them. Expectations are authored from the upstream tests and source; builders (obj, opt, num, ...) are written out as
// the plain JSON they return.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150Cases =>
    [
        ("sync150.conformance-draft2020-12", Sync(() => Conformance("draft2020-12", draft7: false))),
        ("sync150.conformance-draft7", Sync(() => Conformance("draft7", draft7: true))),
        ("sync150.errors-at-the-field", Sync(ErrorsAtTheField)),
        ("sync150.errors-tagged-union-branch", Sync(ErrorsTaggedUnion)),
        ("sync150.errors-pointer-escaping-and-refs", Sync(ErrorsPointersAndRefs)),
        ("sync150.errors-messages", Sync(ErrorMessages)),
        ("sync150.values-own-properties-and-bytes", Sync(ValuesOwnPropertiesAndBytes)),
        ("sync150.string-lengths-in-code-points", Sync(StringLengths)),
        ("sync150.options-formats-documents-draft7", Sync(OptionsFormatsDocumentsDraft7)),
        ("sync150.malformed-schemas-throw", Sync(MalformedSchemasThrow)),
        ("sync150.unresolved-refs-throw-or-reject", Sync(UnresolvedRefs)),
        ("sync150.formats-standard", Sync(FormatsStandard)),
        ("sync150.coerce-copy-on-write-and-parse", Sync(CoerceCopyOnWriteAndParse)),
        ("sync150.coerce-pass1-optional-nulls", Sync(CoercePass1)),
        ("sync150.coerce-pass2-conversion-rules", Sync(CoercePass2)),
        ("sync150.coerce-pass3-primitives-and-conflicts", Sync(CoercePass3)),
        ("sync150.coerce-recursive-schemas", Sync(CoerceRecursive)),
        ("sync150.coerce-member-keywords", Sync(CoerceMemberKeywords)),
        ("sync150.public-api-json-nodes", Sync(PublicApi)),
        ("sync150.tool-arguments-message-and-paths", Sync(ToolArgumentsMessage)),
        ("sync150.tool-arguments-coerced-for-every-origin", Sync(ToolArgumentsEveryOrigin)),
        ("sync150.tool-arguments-dangling-ref-rejects-in-place", Sync(ToolArgumentsDanglingRef)),
        ("sync150.tool-arguments-uncompilable-schema", Sync(ToolArgumentsUncompilableSchema)),
    ];

    private const string Read = """
        {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":1}},"required":["path"],"additionalProperties":false}
        """;

    private const string Block = """
        {"anyOf":[
          {"type":"object","properties":{"kind":{"const":"text"},"text":{"type":"string"}},"required":["kind","text"],"additionalProperties":false},
          {"type":"object","properties":{"kind":{"const":"image"},"width":{"type":"integer","minimum":0}},"required":["kind","width"],"additionalProperties":false}
        ]}
        """;

    private static SchemaValidator Compile(string schema, SchemaOptions? options = null) => SchemaValidator.Compile(Js.Parse(schema), options ?? new SchemaOptions());
    private static SchemaValidator Coercing(string schema) => Compile(schema, new SchemaOptions { Formats = SchemaFormats.All, Coerce = true });

    private static string Errors(SchemaValidator validator, object? value) =>
        string.Join("; ", validator.Errors(value).Select(e => $"{e.Path} {e.Keyword}: {e.Message}"));

    private static void ErrorsEqual(string expected, SchemaValidator validator, string json, string what) => Equal(expected, Errors(validator, Js.Parse(json)), what);

    private static string Json(object? value) => Js.Stringify(value, 0) ?? "undefined";

    private static void ValueEqual(string expectedJson, object? actual, string what) => Equal(Json(Js.Parse(expectedJson)), Json(actual), what);

    private static string ParseText(SchemaValidator validator, string json)
    {
        var (ok, value, errors) = validator.Parse(Js.Parse(json));
        return ok ? "ok " + Json(value) : "errors " + string.Join("; ", errors.Select(e => $"{e.Path} {e.Keyword}: {e.Message}"));
    }

    /// <summary>coerce.test.ts coerceValue(): the coerced `value` of { value } under an object schema with one required property `value`.</summary>
    private static string CoerceValue(string schema, string valueJson)
    {
        var root = Coercing($$"""{"type":"object","properties":{"value":{{schema}}},"required":["value"]}""");
        var coerced = root.Coerce(Js.Parse($$"""{"value":{{valueJson}}}"""));
        return Json(((JsObject)coerced!).GetOwn("value"));
    }

    private static void CoercesTo(string schema, string input, string expected) =>
        Equal(Json(Js.Parse(expected)), CoerceValue(schema, input), $"coerce {input} under {schema}");

    // validator.test.ts "errors".
    private static void ErrorsAtTheField()
    {
        var read = Compile(Read);
        ErrorsEqual("/path required: missing; /offset minimum: expected >= 1; /extra additionalProperties: unknown field", read,
            """{"offset":0,"extra":true}""", "missing, minimum and unknown field");
        ErrorsEqual("", read, """{"path":"a.ts"}""", "a valid value has no errors");
        Check(read.Check(Js.Parse("""{"path":"a.ts"}""")) && !read.Check(Js.Parse("""{"path":1}""")), "check of read");
    }

    private static void ErrorsTaggedUnion()
    {
        var validator = Compile($$"""{"type":"array","items":{{Block}}}""");
        ErrorsEqual("/1/width minimum: expected >= 0", validator, """[{"kind":"text","text":"a"},{"kind":"image","width":-1}]""", "the tag's branch");
        ErrorsEqual("""/0/kind anyOf: expected one of ["text","image"]""", validator, """[{"kind":"video"}]""", "an unknown tag");
    }

    private static void ErrorsPointersAndRefs()
    {
        ErrorsEqual("/a~1b~0c type: expected string, got integer", Compile("""{"type":"object","properties":{"a/b~c":{"type":"string"}}}"""),
            """{"a/b~c":1}""", "JSON Pointer escaping");
        ErrorsEqual("/1/x required: missing",
            Compile("""{"$defs":{"point":{"type":"object","properties":{"x":{"type":"number"}},"required":["x"]}},"type":"array","items":{"$ref":"#/$defs/point"}}"""),
            """[{"x":1},{}]""", "errors follow $ref");
        var tree = Compile("""{"type":"object","properties":{"value":{"type":"number"},"children":{"type":"array","items":{"$ref":"#"}}},"required":["value"]}""");
        Check(tree.Check(Js.Parse("""{"value":1,"children":[{"value":2,"children":[{"value":3}]}]}""")), "a recursive schema accepts a tree");
        ErrorsEqual("/children/0/children/0/value required: missing", tree, """{"value":1,"children":[{"value":2,"children":[{}]}]}""", "recursive errors");
    }

    private static void ErrorMessages()
    {
        var values = Enumerable.Range(0, 5000).Select(i => $"\"v{i}\"").ToList();
        var big = Compile("""{"type":"object","properties":{"e":{"type":"string","enum":[""" + string.Join(",", values) + "]}}}");
        Check(big.Check(Js.Parse("""{"e":"v4999"}""")), "a 5000-value enum accepts its last member");
        Equal($"/e enum: expected one of [{string.Join(",", values.Take(20))}, ... and 4980 more]", Errors(big, Js.Parse("""{"e":"v5000"}""")), "at most 20 enum values");
        ErrorsEqual(""" enum: expected one of ["a","b"]""", Compile("""{"enum":["a","b"]}"""), "\"c\"", "a short enum");
        Check(Compile($$"""{"enum":[{{string.Join(",", Enumerable.Range(0, 10_000).Select(i => $"\"member{i}\""))}}]}""").Check("member9999"), "a 10,000-value enum");

        string At(string extra) => Errors(Compile($$"""{"type":"array",{{extra}}}"""), Js.Parse("""["a"]"""));
        Equal(" contains: expected at least 1 matching item", At("""  "contains":{"type":"number"}"""), "contains");
        Equal(" contains: expected at least 2 matching items", At("""  "contains":{"type":"number"},"minContains":2"""), "minContains");
        Equal(" contains: expected at most 0 matching items", At("""  "contains":{"type":"string"},"maxContains":0,"minContains":0"""), "maxContains");
        Equal(" contains: expected between 1 and 3 matching items", At("""  "contains":{"type":"number"},"minContains":1,"maxContains":3"""), "both bounds");

        var filter = Compile("""
            {"type":"object","properties":{"where":{"$ref":"#/$defs/F"}},"$defs":{"F":{"oneOf":[
              {"type":"object","properties":{"field":{"type":"string"},"eq":{"type":"string"}},"required":["field","eq"],"additionalProperties":false},
              {"type":"object","properties":{"and":{"type":"array","items":{"$ref":"#/$defs/F"}}},"required":["and"],"additionalProperties":false}]}}}
            """);
        ErrorsEqual("/where/and/0/eq type: expected string, got integer; /where/and/0/x additionalProperties: unknown field", filter,
            """{"where":{"and":[{"field":"a","eq":1,"x":2}]}}""", "a oneOf failure blames the branch declaring most properties");

        var twoPaths = Compile("""{"type":"object","properties":{"a":{"$ref":"#/$defs/P"},"b":{"$ref":"#/$defs/P"}},"$defs":{"P":{"type":"object","properties":{"n":{"type":"integer"}}}}}""");
        var shared = Js.Parse("""{"n":"x"}""");
        var value = new JsObject();
        value.Set("a", shared);
        value.Set("b", shared);
        Equal("/a/n type: expected integer, got string; /b/n type: expected integer, got string", Errors(twoPaths, value), "one object at two paths");
        ErrorsEqual(" type: expected string or null, got integer", Compile("""{"type":["string","null"]}"""), "1", "a type array");
        ErrorsEqual(" multipleOf: expected a multiple of 0.5", Compile("""{"type":"number","multipleOf":0.5}"""), "0.3", "multipleOf");
        ErrorsEqual(" not: must not match the schema in not", Compile("""{"not":{}}"""), "1", "not");
        ErrorsEqual("/a false: no value is allowed here", Compile("""{"properties":{"a":false}}"""), """{"a":1}""", "false");
    }

    private static void ValuesOwnPropertiesAndBytes()
    {
        var read = Compile(Read);
        var absent = new JsObject();
        absent.Set("path", "a.ts");
        absent.Set("offset", Js.Undefined);
        Check(read.Check(absent), "a property set to undefined is absent");
        var missing = new JsObject();
        missing.Set("path", Js.Undefined);
        Check(!read.Check(missing), "a required property set to undefined is missing");

        var bytes = Compile("""{"type":"object","properties":{"data":{"type":"bytes","maxLength":4}}}""");
        JsObject Data(object? data) { var o = new JsObject(); o.Set("data", data); return o; }
        Check(bytes.Check(Data(new byte[4])), "4 bytes");
        Check(!bytes.Check(Data(new byte[5])), "5 bytes");
        Check(!bytes.Check(Data(Js.Parse("[1,2]"))), "a JSON array is not bytes");
        Equal("/data maxLength: expected at most 4 bytes", Errors(bytes, Data(new byte[5])), "bytes length error");
        Check(!Compile("""{"type":"object"}""").Check(new byte[1]), "bytes are not an object");

        var proto = Compile("""{"type":"object","properties":{"constructor":{"type":"string"},"toString":{"type":"number"}},"required":["constructor"],"additionalProperties":false}""");
        Check(!proto.Check(Js.Parse("{}")), "constructor is an own property");
        Check(proto.Check(Js.Parse("""{"constructor":"a"}""")), "own constructor");
        Check(!proto.Check(Js.Parse("""{"constructor":"a","toString":"b"}""")), "own toString");
        Check(!Compile("""{"type":"constructor"}""").Check(1.0), "a type named like an Object.prototype member");
    }

    // validator.test.ts "string lengths": code points, an unpaired surrogate counting as one.
    private static void StringLengths()
    {
        var high = ((char)0xD800).ToString();
        var low = ((char)0xDC00).ToString();
        var face = char.ConvertFromUtf32(0x1F600);
        Check(Compile("""{"type":"string","minLength":2}""").Check(high + "A"), "lone high surrogate and A: 2");
        Check(!Compile("""{"type":"string","maxLength":1}""").Check(high + "A"), "lone high surrogate and A: not 1");
        Check(Compile("""{"type":"string","maxLength":1}""").Check(high), "lone high surrogate: 1");
        Check(Compile("""{"type":"string","maxLength":1}""").Check(face), "a pair: 1");
        Check(!Compile("""{"type":"string","minLength":2}""").Check(face), "a pair: not 2");
        Check(Compile("""{"type":"string","minLength":2}""").Check(low + high), "reversed surrogates: 2");
        ErrorsEqual(" minLength: expected at least 3 characters", Compile("""{"type":"string","minLength":3}"""), "\"ab\"", "minLength message");
    }

    private static void OptionsFormatsDocumentsDraft7()
    {
        const string email = """{"type":"string","format":"email"}""";
        Check(Compile(email).Check("not an email"), "format is an annotation without formats");
        var withFormats = Compile(email, new SchemaOptions { Formats = SchemaFormats.All });
        Check(!withFormats.Check("not an email") && withFormats.Check("a@example.com"), "format asserts with formats");
        ErrorsEqual(" format: expected format email", withFormats, "\"x\"", "format message");

        var point = Js.Parse("""{"type":"object","properties":{"x":{"type":"number"}},"required":["x"]}""");
        var documents = new SchemaOptions { Schemas = [new("https://example.com/point.json", point)] };
        var registered = Compile("""{"$ref":"https://example.com/point.json"}""", documents);
        Check(registered.Check(Js.Parse("""{"x":1}""")) && !registered.Check(Js.Parse("{}")), "$ref to a registered document");

        const string siblings = """{"definitions":{"s":{"type":"string"}},"$ref":"#/definitions/s","maxLength":1}""";
        Check(Compile(siblings, new SchemaOptions { Draft7 = true }).Check("ab"), "draft 7: $ref overrides its siblings");
        Check(!Compile(siblings).Check("ab"), "2020-12: $ref beside siblings");
    }

    private static void MalformedSchemasThrow()
    {
        Equal("A schema must be an object or a boolean, got 1", Throws<JsonSchemaException>(() => Compile("""{"properties":{"a":1}}"""), "malformed").Message, "malformed schema");
        // new RegExp's SyntaxError text; "[" is the upstream probe 'mcp-invalid-regex' (validation-probes.test.ts).
        Equal("Invalid regular expression: /(/: Unterminated group", Throws<JsonSchemaException>(() => Compile("""{"type":"string","pattern":"("}"""), "invalid pattern").Message,
            "an invalid pattern throws at compile");
        Equal("Invalid regular expression: /[/: Unterminated character class", Throws<JsonSchemaException>(() => Compile("""{"type":"string","pattern":"["}"""), "unterminated class").Message,
            "an unterminated class throws at compile");
        var schema = Js.Parse("""{"type":"object","properties":{"a":{"type":"string","pattern":"^a"}}}""");
        var before = Json(schema);
        _ = SchemaValidator.Compile(schema, new SchemaOptions { Coerce = true }).Parse(Js.Parse("""{"a":"b"}"""));
        Equal(before, Json(schema), "compile and parse never modify the schema");
    }

    private static void UnresolvedRefs()
    {
        const string dangling = """{"type":"object","properties":{"t":{"$ref":"#/definitions/Missing"}}}""";
        const string external = """{"type":"object","properties":{"t":{"$ref":"https://example.com/schemas/t.json"}}}""";
        Check(Throws<JsonSchemaException>(() => Compile(dangling), "dangling").Message.StartsWith("Unresolved $ref #/definitions/Missing", StringComparison.Ordinal), "dangling throws");
        Check(Throws<JsonSchemaException>(() => Compile(external), "external").Message.StartsWith("Unresolved $ref https://example.com/schemas/t.json", StringComparison.Ordinal), "external throws");
        foreach (var schema in new[] { dangling, external })
        {
            var validator = Compile(schema, new SchemaOptions { RejectUnresolvedRefs = true });
            Check(validator.Check(Js.Parse("{}")) && !validator.Check(Js.Parse("""{"t":1}""")), "reject only values at the place: " + schema);
        }

        ErrorsEqual("/t $ref: unresolved reference #/definitions/Missing", Compile(dangling, new SchemaOptions { RejectUnresolvedRefs = true }), """{"t":1}""", "unresolved reference error");
        Equal("errors /t $ref: unresolved reference #/definitions/Missing",
            ParseText(Compile(dangling, new SchemaOptions { RejectUnresolvedRefs = true, Coerce = true }), """{"t":"1"}"""), "values at the place stay uncoerced");
        ErrorsEqual("/a $ref: unresolved reference A", Compile("""{"type":"object","properties":{"a":{"$ref":"A"}},"required":["a"]}""", new SchemaOptions { RejectUnresolvedRefs = true }),
            """{"a":{"x":"1"}}""", "a TypeBox Ref without its definition");
    }

    // validator.test.ts "TypeBox's extra formats", plus one value either way for each standard format.
    private static void FormatsStandard()
    {
        (string Format, string Valid, string Invalid)[] samples =
        [
            ("date", "2024-02-29", "2023-02-29"),
            ("date-time", "2024-01-01T12:00:00Z", "2024-01-01 12:00:00"),
            ("time", "12:00:00+01:00", "25:00:00Z"),
            ("duration", "P1DT2H", "P1D2H"),
            ("email", "a@example.com", "not an email"),
            ("hostname", "example.com", "-example.com"),
            ("idn-hostname", "xn--bcher-kva.example", "-a.example"),
            ("ipv4", "192.168.0.1", "256.0.0.1"),
            ("ipv6", "::1", "1:2:3:4:5:6:7:8:9"),
            ("uri", "https://example.com/a?b#c", "/relative"),
            ("uri-reference", "/relative", "\\\\x"),
            ("iri", "https://example.com/a", "/relative"),
            ("iri-reference", "a/b", "\\\\x"),
            ("uri-template", "https://example.com/{id}", "https://example.com/{id"),
            ("json-pointer", "/a/b~1c", "a"),
            ("relative-json-pointer", "0/a", "/a"),
            ("regex", "^a+$", "("),
            ("uuid", "123e4567-e89b-12d3-a456-426614174000", "123e4567"),
            ("url", "https://example.com/a?b=1", "not a url"),
            ("json-pointer-uri-fragment", "#/a/b~1c", "#/a b"),
        ];
        foreach (var (format, valid, invalid) in samples)
        {
            var check = SchemaFormats.All[format];
            Check(check(valid), $"{format} accepts {valid}");
            Check(!check(invalid), $"{format} rejects {invalid}");
        }

        Check(!SchemaFormats.All["json-pointer-uri-fragment"]("/a"), "a fragment needs #");
        Equal(21, SchemaFormats.All.Count, "the standard formats");
        Check(JsonSchemaFormats.Standard.ContainsKey("idn-email"), "JsonSchemaFormats.Standard");
    }

    // coerce.test.ts "coerce" and "parse".
    private static void CoerceCopyOnWriteAndParse()
    {
        var schema = Coercing("""{"type":"object","properties":{"count":{"type":"number"},"tags":{"type":"array","items":{"type":"string"}}},"required":["count","tags"],"additionalProperties":false}""");
        var value = Js.Parse("""{"count":"42","tags":"a"}""");
        ValueEqual("""{"count":42,"tags":["a"]}""", schema.Coerce(value), "coerced");
        ValueEqual("""{"count":"42","tags":"a"}""", value, "the input is unchanged");

        var same = Js.Parse("""{"a":{"b":[1,2]},"c":"x"}""");
        var unchanged = Coercing("""{"type":"object","properties":{"a":{"type":"object","properties":{"b":{"type":"array","items":{"type":"number"}}}},"c":{"type":"string"}}}""");
        Check(ReferenceEquals(unchanged.Coerce(same), same), "nothing changes: the value itself");

        var inner = Js.Parse("""{"b":[1,2]}""");
        var mixed = new JsObject();
        mixed.Set("a", inner);
        mixed.Set("c", Js.Parse("""{"d":"1"}"""));
        var paths = Coercing("""{"type":"object","properties":{"a":{"type":"object","properties":{"b":{"type":"array","items":{"type":"number"}}}},"c":{"type":"object","properties":{"d":{"type":"number"}}}}}""");
        var coerced = (JsObject)paths.Coerce(mixed)!;
        ValueEqual("""{"a":{"b":[1,2]},"c":{"d":1}}""", coerced, "only changed paths");
        Check(!ReferenceEquals(coerced, mixed) && ReferenceEquals(coerced.GetOwn("a"), inner), "unchanged subtrees are shared");

        var counter = Coercing("""{"type":"object","properties":{"count":{"type":"number"},"name":{"type":"string"}},"required":["count","name"],"additionalProperties":false}""");
        Equal("ok {\"count\":42,\"name\":\"7\"}", ParseText(counter, """{"count":"42","name":7}"""), "parse returns the coerced value");
        Equal("errors /count type: expected number, got string", ParseText(counter, """{"count":"many","name":7}"""), "parse returns the coerced value's errors");
    }

    private static void CoercePass1()
    {
        var schema = Coercing("""
            {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"number"},"nullable":{"anyOf":[{"type":"string"},{"type":"null"}]},
             "metadata":{"type":"object","properties":{"enabled":{"type":"boolean"}},"additionalProperties":false}},"required":["path","metadata"]}
            """);
        ValueEqual("""{"path":"file.txt","nullable":null,"metadata":{}}""",
            schema.Coerce(Js.Parse("""{"path":"file.txt","offset":null,"nullable":null,"metadata":{"enabled":null}}""")), "optional nulls dropped");
        ValueEqual("""{"value":null}""", Coercing("""{"type":"object","properties":{"value":{"$ref":"#/$defs/value"}},"$defs":{"value":{"anyOf":[{"type":"number"},{"type":"null"}]}}}""")
            .Coerce(Js.Parse("""{"value":null}""")), "a null under a $ref stays");
        ValueEqual("""[{"a":"x"}]""", Coercing("""{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}},"required":["a"],"additionalProperties":false}}""")
            .Coerce(Js.Parse("""[{"a":"x","b":null}]""")), "array items walked");
    }

    private static void CoercePass2()
    {
        CoercesTo("""{"type":"number"}""", "\"42\"", "42");
        CoercesTo("""{"type":"number"}""", "\"\"", "0");
        CoercesTo("""{"type":"number"}""", "\"true\"", "1");
        CoercesTo("""{"type":"integer"}""", "\"3.5\"", "3");
        CoercesTo("""{"type":"integer"}""", "1.5", "1");
        CoercesTo("""{"type":"boolean"}""", "\"1\"", "true");
        CoercesTo("""{"type":"boolean"}""", "\"FALSE\"", "false");
        CoercesTo("""{"type":"null"}""", "\"null\"", "null");
        CoercesTo("""{"type":"string"}""", "42", "\"42\"");
        CoercesTo("""{"type":"array","items":{"type":"number"}}""", "\"1\"", "[1]");
        CoercesTo("""{"type":"array","items":{"type":"string"}}""", "null", """["null"]""");
        const string tuple = """{"type":"array","prefixItems":[{"type":"number"},{"type":"boolean"}],"items":false}""";
        CoercesTo(tuple, """["1","true"]""", "[1,true]");
        CoercesTo(tuple, "\"1\"", "\"1\"");
        CoercesTo("""{"const":1}""", "\"1\"", "1");
        CoercesTo("""{"const":1}""", "\"2\"", "\"2\"");
        CoercesTo("""{"enum":[1,2,"hello"]}""", "\"2\"", "2");
        CoercesTo("""{"enum":[1,2,"hello"]}""", "\"world\"", "\"world\"");
        const string union = """{"anyOf":[{"type":"number"},{"type":"null"}]}""";
        CoercesTo(union, "null", "null");
        CoercesTo(union, "\"42\"", "42");
        CoercesTo(union, "\"null\"", "null");
        CoercesTo("""{"oneOf":[{"type":"boolean"},{"type":"number"}]}""", "\"1\"", "true");
        ValueEqual("""{"a":1,"b":true}""", Coercing("""{"allOf":[{"type":"object","properties":{"a":{"type":"number"}},"required":["a"]},{"type":"object","properties":{"b":{"type":"boolean"}},"required":["b"]}]}""")
            .Coerce(Js.Parse("""{"a":"1","b":"true"}""")), "allOf members in turn");
        ValueEqual("""{"item":{"id":7}}""", Coercing("""{"type":"object","properties":{"item":{"$ref":"#/$defs/item"}},"$defs":{"item":{"type":"object","properties":{"id":{"type":"integer"}}}}}""")
            .Coerce(Js.Parse("""{"item":{"id":"7"}}""")), "follows $ref");
        ValueEqual("""{"a":1,"n_x":2,"other":true}""", Coercing("""{"type":"object","properties":{"a":{"type":"number"}},"patternProperties":{"^n_":{"type":"number"}},"additionalProperties":{"type":"boolean"}}""")
            .Coerce(Js.Parse("""{"a":"1","n_x":"2","other":"true"}""")), "patternProperties and additionalProperties");
    }

    private static void CoercePass3()
    {
        CoercesTo("""{"type":"number"}""", "true", "1");
        CoercesTo("""{"type":"number"}""", "null", "0");
        CoercesTo("""{"type":"integer"}""", "\"42\"", "42");
        CoercesTo("""{"type":"boolean"}""", "\"true\"", "true");
        CoercesTo("""{"type":"boolean"}""", "0", "false");
        CoercesTo("""{"type":"string"}""", "true", "\"true\"");
        CoercesTo("""{"type":"null"}""", "\"\"", "null");
        CoercesTo("""{"type":"null"}""", "0", "null");
        CoercesTo("""{"type":"null"}""", "false", "null");
        CoercesTo("""{"type":["number","string"]}""", "\"1\"", "\"1\"");
        CoercesTo("""{"type":["boolean","number"]}""", "\"1\"", "1");
        CoercesTo("""{"type":["integer","null"]}""", "\"5\"", "5");
        CoercesTo("""{"type":["array","null"],"items":{"type":"string"}}""", "null", "null");
        CoercesTo("""{"anyOf":[{"type":"number"},{"type":"null"}]}""", "\"42\"", "42");
        CoercesTo("""{"oneOf":[{"type":"number"},{"type":"null"}]}""", "null", "null");
        var integer = Coercing("""{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"]}""");
        foreach (var input in new[] { "\"abc\"", "\"4x\"", "{}", """["1","2"]""" })
            Check(!integer.Check(integer.Coerce(Js.Parse($$"""{"value":{{input}}}"""))), "no rule coerces " + input);
        // Where the passes conflict, the conversion rules win.
        CoercesTo("""{"type":"string"}""", "null", "\"null\"");
        CoercesTo("""{"type":"boolean"}""", "\"0\"", "false");
        CoercesTo("""{"type":"integer"}""", "\"42.1\"", "42");
        ValueEqual("{}", Coercing("""{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}""").Coerce(Js.Parse("""{"name":null}""")),
            "an optional null is dropped before it converts");
    }

    private static void CoerceRecursive()
    {
        const string tree = """{"type":"object","properties":{"t":{"$ref":"#/$defs/Tree"}},"$defs":{"Tree":{"anyOf":[{"type":"string"},{"type":"array","items":{"$ref":"#/$defs/Tree"}}]}}}""";
        var trees = Coercing(tree);
        Equal("ok {\"t\":[\"a\",[\"b\"]]}", ParseText(trees, """{"t":["a",["b"]]}"""), "a tree stays");
        Equal("ok {\"t\":\"5\"}", ParseText(trees, """{"t":5}"""), "the first accepting branch");
        Equal("ok {\"t\":[\"5\"]}", ParseText(trees, """{"t":[5]}"""), "items of the array branch");
        Check(ParseText(trees, """{"t":{}}""").StartsWith("errors", StringComparison.Ordinal), "an object is no tree");
        ValueEqual("""{"t":{"a":1}}""", trees.Coerce(Js.Parse("""{"t":{"a":1}}""")), "left as it is");
        var json = Coercing("""
            {"type":"object","properties":{"data":{"$ref":"#/$defs/Json"}},"required":["data"],"$defs":{"Json":{"anyOf":[{"type":"string"},{"type":"number"},{"type":"boolean"},
             {"type":"array","items":{"$ref":"#/$defs/Json"}},{"type":"object","additionalProperties":{"$ref":"#/$defs/Json"}}]}}}
            """);
        Equal("ok {\"data\":{\"a\":[1,\"null\"]}}", ParseText(json, """{"data":{"a":[1,null]}}"""), "a JSON value schema");

        var self = Coercing("""{"type":"object","properties":{"value":{"anyOf":[{"type":"number"},{"type":"array","items":{"$ref":"#/properties/value"}}]}},"required":["value"]}""");
        Equal("ok {\"value\":42}", ParseText(self, """{"value":"42"}"""), "scalar");
        Equal("ok {\"value\":[1,[2]]}", ParseText(self, """{"value":["1",["2"]]}"""), "nested arrays");

        var reentry = Coercing("""{"anyOf":[{"type":"number"},{"type":"array","items":{"$ref":"#"}}]}""");
        for (var i = 0; i < 20; i++)
        {
            Check(ParseText(reentry, "{}").StartsWith("errors", StringComparison.Ordinal), "{} fails");
            Equal("ok 42", ParseText(reentry, "\"42\""), "a number");
            var input = Js.Parse("""[1,"2",["3"]]""");
            var (ok, value, _) = reentry.Parse(input);
            Check(ok, "nested input parses");
            ValueEqual("[1,2,[3]]", value, "nested input");
            ValueEqual("""[1,"2",["3"]]""", input, "the input is unchanged");
        }

        // A deep value every branch rejects converts and explains in linear time.
        var deep = Coercing("""
            {"type":"object","properties":{"s":{"$ref":"#/$defs/S"}},"$defs":{"S":{"anyOf":[
              {"type":"object","additionalProperties":{"$ref":"#/$defs/S"},"required":["a"]},
              {"type":"object","additionalProperties":{"$ref":"#/$defs/S"},"required":["b"]}]}}}
            """);
        var valueJson = """{"x":1}""";
        for (var i = 0; i < 40; i++) valueJson = $$"""{"c":{{valueJson}}}""";
        var expected = Enumerable.Range(0, 41).Select(i => $"/s{string.Concat(Enumerable.Repeat("/c", i))}/a required: missing")
            .Append($"/s{string.Concat(Enumerable.Repeat("/c", 40))}/x type: expected object, got integer");
        Equal("errors " + string.Join("; ", expected), ParseText(deep, $$"""{"s":{{valueJson}}}"""), "deep union errors");
    }

    private static void CoerceMemberKeywords()
    {
        ValueEqual("""{"n_a":5,"other":"7"}""", Coercing("""{"type":"object","patternProperties":{"^n_":{"type":["integer","null"]}},"additionalProperties":{"type":"string"}}""")
            .Coerce(Js.Parse("""{"n_a":"5","other":7}""")), "pattern-matched properties skip additionalProperties");
        const string item = """{"type":"object","properties":{"n":{"type":"integer","minimum":1}},"additionalProperties":false}""";
        foreach (var schema in new[]
        {
            $$"""{"type":"object","properties":{"entry":{{item}}},"required":["entry"],"additionalProperties":false}""",
            $$"""{"type":"object","additionalProperties":{{item}}}""",
            $$"""{"type":"object","patternProperties":{"^entry$":{{item}}},"additionalProperties":false}""",
        })
            Equal("ok {\"entry\":{}}", ParseText(Coercing(schema), """{"entry":{"n":null}}"""), "optional nulls inside member values: " + schema);
        Equal("ok {\"n\":null}", ParseText(Coercing("""{"type":"object","additionalProperties":{"type":["integer","null"]}}"""), """{"n":null}"""), "additional keys are not optional properties");
        ValueEqual("[5,true]", Coercing("""{"type":"array","prefixItems":[{"type":["integer","null"]},{"type":["boolean","null"]}]}""").Coerce(Js.Parse("""["5","true"]""")), "tuple items in pass 3");
        Equal("ok {\"a\":3}", ParseText(Coercing("""{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"anyOf":[{"required":["a"]},{"required":["b"]}]}"""),
            """{"a":"3.5"}"""), "a union node's own properties convert");
        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            var schema = Coercing("""{"type":"object",""" + $"\"{keyword}\"" + """
                :[{"type":"object","properties":{"a":{"type":"array","items":{"type":"integer"}}},"required":["a"]},
                 {"type":"object","properties":{"b":{"type":"boolean"}},"required":["b"]}],"properties":{"c":{"type":"integer"}},"required":["c"]}
                """);
            Equal("ok {\"a\":[3],\"c\":2}", ParseText(schema, """{"a":"3","c":"2"}"""), keyword + " candidates after the node's own properties");
        }
    }

    // The public JsonNode surface: Compile, Check, Errors, Coerce and Parse.
    private static void PublicApi()
    {
        var plain = JsonSchemaValidator.Compile(JsonNode.Parse(Read));
        Check(plain is not CoercingJsonSchemaValidator, "no coercion without Coerce");
        Check(plain.Check(JsonNode.Parse("""{"path":"a"}""")) && !plain.Check(JsonNode.Parse("""{"path":1}""")), "Check");
        Equal(new JsonSchemaValidationError("/path", "type", "expected string, got integer"), plain.Errors(JsonNode.Parse("""{"path":1}""")).Single(), "Errors");

        var coercing = (CoercingJsonSchemaValidator)JsonSchemaValidator.Compile(JsonNode.Parse(Read), new JsonSchemaCompileOptions { Coerce = true });
        var input = JsonNode.Parse("""{"path":"a","offset":"3"}""");
        Equal("""{"path":"a","offset":3}""", coercing.Coerce(input)!.ToJsonString(), "Coerce");
        Equal("""{"path":"a","offset":"3"}""", input!.ToJsonString(), "the input node is unchanged");
        var parsed = coercing.Parse(JsonNode.Parse("""{"path":"a","offset":"0"}"""));
        Check(!parsed.Ok && parsed.Errors.Single().Message == "expected >= 1" && parsed.Value!.ToJsonString() == """{"path":"a","offset":0}""", "Parse");

        var draft7 = JsonSchemaValidator.Compile(JsonNode.Parse("""{"definitions":{"s":{"type":"string"}},"$ref":"#/definitions/s","maxLength":1}"""),
            new JsonSchemaCompileOptions { Draft = JsonSchemaDraft.Draft7 });
        Check(draft7.Check(JsonValue.Create("ab")), "Draft7");
        var reject = JsonSchemaValidator.Compile(JsonNode.Parse("""{"properties":{"t":{"$ref":"#/$defs/none"}}}"""), new JsonSchemaCompileOptions { UnresolvedRefs = JsonSchemaUnresolvedRefs.Reject });
        Check(reject.Check(JsonNode.Parse("{}")) && !reject.Check(JsonNode.Parse("""{"t":0}""")), "UnresolvedRefs.Reject");
        Throws<JsonSchemaException>(() => JsonSchemaValidator.Compile(JsonNode.Parse("""{"$ref":"#/$defs/none"}""")), "UnresolvedRefs.Throw");
        var documents = JsonSchemaValidator.Compile(JsonNode.Parse("""{"$ref":"https://example.com/a.json"}"""),
            new JsonSchemaCompileOptions { Schemas = new Dictionary<string, JsonNode?> { ["https://example.com/a.json"] = JsonNode.Parse("""{"type":"integer"}""") } });
        Check(documents.Check(JsonValue.Create(1)) && !documents.Check(JsonValue.Create(1.5)), "Schemas");
        var formats = JsonSchemaValidator.Compile(JsonNode.Parse("""{"format":"ipv4"}"""), new JsonSchemaCompileOptions { Formats = JsonSchemaFormats.Standard });
        Check(!formats.Check(JsonValue.Create("x")) && formats.Check(JsonValue.Create(1)), "Formats apply to strings only");
    }

    // packages/ai/src/utils/validation.ts: the message the model sees.
    private static void ToolArgumentsMessage()
    {
        var edit = JsonNode.Parse("""{"type":"object","properties":{"edits":{"type":"array","items":{"type":"object","properties":{"oldText":{"type":"string"}},"required":["oldText"]}}},"required":["edits"]}""");
        var result = ToolArgumentValidation.Validate("edit", edit, JsonNode.Parse("""{"edits":[{"oldText":{}},{}]}"""));
        Equal("""
            Validation failed for tool "edit":
              - edits.0.oldText: expected string, got object
              - edits.1.oldText: missing

            Received arguments:
            {
              "edits": [
                {
                  "oldText": {}
                },
                {}
              ]
            }
            """.ReplaceLineEndings("\n"), result.ErrorMessage, "dotted paths and the received arguments");

        var root = ToolArgumentValidation.Validate("read", JsonNode.Parse(Read), JsonNode.Parse("[]"));
        Equal("Validation failed for tool \"read\":\n  - root: expected object, got array\n\nReceived arguments:\n[]", root.ErrorMessage, "the root path");

        var escaped = ToolArgumentValidation.Validate("t", JsonNode.Parse("""{"type":"object","properties":{"a/b~c":{"type":"integer"}}}"""), JsonNode.Parse("""{"a/b~c":"x"}"""));
        Equal("Validation failed for tool \"t\":\n  - a/b~c: expected integer, got string\n\nReceived arguments:\n{\n  \"a/b~c\": \"x\"\n}", escaped.ErrorMessage, "pointer segments unescaped");

        var valid = ToolArgumentValidation.ValidateJson("read", Read, """{"path":"a.ts","offset":"2"}""", ToolSchemaOrigin.JsonSchema);
        Check(valid.IsValid, "valid arguments");
        Equal("""{"path":"a.ts","offset":2}""", valid.ArgumentsJson, "coerced arguments");
    }

    // v1.5.0 validates every schema the same way however it was built: the origin no longer selects a pipeline.
    private static void ToolArgumentsEveryOrigin()
    {
        const string schema = """{"type":"object","properties":{"count":{"type":"number"},"tags":{"type":"array","items":{"type":"string"}},"note":{"type":"string"}},"required":["count"]}""";
        const string args = """{"count":"42","tags":"a","note":null}""";
        foreach (var origin in Enum.GetValues<ToolSchemaOrigin>())
        {
            var result = ToolArgumentValidation.ValidateJson("t", schema, args, origin);
            Equal("""{"count":42,"tags":["a"]}""", result.ArgumentsJson, "coerced for " + origin);
        }
    }

    // An MCP server's schema with a dangling definition: calls that do not reach the reference work.
    private static void ToolArgumentsDanglingRef()
    {
        const string schema = """{"type":"object","properties":{"n":{"type":"number"},"t":{"$ref":"#/definitions/Missing"}}}""";
        var ok = ToolArgumentValidation.ValidateJson("mcp__srv__tool", schema, """{"n":"1"}""", ToolSchemaOrigin.JsonSchema);
        Equal("""{"n":1}""", ok.ArgumentsJson, "a call that does not reach the reference");
        var failed = ToolArgumentValidation.ValidateJson("mcp__srv__tool", schema, """{"t":"x"}""", ToolSchemaOrigin.JsonSchema);
        Equal("Validation failed for tool \"mcp__srv__tool\":\n  - t: unresolved reference #/definitions/Missing\n\nReceived arguments:\n{\n  \"t\": \"x\"\n}",
            failed.ErrorMessage, "a value at the reference");
    }

    private static void ToolArgumentsUncompilableSchema()
    {
        var malformed = ToolArgumentValidation.ValidateJson("t", """{"type":"object","properties":{"a":1}}""", "{}", ToolSchemaOrigin.JsonSchema);
        Equal("A schema must be an object or a boolean, got 1", malformed.ErrorMessage, "a malformed schema fails every call");
        var pattern = ToolArgumentValidation.ValidateJson("t", """{"type":"object","properties":{"a":{"type":"string","pattern":"("}}}""", "{}", ToolSchemaOrigin.JsonSchema);
        Equal("Invalid regular expression: /(/: Unterminated group", pattern.ErrorMessage, "an invalid pattern fails every call");
    }
}
