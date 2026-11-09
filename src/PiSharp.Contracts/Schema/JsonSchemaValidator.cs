// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/validator.ts, packages/schema/src/types.ts, packages/schema/src/index.ts,
// packages/schema/src/formats/index.ts
// Strict validation result and equality exports: Pi 7d073328e44337d3292a7db9b0f0308f679c5e97.
//
// JSON Schema validation (drafts 2020-12 and 7), ported from pi-schema: Compile builds a schema into plain closures once. Values are
// read as JavaScript reads JSON (numbers are doubles, object keys keep JavaScript's order), so verdicts, error messages and coerced
// values follow pi-schema's.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // the engine uses its own experimental types

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace PiSharp.Contracts.Schema;

/// <summary>The draft whose semantics a compile applies.</summary>
[Experimental("PISHARP_SCHEMA001")]
public enum JsonSchemaDraft
{
    /// <summary>Draft 2020-12 (the default).</summary>
    Draft202012,

    /// <summary>Draft 7: <c>$ref</c> overrides its siblings, <c>$id</c> beside <c>$ref</c> is inert, no <c>prefixItems</c> or <c>unevaluated*</c>.</summary>
    Draft7,
}

/// <summary>What a <c>$ref</c> that names nothing (a missing definition, a document not in the schemas) does.</summary>
[Experimental("PISHARP_SCHEMA001")]
public enum JsonSchemaUnresolvedRefs
{
    /// <summary>Compile throws <see cref="JsonSchemaException"/> (the default).</summary>
    Throw,

    /// <summary>The reference compiles as <c>false</c>, so only values at that place fail, with the error "unresolved reference".</summary>
    Reject,
}

/// <summary>Options of <see cref="JsonSchemaValidator.Compile"/> (pi-schema's CompileOptions).</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class JsonSchemaCompileOptions
{
    /// <summary>The draft whose semantics apply.</summary>
    public JsonSchemaDraft Draft { get; init; } = JsonSchemaDraft.Draft202012;

    /// <summary>Format checks by name. Without them <c>format</c> is an annotation, as draft 2020-12 specifies; <see cref="JsonSchemaFormats.Standard"/> has them all.</summary>
    public IReadOnlyDictionary<string, Func<string, bool>>? Formats { get; init; }

    /// <summary>Schema documents by URI, for <c>$ref</c>s to other documents.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Schemas { get; init; }

    /// <summary>Compile a <see cref="CoercingJsonSchemaValidator"/>, which also coerces loosely typed input.</summary>
    public bool Coerce { get; init; }

    /// <summary>What a <c>$ref</c> that names nothing does.</summary>
    public JsonSchemaUnresolvedRefs UnresolvedRefs { get; init; } = JsonSchemaUnresolvedRefs.Throw;
}

/// <summary>One failure of a value: where it is (a JSON Pointer, "" for the value itself), the failing keyword and a message.</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed record JsonSchemaValidationError(string Path, string Keyword, string Message);

/// <summary>A strict validation result: the original value on success, or its errors on failure. No coercion is performed.</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class JsonSchemaValidationResult<T>
{
    internal JsonSchemaValidationResult(bool ok, T? value, IReadOnlyList<JsonSchemaValidationError> errors)
    {
        Ok = ok;
        Value = value;
        Errors = errors;
    }

    /// <summary>Whether the value satisfies the schema. Check this before reading <see cref="Value"/>.</summary>
    public bool Ok { get; }

    /// <summary>The original value on success; <c>default</c> on failure. A successful JSON null also has a null value.</summary>
    public T? Value { get; }

    /// <summary>The validation failures; empty on success.</summary>
    public IReadOnlyList<JsonSchemaValidationError> Errors { get; }
}

/// <summary>The result of <see cref="CoercingJsonSchemaValidator.Parse"/>: the coerced value when it is valid, else the errors of the coerced value.</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class JsonSchemaParseResult
{
    internal JsonSchemaParseResult(bool ok, JsonNode? value, IReadOnlyList<JsonSchemaValidationError> errors)
    {
        Ok = ok;
        Value = value;
        Errors = errors;
    }

    /// <summary>Whether the coerced value is valid.</summary>
    public bool Ok { get; }

    /// <summary>The coerced value (also when it is invalid).</summary>
    public JsonNode? Value { get; }

    /// <summary>The errors of the coerced value; empty when it is valid.</summary>
    public IReadOnlyList<JsonSchemaValidationError> Errors { get; }
}

/// <summary>
/// A schema that cannot be compiled (malformed, a <c>$ref</c> that names nothing, an invalid pattern), or a check that recursed without
/// end ("Maximum call stack size exceeded", a schema that refers to itself in place).
/// </summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class JsonSchemaException(string message) : Exception(message);

/// <summary>The format checks of JSON Schema 2020-12 (pi-schema's <c>formats</c>, adapted there from TypeBox 1.3.27).</summary>
[Experimental("PISHARP_SCHEMA001")]
public static class JsonSchemaFormats
{
    /// <summary>
    /// date, date-time, duration, email, hostname, idn-email, idn-hostname, ipv4, ipv6, iri, iri-reference, json-pointer,
    /// json-pointer-uri-fragment, regex, relative-json-pointer, time, uri, uri-reference, uri-template, url and uuid.
    /// </summary>
    public static IReadOnlyDictionary<string, Func<string, bool>> Standard => SchemaFormats.All;
}

