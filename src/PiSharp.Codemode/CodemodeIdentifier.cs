// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/identifier.ts.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PiSharp.Codemode;

public static class CodemodeIdentifier
{
    /// <summary>The identifier a script uses for a tool: characters that are not valid in a JavaScript identifier become
    /// <c>_</c>. <c>mcp__docs__search</c> stays as is, <c>my-tool</c> becomes <c>my_tool</c>. Iterates code points, as
    /// <c>for (const char of name)</c> does.</summary>
    public static string ToIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var identifier = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var valid = rune.IsAscii && (char.IsAsciiLetter((char)rune.Value) || rune.Value is '_' or '$' ||
                identifier.Length > 0 && char.IsAsciiDigit((char)rune.Value));
            identifier.Append(valid ? (char)rune.Value : '_');
        }
        return identifier.Length == 0 ? "_" : identifier.ToString();
    }

    internal static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] is '_' or '$') &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$');
}

/// <summary>JavaScript <c>JSON.stringify</c> of JSON values: compact, keys in order, numbers in JavaScript's shortest
/// form and only the characters JSON requires escaped.</summary>
internal static class Js
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Stringify(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
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
                    if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        return builder.Append('"').ToString();
    }

    public static string Stringify(JsonElement value)
    {
        var builder = new StringBuilder();
        Write(value, builder);
        return builder.ToString();
    }

    private static void Write(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{'); var first = true;
                foreach (var property in value.EnumerateObject())
                {
                    if (!first) builder.Append(',');
                    first = false; builder.Append(Stringify(property.Name)).Append(':'); Write(property.Value, builder);
                }
                builder.Append('}'); break;
            case JsonValueKind.Array:
                builder.Append('['); var index = 0;
                foreach (var item in value.EnumerateArray()) { if (index++ > 0) builder.Append(','); Write(item, builder); }
                builder.Append(']'); break;
            case JsonValueKind.String: builder.Append(Stringify(value.GetString()!)); break;
            case JsonValueKind.Number: builder.Append(Number(value.GetDouble())); break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            default: builder.Append("null"); break;
        }
    }

    /// <summary>Number.prototype.toString for finite doubles (JSON has no others).</summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) return "null";
        if (value == 0) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('E')) return text;
        // Shortest round-trip digits with JavaScript's exponent rules (1e21 and above, below 1e-6).
        var mantissa = text[..text.IndexOf('E')]; var exponent = int.Parse(text[(text.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
        var negative = mantissa.StartsWith('-'); if (negative) mantissa = mantissa[1..];
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var point = exponent + 1;
        string result;
        if (point is > 0 and <= 21) result = point >= digits.Length ? digits + new string('0', point - digits.Length) : digits[..point] + "." + digits[point..];
        else if (point is <= 0 and > -6) result = "0." + new string('0', -point) + digits;
        else result = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (point - 1 >= 0 ? "+" : "-") + Math.Abs(point - 1).ToString(CultureInfo.InvariantCulture);
        return negative ? "-" + result : result;
    }

    /// <summary>String.prototype.trim: JavaScript white space and line terminators.</summary>
    public static string Trim(string value)
    {
        static bool Space(char c) => c is '﻿' || c != '\u0085' && char.IsWhiteSpace(c);
        int start = 0, end = value.Length;
        while (start < end && Space(value[start])) start++;
        while (end > start && Space(value[end - 1])) end--;
        return value[start..end];
    }

    /// <summary>JSON text of a value serialized by System.Text.Json with relaxed escaping (for host-built objects).</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Relaxed);
}
