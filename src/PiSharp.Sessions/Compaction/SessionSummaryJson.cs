using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PiSharp.Sessions.Compaction;

/// <summary>JSON.stringify's data representation for source summary text and token estimates.
/// This never replaces or rewrites the original stored JSON or grants executable tool authority.</summary>
internal static class SessionSummaryJson
{
    internal static string Stringify(JsonElement value)
    {
        var result = new StringBuilder(); Write(value, result, 0); return result.ToString();
    }
    internal static string String(string value)
    {
        var result = new StringBuilder(); Quoted(value, result); return result.ToString();
    }
    internal static IEnumerable<JsonProperty> Properties(JsonElement value) => value.EnumerateObject().OrderBy(property => Index(property.Name));
    private static void Write(JsonElement value, StringBuilder result, int depth)
    {
        if (depth > 128) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: result.Append("null"); break;
            case JsonValueKind.True: result.Append("true"); break;
            case JsonValueKind.False: result.Append("false"); break;
            case JsonValueKind.String: Quoted(value.GetString()!, result); break;
            case JsonValueKind.Number: result.Append(Number(value.GetDouble())); break;
            case JsonValueKind.Array:
                result.Append('['); var first = true;
                foreach (var item in value.EnumerateArray()) { if (!first) result.Append(','); first = false; Write(item, result, depth + 1); }
                result.Append(']'); break;
            case JsonValueKind.Object:
                result.Append('{'); first = true;
                foreach (var item in Properties(value))
                { if (!first) result.Append(','); first = false; Quoted(item.Name, result); result.Append(':'); Write(item.Value, result, depth + 1); }
                result.Append('}'); break;
            default: throw new SessionCompactionException(SessionCompactionFailure.InvalidMessage);
        }
        if (result.Length > 8_388_608) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
    }
    private static long Index(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value != uint.MaxValue && value.ToString(CultureInfo.InvariantCulture) == key ? value : long.MaxValue;
    private static void Quoted(string value, StringBuilder result)
    {
        result.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': result.Append("\\\""); break; case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break; case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break; case '\r': result.Append("\\r"); break; case '\t': result.Append("\\t"); break;
                default:
                    if (c < 0x20 || char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) &&
                        !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1])))
                        result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else result.Append(c); break;
            }
        }
        result.Append('"');
    }
    private static string Number(double value)
    {
        if (!double.IsFinite(value)) return "null";
        if (value == 0) return "0";
        var negative = value < 0; var raw = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var split = raw.Split('E', 'e'); var exponent = split.Length == 2 ? int.Parse(split[1], CultureInfo.InvariantCulture) : 0;
        var point = split[0].IndexOf('.'); if (point < 0) point = split[0].Length;
        var digits = split[0].Replace(".", "", StringComparison.Ordinal); var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0') leading++;
        var power = exponent + point - leading - 1; digits = digits[leading..].TrimEnd('0');
        string number;
        if (power >= 21 || power < -6)
            number = digits[..1] + (digits.Length == 1 ? "" : "." + digits[1..]) + "e" + (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
        else
        {
            var position = power + 1;
            number = position <= 0 ? "0." + new string('0', -position) + digits :
                position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, ".");
        }
        return negative ? "-" + number : number;
    }
}