/// <summary>A compiled JSON Schema (pi-schema's Validator): <see cref="Check"/> is the boolean test, <see cref="Errors"/> explains failures.</summary>
[Experimental("PISHARP_SCHEMA001")]
public class JsonSchemaValidator
{
    private protected JsonSchemaValidator(SchemaValidator inner) => Inner = inner;

    internal SchemaValidator Inner { get; }

    /// <summary>
    /// Compiles <paramref name="schema"/> (a schema object, <c>true</c> or <c>false</c>). Throws <see cref="JsonSchemaException"/> if the
    /// schema is malformed or a <c>$ref</c> names nothing. With <see cref="JsonSchemaCompileOptions.Coerce"/> the result is a
    /// <see cref="CoercingJsonSchemaValidator"/>.
    /// </summary>
    public static JsonSchemaValidator Compile(JsonNode? schema, JsonSchemaCompileOptions? options = null)
    {
        options ??= new JsonSchemaCompileOptions();
        var inner = SchemaValidator.Compile(Js.FromNode(schema), new SchemaOptions
        {
            Draft7 = options.Draft == JsonSchemaDraft.Draft7,
            Formats = options.Formats,
            Schemas = options.Schemas?.Select(e => new KeyValuePair<string, object?>(e.Key, Js.FromNode(e.Value))).ToList() ?? [],
            Coerce = options.Coerce,
            RejectUnresolvedRefs = options.UnresolvedRefs == JsonSchemaUnresolvedRefs.Reject,
        });
        return options.Coerce ? new CoercingJsonSchemaValidator(inner) : new JsonSchemaValidator(inner);
    }

    /// <summary>Whether <paramref name="value"/> is valid.</summary>
    public bool Check(JsonNode? value) => Inner.Check(Js.FromNode(value));

    /// <summary>Why <paramref name="value"/> is invalid: one error per failing keyword, empty when it is valid.</summary>
    public IReadOnlyList<JsonSchemaValidationError> Errors(JsonNode? value) => Inner.Errors(Js.FromNode(value));

    /// <summary>
    /// Checks <paramref name="value"/> without coercion, cloning, inserting defaults or removing properties.
    /// A successful result contains the same node instance, including when this is a coercing validator.
    /// </summary>
    public JsonSchemaValidationResult<JsonNode> Validate(JsonNode? value)
    {
        var errors = Inner.Errors(Js.FromNode(value));
        return new(errors.Count == 0, errors.Count == 0 ? value : null, errors);
    }

    /// <summary>
    /// Compares JSON values structurally using the validator's value model: object key order is ignored,
    /// array order is significant, and numbers are compared as IEEE 754 doubles.
    /// </summary>
    public static bool Equal(JsonNode? left, JsonNode? right) => SchemaChecks.DeepEqual(Js.FromNode(left), Js.FromNode(right));
}

/// <summary>A validator compiled with <see cref="JsonSchemaCompileOptions.Coerce"/>: it also coerces loosely typed input towards the schema.</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class CoercingJsonSchemaValidator : JsonSchemaValidator
{
    internal CoercingJsonSchemaValidator(SchemaValidator inner) : base(inner)
    {
    }

    /// <summary>
    /// <paramref name="value"/> coerced towards the schema: numeric strings to numbers, "true" to true, a single value to a one-item array,
    /// an optional null dropped, the first union branch the value converts to. Values that cannot be coerced stay as they are.
    /// </summary>
    public JsonNode? Coerce(JsonNode? value) => Js.ToNode(Inner.Coerce(Js.FromNode(value)));

    /// <summary>Coerces <paramref name="value"/>, then checks it.</summary>
    public JsonSchemaParseResult Parse(JsonNode? value)
    {
        var (ok, coerced, errors) = Inner.Parse(Js.FromNode(value));
        return new JsonSchemaParseResult(ok, Js.ToNode(coerced), errors);
    }
}

/// <summary>A compiled schema on the JavaScript value model (what PiSharp.Agent's tool validation uses).</summary>
internal sealed class SchemaValidator
{
    private readonly SchemaCompiler _compiler;
    private readonly SchemaCheck _check;
    private readonly object? _schema;

    private SchemaValidator(SchemaCompiler compiler, object? schema)
    {
        _compiler = compiler;
        _schema = schema;
        _check = compiler.Root();
    }

    /// <summary>Compiles <paramref name="schema"/>; throws <see cref="JsonSchemaException"/> when it cannot be compiled.</summary>
    public static SchemaValidator Compile(object? schema, SchemaOptions options) =>
        Guard(() => new SchemaValidator(SchemaCompiler.Create(schema, options), schema));

    public bool Check(object? value) => Guard(() => _check(value));

    public List<JsonSchemaValidationError> Errors(object? value) =>
        Guard(() => _check(value) ? [] : new SchemaExplainer(_compiler, _schema).Errors(value));

    public object? Coerce(object? value) => Guard(() => new SchemaCoercer(_compiler, _schema).Coerce(value));

    /// <summary>The coerced value and whether it is valid, else its errors.</summary>
    public (bool Ok, object? Value, List<JsonSchemaValidationError> Errors) Parse(object? value)
    {
        var coerced = Coerce(value);
        return Check(coerced) ? (true, coerced, []) : (false, coerced, Errors(coerced));
    }

    // A check that recurses without end runs out of stack in JavaScript too: there it is a RangeError with this message.
    private static T Guard<T>(Func<T> run)
    {
        try
        {
            return run();
        }
        catch (InsufficientExecutionStackException)
        {
            throw new JsonSchemaException("Maximum call stack size exceeded");
        }
    }
}
