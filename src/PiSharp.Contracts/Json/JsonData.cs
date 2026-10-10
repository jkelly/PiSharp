using System.Collections.Immutable;
using System.Text.Json;

namespace PiSharp.Contracts;

/// <summary>An owned, immutable JSON value. Cloning severs JsonDocument lifetime and mutable input ownership.</summary>
public sealed class JsonData
{
    public JsonElement Value { get; }

    private JsonData(JsonElement value)
    {
        Validate(value);
        Value = value.Clone();
    }

    public static JsonData Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        return new JsonData(document.RootElement);
    }

    public static JsonData FromElement(JsonElement value) => new(value);
    public static JsonData EmptyObject { get; } = Parse("{}");
    public static JsonData Null { get; } = Parse("null");
    public override string ToString() => Value.GetRawText();

    internal static void Validate(JsonElement value, int depth = 0)
    {
        if (depth > 64) throw new JsonException("Maximum JSON depth exceeded.");
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new JsonException("Undefined is not a JSON value.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException($"Duplicate JSON property: {property.Name}");
                Validate(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Validate(item, depth + 1);
        }
    }
}

/// <summary>Preserves optional and unknown fields, including explicit null, without exposing mutable dictionaries.</summary>
public sealed class JsonFields
{
    public ImmutableDictionary<string, JsonData> Values { get; }
    private readonly ImmutableList<string> _order;
    public static JsonFields Empty { get; } = new(ImmutableDictionary<string, JsonData>.Empty, []);

    private JsonFields(ImmutableDictionary<string, JsonData> values, ImmutableList<string> order) { Values = values; _order = order; }
    /// <summary>Sets a field. A new name goes last; an existing one keeps its position (JavaScript property order).</summary>
    public JsonFields Set(string name, JsonData value) =>
        new(Values.SetItem(name, value), Values.ContainsKey(name) ? _order : _order.Add(name));
    public JsonFields Remove(string name) => Values.ContainsKey(name) ? new(Values.Remove(name), _order.Remove(name)) : this;
    public bool TryGet(string name, out JsonData? value) => Values.TryGetValue(name, out value);
    /// <summary>The fields in the order they were first set or read (JavaScript object key order).</summary>
    public IEnumerable<KeyValuePair<string, JsonData>> Ordered => _order.Select(name => new KeyValuePair<string, JsonData>(name, Values[name]));

    public static JsonFields FromObjectExcept(JsonElement value, params string[] knownNames)
    {
        JsonData.Validate(value);
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");
        var known = knownNames.ToHashSet(StringComparer.Ordinal);
        var builder = ImmutableDictionary.CreateBuilder<string, JsonData>(StringComparer.Ordinal);
        var order = ImmutableList.CreateBuilder<string>();
        foreach (var property in value.EnumerateObject())
            if (!known.Contains(property.Name)) { builder.Add(property.Name, JsonData.FromElement(property.Value)); order.Add(property.Name); }
        return new(builder.ToImmutable(), order.ToImmutable());
    }
}
