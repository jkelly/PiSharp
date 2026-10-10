using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Pi;

/// <summary>JavaScript <c>JSON.stringify</c> text for JSON nodes: only <c>"</c>, <c>\</c>, control characters and lone surrogates are
/// escaped, so files Pi writes and files PiSharp writes have the same bytes. <paramref name="indent"/> is <c>JSON.stringify(x, null, 2)</c>.</summary>
internal static class PiJson
{
    /// <summary><c>JSON.parse</c>: a malformed text throws a <see cref="JsonException"/> carrying V8's SyntaxError message, the
    /// text Pi interpolates into its "Failed to ..." and "Invalid settings file ..." errors.</summary>
    internal static JsonNode? Parse(string text, int maximumDepth = 256)
    {
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = maximumDepth }); }
        catch (JsonException error) { throw new JsonException(PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(text, error.Message), error); }
    }

    internal static string Stringify(JsonNode? node, bool indent = false)
    {
        var builder = new StringBuilder();
        Write(builder, node, indent, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, bool indent, int depth)
    {
        switch (node)
        {
            case null: builder.Append("null"); return;
            case JsonObject value:
                if (value.Count == 0) { builder.Append("{}"); return; }
                builder.Append('{');
                var first = true;
                foreach (var (key, child) in value)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    if (indent) builder.Append('\n').Append(' ', (depth + 1) * 2);
                    Quote(builder, key); builder.Append(indent ? ": " : ":");
                    Write(builder, child, indent, depth + 1);
                }
                if (indent) builder.Append('\n').Append(' ', depth * 2);
                builder.Append('}');
                return;
            case JsonArray array:
                if (array.Count == 0) { builder.Append("[]"); return; }
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    if (indent) builder.Append('\n').Append(' ', (depth + 1) * 2);
                    Write(builder, array[index], indent, depth + 1);
                }
                if (indent) builder.Append('\n').Append(' ', depth * 2);
                builder.Append(']');
                return;
            case JsonValue scalar:
                switch (scalar.GetValueKind())
                {
                    case JsonValueKind.String: Quote(builder, scalar.GetValue<string>()); return;
                    case JsonValueKind.True: builder.Append("true"); return;
                    case JsonValueKind.False: builder.Append("false"); return;
                    case JsonValueKind.Null: builder.Append("null"); return;
                    case JsonValueKind.Number:
                        using (var document = JsonDocument.Parse(scalar.ToJsonString())) builder.Append(Number(document.RootElement));
                        return;
                }
                break;
        }
        throw new ArgumentException("Unsupported JSON node.", nameof(node));
    }

    /// <summary>JavaScript number text for a parsed JSON number (integers stay integral, others round-trip as doubles).</summary>
    private static string Number(JsonElement element)
    {
        if (element.TryGetInt64(out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
        var value = element.GetDouble();
        if (!double.IsFinite(value)) return "null";
        if (value == Math.Floor(value) && Math.Abs(value) < 1e21) return value.ToString("F0", CultureInfo.InvariantCulture);
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Replace("E+", "e+").Replace("E-", "e-");
    }

    internal static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { builder.Append(c).Append(text[++index]); }
                    else if (char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    internal static string Quote(string text) { var builder = new StringBuilder(); Quote(builder, text); return builder.ToString(); }
}
