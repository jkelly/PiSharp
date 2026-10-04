using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>Pi v0.99.1 combineUsage projection. Opaque singleton usage stays unchanged.</summary>
internal static class NestedToolUsage
{
    internal static JsonData? Combine(JsonData? first, JsonData? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        if (first.Value.ValueKind != JsonValueKind.Object || second.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Nested usage aggregation requires source-shaped usage objects.");
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" })
                Sum(writer, name, first.Value, second.Value);
            foreach (var name in new[] { "cacheWrite1h", "reasoning" })
                if (first.Value.TryGetProperty(name, out _) || second.Value.TryGetProperty(name, out _))
                    Sum(writer, name, first.Value, second.Value, optional: true);
            writer.WritePropertyName("cost"); writer.WriteStartObject();
            var a = first.Value.TryGetProperty("cost", out var costA) ? costA : default;
            var b = second.Value.TryGetProperty("cost", out var costB) ? costB : default;
            foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
                Sum(writer, name, a, b);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return JsonData.Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
    }

    private static void Sum(Utf8JsonWriter writer, string name, JsonElement a, JsonElement b, bool optional = false)
    {
        var sum = Number(a, name, optional) + Number(b, name, optional);
        // Invalid/overflowing opaque usage cannot introduce non-finite JSON into the transcript.
        if (!double.IsFinite(sum)) throw new InvalidOperationException("Nested tool usage exceeds finite numeric bounds.");
        writer.WriteNumber(name, sum);
    }
    private static double Number(JsonElement value, string name, bool optional)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty(name, out var number))
            {
                if (optional) return 0;
            }
            else if (number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var result) && double.IsFinite(result))
                return result;
        }
        throw new InvalidOperationException("Nested usage aggregation requires finite source-shaped numeric fields.");
    }
}
