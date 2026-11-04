// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/json.ts (isJsonValue).
//
// JSON as Chord v2 reads it, on System.Text.Json.Nodes: JSON data is what json.ts's isJsonValue accepts (finite numbers, strings,
// booleans, null, arrays and objects of them; a JsonValue wrapping any other CLR value is not data), and the text is JavaScript's:
// JSON.stringify enumerates an object's array-index keys first, in ascending order, then its other keys in insertion order, writes
// numbers as Number::toString does and escapes only what JSON.stringify escapes. Fingerprints hash this text, so a contract declared
// here and the same contract declared in TypeScript hash alike. String() of a value is JavaScript's too (address keys). PiSharp.Durable
// ports json.ts as its immutable JsonValue; Chord v2 references no Durable project, so it reads JsonNode trees here instead.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.Chord;

/// <summary>JSON values as JavaScript reads and writes them (json.ts's <c>isJsonValue</c>, <c>JSON.stringify</c> and <c>String</c>).</summary>
internal static class ChordJson
{
    /// <summary><c>isJsonValue</c>: finite numbers, strings, booleans, <c>null</c>, and arrays and objects of them (JsonNode trees have
    /// no cycles, symbol keys or accessors to refuse).</summary>
    public static bool IsJsonValue(JsonNode? node) => node switch
    {
        null => true,
        JsonObject value => value.All(entry => IsJsonValue(entry.Value)),
        JsonArray value => value.All(IsJsonValue),
        JsonValue value => Primitive(value, out var primitive) && (primitive is not double number || double.IsFinite(number)),
        _ => false,
    };

