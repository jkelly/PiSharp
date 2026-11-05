// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/schema.ts
//
// Schema helpers: Chord's short constructors over pi-schema's builders (plain JSON Schema), and method and keyed member declarations. The
// helpers keep the shapes contracts were fingerprinted with: literals carry their `type`, `oneOf` is a union of literals, `map` a
// pattern-property record. A schema is a JsonNode; the helpers build the objects pi-schema's builders build (packages/schema/src/
// builders.ts: str, num, int, bool, nul, arr, union, obj, bytes, unknown and Optional, which PiSharp.Contracts.Schema does not port), with
// their keys in the builders' order. A helper copies the schemas it embeds: a JsonNode has one parent, where JavaScript shares the
// object, and declarations snapshot every schema anyway. Static and the schema interfaces are TypeScript types (see Index.cs).
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

/// <summary>The schema helpers (upstream <c>contracts/schema.ts</c>): plain JSON Schema, as pi-schema's builders build it.</summary>
public static class Schemas
{
    /// <summary>A string, with optional length bounds, pattern and default.</summary>
    public static JsonObject Str(double? min = null, double? max = null, string? pattern = null, string? @default = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        Limits(schema, min, max, "minLength", "maxLength");
        if (pattern is not null) schema["pattern"] = pattern;
        if (@default is not null) schema["default"] = @default;
        return schema;
    }

    /// <summary>A finite number within optional bounds, with an optional default.</summary>
    public static JsonObject Num(double? min = null, double? max = null, double? @default = null) => Number("number", min, max, @default);

    /// <summary>An integer within optional bounds, with an optional default.</summary>
    public static JsonObject Int(double? min = null, double? max = null, double? @default = null) => Number("integer", min, max, @default);

    /// <summary>A boolean, with an optional default.</summary>
    public static JsonObject Bool(bool? @default = null)
    {
        var schema = new JsonObject { ["type"] = "boolean" };
        if (@default is { } value) schema["default"] = value;
        return schema;
    }

    /// <summary><c>null</c>; also the omitted side of a method.</summary>
    public static JsonObject Nul() => new() { ["type"] = "null" };

    /// <summary>Exactly the given value, with its JSON type (<c>{ type: "string", const: "a" }</c>).</summary>
    public static JsonObject Lit(Literal value) => new() { ["type"] = value.Type, ["const"] = value.Node() };

    /// <summary>One of the given literals: a union of <see cref="Lit"/> schemas, in order.</summary>
    public static JsonObject OneOf(params Literal[] values) => new() { ["anyOf"] = List(values.Select(value => (JsonNode)Lit(value))) };

    /// <summary>Any of the given schemas.</summary>
    public static JsonObject Union(params JsonNode[] schemas) => new() { ["anyOf"] = List(schemas.Select(Embed)) };

