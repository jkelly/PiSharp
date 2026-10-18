// PiSharp-only (decision 0008), after durable-pi's tool signatures (apps/dpi-playground/apps/dpi-playground-fe/src/client/signature.ts, MIT):
// a tool's JSON Schema shown as the TypeScript declaration a model reads, with each property's description as a doc comment.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Web.Hub;

/// <summary>TypeScript-like signatures of tools, as HTML.</summary>
public static class Signatures
{
    /// <summary><c>function name(args: {...}): Promise&lt;ToolResult&gt;;</c> with highlighting spans.</summary>
    public static string Render(string name, JsonNode? schema)
    {
        var html = new StringBuilder();
        html.Append("<span class=\"sig-k\">function</span> <span class=\"sig-f\">").Append(E(name)).Append("</span>(args: ");
        Type(html, schema as JsonObject, 1);
        html.Append("): <span class=\"sig-t\">Promise</span>&lt;<span class=\"sig-t\">ToolResult</span>&gt;;");
        return html.ToString();
    }

    private static void Type(StringBuilder html, JsonObject? schema, int depth)
    {
        if (schema is null)
        {
            html.Append("<span class=\"sig-t\">unknown</span>");
            return;
        }
        if (schema["enum"] is JsonArray values)
        {
            html.Append(string.Join(" | ", values.Select(value => "<span class=\"sig-t\">" + E(value?.ToJsonString() ?? "null") + "</span>")));
            return;
        }
        if ((schema["anyOf"] as JsonArray ?? schema["oneOf"] as JsonArray) is { } union)
        {
            var first = true;
            foreach (var option in union.OfType<JsonObject>())
            {
                if (!first) html.Append(" | ");
                Type(html, option, depth);
                first = false;
            }
            return;
        }
        var type = schema["type"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonArray types => types.Select(item => item?.ToString()).FirstOrDefault(item => item != "null"),
            _ => schema["properties"] is not null ? "object" : null,
        };
        switch (type)
        {
            case "object" when schema["properties"] is JsonObject properties && properties.Count > 0:
            {
                var required = (schema["required"] as JsonArray)?.Select(item => item?.ToString()).ToHashSet() ?? [];
                var indent = new string(' ', depth * 2);
                html.Append("{\n");
                foreach (var (key, value) in properties)
                {
                    var property = value as JsonObject;
                    var notes = new List<string>();
                    if (Display.Str(property, "description") is { Length: > 0 } description) notes.Add(description);
                    if (property?["minimum"] is JsonNode minimum) notes.Add("@minimum " + minimum.ToJsonString());
                    if (property?["maximum"] is JsonNode maximum) notes.Add("@maximum " + maximum.ToJsonString());
                    if (property?["default"] is JsonNode defaultValue) notes.Add("@default " + defaultValue.ToJsonString());
                    if (notes.Count > 0) html.Append(indent).Append("<span class=\"sig-c\">/** ").Append(E(string.Join(" ", notes).ReplaceLineEndings(" "))).Append(" */</span>\n");
                    html.Append(indent).Append("<span class=\"sig-p\">").Append(E(key)).Append("</span>").Append(required.Contains(key) ? ": " : "?: ");
                    Type(html, property, depth + 1);
                    html.Append(";\n");
                }
                html.Append(new string(' ', (depth - 1) * 2)).Append('}');
                return;
            }
            case "object":
                html.Append("<span class=\"sig-t\">Record</span>&lt;<span class=\"sig-t\">string</span>, <span class=\"sig-t\">unknown</span>&gt;");
                return;
            case "array":
                Type(html, schema["items"] as JsonObject, depth);
                html.Append("[]");
                return;
            case "integer":
                html.Append("<span class=\"sig-t\">number</span>");
                return;
            case null:
                html.Append("<span class=\"sig-t\">unknown</span>");
                return;
            default:
                html.Append("<span class=\"sig-t\">").Append(E(type)).Append("</span>");
                return;
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
