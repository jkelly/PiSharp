// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/scripts/generate-schemas.ts (the published schemas are the
// TypeBox schemas with repeated parts moved to $defs) and the TypeBox 1.3.27 Compile(schema).Check/Errors that model-config.ts,
// keybindings.ts and theme-schema.ts run.
using System.Text.Json.Nodes;

namespace PiSharp.Agent;

/// <summary>One TypeBox validation error: keyword, JSON-pointer instance path, English message and, for <c>required</c>, the missing
/// properties.</summary>
public sealed record TypeBoxSchemaError(string Keyword, string InstancePath, string Message, IReadOnlyList<string> RequiredProperties);

/// <summary>
/// A JSON schema checked with the TypeBox 1.3.27 engine the tools use: <c>Compile(schema).Check(value)</c> and its <c>Errors(value)</c>
/// (at most eight, in TypeBox order). Local <c>#/$defs/</c> references of a published schema are inlined first, so the schema is the
/// one the TypeBox source built (generate-schemas.ts only moved repeated parts into <c>$defs</c>).
/// </summary>
public sealed class TypeBoxJsonSchema
{
    private readonly object? schema;

    private TypeBoxJsonSchema(object? schema) => this.schema = schema;

    /// <summary>A published schema document: its root metadata (<c>$schema</c>, <c>$id</c>, <c>$comment</c>) is dropped and its
    /// <c>$defs</c> references are inlined, keeping each use's own annotations.</summary>
    public static TypeBoxJsonSchema FromPublished(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (JsonNode.Parse(json) is not JsonObject root) throw new ArgumentException("A published schema is a JSON object.", nameof(json));
        var definitions = root["$defs"] as JsonObject ?? [];
        foreach (var name in new[] { "$schema", "$id", "$comment", "$defs" }) root.Remove(name);
        return new(Js.FromNode(Inline(root, definitions, 0)));
    }

    private static JsonNode? Inline(JsonNode? node, JsonObject definitions, int depth)
    {
        if (depth > 64) throw new ArgumentException("Published schema references nest too deeply.");
        switch (node)
        {
            case JsonArray array:
                return new JsonArray([.. array.Select(item => Inline(item, definitions, depth))]);
            case JsonObject value when value["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target) &&
                target.StartsWith("#/$defs/", StringComparison.Ordinal):
            {
                var definition = definitions[target["#/$defs/".Length..]] as JsonObject ?? throw new ArgumentException("Unknown schema reference " + target);
                var merged = (JsonObject)Inline(definition, definitions, depth + 1)!;
                foreach (var (name, child) in value) if (name != "$ref") merged[name] = Inline(child, definitions, depth);
                return merged;
            }
            case JsonObject value:
            {
                var copy = new JsonObject();
                foreach (var (name, child) in value) copy[name] = Inline(child, definitions, depth);
                return copy;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Compile(schema).Check(JSON.parse(<paramref name="json"/>)).</summary>
    public bool CheckJson(string json) => Validator.Compile(schema).Check(Js.Parse(json));

    /// <summary>Compile(schema).Errors(JSON.parse(<paramref name="json"/>)); empty when the value is valid.</summary>
    public IReadOnlyList<TypeBoxSchemaError> ErrorsJson(string json) => Errors(Js.Parse(json));

    /// <summary>Compile(schema).Check for a JSON value.</summary>
    public bool Check(JsonNode? value) => Validator.Compile(schema).Check(Js.FromNode(value));

    /// <summary>Compile(schema).Errors for a JSON value; empty when the value is valid.</summary>
    public IReadOnlyList<TypeBoxSchemaError> Errors(JsonNode? value) => Errors(Js.FromNode(value));

    private IReadOnlyList<TypeBoxSchemaError> Errors(object? value) =>
        [.. Validator.Compile(schema).Errors(value).Select(error => new TypeBoxSchemaError(error.Keyword, error.InstancePath, error.Message,
            error.RequiredProperties is { } required ? [.. required] : []))];
}
