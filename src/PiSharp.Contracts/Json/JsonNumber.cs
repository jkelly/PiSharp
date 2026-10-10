using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Contracts;

/// <summary>JavaScript numbers in JSON: <c>JSON.stringify</c> of a binary64 and the binary64 <c>JSON.parse</c> reads.</summary>
public static class JsonNumber
{
    /// <summary>JSON.stringify of a binary64: Number::toString for finite values, <c>null</c> otherwise.</summary>
    public static string Text(double value)
    {
        if (!double.IsFinite(value)) return "null";
        if (value == 0) return "0";
        // .NET Core 3.0+ "R" is the shortest round-tripping digit string, the digits Number::toString chooses.
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var mantissa = shortest;
        var marker = shortest.IndexOfAny(['E', 'e']);
        if (marker >= 0)
        {
            exponent = int.Parse(shortest.AsSpan(marker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = shortest[..marker];
        }
        var point = mantissa.IndexOf('.');
        var digits = point >= 0 ? mantissa.Remove(point, 1) : mantissa;
        var n = (point >= 0 ? point : mantissa.Length) + exponent;
        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0') leading++;
        digits = digits[leading..].TrimEnd('0');
        n -= leading;
        var k = digits.Length;
        string text;
        if (k <= n && n <= 21) text = digits + new string('0', n - k);
        else if (0 < n && n <= 21) text = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) text = "0." + new string('0', -n) + digits;
        else
        {
            var e = n - 1;
            text = digits[..1] + (k == 1 ? "" : "." + digits[1..]) + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }
        return value < 0 ? "-" + text : text;
    }

    /// <summary>A node holding <see cref="Text"/> of the value (a null node for a non-finite value, as JSON.stringify writes it).</summary>
    public static JsonNode? Node(double value) => double.IsFinite(value) ? JsonNode.Parse(Text(value)) : null;

    /// <summary>Writes <see cref="Text"/> of the value.</summary>
    public static void Write(Utf8JsonWriter writer, string name, double value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(name);
        writer.WriteRawValue(Text(value), skipInputValidation: true);
    }

    /// <summary>The binary64 of a JSON number, as JSON.parse reads it (beyond binary64's range: an infinity).</summary>
    public static double Read(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? double.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw new JsonException("Expected a JSON number.");
}