    /// <summary>A closed object: unknown fields are refused; <see cref="Opt"/> marks an optional field.</summary>
    public static JsonObject Obj(params (string Name, Property Field)[] properties)
    {
        // pi-schema's obj(): properties (required unless opt()), `required` only when some are, and additionalProperties: false.
        var props = new JsonObject();
        var required = new List<string>();
        foreach (var (name, field) in properties)
        {
            required.Remove(name);
            if (field.Value is Optional optional) props[name] = Embed(optional.Schema);
            else
            {
                props[name] = Embed(field.Value as JsonNode);
                required.Add(name);
            }
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = List(required.Select(name => (JsonNode)name));
        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>An array, with optional length bounds.</summary>
    public static JsonObject Arr(JsonNode items, double? min = null, double? max = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = Embed(items) };
        Limits(schema, min, max, "minItems", "maxItems");
        return schema;
    }

    /// <summary>A string-keyed record: one pattern property matching every key.</summary>
    public static JsonObject Map(JsonNode value) => new() { ["type"] = "object", ["patternProperties"] = new JsonObject { ["^.*$"] = Embed(value) } };

    /// <summary>Any value.</summary>
    public static JsonObject Unknown() => new();

    /// <summary>A byte array leaf, checked by type and length, never enumerated: <c>{ type: "bytes", minLength?, maxLength? }</c>
    /// (pi-schema's extension of JSON Schema for values that never pass through JSON).</summary>
    public static JsonObject Bytes(double? min = null, double? max = null)
    {
        var schema = new JsonObject { ["type"] = "bytes" };
        Limits(schema, min, max, "minLength", "maxLength");
        return schema;
    }

    /// <summary>Marks an object field (or a table column) optional; it is no schema elsewhere.</summary>
    public static Optional Opt(JsonNode schema) => new(schema);

    /// <summary>Declares a keyed state member of a local contract: documents of <paramref name="schema"/> by string key, each watchable.</summary>
    public static KeyedMember Keyed(JsonNode schema) => new(schema);

    /// <summary>Declares a method with neither arguments nor result: both sides are <c>null</c>.</summary>
    public static MethodDeclaration Method() => new(Nul(), Nul());

    /// <summary>Declares a method taking <paramref name="args"/>, with a <c>null</c> result.</summary>
    public static MethodDeclaration Method(JsonNode args) => new(args, Nul());

    /// <summary>Declares a method: <c>Method(args, result)</c>, or <c>Method(null, result)</c> for a method without arguments.</summary>
    public static MethodDeclaration Method(JsonNode? args, JsonNode result) => new(args ?? Nul(), result);

    private static JsonObject Number(string type, double? min, double? max, double? @default)
    {
        var schema = new JsonObject { ["type"] = type };
        Limits(schema, min, max, "minimum", "maximum");
        if (@default is { } value) schema["default"] = value;
        return schema;
    }

    /// <summary>The given bounds under their JSON Schema names; an absent bound is left out.</summary>
    private static void Limits(JsonObject schema, double? min, double? max, string minName, string maxName)
    {
        if (min is { } low) schema[minName] = low;
        if (max is { } high) schema[maxName] = high;
    }

    /// <summary>A copy of a schema to embed in another (a JsonNode has one parent).</summary>
    private static JsonNode? Embed(JsonNode? schema) => schema?.DeepClone();

    private static JsonArray List(IEnumerable<JsonNode?> items) => new(items.ToArray());
}

/// <summary>A value <see cref="Schemas.Lit"/> and <see cref="Schemas.OneOf"/> take: a string, a number or a boolean.</summary>
public readonly struct Literal
{
    private readonly object _value;

    private Literal(object value) => _value = value;

    /// <summary>The value's JSON type, as JavaScript's <c>typeof</c> names it: <c>string</c>, <c>number</c> or <c>boolean</c>.</summary>
    public string Type => _value switch { string => "string", bool => "boolean", _ => "number" };

    /// <summary>A string literal.</summary>
    public static implicit operator Literal(string value) => new(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A number literal.</summary>
    public static implicit operator Literal(double value) => new(value);

    /// <summary>A boolean literal.</summary>
    public static implicit operator Literal(bool value) => new(value);

    internal JsonNode Node() => _value switch
    {
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        double number => JsonValue.Create(number),
        _ => throw new InvalidOperationException("An uninitialized literal."),
    };
}

/// <summary>A field of <see cref="Schemas.Obj"/>, or a params field: a schema, or an <see cref="Optional"/> (upstream
/// <c>Schema | Optional</c>; params refuse optional fields where they are declared).</summary>
public readonly struct Property
{
    private Property(object? value) => Value = value;

    internal object? Value { get; }

    /// <summary>A required field of this schema.</summary>
    public static implicit operator Property(JsonNode? schema) => new(schema);

    /// <summary>An optional field.</summary>
    public static implicit operator Property(Optional optional) => new(optional);
}

/// <summary>An optional field of <see cref="Schemas.Obj"/> or of a table (pi-schema's <c>Optional</c>, which <see cref="Schemas.Opt"/>
/// makes).</summary>
public sealed class Optional
{
    internal Optional(JsonNode schema) => Schema = schema;

    /// <summary>The field's schema.</summary>
    public JsonNode Schema { get; }
}

/// <summary>A method declaration: its argument and result schemas; an omitted side is <c>null</c> on the wire (upstream <c>Method</c>).</summary>
public sealed class MethodDeclaration
{
    internal MethodDeclaration(JsonNode args, JsonNode result)
    {
        Args = args;
        Result = result;
    }

    /// <summary>The argument schema.</summary>
    public JsonNode Args { get; }

    /// <summary>The result schema.</summary>
    public JsonNode Result { get; }
}

/// <summary>A keyed state member's declaration (slice 6a §12): documents of <see cref="Schema"/> by string key; only in local contracts.</summary>
public sealed class KeyedMember
{
    internal KeyedMember(JsonNode schema) => Schema = schema;

    /// <summary>The schema of each document.</summary>
    public JsonNode Schema { get; }
}

internal static partial class ContractsInternal
{
    /// <summary>Whether a declared object field is <c>opt(...)</c>.</summary>
    internal static bool IsOptional(object? value) => value is Optional;
}
