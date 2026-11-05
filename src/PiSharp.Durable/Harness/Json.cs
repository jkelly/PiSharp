// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/json.ts. Upstream assigns into a Proxy draft through
// one indexer for objects and arrays; here the explicit drafts have one overload each. JavaScript's `current !== value` compares
// primitives by value and containers by identity, as below. Pi v1.5.0 sync (area partial-commits): AssignPlan (da412b039) appends a
// string member that grew by the planned text with DraftObject.AppendText, checked by length; `keep` leaves a value to the caller. The
// `__proto__` guard (89c921d8b) has no C# counterpart: a draft is a keyed map with no prototype chain, and a member is read only when the
// draft has it, so a `__proto__` member is an ordinary one. 6c2fd73b0's generic signature is TypeScript typing.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>How <see cref="HarnessJson.AssignJson(DraftObject, string, JsonValue, AssignPlan?)"/> writes one value instead of assigning it
/// leaf by leaf (upstream <c>AssignPlan</c>).</summary>
internal abstract record AssignPlan
{
    private AssignPlan() { }

    /// <summary>Leave the value as it is; the caller writes it (upstream <c>{kind: "keep"}</c>).</summary>
    public static AssignPlan Keep { get; } = new KeepPlan();

    /// <summary>The string grew by <paramref name="Text"/> since it was last assigned: append it, when the lengths agree (upstream
    /// <c>{kind: "append", text}</c>).</summary>
    public sealed record Append(string Text) : AssignPlan;

    /// <summary>Plans for some members (<paramref name="Members"/>) or items (<paramref name="Items"/>) of a container (upstream
    /// <c>{kind: "fields", fields}</c>, whose map keys are member names and item indexes).</summary>
    public sealed record Fields(IReadOnlyDictionary<string, AssignPlan>? Members = null, IReadOnlyDictionary<int, AssignPlan>? Items = null)
        : AssignPlan;

    private sealed record KeepPlan : AssignPlan;
}

/// <summary>Leaf-by-leaf JSON assignment into drafts (upstream <c>json.ts</c>).</summary>
internal static class HarnessJson
{
    /// <summary>
    /// Assigns <paramref name="value"/> at <c>target[key]</c> leaf by leaf (upstream <c>assignJson</c>). Chord records a container
    /// assignment as one full set and only emits an append when a string leaf is reassigned with a longer string, so writing the partial
    /// whole would store and publish the complete message on every flush. That append costs a compare of the whole string, though; where
    /// <paramref name="plan"/> says what a string member grew by, and the committed string plus that text has the new string's length, the
    /// text is appended with <see cref="DraftObject.AppendText"/> instead.
    /// </summary>
    public static void AssignJson(DraftObject target, string key, JsonValue value, AssignPlan? plan = null)
    {
        if (ReferenceEquals(plan, AssignPlan.Keep)) return;
        // Only a member the draft has is read: upstream's `__proto__` guard (a missing `__proto__` read through to Object.prototype).
        var current = target.ContainsKey(key) ? target.Get(key) : null;
        var fields = plan as AssignPlan.Fields;
        if (current is JsonObjectValue && value is JsonObjectValue record)
        {
            AssignObject(target.GetObject(key), record, fields);
            return;
        }
        if (current is JsonArrayValue items && value is JsonArrayValue list && items.Count <= list.Count)
        {
            AssignArray(target.GetArray(key), list, fields);
            return;
        }
        if (plan is AssignPlan.Append append && current is JsonStringValue committed && value is JsonStringValue next &&
            committed.Length + append.Text.Length == next.Length)
        {
            target.AppendText(key, append.Text);
            return;
        }
        if (current is null || !Same(current, value)) target.Set(key, value);
    }

    /// <summary><see cref="AssignJson(DraftObject, string, JsonValue, AssignPlan?)"/> for an array slot; an append plan does not apply
    /// to an item, as upstream's <c>appendText()</c> takes object members.</summary>
    public static void AssignJson(DraftArray target, int index, JsonValue value, AssignPlan? plan = null)
    {
        if (ReferenceEquals(plan, AssignPlan.Keep)) return;
        var current = target.Get(index);
        var fields = plan as AssignPlan.Fields;
        if (current is JsonObjectValue && value is JsonObjectValue record)
        {
            AssignObject(target.GetObject(index), record, fields);
            return;
        }
        if (current is JsonArrayValue items && value is JsonArrayValue list && items.Count <= list.Count)
        {
            AssignArray(target.GetArray(index), list, fields);
            return;
        }
        if (!Same(current, value)) target.Set(index, value);
    }

    private static void AssignObject(DraftObject current, JsonObjectValue value, AssignPlan.Fields? fields)
    {
        foreach (var name in current.Keys.ToList()) if (!value.ContainsKey(name)) current.Delete(name);
        foreach (var (name, child) in value)
            AssignJson(current, name, child, fields?.Members is { } members && members.TryGetValue(name, out var plan) ? plan : null);
    }

    private static void AssignArray(DraftArray current, JsonArrayValue value, AssignPlan.Fields? fields)
    {
        for (var index = 0; index < value.Count; index++)
        {
            if (index < current.Count)
                AssignJson(current, index, value[index], fields?.Items is { } items && items.TryGetValue(index, out var plan) ? plan : null);
            else current.Add(value[index]);
        }
    }

    // `current !== value`: primitives by value, containers by identity.
    private static bool Same(JsonValue current, JsonValue value) =>
        ReferenceEquals(current, value) || (current is not (JsonObjectValue or JsonArrayValue) && JsonValue.DeepEquals(current, value));
}