    /// <summary>The value of a JSON primitive as JavaScript holds it: a string, a boolean or a double (<c>null</c> for an object or
    /// array held as a JsonElement). False for a JsonValue wrapping a CLR value that is no JSON primitive.</summary>
    public static bool Primitive(JsonValue value, out object? primitive)
    {
        primitive = null;
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: primitive = JsonUtf16.GetString(element); return true;
                case JsonValueKind.True: primitive = true; return true;
                case JsonValueKind.False: primitive = false; return true;
                case JsonValueKind.Number: primitive = JsonNumber.Read(element); return true;
                case JsonValueKind.Null: return true;
                default: return false;
            }
        }
        if (value.TryGetValue<string>(out var text)) { primitive = text; return true; }
        if (value.TryGetValue<bool>(out var flag)) { primitive = flag; return true; }
        if (value.TryGetValue<double>(out var number)) { primitive = number; return true; }
        if (value.TryGetValue<float>(out var single)) { primitive = double.Parse(single.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture); return true; }
        if (value.TryGetValue<decimal>(out var exact)) { primitive = (double)exact; return true; }
        if (value.TryGetValue<long>(out var wide)) { primitive = (double)wide; return true; }
        if (value.TryGetValue<int>(out var integer)) { primitive = (double)integer; return true; }
        if (value.TryGetValue<short>(out var small)) { primitive = (double)small; return true; }
        if (value.TryGetValue<byte>(out var octet)) { primitive = (double)octet; return true; }
        if (value.TryGetValue<ulong>(out var unsignedWide)) { primitive = (double)unsignedWide; return true; }
        if (value.TryGetValue<uint>(out var unsigned)) { primitive = (double)unsigned; return true; }
        if (value.TryGetValue<ushort>(out var unsignedSmall)) { primitive = (double)unsignedSmall; return true; }
        if (value.TryGetValue<sbyte>(out var signedByte)) { primitive = (double)signedByte; return true; }
        return false;
    }

    /// <summary>An object held as a JsonObject, or as a JsonValue wrapping a JsonElement object; otherwise null.</summary>
    public static JsonObject? AsObject(JsonNode? node) => node switch
    {
        JsonObject value => value,
        JsonValue value when value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Object =>
            JsonUtf16.MutableNode(element) as JsonObject,
        _ => null,
    };

    /// <summary>An array held as a JsonArray, or as a JsonValue wrapping a JsonElement array; otherwise null.</summary>
    public static JsonArray? AsArray(JsonNode? node) => node switch
    {
        JsonArray value => value,
        JsonValue value when value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Array =>
            JsonUtf16.MutableNode(element) as JsonArray,
        _ => null,
    };

    /// <summary>The string a JSON string node holds; otherwise null.</summary>
    public static string? AsString(JsonNode? node) => node is JsonValue value && Primitive(value, out var primitive) ? primitive as string : null;

    /// <summary>The number a JSON number node holds; otherwise null.</summary>
    public static double? AsNumber(JsonNode? node) => node is JsonValue value && Primitive(value, out var primitive) && primitive is double number ? number : null;

    /// <summary>Whether <paramref name="node"/> is the JSON boolean <paramref name="expected"/>.</summary>
    public static bool IsBoolean(JsonNode? node, bool expected) => node is JsonValue value && Primitive(value, out var primitive) && primitive is bool flag && flag == expected;

    /// <summary>JavaScript's <c>a &lt; b</c> on strings: UTF-16 code unit order.</summary>
    public static int Compare(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));

    /// <summary>The keys of an object in JavaScript's own-key order: array indexes ascending, then the rest in insertion order.</summary>
    public static IEnumerable<string> KeysInJavaScriptOrder(JsonObject value)
    {
        var indexes = new List<(uint Index, string Key)>();
        var others = new List<string>();
        foreach (var (key, _) in value)
        {
            if (IsArrayIndex(key, out var index)) indexes.Add((index, key));
            else others.Add(key);
        }
        indexes.Sort((a, b) => a.Index.CompareTo(b.Index));
        return indexes.Select(entry => entry.Key).Concat(others);
    }

    /// <summary>An array index as a property key: the canonical decimal form of an integer from 0 to 2^32 - 2.</summary>
    private static bool IsArrayIndex(string key, out uint index) =>
        uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index != uint.MaxValue &&
        index.ToString(CultureInfo.InvariantCulture) == key;

    /// <summary><c>JSON.stringify(value)</c> of a JSON value.</summary>
    public static string Stringify(JsonNode? node)
    {
        var text = new StringBuilder();
        Stringify(text, node);
        return text.ToString();
    }

    /// <summary>Appends <c>JSON.stringify(value)</c> of a JSON value.</summary>
    public static void Stringify(StringBuilder text, JsonNode? node)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                return;
            case JsonObject value:
            {
                text.Append('{');
                var first = true;
                foreach (var key in KeysInJavaScriptOrder(value))
                {
                    if (!first) text.Append(',');
                    first = false;
                    JsonUtf16.Quote(text, key);
                    text.Append(':');
                    Stringify(text, value[key]);
                }
                text.Append('}');
                return;
            }
            case JsonArray value:
            {
                text.Append('[');
                for (var index = 0; index < value.Count; index++)
                {
                    if (index > 0) text.Append(',');
                    Stringify(text, value[index]);
                }
                text.Append(']');
                return;
            }
            case JsonValue value when AsObject(value) is { } inner:
                Stringify(text, inner);
                return;
            case JsonValue value when AsArray(value) is { } inner:
                Stringify(text, inner);
                return;
            case JsonValue value:
                if (!Primitive(value, out var primitive)) throw new ArgumentException("Not JSON data: " + value.ToJsonString());
                switch (primitive)
                {
                    case null: text.Append("null"); break;
                    case string inner: JsonUtf16.Quote(text, inner); break;
                    case bool inner: text.Append(inner ? "true" : "false"); break;
                    case double inner: text.Append(JsonNumber.Text(inner)); break;
                }
                return;
        }
    }

    /// <summary>JavaScript's <c>String(value)</c> of a JSON value: a string itself, a number's Number::toString, <c>"null"</c>, an array's
    /// items joined with commas (null items empty), an object <c>"[object Object]"</c>.</summary>
    public static string ToJavaScriptString(JsonNode? node)
    {
        if (node is null) return "null";
        if (AsObject(node) is not null) return "[object Object]";
        if (AsArray(node) is { } array) return string.Join(",", array.Select(item => item is null ? "" : ToJavaScriptString(item)));
        if (node is not JsonValue value || !Primitive(value, out var primitive)) return node.ToJsonString();
        return primitive switch
        {
            null => "null",
            string text => text,
            bool flag => flag ? "true" : "false",
            double number => double.IsNaN(number) ? "NaN" : double.IsPositiveInfinity(number) ? "Infinity" :
                double.IsNegativeInfinity(number) ? "-Infinity" : JsonNumber.Text(number),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>A copy of a JSON value with every object's keys sorted (UTF-16 order) and every primitive held as JavaScript holds it.</summary>
    public static JsonNode? SortedCopy(JsonNode? node)
    {
        if (node is null) return null;
        if (AsObject(node) is { } value)
        {
            var copy = new JsonObject();
            foreach (var key in value.Select(entry => entry.Key).Order(StringComparer.Ordinal)) copy[key] = SortedCopy(value[key]);
            return copy;
        }
        if (AsArray(node) is { } array) return new JsonArray([.. array.Select(SortedCopy)]);
        return CopyPrimitive((JsonValue)node);
    }

    /// <summary>A fresh node for a JSON primitive: a string, a boolean, a double or <c>null</c>.</summary>
    public static JsonNode? CopyPrimitive(JsonValue value)
    {
        if (!Primitive(value, out var primitive)) throw new ArgumentException("Not JSON data: " + value.ToJsonString());
        return primitive switch
        {
            null => null,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            double number => JsonValue.Create(number),
            _ => throw new ArgumentException("Not JSON data: " + value.ToJsonString()),
        };
    }

    /// <summary><c>JSON.stringify</c> of a declaration's name in messages: a quoted string, or <c>null</c>.</summary>
    public static string Quote(string? text) => text is null ? "null" : JsonUtf16.Quote(text);
}
