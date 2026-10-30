using System.Text.Json.Nodes;

namespace PiSharp.Samples;

/// <summary>Validates the deliberately small JSON Schema subset used by these authored sample tools.</summary>
public static class SchemaValidation
{
    public static string? Validate(JsonObject schema, JsonNode? value, string path = "$", int depth = 0)
    {
        if (depth > 24) return path + ": schema nesting limit exceeded";
        if (schema["enum"] is JsonArray choices && !choices.Any(x => JsonNode.DeepEquals(x, value))) return path + ": value is not in enum";
        var type = schema["type"]?.GetValue<string>();
        var matches = type switch
        {
            null => true, "object" => value is JsonObject, "array" => value is JsonArray,
            "string" => value is JsonValue s && s.TryGetValue<string>(out _),
            "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
            "number" => Number(value) is not null,
            "integer" => Number(value) is decimal d && d == decimal.Truncate(d), "null" => value is null,
            _ => false
        };
        if (!matches) return $"{path}: expected {type}";
        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (var name in required) if (!obj.ContainsKey(name!.GetValue<string>())) return $"{path}: missing {name}";
            var properties = schema["properties"] as JsonObject;
            foreach (var (key, item) in obj)
            {
                if (properties?[key] is JsonObject child) { var error = Validate(child, item, path + "." + key, depth + 1); if (error is not null) return error; }
                else if (schema["additionalProperties"] is JsonValue a && a.TryGetValue<bool>(out var allowed) && !allowed) return path + ": unexpected " + key;
            }
        }
        if (value is JsonArray array)
        {
            if (schema["minItems"] is JsonValue min && array.Count < min.GetValue<int>()) return path + ": too few items";
            if (schema["maxItems"] is JsonValue max && array.Count > max.GetValue<int>()) return path + ": too many items";
            if (schema["items"] is JsonObject itemSchema) foreach (var item in array) { var error = Validate(itemSchema, item, path + "[]", depth + 1); if (error is not null) return error; }
        }
        if (Number(value) is decimal number)
        {
            if (Number(schema["minimum"]) is decimal min && number < min) return path + ": below minimum";
            if (Number(schema["maximum"]) is decimal max && number > max) return path + ": above maximum";
        }
        return null;
    }
    private static decimal? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;
}
