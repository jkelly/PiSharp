using System.Collections.Immutable;
using System.Text.Json;

namespace PiSharp.Contracts;

/// <summary>An owned, immutable JSON value. Cloning severs JsonDocument lifetime and mutable input ownership.</summary>
public sealed class JsonData
{
    /// <summary>
    /// The deepest nesting an owned value holds (an object or array is one level). JSON.parse has no such limit, but Pi's own writes
    /// recurse: V8's JSON.stringify gives up at about 1,700 nested objects on Node's default stack, so Pi cannot store or send a deeper
    /// value. PiSharp's readers, validators and writers recurse too; 1,000 levels (System.Text.Json's own writer default) keeps every
    /// such walk safely inside a thread's stack.
    /// </summary>
    public const int MaximumDepth = 1000;

    /// <summary>Document options that admit <see cref="MaximumDepth"/> levels (System.Text.Json's default is 64).</summary>
    public static JsonDocumentOptions DocumentOptions => new() { MaxDepth = MaximumDepth };

    public JsonElement Value { get; }

    private JsonData(JsonElement value)
    {
        Validate(value);
        Value = value.Clone();
    }

    public static JsonData Parse(string json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        return new JsonData(document.RootElement);
    }

    public static JsonData FromElement(JsonElement value) => new(value);
    public static JsonData EmptyObject { get; } = Parse("{}");
    public static JsonData Null { get; } = Parse("null");
    public override string ToString() => Value.GetRawText();

    // Iterative: a value of MaximumDepth levels needs no recursion here.
    internal static void Validate(JsonElement value)
    {
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((value, 0));
        while (pending.Count != 0)
        {
            var (current, depth) = pending.Pop();
            if (current.ValueKind == JsonValueKind.Undefined)
                throw new JsonException("Undefined is not a JSON value.");
            if (current.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
            if (depth >= MaximumDepth) throw new JsonException("Maximum JSON depth exceeded.");
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in current.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException($"Duplicate JSON property: {property.Name}");
                    pending.Push((property.Value, depth + 1));
                }
            }
            else foreach (var item in current.EnumerateArray()) pending.Push((item, depth + 1));
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
