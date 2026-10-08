using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

internal static class WholeValueAssertions
{
    internal static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    internal static void Equal(JsonElement expected, JsonElement actual, string path = "")
    {
        Require(expected.ValueKind == actual.ValueKind, $"{path}: JSON kind {actual.ValueKind}, expected {expected.ValueKind}.");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var e = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var a = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                Require(e.Keys.Order(StringComparer.Ordinal).SequenceEqual(a.Keys.Order(StringComparer.Ordinal)), path + ": complete property set differs.");
                foreach (var field in e) Equal(field.Value, a[field.Key], path + "/" + field.Key);
                break;
            case JsonValueKind.Array:
                Require(expected.GetArrayLength() == actual.GetArrayLength(), path + ": array length differs.");
                for (var index = 0; index < expected.GetArrayLength(); index++) Equal(expected[index], actual[index], path + "/" + index);
                break;
            case JsonValueKind.Number:
                Require(BitConverter.DoubleToInt64Bits(expected.GetDouble()) == BitConverter.DoubleToInt64Bits(actual.GetDouble()), path + ": binary64 bits differ.");
                break;
            case JsonValueKind.String: Require(expected.GetString() == actual.GetString(), path + ": complete string differs."); break;
            default: Require(expected.GetRawText() == actual.GetRawText(), path + ": value differs."); break;
        }
    }
    internal static void Json(object expected, object actual, string label)
        => Equal(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual), label);
    internal static object[] Differences(JsonElement expected, JsonElement actual)
    {
        var differences = new List<object>();
        void Visit(JsonElement e, JsonElement a, string path)
        {
            if (e.ValueKind != a.ValueKind)
            { differences.Add(new { path, expected = e.Clone(), actual = a.Clone(), cause = "kind" }); return; }
            if (e.ValueKind == JsonValueKind.Object)
            {
                var left = e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var right = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
                    if (!left.ContainsKey(key) || !right.ContainsKey(key))
                        differences.Add(new { path = path + "/" + key, expectedPresent = left.ContainsKey(key), actualPresent = right.ContainsKey(key) });
                    else Visit(left[key], right[key], path + "/" + key);
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                if (e.GetArrayLength() != a.GetArrayLength()) differences.Add(new { path, expectedLength = e.GetArrayLength(), actualLength = a.GetArrayLength() });
                for (var i = 0; i < Math.Min(e.GetArrayLength(), a.GetArrayLength()); i++) Visit(e[i], a[i], path + "/" + i);
            }
            else
            {
                var equal = e.ValueKind == JsonValueKind.Number
                    ? BitConverter.DoubleToInt64Bits(e.GetDouble()) == BitConverter.DoubleToInt64Bits(a.GetDouble())
                    : e.ValueKind == JsonValueKind.String ? e.GetString() == a.GetString() : e.GetRawText() == a.GetRawText();
                if (!equal) differences.Add(new { path, expected = e.Clone(), actual = a.Clone(), expectedBits = e.ValueKind == JsonValueKind.Number ? BitConverter.DoubleToInt64Bits(e.GetDouble()).ToString("x16") : null,
                    actualBits = a.ValueKind == JsonValueKind.Number ? BitConverter.DoubleToInt64Bits(a.GetDouble()).ToString("x16") : null });
            }
        }
        Visit(expected, actual, ""); return differences.ToArray();
    }
    internal static string EcmaBytes(JsonData value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(EcmaScriptJsonProjection.Project(value)));
}
