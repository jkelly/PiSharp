// Decision 0012: JSON Schemas for typed tools and typed prompt results, from System.Text.Json's schema exporter (in the BCL). Numbers are
// exported strictly (the Web defaults also read numbers from strings, which would put string types in front of the model); [Description]
// attributes become descriptions; a root that is not an object is wrapped as {value}, since tool parameters must be objects; recursive
// types are refused, as the durable validator does not follow self-references.
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JsonValue = PiSharp.Durable.Chord.JsonValue;
using JsonObjectValue = PiSharp.Durable.Chord.JsonObjectValue;

namespace PiSharp.CustomAgents.Internal;

/// <summary>A type's exported schema.</summary>
/// <param name="Schema">The object schema.</param>
/// <param name="Wrapped">Whether the type's own schema sits under the property <c>value</c>.</param>
internal sealed record TypeSchema(JsonObjectValue Schema, bool Wrapped);

internal static class Schemas
{
    public static TypeSchema Export(Type type, JsonSerializerOptions options)
    {
        var strict = new JsonSerializerOptions(options) { NumberHandling = JsonNumberHandling.Strict };
        strict.MakeReadOnly(populateMissingResolver: true);
        return Shape(type, strict.GetJsonSchemaAsNode(type, ExporterOptions));
    }

    /// <summary>The schema of a source-generated type; its options' number handling is kept.</summary>
    public static TypeSchema Export(JsonTypeInfo typeInfo) => Shape(typeInfo.Type, typeInfo.GetJsonSchemaAsNode(ExporterOptions));

    private static JsonSchemaExporterOptions ExporterOptions { get; } = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (context, schema) =>
        {
            var provider = context.PropertyInfo?.AttributeProvider ?? (context.PropertyInfo is null ? context.TypeInfo.Type : null);
            if (provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true).OfType<DescriptionAttribute>().FirstOrDefault()
                is not { } description) return schema;
            if (schema is JsonObject value)
            {
                value["description"] = description.Description;
                return value;
            }
            return new JsonObject { ["description"] = description.Description };
        },
    };

    private static TypeSchema Shape(Type type, JsonNode node)
    {
        if (ContainsRef(node)) throw new ArgumentException($"{type.Name} is recursive; typed tools and results need a non-recursive type.", nameof(type));
        if (node is JsonObject root && IsObjectType(root))
        {
            root.Remove("$schema");
            return new TypeSchema((JsonObjectValue)JsonValue.Parse(root.ToJsonString()), false);
        }
        var wrapped = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = node?.DeepClone() ?? new JsonObject() },
            ["required"] = new JsonArray("value"),
        };
        return new TypeSchema((JsonObjectValue)JsonValue.Parse(wrapped.ToJsonString()), true);
    }

    private static bool IsObjectType(JsonObject schema) => schema["type"] switch
    {
        System.Text.Json.Nodes.JsonValue text when text.TryGetValue<string>(out var name) => name == "object",
        JsonArray types => types.Count == 1 && types[0] is System.Text.Json.Nodes.JsonValue only && only.TryGetValue<string>(out var single) &&
            single == "object",
        _ => false,
    };

    private static bool ContainsRef(JsonNode? node) => node switch
    {
        JsonObject value => value.Any(pair => pair.Key == "$ref" || ContainsRef(pair.Value)),
        JsonArray list => list.Any(ContainsRef),
        _ => false,
    };
}
