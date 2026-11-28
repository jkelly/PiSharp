// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/mcp/src/result-schema.ts.
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Runtime.Mcp.Resources;

public static class McpResultSchema
{
    /// <summary>tools.ts CONTENT_BLOCK_SCHEMA: MCP content blocks as one compact type with the fields of text and image blocks, which
    /// scripts read most (<c>image(block)</c> takes an image block as is). Rendered into every MCP tool's declaration.</summary>
    private const string ContentBlockSchema =
        """{"type":"object","properties":{"type":{"enum":["text","image","audio","resource_link","resource"]},"text":{"type":"string"},"data":{"type":"string"},"mimeType":{"type":"string"}},"required":["type"],"additionalProperties":true}""";
    /// <summary>Keywords whose values are data, so a <c>$ref</c> key in them is not a reference.</summary>
    private static readonly HashSet<string> SchemaDataKeywords = new(StringComparer.Ordinal) { "const", "enum", "default", "examples" };
    /// <summary>Keywords whose values map names to schemas, so their keys are names, not keywords.</summary>
    private static readonly HashSet<string> SchemaMapKeywords = new(StringComparer.Ordinal)
        { "properties", "patternProperties", "dependentSchemas", "dependencies", "$defs", "definitions" };

    /// <summary>createMcpResultSchema: the output schema of every MCP tool, the <c>CallToolResult</c> scripts receive (without
    /// <c>_meta</c>, which callers drop), with the tool's own output schema as <c>structuredContent</c>, relocated so
    /// its local references still resolve. Servers may send structured content without declaring a schema for it, and it may be any
    /// JSON value, so without one it is <c>{}</c> (any value).</summary>
    public static JsonData Create(JsonElement? structuredContentSchema)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteStartObject("content"); writer.WriteString("type", "array");
            writer.WritePropertyName("items"); using var contentSchema = JsonDocument.Parse(ContentBlockSchema); contentSchema.RootElement.WriteTo(writer); writer.WriteEndObject();
            writer.WritePropertyName("structuredContent");
            if (structuredContentSchema is { } schema && Truthy(schema)) Relocate(schema, "#/properties/structuredContent", writer);
            else { writer.WriteStartObject(); writer.WriteEndObject(); }
            writer.WriteStartObject("isError"); writer.WriteString("type", "boolean"); writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("required"); writer.WriteStringValue("content"); writer.WriteEndArray();
            // Fields newer protocol versions add.
            writer.WriteBoolean("additionalProperties", true);
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>JavaScript truthiness of a JSON value.</summary>
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.Number => value.TryGetDouble(out var number) ? number != 0 && !double.IsNaN(number) : true,
        JsonValueKind.String => value.GetString()!.Length != 0,
        _ => true
    };

    /// <summary>tools.ts relocateSchema: <paramref name="schema"/> as a subschema at the JSON pointer <paramref name="pointer"/> of
    /// another schema; its local references (<c>#</c>, <c>#/$defs/Item</c>) are rewritten to point below it, so they resolve to the
    /// same subschemas as before. Keys are written in JavaScript's own-property order.</summary>
    private static void Relocate(JsonElement schema, string pointer, Utf8JsonWriter writer)
    {
        switch (schema.ValueKind)
        {
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in schema.EnumerateArray()) Relocate(item, pointer, writer);
                writer.WriteEndArray();
                return;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var (key, value) in PiSharp.Contracts.ModelOperations.ModelOperationJson.ObjectEntries(schema))
                {
                    writer.WritePropertyName(key);
                    if (key == "$ref" && value.ValueKind == JsonValueKind.String && value.GetString() is var reference &&
                        (reference == "#" || reference!.StartsWith("#/", StringComparison.Ordinal)))
                        writer.WriteStringValue(pointer + reference[1..]);
                    else if (SchemaDataKeywords.Contains(key)) value.WriteTo(writer);
                    else if (SchemaMapKeywords.Contains(key) && value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WriteStartObject();
                        foreach (var (name, subschema) in PiSharp.Contracts.ModelOperations.ModelOperationJson.ObjectEntries(value))
                        { writer.WritePropertyName(name); Relocate(subschema, pointer, writer); }
                        writer.WriteEndObject();
                    }
                    else Relocate(value, pointer, writer);
                }
                writer.WriteEndObject();
                return;
            default:
                schema.WriteTo(writer);
                return;
        }
    }
}
