// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/params.ts
//
// Params declarations: the policy for params fields and the closed object schema checked on every get. A params field is declared as a
// Property, so an opt(...) field reaches the declaration-time refusal that upstream's RequiredFields type also gives; ParamSchema and
// RequiredFields are TypeScript types.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

/// <summary>Declared params: field names in identity order, the closed object schema of all fields, and <c>[field, normal form]</c> pairs
/// in identity order.</summary>
internal sealed record ParamsData(IReadOnlyList<string> Fields, JsonObject Schema, JsonArray Normal);

internal static partial class ContractsInternal
{
    /// <summary>Reads declared params fields: each a required <c>Str()</c>, <c>Int()</c>, <c>Bool()</c> or <c>OneOf(...)</c> of strings,
    /// constraints allowed.</summary>
    internal static ParamsData ReadParams(IEnumerable<KeyValuePair<string, Property>> declared, string path)
    {
        var fields = declared.Select(entry =>
        {
            var fieldPath = $"{path}.{entry.Key}";
            var refused = new ArgumentException($"{fieldPath}: a param is a required str(), int(), bool() or oneOf(...) of strings");
            if (IsOptional(entry.Value.Value)) throw refused;
            var snapshot = SnapshotSchema(entry.Value.Value, fieldPath);
            if (!IsParamSchema(snapshot.Normal)) throw refused;
            return (Field: entry.Key, Snapshot: snapshot);
        }).ToList();
        var properties = new JsonObject();
        foreach (var (field, snapshot) in fields) properties[field] = snapshot.Schema;
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. fields.Select(entry => (JsonNode?)JsonValue.Create(entry.Field))]),
            ["additionalProperties"] = false,
        };
        return new ParamsData(
            [.. fields.Select(entry => entry.Field)],
            schema,
            new JsonArray([.. fields.Select(entry => (JsonNode?)new JsonArray(JsonValue.Create(entry.Field), entry.Snapshot.Normal))]));
    }

    /// <summary>Joins key parts with <c>|</c>, each escaped (<c>\\</c> and <c>|</c>), so two different part lists never join to one key.</summary>
    internal static string JoinKey(IEnumerable<string> parts) =>
        string.Join("|", parts.Select(part => part.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)));

    private static bool IsParamSchema(JsonNode normal)
    {
        if (normal is not JsonObject node) return false;
        if (node.TryGetPropertyValue("anyOf", out var anyOf))
            return node.Count == 1 && anyOf is JsonArray branches && branches.All(IsStringLiteral);
        return ChordJson.AsString(node["type"]) is { } type && ParamKeywords.TryGetValue(type, out var allowed) &&
            node.All(entry => allowed.Contains(entry.Key));
    }

    /// <summary>Keywords a params field may use, by type.</summary>
    private static readonly Dictionary<string, HashSet<string>> ParamKeywords = new(StringComparer.Ordinal)
    {
        ["string"] = new(["type", "minLength", "maxLength", "pattern", "format"], StringComparer.Ordinal),
        ["integer"] = new(["type", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"], StringComparer.Ordinal),
        ["boolean"] = new(["type"], StringComparer.Ordinal),
    };

    private static bool IsStringLiteral(JsonNode? branch) =>
        branch is JsonObject node && ChordJson.AsString(node["type"]) == "string" && ChordJson.AsString(node["const"]) is not null && node.Count == 2;
}
