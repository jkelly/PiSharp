using System.Text;
using System.Text.Json;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.Contracts;

/// <summary>Local JSON Schema references for metadata readers. Never fetches remote resources.</summary>
public static class LocalSchemaReferences
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Reads an object's own member, returning false for other values.</summary>
    public delegate bool PropertyLookup<T>(T value, string name, out T property);

    /// <summary>Resolves a fragment pointer against the supplied root. Invalid escapes, absent members,
    /// non-schema targets, and nonlocal references resolve to nothing. Empty pointer segments are retained.</summary>
    public static bool TryResolve<T>(string reference, T root, PropertyLookup<T> lookup, Func<T, bool> isSchema, out T target)
    {
        target = root;
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal)) return false;
        if (reference != "#")
        {
            foreach (var segment in reference[2..].Split('/'))
            {
                // Uri.UnescapeDataString tolerates malformed escapes and UTF-8. Schema metadata must not
                // throw or accidentally resolve such an input to a different property.
                var bytes = new List<byte>();
                var decoded = new StringBuilder();
                try
                {
                    for (var i = 0; i < segment.Length;)
                    {
                        if (segment[i] != '%') { decoded.Append(segment[i++]); continue; }
                        bytes.Clear();
                        while (i < segment.Length && segment[i] == '%')
                        {
                            if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2]) ||
                                !byte.TryParse(segment.AsSpan(i + 1, 2),
                                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var b)) return false;
                            bytes.Add(b); i += 3;
                        }
                        decoded.Append(StrictUtf8.GetString(bytes.ToArray()));
                    }
                }
                catch (DecoderFallbackException) { return false; }
                var key = decoded.ToString().Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (!lookup(target, key, out var next)) return false;
                target = next;
            }
        }
        return isSchema(target);
    }

    private static bool Property(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property);
    }

    /// <summary>Resolves an object or boolean subschema within <paramref name="root"/>.</summary>
    public static bool TryResolve(string reference, JsonElement root, out JsonElement target) =>
        TryResolve(reference, root, Property, value => value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False, out target);

    /// <summary>Follows a local reference chain, stopping at missing, malformed, remote or cyclic references.</summary>
    public static JsonElement Follow(JsonElement schema, JsonElement root)
    {
        var seen = new HashSet<JsonElement>();
        while (seen.Add(schema) && schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String &&
            TryResolve(reference.GetString()!, root, out var next)) schema = next;
        return schema;
    }

    /// <summary>Adds reachable descriptions and property names in source order; unused definitions are excluded.</summary>
    public static void AppendSearchText(JsonElement root, ICollection<string> parts)
    {
        // Only reference expansions are deduplicated: an inline occurrence and its first
        // reference both contribute to term frequency, matching upstream's ranker input.
        var seen = new HashSet<JsonElement> { root };
        void Visit(JsonElement schema, int depth, bool referenced = false)
        {
            if (depth > 64 || schema.ValueKind != JsonValueKind.Object) return;
            if (referenced && !seen.Add(schema)) return;
            if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String &&
                TryResolve(reference.GetString()!, root, out var target)) Visit(target, depth + 1, referenced: true);
            if (schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String) parts.Add(description.GetString()!);
            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                foreach (var property in ModelOperationJson.ObjectEntries(properties)) { parts.Add(property.Key); Visit(property.Value, depth + 1); }
            if (schema.TryGetProperty("items", out var items)) Visit(items, depth + 1);
            foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
                if (schema.TryGetProperty(key, out var variants) && variants.ValueKind == JsonValueKind.Array)
                    foreach (var variant in variants.EnumerateArray()) Visit(variant, depth + 1);
        }
        Visit(root, 0);
    }
}
