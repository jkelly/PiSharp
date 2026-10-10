using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PiSharp.Contracts;

/// <summary>
/// JSON strings as JavaScript holds them: any sequence of UTF-16 code units, a lone surrogate included. <c>JSON.parse</c> keeps an escaped
/// lone surrogate as that code unit and <c>JSON.stringify</c> writes it back as a lowercase <c>\uXXXX</c> escape. A <see cref="JsonData"/>
/// carries such a string as its escaped JSON text; System.Text.Json reads it only through these helpers (its own <c>GetString</c> and
/// <c>WriteTo</c> refuse it, and its writers turn a lone surrogate of a .NET string into U+FFFD).
/// </summary>
public static class JsonUtf16
{
    /// <summary>The string value of <paramref name="value"/> with every code unit, a lone surrogate included.</summary>
    public static string GetString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value.GetString()!; // throws as System.Text.Json does
        try { return value.GetString()!; }
        catch (InvalidOperationException) { return Unquote(value.GetRawText()); }
    }

    /// <summary>The name of <paramref name="property"/> with every code unit, a lone surrogate included.</summary>
    public static string GetName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException)
        {
            var raw = property.ToString();
            var end = 1;
            for (var escaped = false; end < raw.Length; end++)
            {
                if (escaped) escaped = false;
                else if (raw[end] == '\\') escaped = true;
                else if (raw[end] == '"') break;
            }
            return Unquote(raw[..(end + 1)]);
        }
    }

    /// <summary>Whether <paramref name="text"/> has no lone surrogate.</summary>
    public static bool IsWellFormed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { index++; continue; }
            return false;
        }
        return true;
    }

    /// <summary><c>String.prototype.toWellFormed</c>: every lone surrogate becomes U+FFFD, as Node's UTF-8 encoder writes it.</summary>
    public static string ToWellFormed(string text)
    {
        if (IsWellFormed(text)) return text;
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) builder.Append(character).Append(text[++index]);
            else builder.Append(char.IsSurrogate(character) ? '�' : character);
        }
        return builder.ToString();
    }

    /// <summary>Whether JSON text escapes a surrogate code unit (only a lone one cannot be read by System.Text.Json).</summary>
    public static bool HasEscapedSurrogate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        for (var index = json.IndexOf('\\'); index >= 0 && index + 3 < json.Length; index = json.IndexOf('\\', index + 2))
            if (json[index + 1] == 'u' && json[index + 2] is 'd' or 'D' && json[index + 3] is (>= '8' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
                return true;
        return false;
    }

    /// <summary><c>JSON.stringify</c> of a string: only <c>"</c>, <c>\</c>, control characters and lone surrogates are escaped.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 2);
        Quote(builder, text);
        return builder.ToString();
    }

    /// <summary>Appends <c>JSON.stringify(text)</c>.</summary>
    public static void Quote(StringBuilder builder, string text)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(text);
        builder.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                        builder.Append(character).Append(text[++index]);
                    else if (character < 0x20 || char.IsSurrogate(character))
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>Writes a string value; one with a lone surrogate is written as its <c>JSON.stringify</c> escape.</summary>
    public static void WriteStringValue(Utf8JsonWriter writer, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (IsWellFormed(text)) writer.WriteStringValue(text);
        else writer.WriteRawValue(Quote(text), skipInputValidation: true);
    }

    /// <summary>Writes a property name and string value, keeping a lone surrogate of the value.</summary>
    public static void WriteString(Utf8JsonWriter writer, string name, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(name);
        WriteStringValue(writer, text);
    }

    /// <summary><see cref="JsonElement.WriteTo"/>, which also writes a value whose strings hold a lone surrogate (as their escapes).</summary>
    public static void WriteTo(JsonElement value, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var raw = value.GetRawText();
        if (HasEscapedSurrogate(raw)) writer.WriteRawValue(raw, skipInputValidation: true);
        else value.WriteTo(writer);
    }

    /// <summary>A node for a string value: an ordinary string node, or for a string with a lone surrogate a node that writes its
    /// <c>JSON.stringify</c> escape. The latter is for writing only.</summary>
    public static JsonNode StringNode(string text) => IsWellFormed(text) ? JsonValue.Create(text) : Raw(Quote(text));

    /// <summary><c>JsonNode.Parse(json, documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)</c>, except that a value whose strings hold a lone surrogate becomes a node that writes the JSON
    /// text as it is. The latter is for writing only.</summary>
    public static JsonNode? Node(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HasEscapedSurrogate(json) ? Raw(json) : JsonNode.Parse(json, documentOptions: PiSharp.Contracts.JsonData.DocumentOptions);
    }

    /// <summary>A mutable tree of <paramref name="value"/> that also holds lone surrogates: a name keeps every code unit (written as its
    /// escape by <see cref="Write"/>), a string with a lone surrogate becomes a write-only <see cref="StringNode"/>, a number keeps its text.</summary>
    public static JsonNode? MutableNode(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                foreach (var property in value.EnumerateObject()) result[GetName(property)] = MutableNode(property.Value);
                return result;
            case JsonValueKind.Array: return new JsonArray([.. value.EnumerateArray().Select(MutableNode)]);
            case JsonValueKind.String: return StringNode(GetString(value));
            case JsonValueKind.Null: return null;
            case JsonValueKind.True or JsonValueKind.False: return JsonValue.Create(value.GetBoolean());
            default: return JsonValue.Create(value.Clone());
        }
    }

    /// <summary>A mutable tree of an owned value: <see cref="MutableNode(JsonElement)"/> when its text escapes a surrogate, otherwise
    /// <c>JsonNode.Parse</c>.</summary>
    public static JsonNode? MutableNode(JsonData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.ToString();
        return HasEscapedSurrogate(text) ? MutableNode(value.Value) : JsonNode.Parse(text, documentOptions: JsonData.DocumentOptions);
    }

    /// <summary><c>JsonNode.Parse</c> of JSON text at the depth an owned value holds; text that escapes a surrogate becomes a
    /// <see cref="MutableNode(JsonElement)"/> tree.</summary>
    public static JsonNode? MutableNode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!HasEscapedSurrogate(json)) return JsonNode.Parse(json, documentOptions: JsonData.DocumentOptions);
        using var document = JsonDocument.Parse(json, JsonData.DocumentOptions);
        return MutableNode(document.RootElement);
    }

    /// <summary><see cref="Node(string)"/> of a value.</summary>
    public static JsonNode? Node(JsonData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Node(value.ToString());
    }

    /// <summary><see cref="JsonNode.ToJsonString"/> (compact, with <paramref name="encoder"/>), which also writes a tree holding values parsed
    /// from JSON text with an escaped lone surrogate (such a value is written as that JSON text) and the write-only nodes above.</summary>
    public static string ToJsonString(JsonNode? node, System.Text.Encodings.Web.JavaScriptEncoder? encoder = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = encoder, SkipValidation = true, MaxDepth = 2 * JsonData.MaximumDepth })) Write(writer, node);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    /// <summary>Writes a node as <see cref="ToJsonString"/> does.</summary>
    public static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            // Utf8JsonWriter writes a lone surrogate of a name as U+FFFD: such an object is written as JSON.stringify text instead.
            case JsonObject obj when obj.Any(member => !IsWellFormed(member.Key)):
                var text = new StringBuilder("{");
                foreach (var (name, value) in obj)
                {
                    if (text.Length > 1) text.Append(',');
                    Quote(text, name); text.Append(':').Append(ToJsonString(value, writer.Options.Encoder));
                }
                writer.WriteRawValue(text.Append('}').ToString(), skipInputValidation: true);
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (name, value) in obj) { writer.WritePropertyName(name); Write(writer, value); }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValue value when value.TryGetValue<JsonElement>(out var element): WriteTo(element, writer); break;
            case JsonValue value when value.TryGetValue<RawJson>(out var raw): writer.WriteRawValue(raw.Text, skipInputValidation: true); break;
            default: node.WriteTo(writer); break;
        }
    }

    /// <summary><see cref="JsonElement.DeepEquals"/>, which also compares values whose strings hold a lone surrogate.</summary>
    public static bool DeepEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (!HasEscapedSurrogate(left.GetRawText()) && !HasEscapedSurrogate(right.GetRawText())) return JsonElement.DeepEquals(left, right);
        switch (left.ValueKind)
        {
            case JsonValueKind.String: return string.Equals(GetString(left), GetString(right), StringComparison.Ordinal);
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength()) return false;
                using (var first = left.EnumerateArray().GetEnumerator())
                using (var second = right.EnumerateArray().GetEnumerator())
                    while (first.MoveNext() && second.MoveNext()) if (!DeepEquals(first.Current, second.Current)) return false;
                return true;
            case JsonValueKind.Object:
                var others = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in right.EnumerateObject()) others[GetName(property)] = property.Value;
                var count = 0;
                foreach (var property in left.EnumerateObject())
                {
                    count++;
                    if (!others.TryGetValue(GetName(property), out var other) || !DeepEquals(property.Value, other)) return false;
                }
                return count == others.Count;
            default: return JsonElement.DeepEquals(left, right);
        }
    }

    /// <summary>The value of a JSON string literal, every escape decoded to its code unit.</summary>
    public static string Unquote(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        if (literal.Length < 2 || literal[0] != '"' || literal[^1] != '"') throw new JsonException("Expected a JSON string literal.");
        var builder = new StringBuilder(literal.Length);
        for (var index = 1; index < literal.Length - 1; index++)
        {
            var character = literal[index];
            if (character != '\\') { builder.Append(character); continue; }
            if (++index >= literal.Length - 1) throw new JsonException("Unterminated JSON escape.");
            switch (literal[index])
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (index + 4 >= literal.Length ||
                        !int.TryParse(literal.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        throw new JsonException("Invalid JSON unicode escape.");
                    builder.Append((char)code); index += 4; break;
                default: throw new JsonException("Invalid JSON escape.");
            }
        }
        return builder.ToString();
    }

    /// <summary>A resolver for serializer options passed to <see cref="JsonNode.ToJsonString(JsonSerializerOptions?)"/> on a tree that may
    /// hold the write-only nodes above (such options must resolve their type); any other type resolves by reflection.</summary>
    public static IJsonTypeInfoResolver Resolver { get; } = JsonTypeInfoResolver.Combine(new RawJsonResolver(), new DefaultJsonTypeInfoResolver());

    private static JsonNode Raw(string json) => JsonValue.Create(new RawJson(json), RawJsonInfo)!;

    private sealed class RawJsonResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            type == typeof(RawJson) ? JsonMetadataServices.CreateValueInfo<RawJson>(options, new RawJsonConverter()) : null;
    }

    private sealed record RawJson(string Text);
    private sealed class RawJsonConverter : JsonConverter<RawJson>
    {
        public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Raw JSON nodes are written only.");
        public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.Text, skipInputValidation: true);
    }
    private static readonly JsonTypeInfo<RawJson> RawJsonInfo =
        JsonMetadataServices.CreateValueInfo<RawJson>(new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() }, new RawJsonConverter());
}
