using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Cli.Extensions;

/// <summary>Bounded flat primitive schemas for published tools; unsupported validation is rejected.</summary>
internal sealed class NativeToolObjectSchema
{
    private sealed record Field(string Type, ImmutableArray<string> AllowedStrings);
    private readonly NativeStringObjectSchema? legacy;
    private readonly ImmutableDictionary<string, Field> fields;
    private readonly ImmutableHashSet<string> required;
    private readonly bool allowAdditional;

    private NativeToolObjectSchema(NativeStringObjectSchema legacy)
    { this.legacy = legacy; fields = ImmutableDictionary<string, Field>.Empty; required = ImmutableHashSet<string>.Empty; }
    private NativeToolObjectSchema(ImmutableDictionary<string, Field> fields, ImmutableHashSet<string> required, bool allowAdditional)
    { this.fields = fields; this.required = required; this.allowAdditional = allowAdditional; }

    internal static NativeToolObjectSchema Read(JsonData schema)
    {
        // Preserve the original profile and its exact existing argument behavior.
        try { return new(NativeStringObjectSchema.Read(schema)); }
        catch (NativeExtensionException error) when (error.Failure == NativeExtensionFailure.UnsupportedSchema) { }
        try
        {
            var value = schema.Value;
            if (schema.ToString().Length > 16_384 || !Shape(value, ["type", "properties", "required", "additionalProperties", "description"]) ||
                !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object" ||
                !Annotation(value) || !value.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                properties.EnumerateObject().Count() > 32) throw Unsupported();
            var fields = ImmutableDictionary.CreateBuilder<string, Field>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name.Length is < 1 or > 128 || property.Name.Any(char.IsControl) || !Scalar(property.Name) ||
                    !Shape(property.Value, ["type", "description", "enum"]) || !Annotation(property.Value) ||
                    !property.Value.TryGetProperty("type", out var fieldType) || fieldType.ValueKind != JsonValueKind.String ||
                    fieldType.GetString() is not ("string" or "number" or "boolean")) throw Unsupported();
                var allowed = ImmutableArray<string>.Empty;
                if (property.Value.TryGetProperty("enum", out var enumeration))
                {
                    if (fieldType.GetString() != "string" || enumeration.ValueKind != JsonValueKind.Array ||
                        enumeration.GetArrayLength() is < 1 or > 64) throw Unsupported();
                    var strings = ImmutableArray.CreateBuilder<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in enumeration.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: <= 512 } text ||
                            !Scalar(text) || !unique.Add(text)) throw Unsupported();
                        strings.Add(text);
                    }
                    allowed = strings.ToImmutable();
                }
                if (!fields.TryAdd(property.Name, new(fieldType.GetString()!, allowed))) throw Unsupported();
            }
            var required = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            if (value.TryGetProperty("required", out var mandatory))
            {
                if (mandatory.ValueKind != JsonValueKind.Array || mandatory.GetArrayLength() > 32) throw Unsupported();
                foreach (var item in mandatory.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || !fields.ContainsKey(item.GetString()!) || !required.Add(item.GetString()!)) throw Unsupported();
            }
            var additional = true;
            if (value.TryGetProperty("additionalProperties", out var extra))
            {
                if (extra.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Unsupported();
                additional = extra.GetBoolean();
            }
            return new(fields.ToImmutable(), required.ToImmutable(), additional);
        }
        catch (InvalidOperationException) { throw Unsupported(); }
        catch (JsonException) { throw Unsupported(); }
    }

    internal bool Validate(JsonData arguments)
    {
        if (legacy is not null) return legacy.Validate(arguments);
        try
        {
            var value = arguments.Value;
            if (value.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;
                if (!fields.TryGetValue(property.Name, out var field)) { if (!allowAdditional) return false; continue; }
                if (field.Type == "string")
                {
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } text || !Scalar(text) ||
                        !field.AllowedStrings.IsEmpty && !field.AllowedStrings.Contains(text, StringComparer.Ordinal)) return false;
                }
                else if (field.Type == "number")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
                }
                else if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            }
            return required.All(seen.Contains);
        }
        catch (InvalidOperationException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool Shape(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
    }
    private static bool Annotation(JsonElement value) => !value.TryGetProperty("description", out var annotation) ||
        annotation.ValueKind == JsonValueKind.String && annotation.GetString() is { Length: <= 4096 } text && Scalar(text);
    private static bool Scalar(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsHighSurrogate(text[index])) { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        return true;
    }
    private static NativeExtensionException Unsupported() => new(NativeExtensionFailure.UnsupportedSchema);
}
