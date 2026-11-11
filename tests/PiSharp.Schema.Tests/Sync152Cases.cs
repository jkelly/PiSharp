using System.Numerics;
using System.Text.Json.Nodes;
using PiSharp.Contracts.Schema;

// Authored native equivalents of Pi b4afdddf44e059c67ccf5097e5317d913521b470 schema tests.
// JS-only values deliberately use the internal value model; public APIs continue accepting JSON.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync152Cases =>
    [
        ("sync152.finite-numbers", Sync(FiniteNumbers152)),
        ("sync152.required-undefined", Sync(RequiredUndefined152)),
        ("sync152.bigint-validation", Sync(BigintValidation152)),
        ("sync152.bigint-exact-bounds", Sync(BigintBounds152)),
        ("sync152.bigint-conversion", Sync(BigintConversion152)),
        ("sync152.undefined-conversion", Sync(UndefinedConversion152)),
        ("sync152.bigint-literals-and-unions", Sync(BigintLiterals152)),
        ("sync152.public-json-boundary", Sync(JsonBoundary152)),
    ];

    private static void FiniteNumbers152()
    {
        foreach (var schema in new[] { "{\"type\":\"number\"}", "{\"type\":\"integer\"}", "{\"type\":\"number\",\"minimum\":0}",
            "{\"type\":[\"number\",\"null\"]}", "{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}]}" })
        {
            var validator = Compile(schema);
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Check(!validator.Check(value), schema);
                Check(!Compile($$"""{"type":"object","properties":{"v":{{schema}}},"required":["v"]}""").Check(Obj152(("v", value))), "object " + schema);
                Check(!Compile($$"""{"type":"array","items":{{schema}}}""").Check(new JsArray([value])), "array " + schema);
            }
        }
        Equal(" type: expected number, got NaN", Errors(Compile("{\"type\":\"number\"}"), double.NaN), "NaN diagnostic");
        Equal(" type: expected number, got Infinity", Errors(Compile("{\"type\":\"number\"}"), double.PositiveInfinity), "Infinity diagnostic");
        Equal(" type: expected integer, got -Infinity", Errors(Compile("{\"type\":\"integer\"}"), double.NegativeInfinity), "-Infinity diagnostic");
    }

    private static JsObject Obj152(params (string Key, object? Value)[] entries)
    {
        var result = new JsObject();
        foreach (var (key, value) in entries) result.Set(key, value);
        return result;
    }

    private static SchemaValidator Raw152(JsObject schema) => SchemaValidator.Compile(schema, new SchemaOptions { Coerce = true });

    private static void RequiredUndefined152()
    {
        foreach (var property in new[] { "{\"type\":\"undefined\"}", "{}", "{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"undefined\"}]}" })
        foreach (var extra in new[] { "", ",\"additionalProperties\":false", ",\"patternProperties\":{\"^x\":{}}" })
        {
            var validator = Compile("""{"type":"object","properties":{"v": """ + property + """, "other":{"type":"integer"}},"required":["v"]""" + extra + "}");
            Check(validator.Check(Obj152(("v", Js.Undefined))), "present undefined " + property);
            Check(!validator.Check(new JsObject()), "missing stays missing");
            Equal("/v required: missing", Errors(validator, new JsObject()), "missing diagnostic");
            Equal("/other type: expected integer, got string", Errors(validator, Obj152(("v", Js.Undefined), ("other", "bad"))), "accepted undefined emits no missing error");
        }
        Check(Compile("{\"properties\":{\"v\":{\"type\":\"string\"}}}").Check(Obj152(("v", Js.Undefined))), "optional undefined absent");
        Check(!Compile("{\"properties\":{\"v\":{\"type\":\"string\"}},\"required\":[\"v\"]}").Check(Obj152(("v", Js.Undefined))), "required string rejects undefined");
        Check(Compile("{\"required\":[\"v\"]}").Check(Obj152(("v", Js.Undefined))), "undeclared required checks presence");
    }

    private static void BigintValidation152()
    {
        var validator = Compile("{\"type\":\"bigint\"}");
        Check(validator.Check(new BigInteger(2)), "bigint accepted");
        foreach (var value in new object?[] { 2d, "2", null, Js.Undefined, true }) Check(!validator.Check(value), "only bigint accepted");
        Equal(" type: expected bigint, got integer", Errors(validator, 2d), "bigint type diagnostic");
        Check(Compile("{\"type\":\"undefined\"}").Check(Js.Undefined), "undefined type");
        Check(!Compile("{\"type\":\"undefined\"}").Check(null), "undefined differs from null");
        foreach (var schema in new[] { "{\"type\":[\"bigint\",\"null\"]}", "{\"anyOf\":[{\"type\":\"bigint\"},{\"type\":\"null\"}]}" })
        {
            Check(Compile(schema).Check(BigInteger.One), "bigint union");
            Check(Compile(schema).Check(null), "null union");
        }
    }

    private static void BigintBounds152()
    {
        var huge = BigInteger.Pow(10, 100);
        var validator = Raw152(Obj152(("type", "bigint"), ("minimum", huge), ("exclusiveMaximum", huge + 10), ("multipleOf", new BigInteger(5))));
        Check(validator.Check(huge + 5), "exact bigint bounds");
        Check(!validator.Check(huge - 5), "minimum");
        Check(!validator.Check(huge + 10), "exclusiveMaximum");
        Check(!validator.Check(huge + 1), "multipleOf");
        Equal(" minimum: expected >= " + huge, Errors(validator, huge - 5), "bigint bound diagnostic");
        foreach (var divisor in new object[] { BigInteger.Zero, 0d, .5d, double.PositiveInfinity, double.NaN })
            Check(!Raw152(Obj152(("type", "bigint"), ("multipleOf", divisor))).Check(huge), "invalid bigint multipleOf never throws");
        Check(Compile("{\"type\":\"bigint\",\"minimum\":0.5,\"maximum\":1.5}").Check(BigInteger.One), "fractional double bounds");
        Check(!Compile("{\"type\":\"bigint\",\"minimum\":0.5}").Check(BigInteger.Zero), "fractional lower bound");
        Check(!Compile("{\"type\":\"bigint\",\"maximum\":-0.5}").Check(BigInteger.Zero), "negative fractional upper bound");
        Check(Raw152(Obj152(("type", "number"), ("minimum", BigInteger.One), ("maximum", new BigInteger(2)))).Check(1.5d), "double value bigint bounds");
        Check(!Raw152(Obj152(("type", "number"), ("multipleOf", new BigInteger(2)))).Check(1.5d), "fractional value bigint divisor");
    }

    private static void BigintConversion152()
    {
        var validator = Coercing("{\"type\":\"bigint\"}");
        foreach (var (value, expected) in new (object?, BigInteger)[] { (true, 1), (false, 0), (null, 0), (Js.Undefined, 0),
            (12.9d, 12), (-12.9d, -12), ("12n", 12), ("12.9", 12), ("-12.9", -12), ("TRUE", 1), ("false", 0) })
        {
            var parsed = validator.Parse(value);
            Check(parsed.Ok, "bigint parse " + value);
            Equal(expected, (BigInteger)parsed.Value!, "bigint converted value");
        }
        foreach (var value in new object[] { double.NaN, double.PositiveInfinity, "01", " 12 ", "0x10", "", new JsObject() })
            Check(!validator.Parse(value).Ok, "unsupported bigint conversion");
        var array = Coercing("{\"type\":\"array\",\"items\":{\"type\":\"bigint\"}}").Parse(new JsArray(["1n", "2n"]));
        Check(array.Ok && ((JsArray)array.Value!)[1] is BigInteger big && big == 2, "nested bigint conversion");
    }

    private static void UndefinedConversion152()
    {
        var validator = Coercing("{\"type\":\"undefined\"}");
        foreach (var value in new object?[] { Js.Undefined, null, BigInteger.Zero, false, 0d, "UNDEFINED", "NULL", "", "0" })
        {
            var result = validator.Parse(value);
            Check(result.Ok && result.Value is JsUndefined, "undefined conversion");
        }
        foreach (var value in new object[] { true, 1d, BigInteger.One, "false", "00", " null " }) Check(!validator.Parse(value).Ok, "unsupported undefined conversion");
        var obj = Obj152(("v", "undefined"));
        var parsed = Coercing("{\"properties\":{\"v\":{\"type\":\"undefined\"}},\"required\":[\"v\"]}").Parse(obj);
        Check(parsed.Ok && ((JsObject)parsed.Value!).HasOwn("v") && ((JsObject)parsed.Value!).GetOwn("v") is JsUndefined, "required converted undefined remains present");
        Equal("undefined", (string)obj.GetOwn("v")!, "input unmodified");
    }

    private static void BigintLiterals152()
    {
        var integer = BigInteger.Pow(10, 100) + 1;
        Check(Raw152(Obj152(("const", integer))).Parse(integer + "n").Ok, "bigint literal conversion and equality");
        Check(Raw152(Obj152(("enum", new JsArray([integer, BigInteger.One])))).Parse("1n").Ok, "bigint enum conversion");
        Check(SchemaChecks.DeepEqual(new JsArray([integer]), new JsArray([BigInteger.Parse(integer.ToString())])), "bigints equal by value");
        Check(!Compile("{\"uniqueItems\":true}").Check(new JsArray([integer, BigInteger.Parse(integer.ToString())])), "bigint uniqueness");
        Check(Coercing("{\"type\":\"number\"}").Parse(BigInteger.One).Ok, "safe bigint to number");
        Check(!Coercing("{\"type\":\"number\"}").Parse(integer).Ok, "unsafe bigint remains bigint");
        Equal("1", (string)Coercing("{\"type\":\"string\"}").Coerce(BigInteger.One)!, "bigint to string");
        Check(Coercing("{\"type\":\"boolean\"}").Parse(BigInteger.One).Ok, "bigint to boolean");
        Check(Coercing("{\"type\":\"null\"}").Parse(BigInteger.Zero).Ok, "bigint zero to null");
    }

    private static void JsonBoundary152()
    {
        foreach (var schema in new[] { "{\"type\":\"bigint\"}", "{\"type\":\"undefined\"}" })
        {
            var validator = (CoercingJsonSchemaValidator)JsonSchemaValidator.Compile(JsonNode.Parse(schema), new JsonSchemaCompileOptions { Coerce = true });
            Throws<JsonSchemaException>(() => validator.Parse(null), "non-JSON parse result is explicit");
            Throws<JsonSchemaException>(() => validator.Coerce(null), "non-JSON coerce result is explicit");
        }
        var nested = (CoercingJsonSchemaValidator)JsonSchemaValidator.Compile(JsonNode.Parse("{\"properties\":{\"v\":{\"type\":\"bigint\"}}}"), new JsonSchemaCompileOptions { Coerce = true });
        Throws<JsonSchemaException>(() => nested.Coerce(JsonNode.Parse("{\"v\":\"10\"}")), "nested bigint cannot be lost");
    }
}
