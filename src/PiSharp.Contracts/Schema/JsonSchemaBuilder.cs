// Native JSON/CLR counterparts of Pi 7d073328 packages/schema/src/builders.ts.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace PiSharp.Contracts.Schema;

/// <summary>Builders for recursive JSON schemas and explicit associations with CLR value types.</summary>
[Experimental("PISHARP_SCHEMA001")]
public static class JsonSchemaBuilder
{
    /// <summary>
    /// Builds a recursive schema. <paramref name="build"/> receives a local reference to the returned schema;
    /// use a deep clone of that reference when inserting it at multiple places in a JsonNode tree.
    /// The callback's result is cloned, and <paramref name="id"/> becomes its <c>$id</c>.
    /// The identifier must be unique among recursive resources in the containing schema.
    /// </summary>
    public static JsonObject Recursive(string id, Func<JsonObject, JsonObject> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(build);
        // A fragment-only self reference also handles identifiers with directory segments:
        // reusing "schemas/tree.json" as $ref inside that resource would apply its path twice.
        if (id.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException("A recursive resource identifier must not contain a fragment.", nameof(id));
        var result = build(new JsonObject { ["$ref"] = "#" })
            ?? throw new ArgumentException("The builder must return a schema object.", nameof(build));
        var schema = (JsonObject)result.DeepClone();
        schema["$id"] = id;
        return schema;
    }

    /// <summary>Builds a recursive schema explicitly associated with the serialized representation of <typeparamref name="T"/>.</summary>
    public static JsonSchemaDefinition<T> Recursive<T>(string id, Func<JsonObject, JsonObject> build) =>
        Unsafe<T>(Recursive(id, build));

    /// <summary>
    /// Associates raw JSON Schema with <typeparamref name="T"/> without proving they describe the same values.
    /// Validation subsequently checks the serialized CLR value; it never casts JSON to T or generates a CLR type.
    /// Omit <paramref name="schema"/> for an unconstrained schema. The supplied document is snapshotted.
    /// </summary>
    public static JsonSchemaDefinition<T> Unsafe<T>(JsonNode? schema = null) => new(schema ?? new JsonObject());
}

/// <summary>A schema explicitly associated with the JSON serialization of a CLR type.</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class JsonSchemaDefinition<T>
{
    private readonly JsonNode _schema;

    internal JsonSchemaDefinition(JsonNode schema) => _schema = schema.DeepClone();

    /// <summary>A copy of the schema, suitable for composing other schemas or publishing tool metadata.</summary>
    public JsonNode ToJsonSchema() => _schema.DeepClone();

    /// <summary>
    /// Compiles the schema. Values are serialized using <paramref name="serializerOptions"/> before validation;
    /// callers must choose options whose property naming and converters match the schema. Serialization errors propagate.
    /// Use the overload taking <see cref="JsonTypeInfo{T}"/> for trimming and NativeAOT.
    /// </summary>
    [RequiresDynamicCode("Serializing an arbitrary CLR type with serializer options can require runtime code generation. Use Compile(JsonTypeInfo<T>, options) for NativeAOT.")]
    [RequiresUnreferencedCode("Serializing an arbitrary CLR type with serializer options can require trimmed members. Use Compile(JsonTypeInfo<T>, options) with source-generated metadata.")]
    public TypedJsonSchemaValidator<T> Compile(JsonSchemaCompileOptions? options = null, JsonSerializerOptions? serializerOptions = null) =>
        CreateReflectionValidator(options, serializerOptions);

    [RequiresDynamicCode("Serializing an arbitrary CLR type can require runtime code generation.")]
    [RequiresUnreferencedCode("Serializing an arbitrary CLR type can require trimmed members.")]
    private TypedJsonSchemaValidator<T> CreateReflectionValidator(JsonSchemaCompileOptions? options, JsonSerializerOptions? serializerOptions)
    {
        var snapshot = serializerOptions is null ? null : new JsonSerializerOptions(serializerOptions);
        return new(JsonSchemaValidator.Compile(_schema, options), value => JsonSerializer.SerializeToNode(value, snapshot));
    }

    /// <summary>Compiles with explicit serialization metadata, suitable for source generation and NativeAOT.</summary>
    public TypedJsonSchemaValidator<T> Compile(JsonTypeInfo<T> typeInfo, JsonSchemaCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new(JsonSchemaValidator.Compile(_schema, options), value => JsonSerializer.SerializeToNode<T>(value!, typeInfo));
    }
}

/// <summary>Strictly validates a CLR value's JSON representation without changing or deserializing the value.</summary>
[Experimental("PISHARP_SCHEMA001")]
public sealed class TypedJsonSchemaValidator<T>
{
    private readonly JsonSchemaValidator _validator;
    private readonly Func<T?, JsonNode?> _serialize;

    internal TypedJsonSchemaValidator(JsonSchemaValidator validator, Func<T?, JsonNode?> serialize)
    {
        _validator = validator;
        _serialize = serialize;
    }

    /// <summary>Whether the serialized value satisfies the schema.</summary>
    public bool Check(T? value) => _validator.Check(_serialize(value));

    /// <summary>Failures in the serialized value, with paths in the JSON representation.</summary>
    public IReadOnlyList<JsonSchemaValidationError> Errors(T? value) => _validator.Errors(_serialize(value));

    /// <summary>Checks the serialized representation and returns the original CLR value on success. Never coerces.</summary>
    public JsonSchemaValidationResult<T> Validate(T? value)
    {
        var result = _validator.Validate(_serialize(value));
        return new(result.Ok, result.Ok ? value : default, result.Errors);
    }
}
