using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Cli.Extensions;

/// <summary>A bounded string-object validation profile. It does not convert or clean arguments.</summary>
internal sealed record NativeStringObjectSchema(bool Unconstrained, bool AllowAdditional,
    ImmutableHashSet<string> Properties, ImmutableHashSet<string> Required)
{
    internal static NativeStringObjectSchema Read(JsonData schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        try { return ReadCore(schema); }
        catch (InvalidOperationException) { throw Unsupported(); }
        catch (JsonException) { throw Unsupported(); }
    }

    private static NativeStringObjectSchema ReadCore(JsonData schema)
    {
        var value = schema.Value;
        if (value.ValueKind != JsonValueKind.Object || schema.ToString().Length > 16_384) throw Unsupported();
        if (!value.EnumerateObject().Any())
            return new(true, true, ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);
        if (value.EnumerateObject().Any(property => property.Name is not
                ("type" or "properties" or "required" or "additionalProperties" or "description")) ||
            !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object" ||
            !Annotation(value) || !value.TryGetProperty("properties", out var fields) || fields.ValueKind != JsonValueKind.Object ||
            fields.EnumerateObject().Count() > 32) throw Unsupported();
        var allowAdditional = true;
        if (value.TryGetProperty("additionalProperties", out var additional))
        {
            if (additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Unsupported();
            allowAdditional = additional.GetBoolean();
        }
        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var property in fields.EnumerateObject())
        {
            if (property.Name.Length is < 1 or > 128 || property.Name.Any(char.IsControl) || !Scalar(property.Name) ||
                property.Value.ValueKind != JsonValueKind.Object ||
                property.Value.EnumerateObject().Any(field => field.Name is not ("type" or "description")) ||
                !Annotation(property.Value) || !property.Value.TryGetProperty("type", out var childType) ||
                childType.ValueKind != JsonValueKind.String || childType.GetString() != "string" || !names.Add(property.Name))
                throw Unsupported();
        }
        var mandatory = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (value.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > 32) throw Unsupported();
            foreach (var field in required.EnumerateArray())
                if (field.ValueKind != JsonValueKind.String || !names.Contains(field.GetString()!) ||
                    !mandatory.Add(field.GetString()!)) throw Unsupported();
        }
        return new(false, allowAdditional, names.ToImmutable(), mandatory.ToImmutable());
    }

    internal bool Validate(JsonData arguments)
    {
        // ToolInvoker retains strict JSON/Unicode/finite/depth/budget admission before this validator.
        ArgumentNullException.ThrowIfNull(arguments);
        var value = arguments.Value;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (Unconstrained) return true;
        try
        {
            foreach (var property in value.EnumerateObject())
            {
                if (!Properties.Contains(property.Name)) { if (!AllowAdditional) return false; continue; }
                if (property.Value.ValueKind != JsonValueKind.String || !Scalar(property.Value.GetString()!)) return false;
            }
            return Required.All(name => value.TryGetProperty(name, out _));
        }
        catch (InvalidOperationException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool Annotation(JsonElement value) => !value.TryGetProperty("description", out var description) ||
        description.ValueKind == JsonValueKind.String && description.GetString() is { Length: <= 4096 } text && Scalar(text);

    private static bool Scalar(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsHighSurrogate(text[index])) { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        return true;
    }

    private static NativeExtensionException Unsupported() => new(NativeExtensionFailure.UnsupportedSchema);
}
