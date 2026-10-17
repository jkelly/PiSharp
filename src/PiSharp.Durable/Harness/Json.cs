// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/json.ts. Upstream assigns into a Proxy draft through
// one indexer for objects and arrays; here the explicit drafts have one overload each. JavaScript's `current !== value` compares
// primitives by value and containers by identity, as below.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>Leaf-by-leaf JSON assignment into drafts (upstream <c>json.ts</c>).</summary>
internal static class HarnessJson
{
    /// <summary>
    /// Assigns <paramref name="value"/> at <c>target[key]</c> leaf by leaf (upstream <c>assignJson</c>). Chord records a container
    /// assignment as one full set and only emits an append when a string leaf is reassigned with a longer string, so writing the partial
    /// whole would store and publish the complete message on every flush.
    /// </summary>
    public static void AssignJson(DraftObject target, string key, JsonValue value)
    {
        var current = target.ContainsKey(key) ? target.Get(key) : null;
        if (current is JsonObjectValue && value is JsonObjectValue record)
        {
            AssignObject(target.GetObject(key), record);
            return;
        }
        if (current is JsonArrayValue items && value is JsonArrayValue list && items.Count <= list.Count)
        {
            AssignArray(target.GetArray(key), list);
            return;
        }
        if (current is null || !Same(current, value)) target.Set(key, value);
    }

    /// <summary><see cref="AssignJson(DraftObject, string, JsonValue)"/> for an array slot.</summary>
    public static void AssignJson(DraftArray target, int index, JsonValue value)
    {
        var current = target.Get(index);
        if (current is JsonObjectValue && value is JsonObjectValue record)
        {
            AssignObject(target.GetObject(index), record);
            return;
        }
        if (current is JsonArrayValue items && value is JsonArrayValue list && items.Count <= list.Count)
        {
            AssignArray(target.GetArray(index), list);
            return;
        }
        if (!Same(current, value)) target.Set(index, value);
    }

    private static void AssignObject(DraftObject current, JsonObjectValue value)
    {
        foreach (var name in current.Keys.ToList()) if (!value.ContainsKey(name)) current.Delete(name);
        foreach (var (name, child) in value) AssignJson(current, name, child);
    }

    private static void AssignArray(DraftArray current, JsonArrayValue value)
    {
        for (var index = 0; index < value.Count; index++)
        {
            if (index < current.Count) AssignJson(current, index, value[index]);
            else current.Add(value[index]);
        }
    }

    // `current !== value`: primitives by value, containers by identity.
    private static bool Same(JsonValue current, JsonValue value) =>
        ReferenceEquals(current, value) || (current is not (JsonObjectValue or JsonArrayValue) && JsonValue.DeepEquals(current, value));
}
